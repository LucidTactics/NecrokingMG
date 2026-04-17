using System;
using System.Collections.Generic;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.Render;

namespace Necroking.Movement;

public struct ActiveBuff
{
    public string BuffDefID;
    public float RemainingDuration;
    public bool Permanent;
    public List<BuffEffect> Effects;
    public int StackCount;
}

/// <summary>
/// Tracks a unit being incapacitated by a debuff (knockdown, stun, freeze, etc.).
/// The buff system manages the lifecycle; other systems just check Incap.IsLocked.
/// </summary>
public struct IncapState
{
    public bool Active;                // True while incapacitated (blocks AI/movement/combat)
    public Render.AnimState HoldAnim;  // Animation to show while incapacitated (e.g. Knockdown)
    public Render.AnimState RecoverAnim; // Animation to play on recovery (e.g. Standup)
    public float RecoverTime;          // Duration of recovery animation
    public float RecoverTimer;         // Countdown for recovery phase
    public bool Recovering;            // True during recovery animation (after incap ends)
    public bool HoldAtEnd;             // Snap to last frame of HoldAnim on entry

    /// <summary>Is the unit currently incapacitated or recovering?</summary>
    public readonly bool IsLocked => Active || Recovering;
}

public enum UnitStatusSymbol : byte
{
    None = 0,
    Notice = 1,  // "?" — unit has spotted a threat (e.g. deer freezes and watches)
    React = 2,   // "!" — unit is reacting (fleeing, charging, fighting back)
}

public static class UnitStatusSymbolExt
{
    /// <summary>Show a status symbol above the unit. Higher priority symbols (React &gt; Notice)
    /// don't get downgraded; duration is refreshed to the max of current vs new.</summary>
    public static void ShowStatusSymbol(this Unit u, UnitStatusSymbol sym, float duration)
    {
        if ((byte)sym < u.StatusSymbol) return;
        u.StatusSymbol = (byte)sym;
        if (duration > u.StatusSymbolTimer) u.StatusSymbolTimer = duration;
    }
}

public class Unit
{
    // Hot path
    public Vec2 Position;
    public Vec2 Velocity;
    public Vec2 PreferredVel;
    public float Z;             // Height above ground (0 = on ground). Used by 2.5D impulse physics.
    public bool InPhysics;      // True while physics system owns this unit's movement.
    public bool OverrideStarted; // Tracks whether OverrideAnim has been applied to AnimController

    // Movement
    public float Radius = 0.495f;
    public float MaxSpeed = 8f;
    public int OrcaPriority;
    public Vec2 MoveTarget;

    // Identity
    public int Size = 2;
    public uint Id;
    public UnitType Type;
    public string UnitDefID = "";
    public Faction Faction;
    public AIBehavior AI = AIBehavior.AttackClosest;

    // Combat
    public UnitStats Stats = new();
    public float Fatigue;
    public CombatTarget Target = CombatTarget.None;
    public float RetargetTimer;

    // State
    public bool Alive = true;
    public bool InCombat;
    public float FacingAngle = 90f;
    public float AttackCooldown;
    public CombatTarget PendingAttack = CombatTarget.None;
    /// <summary>
    /// Index into the chosen weapon list for the pending attack.
    /// -1 = no specific weapon (unarmed / legacy). Use with PendingWeaponIsRanged
    /// to pick the right list (Stats.MeleeWeapons vs Stats.RangedWeapons).
    /// </summary>
    public int PendingWeaponIdx = -1;
    public bool PendingWeaponIsRanged;
    /// <summary>UnitID of the ranged target captured at queue time (target may die before action moment).</summary>
    public uint PendingRangedTarget = GameConstants.InvalidUnit;
    public float HitShakeTimer;
    public uint LastAttackerID = GameConstants.InvalidUnit;
    public float FleeTimer;
    public byte WolfPhase;
    public float WolfPhaseTimer;

    // Jumping
    public bool Jumping;
    public float JumpTimer;
    public float JumpDuration = 1f;
    public Vec2 JumpStartPos;
    public Vec2 JumpEndPos;
    public bool JumpIsAttack;
    public bool JumpAttackFired;
    public float JumpHeight;

    // TODO: Add this in settings!
    public float CollisionHeight = 1.0f;

    // Incapacitation (knockdown, stun, freeze, etc.) — managed by buff system
    public IncapState Incap;
    public float StandupTimer; // Legacy: used by AI handlers for sleep→standup (separate from incap)
    public float KnockdownTimer; // Legacy: unused, kept for compatibility
    public int Harassment;

    // Rendering
    public float SpriteScale = 1f;
    public Vec2 EffectSpawnPos2D;
    public float EffectSpawnHeight;

    // Buffs
    public List<ActiveBuff> ActiveBuffs = new();

    // Corpse interaction
    public int CarryingCorpseID = -1;       // CorpseID being carried (-1 = none)
    public int BaggingCorpseID = -1;        // CorpseID being bagged (-1 = none)
    public float BaggingTimer;              // elapsed time during bagging
    public byte CorpseInteractPhase;        // 0=none, 1=WorkStart, 2=WorkLoop, 3=WorkEnd, 4=Pickup, 5=PutDown

    // Building interaction
    public int BuildTargetIdx = -1;         // env object index being built (-1 = none)
    public int BuildGlyphIdx = -1;          // glyph index being built (-1 = none)
    public float BuildTimer;                // elapsed time during building

    // Spawn/Raid/Patrol
    public int SpawnBuildingIdx = -1;
    public int RaidTargetIdx = -1;
    public int PatrolRouteIdx = -1;
    public int PatrolWaypointIdx;

    // Caster
    public float Mana;
    public float MaxMana;
    public float ManaRegen;
    public float SpellCooldownTimer;
    public string SpellID = "";

    // Engagement & combat state
    public CombatTarget EngagedTarget = CombatTarget.None;
    public float PostAttackTimer;
    public float MoveTime;
    public QueuedUnitAction QueuedAction = QueuedUnitAction.None;
    public float AnimPlaybackSpeed = 1f;

    // Ghost mode
    public bool GhostMode;

    // Dodge / React (set on combat events, cleared each tick)
    public bool Dodging;
    public bool HitReacting;
    public bool BlockReacting;

    // Stuck detection for ORCA nudge
    public int StuckFrames;

    // AI behavior framework (replaces WolfPhase/FleeTimer for new archetypes)
    public byte Archetype;
    public byte Routine;
    public byte Subroutine;
    public float SubroutineTimer;
    public byte AlertState;
    public float AlertTimer;
    public uint AlertTarget = GameConstants.InvalidUnit;
    public Vec2 SpawnPosition;
    public bool IsSneaking;

    // Two-channel animation system
    public AnimRequest RoutineAnim;     // Set by AI each frame (locomotion, feeding, etc.)
    public AnimRequest OverrideAnim;    // Set by combat/physics, auto-expires
    public float OverrideTimer;         // Counts down, clears override when <= 0

    // Per-unit awareness config (set from UnitDef at spawn)
    public float DetectionRange;
    public float DetectionBreakRange;
    public float AlertDuration = 2f;
    public float AlertEscalateRange;
    public float GroupAlertRadius;

    // Status symbol above head (? for notice, ! for react). Ticked down by Simulation.
    public byte StatusSymbol;        // 0=none, 1=Notice (?), 2=React (!)
    public float StatusSymbolTimer;  // Seconds remaining before symbol clears

    // Potion effects
    public int PoisonStacks;
    public float PoisonTickTimer;
    public float CloudExposureTime; // Cumulative time spent in poison clouds
    public float WeaponPoisonCoatTimer;
    public int WeaponPoisonAmount;
    public bool ZombieOnDeath;
    public float WeaponZombieCoatTimer;
    public float ParalysisSlowTimer;
    public float ParalysisStunTimer;
    public bool Frenzied;
}

/// <summary>
/// Unit storage — list of Unit objects indexed by unit index.
/// </summary>
public class UnitArrays
{
    private readonly List<Unit> _units = new();
    private uint _nextID;

    public int Count => _units.Count;

    public Unit this[int index] => _units[index];

    public int AddUnit(Vec2 pos, UnitType type)
    {
        int idx = _units.Count;
        var u = new Unit
        {
            Position = pos,
            MoveTarget = pos,
            Id = _nextID++,
            Type = type,
            UnitDefID = type.ToString().ToLowerInvariant(),
            Faction = type <= UnitType.Abomination ? Data.Faction.Undead : Data.Faction.Human,
            SpawnPosition = pos,
        };
        _units.Add(u);
        return idx;
    }

    public void RemoveUnit(int index)
    {
        if (index < 0 || index >= _units.Count) return;
        int last = _units.Count - 1;
        if (index != last)
        {
            (_units[index], _units[last]) = (_units[last], _units[index]);
        }
        _units.RemoveAt(last);
    }

    public void Clear()
    {
        _units.Clear();
    }
}

public static class UnitUtil
{
    public static int ResolveUnitIndex(UnitArrays units, uint uid)
    {
        if (uid == GameConstants.InvalidUnit) return -1;
        for (int i = 0; i < units.Count; i++)
            if (units[i].Id == uid && units[i].Alive) return i;
        return -1;
    }

    private static readonly Random _rng = new();

    /// <summary>Roll a hit location based on size advantage and weapon length.</summary>
    public static HitLocation RollHitLocation(int attackerSize, int defenderSize, int weaponLength)
    {
        int roll = _rng.Next(100);
        int sizeAdvantage = attackerSize - defenderSize + weaponLength;
        // Larger attacker / longer weapon = more head hits
        int headChance = Math.Clamp(10 + sizeAdvantage * 5, 5, 30);
        int armsChance = 15;
        int chestChance = 40;
        int legsChance = 25;
        // remaining = feet
        if (roll < headChance) return HitLocation.Head;
        if (roll < headChance + armsChance) return HitLocation.Arms;
        if (roll < headChance + armsChance + chestChance) return HitLocation.Chest;
        if (roll < headChance + armsChance + chestChance + legsChance) return HitLocation.Legs;
        return HitLocation.Feet;
    }

    /// <summary>DRN: Dominions Random Number — 2d6 open-ended (exploding 6s)</summary>
    public static int RollDRN()
    {
        int total = 0;
        for (int die = 0; die < 2; die++)
        {
            int roll;
            do
            {
                roll = _rng.Next(1, 7);
                total += roll;
            } while (roll == 6);
        }
        return total;
    }
}

public class NecromancerState
{
    public float Mana { get; set; } = 10f;
    public float MaxMana { get; set; } = 50f;
    public float ManaRegen { get; set; } = 1f;
    public Dictionary<string, float> SpellCooldowns { get; set; } = new();
    public Dictionary<string, int> Inventory { get; set; } = new();

    public void TickCooldowns(float dt)
    {
        var keys = new List<string>(SpellCooldowns.Keys);
        foreach (var key in keys)
            SpellCooldowns[key] = MathF.Max(0f, SpellCooldowns[key] - dt);
    }

    public float GetCooldown(string id) =>
        SpellCooldowns.TryGetValue(id, out float cd) ? cd : 0f;
}
