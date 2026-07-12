using System;
using System.Collections.Generic;
using Necroking.Core;
using Necroking.Lib;
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
    private const float HerdCohesionRadius = 12f; // straggler leash from the herd centroid (see TryHerdCohesion)
    private const float FleeDistance = 20f;
    private const float CalmDuration = 8f;
    private const float CalmHoldTime = 3f; // Don't let calm tick below this while threat is in wary buffer
    private const float WaryBufferScale = 1.5f; // Hostile within detRange*this keeps deer wary
    private const float MinFleeDuration = 4f; // Minimum flee time after damage (prevents instant calming)
    private const float AlertRecheckInterval = 1f;
    private const float AlertThresholdFraction = 0.9f;
    private const float FeedDuration = 4f;
    private const float FeedIdleDuration = 2f;
    private const float BushSearchRadius = 20f;
    private const float FeedingChance = 0.3f; // 30% chance to feed instead of roam

    /// <summary>Distance bonus (in world units) used to score Poisoned-state
    /// berry bushes as "closer" than they really are during foraging selection.
    /// A poisoned bush this many units away ties with a fresh bush at 0u.
    /// Lore: the deer sees them as ripest — can't smell the poison itself.</summary>
    private const float PoisonedBushAttractBonus = 6f;

    /// <summary>Multiplier on the deer's detection range used specifically
    /// for "poisoned bush nearby → speed up appetite" — wider than the
    /// alert/flee detection so the scent reaches deer further away than they
    /// can spot a threat. 3× of a 12-tile deer = 36 tile poisoned-bush radius.</summary>
    private const float PoisonedSatiationRangeScale = 3f;

    /// <summary>Multiplier on satiation tick-down while a poisoned berry bush
    /// is within the poisoned-detection range. The "fresh scent" makes them
    /// hungry again faster — 6x means a 30s satiation drains in 5s when a
    /// freshly-poisoned bush is nearby.</summary>
    private const float SatiationBuffPoisonedAccel = 6f;

    public void OnSpawn(ref AIContext ctx)
    {
        ctx.Units[ctx.UnitIndex].SpawnPosition = ctx.MyPos;
        ctx.Routine = ctx.IsNight ? RoutineSleeping : RoutineIdleRoaming;
        ctx.Subroutine = 0;
        ctx.SubroutineTimer = 0f;
    }

    public void OnRoutineExit(ref AIContext ctx, byte oldRoutine, byte newRoutine)
    {
        int i = ctx.UnitIndex;
        // FightBack owns the combat-lock fields; no exit path may leak a queued charge.
        if (oldRoutine == RoutineFightBack)
        {
            ctx.Units[i].Target = CombatTarget.None;
            ctx.Units[i].EngagedTarget = CombatTarget.None;
            ctx.Units[i].PendingAttack = CombatTarget.None;
        }
        // Feeding owns its bush claim — don't leave a stale index behind when spooked away.
        if (oldRoutine == RoutineFeeding)
            ctx.Units[i].BushWorkObjIdx = -1;
    }

    public void OnRoutineEnter(ref AIContext ctx, byte oldRoutine, byte newRoutine)
    {
        // Calming means "threat resolved" — no combat state may survive into it.
        if (newRoutine == RoutineCalming)
        {
            ctx.Units[ctx.UnitIndex].Target = CombatTarget.None;
            ctx.Units[ctx.UnitIndex].EngagedTarget = CombatTarget.None;
        }
    }

    public void Update(ref AIContext ctx)
    {
        // "Herded" cheat (set by AI.WolfPackHuntAI when a wolf pack commits its drive): for a short
        // window force a flat-out flee in the pack's chosen direction (toward the necromancer),
        // bypassing normal routine evaluation entirely. This is what makes the pack able to steer
        // the prey's first bolt toward your horde before it can react on its own.
        if (ctx.Units[ctx.UnitIndex].HerdedTimer > 0f)
        {
            ctx.Units[ctx.UnitIndex].HerdedTimer -= ctx.Dt;
            Vec2 herdDir = ctx.Units[ctx.UnitIndex].HerdedDir;
            if (herdDir.LengthSq() > 1e-4f)
            {
                // Per-frame re-assert: no-op while already Fleeing, real transition
                // (with exit hooks — e.g. out of FightBack) on the first herded frame.
                ctx.TransitionTo(RoutineFleeing);
                ctx.Units[ctx.UnitIndex].Fleeing = true;
                ctx.Units[ctx.UnitIndex].FleeElapsed += ctx.Dt;
                SubroutineSteps.SetEffort(ref ctx, Movement.MoveEffort.Sprint);
                Vec2 dest = ctx.MyPos + herdDir * FleeDistance;
                SubroutineSteps.MoveToward(ref ctx, dest, ctx.MyMaxSpeed);
                return;
            }
        }

        AcceleratePoisonedSatiation(ref ctx);
        EvaluateRoutine(ref ctx);

        // Mirror the flee state onto a unit-level flag so the combat code can suppress
        // the hit-react flinch while the deer is running (keep the gallop, don't flinch)
        // without needing to know DeerHerd's routine numbering.
        ctx.Units[ctx.UnitIndex].Fleeing = ctx.Routine == RoutineFleeing;

        // Reset flee-elapsed counter whenever the deer isn't currently fleeing,
        // so the next fresh flee starts with the 2-second Hurry ramp from zero.
        if (ctx.Routine != RoutineFleeing)
            ctx.Units[ctx.UnitIndex].FleeElapsed = 0f;

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

    /// <summary>If a poisoned berry bush is within the deer's detection range,
    /// drain the buff_satiated timer faster (multiplier on per-tick decay).
    /// Compounds with #1 (poisoned bushes pulled toward higher-priority targets):
    /// here we make the deer get hungry again sooner so it actually gets to the
    /// foraging decision while the poisoned bush is still there. Skipped if the
    /// deer has no satiation buff or no poisoned bush is near — zero cost in
    /// the common case (the inner scan returns early on the first miss).</summary>
    private static void AcceleratePoisonedSatiation(ref AIContext ctx)
    {
        var envSystem = ctx.EnvSystem;
        if (envSystem == null) return;
        var buffs = ctx.Units[ctx.UnitIndex].ActiveBuffs;
        int satiatedIdx = -1;
        for (int j = 0; j < buffs.Count; j++)
            if (buffs[j].BuffDefID == "buff_satiated") { satiatedIdx = j; break; }
        if (satiatedIdx < 0) return; // no satiation to accelerate

        // Use a wider radius than alert/flee detection — the appetite-trigger
        // scent reaches further than a deer's visual threat awareness.
        float detRange = ctx.Units[ctx.UnitIndex].DetectionRange * PoisonedSatiationRangeScale;
        if (detRange <= 0f) return;
        float detRangeSq = detRange * detRange;
        var myPos = ctx.MyPos;

        bool anyPoisonedInRange = false;
        for (int i = 0; i < envSystem.ObjectCount; i++)
        {
            var def = envSystem.Defs[envSystem.GetObject(i).DefIndex];
            if (!def.IsBerryBush) continue;
            var rt = envSystem.GetObjectRuntime(i);
            if (!rt.Alive || rt.BerryState != World.BerryState.Poisoned) continue;
            var obj = envSystem.GetObject(i);
            float dx = obj.X - myPos.X, dy = obj.Y - myPos.Y;
            if (dx * dx + dy * dy <= detRangeSq) { anyPoisonedInRange = true; break; }
        }
        if (!anyPoisonedInRange) return;

        // Subtract the *extra* decay on top of the natural BuffSystem tick.
        // (factor-1) × dt because the normal tick already takes 1× dt.
        var b = buffs[satiatedIdx];
        b.RemainingDuration -= ctx.Dt * (SatiationBuffPoisonedAccel - 1f);
        buffs[satiatedIdx] = b;
    }

    private void EvaluateRoutine(ref AIContext ctx)
    {
        byte alert = ctx.AlertState;
        bool isMale = IsMale(ref ctx);

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
            // Timer = minimum flee time before calming allowed.
            ctx.TransitionTo(RoutineFleeing, 0, MinFleeDuration);
            ctx.AlertState = (byte)UnitAlertState.Alert;
            ctx.Units[ctx.UnitIndex].ShowStatusSymbol(UnitStatusSymbol.React, 1.5f);
            return;
        }

        // Alert detected → enter Alert routine. Re-entering from Calming is
        // important: a deer post-flee with a fresh threat walking back into
        // detection should react, not stand there waiting out its calm timer.
        if (alert >= (byte)UnitAlertState.Alert &&
            (ctx.Routine <= RoutineSleeping || ctx.Routine == RoutineFeeding || ctx.Routine == RoutineCalming))
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

            ctx.TransitionTo(RoutineAlert, AlertWatch);
            return;
        }

        // Threat gone while alert/fleeing → calm down (but respect minimum flee time).
        // (OnRoutineEnter(Calming) clears Target/EngagedTarget on every path in.)
        if (alert == (byte)UnitAlertState.Unaware)
        {
            if (ctx.Routine == RoutineAlert)
            {
                ctx.TransitionTo(RoutineCalming, 0, CalmDuration);
                return;
            }
            if (ctx.Routine == RoutineFleeing)
            {
                // Two cases:
                //   - Threat-driven flee (AlertTarget set): calm as soon as alert
                //     drops to Unaware. AwarenessSystem's own hysteresis is the
                //     buffer; no artificial wall-clock timer. Sprint covering ground
                //     fast naturally triggers calm sooner, but only because the deer
                //     actually escaped detection — not because the clock ran out.
                //   - Poison/random-spook (no AlertTarget): there's nothing for
                //     AwarenessSystem to detect, so alert drops almost immediately.
                //     Fall back to the MinFleeDuration timer so the deer runs at
                //     least a few seconds before calming.
                bool hasAlertTarget = ctx.Units[ctx.UnitIndex].AlertTarget != GameConstants.InvalidUnit;
                bool canCalm = hasAlertTarget || ctx.SubroutineTimer <= 0f;
                if (canCalm)
                {
                    ctx.TransitionTo(RoutineCalming, 0, CalmDuration);
                    return;
                }
            }
            if (ctx.Routine == RoutineFightBack && !SubroutineSteps.IsTargetAlive(ref ctx))
            {
                ctx.TransitionTo(RoutineCalming, 0, CalmDuration);
                return;
            }
        }

        // Male deer: if fleeing but actually engaged in combat, fight back
        if (isMale && ctx.Routine == RoutineFleeing && ctx.Units[ctx.UnitIndex].InCombat)
        {
            if (ShouldFightBack(ref ctx))
            {
                ctx.TransitionTo(RoutineFightBack, FightStance);
                ctx.Units[ctx.UnitIndex].Target = CombatTarget.Unit(ctx.AlertTarget);
                ctx.Units[ctx.UnitIndex].ShowStatusSymbol(UnitStatusSymbol.React, 1.5f);
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

                ctx.TransitionTo(target);
            }
        }

        // Fight target died or left.
        // (Combat-field cleanup lives in OnRoutineExit(FightBack) — the transition fires it.)
        if (ctx.Routine == RoutineFightBack)
        {
            if (!SubroutineSteps.IsTargetAlive(ref ctx))
            {
                ctx.AlertState = (byte)UnitAlertState.Unaware;
                ctx.AlertTarget = GameConstants.InvalidUnit;
                SwitchToTimeOfDayRoutine(ref ctx);
                return;
            }
            if (ctx.AlertState == (byte)UnitAlertState.Unaware)
            {
                SwitchToTimeOfDayRoutine(ref ctx);
                return;
            }
        }
    }

    private static void SwitchToTimeOfDayRoutine(ref AIContext ctx)
    {
        ctx.TransitionTo(ctx.IsNight ? RoutineSleeping : RoutineIdleRoaming);
    }

    private static bool IsMale(ref AIContext ctx)
    {
        string defId = ctx.Units[ctx.UnitIndex].UnitDefID ?? "";
        return defId.Contains("Male", StringComparison.OrdinalIgnoreCase)
            && !defId.Contains("Female", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Check if any hostile is within a fraction of the detection range.</summary>
    private static bool AnyHostileWithinThreshold(ref AIContext ctx)
        => AnyHostileWithinScale(ref ctx, AlertThresholdFraction);

    /// <summary>Check if any hostile is within DetectionRange * scale.</summary>
    private static bool AnyHostileWithinScale(ref AIContext ctx, float scale)
    {
        float threshold = ctx.Units[ctx.UnitIndex].DetectionRange * scale;
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
            if (Vec2.DistSq(ctx.Units[j].Position, ctx.MyPos) < 15f * 15f) // strict < (WithinRange is <=)
                threatCount++;
        }
        return threatCount <= 1;
    }

    // ═══════════════════════════════════════
    //  Routine: Idle & Roaming (with 30% feeding chance)
    // ═══════════════════════════════════════

    private static void UpdateIdleRoaming(ref AIContext ctx)
    {
        // Squad cohesion: if this deer has drifted well past the edge of its herd, amble back
        // toward the group instead of roaming further out. This is what makes deer "remember
        // their pack and stay together" — and, after a flee scatters them, regroup. Uses the
        // live herd centroid (not the fixed spawn point) so a herd that migrates stays cohesive.
        if (TryHerdCohesion(ref ctx)) return;

        // Casual grazing-area wander.
        SubroutineSteps.SetEffort(ref ctx, Movement.MoveEffort.Walk, 0.5f);
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

    /// <summary>Herd cohesion step: when the deer has strayed beyond the herd's edge, walk it
    /// back toward the live centroid and return true (the caller should skip its normal roam this
    /// frame). Returns false — no override — when the deer is solo, squad-less, or already close
    /// enough to the group. The leash is the herd's own spread plus a margin, so a naturally
    /// spread-out grazing herd isn't constantly yanked inward; only genuine stragglers regroup.</summary>
    private static bool TryHerdCohesion(ref AIContext ctx)
    {
        var squad = ctx.MySquad;
        if (squad == null || squad.AliveCount <= 1) return false;

        Vec2 toCentroid = squad.Centroid - ctx.MyPos;
        float dist = toCentroid.Length();
        float leash = MathF.Max(HerdCohesionRadius, squad.Spread + HerdCohesionRadius * 0.5f);
        if (dist <= leash) return false;

        SubroutineSteps.SetEffort(ref ctx, Movement.MoveEffort.Walk, 0.6f);
        // Aim for a point just inside the leash on our side of the centroid, so stragglers gather
        // loosely around the herd rather than all piling onto the exact centroid.
        Vec2 dir = toCentroid * (1f / MathF.Max(dist, 1e-3f));
        Vec2 dest = squad.Centroid - dir * (leash * 0.5f);
        SubroutineSteps.MoveToward(ref ctx, dest, ctx.MyMaxSpeed);
        ctx.Subroutine = 0; // walking sub-state (not idling)
        return true;
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
                AnimResolver.SetOverride(ctx.Units[ctx.UnitIndex], AnimRequest.Combat(AnimState.Standup));
                ctx.SubroutineTimer -= ctx.Dt;
                if (ctx.SubroutineTimer <= 0f)
                {
                    // Standup complete — transition to alert if threat present, else time-of-day routine
                    if (ctx.AlertState >= (byte)UnitAlertState.Alert)
                    {
                        ctx.TransitionTo(RoutineAlert, AlertWatch);
                    }
                    else
                    {
                        // StartRoutine, not SwitchToTimeOfDayRoutine: at night the target
                        // IS Sleeping — the routine must restart at SleepSitting, or the
                        // deer stays wedged in SleepWaking with an expired timer.
                        AIControl.StartRoutine(ref ctx,
                            ctx.IsNight ? RoutineSleeping : RoutineIdleRoaming);
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
                    ctx.TransitionTo(RoutineFleeing);
                    ctx.Units[ctx.UnitIndex].ShowStatusSymbol(UnitStatusSymbol.React, 1.5f);
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
        // Squad-driven: the whole herd bolts with the spooked deer — exact membership, no
        // per-frame radius scan. This is the "remember your pack, flee as one" path. Falls
        // back to a proximity scan only when the deer has no squad (scenarios / unclustered).
        var squad = ctx.MySquad;
        if (squad != null)
        {
            var members = squad.Members;
            for (int m = 0; m < members.Count; m++)
            {
                if (!ctx.Units.TryGetIndex(members[m], out int j)) continue;
                if (j == ctx.UnitIndex || !ctx.Units[j].Alive) continue;
                EscalateHerdmateToFlee(ref ctx, j);
            }
            return;
        }

        float herdRadius = ctx.Units[ctx.UnitIndex].GroupAlertRadius;
        if (herdRadius <= 0f) herdRadius = 15f;
        float herdRadiusSq = herdRadius * herdRadius;

        for (int j = 0; j < ctx.Units.Count; j++)
        {
            if (j == ctx.UnitIndex || !ctx.Units[j].Alive) continue;
            if (ctx.Units[j].Faction != ctx.MyFaction) continue;
            if (ctx.Units[j].Archetype != ArchetypeRegistry.DeerHerd) continue;
            if ((ctx.Units[j].Position - ctx.MyPos).LengthSq() > herdRadiusSq) continue;
            EscalateHerdmateToFlee(ref ctx, j);
        }
    }

    /// <summary>Kick a single herdmate into the flee routine, sharing the spooked deer's alert
    /// target. Skips deer already fleeing / fighting so we don't reset their run.</summary>
    private static void EscalateHerdmateToFlee(ref AIContext ctx, int j)
    {
        byte r = ctx.Units[j].Routine;
        if (r == RoutineFleeing || r == RoutineFightBack) return;

        AIControl.TransitionUnit(ref ctx, j, RoutineFleeing);
        ctx.Units[j].AlertTarget = ctx.AlertTarget;
        ctx.Units[j].AlertState = (byte)UnitAlertState.Aggressive;
        ctx.Units[j].ShowStatusSymbol(UnitStatusSymbol.React, 1.5f);
    }

    // ═══════════════════════════════════════
    //  Routine: Fleeing
    // ═══════════════════════════════════════

    private static void UpdateFleeing(ref AIContext ctx)
    {
        // Tick down minimum flee timer
        if (ctx.SubroutineTimer > 0f)
            ctx.SubroutineTimer -= ctx.Dt;

        // Effort ramp: first 2 seconds capped at Hurry (jog gait — deer
        // accelerating into a controlled bound), after 2s unlocks to Sprint
        // (full panic gallop). FleeElapsed resets to 0 when the deer stops
        // fleeing (see Update entry below), so every fresh flee gets the
        // 2s build-up.
        ctx.Units[ctx.UnitIndex].FleeElapsed += ctx.Dt;
        bool sprintUnlocked = ctx.Units[ctx.UnitIndex].FleeElapsed >= 2.0f;
        SubroutineSteps.SetEffort(ref ctx,
            sprintUnlocked ? Movement.MoveEffort.Sprint : Movement.MoveEffort.Hurry);

        int threatIdx = SubroutineSteps.ResolveAlertTarget(ref ctx);
        if (threatIdx >= 0)
        {
            Vec2 threatPos = ctx.Units[threatIdx].Position;
            Vec2 awayDir = ctx.MyPos - threatPos;
            float dist = awayDir.Length();
            if (dist > 0.01f) awayDir *= 1f / dist;
            else awayDir = new Vec2(1, 0);

            Vec2 fleeDest = ctx.MyPos + awayDir * FleeDistance;
            SubroutineSteps.MoveToward(ref ctx, fleeDest, ctx.MyMaxSpeed);
        }
        else if (ctx.SubroutineTimer > 0f)
        {
            // No visible threat but still in minimum flee time (e.g. poison damage) —
            // keep running in current facing direction
            Vec2 fleeDir = Movement.FacingUtil.ForwardDir(ctx.Units[ctx.UnitIndex]);
            Vec2 fleeDest = ctx.MyPos + fleeDir * FleeDistance;
            SubroutineSteps.MoveToward(ref ctx, fleeDest, ctx.MyMaxSpeed);
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

        // Hold the calm timer while a hostile is still in the wary buffer —
        // deer waits for the player to walk further away before fully calming.
        if (ctx.SubroutineTimer < CalmHoldTime && AnyHostileWithinScale(ref ctx, WaryBufferScale))
            ctx.SubroutineTimer = CalmHoldTime;

        if (ctx.SubroutineTimer <= 0f)
        {
            ctx.TransitionTo(ctx.IsNight ? RoutineSleeping : RoutineIdleRoaming);
        }
    }

    // ═══════════════════════════════════════
    //  Routine: FightBack (male only — stance + charge)
    // ═══════════════════════════════════════

    private static void UpdateFightBack(ref AIContext ctx)
    {
        if (!SubroutineSteps.IsTargetAlive(ref ctx))
        {
            ctx.TransitionTo(RoutineCalming, 0, CalmDuration);
            return;
        }

        int targetIdx = SubroutineSteps.ResolveTarget(ref ctx);

        switch (ctx.Subroutine)
        {
            case FightStance:
            {
                // Stand still facing attacker, wait for attack cooldown.
                // Rate-capped turn toward the target (was a direct facing snap,
                // which bypassed the unit's TurnSpeed cap).
                if (targetIdx >= 0)
                    SubroutineSteps.FacePosition(ref ctx, ctx.Units[targetIdx].Position);
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
                        // Still closing distance — full commit charge.
                        SubroutineSteps.SetEffort(ref ctx, Movement.MoveEffort.Sprint);
                        SubroutineSteps.MoveToward(ref ctx, ctx.Units[targetIdx].Position, ctx.MyMaxSpeed);
                    }
                }
                break;
            }
        }
    }

    // ═══════════════════════════════════════
    //  Routine: Feeding (walk to bush, eat, idle, repeat)
    // ═══════════════════════════════════════

    /// <summary>Try to find a nearby berry bush and start feeding. Skipped
    /// while the deer is satiated (buff_satiated active). Returns true if started.</summary>
    private static bool TryStartFeeding(ref AIContext ctx)
    {
        // Don't path back to a bush while a threat is loitering nearby.
        if (AnyHostileWithinScale(ref ctx, WaryBufferScale))
            return false;

        // Recently ate — wait for the satiated buff to wear off.
        if (HasSatiationBuff(ref ctx))
            return false;

        if (!FindNearbyBush(ref ctx, out var bushPos, out int bushIdx))
            return false;

        ctx.TransitionTo(RoutineFeeding, FeedWalkToBush);
        ctx.Units[ctx.UnitIndex].MoveTarget = bushPos;
        ctx.Units[ctx.UnitIndex].BushWorkObjIdx = bushIdx;
        return true;
    }

    private static bool HasSatiationBuff(ref AIContext ctx)
    {
        var buffs = ctx.Units[ctx.UnitIndex].ActiveBuffs;
        for (int i = 0; i < buffs.Count; i++)
            if (buffs[i].BuffDefID == "buff_satiated") return true;
        return false;
    }

    /// <summary>End of FeedEating: actually consume the targeted bush and apply
    /// the resulting effect to the deer.
    ///   • Vanilla berries  → buff_satiated (blocks foraging for ~60s).
    ///   • Poisoned berries → the same full mechanic the thrown potion produces:
    ///        - buff_poison_dot    → DamageSystem.Apply(10, Poison, ArmorNegating)
    ///                                (PoisonStacks tick HP loss via TickPotionEffects)
    ///        - buff_paralysis_slow → PotionSystem.ApplyParalysis (8s slow → 6s stun)
    ///     The cosmetic buff is also applied so the unit tints + outlines match.
    ///     No satiation when poisoned — the deer's behavior reads as "sick" instead
    ///     of "full," and the slow/stun would lock foraging anyway.</summary>
    private static void ConsumeBushAndApplyEffect(ref AIContext ctx)
    {
        int unitIdx = ctx.UnitIndex;
        int bushIdx = ctx.Units[unitIdx].BushWorkObjIdx;
        ctx.Units[unitIdx].BushWorkObjIdx = -1;
        if (bushIdx < 0 || ctx.EnvSystem == null || ctx.GameData == null) return;

        string? appliedBuffID = ctx.EnvSystem.ConsumeBerryBush(bushIdx);
        if (appliedBuffID == null) return; // bush no longer eligible (eaten by someone else, destroyed, etc.)

        if (string.IsNullOrEmpty(appliedBuffID))
        {
            // Vanilla berries — apply satiation marker buff.
            var satDef = ctx.GameData.Buffs.Get("buff_satiated");
            if (satDef != null)
                GameSystems.BuffSystem.ApplyBuff(ctx.Units, unitIdx, satDef);
            return;
        }

        // Tainted berries — run the full potion mechanic plus the cosmetic buff.
        switch (appliedBuffID)
        {
            case "buff_poison_dot":
            {
                var events = ctx.DamageEvents ?? new List<GameSystems.DamageEvent>();
                GameSystems.DamageSystem.Apply(ctx.Units, unitIdx, 10,
                    GameSystems.DamageType.Poison,
                    GameSystems.DamageFlags.ArmorNegating, events);
                break;
            }
            case "buff_paralysis_slow":
                Game.PotionSystem.ApplyParalysis(unitIdx, ctx.Units);
                break;
        }

        var visualBuff = ctx.GameData.Buffs.Get(appliedBuffID);
        if (visualBuff != null)
            GameSystems.BuffSystem.ApplyBuff(ctx.Units, unitIdx, visualBuff);
    }

    private static void UpdateFeeding(ref AIContext ctx)
    {
        switch (ctx.Subroutine)
        {
            case FeedWalkToBush:
            {
                // Lazy walk to the bush — 0.3 cap = very slow stroll (grazing pace).
                SubroutineSteps.SetEffort(ref ctx, Movement.MoveEffort.Walk, 0.3f);
                Vec2 target = ctx.Units[ctx.UnitIndex].MoveTarget;
                float dist = (ctx.MyPos - target).Length();
                if (dist > 1.5f)
                {
                    SubroutineSteps.MoveToward(ref ctx, target, ctx.MyMaxSpeed);
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
                // Stand still facing the bush, playing feed animation.
                ctx.Units[ctx.UnitIndex].PreferredVel = Vec2.Zero;
                ctx.Units[ctx.UnitIndex].RoutineAnim = AnimRequest.Action(AnimState.Feeding);
                // Face toward the bush target (rate-capped).
                SubroutineSteps.FacePosition(ref ctx, ctx.Units[ctx.UnitIndex].MoveTarget);
                ctx.SubroutineTimer -= ctx.Dt;
                if (ctx.SubroutineTimer <= 0f)
                {
                    ConsumeBushAndApplyEffect(ref ctx);
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
                    // Deer with satiation can't immediately start another forage —
                    // they fall back to roaming until the buff lapses.
                    if (HasSatiationBuff(ref ctx))
                    {
                        SwitchToTimeOfDayRoutine(ref ctx);
                        break;
                    }
                    if (FindNearbyBush(ref ctx, out var nextBush, out int nextIdx, minDist: 3f))
                    {
                        ctx.Subroutine = FeedWalkToBush;
                        ctx.SubroutineTimer = 0f;
                        ctx.Units[ctx.UnitIndex].MoveTarget = nextBush;
                        ctx.Units[ctx.UnitIndex].BushWorkObjIdx = nextIdx;
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

    /// <summary>Find a nearby berry bush (only) within BushSearchRadius of
    /// the deer's spawn position whose runtime state has visible berries
    /// (Berries or Poisoned — deer can't tell the difference). NoBerry bushes
    /// and non-berry bushes are filtered out. Returns a pathable spot adjacent
    /// to the bush and the bush's object index for downstream consumption.
    ///
    /// Poisoned bushes get a "ripe scent" bias: they're scored as if they were
    /// <see cref="PoisonedBushAttractBonus"/> world units closer than their
    /// actual distance. So a poisoned bush at 8u beats a normal bush at 5u.
    /// The deer can't smell poison — they just prefer fresher-looking berries.</summary>
    private static bool FindNearbyBush(ref AIContext ctx, out Vec2 feedSpot, out int bushIdx, float minDist = 0f)
    {
        feedSpot = Vec2.Zero;
        bushIdx = -1;
        var envSystem = ctx.EnvSystem;
        if (envSystem == null) return false;

        Vec2 spawnPos = ctx.Units[ctx.UnitIndex].SpawnPosition;
        float searchRadiusSq = BushSearchRadius * BushSearchRadius;
        float minDistSq = minDist * minDist;

        float bestScore = float.MaxValue;
        Vec2 bestBushPos = Vec2.Zero;
        float bestBushRadius = 0f;
        int bestIdx = -1;

        // Use a semi-random offset to avoid all deer targeting the same bush
        int offset = (ctx.UnitIndex * 37 + ctx.FrameNumber / 60) % Math.Max(envSystem.ObjectCount, 1);

        for (int iter = 0; iter < envSystem.ObjectCount; iter++)
        {
            int i = (iter + offset) % envSystem.ObjectCount;
            var obj = envSystem.GetObject(i);
            var def = envSystem.Defs[obj.DefIndex];

            // Berry-bush only — plain bushes no longer attract deer.
            if (!def.IsBerryBush) continue;
            var rt = envSystem.GetObjectRuntime(i);
            if (!rt.Alive) continue;
            if (rt.BerryState == World.BerryState.NoBerry) continue;

            Vec2 objPos = new Vec2(obj.X, obj.Y);

            // Must be near the deer's spawn area
            if ((objPos - spawnPos).LengthSq() > searchRadiusSq) continue;

            // Must be at least minDist from current position (to find a different bush)
            float distSq = (objPos - ctx.MyPos).LengthSq();
            if (distSq < minDistSq) continue;

            // Score = distance, with a "ripe scent" subtraction for poisoned berries.
            // Negative scores are clamped to 0 so adjacent bushes can't tie pathologically.
            float score = MathF.Sqrt(distSq);
            if (rt.BerryState == World.BerryState.Poisoned)
                score = MathF.Max(0f, score - PoisonedBushAttractBonus);

            if (score < bestScore)
            {
                bestScore = score;
                bestBushPos = objPos;
                bestBushRadius = def.CollisionRadius * obj.Scale;
                bestIdx = i;
            }
        }

        if (bestIdx < 0) return false;
        bushIdx = bestIdx;

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
