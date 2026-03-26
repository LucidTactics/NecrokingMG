using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Necroking.Data.Registries;

public class ArmorDef : IHasId
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("bodyProtection")] public int BodyProtection { get; set; }
    [JsonPropertyName("headProtection")] public int HeadProtection { get; set; }
    [JsonPropertyName("encumbrance")] public int Encumbrance { get; set; }
    [JsonPropertyName("bonuses")] public List<string> Bonuses { get; set; } = new();
}

public class ArmorRegistry : RegistryBase<ArmorDef>
{
    protected override string RootKey => "armors";
}
