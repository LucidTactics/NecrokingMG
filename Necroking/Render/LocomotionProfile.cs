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
    // unit that's clearly stopping flips back to Idle quickly. Kept well below the
    // slowest intentional locomotion (deer graze/feed at ~0.1–0.18 wu/s) so slow
    // movers actually play their walk cycle instead of sliding in Idle. Zero-intent
    // residual momentum is filtered upstream by the PreferredVel gate in
    // SubroutineSteps, so a low Enter here can't make standing-still units walk.
    public const float IdleWalkEnter = 0.06f;
    public const float IdleWalkExit = 0.03f;

    // Clamps on the playback-rate scaling. Below the floor, the cycle looks frozen;
    // above the ceiling, it looks cartoonishly sped up. Shared between modes.
    // MaxPlayback=3.0 gives headroom for the sprint case (vel=4×MS hitting Run gait):
    // even at the worst case where pixel-derived runVel underestimates, 4×MS / runVel
    // stays under 3.0 in practice.
    public const float WalkFloorPlayback = 0.25f;
    public const float MaxPlayback = 3.0f;
    public const float RunFullSpeedDelta = 7f; // (legacy only) units past runThreshold until Run reaches 1.0x

    // Default per-effort velocity multipliers used when a UnitDef doesn't
    // specify its own. Biped pattern: jog ≈ 2× walk, sprint ≈ 4× walk. Per-
    // unit overrides via UnitDef.JogSpeedMultiplier / SprintSpeedMultiplier
    // (e.g. wolf 3/9, horse 3/9, cheetah 5/30). Gait thresholds derive from
    // these as midpoints between adjacent gait max-velocities.
    public const float DefaultJogMult = 2.0f;
    public const float DefaultSprintMult = 4.0f;

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
    /// Two decoupled anchors:
    ///   - <b>Playback anchor</b> for each gait is the pixel-derived feet-lock
    ///     velocity. <c>animWalkVel = (walk_stride_px × 2 / pxPerWorld) / cycle</c>
    ///     and same shape for Jog/Run. This is what makes feet actually lock to
    ///     ground motion at any velocity — playback = velocity / animVel.
    ///   - <b>Threshold anchor</b> for gait switching is <c>CombatSpeed</c>,
    ///     with per-unit jog/sprint multipliers. Default biped (2.0/4.0) gives
    ///     Jog at 1.5xCS and Run at 3.0xCS (midpoints between adjacent gait
    ///     max-velocities). Quadrupeds run much faster than they walk — a
    ///     wolf at (3.0/9.0) has Jog at 2xCS and Run at 6xCS, keeping the Run
    ///     anim's playback near native cadence even at sprint velocity.
    ///
    /// Trade-off the designer should know about: when CombatSpeed differs from
    /// the pixel walk velocity, the Walk anim plays at a non-1.0× cadence at
    /// CombatSpeed (rushed if CombatSpeed > pixelWalk, lazy if &lt;). The unit
    /// editor surfaces this discrepancy. Per-gait <c>AnimXxxVelOverride</c>
    /// fields let the designer force playback to a custom value if they want
    /// natural cadence at the cost of skating.</summary>
    public static LocomotionProfile FromUnit(UnitDef def)
    {
        float baseSpeed = def.Stats?.CombatSpeed ?? 8f;
        if (def.LegacyGaitMode || def.SpriteData?.Calibration == null)
            return FromBaseSpeed(baseSpeed);

        var cal = def.SpriteData.Calibration;
        // Pass SpriteScale alongside SpriteWorldHeight so the pixel→world
        // conversion uses the unit's actual rendered height (some units like
        // Wretched render at 0.9× scale). Omitting it would overstate cycle
        // distance and underestimate feet-lock velocity, causing the playback
        // rate at any given velocity to be too low — feet would drag.
        //
        // For quadrupeds (def.IsQuadruped), subtract IdleFootSpreadPx from
        // each gait's stride. The "stride spread" pixel measurement on a 4-
        // legged unit captures front-paw-to-rear-paw distance, which is
        // dominated by body length, not by leg stride. Idle stance pose gives
        // us the body length to strip out so the residual is the actual
        // leg-stride that drives ground motion.
        float bodySub = def.IsQuadruped ? cal.IdleFootSpreadPx : 0f;
        // 0 means "use default (biped 0.5)"; non-zero values like 0.75 reshape
        // the cycle-distance formula. Per-unit override lets quadrupeds with
        // unusual gait patterns (high-bound run, gallop) be tuned later.
        float duty = def.DutyCycle > 0f ? def.DutyCycle : StrideCalibration.DefaultDutyCycle;
        float pixelWalk = StrideCalibration.ResolveAnimVel(cal.Walk, def.SpriteWorldHeight, def.SpriteScale, bodySub, duty);
        float pixelJog  = StrideCalibration.ResolveAnimVel(cal.Jog,  def.SpriteWorldHeight, def.SpriteScale, bodySub, duty);
        float pixelRun  = StrideCalibration.ResolveAnimVel(cal.Run,  def.SpriteWorldHeight, def.SpriteScale, bodySub, duty);

        // Need valid pixel velocities to use the new mode. If any gait is missing,
        // drop back to legacy.
        if (pixelWalk <= 0f || pixelJog <= 0f || pixelRun <= 0f)
            return FromBaseSpeed(baseSpeed);

        // Playback anchors = pixel-derived per-gait feet-lock velocities. Override
        // fields win when set (designer escape hatch — e.g. force walk to lock at
        // CombatSpeed instead, trading groundedness for natural cadence).
        float walk = def.AnimWalkVelOverride ?? pixelWalk;
        float jog  = def.AnimJogVelOverride  ?? pixelJog;
        float run  = def.AnimRunVelOverride  ?? pixelRun;

        // Threshold anchor = CombatSpeed, modulated by per-unit jog/sprint
        // multipliers. JogThreshold = midpoint between walk-max (CS) and jog-max
        // (CS × jogMult). RunThreshold = midpoint between jog-max and sprint-max
        // (CS × sprintMult). For biped (2/4): 1.5xCS and 3xCS. For quadruped
        // (3/9): 2xCS and 6xCS.
        float jogMult = def.JogSpeedMultiplier > 0f
            ? def.JogSpeedMultiplier : DefaultJogMult;
        float sprintMult = def.SprintSpeedMultiplier > 0f
            ? def.SprintSpeedMultiplier : DefaultSprintMult;
        float jogThresh = baseSpeed * (1f + jogMult) * 0.5f;
        float runThresh = baseSpeed * (jogMult + sprintMult) * 0.5f;
        return BuildNewModeProfile(walk, jog, run, jogThresh, runThresh);
    }

    /// <summary>Build a new-mode profile directly from per-gait feet-lock
    /// velocities. Thresholds derived from a per-unit anchor + biped-default
    /// multipliers (jog at 1.5×anchor, run at 3×anchor). For per-unit-tuned
    /// thresholds (different jog/sprint multipliers like quadruped 3/9), use
    /// the FromUnit path which feeds the right values to
    /// <see cref="BuildNewModeProfile"/>.</summary>
    public static LocomotionProfile FromAnimVels(float walk, float jog, float run,
        float? thresholdAnchor = null)
    {
        float anchor = thresholdAnchor ?? walk;
        // Biped defaults: jog at midpoint of (1, jogMult=2) = 1.5x anchor;
        // run at midpoint of (2, sprintMult=4) = 3x anchor.
        float jogThresh = anchor * (1f + DefaultJogMult) * 0.5f;
        float runThresh = anchor * (DefaultJogMult + DefaultSprintMult) * 0.5f;
        return BuildNewModeProfile(walk, jog, run, jogThresh, runThresh);
    }

    /// <summary>Final assembly of a new-mode profile given per-gait feet-lock
    /// velocities and pre-computed gait thresholds. Hysteresis bands derived
    /// from gait-velocity spread.</summary>
    private static LocomotionProfile BuildNewModeProfile(
        float walk, float jog, float run, float jogThresh, float runThresh)
    {
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
