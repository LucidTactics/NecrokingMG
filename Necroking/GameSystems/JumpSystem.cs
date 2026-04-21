using System;
using Necroking.Core;
using Necroking.Game;
using Necroking.Movement;
using Necroking.Render;

namespace Necroking.GameSystems;

/// <summary>
/// Voluntary scripted jump / pounce system. Shares Z-based height rendering with
/// PhysicsSystem, but uses a scripted parabolic arc instead of gravity/drag — because
/// the motion is intentional (a leap toward a committed target) rather than
/// involuntary (knocked into the air by an explosion).
///
/// Phases (Unit.JumpPhase):
///   0 = None
///   1 = TakeoffApproach — on ground, JumpTakeoff anim playing, AI drives forward motion
///                         (pounce only; attack-jump skips straight to Airborne)
///   2 = Airborne        — in air, scripted lerp + parabolic Z
///   3 = Landing         — still in air, JumpLand anim playing (started land.effect_time
///                         before expected touchdown so anim's effect_time aligns with Z=0)
///   4 = Recovery        — on ground, JumpLand anim finishing
///
/// Effect-time driven timing:
///   - TakeoffApproach → Airborne: triggered by JumpTakeoff's effect_time_ms
///     (the frame where the unit physically leaves the ground).
///   - Airborne → Landing: triggered when remaining-time-to-touchdown equals
///     JumpLand's effect_time_ms, so land anim's touchdown frame aligns with Z=0.
/// </summary>
public static class JumpSystem
{
    public enum Kind : byte { Generic = 0, NecromancerAttack = 1, Pounce = 2 }

    // Tuning
    private const float AirHorizontalSpeed = 6f;      // units/sec through the air
    private const float MinAirDuration = 0.40f;       // seconds, enforced minimum
    private const float DefaultArcPeak = 2.0f;         // parabola peak height
    // Per-phase safety timeouts. Each phase should finish naturally via anim callbacks
    // (ConsumeActionMoment, PlayOnceTransition auto-switch). If anim metadata is broken
    // or the sprite is missing the expected state, the timeouts force progression so
    // the unit doesn't get locked out of AI/combat (every handler has
    // `if (JumpPhase != 0) break` guards).
    private const float TakeoffSafetyTimeout = 1.0f;   // was 3s — shortened; anims shouldn't wind up that long
    private const float LandingSafetyTimeout = 1.5f;   // land phase (airborne→touchdown)
    private const float RecoverySafetyTimeout = 1.5f;  // recovery (post-touchdown anim wind-down)

    // --- Initiation API ---

    /// <summary>
    /// Begin a stationary jump attack (necromancer-style): no ground approach,
    /// unit leaps immediately from current position toward endPos and resolves a
    /// melee attack on landing.
    /// </summary>
    public static void BeginJumpAttack(UnitArrays units, int idx, Vec2 endPos, float arcPeak = DefaultArcPeak)
    {
        if (idx < 0 || idx >= units.Count || !units[idx].Alive) return;
        var u = units[idx];
        u.JumpKind = (byte)Kind.NecromancerAttack;
        u.JumpStartPos = u.Position;
        u.JumpEndPos = endPos;
        u.JumpArcPeak = arcPeak;
        u.JumpTimer = 0f;
        u.JumpDuration = ComputeAirDuration(u.JumpStartPos, u.JumpEndPos);
        u.JumpAttackFired = false;
        // Skip TakeoffApproach — no JumpTakeoff anim on the necromancer; start airborne.
        u.JumpPhase = 2; // Airborne
        u.Jumping = true;
        u.PreferredVel = Vec2.Zero;
        DebugLog.Log("jump", $"[BeginJumpAttack] unit#{idx} dist={(endPos - u.Position).Length():F2} dur={u.JumpDuration:F2}s");
    }

    /// <summary>
    /// Begin a pounce: unit keeps running toward target while JumpTakeoff anim
    /// plays on the ground; physically leaps at the anim's effect_time_ms;
    /// lands at landingPos (locked at pounce-start, not tracking).
    ///
    /// Timing: required total duration (from pounce-start to touchdown) is computed
    /// from dist / MaxSpeed. If this is shorter than the baseline anim duration
    /// (takeoff_total + 1 × jumploop + land_effect), all anims are compressed
    /// (played at higher speed). If longer, extra time is spent looping in JumpLoop.
    /// </summary>
    public static void BeginPounce(UnitArrays units, int idx, Vec2 landingPos, uint targetId,
        System.Collections.Generic.Dictionary<string, AnimationMeta>? animMeta, string spriteName,
        float arcPeak = DefaultArcPeak)
    {
        if (idx < 0 || idx >= units.Count || !units[idx].Alive) return;
        var u = units[idx];

        // Required total duration: dist / MaxSpeed (user's model — unit traverses
        // the full gap at max speed through the takeoff/air/land sequence).
        float dist = (landingPos - u.Position).Length();
        float speed = MathF.Max(1f, u.MaxSpeed);
        float requiredMs = (dist / speed) * 1000f;

        // Baseline anim timings (from pounce-start to physical touchdown, one JumpLoop pass).
        float takeoffTotal = LookupAnimTotalMs(animMeta, spriteName, "JumpTakeoff");
        float takeoffEffect = LookupAnimEffectMs(animMeta, spriteName, "JumpTakeoff");
        float jumploopTotal = LookupAnimTotalMs(animMeta, spriteName, "JumpLoop");
        float landEffect = LookupAnimEffectMs(animMeta, spriteName, "JumpLand");
        float baselineMs = takeoffTotal + jumploopTotal + landEffect;
        if (baselineMs < 100f) baselineMs = 1000f; // safety if meta missing

        float compression;
        if (requiredMs >= baselineMs)
        {
            // Extra time: play at normal speed and spin longer in JumpLoop (one pass already
            // counted in baseline; extra gets added to airborne duration below).
            compression = 1f;
        }
        else
        {
            // Too tight: compress all anims uniformly. Small floor to avoid div-by-zero
            // and absurd playback speeds if baseline >> required (e.g. 1-tile pounce
            // against a 2.6s anim baseline).
            compression = requiredMs / baselineMs;
            if (compression < 0.05f) compression = 0.05f;
        }
        float playbackSpeed = 1f / compression;

        // Airborne real-time duration: everything from liftoff (takeoff.effect_time real) to
        // touchdown (land.effect_time real within JumpLand).
        float airborneMs = MathF.Max(100f, requiredMs - takeoffEffect * compression);

        u.JumpKind = (byte)Kind.Pounce;
        u.JumpStartPos = u.Position;            // placeholder; recaptured at liftoff
        u.JumpEndPos = landingPos;               // committed now
        u.JumpArcPeak = arcPeak;
        u.JumpTimer = 0f;
        u.JumpDuration = airborneMs / 1000f;     // seconds, airborne real time
        u.JumpAttackFired = false;
        u.JumpPounceTargetId = targetId;
        u.JumpPlaybackSpeed = playbackSpeed;
        u.JumpPhase = 1; // TakeoffApproach — AI continues driving ground movement
        u.Jumping = false;                        // only scripted phases set this
        DebugLog.Log("jump", $"[BeginPounce] unit#{idx} dist={dist:F2} speed={speed:F1} " +
            $"required={requiredMs:F0}ms baseline={baselineMs:F0}ms comp={compression:F2} " +
            $"playback={playbackSpeed:F2}x airborne={airborneMs:F0}ms takeoffEff={takeoffEffect:F0}ms landEff={landEffect:F0}ms");
    }

    private static float LookupAnimTotalMs(System.Collections.Generic.Dictionary<string, AnimationMeta>? animMeta,
        string spriteName, string category)
    {
        if (animMeta == null) return 0f;
        string key = AnimMetaLoader.MetaKey(spriteName, category);
        if (animMeta.TryGetValue(key, out var meta)) return meta.TotalDurationMs();
        return 0f;
    }

    private static float LookupAnimEffectMs(System.Collections.Generic.Dictionary<string, AnimationMeta>? animMeta,
        string spriteName, string category)
    {
        if (animMeta == null) return 0f;
        string key = AnimMetaLoader.MetaKey(spriteName, category);
        if (animMeta.TryGetValue(key, out var meta)) return meta.EffectTimeMs;
        return 0f;
    }

    // --- Per-unit tick (called from Game1's per-unit animation loop) ---

    /// <summary>
    /// Advance jump state for one unit. Returns true if the unit is in any jump phase
    /// and the caller should skip its normal anim/movement logic this frame.
    /// </summary>
    public static bool TickUnit(float dt, UnitArrays units, int idx, AnimController ctrl, Simulation sim)
    {
        byte phase = units[idx].JumpPhase;
        if (phase == 0) return false;

        // Apply compression playback speed every tick. SwitchState resets playback to 1,
        // so this keeps the anim running at the compressed rate even across takeoff→loop→land.
        float pb = units[idx].JumpPlaybackSpeed;
        if (pb > 0f) ctrl.PlaybackSpeed = pb;

        switch (phase)
        {
            case 1: TickTakeoffApproach(dt, units, idx, ctrl); break;
            case 2: TickAirborne(dt, units, idx, ctrl); break;
            case 3: TickLanding(dt, units, idx, ctrl, sim); break;
            case 4: TickRecovery(dt, units, idx, ctrl); break;
        }

        ctrl.Update(dt);
        return true;
    }

    // --- Phase handlers ---

    private static void TickTakeoffApproach(float dt, UnitArrays units, int idx, AnimController ctrl)
    {
        // Tick ground-phase timer (used only for safety timeout)
        units[idx].JumpTimer += dt;

        // Force takeoff anim on entry; keep it forced if anything else tried to preempt.
        if (ctrl.CurrentState != AnimState.JumpTakeoff)
            ctrl.ForceState(AnimState.JumpTakeoff);

        // When anim hits effect_time_ms → physically lift off.
        // JustHitEffectFrame is the edge-flag replacement for ConsumeActionMoment.
        if (ctrl.JustHitEffectFrame || units[idx].JumpTimer > TakeoffSafetyTimeout)
        {
            // Capture current position as the real liftoff start. JumpEndPos was locked at
            // BeginPounce; JumpDuration (airborne seconds) was computed there too, so the
            // arc interpolation reaches the landing point in exactly the planned time.
            units[idx].JumpStartPos = units[idx].Position;
            units[idx].JumpTimer = 0f;
            units[idx].JumpPhase = 2; // Airborne
            units[idx].Jumping = true;
            // Queue JumpLoop so anim auto-switches after JumpTakeoff finishes naturally.
            ctrl.RequestState(AnimState.JumpLoop);
            DebugLog.Log("jump", $"[Liftoff] unit#{idx} start=({units[idx].JumpStartPos.X:F1},{units[idx].JumpStartPos.Y:F1}) end=({units[idx].JumpEndPos.X:F1},{units[idx].JumpEndPos.Y:F1}) airborne={units[idx].JumpDuration:F2}s");
        }
    }

    private static void TickAirborne(float dt, UnitArrays units, int idx, AnimController ctrl)
    {
        units[idx].JumpTimer += dt;
        float t = units[idx].JumpDuration > 0f
            ? MathF.Min(units[idx].JumpTimer / units[idx].JumpDuration, 1f)
            : 1f;
        ApplyArc(units, idx, t);

        // For attack jump (skipped TakeoffApproach), force the mid-air anim on first tick.
        var loopAnim = MidAirAnim(units[idx].JumpKind);
        if (units[idx].JumpKind == (byte)Kind.NecromancerAttack && ctrl.CurrentState != loopAnim)
            ctrl.ForceState(loopAnim);

        // Schedule JumpLand: start it land_effect × compression REAL time before touchdown,
        // so the anim's effect_time frame (touchdown in the anim) aligns with Z = 0.
        var landAnim = LandAnim(units[idx].JumpKind);
        float landEffectAnimSec = ctrl.GetEffectTimeSeconds(landAnim);
        float pb = units[idx].JumpPlaybackSpeed > 0f ? units[idx].JumpPlaybackSpeed : 1f;
        float landEffectRealSec = landEffectAnimSec / pb; // anim time → real time via playback
        if (landEffectRealSec <= 0f) landEffectRealSec = 0.25f; // fallback

        float remainingSec = units[idx].JumpDuration - units[idx].JumpTimer;
        if (remainingSec <= landEffectRealSec)
        {
            ctrl.ForceState(landAnim);
            units[idx].JumpPhase = 3; // Landing (still airborne)
        }
    }

    private static void TickLanding(float dt, UnitArrays units, int idx, AnimController ctrl, Simulation sim)
    {
        units[idx].JumpTimer += dt;
        float t = units[idx].JumpDuration > 0f
            ? MathF.Min(units[idx].JumpTimer / units[idx].JumpDuration, 1f)
            : 1f;
        ApplyArc(units, idx, t);

        // Touchdown when land anim's effect_time fires, or (safety) timer overruns duration.
        bool animTouchdown = ctrl.JustHitEffectFrame;
        bool timerTouchdown = t >= 1f;

        if (animTouchdown || timerTouchdown)
        {
            units[idx].Position = units[idx].JumpEndPos;
            units[idx].Z = 0f;
            units[idx].Velocity = Vec2.Zero;
            units[idx].PreferredVel = Vec2.Zero;
            units[idx].JumpPhase = 4; // Recovery
            units[idx].JumpTimer = 0f; // reset for recovery timeout
            FireLandingCallback(units, idx, sim);
            DebugLog.Log("jump", $"[Land] unit#{idx} at=({units[idx].Position.X:F1},{units[idx].Position.Y:F1}) kind={units[idx].JumpKind}");
        }
    }

    private static void TickRecovery(float dt, UnitArrays units, int idx, AnimController ctrl)
    {
        units[idx].Z = 0f;
        units[idx].Velocity = Vec2.Zero;
        units[idx].PreferredVel = Vec2.Zero;
        units[idx].JumpTimer += dt;

        // Landing anim finishes (PlayOnceTransition) → ctrl switches to its pending state
        // (usually Idle). Either way, once CurrentState is no longer the land anim, we're done.
        // Safety timeout: if the anim asset is missing/broken and ctrl never leaves landAnim,
        // force recovery end rather than locking the unit out of AI/combat forever.
        var landAnim = LandAnim(units[idx].JumpKind);
        if (ctrl.CurrentState != landAnim || units[idx].JumpTimer >= RecoverySafetyTimeout)
        {
            if (ctrl.CurrentState == landAnim && units[idx].JumpTimer >= RecoverySafetyTimeout)
            {
                DebugLog.Log("jump",
                    $"[Recovery TIMEOUT] unit#{idx} ctrl stuck in {landAnim} for {units[idx].JumpTimer:F2}s — forcing EndJump");
            }
            EndJump(units, idx);
        }
    }

    private static void EndJump(UnitArrays units, int idx)
    {
        units[idx].JumpPhase = 0;
        units[idx].Jumping = false;
        units[idx].JumpAttackFired = false;
        units[idx].JumpKind = 0;
        units[idx].Z = 0f;
        units[idx].JumpPlaybackSpeed = 1f;
    }

    // --- Helpers ---

    private static float ComputeAirDuration(Vec2 start, Vec2 end)
    {
        float dist = (end - start).Length();
        return MathF.Max(MinAirDuration, dist / AirHorizontalSpeed);
    }

    /// <summary>Parabolic arc: position lerp + Z parabola peaking at JumpArcPeak.</summary>
    private static void ApplyArc(UnitArrays units, int idx, float t)
    {
        units[idx].Position = units[idx].JumpStartPos + (units[idx].JumpEndPos - units[idx].JumpStartPos) * t;
        // 4*t*(1-t) peaks at 1 when t=0.5, so multiplying by ArcPeak gives that peak height.
        units[idx].Z = MathF.Max(0f, units[idx].JumpArcPeak * 4f * t * (1f - t));
        units[idx].Velocity = Vec2.Zero;
    }

    private static AnimState MidAirAnim(byte kind) =>
        kind == (byte)Kind.NecromancerAttack ? AnimState.JumpAttackSetup : AnimState.JumpLoop;

    private static AnimState LandAnim(byte kind) =>
        kind == (byte)Kind.NecromancerAttack ? AnimState.JumpAttackHit : AnimState.JumpLand;

    private static void FireLandingCallback(UnitArrays units, int idx, Simulation sim)
    {
        if (units[idx].JumpAttackFired) return;
        units[idx].JumpAttackFired = true;

        switch ((Kind)units[idx].JumpKind)
        {
            case Kind.NecromancerAttack:
            case Kind.Pounce:
                // Pounce + necromancer jump both resolve their melee attack here at
                // landing. The pounce weapon's damage + bonuses (e.g. Knockdown) apply
                // at this moment. After this, PendingAttack clears — any subsequent
                // attacks (e.g. a wolf's Bite once it's in melee range) fire through
                // the normal combat queue in UpdateCombat with their own anim + effect_time.
                //
                // Guard: PendingAttack is supposed to have been set by InitiatePounce.
                // If it was lost mid-flight (target died, AI reset, etc.), ResolvePendingAttack
                // silently no-ops and the pounce does zero damage — easy to miss.
                // Log a warning so we can spot regressions.
                if (units[idx].PendingAttack.IsNone)
                {
                    DebugLog.Log("jump",
                        $"[LandingCallback] unit#{idx} kind={(Kind)units[idx].JumpKind} — PendingAttack was cleared before landing; " +
                        $"no damage will be resolved. targetId={units[idx].JumpPounceTargetId} target={units[idx].Target}");
                    break;
                }
                sim.ResolvePendingAttack(idx);
                break;
        }
    }
}
