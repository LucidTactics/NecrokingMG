using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Necroking.Render;

/// <summary>
/// HP/Mana bar skins for design review (cycle with Shift+H). Each skin reuses
/// chrome elements from the spell grimoire / unit sheet / tooltip UIs (frames,
/// swaths, ribbons, parchment, gold trim) so the bars match that visual family.
/// Skin 0 is the original flat bars. Once a design is chosen the others (and the
/// cycling) get removed.
/// </summary>
public partial class HUDRenderer
{
    // Vivid fills so HP/Mana read instantly regardless of the chrome behind them.
    private static readonly Color HpFillA = new(214, 64, 52);   // HP top highlight
    private static readonly Color HpFillB = new(150, 30, 24);   // HP body
    private static readonly Color ManaFillA = new(86, 108, 234); // Mana top highlight
    private static readonly Color ManaFillB = new(42, 54, 156);  // Mana body
    private static readonly Color SkinTrack = new(16, 12, 9, 235);
    private static readonly Color SkinTextLight = new(246, 238, 218);
    private static readonly Color SkinTextDark = new(28, 20, 12);
    private static readonly Color SkinShadow = new(0, 0, 0, 140);

    private Rectangle FillR(Rectangle r, float frac)
        => new(r.X, r.Y, (int)(r.Width * MathHelper.Clamp(frac, 0f, 1f)), r.Height);

    private void Solid(Rectangle r, Color c) => _batch.Draw(_pixel, r, c);

    /// <summary>Solid fill with a lighter top band (cheap gradient).</summary>
    private void GradFill(Rectangle inner, float frac, Color top, Color body)
    {
        var fr = FillR(inner, frac);
        if (fr.Width <= 0) return;
        _batch.Draw(_pixel, fr, body);
        _batch.Draw(_pixel, new Rectangle(fr.X, fr.Y, fr.Width, Math.Max(1, fr.Height * 2 / 5)), top);
    }

    private void Elem(string id, Rectangle r, float inset = 0f) => _widgets?.DrawElementImage(id, r, inset);

    /// <summary>Reveal a textured element as the fill (tinted toward the bar color).</summary>
    private void ElemFill(string id, Rectangle inner, float frac, Color tint)
    {
        float f = MathHelper.Clamp(frac, 0f, 1f);
        if (f <= 0f) return;
        _widgets?.DrawElementImageCropped(id, FillR(inner, f), 0f, f, tint);
    }

    private void CenterLabel(SpriteFont? f, string s, Rectangle r, Color c, bool shadow = true)
    {
        if (f == null || string.IsNullOrEmpty(s)) return;
        var sz = f.MeasureString(s);
        var pos = new Vector2((int)(r.Center.X - sz.X / 2f), (int)(r.Center.Y - sz.Y / 2f));
        if (shadow) Text(f, s, pos + new Vector2(1, 1), SkinShadow);
        Text(f, s, pos, c);
    }

    private void LeftLabel(SpriteFont? f, string s, Rectangle r, Color c, bool shadow = true)
    {
        if (f == null || string.IsNullOrEmpty(s)) return;
        var sz = f.MeasureString(s);
        var pos = new Vector2(r.X + 6, (int)(r.Center.Y - sz.Y / 2f));
        if (shadow) Text(f, s, pos + new Vector2(1, 1), SkinShadow);
        Text(f, s, pos, c);
    }

    /// <summary>Dispatch to the selected skin, drawing both the HP and Mana bars.</summary>
    private void DrawStatusBarSkin(int skin, bool hasHp,
        Rectangle hp, float hpFrac, string hpLabel,
        Rectangle mana, float manaFrac, string manaLabel)
    {
        switch (skin)
        {
            case 1: Skin1(hasHp, hp, hpFrac, hpLabel, mana, manaFrac, manaLabel); break;
            case 2: Skin2(hasHp, hp, hpFrac, hpLabel, mana, manaFrac, manaLabel); break;
            case 3: Skin3(hasHp, hp, hpFrac, hpLabel, mana, manaFrac, manaLabel); break;
            case 4: Skin4(hasHp, hp, hpFrac, hpLabel, mana, manaFrac, manaLabel); break;
            case 5: Skin5(hasHp, hp, hpFrac, hpLabel, mana, manaFrac, manaLabel); break;
            case 6: Skin6(hasHp, hp, hpFrac, hpLabel, mana, manaFrac, manaLabel); break;
            case 7: Skin7(hasHp, hp, hpFrac, hpLabel, mana, manaFrac, manaLabel); break;
            case 8: Skin8(hasHp, hp, hpFrac, hpLabel, mana, manaFrac, manaLabel); break;
            case 9: Skin9(hasHp, hp, hpFrac, hpLabel, mana, manaFrac, manaLabel); break;
            case 10: Skin10(hasHp, hp, hpFrac, hpLabel, mana, manaFrac, manaLabel); break;
            default: Skin0(hasHp, hp, hpFrac, hpLabel, mana, manaFrac, manaLabel); break;
        }
        // Small "Bar Style N/10" tag under the bars so it's clear which is showing.
        if (_smallFont != null && skin > 0)
            Text(_smallFont, $"Bar Style {skin}/10  (Shift+H)",
                new Vector2(BarX + 1, mana.Bottom + 3), new Color(170, 160, 140));
    }

    // 0 — original flat bars (unchanged look).
    private void Skin0(bool hasHp, Rectangle hp, float hpF, string hpL, Rectangle mn, float mnF, string mnL)
    {
        if (hasHp) { Solid(hp, HpBarBg); Solid(FillR(hp, hpF), HpBarFg); LeftLabel(_font, hpL, hp, Color.White, false); }
        Solid(mn, ManaBarBg); Solid(FillR(mn, mnF), ManaBarFg); LeftLabel(_font, mnL, mn, Color.White, false);
    }

    // 1 — Unit-sheet blue swath rows (BlueSwath_row) with a clean colored fill.
    private void Skin1(bool hasHp, Rectangle hp, float hpF, string hpL, Rectangle mn, float mnF, string mnL)
    {
        void Bar(Rectangle r, float f, Color a, Color b, string s)
        {
            Solid(r, SkinTrack);
            Elem("ST_RowSwatch", r);                 // blue swath as the empty track
            GradFill(new Rectangle(r.X + 1, r.Y + 1, r.Width - 2, r.Height - 2), f, a, b);
            CenterLabel(_font, s, r, SkinTextLight);
        }
        if (hasHp) Bar(hp, hpF, HpFillA, HpFillB, hpL);
        Bar(mn, mnF, ManaFillA, ManaFillB, mnL);
    }

    // 2 — Grimoire dark cloth divider track + gold underline.
    private void Skin2(bool hasHp, Rectangle hp, float hpF, string hpL, Rectangle mn, float mnF, string mnL)
    {
        void Bar(Rectangle r, float f, Color a, Color b, string s)
        {
            Elem("Grim_HeaderDivider", r);
            GradFill(new Rectangle(r.X + 2, r.Y + 2, r.Width - 4, r.Height - 4), f, a, b);
            Elem("TT_Underline", new Rectangle(r.X, r.Bottom - 3, r.Width, 3));
            LeftLabel(_font, s, r, SkinTextLight);
        }
        if (hasHp) Bar(hp, hpF, HpFillA, HpFillB, hpL);
        Bar(mn, mnF, ManaFillA, ManaFillB, mnL);
    }

    // 3 — Ornate title ribbon (Ribbon6) framing a recessed fill.
    private void Skin3(bool hasHp, Rectangle hp, float hpF, string hpL, Rectangle mn, float mnF, string mnL)
    {
        void Bar(Rectangle r, float f, Color a, Color b, string s)
        {
            var ribbon = new Rectangle(r.X - 3, r.Y - 4, r.Width + 6, r.Height + 8);
            var inner = new Rectangle(r.X + 16, r.Y + 2, r.Width - 32, r.Height - 4);
            Elem("Grim_TitleRibbon", ribbon);        // ornate ribbon first (its centre is opaque)
            Solid(inner, SkinTrack);                 // recessed slot in the ribbon's flat middle
            GradFill(inner, f, a, b);                // fill sits on top so the level reads
            CenterLabel(_font, s, inner, SkinTextLight);
        }
        if (hasHp) Bar(hp, hpF, HpFillA, HpFillB, hpL);
        Bar(mn, mnF, ManaFillA, ManaFillB, mnL);
    }

    // 4 — Tooltip parchment (FancyFrame2_Inner) + nations pattern, dark text.
    private void Skin4(bool hasHp, Rectangle hp, float hpF, string hpL, Rectangle mn, float mnF, string mnL)
    {
        void Bar(Rectangle r, float f, Color a, Color b, string s)
        {
            Elem("SpellSlotBg", r, 0.16f);
            Elem("AbilitiesPattern", r);
            GradFill(new Rectangle(r.X + 2, r.Y + 2, r.Width - 4, r.Height - 4), f, a, b);
            Elem("Grim_SchoolTab_All_Frame", r);
            CenterLabel(_font, s, r, SkinTextLight);
        }
        if (hasHp) Bar(hp, hpF, HpFillA, HpFillB, hpL);
        Bar(mn, mnF, ManaFillA, ManaFillB, mnL);
    }

    // 5 — Stat-box container (BlueSwath_statbox) with gold end caps.
    private void Skin5(bool hasHp, Rectangle hp, float hpF, string hpL, Rectangle mn, float mnF, string mnL)
    {
        void Bar(Rectangle r, float f, Color a, Color b, string s)
        {
            Solid(r, SkinTrack);
            Elem("ST_StatBox", r);
            GradFill(new Rectangle(r.X + 3, r.Y + 2, r.Width - 6, r.Height - 4), f, a, b);
            Elem("ST_ColumnBar", new Rectangle(r.X, r.Y, 3, r.Height));
            Elem("ST_ColumnBar", new Rectangle(r.Right - 3, r.Y, 3, r.Height));
            CenterLabel(_font, s, r, SkinTextLight);
        }
        if (hasHp) Bar(hp, hpF, HpFillA, HpFillB, hpL);
        Bar(mn, mnF, ManaFillA, ManaFillB, mnL);
    }

    // 6 — Abilities swath row with a heraldry strip header.
    private void Skin6(bool hasHp, Rectangle hp, float hpF, string hpL, Rectangle mn, float mnF, string mnL)
    {
        void Bar(Rectangle r, float f, Color a, Color b, string s)
        {
            Elem("AbilitiesBox", r);
            GradFill(new Rectangle(r.X + 2, r.Y + 3, r.Width - 4, r.Height - 5), f, a, b);
            Elem("AbilitiesTitleHeraldry", new Rectangle(r.X, r.Y, r.Width, 4));
            LeftLabel(_font, s, r, SkinTextLight);
        }
        if (hasHp) Bar(hp, hpF, HpFillA, HpFillB, hpL);
        Bar(mn, mnF, ManaFillA, ManaFillB, mnL);
    }

    // 7 — Textured reveal fill: a parchment swatch is revealed left→right, tinted.
    private void Skin7(bool hasHp, Rectangle hp, float hpF, string hpL, Rectangle mn, float mnF, string mnL)
    {
        void Bar(Rectangle r, float f, Color tint, Color baseC, string s)
        {
            Solid(r, SkinTrack);
            Solid(FillR(r, f), baseC);               // solid base guarantees a visible fill
            ElemFill("SpellSlotBg", r, f, tint);     // textured parchment over it
            Elem("Grim_SchoolTab_All_Frame", r);
            CenterLabel(_font, s, r, SkinTextLight);
        }
        if (hasHp) Bar(hp, hpF, new Color(255, 150, 130), HpFillB, hpL);
        Bar(mn, mnF, new Color(150, 170, 255), ManaFillB, mnL);
    }

    // 8 — Dragon pattern field with gold trim top & bottom.
    private void Skin8(bool hasHp, Rectangle hp, float hpF, string hpL, Rectangle mn, float mnF, string mnL)
    {
        void Bar(Rectangle r, float f, Color a, Color b, string s)
        {
            Solid(r, SkinTrack);
            GradFill(r, f, a, b);
            Elem("UnitDescPattern", r);              // dragon damask over the fill
            Elem("TT_Underline", new Rectangle(r.X, r.Y, r.Width, 3));
            Elem("TT_Underline", new Rectangle(r.X, r.Bottom - 3, r.Width, 3));
            CenterLabel(_font, s, r, SkinTextLight);
        }
        if (hasHp) Bar(hp, hpF, HpFillA, HpFillB, hpL);
        Bar(mn, mnF, ManaFillA, ManaFillB, mnL);
    }

    // 9 — Renai tooltip frame + parchment backing.
    private void Skin9(bool hasHp, Rectangle hp, float hpF, string hpL, Rectangle mn, float mnF, string mnL)
    {
        void Bar(Rectangle r, float f, Color a, Color b, string s)
        {
            Elem("RT_Parchment", r);
            GradFill(new Rectangle(r.X + 3, r.Y + 3, r.Width - 6, r.Height - 6), f, a, b);
            Elem("RT_BoxFrame", r);
            CenterLabel(_font, s, r, SkinTextDark, shadow: false);
        }
        if (hasHp) Bar(hp, hpF, HpFillA, HpFillB, hpL);
        Bar(mn, mnF, ManaFillA, ManaFillB, mnL);
    }

    // 10 — Swatch1.3 title ribbon with a recessed inner fill.
    private void Skin10(bool hasHp, Rectangle hp, float hpF, string hpL, Rectangle mn, float mnF, string mnL)
    {
        void Bar(Rectangle r, float f, Color a, Color b, string s)
        {
            Elem("UnitTitleSwatch", r);
            var inner = new Rectangle(r.X + 5, r.Y + 4, r.Width - 10, r.Height - 8);
            Solid(inner, SkinTrack);
            GradFill(inner, f, a, b);
            CenterLabel(_font, s, r, SkinTextLight);
        }
        if (hasHp) Bar(hp, hpF, HpFillA, HpFillB, hpL);
        Bar(mn, mnF, ManaFillA, ManaFillB, mnL);
    }
}
