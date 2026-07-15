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
using Necroking.Lib;
using Necroking.Render;
using Necroking.World;

namespace Necroking.UI;

/// <summary>
/// Widget-driven building placement menu. Uses the "BuildingMenu" widget template
/// containing "BuildingItem" children laid out as a 3-per-row icon grid; each cell
/// shows the building's sprite, with name + costs in a hover tooltip. Handles
/// selection, resource checking, ghost preview, and placement.
/// </summary>
public class BuildingMenuUI : SideListMenu
{
    protected override string MenuWidgetId => "BuildingMenu";
    protected override string ItemWidgetId => "BuildingItem";
    protected override string InstanceId => "buildmenu";
    protected override string ItemChildPrefix => "building_";

    // Session-scoped systems are read live: StartGame disposes the old GameSession
    // and creates a new one, so a cached ref would point at the dead session
    // (cleared defs → empty menu after exiting to main menu and re-entering).
    private EnvironmentSystem _envSystem => Game1.Instance._envSystem;
    private Simulation _sim => Game1.Instance._sim;
    private MagicGlyphSystem _glyphs => _sim.MagicGlyphs;

    private Inventory _inventory = null!;
    private ItemRegistry _items = null!;
    private SpellRegistry? _spells;

    // Buildable defs (cached when menu opens)
    private readonly List<int> _buildableDefIndices = new();
    private int _selectedIndex = -1;
    private bool _placementActive; // ghost follows cursor
    private int _lastScreenW, _lastScreenH;

    protected override int ItemCount => _buildableDefIndices.Count;

    public bool IsPlacementActive => _placementActive && _selectedIndex >= 0;
    /// <summary>Def indices shown by the last Open() — read by the
    /// map_reload_consistency regression scenario.</summary>
    internal IReadOnlyList<int> BuildableDefIndices => _buildableDefIndices;
    public int SelectedDefIndex => _selectedIndex >= 0 && _selectedIndex < _buildableDefIndices.Count
        ? _buildableDefIndices[_selectedIndex] : -1;

    public void Init(RuntimeWidgetRenderer renderer,
        Inventory inventory, ItemRegistry items, int screenH,
        SpriteBatch batch, Texture2D pixel, SpellRegistry? spells = null)
    {
        _renderer = renderer;
        _inventory = inventory;
        _items = items;
        _batch = batch;
        _pixel = pixel;
        _spells = spells;

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

    /// <summary>Grid cells are bare frames — the building sprite is drawn by
    /// <see cref="DrawItemIcons"/> and name/costs live in the hover tooltip.</summary>
    protected override void BindItem(string subId, int i) { }

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
        _lastScreenW = screenW;
        _lastScreenH = screenH;
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
        if (envDef.IsGlyphTrap)
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
            if (_sim.NecromancerIndex >= 0)
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

        // Building sprites, drawn under the overlays so the can't-afford dim
        // darkens them too.
        DrawItemIcons();

        // Hover / selected / can't-afford overlays (shared side-list mechanics).
        // Returns the hovered cell for the tooltip below.
        int hoveredIdx = DrawItemOverlays();

        if (hoveredIdx >= 0 && _lastInput != null)
            DrawBuildingTooltip(hoveredIdx, (int)_lastInput.MousePos.X, (int)_lastInput.MousePos.Y);
    }

    /// <summary>Aspect-fit each building's sprite into its grid cell (frame 0
    /// for animated sheets — same slicing as EnvGhostRenderer).</summary>
    private void DrawItemIcons()
    {
        var def = _renderer.GetWidgetDef(MenuWidgetId);
        if (def == null) return;

        var rects = ComputeItemRects(def);
        for (int i = 0; i < rects.Count; i++)
        {
            int defIdx = _buildableDefIndices[i];
            var envDef = _envSystem.Defs[defIdx];
            var cell = rects[i];
            cell.Inflate(-7, -7); // keep clear of the cell frame

            // Glyph traps never instantiate their env sprite — show the trap
            // spell's grimoire icon (the def texture is usually a placeholder).
            if (envDef.IsGlyphTrap)
            {
                var spell = _spells?.Get(envDef.TrapSpellId);
                if (spell != null && !string.IsNullOrEmpty(spell.Icon))
                {
                    _renderer.DrawIcon(spell.Icon, cell.X, cell.Y, cell.Width, cell.Height);
                    continue;
                }
            }

            var tex = _envSystem.GetDefTexture(defIdx);
            if (tex == null) continue;

            Rectangle? src = null;
            if (envDef.IsAnimated && envDef.AnimTotalFrames > 1 && !_envSystem.IsUsingPlaceholder(defIdx))
                src = envDef.GetAnimFrameRect(tex.Width, tex.Height, 0);

            DrawUtils.DrawAspectFit(Scope, tex, src, cell, Color.White);
        }
    }

    /// <summary>Rich hover tooltip: building name + cost rows with have/need
    /// affordability coloring (same pattern as CraftingMenuUI's potion tooltip).</summary>
    private void DrawBuildingTooltip(int i, int mx, int my)
    {
        var envDef = _envSystem.Defs[_buildableDefIndices[i]];
        string name = string.IsNullOrEmpty(envDef.Name) ? envDef.Id : envDef.Name;

        const int TipW = 240;
        var backend = new RichTip.WidgetBackend(_renderer, Scope, _pixel);

        var rows = new List<RichTip.Row>();
        AddCostRow(rows, envDef.Cost1ItemId, envDef.Cost1Amount);
        AddCostRow(rows, envDef.Cost2ItemId, envDef.Cost2Amount);
        if (rows.Count == 0)
            rows.Add(new("Cost", "Free", RichTip.Green));

        if (envDef.IsGlyphTrap)
        {
            var spell = _spells?.Get(envDef.TrapSpellId);
            if (spell != null)
                rows.Add(new("Trap spell",
                    string.IsNullOrEmpty(spell.DisplayName) ? envDef.TrapSpellId : spell.DisplayName,
                    RichTip.Dim));
        }

        int sw = _lastScreenW > 0 ? _lastScreenW : (_screenX + _widgetW + TipW + 16);
        int sh = _lastScreenH > 0 ? _lastScreenH : _widgetH;

        // Deferred to the global tooltip queue: drawn in the topmost Tooltip band.
        Game1.Tooltips.RequestCustom(_ =>
            RichTip.Draw(backend, RichTip.Palette.Default, name, null,
                Array.Empty<string>(), rows, mx, my, sw, sh, TipW));
    }

    /// <summary>Append a "<c>have/need</c>" cost row (green when affordable) if the
    /// cost slot is populated.</summary>
    private void AddCostRow(List<RichTip.Row> rows, string itemId, int amount)
    {
        if (string.IsNullOrEmpty(itemId) || amount <= 0) return;
        int have = _inventory.GetItemCount(itemId);
        rows.Add(new(_items.NameOf(itemId), $"{have}/{amount}",
            have >= amount ? RichTip.Green : RichTip.Red));
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
