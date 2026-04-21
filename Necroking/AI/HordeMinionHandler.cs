using Necroking.Core;
using Necroking.GameSystems;

namespace Necroking.AI;

/// <summary>
/// Horde minion AI archetype: undead units that follow the necromancer's formation.
///
/// Routines:
///   0 = Following    — move to assigned horde formation slot
///   1 = Chasing      — pursuing an enemy assigned by horde system
///   2 = Engaged      — in melee combat with target
///   3 = Returning    — pathfinding back to formation after combat
///   4 = Commanded    — attack-move to a target point, fight enemies there, auto-return when clear or timeout
///
/// The HordeSystem still manages formation geometry (slot positions, circle center).
/// This handler reads horde state and drives unit movement/combat accordingly.
/// Targets are acquired either from horde chasing assignments or direct enemy detection.
/// </summary>
public class HordeMinionHandler : IArchetypeHandler
{
    private const byte RoutineFollowing = 0;
    private const byte RoutineChasing = 1;
    private const byte RoutineEngaged = 2;
    private const byte RoutineReturning = 3;
    private const byte RoutineCommanded = 4;

    // Following subroutines
    private const byte FollowMoving = 0;  // actively moving toward slot
    private const byte FollowIdle = 1;    // arrived at slot, waiting for slot to drift >1.5 before moving again

    private const float FollowDeadzone = 1.5f;  // don't issue new move until slot is this far away
    private const float FollowArriveThreshold = 0.5f; // close enough to count as "arrived"

    private const float CommandTimeout = 45f;
    private const float CommandClearRadius = 10f;

    public void OnSpawn(ref AIContext ctx)
    {
        ctx.Routine = RoutineFollowing;
        ctx.Subroutine = 0;
        ctx.SubroutineTimer = 0f;

        // Auto-enroll in horde
        ctx.Horde?.AddUnit(ctx.MyId);
    }

    public void Update(ref AIContext ctx)
    {
        if (ctx.Horde == null) return;

        // Sync with horde system state
        var hordeState = ctx.Horde.GetUnitState(ctx.MyId);
        SyncHordeState(ref ctx, hordeState);

        // Amortize only low-urgency routines: Following drifts to a slot and
        // Returning walks back after combat — neither reacts to per-frame
        // events. Chasing/Engaged/Commanded stay every-frame so combat and
        // player orders respond instantly. On skipped frames the unit keeps
        // its previous PreferredVel; ORCA still runs, so it keeps moving.
        bool lowUrgency = ctx.Routine == RoutineFollowing || ctx.Routine == RoutineReturning;
        if (lowUrgency && !ctx.IsAmortizeTick) return;

        // Execute current routine
        switch (ctx.Routine)
        {
            case RoutineFollowing: UpdateFollowing(ref ctx); break;
            case RoutineChasing:   UpdateChasing(ref ctx); break;
            case RoutineEngaged:   UpdateEngaged(ref ctx); break;
            case RoutineReturning: UpdateReturning(ref ctx); break;
            case RoutineCommanded: UpdateCommanded(ref ctx); break;
        }
    }

    /// <summary>Sync our routine with the HordeSystem's state assignments.</summary>
    private static void SyncHordeState(ref AIContext ctx, HordeUnitState hordeState)
    {
        // Don't override commanded units with horde assignments
        if (ctx.Routine == RoutineCommanded) return;

        // Horde assigns Chasing with a target — pick it up
        if (hordeState == HordeUnitState.Chasing && ctx.Routine != RoutineChasing && ctx.Routine != RoutineEngaged)
        {
            uint chasingId = ctx.Horde!.GetChasingTarget(ctx.MyId);
            if (chasingId != GameConstants.InvalidUnit)
            {
                ctx.Units[ctx.UnitIndex].Target = CombatTarget.Unit(chasingId);
                ctx.Routine = RoutineChasing;
                ctx.Subroutine = 0;
                DebugLog.Log("horde_aggro",
                    $"  [Minion {ctx.MyId}] accepted horde Chasing assignment → target={chasingId}");
            }
        }

        // If horde says Returning but we're still fighting, let combat finish
        if (hordeState == HordeUnitState.Returning && ctx.Routine == RoutineChasing)
        {
            if (!SubroutineSteps.IsTargetAlive(ref ctx))
            {
                ctx.Routine = RoutineReturning;
                ctx.Units[ctx.UnitIndex].Target = CombatTarget.None;
                ctx.Units[ctx.UnitIndex].EngagedTarget = CombatTarget.None;
                ctx.Units[ctx.UnitIndex].InCombat = false;
            }
        }
    }

    private static void UpdateFollowing(ref AIContext ctx)
    {
        // If a target is already set (e.g. DamageSystem assigns Target=attacker
        // on hit for units whose EngagedTarget was None) and it's alive, switch
        // to Chasing so we actually fight back. Without this, a following minion
        // hit by a wolf would keep following its slot — target set but ignored.
        if (SubroutineSteps.IsTargetAlive(ref ctx))
        {
            ctx.Routine = RoutineChasing;
            ctx.Subroutine = 0;
            DebugLog.Log("horde_aggro",
                $"  [Minion {ctx.MyId}] UpdateFollowing saw live Target → Chasing " +
                $"(target id={ctx.Units[ctx.UnitIndex].Target.UnitID})");
            return;
        }

        // No target — scan for enemies within engagement range.
        {
            float engageRange = ctx.Horde?.Settings.EngagementRange ?? 10f;
            int enemy = SubroutineSteps.FindClosestEnemy(ref ctx, engageRange);
            if (enemy >= 0)
            {
                ctx.Units[ctx.UnitIndex].Target = CombatTarget.Unit(ctx.Units[enemy].Id);
                ctx.Routine = RoutineChasing;
                ctx.Subroutine = 0;
                DebugLog.Log("horde_aggro",
                    $"  [Minion {ctx.MyId}] self-aggro (UpdateFollowing) → enemy id={ctx.Units[enemy].Id} " +
                    $"dist={(ctx.Units[enemy].Position - ctx.MyPos).Length():F1} range={engageRange:F1}");
                return;
            }
        }

        // Follow horde slot position with deadzone to prevent stuttering
        if (ctx.Horde != null && ctx.Horde.GetTargetPosition(ctx.MyId, out var slotPos))
        {
            float dist = (ctx.MyPos - slotPos).Length();

            if (ctx.Subroutine == FollowIdle)
            {
                // Idle at slot — only start moving again if slot drifted far enough.
                // SetIdle() zeros PreferredVel *and* sets RoutineAnim=Idle so the
                // AnimResolver stops picking whatever locomotion state MoveToward
                // left behind the last time the unit was chasing the slot.
                SubroutineSteps.SetIdle(ref ctx);
                if (dist > FollowDeadzone)
                    ctx.Subroutine = FollowMoving;
            }
            else
            {
                // Actively moving toward slot. MoveToward sets PreferredVel and the
                // Walk/Jog/Run locomotion anim based on intended speed.
                if (dist > FollowArriveThreshold)
                {
                    SubroutineSteps.MoveToward(ref ctx, slotPos, ctx.MySpeed);
                }
                else
                {
                    // Arrived — flip the routine anim to Idle right away (critical,
                    // otherwise the last Walk/Jog/Run request sticks and the unit
                    // walks-in-place until the next chase).
                    SubroutineSteps.SetIdle(ref ctx);
                    ctx.Subroutine = FollowIdle;
                }
            }
        }
        else
        {
            SubroutineSteps.SetIdle(ref ctx);
        }
    }

    private static void UpdateChasing(ref AIContext ctx)
    {
        if (!SubroutineSteps.IsTargetAlive(ref ctx))
        {
            // Target dead — return to formation
            ctx.Routine = RoutineReturning;
            ctx.Units[ctx.UnitIndex].Target = CombatTarget.None;
            ctx.Units[ctx.UnitIndex].EngagedTarget = CombatTarget.None;
            return;
        }

        int targetIdx = SubroutineSteps.ResolveTarget(ref ctx);
        if (targetIdx >= 0)
        {
            SubroutineSteps.MoveToward(ref ctx, ctx.Units[targetIdx].Position, ctx.MySpeed);

            // Auto-engage when in melee range
            float dist = (ctx.Units[targetIdx].Position - ctx.MyPos).Length();
            float engageRange = SubroutineSteps.GetMeleeRange(ref ctx, targetIdx);
            if (dist <= engageRange)
            {
                ctx.Routine = RoutineEngaged;
                ctx.Units[ctx.UnitIndex].EngagedTarget = ctx.Units[ctx.UnitIndex].Target;
            }
        }
    }

    private static void UpdateEngaged(ref AIContext ctx)
    {
        bool frenzied = ctx.Units[ctx.UnitIndex].Frenzied;

        if (!SubroutineSteps.IsTargetAlive(ref ctx))
        {
            // Frenzied: search for new target instead of returning
            if (frenzied)
            {
                int next = SubroutineSteps.FindClosestEnemy(ref ctx, 30f);
                if (next >= 0)
                {
                    ctx.Units[ctx.UnitIndex].Target = CombatTarget.Unit(ctx.Units[next].Id);
                    ctx.Routine = RoutineChasing;
                }
                // else no enemies: stay idle, will recheck
            }
            else
            {
                ctx.Routine = RoutineReturning;
                ctx.Units[ctx.UnitIndex].Target = CombatTarget.None;
                ctx.Units[ctx.UnitIndex].EngagedTarget = CombatTarget.None;
                ctx.Units[ctx.UnitIndex].InCombat = false;
            }
            return;
        }

        // Stay near target, let combat system handle attacks
        SubroutineSteps.AttackTarget(ref ctx);

        // Leash check — frenzied units ignore leash
        if (!frenzied && ctx.Horde != null)
        {
            float leashRadius = ctx.Horde.Settings.LeashRadius;
            float distToCenter = (ctx.MyPos - ctx.Horde.CircleCenter).Length();
            if (distToCenter > leashRadius * 1.5f)
            {
                ctx.Routine = RoutineReturning;
                ctx.Units[ctx.UnitIndex].Target = CombatTarget.None;
                ctx.Units[ctx.UnitIndex].EngagedTarget = CombatTarget.None;
                ctx.Units[ctx.UnitIndex].InCombat = false;
            }
        }
    }

    private static void UpdateReturning(ref AIContext ctx)
    {
        ctx.Units[ctx.UnitIndex].Target = CombatTarget.None;
        ctx.Units[ctx.UnitIndex].EngagedTarget = CombatTarget.None;
        ctx.Units[ctx.UnitIndex].InCombat = false;
        ctx.Units[ctx.UnitIndex].PendingAttack = CombatTarget.None;

        if (ctx.Horde != null && ctx.Horde.GetTargetPosition(ctx.MyId, out var slotPos))
        {
            float dist = (ctx.MyPos - slotPos).Length();
            if (dist > 1.5f)
                SubroutineSteps.MoveToward(ref ctx, slotPos, ctx.MySpeed * (ctx.Horde.Settings.ReturnSpeedMult));
            else
            {
                ctx.Routine = RoutineFollowing;
                ctx.Subroutine = FollowIdle;
                ctx.Units[ctx.UnitIndex].PreferredVel = Vec2.Zero;
            }
        }
        else
        {
            ctx.Routine = RoutineFollowing;
            ctx.Subroutine = FollowMoving;
            ctx.Units[ctx.UnitIndex].PreferredVel = Vec2.Zero;
        }
    }

    private static void UpdateCommanded(ref AIContext ctx)
    {
        ctx.SubroutineTimer += ctx.Dt;
        Vec2 commandTarget = ctx.Units[ctx.UnitIndex].MoveTarget;

        // Timeout — return to horde
        if (ctx.SubroutineTimer > CommandTimeout)
        {
            ReturnFromCommand(ref ctx);
            return;
        }

        // If we have a combat target, fight it
        if (SubroutineSteps.IsTargetAlive(ref ctx))
        {
            int targetIdx = SubroutineSteps.ResolveTarget(ref ctx);
            if (targetIdx >= 0)
            {
                float dist = (ctx.Units[targetIdx].Position - ctx.MyPos).Length();
                float meleeRange = SubroutineSteps.GetMeleeRange(ref ctx, targetIdx);
                if (dist <= meleeRange)
                {
                    ctx.Units[ctx.UnitIndex].EngagedTarget = ctx.Units[ctx.UnitIndex].Target;
                    SubroutineSteps.AttackTarget(ref ctx);
                }
                else
                {
                    SubroutineSteps.MoveToward(ref ctx, ctx.Units[targetIdx].Position, ctx.MySpeed);
                }
            }
            return;
        }

        // No current target — are we at the command point?
        float distToTarget = (ctx.MyPos - commandTarget).Length();
        if (distToTarget > 2f)
        {
            // Still moving to command point
            ctx.Units[ctx.UnitIndex].Target = CombatTarget.None;
            ctx.Units[ctx.UnitIndex].EngagedTarget = CombatTarget.None;
            SubroutineSteps.MoveToward(ref ctx, commandTarget, ctx.MySpeed);
        }
        else
        {
            // At command point — look for enemies nearby
            int enemy = SubroutineSteps.FindClosestEnemy(ref ctx, CommandClearRadius);
            if (enemy >= 0)
            {
                ctx.Units[ctx.UnitIndex].Target = CombatTarget.Unit(ctx.Units[enemy].Id);
            }
            else
            {
                // Area is clear — return to horde
                ReturnFromCommand(ref ctx);
            }
        }
    }

    private static void ReturnFromCommand(ref AIContext ctx)
    {
        ctx.Routine = RoutineReturning;
        ctx.SubroutineTimer = 0f;
        ctx.Units[ctx.UnitIndex].Target = CombatTarget.None;
        ctx.Units[ctx.UnitIndex].EngagedTarget = CombatTarget.None;
        ctx.Units[ctx.UnitIndex].InCombat = false;
    }

    public string GetRoutineName(byte routine) => routine switch
    {
        RoutineFollowing => "Following",
        RoutineChasing => "Chasing",
        RoutineEngaged => "Engaged",
        RoutineReturning => "Returning",
        RoutineCommanded => "Commanded",
        _ => $"Unknown({routine})"
    };

    public string GetSubroutineName(byte routine, byte subroutine) =>
        subroutine == 0 ? "Default" : $"Unknown({subroutine})";
}
