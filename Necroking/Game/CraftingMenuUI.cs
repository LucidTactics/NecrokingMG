using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.GameSystems;
using Necroking.UI;

namespace Necroking.Game;

/// <summary>
/// Widget-driven crafting menu. Uses the "CraftingMenu" widget template
/// containing "CraftingItem" children. Each item shows potion icon, name,
/// up to 2 ingredient costs. Handles selection, affordability, 1s crafting
/// progress bar, and inventory output.
/// </summary>
public class CraftingMenuUI
{
    private const string MenuWidgetId = "CraftingMenu";
    private const string ItemWidgetId = "CraftingItem";
    private const string InstanceId = "craftmenu";

    private RuntimeWidgetRenderer _renderer = null!;
    private Inventory _inventory = null!;
    private ItemRegistry _items = null!;
    private GameData _gameData = null!;
    private SpriteBatch _batch = null!;
    private Texture2D _pixel = null!;

    private bool _visible;
    private int _screenX, _screenY;
    private int _widgetW, _widgetH;

    // Cached potion IDs
    private readonly List<string> _potionIds = new();
    private int _selectedIndex = -1;
    private float _craftProgress;
    private bool _crafting;
    private int _craftingIndex = -1;

    // Widget child mapping
    private int[] _itemChildIndices = Array.Empty<int>();

    public bool IsVisible => _visible;

    public void Init(RuntimeWidgetRenderer renderer, Inventory inventory,
        ItemRegistry items, GameData gameData, int screenH,
        SpriteBatch batch, Texture2D pixel)
    {
        _renderer = renderer;
        _inventory = inventory;
        _items = items;
        _gameData = gameData;
        _batch = batch;
        _pixel = pixel;

        var def = renderer.GetWidgetDef(MenuWidgetId);
        if (def == null) return;

        _widgetW = def.Width;
        _widgetH = screenH;
        def.Height = screenH;
    }

    public void Open(int screenW, int screenH)
    {
        _visible = true;
        _selectedIndex = -1;
        _crafting = false;
        _craftProgress = 0f;
        _craftingIndex = -1;

        // Left-aligned, same as build menu
        _screenX = -12;
        _screenY = 0;

        var def = _renderer.GetWidgetDef(MenuWidgetId);
        if (def != null)
        {
            def.Height = screenH;
            _widgetH = screenH;
            _widgetW = def.Width;
        }

        // Cache potion IDs
        _potionIds.Clear();
        _potionIds.AddRange(_gameData.Potions.GetIDs());

        // Ensure enough CraftingItem children
        if (def != null)
            EnsureItemChildren(def);

        SyncItems();
    }

    public void Close()
    {
        _visible = false;
        _crafting = false;
        _selectedIndex = -1;
    }

    public void Toggle(int screenW, int screenH)
    {
        if (_visible) Close();
        else Open(screenW, screenH);
    }

    private void EnsureItemChildren(Editor.UIEditorWidgetDef def)
    {
        int templateW = 218, templateH = 68;
        for (int i = 0; i < def.Children.Count; i++)
        {
            if (def.Children[i].Widget == ItemWidgetId)
            {
                templateW = def.Children[i].Width;
                templateH = def.Children[i].Height;
                break;
            }
        }

        def.Children.RemoveAll(c => c.Widget == ItemWidgetId);

        int needed = _potionIds.Count;
        var itemIndices = new List<int>();
        for (int i = 0; i < needed; i++)
        {
            var child = new Editor.UIEditorChildDef
            {
                Name = $"recipe_{i}",
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

    private void SyncItems()
    {
        if (_itemChildIndices.Length == 0) return;

        for (int i = 0; i < _itemChildIndices.Length; i++)
        {
            int childIdx = _itemChildIndices[i];
            string subId = $"{InstanceId}.{childIdx}";

            if (i >= _potionIds.Count)
            {
                _renderer.SetChildWidget(InstanceId, childIdx, "Item Slot_Empty");
                _renderer.ClearOverrides(subId);
                continue;
            }

            _renderer.SetChildWidget(InstanceId, childIdx, ItemWidgetId);

            var potion = _gameData.Potions.Get(_potionIds[i]);
            if (potion == null) continue;

            // Potion name
            _renderer.SetText(subId, "child_0", potion.DisplayName);

            // Large potion icon on left (Icon2_copy)
            var potionItem = _items.Get(potion.ItemID);
            if (potionItem != null && !string.IsNullOrEmpty(potionItem.Icon))
                _renderer.SetImage(subId, "Icon2_copy", potionItem.Icon);
            else if (!string.IsNullOrEmpty(potion.Icon))
                _renderer.SetImage(subId, "Icon2_copy", potion.Icon);

            // Cost 1
            if (potion.Recipe.Count > 0)
            {
                var ing1 = potion.Recipe[0];
                var ingItem1 = _items.Get(ing1.ItemId);
                _renderer.SetText(subId, "Quant1", ing1.Amount.ToString());
                if (ingItem1 != null)
                    _renderer.SetImage(subId, "Icon1", ingItem1.Icon);
            }
            else
            {
                _renderer.SetText(subId, "Quant1", "");
                _renderer.SetImage(subId, "Icon1", "");
            }

            // Cost 2
            if (potion.Recipe.Count > 1)
            {
                var ing2 = potion.Recipe[1];
                var ingItem2 = _items.Get(ing2.ItemId);
                _renderer.SetText(subId, "Quant2", ing2.Amount.ToString());
                if (ingItem2 != null)
                    _renderer.SetImage(subId, "Icon2", ingItem2.Icon);
            }
            else
            {
                _renderer.SetText(subId, "Quant2", "");
                _renderer.SetImage(subId, "Icon2", "");
            }
        }
    }

    private bool CanAfford(PotionDef potion)
    {
        foreach (var ing in potion.Recipe)
        {
            if (string.IsNullOrEmpty(ing.ItemId)) continue;
            if (_inventory.GetItemCount(ing.ItemId) < ing.Amount)
                return false;
        }
        return true;
    }

    private void CompleteCraft(PotionDef potion)
    {
        foreach (var ing in potion.Recipe)
        {
            if (string.IsNullOrEmpty(ing.ItemId)) continue;
            _inventory.RemoveItem(ing.ItemId, ing.Amount);
        }
        _inventory.AddItem(potion.ItemID, 1);
    }

    public void Update(MouseState mouse, MouseState prevMouse, int screenW, int screenH, float dt)
    {
        if (!_visible) return;

        SyncItems();

        // Click detection
        if (mouse.LeftButton == ButtonState.Pressed && prevMouse.LeftButton == ButtonState.Released)
        {
            int menuRight = _screenX + _widgetW;
            if (mouse.X < menuRight && mouse.Y >= 0)
            {
                var def = _renderer.GetWidgetDef(MenuWidgetId);
                if (def != null)
                {
                    var rects = ComputeItemRects(def);
                    for (int i = 0; i < rects.Count && i < _potionIds.Count; i++)
                    {
                        if (rects[i].Contains(mouse.X, mouse.Y))
                        {
                            var potion = _gameData.Potions.Get(_potionIds[i]);
                            if (potion != null && CanAfford(potion))
                            {
                                _selectedIndex = i;
                                _crafting = true;
                                _craftProgress = 0f;
                                _craftingIndex = i;
                            }
                            break;
                        }
                    }
                }
            }
        }

        // Tick crafting progress
        if (_crafting && _craftingIndex >= 0 && _craftingIndex < _potionIds.Count)
        {
            var potion = _gameData.Potions.Get(_potionIds[_craftingIndex]);
            if (potion == null || !CanAfford(potion))
            {
                _crafting = false;
                _craftProgress = 0f;
                return;
            }

            float craftDuration = potion.CraftTime > 0 ? potion.CraftTime : 1.0f;
            _craftProgress += dt / craftDuration;
            if (_craftProgress >= 1f)
            {
                CompleteCraft(potion);
                _crafting = false;
                _craftProgress = 0f;
            }
        }
    }

    public void Draw()
    {
        if (!_visible) return;
        _renderer.DrawWidget(MenuWidgetId, _screenX, _screenY, InstanceId);

        // Draw affordability + crafting overlays
        var def = _renderer.GetWidgetDef(MenuWidgetId);
        if (def == null) return;

        var rects = ComputeItemRects(def);
        for (int i = 0; i < rects.Count && i < _potionIds.Count; i++)
        {
            var potion = _gameData.Potions.Get(_potionIds[i]);
            if (potion == null) continue;

            bool canAfford = CanAfford(potion);
            bool isSelected = (i == _selectedIndex);

            if (isSelected)
                DrawBorder(rects[i], new Color(100, 255, 100, 80), 2);

            if (!canAfford)
                _batch.Draw(_pixel, rects[i], new Color(0, 0, 0, 80));

            // Crafting progress bar
            if (_crafting && _craftingIndex == i)
            {
                var r = rects[i];
                int barH = 6;
                int barY = r.Y + r.Height - barH - 2;
                int barW = r.Width - 4;
                _batch.Draw(_pixel, new Rectangle(r.X + 2, barY, barW, barH), new Color(20, 20, 30, 200));
                _batch.Draw(_pixel, new Rectangle(r.X + 2, barY, (int)(barW * _craftProgress), barH), new Color(80, 160, 80, 220));
            }
        }
    }

    public bool ContainsMouse(int mouseX, int mouseY)
    {
        if (!_visible) return false;
        return mouseX >= _screenX && mouseX < _screenX + _widgetW &&
               mouseY >= _screenY && mouseY < _screenY + _widgetH;
    }

    private List<Rectangle> ComputeItemRects(Editor.UIEditorWidgetDef def)
    {
        var allRects = new List<Rectangle>();

        int padL = def.LayoutPadLeft > 0 ? def.LayoutPadLeft : def.LayoutPadding;
        int padT = def.LayoutPadTop > 0 ? def.LayoutPadTop : def.LayoutPadding;
        int spacY = def.LayoutSpacingY > 0 ? def.LayoutSpacingY : def.LayoutSpacing;

        int curY = padT;
        for (int i = 0; i < _itemChildIndices.Length && i < _potionIds.Count; i++)
        {
            int childIdx = _itemChildIndices[i];
            if (childIdx >= def.Children.Count) break;
            var child = def.Children[childIdx];
            int cw = child.Width > 0 ? child.Width : 218;
            int ch = child.Height > 0 ? child.Height : 68;

            allRects.Add(new Rectangle(_screenX + padL, _screenY + curY, cw, ch));
            curY += ch + spacY;
        }
        return allRects;
    }

    private void DrawBorder(Rectangle r, Color c, int t = 1)
    {
        _batch.Draw(_pixel, new Rectangle(r.X, r.Y, r.Width, t), c);
        _batch.Draw(_pixel, new Rectangle(r.X, r.Y + r.Height - t, r.Width, t), c);
        _batch.Draw(_pixel, new Rectangle(r.X, r.Y + t, t, r.Height - t * 2), c);
        _batch.Draw(_pixel, new Rectangle(r.X + r.Width - t, r.Y + t, t, r.Height - t * 2), c);
    }
}
