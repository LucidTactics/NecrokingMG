using System;
using Microsoft.Xna.Framework;
using Necroking.Core;
using Necroking.Data.Registries;

namespace Necroking.Editor;

/// <summary>
/// Draws the "General" tab of the Settings window.
/// Covers: display toggles, ground rendering, editor prefs,
/// combat log, buildings, and damage numbers.
/// </summary>
public static class SettingsGeneralTab
{
    private const int RowH = 24;

    /// <summary>
    /// Draw all General settings fields. Returns the total height consumed.
    /// </summary>
    public static int Draw(EditorBase ui, GeneralSettings s, int x, int y, int w)
    {
        return Draw(ui, s, null, null, x, y, w);
    }

    public static int Draw(EditorBase ui, GeneralSettings s, PerformanceSettings? perf, int x, int y, int w)
    {
        return Draw(ui, s, perf, null, x, y, w);
    }

    public static int Draw(EditorBase ui, GeneralSettings s, PerformanceSettings? perf, CombatSettings? combat, int x, int y, int w)
    {
        int curY = y;

        // --- Display toggles ---
        s.ShowTimeControls = ui.DrawCheckbox("Show Time Controls", s.ShowTimeControls, x, curY);
        curY += RowH;

        s.ShowUnitRadius = ui.DrawCheckbox("Show Unit Radius", s.ShowUnitRadius, x, curY);
        curY += RowH;

        s.ShowObjectRadius = ui.DrawCheckbox("Show Object Radius", s.ShowObjectRadius, x, curY);
        curY += RowH;

        s.PauseDimBackground = ui.DrawCheckbox("Dim Background on Pause", s.PauseDimBackground, x, curY);
        curY += RowH;

        // --- Ground Rendering ---
        curY += 4;
        DrawSectionHeader(ui, "Ground Rendering", x, ref curY, w);

        s.GroundTypeWarp = ui.DrawFloatField("gen_groundTypeWarp", "Type Warp", s.GroundTypeWarp, x, curY, w, 0.1f);
        curY += RowH;

        s.GroundUVWarpAmp = ui.DrawFloatField("gen_groundUVWarpAmp", "UV Warp Amp", s.GroundUVWarpAmp, x, curY, w, 0.05f);
        curY += RowH;

        s.GroundUVWarpFreq = ui.DrawFloatField("gen_groundUVWarpFreq", "UV Warp Freq", s.GroundUVWarpFreq, x, curY, w, 0.01f);
        curY += RowH;

        // --- Physics ---
        // Gravity scales the Z deceleration on every flying body (knockback,
        // trample, spell, corpse). Realistic ≈ 10 (1 unit ≈ 1 metre); engine
        // default 50 is a snappy gamey value. Lower = floatier, longer arcs.
        curY += 4;
        DrawSectionHeader(ui, "Physics", x, ref curY, w);

        float newGravity = ui.DrawFloatField("gen_gravity", "Gravity (u/s²)", s.Gravity, x, curY, w, 1.0f);
        s.Gravity = MathF.Max(0.5f, newGravity);
        curY += RowH;

        // --- Editor ---
        curY += 4;
        DrawSectionHeader(ui, "Editor", x, ref curY, w);

        s.EditorScrollSpeed = ui.DrawFloatField("gen_editorScrollSpeed", "Scroll Speed", s.EditorScrollSpeed, x, curY, w, 1.0f);
        curY += RowH;

        s.EditorScrollAccel = ui.DrawFloatField("gen_editorScrollAccel", "Scroll Accel", s.EditorScrollAccel, x, curY, w, 0.5f);
        curY += RowH;

        s.WpRapidEdit = ui.DrawCheckbox("WP Rapid Edit", s.WpRapidEdit, x, curY);
        curY += RowH;

        // --- Combat Log ---
        curY += 4;
        DrawSectionHeader(ui, "Combat Log", x, ref curY, w);

        s.CombatLogEnabled = ui.DrawCheckbox("Enabled", s.CombatLogEnabled, x, curY);
        curY += RowH;

        s.CombatLogLines = ui.DrawIntField("gen_combatLogLines", "Max Lines", s.CombatLogLines, x, curY, w);
        curY += RowH;

        s.CombatLogFadeTime = ui.DrawFloatField("gen_combatLogFadeTime", "Fade Time", s.CombatLogFadeTime, x, curY, w, 0.1f);
        curY += RowH;

        s.CombatLogFontSize = ui.DrawIntField("gen_combatLogFontSize", "Font Size", s.CombatLogFontSize, x, curY, w);
        curY += RowH;

        // --- Buildings ---
        curY += 4;
        DrawSectionHeader(ui, "Buildings", x, ref curY, w);

        s.BuildingsDestructible = ui.DrawCheckbox("Destructible", s.BuildingsDestructible, x, curY);
        curY += RowH;

        s.BuildingDepositRange = ui.DrawFloatField("gen_buildingDepositRange", "Deposit Range", s.BuildingDepositRange, x, curY, w, 0.5f);
        curY += RowH;

        s.BuildingPlacementRange = ui.DrawFloatField("gen_buildingPlacementRange", "Placement Range", s.BuildingPlacementRange, x, curY, w, 0.5f);
        curY += RowH;

        // --- Damage Numbers ---
        curY += 4;
        DrawSectionHeader(ui, "Damage Numbers", x, ref curY, w);

        s.DamageNumbersEnabled = ui.DrawCheckbox("Enabled", s.DamageNumbersEnabled, x, curY);
        curY += RowH;

        // Color swatch (ColorJson -> interactive color picker + RGBA int fields)
        var dc = s.DamageNumberColor;
        var dmgHdr = new HdrColor((byte)System.Math.Clamp(dc.R, 0, 255),
                                   (byte)System.Math.Clamp(dc.G, 0, 255),
                                   (byte)System.Math.Clamp(dc.B, 0, 255),
                                   (byte)System.Math.Clamp(dc.A, 0, 255));
        ui.DrawText("Color", new Vector2(x, curY + 2), EditorBase.TextDim);
        if (ui.DrawColorSwatch("gen_dmgColor", x + 120, curY, 40, 18, ref dmgHdr, true))
        {
            dc.R = dmgHdr.R; dc.G = dmgHdr.G; dc.B = dmgHdr.B; dc.A = dmgHdr.A;
            s.DamageNumberColor = dc;
        }
        curY += RowH;

        dc.R = System.Math.Clamp(ui.DrawIntField("gen_dmgColorR", "  R", dc.R, x, curY, w), 0, 255);
        curY += RowH;
        dc.G = System.Math.Clamp(ui.DrawIntField("gen_dmgColorG", "  G", dc.G, x, curY, w), 0, 255);
        curY += RowH;
        dc.B = System.Math.Clamp(ui.DrawIntField("gen_dmgColorB", "  B", dc.B, x, curY, w), 0, 255);
        curY += RowH;
        dc.A = System.Math.Clamp(ui.DrawIntField("gen_dmgColorA", "  A", dc.A, x, curY, w), 0, 255);
        curY += RowH;
        s.DamageNumberColor = dc;

        s.DamageNumberSize = ui.DrawIntField("gen_dmgNumSize", "Size", s.DamageNumberSize, x, curY, w);
        curY += RowH;

        s.DamageNumberFadeTime = ui.DrawFloatField("gen_dmgNumFadeTime", "Fade Time", s.DamageNumberFadeTime, x, curY, w, 0.05f);
        curY += RowH;

        s.DamageNumberSpeed = ui.DrawFloatField("gen_dmgNumSpeed", "Speed", s.DamageNumberSpeed, x, curY, w, 0.1f);
        curY += RowH + 8;

        DrawSectionHeader(ui, "Pickup", x, ref curY, w);
        s.AutoPickupForagables = ui.DrawCheckbox("Auto-Pickup Foragables", s.AutoPickupForagables, x + 10, curY);
        curY += RowH;

        if (combat != null)
        {
            curY += 4;
            DrawSectionHeader(ui, "Combat", x, ref curY, w);

            float newRd = ui.DrawFloatField("gen_roundDuration", "Round Duration (s)", combat.RoundDuration, x, curY, w, 0.25f);
            if (System.Math.Abs(newRd - combat.RoundDuration) > 0.001f)
                combat.RoundDuration = System.MathF.Max(0.25f, newRd);
            curY += RowH;

            ui.DrawText("Weapon cycle = CooldownRounds × Round Duration.", new Vector2(x, curY), EditorBase.TextDim);
            curY += 20;
        }

        if (perf != null)
        {
            curY += 4;
            DrawSectionHeader(ui, "Performance (debug)", x, ref curY, w);

            perf.BudgetedPathfinding = ui.DrawCheckbox("Budgeted Pathfinding", perf.BudgetedPathfinding, x + 10, curY);
            curY += RowH;
            perf.DijkstraBudgetMsPerTick = ui.DrawFloatField("perf_dijkstraMs", "Dijkstra Budget (ms/tick)", perf.DijkstraBudgetMsPerTick, x, curY, w, 0.5f);
            curY += RowH;

            ui.DrawText("Caps Dijkstra work/frame; overflow defers (uses stale flow).", new Vector2(x, curY), EditorBase.TextDim);
            curY += 16;
            ui.DrawText("Smooths summon-burst spikes. May hide perf regressions.", new Vector2(x, curY), EditorBase.TextDim);
            curY += 20;

            perf.AmortizedAI = ui.DrawCheckbox("Amortized AI", perf.AmortizedAI, x + 10, curY);
            curY += RowH;
            int interval = ui.DrawIntField("perf_aiInterval", "AI Update Interval (frames)", perf.AIUpdateInterval, x, curY, w);
            if (interval != perf.AIUpdateInterval) perf.AIUpdateInterval = System.Math.Max(1, interval);
            curY += RowH;

            ui.DrawText("Horde follow/return + unaware scans update every N frames.", new Vector2(x, curY), EditorBase.TextDim);
            curY += 16;
            ui.DrawText("Combat (chase/engaged/alert) stays every-frame.", new Vector2(x, curY), EditorBase.TextDim);
            curY += 20;
        }

        return curY - y;
    }

    private static void DrawSectionHeader(EditorBase ui, string text, int x, ref int curY, int w)
    {
        ui.DrawRect(new Rectangle(x, curY, w, 1), new Color(60, 60, 80));
        curY += 6;
        ui.DrawText(text, new Vector2(x, curY), EditorBase.AccentColor);
        curY += 22;
    }
}
