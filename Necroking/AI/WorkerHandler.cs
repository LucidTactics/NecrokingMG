using System;
using Necroking.Core;

namespace Necroking.AI;

/// <summary>
/// Archetype handler for grave-assigned workers. Drives the per-unit state
/// machine for the job the dispatcher assigned (unit.WorkerJobId). Policy —
/// which job, where to deposit/withdraw, what's collectable — lives in
/// <see cref="Necroking.Game.Jobs.WorkerSystem"/> (ctx.Workers); this handler
/// only walks the unit through the phases.
///
/// Two archetypes:
///   Collect — gather a source (foragable instant-pickup / corpse physical-carry /
///             berry channel-poison), haul to the host stockpile, deposit.
///   Process — fetch an input from a source stockpile, carry to the processing
///             building, channel WorkRoutine for processTime, emit output
///             (stockpile deposit / global Essence / spawned unit).
/// </summary>
public class WorkerHandler : IArchetypeHandler
{
    public const byte PhaseDecide = 0;
    public const byte PhaseGoToSource = 1;
    public const byte PhaseChannelAtSource = 2;   // berry poison
    public const byte PhaseGoToStorage = 3;
    public const byte PhaseIdleAtHome = 4;
    public const byte PhaseProcGoToInput = 5;
    public const byte PhaseProcWork = 6;
    public const byte PhaseCollectFetchInput = 7; // fetch a stock input (e.g. potion) before acting on the world source

    private const float SourceRange = 1.0f;
    private const float BuildingRange = 1.6f;
    private const float HomeRange = 1.2f;
    private const float BushRange = 1.2f;
    private const float PoisonDuration = 2.0f;
    private const string DefaultBerryBuff = "buff_poison_dot";

    public void OnSpawn(ref AIContext ctx) => ctx.Units[ctx.UnitIndex].WorkerPhase = PhaseDecide;

    public void Update(ref AIContext ctx)
    {
        var ws = ctx.Workers;
        if (ws == null || ctx.EnvSystem == null) { ctx.Units[ctx.UnitIndex].PreferredVel = Vec2.Zero; return; }

        switch (ctx.Units[ctx.UnitIndex].WorkerPhase)
        {
            case PhaseDecide:          Decide(ref ctx, ws); break;
            case PhaseGoToSource:      GoToSource(ref ctx, ws); break;
            case PhaseChannelAtSource: ChannelAtSource(ref ctx, ws); break;
            case PhaseGoToStorage:     GoToStorage(ref ctx, ws); break;
            case PhaseIdleAtHome:      IdleAtHome(ref ctx, ws); break;
            case PhaseProcGoToInput:   ProcGoToInput(ref ctx, ws); break;
            case PhaseProcWork:        ProcWork(ref ctx, ws); break;
            case PhaseCollectFetchInput: CollectFetchInput(ref ctx, ws); break;
        }
    }

    // ── shared ──
    private static Game.Jobs.JobDef? Job(Game.Jobs.WorkerSystem ws, ref AIContext ctx)
    {
        string id = ctx.Units[ctx.UnitIndex].WorkerJobId;
        return string.IsNullOrEmpty(id) ? null : ws.GetJobState(id)?.Def;
    }

    private void Decide(ref AIContext ctx, Game.Jobs.WorkerSystem ws)
    {
        int i = ctx.UnitIndex;
        ctx.Units[i].PreferredVel = Vec2.Zero;

        var def = Job(ws, ref ctx);
        if (def == null) { ctx.Units[i].WorkerPhase = PhaseIdleAtHome; return; }

        string carry = ctx.Units[i].WorkerCarryType;
        bool carrying = !string.IsNullOrEmpty(carry);
        // Holding the finished output? Go deliver it. (A carried *input* — e.g. a
        // potion en route to a bush — is NOT the store resource, so it falls through.)
        if (carrying && !string.IsNullOrEmpty(def.StoreResource) && carry == def.StoreResource)
        { ctx.Units[i].WorkerPhase = PhaseGoToStorage; return; }

        if (def.Archetype == Game.Jobs.JobArchetype.Collect)
        {
            // Stock inputs (e.g. Poison Berries needs a potion) — fetch one first.
            if (def.Inputs.Count > 0 && !carrying)
            {
                var inp0 = def.Inputs[0];
                int ib = ws.FindWithdrawBuilding(inp0.Resource, ctx.MyPos, inp0.Amount);
                if (ib < 0) { ctx.Units[i].WorkerPhase = PhaseIdleAtHome; return; }
                ctx.Units[i].WorkerTargetObjIdx = ib;
                ctx.Units[i].WorkerPhase = PhaseCollectFetchInput;
                return;
            }
            int src = ws.FindNearestSource(def, ctx.MyPos);
            if (src < 0) { ctx.Units[i].WorkerPhase = PhaseIdleAtHome; return; }
            ctx.Units[i].WorkerTargetObjIdx = src;
            ctx.Units[i].WorkerPhase = PhaseGoToSource;
            return;
        }

        // Process: need a host with room and an input source.
        if (def.Inputs.Count == 0 || ws.FindHostBuilding(def, ctx.MyPos) < 0)
        { ctx.Units[i].WorkerPhase = PhaseIdleAtHome; return; }
        var inp = def.Inputs[0];
        int inputBldg = ws.FindWithdrawBuilding(inp.Resource, ctx.MyPos, inp.Amount);
        if (inputBldg < 0) { ctx.Units[i].WorkerPhase = PhaseIdleAtHome; return; }
        ctx.Units[i].WorkerTargetObjIdx = inputBldg;
        ctx.Units[i].WorkerPhase = PhaseProcGoToInput;
    }

    // ── Collect ──
    private void GoToSource(ref AIContext ctx, Game.Jobs.WorkerSystem ws)
    {
        int i = ctx.UnitIndex;
        var env = ctx.EnvSystem!;
        var def = Job(ws, ref ctx);
        if (def == null) { ctx.Units[i].WorkerPhase = PhaseDecide; return; }
        int target = ctx.Units[i].WorkerTargetObjIdx;

        Vec2 targetPos;
        if (def.CollectKind == "corpse")
        {
            var p = ws.CorpsePos(target);
            if (p == null) { ctx.Units[i].WorkerPhase = PhaseDecide; return; }
            targetPos = p.Value;
        }
        else
        {
            if (target < 0 || target >= env.ObjectCount || !env.GetObjectRuntime(target).Alive
                || !env.IsObjectVisible(target))
            { ctx.Units[i].WorkerPhase = PhaseDecide; return; }
            var obj = env.GetObject(target);
            targetPos = new Vec2(obj.X, obj.Y);
        }

        MoveTo(ref ctx, targetPos);
        float range = def.CollectKind == "berry" ? BushRange : SourceRange;
        if (!SubroutineSteps.MoveToPosition_Arrived(ref ctx, range)) return;

        ctx.Units[i].PreferredVel = Vec2.Zero;
        switch (def.CollectKind)
        {
            case "corpse":
                if (!ws.StartCarryCorpse(i, target)) { ctx.Units[i].WorkerPhase = PhaseDecide; return; }
                ctx.Units[i].WorkerCarryType = string.IsNullOrEmpty(def.StoreResource) ? "Corpse" : def.StoreResource;
                ctx.Units[i].WorkerCarryAmount = 1;
                ctx.Units[i].WorkerTargetObjIdx = -1;
                ctx.Units[i].WorkerPhase = PhaseGoToStorage;
                break;
            case "berry":
                ctx.Subroutine = 0;
                ctx.Units[i].WorkerPhase = PhaseChannelAtSource;
                break;
            default: // foragable: instant pickup
                string? collected = env.CollectForagable(target);
                ctx.Units[i].WorkerTargetObjIdx = -1;
                if (collected == null) { ctx.Units[i].WorkerPhase = PhaseDecide; return; }
                ctx.Units[i].WorkerCarryType = string.IsNullOrEmpty(def.StoreResource) ? collected : def.StoreResource;
                ctx.Units[i].WorkerCarryAmount = 1;
                ctx.Units[i].WorkerPhase = PhaseGoToStorage;
                break;
        }
    }

    private void ChannelAtSource(ref AIContext ctx, Game.Jobs.WorkerSystem ws)
    {
        int i = ctx.UnitIndex;
        var env = ctx.EnvSystem!;
        int target = ctx.Units[i].WorkerTargetObjIdx;
        var def = Job(ws, ref ctx);

        bool inEnd = ctx.Subroutine == WorkRoutine.WorkEnd;
        if (!inEnd && (def == null || target < 0 || target >= env.ObjectCount
            || !env.GetObjectRuntime(target).Alive
            || env.GetObjectRuntime(target).BerryState != World.BerryState.Berries))
        { WorkRoutine.Reset(ref ctx); ctx.Units[i].WorkerTargetObjIdx = -1; ctx.Units[i].WorkerPhase = PhaseDecide; return; }

        var obj = env.GetObject(target < 0 ? 0 : target);
        var bushPos = new Vec2(obj.X, obj.Y);
        var phase = WorkRoutine.Update(ref ctx, bushPos, BushRange, PoisonDuration, out _);

        if (phase == WorkRoutine.Phase.WorkComplete)
        {
            env.PoisonBerryBush(target, DefaultBerryBuff);
            // The carried input (e.g. the potion) is spent on the bush.
            ctx.Units[i].WorkerCarryType = "";
            ctx.Units[i].WorkerCarryAmount = 0;
            // If the job also stockpiles an output, pick it up to deliver; else done.
            if (def != null && !string.IsNullOrEmpty(def.StoreResource))
            {
                ctx.Units[i].WorkerCarryType = def.StoreResource;
                ctx.Units[i].WorkerCarryAmount = 1;
            }
            ctx.Units[i].WorkerTargetObjIdx = -1;
        }
        else if (phase == WorkRoutine.Phase.Done)
        {
            ctx.Units[i].WorkerPhase = string.IsNullOrEmpty(ctx.Units[i].WorkerCarryType)
                ? PhaseDecide : PhaseGoToStorage;
        }
    }

    /// <summary>Walk to the building holding this collect job's stock input, withdraw
    /// it (e.g. a potion_poison), then head to the world source carrying it.</summary>
    private void CollectFetchInput(ref AIContext ctx, Game.Jobs.WorkerSystem ws)
    {
        int i = ctx.UnitIndex;
        var env = ctx.EnvSystem!;
        var def = Job(ws, ref ctx);
        if (def == null || def.Inputs.Count == 0) { ctx.Units[i].WorkerPhase = PhaseDecide; return; }
        var inp = def.Inputs[0];
        int bldg = ctx.Units[i].WorkerTargetObjIdx;

        if (bldg < 0 || bldg >= env.ObjectCount || !env.GetObjectRuntime(bldg).Alive
            || ws.StoredOf(bldg, inp.Resource) < inp.Amount)
        { ctx.Units[i].WorkerPhase = PhaseDecide; return; }

        var obj = env.GetObject(bldg);
        MoveTo(ref ctx, new Vec2(obj.X, obj.Y));
        if (!SubroutineSteps.MoveToPosition_Arrived(ref ctx, BuildingRange)) return;

        int took = ws.Withdraw(bldg, inp.Resource, inp.Amount);
        if (took < inp.Amount) { ctx.Units[i].WorkerPhase = PhaseDecide; return; }

        int src = ws.FindNearestSource(def, ctx.MyPos);
        if (src < 0)
        {
            // No source now — return the input so it isn't lost, then idle.
            ws.Deposit(bldg, inp.Resource, took);
            ctx.Units[i].WorkerPhase = PhaseIdleAtHome;
            return;
        }
        ctx.Units[i].WorkerCarryType = inp.Resource;
        ctx.Units[i].WorkerCarryAmount = took;
        ctx.Units[i].WorkerTargetObjIdx = src;
        ctx.Units[i].WorkerPhase = PhaseGoToSource;
    }

    private void GoToStorage(ref AIContext ctx, Game.Jobs.WorkerSystem ws)
    {
        int i = ctx.UnitIndex;
        var env = ctx.EnvSystem!;
        string carry = ctx.Units[i].WorkerCarryType;
        if (string.IsNullOrEmpty(carry)) { ctx.Units[i].WorkerPhase = PhaseDecide; return; }

        int building = ws.FindDepositBuilding(carry, ctx.MyPos);
        if (building < 0) { ctx.Units[i].PreferredVel = Vec2.Zero; return; } // storage full — hold

        var obj = env.GetObject(building);
        MoveTo(ref ctx, new Vec2(obj.X, obj.Y));
        if (!SubroutineSteps.MoveToPosition_Arrived(ref ctx, BuildingRange)) return;

        if (ctx.Units[i].CarryingCorpseID >= 0) ws.ConsumeCarriedCorpse(i);
        ws.Deposit(building, carry, ctx.Units[i].WorkerCarryAmount);
        ctx.Units[i].WorkerCarryType = "";
        ctx.Units[i].WorkerCarryAmount = 0;
        ctx.Units[i].PreferredVel = Vec2.Zero;
        ctx.Units[i].WorkerPhase = PhaseDecide;
    }

    // ── Process ──
    private void ProcGoToInput(ref AIContext ctx, Game.Jobs.WorkerSystem ws)
    {
        int i = ctx.UnitIndex;
        var env = ctx.EnvSystem!;
        var def = Job(ws, ref ctx);
        if (def == null || def.Inputs.Count == 0) { ctx.Units[i].WorkerPhase = PhaseDecide; return; }
        int bldg = ctx.Units[i].WorkerTargetObjIdx;
        var inp = def.Inputs[0];

        if (bldg < 0 || bldg >= env.ObjectCount || !env.GetObjectRuntime(bldg).Alive
            || ws.StoredOf(bldg, inp.Resource) < inp.Amount)
        { ctx.Units[i].WorkerPhase = PhaseDecide; return; }

        var obj = env.GetObject(bldg);
        MoveTo(ref ctx, new Vec2(obj.X, obj.Y));
        if (!SubroutineSteps.MoveToPosition_Arrived(ref ctx, BuildingRange)) return;

        int took = ws.Withdraw(bldg, inp.Resource, inp.Amount);
        if (took < inp.Amount) { ctx.Units[i].WorkerPhase = PhaseDecide; return; }

        int host = ws.FindHostBuilding(def, ctx.MyPos);
        if (host < 0) { ctx.Units[i].WorkerPhase = PhaseDecide; return; } // lost host — input is dropped
        ctx.Units[i].WorkerCarryType = inp.Resource;
        ctx.Units[i].WorkerCarryAmount = inp.Amount;
        ctx.Units[i].WorkerTargetObjIdx = host;
        ctx.Subroutine = 0;
        ctx.Units[i].WorkerPhase = PhaseProcWork;
    }

    private void ProcWork(ref AIContext ctx, Game.Jobs.WorkerSystem ws)
    {
        int i = ctx.UnitIndex;
        var env = ctx.EnvSystem!;
        var def = Job(ws, ref ctx);
        var js = string.IsNullOrEmpty(ctx.Units[i].WorkerJobId) ? null : ws.GetJobState(ctx.Units[i].WorkerJobId);
        int host = ctx.Units[i].WorkerTargetObjIdx;

        bool inEnd = ctx.Subroutine == WorkRoutine.WorkEnd;
        if (!inEnd && (def == null || js == null || host < 0 || host >= env.ObjectCount
            || !env.GetObjectRuntime(host).Alive))
        {
            WorkRoutine.Reset(ref ctx);
            ctx.Units[i].WorkerCarryType = ""; ctx.Units[i].WorkerCarryAmount = 0;
            ctx.Units[i].WorkerPhase = PhaseDecide;
            return;
        }

        var obj = env.GetObject(host);
        var hostPos = new Vec2(obj.X, obj.Y);
        var phase = WorkRoutine.Update(ref ctx, hostPos, BuildingRange, def!.ProcessTime, out _);

        if (phase == WorkRoutine.Phase.WorkComplete)
        {
            ws.EmitProcessOutput(js!, host);
            ctx.Units[i].WorkerCarryType = "";
            ctx.Units[i].WorkerCarryAmount = 0;
        }
        else if (phase == WorkRoutine.Phase.Done)
        {
            ctx.Units[i].WorkerTargetObjIdx = -1;
            ctx.Units[i].WorkerPhase = PhaseDecide;
        }
    }

    // ── idle ──
    private void IdleAtHome(ref AIContext ctx, Game.Jobs.WorkerSystem ws)
    {
        int i = ctx.UnitIndex;
        var env = ctx.EnvSystem!;
        if (!string.IsNullOrEmpty(ctx.Units[i].WorkerJobId) && ws.GetJobState(ctx.Units[i].WorkerJobId) != null)
        { ctx.Units[i].WorkerPhase = PhaseDecide; return; }

        int home = ctx.Units[i].WorkerHomeObjIdx;
        if (home < 0 || home >= env.ObjectCount || !env.GetObjectRuntime(home).Alive)
        { ctx.Units[i].PreferredVel = Vec2.Zero; return; }

        var obj = env.GetObject(home);
        var homePos = new Vec2(obj.X, obj.Y);
        if ((homePos - ctx.MyPos).LengthSq() > HomeRange * HomeRange) MoveTo(ref ctx, homePos);
        else ctx.Units[i].PreferredVel = Vec2.Zero;
    }

    private static void MoveTo(ref AIContext ctx, Vec2 target)
    {
        ctx.Units[ctx.UnitIndex].MoveTarget = target;
        SubroutineSteps.MoveToPosition(ref ctx, ctx.MyMaxSpeed);
        WorkRoutine.FaceTowards(ref ctx, target);
    }

    public string GetRoutineName(byte routine) => "Worker";

    public string GetSubroutineName(byte routine, byte subroutine) => "—";
}
