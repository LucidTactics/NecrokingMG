using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Necroking.Editor;

/// <summary>
/// A procedural-generation brush preset for the map editor's ProcGen tab: two env-def
/// pools (large / small), each with a density. Density D means objects placed from
/// that pool must stay at least 8/sqrt(D) world units away from every other object
/// placed from the same pool (density 4 = 4 apart, density 16 = 2 apart), and while
/// the brush is held the pool attempts D*5 placements per second.
/// Styles are a global authoring registry (like env defs), stored in
/// data/procgen_styles.json.
/// </summary>
public class ProcGenStyle
{
    public string Name = "New Style";
    public List<string> LargeDefIds = new();
    public float LargeDensity = 4f;
    public List<string> SmallDefIds = new();
    public float SmallDensity = 25f;

    // Per-pool auto-ground: stamp a ground patch under each object this pool
    // places (e.g. dirt under trees). Same feature as the Objects tab, per category.
    public AutoGroundSettings LargeAutoGround = new();
    public AutoGroundSettings SmallAutoGround = new();

    /// <summary>Min spacing between two objects of the same pool: 8/sqrt(density).</summary>
    public static float MinDistance(float density) => 8f / MathF.Sqrt(MathF.Max(0.01f, density));

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
            writer.WriteNumber("largeDensity", s.LargeDensity);
            writer.WriteStartArray("largeDefIds");
            foreach (var id in s.LargeDefIds) writer.WriteStringValue(id);
            writer.WriteEndArray();
            WriteAutoGround(writer, "largeAutoGround", s.LargeAutoGround);
            writer.WriteNumber("smallDensity", s.SmallDensity);
            writer.WriteStartArray("smallDefIds");
            foreach (var id in s.SmallDefIds) writer.WriteStringValue(id);
            writer.WriteEndArray();
            WriteAutoGround(writer, "smallAutoGround", s.SmallAutoGround);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
    }

    /// <summary>Load styles from the registry into <paramref name="styles"/> (cleared
    /// first). No-ops on a missing file.</summary>
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
            if (e.TryGetProperty("largeDensity", out var ld)) s.LargeDensity = ld.GetSingle();
            if (e.TryGetProperty("smallDensity", out var sd)) s.SmallDensity = sd.GetSingle();
            if (e.TryGetProperty("largeDefIds", out var lids))
                foreach (var id in lids.EnumerateArray())
                    if (id.GetString() is { Length: > 0 } v) s.LargeDefIds.Add(v);
            if (e.TryGetProperty("smallDefIds", out var sids))
                foreach (var id in sids.EnumerateArray())
                    if (id.GetString() is { Length: > 0 } v) s.SmallDefIds.Add(v);
            if (e.TryGetProperty("largeAutoGround", out var lag)) ReadAutoGround(lag, s.LargeAutoGround);
            if (e.TryGetProperty("smallAutoGround", out var sag)) ReadAutoGround(sag, s.SmallAutoGround);
            styles.Add(s);
        }
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
