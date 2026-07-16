using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Necroking.Core;
using Necroking.Data.Registries;
using Necroking.Game;
using Necroking.GameSystems;

namespace Necroking.UI;

/// <summary>
/// Bridges the slot-based Inventory to the widget-based UI.
/// Uses the "EquipmentWindow" widget as a template, dynamically
/// populating slots with item icons and quantities.
///
/// The widget template defines the visual style (nine-slices, fonts, colors).
/// This class owns the data binding — which slots show which items.
/// Cosmetic edits in the widget editor flow through automatically.
/// </summary>
public class InventoryUI : IModalLayer
{
    private const string WidgetId = "EquipmentWindow";
    private const string FilledSlotWidget = "Item Slot";
    private const string EmptySlotWidget = "Item Slot_Empty";
    private const string InstanceId = "inventory";

    // Slot child naming: layout children that aren't the title
    // We find them by checking which children reference Item Slot / Item Slot_Empty widgets

    private RuntimeWidgetRenderer _renderer;
    private Inventory _inventory;
    private ItemRegistry _items;
    private SpriteBatch? _batch;
    private Render.SpriteScope Scope => _batch!;  // straight-alpha draw surface (implicit conversion)
    private Texture2D? _pixel;
    private InputState? _lastInput;

    private bool _visible;
    private int _screenX, _screenY;
    private int _widgetW, _widgetH;

    // Rich tooltip styling + mechanics live in UI/RichTip.cs (shared with the
    // crafting menu and character-stats tooltips). This is the RICH tooltip;
    // HUDRenderer.DrawCursorTooltip is the separate SIMPLE plain-string one.

    // Slot mapping: which widget child indices are inventory slots
    private int[] _slotChildIndices = Array.Empty<int>();
    private int _titleChildIndex = -1;

    // Dragging (future)
    private bool _dragging;
    private int _dragOffsetX, _dragOffsetY;

    /// <summary>Invoked with the slot index when a filled slot is left-clicked.
    /// The owner decides what the click does (deposit into an open table, use a
    /// consumable, etc.). Handled here because the inventory is a modal layer —
    /// PopupManager consumes inside-panel clicks before they reach Game1's Update,
    /// so the layer itself must dispatch them.</summary>
    public Action<int>? OnSlotClicked;

    public bool IsVisible => _visible;
    /// <summary>Window-drag in progress — the router captures the mouse for us.</summary>
    public bool IsDragging => _dragging;
    public int Width => _widgetW;
    public int Height => _widgetH;
    public RuntimeWidgetRenderer Renderer => _renderer;
    public int[] SlotChildIndices => _slotChildIndices;

    public void Init(RuntimeWidgetRenderer renderer, Inventory inventory, ItemRegistry items,
        SpriteBatch? batch = null, Texture2D? pixel = null)
    {
        _renderer = renderer;
        _inventory = inventory;
        _items = items;
        _batch = batch;
        _pixel = pixel;

        var def = renderer.GetWidgetDef(WidgetId);
        if (def == null) return;

        _widgetW = def.Width;
        _widgetH = def.Height;

        // Identify which children are slot widgets vs title/other
        var slotIndices = new System.Collections.Generic.List<int>();
        for (int i = 0; i < def.Children.Count; i++)
        {
            var child = def.Children[i];
            if (child.Widget == FilledSlotWidget || child.Widget == EmptySlotWidget)
                slotIndices.Add(i);
            else if (!string.IsNullOrEmpty(child.Element))
                _titleChildIndex = i; // Title or other non-slot element
        }
        _slotChildIndices = slotIndices.ToArray();

        // Expand to match inventory slot count if needed
        // The widget template has a few slots; we dynamically add more
        EnsureSlotChildren(def);
    }

    /// <summary>Ensure the widget def has enough children for all inventory slots.</summary>
    private void EnsureSlotChildren(Editor.UIEditorWidgetDef def)
    {
        int needed = _inventory.SlotCount;
        int existing = _slotChildIndices.Length;

        if (existing >= needed) return;

        // Clone the template slot (first slot child) to create additional slots
        Editor.UIEditorChildDef? template = null;
        if (_slotChildIndices.Length > 0)
            template = def.Children[_slotChildIndices[0]];

        var newSlotIndices = new System.Collections.Generic.List<int>(_slotChildIndices);
        for (int i = existing; i < needed; i++)
        {
            var slot = new Editor.UIEditorChildDef
            {
                Name = $"slot_{i}",
                Widget = EmptySlotWidget,
                Width = template?.Width ?? 109,
                Height = template?.Height ?? 106,
                Anchor = 0,
            };
            def.Children.Add(slot);
            newSlotIndices.Add(def.Children.Count - 1);
        }
        _slotChildIndices = newSlotIndices.ToArray();
    }

    /// <summary>Center the inventory on screen.</summary>
    public void CenterOnScreen(int screenW, int screenH)
    {
        _screenX = (screenW - _widgetW) / 2;
        _screenY = (screenH - _widgetH) / 2;
    }

    public void Toggle(int screenW, int screenH)
    {
        if (_visible) Close();
        else Open(screenW, screenH);
    }

    public void Open(int screenW, int screenH)
    {
        _visible = true;
        CenterOnScreen(screenW, screenH);
    }

    /// <summary>Open at an explicit screen position (e.g. docked beside the
    /// crafting menu) instead of centered.</summary>
    public void OpenAt(int x, int y)
    {
        _visible = true;
        _screenX = x;
        _screenY = y;
    }

    public void Close()
    {
        _visible = false;
    }

    // === IModalLayer ===
    // Inventory is a "soft modal" — gameplay continues behind it but clicks on
    // the panel rect are eaten so the spell bar / world don't accidentally
    // fire. LightDismiss is false so the user must explicitly close it; this
    // matches RPG inventory conventions (clicking outside ≠ close).
    public bool LightDismiss => false;
    // Non-blocking side panel — clicks outside fall through to gameplay
    // (you can spellcast with the inventory open). Panel clicks still
    // consumed via PopupManager.RouteInput's inside-rect path. ContainsMouse
    // is the existing public method below.
    public bool IsBlocking => false;
    public void OnCancel() => Close();

    /// <summary>Sync inventory state to widget visual overrides. Call before Draw.</summary>
    public void Update(InputState input)
    {
        _lastInput = input;
        if (!_visible) return;

        int mx = (int)input.MousePos.X, my = (int)input.MousePos.Y;

        // Window dragging — title-bar grab is inside the panel rect, so
        // PopupManager has already consumed the click for this layer.
        if (input.LeftDown)
        {
            if (!_dragging)
            {
                // Start drag if clicking on title bar area (top 90px)
                var titleRect = new Rectangle(_screenX, _screenY, _widgetW, 90);
                if (titleRect.Contains(mx, my))
                {
                    _dragging = true;
                    _dragOffsetX = mx - _screenX;
                    _dragOffsetY = my - _screenY;
                }
            }
            if (_dragging)
            {
                _screenX = mx - _dragOffsetX;
                _screenY = my - _dragOffsetY;
            }
        }
        else
        {
            _dragging = false;
        }

        // Slot click: dispatch a fresh left-press over a filled slot to the owner.
        // (Slots sit below the 90px title bar, so this never collides with a drag.)
        if (input.LeftPressed && !_dragging
            && TryGetSlotIndexAt(mx, my, out int clickedSlot)
            && !_inventory.GetSlot(clickedSlot).IsEmpty)
        {
            OnSlotClicked?.Invoke(clickedSlot);
        }

        SyncSlots();
    }

    /// <summary>Push inventory data into widget override system.</summary>
    private void SyncSlots()
    {
        for (int i = 0; i < _slotChildIndices.Length && i < _inventory.SlotCount; i++)
        {
            int childIdx = _slotChildIndices[i];
            var slot = _inventory.GetSlot(i);
            string subId = $"{InstanceId}.{childIdx}";

            if (slot.IsEmpty)
            {
                _renderer.SetChildWidget(InstanceId, childIdx, EmptySlotWidget);
                // Clear text/image overrides for this slot
                _renderer.ClearOverrides(subId);
            }
            else
            {
                _renderer.SetChildWidget(InstanceId, childIdx, FilledSlotWidget);
                _renderer.SetText(subId, "child_0", slot.Quantity.ToString());

                var itemDef = _items.Get(slot.ItemId);
                if (itemDef != null && !string.IsNullOrEmpty(itemDef.Icon))
                    _renderer.SetImage(subId, "child_1", itemDef.Icon);
            }
        }
    }

    public void Draw(int screenW = 0, int screenH = 0)
    {
        if (!_visible) return;
        _renderer.DrawWidget(WidgetId, _screenX, _screenY, InstanceId);

        // Slot hover highlight
        if (_batch == null || _pixel == null || _lastInput == null) return;
        var def = _renderer.GetWidgetDef(WidgetId);
        if (def == null) return;

        var rects = Necroking.UI.WidgetLayoutUtils.ComputeLayoutRects(def, _screenX, _screenY);
        int mx = (int)_lastInput.MousePos.X, my = (int)_lastInput.MousePos.Y;
        int hoveredSlot = -1;
        for (int i = 0; i < _slotChildIndices.Length; i++)
        {
            int ci = _slotChildIndices[i];
            if (ci < 0 || ci >= rects.Count) continue;
            var r = rects[ci];
            if (r.Contains(mx, my))
            {
                Scope.Draw(_pixel, r, new Color(255, 255, 255, 26));
                hoveredSlot = i;
                break;
            }
        }

        // Tooltip for the hovered (non-empty) slot, drawn last so it sits on top.
        if (hoveredSlot >= 0 && hoveredSlot < _inventory.SlotCount)
        {
            var slot = _inventory.GetSlot(hoveredSlot);
            if (!slot.IsEmpty)
            {
                var itemDef = _items.Get(slot.ItemId);
                if (itemDef != null)
                    DrawItemTooltip(itemDef, slot.Quantity, mx, my, screenW, screenH);
            }
        }
    }

    private void DrawItemTooltip(ItemDef item, int quantity, int mx, int my, int screenW, int screenH)
    {
        if (_batch == null || _pixel == null) return;

        const int TipW = 260;
        const int Pad = 8;
        int innerW = TipW - Pad * 2;

        var backend = new RichTip.WidgetBackend(_renderer, Scope, _pixel);
        string category = string.IsNullOrEmpty(item.Category)
            ? "" : char.ToUpper(item.Category[0]) + item.Category.Substring(1);
        var descLines = RichTip.Wrap(s => _renderer.MeasureText(s, RichTip.BodySize).X, item.Description, innerW);

        var rows = new System.Collections.Generic.List<RichTip.Row>
        {
            new("Quantity", quantity.ToString(), RichTip.Value),
            new("Max stack", item.MaxStack.ToString(), RichTip.Dim),
        };

        int sw = screenW > 0 ? screenW : _screenX + _widgetW;
        int sh = screenH > 0 ? screenH : _screenY + _widgetH;

        // gapAfterTitle:2 + gapBeforeDesc:4 preserves the item tooltip's subtitle spacing.
        // Deferred to the global tooltip queue: same RichTip box, but drawn in the
        // topmost Tooltip band so no other panel/overlay can cover it.
        Game1.Tooltips.RequestCustom(_ =>
            RichTip.Draw(backend, RichTip.Palette.Default, item.DisplayName,
                string.IsNullOrEmpty(category) ? null : category,
                descLines, rows, mx, my, sw, sh, TipW, Pad,
                gapAfterTitle: 2, gapBeforeDesc: 4));
    }

    /// <summary>Check if mouse is over the inventory window (for blocking game input).</summary>
    public bool ContainsMouse(int mouseX, int mouseY)
    {
        if (!_visible) return false;
        return mouseX >= _screenX && mouseX < _screenX + _widgetW &&
               mouseY >= _screenY && mouseY < _screenY + _widgetH;
    }

    public Rectangle? HitBounds(int screenW, int screenH)
        => _visible ? new Rectangle(_screenX, _screenY, _widgetW, _widgetH) : null;

    /// <summary>
    /// Hit-test a screen position against the inventory's slot rects. Returns
    /// true and the slot index when the mouse is over a slot. Used by external
    /// systems (table craft menu) that need to know which item the player just
    /// clicked without duplicating the layout math.
    /// </summary>
    public bool TryGetSlotIndexAt(int mouseX, int mouseY, out int slotIdx)
    {
        slotIdx = -1;
        if (!_visible) return false;
        var def = _renderer.GetWidgetDef(WidgetId);
        if (def == null) return false;
        var rects = Necroking.UI.WidgetLayoutUtils.ComputeLayoutRects(def, _screenX, _screenY);
        for (int i = 0; i < _slotChildIndices.Length; i++)
        {
            int ci = _slotChildIndices[i];
            if (ci < 0 || ci >= rects.Count) continue;
            if (rects[ci].Contains(mouseX, mouseY))
            {
                slotIdx = i;
                return true;
            }
        }
        return false;
    }
}
