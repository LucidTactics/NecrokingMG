using System;
using Necroking.Core;
using Necroking.Movement;

namespace Necroking.AI;

/// <summary>
/// Wolf pack AI archetype: predatory hit-and-run behavior with day/night cycle.
///
/// Routines:
///   0 = IdleRoaming  — daytime: 30% idle standing, 70% wandering within 10 units of spawn
///   1 = Sleeping     — nighttime: stand still (sleep animation if available)
///   2 = Fighting     — hit-and-run attack cycle when threat detected
///
/// Fighting subroutines:
///   0 = MoveToEngage    — pathfind toward target
///   1 = ExecuteAttack   — in melee range, strike once
///   2 = Disengage       — back away from target
///   3 = WaitForCooldown — maintain distance until attack ready, then repeat
///
/// Routine selection:
///   - Aggressive alert → enter Fighting (interrupt any routine)
///   - Target dies or leaves break range → return to time-of-day routine
///   - Night time → Sleeping
///   - Day time → IdleRoaming
///
/// Detection range: from UnitDef.DetectionRange (default 10)
/// Break range: from UnitDef.DetectionBreakRange (default 15)
/// </summary>
public class WolfPackHandler : IArchetypeHandler
{
    // Routine indices
    private const byte RoutineIdleRoaming = 0;
    private const byte RoutineSleeping = 1;
    private const byte RoutineFighting = 2;

    // Fighting subroutine indices
    private const byte FightMoveToEngage = 0;
    private const byte FightExecuteAttack = 1;
    private const byte FightDisengage = 2;
    private const byte FightWaitCooldown = 3;

    // Sleeping subroutine indices
    private const byte SleepSitting = 0;
    private const byte SleepAsleep = 1;
    private const byte SleepWaking = 2;

    // Tuning
    private const float DisengageDistance = 4f;
    private const float IdleRoamRadius = 10f;
    private const float SitDuration = 10f;
    private const float SleepDetectionScale = 0.6f;
    private const float StandupDuration = 1.0f;

    // Pounce is no longer AI-driven — it's weapon-archetype-driven and handled
    // centrally in Simulation.TryInitiatePounce for any unit whose primary weapon
    // has the Pounce archetype. WolfPackHandler just respects JumpPhase in its
    // engagement loop.

    public void OnSpawn(ref AIContext ctx)
    {
        ctx.Units[ctx.UnitIndex].SpawnPosition = ctx.MyPos;
        ctx.Units[ctx.UnitIndex].MoveTarget = ctx.MyPos;
        ctx.Routine = ctx.IsNight ? RoutineSleeping : RoutineIdleRoaming;
        ctx.Subroutine = 0;
        ctx.SubroutineTimer = 0f;
    }

    public void Update(ref AIContext ctx)
    {
        // --- Event evaluation: select routine ---
        EvaluateRoutine(ref ctx);

        // --- Execute current routine ---
        switch (ctx.Routine)
        {
            case RoutineIdleRoaming: UpdateIdleRoaming(ref ctx); break;
            case RoutineSleeping:    UpdateSleeping(ref ctx); break;
            case RoutineFighting:    UpdateFighting(ref ctx); break;
        }
    }

    private void EvaluateRoutine(ref AIContext ctx)
    {
        byte alertState = ctx.AlertState;

        // Retarget-when-hit (already fighting): if the current target is out of melee
        // reach AND we took damage recently, switch aggro to whoever hit us. Using
        // LastHitTime (not HitReacting) extends the evaluation window beyond the single
        // tick HitReacting lasts — the wolf may briefly flip in/out of melee range as
        // its current target kites, and we need to catch the "out of range" phase even
        // if the hit landed during a brief "in range" moment.
        const float RetargetOnHitWindow = 2.0f;
        if (ctx.Routine == RoutineFighting
            && !ctx.Units[ctx.UnitIndex].InCombat
            && ctx.Units[ctx.UnitIndex].LastHitTime >= 0f
            && (ctx.GameTime - ctx.Units[ctx.UnitIndex].LastHitTime) <= RetargetOnHitWindow)
        {
            uint attackerId = ctx.Units[ctx.UnitIndex].LastAttackerID;
            if (attackerId != GameConstants.InvalidUnit
                && attackerId != ctx.Units[ctx.UnitIndex].Target.UnitID)
            {
                int attackerIdx = UnitUtil.ResolveUnitIndex(ctx.Units, attackerId);
                if (attackerIdx >= 0 && ctx.Units[attackerIdx].Alive)
                {
                    ctx.Units[ctx.UnitIndex].Target = CombatTarget.Unit(attackerId);
                    ctx.Units[ctx.UnitIndex].EngagedTarget = CombatTarget.None;
                    ctx.AlertTarget = attackerId;
                    ctx.AlertState = (byte)UnitAlertState.Aggressive;
                    ctx.Subroutine = FightMoveToEngage;
                    ctx.SubroutineTimer = 0f;
                    ctx.Units[ctx.UnitIndex].ShowStatusSymbol(UnitStatusSymbol.React, 1.0f);
                    // Clear LastHitTime so we don't retarget repeatedly off a single hit.
                    ctx.Units[ctx.UnitIndex].LastHitTime = -1f;
                    return;
                }
            }
        }

        // Spooked: took damage while idle/sleeping → fight or flee
        if (ctx.Units[ctx.UnitIndex].HitReacting
            && ctx.Routine != RoutineFighting)
        {
            // If sleeping, standup first
            if (ctx.Routine == RoutineSleeping && ctx.Subroutine <= SleepAsleep)
            {
                ctx.Subroutine = SleepWaking;
                ctx.SubroutineTimer = StandupDuration;
                ctx.Units[ctx.UnitIndex].StandupTimer = StandupDuration;
                RestoreDetectionRange(ref ctx);
                return;
            }
            if (ctx.Routine == RoutineSleeping && ctx.Subroutine == SleepWaking)
                return;

            float detRange = ctx.Units[ctx.UnitIndex].DetectionRange;
            int enemyIdx = SubroutineSteps.FindClosestEnemy(ref ctx, detRange);
            if (enemyIdx >= 0)
            {
                // Enemy nearby — attack it
                ctx.AlertState = (byte)UnitAlertState.Aggressive;
                ctx.AlertTarget = ctx.Units[enemyIdx].Id;
                ctx.Units[ctx.UnitIndex].Target = CombatTarget.Unit(ctx.Units[enemyIdx].Id);
                ctx.Routine = RoutineFighting;
                ctx.Subroutine = FightMoveToEngage;
                ctx.SubroutineTimer = 0f;
                ctx.Units[ctx.UnitIndex].ShowStatusSymbol(UnitStatusSymbol.React, 1.5f);
                return;
            }
            else
            {
                // No enemy visible — flee in random direction then calm down
                SubroutineSteps.SetFleeRandomTarget(ref ctx, 10f);
                ctx.Routine = RoutineIdleRoaming;
                ctx.Subroutine = 0; // walking subroutine will move to MoveTarget
                ctx.SubroutineTimer = 0f;
                return;
            }
        }

        // Aggressive alert → enter fighting
        if (alertState == (byte)UnitAlertState.Aggressive && ctx.Routine != RoutineFighting)
        {
            // If sleeping, standup first
            if (ctx.Routine == RoutineSleeping && ctx.Subroutine <= SleepAsleep)
            {
                if (ctx.AlertTarget != GameConstants.InvalidUnit)
                {
                    int threatIdx = UnitUtil.ResolveUnitIndex(ctx.Units, ctx.AlertTarget);
                    if (threatIdx >= 0)
                        SubroutineSteps.FacePosition(ref ctx, ctx.Units[threatIdx].Position);
                }
                ctx.Subroutine = SleepWaking;
                ctx.SubroutineTimer = StandupDuration;
                ctx.Units[ctx.UnitIndex].StandupTimer = StandupDuration;
                RestoreDetectionRange(ref ctx);
                return;
            }
            // Wait for standup to finish
            if (ctx.Routine == RoutineSleeping && ctx.Subroutine == SleepWaking)
                return;

            // Acquire the alert target as combat target
            uint threatId = ctx.AlertTarget;
            if (threatId != GameConstants.InvalidUnit)
            {
                ctx.Units[ctx.UnitIndex].Target = CombatTarget.Unit(threatId);
                ctx.Routine = RoutineFighting;
                ctx.Subroutine = FightMoveToEngage;
                ctx.SubroutineTimer = 0f;
                ctx.Units[ctx.UnitIndex].ShowStatusSymbol(UnitStatusSymbol.React, 1.5f);
                return;
            }
        }

        // In fighting: check if target still valid
        if (ctx.Routine == RoutineFighting)
        {
            if (!SubroutineSteps.IsTargetAlive(ref ctx))
            {
                // Target dead — return to time-of-day routine
                ctx.Units[ctx.UnitIndex].Target = CombatTarget.None;
                ctx.Units[ctx.UnitIndex].EngagedTarget = CombatTarget.None;
                ctx.AlertState = (byte)UnitAlertState.Unaware;
                ctx.AlertTarget = GameConstants.InvalidUnit;
                SwitchToTimeOfDayRoutine(ref ctx);
                return;
            }

            // Alert dropped (enemy left break range) — disengage
            if (alertState == (byte)UnitAlertState.Unaware)
            {
                ctx.Units[ctx.UnitIndex].Target = CombatTarget.None;
                ctx.Units[ctx.UnitIndex].EngagedTarget = CombatTarget.None;
                SwitchToTimeOfDayRoutine(ref ctx);
                return;
            }
        }

        // Not fighting — follow time of day
        if (ctx.Routine != RoutineFighting)
            SwitchToTimeOfDayRoutine(ref ctx);
    }

    private static void SwitchToTimeOfDayRoutine(ref AIContext ctx)
    {
        byte target = ctx.IsNight ? RoutineSleeping : RoutineIdleRoaming;
        if (ctx.Routine != target)
        {
            // Waking from sleep — standup first
            if (ctx.Routine == RoutineSleeping && ctx.Subroutine <= SleepAsleep && !ctx.IsNight)
            {
                ctx.Subroutine = SleepWaking;
                ctx.SubroutineTimer = StandupDuration;
                ctx.Units[ctx.UnitIndex].StandupTimer = StandupDuration;
                RestoreDetectionRange(ref ctx);
                return;
            }
            if (ctx.Routine == RoutineSleeping && ctx.Subroutine == SleepWaking)
                return;

            ctx.Routine = target;
            ctx.Subroutine = 0;
            ctx.SubroutineTimer = 0f;
        }
    }

    // ═══════════════════════════════════════
    //  Routine: Idle & Roaming
    // ═══════════════════════════════════════

    private static void UpdateIdleRoaming(ref AIContext ctx)
    {
        SubroutineSteps.IdleRoam(ref ctx, IdleRoamRadius);
    }

    // ═══════════════════════════════════════
    //  Routine: Sleeping
    // ═══════════════════════════════════════

    private static void UpdateSleeping(ref AIContext ctx)
    {
        ctx.Units[ctx.UnitIndex].PreferredVel = Vec2.Zero;

        switch (ctx.Subroutine)
        {
            case SleepSitting:
                ctx.SubroutineTimer += ctx.Dt;
                if (ctx.SubroutineTimer >= SitDuration)
                {
                    ctx.Subroutine = SleepAsleep;
                    ctx.SubroutineTimer = 0f;
                    ReduceDetectionRange(ref ctx);
                }
                break;

            case SleepAsleep:
                // Stay asleep until woken
                break;

            case SleepWaking:
                ctx.SubroutineTimer -= ctx.Dt;
                if (ctx.SubroutineTimer <= 0f)
                {
                    if (ctx.AlertState >= (byte)UnitAlertState.Aggressive)
                    {
                        uint threatId = ctx.AlertTarget;
                        if (threatId != GameConstants.InvalidUnit)
                        {
                            ctx.Units[ctx.UnitIndex].Target = CombatTarget.Unit(threatId);
                            ctx.Routine = RoutineFighting;
                            ctx.Subroutine = FightMoveToEngage;
                            ctx.SubroutineTimer = 0f;
                            ctx.Units[ctx.UnitIndex].ShowStatusSymbol(UnitStatusSymbol.React, 1.5f);
                        }
                        else
                            SwitchToTimeOfDayRoutine(ref ctx);
                    }
                    else
                    {
                        byte target = ctx.IsNight ? RoutineSleeping : RoutineIdleRoaming;
                        ctx.Routine = target;
                        ctx.Subroutine = 0;
                        ctx.SubroutineTimer = 0f;
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
        if (ctx.GameData != null)
        {
            var def = ctx.GameData.Units.Get(ctx.Units[ctx.UnitIndex].UnitDefID);
            if (def != null)
                ctx.Units[ctx.UnitIndex].DetectionRange = def.DetectionRange;
        }
    }

    // ═══════════════════════════════════════
    //  Routine: Fighting (hit-and-run cycle)
    // ═══════════════════════════════════════

    private void UpdateFighting(ref AIContext ctx)
    {
        ctx.SubroutineTimer += ctx.Dt;

        switch (ctx.Subroutine)
        {
            case FightMoveToEngage:
            {
                // If the wolf is in any jump phase (pounce takeoff/airborne/landing/recovery),
                // JumpSystem has control — don't drive AI movement.
                if (ctx.Units[ctx.UnitIndex].JumpPhase != 0) break;

                SubroutineSteps.MoveToTarget(ref ctx);
                int targetIdx = SubroutineSteps.ResolveTarget(ref ctx);
                if (targetIdx >= 0)
                {
                    float dist = (ctx.Units[targetIdx].Position - ctx.MyPos).Length();
                    float attackRange = SubroutineSteps.GetMeleeRange(ref ctx, targetIdx);
                    if (dist <= attackRange)
                    {
                        ctx.Subroutine = FightExecuteAttack;
                        ctx.SubroutineTimer = 0f;
                    }
                }
                break;
            }

            case FightExecuteAttack:
            {
                SubroutineSteps.AttackTarget(ref ctx);
                // Transition: wait for attack animation to finish (PostAttackTimer == 0)
                // before disengaging. AttackCooldown > 0 means the attack was initiated.
                if (ctx.Units[ctx.UnitIndex].AttackCooldown > 0
                    && ctx.Units[ctx.UnitIndex].PostAttackTimer <= 0f
                    && ctx.SubroutineTimer > 0.1f)
                {
                    ctx.Subroutine = FightDisengage;
                    ctx.SubroutineTimer = 0f;
                    ctx.Units[ctx.UnitIndex].EngagedTarget = CombatTarget.None;
                }
                break;
            }

            case FightDisengage:
            {
                int targetIdx = SubroutineSteps.ResolveTarget(ref ctx);
                if (targetIdx >= 0)
                {
                    SubroutineSteps.Disengage(ref ctx, DisengageDistance);
                    if (SubroutineSteps.Disengage_Complete(ref ctx, DisengageDistance))
                    {
                        ctx.Subroutine = FightWaitCooldown;
                        ctx.SubroutineTimer = 0f;
                    }
                }
                else
                {
                    ctx.Subroutine = FightWaitCooldown;
                    ctx.SubroutineTimer = 0f;
                }
                break;
            }

            case FightWaitCooldown:
            {
                SubroutineSteps.WaitForCooldown(ref ctx);
                if (SubroutineSteps.WaitForCooldown_Ready(ref ctx))
                {
                    ctx.Subroutine = FightMoveToEngage;
                    ctx.SubroutineTimer = 0f;
                }
                break;
            }
        }
    }

    public string GetRoutineName(byte routine) => routine switch
    {
        RoutineIdleRoaming => "IdleRoaming",
        RoutineSleeping => "Sleeping",
        RoutineFighting => "Fighting",
        _ => $"Unknown({routine})"
    };

    public string GetSubroutineName(byte routine, byte subroutine) => routine switch
    {
        RoutineFighting => subroutine switch
        {
            FightMoveToEngage => "MoveToEngage",
            FightExecuteAttack => "ExecuteAttack",
            FightDisengage => "Disengage",
            FightWaitCooldown => "WaitCooldown",
            _ => $"Unknown({subroutine})"
        },
        _ => subroutine == 0 ? "Default" : $"Unknown({subroutine})"
    };
}
