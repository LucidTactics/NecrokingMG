using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Core;
using Necroking.Data.Registries;
using Necroking.Editor;
using Necroking.Lib;
using Necroking.Render;

namespace Necroking.UI;

/// <summary>
/// Left-side debug settings panel: a dropdown per debug-mode F-key (F2 Water,
/// F3 Perf, F5 Death Fog, F6 Wind, F7 Gameplay, F8 Collision). Docks under the
/// top-left player-data (HP/Mana) status bars and mirrors the exact same state
/// the F-keys toggle — the F-key block in Game1.Update stays the source of
/// truth for each field, this is the click-driven twin. Besides toggle rows the
/// list also supports section headers (<c>DebugModeToggle.Header</c>) and
/// full-width action buttons (<c>DebugModeToggle.Button</c>) — add new ones in
/// <see cref="BuildToggles"/>.
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

    // One panel row. Four kinds, all sharing the same RowH slot so the layout
    // math stays uniform: a section header (label + rule, no control), a
    // full-width button (fires OnClick on press), a boolean checkbox row, or a
    // dropdown row. Toggle rows carry get/set hooks that read/write the live
    // Game1 field (Direct over Inject).
    private sealed class DebugModeToggle
    {
        public string Label = "";
        public string[] Options = Array.Empty<string>();
        // Per-option hover tooltips, index-aligned with Options (null entry = no tip,
        // '\n' = line break). Dropdown rows show them while the option list is
        // expanded; boolean (checkbox) rows have no list, so they surface the
        // "On" tip (index 1) as a whole-row hover tooltip instead. Button rows
        // use index 0 as their hover tooltip.
        public string?[]? Tooltips;
        public Func<int> Get = () => 0;
        public Action<int> Set = _ => { };
        // Section divider row: label only, no control, no interaction.
        public bool IsHeader;
        // Full-width button row: fires on press instead of holding a value.
        public Action? OnClick;
        // Excluded from the "Reset Debug Settings" button (e.g. fog mode, which
        // is a display setting rather than a debug overlay).
        public bool SkipReset;

        public bool IsButton => OnClick != null;
        // Plain On/Off rows render as a click-to-flip switch, not a dropdown.
        public bool IsBool => Options.Length == 2 && Options[0] == "Off" && Options[1] == "On";

        public static DebugModeToggle Header(string label) => new() { Label = label, IsHeader = true };
        public static DebugModeToggle Button(string label, string? tip, Action onClick)
            => new() { Label = label, OnClick = onClick, Tooltips = tip != null ? new[] { tip } : null };
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

        // Collision-mode option names + tooltips, driven off the enum (minus the
        // Count sentinel) so the panel never drifts from the F8 cycle.
        int collCount = (int)CollisionDebugMode.Count;
        var collOpts = new string[collCount];
        var collTips = new string?[collCount];
        for (int i = 0; i < collCount; i++)
        {
            collOpts[i] = DebugDraw.GetModeLabel((CollisionDebugMode)i);
            collTips[i] = (CollisionDebugMode)i switch
            {
                CollisionDebugMode.All =>
                    "Every collision overlay at once: cost field,\nORCA radii, velocity vectors, occupied tiles, chunks.",
                CollisionDebugMode.Chunks =>
                    "Pathfinder sectors (blue grid + labels), per-unit\nimaginary chunks (orange), each unit's sector tinted.",
                CollisionDebugMode.CostField =>
                    "Tints pathfinding tiles by move cost: red impassable,\nblue water/high cost, yellow rough. Open tiles blank.",
                CollisionDebugMode.UnitORCA =>
                    "Each unit's collision-radius circle: necromancer\nbright green (+ring), other undead green, humans red.",
                CollisionDebugMode.Velocity =>
                    "Per-unit velocity arrows: current velocity (green)\nand preferred/desired velocity (blue).",
                CollisionDebugMode.OccupiedTiles =>
                    "Env-object collision circles (magenta) + covered\ntiles, plus unit radius circles per faction.",
                _ => null,
            };
        }

        return new List<DebugModeToggle>
        {
            new() { Label = "Fog", Options = SettingsWindow.FogModeNames,
                SkipReset = true, // display setting, not a debug overlay
                Get = () => _g._gameData.Settings.FogOfWar.Mode,
                Set = v => _g._gameData.Settings.FogOfWar.Mode = v,
                Tooltips = new[] { null,
                    "Outlines every registered UI hit-region with a\n1px yellow border." } },
            new() { Label = "F2 Water",     Options = onOff, Get = () => _g._waterDebug ? 1 : 0,
                    Set = v => _g._waterDebug = v == 1,
                    Tooltips = new[] { null,
                        "Per-unit wading overlay: body bounding box,\nwaterlines, and w/V/slope/angle text per unit." } },
            new() { Label = "F3 Perf",      Options = onOff, Get = () => _g._showPerfReadout ? 1 : 0,
                    Set = v => _g._showPerfReadout = v == 1,
                    Tooltips = new[] { null,
                        "Bottom-left stats line: zoom, cam pos, speed, FPS,\nframe/sim/draw/ground/present ms + draw counts." } },
            new() { Label = "F5 Death Fog", Options = onOff, Get = () => _g._deathFog.DebugVisible ? 1 : 0,
                    // DeathFogSystem only exposes a toggle — flip only when the
                    // chosen state differs from the current one.
                    Set = v => { if ((v == 1) != _g._deathFog.DebugVisible) _g._deathFog.ToggleDebug(); },
                    Tooltips = new[] { null,
                        "Death-fog density heatmap (blue low to red high)\nplus per-tree corruption stress, DYING or DEAD." } },
            new() { Label = "F6 Wind",      Options = onOff, Get = () => _g._windDebug ? 1 : 0,
                    Set = v => _g._windDebug = v == 1,
                    Tooltips = new[] { null,
                        "Wind field: grid of gust-strength cells (blue still\nto white peak) + direction arrow and angle top-left." } },
            new() { Label = "F7 Gameplay",  Options = new[] { "Off", "Horde", "Unit Info" },
                    Get = () => _g._gameplayDebugMode,
                    Set = v => _g._gameplayDebugMode = v,
                    Tooltips = new[] { null,
                        "Horde formation: formation/engage/leash rings,\nfacing arrow, slot lines colored by unit state.",
                        "Per-unit text: AI + routine, velocity, anim, effort,\nmax speed; red target line and blue velocity arrow." } },
            new() { Label = "F8 Collision", Options = collOpts,
                    Get = () => (int)_g._collisionDebugMode,
                    Set = v => _g._collisionDebugMode = (CollisionDebugMode)v,
                    Tooltips = collTips },
            new() { Label = "UI Debug", Options = new[] { "Off", "DrawRegions" },
                    Get = () => _g._uiDebugDrawMode,
                    Set = v => _g._uiDebugDrawMode = v,
                    Tooltips = new[] { null,
                        "Outlines every registered UI hit-region with a\n1px yellow border." } },
            DebugModeToggle.Header("Actions"),
            DebugModeToggle.Button("Give Skill Resources",
                "Same as Shift+O: +10 to every skill event tally\nand skill point pool, +10 evolution potions.",
                () => _g.CheatAddAllSkillcounters(_g.FindNecromancer(), 10)),
            DebugModeToggle.Button("Reset Debug Settings",
                "Turns every debug toggle above back Off.\nFog mode is left as-is.",
                ResetDebugSettings),
        };
    }

    /// <summary>Set every resettable toggle row back to its Off/default value.
    /// Skips headers, buttons, and rows marked <c>SkipReset</c> (fog mode).</summary>
    private void ResetDebugSettings()
    {
        foreach (var t in Toggles)
            if (!t.IsHeader && !t.IsButton && !t.SkipReset)
                t.Set(0);
    }

    // ── Layout ──────────────────────────────────────────────────────────────
    // Fixed top-left geometry, shared by draw + hit-test so they never desync.
    private const int PanX = 10;      // matches HUDRenderer.BarX
    private const int PanW = 240;     // wider than HUDRenderer.BarWidth (200) so dropdown labels fit
    private const int PanY = 88;      // just under the HP/Mana bars (bottom ≈ 82)
    private const int HeaderH = 18;
    private const int RowH = 22;
    private const int Pad = 6;
    private const int BoxW = 148;     // dropdown box on the right of each row
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

    /// <summary>Full-width hit/hover rect for row <paramref name="i"/> (label +
    /// control). Boolean rows toggle on a press anywhere in here; hovering it
    /// lights the whole row.</summary>
    private Rectangle RowRect(int i)
    {
        int rowY = PanY + HeaderH + Pad + i * RowH;
        return new Rectangle(PanX + 2, rowY, PanW - 4, RowH);
    }

    /// <summary>Clickable dropdown box for row <paramref name="i"/>.</summary>
    private Rectangle BoxRect(int i)
    {
        int rowY = PanY + HeaderH + Pad + i * RowH;
        return new Rectangle(PanX + PanW - Pad - BoxW, rowY + 1, BoxW, RowH - 4);
    }

    /// <summary>Full-width clickable button for a button row (slightly inset
    /// vertically inside the row slot). Shared by draw and hit-test.</summary>
    private Rectangle ButtonRect(int i)
    {
        var row = RowRect(i);
        return new Rectangle(row.X, row.Y + 1, row.Width, RowH - 4);
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

    /// <summary>Row index whose dropdown box contains the cursor, or -1.
    /// Header/button rows have no dropdown box and never match.</summary>
    private int HitBox(int mx, int my)
    {
        for (int i = 0; i < Toggles.Count; i++)
            if (!Toggles[i].IsHeader && !Toggles[i].IsButton && BoxRect(i).Contains(mx, my))
                return i;
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

    /// <summary>Boolean row whose whole row-rect (label + checkbox) contains the
    /// cursor, or -1. Boolean rows toggle on a press anywhere in the row.</summary>
    private int HitBoolRow(int mx, int my)
    {
        for (int i = 0; i < Toggles.Count; i++)
            if (Toggles[i].IsBool && RowRect(i).Contains(mx, my)) return i;
        return -1;
    }

    /// <summary>Button row whose button rect contains the cursor, or -1.</summary>
    private int HitButtonRow(int mx, int my)
    {
        for (int i = 0; i < Toggles.Count; i++)
            if (Toggles[i].IsButton && ButtonRect(i).Contains(mx, my)) return i;
        return -1;
    }

    /// <summary>A left press granted to the panel (layer's OnPointer). Boolean
    /// rows flip immediately (click anywhere in the row); the rest open / arm /
    /// mark-for-close the eager dropdown, whose selection fires on release in
    /// <see cref="HandleFrame"/>.</summary>
    public void HandlePress(int mx, int my)
    {
        int item = HitItem(mx, my);

        // A press anywhere on a boolean row flips it now, and a press on a
        // button row fires it — but not when it lands on an open dropdown's
        // option (selection wins, since an open list overlaps the rows below it).
        if (item < 0)
        {
            int brow = HitBoolRow(mx, my);
            if (brow >= 0)
            {
                var t = Toggles[brow];
                t.Set(t.Get() == 1 ? 0 : 1);
                _dd.Close();
                return;
            }

            int btn = HitButtonRow(mx, my);
            if (btn >= 0)
            {
                _dd.Close();
                Toggles[btn].OnClick!();
                return;
            }
        }

        _dd.OnPress(HitBox(mx, my), item);
    }

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
    // Subtle full-row wash when a boolean row is hovered (label + checkbox lift together).
    private static readonly Color RowHover    = new(120, 200, 150, 28);

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
            int rowY = PanY + HeaderH + Pad + i * RowH;

            if (t.IsHeader)
            {
                // Section divider: header-colored label with a 1px rule filling
                // the rest of the row width.
                Text(f, t.Label, panel.X + Pad, rowY + 4, HeaderCol);
                int lineX = panel.X + Pad + (int)f.MeasureString(t.Label).X + 6;
                int lineR = panel.Right - Pad;
                if (lineR > lineX)
                    Scope.Draw(_g._pixel,
                        new Rectangle(lineX, rowY + RowH / 2, lineR - lineX, 1), BoxBorder);
                continue;
            }

            if (t.IsButton)
            {
                var btn = ButtonRect(i);
                bool btnHover = btn.Contains(mx, my);
                DrawPanel(btn, btnHover ? BoxHover : BoxBg, BoxBorder, 1, bottomAccent: true);
                DrawUtils.DrawRectBorder(Scope, _g._pixel, btn, BoxBorder);
                int tw = (int)f.MeasureString(t.Label).X;
                Text(f, t.Label, btn.X + (btn.Width - tw) / 2, btn.Y + 2, ValActive);
                string? btnTip = t.Tooltips != null && t.Tooltips.Length > 0 ? t.Tooltips[0] : null;
                if (btnHover && !string.IsNullOrEmpty(btnTip))
                    Game1.Tooltips.RequestLines(btnTip.Split('\n'));
                continue;
            }

            int cur = Math.Clamp(t.Get(), 0, t.Options.Length - 1);
            var box = BoxRect(i);

            if (t.IsBool)
            {
                // Whole-row target: hovering lights the label+checkbox strip and
                // the press flips it (see HandlePress / HitBoolRow).
                bool rowHover = RowRect(i).Contains(mx, my);
                if (rowHover) Scope.Draw(_g._pixel, RowRect(i), RowHover);
                // No option list on a checkbox row — surface the "On" tip (what
                // the overlay shows when enabled) as the row's hover tooltip.
                string? btip = t.Tooltips != null && t.Tooltips.Length > 1 ? t.Tooltips[1] : null;
                if (rowHover && !string.IsNullOrEmpty(btip))
                    Game1.Tooltips.RequestLines(btip.Split('\n'));
                Text(f, t.Label, panel.X + Pad, rowY + 4, LabelCol);
                DrawCheckbox(box, cur == 1, rowHover);
                continue;
            }

            Text(f, t.Label, panel.X + Pad, rowY + 4, LabelCol);
            bool hover = box.Contains(mx, my);
            DrawPanel(box, hover ? BoxHover : BoxBg, BoxBorder, 1, bottomAccent: true);
            DrawUtils.DrawRectBorder(_g.Scope, _g._pixel, box, BoxBorder);

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
                // Per-option tooltip via the global service: renders on the Tooltip
                // band, above this list. Parked cursor (hover not owned) never hits.
                string? tip = t.Tooltips != null && o < t.Tooltips.Length ? t.Tooltips[o] : null;
                if (hover && !string.IsNullOrEmpty(tip))
                    Game1.Tooltips.RequestLines(tip.Split('\n'));
                Scope.Draw(_g._pixel, r, hover ? OptHover : sel ? OptSelBg : OptBg);
                // Left-gutter checkmark on the current value so it reads at a
                // glance; all options indent past the gutter to stay aligned.
                if (sel)
                    DrawUtils.DrawCheckmark(Scope, _g._pixel,
                        new Rectangle(r.X + 4, r.Y + 4, 9, 9), HeaderCol);
                Text(f, t.Options[o], r.X + 17, r.Y + 1, sel ? HeaderCol : ValActive);
            }
            var top = OptionRect(_dd.OpenKey, 0);
            DrawUtils.DrawRectBorder(_g.Scope, _g._pixel,
                new Rectangle(top.X, top.Y, BoxW, t.Options.Length * OptH), BoxBorder);
        }
    }

    // ── Draw helpers (rounded text + accented panel, self-contained) ─────────

    private void Text(SpriteFont f, string s, int x, int y, Color c)
        => Scope.DrawString(f, s, new Vector2(x, y), c);

    /// <summary>Draw the boolean row's checkbox (right-aligned in <paramref
    /// name="box"/>'s slot) using the shared editor-style glyph + palette, so it
    /// matches the toggles in the editor property panels.</summary>
    private void DrawCheckbox(Rectangle box, bool on, bool hover)
    {
        const int CbSize = 16;
        var cb = new Rectangle(box.Right - CbSize, box.Y + (box.Height - CbSize) / 2, CbSize, CbSize);
        DrawUtils.DrawCheckbox(Scope, _g._pixel, cb, on, hover,
            EditorBase.InputBg, EditorBase.InputBorder, EditorBase.InputActive, EditorBase.AccentColor);
    }

    private void DrawPanel(Rectangle r, Color fill, Color accent, int accentH = 2, bool bottomAccent = false)
    {
        Scope.Draw(_g._pixel, r, fill);
        Scope.Draw(_g._pixel,
            new Rectangle(r.X, bottomAccent ? r.Bottom - accentH : r.Y, r.Width, accentH), accent);
    }
}
