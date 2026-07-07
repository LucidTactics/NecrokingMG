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
        ui.DrawSectionHeader("Formation", x, ref curY, w);

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
        ui.DrawSectionHeader("Drift", x, ref curY, w);

        s.DriftHz = ui.DrawFloatField("horde_driftHz", "Drift Hz", s.DriftHz, x, curY, w, 0.05f);
        curY += RowH;

        s.DriftAmplitude = ui.DrawFloatField("horde_driftAmplitude", "Drift Amplitude", s.DriftAmplitude, x, curY, w, 0.1f);
        curY += RowH;

        s.IdleRadius = ui.DrawFloatField("horde_idleRadius", "Idle Radius", s.IdleRadius, x, curY, w, 0.5f);
        curY += RowH;

        // --- Combat AI ---
        curY += 4;
        ui.DrawSectionHeader("Combat AI", x, ref curY, w);

        // Orange (engagement) and red (leash) F7 circles are stacked offsets
        // on top of the green EffectiveRadius — so they scale with the horde
        // and the absolute values aren't editable anymore. Direct EngagementRange
        // and LeashRadius fields were removed; tune via these offsets instead.
        s.EngagementOffset = ui.DrawFloatField("horde_engagementOffset", "Engagement Offset (over Effective)", s.EngagementOffset, x, curY, w, 0.5f);
        curY += RowH;

        s.LeashOffset = ui.DrawFloatField("horde_leashOffset", "Leash Offset (over Engagement)", s.LeashOffset, x, curY, w, 0.5f);
        curY += RowH;

        s.MinAggroRadius = ui.DrawFloatField("horde_minAggroRadius", "Min Aggro Radius", s.MinAggroRadius, x, curY, w, 0.5f);
        curY += RowH;

        s.ReturnSpeedMult = ui.DrawFloatField("horde_returnSpeedMult", "Return Speed Mult", s.ReturnSpeedMult, x, curY, w, 0.05f);
        curY += RowH;

        s.VelocityDirLerp = ui.DrawFloatField("horde_velocityDirLerp", "Velocity Dir Lerp", s.VelocityDirLerp, x, curY, w, 0.5f);
        curY += RowH;

        return curY - y;
    }

}
