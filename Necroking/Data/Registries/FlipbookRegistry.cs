using System.Text.Json.Serialization;

namespace Necroking.Data.Registries;

public class FlipbookDef : IHasId
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("cols")] public int Cols { get; set; } = 1;
    [JsonPropertyName("rows")] public int Rows { get; set; } = 1;
    [JsonPropertyName("defaultFPS")] public float DefaultFPS { get; set; } = 30.0f;
}

public class FlipbookRegistry : RegistryBase<FlipbookDef>
{
    protected override string RootKey => "flipbooks";
}
