using System.Text.Json;
using System.Text.Json.Serialization;

namespace Necroking.Core;

/// <summary>
/// Generic deep clone via a JSON round-trip. For defs NOT owned by a
/// RegistryBase (which has its own <c>CloneDef</c> using its exact serializer
/// options) — e.g. EnvironmentObjectDef. The round-trip guarantees clone
/// fidelity for every serializable property, so fields added to a def later
/// can never be silently dropped by a hand-maintained member copy.
/// </summary>
public static class JsonClone
{
    private static readonly JsonSerializerOptions _opts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        IncludeFields = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static T? Deep<T>(T src) where T : class
    {
        try
        {
            string json = JsonSerializer.Serialize(src, _opts);
            return JsonSerializer.Deserialize<T>(json, _opts);
        }
        catch (System.Exception ex)
        {
            DebugLog.Log("error", $"JsonClone.Deep<{typeof(T).Name}> failed: {ex.Message}");
            return null;
        }
    }
}
