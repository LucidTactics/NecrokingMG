using System;
using Necroking.Data;
using Necroking.Data.Registries;

namespace Necroking.GameSystems;

public enum DayNightPhase : byte { Dawn, Day, Dusk, Night }

public class DayNightSystem
{
    private float _phaseTimer;       // Time elapsed in current phase (seconds)
    private DayNightPhase _phase = DayNightPhase.Dawn;
    private float _currentPhaseDuration;

    public DayNightPhase Phase => _phase;
    public float PhaseTimer => _phaseTimer;
    public float PhaseDuration => _currentPhaseDuration;
    public float PhaseProgress => _currentPhaseDuration > 0f ? _phaseTimer / _currentPhaseDuration : 1f;

    /// <summary>
    /// Total cycle time elapsed (for display purposes).
    /// </summary>
    public float TotalTime { get; private set; }

    public void Init(DayNightSettings settings)
    {
        _phase = DayNightPhase.Dawn;
        _phaseTimer = 0f;
        _currentPhaseDuration = settings.DawnDuration;
        TotalTime = 0f;
    }

    /// <summary>
    /// Advance the cycle and apply color grading overrides to the active weather preset.
    /// Only modifies color grading and ambient fields — rain, fog density, etc. are untouched.
    /// </summary>
    public void Update(float dt, GameData gameData)
    {
        var settings = gameData.Settings.DayNight;
        if (!settings.Enabled) return;

        _currentPhaseDuration = GetPhaseDuration(settings, _phase);
        _phaseTimer += dt;
        TotalTime += dt;

        // Advance to next phase if current is complete
        while (_phaseTimer >= _currentPhaseDuration && _currentPhaseDuration > 0f)
        {
            _phaseTimer -= _currentPhaseDuration;
            _phase = NextPhase(_phase);
            _currentPhaseDuration = GetPhaseDuration(settings, _phase);
        }

        // Get the two presets we're interpolating between
        var prevPhase = PrevPhase(_phase);
        var prevPreset = GetPhasePreset(gameData, settings, prevPhase);
        var curPreset = GetPhasePreset(gameData, settings, _phase);
        if (curPreset == null) return;

        // During the first 25% of a phase, lerp from previous phase to current
        // During the remaining 75%, hold at current phase values
        float transitionFraction = 0.25f;
        float transitionDuration = _currentPhaseDuration * transitionFraction;
        float t; // 0 = previous phase values, 1 = current phase values

        if (_phaseTimer < transitionDuration && transitionDuration > 0f && prevPreset != null)
            t = _phaseTimer / transitionDuration;
        else
            t = 1f;

        // Smooth the transition with ease-in-out
        t = t * t * (3f - 2f * t);

        // Apply interpolated values to the active weather preset
        var activePreset = GetActivePreset(gameData);
        if (activePreset == null) return;

        var src = prevPreset?.Effects;
        var dst = curPreset.Effects;
        var fx = activePreset.Effects;

        if (src != null && t < 1f)
        {
            // Lerp color grading fields
            fx.Brightness = Lerp(src.Brightness, dst.Brightness, t);
            fx.Contrast = Lerp(src.Contrast, dst.Contrast, t);
            fx.Saturation = Lerp(src.Saturation, dst.Saturation, t);
            fx.TintR = Lerp(src.TintR, dst.TintR, t);
            fx.TintG = Lerp(src.TintG, dst.TintG, t);
            fx.TintB = Lerp(src.TintB, dst.TintB, t);
            fx.TintStrength = Lerp(src.TintStrength, dst.TintStrength, t);
            fx.AmbientR = Lerp(src.AmbientR, dst.AmbientR, t);
            fx.AmbientG = Lerp(src.AmbientG, dst.AmbientG, t);
            fx.AmbientB = Lerp(src.AmbientB, dst.AmbientB, t);
            fx.VignetteStrength = Lerp(src.VignetteStrength, dst.VignetteStrength, t);
            fx.VignetteRadius = Lerp(src.VignetteRadius, dst.VignetteRadius, t);
        }
        else
        {
            // Hold at current phase values
            fx.Brightness = dst.Brightness;
            fx.Contrast = dst.Contrast;
            fx.Saturation = dst.Saturation;
            fx.TintR = dst.TintR;
            fx.TintG = dst.TintG;
            fx.TintB = dst.TintB;
            fx.TintStrength = dst.TintStrength;
            fx.AmbientR = dst.AmbientR;
            fx.AmbientG = dst.AmbientG;
            fx.AmbientB = dst.AmbientB;
            fx.VignetteStrength = dst.VignetteStrength;
            fx.VignetteRadius = dst.VignetteRadius;
        }
    }

    private WeatherPresetDef? GetActivePreset(GameData gameData)
    {
        string presetId = gameData.Settings.Weather.ActivePreset;
        if (string.IsNullOrEmpty(presetId)) return null;
        return gameData.Weather.Get(presetId);
    }

    private WeatherPresetDef? GetPhasePreset(GameData gameData, DayNightSettings settings, DayNightPhase phase)
    {
        string id = phase switch
        {
            DayNightPhase.Dawn => settings.DawnPreset,
            DayNightPhase.Day => settings.DayPreset,
            DayNightPhase.Dusk => settings.DuskPreset,
            DayNightPhase.Night => settings.NightPreset,
            _ => ""
        };
        if (string.IsNullOrEmpty(id)) return null;
        return gameData.Weather.Get(id);
    }

    private static float GetPhaseDuration(DayNightSettings settings, DayNightPhase phase)
    {
        return phase switch
        {
            DayNightPhase.Dawn => settings.DawnDuration,
            DayNightPhase.Day => settings.DayDuration,
            DayNightPhase.Dusk => settings.DuskDuration,
            DayNightPhase.Night => settings.NightDuration,
            _ => 60f
        };
    }

    private static DayNightPhase NextPhase(DayNightPhase phase)
    {
        return phase switch
        {
            DayNightPhase.Dawn => DayNightPhase.Day,
            DayNightPhase.Day => DayNightPhase.Dusk,
            DayNightPhase.Dusk => DayNightPhase.Night,
            DayNightPhase.Night => DayNightPhase.Dawn,
            _ => DayNightPhase.Dawn
        };
    }

    private static DayNightPhase PrevPhase(DayNightPhase phase)
    {
        return phase switch
        {
            DayNightPhase.Dawn => DayNightPhase.Night,
            DayNightPhase.Day => DayNightPhase.Dawn,
            DayNightPhase.Dusk => DayNightPhase.Day,
            DayNightPhase.Night => DayNightPhase.Dusk,
            _ => DayNightPhase.Night
        };
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
