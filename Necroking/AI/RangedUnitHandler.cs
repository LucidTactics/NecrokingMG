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
        ctx.Units.SpawnPosition[ctx.UnitIndex] = ctx.MyPos;
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
                ctx.Units.Target[ctx.UnitIndex] = CombatTarget.Unit(ctx.AlertTarget);
                ctx.Routine = RoutineCombat;
                ctx.SubroutineTimer = 0f;
                return;
            }
        }

        if (ctx.Routine == RoutineCombat && !SubroutineSteps.IsTargetAlive(ref ctx))
        {
            float range = ctx.Units.DetectionRange[ctx.UnitIndex];
            int next = SubroutineSteps.FindClosestEnemy(ref ctx, range > 0 ? range : 15f);
            if (next >= 0)
                ctx.Units.Target[ctx.UnitIndex] = CombatTarget.Unit(ctx.Units.Id[next]);
            else
            {
                ctx.Routine = RoutineReturn;
                ctx.Units.Target[ctx.UnitIndex] = CombatTarget.None;
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

        float dist = (ctx.Units.Position[targetIdx] - ctx.MyPos).Length();

        if (dist < TooCloseRange)
        {
            // Too close — back away
            SubroutineSteps.MoveAwayFrom(ref ctx, ctx.Units.Position[targetIdx], PreferredRange);
        }
        else if (dist > PreferredRange + 2f)
        {
            // Too far — approach
            SubroutineSteps.MoveToward(ref ctx, ctx.Units.Position[targetIdx], ctx.MySpeed * 0.6f);
        }
        else
        {
            // Good range — stand and fight
            ctx.Units.PreferredVel[ctx.UnitIndex] = Vec2.Zero;
            if (ctx.Units.EngagedTarget[ctx.UnitIndex].IsNone)
                ctx.Units.EngagedTarget[ctx.UnitIndex] = ctx.Units.Target[ctx.UnitIndex];
        }
    }

    private static void UpdateReturn(ref AIContext ctx)
    {
        ctx.Units.EngagedTarget[ctx.UnitIndex] = CombatTarget.None;
        ctx.Units.InCombat[ctx.UnitIndex] = false;

        Vec2 returnPos = ctx.Units.SpawnPosition[ctx.UnitIndex];
        if ((ctx.MyPos - returnPos).Length() > 2f)
            SubroutineSteps.MoveToward(ref ctx, returnPos, ctx.MySpeed * 0.5f);
        else
        {
            ctx.Routine = RoutineIdle;
            ctx.Subroutine = 0;
            ctx.SubroutineTimer = 0f;
            ctx.Units.PreferredVel[ctx.UnitIndex] = Vec2.Zero;
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
