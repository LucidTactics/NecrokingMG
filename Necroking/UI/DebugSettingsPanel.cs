using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Core;
using Necroking.Lib;
using Necroking.Render;

namespace Necroking.UI;

/// <summary>
/// Left-side debug settings panel: a dropdown per debug-mode F-key (F2 Water,
/// F3 Perf, F5 Death Fog, F6 Wind, F7 Gameplay, F8 Collision). Docks under the
/// top-left player-data (HP/Mana) status bars and mirrors the exact same state
/// the F-keys toggle — the F-key block in Game1.Update stays the source of
/// truth for each field, this is the click-driven twin.
///
/// Self-contained (like LogPanel): owns its visibility, layout, draw and input.
/// The thin <see cref="DebugSettingsPanelLayer"/> just routes into it (presses
/// via HandlePress, releases/outside-presses polled via HandleFrame). Dropdowns
/// use the eager press→drag→release gesture (<see cref="EagerDropdown"/>).
/// Toggled by the top-right "Debug" editor-row button. Off by default (opt-in
/// dev tool).
/// </summary>
public class DebugSettingsPanel
{
    private Game1 _g = null!;
    private SpriteScope Scope => _g.Scope;

    // One debug mode the panel exposes: a label, its option names, and get/set
    // hooks that read/write the live Game1 field (Direct over Inject).
    private sealed class DebugModeToggle
    {
        public string Label = "";
        public string[] Options = Array.Empty<string>();
        public Func<int> Get = () => 0;
        public Action<int> Set = _ => { };
    }

    private List<DebugModeToggle>? _toggles;
    private List<DebugModeToggle> Toggles => _toggles ??= BuildToggles();

    // Eager press→drag→release dropdown state; OpenKey = expanded row (-1 = none).
    private readonly EagerDropdown _dd = new();

    private bool _visible;
    public bool IsVisible => _visible;

    public void Init(Game1 g) => _g = g;

    public void Toggle() { _visible = !_visible; if (!_visible) _dd.Close(); }
    public void Close() { _visible = false; _dd.Close(); }

    private List<DebugModeToggle> BuildToggles()
    {
        // On/Off options for the plain boolean toggles.
        string[] onOff = { "Off", "On" };

        // Collision-mode option names, driven off the enum (minus the Count
        // sentinel) so the panel never drifts from the F8 cycle.
        int collCount = (int)CollisionDebugMode.Count;
        var collOpts = new string[collCount];
        for (int i = 0; i < collCount; i++)
            collOpts[i] = DebugDraw.GetModeLabel((CollisionDebugMode)i);

        return new List<DebugModeToggle>
        {
            new() { Label = "F2 Water",     Options = onOff, Get = () => _g._waterDebug ? 1 : 0,
                    Set = v => _g._waterDebug = v == 1 },
            new() { Label = "F3 Perf",      Options = onOff, Get = () => _g._showPerfReadout ? 1 : 0,
                    Set = v => _g._showPerfReadout = v == 1 },
            new() { Label = "F5 Death Fog", Options = onOff, Get = () => _g._deathFog.DebugVisible ? 1 : 0,
                    // DeathFogSystem only exposes a toggle — flip only when the
                    // chosen state differs from the current one.
                    Set = v => { if ((v == 1) != _g._deathFog.DebugVisible) _g._deathFog.ToggleDebug(); } },
            new() { Label = "F6 Wind",      Options = onOff, Get = () => _g._windDebug ? 1 : 0,
                    Set = v => _g._windDebug = v == 1 },
            new() { Label = "F7 Gameplay",  Options = new[] { "Off", "Horde", "Unit Info" },
                    Get = () => _g._gameplayDebugMode,
                    Set = v => _g._gameplayDebugMode = v },
            new() { Label = "F8 Collision", Options = collOpts,
                    Get = () => (int)_g._collisionDebugMode,
                    Set = v => _g._collisionDebugMode = (CollisionDebugMode)v },
            new() { Label = "UI Debug", Options = new[] { "Off", "DrawRegions" },
                    Get = () => _g._uiDebugDrawMode,
                    Set = v => _g._uiDebugDrawMode = v },
        };
    }

    // ── Layout ──────────────────────────────────────────────────────────────
    // Fixed top-left geometry, shared by draw + hit-test so they never desync.
    private const int PanX = 10;      // matches HUDRenderer.BarX
    private const int PanW = 200;     // matches HUDRenderer.BarWidth
    private const int PanY = 88;      // just under the HP/Mana bars (bottom ≈ 82)
    private const int HeaderH = 18;
    private const int RowH = 22;
    private const int Pad = 6;
    private const int BoxW = 108;     // dropdown box on the right of each row
    private const int OptH = 18;

    /// <summary>Panel footprint (excludes any open dropdown list). Exposed so the
    /// router layer can register it in <c>_uiHits</c> (ui_rects + UI-debug overlay).</summary>
    public Rectangle Bounds => PanelRect();

    /// <summary>Panel rect. Height grows with the number of debug toggles.</summary>
    private Rectangle PanelRect()
    {
        int h = HeaderH + Pad + Toggles.Count * RowH + Pad;
        return new Rectangle(PanX, PanY, PanW, h);
    }

    /// <summary>Clickable dropdown box for row <paramref name="i"/>.</summary>
    private Rectangle BoxRect(int i)
    {
        int rowY = PanY + HeaderH + Pad + i * RowH;
        return new Rectangle(PanX + PanW - Pad - BoxW, rowY + 1, BoxW, RowH - 4);
    }

    /// <summary>Rect of option <paramref name="opt"/> in row <paramref name="i"/>'s
    /// open dropdown list (drawn below the box).</summary>
    private Rectangle OptionRect(int i, int opt)
    {
        var box = BoxRect(i);
        return new Rectangle(box.X, box.Bottom + 1 + opt * OptH, box.Width, OptH);
    }

    // ── Input (called by DebugSettingsPanelLayer) ───────────────────────────

    /// <summary>True if the cursor is over the panel or an open dropdown list —
    /// the layer's ContainsMouse.</summary>
    public bool ContainsMouse(int mx, int my)
    {
        if (!_visible) return false;
        if (PanelRect().Contains(mx, my)) return true;
        return HitItem(mx, my) >= 0;
    }

    /// <summary>Row index whose dropdown box contains the cursor, or -1.</summary>
    private int HitBox(int mx, int my)
    {
        for (int i = 0; i < Toggles.Count; i++)
            if (BoxRect(i).Contains(mx, my)) return i;
        return -1;
    }

    /// <summary>Option index under the cursor in the OPEN dropdown list, or -1.</summary>
    private int HitItem(int mx, int my)
    {
        if (_dd.OpenKey < 0) return -1;
        var t = Toggles[_dd.OpenKey];
        for (int o = 0; o < t.Options.Length; o++)
            if (OptionRect(_dd.OpenKey, o).Contains(mx, my)) return o;
        return -1;
    }

    /// <summary>A left press granted to the panel (layer's OnPointer). Opens /
    /// arms / marks-for-close the eager dropdown; selection fires on release in
    /// <see cref="HandleFrame"/>.</summary>
    public void HandlePress(int mx, int my)
        => _dd.OnPress(HitBox(mx, my), HitItem(mx, my));

    /// <summary>Per-frame input poll (layer's OnFrame). Releases are never
    /// routed by the UI router and the layer only receives presses while
    /// hovered, so both the release-select of the eager gesture and
    /// dismiss-on-outside-press live here. Uses the real cursor, never Draw's
    /// parked one.</summary>
    public void HandleFrame(InputState input)
    {
        if (!_visible || !_dd.IsOpen) return;
        int mx = (int)input.MousePos.X, my = (int)input.MousePos.Y;

        if (input.LeftPressed && !ContainsMouse(mx, my))
        {
            _dd.OnPressOutside();
            return;
        }

        if (input.LeftReleased)
        {
            int key = _dd.OpenKey; // snapshot: a selection closes the list
            bool gestureValid = input.PressStartPos.X >= 0;
            int sel = _dd.OnRelease(HitItem(mx, my), gestureValid);
            if (sel >= 0) Toggles[key].Set(sel);
        }
    }

    // ── Draw ────────────────────────────────────────────────────────────────

    private static readonly Color PanelBg    = new(10, 12, 16, 210);
    private static readonly Color PanelAccent = new(80, 200, 120, 220);
    private static readonly Color HeaderCol   = new(120, 230, 160);
    private static readonly Color LabelCol    = new(180, 190, 200);
    private static readonly Color BoxBg       = new(28, 32, 40, 235);
    private static readonly Color BoxHover    = new(46, 54, 66, 240);
    private static readonly Color BoxBorder   = new(90, 110, 130);
    private static readonly Color ValActive   = new(230, 240, 200);
    private static readonly Color ValOff      = new(140, 150, 160);
    private static readonly Color OptBg       = new(20, 24, 30, 245);
    private static readonly Color OptHover    = new(60, 90, 70, 250);
    private static readonly Color OptSelBg    = new(32, 46, 38, 245);

    /// <summary>Draw the panel. Runs inside the open HUD batch.
    /// <paramref name="mx"/>/<paramref name="my"/> are the cursor (parked
    /// off-screen by the layer when it doesn't own the hover).</summary>
    public void Draw(int mx, int my)
    {
        if (!_visible) return;
        var f = _g._smallFont;
        if (f == null) return;

        var toggles = Toggles;
        var panel = PanelRect();
        DrawPanel(panel, PanelBg, PanelAccent);
        Text(f, "DEBUG", panel.X + Pad, panel.Y + 3, HeaderCol);

        for (int i = 0; i < toggles.Count; i++)
        {
            var t = toggles[i];
            int cur = Math.Clamp(t.Get(), 0, t.Options.Length - 1);
            int rowY = PanY + HeaderH + Pad + i * RowH;

            Text(f, t.Label, panel.X + Pad, rowY + 4, LabelCol);

            var box = BoxRect(i);
            bool hover = box.Contains(mx, my);
            DrawPanel(box, hover ? BoxHover : BoxBg, BoxBorder, 1, bottomAccent: true);
            DrawUtils.DrawRectBorder(_g._spriteBatch, _g._pixel, box, BoxBorder);

            Text(f, t.Options[cur], box.X + 5, box.Y + 2, cur == 0 ? ValOff : ValActive);
            // Caret on the right edge of the box — flips when the list is open.
            Text(f, _dd.OpenKey == i ? "^" : "v", box.Right - 12, box.Y + 2, BoxBorder);
        }

        // Open dropdown list, drawn last so it sits over rows below it.
        if (_dd.OpenKey >= 0)
        {
            var t = toggles[_dd.OpenKey];
            int cur = Math.Clamp(t.Get(), 0, t.Options.Length - 1);
            for (int o = 0; o < t.Options.Length; o++)
            {
                var r = OptionRect(_dd.OpenKey, o);
                bool hover = r.Contains(mx, my);
                bool sel = o == cur;
                Scope.Draw(_g._pixel, r, hover ? OptHover : sel ? OptSelBg : OptBg);
                // Left-gutter checkmark on the current value so it reads at a
                // glance; all options indent past the gutter to stay aligned.
                if (sel)
                    DrawUtils.DrawCheckmark(Scope, _g._pixel,
                        new Rectangle(r.X + 4, r.Y + 4, 9, 9), HeaderCol);
                Text(f, t.Options[o], r.X + 17, r.Y + 1, sel ? HeaderCol : ValActive);
            }
            var top = OptionRect(_dd.OpenKey, 0);
            DrawUtils.DrawRectBorder(_g._spriteBatch, _g._pixel,
                new Rectangle(top.X, top.Y, BoxW, t.Options.Length * OptH), BoxBorder);
        }
    }

    // ── Draw helpers (rounded text + accented panel, self-contained) ─────────

    private void Text(SpriteFont f, string s, int x, int y, Color c)
        => Scope.DrawString(f, s, new Vector2(x, y), c);

    private void DrawPanel(Rectangle r, Color fill, Color accent, int accentH = 2, bool bottomAccent = false)
    {
        Scope.Draw(_g._pixel, r, fill);
        Scope.Draw(_g._pixel,
            new Rectangle(r.X, bottomAccent ? r.Bottom - accentH : r.Y, r.Width, accentH), accent);
    }
}
