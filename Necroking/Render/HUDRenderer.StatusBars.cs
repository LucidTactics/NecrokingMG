using System;
using Microsoft.Xna.Framework;

namespace Necroking.Render;

/// <summary>
/// The HP/Mana bars: a grimoire-parchment track with slim gold trim (lines +
/// end caps) and a coloured fill. Bar colour implies HP vs Mana, so the text is
/// just the value.
/// </summary>
public partial class HUDRenderer
{
    private static readonly Color HpFillA = new(216, 70, 56);    // HP highlight
    private static readonly Color HpFillB = new(146, 28, 22);    // HP body
    private static readonly Color ManaFillA = new(92, 116, 238); // Mana highlight
    private static readonly Color ManaFillB = new(40, 52, 150);  // Mana body
    private static readonly Color SkinTextLight = new(247, 240, 222);
    private static readonly Color SkinShadow = new(0, 0, 0, 165);
    private const int ValueSize = 15;

    private static Rectangle Inset(Rectangle r, int n) => new(r.X + n, r.Y + n, r.Width - 2 * n, r.Height - 2 * n);
    private Rectangle FillR(Rectangle r, float frac) => new(r.X, r.Y, (int)(r.Width * MathHelper.Clamp(frac, 0f, 1f)), r.Height);
    private void Elem(string id, Rectangle r, float inset = 0f) => _widgets?.DrawElementImage(id, r, inset);

    /// <summary>Solid fill with a lighter top band (cheap gradient).</summary>
    private void GradFill(Rectangle inner, float frac, Color top, Color body)
    {
        var fr = FillR(inner, frac);
        if (fr.Width <= 0) return;
        Scope.Draw(_pixel, fr, body);
        Scope.Draw(_pixel, new Rectangle(fr.X, fr.Y, fr.Width, Math.Max(1, fr.Height * 2 / 5)), top);
    }

    /// <summary>Centered value text (scalable UI font, light with a shadow so it
    /// reads over both the dark fill and the light parchment track).</summary>
    private void BarValue(string text, Rectangle r)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (_widgets != null)
        {
            var sz = _widgets.MeasureText(text, ValueSize);
            int x = (int)(r.Center.X - sz.X / 2f), y = (int)(r.Center.Y - sz.Y / 2f);
            _widgets.DrawText(text, x + 1, y + 1, ValueSize, SkinShadow);
            _widgets.DrawText(text, x, y, ValueSize, SkinTextLight);
        }
        else if (_smallFont != null)
        {
            var sz = _smallFont.MeasureString(text);
            var pos = new Vector2((int)(r.Center.X - sz.X / 2f), (int)(r.Center.Y - sz.Y / 2f));
            Text(_smallFont, text, pos + new Vector2(1, 1), SkinShadow);
            Text(_smallFont, text, pos, SkinTextLight);
        }
    }

    /// <summary>One HP/Mana bar: grimoire parchment track + gold trim + fill + value.</summary>
    private void DrawStatBar(Rectangle r, float frac, Color fillHi, Color fillLo, string value)
    {
        Elem("SpellSlotBg", r, 0.16f);                                  // parchment track
        GradFill(Inset(r, 3), frac, fillHi, fillLo);                    // coloured fill
        Elem("TT_Underline", new Rectangle(r.X, r.Y, r.Width, 3));      // gold trim, top
        Elem("TT_Underline", new Rectangle(r.X, r.Bottom - 3, r.Width, 3)); // gold trim, bottom
        Elem("ST_ColumnBar", new Rectangle(r.X, r.Y, 3, r.Height));     // gold end caps
        Elem("ST_ColumnBar", new Rectangle(r.Right - 3, r.Y, 3, r.Height));
        BarValue(value, r);
    }
}
