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

    /// <summary>
    /// Runtime-only blended weather effects. Written each frame by the day/night cycle.
    /// Renderers should read from this instead of the preset when IsActive is true.
    /// </summary>
    public WeatherEffects RuntimeEffects { get; } = new();

    /// <summary>
    /// True when the day/night cycle is enabled and actively overriding weather effects.
    /// </summary>
    public bool IsActive { get; private set; }

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
        if (!settings.Enabled)
        {
            IsActive = false;
            return;
        }

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
        if (curPreset == null) { IsActive = false; return; }

        // Start from the active preset's values so non-interpolated fields (rain, fog, etc.) are preserved
        var activePreset = GetActivePreset(gameData);
        if (activePreset == null) { IsActive = false; return; }
        CopyEffects(activePreset.Effects, RuntimeEffects);

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

        var src = prevPreset?.Effects;
        var dst = curPreset.Effects;
        var fx = RuntimeEffects;

        if (src != null && t < 1f)
        {
            // Lerp color grading fields into runtime effects
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

        IsActive = true;
    }

    private static void CopyEffects(WeatherEffects src, WeatherEffects dst)
    {
        dst.Brightness = src.Brightness;
        dst.Contrast = src.Contrast;
        dst.Saturation = src.Saturation;
        dst.TintR = src.TintR; dst.TintG = src.TintG; dst.TintB = src.TintB;
        dst.TintStrength = src.TintStrength;
        dst.AmbientR = src.AmbientR; dst.AmbientG = src.AmbientG; dst.AmbientB = src.AmbientB;
        dst.VignetteStrength = src.VignetteStrength;
        dst.VignetteRadius = src.VignetteRadius; dst.VignetteSoftness = src.VignetteSoftness;
        dst.FogDensity = src.FogDensity;
        dst.FogR = src.FogR; dst.FogG = src.FogG; dst.FogB = src.FogB;
        dst.FogSpeed = src.FogSpeed; dst.FogScale = src.FogScale;
        dst.HazeStrength = src.HazeStrength;
        dst.HazeR = src.HazeR; dst.HazeG = src.HazeG; dst.HazeB = src.HazeB;
        dst.LightningEnabled = src.LightningEnabled;
        dst.LightningMinInterval = src.LightningMinInterval;
        dst.LightningMaxInterval = src.LightningMaxInterval;
        dst.RainDensity = src.RainDensity; dst.RainSpeed = src.RainSpeed;
        dst.RainWindAngle = src.RainWindAngle; dst.RainAlpha = src.RainAlpha;
        dst.RainLength = src.RainLength; dst.RainFarOpacity = src.RainFarOpacity;
        dst.RainNearScale = src.RainNearScale; dst.RainSplashScale = src.RainSplashScale;
        dst.WindStrength = src.WindStrength; dst.WindAngle = src.WindAngle;
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
