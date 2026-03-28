using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Necroking.Core;
using Necroking.GameSystems;
using Necroking.World;

namespace Necroking.Editor;

/// <summary>
/// Full-screen overlay editor for editing EnvironmentObjectDef definitions.
/// Provides a left panel (def list), center panel (sprite preview with collision overlay),
/// and right panel (property editing fields).
/// </summary>
public class EnvObjectEditorWindow
{
    // --- Dependencies ---
    private EditorBase _ui = null!;
    private EnvironmentSystem _env = null!;
    private TriggerSystem? _triggerSystem;
    private GraphicsDevice _device = null!;
    private Texture2D _pixel = null!;
    private SpriteBatch _sb = null!;
    private SpriteFont? _font;
    private SpriteFont? _smallFont;

    // --- State ---
    public bool IsOpen;
    private int _selectedDef = -1;
    private int _categoryFilter; // 0 = All
    private string _searchFilter = "";

    // Collision editing via drag
    private bool _draggingCollisionCenter;
    private bool _draggingCollisionRadius;
    private bool _hoveringCollisionCenter;
    private bool _hoveringCollisionEdge;

    // Pivot editing via click
    private bool _draggingPivot;

    // Reference image slots (up to 3 env object indices for scale comparison)
    private readonly int[] _referenceDefIndices = { -1, -1, -1 };
    // Cached env object name list for reference picker DrawCombo (invalidated when def count changes)
    private string[]? _cachedEnvObjectNames;
    private int _cachedEnvObjectCount = -1;

    // Preview texture cache
    private Texture2D? _previewTex;
    private int _previewDefIdx = -1;
    private string _previewTexPath = "";

    // Cached preview layout (set during DrawPreviewPanel, used by HandleCollisionDrag / HandlePivotDrag)
    private int _pvDrawX, _pvDrawY, _pvDrawW, _pvDrawH;
    private float _pvScale;
    private int _pvAreaX, _pvAreaY, _pvAreaW, _pvAreaH;

    // Scroll
    private float _propScrollY;
    private float _defListScrollY;

    // Save status
    private string _statusMessage = "";
    private float _statusTimer;

    // Delete confirmation
    private bool _confirmDeleteOpen;

    // New category dialog
    private bool _newCategoryDialogOpen;
    private string _newCategoryName = "";

    // Texture file browser (UI01)
    private readonly TextureFileBrowser _textureBrowser = new();
    private MouseState _prevMouseEnv;
    private KeyboardState _prevKbEnv;

    // M04: Color harmonizer
    private readonly ColorHarmonizer _harmonizer = new();
    private bool _harmonizerOpen;

    // M05: Edge tweaker
    private bool _edgeTweakerOpen;
    private int _edgeTweakerThreshold = 20;
    private Texture2D? _edgeTweakerPreview;
    private Color[]? _edgeTweakerPixels;
    private int _edgeTweakerTexW;
    private int _edgeTweakerTexH;

    // Layout constants
    private const int LeftPanelW = 200;
    private const int CenterPanelW = 400;
    private const int TopBarH = 36;
    private const int RowH = 24;
    private const int Padding = 8;
    private const int BtnH = 24;
    private const int RefPanelH = 200; // height reserved for 2x2 reference image grid (M06)

    // Colors
    private static readonly Color OverlayBg = new(0, 0, 0, 200);
    private static readonly Color PanelBg = new(25, 25, 40, 245);
    private static readonly Color HeaderBg = new(40, 40, 65, 250);
    private static readonly Color BorderColor = new(80, 80, 120);
    private static readonly Color PreviewBg = new(18, 18, 30, 255);
    private static readonly Color CollisionColor = new(255, 100, 100, 140);
    private static readonly Color CollisionHoverCenterColor = new(255, 220, 80, 200);
    private static readonly Color CollisionHoverEdgeColor = new(80, 220, 255, 200);
    private static readonly Color PivotColor = new(100, 255, 100, 200);
    private static readonly Color RefSlotBg = new(30, 30, 50, 240);

    public void Init(EditorBase ui, EnvironmentSystem env, GraphicsDevice device,
        SpriteBatch sb, Texture2D pixel, SpriteFont? font, SpriteFont? smallFont,
        TriggerSystem? triggerSystem = null)
    {
        _ui = ui;
        _env = env;
        _triggerSystem = triggerSystem;
        _device = device;
        _sb = sb;
        _pixel = pixel;
        _font = font;
        _smallFont = smallFont;
    }

    public void Open()
    {
        IsOpen = true;
        _selectedDef = _env.DefCount > 0 ? 0 : -1;
        _categoryFilter = 0;
        _searchFilter = "";
        _propScrollY = 0;
        _defListScrollY = 0;
        _confirmDeleteOpen = false;
        _newCategoryDialogOpen = false;
        _draggingCollisionCenter = false;
        _draggingCollisionRadius = false;
        _draggingPivot = false;
        _cachedEnvObjectNames = null;
        _cachedEnvObjectCount = -1;
        _harmonizerOpen = false;
        _harmonizer.Cancel();
        _edgeTweakerOpen = false;
        _edgeTweakerPreview = null;
        _edgeTweakerPixels = null;
        for (int i = 0; i < _referenceDefIndices.Length; i++)
            _referenceDefIndices[i] = -1;
        ReloadPreview();
    }

    public void Close()
    {
        IsOpen = false;
        _previewTex = null;
        _previewDefIdx = -1;
    }

    // ========================================================================
    //  Update + Draw (called from MapEditorWindow)
    // ========================================================================

    public void Update()
    {
        if (!IsOpen) return;

        // Status timer (assume ~60fps)
        if (_statusTimer > 0)
            _statusTimer -= 1f / 60f;

        // Update texture file browser input
        var mouse = Mouse.GetState();
        var kb = Keyboard.GetState();
        _textureBrowser.Update(mouse, _prevMouseEnv, kb, _prevKbEnv);
        _prevMouseEnv = mouse;
        _prevKbEnv = kb;
    }

    public void Draw(int screenW, int screenH)
    {
        if (!IsOpen) return;

        // Dark overlay
        _ui.DrawRect(new Rectangle(0, 0, screenW, screenH), OverlayBg);

        // Main window area (inset from edges)
        int winX = 20, winY = 20;
        int winW = screenW - 40, winH = screenH - 40;

        // Window background + border
        _ui.DrawRect(new Rectangle(winX, winY, winW, winH), PanelBg);
        _ui.DrawBorder(new Rectangle(winX, winY, winW, winH), BorderColor, 2);

        // Title bar
        _ui.DrawRect(new Rectangle(winX, winY, winW, TopBarH), HeaderBg);
        _ui.DrawRect(new Rectangle(winX, winY + TopBarH, winW, 1), BorderColor);
        _ui.DrawText("Environment Object Definition Editor", new Vector2(winX + 10, winY + 8), EditorBase.TextBright, _font);

        // Close button (top-right)
        if (_ui.DrawButton("X", winX + winW - 36, winY + 4, 28, 24, EditorBase.DangerColor))
            Close();

        // Save button
        if (_ui.DrawButton("Save (Ctrl+S)", winX + winW - 180, winY + 4, 130, 24, EditorBase.AccentColor))
            SaveDefs();

        // Status message
        if (_statusTimer > 0 && !string.IsNullOrEmpty(_statusMessage))
        {
            _ui.DrawText(_statusMessage, new Vector2(winX + 220, winY + 10), EditorBase.SuccessColor);
        }

        // Content area
        int contentY = winY + TopBarH + 2;
        int contentH = winH - TopBarH - 2;

        // Left panel: def list
        int leftX = winX;
        int leftW = LeftPanelW;
        DrawDefListPanel(leftX, contentY, leftW, contentH);

        // Center panel: preview
        int centerX = leftX + leftW + 1;
        int centerW = CenterPanelW;
        if (centerW > winW - leftW - 200) centerW = winW - leftW - 200; // ensure right panel has space
        DrawPreviewPanel(centerX, contentY, centerW, contentH);

        // Right panel: properties
        int rightX = centerX + centerW + 1;
        int rightW = winW - leftW - centerW - 2;
        DrawPropertiesPanel(rightX, contentY, rightW, contentH);

        // Handle keyboard
        HandleKeyboard();

        // Overlays (dialogs) - draw last for z-order
        if (_confirmDeleteOpen)
            DrawConfirmDeleteDialog(screenW, screenH);

        if (_newCategoryDialogOpen)
            DrawNewCategoryDialog(screenW, screenH);

        // M05: Edge tweaker popup
        if (_edgeTweakerOpen)
            DrawEdgeTweakerPopup(screenW, screenH);

        // Texture file browser popup
        _textureBrowser.Draw(_ui, screenW, screenH);

        // Dropdown overlays (drawn last, on top of everything)
        _ui.DrawDropdownOverlays();
    }

    // ========================================================================
    //  LEFT PANEL - Def List
    // ========================================================================

    private void DrawDefListPanel(int x, int y, int w, int h)
    {
        _ui.DrawRect(new Rectangle(x, y, w, h), new Color(20, 20, 35, 230));
        _ui.DrawRect(new Rectangle(x + w, y, 1, h), BorderColor);

        int curY = y + Padding;

        // Category filter dropdown
        var categories = GetCategories();
        string[] catOptions = categories.ToArray();
        string currentCat = _categoryFilter < catOptions.Length ? catOptions[_categoryFilter] : "All";
        string newCat = _ui.DrawCombo("envdef_cat_filter", "Category", currentCat, catOptions, x + Padding, curY, w - Padding * 2);
        int newCatIdx = Array.IndexOf(catOptions, newCat);
        if (newCatIdx >= 0) _categoryFilter = newCatIdx;
        curY += RowH + 4;

        // Search field
        _searchFilter = _ui.DrawSearchField("envdef_search", _searchFilter, x + Padding, curY, w - Padding * 2);
        curY += RowH + 4;

        // New / Copy / Delete buttons
        int btnW = (w - Padding * 2 - 8) / 3;
        if (_ui.DrawButton("New", x + Padding, curY, btnW, BtnH, EditorBase.AccentColor))
            CreateNewDef();
        if (_ui.DrawButton("Copy", x + Padding + btnW + 4, curY, btnW, BtnH))
            CopySelectedDef();
        if (_ui.DrawButton("Del", x + Padding + (btnW + 4) * 2, curY, btnW, BtnH, EditorBase.DangerColor))
        {
            if (_selectedDef >= 0)
                _confirmDeleteOpen = true;
        }
        curY += BtnH + 4;

        // Separator
        _ui.DrawRect(new Rectangle(x, curY, w, 1), BorderColor);
        curY += 2;

        // Def list (scrollable)
        var filtered = GetFilteredDefs();
        int listH = y + h - curY;
        int listItemH = 22;

        // Build label list for DrawScrollableList
        var labels = new List<string>();
        for (int i = 0; i < filtered.Count; i++)
        {
            var def = _env.GetDef(filtered[i]);
            string label = def.Name;
            if (string.IsNullOrEmpty(label)) label = def.Id;
            if (def.IsBuilding) label += " [B]";
            labels.Add(label);
        }

        // Find which filtered index corresponds to _selectedDef
        int selectedFiltered = -1;
        for (int i = 0; i < filtered.Count; i++)
        {
            if (filtered[i] == _selectedDef) { selectedFiltered = i; break; }
        }

        int clicked = _ui.DrawScrollableList("envdef_list", labels, selectedFiltered,
            x + 2, curY, w - 4, listH);

        if (clicked >= 0 && clicked < filtered.Count && filtered[clicked] != _selectedDef)
        {
            // RM21: Close harmonizer when switching defs so it doesn't carry stale state
            if (_harmonizerOpen)
            {
                _harmonizerOpen = false;
                _harmonizer.Cancel();
            }
            _selectedDef = filtered[clicked];
            _propScrollY = 0;
            ReloadPreview();
        }
    }

    // ========================================================================
    //  CENTER PANEL - Preview
    // ========================================================================

    private void DrawPreviewPanel(int x, int y, int w, int h)
    {
        _ui.DrawRect(new Rectangle(x, y, w, h), PreviewBg);
        _ui.DrawRect(new Rectangle(x + w, y, 1, h), BorderColor);

        // Reserve bottom strip for reference images
        int mainH = h - RefPanelH;

        // Store panel area for interaction
        _pvAreaX = x;
        _pvAreaY = y;
        _pvAreaW = w;
        _pvAreaH = mainH;

        if (_selectedDef < 0 || _selectedDef >= _env.DefCount)
        {
            _ui.DrawText("No def selected", new Vector2(x + w / 2 - 50, y + mainH / 2), EditorBase.TextDim);
            DrawReferencePanel(x, y + mainH, w, RefPanelH, 0);
            return;
        }

        var def = _env.GetDef(_selectedDef);
        var tex = _env.GetDefTexture(_selectedDef);
        if (tex == null)
        {
            _ui.DrawText("No texture", new Vector2(x + w / 2 - 40, y + mainH / 2), EditorBase.TextDim);
            DrawReferencePanel(x, y + mainH, w, RefPanelH, 0);
        }
        else
        {
            // Fit texture in preview area with padding
            int padded = Math.Min(w, mainH) - Padding * 4;
            float scaleX = (float)padded / tex.Width;
            float scaleY = (float)padded / tex.Height;
            float scale = MathF.Min(scaleX, scaleY);
            if (scale > 4f) scale = 4f; // cap max zoom

            int drawW = (int)(tex.Width * scale);
            int drawH = (int)(tex.Height * scale);
            int drawX = x + (w - drawW) / 2;
            int drawY = y + (mainH - drawH) / 2;

            // Cache layout for interaction handlers
            _pvDrawX = drawX;
            _pvDrawY = drawY;
            _pvDrawW = drawW;
            _pvDrawH = drawH;
            _pvScale = scale;

            _sb.Draw(tex, new Rectangle(drawX, drawY, drawW, drawH), Color.White);

            // Scale factor: pixels per world-unit
            float pixPerWorld = drawH / MathF.Max(def.SpriteWorldHeight, 0.01f);

            // Pivot position in screen pixels
            float pivotPxX = drawX + def.PivotX * drawW;
            float pivotPxY = drawY + def.PivotY * drawH;

            // --- Draw pivot crosshair ---
            DrawPivotCrosshair(pivotPxX, pivotPxY);

            // --- Draw collision circle overlay ---
            if (def.CollisionRadius > 0)
            {
                float cx = pivotPxX + def.CollisionOffsetX * pixPerWorld;
                float cy = pivotPxY + def.CollisionOffsetY * pixPerWorld;
                float radiusPx = def.CollisionRadius * pixPerWorld;

                // Update hover state
                UpdateCollisionHover(cx, cy, radiusPx);

                // Choose colors based on hover / drag state
                Color circleCol = _hoveringCollisionEdge || _draggingCollisionRadius
                    ? CollisionHoverEdgeColor : CollisionColor;
                Color centerCol = _hoveringCollisionCenter || _draggingCollisionCenter
                    ? CollisionHoverCenterColor : CollisionColor;

                // Draw circle outline (thicker when hovering edge)
                int circleSegments = 48;
                DrawCircleOutline(new Vector2(cx, cy), radiusPx, circleCol, circleSegments);
                if (_hoveringCollisionEdge || _draggingCollisionRadius)
                {
                    DrawCircleOutline(new Vector2(cx, cy), radiusPx - 1, circleCol * 0.5f, circleSegments);
                    DrawCircleOutline(new Vector2(cx, cy), radiusPx + 1, circleCol * 0.5f, circleSegments);
                }

                // Center dot (larger when hovered)
                int dotSize = (_hoveringCollisionCenter || _draggingCollisionCenter) ? 4 : 2;
                _ui.DrawRect(new Rectangle((int)cx - dotSize, (int)cy - dotSize, dotSize * 2 + 1, dotSize * 2 + 1), centerCol);

                // Interactive dragging
                HandleCollisionDrag(def, cx, cy, radiusPx, pixPerWorld);
            }
            else
            {
                _hoveringCollisionCenter = false;
                _hoveringCollisionEdge = false;
            }

            // Handle pivot click-to-set
            HandlePivotDrag(def, drawX, drawY, drawW, drawH);

            // Draw reference images at same scale
            DrawReferencePanel(x, y + mainH, w, RefPanelH, pixPerWorld);
        }

        // Label at bottom of main area
        _ui.DrawText($"Preview: {def.Name}", new Vector2(x + Padding, y + mainH - 22), EditorBase.TextDim);
    }

    /// <summary>Draw a crosshair marker at the pivot position.</summary>
    private void DrawPivotCrosshair(float px, float py)
    {
        int armLen = 10;
        // Horizontal arm
        DrawLine(new Vector2(px - armLen, py), new Vector2(px + armLen, py), PivotColor);
        // Vertical arm
        DrawLine(new Vector2(px, py - armLen), new Vector2(px, py + armLen), PivotColor);
        // Small filled center
        _ui.DrawRect(new Rectangle((int)px - 1, (int)py - 1, 3, 3), PivotColor);
    }

    /// <summary>Update hover flags for collision center / edge.</summary>
    private void UpdateCollisionHover(float cx, float cy, float radiusPx)
    {
        if (_draggingCollisionCenter || _draggingCollisionRadius) return; // keep state while dragging

        var mouse = Mouse.GetState();
        float mx = mouse.X, my = mouse.Y;
        float dx = mx - cx, dy = my - cy;
        float dist = MathF.Sqrt(dx * dx + dy * dy);

        _hoveringCollisionCenter = dist < 12f;
        _hoveringCollisionEdge = !_hoveringCollisionCenter && MathF.Abs(dist - radiusPx) < 8f;
    }

    /// <summary>Handle click-to-set pivot by clicking in the preview area.</summary>
    private void HandlePivotDrag(EnvironmentObjectDef def, int drawX, int drawY, int drawW, int drawH)
    {
        var mouse = Mouse.GetState();
        var prevMouse = _ui._prevMouse;

        // Right-click to set pivot position (left-click is for collision)
        bool rightClick = mouse.RightButton == ButtonState.Pressed && prevMouse.RightButton == ButtonState.Released;
        bool rightDown = mouse.RightButton == ButtonState.Pressed;
        bool rightUp = mouse.RightButton == ButtonState.Released && prevMouse.RightButton == ButtonState.Pressed;

        if (rightClick)
        {
            // Check if click is within the sprite area
            if (mouse.X >= drawX && mouse.X <= drawX + drawW &&
                mouse.Y >= drawY && mouse.Y <= drawY + drawH)
            {
                _draggingPivot = true;
            }
        }

        if (rightUp)
            _draggingPivot = false;

        if (_draggingPivot && rightDown && drawW > 0 && drawH > 0)
        {
            // Convert screen position to normalized pivot (0..1)
            float newPivX = (float)(mouse.X - drawX) / drawW;
            float newPivY = (float)(mouse.Y - drawY) / drawH;
            def.PivotX = MathHelper.Clamp(newPivX, 0f, 1f);
            def.PivotY = MathHelper.Clamp(newPivY, 0f, 1f);
        }
    }

    // ========================================================================
    //  REFERENCE IMAGE PANEL (below main preview)
    // ========================================================================

    private void DrawReferencePanel(int x, int y, int w, int h, float pixPerWorld)
    {
        // Separator line
        _ui.DrawRect(new Rectangle(x, y, w, 1), BorderColor);
        y += 1;
        h -= 1;

        _ui.DrawRect(new Rectangle(x, y, w, h), new Color(15, 15, 28, 250));
        _ui.DrawText("2x2 Grid: Edited + 3 References (click to set, X to clear)", new Vector2(x + 4, y + 2), EditorBase.TextDim, _smallFont);

        // M06: 2x2 grid layout
        // Top-left = edited object, Top-right = ref 1, Bottom-left = ref 2, Bottom-right = ref 3
        int gridY = y + 16;
        int gridH = h - 18;
        int cellW = (w - Padding * 2 - 2) / 2;
        int cellH = (gridH - 2) / 2;

        // Top-left: edited object preview
        DrawEditedObjectSlot(x + Padding, gridY, cellW, cellH, pixPerWorld);

        // Top-right: ref 1
        DrawReferenceSlot(0, x + Padding + cellW + 2, gridY, cellW, cellH, pixPerWorld);

        // Bottom-left: ref 2
        DrawReferenceSlot(1, x + Padding, gridY + cellH + 2, cellW, cellH, pixPerWorld);

        // Bottom-right: ref 3
        DrawReferenceSlot(2, x + Padding + cellW + 2, gridY + cellH + 2, cellW, cellH, pixPerWorld);

        // Reference slot DrawCombo pickers are handled inline within DrawReferenceSlot
    }

    /// <summary>M06: Draw the currently edited object in a grid slot at the same scale as references.</summary>
    private void DrawEditedObjectSlot(int x, int y, int w, int h, float pixPerWorld)
    {
        _ui.DrawRect(new Rectangle(x, y, w, h), RefSlotBg);
        _ui.DrawBorder(new Rectangle(x, y, w, h), new Color(100, 100, 180), 1);

        if (_selectedDef < 0 || _selectedDef >= _env.DefCount) return;

        var def = _env.GetDef(_selectedDef);
        var tex = _env.GetDefTexture(_selectedDef);
        if (tex == null || pixPerWorld < 0.001f)
        {
            _ui.DrawText("(no tex)", new Vector2(x + 4, y + h / 2 - 6), EditorBase.TextDim, _smallFont);
            return;
        }

        // Draw at same world-scale as main preview
        float drawH = def.SpriteWorldHeight * pixPerWorld;
        float refScale = drawH / tex.Height;
        float drawW = tex.Width * refScale;

        // Clamp to fit in slot
        if (drawW > w - 4 || drawH > h - 4)
        {
            float fitScale = MathF.Min((w - 4) / drawW, (h - 4) / drawH);
            drawW *= fitScale;
            drawH *= fitScale;
        }

        int rx = x + (w - (int)drawW) / 2;
        int ry = y + (h - (int)drawH) / 2;
        _sb.Draw(tex, new Rectangle(rx, ry, (int)drawW, (int)drawH), Color.White);

        string label = string.IsNullOrEmpty(def.Name) ? def.Id : def.Name;
        if (label.Length > 12) label = label[..12] + "..";
        _ui.DrawText("[Edit] " + label, new Vector2(x + 2, y + h - 14), new Color(140, 180, 255), _smallFont);
    }

    private void DrawReferenceSlot(int slotIdx, int x, int y, int w, int h, float pixPerWorld)
    {
        _ui.DrawRect(new Rectangle(x, y, w, h), RefSlotBg);
        _ui.DrawBorder(new Rectangle(x, y, w, h), BorderColor, 1);

        int refDefIdx = _referenceDefIndices[slotIdx];

        if (refDefIdx >= 0 && refDefIdx < _env.DefCount)
        {
            var refDef = _env.GetDef(refDefIdx);
            var refTex = _env.GetDefTexture(refDefIdx);

            if (refTex != null && pixPerWorld > 0.001f)
            {
                // Draw at the same world-scale as the main preview
                float refDrawH = refDef.SpriteWorldHeight * pixPerWorld;
                float refScale = refDrawH / refTex.Height;
                float refDrawW = refTex.Width * refScale;

                // Clamp to fit in slot
                if (refDrawW > w - 4 || refDrawH > h - 4)
                {
                    float fitScale = MathF.Min((w - 4) / refDrawW, (h - 4) / refDrawH);
                    refDrawW *= fitScale;
                    refDrawH *= fitScale;
                }

                int rx = x + (w - (int)refDrawW) / 2;
                int ry = y + (h - (int)refDrawH) / 2;
                _sb.Draw(refTex, new Rectangle(rx, ry, (int)refDrawW, (int)refDrawH), Color.White);
            }

            // Name label
            string label = string.IsNullOrEmpty(refDef.Name) ? refDef.Id : refDef.Name;
            if (label.Length > 12) label = label[..12] + "..";
            _ui.DrawText(label, new Vector2(x + 2, y + h - 14), EditorBase.TextDim, _smallFont);

            // Clear button (X) in top-right corner
            if (_ui.DrawButton("X", x + w - 18, y + 2, 16, 14, EditorBase.DangerColor))
            {
                _referenceDefIndices[slotIdx] = -1;
            }
        }
        else
        {
            // Empty slot - show a DrawCombo picker for selecting a reference object
            string[] names = GetEnvObjectNames();
            if (names.Length > 0)
            {
                string picked = _ui.DrawCombo($"refpick_{slotIdx}", "Ref", "(Pick...)", names, x + 2, y + h / 2 - 10, w - 4);
                if (picked != "(Pick...)")
                {
                    // Find the def index matching the picked name
                    for (int di = 0; di < _env.DefCount; di++)
                    {
                        var d = _env.GetDef(di);
                        string lbl = string.IsNullOrEmpty(d.Name) ? d.Id : d.Name;
                        if (lbl == picked)
                        {
                            _referenceDefIndices[slotIdx] = di;
                            _env.ReloadDefTexture(di);
                            break;
                        }
                    }
                }
            }
            else
            {
                _ui.DrawText("(no defs)", new Vector2(x + 4, y + h / 2 - 6), EditorBase.TextDim, _smallFont);
            }
        }
    }

    /// <summary>Get cached array of env object display names for reference picker DrawCombo.</summary>
    private string[] GetEnvObjectNames()
    {
        if (_cachedEnvObjectNames == null || _cachedEnvObjectCount != _env.DefCount)
        {
            _cachedEnvObjectCount = _env.DefCount;
            _cachedEnvObjectNames = new string[_env.DefCount];
            for (int i = 0; i < _env.DefCount; i++)
            {
                var d = _env.GetDef(i);
                _cachedEnvObjectNames[i] = string.IsNullOrEmpty(d.Name) ? d.Id : d.Name;
            }
        }
        return _cachedEnvObjectNames;
    }

    private void HandleCollisionDrag(EnvironmentObjectDef def, float cx, float cy, float radiusPx, float pixPerWorld)
    {
        var mouse = Mouse.GetState();
        var prevMouse = _ui._prevMouse;
        float mx = mouse.X, my = mouse.Y;
        float dx = mx - cx, dy = my - cy;
        float dist = MathF.Sqrt(dx * dx + dy * dy);
        bool leftClick = mouse.LeftButton == ButtonState.Pressed && prevMouse.LeftButton == ButtonState.Released;
        bool leftDown = mouse.LeftButton == ButtonState.Pressed;
        bool leftUp = mouse.LeftButton == ButtonState.Released && prevMouse.LeftButton == ButtonState.Pressed;

        if (leftClick)
        {
            // Click near center -> drag center offset
            if (dist < 12f)
                _draggingCollisionCenter = true;
            // Click near ring -> drag radius
            else if (MathF.Abs(dist - radiusPx) < 8f)
                _draggingCollisionRadius = true;
        }

        if (leftUp)
        {
            _draggingCollisionCenter = false;
            _draggingCollisionRadius = false;
        }

        if (_draggingCollisionCenter && leftDown && pixPerWorld > 0.001f)
        {
            float deltaX = (mouse.X - prevMouse.X) / pixPerWorld;
            float deltaY = (mouse.Y - prevMouse.Y) / pixPerWorld;
            def.CollisionOffsetX += deltaX;
            def.CollisionOffsetY += deltaY;
        }

        if (_draggingCollisionRadius && leftDown && pixPerWorld > 0.001f)
        {
            float newDist = MathF.Sqrt(dx * dx + dy * dy);
            def.CollisionRadius = MathF.Max(0f, newDist / pixPerWorld);
        }

        // Draw hover hint text
        if (_hoveringCollisionCenter && !_draggingCollisionCenter && !_draggingCollisionRadius)
            _ui.DrawText("Drag: move collision center", new Vector2(cx + 10, cy - 18), CollisionHoverCenterColor, _smallFont);
        else if (_hoveringCollisionEdge && !_draggingCollisionCenter && !_draggingCollisionRadius)
            _ui.DrawText("Drag: resize radius", new Vector2(cx + radiusPx + 6, cy - 8), CollisionHoverEdgeColor, _smallFont);
    }

    // ========================================================================
    //  RIGHT PANEL - Properties
    // ========================================================================

    private void DrawPropertiesPanel(int x, int y, int w, int h)
    {
        _ui.DrawRect(new Rectangle(x, y, w, h), new Color(22, 22, 38, 240));

        if (_selectedDef < 0 || _selectedDef >= _env.DefCount)
        {
            _ui.DrawText("Select a definition to edit", new Vector2(x + Padding, y + h / 2), EditorBase.TextDim);
            return;
        }

        var def = _env.GetDef(_selectedDef);
        int fieldW = w - Padding * 2;
        int fx = x + Padding;

        // Scroll handling
        var mouse = Mouse.GetState();
        var propRect = new Rectangle(x, y, w, h);
        if (propRect.Contains(mouse.X, mouse.Y))
        {
            int scrollDelta = mouse.ScrollWheelValue - _ui._prevMouse.ScrollWheelValue;
            if (scrollDelta != 0)
            {
                _propScrollY -= scrollDelta * 0.3f;
                if (_propScrollY < 0) _propScrollY = 0;
            }
        }

        // Begin scissor clip for properties
        _ui.BeginClip(propRect);

        int curY = y + Padding - (int)_propScrollY;

        // --- Section: Identity ---
        curY = DrawSectionLabel(fx, curY, fieldW, "IDENTITY");

        // Name
        string newName = _ui.DrawTextField("envdef_name", "Name", def.Name, fx, curY, fieldW);
        if (newName != def.Name) def.Name = newName;
        curY += RowH;

        // ID (display as read-only if already set)
        string newId = _ui.DrawTextField("envdef_id", "ID", def.Id, fx, curY, fieldW);
        if (newId != def.Id) def.Id = newId;
        curY += RowH;

        // Category
        var categories = GetExistingCategories();
        categories.Add("+ New Category");
        string[] catArray = categories.ToArray();
        string currentCat = def.Category;
        string newCatVal = _ui.DrawCombo("envdef_category", "Category", currentCat, catArray, fx, curY, fieldW);
        if (newCatVal == "+ New Category")
        {
            _newCategoryDialogOpen = true;
            _newCategoryName = "";
        }
        else if (newCatVal != currentCat)
        {
            def.Category = newCatVal;
        }
        curY += RowH;

        // Group (dropdown showing existing groups + "None" + manual entry)
        var groupOptions = GetExistingGroups();
        groupOptions.Insert(0, "(None)");
        string currentGroup = string.IsNullOrEmpty(def.Group) ? "(None)" : def.Group;
        string newGroupVal = _ui.DrawCombo("envdef_group", "Group", currentGroup, groupOptions.ToArray(), fx, curY, fieldW);
        if (newGroupVal == "(None)")
        {
            if (!string.IsNullOrEmpty(def.Group)) def.Group = "";
        }
        else if (newGroupVal != currentGroup)
        {
            def.Group = newGroupVal;
        }
        curY += RowH;

        // Manual group name entry (for creating new groups)
        string manualGroup = _ui.DrawTextField("envdef_group_manual", "New Group", def.Group, fx, curY, fieldW);
        if (manualGroup != def.Group) def.Group = manualGroup;
        curY += RowH;

        curY += 4;

        // --- Section: Texture ---
        curY = DrawSectionLabel(fx, curY, fieldW, "TEXTURE & SPRITE");

        int envBrowseBtnW = 55;
        string newTexPath = _ui.DrawTextField("envdef_texpath", "Texture Path", def.TexturePath, fx, curY, fieldW - envBrowseBtnW - 4);
        if (newTexPath != def.TexturePath)
        {
            def.TexturePath = newTexPath;
            _env.ReloadDefTexture(_selectedDef);
            ReloadPreview();
        }
        if (_ui.DrawButton("Browse", fx + fieldW - envBrowseBtnW, curY, envBrowseBtnW, 20))
        {
            _textureBrowser.Open("assets/Environment", def.TexturePath, path =>
            {
                def.TexturePath = path;
                _env.ReloadDefTexture(_selectedDef);
                ReloadPreview();
            });
        }
        curY += RowH;

        float newSWH = _ui.DrawFloatField("envdef_swh", "Sprite Height", def.SpriteWorldHeight, fx, curY, fieldW, 0.5f);
        if (MathF.Abs(newSWH - def.SpriteWorldHeight) > 0.001f) def.SpriteWorldHeight = newSWH;
        curY += RowH;

        float newScale = _ui.DrawFloatField("envdef_scale", "Scale", def.Scale, fx, curY, fieldW, 0.1f);
        if (MathF.Abs(newScale - def.Scale) > 0.001f) def.Scale = newScale;
        curY += RowH;

        float newPlaceScale = _ui.DrawFloatField("envdef_placescale", "Placement Scale", def.PlacementScale, fx, curY, fieldW, 0.1f);
        if (MathF.Abs(newPlaceScale - def.PlacementScale) > 0.001f) def.PlacementScale = newPlaceScale;
        curY += RowH;

        // M04: Tint color + Harmonize button
        _ui.DrawText("Tint:", new Vector2(fx, curY + 4), EditorBase.TextDim);
        var tint = def.TintColor;
        if (_ui.DrawColorSwatch("envdef_tint", fx + 90, curY + 2, 28, RowH - 4, ref tint))
            def.TintColor = tint;
        int harmBtnX = fx + 130;
        int harmBtnW = 80;
        if (_ui.DrawButton("Harmonize", harmBtnX, curY + 1, harmBtnW, RowH - 2, EditorBase.AccentColor))
        {
            if (!_harmonizerOpen)
            {
                _harmonizerOpen = true;
                var colors = new HdrColor[] { def.TintColor };
                _harmonizer.Begin(colors);
            }
            else
            {
                _harmonizerOpen = false;
                _harmonizer.Cancel();
            }
        }
        curY += RowH;

        // M04: Harmonizer panel (inline, shown when open)
        if (_harmonizerOpen && _harmonizer.Active)
        {
            if (_harmonizer.DrawPanel(_ui, fx, ref curY, fieldW))
            {
                // Apply harmonized result back to the tint
                if (_harmonizer.NumColors > 0)
                    def.TintColor = _harmonizer.Result[0];
            }
        }

        // M05: Edge Tweaker button
        if (!string.IsNullOrEmpty(def.TexturePath))
        {
            if (_ui.DrawButton("Edge Tweaker", fx, curY + 1, 110, RowH - 2))
            {
                _edgeTweakerOpen = !_edgeTweakerOpen;
                if (_edgeTweakerOpen)
                    LoadEdgeTweakerTexture(def);
            }
            curY += RowH;
        }

        float newPivotX = _ui.DrawFloatField("envdef_pivotx", "Pivot X", def.PivotX, fx, curY, fieldW, 0.05f);
        if (MathF.Abs(newPivotX - def.PivotX) > 0.001f) def.PivotX = newPivotX;
        curY += RowH;

        float newPivotY = _ui.DrawFloatField("envdef_pivoty", "Pivot Y", def.PivotY, fx, curY, fieldW, 0.05f);
        if (MathF.Abs(newPivotY - def.PivotY) > 0.001f) def.PivotY = newPivotY;
        curY += RowH;

        float newWorldH = _ui.DrawFloatField("envdef_worldh", "World Height", def.WorldHeight, fx, curY, fieldW, 0.5f);
        if (MathF.Abs(newWorldH - def.WorldHeight) > 0.001f) def.WorldHeight = newWorldH;
        curY += RowH;

        float newGroupW = _ui.DrawFloatField("envdef_groupw", "Group Weight", def.GroupWeight, fx, curY, fieldW, 0.1f);
        if (MathF.Abs(newGroupW - def.GroupWeight) > 0.001f) def.GroupWeight = newGroupW;
        curY += RowH;

        // M08: Inline group editor - show all objects in same group
        if (!string.IsNullOrEmpty(def.Group))
        {
            curY += 2;
            _ui.DrawRect(new Rectangle(fx - 2, curY, fieldW + 4, 1), BorderColor);
            curY += 2;
            _ui.DrawText($"Group Members: \"{def.Group}\"", new Vector2(fx + 4, curY), EditorBase.TextBright);
            curY += 18;

            for (int gi = 0; gi < _env.DefCount; gi++)
            {
                var gDef = _env.GetDef(gi);
                if (gDef.Group != def.Group) continue;

                bool isSelf = gi == _selectedDef;
                string gLabel = string.IsNullOrEmpty(gDef.Name) ? gDef.Id : gDef.Name;
                if (isSelf) gLabel += " (this)";

                // Name label
                _ui.DrawText(gLabel, new Vector2(fx + 4, curY + 3), isSelf ? EditorBase.TextBright : EditorBase.TextDim);

                // Editable weight field
                int weightFieldX = fx + fieldW - 120;
                float gw = _ui.DrawFloatField($"grp_w_{gi}", "W:", gDef.GroupWeight, weightFieldX, curY, 120, 0.1f);
                if (MathF.Abs(gw - gDef.GroupWeight) > 0.001f) gDef.GroupWeight = gw;

                // Remove from group button (not for self - use group dropdown instead)
                if (!isSelf)
                {
                    if (_ui.DrawButton("X", fx + fieldW - 140, curY, 18, 18, EditorBase.DangerColor))
                    {
                        gDef.Group = "";
                    }
                }

                curY += RowH;
            }
            curY += 2;
            _ui.DrawRect(new Rectangle(fx - 2, curY, fieldW + 4, 1), BorderColor);
            curY += 4;
        }
        else
        {
            curY += 4;
        }

        // --- Section: Collision ---
        curY = DrawSectionLabel(fx, curY, fieldW, "COLLISION");

        float newCR = _ui.DrawFloatField("envdef_colrad", "Collision Radius", def.CollisionRadius, fx, curY, fieldW, 0.1f);
        if (MathF.Abs(newCR - def.CollisionRadius) > 0.001f) def.CollisionRadius = newCR;
        curY += RowH;

        float newCOX = _ui.DrawFloatField("envdef_colox", "Col Offset X", def.CollisionOffsetX, fx, curY, fieldW, 0.1f);
        if (MathF.Abs(newCOX - def.CollisionOffsetX) > 0.001f) def.CollisionOffsetX = newCOX;
        curY += RowH;

        float newCOY = _ui.DrawFloatField("envdef_coloy", "Col Offset Y", def.CollisionOffsetY, fx, curY, fieldW, 0.1f);
        if (MathF.Abs(newCOY - def.CollisionOffsetY) > 0.001f) def.CollisionOffsetY = newCOY;
        curY += RowH;

        curY += 4;

        // --- Section: Building ---
        curY = DrawSectionLabel(fx, curY, fieldW, "BUILDING");

        bool newIsBuilding = _ui.DrawCheckbox("Is Building", def.IsBuilding, fx, curY);
        if (newIsBuilding != def.IsBuilding) def.IsBuilding = newIsBuilding;
        curY += RowH;

        if (def.IsBuilding)
        {
            bool newPlayerBuildable = _ui.DrawCheckbox("Player Buildable", def.PlayerBuildable, fx, curY);
            if (newPlayerBuildable != def.PlayerBuildable) def.PlayerBuildable = newPlayerBuildable;
            curY += RowH;

            int newMaxHP = _ui.DrawIntField("envdef_buildhp", "Max HP", def.BuildingMaxHP, fx, curY, fieldW);
            if (newMaxHP != def.BuildingMaxHP) def.BuildingMaxHP = newMaxHP;
            curY += RowH;

            int newProt = _ui.DrawIntField("envdef_buildprot", "Protection", def.BuildingProtection, fx, curY, fieldW);
            if (newProt != def.BuildingProtection) def.BuildingProtection = newProt;
            curY += RowH;

            int newOwner = _ui.DrawIntField("envdef_buildown", "Default Owner", def.BuildingDefaultOwner, fx, curY, fieldW);
            if (newOwner != def.BuildingDefaultOwner) def.BuildingDefaultOwner = newOwner;
            curY += RowH;

            // RM22: Building cost fields
            int newCostWood = _ui.DrawIntField("envdef_costwood", "Cost Wood", def.CostWood, fx, curY, fieldW);
            if (newCostWood != def.CostWood) def.CostWood = Math.Max(0, newCostWood);
            curY += RowH;

            int newCostStone = _ui.DrawIntField("envdef_coststone", "Cost Stone", def.CostStone, fx, curY, fieldW);
            if (newCostStone != def.CostStone) def.CostStone = Math.Max(0, newCostStone);
            curY += RowH;

            int newCostGold = _ui.DrawIntField("envdef_costgold", "Cost Gold", def.CostGold, fx, curY, fieldW);
            if (newCostGold != def.CostGold) def.CostGold = Math.Max(0, newCostGold);
            curY += RowH;

            bool newAutoSpawn = _ui.DrawCheckbox("Auto Spawn", def.AutoSpawn, fx, curY);
            if (newAutoSpawn != def.AutoSpawn) def.AutoSpawn = newAutoSpawn;
            curY += RowH;

            if (def.AutoSpawn)
            {
                float newSOX = _ui.DrawFloatField("envdef_spawnox", "Spawn Offset X", def.SpawnOffsetX, fx, curY, fieldW, 0.5f);
                if (MathF.Abs(newSOX - def.SpawnOffsetX) > 0.001f) def.SpawnOffsetX = newSOX;
                curY += RowH;

                float newSOY = _ui.DrawFloatField("envdef_spawnoy", "Spawn Offset Y", def.SpawnOffsetY, fx, curY, fieldW, 0.5f);
                if (MathF.Abs(newSOY - def.SpawnOffsetY) > 0.001f) def.SpawnOffsetY = newSOY;
                curY += RowH;
            }
        }

        curY += 4;

        // --- Section: Trigger ---
        curY = DrawSectionLabel(fx, curY, fieldW, "TRIGGER / PROCESSING");

        // TODO RM19: Change trigger instance display from flat separate list to contextual
        // children under parent trigger (show instances beneath their parent trigger def).
        // This would require restructuring the Triggers tab in MapEditorWindow to display
        // instances grouped under their parent trigger definitions instead of a flat list.

        // RM28: Bound trigger dropdown populated from trigger system
        if (_triggerSystem != null)
        {
            var triggerIds = new string[_triggerSystem.Triggers.Count + 1];
            triggerIds[0] = "(none)";
            for (int ti = 0; ti < _triggerSystem.Triggers.Count; ti++)
                triggerIds[ti + 1] = _triggerSystem.Triggers[ti].Id;
            string curTrigger = string.IsNullOrEmpty(def.BoundTriggerID) ? "(none)" : def.BoundTriggerID;
            string newBoundTrigger = _ui.DrawCombo("envdef_trigger", "Bound Trigger", curTrigger, triggerIds, fx, curY, fieldW);
            if (newBoundTrigger != curTrigger)
                def.BoundTriggerID = newBoundTrigger == "(none)" ? "" : newBoundTrigger;
        }
        else
        {
            string newBoundTrigger = _ui.DrawTextField("envdef_trigger", "Bound Trigger", def.BoundTriggerID, fx, curY, fieldW);
            if (newBoundTrigger != def.BoundTriggerID) def.BoundTriggerID = newBoundTrigger;
        }
        curY += RowH;

        // RM27: Processing slot Kind dropdowns (None/Corpse/Unit/Material) and ResourceID text fields
        string[] slotKindOptions = { "", "Corpse", "Unit", "Material" };
        string[] slotKindLabels = { "None", "Corpse", "Unit", "Material" };

        // Input1 Kind dropdown
        string in1KindDisplay = string.IsNullOrEmpty(def.Input1.Kind) ? "None" : def.Input1.Kind;
        string newIn1Kind = _ui.DrawCombo("envdef_in1kind", "Input1 Kind", in1KindDisplay, slotKindLabels, fx, curY, fieldW);
        if (newIn1Kind != in1KindDisplay)
        {
            int kindIdx = Array.IndexOf(slotKindLabels, newIn1Kind);
            def.Input1.Kind = kindIdx >= 0 ? slotKindOptions[kindIdx] : newIn1Kind;
        }
        curY += RowH;

        string newIn1Res = _ui.DrawTextField("envdef_in1res", "Input1 ResID", def.Input1.ResourceID, fx, curY, fieldW);
        if (newIn1Res != def.Input1.ResourceID) def.Input1.ResourceID = newIn1Res;
        curY += RowH;

        // Input2 Kind dropdown
        string in2KindDisplay = string.IsNullOrEmpty(def.Input2.Kind) ? "None" : def.Input2.Kind;
        string newIn2Kind = _ui.DrawCombo("envdef_in2kind", "Input2 Kind", in2KindDisplay, slotKindLabels, fx, curY, fieldW);
        if (newIn2Kind != in2KindDisplay)
        {
            int kindIdx = Array.IndexOf(slotKindLabels, newIn2Kind);
            def.Input2.Kind = kindIdx >= 0 ? slotKindOptions[kindIdx] : newIn2Kind;
        }
        curY += RowH;

        string newIn2Res = _ui.DrawTextField("envdef_in2res", "Input2 ResID", def.Input2.ResourceID, fx, curY, fieldW);
        if (newIn2Res != def.Input2.ResourceID) def.Input2.ResourceID = newIn2Res;
        curY += RowH;

        // Output Kind dropdown
        string outKindDisplay = string.IsNullOrEmpty(def.Output.Kind) ? "None" : def.Output.Kind;
        string newOutKind = _ui.DrawCombo("envdef_outkind", "Output Kind", outKindDisplay, slotKindLabels, fx, curY, fieldW);
        if (newOutKind != outKindDisplay)
        {
            int kindIdx = Array.IndexOf(slotKindLabels, newOutKind);
            def.Output.Kind = kindIdx >= 0 ? slotKindOptions[kindIdx] : newOutKind;
        }
        curY += RowH;

        string newOutRes = _ui.DrawTextField("envdef_outres", "Output ResID", def.Output.ResourceID, fx, curY, fieldW);
        if (newOutRes != def.Output.ResourceID) def.Output.ResourceID = newOutRes;
        curY += RowH;

        float newProcTime = _ui.DrawFloatField("envdef_proctime", "Process Time", def.ProcessTime, fx, curY, fieldW, 1f);
        if (MathF.Abs(newProcTime - def.ProcessTime) > 0.001f) def.ProcessTime = newProcTime;
        curY += RowH;

        int newMaxIn = _ui.DrawIntField("envdef_maxinq", "Max Input Queue", def.MaxInputQueue, fx, curY, fieldW);
        if (newMaxIn != def.MaxInputQueue) def.MaxInputQueue = newMaxIn;
        curY += RowH;

        int newMaxOut = _ui.DrawIntField("envdef_maxoutq", "Max Output Queue", def.MaxOutputQueue, fx, curY, fieldW);
        if (newMaxOut != def.MaxOutputQueue) def.MaxOutputQueue = newMaxOut;
        curY += RowH;

        // End scissor clip
        _ui.EndClip();

        // Clamp max scroll
        int totalContentH = curY + (int)_propScrollY - y;
        float maxScroll = MathF.Max(0, totalContentH - h);
        _propScrollY = MathF.Min(_propScrollY, maxScroll);
    }

    private int DrawSectionLabel(int x, int curY, int w, string label)
    {
        _ui.DrawRect(new Rectangle(x - 2, curY, w + 4, RowH), HeaderBg);
        _ui.DrawText(label, new Vector2(x + 4, curY + 4), EditorBase.TextBright);
        return curY + RowH + 2;
    }

    // ========================================================================
    //  Keyboard handling
    // ========================================================================

    private void HandleKeyboard()
    {
        var kb = _ui._kb;
        var prevKb = _ui._prevKb;

        // Don't handle hotkeys if text input is active
        if (_ui.IsTextInputActive) return;

        // Escape to close (unless a dialog is open)
        if (kb.IsKeyDown(Keys.Escape) && prevKb.IsKeyUp(Keys.Escape))
        {
            if (_edgeTweakerOpen)
                _edgeTweakerOpen = false;
            else if (_confirmDeleteOpen)
                _confirmDeleteOpen = false;
            else if (_newCategoryDialogOpen)
                _newCategoryDialogOpen = false;
            else
                Close();
        }

        // Ctrl+S to save
        if (kb.IsKeyDown(Keys.LeftControl) && kb.IsKeyDown(Keys.S) && prevKb.IsKeyUp(Keys.S))
            SaveDefs();
    }

    // ========================================================================
    //  Def Management
    // ========================================================================

    private void CreateNewDef()
    {
        int idx = _env.DefCount;
        string id = $"env_obj_{idx}";
        // Ensure unique ID
        while (_env.FindDef(id) >= 0)
        {
            idx++;
            id = $"env_obj_{idx}";
        }

        var def = new EnvironmentObjectDef
        {
            Id = id,
            Name = $"New Object {idx}",
            Category = GetCurrentFilterCategory(),
        };

        int newIdx = _env.AddDef(def);
        _selectedDef = newIdx;
        _propScrollY = 0;
        ReloadPreview();
    }

    private void CopySelectedDef()
    {
        if (_selectedDef < 0 || _selectedDef >= _env.DefCount) return;

        var src = _env.GetDef(_selectedDef);
        int idx = _env.DefCount;
        string id = src.Id + "_copy";
        while (_env.FindDef(id) >= 0)
        {
            idx++;
            id = src.Id + $"_copy{idx}";
        }

        var copy = new EnvironmentObjectDef
        {
            Id = id,
            Name = src.Name + " (Copy)",
            Category = src.Category,
            TexturePath = src.TexturePath,
            HeightMapPath = src.HeightMapPath,
            SpriteWorldHeight = src.SpriteWorldHeight,
            WorldHeight = src.WorldHeight,
            PivotX = src.PivotX,
            PivotY = src.PivotY,
            CollisionRadius = src.CollisionRadius,
            CollisionOffsetX = src.CollisionOffsetX,
            CollisionOffsetY = src.CollisionOffsetY,
            Scale = src.Scale,
            PlacementScale = src.PlacementScale,
            Group = src.Group,
            GroupWeight = src.GroupWeight,
            IsBuilding = src.IsBuilding,
            PlayerBuildable = src.PlayerBuildable,
            BuildingMaxHP = src.BuildingMaxHP,
            BuildingProtection = src.BuildingProtection,
            BuildingDefaultOwner = src.BuildingDefaultOwner,
            CostWood = src.CostWood,
            CostStone = src.CostStone,
            CostGold = src.CostGold,
            BoundTriggerID = src.BoundTriggerID,
            Input1 = new ProcessSlot { Kind = src.Input1.Kind, ResourceID = src.Input1.ResourceID },
            Input2 = new ProcessSlot { Kind = src.Input2.Kind, ResourceID = src.Input2.ResourceID },
            Output = new ProcessSlot { Kind = src.Output.Kind, ResourceID = src.Output.ResourceID },
            ProcessTime = src.ProcessTime,
            MaxInputQueue = src.MaxInputQueue,
            MaxOutputQueue = src.MaxOutputQueue,
            AutoSpawn = src.AutoSpawn,
            SpawnOffsetX = src.SpawnOffsetX,
            SpawnOffsetY = src.SpawnOffsetY,
            TintColor = src.TintColor,
        };

        int newIdx = _env.AddDef(copy);
        _env.ReloadDefTexture(newIdx);
        _selectedDef = newIdx;
        _propScrollY = 0;
        ReloadPreview();
    }

    private void DeleteSelectedDef()
    {
        if (_selectedDef < 0 || _selectedDef >= _env.DefCount) return;

        _env.RemoveDef(_selectedDef);

        // Adjust selection
        if (_selectedDef >= _env.DefCount)
            _selectedDef = _env.DefCount - 1;
        _propScrollY = 0;
        ReloadPreview();
    }

    // ========================================================================
    //  Save
    // ========================================================================

    private void SaveDefs()
    {
        // Save env defs to a JSON file alongside the map
        try
        {
            string dir = "maps";
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "env_defs.json");

            var options = new JsonSerializerOptions { WriteIndented = true };
            using var stream = File.Create(path);
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

            writer.WriteStartArray();
            for (int i = 0; i < _env.DefCount; i++)
            {
                var def = _env.GetDef(i);
                writer.WriteStartObject();
                writer.WriteString("id", def.Id);
                writer.WriteString("name", def.Name);
                writer.WriteString("category", def.Category);
                writer.WriteString("texturePath", def.TexturePath);
                writer.WriteString("heightMapPath", def.HeightMapPath);
                writer.WriteNumber("spriteWorldHeight", def.SpriteWorldHeight);
                writer.WriteNumber("worldHeight", def.WorldHeight);
                writer.WriteNumber("pivotX", def.PivotX);
                writer.WriteNumber("pivotY", def.PivotY);
                writer.WriteNumber("collisionRadius", def.CollisionRadius);
                writer.WriteNumber("collisionOffsetX", def.CollisionOffsetX);
                writer.WriteNumber("collisionOffsetY", def.CollisionOffsetY);
                writer.WriteNumber("scale", def.Scale);
                writer.WriteNumber("placementScale", def.PlacementScale);
                writer.WriteString("group", def.Group);
                writer.WriteNumber("groupWeight", def.GroupWeight);
                writer.WriteBoolean("isBuilding", def.IsBuilding);
                writer.WriteBoolean("playerBuildable", def.PlayerBuildable);
                writer.WriteNumber("buildingMaxHP", def.BuildingMaxHP);
                writer.WriteNumber("buildingProtection", def.BuildingProtection);
                writer.WriteNumber("buildingDefaultOwner", def.BuildingDefaultOwner);
                writer.WriteNumber("costWood", def.CostWood);
                writer.WriteNumber("costStone", def.CostStone);
                writer.WriteNumber("costGold", def.CostGold);
                writer.WriteString("boundTriggerID", def.BoundTriggerID);
                // Processing slots
                writer.WriteStartObject("input1");
                writer.WriteString("kind", def.Input1.Kind);
                writer.WriteString("resourceID", def.Input1.ResourceID);
                writer.WriteEndObject();
                writer.WriteStartObject("input2");
                writer.WriteString("kind", def.Input2.Kind);
                writer.WriteString("resourceID", def.Input2.ResourceID);
                writer.WriteEndObject();
                writer.WriteStartObject("output");
                writer.WriteString("kind", def.Output.Kind);
                writer.WriteString("resourceID", def.Output.ResourceID);
                writer.WriteEndObject();
                writer.WriteNumber("processTime", def.ProcessTime);
                writer.WriteNumber("maxInputQueue", def.MaxInputQueue);
                writer.WriteNumber("maxOutputQueue", def.MaxOutputQueue);
                writer.WriteBoolean("autoSpawn", def.AutoSpawn);
                writer.WriteNumber("spawnOffsetX", def.SpawnOffsetX);
                writer.WriteNumber("spawnOffsetY", def.SpawnOffsetY);
                // M04: Tint color
                writer.WriteStartObject("tintColor");
                writer.WriteNumber("r", def.TintColor.R);
                writer.WriteNumber("g", def.TintColor.G);
                writer.WriteNumber("b", def.TintColor.B);
                writer.WriteNumber("a", def.TintColor.A);
                writer.WriteNumber("intensity", def.TintColor.Intensity);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.Flush();

            _statusMessage = $"Saved {_env.DefCount} defs to {path}";
            _statusTimer = 3f;
        }
        catch (Exception ex)
        {
            _statusMessage = $"Save failed: {ex.Message}";
            _statusTimer = 4f;
        }
    }

    // ========================================================================
    //  Preview texture
    // ========================================================================

    private void ReloadPreview()
    {
        if (_selectedDef < 0 || _selectedDef >= _env.DefCount)
        {
            _previewTex = null;
            _previewDefIdx = -1;
            return;
        }

        // Ensure the environment system has the texture loaded
        var def = _env.GetDef(_selectedDef);
        _previewTex = _env.GetDefTexture(_selectedDef);
        if (_previewTex == null && !string.IsNullOrEmpty(def.TexturePath))
        {
            _env.ReloadDefTexture(_selectedDef);
            _previewTex = _env.GetDefTexture(_selectedDef);
        }
        _previewDefIdx = _selectedDef;
        _previewTexPath = def.TexturePath;
    }

    // ========================================================================
    //  Circle drawing helper
    // ========================================================================

    private void DrawCircleOutline(Vector2 center, float radius, Color color, int segments)
    {
        float step = MathF.PI * 2f / segments;
        for (int i = 0; i < segments; i++)
        {
            float a1 = i * step;
            float a2 = (i + 1) * step;
            var p1 = center + new Vector2(MathF.Cos(a1) * radius, MathF.Sin(a1) * radius);
            var p2 = center + new Vector2(MathF.Cos(a2) * radius, MathF.Sin(a2) * radius);
            DrawLine(p1, p2, color);
        }
    }

    private void DrawLine(Vector2 a, Vector2 b, Color color)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 1f) return;
        float angle = MathF.Atan2(dy, dx);
        _sb.Draw(_pixel, new Rectangle((int)a.X, (int)a.Y, (int)len, 1),
            null, color, angle, Vector2.Zero, SpriteEffects.None, 0);
    }

    // ========================================================================
    //  Category helpers
    // ========================================================================

    /// <summary>Get list starting with "All" for the filter dropdown.</summary>
    private List<string> GetCategories()
    {
        var cats = new HashSet<string>();
        for (int i = 0; i < _env.DefCount; i++)
            cats.Add(_env.GetDef(i).Category);

        var list = new List<string> { "All" };
        foreach (var c in cats)
            if (c != "All") list.Add(c);
        return list;
    }

    /// <summary>Get existing group names from all defs (for the group dropdown).</summary>
    private List<string> GetExistingGroups()
    {
        var groups = new HashSet<string>();
        for (int i = 0; i < _env.DefCount; i++)
        {
            string g = _env.GetDef(i).Group;
            if (!string.IsNullOrEmpty(g))
                groups.Add(g);
        }

        var list = new List<string>(groups);
        list.Sort();
        return list;
    }

    /// <summary>Get existing categories only (no "All", used for the property dropdown).</summary>
    private List<string> GetExistingCategories()
    {
        var cats = new HashSet<string>();
        for (int i = 0; i < _env.DefCount; i++)
        {
            string cat = _env.GetDef(i).Category;
            if (!string.IsNullOrEmpty(cat))
                cats.Add(cat);
        }

        var list = new List<string>(cats);
        list.Sort();
        if (list.Count == 0) list.Add("Misc");
        return list;
    }

    /// <summary>Get the currently-selected filter category (or "Misc" if "All").</summary>
    private string GetCurrentFilterCategory()
    {
        var cats = GetCategories();
        if (_categoryFilter <= 0 || _categoryFilter >= cats.Count) return "Misc";
        return cats[_categoryFilter];
    }

    /// <summary>Get filtered def indices based on category and search filter.</summary>
    private List<int> GetFilteredDefs()
    {
        var cats = GetCategories();
        string filterCat = _categoryFilter > 0 && _categoryFilter < cats.Count ? cats[_categoryFilter] : "";
        var result = new List<int>();
        for (int i = 0; i < _env.DefCount; i++)
        {
            var def = _env.GetDef(i);
            // Category filter
            if (!string.IsNullOrEmpty(filterCat) && def.Category != filterCat)
                continue;
            // Search filter
            if (!string.IsNullOrEmpty(_searchFilter))
            {
                bool matches = def.Name.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase) ||
                               def.Id.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase);
                if (!matches) continue;
            }
            result.Add(i);
        }
        return result;
    }

    // ========================================================================
    //  Dialogs
    // ========================================================================

    private void DrawConfirmDeleteDialog(int screenW, int screenH)
    {
        string defName = _selectedDef >= 0 && _selectedDef < _env.DefCount
            ? _env.GetDef(_selectedDef).Name : "?";

        if (_ui.DrawConfirmDialog("Delete Definition",
            $"Delete '{defName}'? This cannot be undone.\nPlaced objects using this def will break.",
            ref _confirmDeleteOpen))
        {
            DeleteSelectedDef();
        }
    }

    private void DrawNewCategoryDialog(int screenW, int screenH)
    {
        // Block input to lower layers
        _ui.InputLayer = 3;

        // Dark overlay
        _ui.DrawRect(new Rectangle(0, 0, screenW, screenH), new Color(0, 0, 0, 150));

        int dw = 340, dh = 130;
        int dx = (screenW - dw) / 2;
        int dy = (screenH - dh) / 2;

        _ui.DrawRect(new Rectangle(dx, dy, dw, dh), EditorBase.PanelBg);
        _ui.DrawBorder(new Rectangle(dx, dy, dw, dh), EditorBase.PanelBorder, 2);
        _ui.DrawRect(new Rectangle(dx, dy, dw, 28), EditorBase.PanelHeader);
        _ui.DrawText("New Category", new Vector2(dx + 10, dy + 5), EditorBase.TextBright, _font);

        // Temporarily unblock for the text field
        int savedLayer = _ui.InputLayer;
        _ui.InputLayer = 0;

        _newCategoryName = _ui.DrawTextField("new_cat_name", "Name", _newCategoryName, dx + 16, dy + 40, dw - 32);

        if (_ui.DrawButton("Create", dx + dw / 2 - 100, dy + dh - 40, 80, 28, EditorBase.AccentColor))
        {
            if (!string.IsNullOrWhiteSpace(_newCategoryName) && _selectedDef >= 0)
            {
                _env.GetDef(_selectedDef).Category = _newCategoryName.Trim();
            }
            _newCategoryDialogOpen = false;
        }

        if (_ui.DrawButton("Cancel", dx + dw / 2 + 20, dy + dh - 40, 80, 28))
        {
            _newCategoryDialogOpen = false;
        }

        _ui.InputLayer = savedLayer;
    }

    // ========================================================================
    //  M05: Edge Tweaker
    // ========================================================================

    private void LoadEdgeTweakerTexture(EnvironmentObjectDef def)
    {
        _edgeTweakerPreview = null;
        _edgeTweakerPixels = null;

        if (string.IsNullOrEmpty(def.TexturePath) || !File.Exists(def.TexturePath))
            return;

        try
        {
            using var stream = File.OpenRead(def.TexturePath);
            var tex = Texture2D.FromStream(_device, stream);
            _edgeTweakerTexW = tex.Width;
            _edgeTweakerTexH = tex.Height;
            _edgeTweakerPixels = new Color[tex.Width * tex.Height];
            tex.GetData(_edgeTweakerPixels);
            _edgeTweakerPreview = tex;
        }
        catch
        {
            _edgeTweakerPreview = null;
            _edgeTweakerPixels = null;
        }
    }

    private void ProcessEdgeTweaker()
    {
        if (_edgeTweakerPixels == null || _edgeTweakerPreview == null) return;

        int w = _edgeTweakerTexW;
        int h = _edgeTweakerTexH;
        int threshold = _edgeTweakerThreshold;

        // Find edge pixels that are darker than threshold and set them to transparent
        var result = (Color[])_edgeTweakerPixels.Clone();

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int idx = y * w + x;
                var pixel = result[idx];

                // Skip already transparent pixels
                if (pixel.A == 0) continue;

                // Check if this is an edge pixel (adjacent to transparent)
                bool isEdge = false;
                int[] dx = { -1, 1, 0, 0 };
                int[] dy = { 0, 0, -1, 1 };
                for (int d = 0; d < 4; d++)
                {
                    int nx = x + dx[d];
                    int ny = y + dy[d];
                    if (nx < 0 || nx >= w || ny < 0 || ny >= h)
                    {
                        isEdge = true;
                        break;
                    }
                    if (result[ny * w + nx].A == 0)
                    {
                        isEdge = true;
                        break;
                    }
                }

                if (!isEdge) continue;

                // Check brightness
                int brightness = (pixel.R + pixel.G + pixel.B) / 3;
                if (brightness < threshold)
                {
                    result[idx] = Color.Transparent;
                }
            }
        }

        _edgeTweakerPixels = result;
        _edgeTweakerPreview.SetData(result);
    }

    private void SaveEdgeTweakerResult()
    {
        if (_selectedDef < 0 || _selectedDef >= _env.DefCount) return;
        if (_edgeTweakerPreview == null || _edgeTweakerPixels == null) return;

        var def = _env.GetDef(_selectedDef);
        if (string.IsNullOrEmpty(def.TexturePath)) return;

        try
        {
            using var stream = File.Create(def.TexturePath);
            _edgeTweakerPreview.SaveAsPng(stream, _edgeTweakerTexW, _edgeTweakerTexH);

            // Reload the texture in the environment system
            _env.ReloadDefTexture(_selectedDef);
            ReloadPreview();

            _statusMessage = "Edge tweaker: saved modified texture";
            _statusTimer = 3f;
        }
        catch (Exception ex)
        {
            _statusMessage = $"Edge tweaker save failed: {ex.Message}";
            _statusTimer = 4f;
        }
    }

    private void DrawEdgeTweakerPopup(int screenW, int screenH)
    {
        // Block input to lower layers
        _ui.InputLayer = 2;

        // Dark overlay
        _ui.DrawRect(new Rectangle(0, 0, screenW, screenH), new Color(0, 0, 0, 150));

        int dw = 380, dh = 260;
        int dx = (screenW - dw) / 2;
        int dy = (screenH - dh) / 2;

        _ui.DrawRect(new Rectangle(dx, dy, dw, dh), EditorBase.PanelBg);
        _ui.DrawBorder(new Rectangle(dx, dy, dw, dh), EditorBase.PanelBorder, 2);
        _ui.DrawRect(new Rectangle(dx, dy, dw, 28), EditorBase.PanelHeader);
        _ui.DrawText("Edge Tweaker", new Vector2(dx + 10, dy + 5), EditorBase.TextBright, _font);

        // Close button
        if (_ui.DrawButton("X", dx + dw - 32, dy + 4, 24, 20, EditorBase.DangerColor))
        {
            _edgeTweakerOpen = false;
            _ui.InputLayer = 0;
            return;
        }

        int savedLayer = _ui.InputLayer;
        _ui.InputLayer = 0;

        int cy = dy + 36;

        _ui.DrawText("Removes dark edge pixels from the sprite texture.", new Vector2(dx + 10, cy), EditorBase.TextDim);
        cy += 20;

        _ui.DrawText("Pixels on edges darker than the threshold become transparent.", new Vector2(dx + 10, cy), EditorBase.TextDim);
        cy += 24;

        // Threshold slider
        float threshF = _ui.DrawSliderFloat("edge_tweaker_thresh", "Threshold (0-255)", _edgeTweakerThreshold, 0, 255, dx + 10, cy, dw - 20);
        _edgeTweakerThreshold = (int)threshF;
        cy += 28;

        // Preview of texture if available
        if (_edgeTweakerPreview != null)
        {
            int previewSize = 100;
            float scaleX = (float)previewSize / _edgeTweakerTexW;
            float scaleY = (float)previewSize / _edgeTweakerTexH;
            float scale = MathF.Min(scaleX, scaleY);
            int pw = (int)(_edgeTweakerTexW * scale);
            int ph = (int)(_edgeTweakerTexH * scale);
            int px = dx + (dw - pw) / 2;

            _ui.DrawRect(new Rectangle(px - 1, cy - 1, pw + 2, ph + 2), new Color(40, 40, 60));
            _sb.Draw(_edgeTweakerPreview, new Rectangle(px, cy, pw, ph), Color.White);
            cy += ph + 8;
        }
        else
        {
            _ui.DrawText("No texture loaded", new Vector2(dx + 10, cy), EditorBase.TextDim);
            cy += 24;
        }

        // Process button
        if (_ui.DrawButton("Process", dx + dw / 2 - 130, cy, 80, 28, EditorBase.AccentColor))
        {
            ProcessEdgeTweaker();
        }

        // Save button
        if (_ui.DrawButton("Save", dx + dw / 2 - 40, cy, 80, 28, new Color(50, 120, 50)))
        {
            SaveEdgeTweakerResult();
            _edgeTweakerOpen = false;
        }

        // Cancel button
        if (_ui.DrawButton("Cancel", dx + dw / 2 + 50, cy, 80, 28))
        {
            _edgeTweakerOpen = false;
        }

        _ui.InputLayer = savedLayer;
    }
}
