using System.Text.Json;
using System.Text.Encodings.Web;

namespace Necroking.Core;

/// <summary>
/// Shared System.Text.Json options. Reusing one configured instance avoids
/// re-allocating it at every JSON-write site (the serializer also caches type
/// metadata per options instance, so reuse is marginally faster).
/// </summary>
public static class JsonDefaults
{
    /// <summary>Pretty-printed output with otherwise-default settings — the plain
    /// "indent it" preset used by settings/cache/debug savers. Do NOT use for
    /// registry/POCO files that need CamelCase or enum converters (those build their
    /// own options).</summary>
    public static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        // Write &, <, >, + literally instead of & etc. (default HTML-safe
        // escaping is pointless for game-data files and creates noisy diffs against
        // hand/Python-edited saves). Safe here — this output is never embedded in HTML.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
