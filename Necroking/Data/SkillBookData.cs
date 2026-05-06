using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Necroking.Core;

namespace Necroking.Data;

/// <summary>
/// Static defs for the skill book (Potions / Necromancy / Magic / Metamorphosis).
/// Loaded once from data/skills/&lt;tab&gt;.json. Layout (x,y per skill) is authored
/// in JSON in a logical 1280x680 tree-area pixel space, scaled to fit at draw time.
/// </summary>
public static class SkillBookDefs
{
    /// <summary>Source order = tab order in the UI.</summary>
    public static readonly string[] TabIds = { "potions", "necromancy", "magic", "metamorphosis" };

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
        foreach (var id in TabIds)
        {
            string path = GamePaths.Resolve($"data/skills/{id}.json");
            try
            {
                Tabs.Add(LoadTab(id, path));
            }
            catch (Exception ex)
            {
                DebugLog.Log("startup", $"SkillBook: failed to load {path}: {ex.Message}");
                Tabs.Add(new SkillTab { Id = id, DisplayName = id, Skills = new List<SkillDef>() });
            }
        }
        // Resolve parent ids -> indices and build children list per tab.
        foreach (var tab in Tabs) tab.ResolveLinks();
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
            Skills = new List<SkillDef>(),
        };

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
            Parents = new List<string>(),
            Costs   = new List<SkillCost>(),
        };
        if (j.TryGetProperty("parents", out var pj) && pj.ValueKind == JsonValueKind.Array)
            foreach (var p in pj.EnumerateArray())
                if (p.ValueKind == JsonValueKind.String) s.Parents.Add(p.GetString()!);
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
    public List<SkillDef> Skills = new();
    private Dictionary<string, int> _idToIndex = new();

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
    public List<string> Parents = new();
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
