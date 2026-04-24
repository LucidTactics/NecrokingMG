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

    /// <summary>Attack archetype — "None" (default melee) or "Pounce". Weapons have
    /// exactly one archetype (unlike bonuses, which can stack).</summary>
    [JsonPropertyName("archetype")] public string Archetype { get; set; } = "None";

    /// <summary>Cooldown in rounds (a round = GameSettings.Combat.RoundDuration seconds).
    /// 1 round default. Cycle time = CooldownRounds × RoundDuration.</summary>
    [JsonPropertyName("cooldownRounds")] public int CooldownRounds { get; set; } = 1;

    /// <summary>Selection priority in a multi-weapon unit (higher = checked first).
    /// Ties break by weapon-list order. Default 0.</summary>
    [JsonPropertyName("priority")] public int Priority { get; set; } = 0;

    // --- Pounce archetype parameters (used only when Archetype == "Pounce") ---
    [JsonPropertyName("pounceMinRange")] public float PounceMinRange { get; set; } = 3f;
    [JsonPropertyName("pounceMaxRange")] public float PounceMaxRange { get; set; } = 8f;
    [JsonPropertyName("pounceArcPeak")] public float PounceArcPeak { get; set; } = 2f;
    [JsonPropertyName("pounceAirSpeed")] public float PounceAirSpeed { get; set; } = 6f;

    // --- Sweep archetype parameters (used only when Archetype == "Sweep") ---
    [JsonPropertyName("sweepArcDegrees")] public float SweepArcDegrees { get; set; } = 120f;
    [JsonPropertyName("sweepRadius")] public float SweepRadius { get; set; } = 2.5f;
    [JsonPropertyName("sweepHitsAllies")] public bool SweepHitsAllies { get; set; } = false;

    // --- Trample archetype parameters (used only when Archetype == "Trample") ---
    [JsonPropertyName("trampleMinRange")] public float TrampleMinRange { get; set; } = 2f;
    [JsonPropertyName("trampleMaxRange")] public float TrampleMaxRange { get; set; } = 6f;
    [JsonPropertyName("trampleMaxChaseDistance")] public float TrampleMaxChaseDistance { get; set; } = 4f;
    [JsonPropertyName("trampleImpactRange")] public float TrampleImpactRange { get; set; } = 1.5f;
    [JsonPropertyName("trampleSpeedBonus")] public float TrampleSpeedBonus { get; set; } = 0.15f;
    [JsonPropertyName("trampleRadius")] public float TrampleRadius { get; set; } = 1.5f;
    [JsonPropertyName("trampleKnockbackForce")] public float TrampleKnockbackForce { get; set; } = 6f;
    [JsonPropertyName("trampleImpactForce")] public float TrampleImpactForce { get; set; } = 10f;
}

public class WeaponRegistry : RegistryBase<WeaponDef>
{
    protected override string RootKey => "weapons";
}
