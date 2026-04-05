using System;
using System.Linq;
using Microsoft.Xna.Framework;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.GameSystems;

namespace Necroking.Editor;

/// <summary>
/// Draws the Weather settings tab content for the SettingsWindow.
/// Exposes the weather enabled toggle, active preset selector, transition speed,
/// and all WeatherEffects fields organized into logical sections.
/// </summary>
public static class SettingsWeatherTab
{
    private const int RowH = 24;

    /// <summary>
    /// Draw the full weather settings panel.
    /// Returns the total content height consumed (for scroll calculations).
    /// </summary>
    public static int Draw(EditorBase ui, WeatherSettings weather, GameData gameData, int x, int y, int w,
        GameSystems.DayNightSystem? dayNight = null)
    {
        int curY = y;

        // --- Global Weather Settings ---
        ui.DrawText("-- Weather Settings --", new Vector2(x, curY), EditorBase.AccentColor);
        curY += RowH;

        weather.Enabled = ui.DrawCheckbox("Weather Enabled", weather.Enabled, x, curY);

        // Day/night toggle on same row, offset to the right
        var dn = gameData.Settings.DayNight;
        dn.Enabled = ui.DrawCheckbox("Day night cycle enabled", dn.Enabled, x + 200, curY);
        curY += RowH;

        // Day/night settings (collapsible inline)
        if (dn.Enabled)
        {
            // Show current phase info
            if (dayNight != null)
            {
                string phaseStr = dayNight.Phase.ToString();
                float progress = dayNight.PhaseProgress * 100f;
                ui.DrawText($"  Phase: {phaseStr} ({progress:F0}%)", new Vector2(x + 10, curY + 2),
                    new Color(255, 220, 100));
                curY += RowH;
            }

            // Duration fields (in minutes, stored as seconds)
            float dawnMin = dn.DawnDuration / 60f;
            dawnMin = ui.DrawFloatField("dn_dawn", "  Dawn (min)", dawnMin, x, curY, w, 0.5f);
            dn.DawnDuration = Math.Max(5f, dawnMin * 60f);
            curY += RowH;

            float dayMin = dn.DayDuration / 60f;
            dayMin = ui.DrawFloatField("dn_day", "  Day (min)", dayMin, x, curY, w, 0.5f);
            dn.DayDuration = Math.Max(5f, dayMin * 60f);
            curY += RowH;

            float duskMin = dn.DuskDuration / 60f;
            duskMin = ui.DrawFloatField("dn_dusk", "  Dusk (min)", duskMin, x, curY, w, 0.5f);
            dn.DuskDuration = Math.Max(5f, duskMin * 60f);
            curY += RowH;

            float nightMin = dn.NightDuration / 60f;
            nightMin = ui.DrawFloatField("dn_night", "  Night (min)", nightMin, x, curY, w, 0.5f);
            dn.NightDuration = Math.Max(5f, nightMin * 60f);
            curY += RowH;
        }


        // Active Preset dropdown
        var presetIds = gameData.Weather.GetIDs();
        string[] presetNames;
        if (presetIds.Count > 0)
        {
            presetNames = new string[presetIds.Count];
            for (int i = 0; i < presetIds.Count; i++)
            {
                var def = gameData.Weather.Get(presetIds[i]);
                presetNames[i] = def != null ? def.Id : presetIds[i];
            }
        }
        else
        {
            presetNames = new[] { "(none)" };
        }

        string currentPreset = string.IsNullOrEmpty(weather.ActivePreset) ? presetNames[0] : weather.ActivePreset;
        string selected = ui.DrawCombo("wthr_activePreset", "Active Preset", currentPreset, presetNames, x, curY, w);
        if (selected != currentPreset)
        {
            weather.ActivePreset = selected;
        }
        curY += RowH;

        weather.TransitionSpeed = ui.DrawFloatField("wthr_transSpeed", "Transition Spd", weather.TransitionSpeed, x, curY, w, 0.1f);
        weather.TransitionSpeed = Math.Max(0f, weather.TransitionSpeed);
        curY += RowH;

        // Get the active preset's effects for editing
        var presetDef = !string.IsNullOrEmpty(weather.ActivePreset) ? gameData.Weather.Get(weather.ActivePreset) : null;
        if (presetDef == null && presetIds.Count > 0)
        {
            presetDef = gameData.Weather.Get(presetIds[0]);
        }

        if (presetDef == null)
        {
            ui.DrawText("No weather presets available.", new Vector2(x, curY + 2), EditorBase.TextDim);
            curY += RowH;
            return curY - y;
        }

        var fx = presetDef.Effects;

        // --- Section: Color Grading ---
        curY += 4;
        ui.DrawText("-- Color Grading --", new Vector2(x, curY), EditorBase.AccentColor);
        curY += RowH;

        fx.Brightness = ui.DrawFloatField("wthr_brightness", "Brightness", fx.Brightness, x, curY, w, 0.01f);
        fx.Brightness = Math.Clamp(fx.Brightness, 0f, 3f);
        curY += RowH;

        fx.Contrast = ui.DrawFloatField("wthr_contrast", "Contrast", fx.Contrast, x, curY, w, 0.01f);
        fx.Contrast = Math.Clamp(fx.Contrast, 0f, 3f);
        curY += RowH;

        fx.Saturation = ui.DrawFloatField("wthr_saturation", "Saturation", fx.Saturation, x, curY, w, 0.01f);
        fx.Saturation = Math.Clamp(fx.Saturation, 0f, 3f);
        curY += RowH;

        // Tint color swatch
        var tintColor = FloatRgbToHdr(fx.TintR, fx.TintG, fx.TintB, 2f);
        ui.DrawText("Tint Color", new Vector2(x, curY + 2), EditorBase.TextDim);
        if (ui.DrawColorSwatch("wthr_tintColor", x + 120, curY, 40, 18, ref tintColor, true))
        {
            HdrToFloatRgb(tintColor, 2f, out float tr, out float tg, out float tb);
            fx.TintR = tr; fx.TintG = tg; fx.TintB = tb;
        }
        curY += RowH;

        fx.TintR = ui.DrawFloatField("wthr_tintR", "  Tint R", fx.TintR, x, curY, w, 0.01f);
        fx.TintR = Math.Clamp(fx.TintR, 0f, 2f);
        curY += RowH;

        fx.TintG = ui.DrawFloatField("wthr_tintG", "  Tint G", fx.TintG, x, curY, w, 0.01f);
        fx.TintG = Math.Clamp(fx.TintG, 0f, 2f);
        curY += RowH;

        fx.TintB = ui.DrawFloatField("wthr_tintB", "  Tint B", fx.TintB, x, curY, w, 0.01f);
        fx.TintB = Math.Clamp(fx.TintB, 0f, 2f);
        curY += RowH;

        fx.TintStrength = ui.DrawFloatField("wthr_tintStr", "Tint Strength", fx.TintStrength, x, curY, w, 0.01f);
        fx.TintStrength = Math.Clamp(fx.TintStrength, 0f, 1f);
        curY += RowH;

        // --- Section: Ambient ---
        curY += 4;
        ui.DrawText("-- Ambient --", new Vector2(x, curY), EditorBase.AccentColor);
        curY += RowH;

        // Ambient color swatch
        var ambientColor = FloatRgbToHdr(fx.AmbientR, fx.AmbientG, fx.AmbientB, 2f);
        ui.DrawText("Ambient Color", new Vector2(x, curY + 2), EditorBase.TextDim);
        if (ui.DrawColorSwatch("wthr_ambientColor", x + 120, curY, 40, 18, ref ambientColor, true))
        {
            HdrToFloatRgb(ambientColor, 2f, out float ar, out float ag, out float ab);
            fx.AmbientR = ar; fx.AmbientG = ag; fx.AmbientB = ab;
        }
        curY += RowH;

        fx.AmbientR = ui.DrawFloatField("wthr_ambientR", "  Ambient R", fx.AmbientR, x, curY, w, 0.01f);
        fx.AmbientR = Math.Clamp(fx.AmbientR, 0f, 2f);
        curY += RowH;

        fx.AmbientG = ui.DrawFloatField("wthr_ambientG", "  Ambient G", fx.AmbientG, x, curY, w, 0.01f);
        fx.AmbientG = Math.Clamp(fx.AmbientG, 0f, 2f);
        curY += RowH;

        fx.AmbientB = ui.DrawFloatField("wthr_ambientB", "  Ambient B", fx.AmbientB, x, curY, w, 0.01f);
        fx.AmbientB = Math.Clamp(fx.AmbientB, 0f, 2f);
        curY += RowH;

        // --- Section: Vignette ---
        curY += 4;
        ui.DrawText("-- Vignette --", new Vector2(x, curY), EditorBase.AccentColor);
        curY += RowH;

        fx.VignetteStrength = ui.DrawFloatField("wthr_vigStr", "Strength", fx.VignetteStrength, x, curY, w, 0.01f);
        fx.VignetteStrength = Math.Clamp(fx.VignetteStrength, 0f, 1f);
        curY += RowH;

        fx.VignetteRadius = ui.DrawFloatField("wthr_vigRad", "Radius", fx.VignetteRadius, x, curY, w, 0.01f);
        fx.VignetteRadius = Math.Clamp(fx.VignetteRadius, 0f, 1f);
        curY += RowH;

        fx.VignetteSoftness = ui.DrawFloatField("wthr_vigSoft", "Softness", fx.VignetteSoftness, x, curY, w, 0.01f);
        fx.VignetteSoftness = Math.Clamp(fx.VignetteSoftness, 0f, 1f);
        curY += RowH;

        // --- Section: Fog ---
        curY += 4;
        ui.DrawText("-- Fog --", new Vector2(x, curY), EditorBase.AccentColor);
        curY += RowH;

        fx.FogDensity = ui.DrawFloatField("wthr_fogDen", "Density", fx.FogDensity, x, curY, w, 0.01f);
        fx.FogDensity = Math.Clamp(fx.FogDensity, 0f, 1f);
        curY += RowH;

        // Fog color swatch
        var fogColor = FloatRgbToHdr(fx.FogR, fx.FogG, fx.FogB, 1f);
        ui.DrawText("Fog Color", new Vector2(x, curY + 2), EditorBase.TextDim);
        if (ui.DrawColorSwatch("wthr_fogColor", x + 120, curY, 40, 18, ref fogColor, true))
        {
            HdrToFloatRgb(fogColor, 1f, out float fr, out float fg, out float fb);
            fx.FogR = fr; fx.FogG = fg; fx.FogB = fb;
        }
        curY += RowH;

        fx.FogR = ui.DrawFloatField("wthr_fogR", "  Fog R", fx.FogR, x, curY, w, 0.01f);
        fx.FogR = Math.Clamp(fx.FogR, 0f, 1f);
        curY += RowH;

        fx.FogG = ui.DrawFloatField("wthr_fogG", "  Fog G", fx.FogG, x, curY, w, 0.01f);
        fx.FogG = Math.Clamp(fx.FogG, 0f, 1f);
        curY += RowH;

        fx.FogB = ui.DrawFloatField("wthr_fogB", "  Fog B", fx.FogB, x, curY, w, 0.01f);
        fx.FogB = Math.Clamp(fx.FogB, 0f, 1f);
        curY += RowH;

        fx.FogSpeed = ui.DrawFloatField("wthr_fogSpd", "Fog Speed", fx.FogSpeed, x, curY, w, 0.1f);
        fx.FogSpeed = Math.Max(0f, fx.FogSpeed);
        curY += RowH;

        fx.FogScale = ui.DrawFloatField("wthr_fogScl", "Fog Scale", fx.FogScale, x, curY, w, 0.1f);
        fx.FogScale = Math.Max(0.01f, fx.FogScale);
        curY += RowH;

        // --- Section: Haze ---
        curY += 4;
        ui.DrawText("-- Haze --", new Vector2(x, curY), EditorBase.AccentColor);
        curY += RowH;

        fx.HazeStrength = ui.DrawFloatField("wthr_hazeStr", "Strength", fx.HazeStrength, x, curY, w, 0.01f);
        fx.HazeStrength = Math.Clamp(fx.HazeStrength, 0f, 1f);
        curY += RowH;

        // Haze color swatch
        var hazeColor = FloatRgbToHdr(fx.HazeR, fx.HazeG, fx.HazeB, 1f);
        ui.DrawText("Haze Color", new Vector2(x, curY + 2), EditorBase.TextDim);
        if (ui.DrawColorSwatch("wthr_hazeColor", x + 120, curY, 40, 18, ref hazeColor, true))
        {
            HdrToFloatRgb(hazeColor, 1f, out float hr, out float hg, out float hb);
            fx.HazeR = hr; fx.HazeG = hg; fx.HazeB = hb;
        }
        curY += RowH;

        fx.HazeR = ui.DrawFloatField("wthr_hazeR", "  Haze R", fx.HazeR, x, curY, w, 0.01f);
        fx.HazeR = Math.Clamp(fx.HazeR, 0f, 1f);
        curY += RowH;

        fx.HazeG = ui.DrawFloatField("wthr_hazeG", "  Haze G", fx.HazeG, x, curY, w, 0.01f);
        fx.HazeG = Math.Clamp(fx.HazeG, 0f, 1f);
        curY += RowH;

        fx.HazeB = ui.DrawFloatField("wthr_hazeB", "  Haze B", fx.HazeB, x, curY, w, 0.01f);
        fx.HazeB = Math.Clamp(fx.HazeB, 0f, 1f);
        curY += RowH;

        // --- Section: Lightning ---
        curY += 4;
        ui.DrawText("-- Lightning --", new Vector2(x, curY), EditorBase.AccentColor);
        curY += RowH;

        fx.LightningEnabled = ui.DrawCheckbox("Lightning Enabled", fx.LightningEnabled, x, curY);
        curY += RowH;

        fx.LightningMinInterval = ui.DrawFloatField("wthr_ltMinInt", "Min Interval", fx.LightningMinInterval, x, curY, w, 0.5f);
        fx.LightningMinInterval = Math.Max(0.5f, fx.LightningMinInterval);
        curY += RowH;

        fx.LightningMaxInterval = ui.DrawFloatField("wthr_ltMaxInt", "Max Interval", fx.LightningMaxInterval, x, curY, w, 0.5f);
        fx.LightningMaxInterval = Math.Max(fx.LightningMinInterval + 0.5f, fx.LightningMaxInterval);
        curY += RowH;

        // --- Section: Rain ---
        curY += 4;
        ui.DrawText("-- Rain --", new Vector2(x, curY), EditorBase.AccentColor);
        curY += RowH;

        fx.RainDensity = ui.DrawFloatField("wthr_rainDen", "Density", fx.RainDensity, x, curY, w, 10f);
        fx.RainDensity = Math.Max(0f, fx.RainDensity);
        curY += RowH;

        fx.RainSpeed = ui.DrawFloatField("wthr_rainSpd", "Speed", fx.RainSpeed, x, curY, w, 10f);
        fx.RainSpeed = Math.Max(0f, fx.RainSpeed);
        curY += RowH;

        fx.RainWindAngle = ui.DrawFloatField("wthr_rainWAng", "Wind Angle", fx.RainWindAngle, x, curY, w, 1f);
        curY += RowH;

        fx.RainAlpha = ui.DrawFloatField("wthr_rainAlpha", "Alpha", fx.RainAlpha, x, curY, w, 0.01f);
        fx.RainAlpha = Math.Clamp(fx.RainAlpha, 0f, 1f);
        curY += RowH;

        fx.RainLength = ui.DrawFloatField("wthr_rainLen", "Length", fx.RainLength, x, curY, w, 1f);
        fx.RainLength = Math.Max(1f, fx.RainLength);
        curY += RowH;

        fx.RainFarOpacity = ui.DrawFloatField("wthr_rainFarOp", "Far Opacity", fx.RainFarOpacity, x, curY, w, 0.01f);
        fx.RainFarOpacity = Math.Clamp(fx.RainFarOpacity, 0f, 1f);
        curY += RowH;

        fx.RainNearScale = ui.DrawFloatField("wthr_rainNrScl", "Near Scale", fx.RainNearScale, x, curY, w, 0.1f);
        fx.RainNearScale = Math.Max(0.1f, fx.RainNearScale);
        curY += RowH;

        fx.RainSplashScale = ui.DrawFloatField("wthr_rainSplSc", "Splash Scale", fx.RainSplashScale, x, curY, w, 0.1f);
        fx.RainSplashScale = Math.Max(0.1f, fx.RainSplashScale);
        curY += RowH;

        // --- Section: Wind ---
        curY += 4;
        ui.DrawText("-- Wind --", new Vector2(x, curY), EditorBase.AccentColor);
        curY += RowH;

        fx.WindStrength = ui.DrawFloatField("wthr_windStr", "Strength", fx.WindStrength, x, curY, w, 0.1f);
        fx.WindStrength = Math.Max(0f, fx.WindStrength);
        curY += RowH;

        fx.WindAngle = ui.DrawFloatField("wthr_windAng", "Angle", fx.WindAngle, x, curY, w, 1f);
        curY += RowH;

        return curY - y;
    }

    /// <summary>
    /// Convert 3 float RGB values (in 0..maxVal range) to an HdrColor for the color swatch.
    /// </summary>
    private static HdrColor FloatRgbToHdr(float r, float g, float b, float maxVal)
    {
        byte br = (byte)Math.Clamp(r / maxVal * 255f, 0, 255);
        byte bg = (byte)Math.Clamp(g / maxVal * 255f, 0, 255);
        byte bb = (byte)Math.Clamp(b / maxVal * 255f, 0, 255);
        return new HdrColor(br, bg, bb);
    }

    /// <summary>
    /// Write HdrColor bytes back to float RGB fields (in 0..maxVal range).
    /// </summary>
    private static void HdrToFloatRgb(HdrColor c, float maxVal, out float r, out float g, out float b)
    {
        r = Math.Clamp(c.R / 255f * maxVal, 0f, maxVal);
        g = Math.Clamp(c.G / 255f * maxVal, 0f, maxVal);
        b = Math.Clamp(c.B / 255f * maxVal, 0f, maxVal);
    }
}
