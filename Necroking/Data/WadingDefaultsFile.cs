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
    // All properties (here and on DirectionalFractions) carry explicit
    // [JsonPropertyName] attributes, so the shared Indented preset produces
    // the same on-disk shape the old private CamelCase options did.

    public static bool Load(string path)
    {
        if (!Core.JsonFile.Load<WadingDefaultsJson>(path, Core.JsonDefaults.Indented, out var data) || data == null)
            return false;
        // Ensure intercardinals are filled in legacy 4-direction files.
        data.QuadrupedBottom?.EnsureDiagonalsBackfilled();
        data.QuadrupedTop?.EnsureDiagonalsBackfilled();
        WadingDefaults.Apply(data.QuadrupedBottom, data.QuadrupedTop);
        return true;
    }

    /// <summary>Persist the current in-memory <see cref="WadingDefaults"/>
    /// values to the JSON file (atomic via <see cref="Core.JsonFile"/>).
    /// Called from the unit editor's "Save as default" button.</summary>
    public static bool Save(string path)
        => Core.JsonFile.Save(path, new WadingDefaultsJson
        {
            QuadrupedBottom = CloneFractions(WadingDefaults.QuadrupedBottom),
            QuadrupedTop    = CloneFractions(WadingDefaults.QuadrupedTop),
        }, Core.JsonDefaults.Indented);

    private static DirectionalFractions CloneFractions(DirectionalFractions src)
    {
        var c = new DirectionalFractions();
        c.CopyFrom(src);
        return c;
    }
}
