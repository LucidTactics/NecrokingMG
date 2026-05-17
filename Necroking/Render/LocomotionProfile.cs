using Necroking.Core;
using Necroking.Data.Registries;
using Necroking.Movement;

namespace Necroking.Render;

/// <summary>
/// Per-unit locomotion tuning: gait thresholds and (in the new pixel-stride mode)
/// the per-gait feet-lock velocities. Both <see cref="LocomotionScaling"/> and the
/// AI's <c>SetLocomotionAnim</c> read from a profile so playback rate and gait
/// choice always come from the same source of truth.
///
/// Two construction paths:
///   - <see cref="FromUnit"/> — picks the right mode automatically based on
///     <see cref="UnitDef.LegacyGaitMode"/> and the availability of stride
///     calibration data. Use this from gameplay code.
///   - <see cref="FromBaseSpeed"/> / <see cref="FromAnimVels"/> — direct
///     constructors for tests and explicit-mode call sites.
///
/// New mode (default, when calibration exists and legacy_gait_mode=false):
///   - Per-gait feet-lock velocities derived from pixel measurements; gait
///     thresholds sit at the midpoint between adjacent gait velocities so the
///     playback rate is continuous through transitions (no foot-cycle jump).
///   - Hysteresis bands stay small — they only need to absorb single-frame
///     velocity noise, not hide a frame-reset jolt.
///
/// Legacy mode (toggled per-unit via <c>legacyGaitMode</c> JSON field, OR
/// triggered automatically when sprite calibration is missing):
///   - Original CombatSpeed-derived thresholds (jog = 4 + base/3, run = 6 + 2*base/3).
///   - Larger hysteresis bands to suppress the frame-reset visual when crossing.
///   - LocomotionScaling falls back to its original clamped-Lerp playback formula.
/// </summary>
public readonly struct LocomotionProfile
{
    // Idle → Walk boundary. Fixed, small — the threshold itself is the "is moving"
    // test rather than a tier. Downward exit uses a slightly tighter value so a
    // unit that's clearly stopping flips back to Idle quickly.
    public const float IdleWalkEnter = 0.25f;
    public const float IdleWalkExit = 0.10f;

    // Clamps on the playback-rate scaling. Below the floor, the cycle looks frozen;
    // above the ceiling, it looks cartoonishly sped up. Shared between modes.
    // MaxPlayback=3.0 gives headroom for the sprint case (vel=4×MS hitting Run gait):
    // even at the worst case where pixel-derived runVel underestimates, 4×MS / runVel
    // stays under 3.0 in practice.
    public const float WalkFloorPlayback = 0.25f;
    public const float MaxPlayback = 3.0f;
    public const float RunFullSpeedDelta = 7f; // (legacy only) units past runThreshold until Run reaches 1.0x

    // Gait thresholds in units of animWalkVel (= CombatSpeed when anchored). Imported
    // from the Nightfall Rogue project's threshold choice (BattleRenderer.cs:429-431).
    // At velocity = JogThresholdRatio × walkAnimVel, the unit transitions to Jog;
    // at velocity = RunThresholdRatio × walkAnimVel, it transitions to Run.
    public const float JogThresholdRatio = 1.4f;
    public const float RunThresholdRatio = 2.7f;

    /// <summary>True if this profile uses the original CombatSpeed-derived formula
    /// (no pixel-stride data). LocomotionScaling branches on this.</summary>
    public readonly bool IsLegacy;

    /// <summary>Per-gait feet-lock velocity (world units / sec) — the velocity at
    /// which the gait's authored sprite cycle exactly matches ground motion.
    /// Only populated in new mode (IsLegacy=false). At runtime, playback rate
    /// for a gait = unit.velocity / animVelForGait, clamped.</summary>
    public readonly float AnimWalkVel;
    public readonly float AnimJogVel;
    public readonly float AnimRunVel;

    public readonly float JogThreshold;
    public readonly float RunThreshold;
    public readonly float JogHysteresis;
    public readonly float RunHysteresis;

    private LocomotionProfile(bool isLegacy,
        float animWalk, float animJog, float animRun,
        float jogThreshold, float runThreshold, float jogHys, float runHys)
    {
        IsLegacy = isLegacy;
        AnimWalkVel = animWalk;
        AnimJogVel = animJog;
        AnimRunVel = animRun;
        JogThreshold = jogThreshold;
        RunThreshold = runThreshold;
        JogHysteresis = jogHys;
        RunHysteresis = runHys;
    }

    /// <summary>Build the right profile for a UnitDef. Prefers the new
    /// pixel-stride system; falls back to legacy CombatSpeed formula when the
    /// unit opts out (<c>legacyGaitMode=true</c>) or when calibration data is
    /// missing. Per-gait override values on the UnitDef win over auto-computed
    /// values when present.
    ///
    /// Anchoring strategy (matches Nightfall Rogue's design):
    /// <c>animWalkVel</c> is anchored to <c>CombatSpeed</c> by definition — i.e.
    /// when a unit moves at its CombatSpeed it's "walking" with feet locked.
    /// <c>animJogVel</c> and <c>animRunVel</c> are derived by scaling that anchor
    /// up by the per-gait stride ratio measured from the sprite pixels — so the
    /// artist's intent for "how much bigger is a run stride than a walk stride"
    /// is preserved, but the absolute scale follows CombatSpeed (designer
    /// intent). Per-gait <c>AnimXxxVelOverride</c> fields win over both.</summary>
    public static LocomotionProfile FromUnit(UnitDef def)
    {
        float baseSpeed = def.Stats?.CombatSpeed ?? 8f;
        if (def.LegacyGaitMode || def.SpriteData?.Calibration == null)
            return FromBaseSpeed(baseSpeed);

        var cal = def.SpriteData.Calibration;
        float pixelWalk = StrideCalibration.ResolveAnimVel(cal.Walk, def.SpriteWorldHeight);
        float pixelJog  = StrideCalibration.ResolveAnimVel(cal.Jog,  def.SpriteWorldHeight);
        float pixelRun  = StrideCalibration.ResolveAnimVel(cal.Run,  def.SpriteWorldHeight);

        // Need a valid pixel walk velocity to compute the gait ratios. If any
        // gait is missing entirely, drop back to legacy.
        if (pixelWalk <= 0f || pixelJog <= 0f || pixelRun <= 0f)
            return FromBaseSpeed(baseSpeed);

        // Anchor on CombatSpeed; derive jog/run via stride/cycle ratios from
        // the pixel measurement. The ratios preserve "jog stride is 1.62× walk
        // stride" regardless of how fast the designer wants the unit to walk.
        float jogRatio = pixelJog / pixelWalk;
        float runRatio = pixelRun / pixelWalk;

        float walk = def.AnimWalkVelOverride ?? baseSpeed;
        float jog  = def.AnimJogVelOverride  ?? baseSpeed * jogRatio;
        float run  = def.AnimRunVelOverride  ?? baseSpeed * runRatio;

        return FromAnimVels(walk, jog, run);
    }

    /// <summary>Build a new-mode profile directly from per-gait feet-lock
    /// velocities. Thresholds are placed at fixed multiples of the walk anchor
    /// (<see cref="JogThresholdRatio"/> / <see cref="RunThresholdRatio"/>),
    /// matching the Nightfall Rogue project's design. With <c>animWalkVel ==
    /// CombatSpeed</c> from <see cref="FromUnit"/>, this means the unit shows
    /// Walk gait through its full walk-effort range, transitions to Jog as
    /// velocity ramps past <c>1.4 × CombatSpeed</c>, and to Run past
    /// <c>2.7 × CombatSpeed</c> — landing solidly in Run at the sprint cap
    /// of <c>4 × CombatSpeed</c>.</summary>
    public static LocomotionProfile FromAnimVels(float walk, float jog, float run)
    {
        float jogThresh = walk * JogThresholdRatio;
        float runThresh = walk * RunThresholdRatio;
        // Hysteresis is small in new mode — just enough to suppress single-frame
        // velocity noise (ORCA jitter, accel ramp wobble). The visual hitch that
        // legacy needed big bands for (frame-reset on SwitchState) is handled by
        // AnimController's foot-phase carryover.
        float jogHys = MathUtil.Clamp((jog - walk) * 0.05f, 0.05f, 0.5f);
        float runHys = MathUtil.Clamp((run - jog) * 0.05f, 0.05f, 0.5f);
        return new LocomotionProfile(false, walk, jog, run, jogThresh, runThresh, jogHys, runHys);
    }

    /// <summary>Legacy CombatSpeed-derived profile. Original formula kept
    /// verbatim so opting a unit out of the new system reproduces today's
    /// behavior exactly.</summary>
    public static LocomotionProfile FromBaseSpeed(float baseSpeed)
    {
        float jog = 4f + baseSpeed / 3f;
        float run = 6f + 2f * baseSpeed / 3f;
        float band = MathUtil.Clamp(0.5f + baseSpeed * 0.05f, 0.4f, 1.5f);
        return new LocomotionProfile(true, 0f, 0f, 0f, jog, run, band, band);
    }

    /// <summary>Pick the locomotion state tier for the given speed + AI intent,
    /// respecting hysteresis around thresholds. MoveEffort biases the choice
    /// toward a target gait without changing the actual velocity — a "Sprint"
    /// intent picks Run even when speed is still ramping up, but the playback
    /// rate (computed by LocomotionScaling from raw velocity) still matches
    /// foot motion to ground motion.</summary>
    public AnimState PickTier(AnimState prev, float speed, MoveEffort effort = MoveEffort.Normal)
    {
        float biasedSpeed = ApplyEffortBias(speed, effort);

        bool prevIsLoco = prev == AnimState.Idle || prev == AnimState.Walk
            || prev == AnimState.Jog || prev == AnimState.Run;

        if (!prevIsLoco)
        {
            if (biasedSpeed <= IdleWalkEnter) return AnimState.Idle;
            if (biasedSpeed < JogThreshold) return AnimState.Walk;
            if (biasedSpeed < RunThreshold) return AnimState.Jog;
            return AnimState.Run;
        }

        switch (prev)
        {
            case AnimState.Idle:
                if (biasedSpeed >= RunThreshold + RunHysteresis) return AnimState.Run;
                if (biasedSpeed >= JogThreshold + JogHysteresis) return AnimState.Jog;
                if (biasedSpeed > IdleWalkEnter) return AnimState.Walk;
                return AnimState.Idle;

            case AnimState.Walk:
                if (biasedSpeed >= RunThreshold + RunHysteresis) return AnimState.Run;
                if (biasedSpeed >= JogThreshold + JogHysteresis) return AnimState.Jog;
                if (biasedSpeed <= IdleWalkExit) return AnimState.Idle;
                return AnimState.Walk;

            case AnimState.Jog:
                if (biasedSpeed >= RunThreshold + RunHysteresis) return AnimState.Run;
                if (biasedSpeed <= IdleWalkEnter) return AnimState.Idle;
                if (biasedSpeed <= JogThreshold - JogHysteresis) return AnimState.Walk;
                return AnimState.Jog;

            case AnimState.Run:
                if (biasedSpeed <= IdleWalkEnter) return AnimState.Idle;
                if (biasedSpeed <= JogThreshold - JogHysteresis) return AnimState.Walk;
                if (biasedSpeed <= RunThreshold - RunHysteresis) return AnimState.Jog;
                return AnimState.Run;
        }
        return AnimState.Idle;
    }

    /// <summary>Bias raw velocity toward a target value driven by AI intent. The
    /// lerp factors are deliberately small — intent nudges the pick at the
    /// boundaries without overriding clear cases. Modeled on the Nightfall Rogue
    /// MoveEffort biases (BattleRenderer.AnimationForRelativeSpeed).</summary>
    private float ApplyEffortBias(float speed, MoveEffort effort)
    {
        switch (effort)
        {
            case MoveEffort.Walk:
                // Pull toward mid-walk: keeps the unit walking even when velocity
                // briefly spikes toward jog (e.g. ORCA push, pathing shortcut).
                return MathUtil.Lerp(speed, JogThreshold * 0.7f, 0.05f);
            case MoveEffort.Hurry:
                // Pull toward jog: snap into Jog earlier on intent.
                return MathUtil.Lerp(speed, JogThreshold + 0.5f, 0.25f);
            case MoveEffort.Sprint:
                // Pull past the run threshold so a charging unit picks Run on
                // intent, even while velocity is still ramping.
                return MathUtil.Lerp(speed, RunThreshold + RunFullSpeedDelta * 0.5f, 0.15f);
            default:
                return speed;
        }
    }
}
