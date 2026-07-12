using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Necroking.Render;

namespace Necroking;

// Left-side debug settings panel: a dropdown per debug-mode F-key (F2/F3/F5/F6/
// F7/F8). Sits just under the top-left player-data (HP/Mana) status bars and
// mirrors the exact same state the F-keys toggle — the F-key block in
// Game1.Update stays the source of truth for each field, this is just the
// click-driven twin. Draw + hit-test + open-state all live here so the HUD
// layer (DebugSettingsPanelLayer) stays a thin router, matching the
// AggressionBar pattern.
partial class GameRenderer
{
    // One debug mode the panel exposes: a label, its option names, and get/set
    // hooks that read/write the live Game1 field (Direct over Inject).
    private sealed class DebugModeToggle
    {
        public string Label = "";
        public string[] Options = Array.Empty<string>();
        public Func<int> Get = () => 0;
        public Action<int> Set = _ => { };
    }

    private List<DebugModeToggle>? _debugModeToggles;
    // Which row's dropdown list is currently expanded (-1 = none).
    private int _openDebugDropdown = -1;

    private List<DebugModeToggle> DebugModeToggles => _debugModeToggles ??= BuildDebugModeToggles();

    private List<DebugModeToggle> BuildDebugModeToggles()
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
        };
    }

    // ── Layout ──────────────────────────────────────────────────────────────
    // Fixed top-left geometry, shared by draw + hit-test so they never desync.
    private const int DbgPanX = 10;      // matches HUDRenderer.BarX
    private const int DbgPanW = 200;     // matches HUDRenderer.BarWidth
    private const int DbgPanY = 88;      // just under the HP/Mana bars (bottom ≈ 82)
    private const int DbgHeaderH = 18;
    private const int DbgRowH = 22;
    private const int DbgPad = 6;
    private const int DbgBoxW = 108;     // dropdown box on the right of each row
    private const int DbgOptH = 18;

    /// <summary>Panel rect. Height grows with the number of debug toggles.</summary>
    private Rectangle DebugPanelRect()
    {
        int rows = DebugModeToggles.Count;
        int h = DbgHeaderH + DbgPad + rows * DbgRowH + DbgPad;
        return new Rectangle(DbgPanX, DbgPanY, DbgPanW, h);
    }

    /// <summary>Clickable dropdown box for row <paramref name="i"/>.</summary>
    private Rectangle DebugBoxRect(int i)
    {
        int rowY = DbgPanY + DbgHeaderH + DbgPad + i * DbgRowH;
        return new Rectangle(DbgPanX + DbgPanW - DbgPad - DbgBoxW, rowY + 1, DbgBoxW, DbgRowH - 4);
    }

    /// <summary>Rect of option <paramref name="opt"/> in row <paramref name="i"/>'s
    /// open dropdown list (drawn below the box).</summary>
    private Rectangle DebugOptionRect(int i, int opt)
    {
        var box = DebugBoxRect(i);
        return new Rectangle(box.X, box.Bottom + 1 + opt * DbgOptH, box.Width, DbgOptH);
    }

    // ── Input hooks (called by DebugSettingsPanelLayer) ─────────────────────

    /// <summary>True if the cursor is over the panel or an open dropdown list —
    /// the layer's ContainsMouse.</summary>
    internal bool DebugPanelContains(int mx, int my)
    {
        if (DebugPanelRect().Contains(mx, my)) return true;
        if (_openDebugDropdown >= 0)
        {
            var t = DebugModeToggles[_openDebugDropdown];
            for (int o = 0; o < t.Options.Length; o++)
                if (DebugOptionRect(_openDebugDropdown, o).Contains(mx, my)) return true;
        }
        return false;
    }

    /// <summary>Route a left-click into the panel: pick an open-list option, or
    /// open/close/switch a row's dropdown.</summary>
    internal void HandleDebugPanelClick(int mx, int my)
    {
        var toggles = DebugModeToggles;

        // 1) A click on the open dropdown's option list selects it.
        if (_openDebugDropdown >= 0)
        {
            var t = toggles[_openDebugDropdown];
            for (int o = 0; o < t.Options.Length; o++)
            {
                if (DebugOptionRect(_openDebugDropdown, o).Contains(mx, my))
                {
                    t.Set(o);
                    _openDebugDropdown = -1;
                    return;
                }
            }
        }

        // 2) A click on a row's box toggles/switches which list is open.
        for (int i = 0; i < toggles.Count; i++)
        {
            if (DebugBoxRect(i).Contains(mx, my))
            {
                _openDebugDropdown = _openDebugDropdown == i ? -1 : i;
                return;
            }
        }

        // 3) Click elsewhere inside the panel closes any open list.
        _openDebugDropdown = -1;
    }

    /// <summary>Close an open dropdown if a click landed outside the panel — the
    /// layer only receives input while the cursor is over it, so this catches
    /// dismiss-on-outside-click. Called each frame from the layer's Draw.</summary>
    internal void MaybeCloseDebugPanel(int mx, int my, bool leftPressed)
    {
        if (leftPressed && _openDebugDropdown >= 0 && !DebugPanelContains(mx, my))
            _openDebugDropdown = -1;
    }

    // ── Draw ────────────────────────────────────────────────────────────────

    private static readonly Color DbgPanelBg     = new(10, 12, 16, 210);
    private static readonly Color DbgPanelAccent  = new(80, 200, 120, 220);
    private static readonly Color DbgHeaderCol    = new(120, 230, 160);
    private static readonly Color DbgLabelCol     = new(180, 190, 200);
    private static readonly Color DbgBoxBg        = new(28, 32, 40, 235);
    private static readonly Color DbgBoxHover     = new(46, 54, 66, 240);
    private static readonly Color DbgBoxBorder    = new(90, 110, 130);
    private static readonly Color DbgValActive    = new(230, 240, 200);
    private static readonly Color DbgValOff       = new(140, 150, 160);
    private static readonly Color DbgOptBg        = new(20, 24, 30, 245);
    private static readonly Color DbgOptHover     = new(60, 90, 70, 250);

    /// <summary>Draw the left-side debug settings panel. Runs inside the open HUD
    /// batch. <paramref name="mx"/>/<paramref name="my"/> are the cursor (parked
    /// off-screen by the layer when it doesn't own the hover).</summary>
    internal void DrawDebugSettingsPanel(int mx, int my)
    {
        var f = _g._smallFont;
        if (f == null) return;

        var toggles = DebugModeToggles;
        var panel = DebugPanelRect();
        DrawPanel(panel, DbgPanelBg, DbgPanelAccent);
        DrawText(f, "DEBUG", new Vector2(panel.X + DbgPad, panel.Y + 3), DbgHeaderCol);

        for (int i = 0; i < toggles.Count; i++)
        {
            var t = toggles[i];
            int cur = Math.Clamp(t.Get(), 0, t.Options.Length - 1);
            int rowY = DbgPanY + DbgHeaderH + DbgPad + i * DbgRowH;

            DrawText(f, t.Label, new Vector2(panel.X + DbgPad, rowY + 4), DbgLabelCol);

            var box = DebugBoxRect(i);
            bool hover = box.Contains(mx, my);
            DrawPanel(box, hover ? DbgBoxHover : DbgBoxBg, DbgBoxBorder, 1, bottomAccent: true);
            DrawUtils.DrawRectBorder(_g._spriteBatch, _g._pixel, box, DbgBoxBorder);

            string val = t.Options[cur];
            var valCol = cur == 0 ? DbgValOff : DbgValActive;
            DrawText(f, val, new Vector2(box.X + 5, box.Y + 2), valCol);
            // Down caret on the right edge of the box.
            DrawText(f, _openDebugDropdown == i ? "^" : "v",
                new Vector2(box.Right - 12, box.Y + 2), DbgBoxBorder);
        }

        // Open dropdown list, drawn last so it sits over rows below it.
        if (_openDebugDropdown >= 0)
        {
            var t = toggles[_openDebugDropdown];
            int cur = Math.Clamp(t.Get(), 0, t.Options.Length - 1);
            for (int o = 0; o < t.Options.Length; o++)
            {
                var r = DebugOptionRect(_openDebugDropdown, o);
                bool hover = r.Contains(mx, my);
                _g.Scope.Draw(_g._pixel, r, hover ? DbgOptHover : DbgOptBg);
                DrawText(f, t.Options[o], new Vector2(r.X + 5, r.Y + 1),
                    o == cur ? DbgHeaderCol : DbgValActive);
            }
            DrawUtils.DrawRectBorder(_g._spriteBatch, _g._pixel,
                new Rectangle(DebugOptionRect(_openDebugDropdown, 0).X,
                    DebugOptionRect(_openDebugDropdown, 0).Y,
                    DbgBoxW, t.Options.Length * DbgOptH), DbgBoxBorder);
        }
    }
}
