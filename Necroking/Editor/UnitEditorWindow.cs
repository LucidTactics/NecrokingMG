using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.Render;

namespace Necroking.Editor;

/// <summary>
/// Full unit definition editor with faction-filtered list, sprite preview with animation playback,
/// stats/identity/caster/equipment sections, and weapon/armor/shield sub-editors.
/// Opened with F9.
/// </summary>
public class UnitEditorWindow
{
    private readonly EditorBase _ui;
    private GameData _gameData = null!;
    private SpriteAtlas[] _atlases = Array.Empty<SpriteAtlas>();

    // --- Unit list state ---
    private int _selectedIdx = -1;
    /// <summary>Select the first item in the list (for screenshot scenarios).</summary>
    public void SelectFirst() { _selectedIdx = 0; }

    // --- Public accessors for scenario testing ---
    /// <summary>Open the weapon sub-editor popup (for UI test scenarios).</summary>
    public void OpenWeaponSubEditor() { _activeSubEditor = SubEditor.Weapon; _subSelectedIdx = 0; }
    /// <summary>Current preview animation playing state.</summary>
    public bool PreviewPlaying => _previewPlaying;
    /// <summary>Current preview animation looping state.</summary>
    public bool PreviewLooping => _previewLooping;
    /// <summary>Current preview animation time.</summary>
    public float PreviewAnimTime => _previewAnim.AnimTime;
    /// <summary>Toggle play/pause on the animation preview.</summary>
    public void TogglePlayPause() { _previewPlaying = !_previewPlaying; }
    /// <summary>Toggle loop/once on the animation preview.</summary>
    public void ToggleLoop() { _previewLooping = !_previewLooping; }
    /// <summary>Step the animation forward one frame.</summary>
    public void StepForward() { _previewPlaying = false; StepAnimForward(); }
    /// <summary>Step the animation backward one frame.</summary>
    public void StepBack() { _previewPlaying = false; StepAnimBackward(); }
    /// <summary>Whether a unit is currently selected.</summary>
    public bool HasSelection => _selectedIdx >= 0;
    private string _searchFilter = "";
    private int _factionTab; // 0=All, 1=Undead, 2=Human, 3=Animal
    private List<string> _filteredIds = new();

    // --- Right panel scroll ---
    private float _propScrollOffset;
    private float _maxPropHeight;

    // --- Sprite preview ---
    private AnimController _previewAnim = new();
    private bool _previewPlaying = true;
    private bool _previewLooping = true;
    private float _previewAngle = 60f; // default facing angle
    private string _previewAtlas = "";
    private string _previewSprite = "";
    private string _previewAnimName = "Idle";
    private UnitSpriteData? _lastPreviewSpriteData; // track to avoid re-init every frame
    private string _lastPreviewAnimName = "";        // track to detect anim changes

    // --- Weapon point editing ---
    private enum PickTarget { None, Hilt, Tip }
    private PickTarget _pickMode = PickTarget.None;
    private bool _rapidEditEnabled;
    private HdrColor _weaponLineColor = new HdrColor(255, 200, 50, 180);

    // --- Preview sprite geometry (saved from last DrawPreviewSprite for pick-mode) ---
    private float _lastPreviewScale;
    private float _lastPreviewDrawX;
    private float _lastPreviewDrawY;
    private float _lastPreviewDrawW;
    private float _lastPreviewDrawH;
    private bool _lastPreviewFlipX;
    private float _lastPreviewPivotScreenX;
    private float _lastPreviewPivotScreenY;
    private int _lastPreviewBoxX, _lastPreviewBoxY, _lastPreviewBoxSize;
    private bool _lastPreviewValid;

    // --- Status ---
    private string _statusMessage = "";
    private float _statusTimer;
    private bool _unsavedChanges;

    // --- RU39: Deferred frame duration editing ---
    private int _editingFrameMs = -1;       // buffered value while text field is active (-1 = not editing)
    private bool _frameMsFieldWasActive;     // track previous frame's active state for commit-on-blur

    // --- Equipment sub-editor popup state ---
    private enum SubEditor { None, Weapon, Armor, Shield }
    private SubEditor _activeSubEditor = SubEditor.None;
    private int _subSelectedIdx = -1;
    private string _subSearchFilter = "";
    private float _subPropScroll;

    // --- Group editor popup state ---
    private bool _groupEditorOpen;
    private int _groupSelectedIdx = -1;
    private float _groupPropScroll;

    // --- Clipboard for Ctrl+C / Ctrl+V ---
    private UnitDef? _clipboardUnit;
    private WeaponDef? _clipboardWeapon;
    private ArmorDef? _clipboardArmor;
    private ShieldDef? _clipboardShield;

    /// <summary>
    /// When true, Game1 should close this editor (toggle menu state).
    /// Reset by Game1 after reading.
    /// </summary>
    public bool WantsClose { get; set; }

    // --- Delete confirmation dialog state ---
    private bool _confirmDeleteOpen;
    private string _confirmDeleteId = "";
    private SubEditor _confirmDeleteType = SubEditor.None;
    private bool _confirmDeleteUnit;      // U12: unit-level delete confirmation
    private bool _confirmDeleteGroup;     // U12: group-level delete confirmation

    // --- Constants ---
    private const int LeftPanelW = 300;
    private const int TabH = 24;
    private const int RowH = 24;
    private const int SearchH = 24;

    // --- Faction filter labels ---
    private static readonly string[] FactionTabs = { "All", "Undead", "Human", "Animal" };

    // --- Cached enum name arrays ---
    private static readonly string[] AINames = Enum.GetNames<AIBehavior>();
    private static readonly string[] FactionNames = Enum.GetNames<Faction>();

    // --- Zombie type labels (from the unit registry itself) ---
    private static readonly string[] AngleOptions = { "30", "60", "300" };

    public UnitEditorWindow(EditorBase ui)
    {
        _ui = ui;
    }

    public void SetGameData(GameData gameData)
    {
        _gameData = gameData;
    }

    private GraphicsDevice? _graphicsDevice;

    public void SetAtlases(SpriteAtlas[] atlases, GraphicsDevice? device = null)
    {
        _atlases = atlases;
        if (device != null) _graphicsDevice = device;
    }

    /// <summary>
    /// Rescan the sprites directory for new .spritemeta files and load any new atlases.
    /// </summary>
    private void RefreshAtlases()
    {
        if (_graphicsDevice == null) return;
        AtlasDefs.ScanSpritesDirectory("assets/Sprites");
        int oldCount = _atlases.Length;
        if (AtlasDefs.TotalCount > oldCount)
        {
            Array.Resize(ref _atlases, AtlasDefs.TotalCount);
            for (int i = oldCount; i < AtlasDefs.TotalCount; i++)
            {
                _atlases[i] = new SpriteAtlas();
                string name = AtlasDefs.Names[i];
                _atlases[i].Load(_graphicsDevice, $"assets/Sprites/{name}.png", $"assets/Sprites/{name}.spritemeta");
            }
            DebugLog.Log("editor", $"Refreshed atlases: {AtlasDefs.TotalCount} total ({AtlasDefs.TotalCount - oldCount} new)");
        }
    }

    /// <summary>
    /// Pass the global animation metadata so the preview AnimController can use
    /// ms-based frame durations instead of tick-based fallback.
    /// </summary>
    public void SetAnimMeta(Dictionary<string, AnimationMeta> animMeta)
    {
        _animMeta = animMeta;
    }
    private Dictionary<string, AnimationMeta>? _animMeta;

    // =========================================================================
    //  MAIN DRAW
    // =========================================================================

    public void Draw(int screenW, int screenH, GameTime gameTime)
    {
        if (_gameData == null) return;

        float dt = (float)gameTime.ElapsedGameTime.TotalSeconds;
        if (_statusTimer > 0) _statusTimer -= dt;

        // --- Dark overlay behind panel ---
        _ui.DrawRect(new Rectangle(0, 0, screenW, screenH), new Color(0, 0, 0, 160));

        // U22: Centered panel with margins
        int marginX = (int)(screenW * 0.05f);
        int marginY = (int)(screenH * 0.03f);
        int panelX = marginX;
        int panelY = marginY;
        int panelW = screenW - marginX * 2;
        int panelH = screenH - marginY * 2;

        // Panel background + border
        _ui.DrawRect(new Rectangle(panelX, panelY, panelW, panelH), new Color(35, 35, 45, 250));
        _ui.DrawBorder(new Rectangle(panelX, panelY, panelW, panelH), new Color(80, 80, 100), 2);

        // --- Top bar ---
        int topBarH = 30;
        _ui.DrawRect(new Rectangle(panelX, panelY, panelW, topBarH), EditorBase.PanelHeader);

        // RU05: Centered title with asterisk when unsaved + small circle dot to the right
        string titleText = _unsavedChanges ? "UNIT EDITOR *" : "UNIT EDITOR";
        var titleSize = _ui.MeasureText(titleText);
        float titleX = panelX + (panelW - titleSize.X) / 2f;
        _ui.DrawText(titleText, new Vector2(titleX, panelY + 6), EditorBase.TextBright, null);
        if (_unsavedChanges)
        {
            // Small circle dot to the right of the title text
            int dotSize = 6;
            int dotX = (int)(titleX + titleSize.X) + 4;
            int dotY = panelY + 6 + (int)(titleSize.Y - dotSize) / 2;
            _ui.DrawRect(new Rectangle(dotX, dotY, dotSize, dotSize), new Color(255, 165, 0));
        }

        // RU04: Close button [X] on the left side of the top bar
        if (_ui.DrawButton("X", panelX + 6, panelY + 3, 24, 24, EditorBase.DangerColor))
            WantsClose = true;

        // Groups button in top bar
        if (_ui.DrawButton("Groups", panelX + panelW - 370, panelY + 3, 70, 24))
            _groupEditorOpen = true;

        // Save button in top bar
        if (_ui.DrawButton("Save All (Ctrl+S)", panelX + panelW - 200, panelY + 3, 160, 24, EditorBase.SuccessColor))
            SaveAll();

        // U21: Guard keyboard shortcuts against active text fields
        bool textActive = _ui.IsTextInputActive;

        // Ctrl+S (skip if text field active)
        if (!textActive &&
            _ui._kb.IsKeyDown(Keys.LeftControl) && _ui._kb.IsKeyDown(Keys.S) &&
            !(_ui._prevKb.IsKeyDown(Keys.LeftControl) && _ui._prevKb.IsKeyDown(Keys.S)))
            SaveAll();

        // U19: Ctrl+C in sub-editors copies weapon/armor/shield; otherwise copies unit
        if (!textActive &&
            _ui._kb.IsKeyDown(Keys.LeftControl) && _ui._kb.IsKeyDown(Keys.C) &&
            !(_ui._prevKb.IsKeyDown(Keys.LeftControl) && _ui._prevKb.IsKeyDown(Keys.C)))
        {
            if (_activeSubEditor == SubEditor.Weapon)
            {
                var wIds = _gameData.Weapons.GetIDs();
                if (_subSelectedIdx >= 0 && _subSelectedIdx < wIds.Count)
                {
                    var srcW = _gameData.Weapons.Get(wIds[_subSelectedIdx]);
                    if (srcW != null) { _clipboardWeapon = CloneWeapon(srcW, srcW.Id); SetStatus("Copied weapon: " + srcW.Id); }
                }
            }
            else if (_activeSubEditor == SubEditor.Armor)
            {
                var aIds = _gameData.Armors.GetIDs();
                if (_subSelectedIdx >= 0 && _subSelectedIdx < aIds.Count)
                {
                    var srcA = _gameData.Armors.Get(aIds[_subSelectedIdx]);
                    if (srcA != null) { _clipboardArmor = CloneArmor(srcA, srcA.Id); SetStatus("Copied armor: " + srcA.Id); }
                }
            }
            else if (_activeSubEditor == SubEditor.Shield)
            {
                var sIds = _gameData.Shields.GetIDs();
                if (_subSelectedIdx >= 0 && _subSelectedIdx < sIds.Count)
                {
                    var srcS = _gameData.Shields.Get(sIds[_subSelectedIdx]);
                    if (srcS != null) { _clipboardShield = CloneShield(srcS, srcS.Id); SetStatus("Copied shield: " + srcS.Id); }
                }
            }
            else if (!_groupEditorOpen)
            {
                var allIdsCopy = _gameData.Units.GetIDs();
                if (_selectedIdx >= 0 && _selectedIdx < allIdsCopy.Count)
                {
                    var srcDef = _gameData.Units.Get(allIdsCopy[_selectedIdx]);
                    if (srcDef != null)
                    {
                        _clipboardUnit = CloneUnit(srcDef, srcDef.Id);
                        SetStatus("Copied: " + srcDef.Id);
                    }
                }
            }
        }

        // U19: Ctrl+V in sub-editors pastes weapon/armor/shield; otherwise pastes unit
        if (!textActive &&
            _ui._kb.IsKeyDown(Keys.LeftControl) && _ui._kb.IsKeyDown(Keys.V) &&
            !(_ui._prevKb.IsKeyDown(Keys.LeftControl) && _ui._prevKb.IsKeyDown(Keys.V)))
        {
            if (_activeSubEditor == SubEditor.Weapon && _clipboardWeapon != null)
            {
                string newId = _clipboardWeapon.Id + "_paste";
                int suffix = 1;
                while (_gameData.Weapons.Get(newId) != null) newId = _clipboardWeapon.Id + "_paste" + (++suffix);
                var newDef = CloneWeapon(_clipboardWeapon, newId);
                newDef.DisplayName = _clipboardWeapon.DisplayName + " (Paste)";
                _gameData.Weapons.Add(newDef);
                _subSelectedIdx = IndexOf(_gameData.Weapons.GetIDs(), newId);
                _unsavedChanges = true;
                SetStatus("Pasted weapon: " + newId);
            }
            else if (_activeSubEditor == SubEditor.Armor && _clipboardArmor != null)
            {
                string newId = _clipboardArmor.Id + "_paste";
                int suffix = 1;
                while (_gameData.Armors.Get(newId) != null) newId = _clipboardArmor.Id + "_paste" + (++suffix);
                var newDef = CloneArmor(_clipboardArmor, newId);
                newDef.DisplayName = _clipboardArmor.DisplayName + " (Paste)";
                _gameData.Armors.Add(newDef);
                _subSelectedIdx = IndexOf(_gameData.Armors.GetIDs(), newId);
                _unsavedChanges = true;
                SetStatus("Pasted armor: " + newId);
            }
            else if (_activeSubEditor == SubEditor.Shield && _clipboardShield != null)
            {
                string newId = _clipboardShield.Id + "_paste";
                int suffix = 1;
                while (_gameData.Shields.Get(newId) != null) newId = _clipboardShield.Id + "_paste" + (++suffix);
                var newDef = CloneShield(_clipboardShield, newId);
                newDef.DisplayName = _clipboardShield.DisplayName + " (Paste)";
                _gameData.Shields.Add(newDef);
                _subSelectedIdx = IndexOf(_gameData.Shields.GetIDs(), newId);
                _unsavedChanges = true;
                SetStatus("Pasted shield: " + newId);
            }
            else if (_activeSubEditor == SubEditor.None && !_groupEditorOpen && _clipboardUnit != null)
            {
                string newId = _clipboardUnit.Id + "_paste";
                int suffix = 1;
                while (_gameData.Units.Get(newId) != null)
                    newId = _clipboardUnit.Id + "_paste" + (++suffix);

                var newDef = CloneUnit(_clipboardUnit, newId);
                newDef.DisplayName = _clipboardUnit.DisplayName + " (Paste)";
                newDef.UnitType = "Dynamic"; // RU26: pasted units are always Dynamic
                var allIdsPaste = _gameData.Units.GetIDs();
                if (_selectedIdx >= 0 && _selectedIdx < allIdsPaste.Count)
                    _gameData.Units.AddAfter(newDef, allIdsPaste[_selectedIdx]);
                else
                    _gameData.Units.Add(newDef);
                _selectedIdx = IndexOf(_gameData.Units.GetIDs(), newId);
                _unsavedChanges = true;
                SyncPreviewToSelected();
                SetStatus("Pasted: " + newId);
            }
        }

        // Escape key hierarchy: confirm dialog -> dropdown -> pick mode -> sub-editors -> group editor -> (caller handles closing unit editor)
        if (!textActive && _ui._kb.IsKeyDown(Keys.Escape) && _ui._prevKb.IsKeyUp(Keys.Escape))
        {
            if (_confirmDeleteOpen)
            {
                _confirmDeleteOpen = false;
            }
            else if (_confirmDeleteUnit)
            {
                _confirmDeleteUnit = false;
            }
            else if (_confirmDeleteGroup)
            {
                _confirmDeleteGroup = false;
            }
            else if (_ui.CloseActiveDropdown())
            {
                // Dropdown was open, consumed escape
            }
            else if (_pickMode != PickTarget.None)
            {
                _pickMode = PickTarget.None;
            }
            else if (_activeSubEditor != SubEditor.None)
            {
                _activeSubEditor = SubEditor.None;
                _subSelectedIdx = -1;
            }
            else if (_groupEditorOpen)
            {
                _groupEditorOpen = false;
            }
        }

        // RU15: Right-click cancels pick mode
        if (_pickMode != PickTarget.None &&
            _ui._mouse.RightButton == ButtonState.Pressed && _ui._prevMouse.RightButton == ButtonState.Released)
        {
            _pickMode = PickTarget.None;
        }

        // RU12: Rapid-edit arrow key navigation when pick mode is active
        if (!textActive && _rapidEditEnabled && _pickMode != PickTarget.None)
        {
            if ((_ui._kb.IsKeyDown(Keys.Left) && _ui._prevKb.IsKeyUp(Keys.Left)) ||
                (_ui._kb.IsKeyDown(Keys.A) && _ui._prevKb.IsKeyUp(Keys.A)))
            {
                StepBack();
                _pickMode = PickTarget.Hilt;
            }
            if ((_ui._kb.IsKeyDown(Keys.Right) && _ui._prevKb.IsKeyUp(Keys.Right)) ||
                (_ui._kb.IsKeyDown(Keys.D) && _ui._prevKb.IsKeyUp(Keys.D)))
            {
                StepForward();
                _pickMode = PickTarget.Hilt;
            }
            if (_ui._kb.IsKeyDown(Keys.Up) && _ui._prevKb.IsKeyUp(Keys.Up))
            {
                int idx = Array.IndexOf(AngleOptions, ((int)_previewAngle).ToString());
                if (idx < 0) idx = 1;
                idx = (idx - 1 + AngleOptions.Length) % AngleOptions.Length;
                if (int.TryParse(AngleOptions[idx], out int a)) _previewAngle = a;
            }
            if (_ui._kb.IsKeyDown(Keys.Down) && _ui._prevKb.IsKeyUp(Keys.Down))
            {
                int idx = Array.IndexOf(AngleOptions, ((int)_previewAngle).ToString());
                if (idx < 0) idx = 1;
                idx = (idx + 1) % AngleOptions.Length;
                if (int.TryParse(AngleOptions[idx], out int a)) _previewAngle = a;
            }
        }

        // Status message in top bar (RU06: alpha fade during last second)
        if (_statusTimer > 0 && !string.IsNullOrEmpty(_statusMessage))
        {
            Color sc = _statusMessage.Contains("FAIL") ? EditorBase.DangerColor : EditorBase.SuccessColor;
            float alpha = Math.Min(1f, _statusTimer);
            sc = sc * alpha;
            _ui.DrawText(_statusMessage, new Vector2(panelX + panelW - 450, panelY + 6), sc);
        }

        int contentY = panelY + topBarH;
        int contentH = panelH - topBarH;

        // =================================================================
        //  LEFT PANEL: faction tabs + search + unit list + CRUD buttons
        // =================================================================
        DrawLeftPanel(panelX, contentY, contentH);

        // Separator
        _ui.DrawRect(new Rectangle(panelX + LeftPanelW, contentY, 1, contentH), EditorBase.PanelBorder);

        // =================================================================
        //  RIGHT PANEL: scrollable detail editor
        // =================================================================
        DrawRightPanel(panelX + LeftPanelW + 1, contentY, panelW - LeftPanelW - 1, contentH, dt);

        // =================================================================
        //  SUB-EDITOR POPUP (if open)
        // =================================================================
        if (_activeSubEditor != SubEditor.None)
            DrawSubEditor(screenW, screenH);

        // =================================================================
        //  GROUP EDITOR POPUP (if open)
        // =================================================================
        if (_groupEditorOpen)
            DrawGroupEditor(screenW, screenH);

        // =================================================================
        //  CONFIRM DELETE DIALOG (drawn last, on top of everything)
        // =================================================================
        if (_confirmDeleteOpen)
        {
            // RU18: Show affected unit count in delete confirmation
            int affectedCount = _confirmDeleteType switch
            {
                SubEditor.Weapon => _gameData.Units.CountUnitsWithWeapon(_confirmDeleteId),
                SubEditor.Armor => _gameData.Units.CountUnitsWithArmor(_confirmDeleteId),
                SubEditor.Shield => _gameData.Units.CountUnitsWithShield(_confirmDeleteId),
                _ => 0
            };
            string confirmMsg = $"Delete '{_confirmDeleteId}'? Used by {affectedCount} unit(s). Remove from all?";
            if (_ui.DrawConfirmDialog("Confirm Delete", confirmMsg, ref _confirmDeleteOpen))
            {
                // Confirmed deletion
                switch (_confirmDeleteType)
                {
                    case SubEditor.Weapon:
                        _gameData.Units.RemoveWeaponFromAll(_confirmDeleteId);
                        _gameData.Weapons.Remove(_confirmDeleteId);
                        _subSelectedIdx = Math.Min(_subSelectedIdx, _gameData.Weapons.Count - 1);
                        SetStatus("Removed weapon: " + _confirmDeleteId);
                        break;
                    case SubEditor.Armor:
                        _gameData.Units.RemoveArmorFromAll(_confirmDeleteId);
                        _gameData.Armors.Remove(_confirmDeleteId);
                        _subSelectedIdx = Math.Min(_subSelectedIdx, _gameData.Armors.Count - 1);
                        SetStatus("Removed armor: " + _confirmDeleteId);
                        break;
                    case SubEditor.Shield:
                        _gameData.Units.RemoveShieldFromAll(_confirmDeleteId);
                        _gameData.Shields.Remove(_confirmDeleteId);
                        _subSelectedIdx = Math.Min(_subSelectedIdx, _gameData.Shields.Count - 1);
                        SetStatus("Removed shield: " + _confirmDeleteId);
                        break;
                }
                _unsavedChanges = true;
            }
        }

        // U12: Unit delete confirmation dialog
        if (_confirmDeleteUnit)
        {
            string unitMsg = $"Delete unit '{_confirmDeleteId}'?";
            if (_ui.DrawConfirmDialog("Confirm Delete", unitMsg, ref _confirmDeleteUnit))
            {
                _gameData.Units.Remove(_confirmDeleteId);
                _selectedIdx = Math.Min(_selectedIdx, _gameData.Units.Count - 1);
                _unsavedChanges = true;
                SyncPreviewToSelected();
                SetStatus("Removed: " + _confirmDeleteId);
            }
        }

        // U12: Group delete confirmation dialog
        if (_confirmDeleteGroup)
        {
            string groupMsg = $"Delete group '{_confirmDeleteId}'?";
            if (_ui.DrawConfirmDialog("Confirm Delete", groupMsg, ref _confirmDeleteGroup))
            {
                _gameData.UnitGroups.Remove(_confirmDeleteId);
                _groupSelectedIdx = Math.Min(_groupSelectedIdx, _gameData.UnitGroups.Count - 1);
                _unsavedChanges = true;
                SetStatus("Removed group: " + _confirmDeleteId);
            }
        }

        // U03: Crosshair cursor overlay when pick mode is active
        if (_pickMode != PickTarget.None)
        {
            int mx = _ui._mouse.X;
            int my = _ui._mouse.Y;
            Color crossColor = _pickMode == PickTarget.Hilt ? new Color(80, 200, 255) : new Color(255, 200, 80);
            int crossSize = 10;
            // Horizontal line
            _ui.DrawLine(new Vector2(mx - crossSize, my), new Vector2(mx + crossSize, my), crossColor, 1);
            // Vertical line
            _ui.DrawLine(new Vector2(mx, my - crossSize), new Vector2(mx, my + crossSize), crossColor, 1);
        }

        // Dropdown overlays (drawn last, on top of everything)
        _ui.DrawDropdownOverlays();
    }

    // =========================================================================
    //  LEFT PANEL
    // =========================================================================

    private void DrawLeftPanel(int x, int y, int h)
    {
        _ui.DrawRect(new Rectangle(x, y, LeftPanelW, h), EditorBase.PanelBg);

        int curY = y + 4;

        // --- Faction filter tabs ---
        int tabW = (LeftPanelW - 8) / FactionTabs.Length;
        for (int i = 0; i < FactionTabs.Length; i++)
        {
            Color bg = i == _factionTab ? EditorBase.ItemSelected : EditorBase.ButtonBg;
            if (_ui.DrawButton(FactionTabs[i], x + 4 + i * tabW, curY, tabW - 2, TabH, bg))
                _factionTab = i;
        }
        curY += TabH + 4;

        // --- Search box ---
        _searchFilter = _ui.DrawSearchField("unit_search", _searchFilter, x + 4, curY, LeftPanelW - 8);
        curY += SearchH + 4;

        // --- Build filtered list ---
        _filteredIds.Clear();
        var allIds = _gameData.Units.GetIDs();
        for (int i = 0; i < allIds.Count; i++)
        {
            var def = _gameData.Units.Get(allIds[i]);
            if (def == null) continue;

            // Faction filter
            if (_factionTab == 1 && def.Faction != "Undead") continue;
            if (_factionTab == 2 && def.Faction != "Human") continue;
            if (_factionTab == 3 && def.Faction != "Animal") continue;

            // Search filter
            if (!string.IsNullOrEmpty(_searchFilter))
            {
                bool match = allIds[i].Contains(_searchFilter, StringComparison.OrdinalIgnoreCase)
                    || def.DisplayName.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase);
                if (!match) continue;
            }

            _filteredIds.Add(allIds[i]);
        }

        // --- Map selected to filtered ---
        int filteredSelectedIdx = -1;
        if (_selectedIdx >= 0 && _selectedIdx < allIds.Count)
        {
            string selectedId = allIds[_selectedIdx];
            filteredSelectedIdx = _filteredIds.IndexOf(selectedId);
        }

        // U25: Build display strings with space for faction color dot
        var displayItems = new List<string>();
        var factionColors = new List<Color>();
        foreach (var id in _filteredIds)
        {
            var def = _gameData.Units.Get(id);
            string name = def?.DisplayName ?? id;
            displayItems.Add("     " + name); // leading space for dot
            string faction = def?.Faction ?? "";
            factionColors.Add(faction == "Undead" ? new Color(80, 200, 80) :
                              faction == "Human" ? new Color(80, 140, 220) :
                              faction == "Animal" ? new Color(200, 160, 60) :
                              EditorBase.TextDim);
        }

        // --- Draw scrollable list ---
        int listH = h - (curY - y) - 36; // room for bottom buttons
        int listX = x + 4;
        int listW2 = LeftPanelW - 8;
        int clicked = _ui.DrawScrollableList("unit_list", displayItems, filteredSelectedIdx,
            listX, curY, listW2, listH, null);

        if (clicked >= 0 && clicked < _filteredIds.Count)
        {
            string clickedId = _filteredIds[clicked];
            _selectedIdx = IndexOf(allIds, clickedId);
            _propScrollOffset = 0;
            SyncPreviewToSelected();
        }

        // U25: Overlay faction color dots on the list items
        {
            float scroll = _ui.GetScrollOffset("unit_list");
            int itemH = 22;
            float dotDrawY = curY - scroll;
            int dotSize = 8;
            for (int i = 0; i < displayItems.Count; i++)
            {
                if (dotDrawY + itemH > curY && dotDrawY < curY + listH)
                {
                    int dotX2 = listX + 6;
                    int dotY2 = (int)dotDrawY + (itemH - dotSize) / 2;
                    _ui.DrawRect(new Rectangle(dotX2, dotY2, dotSize, dotSize), factionColors[i]);
                }
                dotDrawY += itemH;
            }
        }

        curY += listH + 4;

        // --- CRUD buttons ---
        int btnW = 70;
        int btnH = 24;
        int bx = x + 4;

        if (_ui.DrawButton("+ New", bx, curY, btnW, btnH))
        {
            string newId = "unit_" + DateTime.Now.ToString("HHmmss");
            var newDef = new UnitDef
            {
                Id = newId,
                DisplayName = "New Unit",
                Faction = "Undead",
                AI = "AttackClosest",
                Stats = new UnitStatsJson(),
                // RU27: Default sprite and weapon
                Sprite = new SpriteRef { AtlasName = "VampireFaction", SpriteName = "Skeleton" },
                Weapons = new List<string> { "club" },
            };
            if (_selectedIdx >= 0 && _selectedIdx < allIds.Count)
                _gameData.Units.AddAfter(newDef, allIds[_selectedIdx]);
            else
                _gameData.Units.Add(newDef);
            _selectedIdx = IndexOf(_gameData.Units.GetIDs(), newId);
            _unsavedChanges = true;
            SyncPreviewToSelected();
            SetStatus("Added: " + newId);
        }
        bx += btnW + 4;

        if (_selectedIdx >= 0 && _selectedIdx < allIds.Count)
        {
            if (_ui.DrawButton("Copy", bx, curY, btnW, btnH))
            {
                var srcDef = _gameData.Units.Get(allIds[_selectedIdx]);
                if (srcDef != null)
                {
                    string newId = srcDef.Id + "_copy";
                    int suffix = 1;
                    while (_gameData.Units.Get(newId) != null)
                        newId = srcDef.Id + "_copy" + (++suffix);

                    var newDef = CloneUnit(srcDef, newId);
                    newDef.UnitType = "Dynamic"; // RU28: copied units are always Dynamic
                    _gameData.Units.AddAfter(newDef, srcDef.Id);
                    _selectedIdx = IndexOf(_gameData.Units.GetIDs(), newId);
                    _unsavedChanges = true;
                    SyncPreviewToSelected();
                    SetStatus("Duplicated: " + newId);
                }
            }
            bx += btnW + 4;

            if (_ui.DrawButton("Delete", bx, curY, btnW, btnH, EditorBase.DangerColor))
            {
                _confirmDeleteUnit = true;
                _confirmDeleteId = allIds[_selectedIdx];
            }
        }
    }

    // =========================================================================
    //  RIGHT PANEL
    // =========================================================================

    private void DrawRightPanel(int x, int y, int w, int h, float dt)
    {
        _ui.DrawRect(new Rectangle(x, y, w, h), new Color(20, 20, 35, 220));

        var allIds = _gameData.Units.GetIDs();
        if (_selectedIdx < 0 || _selectedIdx >= allIds.Count)
        {
            _ui.DrawText("Select a unit from the list", new Vector2(x + 40, y + 60), EditorBase.TextDim);
            return;
        }

        var def = _gameData.Units.Get(allIds[_selectedIdx]);
        if (def == null) return;

        // --- Scroll handling ---
        var propArea = new Rectangle(x, y, w, h);
        if (propArea.Contains(_ui._mouse.X, _ui._mouse.Y) && _activeSubEditor == SubEditor.None)
        {
            int scrollDelta = _ui._mouse.ScrollWheelValue - _ui._prevMouse.ScrollWheelValue;
            if (scrollDelta != 0)
            {
                _propScrollOffset -= scrollDelta * 0.3f;
                _propScrollOffset = Math.Clamp(_propScrollOffset, 0, Math.Max(0, _maxPropHeight - h + 40));
            }
        }

        int pad = 8;
        int contentW = Math.Min(w - pad * 2, 600);
        int drawX = x + pad;
        int drawY = y + pad - (int)_propScrollOffset;
        int startDrawY = drawY;

        // RU40: Scissor clip the right panel content area
        _ui.BeginClip(new Rectangle(x, y, w, h));

        // RU02: Name and ID above the sprite preview
        drawY = DrawNameIdFields(def, drawX, drawY, contentW);
        drawY += 4;

        // ==== SPRITE PREVIEW ====
        drawY = DrawSpritePreview(def, drawX, drawY, contentW, dt);
        drawY += 8;

        // RU01: Combat Stats BEFORE Identity
        // ==== STATS SECTION ====
        drawY = DrawStatsSection(def, drawX, drawY, contentW);
        drawY += 8;

        // ==== IDENTITY SECTION ====
        drawY = DrawIdentitySection(def, drawX, drawY, contentW);
        drawY += 8;

        // ==== COMBAT OVERRIDES SECTION ====
        drawY = DrawCombatOverridesSection(def, drawX, drawY, contentW);
        drawY += 8;

        // ==== CASTER SECTION ====
        drawY = DrawCasterSection(def, drawX, drawY, contentW);
        drawY += 8;

        // ==== EQUIPMENT SECTION ====
        drawY = DrawEquipmentSection(def, drawX, drawY, contentW);
        drawY += 8;

        // ==== COLOR SECTION ====
        drawY = DrawColorSection(def, drawX, drawY, contentW);
        drawY += 16;

        _maxPropHeight = (drawY - startDrawY) + (int)_propScrollOffset;

        _ui.EndClip(); // RU40: end scissor clip

        // --- Scrollbar (outside clip so it's always visible) ---
        if (_maxPropHeight > h)
        {
            float scrollRatio = _propScrollOffset / (_maxPropHeight - h);
            int barH = Math.Max(20, h * h / (int)_maxPropHeight);
            int barY = y + (int)(scrollRatio * (h - barH));
            _ui.DrawRect(new Rectangle(x + w - 8, barY, 6, barH), new Color(100, 100, 140, 180));
        }
    }

    // =========================================================================
    //  SPRITE PREVIEW (top of right panel)
    // =========================================================================

    private int DrawSpritePreview(UnitDef def, int x, int y, int w, float dt)
    {
        int curY = y;
        DrawSectionHeader("Sprite Preview", x, ref curY, w);

        int previewSize = 128;
        int previewX = x + 4;
        int previewY = curY;

        // Preview box background
        _ui.DrawRect(new Rectangle(previewX, previewY, previewSize, previewSize), new Color(15, 15, 25, 240));
        _ui.DrawBorder(new Rectangle(previewX, previewY, previewSize, previewSize), EditorBase.PanelBorder);

        // U02: Draw weapon line behind sprite if Behind flag is set
        DrawWeaponLineOverlay(def, behindOnly: true);

        // Draw the sprite frame in the preview
        DrawPreviewSprite(def, previewX, previewY, previewSize, dt);

        // U02: Draw weapon line in front of sprite if Behind flag is not set
        DrawWeaponLineOverlay(def, behindOnly: false);

        // Handle pick mode clicks (U03)
        HandlePickModeClick(def, previewX, previewY, previewSize);

        // Pick mode indicator on the preview box
        if (_pickMode != PickTarget.None)
        {
            Color pickBorderColor = _pickMode == PickTarget.Hilt ? new Color(80, 200, 255) : new Color(255, 200, 80);
            _ui.DrawBorder(new Rectangle(previewX, previewY, previewSize, previewSize), pickBorderColor, 2);
            string pickLabel = _pickMode == PickTarget.Hilt ? "PICK HILT" : "PICK TIP";
            _ui.DrawText(pickLabel, new Vector2(previewX + 4, previewY + previewSize - 16), pickBorderColor);
            // RU14: Hint text near preview during pick mode
            _ui.DrawText("Click sprite to set point", new Vector2(previewX, previewY + previewSize + 2), EditorBase.TextDim);
        }

        // Controls to the right of the preview box
        int ctrlX = previewX + previewSize + 10;
        int ctrlW = w - previewSize - 20;
        int ctrlY = previewY;

        // Atlas dropdown + Refresh button
        string[] atlasNames = AtlasDefs.Names;
        string currentAtlas = def.Sprite?.AtlasName ?? "";
        int refreshBtnW = 50;
        string newAtlas = _ui.DrawCombo("prev_atlas", "Atlas", currentAtlas, atlasNames, ctrlX, ctrlY, ctrlW - refreshBtnW - 4);
        if (newAtlas != currentAtlas)
        {
            if (def.Sprite == null) def.Sprite = new SpriteRef();
            def.Sprite.AtlasName = newAtlas;
            _unsavedChanges = true;
            SyncPreviewToSelected();
        }
        if (_ui.DrawButton("Refresh", ctrlX + ctrlW - refreshBtnW, ctrlY, refreshBtnW, 20))
        {
            RefreshAtlases();
        }
        ctrlY += RowH;

        // Sprite dropdown (unit names from the atlas)
        string[] spriteNames = GetUnitNamesFromAtlas(currentAtlas);
        string currentSprite = def.Sprite?.SpriteName ?? "";
        string newSprite = _ui.DrawCombo("prev_sprite", "Sprite", currentSprite, spriteNames, ctrlX, ctrlY, ctrlW);
        if (newSprite != currentSprite)
        {
            if (def.Sprite == null) def.Sprite = new SpriteRef();
            def.Sprite.SpriteName = newSprite;
            _unsavedChanges = true;
            SyncPreviewToSelected();
        }
        ctrlY += RowH;

        // Animation dropdown
        string[] animNames = GetAnimNamesForSprite(currentAtlas, currentSprite);
        _previewAnimName = _ui.DrawCombo("prev_anim", "Animation", _previewAnimName, animNames, ctrlX, ctrlY, ctrlW);
        ctrlY += RowH;

        // --- U10-U13: Improved preview controls ---
        int btnX = ctrlX;

        // U12: Play/Pause with visually distinct colors
        Color playBtnColor = _previewPlaying ? new Color(200, 80, 80) : new Color(80, 200, 100);
        string playLabel = _previewPlaying ? "||" : ">";
        if (_ui.DrawButton(playLabel, btnX, ctrlY, 30, 20, playBtnColor))
        {
            _previewPlaying = !_previewPlaying;
            Core.DebugLog.Log("editor", $"Play/Pause toggled: playing={_previewPlaying} animTime={_previewAnim.AnimTime:F1}");
        }
        btnX += 34;

        // U11: Step back button
        if (_ui.DrawButton("|<", btnX, ctrlY, 30, 20))
        {
            _previewPlaying = false;
            StepAnimBackward();
            Core.DebugLog.Log("editor", $"Step back: animTime={_previewAnim.AnimTime:F1}");
        }
        btnX += 34;

        // Step forward button
        if (_ui.DrawButton(">|", btnX, ctrlY, 30, 20))
        {
            _previewPlaying = false;
            StepAnimForward();
            Core.DebugLog.Log("editor", $"Step forward: animTime={_previewAnim.AnimTime:F1}");
        }
        btnX += 34;

        // U10: Loop toggle
        Color loopColor = _previewLooping ? EditorBase.AccentColor : EditorBase.ButtonBg;
        if (_ui.DrawButton(_previewLooping ? "Loop" : "Once", btnX, ctrlY, 42, 20, loopColor))
        {
            _previewLooping = !_previewLooping;
            Core.DebugLog.Log("editor", $"Loop toggled: looping={_previewLooping}");
        }
        btnX += 46;

        // Frame info
        string frameInfo = $"T:{_previewAnim.AnimTime:F0}";
        _ui.DrawText(frameInfo, new Vector2(btnX + 4, ctrlY + 2), EditorBase.TextDim);
        ctrlY += RowH;

        // U13: Angle stepper (< angle >) instead of dropdown
        _ui.DrawText("Angle", new Vector2(ctrlX, ctrlY + 2), EditorBase.TextDim);
        int angleStepX = ctrlX + 50;
        if (_ui.DrawButton("<", angleStepX, ctrlY, 24, 20))
        {
            int idx = Array.IndexOf(AngleOptions, ((int)_previewAngle).ToString());
            if (idx < 0) idx = 1;
            idx = (idx - 1 + AngleOptions.Length) % AngleOptions.Length;
            if (int.TryParse(AngleOptions[idx], out int a)) _previewAngle = a;
        }
        _ui.DrawText(((int)_previewAngle).ToString(), new Vector2(angleStepX + 28, ctrlY + 2), EditorBase.TextBright);
        var angleTextW = _ui.MeasureText(((int)_previewAngle).ToString());
        if (_ui.DrawButton(">", angleStepX + 30 + (int)angleTextW.X, ctrlY, 24, 20))
        {
            int idx = Array.IndexOf(AngleOptions, ((int)_previewAngle).ToString());
            if (idx < 0) idx = 1;
            idx = (idx + 1) % AngleOptions.Length;
            if (int.TryParse(AngleOptions[idx], out int a)) _previewAngle = a;
        }
        ctrlY += RowH;

        // Update preview animation
        if (_previewPlaying)
        {
            _previewAnim.Update(dt);

            // Handle non-looping playback (U10)
            if (!_previewLooping && _previewAnim.IsAnimFinished)
                _previewPlaying = false;
        }

        curY = Math.Max(previewY + previewSize, ctrlY) + 4;

        // ==== WEAPON POINT SECTION (U01-U06) ====
        curY = DrawWeaponPointSection(def, x, curY, w);

        // ==== ANIMATION TIMING SECTION (U07-U09) ====
        curY = DrawAnimTimingSection(def, x, curY, w);

        return curY;
    }

    private void DrawPreviewSprite(UnitDef def, int boxX, int boxY, int boxSize, float dt)
    {
        _lastPreviewValid = false;

        if (def.Sprite == null || string.IsNullOrEmpty(def.Sprite.AtlasName) || string.IsNullOrEmpty(def.Sprite.SpriteName))
            return;

        var atlasId = AtlasDefs.ResolveAtlasName(def.Sprite.AtlasName);
        if ((int)atlasId >= _atlases.Length) return;
        var atlas = _atlases[(int)atlasId];
        if (!atlas.IsLoaded || atlas.Texture == null) return;

        var spriteData = atlas.GetUnit(def.Sprite.SpriteName);
        if (spriteData == null) return;

        // Only re-init anim controller when sprite data or animation name changes
        bool spriteChanged = spriteData != _lastPreviewSpriteData;
        bool animChanged = _previewAnimName != _lastPreviewAnimName;

        if (spriteChanged)
        {
            _previewAnim.Init(spriteData);
            if (_animMeta != null && def.Sprite != null)
                _previewAnim.SetAnimMeta(_animMeta, def.Sprite.SpriteName);
            _lastPreviewSpriteData = spriteData;
        }

        // Always keep timing overrides in sync (U09)
        var runtimeTimings = BuildRuntimeTimingOverrides(def);
        if (runtimeTimings.Count > 0)
            _previewAnim.SetAnimTimings(runtimeTimings);
        else
            _previewAnim.SetAnimTimings(null);

        if (spriteChanged || animChanged)
        {
            var targetState = NameToAnimState(_previewAnimName);
            _previewAnim.ForceState(targetState);
            _lastPreviewAnimName = _previewAnimName;
        }

        var fr = _previewAnim.GetCurrentFrame(_previewAngle);
        if (!fr.Frame.HasValue) return;

        var frame = fr.Frame.Value;

        // U14: Use pivot data for positioning — pivot marks the unit's feet
        float scaleX = (float)boxSize / frame.Rect.Width;
        float scaleY = (float)boxSize / frame.Rect.Height;
        float scale = Math.Min(scaleX, scaleY) * 0.8f;

        float drawW = frame.Rect.Width * scale;
        float drawH = frame.Rect.Height * scale;

        // Pivot is normalized (0-1). Spritemeta uses bottom-left origin for Y,
        // so flip Y to convert to top-left coords (matching Game1.cs:2566).
        // When FlipX is true, also flip PivotX (matching Game1.cs:2564).
        float pivotNormX = fr.FlipX ? (1f - frame.PivotX) : frame.PivotX;
        float pivotNormY = 1f - frame.PivotY;
        float feetScreenY = boxY + boxSize - 8f; // 8px margin from box bottom
        float drawX = boxX + boxSize / 2f - pivotNormX * drawW;
        float drawY = feetScreenY - pivotNormY * drawH;

        // Clamp so sprite stays reasonably visible in the box
        drawX = Math.Clamp(drawX, boxX - drawW * 0.2f, boxX + boxSize - drawW * 0.8f);
        drawY = Math.Clamp(drawY, boxY, boxY + boxSize - drawH * 0.3f);

        var effects = fr.FlipX ? SpriteEffects.FlipHorizontally : SpriteEffects.None;
        var origin = new Vector2(frame.Rect.Width * 0.5f, frame.Rect.Height * 0.5f);
        var pos = new Vector2(drawX + drawW / 2f, drawY + drawH / 2f);

        _ui.DrawTexture(atlas.Texture, pos, frame.Rect, Color.White, 0f, origin, scale, effects);

        // Save geometry for pick mode and weapon line overlay
        _lastPreviewScale = scale;
        _lastPreviewDrawX = drawX;
        _lastPreviewDrawY = drawY;
        _lastPreviewDrawW = drawW;
        _lastPreviewDrawH = drawH;
        _lastPreviewFlipX = fr.FlipX;
        // Pivot screen position: the sprite's pivot (anchor) in screen coordinates.
        // PivotX is normalized (0-1) across the frame width; PivotY is bottom-left origin so we flip Y.
        // When FlipX, pivotNormX is already flipped above.
        _lastPreviewPivotScreenX = drawX + pivotNormX * drawW;
        _lastPreviewPivotScreenY = drawY + pivotNormY * drawH;
        _lastPreviewBoxX = boxX;
        _lastPreviewBoxY = boxY;
        _lastPreviewBoxSize = boxSize;
        _lastPreviewValid = true;
    }

    // =========================================================================
    //  WEAPON POINT SECTION (U01-U06)
    // =========================================================================

    private WeaponFrameData GetOrCreateWeaponFrame(UnitDef def, out int frameIndex)
    {
        frameIndex = GetCurrentFrameIndex(def);
        string animName = _previewAnimName;
        string yawKey = ((int)_previewAngle).ToString();

        if (!def.WeaponPoints.TryGetValue(animName, out var yawDict))
        {
            yawDict = new Dictionary<string, List<WeaponFrameData>>();
            def.WeaponPoints[animName] = yawDict;
        }

        if (!yawDict.TryGetValue(yawKey, out var frames))
        {
            frames = new List<WeaponFrameData>();
            yawDict[yawKey] = frames;
        }

        while (frames.Count <= frameIndex)
            frames.Add(new WeaponFrameData());

        return frames[frameIndex];
    }

    private WeaponFrameData? TryGetWeaponFrame(UnitDef def, out int frameIndex)
    {
        frameIndex = GetCurrentFrameIndex(def);
        string animName = _previewAnimName;
        string yawKey = ((int)_previewAngle).ToString();

        if (!def.WeaponPoints.TryGetValue(animName, out var yawDict)) return null;
        if (!yawDict.TryGetValue(yawKey, out var frames)) return null;
        if (frameIndex >= frames.Count) return null;
        return frames[frameIndex];
    }

    private int DrawWeaponPointSection(UnitDef def, int x, int y, int w)
    {
        int curY = y;
        DrawSectionHeader("Weapon Points", x, ref curY, w);

        int frameIdx = GetCurrentFrameIndex(def);
        var wpFrame = TryGetWeaponFrame(def, out _);

        _ui.DrawText($"Frame: {frameIdx}  Anim: {_previewAnimName}  Yaw: {(int)_previewAngle}",
            new Vector2(x, curY + 2), EditorBase.TextDim);
        curY += RowH;

        // U01: Hilt coordinates (RU37: slightly wider fields)
        int halfW = (w + 20) / 2;
        float hiltX = wpFrame?.Hilt.X ?? 0f;
        float hiltY = wpFrame?.Hilt.Y ?? 0f;
        float newHiltX = _ui.DrawFloatField("wp_hilt_x", "Hilt X", hiltX, x, curY, halfW, 1f);
        float newHiltY = _ui.DrawFloatField("wp_hilt_y", "Hilt Y", hiltY, x + halfW, curY, halfW, 1f);
        if (Math.Abs(newHiltX - hiltX) > 0.001f || Math.Abs(newHiltY - hiltY) > 0.001f)
        {
            var frame = GetOrCreateWeaponFrame(def, out _);
            frame.Hilt.X = newHiltX;
            frame.Hilt.Y = newHiltY;
            _unsavedChanges = true;
        }
        curY += RowH;

        // U01: Tip coordinates (RU37: slightly wider fields)
        float tipX = wpFrame?.Tip.X ?? 0f;
        float tipY = wpFrame?.Tip.Y ?? 0f;
        float newTipX = _ui.DrawFloatField("wp_tip_x", "Tip X", tipX, x, curY, halfW, 1f);
        float newTipY = _ui.DrawFloatField("wp_tip_y", "Tip Y", tipY, x + halfW, curY, halfW, 1f);
        if (Math.Abs(newTipX - tipX) > 0.001f || Math.Abs(newTipY - tipY) > 0.001f)
        {
            var frame = GetOrCreateWeaponFrame(def, out _);
            frame.Tip.X = newTipX;
            frame.Tip.Y = newTipY;
            _unsavedChanges = true;
        }
        curY += RowH;

        // U04: Behind checkboxes
        bool hiltBehind = wpFrame?.Hilt.Behind ?? false;
        bool tipBehind = wpFrame?.Tip.Behind ?? false;
        bool newHiltBehind = _ui.DrawCheckbox("Hilt Behind", hiltBehind, x, curY);
        bool newTipBehind = _ui.DrawCheckbox("Tip Behind", tipBehind, x + w / 2, curY);
        if (newHiltBehind != hiltBehind)
        {
            var frame = GetOrCreateWeaponFrame(def, out _);
            frame.Hilt.Behind = newHiltBehind;
            _unsavedChanges = true;
        }
        if (newTipBehind != tipBehind)
        {
            var frame = GetOrCreateWeaponFrame(def, out _);
            frame.Tip.Behind = newTipBehind;
            _unsavedChanges = true;
        }
        curY += RowH;

        // U03: Pick buttons + U06: Rapid edit
        int pickBtnX = x;
        Color pickHiltColor = _pickMode == PickTarget.Hilt ? new Color(80, 200, 255) : EditorBase.ButtonBg;
        if (_ui.DrawButton("Pick Hilt", pickBtnX, curY, 70, 20, pickHiltColor))
            _pickMode = _pickMode == PickTarget.Hilt ? PickTarget.None : PickTarget.Hilt;
        pickBtnX += 74;

        Color pickTipColor = _pickMode == PickTarget.Tip ? new Color(255, 200, 80) : EditorBase.ButtonBg;
        if (_ui.DrawButton("Pick Tip", pickBtnX, curY, 70, 20, pickTipColor))
            _pickMode = _pickMode == PickTarget.Tip ? PickTarget.None : PickTarget.Tip;
        pickBtnX += 74;

        if (_pickMode != PickTarget.None)
        {
            if (_ui.DrawButton("Cancel", pickBtnX, curY, 55, 20, EditorBase.DangerColor))
                _pickMode = PickTarget.None;
            pickBtnX += 59;
        }

        // U06: Rapid edit toggle
        _rapidEditEnabled = _ui.DrawCheckbox("Rapid Edit", _rapidEditEnabled, pickBtnX + 8, curY);
        curY += RowH;

        // U05: Weapon line color swatch
        bool colorChanged = _ui.DrawColorSwatch("wp_line_color", x, curY, 30, 18, ref _weaponLineColor, true);
        _ui.DrawText("Weapon Line Color", new Vector2(x + 36, curY + 2), EditorBase.TextDim);
        curY += RowH;

        return curY;
    }

    /// <summary>
    /// U02: Draw weapon line overlay on the sprite preview.
    /// When behindOnly=true, only draws if the weapon's Behind flag is set (renders before sprite).
    /// When behindOnly=false, only draws if the Behind flag is not set (renders after sprite).
    /// </summary>
    private void DrawWeaponLineOverlay(UnitDef def, bool behindOnly)
    {
        if (!_lastPreviewValid) return;
        var wpFrame = TryGetWeaponFrame(def, out _);
        if (wpFrame == null) return;

        // Use hilt's Behind flag to determine render order for the whole weapon line
        bool isBehind = wpFrame.Hilt.Behind;
        if (behindOnly != isBehind) return;

        var hiltScreen = WeaponPointToScreen(wpFrame.Hilt.X, wpFrame.Hilt.Y);
        var tipScreen = WeaponPointToScreen(wpFrame.Tip.X, wpFrame.Tip.Y);

        var lineColor = new Color(_weaponLineColor.R, _weaponLineColor.G, _weaponLineColor.B, _weaponLineColor.A);
        _ui.DrawLine(hiltScreen, tipScreen, lineColor, 2);

        // U02: Draw circles at hilt and tip positions (4px radius approximated with line segments)
        DrawCircleOverlay(hiltScreen, 4, new Color(80, 200, 255));
        DrawCircleOverlay(tipScreen, 4, new Color(255, 200, 80));
    }

    /// <summary>Draw a circle at the given screen position using line segments.</summary>
    private void DrawCircleOverlay(Vector2 center, int radius, Color color)
    {
        const int segments = 12;
        for (int i = 0; i < segments; i++)
        {
            float a0 = MathF.PI * 2f * i / segments;
            float a1 = MathF.PI * 2f * (i + 1) / segments;
            var p0 = center + new Vector2(MathF.Cos(a0) * radius, MathF.Sin(a0) * radius);
            var p1 = center + new Vector2(MathF.Cos(a1) * radius, MathF.Sin(a1) * radius);
            _ui.DrawLine(p0, p1, color, 1);
        }
    }

    private Vector2 WeaponPointToScreen(float wpX, float wpY)
    {
        // Pivot-based: weapon points are relative to the sprite's pivot (anchor point).
        // C++ reference: pivotScreenX + wpX * scale * flipMul
        float flipMul = _lastPreviewFlipX ? -1f : 1f;
        float sx = _lastPreviewPivotScreenX + wpX * _lastPreviewScale * flipMul;
        float sy = _lastPreviewPivotScreenY + wpY * _lastPreviewScale;
        return new Vector2(sx, sy);
    }

    private Vector2 ScreenToWeaponPoint(float screenX, float screenY)
    {
        // Inverse of WeaponPointToScreen: convert screen coords back to weapon-point space.
        float flipMul = _lastPreviewFlipX ? -1f : 1f;
        float wpX = (screenX - _lastPreviewPivotScreenX) / (_lastPreviewScale * flipMul);
        float wpY = (screenY - _lastPreviewPivotScreenY) / _lastPreviewScale;
        return new Vector2(wpX, wpY);
    }

    private void HandlePickModeClick(UnitDef def, int boxX, int boxY, int boxSize)
    {
        if (_pickMode == PickTarget.None) return;
        if (!_lastPreviewValid) return;

        var boxRect = new Rectangle(boxX, boxY, boxSize, boxSize);
        if (!boxRect.Contains(_ui._mouse.X, _ui._mouse.Y)) return;

        if (_ui._mouse.LeftButton == ButtonState.Pressed && _ui._prevMouse.LeftButton == ButtonState.Released)
        {
            var wp = ScreenToWeaponPoint(_ui._mouse.X, _ui._mouse.Y);
            var frame = GetOrCreateWeaponFrame(def, out _);

            if (_pickMode == PickTarget.Hilt)
            {
                frame.Hilt.X = MathF.Round(wp.X);
                frame.Hilt.Y = MathF.Round(wp.Y);
            }
            else if (_pickMode == PickTarget.Tip)
            {
                frame.Tip.X = MathF.Round(wp.X);
                frame.Tip.Y = MathF.Round(wp.Y);
            }

            _unsavedChanges = true;

            if (_rapidEditEnabled)
            {
                if (_pickMode == PickTarget.Hilt)
                {
                    // After setting hilt, auto-switch to tip
                    _pickMode = PickTarget.Tip;
                }
                else
                {
                    // After setting tip, advance to next frame and switch back to hilt
                    StepAnimForward();
                    _pickMode = PickTarget.Hilt;
                }
            }
            else
                _pickMode = PickTarget.None;
        }
    }

    // =========================================================================
    //  ANIMATION TIMING SECTION (U07-U09)
    // =========================================================================

    private int DrawAnimTimingSection(UnitDef def, int x, int y, int w)
    {
        int curY = y;
        DrawSectionHeader("Animation Timing", x, ref curY, w);

        string animName = _previewAnimName;
        def.AnimTimings.TryGetValue(animName, out var timing);

        int frameIdx = GetCurrentFrameIndex(def);
        int frameCount = GetFrameCountForCurrentAnim(def);

        // U07: Frame duration field (ms)
        int currentFrameDur = 100;
        bool hasTimingOverride = false;
        if (timing != null && frameIdx < timing.FrameDurationsMs.Count)
        {
            currentFrameDur = timing.FrameDurationsMs[frameIdx];
            hasTimingOverride = true;
        }

        // RU39: Deferred frame duration editing - buffer value while text field is active
        string durLabel = hasTimingOverride ? $"Frame {frameIdx} ms *" : $"Frame {frameIdx} ms";
        bool frameMsFieldActive = _ui.IsFieldActive("at_framedur");
        int displayValue = frameMsFieldActive && _editingFrameMs >= 0 ? _editingFrameMs : currentFrameDur;
        int newFrameDur = _ui.DrawIntField("at_framedur", durLabel, displayValue, x, curY, w);

        if (frameMsFieldActive)
        {
            // While field is active, just buffer the value (don't apply to data)
            _editingFrameMs = newFrameDur;
        }

        // Commit on blur: field was active last frame, not active now
        bool justDeactivated = _frameMsFieldWasActive && !frameMsFieldActive;
        // Also commit on +/- button clicks (value changed while not typing)
        bool buttonChanged = !frameMsFieldActive && newFrameDur != currentFrameDur;

        if (justDeactivated || buttonChanged)
        {
            int commitValue = justDeactivated ? (_editingFrameMs >= 0 ? _editingFrameMs : currentFrameDur) : newFrameDur;
            if (commitValue != currentFrameDur)
            {
                if (timing == null)
                {
                    timing = new UnitAnimTimingOverride();
                    def.AnimTimings[animName] = timing;
                }
                while (timing.FrameDurationsMs.Count <= frameIdx)
                    timing.FrameDurationsMs.Add(100);
                timing.FrameDurationsMs[frameIdx] = Math.Max(1, commitValue);
                _unsavedChanges = true;
            }
            _editingFrameMs = -1;
        }
        _frameMsFieldWasActive = frameMsFieldActive;
        curY += RowH;

        if (_ui.DrawButton("Set All Frames", x, curY, 100, 20))
        {
            if (timing == null)
            {
                timing = new UnitAnimTimingOverride();
                def.AnimTimings[animName] = timing;
            }
            timing.FrameDurationsMs.Clear();
            for (int i = 0; i < Math.Max(1, frameCount); i++)
                timing.FrameDurationsMs.Add(Math.Max(1, currentFrameDur));
            _unsavedChanges = true;
        }

        if (timing != null && _ui.DrawButton("Clear Timing", x + 108, curY, 100, 20, EditorBase.DangerColor))
        {
            def.AnimTimings.Remove(animName);
            _unsavedChanges = true;
        }

        _ui.DrawText($"Frames: {frameCount}", new Vector2(x + 220, curY + 2), EditorBase.TextDim);
        curY += RowH;

        // U08: Effect time field with override indicator
        int effectTime = timing?.EffectTimeMs ?? -1;
        bool hasEffectOverride = effectTime >= 0;
        string effectLabel = hasEffectOverride ? "Effect ms *" : "Effect ms";
        int newEffectTime = _ui.DrawIntField("at_effecttime", effectLabel, hasEffectOverride ? effectTime : 0, x, curY, w);
        if (newEffectTime != (hasEffectOverride ? effectTime : 0))
        {
            if (timing == null)
            {
                timing = new UnitAnimTimingOverride();
                def.AnimTimings[animName] = timing;
            }
            timing.EffectTimeMs = Math.Max(0, newEffectTime);
            _unsavedChanges = true;
        }

        if (hasEffectOverride)
        {
            if (_ui.DrawButton("X", x + w - 24, curY, 22, 20, EditorBase.DangerColor))
            {
                if (timing != null) timing.EffectTimeMs = -1;
                _unsavedChanges = true;
            }
        }
        curY += RowH;

        return curY;
    }

    private Dictionary<string, Render.AnimTimingOverride> BuildRuntimeTimingOverrides(UnitDef def)
    {
        var result = new Dictionary<string, Render.AnimTimingOverride>();
        foreach (var (animName, unitTiming) in def.AnimTimings)
        {
            var rt = new Render.AnimTimingOverride
            {
                FrameDurationsMs = new List<int>(unitTiming.FrameDurationsMs),
                EffectTimeMs = unitTiming.EffectTimeMs,
            };
            result[animName] = rt;
        }
        return result;
    }

    private int GetCurrentFrameIndex(UnitDef def)
    {
        if (def.Sprite == null || string.IsNullOrEmpty(def.Sprite.AtlasName) || string.IsNullOrEmpty(def.Sprite.SpriteName))
            return 0;

        var atlasId = AtlasDefs.ResolveAtlasName(def.Sprite.AtlasName);
        if ((int)atlasId >= _atlases.Length) return 0;
        var atlas = _atlases[(int)atlasId];
        if (!atlas.IsLoaded) return 0;

        var spriteData = atlas.GetUnit(def.Sprite.SpriteName);
        if (spriteData == null) return 0;

        var anim = spriteData.GetAnim(_previewAnimName);
        if (anim == null) return 0;

        int spriteAngle = _previewAnim.ResolveAngle(_previewAngle, out _);
        var kfs = anim.GetAngle(spriteAngle);
        if (kfs == null || kfs.Count == 0)
            kfs = anim.GetAngle(30);
        if (kfs == null || kfs.Count == 0) return 0;

        if (def.AnimTimings.TryGetValue(_previewAnimName, out var timing) && timing.FrameDurationsMs.Count > 0)
        {
            float cumMs = 0;
            for (int i = 0; i < timing.FrameDurationsMs.Count; i++)
            {
                cumMs += timing.FrameDurationsMs[i];
                if (_previewAnim.AnimTime < cumMs) return i;
            }
            return Math.Max(0, timing.FrameDurationsMs.Count - 1);
        }

        int frameIdx = 0;
        for (int i = kfs.Count - 1; i >= 0; i--)
        {
            if (_previewAnim.AnimTime >= kfs[i].Time) { frameIdx = i; break; }
        }
        return frameIdx;
    }

    private int GetFrameCountForCurrentAnim(UnitDef def)
    {
        if (def.Sprite == null || string.IsNullOrEmpty(def.Sprite.AtlasName) || string.IsNullOrEmpty(def.Sprite.SpriteName))
            return 0;

        var atlasId = AtlasDefs.ResolveAtlasName(def.Sprite.AtlasName);
        if ((int)atlasId >= _atlases.Length) return 0;
        var atlas = _atlases[(int)atlasId];
        if (!atlas.IsLoaded) return 0;

        var spriteData = atlas.GetUnit(def.Sprite.SpriteName);
        if (spriteData == null) return 0;

        var anim = spriteData.GetAnim(_previewAnimName);
        if (anim == null) return 0;

        int spriteAngle = _previewAnim.ResolveAngle(_previewAngle, out _);
        var kfs = anim.GetAngle(spriteAngle);
        if (kfs == null || kfs.Count == 0)
            kfs = anim.GetAngle(30);
        return kfs?.Count ?? 0;
    }

    private void StepAnimForward()
    {
        var allIds = _gameData.Units.GetIDs();
        if (_selectedIdx < 0 || _selectedIdx >= allIds.Count) return;
        var def = _gameData.Units.Get(allIds[_selectedIdx]);
        if (def?.Sprite == null) return;

        var atlasId = AtlasDefs.ResolveAtlasName(def.Sprite.AtlasName);
        if ((int)atlasId >= _atlases.Length) return;
        var atlas = _atlases[(int)atlasId];
        if (!atlas.IsLoaded) return;

        var spriteData = atlas.GetUnit(def.Sprite.SpriteName);
        if (spriteData == null) return;

        var anim = spriteData.GetAnim(_previewAnimName);
        if (anim == null) return;

        int spriteAngle = _previewAnim.ResolveAngle(_previewAngle, out _);

        if (def.AnimTimings.TryGetValue(_previewAnimName, out var timing) && timing.FrameDurationsMs.Count > 0)
        {
            int currentFrame = GetCurrentFrameIndex(def);
            int nextFrame = (currentFrame + 1) % timing.FrameDurationsMs.Count;
            float targetMs = 0;
            for (int i = 0; i < nextFrame; i++)
                targetMs += timing.FrameDurationsMs[i];
            _previewAnim.AnimTime = targetMs;
            return;
        }

        var kfs = anim.GetAngle(spriteAngle);
        if (kfs == null || kfs.Count == 0)
            kfs = anim.GetAngle(30);
        if (kfs == null || kfs.Count == 0) return;

        int curIdx = 0;
        for (int i = kfs.Count - 1; i >= 0; i--)
        {
            if (_previewAnim.AnimTime >= kfs[i].Time) { curIdx = i; break; }
        }
        int nextIdx = (curIdx + 1) % kfs.Count;
        _previewAnim.AnimTime = kfs[nextIdx].Time;
    }

    private void StepAnimBackward()
    {
        var allIds = _gameData.Units.GetIDs();
        if (_selectedIdx < 0 || _selectedIdx >= allIds.Count) return;
        var def = _gameData.Units.Get(allIds[_selectedIdx]);
        if (def?.Sprite == null) return;

        var atlasId = AtlasDefs.ResolveAtlasName(def.Sprite.AtlasName);
        if ((int)atlasId >= _atlases.Length) return;
        var atlas = _atlases[(int)atlasId];
        if (!atlas.IsLoaded) return;

        var spriteData = atlas.GetUnit(def.Sprite.SpriteName);
        if (spriteData == null) return;

        var anim = spriteData.GetAnim(_previewAnimName);
        if (anim == null) return;

        int spriteAngle = _previewAnim.ResolveAngle(_previewAngle, out _);

        if (def.AnimTimings.TryGetValue(_previewAnimName, out var timing) && timing.FrameDurationsMs.Count > 0)
        {
            int currentFrame = GetCurrentFrameIndex(def);
            int prevFrame = (currentFrame - 1 + timing.FrameDurationsMs.Count) % timing.FrameDurationsMs.Count;
            float targetMs = 0;
            for (int i = 0; i < prevFrame; i++)
                targetMs += timing.FrameDurationsMs[i];
            _previewAnim.AnimTime = targetMs;
            return;
        }

        var kfs = anim.GetAngle(spriteAngle);
        if (kfs == null || kfs.Count == 0)
            kfs = anim.GetAngle(30);
        if (kfs == null || kfs.Count == 0) return;

        int curIdx = 0;
        for (int i = kfs.Count - 1; i >= 0; i--)
        {
            if (_previewAnim.AnimTime >= kfs[i].Time) { curIdx = i; break; }
        }
        int prevIdx = (curIdx - 1 + kfs.Count) % kfs.Count;
        _previewAnim.AnimTime = kfs[prevIdx].Time;
    }

    // =========================================================================
    //  RU02: NAME / ID FIELDS (above sprite preview)
    // =========================================================================

    private string _pendingIdEdit = ""; // buffer for ID editing
    private bool _idEditFailed;
    private float _idEditFailTimer;

    private int DrawNameIdFields(UnitDef def, int x, int y, int w)
    {
        int curY = y;

        // Name
        string newName = _ui.DrawTextField("unit_name", "Name", def.DisplayName, x, curY, w);
        if (newName != def.DisplayName) { def.DisplayName = newName; _unsavedChanges = true; }
        curY += RowH;

        // ID (editable with validation)
        string newId = _ui.DrawTextField("unit_id", "ID", def.Id, x, curY, w);
        if (newId != def.Id && !_ui.IsFieldActive("unit_id"))
        {
            // Field lost focus with a changed value — try to rename
            string oldId = def.Id;
            if (_gameData.Units.RenameId(oldId, newId))
            {
                // Update all references across the game data
                UpdateUnitIdReferences(oldId, newId);
                _unsavedChanges = true;
                _idEditFailed = false;
            }
            else
            {
                // Duplicate or invalid — flash red warning
                _idEditFailed = true;
                _idEditFailTimer = 2f;
            }
        }
        if (_idEditFailed && _idEditFailTimer > 0)
        {
            _idEditFailTimer -= 1f / 60f;
            _ui.DrawText("ID already exists!", new Vector2(x + 120, curY + 14),
                new Color((byte)255, (byte)80, (byte)80, (byte)(255 * Math.Min(1f, _idEditFailTimer))));
        }
        curY += RowH;

        return curY;
    }

    /// <summary>
    /// Update all references to a unit ID across the game data when it's renamed.
    /// </summary>
    private void UpdateUnitIdReferences(string oldId, string newId)
    {
        // Update other units' zombie type references
        foreach (var uid in _gameData.Units.GetIDs())
        {
            var u = _gameData.Units.Get(uid);
            if (u == null) continue;
            if (u.ZombieTypeID == oldId) u.ZombieTypeID = newId;
        }

        // Update unit group entries
        foreach (var gid in _gameData.UnitGroups.GetIDs())
        {
            var g = _gameData.UnitGroups.Get(gid);
            if (g?.Entries == null) continue;
            foreach (var entry in g.Entries)
            {
                if (entry.UnitDefID == oldId) entry.UnitDefID = newId;
            }
        }

        // Update spell summon unit IDs
        foreach (var sid in _gameData.Spells.GetIDs())
        {
            var s = _gameData.Spells.Get(sid);
            if (s == null) continue;
            if (s.SummonUnitID == oldId) s.SummonUnitID = newId;
        }
    }

    // =========================================================================
    //  IDENTITY SECTION
    // =========================================================================

    private int DrawIdentitySection(UnitDef def, int x, int y, int w)
    {
        int curY = y;
        DrawSectionHeader("Identity", x, ref curY, w);

        // Faction
        string newFaction = _ui.DrawCombo("unit_faction", "Faction", def.Faction, FactionNames, x, curY, w);
        if (newFaction != def.Faction) { def.Faction = newFaction; _unsavedChanges = true; }
        curY += RowH;

        // AI
        string newAI = _ui.DrawCombo("unit_ai", "AI", def.AI, AINames, x, curY, w);
        if (newAI != def.AI) { def.AI = newAI; _unsavedChanges = true; }
        curY += RowH;

        // ORCA Priority
        int newOrcaPri = _ui.DrawIntField("unit_orca", "ORCA Priority", def.OrcaPriority, x, curY, w);
        if (newOrcaPri != def.OrcaPriority) { def.OrcaPriority = newOrcaPri; _unsavedChanges = true; }
        curY += RowH;

        // U15: Size (auto-derives radius when changed)
        int newSize = _ui.DrawIntField("unit_size", "Size", def.Size, x, curY, w);
        if (newSize != def.Size)
        {
            def.Size = newSize;
            def.Radius = newSize * 0.25f; // auto-derive radius
            _unsavedChanges = true;
        }
        curY += RowH;

        // Radius (show derived hint)
        float newRadius = _ui.DrawFloatField("unit_radius", "Radius", def.Radius, x, curY, w, 0.05f);
        if (Math.Abs(newRadius - def.Radius) > 0.001f) { def.Radius = newRadius; _unsavedChanges = true; }
        // U15: Show derived radius hint
        float derivedRadius = def.Size * 0.25f;
        _ui.DrawText($"(auto: {derivedRadius:F2})", new Vector2(x + w - 90, curY + 2), EditorBase.TextDim);
        curY += RowH;

        // Sprite Scale
        float newScale = _ui.DrawFloatField("unit_sprscale", "Sprite Scale", def.SpriteScale, x, curY, w, 0.1f);
        if (Math.Abs(newScale - def.SpriteScale) > 0.001f) { def.SpriteScale = newScale; _unsavedChanges = true; }
        curY += RowH;

        // World Height
        float newHeight = _ui.DrawFloatField("unit_worldh", "World Height", def.SpriteWorldHeight, x, curY, w, 0.1f);
        if (Math.Abs(newHeight - def.SpriteWorldHeight) > 0.001f) { def.SpriteWorldHeight = newHeight; _unsavedChanges = true; }
        curY += RowH;

        // U16: Zombie Type dropdown with grouped items (units + groups)
        // RU30: Map current value to display format for groups
        string[] zombieTypes = BuildZombieTypeList();
        string zombieDisplay = def.ZombieTypeID ?? "";
        // If the current value is a group ID, find its display form
        var gDef2 = _gameData.UnitGroups.Get(def.ZombieTypeID);
        if (gDef2 != null)
            zombieDisplay = $"Group: {gDef2.DisplayName} [{def.ZombieTypeID}]";
        string newZombieType = _ui.DrawCombo("unit_zombie", "Zombie Type", zombieDisplay, zombieTypes, x, curY, w, allowNone: true);
        if (newZombieType != zombieDisplay && !newZombieType.StartsWith("-- "))
        {
            // allowNone returns "" when "(none)" is selected
            if (string.IsNullOrEmpty(newZombieType))
            {
                def.ZombieTypeID = "";
                _unsavedChanges = true;
            }
            else
            {
                // RU30: Extract raw group ID from "Group: DisplayName [id]" format
                string resolvedId = newZombieType;
                if (newZombieType.StartsWith("Group: ") && newZombieType.Contains('[') && newZombieType.EndsWith("]"))
                {
                    int bracketStart = newZombieType.LastIndexOf('[');
                    resolvedId = newZombieType.Substring(bracketStart + 1, newZombieType.Length - bracketStart - 2);
                }
                def.ZombieTypeID = resolvedId;
                _unsavedChanges = true;
            }
        }
        curY += RowH;

        return curY;
    }

    // =========================================================================
    //  STATS SECTION
    // =========================================================================

    private int DrawStatsSection(UnitDef def, int x, int y, int w)
    {
        int curY = y;
        DrawSectionHeader("Combat Stats", x, ref curY, w);

        if (def.Stats == null)
        {
            if (_ui.DrawButton("Create Stats", x, curY, 120, 22))
            {
                def.Stats = new UnitStatsJson();
                _unsavedChanges = true;
            }
            curY += RowH;
            return curY;
        }

        var s = def.Stats;

        int newHP = _ui.DrawIntField("st_hp", "MaxHP", s.MaxHP, x, curY, w);
        if (newHP != s.MaxHP) { s.MaxHP = newHP; _unsavedChanges = true; }
        curY += RowH;

        int newStr = _ui.DrawIntField("st_str", "Strength", s.Strength, x, curY, w);
        if (newStr != s.Strength) { s.Strength = newStr; _unsavedChanges = true; }
        curY += RowH;

        int newAtk = _ui.DrawIntField("st_atk", "Attack", s.Attack, x, curY, w);
        if (newAtk != s.Attack) { s.Attack = newAtk; _unsavedChanges = true; }
        curY += RowH;

        int newDef2 = _ui.DrawIntField("st_def", "Defense", s.Defense, x, curY, w);
        if (newDef2 != s.Defense) { s.Defense = newDef2; _unsavedChanges = true; }
        curY += RowH;

        int newMR = _ui.DrawIntField("st_mr", "MagicResist", s.MagicResist, x, curY, w);
        if (newMR != s.MagicResist) { s.MagicResist = newMR; _unsavedChanges = true; }
        curY += RowH;

        int newEnc = _ui.DrawIntField("st_enc", "Encumbrance", s.Encumbrance, x, curY, w);
        if (newEnc != s.Encumbrance) { s.Encumbrance = newEnc; _unsavedChanges = true; }
        curY += RowH;

        int newProt = _ui.DrawIntField("st_prot", "NaturalProt", s.NaturalProt, x, curY, w);
        if (newProt != s.NaturalProt) { s.NaturalProt = newProt; _unsavedChanges = true; }
        curY += RowH;

        float newCS = _ui.DrawFloatField("st_cs", "CombatSpeed", s.CombatSpeed, x, curY, w, 0.5f);
        if (Math.Abs(newCS - s.CombatSpeed) > 0.001f) { s.CombatSpeed = newCS; _unsavedChanges = true; }
        curY += RowH;

        return curY;
    }

    // =========================================================================
    //  COMBAT OVERRIDES SECTION
    // =========================================================================

    private int DrawCombatOverridesSection(UnitDef def, int x, int y, int w)
    {
        int curY = y;
        DrawSectionHeader("Combat Overrides", x, ref curY, w);

        curY = DrawNullableFloat("co_atkcd", "Attack Cooldown", def.AttackCooldown, x, curY, w, 0.5f,
            v => { def.AttackCooldown = v; _unsavedChanges = true; });
        curY = DrawNullableFloat("co_lockout", "Post-Attack Lockout", def.PostAttackLockout, x, curY, w, 0.1f,
            v => { def.PostAttackLockout = v; _unsavedChanges = true; });
        curY = DrawNullableFloat("co_turn", "Turn Speed", def.TurnSpeed, x, curY, w, 10f,
            v => { def.TurnSpeed = v; _unsavedChanges = true; });
        curY = DrawNullableFloat("co_accelh", "Accel Half Time", def.AccelHalfTime, x, curY, w, 0.1f,
            v => { def.AccelHalfTime = v; _unsavedChanges = true; });
        curY = DrawNullableFloat("co_accel80", "Accel 80% Time", def.Accel80Time, x, curY, w, 0.1f,
            v => { def.Accel80Time = v; _unsavedChanges = true; });
        curY = DrawNullableFloat("co_accelf", "Accel Full Time", def.AccelFullTime, x, curY, w, 0.5f,
            v => { def.AccelFullTime = v; _unsavedChanges = true; });

        return curY;
    }

    private int DrawNullableFloat(string id, string label, float? value, int x, int y, int w, float step,
        Action<float?> setter)
    {
        bool hasValue = value.HasValue;
        float displayVal = value ?? 0f;

        // Checkbox to enable/disable override
        bool newHas = _ui.DrawCheckbox(label, hasValue, x, y);
        if (newHas != hasValue)
        {
            setter(newHas ? displayVal : null);
            hasValue = newHas;
        }

        if (hasValue)
        {
            float newVal = _ui.DrawFloatField(id, label, displayVal, x + 20, y + RowH, w - 20, step);
            if (Math.Abs(newVal - displayVal) > 0.001f)
                setter(newVal);
            return y + RowH * 2;
        }
        return y + RowH;
    }

    // =========================================================================
    //  CASTER SECTION
    // =========================================================================

    private int DrawCasterSection(UnitDef def, int x, int y, int w)
    {
        int curY = y;
        DrawSectionHeader("Caster", x, ref curY, w);

        // U17: Spell dropdown shows "DisplayName (spellId)" format
        string[] spellDisplayNames = BuildSpellDropdownDisplayList();
        string[] spellIds = BuildSpellDropdownList();
        // Find current display name for the selected spell
        string currentSpellDisplay = string.IsNullOrEmpty(def.SpellID) ? "" : def.SpellID;
        for (int si = 0; si < spellIds.Length; si++)
        {
            if (spellIds[si] == def.SpellID) { currentSpellDisplay = spellDisplayNames[si]; break; }
        }
        string newSpellDisplay = _ui.DrawCombo("unit_spell", "Spell", currentSpellDisplay, spellDisplayNames, x, curY, w, allowNone: true);
        // Map display name back to ID
        if (newSpellDisplay != currentSpellDisplay)
        {
            if (string.IsNullOrEmpty(newSpellDisplay))
            {
                def.SpellID = "";
                _unsavedChanges = true;
            }
            else
            {
                for (int si = 0; si < spellDisplayNames.Length; si++)
                {
                    if (spellDisplayNames[si] == newSpellDisplay) { def.SpellID = spellIds[si]; _unsavedChanges = true; break; }
                }
            }
        }
        curY += RowH;

        // Max Mana
        float newMana = _ui.DrawFloatField("unit_mana", "Max Mana", def.MaxMana, x, curY, w, 1.0f);
        if (Math.Abs(newMana - def.MaxMana) > 0.001f) { def.MaxMana = newMana; _unsavedChanges = true; }
        curY += RowH;

        // RU10: Mana Regen x10 integer spinner — edit as int, store as value * 0.1f
        int manaRegenX10 = (int)(def.ManaRegen * 10);
        int newRegenX10 = _ui.DrawIntField("unit_mregen", "Mana Regen x10", manaRegenX10, x, curY, w);
        if (newRegenX10 != manaRegenX10) { def.ManaRegen = newRegenX10 * 0.1f; _unsavedChanges = true; }
        // Show full mana time estimate
        if (def.MaxMana > 0 && def.ManaRegen > 0.001f)
        {
            float fullTime = def.MaxMana / def.ManaRegen;
            _ui.DrawText($"(full in {fullTime:F1}s)", new Vector2(x + w - 110, curY + 2), EditorBase.TextDim);
        }
        curY += RowH;

        return curY;
    }

    // =========================================================================
    //  EQUIPMENT SECTION
    // =========================================================================

    private int DrawEquipmentSection(UnitDef def, int x, int y, int w)
    {
        int curY = y;
        DrawSectionHeader("Equipment", x, ref curY, w);

        // --- Weapons --- (RU33: show "DisplayName (id)" format in dropdowns)
        _ui.DrawText("Weapons", new Vector2(x, curY + 2), EditorBase.AccentColor);
        if (_ui.DrawButton("Edit Weapons", x + w - 110, curY, 100, 20))
            _activeSubEditor = SubEditor.Weapon;
        curY += RowH;

        BuildWeaponDropdownLists(out string[] weaponDisplayNames, out string[] weaponIdList);
        for (int i = 0; i < def.Weapons.Count; i++)
        {
            string wId = def.Weapons[i];
            var wDef = _gameData.Weapons.Get(wId);

            // U26: Weapon type tag with color [M] orange for melee, [R] blue for ranged
            if (wDef != null)
            {
                string tag = wDef.IsRanged ? "[R]" : "[M]";
                Color tagColor = wDef.IsRanged ? new Color(80, 140, 220) : new Color(220, 150, 50);
                _ui.DrawText(tag, new Vector2(x, curY + 2), tagColor);
            }
            string displayLabel = wDef != null ? $"     [{i}] {wDef.DisplayName}" : $"  [{i}]";

            // Weapon dropdown with display names
            string currentDisplay = MapIdToDisplay(wId, weaponDisplayNames, weaponIdList);
            string newDisplay = _ui.DrawCombo($"weap_{i}", displayLabel, currentDisplay, weaponDisplayNames, x, curY, w - 28, allowNone: true);
            if (newDisplay != currentDisplay)
            {
                def.Weapons[i] = string.IsNullOrEmpty(newDisplay) ? "" : MapDisplayToId(newDisplay, weaponDisplayNames, weaponIdList);
                _unsavedChanges = true;
            }

            // Remove button
            if (_ui.DrawButton("X", x + w - 24, curY, 22, 20, EditorBase.DangerColor))
            {
                def.Weapons.RemoveAt(i);
                _unsavedChanges = true;
                i--;
                curY += RowH;
                continue;
            }

            // RU34: Stat summary on the SAME row (to the right, after dropdown area)
            if (wDef != null)
            {
                string summary;
                if (wDef.IsRanged)
                    summary = $"R:d{wDef.RangedDamage} r{wDef.Range:F0} p{wDef.Precision}";
                else
                    summary = $"M:d{wDef.Damage} a{wDef.AttackBonus} d{wDef.DefenseBonus}";
                _ui.DrawText(summary, new Vector2(x + w - 180, curY + 2), EditorBase.TextDim);
            }
            curY += RowH;
        }
        if (def.Weapons.Count < 4)
        {
            if (_ui.DrawButton("+ Add Weapon", x + 10, curY, 100, 20))
            {
                def.Weapons.Add(weaponIdList.Length > 0 ? weaponIdList[0] : "");
                _unsavedChanges = true;
            }
            curY += RowH;
        }
        curY += 4;

        // --- Armors --- (RU33: show "DisplayName (id)" format in dropdowns)
        _ui.DrawText("Armors", new Vector2(x, curY + 2), EditorBase.AccentColor);
        if (_ui.DrawButton("Edit Armor", x + w - 110, curY, 100, 20))
            _activeSubEditor = SubEditor.Armor;
        curY += RowH;

        BuildArmorDropdownLists(out string[] armorDisplayNames, out string[] armorIdList);
        for (int i = 0; i < def.Armors.Count; i++)
        {
            string aId = def.Armors[i];
            var aDef = _gameData.Armors.Get(aId);
            string displayLabel = aDef != null ? $"  [{i}] {aDef.DisplayName}" : $"  [{i}]";

            string currentDisplay = MapIdToDisplay(aId, armorDisplayNames, armorIdList);
            string newDisplay = _ui.DrawCombo($"arm_{i}", displayLabel, currentDisplay, armorDisplayNames, x, curY, w - 28, allowNone: true);
            if (newDisplay != currentDisplay)
            {
                def.Armors[i] = string.IsNullOrEmpty(newDisplay) ? "" : MapDisplayToId(newDisplay, armorDisplayNames, armorIdList);
                _unsavedChanges = true;
            }

            if (_ui.DrawButton("X", x + w - 24, curY, 22, 20, EditorBase.DangerColor))
            {
                def.Armors.RemoveAt(i);
                _unsavedChanges = true;
                i--;
                curY += RowH;
                continue;
            }

            // RU34: Stat summary on the SAME row
            if (aDef != null)
            {
                string summary = $"B{aDef.BodyProtection} H{aDef.HeadProtection} E{aDef.Encumbrance}";
                _ui.DrawText(summary, new Vector2(x + w - 180, curY + 2), EditorBase.TextDim);
            }
            curY += RowH;
        }
        if (def.Armors.Count < 4)
        {
            if (_ui.DrawButton("+ Add Armor", x + 10, curY, 100, 20))
            {
                def.Armors.Add(armorIdList.Length > 0 ? armorIdList[0] : "");
                _unsavedChanges = true;
            }
            curY += RowH;
        }
        curY += 4;

        // --- Shields --- (RU33: show "DisplayName (id)" format in dropdowns)
        _ui.DrawText("Shields", new Vector2(x, curY + 2), EditorBase.AccentColor);
        if (_ui.DrawButton("Edit Shields", x + w - 110, curY, 100, 20))
            _activeSubEditor = SubEditor.Shield;
        curY += RowH;

        BuildShieldDropdownLists(out string[] shieldDisplayNames, out string[] shieldIdList);
        for (int i = 0; i < def.Shields.Count; i++)
        {
            string sId = def.Shields[i];
            var sDef = _gameData.Shields.Get(sId);
            string displayLabel = sDef != null ? $"  [{i}] {sDef.DisplayName}" : $"  [{i}]";

            string currentDisplay = MapIdToDisplay(sId, shieldDisplayNames, shieldIdList);
            string newDisplay = _ui.DrawCombo($"shld_{i}", displayLabel, currentDisplay, shieldDisplayNames, x, curY, w - 28, allowNone: true);
            if (newDisplay != currentDisplay)
            {
                def.Shields[i] = string.IsNullOrEmpty(newDisplay) ? "" : MapDisplayToId(newDisplay, shieldDisplayNames, shieldIdList);
                _unsavedChanges = true;
            }

            if (_ui.DrawButton("X", x + w - 24, curY, 22, 20, EditorBase.DangerColor))
            {
                def.Shields.RemoveAt(i);
                _unsavedChanges = true;
                i--;
                curY += RowH;
                continue;
            }

            // RU34: Stat summary on the SAME row
            if (sDef != null)
            {
                string summary = $"P{sDef.Protection} Pa{sDef.Parry} D{sDef.Defense}";
                _ui.DrawText(summary, new Vector2(x + w - 180, curY + 2), EditorBase.TextDim);
            }
            curY += RowH;
        }
        if (def.Shields.Count < 1)
        {
            if (_ui.DrawButton("+ Add Shield", x + 10, curY, 100, 20))
            {
                def.Shields.Add(shieldIdList.Length > 0 ? shieldIdList[0] : "");
                _unsavedChanges = true;
            }
            curY += RowH;
        }

        return curY;
    }

    // =========================================================================
    //  COLOR SECTION
    // =========================================================================

    private int DrawColorSection(UnitDef def, int x, int y, int w)
    {
        int curY = y;
        DrawSectionHeader("Color", x, ref curY, w);

        // Convert ColorJson? to HdrColor for the swatch
        HdrColor hdrColor;
        if (def.Color != null)
            hdrColor = new HdrColor((byte)def.Color.R, (byte)def.Color.G, (byte)def.Color.B, (byte)def.Color.A);
        else
            hdrColor = new HdrColor(255, 255, 255, 255);

        // Draw a clickable color swatch (swatch only, no inline fields - editing happens in the picker popup)
        bool changed = _ui.DrawColorSwatch("unit_color", x, curY, 40, 20, ref hdrColor);

        // Show read-only RGBA info next to swatch
        string info = $"({hdrColor.R},{hdrColor.G},{hdrColor.B},{hdrColor.A})";
        _ui.DrawText(info, new Vector2(x + 48, curY + 2), EditorBase.TextDim);

        if (changed)
        {
            def.Color = new ColorJson { R = hdrColor.R, G = hdrColor.G, B = hdrColor.B, A = hdrColor.A };
            _unsavedChanges = true;
        }
        curY += RowH + 4;

        return curY;
    }

    // =========================================================================
    //  GROUP EDITOR POPUP
    // =========================================================================

    private void DrawGroupEditor(int screenW, int screenH)
    {
        // Modal overlay
        _ui.DrawRect(new Rectangle(0, 0, screenW, screenH), new Color(0, 0, 0, 120));

        int popW = 700;
        int popH = 450;
        int popX = (screenW - popW) / 2;
        int popY = (screenH - popH) / 2;

        _ui.DrawPanel(popX, popY, popW, popH, "Unit Group Editor");

        // Close button
        if (_ui.DrawButton("X", popX + popW - 30, popY + 3, 24, 22, EditorBase.DangerColor))
        {
            _groupEditorOpen = false;
            return;
        }

        int contentY = popY + 32;
        int contentH = popH - 72;
        int listW = 200;

        // --- Left: group list ---
        int leftX = popX + 4;
        var groupIds = _gameData.UnitGroups.GetIDs();
        var groupDisplayItems = new List<string>();
        foreach (var id in groupIds)
        {
            var gDef = _gameData.UnitGroups.Get(id);
            groupDisplayItems.Add(gDef?.DisplayName ?? id);
        }

        int filteredGroupSelIdx = _groupSelectedIdx >= 0 && _groupSelectedIdx < groupIds.Count
            ? _groupSelectedIdx : -1;

        int clicked = _ui.DrawScrollableList("group_list", groupDisplayItems, filteredGroupSelIdx,
            leftX, contentY, listW, contentH, null);
        if (clicked >= 0 && clicked < groupIds.Count)
        {
            _groupSelectedIdx = clicked;
            _groupPropScroll = 0;
        }

        // --- Bottom CRUD buttons for groups ---
        int bottomY = popY + popH - 34;
        int bx = popX + 8;
        int btnW = 70;
        int btnH = 24;

        if (_ui.DrawButton("+ New", bx, bottomY, btnW, btnH))
        {
            string newId = "group_" + DateTime.Now.ToString("HHmmss");
            var newDef = new UnitGroupDef { Id = newId, DisplayName = "New Group" };
            _gameData.UnitGroups.Add(newDef);
            _groupSelectedIdx = IndexOf(_gameData.UnitGroups.GetIDs(), newId);
            _unsavedChanges = true;
            SetStatus("Added group: " + newId);
        }
        bx += btnW + 4;

        // RU31: Copy button between New and Delete
        if (_groupSelectedIdx >= 0 && _groupSelectedIdx < groupIds.Count)
        {
            if (_ui.DrawButton("Copy", bx, bottomY, btnW, btnH))
            {
                var srcGroup = _gameData.UnitGroups.Get(groupIds[_groupSelectedIdx]);
                if (srcGroup != null)
                {
                    string newId = srcGroup.Id + "_copy";
                    int suffix = 1;
                    while (_gameData.UnitGroups.Get(newId) != null)
                        newId = srcGroup.Id + "_copy" + (++suffix);
                    var newDef = new UnitGroupDef
                    {
                        Id = newId,
                        DisplayName = srcGroup.DisplayName + " (Copy)",
                        Entries = new List<UnitGroupEntry>()
                    };
                    foreach (var entry in srcGroup.Entries)
                        newDef.Entries.Add(new UnitGroupEntry { UnitDefID = entry.UnitDefID, Weight = entry.Weight });
                    _gameData.UnitGroups.AddAfter(newDef, srcGroup.Id);
                    _groupSelectedIdx = IndexOf(_gameData.UnitGroups.GetIDs(), newId);
                    _unsavedChanges = true;
                    SetStatus("Copied group: " + newId);
                }
            }
            bx += btnW + 4;

            if (_ui.DrawButton("Delete", bx, bottomY, btnW, btnH, EditorBase.DangerColor))
            {
                _confirmDeleteGroup = true;
                _confirmDeleteId = groupIds[_groupSelectedIdx];
            }
        }

        // Save button
        if (_ui.DrawButton("Save", popX + popW - 80, bottomY, 70, btnH, EditorBase.SuccessColor))
        {
            bool ok = _gameData.UnitGroups.Save("data/unit_groups.json");
            Core.GamePaths.DualSave("data/unit_groups.json");
            SetStatus(ok ? "Saved unit_groups.json" : "SAVE FAILED!");
        }

        // --- Right: group detail ---
        int rightX = popX + listW + 12;
        int rightW = popW - listW - 20;
        _ui.DrawRect(new Rectangle(rightX - 2, contentY, 1, contentH), EditorBase.PanelBorder);

        if (_groupSelectedIdx >= 0 && _groupSelectedIdx < groupIds.Count)
        {
            var gDef = _gameData.UnitGroups.Get(groupIds[_groupSelectedIdx]);
            if (gDef != null)
                DrawGroupDetail(gDef, rightX, contentY, rightW, contentH);
        }
    }

    private void DrawGroupDetail(UnitGroupDef g, int x, int y, int ww, int h)
    {
        // Handle scroll
        var area = new Rectangle(x, y, ww, h);
        if (area.Contains(_ui._mouse.X, _ui._mouse.Y))
        {
            int sd = _ui._mouse.ScrollWheelValue - _ui._prevMouse.ScrollWheelValue;
            if (sd != 0) { _groupPropScroll -= sd * 0.3f; _groupPropScroll = Math.Max(0, _groupPropScroll); }
        }

        int curY = y + 4 - (int)_groupPropScroll;

        // Name field
        string newName = _ui.DrawTextField("g_name", "Name", g.DisplayName, x, curY, ww);
        if (newName != g.DisplayName) { g.DisplayName = newName; _unsavedChanges = true; }
        curY += RowH;

        // ID (read-only)
        _ui.DrawText("ID", new Vector2(x, curY + 2), EditorBase.TextDim);
        _ui.DrawText(g.Id, new Vector2(x + 120, curY + 2), EditorBase.TextColor);
        curY += RowH;

        // Entries header
        _ui.DrawText("Entries:", new Vector2(x, curY + 2), EditorBase.AccentColor);
        curY += RowH;

        // RU32: Build unit dropdown with "DisplayName [id]" format
        BuildUnitDisplayDropdownLists(out string[] unitDisplayOptions, out string[] unitIdOptions);

        for (int i = 0; i < g.Entries.Count; i++)
        {
            var entry = g.Entries[i];

            // RU32: Unit ID dropdown with display names
            string currentDisplay = MapIdToDisplay(entry.UnitDefID, unitDisplayOptions, unitIdOptions);
            string newDisplay = _ui.DrawCombo($"ge_uid_{i}", $"  [{i}] Unit", currentDisplay, unitDisplayOptions, x, curY, ww - 28);
            if (newDisplay != currentDisplay)
            {
                entry.UnitDefID = MapDisplayToId(newDisplay, unitDisplayOptions, unitIdOptions);
                _unsavedChanges = true;
            }

            // Delete button
            if (_ui.DrawButton("X", x + ww - 24, curY, 22, 20, EditorBase.DangerColor))
            {
                g.Entries.RemoveAt(i);
                _unsavedChanges = true;
                i--;
                curY += RowH;
                continue;
            }
            curY += RowH;

            // Weight field
            float newWeight = _ui.DrawFloatField($"ge_wt_{i}", "    Weight", entry.Weight, x, curY, ww, 0.1f);
            if (Math.Abs(newWeight - entry.Weight) > 0.001f) { entry.Weight = newWeight; _unsavedChanges = true; }
            curY += RowH;
        }

        // Add entry button
        if (_ui.DrawButton("+ Add Unit", x + 10, curY, 100, 20))
        {
            g.Entries.Add(new UnitGroupEntry());
            _unsavedChanges = true;
        }
    }

    private string[] BuildUnitDropdownList()
    {
        var list = new List<string> { "" };
        foreach (var id in _gameData.Units.GetIDs())
            list.Add(id);
        return list.ToArray();
    }

    // RU32: Build unit dropdown with "DisplayName [id]" format and parallel ID list
    private void BuildUnitDisplayDropdownLists(out string[] displayNames, out string[] ids)
    {
        var dispList = new List<string> { "" };
        var idList = new List<string> { "" };
        foreach (var id in _gameData.Units.GetIDs())
        {
            var u = _gameData.Units.Get(id);
            string name = u?.DisplayName ?? "";
            dispList.Add(string.IsNullOrEmpty(name) ? id : $"{name} [{id}]");
            idList.Add(id);
        }
        displayNames = dispList.ToArray();
        ids = idList.ToArray();
    }

    // =========================================================================
    //  SUB-EDITOR POPUP (Weapon / Armor / Shield)
    // =========================================================================

    private void DrawSubEditor(int screenW, int screenH)
    {
        // Modal overlay
        _ui.DrawRect(new Rectangle(0, 0, screenW, screenH), new Color(0, 0, 0, 120));

        int popW = 800;
        int popH = 500;
        int popX = (screenW - popW) / 2;
        int popY = (screenH - popH) / 2;

        string title = _activeSubEditor switch
        {
            SubEditor.Weapon => "Weapon Editor",
            SubEditor.Armor => "Armor Editor",
            SubEditor.Shield => "Shield Editor",
            _ => "Editor"
        };

        _ui.DrawPanel(popX, popY, popW, popH, title);

        // Close button
        if (_ui.DrawButton("X", popX + popW - 30, popY + 3, 24, 22, EditorBase.DangerColor))
        {
            _activeSubEditor = SubEditor.None;
            _subSelectedIdx = -1;
            return;
        }

        int contentY = popY + 32;
        int contentH = popH - 72;
        int listW = 200;

        // --- Left: search + list ---
        int leftX = popX + 4;
        _subSearchFilter = _ui.DrawSearchField("sub_search", _subSearchFilter, leftX, contentY, listW);
        int listY = contentY + 26;
        int listH = contentH - 26;

        switch (_activeSubEditor)
        {
            case SubEditor.Weapon: DrawWeaponSubEditor(leftX, listY, listW, listH, popX, popW, contentY, contentH); break;
            case SubEditor.Armor: DrawArmorSubEditor(leftX, listY, listW, listH, popX, popW, contentY, contentH); break;
            case SubEditor.Shield: DrawShieldSubEditor(leftX, listY, listW, listH, popX, popW, contentY, contentH); break;
        }

        // --- Bottom CRUD buttons ---
        int bottomY = popY + popH - 34;
        DrawSubEditorCrudButtons(popX, bottomY, popW);
    }

    // ---- WEAPON SUB-EDITOR ----

    private void DrawWeaponSubEditor(int leftX, int listY, int listW, int listH,
        int popX, int popW, int contentY, int contentH)
    {
        var ids = _gameData.Weapons.GetIDs();
        var displayItems = new List<string>();
        var filteredIds = new List<string>();
        foreach (var id in ids)
        {
            var d = _gameData.Weapons.Get(id);
            string name = d?.DisplayName ?? id;
            if (!string.IsNullOrEmpty(_subSearchFilter) &&
                !id.Contains(_subSearchFilter, StringComparison.OrdinalIgnoreCase) &&
                !name.Contains(_subSearchFilter, StringComparison.OrdinalIgnoreCase))
                continue;
            filteredIds.Add(id);
            displayItems.Add(name);
        }

        int filteredSelIdx = _subSelectedIdx >= 0 && _subSelectedIdx < ids.Count
            ? filteredIds.IndexOf(ids[_subSelectedIdx]) : -1;

        int clicked = _ui.DrawScrollableList("sub_wlist", displayItems, filteredSelIdx,
            leftX, listY, listW, listH, null);
        if (clicked >= 0 && clicked < filteredIds.Count)
        {
            _subSelectedIdx = IndexOf(ids, filteredIds[clicked]);
            _subPropScroll = 0;
        }

        // --- Right: detail ---
        int rightX = popX + listW + 12;
        int rightW = popW - listW - 20;
        _ui.DrawRect(new Rectangle(rightX - 2, contentY, 1, contentH), EditorBase.PanelBorder);

        if (_subSelectedIdx >= 0 && _subSelectedIdx < ids.Count)
        {
            var wDef = _gameData.Weapons.Get(ids[_subSelectedIdx]);
            if (wDef != null)
                DrawWeaponDetail(wDef, rightX, contentY, rightW, contentH);
        }
    }

    private void DrawWeaponDetail(WeaponDef w, int x, int y, int ww, int h)
    {
        // Handle scroll
        var area = new Rectangle(x, y, ww, h);
        if (area.Contains(_ui._mouse.X, _ui._mouse.Y))
        {
            int sd = _ui._mouse.ScrollWheelValue - _ui._prevMouse.ScrollWheelValue;
            if (sd != 0) { _subPropScroll -= sd * 0.3f; _subPropScroll = Math.Max(0, _subPropScroll); }
        }

        int curY = y + 4 - (int)_subPropScroll;

        string newName = _ui.DrawTextField("w_name", "Name", w.DisplayName, x, curY, ww);
        if (newName != w.DisplayName) { w.DisplayName = newName; _unsavedChanges = true; }
        curY += RowH;

        // RU25: ID is read-only (registry key must not change)
        _ui.DrawText("ID", new Vector2(x, curY + 2), EditorBase.TextDim);
        _ui.DrawText(w.Id, new Vector2(x + 120, curY + 2), EditorBase.TextColor);
        curY += RowH;

        // U27: Melee/Ranged toggle buttons instead of checkbox
        {
            int toggleW = 80;
            int toggleH = 22;
            Color meleeColor = !w.IsRanged ? EditorBase.AccentColor : EditorBase.ButtonBg;
            Color rangedColor = w.IsRanged ? EditorBase.AccentColor : EditorBase.ButtonBg;
            _ui.DrawText("Type", new Vector2(x, curY + 2), EditorBase.TextDim);
            if (_ui.DrawButton("Melee", x + 120, curY, toggleW, toggleH, meleeColor))
            { w.IsRanged = false; _unsavedChanges = true; }
            if (_ui.DrawButton("Ranged", x + 120 + toggleW + 4, curY, toggleW, toggleH, rangedColor))
            { w.IsRanged = true; _unsavedChanges = true; }
        }
        curY += RowH;

        if (!w.IsRanged)
        {
            // Melee fields
            int newDmg = _ui.DrawIntField("w_dmg", "Damage", w.Damage, x, curY, ww);
            if (newDmg != w.Damage) { w.Damage = newDmg; _unsavedChanges = true; }
            curY += RowH;

            int newAtk = _ui.DrawIntField("w_atk", "Attack Bonus", w.AttackBonus, x, curY, ww);
            if (newAtk != w.AttackBonus) { w.AttackBonus = newAtk; _unsavedChanges = true; }
            curY += RowH;

            int newDefB = _ui.DrawIntField("w_defb", "Defense Bonus", w.DefenseBonus, x, curY, ww);
            if (newDefB != w.DefenseBonus) { w.DefenseBonus = newDefB; _unsavedChanges = true; }
            curY += RowH;

            int newLen = _ui.DrawIntField("w_len", "Length", w.Length, x, curY, ww);
            if (newLen != w.Length) { w.Length = newLen; _unsavedChanges = true; }
            curY += RowH;
        }
        else
        {
            // Ranged fields
            float newRange = _ui.DrawFloatField("w_rng", "Range", w.Range, x, curY, ww, 1.0f);
            if (Math.Abs(newRange - w.Range) > 0.001f) { w.Range = newRange; _unsavedChanges = true; }
            curY += RowH;

            float newDR = _ui.DrawFloatField("w_dr", "Direct Range", w.DirectRange, x, curY, ww, 1.0f);
            if (Math.Abs(newDR - w.DirectRange) > 0.001f) { w.DirectRange = newDR; _unsavedChanges = true; }
            curY += RowH;

            float newCD = _ui.DrawFloatField("w_cd", "Cooldown", w.Cooldown, x, curY, ww, 0.1f);
            if (Math.Abs(newCD - w.Cooldown) > 0.001f) { w.Cooldown = newCD; _unsavedChanges = true; }
            curY += RowH;

            int newRDmg = _ui.DrawIntField("w_rdmg", "Ranged Damage", w.RangedDamage, x, curY, ww);
            if (newRDmg != w.RangedDamage) { w.RangedDamage = newRDmg; _unsavedChanges = true; }
            curY += RowH;

            int newPrec = _ui.DrawIntField("w_prec", "Precision", w.Precision, x, curY, ww);
            if (newPrec != w.Precision) { w.Precision = newPrec; _unsavedChanges = true; }
            curY += RowH;

            // Projectile Type dropdown
            string[] projTypes = Enum.GetNames<ProjectileType>();
            string newProjType = _ui.DrawCombo("w_projtype", "Projectile", w.ProjectileType, projTypes, x, curY, ww);
            if (newProjType != w.ProjectileType) { w.ProjectileType = newProjType; _unsavedChanges = true; }
            curY += RowH;

            // Also show melee stats (weapons can have both)
            int newDmg2 = _ui.DrawIntField("w_dmg2", "Melee Damage", w.Damage, x, curY, ww);
            if (newDmg2 != w.Damage) { w.Damage = newDmg2; _unsavedChanges = true; }
            curY += RowH;

            int newAtk2 = _ui.DrawIntField("w_atk2", "Attack Bonus", w.AttackBonus, x, curY, ww);
            if (newAtk2 != w.AttackBonus) { w.AttackBonus = newAtk2; _unsavedChanges = true; }
            curY += RowH;

            int newDefB2 = _ui.DrawIntField("w_defb2", "Defense Bonus", w.DefenseBonus, x, curY, ww);
            if (newDefB2 != w.DefenseBonus) { w.DefenseBonus = newDefB2; _unsavedChanges = true; }
            curY += RowH;
        }

        // Bonuses
        _ui.DrawText("Bonuses:", new Vector2(x, curY + 2), EditorBase.AccentColor);
        curY += RowH;
        string[] bonusOptions = Enum.GetNames<WeaponBonus>();
        for (int i = 0; i < w.Bonuses.Count; i++)
        {
            string newB = _ui.DrawCombo($"wb_{i}", $"  [{i}]", w.Bonuses[i], bonusOptions, x, curY, ww - 28);
            if (newB != w.Bonuses[i]) { w.Bonuses[i] = newB; _unsavedChanges = true; }
            if (_ui.DrawButton("X", x + ww - 24, curY, 22, 20, EditorBase.DangerColor))
            {
                w.Bonuses.RemoveAt(i); i--; _unsavedChanges = true;
                curY += RowH; continue;
            }
            curY += RowH;
        }
        if (_ui.DrawButton("+ Bonus", x, curY, 80, 20))
        {
            w.Bonuses.Add(bonusOptions.Length > 0 ? bonusOptions[0] : "");
            _unsavedChanges = true;
        }
    }

    // ---- ARMOR SUB-EDITOR ----

    private void DrawArmorSubEditor(int leftX, int listY, int listW, int listH,
        int popX, int popW, int contentY, int contentH)
    {
        var ids = _gameData.Armors.GetIDs();
        var displayItems = new List<string>();
        var filteredIds = new List<string>();
        foreach (var id in ids)
        {
            var d = _gameData.Armors.Get(id);
            string name = d?.DisplayName ?? id;
            if (!string.IsNullOrEmpty(_subSearchFilter) &&
                !id.Contains(_subSearchFilter, StringComparison.OrdinalIgnoreCase) &&
                !name.Contains(_subSearchFilter, StringComparison.OrdinalIgnoreCase))
                continue;
            filteredIds.Add(id);
            displayItems.Add(name);
        }

        int filteredSelIdx = _subSelectedIdx >= 0 && _subSelectedIdx < ids.Count
            ? filteredIds.IndexOf(ids[_subSelectedIdx]) : -1;

        int clicked = _ui.DrawScrollableList("sub_alist", displayItems, filteredSelIdx,
            leftX, listY, listW, listH, null);
        if (clicked >= 0 && clicked < filteredIds.Count)
        {
            _subSelectedIdx = IndexOf(ids, filteredIds[clicked]);
            _subPropScroll = 0;
        }

        int rightX = popX + listW + 12;
        int rightW = popW - listW - 20;
        _ui.DrawRect(new Rectangle(rightX - 2, contentY, 1, contentH), EditorBase.PanelBorder);

        if (_subSelectedIdx >= 0 && _subSelectedIdx < ids.Count)
        {
            var aDef = _gameData.Armors.Get(ids[_subSelectedIdx]);
            if (aDef != null)
                DrawArmorDetail(aDef, rightX, contentY, rightW, contentH);
        }
    }

    private void DrawArmorDetail(ArmorDef a, int x, int y, int ww, int h)
    {
        var area = new Rectangle(x, y, ww, h);
        if (area.Contains(_ui._mouse.X, _ui._mouse.Y))
        {
            int sd = _ui._mouse.ScrollWheelValue - _ui._prevMouse.ScrollWheelValue;
            if (sd != 0) { _subPropScroll -= sd * 0.3f; _subPropScroll = Math.Max(0, _subPropScroll); }
        }

        int curY = y + 4 - (int)_subPropScroll;

        string newName = _ui.DrawTextField("a_name", "Name", a.DisplayName, x, curY, ww);
        if (newName != a.DisplayName) { a.DisplayName = newName; _unsavedChanges = true; }
        curY += RowH;

        // RU25: ID is read-only (registry key must not change)
        _ui.DrawText("ID", new Vector2(x, curY + 2), EditorBase.TextDim);
        _ui.DrawText(a.Id, new Vector2(x + 120, curY + 2), EditorBase.TextColor);
        curY += RowH;

        int newBody = _ui.DrawIntField("a_body", "Body Prot", a.BodyProtection, x, curY, ww);
        if (newBody != a.BodyProtection) { a.BodyProtection = newBody; _unsavedChanges = true; }
        curY += RowH;

        int newHead = _ui.DrawIntField("a_head", "Head Prot", a.HeadProtection, x, curY, ww);
        if (newHead != a.HeadProtection) { a.HeadProtection = newHead; _unsavedChanges = true; }
        curY += RowH;

        int newEnc = _ui.DrawIntField("a_enc", "Encumbrance", a.Encumbrance, x, curY, ww);
        if (newEnc != a.Encumbrance) { a.Encumbrance = newEnc; _unsavedChanges = true; }
        curY += RowH;

        // Bonuses
        _ui.DrawText("Bonuses:", new Vector2(x, curY + 2), EditorBase.AccentColor);
        curY += RowH;
        string[] bonusOptions = Enum.GetNames<ArmorBonus>();
        for (int i = 0; i < a.Bonuses.Count; i++)
        {
            string newB = _ui.DrawCombo($"ab_{i}", $"  [{i}]", a.Bonuses[i], bonusOptions, x, curY, ww - 28);
            if (newB != a.Bonuses[i]) { a.Bonuses[i] = newB; _unsavedChanges = true; }
            if (_ui.DrawButton("X", x + ww - 24, curY, 22, 20, EditorBase.DangerColor))
            {
                a.Bonuses.RemoveAt(i); i--; _unsavedChanges = true;
                curY += RowH; continue;
            }
            curY += RowH;
        }
        if (_ui.DrawButton("+ Bonus", x, curY, 80, 20))
        {
            a.Bonuses.Add(bonusOptions.Length > 0 ? bonusOptions[0] : "");
            _unsavedChanges = true;
        }
    }

    // ---- SHIELD SUB-EDITOR ----

    private void DrawShieldSubEditor(int leftX, int listY, int listW, int listH,
        int popX, int popW, int contentY, int contentH)
    {
        var ids = _gameData.Shields.GetIDs();
        var displayItems = new List<string>();
        var filteredIds = new List<string>();
        foreach (var id in ids)
        {
            var d = _gameData.Shields.Get(id);
            string name = d?.DisplayName ?? id;
            if (!string.IsNullOrEmpty(_subSearchFilter) &&
                !id.Contains(_subSearchFilter, StringComparison.OrdinalIgnoreCase) &&
                !name.Contains(_subSearchFilter, StringComparison.OrdinalIgnoreCase))
                continue;
            filteredIds.Add(id);
            displayItems.Add(name);
        }

        int filteredSelIdx = _subSelectedIdx >= 0 && _subSelectedIdx < ids.Count
            ? filteredIds.IndexOf(ids[_subSelectedIdx]) : -1;

        int clicked = _ui.DrawScrollableList("sub_slist", displayItems, filteredSelIdx,
            leftX, listY, listW, listH, null);
        if (clicked >= 0 && clicked < filteredIds.Count)
        {
            _subSelectedIdx = IndexOf(ids, filteredIds[clicked]);
            _subPropScroll = 0;
        }

        int rightX = popX + listW + 12;
        int rightW = popW - listW - 20;
        _ui.DrawRect(new Rectangle(rightX - 2, contentY, 1, contentH), EditorBase.PanelBorder);

        if (_subSelectedIdx >= 0 && _subSelectedIdx < ids.Count)
        {
            var sDef = _gameData.Shields.Get(ids[_subSelectedIdx]);
            if (sDef != null)
                DrawShieldDetail(sDef, rightX, contentY, rightW, contentH);
        }
    }

    private void DrawShieldDetail(ShieldDef s, int x, int y, int ww, int h)
    {
        var area = new Rectangle(x, y, ww, h);
        if (area.Contains(_ui._mouse.X, _ui._mouse.Y))
        {
            int sd = _ui._mouse.ScrollWheelValue - _ui._prevMouse.ScrollWheelValue;
            if (sd != 0) { _subPropScroll -= sd * 0.3f; _subPropScroll = Math.Max(0, _subPropScroll); }
        }

        int curY = y + 4 - (int)_subPropScroll;

        string newName = _ui.DrawTextField("s_name", "Name", s.DisplayName, x, curY, ww);
        if (newName != s.DisplayName) { s.DisplayName = newName; _unsavedChanges = true; }
        curY += RowH;

        // RU25: ID is read-only (registry key must not change)
        _ui.DrawText("ID", new Vector2(x, curY + 2), EditorBase.TextDim);
        _ui.DrawText(s.Id, new Vector2(x + 120, curY + 2), EditorBase.TextColor);
        curY += RowH;

        int newProt = _ui.DrawIntField("s_prot", "Protection", s.Protection, x, curY, ww);
        if (newProt != s.Protection) { s.Protection = newProt; _unsavedChanges = true; }
        curY += RowH;

        int newParry = _ui.DrawIntField("s_parry", "Parry", s.Parry, x, curY, ww);
        if (newParry != s.Parry) { s.Parry = newParry; _unsavedChanges = true; }
        curY += RowH;

        int newDef = _ui.DrawIntField("s_def", "Defense", s.Defense, x, curY, ww);
        if (newDef != s.Defense) { s.Defense = newDef; _unsavedChanges = true; }
        curY += RowH;
    }

    // ---- SUB-EDITOR CRUD ----

    private void DrawSubEditorCrudButtons(int popX, int bottomY, int popW)
    {
        int bx = popX + 8;
        int btnW = 70;
        int btnH = 24;

        // Apply & Close button (bottom-right area)
        if (_ui.DrawButton("Apply & Close", popX + popW - 190, bottomY, 100, btnH, EditorBase.AccentColor))
        {
            _activeSubEditor = SubEditor.None;
            _subSelectedIdx = -1;
            return;
        }

        switch (_activeSubEditor)
        {
            case SubEditor.Weapon:
            {
                if (_ui.DrawButton("+ New", bx, bottomY, btnW, btnH))
                {
                    string newId = "weapon_" + DateTime.Now.ToString("HHmmss");
                    var newDef = new WeaponDef { Id = newId, DisplayName = "New Weapon" };
                    _gameData.Weapons.Add(newDef);
                    _subSelectedIdx = IndexOf(_gameData.Weapons.GetIDs(), newId);
                    _unsavedChanges = true;
                    SetStatus("Added weapon: " + newId);
                }
                bx += btnW + 4;

                var wIds = _gameData.Weapons.GetIDs();
                if (_subSelectedIdx >= 0 && _subSelectedIdx < wIds.Count)
                {
                    // Copy button
                    if (_ui.DrawButton("Copy", bx, bottomY, btnW, btnH))
                    {
                        var srcW = _gameData.Weapons.Get(wIds[_subSelectedIdx]);
                        if (srcW != null)
                        {
                            string newId = srcW.Id + "_copy";
                            int suffix = 1;
                            while (_gameData.Weapons.Get(newId) != null)
                                newId = srcW.Id + "_copy" + (++suffix);
                            var newDef = CloneWeapon(srcW, newId);
                            _gameData.Weapons.AddAfter(newDef, srcW.Id);
                            _subSelectedIdx = IndexOf(_gameData.Weapons.GetIDs(), newId);
                            _unsavedChanges = true;
                            SetStatus("Copied weapon: " + newId);
                        }
                    }
                    bx += btnW + 4;

                    // Delete button with confirmation if referenced
                    if (_ui.DrawButton("Delete", bx, bottomY, btnW, btnH, EditorBase.DangerColor))
                    {
                        string removeId = wIds[_subSelectedIdx];
                        int refCount = _gameData.Units.CountUnitsWithWeapon(removeId);
                        if (refCount > 0)
                        {
                            _confirmDeleteOpen = true;
                            _confirmDeleteId = removeId;
                            _confirmDeleteType = SubEditor.Weapon;
                        }
                        else
                        {
                            _gameData.Weapons.Remove(removeId);
                            _subSelectedIdx = Math.Min(_subSelectedIdx, _gameData.Weapons.Count - 1);
                            _unsavedChanges = true;
                            SetStatus("Removed weapon: " + removeId);
                        }
                    }
                }

                // Save
                if (_ui.DrawButton("Save", popX + popW - 80, bottomY, 70, btnH, EditorBase.SuccessColor))
                {
                    bool ok = _gameData.Weapons.Save("data/weapons.json");
                    Core.GamePaths.DualSave("data/weapons.json");
                    SetStatus(ok ? "Saved weapons.json" : "SAVE FAILED!");
                }
                break;
            }
            case SubEditor.Armor:
            {
                if (_ui.DrawButton("+ New", bx, bottomY, btnW, btnH))
                {
                    string newId = "armor_" + DateTime.Now.ToString("HHmmss");
                    var newDef = new ArmorDef { Id = newId, DisplayName = "New Armor" };
                    _gameData.Armors.Add(newDef);
                    _subSelectedIdx = IndexOf(_gameData.Armors.GetIDs(), newId);
                    _unsavedChanges = true;
                    SetStatus("Added armor: " + newId);
                }
                bx += btnW + 4;

                var aIds = _gameData.Armors.GetIDs();
                if (_subSelectedIdx >= 0 && _subSelectedIdx < aIds.Count)
                {
                    // Copy button
                    if (_ui.DrawButton("Copy", bx, bottomY, btnW, btnH))
                    {
                        var srcA = _gameData.Armors.Get(aIds[_subSelectedIdx]);
                        if (srcA != null)
                        {
                            string newId = srcA.Id + "_copy";
                            int suffix = 1;
                            while (_gameData.Armors.Get(newId) != null)
                                newId = srcA.Id + "_copy" + (++suffix);
                            var newDef = CloneArmor(srcA, newId);
                            _gameData.Armors.AddAfter(newDef, srcA.Id);
                            _subSelectedIdx = IndexOf(_gameData.Armors.GetIDs(), newId);
                            _unsavedChanges = true;
                            SetStatus("Copied armor: " + newId);
                        }
                    }
                    bx += btnW + 4;

                    // Delete button with confirmation if referenced
                    if (_ui.DrawButton("Delete", bx, bottomY, btnW, btnH, EditorBase.DangerColor))
                    {
                        string removeId = aIds[_subSelectedIdx];
                        int refCount = _gameData.Units.CountUnitsWithArmor(removeId);
                        if (refCount > 0)
                        {
                            _confirmDeleteOpen = true;
                            _confirmDeleteId = removeId;
                            _confirmDeleteType = SubEditor.Armor;
                        }
                        else
                        {
                            _gameData.Armors.Remove(removeId);
                            _subSelectedIdx = Math.Min(_subSelectedIdx, _gameData.Armors.Count - 1);
                            _unsavedChanges = true;
                            SetStatus("Removed armor: " + removeId);
                        }
                    }
                }

                if (_ui.DrawButton("Save", popX + popW - 80, bottomY, 70, btnH, EditorBase.SuccessColor))
                {
                    bool ok = _gameData.Armors.Save("data/armor.json");
                    Core.GamePaths.DualSave("data/armor.json");
                    SetStatus(ok ? "Saved armor.json" : "SAVE FAILED!");
                }
                break;
            }
            case SubEditor.Shield:
            {
                if (_ui.DrawButton("+ New", bx, bottomY, btnW, btnH))
                {
                    string newId = "shield_" + DateTime.Now.ToString("HHmmss");
                    var newDef = new ShieldDef { Id = newId, DisplayName = "New Shield" };
                    _gameData.Shields.Add(newDef);
                    _subSelectedIdx = IndexOf(_gameData.Shields.GetIDs(), newId);
                    _unsavedChanges = true;
                    SetStatus("Added shield: " + newId);
                }
                bx += btnW + 4;

                var sIds = _gameData.Shields.GetIDs();
                if (_subSelectedIdx >= 0 && _subSelectedIdx < sIds.Count)
                {
                    // Copy button
                    if (_ui.DrawButton("Copy", bx, bottomY, btnW, btnH))
                    {
                        var srcS = _gameData.Shields.Get(sIds[_subSelectedIdx]);
                        if (srcS != null)
                        {
                            string newId = srcS.Id + "_copy";
                            int suffix = 1;
                            while (_gameData.Shields.Get(newId) != null)
                                newId = srcS.Id + "_copy" + (++suffix);
                            var newDef = CloneShield(srcS, newId);
                            _gameData.Shields.AddAfter(newDef, srcS.Id);
                            _subSelectedIdx = IndexOf(_gameData.Shields.GetIDs(), newId);
                            _unsavedChanges = true;
                            SetStatus("Copied shield: " + newId);
                        }
                    }
                    bx += btnW + 4;

                    // Delete button with confirmation if referenced
                    if (_ui.DrawButton("Delete", bx, bottomY, btnW, btnH, EditorBase.DangerColor))
                    {
                        string removeId = sIds[_subSelectedIdx];
                        int refCount = _gameData.Units.CountUnitsWithShield(removeId);
                        if (refCount > 0)
                        {
                            _confirmDeleteOpen = true;
                            _confirmDeleteId = removeId;
                            _confirmDeleteType = SubEditor.Shield;
                        }
                        else
                        {
                            _gameData.Shields.Remove(removeId);
                            _subSelectedIdx = Math.Min(_subSelectedIdx, _gameData.Shields.Count - 1);
                            _unsavedChanges = true;
                            SetStatus("Removed shield: " + removeId);
                        }
                    }
                }

                if (_ui.DrawButton("Save", popX + popW - 80, bottomY, 70, btnH, EditorBase.SuccessColor))
                {
                    bool ok = _gameData.Shields.Save("data/shields.json");
                    Core.GamePaths.DualSave("data/shields.json");
                    SetStatus(ok ? "Saved shields.json" : "SAVE FAILED!");
                }
                break;
            }
        }
    }

    // =========================================================================
    //  SECTION HEADER
    // =========================================================================

    private void DrawSectionHeader(string text, int x, ref int y, int w)
    {
        _ui.DrawRect(new Rectangle(x, y, w, 22), EditorBase.PanelHeader);
        _ui.DrawText(text, new Vector2(x + 6, y + 3), EditorBase.TextBright);
        y += 24;
    }

    // =========================================================================
    //  HELPERS
    // =========================================================================

    private void SaveAll()
    {
        bool ok = _gameData.Save("data");
        _unsavedChanges = !ok;
        SetStatus(ok ? "Saved all game data" : "SAVE FAILED!");
    }

    private void SetStatus(string msg)
    {
        _statusMessage = msg;
        _statusTimer = 3f;
    }

    private void SyncPreviewToSelected()
    {
        var allIds = _gameData.Units.GetIDs();
        if (_selectedIdx < 0 || _selectedIdx >= allIds.Count) return;
        var def = _gameData.Units.Get(allIds[_selectedIdx]);
        if (def?.Sprite == null) return;

        _previewAtlas = def.Sprite.AtlasName;
        _previewSprite = def.Sprite.SpriteName;
        _previewAnimName = "Idle";
        _previewPlaying = true;

        // Force re-init on next DrawPreviewSprite by clearing tracking state
        _lastPreviewSpriteData = null;
        _lastPreviewAnimName = "";

        // Re-init animation controller
        var atlasId = AtlasDefs.ResolveAtlasName(_previewAtlas);
        if ((int)atlasId < _atlases.Length && _atlases[(int)atlasId].IsLoaded)
        {
            var spriteData = _atlases[(int)atlasId].GetUnit(_previewSprite);
            _previewAnim.Init(spriteData);
            _lastPreviewSpriteData = spriteData;
        }
    }

    private string[] GetUnitNamesFromAtlas(string atlasName)
    {
        if (string.IsNullOrEmpty(atlasName)) return new[] { "" };
        var atlasId = AtlasDefs.ResolveAtlasName(atlasName);
        if ((int)atlasId >= _atlases.Length || !_atlases[(int)atlasId].IsLoaded)
            return new[] { "" };
        var names = new List<string> { "" };
        names.AddRange(_atlases[(int)atlasId].Units.Keys);
        return names.ToArray();
    }

    private string[] GetAnimNamesForSprite(string atlasName, string spriteName)
    {
        if (string.IsNullOrEmpty(atlasName) || string.IsNullOrEmpty(spriteName))
            return new[] { "Idle" };
        var atlasId = AtlasDefs.ResolveAtlasName(atlasName);
        if ((int)atlasId >= _atlases.Length || !_atlases[(int)atlasId].IsLoaded)
            return new[] { "Idle" };
        var spriteData = _atlases[(int)atlasId].GetUnit(spriteName);
        if (spriteData == null) return new[] { "Idle" };
        var names = new List<string>(spriteData.Animations.Keys);
        if (names.Count == 0) names.Add("Idle");
        return names.ToArray();
    }

    private string[] BuildZombieTypeList()
    {
        var list = new List<string>();
        // U16: Section header for units
        list.Add("-- Units --");
        foreach (var id in _gameData.Units.GetIDs())
        {
            list.Add(id); // RU29: include all units, not just Undead
        }
        // U16: Section header for groups — RU30: prefix with "Group: " and show display name
        var groupIds = _gameData.UnitGroups.GetIDs();
        if (groupIds.Count > 0)
        {
            list.Add("-- Groups --");
            foreach (var gId in groupIds)
            {
                var gDef = _gameData.UnitGroups.Get(gId);
                string displayName = gDef?.DisplayName ?? gId;
                list.Add($"Group: {displayName} [{gId}]");
            }
        }
        return list.ToArray();
    }

    private string[] BuildSpellDropdownList()
    {
        var list = new List<string>();
        foreach (var id in _gameData.Spells.GetIDs())
            list.Add(id);
        return list.ToArray();
    }

    // U17: Build spell dropdown with "DisplayName (id)" format
    private string[] BuildSpellDropdownDisplayList()
    {
        var list = new List<string>();
        foreach (var id in _gameData.Spells.GetIDs())
        {
            var spell = _gameData.Spells.Get(id);
            string displayName = spell?.DisplayName ?? "";
            list.Add(string.IsNullOrEmpty(displayName) ? id : $"{displayName} ({id})");
        }
        return list.ToArray();
    }

    // RU33: Build weapon dropdown with display names and parallel ID list
    private void BuildWeaponDropdownLists(out string[] displayNames, out string[] ids)
    {
        var dispList = new List<string>();
        var idList = new List<string>();
        foreach (var id in _gameData.Weapons.GetIDs())
        {
            var w = _gameData.Weapons.Get(id);
            string name = w?.DisplayName ?? "";
            dispList.Add(string.IsNullOrEmpty(name) ? id : $"{name} ({id})");
            idList.Add(id);
        }
        displayNames = dispList.ToArray();
        ids = idList.ToArray();
    }

    private void BuildArmorDropdownLists(out string[] displayNames, out string[] ids)
    {
        var dispList = new List<string>();
        var idList = new List<string>();
        foreach (var id in _gameData.Armors.GetIDs())
        {
            var a = _gameData.Armors.Get(id);
            string name = a?.DisplayName ?? "";
            dispList.Add(string.IsNullOrEmpty(name) ? id : $"{name} ({id})");
            idList.Add(id);
        }
        displayNames = dispList.ToArray();
        ids = idList.ToArray();
    }

    private void BuildShieldDropdownLists(out string[] displayNames, out string[] ids)
    {
        var dispList = new List<string>();
        var idList = new List<string>();
        foreach (var id in _gameData.Shields.GetIDs())
        {
            var s = _gameData.Shields.Get(id);
            string name = s?.DisplayName ?? "";
            dispList.Add(string.IsNullOrEmpty(name) ? id : $"{name} ({id})");
            idList.Add(id);
        }
        displayNames = dispList.ToArray();
        ids = idList.ToArray();
    }

    /// <summary>Find an ID in a parallel array by matching the display string, return the corresponding ID.</summary>
    private static string MapDisplayToId(string displayValue, string[] displayNames, string[] ids)
    {
        for (int i = 0; i < displayNames.Length; i++)
        {
            if (displayNames[i] == displayValue)
                return i < ids.Length ? ids[i] : displayValue;
        }
        return displayValue; // fallback
    }

    /// <summary>Find the display name for a raw ID.</summary>
    private static string MapIdToDisplay(string id, string[] displayNames, string[] ids)
    {
        for (int i = 0; i < ids.Length; i++)
        {
            if (ids[i] == id)
                return i < displayNames.Length ? displayNames[i] : id;
        }
        return id; // fallback
    }

    private static AnimState NameToAnimState(string name)
    {
        foreach (AnimState state in Enum.GetValues<AnimState>())
        {
            if (AnimController.StateToAnimName(state) == name)
                return state;
        }
        return AnimState.Idle;
    }

    private static UnitDef CloneUnit(UnitDef src, string newId)
    {
        var def = new UnitDef
        {
            Id = newId,
            DisplayName = src.DisplayName + " (Copy)",
            UnitType = src.UnitType,
            Faction = src.Faction,
            AI = src.AI,
            OrcaPriority = src.OrcaPriority,
            Size = src.Size,
            Radius = src.Radius,
            SpriteScale = src.SpriteScale,
            SpriteWorldHeight = src.SpriteWorldHeight,
            ZombieTypeID = src.ZombieTypeID,
            SpellID = src.SpellID,
            MaxMana = src.MaxMana,
            ManaRegen = src.ManaRegen,
            Weapons = new List<string>(src.Weapons),
            Armors = new List<string>(src.Armors),
            Shields = new List<string>(src.Shields),
        };

        if (src.Stats != null)
        {
            def.Stats = new UnitStatsJson
            {
                MaxHP = src.Stats.MaxHP,
                Strength = src.Stats.Strength,
                Attack = src.Stats.Attack,
                Defense = src.Stats.Defense,
                MagicResist = src.Stats.MagicResist,
                Encumbrance = src.Stats.Encumbrance,
                NaturalProt = src.Stats.NaturalProt,
                CombatSpeed = src.Stats.CombatSpeed,
            };
        }

        if (src.Color != null)
        {
            def.Color = new ColorJson { R = src.Color.R, G = src.Color.G, B = src.Color.B, A = src.Color.A };
        }

        if (src.Sprite != null)
        {
            def.Sprite = new SpriteRef { AtlasName = src.Sprite.AtlasName, SpriteName = src.Sprite.SpriteName };
        }

        // Clone weapon points
        foreach (var (animKey, yawDict) in src.WeaponPoints)
        {
            var newYawDict = new Dictionary<string, List<WeaponFrameData>>();
            foreach (var (yawKey, frames) in yawDict)
            {
                var newFrames = new List<WeaponFrameData>();
                foreach (var f in frames)
                {
                    newFrames.Add(new WeaponFrameData
                    {
                        Hilt = new WeaponPointData { X = f.Hilt.X, Y = f.Hilt.Y, Behind = f.Hilt.Behind },
                        Tip = new WeaponPointData { X = f.Tip.X, Y = f.Tip.Y, Behind = f.Tip.Behind },
                    });
                }
                newYawDict[yawKey] = newFrames;
            }
            def.WeaponPoints[animKey] = newYawDict;
        }

        // Clone anim timings
        foreach (var (animKey, timing) in src.AnimTimings)
        {
            def.AnimTimings[animKey] = new UnitAnimTimingOverride
            {
                FrameDurationsMs = new List<int>(timing.FrameDurationsMs),
                EffectTimeMs = timing.EffectTimeMs,
            };
        }

        return def;
    }

    private static WeaponDef CloneWeapon(WeaponDef src, string newId)
    {
        return new WeaponDef
        {
            Id = newId,
            DisplayName = src.DisplayName + " (Copy)",
            Damage = src.Damage,
            AttackBonus = src.AttackBonus,
            DefenseBonus = src.DefenseBonus,
            Length = src.Length,
            IsRanged = src.IsRanged,
            Range = src.Range,
            DirectRange = src.DirectRange,
            Cooldown = src.Cooldown,
            RangedDamage = src.RangedDamage,
            Precision = src.Precision,
            ProjectileType = src.ProjectileType,
            Bonuses = new List<string>(src.Bonuses),
        };
    }

    private static ArmorDef CloneArmor(ArmorDef src, string newId)
    {
        return new ArmorDef
        {
            Id = newId,
            DisplayName = src.DisplayName + " (Copy)",
            BodyProtection = src.BodyProtection,
            HeadProtection = src.HeadProtection,
            Encumbrance = src.Encumbrance,
            Bonuses = new List<string>(src.Bonuses),
        };
    }

    private static ShieldDef CloneShield(ShieldDef src, string newId)
    {
        return new ShieldDef
        {
            Id = newId,
            DisplayName = src.DisplayName + " (Copy)",
            Protection = src.Protection,
            Parry = src.Parry,
            Defense = src.Defense,
        };
    }

    private static int IndexOf(IReadOnlyList<string> list, string value)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i] == value) return i;
        return -1;
    }
}
