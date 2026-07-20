using System;
using Microsoft.Xna.Framework;
using Necroking.Core;

namespace Necroking.Render;

/// <summary>
/// Pulse envelope shared by the in-game sprite outline
/// (GameRenderer.DrawSpriteOutline) and the editor buff preview
/// (BuffPreview.DrawPulsingOutline) — one source for the width/color math so
/// the preview can't drift from the game. Channels come back as 0-1 floats
/// with intensity separate: the game path feeds the HDR shader uniform, the
/// editor path bakes a clamped LDR Color.
/// </summary>
public static class OutlinePulse
{
    public static void Evaluate(HdrColor c1, HdrColor c2, float outlineWidth, float pulseWidth,
        float pulseSpeed, float time,
        out float offset, out float r, out float g, out float b, out float a, out float intensity)
    {
        float t = 0.5f + 0.5f * MathF.Sin(time * pulseSpeed * 2f * MathF.PI);
        offset = outlineWidth + (pulseWidth - outlineWidth) * t;
        r = MathHelper.Lerp(c1.R / 255f, c2.R / 255f, t);
        g = MathHelper.Lerp(c1.G / 255f, c2.G / 255f, t);
        b = MathHelper.Lerp(c1.B / 255f, c2.B / 255f, t);
        a = MathHelper.Lerp(c1.A / 255f, c2.A / 255f, t);
        intensity = MathHelper.Lerp(c1.Intensity, c2.Intensity, t);
    }
}
