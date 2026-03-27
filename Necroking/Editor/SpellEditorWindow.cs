using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;

namespace Necroking.Editor;

/// <summary>
/// Full spell definition editor with list panel, search, category-specific property editing,
/// flipbook manager popup, buff manager popup, and save.
/// Opened with F10.
/// </summary>
public class SpellEditorWindow
{
    private readonly EditorBase _ui;
    private GameData _gameData = null!;

    /// <summary>Set to true when the user clicks the [X] close button on the top bar.</summary>
    public bool WantsClose { get; set; }

    // Spell list state
    private int _selectedIdx = -1;
    /// <summary>Select the first item in the list (for screenshot scenarios).</summary>
    public void SelectFirst() { _selectedIdx = 0; }

    /// <summary>Open the buff manager popup (for UI test scenarios).</summary>
    public void OpenBuffManager() { _buffManagerOpen = true; _buffSelectedIdx = 0; }
    private string _searchFilter = "";
    private float _detailScroll;
    private List<string> _filteredIds = new();

    // Status bar
    private string _statusMessage = "";
    private float _statusTimer;
    private bool _unsavedChanges;

    // Flipbook manager popup
    private bool _flipbookManagerOpen;
    private int _fbSelectedIdx;
    private float _fbManagerScroll;
    private readonly TextureFileBrowser _fbTextureBrowser = new();

    // Buff manager popup
    private bool _buffManagerOpen;
    private int _buffSelectedIdx;
    private float _buffManagerScroll;
    private float _buffDetailScroll;

    // Buff preview
    private BuffPreview? _buffPreview;
    private int _lastBuffPreviewIdx = -1;

    // Delete confirmation dialog
    private bool _deleteConfirmOpen;
    private string _deleteConfirmTarget = ""; // "spell", "flipbook", or "buff"
    private string _deleteConfirmId = "";

    // Spell preview
    private SpellPreview? _spellPreview;
    private bool _previewBloom = true;
    private bool _buffPreviewBloom = true;
    private int _lastPreviewSelectedIdx = -1;

    // Clipboard for Ctrl+C / Ctrl+V
    private SpellDef? _clipboardSpell;

    // Category options (no Command/Toggle for editing)
    private static readonly string[] CategoryOptions =
        { "Projectile", "Buff", "Debuff", "Summon", "Strike", "Beam", "Drain" };
    private static readonly string[] AoeTypeOptions = { "Single", "AOE", "Chain" };
    private static readonly string[] TrajectoryOptions =
        { "Lob", "DirectFire", "Homing", "Swirly", "HomingSwirly" };
    private static readonly string[] SummonTargetReqOptions =
        { "None", "Corpse", "UnitType", "CorpseAOE" };
    private static readonly string[] SummonModeOptions = { "Spawn", "Transform" };
    private static readonly string[] SpawnLocOptions =
        { "NearestTargetToMouse", "NearestTargetToCaster", "AdjacentToCaster", "AtTargetLocation" };
    private static readonly string[] BlendOptions = { "Alpha", "Additive" };
    private static readonly string[] AlignOptions = { "Ground", "Upright" };
    private static readonly string[] StrikeVisualOptions = { "Lightning", "GodRay" };
    private static readonly string[] EffectTypeOptions = { "Set", "Add", "Multiply" };
    private static readonly string[] StatOptions =
        { "Strength", "Attack", "Defense", "MagicResist", "NaturalProt", "CombatSpeed", "MaxHP", "Encumbrance" };
    private static readonly string[] IntBlendOptions = { "Alpha", "Additive" };
    private static readonly string[] TargetFilterOptions = { "AnyEnemy", "UndeadOnly", "LivingOnly" };

    // Layout constants
    private const int ListWidth = 300;
    private const int TopBarH = 50;
    private const int RowH = 24;
    private const int LabelW = 130;

    public SpellEditorWindow(EditorBase ui)
    {
        _ui = ui;
    }

    public void SetGameData(GameData gameData)
    {
        _gameData = gameData;
    }

    public void Draw(int screenW, int screenH, GameTime gameTime)
    {
        if (_gameData == null) return;

        float dt = (float)gameTime.ElapsedGameTime.TotalSeconds;
        if (_statusTimer > 0) _statusTimer -= dt;

        // Handle Ctrl+S
        HandleKeyboardShortcuts();

        // --- Spell preview update and render-to-target ---
        UpdateSpellPreview(dt);
        RenderSpellPreviewToTarget();

        // Full-screen dark overlay
        _ui.DrawRect(new Rectangle(0, 0, screenW, screenH), new Color(0, 0, 0, 180));

        // U22: Main panel with margins (5% horizontal, 3% vertical)
        int marginX = (int)(screenW * 0.05f);
        int marginY = (int)(screenH * 0.03f);
        int panelW = screenW - marginX * 2;
        int panelH = screenH - marginY * 2;
        int panelX = marginX;
        int panelY = marginY;

        // Panel background
        _ui.DrawRect(new Rectangle(panelX, panelY, panelW, panelH), new Color(35, 35, 45, 250));
        _ui.DrawBorder(new Rectangle(panelX, panelY, panelW, panelH), new Color(80, 80, 100), 2);

        // --- Top bar ---
        DrawTopBar(panelX, panelY, panelW, TopBarH);

        int contentY = panelY + TopBarH;
        int contentH = panelH - TopBarH;

        // --- Left panel: spell list (300px) ---
        DrawSpellList(panelX, contentY, ListWidth, contentH);

        // --- Separator line ---
        _ui.DrawRect(new Rectangle(panelX + ListWidth, contentY, 1, contentH), new Color(80, 80, 100));

        // --- Right panel: scrollable detail editor ---
        int detailX = panelX + ListWidth + 1;
        int detailW = panelW - ListWidth - 1;
        DrawDetailPanel(detailX, contentY, detailW, contentH);

        // --- Popup overlays (drawn on top) ---
        if (_flipbookManagerOpen)
        {
            _ui.InputLayer = 1;
            DrawFlipbookManagerPopup(screenW, screenH);
        }

        if (_buffManagerOpen)
        {
            // Update and render buff preview to its RenderTarget2D
            UpdateBuffPreview(dt);
            RenderBuffPreviewToTarget();

            _ui.InputLayer = 1;
            DrawBuffManagerPopup(screenW, screenH);
        }

        // Delete confirmation dialog (drawn on top of everything)
        if (_deleteConfirmOpen)
        {
            string msg = $"Delete {_deleteConfirmTarget}: {_deleteConfirmId}?";
            if (_ui.DrawConfirmDialog("Confirm Delete", msg, ref _deleteConfirmOpen))
            {
                ExecuteDelete();
            }
        }

        // Dropdown overlays (drawn last, on top of everything)
        _ui.DrawDropdownOverlays();
    }

    // ===========================
    //  Keyboard shortcuts
    // ===========================
    private void HandleKeyboardShortcuts()
    {
        if (_ui.IsTextInputActive) return;

        bool ctrl = _ui._kb.IsKeyDown(Keys.LeftControl) || _ui._kb.IsKeyDown(Keys.RightControl);
        bool sPressed = _ui._kb.IsKeyDown(Keys.S) && !_ui._prevKb.IsKeyDown(Keys.S);

        // Ctrl+S save (works from any context including buff manager)
        if (ctrl && sPressed)
        {
            _gameData.Save("data");
            _unsavedChanges = false;
            SetStatus("Saved!");
        }

        // Ctrl+C: Copy selected spell
        bool cPressed = _ui._kb.IsKeyDown(Keys.C) && !_ui._prevKb.IsKeyDown(Keys.C);
        if (ctrl && cPressed && !_buffManagerOpen && !_flipbookManagerOpen)
        {
            var allIds = _gameData.Spells.GetIDs();
            if (_selectedIdx >= 0 && _selectedIdx < allIds.Count)
            {
                var srcDef = _gameData.Spells.Get(allIds[_selectedIdx]);
                if (srcDef != null)
                {
                    _clipboardSpell = CloneSpell(srcDef, srcDef.Id);
                    SetStatus("Copied: " + srcDef.DisplayName);
                }
            }
        }

        // Ctrl+V: Paste copied spell
        bool vPressed = _ui._kb.IsKeyDown(Keys.V) && !_ui._prevKb.IsKeyDown(Keys.V);
        if (ctrl && vPressed && !_buffManagerOpen && !_flipbookManagerOpen && _clipboardSpell != null)
        {
            string newId = _clipboardSpell.Id + "_paste";
            int suffix = 1;
            while (_gameData.Spells.Get(newId) != null)
                newId = _clipboardSpell.Id + "_paste" + (++suffix);
            var newDef = CloneSpell(_clipboardSpell, newId);
            var allIds = _gameData.Spells.GetIDs();
            if (_selectedIdx >= 0 && _selectedIdx < allIds.Count)
                _gameData.Spells.AddAfter(newDef, allIds[_selectedIdx]);
            else
                _gameData.Spells.Add(newDef);
            _selectedIdx = IndexOf(_gameData.Spells.GetIDs(), newId);
            MarkDirty();
            SetStatus("Pasted: " + newId);
        }

        // Escape hierarchy: dropdown -> delete confirm -> buff manager -> flipbook manager -> (let parent handle close)
        bool escPressed = _ui._kb.IsKeyDown(Keys.Escape) && !_ui._prevKb.IsKeyDown(Keys.Escape);
        if (escPressed)
        {
            if (_ui.CloseActiveDropdown()) return; // dropdown was open, consumed escape
            if (_deleteConfirmOpen) { _deleteConfirmOpen = false; return; }
            if (_buffManagerOpen) { _buffManagerOpen = false; return; }
            if (_flipbookManagerOpen) { _flipbookManagerOpen = false; return; }
        }
    }

    // ===========================
    //  Top bar
    // ===========================
    private void DrawTopBar(int x, int y, int w, int h)
    {
        _ui.DrawRect(new Rectangle(x, y, w, h), new Color(25, 25, 35, 255));
        _ui.DrawRect(new Rectangle(x, y + h - 1, w, 1), new Color(80, 80, 100));

        // Title
        string title = _unsavedChanges ? "SPELL EDITOR *" : "SPELL EDITOR";
        var titleSize = _ui.MeasureText(title);
        _ui.DrawText(title, new Vector2(x + (w - titleSize.X) / 2, y + (h - titleSize.Y) / 2),
            EditorBase.TextBright);

        // Unsaved dot
        if (_unsavedChanges)
        {
            int dotX = (int)(x + (w + titleSize.X) / 2 + 10);
            _ui.DrawRect(new Rectangle(dotX, y + h / 2 - 3, 6, 6), new Color(255, 180, 50));
        }

        // Flipbooks button
        if (_ui.DrawButton("Flipbooks", x + w - 410, y + 10, 100, 30))
            _flipbookManagerOpen = true;

        // Buffs button
        if (_ui.DrawButton("Buffs", x + w - 300, y + 10, 80, 30))
            _buffManagerOpen = true;

        // Save button
        if (_ui.DrawButton("Save (Ctrl+S)", x + w - 210, y + 10, 160, 30, EditorBase.SuccessColor))
        {
            _gameData.Save("data");
            _unsavedChanges = false;
            SetStatus("Saved!");
        }

        // Close button [X] (RS01)
        if (_ui.DrawButton("X", x + w - 40, y + 10, 30, 30))
            WantsClose = true;

        // Status message
        if (_statusTimer > 0 && !string.IsNullOrEmpty(_statusMessage))
        {
            float alpha = Math.Min(1f, _statusTimer);
            int a = (int)(alpha * 255);
            Color c = _statusMessage.Contains("FAIL") ? new Color(255, 80, 80, a) : new Color(100, 255, 100, a);
            _ui.DrawText(_statusMessage, new Vector2(x + 20, y + 17), c);
        }
    }

    // ===========================
    //  Spell List (left panel)
    // ===========================
    private void DrawSpellList(int x, int y, int w, int h)
    {
        _ui.DrawRect(new Rectangle(x, y, w, h), new Color(30, 30, 40, 255));

        int curY = y + 8;

        // Search field
        _searchFilter = _ui.DrawSearchField("spell_search", _searchFilter, x + 8, curY, w - 16);
        curY += 28;

        // Rebuild filtered list
        RebuildFilteredList();

        // Map selection
        int filteredSelectedIdx = -1;
        if (_selectedIdx >= 0)
        {
            var allIds = _gameData.Spells.GetIDs();
            if (_selectedIdx < allIds.Count)
            {
                string selectedId = allIds[_selectedIdx];
                filteredSelectedIdx = _filteredIds.IndexOf(selectedId);
            }
        }

        // Draw list items with category color dots
        int listY = curY;
        int listH = h - (curY - y) - 44;
        int itemH = 24;

        var displayItems = new List<string>();
        foreach (var id in _filteredIds)
        {
            var def = _gameData.Spells.Get(id);
            displayItems.Add(def?.DisplayName ?? id);
        }

        // Draw list background
        _ui.DrawRect(new Rectangle(x, listY, w, listH), new Color(25, 25, 38, 220));

        // Manual list rendering with color dots
        float scrollKey = GetListScroll("spellList");
        int totalItemsH = displayItems.Count * itemH;
        float maxScroll = Math.Max(0, totalItemsH - listH);

        // Handle scroll
        var listRect = new Rectangle(x, listY, w, listH);
        if (listRect.Contains(_ui._mouse.X, _ui._mouse.Y))
        {
            int scrollDelta = _ui._mouse.ScrollWheelValue - _ui._prevMouse.ScrollWheelValue;
            if (scrollDelta != 0)
            {
                scrollKey -= scrollDelta * 0.15f;
                scrollKey = Math.Clamp(scrollKey, 0, maxScroll);
                SetListScroll("spellList", scrollKey);
            }
        }

        float drawItemY = listY - scrollKey;
        for (int i = 0; i < _filteredIds.Count; i++)
        {
            if (drawItemY + itemH < listY) { drawItemY += itemH; continue; }
            if (drawItemY >= listY + listH) break;

            var itemRect = new Rectangle(x + 2, (int)drawItemY, w - 4, itemH);
            bool hovered = itemRect.Contains(_ui._mouse.X, _ui._mouse.Y) && listRect.Contains(_ui._mouse.X, _ui._mouse.Y);
            bool isSelected = (i == filteredSelectedIdx);

            Color bg;
            if (isSelected) bg = EditorBase.ItemSelected;
            else if (hovered) bg = EditorBase.ItemHover;
            else bg = (i % 2 == 0) ? new Color(30, 30, 48, 200) : new Color(25, 25, 40, 200);

            if (drawItemY + itemH > listY && drawItemY < listY + listH)
            {
                _ui.DrawRect(itemRect, bg);

                // Category color circle
                var def = _gameData.Spells.Get(_filteredIds[i]);
                if (def != null)
                {
                    Color catColor = GetCategoryColor(def.Category);
                    int dotCX = x + 13;
                    int dotCY = (int)(drawItemY + itemH / 2);
                    DrawSmallFilledCircle(dotCX, dotCY, 4, catColor);
                }

                string displayName = displayItems[i];
                _ui.DrawText(displayName, new Vector2(x + 22, drawItemY + 3),
                    isSelected ? EditorBase.TextBright : EditorBase.TextColor);

                if (hovered && _ui._mouse.LeftButton == ButtonState.Pressed &&
                    _ui._prevMouse.LeftButton == ButtonState.Released)
                {
                    string clickedId = _filteredIds[i];
                    var allIds = _gameData.Spells.GetIDs();
                    _selectedIdx = IndexOf(allIds, clickedId);
                    _detailScroll = 0;
                }
            }
            drawItemY += itemH;
        }

        // Scrollbar
        if (totalItemsH > listH)
        {
            float scrollRatio = scrollKey / Math.Max(1, totalItemsH - listH);
            int barH = Math.Max(20, listH * listH / totalItemsH);
            int barY = listY + (int)(scrollRatio * (listH - barH));
            _ui.DrawRect(new Rectangle(x + w - 7, barY, 5, barH), new Color(100, 100, 140, 180));
        }

        // --- Bottom button row ---
        int btnY = y + h - 40;
        int btnW = (w - 24) / 3;

        if (_ui.DrawButton("+ New", x + 6, btnY, btnW, 28))
        {
            string newId = "spell_" + DateTime.Now.ToString("HHmmss");
            var newDef = new SpellDef { Id = newId, DisplayName = "New Spell" };
            var allIds = _gameData.Spells.GetIDs();
            if (_selectedIdx >= 0 && _selectedIdx < allIds.Count)
                _gameData.Spells.AddAfter(newDef, allIds[_selectedIdx]);
            else
                _gameData.Spells.Add(newDef);
            _selectedIdx = IndexOf(_gameData.Spells.GetIDs(), newId);
            MarkDirty();
            SetStatus("Added: " + newId);
        }

        if (_ui.DrawButton("Copy", x + 10 + btnW, btnY, btnW, 28))
        {
            var allIds = _gameData.Spells.GetIDs();
            if (_selectedIdx >= 0 && _selectedIdx < allIds.Count)
            {
                var srcDef = _gameData.Spells.Get(allIds[_selectedIdx]);
                if (srcDef != null)
                {
                    string newId = srcDef.Id + "_copy";
                    int suffix = 1;
                    while (_gameData.Spells.Get(newId) != null)
                        newId = srcDef.Id + "_copy" + (++suffix);
                    var newDef = CloneSpell(srcDef, newId);
                    _gameData.Spells.AddAfter(newDef, srcDef.Id);
                    _selectedIdx = IndexOf(_gameData.Spells.GetIDs(), newId);
                    MarkDirty();
                    SetStatus("Copied: " + newId);
                }
            }
        }

        if (_ui.DrawButton("Delete", x + 14 + btnW * 2, btnY, btnW, 28, EditorBase.DangerColor))
        {
            var allIds = _gameData.Spells.GetIDs();
            if (_selectedIdx >= 0 && _selectedIdx < allIds.Count)
            {
                _deleteConfirmTarget = "spell";
                _deleteConfirmId = allIds[_selectedIdx];
                _deleteConfirmOpen = true;
            }
        }
    }

    // ===========================
    //  Detail Panel (right side)
    // ===========================
    private void DrawDetailPanel(int x, int y, int w, int h)
    {
        var allIds = _gameData.Spells.GetIDs();
        if (_selectedIdx < 0 || _selectedIdx >= allIds.Count)
        {
            _ui.DrawText("Select a spell from the list",
                new Vector2(x + 20, y + 40), EditorBase.TextDim);
            return;
        }

        var def = _gameData.Spells.Get(allIds[_selectedIdx]);
        if (def == null) return;

        // Scroll handling
        var panelRect = new Rectangle(x, y, w, h);
        if (panelRect.Contains(_ui._mouse.X, _ui._mouse.Y))
        {
            int scrollDelta = _ui._mouse.ScrollWheelValue - _ui._prevMouse.ScrollWheelValue;
            if (scrollDelta != 0)
            {
                _detailScroll -= scrollDelta * 0.4f;
                _detailScroll = Math.Max(0, _detailScroll);
            }
        }

        // Clipping background
        _ui.DrawRect(panelRect, new Color(28, 28, 42, 200));

        // RS22: Begin scissor clipping to prevent overflow
        _ui.BeginClip(panelRect);

        int fieldW = w - 24;
        int curY = y + 8 - (int)_detailScroll;

        // =================== PREVIEW ===================
        curY = DrawSpellPreviewSection(def, x + 8, curY, fieldW);

        // =================== NAME ===================
        string oldName = def.DisplayName;
        def.DisplayName = _ui.DrawTextField("sp_name", "Name", def.DisplayName, x + 8, curY, fieldW);
        if (def.DisplayName != oldName) MarkDirty();
        curY += RowH;

        // =================== ID (read-only) ===================
        _ui.DrawText("ID", new Vector2(x + 8, curY + 2), EditorBase.TextDim);
        _ui.DrawText(def.Id, new Vector2(x + 8 + LabelW, curY + 2), new Color(140, 140, 165));
        curY += RowH;

        // =================== CATEGORY ===================
        string oldCat = def.Category;
        def.Category = _ui.DrawCombo("sp_category", "Category", def.Category, CategoryOptions, x + 8, curY, fieldW);
        if (def.Category != oldCat) MarkDirty();
        curY += RowH;

        // =================== SEPARATOR: COMMON ===================
        curY += 4;
        DrawSectionHeader(x + 8, ref curY, w - 16, "COMMON", EditorBase.TextBright);

        // Range
        def.Range = DrawFloatField10x("sp_range", "Range", def.Range, x + 8, ref curY, fieldW);
        // Mana Cost
        def.ManaCost = DrawFloatField10x("sp_mana", "Mana Cost", def.ManaCost, x + 8, ref curY, fieldW);
        // Cooldown
        def.Cooldown = DrawFloatField10x("sp_cd", "Cooldown", def.Cooldown, x + 8, ref curY, fieldW);
        // Cast Time
        def.CastTime = DrawFloatField10x("sp_cast", "Cast Time", def.CastTime, x + 8, ref curY, fieldW);

        // Casting Buff dropdown
        def.CastingBuffID = DrawBuffDropdown("sp_castbuff", "Casting Buff", def.CastingBuffID, x + 8, ref curY, fieldW);

        // =================== CATEGORY-SPECIFIC ===================
        switch (def.Category)
        {
            case "Projectile": DrawProjectileFields(def, x + 8, ref curY, fieldW); break;
            case "Buff":
            case "Debuff": DrawBuffDebuffFields(def, x + 8, ref curY, fieldW); break;
            case "Summon": DrawSummonFields(def, x + 8, ref curY, fieldW); break;
            case "Strike": DrawStrikeFields(def, x + 8, ref curY, fieldW); break;
            case "Beam": DrawBeamFields(def, x + 8, ref curY, fieldW); break;
            case "Drain": DrawDrainFields(def, x + 8, ref curY, fieldW); break;
        }

        // RS22: End scissor clipping
        _ui.EndClip();

        // Clamp scroll to content
        float totalContentH = (curY + _detailScroll) - y;
        float maxDetailScroll = Math.Max(0, totalContentH - h + 20);
        _detailScroll = Math.Min(_detailScroll, maxDetailScroll);
    }

    // ===========================
    //  Projectile fields
    // ===========================
    private void DrawProjectileFields(SpellDef def, int x, ref int curY, int w)
    {
        curY += 4;
        DrawSectionHeader(x, ref curY, w, "PROJECTILE", new Color(255, 160, 100));

        // AOE Type
        string oldAoe = def.AoeType;
        def.AoeType = _ui.DrawCombo("sp_aoe", "AOE Type", def.AoeType, AoeTypeOptions, x, curY, w);
        if (def.AoeType != oldAoe) MarkDirty();
        curY += RowH;

        // Quantity
        int oldQty = def.Quantity;
        def.Quantity = _ui.DrawIntField("sp_qty", "Quantity", def.Quantity, x, curY, w);
        if (def.Quantity != oldQty) MarkDirty();
        curY += RowH;

        // Trajectory
        string oldTraj = def.Trajectory;
        def.Trajectory = _ui.DrawCombo("sp_traj", "Trajectory", def.Trajectory, TrajectoryOptions, x, curY, w);
        if (def.Trajectory != oldTraj) MarkDirty();
        curY += RowH;

        // Speed
        def.ProjectileSpeed = DrawFloatField10x("sp_pspeed", "Proj Speed", def.ProjectileSpeed, x, ref curY, w);

        // Precision
        int oldPrec = def.PrecisionBonus;
        def.PrecisionBonus = _ui.DrawIntField("sp_prec", "Precision Bonus", def.PrecisionBonus, x, curY, w);
        if (def.PrecisionBonus != oldPrec) MarkDirty();
        curY += RowH;

        // AOE Radius
        def.AoeRadius = DrawFloatField10x("sp_aoer", "AOE Radius", def.AoeRadius, x, ref curY, w);

        // Damage
        int oldDmg = def.Damage;
        def.Damage = _ui.DrawIntField("sp_dmg", "Damage", def.Damage, x, curY, w);
        if (def.Damage != oldDmg) MarkDirty();
        curY += RowH;

        // Delay
        def.ProjectileDelay = DrawFloatField100x("sp_pdelay", "Proj Delay", def.ProjectileDelay, x, ref curY, w);

        // Projectile flipbook ref
        def.ProjectileFlipbook = DrawFlipbookRefSection("sp_proj_fb", "Projectile Effect", def.ProjectileFlipbook, x, ref curY, w);
        // Hit effect flipbook ref
        def.HitEffectFlipbook = DrawFlipbookRefSection("sp_hit_fb", "Hit Effect", def.HitEffectFlipbook, x, ref curY, w);
    }

    // ===========================
    //  Buff/Debuff fields
    // ===========================
    private void DrawBuffDebuffFields(SpellDef def, int x, ref int curY, int w)
    {
        curY += 4;
        bool isBuff = def.Category == "Buff";
        string title = isBuff ? "BUFF" : "DEBUFF";
        Color titleColor = isBuff ? new Color(100, 255, 150) : new Color(200, 100, 255);
        DrawSectionHeader(x, ref curY, w, title, titleColor);

        // AOE Type
        string oldAoe = def.AoeType;
        def.AoeType = _ui.DrawCombo("sp_bd_aoe", "AOE Type", def.AoeType, AoeTypeOptions, x, curY, w);
        if (def.AoeType != oldAoe) MarkDirty();
        curY += RowH;

        // Buff ID dropdown
        def.BuffID = DrawBuffDropdown("sp_bd_buff", "Buff ID", def.BuffID, x, ref curY, w);

        // Friendly Only toggle
        bool oldFriendly = def.FriendlyOnly;
        def.FriendlyOnly = _ui.DrawCheckbox("Friendly Only", def.FriendlyOnly, x, curY);
        if (def.FriendlyOnly != oldFriendly) MarkDirty();
        curY += RowH;

        // Chain-specific fields
        if (def.AoeType == "Chain")
        {
            int oldCQty = def.ChainQuantity;
            def.ChainQuantity = _ui.DrawIntField("sp_bd_cqty", "Chain Qty", def.ChainQuantity, x, curY, w);
            if (def.ChainQuantity != oldCQty) MarkDirty();
            curY += RowH;

            def.ChainRange = DrawFloatField10x("sp_bd_crange", "Chain Range", def.ChainRange, x, ref curY, w);
            def.ChainDelay = DrawFloatField100x("sp_bd_cdelay", "Chain Delay", def.ChainDelay, x, ref curY, w);
        }

        // AOE radius (for AOE type)
        if (def.AoeType == "AOE")
        {
            def.AoeRadius = DrawFloatField10x("sp_bd_aoer", "AOE Radius", def.AoeRadius, x, ref curY, w);
        }

        // Acceptable Targets checklist (unit IDs)
        curY += 4;
        _ui.DrawRect(new Rectangle(x, curY, w, 1), new Color(60, 60, 80));
        curY += 4;
        _ui.DrawText("ACCEPTABLE TARGETS", new Vector2(x, curY), new Color(200, 180, 255));
        curY += 18;

        var unitIDs = _gameData.Units.GetIDs();
        if (def.AcceptableTargets == null)
            def.AcceptableTargets = new List<string>();

        // "(all)" hint when empty
        if (def.AcceptableTargets.Count == 0)
        {
            _ui.DrawText("(all units - check to restrict)", new Vector2(x + 4, curY + 2), new Color(120, 120, 140));
            curY += RowH;
        }

        // RS25: 2-column layout for acceptable targets
        int colW = (w - 8) / 2;
        for (int i = 0; i < unitIDs.Count; i++)
        {
            int col = i % 2;
            int colX = x + 4 + col * colW;
            var ud = _gameData.Units.Get(unitIDs[i]);
            string displayLabel = ud?.DisplayName ?? unitIDs[i];
            bool isChecked = def.AcceptableTargets.Contains(unitIDs[i]);
            bool newChecked = _ui.DrawCheckbox(displayLabel, isChecked, colX, curY);
            if (newChecked != isChecked)
            {
                if (newChecked)
                    def.AcceptableTargets.Add(unitIDs[i]);
                else
                    def.AcceptableTargets.Remove(unitIDs[i]);
                MarkDirty();
            }
            if (col == 1 || i == unitIDs.Count - 1)
                curY += RowH;
        }

        // Cast flipbook ref
        def.CastFlipbook = DrawFlipbookRefSection("sp_cast_fb", "Cast Effect", def.CastFlipbook, x, ref curY, w);
    }

    // ===========================
    //  Summon fields
    // ===========================
    private void DrawSummonFields(SpellDef def, int x, ref int curY, int w)
    {
        curY += 4;
        DrawSectionHeader(x, ref curY, w, "SUMMON", new Color(80, 200, 255));

        // Target Requirement
        string oldReq = def.SummonTargetReq;
        def.SummonTargetReq = _ui.DrawCombo("sp_sm_req", "Target Req", def.SummonTargetReq,
            SummonTargetReqOptions, x, curY, w);
        if (def.SummonTargetReq != oldReq) MarkDirty();
        curY += RowH;

        // Mode
        string oldMode = def.SummonMode;
        def.SummonMode = _ui.DrawCombo("sp_sm_mode", "Mode", def.SummonMode,
            SummonModeOptions, x, curY, w);
        if (def.SummonMode != oldMode) MarkDirty();
        curY += RowH;

        // Unit dropdown
        def.SummonUnitID = DrawUnitDropdown("sp_sm_unit", "Unit", def.SummonUnitID, x, ref curY, w);

        // Quantity
        int oldQty = def.SummonQuantity;
        def.SummonQuantity = _ui.DrawIntField("sp_sm_qty", "Quantity", def.SummonQuantity, x, curY, w);
        if (def.SummonQuantity != oldQty) MarkDirty();
        curY += RowH;

        // Spawn Location
        string oldLoc = def.SpawnLocation;
        def.SpawnLocation = _ui.DrawCombo("sp_sm_loc", "Spawn At", def.SpawnLocation,
            SpawnLocOptions, x, curY, w);
        if (def.SpawnLocation != oldLoc) MarkDirty();
        curY += RowH;

        // RS10: Acceptable Targets checklist when SummonTargetReq == "UnitType"
        if (def.SummonTargetReq == "UnitType")
        {
            curY += 4;
            _ui.DrawRect(new Rectangle(x, curY, w, 1), new Color(60, 60, 80));
            curY += 4;
            _ui.DrawText("ACCEPTABLE TARGETS", new Vector2(x, curY), new Color(200, 180, 255));
            curY += 18;

            var unitIDs = _gameData.Units.GetIDs();
            if (def.AcceptableTargets == null)
                def.AcceptableTargets = new List<string>();

            // "(all)" hint when empty
            if (def.AcceptableTargets.Count == 0)
            {
                _ui.DrawText("(all units - check to restrict)", new Vector2(x + 4, curY + 2), new Color(120, 120, 140));
                curY += RowH;
            }

            // RS25: 2-column layout for acceptable targets
            int smColW = (w - 8) / 2;
            for (int i = 0; i < unitIDs.Count; i++)
            {
                int col = i % 2;
                int colX = x + 4 + col * smColW;
                var ud = _gameData.Units.Get(unitIDs[i]);
                string displayLabel = ud?.DisplayName ?? unitIDs[i];
                bool isChecked = def.AcceptableTargets.Contains(unitIDs[i]);
                bool newChecked = _ui.DrawCheckbox(displayLabel, isChecked, colX, curY);
                if (newChecked != isChecked)
                {
                    if (newChecked)
                        def.AcceptableTargets.Add(unitIDs[i]);
                    else
                        def.AcceptableTargets.Remove(unitIDs[i]);
                    MarkDirty();
                }
                if (col == 1 || i == unitIDs.Count - 1)
                    curY += RowH;
            }
        }

        // Summon flipbook ref
        def.SummonFlipbook = DrawFlipbookRefSection("sp_sm_fb", "Summon Effect", def.SummonFlipbook, x, ref curY, w);
    }

    // ===========================
    //  Strike fields
    // ===========================
    private void DrawStrikeFields(SpellDef def, int x, ref int curY, int w)
    {
        curY += 4;
        DrawSectionHeader(x, ref curY, w, "STRIKE", new Color(255, 255, 100));

        // Target Filter row (3 toggle buttons)
        _ui.DrawText("Target Filter", new Vector2(x, curY + 2), EditorBase.TextDim);
        int tfBtnW = (w - LabelW - 8) / 3;
        int tfX = x + LabelW;
        for (int i = 0; i < TargetFilterOptions.Length; i++)
        {
            bool isActive = def.TargetFilter == TargetFilterOptions[i];
            Color btnColor = isActive ? EditorBase.AccentColor : EditorBase.ButtonBg;
            if (_ui.DrawButton(TargetFilterOptions[i], tfX + i * (tfBtnW + 4), curY, tfBtnW, 20, btnColor))
            {
                def.TargetFilter = TargetFilterOptions[i];
                MarkDirty();
            }
        }
        curY += RowH;

        // Target toggle (Ground vs Unit)
        bool oldTarget = def.StrikeTargetUnit;
        def.StrikeTargetUnit = _ui.DrawCheckbox(def.StrikeTargetUnit ? "Target: Unit (Zap)" : "Target: Ground", def.StrikeTargetUnit, x, curY);
        if (def.StrikeTargetUnit != oldTarget) MarkDirty();
        curY += RowH;

        // Visual type
        string oldVis = def.StrikeVisualType;
        def.StrikeVisualType = _ui.DrawCombo("sp_st_vis", "Visual", def.StrikeVisualType,
            StrikeVisualOptions, x, curY, w);
        if (def.StrikeVisualType != oldVis) MarkDirty();
        curY += RowH;

        // Damage
        int oldDmg = def.Damage;
        def.Damage = _ui.DrawIntField("sp_st_dmg", "Damage", def.Damage, x, curY, w);
        if (def.Damage != oldDmg) MarkDirty();
        curY += RowH;

        // RS23: AOE Radius only for ground strikes (not unit-targeted zaps)
        if (!def.StrikeTargetUnit)
        {
            def.AoeRadius = DrawFloatField10x("sp_st_aoer", "AOE Radius", def.AoeRadius, x, ref curY, w);
        }

        if (!def.StrikeTargetUnit)
        {
            // Telegraph, Duration
            def.TelegraphDuration = DrawFloatField100x("sp_st_tele", "Telegraph", def.TelegraphDuration, x, ref curY, w);
            def.StrikeDuration = DrawFloatField100x("sp_st_dur", "Duration", def.StrikeDuration, x, ref curY, w);
        }
        else
        {
            // Zap duration
            def.ZapDuration = DrawFloatField100x("sp_st_zap", "Zap Duration", def.ZapDuration, x, ref curY, w);

            // Chain Lightning sub-section
            _ui.DrawText("Chain Lightning:", new Vector2(x, curY + 2), new Color(120, 120, 140));
            curY += 18;

            int oldCBr = def.StrikeChainBranches;
            def.StrikeChainBranches = _ui.DrawIntField("sp_st_cbr", "Chain Branches", def.StrikeChainBranches, x, curY, w);
            if (def.StrikeChainBranches != oldCBr) MarkDirty();
            curY += RowH;

            int oldCDp = def.StrikeChainDepth;
            def.StrikeChainDepth = _ui.DrawIntField("sp_st_cdp", "Chain Depth", def.StrikeChainDepth, x, curY, w);
            if (def.StrikeChainDepth != oldCDp) MarkDirty();
            curY += RowH;

            def.StrikeChainRange = DrawFloatField10x("sp_st_crng", "Chain Range", def.StrikeChainRange, x, ref curY, w);
            def.StrikeChainWidthDecay = DrawFloatField100x("sp_st_cwdec", "Chain W.Decay", def.StrikeChainWidthDecay, x, ref curY, w);
        }

        // Common lightning style fields
        def.StrikeDisplacement = DrawFloatField100x("sp_st_disp", "Displacement", def.StrikeDisplacement, x, ref curY, w);

        int oldBr = def.StrikeBranches;
        def.StrikeBranches = _ui.DrawIntField("sp_st_br", "Branches", def.StrikeBranches, x, curY, w);
        if (def.StrikeBranches != oldBr) MarkDirty();
        curY += RowH;

        def.StrikeCoreWidth = DrawFloatField10x("sp_st_cw", "Core Width", def.StrikeCoreWidth, x, ref curY, w);
        def.StrikeGlowWidth = DrawFloatField10x("sp_st_gw", "Glow Width", def.StrikeGlowWidth, x, ref curY, w);
        def.StrikeFlickerMin = DrawFloatField100x("sp_st_fmin", "Flicker Min", def.StrikeFlickerMin, x, ref curY, w);
        def.StrikeFlickerMax = DrawFloatField100x("sp_st_fmax", "Flicker Max", def.StrikeFlickerMax, x, ref curY, w);
        def.StrikeFlickerHz = DrawFloatField10x("sp_st_fhz", "Flicker Hz", def.StrikeFlickerHz, x, ref curY, w);
        def.StrikeJitterHz = DrawFloatField10x("sp_st_jhz", "Jitter Hz", def.StrikeJitterHz, x, ref curY, w);

        // Core Color swatch
        def.StrikeCoreColor = DrawHdrColorSwatch("sp_st_core", "Core Color", def.StrikeCoreColor, x, ref curY, w);
        // Glow Color swatch
        def.StrikeGlowColor = DrawHdrColorSwatch("sp_st_glow", "Glow Color", def.StrikeGlowColor, x, ref curY, w);

        // God Ray sub-section
        if (def.StrikeVisualType == "GodRay")
        {
            curY += 4;
            _ui.DrawText("GOD RAY", new Vector2(x, curY), new Color(255, 220, 100));
            curY += 18;
            def.GodRayEdgeSoftness = DrawFloatField100x("sp_gr_edge", "Edge Softness", def.GodRayEdgeSoftness, x, ref curY, w);
            def.GodRayNoiseStrength = DrawFloatField100x("sp_gr_nstr", "Noise Strength", def.GodRayNoiseStrength, x, ref curY, w);
            def.GodRayNoiseSpeed = DrawFloatField10x("sp_gr_nspd", "Noise Speed", def.GodRayNoiseSpeed, x, ref curY, w);
            def.GodRayNoiseScale = DrawFloatField10x("sp_gr_nscl", "Noise Scale", def.GodRayNoiseScale, x, ref curY, w);
        }

        // Hit effect flipbook (for ground strikes)
        if (!def.StrikeTargetUnit)
        {
            def.HitEffectFlipbook = DrawFlipbookRefSection("sp_st_hitfb", "Hit Effect", def.HitEffectFlipbook, x, ref curY, w);
        }
    }

    // ===========================
    //  Beam fields
    // ===========================
    private void DrawBeamFields(SpellDef def, int x, ref int curY, int w)
    {
        curY += 4;
        DrawSectionHeader(x, ref curY, w, "BEAM", new Color(100, 220, 255));

        // Damage/Tick
        int oldDmg = def.Damage;
        def.Damage = _ui.DrawIntField("sp_bm_dmg", "Damage/Tick", def.Damage, x, curY, w);
        if (def.Damage != oldDmg) MarkDirty();
        curY += RowH;

        def.BeamTickRate = DrawFloatField100x("sp_bm_tick", "Tick Rate", def.BeamTickRate, x, ref curY, w);
        def.BeamMaxDuration = DrawFloatField10x("sp_bm_dur", "Max Duration", def.BeamMaxDuration, x, ref curY, w);

        // Show (unlimited) hint
        if (def.BeamMaxDuration <= 0.01f)
        {
            _ui.DrawText("(unlimited)", new Vector2(x + w - 80, curY - RowH + 4), new Color(120, 120, 140));
        }

        def.BeamRetargetRadius = DrawFloatField10x("sp_bm_retar", "Retarget Radius", def.BeamRetargetRadius, x, ref curY, w);
        def.BeamDisplacement = DrawFloatField100x("sp_bm_disp", "Displacement", def.BeamDisplacement, x, ref curY, w);

        int oldBr = def.BeamBranches;
        def.BeamBranches = _ui.DrawIntField("sp_bm_br", "Branches", def.BeamBranches, x, curY, w);
        if (def.BeamBranches != oldBr) MarkDirty();
        curY += RowH;

        def.BeamCoreWidth = DrawFloatField10x("sp_bm_cw", "Core Width", def.BeamCoreWidth, x, ref curY, w);
        def.BeamGlowWidth = DrawFloatField10x("sp_bm_gw", "Glow Width", def.BeamGlowWidth, x, ref curY, w);
        def.BeamFlickerMin = DrawFloatField100x("sp_bm_fmin", "Flicker Min", def.BeamFlickerMin, x, ref curY, w);
        def.BeamFlickerMax = DrawFloatField100x("sp_bm_fmax", "Flicker Max", def.BeamFlickerMax, x, ref curY, w);
        def.BeamFlickerHz = DrawFloatField10x("sp_bm_fhz", "Flicker Hz", def.BeamFlickerHz, x, ref curY, w);
        def.BeamJitterHz = DrawFloatField10x("sp_bm_jhz", "Jitter Hz", def.BeamJitterHz, x, ref curY, w);

        // Core/Glow colors
        def.BeamCoreColor = DrawHdrColorSwatch("sp_bm_core", "Core Color", def.BeamCoreColor, x, ref curY, w);
        def.BeamGlowColor = DrawHdrColorSwatch("sp_bm_glow", "Glow Color", def.BeamGlowColor, x, ref curY, w);

        // Chain Lightning section
        _ui.DrawText("Chain Lightning:", new Vector2(x, curY + 2), new Color(120, 120, 140));
        curY += 18;

        int oldCBr = def.BeamChainBranches;
        def.BeamChainBranches = _ui.DrawIntField("sp_bm_cbr", "Chain Branches", def.BeamChainBranches, x, curY, w);
        if (def.BeamChainBranches != oldCBr) MarkDirty();
        curY += RowH;

        int oldCDp = def.BeamChainDepth;
        def.BeamChainDepth = _ui.DrawIntField("sp_bm_cdp", "Chain Depth", def.BeamChainDepth, x, curY, w);
        if (def.BeamChainDepth != oldCDp) MarkDirty();
        curY += RowH;

        def.BeamChainRange = DrawFloatField10x("sp_bm_crng", "Chain Range", def.BeamChainRange, x, ref curY, w);
        def.BeamChainWidthDecay = DrawFloatField100x("sp_bm_cwdec", "Chain W.Decay", def.BeamChainWidthDecay, x, ref curY, w);
    }

    // ===========================
    //  Drain fields
    // ===========================
    private void DrawDrainFields(SpellDef def, int x, ref int curY, int w)
    {
        curY += 4;
        DrawSectionHeader(x, ref curY, w, "DRAIN", new Color(80, 255, 80));

        // Damage/Tick
        int oldDmg = def.Damage;
        def.Damage = _ui.DrawIntField("sp_dr_dmg", "Damage/Tick", def.Damage, x, curY, w);
        if (def.Damage != oldDmg) MarkDirty();
        curY += RowH;

        def.DrainTickRate = DrawFloatField100x("sp_dr_tick", "Tick Rate", def.DrainTickRate, x, ref curY, w);
        def.DrainHealPercent = DrawFloatField100x("sp_dr_heal", "Heal %", def.DrainHealPercent, x, ref curY, w);

        // Corpse HP
        int oldCorpse = def.DrainCorpseHP;
        def.DrainCorpseHP = _ui.DrawIntField("sp_dr_chp", "Corpse HP", def.DrainCorpseHP, x, curY, w);
        if (def.DrainCorpseHP != oldCorpse) MarkDirty();
        curY += RowH;

        def.DrainMaxDuration = DrawFloatField10x("sp_dr_dur", "Max Duration", def.DrainMaxDuration, x, ref curY, w);
        if (def.DrainMaxDuration <= 0.01f)
            _ui.DrawText("(unlimited)", new Vector2(x + w - 80, curY - RowH + 4), new Color(120, 120, 140));

        def.DrainBreakRange = DrawFloatField10x("sp_dr_brk", "Break Range", def.DrainBreakRange, x, ref curY, w);
        if (def.DrainBreakRange <= 0.01f)
            _ui.DrawText("(off)", new Vector2(x + w - 40, curY - RowH + 4), new Color(120, 120, 140));

        // Reversed toggles
        bool oldRev = def.DrainReversed;
        def.DrainReversed = _ui.DrawCheckbox("Reversed", def.DrainReversed, x, curY);
        if (def.DrainReversed != oldRev) MarkDirty();
        curY += RowH;

        bool oldVRev = def.DrainVisualReversed;
        def.DrainVisualReversed = _ui.DrawCheckbox("Visual Reversed", def.DrainVisualReversed, x, curY);
        if (def.DrainVisualReversed != oldVRev) MarkDirty();
        curY += RowH;

        // Visuals sub-section
        curY += 4;
        _ui.DrawRect(new Rectangle(x, curY, w, 1), new Color(50, 50, 60));
        curY += 4;
        _ui.DrawText("Visuals:", new Vector2(x, curY), new Color(120, 120, 140));
        curY += 18;

        // Tendril count
        int oldTend = def.DrainTendrilCount;
        def.DrainTendrilCount = _ui.DrawIntField("sp_dr_tend", "Tendrils", def.DrainTendrilCount, x, curY, w);
        if (def.DrainTendrilCount != oldTend) MarkDirty();
        curY += RowH;

        def.DrainArcHeight = DrawFloatField10x("sp_dr_arc", "Arc Height", def.DrainArcHeight, x, ref curY, w);
        def.DrainSwayAmplitude = DrawFloatField10x("sp_dr_samp", "Sway Amp", def.DrainSwayAmplitude, x, ref curY, w);
        def.DrainSwayHz = DrawFloatField10x("sp_dr_shz", "Sway Hz", def.DrainSwayHz, x, ref curY, w);
        def.DrainCoreWidth = DrawFloatField10x("sp_dr_cw", "Core Width", def.DrainCoreWidth, x, ref curY, w);
        def.DrainGlowWidth = DrawFloatField10x("sp_dr_gw", "Glow Width", def.DrainGlowWidth, x, ref curY, w);
        def.DrainPulseHz = DrawFloatField10x("sp_dr_phz", "Pulse Hz", def.DrainPulseHz, x, ref curY, w);
        def.DrainPulseStrength = DrawFloatField100x("sp_dr_pstr", "Pulse Str", def.DrainPulseStrength, x, ref curY, w);
        def.DrainFlickerMin = DrawFloatField100x("sp_dr_fmin", "Flicker Min", def.DrainFlickerMin, x, ref curY, w);
        def.DrainFlickerMax = DrawFloatField100x("sp_dr_fmax", "Flicker Max", def.DrainFlickerMax, x, ref curY, w);

        // Core/Glow colors
        def.DrainCoreColor = DrawHdrColorSwatch("sp_dr_core", "Core Color", def.DrainCoreColor, x, ref curY, w);
        def.DrainGlowColor = DrawHdrColorSwatch("sp_dr_glow", "Glow Color", def.DrainGlowColor, x, ref curY, w);

        // Target effect flipbook ref
        def.DrainTargetEffect = DrawFlipbookRefSection("sp_dr_tfb", "Target Effect", def.DrainTargetEffect, x, ref curY, w);
    }

    // ======================================
    //  FlipbookRef sub-editor (reusable)
    //  Returns the (possibly created) ref
    // ======================================
    private FlipbookRef? DrawFlipbookRefSection(string prefix, string sectionLabel,
        FlipbookRef? fbRef, int x, ref int curY, int w)
    {
        curY += 6;
        _ui.DrawText(sectionLabel, new Vector2(x, curY), new Color(200, 180, 255));
        curY += 18;

        // Create button if null
        if (fbRef == null)
        {
            if (_ui.DrawButton("Create " + sectionLabel, x, curY, 160, 22))
            {
                fbRef = new FlipbookRef();
                MarkDirty();
            }
            curY += RowH;
            return fbRef;
        }

        var fb = fbRef;

        // Flipbook dropdown
        fb.FlipbookID = DrawFlipbookDropdown(prefix + "_fb", "Flipbook", fb.FlipbookID, x, ref curY, w);

        // FPS
        fb.FPS = DrawFloatField10x(prefix + "_fps", "FPS", fb.FPS, x, ref curY, w);
        if (fb.FPS < 0) _ui.DrawText("(default)", new Vector2(x + w - 70, curY - RowH + 4), new Color(120, 120, 140));

        // Scale
        fb.Scale = DrawFloatField100x(prefix + "_scl", "Scale", fb.Scale, x, ref curY, w);

        // Rotation
        float oldRot = fb.Rotation;
        fb.Rotation = _ui.DrawFloatField(prefix + "_rot", "Rotation", fb.Rotation, x, curY, w, 1f);
        if (MathF.Abs(fb.Rotation - oldRot) > 0.01f) MarkDirty();
        curY += RowH;

        // Color swatch
        var c = fb.Color;
        c = DrawHdrColorSwatch(prefix + "_col", "Color", c, x, ref curY, w);
        fb.Color = c;

        // Blend Mode toggle
        string oldBlend = fb.BlendMode;
        fb.BlendMode = _ui.DrawCombo(prefix + "_blend", "Blend", fb.BlendMode, BlendOptions, x, curY, w);
        if (fb.BlendMode != oldBlend) MarkDirty();
        curY += RowH;

        // Alignment toggle
        string oldAlign = fb.Alignment;
        fb.Alignment = _ui.DrawCombo(prefix + "_align", "Alignment", fb.Alignment, AlignOptions, x, curY, w);
        if (fb.Alignment != oldAlign) MarkDirty();
        curY += RowH;

        // Duration
        fb.Duration = DrawFloatField100x(prefix + "_dur", "Duration", fb.Duration, x, ref curY, w);
        if (fb.Duration < 0) _ui.DrawText("(default 0.4s)", new Vector2(x + w - 100, curY - RowH + 4), new Color(120, 120, 140));

        return fb;
    }

    // ======================================
    //  Flipbook Manager Popup
    // ======================================
    private void DrawFlipbookManagerPopup(int screenW, int screenH)
    {
        // Update texture file browser input
        _fbTextureBrowser.Update(_ui._mouse, _ui._prevMouse, _ui._kb, _ui._prevKb);

        // Modal overlay
        _ui.DrawRect(new Rectangle(0, 0, screenW, screenH), new Color(0, 0, 0, 100));

        int pw = 700, ph = 500;
        int px = (screenW - pw) / 2;
        int py = (screenH - ph) / 2;

        _ui.DrawRect(new Rectangle(px, py, pw, ph), new Color(35, 35, 45, 250));
        _ui.DrawBorder(new Rectangle(px, py, pw, ph), new Color(100, 100, 120), 2);

        // Header
        _ui.DrawRect(new Rectangle(px, py, pw, 40), new Color(25, 25, 35, 255));
        string headerText = "FLIPBOOK MANAGER";
        var headerSize = _ui.MeasureText(headerText);
        _ui.DrawText(headerText, new Vector2(px + (pw - headerSize.X) / 2, py + 10), EditorBase.TextBright);

        // Temporarily unblock input so popup buttons/fields work
        int savedLayer = _ui.InputLayer;
        _ui.InputLayer = 0;

        // Close button
        if (_ui.DrawButton("X", px + pw - 40, py + 5, 30, 30))
        {
            _ui.InputLayer = savedLayer;
            _flipbookManagerOpen = false;
            return;
        }

        int listW = 200;
        int contentX = px + listW + 10;
        int contentW = pw - listW - 20;
        int contentY = py + 50;
        int rowH = 28;

        // --- Flipbook list ---
        var fbIDs = _gameData.Flipbooks.GetIDs();
        int listY = py + 50;
        int listH = ph - 100;

        _ui.DrawRect(new Rectangle(px, listY, listW, listH), new Color(30, 30, 40, 255));
        _ui.DrawRect(new Rectangle(px + listW, listY, 1, listH), new Color(80, 80, 100));

        // Scroll
        int itemH = 26;
        int totalFbH = fbIDs.Count * itemH;
        float maxFbScroll = Math.Max(0, totalFbH - listH);

        var fbListRect = new Rectangle(px, listY, listW, listH);
        if (fbListRect.Contains(_ui._mouse.X, _ui._mouse.Y))
        {
            int sd = _ui._mouse.ScrollWheelValue - _ui._prevMouse.ScrollWheelValue;
            if (sd != 0)
            {
                _fbManagerScroll -= sd * 0.15f;
                _fbManagerScroll = Math.Clamp(_fbManagerScroll, 0, maxFbScroll);
            }
        }

        float fbDrawY = listY - _fbManagerScroll;
        for (int i = 0; i < fbIDs.Count; i++)
        {
            if (fbDrawY + itemH < listY) { fbDrawY += itemH; continue; }
            if (fbDrawY >= listY + listH) break;

            var iRect = new Rectangle(px + 4, (int)fbDrawY, listW - 8, itemH);
            bool sel = (i == _fbSelectedIdx);
            bool hov = iRect.Contains(_ui._mouse.X, _ui._mouse.Y) && fbListRect.Contains(_ui._mouse.X, _ui._mouse.Y);

            Color bg = sel ? new Color(60, 60, 90) : (hov ? new Color(45, 45, 60) : Color.Transparent);
            _ui.DrawRect(iRect, bg);

            var fd = _gameData.Flipbooks.Get(fbIDs[i]);
            _ui.DrawText(fd?.DisplayName ?? fbIDs[i], new Vector2(px + 12, fbDrawY + 5),
                sel ? EditorBase.TextBright : EditorBase.TextColor);

            if (hov && _ui._mouse.LeftButton == ButtonState.Pressed && _ui._prevMouse.LeftButton == ButtonState.Released)
                _fbSelectedIdx = i;

            fbDrawY += itemH;
        }

        // Button row
        int btnRowY = py + ph - 44;
        int btnW = (listW - 24) / 3;

        if (_ui.DrawButton("+ New", px + 6, btnRowY, btnW, 32))
        {
            var nf = new FlipbookDef
            {
                Id = "flipbook_" + _gameData.Flipbooks.Count,
                DisplayName = "New Flipbook",
                Path = "assets/Effects/"
            };
            _gameData.Flipbooks.Add(nf);
            MarkDirty();
        }
        if (_ui.DrawButton("Copy", px + 10 + btnW, btnRowY, btnW, 32))
        {
            var currentFbIDs = _gameData.Flipbooks.GetIDs();
            if (_fbSelectedIdx >= 0 && _fbSelectedIdx < currentFbIDs.Count)
            {
                var src = _gameData.Flipbooks.Get(currentFbIDs[_fbSelectedIdx]);
                if (src != null)
                {
                    string newId = src.Id + "_copy";
                    int n = 2;
                    while (_gameData.Flipbooks.Get(newId) != null) newId = src.Id + "_copy" + n++;
                    var copy = new FlipbookDef
                    {
                        Id = newId, DisplayName = src.DisplayName + " (Copy)",
                        Path = src.Path, Cols = src.Cols, Rows = src.Rows, DefaultFPS = src.DefaultFPS
                    };
                    _gameData.Flipbooks.AddAfter(copy, src.Id);
                    MarkDirty();
                }
            }
        }
        if (_ui.DrawButton("Del", px + 14 + btnW * 2, btnRowY, btnW, 32, EditorBase.DangerColor))
        {
            var currentFbIDs = _gameData.Flipbooks.GetIDs();
            if (_fbSelectedIdx >= 0 && _fbSelectedIdx < currentFbIDs.Count)
            {
                _deleteConfirmTarget = "flipbook";
                _deleteConfirmId = currentFbIDs[_fbSelectedIdx];
                _deleteConfirmOpen = true;
            }
        }

        // Apply & Close
        if (_ui.DrawButton("Apply & Close", px + pw - 160, btnRowY, 150, 32))
        {
            _ui.InputLayer = savedLayer;
            _flipbookManagerOpen = false;
            return;
        }

        // --- Flipbook detail (right side) ---
        var curFbIDs = _gameData.Flipbooks.GetIDs();
        if (_fbSelectedIdx >= 0 && _fbSelectedIdx < curFbIDs.Count)
        {
            var fd = _gameData.Flipbooks.Get(curFbIDs[_fbSelectedIdx]);
            if (fd != null)
            {
                int cy = contentY;
                int fieldW = contentW - 10;

                // Name
                string oldName = fd.DisplayName;
                fd.DisplayName = _ui.DrawTextField("fb_name", "Name", fd.DisplayName, contentX, cy, fieldW);
                if (fd.DisplayName != oldName) MarkDirty();
                cy += rowH + 4;

                // Path with Browse button
                int browseBtnW = 55;
                string oldPath = fd.Path;
                fd.Path = _ui.DrawTextField("fb_path", "Path", fd.Path, contentX, cy, fieldW - browseBtnW - 4);
                if (fd.Path != oldPath) MarkDirty();
                if (_ui.DrawButton("Browse", contentX + fieldW - browseBtnW, cy, browseBtnW, 20))
                {
                    _fbTextureBrowser.Open("assets/Effects", fd.Path, path =>
                    {
                        fd.Path = path;
                        MarkDirty();
                    });
                }
                cy += rowH + 4;

                // Cols
                int oldCols = fd.Cols;
                fd.Cols = _ui.DrawIntField("fb_cols", "Cols", fd.Cols, contentX, cy, fieldW);
                fd.Cols = Math.Max(1, fd.Cols);
                if (fd.Cols != oldCols) MarkDirty();
                cy += rowH + 2;

                // Rows
                int oldRows = fd.Rows;
                fd.Rows = _ui.DrawIntField("fb_rows", "Rows", fd.Rows, contentX, cy, fieldW);
                fd.Rows = Math.Max(1, fd.Rows);
                if (fd.Rows != oldRows) MarkDirty();
                cy += rowH + 2;

                // Default FPS
                float oldFps = fd.DefaultFPS;
                fd.DefaultFPS = _ui.DrawFloatField("fb_fps", "FPS", fd.DefaultFPS, contentX, cy, fieldW, 1f);
                fd.DefaultFPS = Math.Max(1, fd.DefaultFPS);
                if (MathF.Abs(fd.DefaultFPS - oldFps) > 0.01f) MarkDirty();
                cy += rowH + 2;

                // Total frames info
                _ui.DrawText($"Total frames: {fd.Cols * fd.Rows}", new Vector2(contentX, cy + 6), new Color(120, 120, 140));
            }
        }

        // Texture file browser popup (drawn on top of flipbook manager)
        _fbTextureBrowser.Draw(_ui, screenW, screenH);

        // Restore input layer
        _ui.InputLayer = savedLayer;
    }

    // ======================================
    //  Buff Manager Popup
    // ======================================
    private void EnsureBuffPreviewInitialized()
    {
        if (_buffPreview != null && _buffPreview.IsInitialized) return;
        if (_ui._gd == null || _ui._pixel == null) return;
        _buffPreview = new BuffPreview();
        _buffPreview.Init(_ui._gd, _ui._pixel);
        _buffPreview.BloomEnabled = _buffPreviewBloom;
    }

    private void UpdateBuffPreview(float dt)
    {
        EnsureBuffPreviewInitialized();
        if (_buffPreview == null) return;

        var buffIDs = _gameData.Buffs.GetIDs();
        if (_buffSelectedIdx >= 0 && _buffSelectedIdx < buffIDs.Count)
        {
            var bd = _gameData.Buffs.Get(buffIDs[_buffSelectedIdx]);
            if (bd != null)
            {
                if (_buffSelectedIdx != _lastBuffPreviewIdx)
                {
                    _lastBuffPreviewIdx = _buffSelectedIdx;
                    _buffPreview.SetBuff(bd);
                }
                else
                {
                    // Live update: always pass the current buff data
                    _buffPreview.SetBuff(bd);
                }
            }
        }

        _buffPreview.Update(dt);
    }

    private void RenderBuffPreviewToTarget()
    {
        if (_buffPreview == null || !_buffPreview.IsInitialized) return;
        if (_buffSelectedIdx < 0) return;

        // End the current SpriteBatch so we can render to the RT
        _ui._sb.End();

        _buffPreview.RenderToTarget();

        // Re-begin the SpriteBatch to continue UI drawing
        _ui._sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
    }

    private void DrawBuffManagerPopup(int screenW, int screenH)
    {
        // Modal overlay
        _ui.DrawRect(new Rectangle(0, 0, screenW, screenH), new Color(0, 0, 0, 100));

        int pw = 900, ph = 700;
        int px = (screenW - pw) / 2;
        int py = (screenH - ph) / 2;

        _ui.DrawRect(new Rectangle(px, py, pw, ph), new Color(35, 35, 45, 250));
        _ui.DrawBorder(new Rectangle(px, py, pw, ph), new Color(100, 100, 120), 2);

        // Header
        _ui.DrawRect(new Rectangle(px, py, pw, 40), new Color(25, 25, 35, 255));
        string headerText = "BUFF MANAGER";
        var headerSize = _ui.MeasureText(headerText);
        _ui.DrawText(headerText, new Vector2(px + (pw - headerSize.X) / 2, py + 10), EditorBase.TextBright);

        // Close button
        if (_ui.DrawButton("X", px + pw - 40, py + 5, 30, 30))
        {
            _buffManagerOpen = false;
            return;
        }

        int listW = 200;
        int contentX = px + listW + 10;
        int contentW = pw - listW - 20;
        int contentY = py + 50;

        // --- Buff list (left side) ---
        var buffIDs = _gameData.Buffs.GetIDs();
        int listY = py + 50;
        int listH = ph - 100;

        _ui.DrawRect(new Rectangle(px, listY, listW, listH), new Color(30, 30, 40, 255));
        _ui.DrawRect(new Rectangle(px + listW, listY, 1, listH), new Color(80, 80, 100));

        int itemH = 26;
        int totalBuffH = buffIDs.Count * itemH;
        float maxBuffScroll = Math.Max(0, totalBuffH - listH);

        var buffListRect = new Rectangle(px, listY, listW, listH);
        if (buffListRect.Contains(_ui._mouse.X, _ui._mouse.Y))
        {
            int sd = _ui._mouse.ScrollWheelValue - _ui._prevMouse.ScrollWheelValue;
            if (sd != 0)
            {
                _buffManagerScroll -= sd * 0.15f;
                _buffManagerScroll = Math.Clamp(_buffManagerScroll, 0, maxBuffScroll);
            }
        }

        float buffDrawY = listY - _buffManagerScroll;
        for (int i = 0; i < buffIDs.Count; i++)
        {
            if (buffDrawY + itemH < listY) { buffDrawY += itemH; continue; }
            if (buffDrawY >= listY + listH) break;

            var iRect = new Rectangle(px + 4, (int)buffDrawY, listW - 8, itemH);
            bool sel = (i == _buffSelectedIdx);
            bool hov = iRect.Contains(_ui._mouse.X, _ui._mouse.Y) && buffListRect.Contains(_ui._mouse.X, _ui._mouse.Y);

            Color bg = sel ? new Color(60, 60, 90) : (hov ? new Color(45, 45, 60) : Color.Transparent);
            _ui.DrawRect(iRect, bg);

            var bd = _gameData.Buffs.Get(buffIDs[i]);
            _ui.DrawText(bd?.DisplayName ?? buffIDs[i], new Vector2(px + 12, buffDrawY + 5),
                sel ? EditorBase.TextBright : EditorBase.TextColor);

            if (hov && _ui._mouse.LeftButton == ButtonState.Pressed && _ui._prevMouse.LeftButton == ButtonState.Released)
                _buffSelectedIdx = i;

            buffDrawY += itemH;
        }

        // Button row
        int btnRowY = py + ph - 44;
        int btnW = (listW - 24) / 3;

        if (_ui.DrawButton("+ New", px + 6, btnRowY, btnW, 32))
        {
            var nb = new BuffDef
            {
                Id = "buff_" + _gameData.Buffs.Count,
                DisplayName = "New Buff"
            };
            _gameData.Buffs.Add(nb);
            MarkDirty();
        }
        if (_ui.DrawButton("Copy", px + 10 + btnW, btnRowY, btnW, 32))
        {
            var curBuffIDs = _gameData.Buffs.GetIDs();
            if (_buffSelectedIdx >= 0 && _buffSelectedIdx < curBuffIDs.Count)
            {
                var src = _gameData.Buffs.Get(curBuffIDs[_buffSelectedIdx]);
                if (src != null)
                {
                    string newId = src.Id + "_copy";
                    int n = 2;
                    while (_gameData.Buffs.Get(newId) != null) newId = src.Id + "_copy" + n++;
                    var copy = CloneBuff(src, newId);
                    _gameData.Buffs.AddAfter(copy, src.Id);
                    MarkDirty();
                }
            }
        }
        if (_ui.DrawButton("Del", px + 14 + btnW * 2, btnRowY, btnW, 32, EditorBase.DangerColor))
        {
            var curBuffIDs = _gameData.Buffs.GetIDs();
            if (_buffSelectedIdx >= 0 && _buffSelectedIdx < curBuffIDs.Count)
            {
                _deleteConfirmTarget = "buff";
                _deleteConfirmId = curBuffIDs[_buffSelectedIdx];
                _deleteConfirmOpen = true;
            }
        }

        // Apply & Close
        if (_ui.DrawButton("Apply & Close", px + pw - 160, btnRowY, 150, 32))
            _buffManagerOpen = false;

        // --- Buff detail (right side, scrollable) ---
        var currentBuffIDs = _gameData.Buffs.GetIDs();
        if (_buffSelectedIdx >= 0 && _buffSelectedIdx < currentBuffIDs.Count)
        {
            var bd = _gameData.Buffs.Get(currentBuffIDs[_buffSelectedIdx]);
            if (bd != null)
                DrawBuffDetail(bd, contentX, contentY, contentW, btnRowY - contentY - 10);
        }
    }

    // ======================================
    //  Buff detail panel (inside popup)
    // ======================================
    private void DrawBuffDetail(BuffDef bd, int contentX, int detailY, int contentW, int detailH)
    {
        int fieldW = contentW - 10;
        int rowH = 26;

        // Scroll handling for buff detail area
        var detailRect = new Rectangle(contentX, detailY, contentW, detailH);
        if (detailRect.Contains(_ui._mouse.X, _ui._mouse.Y))
        {
            int sd = _ui._mouse.ScrollWheelValue - _ui._prevMouse.ScrollWheelValue;
            if (sd != 0)
            {
                _buffDetailScroll -= sd * 0.4f;
                _buffDetailScroll = Math.Max(0, _buffDetailScroll);
            }
        }

        _ui.DrawRect(detailRect, new Color(28, 28, 42, 200));

        // Begin scissor clipping for scroll area
        _ui.BeginClip(detailRect);

        int cy = detailY - (int)_buffDetailScroll;

        // --- Buff Preview ---
        if (_buffPreview != null && _buffPreview.IsInitialized)
        {
            var tex = _buffPreview.GetTexture();
            if (tex != null)
            {
                int previewW = Math.Min(_buffPreview.Width, fieldW);
                float previewScale = previewW / (float)_buffPreview.Width;
                int previewH = (int)(_buffPreview.Height * previewScale);

                // Border
                _ui.DrawRect(new Rectangle(contentX - 1, cy - 1, previewW + 2, previewH + 2), new Color(60, 60, 80));

                // Draw the preview texture
                _ui._sb.Draw(tex, new Rectangle(contentX, cy, previewW, previewH), Color.White);

                // Label
                _ui.DrawText("BUFF PREVIEW", new Vector2(contentX + 4, cy + 2), new Color(255, 255, 255, 120));

                cy += previewH + 4;

                // Animation dropdown
                string currentAnim = BuffPreview.AnimOptions[_buffPreview.PreviewAnimIndex];
                string newAnim = _ui.DrawCombo("bf_prev_anim", "Animation", currentAnim, BuffPreview.AnimOptions, contentX, cy, Math.Min(250, fieldW));
                for (int ai = 0; ai < BuffPreview.AnimOptions.Length; ai++)
                {
                    if (BuffPreview.AnimOptions[ai] == newAnim)
                    {
                        _buffPreview.PreviewAnimIndex = ai;
                        break;
                    }
                }
                cy += rowH;

                // RS15: Stack Count spinner for buff preview
                int oldStacks = _buffPreview.PreviewStackCount;
                _buffPreview.PreviewStackCount = _ui.DrawIntField("bf_prev_stacks", "Stacks", _buffPreview.PreviewStackCount, contentX, cy, Math.Min(250, fieldW));
                _buffPreview.PreviewStackCount = Math.Clamp(_buffPreview.PreviewStackCount, 1, 10);
                if (_buffPreview.PreviewStackCount != oldStacks)
                    _buffPreview.MarkDirty();
                cy += rowH;

                // RS14: Bloom toggle for buff preview
                bool oldBuffBloom = _buffPreviewBloom;
                _buffPreviewBloom = _ui.DrawCheckbox("Bloom", _buffPreviewBloom, contentX, cy);
                if (_buffPreviewBloom != oldBuffBloom && _buffPreview != null)
                    _buffPreview.BloomEnabled = _buffPreviewBloom;
                cy += rowH + 8;
            }
        }

        // Name
        string oldName = bd.DisplayName;
        bd.DisplayName = _ui.DrawTextField("bf_name", "Name", bd.DisplayName, contentX, cy, fieldW);
        if (bd.DisplayName != oldName) MarkDirty();
        cy += rowH + 4;

        // Duration
        bd.Duration = DrawFloatField10x("bf_dur", "Duration", bd.Duration, contentX, ref cy, fieldW);
        if (bd.Duration == 0) _ui.DrawText("(permanent)", new Vector2(contentX + fieldW - 80, cy - RowH + 4), new Color(120, 120, 140));

        // --- EFFECTS ---
        cy += 4;
        _ui.DrawRect(new Rectangle(contentX, cy, fieldW, 1), new Color(60, 60, 80));
        cy += 4;
        _ui.DrawText("EFFECTS", new Vector2(contentX, cy), new Color(255, 220, 100));
        cy += 18;

        int effectToRemove = -1;
        for (int e = 0; e < bd.Effects.Count; e++)
        {
            var eff = bd.Effects[e];

            // Type dropdown (no label, inline)
            string oldType = eff.Type;
            eff.Type = _ui.DrawCombo($"bf_eff{e}_type", "", eff.Type, EffectTypeOptions, contentX, cy, 90);
            if (eff.Type != oldType) MarkDirty();

            // Stat dropdown
            string oldStat = eff.Stat;
            eff.Stat = _ui.DrawCombo($"bf_eff{e}_stat", "", eff.Stat, StatOptions, contentX + 94, cy, 120);
            if (eff.Stat != oldStat) MarkDirty();

            // Value
            float oldVal = eff.Value;
            eff.Value = _ui.DrawFloatField($"bf_eff{e}_val", "", eff.Value, contentX + 218, cy, 120, 0.1f);
            if (MathF.Abs(eff.Value - oldVal) > 0.001f) MarkDirty();

            // Remove button
            if (_ui.DrawButton("x", contentX + fieldW - 26, cy, 24, 22, EditorBase.DangerColor))
                effectToRemove = e;

            cy += rowH + 2;
        }
        if (effectToRemove >= 0 && effectToRemove < bd.Effects.Count)
        {
            bd.Effects.RemoveAt(effectToRemove);
            MarkDirty();
        }

        if (_ui.DrawButton("+ Add Effect", contentX, cy, 110, 24))
        {
            bd.Effects.Add(new BuffEffect { Type = "Add", Stat = "Strength", Value = 0 });
            MarkDirty();
        }
        cy += rowH + 8;

        // --- STACKING ---
        _ui.DrawRect(new Rectangle(contentX, cy, fieldW, 1), new Color(60, 60, 80));
        cy += 4;
        _ui.DrawText("STACKING", new Vector2(contentX, cy), new Color(255, 220, 100));
        cy += 18;

        int oldMaxSt = bd.MaxStacks;
        bd.MaxStacks = _ui.DrawIntField("bf_maxst", "Max Stacks", bd.MaxStacks, contentX, cy, fieldW);
        bd.MaxStacks = Math.Max(1, bd.MaxStacks);
        if (bd.MaxStacks != oldMaxSt) MarkDirty();
        if (bd.MaxStacks == 1) _ui.DrawText("(no stacking)", new Vector2(contentX + fieldW - 100, cy + 4), new Color(120, 120, 140));
        cy += rowH + 8;

        // --- VISUALS ---
        _ui.DrawRect(new Rectangle(contentX, cy, fieldW, 1), new Color(60, 60, 80));
        cy += 4;
        _ui.DrawText("VISUALS", new Vector2(contentX, cy), new Color(255, 220, 100));
        cy += 18;

        // Orbital
        bd.HasOrbital = DrawBuffVisualToggle("Orbital Orbs", bd.HasOrbital, contentX, ref cy, () =>
        {
            bd.Orbital ??= new OrbitalVisual();
            var orb = bd.Orbital;
            orb.FlipbookID = DrawFlipbookDropdown("bf_orb_fb", "Flipbook", orb.FlipbookID, contentX, ref cy, fieldW);
            orb.OrbScale = DrawFloatField100x("bf_orb_scl", "Orb Scale", orb.OrbScale, contentX, ref cy, fieldW);
            orb.OrbColor = DrawHdrColorSwatch("bf_orb_col", "Orb Color", orb.OrbColor, contentX, ref cy, fieldW);
            orb.SunOrbitRadius = DrawFloatField100x("bf_orb_sr", "Sun Radius", orb.SunOrbitRadius, contentX, ref cy, fieldW);
            orb.SunOrbitSpeed = DrawFloatField100x("bf_orb_ss", "Sun Speed", orb.SunOrbitSpeed, contentX, ref cy, fieldW);
            orb.MoonOrbitRadius = DrawFloatField100x("bf_orb_mr", "Moon Radius", orb.MoonOrbitRadius, contentX, ref cy, fieldW);
            orb.MoonOrbitSpeed = DrawFloatField100x("bf_orb_ms", "Moon Speed", orb.MoonOrbitSpeed, contentX, ref cy, fieldW);
            int oldOC = orb.OrbCount;
            orb.OrbCount = _ui.DrawIntField("bf_orb_cnt", "Orb Count", orb.OrbCount, contentX, cy, fieldW);
            if (orb.OrbCount != oldOC) MarkDirty();
            cy += RowH;
            bool oldMatch = orb.OrbCountMatchesStacks;
            orb.OrbCountMatchesStacks = _ui.DrawCheckbox("Count = Stacks", orb.OrbCountMatchesStacks, contentX, cy);
            if (orb.OrbCountMatchesStacks != oldMatch) MarkDirty();
            cy += RowH;
        });

        // Ground Aura
        bd.HasGroundAura = DrawBuffVisualToggle("Ground Aura", bd.HasGroundAura, contentX, ref cy, () =>
        {
            bd.GroundAura ??= new GroundAuraVisual();
            var ga = bd.GroundAura;
            ga.FlipbookID = DrawFlipbookDropdown("bf_ga_fb", "Flipbook", ga.FlipbookID, contentX, ref cy, fieldW);
            ga.Scale = DrawFloatField100x("bf_ga_scl", "Scale", ga.Scale, contentX, ref cy, fieldW);
            ga.Color = DrawHdrColorSwatch("bf_ga_col", "Color", ga.Color, contentX, ref cy, fieldW);
            int oldBm = ga.BlendMode;
            ga.BlendMode = DrawIntBlendMode("bf_ga_bm", ga.BlendMode, contentX, ref cy, fieldW);
            if (ga.BlendMode != oldBm) MarkDirty();
            ga.PulseSpeed = DrawFloatField100x("bf_ga_ps", "Pulse Speed", ga.PulseSpeed, contentX, ref cy, fieldW);
            ga.PulseAmount = DrawFloatField100x("bf_ga_pa", "Pulse Amount", ga.PulseAmount, contentX, ref cy, fieldW);
        });

        // Behind Effect
        bd.HasBehindEffect = DrawBuffVisualToggle("Behind Effect", bd.HasBehindEffect, contentX, ref cy, () =>
        {
            bd.BehindEffect ??= new UprightEffectVisual();
            DrawUprightEffectFields("bf_be", bd.BehindEffect, contentX, ref cy, fieldW);
        });

        // Front Effect
        bd.HasFrontEffect = DrawBuffVisualToggle("Front Effect", bd.HasFrontEffect, contentX, ref cy, () =>
        {
            bd.FrontEffect ??= new UprightEffectVisual();
            DrawUprightEffectFields("bf_fe", bd.FrontEffect, contentX, ref cy, fieldW);
        });

        // Lightning Crackle
        bd.HasLightningAura = DrawBuffVisualToggle("Lightning Crackle", bd.HasLightningAura, contentX, ref cy, () =>
        {
            bd.LightningAura ??= new LightningAuraVisual();
            var la = bd.LightningAura;
            int oldAC = la.ArcCount;
            la.ArcCount = _ui.DrawIntField("bf_la_ac", "Arc Count", la.ArcCount, contentX, cy, fieldW);
            if (la.ArcCount != oldAC) MarkDirty();
            cy += RowH;
            la.ArcRadius = DrawFloatField100x("bf_la_ar", "Arc Radius", la.ArcRadius, contentX, ref cy, fieldW);
            la.CoreColor = DrawHdrColorSwatch("bf_la_core", "Core Color", la.CoreColor, contentX, ref cy, fieldW);
            la.GlowColor = DrawHdrColorSwatch("bf_la_glow", "Glow Color", la.GlowColor, contentX, ref cy, fieldW);
            la.CoreWidth = DrawFloatField10x("bf_la_cw", "Core Width", la.CoreWidth, contentX, ref cy, fieldW);
            la.GlowWidth = DrawFloatField10x("bf_la_gw", "Glow Width", la.GlowWidth, contentX, ref cy, fieldW);
            la.FlickerHz = DrawFloatField10x("bf_la_fhz", "Flicker Hz", la.FlickerHz, contentX, ref cy, fieldW);
            la.JitterHz = DrawFloatField10x("bf_la_jhz", "Jitter Hz", la.JitterHz, contentX, ref cy, fieldW);
        });

        // Image Behind
        bd.HasImageBehind = DrawBuffVisualToggle("Image Behind", bd.HasImageBehind, contentX, ref cy, () =>
        {
            bd.ImageBehind ??= new ImageBehindVisual();
            var ib = bd.ImageBehind;
            ib.Color = DrawHdrColorSwatch("bf_ib_col", "Color", ib.Color, contentX, ref cy, fieldW);
            ib.Scale = DrawFloatField100x("bf_ib_scl", "Scale", ib.Scale, contentX, ref cy, fieldW);
            ib.PulseSpeed = DrawFloatField100x("bf_ib_ps", "Pulse Speed", ib.PulseSpeed, contentX, ref cy, fieldW);
            ib.PulseAmount = DrawFloatField100x("bf_ib_pa", "Pulse Amount", ib.PulseAmount, contentX, ref cy, fieldW);
            int oldBm = ib.BlendMode;
            ib.BlendMode = DrawIntBlendMode("bf_ib_bm", ib.BlendMode, contentX, ref cy, fieldW);
            if (ib.BlendMode != oldBm) MarkDirty();
        });

        // Pulsing Outline
        bd.HasPulsingOutline = DrawBuffVisualToggle("Pulsing Outline", bd.HasPulsingOutline, contentX, ref cy, () =>
        {
            bd.PulsingOutline ??= new PulsingOutlineVisual();
            var po = bd.PulsingOutline;
            po.Color = DrawHdrColorSwatch("bf_po_col", "Color", po.Color, contentX, ref cy, fieldW);
            po.PulseColor = DrawHdrColorSwatch("bf_po_pcol", "Pulse Color", po.PulseColor, contentX, ref cy, fieldW);
            po.OutlineWidth = DrawFloatField100x("bf_po_ow", "Outline Width", po.OutlineWidth, contentX, ref cy, fieldW);
            po.PulseWidth = DrawFloatField100x("bf_po_pw", "Pulse Width", po.PulseWidth, contentX, ref cy, fieldW);
            po.PulseSpeed = DrawFloatField100x("bf_po_ps", "Pulse Speed", po.PulseSpeed, contentX, ref cy, fieldW);
            int oldBm = po.BlendMode;
            po.BlendMode = DrawIntBlendMode("bf_po_bm", po.BlendMode, contentX, ref cy, fieldW);
            if (po.BlendMode != oldBm) MarkDirty();
        });

        // Weapon Particles
        bd.HasWeaponParticle = DrawBuffVisualToggle("Weapon Particles", bd.HasWeaponParticle, contentX, ref cy, () =>
        {
            bd.WeaponParticle ??= new WeaponParticleVisual();
            var wp = bd.WeaponParticle;
            wp.FlipbookID = DrawFlipbookDropdown("bf_wp_fb", "Flipbook", wp.FlipbookID, contentX, ref cy, fieldW);
            wp.FPS = DrawFloatField10x("bf_wp_fps", "FPS", wp.FPS, contentX, ref cy, fieldW);
            wp.Color = DrawHdrColorSwatch("bf_wp_col", "Color", wp.Color, contentX, ref cy, fieldW);
            wp.SpawnRate = DrawFloatField10x("bf_wp_sr", "Spawn Rate", wp.SpawnRate, contentX, ref cy, fieldW);
            wp.RangeMin = DrawFloatField100x("bf_wp_rmin", "Range Min", wp.RangeMin, contentX, ref cy, fieldW);
            wp.RangeMax = DrawFloatField100x("bf_wp_rmax", "Range Max", wp.RangeMax, contentX, ref cy, fieldW);
            wp.ParticleLifetime = DrawFloatField100x("bf_wp_lt", "Lifetime", wp.ParticleLifetime, contentX, ref cy, fieldW);
            wp.ParticleScale = DrawFloatField100x("bf_wp_pscl", "Part Scale", wp.ParticleScale, contentX, ref cy, fieldW);
            wp.MoveSpeed = DrawFloatField100x("bf_wp_mspd", "Move Speed", wp.MoveSpeed, contentX, ref cy, fieldW);
            wp.MoveDirX = DrawFloatField100x("bf_wp_mdx", "Move Dir X", wp.MoveDirX, contentX, ref cy, fieldW);
            wp.MoveDirY = DrawFloatField100x("bf_wp_mdy", "Move Dir Y", wp.MoveDirY, contentX, ref cy, fieldW);
            wp.MoveDirZ = DrawFloatField100x("bf_wp_mdz", "Move Dir Z", wp.MoveDirZ, contentX, ref cy, fieldW);
            int oldBm = wp.BlendMode;
            wp.BlendMode = DrawIntBlendMode("bf_wp_bm", wp.BlendMode, contentX, ref cy, fieldW);
            if (wp.BlendMode != oldBm) MarkDirty();
            bool oldRB = wp.RenderBehind;
            wp.RenderBehind = _ui.DrawCheckbox("Render Behind", wp.RenderBehind, contentX, cy);
            if (wp.RenderBehind != oldRB) MarkDirty();
            cy += RowH;
        });

        // Unit Tint
        cy += 4;
        _ui.DrawRect(new Rectangle(contentX, cy, fieldW, 1), new Color(60, 60, 80));
        cy += 4;
        _ui.DrawText("UNIT TINT", new Vector2(contentX, cy), new Color(255, 220, 100));
        cy += 18;

        bd.UnitTint ??= new ColorJson();
        var tint = bd.UnitTint;

        int oldTR = tint.R;
        tint.R = _ui.DrawIntField("bf_tint_r", "R", tint.R, contentX, cy, fieldW);
        tint.R = Math.Clamp(tint.R, 0, 255);
        if (tint.R != oldTR) MarkDirty();
        cy += RowH;

        int oldTG = tint.G;
        tint.G = _ui.DrawIntField("bf_tint_g", "G", tint.G, contentX, cy, fieldW);
        tint.G = Math.Clamp(tint.G, 0, 255);
        if (tint.G != oldTG) MarkDirty();
        cy += RowH;

        int oldTB = tint.B;
        tint.B = _ui.DrawIntField("bf_tint_b", "B", tint.B, contentX, cy, fieldW);
        tint.B = Math.Clamp(tint.B, 0, 255);
        if (tint.B != oldTB) MarkDirty();
        cy += RowH;

        int oldTA = tint.A;
        tint.A = _ui.DrawIntField("bf_tint_a", "A", tint.A, contentX, cy, fieldW);
        tint.A = Math.Clamp(tint.A, 0, 255);
        if (tint.A != oldTA) MarkDirty();
        cy += RowH;

        // End scissor clipping for scroll area
        _ui.EndClip();

        // Clamp buff detail scroll
        float totalBuffContentH = (cy + _buffDetailScroll) - detailY;
        float maxBDS = Math.Max(0, totalBuffContentH - detailH + 20);
        _buffDetailScroll = Math.Min(_buffDetailScroll, maxBDS);
    }

    // ======================================
    //  Buff visual toggle helper
    // ======================================
    private bool DrawBuffVisualToggle(string label, bool hasFlag,
        int x, ref int cy, Action drawFields)
    {
        bool oldFlag = hasFlag;
        hasFlag = _ui.DrawCheckbox(label, hasFlag, x, cy);
        if (hasFlag != oldFlag) MarkDirty();
        cy += RowH;

        if (hasFlag)
        {
            drawFields();
            cy += 4;
        }
        return hasFlag;
    }

    // ======================================
    //  UprightEffectVisual shared fields
    // ======================================
    private void DrawUprightEffectFields(string prefix, UprightEffectVisual ue, int x, ref int cy, int w)
    {
        ue.FlipbookID = DrawFlipbookDropdown(prefix + "_fb", "Flipbook", ue.FlipbookID, x, ref cy, w);
        ue.Scale = DrawFloatField100x(prefix + "_scl", "Scale", ue.Scale, x, ref cy, w);
        ue.Color = DrawHdrColorSwatch(prefix + "_col", "Color", ue.Color, x, ref cy, w);

        int oldBm = ue.BlendMode;
        ue.BlendMode = DrawIntBlendMode(prefix + "_bm", ue.BlendMode, x, ref cy, w);
        if (ue.BlendMode != oldBm) MarkDirty();

        ue.YOffset = DrawFloatField100x(prefix + "_yo", "Y Offset", ue.YOffset, x, ref cy, w);

        bool oldPin = ue.PinToEffectSpawn;
        ue.PinToEffectSpawn = _ui.DrawCheckbox("Pin to Spawn", ue.PinToEffectSpawn, x, cy);
        if (ue.PinToEffectSpawn != oldPin) MarkDirty();
        cy += RowH;
    }

    // ======================================
    //  Helper: int blend mode as dropdown
    // ======================================
    private int DrawIntBlendMode(string prefix, int value, int x, ref int cy, int w)
    {
        string current = value == 1 ? "Additive" : "Alpha";
        string result = _ui.DrawCombo(prefix, "Blend", current, IntBlendOptions, x, cy, w);
        cy += RowH;
        return result == "Additive" ? 1 : 0;
    }

    // ======================================
    //  Helper: Float field (10x precision, 1 decimal)
    // ======================================
    private float DrawFloatField10x(string fieldId, string label, float value, int x, ref int curY, int w)
    {
        float oldVal = value;
        float newVal = _ui.DrawFloatField(fieldId, label, value, x, curY, w, 0.1f);
        // Round to 1 decimal
        newVal = MathF.Round(newVal * 10f) / 10f;
        if (MathF.Abs(newVal - oldVal) > 0.001f) MarkDirty();
        curY += RowH;
        return newVal;
    }

    // ======================================
    //  Helper: Float field (100x precision, 2 decimals)
    // ======================================
    private float DrawFloatField100x(string fieldId, string label, float value, int x, ref int curY, int w)
    {
        float oldVal = value;
        float newVal = _ui.DrawFloatField(fieldId, label, value, x, curY, w, 0.01f);
        // Round to 2 decimals
        newVal = MathF.Round(newVal * 100f) / 100f;
        if (MathF.Abs(newVal - oldVal) > 0.001f) MarkDirty();
        curY += RowH;
        return newVal;
    }

    // ======================================
    //  Helper: HDR Color swatch (compact)
    //  Returns the edited color (value type)
    // ======================================
    private HdrColor DrawHdrColorSwatch(string prefix, string label, HdrColor color, int x, ref int curY, int w)
    {
        _ui.DrawText(label, new Vector2(x, curY + 2), EditorBase.TextDim);

        // Clickable color swatch that opens the HSV picker popup (swatch only, no inline fields)
        int swatchX = x + LabelW;
        if (_ui.DrawColorSwatch(prefix, swatchX, curY, 40, 18, ref color))
            MarkDirty();

        // Compact RGBA + I display next to swatch (read-only info)
        string info = $"({color.R},{color.G},{color.B},{color.A}) x{color.Intensity:F1}";
        _ui.DrawText(info, new Vector2(swatchX + 46, curY + 2), new Color(120, 120, 140));
        curY += RowH;

        return color;
    }

    // ======================================
    //  Helper: Section header
    // ======================================
    private void DrawSectionHeader(int x, ref int curY, int w, string text, Color color)
    {
        _ui.DrawRect(new Rectangle(x, curY, w, 1), new Color(60, 60, 80));
        curY += 6;
        _ui.DrawText(text, new Vector2(x, curY), color);
        curY += 22;
    }

    // ======================================
    //  Helper: Buff ID dropdown
    // ======================================
    private string DrawBuffDropdown(string fieldId, string label, string currentId,
        int x, ref int curY, int w)
    {
        var buffIDs = _gameData.Buffs.GetIDs();
        var names = new string[buffIDs.Count + 1];
        names[0] = "(none)";
        int currentIdx = 0;
        for (int i = 0; i < buffIDs.Count; i++)
        {
            var bd = _gameData.Buffs.Get(buffIDs[i]);
            names[i + 1] = bd?.DisplayName ?? buffIDs[i];
            if (currentId == buffIDs[i]) currentIdx = i + 1;
        }

        string currentName = currentIdx < names.Length ? names[currentIdx] : "(none)";
        string newName = _ui.DrawCombo(fieldId, label, currentName, names, x, curY, w);
        curY += RowH;

        if (newName != currentName)
        {
            MarkDirty();
            if (newName == "(none)") return "";
            for (int i = 0; i < buffIDs.Count; i++)
            {
                var bd = _gameData.Buffs.Get(buffIDs[i]);
                if ((bd?.DisplayName ?? buffIDs[i]) == newName)
                    return buffIDs[i];
            }
        }
        return currentId;
    }

    // ======================================
    //  Helper: Flipbook ID dropdown
    // ======================================
    private string DrawFlipbookDropdown(string fieldId, string label, string currentId,
        int x, ref int curY, int w)
    {
        var fbIDs = _gameData.Flipbooks.GetIDs();
        var names = new string[fbIDs.Count + 1];
        names[0] = "(none)";
        int currentIdx = 0;
        for (int i = 0; i < fbIDs.Count; i++)
        {
            var fd = _gameData.Flipbooks.Get(fbIDs[i]);
            names[i + 1] = fd?.DisplayName ?? fbIDs[i];
            if (currentId == fbIDs[i]) currentIdx = i + 1;
        }

        string currentName = currentIdx < names.Length ? names[currentIdx] : "(none)";
        string newName = _ui.DrawCombo(fieldId, label, currentName, names, x, curY, w);
        curY += RowH;

        if (newName != currentName)
        {
            MarkDirty();
            if (newName == "(none)") return "";
            for (int i = 0; i < fbIDs.Count; i++)
            {
                var fd = _gameData.Flipbooks.Get(fbIDs[i]);
                if ((fd?.DisplayName ?? fbIDs[i]) == newName)
                    return fbIDs[i];
            }
        }
        return currentId;
    }

    // ======================================
    //  Helper: Unit ID dropdown
    // ======================================
    private string DrawUnitDropdown(string fieldId, string label, string currentId,
        int x, ref int curY, int w)
    {
        var unitIDs = _gameData.Units.GetIDs();
        var names = new string[unitIDs.Count + 1];
        names[0] = "(none)";
        int currentIdx = 0;
        for (int i = 0; i < unitIDs.Count; i++)
        {
            var ud = _gameData.Units.Get(unitIDs[i]);
            names[i + 1] = ud?.DisplayName ?? unitIDs[i];
            if (currentId == unitIDs[i]) currentIdx = i + 1;
        }

        string currentName = currentIdx < names.Length ? names[currentIdx] : "(none)";
        string newName = _ui.DrawCombo(fieldId, label, currentName, names, x, curY, w);
        curY += RowH;

        if (newName != currentName)
        {
            MarkDirty();
            if (newName == "(none)") return "";
            for (int i = 0; i < unitIDs.Count; i++)
            {
                var ud = _gameData.Units.Get(unitIDs[i]);
                if ((ud?.DisplayName ?? unitIDs[i]) == newName)
                    return unitIDs[i];
            }
        }
        return currentId;
    }

    // ======================================
    //  Delete confirmation execution
    // ======================================
    private void ExecuteDelete()
    {
        switch (_deleteConfirmTarget)
        {
            case "spell":
            {
                var allIds = _gameData.Spells.GetIDs();
                if (IndexOf(allIds, _deleteConfirmId) >= 0)
                {
                    _gameData.Spells.Remove(_deleteConfirmId);
                    _selectedIdx = Math.Min(_selectedIdx, _gameData.Spells.Count - 1);
                    MarkDirty();
                    SetStatus("Deleted: " + _deleteConfirmId);
                }
                break;
            }
            case "flipbook":
            {
                var fbIds = _gameData.Flipbooks.GetIDs();
                if (IndexOf(fbIds, _deleteConfirmId) >= 0)
                {
                    _gameData.Flipbooks.Remove(_deleteConfirmId);
                    if (_fbSelectedIdx >= _gameData.Flipbooks.Count)
                        _fbSelectedIdx = Math.Max(0, _gameData.Flipbooks.Count - 1);
                    MarkDirty();
                    SetStatus("Deleted flipbook: " + _deleteConfirmId);
                }
                break;
            }
            case "buff":
            {
                var buffIds = _gameData.Buffs.GetIDs();
                if (IndexOf(buffIds, _deleteConfirmId) >= 0)
                {
                    _gameData.Buffs.Remove(_deleteConfirmId);
                    if (_buffSelectedIdx >= _gameData.Buffs.Count)
                        _buffSelectedIdx = Math.Max(0, _gameData.Buffs.Count - 1);
                    MarkDirty();
                    SetStatus("Deleted buff: " + _deleteConfirmId);
                }
                break;
            }
        }
    }

    // ======================================
    //  Spell Preview integration
    // ======================================
    private void EnsurePreviewInitialized()
    {
        if (_spellPreview != null && _spellPreview.IsInitialized) return;
        if (_ui._gd == null || _ui._pixel == null) return;
        _spellPreview = new SpellPreview();
        _spellPreview.Init(_ui._gd, _ui._pixel);
        _spellPreview.BloomEnabled = _previewBloom;
    }

    private void UpdateSpellPreview(float dt)
    {
        EnsurePreviewInitialized();
        if (_spellPreview == null) return;

        // Track selection changes
        var allIds = _gameData.Spells.GetIDs();
        if (_selectedIdx >= 0 && _selectedIdx < allIds.Count && _selectedIdx != _lastPreviewSelectedIdx)
        {
            _lastPreviewSelectedIdx = _selectedIdx;
            var def = _gameData.Spells.Get(allIds[_selectedIdx]);
            if (def != null)
                _spellPreview.TriggerSpell(def);
        }
        else if (_selectedIdx < 0 || _selectedIdx >= allIds.Count)
        {
            _lastPreviewSelectedIdx = -1;
        }

        // Keep the cached spell updated for live editing
        if (_selectedIdx >= 0 && _selectedIdx < allIds.Count)
        {
            var def = _gameData.Spells.Get(allIds[_selectedIdx]);
            if (def != null)
                _spellPreview.UpdateSpell(def);
        }

        _spellPreview.Update(dt);
    }

    private void RenderSpellPreviewToTarget()
    {
        if (_spellPreview == null || !_spellPreview.IsInitialized) return;
        if (_selectedIdx < 0) return;

        // End the current SpriteBatch so we can render to the RT
        _ui._sb.End();

        _spellPreview.RenderToTarget();

        // Re-begin the SpriteBatch to continue UI drawing
        _ui._sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
    }

    private int DrawSpellPreviewSection(SpellDef def, int x, int curY, int fieldW)
    {
        if (_spellPreview == null || !_spellPreview.IsInitialized) return curY;

        var tex = _spellPreview.GetTexture();
        if (tex == null) return curY;

        // Constrain preview width to available field width
        int previewW = Math.Min(_spellPreview.Width, fieldW);
        float scale = previewW / (float)_spellPreview.Width;
        int previewH = (int)(_spellPreview.Height * scale);

        // RS03: Center the preview horizontally within the detail panel
        int previewX = x + (fieldW - previewW) / 2;

        // Border
        _ui.DrawRect(new Rectangle(previewX - 1, curY - 1, previewW + 2, previewH + 2), new Color(60, 60, 80));

        // Draw the preview texture
        _ui._sb.Draw(tex, new Rectangle(previewX, curY, previewW, previewH), Color.White);

        // Category label overlay
        string catLabel = def.Category.ToUpperInvariant() + " PREVIEW";
        _ui.DrawText(catLabel, new Vector2(previewX + 4, curY + 2), new Color(255, 255, 255, 120));

        curY += previewH + 4;

        // Bloom checkbox
        bool oldBloom = _previewBloom;
        _previewBloom = _ui.DrawCheckbox("Bloom", _previewBloom, x, curY);
        if (_previewBloom != oldBloom && _spellPreview != null)
            _spellPreview.BloomEnabled = _previewBloom;

        // RS04: Loop indicator next to Bloom checkbox
        _ui.DrawText("Loop", new Vector2(x + 80, curY + 2), new Color(120, 200, 120));

        curY += RowH + 4;

        return curY;
    }

    // ======================================
    //  Utility
    // ======================================
    private void RebuildFilteredList()
    {
        _filteredIds.Clear();
        var allIds = _gameData.Spells.GetIDs();
        for (int i = 0; i < allIds.Count; i++)
        {
            if (string.IsNullOrEmpty(_searchFilter))
            {
                _filteredIds.Add(allIds[i]);
                continue;
            }
            if (allIds[i].Contains(_searchFilter, StringComparison.OrdinalIgnoreCase))
            {
                _filteredIds.Add(allIds[i]);
                continue;
            }
            var def = _gameData.Spells.Get(allIds[i]);
            if (def != null && def.DisplayName.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase))
                _filteredIds.Add(allIds[i]);
        }
    }

    private void MarkDirty()
    {
        _unsavedChanges = true;
        _spellPreview?.MarkDirty();
    }

    private void SetStatus(string msg)
    {
        _statusMessage = msg;
        _statusTimer = 3f;
    }

    // Scroll state for spell list
    private readonly Dictionary<string, float> _listScrolls = new();
    private float GetListScroll(string key) => _listScrolls.GetValueOrDefault(key, 0);
    private void SetListScroll(string key, float val) => _listScrolls[key] = val;

    private static Color GetCategoryColor(string category)
    {
        return category switch
        {
            "Projectile" => new Color(255, 120, 80),
            "Buff" => new Color(80, 255, 120),
            "Debuff" => new Color(200, 80, 255),
            "Summon" => new Color(80, 180, 255),
            "Strike" => new Color(255, 255, 100),
            "Beam" => new Color(100, 220, 255),
            "Drain" => new Color(80, 255, 80),
            "Command" => new Color(200, 180, 100),
            "Toggle" => new Color(160, 160, 200),
            _ => new Color(180, 180, 200),
        };
    }

    private static SpellDef CloneSpell(SpellDef src, string newId)
    {
        var def = new SpellDef();
        var props = typeof(SpellDef).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

        foreach (var prop in props)
        {
            if (!prop.CanWrite || !prop.CanRead) continue;
            var val = prop.GetValue(src);
            if (val == null) { prop.SetValue(def, null); continue; }

            if (val is List<string> strList)
                prop.SetValue(def, new List<string>(strList));
            else if (val is FlipbookRef fbRef)
                prop.SetValue(def, new FlipbookRef
                {
                    FlipbookID = fbRef.FlipbookID, FPS = fbRef.FPS, Scale = fbRef.Scale,
                    Color = fbRef.Color, Rotation = fbRef.Rotation, BlendMode = fbRef.BlendMode,
                    Duration = fbRef.Duration, Alignment = fbRef.Alignment,
                });
            else if (prop.PropertyType.IsValueType || prop.PropertyType == typeof(string))
                prop.SetValue(def, val);
        }

        def.Id = newId;
        def.DisplayName = src.DisplayName + " (Copy)";
        return def;
    }

    private static BuffDef CloneBuff(BuffDef src, string newId)
    {
        var def = new BuffDef
        {
            Id = newId,
            DisplayName = src.DisplayName + " (Copy)",
            Duration = src.Duration,
            MaxStacks = src.MaxStacks,
            HasOrbital = src.HasOrbital,
            HasGroundAura = src.HasGroundAura,
            HasBehindEffect = src.HasBehindEffect,
            HasFrontEffect = src.HasFrontEffect,
            HasLightningAura = src.HasLightningAura,
            HasImageBehind = src.HasImageBehind,
            HasPulsingOutline = src.HasPulsingOutline,
            HasWeaponParticle = src.HasWeaponParticle,
        };

        foreach (var eff in src.Effects)
            def.Effects.Add(new BuffEffect { Type = eff.Type, Stat = eff.Stat, Value = eff.Value });

        if (src.Orbital != null)
            def.Orbital = new OrbitalVisual
            {
                FlipbookID = src.Orbital.FlipbookID, OrbScale = src.Orbital.OrbScale,
                OrbColor = src.Orbital.OrbColor, SunOrbitRadius = src.Orbital.SunOrbitRadius,
                SunOrbitSpeed = src.Orbital.SunOrbitSpeed, MoonOrbitRadius = src.Orbital.MoonOrbitRadius,
                MoonOrbitSpeed = src.Orbital.MoonOrbitSpeed, OrbCount = src.Orbital.OrbCount,
                OrbCountMatchesStacks = src.Orbital.OrbCountMatchesStacks,
            };
        if (src.GroundAura != null)
            def.GroundAura = new GroundAuraVisual
            {
                FlipbookID = src.GroundAura.FlipbookID, Scale = src.GroundAura.Scale,
                Color = src.GroundAura.Color, BlendMode = src.GroundAura.BlendMode,
                PulseSpeed = src.GroundAura.PulseSpeed, PulseAmount = src.GroundAura.PulseAmount,
            };
        if (src.UnitTint != null)
            def.UnitTint = new ColorJson { R = src.UnitTint.R, G = src.UnitTint.G, B = src.UnitTint.B, A = src.UnitTint.A };

        // RS19: Deep copy remaining visual sub-objects
        if (src.BehindEffect != null)
            def.BehindEffect = new UprightEffectVisual
            {
                FlipbookID = src.BehindEffect.FlipbookID, Scale = src.BehindEffect.Scale,
                Color = src.BehindEffect.Color, BlendMode = src.BehindEffect.BlendMode,
                YOffset = src.BehindEffect.YOffset, PinToEffectSpawn = src.BehindEffect.PinToEffectSpawn,
            };
        if (src.FrontEffect != null)
            def.FrontEffect = new UprightEffectVisual
            {
                FlipbookID = src.FrontEffect.FlipbookID, Scale = src.FrontEffect.Scale,
                Color = src.FrontEffect.Color, BlendMode = src.FrontEffect.BlendMode,
                YOffset = src.FrontEffect.YOffset, PinToEffectSpawn = src.FrontEffect.PinToEffectSpawn,
            };
        if (src.LightningAura != null)
            def.LightningAura = new LightningAuraVisual
            {
                ArcCount = src.LightningAura.ArcCount, ArcRadius = src.LightningAura.ArcRadius,
                CoreColor = src.LightningAura.CoreColor, GlowColor = src.LightningAura.GlowColor,
                CoreWidth = src.LightningAura.CoreWidth, GlowWidth = src.LightningAura.GlowWidth,
                FlickerHz = src.LightningAura.FlickerHz, JitterHz = src.LightningAura.JitterHz,
            };
        if (src.ImageBehind != null)
            def.ImageBehind = new ImageBehindVisual
            {
                Color = src.ImageBehind.Color, Scale = src.ImageBehind.Scale,
                PulseSpeed = src.ImageBehind.PulseSpeed, PulseAmount = src.ImageBehind.PulseAmount,
                BlendMode = src.ImageBehind.BlendMode,
            };
        if (src.PulsingOutline != null)
            def.PulsingOutline = new PulsingOutlineVisual
            {
                Color = src.PulsingOutline.Color, PulseColor = src.PulsingOutline.PulseColor,
                OutlineWidth = src.PulsingOutline.OutlineWidth, PulseWidth = src.PulsingOutline.PulseWidth,
                PulseSpeed = src.PulsingOutline.PulseSpeed, BlendMode = src.PulsingOutline.BlendMode,
            };
        if (src.WeaponParticle != null)
            def.WeaponParticle = new WeaponParticleVisual
            {
                FlipbookID = src.WeaponParticle.FlipbookID, FPS = src.WeaponParticle.FPS,
                Color = src.WeaponParticle.Color, SpawnRate = src.WeaponParticle.SpawnRate,
                RangeMin = src.WeaponParticle.RangeMin, RangeMax = src.WeaponParticle.RangeMax,
                ParticleLifetime = src.WeaponParticle.ParticleLifetime,
                ParticleScale = src.WeaponParticle.ParticleScale,
                MoveSpeed = src.WeaponParticle.MoveSpeed,
                MoveDirX = src.WeaponParticle.MoveDirX, MoveDirY = src.WeaponParticle.MoveDirY,
                MoveDirZ = src.WeaponParticle.MoveDirZ,
                BlendMode = src.WeaponParticle.BlendMode, RenderBehind = src.WeaponParticle.RenderBehind,
            };

        return def;
    }

    /// <summary>
    /// Draw a small filled circle using the pixel texture (scanline approach).
    /// </summary>
    private void DrawSmallFilledCircle(int cx, int cy, int radius, Color color)
    {
        for (int dy = -radius; dy <= radius; dy++)
        {
            float hw = MathF.Sqrt(radius * radius - dy * dy);
            int x = (int)(cx - hw);
            int w = (int)(hw * 2);
            if (w > 0)
                _ui.DrawRect(new Rectangle(x, cy + dy, w, 1), color);
        }
    }

    private static int IndexOf(IReadOnlyList<string> list, string value)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i] == value) return i;
        return -1;
    }
}
