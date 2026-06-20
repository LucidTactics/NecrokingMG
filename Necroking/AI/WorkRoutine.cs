using System;
using Necroking.Core;

namespace Necroking.AI;

/// <summary>
/// Shared driver for "walk to a target and channel a work animation" tasks.
/// Replaces the duplicated WalkToSite → WorkStart → WorkLoop → WorkEnd state
/// machine that previously lived inside BuildGlyph / BuildTrap / CraftAtTable.
///
/// Caller owns the routine ID, target validation, and what to do on completion.
/// Each tick the caller calls <see cref="Update"/> with the current target
/// position and duration; the helper drives the four subroutines using the
/// unit's BuildTimer + CorpseInteractPhase fields and reports its current
/// <see cref="Phase"/> back so the caller can handle Complete / Cancel.
/// </summary>
public static class WorkRoutine
{
    // Subroutine ids — match PlayerControlledHandler.BuildSub_* for compatibility
    // with the existing animation hooks that read CorpseInteractPhase.
    public const byte WalkToSite = 0;
    public const byte WorkStart  = 1;
    public const byte WorkLoop   = 2;
    public const byte WorkEnd    = 3;

    public enum Phase { Walking, Starting, Working, WorkComplete, Ending, Done }

    /// <summary>Advance the work state machine by one tick. Returns the phase
    /// that ran this tick. The caller should treat <see cref="Phase.Done"/> as
    /// "finalize and exit the routine" — at that point ctx.Subroutine is left
    /// on WorkEnd and CorpseInteractPhase has fully returned to 0.</summary>
    /// <param name="workProgress01">Normalized progress (0..1) during the
    /// WorkLoop phase; 0 outside it. Lets callers drive a visible progress
    /// bar on the target without re-deriving from BuildTimer.</param>
    public static Phase Update(ref AIContext ctx, Vec2 targetPos,
        float interactRange, float workDuration, out float workProgress01)
    {
        int i = ctx.UnitIndex;
        workProgress01 = 0f;

        switch (ctx.Subroutine)
        {
            case WalkToSite:
                ctx.Units[i].MoveTarget = targetPos;
                SubroutineSteps.MoveToPosition(ref ctx, ctx.MyMaxSpeed);
                FaceTowards(ref ctx, targetPos);
                if (SubroutineSteps.MoveToPosition_Arrived(ref ctx, interactRange))
                {
                    ctx.Subroutine = WorkStart;
                    ctx.Units[i].PreferredVel = Vec2.Zero;
                    ctx.Units[i].CorpseInteractPhase = 1; // WorkStart anim
                }
                return Phase.Walking;

            case WorkStart:
                ctx.Units[i].PreferredVel = Vec2.Zero;
                FaceTowards(ref ctx, targetPos);
                if (ctx.Units[i].CorpseInteractPhase == 2)
                {
                    ctx.Subroutine = WorkLoop;
                    ctx.Units[i].BuildTimer = 0f;
                }
                return Phase.Starting;

            case WorkLoop:
                ctx.Units[i].PreferredVel = Vec2.Zero;
                FaceTowards(ref ctx, targetPos);
                ctx.Units[i].BuildTimer += ctx.Dt;
                workProgress01 = workDuration > 0f
                    ? Math.Min(1f, ctx.Units[i].BuildTimer / workDuration) : 1f;
                if (ctx.Units[i].BuildTimer >= workDuration)
                {
                    ctx.Subroutine = WorkEnd;
                    ctx.Units[i].CorpseInteractPhase = 3; // WorkEnd anim
                    // One-shot signal to the caller: the work is *done* (apply
                    // side-effects now), but the standup animation has just
                    // started — keep the routine alive through Ending.
                    return Phase.WorkComplete;
                }
                return Phase.Working;

            case WorkEnd:
                ctx.Units[i].PreferredVel = Vec2.Zero;
                if (ctx.Units[i].CorpseInteractPhase == 0)
                    return Phase.Done;
                return Phase.Ending;
        }
        return Phase.Done;
    }

    /// <summary>Reset the unit out of any work routine. Caller is responsible
    /// for resetting its own routine-specific fields (target idx, etc.) and
    /// for transitioning ctx.Routine back to whatever the idle state is.</summary>
    public static void Reset(ref AIContext ctx)
    {
        ctx.Units[ctx.UnitIndex].CorpseInteractPhase = 0;
        ctx.Units[ctx.UnitIndex].BuildTimer = 0f;
        ctx.Subroutine = 0;
    }

    /// <summary>Force the WorkLoop → WorkEnd transition without waiting for
    /// BuildTimer to reach workDuration. Used by routines that are gated on an
    /// external signal (e.g. CraftAtTable waiting for the table-side craft
    /// timer). No-op if not currently in WorkLoop.</summary>
    public static void EndLoopNow(ref AIContext ctx)
    {
        if (ctx.Subroutine != WorkLoop) return;
        ctx.Subroutine = WorkEnd;
        ctx.Units[ctx.UnitIndex].CorpseInteractPhase = 3;
    }

    public static void FaceTowards(ref AIContext ctx, Vec2 target)
    {
        var dir = target - ctx.MyPos;
        if (dir.LengthSq() > 0.01f)
            ctx.Units[ctx.UnitIndex].FacingAngle = MathF.Atan2(dir.Y, dir.X) * 180f / MathF.PI;
    }
}
