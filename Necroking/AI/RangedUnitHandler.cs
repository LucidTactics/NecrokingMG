using System;
using Necroking.Core;

namespace Necroking.AI;

/// <summary>
/// Ranged unit handler for Archers and Casters.
/// Maintains distance from enemies while attacking. Uses spell/ranged attacks.
///
/// Routines:
///   0 = IdleRoaming  — wander near spawn at 30% speed
///   1 = Alert        — noticed threat
///   2 = Combat       — attack from range, kite if enemy gets close
///   3 = Return       — go back to position
///
/// Archers try to maintain distance; casters stand and cast.
/// Both fall back to melee if cornered.
/// </summary>
public class RangedUnitHandler : IArchetypeHandler
{
    private const byte RoutineIdle = 0;
    private const byte RoutineAlert = 1;
    private const byte RoutineCombat = 2;
    private const byte RoutineReturn = 3;

    private const float PreferredRange = 8f; // ideal distance to target
    private const float TooCloseRange = 4f;  // back away if closer

    private readonly byte _archetypeId;

    public RangedUnitHandler(byte archetypeId) { _archetypeId = archetypeId; }

    public void OnSpawn(ref AIContext ctx)
    {
        ctx.Units[ctx.UnitIndex].SpawnPosition = ctx.MyPos;
        ctx.Routine = RoutineIdle;
        ctx.Subroutine = 0;
        ctx.SubroutineTimer = 0f;
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
            ctx.Routine = RoutineAlert;
            ctx.SubroutineTimer = 0f;
            return;
        }

        if (alert == (byte)UnitAlertState.Aggressive && ctx.Routine <= RoutineAlert)
        {
            if (ctx.AlertTarget != GameConstants.InvalidUnit)
            {
                ctx.Units[ctx.UnitIndex].Target = CombatTarget.Unit(ctx.AlertTarget);
                ctx.Routine = RoutineCombat;
                ctx.SubroutineTimer = 0f;
                return;
            }
        }

        if (ctx.Routine == RoutineCombat && !SubroutineSteps.IsTargetAlive(ref ctx))
        {
            float range = ctx.Units[ctx.UnitIndex].DetectionRange;
            int next = SubroutineSteps.FindClosestEnemy(ref ctx, range > 0 ? range : 15f);
            if (next >= 0)
                ctx.Units[ctx.UnitIndex].Target = CombatTarget.Unit(ctx.Units[next].Id);
            else
            {
                ctx.Routine = RoutineReturn;
                ctx.Units[ctx.UnitIndex].Target = CombatTarget.None;
                ctx.AlertState = (byte)UnitAlertState.Unaware;
                ctx.AlertTarget = GameConstants.InvalidUnit;
            }
        }

        if (alert == (byte)UnitAlertState.Unaware && ctx.Routine == RoutineAlert)
        {
            ctx.Routine = RoutineIdle;
            ctx.SubroutineTimer = 0f;
        }
    }

    private static void UpdateIdle(ref AIContext ctx)
    {
        SubroutineSteps.IdleRoam(ref ctx, 6f);
    }

    private static void UpdateAlert(ref AIContext ctx)
    {
        SubroutineSteps.AlertStance(ref ctx);
    }

    private static void UpdateCombat(ref AIContext ctx)
    {
        int targetIdx = SubroutineSteps.ResolveTarget(ref ctx);
        if (targetIdx < 0) return;

        float dist = (ctx.Units[targetIdx].Position - ctx.MyPos).Length();

        if (dist < TooCloseRange)
        {
            // Too close — back away
            SubroutineSteps.MoveAwayFrom(ref ctx, ctx.Units[targetIdx].Position, PreferredRange);
        }
        else if (dist > PreferredRange + 2f)
        {
            // Too far — approach
            SubroutineSteps.MoveToward(ref ctx, ctx.Units[targetIdx].Position, ctx.MySpeed * 0.6f);
        }
        else
        {
            // Good range — stand and fight
            ctx.Units[ctx.UnitIndex].PreferredVel = Vec2.Zero;
            if (ctx.Units[ctx.UnitIndex].EngagedTarget.IsNone)
                ctx.Units[ctx.UnitIndex].EngagedTarget = ctx.Units[ctx.UnitIndex].Target;
        }
    }

    private static void UpdateReturn(ref AIContext ctx)
    {
        ctx.Units[ctx.UnitIndex].EngagedTarget = CombatTarget.None;
        ctx.Units[ctx.UnitIndex].InCombat = false;

        Vec2 returnPos = ctx.Units[ctx.UnitIndex].SpawnPosition;
        if ((ctx.MyPos - returnPos).Length() > 2f)
            SubroutineSteps.MoveToward(ref ctx, returnPos, ctx.MySpeed * 0.5f);
        else
        {
            ctx.Routine = RoutineIdle;
            ctx.Subroutine = 0;
            ctx.SubroutineTimer = 0f;
            ctx.Units[ctx.UnitIndex].PreferredVel = Vec2.Zero;
        }
    }

    public string GetRoutineName(byte routine) => routine switch
    {
        RoutineIdle => "IdleRoaming",
        RoutineAlert => "Alert",
        RoutineCombat => "Combat",
        RoutineReturn => "Return",
        _ => $"Unknown({routine})"
    };

    public string GetSubroutineName(byte routine, byte subroutine) =>
        subroutine == 0 ? "Default" : $"Unknown({subroutine})";
}
