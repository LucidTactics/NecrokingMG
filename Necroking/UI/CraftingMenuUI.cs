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
public class CraftingMenuUI : SideListMenu
{
    protected override string MenuWidgetId => "CraftingMenu";
    protected override string ItemWidgetId => "CraftingItem";
    protected override string InstanceId => "craftmenu";
    protected override string ItemChildPrefix => "recipe_";

    private Inventory _inventory = null!;
    private ItemRegistry _items = null!;
    private GameData _gameData = null!;
    private SkillBookState? _bookState;
    private readonly Random _rng = new();

    private int _lastScreenW, _lastScreenH;

    // Rich tooltip styling + mechanics live in UI/RichTip.cs (shared with the
    // inventory and character-stats tooltips). This is the RICH tooltip;
    // HUDRenderer.DrawCursorTooltip is the separate SIMPLE plain-string one.

    // Cached potion IDs
    private readonly List<string> _potionIds = new();
    private int _selectedIndex = -1;
    private float _craftProgress;
    private bool _crafting;
    private int _craftingIndex = -1;

    protected override int ItemCount => _potionIds.Count;

    public void Init(RuntimeWidgetRenderer renderer, Inventory inventory,
        ItemRegistry items, GameData gameData, int screenH,
        Texture2D pixel)
    {
        _renderer = renderer;
        _inventory = inventory;
        _items = items;
        _gameData = gameData;
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

    public override void Open(int screenW, int screenH)
    {
        _selectedIndex = -1;
        _crafting = false;
        _craftProgress = 0f;
        _craftingIndex = -1;
        OpenAnchorStretch(screenH);

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

        RebuildItems();
    }

    public override void Close()
    {
        _visible = false;
        _crafting = false;
        _craftProgress = 0f;
        _craftingIndex = -1;
        _selectedIndex = -1;
    }

    /// <summary>Bind potion <paramref name="i"/>'s name, icon and up to 2
    /// ingredient costs.</summary>
    protected override void BindItem(string subId, int i)
    {
        var potion = _gameData.Potions.Get(_potionIds[i]);
        if (potion == null) return;

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

    protected override bool CanAfford(int i)
    {
        var potion = _gameData.Potions.Get(_potionIds[i]);
        return potion != null && CanAfford(potion);
    }

    protected override bool IsItemSelected(int i) => i == _selectedIndex;

    /// <summary>An affordable recipe row was clicked — start crafting it.</summary>
    protected override void OnItemClicked(int i)
    {
        _selectedIndex = i;
        _crafting = true;
        _craftProgress = 0f;
        _craftingIndex = i;
    }

    /// <summary>Crafting progress bar along the bottom of the crafting row.</summary>
    protected override void DrawItemExtras(Rectangle r, int i)
    {
        if (!(_crafting && _craftingIndex == i)) return;
        int barH = 6;
        int barY = r.Y + r.Height - barH - 2;
        int barW = r.Width - 4;
        Scope.Draw(_pixel, new Rectangle(r.X + 2, barY, barW, barH), new Color(20, 20, 30, 200));
        Scope.Draw(_pixel, new Rectangle(r.X + 2, barY, (int)(barW * _craftProgress), barH), new Color(80, 160, 80, 220));
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
        HandleItemClick(input);

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

        // Hover / selected / can't-afford overlays + the per-row craft progress
        // bar (DrawItemExtras). Returns the hovered row for the tooltip below.
        int hoveredIdx = DrawItemOverlays();

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

        var backend = new RichTip.WidgetBackend(_renderer, Scope, _pixel);
        var descLines = RichTip.Wrap(s => _renderer.MeasureText(s, RichTip.BodySize).X, potion.Description, innerW);

        // Breakdown: ingredients (with have/need + affordability color), then meta.
        var rows = new List<RichTip.Row>();
        bool anyIngredient = false;
        foreach (var ing in potion.Recipe)
        {
            if (string.IsNullOrEmpty(ing.ItemId)) continue;
            anyIngredient = true;
            int have = _inventory.GetItemCount(ing.ItemId);
            string name = _items.NameOf(ing.ItemId);
            rows.Add(new(name, $"{have}/{ing.Amount}", have >= ing.Amount ? RichTip.Green : RichTip.Red));
        }
        if (!anyIngredient)
            rows.Add(new("Cost", "Free", RichTip.Green));

        rows.Add(new("Target", potion.TargetType, RichTip.Dim));
        if (potion.ThrowRange > 0)
            rows.Add(new("Throw range", potion.ThrowRange.ToString("0.#"), RichTip.Dim));
        rows.Add(new("Craft time", $"{potion.CraftTime:0.#}s", RichTip.Dim));

        int sw = _lastScreenW > 0 ? _lastScreenW : (_screenX + _widgetW + TipW + 16);
        int sh = _lastScreenH > 0 ? _lastScreenH : _widgetH;

        // Deferred to the global tooltip queue: drawn in the topmost Tooltip band.
        Game1.Tooltips.RequestCustom(_ =>
            RichTip.Draw(backend, RichTip.Palette.Default, potion.DisplayName, null,
                descLines, rows, mx, my, sw, sh, TipW, Pad));
    }
}
