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
    private string _searchFilter = "";
    private int _factionTab; // 0=All, 1=Undead, 2=Human
    private List<string> _filteredIds = new();

    // --- Right panel scroll ---
    private float _propScrollOffset;
    private float _maxPropHeight;

    // --- Sprite preview ---
    private AnimController _previewAnim = new();
    private bool _previewPlaying = true;
    private float _previewAngle = 60f; // default facing angle
    private string _previewAtlas = "";
    private string _previewSprite = "";
    private string _previewAnimName = "Idle";

    // --- Status ---
    private string _statusMessage = "";
    private float _statusTimer;
    private bool _unsavedChanges;

    // --- Equipment sub-editor popup state ---
    private enum SubEditor { None, Weapon, Armor, Shield }
    private SubEditor _activeSubEditor = SubEditor.None;
    private int _subSelectedIdx = -1;
    private string _subSearchFilter = "";
    private float _subPropScroll;

    // --- Constants ---
    private const int LeftPanelW = 300;
    private const int TabH = 24;
    private const int RowH = 24;
    private const int SearchH = 24;

    // --- Faction filter labels ---
    private static readonly string[] FactionTabs = { "All", "Undead", "Human" };

    // --- AI behavior names ---
    private static readonly string[] AINames = Enum.GetNames<AIBehavior>();

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

    public void SetAtlases(SpriteAtlas[] atlases)
    {
        _atlases = atlases;
    }

    // =========================================================================
    //  MAIN DRAW
    // =========================================================================

    public void Draw(int screenW, int screenH, GameTime gameTime)
    {
        if (_gameData == null) return;

        float dt = (float)gameTime.ElapsedGameTime.TotalSeconds;
        if (_statusTimer > 0) _statusTimer -= dt;

        // --- Dark overlay ---
        _ui.DrawRect(new Rectangle(0, 0, screenW, screenH), new Color(0, 0, 0, 160));

        int panelX = 0;
        int panelY = 0;
        int panelW = screenW;
        int panelH = screenH;

        // --- Top bar ---
        int topBarH = 30;
        _ui.DrawRect(new Rectangle(panelX, panelY, panelW, topBarH), EditorBase.PanelHeader);
        _ui.DrawText("Unit Editor (F9)", new Vector2(panelX + 10, panelY + 6), EditorBase.TextBright, null);

        // Unsaved indicator
        if (_unsavedChanges)
            _ui.DrawText("*UNSAVED*", new Vector2(panelX + 200, panelY + 6), EditorBase.DangerColor);

        // Save button in top bar
        if (_ui.DrawButton("Save All (Ctrl+S)", panelX + panelW - 160, panelY + 3, 150, 24, EditorBase.SuccessColor))
            SaveAll();

        // Ctrl+S
        if (_ui._kb.IsKeyDown(Keys.LeftControl) && _ui._kb.IsKeyDown(Keys.S) &&
            !(_ui._prevKb.IsKeyDown(Keys.LeftControl) && _ui._prevKb.IsKeyDown(Keys.S)))
            SaveAll();

        // Status message in top bar
        if (_statusTimer > 0 && !string.IsNullOrEmpty(_statusMessage))
        {
            Color sc = _statusMessage.Contains("FAIL") ? EditorBase.DangerColor : EditorBase.SuccessColor;
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

        // --- Build display strings with faction color dots ---
        var displayItems = new List<string>();
        foreach (var id in _filteredIds)
        {
            var def = _gameData.Units.Get(id);
            string name = def?.DisplayName ?? id;
            string fChar = def?.Faction?.Length > 0 ? def.Faction[..1] : "?";
            displayItems.Add($"[{fChar}] {name}");
        }

        // --- Draw scrollable list ---
        int listH = h - (curY - y) - 36; // room for bottom buttons
        int clicked = _ui.DrawScrollableList("unit_list", displayItems, filteredSelectedIdx,
            x + 4, curY, LeftPanelW - 8, listH, null);

        if (clicked >= 0 && clicked < _filteredIds.Count)
        {
            string clickedId = _filteredIds[clicked];
            _selectedIdx = IndexOf(allIds, clickedId);
            _propScrollOffset = 0;
            SyncPreviewToSelected();
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
                Stats = new UnitStatsJson()
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
                string removeId = allIds[_selectedIdx];
                _gameData.Units.Remove(removeId);
                _selectedIdx = Math.Min(_selectedIdx, _gameData.Units.Count - 1);
                _unsavedChanges = true;
                SyncPreviewToSelected();
                SetStatus("Removed: " + removeId);
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

        // ==== SPRITE PREVIEW ====
        drawY = DrawSpritePreview(def, drawX, drawY, contentW, dt);
        drawY += 8;

        // ==== IDENTITY SECTION ====
        drawY = DrawIdentitySection(def, drawX, drawY, contentW);
        drawY += 8;

        // ==== STATS SECTION ====
        drawY = DrawStatsSection(def, drawX, drawY, contentW);
        drawY += 8;

        // ==== CASTER SECTION ====
        drawY = DrawCasterSection(def, drawX, drawY, contentW);
        drawY += 8;

        // ==== EQUIPMENT SECTION ====
        drawY = DrawEquipmentSection(def, drawX, drawY, contentW);
        drawY += 16;

        _maxPropHeight = (drawY - startDrawY) + (int)_propScrollOffset;

        // --- Scrollbar ---
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

        // Draw the sprite frame in the preview
        DrawPreviewSprite(def, previewX, previewY, previewSize, dt);

        // Controls to the right of the preview box
        int ctrlX = previewX + previewSize + 10;
        int ctrlW = w - previewSize - 20;
        int ctrlY = previewY;

        // Atlas dropdown
        string[] atlasNames = AtlasDefs.Names;
        string currentAtlas = def.Sprite?.AtlasName ?? "";
        string newAtlas = _ui.DrawCombo("prev_atlas", "Atlas", currentAtlas, atlasNames, ctrlX, ctrlY, ctrlW);
        if (newAtlas != currentAtlas)
        {
            if (def.Sprite == null) def.Sprite = new SpriteRef();
            def.Sprite.AtlasName = newAtlas;
            _unsavedChanges = true;
            SyncPreviewToSelected();
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

        // Play/Pause/Step, Frame counter
        int btnW2 = 50;
        if (_ui.DrawButton(_previewPlaying ? "Pause" : "Play", ctrlX, ctrlY, btnW2, 20))
            _previewPlaying = !_previewPlaying;

        if (_ui.DrawButton("Step", ctrlX + btnW2 + 4, ctrlY, 40, 20))
        {
            _previewPlaying = false;
            _previewAnim.Update(1f / 30f);
        }

        // Frame info
        string frameInfo = $"T:{_previewAnim.AnimTime:F0}";
        _ui.DrawText(frameInfo, new Vector2(ctrlX + btnW2 + 48, ctrlY + 2), EditorBase.TextDim);
        ctrlY += RowH;

        // Angle selector
        string currentAngleStr = ((int)_previewAngle).ToString();
        if (!AngleOptions.Contains(currentAngleStr)) currentAngleStr = "60";
        string newAngleStr = _ui.DrawCombo("prev_angle", "Angle", currentAngleStr, AngleOptions, ctrlX, ctrlY, ctrlW);
        if (int.TryParse(newAngleStr, out int newAngle))
            _previewAngle = newAngle;
        ctrlY += RowH;

        // Update preview animation
        if (_previewPlaying)
            _previewAnim.Update(dt);

        curY = Math.Max(previewY + previewSize, ctrlY) + 4;
        return curY;
    }

    private void DrawPreviewSprite(UnitDef def, int boxX, int boxY, int boxSize, float dt)
    {
        if (def.Sprite == null || string.IsNullOrEmpty(def.Sprite.AtlasName) || string.IsNullOrEmpty(def.Sprite.SpriteName))
            return;

        var atlasId = AtlasDefs.ResolveAtlasName(def.Sprite.AtlasName);
        if ((int)atlasId >= _atlases.Length) return;
        var atlas = _atlases[(int)atlasId];
        if (!atlas.IsLoaded || atlas.Texture == null) return;

        var spriteData = atlas.GetUnit(def.Sprite.SpriteName);
        if (spriteData == null) return;

        // Make sure anim controller is initialized
        if (_previewAnim.CurrentState == AnimState.Idle || true) // always keep in sync
        {
            _previewAnim.Init(spriteData);
            // Request the preview animation by mapping name to state
            var targetState = NameToAnimState(_previewAnimName);
            _previewAnim.ForceState(targetState);
            // Preserve time if playing
        }

        var fr = _previewAnim.GetCurrentFrame(_previewAngle);
        if (!fr.Frame.HasValue) return;

        var frame = fr.Frame.Value;
        // Scale sprite to fit in the preview box while maintaining aspect ratio
        float scaleX = (float)boxSize / frame.Rect.Width;
        float scaleY = (float)boxSize / frame.Rect.Height;
        float scale = Math.Min(scaleX, scaleY) * 0.8f; // 80% to leave some padding

        float drawW = frame.Rect.Width * scale;
        float drawH = frame.Rect.Height * scale;
        float drawX = boxX + (boxSize - drawW) / 2f;
        float drawY = boxY + (boxSize - drawH) / 2f;

        var effects = fr.FlipX ? SpriteEffects.FlipHorizontally : SpriteEffects.None;
        var origin = new Vector2(frame.Rect.Width * 0.5f, frame.Rect.Height * 0.5f);
        var pos = new Vector2(drawX + drawW / 2f, drawY + drawH / 2f);

        _ui.DrawTexture(atlas.Texture, pos, frame.Rect, Color.White, 0f, origin, scale, effects);
    }

    // =========================================================================
    //  IDENTITY SECTION
    // =========================================================================

    private int DrawIdentitySection(UnitDef def, int x, int y, int w)
    {
        int curY = y;
        DrawSectionHeader("Identity", x, ref curY, w);

        // Name
        string newName = _ui.DrawTextField("unit_name", "Name", def.DisplayName, x, curY, w);
        if (newName != def.DisplayName) { def.DisplayName = newName; _unsavedChanges = true; }
        curY += RowH;

        // ID (read-only display)
        _ui.DrawText("ID", new Vector2(x, curY + 2), EditorBase.TextDim);
        _ui.DrawText(def.Id, new Vector2(x + 120, curY + 2), EditorBase.TextColor);
        curY += RowH;

        // Faction
        string[] factions = Enum.GetNames<Faction>();
        string newFaction = _ui.DrawCombo("unit_faction", "Faction", def.Faction, factions, x, curY, w);
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

        // Size
        int newSize = _ui.DrawIntField("unit_size", "Size", def.Size, x, curY, w);
        if (newSize != def.Size) { def.Size = newSize; _unsavedChanges = true; }
        curY += RowH;

        // Radius
        float newRadius = _ui.DrawFloatField("unit_radius", "Radius", def.Radius, x, curY, w, 0.05f);
        if (Math.Abs(newRadius - def.Radius) > 0.001f) { def.Radius = newRadius; _unsavedChanges = true; }
        curY += RowH;

        // Sprite Scale
        float newScale = _ui.DrawFloatField("unit_sprscale", "Sprite Scale", def.SpriteScale, x, curY, w, 0.1f);
        if (Math.Abs(newScale - def.SpriteScale) > 0.001f) { def.SpriteScale = newScale; _unsavedChanges = true; }
        curY += RowH;

        // World Height
        float newHeight = _ui.DrawFloatField("unit_worldh", "World Height", def.SpriteWorldHeight, x, curY, w, 0.1f);
        if (Math.Abs(newHeight - def.SpriteWorldHeight) > 0.001f) { def.SpriteWorldHeight = newHeight; _unsavedChanges = true; }
        curY += RowH;

        // Zombie Type dropdown (populated from unit IDs that are faction Undead)
        string[] zombieTypes = BuildZombieTypeList();
        string newZombieType = _ui.DrawCombo("unit_zombie", "Zombie Type", def.ZombieTypeID, zombieTypes, x, curY, w);
        if (newZombieType != def.ZombieTypeID) { def.ZombieTypeID = newZombieType; _unsavedChanges = true; }
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
    //  CASTER SECTION
    // =========================================================================

    private int DrawCasterSection(UnitDef def, int x, int y, int w)
    {
        int curY = y;
        DrawSectionHeader("Caster", x, ref curY, w);

        // Spell dropdown
        string[] spellIds = BuildSpellDropdownList();
        string newSpellID = _ui.DrawCombo("unit_spell", "Spell", def.SpellID, spellIds, x, curY, w);
        if (newSpellID != def.SpellID) { def.SpellID = newSpellID; _unsavedChanges = true; }
        curY += RowH;

        // Max Mana
        float newMana = _ui.DrawFloatField("unit_mana", "Max Mana", def.MaxMana, x, curY, w, 1.0f);
        if (Math.Abs(newMana - def.MaxMana) > 0.001f) { def.MaxMana = newMana; _unsavedChanges = true; }
        curY += RowH;

        // Mana Regen
        float newRegen = _ui.DrawFloatField("unit_mregen", "Mana Regen", def.ManaRegen, x, curY, w, 0.1f);
        if (Math.Abs(newRegen - def.ManaRegen) > 0.001f) { def.ManaRegen = newRegen; _unsavedChanges = true; }
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

        // --- Weapons ---
        _ui.DrawText("Weapons", new Vector2(x, curY + 2), EditorBase.AccentColor);
        if (_ui.DrawButton("Edit Weapons", x + w - 110, curY, 100, 20))
            _activeSubEditor = SubEditor.Weapon;
        curY += RowH;

        string[] weaponIds = BuildWeaponDropdownList();
        for (int i = 0; i < def.Weapons.Count; i++)
        {
            string wId = def.Weapons[i];
            var wDef = _gameData.Weapons.Get(wId);
            string displayLabel = wDef != null ? $"  [{i}] {wDef.DisplayName}" : $"  [{i}]";

            // Weapon dropdown
            string newWId = _ui.DrawCombo($"weap_{i}", displayLabel, wId, weaponIds, x, curY, w - 28);
            if (newWId != wId) { def.Weapons[i] = newWId; _unsavedChanges = true; }

            // Remove button
            if (_ui.DrawButton("X", x + w - 24, curY, 22, 20, EditorBase.DangerColor))
            {
                def.Weapons.RemoveAt(i);
                _unsavedChanges = true;
                i--;
                curY += RowH;
                continue;
            }
            curY += RowH;

            // Stat summary for this weapon
            if (wDef != null)
            {
                string summary;
                if (wDef.IsRanged)
                    summary = $"    Ranged: dmg={wDef.RangedDamage} rng={wDef.Range:F1} prec={wDef.Precision} cd={wDef.Cooldown:F1}";
                else
                    summary = $"    Melee: dmg={wDef.Damage} atk={wDef.AttackBonus} def={wDef.DefenseBonus} len={wDef.Length}";
                _ui.DrawText(summary, new Vector2(x + 10, curY + 2), EditorBase.TextDim);
                curY += 18;
            }
        }
        if (def.Weapons.Count < 4)
        {
            if (_ui.DrawButton("+ Add Weapon", x + 10, curY, 100, 20))
            {
                def.Weapons.Add(weaponIds.Length > 0 ? weaponIds[0] : "");
                _unsavedChanges = true;
            }
            curY += RowH;
        }
        curY += 4;

        // --- Armors ---
        _ui.DrawText("Armors", new Vector2(x, curY + 2), EditorBase.AccentColor);
        if (_ui.DrawButton("Edit Armor", x + w - 110, curY, 100, 20))
            _activeSubEditor = SubEditor.Armor;
        curY += RowH;

        string[] armorIds = BuildArmorDropdownList();
        for (int i = 0; i < def.Armors.Count; i++)
        {
            string aId = def.Armors[i];
            var aDef = _gameData.Armors.Get(aId);
            string displayLabel = aDef != null ? $"  [{i}] {aDef.DisplayName}" : $"  [{i}]";

            string newAId = _ui.DrawCombo($"arm_{i}", displayLabel, aId, armorIds, x, curY, w - 28);
            if (newAId != aId) { def.Armors[i] = newAId; _unsavedChanges = true; }

            if (_ui.DrawButton("X", x + w - 24, curY, 22, 20, EditorBase.DangerColor))
            {
                def.Armors.RemoveAt(i);
                _unsavedChanges = true;
                i--;
                curY += RowH;
                continue;
            }
            curY += RowH;

            if (aDef != null)
            {
                string summary = $"    Body={aDef.BodyProtection} Head={aDef.HeadProtection} Enc={aDef.Encumbrance}";
                if (aDef.Bonuses.Count > 0) summary += $" [{string.Join(",", aDef.Bonuses)}]";
                _ui.DrawText(summary, new Vector2(x + 10, curY + 2), EditorBase.TextDim);
                curY += 18;
            }
        }
        if (def.Armors.Count < 4)
        {
            if (_ui.DrawButton("+ Add Armor", x + 10, curY, 100, 20))
            {
                def.Armors.Add(armorIds.Length > 0 ? armorIds[0] : "");
                _unsavedChanges = true;
            }
            curY += RowH;
        }
        curY += 4;

        // --- Shields ---
        _ui.DrawText("Shields", new Vector2(x, curY + 2), EditorBase.AccentColor);
        if (_ui.DrawButton("Edit Shields", x + w - 110, curY, 100, 20))
            _activeSubEditor = SubEditor.Shield;
        curY += RowH;

        string[] shieldIds = BuildShieldDropdownList();
        for (int i = 0; i < def.Shields.Count; i++)
        {
            string sId = def.Shields[i];
            var sDef = _gameData.Shields.Get(sId);
            string displayLabel = sDef != null ? $"  [{i}] {sDef.DisplayName}" : $"  [{i}]";

            string newSId = _ui.DrawCombo($"shld_{i}", displayLabel, sId, shieldIds, x, curY, w - 28);
            if (newSId != sId) { def.Shields[i] = newSId; _unsavedChanges = true; }

            if (_ui.DrawButton("X", x + w - 24, curY, 22, 20, EditorBase.DangerColor))
            {
                def.Shields.RemoveAt(i);
                _unsavedChanges = true;
                i--;
                curY += RowH;
                continue;
            }
            curY += RowH;

            if (sDef != null)
            {
                string summary = $"    Prot={sDef.Protection} Parry={sDef.Parry} Def={sDef.Defense}";
                _ui.DrawText(summary, new Vector2(x + 10, curY + 2), EditorBase.TextDim);
                curY += 18;
            }
        }
        if (def.Shields.Count < 1)
        {
            if (_ui.DrawButton("+ Add Shield", x + 10, curY, 100, 20))
            {
                def.Shields.Add(shieldIds.Length > 0 ? shieldIds[0] : "");
                _unsavedChanges = true;
            }
            curY += RowH;
        }

        return curY;
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

        string newId = _ui.DrawTextField("w_id", "ID", w.Id, x, curY, ww);
        if (newId != w.Id)
        {
            // Rename: update references
            string oldId = w.Id;
            w.Id = newId;
            // Re-key in registry if needed - for simplicity just mark unsaved
            _unsavedChanges = true;
        }
        curY += RowH;

        bool newRanged = _ui.DrawCheckbox("Is Ranged", w.IsRanged, x, curY);
        if (newRanged != w.IsRanged) { w.IsRanged = newRanged; _unsavedChanges = true; }
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

        string newId = _ui.DrawTextField("a_id", "ID", a.Id, x, curY, ww);
        if (newId != a.Id) { a.Id = newId; _unsavedChanges = true; }
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

        string newId = _ui.DrawTextField("s_id", "ID", s.Id, x, curY, ww);
        if (newId != s.Id) { s.Id = newId; _unsavedChanges = true; }
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
                    if (_ui.DrawButton("Delete", bx, bottomY, btnW, btnH, EditorBase.DangerColor))
                    {
                        string removeId = wIds[_subSelectedIdx];
                        _gameData.Units.RemoveWeaponFromAll(removeId);
                        _gameData.Weapons.Remove(removeId);
                        _subSelectedIdx = Math.Min(_subSelectedIdx, _gameData.Weapons.Count - 1);
                        _unsavedChanges = true;
                        SetStatus("Removed weapon: " + removeId);
                    }
                }

                // Save
                if (_ui.DrawButton("Save", popX + popW - 80, bottomY, 70, btnH, EditorBase.SuccessColor))
                {
                    bool ok = _gameData.Weapons.Save("data/weapons.json");
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
                    if (_ui.DrawButton("Delete", bx, bottomY, btnW, btnH, EditorBase.DangerColor))
                    {
                        string removeId = aIds[_subSelectedIdx];
                        _gameData.Units.RemoveArmorFromAll(removeId);
                        _gameData.Armors.Remove(removeId);
                        _subSelectedIdx = Math.Min(_subSelectedIdx, _gameData.Armors.Count - 1);
                        _unsavedChanges = true;
                        SetStatus("Removed armor: " + removeId);
                    }
                }

                if (_ui.DrawButton("Save", popX + popW - 80, bottomY, 70, btnH, EditorBase.SuccessColor))
                {
                    bool ok = _gameData.Armors.Save("data/armor.json");
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
                    if (_ui.DrawButton("Delete", bx, bottomY, btnW, btnH, EditorBase.DangerColor))
                    {
                        string removeId = sIds[_subSelectedIdx];
                        _gameData.Units.RemoveShieldFromAll(removeId);
                        _gameData.Shields.Remove(removeId);
                        _subSelectedIdx = Math.Min(_subSelectedIdx, _gameData.Shields.Count - 1);
                        _unsavedChanges = true;
                        SetStatus("Removed shield: " + removeId);
                    }
                }

                if (_ui.DrawButton("Save", popX + popW - 80, bottomY, 70, btnH, EditorBase.SuccessColor))
                {
                    bool ok = _gameData.Shields.Save("data/shields.json");
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

        // Re-init animation controller
        var atlasId = AtlasDefs.ResolveAtlasName(_previewAtlas);
        if ((int)atlasId < _atlases.Length && _atlases[(int)atlasId].IsLoaded)
        {
            var spriteData = _atlases[(int)atlasId].GetUnit(_previewSprite);
            _previewAnim.Init(spriteData);
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
        var list = new List<string> { "" };
        foreach (var id in _gameData.Units.GetIDs())
        {
            var d = _gameData.Units.Get(id);
            if (d != null && d.Faction == "Undead")
                list.Add(id);
        }
        return list.ToArray();
    }

    private string[] BuildSpellDropdownList()
    {
        var list = new List<string> { "" };
        foreach (var id in _gameData.Spells.GetIDs())
            list.Add(id);
        return list.ToArray();
    }

    private string[] BuildWeaponDropdownList()
    {
        var list = new List<string> { "" };
        foreach (var id in _gameData.Weapons.GetIDs())
            list.Add(id);
        return list.ToArray();
    }

    private string[] BuildArmorDropdownList()
    {
        var list = new List<string> { "" };
        foreach (var id in _gameData.Armors.GetIDs())
            list.Add(id);
        return list.ToArray();
    }

    private string[] BuildShieldDropdownList()
    {
        var list = new List<string> { "" };
        foreach (var id in _gameData.Shields.GetIDs())
            list.Add(id);
        return list.ToArray();
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

        return def;
    }

    private static int IndexOf(IReadOnlyList<string> list, string value)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i] == value) return i;
        return -1;
    }
}
