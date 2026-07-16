using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Core;

namespace Necroking.UI;

/// <summary>
/// Shared skeleton for the left-anchored, vertically-stacked "pick an item"
/// side menus (building placement, potion crafting). Owns the <b>mechanics</b>:
/// open/close/toggle + the x=-12 anchor and stretch-to-screen-height, the widget
/// child pool (<see cref="EnsureItemChildren"/> / <see cref="SyncItems"/>), the
/// item hit rects (<see cref="ComputeItemRects"/> — driven by the same shared
/// WidgetLayoutUtils pass the renderer draws with, so draw and hit-test can't
/// desync), the click hit-test, the hover / selected / can't-afford overlays,
/// and <see cref="IModalLayer"/> footprint. Subclasses own the <b>content</b>:
/// which items exist, how each binds, affordability, and what a click does.
///
/// NOTE: TableCraftMenuUI is deliberately NOT a subclass — it is a
/// world-anchored, camera-zoom-scaled popover with different layout entirely.
///
/// Registered with the router via a PanelLayer wrapper over this IModalLayer;
/// subclasses keep their existing public entry points (Init / Update / draw)
/// so that wiring is untouched.
/// </summary>
public abstract class SideListMenu : IModalLayer
{
    protected RuntimeWidgetRenderer _renderer = null!;
    protected SpriteBatch _batch = null!;
    protected Render.SpriteScope Scope => _batch;  // straight-alpha draw surface (implicit conversion)
    protected Texture2D _pixel = null!;

    protected bool _visible;
    protected InputState? _lastInput;
    protected int _screenX, _screenY;
    protected int _widgetW, _widgetH;

    // Which widget children are the item rows.
    protected int[] _itemChildIndices = Array.Empty<int>();

    // === Content hooks — subclass supplies the data ===

    /// <summary>Widget template id for the whole menu panel.</summary>
    protected abstract string MenuWidgetId { get; }
    /// <summary>Widget template id for one item row.</summary>
    protected abstract string ItemWidgetId { get; }
    /// <summary>Override instance id for this menu's widget overrides.</summary>
    protected abstract string InstanceId { get; }
    /// <summary>Prefix for generated child names (e.g. "building_", "recipe_").</summary>
    protected abstract string ItemChildPrefix { get; }
    /// <summary>How many items the menu currently lists.</summary>
    protected abstract int ItemCount { get; }
    /// <summary>Bind item <paramref name="index"/>'s content into the widget
    /// overrides under <paramref name="subId"/> (name, icons, costs, …).</summary>
    protected abstract void BindItem(string subId, int index);
    /// <summary>Can the player afford item <paramref name="index"/>?</summary>
    protected abstract bool CanAfford(int index);
    /// <summary>An affordable item row was clicked.</summary>
    protected abstract void OnItemClicked(int index);
    /// <summary>Should item <paramref name="index"/> show the green selected border?</summary>
    protected virtual bool IsItemSelected(int index) => false;
    /// <summary>Extra per-row draw after the overlays (e.g. a craft progress bar).</summary>
    protected virtual void DrawItemExtras(Rectangle rect, int index) { }

    // Subclasses own their concrete open/close (they reset their own selection /
    // progress state and cache their own item source before RebuildItems()).
    public abstract void Open(int screenW, int screenH);
    public abstract void Close();

    // === Lifecycle ===

    public bool IsVisible => _visible;

    public void Toggle(int screenW, int screenH)
    {
        if (_visible) Close();
        else Open(screenW, screenH);
    }

    /// <summary>Common Open prologue: show, left-anchor (x=-12), and stretch the
    /// panel to the screen height. Returns after positioning; the subclass then
    /// caches its item source and calls <see cref="RebuildItems"/>.</summary>
    protected void OpenAnchorStretch(int screenH)
    {
        _visible = true;
        _screenX = -12;
        _screenY = 0;
        var def = _renderer.GetWidgetDef(MenuWidgetId);
        if (def != null)
        {
            def.Height = screenH;
            _widgetH = screenH;
            _widgetW = def.Width;
        }
    }

    /// <summary>Rebuild the item child pool to the current <see cref="ItemCount"/>
    /// and push each item's data into the widget overrides.</summary>
    protected void RebuildItems()
    {
        var def = _renderer.GetWidgetDef(MenuWidgetId);
        if (def != null) EnsureItemChildren(def);
        SyncItems();
    }

    // === IModalLayer — side-panel: not light-dismiss, non-blocking (gameplay /
    // placement clicks reach the world). PopupManager still eats inside-panel
    // clicks so the spell bar / world-edit code don't dual-fire. ===
    public bool LightDismiss => false;
    public bool IsBlocking => false;
    public void OnCancel() => Close();

    public bool ContainsMouse(int mouseX, int mouseY)
    {
        if (!_visible) return false;
        return mouseX >= _screenX && mouseX < _screenX + _widgetW &&
               mouseY >= _screenY && mouseY < _screenY + _widgetH;
    }

    public Rectangle? HitBounds(int screenW, int screenH)
        => _visible ? new Rectangle(_screenX, _screenY, _widgetW, _widgetH) : null;

    // === Widget child pool ===

    // Item-cell size cached from the first template child seen. The rebuild
    // removes ALL item children and re-adds ItemCount clones, so an open with
    // zero items would otherwise destroy the def's template — every later open
    // would fall back to the hardcoded size and mis-wrap the grid.
    private int _templateW, _templateH;

    /// <summary>Ensure the widget def has exactly <see cref="ItemCount"/> item
    /// children, cloning the template's dimensions.</summary>
    private void EnsureItemChildren(Editor.UIEditorWidgetDef def)
    {
        for (int i = 0; i < def.Children.Count; i++)
        {
            if (def.Children[i].Widget == ItemWidgetId)
            {
                _templateW = def.Children[i].Width;
                _templateH = def.Children[i].Height;
                break;
            }
        }
        int templateW = _templateW > 0 ? _templateW : 218;
        int templateH = _templateH > 0 ? _templateH : 68;

        def.Children.RemoveAll(c => c.Widget == ItemWidgetId);

        int needed = ItemCount;
        var itemIndices = new List<int>();
        for (int i = 0; i < needed; i++)
        {
            var child = new Editor.UIEditorChildDef
            {
                Name = $"{ItemChildPrefix}{i}",
                Widget = ItemWidgetId,
                Width = templateW,
                Height = templateH,
                Anchor = 0,
            };
            def.Children.Add(child);
            itemIndices.Add(def.Children.Count - 1);
        }

        _itemChildIndices = itemIndices.ToArray();
    }

    /// <summary>Push each item's data into the widget override system; empty
    /// out any surplus child slots.</summary>
    protected void SyncItems()
    {
        if (_itemChildIndices.Length == 0) return;

        for (int i = 0; i < _itemChildIndices.Length; i++)
        {
            int childIdx = _itemChildIndices[i];
            string subId = $"{InstanceId}.{childIdx}";

            if (i >= ItemCount)
            {
                _renderer.SetChildWidget(InstanceId, childIdx, "Item Slot_Empty");
                _renderer.ClearOverrides(subId);
                continue;
            }

            _renderer.SetChildWidget(InstanceId, childIdx, ItemWidgetId);
            BindItem(subId, i);
        }
    }

    // === Layout / hit-test ===

    /// <summary>Item rects from the same shared layout pass the renderer draws
    /// with (<see cref="WidgetLayoutUtils.ComputeLayoutRects"/> — vertical,
    /// horizontal and wrapped grid layouts alike). Both the overlay draw and
    /// the click hit-test read it, so they can never desync.</summary>
    protected List<Rectangle> ComputeItemRects(Editor.UIEditorWidgetDef def)
    {
        var layoutRects = WidgetLayoutUtils.ComputeLayoutRects(def, _screenX, _screenY);

        var itemRects = new List<Rectangle>();
        for (int i = 0; i < _itemChildIndices.Length && i < ItemCount; i++)
        {
            int childIdx = _itemChildIndices[i];
            if (childIdx >= layoutRects.Count) break;
            itemRects.Add(layoutRects[childIdx]);
        }
        return itemRects;
    }

    /// <summary>Left-press hit-test over the item rows; an affordable hit fires
    /// <see cref="OnItemClicked"/>. PopupManager has already consumed the
    /// inside-panel click for this layer.</summary>
    protected void HandleItemClick(InputState input)
    {
        if (!input.LeftPressed) return;

        int mx = (int)input.MousePos.X, my = (int)input.MousePos.Y;
        int menuRight = _screenX + _widgetW;
        if (mx >= menuRight || my < 0) return;

        var def = _renderer.GetWidgetDef(MenuWidgetId);
        if (def == null) return;

        var rects = ComputeItemRects(def);
        for (int i = 0; i < rects.Count && i < ItemCount; i++)
        {
            if (rects[i].Contains(mx, my))
            {
                if (CanAfford(i)) OnItemClicked(i);
                break;
            }
        }
    }

    // === Overlays ===

    /// <summary>Draw the hover highlight, selected border and can't-afford dim
    /// for every item row (plus each subclass's <see cref="DrawItemExtras"/>).
    /// Returns the hovered item index (or -1) so callers can drive a tooltip.</summary>
    protected int DrawItemOverlays()
    {
        var def = _renderer.GetWidgetDef(MenuWidgetId);
        if (def == null) return -1;

        var rects = ComputeItemRects(def);
        int hovered = -1;
        for (int i = 0; i < rects.Count && i < ItemCount; i++)
        {
            bool canAfford = CanAfford(i);

            if (_lastInput != null)
            {
                int hmx = (int)_lastInput.MousePos.X, hmy = (int)_lastInput.MousePos.Y;
                if (rects[i].Contains(hmx, hmy))
                {
                    hovered = i;
                    if (canAfford)
                        Scope.Draw(_pixel, rects[i], new Color(255, 255, 255, 26));
                }
            }

            if (IsItemSelected(i))
                Necroking.Render.DrawUtils.DrawRectBorder(_batch, _pixel, rects[i], new Color(100, 255, 100, 80), 2);

            if (!canAfford)
                Scope.Draw(_pixel, rects[i], new Color(0, 0, 0, 80));

            DrawItemExtras(rects[i], i);
        }
        return hovered;
    }
}
