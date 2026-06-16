using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;

namespace Necroking.UI;

/// <summary>
/// In-game grimoire ('J' to browse; also opened in ASSIGN mode when a spell-bar
/// slot is clicked). Owns the filter state (school tab + magic-path icon strip)
/// and routes clicks: a school/path tab re-filters; a spell tile either assigns
/// to the pending slot (assign mode) or does nothing yet (browse — direct cast
/// is a later phase).
/// </summary>
public class GrimoireOverlay : IModalLayer
{
    private const string InstanceId = "grimoire_ingame";
    private const int PanelW = 706;
    private const int PanelH = 1080;
    private const string TitleChild = "TitleText";

    // path-tab index (1..12) -> MagicPath, from the icon strip order
    // (confirmed via the baked PathIcon images).
    private static readonly MagicPath[] PathTabOrder =
    {
        MagicPath.Shock, MagicPath.Fire, MagicPath.Metal, MagicPath.Water,
        MagicPath.Heavens, MagicPath.Order, MagicPath.Earth, MagicPath.Chaos,
        MagicPath.Spirit, MagicPath.Nature, MagicPath.Body, MagicPath.Death,
    };

    // school tabs in tab order: a name token (drives the SchoolTab_{name}_Text /
    // _Backing child names) + the school it filters to; "All" clears the filter.
    private static readonly (string Name, string? School)[] SchoolTabs =
    {
        ("All", null), ("Conjuration", "Conjuration"),
        ("Alteration", "Alteration"), ("Evocation", "Evocation"),
        ("Construction", "Construction"),
    };
    private static readonly Color TabActive = new(245, 223, 182);
    private static readonly Color TabInactive = new(150, 138, 116);
    // Backing/icon tint for the active vs inactive tab. Active draws at full
    // brightness; inactive dims (warm grey multiply) so the selected school/path
    // tab clearly reads as pressed-in against the rest of the strip.
    private static readonly Color ChromeActive = Color.White;
    private static readonly Color ChromeInactive = new(112, 104, 92);

    private RuntimeWidgetRenderer _renderer = null!;
    private GameData? _gameData;
    private int _x, _y;

    private string? _schoolFilter;
    private MagicPath _pathFilter = MagicPath.None;
    private List<SpellDef> _shown = new();
    private Action<string>? _onPick;   // non-null => assign mode
    private bool _justOpened;          // skip the click that opened us
    private Func<SpellDef, bool>? _canShow; // null => show all; else path-req filter

    public bool IsVisible { get; private set; }

    public void Init(RuntimeWidgetRenderer renderer, GameData gameData,
        Func<SpellDef, bool>? canShow = null)
    {
        _renderer = renderer;
        _gameData = gameData;
        _canShow = canShow;
    }

    /// <summary>'J' — open in browse mode (or close if open).</summary>
    public void Toggle()
    {
        if (IsVisible) { Hide(); return; }
        _onPick = null;
        Open();
    }

    /// <summary>Open to pick a spell for a bar slot; onPick gets the spell id.</summary>
    public void OpenForAssign(Action<string> onPick)
    {
        _onPick = onPick;
        if (!IsVisible) Open();
        else Refresh();
    }

    private void Open()
    {
        IsVisible = true;
        _justOpened = true;
        _schoolFilter = null;
        _pathFilter = MagicPath.None;
        Refresh();
        Game1.Popups.Push(this);
    }

    public void Hide()
    {
        if (!IsVisible) return;
        IsVisible = false;
        _onPick = null;
        Game1.Popups.Pop(this);
    }

    private void Refresh()
    {
        if (_gameData == null) return;
        _shown = GrimoirePanel.Populate(_renderer, _gameData, InstanceId, _schoolFilter, _pathFilter, _canShow);
        _renderer.SetText(InstanceId, TitleChild, _onPick != null ? "Choose a Spell" : "Spells");
        ApplyTabHighlights(_renderer, InstanceId, _schoolFilter, _pathFilter);
    }

    /// <summary>Light the active school + path tabs (backing, icon, and school
    /// text) and dim the rest, so the current filter reads as selected/pressed.
    /// Static + instance-parameterised so the live overlay and the UI
    /// screenshot scenario drive identical visuals off the same code.</summary>
    public static void ApplyTabHighlights(RuntimeWidgetRenderer r, string instanceId,
        string? schoolFilter, MagicPath pathFilter)
    {
        // "All" mode (no specific filter) lights the whole group — nothing is
        // filtered out, so nothing dims. Selecting a specific school/path lights
        // just that tab and dims the rest, so the filtered-out options recede.
        bool allSchools = schoolFilter == null;
        bool allPaths = pathFilter == MagicPath.None;

        // School tabs — text colour + backing tint.
        foreach (var (name, school) in SchoolTabs)
        {
            bool lit = allSchools || school == schoolFilter;
            var tc = lit ? TabActive : TabInactive;
            r.SetTextColor(instanceId, $"SchoolTab_{name}_Text", tc.R, tc.G, tc.B);
            r.SetElementTint(instanceId, $"SchoolTab_{name}_Backing",
                lit ? ChromeActive : ChromeInactive);
        }
        // Path "All" tab (no icon) — the active filter when no path is picked.
        r.SetElementTint(instanceId, "PathTab_All_Backing",
            allPaths ? ChromeActive : ChromeInactive);
        // Path icon tabs — backing + icon dim together so the whole tab reads
        // as one unit. In all-paths mode every icon stays lit; once a path is
        // picked only it stays lit and the others dim.
        foreach (var path in PathTabOrder)
        {
            var chrome = (allPaths || path == pathFilter) ? ChromeActive : ChromeInactive;
            r.SetElementTint(instanceId, $"PathTab_{path}_Backing", chrome);
            r.SetElementTint(instanceId, $"PathTab_{path}_Icon", chrome);
        }
    }

    // === IModalLayer ===
    public bool ContainsMouse(int mx, int my)
        => IsVisible && mx >= _x && mx < _x + PanelW && my >= _y && my < _y + PanelH;
    public void OnCancel() => Hide();
    public bool LightDismiss => false;
    public bool IsBlocking => false;

    public void Update(InputState input, int screenW, int screenH)
    {
        if (!IsVisible) return;
        Layout(screenH);
        // The PopupManager already consumed the mouse for clicks inside us
        // (that's its contract — we handle them here via LeftPressed). But skip
        // the very click that OPENED us this frame (a bar-slot click lands
        // where a tile now is) — one-frame guard.
        if (_justOpened) { _justOpened = false; return; }
        if (!input.LeftPressed) return;
        int mx = (int)input.MousePos.X, my = (int)input.MousePos.Y;
        if (!ContainsMouse(mx, my)) return;
        HandleClickAt(mx, my);
    }

    /// <summary>Resolve a click at screen (mx,my) to a tab/path/tile action.
    /// Returns true if it hit something. Public so scenarios can drive it
    /// without an OS cursor (the panel layout must already be set via Draw).</summary>
    public bool HandleClickAt(int mx, int my)
    {
        // School tabs (hit-test the backing — the text element is wider than
        // its tab and would overlap neighbours).
        foreach (var (name, school) in SchoolTabs)
        {
            if (HitChild($"SchoolTab_{name}_Backing", mx, my))
            {
                _schoolFilter = school;
                Refresh();
                return true;
            }
        }
        // Path "All" tab clears the path filter
        if (HitChild("PathTab_All_Backing", mx, my))
        {
            _pathFilter = MagicPath.None;
            Refresh();
            return true;
        }
        // Path icon tabs (hit-test each path's backing)
        foreach (var path in PathTabOrder)
        {
            if (HitChild($"PathTab_{path}_Backing", mx, my))
            {
                _pathFilter = _pathFilter == path ? MagicPath.None : path; // click again clears
                Refresh();
                return true;
            }
        }
        // Spell tiles
        for (int i = 0; i < _shown.Count; i++)
        {
            if (HitChild($"tile{i}", mx, my))
            {
                if (_onPick != null)
                {
                    _onPick(_shown[i].Id);
                    Hide();
                }
                return true;
            }
        }
        return false;
    }

    // Test seam: the centre point of a named chrome child (tab) or a tile.
    public Point DebugChildCenter(string child)
    {
        var r = _renderer.GetChildRect(GrimoirePanel.WidgetId, child, _x, _y, InstanceId);
        return r == Rectangle.Empty ? Point.Zero : r.Center;
    }
    public int DebugShownCount => _shown.Count;
    public string DebugShownId(int i) => i < _shown.Count ? _shown[i].Id : "";

    public void Draw(int screenW, int screenH)
    {
        if (!IsVisible) return;
        Layout(screenH);
        _renderer.DrawWidget(GrimoirePanel.WidgetId, _x, _y, InstanceId);
    }

    private void Layout(int screenH)
    {
        _x = 16;
        _y = Math.Min(0, (screenH - PanelH) / 2);
    }

    private bool HitChild(string child, int mx, int my)
    {
        var r = _renderer.GetChildRect(GrimoirePanel.WidgetId, child, _x, _y, InstanceId);
        return r != Rectangle.Empty && r.Contains(mx, my);
    }
}
