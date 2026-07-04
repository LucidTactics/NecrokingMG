using System;
using System.Collections.Generic;
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
///
/// Same-priority replacement (e.g. one Combat request immediately followed by
/// another) is rejected unless the current override has already started
/// playing (OverrideStarted=true). This protects a queued request from being
/// stolen by a later same-priority request on the next frame. SetOverride
/// returns an OverrideHandle the caller can pass to ClearIfOwned later; the
/// handle compare prevents "I queued X, but Y preempted me, and my ClearIfOwned
/// shouldn't touch Y" races.
///
/// See the larger doc-block at the top of AnimController.cs for the full rules
/// (priority scale, OverrideKind lifecycle, CorpseInteractPhase contract,
/// JustFinished one-frame edge, etc).
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
            // Slowed recoveries (reanimation's half-speed rise) take longer in
            // wall-time than the clip's 1x length, so stretch the lock to match.
            float spd = unit.Incap.RecoverPlaybackSpeed > 0f ? unit.Incap.RecoverPlaybackSpeed : 1f;
            unit.Incap.RecoverTimer = realDuration / spd;
        }

        // Tick override timer (TimedHold Duration > 0)
        if (unit.OverrideTimer > 0f)
        {
            unit.OverrideTimer -= dt;
            if (unit.OverrideTimer <= 0f)
            {
                unit.OverrideAnim = AnimRequest.None;
                unit.CurrentOverrideHandleId = 0;
            }
        }

        // Missing-clip policy: an override whose state has no real clip on this
        // sprite would silently render the Idle clip while the resolver believes
        // e.g. a flinch/stun is playing — the render lies about the state and the
        // one-shot's length becomes "the Idle clip's length". Drop it (logged once
        // per unit-type+state) so the unit honestly falls back to its routine anim.
        // SetOverride can't do this check (no controller there); doing it here
        // catches the override before it is ever applied to the controller.
        if (unit.OverrideAnim.IsActive && !unit.OverrideStarted
            && !ctrl.HasRealAnim(unit.OverrideAnim.State))
        {
            LogMissingClipOnce(unit.UnitDefID, unit.OverrideAnim.State);
            unit.OverrideAnim = AnimRequest.None;
            unit.OverrideTimer = 0f;
            unit.CurrentOverrideHandleId = 0;
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
                {
                    unit.OverrideAnim = AnimRequest.None;
                    unit.CurrentOverrideHandleId = 0;
                }
            }
            else if (unit.OverrideStarted && unit.OverrideAnim.Kind == OverrideKind.OneShot)
            {
                // Controller moved away from a started OneShot — the override played
                // out. Clear it. Hold and TimedHold don't auto-clear here (Hold is
                // caller-owned; TimedHold ticks via OverrideTimer above).
                unit.OverrideAnim = AnimRequest.None;
                unit.OverrideStarted = false;
                unit.CurrentOverrideHandleId = 0;
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
    /// Set an override animation on a unit. Returns an <see cref="OverrideHandle"/>
    /// that the caller can stash and later pass to <see cref="ClearIfOwned"/> to
    /// safely tear down their override without racing a preemption.
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
    ///
    /// Returns <see cref="OverrideHandle.None"/> if the request was rejected
    /// (lower priority than current, no replacement). Otherwise the returned
    /// handle is owned by this call until it's preempted or auto-expires.
    /// </summary>
    public static OverrideHandle SetOverride(Unit unit, AnimRequest request)
    {
        // Movement gate (Rule 1): a state authored for a stationary body may not
        // play while the unit is actually moving, UNLESS something is stopping the
        // movement. Priority-3 requests (death, knockdown holds, physics Fall) are
        // exempt — their owning systems zero/own velocity themselves. Combat
        // requests pass whenever a plant flag is set (PendingAttack/PostAttackTimer/
        // InCombat/incap/jump/dodge-hop) because movement is zeroed the same tick.
        // This is the structural guard against the "sliding" bug class: any writer —
        // current or future — that requests e.g. a Dodge or flinch on a running unit
        // simply gets rejected and locomotion keeps playing.
        if (request.Priority < 3
            && AnimController.IsMovementLocked(request.State)
            && !IsPlantedOrStopping(unit))
            return OverrideHandle.None;

        bool canReplace;
        if (!unit.OverrideAnim.IsActive) canReplace = true;
        else if (request.Priority > unit.OverrideAnim.Priority) canReplace = true;
        else if (request.Priority == unit.OverrideAnim.Priority) canReplace = unit.OverrideStarted;
        else canReplace = false;

        if (!canReplace) return OverrideHandle.None;

        uint id = NextHandleId();
        unit.OverrideAnim = request;
        unit.OverrideTimer = request.Duration > 0f ? request.Duration : 0f;
        unit.OverrideStarted = false;
        unit.CurrentOverrideHandleId = id;
        return new OverrideHandle(id);
    }

    /// <summary>
    /// Safe teardown by handle: clears the current override iff the passed
    /// handle still matches the unit's current override ID. No-op if the
    /// handle is stale (another caller preempted us, or the override already
    /// auto-expired).
    ///
    /// Returns true if the override was actually cleared, false otherwise.
    /// </summary>
    public static bool ClearIfOwned(Unit unit, OverrideHandle handle)
    {
        if (!handle.IsValid) return false;
        if (unit.CurrentOverrideHandleId != handle.Id) return false;
        unit.OverrideAnim = AnimRequest.None;
        unit.OverrideTimer = 0f;
        unit.OverrideStarted = false;
        unit.CurrentOverrideHandleId = 0;
        return true;
    }

    /// <summary>
    /// Unconditionally drop the unit's current override so the next Resolve falls
    /// back to RoutineAnim (locomotion). Used to cancel a stale one-shot that would
    /// otherwise bleed past its purpose — e.g. an attack swing still on screen after
    /// the unit has left combat and started chasing. Safe to call when no override
    /// is active (no-op).
    /// </summary>
    public static void ClearOverride(Unit unit)
    {
        if (!unit.OverrideAnim.IsActive) return;
        unit.OverrideAnim = AnimRequest.None;
        unit.OverrideTimer = 0f;
        unit.OverrideStarted = false;
        unit.CurrentOverrideHandleId = 0;
    }

    /// <summary>Speed below which a unit visually reads as standing — movement-
    /// locked overrides are allowed through. Roughly the Idle/Walk gait boundary.</summary>
    private const float MovingSpeedSq = 0.3f * 0.3f;

    /// <summary>True when the unit is effectively stationary, or a plant mechanism
    /// is active that zeroes/owns its movement this tick (see the movement gate in
    /// SetOverride).</summary>
    private static bool IsPlantedOrStopping(Unit unit)
    {
        if (unit.Velocity.LengthSq() <= MovingSpeedSq) return true;
        return !unit.PendingAttack.IsNone
            || unit.PostAttackTimer > 0f
            || unit.InCombat
            || unit.Incap.Active
            || unit.JumpPhase != 0
            || unit.DodgeTimer > 0f;
    }

    // One log line per (unit type, state) so a missing clip is loud in the debug
    // log without spamming every frame of every unit.
    private static readonly HashSet<(string, AnimState)> _loggedMissingClips = new();
    private static void LogMissingClipOnce(string unitDefId, AnimState state)
    {
        if (!_loggedMissingClips.Add((unitDefId ?? "?", state))) return;
        Necroking.Core.DebugLog.Log("anim",
            $"[AnimResolver] '{unitDefId}' has no clip for {state} (nor a chain fallback) — " +
            "override dropped; author the clip or stop requesting this state for this unit");
    }

    // Handle ID counter. 0 is reserved for OverrideHandle.None, so we start at 1
    // and wrap (~4B overrides before collision, effectively never in gameplay).
    private static uint _handleCounter;
    private static uint NextHandleId()
    {
        uint next = System.Threading.Interlocked.Increment(ref _handleCounter);
        return next == 0 ? System.Threading.Interlocked.Increment(ref _handleCounter) : next;
    }
}
