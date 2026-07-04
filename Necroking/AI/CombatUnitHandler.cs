using System;
using Necroking.Core;
using Necroking.Movement;

namespace Necroking.AI;

/// <summary>
/// Generic combat unit handler for: soldiers, guards, knights, armies.
/// Covers PatrolSoldier, GuardStationary, and ArmyUnit archetypes.
///
/// PatrolSoldier routines:
///   0 = Patrol       — walk waypoints
///   1 = Alert        — noticed threat, watching
///   2 = Investigate  — move toward detection point
///   3 = Combat       — fighting enemies
///   4 = Return       — going back to patrol/guard position
///
/// GuardStationary routines:
///   0 = Guard        — stand at post, scan for enemies
///   1 = Alert        — noticed threat
///   2 = Combat       — fighting enemies
///   3 = Return       — returning to guard position
///
/// ArmyUnit routines:
///   0 = IdleRoaming  — wander near spawn
///   1 = Alert        — noticed threat
///   2 = Combat       — fighting enemies
///   3 = Return       — returning to position
///
/// All variants use shared combat subroutines and the awareness system.
/// The archetype ID determines which patrol/guard/army behavior to use.
/// </summary>
public class CombatUnitHandler : IArchetypeHandler
{
    private const byte RoutineIdle = 0;
    private const byte RoutineAlert = 1;
    private const byte RoutineCombat = 2;
    private const byte RoutineReturn = 3;

    // Combat subroutines
    private const byte CombatChase = 0;
    private const byte CombatAttack = 1;

    // Patrol subroutines
    private const byte PatrolWalking = 0;
    private const byte PatrolWaiting = 1;

    private readonly byte _archetypeId;

    public CombatUnitHandler(byte archetypeId) { _archetypeId = archetypeId; }

    public void OnSpawn(ref AIContext ctx)
    {
        ctx.Units[ctx.UnitIndex].SpawnPosition = ctx.MyPos;
        ctx.Routine = RoutineIdle;
        ctx.Subroutine = 0;
        ctx.SubroutineTimer = 0f;
    }

    public void OnRoutineExit(ref AIContext ctx, byte oldRoutine, byte newRoutine)
    {
        // Combat owns the melee-lock fields; no exit path may leak a queued swing.
        if (oldRoutine == RoutineCombat)
        {
            ctx.Units[ctx.UnitIndex].Target = CombatTarget.None;
            ctx.Units[ctx.UnitIndex].EngagedTarget = CombatTarget.None;
            ctx.Units[ctx.UnitIndex].PendingAttack = CombatTarget.None;
            ctx.Units[ctx.UnitIndex].InCombat = false;
        }
    }

    public void Update(ref AIContext ctx)
    {
        EvaluateRoutine(ref ctx);

        switch (ctx.Routine)
        {
            case RoutineIdle:   UpdateIdle(ref ctx); break;
            case RoutineAlert:  UpdateAlert(ref ctx); break;
            case RoutineCombat: UpdateCombat(ref ctx); break;
            case RoutineReturn: UpdateReturn(ref ctx); break;
        }
    }

    private void EvaluateRoutine(ref AIContext ctx)
    {
        byte alert = ctx.AlertState;

        // Alert → enter Alert routine
        if (alert >= (byte)UnitAlertState.Alert && ctx.Routine == RoutineIdle)
        {
            ctx.TransitionTo(RoutineAlert);
            return;
        }

        // Aggressive → enter Combat
        if (alert == (byte)UnitAlertState.Aggressive && ctx.Routine <= RoutineAlert)
        {
            uint threatId = ctx.AlertTarget;
            if (threatId != GameConstants.InvalidUnit)
            {
                ctx.TransitionTo(RoutineCombat, CombatChase);
                ctx.Units[ctx.UnitIndex].Target = CombatTarget.Unit(threatId);
                return;
            }
        }

        // Target dead in combat → return (unless frenzied)
        if (ctx.Routine == RoutineCombat && !SubroutineSteps.IsTargetAlive(ref ctx))
        {
            // Frenzied units search wider and never return to leash
            bool frenzied = ctx.Units[ctx.UnitIndex].Frenzied;
            float range = ctx.Units[ctx.UnitIndex].DetectionRange;
            float searchRange = frenzied ? MathF.Max(range, 30f) : (range > 0 ? range : 12f);
            int next = SubroutineSteps.FindClosestEnemy(ref ctx, searchRange);
            if (next >= 0)
            {
                ctx.Units[ctx.UnitIndex].Target = CombatTarget.Unit(ctx.Units[next].Id);
                ctx.Subroutine = CombatChase;
            }
            else if (!frenzied)
            {
                // OnRoutineExit(Combat) clears Target/EngagedTarget/PendingAttack/InCombat.
                ctx.TransitionTo(RoutineReturn);
                ctx.AlertState = (byte)UnitAlertState.Unaware;
                ctx.AlertTarget = GameConstants.InvalidUnit;
            }
            // else frenzied with no targets: stay in combat routine, will recheck next tick
        }

        // Threat gone → return
        if (alert == (byte)UnitAlertState.Unaware && ctx.Routine == RoutineAlert)
        {
            ctx.TransitionTo(RoutineIdle);
        }
    }

    // ═══════════════════════════════════════

    private void UpdateIdle(ref AIContext ctx)
    {
        if (_archetypeId == ArchetypeRegistry.PatrolSoldier)
            UpdatePatrol(ref ctx);
        else if (_archetypeId == ArchetypeRegistry.GuardStationary)
            UpdateGuard(ref ctx);
        else
            UpdateIdleRoam(ref ctx);
    }

    private static void UpdatePatrol(ref AIContext ctx)
    {
        // Walk to current waypoint, pause, advance. Walk effort, full walk
        // speed — patrol is purposeful but unhurried.
        if (ctx.Subroutine == PatrolWalking)
        {
            SubroutineSteps.SetEffort(ref ctx, MoveEffort.Walk);
            SubroutineSteps.MoveToward(ref ctx, ctx.Units[ctx.UnitIndex].MoveTarget, ctx.MyMaxSpeed);
            if ((ctx.Units[ctx.UnitIndex].MoveTarget - ctx.MyPos).LengthSq() < 2f)
            {
                ctx.Subroutine = PatrolWaiting;
                ctx.SubroutineTimer = 1.5f + (ctx.UnitIndex % 3); // 1.5-3.5s pause at waypoint
                ctx.Units[ctx.UnitIndex].PreferredVel = Vec2.Zero;
            }
        }
        else // PatrolWaiting
        {
            SubroutineSteps.Idle(ref ctx);
            ctx.SubroutineTimer -= ctx.Dt;
            if (ctx.SubroutineTimer <= 0f)
            {
                // Advance to next waypoint
                int routeIdx = ctx.Units[ctx.UnitIndex].PatrolRouteIdx;
                int waypointIdx = ctx.Units[ctx.UnitIndex].PatrolWaypointIdx;
                if (ctx.TriggerSystem != null && routeIdx >= 0 && routeIdx < ctx.TriggerSystem.PatrolRoutes.Count)
                {
                    var route = ctx.TriggerSystem.PatrolRoutes[routeIdx];
                    if (route.Waypoints.Count > 0)
                    {
                        waypointIdx = (waypointIdx + 1) % route.Waypoints.Count;
                        ctx.Units[ctx.UnitIndex].PatrolWaypointIdx = waypointIdx;
                        ctx.Units[ctx.UnitIndex].MoveTarget = route.Waypoints[waypointIdx];
                    }
                }
                ctx.Subroutine = PatrolWalking;
            }
        }
    }

    private static void UpdateGuard(ref AIContext ctx)
    {
        // Stand at spawn position. Walk effort if drifted off, lazier cap
        // (0.5) since guards repositioning shouldn't look hurried.
        float dist = (ctx.MyPos - ctx.Units[ctx.UnitIndex].SpawnPosition).Length();
        if (dist > 1f)
        {
            SubroutineSteps.SetEffort(ref ctx, MoveEffort.Walk, 0.5f);
            SubroutineSteps.MoveToward(ref ctx, ctx.Units[ctx.UnitIndex].SpawnPosition, ctx.MyMaxSpeed);
        }
        else
            ctx.Units[ctx.UnitIndex].PreferredVel = Vec2.Zero;
    }

    private static void UpdateIdleRoam(ref AIContext ctx)
    {
        // Roaming/wandering: Walk effort, half walk speed so it reads as a
        // casual stroll. IdleRoam itself uses ctx.MyMaxSpeed as the cap.
        SubroutineSteps.SetEffort(ref ctx, MoveEffort.Walk, 0.5f);
        SubroutineSteps.IdleRoam(ref ctx, 8f);
    }

    private static void UpdateAlert(ref AIContext ctx)
    {
        SubroutineSteps.AlertStance(ref ctx);
    }

    private static void UpdateCombat(ref AIContext ctx)
    {
        if (!SubroutineSteps.IsTargetAlive(ref ctx)) return; // handled in EvaluateRoutine

        ctx.SubroutineTimer += ctx.Dt;

        if (ctx.Subroutine == CombatChase)
        {
            // Chase = full Sprint effort. Unit ramps from current velocity up
            // to CombatSpeed × sprintMultiplier; the gait picker shows Walk →
            // Jog → Run as velocity catches up.
            SubroutineSteps.SetEffort(ref ctx, MoveEffort.Sprint);
            SubroutineSteps.MoveToTarget(ref ctx);
            int targetIdx = SubroutineSteps.ResolveTarget(ref ctx);
            if (targetIdx >= 0)
            {
                float range = SubroutineSteps.GetMeleeRange(ref ctx, targetIdx);
                if ((ctx.Units[targetIdx].Position - ctx.MyPos).Length() <= range)
                {
                    ctx.Subroutine = CombatAttack;
                    ctx.SubroutineTimer = 0f;
                }
            }
        }
        else // CombatAttack
        {
            SubroutineSteps.AttackTarget(ref ctx);
        }
    }

    private static void UpdateReturn(ref AIContext ctx)
    {
        // Frenzied units don't return — go back to idle/combat search
        if (ctx.Units[ctx.UnitIndex].Frenzied)
        {
            ctx.TransitionTo(RoutineIdle);
            return;
        }

        ctx.Units[ctx.UnitIndex].EngagedTarget = CombatTarget.None;
        ctx.Units[ctx.UnitIndex].InCombat = false;

        Vec2 returnPos = ctx.Units[ctx.UnitIndex].SpawnPosition;
        float dist = (ctx.MyPos - returnPos).Length();
        if (dist > 2f)
        {
            // Effort depends on whether threats are still detected. Awareness
            // state Alert/Aggressive (and a non-Unaware AlertState) means
            // enemies haven't fully cleared yet — retreat under pursuit
            // warrants Sprint. Otherwise it's just a stroll home.
            bool stillThreatened = ctx.AlertState >= (byte)UnitAlertState.Alert;
            if (stillThreatened)
                SubroutineSteps.SetEffort(ref ctx, MoveEffort.Sprint);
            else
                SubroutineSteps.SetEffort(ref ctx, MoveEffort.Walk);
            SubroutineSteps.MoveToward(ref ctx, returnPos, ctx.MyMaxSpeed);
        }
        else
        {
            ctx.TransitionTo(RoutineIdle);
            ctx.Units[ctx.UnitIndex].PreferredVel = Vec2.Zero;
        }
    }

    public string GetRoutineName(byte routine) => routine switch
    {
        RoutineIdle => _archetypeId == ArchetypeRegistry.PatrolSoldier ? "Patrol"
                     : _archetypeId == ArchetypeRegistry.GuardStationary ? "Guard"
                     : "IdleRoaming",
        RoutineAlert => "Alert",
        RoutineCombat => "Combat",
        RoutineReturn => "Return",
        _ => $"Unknown({routine})"
    };

    public string GetSubroutineName(byte routine, byte subroutine) => routine switch
    {
        RoutineIdle when _archetypeId == ArchetypeRegistry.PatrolSoldier => subroutine switch
        {
            PatrolWalking => "PatrolWalking",
            PatrolWaiting => "PatrolWaiting",
            _ => $"Unknown({subroutine})"
        },
        RoutineCombat => subroutine switch
        {
            CombatChase => "CombatChase",
            CombatAttack => "CombatAttack",
            _ => $"Unknown({subroutine})"
        },
        _ => subroutine == 0 ? "Default" : $"Unknown({subroutine})"
    };
}
