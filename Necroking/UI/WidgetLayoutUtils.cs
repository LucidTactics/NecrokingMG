using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Necroking.Editor;

namespace Necroking.UI;

/// <summary>
/// Shared widget layout computation used by both the editor and runtime renderer.
/// Matches C++ computeLayoutPositions: supports wrapping, per-side padding, spacing.
/// </summary>
public static class WidgetLayoutUtils
{
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
