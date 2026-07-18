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

    // Sweep-archetype parameters (used only when Archetype == Sweep).
    // Forward cone centered on attacker's facing: half-arc is SweepArcDegrees/2
    // on each side. Cone origin is the attacker's center — at short range the
    // arc is narrow; at range SweepRadius it's the widest.
    public float SweepArcDegrees { get; set; } = 120f;
    public float SweepRadius { get; set; } = 2.5f;
    /// <summary>If true, sweep hits allies caught in the cone as well as enemies.
    /// Default false — hostiles only, standard AOE melee behaviour.</summary>
    public bool SweepHitsAllies { get; set; } = false;

    // Trample-archetype parameters (used only when Archetype == Trample).
    // A charging attack: attacker picks a smaller-size target in the range
    // window, homes toward its current position at CombatSpeed × (1 + SpeedBonus).
    // While charging it phases through smaller units, damaging each one that
    // enters TrampleRadius (independent roll + knockback impulse). Impact fires
    // when the attacker closes within TrampleImpactRange of the target or after
    // travelling TrampleMaxChaseDistance, whichever comes first.
    public float TrampleMinRange { get; set; } = 2f;
    public float TrampleMaxRange { get; set; } = 6f;
    public float TrampleMaxChaseDistance { get; set; } = 4f;
    public float TrampleImpactRange { get; set; } = 1.5f;
    public float TrampleSpeedBonus { get; set; } = 0.15f;
    public float TrampleRadius { get; set; } = 1.5f;
    public float TrampleKnockbackForce { get; set; } = 6f;
    public float TrampleImpactForce { get; set; } = 10f;

    /// <summary>Bonus flags per-weapon (currently also aggregated onto UnitStats for
    /// backwards compat, but kept here so resolution can check per-weapon accurately).</summary>
    public bool HasArmorPiercing { get; set; }
    public bool HasArmorNegating { get; set; }
    public bool HasKnockdown { get; set; }

    /// <summary>Dominions damage type (Slashing/Piercing/Blunt). Drives the per-type
    /// damage modifiers in melee resolution (piercing armor-reduction, blunt head bonus,
    /// slashing post-prot bonus + limb-chopping). An explicit <see cref="DamageTypeOverride"/>
    /// (from data/weapons.json "damageType") WINS; otherwise it is inferred from
    /// <see cref="Name"/> via <see cref="WeaponClassifier"/>. The override exists so a
    /// cosmetic rename can never silently change a weapon's combat mechanics.
    /// The inference is cached per Name instance — this is read per hit in combat
    /// resolution, and the keyword scan was rerunning on every read.</summary>
    public WeaponDamageType DamageType
    {
        get
        {
            if (DamageTypeOverride.HasValue) return DamageTypeOverride.Value;
            if (!ReferenceEquals(_classifiedName, Name))
            {
                _classifiedDamageType = WeaponClassifier.Classify(Name);
                _classifiedTwoHanded = WeaponClassifier.IsTwoHanded(Name);
                _classifiedName = Name;
            }
            return _classifiedDamageType;
        }
    }

    /// <summary>Explicit damage type from weapon data ("damageType"); null = infer from Name.</summary>
    public WeaponDamageType? DamageTypeOverride { get; set; }

    private string? _classifiedName;
    private WeaponDamageType _classifiedDamageType;
    private bool _classifiedTwoHanded;

    /// <summary>True for unambiguously two-handed weapons (greatsword, maul, pike,
    /// halberd, …). Two-handed weapons add 125% of Strength to damage instead of
    /// 100% (manual p.61). Explicit <see cref="TwoHandedOverride"/> wins; else inferred
    /// from <see cref="Name"/> (cached per Name instance, same as <see cref="DamageType"/>).</summary>
    public bool TwoHanded
    {
        get
        {
            if (TwoHandedOverride.HasValue) return TwoHandedOverride.Value;
            if (!ReferenceEquals(_classifiedName, Name))
            {
                _classifiedDamageType = WeaponClassifier.Classify(Name);
                _classifiedTwoHanded = WeaponClassifier.IsTwoHanded(Name);
                _classifiedName = Name;
            }
            return _classifiedTwoHanded;
        }
    }

    /// <summary>Explicit two-handed flag from weapon data ("twoHanded"); null = infer from Name.</summary>
    public bool? TwoHandedOverride { get; set; }

    /// <summary>ID of the buff that contributed this weapon to the unit's effective
    /// list, or empty for weapons that come from the UnitDef. Lets the buff-removal
    /// path strip granted weapons without touching base-equipment slots that happen
    /// to share an ID.</summary>
    public string SourceBuffID { get; set; } = "";
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
    /// <summary>Dominions Morale — likelihood of not routing. Checked when the unit
    /// takes casualties / is locally outnumbered (see MoraleSystem). Higher = steadier.
    /// Mindless/fearless units use a very high value (e.g. 50).</summary>
    public int Morale { get; set; } = 10;
    public int Encumbrance { get; set; }
    /// <summary>Hide/flesh thickness. Unlike armor's flat block, toughness HALVES
    /// post-armor damage up to its value: net = D - min(D, Toughness)/2. Great vs
    /// chip damage, leaks vs big hits. Armor bypass mechanics (piercing/AP/
    /// armor-defeating/AN) reduce it by the same fraction as armor.</summary>
    public int Toughness { get; set; }
    /// <summary>Dice tier for this unit's rolls EXCEPT hit resolution: damage as
    /// attacker, protection as defender, morale, knockdown, MR. Attack-vs-defense
    /// hit rolls always use tier 4 for both sides (see UnitUtil.RollDRN).
    /// 1=d3, 2=d6, 3=d6 exploding once, 4=d6 open-ended, -1=0 deterministic.</summary>
    public int Drn { get; set; } = 2;
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
    public bool HasBarbed { get; set; }

    public List<float> RangedRange { get; set; } = new();
    public List<float> RangedDirectRange { get; set; } = new();
    public List<float> RangedCooldownTime { get; set; } = new();
    public List<int> RangedDmg { get; set; } = new();
    public List<int> RangedPrecision { get; set; } = new();
}
