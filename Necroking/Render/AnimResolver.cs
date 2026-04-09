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

        // Auto-expire play-once overrides (Duration == 0).
        // PlayOnceTransition anims auto-switch to Idle when done, so we can't just
        // check IsAnimFinished (the state changed). Instead: once we've applied the
        // override (controller entered the state), track that. When the controller
        // leaves that state, the override is done.
        if (unit.OverrideAnim.IsActive && unit.OverrideAnim.Duration == 0f)
        {
            if (ctrl.CurrentState == unit.OverrideAnim.State)
            {
                // Controller is playing the override — mark it as started
                unit.OverrideStarted = true;
                // If the anim finished in a hold mode, expire
                if (ctrl.IsAnimFinished)
                    unit.OverrideAnim = AnimRequest.None;
            }
            else if (unit.OverrideStarted)
            {
                // Controller moved away from the override state — it played and finished
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
    /// </summary>
    public static void SetOverride(Unit unit, AnimRequest request)
    {
        // Higher priority always replaces. Same priority replaces if current is interruptible or finished.
        if (request.Priority > unit.OverrideAnim.Priority
            || !unit.OverrideAnim.IsActive
            || (request.Priority == unit.OverrideAnim.Priority))
        {
            unit.OverrideAnim = request;
            unit.OverrideTimer = request.Duration > 0f ? request.Duration : 0f;
            unit.OverrideStarted = false;
        }
    }
}
