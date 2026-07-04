using Necroking.Core;
using Necroking.Movement;

namespace Necroking.AI;

/// <summary>
/// Civilian villager AI (peasants). Villagers never fight — they roam near home during
/// peaceful times and react to danger based on their village's <see cref="GameSystems.VillagePosture"/>:
///
///   • An undead within personal detection range → <b>Panic</b>: sprint straight away from it
///     (survival instinct, overrides everything).
///   • Village posture <b>Fleeing</b> (no guard, daytime) → run to the village's chosen flee
///     destination (a safer neighbouring village).
///   • Village posture <b>Cowering</b> (no guard, night) → run home and stay put; too dark to
///     flee cross-country, which makes them easy prey.
///   • Otherwise → roam near home.
///
/// Routines: 0 Roam · 1 Flee · 2 Cower · 3 Panic
/// </summary>
public class VillagerHandler : IArchetypeHandler
{
    private const byte RoutineRoam = 0;
    private const byte RoutineFlee = 1;
    private const byte RoutineCower = 2;
    private const byte RoutinePanic = 3;

    private const float RoamRadius = 6f;
    private const float PanicFleeDist = 18f;
    private const float ArriveDist = 3.5f;

    public void OnSpawn(ref AIContext ctx)
    {
        ctx.Units[ctx.UnitIndex].SpawnPosition = ctx.MyPos;
        ctx.Routine = RoutineRoam;
        ctx.Subroutine = 0;
        ctx.SubroutineTimer = 0f;
    }

    public void Update(ref AIContext ctx)
    {
        int i = ctx.UnitIndex;
        float det = ctx.Units[i].DetectionRange;
        int enemy = VillageThreat.FindNearestUndead(ref ctx, det);
        var village = ctx.Villages?.Get(ctx.Units[i].VillageId);

        // Personal survival trumps village coordination.
        if (enemy >= 0)
        {
            if (ctx.TransitionTo(RoutinePanic))
                ctx.Units[i].ShowStatusSymbol(UnitStatusSymbol.React, 1.5f);
        }
        else if (village != null)
        {
            switch (village.Posture)
            {
                case GameSystems.VillagePosture.Fleeing:  ctx.TransitionTo(RoutineFlee); break;
                case GameSystems.VillagePosture.Cowering: ctx.TransitionTo(RoutineCower); break;
                default:                                  ctx.TransitionTo(RoutineRoam); break;
            }
        }
        else if (ctx.Routine == RoutinePanic)
        {
            // No village and threat gone — settle back to roaming.
            ctx.TransitionTo(RoutineRoam);
        }

        ctx.Units[i].Fleeing = ctx.Routine == RoutineFlee || ctx.Routine == RoutinePanic;

        switch (ctx.Routine)
        {
            case RoutineRoam: UpdateRoam(ref ctx); break;
            case RoutineFlee: UpdateFlee(ref ctx, village); break;
            case RoutineCower: UpdateCower(ref ctx); break;
            case RoutinePanic: UpdatePanic(ref ctx); break;
        }
    }

    private static void UpdateRoam(ref AIContext ctx)
    {
        SubroutineSteps.SetEffort(ref ctx, MoveEffort.Walk, 0.5f);
        SubroutineSteps.IdleRoam(ref ctx, RoamRadius);
    }

    private static void UpdateFlee(ref AIContext ctx, GameSystems.Village? village)
    {
        SubroutineSteps.SetEffort(ref ctx, MoveEffort.Sprint);
        if (village == null || !village.FleeTargetSet)
        {
            // No destination — head home and hunker down instead of freezing.
            SubroutineSteps.MoveToward(ref ctx, ctx.Units[ctx.UnitIndex].SpawnPosition, ctx.MyMaxSpeed);
            return;
        }
        Vec2 dest = village.FleeTarget;
        if ((dest - ctx.MyPos).LengthSq() > ArriveDist * ArriveDist)
            SubroutineSteps.MoveToward(ref ctx, dest, ctx.MyMaxSpeed);
        else
            SubroutineSteps.SetIdle(ref ctx); // reached safety
    }

    private static void UpdateCower(ref AIContext ctx)
    {
        Vec2 home = ctx.Units[ctx.UnitIndex].SpawnPosition;
        if ((home - ctx.MyPos).LengthSq() > ArriveDist * ArriveDist)
        {
            SubroutineSteps.SetEffort(ref ctx, MoveEffort.Hurry);
            SubroutineSteps.MoveToward(ref ctx, home, ctx.MyMaxSpeed);
        }
        else
        {
            SubroutineSteps.SetIdle(ref ctx); // huddle at home in the dark
        }
    }

    private static void UpdatePanic(ref AIContext ctx)
    {
        int i = ctx.UnitIndex;
        float det = ctx.Units[i].DetectionRange;
        int enemy = VillageThreat.FindNearestUndead(ref ctx, det * 1.5f);
        if (enemy < 0)
        {
            // Threat left the area — calm down.
            ctx.TransitionTo(RoutineRoam);
            return;
        }
        SubroutineSteps.SetEffort(ref ctx, MoveEffort.Sprint);
        Vec2 away = ctx.MyPos - ctx.Units[enemy].Position;
        if (away.LengthSq() > 0.01f) away = away.Normalized();
        else away = new Vec2(1, 0);
        SubroutineSteps.MoveToward(ref ctx, ctx.MyPos + away * PanicFleeDist, ctx.MyMaxSpeed);
    }

    public string GetRoutineName(byte routine) => routine switch
    {
        RoutineRoam => "Roam",
        RoutineFlee => "Flee",
        RoutineCower => "Cower",
        RoutinePanic => "Panic",
        _ => $"Unknown({routine})"
    };

    public string GetSubroutineName(byte routine, byte subroutine) =>
        subroutine == 0 ? "Default" : $"Sub({subroutine})";
}
