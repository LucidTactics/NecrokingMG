using System;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.Render;

namespace Necroking.Movement;

// ═══════════════════════════════════════════════════════════════════════════
//  Locomotion.cs — THE single home for unit effort, speed, movement animation
//  and facing selection.
//
//  Everything that answers one of these questions lives in this file, and
//  nowhere else:
//    - What does an effort level (MoveEffort) mean in velocity terms?
//    - What is a unit's MaxSpeed right now? (the ONLY writer of Unit.MaxSpeed)
//    - Which movement animation (Idle/Walk/Jog/Run) plays at a given speed?
//    - How fast does that animation play back (feet-lock)?
//    - Which direction does a moving unit face?
//
//  Other systems provide INPUTS (effort intent, buff/terrain/potion state,
//  player input) or consume OUTPUTS (MaxSpeed, RoutineAnim, FacingAngle) —
//  they never compute any of the above themselves.
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>AI's stated locomotion intent, used as a bias on top of raw velocity
/// when choosing Walk vs Jog vs Run. Lets a patrol pick Walk gait even when the
/// path lets it move faster, or lets a charging unit pick Run even before its
/// actual velocity has reached the run threshold (so the gait switch happens at
/// the moment of intent, not several frames later when speed catches up).
///
/// Imported from the Nightfall Rogue project's MoveEffort concept. Bias is
/// applied only to the gait-tier picker, NOT to playback rate — feet still
/// lock to ground motion at actual velocity within whatever gait is chosen.
/// </summary>
public enum MoveEffort : byte
{
    /// <summary>No intent bias — gait is purely a function of measured velocity.
    /// The default and the most common case.</summary>
    Normal = 0,
    /// <summary>Bias toward Walk gait. Patrolling, cautious approach, sneak.
    /// A unit that physically COULD jog will still stay in Walk gait unless
    /// velocity is well above the jog threshold.</summary>
    Walk = 1,
    /// <summary>Bias toward Jog gait. "Get there, but don't sprint" — routine
    /// reposition, formation-up, follow-orders. Snaps into Jog earlier than
    /// raw velocity would warrant.</summary>
    Hurry = 2,
    /// <summary>Bias toward Run gait. Combat charge, urgent retreat, chase.
    /// Snaps into Run on intent, well before measured velocity catches up.</summary>
    Sprint = 3,
}

/// <summary>
/// Central turn-rate-limited facing helper. Every voluntary facing change on
/// any unit should go through <see cref="TurnToward"/> or
/// <see cref="TurnTowardPosition"/> so the angular-velocity cap
/// (<c>UnitDef.TurnSpeed</c> or <c>GameSettings.Combat.TurnSpeed</c>) is
/// respected. Before this helper existed, handlers wrote
/// <c>unit.FacingAngle = ...</c> directly and snapped instantly, bypassing the
/// rate cap that the central facing pass was supposed to enforce.
///
/// PlayerControlled units obey turn rate too: mouse-driven facing rotates
/// smoothly toward the cursor (and toward velocity direction during jog/run).
/// Incap-locked and airborne units (JumpPhase ≥ 2) skip the rotation because
/// their facing is frozen by other systems.
///
/// No turn acceleration is modeled — turn speed is a flat deg/s. Units always
/// rotate at the same rate when they do rotate; they don't ramp up/down.
/// </summary>
public static class FacingUtil
{
    public const float DefaultTurnSpeed = 360f;

    /// <summary>Signed short-way angle from <paramref name="current"/> to
    /// <paramref name="target"/>, in the range (-180, 180] degrees.</summary>
    public static float AngleDiff(float target, float current)
    {
        float diff = target - current;
        while (diff > 180f) diff -= 360f;
        while (diff < -180f) diff += 360f;
        return diff;
    }

    /// <summary>
    /// Rotate <paramref name="unit"/>'s facing toward <paramref name="targetAngle"/>
    /// (degrees), clamped by the unit's turn speed × <paramref name="dt"/>.
    /// Incap'd / airborne units don't rotate.
    /// </summary>
    public static void TurnToward(Unit unit, float targetAngle, float dt, GameData gameData,
        float rateMult = 1f)
    {
        // Can't rotate while knocked down / airborne.
        if (unit.Incap.IsLocked) return;
        if (unit.JumpPhase >= 2) return;

        float turnSpeed = ResolveTurnSpeed(unit, gameData) * rateMult;
        float diff = AngleDiff(targetAngle, unit.FacingAngle);
        float maxTurn = turnSpeed * dt;
        unit.FacingAngle += MathUtil.Clamp(diff, -maxTurn, maxTurn);
    }

    /// <summary>Rotate toward the angle pointing from <paramref name="unit"/>
    /// toward <paramref name="worldTarget"/>. No-op if target is on top of
    /// the unit.</summary>
    public static void TurnTowardPosition(Unit unit, Vec2 worldTarget, float dt, GameData gameData)
    {
        var dir = worldTarget - unit.Position;
        if (dir.LengthSq() < 0.01f) return;
        float angle = System.MathF.Atan2(dir.Y, dir.X) * (180f / System.MathF.PI);
        TurnToward(unit, angle, dt, gameData);
    }

    /// <summary>Per-unit TurnSpeed with fallback to the global default.</summary>
    public static float ResolveTurnSpeed(Unit unit, GameData gameData)
    {
        // Memoized def lookup — this runs per facing unit per frame.
        var def = UnitUtil.ResolveDef(unit, gameData);
        if (def?.TurnSpeed.HasValue == true) return def.TurnSpeed.Value;
        return gameData.Settings.Combat.TurnSpeed;
    }

    /// <summary>Unit direction vector for a FacingAngle in DEGREES (the engine's facing
    /// convention). Replaces scattered open-coded `new Vec2(Cos(a*PI/180), Sin(a*PI/180))`
    /// — and the one site that forgot the deg-&gt;rad multiply entirely.</summary>
    public static Vec2 AngleToDir(float deg)
    {
        float rad = deg * (System.MathF.PI / 180f);
        return new Vec2(System.MathF.Cos(rad), System.MathF.Sin(rad));
    }

    /// <summary>The direction the unit is currently facing (FacingAngle is in degrees).</summary>
    public static Vec2 ForwardDir(Unit unit) => AngleToDir(unit.FacingAngle);
}

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
        // Need valid pixel velocities to use the new mode. If any gait is missing
        // (or legacy mode / no calibration), drop back to legacy.
        if (!TryComputePixelVels(def, out float pixelWalk, out float pixelJog, out float pixelRun))
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

    /// <summary>Raw pixel-stride-derived feet-lock velocity for each gait, BEFORE
    /// the per-gait <c>AnimXxxVelOverride</c> fields are applied — i.e. what the
    /// unit uses when no overrides are set. Returns false when the unit is in
    /// legacy gait mode, has no stride calibration, or any gait's calibration is
    /// invalid (callers should show "n/a"). Single source of truth shared with
    /// <see cref="FromUnit"/>; the unit editor also displays these next to the
    /// override inputs.
    ///
    /// Details of the computation:
    ///  - SpriteScale is passed alongside SpriteWorldHeight so the pixel→world
    ///    conversion uses the unit's actual rendered height (some units like
    ///    Wretched render at 0.9× scale). Omitting it would overstate cycle
    ///    distance and underestimate feet-lock velocity — feet would drag.
    ///  - For quadrupeds, IdleFootSpreadPx is subtracted from each gait's stride:
    ///    the stride-spread pixel measurement on a 4-legged unit captures
    ///    front-paw-to-rear-paw distance, dominated by body length; the idle
    ///    stance gives the body length to strip so the residual is the actual
    ///    leg-stride that drives ground motion.
    ///  - DutyCycle 0 means "use default (biped 0.5)"; non-zero values like 0.75
    ///    reshape the cycle-distance formula for unusual gaits (bound, gallop).</summary>
    public static bool TryComputePixelVels(UnitDef def,
        out float pixelWalk, out float pixelJog, out float pixelRun)
    {
        pixelWalk = pixelJog = pixelRun = 0f;
        if (def.LegacyGaitMode || def.SpriteData?.Calibration == null) return false;

        var cal = def.SpriteData.Calibration;
        float bodySub = def.IsQuadruped ? cal.IdleFootSpreadPx : 0f;
        float duty = def.DutyCycle > 0f ? def.DutyCycle : StrideCalibration.DefaultDutyCycle;
        pixelWalk = StrideCalibration.ResolveAnimVel(cal.Walk, def.SpriteWorldHeight, def.SpriteScale, bodySub, duty);
        pixelJog  = StrideCalibration.ResolveAnimVel(cal.Jog,  def.SpriteWorldHeight, def.SpriteScale, bodySub, duty);
        pixelRun  = StrideCalibration.ResolveAnimVel(cal.Run,  def.SpriteWorldHeight, def.SpriteScale, bodySub, duty);
        return pixelWalk > 0f && pixelJog > 0f && pixelRun > 0f;
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
