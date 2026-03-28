using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    private EditorBase? _eb;

    // Callbacks
    private Action? _onVertexMapChanged;
    private Action? _onGrassMapChanged;

    // ---- Grass data (owned by editor, synced back to Game1) ----
    private readonly List<GrassTypeDef> _grassTypes = new();
    private byte[] _grassMap = Array.Empty<byte>();
    private int _grassW, _grassH;
    private float _grassCellSize = 0.8f;

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
    private bool _showWallDebug;
    private WallEditorWindow? _wallEditor;

    // Objects tab extras
    private bool _showCollisions;
    private EnvObjectEditorWindow? _envObjectEditor;

    // Texture file browser (UI01)
    private readonly TextureFileBrowser _textureBrowser = new();

    // Roads tab
    public int SelectedRoadTexDef = -1;
    public int SelectedRoadIndex = -1;
    public int SelectedRoadPoint = -1;
    public int SelectedJunctionIndex = -1;
    private bool _roadPlaceMode;
    private bool _junctionPlaceMode; // RM15: click-to-place junction in viewport
    private int _draggingPoint = -1;
    private int _draggingJunction = -1;

    // Regions tab
    public int SelectedRegionIndex = -1;
    public int SelectedPatrolRoute = -1;
    public int SelectedWaypointIndex = -1;
    private bool _regionPlaceWaypoint;
    private int _draggingRegion = -1;
    private bool _draggingRegionResize;

    // M26: Region drag handle types
    private enum RegionHandle
    {
        None, Body,
        N, E, S, W,       // Edge midpoints
        NW, NE, SE, SW,   // Corners
        CircleRadius       // Circle radius handle
    }
    private RegionHandle _activeHandle = RegionHandle.None;

    // Cached enum name arrays (avoid per-frame allocation)
    private static readonly string[] CachedFactionNames = Enum.GetNames<Faction>();
    private static readonly string[] CachedPostSpawnBehaviorNames = Enum.GetNames<PostSpawnBehavior>();

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

    // M16: Smooth camera velocity
    private float _camVelX;
    private float _camVelY;
    private const float CamAcceleration = 40f;
    private const float CamFriction = 8f;
    private const float CamBaseSpeed = 30f;

    // RM17: Waypoint dragging
    private int _draggingWaypoint = -1;

    // M28: Object batch undo accumulators
    private List<(ushort defIdx, float x, float y, float scale, float seed, int objIdx)>? _batchPlacedObjects;
    private List<(ushort defIdx, float x, float y, float scale, float seed)>? _batchRemovedObjects;

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

    // ---- Undo system ----
    private const int MaxUndoStack = 50;
    private readonly List<UndoAction> _undoStack = new();

    // Stroke accumulators (active during a drag)
    private Dictionary<long, byte>? _groundStrokeOld;   // key = vy*VertexW+vx -> old type
    private Dictionary<int, byte>? _grassStrokeOld;     // key = gy*grassW+gx -> old value
    private Dictionary<int, (byte type, short hp)>? _wallStrokeOld; // key = ty*wallW+tx -> old (type,hp)

    // ---- Undo action types ----

    private abstract class UndoAction { public abstract void Undo(); }

    private class UndoGroundStroke : UndoAction
    {
        public GroundSystem Ground = null!;
        public Dictionary<long, byte> OldValues = new();
        public Action? OnChanged;
        public override void Undo()
        {
            foreach (var kv in OldValues)
            {
                int vx = (int)(kv.Key % 100000);
                int vy = (int)(kv.Key / 100000);
                Ground.SetVertex(vx, vy, kv.Value);
            }
            OnChanged?.Invoke();
        }
    }

    private class UndoGrassStroke : UndoAction
    {
        public byte[] GrassMap = null!;
        public Dictionary<int, byte> OldValues = new();
        public Action? OnChanged;
        public override void Undo()
        {
            foreach (var kv in OldValues)
            {
                if (kv.Key >= 0 && kv.Key < GrassMap.Length)
                    GrassMap[kv.Key] = kv.Value;
            }
            OnChanged?.Invoke();
        }
    }

    private class UndoWallStroke : UndoAction
    {
        public WallSystem Walls = null!;
        public Dictionary<int, (byte type, short hp)> OldValues = new();
        public int WallWidth;
        public override void Undo()
        {
            var types = Walls.GetTypes();
            var hps = Walls.GetHPArray();
            foreach (var kv in OldValues)
            {
                if (kv.Key >= 0 && kv.Key < types.Length)
                {
                    types[kv.Key] = kv.Value.type;
                    hps[kv.Key] = kv.Value.hp;
                }
            }
        }
    }

    private class UndoObjectPlace : UndoAction
    {
        public EnvironmentSystem Env = null!;
        public int ObjectIndex;
        public override void Undo()
        {
            if (ObjectIndex >= 0 && ObjectIndex < Env.ObjectCount)
                Env.RemoveObject(ObjectIndex);
        }
    }

    private class UndoObjectRemove : UndoAction
    {
        public EnvironmentSystem Env = null!;
        public ushort DefIndex;
        public float X, Y, Scale, Seed;
        public override void Undo()
        {
            Env.AddObject(DefIndex, X, Y, Scale, Seed);
        }
    }

    // M28: Batch undo for paint-mode object placement
    private class UndoObjectBatchPlace : UndoAction
    {
        public EnvironmentSystem Env = null!;
        public List<int> ObjectIndices = new();
        public override void Undo()
        {
            // Remove in reverse order so indices remain valid
            for (int i = ObjectIndices.Count - 1; i >= 0; i--)
            {
                int idx = ObjectIndices[i];
                if (idx >= 0 && idx < Env.ObjectCount)
                    Env.RemoveObject(idx);
            }
        }
    }

    // M28: Batch undo for paint-mode object removal
    private class UndoObjectBatchRemove : UndoAction
    {
        public EnvironmentSystem Env = null!;
        public List<(ushort defIdx, float x, float y, float scale, float seed)> Removed = new();
        public override void Undo()
        {
            foreach (var r in Removed)
                Env.AddObject(r.defIdx, r.x, r.y, r.scale, r.seed);
        }
    }

    private void PushUndo(UndoAction action)
    {
        _undoStack.Add(action);
        if (_undoStack.Count > MaxUndoStack)
            _undoStack.RemoveAt(0);
    }

    private void PerformUndo()
    {
        if (_undoStack.Count == 0) return;
        var action = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        action.Undo();
    }

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
        Action? onGrassMapChanged = null,
        EditorBase? editorBase = null)
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
        _eb = editorBase;

        // Initialize the environment object def editor sub-window
        if (_eb != null)
        {
            _envObjectEditor = new EnvObjectEditorWindow();
            _envObjectEditor.Init(_eb, _envSystem, device, spriteBatch, pixel, font, smallFont, _triggerSystem);

            _wallEditor = new WallEditorWindow(_eb);
            _wallEditor.SetWallSystem(_wallSystem);
            _wallEditor.SetMapFilename(_mapFilename);
        }
    }

    /// <summary>
    /// Set the grass data arrays for the editor to manipulate.
    /// </summary>
    public void SetGrassData(byte[] grassMap, int grassW, int grassH, MapData.GrassTypeInfo[] types, float cellSize = 0.8f)
    {
        _grassMap = grassMap;
        _grassW = grassW;
        _grassH = grassH;
        _grassCellSize = cellSize > 0f ? cellSize : 0.8f;
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

    /// <summary>Whether the env object def editor overlay is open (blocks map interaction).</summary>
    public bool IsEnvObjectEditorOpen => _envObjectEditor != null && _envObjectEditor.IsOpen;

    public void Update(int screenW, int screenH)
    {
        // Update env object editor if open (blocks all normal input)
        if (_envObjectEditor != null && _envObjectEditor.IsOpen)
        {
            _envObjectEditor.Update();
            _prevMouse = Mouse.GetState();
            _prevKb = Keyboard.GetState();
            _prevScrollValue = _prevMouse.ScrollWheelValue;
            return;
        }

        var mouse = Mouse.GetState();
        var kb = Keyboard.GetState();
        float dt = 1f / 60f; // fixed timestep assumption

        // If wall editor overlay is open, skip normal input processing
        if (_wallEditor != null && _wallEditor.IsOpen)
        {
            _prevMouse = mouse;
            _prevKb = kb;
            _prevScrollValue = mouse.ScrollWheelValue;
            return;
        }

        bool leftClick = mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released;
        bool leftDown = mouse.LeftButton == ButtonState.Pressed;
        bool leftUp = mouse.LeftButton == ButtonState.Released && _prevMouse.LeftButton == ButtonState.Pressed;
        bool rightClick = mouse.RightButton == ButtonState.Pressed && _prevMouse.RightButton == ButtonState.Released;
        bool rightDown = mouse.RightButton == ButtonState.Pressed;
        bool rightUp = mouse.RightButton == ButtonState.Released && _prevMouse.RightButton == ButtonState.Pressed;

        int panelX = screenW - PanelWidth - 10;
        int panelY = 10;
        bool overPanel = IsMouseOverPanel(screenW, screenH);

        // INF03: Set mouse-over-UI flag when over the editor panel
        if (overPanel && _eb != null)
            _eb.SetMouseOverUI();

        // Status timer
        if (_statusTimer > 0) _statusTimer -= dt;

        // M31: Check if text field is being edited to suppress hotkeys
        bool textEditing = _eb != null && _eb.IsTextInputActive;

        if (!textEditing)
        {
            // --- Tab switching via keyboard (1-7) ---
            for (int i = 0; i < 7; i++)
            {
                Keys key = Keys.D1 + i;
                if (kb.IsKeyDown(key) && _prevKb.IsKeyUp(key))
                    ActiveTab = (MapEditorTab)i;
            }
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

        if (!textEditing)
        {
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

            // M16: Smooth WASD camera with acceleration/friction
            // RM37: Arrow keys in addition to WASD
            if (kb.IsKeyDown(Keys.W) || kb.IsKeyDown(Keys.Up)) _camVelY -= CamAcceleration * dt;
            if ((kb.IsKeyDown(Keys.S) && !kb.IsKeyDown(Keys.LeftControl)) || kb.IsKeyDown(Keys.Down)) _camVelY += CamAcceleration * dt;
            if (kb.IsKeyDown(Keys.A) || kb.IsKeyDown(Keys.Left)) _camVelX -= CamAcceleration * dt;
            if (kb.IsKeyDown(Keys.D) || kb.IsKeyDown(Keys.Right)) _camVelX += CamAcceleration * dt;

            // RM10: 'B' hotkey to toggle Single/Paint mode in Objects tab
            if (ActiveTab == MapEditorTab.Objects && kb.IsKeyDown(Keys.B) && _prevKb.IsKeyUp(Keys.B))
                _objectPaintMode = !_objectPaintMode;
        }

        // RM35: Apply exponential friction to camera velocity
        float frictionFactor = MathF.Exp(-CamFriction * dt);
        _camVelX *= frictionFactor;
        _camVelY *= frictionFactor;

        // RM36: Clamp camera speed inversely with zoom
        float camMaxSpeed = CamBaseSpeed / _camera.Zoom * 100f;
        float speed = MathF.Sqrt(_camVelX * _camVelX + _camVelY * _camVelY);
        if (speed > camMaxSpeed)
        {
            _camVelX = _camVelX / speed * camMaxSpeed;
            _camVelY = _camVelY / speed * camMaxSpeed;
        }

        // Apply camera velocity
        if (MathF.Abs(_camVelX) > 0.01f || MathF.Abs(_camVelY) > 0.01f)
        {
            var pos = _camera.Position;
            _camera.Position = new Vec2(pos.X + _camVelX * dt, pos.Y + _camVelY * dt);
        }

        // --- Scroll per-tab ---
        int scrollDelta = mouse.ScrollWheelValue - _prevScrollValue;
        if (scrollDelta != 0 && overPanel)
        {
            int tabIdx = (int)ActiveTab;
            _tabScroll[tabIdx] = MathF.Max(0, _tabScroll[tabIdx] - scrollDelta * 0.2f);
        }

        // --- Save (Ctrl+S) — suppress when text field is active ---
        if (!textEditing && kb.IsKeyDown(Keys.LeftControl) && kb.IsKeyDown(Keys.S) && _prevKb.IsKeyUp(Keys.S))
            SaveMap();

        // --- Load (Ctrl+L) — suppress when text field is active ---
        if (!textEditing && kb.IsKeyDown(Keys.LeftControl) && kb.IsKeyDown(Keys.L) && _prevKb.IsKeyUp(Keys.L))
            LoadMap();

        // --- Undo (Ctrl+Z) — suppress when text field is active ---
        if (!textEditing && kb.IsKeyDown(Keys.LeftControl) && kb.IsKeyDown(Keys.Z) && _prevKb.IsKeyUp(Keys.Z))
        {
            PerformUndo();
            _statusMessage = $"Undo ({_undoStack.Count} remaining)";
            _statusTimer = 1.5f;
        }

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
                UpdateWallsTab(mouse, kb, leftClick, leftDown, leftUp, rightClick, rightDown, rightUp, overPanel, panelX, panelY, screenW, screenH);
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

        // Update texture file browser input
        _textureBrowser.Update(mouse, _prevMouse, kb, _prevKb);

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

        // Content area (reserve 92px for bottom bar: filename + buttons + shortcuts + status)
        int contentY = tabsBottom + 2;
        int contentH = panelH - (tabsBottom - panelY) - 2 - 92;

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

        // Bottom bar: map filename, Save/Load buttons, undo info, status message
        int bottomH = 90; // height of the bottom section
        int bottomY = panelY + panelH - bottomH;
        _spriteBatch.Draw(_pixel, new Rectangle(panelX, bottomY, PanelWidth, 1), SeparatorColor);
        bottomY += 2;

        // Map filename text field
        if (_eb != null)
        {
            string newFilename = _eb.DrawTextField("map_filename", "Map File", _mapFilename, panelX + Margin, bottomY, PanelWidth - Margin * 2);
            if (newFilename != _mapFilename) _mapFilename = newFilename;
        }
        else
        {
            DrawSmallText($"Map: {_mapFilename}", panelX + Margin, bottomY + 3, TextColor);
        }
        bottomY += FieldHeight + 2;

        // Save / Load / Undo buttons
        int btnW3 = (PanelWidth - Margin * 2 - 8) / 3;
        DrawButtonRect("Save", panelX + Margin, bottomY, btnW3, ButtonHeight, ButtonBg);
        DrawButtonRect("Load", panelX + Margin + btnW3 + 4, bottomY, btnW3, ButtonHeight, ButtonBg);
        DrawButtonRect($"Undo ({_undoStack.Count})", panelX + Margin + (btnW3 + 4) * 2, bottomY, btnW3, ButtonHeight,
            _undoStack.Count > 0 ? AccentColor : ButtonBg);

        // Handle Save/Load/Undo button clicks
        {
            var mouse2 = Mouse.GetState();
            if (mouse2.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released)
            {
                if (mouse2.Y >= bottomY && mouse2.Y < bottomY + ButtonHeight)
                {
                    int relX = mouse2.X - (panelX + Margin);
                    if (relX >= 0 && relX < btnW3)
                        SaveMap();
                    else if (relX >= btnW3 + 4 && relX < btnW3 * 2 + 4)
                        LoadMap();
                    else if (relX >= (btnW3 + 4) * 2 && relX < (btnW3 + 4) * 2 + btnW3)
                    {
                        PerformUndo();
                        _statusMessage = $"Undo ({_undoStack.Count} remaining)";
                        _statusTimer = 1.5f;
                    }
                }
            }
        }
        bottomY += ButtonHeight + 2;

        // Keyboard shortcuts hint
        DrawSmallText("Ctrl+S Save | Ctrl+L Load | Ctrl+Z Undo", panelX + Margin, bottomY + 2, TextDim);
        bottomY += LineHeight;

        // INF14: Status message with alpha fade
        if (_statusTimer > 0 && !string.IsNullOrEmpty(_statusMessage))
        {
            bool isSuccess = _statusMessage.StartsWith("Saved") || _statusMessage.StartsWith("Loaded") || _statusMessage.StartsWith("Undo");
            Color baseColor = isSuccess ? SuccessColor : DangerColor;
            Color fadedColor = EditorBase.FadeStatusColor(baseColor, _statusTimer);
            DrawSmallText(_statusMessage, panelX + Margin, bottomY + 2, fadedColor);
        }

        // Wall editor overlay (drawn last, on top of everything)
        if (_wallEditor != null && _wallEditor.IsOpen)
        {
            var gameTime = new GameTime(TimeSpan.Zero, TimeSpan.FromSeconds(1.0 / 60.0));
            _wallEditor.Draw(screenW, screenH, gameTime);
        }

        // Env object def editor overlay
        if (_envObjectEditor != null && _envObjectEditor.IsOpen)
        {
            _envObjectEditor.Draw(screenW, screenH);
        }

        // Texture file browser popup
        if (_eb != null)
        {
            _textureBrowser.Draw(_eb, screenW, screenH);
        }

        // Dropdown overlays (drawn last, on top of everything)
        if (_eb != null)
        {
            _eb.DrawDropdownOverlays();
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
                mouse.X >= panelX + Margin && mouse.X < panelX + 80 + Margin)
            {
                _groundSystem.AddGroundType(new GroundTypeDef
                {
                    Id = $"type_{_groundSystem.TypeCount}",
                    Name = $"Type {_groundSystem.TypeCount}"
                });
            }

            // Delete Type button
            if (_groundSystem.TypeCount > 1 && SelectedGroundType >= 0 &&
                mouse.Y >= addY && mouse.Y < addY + ButtonHeight &&
                mouse.X >= panelX + Margin + 90 && mouse.X < panelX + Margin + 160)
            {
                _groundSystem.RemoveType(SelectedGroundType);
                SelectedGroundType = Math.Min(SelectedGroundType, _groundSystem.TypeCount - 1);
                _onVertexMapChanged?.Invoke();
            }
        }

        // Paint on world
        if (leftDown && !overPanel)
        {
            if (!_painting)
                _groundStrokeOld = new Dictionary<long, byte>();
            _painting = true;
            PaintGround(mouse, screenW, screenH);
        }
        if (leftUp)
        {
            if (_painting && _groundStrokeOld != null && _groundStrokeOld.Count > 0)
            {
                PushUndo(new UndoGroundStroke
                {
                    Ground = _groundSystem,
                    OldValues = _groundStrokeOld,
                    OnChanged = _onVertexMapChanged
                });
            }
            _groundStrokeOld = null;
            _painting = false;
        }
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
                    byte oldVal = _groundSystem.GetVertex(cx, cy);
                    if (oldVal != typeIdx)
                    {
                        // Record old value if not already recorded this stroke
                        long key = (long)cy * 100000 + cx;
                        _groundStrokeOld?.TryAdd(key, oldVal);
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

        // Delete Type button (only if >1 type)
        if (_groundSystem.TypeCount > 1 && SelectedGroundType >= 0)
            DrawButtonRect("Delete", panelX + Margin + 90, addY, 70, ButtonHeight, DangerColor);

        addY += ButtonHeight + 8;

        // Selected ground type editable properties
        if (SelectedGroundType >= 0 && SelectedGroundType < _groundSystem.TypeCount && _eb != null)
        {
            _spriteBatch.Draw(_pixel, new Rectangle(panelX, addY, PanelWidth, 1), SeparatorColor);
            addY += 4;
            var def = _groundSystem.GetTypeDef(SelectedGroundType);
            int fw = PanelWidth - Margin * 2;

            string newName = _eb.DrawTextField("ground_name", "Name", def.Name, panelX + Margin, addY, fw);
            if (newName != def.Name) def.Name = newName;
            addY += FieldHeight + 2;

            int gndBrowseBtnW = 55;
            string newTexPath = _eb.DrawTextField("ground_tex", "Texture", def.TexturePath, panelX + Margin, addY, fw - gndBrowseBtnW - 4);
            if (newTexPath != def.TexturePath)
            {
                def.TexturePath = newTexPath;
                _groundSystem.LoadTextures(_device);
            }
            if (_eb.DrawButton("Browse", panelX + Margin + fw - gndBrowseBtnW, addY, gndBrowseBtnW, 20))
            {
                _textureBrowser.Open("assets/Environment/Ground", def.TexturePath, path =>
                {
                    def.TexturePath = path;
                    _groundSystem.LoadTextures(_device);
                });
            }
            addY += FieldHeight + 2;
        }

        // Brush size
        addY += 4;
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
                mouse.X >= panelX + Margin && mouse.X < panelX + 80 + Margin)
            {
                _grassTypes.Add(new GrassTypeDef
                {
                    Id = $"grass_{_grassTypes.Count}",
                    Name = $"Grass {_grassTypes.Count}"
                });
                // Allocate grass map if none exists yet
                if (_grassMap.Length == 0 && _groundSystem.WorldW > 0)
                {
                    float cs = _grassCellSize > 0f ? _grassCellSize : 0.8f;
                    _grassW = (int)MathF.Ceiling(_groundSystem.WorldW / cs);
                    _grassH = (int)MathF.Ceiling(_groundSystem.WorldH / cs);
                    _grassMap = new byte[_grassW * _grassH];
                }
                _onGrassMapChanged?.Invoke();
            }

            // Delete Type button (next to Add button)
            if (!_grassEraserSelected && SelectedGrassType >= 0 && SelectedGrassType < _grassTypes.Count &&
                mouse.Y >= addY && mouse.Y < addY + ButtonHeight &&
                mouse.X >= panelX + Margin + 90 && mouse.X < panelX + Margin + 180)
            {
                _grassTypes.RemoveAt(SelectedGrassType);
                // Remap grass map
                for (int gi = 0; gi < _grassMap.Length; gi++)
                {
                    byte v = _grassMap[gi];
                    if (v == 255 || v == 0) continue; // eraser or empty
                    int typeIdx = v - 1; // 1-based to 0-based
                    if (typeIdx == SelectedGrassType) _grassMap[gi] = 0;
                    else if (typeIdx > SelectedGrassType) _grassMap[gi] = (byte)(v - 1);
                }
                SelectedGrassType = Math.Min(SelectedGrassType, _grassTypes.Count - 1);
                _onGrassMapChanged?.Invoke();
            }
        }

        // Paint grass
        if (leftDown && !overPanel && _grassMap.Length > 0)
        {
            if (!_painting)
                _grassStrokeOld = new Dictionary<int, byte>();
            _painting = true;
            PaintGrass(mouse, screenW, screenH);
        }
        if (leftUp)
        {
            if (_painting && _grassStrokeOld != null && _grassStrokeOld.Count > 0)
            {
                PushUndo(new UndoGrassStroke
                {
                    GrassMap = _grassMap,
                    OldValues = _grassStrokeOld,
                    OnChanged = _onGrassMapChanged
                });
            }
            _grassStrokeOld = null;
            _painting = false;
        }
    }

    private void PaintGrass(MouseState mouse, int screenW, int screenH)
    {
        if (_grassW == 0 || _grassH == 0) return;

        Vec2 worldPos = _camera.ScreenToWorld(new Vector2(mouse.X, mouse.Y), screenW, screenH);
        // Grass cells use cellSize (e.g. 0.8) — must match GrassRenderer mapping
        float cs = _grassCellSize > 0f ? _grassCellSize : 0.8f;
        int cx = (int)MathF.Floor(worldPos.X / cs);
        int cy = (int)MathF.Floor(worldPos.Y / cs);

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
                    byte oldVal = _grassMap[idx];
                    if (oldVal != paintValue)
                    {
                        _grassStrokeOld?.TryAdd(idx, oldVal);
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

        // Delete Type button
        if (!_grassEraserSelected && SelectedGrassType >= 0 && SelectedGrassType < _grassTypes.Count)
        {
            DrawButtonRect("Delete Type", panelX + Margin + 90, y - ButtonHeight - 8, 90, ButtonHeight, DangerColor);
        }

        // Selected type properties (editable)
        if (!_grassEraserSelected && SelectedGrassType >= 0 && SelectedGrassType < _grassTypes.Count && _eb != null)
        {
            _spriteBatch.Draw(_pixel, new Rectangle(panelX, y, PanelWidth, 1), SeparatorColor);
            y += 4;
            var gt = _grassTypes[SelectedGrassType];
            int fw = PanelWidth - Margin * 2;
            bool changed = false;

            string newName = _eb.DrawTextField("grass_name", "Name", gt.Name, panelX + Margin, y, fw);
            if (newName != gt.Name) { gt.Name = newName; changed = true; }
            y += FieldHeight + 2;

            // Base Color — LDR color swatch
            DrawSmallText("Base Color:", panelX + Margin, y, AccentColor);
            var baseHdr = new HdrColor(gt.BaseR, gt.BaseG, gt.BaseB, 255, 1.0f);
            if (_eb.DrawColorSwatch("grass_baseColor", panelX + Margin + 80, y, 40, 18, ref baseHdr, hideIntensity: true))
            {
                gt.BaseR = baseHdr.R; gt.BaseG = baseHdr.G; gt.BaseB = baseHdr.B;
                changed = true;
            }
            y += FieldHeight + 2;

            // Tip Color — LDR color swatch
            DrawSmallText("Tip Color:", panelX + Margin, y, AccentColor);
            var tipHdr = new HdrColor(gt.TipR, gt.TipG, gt.TipB, 255, 1.0f);
            if (_eb.DrawColorSwatch("grass_tipColor", panelX + Margin + 80, y, 40, 18, ref tipHdr, hideIntensity: true))
            {
                gt.TipR = tipHdr.R; gt.TipG = tipHdr.G; gt.TipB = tipHdr.B;
                changed = true;
            }
            y += FieldHeight + 2;

            // Density
            float newDensity = _eb.DrawFloatField("grass_density", "Density", gt.Density, panelX + Margin, y, fw, 0.1f);
            if (MathF.Abs(newDensity - gt.Density) > 0.001f) { gt.Density = newDensity; changed = true; }
            y += FieldHeight + 2;

            // Height
            float newHeight = _eb.DrawFloatField("grass_height", "Height", gt.Height, panelX + Margin, y, fw, 0.1f);
            if (MathF.Abs(newHeight - gt.Height) > 0.001f) { gt.Height = newHeight; changed = true; }
            y += FieldHeight + 2;

            // Blades per cell
            int newBlades = _eb.DrawIntField("grass_blades", "Blades/cell", gt.Blades, panelX + Margin, y, fw);
            newBlades = Math.Max(1, newBlades);
            if (newBlades != gt.Blades) { gt.Blades = newBlades; changed = true; }
            y += FieldHeight + 2;

            if (changed) _onGrassMapChanged?.Invoke();
        }
        else if (!_grassEraserSelected && SelectedGrassType >= 0 && SelectedGrassType < _grassTypes.Count)
        {
            // Fallback if no EditorBase
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
        // Access rightDown/rightUp from mouse state
        bool rightDown = mouse.RightButton == ButtonState.Pressed;
        bool rightUp = mouse.RightButton == ButtonState.Released && _prevMouse.RightButton == ButtonState.Pressed;

        if (leftClick && overPanel)
        {
            int contentY = panelY + TabRowHeight * 2 + HeaderHeight + 6;

            // Skip past "Edit Defs" button area (handled by EditorBase.DrawButton)
            if (_eb != null && _envObjectEditor != null)
                contentY += ButtonHeight + 4;

            // Category buttons — wrapping layout (must match DrawObjectsTab)
            var categories = GetEnvCategories();
            int availW = PanelWidth - Margin * 2;
            int catBtnW = Math.Max(56, availW / Math.Clamp(categories.Count, 1, 5));
            int catsPerRow = Math.Max(1, availW / catBtnW);
            int catRows = (categories.Count + catsPerRow - 1) / catsPerRow;
            int catTotalH = catRows * (ButtonHeight + 2) + 2;
            if (mouse.Y >= contentY && mouse.Y < contentY + catTotalH)
            {
                int relX = mouse.X - (panelX + Margin);
                int row = (mouse.Y - contentY) / (ButtonHeight + 2);
                int col = relX / catBtnW;
                int catIdx = row * catsPerRow + col;
                if (catIdx >= 0 && catIdx < categories.Count)
                    SelectedEnvCategory = catIdx;
            }

            // Mode toggle
            int modeY = contentY + catTotalH;
            int halfW = (PanelWidth - Margin * 2) / 2;
            if (mouse.Y >= modeY && mouse.Y < modeY + ButtonHeight)
            {
                int relX = mouse.X - (panelX + Margin);
                if (relX >= 0 && relX < halfW) _objectPaintMode = false;
                else if (relX >= halfW && relX < halfW * 2) _objectPaintMode = true;
            }

            // Def list (or group list for M17)
            int listY = modeY + ButtonHeight + 4;
            var filteredDefs = GetFilteredEnvDefs(categories);
            // M17: If category is "Groups", show group list instead
            bool isGroupMode = SelectedEnvCategory < categories.Count && categories[SelectedEnvCategory] == "Groups";
            if (isGroupMode)
            {
                var groups = GetEnvGroups();
                for (int i = 0; i < groups.Count; i++)
                {
                    int itemY = listY + i * (ButtonHeight + 2) - (int)_envListScroll;
                    if (mouse.Y >= itemY && mouse.Y < itemY + ButtonHeight)
                    {
                        // Store group index as negative to differentiate from def index
                        SelectedEnvDefIndex = -(i + 1); // -1 = group 0, -2 = group 1, etc.
                        break;
                    }
                }
            }
            else
            {
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
        }

        // Scroll env list
        int scrollDelta2 = mouse.ScrollWheelValue - _prevScrollValue;
        if (scrollDelta2 != 0 && overPanel)
            _envListScroll = MathF.Max(0, _envListScroll - scrollDelta2 * 0.2f);

        // M17: Resolve def index (may be a group selection using weighted random)
        int resolvedDefIndex = ResolveObjectDefIndex();

        // Place/paint on world
        if (resolvedDefIndex >= 0 || SelectedEnvDefIndex < -0) // allow group mode too
        {
            if (!_objectPaintMode)
            {
                // Single mode: click to place
                if (leftClick && !overPanel)
                {
                    int defToPlace = ResolveObjectDefIndex();
                    if (defToPlace >= 0)
                    {
                        Vec2 worldPos = _camera.ScreenToWorld(new Vector2(mouse.X, mouse.Y), screenW, screenH);
                        // RM03: Check collision radius overlap before placing
                        if (_envSystem.CanPlaceObject(defToPlace, worldPos.X, worldPos.Y))
                        {
                            int newIdx = _envSystem.AddObject((ushort)defToPlace, worldPos.X, worldPos.Y);
                            PushUndo(new UndoObjectPlace { Env = _envSystem, ObjectIndex = newIdx });
                            AutoCreateTriggerInstance(newIdx); // RM06
                            RebakeObjectCollisions(); // RM04
                        }
                    }
                }
            }
            else
            {
                // M28: Paint mode with batch undo
                if (leftDown && !overPanel)
                {
                    if (_batchPlacedObjects == null)
                        _batchPlacedObjects = new();
                    PaintObjectsBatch(mouse, screenW, screenH);
                }
                if (leftUp && _batchPlacedObjects != null)
                {
                    if (_batchPlacedObjects.Count > 0)
                    {
                        PushUndo(new UndoObjectBatchPlace
                        {
                            Env = _envSystem,
                            ObjectIndices = new List<int>(_batchPlacedObjects.Select(b => b.objIdx))
                        });
                        RebakeObjectCollisions(); // RM04
                    }
                    _batchPlacedObjects = null;
                }
            }
        }

        // Right-click to remove nearest (single or batch)
        if (!_objectPaintMode)
        {
            // Single remove
            if (rightClick && !overPanel)
            {
                Vec2 worldPos = _camera.ScreenToWorld(new Vector2(mouse.X, mouse.Y), screenW, screenH);
                int closest = FindClosestObject(worldPos, 3f);
                if (closest >= 0)
                {
                    var obj = _envSystem.GetObject(closest);
                    PushUndo(new UndoObjectRemove
                    {
                        Env = _envSystem,
                        DefIndex = obj.DefIndex,
                        X = obj.X, Y = obj.Y,
                        Scale = obj.Scale, Seed = obj.Seed
                    });
                    AutoRemoveTriggerInstance(closest); // RM07
                    _envSystem.RemoveObject(closest);
                    RebakeObjectCollisions(); // RM04
                }
            }
        }
        else
        {
            // M28: Batch remove in paint mode (right-click drag)
            if (rightDown && !overPanel)
            {
                if (_batchRemovedObjects == null)
                    _batchRemovedObjects = new();
                Vec2 worldPos = _camera.ScreenToWorld(new Vector2(mouse.X, mouse.Y), screenW, screenH);
                int closest = FindClosestObject(worldPos, 3f);
                if (closest >= 0)
                {
                    var obj = _envSystem.GetObject(closest);
                    _batchRemovedObjects.Add((obj.DefIndex, obj.X, obj.Y, obj.Scale, obj.Seed));
                    AutoRemoveTriggerInstance(closest); // RM07
                    _envSystem.RemoveObject(closest);
                }
            }
            if (rightUp && _batchRemovedObjects != null)
            {
                if (_batchRemovedObjects.Count > 0)
                {
                    PushUndo(new UndoObjectBatchRemove
                    {
                        Env = _envSystem,
                        Removed = new(_batchRemovedObjects)
                    });
                    RebakeObjectCollisions(); // RM04
                }
                _batchRemovedObjects = null;
            }
        }
    }

    /// <summary>
    /// M17: Resolve the currently selected def index, handling group mode with weighted random.
    /// Returns -1 if nothing valid is selected.
    /// </summary>
    private int ResolveObjectDefIndex()
    {
        if (SelectedEnvDefIndex >= 0)
            return SelectedEnvDefIndex;

        // Group mode: SelectedEnvDefIndex is -(groupIndex+1)
        if (SelectedEnvDefIndex < 0)
        {
            int groupIdx = -(SelectedEnvDefIndex + 1);
            var groups = GetEnvGroups();
            if (groupIdx < 0 || groupIdx >= groups.Count) return -1;
            string groupName = groups[groupIdx];

            // Collect defs in this group with weights
            float totalWeight = 0;
            var candidates = new List<(int defIdx, float weight)>();
            for (int i = 0; i < _envSystem.DefCount; i++)
            {
                var def = _envSystem.GetDef(i);
                if (def.Group == groupName)
                {
                    float w = Math.Max(0.001f, def.GroupWeight);
                    candidates.Add((i, w));
                    totalWeight += w;
                }
            }
            if (candidates.Count == 0) return -1;

            // Weighted random selection
            float roll = Random.Shared.NextSingle() * totalWeight;
            float accum = 0;
            foreach (var (defIdx, weight) in candidates)
            {
                accum += weight;
                if (roll <= accum) return defIdx;
            }
            return candidates[^1].defIdx;
        }

        return -1;
    }

    /// <summary>
    /// M28: Paint objects with batch accumulation for undo.
    /// </summary>
    private void PaintObjectsBatch(MouseState mouse, int screenW, int screenH)
    {
        int defToPlace = ResolveObjectDefIndex();
        if (defToPlace < 0) return;
        Vec2 worldPos = _camera.ScreenToWorld(new Vector2(mouse.X, mouse.Y), screenW, screenH);
        var def = _envSystem.GetDef(defToPlace);
        // RM05: Use collision radius in spacing calculation (fallback to PlacementScale)
        float colRadius = def.CollisionRadius > 0 ? def.CollisionRadius : def.PlacementScale;
        float spacing = Math.Max(1f, colRadius * 2.2f);

        for (int dy = -BrushRadius; dy <= BrushRadius; dy++)
        {
            for (int dx = -BrushRadius; dx <= BrushRadius; dx++)
            {
                if (dx * dx + dy * dy > BrushRadius * BrushRadius) continue;
                float ox = dx * spacing + (dy % 2 == 0 ? 0 : spacing * 0.5f);
                float oy = dy * spacing * 0.866f;

                // RM05: Add random jitter to prevent grid-like appearance
                float jitter = spacing * 0.25f;
                ox += (Random.Shared.NextSingle() - 0.5f) * 2f * jitter;
                oy += (Random.Shared.NextSingle() - 0.5f) * 2f * jitter;

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
                // RM03: Also check collision radius overlap
                if (!tooClose && !_envSystem.CanPlaceObject(defToPlace, px, py))
                    tooClose = true;

                if (!tooClose)
                {
                    int newIdx = _envSystem.AddObject((ushort)defToPlace, px, py);
                    _batchPlacedObjects?.Add(((ushort)defToPlace, px, py, 1f, 0f, newIdx));
                    AutoCreateTriggerInstance(newIdx); // RM06
                }
            }
        }
    }

    private void PaintObjects(MouseState mouse, int screenW, int screenH)
    {
        if (SelectedEnvDefIndex < 0) return;
        Vec2 worldPos = _camera.ScreenToWorld(new Vector2(mouse.X, mouse.Y), screenW, screenH);
        var def = _envSystem.GetDef(SelectedEnvDefIndex);
        // RM05: Use collision radius in spacing calculation
        float colRadius = def.CollisionRadius > 0 ? def.CollisionRadius : def.PlacementScale;
        float spacing = Math.Max(1f, colRadius * 2.2f);

        for (int dy = -BrushRadius; dy <= BrushRadius; dy++)
        {
            for (int dx = -BrushRadius; dx <= BrushRadius; dx++)
            {
                if (dx * dx + dy * dy > BrushRadius * BrushRadius) continue;
                // Hex-grid offset
                float ox = dx * spacing + (dy % 2 == 0 ? 0 : spacing * 0.5f);
                float oy = dy * spacing * 0.866f; // sqrt(3)/2

                // RM05: Random jitter
                float jitter = spacing * 0.25f;
                ox += (Random.Shared.NextSingle() - 0.5f) * 2f * jitter;
                oy += (Random.Shared.NextSingle() - 0.5f) * 2f * jitter;

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
                // RM03: Check collision overlap
                if (!tooClose && !_envSystem.CanPlaceObject(SelectedEnvDefIndex, px, py))
                    tooClose = true;

                if (!tooClose)
                {
                    int newIdx = _envSystem.AddObject((ushort)SelectedEnvDefIndex, px, py);
                    AutoCreateTriggerInstance(newIdx); // RM06
                }
            }
        }
        RebakeObjectCollisions(); // RM04
    }

    private void DrawObjectsTab(int panelX, int contentY, int contentH, int screenW, int screenH)
    {
        // Save original top of content area for bottom-anchored sections
        int contentTop = contentY;

        var categories = GetEnvCategories();
        DrawSectionHeader(panelX, ref contentY, $"Objects ({_envSystem.DefCount} defs, {_envSystem.ObjectCount} placed)");

        // "Edit Defs" button to open the EnvObjectEditor overlay
        if (_eb != null && _envObjectEditor != null)
        {
            if (_eb.DrawButton("Edit Defs", panelX + Margin, contentY, PanelWidth - Margin * 2, ButtonHeight, EditorBase.AccentColor))
            {
                Core.DebugLog.Log("editor", $"Edit Defs clicked, opening EnvObjectEditor (defCount={_envSystem.DefCount})");
                _envObjectEditor.Open();
            }
            contentY += ButtonHeight + 4;
        }

        var mouse = Mouse.GetState();

        // Category buttons — wrap to multiple rows if they exceed panel width
        int availW = PanelWidth - Margin * 2;
        int catBtnW = Math.Max(56, availW / Math.Clamp(categories.Count, 1, 5));
        int catsPerRow = Math.Max(1, availW / catBtnW);
        int catRows = (categories.Count + catsPerRow - 1) / catsPerRow;
        for (int i = 0; i < categories.Count; i++)
        {
            int col = i % catsPerRow;
            int row = i / catsPerRow;
            bool active = i == SelectedEnvCategory;
            var btnRect = new Rectangle(panelX + Margin + col * catBtnW, contentY + row * (ButtonHeight + 2), catBtnW - 2, ButtonHeight);
            var bg = active ? TabActiveColor : (IsInRect(mouse, btnRect) ? ButtonHoverColor : TabInactiveColor);
            _spriteBatch.Draw(_pixel, btnRect, bg);
            DrawTextCentered(categories[i], btnRect, TextColor);
        }
        contentY += catRows * (ButtonHeight + 2) + 2;

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

        // M17: Check if category is "Groups"
        bool isGroupMode = SelectedEnvCategory < categories.Count && categories[SelectedEnvCategory] == "Groups";
        int listAreaH = contentH - (contentY - contentTop) - 160;

        if (isGroupMode)
        {
            // M17: Draw group list instead of individual defs
            var groups = GetEnvGroups();
            for (int i = 0; i < groups.Count; i++)
            {
                int itemY = contentY + i * (ButtonHeight + 2) - (int)_envListScroll;
                if (itemY < contentY - ButtonHeight || itemY > contentY + listAreaH) continue;

                bool selected = SelectedEnvDefIndex == -(i + 1);
                var btnRect = new Rectangle(panelX + Margin, itemY, PanelWidth - Margin * 2, ButtonHeight);

                var bgColor = selected ? HighlightColor : (IsInRect(mouse, btnRect) ? ButtonHoverColor : Color.Transparent);
                if (bgColor != Color.Transparent)
                    _spriteBatch.Draw(_pixel, btnRect, bgColor);

                // Count defs in group
                int defCount = 0;
                for (int d = 0; d < _envSystem.DefCount; d++)
                    if (_envSystem.GetDef(d).Group == groups[i]) defCount++;

                DrawSmallText($"{groups[i]} ({defCount} defs)", panelX + Margin + 4, itemY + 3, selected ? TextBright : TextColor);
            }

            // Selected group info
            if (SelectedEnvDefIndex < 0)
            {
                int groupIdx = -(SelectedEnvDefIndex + 1);
                if (groupIdx >= 0 && groupIdx < groups.Count)
                {
                    int propY = contentTop + contentH - 140;
                    _spriteBatch.Draw(_pixel, new Rectangle(panelX, propY - 4, PanelWidth, 1), SeparatorColor);
                    propY += 2;
                    DrawSmallText($"Group: {groups[groupIdx]}", panelX + Margin, propY, TextBright); propY += LineHeight;
                    DrawSmallText("Uses weighted random selection", panelX + Margin, propY, TextColor); propY += LineHeight;
                    DrawSmallText(_objectPaintMode ? "Left-drag to paint (random)" : "Click to place (random)", panelX + Margin, propY, TextDim);
                }
            }
        }
        else
        {
            // Normal def list
            var filteredDefs = GetFilteredEnvDefs(categories);

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

                // RM09: Display as "[category] name" instead of "name [B]"
                string label = $"[{def.Category}] {def.Name}";
                DrawSmallText(label, panelX + Margin + 4, itemY + 3, selected ? TextBright : TextColor);
            }

            // Selected def properties
            if (SelectedEnvDefIndex >= 0 && SelectedEnvDefIndex < _envSystem.DefCount)
            {
                int propY = contentTop + contentH - 140;
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
        }

        // Show Collisions checkbox
        if (_eb != null)
        {
            int checkY = contentTop + contentH - 48;
            _showCollisions = _eb.DrawCheckbox("Show Collisions", _showCollisions, panelX + Margin, checkY);
        }

        // Brush size (for paint mode)
        if (_objectPaintMode)
        {
            int brushY = contentTop + contentH - 22;
            DrawBrushSizeControl(panelX, brushY);
        }

        // Brush cursor for paint mode
        if (_objectPaintMode && !IsMouseOverPanel(screenW, screenH))
            DrawBrushCursor(screenW, screenH);

        // RM08: Draw collision overlay with isometric ellipses
        if (_showCollisions)
            DrawCollisionOverlay(screenW, screenH);
    }

    /// <summary>Whether the "Show Collisions" checkbox is active on the Objects tab.</summary>
    public bool ShowObjectCollisions => _showCollisions;

    /// <summary>
    /// RM08: Draw isometric ellipses for each placed object's collision radius.
    /// Y is compressed by camera YRatio. When Alt is held, show object names.
    /// </summary>
    private void DrawCollisionOverlay(int screenW, int screenH)
    {
        var kb = Keyboard.GetState();
        bool showNames = kb.IsKeyDown(Keys.LeftAlt) || kb.IsKeyDown(Keys.RightAlt);
        var collisionColor = new Color(255, 100, 100, 120);
        var nameColor = new Color(255, 220, 100, 220);

        for (int i = 0; i < _envSystem.ObjectCount; i++)
        {
            var obj = _envSystem.GetObject(i);
            if (obj.DefIndex < 0 || obj.DefIndex >= _envSystem.DefCount) continue;
            var def = _envSystem.GetDef(obj.DefIndex);
            if (def.CollisionRadius <= 0) continue;

            float worldCx = obj.X + def.CollisionOffsetX;
            float worldCy = obj.Y + def.CollisionOffsetY;
            float radius = def.CollisionRadius * obj.Scale;

            var center = _camera.WorldToScreen(new Vec2(worldCx, worldCy), 0, screenW, screenH);
            float radiusX = radius * _camera.Zoom;
            float radiusY = radius * _camera.Zoom * _camera.YRatio; // Compress Y for isometric

            int segments = Math.Max(16, (int)(radiusX * 0.5f));
            DrawEllipseOutline(center, radiusX, radiusY, collisionColor, segments);

            if (showNames)
            {
                DrawSmallText(def.Name, (int)(center.X - 20), (int)(center.Y - radiusY - 14), nameColor);
            }
        }
    }

    /// <summary>
    /// RM08: Draw an ellipse outline (used for isometric collision circles).
    /// </summary>
    private void DrawEllipseOutline(Vector2 center, float radiusX, float radiusY, Color color, int segments)
    {
        float step = MathF.PI * 2f / segments;
        for (int i = 0; i < segments; i++)
        {
            float a1 = i * step;
            float a2 = (i + 1) * step;
            var p1 = center + new Vector2(MathF.Cos(a1) * radiusX, MathF.Sin(a1) * radiusY);
            var p2 = center + new Vector2(MathF.Cos(a2) * radiusX, MathF.Sin(a2) * radiusY);
            DrawLine(p1, p2, color);
        }
    }

    // ====================================================================
    //  WALLS TAB
    // ====================================================================

    private void UpdateWallsTab(MouseState mouse, KeyboardState kb, bool leftClick, bool leftDown, bool leftUp,
        bool rightClick, bool rightDown, bool rightUp,
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

        // Paint walls (left-click uses selected type)
        if (leftDown && !overPanel)
        {
            if (!_painting)
                _wallStrokeOld = new Dictionary<int, (byte type, short hp)>();
            _painting = true;
            PaintWalls(mouse, screenW, screenH);
        }
        if (leftUp)
        {
            if (_painting)
            {
                if (_wallStrokeOld != null && _wallStrokeOld.Count > 0)
                {
                    PushUndo(new UndoWallStroke
                    {
                        Walls = _wallSystem,
                        OldValues = _wallStrokeOld,
                        WallWidth = _wallSystem.Width
                    });
                }
                _wallStrokeOld = null;
                // Rebuild cost field and bake walls after painting
                _tileGrid.RebuildCostField();
                _wallSystem.BakeWalls(_tileGrid);
            }
            _painting = false;
        }

        // RM11: Right-click to erase walls regardless of selected type
        if (rightDown && !overPanel)
        {
            if (!_painting)
                _wallStrokeOld = new Dictionary<int, (byte type, short hp)>();
            _painting = true;
            EraseWalls(mouse, screenW, screenH);
        }
        if (rightUp)
        {
            if (_painting)
            {
                if (_wallStrokeOld != null && _wallStrokeOld.Count > 0)
                {
                    PushUndo(new UndoWallStroke
                    {
                        Walls = _wallSystem,
                        OldValues = _wallStrokeOld,
                        WallWidth = _wallSystem.Width
                    });
                }
                _wallStrokeOld = null;
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

        var types = _wallSystem.GetTypes();
        var hps = _wallSystem.GetHPArray();

        for (int dy = -BrushRadius; dy <= BrushRadius; dy++)
        {
            for (int dx = -BrushRadius; dx <= BrushRadius; dx++)
            {
                if (dx * dx + dy * dy > BrushRadius * BrushRadius) continue;
                int tx = wx + dx * WallSystem.WallStep;
                int ty = wy + dy * WallSystem.WallStep;
                if (!_wallSystem.InBounds(tx, ty)) continue;

                int idx = ty * _wallSystem.Width + tx;
                _wallStrokeOld?.TryAdd(idx, (types[idx], hps[idx]));

                if (SelectedWallType == 0)
                    _wallSystem.ClearWall(tx, ty);
                else
                    _wallSystem.SetWall(tx, ty, (byte)SelectedWallType);
            }
        }
    }

    /// <summary>
    /// RM11: Erase walls under the brush, regardless of selected wall type.
    /// </summary>
    private void EraseWalls(MouseState mouse, int screenW, int screenH)
    {
        Vec2 worldPos = _camera.ScreenToWorld(new Vector2(mouse.X, mouse.Y), screenW, screenH);
        int wx = WallSystem.SnapToWallGrid((int)MathF.Round(worldPos.X));
        int wy = WallSystem.SnapToWallGrid((int)MathF.Round(worldPos.Y));

        var types = _wallSystem.GetTypes();
        var hps = _wallSystem.GetHPArray();

        for (int dy = -BrushRadius; dy <= BrushRadius; dy++)
        {
            for (int dx = -BrushRadius; dx <= BrushRadius; dx++)
            {
                if (dx * dx + dy * dy > BrushRadius * BrushRadius) continue;
                int tx = wx + dx * WallSystem.WallStep;
                int ty = wy + dy * WallSystem.WallStep;
                if (!_wallSystem.InBounds(tx, ty)) continue;

                int idx = ty * _wallSystem.Width + tx;
                _wallStrokeOld?.TryAdd(idx, (types[idx], hps[idx]));
                _wallSystem.ClearWall(tx, ty);
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

        // Show Debug checkbox
        if (_eb != null)
        {
            _showWallDebug = _eb.DrawCheckbox("Show Debug", _showWallDebug, panelX + Margin, y);
            y += FieldHeight + 4;
        }

        // Edit Wall Defs button
        if (_eb != null && _wallEditor != null)
        {
            if (_eb.DrawButton("Edit Wall Defs", panelX + Margin, y, PanelWidth - Margin * 2, ButtonHeight))
            {
                _wallEditor.SetMapFilename(_mapFilename);
                _wallEditor.Open(Math.Max(0, SelectedWallType - 1));
            }
            y += ButtonHeight + 4;
        }

        // Info
        DrawSmallText($"Grid: {_wallSystem.Width}x{_wallSystem.Height} step={WallSystem.WallStep}", panelX + Margin, y, TextDim);
        y += LineHeight;
        DrawSmallText("Left-drag to paint, Right-drag to erase", panelX + Margin, y, TextDim);

        // Brush cursor
        if (!IsMouseOverPanel(screenW, screenH))
            DrawBrushCursor(screenW, screenH);
    }

    /// <summary>Whether the "Show Debug" checkbox is active on the Walls tab.</summary>
    public bool ShowWallDebug => _showWallDebug;

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
            // Add Junction (at camera center)
            if (mouse.Y >= juncY && mouse.Y < juncY + ButtonHeight &&
                mouse.X >= panelX + Margin && mouse.X < panelX + 100 + Margin)
            {
                Vec2 camPos = _camera.Position;
                int newJuncIdx = _roadSystem.AddJunction(camPos);
                SelectedJunctionIndex = newJuncIdx;
                // RM16: Set textureDefIndex to the selected road's texture
                if (newJuncIdx >= 0 && SelectedRoadIndex >= 0 && SelectedRoadIndex < _roadSystem.RoadCount)
                {
                    var junc = _roadSystem.GetJunction(newJuncIdx);
                    junc.TextureDefIndex = _roadSystem.GetRoad(SelectedRoadIndex).TextureDefIndex;
                }
            }

            // RM15: Toggle click-to-place junction mode (same row as Add Junction)
            if (mouse.Y >= juncY && mouse.Y < juncY + ButtonHeight &&
                mouse.X >= panelX + Margin + 110 && mouse.X < panelX + Margin + 230)
            {
                _junctionPlaceMode = !_junctionPlaceMode;
            }
        }

        // World interaction
        if (!overPanel)
        {
            Vec2 worldPos = _camera.ScreenToWorld(new Vector2(mouse.X, mouse.Y), screenW, screenH);

            // RM15: Click-to-place junction in viewport
            if (_junctionPlaceMode && leftClick)
            {
                int newJuncIdx = _roadSystem.AddJunction(worldPos);
                SelectedJunctionIndex = newJuncIdx;
                // RM16: Set textureDefIndex to the selected road's texture
                if (newJuncIdx >= 0 && SelectedRoadIndex >= 0 && SelectedRoadIndex < _roadSystem.RoadCount)
                {
                    var junc = _roadSystem.GetJunction(newJuncIdx);
                    junc.TextureDefIndex = _roadSystem.GetRoad(SelectedRoadIndex).TextureDefIndex;
                }
                _junctionPlaceMode = false;
            }

            // Place mode: click to add control points
            if (_roadPlaceMode && leftClick && SelectedRoadIndex >= 0)
            {
                var road = _roadSystem.GetRoad(SelectedRoadIndex);
                road.Points.Add(new RoadControlPoint { Position = worldPos, Width = 2f });
                // TODO RM13: Call road system mesh rebuild once a cached mesh pipeline exists
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
                if (toRemove >= 0)
                {
                    road.Points.RemoveAt(toRemove);
                    // TODO RM13: Call road system mesh rebuild once a cached mesh pipeline exists
                }
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

        // Selected road properties (editable)
        if (SelectedRoadIndex >= 0 && SelectedRoadIndex < _roadSystem.RoadCount)
        {
            _spriteBatch.Draw(_pixel, new Rectangle(panelX, y, PanelWidth, 1), SeparatorColor);
            y += 4;
            var road = _roadSystem.GetRoad(SelectedRoadIndex);
            int fw = PanelWidth - Margin * 2;

            if (_eb != null)
            {
                string newName = _eb.DrawTextField("road_name", "Name", road.Name, panelX + Margin, y, fw);
                if (newName != road.Name) road.Name = newName;
                y += FieldHeight + 2;

                // Road texture selection via browse button
                int texDefIdx = road.TextureDefIndex;
                string roadTexPath = (texDefIdx >= 0 && texDefIdx < _roadSystem.TextureDefCount)
                    ? _roadSystem.GetTextureDef(texDefIdx).TexturePath : "";
                int rdBrowseBtnW = 55;
                string newRoadTexPath = _eb.DrawTextField("road_texpath", "Texture", roadTexPath, panelX + Margin, y, fw - rdBrowseBtnW - 4);
                if (newRoadTexPath != roadTexPath && texDefIdx >= 0 && texDefIdx < _roadSystem.TextureDefCount)
                    _roadSystem.GetTextureDef(texDefIdx).TexturePath = newRoadTexPath;
                if (_eb.DrawButton("Browse", panelX + Margin + fw - rdBrowseBtnW, y, rdBrowseBtnW, 20))
                {
                    int capturedIdx = texDefIdx;
                    _textureBrowser.Open("assets/Environment/Roads", roadTexPath, path =>
                    {
                        if (capturedIdx >= 0 && capturedIdx < _roadSystem.TextureDefCount)
                            _roadSystem.GetTextureDef(capturedIdx).TexturePath = path;
                    });
                }
                y += FieldHeight + 2;

                int newOrder = _eb.DrawIntField("road_order", "Render Order", road.RenderOrder, panelX + Margin, y, fw);
                if (newOrder != road.RenderOrder) road.RenderOrder = newOrder;
                y += FieldHeight + 2;

                float newTexScale = _eb.DrawFloatField("road_texScale", "Tex Scale", road.TextureScale, panelX + Margin, y, fw, 0.05f);
                if (MathF.Abs(newTexScale - road.TextureScale) > 0.001f) road.TextureScale = newTexScale;
                y += FieldHeight + 2;

                float newEdgeSoft = _eb.DrawFloatField("road_edgeSoft", "Edge Softness", road.EdgeSoftness, panelX + Margin, y, fw, 0.01f);
                if (MathF.Abs(newEdgeSoft - road.EdgeSoftness) > 0.001f) road.EdgeSoftness = newEdgeSoft;
                y += FieldHeight + 2;

                // Rim section
                DrawSmallText("Rim:", panelX + Margin, y, AccentColor); y += LineHeight;

                float newRimW = _eb.DrawFloatField("road_rimW", "  Rim Width", road.RimWidth, panelX + Margin, y, fw, 0.1f);
                if (MathF.Abs(newRimW - road.RimWidth) > 0.001f) road.RimWidth = newRimW;
                y += FieldHeight + 2;

                float newRimTile = _eb.DrawFloatField("road_rimTile", "  Rim Tile Rate", road.RimTextureScale, panelX + Margin, y, fw, 0.1f);
                if (MathF.Abs(newRimTile - road.RimTextureScale) > 0.001f) road.RimTextureScale = newRimTile;
                y += FieldHeight + 2;

                float newRimEdge = _eb.DrawFloatField("road_rimEdge", "  Rim Softness", road.RimEdgeSoftness, panelX + Margin, y, fw, 0.01f);
                if (MathF.Abs(newRimEdge - road.RimEdgeSoftness) > 0.001f) road.RimEdgeSoftness = newRimEdge;
                y += FieldHeight + 2;

                // New Point Width
                float newPtW = _eb.DrawFloatField("road_newPtW", "New Pt Width", 2f, panelX + Margin, y, fw, 0.1f);
                y += FieldHeight + 2;

                DrawSmallText($"Points: {road.Points.Count}", panelX + Margin, y, TextDim); y += LineHeight;
            }
            else
            {
                DrawSmallText($"Name: {road.Name}", panelX + Margin, y, TextBright); y += LineHeight;
                DrawSmallText($"Texture: {road.TextureDefIndex}", panelX + Margin, y, TextColor); y += LineHeight;
                DrawSmallText($"Order: {road.RenderOrder}", panelX + Margin, y, TextColor); y += LineHeight;
                DrawSmallText($"EdgeSoftness: {road.EdgeSoftness:F2}", panelX + Margin, y, TextColor); y += LineHeight;
                DrawSmallText($"TexScale: {road.TextureScale:F2}", panelX + Margin, y, TextColor); y += LineHeight;
                DrawSmallText($"Rim: w={road.RimWidth:F1} tex={road.RimTextureDefIndex}", panelX + Margin, y, TextColor); y += LineHeight;
                DrawSmallText($"Points: {road.Points.Count}", panelX + Margin, y, TextColor); y += LineHeight;
            }
        }

        // M15: Road Texture Defs section - editable names
        y += 4;
        _spriteBatch.Draw(_pixel, new Rectangle(panelX, y, PanelWidth, 1), SeparatorColor);
        y += 4;
        DrawSmallText($"Road Texture Defs ({_roadSystem.TextureDefCount})", panelX + Margin, y, AccentColor);
        y += LineHeight;

        if (_eb != null)
        {
            for (int tdi = 0; tdi < _roadSystem.TextureDefCount; tdi++)
            {
                if (y > contentY + contentH - 200) break;
                var td = _roadSystem.GetTextureDef(tdi);
                int fw = PanelWidth - Margin * 2;

                string newTdName = _eb.DrawTextField($"road_texdef_name_{tdi}", $"Tex[{tdi}] Name", td.Name, panelX + Margin, y, fw);
                if (newTdName != td.Name) td.Name = newTdName;
                y += FieldHeight + 2;
            }
        }
        else
        {
            for (int tdi = 0; tdi < _roadSystem.TextureDefCount; tdi++)
            {
                var td = _roadSystem.GetTextureDef(tdi);
                DrawSmallText($"  [{tdi}] {td.Name} - {td.TexturePath}", panelX + Margin, y, TextColor);
                y += LineHeight;
            }
        }

        // Junctions header
        y += 4;
        _spriteBatch.Draw(_pixel, new Rectangle(panelX, y, PanelWidth, 1), SeparatorColor);
        y += 4;
        DrawSmallText("Junctions", panelX + Margin, y, AccentColor); y += LineHeight;
        DrawButtonRect("+ Add Junction", panelX + Margin, y, 120, ButtonHeight, ButtonBg);
        // RM15: Click-to-place junction button
        DrawButtonRect(_junctionPlaceMode ? "[Placing...]" : "Place on Map", panelX + Margin + 110, y, 120, ButtonHeight,
            _junctionPlaceMode ? AccentColor : ButtonBg);
        y += ButtonHeight + 4;

        for (int i = 0; i < _roadSystem.JunctionCount; i++)
        {
            if (y > contentY + contentH - 120) break;
            var junc = _roadSystem.GetJunction(i);
            bool selected = i == SelectedJunctionIndex;
            DrawSmallText($"{(selected ? "> " : "  ")}{junc.Name} ({junc.Position.X:F0},{junc.Position.Y:F0}) r={junc.Radius:F1}",
                panelX + Margin, y, selected ? TextBright : TextColor);
            y += LineHeight;
        }

        // Selected junction editable properties
        if (SelectedJunctionIndex >= 0 && SelectedJunctionIndex < _roadSystem.JunctionCount && _eb != null)
        {
            y += 4;
            _spriteBatch.Draw(_pixel, new Rectangle(panelX, y, PanelWidth, 1), SeparatorColor);
            y += 4;
            var junc = _roadSystem.GetJunction(SelectedJunctionIndex);
            int fw = PanelWidth - Margin * 2;

            string jName = _eb.DrawTextField("junc_name", "Junc Name", junc.Name, panelX + Margin, y, fw);
            if (jName != junc.Name) junc.Name = jName;
            y += FieldHeight + 2;

            float jRadius = _eb.DrawFloatField("junc_radius", "Radius", junc.Radius, panelX + Margin, y, fw, 0.5f);
            if (MathF.Abs(jRadius - junc.Radius) > 0.001f) junc.Radius = jRadius;
            y += FieldHeight + 2;

            float jTexScale = _eb.DrawFloatField("junc_texScale", "Tex Scale", junc.TextureScale, panelX + Margin, y, fw, 0.05f);
            if (MathF.Abs(jTexScale - junc.TextureScale) > 0.001f) junc.TextureScale = jTexScale;
            y += FieldHeight + 2;

            float jEdgeSoft = _eb.DrawFloatField("junc_edgeSoft", "Edge Softness", junc.EdgeSoftness, panelX + Margin, y, fw, 0.01f);
            if (MathF.Abs(jEdgeSoft - junc.EdgeSoftness) > 0.001f) junc.EdgeSoftness = jEdgeSoft;
            y += FieldHeight + 2;
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

            // Shape toggle button -- compute Y offset from region properties section
            // We use a rough layout calculation: region list + Add/Delete row + separator + 2 fields (Name, ID) + shape button
            if (SelectedRegionIndex >= 0 && SelectedRegionIndex < regions.Count)
            {
                int shapeY = addY + ButtonHeight + 8 + 4 + (FieldHeight + 2) * 2; // after Name + ID fields
                if (mouse.Y >= shapeY && mouse.Y < shapeY + ButtonHeight &&
                    mouse.X >= panelX + Margin && mouse.X < panelX + PanelWidth - Margin)
                {
                    var region = _triggerSystem.RegionsMut[SelectedRegionIndex];
                    region.Shape = region.Shape == RegionShape.Rectangle ? RegionShape.Circle : RegionShape.Rectangle;
                }
            }

            // Patrol route list clicks and buttons - these are drawn below the region properties
            // We need approximate Y positions. Use a simplified approach:
            // scan the patrol route list buttons in the rendered area
            var pr = _triggerSystem.PatrolRoutes;

            // Look for patrol route Add/Delete Route clicks
            // Since the exact Y depends on region properties, we check the patrol route list from the bottom area.
            // A simpler approach: iterate patrol route items and check position.
            // We rely on the fact that patrol routes section is after region properties. Approximate:
            int regionPropsHeight = 0;
            if (SelectedRegionIndex >= 0 && SelectedRegionIndex < regions.Count)
            {
                var reg = regions[SelectedRegionIndex];
                regionPropsHeight = 4 + (FieldHeight + 2) * 2 + ButtonHeight + 2 + (FieldHeight + 2) * 2; // Name + ID + Shape + PosX + PosY
                if (reg.Shape == RegionShape.Rectangle)
                    regionPropsHeight += (FieldHeight + 2) * 2; // HalfW + HalfH
                else
                    regionPropsHeight += (FieldHeight + 2); // Radius
                regionPropsHeight += 4; // spacing
            }

            int prSectionY = addY + ButtonHeight + 8 + regionPropsHeight + 1 + 4 + LineHeight; // separator + header

            // Click on patrol route items
            for (int i = 0; i < pr.Count; i++)
            {
                int prBtnY = prSectionY + i * (ButtonHeight + 2);
                if (mouse.Y >= prBtnY && mouse.Y < prBtnY + ButtonHeight &&
                    mouse.X >= panelX + Margin && mouse.X < panelX + PanelWidth - Margin)
                {
                    SelectedPatrolRoute = i;
                    break;
                }
            }

            // Add Route button
            int addRouteY = prSectionY + pr.Count * (ButtonHeight + 2);
            if (mouse.Y >= addRouteY && mouse.Y < addRouteY + ButtonHeight &&
                mouse.X >= panelX + Margin && mouse.X < panelX + Margin + 100)
            {
                var newRoute = new PatrolRoute
                {
                    Id = $"patrol_{pr.Count}",
                    Name = $"Patrol {pr.Count}"
                };
                _triggerSystem.PatrolRoutesMut.Add(newRoute);
                SelectedPatrolRoute = pr.Count - 1;
            }

            // Delete Route button
            if (mouse.Y >= addRouteY && mouse.Y < addRouteY + ButtonHeight &&
                mouse.X >= panelX + Margin + 110 && mouse.X < panelX + Margin + 210 &&
                SelectedPatrolRoute >= 0 && SelectedPatrolRoute < pr.Count)
            {
                _triggerSystem.PatrolRoutesMut.RemoveAt(SelectedPatrolRoute);
                SelectedPatrolRoute = Math.Min(SelectedPatrolRoute, _triggerSystem.PatrolRoutes.Count - 1);
            }

            // Place WP button - appears below patrol route properties
            if (SelectedPatrolRoute >= 0 && SelectedPatrolRoute < pr.Count)
            {
                // The Place WP button is at the bottom of the patrol section
                // We scan for it roughly
                int wpBtnY = addRouteY + ButtonHeight + 4;
                if (_eb != null)
                    wpBtnY += (FieldHeight + 2) * 3; // Name + ID + Loop
                wpBtnY += LineHeight; // Waypoints header
                wpBtnY += pr[SelectedPatrolRoute].Waypoints.Count * LineHeight; // waypoints
                wpBtnY += 4 + LineHeight; // help text

                if (mouse.Y >= wpBtnY && mouse.Y < wpBtnY + ButtonHeight &&
                    mouse.X >= panelX + Margin && mouse.X < panelX + Margin + 80)
                {
                    _regionPlaceWaypoint = !_regionPlaceWaypoint;
                }
            }
        }

        // World interaction: drag regions (M26: 10 handle types)
        if (!overPanel)
        {
            Vec2 worldPos = _camera.ScreenToWorld(new Vector2(mouse.X, mouse.Y), screenW, screenH);

            if (leftClick && SelectedRegionIndex >= 0 && SelectedRegionIndex < _triggerSystem.Regions.Count)
            {
                var region = _triggerSystem.Regions[SelectedRegionIndex];
                _activeHandle = RegionHandle.None;
                _draggingRegion = -1;

                float handleTol = 1.5f; // world-space tolerance for handle hit

                if (region.Shape == RegionShape.Rectangle)
                {
                    float l = region.X - region.HalfW;
                    float r = region.X + region.HalfW;
                    float t = region.Y - region.HalfH;
                    float b = region.Y + region.HalfH;
                    float mx = region.X;
                    float my = region.Y;

                    // Test corners first (smallest targets)
                    if (Vec2Near(worldPos, new Vec2(l, t), handleTol)) { _activeHandle = RegionHandle.NW; }
                    else if (Vec2Near(worldPos, new Vec2(r, t), handleTol)) { _activeHandle = RegionHandle.NE; }
                    else if (Vec2Near(worldPos, new Vec2(r, b), handleTol)) { _activeHandle = RegionHandle.SE; }
                    else if (Vec2Near(worldPos, new Vec2(l, b), handleTol)) { _activeHandle = RegionHandle.SW; }
                    // Edge midpoints
                    else if (Vec2Near(worldPos, new Vec2(mx, t), handleTol)) { _activeHandle = RegionHandle.N; }
                    else if (Vec2Near(worldPos, new Vec2(r, my), handleTol)) { _activeHandle = RegionHandle.E; }
                    else if (Vec2Near(worldPos, new Vec2(mx, b), handleTol)) { _activeHandle = RegionHandle.S; }
                    else if (Vec2Near(worldPos, new Vec2(l, my), handleTol)) { _activeHandle = RegionHandle.W; }
                    // Body (inside rect)
                    else if (worldPos.X >= l && worldPos.X <= r && worldPos.Y >= t && worldPos.Y <= b)
                    {
                        _activeHandle = RegionHandle.Body;
                    }

                    if (_activeHandle != RegionHandle.None)
                    {
                        _draggingRegion = SelectedRegionIndex;
                        _draggingRegionResize = _activeHandle != RegionHandle.Body;
                    }
                }
                else // Circle
                {
                    float dist = (worldPos - new Vec2(region.X, region.Y)).Length();
                    if (MathF.Abs(dist - region.Radius) < handleTol)
                    {
                        _activeHandle = RegionHandle.CircleRadius;
                        _draggingRegion = SelectedRegionIndex;
                        _draggingRegionResize = true;
                    }
                    else if (dist <= region.Radius)
                    {
                        _activeHandle = RegionHandle.Body;
                        _draggingRegion = SelectedRegionIndex;
                        _draggingRegionResize = false;
                    }
                }
            }

            if (leftDown && _draggingRegion >= 0 && _draggingRegion < _triggerSystem.Regions.Count)
            {
                var region = _triggerSystem.RegionsMut[_draggingRegion];

                switch (_activeHandle)
                {
                    case RegionHandle.Body:
                        region.X = worldPos.X;
                        region.Y = worldPos.Y;
                        break;

                    // Edge midpoints - resize from one side only
                    case RegionHandle.N:
                    {
                        float oldBottom = region.Y + region.HalfH;
                        float newTop = worldPos.Y;
                        if (newTop < oldBottom - 1f)
                        {
                            region.Y = (newTop + oldBottom) / 2f;
                            region.HalfH = (oldBottom - newTop) / 2f;
                        }
                        break;
                    }
                    case RegionHandle.S:
                    {
                        float oldTop = region.Y - region.HalfH;
                        float newBottom = worldPos.Y;
                        if (newBottom > oldTop + 1f)
                        {
                            region.Y = (oldTop + newBottom) / 2f;
                            region.HalfH = (newBottom - oldTop) / 2f;
                        }
                        break;
                    }
                    case RegionHandle.W:
                    {
                        float oldRight = region.X + region.HalfW;
                        float newLeft = worldPos.X;
                        if (newLeft < oldRight - 1f)
                        {
                            region.X = (newLeft + oldRight) / 2f;
                            region.HalfW = (oldRight - newLeft) / 2f;
                        }
                        break;
                    }
                    case RegionHandle.E:
                    {
                        float oldLeft = region.X - region.HalfW;
                        float newRight = worldPos.X;
                        if (newRight > oldLeft + 1f)
                        {
                            region.X = (oldLeft + newRight) / 2f;
                            region.HalfW = (newRight - oldLeft) / 2f;
                        }
                        break;
                    }

                    // Corners - resize from corner
                    case RegionHandle.NW:
                    {
                        float oldRight = region.X + region.HalfW;
                        float oldBottom = region.Y + region.HalfH;
                        float newLeft = worldPos.X;
                        float newTop = worldPos.Y;
                        if (newLeft < oldRight - 1f && newTop < oldBottom - 1f)
                        {
                            region.X = (newLeft + oldRight) / 2f;
                            region.HalfW = (oldRight - newLeft) / 2f;
                            region.Y = (newTop + oldBottom) / 2f;
                            region.HalfH = (oldBottom - newTop) / 2f;
                        }
                        break;
                    }
                    case RegionHandle.NE:
                    {
                        float oldLeft = region.X - region.HalfW;
                        float oldBottom = region.Y + region.HalfH;
                        float newRight = worldPos.X;
                        float newTop = worldPos.Y;
                        if (newRight > oldLeft + 1f && newTop < oldBottom - 1f)
                        {
                            region.X = (oldLeft + newRight) / 2f;
                            region.HalfW = (newRight - oldLeft) / 2f;
                            region.Y = (newTop + oldBottom) / 2f;
                            region.HalfH = (oldBottom - newTop) / 2f;
                        }
                        break;
                    }
                    case RegionHandle.SE:
                    {
                        float oldLeft = region.X - region.HalfW;
                        float oldTop = region.Y - region.HalfH;
                        float newRight = worldPos.X;
                        float newBottom = worldPos.Y;
                        if (newRight > oldLeft + 1f && newBottom > oldTop + 1f)
                        {
                            region.X = (oldLeft + newRight) / 2f;
                            region.HalfW = (newRight - oldLeft) / 2f;
                            region.Y = (oldTop + newBottom) / 2f;
                            region.HalfH = (newBottom - oldTop) / 2f;
                        }
                        break;
                    }
                    case RegionHandle.SW:
                    {
                        float oldRight = region.X + region.HalfW;
                        float oldTop = region.Y - region.HalfH;
                        float newLeft = worldPos.X;
                        float newBottom = worldPos.Y;
                        if (newLeft < oldRight - 1f && newBottom > oldTop + 1f)
                        {
                            region.X = (newLeft + oldRight) / 2f;
                            region.HalfW = (oldRight - newLeft) / 2f;
                            region.Y = (oldTop + newBottom) / 2f;
                            region.HalfH = (newBottom - oldTop) / 2f;
                        }
                        break;
                    }

                    case RegionHandle.CircleRadius:
                        region.Radius = MathF.Max(1f, (worldPos - new Vec2(region.X, region.Y)).Length());
                        break;
                }
            }
            if (leftUp) { _draggingRegion = -1; _activeHandle = RegionHandle.None; }

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

            // RM17: Waypoint dragging - click to start dragging nearest waypoint
            if (leftClick && !_regionPlaceWaypoint && _draggingRegion < 0 &&
                SelectedPatrolRoute >= 0 && SelectedPatrolRoute < _triggerSystem.PatrolRoutes.Count)
            {
                var route = _triggerSystem.PatrolRoutes[SelectedPatrolRoute];
                float bestDist = 2f; // world-space tolerance
                _draggingWaypoint = -1;
                for (int wi = 0; wi < route.Waypoints.Count; wi++)
                {
                    float d = (route.Waypoints[wi] - worldPos).Length();
                    if (d < bestDist) { bestDist = d; _draggingWaypoint = wi; SelectedWaypointIndex = wi; }
                }
            }
            if (leftDown && _draggingWaypoint >= 0 &&
                SelectedPatrolRoute >= 0 && SelectedPatrolRoute < _triggerSystem.PatrolRoutes.Count)
            {
                var routes = _triggerSystem.PatrolRoutesMut;
                if (_draggingWaypoint < routes[SelectedPatrolRoute].Waypoints.Count)
                    routes[SelectedPatrolRoute].Waypoints[_draggingWaypoint] = worldPos;
            }
            if (leftUp) _draggingWaypoint = -1;
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

        // Selected region properties (editable)
        if (SelectedRegionIndex >= 0 && SelectedRegionIndex < regions.Count)
        {
            _spriteBatch.Draw(_pixel, new Rectangle(panelX, y, PanelWidth, 1), SeparatorColor);
            y += 4;
            var region = _triggerSystem.RegionsMut[SelectedRegionIndex];
            int fw = PanelWidth - Margin * 2;

            if (_eb != null)
            {
                string newName = _eb.DrawTextField("region_name", "Name", region.Name, panelX + Margin, y, fw);
                if (newName != region.Name) region.Name = newName;
                y += FieldHeight + 2;

                string newId = _eb.DrawTextField("region_id", "ID", region.Id, panelX + Margin, y, fw);
                if (newId != region.Id) region.Id = newId;
                y += FieldHeight + 2;

                // Shape toggle button
                string shapeLabel = $"Shape: {region.Shape}";
                DrawButtonRect(shapeLabel, panelX + Margin, y, fw, ButtonHeight, ButtonBg);
                y += ButtonHeight + 2;

                float newX = _eb.DrawFloatField("region_x", "Position X", region.X, panelX + Margin, y, fw, 1f);
                if (MathF.Abs(newX - region.X) > 0.001f) region.X = newX;
                y += FieldHeight + 2;

                float newY = _eb.DrawFloatField("region_y", "Position Y", region.Y, panelX + Margin, y, fw, 1f);
                if (MathF.Abs(newY - region.Y) > 0.001f) region.Y = newY;
                y += FieldHeight + 2;

                if (region.Shape == RegionShape.Rectangle)
                {
                    float newHW = _eb.DrawFloatField("region_hw", "Half W", region.HalfW, panelX + Margin, y, fw, 0.5f);
                    if (MathF.Abs(newHW - region.HalfW) > 0.001f) region.HalfW = MathF.Max(0.5f, newHW);
                    y += FieldHeight + 2;

                    float newHH = _eb.DrawFloatField("region_hh", "Half H", region.HalfH, panelX + Margin, y, fw, 0.5f);
                    if (MathF.Abs(newHH - region.HalfH) > 0.001f) region.HalfH = MathF.Max(0.5f, newHH);
                    y += FieldHeight + 2;
                }
                else
                {
                    float newR = _eb.DrawFloatField("region_radius", "Radius", region.Radius, panelX + Margin, y, fw, 0.5f);
                    if (MathF.Abs(newR - region.Radius) > 0.001f) region.Radius = MathF.Max(0.5f, newR);
                    y += FieldHeight + 2;
                }
            }
            else
            {
                DrawSmallText($"ID: {region.Id}", panelX + Margin, y, TextBright); y += LineHeight;
                DrawSmallText($"Name: {region.Name}", panelX + Margin, y, TextColor); y += LineHeight;
                DrawSmallText($"Shape: {region.Shape}", panelX + Margin, y, TextColor); y += LineHeight;
                DrawSmallText($"Pos: ({region.X:F1}, {region.Y:F1})", panelX + Margin, y, TextColor); y += LineHeight;
                if (region.Shape == RegionShape.Rectangle)
                    DrawSmallText($"Half: ({region.HalfW:F1}, {region.HalfH:F1})", panelX + Margin, y, TextColor);
                else
                    DrawSmallText($"Radius: {region.Radius:F1}", panelX + Margin, y, TextColor);
                y += LineHeight;
            }
            y += 4;
        }

        // Patrol Routes section
        _spriteBatch.Draw(_pixel, new Rectangle(panelX, y, PanelWidth, 1), SeparatorColor);
        y += 4;
        DrawSmallText($"Patrol Routes ({_triggerSystem.PatrolRoutes.Count})", panelX + Margin, y, AccentColor);
        y += LineHeight;

        var patrolRoutes = _triggerSystem.PatrolRoutes;
        for (int i = 0; i < patrolRoutes.Count; i++)
        {
            if (y > contentY + contentH - 200) break;
            bool selected = i == SelectedPatrolRoute;
            var prBtnRect = new Rectangle(panelX + Margin, y, PanelWidth - Margin * 2, ButtonHeight);
            var prBg = selected ? HighlightColor : (IsInRect(mouse, prBtnRect) ? ButtonHoverColor : Color.Transparent);
            if (prBg != Color.Transparent)
                _spriteBatch.Draw(_pixel, prBtnRect, prBg);
            DrawSmallText($"{patrolRoutes[i].Name} ({patrolRoutes[i].Waypoints.Count} wps)",
                panelX + Margin + 4, y + 3, selected ? TextBright : TextColor);
            y += ButtonHeight + 2;
        }

        // Add patrol route button region
        if (y < contentY + contentH - ButtonHeight * 3)
        {
            DrawButtonRect("+ Add Route", panelX + Margin, y, 100, ButtonHeight, ButtonBg);
            DrawButtonRect("Delete Route", panelX + Margin + 110, y, 100, ButtonHeight,
                SelectedPatrolRoute >= 0 ? DangerColor : ButtonBg);
            y += ButtonHeight + 4;
        }

        // Selected patrol route editable properties
        if (SelectedPatrolRoute >= 0 && SelectedPatrolRoute < patrolRoutes.Count)
        {
            var route = _triggerSystem.PatrolRoutesMut[SelectedPatrolRoute];
            int fw = PanelWidth - Margin * 2;

            if (_eb != null)
            {
                string prName = _eb.DrawTextField("patrol_name", "Route Name", route.Name, panelX + Margin, y, fw);
                if (prName != route.Name) route.Name = prName;
                y += FieldHeight + 2;

                string prId = _eb.DrawTextField("patrol_id", "Route ID", route.Id, panelX + Margin, y, fw);
                if (prId != route.Id) route.Id = prId;
                y += FieldHeight + 2;

                route.Loop = _eb.DrawCheckbox("Loop", route.Loop, panelX + Margin, y);
                y += FieldHeight + 2;
            }

            // Waypoints list with delete buttons
            DrawSmallText($"Waypoints ({route.Waypoints.Count}):", panelX + Margin, y, AccentColor);
            y += LineHeight;

            for (int wi = 0; wi < route.Waypoints.Count; wi++)
            {
                if (y > contentY + contentH - ButtonHeight) break;
                var wp = route.Waypoints[wi];
                bool wpSel = wi == SelectedWaypointIndex;
                DrawSmallText($"  {(wpSel ? ">" : " ")} ({wp.X:F1}, {wp.Y:F1})", panelX + Margin, y + 3,
                    wpSel ? TextBright : TextColor);

                // Delete waypoint button
                if (_eb != null)
                {
                    if (_eb.DrawButton("X", panelX + PanelWidth - Margin - 24, y, 22, 20, DangerColor))
                    {
                        route.Waypoints.RemoveAt(wi);
                        if (SelectedWaypointIndex >= route.Waypoints.Count)
                            SelectedWaypointIndex = route.Waypoints.Count - 1;
                        break; // list modified, stop drawing
                    }
                }
                y += LineHeight;
            }

            y += 4;
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
        var handleColor = new Color(255, 255, 100, 220);
        var handleActiveColor = new Color(255, 120, 60, 255);
        const int handleSz = 6; // half-size of handle square

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

                // M26: Draw drag handles when selected
                if (selected && rw > 0 && rh > 0)
                {
                    int cx = rx + rw / 2;
                    int cy = ry + rh / 2;

                    // 4 corners
                    DrawRegionHandleSquare(rx, ry, handleSz, _activeHandle == RegionHandle.NW ? handleActiveColor : handleColor);
                    DrawRegionHandleSquare(rx + rw, ry, handleSz, _activeHandle == RegionHandle.NE ? handleActiveColor : handleColor);
                    DrawRegionHandleSquare(rx + rw, ry + rh, handleSz, _activeHandle == RegionHandle.SE ? handleActiveColor : handleColor);
                    DrawRegionHandleSquare(rx, ry + rh, handleSz, _activeHandle == RegionHandle.SW ? handleActiveColor : handleColor);

                    // 4 edge midpoints
                    DrawRegionHandleSquare(cx, ry, handleSz, _activeHandle == RegionHandle.N ? handleActiveColor : handleColor);
                    DrawRegionHandleSquare(rx + rw, cy, handleSz, _activeHandle == RegionHandle.E ? handleActiveColor : handleColor);
                    DrawRegionHandleSquare(cx, ry + rh, handleSz, _activeHandle == RegionHandle.S ? handleActiveColor : handleColor);
                    DrawRegionHandleSquare(rx, cy, handleSz, _activeHandle == RegionHandle.W ? handleActiveColor : handleColor);

                    // Body center indicator
                    DrawRegionHandleSquare(cx, cy, handleSz - 1, _activeHandle == RegionHandle.Body ? handleActiveColor : new Color(100, 255, 100, 180));
                }
            }
            else // Circle
            {
                var center = _camera.WorldToScreen(new Vec2(region.X, region.Y), 0, screenW, screenH);
                float screenRadius = region.Radius * _camera.Zoom;
                int segments = Math.Max(16, (int)(screenRadius * 0.5f));
                var color = selected ? new Color(120, 120, 255, 220) : RegionCircleBorder;
                DrawCircleOutline(center, screenRadius, color, segments);

                // M26: Draw handles when selected
                if (selected)
                {
                    // Center handle (body)
                    DrawRegionHandleSquare((int)center.X, (int)center.Y, handleSz - 1,
                        _activeHandle == RegionHandle.Body ? handleActiveColor : new Color(100, 100, 255, 180));

                    // Radius handle (right side of circle)
                    int rHandleX = (int)(center.X + screenRadius);
                    int rHandleY = (int)center.Y;
                    DrawRegionHandleSquare(rHandleX, rHandleY, handleSz,
                        _activeHandle == RegionHandle.CircleRadius ? handleActiveColor : handleColor);
                }
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

    /// <summary>M26: Draw a small square handle indicator at the given screen position.</summary>
    private void DrawRegionHandleSquare(int cx, int cy, int halfSz, Color color)
    {
        _spriteBatch.Draw(_pixel, new Rectangle(cx - halfSz, cy - halfSz, halfSz * 2, halfSz * 2), color);
        // Border
        DrawRectBorder(cx - halfSz, cy - halfSz, halfSz * 2, halfSz * 2, new Color(0, 0, 0, 180));
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

            // Properties for selected def (editable)
            if (SelectedTriggerDefIndex >= 0 && SelectedTriggerDefIndex < _triggerSystem.Triggers.Count)
            {
                _spriteBatch.Draw(_pixel, new Rectangle(panelX, y, PanelWidth, 1), SeparatorColor);
                y += 4;
                var def = _triggerSystem.TriggersMut[SelectedTriggerDefIndex];
                int fw = PanelWidth - Margin * 2;

                if (_eb != null)
                {
                    string newName = _eb.DrawTextField("trig_name", "Name", def.Name, panelX + Margin, y, fw);
                    if (newName != def.Name) def.Name = newName;
                    y += FieldHeight + 2;

                    string newId = _eb.DrawTextField("trig_id", "ID", def.Id, panelX + Margin, y, fw);
                    if (newId != def.Id) def.Id = newId;
                    y += FieldHeight + 2;

                    def.ActiveByDefault = _eb.DrawCheckbox("Active", def.ActiveByDefault, panelX + Margin, y);
                    y += FieldHeight + 2;

                    def.OneShot = _eb.DrawCheckbox("OneShot", def.OneShot, panelX + Margin, y);
                    y += FieldHeight + 2;

                    int newMaxFire = _eb.DrawIntField("trig_maxFire", "MaxFireCount", def.MaxFireCount, panelX + Margin, y, fw);
                    if (newMaxFire != def.MaxFireCount) def.MaxFireCount = Math.Max(0, newMaxFire);
                    y += FieldHeight + 2;
                }
                else
                {
                    DrawSmallText($"ID: {def.Id}", panelX + Margin, y, TextBright); y += LineHeight;
                    DrawSmallText($"Name: {def.Name}", panelX + Margin, y, TextColor); y += LineHeight;
                    DrawSmallText($"Active: {def.ActiveByDefault}", panelX + Margin, y, TextColor); y += LineHeight;
                    DrawSmallText($"OneShot: {def.OneShot}  MaxFire: {def.MaxFireCount}", panelX + Margin, y, TextColor); y += LineHeight;
                }

                // Condition
                y += 4;
                _condEditIdCounter = 0; // Reset per-frame counter for unique field IDs
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

                    // Click to select effect
                    var effRect = new Rectangle(panelX + Margin, y, PanelWidth - Margin * 2 - 26, LineHeight);
                    var effMouse = Mouse.GetState();
                    if (IsInRect(effMouse, effRect) && effMouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released)
                        SelectedEffectIndex = ei;

                    DrawSmallText($"  {(selEffect ? "> " : "")}{effectLabel}", panelX + Margin, y,
                        selEffect ? TextBright : TextColor);

                    // Delete effect button
                    if (_eb != null && _eb.DrawButton("X", panelX + PanelWidth - Margin - 24, y, 22, 20, DangerColor))
                    {
                        def.Effects.RemoveAt(ei);
                        if (SelectedEffectIndex >= def.Effects.Count)
                            SelectedEffectIndex = def.Effects.Count - 1;
                        break;
                    }
                    y += LineHeight;

                    // RM18: Editable effect parameters when selected
                    if (selEffect && _eb != null)
                    {
                        int ex = panelX + Margin + 16;
                        int ew = fw - 16;
                        DrawEffectEditor(def.Effects[ei], ei, ex, ref y, ew);
                    }
                }

                // Add effect / add condition buttons
                if (y < contentY + contentH - ButtonHeight * 2)
                {
                    // Add condition dropdown
                    if (_eb != null)
                    {
                        string[] condTypes = { "AND", "OR", "NOT", "EntersRegion", "UnitsKilled", "GameTime", "Cooldown" };
                        string condType = _eb.DrawCombo("trig_addCond", "+ Condition", "(add)", condTypes, panelX + Margin, y, fw / 2);
                        if (condType != "(add)")
                        {
                            ConditionNode? newCond = condType switch
                            {
                                "AND" => new CondAnd(),
                                "OR" => new CondOr(),
                                "NOT" => new CondNot(),
                                "EntersRegion" => new CondEntersRegion(),
                                "UnitsKilled" => new CondUnitsKilled(),
                                "GameTime" => new CondGameTime(),
                                "Cooldown" => new CondCooldown(),
                                _ => null
                            };
                            if (newCond != null)
                            {
                                if (def.Condition == null)
                                    def.Condition = newCond;
                                else if (def.Condition is CondAnd andC)
                                    andC.Children.Add(newCond);
                                else
                                {
                                    var wrap = new CondAnd();
                                    wrap.Children.Add(def.Condition);
                                    wrap.Children.Add(newCond);
                                    def.Condition = wrap;
                                }
                            }
                        }

                        // Add effect dropdown
                        string[] effectTypes = { "ActivateTrigger", "DeactivateTrigger", "SpawnUnits", "KillUnits" };
                        string effectType = _eb.DrawCombo("trig_addEff", "+ Effect", "(add)", effectTypes, panelX + Margin + fw / 2 + 4, y, fw / 2 - 4);
                        if (effectType != "(add)")
                        {
                            TriggerEffect? newEff = effectType switch
                            {
                                "ActivateTrigger" => new EffActivateTrigger(),
                                "DeactivateTrigger" => new EffDeactivateTrigger(),
                                "SpawnUnits" => new EffSpawnUnits(),
                                "KillUnits" => new EffKillUnits(),
                                _ => null
                            };
                            if (newEff != null) def.Effects.Add(newEff);
                        }
                    }
                    else
                    {
                        DrawButtonRect("+ Condition", panelX + Margin, y, 100, ButtonHeight, ButtonBg);
                        DrawButtonRect("+ Effect", panelX + Margin + 110, y, 80, ButtonHeight, ButtonBg);
                    }
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

            // Properties for selected instance (editable)
            if (SelectedTriggerInstanceIndex >= 0 && SelectedTriggerInstanceIndex < _triggerSystem.Instances.Count)
            {
                _spriteBatch.Draw(_pixel, new Rectangle(panelX, y, PanelWidth, 1), SeparatorColor);
                y += 4;
                var inst = _triggerSystem.InstancesMut[SelectedTriggerInstanceIndex];
                int fw = PanelWidth - Margin * 2;

                if (_eb != null)
                {
                    string newInstId = _eb.DrawTextField("inst_id", "ID", inst.InstanceID, panelX + Margin, y, fw);
                    if (newInstId != inst.InstanceID) inst.InstanceID = newInstId;
                    y += FieldHeight + 2;

                    // RM28: Parent trigger dropdown populated from trigger system
                    {
                        var triggerIds = new string[_triggerSystem.Triggers.Count];
                        for (int ti = 0; ti < _triggerSystem.Triggers.Count; ti++)
                            triggerIds[ti] = _triggerSystem.Triggers[ti].Id;
                        string newParent = _eb.DrawCombo("inst_parent", "Parent", inst.ParentTriggerID, triggerIds, panelX + Margin, y, fw, allowNone: true);
                        if (newParent != inst.ParentTriggerID)
                            inst.ParentTriggerID = newParent;
                    }
                    y += FieldHeight + 2;

                    inst.ActiveByDefault = _eb.DrawCheckbox("Active", inst.ActiveByDefault, panelX + Margin, y);
                    y += FieldHeight + 2;

                    // RM20: BoundObjectID editable (was read-only)
                    string newBoundObj = _eb.DrawTextField("inst_boundobj", "BoundObjID", inst.BoundObjectID, panelX + Margin, y, fw);
                    if (newBoundObj != inst.BoundObjectID) inst.BoundObjectID = newBoundObj;
                    y += FieldHeight + 2;

                    DrawSmallText($"AutoCreated: {inst.AutoCreated}", panelX + Margin, y, TextDim); y += LineHeight;
                }
                else
                {
                    DrawSmallText($"ID: {inst.InstanceID}", panelX + Margin, y, TextBright); y += LineHeight;
                    DrawSmallText($"Parent: {inst.ParentTriggerID}", panelX + Margin, y, TextColor); y += LineHeight;
                    DrawSmallText($"Active: {inst.ActiveByDefault}", panelX + Margin, y, TextColor); y += LineHeight;
                    DrawSmallText($"Bound: {inst.BoundObjectID}", panelX + Margin, y, TextColor); y += LineHeight;
                    DrawSmallText($"AutoCreated: {inst.AutoCreated}", panelX + Margin, y, TextColor); y += LineHeight;
                }
            }
        }
    }

    private int _condEditIdCounter; // Incremented each frame to create unique field IDs

    private void DrawConditionTree(ConditionNode cond, int x, ref int y)
    {
        int fw = PanelWidth - Margin * 2 - (x - Margin);
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
            {
                DrawSmallText("EntersRegion:", x, y, TextColor); y += LineHeight;
                // RM18: Editable condition parameters
                if (_eb != null)
                {
                    int cid = _condEditIdCounter++;
                    string newRegion = _eb.DrawTextField($"cond_er_region_{cid}", "  RegionID", enters.RegionID, x, y, Math.Max(80, fw));
                    if (newRegion != enters.RegionID) enters.RegionID = newRegion;
                    y += FieldHeight + 2;
                    int newMin = _eb.DrawIntField($"cond_er_min_{cid}", "  MinCount", enters.MinCount, x, y, Math.Max(80, fw));
                    if (newMin != enters.MinCount) enters.MinCount = Math.Max(1, newMin);
                    y += FieldHeight + 2;
                }
                break;
            }
            case CondUnitsKilled killed:
            {
                DrawSmallText("UnitsKilled:", x, y, TextColor); y += LineHeight;
                if (_eb != null)
                {
                    int cid = _condEditIdCounter++;
                    int newCount = _eb.DrawIntField($"cond_uk_count_{cid}", "  Count", killed.Count, x, y, Math.Max(80, fw));
                    if (newCount != killed.Count) killed.Count = Math.Max(1, newCount);
                    y += FieldHeight + 2;
                    killed.Cumulative = _eb.DrawCheckbox("  Cumulative", killed.Cumulative, x, y);
                    y += FieldHeight + 2;
                }
                break;
            }
            case CondGameTime gameTime:
            {
                DrawSmallText("GameTime:", x, y, TextColor); y += LineHeight;
                if (_eb != null)
                {
                    int cid = _condEditIdCounter++;
                    float newTime = _eb.DrawFloatField($"cond_gt_time_{cid}", "  Time >=", gameTime.Time, x, y, Math.Max(80, fw), 1f);
                    if (MathF.Abs(newTime - gameTime.Time) > 0.001f) gameTime.Time = newTime;
                    y += FieldHeight + 2;
                }
                break;
            }
            case CondCooldown cooldown:
            {
                DrawSmallText("Cooldown:", x, y, TextColor); y += LineHeight;
                if (_eb != null)
                {
                    int cid = _condEditIdCounter++;
                    float newInterval = _eb.DrawFloatField($"cond_cd_int_{cid}", "  Interval", cooldown.Interval, x, y, Math.Max(80, fw), 0.5f);
                    if (MathF.Abs(newInterval - cooldown.Interval) > 0.001f) cooldown.Interval = newInterval;
                    y += FieldHeight + 2;
                }
                break;
            }
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

    /// <summary>
    /// RM18: Draw editable parameter fields for a trigger effect.
    /// </summary>
    private void DrawEffectEditor(TriggerEffect effect, int effectIndex, int x, ref int y, int w)
    {
        if (_eb == null) return;
        string prefix = $"eff_{effectIndex}_";

        switch (effect)
        {
            case EffActivateTrigger act:
            {
                string newId = _eb.DrawTextField(prefix + "trigId", "TriggerID", act.TriggerID, x, y, w);
                if (newId != act.TriggerID) act.TriggerID = newId;
                y += FieldHeight + 2;
                break;
            }
            case EffDeactivateTrigger deact:
            {
                string newId = _eb.DrawTextField(prefix + "trigId", "TriggerID", deact.TriggerID, x, y, w);
                if (newId != deact.TriggerID) deact.TriggerID = newId;
                y += FieldHeight + 2;
                break;
            }
            case EffSpawnUnits spawn:
            {
                string newUnitDef = _eb.DrawTextField(prefix + "unitDef", "UnitDefID", spawn.UnitDefID, x, y, w);
                if (newUnitDef != spawn.UnitDefID) spawn.UnitDefID = newUnitDef;
                y += FieldHeight + 2;

                int newCount = _eb.DrawIntField(prefix + "count", "Count", spawn.Count, x, y, w);
                if (newCount != spawn.Count) spawn.Count = Math.Max(1, newCount);
                y += FieldHeight + 2;

                string curFaction = spawn.Faction.ToString();
                string newFaction = _eb.DrawCombo(prefix + "faction", "Faction", curFaction, CachedFactionNames, x, y, w);
                if (newFaction != curFaction && Enum.TryParse<Faction>(newFaction, out var parsedFaction))
                    spawn.Faction = parsedFaction;
                y += FieldHeight + 2;

                string newRegion = _eb.DrawTextField(prefix + "region", "RegionID", spawn.RegionID, x, y, w);
                if (newRegion != spawn.RegionID) spawn.RegionID = newRegion;
                y += FieldHeight + 2;

                float newDist = _eb.DrawFloatField(prefix + "dist", "SpawnDist", spawn.SpawnDistance, x, y, w, 0.5f);
                if (MathF.Abs(newDist - spawn.SpawnDistance) > 0.001f) spawn.SpawnDistance = newDist;
                y += FieldHeight + 2;

                float newInterval = _eb.DrawFloatField(prefix + "interval", "SpawnInterval", spawn.SpawnInterval, x, y, w, 0.5f);
                if (MathF.Abs(newInterval - spawn.SpawnInterval) > 0.001f) spawn.SpawnInterval = newInterval;
                y += FieldHeight + 2;

                string curBehavior = spawn.PostBehavior.ToString();
                string newBehavior = _eb.DrawCombo(prefix + "behavior", "PostBehavior", curBehavior, CachedPostSpawnBehaviorNames, x, y, w);
                if (newBehavior != curBehavior && Enum.TryParse<PostSpawnBehavior>(newBehavior, out var parsedBehavior))
                    spawn.PostBehavior = parsedBehavior;
                y += FieldHeight + 2;

                string newPatrol = _eb.DrawTextField(prefix + "patrol", "PatrolRouteID", spawn.PatrolRouteID, x, y, w);
                if (newPatrol != spawn.PatrolRouteID) spawn.PatrolRouteID = newPatrol;
                y += FieldHeight + 2;
                break;
            }
            case EffKillUnits kill:
            {
                string newRegion = _eb.DrawTextField(prefix + "region", "RegionID", kill.RegionID, x, y, w);
                if (newRegion != kill.RegionID) kill.RegionID = newRegion;
                y += FieldHeight + 2;

                int newMax = _eb.DrawIntField(prefix + "maxKills", "MaxKills", kill.MaxKills, x, y, w);
                if (newMax != kill.MaxKills) kill.MaxKills = Math.Max(0, newMax);
                y += FieldHeight + 2;
                break;
            }
        }
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
                writer.WriteNumber("costWood", def.CostWood);
                writer.WriteNumber("costStone", def.CostStone);
                writer.WriteNumber("costGold", def.CostGold);
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

    private void LoadMap()
    {
        try
        {
            string mapsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "maps");
            string mapPath = Path.Combine(mapsDir, _mapFilename + ".json");
            string triggerPath = Path.Combine(mapsDir, _mapFilename + "_triggers.json");
            string roadsPath = Path.Combine(mapsDir, _mapFilename + "_roads.json");

            if (!File.Exists(mapPath))
            {
                _statusMessage = $"File not found: {mapPath}";
                _statusTimer = 3f;
                return;
            }

            // Clear existing data
            _groundSystem.ClearTypes();
            _envSystem.ClearObjects();
            _envSystem.ClearDefs();
            _wallSystem.Defs.Clear();
            _undoStack.Clear();

            // Load main map data (ground, env defs, placed objects, wall defs)
            MapData.Load(mapPath, _groundSystem, _envSystem, _wallSystem);

            // Reload ground textures
            _groundSystem.LoadTextures(_device);

            // Load grass data from the map JSON
            string json = File.ReadAllText(mapPath);
            using var doc = JsonDocument.Parse(json);
            var grassInfo = MapData.LoadGrass(doc.RootElement);
            if (grassInfo.HasValue)
            {
                var gi = grassInfo.Value;
                _grassMap = gi.Cells;
                _grassW = gi.Width;
                _grassH = gi.Height;
                _grassTypes.Clear();
                foreach (var t in gi.Types)
                {
                    _grassTypes.Add(new GrassTypeDef
                    {
                        Id = t.Id, Name = t.Name,
                        BaseR = t.BaseR, BaseG = t.BaseG, BaseB = t.BaseB,
                        TipR = t.TipR, TipG = t.TipG, TipB = t.TipB
                    });
                }
            }

            // Load triggers
            MapData.LoadTriggers(triggerPath, _triggerSystem);

            // Load roads
            MapData.LoadRoads(roadsPath, _roadSystem);

            // Reload env textures
            _envSystem.LoadTextures(_device);

            // Notify listeners
            _onVertexMapChanged?.Invoke();
            _onGrassMapChanged?.Invoke();

            _statusMessage = $"Loaded: {mapPath}";
            _statusTimer = 3f;
            DebugLog.Log("editor", $"Map loaded from {mapPath}");
        }
        catch (Exception ex)
        {
            _statusMessage = $"Load error: {ex.Message}";
            _statusTimer = 5f;
            DebugLog.Log("editor", $"Map load error: {ex.Message}");
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

    /// <summary>M26: Check if a world position is near a target point within tolerance.</summary>
    private static bool Vec2Near(Vec2 a, Vec2 b, float tol) =>
        MathF.Abs(a.X - b.X) < tol && MathF.Abs(a.Y - b.Y) < tol;

    // ---- Environment helpers ----

    private List<string> GetEnvCategories()
    {
        var cats = new HashSet<string>();
        bool hasGroups = false;
        for (int i = 0; i < _envSystem.DefCount; i++)
        {
            cats.Add(_envSystem.GetDef(i).Category);
            if (!string.IsNullOrEmpty(_envSystem.GetDef(i).Group))
                hasGroups = true;
        }

        var list = new List<string> { "All" };
        foreach (var c in cats)
            if (c != "All") list.Add(c);
        // M17: Add "Groups" option if any defs have a group
        if (hasGroups)
            list.Add("Groups");
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

    /// <summary>
    /// M17: Get distinct group names from environment object defs.
    /// </summary>
    private List<string> GetEnvGroups()
    {
        var groups = new List<string>();
        var seen = new HashSet<string>();
        for (int i = 0; i < _envSystem.DefCount; i++)
        {
            var def = _envSystem.GetDef(i);
            if (!string.IsNullOrEmpty(def.Group) && seen.Add(def.Group))
                groups.Add(def.Group);
        }
        return groups;
    }

    /// <summary>
    /// RM04: Rebuild cost field and bake environment collisions.
    /// Called after placing or removing objects so pathfinding data stays in sync.
    /// </summary>
    private void RebakeObjectCollisions()
    {
        _tileGrid.RebuildCostField();
        _envSystem.BakeCollisions(_tileGrid);
    }

    /// <summary>
    /// RM06: If the placed object's def has a BoundTriggerID, auto-create a TriggerInstance for it.
    /// </summary>
    private void AutoCreateTriggerInstance(int objectIndex)
    {
        if (objectIndex < 0 || objectIndex >= _envSystem.ObjectCount) return;
        var obj = _envSystem.GetObject(objectIndex);
        if (obj.DefIndex < 0 || obj.DefIndex >= _envSystem.DefCount) return;
        var def = _envSystem.GetDef(obj.DefIndex);
        if (string.IsNullOrEmpty(def.BoundTriggerID)) return;

        var inst = new TriggerInstance
        {
            InstanceID = $"auto_{obj.ObjectID}",
            ParentTriggerID = def.BoundTriggerID,
            BoundObjectID = obj.ObjectID,
            ActiveByDefault = true,
            AutoCreated = true
        };
        _triggerSystem.AddInstance(inst);
    }

    /// <summary>
    /// RM07: Before removing an object, check if it has a bound trigger instance and remove it.
    /// </summary>
    private void AutoRemoveTriggerInstance(int objectIndex)
    {
        if (objectIndex < 0 || objectIndex >= _envSystem.ObjectCount) return;
        var obj = _envSystem.GetObject(objectIndex);

        // Find and remove any trigger instance bound to this object
        for (int i = _triggerSystem.Instances.Count - 1; i >= 0; i--)
        {
            if (_triggerSystem.Instances[i].BoundObjectID == obj.ObjectID)
            {
                _triggerSystem.RemoveInstance(i);
            }
        }
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
