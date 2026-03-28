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
    Count
}

public enum AnimPlayMode : byte { Loop, PlayOnceHold, PlayOnceTransition }

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
    private bool _actionMomentFired;
    private Dictionary<string, AnimTimingOverride>? _timingOverrides;
    private float _playbackSpeed = 1f;

    // Angle sectors: maps world angle to sprite angle + flip
    // World: 0=right, 90=down(toward camera), 180=left, 270=up(away)
    // Sprite angles: 30 (right-ish), 60 (down), 300 (up/away)
    private static readonly (float min, float max, int angle, bool flip)[] AngleSectors =
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

    public AnimState CurrentState => _currentState;
    public bool IsAnimFinished => _finished;
    public float AnimTime { get => _animTime; set => _animTime = value; }
    public float PlaybackSpeed { get => _playbackSpeed; set => _playbackSpeed = MathF.Max(0.1f, value); }
    public void SetReversePlayback(bool reverse) => _reversePlayback = reverse;

    public void Init(UnitSpriteData? spriteData, float tickRate = 30f)
    {
        _spriteData = spriteData;
        _currentState = AnimState.Idle;
        _pendingState = AnimState.Idle;
        _animTime = 0f;
        _finished = false;
        _actionMomentFired = false;
        _timingOverrides = null;
        for (int i = 0; i < _stateTickRate.Length; i++)
            _stateTickRate[i] = 30f; // flat fallback
    }

    public void SetAnimMeta(Dictionary<string, AnimationMeta>? metaMap, string unitName)
    {
        _animMeta = metaMap;
        _unitName = unitName;
    }

    public void SetAnimTimings(Dictionary<string, AnimTimingOverride>? timings)
    {
        _timingOverrides = timings;
    }

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

    private void SwitchState(AnimState newState)
    {
        _currentState = newState;
        _pendingState = newState;
        _animTime = 0f;
        _finished = false;
        _actionMomentFired = false;
        _playbackSpeed = 1f;
    }

    // --- Update ---

    public void Update(float dt)
    {
        if (_spriteData == null) return;

        string animName = StateToAnimName(_currentState);
        var anim = _spriteData.GetAnim(animName);
        int totalMs = GetEffectiveTotalDurationMs();

        if (totalMs > 0)
        {
            // MS-BASED PLAYBACK (scaled by playback speed)
            _animTime += dt * 1000f * _playbackSpeed;
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
                if (_animTime >= totalMs) { _animTime = totalMs; _finished = true; }
            }
            else // PlayOnceTransition
            {
                if (_animTime >= totalMs)
                {
                    _finished = true;
                    SwitchState(_pendingState != _currentState ? _pendingState : AnimState.Idle);
                }
            }
        }
        else if (anim != null)
        {
            // TICK-BASED FALLBACK (scaled by playback speed)
            _animTime += _stateTickRate[(int)_currentState] * dt * _playbackSpeed;
            int totalT = anim.TotalTicks();
            if (totalT <= 0) return;

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
                if (_animTime >= totalT) { _animTime = totalT; _finished = true; }
            }
            else
            {
                if (_animTime >= totalT)
                {
                    _finished = true;
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
        if (_spriteData == null) return result;

        string animName = StateToAnimName(_currentState);
        var anim = _spriteData.GetAnim(animName);

        // Fallback chain
        if (anim == null)
        {
            if (_currentState == AnimState.Run || _currentState == AnimState.Jog)
                anim = _spriteData.GetAnim("Walk");
            anim ??= _spriteData.GetAnim("Idle");
            if (anim == null) return result;
        }

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
                int totalT = anim.TotalTicks();
                if (totalT > 0) effectiveTime = MathF.Max(0f, totalT - _animTime);
            }
        }

        // Try ms-based frame lookup
        var durations = GetEffectiveFrameDurations(spriteAngle);
        if (durations != null && durations.Count > 0)
        {
            var kfs = anim.GetAngle(spriteAngle);
            if ((kfs == null || kfs.Count == 0) && spriteAngle != 30)
                kfs = anim.GetAngle(30);

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
        var tickKfs = anim.GetAngle(spriteAngle);
        if (tickKfs == null || tickKfs.Count == 0)
            tickKfs = anim.GetAngle(30); // fallback angle

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
    /// Returns the current frame index (0-based) for the given facing angle.
    /// This mirrors the logic in GetCurrentFrame but returns the index instead of the frame data.
    /// </summary>
    public int GetCurrentFrameIndex(float facingAngleDeg)
    {
        if (_spriteData == null) return 0;

        string animName = StateToAnimName(_currentState);
        var anim = _spriteData.GetAnim(animName);
        if (anim == null)
        {
            if (_currentState == AnimState.Run || _currentState == AnimState.Jog)
                anim = _spriteData.GetAnim("Walk");
            anim ??= _spriteData.GetAnim("Idle");
            if (anim == null) return 0;
        }

        int spriteAngle = ResolveAngle(facingAngleDeg, out _);

        float effectiveTime = _animTime;
        if (_reversePlayback && (_currentState == AnimState.Walk || _currentState == AnimState.Jog || _currentState == AnimState.Run))
        {
            int totalMs = GetEffectiveTotalDurationMs();
            if (totalMs > 0)
                effectiveTime = MathF.Max(0f, totalMs - _animTime);
            else
            {
                int totalT = anim.TotalTicks();
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
        var tickKfs = anim.GetAngle(spriteAngle);
        if (tickKfs == null || tickKfs.Count == 0)
            tickKfs = anim.GetAngle(30);
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

        foreach (var (min, max, angle, flip) in AngleSectors)
        {
            if (angleDeg >= min && angleDeg < max)
            {
                flipX = flip;
                return angle;
            }
        }
        flipX = false;
        return 30;
    }

    // --- Action moment ---

    public bool ConsumeActionMoment()
    {
        if (_actionMomentFired) return false;
        if (!HasReachedActionMoment()) return false;
        _actionMomentFired = true;
        return true;
    }

    public bool HasReachedActionMoment()
    {
        int effectMs = GetEffectiveEffectTimeMs();
        if (effectMs > 0) return _animTime >= effectMs;

        // Fallback: 50% of total ticks
        var anim = _spriteData?.GetAnim(StateToAnimName(_currentState));
        if (anim != null)
        {
            int total = anim.TotalTicks();
            return total > 0 && _animTime >= total * 0.5f;
        }
        return false;
    }

    public float GetEffectTimeSeconds(AnimState state)
    {
        string animName = StateToAnimName(state);
        if (_timingOverrides != null && _timingOverrides.TryGetValue(animName, out var ov) && ov.EffectTimeMs >= 0)
            return ov.EffectTimeMs / 1000f;
        var meta = GetMetaForState(state);
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
        var meta = GetMetaForState(state);
        if (meta != null)
        {
            int ms = meta.TotalDurationMs();
            if (ms > 0) return ms / 1000f;
        }
        return 0f;
    }

    private AnimationMeta? GetMetaForState(AnimState state)
    {
        if (_animMeta == null || string.IsNullOrEmpty(_unitName)) return null;
        string animName = StateToAnimName(state);
        string key = AnimMetaLoader.MetaKey(_unitName, animName);
        _animMeta.TryGetValue(key, out var meta);
        return meta;
    }

    // --- Effective timing helpers ---

    private int GetEffectiveTotalDurationMs()
    {
        string animName = StateToAnimName(_currentState);
        if (_timingOverrides != null && _timingOverrides.TryGetValue(animName, out var ov) && ov.FrameDurationsMs.Count > 0)
        {
            int total = 0;
            foreach (int d in ov.FrameDurationsMs) total += d;
            return total;
        }
        var meta = GetCurrentMeta();
        return meta?.TotalDurationMs() ?? 0;
    }

    private int GetEffectiveEffectTimeMs()
    {
        string animName = StateToAnimName(_currentState);
        if (_timingOverrides != null && _timingOverrides.TryGetValue(animName, out var ov) && ov.EffectTimeMs >= 0)
            return ov.EffectTimeMs;
        var meta = GetCurrentMeta();
        return meta?.EffectTimeMs ?? 0;
    }

    private List<int>? GetEffectiveFrameDurations(int spriteAngle)
    {
        string animName = StateToAnimName(_currentState);
        if (_timingOverrides != null && _timingOverrides.TryGetValue(animName, out var ov) && ov.FrameDurationsMs.Count > 0)
            return ov.FrameDurationsMs;

        var meta = GetCurrentMeta();
        if (meta != null)
        {
            if (meta.YawData.TryGetValue(spriteAngle, out var ym) && ym.FrameDurationsMs.Count > 0)
                return ym.FrameDurationsMs;
            foreach (var (_, y) in meta.YawData)
                if (y.FrameDurationsMs.Count > 0) return y.FrameDurationsMs;
        }
        return null;
    }

    private AnimationMeta? GetCurrentMeta()
    {
        if (_animMeta == null || string.IsNullOrEmpty(_unitName)) return null;
        string key = AnimMetaLoader.MetaKey(_unitName, StateToAnimName(_currentState));
        return _animMeta.TryGetValue(key, out var meta) ? meta : null;
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
        _ => "Idle"
    };

    public static AnimPlayMode GetPlayMode(AnimState state) => state switch
    {
        AnimState.Idle or AnimState.Walk or AnimState.Jog or AnimState.Run
            or AnimState.Block or AnimState.Stunned or AnimState.JumpLoop
            or AnimState.Panic => AnimPlayMode.Loop,
        AnimState.Death or AnimState.Knockdown or AnimState.Fall => AnimPlayMode.PlayOnceHold,
        _ => AnimPlayMode.PlayOnceTransition
    };

    public static bool IsMovementLocked(AnimState state) => state switch
    {
        AnimState.Attack1 or AnimState.Attack2 or AnimState.Attack3
            or AnimState.Spell1 or AnimState.Special1 or AnimState.Ranged1
            or AnimState.BlockBreak or AnimState.Knockdown or AnimState.Standup
            or AnimState.JumpTakeoff or AnimState.JumpLoop or AnimState.JumpLand
            or AnimState.JumpAttackSetup or AnimState.JumpAttackHit
            or AnimState.Death => true,
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
            or AnimState.Ranged1 => 3,
        AnimState.Dodge or AnimState.BlockReact => 4,
        AnimState.JumpTakeoff or AnimState.JumpLoop or AnimState.JumpLand
            or AnimState.JumpAttackSetup or AnimState.JumpAttackHit => 5,
        AnimState.Knockdown => 6,
        AnimState.Death or AnimState.Fall => 100,
        _ => 0
    };
}
