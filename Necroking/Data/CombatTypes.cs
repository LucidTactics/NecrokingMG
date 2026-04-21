using System.Collections.Generic;

namespace Necroking.Data;

public class WeaponStats
{
    public int Damage { get; set; }
    public int AttackBonus { get; set; }
    public int DefenseBonus { get; set; }
    public int Length { get; set; } = 1;
    public string Name { get; set; } = "";
    public bool IsRanged { get; set; }
    /// <summary>
    /// Animation name (e.g. "Attack1", "Attack2", "Ranged1", "AttackBite").
    /// Empty/null = use default ("Ranged1" for ranged, "Attack1" for melee).
    /// </summary>
    public string? AnimName { get; set; }

    /// <summary>Attack archetype — None = default melee, Pounce = leap-then-melee.</summary>
    public WeaponArchetype Archetype { get; set; } = WeaponArchetype.None;

    /// <summary>Per-weapon cooldown in rounds. Cycle time = CooldownRounds × RoundDuration.</summary>
    public int CooldownRounds { get; set; } = 1;

    /// <summary>Selection priority for multi-weapon units. Higher = checked first when
    /// scanning for an eligible attack. Default 0 — ties break by weapon-list order.
    /// Prevents "flipping weapon JSON order silently changes combat behavior".</summary>
    public int Priority { get; set; } = 0;

    /// <summary>Cosmetic lunge distance (world units) for this unit-weapon combo.
    /// The sprite translates forward toward the target at the hit frame and decays
    /// back by the end of the anim; simulation position is unchanged. 0 = no lunge.
    /// Sourced from the per-unit-per-slot override on UnitWeaponRef at load time.</summary>
    public float LungeDist { get; set; }

    /// <summary>Runtime per-weapon cooldown timer in seconds (ticked down each frame).
    /// Reset to cycle when this weapon is used. Lets a unit carry both a short-cycle
    /// normal melee AND a long-cycle pounce — when pounce is on cooldown the normal
    /// melee can still fire.</summary>
    public float Cooldown { get; set; }

    // Pounce-archetype parameters (used only when Archetype == Pounce)
    public float PounceMinRange { get; set; } = 3f;
    public float PounceMaxRange { get; set; } = 8f;
    public float PounceArcPeak { get; set; } = 2f;
    public float PounceAirSpeed { get; set; } = 6f;

    /// <summary>Bonus flags per-weapon (currently also aggregated onto UnitStats for
    /// backwards compat, but kept here so resolution can check per-weapon accurately).</summary>
    public bool HasArmorPiercing { get; set; }
    public bool HasArmorNegating { get; set; }
    public bool HasKnockdown { get; set; }
}

public class ArmorStats
{
    public int BodyProtection { get; set; }
    public int HeadProtection { get; set; }
}

public class RangedStats
{
    public float Range { get; set; }
    public float DirectRange { get; set; }
    public float Cooldown { get; set; }
    public int Damage { get; set; }
    public int Precision { get; set; }
}

public class UnitStats
{
    public int MaxHP { get; set; } = 10;
    public int HP { get; set; } = 10;
    public int Strength { get; set; } = 10;
    public int Attack { get; set; } = 10;
    public int Defense { get; set; } = 10;
    public int MagicResist { get; set; } = 10;
    public int Encumbrance { get; set; }
    public int NaturalProt { get; set; }
    public float CombatSpeed { get; set; } = 8.0f;

    // Primary weapon (backward compat — populated from first melee weapon)
    public int Damage { get; set; }
    public int Length { get; set; } = 1;

    // Multi-weapon support
    public List<WeaponStats> MeleeWeapons { get; set; } = new();
    public List<WeaponStats> RangedWeapons { get; set; } = new();
    public ArmorStats Armor { get; set; } = new();

    // Shield
    public int ShieldProtection { get; set; }
    public int ShieldParry { get; set; }
    public int ShieldDefense { get; set; }

    // Bonus flags
    public bool HasArmorPiercing { get; set; }
    public bool HasArmorNegating { get; set; }
    public bool HasKnockdown { get; set; }
    public bool HasTrueArmor { get; set; }
    public bool HasBarbed { get; set; }

    public List<float> RangedRange { get; set; } = new();
    public List<float> RangedDirectRange { get; set; } = new();
    public List<float> RangedCooldownTime { get; set; } = new();
    public List<int> RangedDmg { get; set; } = new();
}
