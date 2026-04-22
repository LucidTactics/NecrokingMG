using System;
using Necroking.Movement;

namespace Necroking.Render;

/// <summary>
/// Resolves which animation should play on a unit each frame.
///
/// Two-channel system:
///   - RoutineAnim: set by AI routines (locomotion, feeding, sleeping). Persistent.
///   - OverrideAnim: set by combat/physics (attacks, reactions, death). Temporary, auto-expires.
///
/// Resolution: override wins on tie. Higher priority always wins.
/// The interrupt flag controls whether the winner can cut the current animation mid-loop.
/// </summary>
public static class AnimResolver
{
    /// <summary>
    /// Resolve animation for one unit. Call once per frame before AnimController.Update.
    /// </summary>
    public static void Resolve(Unit unit, AnimController ctrl, float dt)
    {
        // Set real recovery timer from actual animation duration (first frame of recovery)
        if (unit.Incap.Recovering && unit.Incap.RecoverTimer < 0f)
        {
            float realDuration = ctrl.GetTotalDurationSeconds(unit.Incap.RecoverAnim);
            if (realDuration <= 0f) realDuration = unit.Incap.RecoverTime; // fallback
            unit.Incap.RecoverTimer = realDuration;
        }

        // Tick override timer
        if (unit.OverrideTimer > 0f)
        {
            unit.OverrideTimer -= dt;
            if (unit.OverrideTimer <= 0f)
                unit.OverrideAnim = AnimRequest.None;
        }

        // Track OverrideStarted for ALL active overrides, regardless of Kind. The
        // flag means "has the controller ever entered the override state," and is
        // used for:
        //   - OneShot auto-expire: if started AND ctrl moved on, the one-shot is done.
        //   - Same-priority replacement gate in SetOverride: prevents a newly-queued
        //     request from being stolen at frame 0 by another same-priority caller.
        //
        // Both OneShot and Hold need OverrideStarted to be accurate; the earlier
        // bug was that Hold (Duration==-1) never set it, silently blocking
        // Knockdown→Standup transitions.
        if (unit.OverrideAnim.IsActive)
        {
            if (ctrl.CurrentState == unit.OverrideAnim.State)
            {
                unit.OverrideStarted = true;
                // OneShot auto-expires when the anim finishes in hold mode (Death,
                // Knockdown-authored-with-PlayOnceHold). For PlayOnceTransition the
                // controller auto-switches to Idle and the mismatch-branch below
                // handles it.
                if (unit.OverrideAnim.Kind == OverrideKind.OneShot && ctrl.IsAnimFinished)
                    unit.OverrideAnim = AnimRequest.None;
            }
            else if (unit.OverrideStarted && unit.OverrideAnim.Kind == OverrideKind.OneShot)
            {
                // Controller moved away from a started OneShot — the override played
                // out. Clear it. Hold and TimedHold don't auto-clear here (Hold is
                // caller-owned; TimedHold ticks via OverrideTimer above).
                unit.OverrideAnim = AnimRequest.None;
                unit.OverrideStarted = false;
            }
            // else: override not started yet (waiting for ForceState to apply it)
        }

        // Pick the winning request: override wins on tie
        AnimRequest winner;
        if (unit.OverrideAnim.IsActive && unit.OverrideAnim.Priority >= unit.RoutineAnim.Priority)
            winner = unit.OverrideAnim;
        else if (unit.RoutineAnim.Priority > unit.OverrideAnim.Priority || !unit.OverrideAnim.IsActive)
            winner = unit.RoutineAnim;
        else
            winner = unit.OverrideAnim; // tie → override wins

        // Apply to controller
        if (ctrl.CurrentState == winner.State)
        {
            // Already playing the right anim — just update speed
            ctrl.PlaybackSpeed = winner.PlaybackSpeed;
            return;
        }

        // The resolver has already decided who wins — always ForceState to apply it.
        if (unit.Incap.HoldAtEnd && unit.Incap.Active && !unit.Incap.Recovering)
        {
            ctrl.ForceStateAtEnd(winner.State);
            unit.Incap.HoldAtEnd = false; // only snap once
        }
        else
        {
            ctrl.ForceState(winner.State);
        }
        ctrl.PlaybackSpeed = winner.PlaybackSpeed;
    }

    /// <summary>
    /// Set an override animation on a unit. Used by combat, physics, game events.
    ///
    /// Replacement rules (priority lanes):
    ///   - Strictly higher priority always wins.
    ///   - Same priority only wins if the current override has *already started*
    ///     (OverrideStarted=true) — i.e. the controller has entered its state and
    ///     begun playing. A new same-priority request can't steal frame-0 from a
    ///     same-priority request that was just queued but hasn't hit the controller
    ///     yet. This prevents two overrides queued on the same frame (e.g. hit +
    ///     dodge) from last-writer-wins with neither getting to play.
    ///   - Lower priority never replaces a live override.
    /// </summary>
    public static void SetOverride(Unit unit, AnimRequest request)
    {
        bool canReplace;
        if (!unit.OverrideAnim.IsActive) canReplace = true;
        else if (request.Priority > unit.OverrideAnim.Priority) canReplace = true;
        else if (request.Priority == unit.OverrideAnim.Priority) canReplace = unit.OverrideStarted;
        else canReplace = false;

        if (canReplace)
        {
            unit.OverrideAnim = request;
            unit.OverrideTimer = request.Duration > 0f ? request.Duration : 0f;
            unit.OverrideStarted = false;
        }
    }
}
