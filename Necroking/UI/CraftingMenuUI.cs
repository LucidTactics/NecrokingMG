using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.Game;
using Necroking.GameSystems;

namespace Necroking.UI;

/// <summary>
/// Widget-driven crafting menu. Uses the "CraftingMenu" widget template
/// containing "CraftingItem" children. Each item shows potion icon, name,
/// up to 2 ingredient costs. Handles selection, affordability, 1s crafting
/// progress bar, and inventory output.
/// </summary>
public class CraftingMenuUI : IModalLayer
{
    private const string MenuWidgetId = "CraftingMenu";
    private const string ItemWidgetId = "CraftingItem";
    private const string InstanceId = "craftmenu";

    private RuntimeWidgetRenderer _renderer = null!;
    private Inventory _inventory = null!;
    private ItemRegistry _items = null!;
    private GameData _gameData = null!;
    private SpriteBatch _batch = null!;
    private Render.SpriteScope Scope => _batch;  // straight-alpha draw surface (implicit conversion)
    private Texture2D _pixel = null!;
    private SkillBookState? _bookState;
    private readonly Random _rng = new();

    private bool _visible;
    private InputState? _lastInput;
    private int _screenX, _screenY;
    private int _widgetW, _widgetH;
    private int _lastScreenW, _lastScreenH;

    // Tooltip styling
    private static readonly Color TipBg = new(20, 20, 32, 245);
    private static readonly Color TipBorder = new(120, 120, 170, 240);
    private static readonly Color TipTitle = new(255, 220, 140);
    private static readonly Color TipDesc = new(200, 200, 215);
    private static readonly Color TipDim = new(150, 150, 165);
    private static readonly Color TipGreen = new(120, 230, 120);
    private static readonly Color TipRed = new(230, 110, 110);
    private const int TipTitleSize = 18;
    private const int TipBodySize = 14;

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

    /// <summary>Wire the skill book so the menu can filter recipes by
    /// UnlockedPotions, award a potion skill point per craft, and roll the
    /// 25% skip-cost for the Efficient Tinctures passive.</summary>
    public void SetSkillBook(SkillBookState bookState) => _bookState = bookState;

    public void Open(int screenW, int screenH)
    {
        _visible = true;
        _selectedIndex = -1;
        _crafting = false;
        _craftProgress = 0f;
        _craftingIndex = -1;
        Necroking.Game1.Popups.Push(this);

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

        // Cache potion IDs — filtered by skill-tree unlocks. Empty book = empty
        // list (player hasn't learned the root yet). The crafting menu is opt-in
        // so locked recipes simply don't appear instead of greying out.
        _potionIds.Clear();
        var allIds = _gameData.Potions.GetIDs();
        if (_bookState == null)
        {
            _potionIds.AddRange(allIds);
        }
        else
        {
            for (int i = 0; i < allIds.Count; i++)
                if (_bookState.IsPotionUnlocked(allIds[i])) _potionIds.Add(allIds[i]);
        }

        // Ensure enough CraftingItem children
        if (def != null)
            EnsureItemChildren(def);

        SyncItems();
    }

    public void Close()
    {
        _visible = false;
        _crafting = false;
        _craftProgress = 0f;
        _craftingIndex = -1;
        _selectedIndex = -1;
        Necroking.Game1.Popups.Pop(this);
    }

    // === IModalLayer ===
    public bool LightDismiss => false;
    public bool IsBlocking => false;  // side panel — gameplay coexists
    // ContainsMouse is the existing public method on this class.
    public void OnCancel() => Close();

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
        // Efficient Tinctures: 25% chance to skip the ingredient deduction
        // entirely. Rolled per-craft, not per-ingredient (one die, all-or-nothing).
        bool skipCost = _bookState != null
            && _bookState.HasPassive("efficient_tinctures")
            && _rng.NextDouble() < 0.25;

        if (!skipCost)
        {
            foreach (var ing in potion.Recipe)
            {
                if (string.IsNullOrEmpty(ing.ItemId)) continue;
                _inventory.RemoveItem(ing.ItemId, ing.Amount);
            }
        }
        else
        {
            DebugLog.Log("skillbook", $"Efficient Tinctures: skipped cost on {potion.Id}");
        }

        _inventory.AddItem(potion.ItemID, 1);

        // One potion skill point per successful craft, regardless of cost-skip.
        _bookState?.AddSkillPoints("potions", 1);
    }

    public void Update(InputState input, int screenW, int screenH, float dt)
    {
        _lastInput = input;
        _lastScreenW = screenW;
        _lastScreenH = screenH;
        if (!_visible) return;

        SyncItems();

        int mx = (int)input.MousePos.X, my = (int)input.MousePos.Y;

        // Click detection
        if (input.LeftPressed)
        {
            int menuRight = _screenX + _widgetW;
            if (mx < menuRight && my >= 0)
            {
                var def = _renderer.GetWidgetDef(MenuWidgetId);
                if (def != null)
                {
                    var rects = ComputeItemRects(def);
                    for (int i = 0; i < rects.Count && i < _potionIds.Count; i++)
                    {
                        if (rects[i].Contains(mx, my))
                        {
                            var potion = _gameData.Potions.Get(_potionIds[i]);
                            if (potion != null && CanAfford(potion))
                            {
                                _selectedIndex = i;
                                _crafting = true;
                                _craftProgress = 0f;
                                _craftingIndex = i;
                            }
                            // PopupManager already consumed this inside-panel click.
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
        int hoveredIdx = -1;
        for (int i = 0; i < rects.Count && i < _potionIds.Count; i++)
        {
            var potion = _gameData.Potions.Get(_potionIds[i]);
            if (potion == null) continue;

            bool canAfford = CanAfford(potion);
            bool isSelected = (i == _selectedIndex);

            // Hover highlight
            if (_lastInput != null)
            {
                int hmx = (int)_lastInput.MousePos.X, hmy = (int)_lastInput.MousePos.Y;
                if (rects[i].Contains(hmx, hmy))
                {
                    hoveredIdx = i;
                    if (canAfford)
                        Scope.Draw(_pixel, rects[i], new Color(255, 255, 255, 26));
                }
            }

            if (isSelected)
                DrawBorder(rects[i], new Color(100, 255, 100, 80), 2);

            if (!canAfford)
                Scope.Draw(_pixel, rects[i], new Color(0, 0, 0, 80));

            // Crafting progress bar
            if (_crafting && _craftingIndex == i)
            {
                var r = rects[i];
                int barH = 6;
                int barY = r.Y + r.Height - barH - 2;
                int barW = r.Width - 4;
                Scope.Draw(_pixel, new Rectangle(r.X + 2, barY, barW, barH), new Color(20, 20, 30, 200));
                Scope.Draw(_pixel, new Rectangle(r.X + 2, barY, (int)(barW * _craftProgress), barH), new Color(80, 160, 80, 220));
            }
        }

        // Tooltip for the hovered recipe, drawn last so it sits on top.
        if (hoveredIdx >= 0 && _lastInput != null)
        {
            var potion = _gameData.Potions.Get(_potionIds[hoveredIdx]);
            if (potion != null)
                DrawPotionTooltip(potion, (int)_lastInput.MousePos.X, (int)_lastInput.MousePos.Y);
        }
    }

    private void DrawPotionTooltip(PotionDef potion, int mx, int my)
    {
        const int TipW = 270;
        const int Pad = 8;
        int innerW = TipW - Pad * 2;

        var descLines = WrapText(potion.Description, TipBodySize, innerW);

        // Breakdown: ingredients (with have/need + affordability color), then meta.
        var lines = new List<(string Label, string Value, Color Color)>();
        bool anyIngredient = false;
        foreach (var ing in potion.Recipe)
        {
            if (string.IsNullOrEmpty(ing.ItemId)) continue;
            anyIngredient = true;
            int have = _inventory.GetItemCount(ing.ItemId);
            string name = _items.Get(ing.ItemId)?.DisplayName ?? ing.ItemId;
            lines.Add((name, $"{have}/{ing.Amount}", have >= ing.Amount ? TipGreen : TipRed));
        }
        if (!anyIngredient)
            lines.Add(("Cost", "Free", TipGreen));

        lines.Add(("Target", potion.TargetType, TipDim));
        if (potion.ThrowRange > 0)
            lines.Add(("Throw range", potion.ThrowRange.ToString("0.#"), TipDim));
        lines.Add(("Craft time", $"{potion.CraftTime:0.#}s", TipDim));

        // Measure.
        int lineH = (int)System.MathF.Ceiling(_renderer.MeasureText("Ay", TipBodySize).Y);
        int titleH = (int)System.MathF.Ceiling(_renderer.MeasureText(potion.DisplayName, TipTitleSize).Y);

        int height = Pad + titleH + 4;
        height += descLines.Count * lineH;
        height += 8 + lines.Count * lineH; // divider gap + breakdown rows
        height += Pad;

        int sw = _lastScreenW > 0 ? _lastScreenW : (_screenX + _widgetW + TipW + 16);
        int sh = _lastScreenH > 0 ? _lastScreenH : _widgetH;
        int tx = mx + 16, ty = my + 20;
        if (tx + TipW > sw - 4) tx = mx - TipW - 8;
        if (ty + height > sh - 4) ty = my - height - 8;
        tx = System.Math.Max(4, tx);
        ty = System.Math.Max(4, ty);

        Scope.Draw(_pixel, new Rectangle(tx, ty, TipW, height), TipBg);
        DrawBorder(new Rectangle(tx, ty, TipW, height), TipBorder, 2);

        int cy = ty + Pad;
        _renderer.DrawText(potion.DisplayName, tx + Pad, cy, TipTitleSize, TipTitle);
        cy += titleH + 4;

        foreach (var ln in descLines)
        {
            _renderer.DrawText(ln, tx + Pad, cy, TipBodySize, TipDesc);
            cy += lineH;
        }

        cy += 3;
        Scope.Draw(_pixel, new Rectangle(tx + Pad, cy, innerW, 1), TipBorder);
        cy += 5;
        foreach (var (label, value, color) in lines)
        {
            _renderer.DrawText(label, tx + Pad, cy, TipBodySize, TipDim);
            var vs = _renderer.MeasureText(value, TipBodySize);
            _renderer.DrawText(value, (int)(tx + TipW - Pad - vs.X), cy, TipBodySize, color);
            cy += lineH;
        }
    }

    /// <summary>Greedy word-wrap to a pixel width using the widget font.</summary>
    private List<string> WrapText(string text, int fontSize, float maxW)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(text)) return result;
        var sb = new System.Text.StringBuilder();
        foreach (var word in text.Split(' '))
        {
            string trial = sb.Length == 0 ? word : sb + " " + word;
            if (sb.Length > 0 && _renderer.MeasureText(trial, fontSize).X > maxW)
            {
                result.Add(sb.ToString());
                sb.Clear();
                sb.Append(word);
            }
            else
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(word);
            }
        }
        if (sb.Length > 0) result.Add(sb.ToString());
        return result;
    }

    public bool ContainsMouse(int mouseX, int mouseY)
    {
        if (!_visible) return false;
        return mouseX >= _screenX && mouseX < _screenX + _widgetW &&
               mouseY >= _screenY && mouseY < _screenY + _widgetH;
    }

    public Rectangle? HitBounds(int screenW, int screenH)
        => _visible ? new Rectangle(_screenX, _screenY, _widgetW, _widgetH) : null;

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
        Necroking.Render.DrawUtils.DrawRectBorder(_batch, _pixel, r, c, t);
    }
}
