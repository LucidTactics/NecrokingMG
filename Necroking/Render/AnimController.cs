using System;
using System.Collections.Generic;

namespace Necroking.Render;

public enum AnimState : byte
{
    Idle = 0, Walk, Jog, Run,
    Attack1, Attack2, Attack3, Spell1, Special1,
    Block, BlockBreak, BlockReact, Dodge,
    Death, Fall, Knockdown, Standup, Stunned, Panic,
    JumpTakeoff, JumpLoop, JumpLand, JumpAttackSetup, JumpAttackHit,
    Ranged1,
    Feeding,
    Hover,
    Sit,
    Sleep,
    WorkStart,
    WorkLoop,
    WorkEnd,
    Pickup,
    PutDown,
    Carry,
    Count
}

public enum AnimPlayMode : byte { Loop, PlayOnceHold, PlayOnceTransition }

/// <summary>
/// Explicit lifecycle kind for override anims. Replaces the implicit
/// Duration=-1 (permanent) vs Duration=0 (one-shot) encoding, which was easy to
/// misuse — the knockdown-hold bug in e791330 + anim-harness was a case where
/// the -1 hold silently never set OverrideStarted because the expire logic was
/// gated on Duration==0.
///
///   OneShot    — plays once, auto-clears when the controller leaves the state.
///   Hold       — stays active until the caller explicitly replaces it (or a
///                same-channel higher-priority request preempts). No auto-expire.
///   TimedHold  — stays for Duration seconds, then auto-clears.
/// </summary>
public enum OverrideKind : byte
{
    OneShot = 0,
    Hold = 1,
    TimedHold = 2,
}

/// <summary>
/// Animation request from either the Routine channel (AI) or Override channel (combat/physics).
/// Two-channel system: routine is the persistent base, override is a temporary interrupt.
///
/// Priority layering (for override channel):
///   0 = Locomotion / Idle        (routine only)
///   1 = Action (routine: Sit, Sleep, Feed)
///   2 = Combat (attacks, hit-reacts, dodges)
///   3 = Forced / HardState (incap holds, death, recovery)
///
/// Lifecycle is carried by Kind (OneShot/Hold/TimedHold) rather than by Duration
/// alone. Duration is still used as the timer for TimedHold.
/// </summary>
public struct AnimRequest
{
    public AnimState State;
    public byte Priority;        // 0=locomotion, 1=action, 2=combat, 3=forced
    public bool Interrupt;       // If winning priority, cut current anim mid-loop?
    public OverrideKind Kind;    // OneShot / Hold / TimedHold — see enum docs
    public float Duration;       // TimedHold: seconds. OneShot: 0. Hold: unused (kept for compat).
    public float PlaybackSpeed;  // 1.0=normal

    public bool IsActive => State != AnimState.Idle || Priority > 0;

    public static AnimRequest Locomotion(AnimState state) => new()
        { State = state, Priority = 0, Interrupt = false, Kind = OverrideKind.Hold, Duration = -1, PlaybackSpeed = 1f };

    public static AnimRequest Action(AnimState state, float duration = -1f) => new()
        { State = state, Priority = 1, Interrupt = false,
          Kind = duration > 0 ? OverrideKind.TimedHold : OverrideKind.Hold,
          Duration = duration, PlaybackSpeed = 1f };

    public static AnimRequest Combat(AnimState state, float playbackSpeed = 1f) => new()
        { State = state, Priority = 2, Interrupt = true, Kind = OverrideKind.OneShot,
          Duration = 0, PlaybackSpeed = playbackSpeed };

    /// <summary>Priority-3 one-shot override. Plays once and auto-clears. Use for
    /// state exits like Standup, Death (where the anim has PlayOnceHold mode the
    /// controller will pin the final frame anyway).</summary>
    public static AnimRequest Forced(AnimState state) => new()
        { State = state, Priority = 3, Interrupt = true, Kind = OverrideKind.OneShot,
          Duration = 0, PlaybackSpeed = 1f };

    /// <summary>Priority-3 permanent hold. Stays until a caller explicitly
    /// replaces it. Use for incap holds (Knockdown, Stunned, Paralyzed, Sleep)
    /// where a buff owns the lifetime.</summary>
    public static AnimRequest Hold(AnimState state, byte priority = 3) => new()
        { State = state, Priority = priority, Interrupt = true, Kind = OverrideKind.Hold,
          Duration = -1, PlaybackSpeed = 1f };

    public static readonly AnimRequest None = new()
        { State = AnimState.Idle, Priority = 0, Interrupt = false, Kind = OverrideKind.Hold,
          Duration = -1, PlaybackSpeed = 1f };
}

public struct FrameResult
{
    public SpriteFrame? Frame;
    public bool FlipX;
}

public class AnimController
{
    private UnitSpriteData? _spriteData;
    private Dictionary<string, AnimationMeta>? _animMeta;
    private string _unitName = "";

    private AnimState _currentState = AnimState.Idle;
    private AnimState _pendingState = AnimState.Idle;
    private float _animTime;           // ms when metadata exists, ticks for fallback
    private float[] _stateTickRate = new float[(int)AnimState.Count];
    private bool _finished;
    private bool _reversePlayback;
    private Dictionary<string, AnimTimingOverride>? _timingOverrides;
    private float _playbackSpeed = 1f;

    // Per-unit attack animation override (e.g. "AttackBite" for wolves)
    private string? _attackAnimOverride;

    // Cached resolved data for _currentState (set once per state transition)
    private AnimationData? _resolvedAnim;
    private AnimationMeta? _resolvedMeta;
    private AngleScheme _resolvedScheme = AngleScheme.Old;
    private int _resolvedFallbackAngle = 30;

    private enum AngleScheme { Old, New }

    // Angle sectors: map world angle (Y-down: 0=right, 90=down, 180=left, 270=up) → stored sprite angle + flip.
    //
    // OldSectors: legacy 3-angle scheme (30 right-ish, 60 down-ish, 300 up-ish). Down and Up are split at the
    // cardinal so their left half flips. Units authored before the angle refactor use this.
    //
    // NewSectors: compass scheme matching the Unity authoring convention —
    //   yaw 0=right (E), yaw 45=down-right (SE), yaw 90=down (S, toward camera),
    //   yaw 270=up (N, away from camera), yaw 315=up-right (NE).
    // N and S are distinct sprites (face vs back) — they never flip. W / NW / SW
    // come from horizontal-flipping E / NE / SE.
    private static readonly (float min, float max, int angle, bool flip)[] OldSectors =
    {
        (-22.5f,  22.5f, 30,  false),  // Right
        ( 22.5f,  67.5f, 60,  false),  // Down-Right
        ( 67.5f,  90.0f, 60,  false),  // Down (right half)
        ( 90.0f, 112.5f, 60,  true),   // Down (left half)
        (112.5f, 157.5f, 60,  true),   // Down-Left
        (157.5f, 202.5f, 30,  true),   // Left
        (202.5f, 247.5f, 300, true),   // Up-Left
        (247.5f, 270.0f, 300, true),   // Up (left half)
        (270.0f, 292.5f, 300, false),  // Up (right half)
        (292.5f, 337.5f, 300, false),  // Up-Right
    };

    private static readonly (float min, float max, int angle, bool flip)[] NewSectors =
    {
        (-22.5f,  22.5f,   0, false),  // E
        ( 22.5f,  67.5f,  45, false),  // SE (down-right = Unity yaw 45)
        ( 67.5f, 112.5f,  90, false),  // S (down = Unity yaw 90, distinct sprite, never flipped)
        (112.5f, 157.5f,  45, true),   // SW (flip of SE)
        (157.5f, 202.5f,   0, true),   // W (flip of E)
        (202.5f, 247.5f, 315, true),   // NW (flip of NE)
        (247.5f, 292.5f, 270, false),  // N (up = Unity yaw 270, distinct sprite, never flipped)
        (292.5f, 337.5f, 315, false),  // NE (up-right = Unity yaw 315)
    };

    public AnimState CurrentState => _currentState;
    public bool IsAnimFinished => _finished;
    public float AnimTime { get => _animTime; set => _animTime = value; }
    public float PlaybackSpeed { get => _playbackSpeed; set => _playbackSpeed = MathF.Max(0.1f, value); }
    public void SetReversePlayback(bool reverse) => _reversePlayback = reverse;

    // --- Edge flags ---
    // Single-frame "an event happened this tick" flags. Set during Update(),
    // cleared at the START of the next Update(). Read by consumers in the same
    // frame between Update calls. Semantics:
    //
    //   JustEnteredState    — SwitchState ran this frame; the new state is
    //                         CurrentState and _animTime == 0.
    //   JustExitedState     — SwitchState ran this frame; the outgoing state
    //                         is in ExitedState (for the one-frame window only).
    //   JustHitEffectFrame  — _animTime just crossed the state's effect_time_ms
    //                         this frame. Replaces the polled ConsumeActionMoment
    //                         pattern for consumers that need a reliable edge
    //                         (subscribers check the flag AND whether they're
    //                         the intended consumer; no exclusive consumption).
    //   JustFinished        — a play-once anim finished this frame. For
    //                         PlayOnceTransition this also implies JustEnteredState
    //                         will be set (for the target state).
    //
    // Why edge flags instead of events: zero allocation, same-frame determinism,
    // no subscribe/unsubscribe bookkeeping, and the old ConsumeActionMoment
    // exclusive-consumption model is the bug we're replacing.
    public bool JustEnteredState { get; private set; }
    public bool JustExitedState { get; private set; }
    public bool JustHitEffectFrame { get; private set; }
    public bool JustFinished { get; private set; }
    public AnimState ExitedState { get; private set; }

    public void Init(UnitSpriteData? spriteData, float tickRate = 30f)
    {
        _spriteData = spriteData;
        _currentState = AnimState.Idle;
        _pendingState = AnimState.Idle;
        _animTime = 0f;
        _finished = false;
        _timingOverrides = null;
        for (int i = 0; i < _stateTickRate.Length; i++)
            _stateTickRate[i] = 30f; // flat fallback
        ResolveForState();
    }

    public void SetAnimMeta(Dictionary<string, AnimationMeta>? metaMap, string unitName)
    {
        _animMeta = metaMap;
        _unitName = unitName;
        ResolveForState();
    }

    public void SetAnimTimings(Dictionary<string, AnimTimingOverride>? timings)
    {
        _timingOverrides = timings;
    }

    public void SetAttackAnimOverride(string? animName)
    {
        _attackAnimOverride = animName;
        ResolveForState();
    }

    // --- Resolved data caching ---

    /// <summary>
    /// Resolves and caches AnimationData + AnimationMeta for _currentState.
    /// Called once per state transition (from SwitchState) and when underlying data changes.
    /// </summary>
    private void ResolveForState()
    {
        _resolvedAnim = ResolveAnimForState(_currentState);
        _resolvedMeta = ResolveMetaForState(_currentState);
        DetectAngleScheme();
    }

    private void DetectAngleScheme()
    {
        _resolvedScheme = AngleScheme.Old;
        _resolvedFallbackAngle = 30;
        if (_resolvedAnim == null) return;

        // New scheme keys: 0, 45, 90, 270, 315. Old scheme keys: 30, 60, 300.
        // Presence of any new-scheme key switches the whole animation to the new table.
        foreach (var (a, _) in _resolvedAnim.AngleFrames)
        {
            if (a == 0 || a == 45 || a == 90 || a == 270 || a == 315)
            {
                _resolvedScheme = AngleScheme.New;
                break;
            }
        }

        // Pick a stable fallback from whatever is actually authored.
        int[] prefs = _resolvedScheme == AngleScheme.New
            ? new[] { 0, 45, 315, 90, 270 }
            : new[] { 30, 60, 300 };
        foreach (var p in prefs)
        {
            if (_resolvedAnim.AngleFrames.ContainsKey(p)) { _resolvedFallbackAngle = p; return; }
        }
        // Nothing matched the expected scheme — use any authored angle.
        foreach (var (a, _) in _resolvedAnim.AngleFrames) { _resolvedFallbackAngle = a; return; }
    }

    private AnimationData? ResolveAnimForState(AnimState state)
    {
        if (_spriteData == null) return null;

        // Try attack anim override first (e.g. "AttackBite" for wolves)
        if (_attackAnimOverride != null && IsAttackState(state))
        {
            var ov = _spriteData.GetAnim(_attackAnimOverride);
            if (ov != null) return ov;
        }

        var anim = _spriteData.GetAnim(StateToAnimName(state));
        if (anim != null) return anim;

        string? fallback = GetFallbackAnimName(state);
        if (fallback != null)
            anim = _spriteData.GetAnim(fallback);

        return anim ?? _spriteData.GetAnim("Idle");
    }

    private AnimationMeta? ResolveMetaForState(AnimState state)
    {
        if (_animMeta == null || string.IsNullOrEmpty(_unitName)) return null;

        // Try attack anim override first
        if (_attackAnimOverride != null && IsAttackState(state))
        {
            string ovKey = AnimMetaLoader.MetaKey(_unitName, _attackAnimOverride);
            if (_animMeta.TryGetValue(ovKey, out var ovMeta)) return ovMeta;
        }

        string key = AnimMetaLoader.MetaKey(_unitName, StateToAnimName(state));
        if (_animMeta.TryGetValue(key, out var meta)) return meta;

        string? fallback = GetFallbackAnimName(state);
        if (fallback != null)
        {
            key = AnimMetaLoader.MetaKey(_unitName, fallback);
            if (_animMeta.TryGetValue(key, out meta)) return meta;
        }
        return null;
    }

    private static bool IsAttackState(AnimState state) =>
        state == AnimState.Attack1 || state == AnimState.Attack2 || state == AnimState.Attack3;

    /// <summary>
    /// Single source of truth for animation fallback chains.
    /// </summary>
    private static string? GetFallbackAnimName(AnimState state) => state switch
    {
        AnimState.Run or AnimState.Jog => "Walk",
        AnimState.Fall => "Knockdown",       // Fall → Knockdown if no Fall anim
        AnimState.Knockdown => "Death",      // Knockdown → Death if no Knockdown anim
        AnimState.Standup => "Idle",         // Standup → Idle if no Standup anim
        AnimState.Hover => "Fall",
        AnimState.Carry => "Walk",
        _ => null
    };

    // --- State transitions ---

    public void RequestState(AnimState newState)
    {
        if (newState == _currentState) return;
        if (_currentState == AnimState.Death) return;

        if (GetPlayMode(_currentState) == AnimPlayMode.PlayOnceHold && !_finished)
        {
            if (GetStatePriority(newState) > GetStatePriority(_currentState))
                SwitchState(newState);
            else
                _pendingState = newState;
            return;
        }

        if (IsInterruptible(_currentState))
            SwitchState(newState);
        else if (GetStatePriority(newState) >= GetStatePriority(_pendingState))
            _pendingState = newState;
    }

    public void ForceState(AnimState newState)
    {
        if (newState == _currentState) return;
        if (_currentState == AnimState.Death) return;
        SwitchState(newState);
    }

    /// <summary>Force a state and skip to its last frame (for PlayOnceHold animations).</summary>
    public void ForceStateAtEnd(AnimState newState)
    {
        if (_currentState == AnimState.Death) return;
        SwitchState(newState);
        _animTime = 999999f; // will be clamped to end on next Update
        _finished = true;
    }

    private void SwitchState(AnimState newState)
    {
        // Edge flags: record the transition so same-frame consumers can detect it.
        ExitedState = _currentState;
        JustExitedState = true;
        JustEnteredState = true;
        _currentState = newState;
        _pendingState = newState;
        _animTime = 0f;
        _finished = false;
        _playbackSpeed = 1f;
        ResolveForState();
    }

    // --- Update ---

    public void Update(float dt)
    {
        // Clear single-frame edge flags at the top of each tick; anything set below
        // is the current frame's events, which consumers read after Update returns.
        JustEnteredState = false;
        JustExitedState = false;
        JustHitEffectFrame = false;
        JustFinished = false;

        if (_spriteData == null) return;

        float animTimeBefore = _animTime;
        int totalMs = GetEffectiveTotalDurationMs();

        if (totalMs > 0)
        {
            // MS-BASED PLAYBACK (scaled by playback speed)
            _animTime += dt * 1000f * _playbackSpeed;

            // Effect-time edge: fires on the single tick where _animTime crosses
            // the state's effect_time_ms threshold. Replaces the polled
            // ConsumeActionMoment model — callers read JustHitEffectFrame and
            // decide on their own whether they're the intended consumer, without
            // the "someone consumed the moment before my handler ran" race.
            int effectMs = GetEffectiveEffectTimeMs();
            if (effectMs > 0 && animTimeBefore < effectMs && _animTime >= effectMs)
                JustHitEffectFrame = true;

            var mode = GetPlayMode(_currentState);

            if (mode == AnimPlayMode.Loop)
            {
                if (_animTime >= totalMs && _pendingState != _currentState)
                {
                    SwitchState(_pendingState);
                    return;
                }
                while (_animTime >= totalMs) _animTime -= totalMs;
            }
            else if (mode == AnimPlayMode.PlayOnceHold)
            {
                if (_animTime >= totalMs)
                {
                    if (!_finished) JustFinished = true;
                    _animTime = totalMs;
                    _finished = true;
                }
            }
            else // PlayOnceTransition
            {
                if (_animTime >= totalMs)
                {
                    _finished = true;
                    JustFinished = true;
                    SwitchState(_pendingState != _currentState ? _pendingState : AnimState.Idle);
                }
            }
        }
        else if (_resolvedAnim != null)
        {
            // TICK-BASED FALLBACK (scaled by playback speed)
            _animTime += _stateTickRate[(int)_currentState] * dt * _playbackSpeed;
            int totalT = _resolvedAnim.TotalTicks();
            if (totalT <= 0) return;

            // No effect_time metadata in tick-based → use 50% of total as the
            // fallback "hit frame" edge, matching HasReachedActionMoment.
            float effectT = totalT * 0.5f;
            if (animTimeBefore < effectT && _animTime >= effectT)
                JustHitEffectFrame = true;

            var mode = GetPlayMode(_currentState);
            if (mode == AnimPlayMode.Loop)
            {
                if (_animTime >= totalT && _pendingState != _currentState)
                {
                    SwitchState(_pendingState);
                    return;
                }
                while (_animTime >= totalT) _animTime -= totalT;
            }
            else if (mode == AnimPlayMode.PlayOnceHold)
            {
                if (_animTime >= totalT)
                {
                    if (!_finished) JustFinished = true;
                    _animTime = totalT;
                    _finished = true;
                }
            }
            else
            {
                if (_animTime >= totalT)
                {
                    _finished = true;
                    JustFinished = true;
                    SwitchState(_pendingState != _currentState ? _pendingState : AnimState.Idle);
                }
            }
        }
        else
        {
            // No data — immediately transition out
            _finished = true;
            SwitchState(_pendingState != _currentState ? _pendingState : AnimState.Idle);
        }
    }

    // --- Frame lookup ---

    public FrameResult GetCurrentFrame(float facingAngleDeg)
    {
        var result = new FrameResult();
        if (_resolvedAnim == null) return result;

        int spriteAngle = ResolveAngle(facingAngleDeg, out bool flipX);
        result.FlipX = flipX;

        float effectiveTime = _animTime;

        // Reverse playback for walking backward
        if (_reversePlayback && (_currentState == AnimState.Walk || _currentState == AnimState.Jog || _currentState == AnimState.Run))
        {
            int totalMs = GetEffectiveTotalDurationMs();
            if (totalMs > 0)
                effectiveTime = MathF.Max(0f, totalMs - _animTime);
            else
            {
                int totalT = _resolvedAnim.TotalTicks();
                if (totalT > 0) effectiveTime = MathF.Max(0f, totalT - _animTime);
            }
        }

        // Try ms-based frame lookup
        var durations = GetEffectiveFrameDurations(spriteAngle);
        if (durations != null && durations.Count > 0)
        {
            var kfs = _resolvedAnim.GetAngle(spriteAngle);
            if ((kfs == null || kfs.Count == 0) && spriteAngle != _resolvedFallbackAngle)
                kfs = _resolvedAnim.GetAngle(_resolvedFallbackAngle);

            // Find frame index from cumulative ms
            int numDurFrames = durations.Count;
            float cumMs = 0;
            int frameIdx = numDurFrames - 1;
            for (int f = 0; f < numDurFrames; f++)
            {
                cumMs += durations[f];
                if (effectiveTime < cumMs) { frameIdx = f; break; }
            }

            if (kfs != null && kfs.Count > 0)
            {
                if (kfs.Count == numDurFrames)
                {
                    result.Frame = kfs[frameIdx].Frame;
                    return result;
                }
                // Clamp to available
                int clampedIdx = Math.Min(frameIdx, kfs.Count - 1);
                result.Frame = kfs[clampedIdx].Frame;
                return result;
            }
        }

        // Tick-based fallback
        var tickKfs = _resolvedAnim.GetAngle(spriteAngle);
        if (tickKfs == null || tickKfs.Count == 0)
            tickKfs = _resolvedAnim.GetAngle(_resolvedFallbackAngle);

        if (tickKfs != null && tickKfs.Count > 0)
        {
            // Floor lookup
            SpriteFrame frame = tickKfs[0].Frame;
            for (int i = tickKfs.Count - 1; i >= 0; i--)
            {
                if (effectiveTime >= tickKfs[i].Time) { frame = tickKfs[i].Frame; break; }
            }
            result.Frame = frame;
        }

        return result;
    }

    /// <summary>
    /// Returns the current frame index (0-based) for the given facing angle. Mirrors
    /// GetCurrentFrame but returns the index instead of the frame data.
    ///
    /// When the reverse-playback flag is set and the state is Walk/Jog/Run, time is
    /// mirrored (totalMs - _animTime) before frame lookup so the locomotion plays
    /// backward — this is how backpedaling reads from the same asset as forward walk.
    /// Callers should be aware that the returned index is not a monotonic function of
    /// elapsed time in that case.
    /// </summary>
    public int GetCurrentFrameIndex(float facingAngleDeg)
    {
        if (_resolvedAnim == null) return 0;

        int spriteAngle = ResolveAngle(facingAngleDeg, out _);

        float effectiveTime = _animTime;
        if (_reversePlayback && (_currentState == AnimState.Walk || _currentState == AnimState.Jog || _currentState == AnimState.Run))
        {
            int totalMs = GetEffectiveTotalDurationMs();
            if (totalMs > 0)
                effectiveTime = MathF.Max(0f, totalMs - _animTime);
            else
            {
                int totalT = _resolvedAnim.TotalTicks();
                if (totalT > 0) effectiveTime = MathF.Max(0f, totalT - _animTime);
            }
        }

        // ms-based
        var durations = GetEffectiveFrameDurations(spriteAngle);
        if (durations != null && durations.Count > 0)
        {
            float cumMs = 0;
            for (int f = 0; f < durations.Count; f++)
            {
                cumMs += durations[f];
                if (effectiveTime < cumMs) return f;
            }
            return durations.Count - 1;
        }

        // tick-based fallback
        var tickKfs = _resolvedAnim.GetAngle(spriteAngle);
        if (tickKfs == null || tickKfs.Count == 0)
            tickKfs = _resolvedAnim.GetAngle(_resolvedFallbackAngle);
        if (tickKfs != null && tickKfs.Count > 0)
        {
            for (int i = tickKfs.Count - 1; i >= 0; i--)
            {
                if (effectiveTime >= tickKfs[i].Time) return i;
            }
        }
        return 0;
    }

    // --- Angle resolution ---

    public int ResolveAngle(float angleDeg, out bool flipX)
    {
        angleDeg = ((angleDeg % 360f) + 360f) % 360f;
        if (angleDeg >= 337.5f) angleDeg -= 360f;

        var sectors = _resolvedScheme == AngleScheme.New ? NewSectors : OldSectors;
        foreach (var (min, max, angle, flip) in sectors)
        {
            if (angleDeg >= min && angleDeg < max)
            {
                flipX = flip;
                return angle;
            }
        }
        flipX = false;
        return _resolvedFallbackAngle;
    }

    // --- Action moment ---
    // Consumers use the single-frame edge flag JustHitEffectFrame (set in Update,
    // cleared at the top of the next Update). The old ConsumeActionMoment method
    // was removed — non-destructive edge reads are strictly better (no "who
    // consumed it first" races).

    public bool HasReachedActionMoment()
    {
        int effectMs = GetEffectiveEffectTimeMs();
        if (effectMs > 0) return _animTime >= effectMs;

        // Fallback: 50% of total ticks
        if (_resolvedAnim != null)
        {
            int total = _resolvedAnim.TotalTicks();
            return total > 0 && _animTime >= total * 0.5f;
        }
        return false;
    }

    public float GetEffectTimeSeconds(AnimState state)
    {
        string animName = StateToAnimName(state);
        if (_timingOverrides != null && _timingOverrides.TryGetValue(animName, out var ov) && ov.EffectTimeMs >= 0)
            return ov.EffectTimeMs / 1000f;
        var meta = ResolveMetaForState(state);
        if (meta != null && meta.EffectTimeMs > 0) return meta.EffectTimeMs / 1000f;
        return 0f;
    }

    public float GetTotalDurationSeconds(AnimState state)
    {
        string animName = StateToAnimName(state);
        if (_timingOverrides != null && _timingOverrides.TryGetValue(animName, out var ov) && ov.FrameDurationsMs.Count > 0)
        {
            int total = 0;
            foreach (int d in ov.FrameDurationsMs) total += d;
            return total / 1000f;
        }
        var meta = ResolveMetaForState(state);
        if (meta != null)
        {
            int ms = meta.TotalDurationMs();
            if (ms > 0) return ms / 1000f;
        }
        return 0f;
    }

    // --- Effective timing helpers (use cached _resolvedMeta) ---

    private int GetEffectiveTotalDurationMs()
    {
        string animName = StateToAnimName(_currentState);
        if (_timingOverrides != null && _timingOverrides.TryGetValue(animName, out var ov) && ov.FrameDurationsMs.Count > 0)
        {
            int total = 0;
            foreach (int d in ov.FrameDurationsMs) total += d;
            return total;
        }
        return _resolvedMeta?.TotalDurationMs() ?? 0;
    }

    private int GetEffectiveEffectTimeMs()
    {
        string animName = StateToAnimName(_currentState);
        if (_timingOverrides != null && _timingOverrides.TryGetValue(animName, out var ov) && ov.EffectTimeMs >= 0)
            return ov.EffectTimeMs;
        return _resolvedMeta?.EffectTimeMs ?? 0;
    }

    private List<int>? GetEffectiveFrameDurations(int spriteAngle)
    {
        string animName = StateToAnimName(_currentState);
        if (_timingOverrides != null && _timingOverrides.TryGetValue(animName, out var ov) && ov.FrameDurationsMs.Count > 0)
            return ov.FrameDurationsMs;

        if (_resolvedMeta != null)
        {
            if (_resolvedMeta.YawData.TryGetValue(spriteAngle, out var ym) && ym.FrameDurationsMs.Count > 0)
                return ym.FrameDurationsMs;
            foreach (var (_, y) in _resolvedMeta.YawData)
                if (y.FrameDurationsMs.Count > 0) return y.FrameDurationsMs;
        }
        return null;
    }

    // --- Static helpers ---

    public static string StateToAnimName(AnimState state) => state switch
    {
        AnimState.Idle => "Idle", AnimState.Walk => "Walk", AnimState.Jog => "Jog", AnimState.Run => "Run",
        AnimState.Attack1 => "Attack1", AnimState.Attack2 => "Attack2", AnimState.Attack3 => "Attack3",
        AnimState.Spell1 => "Spell1", AnimState.Special1 => "Special1",
        AnimState.Block => "Block", AnimState.BlockBreak => "BlockBreak", AnimState.BlockReact => "BlockReact",
        AnimState.Dodge => "Dodge", AnimState.Death => "Death", AnimState.Fall => "Fall",
        AnimState.Knockdown => "Knockdown", AnimState.Standup => "Standup", AnimState.Stunned => "Stunned",
        AnimState.Panic => "Panic", AnimState.Ranged1 => "Ranged1",
        AnimState.JumpTakeoff => "JumpTakeoff", AnimState.JumpLoop => "JumpLoop",
        AnimState.JumpLand => "JumpLand", AnimState.JumpAttackSetup => "JumpAttackSetup",
        AnimState.JumpAttackHit => "JumpAttackHit",
        AnimState.Feeding => "Feed",
        AnimState.Hover => "Hover",
        AnimState.Sit => "Sit",
        AnimState.Sleep => "Sleep",
        AnimState.WorkStart => "WorkStart",
        AnimState.WorkLoop => "WorkLoop",
        AnimState.WorkEnd => "WorkEnd",
        AnimState.Pickup => "Pickup",
        AnimState.PutDown => "PutDown",
        AnimState.Carry => "Carry",
        _ => "Idle"
    };

    public static AnimPlayMode GetPlayMode(AnimState state) => state switch
    {
        AnimState.Idle or AnimState.Walk or AnimState.Jog or AnimState.Run
            or AnimState.Block or AnimState.Stunned or AnimState.JumpLoop
            or AnimState.Panic or AnimState.Feeding or AnimState.Hover
            or AnimState.WorkLoop or AnimState.Carry => AnimPlayMode.Loop,
        AnimState.Death or AnimState.Knockdown or AnimState.Fall
            or AnimState.Sit or AnimState.Sleep
            or AnimState.WorkStart or AnimState.WorkEnd
            or AnimState.Pickup or AnimState.PutDown => AnimPlayMode.PlayOnceHold,
        _ => AnimPlayMode.PlayOnceTransition
    };

    public static bool IsMovementLocked(AnimState state) => state switch
    {
        AnimState.Attack1 or AnimState.Attack2 or AnimState.Attack3
            or AnimState.Spell1 or AnimState.Special1 or AnimState.Ranged1
            or AnimState.BlockBreak or AnimState.Knockdown or AnimState.Standup
            or AnimState.JumpTakeoff or AnimState.JumpLoop or AnimState.JumpLand
            or AnimState.JumpAttackSetup or AnimState.JumpAttackHit
            or AnimState.Death
            or AnimState.WorkStart or AnimState.WorkLoop or AnimState.WorkEnd
            or AnimState.Pickup or AnimState.PutDown => true,
        _ => false
    };

    private static bool IsInterruptible(AnimState state) => state switch
    {
        AnimState.Idle or AnimState.Walk or AnimState.Jog or AnimState.Run
            or AnimState.Block => true,
        _ => false
    };

    private static int GetStatePriority(AnimState state) => state switch
    {
        AnimState.Idle => 0,
        AnimState.Walk or AnimState.Jog or AnimState.Run => 1,
        AnimState.Block => 2,
        AnimState.Attack1 or AnimState.Attack2 or AnimState.Attack3
            or AnimState.Spell1 or AnimState.Special1 or AnimState.Standup
            or AnimState.Ranged1
            or AnimState.WorkStart or AnimState.WorkLoop or AnimState.WorkEnd
            or AnimState.Pickup or AnimState.PutDown => 3,
        AnimState.Dodge or AnimState.BlockReact => 4,
        AnimState.JumpTakeoff or AnimState.JumpLoop or AnimState.JumpLand
            or AnimState.JumpAttackSetup or AnimState.JumpAttackHit => 5,
        AnimState.Knockdown => 6,
        AnimState.Death or AnimState.Fall => 100,
        _ => 0
    };
}
