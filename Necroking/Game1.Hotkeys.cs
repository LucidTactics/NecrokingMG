using System;
using Microsoft.Xna.Framework.Input;
using Necroking.Core;
using Necroking.GameSystems;
using Necroking.Lib;
using Necroking.Render;

namespace Necroking;

// The game's hotkey table — every non-editor keyboard shortcut, registered once into
// HotkeySystem from LoadContent. Delegates capture `this` and read live state at press
// time. Editor-window keys (EditorBase widgets, MapEditorWindow) are still hand-rolled —
// see todos/migrate-editor-hotkeys.md.
public partial class Game1
{
    private void RegisterHotkeys()
    {
        // ---------- Global (works on the main menu / scenario list too) ----------
        HotkeySystem.Register(HotkeyContext.Global, Keys.Enter, ToggleWindowMode,
            alt: ModReq.Down, name: "Toggle fullscreen");

        // ---------- Session-wide debug + editor toggles (work inside editors) ----------
        HotkeySystem.Register(HotkeyContext.Session, Keys.F2,
            () => _waterDebug = !_waterDebug, name: "Water debug");
        HotkeySystem.Register(HotkeyContext.Session, Keys.F3,
            () => _showPerfReadout = !_showPerfReadout, name: "Perf/zoom readout");
        // F5 death fog (F9 was requested but is taken by the unit editor toggle; F5 was free).
        HotkeySystem.Register(HotkeyContext.Session, Keys.F5,
            _deathFog.ToggleDebug, name: "Death-fog debug");
        HotkeySystem.Register(HotkeyContext.Session, Keys.F6,
            () => _windDebug = !_windDebug, name: "Wind debug");
        // Off → Horde → Unit Info → Off.
        HotkeySystem.Register(HotkeyContext.Session, Keys.F7,
            () => _gameplayDebugMode = (_gameplayDebugMode + 1) % 3, name: "Gameplay debug");
        HotkeySystem.Register(HotkeyContext.Session, Keys.F8,
            () => _collisionDebugMode = (CollisionDebugMode)(((int)_collisionDebugMode + 1) % (int)CollisionDebugMode.Count),
            name: "Collision debug");
        HotkeySystem.Register(HotkeyContext.Session, Keys.F9, () =>
        {
            _menuState = _menuState == MenuState.UnitEditor ? MenuState.None : MenuState.UnitEditor;
            _editorUi.ClearActiveField();
        }, name: "Unit editor");
        HotkeySystem.Register(HotkeyContext.Session, Keys.F10, () =>
        {
            _menuState = _menuState == MenuState.SpellEditor ? MenuState.None : MenuState.SpellEditor;
            _editorUi.ClearActiveField();
        }, name: "Spell editor");
        HotkeySystem.Register(HotkeyContext.Session, Keys.F11,
            () => _menuState = _menuState == MenuState.MapEditor ? MenuState.None : MenuState.MapEditor,
            name: "Map editor");
        HotkeySystem.Register(HotkeyContext.Session, Keys.F12, () =>
        {
            if (_menuState == MenuState.UIEditor) _menuState = MenuState.None;
            else { EnsureUIEditorInitialized(); _menuState = MenuState.UIEditor; }
        }, name: "UI editor");
        HotkeySystem.Register(HotkeyContext.Session, Keys.OemTilde, () =>
        {
            // Open only, never toggle-closed — we might want to see it while holding tilde.
            if (_logPanel.IsVisible) return;
            CloseSameSidePanels(PanelSide.Left, _logPanel);
            _logPanel.Toggle(GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height);
        }, name: "Log panel");
        HotkeySystem.Register(HotkeyContext.Session, Keys.OemQuestion,
            () => _debugPanel.Toggle(), name: "Debug panel");

        // ---------- HUD panel toggles (MenuState.None; also work while paused) ----------
        HotkeySystem.Register(HotkeyContext.Hud, Keys.I, () =>
        {
            EnsureInventoryUIsInitialized();
            _inventoryUI.Toggle(GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height);
        }, name: "Inventory");
        // wOrk/jObs. Shift=Up: Shift+O is the skill-point cheat (Gameplay context below).
        HotkeySystem.Register(HotkeyContext.Hud, Keys.O, () =>
        {
            EnsureInventoryUIsInitialized();
            if (!_jobBoardUI.IsVisible) CloseSameSidePanels(PanelSide.Left, _jobBoardUI);
            _jobBoardUI.Toggle(GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height);
        }, shift: ModReq.Up, name: "Job board");
        HotkeySystem.Register(HotkeyContext.Hud, Keys.Tab, () =>
        {
            if (!_characterStatsUI.IsVisible) CloseSameSidePanels(PanelSide.Left, _characterStatsUI);
            _characterStatsUI.Toggle();
        }, name: "Character stats");
        HotkeySystem.Register(HotkeyContext.Hud, Keys.K,
            () => _skillBookOverlay.Toggle(), name: "Skill book");
        HotkeySystem.Register(HotkeyContext.Hud, Keys.J, () =>
        {
            EnsureInventoryUIsInitialized();
            if (!_grimoireOverlay.IsVisible) CloseSameSidePanels(PanelSide.Left, _grimoireOverlay);
            _grimoireOverlay.Toggle();
        }, name: "Grimoire");
        HotkeySystem.Register(HotkeyContext.Hud, Keys.U, () =>
        {
            EnsureInventoryUIsInitialized();
            if (_unitInfoPanel.IsVisible)
                _unitInfoPanel.Hide();
            else if (_sim.NecromancerIndex >= 0)
            {
                CloseSameSidePanels(PanelSide.Right, _unitInfoPanel);
                _unitInfoPanel.ShowForUnit(_sim.Units[_sim.NecromancerIndex].Id);
            }
        }, name: "Character sheet");
        // Depth-sorted reanimation fog A/B dev switch (Performance.DepthSortedFog).
        HotkeySystem.Register(HotkeyContext.Hud, Keys.H, () =>
        {
            _gameData.Settings.Performance.DepthSortedFog = !_gameData.Settings.Performance.DepthSortedFog;
            _depthFogToastTimer = 2.25f; // flash the new state on screen
        }, name: "Depth-sorted fog");
        // Press-to-inspect; disabled when auto-show-on-hover owns the panel instead.
        HotkeySystem.Register(HotkeyContext.Hud, Keys.L, InspectUnitUnderCursorHotkey,
            when: () => !_gameData.Settings.Tooltips.AutoShowUnitStats, name: "Inspect unit");

        // ---------- Post-UI: keys that must respect UI-layer ConsumeKey claims ----------
        // ESC is layered: popups/panels claim it during the router dispatch, an armed
        // circle-spell aim cancels next, and only then does bare ESC open the pause menu.
        HotkeySystem.Register(HotkeyContext.Session, Keys.Escape,
            () => _aimingSlot = -1, when: () => _aimingSlot >= 0,
            name: "Cancel spell aim", phase: HotkeyPhase.PostUI);
        HotkeySystem.Register(HotkeyContext.Hud, Keys.Escape, () =>
        {
            _menuState = MenuState.PauseMenu;
            _clock.Pause(GameClock.PauseSource.User);
        }, name: "Pause menu", phase: HotkeyPhase.PostUI);
        HotkeySystem.Register(HotkeyContext.GameOver, Keys.R, () =>
        {
            _gameOver = false;
            StartGame();
        }, name: "Restart after game over");

        // ---------- Gameplay (world running) ----------
        // Aggression bar: Shift+E raises, Shift+Q lowers. The plain Q/E cast bindings
        // below set Shift=Up, so the shifted press never also casts slots 0/1.
        HotkeySystem.Register(HotkeyContext.Gameplay, Keys.E,
            () => _sim.Horde.AggressionLevel++, shift: ModReq.Down, name: "Aggression up");
        HotkeySystem.Register(HotkeyContext.Gameplay, Keys.Q,
            () => _sim.Horde.AggressionLevel--, shift: ModReq.Down, name: "Aggression down");

        // Spell casting (keyboard-only; SpellBarBindings is the slot→key table — mouse
        // buttons never cast, they belong to Game1.WorldClicks.cs). Number-row slots
        // ignore modifiers entirely (Shift+1 still casts slot 2), matching the old loop.
        for (int i = 0; i < SpellBarBindings.SlotCount; i++)
        {
            int slot = i;
            if (slot == 0)
                HotkeySystem.Register(HotkeyContext.Gameplay, SpellBarBindings.SlotKeys[0], () =>
                {
                    // Q while spirit-walking roots the spirit as a scrying eye and wakes
                    // the body, instead of casting slot 0.
                    if (SpiritWalkSystem.Active) SpiritWalkSystem.RootSpirit(this);
                    else CastSlotHotkey(0);
                }, shift: ModReq.Up, name: "Cast slot Q / root spirit");
            else
                HotkeySystem.Register(HotkeyContext.Gameplay, SpellBarBindings.SlotKeys[slot],
                    () => CastSlotHotkey(slot), shift: slot == 1 ? ModReq.Up : ModReq.Ignore,
                    name: $"Cast slot {SpellBarBindings.SlotLabels[slot]}");
        }

        // Space = Nightfall rogue momentum jump (NightfallPorts/RogueJump.cs): leap in
        // the current movement direction scaled by speed — not cursor-targeted.
        HotkeySystem.Register(HotkeyContext.Gameplay, Keys.Space, () =>
        {
            int necroIdx = FindNecromancer();
            if (necroIdx < 0) return;
            var mu = _sim.UnitsMut;
            bool canJump = mu[necroIdx].Alive && mu[necroIdx].JumpPhase == 0
                && !NightfallPorts.RogueJump.IsJumping(mu[necroIdx].Id)
                && !mu[necroIdx].Incap.IsLocked && !_pendingSpell.Active;
            if (canJump)
                // slideThrough: keep the run momentum gliding through the squat +
                // landing anims and don't stop dead on landing.
                NightfallPorts.RogueJump.BeginMomentumJump(mu, necroIdx, slideThrough: true);
        }, name: "Jump");

        HotkeySystem.Register(HotkeyContext.Gameplay, Keys.G, () =>
        {
            int necroIdx = FindNecromancer();
            if (necroIdx < 0) return;
            _sim.UnitsMut[necroIdx].GhostMode = !_sim.Units[necroIdx].GhostMode;
            ToggleGodMode(necroIdx, force_to_value: _sim.UnitsMut[necroIdx].GhostMode);
            // Also flip the top-left debug readout (projectile counts etc).
            _hudRenderer.ShowDebugPanel = _sim.UnitsMut[necroIdx].GhostMode;
        }, name: "Ghost mode");
        // God-mode cheat: applies/removes buff_god_mode on the necromancer; the buff's
        // effects come from data/buffs.json (Duration=0 = permanent until removed).
        HotkeySystem.Register(HotkeyContext.Gameplay, Keys.P, () =>
        {
            int necroIdx = FindNecromancer();
            if (necroIdx >= 0) ToggleGodMode(necroIdx);
        }, shift: ModReq.Down, name: "God mode");
        // Cheat / debug to test the skill tree.
        HotkeySystem.Register(HotkeyContext.Gameplay, Keys.O, () =>
        {
            int necroIdx = FindNecromancer();
            if (necroIdx >= 0) CheatAddAllSkillcounters(necroIdx, 10);
        }, shift: ModReq.Down, name: "Add skill points");

        // Corpse / world interaction — see Game1.WorldClicks.cs.
        HotkeySystem.Register(HotkeyContext.Gameplay, Keys.F,
            () => HandleInteractKey(FindNecromancer(), MouseWorld()), name: "Interact");

        // Tethers: Shift+T marks the unit/corpse under the cursor as the anchor; Shift+R
        // then connects it to whatever's under the cursor (any unit↔corpse combo). With
        // no anchor set, Shift+R detaches a tethered thing under the cursor, or quick-
        // drags the nearest free corpse with the necromancer.
        HotkeySystem.Register(HotkeyContext.Gameplay, Keys.T,
            () => _tetherAnchor = TryPickTetherEnd(MouseWorld(), out var anchor) ? anchor : (TetherEnd?)null,
            shift: ModReq.Down, name: "Tether anchor");
        HotkeySystem.Register(HotkeyContext.Gameplay, Keys.R,
            () => HandleRopeKey(MouseWorld(), FindNecromancer()),
            shift: ModReq.Down, name: "Tether attach/detach");

        HotkeySystem.Register(HotkeyContext.Gameplay, Keys.B, () =>
        {
            EnsureInventoryUIsInitialized();
            if (!_buildingMenuUI.IsVisible)
            {
                CloseSameSidePanels(PanelSide.Left, _buildingMenuUI);
                // Building menu wants to build to the map, so close center panels like inventory as well.
                _inventoryUI.Close();
            }
            _buildingMenuUI.Toggle(GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height);
        }, name: "Building menu");
        HotkeySystem.Register(HotkeyContext.Gameplay, Keys.C,
            () => ToggleCraftingMenu(GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height),
            name: "Crafting menu");

        // Time controls (the mouse buttons live in TimeControlsLayer).
        Action timeUp = () => _timeScale = MathF.Min(_timeScale * 2f, 8f);
        Action timeDown = () => _timeScale = MathF.Max(_timeScale * 0.5f, 0.25f);
        HotkeySystem.Register(HotkeyContext.Gameplay, Keys.OemPlus, timeUp, name: "Speed up time");
        HotkeySystem.Register(HotkeyContext.Gameplay, Keys.Add, timeUp, name: "Speed up time (numpad)");
        HotkeySystem.Register(HotkeyContext.Gameplay, Keys.OemMinus, timeDown, name: "Slow down time");
        HotkeySystem.Register(HotkeyContext.Gameplay, Keys.Subtract, timeDown, name: "Slow down time (numpad)");
        HotkeySystem.Register(HotkeyContext.Gameplay, Keys.D0,
            () => _timeScale = 1f, name: "Reset time scale");
    }

    /// <summary>Cursor position in world space (honours the dev `mousepos` override,
    /// same as UpdateHoverPicks).</summary>
    private Vec2 MouseWorld()
        => _camera.ScreenToWorld(_input.MousePos, GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height);

    /// <summary>Keyboard cast for one spell-bar slot. Circle-targeted spells arm an aim
    /// mode instead of casting outright — the cast fires on the confirming left-click
    /// (Game1.WorldClicks.cs); re-pressing the armed slot cancels.</summary>
    private void CastSlotHotkey(int slot)
    {
        if (slot >= _spellBarState.Slots.Length) return;
        var pressedDef = _gameData.Spells.Get(_spellBarState.Slots[slot].SpellID);
        if (pressedDef != null && pressedDef.TargetingMode == "Circle")
        {
            _aimingSlot = _aimingSlot == slot ? -1 : slot;
            return;
        }
        _aimingSlot = -1; // an instant cast breaks any armed aim
        DispatchSpellCast(_spellBarState.Slots[slot].SpellID, FindNecromancer(), slot, MouseWorld());
    }

    /// <summary>'L' press-to-inspect: show/hide the unit sheet for the unit under the
    /// cursor (may auto-pause while open; closing restores only the pause WE set).</summary>
    private void InspectUnitUnderCursorHotkey()
    {
        var tipCfg = _gameData.Settings.Tooltips;
        EnsureInventoryUIsInitialized();
        if (_unitInfoPanel.IsVisible)
        {
            _unitInfoPanel.Hide();
            return;
        }
        Vec2 cursorWorld = MouseWorld();
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
