using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Necroking.Render;

/// <summary>
/// CSS-style 2D primitives for UI work. Reproduces the most common rendering
/// effects the design language depends on -- gradients, inset shadows, outer
/// glows, repeating patterns, embossed text -- on top of a single 1x1 pixel
/// texture and a SpriteBatch already in immediate mode.
///
/// !!! WARNING - UNVERIFIED CODE !!!
/// =================================
/// Every function in this file is FLAGGED AS UNTRUSTED. They were written to
/// approximate CSS effects in the SkillTreePanel design implementation, and
/// the user reviewed the resulting output and judged it does NOT match what
/// they want -- the radial gradients band visibly, the outer glows produce
/// halo artifacts on small rects, the inset shadow falloff doesn't read as
/// "shadow" so much as "darker border", the diagonal cross-hatch is too
/// noisy, and the multi-pass text glow is a kludge that produces a + cross
/// rather than a real soft glow.
///
/// DO NOT REUSE THESE FUNCTIONS BLINDLY in new UI work. If you reach for one
/// of them, you must:
///   1. Re-evaluate whether it actually achieves the intended look in your
///      context (test it in isolation first)
///   2. Be prepared to rewrite the implementation if the result is wrong
///   3. NOT assume that because SkillTreePanel uses them, they're "working"
///      -- SkillTreePanel ships with these as known-imperfect placeholders
///
/// What's most likely wrong (per-function notes are below):
/// - Gradients: discrete row/column stripes (no anti-aliasing) cause banding
/// - Radial gradient: 6-band horizontal split is far too coarse
/// - Outer glow: stacked 1px outlines are visibly stairstepped, not blurred
/// - Inset shadow: quadratic alpha falloff over discrete pixels is too sharp
/// - Repeating diagonal: pixel-by-pixel draw is slow and aliased
/// - Text emboss/glow: SpriteFont can't be alpha-blended properly with these
///   tricks because the font has its own anti-aliasing
///
/// If you need real CSS-quality effects, the right path is probably a custom
/// shader (.fx) that renders a quad with the effect baked in, not stacking
/// SpriteBatch primitives. Keep this file around as a reference for what was
/// tried and why it isn't enough.
/// </summary>
public static class UIGfx
{
    private static Color Lerp(Color a, Color b, float t)
    {
        t = MathHelper.Clamp(t, 0f, 1f);
        return new Color(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t),
            (byte)(a.A + (b.A - a.A) * t));
    }

    /// <summary>
    /// [UNVERIFIED] Vertical gradient: top color to bottom color, row by row.
    /// Known issue: discrete 1px rows produce visible banding on subtle gradients
    /// and on tall rects. Acceptable for short rects (≤40px) only.
    /// </summary>
    public static void FillVerticalGradient(SpriteBatch batch, Texture2D pixel,
        Rectangle r, Color top, Color bottom)
    {
        if (r.Height <= 0 || r.Width <= 0) return;
        float invH = 1f / Math.Max(1, r.Height - 1);
        for (int y = 0; y < r.Height; y++)
        {
            var c = Lerp(top, bottom, y * invH);
            batch.Draw(pixel, new Rectangle(r.X, r.Y + y, r.Width, 1), c);
        }
    }

    /// <summary>
    /// [UNVERIFIED] Three-stop vertical gradient (top -> mid at midPos -> bottom).
    /// Same banding issues as FillVerticalGradient. Stop position is correct in
    /// math but the visual is plain stripes, not a smooth metallic look.
    /// </summary>
    public static void FillVerticalGradient3(SpriteBatch batch, Texture2D pixel,
        Rectangle r, Color top, Color mid, Color bottom, float midPos = 0.5f)
    {
        if (r.Height <= 0 || r.Width <= 0) return;
        int splitY = (int)(r.Height * MathHelper.Clamp(midPos, 0f, 1f));
        for (int y = 0; y < r.Height; y++)
        {
            float t;
            Color c;
            if (y < splitY)
            {
                t = splitY <= 0 ? 0f : y / (float)splitY;
                c = Lerp(top, mid, t);
            }
            else
            {
                int span = r.Height - splitY;
                t = span <= 0 ? 0f : (y - splitY) / (float)span;
                c = Lerp(mid, bottom, t);
            }
            batch.Draw(pixel, new Rectangle(r.X, r.Y + y, r.Width, 1), c);
        }
    }

    /// <summary>
    /// [UNVERIFIED] Horizontal gradient: left color to right color, column by column.
    /// Same banding caveat as the vertical version. Was used for progress-bar
    /// fills (works OK there at 8px tall) but probably not what you want for
    /// anything larger.
    /// </summary>
    public static void FillHorizontalGradient(SpriteBatch batch, Texture2D pixel,
        Rectangle r, Color left, Color right)
    {
        if (r.Height <= 0 || r.Width <= 0) return;
        float invW = 1f / Math.Max(1, r.Width - 1);
        for (int x = 0; x < r.Width; x++)
        {
            var c = Lerp(left, right, x * invW);
            batch.Draw(pixel, new Rectangle(r.X + x, r.Y, 1, r.Height), c);
        }
    }

    /// <summary>
    /// [UNVERIFIED - BAD OUTPUT] Radial gradient: inner color at center, lerping
    /// out to outer color at outerRadius.
    /// Known issue: implementation splits each row into 6 horizontal bands and
    /// colors each band by its midpoint distance, which produces visible
    /// rectangular striping rather than a smooth radial fade. To do this
    /// properly you'd need either (a) per-pixel sampling (very slow) or (b)
    /// a fragment shader. Do not use for anything where the gradient quality
    /// matters.
    /// </summary>
    public static void FillRadialGradient(SpriteBatch batch, Texture2D pixel,
        Rectangle bounds, Vector2 center, float innerRadius, float outerRadius,
        Color inner, Color outer)
    {
        if (outerRadius <= innerRadius) outerRadius = innerRadius + 1;
        float invSpan = 1f / (outerRadius - innerRadius);
        // Draw row-by-row, splitting each row into a single span of the same
        // distance from center (computed by quantizing into bands).
        for (int y = 0; y < bounds.Height; y++)
        {
            int py = bounds.Y + y;
            float dy = py - center.Y;
            // We could draw pixel-by-pixel but that's 4x as many draw calls.
            // Compromise: split each row into 4 horizontal bands of equal width
            // and color each band by its midpoint distance.
            const int bands = 6;
            int bw = Math.Max(1, bounds.Width / bands);
            for (int b = 0; b < bands; b++)
            {
                int x0 = bounds.X + b * bw;
                int x1 = (b == bands - 1) ? bounds.Right : (x0 + bw);
                float midX = (x0 + x1) * 0.5f;
                float dx = midX - center.X;
                float dist = MathF.Sqrt(dx * dx + dy * dy);
                float t = (dist - innerRadius) * invSpan;
                var c = Lerp(inner, outer, t);
                batch.Draw(pixel, new Rectangle(x0, py, x1 - x0, 1), c);
            }
        }
    }

    /// <summary>
    /// [UNVERIFIED] Inset shadow: a soft inner border that fades to transparent.
    /// Mimics CSS `box-shadow: inset 0 0 size color`.
    /// Known issue: quadratic alpha falloff over discrete 1px-thick rectangles
    /// reads as a "darker border" rather than a soft shadow. The fade isn't
    /// gradual enough -- you can see the individual rings. The result tends
    /// to muddy the rect's interior near the edges instead of suggesting
    /// depth.
    /// </summary>
    public static void DrawInsetShadow(SpriteBatch batch, Texture2D pixel,
        Rectangle r, Color color, int size)
    {
        for (int i = 0; i < size; i++)
        {
            float t = 1f - (i / (float)size);
            byte a = (byte)(color.A * t * t); // quadratic falloff for softer edge
            var c = new Color(color.R, color.G, color.B, a);
            // Top
            batch.Draw(pixel, new Rectangle(r.X + i, r.Y + i, r.Width - i * 2, 1), c);
            // Bottom
            batch.Draw(pixel, new Rectangle(r.X + i, r.Bottom - 1 - i, r.Width - i * 2, 1), c);
            // Left
            batch.Draw(pixel, new Rectangle(r.X + i, r.Y + i + 1, 1, r.Height - i * 2 - 2), c);
            // Right
            batch.Draw(pixel, new Rectangle(r.Right - 1 - i, r.Y + i + 1, 1, r.Height - i * 2 - 2), c);
        }
    }

    /// <summary>
    /// [UNVERIFIED] Soft outer glow around a circle. Reproduces CSS
    /// `drop-shadow(0 0 N c)` applied to a circle.
    /// Known issue: paints N concentric 1px circle outlines, each at lower
    /// alpha. Result is visibly stair-stepped (you can count the rings) and
    /// nothing like a real Gaussian blur. Acceptable when blur ≤ 4 px and
    /// the glow color is similar to the background; otherwise distracting.
    /// </summary>
    public static void DrawCircleOuterGlow(SpriteBatch batch, Texture2D pixel,
        Vector2 center, float radius, Color color, int blur)
    {
        for (int i = 1; i <= blur; i++)
        {
            float t = 1f - (i / (float)blur);
            byte a = (byte)(color.A * t * t);
            var c = new Color(color.R, color.G, color.B, a);
            DrawUtils.DrawCircleOutline(batch, pixel, center, radius + i, c, 48);
        }
    }

    /// <summary>
    /// [UNVERIFIED - HALO ARTIFACTS] Soft outer glow around a rect.
    /// Approximates CSS `box-shadow: 0 0 N c` by stacking 1px frames.
    /// Known issue: on small or narrow rects (e.g. progress-bar fills) the
    /// stacked frames overlap into the surrounding area and produce visible
    /// halo blocks rather than a soft glow. Was actively removed from the
    /// progress-bar fills in SkillTreePanel because of this. Don't use on
    /// anything where the rect is narrower than ~3x the blur radius.
    /// </summary>
    public static void DrawRectOuterGlow(SpriteBatch batch, Texture2D pixel,
        Rectangle r, Color color, int blur)
    {
        for (int i = 1; i <= blur; i++)
        {
            float t = 1f - (i / (float)blur);
            byte a = (byte)(color.A * t * t);
            var c = new Color(color.R, color.G, color.B, a);
            // Top edge
            batch.Draw(pixel, new Rectangle(r.X - i, r.Y - i, r.Width + i * 2, 1), c);
            // Bottom edge
            batch.Draw(pixel, new Rectangle(r.X - i, r.Bottom + i - 1, r.Width + i * 2, 1), c);
            // Left edge
            batch.Draw(pixel, new Rectangle(r.X - i, r.Y - i + 1, 1, r.Height + i * 2 - 2), c);
            // Right edge
            batch.Draw(pixel, new Rectangle(r.Right + i - 1, r.Y - i + 1, 1, r.Height + i * 2 - 2), c);
        }
    }

    /// <summary>
    /// [UNVERIFIED - SLOW & NOISY] Repeating diagonal stripes. Tried to
    /// reproduce CSS `repeating-linear-gradient(45deg, transparent 0 N, color N N+1)`
    /// for leather cross-hatch.
    /// Known issues: (1) draws every pixel along every line as a separate
    /// 1x1 batch.Draw call which is very slow on large rects, (2) at the 45deg
    /// angle the result is visibly aliased -- doesn't read as "subtle leather
    /// texture", reads as "grid of dots". Drawing both directions for
    /// cross-hatch makes both problems worse. A real solution would be a
    /// pre-baked tiled texture.
    /// </summary>
    public static void DrawRepeatingDiagonal(SpriteBatch batch, Texture2D pixel,
        Rectangle r, Color color, int spacing, int direction)
    {
        if (spacing < 2) spacing = 2;
        // Number of diagonal lines that pass through the rect:
        int total = r.Width + r.Height;
        for (int o = -r.Height; o < r.Width + r.Height; o += spacing)
        {
            // A diagonal line parameterized by t in [0, len]; sweep through pixels.
            // Direction +1: y = x - o starting at (max(0,o), max(0,-o))
            // We'll just draw thin 1x1 squares along the line; cheap enough.
            int len = Math.Min(r.Width - Math.Max(0, o), r.Height - Math.Max(0, -o));
            if (len <= 0) continue;
            int sx = r.X + Math.Max(0, o);
            int sy = direction > 0 ? r.Y + Math.Max(0, -o) : r.Bottom - 1 - Math.Max(0, -o);
            for (int t = 0; t < len; t++)
            {
                int px = sx + t;
                int py = sy + (direction > 0 ? t : -t);
                if (py < r.Y || py >= r.Bottom) continue;
                batch.Draw(pixel, new Rectangle(px, py, 1, 1), color);
            }
        }
    }

    /// <summary>
    /// [UNVERIFIED] Repeating vertical tick marks for progress-bar divisions.
    /// This one is the simplest in the file (just N evenly-spaced 1px columns)
    /// and probably the only one that actually does what it claims. Still not
    /// independently tested outside the SkillTreePanel use case.
    /// </summary>
    public static void DrawRepeatingVerticalTicks(SpriteBatch batch, Texture2D pixel,
        Rectangle r, Color color, int divisions)
    {
        for (int t = 1; t < divisions; t++)
        {
            int tx = r.X + (r.Width * t / divisions);
            batch.Draw(pixel, new Rectangle(tx, r.Y, 1, r.Height), color);
        }
    }

    /// <summary>
    /// [UNVERIFIED - LOOKS LIKE TRIPLE-PRINT] Embossed text: highlight above,
    /// drop below, fg on top. Tried to reproduce a CSS multi-stop text-shadow.
    /// Known issue: SpriteFont glyphs already include their own anti-aliased
    /// edges. Drawing the same string three times at 1-2px offsets produces
    /// visible "ghost" outlines, not an emboss -- you can clearly see the
    /// three copies. The effect is only acceptable when the offsets are very
    /// small (1px) and alpha is low; otherwise it just looks like a
    /// printing error. Real emboss would need a glyph atlas with the effect
    /// pre-baked, or a shader that operates on the rendered glyph.
    /// </summary>
    public static void DrawTextEmbossed(SpriteBatch batch, SpriteFont font, string text,
        Vector2 pos, Color color, Color highlight, Color shadow)
    {
        // Highlight (1 pixel above)
        batch.DrawString(font, text, new Vector2(pos.X, pos.Y - 1), highlight);
        // Shadow (2 pixels below, slightly bigger feel)
        batch.DrawString(font, text, new Vector2(pos.X, pos.Y + 2), shadow);
        // Foreground
        batch.DrawString(font, text, pos, color);
    }

    /// <summary>
    /// [UNVERIFIED] Dropped text: dark drop shadow at offset, then foreground.
    /// Same caveat as DrawTextEmbossed -- the shadow is just a second copy of
    /// the glyph offset by (dx, dy), so at any noticeable offset the result
    /// reads as "two glyphs" not "glyph with shadow". Acceptable at dx=dy=1
    /// for legibility on busy backgrounds, but don't expect a proper soft
    /// drop shadow.
    /// </summary>
    public static void DrawTextShadow(SpriteBatch batch, SpriteFont font, string text,
        Vector2 pos, Color color, Color shadow, int dx = 1, int dy = 1)
    {
        batch.DrawString(font, text, new Vector2(pos.X + dx, pos.Y + dy), shadow);
        batch.DrawString(font, text, pos, color);
    }
}
