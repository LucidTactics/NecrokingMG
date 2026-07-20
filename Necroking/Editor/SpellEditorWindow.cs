using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.Render;

namespace Necroking.Editor;

/// <summary>
/// Full spell definition editor with list panel, search, category-specific property editing,
/// flipbook manager popup, buff manager popup, and save.
/// Opened with F10.
/// </summary>
public class SpellEditorWindow : EditorWindow
{
    public override string Title => "SPELL EDITOR";

    private GameData _gameData = null!;

    /// <summary>Set HDR sprite effect for spell preview rendering.</summary>
    public void SetHdrEffect(Microsoft.Xna.Framework.Graphics.Effect? effect) => _hdrSpriteEffect = effect;

    /// <summary>Set flipbook dictionary for spell preview rendering.</summary>
    public void SetFlipbooks(Dictionary<string, Flipbook>? flipbooks) => _flipbooks = flipbooks;

    /// <summary>Set content manager for bloom shader loading in preview.</summary>
    public void SetContent(Microsoft.Xna.Framework.Content.ContentManager? content) => _content = content;

    // Spell list state
    private int _selectedIdx = -1;
    /// <summary>Select the first item in the list (for screenshot scenarios).</summary>
    public void SelectFirst() { _selectedIdx = 0; }

    /// <summary>Select a spell by display name (for screenshot scenarios).</summary>
    public void SelectByName(string name)
    {
        if (_gameData == null) return;
        var ids = _gameData.Spells.GetIDs();
        for (int i = 0; i < ids.Count; i++)
        {
            var def = _gameData.Spells.Get(ids[i]);
            if (def != null && def.DisplayName == name)
            {
                _selectedIdx = i;
                return;
            }
        }
    }

    /// <summary>Dev-server select by numeric index, def id, or display name
    /// (case-insensitive). Clears the search filter so the pick is visible in the
    /// list. Returns the selected spell's display name, or null if nothing matched.</summary>
    public string? DevSelect(string token)
    {
        if (_gameData == null) return null;
        var ids = _gameData.Spells.GetIDs();
        _searchFilter = "";
        if (int.TryParse(token, out int idx))
        {
            if (idx < 0 || idx >= ids.Count) return null;
            _selectedIdx = idx;
            return _gameData.Spells.Get(ids[idx])?.DisplayName ?? ids[idx];
        }
        for (int i = 0; i < ids.Count; i++)
        {
            var def = _gameData.Spells.Get(ids[i]);
            if (ids[i].Equals(token, StringComparison.OrdinalIgnoreCase)
                || (def?.DisplayName != null && def.DisplayName.Equals(token, StringComparison.OrdinalIgnoreCase)))
            { _selectedIdx = i; return def?.DisplayName ?? ids[i]; }
        }
        return null;
    }

    /// <summary>Open the buff manager popup (for UI test scenarios).</summary>
    public void OpenBuffManager() { _buffManagerOpen = true; _buffSelectedIdx = 0; }
    private string _searchFilter = "";
    private float _detailScroll;
    private List<string> _filteredIds = new();

    // Status bar + dirty flag now inherited from EditorWindow:
    //   StatusMessage, StatusTimer, IsDirty
    //   SetStatus(msg, durationSec), MarkDirty() (overridden below), ClearDirty()

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
    private Microsoft.Xna.Framework.Graphics.Effect? _hdrSpriteEffect;
    private Dictionary<string, Flipbook>? _flipbooks;
    private Microsoft.Xna.Framework.Content.ContentManager? _content;
    private bool _previewBloom = true;
    private bool _buffPreviewBloom = true;
    private int _lastPreviewSelectedIdx = -1;

    // Clipboard for Ctrl+C / Ctrl+V
    private SpellDef? _clipboardSpell;

    // Category options (no Command/Toggle for editing)
    // Option arrays still used by buff manager popup and FlipbookRefSection
    private static readonly string[] BlendOptions = { "Alpha", "Additive" };
    private static readonly string[] BlendTips =
    {
        "Standard transparent blending: the sprite\ncovers what is behind it.",
        "Colors add to the scene, overlaps get brighter.\nBest for glows, fire, magic. Dark pixels vanish.",
    };
    private static readonly string[] AlignOptions = { "Ground", "Upright" };
    private static readonly string[] AlignTips =
    {
        "Anchored at its center on the hit point,\nlike a splat lying on the ground.",
        "Anchored at its bottom edge on the hit point,\nstanding up like a pillar or rising burst.",
    };
    private static readonly string[] EffectTypeOptions = { "Set", "Add", "Multiply" };
    private static readonly string[] EffectTypeTips =
    {
        "Forces the stat to exactly Value, overriding\nthe base value and any Add/Multiply effects.",
        "Adds Value to the stat (times stack count).\nNegative subtracts. Applied before Multiply.",
        "Scales the stat by Value (per stack).\n1.5 = +50%, 0.5 = half, 2 = double.",
    };
    private static readonly string[] StatOptions =
        { "Strength", "Attack", "Defense", "MagicResist", "Toughness", "CombatSpeed", "MaxHP", "Encumbrance" };
    private static readonly string?[] StatTips =
    {
        null, null, null, null,
        "Halves incoming damage up to its value;\nheavy hits still punch through.",
        null, null,
        "Burden of gear: each attack builds fatigue\nequal to it, eroding defense over a long fight.",
    };
    private static readonly string[] IntBlendOptions = { "Alpha", "Additive" };

    // Layout constants
    private const int ListWidth = 300;
    private const int TopBarH = 50;
    private const int RowH = 24;
    private const int LabelW = 130;

    private ReflectionPropertyRenderer _renderer = null!;

    public SpellEditorWindow(EditorBase ui) : base(ui) { }

    public void SetGameData(GameData gameData)
    {
        _gameData = gameData;
        _renderer = new ReflectionPropertyRenderer(_ui, gameData);
    }

    // Modal-stack adapters for the two sub-popups. PopupManager pushes/pops
    // them automatically via SyncSubPopupsModalState (called below) so ESC
    // routes here without also closing the SpellEditor.
    private readonly Necroking.UI.ActionModalLayer _buffManagerLayer = new() { LightDismiss = false };
    private readonly Necroking.UI.ActionModalLayer _flipbookManagerLayer = new() { LightDismiss = false };
    private bool _modalLayersWired;

    private void SyncSubPopupsModalState(int screenW, int screenH)
    {
        if (!_modalLayersWired)
        {
            _buffManagerLayer.OnCancelAction = () => _buffManagerOpen = false;
            _flipbookManagerLayer.OnCancelAction = () => _flipbookManagerOpen = false;
            _modalLayersWired = true;
        }
        // Whole-screen rect — these popups are full-overlay so any click
        // anywhere is "inside." LightDismiss is false so this never matters
        // for outside-click handling, but matters for ContainsMouse contract.
        var full = new Rectangle(0, 0, screenW, screenH);
        _buffManagerLayer.Panel = full;
        _flipbookManagerLayer.Panel = full;

        bool buffOnStack = Necroking.Game1.Popups.Contains(_buffManagerLayer);
        if (_buffManagerOpen && !buffOnStack) Necroking.Game1.Popups.Push(_buffManagerLayer);
        else if (!_buffManagerOpen && buffOnStack) Necroking.Game1.Popups.Pop(_buffManagerLayer);

        bool flipOnStack = Necroking.Game1.Popups.Contains(_flipbookManagerLayer);
        if (_flipbookManagerOpen && !flipOnStack) Necroking.Game1.Popups.Push(_flipbookManagerLayer);
        else if (!_flipbookManagerOpen && flipOnStack) Necroking.Game1.Popups.Pop(_flipbookManagerLayer);
    }

    public void Draw(int screenW, int screenH, GameTime gameTime)
    {
        if (_gameData == null) return;

        float dt = (float)gameTime.ElapsedGameTime.TotalSeconds;
        TickChrome(dt);

        // Run modal stack sync BEFORE the existing keyboard / shortcut code so
        // PopupManager has consumed ESC correctly when this frame's chain runs.
        SyncSubPopupsModalState(screenW, screenH);

        // Handle Ctrl+S (and editor-specific shortcuts Ctrl+C / Ctrl+V)
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

        // --- Middle panel: scrollable detail editor (properties) ---
        int detailX = panelX + ListWidth + 1;
        int previewPanelW = (panelW - ListWidth - 1) / 2;
        int detailW = panelW - ListWidth - 1 - previewPanelW - 1;
        DrawDetailPanel(detailX, contentY, detailW, contentH);

        // --- Separator line ---
        _ui.DrawRect(new Rectangle(detailX + detailW, contentY, 1, contentH), new Color(80, 80, 100));

        // --- Right panel: spell preview ---
        int previewX = detailX + detailW + 1;
        DrawPreviewPanel(previewX, contentY, previewPanelW, contentH);

        // --- Popup overlays (drawn on top; each wraps itself in BeginOverlay
        // so the panels above are blocked and its own widgets work) ---
        if (_flipbookManagerOpen)
        {
            DrawFlipbookManagerPopup(screenW, screenH);
        }

        if (_buffManagerOpen)
        {
            // Update and render buff preview to its RenderTarget2D
            UpdateBuffPreview(dt);
            RenderBuffPreviewToTarget();

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
            _gameData.Save();
            ClearDirty();
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
            _selectedIdx = EditorBase.IndexOf(_gameData.Spells.GetIDs(), newId);
            MarkDirty();
            SetStatus("Pasted: " + newId);
        }

        // ESC handled by the UI router — dropdown, confirm dialog, buff
        // manager, flipbook manager all push their own modal layers onto
        // Game1.Popups (ModalStackLayer, LIFO); the spell editor itself is
        // covered by the EditorHostLayer seat.
    }

    // ===========================
    //  Top bar
    // ===========================
    private void DrawTopBar(int x, int y, int w, int h)
    {
        _ui.DrawRect(new Rectangle(x, y, w, h), new Color(25, 25, 35, 255));
        _ui.DrawRect(new Rectangle(x, y + h - 1, w, 1), new Color(80, 80, 100));

        // Title (with dirty asterisk from inherited IsDirty)
        string title = IsDirty ? $"{Title} *" : Title;
        var titleSize = _ui.MeasureText(title);
        _ui.DrawText(title, new Vector2(x + (w - titleSize.X) / 2, y + (h - titleSize.Y) / 2),
            EditorBase.TextBright);

        // Unsaved dot
        if (IsDirty)
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
            _gameData.Save();
            ClearDirty();
            SetStatus("Saved!");
        }

        // Close button [X] (RS01)
        if (_ui.DrawButton("X", x + w - 40, y + 10, 30, 30))
            WantsClose = true;

        // Status message (inherited from EditorWindow chrome)
        if (StatusTimer > 0 && !string.IsNullOrEmpty(StatusMessage))
        {
            float alpha = Math.Min(1f, StatusTimer);
            int a = (int)(alpha * 255);
            Color c = StatusMessage.Contains("FAIL") ? new Color(255, 80, 80, a) : new Color(100, 255, 100, a);
            _ui.DrawText(StatusMessage, new Vector2(x + 20, y + 17), c);
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
            displayItems.Add(_gameData.Spells.NameOf(id));

        // Shared scrollable list with custom renderer for category color dots
        int clicked = _ui.DrawScrollableList("spellList", displayItems, filteredSelectedIdx,
            x, listY, w, listH, customRenderer: (i, rect, isSel) =>
            {
                var def = _gameData.Spells.Get(_filteredIds[i]);
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
            var allIds = _gameData.Spells.GetIDs();
            _selectedIdx = EditorBase.IndexOf(allIds, clickedId);
            _detailScroll = 0;
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
            _selectedIdx = EditorBase.IndexOf(_gameData.Spells.GetIDs(), newId);
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
                    _selectedIdx = EditorBase.IndexOf(_gameData.Spells.GetIDs(), newId);
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

        // Scroll handling. Id-keyed overload clamps to the end using last frame's
        // content height (recorded below) so the wheel can't overshoot and snap back.
        var panelRect = new Rectangle(x, y, w, h);
        _ui.HandlePanelScroll(panelRect, ref _detailScroll, "sp_detail", h - 20, sensitivity: 0.4f);

        // Clipping background
        _ui.DrawRect(panelRect, new Color(28, 28, 42, 200));

        // RS22: Begin scissor clipping to prevent overflow
        _ui.BeginClip(panelRect);

        int fieldW = w - 24;
        int curY = y + 8 - (int)_detailScroll;

        // =================== ALL FIELDS VIA REFLECTION ===================
        var (nextY, changed) = _renderer.DrawAnnotatedProperties("sp", def, x + 8, curY, fieldW);
        curY = nextY;
        if (changed) MarkDirty();

        // =================== SHARED FIELDS (manual, compound visibility) ===================
        // AoeRadius — different conditions per category
        if (def.Category == "Projectile" ||
            (def.Category is "Buff" or "Debuff" && def.AoeType == "AOE") ||
            (def.Category == "Strike" && !def.StrikeTargetUnit))
        {
            float oldAoeR = def.AoeRadius;
            def.AoeRadius = _ui.DrawFloatField("sp_aoer", "AOE Radius", def.AoeRadius, x + 8, curY, fieldW, 0.1f);
            def.AoeRadius = MathF.Round(def.AoeRadius * 10f) / 10f;
            _ui.RowTip(x + 8, curY, fieldW, RowH,
                "Radius around the hit point that the effect covers.");
            curY += RowH;
            if (MathF.Abs(def.AoeRadius - oldAoeR) > 0.001f) MarkDirty();
        }

        // HitEffectFlipbook — shown in Projectile (via reflection) and Strike ground
        if (def.Category == "Strike" && !def.StrikeTargetUnit)
        {
            def.HitEffectFlipbook = DrawFlipbookRefSection("sp_st_hitfb", "Hit Effect", def.HitEffectFlipbook, x + 8, ref curY, fieldW);
        }

        // RS22: End scissor clipping
        _ui.EndClip();

        // Clamp scroll to content
        float totalContentH = (curY + _detailScroll) - y;
        float maxDetailScroll = Math.Max(0, totalContentH - h + 20);
        _detailScroll = Math.Min(_detailScroll, maxDetailScroll);
        _ui.SetPanelContentHeight("sp_detail", totalContentH);
    }

    // Draw*Fields methods removed — all fields now rendered via reflection attributes on SpellDef.
    // Only DrawFlipbookRefSection kept below for manual HitEffectFlipbook in Strike.

    // ===========================
    //  DELETED: DrawProjectileFields, DrawBuffDebuffFields, DrawSummonFields,
    //           DrawStrikeFields, DrawBeamFields, DrawDrainFields
    //  (replaced by [EditorField] attributes on SpellDef properties)
    // ===========================

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
        _ui.RowTip(x, curY - RowH, w, RowH, "Which effect animation plays.");

        // FPS
        fb.FPS = DrawFloatField10x(prefix + "_fps", "FPS", fb.FPS, x, ref curY, w);
        if (fb.FPS < 0) _ui.DrawText("(default)", new Vector2(x + w - 70, curY - RowH + 4), new Color(120, 120, 140));
        _ui.RowTip(x, curY - RowH, w, RowH,
            "Playback speed (frames/sec). -1 = the flipbook's own rate.");

        // Scale
        fb.Scale = DrawFloatField100x(prefix + "_scl", "Scale", fb.Scale, x, ref curY, w);
        _ui.RowTip(x, curY - RowH, w, RowH, "Size multiplier for the effect.");

        // Rotation
        float oldRot = fb.Rotation;
        fb.Rotation = _ui.DrawFloatField(prefix + "_rot", "Rotation", fb.Rotation, x, curY, w, 1f);
        if (MathF.Abs(fb.Rotation - oldRot) > 0.01f) MarkDirty();
        _ui.RowTip(x, curY, w, RowH, "Rotates the effect sprite (degrees).");
        curY += RowH;

        // Color swatch
        var c = fb.Color;
        c = DrawHdrColorSwatch(prefix + "_col", "Color", c, x, ref curY, w);
        fb.Color = c;
        _ui.RowTip(x, curY - RowH, w, RowH, "Tint color; intensity above 1 makes it glow.");

        // Blend Mode toggle
        string oldBlend = fb.BlendMode;
        fb.BlendMode = _ui.DrawCombo(prefix + "_blend", "Blend", fb.BlendMode, BlendOptions, x, curY, w,
            optionTooltips: BlendTips);
        if (fb.BlendMode != oldBlend) MarkDirty();
        _ui.RowTip(x, curY, w, RowH, "Alpha = solid/smoky look; Additive = glowing light look.");
        curY += RowH;

        // Alignment toggle
        string oldAlign = fb.Alignment;
        fb.Alignment = _ui.DrawCombo(prefix + "_align", "Alignment", fb.Alignment, AlignOptions, x, curY, w,
            optionTooltips: AlignTips);
        if (fb.Alignment != oldAlign) MarkDirty();
        _ui.RowTip(x, curY, w, RowH, "Ground = flat on the terrain; Upright = stands facing the camera.");
        curY += RowH;

        // Duration
        fb.Duration = DrawFloatField100x(prefix + "_dur", "Duration", fb.Duration, x, ref curY, w);
        if (fb.Duration < 0) _ui.DrawText("(default 0.4s)", new Vector2(x + w - 100, curY - RowH + 4), new Color(120, 120, 140));
        _ui.RowTip(x, curY - RowH, w, RowH, "Seconds the effect lasts. -1 = one full playthrough.");

        return fb;
    }

    // ======================================
    //  Flipbook Manager Popup
    // ======================================
    private void DrawFlipbookManagerPopup(int screenW, int screenH)
    {
        // Update texture file browser input
        _fbTextureBrowser.Update(_ui, _ui._mouse, _ui._prevMouse, _ui._kb, _ui._prevKb);

        // Overlay contract: blocks the spell editor's panels (which drew
        // earlier this frame) and lets this popup's widgets interact at layer 1.
        _ui.BeginOverlay(1);

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

        // Close button
        if (_ui.DrawButton("X", px + pw - 40, py + 5, 30, 30))
        {
            _flipbookManagerOpen = false;
            _ui.EndOverlay();
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
        _ui.HandlePanelScroll(fbListRect, ref _fbManagerScroll, maxFbScroll, sensitivity: 0.15f, layer: 1);

        // Clip the list area so partially-scrolled top/bottom rows don't spill
        // into the surrounding popup chrome.
        _ui.BeginClip(fbListRect);
        float fbDrawY = listY - _fbManagerScroll;
        for (int i = 0; i < fbIDs.Count; i++)
        {
            if (fbDrawY + itemH < listY) { fbDrawY += itemH; continue; }
            if (fbDrawY >= listY + listH) break;

            var iRect = new Rectangle(px + 4, (int)fbDrawY, listW - 8, itemH);
            bool sel = (i == _fbSelectedIdx);
            bool hov = !_ui.IsInputBlocked(_ui.EffectiveLayer(0)) && _ui.HitTest(iRect);
            if (hov) _ui.SetMouseOverUI(); // drift fix: buff list already did this

            Color bg = sel ? new Color(60, 60, 90) : (hov ? new Color(45, 45, 60) : Color.Transparent);
            _ui.DrawRect(iRect, bg);

            var fd = _gameData.Flipbooks.Get(fbIDs[i]);
            _ui.DrawText(fd?.DisplayName ?? fbIDs[i], new Vector2(px + 12, fbDrawY + 5),
                sel ? EditorBase.TextBright : EditorBase.TextColor);

            if (hov && _ui._mouse.LeftButton == ButtonState.Pressed && _ui._prevMouse.LeftButton == ButtonState.Released)
            {
                // Abandon any in-progress field edit — the fb_* field ids are
                // selection-agnostic, so an active buffer would otherwise be
                // committed into the newly selected flipbook.
                if (i != _fbSelectedIdx) _ui.ClearActiveField();
                _fbSelectedIdx = i;
            }

            fbDrawY += itemH;
        }
        _ui.EndClip();

        // Button row
        int btnRowY = py + ph - 44;
        int btnW = (listW - 24) / 3;

        if (_ui.DrawButton("+ New", px + 6, btnRowY, btnW, 32))
        {
            // Unique id: "flipbook_N" can collide after deletions (Add is an
            // upsert and would silently overwrite the existing def).
            int n = _gameData.Flipbooks.Count;
            string newId = "flipbook_" + n;
            while (_gameData.Flipbooks.Get(newId) != null) newId = "flipbook_" + ++n;
            var nf = new FlipbookDef
            {
                Id = newId,
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
                    var copy = _gameData.Flipbooks.CloneDef(src, newId) ?? new FlipbookDef { Id = newId };
                    copy.DisplayName = src.DisplayName + " (Copy)";
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
            _flipbookManagerOpen = false;
            _ui.EndOverlay();
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
                    _fbTextureBrowser.Open(GamePaths.Resolve("assets/Effects"), fd.Path, path =>
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

        // Texture file browser popup (drawn on top of flipbook manager; runs
        // at its own higher overlay layer, blocking this popup's widgets)
        _fbTextureBrowser.Draw(_ui, screenW, screenH);

        _ui.EndOverlay();
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
                // Re-sync only when the selection changed or an edit marked the
                // preview dirty — NOT every frame (SetBuff sets the preview's
                // dirty flag, and a permanently-dirty preview re-syncs its orbs
                // to spawn angles each frame, freezing the orbit animation).
                if (_buffSelectedIdx != _lastBuffPreviewIdx || _buffPreviewDirty)
                {
                    _lastBuffPreviewIdx = _buffSelectedIdx;
                    _buffPreviewDirty = false;
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

        // Suspend the shared batch so the preview can render to its RT, then
        // resume. Same captured scope for both halves of the cycle.
        var scope = _ui.Scope;
        scope.Suspend();

        _buffPreview.RenderToTarget();

        scope.Resume();
    }

    private void DrawBuffManagerPopup(int screenW, int screenH)
    {
        // Overlay contract: blocks the spell editor's panels (which drew
        // earlier this frame) and lets this popup's widgets interact at layer 1.
        _ui.BeginOverlay(1);

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
            _ui.EndOverlay();
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
        _ui.HandlePanelScroll(buffListRect, ref _buffManagerScroll, maxBuffScroll, sensitivity: 0.15f, layer: 1);

        // Clip the list area so partially-scrolled top/bottom rows don't spill
        // into the surrounding popup chrome.
        _ui.BeginClip(buffListRect);
        float buffDrawY = listY - _buffManagerScroll;
        for (int i = 0; i < buffIDs.Count; i++)
        {
            if (buffDrawY + itemH < listY) { buffDrawY += itemH; continue; }
            if (buffDrawY >= listY + listH) break;

            var iRect = new Rectangle(px + 4, (int)buffDrawY, listW - 8, itemH);
            bool sel = (i == _buffSelectedIdx);
            // Inert while anything above owns input (color picker, open
            // dropdown, texture browser) — the effective-layer check covers all.
            bool hov = !_ui.IsInputBlocked(_ui.EffectiveLayer(0)) && _ui.HitTest(iRect);
            if (hov) _ui.SetMouseOverUI();

            Color bg = sel ? new Color(60, 60, 90) : (hov ? new Color(45, 45, 60) : Color.Transparent);
            _ui.DrawRect(iRect, bg);

            var bd = _gameData.Buffs.Get(buffIDs[i]);
            _ui.DrawText(bd?.DisplayName ?? buffIDs[i], new Vector2(px + 12, buffDrawY + 5),
                sel ? EditorBase.TextBright : EditorBase.TextColor);

            if (hov && _ui._mouse.LeftButton == ButtonState.Pressed && _ui._prevMouse.LeftButton == ButtonState.Released)
            {
                // Same wrong-object-commit guard as the flipbook list (bf_* ids
                // are selection-agnostic).
                if (i != _buffSelectedIdx) _ui.ClearActiveField();
                _buffSelectedIdx = i;
            }

            buffDrawY += itemH;
        }
        _ui.EndClip();

        // Button row
        int btnRowY = py + ph - 44;
        int btnW = (listW - 24) / 3;

        if (_ui.DrawButton("+ New", px + 6, btnRowY, btnW, 32))
        {
            // Unique id: "buff_N" can collide after deletions (Add is an
            // upsert and would silently overwrite the existing def).
            int n = _gameData.Buffs.Count;
            string newId = "buff_" + n;
            while (_gameData.Buffs.Get(newId) != null) newId = "buff_" + ++n;
            var nb = new BuffDef
            {
                Id = newId,
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
        {
            _buffManagerOpen = false;
            _ui.EndOverlay();
            return;
        }

        // --- Buff detail (right side, scrollable) ---
        var currentBuffIDs = _gameData.Buffs.GetIDs();
        if (_buffSelectedIdx >= 0 && _buffSelectedIdx < currentBuffIDs.Count)
        {
            var bd = _gameData.Buffs.Get(currentBuffIDs[_buffSelectedIdx]);
            if (bd != null)
                DrawBuffDetail(bd, contentX, contentY, contentW, btnRowY - contentY - 10);
        }

        _ui.EndOverlay();
    }

    // ======================================
    //  Buff detail panel (inside popup)
    // ======================================
    private void DrawBuffDetail(BuffDef bd, int contentX, int detailY, int contentW, int detailH)
    {
        int fieldW = contentW - 10;
        int rowH = 26;

        // Scroll handling for buff detail area. Id-keyed overload clamps to the
        // end using last frame's content height (recorded below) — no overshoot.
        var detailRect = new Rectangle(contentX, detailY, contentW, detailH);
        _ui.HandlePanelScroll(detailRect, ref _buffDetailScroll, "sp_buffdetail", detailH - 20, sensitivity: 0.4f, layer: 1);

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
                _ui.Scope.Draw(tex, new Rectangle(contentX, cy, previewW, previewH), Color.White);

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
            eff.Type = _ui.DrawCombo($"bf_eff{e}_type", "", eff.Type, EffectTypeOptions, contentX, cy, 90,
                optionTooltips: EffectTypeTips);
            if (eff.Type != oldType) MarkDirty();

            // Stat dropdown
            string oldStat = eff.Stat;
            eff.Stat = _ui.DrawCombo($"bf_eff{e}_stat", "", eff.Stat, StatOptions, contentX + 94, cy, 120,
                optionTooltips: StatTips);
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
            bool oldAF = wp.AttachedFlame;
            wp.AttachedFlame = _ui.DrawCheckbox("Attached Flame", wp.AttachedFlame, contentX, cy);
            if (wp.AttachedFlame != oldAF) MarkDirty();
            cy += RowH;
            if (wp.AttachedFlame)
            {
                // Attached mode: one persistent flame pinned at RangeMax along
                // hilt→tip; the spawn/lifetime/drift machinery is inert, so its
                // rows are hidden (their stored values survive unchecking).
                wp.RangeMax = DrawFloatField100x("bf_wp_rmax", "Flame Pos", wp.RangeMax, contentX, ref cy, fieldW);
                wp.ParticleScale = DrawFloatField100x("bf_wp_pscl", "Part Scale", wp.ParticleScale, contentX, ref cy, fieldW);
            }
            else
            {
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
            }
            wp.ScatterRadius = DrawFloatField100x("bf_wp_scr", "Scatter Radius", wp.ScatterRadius, contentX, ref cy, fieldW);
            wp.ScatterStrength = DrawFloatField100x("bf_wp_scs", "Scatter Str", wp.ScatterStrength, contentX, ref cy, fieldW);
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
        _ui.SetPanelContentHeight("sp_buffdetail", totalBuffContentH);
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
        string result = _ui.DrawCombo(prefix, "Blend", current, IntBlendOptions, x, cy, w,
            optionTooltips: BlendTips);
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
    //  Helper: Flipbook ID dropdown
    // ======================================
    private string DrawFlipbookDropdown(string fieldId, string label, string currentId,
        int x, ref int curY, int w)
    {
        var fbIDs = _gameData.Flipbooks.GetIDs();
        var names = new string[fbIDs.Count];
        int currentIdx = -1;
        for (int i = 0; i < fbIDs.Count; i++)
        {
            var fd = _gameData.Flipbooks.Get(fbIDs[i]);
            names[i] = fd?.DisplayName ?? fbIDs[i];
            if (currentId == fbIDs[i]) currentIdx = i;
        }

        string currentName = currentIdx >= 0 ? names[currentIdx] : "";
        string newName = _ui.DrawCombo(fieldId, label, currentName, names, x, curY, w, allowNone: true,
            optionTooltipFor: fi => DefTips.ForFlipbook(_gameData.Flipbooks.Get(fbIDs[fi])));
        curY += RowH;

        if (newName != currentName)
        {
            MarkDirty();
            if (string.IsNullOrEmpty(newName)) return "";
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
    //  Delete confirmation execution
    // ======================================
    private void ExecuteDelete()
    {
        switch (_deleteConfirmTarget)
        {
            case "spell":
            {
                var allIds = _gameData.Spells.GetIDs();
                if (EditorBase.IndexOf(allIds, _deleteConfirmId) >= 0)
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
                if (EditorBase.IndexOf(fbIds, _deleteConfirmId) >= 0)
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
                if (EditorBase.IndexOf(buffIds, _deleteConfirmId) >= 0)
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
        _spellPreview.Init(_ui._gd, _ui._pixel, _hdrSpriteEffect, _flipbooks, _content);
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

        // Suspend the shared batch so the preview can render to its RT, then
        // resume. Same captured scope for both halves of the cycle.
        var scope = _ui.Scope;
        scope.Suspend();

        _spellPreview.RenderToTarget();

        scope.Resume();
    }

    private void DrawPreviewPanel(int x, int y, int w, int h)
    {
        var allIds = _gameData.Spells.GetIDs();
        if (_selectedIdx < 0 || _selectedIdx >= allIds.Count) return;
        var def = _gameData.Spells.Get(allIds[_selectedIdx]);
        if (def == null) return;

        // Background
        _ui.DrawRect(new Rectangle(x, y, w, h), new Color(22, 22, 35, 200));

        int pad = 8;
        int curY2 = y + pad;

        // Draw preview filling the panel width
        curY2 = DrawSpellPreviewSection(def, x + pad, curY2, w - pad * 2);
    }

    private int DrawSpellPreviewSection(SpellDef def, int x, int curY, int fieldW)
    {
        if (_spellPreview == null || !_spellPreview.IsInitialized) return curY;

        // Size the preview to fill available width with 5:3 aspect ratio
        int previewW = fieldW;
        int previewH = (int)(fieldW * 0.6f);
        _spellPreview.Resize(previewW, previewH);

        var tex = _spellPreview.GetTexture();
        if (tex == null) return curY;

        // Border
        _ui.DrawRect(new Rectangle(x - 1, curY - 1, previewW + 2, previewH + 2), new Color(60, 60, 80));

        // Draw the preview texture at native resolution (no scaling needed since RT matches)
        _ui.Scope.Draw(tex, new Rectangle(x, curY, previewW, previewH), Color.White);

        // Category label overlay
        string catLabel = def.Category.ToUpperInvariant() + " PREVIEW";
        _ui.DrawText(catLabel, new Vector2(x + 4, curY + 2), new Color(255, 255, 255, 120));

        curY += previewH + 4;

        // Bloom checkbox
        bool oldBloom = _previewBloom;
        _previewBloom = _ui.DrawCheckbox("Bloom", _previewBloom, x, curY);
        if (_previewBloom != oldBloom && _spellPreview != null)
            _spellPreview.BloomEnabled = _previewBloom;

        // Loop indicator next to Bloom checkbox
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

    // MarkDirty is the base EditorWindow.MarkDirty plus a preview-cache invalidation.
    // The base method sets IsDirty; we also tell the spell preview to redraw.
    protected override void MarkDirty()
    {
        base.MarkDirty();
        // Dispatch to the preview the user is actually looking at: buff-manager
        // edits invalidate the buff preview (its orbs/arcs resync on dirty);
        // everything else restarts the spell preview. Restarting the spell
        // preview for every buff-color tweak was pure churn.
        if (_buffManagerOpen)
            _buffPreviewDirty = true;
        else
            _spellPreview?.MarkDirty();
        // Flipbook grid/path edits must also reach the RUNTIME flipbook
        // dictionary the previews (and the game) render from — without this,
        // "Apply & Close" applied nothing until a map reload.
        if (_flipbookManagerOpen)
            Necroking.Game1.Instance?.ReloadFlipbooksFromRegistry();
    }

    // Set by MarkDirty while the buff manager is open; consumed by
    // UpdateBuffPreview. Pushing SetBuff every frame (the old approach)
    // kept the preview permanently dirty — orbs re-synced to their spawn
    // angles each frame (never orbiting) and lightning re-rolled at
    // framerate regardless of JitterHz.
    private bool _buffPreviewDirty;

    // SetStatus is inherited (default 2s timer). Old SpellEditor used 3s;
    // the 1s difference is imperceptible.

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

    // Clones go through the registry's JSON round-trip (RegistryBase.CloneDef):
    // clone fidelity == save/load fidelity, so new SpellDef/BuffDef fields can
    // never be silently dropped by Copy/Paste. (The old reflection copy here
    // skipped every complex sub-object it didn't special-case; the manual
    // BuffDef copy needed updating for every new visual block.)
    private SpellDef CloneSpell(SpellDef src, string newId)
    {
        var def = _gameData.Spells.CloneDef(src, newId) ?? new SpellDef { Id = newId };
        def.DisplayName = src.DisplayName + " (Copy)";
        return def;
    }

    private BuffDef CloneBuff(BuffDef src, string newId)
    {
        var def = _gameData.Buffs.CloneDef(src, newId) ?? new BuffDef { Id = newId };
        def.DisplayName = src.DisplayName + " (Copy)";
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

}
