using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Necroking.Core;

namespace Necroking.Editor;

/// <summary>
/// Reusable color harmonization panel.
/// Adjusts multiple colors toward a target color using per-channel HSV strength sliders.
/// Non-destructive: keeps originals so the user can tweak sliders freely.
/// </summary>
public class ColorHarmonizer
{
    public const int MaxColors = 16;

    public bool Active { get; private set; }

    // Target color to harmonize toward
    public HdrColor TargetColor = new(200, 160, 80, 255, 1f);

    // Per-channel strength sliders (0-1)
    public float HueStrength;
    public float SatStrength;
    public float ValStrength;

    // RI12: HSV/HCL mode toggle
    public bool UseHclMode;

    // Snapshot of original colors at the time Begin() was called
    private readonly HdrColor[] _originals = new HdrColor[MaxColors];

    // Live harmonized results (updated each frame via Recompute)
    public readonly HdrColor[] Result = new HdrColor[MaxColors];

    public int NumColors { get; private set; }

    // === HSV Color Space Helpers ===

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float LerpHue(float from, float to, float t)
    {
        float diff = to - from;
        if (diff > 180f) diff -= 360f;
        if (diff < -180f) diff += 360f;
        float result = from + diff * t;
        if (result < 0f) result += 360f;
        if (result >= 360f) result -= 360f;
        return result;
    }

    private static Color Harmonize(Color original, Color target,
        float hStr, float sStr, float vStr)
    {
        var (origH, origS, origV) = ColorPickerPopup.RgbToHsv(original.R, original.G, original.B);
        var (targH, targS, targV) = ColorPickerPopup.RgbToHsv(target.R, target.G, target.B);

        float newH = LerpHue(origH, targH, hStr);
        float newS = Lerp(origS, targS, sStr);
        float newV = Lerp(origV, targV, vStr);

        var (r, g, b) = ColorPickerPopup.HsvToRgb(newH, newS, newV);
        return new Color(r, g, b, original.A);
    }

    // RI12: HCL (Hue-Chroma-Luminance) color space helpers
    // Simplified HCL approximation using cylindrical Lab transform

    private static float GammaToLinear(float c)
    {
        return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    }

    private static float LinearToGamma(float c)
    {
        return c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1f / 2.4f) - 0.055f;
    }

    private static (float L, float a, float b) RgbToLab(byte rB, byte gB, byte bB)
    {
        float rf = GammaToLinear(rB / 255f);
        float gf = GammaToLinear(gB / 255f);
        float bf = GammaToLinear(bB / 255f);

        // sRGB to XYZ (D65)
        float x = rf * 0.4124564f + gf * 0.3575761f + bf * 0.1804375f;
        float y = rf * 0.2126729f + gf * 0.7151522f + bf * 0.0721750f;
        float z = rf * 0.0193339f + gf * 0.1191920f + bf * 0.9503041f;

        // D65 reference
        x /= 0.95047f; y /= 1.00000f; z /= 1.08883f;

        x = x > 0.008856f ? MathF.Cbrt(x) : 7.787f * x + 16f / 116f;
        y = y > 0.008856f ? MathF.Cbrt(y) : 7.787f * y + 16f / 116f;
        z = z > 0.008856f ? MathF.Cbrt(z) : 7.787f * z + 16f / 116f;

        float L = 116f * y - 16f;
        float a = 500f * (x - y);
        float b2 = 200f * (y - z);
        return (L, a, b2);
    }

    private static (byte r, byte g, byte b) LabToRgb(float L, float a, float b2)
    {
        float y = (L + 16f) / 116f;
        float x = a / 500f + y;
        float z = y - b2 / 200f;

        float x3 = x * x * x;
        float y3 = y * y * y;
        float z3 = z * z * z;

        x = x3 > 0.008856f ? x3 : (x - 16f / 116f) / 7.787f;
        y = y3 > 0.008856f ? y3 : (y - 16f / 116f) / 7.787f;
        z = z3 > 0.008856f ? z3 : (z - 16f / 116f) / 7.787f;

        x *= 0.95047f; y *= 1.00000f; z *= 1.08883f;

        float rf = x * 3.2404542f + y * -1.5371385f + z * -0.4985314f;
        float gf = x * -0.9692660f + y * 1.8760108f + z * 0.0415560f;
        float bf = x * 0.0556434f + y * -0.2040259f + z * 1.0572252f;

        rf = Math.Clamp(LinearToGamma(Math.Max(0, rf)), 0f, 1f);
        gf = Math.Clamp(LinearToGamma(Math.Max(0, gf)), 0f, 1f);
        bf = Math.Clamp(LinearToGamma(Math.Max(0, bf)), 0f, 1f);

        return ((byte)(rf * 255f + 0.5f), (byte)(gf * 255f + 0.5f), (byte)(bf * 255f + 0.5f));
    }

    private static (float H, float C, float L) LabToHcl(float L, float a, float b)
    {
        float C = MathF.Sqrt(a * a + b * b);
        float H = MathF.Atan2(b, a) * (180f / MathF.PI);
        if (H < 0) H += 360f;
        return (H, C, L);
    }

    private static (float L, float a, float b) HclToLab(float H, float C, float L)
    {
        float hRad = H * (MathF.PI / 180f);
        float a = C * MathF.Cos(hRad);
        float b = C * MathF.Sin(hRad);
        return (L, a, b);
    }

    private static Color HarmonizeHcl(Color original, Color target,
        float hStr, float cStr, float lStr)
    {
        var (origL, origA, origB) = RgbToLab(original.R, original.G, original.B);
        var (targL, targA, targB) = RgbToLab(target.R, target.G, target.B);

        var (origH, origC, origLum) = LabToHcl(origL, origA, origB);
        var (targH, targC, targLum) = LabToHcl(targL, targA, targB);

        float newH = LerpHue(origH, targH, hStr);
        float newC = Lerp(origC, targC, cStr);
        float newL = Lerp(origLum, targLum, lStr);

        var (labL, labA, labB) = HclToLab(newH, newC, newL);
        var (r, g, b) = LabToRgb(labL, labA, labB);
        return new Color(r, g, b, original.A);
    }

    // === Public API ===

    /// <summary>
    /// Start harmonizing a set of colors (copies originals).
    /// </summary>
    public void Begin(HdrColor[] colors)
    {
        NumColors = Math.Min(colors.Length, MaxColors);
        for (int i = 0; i < NumColors; i++)
        {
            _originals[i] = colors[i];
            Result[i] = colors[i];
        }
        Active = true;
        HueStrength = 0f;
        SatStrength = 0f;
        ValStrength = 0f;
    }

    /// <summary>
    /// Recompute Result[] from originals + current settings.
    /// </summary>
    public void Recompute()
    {
        var targetCol = TargetColor.ToColor();
        for (int i = 0; i < NumColors; i++)
        {
            Color origCol = _originals[i].ToColor();
            Color harmonized = UseHclMode
                ? HarmonizeHcl(origCol, targetCol, HueStrength, SatStrength, ValStrength)
                : Harmonize(origCol, targetCol, HueStrength, SatStrength, ValStrength);
            Result[i] = new HdrColor(harmonized.R, harmonized.G, harmonized.B,
                _originals[i].A, _originals[i].Intensity);
        }
    }

    /// <summary>
    /// Stop harmonizing without applying.
    /// </summary>
    public void Cancel()
    {
        Active = false;
        NumColors = 0;
    }

    /// <summary>
    /// Commit current results as the new originals (for continued editing).
    /// Resets strengths to 0.
    /// </summary>
    public void Apply()
    {
        for (int i = 0; i < NumColors; i++)
            _originals[i] = Result[i];
        HueStrength = 0f;
        SatStrength = 0f;
        ValStrength = 0f;
    }

    /// <summary>
    /// Reset results back to originals and zero out strengths.
    /// </summary>
    public void Reset()
    {
        for (int i = 0; i < NumColors; i++)
            Result[i] = _originals[i];
        HueStrength = 0f;
        SatStrength = 0f;
        ValStrength = 0f;
    }

    /// <summary>
    /// Get the original colors (before harmonization).
    /// </summary>
    public HdrColor GetOriginal(int index) =>
        index >= 0 && index < NumColors ? _originals[index] : default;

    /// <summary>
    /// Draw the harmonizer panel UI inside an editor panel.
    /// Returns true if any setting changed this frame (caller should read Result[]).
    /// </summary>
    public bool DrawPanel(EditorBase editor, int x, ref int curY, int w)
    {
        const int padding = 6;
        const int rowH = 26;
        int labelX = x + padding + 8;
        int fieldX = x + 100;
        int fieldW = w - 116;
        bool changed = false;

        // Section divider
        editor.DrawRect(new Rectangle(x + padding, curY, w - padding * 2, 1), EditorBase.PanelBorder);
        curY += 4;
        editor.DrawText("Harmonize", new Vector2(x + padding, curY), EditorBase.TextBright);
        curY += 20;

        // Target color swatch
        editor.DrawText("Target:", new Vector2(labelX, curY + 4), EditorBase.TextDim);
        if (editor.DrawColorSwatch("harmonize_target", fieldX, curY + 2, 28, rowH - 4, ref TargetColor, hideIntensity: true))
            changed = true;

        // RI13: Pick (eyedropper) button next to target swatch
        int pickBtnX = fieldX + 34;
        if (!editor.IsDropperActive && !editor.IsColorPickerOpen)
        {
            if (editor.DrawButton("Pick", pickBtnX, curY + 2, 40, rowH - 4, new Color(40, 45, 65)))
            {
                editor.OpenColorPicker("harmonize_target", TargetColor, true);
            }
        }

        // Show RGB text next to swatch
        string rgbText = $"({TargetColor.R},{TargetColor.G},{TargetColor.B})";
        editor.DrawText(rgbText, new Vector2(pickBtnX + 46, curY + 6), EditorBase.TextDim);
        curY += rowH + padding;

        // RI12: HSV/HCL mode toggle button
        {
            string modeLabel = UseHclMode ? "HCL" : "HSV";
            int modeBtnW = 48;
            int modeBtnH = 20;
            Color modeBtnColor = UseHclMode ? new Color(70, 50, 90) : new Color(50, 60, 80);
            editor.DrawText("Mode:", new Vector2(labelX, curY + 2), EditorBase.TextDim);
            if (editor.DrawButton(modeLabel, fieldX, curY, modeBtnW, modeBtnH, modeBtnColor))
            {
                UseHclMode = !UseHclMode;
                changed = true;
            }
            string modeHint = UseHclMode ? "(perceptual)" : "(standard)";
            editor.DrawText(modeHint, new Vector2(fieldX + modeBtnW + 6, curY + 3), EditorBase.TextDim);
            curY += modeBtnH + padding;
        }

        // Channel strength sliders
        string[] labels = UseHclMode
            ? new[] { "Hue:", "Chroma:", "Lum:" }
            : new[] { "Hue:", "Sat:", "Value:" };
        float[] strengths = { HueStrength, SatStrength, ValStrength };

        for (int i = 0; i < 3; i++)
        {
            editor.DrawText(labels[i], new Vector2(labelX, curY + 6), EditorBase.TextDim);

            // Slider track
            int sliderX = fieldX;
            int sliderW = fieldW - 62;
            int sliderH2 = 12;
            int sliderY = curY + (rowH - sliderH2) / 2;

            // Track background
            editor.DrawRect(new Rectangle(sliderX, sliderY, sliderW, sliderH2), new Color(30, 30, 42));
            editor.DrawBorder(new Rectangle(sliderX, sliderY, sliderW, sliderH2), EditorBase.InputBorder);

            // Filled portion
            int fillW = (int)(strengths[i] * sliderW);
            if (fillW > 0)
                editor.DrawRect(new Rectangle(sliderX, sliderY, fillW, sliderH2), new Color(60, 80, 140, 180));

            // Thumb
            int thumbX = sliderX + fillW - 4;
            editor.DrawRect(new Rectangle(thumbX, sliderY - 2, 8, sliderH2 + 4), new Color(140, 170, 230));

            // Slider interaction
            var sliderArea = new Rectangle(sliderX - 4, sliderY - 4, sliderW + 8, sliderH2 + 8);
            if (sliderArea.Contains(editor._mouse.X, editor._mouse.Y) &&
                editor._mouse.LeftButton == ButtonState.Pressed)
            {
                float newVal = (editor._mouse.X - sliderX) / (float)sliderW;
                newVal = Math.Clamp(newVal, 0f, 1f);
                if (MathF.Abs(newVal - strengths[i]) > 0.001f)
                {
                    strengths[i] = newVal;
                    changed = true;
                }
            }

            // Numeric value display
            string valText = $"{(int)(strengths[i] * 100)}%";
            editor.DrawText(valText, new Vector2(sliderX + sliderW + 6, curY + 6), EditorBase.TextDim);

            curY += rowH + 2;
        }

        HueStrength = strengths[0];
        SatStrength = strengths[1];
        ValStrength = strengths[2];

        curY += padding;

        // Preview swatches: original -> result
        if (NumColors > 0)
        {
            editor.DrawText("Preview:", new Vector2(labelX, curY + 2), EditorBase.TextDim);
            float swSize = 20f;
            float gap = 3f;
            float sx = fieldX;
            int maxShow = Math.Min(NumColors, 8);
            for (int i = 0; i < maxShow; i++)
            {
                // Original
                editor.DrawRect(new Rectangle((int)sx, curY, (int)swSize, (int)swSize), _originals[i].ToColor());
                editor.DrawBorder(new Rectangle((int)sx, curY, (int)swSize, (int)swSize), new Color(80, 80, 100, 200));

                // Arrow
                editor.DrawText(">", new Vector2(sx + swSize + 1, curY + 4), new Color(100, 100, 120, 200));

                // Result
                float rx = sx + swSize + 12;
                editor.DrawRect(new Rectangle((int)rx, curY, (int)swSize, (int)swSize), Result[i].ToColor());
                editor.DrawBorder(new Rectangle((int)rx, curY, (int)swSize, (int)swSize), new Color(80, 80, 100, 200));

                sx += swSize * 2 + 18 + gap;
            }
            curY += (int)swSize + padding;
        }

        // Apply / Reset buttons
        {
            int btnW = 60;
            int btnH = 24;

            // Apply button
            var applyRect = new Rectangle(fieldX, curY, btnW, btnH);
            bool applyHov = applyRect.Contains(editor._mouse.X, editor._mouse.Y);
            editor.DrawRect(applyRect, applyHov ? new Color(50, 80, 50) : new Color(40, 60, 40));
            editor.DrawBorder(applyRect, new Color(80, 120, 80));
            var applyTxtSize = editor.MeasureText("Apply");
            editor.DrawText("Apply",
                new Vector2(fieldX + (btnW - applyTxtSize.X) / 2, curY + (btnH - applyTxtSize.Y) / 2),
                EditorBase.TextBright);

            // Reset button
            int resetX = fieldX + btnW + 8;
            var resetRect = new Rectangle(resetX, curY, btnW, btnH);
            bool resetHov = resetRect.Contains(editor._mouse.X, editor._mouse.Y);
            editor.DrawRect(resetRect, resetHov ? new Color(70, 50, 50) : new Color(50, 40, 40));
            editor.DrawBorder(resetRect, new Color(120, 80, 80));
            var resetTxtSize = editor.MeasureText("Reset");
            editor.DrawText("Reset",
                new Vector2(resetX + (btnW - resetTxtSize.X) / 2, curY + (btnH - resetTxtSize.Y) / 2),
                EditorBase.TextBright);

            bool leftJustReleased = editor._mouse.LeftButton == ButtonState.Released &&
                                    editor._prevMouse.LeftButton == ButtonState.Pressed;

            if (applyHov && leftJustReleased)
            {
                Apply();
                changed = true;
            }

            if (resetHov && leftJustReleased)
            {
                Reset();
                changed = true;
            }
        }
        curY += 28 + padding;

        if (changed)
            Recompute();

        return changed;
    }
}
