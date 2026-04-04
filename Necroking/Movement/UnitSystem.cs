using System;
using System.Collections.Generic;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;

namespace Necroking.Movement;

public struct ActiveBuff
{
    public string BuffDefID;
    public float RemainingDuration;
    public bool Permanent;
    public List<BuffEffect> Effects;
    public int StackCount;
}

public class Unit
{
    // Hot path
    public Vec2 Position;
    public Vec2 Velocity;
    public Vec2 PreferredVel;

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

    // Knockdown / Standup
    public float KnockdownTimer;
    public float StandupTimer;
    public int Harassment;

    // Rendering
    public float SpriteScale = 1f;
    public Vec2 EffectSpawnPos2D;
    public float EffectSpawnHeight;

    // Buffs
    public List<ActiveBuff> ActiveBuffs = new();

    // Corpse dragging
    public int DraggingCorpseIdx = -1;

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

    // Per-unit awareness config (set from UnitDef at spawn)
    public float DetectionRange;
    public float DetectionBreakRange;
    public float AlertDuration = 2f;
    public float AlertEscalateRange;
    public float GroupAlertRadius;

    // Potion effects
    public int PoisonStacks;
    public float PoisonTickTimer;
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
