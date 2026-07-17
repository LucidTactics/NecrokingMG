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

    /// <summary>Draw a checkmark fitted inside <paramref name="cell"/> — two thick
    /// strokes (short down-right, long up-right). The canonical "this option is
    /// selected" glyph for dropdown lists (the SpriteFonts have no ✓ glyph).</summary>
    public static void DrawCheckmark(SpriteScope batch, Texture2D pixel, Rectangle cell, Color color, float thickness = 2f)
    {
        var a = new Vector2(cell.X + cell.Width * 0.10f, cell.Y + cell.Height * 0.55f);
        var b = new Vector2(cell.X + cell.Width * 0.40f, cell.Y + cell.Height * 0.85f);
        var c = new Vector2(cell.X + cell.Width * 0.90f, cell.Y + cell.Height * 0.15f);
        DrawLine(batch, pixel, a, b, color, thickness);
        DrawLine(batch, pixel, b, c, color, thickness);
    }

    /// <summary>Draw a checkbox glyph: a bg-filled square with a border (lit when
    /// <paramref name="hovered"/>) and an accent-filled inner square when
    /// <paramref name="value"/> is true. The canonical boolean-toggle visual,
    /// shared by the editor property panels (<c>EditorBase.DrawCheckbox</c>) and
    /// the debug settings panel — callers own the label + hit-testing.</summary>
    public static void DrawCheckbox(SpriteScope batch, Texture2D pixel, Rectangle box,
        bool value, bool hovered, Color bg, Color border, Color borderHover, Color accent)
    {
        batch.Draw(pixel, box, bg);
        DrawRectBorder(batch, pixel, box, hovered ? borderHover : border);
        if (value)
            batch.Draw(pixel, new Rectangle(box.X + 3, box.Y + 3, box.Width - 6, box.Height - 6), accent);
    }

    /// <summary>Draw a texture (or a source region of it) scaled to fit inside
    /// <paramref name="dest"/> preserving aspect ratio, centered on both axes.
    /// The canonical "sprite thumbnail in a UI cell" draw (build menu grid, etc.).</summary>
    public static void DrawAspectFit(SpriteScope batch, Texture2D tex, Rectangle? src,
        Rectangle dest, Color color)
    {
        int srcW = src?.Width ?? tex.Width, srcH = src?.Height ?? tex.Height;
        if (srcW <= 0 || srcH <= 0 || dest.Width <= 0 || dest.Height <= 0) return;
        float scale = MathF.Min((float)dest.Width / srcW, (float)dest.Height / srcH);
        int w = (int)(srcW * scale), h = (int)(srcH * scale);
        var fitted = new Rectangle(dest.X + (dest.Width - w) / 2, dest.Y + (dest.Height - h) / 2, w, h);
        batch.Draw(tex, fitted, src, color);
    }

    /// <summary>Replace characters the embedded ASCII-only SpriteFonts can't render
    /// with '?'. Run any dynamic string through this before MeasureString/DrawString —
    /// an out-of-range char throws in SpriteFont. The canonical sanitize (was private
    /// in GameRenderer.Hud.cs).</summary>
    public static string SanitizeAscii(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        bool needs = false;
        for (int i = 0; i < text.Length; i++)
            if (text[i] > 126 || (text[i] < 32 && text[i] != '\n')) { needs = true; break; }
        if (!needs) return text;
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text) sb.Append(ch >= 32 && ch <= 126 ? ch : '?');
        return sb.ToString();
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
