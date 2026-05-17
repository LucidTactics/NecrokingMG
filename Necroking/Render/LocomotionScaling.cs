using Necroking.Core;

namespace Necroking.Render;

/// <summary>
/// Per-frame playback-rate scaling for locomotion anims (Walk / Jog / Run / Carry)
/// so the on-screen foot-cycle frequency matches actual movement velocity. The
/// formula is selected by the <see cref="LocomotionProfile.IsLegacy"/> flag:
///
///   - New mode (default): <c>playback = velocity / animVelForGait</c>, where
///     <c>animVelForGait</c> is the per-gait feet-lock velocity from the
///     pixel-stride calibration. Clamped to a sensible playback envelope.
///     This is mathematically the right thing — when velocity equals the
///     anim's authored stride velocity, playback = 1.0 and feet lock to ground
///     by construction. Above/below, the rate scales linearly and feet stay
///     locked across the full velocity range. No skating.
///
///   - Legacy mode: original clamped-Lerp formula tied to gait thresholds.
///     Not feet-locked (the playback rate at threshold = 1.0 is just a
///     convention, not a calibrated value). Kept for the per-unit legacy_gait_mode
///     opt-out and as the fallback when stride calibration isn't available.
///
/// Both modes share the same <see cref="LocomotionProfile"/> constants for
/// playback floor/ceiling, so a unit toggled between modes never produces a
/// playback rate outside the expected envelope.
/// </summary>
public static class LocomotionScaling
{
    /// <summary>Compute the playback-speed scalar for a locomotion state at a
    /// given velocity. Returns 1.0 for non-locomotion states or when required
    /// metadata is missing (graceful fallback).</summary>
    public static float ComputeLocomotionPlayback(
        AnimController ctrl, in LocomotionProfile profile, AnimState state, float speed)
    {
        if (profile.IsLegacy)
            return ComputeLegacyPlayback(ctrl, profile, state, speed);

        return ComputeNewPlayback(profile, state, speed);
    }

    /// <summary>New mode: playback rate is directly proportional to velocity, with
    /// the per-gait <c>AnimVel</c> as the unit-scale factor that locks feet to
    /// ground. One formula for all three gaits; the per-gait differentiation
    /// lives entirely in the calibration value (each gait was authored with its
    /// own stride length and cycle duration, so its AnimVel encodes both).</summary>
    private static float ComputeNewPlayback(in LocomotionProfile profile, AnimState state, float speed)
    {
        float animVel = state switch
        {
            AnimState.Walk => profile.AnimWalkVel,
            AnimState.Jog  => profile.AnimJogVel,
            AnimState.Run  => profile.AnimRunVel,
            // Carry isn't a gait variant — reuse Walk feet-lock since the Walk
            // anim is what plays under it. If we ever author a distinct Carry
            // anim with its own stride, add a per-state CarryVel field.
            AnimState.Carry => profile.AnimWalkVel,
            _ => 0f,
        };
        if (animVel <= 0f) return 1f;
        return MathUtil.Clamp(speed / animVel,
            LocomotionProfile.WalkFloorPlayback, LocomotionProfile.MaxPlayback);
    }

    /// <summary>Legacy mode: original clamped-Lerp formula preserved verbatim. At
    /// each gait threshold the new state's initial playback is chosen so its
    /// foot-cycle rate equals the outgoing state's rate — but only as a foot-rate
    /// hack, not a real feet-to-ground lock. Skating is the visible failure mode
    /// of this formula; the new mode replaces it.</summary>
    private static float ComputeLegacyPlayback(
        AnimController ctrl, in LocomotionProfile profile, AnimState state, float speed)
    {
        float jogThreshold = profile.JogThreshold;
        float runThreshold = profile.RunThreshold;
        float walkFloor = LocomotionProfile.WalkFloorPlayback;
        float maxPlay = LocomotionProfile.MaxPlayback;

        switch (state)
        {
            case AnimState.Walk:
            {
                float span = jogThreshold - LocomotionProfile.IdleWalkEnter;
                if (span <= 0.01f) return 1f;
                float t = (speed - LocomotionProfile.IdleWalkEnter) / span;
                return MathUtil.Clamp(MathUtil.Lerp(walkFloor, 1f, t), walkFloor, maxPlay);
            }

            case AnimState.Jog:
            {
                float walkCycle = ctrl.GetTotalDurationSeconds(AnimState.Walk);
                float jogCycle  = ctrl.GetTotalDurationSeconds(AnimState.Jog);
                if (walkCycle <= 0f || jogCycle <= 0f) return 1f;
                float jogStart = walkCycle / jogCycle;
                float span = runThreshold - jogThreshold;
                if (span <= 0.01f) return 1f;
                float t = (speed - jogThreshold) / span;
                return MathUtil.Clamp(MathUtil.Lerp(jogStart, 1f, t), walkFloor, maxPlay);
            }

            case AnimState.Run:
            {
                float jogCycle = ctrl.GetTotalDurationSeconds(AnimState.Jog);
                float runCycle = ctrl.GetTotalDurationSeconds(AnimState.Run);
                if (jogCycle <= 0f || runCycle <= 0f) return 1f;
                float runStart = runCycle / jogCycle;
                float t = (speed - runThreshold) / LocomotionProfile.RunFullSpeedDelta;
                return MathUtil.Clamp(MathUtil.Lerp(runStart, 1f, t), walkFloor, maxPlay);
            }

            case AnimState.Carry:
            {
                // Legacy Carry scaling — single-state floor→1.0 across the unit's
                // expected speed range. Uses jogThreshold as the "full speed" anchor
                // since CombatSpeed in legacy mode falls inside the jog band.
                if (jogThreshold <= 0.01f) return 1f;
                return MathUtil.Clamp(speed / jogThreshold, walkFloor, maxPlay);
            }

            default:
                return 1f;
        }
    }
}
