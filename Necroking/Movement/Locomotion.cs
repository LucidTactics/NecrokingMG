using System;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.Game;
using Necroking.GameSystems;
using Necroking.Lib;
using Necroking.Render;
using Necroking.World;

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

/// <summary>Per-tick local-player input for the locomotion pass. Simulation
/// fills this from its <c>_necro*</c> fields (set by Game1 before Tick) and
/// hands it to <see cref="Locomotion.UpdateSpeeds"/> /
/// <see cref="Locomotion.UpdateFacing"/>. Applied to units whose AI is
/// <see cref="AIBehavior.PlayerControlled"/>. Headless sims pass
/// <see cref="None"/>.</summary>
public struct PlayerLocoInput
{
    /// <summary>Shift held and sprinting isn't externally forbidden (rope not
    /// taut). Unit-state disqualifiers (carrying a corpse, ghost mode) are
    /// checked inside the locomotion pass.</summary>
    public bool WantSprint;
    /// <summary>Player has a pending spell cast: movement input is ignored and
    /// the effort ramp decays at half rate (never rises), so cast-weaving
    /// doesn't eat the whole sprint ramp.</summary>
    public bool CastPlant;
    /// <summary>Rope-drag speed penalty, 0.05..1 (1 = no penalty).</summary>
    public float DragSlow;
    /// <summary>Mouse-driven target facing angle in degrees; NaN = none.</summary>
    public float FacingOverrideDeg;
    /// <summary>Frozen cast aim angle in degrees while casting or channeling;
    /// NaN = none. Owns the player's facing whenever set — independent of
    /// CastPlant, so a channel that permits movement still locks facing.</summary>
    public float CastAimAngleDeg;

    public static PlayerLocoInput None => new PlayerLocoInput
    {
        DragSlow = 1f,
        FacingOverrideDeg = float.NaN,
        CastAimAngleDeg = float.NaN,
    };
}

/// <summary>
/// The locomotion system — effort → speed → animation/facing, in one place.
///
/// Responsibilities and the per-tick data flow:
///   1. <see cref="SetEffort"/> — AI/player declare effort INTENT
///      (<see cref="MoveEffort"/> + optional routine speed cap). Intent only;
///      no speed is written here.
///   2. <see cref="UpdateSpeeds"/> (pre-AI) — the ONLY writer of
///      <see cref="Unit.MaxSpeed"/> at runtime. Ramps <see cref="Unit.EffortMult"/>
///      toward the effort's multiplier at the unit's physical accel/decel rate,
///      then composes ALL speed modifiers (buffed CombatSpeed, routine cap,
///      terrain, paralysis, player rope-drag / ghost mode) into the final cap.
///      Because every modifier is an input to this one computation, nothing can
///      clobber anything else by write ordering anymore.
///   3. AI builds <see cref="Unit.PreferredVel"/> from direction × MaxSpeed
///      (see <see cref="PreferredVelToward"/>); the movement integrator ramps
///      Velocity toward it under the Newtonian accel model.
///   4. Phase B (post-movement) — the smoothed loco vector
///      <c>Lerp(Velocity, PreferredVel, 0.2)</c> drives which movement
///      animation plays (by its length) and which way a moving unit faces.
/// </summary>
public static class Locomotion
{
    /// <summary>Blend factor for the loco vector: how strongly movement INTENT
    /// (PreferredVel) pulls the anim/facing vector away from measured Velocity.</summary>
    public const float LocoLerpFactor = 0.2f;

    /// <summary>Below this much movement *intent* (PreferredVel magnitude, wu/s)
    /// a unit is treated as standing still — filters residual-momentum slide so
    /// a stopped or can't-pathfind unit shows Idle, not walk-in-place. Must stay
    /// well under the slowest deliberate locomotion (deer feed at ~0.1 wu/s).</summary>
    public const float MoveIntentEpsilon = 0.05f;

    /// <summary>Dev fly-mode speed cap (ignores all other modifiers).</summary>
    public const float GhostModeSpeed = 20f;

    /// <summary>When the effort ramp starts rising from rest, it kickstarts to
    /// this fraction of the target gain so a sprint tap responds immediately
    /// instead of creeping off the line (from the original player sprint ramp).</summary>
    public const float RampStartFloor = 0.25f;

    // ─── Effort intent ───────────────────────────────────────────────────

    /// <summary>Declare a unit's movement effort. Intent only — the actual
    /// MaxSpeed is derived (with ramping and modifiers) by the per-tick
    /// <see cref="UpdateSpeeds"/> pass, so it persists correctly for amortized
    /// AI that doesn't re-issue effort every frame.
    ///
    /// <paramref name="routineCapMult"/> is an optional further cap as a
    /// fraction of the effort-max speed, for "lazy" routines where the gait
    /// should be Walk but slower than full walk speed — e.g.
    /// <c>SetEffort(Walk, 0.5f)</c> for a half-speed idle-roam stroll.
    /// Null resets the cap to 1 (full effort speed).</summary>
    public static void SetEffort(Unit u, MoveEffort effort, float? routineCapMult = null)
    {
        u.MoveEffort = effort;
        u.RoutineSpeedCap = routineCapMult ?? 1f;
    }

    /// <summary>The velocity multiplier a given <see cref="MoveEffort"/> applies
    /// to a unit's base CombatSpeed, from the unit def's jog/sprint multipliers
    /// (falling back to the locomotion-profile defaults). Walk/Normal = 1×.</summary>
    public static float EffortMultiplier(UnitDef def, MoveEffort effort)
    {
        switch (effort)
        {
            case MoveEffort.Hurry:
                return (def?.JogSpeedMultiplier > 0f) ? def.JogSpeedMultiplier : LocomotionProfile.DefaultJogMult;
            case MoveEffort.Sprint:
                return (def?.SprintSpeedMultiplier > 0f) ? def.SprintSpeedMultiplier : LocomotionProfile.DefaultSprintMult;
            default:
                return 1.0f; // Walk / Normal
        }
    }

    /// <summary>Compute the (unramped, unmodified) velocity cap a given effort
    /// would give this unit, without mutating anything. Useful when a routine
    /// needs the cap for clamping PreferredVel but isn't changing effort.</summary>
    public static float ResolveEffortSpeed(Unit u, GameData gameData, MoveEffort effort,
        float? routineCapMult = null)
    {
        var def = UnitUtil.ResolveDef(u, gameData);
        float speed = u.Stats.CombatSpeed * EffortMultiplier(def, effort);
        if (routineCapMult.HasValue) speed *= routineCapMult.Value;
        return speed;
    }

    /// <summary>Full sprint top speed for a unit: CombatSpeed × sprint
    /// multiplier (def override or default 4×). The single home for the
    /// "sprint mult with fallback" pattern used by pounce/trample/etc.</summary>
    public static float SprintTopSpeed(UnitDef def, in UnitStats stats)
        => stats.CombatSpeed * ((def?.SprintSpeedMultiplier > 0f)
            ? def.SprintSpeedMultiplier : LocomotionProfile.DefaultSprintMult);

    /// <summary>Initialize the speed cap on spawn / stat rebuild. The next
    /// <see cref="UpdateSpeeds"/> pass takes over from here.</summary>
    public static void ResetSpeed(Unit u)
    {
        u.MaxSpeed = u.Stats.CombatSpeed;
    }

    /// <summary>Movement intent from a desired direction: direction × the
    /// unit's current speed cap (× optional fraction).</summary>
    public static Vec2 PreferredVelToward(Unit u, Vec2 dir, float speedFrac = 1f)
        => dir * (u.MaxSpeed * speedFrac);

    // ─── Phase A: the single per-frame MaxSpeed computation ─────────────

    /// <summary>Per-tick derivation of every unit's MaxSpeed — the only runtime
    /// writer of <see cref="Unit.MaxSpeed"/>. Runs BEFORE UpdateAI so the AI
    /// builds PreferredVel from a cap that already includes terrain/paralysis
    /// (fixing the old ordering bugs where those were post-hoc multiplies that
    /// either got clobbered by AI writes or arrived after PreferredVel was
    /// built). A same-frame SetEffort only moves the ramp target; the ramp
    /// matches the Newtonian accel limit, so the one-frame pickup is identical
    /// to what the velocity integrator would have allowed anyway.</summary>
    public static void UpdateSpeeds(UnitArrays units, GameData gameData, TileGrid grid,
        float dt, in PlayerLocoInput player)
    {
        int w = grid?.Width ?? 0;
        int h = grid?.Height ?? 0;

        for (int i = 0; i < units.Count; i++)
        {
            var u = units[i];
            if (!u.Alive) continue;

            var def = UnitUtil.ResolveDef(u, gameData);
            bool isPlayer = u.AI == Data.AIBehavior.PlayerControlled;

            // Player input → effort intent. Carrying a corpse or ghost mode
            // disqualifies sprinting (preserves the prior behavior where
            // carrying suppressed the run bonus).
            if (isPlayer)
            {
                bool wantSprint = player.WantSprint && u.CarryingCorpseID < 0 && !u.GhostMode;
                SetEffort(u, wantSprint ? MoveEffort.Sprint : MoveEffort.Normal);
            }

            // Continuous effort ramp: EffortMult moves toward the effort's
            // target multiplier at the unit's physical accel/decel rate
            // (dMult/dt = accel / CombatSpeed — the exact rate the Newtonian
            // velocity model can follow, so the cap is always honest; derived
            // from the original accel-based player sprint ramp).
            float targetMult = EffortMultiplier(def, u.MoveEffort);
            float cs = MathF.Max(0.01f, u.Stats.CombatSpeed);
            float em = u.EffortMult;
            if (em <= 0f) em = 1f; // safety for uninitialized units

            if (isPlayer && player.CastPlant)
            {
                // Cast plant: the ramp decays at HALF rate (and never rises)
                // while casting, so a quick cast mid-sprint doesn't cost the
                // whole ramp.
                float decel = def?.MaxDeceleration ?? gameData.Settings.Combat.MaxDeceleration;
                if (u.ActiveBuffs.Count > 0)
                   decel = BuffSystem.GetModifiedExtra(units, i, "MaxAcceleration", decel);
                em = MathF.Max(1f, em - (decel / cs) * 0.5f * dt);
            }
            else if (em < targetMult)
            {
                float accel = def?.MaxAcceleration ?? gameData.Settings.Combat.MaxAcceleration;
                if (u.ActiveBuffs.Count > 0)
                    accel = BuffSystem.GetModifiedExtra(units, i, "MaxAcceleration", accel);
                // Kickstart: a ramp rising from rest starts at a floor fraction
                // of the gain so a sprint tap responds immediately.
                float floor = 1f + RampStartFloor * (targetMult - 1f);
                if (em < floor) em = floor;
                em = MathF.Min(targetMult, em + (MathF.Max(0.01f, accel) / cs) * dt);
            }
            else if (em > targetMult)
            {
                float decel = def?.MaxDeceleration ?? gameData.Settings.Combat.MaxDeceleration;
                em = MathF.Max(targetMult, em - (MathF.Max(0.01f, decel) / cs) * dt);
            }
            u.EffortMult = em;

            // Ghost mode: dev fly speed, ignores physical modifiers (still
            // honors rope drag like the old player branch did).
            if (u.GhostMode)
            {
                u.MaxSpeed = GhostModeSpeed * (isPlayer ? player.DragSlow * (player.WantSprint ? 2 : 1) : 1f);
                continue;
            }

            // Paralysis stun: hard zero (the unit is also Incap-locked; a zero
            // cap keeps every MaxSpeed consumer honest during the stun).
            if (u.ParalysisStunTimer > 0f)
            {
                u.MaxSpeed = 0f;
                continue;
            }

            // Base speed with buffs, then compose every modifier exactly once.
            float speed = u.ActiveBuffs.Count > 0
                ? BuffSystem.GetModifiedStat(units, i, BuffStat.CombatSpeed, u.Stats.CombatSpeed)
                : u.Stats.CombatSpeed;
            speed *= em;
            if (u.RoutineSpeedCap > 0f) speed *= u.RoutineSpeedCap;

            // Paralysis slow phase: lerps 0.7× → 0 over the slow duration.
            if (u.ParalysisSlowTimer > 0f)
                speed *= PotionSystem.ParalyzeSlowStartMultiplier
                    * MathF.Max(u.ParalysisSlowTimer / PotionSystem.ParalyzeSlowDuration, 0f);

            // Terrain (e.g. shallow water 0.5×). Skips units whose movement is
            // owned elsewhere (physics-impulse, dodge-hop, trample charge).
            if (w > 0 && h > 0 && !u.InPhysics && u.DodgeTimer <= 0f
                && u.ChargePhase != 1 && u.ChargePhase != 3)
            {
                int gx = (int)MathF.Floor(u.Position.X);
                int gy = (int)MathF.Floor(u.Position.Y);
                if (gx >= 0 && gx < w && gy >= 0 && gy < h)
                {
                    var terrain = grid.GetTerrain(gx, gy);
                    if (terrain != TerrainType.Open) // fast path: most tiles
                    {
                        float mult = TerrainCosts.GetSpeedMultiplier(terrain);
                        if (mult < 1f) speed *= mult;
                    }
                }
            }

            // Player rope-drag penalty (taut rope hauling a corpse).
            if (isPlayer) speed *= player.DragSlow;

            u.MaxSpeed = speed;
        }
    }

    // ─── Phase B: loco vector → movement animation ───────────────────────

    private static bool IsLocoClass(AnimState s)
        => s == AnimState.Idle || s == AnimState.Walk
        || s == AnimState.Jog || s == AnimState.Run;

    /// <summary>Post-movement pass: compute every unit's smoothed loco vector
    /// and pick its movement-animation tier from that vector's LENGTH — the
    /// single gait selector for all units (archetype AI, legacy AI, and the
    /// player alike). Effort influences the tier only through speed now
    /// (PreferredVel is built from the effort-ramped MaxSpeed and pulled in by
    /// the lerp) — no separate bias that could disagree with actual motion.
    ///
    /// Only the locomotion channel is steered: a deliberate non-loco
    /// RoutineAnim (Feed, Sleep, cast poses) and any OverrideAnim are
    /// untouched. Zero movement INTENT (PreferredVel below
    /// <see cref="MoveIntentEpsilon"/>) forces Idle so residual momentum
    /// doesn't walk-in-place.</summary>
    public static void UpdateLocoVectorsAndGait(UnitArrays units, GameData gameData)
    {
        for (int i = 0; i < units.Count; i++)
        {
            var u = units[i];
            if (!u.Alive) continue;

            u.LocoVector = Vec2.Lerp(u.Velocity, u.PreferredVel, LocoLerpFactor);

            var prev = u.RoutineAnim.State;
            if (!IsLocoClass(prev)) continue;

            float tierSpeed = u.PreferredVel.Length() <= MoveIntentEpsilon
                ? 0f : u.LocoVector.Length();
            var profile = ProfileFor(u, gameData);
            u.RoutineAnim = AnimRequest.Locomotion(profile.PickTier(prev, tierSpeed));
        }
    }

    /// <summary>The unit's locomotion profile (gait thresholds + per-gait
    /// feet-lock velocities), via the memoized def lookup. Units without a def
    /// (raw scenario spawns) get derived defaults anchored on their live
    /// CombatSpeed.</summary>
    public static LocomotionProfile ProfileFor(Unit u, GameData gameData)
    {
        var def = gameData != null ? UnitUtil.ResolveDef(u, gameData) : null;
        return LocomotionProfile.FromUnit(def, u.Stats.CombatSpeed);
    }

    /// <summary>Playback-rate scalar for a locomotion state at a given velocity
    /// — <c>speed / animVelForGait</c>, clamped, so the foot cycle locks to
    /// ground motion. Returns 1 for non-locomotion states (graceful fallback).
    /// The single playback formula for all units.</summary>
    public static float ComputePlayback(Unit u, GameData gameData, AnimState state, float speed)
    {
        var profile = ProfileFor(u, gameData);
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

    // ─── Phase B: facing ─────────────────────────────────────────────────

    private const float Rad2Deg = 180f / MathF.PI;

    /// <summary>Per-tick facing pass — the single priority ladder for which
    /// way every unit faces. All turns are rate-capped via
    /// <see cref="FacingUtil.TurnToward"/>; movement facing follows the
    /// smoothed <see cref="Unit.LocoVector"/> (so the body blends between
    /// where it's going and where it intends to go, matching the gait pick).
    ///
    /// Ladder (first match wins):
    ///   Player: cast-plant aim (boosted turn) → loco vector while in
    ///     FaceVelocityMode (jog/run hysteresis) → cursor.
    ///   1. Engaged combat target — unless actively fleeing it (then face
    ///      where we're GOING, not back over the shoulder).
    ///   2. Loco vector (movement direction).
    ///   3. Stationary with a combat Target → face it.
    ///
    /// Skip guards: trample charge owns facing during ChargePhase; physics
    /// ragdolls keep their launch facing; Incap/airborne can't rotate.</summary>
    public static void UpdateFacing(UnitArrays units, float dt, GameData gameData,
        in PlayerLocoInput player)
    {
        for (int i = 0; i < units.Count; i++)
        {
            var u = units[i];
            if (!u.Alive) continue;
            // FacingUtil.TurnToward enforces these guards too, but short-circuiting
            // at this level saves the target-angle resolution for units that can't
            // rotate anyway.
            if (u.Incap.IsLocked) continue;
            if (u.JumpPhase >= 2) continue;
            if (u.ChargePhase > 0) continue; // TrampleSystem owns facing during charge
            // Tumbling ragdolls keep the facing they were launched with —
            // PhysicsSystem writes Velocity each tick, and letting the
            // face-away-from-attacker priority act on it turned launched units
            // to face their flight direction mid-arc (visible pop on launch).
            if (u.InPhysics) continue;

            // Player-controlled (necromancer): two facing sources, hysteresis
            // between them. Walk gait → face the mouse. Jog/Run gait (or an
            // active sprint ramp) → face the loco vector so sprinting backward
            // doesn't reverse-play the animation. Turn rate applies either way.
            if (u.AI == Data.AIBehavior.PlayerControlled)
            {
                if (u.IsLockedByAction()) continue;

                var profile = ProfileFor(u, gameData);
                float speed = u.LocoVector.Length();
                float enterT = profile.JogThreshold + profile.JogHysteresis;
                float exitT  = profile.JogThreshold - profile.JogHysteresis;
                if (u.FaceVelocityMode)
                {
                    if (speed <= exitT) u.FaceVelocityMode = false;
                }
                else
                {
                    // Sprint intent flips to velocity-facing early (EffortMult
                    // rising past 1 = the ramp is engaged), so a sprint start
                    // reads immediately even before speed crosses the jog line.
                    if (speed >= enterT || (speed >= 0.2f && u.EffortMult > 1.05f))
                        u.FaceVelocityMode = true;
                }

                float targetAngle;
                float turnMult = 1f;
                if (!float.IsNaN(player.CastAimAngleDeg))
                {
                    // Cast aim: the body swings to face the frozen cast aim point
                    // (where the spell will actually go — not the live cursor) at a
                    // boosted rate, overriding the walk/jog facing hysteresis. The
                    // pivot overlaps the brake, so even a 180° sprint-cast is aimed
                    // before the cast anim's effect frame. Keyed off the aim angle
                    // alone (not CastPlant) so a hold-channel with movement allowed
                    // (ChannelStopsMovement off) still owns the facing — the caster
                    // may walk, but never voluntarily turns away from its target.
                    targetAngle = player.CastAimAngleDeg;
                    turnMult = gameData.Settings.Animation.CastTurnBoost;
                }
                else if (u.FaceVelocityMode && u.LocoVector.LengthSq() > 0.01f)
                {
                    targetAngle = MathF.Atan2(u.LocoVector.Y, u.LocoVector.X) * Rad2Deg;
                }
                else if (!float.IsNaN(player.FacingOverrideDeg))
                {
                    targetAngle = player.FacingOverrideDeg;
                }
                else
                {
                    continue; // nothing to aim at yet (pre-input frames)
                }
                FacingUtil.TurnToward(u, targetAngle, dt, gameData, turnMult);
                continue;
            }

            // Priority 1: turn toward the engaged target — UNLESS we're actively
            // fleeing it. A unit retreating from its engaged target (e.g. a deer
            // bolting from an attacker) should face where it's GOING, not look back
            // over its shoulder; otherwise it runs away while facing the threat,
            // which reads as a backwards run under a forward-run animation. When the
            // loco vector points away from the target we fall through to Priority 2
            // (face movement direction). Stationary-but-engaged units (a wolf waiting
            // out its cooldown) keep facing the target since the loco vector ~ 0.
            if (!u.EngagedTarget.IsNone && u.EngagedTarget.IsUnit)
            {
                int ti = UnitUtil.ResolveUnitIndex(units, u.EngagedTarget.UnitID);
                if (ti >= 0)
                {
                    Vec2 toTarget = units[ti].Position - u.Position;
                    bool fleeingTarget = u.LocoVector.LengthSq() > 0.25f
                        && u.LocoVector.Dot(toTarget) < 0f;
                    if (!fleeingTarget)
                    {
                        FacingUtil.TurnTowardPosition(u, units[ti].Position, dt, gameData);
                        continue;
                    }
                }
            }

            // Priority 2: face the loco vector — the same smoothed blend of
            // Velocity and PreferredVel that drives the gait pick, so body
            // direction and movement animation always agree.
            Vec2 faceDir = u.LocoVector;

            // Priority 3: Stationary with a combat target (e.g. wolf waiting for
            // cooldown) — keep facing the target so the idle frame reads naturally.
            if (faceDir.LengthSq() < 0.0025f && u.Target.IsUnit)
            {
                int ti = UnitUtil.ResolveUnitIndex(units, u.Target.UnitID);
                if (ti >= 0)
                    faceDir = units[ti].Position - u.Position;
            }

            if (faceDir.LengthSq() > 0.0025f)
            {
                float targetAngle = MathF.Atan2(faceDir.Y, faceDir.X) * Rad2Deg;
                FacingUtil.TurnToward(u, targetAngle, dt, gameData);
            }
        }
    }
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
        => MathUtil.AngleDeltaDeg(current, target);

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
/// Per-unit locomotion tuning: gait-tier thresholds and the per-gait feet-lock
/// velocities. Both the tier picker (<see cref="Locomotion.UpdateLocoVectorsAndGait"/>)
/// and the playback scaler (<see cref="Locomotion.ComputePlayback"/>) read from
/// a profile so gait choice and playback rate always come from the same source
/// of truth.
///
/// Build via <see cref="FromUnit"/>:
///   - Per-gait feet-lock velocities come from pixel-stride calibration when the
///     sprite has it; otherwise derived defaults anchored on CombatSpeed
///     (walk = CS, jog = CS × jogMult, run = CS × sprintMult). Per-gait
///     <c>AnimXxxVelOverride</c> fields on the def win in both cases.
///   - Gait thresholds sit at the midpoint between adjacent gait max-velocities
///     so the playback rate is continuous through transitions.
///   - Hysteresis bands stay small — they only need to absorb single-frame
///     velocity noise, not hide a frame-reset jolt.
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
    // above the ceiling, it looks cartoonishly sped up.
    // MaxPlayback=3.0 gives headroom for the sprint case (vel=4×MS hitting Run gait):
    // even at the worst case where pixel-derived runVel underestimates, 4×MS / runVel
    // stays under 3.0 in practice.
    public const float WalkFloorPlayback = 0.25f;
    public const float MaxPlayback = 3.0f;

    // Default per-effort velocity multipliers used when a UnitDef doesn't
    // specify its own. Biped pattern: jog ≈ 2× walk, sprint ≈ 4× walk. Per-
    // unit overrides via UnitDef.JogSpeedMultiplier / SprintSpeedMultiplier
    // (e.g. wolf 3/9, horse 3/9, cheetah 5/30). Gait thresholds derive from
    // these as midpoints between adjacent gait max-velocities.
    public const float DefaultJogMult = 2.0f;
    public const float DefaultSprintMult = 4.0f;

    /// <summary>Per-gait feet-lock velocity (world units / sec) — the velocity at
    /// which the gait's authored sprite cycle exactly matches ground motion.
    /// At runtime, playback rate for a gait = unit.velocity / animVelForGait,
    /// clamped.</summary>
    public readonly float AnimWalkVel;
    public readonly float AnimJogVel;
    public readonly float AnimRunVel;

    public readonly float JogThreshold;
    public readonly float RunThreshold;
    public readonly float JogHysteresis;
    public readonly float RunHysteresis;

    private LocomotionProfile(
        float animWalk, float animJog, float animRun,
        float jogThreshold, float runThreshold, float jogHys, float runHys)
    {
        AnimWalkVel = animWalk;
        AnimJogVel = animJog;
        AnimRunVel = animRun;
        JogThreshold = jogThreshold;
        RunThreshold = runThreshold;
        JogHysteresis = jogHys;
        RunHysteresis = runHys;
    }

    /// <summary>Build the profile for a UnitDef. Per-gait feet-lock velocities
    /// come from pixel-stride calibration when the sprite has it; without
    /// calibration (or without a def at all — raw scenario spawns) they fall
    /// back to derived defaults anchored on CombatSpeed (walk = CS, jog =
    /// CS × jogMult, run = CS × sprintMult — i.e. "the anim plays at 1.0× when
    /// moving at that gait's max effort speed"). Per-gait override values on
    /// the def win over auto-computed values in both cases.
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
    public static LocomotionProfile FromUnit(UnitDef def, float fallbackCombatSpeed = 8f)
    {
        float baseSpeed = def?.Stats?.CombatSpeed ?? fallbackCombatSpeed;
        float jogMult = def?.JogSpeedMultiplier > 0f
            ? def.JogSpeedMultiplier : DefaultJogMult;
        float sprintMult = def?.SprintSpeedMultiplier > 0f
            ? def.SprintSpeedMultiplier : DefaultSprintMult;

        // Playback anchors: pixel-derived feet-lock velocities when calibrated,
        // else CombatSpeed-derived defaults. Override fields win when set
        // (designer escape hatch — e.g. force walk to lock at CombatSpeed
        // instead, trading groundedness for natural cadence).
        float pixelWalk, pixelJog, pixelRun;
        if (def == null || !TryComputePixelVels(def, out pixelWalk, out pixelJog, out pixelRun))
        {
            pixelWalk = baseSpeed;
            pixelJog  = baseSpeed * jogMult;
            pixelRun  = baseSpeed * sprintMult;
        }
        float walk = def?.AnimWalkVelOverride ?? pixelWalk;
        float jog  = def?.AnimJogVelOverride  ?? pixelJog;
        float run  = def?.AnimRunVelOverride  ?? pixelRun;

        // Threshold anchor = CombatSpeed, modulated by per-unit jog/sprint
        // multipliers. JogThreshold = midpoint between walk-max (CS) and jog-max
        // (CS × jogMult). RunThreshold = midpoint between jog-max and sprint-max
        // (CS × sprintMult). For biped (2/4): 1.5xCS and 3xCS. For quadruped
        // (3/9): 2xCS and 6xCS.
        float jogThresh = baseSpeed * (1f + jogMult) * 0.5f;
        float runThresh = baseSpeed * (jogMult + sprintMult) * 0.5f;
        return Build(walk, jog, run, jogThresh, runThresh);
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
        if (def.SpriteData?.Calibration == null) return false;

        var cal = def.SpriteData.Calibration;
        float bodySub = def.IsQuadruped ? cal.IdleFootSpreadPx : 0f;
        float duty = def.DutyCycle > 0f ? def.DutyCycle : StrideCalibration.DefaultDutyCycle;
        pixelWalk = StrideCalibration.ResolveAnimVel(cal.Walk, def.SpriteWorldHeight, def.SpriteScale, bodySub, duty);
        pixelJog  = StrideCalibration.ResolveAnimVel(cal.Jog,  def.SpriteWorldHeight, def.SpriteScale, bodySub, duty);
        pixelRun  = StrideCalibration.ResolveAnimVel(cal.Run,  def.SpriteWorldHeight, def.SpriteScale, bodySub, duty);
        return pixelWalk > 0f && pixelJog > 0f && pixelRun > 0f;
    }

    /// <summary>Final assembly of a profile given per-gait feet-lock
    /// velocities and pre-computed gait thresholds. Hysteresis bands derived
    /// from gait-velocity spread.</summary>
    private static LocomotionProfile Build(
        float walk, float jog, float run, float jogThresh, float runThresh)
    {
        // Hysteresis is small — just enough to suppress single-frame velocity
        // noise (ORCA jitter, accel ramp wobble). The frame-reset visual hitch
        // is handled by AnimController's foot-phase carryover.
        float jogHys = MathUtil.Clamp((jog - walk) * 0.05f, 0.05f, 0.5f);
        float runHys = MathUtil.Clamp((run - jog) * 0.05f, 0.05f, 0.5f);
        return new LocomotionProfile(walk, jog, run, jogThresh, runThresh, jogHys, runHys);
    }

    /// <summary>Pick the locomotion state tier for the given speed, respecting
    /// hysteresis around thresholds. Speed is the length of the unit's smoothed
    /// loco vector — Lerp(Velocity, PreferredVel, 0.2) — so movement INTENT
    /// already pulls the pick slightly ahead of measured velocity; there is no
    /// separate effort bias.</summary>
    public AnimState PickTier(AnimState prev, float speed)
    {
        float biasedSpeed = speed;

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

}
