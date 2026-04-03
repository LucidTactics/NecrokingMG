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

        // Execute current routine
        switch (ctx.Routine)
        {
            case RoutineFollowing: UpdateFollowing(ref ctx); break;
            case RoutineChasing:   UpdateChasing(ref ctx); break;
            case RoutineEngaged:   UpdateEngaged(ref ctx); break;
            case RoutineReturning: UpdateReturning(ref ctx); break;
        }
    }

    /// <summary>Sync our routine with the HordeSystem's state assignments.</summary>
    private static void SyncHordeState(ref AIContext ctx, HordeUnitState hordeState)
    {
        // Horde assigns Chasing with a target — pick it up
        if (hordeState == HordeUnitState.Chasing && ctx.Routine != RoutineChasing && ctx.Routine != RoutineEngaged)
        {
            uint chasingId = ctx.Horde!.GetChasingTarget(ctx.MyId);
            if (chasingId != GameConstants.InvalidUnit)
            {
                ctx.Units.Target[ctx.UnitIndex] = CombatTarget.Unit(chasingId);
                ctx.Routine = RoutineChasing;
                ctx.Subroutine = 0;
            }
        }

        // If horde says Returning but we're still fighting, let combat finish
        if (hordeState == HordeUnitState.Returning && ctx.Routine == RoutineChasing)
        {
            if (!SubroutineSteps.IsTargetAlive(ref ctx))
            {
                ctx.Routine = RoutineReturning;
                ctx.Units.Target[ctx.UnitIndex] = CombatTarget.None;
                ctx.Units.EngagedTarget[ctx.UnitIndex] = CombatTarget.None;
                ctx.Units.InCombat[ctx.UnitIndex] = false;
            }
        }
    }

    private static void UpdateFollowing(ref AIContext ctx)
    {
        // Acquire targets within engagement range
        if (!SubroutineSteps.IsTargetAlive(ref ctx))
        {
            float engageRange = ctx.Horde?.Settings.EngagementRange ?? 10f;
            int enemy = SubroutineSteps.FindClosestEnemy(ref ctx, engageRange);
            if (enemy >= 0)
            {
                ctx.Units.Target[ctx.UnitIndex] = CombatTarget.Unit(ctx.Units.Id[enemy]);
                ctx.Routine = RoutineChasing;
                return;
            }
        }

        // Follow horde slot position
        if (ctx.Horde != null && ctx.Horde.GetTargetPosition(ctx.MyId, out var slotPos))
        {
            float dist = (ctx.MyPos - slotPos).Length();
            if (dist > 0.5f)
                SubroutineSteps.MoveToward(ref ctx, slotPos, ctx.MySpeed);
            else
                ctx.Units.PreferredVel[ctx.UnitIndex] = Vec2.Zero;
        }
        else
            ctx.Units.PreferredVel[ctx.UnitIndex] = Vec2.Zero;
    }

    private static void UpdateChasing(ref AIContext ctx)
    {
        if (!SubroutineSteps.IsTargetAlive(ref ctx))
        {
            // Target dead — return to formation
            ctx.Routine = RoutineReturning;
            ctx.Units.Target[ctx.UnitIndex] = CombatTarget.None;
            ctx.Units.EngagedTarget[ctx.UnitIndex] = CombatTarget.None;
            return;
        }

        int targetIdx = SubroutineSteps.ResolveTarget(ref ctx);
        if (targetIdx >= 0)
        {
            SubroutineSteps.MoveToward(ref ctx, ctx.Units.Position[targetIdx], ctx.MySpeed);

            // Auto-engage when in melee range
            float dist = (ctx.Units.Position[targetIdx] - ctx.MyPos).Length();
            float engageRange = SubroutineSteps.GetMeleeRange(ref ctx, targetIdx);
            if (dist <= engageRange)
            {
                ctx.Routine = RoutineEngaged;
                ctx.Units.EngagedTarget[ctx.UnitIndex] = ctx.Units.Target[ctx.UnitIndex];
            }
        }
    }

    private static void UpdateEngaged(ref AIContext ctx)
    {
        if (!SubroutineSteps.IsTargetAlive(ref ctx))
        {
            ctx.Routine = RoutineReturning;
            ctx.Units.Target[ctx.UnitIndex] = CombatTarget.None;
            ctx.Units.EngagedTarget[ctx.UnitIndex] = CombatTarget.None;
            ctx.Units.InCombat[ctx.UnitIndex] = false;
            return;
        }

        // Stay near target, let combat system handle attacks
        SubroutineSteps.AttackTarget(ref ctx);

        // Leash check
        if (ctx.Horde != null)
        {
            float leashRadius = ctx.Horde.Settings.LeashRadius;
            float distToCenter = (ctx.MyPos - ctx.Horde.CircleCenter).Length();
            if (distToCenter > leashRadius * 1.5f)
            {
                ctx.Routine = RoutineReturning;
                ctx.Units.Target[ctx.UnitIndex] = CombatTarget.None;
                ctx.Units.EngagedTarget[ctx.UnitIndex] = CombatTarget.None;
                ctx.Units.InCombat[ctx.UnitIndex] = false;
            }
        }
    }

    private static void UpdateReturning(ref AIContext ctx)
    {
        ctx.Units.Target[ctx.UnitIndex] = CombatTarget.None;
        ctx.Units.EngagedTarget[ctx.UnitIndex] = CombatTarget.None;
        ctx.Units.InCombat[ctx.UnitIndex] = false;
        ctx.Units.PendingAttack[ctx.UnitIndex] = CombatTarget.None;

        if (ctx.Horde != null && ctx.Horde.GetTargetPosition(ctx.MyId, out var slotPos))
        {
            float dist = (ctx.MyPos - slotPos).Length();
            if (dist > 1.5f)
                SubroutineSteps.MoveToward(ref ctx, slotPos, ctx.MySpeed * (ctx.Horde.Settings.ReturnSpeedMult));
            else
            {
                ctx.Routine = RoutineFollowing;
                ctx.Units.PreferredVel[ctx.UnitIndex] = Vec2.Zero;
            }
        }
        else
        {
            ctx.Routine = RoutineFollowing;
            ctx.Units.PreferredVel[ctx.UnitIndex] = Vec2.Zero;
        }
    }
}
