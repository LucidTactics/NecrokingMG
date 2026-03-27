using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Necroking.Core;
using Necroking.World;

namespace Necroking.Editor;

/// <summary>
/// Popup sub-window for editing wall visual definitions.
/// Layout: Left panel (def list), Center panel (preview + neighbor sim), Right panel (properties).
/// Opened from the Walls tab "Edit Wall Defs" button.
/// </summary>
public class WallEditorWindow
{
    private readonly EditorBase _ui;
    private WallSystem _walls = null!;

    // State
    private bool _open;
    public bool IsOpen => _open;

    private int _selectedDef;
    private int _selectedSegment; // WallSegDir index

    // Neighbor simulation (N, E, S, W, NE, NW, SE, SW)
    private readonly bool[] _simNeighbors = { true, true, true, true, true, true, true, true };
    private bool _showNeighborTiles;

    // Status
    private string _statusMessage = "";
    private float _statusTimer;

    // Layout constants
    private const int WinMargin = 30;
    private const int TitleBarH = 30;
    private const int ListW = 180;
    private const int PreviewW = 300;
    private const int RowH = 24;

    // Segment grid labels matching WallSegDir enum order
    private static readonly string[] SegDirNames =
        { "Ctr", "N", "S", "E", "W", "NE", "NW", "SE", "SW" };

    // 3x3 grid layout: row,col -> WallSegDir
    // NW, N, NE / W, Ctr, E / SW, S, SE
    private static readonly WallSegDir[,] GridMap =
    {
        { WallSegDir.NW, WallSegDir.N, WallSegDir.NE },
        { WallSegDir.W, WallSegDir.Center, WallSegDir.E },
        { WallSegDir.SW, WallSegDir.S, WallSegDir.SE }
    };
    private static readonly string[,] GridLabels =
    {
        { "NW", "N", "NE" },
        { "W", "Ctr", "E" },
        { "SW", "S", "SE" }
    };

    // Neighbor labels (N, E, S, W, NE, NW, SE, SW) matching _simNeighbors order
    private static readonly string[] NeighborLabels = { "N", "E", "S", "W", "NE", "NW", "SE", "SW" };

    // M12: Segment drag/resize state
    private enum DragMode { None, Move, ResizeLeft, ResizeRight, ResizeTop, ResizeBottom,
        ResizeTL, ResizeTR, ResizeBL, ResizeBR }
    private DragMode _dragMode = DragMode.None;
    private int _dragSegment = -1;
    private float _dragStartMouseX, _dragStartMouseY;
    private float _dragStartOffX, _dragStartOffY;
    private float _dragStartSzW, _dragStartSzH;
    private const int HandleSize = 6; // resize handle half-size in pixels

    // RM32: Texture file browser for per-segment sprite path
    private readonly TextureFileBrowser _textureBrowser = new();
    private MouseState _prevMouseWall;
    private KeyboardState _prevKbWall;

    // Save path
    private string _mapFilename = "default";

    public WallEditorWindow(EditorBase ui)
    {
        _ui = ui;
    }

    public void SetWallSystem(WallSystem walls)
    {
        _walls = walls;
    }

    public void SetMapFilename(string filename)
    {
        _mapFilename = filename;
    }

    public void Open(int defIndex = 0)
    {
        _open = true;
        if (_walls != null && defIndex >= 0 && defIndex < _walls.DefCount)
            _selectedDef = defIndex;
        else
            _selectedDef = 0;
        _selectedSegment = 0;
        // Reset scroll state (managed by EditorBase internally)
    }

    public void Close()
    {
        _open = false;
    }

    /// <summary>
    /// Draw the wall editor overlay. Returns true if the overlay is open (blocks input to parent).
    /// </summary>
    public bool Draw(int screenW, int screenH, GameTime gameTime)
    {
        if (!_open || _walls == null) return false;

        float dt = (float)gameTime.ElapsedGameTime.TotalSeconds;
        if (_statusTimer > 0) _statusTimer -= dt;

        // Handle keyboard shortcuts
        var kb = Keyboard.GetState();
        var prevKb = _ui._prevKb;

        // Escape: close dropdown first, then close window
        if (kb.IsKeyDown(Keys.Escape) && prevKb.IsKeyUp(Keys.Escape))
        {
            if (!_ui.CloseActiveDropdown())
            {
                _open = false;
                return false;
            }
        }

        // Ctrl+S to save
        if (kb.IsKeyDown(Keys.LeftControl) && kb.IsKeyDown(Keys.S) && prevKb.IsKeyUp(Keys.S))
            Save();

        // Block input to layer 0 (parent editor) while this popup is open
        _ui.InputLayer = 1;

        // Full-screen dark overlay
        _ui.DrawRect(new Rectangle(0, 0, screenW, screenH), new Color(0, 0, 0, 180));

        // Window dimensions
        int winW = Math.Min(960, screenW - WinMargin * 2);
        int winH = Math.Min(640, screenH - WinMargin * 2);
        int winX = (screenW - winW) / 2;
        int winY = (screenH - winH) / 2;

        // Window background
        _ui.DrawRect(new Rectangle(winX, winY, winW, winH), new Color(35, 35, 45, 250));
        _ui.DrawBorder(new Rectangle(winX, winY, winW, winH), new Color(70, 70, 90), 2);

        // Title bar
        _ui.DrawRect(new Rectangle(winX, winY, winW, TitleBarH), new Color(45, 45, 60, 255));

        string title = "Wall Editor";
        if (_selectedDef >= 0 && _selectedDef < _walls.DefCount)
            title = $"Wall Editor - {_walls.Defs[_selectedDef].Name}";
        _ui.DrawText(title, new Vector2(winX + 70, winY + 7), Color.White);

        // Save button
        if (_ui.DrawButton("Save", winX + 6, winY + 3, 54, 24))
            Save();

        // Save status flash
        if (_statusTimer > 0)
        {
            float alpha = Math.Min(1f, _statusTimer);
            var col = new Color(100, 255, 100, (int)(alpha * 200));
            _ui.DrawText(_statusMessage, new Vector2(winX + winW - 160, winY + 8), col);
        }

        // Close button
        if (_ui.DrawButton("X", winX + winW - 30, winY + 1, 28, 28, new Color(150, 50, 50)))
        {
            _open = false;
            return false;
        }

        // Content area
        int contentY = winY + TitleBarH + 2;
        int contentH = winH - TitleBarH - 2;

        // Column layout
        int propsW = winW - ListW - PreviewW;
        int col0X = winX;
        int col1X = col0X + ListW;
        int col2X = col1X + PreviewW;

        // Draw columns
        DrawDefList(col0X, contentY, ListW, contentH);
        DrawPreview(col1X, contentY, PreviewW, contentH);
        DrawProperties(col2X, contentY, propsW, contentH);

        // RM32: Update and draw texture file browser
        var wallMouse = Mouse.GetState();
        var wallKb = Keyboard.GetState();
        _textureBrowser.Update(wallMouse, _prevMouseWall, wallKb, _prevKbWall);
        _textureBrowser.Draw(_ui, screenW, screenH);
        _prevMouseWall = wallMouse;
        _prevKbWall = wallKb;

        // Color picker overlay (drawn last for z-order)
        _ui.DrawColorPickerPopup();

        // Dropdown overlays (drawn last, on top of everything)
        _ui.DrawDropdownOverlays();

        return true;
    }

    // ========================================================================
    //  Def List (left panel)
    // ========================================================================

    private void DrawDefList(int x, int y, int w, int h)
    {
        // Column separator
        _ui.DrawRect(new Rectangle(x + w - 1, y, 1, h), new Color(60, 60, 80));

        // Section label
        _ui.DrawText("Wall Definitions", new Vector2(x + 6, y + 4), EditorBase.TextBright);
        int listY = y + 24;
        int btnH = 26;
        int btnPad = 6;

        // Reserve space for New/Delete buttons at bottom
        int bottomBtnArea = btnH * 2 + btnPad * 3;
        int listH = h - 24 - bottomBtnArea;

        // Build display list
        var defs = _walls.Defs;
        var names = new List<string>();
        for (int i = 0; i < defs.Count; i++)
            names.Add(defs[i].Name);

        // Draw scrollable list
        int clicked = _ui.DrawScrollableList("walleditor_deflist", names, _selectedDef,
            x + 2, listY, w - 4, listH);
        if (clicked >= 0 && clicked != _selectedDef)
        {
            _selectedDef = clicked;
            _selectedSegment = 0;
            // Scroll state is managed by EditorBase per-panel
        }

        // New button
        int btnY = listY + listH + btnPad;
        if (_ui.DrawButton("+ New", x + btnPad, btnY, w - btnPad * 2, btnH))
        {
            var newDef = new WallVisualDef { Name = $"New Wall {defs.Count}" };
            defs.Add(newDef);
            _selectedDef = defs.Count - 1;
            _selectedSegment = 0;
            // Scroll state is managed by EditorBase per-panel
        }

        // Delete button
        btnY += btnH + btnPad;
        bool canDelete = defs.Count > 1 && _selectedDef >= 0 && _selectedDef < defs.Count;
        if (_ui.DrawButton("Delete", x + btnPad, btnY, w - btnPad * 2, btnH,
                canDelete ? EditorBase.DangerColor : new Color(60, 60, 60)) && canDelete)
        {
            defs.RemoveAt(_selectedDef);
            if (_selectedDef >= defs.Count)
                _selectedDef = defs.Count - 1;
            _selectedSegment = 0;
            // Scroll state is managed by EditorBase per-panel
        }
    }

    // ========================================================================
    //  Preview (center panel)
    // ========================================================================

    private void DrawPreview(int x, int y, int w, int h)
    {
        // Column separator
        _ui.DrawRect(new Rectangle(x + w - 1, y, 1, h), new Color(60, 60, 80));

        if (_selectedDef < 0 || _selectedDef >= _walls.DefCount) return;
        var def = _walls.Defs[_selectedDef];

        int pad = 8;
        int previewH = h - 130; // Reserve space for neighbor sim controls

        // Preview background
        var previewRect = new Rectangle(x + pad, y + pad, w - pad * 2, previewH);
        _ui.DrawRect(previewRect, new Color(20, 20, 30, 255));
        _ui.DrawBorder(previewRect, new Color(50, 50, 65));

        // Center of preview
        float cx = previewRect.X + previewRect.Width * 0.5f;
        float cy = previewRect.Y + previewRect.Height * 0.5f;

        // Draw wall block outline (WallScale = 4 tiles wide/high conceptually)
        float tileW = Math.Min(previewRect.Width - pad * 4, previewH * 0.6f) / WallSystem.WallScale;
        float blockW = tileW * WallSystem.WallScale;
        float blockH = blockW * 0.5f; // Y ratio
        var blockRect = new Rectangle(
            (int)(cx - blockW * 0.5f), (int)(cy - blockH * 0.5f),
            (int)blockW, (int)blockH);
        _ui.DrawBorder(blockRect, new Color(60, 60, 70, 120));

        // Crosshair at center
        _ui.DrawRect(new Rectangle((int)(cx - 4), (int)cy, 8, 1), new Color(80, 80, 90, 100));
        _ui.DrawRect(new Rectangle((int)cx, (int)(cy - 4), 1, 8), new Color(80, 80, 90, 100));

        // Build neighbor mask from sim checkboxes
        // _simNeighbors: 0=N, 1=E, 2=S, 3=W, 4=NE, 5=NW, 6=SE, 7=SW
        int neighborMask = 0;
        for (int i = 0; i < 8; i++)
        {
            if (_simNeighbors[i]) neighborMask |= (1 << i);
        }

        // Draw colored rectangles for each enabled segment
        float zoom = tileW * WallSystem.WallScale;

        // Draw order (back to front): N, NW, NE, W, E, Center, SW, SE, S
        WallSegDir[] drawOrder =
        {
            WallSegDir.N, WallSegDir.NW, WallSegDir.NE,
            WallSegDir.W, WallSegDir.E, WallSegDir.Center,
            WallSegDir.SW, WallSegDir.SE, WallSegDir.S
        };

        // Neighbor tile offsets: N,E,S,W,NE,NW,SE,SW (in wall-block units)
        int[,] neighborOff = { {0,-1}, {1,0}, {0,1}, {-1,0}, {1,-1}, {-1,-1}, {1,1}, {-1,1} };

        // Draw neighbor tile outlines and dimmed center blocks when Show Neighbors is on
        if (_showNeighborTiles)
        {
            var dimColor = new Color(
                (byte)(def.Color.R * 0.4f),
                (byte)(def.Color.G * 0.4f),
                (byte)(def.Color.B * 0.4f),
                (byte)140);

            for (int ni = 0; ni < 8; ni++)
            {
                if (!_simNeighbors[ni]) continue;
                float ncx = cx + neighborOff[ni, 0] * blockW;
                float ncy = cy + neighborOff[ni, 1] * blockH;

                // Faint tile outline for neighbor
                var nTileRect = new Rectangle(
                    (int)(ncx - blockW * 0.5f), (int)(ncy - blockH * 0.5f),
                    (int)blockW, (int)blockH);
                _ui.DrawBorder(nTileRect, new Color(40, 40, 55, 60));

                // Draw a simple dimmed center block for the neighbor
                var centerSeg = def.Segments[(int)WallSegDir.Center];
                if (centerSeg.Enabled)
                {
                    float dstW = centerSeg.Size.X * zoom;
                    float dstH = centerSeg.Size.Y * zoom;
                    float dstX = ncx + centerSeg.Offset.X * zoom - dstW * centerSeg.Pivot.X;
                    float dstY = ncy + centerSeg.Offset.Y * zoom * 0.5f - dstH * centerSeg.Pivot.Y;
                    _ui.DrawRect(new Rectangle((int)dstX, (int)dstY,
                        Math.Max(1, (int)dstW), Math.Max(1, (int)dstH)), dimColor);
                }
            }
        }

        // Draw main tile segments as colored rectangles
        // Track the selected segment's screen rect for drag handles
        Rectangle selectedSegRect = Rectangle.Empty;
        foreach (var dir in drawOrder)
        {
            int idx = (int)dir;
            var seg = def.Segments[idx];
            if (!seg.Enabled) continue;

            // Determine if this segment should draw given the neighbor mask
            if (!ShouldDrawSegment(dir, neighborMask)) continue;

            float dstW = seg.Size.X * zoom;
            float dstH = seg.Size.Y * zoom;
            float dstX = cx + seg.Offset.X * zoom - dstW * seg.Pivot.X;
            float dstY = cy + seg.Offset.Y * zoom * 0.5f - dstH * seg.Pivot.Y;

            var segRect = new Rectangle((int)dstX, (int)dstY, Math.Max(1, (int)dstW), Math.Max(1, (int)dstH));

            // Draw the segment as a colored rectangle
            bool isSelected = (_selectedSegment == idx);
            Color segColor = def.Color;
            if (isSelected)
            {
                // Brighten selected segment
                segColor = new Color(
                    Math.Min(255, segColor.R + 40),
                    Math.Min(255, segColor.G + 40),
                    Math.Min(255, segColor.B + 40),
                    segColor.A);
                selectedSegRect = segRect;
            }
            _ui.DrawRect(segRect, segColor);

            // Draw label on segment
            string segLabel = SegDirNames[idx];
            _ui.DrawText(segLabel,
                new Vector2(segRect.X + 2, segRect.Y + 2),
                isSelected ? Color.Yellow : new Color(220, 220, 220));

            // Highlight border for selected segment
            if (isSelected)
            {
                _ui.DrawBorder(segRect, new Color(255, 200, 50, 200), 2);
            }
        }

        // M12: Draw resize handles and handle drag for the selected segment
        if (_selectedSegment >= 0 && _selectedSegment < (int)WallSegDir.Count
            && selectedSegRect != Rectangle.Empty)
        {
            var selSeg = def.Segments[_selectedSegment];
            var mouse = _ui._mouse;
            var prevMouse = _ui._prevMouse;
            int hs = HandleSize;

            // Define the 8 handle rects (corners + edges)
            var handleTL = new Rectangle(selectedSegRect.X - hs, selectedSegRect.Y - hs, hs * 2, hs * 2);
            var handleTR = new Rectangle(selectedSegRect.Right - hs, selectedSegRect.Y - hs, hs * 2, hs * 2);
            var handleBL = new Rectangle(selectedSegRect.X - hs, selectedSegRect.Bottom - hs, hs * 2, hs * 2);
            var handleBR = new Rectangle(selectedSegRect.Right - hs, selectedSegRect.Bottom - hs, hs * 2, hs * 2);
            var handleT = new Rectangle(selectedSegRect.X + selectedSegRect.Width / 2 - hs, selectedSegRect.Y - hs, hs * 2, hs * 2);
            var handleB = new Rectangle(selectedSegRect.X + selectedSegRect.Width / 2 - hs, selectedSegRect.Bottom - hs, hs * 2, hs * 2);
            var handleL = new Rectangle(selectedSegRect.X - hs, selectedSegRect.Y + selectedSegRect.Height / 2 - hs, hs * 2, hs * 2);
            var handleR = new Rectangle(selectedSegRect.Right - hs, selectedSegRect.Y + selectedSegRect.Height / 2 - hs, hs * 2, hs * 2);

            // Draw handles as small filled squares
            Color handleColor = new Color(255, 220, 80, 220);
            _ui.DrawRect(handleTL, handleColor); _ui.DrawRect(handleTR, handleColor);
            _ui.DrawRect(handleBL, handleColor); _ui.DrawRect(handleBR, handleColor);
            _ui.DrawRect(handleT, handleColor); _ui.DrawRect(handleB, handleColor);
            _ui.DrawRect(handleL, handleColor); _ui.DrawRect(handleR, handleColor);

            // Mouse press: determine drag mode
            bool mouseDown = mouse.LeftButton == ButtonState.Pressed && prevMouse.LeftButton == ButtonState.Released;
            if (mouseDown && _dragMode == DragMode.None && previewRect.Contains(mouse.X, mouse.Y))
            {
                DragMode mode = DragMode.None;
                if (handleTL.Contains(mouse.X, mouse.Y)) mode = DragMode.ResizeTL;
                else if (handleTR.Contains(mouse.X, mouse.Y)) mode = DragMode.ResizeTR;
                else if (handleBL.Contains(mouse.X, mouse.Y)) mode = DragMode.ResizeBL;
                else if (handleBR.Contains(mouse.X, mouse.Y)) mode = DragMode.ResizeBR;
                else if (handleT.Contains(mouse.X, mouse.Y)) mode = DragMode.ResizeTop;
                else if (handleB.Contains(mouse.X, mouse.Y)) mode = DragMode.ResizeBottom;
                else if (handleL.Contains(mouse.X, mouse.Y)) mode = DragMode.ResizeLeft;
                else if (handleR.Contains(mouse.X, mouse.Y)) mode = DragMode.ResizeRight;
                else if (selectedSegRect.Contains(mouse.X, mouse.Y)) mode = DragMode.Move;

                if (mode != DragMode.None)
                {
                    _dragMode = mode;
                    _dragSegment = _selectedSegment;
                    _dragStartMouseX = mouse.X;
                    _dragStartMouseY = mouse.Y;
                    _dragStartOffX = selSeg.Offset.X;
                    _dragStartOffY = selSeg.Offset.Y;
                    _dragStartSzW = selSeg.Size.X;
                    _dragStartSzH = selSeg.Size.Y;
                }
            }

            // Mouse drag: apply offset/size changes
            if (_dragMode != DragMode.None && _dragSegment == _selectedSegment
                && mouse.LeftButton == ButtonState.Pressed)
            {
                float dx = mouse.X - _dragStartMouseX;
                float dy = mouse.Y - _dragStartMouseY;
                // Convert pixel delta to world units
                float worldDx = dx / zoom;
                float worldDy = dy / (zoom * 0.5f); // Y has 0.5 ratio

                // RM33: Account for pivot in resize offset adjustment
                // When resizing from an edge, the offset must compensate for the pivot
                // so the opposite edge stays anchored.
                float pvX = selSeg.Pivot.X;
                float pvY = selSeg.Pivot.Y;

                switch (_dragMode)
                {
                    case DragMode.Move:
                        selSeg.Offset = new Vector2(_dragStartOffX + worldDx, _dragStartOffY + worldDy);
                        break;
                    case DragMode.ResizeRight:
                    {
                        float newW = Math.Max(0.01f, _dragStartSzW + worldDx);
                        float dw = newW - _dragStartSzW;
                        selSeg.Size = new Vector2(newW, selSeg.Size.Y);
                        selSeg.Offset = new Vector2(_dragStartOffX + dw * (1f - pvX), selSeg.Offset.Y);
                        break;
                    }
                    case DragMode.ResizeLeft:
                    {
                        float newW = Math.Max(0.01f, _dragStartSzW - worldDx);
                        float dw = newW - _dragStartSzW;
                        selSeg.Size = new Vector2(newW, selSeg.Size.Y);
                        selSeg.Offset = new Vector2(_dragStartOffX - dw * pvX, selSeg.Offset.Y);
                        break;
                    }
                    case DragMode.ResizeBottom:
                    {
                        float newH = Math.Max(0.01f, _dragStartSzH + worldDy);
                        float dh = newH - _dragStartSzH;
                        selSeg.Size = new Vector2(selSeg.Size.X, newH);
                        selSeg.Offset = new Vector2(selSeg.Offset.X, _dragStartOffY + dh * (1f - pvY));
                        break;
                    }
                    case DragMode.ResizeTop:
                    {
                        float newH = Math.Max(0.01f, _dragStartSzH - worldDy);
                        float dh = newH - _dragStartSzH;
                        selSeg.Size = new Vector2(selSeg.Size.X, newH);
                        selSeg.Offset = new Vector2(selSeg.Offset.X, _dragStartOffY - dh * pvY);
                        break;
                    }
                    case DragMode.ResizeTL:
                    {
                        float newW = Math.Max(0.01f, _dragStartSzW - worldDx);
                        float newH = Math.Max(0.01f, _dragStartSzH - worldDy);
                        float dw = newW - _dragStartSzW;
                        float dh = newH - _dragStartSzH;
                        selSeg.Size = new Vector2(newW, newH);
                        selSeg.Offset = new Vector2(_dragStartOffX - dw * pvX, _dragStartOffY - dh * pvY);
                        break;
                    }
                    case DragMode.ResizeTR:
                    {
                        float newW = Math.Max(0.01f, _dragStartSzW + worldDx);
                        float newH = Math.Max(0.01f, _dragStartSzH - worldDy);
                        float dw = newW - _dragStartSzW;
                        float dh = newH - _dragStartSzH;
                        selSeg.Size = new Vector2(newW, newH);
                        selSeg.Offset = new Vector2(_dragStartOffX + dw * (1f - pvX), _dragStartOffY - dh * pvY);
                        break;
                    }
                    case DragMode.ResizeBL:
                    {
                        float newW = Math.Max(0.01f, _dragStartSzW - worldDx);
                        float newH = Math.Max(0.01f, _dragStartSzH + worldDy);
                        float dw = newW - _dragStartSzW;
                        float dh = newH - _dragStartSzH;
                        selSeg.Size = new Vector2(newW, newH);
                        selSeg.Offset = new Vector2(_dragStartOffX - dw * pvX, _dragStartOffY + dh * (1f - pvY));
                        break;
                    }
                    case DragMode.ResizeBR:
                    {
                        float newW = Math.Max(0.01f, _dragStartSzW + worldDx);
                        float newH = Math.Max(0.01f, _dragStartSzH + worldDy);
                        float dw = newW - _dragStartSzW;
                        float dh = newH - _dragStartSzH;
                        selSeg.Size = new Vector2(newW, newH);
                        selSeg.Offset = new Vector2(_dragStartOffX + dw * (1f - pvX), _dragStartOffY + dh * (1f - pvY));
                        break;
                    }
                }
            }

            // Mouse release: end drag
            if (mouse.LeftButton == ButtonState.Released && _dragMode != DragMode.None)
            {
                _dragMode = DragMode.None;
                _dragSegment = -1;
            }
        }
        else if (_ui._mouse.LeftButton == ButtonState.Released)
        {
            // Cancel drag if segment deselected
            _dragMode = DragMode.None;
            _dragSegment = -1;
        }

        // --- Neighbor simulation controls ---
        int simY = y + pad + previewH + 14;
        _ui.DrawText("Sim Neighbors:", new Vector2(x + pad, simY), EditorBase.TextDim);
        simY += 20;

        // Row 1: Cardinal directions (N, E, S, W)
        int cbX = x + pad;
        for (int i = 0; i < 4; i++)
        {
            _simNeighbors[i] = _ui.DrawCheckbox(NeighborLabels[i], _simNeighbors[i], cbX, simY);
            cbX += 60;
        }
        simY += 22;

        // Row 2: Diagonal directions (NE, NW, SE, SW)
        cbX = x + pad;
        for (int i = 4; i < 8; i++)
        {
            _simNeighbors[i] = _ui.DrawCheckbox(NeighborLabels[i], _simNeighbors[i], cbX, simY);
            cbX += 60;
        }
        simY += 22;

        // Show Neighbor Tiles toggle
        _showNeighborTiles = _ui.DrawCheckbox("Show Neighbors", _showNeighborTiles, x + pad, simY);
        simY += 24;

        // Segment selector (3x3 grid)
        _ui.DrawText("Segments:", new Vector2(x + pad, simY), EditorBase.TextDim);
        simY += 18;

        int btnSize = 32;
        int gap = 4;
        int gridW = btnSize * 3 + gap * 2;
        int gridX = x + (w - gridW) / 2;

        for (int row = 0; row < 3; row++)
        {
            for (int col = 0; col < 3; col++)
            {
                var dir = GridMap[row, col];
                int idx = (int)dir;
                bool enabled = def.Segments[idx].Enabled;
                bool selected = (_selectedSegment == idx);

                int bx = gridX + col * (btnSize + gap);
                int by = simY + row * (btnSize + gap);

                // Background color
                Color bg;
                if (selected) bg = new Color(70, 70, 140);
                else if (enabled) bg = new Color(40, 80, 40);
                else bg = new Color(35, 35, 45);

                // Check hover
                var mouse = _ui._mouse;
                var btnRect = new Rectangle(bx, by, btnSize, btnSize);
                bool hovered = btnRect.Contains(mouse.X, mouse.Y);
                if (hovered)
                    bg = new Color(Math.Min(255, bg.R + 20), Math.Min(255, bg.G + 20), Math.Min(255, bg.B + 20));

                _ui.DrawRect(btnRect, bg);

                // Border
                Color border = selected ? new Color(120, 120, 220) :
                               enabled ? new Color(60, 120, 60) :
                                         new Color(50, 50, 65);
                _ui.DrawBorder(btnRect, border, selected ? 2 : 1);

                // Label
                string lbl = GridLabels[row, col];
                Color textCol = enabled ? Color.White : new Color(100, 100, 115);
                _ui.DrawText(lbl, new Vector2(bx + 4, by + (btnSize - 14) / 2), textCol);

                // Click to select
                if (hovered && mouse.LeftButton == ButtonState.Pressed &&
                    _ui._prevMouse.LeftButton == ButtonState.Released)
                {
                    _selectedSegment = idx;
                }
            }
        }
    }

    // ========================================================================
    //  Properties (right panel)
    // ========================================================================

    private void DrawProperties(int x, int y, int w, int h)
    {
        if (_selectedDef < 0 || _selectedDef >= _walls.DefCount) return;
        var def = _walls.Defs[_selectedDef];

        int pad = 8;
        int fieldW = w - pad * 2;
        int curY = y + pad;

        // --- Wall Definition Properties ---
        _ui.DrawText("Wall Definition", new Vector2(x + pad, curY), EditorBase.AccentColor);
        curY += RowH;

        // Name
        string newName = _ui.DrawTextField("walldef_name", "Name", def.Name, x + pad, curY, fieldW);
        if (newName != def.Name) def.Name = newName;
        curY += RowH;

        // Max HP
        int newMaxHP = _ui.DrawIntField("walldef_maxhp", "Max HP", def.MaxHP, x + pad, curY, fieldW);
        if (newMaxHP != def.MaxHP) def.MaxHP = Math.Clamp(newMaxHP, 1, 99999);
        curY += RowH;

        // Protection
        int newProt = _ui.DrawIntField("walldef_prot", "Protection", def.Protection, x + pad, curY, fieldW);
        if (newProt != def.Protection) def.Protection = Math.Clamp(newProt, 0, 9999);
        curY += RowH;

        // Color swatch
        var hdrColor = new HdrColor(def.Color.R, def.Color.G, def.Color.B, def.Color.A);
        if (_ui.DrawColorSwatch("walldef_color", x + pad, curY, 140, 20, ref hdrColor))
        {
            def.Color = hdrColor.ToColor();
        }
        _ui.DrawText("Color", new Vector2(x + pad + 148, curY + 2), EditorBase.TextDim);
        curY += RowH + 4;

        // Separator
        _ui.DrawRect(new Rectangle(x + pad, curY, fieldW, 1), new Color(60, 60, 80));
        curY += 8;

        // --- Segment Properties ---
        if (_selectedSegment >= 0 && _selectedSegment < (int)WallSegDir.Count)
        {
            var seg = def.Segments[_selectedSegment];
            string segName = SegDirNames[_selectedSegment];

            _ui.DrawText($"Segment: {segName}", new Vector2(x + pad, curY), EditorBase.AccentColor);
            curY += RowH;

            // Enabled checkbox
            bool newEnabled = _ui.DrawCheckbox("Enabled", seg.Enabled, x + pad, curY);
            if (newEnabled != seg.Enabled) seg.Enabled = newEnabled;
            curY += RowH;

            // RM32: Sprite path with Browse button
            string displayPath = string.IsNullOrEmpty(seg.SpritePath) ? "(none)" : seg.SpritePath;
            if (displayPath.Length > 25)
            {
                int lastSlash = displayPath.LastIndexOfAny(new[] { '/', '\\' });
                if (lastSlash >= 0) displayPath = "..." + displayPath[lastSlash..];
            }
            int browseBtnW = 55;
            string newPath = _ui.DrawTextField("wallseg_sprite", "Sprite", displayPath, x + pad, curY, fieldW - browseBtnW - 4);
            // If user edits the sprite path, apply it
            if (newPath != displayPath && newPath != "(none)")
                seg.SpritePath = newPath;
            if (_ui.DrawButton("Browse", x + pad + fieldW - browseBtnW, curY, browseBtnW, 20))
            {
                int capturedSeg = _selectedSegment;
                _textureBrowser.Open("assets/Environment/Walls", seg.SpritePath, path =>
                {
                    if (_selectedDef >= 0 && _selectedDef < _walls.DefCount &&
                        capturedSeg >= 0 && capturedSeg < (int)WallSegDir.Count)
                    {
                        _walls.Defs[_selectedDef].Segments[capturedSeg].SpritePath = path;
                    }
                });
            }
            curY += RowH;

            // Separator
            _ui.DrawRect(new Rectangle(x + pad, curY, fieldW, 1), new Color(50, 50, 65));
            curY += 6;

            // Source Rect
            _ui.DrawText("Source Rect", new Vector2(x + pad, curY), EditorBase.TextDim);
            curY += 18;

            var srcRect = seg.SrcRect;
            float newSrcX = _ui.DrawFloatField("wallseg_srcx", "SrcX", srcRect.X, x + pad, curY, fieldW, 1f);
            curY += RowH;
            float newSrcY = _ui.DrawFloatField("wallseg_srcy", "SrcY", srcRect.Y, x + pad, curY, fieldW, 1f);
            curY += RowH;
            float newSrcW = _ui.DrawFloatField("wallseg_srcw", "SrcW", srcRect.Width, x + pad, curY, fieldW, 1f);
            curY += RowH;
            float newSrcH = _ui.DrawFloatField("wallseg_srch", "SrcH", srcRect.Height, x + pad, curY, fieldW, 1f);
            curY += RowH;
            seg.SrcRect = new Rectangle((int)newSrcX, (int)newSrcY, (int)newSrcW, (int)newSrcH);

            // Separator
            _ui.DrawRect(new Rectangle(x + pad, curY, fieldW, 1), new Color(50, 50, 65));
            curY += 6;

            // Offset & Size
            _ui.DrawText("Offset & Size", new Vector2(x + pad, curY), EditorBase.TextDim);
            curY += 18;

            var offset = seg.Offset;
            float newOffX = _ui.DrawFloatField("wallseg_offx", "OffsetX", offset.X, x + pad, curY, fieldW, 0.01f);
            curY += RowH;
            float newOffY = _ui.DrawFloatField("wallseg_offy", "OffsetY", offset.Y, x + pad, curY, fieldW, 0.01f);
            curY += RowH;
            seg.Offset = new Vector2(newOffX, newOffY);

            var size = seg.Size;
            float newSzW = _ui.DrawFloatField("wallseg_szw", "SizeW", size.X, x + pad, curY, fieldW, 0.01f);
            curY += RowH;
            float newSzH = _ui.DrawFloatField("wallseg_szh", "SizeH", size.Y, x + pad, curY, fieldW, 0.01f);
            curY += RowH;
            seg.Size = new Vector2(Math.Max(0.01f, newSzW), Math.Max(0.01f, newSzH));

            var pivot = seg.Pivot;
            float newPvX = _ui.DrawFloatField("wallseg_pvx", "PivotX", pivot.X, x + pad, curY, fieldW, 0.01f);
            curY += RowH;
            float newPvY = _ui.DrawFloatField("wallseg_pvy", "PivotY", pivot.Y, x + pad, curY, fieldW, 0.01f);
            curY += RowH;
            seg.Pivot = new Vector2(Math.Clamp(newPvX, 0f, 1f), Math.Clamp(newPvY, 0f, 1f));
        }
    }

    // ========================================================================
    //  Segment visibility logic
    // ========================================================================

    /// <summary>
    /// Determines whether a given wall segment direction should be drawn for the given neighbor mask.
    /// The center segment is always drawn. Cardinal segments (N/S/E/W) are drawn when
    /// that neighbor is NOT present (the segment fills the gap). Diagonal segments are drawn
    /// based on adjacency rules.
    /// </summary>
    private static bool ShouldDrawSegment(WallSegDir dir, int neighborMask)
    {
        // Mask bits: 0=N, 1=E, 2=S, 3=W, 4=NE, 5=NW, 6=SE, 7=SW
        const int N = 0x01, E = 0x02, S = 0x04, W = 0x08;
        const int NE = 0x10, NW = 0x20, SE = 0x40, SW = 0x80;

        switch (dir)
        {
            case WallSegDir.Center:
                return true; // Always draw center
            case WallSegDir.N:
                return (neighborMask & N) != 0;
            case WallSegDir.S:
                return (neighborMask & S) != 0;
            case WallSegDir.E:
                return (neighborMask & E) != 0;
            case WallSegDir.W:
                return (neighborMask & W) != 0;
            case WallSegDir.NE:
                return (neighborMask & NE) != 0 || ((neighborMask & N) != 0 && (neighborMask & E) != 0);
            case WallSegDir.NW:
                return (neighborMask & NW) != 0 || ((neighborMask & N) != 0 && (neighborMask & W) != 0);
            case WallSegDir.SE:
                return (neighborMask & SE) != 0 || ((neighborMask & S) != 0 && (neighborMask & E) != 0);
            case WallSegDir.SW:
                return (neighborMask & SW) != 0 || ((neighborMask & S) != 0 && (neighborMask & W) != 0);
            default:
                return false;
        }
    }

    // ========================================================================
    //  Save
    // ========================================================================

    private void Save()
    {
        try
        {
            string mapsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "maps");
            Directory.CreateDirectory(mapsDir);
            string path = Path.Combine(mapsDir, _mapFilename + "_walldefs.json");

            using var stream = File.Create(path);
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

            writer.WriteStartArray();
            foreach (var def in _walls.Defs)
            {
                writer.WriteStartObject();
                writer.WriteString("name", def.Name);
                writer.WriteNumber("maxHP", def.MaxHP);
                writer.WriteNumber("protection", def.Protection);

                // Color
                writer.WriteStartObject("color");
                writer.WriteNumber("r", def.Color.R);
                writer.WriteNumber("g", def.Color.G);
                writer.WriteNumber("b", def.Color.B);
                writer.WriteNumber("a", def.Color.A);
                writer.WriteEndObject();

                // Segments
                writer.WriteStartArray("segments");
                foreach (var seg in def.Segments)
                {
                    writer.WriteStartObject();
                    writer.WriteBoolean("enabled", seg.Enabled);
                    writer.WriteString("spritePath", seg.SpritePath);
                    writer.WriteNumber("srcX", seg.SrcRect.X);
                    writer.WriteNumber("srcY", seg.SrcRect.Y);
                    writer.WriteNumber("srcW", seg.SrcRect.Width);
                    writer.WriteNumber("srcH", seg.SrcRect.Height);
                    writer.WriteNumber("offsetX", seg.Offset.X);
                    writer.WriteNumber("offsetY", seg.Offset.Y);
                    writer.WriteNumber("sizeW", seg.Size.X);
                    writer.WriteNumber("sizeH", seg.Size.Y);
                    writer.WriteNumber("pivotX", seg.Pivot.X);
                    writer.WriteNumber("pivotY", seg.Pivot.Y);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();

                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.Flush();

            _statusMessage = "Saved!";
            _statusTimer = 2f;
        }
        catch (Exception ex)
        {
            _statusMessage = $"Error: {ex.Message}";
            _statusTimer = 4f;
        }
    }
}
