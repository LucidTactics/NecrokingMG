using System;
using System.Collections.Generic;
using Necroking.Core;
using Necroking.Lib;
using Necroking.Movement;
using Necroking.Render;

namespace Necroking.NightfallPorts;

/// <summary>
/// Rogue jump — a faithful port of the (well-tested) Nightfall Rogue leap
/// (`Unit_MoveSpecial.ProcessJumpAction` + `UnitHandle.SlideMoveTowards` +
/// `Asd.ArchProgress`). Its defining trait, and the reason it is NOT the engine's
/// <see cref="Necroking.GameSystems.JumpSystem"/>, is that it owns NO dedicated
/// jump sprites. Instead it <b>abuses partial states of animations every unit
/// already has</b>:
///
///   • Take-off  — <see cref="AnimState.Standup"/> seeked to its MIDPOINT: the back
///                 half of a stand-up (crouch → full stand) reads as the launch
///                 spring. This mid-clip seek is the technique the engine's jump
///                 system lacks.
///   • Airborne  — <see cref="AnimState.Fall"/> held while the body follows a
///                 parabolic arc (Nightfall marked the airborne pose `Fall`).
///   • Landing   — <see cref="AnimState.Standup"/> from frame 0 (crouch → stand =
///                 absorb the impact and rise), then control is handed back.
///
/// Every abused state has a fallback chain in <see cref="AnimController"/>
/// (Standup→Idle, Fall→Knockdown), so the jump works on ANY unit even if it was
/// never authored with jump clips — which is the whole point.
///
/// <b>Slide-through (a per-jump setting):</b> by default the unit plants for the
/// take-off and landing animations (and stops on landing). When a jump is started
/// with <c>slideThrough</c> it instead keeps gliding at its entry momentum through
/// those ground animations and hands that velocity back on landing — so a running
/// leap flows run → squat-slide → arc → land-slide → run without a dead stop.
///
/// Isolation: all per-unit jump state lives in <see cref="_active"/> (keyed by
/// <see cref="Unit.Id"/>). Nothing is added to the Unit model, and the engine's
/// JumpSystem phase machine is untouched — the two never run on the same unit
/// (<see cref="Register"/> refuses if an engine jump is in progress).
///
/// Integration mirrors JumpSystem: <see cref="TickUnit"/> is driven from the
/// per-unit loop in <c>Game1.UpdateAnimations</c>; returning true means "the jump
/// owns this unit's animation + motion this frame — skip normal handling and
/// continue". Height rendering rides on <see cref="Unit.Z"/> exactly as the engine
/// jump does; <see cref="Unit.Jumping"/> suppresses AI intent + ORCA (and makes
/// Simulation skip the unit's own Position integration, so the jump moves it).
/// </summary>
public static class RogueJump
{
    // --- Which existing animations to abuse (see class summary) ---
    private const AnimState TakeoffAnim  = AnimState.Standup; // played from its MIDPOINT
    private const AnimState AirborneAnim = AnimState.Fall;    // held while arcing
    private const AnimState LandAnim     = AnimState.Standup; // played from the START

    // --- Tuning ---
    /// <summary>Fixed horizontal air speed for the cursor jump (units/sec):
    /// flight time = distance/speed. Matches Nightfall's `time_cost = dist/speed`.</summary>
    private const float DefaultJumpSpeed = 12f;
    private const float MinAirDuration   = 0.35f;  // floor so a zero-distance jump still arcs briefly
    private const float TakeoffPlayback  = 1.5f;   // Nightfall played the launch stand-up at 1.5x
    private const float LandPlayback     = 1.5f;
    private const float RecoverySafetyTimeout = 1.2f; // force hand-back if the land anim never resolves

    /// <summary>Ported parabola gain: peak height = time² × this. Faithful to
    /// Nightfall's `arch_height = tc² × 10/8 × 3` ("gravity × time²") — a ballistic
    /// arc, so longer leaps rise higher.</summary>
    private const float ArchGain = 10f / 8f * 3f;

    // --- Momentum jump (Space): scale the leap by the unit's CURRENT speed s ---
    //   flight time  = DurationSqrtCoeff · √s     (∝ √speed)
    //   leap length  = DistancePow15Coeff · s^1.5 (∝ speed^1.5)
    //   peak height  = time² · ArchGain           (∝ speed, since time ∝ √s)
    // Tuned around the necromancer's ~8 u/s run: ≈0.8s aloft, ≈7u far, ≈2.3u high.
    // A standing unit barely hops; a sprinter leaps far and high. Tune these two.
    private const float DurationSqrtCoeff  = 0.28f;
    private const float DistancePow15Coeff = 0.31f;

    // state: 0 = launch setup, 1 = crouch/spring (partial Standup) wait,
    //        2 = airborne slide + arc, 3 = landing recovery
    private sealed class JumpAction
    {
        public Vec2  Dir;          // normalized horizontal launch direction
        public float AirDistance;  // airborne leap length from liftoff (world units)
        public Vec2  Dest;         // committed at liftoff = liftoffPos + Dir·AirDistance
        public float TimeCost;     // remaining airborne seconds (counts down in state 2)
        public float TcOrig;       // total airborne seconds (parabola denominator)
        public float ArchHeight;   // peak Z (world units)
        public Vec2  SlideVel;     // momentum carried through the ground anims (slide mode)
        public bool  SlideThrough; // setting: glide through take-off/landing anims, keep speed
        public int   State;
        public float RecoverTimer;
    }

    // Per-unit jump state, keyed by Unit.Id — the port's entire footprint.
    private static readonly Dictionary<uint, JumpAction> _active = new();

    /// <summary>True while <paramref name="unitId"/> has a rogue jump in flight. The
    /// animation loop checks this before calling <see cref="TickUnit"/>.</summary>
    public static bool IsJumping(uint unitId) => _active.ContainsKey(unitId);

    // --- Initiation ---

    /// <summary>
    /// Momentum jump (the Space-key leap): spring off in the direction the unit is
    /// currently moving, scaled by its current speed. Duration ∝ √speed and distance
    /// ∝ speed^1.5, so height (duration²·gain) ∝ speed — a standing unit barely hops,
    /// a sprinting one leaps far and high. Reads <see cref="Unit.Velocity"/> at call
    /// time (before the jump zeroes it); falls back to facing when near-stationary.
    /// <paramref name="slideThrough"/> = keep the run momentum gliding through the
    /// take-off/landing anims and hand it back on landing (see class summary).
    /// </summary>
    public static void BeginMomentumJump(UnitArrays units, int idx, bool slideThrough = false, float slide_factor=0.5f)
    {
        if (idx < 0 || idx >= units.Count) return;
        var u = units[idx];

        float s = u.Velocity.Length();
        Vec2 dir = s > 0.05f ? u.Velocity * (1f / s) : FacingUtil.ForwardDir(u);
        float duration = DurationSqrtCoeff * MathF.Sqrt(s);
        float distance = DistancePow15Coeff * MathF.Pow(s, 1.5f);

        // Carry the current run velocity as slide momentum (used only in slide mode).
        Register(units, idx, dir, distance, duration, u.Velocity * slide_factor, slideThrough, $"momentum s={s:F1}");
    }

    /// <summary>
    /// Fixed-speed leap to an explicit <paramref name="dest"/> (used by the
    /// <c>roguejump</c> dev command). Duration = distance / <paramref name="speed"/>.
    /// Always plants (no slide-through) — it's a targeted hop, not a running leap.
    /// </summary>
    public static void BeginJump(UnitArrays units, int idx, Vec2 dest, float speed = DefaultJumpSpeed)
    {
        if (idx < 0 || idx >= units.Count) return;
        Vec2 to = dest - units[idx].Position;
        float dist = to.Length();
        Vec2 dir = dist > 1e-4f ? to * (1f / dist) : FacingUtil.ForwardDir(units[idx]);
        Register(units, idx, dir, dist, dist / MathF.Max(0.5f, speed), Vec2.Zero, false, "cursor");
    }

    /// <summary>Register a jump on the unit: a launch <paramref name="dir"/> +
    /// <paramref name="airDistance"/> (the airborne dest is committed at liftoff so a
    /// take-off slide extends the leap rather than eating it) and an explicit airborne
    /// <paramref name="duration"/> (floored at <see cref="MinAirDuration"/>). Shared by
    /// both initiators. No-ops if the unit is dead, incap-locked, or already mid
    /// engine-jump (mirrors <c>JumpSystem.BeginJumpAttack</c>'s guard).</summary>
    private static void Register(UnitArrays units, int idx, Vec2 dir, float airDistance,
        float duration, Vec2 slideVel, bool slideThrough, string tag)
    {
        var u = units[idx];
        if (!u.Alive || u.Incap.IsLocked || u.JumpPhase != 0) return;

        float tc = MathF.Max(MinAirDuration, duration);
        _active[u.Id] = new JumpAction
        {
            Dir          = dir,
            AirDistance  = airDistance,
            TimeCost     = tc,
            TcOrig       = tc,
            ArchHeight   = tc * tc * ArchGain,
            SlideVel     = slideThrough ? slideVel : Vec2.Zero,
            SlideThrough = slideThrough,
            State        = 0,
        };
        DebugLog.Log("jump", $"[RogueJump {tag}] unit#{idx} id={u.Id} dir=({dir.X:F2},{dir.Y:F2}) " +
            $"airDist={airDistance:F2} tc={tc:F2}s arch={tc * tc * ArchGain:F2} slide={slideThrough}");
    }

    // --- Per-unit tick (called from Game1's per-unit animation loop) ---

    /// <summary>
    /// Advance the rogue jump for one unit. Returns true while the jump owns the
    /// unit (caller must skip its normal anim/movement and <c>continue</c>, exactly
    /// like <c>JumpSystem.TickUnit</c>); false when there is no jump or it just
    /// ended this frame (normal handling resumes).
    /// </summary>
    public static bool TickUnit(float dt, UnitArrays units, int idx, AnimController ctrl)
    {
        uint id = units[idx].Id;
        if (!_active.TryGetValue(id, out var jump)) return false;

        // Died mid-flight → drop the arc, hand back immediately.
        if (!units[idx].Alive) { EndJump(units, idx, id); return false; }

        // The Nightfall version recurses to advance through instantaneous state
        // transitions in a single frame (setup → wait, launch → first slide). A
        // reprocess loop reproduces that without recursion; anims still tick once.
        bool reprocess = true;
        while (reprocess)
        {
            reprocess = false;
            switch (jump.State)
            {
                // 0 — setup: face the launch direction and start the partial-Standup
                //     spring. Plant only when NOT sliding through (slide mode keeps its
                //     momentum; Simulation zeroes Velocity for Jumping units anyway, so
                //     the slide is applied via Position directly in phases 1/3).
                case 0:
                    FaceToward(units, idx, units[idx].Position + jump.Dir);
                    units[idx].Jumping = true;        // suppress AI intent + ORCA (see class summary)
                    units[idx].PreferredVel = Vec2.Zero;
                    if (!jump.SlideThrough) units[idx].Velocity = Vec2.Zero;
                    ctrl.ForceState(TakeoffAnim);
                    SeekToMidpoint(ctrl);             // <-- the "abuse": start halfway through Standup
                    ctrl.PlaybackSpeed = TakeoffPlayback;
                    jump.State = 1;
                    reprocess = true;                 // fall into the wait this frame
                    break;

                // 1 — hold on the ground until the (partial) spring anim plays out.
                //     In slide mode, glide forward at the entry momentum while it plays.
                case 1:
                    ctrl.PlaybackSpeed = TakeoffPlayback; // SwitchState resets it; keep it applied
                    if (jump.SlideThrough) units[idx].Position += jump.SlideVel * dt;
                    // Standup is PlayOnceTransition: when it completes the controller
                    // leaves the Standup state (→ Idle). That edge = feet leave the ground.
                    if (ctrl.CurrentState == TakeoffAnim)
                        break;                        // still springing → yield the frame
                    // Liftoff: commit the airborne dest from wherever we are now, so any
                    // take-off slide lengthens the leap instead of shortening it.
                    jump.Dest = units[idx].Position + jump.Dir * jump.AirDistance;
                    jump.State = 2;
                    ctrl.ForceState(AirborneAnim);    // held falling pose for the arc
                    reprocess = true;                 // start sliding this same frame
                    break;

                // 2 — airborne: slide toward dest over TimeCost while Z rides the parabola.
                case 2:
                    if (SlideMoveTowards(units, idx, jump.Dest, jump.TimeCost, dt))
                    {
                        units[idx].Z = 0f;
                        jump.State = 3;
                        jump.RecoverTimer = 0f;
                        ctrl.ForceState(LandAnim);    // Standup from frame 0 = land & recover
                        ctrl.PlaybackSpeed = LandPlayback;
                        break;                        // recovery starts next frame
                    }
                    jump.TimeCost -= dt;
                    units[idx].Z = MathF.Max(0f, jump.ArchHeight * ArchProgress(jump.TimeCost, jump.TcOrig));
                    break;

                // 3 — landing recovery: let the land anim finish, then hand control back.
                //     In slide mode, keep gliding out of the landing at the entry
                //     momentum (so you don't stop dead), else plant.
                case 3:
                    units[idx].Z = 0f;
                    units[idx].PreferredVel = Vec2.Zero;
                    if (jump.SlideThrough) units[idx].Position += jump.SlideVel * dt;
                    else units[idx].Velocity = Vec2.Zero;
                    jump.RecoverTimer += dt;
                    if (ctrl.CurrentState != LandAnim || jump.RecoverTimer >= RecoverySafetyTimeout)
                    {
                        EndJump(units, idx, id);
                        return false;                 // done — normal anim/movement resumes this frame
                    }
                    break;
            }
        }

        ctrl.Update(dt);
        return true;
    }

    /// <summary>Abort any in-flight rogue jump on a unit WITHOUT restoring Z — for a
    /// system taking over the unit's motion mid-jump (e.g. a knockback that must win
    /// and will own Z from here). Mirrors <c>JumpSystem.CancelJump</c>.</summary>
    public static void CancelJump(UnitArrays units, int idx)
    {
        if (idx < 0 || idx >= units.Count) return;
        if (_active.Remove(units[idx].Id))
            units[idx].Jumping = false;
    }

    private static void EndJump(UnitArrays units, int idx, uint id)
    {
        _active.TryGetValue(id, out var jump);
        _active.Remove(id);
        if (idx >= 0 && idx < units.Count)
        {
            units[idx].Z = 0f;            // Unit.Z never auto-resets — the owner must zero it
            units[idx].Jumping = false;
            // Slide mode hands the run momentum back so the unit keeps moving on
            // landing instead of stopping dead; plain jumps come to rest.
            units[idx].Velocity = (jump?.SlideThrough ?? false) ? (jump!.SlideVel + (jump!.AirDistance / jump!.TcOrig * 0.2f) * jump!.Dir) : Vec2.Zero;
        }
    }

    // --- Ported helpers ---

    /// <summary>Nightfall <c>UnitHandle.SlideMoveTowards</c>: a time-normalized lerp
    /// that arrives at <paramref name="dest"/> exactly when the remaining time is
    /// consumed. Writes Position directly (the arc bypasses pathfinding/ORCA) and
    /// keeps Velocity zeroed. Returns true on arrival.</summary>
    private static bool SlideMoveTowards(UnitArrays units, int idx, Vec2 dest, float timeLeft, float dt)
    {
        float tc = MathF.Max(timeLeft, 1e-4f);
        float x = dt / tc;
        if (x >= 1f)
        {
            units[idx].Position = dest;
            units[idx].Velocity = Vec2.Zero;
            return true;
        }
        units[idx].Position = Vec2.Lerp(units[idx].Position, dest, x);
        units[idx].Velocity = Vec2.Zero;
        return false;
    }

    /// <summary>Nightfall <c>Asd.ArchProgress</c>: a downward parabola over the jump,
    /// 0 at both ends and 1 at the midpoint. <paramref name="cur"/> counts down from
    /// <paramref name="tot"/>.</summary>
    private static float ArchProgress(float cur, float tot)
    {
        if (tot <= 0f) return 0f;
        float dm = cur / tot - 0.5f;
        return (0.25f - dm * dm) * 4f;
    }

    /// <summary>The partial-state abuse: seek the just-forced one-shot to its middle
    /// frame (Nightfall's <c>cur_clip.i = sprites.Length / 2</c>) so only the back
    /// half of Standup — the crouch-to-stand spring — plays as the launch. AnimTime is
    /// in ms when metadata exists; when it doesn't (tick-based anim) we can't compute
    /// the midpoint, so we leave it at frame 0 (a full stand-up — graceful, not broken).</summary>
    private static void SeekToMidpoint(AnimController ctrl)
    {
        int durMs = ctrl.CurrentAnimDurationMs;
        if (durMs > 0) ctrl.AnimTime = durMs * 0.5f;
    }

    /// <summary>Instant facing snap toward a world point (Nightfall
    /// <c>animation_renderer.Face</c>). FacingAngle is degrees, Y-down.</summary>
    private static void FaceToward(UnitArrays units, int idx, Vec2 target)
    {
        Vec2 d = target - units[idx].Position;
        if (d.LengthSq() > 1e-6f)
            units[idx].FacingAngle = MathF.Atan2(d.Y, d.X) * 180f / MathF.PI;
    }
}
