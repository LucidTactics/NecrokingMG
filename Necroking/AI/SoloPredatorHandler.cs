using System;
using Necroking.Core;
using Necroking.Movement;

namespace Necroking.AI;

/// <summary>
/// Solo predator handler — un-packed hunting animals (dire wolves, juvenile
/// wolves, boars, bears). Migrated from the legacy AIBehavior.WolfHitAndRun /
/// WolfOpportunist brain (Simulation.UpdateWolfAI): engage → bite → disengage
/// a couple units → wait out the attack cooldown (circling, which reads as
/// emergent flanking with several predators) → re-engage.
///
/// Two registered flavors share this handler:
///   SoloPredator   — hit-and-run: attacks as soon as it's in melee range.
///   AmbushPredator — opportunist: in range, it circles and waits for the
///                    target to face ≥100° away (or one cooldown's timeout)
///                    before committing. Bears.
///
/// Routines:
///   0 = IdleRoaming — wander near spawn
///   1 = Alert       — noticed threat
///   2 = Combat      — the hit-and-run cycle (subroutines below)
///   3 = Return      — go back to spawn
///
/// Combat subroutines mirror the legacy WolfPhase states:
///   0 = Engage, 1 = Attacking, 2 = Disengage, 3 = WaitCooldown
///
/// The melee attack itself runs through the normal combat queue via
/// EngagedTarget (stamped in Engage→Attacking); Disengage/WaitCooldown
/// force-clear EngagedTarget/PendingAttack every tick — the legacy preamble
/// had an explicit bypass so a disengaging wolf is never planted by a stale
/// queued attack, and this preserves it.
/// </summary>
public class SoloPredatorHandler : IArchetypeHandler
{
    private const byte RoutineIdle = 0;
    private const byte RoutineAlert = 1;
    private const byte RoutineCombat = 2;
    private const byte RoutineReturn = 3;

    // Combat subroutines (legacy WolfPhase states)
    private const byte SubEngage = 0;
    private const byte SubAttacking = 1;
    private const byte SubDisengage = 2;
    private const byte SubWaitCooldown = 3;

    private const float AggroRange = 10f;       // legacy WolfAggroRange
    private const float AggroBreakRange = 15f;  // legacy WolfAggroBreakRange
    private const float OpportunistAngle = 100f;

    private readonly bool _opportunist;

    public SoloPredatorHandler(bool opportunist) { _opportunist = opportunist; }

    public void OnSpawn(ref AIContext ctx)
    {
        ctx.Units[ctx.UnitIndex].SpawnPosition = ctx.MyPos;
        ctx.Routine = RoutineIdle;
        ctx.Subroutine = 0;
        ctx.SubroutineTimer = 0f;
    }

    public void OnRoutineExit(ref AIContext ctx, byte oldRoutine, byte newRoutine)
    {
        if (oldRoutine == RoutineCombat)
        {
            ctx.Units[ctx.UnitIndex].Target = CombatTarget.None;
            ctx.Units[ctx.UnitIndex].EngagedTarget = CombatTarget.None;
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
        byte alert = ctx.AlertState;

        if (alert >= (byte)UnitAlertState.Alert && ctx.Routine == RoutineIdle)
        {
            ctx.TransitionTo(RoutineAlert);
            return;
        }

        if (alert == (byte)UnitAlertState.Aggressive && ctx.Routine <= RoutineAlert)
        {
            if (ctx.AlertTarget != GameConstants.InvalidUnit)
            {
                ctx.TransitionTo(RoutineCombat);
                ctx.Units[ctx.UnitIndex].Target = CombatTarget.Unit(ctx.AlertTarget);
                return;
            }
        }

        // Self-acquire: legacy wolves aggroed anything inside WolfAggroRange
        // regardless of awareness state.
        if (ctx.Routine <= RoutineAlert)
        {
            int enemy = SubroutineSteps.FindClosestEnemy(ref ctx, AggroRange);
            if (enemy >= 0)
            {
                ctx.TransitionTo(RoutineCombat);
                ctx.Units[ctx.UnitIndex].Target = CombatTarget.Unit(ctx.Units[enemy].Id);
                return;
            }
        }

        if (ctx.Routine == RoutineCombat && !SubroutineSteps.IsTargetAlive(ref ctx))
        {
            // Target died — reacquire in aggro range or go home.
            int next = SubroutineSteps.FindClosestEnemy(ref ctx, AggroRange);
            if (next >= 0)
            {
                ctx.Units[ctx.UnitIndex].Target = CombatTarget.Unit(ctx.Units[next].Id);
                ctx.Subroutine = SubEngage;
                ctx.SubroutineTimer = 0f;
            }
            else
            {
                // OnRoutineExit(Combat) clears Target/EngagedTarget.
                ctx.TransitionTo(RoutineReturn);
                ctx.AlertState = (byte)UnitAlertState.Unaware;
                ctx.AlertTarget = GameConstants.InvalidUnit;
            }
        }

        if (alert == (byte)UnitAlertState.Unaware && ctx.Routine == RoutineAlert)
        {
            ctx.TransitionTo(RoutineIdle);
        }
    }

    private static void UpdateIdle(ref AIContext ctx)
    {
        SubroutineSteps.SetEffort(ref ctx, MoveEffort.Walk, 0.5f);
        SubroutineSteps.IdleRoam(ref ctx, 8f);
    }

    private static void UpdateAlert(ref AIContext ctx)
    {
        SubroutineSteps.AlertStance(ref ctx);
    }

    private void UpdateCombat(ref AIContext ctx)
    {
        int i = ctx.UnitIndex;
        int targetIdx = SubroutineSteps.ResolveTarget(ref ctx);
        if (targetIdx < 0) return;

        Vec2 myPos = ctx.MyPos;
        Vec2 targetPos = ctx.Units[targetIdx].Position;
        float dist = (targetPos - myPos).Length();

        // Drop the target beyond break range — go home.
        if (dist > AggroBreakRange)
        {
            ctx.TransitionTo(RoutineReturn);  // exit hook clears Target/EngagedTarget
            return;
        }

        float attackRange = SubroutineSteps.GetMeleeRange(ref ctx, targetIdx);
        float disengageDist = attackRange + 2f;  // back off 2 units beyond attack range
        float attackCooldown = ctx.Units[i].AttackCooldown;

        switch (ctx.Subroutine)
        {
            case SubEngage:
            {
                if (dist <= attackRange)
                {
                    if (_opportunist
                        && !IsTargetFacingAway(ref ctx, targetIdx, OpportunistAngle)
                        && ctx.SubroutineTimer <= attackCooldown)
                    {
                        // Wait for the opening, circling at the edge of range.
                        ctx.SubroutineTimer += ctx.Dt;
                        Vec2 perp = new Vec2(-(targetPos.Y - myPos.Y), targetPos.X - myPos.X);
                        if (perp.LengthSq() > 0.01f) perp = perp.Normalized();
                        ctx.Units[i].PreferredVel = perp * ctx.Units[i].MaxSpeed * 0.5f;
                    }
                    else
                    {
                        // Commit: the combat queue takes it from here.
                        ctx.Subroutine = SubAttacking;
                        ctx.SubroutineTimer = 0f;
                        ctx.Units[i].EngagedTarget = ctx.Units[i].Target;
                    }
                }
                else
                {
                    SubroutineSteps.SetEffort(ref ctx, MoveEffort.Sprint);
                    SubroutineSteps.MoveToward(ref ctx, targetPos, ctx.MyMaxSpeed);
                    if (_opportunist) ctx.SubroutineTimer += ctx.Dt;
                }
                break;
            }

            case SubAttacking:
            {
                // Once the attack cooldown starts (we just bit) → disengage.
                if (attackCooldown > 0f && ctx.SubroutineTimer > 0.2f)
                {
                    ctx.Subroutine = SubDisengage;
                    ctx.SubroutineTimer = 0f;
                    ctx.Units[i].EngagedTarget = CombatTarget.None;
                }
                else
                {
                    ctx.SubroutineTimer += ctx.Dt;
                    if (dist > attackRange * 1.5f)
                    {
                        SubroutineSteps.SetEffort(ref ctx, MoveEffort.Sprint);
                        SubroutineSteps.MoveToward(ref ctx, targetPos, ctx.MyMaxSpeed);
                    }
                    else
                        ctx.Units[i].PreferredVel = Vec2.Zero;
                }
                break;
            }

            case SubDisengage:
            {
                // Force-clear engagement and lockout every tick — the predator
                // must move NOW, never planted by a stale queued attack.
                ctx.Units[i].EngagedTarget = CombatTarget.None;
                ctx.Units[i].PendingAttack = CombatTarget.None;
                ctx.Units[i].PostAttackTimer = 0f;

                if (dist < disengageDist)
                {
                    Vec2 awayDir = dist > 0.01f ? (myPos - targetPos) * (1f / dist) : new Vec2(1, 0);
                    ctx.Units[i].PreferredVel = awayDir * ctx.Units[i].MaxSpeed;
                }
                else
                {
                    ctx.Subroutine = SubWaitCooldown;
                    ctx.SubroutineTimer = 0f;
                    ctx.Units[i].PreferredVel = Vec2.Zero;
                }
                break;
            }

            case SubWaitCooldown:
            {
                // Keep engagement clear but KEEP the target.
                ctx.Units[i].EngagedTarget = CombatTarget.None;
                ctx.Units[i].PendingAttack = CombatTarget.None;

                if (dist < disengageDist - 0.5f)
                {
                    Vec2 awayDir = dist > 0.01f ? (myPos - targetPos) * (1f / dist) : new Vec2(1, 0);
                    ctx.Units[i].PreferredVel = awayDir * ctx.Units[i].MaxSpeed * 0.5f;
                }
                else if (attackCooldown <= 0f)
                {
                    ctx.Subroutine = SubEngage;
                    ctx.SubroutineTimer = 0f;
                }
                else
                {
                    // Circle while waiting (emergent flanking with several predators).
                    Vec2 toTarget = targetPos - myPos;
                    Vec2 perp = new Vec2(-toTarget.Y, toTarget.X);
                    if (perp.LengthSq() > 0.01f) perp = perp.Normalized();
                    ctx.Units[i].PreferredVel = perp * ctx.Units[i].MaxSpeed * 0.3f;
                }
                break;
            }
        }
    }

    private static void UpdateReturn(ref AIContext ctx)
    {
        ctx.Units[ctx.UnitIndex].EngagedTarget = CombatTarget.None;
        ctx.Units[ctx.UnitIndex].InCombat = false;

        Vec2 returnPos = ctx.Units[ctx.UnitIndex].SpawnPosition;
        if ((ctx.MyPos - returnPos).Length() > 2f)
        {
            bool stillThreatened = ctx.AlertState >= (byte)UnitAlertState.Alert;
            SubroutineSteps.SetEffort(ref ctx, stillThreatened ? MoveEffort.Sprint : MoveEffort.Walk);
            SubroutineSteps.MoveToward(ref ctx, returnPos, ctx.MyMaxSpeed);
        }
        else
        {
            ctx.TransitionTo(RoutineIdle);
            ctx.Units[ctx.UnitIndex].PreferredVel = Vec2.Zero;
        }
    }

    /// <summary>Is the target facing more than angleDeg away from this unit?
    /// (Migrated from Simulation.IsTargetFacingAway.)</summary>
    private static bool IsTargetFacingAway(ref AIContext ctx, int targetIdx, float angleDeg)
    {
        Vec2 targetFacingDir = Movement.FacingUtil.ForwardDir(ctx.Units[targetIdx]);
        Vec2 toUs = ctx.MyPos - ctx.Units[targetIdx].Position;
        if (toUs.LengthSq() < 0.01f) return false;
        toUs = toUs.Normalized();
        float dot = targetFacingDir.X * toUs.X + targetFacingDir.Y * toUs.Y;
        float angleRad = angleDeg * MathF.PI / 180f;
        return dot < MathF.Cos(angleRad);
    }

    public string GetRoutineName(byte routine) => routine switch
    {
        RoutineIdle => "IdleRoaming",
        RoutineAlert => "Alert",
        RoutineCombat => "Combat",
        RoutineReturn => "Return",
        _ => $"Unknown({routine})"
    };

    public string GetSubroutineName(byte routine, byte subroutine) => routine switch
    {
        RoutineCombat => subroutine switch
        {
            SubEngage => "Engage",
            SubAttacking => "Attacking",
            SubDisengage => "Disengage",
            SubWaitCooldown => "WaitCooldown",
            _ => $"Unknown({subroutine})"
        },
        _ => subroutine == 0 ? "Default" : $"Unknown({subroutine})"
    };
}
