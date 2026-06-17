using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Necroking.Core;

namespace Necroking.Data;

/// <summary>
/// Static defs for the skill book (Potions / Necromancy / Magic / Metamorphosis).
/// Loaded once from data/skills/&lt;tab&gt;.json. Layout (x,y per skill) is authored
/// in JSON in a logical 1280x680 tree-area pixel space, scaled to fit at draw time.
/// </summary>
public static class SkillBookDefs
{
    /// <summary>Canonical ordering hint for the built-in tabs. Tabs are discovered
    /// dynamically from data/skills/*.json — this list only fixes the left-to-right
    /// order of the known ones. Any extra tab file (e.g. lich.json) appends after
    /// these, sorted by its optional "order" field then id, so new tabs can be
    /// dropped in without code changes.</summary>
    public static readonly string[] TabIds = { "potions", "monstrology", "necromancy", "magic", "metamorphosis" };

    public static List<SkillTab> Tabs { get; private set; } = new();

    /// <summary>Find which tab contains the given skill id. Returns -1 if not found.</summary>
    public static int FindTabIndexFor(string skillId)
    {
        for (int i = 0; i < Tabs.Count; i++)
            if (Tabs[i].IndexOf(skillId) >= 0) return i;
        return -1;
    }

    public static void Load()
    {
        Tabs = new List<SkillTab>();

        // Discover every tab file in data/skills/ — no fixed set, so dropping in a
        // new <tab>.json adds a tab.
        string dir = GamePaths.Resolve("data/skills");
        var paths = new List<string>();
        try { if (Directory.Exists(dir)) paths.AddRange(Directory.GetFiles(dir, "*.json")); }
        catch (Exception ex) { DebugLog.Log("startup", $"SkillBook: listing {dir} failed: {ex.Message}"); }

        foreach (var path in paths)
        {
            string id = Path.GetFileNameWithoutExtension(path);
            try
            {
                Tabs.Add(LoadTab(id, path));
            }
            catch (Exception ex)
            {
                DebugLog.Log("startup", $"SkillBook: failed to load {path}: {ex.Message}");
            }
        }

        // Order: explicit "order" wins, else the canonical TabIds index for known
        // tabs, else after the known ones (alphabetical tiebreak).
        Tabs.Sort((a, b) =>
        {
            int ka = SortKey(a), kb = SortKey(b);
            return ka != kb ? ka.CompareTo(kb) : string.CompareOrdinal(a.Id, b.Id);
        });

        // Resolve parent ids -> indices and build children list per tab.
        foreach (var tab in Tabs) tab.ResolveLinks();
    }

    private static int SortKey(SkillTab t)
    {
        if (t.Order >= 0) return t.Order;
        int idx = Array.IndexOf(TabIds, t.Id);
        return idx >= 0 ? idx : 1000;
    }

    /// <summary>Write the current per-skill x,y back into data/skills/&lt;tab&gt;.json,
    /// preserving every other field (parsed as a JSON tree, only x/y touched). Used by
    /// the in-book layout editor. Returns false if any tab failed to save.</summary>
    public static bool SaveLayout()
    {
        bool ok = true;
        foreach (var tab in Tabs)
        {
            string path = GamePaths.Resolve($"data/skills/{tab.Id}.json");
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(path));
                var arr = root?["skills"]?.AsArray();
                if (arr == null) continue;
                var byId = new Dictionary<string, SkillDef>();
                foreach (var s in tab.Skills) byId[s.Id] = s;
                foreach (var sn in arr)
                {
                    var id = sn?["id"]?.GetValue<string>();
                    if (id != null && byId.TryGetValue(id, out var def))
                    {
                        sn!["x"] = def.X;
                        sn!["y"] = def.Y;
                    }
                }
                File.WriteAllText(path, root!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                DebugLog.Log("startup", $"SkillBook: saved layout -> {path}");
            }
            catch (Exception ex)
            {
                ok = false;
                DebugLog.Log("error", $"SkillBook SaveLayout {path}: {ex.Message}");
            }
        }
        return ok;
    }

    private static SkillTab LoadTab(string id, string path)
    {
        var json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var tab = new SkillTab
        {
            Id = id,
            DisplayName = root.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? id : id,
            Order = root.TryGetProperty("order", out var ord) && ord.ValueKind == JsonValueKind.Number ? ord.GetInt32() : -1,
            Skills = new List<SkillDef>(),
        };

        // Optional gate that hides the whole tab until a trigger is met. Absent =
        // always visible. e.g. { "type": "seen_item", "id": "potion_death_evolution" }.
        if (root.TryGetProperty("unlockRequirement", out var ur) && ur.ValueKind == JsonValueKind.Object)
        {
            tab.UnlockType   = Str(ur, "type", "");
            tab.UnlockId     = Str(ur, "id", "");
            tab.UnlockAmount = Int(ur, "amount", 0);
        }

        if (root.TryGetProperty("skills", out var skills) && skills.ValueKind == JsonValueKind.Array)
        {
            foreach (var sj in skills.EnumerateArray())
                tab.Skills.Add(ParseSkill(sj));
        }
        return tab;
    }

    private static SkillDef ParseSkill(JsonElement j)
    {
        var s = new SkillDef
        {
            Id          = Str(j, "id", ""),
            Name        = Str(j, "name", ""),
            Description = Str(j, "description", ""),
            X           = Int(j, "x", 0),
            Y           = Int(j, "y", 0),
            Effect      = Str(j, "effect", "noop"),
            EffectArg   = Str(j, "effectArg", ""),
            StartLearned = Bool(j, "startLearned", false),
            Parents     = new List<string>(),
            ParentsAny  = new List<string>(),
            ExclusiveOf = new List<string>(),
            Costs       = new List<SkillCost>(),
        };
        if (j.TryGetProperty("parents", out var pj) && pj.ValueKind == JsonValueKind.Array)
            foreach (var p in pj.EnumerateArray())
                if (p.ValueKind == JsonValueKind.String) s.Parents.Add(p.GetString()!);
        if (j.TryGetProperty("parentsAny", out var paj) && paj.ValueKind == JsonValueKind.Array)
            foreach (var p in paj.EnumerateArray())
                if (p.ValueKind == JsonValueKind.String) s.ParentsAny.Add(p.GetString()!);
        if (j.TryGetProperty("exclusiveOf", out var ej) && ej.ValueKind == JsonValueKind.Array)
            foreach (var e in ej.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String) s.ExclusiveOf.Add(e.GetString()!);
        if (j.TryGetProperty("costs", out var cj) && cj.ValueKind == JsonValueKind.Array)
            foreach (var c in cj.EnumerateArray())
                s.Costs.Add(new SkillCost
                {
                    Type   = Str(c, "type", "item"),
                    Id     = Str(c, "id", ""),
                    Amount = Int(c, "amount", 1),
                });
        return s;
    }

    private static string Str(JsonElement j, string k, string def)
        => j.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? def : def;
    private static int Int(JsonElement j, string k, int def)
        => j.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : def;
    private static bool Bool(JsonElement j, string k, bool def)
        => j.TryGetProperty(k, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? v.GetBoolean() : def;
}

public class SkillTab
{
    public string Id = "";
    public string DisplayName = "";
    /// <summary>Explicit left-to-right order from JSON ("order"); -1 = unset, fall
    /// back to the canonical TabIds order (then alphabetical). See SkillBookDefs.</summary>
    public int Order = -1;
    public List<SkillDef> Skills = new();
    private Dictionary<string, int> _idToIndex = new();

    /// <summary>Visibility gate for the whole tab. Empty UnlockType = always
    /// visible. Supported: "seen_item" (UnlockId = item id the player must have
    /// seen). UnlockAmount is reserved for future count/event-based gates.</summary>
    public string UnlockType = "";
    public string UnlockId = "";
    public int UnlockAmount;

    /// <summary>Bounds of node-center positions in JSON logical units.
    /// The panel uses these to scale the tree to fit the content rect.</summary>
    public int MinX, MinY, MaxX, MaxY;

    public int IndexOf(string id) => _idToIndex.TryGetValue(id, out var i) ? i : -1;

    public void ResolveLinks()
    {
        _idToIndex.Clear();
        for (int i = 0; i < Skills.Count; i++) _idToIndex[Skills[i].Id] = i;

        foreach (var s in Skills) s.ChildIds = new List<string>();
        foreach (var s in Skills)
            foreach (var p in s.Parents)
                if (_idToIndex.TryGetValue(p, out var pi))
                    Skills[pi].ChildIds.Add(s.Id);

        if (Skills.Count == 0) { MinX = MinY = 0; MaxX = MaxY = 0; return; }
        MinX = MaxX = Skills[0].X;
        MinY = MaxY = Skills[0].Y;
        for (int i = 1; i < Skills.Count; i++)
        {
            if (Skills[i].X < MinX) MinX = Skills[i].X; if (Skills[i].X > MaxX) MaxX = Skills[i].X;
            if (Skills[i].Y < MinY) MinY = Skills[i].Y; if (Skills[i].Y > MaxY) MaxY = Skills[i].Y;
        }
    }
}

public class SkillDef
{
    public string Id = "";
    public string Name = "";
    public string Description = "";
    public int X;
    public int Y;
    /// <summary>AND-prereq: every id in this list must be learned. Use for the
    /// typical "must have parent X before child Y" relationship.</summary>
    public List<string> Parents = new();
    /// <summary>OR-prereq: at least one id in this list must be learned. Use when
    /// a skill descends from a branch point (e.g. Soul Consumption needs Wight
    /// OR Necromancer). Independent of Parents — both checks must pass if both
    /// are non-empty.</summary>
    public List<string> ParentsAny = new();
    /// <summary>Mutex set: this skill is unavailable while any of these is
    /// learned (and vice versa, by symmetry — the panel checks both directions).
    /// "Can't have both" relationships in the metamorphosis tree use this.</summary>
    public List<string> ExclusiveOf = new();
    public List<SkillCost> Costs = new();
    public string Effect = "noop";
    public string EffectArg = "";
    public bool StartLearned;

    /// <summary>Populated by SkillTab.ResolveLinks — read-only for callers.</summary>
    public List<string> ChildIds = new();
}

public struct SkillCost
{
    /// <summary>"item" — consumed from inventory at unlock. "event" — must have been
    /// tallied at least Amount times (milestone, not consumed).</summary>
    public string Type;
    /// <summary>Item id (matches data/items.json) or event key.</summary>
    public string Id;
    public int Amount;
}
