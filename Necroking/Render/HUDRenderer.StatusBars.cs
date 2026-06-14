using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Necroking.Render;

/// <summary>
/// HP/Mana bar skins for design review (cycle with Shift+H). Each reuses chrome
/// from the spell grimoire / unit sheet / tooltip UIs. Bar colour implies HP vs
/// Mana, so the text is just the value. Frames use real nine-slices so corners
/// don't distort. Skin 0 = the original flat bars; once a design is picked the
/// rest (and the cycling) get removed.
/// </summary>
public partial class HUDRenderer
{
    private static readonly Color HpFillA = new(216, 70, 56);   // HP highlight
    private static readonly Color HpFillB = new(146, 28, 22);   // HP body
    private static readonly Color ManaFillA = new(92, 116, 238); // Mana highlight
    private static readonly Color ManaFillB = new(40, 52, 150);  // Mana body
    private static readonly Color SkinTrack = new(14, 11, 8, 235);
    private static readonly Color SkinTextLight = new(247, 240, 222);
    private static readonly Color SkinShadow = new(0, 0, 0, 165);
    private const int ValueSize = 15;

    private static Rectangle Inset(Rectangle r, int n) => new(r.X + n, r.Y + n, r.Width - 2 * n, r.Height - 2 * n);
    private Rectangle FillR(Rectangle r, float frac) => new(r.X, r.Y, (int)(r.Width * MathHelper.Clamp(frac, 0f, 1f)), r.Height);
    private void Solid(Rectangle r, Color c) => _batch.Draw(_pixel, r, c);
    private void Elem(string id, Rectangle r, float inset = 0f) => _widgets?.DrawElementImage(id, r, inset);
    private void Ns(string nsId, Rectangle r, Color? tint = null) => _widgets?.DrawNineSlice(nsId, r, tint);
    // Thin the RenaiThinFrame's 10px borders down to ~4-5px so the gold frame
    // matches skin 4's slim gold trim instead of eating the bar height.
    private const float ThinFrame = 0.45f;
    private void NsThin(string nsId, Rectangle r) => _widgets?.DrawNineSlice(nsId, r, null, ThinFrame);

    /// <summary>Solid fill with a lighter top band (cheap gradient).</summary>
    private void GradFill(Rectangle inner, float frac, Color top, Color body)
    {
        var fr = FillR(inner, frac);
        if (fr.Width <= 0) return;
        _batch.Draw(_pixel, fr, body);
        _batch.Draw(_pixel, new Rectangle(fr.X, fr.Y, fr.Width, Math.Max(1, fr.Height * 2 / 5)), top);
    }

    /// <summary>Reveal a textured element as the fill, tinted toward the bar colour.</summary>
    private void ElemFill(string id, Rectangle inner, float frac, Color tint)
    {
        float f = MathHelper.Clamp(frac, 0f, 1f);
        if (f > 0f) _widgets?.DrawElementImageCropped(id, FillR(inner, f), 0f, f, tint);
    }

    /// <summary>Centered value text (scalable UI font, light with a shadow so it
    /// reads over both the dark fill and a light parchment track).</summary>
    private void BarValue(string text, Rectangle r, bool shadow = true)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (_widgets != null)
        {
            var sz = _widgets.MeasureText(text, ValueSize);
            int x = (int)(r.Center.X - sz.X / 2f), y = (int)(r.Center.Y - sz.Y / 2f);
            if (shadow) _widgets.DrawText(text, x + 1, y + 1, ValueSize, SkinShadow);
            _widgets.DrawText(text, x, y, ValueSize, SkinTextLight);
        }
        else if (_smallFont != null)
        {
            var sz = _smallFont.MeasureString(text);
            var pos = new Vector2((int)(r.Center.X - sz.X / 2f), (int)(r.Center.Y - sz.Y / 2f));
            if (shadow) Text(_smallFont, text, pos + new Vector2(1, 1), SkinShadow);
            Text(_smallFont, text, pos, SkinTextLight);
        }
    }

    private void GoldH(Rectangle r) => Elem("TT_Underline", r);
    private void GoldCap(int x, Rectangle bar) => Elem("ST_ColumnBar", new Rectangle(x, bar.Y, 3, bar.Height));

    /// <summary>Dispatch to the selected skin, drawing both the HP and Mana bars.</summary>
    private void DrawStatusBarSkin(int skin, bool hasHp,
        Rectangle hp, float hpFrac, string hpLabel, Rectangle mana, float manaFrac, string manaLabel)
    {
        Action<Rectangle, float, Color, Color, Color, string> bar = skin switch
        {
            1 => BarRenaiParchment,
            2 => BarParchmentPattern,
            3 => BarSwatchBanner,
            4 => BarParchmentGold,
            5 => BarSwathCaps,
            6 => BarClothDivider,
            7 => BarDragonTrim,
            8 => BarStatBox,
            9 => BarSegmented,
            10 => BarTexturedReveal,
            _ => BarOriginal,
        };
        var hpTint = new Color(255, 150, 130);
        var manaTint = new Color(150, 170, 255);
        if (hasHp) bar(hp, hpFrac, HpFillA, HpFillB, hpTint, hpLabel);
        bar(mana, manaFrac, ManaFillA, ManaFillB, manaTint, manaLabel);

        if (_smallFont != null && skin > 0)
            Text(_smallFont, $"Bar Style {skin}  (Shift+H)",
                new Vector2(BarX + 1, mana.Bottom + 3), new Color(170, 160, 140));
    }

    // 0 — original flat bars, value-only text.
    private void BarOriginal(Rectangle r, float f, Color a, Color b, Color tint, string s)
    {
        Solid(r, a == HpFillA ? HpBarBg : ManaBarBg);
        Solid(FillR(r, f), a == HpFillA ? HpBarFg : ManaBarFg);
        BarValue(s, r);
    }

    // 1 — Renai thin frame + grimoire parchment (refined #9).
    private void BarRenaiParchment(Rectangle r, float f, Color a, Color b, Color tint, string s)
    {
        Elem("SpellSlotBg", r, 0.16f);
        GradFill(Inset(r, 4), f, a, b);
        NsThin("RenaiThinBorder", r);
        BarValue(s, r);
    }

    // 2 — Parchment + nations pattern + Renai frame (refined #4).
    private void BarParchmentPattern(Rectangle r, float f, Color a, Color b, Color tint, string s)
    {
        Elem("SpellSlotBg", r, 0.16f);
        Elem("AbilitiesPattern", Inset(r, 3));
        GradFill(Inset(r, 4), f, a, b);
        NsThin("RenaiThinBorder", r);
        BarValue(s, r);
    }

    // 3 — Ornate Swatch1.3 banner (nine-slice) with a recessed fill.
    private void BarSwatchBanner(Rectangle r, float f, Color a, Color b, Color tint, string s)
    {
        Ns("SwatchBanner", r);
        var inner = Inset(r, 5);
        Solid(inner, SkinTrack);
        GradFill(inner, f, a, b);
        BarValue(s, r);
    }

    // 4 — Grimoire parchment with gold trim (lines + end caps, no stretched frame).
    private void BarParchmentGold(Rectangle r, float f, Color a, Color b, Color tint, string s)
    {
        Elem("SpellSlotBg", r, 0.16f);
        GradFill(Inset(r, 3), f, a, b);
        GoldH(new Rectangle(r.X, r.Y, r.Width, 3));
        GoldH(new Rectangle(r.X, r.Bottom - 3, r.Width, 3));
        GoldCap(r.X, r); GoldCap(r.Right - 3, r);
        BarValue(s, r);
    }

    // 5 — Unit-sheet blue swath row + gold end caps.
    private void BarSwathCaps(Rectangle r, float f, Color a, Color b, Color tint, string s)
    {
        Elem("ST_RowSwatch", r);
        GradFill(Inset(r, 2), f, a, b);
        GoldCap(r.X, r); GoldCap(r.Right - 3, r);
        BarValue(s, r);
    }

    // 6 — Grimoire dark cloth divider + gold underline (minimal).
    private void BarClothDivider(Rectangle r, float f, Color a, Color b, Color tint, string s)
    {
        Elem("Grim_HeaderDivider", r);
        GradFill(Inset(r, 3), f, a, b);
        GoldH(new Rectangle(r.X, r.Bottom - 3, r.Width, 3));
        BarValue(s, r);
    }

    // 7 — Dragon damask over the fill + gold trim top & bottom.
    private void BarDragonTrim(Rectangle r, float f, Color a, Color b, Color tint, string s)
    {
        Solid(r, SkinTrack);
        GradFill(r, f, a, b);
        Elem("UnitDescPattern", r);
        GoldH(new Rectangle(r.X, r.Y, r.Width, 3));
        GoldH(new Rectangle(r.X, r.Bottom - 3, r.Width, 3));
        BarValue(s, r);
    }

    // 8 — Blue stat-box container + Renai thin frame.
    private void BarStatBox(Rectangle r, float f, Color a, Color b, Color tint, string s)
    {
        Elem("ST_StatBox", r);
        GradFill(Inset(r, 4), f, a, b);
        NsThin("RenaiThinBorder", r);
        BarValue(s, r);
    }

    // 9 — Parchment with segmented (ticked) fill + Renai frame.
    private void BarSegmented(Rectangle r, float f, Color a, Color b, Color tint, string s)
    {
        Elem("SpellSlotBg", r, 0.16f);
        var inner = Inset(r, 4);
        GradFill(inner, f, a, b);
        for (int x = inner.X + 18; x < inner.Right - 2; x += 18)         // dark pips segment the fill
            Solid(new Rectangle(x, inner.Y, 1, inner.Height), new Color(0, 0, 0, 110));
        NsThin("RenaiThinBorder", r);
        BarValue(s, r);
    }

    // 10 — Textured parchment fill revealed left→right (tinted) + Renai frame.
    private void BarTexturedReveal(Rectangle r, float f, Color a, Color b, Color tint, string s)
    {
        var inner = Inset(r, 4);
        Solid(r, SkinTrack);
        Solid(FillR(inner, f), b);              // solid base guarantees a visible fill
        ElemFill("SpellSlotBg", inner, f, tint); // textured parchment over it
        NsThin("RenaiThinBorder", r);
        BarValue(s, r);
    }
}
