using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.GameSystems;
using Necroking.Render;
using Necroking.UI;
using Necroking.World;

namespace Necroking.Game;

/// <summary>
/// Widget-driven building placement menu. Uses the "BuildingMenu" widget template
/// containing "BuildingItem" children. Each item shows building name, up to 2
/// resource costs with icons and quantities. Handles selection, resource checking,
/// ghost preview, and placement.
/// </summary>
public class BuildingMenuUI
{
    private const string MenuWidgetId = "BuildingMenu";
    private const string ItemWidgetId = "BuildingItem";
    private const string InstanceId = "buildmenu";

    private RuntimeWidgetRenderer _renderer = null!;
    private EnvironmentSystem _envSystem = null!;
    private Inventory _inventory = null!;
    private ItemRegistry _items = null!;
    private SpriteBatch _batch = null!;
    private Texture2D _pixel = null!;
    private MagicGlyphSystem? _glyphs;
    private SpellRegistry? _spells;
    private Simulation? _sim;

    private bool _visible;
    private InputState? _lastInput;
    private int _screenX, _screenY;
    private int _widgetW, _widgetH;

    // Buildable defs (cached when menu opens)
    private readonly List<int> _buildableDefIndices = new();
    private int _selectedIndex = -1;
    private bool _placementActive; // ghost follows cursor

    // Widget child mapping
    private int[] _itemChildIndices = Array.Empty<int>();

    public bool IsVisible => _visible;
    public bool IsPlacementActive => _placementActive && _selectedIndex >= 0;
    public int SelectedDefIndex => _selectedIndex >= 0 && _selectedIndex < _buildableDefIndices.Count
        ? _buildableDefIndices[_selectedIndex] : -1;

    public void Init(RuntimeWidgetRenderer renderer, EnvironmentSystem envSystem,
        Inventory inventory, ItemRegistry items, int screenH,
        SpriteBatch batch, Texture2D pixel,
        MagicGlyphSystem? glyphs = null, SpellRegistry? spells = null,
        Simulation? sim = null)
    {
        _renderer = renderer;
        _envSystem = envSystem;
        _inventory = inventory;
        _items = items;
        _batch = batch;
        _pixel = pixel;
        _glyphs = glyphs;
        _spells = spells;
        _sim = sim;

        var def = renderer.GetWidgetDef(MenuWidgetId);
        if (def == null) return;

        _widgetW = def.Width;
        _widgetH = screenH; // stretch to full screen height
        def.Height = screenH; // modify the def so layout uses full height
    }

    public void Open(int screenW, int screenH)
    {
        _visible = true;
        _placementActive = false;
        _selectedIndex = -1;

        // Position: left-aligned, 12px off-screen
        _screenX = -12;
        _screenY = 0;

        // Stretch to screen height
        var def = _renderer.GetWidgetDef(MenuWidgetId);
        if (def != null)
        {
            def.Height = screenH;
            _widgetH = screenH;
            _widgetW = def.Width;
        }

        // Cache buildable defs
        _buildableDefIndices.Clear();
        for (int di = 0; di < _envSystem.DefCount; di++)
        {
            if (_envSystem.Defs[di].PlayerBuildable)
                _buildableDefIndices.Add(di);
        }

        // Ensure enough BuildingItem children in the widget
        if (def != null)
            EnsureItemChildren(def);

        SyncItems();
    }

    public void Close()
    {
        _visible = false;
        _placementActive = false;
        _selectedIndex = -1;
    }

    public void Toggle(int screenW, int screenH)
    {
        if (_visible) Close();
        else Open(screenW, screenH);
    }

    /// <summary>Ensure the widget def has exactly the right number of BuildingItem children.</summary>
    private void EnsureItemChildren(Editor.UIEditorWidgetDef def)
    {
        // Get template dimensions from first BuildingItem child
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

        // Remove all existing BuildingItem children
        def.Children.RemoveAll(c => c.Widget == ItemWidgetId);

        // Add exactly the number we need
        int needed = _buildableDefIndices.Count;
        var itemIndices = new List<int>();
        for (int i = 0; i < needed; i++)
        {
            var child = new Editor.UIEditorChildDef
            {
                Name = $"building_{i}",
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

    /// <summary>Push building data into widget overrides.</summary>
    private void SyncItems()
    {
        if (_itemChildIndices.Length == 0) return;

        for (int i = 0; i < _itemChildIndices.Length; i++)
        {
            int childIdx = _itemChildIndices[i];
            string subId = $"{InstanceId}.{childIdx}";

            if (i >= _buildableDefIndices.Count)
            {
                // Hide excess items — use empty widget or clear
                _renderer.SetChildWidget(InstanceId, childIdx, "Item Slot_Empty");
                _renderer.ClearOverrides(subId);
                continue;
            }

            int defIdx = _buildableDefIndices[i];
            var envDef = _envSystem.Defs[defIdx];

            // Use BuildingItem widget
            _renderer.SetChildWidget(InstanceId, childIdx, ItemWidgetId);

            // Set building name
            string name = string.IsNullOrEmpty(envDef.Name) ? envDef.Id : envDef.Name;
            bool canAfford = CanAfford(envDef);

            // Building name — dim if can't afford
            _renderer.SetText(subId, "child_0", name);

            // Cost 1
            bool hasCost1 = !string.IsNullOrEmpty(envDef.Cost1ItemId) && envDef.Cost1Amount > 0;
            bool hasCost2 = !string.IsNullOrEmpty(envDef.Cost2ItemId) && envDef.Cost2Amount > 0;

            if (hasCost1)
            {
                var item1 = _items.Get(envDef.Cost1ItemId);
                _renderer.SetText(subId, "Quant1", envDef.Cost1Amount.ToString());
                if (item1 != null)
                    _renderer.SetImage(subId, "Icon1", item1.Icon);
            }
            else
            {
                _renderer.SetText(subId, "Quant1", "");
                _renderer.SetImage(subId, "Icon1", ""); // empty path = nothing rendered
            }

            if (hasCost2)
            {
                var item2 = _items.Get(envDef.Cost2ItemId);
                _renderer.SetText(subId, "Quant2", envDef.Cost2Amount.ToString());
                if (item2 != null)
                    _renderer.SetImage(subId, "Icon2", item2.Icon);
            }
            else
            {
                _renderer.SetText(subId, "Quant2", "");
                _renderer.SetImage(subId, "Icon2", ""); // hide cost 2 when not needed
            }
        }
    }

    /// <summary>Check if the player can afford a building.</summary>
    private bool CanAfford(EnvironmentObjectDef def)
    {
        if (!string.IsNullOrEmpty(def.Cost1ItemId) && def.Cost1Amount > 0)
        {
            if (_inventory.GetItemCount(def.Cost1ItemId) < def.Cost1Amount)
                return false;
        }
        if (!string.IsNullOrEmpty(def.Cost2ItemId) && def.Cost2Amount > 0)
        {
            if (_inventory.GetItemCount(def.Cost2ItemId) < def.Cost2Amount)
                return false;
        }
        return true;
    }

    /// <summary>Check if a specific cost is affordable.</summary>
    private bool CanAffordCost(string itemId, int amount)
    {
        if (string.IsNullOrEmpty(itemId) || amount <= 0) return true;
        return _inventory.GetItemCount(itemId) >= amount;
    }

    /// <summary>Deduct building costs from inventory.</summary>
    private void DeductCosts(EnvironmentObjectDef def)
    {
        if (!string.IsNullOrEmpty(def.Cost1ItemId) && def.Cost1Amount > 0)
            _inventory.RemoveItem(def.Cost1ItemId, def.Cost1Amount);
        if (!string.IsNullOrEmpty(def.Cost2ItemId) && def.Cost2Amount > 0)
            _inventory.RemoveItem(def.Cost2ItemId, def.Cost2Amount);
    }

    public void Update(InputState input, int screenW, int screenH)
    {
        _lastInput = input;
        if (!_visible) return;

        SyncItems();

        int mx = (int)input.MousePos.X, my = (int)input.MousePos.Y;

        // Click detection on building items
        if (input.LeftPressed)
        {
            // Check if click is within the menu panel
            int menuRight = _screenX + _widgetW;
            if (mx < menuRight && my >= 0)
            {
                // Hit-test against item rects
                var def = _renderer.GetWidgetDef(MenuWidgetId);
                if (def != null)
                {
                    var rects = ComputeItemRects(def);
                    for (int i = 0; i < rects.Count && i < _buildableDefIndices.Count; i++)
                    {
                        if (rects[i].Contains(mx, my))
                        {
                            int envDefIdx = _buildableDefIndices[i];
                            var envDef = _envSystem.Defs[envDefIdx];
                            if (CanAfford(envDef))
                            {
                                _selectedIndex = i;
                                _placementActive = true;
                            }
                            input.ConsumeMouse();
                            break;
                        }
                    }
                }
            }
        }

        // Right-click or Escape cancels placement
        if (_placementActive && input.RightPressed)
        {
            _placementActive = false;
            _selectedIndex = -1;
        }
    }

    /// <summary>Try to place the selected building at world position. Returns true if placed.</summary>
    public bool TryPlace(float worldX, float worldY)
    {
        if (!_placementActive || _selectedIndex < 0 || _selectedIndex >= _buildableDefIndices.Count)
            return false;

        int defIdx = _buildableDefIndices[_selectedIndex];
        var envDef = _envSystem.Defs[defIdx];

        if (!CanAfford(envDef)) return false;

        // Glyph traps spawn a MagicGlyph blueprint instead of an env object. The glyph
        // has its own placement overlap check (radius-based) and its own build progress.
        // Auto-assigns the necromancer to build it (walk over, work, activate).
        if (envDef.IsGlyphTrap && _glyphs != null)
        {
            var pos = new Vec2(worldX, worldY);
            if (!_glyphs.CanPlace(pos, envDef.GlyphRadius)) return false;

            DeductCosts(envDef);
            var glyph = _glyphs.SpawnBlueprint(pos, envDef.GlyphRadius, Faction.Undead);
            glyph.TriggerSpellID = envDef.TrapSpellId;
            var spell = _spells?.Get(envDef.TrapSpellId);
            if (spell != null)
            {
                glyph.Color = spell.CloudColor;
                glyph.Color2 = spell.CloudGlowColor;
            }

            // Assign the player's necromancer to walk over and build the glyph.
            if (_sim != null && _sim.NecromancerIndex >= 0)
            {
                int glyphIdx = _glyphs.IndexOf(glyph);
                int necroIdx = _sim.NecromancerIndex;
                _sim.UnitsMut[necroIdx].BuildGlyphIdx = glyphIdx;
                _sim.UnitsMut[necroIdx].BuildTimer = 0f;
                _sim.UnitsMut[necroIdx].Routine = AI.PlayerControlledHandler.RoutineBuildGlyph;
                _sim.UnitsMut[necroIdx].Subroutine = AI.PlayerControlledHandler.BuildSub_WalkToSite;
            }
            return true;
        }

        if (!_envSystem.CanPlaceObject(defIdx, worldX, worldY)) return false;

        DeductCosts(envDef);
        _envSystem.AddObject((ushort)defIdx, worldX, worldY);
        return true;
    }

    /// <summary>Check if placement is valid at position (for ghost color).</summary>
    public bool CanPlaceAt(float worldX, float worldY)
    {
        if (_selectedIndex < 0 || _selectedIndex >= _buildableDefIndices.Count) return false;
        int defIdx = _buildableDefIndices[_selectedIndex];
        return _envSystem.CanPlaceObject(defIdx, worldX, worldY);
    }

    /// <summary>Draw the building menu panel.</summary>
    public void DrawMenu()
    {
        if (!_visible) return;
        _renderer.DrawWidget(MenuWidgetId, _screenX, _screenY, InstanceId);

        // Draw affordability overlays on top of each item
        DrawAffordabilityOverlays();
    }

    /// <summary>Draw colored overlays for affordability feedback.</summary>
    private void DrawAffordabilityOverlays()
    {
        var def = _renderer.GetWidgetDef(MenuWidgetId);
        if (def == null) return;

        var rects = ComputeItemRects(def);
        for (int i = 0; i < rects.Count && i < _buildableDefIndices.Count; i++)
        {
            int defIdx = _buildableDefIndices[i];
            var envDef = _envSystem.Defs[defIdx];
            bool canAfford = CanAfford(envDef);
            bool isSelected = (i == _selectedIndex && _placementActive);

            // Hover highlight
            if (_lastInput != null)
            {
                var r = rects[i];
                int hmx = (int)_lastInput.MousePos.X, hmy = (int)_lastInput.MousePos.Y;
                if (r.Contains(hmx, hmy) && canAfford)
                    DrawRect(r, Color.White * 0.1f);
            }

            // Selected highlight
            if (isSelected)
            {
                var r = rects[i];
                DrawBorder(r, new Color(100, 255, 100, 80), 2);
            }

            if (!canAfford)
            {
                var r = rects[i];
                DrawRect(r, new Color(0, 0, 0, 80));
            }
        }
    }

    /// <summary>Draw ghost preview of building at cursor position.</summary>
    public void DrawGhostPreview(SpriteBatch batch, Texture2D pixel, Vec2 mouseWorld,
        Vector2 screenPos, Camera25D camera, Renderer renderer)
    {
        if (!IsPlacementActive) return;

        int defIdx = _buildableDefIndices[_selectedIndex];
        var envDef = _envSystem.Defs[defIdx];
        bool canPlace = _envSystem.CanPlaceObject(defIdx, mouseWorld.X, mouseWorld.Y);

        var tex = _envSystem.GetDefTexture(defIdx);
        if (tex != null)
        {
            float worldH = envDef.SpriteWorldHeight * envDef.Scale;
            float pixelH = worldH * camera.Zoom;
            float scale = pixelH / tex.Height;
            var origin = new Vector2(envDef.PivotX * tex.Width, envDef.PivotY * tex.Height);

            // Ghost tint: green if valid, red if invalid, max alpha 0.3
            byte alpha = 76; // ~0.3 * 255
            Color ghostColor = canPlace
                ? new Color((byte)(50 * alpha / 255), (byte)(200 * alpha / 255), (byte)(50 * alpha / 255), alpha)
                : new Color((byte)(200 * alpha / 255), (byte)(50 * alpha / 255), (byte)(50 * alpha / 255), alpha);

            batch.Draw(tex, screenPos, null, ghostColor, 0f, origin, scale, SpriteEffects.None, 0f);
        }

        // Draw placement radius circle hint
        if (envDef.PlacementRadius > 0)
        {
            float radiusPixels = envDef.PlacementRadius * camera.Zoom;
            Render.DrawUtils.DrawCircleOutline(batch, pixel, screenPos, radiusPixels,
                canPlace ? new Color(50, 200, 50, 40) : new Color(200, 50, 50, 40), 16);
        }
    }

    /// <summary>Check if mouse is over the building menu panel.</summary>
    public bool ContainsMouse(int mouseX, int mouseY)
    {
        if (!_visible) return false;
        return mouseX >= _screenX && mouseX < _screenX + _widgetW &&
               mouseY >= _screenY && mouseY < _screenY + _widgetH;
    }

    // ═══════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════

    private List<Rectangle> ComputeItemRects(Editor.UIEditorWidgetDef def)
    {
        // Use the same layout computation as the renderer
        var allRects = new List<Rectangle>();
        bool isVert = def.Layout == "vertical";

        int padL = def.LayoutPadLeft > 0 ? def.LayoutPadLeft : def.LayoutPadding;
        int padT = def.LayoutPadTop > 0 ? def.LayoutPadTop : def.LayoutPadding;
        int spacY = def.LayoutSpacingY > 0 ? def.LayoutSpacingY : def.LayoutSpacing;

        int curY = padT;
        for (int i = 0; i < _itemChildIndices.Length && i < _buildableDefIndices.Count; i++)
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

    private void DrawRect(Rectangle r, Color c)
    {
        _batch.Draw(_pixel, r, c);
    }

    private void DrawBorder(Rectangle r, Color c, int t = 1)
    {
        _batch.Draw(_pixel, new Rectangle(r.X, r.Y, r.Width, t), c);
        _batch.Draw(_pixel, new Rectangle(r.X, r.Y + r.Height - t, r.Width, t), c);
        _batch.Draw(_pixel, new Rectangle(r.X, r.Y + t, t, r.Height - t * 2), c);
        _batch.Draw(_pixel, new Rectangle(r.X + r.Width - t, r.Y + t, t, r.Height - t * 2), c);
    }

}
