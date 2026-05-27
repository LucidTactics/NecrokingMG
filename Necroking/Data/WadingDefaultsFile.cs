using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Necroking.Data.Registries;
using Necroking.Render;

namespace Necroking.Data;

/// <summary>JSON shape of data/wading_defaults.json. Loaded once at
/// startup to overwrite the code-level <see cref="WadingDefaults"/>
/// constants. Saved by the unit editor's "Save as default" button when
/// the user wants their per-unit tuning to apply to all unset
/// quadrupeds.</summary>
internal class WadingDefaultsJson
{
    [JsonPropertyName("quadrupedBottom")] public DirectionalFractions? QuadrupedBottom { get; set; }
    [JsonPropertyName("quadrupedTop")]    public DirectionalFractions? QuadrupedTop    { get; set; }
}

/// <summary>Load/save helper for data/wading_defaults.json. The file is
/// optional — missing or malformed file silently falls back to the
/// embedded literals in <see cref="WadingDefaults"/>.</summary>
public static class WadingDefaultsFile
{
    private static readonly JsonSerializerOptions _opts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static bool Load(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            string text = File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<WadingDefaultsJson>(text, _opts);
            if (data == null) return false;
            // Ensure intercardinals are filled in legacy 4-direction files.
            data.QuadrupedBottom?.EnsureDiagonalsBackfilled();
            data.QuadrupedTop?.EnsureDiagonalsBackfilled();
            WadingDefaults.Apply(data.QuadrupedBottom, data.QuadrupedTop);
            return true;
        }
        catch (Exception ex)
        {
            Core.DebugLog.Log("startup", $"WadingDefaults.Load failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Persist the current in-memory <see cref="WadingDefaults"/>
    /// values to the JSON file. Called from the unit editor's
    /// "Save as default" button.</summary>
    public static bool Save(string path)
    {
        try
        {
            var data = new WadingDefaultsJson
            {
                QuadrupedBottom = CloneFractions(WadingDefaults.QuadrupedBottom),
                QuadrupedTop    = CloneFractions(WadingDefaults.QuadrupedTop),
            };
            string text = JsonSerializer.Serialize(data, _opts);
            File.WriteAllText(path, text);
            return true;
        }
        catch (Exception ex)
        {
            Core.DebugLog.Log("startup", $"WadingDefaults.Save failed: {ex.Message}");
            return false;
        }
    }

    private static DirectionalFractions CloneFractions(DirectionalFractions src)
    {
        var c = new DirectionalFractions();
        c.CopyFrom(src);
        return c;
    }
}
