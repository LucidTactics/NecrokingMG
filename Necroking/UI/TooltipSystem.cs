using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace Necroking.UI;

/// <summary>
/// Global tooltip service: a frame-scoped request queue drained by
/// <see cref="TooltipHostLayer"/> at <see cref="UIBand.Tooltip"/> — the topmost
/// band, after every other layer has drawn and every editor scissor clip has
/// closed. Callers anywhere (HUD, panels, editors — even inside a
/// <c>BeginClip</c> region) request a tooltip during their Update or Draw and
/// the box renders unclipped on top of everything. Requests never survive the
/// frame; the host clears the queue unconditionally.
///
/// Access via the static <see cref="Game1.Tooltips"/> (same lifetime rationale
/// as <see cref="Game1.Popups"/>).
///
/// Simple default paths: <see cref="RequestLines"/> (cursor-anchored) and
/// <see cref="RequestText"/> (rect-anchored), both in the one canonical style.
/// Escape hatch: <see cref="RequestCustom"/> defers an arbitrary draw callback
/// (RichTip boxes, widget tooltips, custom palettes) to the same topmost slot —
/// the callback owns its own rendering, this class only owns WHEN it draws.
///
/// Contracts: don't request from inside a custom callback (it would only be
/// appended and dropped); Update-time requesters must request at most once per
/// frame (fixed timestep can run several Updates per Draw). Cursor position is
/// snapshotted at request time from <c>_input.MousePos</c>, so the router's
/// cursor-parking (a covering layer parks the mouse off-screen) gates requests
/// for free: parked cursor ⇒ the caller's hover check fails ⇒ no request.
/// </summary>
public sealed class TooltipSystem
{
    private enum Kind { Lines, ColoredLines, Text, Custom }

    private struct Request
    {
        public Kind Kind;
        public string[]? Lines;
        public (string Text, Color Color)[]? ColoredLines;
        public string? Text;
        public Rectangle Anchor;      // rect anchor (Text) / captured cursor in X,Y (Lines)
        public Action<UICtx>? Custom;
    }

    // Canonical simple-tooltip style — the deliberate merge of the old
    // HUDRenderer box (bg + 2px top accent) and the map-editor grid box
    // (bg + 1px outline); they were near-identical.
    private static readonly Color BoxBg = new(15, 15, 25, 230);
    private static readonly Color BoxAccent = new(100, 100, 160);  // 2px top strip
    private static readonly Color BoxBorder = new(90, 90, 130);    // 1px outline
    private static readonly Color BoxText = new(220, 220, 240);
    private const int Pad = 4;
    private const int LineH = 16;

    private readonly Game1 _g;
    private readonly List<Request> _queue = new();

    public TooltipSystem(Game1 g) { _g = g; }

    /// <summary>Cursor-anchored multi-line tooltip in the canonical style.
    /// Snapshots <c>_input.MousePos</c> NOW, so call it from the hover check
    /// that read the same (possibly parked) cursor.</summary>
    public void RequestLines(params string[] lines)
    {
        if (lines == null || lines.Length == 0) return;
        _queue.Add(new Request
        {
            Kind = Kind.Lines,
            Lines = lines,
            Anchor = new Rectangle((int)_g._input.MousePos.X, (int)_g._input.MousePos.Y, 0, 0),
        });
    }

    /// <summary>Cursor-anchored multi-line tooltip with a color per line — the
    /// canonical box, used where lines carry state (e.g. a spell's mastery
    /// bonuses: reached = green, locked = grey).</summary>
    public void RequestLines(IReadOnlyList<(string Text, Color Color)> lines)
    {
        if (lines == null || lines.Count == 0) return;
        var copy = new (string, Color)[lines.Count];
        for (int i = 0; i < lines.Count; i++) copy[i] = lines[i];
        _queue.Add(new Request
        {
            Kind = Kind.ColoredLines,
            ColoredLines = copy,
            Anchor = new Rectangle((int)_g._input.MousePos.X, (int)_g._input.MousePos.Y, 0, 0),
        });
    }

    /// <summary>Rect-anchored one-line tooltip: centered above the anchor,
    /// flips below it when clipped at the top, clamped horizontally.</summary>
    public void RequestText(string text, Rectangle anchorRect)
    {
        if (string.IsNullOrEmpty(text)) return;
        _queue.Add(new Request { Kind = Kind.Text, Text = text, Anchor = anchorRect });
    }

    /// <summary>Escape hatch: defer an arbitrary draw to the tooltip slot. The
    /// callback draws into the already-open Hud-pass batch (same material as at
    /// request time) — capture your layout locals, don't re-read hover state.</summary>
    public void RequestCustom(Action<UICtx> draw)
    {
        if (draw == null) return;
        _queue.Add(new Request { Kind = Kind.Custom, Custom = draw });
    }

    /// <summary>Host-only: draw every queued request FIFO (later = on top),
    /// then clear. Index loop on purpose — a callback that (wrongly) requests
    /// mid-drain only appends, and the append is cleared below.</summary>
    internal void DrawAndClear(in UICtx ctx)
    {
        for (int i = 0; i < _queue.Count; i++)
        {
            var r = _queue[i];
            switch (r.Kind)
            {
                case Kind.Lines:
                    DrawLinesBox(r.Lines!, r.Anchor.X, r.Anchor.Y, ctx.ScreenW, ctx.ScreenH);
                    break;
                case Kind.ColoredLines:
                    DrawColoredLinesBox(r.ColoredLines!, r.Anchor.X, r.Anchor.Y, ctx.ScreenW, ctx.ScreenH);
                    break;
                case Kind.Text:
                    DrawTextBox(r.Text!, r.Anchor, ctx.ScreenW);
                    break;
                case Kind.Custom:
                    r.Custom!(ctx);
                    break;
            }
        }
        _queue.Clear();
    }

    /// <summary>Host-only: UI hidden this frame — drop the requests so nothing
    /// goes stale (no tooltips bleeding into no-UI screenshots).</summary>
    internal void Clear() => _queue.Clear();

    /// <summary>Above-right of the cursor; flips left / below at screen edges.
    /// (w,h) is the CONTENT size — the box pads around the returned origin.</summary>
    public static (int x, int y) PlaceAtCursor(int mx, int my, int w, int h, int screenW, int screenH)
    {
        int x = mx + 16, y = my - h - 12;
        if (x + w + 4 > screenW) x = mx - w - 12;
        if (x < 4) x = 4;
        if (y < 4) y = my + 20;
        return (x, y);
    }

    /// <summary>Centered above the anchor rect; flips below when clipped at the
    /// top; clamped horizontally. (w,h) is the full BOX size.</summary>
    public static (int x, int y) PlaceAboveRect(Rectangle anchor, int w, int h, int screenW)
    {
        int x = Math.Clamp(anchor.X + anchor.Width / 2 - w / 2, 2, Math.Max(2, screenW - w - 2));
        int y = anchor.Y - h - 2;
        if (y < 2) y = anchor.Bottom + 2;
        return (x, y);
    }

    private void DrawLinesBox(string[] lines, int mx, int my, int screenW, int screenH)
    {
        var font = _g._smallFont;
        if (font == null) return;
        float maxW = 0f;
        foreach (var l in lines)
        {
            float w = font.MeasureString(l).X;
            if (w > maxW) maxW = w;
        }
        int tw = (int)maxW, th = lines.Length * LineH;
        var (tx, ty) = PlaceAtCursor(mx, my, tw, th, screenW, screenH);
        DrawBoxChrome(new Rectangle(tx - Pad, ty - Pad, tw + Pad * 2, th + Pad * 2));
        for (int i = 0; i < lines.Length; i++)
            _g.Scope.DrawString(font, lines[i], new Vector2(tx, ty + i * LineH), BoxText);
    }

    private void DrawColoredLinesBox((string Text, Color Color)[] lines, int mx, int my,
        int screenW, int screenH)
    {
        var font = _g._smallFont;
        if (font == null) return;
        float maxW = 0f;
        foreach (var l in lines)
        {
            float w = font.MeasureString(l.Text).X;
            if (w > maxW) maxW = w;
        }
        int tw = (int)maxW, th = lines.Length * LineH;
        var (tx, ty) = PlaceAtCursor(mx, my, tw, th, screenW, screenH);
        DrawBoxChrome(new Rectangle(tx - Pad, ty - Pad, tw + Pad * 2, th + Pad * 2));
        for (int i = 0; i < lines.Length; i++)
            _g.Scope.DrawString(font, lines[i].Text, new Vector2(tx, ty + i * LineH), lines[i].Color);
    }

    private void DrawTextBox(string text, Rectangle anchor, int screenW)
    {
        var font = _g._smallFont;
        if (font == null) return;
        var size = font.MeasureString(text);
        int tw = (int)size.X + Pad * 2, th = (int)size.Y + Pad * 2;
        var (tx, ty) = PlaceAboveRect(anchor, tw, th, screenW);
        DrawBoxChrome(new Rectangle(tx, ty, tw, th));
        _g.Scope.DrawString(font, text, new Vector2(tx + Pad, ty + Pad), BoxText);
    }

    private void DrawBoxChrome(Rectangle box)
    {
        _g.Scope.Draw(_g._pixel, box, BoxBg);
        Render.DrawUtils.DrawRectBorder(_g.Scope, _g._pixel, box, BoxBorder);
        _g.Scope.Draw(_g._pixel, new Rectangle(box.X, box.Y, box.Width, 2), BoxAccent);
    }
}
