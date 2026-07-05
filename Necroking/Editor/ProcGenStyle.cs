using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Necroking.Editor;

/// <summary>
/// One named object pool within a <see cref="ProcGenStyle"/> (e.g. "Large objects",
/// "Small objects", "Rocks"). Density D means objects placed from this category must
/// stay at least 8/sqrt(D) world units away from every other object placed from the
/// same category, and while the brush is held the category attempts D*5 placements
/// per second. Each category can stamp auto-ground under the objects it places.
/// </summary>
public class ProcGenCategory
{
    public string Name = "Category";
    public List<string> DefIds = new();
    public float Density = 4f;
    public AutoGroundSettings AutoGround = new();

    /// <summary>Runtime-only fractional placement attempts accrued while the brush
    /// is held. Not serialized — reset whenever painting isn't active.</summary>
    public float Accum;

    /// <summary>Deep copy (fresh lists + auto-ground; runtime Accum left at 0).</summary>
    public ProcGenCategory Clone() => new()
    {
        Name = Name,
        Density = Density,
        DefIds = new List<string>(DefIds),
        AutoGround = new AutoGroundSettings
        {
            Enabled = AutoGround.Enabled,
            TypeName = AutoGround.TypeName,
            Size = AutoGround.Size,
            Noise = AutoGround.Noise,
        },
    };
}

/// <summary>
/// A procedural-generation brush preset for the map editor's ProcGen tab: an
/// arbitrary list of named <see cref="ProcGenCategory"/> pools, each with its own
/// density and auto-ground. Styles are a global authoring registry (like env defs),
/// stored in data/procgen_styles.json.
///
/// Legacy files (two fixed "large"/"small" pools) are migrated to two categories on
/// load and re-saved in the new <c>categories</c> format on the next save.
/// </summary>
public class ProcGenStyle
{
    public string Name = "New Style";
    public List<ProcGenCategory> Categories = new();

    /// <summary>Min spacing between two objects of the same category: 8/sqrt(density).</summary>
    public static float MinDistance(float density) => 8f / MathF.Sqrt(MathF.Max(0.01f, density));

    /// <summary>Deep copy: clones every category (and its lists/auto-ground) so the
    /// duplicate shares no mutable state with the original.</summary>
    public ProcGenStyle Clone()
    {
        var copy = new ProcGenStyle { Name = Name };
        foreach (var c in Categories) copy.Categories.Add(c.Clone());
        return copy;
    }

    /// <summary>A fresh style seeded with the two classic categories, matching the
    /// old fixed-pool defaults.</summary>
    public static ProcGenStyle NewDefault(string name) => new()
    {
        Name = name,
        Categories =
        {
            new ProcGenCategory { Name = "Large objects", Density = 4f },
            new ProcGenCategory { Name = "Small objects", Density = 25f },
        }
    };

    public static void SaveAll(string path, List<ProcGenStyle> styles)
    {
        using var stream = File.Create(path);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        writer.WriteStartObject();
        writer.WriteStartArray("styles");
        foreach (var s in styles)
        {
            writer.WriteStartObject();
            writer.WriteString("name", s.Name);
            writer.WriteStartArray("categories");
            foreach (var c in s.Categories)
            {
                writer.WriteStartObject();
                writer.WriteString("name", c.Name);
                writer.WriteNumber("density", c.Density);
                writer.WriteStartArray("defIds");
                foreach (var id in c.DefIds) writer.WriteStringValue(id);
                writer.WriteEndArray();
                WriteAutoGround(writer, "autoGround", c.AutoGround);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
    }

    /// <summary>Load styles from the registry into <paramref name="styles"/> (cleared
    /// first). No-ops on a missing file. Legacy large/small files are migrated.</summary>
    public static void LoadAll(string path, List<ProcGenStyle> styles)
    {
        if (!File.Exists(path)) return;
        styles.Clear();

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("styles", out var arr)) return;
        foreach (var e in arr.EnumerateArray())
        {
            var s = new ProcGenStyle();
            if (e.TryGetProperty("name", out var name)) s.Name = name.GetString() ?? s.Name;

            if (e.TryGetProperty("categories", out var cats) && cats.ValueKind == JsonValueKind.Array)
            {
                foreach (var ce in cats.EnumerateArray())
                    s.Categories.Add(ReadCategory(ce));
            }
            else
            {
                // Legacy format: two fixed pools (large / small) → two categories.
                MigrateLegacyPools(e, s);
            }
            styles.Add(s);
        }
    }

    private static ProcGenCategory ReadCategory(JsonElement e)
    {
        var c = new ProcGenCategory();
        if (e.TryGetProperty("name", out var n)) c.Name = n.GetString() ?? c.Name;
        if (e.TryGetProperty("density", out var d)) c.Density = d.GetSingle();
        if (e.TryGetProperty("defIds", out var ids) && ids.ValueKind == JsonValueKind.Array)
            foreach (var id in ids.EnumerateArray())
                if (id.GetString() is { Length: > 0 } v) c.DefIds.Add(v);
        if (e.TryGetProperty("autoGround", out var ag)) ReadAutoGround(ag, c.AutoGround);
        return c;
    }

    /// <summary>Convert a legacy style element (largeDensity/largeDefIds/… +
    /// smallDensity/smallDefIds/…) into two named categories.</summary>
    private static void MigrateLegacyPools(JsonElement e, ProcGenStyle s)
    {
        var large = new ProcGenCategory { Name = "Large objects", Density = 4f };
        var small = new ProcGenCategory { Name = "Small objects", Density = 25f };
        if (e.TryGetProperty("largeDensity", out var ld)) large.Density = ld.GetSingle();
        if (e.TryGetProperty("smallDensity", out var sd)) small.Density = sd.GetSingle();
        if (e.TryGetProperty("largeDefIds", out var lids) && lids.ValueKind == JsonValueKind.Array)
            foreach (var id in lids.EnumerateArray())
                if (id.GetString() is { Length: > 0 } v) large.DefIds.Add(v);
        if (e.TryGetProperty("smallDefIds", out var sids) && sids.ValueKind == JsonValueKind.Array)
            foreach (var id in sids.EnumerateArray())
                if (id.GetString() is { Length: > 0 } v) small.DefIds.Add(v);
        if (e.TryGetProperty("largeAutoGround", out var lag)) ReadAutoGround(lag, large.AutoGround);
        if (e.TryGetProperty("smallAutoGround", out var sag)) ReadAutoGround(sag, small.AutoGround);
        s.Categories.Add(large);
        s.Categories.Add(small);
    }

    private static void WriteAutoGround(Utf8JsonWriter writer, string prop, AutoGroundSettings s)
    {
        writer.WriteStartObject(prop);
        writer.WriteBoolean("enabled", s.Enabled);
        writer.WriteString("type", s.TypeName);
        writer.WriteNumber("size", s.Size);
        writer.WriteNumber("noise", s.Noise);
        writer.WriteEndObject();
    }

    private static void ReadAutoGround(JsonElement e, AutoGroundSettings s)
    {
        if (e.TryGetProperty("enabled", out var en)) s.Enabled = en.GetBoolean();
        if (e.TryGetProperty("type", out var ty)) s.TypeName = ty.GetString() ?? s.TypeName;
        if (e.TryGetProperty("size", out var sz)) s.Size = sz.GetInt32();
        if (e.TryGetProperty("noise", out var no)) s.Noise = no.GetInt32();
    }
}
