using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Necroking.Data;
using Necroking.Data.Registries;

namespace Necroking.Editor;

/// <summary>
/// Editor window for items and potions. Provides a list panel with search/filter,
/// detail editor with category-specific fields, and potion recipe editing.
/// </summary>
public class ItemEditorWindow
{
    private readonly EditorBase _ui;
    private GameData _gameData = null!;

    /// <summary>Set to true when the user clicks the [X] close button.</summary>
    public bool WantsClose { get; set; }

    // Item list state
    private int _selectedIdx = -1;
    /// <summary>Select the first item in the list (for screenshot scenarios).</summary>
    public void SelectFirst() { _selectedIdx = 0; }
    private string _searchFilter = "";
    private float _detailScroll;
    private List<string> _filteredIds = new();

    // Status bar
    private string _statusMessage = "";
    private float _statusTimer;
    private bool _unsavedChanges;

    // Delete confirmation
    private bool _deleteConfirmOpen;
    private string _deleteConfirmId = "";

    // Clipboard
    private ItemDef? _clipboardItem;

    // Layout constants
    private const int ListWidth = 300;
    private const int TopBarH = 50;
    private const int RowH = 24;

    // Scroll state
    private readonly Dictionary<string, float> _listScrolls = new();
    private float GetListScroll(string key) => _listScrolls.GetValueOrDefault(key, 0);
    private void SetListScroll(string key, float val) => _listScrolls[key] = val;

    private ReflectionPropertyRenderer _renderer = null!;

    public ItemEditorWindow(EditorBase ui)
    {
        _ui = ui;
    }

    public void SetGameData(GameData gameData)
    {
        _gameData = gameData;
        _renderer = new ReflectionPropertyRenderer(_ui, gameData);
    }

    public void Draw(int screenW, int screenH, GameTime gameTime)
    {
        if (_gameData == null) return;

        float dt = (float)gameTime.ElapsedGameTime.TotalSeconds;
        if (_statusTimer > 0) _statusTimer -= dt;

        // Handle keyboard shortcuts
        HandleKeyboardShortcuts();

        // Full-screen dark overlay
        _ui.DrawRect(new Rectangle(0, 0, screenW, screenH), new Color(0, 0, 0, 180));

        // Main panel with margins (5% horizontal, 3% vertical)
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

        // --- Left panel: item list ---
        DrawItemList(panelX, contentY, ListWidth, contentH);

        // --- Separator line ---
        _ui.DrawRect(new Rectangle(panelX + ListWidth, contentY, 1, contentH), new Color(80, 80, 100));

        // --- Right panel: detail editor ---
        int detailX = panelX + ListWidth + 1;
        int detailW = panelW - ListWidth - 1;
        DrawDetailPanel(detailX, contentY, detailW, contentH);

        // Delete confirmation dialog
        if (_deleteConfirmOpen)
        {
            string msg = $"Delete item: {_deleteConfirmId}?";
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

        // Ctrl+S save
        if (ctrl && sPressed)
        {
            _gameData.Save();
            _unsavedChanges = false;
            SetStatus("Saved!");
        }

        // Ctrl+C: Copy selected item
        bool cPressed = _ui._kb.IsKeyDown(Keys.C) && !_ui._prevKb.IsKeyDown(Keys.C);
        if (ctrl && cPressed)
        {
            var allIds = _gameData.Items.GetIDs();
            if (_selectedIdx >= 0 && _selectedIdx < allIds.Count)
            {
                var srcDef = _gameData.Items.Get(allIds[_selectedIdx]);
                if (srcDef != null)
                {
                    _clipboardItem = CloneItem(srcDef, srcDef.Id);
                    SetStatus("Copied: " + srcDef.DisplayName);
                }
            }
        }

        // Ctrl+V: Paste copied item
        bool vPressed = _ui._kb.IsKeyDown(Keys.V) && !_ui._prevKb.IsKeyDown(Keys.V);
        if (ctrl && vPressed && _clipboardItem != null)
        {
            string newId = _clipboardItem.Id + "_paste";
            int suffix = 1;
            while (_gameData.Items.Get(newId) != null)
                newId = _clipboardItem.Id + "_paste" + (++suffix);
            var newDef = CloneItem(_clipboardItem, newId);
            var allIds = _gameData.Items.GetIDs();
            if (_selectedIdx >= 0 && _selectedIdx < allIds.Count)
                _gameData.Items.AddAfter(newDef, allIds[_selectedIdx]);
            else
                _gameData.Items.Add(newDef);
            _selectedIdx = IndexOf(_gameData.Items.GetIDs(), newId);
            MarkDirty();
            SetStatus("Pasted: " + newId);
        }

        // Escape hierarchy: dropdown -> delete confirm -> close
        bool escPressed = _ui._kb.IsKeyDown(Keys.Escape) && !_ui._prevKb.IsKeyDown(Keys.Escape);
        if (escPressed)
        {
            if (_ui.CloseActiveDropdown()) return;
            if (_deleteConfirmOpen) { _deleteConfirmOpen = false; return; }
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
        string title = _unsavedChanges ? "ITEM EDITOR *" : "ITEM EDITOR";
        var titleSize = _ui.MeasureText(title);
        _ui.DrawText(title, new Vector2(x + (w - titleSize.X) / 2, y + (h - titleSize.Y) / 2),
            EditorBase.TextBright);

        // Unsaved dot
        if (_unsavedChanges)
        {
            int dotX = (int)(x + (w + titleSize.X) / 2 + 10);
            _ui.DrawRect(new Rectangle(dotX, y + h / 2 - 3, 6, 6), new Color(255, 180, 50));
        }

        // Save button
        if (_ui.DrawButton("Save (Ctrl+S)", x + w - 210, y + 10, 160, 30, EditorBase.SuccessColor))
        {
            _gameData.Save();
            _unsavedChanges = false;
            SetStatus("Saved!");
        }

        // Close button [X]
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
    //  Item List (left panel)
    // ===========================
    private void DrawItemList(int x, int y, int w, int h)
    {
        _ui.DrawRect(new Rectangle(x, y, w, h), new Color(30, 30, 40, 255));

        int curY = y + 8;

        // Search field
        _searchFilter = _ui.DrawSearchField("item_search", _searchFilter, x + 8, curY, w - 16);
        curY += 28;

        // Rebuild filtered list
        RebuildFilteredList();

        // Map selection
        int filteredSelectedIdx = -1;
        if (_selectedIdx >= 0)
        {
            var allIds = _gameData.Items.GetIDs();
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
            var def = _gameData.Items.Get(id);
            displayItems.Add(def?.DisplayName ?? id);
        }

        // Shared scrollable list with custom renderer for category color dots
        int clicked = _ui.DrawScrollableList("itemList", displayItems, filteredSelectedIdx,
            x, listY, w, listH, customRenderer: (i, rect, isSel) =>
            {
                var def = _gameData.Items.Get(_filteredIds[i]);
                if (def != null)
                {
                    Color catColor = GetCategoryColor(def.Category);
                    DrawSmallFilledCircle(x + 13, rect.Y + itemH / 2, 4, catColor);
                }
                _ui.DrawText(displayItems[i], new Vector2(rect.X + 20, rect.Y + 2),
                    isSel ? EditorBase.TextBright : EditorBase.TextColor);
            });

        if (clicked >= 0 && clicked < _filteredIds.Count)
        {
            string clickedId = _filteredIds[clicked];
            var allIds = _gameData.Items.GetIDs();
            _selectedIdx = IndexOf(allIds, clickedId);
            _detailScroll = 0;
        }

        // --- Bottom button row ---
        int btnY = y + h - 40;
        int btnW = (w - 24) / 3;

        if (_ui.DrawButton("+ New", x + 6, btnY, btnW, 28))
        {
            string newId = "item_" + DateTime.Now.ToString("HHmmss");
            var newDef = new ItemDef { Id = newId, DisplayName = "New Item" };
            var allIds = _gameData.Items.GetIDs();
            if (_selectedIdx >= 0 && _selectedIdx < allIds.Count)
                _gameData.Items.AddAfter(newDef, allIds[_selectedIdx]);
            else
                _gameData.Items.Add(newDef);
            _selectedIdx = IndexOf(_gameData.Items.GetIDs(), newId);
            MarkDirty();
            SetStatus("Added: " + newId);
        }

        if (_ui.DrawButton("Copy", x + 10 + btnW, btnY, btnW, 28))
        {
            var allIds = _gameData.Items.GetIDs();
            if (_selectedIdx >= 0 && _selectedIdx < allIds.Count)
            {
                var srcDef = _gameData.Items.Get(allIds[_selectedIdx]);
                if (srcDef != null)
                {
                    string newId = srcDef.Id + "_copy";
                    int suffix = 1;
                    while (_gameData.Items.Get(newId) != null)
                        newId = srcDef.Id + "_copy" + (++suffix);
                    var newDef = CloneItem(srcDef, newId);
                    _gameData.Items.AddAfter(newDef, srcDef.Id);
                    _selectedIdx = IndexOf(_gameData.Items.GetIDs(), newId);
                    MarkDirty();
                    SetStatus("Copied: " + newId);
                }
            }
        }

        if (_ui.DrawButton("Delete", x + 14 + btnW * 2, btnY, btnW, 28, EditorBase.DangerColor))
        {
            var allIds = _gameData.Items.GetIDs();
            if (_selectedIdx >= 0 && _selectedIdx < allIds.Count)
            {
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
        var allIds = _gameData.Items.GetIDs();
        if (_selectedIdx < 0 || _selectedIdx >= allIds.Count)
        {
            _ui.DrawText("Select an item from the list",
                new Vector2(x + 20, y + 40), EditorBase.TextDim);
            return;
        }

        var def = _gameData.Items.Get(allIds[_selectedIdx]);
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

        // Begin scissor clipping
        _ui.BeginClip(panelRect);

        int fieldW = w - 24;
        int curY = y + 8 - (int)_detailScroll;

        // Draw all annotated ItemDef fields via reflection
        var (nextY, changed) = _renderer.DrawAnnotatedProperties("item", def, x + 8, curY, fieldW);
        curY = nextY;
        if (changed) MarkDirty();

        // =================== POTION FIELDS ===================
        if (def.Category == "potion")
        {
            curY += 4;
            DrawSectionHeader(x + 8, ref curY, fieldW, "POTION PROPERTIES", new Color(120, 200, 255));

            // Find matching PotionDef by ItemID
            PotionDef? potionDef = FindPotionForItem(def.Id);

            if (potionDef == null)
            {
                _ui.DrawText("No potion definition linked.", new Vector2(x + 8, curY + 2), EditorBase.TextDim);
                curY += RowH;

                if (_ui.DrawButton("Create Potion Def", x + 8, curY, 180, 28))
                {
                    var newPotion = new PotionDef
                    {
                        Id = def.Id + "_potion",
                        DisplayName = def.DisplayName,
                        Icon = def.Icon,
                        Description = def.Description,
                        ItemID = def.Id,
                    };
                    _gameData.Potions.Add(newPotion);
                    MarkDirty();
                    SetStatus("Created potion def: " + newPotion.Id);
                }
                curY += RowH + 4;
            }
            else
            {
                DrawPotionFields(potionDef, x + 8, ref curY, fieldW);
            }
        }

        // End scissor clipping
        _ui.EndClip();

        // Clamp scroll to content
        float totalContentH = (curY + _detailScroll) - y;
        float maxDetailScroll = Math.Max(0, totalContentH - h + 20);
        _detailScroll = Math.Min(_detailScroll, maxDetailScroll);
    }

    // ===========================
    //  Potion fields
    // ===========================
    private void DrawPotionFields(PotionDef def, int x, ref int curY, int w)
    {
        // Draw all annotated PotionDef fields via reflection
        var (nextY, changed) = _renderer.DrawAnnotatedProperties("pot", def, x, curY, w);
        curY = nextY;
        if (changed) MarkDirty();

        // =================== RECIPE ===================
        curY += 4;
        DrawSectionHeader(x, ref curY, w, "RECIPE", new Color(200, 180, 100));

        def.Recipe ??= new List<RecipeIngredient>();

        // Ensure at least 3 slots for editing
        while (def.Recipe.Count < 3)
            def.Recipe.Add(new RecipeIngredient());

        for (int i = 0; i < 3; i++)
        {
            var ingredient = def.Recipe[i];

            // Label
            _ui.DrawText($"Slot {i + 1}", new Vector2(x, curY + 2), EditorBase.TextDim);

            // Item ID field (takes most of the width)
            int fieldX = x + 50;
            int idFieldW = w - 50 - 90;
            string oldItemId = ingredient.ItemId;
            ingredient.ItemId = _ui.DrawTextField($"recipe_{i}_id", "", ingredient.ItemId, fieldX, curY, idFieldW);
            if (ingredient.ItemId != oldItemId) MarkDirty();

            // Amount field
            int amtX = fieldX + idFieldW + 8;
            int oldAmt = ingredient.Amount;
            ingredient.Amount = _ui.DrawIntField($"recipe_{i}_amt", "", ingredient.Amount, amtX, curY, 80);
            if (ingredient.Amount != oldAmt) MarkDirty();

            curY += RowH;
        }
    }

    // ===========================
    //  Helpers
    // ===========================

    private PotionDef? FindPotionForItem(string itemId)
    {
        var potionIds = _gameData.Potions.GetIDs();
        foreach (var pid in potionIds)
        {
            var p = _gameData.Potions.Get(pid);
            if (p != null && p.ItemID == itemId)
                return p;
        }
        return null;
    }

    private void ExecuteDelete()
    {
        if (string.IsNullOrEmpty(_deleteConfirmId)) return;

        // Also remove linked potion def if category is potion
        var itemDef = _gameData.Items.Get(_deleteConfirmId);
        if (itemDef != null && itemDef.Category == "potion")
        {
            var potionDef = FindPotionForItem(_deleteConfirmId);
            if (potionDef != null)
                _gameData.Potions.Remove(potionDef.Id);
        }

        _gameData.Items.Remove(_deleteConfirmId);
        var allIds = _gameData.Items.GetIDs();
        if (_selectedIdx >= allIds.Count) _selectedIdx = allIds.Count - 1;
        MarkDirty();
        SetStatus("Deleted: " + _deleteConfirmId);
        _deleteConfirmId = "";
    }

    private void RebuildFilteredList()
    {
        _filteredIds.Clear();
        var allIds = _gameData.Items.GetIDs();
        if (string.IsNullOrEmpty(_searchFilter))
        {
            _filteredIds.AddRange(allIds);
            return;
        }
        for (int i = 0; i < allIds.Count; i++)
        {
            var def = _gameData.Items.Get(allIds[i]);
            if (def != null && (def.DisplayName.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase)
                             || def.Id.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase)))
                _filteredIds.Add(allIds[i]);
        }
    }

    private void MarkDirty()
    {
        _unsavedChanges = true;
    }

    private void SetStatus(string msg)
    {
        _statusMessage = msg;
        _statusTimer = 3f;
    }

    private void DrawSectionHeader(int x, ref int curY, int w, string text, Color color)
    {
        _ui.DrawRect(new Rectangle(x, curY, w, 1), new Color(60, 60, 80));
        curY += 6;
        _ui.DrawText(text, new Vector2(x, curY), color);
        curY += 22;
    }

    private static Color GetCategoryColor(string category)
    {
        return category switch
        {
            "material" => new Color(160, 140, 100),
            "potion" => new Color(120, 200, 255),
            "consumable" => new Color(100, 220, 130),
            "equipment" => new Color(220, 160, 80),
            _ => new Color(140, 140, 160),
        };
    }

    private static ItemDef CloneItem(ItemDef src, string newId)
    {
        return new ItemDef
        {
            Id = newId,
            DisplayName = src.DisplayName,
            Icon = src.Icon,
            MaxStack = src.MaxStack,
            Category = src.Category,
            Description = src.Description,
        };
    }

    /// <summary>
    /// Draw a small filled circle using the pixel texture (scanline approach).
    /// </summary>
    private void DrawSmallFilledCircle(int cx, int cy, int radius, Color color)
    {
        for (int dy = -radius; dy <= radius; dy++)
        {
            float hw = MathF.Sqrt(radius * radius - dy * dy);
            int px = (int)(cx - hw);
            int pw = (int)(hw * 2);
            if (pw > 0)
                _ui.DrawRect(new Rectangle(px, cy + dy, pw, 1), color);
        }
    }

    private static int IndexOf(IReadOnlyList<string> list, string value)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i] == value) return i;
        return -1;
    }
}
