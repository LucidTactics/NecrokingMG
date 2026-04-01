using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Necroking.Render;

/// <summary>
/// Shared drawing primitives: circle outlines, lines, etc.
/// Used by editors, HUD, and gameplay rendering.
/// </summary>
public static class DrawUtils
{
    /// <summary>Draw a circle outline using line segments.</summary>
    public static void DrawCircleOutline(SpriteBatch batch, Texture2D pixel,
        Vector2 center, float radius, Color color, int segments = 48)
    {
        float step = MathF.PI * 2f / segments;
        for (int i = 0; i < segments; i++)
        {
            float a1 = i * step;
            float a2 = (i + 1) * step;
            var p1 = center + new Vector2(MathF.Cos(a1) * radius, MathF.Sin(a1) * radius);
            var p2 = center + new Vector2(MathF.Cos(a2) * radius, MathF.Sin(a2) * radius);
            DrawLine(batch, pixel, p1, p2, color);
        }
    }

    /// <summary>Draw a 1-pixel line between two points.</summary>
    public static void DrawLine(SpriteBatch batch, Texture2D pixel, Vector2 a, Vector2 b, Color color)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 0.5f) return;
        float angle = MathF.Atan2(dy, dx);
        batch.Draw(pixel, new Rectangle((int)a.X, (int)a.Y, (int)len, 1),
            null, color, angle, Vector2.Zero, SpriteEffects.None, 0);
    }
}
