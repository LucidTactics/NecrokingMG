using System;
using Necroking.Core;
using Necroking.Movement;
using Necroking.Render;

namespace Necroking.AI;

/// <summary>
/// Deer herd AI archetype: prey animal with alert/flee/feeding behavior.
///
/// Behavior varies by sex (determined by UnitDef — FemaleDeer vs MaleDeer):
///   Female: always flees from threats, never fights
///   Male: fights back when hit. Stands facing attacker between attacks, charges when ready.
///
/// Routines:
///   0 = IdleRoaming  — walk to a point at 30% speed, idle for a while, repeat within 10u of spawn
///   1 = Sleeping     — nighttime: stand still
///   2 = Alert        — freeze and watch threat; recheck every second
///   3 = Fleeing      — run away from threat
///   4 = Calming      — threat gone, gradually return to idle behavior
///   5 = FightBack    — male only: charge when ready, stand and face between attacks
///   6 = Feeding      — walk to bush, play feed animation, idle, repeat
///
/// Alert behavior (reworked):
///   - On detection: freeze, face threat (Alert routine, Watch subroutine)
///   - Every 1 second: if ANY hostile within 90% of alert radius → flee (or fight if male)
///   - If no hostile within 90%: stay frozen, keep watching
///   - If alert drops to Unaware: enter Calming then back to Idle
/// </summary>
public class DeerHerdHandler : IArchetypeHandler
{
    private const byte RoutineIdleRoaming = 0;
    private const byte RoutineSleeping = 1;
    private const byte RoutineAlert = 2;
    private const byte RoutineFleeing = 3;
    private const byte RoutineCalming = 4;
    private const byte RoutineFightBack = 5;
    private const byte RoutineFeeding = 6;

    // Alert subroutines
    private const byte AlertWatch = 0;
    private const byte AlertRun = 1;

    // Fighting subroutines (male only)
    private const byte FightStance = 0;  // stand facing attacker, wait for cooldown
    private const byte FightCharge = 1;  // charge in and strike

    // Sleeping subroutines
    private const byte SleepSitting = 0;    // Play sit animation, hold
    private const byte SleepAsleep = 1;     // Play sleep animation, hold
    private const byte SleepWaking = 2;     // Standup animation playing

    // Feeding subroutines
    private const byte FeedWalkToBush = 0;
    private const byte FeedEating = 1;
    private const byte FeedIdleAfter = 2;

    private const float SitDuration = 10f;          // Seconds in sit before sleep
    private const float SleepDetectionScale = 0.6f;  // 40% reduction in alert radius
    private const float StandupDuration = 1.0f;      // Standup animation time

    private const float RoamRadius = 10f;
    private const float FleeDistance = 20f;
    private const float CalmDuration = 3f;
    private const float MinFleeDuration = 4f; // Minimum flee time after damage (prevents instant calming)
    private const float AlertRecheckInterval = 1f;
    private const float AlertThresholdFraction = 0.9f;
    private const float FeedDuration = 4f;
    private const float FeedIdleDuration = 2f;
    private const float BushSearchRadius = 20f;
    private const float FeedingChance = 0.3f; // 30% chance to feed instead of roam

    public void OnSpawn(ref AIContext ctx)
    {
        ctx.Units[ctx.UnitIndex].SpawnPosition = ctx.MyPos;
        ctx.Routine = ctx.IsNight ? RoutineSleeping : RoutineIdleRoaming;
        ctx.Subroutine = 0;
        ctx.SubroutineTimer = 0f;
    }

    public void Update(ref AIContext ctx)
    {
        EvaluateRoutine(ref ctx);

        switch (ctx.Routine)
        {
            case RoutineIdleRoaming: UpdateIdleRoaming(ref ctx); break;
            case RoutineSleeping:    UpdateSleeping(ref ctx); break;
            case RoutineAlert:       UpdateAlert(ref ctx); break;
            case RoutineFleeing:     UpdateFleeing(ref ctx); break;
            case RoutineCalming:     UpdateCalming(ref ctx); break;
            case RoutineFightBack:   UpdateFightBack(ref ctx); break;
            case RoutineFeeding:     UpdateFeeding(ref ctx); break;
        }
    }

    private void EvaluateRoutine(ref AIContext ctx)
    {
        byte alert = ctx.AlertState;
        bool isMale = IsMale(ref ctx);

        // Log deer state every 60 frames (~1s) for debugging
        if (ctx.FrameNumber % 60 == 0)
        {
            var u = ctx.Units[ctx.UnitIndex];
            DebugLog.Log("ai", $"[Deer#{ctx.UnitIndex}] routine={GetRoutineName(ctx.Routine)} " +
                $"hitReact={u.HitReacting} poison={u.PoisonStacks} HP={u.Stats.HP} " +
                $"alert={ctx.AlertState} vel={u.Velocity.Length():F2} prefVel={u.PreferredVel.Length():F2} " +
                $"fleeT={ctx.SubroutineTimer:F1} pos=({u.Position.X:F1},{u.Position.Y:F1})");
        }

        // Spooked: took damage (melee or poison) → flee from any non-flee routine
        if (ctx.Units[ctx.UnitIndex].HitReacting
            && ctx.Routine != RoutineFleeing)
        {
            DebugLog.Log("ai", $"[Deer#{ctx.UnitIndex}] HitReacting! routine={GetRoutineName(ctx.Routine)} → fleeing");

            // If sleeping, need standup first
            if (ctx.Routine == RoutineSleeping && ctx.Subroutine <= SleepAsleep)
            {
                DebugLog.Log("ai", $"[Deer#{ctx.UnitIndex}] sleeping, starting wakeup first");
                ctx.Subroutine = SleepWaking;
                ctx.SubroutineTimer = StandupDuration;
                ctx.Units[ctx.UnitIndex].StandupTimer = StandupDuration;
                RestoreDetectionRange(ref ctx);
                return;
            }
            if (ctx.Routine == RoutineSleeping && ctx.Subroutine == SleepWaking)
                return;

            // Check for nearby enemy to flee from
            float detRange = ctx.Units[ctx.UnitIndex].DetectionRange;
            int enemyIdx = SubroutineSteps.FindClosestEnemy(ref ctx, detRange);
            if (enemyIdx >= 0)
            {
                DebugLog.Log("ai", $"[Deer#{ctx.UnitIndex}] fleeing from enemy idx={enemyIdx} at ({ctx.Units[enemyIdx].Position.X:F1},{ctx.Units[enemyIdx].Position.Y:F1})");
                // Flee away from the enemy
                SubroutineSteps.SetFleeFromTarget(ref ctx, ctx.Units[enemyIdx].Position, 10f);
                ctx.AlertTarget = ctx.Units[enemyIdx].Id;
            }
            else
            {
                DebugLog.Log("ai", $"[Deer#{ctx.UnitIndex}] no enemy in range {detRange:F1}, fleeing random direction");
                // No visible enemy — flee in random direction
                SubroutineSteps.SetFleeRandomTarget(ref ctx, 10f);
            }
            ctx.Routine = RoutineFleeing;
            ctx.Subroutine = 0;
            ctx.SubroutineTimer = MinFleeDuration; // Minimum flee time before calming allowed
            ctx.AlertState = (byte)UnitAlertState.Alert;
            return;
        }

        // Alert detected → enter Alert routine (from idle/sleeping/feeding)
        if (alert >= (byte)UnitAlertState.Alert &&
            (ctx.Routine <= RoutineSleeping || ctx.Routine == RoutineFeeding))
        {
            // If sleeping or sitting, need to standup first
            if (ctx.Routine == RoutineSleeping && ctx.Subroutine <= SleepAsleep)
            {
                // Face the threat before standing up
                if (ctx.AlertTarget != GameConstants.InvalidUnit)
                {
                    int threatIdx = UnitUtil.ResolveUnitIndex(ctx.Units, ctx.AlertTarget);
                    if (threatIdx >= 0)
                        SubroutineSteps.FacePosition(ref ctx, ctx.Units[threatIdx].Position);
                }
                ctx.Subroutine = SleepWaking;
                ctx.SubroutineTimer = StandupDuration;
                ctx.Units[ctx.UnitIndex].StandupTimer = StandupDuration;
                // Restore detection range
                RestoreDetectionRange(ref ctx);
                return;
            }
            // If already waking (standup playing), wait for it to finish
            if (ctx.Routine == RoutineSleeping && ctx.Subroutine == SleepWaking)
                return;

            ctx.Routine = RoutineAlert;
            ctx.Subroutine = AlertWatch;
            ctx.SubroutineTimer = 0f;
            return;
        }

        // Threat gone while alert/fleeing → calm down (but respect minimum flee time)
        if (alert == (byte)UnitAlertState.Unaware)
        {
            if (ctx.Routine == RoutineAlert)
            {
                ctx.Routine = RoutineCalming;
                ctx.SubroutineTimer = CalmDuration;
                ctx.Units[ctx.UnitIndex].Target = CombatTarget.None;
                ctx.Units[ctx.UnitIndex].EngagedTarget = CombatTarget.None;
                return;
            }
            if (ctx.Routine == RoutineFleeing && ctx.SubroutineTimer <= 0f)
            {
                ctx.Routine = RoutineCalming;
                ctx.SubroutineTimer = CalmDuration;
                ctx.Units[ctx.UnitIndex].Target = CombatTarget.None;
                ctx.Units[ctx.UnitIndex].EngagedTarget = CombatTarget.None;
                return;
            }
            if (ctx.Routine == RoutineFightBack && !SubroutineSteps.IsTargetAlive(ref ctx))
            {
                ctx.Routine = RoutineCalming;
                ctx.SubroutineTimer = CalmDuration;
                ctx.Units[ctx.UnitIndex].Target = CombatTarget.None;
                ctx.Units[ctx.UnitIndex].EngagedTarget = CombatTarget.None;
                return;
            }
        }

        // Male deer: if fleeing but actually engaged in combat, fight back
        if (isMale && ctx.Routine == RoutineFleeing && ctx.Units[ctx.UnitIndex].InCombat)
        {
            if (ShouldFightBack(ref ctx))
            {
                ctx.Routine = RoutineFightBack;
                ctx.Subroutine = FightStance;
                ctx.SubroutineTimer = 0f;
                ctx.Units[ctx.UnitIndex].Target = CombatTarget.Unit(ctx.AlertTarget);
                return;
            }
        }

        // Time of day for idle routines
        if (ctx.Routine <= RoutineSleeping)
        {
            byte target = ctx.IsNight ? RoutineSleeping : RoutineIdleRoaming;
            if (ctx.Routine != target)
            {
                // Waking from sleep — need standup animation first
                if (ctx.Routine == RoutineSleeping && ctx.Subroutine <= SleepAsleep && !ctx.IsNight)
                {
                    ctx.Subroutine = SleepWaking;
                    ctx.SubroutineTimer = StandupDuration;
                    ctx.Units[ctx.UnitIndex].StandupTimer = StandupDuration;
                    RestoreDetectionRange(ref ctx);
                    return;
                }
                // Don't switch while standup is playing
                if (ctx.Routine == RoutineSleeping && ctx.Subroutine == SleepWaking)
                    return;

                ctx.Routine = target;
                ctx.Subroutine = 0;
                ctx.SubroutineTimer = 0f;
            }
        }

        // Fight target died or left
        if (ctx.Routine == RoutineFightBack)
        {
            if (!SubroutineSteps.IsTargetAlive(ref ctx))
            {
                ctx.Units[ctx.UnitIndex].Target = CombatTarget.None;
                ctx.Units[ctx.UnitIndex].EngagedTarget = CombatTarget.None;
                ctx.AlertState = (byte)UnitAlertState.Unaware;
                ctx.AlertTarget = GameConstants.InvalidUnit;
                SwitchToTimeOfDayRoutine(ref ctx);
                return;
            }
            if (ctx.AlertState == (byte)UnitAlertState.Unaware)
            {
                ctx.Units[ctx.UnitIndex].Target = CombatTarget.None;
                ctx.Units[ctx.UnitIndex].EngagedTarget = CombatTarget.None;
                SwitchToTimeOfDayRoutine(ref ctx);
                return;
            }
        }
    }

    private static void SwitchToTimeOfDayRoutine(ref AIContext ctx)
    {
        byte target = ctx.IsNight ? RoutineSleeping : RoutineIdleRoaming;
        if (ctx.Routine != target)
        {
            ctx.Routine = target;
            ctx.Subroutine = 0;
            ctx.SubroutineTimer = 0f;
        }
    }

    private static bool IsMale(ref AIContext ctx)
    {
        string defId = ctx.Units[ctx.UnitIndex].UnitDefID ?? "";
        return defId.Contains("Male", StringComparison.OrdinalIgnoreCase)
            && !defId.Contains("Female", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Check if any hostile is within a fraction of the detection range.</summary>
    private static bool AnyHostileWithinThreshold(ref AIContext ctx)
    {
        float detRange = ctx.Units[ctx.UnitIndex].DetectionRange;
        float threshold = detRange * AlertThresholdFraction;
        float threshSq = threshold * threshold;
        var myFaction = ctx.MyFaction;
        for (int j = 0; j < ctx.Units.Count; j++)
        {
            if (!ctx.Units[j].Alive || ctx.Units[j].Faction == myFaction) continue;
            if ((ctx.Units[j].Position - ctx.MyPos).LengthSq() < threshSq)
                return true;
        }
        return false;
    }

    /// <summary>Male fights if threat is alone (no other enemies nearby).</summary>
    private static bool ShouldFightBack(ref AIContext ctx)
    {
        int threatCount = 0;
        var myFaction = ctx.MyFaction;
        for (int j = 0; j < ctx.Units.Count; j++)
        {
            if (!ctx.Units[j].Alive || ctx.Units[j].Faction == myFaction) continue;
            if ((ctx.Units[j].Position - ctx.MyPos).LengthSq() < 15f * 15f)
                threatCount++;
        }
        return threatCount <= 1;
    }

    // ═══════════════════════════════════════
    //  Routine: Idle & Roaming (with 30% feeding chance)
    // ═══════════════════════════════════════

    private static void UpdateIdleRoaming(ref AIContext ctx)
    {
        byte prevSub = ctx.Subroutine;
        SubroutineSteps.IdleRoam(ref ctx, RoamRadius);

        // When IdleRoam just transitioned from idle (1) to walking (0),
        // 30% chance to feed at a bush instead
        if (prevSub == 1 && ctx.Subroutine == 0)
        {
            int roll = (ctx.FrameNumber + ctx.UnitIndex * 7) % 100;
            if (roll < (int)(FeedingChance * 100))
                TryStartFeeding(ref ctx);
        }
    }

    // ═══════════════════════════════════════
    //  Routine: Sleeping (Sit → Sleep → Wake)
    // ═══════════════════════════════════════

    private static void UpdateSleeping(ref AIContext ctx)
    {
        ctx.Units[ctx.UnitIndex].PreferredVel = Vec2.Zero;

        switch (ctx.Subroutine)
        {
            case SleepSitting:
                // Sit animation is PlayOnceHold — it plays and holds on last frame
                ctx.Units[ctx.UnitIndex].RoutineAnim = AnimRequest.Action(AnimState.Sit);
                ctx.SubroutineTimer += ctx.Dt;
                if (ctx.SubroutineTimer >= SitDuration)
                {
                    ctx.Subroutine = SleepAsleep;
                    ctx.SubroutineTimer = 0f;
                    // Reduce detection range while sleeping
                    ReduceDetectionRange(ref ctx);
                }
                break;

            case SleepAsleep:
                // Sleep animation is PlayOnceHold — plays and holds on last frame
                ctx.Units[ctx.UnitIndex].RoutineAnim = AnimRequest.Action(AnimState.Sleep);
                break;

            case SleepWaking:
                // Standup animation — override since it interrupts sleep
                ctx.Units[ctx.UnitIndex].OverrideAnim = AnimRequest.Combat(AnimState.Standup);
                ctx.SubroutineTimer -= ctx.Dt;
                if (ctx.SubroutineTimer <= 0f)
                {
                    // Standup complete — transition to alert if threat present, else time-of-day routine
                    if (ctx.AlertState >= (byte)UnitAlertState.Alert)
                    {
                        ctx.Routine = RoutineAlert;
                        ctx.Subroutine = AlertWatch;
                        ctx.SubroutineTimer = 0f;
                    }
                    else
                    {
                        SwitchToTimeOfDayRoutine(ref ctx);
                    }
                }
                break;
        }
    }

    private static void ReduceDetectionRange(ref AIContext ctx)
    {
        ctx.Units[ctx.UnitIndex].DetectionRange *= SleepDetectionScale;
    }

    private static void RestoreDetectionRange(ref AIContext ctx)
    {
        // Restore from UnitDef
        if (ctx.GameData != null)
        {
            var def = ctx.GameData.Units.Get(ctx.Units[ctx.UnitIndex].UnitDefID);
            if (def != null)
                ctx.Units[ctx.UnitIndex].DetectionRange = def.DetectionRange;
        }
    }

    // ═══════════════════════════════════════
    //  Routine: Alert (Watch + escalation)
    // ═══════════════════════════════════════

    private void UpdateAlert(ref AIContext ctx)
    {
        bool isMale = IsMale(ref ctx);

        // Face the threat
        SubroutineSteps.AlertStance(ref ctx);

        ctx.SubroutineTimer += ctx.Dt;

        // Recheck every 1 second
        if (ctx.SubroutineTimer >= AlertRecheckInterval)
        {
            ctx.SubroutineTimer = 0f;

            if (AnyHostileWithinThreshold(ref ctx))
            {
                // Threat is close — escalate (always flee, FightBack kept for future use)
                {
                    ctx.Routine = RoutineFleeing;
                    ctx.Subroutine = 0;
                    ctx.SubroutineTimer = 0f;
                    // Propagate flee to nearby herd
                    PropagateFleeToHerd(ref ctx);
                    return;
                }
            }
            // else: no hostile close enough, keep watching
        }
    }

    private static void PropagateFleeToHerd(ref AIContext ctx)
    {
        float herdRadius = ctx.Units[ctx.UnitIndex].GroupAlertRadius;
        if (herdRadius <= 0f) herdRadius = 15f;
        float herdRadiusSq = herdRadius * herdRadius;

        for (int j = 0; j < ctx.Units.Count; j++)
        {
            if (j == ctx.UnitIndex || !ctx.Units[j].Alive) continue;
            if (ctx.Units[j].Faction != ctx.MyFaction) continue;
            if (ctx.Units[j].Archetype != ArchetypeRegistry.DeerHerd) continue;
            if ((ctx.Units[j].Position - ctx.MyPos).LengthSq() > herdRadiusSq) continue;

            // Only escalate deer that aren't already fleeing/fighting
            byte r = ctx.Units[j].Routine;
            if (r == RoutineFleeing || r == RoutineFightBack) continue;

            ctx.Units[j].Routine = RoutineFleeing;
            ctx.Units[j].Subroutine = 0;
            ctx.Units[j].SubroutineTimer = 0f;
            ctx.Units[j].AlertTarget = ctx.AlertTarget;
            ctx.Units[j].AlertState = (byte)UnitAlertState.Aggressive;
        }
    }

    // ═══════════════════════════════════════
    //  Routine: Fleeing
    // ═══════════════════════════════════════

    private static void UpdateFleeing(ref AIContext ctx)
    {
        // Tick down minimum flee timer
        if (ctx.SubroutineTimer > 0f)
            ctx.SubroutineTimer -= ctx.Dt;

        int threatIdx = SubroutineSteps.ResolveAlertTarget(ref ctx);
        if (threatIdx >= 0)
        {
            Vec2 threatPos = ctx.Units[threatIdx].Position;
            Vec2 awayDir = ctx.MyPos - threatPos;
            float dist = awayDir.Length();
            if (dist > 0.01f) awayDir *= 1f / dist;
            else awayDir = new Vec2(1, 0);

            Vec2 fleeDest = ctx.MyPos + awayDir * FleeDistance;
            SubroutineSteps.MoveToward(ref ctx, fleeDest, ctx.MySpeed);
        }
        else if (ctx.SubroutineTimer > 0f)
        {
            // No visible threat but still in minimum flee time (e.g. poison damage) —
            // keep running in current facing direction
            float fRad = ctx.Units[ctx.UnitIndex].FacingAngle * MathF.PI / 180f;
            Vec2 fleeDir = new Vec2(MathF.Cos(fRad), MathF.Sin(fRad));
            Vec2 fleeDest = ctx.MyPos + fleeDir * FleeDistance;
            SubroutineSteps.MoveToward(ref ctx, fleeDest, ctx.MySpeed);
        }
        else
        {
            SubroutineSteps.SetIdle(ref ctx);
        }
    }

    // ═══════════════════════════════════════
    //  Routine: Calming
    // ═══════════════════════════════════════

    private static void UpdateCalming(ref AIContext ctx)
    {
        SubroutineSteps.SetIdle(ref ctx);
        ctx.SubroutineTimer -= ctx.Dt;
        if (ctx.SubroutineTimer <= 0f)
        {
            ctx.Routine = ctx.IsNight ? RoutineSleeping : RoutineIdleRoaming;
            ctx.Subroutine = 0;
            ctx.SubroutineTimer = 0f;
        }
    }

    // ═══════════════════════════════════════
    //  Routine: FightBack (male only — stance + charge)
    // ═══════════════════════════════════════

    private static void UpdateFightBack(ref AIContext ctx)
    {
        if (!SubroutineSteps.IsTargetAlive(ref ctx))
        {
            ctx.Routine = RoutineCalming;
            ctx.SubroutineTimer = CalmDuration;
            return;
        }

        int targetIdx = SubroutineSteps.ResolveTarget(ref ctx);

        switch (ctx.Subroutine)
        {
            case FightStance:
            {
                // Stand still facing attacker, wait for attack cooldown
                if (targetIdx >= 0)
                {
                    // Face the target but don't move
                    var dir = ctx.Units[targetIdx].Position - ctx.MyPos;
                    if (dir.LengthSq() > 0.01f)
                    {
                        float angle = MathF.Atan2(dir.Y, dir.X) * (180f / MathF.PI);
                        ctx.Units[ctx.UnitIndex].FacingAngle = angle;
                    }
                }
                SubroutineSteps.SetIdle(ref ctx);

                // When attack is ready, charge
                if (ctx.Units[ctx.UnitIndex].AttackCooldown <= 0f &&
                    ctx.Units[ctx.UnitIndex].PostAttackTimer <= 0f)
                {
                    ctx.Subroutine = FightCharge;
                    ctx.SubroutineTimer = 0f;
                }
                break;
            }

            case FightCharge:
            {
                // Charge toward target
                if (targetIdx >= 0)
                {
                    float range = SubroutineSteps.GetMeleeRange(ref ctx, targetIdx);
                    float dist = (ctx.Units[targetIdx].Position - ctx.MyPos).Length();

                    if (dist <= range)
                    {
                        // In range — attack and go back to stance
                        SubroutineSteps.AttackTarget(ref ctx);

                        // Once attack fires (cooldown starts), return to stance
                        if (ctx.Units[ctx.UnitIndex].AttackCooldown > 0f)
                        {
                            ctx.Subroutine = FightStance;
                            ctx.SubroutineTimer = 0f;
                            ctx.Units[ctx.UnitIndex].EngagedTarget = CombatTarget.None;
                        }
                    }
                    else
                    {
                        // Still closing distance
                        SubroutineSteps.MoveToward(ref ctx, ctx.Units[targetIdx].Position, ctx.MySpeed);
                    }
                }
                break;
            }
        }
    }

    // ═══════════════════════════════════════
    //  Routine: Feeding (walk to bush, eat, idle, repeat)
    // ═══════════════════════════════════════

    /// <summary>Try to find a nearby bush and start feeding. Returns true if started.</summary>
    private static bool TryStartFeeding(ref AIContext ctx)
    {
        Vec2 bushPos;
        if (!FindNearbyBush(ref ctx, out bushPos))
            return false;

        ctx.Routine = RoutineFeeding;
        ctx.Subroutine = FeedWalkToBush;
        ctx.SubroutineTimer = 0f;
        ctx.Units[ctx.UnitIndex].MoveTarget = bushPos;
        return true;
    }

    private static void UpdateFeeding(ref AIContext ctx)
    {
        switch (ctx.Subroutine)
        {
            case FeedWalkToBush:
            {
                Vec2 target = ctx.Units[ctx.UnitIndex].MoveTarget;
                float dist = (ctx.MyPos - target).Length();
                if (dist > 1.5f)
                {
                    SubroutineSteps.MoveToward(ref ctx, target, ctx.MySpeed * 0.3f);
                }
                else
                {
                    // Arrived at bush — start eating
                    ctx.Subroutine = FeedEating;
                    ctx.SubroutineTimer = FeedDuration;
                    ctx.Units[ctx.UnitIndex].PreferredVel = Vec2.Zero;
                }
                break;
            }

            case FeedEating:
            {
                // Stand still facing the bush, playing feed animation
                ctx.Units[ctx.UnitIndex].PreferredVel = Vec2.Zero;
                ctx.Units[ctx.UnitIndex].RoutineAnim = AnimRequest.Action(AnimState.Feeding);
                // Face toward the bush target
                Vec2 toBush = ctx.Units[ctx.UnitIndex].MoveTarget - ctx.MyPos;
                if (toBush.LengthSq() > 0.01f)
                    ctx.Units[ctx.UnitIndex].FacingAngle = MathF.Atan2(toBush.Y, toBush.X) * (180f / MathF.PI);
                ctx.SubroutineTimer -= ctx.Dt;
                if (ctx.SubroutineTimer <= 0f)
                {
                    ctx.Subroutine = FeedIdleAfter;
                    ctx.SubroutineTimer = FeedIdleDuration;
                }
                break;
            }

            case FeedIdleAfter:
            {
                SubroutineSteps.SetIdle(ref ctx);
                ctx.SubroutineTimer -= ctx.Dt;
                if (ctx.SubroutineTimer <= 0f)
                {
                    // At night, go to sleep instead of finding another bush
                    if (ctx.IsNight)
                    {
                        SwitchToTimeOfDayRoutine(ref ctx);
                        break;
                    }
                    // Try to find another bush nearby, otherwise return to roaming
                    Vec2 nextBush;
                    if (FindNearbyBush(ref ctx, out nextBush, minDist: 3f))
                    {
                        ctx.Subroutine = FeedWalkToBush;
                        ctx.SubroutineTimer = 0f;
                        ctx.Units[ctx.UnitIndex].MoveTarget = nextBush;
                    }
                    else
                    {
                        SwitchToTimeOfDayRoutine(ref ctx);
                    }
                }
                break;
            }
        }
    }

    /// <summary>Find a nearby bush within BushSearchRadius of spawn position.
    /// Returns a pathable spot adjacent to the bush, not the bush center.</summary>
    private static bool FindNearbyBush(ref AIContext ctx, out Vec2 feedSpot, float minDist = 0f)
    {
        feedSpot = Vec2.Zero;
        var envSystem = ctx.EnvSystem;
        if (envSystem == null) return false;

        Vec2 spawnPos = ctx.Units[ctx.UnitIndex].SpawnPosition;
        float searchRadiusSq = BushSearchRadius * BushSearchRadius;
        float minDistSq = minDist * minDist;

        float bestDist = float.MaxValue;
        Vec2 bestBushPos = Vec2.Zero;
        float bestBushRadius = 0f;
        bool found = false;

        // Use a semi-random offset to avoid all deer targeting the same bush
        int offset = (ctx.UnitIndex * 37 + ctx.FrameNumber / 60) % Math.Max(envSystem.ObjectCount, 1);

        for (int iter = 0; iter < envSystem.ObjectCount; iter++)
        {
            int i = (iter + offset) % envSystem.ObjectCount;
            var obj = envSystem.GetObject(i);
            var def = envSystem.Defs[obj.DefIndex];

            if (def.Category != "Bush") continue;

            Vec2 objPos = new Vec2(obj.X, obj.Y);

            // Must be near the deer's spawn area
            if ((objPos - spawnPos).LengthSq() > searchRadiusSq) continue;

            // Must be at least minDist from current position (to find a different bush)
            float distSq = (objPos - ctx.MyPos).LengthSq();
            if (distSq < minDistSq) continue;

            if (distSq < bestDist)
            {
                bestDist = distSq;
                bestBushPos = objPos;
                bestBushRadius = def.CollisionRadius * obj.Scale;
                found = true;
            }
        }

        if (!found) return false;

        // Pick a spot just outside the bush that's actually pathable. The deer
        // can't stand inside the bush's collision radius, so the standoff has to
        // cover both the bush's radius AND the deer's own radius (plus a small
        // buffer). Then sweep N angles starting from a semi-random offset and
        // return the first one that isn't blocked — otherwise a bush surrounded
        // on one side by walls/trees leaves the deer jammed against the far side.
        float deerRadius = ctx.Units[ctx.UnitIndex].Radius;
        float standoff = bestBushRadius + deerRadius + 0.2f;
        float startAngle = ((ctx.UnitIndex * 53 + ctx.FrameNumber / 30) % 628) / 100f;
        const int AngleSamples = 16;

        var grid = ctx.Pathfinder?.Grid;
        for (int i = 0; i < AngleSamples; i++)
        {
            float a = startAngle + i * (MathF.PI * 2f / AngleSamples);
            Vec2 candidate = bestBushPos + new Vec2(MathF.Cos(a) * standoff, MathF.Sin(a) * standoff);

            if (grid == null || SubroutineSteps.IsPointWalkable(grid, candidate, deerRadius))
            {
                feedSpot = candidate;
                return true;
            }
        }

        // Fallback: no walkable angle found — return the preferred angle and let
        // the deer try. It may still stall, but this path is rare (bush surrounded).
        feedSpot = bestBushPos + new Vec2(MathF.Cos(startAngle) * standoff, MathF.Sin(startAngle) * standoff);
        return true;
    }

    // ═══════════════════════════════════════
    //  Debug names
    // ═══════════════════════════════════════

    public string GetRoutineName(byte routine) => routine switch
    {
        RoutineIdleRoaming => "IdleRoaming",
        RoutineSleeping => "Sleeping",
        RoutineAlert => "Alert",
        RoutineFleeing => "Fleeing",
        RoutineCalming => "Calming",
        RoutineFightBack => "FightBack",
        RoutineFeeding => "Feeding",
        _ => $"Unknown({routine})"
    };

    public string GetSubroutineName(byte routine, byte subroutine) => routine switch
    {
        RoutineAlert => subroutine switch
        {
            AlertWatch => "Watch",
            AlertRun => "Run",
            _ => $"Unknown({subroutine})"
        },
        RoutineFightBack => subroutine switch
        {
            FightStance => "Stance",
            FightCharge => "Charge",
            _ => $"Unknown({subroutine})"
        },
        RoutineFeeding => subroutine switch
        {
            FeedWalkToBush => "WalkToBush",
            FeedEating => "Eating",
            FeedIdleAfter => "IdleAfter",
            _ => $"Unknown({subroutine})"
        },
        _ => subroutine == 0 ? "Default" : $"Unknown({subroutine})"
    };
}
