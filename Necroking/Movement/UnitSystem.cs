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

/// <summary>
/// SoA unit storage — mirrors the C++ UnitArrays struct.
/// All parallel arrays indexed by unit index.
/// </summary>
public class UnitArrays
{
    // Hot path
    public List<Vec2> Position = new();
    public List<Vec2> Velocity = new();
    public List<Vec2> PreferredVel = new();

    // Movement
    public List<float> Radius = new();
    public List<float> MaxSpeed = new();
    public List<int> OrcaPriority = new();
    public List<Vec2> MoveTarget = new();

    // Identity
    public List<int> Size = new();
    public List<uint> Id = new();
    public List<UnitType> Type = new();
    public List<string> UnitDefID = new();
    public List<Faction> Faction = new();
    public List<AIBehavior> AI = new();

    // Combat
    public List<UnitStats> Stats = new();
    public List<float> Fatigue = new();
    public List<CombatTarget> Target = new();
    public List<float> RetargetTimer = new();

    // State
    public List<bool> Alive = new();
    public List<bool> InCombat = new();
    public List<float> FacingAngle = new();
    public List<float> AttackCooldown = new();
    public List<CombatTarget> PendingAttack = new();
    public List<float> HitShakeTimer = new();
    public List<uint> LastAttackerID = new();   // stable ID of last unit that hit us
    public List<float> FleeTimer = new();       // countdown for FleeWhenHit flee duration
    public List<byte> WolfPhase = new();        // 0=engage, 1=attacking, 2=disengage, 3=wait/cooldown
    public List<float> WolfPhaseTimer = new();  // timer for current wolf phase

    // Jumping
    public List<bool> Jumping = new();
    public List<float> JumpTimer = new();
    public List<float> JumpDuration = new();
    public List<Vec2> JumpStartPos = new();
    public List<Vec2> JumpEndPos = new();
    public List<bool> JumpIsAttack = new();
    public List<bool> JumpAttackFired = new();
    public List<float> JumpHeight = new();

    // Knockdown / Standup
    public List<float> KnockdownTimer = new();
    public List<float> StandupTimer = new();
    public List<int> Harassment = new();

    // Rendering
    public List<float> SpriteScale = new();
    public List<Vec2> EffectSpawnPos2D = new();
    public List<float> EffectSpawnHeight = new();

    // Buffs
    public List<List<ActiveBuff>> ActiveBuffs = new();

    // Corpse dragging
    public List<int> DraggingCorpseIdx = new();

    // Spawn/Raid/Patrol
    public List<int> SpawnBuildingIdx = new();
    public List<int> RaidTargetIdx = new();
    public List<int> PatrolRouteIdx = new();
    public List<int> PatrolWaypointIdx = new();

    // Caster
    public List<float> Mana = new();
    public List<float> MaxMana = new();
    public List<float> ManaRegen = new();
    public List<float> SpellCooldownTimer = new();
    public List<string> SpellID = new();

    // Engagement & combat state
    public List<CombatTarget> EngagedTarget = new();       // who this unit is fighting
    public List<float> PostAttackTimer = new();             // movement lockout countdown from attack initiation
    public List<float> MoveTime = new();                    // continuous movement time for acceleration curve
    public List<QueuedUnitAction> QueuedAction = new();     // action to execute when unit becomes free
    public List<float> AnimPlaybackSpeed = new();           // per-unit animation speed multiplier

    // Ghost mode
    public List<bool> GhostMode = new();

    // Dodge / React (set on combat events, cleared each tick)
    public List<bool> Dodging = new();
    public List<bool> HitReacting = new();     // set when unit takes damage — triggers HitReact anim
    public List<bool> BlockReacting = new();   // set when attack is blocked — triggers BlockReact anim

    // Stuck detection for ORCA nudge
    public List<int> StuckFrames = new();

    // AI behavior framework (replaces WolfPhase/FleeTimer for new archetypes)
    public List<byte> Archetype = new();        // ArchetypeRegistry ID (0=None=use legacy AI)
    public List<byte> Routine = new();           // current routine index (meaning varies by archetype)
    public List<byte> Subroutine = new();        // current step within routine
    public List<float> SubroutineTimer = new();  // generic timer for current step
    public List<byte> AlertState = new();        // 0=Unaware, 1=Alert, 2=Aggressive
    public List<float> AlertTimer = new();       // time in alert state
    public List<uint> AlertTarget = new();       // who triggered awareness
    public List<Vec2> SpawnPosition = new();     // where unit was spawned (used as roam center)
    public List<bool> IsSneaking = new();        // sneaking movement mode

    // Per-unit awareness config (set from UnitDef at spawn)
    public List<float> DetectionRange = new();
    public List<float> DetectionBreakRange = new();
    public List<float> AlertDuration = new();
    public List<float> AlertEscalateRange = new();
    public List<float> GroupAlertRadius = new();

    public int Count;
    private uint _nextID;

    public int AddUnit(Vec2 pos, UnitType type)
    {
        int idx = Count++;
        Position.Add(pos);
        Velocity.Add(Vec2.Zero);
        PreferredVel.Add(Vec2.Zero);
        Radius.Add(0.495f);
        MaxSpeed.Add(8f);
        OrcaPriority.Add(0);
        MoveTarget.Add(pos);
        Size.Add(2);
        Id.Add(_nextID++);
        Type.Add(type);
        UnitDefID.Add(type.ToString().ToLowerInvariant());
        Faction.Add(type <= UnitType.Abomination ? Data.Faction.Undead : Data.Faction.Human);
        AI.Add(AIBehavior.AttackClosest);
        Stats.Add(new UnitStats());
        Fatigue.Add(0f);
        Target.Add(CombatTarget.None);
        RetargetTimer.Add(0f);
        Alive.Add(true);
        InCombat.Add(false);
        FacingAngle.Add(90f);
        AttackCooldown.Add(0f);
        PendingAttack.Add(CombatTarget.None);
        HitShakeTimer.Add(0f);
        LastAttackerID.Add(GameConstants.InvalidUnit);
        FleeTimer.Add(0f);
        WolfPhase.Add(0);
        WolfPhaseTimer.Add(0f);
        Jumping.Add(false);
        JumpTimer.Add(0f);
        JumpDuration.Add(1f);
        JumpStartPos.Add(Vec2.Zero);
        JumpEndPos.Add(Vec2.Zero);
        JumpIsAttack.Add(false);
        JumpAttackFired.Add(false);
        JumpHeight.Add(0f);
        KnockdownTimer.Add(0f);
        StandupTimer.Add(0f);
        Harassment.Add(0);
        SpriteScale.Add(1f);
        EffectSpawnPos2D.Add(Vec2.Zero);
        EffectSpawnHeight.Add(0f);
        Dodging.Add(false);
        HitReacting.Add(false);
        BlockReacting.Add(false);
        StuckFrames.Add(0);
        ActiveBuffs.Add(new List<ActiveBuff>());
        DraggingCorpseIdx.Add(-1);
        SpawnBuildingIdx.Add(-1);
        RaidTargetIdx.Add(-1);
        PatrolRouteIdx.Add(-1);
        PatrolWaypointIdx.Add(0);
        Mana.Add(0f);
        MaxMana.Add(0f);
        ManaRegen.Add(0f);
        SpellCooldownTimer.Add(0f);
        SpellID.Add("");
        EngagedTarget.Add(CombatTarget.None);
        PostAttackTimer.Add(0f);
        MoveTime.Add(0f);
        QueuedAction.Add(QueuedUnitAction.None);
        AnimPlaybackSpeed.Add(1f);
        GhostMode.Add(false);
        Archetype.Add(0);
        Routine.Add(0);
        Subroutine.Add(0);
        SubroutineTimer.Add(0f);
        AlertState.Add(0);
        AlertTimer.Add(0f);
        AlertTarget.Add(GameConstants.InvalidUnit);
        SpawnPosition.Add(pos);
        IsSneaking.Add(false);
        DetectionRange.Add(0f);
        DetectionBreakRange.Add(0f);
        AlertDuration.Add(2f);
        AlertEscalateRange.Add(0f);
        GroupAlertRadius.Add(0f);
        return idx;
    }

    public void RemoveUnit(int index)
    {
        if (index < 0 || index >= Count) return;
        int last = Count - 1;
        if (index != last)
        {
            // Swap with last
            SwapAt(index, last);
        }
        // Pop last
        TrimLast();
        Count--;
    }

    private void SwapAt(int a, int b)
    {
        (Position[a], Position[b]) = (Position[b], Position[a]);
        (Velocity[a], Velocity[b]) = (Velocity[b], Velocity[a]);
        (PreferredVel[a], PreferredVel[b]) = (PreferredVel[b], PreferredVel[a]);
        (Radius[a], Radius[b]) = (Radius[b], Radius[a]);
        (MaxSpeed[a], MaxSpeed[b]) = (MaxSpeed[b], MaxSpeed[a]);
        (OrcaPriority[a], OrcaPriority[b]) = (OrcaPriority[b], OrcaPriority[a]);
        (MoveTarget[a], MoveTarget[b]) = (MoveTarget[b], MoveTarget[a]);
        (Size[a], Size[b]) = (Size[b], Size[a]);
        (Id[a], Id[b]) = (Id[b], Id[a]);
        (Type[a], Type[b]) = (Type[b], Type[a]);
        (UnitDefID[a], UnitDefID[b]) = (UnitDefID[b], UnitDefID[a]);
        (Faction[a], Faction[b]) = (Faction[b], Faction[a]);
        (AI[a], AI[b]) = (AI[b], AI[a]);
        (Stats[a], Stats[b]) = (Stats[b], Stats[a]);
        (Fatigue[a], Fatigue[b]) = (Fatigue[b], Fatigue[a]);
        (Target[a], Target[b]) = (Target[b], Target[a]);
        (RetargetTimer[a], RetargetTimer[b]) = (RetargetTimer[b], RetargetTimer[a]);
        (Alive[a], Alive[b]) = (Alive[b], Alive[a]);
        (InCombat[a], InCombat[b]) = (InCombat[b], InCombat[a]);
        (FacingAngle[a], FacingAngle[b]) = (FacingAngle[b], FacingAngle[a]);
        (AttackCooldown[a], AttackCooldown[b]) = (AttackCooldown[b], AttackCooldown[a]);
        (PendingAttack[a], PendingAttack[b]) = (PendingAttack[b], PendingAttack[a]);
        (HitShakeTimer[a], HitShakeTimer[b]) = (HitShakeTimer[b], HitShakeTimer[a]);
        (LastAttackerID[a], LastAttackerID[b]) = (LastAttackerID[b], LastAttackerID[a]);
        (FleeTimer[a], FleeTimer[b]) = (FleeTimer[b], FleeTimer[a]);
        (WolfPhase[a], WolfPhase[b]) = (WolfPhase[b], WolfPhase[a]);
        (WolfPhaseTimer[a], WolfPhaseTimer[b]) = (WolfPhaseTimer[b], WolfPhaseTimer[a]);
        (Jumping[a], Jumping[b]) = (Jumping[b], Jumping[a]);
        (JumpTimer[a], JumpTimer[b]) = (JumpTimer[b], JumpTimer[a]);
        (JumpDuration[a], JumpDuration[b]) = (JumpDuration[b], JumpDuration[a]);
        (JumpStartPos[a], JumpStartPos[b]) = (JumpStartPos[b], JumpStartPos[a]);
        (JumpEndPos[a], JumpEndPos[b]) = (JumpEndPos[b], JumpEndPos[a]);
        (JumpIsAttack[a], JumpIsAttack[b]) = (JumpIsAttack[b], JumpIsAttack[a]);
        (JumpAttackFired[a], JumpAttackFired[b]) = (JumpAttackFired[b], JumpAttackFired[a]);
        (JumpHeight[a], JumpHeight[b]) = (JumpHeight[b], JumpHeight[a]);
        (KnockdownTimer[a], KnockdownTimer[b]) = (KnockdownTimer[b], KnockdownTimer[a]);
        (StandupTimer[a], StandupTimer[b]) = (StandupTimer[b], StandupTimer[a]);
        (Harassment[a], Harassment[b]) = (Harassment[b], Harassment[a]);
        (SpriteScale[a], SpriteScale[b]) = (SpriteScale[b], SpriteScale[a]);
        (EffectSpawnPos2D[a], EffectSpawnPos2D[b]) = (EffectSpawnPos2D[b], EffectSpawnPos2D[a]);
        (EffectSpawnHeight[a], EffectSpawnHeight[b]) = (EffectSpawnHeight[b], EffectSpawnHeight[a]);
        (Dodging[a], Dodging[b]) = (Dodging[b], Dodging[a]);
        (HitReacting[a], HitReacting[b]) = (HitReacting[b], HitReacting[a]);
        (BlockReacting[a], BlockReacting[b]) = (BlockReacting[b], BlockReacting[a]);
        (StuckFrames[a], StuckFrames[b]) = (StuckFrames[b], StuckFrames[a]);
        (ActiveBuffs[a], ActiveBuffs[b]) = (ActiveBuffs[b], ActiveBuffs[a]);
        (DraggingCorpseIdx[a], DraggingCorpseIdx[b]) = (DraggingCorpseIdx[b], DraggingCorpseIdx[a]);
        (SpawnBuildingIdx[a], SpawnBuildingIdx[b]) = (SpawnBuildingIdx[b], SpawnBuildingIdx[a]);
        (RaidTargetIdx[a], RaidTargetIdx[b]) = (RaidTargetIdx[b], RaidTargetIdx[a]);
        (PatrolRouteIdx[a], PatrolRouteIdx[b]) = (PatrolRouteIdx[b], PatrolRouteIdx[a]);
        (PatrolWaypointIdx[a], PatrolWaypointIdx[b]) = (PatrolWaypointIdx[b], PatrolWaypointIdx[a]);
        (Mana[a], Mana[b]) = (Mana[b], Mana[a]);
        (MaxMana[a], MaxMana[b]) = (MaxMana[b], MaxMana[a]);
        (ManaRegen[a], ManaRegen[b]) = (ManaRegen[b], ManaRegen[a]);
        (SpellCooldownTimer[a], SpellCooldownTimer[b]) = (SpellCooldownTimer[b], SpellCooldownTimer[a]);
        (SpellID[a], SpellID[b]) = (SpellID[b], SpellID[a]);
        (EngagedTarget[a], EngagedTarget[b]) = (EngagedTarget[b], EngagedTarget[a]);
        (PostAttackTimer[a], PostAttackTimer[b]) = (PostAttackTimer[b], PostAttackTimer[a]);
        (MoveTime[a], MoveTime[b]) = (MoveTime[b], MoveTime[a]);
        (QueuedAction[a], QueuedAction[b]) = (QueuedAction[b], QueuedAction[a]);
        (AnimPlaybackSpeed[a], AnimPlaybackSpeed[b]) = (AnimPlaybackSpeed[b], AnimPlaybackSpeed[a]);
        (GhostMode[a], GhostMode[b]) = (GhostMode[b], GhostMode[a]);
        (Archetype[a], Archetype[b]) = (Archetype[b], Archetype[a]);
        (Routine[a], Routine[b]) = (Routine[b], Routine[a]);
        (Subroutine[a], Subroutine[b]) = (Subroutine[b], Subroutine[a]);
        (SubroutineTimer[a], SubroutineTimer[b]) = (SubroutineTimer[b], SubroutineTimer[a]);
        (AlertState[a], AlertState[b]) = (AlertState[b], AlertState[a]);
        (AlertTimer[a], AlertTimer[b]) = (AlertTimer[b], AlertTimer[a]);
        (AlertTarget[a], AlertTarget[b]) = (AlertTarget[b], AlertTarget[a]);
        (SpawnPosition[a], SpawnPosition[b]) = (SpawnPosition[b], SpawnPosition[a]);
        (IsSneaking[a], IsSneaking[b]) = (IsSneaking[b], IsSneaking[a]);
        (DetectionRange[a], DetectionRange[b]) = (DetectionRange[b], DetectionRange[a]);
        (DetectionBreakRange[a], DetectionBreakRange[b]) = (DetectionBreakRange[b], DetectionBreakRange[a]);
        (AlertDuration[a], AlertDuration[b]) = (AlertDuration[b], AlertDuration[a]);
        (AlertEscalateRange[a], AlertEscalateRange[b]) = (AlertEscalateRange[b], AlertEscalateRange[a]);
        (GroupAlertRadius[a], GroupAlertRadius[b]) = (GroupAlertRadius[b], GroupAlertRadius[a]);
    }

    private void TrimLast()
    {
        int last = Position.Count - 1;
        Position.RemoveAt(last); Velocity.RemoveAt(last); PreferredVel.RemoveAt(last);
        Radius.RemoveAt(last); MaxSpeed.RemoveAt(last); OrcaPriority.RemoveAt(last);
        MoveTarget.RemoveAt(last); Size.RemoveAt(last); Id.RemoveAt(last);
        Type.RemoveAt(last); UnitDefID.RemoveAt(last); Faction.RemoveAt(last);
        AI.RemoveAt(last); Stats.RemoveAt(last); Fatigue.RemoveAt(last);
        Target.RemoveAt(last); RetargetTimer.RemoveAt(last); Alive.RemoveAt(last);
        InCombat.RemoveAt(last); FacingAngle.RemoveAt(last); AttackCooldown.RemoveAt(last);
        PendingAttack.RemoveAt(last); HitShakeTimer.RemoveAt(last);
        LastAttackerID.RemoveAt(last); FleeTimer.RemoveAt(last);
        WolfPhase.RemoveAt(last); WolfPhaseTimer.RemoveAt(last);
        Jumping.RemoveAt(last); JumpTimer.RemoveAt(last); JumpDuration.RemoveAt(last);
        JumpStartPos.RemoveAt(last); JumpEndPos.RemoveAt(last);
        JumpIsAttack.RemoveAt(last); JumpAttackFired.RemoveAt(last); JumpHeight.RemoveAt(last);
        KnockdownTimer.RemoveAt(last); StandupTimer.RemoveAt(last);
        Harassment.RemoveAt(last); SpriteScale.RemoveAt(last);
        EffectSpawnPos2D.RemoveAt(last); EffectSpawnHeight.RemoveAt(last);
        Dodging.RemoveAt(last); HitReacting.RemoveAt(last);
        BlockReacting.RemoveAt(last); StuckFrames.RemoveAt(last);
        ActiveBuffs.RemoveAt(last); DraggingCorpseIdx.RemoveAt(last);
        SpawnBuildingIdx.RemoveAt(last); RaidTargetIdx.RemoveAt(last);
        PatrolRouteIdx.RemoveAt(last); PatrolWaypointIdx.RemoveAt(last);
        Mana.RemoveAt(last); MaxMana.RemoveAt(last); ManaRegen.RemoveAt(last);
        SpellCooldownTimer.RemoveAt(last); SpellID.RemoveAt(last);
        EngagedTarget.RemoveAt(last); PostAttackTimer.RemoveAt(last);
        MoveTime.RemoveAt(last); QueuedAction.RemoveAt(last);
        AnimPlaybackSpeed.RemoveAt(last);
        GhostMode.RemoveAt(last);
        Archetype.RemoveAt(last); Routine.RemoveAt(last); Subroutine.RemoveAt(last);
        SubroutineTimer.RemoveAt(last); AlertState.RemoveAt(last); AlertTimer.RemoveAt(last);
        AlertTarget.RemoveAt(last); SpawnPosition.RemoveAt(last); IsSneaking.RemoveAt(last);
        DetectionRange.RemoveAt(last); DetectionBreakRange.RemoveAt(last);
        AlertDuration.RemoveAt(last); AlertEscalateRange.RemoveAt(last);
        GroupAlertRadius.RemoveAt(last);
    }

    public void Clear()
    {
        Count = 0;
        Position.Clear(); Velocity.Clear(); PreferredVel.Clear();
        Radius.Clear(); MaxSpeed.Clear(); OrcaPriority.Clear();
        MoveTarget.Clear(); Size.Clear(); Id.Clear();
        Type.Clear(); UnitDefID.Clear(); Faction.Clear();
        AI.Clear(); Stats.Clear(); Fatigue.Clear();
        Target.Clear(); RetargetTimer.Clear(); Alive.Clear();
        InCombat.Clear(); FacingAngle.Clear(); AttackCooldown.Clear();
        PendingAttack.Clear(); HitShakeTimer.Clear();
        LastAttackerID.Clear(); FleeTimer.Clear();
        WolfPhase.Clear(); WolfPhaseTimer.Clear();
        Jumping.Clear(); JumpTimer.Clear(); JumpDuration.Clear();
        JumpStartPos.Clear(); JumpEndPos.Clear();
        JumpIsAttack.Clear(); JumpAttackFired.Clear(); JumpHeight.Clear();
        KnockdownTimer.Clear(); StandupTimer.Clear();
        Harassment.Clear(); SpriteScale.Clear();
        EffectSpawnPos2D.Clear(); EffectSpawnHeight.Clear();
        Dodging.Clear(); StuckFrames.Clear();
        ActiveBuffs.Clear(); DraggingCorpseIdx.Clear();
        SpawnBuildingIdx.Clear(); RaidTargetIdx.Clear();
        PatrolRouteIdx.Clear(); PatrolWaypointIdx.Clear();
        Mana.Clear(); MaxMana.Clear(); ManaRegen.Clear();
        SpellCooldownTimer.Clear(); SpellID.Clear();
        GhostMode.Clear();
        Archetype.Clear(); Routine.Clear(); Subroutine.Clear();
        SubroutineTimer.Clear(); AlertState.Clear(); AlertTimer.Clear();
        AlertTarget.Clear(); SpawnPosition.Clear(); IsSneaking.Clear();
        DetectionRange.Clear(); DetectionBreakRange.Clear();
        AlertDuration.Clear(); AlertEscalateRange.Clear(); GroupAlertRadius.Clear();
    }
}

public static class UnitUtil
{
    public static int ResolveUnitIndex(UnitArrays units, uint uid)
    {
        if (uid == GameConstants.InvalidUnit) return -1;
        for (int i = 0; i < units.Count; i++)
            if (units.Id[i] == uid && units.Alive[i]) return i;
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
