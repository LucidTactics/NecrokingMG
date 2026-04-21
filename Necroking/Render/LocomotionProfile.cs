using Necroking.Core;

namespace Necroking.Render;

/// <summary>
/// Single source of truth for locomotion tier thresholds (Walk/Jog/Run) and the
/// hysteresis bands around them. Both the AI state-picker
/// (SubroutineSteps.SetLocomotionAnim) and the playback-rate scaler
/// (LocomotionScaling.ComputeLocomotionPlayback) read from here so the two
/// systems can't silently drift apart.
///
/// Thresholds are derived from the unit's CombatSpeed. Per-unit overrides can
/// be layered on later by feeding a custom LocomotionProfile from UnitDef data
/// — for now everything falls out of the formula.
///
/// The hysteresis bands (how far past a threshold the velocity must go before
/// switching tiers) keep a unit from flipping states when ORCA/accel leaves
/// its velocity oscillating near a boundary; without them the AnimController's
/// SwitchState would reset _animTime=0 every flap, freezing the walk cycle
/// on its first couple of frames.
/// </summary>
public readonly struct LocomotionProfile
{
    // Idle → Walk boundary. Fixed, small — the threshold itself is the "is moving"
    // test rather than a tier. Downward exit uses a slightly tighter value so a
    // unit that's clearly stopping flips back to Idle quickly.
    public const float IdleWalkEnter = 0.25f;
    public const float IdleWalkExit = 0.10f;

    // Clamps on the playback-rate scaling. Below the floor, Walk looks hung; above
    // the ceiling, Run looks cartoonishly sped up. These are shared so both the
    // tier-picker's continuity math and the rate scaler use the same envelope.
    public const float WalkFloorPlayback = 0.25f;
    public const float MaxPlayback = 1.5f;
    public const float RunFullSpeedDelta = 7f; // how much past runThreshold until Run reaches 1.0x

    public readonly float JogThreshold;
    public readonly float RunThreshold;
    public readonly float JogHysteresis;
    public readonly float RunHysteresis;

    public LocomotionProfile(float jogThreshold, float runThreshold, float jogHys, float runHys)
    {
        JogThreshold = jogThreshold;
        RunThreshold = runThreshold;
        JogHysteresis = jogHys;
        RunHysteresis = runHys;
    }

    /// <summary>Derive the standard profile from a unit's CombatSpeed.</summary>
    public static LocomotionProfile FromBaseSpeed(float baseSpeed)
    {
        float jog = 4f + baseSpeed / 3f;
        float run = 6f + 2f * baseSpeed / 3f;
        // Proportional bands — scale with baseSpeed so fast and slow units both get
        // meaningful dead zones. Floor guards against tiny bands for very slow units.
        float band = MathUtil.Clamp(0.5f + baseSpeed * 0.05f, 0.4f, 1.5f);
        return new LocomotionProfile(jog, run, band, band);
    }

    /// <summary>Pick the locomotion state tier for the given speed, respecting
    /// hysteresis around thresholds so the state doesn't flap when velocity
    /// oscillates near a boundary.</summary>
    public AnimState PickTier(AnimState prev, float speed)
    {
        bool prevIsLoco = prev == AnimState.Idle || prev == AnimState.Walk
            || prev == AnimState.Jog || prev == AnimState.Run;

        if (!prevIsLoco)
        {
            // Fresh pick — no previous tier to anchor to. Use the bare thresholds.
            if (speed <= IdleWalkEnter) return AnimState.Idle;
            if (speed < JogThreshold) return AnimState.Walk;
            if (speed < RunThreshold) return AnimState.Jog;
            return AnimState.Run;
        }

        // Sticky selection: stay in prev unless clearly past the boundary by the
        // hysteresis amount. Also allow multi-tier jumps if speed is well past the
        // upper band (e.g. sudden velocity spike from a dash).
        switch (prev)
        {
            case AnimState.Idle:
                if (speed >= RunThreshold + RunHysteresis) return AnimState.Run;
                if (speed >= JogThreshold + JogHysteresis) return AnimState.Jog;
                if (speed > IdleWalkEnter) return AnimState.Walk;
                return AnimState.Idle;

            case AnimState.Walk:
                if (speed >= RunThreshold + RunHysteresis) return AnimState.Run;
                if (speed >= JogThreshold + JogHysteresis) return AnimState.Jog;
                if (speed <= IdleWalkExit) return AnimState.Idle;
                return AnimState.Walk;

            case AnimState.Jog:
                if (speed >= RunThreshold + RunHysteresis) return AnimState.Run;
                if (speed <= IdleWalkEnter) return AnimState.Idle;
                if (speed <= JogThreshold - JogHysteresis) return AnimState.Walk;
                return AnimState.Jog;

            case AnimState.Run:
                if (speed <= IdleWalkEnter) return AnimState.Idle;
                if (speed <= JogThreshold - JogHysteresis) return AnimState.Walk;
                if (speed <= RunThreshold - RunHysteresis) return AnimState.Jog;
                return AnimState.Run;
        }
        return AnimState.Idle;
    }
}
