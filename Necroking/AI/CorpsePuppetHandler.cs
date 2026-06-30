using Necroking.Core;

namespace Necroking.AI;

/// <summary>
/// Archetype handler for the "corpse puppet" — a raised body whose whole purpose is
/// to deliver itself to storage. Each tick it walks to the nearest Corpse Pile with
/// room and, on arrival, adds itself to that pile as a corpse (records its own type +
/// bumps the abstract "Corpse" count) then vanishes with no loose body.
///
/// Reuses the worker logistics policy (<see cref="Necroking.Game.Jobs.WorkerSystem"/>
/// via ctx.Workers) so "nearest pile with room" and the deposit are the exact same
/// queries the Collect workers use — no parallel implementation.
/// </summary>
public class CorpsePuppetHandler : IArchetypeHandler
{
    // Tables have a collision radius (~0.5) + the unit's own radius, so center-to-center
    // can't get below ~1.0; 1.6 gives ORCA tolerance (matches WorkerHandler.BuildingRange).
    private const float DepositRange = 1.6f;

    public void OnSpawn(ref AIContext ctx) { /* no state — Update re-resolves the pile each tick */ }

    public void Update(ref AIContext ctx)
    {
        int i = ctx.UnitIndex;
        var ws = ctx.Workers;
        if (ws == null || ctx.EnvSystem == null) { ctx.Units[i].PreferredVel = Vec2.Zero; return; }

        // Nearest built Corpse Pile with room. -1 when every pile is full or none exist —
        // just idle in place until one frees up (don't despawn, don't drop a loose body).
        int pile = ws.FindDepositBuilding(Game.Jobs.JobResources.Corpse, ctx.MyPos);
        if (pile < 0) { ctx.Units[i].PreferredVel = Vec2.Zero; return; }

        var obj = ctx.EnvSystem.GetObject(pile);
        var pilePos = new Vec2(obj.X, obj.Y);
        ctx.Units[i].MoveTarget = pilePos;

        if (!SubroutineSteps.MoveToPosition_Arrived(ref ctx, DepositRange))
        {
            SubroutineSteps.MoveToPosition(ref ctx, ctx.MyMaxSpeed);
            WorkRoutine.FaceTowards(ref ctx, pilePos);
            return;
        }

        // Arrived: become a corpse in the pile. Deposit can return 0 if the pile filled
        // up between the find and now — only record its type + vanish when it actually
        // landed (recording on a rejected deposit would drift the type stack vs the count).
        ctx.Units[i].PreferredVel = Vec2.Zero;
        if (ws.Deposit(pile, Game.Jobs.JobResources.Corpse, 1) > 0)
        {
            ws.RecordPiledCorpse(pile, ctx.Units[i].UnitDefID);
            ctx.Units[i].PendingDespawn = true; // reaped (no corpse) by Simulation.RemoveDeadUnits
        }
    }

    public string GetRoutineName(byte routine) => "CorpsePuppet";
    public string GetSubroutineName(byte routine, byte subroutine) => "ToPile";
}
