using System;
using Microsoft.Xna.Framework;

namespace Necroking.UI;

/// <summary>
/// Pure geometry + palette for the canonical thin vertical scrollbar — the single
/// source of truth for the look and thumb math shared by the editor detail panels
/// (<c>EditorBase.DrawVScrollbar</c>) and the main-menu scenario grid
/// (<c>GameRenderer.DrawScenarioList</c>). It only computes rects and maps drags to
/// scroll offsets; the caller owns input reading and the actual rect-fill draw
/// (the editor draws through <c>DrawRect</c>, the menu through GameRenderer's
/// SpriteBatch scope). Units are the caller's — px in the editor, layout-rows-times-
/// stride in the menu — the math is unitless as long as viewH/contentH/scroll agree.
/// </summary>
public static class VScrollbar
{
    // Canonical scrollbar palette — every scrollbar in the game uses these.
    public static readonly Color TrackColor = new(30, 30, 45, 120);
    public static readonly Color ThumbColor = new(100, 100, 140, 180);
    public static readonly Color ThumbHotColor = new(140, 140, 185, 220);
    public const int Width = 5;
    public const int MinThumbH = 20;

    /// <summary>True when the content fits the viewport (the bar draws nothing).</summary>
    public static bool Fits(int viewH, float contentH) => viewH <= 0 || contentH <= viewH;

    /// <summary>The full-height track column, 5px wide with its left edge at x.</summary>
    public static Rectangle TrackRect(int x, int y, int viewH) => new(x, y, Width, viewH);

    /// <summary>The proportional thumb for a given scroll offset. The ratio is
    /// clamped so an overscrolled value can't push the thumb past the track.</summary>
    public static Rectangle ThumbRect(int x, int y, int viewH, float contentH, float scroll)
    {
        int barH = ThumbH(viewH, contentH);
        float ratio = Math.Clamp(scroll / (contentH - viewH), 0f, 1f);
        int barY = y + (int)(ratio * (viewH - barH));
        return new Rectangle(x, barY, Width, barH);
    }

    /// <summary>Hit zone around the bar (5px + padding) so the thumb is easy to
    /// grab. Callers whose clickable rows extend under the bar column must exclude
    /// this rect from their own hit tests.</summary>
    public static Rectangle HitRect(int x, int y, int viewH) => new(x - 4, y, Width + 8, viewH);

    /// <summary>Map a dragged thumb (mouse Y minus the offset within the thumb where
    /// it was grabbed) back to a scroll offset clamped to [0, contentH - viewH].</summary>
    public static float ScrollFromDrag(int mouseY, float grabOffset, int y, int viewH, float contentH)
    {
        int travel = viewH - ThumbH(viewH, contentH);
        float frac = travel > 0 ? Math.Clamp((mouseY - grabOffset - y) / travel, 0f, 1f) : 0f;
        return frac * (contentH - viewH);
    }

    private static int ThumbH(int viewH, float contentH)
        => Math.Max(MinThumbH, (int)(viewH * (float)viewH / contentH));
}
