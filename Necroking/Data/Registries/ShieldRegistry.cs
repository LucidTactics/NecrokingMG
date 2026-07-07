using System.Text.Json.Serialization;

namespace Necroking.Data.Registries;

public class ShieldDef : INamedDef
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("protection")] public int Protection { get; set; }
    [JsonPropertyName("parry")] public int Parry { get; set; }
    [JsonPropertyName("defense")] public int Defense { get; set; }
}

public class ShieldRegistry : RegistryBase<ShieldDef>
{
    protected override string RootKey => "shields";
}
