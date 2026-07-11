// Game1 partial: this file is the root — fields, app lifecycle (Initialize/LoadContent),
// menu + input handling, and the Update orchestrator. Sibling Game1.*.cs partials each
// carry their own banner; see docs/locate-behavior/game1-partials.md for the routing map.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.Core;
using Necroking.Render;
using Necroking.Movement;
using Necroking.Game;
using Necroking.GameSystems;
using Necroking.World;
using Necroking.Scenario;
using Necroking.Editor;
using Necroking.UI;

namespace Necroking;

public enum MenuState { MainMenu, None, PauseMenu, Settings, Multiplayer, UnitEditor, SpellEditor, MapEditor, UIEditor, ItemEditor, ScenarioList }

/// <summary>
/// The MonoGame app shell and orchestrator — everything platform- and presentation-side.
/// Owns the app-lifetime objects: graphics device, SpriteBatch/fonts, the renderers
/// (GameRenderer, HUD, shadows, weather), UI panels/overlays, the editors, <see cref="GameData"/>,
/// camera, <see cref="InputState"/>, the <see cref="GameClock"/> (single time/pause authority),
/// day/night, menus, and the dev server. Per-game world state lives in <see cref="GameSession"/>
/// (<c>_session</c>, recreated on every map load); <c>_sim</c> forwards to <c>_session.Sim</c>.
///
/// Each Update: read devices → translate input into abstract Simulation intents → if
/// <c>_clock.WorldRunning</c>, call <c>_sim.Tick(_clock.WorldDt)</c> → tick presentation-side
/// systems (animation, workers, environment visuals). Draw reads sim state without mutating it.
///
/// The rule: gameplay state and rules go in <see cref="Simulation"/> (headless, deterministic);
/// drawing goes in Render/; deep logic belongs in Game/, World/, or AI/ classes that Game1
/// merely constructs and wires. Game1 itself is glue — if a method here grows real logic,
/// it's in the wrong place.
/// </summary>
public partial class Game1 : Microsoft.Xna.Framework.Game
{
    internal const int WorldSize = 64; // start small, load map for real size

    private GraphicsDeviceManager _graphics;
    internal SpriteBatch _spriteBatch = null!;

    /// <summary>Draw surface over the shared SpriteBatch — THE way scene/HUD code
    /// draws. Colors are straight alpha; the open material (Materials.Open)
    /// encodes them. Use <c>Scope.Batch</c> only for colors that are already a
    /// material-native encoding (HDR pack, additive-via-A=0 trick).</summary>
    internal Render.SpriteScope Scope =>
        new(_spriteBatch, Render.Materials.Open ?? Render.Materials.Hud);
    internal Texture2D _pixel = null!;
    internal Texture2D _glowTex = null!;
    internal Texture2D? _mainMenuBg;
    internal SpriteFont? _font;
    internal SpriteFont? _smallFont;
    internal SpriteFont? _largeFont;
    internal ShadowRenderer _shadowRenderer = new();
    internal HUDRenderer _hudRenderer = new();
    internal CharacterStatsUI _characterStatsUI = new();
    internal UI.UnitInfoPanel _unitInfoPanel = new();
    internal UI.GrimoireOverlay _grimoireOverlay = new();
    internal UI.SkillBookOverlay _skillBookOverlay = new();
    internal SkillBookState _skillBookState = new();
    internal GameSystems.DeathFogSystem _deathFog = new();

    internal struct SkillLearnToast
    {
        public string Header;   // e.g. "Recipe Learned"
        public string SkillName;
        public string SkillId;  // for clicking through to the right tab
        public float Timer;     // seconds shown so far
        public float Duration;  // seconds total
    }
    internal readonly List<SkillLearnToast> _skillLearnToasts = new();
    internal UIShaders _uiShaders = null!;

    // Data
    internal GameData _gameData = new();

    // Simulation — owned by the per-game GameSession (see _session below); forwarding
    // property keeps every _sim.* call site unchanged while StartGame recreates the session.
    internal Simulation _sim => _session.Sim;
    internal Inventory _inventory = null!;
    private Render.FontManager _fontManager = new();
    internal RuntimeWidgetRenderer _widgetRenderer = new();
    internal InventoryUI _inventoryUI = new();
    internal BuildingMenuUI _buildingMenuUI = new();
    internal CraftingMenuUI _craftingMenu = new();
    internal TableCraftMenuUI _tableMenuUI = new();
    /// <summary>True after Inventory/Building/Crafting/Table menu UIs have been
    /// fully initialized. Initialization is deferred from LoadContent to first-use
    /// (see EnsureInventoryUIsInitialized) — these four UIs collectively cost
    /// ~250ms during startup but most launches never open them. Triggered on the
    /// first input/scenario event that touches an inventory-family UI.</summary>
    private bool _inventoryUIsInitialized;
    /// <summary>UI definitions directory path captured during LoadContent so
    /// EnsureInventoryUIsInitialized can hand it to RuntimeWidgetRenderer when
    /// the deferred init fires (the widget renderer is only used by inventory
    /// family UIs, so its init happens with theirs).</summary>
    private string? _widgetRendererUiDefPath;
    private System.Diagnostics.Stopwatch? _startupTimer;

    // (World-object pick helpers — FindGraveUnderCursor, FindCorpsePileUnderCursor,
    // FindNearestCorpsePileInRange — live in Game1.WorldClicks.cs with the rest of
    // the world-interaction input.)

    // True if this corpse is an endpoint of any tether (so the renderer still draws it on
    // the ground even though its DraggedByUnitID claim would otherwise hide it as "carried").
    internal bool IsCorpseTethered(int corpseId)
    {
        foreach (var t in _tethers)
        {
            if (t.A.Kind == TetherEndKind.Corpse && t.A.CorpseId == corpseId) return true;
            if (t.B.Kind == TetherEndKind.Corpse && t.B.CorpseId == corpseId) return true;
        }
        return false;
    }

    private static bool SameEnd(TetherEnd a, TetherEnd b)
        => a.Kind == b.Kind &&
           (a.Kind == TetherEndKind.Unit ? a.UnitId == b.UnitId : a.CorpseId == b.CorpseId);

    // Resolve an endpoint to a live world position. Returns false (drop the tether) when the
    // unit is gone/dead or the corpse was consumed/dissolved/bagged/removed.
    private bool TryEndState(TetherEnd e, out Vec2 pos, out int unitIdx, out int corpseIdx)
    {
        pos = default; unitIdx = -1; corpseIdx = -1;
        if (e.Kind == TetherEndKind.Unit)
        {
            int ui = Necroking.Movement.UnitUtil.ResolveUnitIndex(_sim.Units, e.UnitId);
            if (ui < 0) return false;
            unitIdx = ui; pos = _sim.Units[ui].Position; return true;
        }
        int ci = _sim.FindCorpseIndexByID(e.CorpseId);
        if (ci < 0) return false;
        var cp = _sim.Corpses[ci];
        if (cp.ConsumedBySummon || cp.Dissolving || cp.Bagged) return false;
        corpseIdx = ci; pos = cp.Position; return true;
    }

    // Pick the nearest unit or corpse to a world point, within RopeAttachRadius. Claimed
    // corpses (already tethered/carried) are skipped unless includeClaimedCorpses is set.
    // WorldQuery does both halves; only the cross-collection "nearest of either"
    // comparison lives here (tie goes to the corpse, matching the old scan order).
    private bool TryPickTetherEnd(Vec2 worldPos, out TetherEnd end, bool includeClaimedCorpses = false)
    {
        end = default;
        var exclude = includeClaimedCorpses
            ? CorpseExclude.Free & ~CorpseExclude.Dragged
            : CorpseExclude.Free;
        int ci = _sim.Query.NearestCorpse(worldPos, RopeAttachRadius, exclude);
        int ui = _sim.Query.UnitUnderCursor(worldPos, RopeAttachRadius);
        if (ci < 0 && ui < 0) return false;

        float cd = ci >= 0 ? (_sim.Corpses[ci].Position - worldPos).LengthSq() : float.MaxValue;
        float ud = ui >= 0 ? (_sim.Units[ui].Position - worldPos).LengthSq() : float.MaxValue;
        end = ud < cd
            ? TetherEnd.ForUnit(_sim.Units[ui].Id)
            : TetherEnd.ForCorpse(_sim.Corpses[ci].CorpseID);
        return true;
    }

    // Create a tether between two endpoints. If exactly one end is a corpse and the other a
    // unit, claim the corpse (DraggedByUnitID) so pickup/hover/workers leave it alone, and
    // drop its frozen carry pose so the drag update can rotate it via FacingAngle.
    private void CreateTether(TetherEnd a, TetherEnd b)
    {
        _tethers.Add(new Tether { A = a, B = b });
        ClaimCorpseEnd(a, b);
        ClaimCorpseEnd(b, a);
    }

    private void ClaimCorpseEnd(TetherEnd corpseEnd, TetherEnd otherEnd)
    {
        if (corpseEnd.Kind != TetherEndKind.Corpse || otherEnd.Kind != TetherEndKind.Unit) return;
        int idx = _sim.FindCorpseIndexByID(corpseEnd.CorpseId);
        if (idx < 0) return;
        var cm = _sim.CorpsesMut;
        cm[idx].DraggedByUnitID = otherEnd.UnitId;
        cm[idx].CarryDisplayAngle = -1;
    }

    // Remove a tether, releasing any corpse claim it held.
    private void RemoveTetherAt(int ti)
    {
        var t = _tethers[ti];
        ReleaseCorpseEnd(t.A);
        ReleaseCorpseEnd(t.B);
        _tethers.RemoveAt(ti);
    }

    private void ReleaseCorpseEnd(TetherEnd e)
    {
        if (e.Kind != TetherEndKind.Corpse) return;
        int idx = _sim.FindCorpseIndexByID(e.CorpseId);
        if (idx >= 0) _sim.CorpsesMut[idx].DraggedByUnitID = GameConstants.InvalidUnit;
        _tetherDustAccum.Remove(e.CorpseId);
    }

    // Remove the tether nearest to a world point (either endpoint within RopeAttachRadius).
    private bool DetachNearestTether(Vec2 worldPos)
    {
        int bestTi = -1;
        float best = RopeAttachRadius * RopeAttachRadius;
        for (int ti = 0; ti < _tethers.Count; ti++)
        {
            var t = _tethers[ti];
            if (TryEndState(t.A, out var pa, out _, out _))
            {
                float d = (pa - worldPos).LengthSq();
                if (d < best) { best = d; bestTi = ti; }
            }
            if (TryEndState(t.B, out var pb, out _, out _))
            {
                float d = (pb - worldPos).LengthSq();
                if (d < best) { best = d; bestTi = ti; }
            }
        }
        if (bestTi < 0) return false;
        RemoveTetherAt(bestTi);
        return true;
    }

    // Shift+R press. Priority: (1) if a Shift+T anchor is set, connect it to whatever's under
    // the cursor; (2) else if something tethered is under the cursor, detach it; (3) else the
    // necromancer quick-drags the nearest free corpse (the original one-key behavior).
    // Returns a short status string for the dev command.
    private string HandleRopeKey(Vec2 worldPos, int necroIdx)
    {
        if (_tetherAnchor.HasValue)
        {
            if (TryPickTetherEnd(worldPos, out var b) && !SameEnd(_tetherAnchor.Value, b))
            {
                CreateTether(_tetherAnchor.Value, b);
                _tetherAnchor = null;
                return "tether attached";
            }
            _tetherAnchor = null;
            return "no target under cursor";
        }

        if (DetachNearestTether(worldPos)) return "tether detached";

        if (necroIdx >= 0)
        {
            var necroEnd = TetherEnd.ForUnit(_sim.Units[necroIdx].Id);
            if (TryNearestFreeCorpse(_sim.Units[necroIdx].Position, out var corpseEnd))
            {
                CreateTether(necroEnd, corpseEnd);
                return "roped nearest corpse";
            }
        }
        return "nothing to rope";
    }

    private bool TryNearestFreeCorpse(Vec2 from, out TetherEnd end)
    {
        end = default;
        int best = _sim.Query.NearestCorpse(from, RopeAttachRadius, CorpseExclude.Free);
        if (best < 0) return false;
        end = TetherEnd.ForCorpse(_sim.Corpses[best].CorpseID);
        return true;
    }

    // Per-frame tether physics: for every tether with a unit end and a corpse end, once they
    // stretch past RopeMaxLength the unit hauls the corpse in behind it (turning the body to
    // face the pull + kicking up dust). The necromancer is slowed while any of its ropes is
    // taut. Invalid tethers (dead unit / gone corpse) are dropped.
    private void UpdateTethers(float dt)
    {
        bool necroTaut = false;
        for (int ti = _tethers.Count - 1; ti >= 0; ti--)
        {
            var t = _tethers[ti];
            if (!TryEndState(t.A, out var pa, out int ua, out int ca)
             || !TryEndState(t.B, out var pb, out int ub, out int cb))
            {
                RemoveTetherAt(ti);
                continue;
            }

            // Identify a single unit-mover / corpse-follower pair. Unit↔unit and
            // corpse↔corpse tethers are rope-only (no dragging).
            int corpseIdx, moverUnitIdx;
            Vec2 corpsePos, moverPos;
            if (ca >= 0 && ub >= 0) { corpseIdx = ca; moverUnitIdx = ub; corpsePos = pa; moverPos = pb; }
            else if (cb >= 0 && ua >= 0) { corpseIdx = cb; moverUnitIdx = ua; corpsePos = pb; moverPos = pa; }
            else continue;

            var toCorpse = corpsePos - moverPos;
            float dist = toCorpse.Length();
            if (dist <= RopeMaxLength || dist <= 0.0001f) continue;

            // Taut: reel the corpse to rope's length behind the mover and face it that way.
            var dir = toCorpse.Normalized();
            var newPos = moverPos + dir * RopeMaxLength;
            var cm = _sim.CorpsesMut;
            float moved = (newPos - cm[corpseIdx].Position).Length();
            cm[corpseIdx].Position = newPos;
            cm[corpseIdx].FacingAngle = MathF.Atan2(-dir.Y, -dir.X) * 180f / MathF.PI;

            if (moverUnitIdx == _sim.NecromancerIndex) necroTaut = true;

            // Dust as the body scrapes — tied to actual travel, throttled per corpse.
            int cid = cm[corpseIdx].CorpseID;
            _tetherDustAccum.TryGetValue(cid, out float acc);
            acc += moved;
            if (acc > 0.4f && _effectManager != null)
            {
                _ropeDustAngle = (_ropeDustAngle + 137) % 360;
                float ang = _ropeDustAngle * MathF.PI / 180f;
                _effectManager.SpawnDustPuff(newPos + new Vec2(MathF.Cos(ang), MathF.Sin(ang)) * 0.35f);
                acc = 0f;
            }
            _tetherDustAccum[cid] = acc;
        }

        // The necromancer's haul penalty (0.5× + no sprint) is on only while one of its
        // ropes is taut this frame.
        _sim.SetNecromancerDragSlow(necroTaut ? 0.5f : 1f, necroTaut);
    }

    // Withdraw one corpse from a pile and hand it to the necromancer to carry.
    // Gated on proximity + the necromancer being free + the pile holding a corpse.
    // Returns true if the pickup started.
    private bool TryTakeCorpseFromPile(int necroIdx, int pileObjIdx)
    {
        if (necroIdx < 0 || pileObjIdx < 0 || _envSystem == null) return false;
        var nu = _sim.Units[necroIdx];
        // Busy carrying / mid-action → can't start a fresh pickup.
        if (nu.CarryingCorpseID >= 0 || nu.CorpseInteractPhase != 0) return false;
        if (nu.BaggingCorpseID >= 0) return false;
        if (_workerSystem.StoredOf(pileObjIdx, Game.Jobs.JobResources.Corpse) <= 0) return false;

        var o = _envSystem.GetObject(pileObjIdx);
        var pilePos = new Vec2(o.X, o.Y);
        // Same reach as the F-key corpse pickup, so "close enough" feels identical.
        const float pickRange = 3f;
        if ((pilePos - nu.Position).LengthSq() > pickRange * pickRange) return false;

        if (_workerSystem.Withdraw(pileObjIdx, Game.Jobs.JobResources.Corpse, 1) <= 0) return false;

        // Materialise a physical corpse at the pile and have the necromancer carry it,
        // exactly like an F-key pickup (Pickup phase → the anim lerps it to the hand).
        // Pull the real body type the pile remembers; fall back to a visible default
        // so a count-only pile (e.g. dev-seeded) never yields an invisible corpse.
        // Fall back to a body type known to render (a carried corpse needs a "Death"
        // anim — see DrawCarriedCorpse; "skeleton" has one, so a count-only pile never
        // yields an invisible corpse).
        string defId = _workerSystem.TakePiledCorpse(pileObjIdx);
        if (string.IsNullOrEmpty(defId)) defId = "skeleton";
        int cid = _sim.SpawnLooseCorpse(pilePos, defId);
        var c = _sim.FindCorpseByID(cid);
        if (c != null) c.LerpStartPos = pilePos;
        _sim.UnitsMut[necroIdx].CarryingCorpseID = cid;
        _sim.UnitsMut[necroIdx].CorpseInteractPhase = 4; // Pickup
        if (c != null) c.DraggedByUnitID = nu.Id;
        return true;
    }

    /// <summary>Deferred init for Inventory/Building/Crafting/Table menu UIs.
    /// Called lazily on the first frame any of those UIs needs to be drawn or
    /// updated. Idempotent — second call is a no-op via the flag.</summary>
    internal void EnsureInventoryUIsInitialized()
    {
        if (_inventoryUIsInitialized) return;
        _inventoryUIsInitialized = true;
        // Initialize the widget renderer first (its definitions get loaded from
        // the UI defs dir captured at startup). Without this, the four UIs below
        // would crash on their first LayoutWidget call.
        _widgetRenderer.Init(GraphicsDevice, _spriteBatch, _fontManager);
        if (_widgetRendererUiDefPath != null && Directory.Exists(_widgetRendererUiDefPath))
            _widgetRenderer.LoadDefinitions(_widgetRendererUiDefPath);
        _inventoryUI.Init(_widgetRenderer, _inventory, _gameData.Items, _spriteBatch, _pixel);
        // Slot click: deposit into an open crafting table, else use a consumable
        // (e.g. Skillpoint Potion). Handled via the layer callback because the
        // inventory is a modal layer — PopupManager consumes inside-panel clicks
        // before Game1.Update's own handlers run.
        _inventoryUI.OnSlotClicked = slotIdx =>
        {
            var s = _inventory.GetSlot(slotIdx);
            if (s.IsEmpty) return;
            if (_tableMenuUI.IsVisible)
                _tableMenuUI.TryDepositItem(s.ItemId);
            else if (TryConsumeInventoryItem(s.ItemId))
                _inventory.RemoveItem(s.ItemId, 1);
        };
        _buildingMenuUI.Init(_widgetRenderer, _envSystem, _inventory, _gameData.Items,
            _graphics.PreferredBackBufferHeight, _spriteBatch, _pixel,
            _sim.MagicGlyphs, _gameData.Spells, _sim);
        _craftingMenu.Init(_widgetRenderer, _inventory, _gameData.Items, _gameData,
            _graphics.PreferredBackBufferHeight, _spriteBatch, _pixel);
        _craftingMenu.SetSkillBook(_skillBookState);
        _tableMenuUI.Init(_widgetRenderer, _envSystem, _inventory, _gameData.Items,
            _sim.PlayerResources, _spriteBatch, _pixel, _font);
        _tableMenuUI.SetSkillBook(_skillBookState);
        _tableMenuUI.StartCraftCallback = (envIdx) => StartTableCraft(envIdx);
        _tableMenuUI.DrawUnitIconCallback = (defId, rect) => _gameRenderer.DrawUnitIdleSprite(defId, rect);
        _graveRosterUI.Init(_spriteBatch, _pixel, _widgetRenderer, _workerSystem);
        _jobBoardUI.Init(_spriteBatch, _pixel, _widgetRenderer, _workerSystem);
        _unitInfoPanel.Init(_widgetRenderer, _gameData);
        _grimoireOverlay.Init(_widgetRenderer, _gameData,
            spell => SpellCaster.HasSpellRequirements(spell, _gameData, _sim.UnitsMut, FindNecromancer())
                // Potion throw-spells (ConsumesItem) stay hidden until the player has
                // actually seen that potion in their inventory at least once.
                && (string.IsNullOrEmpty(spell.ConsumesItem) || _inventory.HasEverSeen(spell.ConsumesItem)));
        ValidatePotionAbilities();
        _unitInfoPanel.DrawUnitIconCallback = (defId, rect) => _gameRenderer.DrawUnitIdleSprite(defId, rect);
        _unitInfoPanel.OnClosed = () =>
        {
            // Release only the pause the inspect set — no-op if a menu button
            // already force-cleared it (GameClock.Resume is per-source).
            _clock.Resume(GameClock.PauseSource.Inspect);
        };
        Necroking.Core.DebugLog.Log("startup", "  [LazyInit] Inventory/Building/Crafting/Table UIs initialized on demand");
    }

    /// <summary>Which screen edge a HUD panel docks to. Docked panels are exclusive
    /// per side: opening one closes whatever else is on that side, so a left and a
    /// right panel can coexist but never two left panels. Centered overlays
    /// (inventory, skill book) and contextual popups (table craft) don't participate.</summary>
    internal enum PanelSide { Left, Right }

    /// <summary>Close every visible panel docked on <paramref name="side"/> except
    /// <paramref name="opening"/>. Call right before opening a docked panel (skip when
    /// the toggle is about to close it). Each panel closes via its own method so it
    /// pops itself off <see cref="Popups"/> — never mutate the stack directly.</summary>
    internal void CloseSameSidePanels(PanelSide side, object opening)
    {
        if (side == PanelSide.Left)
        {
            if (!ReferenceEquals(opening, _craftingMenu) && _craftingMenu.IsVisible) _craftingMenu.Close();
            if (!ReferenceEquals(opening, _buildingMenuUI) && _buildingMenuUI.IsVisible) _buildingMenuUI.Close();
            if (!ReferenceEquals(opening, _grimoireOverlay) && _grimoireOverlay.IsVisible) _grimoireOverlay.Hide();
            if (!ReferenceEquals(opening, _characterStatsUI) && _characterStatsUI.IsVisible) _characterStatsUI.Close();
            if (!ReferenceEquals(opening, _jobBoardUI) && _jobBoardUI.IsVisible) _jobBoardUI.Close();
        }
        else
        {
            // Only the pinned unit-info sheet docks right today. Transient (hover)
            // shows never route through here — they'd slam other panels shut every frame.
            if (!ReferenceEquals(opening, _unitInfoPanel) && _unitInfoPanel.IsVisible) _unitInfoPanel.Hide();
        }
    }

    private bool _uiEditorInitialized;
    /// <summary>Deferred init for the UI editor (F12 / menu). LoadDefinitions bakes
    /// every harmonized widget/element texture (~4s of CPU work) — a dev-only tool
    /// that most launches never open, so it's paid on first open, not at startup.</summary>
    private void EnsureUIEditorInitialized()
    {
        if (_uiEditorInitialized) return;
        _uiEditorInitialized = true;
        _uiEditor.Init(_spriteBatch, _pixel, _font, _smallFont);
        _uiEditor.SetFontManager(_fontManager);
        string uiDefPath = GamePaths.Resolve(GamePaths.UIDefsDir);
        if (Directory.Exists(uiDefPath))
            _uiEditor.LoadDefinitions(uiDefPath);
        // Saving in the UI editor reloads the runtime widget renderer, so
        // in-game HUD/panels pick up the edits without restarting the game.
        _uiEditor.OnSaved = () =>
        {
            if (_inventoryUIsInitialized && _widgetRendererUiDefPath != null
                && Directory.Exists(_widgetRendererUiDefPath))
                _widgetRenderer.LoadDefinitions(_widgetRendererUiDefPath);
        };
        Necroking.Core.DebugLog.Log("startup", "  [LazyInit] UI editor initialized on demand");
    }

    private long _startupLastMs;
    private void LogTiming(string step)
    {
        if (_startupTimer == null) return;
        long now = _startupTimer.ElapsedMilliseconds;
        DebugLog.Log("startup", $"  [{now}ms +{now - _startupLastMs}ms] {step}");
        _startupLastMs = now;
    }
    // Per-game world state lives in GameSession; StartGame recreates it so nothing carries
    // over between maps and its Dispose() frees the GPU resources. These forwarding
    // properties keep every existing _groundSystem/_envSystem/... call site unchanged.
    internal GameSession _session = new();
    internal GroundSystem _groundSystem => _session.Ground;
    internal EnvironmentSystem _envSystem => _session.Env;
    internal WallSystem _wallSystem => _session.Wall;
    internal RoadSystem _roadSystem => _session.Road;
    internal TriggerSystem _triggerSystem = new();

    // Grass
    internal byte[] _grassMap = Array.Empty<byte>();
    internal int _grassW, _grassH;
    private string[] _grassTypeIds = Array.Empty<string>();
    private string[] _grassTypeNames = Array.Empty<string>();
    private Color[] _grassDefaultTints = Array.Empty<Color>();
    private Color[] _grassCorruptedTints = Array.Empty<Color>();
    // Per-type sprite paths (parallel array to the color arrays). One entry per grass
    // type; each entry is a list of 0-5 project-relative PNG paths. Drives the tuft
    // renderer. Populated from the map editor (SyncGrassFromEditor) or the scenario
    // setup path.
    private string[][] _grassTypeSpritePaths = Array.Empty<string[]>();
    private float[] _grassTypeScales = Array.Empty<float>();
    private float[] _grassTypeDensities = Array.Empty<float>();
    internal GrassTuftRenderer _grassRenderer = new();

    // Rendering
    internal Renderer _renderer = new();
    internal Camera25D _camera = new();
    internal SpriteAtlas[] _atlases = new SpriteAtlas[0]; // rebuilt in LoadContent from AtlasDefs.TotalCount
    internal Dictionary<uint, UnitAnimData> _unitAnims = new(); // keyed by stable unit ID
    internal Dictionary<int, UnitAnimData> _corpseAnims = new(); // keyed by corpse ID

    // (Carried body-bag offsets/scale moved to GameRenderer.)
    // Cache of opaque-pixel centroids per (atlas texture, frame rect), in frame-local
    // top-left pixels. Computed once via GetData; used to balance carried corpses.
    internal readonly Dictionary<(Microsoft.Xna.Framework.Graphics.Texture2D, Rectangle), Vector2> _frameCentroidCache = new();
    private bool _autostartExitPending; // --autostart headless: exit once world is loaded
    internal Dictionary<string, Flipbook> _flipbooks = new(); // keyed by flipbook ID

    /// <summary>(Re)build the runtime flipbook dictionary from the registry.
    /// Called at StartGame and by the spell editor after flipbook edits so
    /// path/grid changes take effect immediately (previews + game) instead of
    /// waiting for a map reload. Disposes the previous load's textures.</summary>
    internal void ReloadFlipbooksFromRegistry()
    {
        foreach (var oldFb in _flipbooks.Values) oldFb.Unload();
        _flipbooks.Clear();
        foreach (var fbId in _gameData.Flipbooks.GetIDs())
        {
            var fbDef = _gameData.Flipbooks.Get(fbId);
            if (fbDef == null || string.IsNullOrEmpty(fbDef.Path)) continue;
            var resolvedPath = GamePaths.Resolve(fbDef.Path);
            if (!File.Exists(resolvedPath)) continue;
            var fb = new Flipbook();
            if (fb.Load(GraphicsDevice, resolvedPath, fbDef.Cols, fbDef.Rows, fbDef.DefaultFPS))
                _flipbooks[fbId] = fb;
        }
        // Systems holding flipbook lookups keep working: the dictionary
        // INSTANCE is stable (cleared + repopulated in place).
        _wakeSystem.Init(_flipbooks);
    }
    internal Dictionary<string, AnimationMeta> _animMeta = new(); // animation metadata
    internal Microsoft.Xna.Framework.Graphics.Effect? _groundEffect;
    internal Microsoft.Xna.Framework.Graphics.Effect? _dissolveTreeEffect;
    internal Microsoft.Xna.Framework.Graphics.Effect? _outlineFlatEffect;
    internal Microsoft.Xna.Framework.Graphics.Effect? _morphSdfEffect; // reanimation SDF body morph
    internal Microsoft.Xna.Framework.Graphics.Effect? _depthCutoutEffect; // depth-only occluder stamp (depth-sorted fog)
    internal Microsoft.Xna.Framework.Graphics.Effect? _wadingEffect;
    private Microsoft.Xna.Framework.Graphics.Effect? _hdrIntensityEffect;
    internal readonly Render.WadingWakeSystem _wakeSystem = new();
    /// <summary>Cached gameplay delta from the last Update tick. Drives
    /// frame-rate-independent systems that need dt during the Draw pass
    /// (e.g. wading wake particles). Respects pause and time scale.
    /// (Forwarder — the value lives on <see cref="_clock"/>.)</summary>
    internal float _frameDt => _clock.VisualDt;
    internal Microsoft.Xna.Framework.Graphics.Effect? _hdrSpriteEffect;
    internal Texture2D? _groundVertexMapTex;
    internal EffectManager _effectManager = new();
    internal BuffVisualSystem _buffVisuals = new();
    internal readonly List<Data.Registries.BuffDef> _wpDefsCache = new(); // reused per-unit in DrawSingleUnit
    internal BloomRenderer _bloom = new();
    internal WeatherRenderer _weatherRenderer = new();
    internal FogOfWarSystem _fogOfWar = new();

    // Window mode (Alt+Enter toggles). Default false = the borderless windowed
    // "fullscreen" set up in the constructor (with the +1 height trick that keeps
    // the OS from treating it as exclusive fullscreen, which breaks screenshots).
    // true = a normal resizable bordered window.
    private bool _windowedMode;
    private int _windowedW = 1280, _windowedH = 720;
    private bool _handlingResize;
    internal Color _ambientColor = Color.White; // weather ambient tint, applied to lit sprites before bloom
    private DayNightSystem _dayNightSystem = new();
    internal LightningRenderer _lightningRenderer = new();
    internal PoisonCloudRenderer _poisonCloudRenderer = new();
    internal DeathFogRenderer _deathFogRenderer = new();
    internal GroundFogSystem _groundFog = new();   // depth-stamped ground-fog volume (wisps + blanket)
    internal ReanimEffectSystem _reanimFx = new();
    internal Render.ReanimMorph _reanimMorph = new();   // SDF body-morph data cache for the reanimation rise
    internal MagicGlyphRenderer _glyphRenderer = new();
    internal DebugDraw _debugDraw = new();
    internal List<GameSystems.DamageNumber> _damageNumbers = new();

    // Game state
    internal MenuState _menuState = MenuState.MainMenu;
    /// <summary>Last frame's <see cref="_menuState"/>, snapshotted at end of
    /// Update. Used to detect transitions — specifically MapEditor → anything
    /// else, where the suppressed per-click pathfinder rebuilds during the
    /// editor session need to fire once so gameplay resumes with current
    /// collision data.</summary>
    private MenuState _prevMenuState = MenuState.MainMenu;
    internal bool _gameWorldLoaded;
    /// <summary>True while anything holds the game paused (forwarder for
    /// <see cref="Core.GameClock.Paused"/>). Write via <c>_clock.Pause / Resume /
    /// TogglePause / ClearAllPauses</c> with a <see cref="Core.GameClock.PauseSource"/>.</summary>
    internal bool _paused => _clock.Paused;

    /// <summary>True while any full-screen editor (map / unit / spell / UI / item) owns
    /// the screen. Fed into <c>_clock.GateWorld(worldSuspended:)</c> each frame — editors
    /// freeze the world by zeroing <see cref="Core.GameClock.WorldDt"/>, so gameplay code
    /// never needs to consult this directly: consume WorldDt (not VisualDt) and the
    /// editor gate comes for free.</summary>
    internal bool EditorActive => _menuState == MenuState.UnitEditor || _menuState == MenuState.SpellEditor
        || _menuState == MenuState.MapEditor || _menuState == MenuState.UIEditor
        || _menuState == MenuState.ItemEditor;
    internal bool _gameOver;

    /// <summary>Central time & pause authority — see <see cref="Core.GameClock"/> for the
    /// full domain docs (RawDt / RealDt / VisualDt+VisualTime / WorldDt+WorldRunning,
    /// pause sources). Driven two-phase from Update: BeginFrame at the dt derivation
    /// point, GateWorld right before the sim gate. The legacy fields below (_gameTime,
    /// _timeScale, _rawDt, _frameDt) are read-forwarders kept so the many renderer
    /// call sites don't churn.</summary>
    internal readonly Core.GameClock _clock = new();

    /// <summary>Visual/presentation clock (forwarder for <see cref="Core.GameClock.VisualTime"/>):
    /// phase driver for wind/shader/pulse visuals. Never reset — NOT the world's age;
    /// for "how long has this world existed" use <c>_sim.GameTime</c>.</summary>
    internal float _gameTime => _clock.VisualTime;
    internal float _timeScale
    {
        get => _clock.TimeScale;
        set => _clock.SetTimeScale(value);
    }

    // Glyph trap placement mode
    internal PendingSpellCast _pendingSpell = new();
    private PendingCastAnim? _pendingCastAnim;
    internal SpellBarState _spellBarState = new();
    // True when the bar was seeded for a test context (test maps, scenario
    // DebugSpells) — SaveSpellBars then skips writing, so test runs can't
    // stomp the player's per-machine spellbar.json on exit.
    private bool _spellBarSeededForTest;

    // Per-slot "just activated" flash timers (seconds remaining), decayed in real
    // time. Set when a slot successfully fires a spell; the HUD draws a fading
    // highlight so a keypress visibly lights up its hotbar slot. Duration is owned
    // by HUDRenderer.SlotFlashDuration (single source of truth for the fade math).
    internal readonly float[] _slotFlash = new float[SpellBarBindings.SlotCount];

    // Dev cursor override for headless hover testing (set via the `mousepos` dev command).
    private Microsoft.Xna.Framework.Vector2? _devMouseOverride;
    // Menu state as of the last input capture — detects mode transitions so the
    // in-flight press gesture can be invalidated (see Update, ConsumeGesture).
    private MenuState _menuStateAtLastCapture;
    internal int _channelingSlot = -1;
    private readonly TextureCache _itemTextureCache = new();
    internal int _hoveredObjectIdx = -1;
    internal int _hoveredCorpseIdx = -1;
    internal int _hoveredUnitIdx = -1;
    // Unit id of a hovered forager (zombie boar). Foragers suppress the normal
    // right-side unit stat sheet and instead show a corpse-pile-style belly tooltip
    // listing the mushrooms they've eaten. uint.MaxValue when none is hovered.
    internal uint _hoveredBellyUnitId = uint.MaxValue;
    // Screen-space outline boxes for the hovered world object, captured during the
    // sprite draw pass (exact sprite bounds) and drawn in the HUD overlay pass.
    // Reset to null each frame at the top of Draw. See ShowHoverHighlight setting.
    internal Rectangle? _hoverBoxObject, _hoverBoxCorpse, _hoverBoxUnit;
    // --- Hover-highlight style variant cycling (design test harness; cycle with 'H') ----
    // 13 states: 0-11 = 3 shapes (Circle / Corners / Rectangle) × 4 line styles
    // (Hover-marker ground geometry constants moved to GameRenderer.)
    // Dev override for the hover-highlight variant (shape*4 + style). -1 = OFF (use the per-category
    // Tooltips settings — the normal path); 0..19 forces one variant on everything; 20 = highlight off.
    internal int _hoverHighlightVariant = -1;
    internal float _hoverVariantLabelTimer;     // seconds left to show the "which variant" toast
    internal float _depthFogToastTimer;         // seconds left to show the depth-fog ON/OFF toast ('H' key)
    // Hybrid-GPU warning (see Core/GpuPreference.cs): set once in LoadContent when the
    // game is rendering on an integrated GPU while a discrete one is installed.
    internal float _gpuWarnToastTimer;
    internal string? _gpuWarnToastMsg;
    // Dev: pin the hovered unit (headless testing has no real mouse). uint.MaxValue = off.
    private uint _devForceHoverUnitId = uint.MaxValue;
    // Dev: pin the hovered env object by index (headless variant testing). -1 = off.
    private int _devForceHoverObjectIdx = -1;
    // Dev: detach the camera from the necromancer so dev 'camera' commands stick
    // (lets headless testing pan freely WITHOUT killing the necromancer / triggering game-over).
    private bool _devFreeCamera;
    // Dev-marked units (via the 'mark' dev command): persistent white boxes,
    // independent of mouse hover and the ShowHoverHighlight setting. Keyed by
    // stable unit Id; their on-screen boxes are recaptured each frame in Draw.
    internal readonly HashSet<uint> _devMarkedUnitIds = new();
    internal readonly List<Rectangle> _devMarkBoxes = new();
    private KeyboardState _prevKb;
    private MouseState _prevMouse;
    /// <summary>Unclamped wall-clock frame delta (forwarder for <see cref="Core.GameClock.RawDt"/>).</summary>
    internal float _rawDt => _clock.RawDt;

    /// <summary>F2 — overlay raw waterness, computed waterline V, slope, and
    /// the body-bbox bounds on each wading unit. Tuning helper for the per-
    /// direction WadingFractionByDirection values.</summary>
    internal bool _waterDebug;
    // Bottom-left perf/zoom readout (frame/sim/draw/present ms). Off by default —
    // toggle with F3 when debugging; it otherwise just clutters the screen.
    internal bool _showPerfReadout;

    // Per-frame perf timers — populated each Draw, smoothed via EMA for the
    // HUD readout so the numbers don't jitter. Stale frames keep the EMA
    // value so a paused game shows the last working number.
    internal readonly System.Diagnostics.Stopwatch _drawStopwatch = new();
    internal readonly System.Diagnostics.Stopwatch _groundDrawStopwatch = new();
    internal double _drawMsAvg;
    internal double _groundMsAvg;
    internal double _gpuPresentMsAvg;
    internal readonly InputState _input = new();
    private readonly Necroking.UI.PopupManager _popups = new();
    /// <summary>Central per-frame catalogue of every active UI region (popup
    /// panels, HUD buttons/bars, toasts). Rebuilt in <see cref="RebuildUIHitRects"/>
    /// each Update; the single source for <c>_input.MouseOverUI</c>. Inspect live
    /// via the <c>ui_rects</c> dev command.</summary>
    internal readonly Necroking.UI.UIHitRegistry _uiHits = new();
    /// <summary>The unified UI layer router — one z-ordered list that input
    /// walks top-down (reverse render order) and drawing will walk bottom-up.
    /// Layers are registered in the ctor; see <see cref="Necroking.UI.UIRouter"/>.</summary>
    internal readonly Necroking.UI.UIRouter _uiRouter = new();
    /// <summary>Process-wide accessor — popups call <c>Game1.Popups.Push(this)</c>
    /// on open and <c>Pop</c> on close. Static because the alternative is
    /// threading the manager through 20+ existing UI constructors. Lifetime
    /// matches the Game1 instance; assigned in the ctor.</summary>
    public static Necroking.UI.PopupManager Popups { get; private set; } = null!;

    /// <summary>Global tooltip service — any UI code (HUD, panels, editors)
    /// requests a tooltip during Update/Draw and TooltipHostLayer draws the
    /// queue in the Tooltip band: topmost, after all scissor clips close.
    /// Static for the same reason as <see cref="Popups"/>; assigned in the ctor.</summary>
    public static Necroking.UI.TooltipSystem Tooltips { get; private set; } = null!;

    /// <summary>The running Game1 instance, for editor components that need a
    /// narrow runtime hook (e.g. the spell editor reloading flipbooks after an
    /// edit) without threading the game through their constructors. Same
    /// lifetime rationale as <see cref="Popups"/>; assigned in the ctor.</summary>
    public static Game1 Instance { get; private set; }

    // (The per-editor/menu ActionModalLayers + ReconcileTopLevelEditorLayers
    // are gone: the full-screen editors sit in the router as one opaque
    // EditorHostLayer and the pause-menu family as one MenuHostLayer — see
    // UI/Layers/HostLayers.cs. Game1.Popups now only carries the editors'
    // transient sub-popups, seated in the router via ModalStackLayer.)

    // Pending spell cast with animation delay (Spell1 animation → action moment → execute)
    private struct PendingCastAnim
    {
        public string SpellID;
        public Vec2 Target;
        public int Slot;           // spellbar slot that was used
        public string? CastingBuffID; // to remove on animation end

        // Channeled casts (CastAnim ImbueGround/Raise): a Start→Loop→Finish state
        // machine. The effect fires at the END of the loop. Empty/"Spell1" = the
        // legacy single-shot path (effect on the Spell1 effect frame).
        public string CastAnim;
        public byte ChannelPhase;     // 0=Start, 1=Loop, 2=Finish
        public float ChannelElapsed;  // seconds since the cast began
        public float LoopElapsed;     // seconds spent in the Loop phase
        public float CastTime;        // total target cast time (start + loop)
        public bool Executed;         // effect already fired (end of loop)

        // Cast plant (todos/player_cast_plant.md): true from dispatch until the
        // player has braked below the anim-start gate. While set, the cast anim
        // has NOT started (locomotion keeps playing the skid), the channel state
        // machine and the left-Spell1 safety net are both suspended, and movement
        // input cannot cancel the cast (committed at press, Q2).
        public bool WaitingForPlant;
    }

    private static bool IsChanneledCast(string? castAnim)
        => castAnim == "ImbueGround" || castAnim == "Raise" || castAnim == "ImbueTable";

    /// <summary>Resolve the Start/Loop/Finish anim states for a channeled cast.
    /// Raise has no Finish (finish == null → go straight to Idle after the loop).</summary>
    private static void GetChannelStates(string castAnim, out AnimState start, out AnimState loop, out AnimState? finish)
    {
        switch (castAnim)
        {
            case "Raise":
                start = AnimState.RaiseStart; loop = AnimState.RaiseLoop; finish = null; break;
            case "ImbueTable":
                // The over-the-corpse "working at a table" animation, but cast on a
                // loose ground corpse (no table). Start→Loop→Finish like ImbueGround.
                start = AnimState.ImbueTableStart; loop = AnimState.ImbueTableLoop; finish = AnimState.ImbueTableFinish; break;
            default: // "ImbueGround"
                start = AnimState.ImbueGroundStart; loop = AnimState.ImbueGroundLoop; finish = AnimState.ImbueGroundFinish; break;
        }
    }

    // Pending projectiles (multi-projectile delay)
    internal readonly List<GameSystems.PendingProjectileGroup> _pendingProjectiles = new();

    // Editors
    internal MapEditorWindow _mapEditor = new();
    internal UIEditorWindow _uiEditor = new();
    internal EditorBase _editorUi = new();
    internal UnitEditorWindow _unitEditor = null!;
    internal SpellEditorWindow _spellEditor = null!;
    internal ItemEditorWindow _itemEditor = null!;
    internal SettingsWindow _settingsWindow = null!;

    // Random
    internal readonly Random _rng = new();

    // Collision debug
    internal CollisionDebugMode _collisionDebugMode = CollisionDebugMode.Off;

    // Gameplay debug (F7): 0=Off, 1=Horde, 2=Unit Info
    internal int _gameplayDebugMode;

    // Wind debug (F6): shows gust heatmap + direction arrow
    internal bool _windDebug;

    // Scenario state
    internal ScenarioBase? _activeScenario;

    // Lean dev control server (Necroking/Dev/DevServer.cs). Non-null only when
    // launched with --devserver <port>. Commands are queued on its listener
    // thread and executed here on the main thread via ExecuteDevCommand.
    private Necroking.Dev.DevServer? _devServer;
    private bool _taskbarHidden;                   // headless: off-screen window's taskbar button dropped once
    private bool _devWindowShown;                  // dev `window show`: headless window surfaced on-screen for the user
    private string? _pendingDevScreenshot;        // set by a screenshot cmd, consumed in Draw
    private Necroking.Dev.DevCommand? _pendingDevScreenshotCmd; // completed once the PNG is written
    private int _devShotW, _devShotH;             // downsample target for the pending shot (0 = native)
    internal bool _devShotNoUi, _devShotNoGround;  // suppress UI / ground for the pending shot's frame
    private Necroking.Dev.DevJob? _devJob;        // active batch script (stepped each frame in Update)
    private int _devJobSeq;                        // id counter for batch jobs
    private Vec2? _devWalkTarget;                  // dev "walk_necro" goal; drives WASD-equivalent input, cancelled by any WASD press
    private bool _devWalkSprint;                   // dev "walk_necro" sprint=true opt: hold virtual Shift while auto-walking
    internal int _scenarioScrollOffset;           // scenario-menu scroll, in layout rows
    internal bool _scenarioScrollDragging;         // dragging the scenario-menu scrollbar thumb
    private float _scenarioScrollGrabOffset;        // Y within the thumb where the drag started

    // --- Tethers / drag ropes (Shift+T target, Shift+R attach) ---
    // A tether connects two endpoints, each a live unit or a corpse. When a unit end is
    // farther than RopeMaxLength from a corpse end, the unit hauls the corpse in behind
    // it (dust + corpse turns to face the pull; the necromancer is also slowed). Ropes
    // are drawn as bezier curves in GameRenderer.DrawRope.
    internal enum TetherEndKind { Unit, Corpse }
    internal struct TetherEnd
    {
        public TetherEndKind Kind;
        public uint UnitId;   // valid when Kind == Unit
        public int CorpseId;  // valid when Kind == Corpse
        public static TetherEnd ForUnit(uint id) => new TetherEnd { Kind = TetherEndKind.Unit, UnitId = id, CorpseId = -1 };
        public static TetherEnd ForCorpse(int id) => new TetherEnd { Kind = TetherEndKind.Corpse, CorpseId = id, UnitId = GameConstants.InvalidUnit };
    }
    internal struct Tether { public TetherEnd A; public TetherEnd B; }

    internal readonly System.Collections.Generic.List<Tether> _tethers = new();
    private TetherEnd? _tetherAnchor;               // endpoint picked by Shift+T, consumed by Shift+R
    internal const float RopeMaxLength = 4.0f;      // world units; beyond this a corpse gets dragged
    private const float RopeAttachRadius = 5.0f;    // pick radius for tether endpoints under the cursor
    private int _ropeDustAngle;                     // rotating offset so dust puffs spread around the corpse
    // Per-corpse accumulated drag distance since its last dust puff (keyed by CorpseID).
    private readonly System.Collections.Generic.Dictionary<int, float> _tetherDustAccum = new();

    // Per-unit animation data
    internal struct UnitAnimData
    {
        public AnimController Ctrl;
        public int AtlasID; // index into AtlasDefs.Names
        public float RefFrameHeight;
        public string CachedDefID;
    }

    // Foragable arc-pickup state was moved to Game.ForagableSystem (the first
    // Game1 subsystem split, 2026-05-13). Game1 owns the instance and the
    // pickup-sound asset; everything else lives in the system.
    private SoundEffect? _pickupSound;
    // Throttle + pitch-rotation for the boar-eat pickup pop, so several boars
    // grazing at once don't machine-gun the same sample.
    private float _foragerEatSoundCd;
    private int _foragerEatSoundStep;
    internal readonly Game.ForagableSystem _foragables = new();
    private readonly Game.Jobs.WorkerSystem _workerSystem = new();
    internal readonly UI.GraveRosterUI _graveRosterUI = new();
    internal readonly UI.JobBoardUI _jobBoardUI = new();
    // (ForagableWiggleRange moved to GameRenderer.)

    // DamageNumber and PendingProjectileGroup moved to GameSystems.SpellEffectSystem

    /// <summary>The draw pipeline, split out of Game1 into its own class (the former
    /// Game1.Render*.cs partials). Reaches back into Game1 state via a reference to this.</summary>
    internal readonly GameRenderer _gameRenderer;

    public Game1()
    {
        Popups = _popups;
        Tooltips = new Necroking.UI.TooltipSystem(this);
        Instance = this;
        _gameRenderer = new GameRenderer(this);

        // Editors read mouse state live through the shared InputState (see
        // EditorBase._mouse) — wire it up before any editor can draw so they
        // never sit on a stale default instance.
        _editorUi._input = _input;
        _uiEditor._input = _input;

        // Unified UI layer list (see UI/UIRouter.cs): input dispatches through
        // these top-down in reverse render order, so the topmost visible widget
        // gets the click, hover ownership matches click ownership, and drawing
        // (phase 3) walks the same list bottom-up. Registration order is the
        // within-band tiebreak (later = on top), mirroring the historical
        // DrawHudBlock order within the Panels band.
        _uiRouter.Register(new Necroking.UI.WorldInputLayer(this));
        _uiRouter.Register(new Necroking.UI.HudChromeLayer(this)); // draw-only HUD chrome, bottom of the Hud band
        _uiRouter.Register(new Necroking.UI.SpellBarLayer(this));
        _uiRouter.Register(new Necroking.UI.TimeControlsLayer(this));
        _uiRouter.Register(new Necroking.UI.AggressionBarLayer(this));
        // Tooltips live in the Tooltip band (topmost) so they draw over every
        // other layer: the host runs the HUD cursor-tooltip funnel and then
        // drains the global Game1.Tooltips queue — see TooltipHostLayer.
        _uiRouter.Register(new Necroking.UI.TooltipHostLayer(this));

        // In-game panels (Panels band). Ids keep the "popup.<Type>" convention
        // the hit registry has always used. Update delegates own each panel's
        // per-frame input logic; drag providers get router mouse capture; the
        // WithDraw delegate is the panel's slot in the unified draw pass (the
        // ShowUIForDraw gate replaces DrawHudBlock's old inline showUI checks).
        _uiRouter.Register(new Necroking.UI.PanelLayer(_uiRouter, _inventoryUI,
            Necroking.UI.UIBand.Panels, "popup.InventoryUI",
            () => _inventoryUI.IsVisible,
            (InputState inp, in Necroking.UI.UICtx c) => _inventoryUI.Update(inp),
            () => _inventoryUI.IsDragging)
            .WithDraw((in Necroking.UI.UICtx c) => _inventoryUI.Draw(c.ScreenW, c.ScreenH),
                () => ShowUIForDraw && _inventoryUI.IsVisible));
        _uiRouter.Register(new Necroking.UI.PanelLayer(_uiRouter, _tableMenuUI,
            Necroking.UI.UIBand.Panels, "popup.TableCraftMenuUI",
            () => _tableMenuUI.IsVisible,
            (InputState inp, in Necroking.UI.UICtx c) => _tableMenuUI.Update(inp))
            .WithDraw((in Necroking.UI.UICtx c) => _tableMenuUI.Draw(),
                () => ShowUIForDraw && _tableMenuUI.IsVisible));
        _uiRouter.Register(new Necroking.UI.PanelLayer(_uiRouter, _graveRosterUI,
            Necroking.UI.UIBand.Panels, "popup.GraveRosterUI",
            () => _graveRosterUI.IsVisible,
            (InputState inp, in Necroking.UI.UICtx c) => _graveRosterUI.Update(inp))
            .WithDraw((in Necroking.UI.UICtx c) => _graveRosterUI.Draw(),
                () => ShowUIForDraw && _graveRosterUI.IsVisible));
        _uiRouter.Register(new Necroking.UI.PanelLayer(_uiRouter, _jobBoardUI,
            Necroking.UI.UIBand.Panels, "popup.JobBoardUI",
            () => _jobBoardUI.IsVisible,
            (InputState inp, in Necroking.UI.UICtx c) => _jobBoardUI.Update(inp),
            () => _jobBoardUI.IsDragging)
            .WithDraw((in Necroking.UI.UICtx c) => _jobBoardUI.Draw(),
                () => ShowUIForDraw && _jobBoardUI.IsVisible));
        _uiRouter.Register(new Necroking.UI.PanelLayer(_uiRouter, _characterStatsUI,
            Necroking.UI.UIBand.Panels, "popup.CharacterStatsUI",
            () => _characterStatsUI.IsVisible) // input-in-Draw panel: no Update, masked in Draw
            .WithDraw((in Necroking.UI.UICtx c) => _characterStatsUI.Draw(c.ScreenW, c.ScreenH,
                    _sim, _gameData.Buffs, ref _spellBarState, _input, _gameData, _skillBookState),
                () => ShowUIForDraw && _characterStatsUI.IsVisible));
        _uiRouter.Register(new Necroking.UI.PanelLayer(_uiRouter, _unitInfoPanel,
            Necroking.UI.UIBand.Panels, "popup.UnitInfoPanel",
            // Transient (auto-hover) views must not claim hits/ESC — only a
            // pinned sheet is a real layer. Drawing includes the transient view.
            () => _unitInfoPanel.IsVisible && !_unitInfoPanel.IsTransient)
            .WithDraw((in Necroking.UI.UICtx c) => _unitInfoPanel.Draw(c.ScreenW, c.ScreenH, _sim),
                () => ShowUIForDraw && _unitInfoPanel.IsVisible));
        _uiRouter.Register(new Necroking.UI.PanelLayer(_uiRouter, _grimoireOverlay,
            Necroking.UI.UIBand.Panels, "popup.GrimoireOverlay",
            () => _grimoireOverlay.IsVisible,
            (InputState inp, in Necroking.UI.UICtx c) => _grimoireOverlay.Update(inp, c.ScreenW, c.ScreenH))
            .WithDraw((in Necroking.UI.UICtx c) => _grimoireOverlay.Draw(c.ScreenW, c.ScreenH),
                () => ShowUIForDraw && _grimoireOverlay.IsVisible));
        _uiRouter.Register(new Necroking.UI.PanelLayer(_uiRouter, _buildingMenuUI,
            Necroking.UI.UIBand.Panels, "popup.BuildingMenuUI",
            () => _buildingMenuUI.IsVisible,
            (InputState inp, in Necroking.UI.UICtx c) => _buildingMenuUI.Update(inp, c.ScreenW, c.ScreenH))
            .WithDraw((in Necroking.UI.UICtx c) =>
            {
                _buildingMenuUI.DrawMenu();
                // Ghost preview for building placement rides with the menu.
                if (_buildingMenuUI.IsPlacementActive)
                {
                    Vec2 mw = _camera.ScreenToWorld(_input.MousePos, c.ScreenW, c.ScreenH);
                    var sp = _renderer.WorldToScreen(mw, 0f, _camera);
                    _buildingMenuUI.DrawGhostPreview(_spriteBatch, _pixel, mw, sp, _camera);
                }
            }, () => ShowUIForDraw && _buildingMenuUI.IsVisible));
        _uiRouter.Register(new Necroking.UI.PanelLayer(_uiRouter, _craftingMenu,
            Necroking.UI.UIBand.Panels, "popup.CraftingMenuUI",
            () => _craftingMenu.IsVisible,
            (InputState inp, in Necroking.UI.UICtx c) => _craftingMenu.Update(inp, c.ScreenW, c.ScreenH, _clock.VisualDt))
            .WithDraw((in Necroking.UI.UICtx c) => _craftingMenu.Draw(),
                () => ShowUIForDraw && _craftingMenu.IsVisible));

        // Blocking full overlay (Overlay band), then the HUD rows that sit
        // ABOVE it (HudTop band) — the declarative form of "menu buttons work
        // over the open skill book", for clicks AND drawing alike.
        _uiRouter.Register(new Necroking.UI.PanelLayer(_uiRouter, _skillBookOverlay,
            Necroking.UI.UIBand.Overlay, "popup.SkillBookOverlay",
            () => _skillBookOverlay.IsVisible,
            (InputState inp, in Necroking.UI.UICtx c) => _skillBookOverlay.Update(inp, c.ScreenW, c.ScreenH, c.TimeSec),
            () => _skillBookOverlay.IsDragging)
            .WithDraw((in Necroking.UI.UICtx c) => _skillBookOverlay.Draw(c.ScreenW, c.ScreenH),
                () => ShowUIForDraw && _skillBookOverlay.IsVisible));
        _uiRouter.Register(new Necroking.UI.CoreMenuButtonsLayer(this));
        _uiRouter.Register(new Necroking.UI.EditorLauncherLayer(this));
        _uiRouter.Register(new Necroking.UI.SkillToastLayer(this));

        // Menus, editors, and the editors' transient sub-popup stack, top bands.
        // The map editor gets its own panel-like seat (non-blocking, footprint =
        // its side panel) and the UI editor its own modal seat (blocking,
        // footprint = its centered window); the remaining full-screen editors
        // share the opaque EditorHostLayer. Mutually exclusive via _menuState.
        _uiRouter.Register(new Necroking.UI.MenuHostLayer(this));
        _uiRouter.Register(new Necroking.UI.MapEditorLayer(this, _popups));
        _uiRouter.Register(new Necroking.UI.UIEditorLayer(this));
        _uiRouter.Register(new Necroking.UI.EditorHostLayer(this));
        _uiRouter.Register(new Necroking.UI.ModalStackLayer(_popups));

        _graphics = new GraphicsDeviceManager(this);
        _graphics.GraphicsProfile = GraphicsProfile.HiDef;

        if (LaunchArgs.ResolutionW > 0 && LaunchArgs.ResolutionH > 0)
        {
            // Windowed mode at specified resolution
            _graphics.PreferredBackBufferWidth = LaunchArgs.ResolutionW;
            _graphics.PreferredBackBufferHeight = LaunchArgs.ResolutionH;
            _graphics.IsFullScreen = false;
        }
        else
        {
            // Windowed borderless "fullscreen" with +1 height trick:
            // Prevents OS from treating it as exclusive fullscreen, which breaks screenshots
            var adapter = GraphicsAdapter.DefaultAdapter;
            _graphics.PreferredBackBufferWidth = adapter.CurrentDisplayMode.Width;
            _graphics.PreferredBackBufferHeight = adapter.CurrentDisplayMode.Height + 1;
            _graphics.IsFullScreen = false;
            Window.IsBorderless = true;
        }

        Content.RootDirectory = "resources";
        IsMouseVisible = true;

        // Recreate screen-sized render targets when the window resizes (e.g. the
        // user drags the edge in windowed mode). Fog-of-war targets are world-sized
        // so they're untouched; only bloom + weather follow the back buffer.
        Window.ClientSizeChanged += OnClientSizeChanged;

        // Save settings (to the per-machine 'user settings/', gitignored), weather
        // presets, and spell bar slot assignments when the game exits. Atomic writes.
        Exiting += (_, _) =>
        {
            System.IO.Directory.CreateDirectory(GamePaths.Resolve(GamePaths.UserSettingsDir));
            _gameData.Settings.General.MapEditorLastTab = (int)_mapEditor.ActiveTab;
            _gameData.Settings.Save(GamePaths.Resolve(GamePaths.UserSettingsJson));
            _gameData.Weather.Save(GamePaths.Resolve(GamePaths.UserWeatherJson));
            SaveSpellBars();
        };

        if (LaunchArgs.Headless)
        {
            // Hide window for headless mode — use larger resolution if user specifies one
            Window.IsBorderless = true;
            _graphics.PreferredBackBufferWidth = LaunchArgs.ResolutionW > 0 ? LaunchArgs.ResolutionW : 320;
            _graphics.PreferredBackBufferHeight = LaunchArgs.ResolutionH > 0 ? LaunchArgs.ResolutionH : 240;
            _graphics.IsFullScreen = false;
        }

        // Unlock frame-rate up front (must happen before GraphicsDevice is created
        // — toggling SynchronizeWithVerticalRetrace mid-run is unreliable on the
        // DesktopGL backend). Lets benchmark scenarios measure raw GPU throughput.
        if (LaunchArgs.NoVsync)
        {
            _graphics.SynchronizeWithVerticalRetrace = false;
            IsFixedTimeStep = false;
        }
    }

    /// <summary>Alt+Enter: flip between the borderless windowed "fullscreen" and a
    /// normal resizable window.</summary>
    private void ToggleWindowMode() => ApplyWindowMode(!_windowedMode);

    /// <summary>Apply windowed (bordered, resizable) or borderless-fullscreen mode.
    /// Borderless keeps the +1 height trick so the OS doesn't switch to exclusive
    /// fullscreen (which breaks screenshots and the dev-server frame capture).</summary>
    private void ApplyWindowMode(bool windowed)
    {
        if (LaunchArgs.Headless) return; // headless owns its own tiny hidden window
        _windowedMode = windowed;
        var dm = GraphicsAdapter.DefaultAdapter.CurrentDisplayMode;
        if (windowed)
        {
            Window.IsBorderless = false;
            Window.AllowUserResizing = true;
            _graphics.IsFullScreen = false;
            _graphics.PreferredBackBufferWidth = _windowedW;
            _graphics.PreferredBackBufferHeight = _windowedH;
            _graphics.ApplyChanges();
            // Center on the primary display.
            Window.Position = new Point(
                Math.Max(0, (dm.Width - _windowedW) / 2),
                Math.Max(0, (dm.Height - _windowedH) / 2));
        }
        else
        {
            // Borderless windowed "fullscreen" with the +1 height trick.
            Window.AllowUserResizing = false;
            Window.IsBorderless = true;
            _graphics.IsFullScreen = false;
            _graphics.PreferredBackBufferWidth = dm.Width;
            _graphics.PreferredBackBufferHeight = dm.Height + 1;
            _graphics.ApplyChanges();
            Window.Position = Point.Zero;
        }
        RefreshScreenSizedTargets();

        // Remember the chosen mode + windowed size so a restart reopens the same
        // window (persisted to settings.json by the Exiting handler).
        var disp = _gameData.Settings.Display;
        disp.Windowed = windowed;
        disp.WindowedWidth = _windowedW;
        disp.WindowedHeight = _windowedH;
    }

    /// <summary>Resize the back-buffer-sized render targets (bloom, weather) and the
    /// renderer's screen dims to the current viewport. Cheap no-ops before those
    /// systems are initialized, so it's safe to call early.</summary>
    private void RefreshScreenSizedTargets()
    {
        if (GraphicsDevice == null) return;
        int w = GraphicsDevice.Viewport.Width, h = GraphicsDevice.Viewport.Height;
        if (w <= 0 || h <= 0) return;
        _renderer.SetScreenSize(w, h);
        _bloom.Resize(GraphicsDevice, w, h);
        _weatherRenderer.Resize(w, h);
    }

    private void OnClientSizeChanged(object? sender, EventArgs e)
    {
        if (_handlingResize || GraphicsDevice == null) return;
        _handlingResize = true;
        try
        {
            int w = Window.ClientBounds.Width, h = Window.ClientBounds.Height;
            if (w <= 0 || h <= 0) return;
            // In windowed mode, follow the user's drag and remember the new size so
            // toggling back to windowed later restores it.
            if (_windowedMode)
            {
                _windowedW = w; _windowedH = h;
                // Remember the dragged size so a restart reopens at it.
                _gameData.Settings.Display.WindowedWidth = w;
                _gameData.Settings.Display.WindowedHeight = h;
                if (_graphics.PreferredBackBufferWidth != w || _graphics.PreferredBackBufferHeight != h)
                {
                    _graphics.PreferredBackBufferWidth = w;
                    _graphics.PreferredBackBufferHeight = h;
                    _graphics.ApplyChanges();
                }
            }
            RefreshScreenSizedTargets();
        }
        finally { _handlingResize = false; }
    }

    protected override void Initialize()
    {
        DebugLog.Clear("startup");
        DebugLog.Log("startup", "=== Necroking MG Startup ===");

        // Register AI archetypes
        AI.ArchetypeRegistry.Register(AI.ArchetypeRegistry.PlayerControlled, "PlayerControlled", new AI.PlayerControlledHandler());
        AI.ArchetypeRegistry.Register(AI.ArchetypeRegistry.WolfPack, "WolfPack", new AI.WolfPackHandler());
        AI.ArchetypeRegistry.Register(AI.ArchetypeRegistry.RatPack, "RatPack", new AI.RatPackHandler());
        AI.ArchetypeRegistry.Register(AI.ArchetypeRegistry.DeerHerd, "DeerHerd", new AI.DeerHerdHandler());
        AI.ArchetypeRegistry.Register(AI.ArchetypeRegistry.HordeMinion, "HordeMinion", new AI.HordeMinionHandler());
        AI.ArchetypeRegistry.Register(AI.ArchetypeRegistry.PatrolSoldier, "PatrolSoldier",
            new AI.CombatUnitHandler(AI.ArchetypeRegistry.PatrolSoldier));
        AI.ArchetypeRegistry.Register(AI.ArchetypeRegistry.GuardStationary, "GuardStationary",
            new AI.CombatUnitHandler(AI.ArchetypeRegistry.GuardStationary));
        AI.ArchetypeRegistry.Register(AI.ArchetypeRegistry.ArmyUnit, "ArmyUnit",
            new AI.CombatUnitHandler(AI.ArchetypeRegistry.ArmyUnit));
        AI.ArchetypeRegistry.Register(AI.ArchetypeRegistry.ArcherUnit, "ArcherUnit",
            new AI.RangedUnitHandler(AI.ArchetypeRegistry.ArcherUnit));
        AI.ArchetypeRegistry.Register(AI.ArchetypeRegistry.CasterUnit, "CasterUnit",
            new AI.CasterUnitHandler());
        AI.ArchetypeRegistry.Register(AI.ArchetypeRegistry.SoloPredator, "SoloPredator",
            new AI.SoloPredatorHandler(opportunist: false));
        AI.ArchetypeRegistry.Register(AI.ArchetypeRegistry.AmbushPredator, "AmbushPredator",
            new AI.SoloPredatorHandler(opportunist: true));
        AI.ArchetypeRegistry.Register(AI.ArchetypeRegistry.Worker, "Worker", new AI.WorkerHandler());
        AI.ArchetypeRegistry.Register(AI.ArchetypeRegistry.CorpsePuppet, "CorpsePuppet", new AI.CorpsePuppetHandler());
        AI.ArchetypeRegistry.Register(AI.ArchetypeRegistry.Civilian, "Civilian", new AI.VillagerHandler());
        AI.ArchetypeRegistry.Register(AI.ArchetypeRegistry.Watchdog, "Watchdog", new AI.WatchdogHandler());
        _startupTimer = System.Diagnostics.Stopwatch.StartNew();
        _startupLastMs = 0;
        // The pre-LoadContent gap = OS process spawn + .NET runtime init +
        // MonoGame window/GL setup + JIT of code reached during Initialize.
        // We can split it: ProcessStartTime is when the OS forked the process;
        // ProcessStartStopwatch was reset at Main's first instruction.
        // Difference between those = pure runtime/load overhead before any of
        // our code ran. From there to here is mostly MonoGame setup.
        long mainToLoadContentMs = Program.ProcessStartStopwatch.ElapsedMilliseconds;
        long osToMainMs = (long)(DateTime.UtcNow - Program.ProcessStartTime.ToUniversalTime()).TotalMilliseconds - mainToLoadContentMs;
        DebugLog.Log("startup", $"  [pre-LoadContent: ~{osToMainMs + mainToLoadContentMs}ms total — process spawn+runtime: {Math.Max(0, osToMainMs)}ms, MonoGame init+JIT: {mainToLoadContentMs}ms]");

        if (LaunchArgs.Headless)
            Window.Position = new Point(-10000, -10000);
        else if (LaunchArgs.ResolutionW <= 0 && LaunchArgs.ResolutionH <= 0)
            Window.Position = new Point(0, 0);

        // Load game data
        _gameData.Load();

        // Restore the persisted window mode/size unless a launch override
        // (--resolution) or headless mode owns the window (the constructor set the
        // borderless-fullscreen default, which is also the Display default).
        if (!LaunchArgs.Headless && LaunchArgs.ResolutionW <= 0 && LaunchArgs.ResolutionH <= 0)
        {
            var disp = _gameData.Settings.Display;
            if (disp.WindowedWidth > 0) _windowedW = disp.WindowedWidth;
            if (disp.WindowedHeight > 0) _windowedH = disp.WindowedHeight;
            if (disp.Windowed) ApplyWindowMode(true);
        }

        _inventory = new Inventory(20, _gameData.Items);
        SkillBookDefs.Load();
        _skillBookState.InitFromDefs();
        LogTiming($"GameData loaded: {_gameData.Units.Count} units, {_gameData.Spells.Count} spells, {_gameData.Weapons.Count} weapons, {_gameData.Items.Count} items");

        // Init renderer
        _renderer.Init(_graphics.PreferredBackBufferWidth, _graphics.PreferredBackBufferHeight);

        // Scan for sprite sheets and load atlases (parallel file read + sequential GPU upload)
        AtlasDefs.ScanSpritesDirectory();
        int atlasCount = AtlasDefs.TotalCount;
        _atlases = new SpriteAtlas[atlasCount];

        // Phase 1: Read PNG files, decode to pixels, and parse metadata in parallel (no GPU)
        var pngBytes = new byte[atlasCount][];
        var decodedPixels = new Color[atlasCount][];
        var decodedW = new int[atlasCount];
        var decodedH = new int[atlasCount];
        var metaParsed = new bool[atlasCount];
        for (int i = 0; i < atlasCount; i++)
            _atlases[i] = new SpriteAtlas();

        // Build per-atlas work list: base sheet at index 0, overflow __N sheets after.
        // Each entry: (atlasIdx, pngPath, metaPath, isExtension).
        var extSheets = new List<(string png, string meta)>[atlasCount];
        for (int i = 0; i < atlasCount; i++)
            extSheets[i] = new List<(string, string)>(AtlasDefs.FindExtensionSheets(AtlasDefs.Names[i]));

        // Flat parallel decode of every PNG (base + all extension sheets) so the
        // largest single atlas isn't gated by its own extension on the same thread.
        // Meta parsing is fast and runs serially after decode (extension meta has
        // to be parsed after the base meta, per atlas).
        var extDecoded = new (Color[] pixels, int w, int h, bool decoded)[atlasCount][];
        for (int i = 0; i < atlasCount; i++)
            extDecoded[i] = new (Color[], int, int, bool)[extSheets[i].Count];

        // Build flat work list: (atlasIdx, extIdx=-1 for base, pngPath).
        var decodeJobs = new List<(int ai, int ei, string png)>(atlasCount * 2);
        for (int i = 0; i < atlasCount; i++)
        {
            string name = AtlasDefs.Names[i];
            decodeJobs.Add((i, -1, GamePaths.Resolve($"assets/Sprites/{name}.png")));
            for (int e = 0; e < extSheets[i].Count; e++)
                decodeJobs.Add((i, e, extSheets[i][e].png));
        }

        // BENCHMARK: per-job timing across the flat decode pool. Each job tries
        // the .pcache (zstd-compressed pre-decoded RGBA) first; on miss falls
        // back to PNG decode and writes a fresh cache for next launch.
        var decodeBench = new (string label, int sizeMb, long readMs, long decodeMs, long pmaMs, long totalMs, int threadId, bool skia, bool cacheHit, bool wroteCache)[decodeJobs.Count];
        var phaseStart = System.Diagnostics.Stopwatch.StartNew();
        System.Threading.Tasks.Parallel.For(0, decodeJobs.Count, j =>
        {
            var (ai, ei, png) = decodeJobs[j];
            string label = ei < 0 ? AtlasDefs.Names[ai] : $"{AtlasDefs.Names[ai]}__{ei + 1}";
            if (!File.Exists(png)) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int tid = System.Threading.Thread.CurrentThread.ManagedThreadId;

            // FAST PATH: try the zstd-decoded pixel cache.
            long t0 = sw.ElapsedMilliseconds;
            if (Render.AtlasCache.TryLoad(png, out var cachedPixels, out int cw, out int ch))
            {
                long readMs = sw.ElapsedMilliseconds - t0;
                if (ei < 0)
                {
                    decodedPixels[ai] = cachedPixels;
                    decodedW[ai] = cw;
                    decodedH[ai] = ch;
                }
                else
                {
                    extDecoded[ai][ei] = (cachedPixels, cw, ch, true);
                }
                int sizeMbCache = (int)(new FileInfo(Render.AtlasCache.GetCachePath(png)).Length / (1024 * 1024));
                decodeBench[j] = (label, sizeMbCache, readMs, 0, 0, sw.ElapsedMilliseconds, tid, true, true, false);
                return;
            }

            // SLOW PATH: cache miss. Decode the PNG and write a fresh cache.
            t0 = sw.ElapsedMilliseconds;
            byte[] bytes = File.ReadAllBytes(png);
            long pngReadMs = sw.ElapsedMilliseconds - t0;
            int sizeMb = bytes.Length / (1024 * 1024);
            var (pixels, w, h, decTicks, pmaTicks, skia) = TextureUtil.DecodePngPremultipliedTimed(bytes);
            long decodeMs = decTicks * 1000 / System.Diagnostics.Stopwatch.Frequency;
            long pmaMs = pmaTicks * 1000 / System.Diagnostics.Stopwatch.Frequency;
            if (ei < 0)
            {
                decodedPixels[ai] = pixels;
                decodedW[ai] = w;
                decodedH[ai] = h;
            }
            else
            {
                extDecoded[ai][ei] = (pixels, w, h, true);
            }
            // Write cache for next launch (best-effort; failure logs to startup but doesn't abort).
            Render.AtlasCache.Save(png, pixels, w, h);
            decodeBench[j] = (label, sizeMb, pngReadMs, decodeMs, pmaMs, sw.ElapsedMilliseconds, tid, skia, false, true);
        });
        long phaseWallMs = phaseStart.ElapsedMilliseconds;

        // Meta parse: sequential per atlas (base before extensions). Cheap.
        for (int i = 0; i < atlasCount; i++)
        {
            string name = AtlasDefs.Names[i];
            string metaPath = GamePaths.Resolve($"assets/Sprites/{name}.spritemeta");
            if (File.Exists(metaPath))
                metaParsed[i] = _atlases[i].ParseMetaOnly(metaPath);
            for (int e = 0; e < extSheets[i].Count; e++)
            {
                string extMeta = extSheets[i][e].meta;
                bool metaOk = File.Exists(extMeta) && _atlases[i].ParseExtensionMeta(extMeta);
                if (!metaOk) extDecoded[i][e] = (extDecoded[i][e].pixels, extDecoded[i][e].w, extDecoded[i][e].h, false);
            }
        }
        int extCount = extSheets.Sum(l => l.Count);
        LogTiming($"Atlas PNG decode + metadata parsed (flat parallel, {atlasCount} base + {extCount} ext)");
        // Aggregate parallelism across the flat decode pool.
        long sumWork = 0; var threadSet = new HashSet<int>();
        foreach (var b in decodeBench) { sumWork += b.totalMs; threadSet.Add(b.threadId); }
        int cacheHits = 0, cacheWrites = 0;
        foreach (var b in decodeBench) { if (b.cacheHit) cacheHits++; if (b.wroteCache) cacheWrites++; }
        DebugLog.Log("startup",
            $"  [BENCH] flat decode pool: wall={phaseWallMs}ms sumWork={sumWork}ms parallelism={(double)sumWork / Math.Max(1, phaseWallMs):F2}x threads={threadSet.Count} cacheHits={cacheHits}/{decodeJobs.Count} cacheWrites={cacheWrites}");
        foreach (var b in decodeBench)
            DebugLog.Log("startup",
                $"  [BENCH] {b.label,-22} {b.sizeMb,3}MB tid={b.threadId,2} read={b.readMs,4}ms decode={b.decodeMs,5}ms pma={b.pmaMs,5}ms total={b.totalMs,5}ms {(b.cacheHit ? "CACHE-HIT" : b.skia ? "skia" : "stb")}{(b.wroteCache && !b.cacheHit ? " (wrote cache)" : "")}");

        // Fresh asset log per process so it doesn't grow unbounded across runs (mirrors the
        // perf-log Clear in Simulation.Init). Prior runs' AnimMeta/buff warnings are discarded.
        DebugLog.Clear("asset");

        // Load animation metadata BEFORE GPU upload so the stride calibration pass
        // (which runs in the upload loop, while decoded pixels are still live) can
        // read per-gait cycle durations from animationmeta. Animationmeta is CPU-
        // only — no dependency on GPU textures.
        foreach (string name in AtlasDefs.Names)
        {
            string metaPath = GamePaths.Resolve($"assets/Sprites/{name}.animationmeta");
            if (File.Exists(metaPath))
                AnimMetaLoader.Load(metaPath, _animMeta);
            foreach (string extMeta in AtlasDefs.FindExtensionAnimMeta(name))
                AnimMetaLoader.Load(extMeta, _animMeta);
        }
        // Validate effect_time ONCE over the fully-loaded dict (not per-file inside Load — that
        // was O(files × keys) and dumped tens of thousands of duplicate warnings into asset.log).
        AnimMetaLoader.ValidateEffectTimes(_animMeta);
        LogTiming($"Animation metadata: {_animMeta.Count} entries");
        _sim.SetAnimMeta(_animMeta);

        // Phase 2: Upload decoded pixels to GPU (fast — just SetData, no PNG decode).
        // Stride calibration runs per-atlas in this loop, BEFORE pixels are freed,
        // so it can scan the source rgba without a GPU readback. Cache hit skips
        // the pixel scan; only the first launch (or asset edit) pays the cost.
        int strideCacheHits = 0, strideCacheBuilds = 0;
        for (int i = 0; i < atlasCount; i++)
        {
            if (decodedPixels[i] != null && metaParsed[i])
            {
                var tex = TextureUtil.CreateTextureFromPixels(GraphicsDevice,
                    decodedPixels[i], decodedW[i], decodedH[i]);
                _atlases[i].SetTextureAndFinalize(tex, decodedW[i], decodedH[i]);

                // Calibrate stride per unit. Y-coords in the spritemeta have been
                // flipped by SetTextureAndFinalize (top-left origin), matching the
                // pixel buffer layout we're handing to StrideCalibration.
                string atlasName = AtlasDefs.Names[i];
                string pngPath = GamePaths.Resolve($"assets/Sprites/{atlasName}.png");
                string smPath  = GamePaths.Resolve($"assets/Sprites/{atlasName}.spritemeta");
                string amPath  = GamePaths.Resolve($"assets/Sprites/{atlasName}.animationmeta");
                bool cacheHit = Render.StrideCalibration.CalibrateAtlas(_atlases[i], atlasName,
                    pngPath, smPath, amPath, decodedPixels[i], decodedW[i], decodedH[i], _animMeta);
                if (cacheHit) strideCacheHits++; else strideCacheBuilds++;

                decodedPixels[i] = null!; // free memory after calibration is done with it
            }
            // Attach extension sheets in the order they were decoded (matches the
            // TextureIndex assigned by ParseExtensionMeta).
            foreach (var ext in extDecoded[i])
            {
                if (!ext.decoded || ext.pixels == null) continue;
                var extTex = TextureUtil.CreateTextureFromPixels(GraphicsDevice,
                    ext.pixels, ext.w, ext.h);
                _atlases[i].AttachExtensionTexture(extTex, ext.w, ext.h);
            }
        }
        LogTiming($"Atlases GPU upload + stride calibration: {atlasCount} ({string.Join(", ", AtlasDefs.Names)}) — strideCacheHits={strideCacheHits} builds={strideCacheBuilds}");

        // Wire each UnitDef's runtime SpriteData reference now that both registries
        // (loaded in _gameData.Load above) and atlases (just uploaded) exist. Lets
        // LocomotionProfile.FromUnit reach stride calibration without separately
        // plumbing atlas access through AI / render call sites.
        int spriteWireCount = 0;
        foreach (var def in _gameData.Units.All())
        {
            if (def.Sprite == null || string.IsNullOrEmpty(def.Sprite.AtlasName)) continue;
            int aIdx = AtlasDefs.ResolveAtlasName(def.Sprite.AtlasName);
            if (aIdx < 0 || aIdx >= _atlases.Length) continue;
            def.SpriteData = _atlases[aIdx].GetUnit(def.Sprite.SpriteName);
            if (def.SpriteData != null) spriteWireCount++;
        }
        LogTiming($"UnitDef→SpriteData wired for {spriteWireCount}/{_gameData.Units.Count} units");

        // Push corpse.json pivot overrides into the BodyBag/Icon atlas frames now
        // that the Corpses atlas exists. Spritemeta provides the defaults; this
        // step lets the editor tune per-angle hand-attach points without re-export.
        {
            int corpsesIdx = AtlasDefs.ResolveAtlasName("Corpses");
            if (corpsesIdx >= 0 && corpsesIdx < _atlases.Length)
                _gameData.Corpse.ApplyToAtlas(_atlases[corpsesIdx]);
        }

        base.Initialize();
    }

    /// <summary>
    /// Install the Game1→Simulation back-references (delegates + the worker back-ref) onto
    /// the CURRENT session's Sim. Must run after every session recreation, because <c>_sim</c>
    /// forwards to <c>_session.Sim</c> and StartGame does <c>_session = new GameSession()</c> —
    /// a fresh Simulation has these fields null, so reanimation / forager sounds / worker
    /// dispatch would silently break on map load otherwise. Unlike a cached Simulation
    /// reference (see LightningRenderer/ForagableSystem/WorkerSystem), these are one-way
    /// writes onto the live Sim, so they must be re-applied rather than read live.
    /// </summary>
    private void WireSimCallbacks()
    {
        // Route sim-layer reanimations (potion / on-death / table-craft) through the composite
        // reanimation pipeline, the same one spells use (headless sims fall back to a direct spawn).
        _sim.ReanimHandler = OnSimReanimReady;
        // Foraging boars reuse the same pickup pop when they swallow a mushroom.
        _sim.OnForagerAte = OnForagerAte;
        // Worker job system back-ref so the sim can reach the job brain.
        _sim.Workers = _workerSystem;
    }

    private void StartGame(string mapName = "default")
    {
        _gameWorldLoaded = false;
        _gameOver = false;
        // Fresh world = fresh clock state: clear pause holders + restore 1x speed so a
        // pause or 8x from the previous session isn't carried in. World age resets with
        // the fresh Simulation below; VisualTime deliberately keeps running (phase).
        _clock.OnWorldStart();
        _damageNumbers.Clear();
        _unitAnims.Clear();
        _corpseAnims.Clear();
        _effectManager.Clear();
        // In-flight reanimation effects + buff particle emitters would otherwise
        // carry into the new session (they were the only per-game visual systems
        // not cleared here).
        _reanimFx.Clear();
        _buffVisuals.Clear();
        _tethers.Clear(); _tetherAnchor = null; _tetherDustAccum.Clear();
        _pendingProjectiles.Clear();
        // Kill mid-flight pickup arcs — they hold textures from the session being
        // disposed below and would deposit the old map's item into the new game.
        _foragables.Clear();
        // Reset per-game skill book progress (learned set + event tally) so
        // returning to the main menu and starting a new game wipes prior unlocks.
        _skillBookState.InitFromDefs();
        _skillLearnToasts.Clear();

        // Recreate the per-game world state: Dispose() frees the old map's GPU resources
        // (ground/env textures) and the reassignment drops all references to the previous
        // map's managed state (env objects/defs, wall defs) so it can't leak or bleed into
        // the new map. Replaces the old per-system ClearObjects/ClearDefs/ClearTypes dance —
        // wall defs in particular were never cleared, so every reload used to stack another
        // full copy (unbounded growth that OOM'd on maps with a large walls array).
        _session.Dispose();
        _session = new GameSession();
        // The new session has a fresh Simulation — re-install the Game1→Sim back-references
        // (reanim / forager / worker) that would otherwise be null on it. Live-reading
        // consumers (lightning/foragable/worker systems) follow _sim automatically.
        WireSimCallbacks();
        _envSystem.OnCollisionsDirty = null;
        _envSystem.OnCollisionRegionDirty = null;
        // Reset the worker job system: reload jobs.json, wipe stockpiles + assignments
        // so a fresh game doesn't inherit the previous session's piles or priorities.
        _workerSystem.Reset();
        // Wipe per-cell grass corruption fades — the renderer instance persists
        // across new-games, so without this stale fade values from the previous
        // session would render the grass already-corrupted on map load.
        _grassRenderer.ClearAllFades();
        _roadSystem.Init();
        _dayNightSystem.Init(_gameData.Settings.DayNight);

        // Load flipbooks (shared with the spell editor's live-reload path).
        ReloadFlipbooksFromRegistry();
        LogTiming($"Flipbooks loaded: {_flipbooks.Count}");
        // Now that flipbooks are loaded, hand the dictionary to systems
        // that need to look up the trail / splash / rain-splash animations.
        _wakeSystem.Init(_flipbooks);

        // Animation metadata is loaded once in Initialize() and reused across
        // main-game and scenario flows.

        // Load animations.json timing overrides (stub)
        if (File.Exists(GamePaths.Resolve(GamePaths.AnimationsJson)))
            DebugLog.Log("startup", "animations.json found");

        // weapon_points.json is now loaded by GameData.Load() into each UnitDef.WeaponPoints

        // Load map. mapName selects which map under assets/maps/ to load
        // ("default", "testmap", ...); the trigger/road companions follow the
        // same "<mapName>_triggers.json" / "<mapName>_roads.json" convention.
        // Special name "empty_test" synthesizes a tiny grass-only map in code
        // (no JSON file) so technical-behavior tests don't fight the regular
        // map's content. See the "start_game" dev command (its no-arg default).
        var placedUnits = new List<Data.PlacedUnit>();
        // Zones are reloaded per map below; clear here so maps without a zones file
        // (empty_test, missing map) don't inherit the previous map's zones.
        _zoneSystem.Clear();
        // Grass is per-map like zones: clear here so a map without a grassMap
        // section doesn't inherit the previous map's grass — Save Map would then
        // bake the stale grid into that map's JSON (this is how testmap.json once
        // gained default's full 5120x5120 grass layer, +34MB).
        _grassW = 0; _grassH = 0;
        _grassMap = Array.Empty<byte>();
        _grassTypeIds = Array.Empty<string>();
        _grassTypeNames = Array.Empty<string>();
        _grassTypeSpritePaths = Array.Empty<string[]>();
        _grassTypeScales = Array.Empty<float>();
        _grassTypeDensities = Array.Empty<float>();
        _grassDefaultTints = Array.Empty<Color>();
        _grassCorruptedTints = Array.Empty<Color>();
        string mapPath = GamePaths.Resolve($"{GamePaths.MapsDir}/{mapName}.json");
        if (mapName == "empty_test")
        {
            DebugLog.Log("startup", "Empty test map: synthesizing grass-only grid + debug necromancer");
            _groundSystem.Init(WorldSize, WorldSize);
            // One grass ground type at index 0 — the vertex map defaults to 0,
            // so the whole map renders as grass with no further work.
            _groundSystem.AddGroundType(new World.GroundTypeDef
            {
                Id = "grass",
                Name = "Grass",
                TexturePath = "assets/Environment/Ground/GroundGrass1.png",
            });
            // Pathfinder needs a non-zero map; the fog grid sizes off the
            // ground dims (mirrors the regular load path).
            _deathFog.Init(_groundSystem.WorldW, _groundSystem.WorldH, cellSize: 4);
            placedUnits.Add(new Data.PlacedUnit
            {
                UnitDefId = "necromancer_debug",
                X = WorldSize * 0.5f,
                Y = WorldSize * 0.5f,
            });
        }
        else if (File.Exists(mapPath))
        {
            DebugLog.Log("startup", $"Loading map '{mapName}' from file...");
            // Load env defs from canonical location (before map, so placed objects can resolve IDs)
            MapData.LoadEnvDefs(GamePaths.Resolve(GamePaths.EnvDefsJson), _envSystem);
            // Parse the 55MB map JSON exactly once — Load returns the grass info from
            // the same JsonDocument it already parsed, avoiding a redundant disk+parse pass.
            MapData.Load(mapPath, _groundSystem, _envSystem, _wallSystem, placedUnits,
                out var grassInfo);
            MapSidecars.LoadTriggers(GamePaths.Resolve($"{GamePaths.MapsDir}/{mapName}_triggers.json"), _triggerSystem);
            MapSidecars.LoadRoads(GamePaths.Resolve($"{GamePaths.MapsDir}/{mapName}_roads.json"), _roadSystem);
            MapSidecars.LoadZones(GamePaths.Resolve($"{GamePaths.MapsDir}/{mapName}_zones.json"), _zoneSystem);
            // Village structures are placed here (before the collision bake below) so their
            // buildings are stamped into the pathfinding grid alongside the map's own objects.
            LoadVillageStructures(mapName);
            LogTiming($"Map loaded: ground={_groundSystem.WorldW}x{_groundSystem.WorldH}, objects={_envSystem.ObjectCount}, defs={_envSystem.DefCount}");

            // Death fog: coarse grid sized to the map. Auto-tag tree assets as
            // sinks so we don't need to edit ~40 JSON entries by hand.
            _deathFog.Init(_groundSystem.WorldW, _groundSystem.WorldH, cellSize: 4);
            GameSystems.DeathFogSystem.AutoTagTreesAsSinks(_envSystem, absorbRate: 6f);

            // Unpack grass data returned from the same map JSON parse.
            if (grassInfo.HasValue)
            {
                var gi = grassInfo.Value;
                _grassW = gi.Width;
                _grassH = gi.Height;
                _grassMap = gi.Cells;
                _grassTypeIds = new string[gi.Types.Length];
                _grassTypeNames = new string[gi.Types.Length];
                _grassTypeSpritePaths = new string[gi.Types.Length][];
                _grassTypeScales = new float[gi.Types.Length];
                _grassTypeDensities = new float[gi.Types.Length];
                _grassDefaultTints = new Color[gi.Types.Length];
                _grassCorruptedTints = new Color[gi.Types.Length];
                for (int i = 0; i < gi.Types.Length; i++)
                {
                    _grassTypeIds[i] = gi.Types[i].Id ?? $"grass_{i}";
                    _grassTypeNames[i] = gi.Types[i].Name ?? $"Grass {i}";
                    _grassTypeSpritePaths[i] = gi.Types[i].SpritePaths ?? Array.Empty<string>();
                    _grassTypeScales[i] = gi.Types[i].Scale > 0f ? gi.Types[i].Scale : 1f;
                    _grassTypeDensities[i] = gi.Types[i].Density > 0f ? gi.Types[i].Density : 1f;
                    _grassDefaultTints[i]   = new Color(gi.Types[i].DefR, gi.Types[i].DefG, gi.Types[i].DefB, gi.Types[i].DefA);
                    _grassCorruptedTints[i] = new Color(gi.Types[i].CorR, gi.Types[i].CorG, gi.Types[i].CorB, gi.Types[i].CorA);
                }
                DebugLog.Log("startup", $"Grass map: {_grassW}x{_grassH}, {gi.Types.Length} types");
            }
        }
        else
        {
            _groundSystem.Init(WorldSize, WorldSize);
            DebugLog.Log("startup", "No map file found, using empty grid");
        }

        int worldW = _groundSystem.WorldW > 0 ? _groundSystem.WorldW : WorldSize;
        int worldH = _groundSystem.WorldH > 0 ? _groundSystem.WorldH : WorldSize;
        _wallSystem.Init(worldW, worldH, worldW);

        // Load textures
        _groundSystem.LoadTextures(GraphicsDevice);
        // Dispose the previous map's vertex texture before replacing it — StartGame runs on
        // every "load map", so without this each reload orphans a full map-sized texture on
        // the GPU (every other CreateVertexMapTexture call site disposes first).
        _groundVertexMapTex?.Dispose();
        _groundVertexMapTex = _groundSystem.CreateVertexMapTexture(GraphicsDevice);
        // Now that the ground types are populated, bake a wake-particle
        // gradient variant per unique water tint so swamp shallow water
        // produces a swamp-green wake instead of the default shoreline cyan.
        _wakeSystem.InitWaterVariants(_groundSystem);
        _envSystem.LoadTextures(GraphicsDevice);
        // NOTE: corpse-carry centroids are computed lazily on first carry (see
        // GetFrameCentroid). Pre-baking them all here stalled Start-Game ~13s —
        // each GetData read-back on the huge unit atlases is ~85ms and there are
        // ~200 death frames. One brief hitch per carried corpse type is the
        // cheaper trade until centroids can be baked offline into the spritemeta.
        LogTiming($"Ground textures: {_groundSystem.TypeCount}, Env textures: {_envSystem.DefCount}, VertexMap: {(_groundVertexMapTex != null ? "OK" : "NONE")}");

        // Init simulation with map size
        _sim.Init(worldW, worldH, _gameData);
        _sim.SetEnvironmentSystem(_envSystem);
        _sim.SetWallSystem(_wallSystem);
        _sim.SetTriggerSystem(_triggerSystem);
        _sim.SetVillageSystem(_villageSystem);
        _sim.SetSkillBook(_skillBookState);

        // Wire collision change callbacks so pathfinding stays in sync.
        // Region path: single-object changes (forage pickup/respawn, tree
        // destroyed, building placed) do a targeted ~ms rebake + invalidation.
        // Full path: batch signals with no region info (editor exit). Both are
        // deferred + coalesced to at most one rebuild per tick.
        _envSystem.OnCollisionsDirty = () => _sim.RequestPathfinderRebuild();
        _envSystem.OnCollisionRegionDirty = (x0, y0, x1, y1) => _sim.RequestPathfinderRegionRebuild(x0, y0, x1, y1);

        // Stamp pathfinding terrain from ground vertex types (e.g. water-textured
        // tiles become ShallowWater/DeepWater for the cost field). Must happen
        // BEFORE BakeWalls so walls win on shared tiles, and BEFORE
        // RebuildPathfinder so the cost-field rebuild picks up the new terrain.
        _groundSystem.StampTerrainOnto(_sim.Grid);

        // Bake walls + env (and build the env spatial index used by ORCA).
        // RebuildPathfinder now bakes walls itself (before the cost field) and
        // populates the env index, so a separate BakeWalls call here would just
        // re-walk the whole 4096² grid redundantly.
        _sim.RebuildPathfinder();
        LogTiming($"Baked collisions: {_envSystem.ObjectCount} objects, grid {worldW}x{worldH}");

        // Fog of war
        _fogOfWar.Init(worldW, worldH, GraphicsDevice, Content);

        // Spawn placed units from map data
        float center = worldW * 0.5f;
        foreach (var pu in placedUnits)
        {
            SpawnUnit(pu.UnitDefId, new Vec2(pu.X, pu.Y));
            int lastIdx = _sim.Units.Count - 1;
            if (!string.IsNullOrEmpty(pu.Faction))
            {
                _sim.UnitsMut[lastIdx].Faction = pu.Faction switch
                {
                    "Human" => Faction.Human,
                    "Animal" => Faction.Animal,
                    _ => Faction.Undead
                };
            }
            if (!string.IsNullOrEmpty(pu.PatrolRouteId))
            {
                // PatrolSoldier archetype walks the route's waypoints; the old
                // AIBehavior.Patrol only ever moved to MoveTarget and ignored
                // the route (and silently lost to any def archetype anyway).
                // Same wiring as the inter-village patrols in Game1.Villages.cs.
                for (int pri = 0; pri < _triggerSystem.PatrolRoutes.Count; pri++)
                {
                    if (_triggerSystem.PatrolRoutes[pri].Id != pu.PatrolRouteId) continue;
                    var route = _triggerSystem.PatrolRoutes[pri];
                    _sim.UnitsMut[lastIdx].Archetype = AI.ArchetypeRegistry.PatrolSoldier;
                    _sim.UnitsMut[lastIdx].PatrolRouteIdx = pri;
                    _sim.UnitsMut[lastIdx].PatrolWaypointIdx = 0;
                    if (route.Waypoints.Count > 0)
                        _sim.UnitsMut[lastIdx].MoveTarget = route.Waypoints[0];
                    _sim.UnitsMut[lastIdx].Routine = 0;
                    _sim.UnitsMut[lastIdx].Subroutine = 0;
                    break;
                }
            }
            // Editor-placed corpse: spawn the unit (so it resolves its def/sprite/scale),
            // then immediately convert it to a corpse and drop it from the unit array.
            if (pu.IsCorpse)
                _sim.SpawnCorpseFromUnit(lastIdx);
        }
        if (placedUnits.Count > 0)
            LogTiming($"Spawned {placedUnits.Count} placed units");

        // Populate villages: spawn people (peasants, hunters, militia, watchdogs), buried
        // corpses, and the inter-village militia patrols. Structures were already placed
        // pre-bake in LoadVillageStructures.
        LoadVillagePopulation(mapName);

        // Apply authored zones (village creation / animal squad grouping) now that every
        // unit — map-placed and legacy villagers alike — exists with its final position.
        ApplyZones();

        // One-shot: pull any editor-placed corpses sitting on a Corpse Pile into its
        // stock so they can be gathered back out. Done once here on map load (not per
        // frame) — AbsorbCorpsesOnPiles is an O(piles × corpses) scan, too costly to tick.
        _workerSystem.AbsorbCorpsesOnPiles();

        // Always ensure the player unit exists. The player starts as the
        // Wretched form — every other "necromancer-type" UnitDef is reached
        // via the Metamorphosis skill tree (Become Pale Acolyte, Become Wight,
        // etc.). Across the codebase we still refer to the player unit as
        // "the necromancer" regardless of which PlayerForm def it currently is.
        if (_sim.NecromancerIndex < 0)
        {
            SpawnUnit("wretched", new Vec2(center, center));
            DebugLog.Log("startup", "No necromancer in placed units, spawned Wretched default at map center");
        }

        _camera.Position = _sim.NecromancerIndex >= 0
            ? _sim.Units[_sim.NecromancerIndex].Position : new Vec2(center, center);
        _sim.Horde.CircleCenter = _sim.NecromancerIndex >= 0
           ? _sim.Units[_sim.NecromancerIndex].Position : new Vec2(center, center);
        // Empty test map is for Claude's behavior tests — closer zoom so floating
        // text (cast-failure alerts, damage numbers, ActionLabels) is legible in
        // the downsampled screenshots that get sent back to the model. Regular
        // maps keep the 24f default tuned for human play.
        _camera.Zoom = mapName == "empty_test" ? 48f : 24f;

        // Pass placed units to map editor so markers are visible
        _mapEditor.SetPlacedUnits(placedUnits);

        // Load the spell bar from the data file
        _spellBarSeededForTest = false; // regular loads save normally again
        _spellBarState.Slots = new SpellBarSlot[SpellBarBindings.SlotCount];
        for (int si = 0; si < _spellBarState.Slots.Length; si++)
            _spellBarState.Slots[si] = new SpellBarSlot { SpellID = "" };
        try
        {
            // Per-machine spell-bar loadout: gitignored 'user settings/', seeded from
            // the shipped default data/spellbar.json on first run.
            string sbJson = File.ReadAllText(GamePaths.SeededUserFile(
                GamePaths.UserSpellBarJson, GamePaths.Resolve(GamePaths.SpellBarJson)));
            using var sbDoc = System.Text.Json.JsonDocument.Parse(sbJson);
            if (sbDoc.RootElement.TryGetProperty("secondary", out _))
                MigrateOldSpellBarJson(sbDoc.RootElement, _spellBarState);
            else
                LoadSpellBarSlots(sbDoc.RootElement, "slots", _spellBarState);
        }
        catch (Exception ex)
        {
            DebugLog.Log("startup", $"Failed to load spellbar.json: {ex.Message}");
            _spellBarState.Slots[0] = new SpellBarSlot { SpellID = "summon_wolf" };
            _spellBarState.Slots[1] = new SpellBarSlot { SpellID = "summon_deer" };
            _spellBarState.Slots[2] = new SpellBarSlot { SpellID = "raise_zombie" };
        }

        // Test maps: pre-load no-path test spells so the necromancer can
        // immediately cast and exercise failure modes (OutOfRange /
        // NotEnoughMana / OnCooldown) without granting paths or touching
        // the player's saved spellbar.json. Regular maps are deliberately
        // left alone — they ship with an empty default spellbar.
        if (mapName == "testmap" || mapName == "empty_test")
        {
            // OutOfRange/NotEnoughMana/OnCooldown test projectile on Q (slot 0).
            _spellBarState.Slots[0] = new SpellBarSlot { SpellID = "test_projectile" };
            // Zero-damage directional-impact projectile (physics shove) on slot 1.
            _spellBarState.Slots[1] = new SpellBarSlot { SpellID = "test_impact" };
            // The canonical reanimation spell on slot 2 (the "1" key) so it can be
            // cast on a corpse. The debug necromancer has every path/mana.
            _spellBarState.Slots[2] = new SpellBarSlot { SpellID = "reanimate_corpse" };
            _spellBarSeededForTest = true;
        }

        // Empty test map: top up the necromancer's mana pool so high-cost
        // spells are castable out of the gate. NecroState.MaxMana is separate
        // from the UnitDef's maxMana field (which only affects non-necro mana
        // users like priests), so the debug def's 999 doesn't reach here on
        // its own.
        if (mapName == "empty_test")
        {
            _sim.NecroState.MaxMana = 999f;
            _sim.NecroState.Mana = 999f;
            _sim.NecroState.ManaRegen = 20f;
        }

        // Init map editor with live systems
        _mapEditor.Init(
            this,
            onVertexMapChanged: () =>
            {
                // Incremental: patch only the brush-sized dirty rect into the
                // existing texture (a few hundred texels) instead of allocating
                // and uploading a fresh 4097x4097 (~67 MB) texture every painted
                // frame. Fall back to a full rebuild only if the texture is
                // missing or its size no longer matches the vertex map.
                if (_groundVertexMapTex == null
                    || !_groundSystem.UploadDirtyRect(_groundVertexMapTex))
                {
                    _groundVertexMapTex?.Dispose();
                    _groundVertexMapTex = _groundSystem.CreateVertexMapTexture(GraphicsDevice);
                }
            },
            onGrassMapChanged: SyncGrassMapReference,
            onGrassTypesChanged: SyncGrassFromEditor);
        _mapEditor.SetItemRegistry(_gameData.Items);
        _mapEditor.SetSpellRegistry(_gameData.Spells);
        _mapEditor.SetGameData(_gameData);
        // Restore the last-open tab once, now that the editor has its settings —
        // not in the open handlers (F11 / HUD button / pause menu each open it and
        // we'd otherwise have to remember all of them).
        _mapEditor.RestoreTabFromSettings();
        // Default the editor's Save target to whichever map was just loaded, so
        // editing after "Play Test Map" saves to testmap.json — not default.json.
        _mapEditor.SetMapFilename(mapName);
        {
            int corpsesIdx = AtlasDefs.ResolveAtlasName("Corpses");
            var corpsesAtlas = (corpsesIdx >= 0 && corpsesIdx < _atlases.Length) ? _atlases[corpsesIdx] : null;
            _mapEditor.SetCorpseSettings(_gameData.Corpse, corpsesAtlas);
        }

        // Feed grass data to map editor — unconditionally, so loading a map
        // without grass also CLEARS the editor's grass state from the previous
        // map (the editor outlives map loads; stale grass would get re-saved).
        {
            var grassTypeInfos = new MapData.GrassTypeInfo[_grassTypeIds.Length];
            for (int gi = 0; gi < grassTypeInfos.Length; gi++)
            {
                var dt = gi < _grassDefaultTints.Length ? _grassDefaultTints[gi] : Color.White;
                var ct = gi < _grassCorruptedTints.Length ? _grassCorruptedTints[gi] : new Color((byte)80, (byte)60, (byte)70, (byte)255);
                grassTypeInfos[gi] = new MapData.GrassTypeInfo
                {
                    Id = _grassTypeIds[gi] ?? $"grass_{gi}",
                    Name = gi < _grassTypeNames.Length && _grassTypeNames[gi] != null ? _grassTypeNames[gi] : $"Grass {gi}",
                    SpritePaths = gi < _grassTypeSpritePaths.Length ? _grassTypeSpritePaths[gi] : Array.Empty<string>(),
                    Scale = gi < _grassTypeScales.Length ? _grassTypeScales[gi] : 1f,
                    Density = gi < _grassTypeDensities.Length ? _grassTypeDensities[gi] : 1f,
                    DefR = dt.R, DefG = dt.G, DefB = dt.B, DefA = dt.A,
                    CorR = ct.R, CorG = ct.G, CorB = ct.B, CorA = ct.A,
                };
            }
            _mapEditor.SetGrassData(_grassMap, _grassW, _grassH, grassTypeInfos, _gameData.Settings.Grass.CellSize);
            PushGrassSpritesToRenderer();
        }

        Window.Title = "Necroking";
        // Warm the widget-UI family here, inside the load phase: LoadDefinitions
        // bakes every harmonized texture (~seconds of CPU work) — paying it
        // lazily froze the game on the FIRST UI keypress (I/C/U/O) instead.
        EnsureInventoryUIsInitialized();
        // Optional reanimation pose-morph prewarm — only when BOTH the morph effect and
        // the prewarm are enabled (both OFF by default; the morph is an opt-in prototype
        // and warming it is hundreds of heavy builds that chew CPU after load). When on,
        // ENQUEUE the descriptors now and drain a few builds per frame over the first
        // seconds of play (QueueReanimMorphPrewarm / TickReanimMorphPrewarm) so the first
        // raise of a type never stalls; otherwise morphs build lazily on first use.
        if (_gameData.Settings.Performance.ReanimMorph && _gameData.Settings.Performance.PrewarmReanimMorphs)
            _gameRenderer.QueueReanimMorphPrewarm();
        LogTiming("Game world loaded");
        DebugLog.Log("startup", $"=== Total startup: {_startupTimer?.ElapsedMilliseconds ?? 0}ms ===");
        _gameWorldLoaded = true;
        _menuState = MenuState.None;

        // Starting inventory
        foreach (var item in _gameData.Settings.StartingInventory)
            _inventory.AddItem(item.ItemId, item.Quantity);

        _skillBookOverlay.Bind(_skillBookState, _inventory, _gameData,
            _spellBarState, _sim);

    }

    /// <summary>Auto-learn a skill if not already learned, and surface a corner
    /// toast on success. Used by gameplay triggers (pickups, milestones, etc.).</summary>
    private void TryAutoLearn(string skillId, string header)
    {
        bool learned = _skillBookState.LearnFree(skillId, new Game.SkillEffects.SkillEffectContext
        {
            Inventory = _inventory,
            GameData = _gameData,
            Bar = _spellBarState,
            BookState = _skillBookState,
            Sim = _sim,
        });
        if (!learned) return;
        var def = _skillBookState.FindSkill(skillId);
        if (def == null) return;
        _skillLearnToasts.Add(new SkillLearnToast
        {
            Header = header,
            SkillName = def.Name,
            SkillId = skillId,
            Timer = 0f,
            Duration = 5f,
        });
    }

    /// <summary>Use a consumable inventory item by id. Currently supports skill-point
    /// potions (grant SkillPointAmount to the SkillPointPool). Returns true if the
    /// item was consumed, in which case the caller decrements the stack by one.</summary>
    private bool TryConsumeInventoryItem(string itemId)
    {
        var def = _gameData.Items.Get(itemId);
        if (def == null) return false;

        if (def.SkillPointAmount > 0 && !string.IsNullOrEmpty(def.SkillPointPool))
        {
            _skillBookState.AddSkillPoints(def.SkillPointPool, def.SkillPointAmount);
            DebugLog.Log("items",
                $"Consumed '{itemId}': +{def.SkillPointAmount} '{def.SkillPointPool}' skill points " +
                $"(now {_skillBookState.GetSkillPoints(def.SkillPointPool)})");
            // Reuse the skill-learn toast for feedback; empty SkillId just opens the
            // skill book (where the new points are visible) if clicked.
            _skillLearnToasts.Add(new SkillLearnToast
            {
                Header = "Skill Points",
                SkillName = $"+{def.SkillPointAmount} {def.SkillPointPool}",
                SkillId = "",
                Timer = 0f,
                Duration = 5f,
            });
            return true;
        }

        return false;
    }

    /// <summary>
    /// Sync grass data from the map editor back to Game1's rendering arrays.
    /// Called by the editor whenever the grass map or grass type properties change.
    /// </summary>
    /// <summary>
    /// Cheap cell-paint notification: the grass map array is shared with the
    /// renderer and read live each frame, so painting cells needs no work beyond
    /// re-pointing at the editor's array if it was swapped (it isn't during a
    /// paint stroke — only after Add-first-type or Load, which both go through
    /// the full <see cref="SyncGrassFromEditor"/> path). O(1), safe to call per
    /// painted frame.
    /// </summary>
    private void SyncGrassMapReference()
    {
        var editorMap = _mapEditor.GetGrassMap();
        if (editorMap != _grassMap && editorMap.Length > 0)
        {
            _grassMap = editorMap;
            _grassW = _mapEditor.GrassW;
            _grassH = _mapEditor.GrassH;
        }
    }

    private void SyncGrassFromEditor()
    {
        // The editor may have a new/different grass map reference (e.g. after editor Load)
        SyncGrassMapReference();

        // Sync grass type properties from editor definitions.
        var types = _mapEditor.GrassTypes;

        _grassTypeSpritePaths = new string[types.Count][];
        _grassTypeScales = new float[types.Count];
        _grassTypeDensities = new float[types.Count];
        _grassDefaultTints = new Color[types.Count];
        _grassCorruptedTints = new Color[types.Count];
        if (_grassTypeIds.Length != types.Count) _grassTypeIds = new string[types.Count];
        if (_grassTypeNames.Length != types.Count) _grassTypeNames = new string[types.Count];

        for (int i = 0; i < types.Count; i++)
        {
            _grassTypeIds[i] = types[i].Id;
            _grassTypeNames[i] = types[i].Name;
            _grassTypeSpritePaths[i] = types[i].SpritePaths.ToArray();
            _grassTypeScales[i] = types[i].Scale > 0f ? types[i].Scale : 1f;
            _grassTypeDensities[i] = MathF.Max(0f, types[i].Density);
            _grassDefaultTints[i] = types[i].DefaultTint;
            _grassCorruptedTints[i] = types[i].CorruptedTint;
        }

        PushGrassSpritesToRenderer();
    }

    /// <summary>
    /// Push the current _grassTypeSpritePaths + _grassTypeScales + _grassTypeDensities
    /// to GrassTuftRenderer so its texture cache and per-type tables reflect the live
    /// grass types. Called from SyncGrassFromEditor and StartScenario after grass data
    /// is set up.
    /// </summary>
    /// <summary>Push <see cref="Data.Registries.CorruptionSettings"/> values into
    /// the live systems that own them. Cheap (a handful of float assignments) so
    /// it's safe to call every frame — that way live edits via the Settings UI
    /// take effect immediately on the next gameplay tick. The systems hold their
    /// own copies in their hot paths; this method is the one place those copies
    /// get refreshed from settings.json.</summary>
    private void SyncCorruptionSettings()
    {
        if (_gameData == null) return;
        var c = _gameData.Settings.Corruption;

        _deathFog.CorruptionHealRate = c.TreeHealRate;
        _deathFog.CorruptionThreshold = c.TreeThreshold;
        _deathFog.CorruptedAbsorbRate = c.TreeCorruptedAbsorbRate;
        _deathFog.CorruptionTransitionDuration = c.TreeFadeDuration;
        _deathFog.GroundCorruptionMaxRate = c.GroundMaxRatePerSec;
        _deathFog.DiffusionRate = c.DiffusionRate;
        _deathFog.SourceRateScale = c.SourceRateScale;
        _deathFog.SinkRateScale = c.SinkRateScale;

        _groundSystem.CorruptionFadeDuration = c.GroundFadeDuration;
        _grassRenderer.CorruptionFadeDuration = c.GrassFadeDuration;

        _deathFogRenderer.VisibilityThreshold = c.FogVisibilityThreshold;
        _deathFogRenderer.SaturationDensity = c.FogSaturationDensity;
        _deathFogRenderer.MaxAlpha = c.FogMaxAlpha;
        _deathFogRenderer.FlipbookCycleSeconds = c.FogFlipbookCycleSeconds;
        _deathFogRenderer.PuffWorldSizeMultiplier = c.FogPuffWorldSizeMultiplier;
        _deathFogRenderer.PositionJitter = c.FogPositionJitter;
        _deathFogRenderer.FogTint = new Microsoft.Xna.Framework.Color(
            (byte)c.FogTint.R, (byte)c.FogTint.G, (byte)c.FogTint.B, (byte)c.FogTint.A);
    }

    /// <summary>Translate a newly-corrupted ground vertex into the grass cells
    /// it sits under and start their fade. A vertex (vx, vy) is the corner of up
    /// to four world tiles spanning [vx-1, vx+1) × [vy-1, vy+1); we mark every
    /// grass cell touching that 2×2 world region. StartCellFade is idempotent,
    /// so multiple adjacent vertex flips don't reset progress already in flight.</summary>
    private void OnGroundVertexCorruptedForGrass(int vx, int vy)
    {
        if (_grassMap.Length == 0 || _grassW == 0) return;
        float cellSize = _gameData.Settings.Grass.CellSize;
        if (cellSize <= 0f) cellSize = 1f;

        float wx0 = vx - 1f, wx1 = vx + 1f;
        float wy0 = vy - 1f, wy1 = vy + 1f;
        int cx0 = Math.Max(0, (int)MathF.Floor(wx0 / cellSize));
        int cy0 = Math.Max(0, (int)MathF.Floor(wy0 / cellSize));
        int cx1 = Math.Min(_grassW - 1, (int)MathF.Floor((wx1 - 0.0001f) / cellSize));
        int cy1 = Math.Min(_grassH - 1, (int)MathF.Floor((wy1 - 0.0001f) / cellSize));
        for (int cy = cy0; cy <= cy1; cy++)
        {
            for (int cx = cx0; cx <= cx1; cx++)
            {
                int idx = cy * _grassW + cx;
                if ((uint)idx >= (uint)_grassMap.Length) continue;
                if (_grassMap[idx] == 0) continue; // empty cell, no tuft to fade
                _grassRenderer.StartCellFade(idx);
            }
        }
    }

    private void PushGrassSpritesToRenderer()
    {
        var list = new List<Render.GrassTypeRender>(_grassTypeSpritePaths.Length);
        for (int i = 0; i < _grassTypeSpritePaths.Length; i++)
        {
            var paths = _grassTypeSpritePaths[i] ?? Array.Empty<string>();
            float scale = i < _grassTypeScales.Length && _grassTypeScales[i] > 0f ? _grassTypeScales[i] : 1f;
            float density = i < _grassTypeDensities.Length ? _grassTypeDensities[i] : 1f;
            Color def = i < _grassDefaultTints.Length ? _grassDefaultTints[i] : Color.White;
            Color cor = i < _grassCorruptedTints.Length ? _grassCorruptedTints[i] : new Color((byte)80, (byte)60, (byte)70, (byte)255);
            list.Add(new Render.GrassTypeRender(paths, scale, density, def, cor));
        }
        _grassRenderer.SetGrassTypes(list);
    }

    // --- Dev server command execution (main thread) ---------------------------
    // Each case rides Game1's existing APIs / the same Simulation primitives that
    // scenarios use, so there's a single source of truth for world manipulation.

    private void StartScenario(string scenarioName)
    {
        var scenario = ScenarioRegistry.Create(scenarioName);
        if (scenario == null)
        {
            DebugLog.Log("scenario", $"Failed to create scenario: {scenarioName}");
            return;
        }

        _activeScenario = scenario;

        // UI scenarios need a larger resolution in headless mode
        if (LaunchArgs.Headless && scenario.WantsUI && _graphics.PreferredBackBufferWidth < 800)
        {
            _graphics.PreferredBackBufferWidth = 800;
            _graphics.PreferredBackBufferHeight = 600;
            _graphics.ApplyChanges();
        }

        _gameWorldLoaded = false;
        _gameOver = false;
        _clock.OnWorldStart(); // same reset as StartGame: no inherited pause/speed
        _damageNumbers.Clear();
        _unitAnims.Clear();
        _corpseAnims.Clear();
        _effectManager.Clear();
        _reanimFx.Clear();
        _buffVisuals.Clear();
        _tethers.Clear(); _tetherAnchor = null; _tetherDustAccum.Clear();

        // Load flipbooks (needed for cloud effects, hit effects, etc.)
        if (_flipbooks.Count == 0)
        {
            foreach (var fbId in _gameData.Flipbooks.GetIDs())
            {
                var fbDef = _gameData.Flipbooks.Get(fbId);
                if (fbDef == null || string.IsNullOrEmpty(fbDef.Path)) continue;
                var resolvedPath = GamePaths.Resolve(fbDef.Path);
                if (!File.Exists(resolvedPath)) continue;
                var fb = new Flipbook();
                if (fb.Load(GraphicsDevice, resolvedPath, fbDef.Cols, fbDef.Rows, fbDef.DefaultFPS))
                    _flipbooks[fbId] = fb;
            }
        }
        // Init systems that consume the flipbook dictionary. LoadGame does
        // this for main-game runs; scenarios use this code path instead.
        _wakeSystem.Init(_flipbooks);

        // Init simulation with a grid sized to the scenario's needs
        int gridSize = scenario.GridSize;
        _groundSystem.ClearTypes();
        _groundSystem.Init(gridSize, gridSize);
        _envSystem.Init(gridSize);
        _envSystem.OnCollisionsDirty = () => _sim.RequestPathfinderRebuild();
        _envSystem.OnCollisionRegionDirty = (x0, y0, x1, y1) => _sim.RequestPathfinderRegionRebuild(x0, y0, x1, y1);
        _wallSystem.Init(gridSize, gridSize, gridSize);
        // Add a default wall def so scenarios can render walls
        _wallSystem.Defs.Add(new World.WallVisualDef { Name = "Stone", Color = new Color(130, 130, 130, 255), MaxHP = 100 });
        _sim.Init(gridSize, gridSize, _gameData);
        _sim.SetEnvironmentSystem(_envSystem);
        _sim.SetWallSystem(_wallSystem);
        _sim.SetTriggerSystem(_triggerSystem);
        _sim.SetVillageSystem(_villageSystem);
        _sim.SetSkillBook(_skillBookState);
        _fogOfWar.Init(gridSize, gridSize, GraphicsDevice, Content);

        // Ensure spell bar state is initialized for HUD safety
        if (_spellBarState.Slots == null)
        {
            _spellBarState.Slots = new SpellBarSlot[SpellBarBindings.SlotCount];
            for (int i = 0; i < _spellBarState.Slots.Length; i++)
                _spellBarState.Slots[i] = new SpellBarSlot { SpellID = "" };
        }
        // UI tests can seed the bar (StartGame's spellbar.json load doesn't run).
        // Scenario runs never save the bar — even unseeded ones exit with
        // scenario state, not the player's loadout.
        _spellBarSeededForTest = true;
        if (scenario.DebugSpells != null)
            for (int i = 0; i < _spellBarState.Slots.Length && i < scenario.DebugSpells.Length; i++)
                _spellBarState.Slots[i] = new SpellBarSlot { SpellID = scenario.DebugSpells[i] };

        // Load ground data for scenarios that want it
        if (scenario.WantsGround)
        {
            _groundSystem.AddGroundType(new World.GroundTypeDef { Id = "grass", Name = "Grass", TexturePath = "assets/Environment/Ground/GroundGrass1.png" });
            _groundSystem.AddGroundType(new World.GroundTypeDef { Id = "dirt", Name = "Dirt", TexturePath = "assets/Environment/Ground/GroundDirt1.png" });
            _groundSystem.AddGroundType(new World.GroundTypeDef { Id = "cobblestone", Name = "Cobblestone", TexturePath = "assets/Environment/Ground/GroundCobblestone1.png" });
            _groundSystem.AddGroundType(new World.GroundTypeDef { Id = "shallow_water", Name = "Shallow Water", TexturePath = "assets/Environment/Ground/ShallowWater.png", MovementTerrain = World.TerrainType.ShallowWater });
            _groundSystem.AddGroundType(new World.GroundTypeDef { Id = "deep_water", Name = "Deep Water", TexturePath = "assets/Environment/Ground/DeepWater.png", MovementTerrain = World.TerrainType.DeepWater });
            _groundSystem.FillAll(0); // Default to grass
            scenario.GroundSystem = _groundSystem;
            // Scenario.OnInit may paint additional types into the vertex map;
            // we (re)build the vertex map texture AFTER OnInit (see below).
            _groundSystem.LoadTextures(GraphicsDevice);
            _groundVertexMapTex = _groundSystem.CreateVertexMapTexture(GraphicsDevice);
            _wakeSystem.InitWaterVariants(_groundSystem);
            DebugLog.Log("scenario", $"Ground setup: types={_groundSystem.TypeCount}, vertexMap={(_groundVertexMapTex != null ? "OK" : "NONE")}, effect={(_groundEffect != null ? "OK" : "NONE")}");
        }

        // Benchmark mode: unlock framerate so GPU cost shows up in the timing.
        if (scenario.BenchmarkMode)
        {
            IsFixedTimeStep = false;
            _graphics.SynchronizeWithVerticalRetrace = false;
            _graphics.ApplyChanges();
        }
        // Mirror current state back so the scenario can verify post-OnInit.
        scenario.VsyncEnabled = _graphics.SynchronizeWithVerticalRetrace;
        scenario.FixedTimeStepEnabled = IsFixedTimeStep;

        // Setup grass data for scenarios that want it
        if (scenario.WantsGrass)
        {
            // Use settings.CellSize so scenarios share the same grass grid layout as
            // the editor / main game. For a default CellSize of 1.0, a 64-unit world
            // gets a 64x64 grass grid (one cell per pathability tile).
            float cs = _gameData.Settings.Grass.CellSize > 0f ? _gameData.Settings.Grass.CellSize : 1.0f;
            int gw = (int)MathF.Ceiling(gridSize / cs);
            int gh = (int)MathF.Ceiling(gridSize / cs);
            _grassMap = new byte[gw * gh];
            _grassW = gw;
            _grassH = gh;
            // Default 3 grass types — no per-type tint distinction in scenarios.
            string[] defaultSprites = { "assets/Environment/Grass/GreenGrass1.png" };
            _grassTypeIds = new[] { "grass_0", "grass_1", "grass_2" };
            _grassTypeNames = new[] { "Grass 0", "Grass 1", "Grass 2" };
            _grassTypeSpritePaths = new[] { defaultSprites, defaultSprites, defaultSprites };
            _grassTypeScales = new[] { 1f, 1f, 1f };
            _grassTypeDensities = new[] { 1f, 1f, 1f };
            var defaultCorrupted = new Color((byte)80, (byte)60, (byte)70, (byte)255);
            _grassDefaultTints = new[] { Color.White, Color.White, Color.White };
            _grassCorruptedTints = new[] { defaultCorrupted, defaultCorrupted, defaultCorrupted };
            Array.Fill(_grassMap, (byte)0);
            scenario.GrassMap = _grassMap;
            scenario.GrassW = gw;
            scenario.GrassH = gh;
            PushGrassSpritesToRenderer();
            DebugLog.Log("scenario", $"Grass setup: {gw}x{gh}, cellSize={cs}, 3 types");
        }

        // Give scenario access to road system and inventory
        scenario.RoadSystem = _roadSystem;
        scenario.Inventory = _inventory;
        scenario.ItemRegistry = _gameData.Items;
        scenario.InventoryUI = _inventoryUI;
        scenario.SkillBookOverlay = _skillBookOverlay;
        scenario.UIShaders = _uiShaders;
        scenario.Atlases = _atlases;
        scenario.Font = _font;
        scenario.SmallFont = _smallFont;
        scenario.PixelTexture = _pixel;
        scenario.GroundFog = _groundFog;
        if (scenario.WantsWidgetRenderer)
        {
            EnsureInventoryUIsInitialized();
            scenario.WidgetRenderer = _widgetRenderer;
            scenario.DrawUnitSprite = (defId, rect) => _gameRenderer.DrawUnitIdleSprite(defId, rect);
        }

        // Init map editor with scenario systems (needed for editor screenshot scenarios)
        _mapEditor.Init(
            this,
            onVertexMapChanged: () =>
            {
                // Incremental: patch only the brush-sized dirty rect into the
                // existing texture (a few hundred texels) instead of allocating
                // and uploading a fresh 4097x4097 (~67 MB) texture every painted
                // frame. Fall back to a full rebuild only if the texture is
                // missing or its size no longer matches the vertex map.
                if (_groundVertexMapTex == null
                    || !_groundSystem.UploadDirtyRect(_groundVertexMapTex))
                {
                    _groundVertexMapTex?.Dispose();
                    _groundVertexMapTex = _groundSystem.CreateVertexMapTexture(GraphicsDevice);
                }
            },
            onGrassMapChanged: SyncGrassMapReference,
            onGrassTypesChanged: SyncGrassFromEditor);
        _mapEditor.SetItemRegistry(_gameData.Items);

        // Initialize the scenario
        scenario.OnInit(_sim);

        // Reload env textures in case the scenario added new defs
        _envSystem.LoadTextures(GraphicsDevice);

        // Rebuild the vertex map texture if the scenario painted ground in OnInit.
        if (scenario.WantsGround)
        {
            _groundSystem.LoadTextures(GraphicsDevice);
            _groundVertexMapTex?.Dispose();
            _groundVertexMapTex = _groundSystem.CreateVertexMapTexture(GraphicsDevice);
            _wakeSystem.InitWaterVariants(_groundSystem);
            _groundSystem.StampTerrainOnto(_sim.Grid);
            _sim.RebuildPathfinder();
        }

        // Wire up UnitEditorAccessor for AnimButtonTestScenario
        if (scenario is Scenario.Scenarios.AnimButtonTestScenario animTest)
            animTest.UnitEditor = new Scenario.Scenarios.UnitEditorAccessor(_unitEditor);

        // Wire up BloomRenderer reference for bloom debug
        if (scenario is Scenario.Scenarios.BloomDebugScenario bloomDbg)
            bloomDbg.BloomRef = _bloom;

        // Wire up graphics context for blend test
        if (scenario is Scenario.Scenarios.BlendTestScenario blendTest)
        {
            blendTest.Device = GraphicsDevice;
            blendTest.Batch = _spriteBatch;
        }

        // Wire up god ray render test
        if (scenario is Scenario.Scenarios.GodRayRenderTestScenario godRayTest)
        {
            godRayTest.Device = GraphicsDevice;
            godRayTest.SpriteBatchRef = _spriteBatch;
            godRayTest.GodRayRef = _lightningRenderer.GetGodRayRenderer();
            godRayTest.HdrEffect = _hdrIntensityEffect;
        }

        // Apply camera override
        if (scenario.HasCameraOverride)
        {
            _camera.Position = new Vec2(scenario.CameraX, scenario.CameraY);
            _camera.Zoom = scenario.CameraZoom;
        }

        // Apply weather override from scenario
        if (scenario.WeatherPreset != null)
        {
            _gameData.Settings.Weather.Enabled = true;
            _gameData.Settings.Weather.ActivePreset = scenario.WeatherPreset;
        }

        Window.Title = $"Necroking - Scenario: {scenarioName}";
        DebugLog.Log("scenario", $"Started scenario: {scenarioName}");
        _gameWorldLoaded = true;
        _menuState = MenuState.None;
    }

    internal int SpawnUnit(string unitDefID, Vec2 pos)
    {
        // Core spawn (AddUnit + def runtime fields + built stats + skill-tree
        // intrinsic buffs) is the single sim-level implementation shared with
        // every other spawn path — see Simulation.SpawnUnitByID. Game1 adds only
        // its client-side extras below.
        int idx = _sim.SpawnUnitByID(unitDefID, pos);
        if (idx < 0) return idx;

        var unitDef = _gameData.Units.Get(unitDefID);
        if (unitDef == null) return idx;

        // If necromancer, record index in simulation
        if (unitDef.AI == "PlayerControlled")
        {
            _sim.SetNecromancerIndex(idx);
        }

        // Auto-enroll undead non-necromancer units in the horde (skip if archetype handles it)
        if (_sim.Units[idx].Archetype == 0 && _sim.Units[idx].Faction == Faction.Undead
            && _sim.Units[idx].AI != AIBehavior.PlayerControlled)
            _sim.Horde.AddUnit(_sim.Units[idx].Id);

        // Set up animation (shared factory — see Game1.Animation.cs)
        var anim = BuildUnitAnimData(unitDefID);
        if (anim != null)
            _unitAnims[_sim.Units[idx].Id] = anim.Value;

        return idx;
    }

    protected override void LoadContent()
    {
        _spriteBatch = new SpriteBatch(GraphicsDevice);
        _pixel = TextureUtil.GetWhitePixel(GraphicsDevice);
        _buffVisuals.SetPixel(_pixel);

        // Hybrid-GPU check: are we actually rendering on the integrated GPU while a
        // discrete one sits idle? GpuPreference wrote the high-performance registry
        // entry before context creation; if the driver didn't pick it up in-process,
        // tell the player a restart fixes it. (Toast drawn in GameRenderer.Units.cs.)
        string glRenderer = GpuPreference.ActiveRenderer();
        DebugLog.Log("startup", $"GL_RENDERER: {glRenderer}");
        if (GpuPreference.IsIntegrated(glRenderer) && GpuPreference.HasDiscreteAdapter())
        {
            _gpuWarnToastMsg = GpuPreference.WrotePreferenceThisLaunch
                ? "Integrated graphics detected - high-performance GPU enabled. Restart the game to apply."
                : "Running on integrated graphics. Set Necroking to High performance in Windows > Display > Graphics.";
            _gpuWarnToastTimer = 15f;
            DebugLog.Log("startup", $"GPU warning: {_gpuWarnToastMsg}");
        }

        // Shared radial glow texture (64x64, quadratic falloff) — cached in TextureUtil.
        _glowTex = TextureUtil.GetRadialGlow(GraphicsDevice);

        LogTiming("Glow texture created");

        // Shadow renderer (BasicEffect for parallelogram quads)
        _shadowRenderer.Init(GraphicsDevice);

        // Load main menu background
        string menuBgPath = GamePaths.Resolve(Path.Combine("assets", "UI", "Background", "VampireBackground.png"));
        if (File.Exists(menuBgPath))
        {
            _mainMenuBg = TextureUtil.LoadPremultiplied(GraphicsDevice, menuBgPath);
        }

        LogTiming("Menu background loaded");

        _font = Content.Load<SpriteFont>("DefaultFont");
        _smallFont = Content.Load<SpriteFont>("SmallFont");
        _largeFont = Content.Load<SpriteFont>("LargeFont");
        // Glyphs missing from the bitmap font (em-dashes, curly quotes, accents in
        // data/item text, etc.) otherwise make MeasureString/DrawString THROW,
        // crashing the whole game on a hover tooltip. A DefaultCharacter degrades
        // unsupported glyphs to '?' instead of throwing.
        _font.DefaultCharacter = '?';
        _smallFont.DefaultCharacter = '?';
        _largeFont.DefaultCharacter = '?';
        _debugDraw.SetFont(_smallFont);
        _hudRenderer.Init(_spriteBatch, _pixel, _font, _smallFont, _widgetRenderer);
        _hudRenderer.SetInput(_input);
        _characterStatsUI.Init(_spriteBatch, _pixel, _font, _smallFont);
        // Note: _uiShaders is initialized later after Content.Load path -- we
        // set it on the panel below after Load completes.
        _skillBookOverlay.Init(_widgetRenderer, _spriteBatch, _pixel);
        // Early bind so scenarios (which skip StartGame) still have state. The spell-
        // bar Slots may be null at this point — re-bind happens in StartGame once the
        // bar is allocated. AddSpellToBarEffect handles null Slots gracefully.
        _skillBookOverlay.Bind(_skillBookState, _inventory, _gameData,
            _spellBarState, _sim);

        // Load TrueType fonts via FontStashSharp (dynamic sizing)
        _fontManager.LoadFontsFromDirectory(GamePaths.Resolve(GamePaths.FontsDir));
        if (_fontManager.HasFonts)
        {
            // Prefer "Standard" as default, fall back to first loaded
            if (_fontManager.FontFamilies.Any(f => f == "Standard"))
                _fontManager.SetDefault("Standard");
        }
        LogTiming("Fonts loaded");

        _bloom.Init(GraphicsDevice, Content,
            _graphics.PreferredBackBufferWidth, _graphics.PreferredBackBufferHeight);
        LogTiming("Bloom initialized");

        try { _groundEffect = Content.Load<Microsoft.Xna.Framework.Graphics.Effect>("GroundShader"); }
        catch (Exception ex) { _groundEffect = null; DebugLog.Log("startup", $"GroundShader not loaded: {ex.Message}"); }

        try {
            _dissolveTreeEffect = Content.Load<Microsoft.Xna.Framework.Graphics.Effect>("DissolveTree");
            DebugLog.Log("startup", $"DissolveTree shader loaded — params: {string.Join(", ", _dissolveTreeEffect.Parameters.Cast<Microsoft.Xna.Framework.Graphics.EffectParameter>().Select(p => p.Name))}");
        }
        catch (Exception ex) { _dissolveTreeEffect = null; DebugLog.Log("startup", $"DissolveTree shader not loaded: {ex.Message}"); }

        _uiShaders = new UIShaders(GraphicsDevice, _pixel, BlendState.AlphaBlend, SamplerState.PointClamp);
        _uiShaders.Load(Content);

        try { _outlineFlatEffect = Content.Load<Microsoft.Xna.Framework.Graphics.Effect>("OutlineFlat"); }
        catch (Exception ex) { _outlineFlatEffect = null; DebugLog.Log("startup", $"OutlineFlat not loaded: {ex.Message}"); }
        try
        {
            _morphSdfEffect = Content.Load<Microsoft.Xna.Framework.Graphics.Effect>("MorphSDF");
            // Constant look parameters, set once (MGFX on GL ignores .fx initializers;
            // dynamic params — MorphT, GreenFill, OutlineColor, OutlinePulse, textures —
            // are set per draw in DrawReanimMorph). Bulge is the amoeba swell that opens
            // bridge gaps (was 4.0 — gentler swell reads as a quiet wisp, and the green
            // gap-fill is dimmed at the call site so it doesn't glow).
            _morphSdfEffect.Parameters["Bulge"]?.SetValue(2.0f);
            _morphSdfEffect.Parameters["EdgeSoftness"]?.SetValue(1.5f); // AA band, px
            _morphSdfEffect.Parameters["OutlineWidth"]?.SetValue(1.2f); // px
        }
        catch (Exception ex) { _morphSdfEffect = null; DebugLog.Log("startup", $"MorphSDF not loaded: {ex.Message}"); }
        try { _depthCutoutEffect = Content.Load<Microsoft.Xna.Framework.Graphics.Effect>("DepthCutout"); }
        catch (Exception ex) { _depthCutoutEffect = null; DebugLog.Log("startup", $"DepthCutout not loaded: {ex.Message}"); }
        try {
            _wadingEffect = Content.Load<Microsoft.Xna.Framework.Graphics.Effect>("Wading");
            var pnames = string.Join(",", _wadingEffect.Parameters.Select(p => p.Name));
            DebugLog.Log("startup", $"Wading loaded. params=[{pnames}]");
            // Constant look parameters, set once (MGFX on GL ignores .fx initializers).
            // The per-frame waterline/frame-UV params are set in DrawWadingSpriteFrame
            // and the WadingEditorPopup preview — both share this Effect instance.
            _wadingEffect.Parameters["FoamHalfWidth"]?.SetValue(0.05f);    // half-width of the foam band, local V
            _wadingEffect.Parameters["TopFoamHalfWidth"]?.SetValue(0.05f);
            _wadingEffect.Parameters["UnderwaterAlpha"]?.SetValue(0.0f);   // submerged pixels fully hidden
            _wadingEffect.Parameters["FoamColor"]?.SetValue(new Vector3(0.88f, 0.94f, 0.96f));
        }
        catch (Exception ex) { _wadingEffect = null; DebugLog.Log("startup", $"Wading NOT loaded: {ex.Message}"); }
        try { _hdrIntensityEffect = Content.Load<Microsoft.Xna.Framework.Graphics.Effect>("HdrIntensity"); }
        catch (Exception ex) { _hdrIntensityEffect = null; DebugLog.Log("startup", $"HdrIntensity not loaded: {ex.Message}"); }
        try
        {
            _hdrSpriteEffect = Content.Load<Microsoft.Xna.Framework.Graphics.Effect>("HdrSprite");
            if (_hdrSpriteEffect != null)
            {
                _hdrSpriteEffect.Parameters["MaxIntensity"]?.SetValue(HdrColor.MaxHdrIntensity);
                _hdrSpriteEffect.Parameters["AlphaMode"]?.SetValue(0f);
            }
        }
        catch (Exception ex) { _hdrSpriteEffect = null; DebugLog.Log("startup", $"HdrSprite not loaded: {ex.Message}"); }

        // Register the effect-backed materials now that shaders are loaded
        // (Materials is the canonical pass-state registry for the render
        // pipeline — see todos/render-pipeline-design.md).
        Render.Materials.InitEffectMaterials(_wadingEffect, _dissolveTreeEffect,
            _hdrSpriteEffect, _depthCutoutEffect, _morphSdfEffect, _outlineFlatEffect);

        {
            Microsoft.Xna.Framework.Graphics.Effect? glyphEffect = null;
            try { glyphEffect = Content.Load<Microsoft.Xna.Framework.Graphics.Effect>("MagicCircle"); }
            catch (Exception ex) { DebugLog.Log("startup", $"MagicCircle not loaded: {ex.Message}"); }
            _glyphRenderer.LoadEffect(glyphEffect);
        }

        {
            Microsoft.Xna.Framework.Graphics.Effect? fogEffect = null;
            try { fogEffect = Content.Load<Microsoft.Xna.Framework.Graphics.Effect>("WeatherFog"); }
            catch (Exception ex) { DebugLog.Log("startup", $"WeatherFog not loaded: {ex.Message}"); }
            _weatherRenderer.LoadEffect(fogEffect);
        }
        _weatherRenderer.Init(_graphics.PreferredBackBufferWidth, _graphics.PreferredBackBufferHeight);
        _weatherRenderer.SetDayNight(_dayNightSystem);

        _grassRenderer.Init(GraphicsDevice);
        Necroking.Render.MagicPathIcons.SetDevice(GraphicsDevice);
        _lightningRenderer.Init(_spriteBatch, _pixel, _glowTex, this, _camera, _renderer, GraphicsDevice, _hdrIntensityEffect);
        // When a ground vertex newly corrupts, fade nearby grass tufts toward
        // their CorruptedTint over GrassTuftRenderer.CorruptionFadeDuration.
        _groundSystem.OnVertexCorrupted = OnGroundVertexCorruptedForGrass;
        LogTiming("Renderers initialized (weather, grass, lightning)");

        // UI editor (F12 / menu) is a dev-only tool — its LoadDefinitions bakes
        // every harmonized texture (~4s). Deferred to first open via
        // EnsureUIEditorInitialized() so it never costs startup time.
        string uiDefPath = GamePaths.Resolve(GamePaths.UIDefsDir);
        LogTiming("UI editor init deferred");

        // Runtime widget renderer + inventory family UIs are deferred to
        // first-needed via EnsureInventoryUIsInitialized(). The widget renderer
        // is exclusively consumed by Inventory/Building/Crafting/Table UIs —
        // no other system uses it. Together they cost ~250 ms at startup;
        // most launches never open any of them so this is pure savings.
        _widgetRendererUiDefPath = uiDefPath;

        // Load audio
        try
        {
            string pickupPath = GamePaths.Resolve("assets/Audio/Interaction/PickupPop.wav");
            if (System.IO.File.Exists(pickupPath))
            {
                using var stream = System.IO.File.OpenRead(pickupPath);
                _pickupSound = SoundEffect.FromStream(stream);
            }
        }
        catch { /* audio is optional */ }

        // Wire the foragable subsystem now that all its dependencies exist.
        // Callbacks bridge back to Game1-private state (damage numbers, skill book).
        _foragables.Bind(this, _camera, _renderer, _spriteBatch,
            _inventory, _effectManager, _pickupSound,
            onPickup: OnForagablePickedUp,
            onLearnTrigger: OnForagableLearnTrigger);

        // Worker job system: brain that assigns grave workers to jobs.
        _workerSystem.Bind(this, _gameData);
        _workerSystem.Reset();

        // Install the Game1→Simulation back-references onto the current Sim. Also called
        // from StartGame after every session recreation (see WireSimCallbacks).
        WireSimCallbacks();
        // Reanimate job spawns through the canonical reanim pipeline (green rise effect).
        _workerSystem.SpawnWorkerUnit = (defId, pos) =>
            QueueReanimRise(defId, -1, "", posOverride: pos);  // "" → the unit's own effect (else reanim_smoke)

        // Lean dev control server: only when launched with --devserver <port>.
        if (LaunchArgs.DevServerPort > 0)
        {
            _devServer = new Necroking.Dev.DevServer(LaunchArgs.DevServerPort);
            if (_devServer.Start())
            {
                Exiting += (_, _) => _devServer?.Stop();
            }
            else
            {
                // Port taken (a second instance raced onto it). Without the control
                // channel this process is unreachable AND invisible (headless runs
                // hide the taskbar button), so it would idle forever burning CPU.
                // Exit instead; the supervisor reports "exited during startup".
                _devServer = null;
                Exit();
            }
        }

        // Init property editor infrastructure
        _editorUi.SetContext(_spriteBatch, _pixel, _font, _smallFont, _largeFont);
        _unitEditor = new UnitEditorWindow(_editorUi);
        _unitEditor.SetGameData(_gameData);
        _unitEditor.SetAtlases(_atlases, GraphicsDevice);
        _unitEditor.SetAnimMeta(_animMeta);
        // Hand the wading effect + camera Y-ratio to the wading sub-editor
        // so its preview applies the actual shader (matching in-game look).
        _unitEditor.SetWadingShader(_wadingEffect, _camera.YRatio);
        _spellEditor = new SpellEditorWindow(_editorUi);
        _spellEditor.SetGameData(_gameData);
        _spellEditor.SetHdrEffect(_hdrSpriteEffect);
        _spellEditor.SetFlipbooks(_flipbooks);
        _spellEditor.SetContent(Content);
        _itemEditor = new ItemEditorWindow(_editorUi);
        _itemEditor.SetGameData(_gameData);
        _net = new Necroking.Net.NetSession();
        _multiplayerWindow = new MultiplayerWindow(_editorUi);
        _multiplayerWindow.SetSession(_net);
        // Clean disconnect on quit so peers see us leave immediately instead of timing out.
        Exiting += (s, e) => _net.Stop();

        _settingsWindow = new SettingsWindow(_editorUi);
        System.IO.Directory.CreateDirectory(GamePaths.Resolve(GamePaths.UserSettingsDir));
        _settingsWindow.SetGameData(_gameData, GamePaths.Resolve(GamePaths.UserSettingsJson), GamePaths.Resolve(GamePaths.UserWeatherJson));
        _settingsWindow.SetDayNightSystem(_dayNightSystem);
        LogTiming("Editors initialized");
        DebugLog.Log("startup", $"=== LoadContent complete ===");
    }

    /// <summary>Read-only "what is under the cursor" picking that drives the debug
    /// info tooltips (object/corpse/unit/belly). Sets the <c>_hovered*Idx</c> /
    /// <c>_hoveredBellyUnitId</c> fields; renders nothing. Called from gameplay
    /// Update and from the map editor (hover-inspect) — never runs gameplay input.
    /// Uses <c>_input.MousePos</c> (not the raw MouseState) so picks stay anchored
    /// to the same cursor the tooltips draw at, and the `mousepos` dev override
    /// exercises the real pick path in headless runs.
    /// Gameplay: each pick kind honours its Tooltips toggle. Map editor: all kinds
    /// always pick (it's an inspection mode), including env objects that have no
    /// gameplay tooltip (trees, rocks, props). Both suppress over UI — see
    /// <see cref="HoverBlockedByUI"/> for what "over UI" means per mode.</summary>
    private void UpdateHoverPicks(int screenW, int screenH)
    {
        bool inspectAll = _menuState == MenuState.MapEditor;
        bool overUI = HoverBlockedByUI(screenW, screenH);
        Vec2 mouseWorld = _camera.ScreenToWorld(_input.MousePos, screenW, screenH);
        var tcfg = _gameData.Settings.Tooltips;

        // --- Ground-object hover detection (buildings + foragable items;
        //     every env object in the map editor) ---
        _hoveredObjectIdx = -1;
        if ((inspectAll || tcfg.ShowBuildingInfo || tcfg.ShowGroundItemInfo) && !overUI)
            _hoveredObjectIdx = _gameRenderer.PickHoveredObject(_input.MousePos, mouseWorld, inspectAll);

        // --- Corpse hover detection (for the reanimation info tooltip) ---
        // Skips bodies that are mid-dissolve, consumed, bagged, or being carried.
        _hoveredCorpseIdx = -1;
        if ((inspectAll || tcfg.ShowCorpseInfo) && !overUI)
            _hoveredCorpseIdx = _sim.Query.NearestCorpse(
                mouseWorld, tcfg.GroundPickRadius, CorpseExclude.Free);

        // --- Hovered unit (for the outline-box highlight + info tooltip) ---
        _hoveredUnitIdx = -1;
        if ((inspectAll || tcfg.ShowHoverHighlight) && !overUI)
            _hoveredUnitIdx = _sim.Query.UnitUnderCursor(mouseWorld, tcfg.HoverPickRadius);

        // Dev force-hover: pin the highlight to a chosen unit for headless variant testing.
        if (_devForceHoverUnitId != uint.MaxValue)
        {
            _hoveredUnitIdx = -1;
            for (int i = 0; i < _sim.Units.Count; i++)
                if (_sim.Units[i].Alive && _sim.Units[i].Id == _devForceHoverUnitId) { _hoveredUnitIdx = i; break; }
        }
        // Dev force-hover an env object by index (headless variant testing has no real mouse).
        if (_devForceHoverObjectIdx >= 0 && _devForceHoverObjectIdx < _envSystem.ObjectCount)
            _hoveredObjectIdx = _devForceHoverObjectIdx;

        // Forager (zombie boar) under the cursor? It suppresses the normal unit stat
        // sheet and instead shows a corpse-pile-style belly tooltip (mushrooms eaten).
        // Reuses the same hovered-unit pick that drives the outline highlight.
        _hoveredBellyUnitId = uint.MaxValue;
        if (_hoveredUnitIdx >= 0
            && _gameData.Units.Get(_sim.Units[_hoveredUnitIdx].UnitDefID)?.Tags.Contains("forager") == true)
            _hoveredBellyUnitId = _sim.Units[_hoveredUnitIdx].Id;
    }

    /// <summary>"Is the cursor over UI?" as the hover-inspect picks and the world-
    /// hover readout should see it. Gameplay: the per-frame MouseOverUI flag.
    /// Map editor: PopupManager keeps MouseOverUI true across the whole screen
    /// (the editor layer is a full-screen popup — same trap the scroll-zoom gate
    /// works around), so instead hover is blocked only while the cursor is on the
    /// editor's side panel, or while a sub-popup (texture browser, env editor, …)
    /// sits above the editor on the popup stack.</summary>
    internal bool HoverBlockedByUI(int screenW, int screenH) => _menuState == MenuState.MapEditor
        ? !_popups.IsEmpty || _mapEditor.IsMouseOverPanel(screenW, screenH)
        : _input.MouseOverUI;

    /// <summary>Rebuild the central UI hit-rect registry for this frame and derive
    /// the "cursor over UI" state from it in ONE place: popup layers (via
    /// PopupManager), the persistent HUD (spell bar, time controls, top-right
    /// button rows), and Game1-level extras (spell dropdown, skill toasts,
    /// aggression bar). Replaces the old scattering of per-element hit tests.
    /// The map editor's OverGameplayHud flag is derived here too — the HUD rows
    /// render on top of the editor, and without this a click on e.g. "Crafting"
    /// painted/placed in the world underneath.</summary>
    private void RebuildUIHitRects(int screenW, int screenH)
    {
        _uiHits.Clear();
        // Router layers top-down (append order = z-order, so HitId returns the
        // true topmost region): editor sub-popups via ModalStackLayer, the
        // editor/menu blankets, panels via their PanelLayers. The persistent
        // HUD widgets keep their fine-grained per-button ids below instead
        // (their layers deliberately don't self-append).
        _uiRouter.AppendHitRects(_uiHits,
            new Necroking.UI.UICtx(screenW, screenH, _clock.VisualTime));

        // The persistent gameplay HUD draws during normal play AND on top of the
        // (full-screen) map editor — HudVisible mirrors the showUI gate in DrawHudBlock.
        if (HudVisible)
        {
            _hudRenderer.AppendHitRects(_uiHits, screenW, screenH,
                _gameData.Settings.General.ShowTimeControls);
            _gameRenderer.AppendSkillToastHitRects(_uiHits, screenW, screenH);
            if (_gameRenderer.GetAggressionBarLayout(screenW, screenH, out var aggroBar, out _))
            {
                aggroBar.Inflate(0, 8); // same hover slack as the click handler
                _uiHits.Add("hud.aggression_bar", aggroBar);
            }
        }

        int mx = (int)_input.MousePos.X, my = (int)_input.MousePos.Y;
        if (_uiHits.Hit(mx, my))
            _input.MouseOverUI = true;

        // Map editor rides a full-screen blocking popup layer, so MouseOverUI is
        // blanket-true there; the editor instead needs "is the cursor on the HUD
        // drawn OVER me" — scoped to hud./toast. entries so its own popup-layer
        // blanket doesn't count.
        if (_menuState == MenuState.MapEditor)
            _mapEditor.OverGameplayHud = _uiHits.Hit(mx, my, "hud.") || _uiHits.Hit(mx, my, "toast.");
    }

    protected override void Update(GameTime gameTime)
    {
        // Drain dev-server commands first so they run even if the window is
        // unfocused (we bail out below in that case) and regardless of menu state.
        _devServer?.Drain(ExecuteDevCommand);

        // Pump multiplayer next, same placement rationale: the connection must
        // stay alive (keepalives, joins, ghost states) while paused, in menus,
        // or unfocused. See Game1.Net.cs / Net/README.md.
        UpdateNetwork(gameTime);

        // Headless (dev-server / scenario) runs keep an off-screen window; drop its
        // taskbar button so the supervisor-owned game doesn't clutter the taskbar.
        // MainWindowHandle isn't valid the instant the window is created, so retry
        // each frame until it applies (HideFromTaskbar returns true once done).
        if (LaunchArgs.Headless && !_taskbarHidden)
            _taskbarHidden = Core.WindowChrome.HideFromTaskbar();

        var kb = Keyboard.GetState();
        var mouse = Mouse.GetState();

        // Window unfocused or minimized. IsActive is MonoGame's canonical window-focus
        // flag. Exempt scenario / headless runs — automated runs often lack window focus,
        // and freezing them would break the test harness.
        bool unfocused = !IsActive && _activeScenario == null && LaunchArgs.Scenario == null && !LaunchArgs.Headless;

        // A dev server drives the game while the OS window is unfocused, so it must keep
        // ticking (like "run when unfocused"). But the dev server injects input directly
        // (DispatchSpellCast / editor mouse), so we still NEUTRALISE the real mouse below
        // when unfocused — otherwise a stray click on the unfocused window casts a spell.
        bool runWhenUnfocused = _gameData.Settings.General.RunWhenUnfocused;
        bool keepRunningUnfocused = runWhenUnfocused || _devServer != null;

        // Default behaviour: freeze entirely while unfocused — skip all input so
        // background clicks (taskbar, other apps) read by the global Mouse.GetState()
        // aren't consumed by the game, and skip the simulation tick. We still advance
        // _prevMouse/_prevKb to the real states so the click that refocuses the
        // window isn't seen as an in-game press.
        if (unfocused && !keepRunningUnfocused)
        {
            _prevKb = kb;
            _prevMouse = mouse;
            base.Update(gameTime);
            return;
        }

        int vpW = GraphicsDevice.Viewport.Width, vpH = GraphicsDevice.Viewport.Height;
        // Even while focused, the OS reports clicks whose cursor sits OUTSIDE the window
        // (drag-out, multi-monitor). Those must not cast/command either.
        bool cursorOutside = mouse.X < 0 || mouse.Y < 0 || mouse.X >= vpW || mouse.Y >= vpH;

        // A menu-state transition (editor opened/closed, pause menu, …) means the
        // in-flight press gesture belonged to the OLD screen — it must not fire a
        // widget that appeared under the cursor in the new mode. Checked before
        // Capture so a press starting this very Update is honored, not killed.
        if (_menuState != _menuStateAtLastCapture)
        {
            _input.ConsumeGesture();
            _menuStateAtLastCapture = _menuState;
        }

        if (unfocused)
        {
            // Kept running (dev server or "run when unfocused"): feed NEUTRAL input — no
            // buttons, no keys, cursor parked at centre — so background/real clicks + keys
            // aren't consumed and the camera doesn't edge-drift. Dev-injected input is
            // unaffected (it doesn't flow through here). Real states still reach
            // _prevMouse/_prevKb at the exit points, preserving refocus-click protection.
            var neutral = new MouseState(vpW / 2, vpH / 2, mouse.ScrollWheelValue,
                ButtonState.Released, ButtonState.Released, ButtonState.Released,
                ButtonState.Released, ButtonState.Released);
            _input.Capture(neutral, neutral, new KeyboardState(), new KeyboardState());
        }
        else if (cursorOutside)
        {
            // Focused but the cursor is outside the window: keep keyboard live, but strip
            // the mouse buttons so an out-of-bounds click can't trigger a world action.
            var noButtons = new MouseState(mouse.X, mouse.Y, mouse.ScrollWheelValue,
                ButtonState.Released, ButtonState.Released, ButtonState.Released,
                ButtonState.Released, ButtonState.Released);
            _input.Capture(noButtons, noButtons, kb, _prevKb);
        }
        else
        {
            _input.Capture(mouse, _prevMouse, kb, _prevKb);
        }

        // Dev-only cursor override (the `mousepos` dev command): headless runs have no
        // real mouse, so force MousePos to a fixed screen point to exercise hover UI
        // (tooltips, hover highlights) from the dev server. No-op when unset.
        if (_devMouseOverride.HasValue)
            _input.MousePos = _devMouseOverride.Value;

        // Alt+Enter: toggle between borderless "fullscreen" and a resizable window.
        // Handled here, before the menu-state early-returns below, so it works on the
        // main menu and scenario list too — not just in active gameplay.
        {
            bool textActive = (_editorUi != null && _editorUi.IsTextInputActive)
                || (_menuState == MenuState.UIEditor && _uiEditor.IsTextInputActive);
            if (!textActive
                && (_input.IsKeyDown(Keys.LeftAlt) || _input.IsKeyDown(Keys.RightAlt))
                && _input.WasKeyPressed(Keys.Enter))
                ToggleWindowMode();
        }

        // (All modal/panel/HUD/world input routing now happens in ONE place —
        // the _uiRouter.DispatchInput call further down, after hit rects are
        // rebuilt and the world clock is gated. Layers are walked top-down in
        // reverse render order and consume what they use; nothing is
        // pre-consumed on anyone's behalf.)

        // MapEditor → gameplay transition: fire the suppressed pathfinder
        // rebuild once. Per-click placement skips it (would be O(N) per
        // click for a populated map) so we batch into a single rebuild when
        // the editor closes. Catches all exit routes — ESC, pause-menu Resume,
        // main-menu, etc. — because we react to the state change, not the
        // closing action itself.
        if (_prevMenuState == MenuState.MapEditor && _menuState != MenuState.MapEditor)
        {
            // Re-derive pathfinding terrain from the (possibly re-painted)
            // vertex map BEFORE the env-driven rebuild — otherwise newly
            // painted shallow/deep water tiles won't have their cost field
            // updated and units would walk through deep water until the next
            // map load.
            _groundSystem.StampTerrainOnto(_sim.Grid);
            _envSystem.OnCollisionsDirty?.Invoke();
        }
        _prevMenuState = _menuState;

        // Clock phase 1: derive this frame's time domains (see GameClock docs).
        // Runs BEFORE the MainMenu/ScenarioList early-returns on purpose — menu frames
        // keep accruing VisualTime so shader/wind phases don't stall in menus.
        _clock.BeginFrame((float)gameTime.ElapsedGameTime.TotalSeconds);
        float rawDt = _clock.RawDt;
        float dt = _clock.VisualDt;

        // Decay spell-bar activation flashes in REAL time (rawDt) so the press
        // feedback fades consistently regardless of game pause/speed.
        for (int i = 0; i < _slotFlash.Length; i++)
            if (_slotFlash[i] > 0f) _slotFlash[i] = MathF.Max(0f, _slotFlash[i] - rawDt);

        // Drain a slice of the reanim-morph prewarm queue (one heavy SDF build per frame)
        // so the builds spread over the first seconds of play instead of one big stall.
        // No-op once the queue is empty.
        _gameRenderer.TickReanimMorphPrewarm();

        // Step the active dev batch script (if any) over the same sim/real clock a
        // scenario's OnTick uses, so scripted waits + screenshots land deterministically.
        // The sim-seconds dt must mirror the world gate (GateWorld runs later this
        // frame, so predict it): VisualDt keeps flowing while a full-screen editor
        // suspends the world or no world is loaded, but the sim doesn't — burning
        // VisualDt there would complete a {wait:n} while the sim is frozen.
        if (_devJob != null)
            UpdateDevScript(EditorActive || !_gameWorldLoaded ? 0f : dt, rawDt);

        // --- Diagnostic: auto-click Start Game from command line (--autostart) ---
        if (_menuState == MenuState.MainMenu && LaunchArgs.AutoStart)
        {
            LaunchArgs.AutoStart = false; // once
            StartGame();
            DebugLog.Log("startup", "[autostart] StartGame() returned — world loaded");
            if (LaunchArgs.BakeCentroids) _gameRenderer.BakeAllCorpseCentroids();
            if (LaunchArgs.Headless) _autostartExitPending = true; // exit on next frame
            _prevKb = kb;
            _prevMouse = mouse;
            base.Update(gameTime);
            return;
        }

        // --- Auto-start scenario from command line ---
        if (_menuState == MenuState.MainMenu && LaunchArgs.Scenario != null)
        {
            string scenName = LaunchArgs.Scenario;
            LaunchArgs.Scenario = null; // Only auto-start once
            StartScenario(scenName);
            if (_activeScenario == null)
            {
                // Unknown/typo'd name. The process exists solely to run this
                // scenario, so exit with an error instead of idling at the menu
                // forever (which wedged headless test runs and held the exe lock).
                var similar = ScenarioRegistry.GetNames()
                    .Where(n => n.Contains(scenName, StringComparison.OrdinalIgnoreCase)
                             || scenName.Contains(n, StringComparison.OrdinalIgnoreCase))
                    .Take(5).ToList();
                Console.Error.WriteLine($"SCENARIO FAIL: {scenName} (unknown scenario name)");
                if (similar.Count > 0)
                    Console.Error.WriteLine($"  Did you mean: {string.Join(", ", similar)}");
                Console.Error.WriteLine("  Valid names: see Register(...) calls in Necroking/Scenario/ScenarioRegistry.cs");
                Environment.ExitCode = 2;
                Exit();
            }
            _prevKb = kb;
            _prevMouse = mouse;
            base.Update(gameTime);
            return;
        }

        // --- Main menu ---
        if (_menuState == MenuState.MainMenu)
        {
            if (_input.LeftPressed)
            {
                int screenW2 = GraphicsDevice.Viewport.Width;
                int screenH2 = GraphicsDevice.Viewport.Height;
                int btnW = 320, btnH = 55, btnGap = 18;
                int menuX = screenW2 / 2 - btnW / 2;
                int menuY = screenH2 / 2 + 20;

                // Play button
                if (mouse.X >= menuX && mouse.X < menuX + btnW && mouse.Y >= menuY && mouse.Y < menuY + btnH)
                {
                    StartGame();
                    _prevKb = kb;
                    _prevMouse = mouse;
                    base.Update(gameTime);
                    return;
                }
                menuY += btnH + btnGap;

                // Play Test Map button — loads assets/maps/testmap.json
                if (mouse.X >= menuX && mouse.X < menuX + btnW && mouse.Y >= menuY && mouse.Y < menuY + btnH)
                {
                    StartGame("testmap");
                    _prevKb = kb;
                    _prevMouse = mouse;
                    base.Update(gameTime);
                    return;
                }
                menuY += btnH + btnGap;

                // Scenarios button
                if (mouse.X >= menuX && mouse.X < menuX + btnW && mouse.Y >= menuY && mouse.Y < menuY + btnH)
                {
                    _menuState = MenuState.ScenarioList;
                    _scenarioScrollOffset = 0;
                    _prevKb = kb;
                    _prevMouse = mouse;
                    base.Update(gameTime);
                    return;
                }
                menuY += btnH + btnGap;

                // Quit button
                if (mouse.X >= menuX && mouse.X < menuX + btnW && mouse.Y >= menuY && mouse.Y < menuY + btnH)
                    Exit();
            }
            _prevKb = kb;
            _prevMouse = mouse;
            base.Update(gameTime);
            return;
        }

        // --- Scenario list ---
        if (_menuState == MenuState.ScenarioList)
        {
            // Escape to go back
            if (_input.WasKeyPressed(Keys.Escape))
            {
                _menuState = MenuState.MainMenu;
                _prevKb = kb;
                _prevMouse = mouse;
                base.Update(gameTime);
                return;
            }

            int screenW2 = GraphicsDevice.Viewport.Width;
            int screenH2 = GraphicsDevice.Viewport.Height;
            var view = _gameRenderer.BuildScenarioMenuLayout(screenW2, screenH2, _scenarioScrollOffset);

            // Draggable scrollbar — same behaviour as the editor panels (shared
            // Necroking.UI.VScrollbar). Thumb math runs in pixels (row * RowStride);
            // the resulting offset is converted back to whole layout rows.
            if (_scenarioScrollDragging && mouse.LeftButton != ButtonState.Pressed)
                _scenarioScrollDragging = false;

            bool hasBar = !Necroking.UI.VScrollbar.Fits(view.ScrollViewH, view.ScrollContentH);
            if (hasBar)
            {
                float scrollPx = _scenarioScrollOffset * (float)view.RowStride;
                var thumb = Necroking.UI.VScrollbar.ThumbRect(view.ScrollX, view.ScrollY, view.ScrollViewH, view.ScrollContentH, scrollPx);
                var hit = Necroking.UI.VScrollbar.HitRect(view.ScrollX, view.ScrollY, view.ScrollViewH);

                // Grab the thumb, or click the track to jump (thumb centres on the cursor).
                if (_input.LeftPressed && hit.Contains(mouse.X, mouse.Y))
                {
                    _scenarioScrollDragging = true;
                    _scenarioScrollGrabOffset = thumb.Contains(mouse.X, mouse.Y) ? mouse.Y - thumb.Y : thumb.Height / 2f;
                }

                if (_scenarioScrollDragging)
                {
                    float newPx = Necroking.UI.VScrollbar.ScrollFromDrag(mouse.Y, _scenarioScrollGrabOffset,
                        view.ScrollY, view.ScrollViewH, view.ScrollContentH);
                    _scenarioScrollOffset = view.ClampScroll((int)Math.Round(newPx / view.RowStride));
                }
            }

            // Wheel scroll (a few layout rows at a time), clamped to the layout height.
            if (!_scenarioScrollDragging && _input.ScrollDelta != 0)
            {
                _scenarioScrollOffset += _input.ScrollDelta > 0 ? -3 : 3;
                _scenarioScrollOffset = view.ClampScroll(_scenarioScrollOffset);
            }

            // Grid / back-button clicks (skipped while dragging the scrollbar).
            if (_input.LeftPressed && !_scenarioScrollDragging)
            {
                foreach (var e in view.Entries)
                {
                    if (!e.Visible || e.IsHeader) continue;
                    if (e.Rect.Contains(mouse.X, mouse.Y))
                    {
                        StartScenario(e.Text);
                        _prevKb = kb;
                        _prevMouse = mouse;
                        base.Update(gameTime);
                        return;
                    }
                }

                // Back button (fixed below the visible grid)
                if (view.BackRect.Contains(mouse.X, mouse.Y))
                    _menuState = MenuState.MainMenu;
            }
            _prevKb = kb;
            _prevMouse = mouse;
            base.Update(gameTime);
            return;
        }

        // Check if any editor UI element owns the keyboard — active text field,
        // open combo filter, or color-picker value box (persists from previous
        // frame, safe to read before UpdateInput). Typing a digit into a combo
        // filter must not trip F-key/hotkey toggles.
        bool anyTextInputActive = (_editorUi != null && _editorUi.IsKeyboardCaptured)
            || (_menuState == MenuState.UIEditor && _uiEditor.IsKeyboardCaptured);

        // --- F2 water debug toggle ---
        if (!anyTextInputActive && _input.WasKeyPressed(Keys.F2))
            _waterDebug = !_waterDebug;

        // --- F3 toggles the bottom-left perf/zoom readout ---
        if (!anyTextInputActive && _input.WasKeyPressed(Keys.F3))
            _showPerfReadout = !_showPerfReadout;

        // --- F5 death-fog debug overlay (F9 was requested but is taken by the
        // unit editor toggle; F5 was free) ---
        if (!anyTextInputActive && _input.WasKeyPressed(Keys.F5))
            _deathFog.ToggleDebug();

        // --- F6 wind debug toggle ---
        if (!anyTextInputActive && _input.WasKeyPressed(Keys.F6))
            _windDebug = !_windDebug;

        // --- F7 gameplay debug toggle (Off → Horde → Unit Info → Off) ---
        if (!anyTextInputActive && _input.WasKeyPressed(Keys.F7))
            _gameplayDebugMode = (_gameplayDebugMode + 1) % 3;

        // --- F8 collision debug toggle ---
        if (!anyTextInputActive && _input.WasKeyPressed(Keys.F8))
        {
            _collisionDebugMode = (CollisionDebugMode)(((int)_collisionDebugMode + 1) % (int)CollisionDebugMode.Count);
        }

        // --- F9-F12 editor toggles ---
        if (!anyTextInputActive && _input.WasKeyPressed(Keys.F9))
        {
            _menuState = _menuState == MenuState.UnitEditor ? MenuState.None : MenuState.UnitEditor;
            _editorUi.ClearActiveField();
        }
        if (!anyTextInputActive && _input.WasKeyPressed(Keys.F10))
        {
            _menuState = _menuState == MenuState.SpellEditor ? MenuState.None : MenuState.SpellEditor;
            _editorUi.ClearActiveField();
        }
        if (!anyTextInputActive && _input.WasKeyPressed(Keys.F11))
            _menuState = _menuState == MenuState.MapEditor ? MenuState.None : MenuState.MapEditor;
        if (!anyTextInputActive && _input.WasKeyPressed(Keys.F12))
        {
            if (_menuState == MenuState.UIEditor) _menuState = MenuState.None;
            else { EnsureUIEditorInitialized(); _menuState = MenuState.UIEditor; }
        }

        // (Editor-launcher row clicks are handled by EditorLauncherLayer via the
        // UI router dispatch below — see UI/Layers/HudLayers.cs.)

        // 'I' key toggles inventory (lazy-inits the UI family on first open)
        if (!anyTextInputActive && _input.WasKeyPressed(Keys.I) && _menuState == MenuState.None)
        {
            EnsureInventoryUIsInitialized();
            _inventoryUI.Toggle(GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height);
        }

        // 'O' key toggles the job board (wOrk/jObs). Shift+O is the skill-point
        // cheat below — don't also flip the board on it.
        if (!anyTextInputActive && _input.WasKeyPressed(Keys.O) && _menuState == MenuState.None
            && !_input.IsKeyDown(Keys.LeftShift) && !_input.IsKeyDown(Keys.RightShift))
        {
            EnsureInventoryUIsInitialized();
            if (!_jobBoardUI.IsVisible) CloseSameSidePanels(PanelSide.Left, _jobBoardUI);
            _jobBoardUI.Toggle(GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height);
        }

        // 'Tab' key toggles character stats
        if (!anyTextInputActive && _input.WasKeyPressed(Keys.Tab) && _menuState == MenuState.None)
        {
            if (!_characterStatsUI.IsVisible) CloseSameSidePanels(PanelSide.Left, _characterStatsUI);
            _characterStatsUI.Toggle();
        }

        // 'K' = the Ability Upgrades skill book (tabbed school trees).
        if (!anyTextInputActive && _input.WasKeyPressed(Keys.K) && _menuState == MenuState.None)
            _skillBookOverlay.Toggle();

        // 'J' = spell grimoire (phase 1: display only)
        if (!anyTextInputActive && _input.WasKeyPressed(Keys.J) && _menuState == MenuState.None)
        {
            EnsureInventoryUIsInitialized();
            if (!_grimoireOverlay.IsVisible) CloseSameSidePanels(PanelSide.Left, _grimoireOverlay);
            _grimoireOverlay.Toggle();
        }

        // 'U' = character sheet for the player necromancer (current form)
        if (!anyTextInputActive && _input.WasKeyPressed(Keys.U) && _menuState == MenuState.None)
        {
            EnsureInventoryUIsInitialized();
            if (_unitInfoPanel.IsVisible)
                _unitInfoPanel.Hide();
            else if (_sim.NecromancerIndex >= 0)
            {
                CloseSameSidePanels(PanelSide.Right, _unitInfoPanel);
                _unitInfoPanel.ShowForUnit(_sim.Units[_sim.NecromancerIndex].Id);
            }
        }

        // Hover-highlight is configured in Settings ▸ Tooltips (per-category shape/style). A dev
        // OVERRIDE still exists via the 'hover_variant' dev command for quick previewing; its toast
        // timer ticks down here. (No keyboard hotkey — it was removed to free 'H'.)
        if (_hoverVariantLabelTimer > 0f) _hoverVariantLabelTimer -= _rawDt;
        if (_depthFogToastTimer > 0f) _depthFogToastTimer -= _rawDt;
        if (_gpuWarnToastTimer > 0f) _gpuWarnToastTimer -= _rawDt;
        if (_foragerEatSoundCd > 0f) _foragerEatSoundCd -= _rawDt;

        // 'H' = toggle depth-sorted reanimation fog (A/B dev switch; Performance.DepthSortedFog).
        // ON = a risen unit can occlude its own lingering smoke; OFF = the fog always draws on top.
        if (!anyTextInputActive && _input.WasKeyPressed(Keys.H) && _menuState == MenuState.None)
        {
            _gameData.Settings.Performance.DepthSortedFog = !_gameData.Settings.Performance.DepthSortedFog;
            _depthFogToastTimer = 2.25f;   // flash the new state on screen
        }

        // 'L' = inspect ("Look at") the unit under the cursor (press-to-inspect
        // mode; may auto-pause while open, closing restores only the pause WE set).
        // Disabled when auto-show-on-hover is on — the hover logic below owns
        // the panel in that mode. Both modes share the configurable pick radius.
        var tipCfg = _gameData.Settings.Tooltips;
        if (!tipCfg.AutoShowUnitStats
            && !anyTextInputActive && _input.WasKeyPressed(Keys.L) && _menuState == MenuState.None)
        {
            EnsureInventoryUIsInitialized();
            if (_unitInfoPanel.IsVisible)
            {
                _unitInfoPanel.Hide();
            }
            else
            {
                int sw = GraphicsDevice.Viewport.Width, sh = GraphicsDevice.Viewport.Height;
                Vec2 cursorWorld = _camera.ScreenToWorld(new Vector2(mouse.X, mouse.Y), sw, sh);
                int best = -1;
                float pr = tipCfg.HoverPickRadius;
                float bestD2 = pr * pr; // pick radius in world units
                for (int i = 0; i < _sim.Units.Count; i++)
                {
                    var u = _sim.Units[i];
                    if (!u.Alive) continue;
                    // Foragers (zombie boars) never show the unit sheet — they use the belly tooltip.
                    if (_gameData.Units.Get(u.UnitDefID)?.Tags.Contains("forager") == true) continue;
                    float d2 = (u.Position - cursorWorld).LengthSq();
                    if (d2 < bestD2) { bestD2 = d2; best = i; }
                }
                if (best >= 0)
                {
                    _unitInfoPanel.ShowForUnit(_sim.Units[best].Id);
                    if (tipCfg.PauseOnManualInspect && !_paused) _clock.Pause(GameClock.PauseSource.Inspect);
                }
            }
        }

        // --- Pause menu button clicks ---
        if (_menuState == MenuState.PauseMenu && _input.LeftPressed)
        {
            int sw = GraphicsDevice.Viewport.Width;
            int sh = GraphicsDevice.Viewport.Height;
            int btnW2 = 280, btnH2 = 40, btnGap2 = 10;
            int pauseBtnCount = 10;
            int pauseControlLines = 4;
            int pauseBoxH = 60 + pauseBtnCount * (btnH2 + btnGap2) + 10 + pauseControlLines * 16 + 20;
            int boxY2 = (sh - pauseBoxH) / 2 + 60;
            int menuX2 = (sw - btnW2) / 2;
            int y2 = boxY2;

            // Resume
            if (mouse.X >= menuX2 && mouse.X < menuX2 + btnW2 && mouse.Y >= y2 && mouse.Y < y2 + btnH2)
            { _menuState = MenuState.None; _clock.ClearAllPauses(); }
            y2 += btnH2 + btnGap2;
            // Unit Editor
            if (mouse.X >= menuX2 && mouse.X < menuX2 + btnW2 && mouse.Y >= y2 && mouse.Y < y2 + btnH2)
            { _menuState = MenuState.UnitEditor; _clock.ClearAllPauses(); }
            y2 += btnH2 + btnGap2;
            // Spell Editor
            if (mouse.X >= menuX2 && mouse.X < menuX2 + btnW2 && mouse.Y >= y2 && mouse.Y < y2 + btnH2)
            { _menuState = MenuState.SpellEditor; _clock.ClearAllPauses(); }
            y2 += btnH2 + btnGap2;
            // Map Editor
            if (mouse.X >= menuX2 && mouse.X < menuX2 + btnW2 && mouse.Y >= y2 && mouse.Y < y2 + btnH2)
            { _menuState = MenuState.MapEditor; _clock.ClearAllPauses(); _mapEditor.SuppressClicksUntilRelease(); }
            y2 += btnH2 + btnGap2;
            // UI Editor
            if (mouse.X >= menuX2 && mouse.X < menuX2 + btnW2 && mouse.Y >= y2 && mouse.Y < y2 + btnH2)
            { EnsureUIEditorInitialized(); _menuState = MenuState.UIEditor; _clock.ClearAllPauses(); }
            y2 += btnH2 + btnGap2;
            // Item Editor
            if (mouse.X >= menuX2 && mouse.X < menuX2 + btnW2 && mouse.Y >= y2 && mouse.Y < y2 + btnH2)
            { _menuState = MenuState.ItemEditor; _clock.ClearAllPauses(); }
            y2 += btnH2 + btnGap2;
            // Settings
            if (mouse.X >= menuX2 && mouse.X < menuX2 + btnW2 && mouse.Y >= y2 && mouse.Y < y2 + btnH2)
            { _menuState = MenuState.Settings; }
            y2 += btnH2 + btnGap2;
            // Multiplayer
            if (mouse.X >= menuX2 && mouse.X < menuX2 + btnW2 && mouse.Y >= y2 && mouse.Y < y2 + btnH2)
            { _menuState = MenuState.Multiplayer; }
            y2 += btnH2 + btnGap2 + 10;
            // Main Menu
            if (mouse.X >= menuX2 && mouse.X < menuX2 + btnW2 && mouse.Y >= y2 && mouse.Y < y2 + btnH2)
            { _menuState = MenuState.MainMenu; _clock.ClearAllPauses(); _gameWorldLoaded = false; }
            y2 += btnH2 + btnGap2;
            // Quit
            if (mouse.X >= menuX2 && mouse.X < menuX2 + btnW2 && mouse.Y >= y2 && mouse.Y < y2 + btnH2)
                Exit();
        }

        // --- Settings window close handling ---
        if (_menuState == MenuState.Settings && _settingsWindow.WantsClose)
        {
            _settingsWindow.WantsClose = false;
            _editorUi.ResetAllState();
            _menuState = MenuState.PauseMenu;
        }

        // --- Multiplayer window close handling (session keeps running) ---
        if (_menuState == MenuState.Multiplayer && _multiplayerWindow.WantsClose)
        {
            _multiplayerWindow.WantsClose = false;
            _editorUi.ResetAllState();
            _menuState = MenuState.PauseMenu;
        }

        // --- ESC: gameplay → pause menu only ---
        // (The "ESC with nothing open → pause menu" fallback now lives AFTER
        // the router dispatch below, since panels/editors/popups consume ESC
        // during the dispatch walk, not before this point.)

        // --- Editor updates ---
        int screenW = GraphicsDevice.Viewport.Width;
        int screenH = GraphicsDevice.Viewport.Height;
        // Update EditorBase input first so _eb has current mouse/keyboard state for all editors
        // WASD/arrow ownership: only the BARE map editor (map editor on top, no
        // sub-editor popup focused) keeps WASD for camera panning; everywhere else
        // WASD navigates the focused editor list. Arrows always navigate lists.
        // "Bare" = the map editor with no sub-editor popup above it (the popup
        // stack now only ever holds the editors' transient sub-popups).
        bool bareMapEditor = _menuState == MenuState.MapEditor && _popups.IsEmpty;
        _editorUi.AllowWasdListNav = !bareMapEditor;
        if (_uiEditor != null) _uiEditor.AllowWasdListNav = true;
        if (_mapEditor != null) _mapEditor.CameraInputEnabled = bareMapEditor;

        // Central UI hit-rect pass: catalogue every active UI region (popup
        // panels, HUD buttons/bars, toasts) and derive MouseOverUI from it in one
        // place. Runs after the keyboard toggles above (so a panel opened this
        // frame is counted) and before the editor updates / world input below
        // (which consume MouseOverUI and OverGameplayHud).
        RebuildUIHitRects(screenW, screenH);

        if (_menuState == MenuState.UnitEditor || _menuState == MenuState.SpellEditor || _menuState == MenuState.MapEditor || _menuState == MenuState.Settings || _menuState == MenuState.Multiplayer || _menuState == MenuState.ItemEditor)
        {
            _editorUi.UpdateInput(mouse, _prevMouse, kb, _prevKb, screenW, screenH, gameTime, _input);
        }
        if (_menuState == MenuState.MapEditor && _gameWorldLoaded)
        {
            _mapEditor.Update(screenW, screenH);
        }
        if (_menuState == MenuState.Settings)
            _settingsWindow.Update(screenW, screenH, gameTime);
        if (_menuState == MenuState.UIEditor)
        {
            EnsureUIEditorInitialized(); // safety net: idempotent, covers any entry path
            // UIEditorWindow IS-A EditorBase with its own private _input field.
            // Without this UpdateInput call its _input.Mouse stays default
            // (released) and LeftJustPressed never fires — tab clicks, swatch
            // clicks, etc. all go nowhere. Mirrors the pattern used for
            // _editorUi above; the trailing _input arg shares Game1's
            // captured InputState so the UI editor sees real mouse data.
            _uiEditor.UpdateInput(mouse, _prevMouse, kb, _prevKb, screenW, screenH, gameTime, _input);
            _uiEditor.Update(screenW, screenH);
        }

        // --- Camera ---
        _renderer.SetScreenSize(screenW, screenH);

        // Camera follows necromancer, or free pan in editors/scenarios
        int necroIdx = FindNecromancer();
        bool editorOpen = _menuState == MenuState.UnitEditor || _menuState == MenuState.SpellEditor
            || _menuState == MenuState.MapEditor || _menuState == MenuState.UIEditor
            || _menuState == MenuState.ItemEditor;
        if (editorOpen)
        {
            // Editors no longer free-pan with WASD/arrows — those keys belong to
            // editor list navigation now (handled in EditorBase.DrawScrollableList).
            // The map editor still pans with WASD via MapEditorWindow.Update, gated
            // on CameraInputEnabled (true only for the bare map editor); arrows
            // there are free for list nav.
        }
        else if (necroIdx >= 0 && !_devFreeCamera)
        {
            var necroPos = _sim.Units[necroIdx].Position;
            var diff = necroPos - _camera.Position;
            _camera.Position += diff * MathF.Min(1f, 5f * rawDt);
        }
        else if (_activeScenario != null || _menuState == MenuState.None)
        {
            // Free camera pan with arrow keys (scenarios or when no necromancer)
            float camSpeed = 400f / MathF.Max(1f, _camera.Zoom);
            Vec2 camMove = Vec2.Zero;
            if (_input.IsKeyDown(Keys.Up)) camMove.Y -= 1f;
            if (_input.IsKeyDown(Keys.Down)) camMove.Y += 1f;
            if (_input.IsKeyDown(Keys.Left)) camMove.X -= 1f;
            if (_input.IsKeyDown(Keys.Right)) camMove.X += 1f;
            if (camMove.LengthSq() > 0.01f)
                _camera.Position += camMove.Normalized() * camSpeed * rawDt;
        }

        // (Skill-toast clicks, the aggression bar, and gameplay scroll-zoom are
        // handled by their router layers in the dispatch below.)

        // (Scroll zoom lives in the router layers: gameplay zoom in
        // WorldInputLayer, map-editor world-area zoom in MapEditorLayer.)

        // Editors pause the game. Clock phase 2: all pause/menu input for the frame
        // has run by here, so gate the world domain — WorldRunning/WorldDt reflect
        // THIS frame's pause + editor state (same-frame freeze, as the old inline
        // `!_paused && !editorActive` check did).
        bool editorActive = EditorActive;
        _clock.GateWorld(worldSuspended: editorActive);

        // Map-editor hover-inspect: run ONLY the read-only debug-tooltip picks while
        // editing the map so you can hover things to see what they are. None of the
        // gameplay input below runs in editors. The HUD pass renders the resulting
        // tooltips; UpdateHoverPicks handles the editor-specific "over UI" rules.
        if (_menuState == MenuState.MapEditor)
            UpdateHoverPicks(screenW, screenH);

        // --- Unified UI input dispatch: every clickable UI surface — editor
        // sub-popups, editors, menus, toasts, HUD-over-overlay button rows,
        // the blocking skill book, side panels, HUD widgets, and the world
        // floor — as router layers walked top-down in reverse render order.
        // Each layer sees exactly what the layers above it left un-consumed;
        // hover ownership is stamped from the same walk (IsHovered /
        // HoverStolen), so a widget only hover-lights when a click would
        // actually reach it.
        _uiRouter.DispatchInput(_input,
            new Necroking.UI.UICtx(screenW, screenH, gameTime.TotalGameTime.TotalSeconds, gameTime));

        // ESC fallback: nothing above wanted the key (no popup, panel, editor,
        // or menu consumed it) → open the pause menu.
        if (!anyTextInputActive && _input.WasKeyPressedUnhandled(Keys.Escape)
            && _menuState == MenuState.None)
        {
            _menuState = MenuState.PauseMenu;
            _clock.Pause(GameClock.PauseSource.User);
            _input.ConsumeKey(Keys.Escape);
        }

        if (_clock.WorldRunning)
        {
            // --- Player input ---
            Vec2 mouseWorld = _camera.ScreenToWorld(new Vector2(mouse.X, mouse.Y), screenW, screenH);

            if (necroIdx >= 0)
            {
                // WASD movement
                Vec2 moveDir = Vec2.Zero;
                if (_input.IsKeyDown(Keys.W)) moveDir.Y -= 1f;
                if (_input.IsKeyDown(Keys.S)) moveDir.Y += 1f;
                if (_input.IsKeyDown(Keys.A)) moveDir.X -= 1f;
                if (_input.IsKeyDown(Keys.D)) moveDir.X += 1f;
                bool wasdHeld = moveDir.LengthSq() > 0.01f;
                if (wasdHeld) moveDir = moveDir.Normalized();

                // Dev "walk_necro" auto-walk: drives the same movement input toward
                // a goal point. Any WASD press cancels it so manual control always
                // wins (avoids the player fighting an invisible auto-walk).
                if (_devWalkTarget.HasValue)
                {
                    if (wasdHeld)
                    {
                        _devWalkTarget = null;
                    }
                    else
                    {
                        var toGoal = _devWalkTarget.Value - _sim.Units[necroIdx].Position;
                        if (toGoal.LengthSq() <= 0.25f) // ~0.5 world units = arrived
                            _devWalkTarget = null;
                        else
                            moveDir = toGoal.Normalized();
                    }
                }

                bool running = _input.IsKeyDown(Keys.LeftShift) || _input.IsKeyDown(Keys.RightShift)
                    || (_devWalkTarget.HasValue && _devWalkSprint);
                _sim.SetNecromancerInput(moveDir, running);

                // Mouse facing — skipped when the necromancer is mid-scripted-
                // action (e.g. corpse pickup/putdown, channeling at bench). The
                // movement subsystem already zeros velocity in those states, but
                // facing would otherwise still snap to the cursor every frame
                // and swing the bag around mid-animation. WASD input is left
                // ungated so cancel-by-WASD on routines still works through
                // Simulation's existing path.
                if (!_sim.Units[necroIdx].IsLockedByAction())
                {
                    var necroPos = _sim.Units[necroIdx].Position;
                    var toMouse = mouseWorld - necroPos;
                    if (toMouse.LengthSq() > 0.01f)
                    {
                        float mouseAngle = MathF.Atan2(toMouse.Y, toMouse.X) * 180f / MathF.PI;
                        _sim.SetNecromancerFacing(mouseAngle);
                    }
                }
            }

            // --- Jump input (Space = jump attack) ---
            if (_input.WasKeyPressed(Keys.Space) && necroIdx >= 0)
            {
                var mu = _sim.UnitsMut;
                bool canJump = mu[necroIdx].Alive && mu[necroIdx].JumpPhase == 0
                    && !mu[necroIdx].Incap.IsLocked && !_pendingSpell.Active;
                if (canJump)
                {
                    var facingDir = Movement.FacingUtil.ForwardDir(mu[necroIdx]);
                    JumpSystem.BeginJumpAttack(mu, necroIdx, mu[necroIdx].Position + facingDir * 4f);
                }
            }

            // --- Beam/drain channel-hold ---
            if (_channelingSlot >= 0
                && !SpellBarBindings.IsSlotHeld(_input, _channelingSlot))
            {
                if (necroIdx >= 0)
                {
                    _sim.Lightning.CancelBeamsForCaster(_sim.Units[necroIdx].Id);
                    _sim.Lightning.CancelDrainsForCaster(_sim.Units[necroIdx].Id);
                }
                _channelingSlot = -1;
            }

            // (Spell-bar slot clicks are handled by SpellBarLayer in the router
            // dispatch above; slot assignment opens the grimoire assign flow.)

            // --- Aggression bar: Shift+E raises, Shift+Q lowers. The shift guard
            // in the cast loop stops Q/E from also casting slots 0/1 while adjusting. ---
            bool aggrShift = _input.IsKeyDown(Keys.LeftShift) || _input.IsKeyDown(Keys.RightShift);
            if (aggrShift && _input.WasKeyPressed(Keys.E)) _sim.Horde.AggressionLevel++;
            if (aggrShift && _input.WasKeyPressed(Keys.Q)) _sim.Horde.AggressionLevel--;

            // --- Spell casting (keyboard-only; see SpellBarBindings for the
            // slot→key table). Mouse buttons never cast — they belong to the
            // world-click dispatch in Game1.WorldClicks.cs. ---
            for (int slot = 0; slot < SpellBarBindings.SlotCount; slot++)
            {
                if (aggrShift && slot <= 1) continue; // Shift+Q/E = aggression, not a cast
                if (!SpellBarBindings.WasSlotPressed(_input, slot)) continue;
                if (slot >= _spellBarState.Slots.Length) continue;
                DispatchSpellCast(_spellBarState.Slots[slot].SpellID, necroIdx, slot, mouseWorld);
            }

            // --- Ghost mode toggle (G) ---
            if (_input.WasKeyPressed(Keys.G) && necroIdx >= 0)
                _sim.UnitsMut[necroIdx].GhostMode = !_sim.Units[necroIdx].GhostMode;

            // --- God mode toggle (Shift+P) ---
            // Cheat / debug toggle. Applies/removes buff_god_mode on the
            // necromancer; the buff's effects (path-9, +caps, +mana, +regen,
            // 2x movement) come from data/buffs.json. Duration=0 on the def
            // means the buff is Permanent until removed.
            if (_input.WasKeyPressed(Keys.P) && necroIdx >= 0
                && (_input.IsKeyDown(Keys.LeftShift) || _input.IsKeyDown(Keys.RightShift)))
            {
                ToggleGodMode(necroIdx);
            }

            // --- Add Skillpoints ---
            // Cheat / debug to test skill tree.
            if (_input.WasKeyPressed(Keys.O) && necroIdx >= 0
                                             && (_input.IsKeyDown(Keys.LeftShift) || _input.IsKeyDown(Keys.RightShift)))
            {
                CheatAddAllSkillcounters(necroIdx, 10);
            }
            
            // Potions are Construction spells (assignable to any spell slot) —
            // the old dedicated potion slots and their throw-on-click flow are
            // gone; casting routes through CastPotionSpell.

            // --- Corpse interaction (F key) — see Game1.WorldClicks.cs ---
            if (_input.WasKeyPressed(Keys.F))
                HandleInteractKey(necroIdx, mouseWorld);

            // --- Tethers (Shift+T target, Shift+R attach/detach) ---
            // Shift+T marks the unit/corpse under the cursor as the tether anchor; Shift+R
            // then connects it to whatever's under the cursor (any unit↔corpse combo). With
            // no anchor set, Shift+R detaches a tethered thing under the cursor, or quick-
            // drags the nearest free corpse with the necromancer.
            bool tetherShift = _input.IsKeyDown(Keys.LeftShift) || _input.IsKeyDown(Keys.RightShift);
            if (_input.WasKeyPressed(Keys.T) && tetherShift)
            {
                _tetherAnchor = TryPickTetherEnd(mouseWorld, out var anchor) ? anchor : (TetherEnd?)null;
            }
            if (_input.WasKeyPressed(Keys.R) && tetherShift)
            {
                HandleRopeKey(mouseWorld, necroIdx);
            }

            // Per-frame tether physics (haul corpses, slow the necromancer, kick up dust).
            UpdateTethers(_clock.WorldDt);

            // --- Read-only hover picks (drive the debug info tooltips) ---
            // Object/corpse/unit/belly picks under the cursor. Shared with the
            // map editor's hover-inspect (the MenuState.MapEditor branch above).
            UpdateHoverPicks(screenW, screenH);

            // --- Unit auto-hover stat sheet (Factorio-style; opt-in via Tooltips) ---
            // The cursor "carries" the stat sheet: hover a unit to show it, move off
            // to dismiss. Never pauses. The auto-shown panel is transient (not a
            // popup) so MouseOverUI stays clean — letting us re-pick and hide as the
            // cursor moves. We only ever touch the panel when it's our transient view
            // (or nothing's shown), so a pinned 'U'/'L' sheet is left alone.
            if (_gameData.Settings.Tooltips.AutoShowUnitStats)
            {
                EnsureInventoryUIsInitialized();
                bool ownsPanel = !_unitInfoPanel.IsVisible || _unitInfoPanel.IsTransient;
                // Suppress over real UI (other popups, HUD bars) or while parked on
                // the panel itself (so the user can hover stat cells for breakdowns).
                bool overUI = _input.MouseOverUI
                              || (_unitInfoPanel.IsVisible && _unitInfoPanel.ContainsMouse(mouse.X, mouse.Y));
                int hoveredUnit = -1;
                if (ownsPanel && !overUI)
                {
                    float pr = _gameData.Settings.Tooltips.HoverPickRadius;
                    float bestD2 = pr * pr;
                    for (int i = 0; i < _sim.Units.Count; i++)
                    {
                        var u = _sim.Units[i];
                        if (!u.Alive) continue;
                        // Foragers (zombie boars) get the belly tooltip, not the stat sheet.
                        if (_gameData.Units.Get(u.UnitDefID)?.Tags.Contains("forager") == true) continue;
                        float udx = u.Position.X - mouseWorld.X, udy = u.Position.Y - mouseWorld.Y;
                        float d2 = udx * udx + udy * udy;
                        if (d2 < bestD2) { bestD2 = d2; hoveredUnit = i; }
                    }
                }

                if (!ownsPanel)
                {
                    // A pinned sheet is up — leave it be.
                }
                else if (hoveredUnit >= 0)
                {
                    uint hoveredId = _sim.Units[hoveredUnit].Id;
                    if (_unitInfoPanel.UnitId != hoveredId)
                        _unitInfoPanel.ShowForUnitTransient(hoveredId);
                }
                else if (_unitInfoPanel.IsVisible && !overUI)
                {
                    // Cursor left all units (and isn't parked on the panel) — dismiss.
                    _unitInfoPanel.Hide();
                }
            }

            // --- Building placement toggle (B) ---
            if (_input.WasKeyPressed(Keys.B))
            {
                EnsureInventoryUIsInitialized();
                if (!_buildingMenuUI.IsVisible) CloseSameSidePanels(PanelSide.Left, _buildingMenuUI);
                _buildingMenuUI.Toggle(screenW, screenH);
            }

            // --- Crafting menu toggle (C) ---
            if (_input.WasKeyPressed(Keys.C))
            {
                EnsureInventoryUIsInitialized();
                if (!_craftingMenu.IsVisible) CloseSameSidePanels(PanelSide.Left, _craftingMenu);
                _craftingMenu.Toggle(screenW, screenH);
            }

            // (World mouse clicks — placement, building panels, pile gather,
            // foraging — are handled by WorldLayer in the router dispatch above;
            // the dispatch table lives in Game1.WorldClicks.cs.)

            // --- Auto-pickup foragables ---
            _foragables.TickAutoPickup(_clock.WorldDt, _gameData.Settings.General.AutoPickupForagables);

            // --- Time controls (keyboard) ---
            if (_input.WasKeyPressed(Keys.OemPlus) || _input.WasKeyPressed(Keys.Add))
                _timeScale = MathF.Min(_timeScale * 2f, 8f);
            if (_input.WasKeyPressed(Keys.OemMinus) || _input.WasKeyPressed(Keys.Subtract))
                _timeScale = MathF.Max(_timeScale * 0.5f, 0.25f);
            if (_input.WasKeyPressed(Keys.D0))
                _timeScale = 1f;

            // (Time-control and core-menu button clicks are handled by
            // TimeControlsLayer / CoreMenuButtonsLayer in the router dispatch
            // above — notably the time controls now also work while paused,
            // fixing the one-way mouse pause button.)

            // --- Tick pending projectiles ---
            // Gameplay consumes the WORLD domain (== dt inside this gate, but explicit
            // so a future move outside the gate can't silently pick up editor-live dt).
            TickPendingProjectiles(_clock.WorldDt);

            // --- Simulate ---
            _sim.Tick(_clock.WorldDt);
            // AI cast requests queued during the tick's AI pass run through the
            // shared SpellCaster + SpellEffectSystem pipeline (same as the player).
            DrainAISpellCasts();
            _workerSystem.Update(_clock.WorldDt);
            ApplyBlightBombImpacts();
            FinalizeBushWorkIfPending();
            _dayNightSystem.Update(_clock.WorldDt, _gameData);
            _sim.MagicGlyphs.Update(_clock.WorldDt, _sim.UnitsMut, _sim.Quadtree, _sim.PoisonClouds, _gameData.Spells);
            _weatherRenderer.Update(_clock.WorldDt, _gameData);
            _envSystem.UpdateForagables(_clock.WorldDt);
            UpdateZoneSpawns(_clock.WorldDt);
            _envSystem.UpdateBerryBushes(_clock.WorldDt);
            _envSystem.UpdateAnimations(_clock.WorldDt, _clock.VisualTime);
            // Trap targeting uses the canonical WorldQuery scan; the delegate is
            // cached (reads _sim at invoke time, so it survives map reloads).
            _trapTargetFinder ??= (pos, trapFaction) => _sim.Query.NearestEnemyToPoint(
                pos, World.EnvironmentSystem.TrapDetectRange, trapFaction);
            _envSystem.UpdateTraps(_clock.WorldDt, _trapTargetFinder);
            ProcessTrapFireEvents();

            // --- Scenario tick ---
            if (_activeScenario != null)
            {
                _activeScenario.OnTick(_sim, _clock.WorldDt);

                // Apply menu state requests from scenario (for editor screenshots)
                if (_activeScenario.RequestedMenuState != null)
                {
                    var requested = _activeScenario.RequestedMenuState;
                    _activeScenario.RequestedMenuState = null;
                    if (Enum.TryParse<MenuState>(requested, true, out var state))
                        _menuState = state;
                }

                // Apply camera overrides from scenario each tick
                if (_activeScenario.HasCameraOverride)
                {
                    _camera.Position = new Vec2(_activeScenario.CameraX, _activeScenario.CameraY);
                    _camera.Zoom = _activeScenario.CameraZoom;
                }

                // Apply inventory UI requests from scenario
                if (_activeScenario.RequestOpenInventory)
                {
                    _activeScenario.RequestOpenInventory = false;
                    EnsureInventoryUIsInitialized();
                    _inventoryUI.Open(screenW, screenH);
                }
                if (_activeScenario.RequestCloseInventory)
                {
                    _activeScenario.RequestCloseInventory = false;
                    _inventoryUI.Close();
                }
                if (_activeScenario.RequestOpenSkillBook)
                {
                    _activeScenario.RequestOpenSkillBook = false;
                    _skillBookOverlay.Open();
                }
                if (_activeScenario.RequestCloseSkillBook)
                {
                    _activeScenario.RequestCloseSkillBook = false;
                    _skillBookOverlay.Close();
                }

                // Apply weather preset override from scenario
                if (_activeScenario.WeatherPreset != null)
                {
                    if (_gameData.Settings.Weather.ActivePreset != _activeScenario.WeatherPreset)
                    {
                        _gameData.Settings.Weather.Enabled = true;
                        _gameData.Settings.Weather.ActivePreset = _activeScenario.WeatherPreset;
                    }
                }

                // Apply collision debug override from scenario
                if (_activeScenario.CollisionDebugOverride.HasValue)
                {
                    _collisionDebugMode = _activeScenario.CollisionDebugOverride.Value;
                    _activeScenario.CollisionDebugOverride = null;
                }

                if (_activeScenario.IsComplete)
                {
                    int result = _activeScenario.OnComplete(_sim);
                    string scenarioName = _activeScenario.Name;
                    DebugLog.Log("scenario", $"Scenario '{scenarioName}' completed with result: {(result == 0 ? "PASS" : "FAIL")} (code={result})");
                    Console.Error.WriteLine(result == 0 ? $"SCENARIO PASS: {scenarioName}" : $"SCENARIO FAIL: {scenarioName} (code={result})");
                    _activeScenario = null;

                    // Auto-exit if launched from command line
                    if (LaunchArgs.Headless)
                    {
                        Environment.ExitCode = result;
                        Exit();
                        return;
                    }

                    _menuState = MenuState.MainMenu;
                    _gameWorldLoaded = false;
                    _prevKb = kb;
                    _prevMouse = mouse;
                    base.Update(gameTime);
                    return;
                }
            }

            // Collect damage events for floating numbers (height owned by the sim event)
            foreach (var dmg in _sim.DamageEvents)
                FloatingText.AddDamage(_damageNumbers, dmg.Position, dmg.Damage,
                    dmg.Height, dmg.IsPoison, dmg.IsFatigue);
        }

        // --- Scenario tick when editor is active (editors pause normal sim but scenarios must still tick) ---
        if (editorActive && _activeScenario != null)
        {
            _activeScenario.OnTick(_sim, 1f / 60f);

            if (_activeScenario.RequestedMenuState != null)
            {
                var requested = _activeScenario.RequestedMenuState;
                _activeScenario.RequestedMenuState = null;
                if (Enum.TryParse<MenuState>(requested, true, out var state))
                    _menuState = state;
            }

            if (_activeScenario.RequestSelectFirst)
            {
                _activeScenario.RequestSelectFirst = false;
                if (_menuState == MenuState.UnitEditor) _unitEditor.SelectFirst();
                else if (_menuState == MenuState.SpellEditor) _spellEditor.SelectFirst();
                else if (_menuState == MenuState.UIEditor) _uiEditor.SelectedIndex = 0;
            }

            // Apply spell select-by-name from scenario
            if (_activeScenario.RequestSelectSpellByName != null)
            {
                var name = _activeScenario.RequestSelectSpellByName;
                _activeScenario.RequestSelectSpellByName = null;
                if (_menuState == MenuState.SpellEditor)
                    _spellEditor.SelectByName(name);
            }

            // Apply map editor tab switch from scenario
            if (_activeScenario.RequestedMapTab != null)
            {
                var tabName = _activeScenario.RequestedMapTab;
                _activeScenario.RequestedMapTab = null;
                if (_menuState == MenuState.MapEditor && Enum.TryParse<MapEditorTab>(tabName, true, out var mapTab))
                    _mapEditor.ActiveTab = mapTab;
            }

            // Apply UI editor tab switch from scenario
            if (_activeScenario.RequestedUITab != null)
            {
                var tabName = _activeScenario.RequestedUITab;
                _activeScenario.RequestedUITab = null;
                if (_menuState == MenuState.UIEditor && Enum.TryParse<UIEditorTab>(tabName, true, out var uiTab))
                    _uiEditor.ActiveTab = uiTab;
            }

            // Select a specific UI-editor widget by id from scenario (Widgets tab)
            if (_activeScenario.RequestSelectUIWidgetById != null)
            {
                var wid = _activeScenario.RequestSelectUIWidgetById;
                _activeScenario.RequestSelectUIWidgetById = null;
                if (_menuState == MenuState.UIEditor) _uiEditor.SelectWidgetById(wid);
            }

            // Inventory UI requests from scenario
            if (_activeScenario.RequestOpenInventory)
            {
                _activeScenario.RequestOpenInventory = false;
                EnsureInventoryUIsInitialized();
                _inventoryUI.Open(screenW, screenH);
            }
            if (_activeScenario.RequestCloseInventory)
            {
                _activeScenario.RequestCloseInventory = false;
                _inventoryUI.Close();
            }

            // Open weapon sub-editor popup
            if (_activeScenario.RequestOpenWeaponSub)
            {
                _activeScenario.RequestOpenWeaponSub = false;
                if (_menuState == MenuState.UnitEditor)
                    _unitEditor.OpenWeaponSubEditor();
            }

            // Open buff manager popup
            if (_activeScenario.RequestOpenBuffManager)
            {
                _activeScenario.RequestOpenBuffManager = false;
                if (_menuState == MenuState.SpellEditor)
                    _spellEditor.OpenBuffManager();
            }

            if (_activeScenario.IsComplete)
            {
                int result = _activeScenario.OnComplete(_sim);
                string scenarioName = _activeScenario.Name;
                DebugLog.Log("scenario", $"Scenario '{scenarioName}' completed with result: {(result == 0 ? "PASS" : "FAIL")} (code={result})");
                Console.Error.WriteLine(result == 0 ? $"SCENARIO PASS: {scenarioName}" : $"SCENARIO FAIL: {scenarioName} (code={result})");
                _activeScenario = null;
                if (LaunchArgs.Headless)
                {
                    Environment.ExitCode = result;
                    Exit();
                    return;
                }
                _menuState = MenuState.MainMenu;
                _gameWorldLoaded = false;
                _prevKb = kb;
                _prevMouse = mouse;
                base.Update(gameTime);
                return;
            }
        }

        // --- Update animations (scaled by timeScale so they match game speed) ---
        // WORLD domain: UpdateAnimations isn't just visual — it drives the corpse-
        // interaction state machine, resolves pending attacks / spell casts on anim
        // action-frames, ticks jump/incap and death-fog corruption. Editors and pause
        // must freeze all of it (the map-editor corruption-spread bug lived here).
        UpdateAnimations(_clock.WorldDt);

        // --- Update damage numbers ---
        for (int i = _damageNumbers.Count - 1; i >= 0; i--)
        {
            var dn = _damageNumbers[i];
            dn.Timer += dt;
            dn.Height += _gameData.Settings.General.DamageNumberSpeed * dt;
            if (dn.Timer > _gameData.Settings.General.DamageNumberFadeTime)
                _damageNumbers.RemoveAt(i);
            else
                _damageNumbers[i] = dn;
        }

        // --- Game over check ---
        if (_gameOver)
        {
            if (_input.WasKeyPressed(Keys.R))
            {
                _gameOver = false;
                StartGame();
            }
        }
        else if (FindNecromancer() < 0 && !_paused && _gameWorldLoaded && _activeScenario == null)
        {
            _gameOver = true;
        }

        // (Panel per-frame updates — inventory, building, crafting, grimoire,
        // table bench, grave roster, job board, skill book — run inside the
        // router dispatch above, each at its own z-position with press edges
        // masked when a higher layer claimed the click. Slot-sync consequences
        // of THIS frame's sim tick therefore show one frame later, which is
        // imperceptible.)

        // Cursor swap: hand when hovering interactive UI, arrow otherwise
        bool overInteractiveUI = _input.MouseOverUI || _editorUi.IsMouseOverUI;
        Mouse.SetCursor(overInteractiveUI ? MouseCursor.Hand : MouseCursor.Arrow);

        _prevKb = kb;
        _prevMouse = mouse;
        base.Update(gameTime);
    }

    /// <summary>Cached trap-target query passed to EnvironmentSystem.UpdateTraps
    /// (see the call site in Update — avoids a per-frame closure allocation).</summary>
    private Func<Vec2, Faction, int>? _trapTargetFinder;

    /// <summary>Process trap fire events from EnvironmentSystem.UpdateTraps — cast spells and apply damage.</summary>
    private void ProcessTrapFireEvents()
    {
        foreach (var evt in _envSystem.TrapFireEvents)
        {
            var spell = _gameData.Spells.Get(evt.SpellId);
            if (spell == null) continue;

            // Set proper cooldown from spell def
            _envSystem.SetTrapCooldown(evt.ObjectIndex, spell.Cooldown);

            var targetPos = _sim.Units[evt.TargetUnitIdx].Position;

            if (spell.Category == "Strike")
            {
                // Shared strike executor — traps go through the same pipeline as
                // player/AI casts (MR penetration gate, damage number, god-ray
                // params + target filter on ground strikes). casterIdx -1 =
                // casterless source: base spell penetration, no killer credit.
                // Faction mirrors EnvironmentSystem.FindTrapTarget's owner rule.
                var trapFaction = evt.TrapOwner == 0 ? Faction.Undead : Faction.Human;
                GameSystems.SpellEffectSystem.ExecuteStrikeFrom(spell, _sim, _gameData,
                    casterIdx: -1, ownerUid: GameConstants.InvalidUnit,
                    origin: evt.TrapPos, originHeight: 0.3f,
                    target: targetPos, sourceFaction: trapFaction,
                    damageNumbers: _damageNumbers);
            }
            else if (spell.Category == "Cloud")
            {
                // Spawn cloud at the trap position (not the target — the trap itself is
                // where the burst originates). Same code path as a caster casting the
                // spell; traps are player-built, so the cloud is Undead-owned.
                GameSystems.SpellEffectSystem.ExecuteCloud(spell, _sim, evt.TrapPos, Faction.Undead);
            }
        }
    }

    private static void LoadSpellBarSlots(System.Text.Json.JsonElement root, string key, SpellBarState bar)
    {
        if (!root.TryGetProperty(key, out var arr)) return;
        int si = 0;
        foreach (var slot in arr.EnumerateArray())
        {
            if (si >= bar.Slots.Length) break;
            bar.Slots[si] = new SpellBarSlot { SpellID = slot.GetProperty("spellID").GetString() ?? "" };
            si++;
        }
        DebugLog.Log("startup", $"SpellBar '{key}' loaded: {string.Join(", ", bar.Slots.Select(s => s.SpellID))}");
    }

    /// <summary>One-time migration of the old two-bar spellbar.json shape
    /// ({"slots":[4], "secondary":[6]}) onto the single 10-slot bar. Q/E keep
    /// their spells (slots 0-1), the old number keys 1-6 keep theirs (slots
    /// 2-7), and the old LMB/RMB spells land in the first empty slots
    /// (normally 8-9 = keys "7"/"8"; dropped only if the bar is full). The
    /// next SaveSpellBars persists the new single-list shape.</summary>
    private static void MigrateOldSpellBarJson(System.Text.Json.JsonElement root, SpellBarState bar)
    {
        static List<string> ReadIds(System.Text.Json.JsonElement root, string key)
        {
            var ids = new List<string>();
            if (!root.TryGetProperty(key, out var arr)) return ids;
            foreach (var slot in arr.EnumerateArray())
                ids.Add(slot.TryGetProperty("spellID", out var id) ? id.GetString() ?? "" : "");
            return ids;
        }

        var oldPrimary = ReadIds(root, "slots");       // Q, E, LMB, RMB
        var oldSecondary = ReadIds(root, "secondary"); // number keys 1-6

        for (int i = 0; i < 2 && i < oldPrimary.Count; i++)
            bar.Slots[i].SpellID = oldPrimary[i];
        for (int i = 0; i < 6 && i < oldSecondary.Count; i++)
            bar.Slots[2 + i].SpellID = oldSecondary[i];

        // Ex-mouse-button spells: first empty slot, or dropped when full.
        for (int i = 2; i < 4 && i < oldPrimary.Count; i++)
        {
            if (string.IsNullOrEmpty(oldPrimary[i])) continue;
            int empty = Array.FindIndex(bar.Slots, s => string.IsNullOrEmpty(s.SpellID));
            if (empty >= 0) bar.Slots[empty].SpellID = oldPrimary[i];
            else DebugLog.Log("startup", $"spellbar migration: bar full, dropped '{oldPrimary[i]}'");
        }
        DebugLog.Log("startup",
            $"SpellBar migrated from two-bar format: {string.Join(", ", bar.Slots.Select(s => s.SpellID))}");
    }

    /// <summary>Persist the current spell bar slot assignments to
    /// spellbar.json. Called whenever the player edits a slot, and on game
    /// exit. Atomic write — partial writes can't corrupt the file. No-op when
    /// the bar was seeded for a test map/scenario — those runs must not stomp
    /// the player's per-machine loadout.</summary>
    private void SaveSpellBars()
    {
        if (_spellBarSeededForTest)
        {
            DebugLog.Log("startup", "SaveSpellBars skipped: bar was test-seeded");
            return;
        }
        try
        {
            var doc = new Dictionary<string, object>
            {
                ["slots"] = _spellBarState.Slots.Select(s => new Dictionary<string, string>
                {
                    ["spellID"] = s.SpellID ?? ""
                }).Cast<object>().ToList(),
            };
            var options = Necroking.Core.JsonDefaults.Indented;
            string json = System.Text.Json.JsonSerializer.Serialize(doc, options);
            System.IO.Directory.CreateDirectory(GamePaths.Resolve(GamePaths.UserSettingsDir));
            Core.AtomicFile.WriteAllText(GamePaths.Resolve(GamePaths.UserSpellBarJson), json);
        }
        catch (Exception ex) { DebugLog.Log("error", $"SaveSpellBars failed: {ex.Message}"); }
    }

    /// <summary>
    /// Melee & Gather: try to melee attack an enemy near mouse, otherwise try to collect a foragable.
    /// </summary>
    private void TryMeleeOrGather(int necroIdx, Vec2 mouseWorld)
    {
        // Melee first (shared cursor-melee order — see TryOrderMeleeAtCursor in
        // Game1.WorldClicks.cs), gated by this ability's own cooldown check.
        if (_sim.Units[necroIdx].AttackCooldown <= 0f && _sim.Units[necroIdx].PostAttackTimer <= 0f
            && TryOrderMeleeAtCursor(necroIdx, mouseWorld,
                _gameData.Settings.Tooltips.HoverPickRadius) == CursorMeleeResult.Ordered)
            return;

        // No melee target — try foragable collection
        int bestForage = FindNearestForagable(_sim.Units[necroIdx].Position, 2f);
        if (bestForage >= 0)
            StartForagableCollection(bestForage);
    }

    /// <summary>Pick a berry bush in Berries state nearest to <paramref name="worldPos"/>.
    /// Returns -1 if no eligible bush is within <paramref name="maxRadius"/>.</summary>
    private int FindBerryBushNear(Vec2 worldPos, float maxRadius)
        => _sim.Query.NearestEnvObject(worldPos, maxRadius, new EnvBerryBushes());

    /// <summary>Post-AI hook. Fires the moment WorkLoop completes (signalled
    /// by Subroutine == BushSub_AwaitFinalize) — applies the bush poison and
    /// consumes one of the matching potion immediately, then hands the routine
    /// back to WorkEnd so the standup animation plays out. The BushWork*
    /// fields stay populated through standup; they're cleared by the AI
    /// handler's CancelBushWork on Phase.Done.</summary>
    private void FinalizeBushWorkIfPending()
    {
        int necroIdx = _sim.NecromancerIndex;
        if (necroIdx < 0) return;
        if (_sim.Units[necroIdx].Routine != AI.PlayerControlledHandler.RoutineWorkOnBush) return;
        if (_sim.Units[necroIdx].Subroutine != AI.PlayerControlledHandler.BushSub_AwaitFinalize) return;

        int bushIdx = _sim.Units[necroIdx].BushWorkObjIdx;
        string buffID = _sim.Units[necroIdx].BushWorkBuffID;
        string itemID = _sim.Units[necroIdx].BushWorkItemID;

        bool applied = bushIdx >= 0 && _envSystem.PoisonBerryBush(bushIdx, buffID);
        if (applied && !string.IsNullOrEmpty(itemID))
            _inventory.RemoveItem(itemID, 1);

        // Continue to WorkEnd so the standup plays normally.
        var u = _sim.UnitsMut[necroIdx];
        u.Subroutine = AI.WorkRoutine.WorkEnd;
    }

    private void CheatAddAllSkillcounters(int necroIdx, int amount) 
    {
       if (necroIdx < 0 || _gameData == null) return;

       foreach (var et in SkillBookState.EVENT_TYPES) {
          _skillBookState.Events.Tally(et, amount);
       }

       foreach (var sp in SkillBookState.SKILL_POINT_TYPES) {
          _skillBookState.AddSkillPoints(sp, amount);
       }

       // Potions needed for metamorphosis.
       _inventory.AddItem("potion_death_evolution", amount);
    }

    /// <summary>Floating "Horde Full" text above the necromancer when a summon
    /// is refused for hitting the cap. Reuses the DamageNumber PickupText
    /// channel so the existing renderer + fade-out apply unchanged.</summary>
    /// <summary>Shift+P cheat. Applies / removes buff_god_mode on the
    /// necromancer. On apply, also tops mana up to the new effective cap so
    /// the +999 mana effect is felt immediately rather than slowly filling
    /// at the buffed regen rate.</summary>
    private void ToggleGodMode(int necroIdx)
    {
        if (necroIdx < 0 || _gameData == null) return;
        const string godBuffId = "buff_god_mode";
        if (BuffSystem.HasBuff(_sim.Units, necroIdx, godBuffId))
        {
            BuffSystem.RemoveBuff(_sim.UnitsMut, necroIdx, godBuffId);
            // Clamp mana back down to the base cap since the +999 just went
            // away — without this, Mana would stay at e.g. 1049 against a 50
            // MaxMana and read weirdly in the HUD.
            if (_sim.NecroState.Mana > _sim.NecroState.MaxMana)
                _sim.NecroState.Mana = _sim.NecroState.MaxMana;
            return;
        }
        var def = _gameData.Buffs.Get(godBuffId);
        if (def == null)
        {
            DebugLog.Log("ai", "[GodMode] buff_god_mode missing from buffs.json — toggle ignored.");
            return;
        }
        BuffSystem.ApplyBuff(_sim.UnitsMut, necroIdx, def);
        // Instant top-up to the buffed cap.
        float maxManaEff = _sim.NecroState.MaxMana
            + BuffSystem.SumExtraAdd(_sim.Units, necroIdx, "MaxMana");
        _sim.NecroState.Mana = maxManaEff;
    }

    /// <summary>Floating alert text above the necromancer — the shared channel for
    /// every cast-failure reason ("Too Far", "Horde Full", "Out of Range", "Not
    /// Enough Mana", "Need Death 1", …). Renders red via the DamageNumber alert
    /// path, starting at the unit's HEAD.</summary>
    private void SpawnCastFailText(int necroIdx, string message)
    {
        if (necroIdx < 0 || necroIdx >= _sim.Units.Count) return;
        if (string.IsNullOrEmpty(message)) return;

        // Head anchor via the canonical formula (FloatingText.HeadHeight owns
        // the YRatio conversion — see its doc for the Height trap).
        var unit = _sim.Units[necroIdx];
        var udef = _gameData.Units.Get(unit.UnitDefID);
        FloatingText.AddText(_damageNumbers, unit.Position, message,
            FloatingText.HeadHeight(unit, udef, _camera.YRatio), alert: true);
    }

    private void SpawnHordeCapText(int necroIdx) => SpawnCastFailText(necroIdx, "Horde Full");

    /// <summary>Floating feedback when a cast fails because the necromancer lacks the
    /// spell's magic-path requirement — names the path(s) it needs (e.g.
    /// "Need Death 1") so it's never mistaken for a mana shortfall.</summary>
    private void SpawnMissingPathText(int necroIdx, string spellId)
    {
        string need = GameSystems.SpellCaster.DescribeMissingPath(
            _gameData.Spells.Get(spellId), _gameData, _sim.Units, necroIdx);
        SpawnCastFailText(necroIdx, string.IsNullOrEmpty(need) ? "Path Locked" : $"Need {need}");
    }

    internal Texture2D? GetItemTextureByPath(string path)
        => _itemTextureCache.GetOrLoad(GraphicsDevice, path);

    // Enemy = different faction from the necromancer (undead).
    private int FindClosestEnemyToPoint(Vec2 worldPos, float maxRange)
        => _sim.Query.NearestEnemyToPoint(worldPos, maxRange, Faction.Undead);

    internal int FindNecromancer() => _sim.Units.FindAliveNecromancerIndex();

    /// <summary>The persistent gameplay HUD is on screen — during normal play
    /// and on top of the (full-screen) map editor. Mirrors the showUI gate in
    /// DrawHudBlock; the single visibility source for the HUD's router layers
    /// and RebuildUIHitRects.</summary>
    internal bool HudVisible => _gameWorldLoaded
        && (_menuState == MenuState.None || _menuState == MenuState.MapEditor)
        && (_activeScenario == null || _activeScenario.WantsUI);

    /// <summary>The DrawHudBlock "showUI" gate: scenario wants UI and no no-UI
    /// dev screenshot is in flight. Router layers use it as their draw gate.</summary>
    internal bool ShowUIForDraw => (_activeScenario == null || _activeScenario.WantsUI) && !_devShotNoUi;

    /// <summary>A text-input field currently owns the keyboard (editor UI or
    /// UI-editor field) — hotkey-ish handlers must stand down.</summary>
    internal bool AnyTextInputActive => (_editorUi != null && _editorUi.IsTextInputActive)
        || (_menuState == MenuState.UIEditor && _uiEditor.IsTextInputActive);

    /// <summary>Toggle an editor window by HUDRenderer.Editor* index — the
    /// click-side mirror of F9-F12, shared by the editor-launcher layer.</summary>
    internal void ToggleEditorWindow(int idx)
    {
        // The click that toggles an editor must not also fire a widget that
        // appears (or disappears) under the cursor this frame — kill the
        // in-flight press gesture before switching modes. (The Update-loop
        // menu-state-transition check covers this one frame later; this call
        // covers the toggle frame's own Draw.)
        _input.ConsumeGesture();
        switch (idx)
        {
            case HUDRenderer.EditorUnit:
                _menuState = _menuState == MenuState.UnitEditor ? MenuState.None : MenuState.UnitEditor;
                _editorUi.ClearActiveField();
                break;
            case HUDRenderer.EditorSpell:
                _menuState = _menuState == MenuState.SpellEditor ? MenuState.None : MenuState.SpellEditor;
                _editorUi.ClearActiveField();
                break;
            case HUDRenderer.EditorMap:
                if (_menuState == MenuState.MapEditor) _menuState = MenuState.None;
                else { _menuState = MenuState.MapEditor; _mapEditor.SuppressClicksUntilRelease(); }
                break;
            case HUDRenderer.EditorUi:
                if (_menuState == MenuState.UIEditor) _menuState = MenuState.None;
                else { EnsureUIEditorInitialized(); _menuState = MenuState.UIEditor; }
                break;
        }
    }

    /// <summary>Clicking a spell-bar slot opens the grimoire in assign mode for
    /// that slot; picking a spell writes it to the bar and saves.</summary>
    internal void OpenSpellAssignForSlot(int slot)
    {
        EnsureInventoryUIsInitialized();
        _grimoireOverlay.OpenForAssign(id =>
        {
            _spellBarState.Slots[slot].SpellID = id;
            SaveSpellBars();
        });
    }

    // "CastingEffect Green" buff — visual glow shown on the necromancer while it
    // channels a reanimation at the necro table.
    private const string TableChannelBuffId = "buff_4_copy";

    // (ReconcileTopLevelEditorLayers is gone: the editors/menus sit in the
    // router as EditorHostLayer/MenuHostLayer with LIVE Visible getters over
    // _menuState — no per-frame stack syncing needed.)

    // ── Raw-corpse carry (body bag mothballed; see GameConstants.UseBodyBag) ──

    // --- Persistent frame-centroid cache (cache/frame_centroids.json) ---
    internal Dictionary<string, Vector2>? _persistedCentroids;
    internal bool _centroidsDirty;
    internal bool _bulkCentroidBake;
    internal Dictionary<Microsoft.Xna.Framework.Graphics.Texture2D, int>? _texToAtlasIdx;

    // ═══════════════════════════════════════
    //  Gameplay Debug Visualizations (F7)
    // ═══════════════════════════════════════

    // Sortable item for merged unit+object depth sorting
    internal readonly List<DepthItem> _depthItems = new(256); // reused each frame

    internal enum DepthItemType : byte { Unit, EnvObject, CloudPuff, GrassTuft, DeathFogPuff, ReanimDust }

    internal struct DepthItem : IComparable<DepthItem>
    {
        public float Y;
        public DepthItemType Type;
        public int Index;       // Unit index, env object index, or cloud index
        public int SubIndex;    // For cloud puffs: puff index within the cloud
        // Sort by Y first, then by (Type, Index, SubIndex) as tiebreaker so items
        // with equal Y always order deterministically. .NET's List<T>.Sort is
        // introsort — unstable — so without a tiebreaker, tufts with Y equal to
        // a bush's Y would flicker in front of / behind it as the camera pans.
        public int CompareTo(DepthItem other)
        {
            int c = Y.CompareTo(other.Y);
            if (c != 0) return c;
            c = ((byte)Type).CompareTo((byte)other.Type);
            if (c != 0) return c;
            c = Index.CompareTo(other.Index);
            if (c != 0) return c;
            return SubIndex.CompareTo(other.SubIndex);
        }
    }

    internal readonly Dictionary<int, float> _dissolveLoggedSeeds = new();

    // (_outlineDirs, _ghostColor1/2, AggroNames/AggroDescs moved to GameRenderer.)

    /// <summary>The entire draw pipeline lives in <see cref="GameRenderer"/> (the
    /// former Game1.Render*.cs partials). MonoGame invokes this override, which
    /// forwards into it. Keep this thin — no draw logic here.</summary>
    protected override void Draw(GameTime gameTime) => _gameRenderer.Draw(gameTime);

    /// <summary>Lets <see cref="GameRenderer"/> invoke the MonoGame base Game.Draw
    /// (component drawing) it used to reach via <c>base.Draw</c> when it lived on Game1.</summary>
    internal void BaseDraw(GameTime gameTime) => base.Draw(gameTime);

    /// <summary>The GPU swap/Present happens here, after <see cref="Draw"/> returns —
    /// NOT inside <see cref="BaseDraw"/> (profiling showed the old BaseDraw stopwatch
    /// reads ~0 while the thread spends 25%+ of the frame blocked in Present). This is
    /// the real "GPU + swap wait" number for the F3 readout and the `perf` dev command.</summary>
    protected override void EndDraw()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        base.EndDraw();
        sw.Stop();
        double presentMs = sw.Elapsed.TotalMilliseconds;
        _gpuPresentMsAvg = _gpuPresentMsAvg * 0.9 + presentMs * 0.1;
        if (_perfFrames > 0)
            _perfPresentMs[(int)((_perfFrames - 1) % _perfPresentMs.Length)] = presentMs;
    }

    protected override void UnloadContent()
    {
        _widgetRenderer.Shutdown();
        // _pixel / _glowTex are shared TextureUtil caches (GetWhitePixel / GetRadialGlow)
        // — do NOT dispose them here.
        _mainMenuBg?.Dispose();
        _groundVertexMapTex?.Dispose();
        foreach (var atlas in _atlases)
            atlas?.Texture?.Dispose();
        _envSystem.OnCollisionsDirty = null;
        _envSystem.OnCollisionRegionDirty = null;
        base.UnloadContent();
    }
}
