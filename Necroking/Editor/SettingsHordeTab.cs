using Microsoft.Xna.Framework;
using Necroking.Data.Registries;

namespace Necroking.Editor;

/// <summary>
/// Draws the "Horde / Movement" tab of the Settings window.
/// Covers: formation shape, drift/noise, and combat AI parameters.
/// </summary>
public static class SettingsHordeTab
{
    private const int RowH = 24;

    /// <summary>
    /// Draw all Horde settings fields. Returns the total height consumed.
    /// </summary>
    public static int Draw(EditorBase ui, HordeSettings s, int x, int y, int w)
    {
        int curY = y;

        // --- Formation ---
        DrawSectionHeader(ui, "Formation", x, ref curY, w);

        s.CircleOffset = ui.DrawFloatField("horde_circleOffset", "Circle Offset", s.CircleOffset, x, curY, w, 0.5f);
        curY += RowH;

        s.CircleRadius = ui.DrawFloatField("horde_circleRadius", "Circle Radius", s.CircleRadius, x, curY, w, 0.5f);
        curY += RowH;

        s.PositionLerp = ui.DrawFloatField("horde_positionLerp", "Position Lerp", s.PositionLerp, x, curY, w, 0.1f);
        curY += RowH;

        s.RotationLerp = ui.DrawFloatField("horde_rotationLerp", "Rotation Lerp", s.RotationLerp, x, curY, w, 0.1f);
        curY += RowH;

        // --- Drift ---
        curY += 4;
        DrawSectionHeader(ui, "Drift", x, ref curY, w);

        s.DriftHz = ui.DrawFloatField("horde_driftHz", "Drift Hz", s.DriftHz, x, curY, w, 0.05f);
        curY += RowH;

        s.DriftAmplitude = ui.DrawFloatField("horde_driftAmplitude", "Drift Amplitude", s.DriftAmplitude, x, curY, w, 0.1f);
        curY += RowH;

        s.IdleRadius = ui.DrawFloatField("horde_idleRadius", "Idle Radius", s.IdleRadius, x, curY, w, 0.5f);
        curY += RowH;

        // --- Combat AI ---
        curY += 4;
        DrawSectionHeader(ui, "Combat AI", x, ref curY, w);

        s.EngagementRange = ui.DrawFloatField("horde_engagementRange", "Engagement Range", s.EngagementRange, x, curY, w, 0.5f);
        curY += RowH;

        s.LeashRadius = ui.DrawFloatField("horde_leashRadius", "Leash Radius", s.LeashRadius, x, curY, w, 0.5f);
        curY += RowH;

        s.LeashChance = ui.DrawFloatField("horde_leashChance", "Leash Chance", s.LeashChance, x, curY, w, 0.05f);
        curY += RowH;

        s.ReturnSpeedMult = ui.DrawFloatField("horde_returnSpeedMult", "Return Speed Mult", s.ReturnSpeedMult, x, curY, w, 0.05f);
        curY += RowH;

        s.VelocityDirLerp = ui.DrawFloatField("horde_velocityDirLerp", "Velocity Dir Lerp", s.VelocityDirLerp, x, curY, w, 0.5f);
        curY += RowH;

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
