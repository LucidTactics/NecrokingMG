using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Necroking.Core;
using Necroking.Data;
using Necroking.Render;
using Necroking.World;
using Necroking.GameSystems;

namespace Necroking.Editor;

public enum MapEditorTab { Ground, Grass, Objects, Walls, Roads, Regions, Triggers }

// ============================================================================
//  Grass type definition (editor-side, no dedicated GrassSystem yet)
// ============================================================================
public class GrassTypeDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public byte BaseR { get; set; } = 46;
    public byte BaseG { get; set; } = 102;
    public byte BaseB { get; set; } = 20;
    public byte TipR { get; set; } = 100;
    public byte TipG { get; set; } = 166;
    public byte TipB { get; set; } = 50;
    public float Density { get; set; } = 1f;
    public float Height { get; set; } = 1f;
    public int Blades { get; set; } = 5;
}

// ============================================================================
//  MapEditorWindow - Full 7-tab map editor
// ============================================================================
public class MapEditorWindow
{
    // ---- Dependencies (set via Init) ----
    private GroundSystem _groundSystem = null!;
    private EnvironmentSystem _envSystem = null!;
    private TriggerSystem _triggerSystem = null!;
    private WallSystem _wallSystem = null!;
    private RoadSystem _roadSystem = null!;
    private TileGrid _tileGrid = null!;
    private Camera25D _camera = null!;
    private SpriteBatch _spriteBatch = null!;
    private Texture2D _pixel = null!;
    private SpriteFont? _font;
    private SpriteFont? _smallFont;
    private GraphicsDevice _device = null!;

    // Callbacks
    private Action? _onVertexMapChanged;
    private Action? _onGrassMapChanged;

    // ---- Grass data (owned by editor, synced back to Game1) ----
    private readonly List<GrassTypeDef> _grassTypes = new();
    private byte[] _grassMap = Array.Empty<byte>();
    private int _grassW, _grassH;

    // ---- Editor state ----
    public MapEditorTab ActiveTab = MapEditorTab.Ground;
    public int BrushRadius = 2;

    // Per-tab scroll offsets
    private readonly float[] _tabScroll = new float[7];

    // Ground tab
    public int SelectedGroundType;

    // Grass tab
    public int SelectedGrassType; // -1 for none, type indices 0-N, 255 = eraser sentinel
    private bool _grassEraserSelected;

    // Objects tab
    public int SelectedEnvDefIndex = -1;
    public int SelectedEnvCategory;
    private bool _objectPaintMode; // false = single, true = paint
    private float _envListScroll;

    // Walls tab
    public int SelectedWallType; // 0 = erase, 1+ = wall def index+1

    // Roads tab
    public int SelectedRoadTexDef = -1;
    public int SelectedRoadIndex = -1;
    public int SelectedRoadPoint = -1;
    public int SelectedJunctionIndex = -1;
    private bool _roadPlaceMode;
    private int _draggingPoint = -1;
    private int _draggingJunction = -1;

    // Regions tab
    public int SelectedRegionIndex = -1;
    public int SelectedPatrolRoute = -1;
    public int SelectedWaypointIndex = -1;
    private bool _regionPlaceWaypoint;
    private int _draggingRegion = -1;
    private bool _draggingRegionResize;

    // Triggers tab
    public int SelectedTriggerDefIndex = -1;
    public int SelectedTriggerInstanceIndex = -1;
    public int SelectedConditionIndex = -1;
    public int SelectedEffectIndex = -1;
    private int _triggerSubSection; // 0=defs, 1=instances

    // Mouse drag painting state
    private bool _painting;
    private MouseState _prevMouse;
    private KeyboardState _prevKb;
    private int _prevScrollValue;

    // Save/Load
    private string _mapFilename = "default";
    private string _statusMessage = "";
    private float _statusTimer;

    // Panel dimensions
    private const int PanelWidth = 320;
    private const int TabRowHeight = 26;
    private const int HeaderHeight = 28;
    private const int LineHeight = 20;
    private const int ButtonHeight = 22;
    private const int Margin = 6;
    private const int FieldHeight = 22;

    // Colors
    private static readonly Color BgColor = new(25, 25, 40, 235);
    private static readonly Color HeaderBg = new(40, 40, 65, 245);
    private static readonly Color SeparatorColor = new(80, 80, 120);
    private static readonly Color TextColor = new(190, 190, 210);
    private static readonly Color TextDim = new(130, 130, 155);
    private static readonly Color TextBright = Color.White;
    private static readonly Color HighlightColor = new(100, 80, 160, 200);
    private static readonly Color TabActiveColor = new(60, 50, 100, 245);
    private static readonly Color TabInactiveColor = new(35, 35, 55, 220);
    private static readonly Color ButtonBg = new(50, 50, 75, 220);
    private static readonly Color ButtonHoverColor = new(70, 65, 110, 240);
    private static readonly Color InputBg = new(15, 15, 25, 230);
    private static readonly Color DangerColor = new(180, 60, 60);
    private static readonly Color SuccessColor = new(60, 180, 80);
    private static readonly Color AccentColor = new(100, 140, 220);
    private static readonly Color BrushCursorFill = new(255, 200, 80, 35);
    private static readonly Color BrushCursorEdge = new(255, 200, 80, 100);
    private static readonly Color RegionRectColor = new(80, 200, 80, 60);
    private static readonly Color RegionRectBorder = new(80, 200, 80, 180);
    private static readonly Color RegionCircleColor = new(80, 80, 200, 60);
    private static readonly Color RegionCircleBorder = new(80, 80, 200, 180);
    private static readonly Color WaypointColor = new(200, 200, 60, 200);
    private static readonly Color RoadPointColor = new(200, 120, 60, 200);
    private static readonly Color JunctionColor = new(60, 200, 200, 200);

    // Tab names and layout
    private static readonly string[] TabRow1 = { "Ground", "Grass", "Objects", "Walls" };
    private static readonly string[] TabRow2 = { "Roads", "Regions", "Triggers" };

    // ========================================================================
    //  Init
    // ========================================================================

    public void Init(
        GroundSystem groundSystem,
        EnvironmentSystem envSystem,
        TriggerSystem triggerSystem,
        Camera25D camera,
        SpriteBatch spriteBatch,
        Texture2D pixel,
        SpriteFont? font,
        SpriteFont? smallFont,
        GraphicsDevice device,
        Action? onVertexMapChanged = null,
        WallSystem? wallSystem = null,
        RoadSystem? roadSystem = null,
        TileGrid? tileGrid = null,
        Action? onGrassMapChanged = null)
    {
        _groundSystem = groundSystem;
        _envSystem = envSystem;
        _triggerSystem = triggerSystem;
        _camera = camera;
        _spriteBatch = spriteBatch;
        _pixel = pixel;
        _font = font;
        _smallFont = smallFont;
        _device = device;
        _onVertexMapChanged = onVertexMapChanged;
        _wallSystem = wallSystem ?? new WallSystem();
        _roadSystem = roadSystem ?? new RoadSystem();
        _tileGrid = tileGrid ?? new TileGrid();
        _onGrassMapChanged = onGrassMapChanged;
    }

    /// <summary>
    /// Set the grass data arrays for the editor to manipulate.
    /// </summary>
    public void SetGrassData(byte[] grassMap, int grassW, int grassH, MapData.GrassTypeInfo[] types)
    {
        _grassMap = grassMap;
        _grassW = grassW;
        _grassH = grassH;
        _grassTypes.Clear();
        foreach (var t in types)
        {
            _grassTypes.Add(new GrassTypeDef
            {
                Id = t.Id, Name = t.Name,
                BaseR = t.BaseR, BaseG = t.BaseG, BaseB = t.BaseB,
                TipR = t.TipR, TipG = t.TipG, TipB = t.TipB
            });
        }
    }

    /// <summary>
    /// Get the current grass map for rendering sync.
    /// </summary>
    public byte[] GetGrassMap() => _grassMap;
    public int GrassW => _grassW;
    public int GrassH => _grassH;
    public IReadOnlyList<GrassTypeDef> GrassTypes => _grassTypes;

    // ========================================================================
    //  IsMouseOverPanel
    // ========================================================================

    public bool IsMouseOverPanel(int screenW, int screenH)
    {
        var mouse = Mouse.GetState();
        int panelX = screenW - PanelWidth - 10;
        int panelY = 10;
        int panelH = screenH - 20;
        return mouse.X >= panelX && mouse.X < panelX + PanelWidth &&
               mouse.Y >= panelY && mouse.Y < panelY + panelH;
    }

    // ========================================================================
    //  Update
    // ========================================================================

    public void Update(int screenW, int screenH)
    {
        var mouse = Mouse.GetState();
        var kb = Keyboard.GetState();
        bool leftClick = mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released;
        bool leftDown = mouse.LeftButton == ButtonState.Pressed;
        bool leftUp = mouse.LeftButton == ButtonState.Released && _prevMouse.LeftButton == ButtonState.Pressed;
        bool rightClick = mouse.RightButton == ButtonState.Pressed && _prevMouse.RightButton == ButtonState.Released;

        int panelX = screenW - PanelWidth - 10;
        int panelY = 10;
        bool overPanel = IsMouseOverPanel(screenW, screenH);

        // Status timer
        if (_statusTimer > 0) _statusTimer -= 1f / 60f;

        // --- Tab switching via keyboard (1-7) ---
        for (int i = 0; i < 7; i++)
        {
            Keys key = Keys.D1 + i;
            if (kb.IsKeyDown(key) && _prevKb.IsKeyUp(key))
                ActiveTab = (MapEditorTab)i;
        }

        // --- Tab switching via click ---
        if (leftClick && overPanel)
        {
            int tabY1 = panelY;
            int tabY2 = panelY + TabRowHeight;
            int tabW1 = PanelWidth / 4;
            int tabW2 = PanelWidth / 3;

            if (mouse.Y >= tabY1 && mouse.Y < tabY1 + TabRowHeight)
            {
                int relX = mouse.X - panelX;
                int idx = relX / tabW1;
                if (idx >= 0 && idx < 4) ActiveTab = (MapEditorTab)idx;
            }
            else if (mouse.Y >= tabY2 && mouse.Y < tabY2 + TabRowHeight)
            {
                int relX = mouse.X - panelX;
                int idx = relX / tabW2;
                if (idx >= 0 && idx < 3) ActiveTab = (MapEditorTab)(idx + 4);
            }
        }

        // --- Brush size via Q/E or [/] ---
        bool canAdjustBrush = ActiveTab == MapEditorTab.Ground || ActiveTab == MapEditorTab.Grass ||
                              ActiveTab == MapEditorTab.Walls || ActiveTab == MapEditorTab.Objects;
        if (canAdjustBrush)
        {
            if ((kb.IsKeyDown(Keys.Q) && _prevKb.IsKeyUp(Keys.Q)) ||
                (kb.IsKeyDown(Keys.OemOpenBrackets) && _prevKb.IsKeyUp(Keys.OemOpenBrackets)))
                BrushRadius = Math.Max(0, BrushRadius - 1);
            if ((kb.IsKeyDown(Keys.E) && _prevKb.IsKeyUp(Keys.E)) ||
                (kb.IsKeyDown(Keys.OemCloseBrackets) && _prevKb.IsKeyUp(Keys.OemCloseBrackets)))
                BrushRadius = Math.Min(20, BrushRadius + 1);
        }

        // --- Scroll per-tab ---
        int scrollDelta = mouse.ScrollWheelValue - _prevScrollValue;
        if (scrollDelta != 0 && overPanel)
        {
            int tabIdx = (int)ActiveTab;
            _tabScroll[tabIdx] = MathF.Max(0, _tabScroll[tabIdx] - scrollDelta * 0.2f);
        }

        // --- Save (Ctrl+S) ---
        if (kb.IsKeyDown(Keys.LeftControl) && kb.IsKeyDown(Keys.S) && _prevKb.IsKeyUp(Keys.S))
            SaveMap();

        // --- Tab-specific update ---
        switch (ActiveTab)
        {
            case MapEditorTab.Ground:
                UpdateGroundTab(mouse, kb, leftClick, leftDown, leftUp, overPanel, panelX, panelY, screenW, screenH);
                break;
            case MapEditorTab.Grass:
                UpdateGrassTab(mouse, kb, leftClick, leftDown, leftUp, overPanel, panelX, panelY, screenW, screenH);
                break;
            case MapEditorTab.Objects:
                UpdateObjectsTab(mouse, kb, leftClick, leftDown, leftUp, rightClick, overPanel, panelX, panelY, screenW, screenH);
                break;
            case MapEditorTab.Walls:
                UpdateWallsTab(mouse, kb, leftClick, leftDown, leftUp, overPanel, panelX, panelY, screenW, screenH);
                break;
            case MapEditorTab.Roads:
                UpdateRoadsTab(mouse, kb, leftClick, leftDown, leftUp, rightClick, overPanel, panelX, panelY, screenW, screenH);
                break;
            case MapEditorTab.Regions:
                UpdateRegionsTab(mouse, kb, leftClick, leftDown, leftUp, rightClick, overPanel, panelX, panelY, screenW, screenH);
                break;
            case MapEditorTab.Triggers:
                UpdateTriggersTab(mouse, kb, leftClick, overPanel, panelX, panelY, screenW, screenH);
                break;
        }

        _prevMouse = mouse;
        _prevKb = kb;
        _prevScrollValue = mouse.ScrollWheelValue;
    }

    // ========================================================================
    //  Draw
    // ========================================================================

    public void Draw(int screenW, int screenH)
    {
        int panelX = screenW - PanelWidth - 10;
        int panelY = 10;
        int panelH = screenH - 20;

        // Panel background
        _spriteBatch.Draw(_pixel, new Rectangle(panelX, panelY, PanelWidth, panelH), BgColor);

        // Tab rows
        DrawTabRows(panelX, panelY);

        // Separator under tabs
        int tabsBottom = panelY + TabRowHeight * 2;
        _spriteBatch.Draw(_pixel, new Rectangle(panelX, tabsBottom, PanelWidth, 1), SeparatorColor);

        // Content area
        int contentY = tabsBottom + 2;
        int contentH = panelH - (tabsBottom - panelY) - 2;

        switch (ActiveTab)
        {
            case MapEditorTab.Ground:
                DrawGroundTab(panelX, contentY, contentH, screenW, screenH);
                break;
            case MapEditorTab.Grass:
                DrawGrassTab(panelX, contentY, contentH, screenW, screenH);
                break;
            case MapEditorTab.Objects:
                DrawObjectsTab(panelX, contentY, contentH, screenW, screenH);
                break;
            case MapEditorTab.Walls:
                DrawWallsTab(panelX, contentY, contentH, screenW, screenH);
                break;
            case MapEditorTab.Roads:
                DrawRoadsTab(panelX, contentY, contentH, screenW, screenH);
                break;
            case MapEditorTab.Regions:
                DrawRegionsTab(panelX, contentY, contentH, screenW, screenH);
                break;
            case MapEditorTab.Triggers:
                DrawTriggersTab(panelX, contentY, contentH);
                break;
        }

        // Status bar at bottom
        if (_statusTimer > 0 && !string.IsNullOrEmpty(_statusMessage))
        {
            int statusY = panelY + panelH - 22;
            _spriteBatch.Draw(_pixel, new Rectangle(panelX, statusY, PanelWidth, 22), new Color(20, 20, 20, 220));
            DrawSmallText(_statusMessage, panelX + Margin, statusY + 3,
                _statusMessage.StartsWith("Saved") ? SuccessColor : DangerColor);
        }
    }

    // ========================================================================
    //  Tab Rows
    // ========================================================================

    private void DrawTabRows(int panelX, int panelY)
    {
        var mouse = Mouse.GetState();

        // Row 1: Ground, Grass, Objects, Walls
        int tabW1 = PanelWidth / 4;
        for (int i = 0; i < 4; i++)
        {
            var tab = (MapEditorTab)i;
            bool active = ActiveTab == tab;
            var bg = active ? TabActiveColor : TabInactiveColor;
            var rect = new Rectangle(panelX + i * tabW1, panelY, tabW1, TabRowHeight);

            bool hovered = IsInRect(mouse, rect);
            if (!active && hovered) bg = ButtonHoverColor;
            _spriteBatch.Draw(_pixel, rect, bg);

            if (active)
                _spriteBatch.Draw(_pixel, new Rectangle(rect.X, rect.Y + TabRowHeight - 2, tabW1, 2), new Color(180, 140, 80));

            DrawTextCentered(TabRow1[i], rect, TextColor);
        }

        // Row 2: Roads, Regions, Triggers
        int tabW2 = PanelWidth / 3;
        int row2Y = panelY + TabRowHeight;
        for (int i = 0; i < 3; i++)
        {
            var tab = (MapEditorTab)(i + 4);
            bool active = ActiveTab == tab;
            var bg = active ? TabActiveColor : TabInactiveColor;
            var rect = new Rectangle(panelX + i * tabW2, row2Y, tabW2, TabRowHeight);

            bool hovered = IsInRect(mouse, rect);
            if (!active && hovered) bg = ButtonHoverColor;
            _spriteBatch.Draw(_pixel, rect, bg);

            if (active)
                _spriteBatch.Draw(_pixel, new Rectangle(rect.X, rect.Y + TabRowHeight - 2, tabW2, 2), new Color(180, 140, 80));

            DrawTextCentered(TabRow2[i], rect, TextColor);
        }
    }

    // ====================================================================
    //  GROUND TAB
    // ====================================================================

    private void UpdateGroundTab(MouseState mouse, KeyboardState kb, bool leftClick, bool leftDown, bool leftUp,
        bool overPanel, int panelX, int panelY, int screenW, int screenH)
    {
        if (leftClick && overPanel)
        {
            int contentY = panelY + TabRowHeight * 2 + HeaderHeight + 6;
            for (int i = 0; i < _groundSystem.TypeCount; i++)
            {
                int btnY = contentY + i * (ButtonHeight + 2) - (int)_tabScroll[0];
                if (mouse.Y >= btnY && mouse.Y < btnY + ButtonHeight &&
                    mouse.X >= panelX + Margin && mouse.X < panelX + PanelWidth - Margin)
                {
                    SelectedGroundType = i;
                    break;
                }
            }

            // Add Type button
            int addY = contentY + _groundSystem.TypeCount * (ButtonHeight + 2) + 4 - (int)_tabScroll[0];
            if (mouse.Y >= addY && mouse.Y < addY + ButtonHeight &&
                mouse.X >= panelX + Margin && mouse.X < panelX + 60 + Margin)
            {
                _groundSystem.AddGroundType(new GroundTypeDef
                {
                    Id = $"type_{_groundSystem.TypeCount}",
                    Name = $"Type {_groundSystem.TypeCount}"
                });
            }
        }

        // Paint on world
        if (leftDown && !overPanel)
        {
            _painting = true;
            PaintGround(mouse, screenW, screenH);
        }
        if (leftUp) _painting = false;
    }

    private void PaintGround(MouseState mouse, int screenW, int screenH)
    {
        Vec2 worldPos = _camera.ScreenToWorld(new Vector2(mouse.X, mouse.Y), screenW, screenH);
        int vx = (int)MathF.Round(worldPos.X);
        int vy = (int)MathF.Round(worldPos.Y);
        byte typeIdx = (byte)SelectedGroundType;

        bool changed = false;
        for (int dy = -BrushRadius; dy <= BrushRadius; dy++)
        {
            for (int dx = -BrushRadius; dx <= BrushRadius; dx++)
            {
                if (dx * dx + dy * dy > BrushRadius * BrushRadius) continue;
                int cx = vx + dx;
                int cy = vy + dy;
                if (cx >= 0 && cx < _groundSystem.VertexW && cy >= 0 && cy < _groundSystem.VertexH)
                {
                    if (_groundSystem.GetVertex(cx, cy) != typeIdx)
                    {
                        _groundSystem.SetVertex(cx, cy, typeIdx);
                        changed = true;
                    }
                }
            }
        }
        if (changed) _onVertexMapChanged?.Invoke();
    }

    private void DrawGroundTab(int panelX, int contentY, int contentH, int screenW, int screenH)
    {
        DrawSectionHeader(panelX, ref contentY, $"Ground Types ({_groundSystem.TypeCount})");

        var mouse = Mouse.GetState();
        float scroll = _tabScroll[0];
        int startY = contentY;

        for (int i = 0; i < _groundSystem.TypeCount; i++)
        {
            int y = contentY + i * (ButtonHeight + 2) - (int)scroll;
            if (y < startY - ButtonHeight || y > startY + contentH) continue;

            var def = _groundSystem.GetTypeDef(i);
            bool selected = i == SelectedGroundType;
            var btnRect = new Rectangle(panelX + Margin, y, PanelWidth - Margin * 2, ButtonHeight);

            var bgColor = selected ? HighlightColor : (IsInRect(mouse, btnRect) ? ButtonHoverColor : Color.Transparent);
            if (bgColor != Color.Transparent)
                _spriteBatch.Draw(_pixel, btnRect, bgColor);

            string prefix = selected ? "[*] " : "[ ] ";
            DrawSmallText(prefix + def.Name, panelX + Margin + 4, y + 3, TextColor);
        }

        // Add Type button
        int addY = contentY + _groundSystem.TypeCount * (ButtonHeight + 2) + 4 - (int)scroll;
        DrawButtonRect("+ Add Type", panelX + Margin, addY, 80, ButtonHeight, ButtonBg);

        // Brush size
        addY += ButtonHeight + 10;
        DrawBrushSizeControl(panelX, addY);

        // Info
        addY += ButtonHeight + 8;
        _spriteBatch.Draw(_pixel, new Rectangle(panelX, addY, PanelWidth, 1), SeparatorColor);
        addY += 4;
        DrawSmallText($"World: {_groundSystem.WorldW}x{_groundSystem.WorldH}", panelX + Margin, addY, TextDim);
        addY += LineHeight;
        DrawSmallText($"Vertices: {_groundSystem.VertexW}x{_groundSystem.VertexH}", panelX + Margin, addY, TextDim);
        addY += LineHeight;
        DrawSmallText("Left-drag to paint | Q/E brush size", panelX + Margin, addY, TextDim);

        // Brush cursor
        if (!IsMouseOverPanel(screenW, screenH))
            DrawBrushCursor(screenW, screenH);
    }

    // ====================================================================
    //  GRASS TAB
    // ====================================================================

    private void UpdateGrassTab(MouseState mouse, KeyboardState kb, bool leftClick, bool leftDown, bool leftUp,
        bool overPanel, int panelX, int panelY, int screenW, int screenH)
    {
        if (leftClick && overPanel)
        {
            int contentY = panelY + TabRowHeight * 2 + HeaderHeight + 6;

            // Eraser button
            int eraserY = contentY - (int)_tabScroll[1];
            if (mouse.Y >= eraserY && mouse.Y < eraserY + ButtonHeight &&
                mouse.X >= panelX + Margin && mouse.X < panelX + PanelWidth - Margin)
            {
                _grassEraserSelected = true;
                SelectedGrassType = -1;
            }

            // Type list (starts after eraser)
            int listY = eraserY + ButtonHeight + 4;
            for (int i = 0; i < _grassTypes.Count; i++)
            {
                int btnY = listY + i * (ButtonHeight + 2);
                if (mouse.Y >= btnY && mouse.Y < btnY + ButtonHeight &&
                    mouse.X >= panelX + Margin && mouse.X < panelX + PanelWidth - Margin)
                {
                    SelectedGrassType = i;
                    _grassEraserSelected = false;
                    break;
                }
            }

            // Add Type button
            int addY = listY + _grassTypes.Count * (ButtonHeight + 2) + 4;
            if (mouse.Y >= addY && mouse.Y < addY + ButtonHeight &&
                mouse.X >= panelX + Margin && mouse.X < panelX + 60 + Margin)
            {
                _grassTypes.Add(new GrassTypeDef
                {
                    Id = $"grass_{_grassTypes.Count}",
                    Name = $"Grass {_grassTypes.Count}"
                });
            }
        }

        // Paint grass
        if (leftDown && !overPanel && _grassMap.Length > 0)
        {
            _painting = true;
            PaintGrass(mouse, screenW, screenH);
        }
        if (leftUp) _painting = false;
    }

    private void PaintGrass(MouseState mouse, int screenW, int screenH)
    {
        if (_grassW == 0 || _grassH == 0) return;

        Vec2 worldPos = _camera.ScreenToWorld(new Vector2(mouse.X, mouse.Y), screenW, screenH);
        // Grass cells are 1:1 with world coords (cellSize = 1.0)
        int cx = (int)MathF.Floor(worldPos.X);
        int cy = (int)MathF.Floor(worldPos.Y);

        byte paintValue;
        if (_grassEraserSelected)
            paintValue = 255;
        else if (SelectedGrassType >= 0 && SelectedGrassType < _grassTypes.Count)
            paintValue = (byte)(SelectedGrassType + 1); // type index is 1-based in cell grid (0 = no grass, 255 = erased)
        else
            return;

        bool changed = false;
        for (int dy = -BrushRadius; dy <= BrushRadius; dy++)
        {
            for (int dx = -BrushRadius; dx <= BrushRadius; dx++)
            {
                if (dx * dx + dy * dy > BrushRadius * BrushRadius) continue;
                int gx = cx + dx;
                int gy = cy + dy;
                if (gx >= 0 && gx < _grassW && gy >= 0 && gy < _grassH)
                {
                    int idx = gy * _grassW + gx;
                    if (_grassMap[idx] != paintValue)
                    {
                        _grassMap[idx] = paintValue;
                        changed = true;
                    }
                }
            }
        }
        if (changed) _onGrassMapChanged?.Invoke();
    }

    private void DrawGrassTab(int panelX, int contentY, int contentH, int screenW, int screenH)
    {
        DrawSectionHeader(panelX, ref contentY, $"Grass Types ({_grassTypes.Count})");

        var mouse = Mouse.GetState();
        float scroll = _tabScroll[1];
        int y = contentY - (int)scroll;

        // Eraser entry
        {
            var btnRect = new Rectangle(panelX + Margin, y, PanelWidth - Margin * 2, ButtonHeight);
            var bg = _grassEraserSelected ? HighlightColor : (IsInRect(mouse, btnRect) ? ButtonHoverColor : Color.Transparent);
            if (bg != Color.Transparent)
                _spriteBatch.Draw(_pixel, btnRect, bg);
            // X swatch for eraser
            _spriteBatch.Draw(_pixel, new Rectangle(panelX + Margin + 4, y + 4, 14, 14), new Color(180, 60, 60));
            DrawSmallText("Eraser (255)", panelX + Margin + 24, y + 3, TextColor);
        }
        y += ButtonHeight + 4;

        // Type list
        for (int i = 0; i < _grassTypes.Count; i++)
        {
            if (y < contentY - ButtonHeight || y > contentY + contentH) { y += ButtonHeight + 2; continue; }

            var gt = _grassTypes[i];
            bool selected = !_grassEraserSelected && i == SelectedGrassType;
            var btnRect = new Rectangle(panelX + Margin, y, PanelWidth - Margin * 2, ButtonHeight);

            var bg = selected ? HighlightColor : (IsInRect(mouse, btnRect) ? ButtonHoverColor : Color.Transparent);
            if (bg != Color.Transparent)
                _spriteBatch.Draw(_pixel, btnRect, bg);

            // Base color swatch
            var baseColor = new Color(gt.BaseR, gt.BaseG, gt.BaseB);
            _spriteBatch.Draw(_pixel, new Rectangle(panelX + Margin + 4, y + 4, 14, 14), baseColor);

            // Tip color swatch
            var tipColor = new Color(gt.TipR, gt.TipG, gt.TipB);
            _spriteBatch.Draw(_pixel, new Rectangle(panelX + Margin + 22, y + 4, 14, 14), tipColor);

            DrawSmallText(gt.Name, panelX + Margin + 42, y + 3, TextColor);
            y += ButtonHeight + 2;
        }

        // Add button
        DrawButtonRect("+ Add Type", panelX + Margin, y, 80, ButtonHeight, ButtonBg);
        y += ButtonHeight + 8;

        // Selected type properties
        if (!_grassEraserSelected && SelectedGrassType >= 0 && SelectedGrassType < _grassTypes.Count)
        {
            _spriteBatch.Draw(_pixel, new Rectangle(panelX, y, PanelWidth, 1), SeparatorColor);
            y += 4;
            var gt = _grassTypes[SelectedGrassType];

            DrawSmallText($"Name: {gt.Name}", panelX + Margin, y, TextBright); y += LineHeight;
            DrawSmallText($"Base: R={gt.BaseR} G={gt.BaseG} B={gt.BaseB}", panelX + Margin, y, TextColor); y += LineHeight;
            DrawSmallText($"Tip:  R={gt.TipR} G={gt.TipG} B={gt.TipB}", panelX + Margin, y, TextColor); y += LineHeight;
            DrawSmallText($"Density: {gt.Density:F1}  Height: {gt.Height:F1}", panelX + Margin, y, TextColor); y += LineHeight;
            DrawSmallText($"Blades: {gt.Blades}", panelX + Margin, y, TextColor); y += LineHeight;
        }

        // Brush size
        y += 4;
        DrawBrushSizeControl(panelX, y);
        y += ButtonHeight + 4;
        DrawSmallText($"Grass map: {_grassW}x{_grassH}", panelX + Margin, y, TextDim);

        // Brush cursor
        if (!IsMouseOverPanel(screenW, screenH))
            DrawBrushCursor(screenW, screenH);
    }

    // ====================================================================
    //  OBJECTS TAB
    // ====================================================================

    private void UpdateObjectsTab(MouseState mouse, KeyboardState kb, bool leftClick, bool leftDown, bool leftUp,
        bool rightClick, bool overPanel, int panelX, int panelY, int screenW, int screenH)
    {
        if (leftClick && overPanel)
        {
            int contentY = panelY + TabRowHeight * 2 + HeaderHeight + 6;

            // Category buttons
            var categories = GetEnvCategories();
            int catBtnW = Math.Max(40, (PanelWidth - Margin * 2) / Math.Max(1, categories.Count));
            if (mouse.Y >= contentY && mouse.Y < contentY + ButtonHeight)
            {
                int relX = mouse.X - (panelX + Margin);
                int catIdx = relX / catBtnW;
                if (catIdx >= 0 && catIdx < categories.Count)
                    SelectedEnvCategory = catIdx;
            }

            // Mode toggle
            int modeY = contentY + ButtonHeight + 4;
            int halfW = (PanelWidth - Margin * 2) / 2;
            if (mouse.Y >= modeY && mouse.Y < modeY + ButtonHeight)
            {
                int relX = mouse.X - (panelX + Margin);
                if (relX >= 0 && relX < halfW) _objectPaintMode = false;
                else if (relX >= halfW && relX < halfW * 2) _objectPaintMode = true;
            }

            // Def list
            int listY = modeY + ButtonHeight + 4;
            var filteredDefs = GetFilteredEnvDefs(categories);
            for (int i = 0; i < filteredDefs.Count; i++)
            {
                int itemY = listY + i * (ButtonHeight + 2) - (int)_envListScroll;
                if (mouse.Y >= itemY && mouse.Y < itemY + ButtonHeight)
                {
                    SelectedEnvDefIndex = filteredDefs[i];
                    break;
                }
            }
        }

        // Scroll env list
        int scrollDelta = mouse.ScrollWheelValue - _prevScrollValue;
        if (scrollDelta != 0 && overPanel)
            _envListScroll = MathF.Max(0, _envListScroll - scrollDelta * 0.2f);

        // Place/paint on world
        if (SelectedEnvDefIndex >= 0)
        {
            if (!_objectPaintMode)
            {
                // Single mode: click to place
                if (leftClick && !overPanel)
                {
                    Vec2 worldPos = _camera.ScreenToWorld(new Vector2(mouse.X, mouse.Y), screenW, screenH);
                    _envSystem.AddObject((ushort)SelectedEnvDefIndex, worldPos.X, worldPos.Y);
                }
            }
            else
            {
                // Paint mode: drag to paint hex-grid pattern
                if (leftDown && !overPanel)
                {
                    PaintObjects(mouse, screenW, screenH);
                }
            }
        }

        // Right-click to remove nearest
        if (rightClick && !overPanel)
        {
            Vec2 worldPos = _camera.ScreenToWorld(new Vector2(mouse.X, mouse.Y), screenW, screenH);
            int closest = FindClosestObject(worldPos, 3f);
            if (closest >= 0) _envSystem.RemoveObject(closest);
        }
    }

    private void PaintObjects(MouseState mouse, int screenW, int screenH)
    {
        if (SelectedEnvDefIndex < 0) return;
        Vec2 worldPos = _camera.ScreenToWorld(new Vector2(mouse.X, mouse.Y), screenW, screenH);
        var def = _envSystem.GetDef(SelectedEnvDefIndex);
        float spacing = Math.Max(1f, def.PlacementScale * 2f);

        for (int dy = -BrushRadius; dy <= BrushRadius; dy++)
        {
            for (int dx = -BrushRadius; dx <= BrushRadius; dx++)
            {
                if (dx * dx + dy * dy > BrushRadius * BrushRadius) continue;
                // Hex-grid offset
                float ox = dx * spacing + (dy % 2 == 0 ? 0 : spacing * 0.5f);
                float oy = dy * spacing * 0.866f; // sqrt(3)/2

                float px = worldPos.X + ox;
                float py = worldPos.Y + oy;

                // Check no existing object too close
                bool tooClose = false;
                float minDist2 = spacing * spacing * 0.5f;
                for (int i = 0; i < _envSystem.ObjectCount; i++)
                {
                    var obj = _envSystem.GetObject(i);
                    float ddx = obj.X - px, ddy = obj.Y - py;
                    if (ddx * ddx + ddy * ddy < minDist2) { tooClose = true; break; }
                }
                if (!tooClose)
                    _envSystem.AddObject((ushort)SelectedEnvDefIndex, px, py);
            }
        }
    }

    private void DrawObjectsTab(int panelX, int contentY, int contentH, int screenW, int screenH)
    {
        var categories = GetEnvCategories();
        DrawSectionHeader(panelX, ref contentY, $"Objects ({_envSystem.DefCount} defs, {_envSystem.ObjectCount} placed)");

        var mouse = Mouse.GetState();

        // Category buttons
        int catBtnW = Math.Max(40, (PanelWidth - Margin * 2) / Math.Max(1, categories.Count));
        for (int i = 0; i < categories.Count; i++)
        {
            bool active = i == SelectedEnvCategory;
            var btnRect = new Rectangle(panelX + Margin + i * catBtnW, contentY, catBtnW - 2, ButtonHeight);
            var bg = active ? TabActiveColor : (IsInRect(mouse, btnRect) ? ButtonHoverColor : TabInactiveColor);
            _spriteBatch.Draw(_pixel, btnRect, bg);
            DrawTextCentered(categories[i], btnRect, TextColor);
        }
        contentY += ButtonHeight + 4;

        // Mode toggle: Single | Paint
        int halfW = (PanelWidth - Margin * 2) / 2;
        {
            var singleRect = new Rectangle(panelX + Margin, contentY, halfW - 1, ButtonHeight);
            var paintRect = new Rectangle(panelX + Margin + halfW, contentY, halfW - 1, ButtonHeight);
            _spriteBatch.Draw(_pixel, singleRect, !_objectPaintMode ? TabActiveColor : TabInactiveColor);
            _spriteBatch.Draw(_pixel, paintRect, _objectPaintMode ? TabActiveColor : TabInactiveColor);
            DrawTextCentered("Single", singleRect, TextColor);
            DrawTextCentered("Paint", paintRect, TextColor);
        }
        contentY += ButtonHeight + 4;

        // Separator
        _spriteBatch.Draw(_pixel, new Rectangle(panelX, contentY - 2, PanelWidth, 1), SeparatorColor);

        // Def list
        var filteredDefs = GetFilteredEnvDefs(categories);
        int listAreaH = contentH - (contentY - (panelX + 10)) - 160; // reserve space for properties

        for (int i = 0; i < filteredDefs.Count; i++)
        {
            int itemY = contentY + i * (ButtonHeight + 2) - (int)_envListScroll;
            if (itemY < contentY - ButtonHeight || itemY > contentY + listAreaH) continue;

            int defIdx = filteredDefs[i];
            var def = _envSystem.GetDef(defIdx);
            bool selected = defIdx == SelectedEnvDefIndex;
            var btnRect = new Rectangle(panelX + Margin, itemY, PanelWidth - Margin * 2, ButtonHeight);

            var bgColor = selected ? HighlightColor : (IsInRect(mouse, btnRect) ? ButtonHoverColor : Color.Transparent);
            if (bgColor != Color.Transparent)
                _spriteBatch.Draw(_pixel, btnRect, bgColor);

            string label = def.Name;
            if (def.IsBuilding) label += " [B]";
            DrawSmallText(label, panelX + Margin + 4, itemY + 3, selected ? TextBright : TextColor);
        }

        // Selected def properties
        if (SelectedEnvDefIndex >= 0 && SelectedEnvDefIndex < _envSystem.DefCount)
        {
            int propY = contentY + contentH - 140;
            _spriteBatch.Draw(_pixel, new Rectangle(panelX, propY - 4, PanelWidth, 1), SeparatorColor);
            propY += 2;

            var selDef = _envSystem.GetDef(SelectedEnvDefIndex);
            DrawSmallText($"Selected: {selDef.Name}", panelX + Margin, propY, TextBright); propY += LineHeight;
            DrawSmallText($"Category: {selDef.Category}", panelX + Margin, propY, TextColor); propY += LineHeight;
            DrawSmallText($"Collision: r={selDef.CollisionRadius:F1}", panelX + Margin, propY, TextColor); propY += LineHeight;
            DrawSmallText($"Scale: {selDef.Scale:F2} Height: {selDef.SpriteWorldHeight:F1}", panelX + Margin, propY, TextColor); propY += LineHeight;
            if (selDef.IsBuilding)
                DrawSmallText($"Building: HP={selDef.BuildingMaxHP} Prot={selDef.BuildingProtection}", panelX + Margin, propY, SuccessColor);
            else
                DrawSmallText(_objectPaintMode ? "Left-drag to paint objects" : "Click to place, right-click remove", panelX + Margin, propY, TextDim);
        }

        // Brush size (for paint mode)
        if (_objectPaintMode)
        {
            int brushY = contentY + contentH - 22;
            DrawBrushSizeControl(panelX, brushY);
        }

        // Brush cursor for paint mode
        if (_objectPaintMode && !IsMouseOverPanel(screenW, screenH))
            DrawBrushCursor(screenW, screenH);
    }

    // ====================================================================
    //  WALLS TAB
    // ====================================================================

    private void UpdateWallsTab(MouseState mouse, KeyboardState kb, bool leftClick, bool leftDown, bool leftUp,
        bool overPanel, int panelX, int panelY, int screenW, int screenH)
    {
        if (leftClick && overPanel)
        {
            int contentY = panelY + TabRowHeight * 2 + HeaderHeight + 6;

            // Erase entry
            int y = contentY - (int)_tabScroll[3];
            if (mouse.Y >= y && mouse.Y < y + ButtonHeight &&
                mouse.X >= panelX + Margin && mouse.X < panelX + PanelWidth - Margin)
            {
                SelectedWallType = 0;
            }
            y += ButtonHeight + 4;

            // Wall def list
            for (int i = 0; i < _wallSystem.DefCount; i++)
            {
                int btnY = y + i * (ButtonHeight + 2);
                if (mouse.Y >= btnY && mouse.Y < btnY + ButtonHeight &&
                    mouse.X >= panelX + Margin && mouse.X < panelX + PanelWidth - Margin)
                {
                    SelectedWallType = i + 1; // 1-based
                    break;
                }
            }
        }

        // Paint walls
        if (leftDown && !overPanel)
        {
            _painting = true;
            PaintWalls(mouse, screenW, screenH);
        }
        if (leftUp)
        {
            if (_painting)
            {
                // Rebuild cost field and bake walls after painting
                _tileGrid.RebuildCostField();
                _wallSystem.BakeWalls(_tileGrid);
            }
            _painting = false;
        }
    }

    private void PaintWalls(MouseState mouse, int screenW, int screenH)
    {
        Vec2 worldPos = _camera.ScreenToWorld(new Vector2(mouse.X, mouse.Y), screenW, screenH);
        int wx = WallSystem.SnapToWallGrid((int)MathF.Round(worldPos.X));
        int wy = WallSystem.SnapToWallGrid((int)MathF.Round(worldPos.Y));

        for (int dy = -BrushRadius; dy <= BrushRadius; dy++)
        {
            for (int dx = -BrushRadius; dx <= BrushRadius; dx++)
            {
                if (dx * dx + dy * dy > BrushRadius * BrushRadius) continue;
                int tx = wx + dx * WallSystem.WallStep;
                int ty = wy + dy * WallSystem.WallStep;
                if (!_wallSystem.InBounds(tx, ty)) continue;

                if (SelectedWallType == 0)
                    _wallSystem.ClearWall(tx, ty);
                else
                    _wallSystem.SetWall(tx, ty, (byte)SelectedWallType);
            }
        }
    }

    private void DrawWallsTab(int panelX, int contentY, int contentH, int screenW, int screenH)
    {
        DrawSectionHeader(panelX, ref contentY, $"Walls ({_wallSystem.DefCount} types)");

        var mouse = Mouse.GetState();
        float scroll = _tabScroll[3];
        int y = contentY - (int)scroll;

        // Erase entry
        {
            bool selected = SelectedWallType == 0;
            var btnRect = new Rectangle(panelX + Margin, y, PanelWidth - Margin * 2, ButtonHeight);
            var bg = selected ? HighlightColor : (IsInRect(mouse, btnRect) ? ButtonHoverColor : Color.Transparent);
            if (bg != Color.Transparent) _spriteBatch.Draw(_pixel, btnRect, bg);
            _spriteBatch.Draw(_pixel, new Rectangle(panelX + Margin + 4, y + 4, 14, 14), DangerColor);
            DrawSmallText("Erase Walls", panelX + Margin + 24, y + 3, TextColor);
        }
        y += ButtonHeight + 4;

        // Wall def list
        for (int i = 0; i < _wallSystem.DefCount; i++)
        {
            if (y < contentY - ButtonHeight || y > contentY + contentH) { y += ButtonHeight + 2; continue; }

            var def = _wallSystem.Defs[i];
            bool selected = SelectedWallType == i + 1;
            var btnRect = new Rectangle(panelX + Margin, y, PanelWidth - Margin * 2, ButtonHeight);

            var bg = selected ? HighlightColor : (IsInRect(mouse, btnRect) ? ButtonHoverColor : Color.Transparent);
            if (bg != Color.Transparent) _spriteBatch.Draw(_pixel, btnRect, bg);

            // Color swatch
            _spriteBatch.Draw(_pixel, new Rectangle(panelX + Margin + 4, y + 4, 14, 14), def.Color);
            DrawSmallText($"{def.Name} (HP:{def.MaxHP})", panelX + Margin + 24, y + 3, TextColor);
            y += ButtonHeight + 2;
        }

        // Brush size
        y += 8;
        DrawBrushSizeControl(panelX, y);
        y += ButtonHeight + 4;

        // Info
        DrawSmallText($"Grid: {_wallSystem.Width}x{_wallSystem.Height} step={WallSystem.WallStep}", panelX + Margin, y, TextDim);
        y += LineHeight;
        DrawSmallText("Left-drag to paint walls", panelX + Margin, y, TextDim);

        // Brush cursor
        if (!IsMouseOverPanel(screenW, screenH))
            DrawBrushCursor(screenW, screenH);
    }

    // ====================================================================
    //  ROADS TAB
    // ====================================================================

    private void UpdateRoadsTab(MouseState mouse, KeyboardState kb, bool leftClick, bool leftDown, bool leftUp,
        bool rightClick, bool overPanel, int panelX, int panelY, int screenW, int screenH)
    {
        if (leftClick && overPanel)
        {
            int contentY = panelY + TabRowHeight * 2 + HeaderHeight + 6;
            float scroll = _tabScroll[4];

            // Road list
            int y = contentY - (int)scroll;
            for (int i = 0; i < _roadSystem.RoadCount; i++)
            {
                int btnY = y + i * (ButtonHeight + 2);
                if (mouse.Y >= btnY && mouse.Y < btnY + ButtonHeight)
                {
                    SelectedRoadIndex = i;
                    SelectedRoadPoint = -1;
                    break;
                }
            }

            // Add Road button
            int addY = y + _roadSystem.RoadCount * (ButtonHeight + 2) + 4;
            if (mouse.Y >= addY && mouse.Y < addY + ButtonHeight &&
                mouse.X >= panelX + Margin && mouse.X < panelX + 80 + Margin)
            {
                SelectedRoadIndex = _roadSystem.AddRoad();
            }

            // Delete Road button
            int delY = addY;
            if (mouse.Y >= delY && mouse.Y < delY + ButtonHeight &&
                mouse.X >= panelX + 90 + Margin && mouse.X < panelX + 160 + Margin &&
                SelectedRoadIndex >= 0)
            {
                _roadSystem.RemoveRoad(SelectedRoadIndex);
                SelectedRoadIndex = Math.Min(SelectedRoadIndex, _roadSystem.RoadCount - 1);
            }

            // Place Mode toggle
            int modeY = addY + ButtonHeight + 4;
            if (mouse.Y >= modeY && mouse.Y < modeY + ButtonHeight)
            {
                _roadPlaceMode = !_roadPlaceMode;
            }

            // Junction section
            int juncY = modeY + ButtonHeight + 4;
            // Add Junction
            if (mouse.Y >= juncY && mouse.Y < juncY + ButtonHeight &&
                mouse.X >= panelX + Margin && mouse.X < panelX + 100 + Margin)
            {
                Vec2 camPos = _camera.Position;
                SelectedJunctionIndex = _roadSystem.AddJunction(camPos);
            }
        }

        // World interaction
        if (!overPanel)
        {
            Vec2 worldPos = _camera.ScreenToWorld(new Vector2(mouse.X, mouse.Y), screenW, screenH);

            // Place mode: click to add control points
            if (_roadPlaceMode && leftClick && SelectedRoadIndex >= 0)
            {
                var road = _roadSystem.GetRoad(SelectedRoadIndex);
                road.Points.Add(new RoadControlPoint { Position = worldPos, Width = 2f });
            }

            // Drag control points
            if (leftClick && !_roadPlaceMode && SelectedRoadIndex >= 0)
            {
                var road = _roadSystem.GetRoad(SelectedRoadIndex);
                float bestDist = 2f;
                _draggingPoint = -1;
                for (int i = 0; i < road.Points.Count; i++)
                {
                    float d = (road.Points[i].Position - worldPos).Length();
                    if (d < bestDist) { bestDist = d; _draggingPoint = i; }
                }
            }
            if (leftDown && _draggingPoint >= 0 && SelectedRoadIndex >= 0)
            {
                var road = _roadSystem.GetRoad(SelectedRoadIndex);
                if (_draggingPoint < road.Points.Count)
                    road.Points[_draggingPoint] = new RoadControlPoint
                    {
                        Position = worldPos,
                        Width = road.Points[_draggingPoint].Width
                    };
            }
            if (leftUp) _draggingPoint = -1;

            // Drag junctions
            if (leftClick && !_roadPlaceMode && SelectedRoadIndex < 0)
            {
                float bestDist = 3f;
                _draggingJunction = -1;
                for (int i = 0; i < _roadSystem.JunctionCount; i++)
                {
                    float d = (_roadSystem.GetJunction(i).Position - worldPos).Length();
                    if (d < bestDist) { bestDist = d; _draggingJunction = i; SelectedJunctionIndex = i; }
                }
            }
            if (leftDown && _draggingJunction >= 0)
            {
                var junc = _roadSystem.GetJunction(_draggingJunction);
                junc.Position = worldPos;
            }
            if (leftUp) _draggingJunction = -1;

            // Right-click to delete point
            if (rightClick && SelectedRoadIndex >= 0)
            {
                var road = _roadSystem.GetRoad(SelectedRoadIndex);
                float bestDist = 2f;
                int toRemove = -1;
                for (int i = 0; i < road.Points.Count; i++)
                {
                    float d = (road.Points[i].Position - worldPos).Length();
                    if (d < bestDist) { bestDist = d; toRemove = i; }
                }
                if (toRemove >= 0) road.Points.RemoveAt(toRemove);
            }
        }
    }

    private void DrawRoadsTab(int panelX, int contentY, int contentH, int screenW, int screenH)
    {
        DrawSectionHeader(panelX, ref contentY, $"Roads ({_roadSystem.RoadCount} roads, {_roadSystem.JunctionCount} junctions)");

        var mouse = Mouse.GetState();
        float scroll = _tabScroll[4];
        int y = contentY - (int)scroll;

        // Road list
        for (int i = 0; i < _roadSystem.RoadCount; i++)
        {
            if (y < contentY - ButtonHeight) { y += ButtonHeight + 2; continue; }
            if (y > contentY + contentH - 200) break;

            var road = _roadSystem.GetRoad(i);
            bool selected = i == SelectedRoadIndex;
            var btnRect = new Rectangle(panelX + Margin, y, PanelWidth - Margin * 2, ButtonHeight);

            var bg = selected ? HighlightColor : (IsInRect(mouse, btnRect) ? ButtonHoverColor : Color.Transparent);
            if (bg != Color.Transparent) _spriteBatch.Draw(_pixel, btnRect, bg);

            string label = string.IsNullOrEmpty(road.Name) ? road.Id : road.Name;
            DrawSmallText($"{label} ({road.Points.Count} pts)", panelX + Margin + 4, y + 3,
                selected ? TextBright : TextColor);
            y += ButtonHeight + 2;
        }

        // Add / Delete buttons
        DrawButtonRect("+ Add", panelX + Margin, y, 75, ButtonHeight, ButtonBg);
        DrawButtonRect("Delete", panelX + Margin + 85, y, 75, ButtonHeight, SelectedRoadIndex >= 0 ? DangerColor : ButtonBg);
        y += ButtonHeight + 4;

        // Place mode toggle
        DrawButtonRect(_roadPlaceMode ? "[Place Mode ON]" : "[Place Mode OFF]", panelX + Margin, y, PanelWidth - Margin * 2, ButtonHeight,
            _roadPlaceMode ? AccentColor : ButtonBg);
        y += ButtonHeight + 8;

        // Selected road properties
        if (SelectedRoadIndex >= 0 && SelectedRoadIndex < _roadSystem.RoadCount)
        {
            _spriteBatch.Draw(_pixel, new Rectangle(panelX, y, PanelWidth, 1), SeparatorColor);
            y += 4;
            var road = _roadSystem.GetRoad(SelectedRoadIndex);

            DrawSmallText($"Name: {road.Name}", panelX + Margin, y, TextBright); y += LineHeight;
            DrawSmallText($"Texture: {road.TextureDefIndex}", panelX + Margin, y, TextColor); y += LineHeight;
            DrawSmallText($"Order: {road.RenderOrder}", panelX + Margin, y, TextColor); y += LineHeight;
            DrawSmallText($"EdgeSoftness: {road.EdgeSoftness:F2}", panelX + Margin, y, TextColor); y += LineHeight;
            DrawSmallText($"TexScale: {road.TextureScale:F2}", panelX + Margin, y, TextColor); y += LineHeight;
            DrawSmallText($"Rim: w={road.RimWidth:F1} tex={road.RimTextureDefIndex}", panelX + Margin, y, TextColor); y += LineHeight;
            DrawSmallText($"Points: {road.Points.Count}", panelX + Margin, y, TextColor); y += LineHeight;
        }

        // Junctions header
        y += 4;
        _spriteBatch.Draw(_pixel, new Rectangle(panelX, y, PanelWidth, 1), SeparatorColor);
        y += 4;
        DrawSmallText("Junctions", panelX + Margin, y, AccentColor); y += LineHeight;
        DrawButtonRect("+ Add Junction", panelX + Margin, y, 120, ButtonHeight, ButtonBg);
        y += ButtonHeight + 4;

        for (int i = 0; i < _roadSystem.JunctionCount; i++)
        {
            if (y > contentY + contentH) break;
            var junc = _roadSystem.GetJunction(i);
            bool selected = i == SelectedJunctionIndex;
            DrawSmallText($"{(selected ? "> " : "  ")}{junc.Name} ({junc.Position.X:F0},{junc.Position.Y:F0}) r={junc.Radius:F1}",
                panelX + Margin, y, selected ? TextBright : TextColor);
            y += LineHeight;
        }

        // Draw road control points and junctions on world
        DrawRoadOverlays(screenW, screenH);
    }

    private void DrawRoadOverlays(int screenW, int screenH)
    {
        // Draw control points for selected road
        if (SelectedRoadIndex >= 0 && SelectedRoadIndex < _roadSystem.RoadCount)
        {
            var road = _roadSystem.GetRoad(SelectedRoadIndex);
            for (int i = 0; i < road.Points.Count; i++)
            {
                var sp = _camera.WorldToScreen(road.Points[i].Position, 0, screenW, screenH);
                int sz = i == SelectedRoadPoint ? 8 : 6;
                _spriteBatch.Draw(_pixel, new Rectangle((int)sp.X - sz / 2, (int)sp.Y - sz / 2, sz, sz), RoadPointColor);

                // Line between points
                if (i > 0)
                {
                    var prev = _camera.WorldToScreen(road.Points[i - 1].Position, 0, screenW, screenH);
                    DrawLine(prev, sp, RoadPointColor);
                }
            }
        }

        // Draw junctions
        for (int i = 0; i < _roadSystem.JunctionCount; i++)
        {
            var junc = _roadSystem.GetJunction(i);
            var sp = _camera.WorldToScreen(junc.Position, 0, screenW, screenH);
            int sz = i == SelectedJunctionIndex ? 10 : 7;
            _spriteBatch.Draw(_pixel, new Rectangle((int)sp.X - sz / 2, (int)sp.Y - sz / 2, sz, sz), JunctionColor);
        }
    }

    // ====================================================================
    //  REGIONS TAB
    // ====================================================================

    private void UpdateRegionsTab(MouseState mouse, KeyboardState kb, bool leftClick, bool leftDown, bool leftUp,
        bool rightClick, bool overPanel, int panelX, int panelY, int screenW, int screenH)
    {
        if (leftClick && overPanel)
        {
            int contentY = panelY + TabRowHeight * 2 + HeaderHeight + 6;
            float scroll = _tabScroll[5];
            int y = contentY - (int)scroll;

            // Region list
            var regions = _triggerSystem.Regions;
            for (int i = 0; i < regions.Count; i++)
            {
                int btnY = y + i * (ButtonHeight + 2);
                if (mouse.Y >= btnY && mouse.Y < btnY + ButtonHeight)
                {
                    SelectedRegionIndex = i;
                    break;
                }
            }

            // Add Region button
            int addY = y + regions.Count * (ButtonHeight + 2) + 4;
            if (mouse.Y >= addY && mouse.Y < addY + ButtonHeight &&
                mouse.X >= panelX + Margin && mouse.X < panelX + 80 + Margin)
            {
                var newRegion = new TriggerRegion
                {
                    Id = $"region_{regions.Count}",
                    Name = $"Region {regions.Count}",
                    X = _camera.Position.X,
                    Y = _camera.Position.Y
                };
                SelectedRegionIndex = _triggerSystem.AddRegion(newRegion);
            }

            // Delete Region button
            if (mouse.Y >= addY && mouse.Y < addY + ButtonHeight &&
                mouse.X >= panelX + 90 + Margin && mouse.X < panelX + 160 + Margin &&
                SelectedRegionIndex >= 0)
            {
                _triggerSystem.RemoveRegion(SelectedRegionIndex);
                SelectedRegionIndex = Math.Min(SelectedRegionIndex, _triggerSystem.Regions.Count - 1);
            }
        }

        // World interaction: drag regions
        if (!overPanel)
        {
            Vec2 worldPos = _camera.ScreenToWorld(new Vector2(mouse.X, mouse.Y), screenW, screenH);

            if (leftClick && SelectedRegionIndex >= 0 && SelectedRegionIndex < _triggerSystem.Regions.Count)
            {
                var region = _triggerSystem.Regions[SelectedRegionIndex];
                // Check if we're on the edge (resize) or center (drag)
                float dx = MathF.Abs(worldPos.X - region.X);
                float dy = MathF.Abs(worldPos.Y - region.Y);

                if (region.Shape == RegionShape.Rectangle)
                {
                    bool onEdge = (MathF.Abs(dx - region.HalfW) < 1.5f) || (MathF.Abs(dy - region.HalfH) < 1.5f);
                    if (onEdge && dx <= region.HalfW + 1.5f && dy <= region.HalfH + 1.5f)
                    {
                        _draggingRegion = SelectedRegionIndex;
                        _draggingRegionResize = true;
                    }
                    else if (dx <= region.HalfW && dy <= region.HalfH)
                    {
                        _draggingRegion = SelectedRegionIndex;
                        _draggingRegionResize = false;
                    }
                }
                else // Circle
                {
                    float dist = (worldPos - new Vec2(region.X, region.Y)).Length();
                    if (MathF.Abs(dist - region.Radius) < 1.5f)
                    {
                        _draggingRegion = SelectedRegionIndex;
                        _draggingRegionResize = true;
                    }
                    else if (dist <= region.Radius)
                    {
                        _draggingRegion = SelectedRegionIndex;
                        _draggingRegionResize = false;
                    }
                }
            }

            if (leftDown && _draggingRegion >= 0 && _draggingRegion < _triggerSystem.Regions.Count)
            {
                var region = _triggerSystem.RegionsMut[_draggingRegion];
                if (_draggingRegionResize)
                {
                    if (region.Shape == RegionShape.Rectangle)
                    {
                        region.HalfW = MathF.Max(1f, MathF.Abs(worldPos.X - region.X));
                        region.HalfH = MathF.Max(1f, MathF.Abs(worldPos.Y - region.Y));
                    }
                    else
                    {
                        region.Radius = MathF.Max(1f, (worldPos - new Vec2(region.X, region.Y)).Length());
                    }
                }
                else
                {
                    region.X = worldPos.X;
                    region.Y = worldPos.Y;
                }
            }
            if (leftUp) _draggingRegion = -1;

            // Waypoint placement
            if (_regionPlaceWaypoint && leftClick && SelectedPatrolRoute >= 0)
            {
                var routes = _triggerSystem.PatrolRoutesMut;
                if (SelectedPatrolRoute < routes.Count)
                {
                    routes[SelectedPatrolRoute].Waypoints.Add(worldPos);
                    _regionPlaceWaypoint = false;
                }
            }
        }
    }

    private void DrawRegionsTab(int panelX, int contentY, int contentH, int screenW, int screenH)
    {
        DrawSectionHeader(panelX, ref contentY, $"Regions ({_triggerSystem.Regions.Count})");

        var mouse = Mouse.GetState();
        float scroll = _tabScroll[5];
        int y = contentY - (int)scroll;

        // Region list
        var regions = _triggerSystem.Regions;
        for (int i = 0; i < regions.Count; i++)
        {
            if (y < contentY - ButtonHeight) { y += ButtonHeight + 2; continue; }
            if (y > contentY + contentH - 300) break;

            bool selected = i == SelectedRegionIndex;
            var btnRect = new Rectangle(panelX + Margin, y, PanelWidth - Margin * 2, ButtonHeight);
            var bg = selected ? HighlightColor : (IsInRect(mouse, btnRect) ? ButtonHoverColor : Color.Transparent);
            if (bg != Color.Transparent) _spriteBatch.Draw(_pixel, btnRect, bg);

            DrawSmallText($"{regions[i].Name} ({regions[i].Id})", panelX + Margin + 4, y + 3,
                selected ? TextBright : TextColor);
            y += ButtonHeight + 2;
        }

        // Add / Delete
        DrawButtonRect("+ Add", panelX + Margin, y, 75, ButtonHeight, ButtonBg);
        DrawButtonRect("Delete", panelX + Margin + 85, y, 75, ButtonHeight, SelectedRegionIndex >= 0 ? DangerColor : ButtonBg);
        y += ButtonHeight + 8;

        // Selected region properties
        if (SelectedRegionIndex >= 0 && SelectedRegionIndex < regions.Count)
        {
            _spriteBatch.Draw(_pixel, new Rectangle(panelX, y, PanelWidth, 1), SeparatorColor);
            y += 4;
            var region = regions[SelectedRegionIndex];

            DrawSmallText($"ID: {region.Id}", panelX + Margin, y, TextBright); y += LineHeight;
            DrawSmallText($"Name: {region.Name}", panelX + Margin, y, TextColor); y += LineHeight;
            DrawSmallText($"Shape: {region.Shape}", panelX + Margin, y, TextColor); y += LineHeight;
            DrawSmallText($"Pos: ({region.X:F1}, {region.Y:F1})", panelX + Margin, y, TextColor); y += LineHeight;

            if (region.Shape == RegionShape.Rectangle)
            {
                DrawSmallText($"Half: ({region.HalfW:F1}, {region.HalfH:F1})", panelX + Margin, y, TextColor);
            }
            else
            {
                DrawSmallText($"Radius: {region.Radius:F1}", panelX + Margin, y, TextColor);
            }
            y += LineHeight + 4;
        }

        // Patrol Routes section
        _spriteBatch.Draw(_pixel, new Rectangle(panelX, y, PanelWidth, 1), SeparatorColor);
        y += 4;
        DrawSmallText($"Patrol Routes ({_triggerSystem.PatrolRoutes.Count})", panelX + Margin, y, AccentColor);
        y += LineHeight;

        var patrolRoutes = _triggerSystem.PatrolRoutes;
        for (int i = 0; i < patrolRoutes.Count; i++)
        {
            if (y > contentY + contentH) break;
            bool selected = i == SelectedPatrolRoute;
            DrawSmallText($"{(selected ? "> " : "  ")}{patrolRoutes[i].Name} ({patrolRoutes[i].Waypoints.Count} wps)",
                panelX + Margin, y, selected ? TextBright : TextColor);
            y += LineHeight;
        }

        // Add patrol route button region
        if (y < contentY + contentH - ButtonHeight)
        {
            DrawButtonRect("+ Add Route", panelX + Margin, y, 100, ButtonHeight, ButtonBg);
            y += ButtonHeight + 4;
        }

        // Waypoint controls
        if (SelectedPatrolRoute >= 0 && SelectedPatrolRoute < patrolRoutes.Count)
        {
            DrawSmallText("Click 'Place WP' then click on world", panelX + Margin, y, TextDim);
            y += LineHeight;
            DrawButtonRect(_regionPlaceWaypoint ? "[Placing...]" : "Place WP", panelX + Margin, y, 80, ButtonHeight,
                _regionPlaceWaypoint ? AccentColor : ButtonBg);
        }

        // Draw region overlays on world
        DrawRegionOverlays(screenW, screenH);
    }

    private void DrawRegionOverlays(int screenW, int screenH)
    {
        var regions = _triggerSystem.Regions;
        for (int i = 0; i < regions.Count; i++)
        {
            var region = regions[i];
            bool selected = i == SelectedRegionIndex;

            if (region.Shape == RegionShape.Rectangle)
            {
                var tl = _camera.WorldToScreen(new Vec2(region.X - region.HalfW, region.Y - region.HalfH), 0, screenW, screenH);
                var br = _camera.WorldToScreen(new Vec2(region.X + region.HalfW, region.Y + region.HalfH), 0, screenW, screenH);
                int rx = (int)tl.X, ry = (int)tl.Y;
                int rw = (int)(br.X - tl.X), rh = (int)(br.Y - tl.Y);
                if (rw > 0 && rh > 0)
                {
                    var fill = selected ? new Color(80, 200, 80, 40) : RegionRectColor;
                    var border = selected ? new Color(120, 255, 120, 220) : RegionRectBorder;
                    _spriteBatch.Draw(_pixel, new Rectangle(rx, ry, rw, rh), fill);
                    DrawRectBorder(rx, ry, rw, rh, border);
                }
            }
            else // Circle
            {
                var center = _camera.WorldToScreen(new Vec2(region.X, region.Y), 0, screenW, screenH);
                float screenRadius = region.Radius * _camera.Zoom;
                int segments = Math.Max(16, (int)(screenRadius * 0.5f));
                var color = selected ? new Color(120, 120, 255, 220) : RegionCircleBorder;
                DrawCircleOutline(center, screenRadius, color, segments);
            }

            // Label
            var labelPos = _camera.WorldToScreen(new Vec2(region.X, region.Y), 0, screenW, screenH);
            DrawSmallText(region.Name, (int)(labelPos.X - 20), (int)(labelPos.Y - 10), TextBright);
        }

        // Draw patrol route waypoints
        var patrolRoutes = _triggerSystem.PatrolRoutes;
        for (int ri = 0; ri < patrolRoutes.Count; ri++)
        {
            var route = patrolRoutes[ri];
            for (int wi = 0; wi < route.Waypoints.Count; wi++)
            {
                var sp = _camera.WorldToScreen(route.Waypoints[wi], 0, screenW, screenH);
                int sz = wi == SelectedWaypointIndex && ri == SelectedPatrolRoute ? 8 : 5;
                _spriteBatch.Draw(_pixel, new Rectangle((int)sp.X - sz / 2, (int)sp.Y - sz / 2, sz, sz), WaypointColor);

                if (wi > 0)
                {
                    var prev = _camera.WorldToScreen(route.Waypoints[wi - 1], 0, screenW, screenH);
                    DrawLine(prev, sp, WaypointColor);
                }
            }
        }
    }

    // ====================================================================
    //  TRIGGERS TAB
    // ====================================================================

    private void UpdateTriggersTab(MouseState mouse, KeyboardState kb, bool leftClick,
        bool overPanel, int panelX, int panelY, int screenW, int screenH)
    {
        if (leftClick && overPanel)
        {
            int contentY = panelY + TabRowHeight * 2 + HeaderHeight + 6;
            float scroll = _tabScroll[6];

            // Sub-section toggle: Defs | Instances
            int halfW = (PanelWidth - Margin * 2) / 2;
            if (mouse.Y >= contentY && mouse.Y < contentY + ButtonHeight)
            {
                int relX = mouse.X - (panelX + Margin);
                if (relX >= 0 && relX < halfW) _triggerSubSection = 0;
                else if (relX >= halfW) _triggerSubSection = 1;
            }

            int y = contentY + ButtonHeight + 4 - (int)scroll;

            if (_triggerSubSection == 0)
            {
                // Trigger def list
                for (int i = 0; i < _triggerSystem.Triggers.Count; i++)
                {
                    int btnY = y + i * (ButtonHeight + 2);
                    if (mouse.Y >= btnY && mouse.Y < btnY + ButtonHeight)
                    {
                        SelectedTriggerDefIndex = i;
                        break;
                    }
                }

                // Add Trigger Def
                int addY = y + _triggerSystem.Triggers.Count * (ButtonHeight + 2) + 4;
                if (mouse.Y >= addY && mouse.Y < addY + ButtonHeight &&
                    mouse.X >= panelX + Margin && mouse.X < panelX + 80 + Margin)
                {
                    var newDef = new TriggerDef
                    {
                        Id = $"trigger_{_triggerSystem.Triggers.Count}",
                        Name = $"Trigger {_triggerSystem.Triggers.Count}"
                    };
                    SelectedTriggerDefIndex = _triggerSystem.AddTrigger(newDef);
                }

                // Delete Trigger Def
                if (mouse.Y >= addY && mouse.Y < addY + ButtonHeight &&
                    mouse.X >= panelX + 90 + Margin && mouse.X < panelX + 160 + Margin &&
                    SelectedTriggerDefIndex >= 0)
                {
                    _triggerSystem.RemoveTrigger(SelectedTriggerDefIndex);
                    SelectedTriggerDefIndex = Math.Min(SelectedTriggerDefIndex, _triggerSystem.Triggers.Count - 1);
                }
            }
            else
            {
                // Instance list
                for (int i = 0; i < _triggerSystem.Instances.Count; i++)
                {
                    int btnY = y + i * (ButtonHeight + 2);
                    if (mouse.Y >= btnY && mouse.Y < btnY + ButtonHeight)
                    {
                        SelectedTriggerInstanceIndex = i;
                        break;
                    }
                }

                // Add Instance
                int addY = y + _triggerSystem.Instances.Count * (ButtonHeight + 2) + 4;
                if (mouse.Y >= addY && mouse.Y < addY + ButtonHeight &&
                    mouse.X >= panelX + Margin && mouse.X < panelX + 80 + Margin)
                {
                    var newInst = new TriggerInstance
                    {
                        InstanceID = $"inst_{_triggerSystem.Instances.Count}",
                        ParentTriggerID = SelectedTriggerDefIndex >= 0 && SelectedTriggerDefIndex < _triggerSystem.Triggers.Count
                            ? _triggerSystem.Triggers[SelectedTriggerDefIndex].Id : ""
                    };
                    SelectedTriggerInstanceIndex = _triggerSystem.AddInstance(newInst);
                }

                // Delete Instance
                if (mouse.Y >= addY && mouse.Y < addY + ButtonHeight &&
                    mouse.X >= panelX + 90 + Margin && mouse.X < panelX + 160 + Margin &&
                    SelectedTriggerInstanceIndex >= 0)
                {
                    _triggerSystem.RemoveInstance(SelectedTriggerInstanceIndex);
                    SelectedTriggerInstanceIndex = Math.Min(SelectedTriggerInstanceIndex, _triggerSystem.Instances.Count - 1);
                }
            }
        }

        // Scroll
        var scrollDelta = mouse.ScrollWheelValue - _prevScrollValue;
        if (scrollDelta != 0 && overPanel)
            _tabScroll[6] = MathF.Max(0, _tabScroll[6] - scrollDelta * 0.2f);
    }

    private void DrawTriggersTab(int panelX, int contentY, int contentH)
    {
        DrawSectionHeader(panelX, ref contentY, "Triggers");

        var mouse = Mouse.GetState();

        // Sub-section tabs: Defs | Instances
        int halfW = (PanelWidth - Margin * 2) / 2;
        {
            var defsRect = new Rectangle(panelX + Margin, contentY, halfW - 1, ButtonHeight);
            var instRect = new Rectangle(panelX + Margin + halfW, contentY, halfW - 1, ButtonHeight);
            _spriteBatch.Draw(_pixel, defsRect, _triggerSubSection == 0 ? TabActiveColor : TabInactiveColor);
            _spriteBatch.Draw(_pixel, instRect, _triggerSubSection == 1 ? TabActiveColor : TabInactiveColor);
            DrawTextCentered($"Defs ({_triggerSystem.Triggers.Count})", defsRect, TextColor);
            DrawTextCentered($"Instances ({_triggerSystem.Instances.Count})", instRect, TextColor);
        }
        contentY += ButtonHeight + 4;
        _spriteBatch.Draw(_pixel, new Rectangle(panelX, contentY - 2, PanelWidth, 1), SeparatorColor);

        float scroll = _tabScroll[6];
        int y = contentY - (int)scroll;

        if (_triggerSubSection == 0)
        {
            // Trigger Defs
            for (int i = 0; i < _triggerSystem.Triggers.Count; i++)
            {
                if (y < contentY - ButtonHeight) { y += ButtonHeight + 2; continue; }
                if (y > contentY + contentH - 300) break;

                var def = _triggerSystem.Triggers[i];
                bool selected = i == SelectedTriggerDefIndex;
                var btnRect = new Rectangle(panelX + Margin, y, PanelWidth - Margin * 2, ButtonHeight);

                var bg = selected ? HighlightColor : (IsInRect(mouse, btnRect) ? ButtonHoverColor : Color.Transparent);
                if (bg != Color.Transparent) _spriteBatch.Draw(_pixel, btnRect, bg);

                string label = $"{def.Name} ({def.Id})";
                DrawSmallText(label, panelX + Margin + 4, y + 3, selected ? TextBright : TextColor);
                y += ButtonHeight + 2;
            }

            // Add / Delete
            DrawButtonRect("+ Add", panelX + Margin, y, 75, ButtonHeight, ButtonBg);
            DrawButtonRect("Delete", panelX + Margin + 85, y, 75, ButtonHeight,
                SelectedTriggerDefIndex >= 0 ? DangerColor : ButtonBg);
            y += ButtonHeight + 8;

            // Properties for selected def
            if (SelectedTriggerDefIndex >= 0 && SelectedTriggerDefIndex < _triggerSystem.Triggers.Count)
            {
                _spriteBatch.Draw(_pixel, new Rectangle(panelX, y, PanelWidth, 1), SeparatorColor);
                y += 4;
                var def = _triggerSystem.Triggers[SelectedTriggerDefIndex];

                DrawSmallText($"ID: {def.Id}", panelX + Margin, y, TextBright); y += LineHeight;
                DrawSmallText($"Name: {def.Name}", panelX + Margin, y, TextColor); y += LineHeight;
                DrawSmallText($"Active: {def.ActiveByDefault}", panelX + Margin, y, TextColor); y += LineHeight;
                DrawSmallText($"OneShot: {def.OneShot}  MaxFire: {def.MaxFireCount}", panelX + Margin, y, TextColor); y += LineHeight;

                // Condition
                y += 4;
                DrawSmallText("Condition:", panelX + Margin, y, AccentColor); y += LineHeight;
                if (def.Condition != null)
                    DrawConditionTree(def.Condition, panelX + Margin + 8, ref y);
                else
                    DrawSmallText("  (none)", panelX + Margin, y, TextDim);
                y += LineHeight;

                // Effects
                y += 4;
                DrawSmallText($"Effects ({def.Effects.Count}):", panelX + Margin, y, AccentColor); y += LineHeight;
                for (int ei = 0; ei < def.Effects.Count; ei++)
                {
                    if (y > contentY + contentH) break;
                    bool selEffect = ei == SelectedEffectIndex;
                    string effectLabel = GetEffectLabel(def.Effects[ei]);
                    DrawSmallText($"  {(selEffect ? "> " : "")}{effectLabel}", panelX + Margin, y,
                        selEffect ? TextBright : TextColor);
                    y += LineHeight;
                }

                // Add effect / add condition buttons
                if (y < contentY + contentH - ButtonHeight * 2)
                {
                    DrawButtonRect("+ Condition", panelX + Margin, y, 100, ButtonHeight, ButtonBg);
                    DrawButtonRect("+ Effect", panelX + Margin + 110, y, 80, ButtonHeight, ButtonBg);
                    y += ButtonHeight + 4;
                }

                if (!string.IsNullOrEmpty(def.BoundObjectID))
                {
                    DrawSmallText($"Bound: {def.BoundObjectID}", panelX + Margin, y, SuccessColor);
                    y += LineHeight;
                }
            }
        }
        else
        {
            // Trigger Instances
            for (int i = 0; i < _triggerSystem.Instances.Count; i++)
            {
                if (y < contentY - ButtonHeight) { y += ButtonHeight + 2; continue; }
                if (y > contentY + contentH - 200) break;

                var inst = _triggerSystem.Instances[i];
                bool selected = i == SelectedTriggerInstanceIndex;
                var btnRect = new Rectangle(panelX + Margin, y, PanelWidth - Margin * 2, ButtonHeight);

                var bg = selected ? HighlightColor : (IsInRect(mouse, btnRect) ? ButtonHoverColor : Color.Transparent);
                if (bg != Color.Transparent) _spriteBatch.Draw(_pixel, btnRect, bg);

                DrawSmallText($"{inst.InstanceID} -> {inst.ParentTriggerID}",
                    panelX + Margin + 4, y + 3, selected ? TextBright : TextColor);
                y += ButtonHeight + 2;
            }

            // Add / Delete
            DrawButtonRect("+ Add", panelX + Margin, y, 75, ButtonHeight, ButtonBg);
            DrawButtonRect("Delete", panelX + Margin + 85, y, 75, ButtonHeight,
                SelectedTriggerInstanceIndex >= 0 ? DangerColor : ButtonBg);
            y += ButtonHeight + 8;

            // Properties for selected instance
            if (SelectedTriggerInstanceIndex >= 0 && SelectedTriggerInstanceIndex < _triggerSystem.Instances.Count)
            {
                _spriteBatch.Draw(_pixel, new Rectangle(panelX, y, PanelWidth, 1), SeparatorColor);
                y += 4;
                var inst = _triggerSystem.Instances[SelectedTriggerInstanceIndex];

                DrawSmallText($"ID: {inst.InstanceID}", panelX + Margin, y, TextBright); y += LineHeight;
                DrawSmallText($"Parent: {inst.ParentTriggerID}", panelX + Margin, y, TextColor); y += LineHeight;
                DrawSmallText($"Active: {inst.ActiveByDefault}", panelX + Margin, y, TextColor); y += LineHeight;
                DrawSmallText($"AutoCreated: {inst.AutoCreated}", panelX + Margin, y, TextColor); y += LineHeight;
                if (!string.IsNullOrEmpty(inst.BoundObjectID))
                {
                    DrawSmallText($"Bound: {inst.BoundObjectID}", panelX + Margin, y, SuccessColor);
                    y += LineHeight;
                }
            }
        }
    }

    private void DrawConditionTree(ConditionNode cond, int x, ref int y)
    {
        switch (cond)
        {
            case CondAnd andCond:
                DrawSmallText("AND", x, y, AccentColor); y += LineHeight;
                foreach (var child in andCond.Children) DrawConditionTree(child, x + 12, ref y);
                break;
            case CondOr orCond:
                DrawSmallText("OR", x, y, AccentColor); y += LineHeight;
                foreach (var child in orCond.Children) DrawConditionTree(child, x + 12, ref y);
                break;
            case CondNot notCond:
                DrawSmallText("NOT", x, y, AccentColor); y += LineHeight;
                if (notCond.Child != null) DrawConditionTree(notCond.Child, x + 12, ref y);
                break;
            case CondEntersRegion enters:
                DrawSmallText($"EntersRegion: {enters.RegionID} (min:{enters.MinCount})", x, y, TextColor);
                y += LineHeight;
                break;
            case CondUnitsKilled killed:
                DrawSmallText($"UnitsKilled: {killed.Count} cumul={killed.Cumulative}", x, y, TextColor);
                y += LineHeight;
                break;
            case CondGameTime gameTime:
                DrawSmallText($"GameTime >= {gameTime.Time:F1}", x, y, TextColor);
                y += LineHeight;
                break;
            case CondCooldown cooldown:
                DrawSmallText($"Cooldown: {cooldown.Interval:F1}s", x, y, TextColor);
                y += LineHeight;
                break;
            default:
                DrawSmallText($"Unknown: {cond.GetType().Name}", x, y, TextDim);
                y += LineHeight;
                break;
        }
    }

    private static string GetEffectLabel(TriggerEffect effect)
    {
        return effect switch
        {
            EffActivateTrigger act => $"Activate: {act.TriggerID}",
            EffDeactivateTrigger deact => $"Deactivate: {deact.TriggerID}",
            EffSpawnUnits spawn => $"Spawn: {spawn.Count}x {spawn.UnitDefID} ({spawn.Faction})",
            EffKillUnits kill => $"Kill in: {kill.RegionID} (max:{kill.MaxKills})",
            _ => $"Unknown: {effect.GetType().Name}"
        };
    }

    // ====================================================================
    //  SAVE / LOAD
    // ====================================================================

    private void SaveMap()
    {
        try
        {
            string mapsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "maps");
            Directory.CreateDirectory(mapsDir);
            string path = Path.Combine(mapsDir, _mapFilename + ".json");

            using var stream = File.Create(path);
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

            writer.WriteStartObject();

            // Ground types
            writer.WriteStartArray("groundTypes");
            for (int i = 0; i < _groundSystem.TypeCount; i++)
            {
                var gt = _groundSystem.GetTypeDef(i);
                writer.WriteStartObject();
                writer.WriteString("id", gt.Id);
                writer.WriteString("name", gt.Name);
                writer.WriteString("texturePath", gt.TexturePath);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            // Ground vertex map
            writer.WriteStartObject("groundMap");
            writer.WriteNumber("width", _groundSystem.VertexW);
            writer.WriteNumber("height", _groundSystem.VertexH);
            writer.WriteString("tilesBase64", Convert.ToBase64String(_groundSystem.GetVertexMap()));
            writer.WriteEndObject();

            // Grass types
            writer.WriteStartArray("grassTypes");
            foreach (var gt in _grassTypes)
            {
                writer.WriteStartObject();
                writer.WriteString("id", gt.Id);
                writer.WriteString("name", gt.Name);
                writer.WriteStartObject("baseColor");
                writer.WriteNumber("r", gt.BaseR);
                writer.WriteNumber("g", gt.BaseG);
                writer.WriteNumber("b", gt.BaseB);
                writer.WriteEndObject();
                writer.WriteStartObject("tipColor");
                writer.WriteNumber("r", gt.TipR);
                writer.WriteNumber("g", gt.TipG);
                writer.WriteNumber("b", gt.TipB);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            // Grass map
            if (_grassMap.Length > 0)
            {
                writer.WriteStartObject("grassMap");
                writer.WriteNumber("width", _grassW);
                writer.WriteNumber("height", _grassH);
                writer.WriteString("cellsBase64", Convert.ToBase64String(_grassMap));
                writer.WriteEndObject();
            }

            // Environment defs
            writer.WriteStartArray("envDefs");
            for (int i = 0; i < _envSystem.DefCount; i++)
            {
                var def = _envSystem.GetDef(i);
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
                writer.WriteBoolean("isBuilding", def.IsBuilding);
                writer.WriteBoolean("playerBuildable", def.PlayerBuildable);
                writer.WriteNumber("buildingMaxHP", def.BuildingMaxHP);
                writer.WriteNumber("buildingProtection", def.BuildingProtection);
                writer.WriteNumber("buildingDefaultOwner", def.BuildingDefaultOwner);
                writer.WriteString("boundTriggerID", def.BoundTriggerID);
                writer.WriteNumber("processTime", def.ProcessTime);
                writer.WriteBoolean("autoSpawn", def.AutoSpawn);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            // Placed objects
            writer.WriteStartArray("placedObjects");
            for (int i = 0; i < _envSystem.ObjectCount; i++)
            {
                var obj = _envSystem.GetObject(i);
                var def = _envSystem.GetDef(obj.DefIndex);
                writer.WriteStartObject();
                writer.WriteString("defId", def.Id);
                writer.WriteNumber("x", obj.X);
                writer.WriteNumber("y", obj.Y);
                writer.WriteNumber("scale", obj.Scale);
                writer.WriteNumber("seed", obj.Seed);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            // Wall defs
            writer.WriteStartArray("walls");
            foreach (var wd in _wallSystem.Defs)
            {
                writer.WriteStartObject();
                writer.WriteString("name", wd.Name);
                writer.WriteNumber("maxHP", wd.MaxHP);
                writer.WriteNumber("protection", wd.Protection);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            // Road texture defs
            writer.WriteStartArray("roadTextures");
            for (int i = 0; i < _roadSystem.TextureDefCount; i++)
            {
                var td = _roadSystem.GetTextureDef(i);
                writer.WriteStartObject();
                writer.WriteString("id", td.Id);
                writer.WriteString("name", td.Name);
                writer.WriteString("texturePath", td.TexturePath);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
            writer.Flush();

            // Save triggers separately
            SaveTriggers(mapsDir);

            // Save roads separately
            SaveRoads(mapsDir);

            _statusMessage = $"Saved: {path}";
            _statusTimer = 3f;
            DebugLog.Log("editor", $"Map saved to {path}");
        }
        catch (Exception ex)
        {
            _statusMessage = $"Save error: {ex.Message}";
            _statusTimer = 5f;
            DebugLog.Log("editor", $"Map save error: {ex.Message}");
        }
    }

    private void SaveTriggers(string mapsDir)
    {
        string path = Path.Combine(mapsDir, _mapFilename + "_triggers.json");
        using var stream = File.Create(path);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        writer.WriteStartObject();

        // Regions
        writer.WriteStartArray("regions");
        foreach (var r in _triggerSystem.Regions)
        {
            writer.WriteStartObject();
            writer.WriteString("id", r.Id);
            writer.WriteString("name", r.Name);
            writer.WriteString("shape", r.Shape.ToString());
            writer.WriteNumber("x", r.X);
            writer.WriteNumber("y", r.Y);
            writer.WriteNumber("halfW", r.HalfW);
            writer.WriteNumber("halfH", r.HalfH);
            writer.WriteNumber("radius", r.Radius);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        // Patrol routes
        writer.WriteStartArray("patrolRoutes");
        foreach (var pr in _triggerSystem.PatrolRoutes)
        {
            writer.WriteStartObject();
            writer.WriteString("id", pr.Id);
            writer.WriteString("name", pr.Name);
            writer.WriteBoolean("loop", pr.Loop);
            writer.WriteStartArray("waypoints");
            foreach (var wp in pr.Waypoints)
            {
                writer.WriteStartObject();
                writer.WriteNumber("x", wp.X);
                writer.WriteNumber("y", wp.Y);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        // Trigger defs
        writer.WriteStartArray("triggers");
        foreach (var t in _triggerSystem.Triggers)
        {
            writer.WriteStartObject();
            writer.WriteString("id", t.Id);
            writer.WriteString("name", t.Name);
            writer.WriteBoolean("activeByDefault", t.ActiveByDefault);
            writer.WriteBoolean("oneShot", t.OneShot);
            writer.WriteNumber("maxFireCount", t.MaxFireCount);
            if (!string.IsNullOrEmpty(t.BoundObjectID))
                writer.WriteString("boundObjectID", t.BoundObjectID);

            // Condition
            if (t.Condition != null)
            {
                writer.WritePropertyName("condition");
                WriteCondition(writer, t.Condition);
            }

            // Effects
            writer.WriteStartArray("effects");
            foreach (var e in t.Effects)
                WriteEffect(writer, e);
            writer.WriteEndArray();

            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        // Instances
        writer.WriteStartArray("instances");
        foreach (var inst in _triggerSystem.Instances)
        {
            writer.WriteStartObject();
            writer.WriteString("instanceID", inst.InstanceID);
            writer.WriteString("parentTriggerID", inst.ParentTriggerID);
            writer.WriteBoolean("activeByDefault", inst.ActiveByDefault);
            writer.WriteBoolean("autoCreated", inst.AutoCreated);
            if (!string.IsNullOrEmpty(inst.BoundObjectID))
                writer.WriteString("boundObjectID", inst.BoundObjectID);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteEndObject();
    }

    private void SaveRoads(string mapsDir)
    {
        string path = Path.Combine(mapsDir, _mapFilename + "_roads.json");
        using var stream = File.Create(path);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        writer.WriteStartObject();

        writer.WriteStartArray("roads");
        foreach (var road in _roadSystem.Roads)
        {
            writer.WriteStartObject();
            writer.WriteString("id", road.Id);
            writer.WriteString("name", road.Name);
            writer.WriteNumber("textureDefIndex", road.TextureDefIndex);
            writer.WriteNumber("renderOrder", road.RenderOrder);
            writer.WriteBoolean("closed", road.Closed);
            writer.WriteNumber("edgeSoftness", road.EdgeSoftness);
            writer.WriteNumber("textureScale", road.TextureScale);
            writer.WriteNumber("rimTextureDefIndex", road.RimTextureDefIndex);
            writer.WriteNumber("rimWidth", road.RimWidth);
            writer.WriteNumber("rimTextureScale", road.RimTextureScale);
            writer.WriteNumber("rimEdgeSoftness", road.RimEdgeSoftness);

            writer.WriteStartArray("points");
            foreach (var pt in road.Points)
            {
                writer.WriteStartObject();
                writer.WriteNumber("x", pt.Position.X);
                writer.WriteNumber("y", pt.Position.Y);
                writer.WriteNumber("width", pt.Width);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteStartArray("junctions");
        foreach (var j in _roadSystem.Junctions)
        {
            writer.WriteStartObject();
            writer.WriteString("id", j.Id);
            writer.WriteString("name", j.Name);
            writer.WriteNumber("x", j.Position.X);
            writer.WriteNumber("y", j.Position.Y);
            writer.WriteNumber("radius", j.Radius);
            writer.WriteNumber("textureDefIndex", j.TextureDefIndex);
            writer.WriteNumber("textureScale", j.TextureScale);
            writer.WriteNumber("edgeSoftness", j.EdgeSoftness);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteEndObject();
    }

    private static void WriteCondition(Utf8JsonWriter writer, ConditionNode cond)
    {
        writer.WriteStartObject();
        switch (cond)
        {
            case CondAnd andC:
                writer.WriteString("type", "AND");
                writer.WriteStartArray("children");
                foreach (var child in andC.Children) WriteCondition(writer, child);
                writer.WriteEndArray();
                break;
            case CondOr orC:
                writer.WriteString("type", "OR");
                writer.WriteStartArray("children");
                foreach (var child in orC.Children) WriteCondition(writer, child);
                writer.WriteEndArray();
                break;
            case CondNot notC:
                writer.WriteString("type", "NOT");
                if (notC.Child != null)
                {
                    writer.WritePropertyName("child");
                    WriteCondition(writer, notC.Child);
                }
                break;
            case CondEntersRegion er:
                writer.WriteString("type", "EntersRegion");
                writer.WriteString("regionID", er.RegionID);
                writer.WriteNumber("minCount", er.MinCount);
                break;
            case CondUnitsKilled uk:
                writer.WriteString("type", "UnitsKilled");
                writer.WriteNumber("count", uk.Count);
                writer.WriteBoolean("cumulative", uk.Cumulative);
                break;
            case CondGameTime gt:
                writer.WriteString("type", "GameTime");
                writer.WriteNumber("time", gt.Time);
                break;
            case CondCooldown cd:
                writer.WriteString("type", "Cooldown");
                writer.WriteNumber("interval", cd.Interval);
                break;
        }
        writer.WriteEndObject();
    }

    private static void WriteEffect(Utf8JsonWriter writer, TriggerEffect effect)
    {
        writer.WriteStartObject();
        switch (effect)
        {
            case EffActivateTrigger act:
                writer.WriteString("type", "ActivateTrigger");
                writer.WriteString("triggerID", act.TriggerID);
                break;
            case EffDeactivateTrigger deact:
                writer.WriteString("type", "DeactivateTrigger");
                writer.WriteString("triggerID", deact.TriggerID);
                break;
            case EffSpawnUnits spawn:
                writer.WriteString("type", "SpawnUnits");
                writer.WriteString("unitDefID", spawn.UnitDefID);
                writer.WriteNumber("count", spawn.Count);
                writer.WriteString("faction", spawn.Faction.ToString());
                writer.WriteString("regionID", spawn.RegionID);
                writer.WriteNumber("posX", spawn.Position.X);
                writer.WriteNumber("posY", spawn.Position.Y);
                writer.WriteNumber("spawnDistance", spawn.SpawnDistance);
                writer.WriteNumber("spawnInterval", spawn.SpawnInterval);
                writer.WriteString("postBehavior", spawn.PostBehavior.ToString());
                writer.WriteString("patrolRouteID", spawn.PatrolRouteID);
                break;
            case EffKillUnits kill:
                writer.WriteString("type", "KillUnits");
                writer.WriteString("regionID", kill.RegionID);
                writer.WriteNumber("maxKills", kill.MaxKills);
                break;
        }
        writer.WriteEndObject();
    }

    // ====================================================================
    //  BRUSH SYSTEM (shared)
    // ====================================================================

    private void DrawBrushSizeControl(int panelX, int y)
    {
        var mouse = Mouse.GetState();

        DrawSmallText($"Brush: {BrushRadius}", panelX + Margin + 40, y + 3, TextColor);

        // - button
        var minusRect = new Rectangle(panelX + Margin, y, 30, ButtonHeight);
        _spriteBatch.Draw(_pixel, minusRect, IsInRect(mouse, minusRect) ? ButtonHoverColor : ButtonBg);
        DrawTextCentered("-", minusRect, TextColor);

        // + button
        var plusRect = new Rectangle(panelX + PanelWidth - Margin - 30, y, 30, ButtonHeight);
        _spriteBatch.Draw(_pixel, plusRect, IsInRect(mouse, plusRect) ? ButtonHoverColor : ButtonBg);
        DrawTextCentered("+", plusRect, TextColor);

        // Handle clicks
        if (Mouse.GetState().LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released)
        {
            if (IsInRect(mouse, minusRect)) BrushRadius = Math.Max(0, BrushRadius - 1);
            if (IsInRect(mouse, plusRect)) BrushRadius = Math.Min(20, BrushRadius + 1);
        }
    }

    private void DrawBrushCursor(int screenW, int screenH)
    {
        var mouse = Mouse.GetState();
        Vec2 worldPos = _camera.ScreenToWorld(new Vector2(mouse.X, mouse.Y), screenW, screenH);

        // Determine grid snapping based on tab
        float gridStep = 1f;
        if (ActiveTab == MapEditorTab.Walls) gridStep = WallSystem.WallStep;

        float cx, cy;
        if (ActiveTab == MapEditorTab.Walls)
        {
            cx = WallSystem.SnapToWallGrid((int)MathF.Round(worldPos.X));
            cy = WallSystem.SnapToWallGrid((int)MathF.Round(worldPos.Y));
        }
        else
        {
            cx = MathF.Round(worldPos.X);
            cy = MathF.Round(worldPos.Y);
        }

        for (int dy = -BrushRadius; dy <= BrushRadius; dy++)
        {
            for (int dx = -BrushRadius; dx <= BrushRadius; dx++)
            {
                if (dx * dx + dy * dy > BrushRadius * BrushRadius) continue;

                float wx = cx + dx * gridStep;
                float wy = cy + dy * gridStep;

                float halfGrid = gridStep * 0.5f;
                var tl = _camera.WorldToScreen(new Vec2(wx - halfGrid, wy - halfGrid), 0, screenW, screenH);
                var br = _camera.WorldToScreen(new Vec2(wx + halfGrid, wy + halfGrid), 0, screenW, screenH);
                int rx = (int)tl.X, ry = (int)tl.Y;
                int rw = (int)(br.X - tl.X), rh = (int)(br.Y - tl.Y);
                if (rw > 0 && rh > 0)
                {
                    _spriteBatch.Draw(_pixel, new Rectangle(rx, ry, rw, rh), BrushCursorFill);
                    DrawRectBorder(rx, ry, rw, rh, BrushCursorEdge);
                }
            }
        }
    }

    // ====================================================================
    //  DRAWING HELPERS
    // ====================================================================

    private void DrawSectionHeader(int panelX, ref int y, string text)
    {
        _spriteBatch.Draw(_pixel, new Rectangle(panelX, y, PanelWidth, HeaderHeight), HeaderBg);
        DrawSmallText(text, panelX + Margin, y + 6, TextBright);
        y += HeaderHeight + 4;
    }

    private void DrawSmallText(string text, int x, int y, Color color)
    {
        var font = _smallFont ?? _font;
        if (font != null)
            _spriteBatch.DrawString(font, text, new Vector2(x, y), color);
    }

    private void DrawTextCentered(string text, Rectangle rect, Color color)
    {
        var font = _smallFont ?? _font;
        if (font == null) return;
        var size = font.MeasureString(text);
        _spriteBatch.DrawString(font, text,
            new Vector2(rect.X + (rect.Width - size.X) / 2f, rect.Y + (rect.Height - size.Y) / 2f), color);
    }

    private void DrawButtonRect(string text, int x, int y, int w, int h, Color bg)
    {
        var mouse = Mouse.GetState();
        var rect = new Rectangle(x, y, w, h);
        bool hovered = IsInRect(mouse, rect);
        _spriteBatch.Draw(_pixel, rect, hovered ? ButtonHoverColor : bg);
        DrawTextCentered(text, rect, TextColor);
    }

    private void DrawRectBorder(int x, int y, int w, int h, Color color)
    {
        _spriteBatch.Draw(_pixel, new Rectangle(x, y, w, 1), color);
        _spriteBatch.Draw(_pixel, new Rectangle(x, y + h - 1, w, 1), color);
        _spriteBatch.Draw(_pixel, new Rectangle(x, y, 1, h), color);
        _spriteBatch.Draw(_pixel, new Rectangle(x + w - 1, y, 1, h), color);
    }

    private void DrawLine(Vector2 a, Vector2 b, Color color)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 1f) return;

        float angle = MathF.Atan2(dy, dx);
        _spriteBatch.Draw(_pixel, new Rectangle((int)a.X, (int)a.Y, (int)len, 1),
            null, color, angle, Vector2.Zero, SpriteEffects.None, 0);
    }

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

    private static bool IsInRect(MouseState mouse, Rectangle rect) =>
        mouse.X >= rect.X && mouse.X < rect.X + rect.Width &&
        mouse.Y >= rect.Y && mouse.Y < rect.Y + rect.Height;

    // ---- Environment helpers ----

    private List<string> GetEnvCategories()
    {
        var cats = new HashSet<string>();
        for (int i = 0; i < _envSystem.DefCount; i++)
            cats.Add(_envSystem.GetDef(i).Category);

        var list = new List<string> { "All" };
        foreach (var c in cats)
            if (c != "All") list.Add(c);
        return list;
    }

    private List<int> GetFilteredEnvDefs(List<string> categories)
    {
        var result = new List<int>();
        string cat = SelectedEnvCategory < categories.Count ? categories[SelectedEnvCategory] : "All";
        for (int i = 0; i < _envSystem.DefCount; i++)
        {
            if (cat == "All" || _envSystem.GetDef(i).Category == cat)
                result.Add(i);
        }
        return result;
    }

    private int FindClosestObject(Vec2 worldPos, float maxDist)
    {
        float bestDist = maxDist * maxDist;
        int best = -1;
        for (int i = 0; i < _envSystem.ObjectCount; i++)
        {
            var obj = _envSystem.GetObject(i);
            float dx = obj.X - worldPos.X;
            float dy = obj.Y - worldPos.Y;
            float d2 = dx * dx + dy * dy;
            if (d2 < bestDist) { bestDist = d2; best = i; }
        }
        return best;
    }
}
