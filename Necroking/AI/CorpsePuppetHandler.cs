using Necroking.Core;

namespace Necroking.AI;

/// <summary>
/// Archetype handler for the "corpse puppet" — a raised body whose whole purpose is
/// to deliver itself to storage. It walks to the nearest Corpse Pile with room and,
/// on arrival, adds itself to that pile as a corpse (records the original body type +
/// bumps the abstract "Corpse" count). Rather than snapping out, it then plays its
/// death animation while sliding into the building, and only vanishes (no loose body)
/// once the collapse has finished — so you visibly watch it deposit itself.
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

    private const byte RoutineToPile = 0;      // walking to the nearest pile
    private const byte RoutineDepositing = 1;  // collapsing + sliding into the pile

    // Used only if the unit's sprite has no Death clip / no anim metadata.
    private const float FallbackDepositSeconds = 1.1f;

    public void OnSpawn(ref AIContext ctx) { /* no state — Update re-resolves the pile each tick */ }

    public void Update(ref AIContext ctx)
    {
        if (ctx.Routine == RoutineDepositing) { TickDeposit(ref ctx); return; }

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

        // Arrived: register into the pile. Deposit can return 0 if the pile filled
        // up between the find and now — only record its type + start the collapse when it
        // actually landed (recording on a rejected deposit would drift the type stack vs the count).
        ctx.Units[i].PreferredVel = Vec2.Zero;
        if (ws.Deposit(pile, Game.Jobs.JobResources.Corpse, 1) > 0)
        {
            // Pile as the ORIGINAL body if this puppet was raised as a zombie variant over a
            // real corpse (PuppetSourceDefID stamped at raise time); fall back to the unit's
            // own def for a plain corpse_puppet with no recorded source.
            string pileAs = !string.IsNullOrEmpty(ctx.Units[i].PuppetSourceDefID)
                ? ctx.Units[i].PuppetSourceDefID : ctx.Units[i].UnitDefID;
            ws.RecordPiledCorpse(pile, pileAs);
            BeginDeposit(ref ctx, pilePos);
        }
    }

    /// <summary>Start the collapse: force the death animation, and slide the body from where it
    /// stands onto the pile over the clip's real length. The slide reuses the dodge position-lerp
    /// (see UnitArrays.DodgeTimer) which owns Position and skips ORCA/wall/env — so the body glides
    /// into the building unobstructed instead of being shoved back out at the collision boundary.</summary>
    private void BeginDeposit(ref AIContext ctx, Vec2 pilePos)
    {
        int i = ctx.UnitIndex;
        float dur = DeathClipSeconds(ref ctx);

        Render.AnimResolver.SetOverride(ctx.Units[i], Render.AnimRequest.Forced(Render.AnimState.Death));

        ctx.Units[i].DodgeStartPos = ctx.MyPos;
        ctx.Units[i].DodgeEndPos = pilePos;
        ctx.Units[i].DodgeDuration = dur;
        ctx.Units[i].DodgeTimer = dur;     // > 0 → UpdateMovement hands Position to the dodge lerp
        ctx.Units[i].PreferredVel = Vec2.Zero;

        // Timer = removal clock, in lockstep with the slide + death anim.
        ctx.TransitionTo(RoutineDepositing, 0, dur);
    }

    /// <summary>Collapse phase: the dodge lerp slides the body in; when the death animation has
    /// run its course, vanish with NO loose corpse (it's already counted into the pile).</summary>
    private void TickDeposit(ref AIContext ctx)
    {
        int i = ctx.UnitIndex;
        ctx.Units[i].PreferredVel = Vec2.Zero;   // dodge lerp owns Position
        ctx.SubroutineTimer -= ctx.Dt;
        if (ctx.SubroutineTimer <= 0f)
            ctx.Units[i].PendingDespawn = true;   // reaped (no corpse) by Simulation.RemoveDeadUnits
    }

    /// <summary>Real length of this unit's Death clip in seconds (from the export's anim metadata,
    /// the same source combat/jumps time against). Falls back to a fixed duration when the sprite
    /// has no Death clip or no metadata is loaded (e.g. headless).</summary>
    private static float DeathClipSeconds(ref AIContext ctx)
    {
        var def = ctx.GameData?.Units.Get(ctx.Units[ctx.UnitIndex].UnitDefID);
        if (def != null && ctx.AnimMeta != null)
        {
            string key = Render.AnimMetaLoader.MetaKey(def.Sprite.SpriteName, "Death");
            if (ctx.AnimMeta.TryGetValue(key, out var meta))
            {
                float ms = meta.TotalDurationMs();
                if (ms > 0f) return ms / 1000f;
            }
        }
        return FallbackDepositSeconds;
    }

    public string GetRoutineName(byte routine) => "CorpsePuppet";
    public string GetSubroutineName(byte routine, byte subroutine) =>
        routine == RoutineDepositing ? "Depositing" : "ToPile";
}
