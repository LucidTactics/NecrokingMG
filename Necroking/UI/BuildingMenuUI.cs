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
using Necroking.Render;
using Necroking.World;

namespace Necroking.UI;

/// <summary>
/// Widget-driven building placement menu. Uses the "BuildingMenu" widget template
/// containing "BuildingItem" children. Each item shows building name, up to 2
/// resource costs with icons and quantities. Handles selection, resource checking,
/// ghost preview, and placement.
/// </summary>
public class BuildingMenuUI : SideListMenu
{
    protected override string MenuWidgetId => "BuildingMenu";
    protected override string ItemWidgetId => "BuildingItem";
    protected override string InstanceId => "buildmenu";
    protected override string ItemChildPrefix => "building_";

    private EnvironmentSystem _envSystem = null!;
    private Inventory _inventory = null!;
    private ItemRegistry _items = null!;
    private MagicGlyphSystem? _glyphs;
    private SpellRegistry? _spells;
    private Simulation? _sim;

    // Buildable defs (cached when menu opens)
    private readonly List<int> _buildableDefIndices = new();
    private int _selectedIndex = -1;
    private bool _placementActive; // ghost follows cursor

    protected override int ItemCount => _buildableDefIndices.Count;

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

    public override void Open(int screenW, int screenH)
    {
        _placementActive = false;
        _selectedIndex = -1;
        OpenAnchorStretch(screenH);

        // Cache buildable defs
        _buildableDefIndices.Clear();
        for (int di = 0; di < _envSystem.DefCount; di++)
        {
            if (_envSystem.Defs[di].PlayerBuildable)
                _buildableDefIndices.Add(di);
        }

        RebuildItems();
    }

    public override void Close()
    {
        _visible = false;
        _placementActive = false;
        _selectedIndex = -1;
    }

    /// <summary>Bind building <paramref name="i"/>'s name + up to 2 resource costs.</summary>
    protected override void BindItem(string subId, int i)
    {
        int defIdx = _buildableDefIndices[i];
        var envDef = _envSystem.Defs[defIdx];

        string name = string.IsNullOrEmpty(envDef.Name) ? envDef.Id : envDef.Name;
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

    protected override bool CanAfford(int i)
        => CanAfford(_envSystem.Defs[_buildableDefIndices[i]]);

    protected override bool IsItemSelected(int i) => i == _selectedIndex && _placementActive;

    /// <summary>An affordable building row was clicked — arm placement mode.</summary>
    protected override void OnItemClicked(int i)
    {
        _selectedIndex = i;
        _placementActive = true;
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
        HandleItemClick(input);

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
            // StartRoutine fires the OLD routine's exit cleanup (and restarts the build
            // routine when retargeting a new blueprint); fields are set after.
            if (_sim != null && _sim.NecromancerIndex >= 0)
            {
                int necroIdx = _sim.NecromancerIndex;
                AI.AIControl.StartRoutine(_sim.UnitsMut, necroIdx,
                    AI.PlayerControlledHandler.RoutineBuildGlyph,
                    AI.PlayerControlledHandler.BuildSub_WalkToSite);
                _sim.UnitsMut[necroIdx].BuildGlyphId = glyph.Id;
                _sim.UnitsMut[necroIdx].BuildTimer = 0f;
            }
            return true;
        }

        if (!_envSystem.CanPlaceObject(defIdx, worldX, worldY)) return false;

        DeductCosts(envDef);
        // Persistent: map save is currently the only way player constructions survive a restart.
        _envSystem.AddObject((ushort)defIdx, worldX, worldY, persistent: true);
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

        // Hover / selected / can't-afford overlays (shared side-list mechanics).
        DrawItemOverlays();
    }

    /// <summary>Draw ghost preview of building at cursor position.</summary>
    public void DrawGhostPreview(SpriteScope batch, Texture2D pixel, Vec2 mouseWorld,
        Vector2 screenPos, Camera25D camera)
    {
        if (!IsPlacementActive) return;

        int defIdx = _buildableDefIndices[_selectedIndex];
        bool canPlace = _envSystem.CanPlaceObject(defIdx, mouseWorld.X, mouseWorld.Y);
        EnvGhostRenderer.Draw(batch, _envSystem, defIdx, screenPos, camera.Zoom,
            canPlace, EnvGhostRenderer.BuildValidTint, pixel);
    }

}
