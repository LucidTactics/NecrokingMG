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

    // The window's five tab slots, in SkillBookDefs.Tabs order. The tabs live in a
    // horizontal-layout "TabBar" sub-widget; each tab is a "SkillBookTab" widget
    // (Backing / Name / Frac / Frame) instanced as TabBar child "tab{i}". Their data
    // is bound through the nested instance path "{Instance}.{tabBarIdx}.{i}".
    private static readonly string[] TabSlots =
        { "Potions", "Monstrology", "Necromancy", "Magic", "Metamorphosis" };

    private RuntimeWidgetRenderer _renderer = null!;
    private SpriteBatch _batch = null!;
    private Texture2D _pixel = null!;

    private SkillBookState _state = null!;
    private Inventory _inventory = null!;
    private GameData _gameData = null!;
    private SpellBarState _primaryBar;
    private SpellBarState _secondaryBar;
    private Simulation? _sim;

    private int _x, _y, _w, _h;
    private int _activeTab;
    private string? _toast;
    private double _toastUntil;

    // Cached index of the TabBar child within SkillBookWindow. The five tabs are
    // nested child-widgets of it, so they're addressed as "{Instance}.{tabBarIdx}.{i}".
    private int _tabBarIdx = -1;

    // Per-skill stamped tile rects this frame (for hit-testing clicks).
    private readonly List<(string id, Rectangle rect)> _tileRects = new();

    public bool IsVisible { get; private set; }

    public void Init(RuntimeWidgetRenderer renderer, SpriteBatch batch, Texture2D pixel)
    {
        _renderer = renderer;
        _batch = batch;
        _pixel = pixel;
    }

    public void Bind(SkillBookState state, Inventory inv, GameData gd,
        SpellBarState primaryBar, SpellBarState secondaryBar, Simulation? sim = null)
    {
        _state = state; _inventory = inv; _gameData = gd;
        _primaryBar = primaryBar; _secondaryBar = secondaryBar; _sim = sim;
    }

    public void Toggle() { if (IsVisible) Close(); else Open(); }
    public void Open()  { IsVisible = true;  Game1.Popups.Push(this); }
    public void Close() { IsVisible = false; Game1.Popups.Pop(this); }

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
    public bool TryLearnById(string id, double timeSec = 0)
    {
        TryLearn(id, timeSec);
        return _state?.IsLearned(id) ?? false;
    }

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
    }

    public void Update(InputState input, int sw, int sh, double timeSec)
    {
        if (!IsVisible) return;
        if (_toast != null && timeSec >= _toastUntil) _toast = null;
        Layout(sw, sh);
        int mx = (int)input.MousePos.X, my = (int)input.MousePos.Y;
        if (!ContainsMouse(mx, my)) return;
        input.MouseOverUI = true;
        if (!input.LeftPressed) return;

        // Tab clicks: tabs are nested child-widgets of the TabBar, so resolve the
        // TabBar's screen rect first, then each tab's laid-out rect within it.
        int barIdx = TabBarIndex();
        if (barIdx >= 0)
        {
            var bar = _renderer.GetChildRect(WindowId, "TabBar", _x, _y, Instance);
            string barInst = $"{Instance}.{barIdx}";
            for (int i = 0; i < SkillBookDefs.Tabs.Count && i < TabSlots.Length; i++)
            {
                var r = _renderer.GetChildRect("TabBar", $"tab{i}", bar.X, bar.Y, barInst);
                if (r != Rectangle.Empty && r.Contains(mx, my)) { _activeTab = i; return; }
            }
        }
        // Tile clicks (the rects we stamped last Draw).
        foreach (var (id, rect) in _tileRects)
            if (rect.Contains(mx, my)) { TryLearn(id, timeSec); return; }
    }

    public void Draw(int sw, int sh)
    {
        if (!IsVisible) return;
        Layout(sw, sh);

        // Dim the world behind the modal.
        _batch.Draw(_pixel, new Rectangle(0, 0, sw, sh), new Color(0, 0, 0, 180));

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

        if (_toast != null) DrawToast();
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
        _renderer.SetText(Instance, "TitleText", "TOME OF THE NECROKING");
        int barIdx = TabBarIndex();
        if (barIdx < 0) return;
        for (int i = 0; i < TabSlots.Length && i < SkillBookDefs.Tabs.Count; i++)
        {
            var tab = SkillBookDefs.Tabs[i];
            var (learned, total) = _state?.GetProgress(tab) ?? (0, tab.Skills.Count);
            string tabInst = $"{Instance}.{barIdx}.{i}";
            // The tab name is a static per-tab label carried in the widget data
            // (TabBar child override → SkillBookTab "Name"), so it renders in both the
            // game and the editor without code. Only the dynamic bits are bound here.
            _renderer.SetText(tabInst, "Frac", $"{learned}/{total}");
            bool active = i == _activeTab;
            _renderer.SetElementTint(tabInst, "Backing",
                active ? Color.White : new Color(150, 140, 120));
        }
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

        // Cost line: flat "Cost N" (or Free / Learned), coloured by state.
        string costText = learned ? "Learned"
                        : def.Costs.Count == 0 ? "Free"
                        : $"Cost {def.Costs[0].Amount}";
        _renderer.SetText(inst, "cost", costText);

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
            _batch.Draw(_pixel, new Rectangle((int)a.X, (int)a.Y - thickness / 2 + t, (int)len, 1),
                null, color, angle, Vector2.Zero, SpriteEffects.None, 0f);
    }

    private bool Has(string? s) => !string.IsNullOrEmpty(s);

    private string SkillIcon(SkillDef def)
    {
        string rel = $"assets/UI/Icons/Skills/{def.Id}.png";
        if (System.IO.File.Exists(GamePaths.Resolve(rel))) return rel;
        foreach (var c in def.Costs)
            if (c.Type == "item")
            {
                var it = _gameData?.Items?.Get(c.Id);
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
            PrimaryBar = _primaryBar, SecondaryBar = _secondaryBar,
            BookState = _state, Sim = _sim,
        };
        if (_state.TryLearn(def, ctx)) Toast($"Learned: {def.Name}", timeSec);
    }

    private void Toast(string msg, double timeSec) { _toast = msg; _toastUntil = timeSec + 2.2; }

    private void DrawToast()
    {
        // Minimal toast band near the bottom of the window.
        var c = new Color(26, 13, 8);
        int w = Math.Min(_w - 40, 360), h = 28;
        var r = new Rectangle(_x + (_w - w) / 2, _y + _h - 60, w, h);
        _batch.Draw(_pixel, r, c);
        // (text drawn by the widget-font path would go here; toast text is optional.)
    }
}
