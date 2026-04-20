using Necroking.Core;

namespace Necroking.Render;

/// <summary>
/// Playback-speed scaling for locomotion anims (Walk / Jog / Run / Carry) so the
/// foot-cycle frequency matches actual movement velocity instead of always playing
/// at 1.0x. Continuous at the Walk↔Jog↔Run transitions — at each threshold the new
/// state's initial playback is chosen so its foot-cycle rate equals the outgoing
/// state's rate (prevents a visible footfall-rate jump on state switch).
///
/// Thresholds match SetLocomotionAnim: jog at speed ≥ 4 + baseSpeed/3,
/// run at speed ≥ 6 + 2·baseSpeed/3.
/// </summary>
public static class LocomotionScaling
{
    public const float WalkFloor = 0.25f;
    public const float RunFullSpeedDelta = 7f; // speed past runThreshold at which run hits 1.0x
    public const float MaxPlayback = 1.5f;

    /// <summary>
    /// Returns the playback-speed scalar for the given locomotion state and current
    /// velocity. Returns 1.0 for non-locomotion states, or when cycle metadata is
    /// missing (graceful fallback).
    /// </summary>
    public static float ComputeLocomotionPlayback(
        AnimController ctrl, AnimState state, float speed, float baseSpeed)
    {
        float jogThreshold = 4f + baseSpeed / 3f;
        float runThreshold = 6f + 2f * baseSpeed / 3f;

        switch (state)
        {
            case AnimState.Walk:
            {
                // Linear from (IdleThresh, WalkFloor) to (jogThreshold, 1.0).
                float span = jogThreshold - 0.25f;
                if (span <= 0.01f) return 1f;
                float t = (speed - 0.25f) / span;
                return MathUtil.Clamp(MathUtil.Lerp(WalkFloor, 1f, t), WalkFloor, MaxPlayback);
            }

            case AnimState.Jog:
            {
                // Continuous at jogThreshold: jog_at_start = walkCycle / jogCycle so that
                // jog's foot-cycle rate equals walk-at-1.0x's rate at the boundary.
                // Then linear to (runThreshold, 1.0).
                float walkCycle = ctrl.GetTotalDurationSeconds(AnimState.Walk);
                float jogCycle  = ctrl.GetTotalDurationSeconds(AnimState.Jog);
                if (walkCycle <= 0f || jogCycle <= 0f) return 1f;
                float jogStart = walkCycle / jogCycle;
                float span = runThreshold - jogThreshold;
                if (span <= 0.01f) return 1f;
                float t = (speed - jogThreshold) / span;
                return MathUtil.Clamp(MathUtil.Lerp(jogStart, 1f, t), WalkFloor, MaxPlayback);
            }

            case AnimState.Run:
            {
                // Continuous at runThreshold: run_at_start = runCycle / jogCycle so
                // run's foot-cycle rate equals jog-at-1.0x's rate at the boundary.
                // Run reaches 1.0x at runThreshold + RunFullSpeedDelta, keeps growing
                // past that, capped at MaxPlayback.
                float jogCycle = ctrl.GetTotalDurationSeconds(AnimState.Jog);
                float runCycle = ctrl.GetTotalDurationSeconds(AnimState.Run);
                if (jogCycle <= 0f || runCycle <= 0f) return 1f;
                float runStart = runCycle / jogCycle;
                float t = (speed - runThreshold) / RunFullSpeedDelta;
                return MathUtil.Clamp(MathUtil.Lerp(runStart, 1f, t), WalkFloor, MaxPlayback);
            }

            case AnimState.Carry:
            {
                // Single-state scaling (no transitions): floor at low speed → 1.0
                // at baseSpeed, capped at MaxPlayback above.
                if (baseSpeed <= 0.01f) return 1f;
                return MathUtil.Clamp(speed / baseSpeed, WalkFloor, MaxPlayback);
            }

            default:
                return 1f;
        }
    }
}
