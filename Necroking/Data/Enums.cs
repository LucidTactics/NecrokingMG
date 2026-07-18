namespace Necroking.Data;

// --- Unit & Combat ---

public enum UnitType : byte
{
    Necromancer = 0, Skeleton, Abomination, Militia, Soldier, Knight, Archer,
    Dynamic, // editor-created units (string ID lookup)
    Count
}

public enum Faction : byte { Undead = 0, Human = 1, Animal = 2 }

// Bitmask of factions for spatial queries that want to include/exclude multiple
// factions in a single call (e.g. "everyone except my faction"). Stays in sync
// with Faction enum values via (1 << (int)Faction).
[System.Flags]
public enum FactionMask : byte
{
    None   = 0,
    Undead = 1 << 0,
    Human  = 1 << 1,
    Animal = 1 << 2,
    All    = Undead | Human | Animal,
}

public static class FactionMaskExt
{
    public static FactionMask Bit(this Faction f) => (FactionMask)(1 << (int)f);
    public static FactionMask AllExcept(Faction f) => FactionMask.All & ~f.Bit();
}

// Legacy AI enum — most behaviors have migrated to the archetype system
// (ArchetypeRegistry / IArchetypeHandler); a unit with Archetype > 0 never runs
// these. What's left: PlayerControlled is the load-bearing "is the player"
// marker; MoveToPoint/IdleAtPoint are dev/scenario/net-ghost primitives;
// AttackClosest is the legacy horde brain (the field default); the rest are
// pending migration.
public enum AIBehavior : byte
{
    PlayerControlled = 0, AttackClosest,
    MoveToPoint, IdleAtPoint,
    Caster,   // migrated to CasterUnit archetype; kept so old def "ai" strings parse harmlessly
}

/// <summary>
/// What a projectile does when it reaches something — named for behavior, not visuals.
/// RegularHit strikes the first unit it touches along its flight path (arrows, magic darts);
/// Explosive bursts on proximity/ground impact and deals AoE damage; Potion delivers a
/// potion payload to the closest unit/corpse where it lands. How it flies (flat vs lob)
/// is the separate <c>lob</c> argument to <c>ProjectileManager.Spawn</c>.
/// </summary>
public enum ProjectileType : byte { RegularHit, Explosive, Potion }
public enum HitLocation : byte { Head, Arms, Chest, Legs, Feet }

/// <summary>
/// Permanent battle wounds (manual p.60). Applied when a slashing weapon scores a
/// limb/head hit costing >= 50% of the target's max HP: arms/legs are maimed, a head
/// is severed (lethal). Each is a one-time flag — a unit can lose each part once.
/// </summary>
[System.Flags]
public enum Affliction : byte
{
    None    = 0,
    LostEye = 1,  // head hit — reduced Attack/Precision
    LostArm = 2,  // arm chopped — reduced Attack + Strength
    LostLeg = 4,  // leg chopped — reduced Defense + Combat Speed
}

// --- Spell ---

// Every category that appears in data/spells.json. Unknown is the parse-failure
// sentinel for SpellDef.CategoryEnum — it lands in the same `default:` switch
// branches an unrecognized string always did, and the bad string itself is
// reported at load by SpellRegistry.ValidateDef.
public enum SpellCategory : byte
{
    Projectile, Buff, Debuff, Summon, Strike, Beam, Drain, Command, Toggle,
    Cloud, Sacrifice, Blight, WolfHunt, TestShape,
    Unknown,
}
public enum AOEType : byte { Single, AOE, Chain }
public enum Trajectory : byte { Lob, DirectFire, Homing, Swirly, HomingSwirly, HighLob }
/// <summary>Extra corkscrew wobble layered on a projectile's flight path
/// (SpellDef.TrajectoryMods; "" = None).</summary>
public enum TrajectoryMod : byte { None, Swirly, Swirly3d }
public enum SummonTargetReq : byte { None, Corpse, UnitType, CorpseAOE }
public enum SummonMode : byte { Spawn, Transform }
public enum StrikeVisual : byte { Lightning, GodRay }
public enum SpellTargetFilter : byte { AnyEnemy, UndeadOnly, LivingOnly }
public enum SpawnLocation : byte { NearestTargetToMouse, NearestTargetToCaster, AdjacentToCaster, AtTargetLocation }
public enum EffectBlendMode : byte { Alpha, Additive }
public enum EffectAlignment : byte { Ground, Upright }

// --- Buff ---

// Unknown = parse-failure sentinel for BuffEffect.TypeEnum: an unrecognized type
// string falls through every switch (the effect is inert), exactly as the raw
// string compares behaved — and the bad string is reported at load.
public enum BuffEffectType : byte { Set, Add, Multiply, Unknown }
public enum BuffStat : byte { Strength, Attack, Defense, MagicResist, Toughness, CombatSpeed, MaxHP, Encumbrance, Count }

// --- Weapon/Armor ---

public enum WeaponBonus : byte { ArmorPiercing, ArmorNegating, Knockdown }
public enum ArmorBonus : byte { Barbed }
public enum WeaponArchetype : byte { None = 0, Pounce = 1, Sweep = 2, Trample = 3 }

// --- Settings ---

public enum UnitShadowMode { Ellipse = 0, Shader = 1 }
public enum HordePositionPref { Front = 0, Back = 1, Even = 2 }
