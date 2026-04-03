using System;
using Necroking.Core;

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

    // Tuning
    private const float DisengageDistance = 4f; // back off this far after attacking
    private const float IdleRoamRadius = 10f;   // wander within this of spawn point

    public void OnSpawn(ref AIContext ctx)
    {
        ctx.Units.SpawnPosition[ctx.UnitIndex] = ctx.MyPos;
        ctx.Units.MoveTarget[ctx.UnitIndex] = ctx.MyPos;
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

        // Aggressive alert → enter fighting
        if (alertState == (byte)UnitAlertState.Aggressive && ctx.Routine != RoutineFighting)
        {
            // Acquire the alert target as combat target
            uint threatId = ctx.AlertTarget;
            if (threatId != GameConstants.InvalidUnit)
            {
                ctx.Units.Target[ctx.UnitIndex] = CombatTarget.Unit(threatId);
                ctx.Routine = RoutineFighting;
                ctx.Subroutine = FightMoveToEngage;
                ctx.SubroutineTimer = 0f;
                return;
            }
        }

        // In fighting: check if target still valid
        if (ctx.Routine == RoutineFighting)
        {
            if (!SubroutineSteps.IsTargetAlive(ref ctx))
            {
                // Target dead — return to time-of-day routine
                ctx.Units.Target[ctx.UnitIndex] = CombatTarget.None;
                ctx.Units.EngagedTarget[ctx.UnitIndex] = CombatTarget.None;
                ctx.AlertState = (byte)UnitAlertState.Unaware;
                ctx.AlertTarget = GameConstants.InvalidUnit;
                SwitchToTimeOfDayRoutine(ref ctx);
                return;
            }

            // Alert dropped (enemy left break range) — disengage
            if (alertState == (byte)UnitAlertState.Unaware)
            {
                ctx.Units.Target[ctx.UnitIndex] = CombatTarget.None;
                ctx.Units.EngagedTarget[ctx.UnitIndex] = CombatTarget.None;
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
        // Just stand still. Could play a sleep animation in the future.
        SubroutineSteps.Idle(ref ctx);
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
                SubroutineSteps.MoveToTarget(ref ctx);
                int targetIdx = SubroutineSteps.ResolveTarget(ref ctx);
                if (targetIdx >= 0)
                {
                    float attackRange = SubroutineSteps.GetMeleeRange(ref ctx, targetIdx);
                    float dist = (ctx.Units.Position[targetIdx] - ctx.MyPos).Length();
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
                if (ctx.Units.AttackCooldown[ctx.UnitIndex] > 0
                    && ctx.Units.PostAttackTimer[ctx.UnitIndex] <= 0f
                    && ctx.SubroutineTimer > 0.1f)
                {
                    ctx.Subroutine = FightDisengage;
                    ctx.SubroutineTimer = 0f;
                    ctx.Units.EngagedTarget[ctx.UnitIndex] = CombatTarget.None;
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
