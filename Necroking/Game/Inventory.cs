using System;
using System.Collections.Generic;
using Necroking.Data.Registries;

namespace Necroking.GameSystems;

public struct InventorySlot
{
    public string ItemId;    // empty string = empty slot
    public int Quantity;

    public bool IsEmpty => string.IsNullOrEmpty(ItemId) || Quantity <= 0;

    public static InventorySlot Empty => new() { ItemId = "", Quantity = 0 };
}

/// <summary>
/// Slot-based inventory. Fixed number of slots, stackable items.
/// </summary>
public class Inventory
{
    private readonly InventorySlot[] _slots;
    private readonly ItemRegistry? _items;

    public int SlotCount => _slots.Length;

    public Inventory(int slotCount = 20, ItemRegistry? items = null)
    {
        _slots = new InventorySlot[slotCount];
        _items = items;
        for (int i = 0; i < slotCount; i++)
            _slots[i] = InventorySlot.Empty;
    }

    public InventorySlot GetSlot(int index)
    {
        if (index < 0 || index >= _slots.Length) return InventorySlot.Empty;
        return _slots[index];
    }

    /// <summary>Add items to inventory. Stacks onto existing slots first, then uses empty slots.
    /// Returns the number of items that could NOT be added (0 = all added).</summary>
    public int AddItem(string itemId, int quantity = 1)
    {
        if (string.IsNullOrEmpty(itemId) || quantity <= 0) return quantity;
        int maxStack = _items?.Get(itemId)?.MaxStack ?? 99;
        int remaining = quantity;

        // First pass: stack onto existing slots with same item
        for (int i = 0; i < _slots.Length && remaining > 0; i++)
        {
            if (_slots[i].ItemId == itemId && _slots[i].Quantity < maxStack)
            {
                int space = maxStack - _slots[i].Quantity;
                int add = Math.Min(space, remaining);
                _slots[i].Quantity += add;
                remaining -= add;
            }
        }

        // Second pass: use empty slots
        for (int i = 0; i < _slots.Length && remaining > 0; i++)
        {
            if (_slots[i].IsEmpty)
            {
                int add = Math.Min(maxStack, remaining);
                _slots[i].ItemId = itemId;
                _slots[i].Quantity = add;
                remaining -= add;
            }
        }

        return remaining;
    }

    /// <summary>Remove items from inventory. Returns true if the full amount was removed.</summary>
    public bool RemoveItem(string itemId, int quantity = 1)
    {
        if (string.IsNullOrEmpty(itemId) || quantity <= 0) return true;
        if (GetItemCount(itemId) < quantity) return false;

        int remaining = quantity;
        // Remove from last slots first (preserves earlier stacks)
        for (int i = _slots.Length - 1; i >= 0 && remaining > 0; i--)
        {
            if (_slots[i].ItemId == itemId)
            {
                int remove = Math.Min(_slots[i].Quantity, remaining);
                _slots[i].Quantity -= remove;
                remaining -= remove;
                if (_slots[i].Quantity <= 0)
                    _slots[i] = InventorySlot.Empty;
            }
        }
        return true;
    }

    /// <summary>Swap or merge two slots.</summary>
    public void MoveSlot(int from, int to)
    {
        if (from < 0 || from >= _slots.Length || to < 0 || to >= _slots.Length || from == to) return;

        // If same item, try to merge
        if (_slots[from].ItemId == _slots[to].ItemId && !_slots[from].IsEmpty)
        {
            int maxStack = _items?.Get(_slots[from].ItemId)?.MaxStack ?? 99;
            int space = maxStack - _slots[to].Quantity;
            if (space >= _slots[from].Quantity)
            {
                _slots[to].Quantity += _slots[from].Quantity;
                _slots[from] = InventorySlot.Empty;
            }
            else
            {
                _slots[to].Quantity = maxStack;
                _slots[from].Quantity -= space;
            }
            return;
        }

        // Otherwise swap
        (_slots[from], _slots[to]) = (_slots[to], _slots[from]);
    }

    /// <summary>Total count of a specific item across all slots.</summary>
    public int GetItemCount(string itemId)
    {
        int total = 0;
        for (int i = 0; i < _slots.Length; i++)
            if (_slots[i].ItemId == itemId)
                total += _slots[i].Quantity;
        return total;
    }

    /// <summary>Get all non-empty items as a dictionary (for legacy compatibility).</summary>
    public IReadOnlyDictionary<string, int> All
    {
        get
        {
            var dict = new Dictionary<string, int>();
            for (int i = 0; i < _slots.Length; i++)
            {
                if (_slots[i].IsEmpty) continue;
                if (dict.ContainsKey(_slots[i].ItemId))
                    dict[_slots[i].ItemId] += _slots[i].Quantity;
                else
                    dict[_slots[i].ItemId] = _slots[i].Quantity;
            }
            return dict;
        }
    }

    /// <summary>Find first slot containing the given item, or -1.</summary>
    public int FindSlot(string itemId)
    {
        for (int i = 0; i < _slots.Length; i++)
            if (_slots[i].ItemId == itemId) return i;
        return -1;
    }

    /// <summary>Number of non-empty slots.</summary>
    public int UsedSlots
    {
        get
        {
            int count = 0;
            for (int i = 0; i < _slots.Length; i++)
                if (!_slots[i].IsEmpty) count++;
            return count;
        }
    }
}
