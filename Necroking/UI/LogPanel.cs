using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Core;

namespace Necroking.UI;

/// <summary>
/// Left-docked log viewer: a tabbed, scrollable text panel showing the combat
/// log, the error log, and a live game-stats readout (unit/object census, frame
/// timings, startup timings). Primitive-drawn panel following the GraveRosterUI
/// pattern. Combat/error tabs render DebugLog's in-memory tail — never re-read
/// from log/*.log — and stick to the bottom like a terminal until the user
/// scrolls up.
/// </summary>
public class LogPanel : IModalLayer
{
    private SpriteBatch _batch = null!;
    private Render.SpriteScope Scope => _batch;  // straight-alpha draw surface (implicit conversion)
    private Texture2D _pixel = null!;
    private RuntimeWidgetRenderer _r = null!;
    private Game1 _g = null!;

    private const int FsTitle = 16, FsTab = 13, FsLine = 11;
    private const int PanelW = 800;
    private const int Pad = 10;
    private const int TitleH = 26;
    private const int TabW = 90;
    private const int TabH = 24;
    private const int LineH = 15;

    private enum Tab { Combat, Errors, Stats }
    private static readonly string[] TabLabels = { "Combat", "Errors", "Stats" };
    private Tab _tab = Tab.Combat;

    private bool _visible;
    private int _x, _y, _w, _h;

    // Per-tab scroll offset (px) + whether the view is pinned to the newest
    // line. Combat/errors start pinned; stats reads top-down.
    private readonly float[] _scroll = new float[3];
    private readonly bool[] _stick = { true, true, false };
    private readonly int[] _seenVersion = { -1, -1, -1 };

    private readonly List<string> _lines = new();
    private int _phaseRowsMax;
    private bool _dragThumb;
    private float _grabOffset;

    // Palette — dark, low-key; small proportional font.
    private static readonly Color BgColor = new(13, 13, 17, 245);
    private static readonly Color EdgeColor = new(90, 85, 100, 200);
    private static readonly Color TitleColor = new(210, 205, 215);
    private static readonly Color LineColorNormal = new(190, 190, 198);
    private static readonly Color LineColorError = new(235, 140, 130);
    private static readonly Color LineColorHeader = new(150, 185, 225);
    private static readonly Color TabIdleBg = new(30, 29, 36, 235);
    private static readonly Color TabHoverBg = new(46, 43, 56, 240);
    private static readonly Color TabOpenBg = new(66, 60, 84, 245);
    private static readonly Color TabAccent = new(150, 135, 190, 220);

    public bool IsVisible => _visible;
    public bool IsDragging => _dragThumb;

    public void Init(SpriteBatch batch, Texture2D pixel, RuntimeWidgetRenderer renderer, Game1 g)
    {
        _batch = batch; _pixel = pixel; _r = renderer; _g = g;
    }

    public void Toggle(int screenW, int screenH)
    {
        if (_visible) Close();
        else { _visible = true; Layout(screenW, screenH); }
    }

    public void Close()
    {
        _visible = false;
        _dragThumb = false;
    }

    // ── Single-source layout, used by Update and Draw alike ──
    private void Layout(int screenW, int screenH)
    {
        _x = 0; _y = 0;
        _w = Math.Min(PanelW, screenW / 2);
        _h = screenH;
    }

    private Rectangle CloseRect => new(_x + _w - 28, _y + 5, 20, 18);
    private Rectangle TabRect(int i) => new(_x + Pad + i * (TabW + 4), _y + TitleH + 2, TabW, TabH);
    private Rectangle ViewRect => new(_x + Pad, _y + TitleH + 2 + TabH + 6,
        _w - Pad * 2, _h - (TitleH + 2 + TabH + 6) - Pad);
    private int ScrollbarX => ViewRect.Right - VScrollbar.Width;
    private float ContentH => _lines.Count * LineH;
    private float MaxScroll => Math.Max(0f, ContentH - ViewRect.Height);

    public void Update(InputState input, int screenW, int screenH)
    {
        if (!_visible) return;
        Layout(screenW, screenH);
        RefreshLines();

        int ti = (int)_tab;
        int mx = (int)input.MousePos.X, my = (int)input.MousePos.Y;
        var view = ViewRect;
        _scroll[ti] = Math.Clamp(_scroll[ti], 0f, MaxScroll);

        if (input.LeftPressed && CloseRect.Contains(mx, my)) { Close(); return; }

        if (input.LeftPressed)
        {
            for (int i = 0; i < TabLabels.Length; i++)
            {
                if (!TabRect(i).Contains(mx, my) || (Tab)i == _tab) continue;
                _tab = (Tab)i;
                _seenVersion[i] = -1; // force rebuild on switch
                RefreshLines();
                return;
            }
        }

        // Mouse wheel — unpin from the bottom when scrolling up.
        if (input.ScrollDelta != 0 && ContainsMouse(mx, my))
        {
            _scroll[ti] = Math.Clamp(_scroll[ti] + (input.ScrollDelta > 0 ? -3 : 3) * LineH, 0f, MaxScroll);
            _stick[ti] = _scroll[ti] >= MaxScroll - 0.5f;
        }

        // Scrollbar drag (press on thumb drags; press on track jumps there).
        if (input.LeftPressed && !VScrollbar.Fits(view.Height, ContentH)
            && VScrollbar.HitRect(ScrollbarX, view.Y, view.Height).Contains(mx, my))
        {
            var thumb = VScrollbar.ThumbRect(ScrollbarX, view.Y, view.Height, ContentH, _scroll[ti]);
            _grabOffset = thumb.Contains(mx, my) ? my - thumb.Y : thumb.Height / 2f;
            _dragThumb = true;
        }
        if (_dragThumb)
        {
            if (!input.LeftDown) _dragThumb = false;
            else
            {
                _scroll[ti] = VScrollbar.ScrollFromDrag(my, _grabOffset, view.Y, view.Height, ContentH);
                _stick[ti] = _scroll[ti] >= MaxScroll - 0.5f;
            }
        }
    }

    /// <summary>Rebuild _lines for the active tab. Combat/errors only re-copy
    /// when DebugLog's version bumped; stats rebuilds every frame (it's live).</summary>
    private void RefreshLines()
    {
        switch (_tab)
        {
            case Tab.Combat: RefreshTail("combat", (int)Tab.Combat); break;
            case Tab.Errors: RefreshTail("error", (int)Tab.Errors); break;
            case Tab.Stats: BuildStatsLines(); break;
        }
        int ti = (int)_tab;
        if (_stick[ti]) _scroll[ti] = MaxScroll;
    }

    private void RefreshTail(string tag, int ti)
    {
        int v = DebugLog.Version(tag);
        if (v == _seenVersion[ti]) return;
        _seenVersion[ti] = v;
        _lines.Clear();
        DebugLog.CopyRecent(tag, _lines);
    }

    private void BuildStatsLines()
    {
        _lines.Clear();

        _lines.Add("== Frame ==");
        float fps = _g._rawDt > 0 ? 1f / _g._rawDt : 0f;
        _lines.Add($"fps: {fps:F0}   frame: {_g._rawDt * 1000f:F1}ms   sim: {_g._sim.LastTickMs:F2}ms" +
                   $"   draw: {_g._drawMsAvg:F2}ms   ground: {_g._groundMsAvg:F2}ms   present: {_g._gpuPresentMsAvg:F2}ms");
        // Fixed row count in a fixed order — value-sorted/thresholded rows made
        // the whole view jump around every frame as timings fluctuated. Phases
        // register over time, so pad to the high-water mark with blank rows to
        // keep the sections below from shifting.
        int phaseRows = 0;
        foreach (var kv in _g._sim.LastPhaseMs.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            _lines.Add($"    {kv.Key}: {kv.Value:F2}");
            phaseRows++;
        }
        _phaseRowsMax = Math.Max(_phaseRowsMax, phaseRows);
        for (; phaseRows < _phaseRowsMax; phaseRows++) _lines.Add("");

        _lines.Add("");
        _lines.Add("== Census ==");
        foreach (var line in _g._session.Census().TrimEnd('\n').Split('\n'))
            _lines.Add(line);

        _lines.Add("");
        _lines.Add("== Startup ==");
        DebugLog.CopyRecent("startup", _lines);
    }

    public void Draw(int screenW, int screenH)
    {
        if (!_visible) return;
        Layout(screenW, screenH);
        int ti = (int)_tab;
        int mx = (int)_g._input.MousePos.X, my = (int)_g._input.MousePos.Y;

        var panel = new Rectangle(_x, _y, _w, _h);
        Scope.Draw(_pixel, panel, BgColor);
        Scope.Draw(_pixel, new Rectangle(_x + _w - 1, _y, 1, _h), EdgeColor);

        Text("Log", _x + Pad, _y + 5, FsTitle, TitleColor);
        DrawButton(CloseRect, "x", new Color(80, 50, 50, 230));

        for (int i = 0; i < TabLabels.Length; i++)
        {
            var tr = TabRect(i);
            bool open = i == ti;
            bool hover = tr.Contains(mx, my);
            Scope.Draw(_pixel, tr, open ? TabOpenBg : hover ? TabHoverBg : TabIdleBg);
            Scope.Draw(_pixel, new Rectangle(tr.X, tr.Y, tr.Width, 2),
                open ? TabAccent : new Color(60, 57, 70, 180));
            var sz = _r.MeasureText(TabLabels[i], FsTab);
            _r.DrawText(TabLabels[i], (int)(tr.X + (tr.Width - sz.X) / 2f),
                (int)(tr.Y + (tr.Height - sz.Y) / 2f), FsTab,
                open ? new Color(232, 228, 238) : new Color(178, 174, 186));
        }

        var view = ViewRect;
        float scroll = Math.Clamp(_scroll[ti], 0f, MaxScroll);
        bool hasBar = !VScrollbar.Fits(view.Height, ContentH);
        int textW = view.Width - (hasBar ? VScrollbar.Width + 8 : 0);

        int first = Math.Max(0, (int)(scroll / LineH));
        for (int i = first; i < _lines.Count; i++)
        {
            int ly = view.Y + (int)(i * LineH - scroll);
            if (ly + LineH > view.Bottom) break;
            if (ly < view.Y) continue;
            string line = _lines[i];
            if (line.Length == 0) continue;
            Text(FitLine(line, textW), view.X, ly, FsLine, LineColorFor(line));
        }
        if (_lines.Count == 0)
            Text("(empty)", view.X, view.Y, FsLine, new Color(130, 127, 138));

        if (hasBar)
        {
            Scope.Draw(_pixel, VScrollbar.TrackRect(ScrollbarX, view.Y, view.Height), VScrollbar.TrackColor);
            var thumb = VScrollbar.ThumbRect(ScrollbarX, view.Y, view.Height, ContentH, scroll);
            bool hot = _dragThumb || VScrollbar.HitRect(ScrollbarX, view.Y, view.Height).Contains(mx, my);
            Scope.Draw(_pixel, thumb, hot ? VScrollbar.ThumbHotColor : VScrollbar.ThumbColor);
        }
    }

    private Color LineColorFor(string line)
    {
        if (_tab == Tab.Errors) return LineColorError;
        if (line.StartsWith("== ")) return LineColorHeader;
        return LineColorNormal;
    }

    /// <summary>Truncate a line to the viewport width (proportional-font
    /// estimate — one measure, then a length-ratio cut; close enough for a log).</summary>
    private string FitLine(string s, int availW)
    {
        float w = _r.MeasureText(s, FsLine).X;
        if (w <= availW) return s;
        int keep = Math.Max(1, (int)(s.Length * availW / w) - 1);
        return s[..keep];
    }

    // IModalLayer
    public bool ContainsMouse(int mx, int my) => _visible && new Rectangle(_x, _y, _w, _h).Contains(mx, my);
    public Rectangle? HitBounds(int screenW, int screenH) => _visible ? new Rectangle(_x, _y, _w, _h) : null;
    public void OnCancel() => Close();
    public bool LightDismiss => false;
    public bool IsBlocking => false;

    // helpers
    private void DrawButton(Rectangle r, string label, Color fill)
    {
        Scope.Draw(_pixel, r, fill);
        Render.DrawUtils.DrawRectBorder(_batch, _pixel, r, new Color(206, 206, 214, 200), 1);
        var sz = _r.MeasureText(label, FsTab);
        _r.DrawText(label, (int)(r.X + (r.Width - sz.X) / 2f), (int)(r.Y + (r.Height - sz.Y) / 2f),
            FsTab, new Color(232, 232, 236));
    }

    private void Text(string s, int x, int y, int size, Color c) => _r.DrawText(s, x, y, size, c);
}
