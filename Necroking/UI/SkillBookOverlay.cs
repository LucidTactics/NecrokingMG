using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Core;
using Necroking.Data;
using Necroking.Game;
using Necroking.Game.SkillEffects;
using Necroking.GameSystems;

namespace Necroking.UI;

/// <summary>
/// Widget-driven skill book ("Tome of the Necroking", K to open). Cloned from the
/// spell grimoire's pattern: the chrome is the <c>SkillBookWindow</c> widget
/// (frame, ribbon, school tabs, divider, page) drawn via the runtime widget
/// renderer, and each skill is a <c>SkillTile</c> widget stamped at its tree
/// position with its data bound (icon / name / cost) — exactly how
/// <see cref="GrimoireOverlay"/> + <see cref="GrimoirePanel"/> bind spell tiles.
///
/// Unlike the grimoire's fixed tile grid, skill tiles sit at authored 2D tree
/// positions with connector lines between prerequisites, so they're stamped as
/// standalone instances (there is no child-position override) and the connectors
/// are drawn in code between the chrome and the tiles.
/// </summary>
public class SkillBookOverlay : IModalLayer
{
    private const string WindowId = "SkillBookWindow";
    private const string TileId   = "SkillTile";
    private const string Instance = "skillbook";

    // Tabs are driven dynamically from SkillBookDefs.Tabs (no fixed name list). The
    // tabs live in a horizontal-layout "TabBar" sub-widget; each is a "SkillBookTab"
    // widget (Backing / Name / Frac / Frame) instanced as TabBar child "tab{i}",
    // bound through the nested instance path "{Instance}.{tabBarIdx}.{i}". The TabBar
    // ships with a pool of slots (tab0..tabN); we bind/show the first Tabs.Count and
    // hide the rest, sizing each visible tab to fill the bar.

    private RuntimeWidgetRenderer _renderer = null!;
    private SpriteBatch _batch = null!;
    private Render.SpriteScope Scope => _batch;  // straight-alpha draw surface (implicit conversion)
    private Texture2D _pixel = null!;

    private SkillBookState _state = null!;
    private Inventory _inventory = null!;
    private GameData _gameData = null!;
    private SpellBarState _bar;
    private Simulation? _sim;

    private int _x, _y, _w, _h, _sh;
    private int _activeTab;
    private string? _toast;
    private double _toastUntil;

    // Hover tooltip: the skill currently under the cursor (null = none) and the
    // mouse position to anchor the panel to. Set in Update, drawn in Draw.
    private string? _hoverId;
    private Vector2 _hoverMouse;
    // When set (test/host only), Update leaves the hover state alone so a forced
    // tooltip survives across frames for screenshots.
    private bool _hoverLock;

    // --- Layout editor: drag tiles to reposition, connectors follow, save to JSON ---
    /// <summary>Gates the on-screen "Edit Layout" toggle. Dev/authoring tool — set
    /// false (or behind a dev setting) before shipping to players.</summary>
    public static bool EnableLayoutEditor = true;
    private bool _editLayout;
    private string? _dragId;
    private float _dragGrabX, _dragGrabY;
    private bool _layoutDirty;

    // Cached index of the TabBar child within SkillBookWindow. The five tabs are
    // nested child-widgets of it, so they're addressed as "{Instance}.{tabBarIdx}.{i}".
    private int _tabBarIdx = -1;

    // Per-skill stamped tile rects this frame (for hit-testing clicks).
    private readonly List<(string id, Rectangle rect)> _tileRects = new();

    public bool IsVisible { get; private set; }
    /// <summary>Layout-editor tile drag in progress — the router captures the mouse for us.</summary>
    public bool IsDragging => _dragId != null;

    public void Init(RuntimeWidgetRenderer renderer, SpriteBatch batch, Texture2D pixel)
    {
        _renderer = renderer;
        _batch = batch;
        _pixel = pixel;
    }

    public void Bind(SkillBookState state, Inventory inv, GameData gd,
        SpellBarState bar, Simulation? sim = null)
    {
        _state = state; _inventory = inv; _gameData = gd;
        _bar = bar; _sim = sim;
    }

    public void Toggle() { if (IsVisible) Close(); else Open(); }
    public void Open()  { IsVisible = true; }
    public void Close() { IsVisible = false; }

    // === IModalLayer ===
    public bool LightDismiss => false;
    public bool IsBlocking => true;
    public bool ContainsMouse(int mx, int my)
        => IsVisible && mx >= _x && mx < _x + _w && my >= _y && my < _y + _h;
    public void OnCancel() => Close();

    // ----- test/host hooks (parity with the old panel) -----
    public int ActiveTab => _activeTab;
    public void SetActiveTab(int i)
    {
        if (i >= 0 && i < SkillBookDefs.Tabs.Count) _activeTab = i;
    }

    /// <summary>Test/host hook: is the tab at index <paramref name="i"/> currently
    /// unlocked (visible)? Mirrors the visibility gate used to hide locked tabs.</summary>
    public bool IsTabUnlockedForTest(int i)
        => i >= 0 && i < SkillBookDefs.Tabs.Count && IsTabUnlocked(SkillBookDefs.Tabs[i]);

    /// <summary>Test/host hook: set a passive flag on the book state (e.g.
    /// "morphed:lich") to drive morph-gated tab visibility without a live morph.</summary>
    public void SetPassiveForTest(string flag) => _state?.SetPassive(flag);
    public bool TryLearnById(string id, double timeSec = 0)
    {
        TryLearn(id, timeSec);
        return _state?.IsLearned(id) ?? false;
    }
    /// <summary>Test/host hook: toggle the layout-edit mode programmatically.</summary>
    public void SetLayoutEditMode(bool on) { _editLayout = on; _dragId = null; }

    /// <summary>Test/host hook: force the hover tooltip onto a skill by id, anchored at
    /// its tile centre (falls back to screen-ish coords if the tile isn't laid out yet).
    /// Pass null to clear. Only takes effect for the frame(s) before Update reruns.</summary>
    public void SetHoverForTest(string? id)
    {
        _hoverId = id;
        _hoverLock = id != null;
        if (id == null) return;
        foreach (var (tid, rect) in _tileRects)
            if (tid == id) { _hoverMouse = new Vector2(rect.Center.X, rect.Center.Y); return; }
        _hoverMouse = new Vector2(_x + _w / 2f, _y + _h / 2f);
    }

    /// <summary>Test/host hook: bump a milestone-event tally (e.g. "monster_kill") so the
    /// tooltip's have/need progress can be exercised without staging a real kill.</summary>
    public void TallyEventForTest(string eventKey, int n = 1) => _state?.Events.Tally(eventKey, n);

    private void Layout(int sw, int sh)
    {
        var def = _renderer.GetWidgetDef(WindowId);
        _w = def?.Width ?? 706;
        _h = def?.Height ?? 1080;
        // Fixed size, centred horizontally. If the window is taller than the
        // screen, anchor its top (clip the bottom) so the ribbon + tabs stay
        // usable; otherwise centre vertically.
        _x = (sw - _w) / 2;
        _y = _h <= sh - 16 ? (sh - _h) / 2 : 8;
        _sh = sh;
    }

    public void Update(InputState input, int sw, int sh, double timeSec)
    {
        if (!IsVisible) return;
        if (_toast != null && timeSec >= _toastUntil) _toast = null;
        Layout(sw, sh);
        int mx = (int)input.MousePos.X, my = (int)input.MousePos.Y;
        if (!_hoverLock) _hoverId = null;

        // A drag in progress continues anywhere on screen until the button releases.
        // (No MouseOverUI set needed here: this overlay is IsBlocking, so the
        // central UIHitRegistry blankets the whole screen while it's open.)
        if (_dragId != null)
        {
            if (input.LeftDown) DragTo(mx, my);
            else _dragId = null;
            return;
        }

        if (!ContainsMouse(mx, my)) return;

        // Hover tooltip: track the tile under the cursor (skip while editing layout,
        // where the cursor means "drag", not "inspect"). _tileRects is from last
        // frame's Draw, which is fine for hover.
        if (!_editLayout && !_hoverLock)
            foreach (var (id, rect) in _tileRects)
                if (rect.Contains(mx, my)) { _hoverId = id; _hoverMouse = new Vector2(mx, my); break; }

        if (!input.LeftPressed) return;

        // Layout-editor toolbar (drawn over the page bottom).
        if (EnableLayoutEditor && EditBtnRect().Contains(mx, my))
        {
            _editLayout = !_editLayout; _dragId = null; return;
        }
        if (_editLayout && SaveBtnRect().Contains(mx, my)) { DoSaveLayout(timeSec); return; }

        // Tab clicks: tabs are nested child-widgets of the TabBar, so resolve the
        // TabBar's screen rect first, then each tab's laid-out rect within it.
        int barIdx = TabBarIndex();
        if (barIdx >= 0)
        {
            var bar = _renderer.GetChildRect(WindowId, "TabBar", _x, _y, Instance);
            string barInst = $"{Instance}.{barIdx}";
            var barChildren = _renderer.GetWidgetDef("TabBar")?.Children;
            for (int i = 0; i < SkillBookDefs.Tabs.Count && i < SlotCount(); i++)
            {
                if (!IsTabUnlocked(SkillBookDefs.Tabs[i])) continue; // locked tab isn't clickable
                // Slot addressed by its actual child name (see BindTabs) — computed
                // "tab{i}" broke when the widget data's slot naming had a gap.
                var r = _renderer.GetChildRect("TabBar", barChildren![i].Name, bar.X, bar.Y, barInst);
                if (r != Rectangle.Empty && r.Contains(mx, my)) { _activeTab = i; return; }
            }
        }
        // Tile clicks: in edit mode start dragging the tile; otherwise try to learn it.
        foreach (var (id, rect) in _tileRects)
            if (rect.Contains(mx, my))
            {
                if (_editLayout)
                {
                    _dragId = id;
                    _dragGrabX = mx - (rect.X + rect.Width / 2f);
                    _dragGrabY = my - (rect.Y + rect.Height / 2f);
                }
                else TryLearn(id, timeSec);
                return;
            }
    }

    /// <summary>Move the dragged skill so its centre tracks the cursor (minus the grab
    /// offset), converting screen position back to the tab's logical tree coords. The
    /// connectors recompute from positions, so they follow automatically.</summary>
    private void DragTo(int mx, int my)
    {
        if (_activeTab < 0 || _activeTab >= SkillBookDefs.Tabs.Count) return;
        var tab = SkillBookDefs.Tabs[_activeTab];
        int i = tab.IndexOf(_dragId!);
        if (i < 0) return;
        var p = TreePlacement(tab);
        if (p.scale <= 0) return;
        float cx = mx - _dragGrabX, cy = my - _dragGrabY;
        tab.Skills[i].X = (int)MathF.Round((cx - p.ox) / p.scale);
        tab.Skills[i].Y = (int)MathF.Round((cy - p.oy) / p.scale);
        _layoutDirty = true;
    }

    private void DoSaveLayout(double timeSec)
    {
        bool ok = SkillBookDefs.SaveLayout();
        if (ok) _layoutDirty = false;
        Toast(ok ? "Layout saved." : "Save failed - see log.", timeSec);
    }

    // Toolbar button rects, anchored to the visible bottom-right of the window so
    // they stay on-screen even when the window is taller than the display.
    private Rectangle EditBtnRect()
    {
        const int bw = 104, bh = 26, pad = 12;
        int by = Math.Min(_y + _h, _sh) - pad - bh;
        return new Rectangle(_x + _w - pad - bw, by, bw, bh);
    }
    private Rectangle SaveBtnRect()
    {
        var e = EditBtnRect();
        return new Rectangle(e.X - 8 - e.Width, e.Y, e.Width, e.Height);
    }

    public void Draw(int sw, int sh)
    {
        if (!IsVisible) return;
        Layout(sw, sh);

        // Dim the world behind the modal (Overlay band — not covered by
        // MenuHostLayer's shared dim, so it draws its own).
        Scope.Draw(_pixel, new Rectangle(0, 0, sw, sh), MenuDraw.ModalDimColor);

        // Never sit on a locked tab (e.g. it re-locked, or one was force-set).
        EnsureActiveTabUnlocked();

        // Bind the chrome (title + tab labels/highlights) then draw the window.
        BindChrome();
        
        _renderer.DrawWidget(WindowId, _x, _y, Instance);

        // Tree: connectors first (under the tiles), then the stamped tiles.
        _tileRects.Clear();
        if (_activeTab >= 0 && _activeTab < SkillBookDefs.Tabs.Count)
        {
            var tab = SkillBookDefs.Tabs[_activeTab];
            var place = TreePlacement(tab);
            DrawConnectors(tab, place);
            foreach (var s in tab.Skills) StampTile(s, place);
        }

        if (_editLayout) DrawEditOverlay();
        if (EnableLayoutEditor) DrawLayoutToolbar();

        if (_toast != null) DrawToast();
        if (_hoverId != null) DrawHoverTooltip(sw, sh);
    }

    /// <summary>Edit-mode affordance: a draggable outline on each tile (brighter on the
    /// one being dragged) plus a one-line instruction above the toolbar.</summary>
    private void DrawEditOverlay()
    {
        foreach (var (id, rect) in _tileRects)
            DrawOutline(rect, id == _dragId ? new Color(255, 232, 150, 230)
                                            : new Color(180, 150, 90, 150));
        const string hint = "Drag skills to reposition - connectors follow. Save when done.";
        var sz = _renderer.MeasureText(hint, 15, "Roboto");
        _renderer.DrawText(hint, (int)(_x + (_w - sz.X) / 2), EditBtnRect().Y - 22,
            15, new Color(235, 215, 165), "Roboto");
    }

    private void DrawLayoutToolbar()
    {
        DrawButton(EditBtnRect(), _editLayout ? "Done" : "Edit Layout", _editLayout);
        if (_editLayout)
            DrawButton(SaveBtnRect(), _layoutDirty ? "Save *" : "Save", _layoutDirty);
    }

    private void DrawButton(Rectangle r, string label, bool active)
    {
        Scope.Draw(_pixel, r, new Color(40, 26, 14, 235));
        DrawOutline(r, active ? new Color(214, 182, 112) : new Color(120, 100, 70));
        var sz = _renderer.MeasureText(label, 15, "Roboto");
        _renderer.DrawText(label, (int)(r.X + (r.Width - sz.X) / 2), (int)(r.Y + (r.Height - sz.Y) / 2),
            15, active ? new Color(245, 230, 190) : new Color(200, 185, 150), "Roboto");
    }

    private void DrawOutline(Rectangle r, Color c)
    {
        Necroking.Render.DrawUtils.DrawRectBorder(_batch, _pixel, r, c);
    }

    /// <summary>Index of the TabBar child inside SkillBookWindow (cached). The tab
    /// widgets nest under it, addressed as "{Instance}.{tabBarIdx}.{i}".</summary>
    private int TabBarIndex()
    {
        if (_tabBarIdx >= 0) return _tabBarIdx;
        var def = _renderer.GetWidgetDef(WindowId);
        if (def?.Children != null)
            for (int i = 0; i < def.Children.Count; i++)
                if (def.Children[i].Name == "TabBar") { _tabBarIdx = i; break; }
        return _tabBarIdx;
    }

    private void BindChrome()
    {
        // Title is a static label in the widget data (TitleText child default,
        // "Ability Upgrades") — edited in the UI editor, no code needed.
        int barIdx = TabBarIndex();
        if (barIdx < 0) return;
        string barInst = $"{Instance}.{barIdx}";
        var tabs = SkillBookDefs.Tabs;
        int slots = SlotCount();
        var barDef = _renderer.GetWidgetDef("TabBar");
        int barWidth = barDef?.Width ?? 1020;

        // Visible = unlocked tabs that have a slot. Each gets an equal share of the
        // bar so any number of tabs fills the width without overflow or gaps.
        int visible = 0;
        for (int i = 0; i < tabs.Count && i < slots; i++)
            if (IsTabUnlocked(tabs[i])) visible++;
        int tabW = visible > 0 ? barWidth / visible : barWidth;

        for (int i = 0; i < slots; i++)
        {
            // Address the slot by its ACTUAL child name, not a computed "tab{i}" —
            // a gap in the widget data's naming (tab0..tab4,tab6..) once left a
            // never-addressed slot visible with its default "Tab 0/0" label.
            string slotName = barDef!.Children![i].Name;
            bool unlocked = i < tabs.Count && IsTabUnlocked(tabs[i]);
            // Hide locked tabs and any unused slots; the horizontal TabBar collapses
            // the gaps so the remaining tabs reflow flush.
            _renderer.SetHidden(barInst, slotName, !unlocked);
            if (!unlocked) continue;

            var tab = tabs[i];
            string tabInst = $"{barInst}.{i}";
            // Name is data-driven (DisplayName) so new tab files need no widget edits;
            // width is sized to fill the bar for the current tab count.
            _renderer.SetChildWidth(barInst, slotName, tabW);
            _renderer.SetText(tabInst, "Name", tab.DisplayName);
            var (learned, total) = _state?.GetProgress(tab) ?? (0, tab.Skills.Count);
            _renderer.SetText(tabInst, "Frac", $"{learned}/{total}");
            bool active = i == _activeTab;
            _renderer.SetElementTint(tabInst, "Backing",
                active ? Color.White : new Color(150, 140, 120));
        }
    }

    /// <summary>Number of tab slots the TabBar widget ships with (tab0..tabN).</summary>
    private int SlotCount() => _renderer.GetWidgetDef("TabBar")?.Children?.Count ?? 0;

    /// <summary>A tab is visible only when its unlock requirement is met. Empty
    /// requirement = always visible. "seen_item" checks the inventory's ever-seen
    /// registry; unknown gate types fail open (stay visible).</summary>
    private bool IsTabUnlocked(SkillTab tab)
    {
        if (string.IsNullOrEmpty(tab.UnlockType)) return true;
        return tab.UnlockType switch
        {
            "seen_item" => _inventory?.HasEverSeen(tab.UnlockId) ?? false,
            // Set when the necromancer morphs into UnlockId (MorphNecromancerEffect
            // records "morphed:<id>"); e.g. the Lich tab unlocks after becoming a lich.
            "morphed" => _state?.HasPassive($"morphed:{tab.UnlockId}") ?? false,
            _ => true,
        };
    }

    /// <summary>Snap the active tab to the first unlocked one if the current
    /// selection is locked or out of range, so we never render a hidden tab.</summary>
    private void EnsureActiveTabUnlocked()
    {
        var tabs = SkillBookDefs.Tabs;
        if (_activeTab >= 0 && _activeTab < tabs.Count && IsTabUnlocked(tabs[_activeTab])) return;
        for (int i = 0; i < tabs.Count; i++)
            if (IsTabUnlocked(tabs[i])) { _activeTab = i; return; }
    }

    /// <summary>Map a tab's logical skill coords into the window's page area (below
    /// the divider), scaled to fit, with the SkillTile drawn at native size.</summary>
    private (int ox, int oy, float scale, int tw, int th) TreePlacement(SkillTab tab)
    {
        var tileDef = _renderer.GetWidgetDef(TileId);
        int tw = tileDef?.Width ?? 322;
        int th = tileDef?.Height ?? 80;

        // Page area: from just below the header divider to the window bottom.
        var div = _renderer.GetChildRect(WindowId, "HeaderDivider", _x, _y, Instance);
        int pad = (int)(_w * 0.035f);
        int top = (div != Rectangle.Empty ? div.Bottom : _y + (int)(_h * 0.18f)) + pad;
        int left = _x + pad;
        int areaW = _w - pad * 2;
        int areaH = (_y + _h - pad) - top;

        float rangeW = Math.Max(1, tab.MaxX - tab.MinX);
        float rangeH = Math.Max(1, tab.MaxY - tab.MinY);
        float scale = Math.Min((areaW - tw) / rangeW, (areaH - th) / rangeH);
        if (scale <= 0) scale = 0.1f;
        int treeW = (int)(rangeW * scale), treeH = (int)(rangeH * scale);
        int ox = left + (areaW - treeW) / 2 - (int)(tab.MinX * scale);
        int oy = top + (areaH - treeH) / 2 - (int)(tab.MinY * scale);
        return (ox, oy, scale, tw, th);
    }

    private Rectangle NodeRect((int ox, int oy, float scale, int tw, int th) p, SkillDef s)
    {
        int cx = p.ox + (int)(s.X * p.scale);
        int cy = p.oy + (int)(s.Y * p.scale);
        return new Rectangle(cx - p.tw / 2, cy - p.th / 2, p.tw, p.th);
    }

    private void StampTile(SkillDef def, (int ox, int oy, float scale, int tw, int th) p)
    {
        var r = NodeRect(p, def);
        _tileRects.Add((def.Id, r));

        bool learned    = _state?.IsLearned(def.Id) ?? false;
        bool prereqsMet = _state?.ArePrereqsMet(def) ?? true;
        bool affordable = (_state != null && _inventory != null) && _state.CanAfford(def, _inventory);

        string inst = $"{Instance}.tile.{def.Id}";
        _renderer.ClearOverridesRecursive(inst);
        _renderer.SetText(inst, "name", def.Name);
        if (Has(SkillIcon(def))) _renderer.SetImage(inst, "icon", SkillIcon(def));

        // Cost line: the amount inline with the resource icon, coloured by whether
        // it's affordable. Item costs carry the item's icon; skillpoints / event
        // milestones have no art so they read "Cost N" with the icon hidden.
        //   learned -> "Learned"   no costs -> "Free"
        string costIcon = "";
        string costText;
        Color costCol;
        if (learned) { costText = "Learned"; costCol = new Color(196, 180, 146); }
        else if (def.Costs.Count == 0) { costText = "Free"; costCol = affordable ? CostGood : CostBad; }
        else
        {
            var c = def.Costs[0];
            string plus = def.Costs.Count > 1 ? "+" : "";
            costIcon = CostIcon(c);
            costText = Has(costIcon) ? $"{c.Amount}{plus}" : $"Cost {c.Amount}{plus}";
            costCol = !prereqsMet ? new Color(130, 108, 70) : affordable ? CostGood : CostBad;
        }
        _renderer.SetText(inst, "cost", costText);
        _renderer.SetTextColor(inst, "cost", costCol.R, costCol.G, costCol.B, costCol.A);
        bool showCostIcon = Has(costIcon);
        _renderer.SetHidden(inst, "cost_icon", !showCostIcon);
        if (showCostIcon) _renderer.SetImage(inst, "cost_icon", costIcon);

        // State wash via the frame tint (kept subtle; bright parchment otherwise).
        var tint = learned ? new Color(150, 170, 150)
                 : !prereqsMet ? new Color(120, 110, 96)
                 : affordable ? Color.White
                 : new Color(210, 200, 180);
        _renderer.SetElementTint(inst, "frame", tint);

        _renderer.DrawWidget(TileId, r.X, r.Y, inst);
    }

    private void DrawConnectors(SkillTab tab, (int ox, int oy, float scale, int tw, int th) p)
    {
        foreach (var s in tab.Skills)
        {
            foreach (var pid in s.Parents)    Edge(tab, s, pid, p);
            foreach (var pid in s.ParentsAny) Edge(tab, s, pid, p);
        }
    }

    private void Edge(SkillTab tab, SkillDef child, string parentId,
        (int ox, int oy, float scale, int tw, int th) p)
    {
        int pi = tab.IndexOf(parentId);
        if (pi < 0) return;
        var pr = NodeRect(p, tab.Skills[pi]);
        var cr = NodeRect(p, child);
        var a = new Vector2(pr.X + pr.Width / 2f, pr.Bottom);
        var b = new Vector2(cr.X + cr.Width / 2f, cr.Y);
        bool childLearned = _state?.IsLearned(child.Id) ?? false;
        Color core = childLearned ? new Color(218, 184, 96) : new Color(158, 126, 72);
        DrawCurve(a, b, new Color(0, 0, 0, 170), childLearned ? 5 : 4);
        DrawCurve(a, b, core, childLearned ? 2 : 1);
    }

    private void DrawCurve(Vector2 a, Vector2 b, Color color, int thickness)
    {
        var c1 = new Vector2(a.X, a.Y + (b.Y - a.Y) * 0.5f);
        var c2 = new Vector2(b.X, b.Y - (b.Y - a.Y) * 0.5f);
        Vector2 prev = a;
        for (int i = 1; i <= 22; i++)
        {
            float t = i / 22f, u = 1 - t;
            var q = u * u * u * a + 3 * u * u * t * c1 + 3 * u * t * t * c2 + t * t * t * b;
            ThickLine(prev, q, color, thickness);
            prev = q;
        }
    }

    private void ThickLine(Vector2 a, Vector2 b, Color color, int thickness)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 0.5f) return;
        float angle = MathF.Atan2(dy, dx);
        for (int t = 0; t < thickness; t++)
            Scope.Draw(_pixel, new Rectangle((int)a.X, (int)a.Y - thickness / 2 + t, (int)len, 1),
                null, color, angle, Vector2.Zero, SpriteEffects.None, 0f);
    }

    // Cost affordability colours (match the old grimoire panel).
    private static readonly Color CostGood = new(60, 130, 56);
    private static readonly Color CostBad  = new(168, 44, 44);

    private bool Has(string? s) => !string.IsNullOrEmpty(s);

    /// <summary>Icon for a cost's resource. Item costs use the item's art; skillpoints
    /// and event milestones have none (the amount reads "Cost N" on its own).</summary>
    private string CostIcon(SkillCost c)
    {
        if (c.Type == "item")
        {
            var it = _gameData.Items?.Get(c.Id);
            if (it != null && Has(it.Icon)) return it.Icon;
        }
        return "";
    }

    private string SkillIcon(SkillDef def)
    {
        string rel = $"assets/UI/Icons/Skills/{def.Id}.png";
        if (System.IO.File.Exists(GamePaths.Resolve(rel))) return rel;
        foreach (var c in def.Costs)
            if (c.Type == "item")
            {
                var it = _gameData.Items?.Get(c.Id);
                if (it != null && !string.IsNullOrEmpty(it.Icon)) return it.Icon;
            }
        return Data.Registries.MagicPathHelpers.IconPath(Data.Registries.MagicPath.Death, 24);
    }

    private void TryLearn(string id, double timeSec)
    {
        SkillDef? def = null;
        foreach (var tab in SkillBookDefs.Tabs) { int i = tab.IndexOf(id); if (i >= 0) { def = tab.Skills[i]; break; } }
        if (def == null || _state == null) return;
        if (_state.IsLearned(def.Id)) { Toast("Already learned.", timeSec); return; }
        if (_state.IsExcluded(def))   { Toast("Locked - mutually exclusive.", timeSec); return; }
        if (!_state.ArePrereqsMet(def)) { Toast("Locked - earlier skills required.", timeSec); return; }
        if (!_state.CanAfford(def, _inventory)) { Toast("Not enough resources.", timeSec); return; }
        var ctx = new SkillEffectContext
        {
            Inventory = _inventory, GameData = _gameData,
            Bar = _bar,
            BookState = _state, Sim = _sim,
        };
        if (_state.TryLearn(def, ctx)) Toast($"Learned: {def.Name}", timeSec);
    }

    private void Toast(string msg, double timeSec) { _toast = msg; _toastUntil = timeSec + 2.2; }

    /// <summary>Skill name + cost lines, anchored near the cursor. The cost reads as a
    /// word ("5 Bone Dust") rather than a bare number; resources without a real
    /// inventory item (event milestones, skill points, unknown ids) fall back to a
    /// ticker-style code so they're still identifiable. Affordable costs are green,
    /// unaffordable red.</summary>
    private void DrawHoverTooltip(int sw, int sh)
    {
        var def = _state?.FindSkill(_hoverId!);
        if (def == null) return;

        const int titleSize = 18, lineSize = 15, padX = 12, padY = 10, gap = 6, lineGap = 3, indentPx = 14;
        var lines = CostLines(def);
        AppendEffectLines(def, lines);

        var titleSz = _renderer.MeasureText(def.Name, titleSize, "Roboto");
        int innerW = (int)titleSz.X;
        var lineSizes = new List<Vector2>(lines.Count);
        foreach (var (text, _, indent) in lines)
        {
            var s = _renderer.MeasureText(text, lineSize, "Roboto");
            lineSizes.Add(s);
            int lineW = (int)s.X + indent * indentPx;
            if (lineW > innerW) innerW = lineW;
        }

        int w = innerW + padX * 2;
        int h = padY * 2 + (int)titleSz.Y + gap;
        foreach (var s in lineSizes) h += (int)s.Y + lineGap;

        // Anchor below-right of the cursor; flip / clamp to keep it on-screen.
        int tx = (int)_hoverMouse.X + 18, ty = (int)_hoverMouse.Y + 18;
        if (tx + w > sw) tx = (int)_hoverMouse.X - 18 - w;
        if (ty + h > sh) ty = sh - h - 4;
        if (tx < 4) tx = 4;
        if (ty < 4) ty = 4;

        var bg = new Rectangle(tx, ty, w, h);
        Scope.Draw(_pixel, bg, new Color(26, 13, 8, 240));
        DrawOutline(bg, new Color(120, 96, 56));

        int cy = ty + padY;
        _renderer.DrawText(def.Name, tx + padX, cy, titleSize, new Color(240, 226, 186), "Roboto");
        cy += (int)titleSz.Y + gap;
        for (int i = 0; i < lines.Count; i++)
        {
            _renderer.DrawText(lines[i].text, tx + padX + lines[i].indent * indentPx, cy,
                lineSize, lines[i].col, "Roboto");
            cy += (int)lineSizes[i].Y + lineGap;
        }
    }

    /// <summary>One coloured line per cost (or a single "Learned" / "Free" line).
    /// All cost lines sit at indent 0.</summary>
    private List<(string text, Color col, int indent)> CostLines(SkillDef def)
    {
        var lines = new List<(string, Color, int)>();
        if (_state?.IsLearned(def.Id) ?? false) { lines.Add(("Learned", new Color(150, 196, 150), 0)); return lines; }
        if (def.Costs.Count == 0) { lines.Add(("Free", CostGood, 0)); return lines; }
        foreach (var c in def.Costs)
        {
            int have = ResourceHave(c);
            bool met = have >= c.Amount;
            // "{resource} {have}/{need}" — shows current tally against the requirement,
            // so milestone events (MONSTER KILL 3/1) and pools (MONSTROLOGY points 0/5)
            // read as progress, not just a target number.
            lines.Add(($"{CostResourceName(c)} {have}/{c.Amount}", met ? CostGood : CostBad, 0));
        }
        return lines;
    }

    // Effect-line colours: a gold "Effect:" header, lavender body rows.
    private static readonly Color EffectHeader = new(196, 180, 146);
    private static readonly Color EffectBody   = new(176, 166, 204);

    /// <summary>Append a human-readable description of the node's effect. A
    /// <c>compound</c> effect ("a=x|b=y|…") becomes an "Effect:" header followed by
    /// one indented row per sub-effect; a single effect is one "Effect: …" line.
    /// The inert <c>noop</c> placeholder adds nothing.</summary>
    private void AppendEffectLines(SkillDef def, List<(string text, Color col, int indent)> lines)
    {
        if (string.IsNullOrEmpty(def.Effect) || def.Effect == "noop") return;

        if (def.Effect == "compound")
        {
            lines.Add(("Effect:", EffectHeader, 0));
            foreach (var entry in (def.EffectArg ?? "").Split('|'))
            {
                var t = entry.Trim();
                if (t.Length == 0) continue;
                int eq = t.IndexOf('=');
                string sub    = eq >= 0 ? t.Substring(0, eq).Trim() : t;
                string subArg = eq >= 0 ? t.Substring(eq + 1)       : "";
                lines.Add(("• " + DescribeEffect(sub, subArg), EffectBody, 1));
            }
        }
        else
        {
            lines.Add(($"Effect: {DescribeEffect(def.Effect, def.EffectArg)}", EffectHeader, 0));
        }
    }

    /// <summary>Render one effect+arg pair as a readable phrase, resolving ids to
    /// display names where possible. Falls back to a humanized "effect: arg" for
    /// unknown effects so nothing ever shows as a blank.</summary>
    private string DescribeEffect(string effect, string? rawArg)
    {
        string arg = (rawArg ?? "").Trim();
        switch (effect)
        {
            case "add_spell":            return $"Add spell: {SpellName(arg)}";
            case "unlock_potion":        return $"Unlock potion: {ItemName(arg)}";
            case "unlock_potion_slot":   { int n = ParseInt(arg, 1); return $"+{n} potion slot{(n == 1 ? "" : "s")}"; }
            case "unlock_summon":        return $"Unlock summon: {NameList(arg, UnitName)}";
            case "unlock_unit":          return $"Unlock unit: {UnitName(arg)}";
            case "unlock_building":      return $"Unlock building: {Humanize(arg)}";
            case "unlock_ai_behavior":   return DescribeAi(arg);
            case "cap_buff":             return DescribeCap(arg);
            case "grant_intrinsic_buff": return DescribeIntrinsic(arg);
            case "passive_stat":         return $"Passive: {Humanize(arg)}";
            case "morph_necromancer":    return $"Become {UnitName(arg)}";
            case "metamorph_action":     return $"Unlock action: {Humanize(arg)}";
            case "noop":                 return "—";
            default:                     return string.IsNullOrEmpty(arg) ? Humanize(effect) : $"{Humanize(effect)}: {arg}";
        }
    }

    // "behavior" or "behavior:N" -> "Unlock ability: Behavior (xN)".
    private string DescribeAi(string arg)
    {
        int colon = arg.IndexOf(':');
        string behavior = colon >= 0 ? arg.Substring(0, colon) : arg;
        int n = colon >= 0 ? ParseInt(arg.Substring(colon + 1), 1) : 1;
        string tail = n > 1 ? $" (x{n})" : "";
        return $"Unlock ability: {Humanize(behavior)}{tail}";
    }

    // "monster:N" / "human:N" -> "+N monster cap".
    private string DescribeCap(string arg)
    {
        int colon = arg.IndexOf(':');
        string kind = colon >= 0 ? arg.Substring(0, colon).Trim() : arg.Trim();
        int n = colon >= 0 ? ParseInt(arg.Substring(colon + 1), 1) : 1;
        return $"+{n} {Humanize(kind)} cap";
    }

    // "buff_id:tag1,tag2" -> "Grant Buff Name to tag1, tag2".
    private string DescribeIntrinsic(string arg)
    {
        int colon = arg.IndexOf(':');
        string buff = colon >= 0 ? arg.Substring(0, colon).Trim() : arg.Trim();
        string tags = colon >= 0 ? arg.Substring(colon + 1) : "";
        string who  = string.IsNullOrWhiteSpace(tags) ? "all units" : string.Join(", ", SplitTrim(tags));
        return $"Grant {BuffName(buff)} to {who}";
    }

    private string SpellName(string id) => _gameData.Spells?.NameOf(id) ?? id;
    private string ItemName(string id)  => _gameData.Items?.NameOf(id) ?? id;
    private string UnitName(string id)  => _gameData.Units?.NameOf(id) ?? id;
    // Buffs keep their extra flavor: a nameless buff humanizes its id instead.
    private string BuffName(string id)  { var n = _gameData.Buffs?.NameOf(id) ?? id; return n == id ? Humanize(id) : n; }

    private string NameList(string csv, Func<string, string> map)
        => string.Join(", ", System.Linq.Enumerable.Select(SplitTrim(csv), map));

    private static IEnumerable<string> SplitTrim(string csv)
    {
        foreach (var p in csv.Split(','))
        {
            var t = p.Trim();
            if (t.Length > 0) yield return t;
        }
    }

    private static int ParseInt(string s, int dflt) => int.TryParse(s.Trim(), out var v) ? v : dflt;

    /// <summary>Turn a raw id into title-case words: "unholy_strength" -> "Unholy Strength".</summary>
    private static string Humanize(string id)
    {
        if (string.IsNullOrEmpty(id)) return "";
        var words = id.Replace('_', ' ').Replace('-', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
            words[i] = char.ToUpperInvariant(words[i][0]) + words[i].Substring(1);
        return string.Join(' ', words);
    }

    /// <summary>How much of a cost's resource the player currently has: item count
    /// from the inventory, event tally from the milestone tracker, or skill-point pool.</summary>
    private int ResourceHave(SkillCost c)
    {
        if (c.Type == "item")        return _inventory?.GetItemCount(c.Id) ?? 0;
        if (c.Type == "event")       return _state?.Events.Get(c.Id) ?? 0;
        if (c.Type == "skillpoints") return _state?.GetSkillPoints(c.Id) ?? 0;
        return 0;
    }

    /// <summary>Human-readable name for a cost's resource. Item costs resolve to the
    /// item's display name; anything without a real inventory item (event milestones,
    /// skill-point pools, unknown ids) falls back to a ticker-style upper-case code.</summary>
    private string CostResourceName(SkillCost c)
    {
        if (c.Type == "item")
        {
            var it = _gameData.Items?.Get(c.Id);
            if (it != null && Has(it.DisplayName)) return it.DisplayName;
        }
        else if (c.Type == "skillpoints")
        {
            return $"{Ticker(c.Id)} points";
        }
        return Ticker(c.Id);
    }

    /// <summary>Ticker form of a raw id: upper-case with separators spaced out, e.g.
    /// "bone_dust" -> "BONE DUST". Used when no friendly display name exists.</summary>
    private static string Ticker(string id)
        => string.IsNullOrEmpty(id) ? "?" : id.Replace('_', ' ').Replace('-', ' ').ToUpperInvariant();

    private void DrawToast()
    {
        if (_toast == null) return;
        const int fontSize = 18;
        var sz = _renderer.MeasureText(_toast, fontSize, "Roboto");
        int w = Math.Min(_w - 40, (int)sz.X + 36), h = 32;
        // Keep the band on-screen even when the window is taller than the display.
        int bottom = Math.Min(_y + _h, _sh);
        var r = new Rectangle(_x + (_w - w) / 2, bottom - 72, w, h);
        Scope.Draw(_pixel, r, new Color(26, 13, 8, 235));
        _renderer.DrawText(_toast, (int)(r.X + (w - sz.X) / 2), (int)(r.Y + (h - sz.Y) / 2),
            fontSize, new Color(236, 221, 179), "Roboto");
    }
}
