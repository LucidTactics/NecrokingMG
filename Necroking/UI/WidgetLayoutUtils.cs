using System.Collections.Generic;
using System.Text;
using FontStashSharp;
using Microsoft.Xna.Framework;
using Necroking.Editor;

namespace Necroking.UI;

/// <summary>
/// Shared widget layout computation used by both the editor and runtime renderer.
/// Matches C++ computeLayoutPositions: supports wrapping, per-side padding, spacing.
/// </summary>
public static class WidgetLayoutUtils
{
    /// <summary>Greedy word-wrap to maxWidth using the font's measurements.
    /// Existing newlines are preserved as paragraph breaks. Shared by the
    /// editor preview and runtime renderer so wrapped text matches.</summary>
    public static string WrapText(FontStashSharp.SpriteFontBase font, string text, float maxWidth,
        float charSpacing = 0f)
    {
        if (maxWidth <= 0 || string.IsNullOrEmpty(text)) return text;
        var sb = new StringBuilder();
        var paragraphs = text.Split('\n');
        for (int p = 0; p < paragraphs.Length; p++)
        {
            if (p > 0) sb.Append('\n');
            string line = "";
            foreach (var word in paragraphs[p].Split(' '))
            {
                if (word.Length == 0) continue;
                string candidate = line.Length == 0 ? word : line + " " + word;
                float cw = font.MeasureString(candidate).X + charSpacing * (candidate.Length - 1);
                if (line.Length > 0 && cw > maxWidth)
                {
                    sb.Append(line).Append('\n');
                    line = word;
                }
                else
                {
                    line = candidate;
                }
            }
            sb.Append(line);
        }
        return sb.ToString();
    }

    /// <summary>Draw a (possibly multi-line) text block into rect with per-line
    /// horizontal alignment, block vertical alignment, and extra line spacing in
    /// px. Shared by the editor preview and runtime renderer. Positions are
    /// rounded to integer pixels (PointClamp text rule).</summary>
    public static void DrawTextBlock(Microsoft.Xna.Framework.Graphics.SpriteBatch batch,
        FontStashSharp.SpriteFontBase font, string text, Rectangle rect, Color color,
        string align, string valign, int lineSpacing, float charSpacing = 0f, bool bold = false,
        int outlineWidth = 0, Color outlineColor = default, float boldStrength = 1f,
        int outlineOffsetX = 0, int outlineOffsetY = 0)
    {
        var lines = text.Split('\n');
        // Exact per-line advance as DrawString uses internally (version-proof:
        // two-line measurement minus one-line measurement).
        float oneLine = font.MeasureString("Ay").Y;
        float lineH = font.MeasureString("Ay\nAy").Y - oneLine;
        float blockH = oneLine + (lines.Length - 1) * (lineH + lineSpacing);

        float y = valign switch
        {
            "center" => rect.Y + (rect.Height - blockH) / 2,
            "bottom" => rect.Y + rect.Height - blockH - 2,
            _ => rect.Y + 2
        };

        foreach (var line in lines)
        {
            if (line.Length > 0)
            {
                float lw = font.MeasureString(line).X + charSpacing * (line.Length - 1);
                float x = align switch
                {
                    "center" => rect.X + (rect.Width - lw) / 2,
                    "right" => rect.X + rect.Width - lw - 2,
                    _ => rect.X + 2
                };
                if (outlineWidth > 0)
                {
                    // TMP-style UNDERLAY: a dilated silhouette of the glyphs drawn
                    // BEHIND the face, optionally offset. A stroked draw tinted with
                    // the (dark) underlay color renders the whole silhouette dark
                    // (FontStash bakes the stroke black and the face takes the tint),
                    // dilated by effectAmount px. Exactly one pass — never bold-doubled
                    // (a second semi-transparent dark pass reads as mud).
                    // NOTE: FontStash anchors the stroked bitmap so the expansion
                    // lands down-right — offset by -amount to center the dilation.
                    batch.DrawString(font, line,
                        new Vector2((int)x + outlineOffsetX - outlineWidth, (int)y + outlineOffsetY - outlineWidth),
                        outlineColor, characterSpacing: charSpacing,
                        effect: FontSystemEffect.Stroked, effectAmount: outlineWidth);
                }
                batch.DrawString(font, line, new Vector2((int)x, (int)y), color,
                    characterSpacing: charSpacing);
                if (bold) // face weight only: +1px horizontal pass, boldStrength = opacity
                    batch.DrawString(font, line, new Vector2((int)x + 1, (int)y), color * boldStrength,
                        characterSpacing: charSpacing);
            }
            y += lineH + lineSpacing;
        }
    }

    public static List<Rectangle> ComputeLayoutRects(UIEditorWidgetDef def, int wdX, int wdY)
    {
        var rects = new List<Rectangle>();
        bool isHoriz = def.Layout == "horizontal";
        bool isVert = def.Layout == "vertical";
        bool useLayout = isHoriz || isVert;

        int padL = def.LayoutPadLeft > 0 ? def.LayoutPadLeft : def.LayoutPadding;
        int padR = def.LayoutPadRight > 0 ? def.LayoutPadRight : def.LayoutPadding;
        int padT = def.LayoutPadTop > 0 ? def.LayoutPadTop : def.LayoutPadding;
        int padB = def.LayoutPadBottom > 0 ? def.LayoutPadBottom : def.LayoutPadding;
        int spacX = def.LayoutSpacingX > 0 ? def.LayoutSpacingX : def.LayoutSpacing;
        int spacY = def.LayoutSpacingY > 0 ? def.LayoutSpacingY : def.LayoutSpacing;

        int curX = padL, curY = padT;
        int rowMaxH = 0, colMaxW = 0;

        for (int i = 0; i < def.Children.Count; i++)
        {
            var child = def.Children[i];
            int cw = child.Width > 0 ? child.Width : 100;
            int ch = child.Height > 0 ? child.Height : 40;

            if (useLayout && !child.IgnoreLayout)
            {
                if (isHoriz)
                {
                    if (curX > padL && curX + cw > def.Width - padR)
                    {
                        curY += rowMaxH + spacY;
                        curX = padL;
                        rowMaxH = 0;
                    }
                    rects.Add(new Rectangle(wdX + curX, wdY + curY, cw, ch));
                    curX += cw + spacX;
                    if (ch > rowMaxH) rowMaxH = ch;
                }
                else
                {
                    if (curY > padT && curY + ch > def.Height - padB)
                    {
                        curX += colMaxW + spacX;
                        curY = padT;
                        colMaxW = 0;
                    }
                    rects.Add(new Rectangle(wdX + curX, wdY + curY, cw, ch));
                    curY += ch + spacY;
                    if (cw > colMaxW) colMaxW = cw;
                }
            }
            else
            {
                int col = child.Anchor % 3, row = child.Anchor / 3;
                int anchorX = col switch { 0 => 0, 1 => def.Width / 2, 2 => def.Width, _ => 0 };
                int anchorY = row switch { 0 => 0, 1 => def.Height / 2, 2 => def.Height, _ => 0 };
                rects.Add(new Rectangle(wdX + anchorX + child.X, wdY + anchorY + child.Y, cw, ch));
            }
        }
        return rects;
    }

    /// <summary>
    /// Compute the minimum width / height a widget def needs to fit all of its
    /// laid-out children plus padding. Mirrors the single-axis cursor of
    /// ComputeLayoutRects (no wrapping — callers wanting auto-size should size
    /// the widget large enough that wrapping doesn't kick in).
    ///
    /// Returns (def.Width, def.Height) for non-layout widgets — anchor-positioned
    /// children can spill arbitrarily, so there's no meaningful "content size."
    /// </summary>
    public static (int Width, int Height) ComputeContentSize(UIEditorWidgetDef def)
    {
        bool isHoriz = def.Layout == "horizontal";
        bool isVert = def.Layout == "vertical";
        if (!isHoriz && !isVert) return (def.Width, def.Height);

        int padL = def.LayoutPadLeft > 0 ? def.LayoutPadLeft : def.LayoutPadding;
        int padR = def.LayoutPadRight > 0 ? def.LayoutPadRight : def.LayoutPadding;
        int padT = def.LayoutPadTop > 0 ? def.LayoutPadTop : def.LayoutPadding;
        int padB = def.LayoutPadBottom > 0 ? def.LayoutPadBottom : def.LayoutPadding;
        int spacX = def.LayoutSpacingX > 0 ? def.LayoutSpacingX : def.LayoutSpacing;
        int spacY = def.LayoutSpacingY > 0 ? def.LayoutSpacingY : def.LayoutSpacing;

        int sumMain = 0, maxCross = 0, layoutCount = 0;
        for (int i = 0; i < def.Children.Count; i++)
        {
            var child = def.Children[i];
            if (child.IgnoreLayout) continue;
            int cw = child.Width > 0 ? child.Width : 100;
            int ch = child.Height > 0 ? child.Height : 40;
            if (isHoriz) { sumMain += cw; if (ch > maxCross) maxCross = ch; }
            else         { sumMain += ch; if (cw > maxCross) maxCross = cw; }
            layoutCount++;
        }
        int spacing = layoutCount > 1 ? (layoutCount - 1) * (isHoriz ? spacX : spacY) : 0;
        int contentMain = sumMain + spacing;

        if (isHoriz) return (padL + contentMain + padR, padT + maxCross + padB);
        else         return (padL + maxCross + padR, padT + contentMain + padB);
    }
}
