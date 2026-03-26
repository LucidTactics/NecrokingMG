namespace Necroking.Data;

// --- Unit & Combat ---

public enum UnitType : byte
{
    Necromancer = 0, Skeleton, Abomination, Militia, Soldier, Knight, Archer,
    Dynamic, // editor-created units (string ID lookup)
    Count
}

public enum Faction : byte { Undead = 0, Human = 1 }

public enum AIBehavior : byte
{
    PlayerControlled = 0, AttackClosest, AttackClosestRetarget, GuardKnight,
    AttackNecromancer, MoveToPoint, ArcherAttack, IdleAtPoint, DefendPoint,
    Raid, Patrol, CorpseWorker, Caster, OrderAttack
}

public enum ProjectileType : byte { Arrow, Fireball }
public enum HitLocation : byte { Head, Arms, Chest, Legs, Feet }

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

public enum WeaponBonus : byte { ArmorPiercing, ArmorNegating }
public enum ArmorBonus : byte { TrueArmor, Barbed }

// --- Settings ---

public enum UnitShadowMode { Ellipse = 0, Shader = 1 }
public enum HordePositionPref { Front = 0, Back = 1, Even = 2 }
