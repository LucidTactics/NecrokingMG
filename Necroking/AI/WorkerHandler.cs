using System;
using Necroking.Core;

namespace Necroking.AI;

/// <summary>
/// Archetype handler for grave-assigned workers. Drives the per-unit Collect
/// state machine for the job the dispatcher assigned (read from the unit's
/// WorkerJobId). Policy — which job, where to deposit, what's collectable —
/// lives in <see cref="Necroking.Game.Jobs.WorkerSystem"/> (reached via
/// ctx.Workers); this handler only walks the unit through the phases.
///
/// P1 scope: the Collect archetype (Forage Mushrooms) — find a source, walk to
/// it, gather, carry to the host stockpile, deposit, repeat; idle near the home
/// grave when there's no work.
/// </summary>
public class WorkerHandler : IArchetypeHandler
{
    public const byte PhaseDecide = 0;
    public const byte PhaseGoToSource = 1;
    public const byte PhaseGoToStorage = 2;
    public const byte PhaseIdleAtHome = 3;

    private const float SourceRange = 1.0f;
    private const float BuildingRange = 1.6f;
    private const float HomeRange = 1.2f;

    public void OnSpawn(ref AIContext ctx)
    {
        ctx.Units[ctx.UnitIndex].WorkerPhase = PhaseDecide;
    }

    public void Update(ref AIContext ctx)
    {
        var ws = ctx.Workers;
        var env = ctx.EnvSystem;
        int i = ctx.UnitIndex;
        if (ws == null || env == null) { ctx.Units[i].PreferredVel = Vec2.Zero; return; }

        // If we're holding something, the only goal is to deliver it.
        if (!string.IsNullOrEmpty(ctx.Units[i].WorkerCarryType)
            && ctx.Units[i].WorkerPhase != PhaseGoToStorage)
            ctx.Units[i].WorkerPhase = PhaseGoToStorage;

        switch (ctx.Units[i].WorkerPhase)
        {
            case PhaseDecide:       Decide(ref ctx, ws); break;
            case PhaseGoToSource:   GoToSource(ref ctx, ws); break;
            case PhaseGoToStorage:  GoToStorage(ref ctx, ws); break;
            case PhaseIdleAtHome:   IdleAtHome(ref ctx, ws); break;
        }
    }

    private void Decide(ref AIContext ctx, Game.Jobs.WorkerSystem ws)
    {
        int i = ctx.UnitIndex;
        ctx.Units[i].PreferredVel = Vec2.Zero;

        string jobId = ctx.Units[i].WorkerJobId;
        var js = string.IsNullOrEmpty(jobId) ? null : ws.GetJobState(jobId);
        if (js == null) { ctx.Units[i].WorkerPhase = PhaseIdleAtHome; return; }

        int src = ws.FindNearestSource(js.Def, ctx.MyPos);
        if (src < 0) { ctx.Units[i].WorkerPhase = PhaseIdleAtHome; return; }

        ctx.Units[i].WorkerTargetObjIdx = src;
        ctx.Units[i].WorkerPhase = PhaseGoToSource;
    }

    private void GoToSource(ref AIContext ctx, Game.Jobs.WorkerSystem ws)
    {
        int i = ctx.UnitIndex;
        var env = ctx.EnvSystem!;
        int target = ctx.Units[i].WorkerTargetObjIdx;

        // Target gone / already collected by someone else → re-decide.
        if (target < 0 || target >= env.ObjectCount
            || !env.GetObjectRuntime(target).Alive
            || !env.IsObjectVisible(target))
        {
            ctx.Units[i].WorkerPhase = PhaseDecide;
            return;
        }

        var obj = env.GetObject(target);
        var targetPos = new Vec2(obj.X, obj.Y);
        ctx.Units[i].MoveTarget = targetPos;
        SubroutineSteps.MoveToPosition(ref ctx, ctx.MyMaxSpeed);
        WorkRoutine.FaceTowards(ref ctx, targetPos);

        if (SubroutineSteps.MoveToPosition_Arrived(ref ctx, SourceRange))
        {
            string? collected = env.CollectForagable(target);
            ctx.Units[i].PreferredVel = Vec2.Zero;
            ctx.Units[i].WorkerTargetObjIdx = -1;
            if (collected == null) { ctx.Units[i].WorkerPhase = PhaseDecide; return; }

            var js = ws.GetJobState(ctx.Units[i].WorkerJobId);
            string store = js != null && !string.IsNullOrEmpty(js.Def.StoreResource)
                ? js.Def.StoreResource : collected;
            ctx.Units[i].WorkerCarryType = store;
            ctx.Units[i].WorkerCarryAmount = 1;
            ctx.Units[i].WorkerPhase = PhaseGoToStorage;
        }
    }

    private void GoToStorage(ref AIContext ctx, Game.Jobs.WorkerSystem ws)
    {
        int i = ctx.UnitIndex;
        var env = ctx.EnvSystem!;
        string carry = ctx.Units[i].WorkerCarryType;
        if (string.IsNullOrEmpty(carry)) { ctx.Units[i].WorkerPhase = PhaseDecide; return; }

        int building = ws.FindDepositBuilding(carry, ctx.MyPos);
        if (building < 0)
        {
            // Storage full / none — hold and retry (keep carrying).
            ctx.Units[i].PreferredVel = Vec2.Zero;
            return;
        }

        var obj = env.GetObject(building);
        var targetPos = new Vec2(obj.X, obj.Y);
        ctx.Units[i].MoveTarget = targetPos;
        SubroutineSteps.MoveToPosition(ref ctx, ctx.MyMaxSpeed);
        WorkRoutine.FaceTowards(ref ctx, targetPos);

        if (SubroutineSteps.MoveToPosition_Arrived(ref ctx, BuildingRange))
        {
            ws.Deposit(building, ctx.Units[i].WorkerCarryAmount);
            ctx.Units[i].WorkerCarryType = "";
            ctx.Units[i].WorkerCarryAmount = 0;
            ctx.Units[i].PreferredVel = Vec2.Zero;
            ctx.Units[i].WorkerPhase = PhaseDecide;
        }
    }

    private void IdleAtHome(ref AIContext ctx, Game.Jobs.WorkerSystem ws)
    {
        int i = ctx.UnitIndex;
        var env = ctx.EnvSystem!;

        // Got a job again? Re-decide.
        if (!string.IsNullOrEmpty(ctx.Units[i].WorkerJobId)
            && ws.GetJobState(ctx.Units[i].WorkerJobId) != null)
        {
            ctx.Units[i].WorkerPhase = PhaseDecide;
            return;
        }

        int home = ctx.Units[i].WorkerHomeObjIdx;
        if (home < 0 || home >= env.ObjectCount || !env.GetObjectRuntime(home).Alive)
        {
            ctx.Units[i].PreferredVel = Vec2.Zero;
            return;
        }

        var obj = env.GetObject(home);
        var homePos = new Vec2(obj.X, obj.Y);
        if ((homePos - ctx.MyPos).LengthSq() > HomeRange * HomeRange)
        {
            ctx.Units[i].MoveTarget = homePos;
            SubroutineSteps.MoveToPosition(ref ctx, ctx.MyMaxSpeed);
        }
        else
        {
            ctx.Units[i].PreferredVel = Vec2.Zero;
        }
    }

    public string GetRoutineName(byte routine) => "Worker";

    public string GetSubroutineName(byte routine, byte subroutine) => subroutine switch
    {
        PhaseDecide => "Decide",
        PhaseGoToSource => "GoToSource",
        PhaseGoToStorage => "GoToStorage",
        PhaseIdleAtHome => "IdleAtHome",
        _ => $"P{subroutine}"
    };
}
