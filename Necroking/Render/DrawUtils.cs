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
    public static void DrawCircleOutline(SpriteScope batch, Texture2D pixel,
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
    public static void DrawLine(SpriteScope batch, Texture2D pixel, Vector2 a, Vector2 b, Color color)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 0.5f) return;
        float angle = MathF.Atan2(dy, dx);
        batch.Draw(pixel, new Rectangle((int)a.X, (int)a.Y, (int)len, 1),
            null, color, angle, Vector2.Zero, SpriteEffects.None, 0);
    }

    /// <summary>Draw a line with an arbitrary pixel thickness (centered on the a→b axis).</summary>
    public static void DrawLine(SpriteScope batch, Texture2D pixel, Vector2 a, Vector2 b, Color color, float thickness)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 0.5f) return;
        float angle = MathF.Atan2(dy, dx);
        // Origin (0, 0.5) centers the 1-px-tall pixel strip vertically before scaling to thickness.
        batch.Draw(pixel, a, null, color, angle, new Vector2(0f, 0.5f),
            new Vector2(len, thickness), SpriteEffects.None, 0f);
    }

    /// <summary>Draw a rectangle outline. The single canonical rect-stroke — replaces
    /// ~13 per-file DrawBorder/DrawRectOutline copies. Corners are non-overlapping (drawn
    /// once each) so the stroke is correct under a translucent color as well as solid.</summary>
    public static void DrawRectBorder(SpriteScope batch, Texture2D pixel, Rectangle r, Color color, int thickness = 1)
    {
        // Top/bottom span the full width; left/right fill only the gap between them, so
        // each corner pixel is drawn exactly once. (A full-height left/right would
        // double-draw the corners — invisible at solid alpha, but it darkens them under a
        // translucent color, e.g. the crafting/building menu borders.)
        batch.Draw(pixel, new Rectangle(r.X, r.Y, r.Width, thickness), color);                          // top
        batch.Draw(pixel, new Rectangle(r.X, r.Bottom - thickness, r.Width, thickness), color);         // bottom
        int midH = r.Height - thickness * 2;
        if (midH > 0)
        {
            batch.Draw(pixel, new Rectangle(r.X, r.Y + thickness, thickness, midH), color);             // left
            batch.Draw(pixel, new Rectangle(r.Right - thickness, r.Y + thickness, thickness, midH), color); // right
        }
    }
}
