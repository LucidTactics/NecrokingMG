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
    PlayerControlled = 0, AttackClosest, AttackClosestRetarget,
    MoveToPoint, ArcherAttack, IdleAtPoint,
    Caster,
    FleeWhenHit, NeutralFightBack,
    WolfHitAndRun,          // Attack → disengage 3 units → wait for cooldown → re-engage (nearest target)
    WolfOpportunist,        // Like HitAndRun but waits up to 1 cycle for target to turn away (>100° from facing)
}

public enum QueuedUnitAction : byte { None, Flee, Disengage }

public enum ProjectileType : byte { Arrow, Fireball, Potion }
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

public enum SpellCategory : byte { Projectile, Buff, Debuff, Summon, Strike, Beam, Drain, Command, Toggle }
public enum AOEType : byte { Single, AOE, Chain }
public enum Trajectory : byte { Lob, DirectFire, Homing, Swirly, HomingSwirly }
public enum SummonTargetReq : byte { None, Corpse, UnitType, CorpseAOE }
public enum SummonMode : byte { Spawn, Transform }
public enum StrikeVisual : byte { Lightning, GodRay }
public enum SpellTargetFilter : byte { AnyEnemy, UndeadOnly, LivingOnly }
public enum SpawnLocation : byte { NearestTargetToMouse, NearestTargetToCaster, AdjacentToCaster, AtTargetLocation }
public enum EffectBlendMode : byte { Alpha, Additive }
public enum EffectAlignment : byte { Ground, Upright }

// --- Buff ---

public enum BuffEffectType : byte { Set, Add, Multiply }
public enum BuffStat : byte { Strength, Attack, Defense, MagicResist, NaturalProt, CombatSpeed, MaxHP, Encumbrance, Count }

// --- Weapon/Armor ---

public enum WeaponBonus : byte { ArmorPiercing, ArmorNegating, Knockdown }
public enum ArmorBonus : byte { TrueArmor, Barbed }
public enum WeaponArchetype : byte { None = 0, Pounce = 1, Sweep = 2, Trample = 3 }

// --- Settings ---

public enum UnitShadowMode { Ellipse = 0, Shader = 1 }
public enum HordePositionPref { Front = 0, Back = 1, Even = 2 }
