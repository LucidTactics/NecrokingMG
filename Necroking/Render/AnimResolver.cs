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
        // Tick override timer
        if (unit.OverrideTimer > 0f)
        {
            unit.OverrideTimer -= dt;
            if (unit.OverrideTimer <= 0f)
                unit.OverrideAnim = AnimRequest.None;
        }

        // Auto-expire play-once overrides when the animation finishes
        if (unit.OverrideAnim.IsActive && unit.OverrideAnim.Duration == 0f && ctrl.IsAnimFinished)
            unit.OverrideAnim = AnimRequest.None;

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

        // Decide whether to force or request based on interrupt flag
        if (winner.Interrupt)
            ctrl.ForceState(winner.State);
        else
            ctrl.RequestState(winner.State);

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
        }
    }
}
