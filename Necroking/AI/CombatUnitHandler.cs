using System;
using Necroking.Core;
using Necroking.Lib;
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

    public void OnSpawn(ref AIContext ctx) => SentryTransitions.SpawnAtIdle(ref ctx);

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

    private static void EvaluateRoutine(ref AIContext ctx)
    {
        // Shared sentry ladder (no self-acquire; reacquire falls back to 12u and
        // re-enters the chase subroutine on a new target).
        float range = ctx.Units[ctx.UnitIndex].DetectionRange;
        var cfg = new SentryConfig(
            selfAcquireRange: 0f,
            reacquireRange: range > 0 ? range : 12f,
            reacquireResetsSubroutine: true); // Subroutine 0 == CombatChase
        SentryTransitions.EvaluateSentryRoutine(ref ctx, cfg);
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
        if (!SubroutineSteps.IsTargetAlive(ref ctx))
        {
            // Reacquire is handled in EvaluateRoutine; stop here so a frenzied
            // unit (kept in Combat with no target) doesn't coast on stale
            // PreferredVel until something re-enters range.
            SubroutineSteps.SetIdle(ref ctx);
            return;
        }

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

    private static void UpdateReturn(ref AIContext ctx) => SentryTransitions.UpdateReturn(ref ctx);

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
