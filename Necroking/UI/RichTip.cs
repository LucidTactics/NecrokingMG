using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Render;

namespace Necroking.UI;

/// <summary>
/// Shared "rich" cursor tooltip: title + optional subtitle + wrapped description
/// + divider + right-aligned (label, value, color) rows, anchored at the cursor
/// with edge-flip placement. Owns the mechanics (word-wrap, placement, palette,
/// row/divider layout); callers own the <b>content</b> — which subtitle, which
/// description text, which rows to show.
///
/// The only structural variance is the text backend (RuntimeWidgetRenderer's
/// scalable widget font vs. a SpriteFont pair), captured by <see cref="IBackend"/>
/// — a small measure+draw adapter, not a framework.
///
/// NOTE: this is the RICH tooltip renderer. For SIMPLE plain-string tooltips see
/// <see cref="TooltipSystem"/>'s canonical box — deliberately separate; do not
/// merge the two. They DO share a scheduler: callers wrap their RichTip.Draw in
/// <c>Game1.Tooltips.RequestCustom(...)</c> so every tooltip, rich or simple,
/// is drawn by the same Tooltip-band host — topmost, never clipped or covered.
/// </summary>
public static class RichTip
{
    // === Canonical palette (declared once; was hand-duplicated across
    // InventoryUI / CraftingMenuUI / CharacterStatsUI tooltips). ===
    public static readonly Color Value = new(230, 230, 240);
    public static readonly Color Dim = new(150, 150, 165);
    public static readonly Color Green = new(120, 230, 120);
    public static readonly Color Red = new(230, 110, 110);
    public const int TitleSize = 18;
    public const int BodySize = 14;

    /// <summary>A right-aligned breakdown row: left label, right value, value color.</summary>
    public readonly record struct Row(string Label, string Value, Color Color);

    /// <summary>Box styling. Default matches the item/potion tooltips; the
    /// character sheet passes its own panel colors so its tooltip stays in step
    /// with the panel behind it.</summary>
    public readonly struct Palette
    {
        public readonly Color Bg, Border, Title, Desc, Label;
        public Palette(Color bg, Color border, Color title, Color desc, Color label)
        {
            Bg = bg; Border = border; Title = title; Desc = desc; Label = label;
        }
        public static Palette Default => new(
            new Color(20, 20, 32, 245), new Color(120, 120, 170, 240),
            new Color(255, 220, 140), new Color(200, 200, 215), new Color(150, 150, 165));
    }

    /// <summary>Measure+draw adapter over a concrete text backend.</summary>
    public interface IBackend
    {
        SpriteScope Scope { get; }
        Texture2D Pixel { get; }
        int LineH { get; }
        int MeasureTitleH(string title);
        float MeasureBodyW(string text);
        void DrawTitle(string text, int x, int y, Color c);
        void DrawBody(string text, int x, int y, Color c);
    }

    /// <summary>Widget-font backend (RuntimeWidgetRenderer, scalable font sizes).</summary>
    public readonly struct WidgetBackend : IBackend
    {
        private readonly RuntimeWidgetRenderer _r;
        private readonly int _titleSize, _bodySize;
        public SpriteScope Scope { get; }
        public Texture2D Pixel { get; }
        public WidgetBackend(RuntimeWidgetRenderer r, SpriteScope scope, Texture2D pixel,
            int titleSize = TitleSize, int bodySize = BodySize)
        {
            _r = r; Scope = scope; Pixel = pixel; _titleSize = titleSize; _bodySize = bodySize;
        }
        public int LineH => (int)MathF.Ceiling(_r.MeasureText("Ay", _bodySize).Y);
        public int MeasureTitleH(string title) => (int)MathF.Ceiling(_r.MeasureText(title, _titleSize).Y);
        public float MeasureBodyW(string text) => _r.MeasureText(text, _bodySize).X;
        public void DrawTitle(string text, int x, int y, Color c) => _r.DrawText(text, x, y, _titleSize, c);
        public void DrawBody(string text, int x, int y, Color c) => _r.DrawText(text, x, y, _bodySize, c);
    }

    /// <summary>SpriteFont backend (distinct title + body fonts).</summary>
    public readonly struct FontBackend : IBackend
    {
        private readonly SpriteFont _titleFont, _bodyFont;
        public SpriteScope Scope { get; }
        public Texture2D Pixel { get; }
        public FontBackend(SpriteFont titleFont, SpriteFont bodyFont, SpriteScope scope, Texture2D pixel)
        {
            _titleFont = titleFont; _bodyFont = bodyFont; Scope = scope; Pixel = pixel;
        }
        public int LineH => _bodyFont.LineSpacing;
        public int MeasureTitleH(string title) => (int)_titleFont.MeasureString(title).Y;
        public float MeasureBodyW(string text) => _bodyFont.MeasureString(text).X;
        public void DrawTitle(string text, int x, int y, Color c)
            => Scope.DrawString(_titleFont, text, new Vector2(x, y), c);
        public void DrawBody(string text, int x, int y, Color c)
            => Scope.DrawString(_bodyFont, text, new Vector2(x, y), c);
    }

    /// <summary>Cursor offset +16/+20, flipped when it would clip a screen edge,
    /// clamped 4px off the top-left. (The canonical placement duplicated 4x.)</summary>
    public static (int x, int y) Place(int mx, int my, int w, int h, int sw, int sh)
    {
        int x = mx + 16, y = my + 20;
        if (x + w > sw - 4) x = mx - w - 8;
        if (y + h > sh - 4) y = my - h - 8;
        return (Math.Max(4, x), Math.Max(4, y));
    }

    /// <summary>Greedy word-wrap to a pixel width using the supplied measure func.</summary>
    public static List<string> Wrap(Func<string, float> measure, string text, float maxW)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(text)) return result;
        var sb = new System.Text.StringBuilder();
        foreach (var word in text.Split(' '))
        {
            string trial = sb.Length == 0 ? word : sb + " " + word;
            if (sb.Length > 0 && measure(trial) > maxW)
            {
                result.Add(sb.ToString());
                sb.Clear();
                sb.Append(word);
            }
            else
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(word);
            }
        }
        if (sb.Length > 0) result.Add(sb.ToString());
        return result;
    }

    /// <summary>Compute the tooltip box height for the given content (same walk
    /// as <see cref="Draw"/>).</summary>
    private static int MeasureHeight(IBackend b, string title, string? subtitle,
        IReadOnlyList<string> descLines, IReadOnlyList<Row> rows,
        int pad, int gapAfterTitle, int gapBeforeDesc)
    {
        int lineH = b.LineH;
        int height = pad + b.MeasureTitleH(title) + gapAfterTitle;
        if (subtitle != null) height += lineH;
        height += gapBeforeDesc;
        height += descLines.Count * lineH;
        if (rows.Count > 0) height += 8 + rows.Count * lineH; // divider gap + rows
        height += pad;
        return height;
    }

    /// <summary>Draw a rich tooltip anchored at the cursor. Callers pass their
    /// own title/subtitle/description/rows; RichTip owns wrap, placement, palette
    /// and layout. <paramref name="gapAfterTitle"/>/<paramref name="gapBeforeDesc"/>
    /// preserve the item tooltip's subtitle spacing.</summary>
    public static void Draw(IBackend b, in Palette pal, string title, string? subtitle,
        IReadOnlyList<string> descLines, IReadOnlyList<Row> rows,
        int mx, int my, int sw, int sh,
        int tipW, int pad = 8, int gapAfterTitle = 4, int gapBeforeDesc = 0)
    {
        int innerW = tipW - pad * 2;
        int lineH = b.LineH;
        int titleH = b.MeasureTitleH(title);
        int height = MeasureHeight(b, title, subtitle, descLines, rows, pad, gapAfterTitle, gapBeforeDesc);

        var (tx, ty) = Place(mx, my, tipW, height, sw, sh);

        b.Scope.Draw(b.Pixel, new Rectangle(tx, ty, tipW, height), pal.Bg);
        DrawUtils.DrawRectBorder(b.Scope, b.Pixel, new Rectangle(tx, ty, tipW, height), pal.Border, 2);

        int cy = ty + pad;
        b.DrawTitle(title, tx + pad, cy, pal.Title);
        cy += titleH + gapAfterTitle;

        if (subtitle != null)
        {
            b.DrawBody(subtitle, tx + pad, cy, pal.Label);
            cy += lineH;
        }
        cy += gapBeforeDesc;

        foreach (var ln in descLines)
        {
            b.DrawBody(ln, tx + pad, cy, pal.Desc);
            cy += lineH;
        }

        if (rows.Count > 0)
        {
            cy += 3;
            b.Scope.Draw(b.Pixel, new Rectangle(tx + pad, cy, innerW, 1), pal.Border);
            cy += 5;
            foreach (var row in rows)
            {
                b.DrawBody(row.Label, tx + pad, cy, pal.Label);
                float vw = b.MeasureBodyW(row.Value);
                b.DrawBody(row.Value, (int)(tx + tipW - pad - vw), cy, row.Color);
                cy += lineH;
            }
        }
    }
}
