using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Necroking.Data.Registries;
using Necroking.GameSystems;
using Necroking.UI;

namespace Necroking.Game;

/// <summary>
/// Bridges the slot-based Inventory to the widget-based UI.
/// Uses the "EquipmentWindow" widget as a template, dynamically
/// populating slots with item icons and quantities.
///
/// The widget template defines the visual style (nine-slices, fonts, colors).
/// This class owns the data binding — which slots show which items.
/// Cosmetic edits in the widget editor flow through automatically.
/// </summary>
public class InventoryUI
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

    private bool _visible;
    private int _screenX, _screenY;
    private int _widgetW, _widgetH;

    // Slot mapping: which widget child indices are inventory slots
    private int[] _slotChildIndices = Array.Empty<int>();
    private int _titleChildIndex = -1;

    // Dragging (future)
    private bool _dragging;
    private int _dragOffsetX, _dragOffsetY;

    public bool IsVisible => _visible;
    public int Width => _widgetW;
    public int Height => _widgetH;
    public RuntimeWidgetRenderer Renderer => _renderer;
    public int[] SlotChildIndices => _slotChildIndices;

    public void Init(RuntimeWidgetRenderer renderer, Inventory inventory, ItemRegistry items)
    {
        _renderer = renderer;
        _inventory = inventory;
        _items = items;

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
        _visible = !_visible;
        if (_visible)
            CenterOnScreen(screenW, screenH);
    }

    public void Open(int screenW, int screenH)
    {
        _visible = true;
        CenterOnScreen(screenW, screenH);
    }

    public void Close() => _visible = false;

    /// <summary>Sync inventory state to widget visual overrides. Call before Draw.</summary>
    public void Update(MouseState mouse, KeyboardState kb)
    {
        if (!_visible) return;

        // Window dragging
        if (mouse.LeftButton == ButtonState.Pressed)
        {
            if (!_dragging)
            {
                // Start drag if clicking on title bar area (top 90px)
                var titleRect = new Rectangle(_screenX, _screenY, _widgetW, 90);
                if (titleRect.Contains(mouse.X, mouse.Y))
                {
                    _dragging = true;
                    _dragOffsetX = mouse.X - _screenX;
                    _dragOffsetY = mouse.Y - _screenY;
                }
            }
            if (_dragging)
            {
                _screenX = mouse.X - _dragOffsetX;
                _screenY = mouse.Y - _dragOffsetY;
            }
        }
        else
        {
            _dragging = false;
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

    public void Draw()
    {
        if (!_visible) return;
        _renderer.DrawWidget(WidgetId, _screenX, _screenY, InstanceId);
    }

    /// <summary>Check if mouse is over the inventory window (for blocking game input).</summary>
    public bool ContainsMouse(int mouseX, int mouseY)
    {
        if (!_visible) return false;
        return mouseX >= _screenX && mouseX < _screenX + _widgetW &&
               mouseY >= _screenY && mouseY < _screenY + _widgetH;
    }
}
