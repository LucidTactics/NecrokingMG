using Necroking.Core;
using Necroking.Movement;

namespace Necroking.AI;

/// <summary>
/// Village watchdog AI. Dogs patrol near their kennel and, thanks to a wide detection
/// range, spot approaching undead well before the humans do. When a dog detects a threat
/// it <b>barks</b>: it sounds the village alarm (<see cref="GameSystems.VillageSystem.RaiseAlert"/>)
/// so the town mobilises, and it holds a barking standoff at the intruder — advancing to
/// confront, backing off to keep its distance, never suiciding into the horde.
///
/// Dogs are the Human faction so a bark also propagates through the awareness system's
/// same-faction group alert, waking nearby militia and peasants directly.
///
/// Routines: 0 Guard · 1 Bark
/// </summary>
public class WatchdogHandler : IArchetypeHandler
{
    private const byte RoutineGuard = 0;
    private const byte RoutineBark = 1;

    private const float RoamRadius = 5f;
    private const float StandoffDist = 5f;
    private const float BarkInterval = 1.2f;
    private const float LeashDist = 22f; // don't chase the intruder further than this from home

    public void OnSpawn(ref AIContext ctx)
    {
        ctx.Units[ctx.UnitIndex].SpawnPosition = ctx.MyPos;
        ctx.Routine = RoutineGuard;
        ctx.Subroutine = 0;
        ctx.SubroutineTimer = 0f;
    }

    public void Update(ref AIContext ctx)
    {
        // Dogs bark at the undead specifically — ambient wildlife is ignored so a passing
        // deer never sounds the village alarm.
        int threatIdx = VillageThreat.FindNearestUndead(ref ctx, ctx.Units[ctx.UnitIndex].DetectionRange);
        if (threatIdx >= 0)
        {
            ctx.AlertTarget = ctx.Units[threatIdx].Id;
            if (ctx.Routine != RoutineBark) { ctx.Routine = RoutineBark; ctx.Subroutine = 0; ctx.SubroutineTimer = 0f; }
        }
        else if (ctx.Routine != RoutineGuard)
        {
            ctx.Routine = RoutineGuard;
            ctx.Subroutine = 0;
        }

        switch (ctx.Routine)
        {
            case RoutineGuard: UpdateGuard(ref ctx); break;
            case RoutineBark: UpdateBark(ref ctx, threatIdx); break;
        }
    }

    private static void UpdateGuard(ref AIContext ctx)
    {
        SubroutineSteps.SetEffort(ref ctx, MoveEffort.Walk, 0.6f);
        SubroutineSteps.IdleRoam(ref ctx, RoamRadius);
    }

    private static void UpdateBark(ref AIContext ctx, int threatIdx)
    {
        int i = ctx.UnitIndex;
        if (threatIdx < 0)
        {
            // Lost sight of the intruder — go back to guarding.
            ctx.Units[i].PreferredVel = Vec2.Zero;
            return;
        }

        Vec2 threatPos = ctx.Units[threatIdx].Position;

        // Sound the alarm every tick — cheap, and keeps the village's known threat
        // position current while the dog can see it.
        ctx.Villages?.RaiseAlert(ctx.Units[i].VillageId, ctx.AlertTarget, threatPos);

        // Periodic bark bubble.
        ctx.SubroutineTimer -= ctx.Dt;
        if (ctx.SubroutineTimer <= 0f)
        {
            ctx.Units[i].ShowStatusSymbol(UnitStatusSymbol.React, 1f);
            ctx.SubroutineTimer = BarkInterval;
        }

        float dist = (threatPos - ctx.MyPos).Length();
        float fromHome = (ctx.MyPos - ctx.Units[i].SpawnPosition).Length();
        SubroutineSteps.FacePosition(ref ctx, threatPos);

        if (fromHome > LeashDist)
        {
            // Strayed too far chasing — fall back toward the kennel, still barking.
            SubroutineSteps.SetEffort(ref ctx, MoveEffort.Hurry);
            SubroutineSteps.MoveToward(ref ctx, ctx.Units[i].SpawnPosition, ctx.MyMaxSpeed);
        }
        else if (dist > StandoffDist + 3f)
        {
            // Charge in to confront and harry.
            SubroutineSteps.SetEffort(ref ctx, MoveEffort.Hurry);
            SubroutineSteps.MoveToward(ref ctx, threatPos, ctx.MyMaxSpeed);
        }
        else if (dist < StandoffDist)
        {
            // Too close — back off, keep barking from a safer distance.
            SubroutineSteps.SetEffort(ref ctx, MoveEffort.Hurry);
            SubroutineSteps.MoveAwayFrom(ref ctx, threatPos, StandoffDist);
        }
        else
        {
            SubroutineSteps.SetIdle(ref ctx);
        }
    }

    public string GetRoutineName(byte routine) => routine switch
    {
        RoutineGuard => "Guard",
        RoutineBark => "Bark",
        _ => $"Unknown({routine})"
    };

    public string GetSubroutineName(byte routine, byte subroutine) =>
        subroutine == 0 ? "Default" : $"Sub({subroutine})";
}
