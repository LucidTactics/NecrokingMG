using System;
using Microsoft.Xna.Framework;
using Necroking.Core;
using Necroking.Data.Registries;

namespace Necroking.Editor;

/// <summary>
/// Draws the Environment (Grass) settings tab content for the SettingsWindow.
/// All grass-related fields are exposed: colors, density, height, blade params, and wind.
/// </summary>
public static class SettingsEnvironmentTab
{
    private const int RowH = 24;
    private const int Pad = 8;

    /// <summary>
    /// Draw the full grass settings panel.
    /// Returns the total content height consumed (for scroll calculations).
    /// </summary>
    public static int Draw(EditorBase ui, GrassSettings grass, int x, int y, int w)
    {
        int curY = y;

        // --- Section: Colors ---
        ui.DrawText("-- Colors --", new Vector2(x, curY), EditorBase.AccentColor);
        curY += RowH;

        // Base Color (convert ColorJson <-> HdrColor for the swatch)
        var baseHdr = ColorJsonToHdr(grass.BaseColor);
        ui.DrawText("Base Color", new Vector2(x, curY + 2), EditorBase.TextDim);
        if (ui.DrawColorSwatch("env_baseColor", x + 120, curY, 40, 18, ref baseHdr))
        {
            HdrToColorJson(baseHdr, grass.BaseColor);
        }
        // Also allow direct R/G/B editing
        int newR = ui.DrawIntField("env_baseR", "  R", grass.BaseColor.R, x, curY + RowH, w);
        grass.BaseColor.R = Math.Clamp(newR, 0, 255);
        int newG = ui.DrawIntField("env_baseG", "  G", grass.BaseColor.G, x, curY + RowH * 2, w);
        grass.BaseColor.G = Math.Clamp(newG, 0, 255);
        int newB = ui.DrawIntField("env_baseB", "  B", grass.BaseColor.B, x, curY + RowH * 3, w);
        grass.BaseColor.B = Math.Clamp(newB, 0, 255);
        // Sync swatch changes back
        HdrToColorJson(baseHdr, grass.BaseColor);
        curY += RowH * 4;

        // Tip Color
        var tipHdr = ColorJsonToHdr(grass.TipColor);
        ui.DrawText("Tip Color", new Vector2(x, curY + 2), EditorBase.TextDim);
        if (ui.DrawColorSwatch("env_tipColor", x + 120, curY, 40, 18, ref tipHdr))
        {
            HdrToColorJson(tipHdr, grass.TipColor);
        }
        int tipR = ui.DrawIntField("env_tipR", "  R", grass.TipColor.R, x, curY + RowH, w);
        grass.TipColor.R = Math.Clamp(tipR, 0, 255);
        int tipG = ui.DrawIntField("env_tipG", "  G", grass.TipColor.G, x, curY + RowH * 2, w);
        grass.TipColor.G = Math.Clamp(tipG, 0, 255);
        int tipB = ui.DrawIntField("env_tipB", "  B", grass.TipColor.B, x, curY + RowH * 3, w);
        grass.TipColor.B = Math.Clamp(tipB, 0, 255);
        HdrToColorJson(tipHdr, grass.TipColor);
        curY += RowH * 4;

        // --- Section: Blade Parameters ---
        ui.DrawText("-- Blade Parameters --", new Vector2(x, curY), EditorBase.AccentColor);
        curY += RowH;

        grass.Density = ui.DrawFloatField("env_density", "Density", grass.Density, x, curY, w, 1.0f);
        grass.Density = Math.Clamp(grass.Density, 0f, 255f);
        curY += RowH;

        grass.Height = ui.DrawFloatField("env_height", "Height", grass.Height, x, curY, w, 1.0f);
        grass.Height = Math.Clamp(grass.Height, 0f, 255f);
        curY += RowH;

        int blades = ui.DrawIntField("env_bladesPerCell", "Blades/Cell", grass.BladesPerCell, x, curY, w);
        grass.BladesPerCell = Math.Clamp(blades, 1, 30);
        curY += RowH;

        grass.CellSize = ui.DrawFloatField("env_cellSize", "Cell Size", grass.CellSize, x, curY, w, 0.05f);
        grass.CellSize = Math.Clamp(grass.CellSize, 0.4f, 2.0f);
        curY += RowH;

        // --- Section: Wind ---
        ui.DrawText("-- Wind --", new Vector2(x, curY), EditorBase.AccentColor);
        curY += RowH;

        grass.WindSpeed = ui.DrawFloatField("env_windSpeed", "Wind Speed", grass.WindSpeed, x, curY, w, 0.05f);
        grass.WindSpeed = Math.Clamp(grass.WindSpeed, 0f, 3f);
        curY += RowH;

        grass.WindStrength = ui.DrawFloatField("env_windStrength", "Wind Strength", grass.WindStrength, x, curY, w, 0.05f);
        grass.WindStrength = Math.Clamp(grass.WindStrength, 0f, 3f);
        curY += RowH;

        // --- Section: Options ---
        ui.DrawText("-- Options --", new Vector2(x, curY), EditorBase.AccentColor);
        curY += RowH;

        grass.FwidthSmoothing = ui.DrawCheckbox("Fwidth Smoothing", grass.FwidthSmoothing, x, curY);
        curY += RowH;

        grass.MinBladeWidth = ui.DrawCheckbox("Min Blade Width", grass.MinBladeWidth, x, curY);
        curY += RowH;

        return curY - y;
    }

    // --- Helpers to convert between ColorJson and HdrColor ---

    private static HdrColor ColorJsonToHdr(ColorJson c)
    {
        return new HdrColor((byte)Math.Clamp(c.R, 0, 255),
                            (byte)Math.Clamp(c.G, 0, 255),
                            (byte)Math.Clamp(c.B, 0, 255),
                            (byte)Math.Clamp(c.A, 0, 255));
    }

    private static void HdrToColorJson(HdrColor hdr, ColorJson c)
    {
        c.R = hdr.R;
        c.G = hdr.G;
        c.B = hdr.B;
        c.A = hdr.A;
    }
}
