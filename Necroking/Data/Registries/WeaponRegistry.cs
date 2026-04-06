using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Necroking.Data.Registries;

public class WeaponDef : IHasId
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("damage")] public int Damage { get; set; }
    [JsonPropertyName("attackBonus")] public int AttackBonus { get; set; }
    [JsonPropertyName("defenseBonus")] public int DefenseBonus { get; set; }
    [JsonPropertyName("length")] public int Length { get; set; } = 1;
    [JsonPropertyName("isRanged")] public bool IsRanged { get; set; }
    [JsonPropertyName("range")] public float Range { get; set; }
    [JsonPropertyName("directRange")] public float DirectRange { get; set; }
    [JsonPropertyName("cooldown")] public float Cooldown { get; set; }
    [JsonPropertyName("rangedDamage")] public int RangedDamage { get; set; }
    [JsonPropertyName("precision")] public int Precision { get; set; }
    [JsonPropertyName("projectileType")] public string ProjectileType { get; set; } = "Arrow";
    [JsonPropertyName("bonuses")] public List<string> Bonuses { get; set; } = new();
    /// <summary>
    /// Animation name to play when this weapon is used. Examples: "Attack1", "Attack2",
    /// "Ranged1", "AttackBite". If null/empty, defaults to "Ranged1" for ranged weapons
    /// and "Attack1" for melee weapons. Looked up via AnimController fallback chain.
    /// </summary>
    [JsonPropertyName("anim")] public string? AnimName { get; set; }
}

public class WeaponRegistry : RegistryBase<WeaponDef>
{
    protected override string RootKey => "weapons";
}
