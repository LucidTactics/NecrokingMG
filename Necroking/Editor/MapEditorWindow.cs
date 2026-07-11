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

public enum MapEditorTab { Ground, Grass, Objects, Walls, Roads, Regions, Triggers, Units, Zones, ProcGen }

// ============================================================================
//  Grass type definition (editor-side, no dedicated GrassSystem yet)
// ============================================================================
public class GrassTypeDef
{
    public const int MaxSprites = 5;

    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>
    /// Sprite paths (relative to project root, e.g. "assets/Environment/Grass/GreenGrass1.png").
    /// Up to MaxSprites entries; renderer picks one per cell via a hash of the cell position.
    /// </summary>
    public List<string> SpritePaths { get; set; } = new();

    /// <summary>
    /// Per-type rendered-size multiplier applied on top of the base cell-size tuft
    /// footprint. 1.0 = tuft fills one cell exactly. Useful for "hero clump" types
    /// (e.g. 1.5) or fine detail types (e.g. 0.7) without re-authoring the sprites.
    /// </summary>
    public float Scale { get; set; } = 1.0f;

    /// <summary>
    /// Per-type tuft density per painted cell. Below 1.0 it's a probability (0.3 =
    /// ~30% of cells render a tuft). At or above 1.0 it's a count: integer part =
    /// guaranteed tufts per cell, fractional part = probability of one extra
    /// (2.7 = 2 tufts + 70% chance of a 3rd). Each tuft uses a different hash seed
    /// so position, sprite choice, flip, and scale vary within the cell.
    /// </summary>
    public float Density { get; set; } = 1.0f;

    /// <summary>Multiplicative tint for healthy tufts. White (default) leaves the
    /// sprite colors untouched.</summary>
    public Color DefaultTint { get; set; } = Color.White;

    /// <summary>Multiplicative tint applied once a tuft has fully corrupted. The
    /// renderer lerps from DefaultTint to this over GrassCorruptionFadeDuration
    /// when the underlying ground vertex first flips.</summary>
    public Color CorruptedTint { get; set; } = new Color((byte)80, (byte)60, (byte)70, (byte)255);
}

// ============================================================================
//  MapEditorWindow - the full tabbed map editor
// ============================================================================
/// <summary>
/// The full in-game map editor (MenuState.MapEditor) — tabbed tools for ground/grass
/// painting, env-object, wall and road placement, regions, triggers, units, zones and
/// procgen, plus map save/load. Edits the live world directly and persists through
/// <see cref="Data.MapData"/>. Editor-only: no gameplay rules belong here.
/// </summary>
public class MapEditorWindow
{
    // ---- Owning game (set via Init) ----
    // The editor pulls its shared engine systems, rendering handles, and the
    // EditorBase straight off Game1 rather than caching its own copies, so it
    // can never drift from the live session (StartGame swaps GameSession, and
    // Game1's own _groundSystem/_envSystem/... are already session-forwarders).
    // The underscore-named properties keep every existing _groundSystem/_camera/
    // ... call site in this file unchanged.
    private Game1 _game = null!;
    private GroundSystem _groundSystem => _game._groundSystem;
    private EnvironmentSystem _envSystem => _game._envSystem;
    private TriggerSystem _triggerSystem => _game._triggerSystem;
    private ZoneSystem _zoneSystem => _game._zoneSystem;
    private Data.Registries.ItemRegistry? _itemRegistry;
    private Data.GameData _gameData;
    private WallSystem _wallSystem => _game._wallSystem;
    private RoadSystem _roadSystem => _game._roadSystem;
    private TileGrid _tileGrid => _game._sim.Grid;
    private Camera25D _camera => _game._camera;
    private SpriteBatch _spriteBatch => _game._spriteBatch;
    // Straight-alpha draw surface — all draw calls go through this (colors get
    // encoded per the open material); _spriteBatch stays only for Init plumbing.
    private Render.SpriteScope Scope => _spriteBatch;
    private Texture2D _pixel => _game._pixel;
    private SpriteFont? _font => _game._font;
    private SpriteFont? _smallFont => _game._smallFont;
    private GraphicsDevice _device => _game.GraphicsDevice;
    private EditorBase? _eb => _game._editorUi;

    // Callbacks
    private Action? _onVertexMapChanged;
    // Fired when grass CELLS change (painting). The grass map array is shared
    // with the renderer and read live, so this is a cheap reference-reconcile —
    // it must NOT trigger a full grass-type/sprite resync per painted frame.
    private Action? _onGrassMapChanged;
    // Fired when grass TYPE DEFINITIONS change (add/remove type, edit sprites,
    // load). This is the expensive path that rebuilds type tables and re-pushes
    // sprites to the renderer; it only runs on structural edits, not painting.
    private Action? _onGrassTypesChanged;

    // ---- Grass data (owned by editor, synced back to Game1) ----
    private readonly List<GrassTypeDef> _grassTypes = new();
    private byte[] _grassMap = Array.Empty<byte>();
    private int _grassW, _grassH;
    private float _grassCellSize = 0.8f;

    // ---- Editor state ----
    public MapEditorTab ActiveTab = MapEditorTab.Ground;
    public int BrushRadius = 2;

    // Per-tab scroll offsets
    private readonly float[] _tabScroll = new float[10];

    // Ground tab
    public int SelectedGroundType;

    // Grass tab
    public int SelectedGrassType; // -1 for none, type indices 0-N
    private bool _grassEraserSelected;
    private bool _grassGridDebugEnabled;

    // Objects tab
    // "Nothing selected" sentinel for the Objects tab. Must NOT be -1: negative
    // values encode group selection as -(groupIndex+1), so -1 IS "group 0" —
    // the old -1 initial value made a fresh Objects tab place a random group-0
    // object on the first world click.
    public const int EnvNoSelection = int.MinValue;
    public int SelectedEnvDefIndex = EnvNoSelection;
    /// <summary>True when the Objects tab has a GROUP selected (encoded as
    /// -(groupIndex+1)) as opposed to a specific def or nothing.</summary>
    private bool IsEnvGroupSelected => SelectedEnvDefIndex < 0 && SelectedEnvDefIndex != EnvNoSelection;
    public int SelectedEnvCategory;
    private bool _objectPaintMode; // false = single, true = paint
    private float _envListScroll;

    // Auto-ground: when placing objects, stamp a ground patch underneath (e.g.
    // dirt under trees on grassland). Applies in both single and paint modes.
    // The Objects tab uses this one global instance; each ProcGen pool carries its
    // own AutoGroundSettings. All stamping/UI is shared (see the auto-ground helpers).
    private readonly AutoGroundSettings _objectsAutoGround = new();
    // Accumulates old ground vertex values across a paint stroke so the auto-ground
    // stamped under a whole drag undoes together with the placed objects. Reused by
    // both the Objects paint stroke and the ProcGen paint stroke.
    private Dictionary<long, byte>? _autoGroundStrokeOld;

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
    // Width applied to newly placed road control points (editable in-panel).
    private float _roadNewPointWidth = 2f;
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
    private Vec2 _regionDragOffset; // offset from region center to click point for body drag

    // Zones tab
    public int SelectedZoneIndex = -1;
    private int _zoneCreateKindIdx;              // index into ZoneKind values for "+ Draw Zone"
    private bool _zoneDrawMode;                  // "+ Draw Zone" armed, next world drag creates
    private bool _zoneRubberBanding;             // drag-out in progress
    private Vec2 _zoneDragStartWorld;            // rubber-band anchor corner
    private int _draggingZone = -1;
    // Snapshot of the dragged zone's rect, taken at mouse press; pushed to the undo
    // stack on the first frame the drag actually changes something (see UndoZoneEdit).
    private UndoZoneEdit? _zonePendingUndo;
    private RegionHandle _zoneHandle = RegionHandle.None; // rect subset only (no CircleRadius)
    private Vec2 _zoneDragOffset;                // zone center - click point, for body drag
    // Reused per-frame accumulator for the village panel's contents summary
    private readonly Dictionary<string, int> _zoneContentCounts = new();
    // Ctrl+C/Ctrl+V clipboard for the Zones tab (deep copy, so later edits to the
    // source zone don't leak into what gets pasted)
    private MapZone? _zoneClipboard;

    // ProcGen tab
    private readonly List<ProcGenStyle> _procGenStyles = new();
    private bool _procGenStylesLoaded;                 // lazy-load data/procgen_styles.json once
    public int SelectedProcGenStyle = -1;
    // Fractional placement attempts are accrued per-category (ProcGenCategory.Accum).
    private const float ProcGenBrushRadius = 5f;       // brush disc radius in world units
    private const float ProcGenRatePerDensity = 5f;    // placement attempts/sec per density point
    private const int ProcGenTries = 6;                // 1 attempt + 5 retries per placement
    private string[]? _procGenDefIdOptions;            // all env def ids, cached for the combos
    private int _procGenDefIdOptionCount = -1;

    // Cached enum name arrays (avoid per-frame allocation)
    private static readonly string[] CachedFactionNames = Enum.GetNames<Faction>();
    private static readonly string[] CachedPostSpawnBehaviorNames = Enum.GetNames<PostSpawnBehavior>();
    private static readonly string[] CachedZoneKindNames = Enum.GetNames<ZoneKind>();

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
    private const float CamAcceleration = 100f;
    private const float CamFriction = 8f;
    private const float CamBaseSpeed = 30f;

    // RM17: Waypoint dragging
    private int _draggingWaypoint = -1;

    // M28: Object batch undo accumulators
    private List<(ushort defIdx, float x, float y, float scale, float seed, int objIdx)>? _batchPlacedObjects;
    private List<(ushort defIdx, float x, float y, float scale, float seed)>? _batchRemovedObjects;

    // Units tab
    private readonly List<PlacedUnit> _placedUnits = new();
    private int _selectedUnitDefIdx = -1;
    private int _unitFactionFilter; // 0=All, 1=Undead, 2=Human, 3=Animal
    private string _unitPatrolRoute = "";
    private bool _placeAsCorpse; // when set, the Units tool places dead bodies
    // Units-tab thumbnail grid: layout cached from Draw for hit-testing in Update
    private const int ThumbGridCols = 6;
    private const int ThumbGridGap = 2;
    private int _unitGridDrawX;
    private int _unitGridDrawY;
    private int _unitGridCellW;
    private int _unitGridCellH;
    private int _unitGridViewH; // grid viewport height in px (scroll is continuous, not row-stepped)
    // Objects-tab thumbnail grid (same scheme)
    private int _objGridDrawX;
    private int _objGridDrawY;
    private int _objGridCellW;
    private int _objGridCellH;
    private int _objGridViewH;

    // When the editor is entered via a mouse click (e.g. the pause-menu "Map
    // Editor" button), that same click would otherwise bubble straight into the
    // world and place/paint at the cursor — the editor tracks its own _prevMouse,
    // which is stale (released) while the menu is up, so it reads the held button
    // as a fresh press. Set by SuppressClicksUntilRelease(); cleared once both
    // buttons are up so the entry click is ignored entirely.
    private bool _suppressClicksUntilRelease;

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

    // M28: Batch undo for paint-mode object placement (also used for single placement
    // as a one-element batch — see UpdateObjectsTab single-place path)
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
                Env.AddObject(r.defIdx, r.x, r.y, r.scale, r.seed, persistent: true);
        }
    }

    private class UndoUnitPlace : UndoAction
    {
        public List<PlacedUnit> Units = null!;
        public override void Undo()
        {
            if (Units.Count > 0) Units.RemoveAt(Units.Count - 1);
        }
    }

    private class UndoUnitRemove : UndoAction
    {
        public List<PlacedUnit> Units = null!;
        public PlacedUnit Removed;
        public int RemovedIndex;
        public override void Undo()
        {
            Units.Insert(RemovedIndex, Removed);
        }
    }

    // "Clear All Units": restores the full pre-clear list contents.
    private class UndoUnitClearAll : UndoAction
    {
        public List<PlacedUnit> Units = null!;
        public List<PlacedUnit> Cleared = null!;
        public override void Undo()
        {
            Units.Clear();
            Units.AddRange(Cleared);
        }
    }

    // One zone rect move/resize = one mouse press (snapshot taken at drag start,
    // pushed on the first actual change). Undoing also cancels an in-flight drag
    // so the held mouse doesn't immediately re-apply the undone move. The zone is
    // found by Id — indices may have shifted since the drag.
    private class UndoZoneEdit : UndoAction
    {
        public MapEditorWindow Owner = null!;
        public string ZoneId = "";
        public float X, Y, HalfW, HalfH;
        public override void Undo()
        {
            var zones = Owner._zoneSystem.ZonesMut;
            for (int i = 0; i < zones.Count; i++)
            {
                if (zones[i].Id != ZoneId) continue;
                zones[i].X = X; zones[i].Y = Y;
                zones[i].HalfW = HalfW; zones[i].HalfH = HalfH;
                break;
            }
            Owner._draggingZone = -1;
            Owner._zoneHandle = RegionHandle.None;
            Owner._zonePendingUndo = null;
        }
    }

    // Undo a zone paste/add: remove the zone by Id (indices may have shifted).
    private class UndoZonePlace : UndoAction
    {
        public MapEditorWindow Owner = null!;
        public string ZoneId = "";
        public override void Undo()
        {
            var zones = Owner._zoneSystem.ZonesMut;
            for (int i = 0; i < zones.Count; i++)
            {
                if (zones[i].Id != ZoneId) continue;
                zones.RemoveAt(i);
                break;
            }
            if (Owner.SelectedZoneIndex >= zones.Count)
                Owner.SelectedZoneIndex = zones.Count - 1;
        }
    }

    // Undo a zone delete: re-insert the removed zone at its original index.
    private class UndoZoneRemove : UndoAction
    {
        public MapEditorWindow Owner = null!;
        public MapZone Zone = null!;
        public int Index;
        public override void Undo()
        {
            var zones = Owner._zoneSystem.ZonesMut;
            int idx = Math.Clamp(Index, 0, zones.Count);
            zones.Insert(idx, Zone);
            Owner.SelectedZoneIndex = idx;
        }
    }

    // Groups several actions into one undo step (e.g. object placement + the
    // auto-ground patch stamped under it). Undone in reverse order.
    private class UndoComposite : UndoAction
    {
        public List<UndoAction> Actions = new();
        public override void Undo()
        {
            for (int i = Actions.Count - 1; i >= 0; i--)
                Actions[i].Undo();
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

        // Suppress the per-object pathfinder rebuild that AddObject/RemoveObject
        // fires during undo. The editor doesn't run AI and nothing reads the
        // cost field between edits — the MapEditor→gameplay transition (Game1)
        // rebuilds once for the whole session. Without this, undoing a paint
        // stroke fired one full RebuildPathfinder PER object (~450 ms each on a
        // 4097² map → multi-second freezes); a single-object undo cost a full
        // rebuild too. Placement already relies on this same exit-rebuild
        // guarantee and only stamps incrementally.
        var prev = _envSystem.OnCollisionsDirty;
        _envSystem.OnCollisionsDirty = null;
        try { action.Undo(); }
        finally { _envSystem.OnCollisionsDirty = prev; }
    }

    // Tab names and layout
    private static readonly string[] TabRow1 = { "Ground", "Grass", "Objects", "Walls" };
    private static readonly string[] TabRow2 = { "Roads", "Regions", "Triggers", "Units", "Zones", "ProcGen" };

    // ========================================================================
    //  Init
    // ========================================================================

    public void Init(
        Game1 game,
        Action? onVertexMapChanged = null,
        Action? onGrassMapChanged = null,
        Action? onGrassTypesChanged = null)
    {
        _game = game;
        _onVertexMapChanged = onVertexMapChanged;
        _onGrassMapChanged = onGrassMapChanged;
        _onGrassTypesChanged = onGrassTypesChanged;

        // Initialize the environment object def editor sub-window
        if (_eb != null)
        {
            _envObjectEditor = new EnvObjectEditorWindow();
            _envObjectEditor.Init(_eb, _envSystem, _device, _spriteBatch, _pixel, _font, _smallFont, _triggerSystem);
            _envObjectEditor.SetItemRegistry(_itemRegistry);

            _wallEditor = new WallEditorWindow(_eb);
            _wallEditor.SetWallSystem(_wallSystem);
            _wallEditor.SetMapFilename(_mapFilename);
        }
    }

    /// <summary>
    /// Set the map filename the editor saves to (without the ".json" extension).
    /// Called when a map is loaded so Save Map defaults to the currently-loaded
    /// map (e.g. "testmap") instead of always overwriting "default". Also keeps
    /// the wall editor's filename in sync.
    /// </summary>
    public void SetMapFilename(string name)
    {
        if (string.IsNullOrEmpty(name)) return;
        _mapFilename = name;
        _wallEditor?.SetMapFilename(name);
    }

    /// <summary>Dev-command hook (`save_map [name]`): trigger the same save as the
    /// editor's Save Map button, optionally to a different filename (the editor's
    /// save target is left unchanged). Returns the map filename that was written.</summary>
    public string DevSaveMap(string? filename = null)
    {
        string prev = _mapFilename;
        if (!string.IsNullOrEmpty(filename)) _mapFilename = filename;
        try { SaveMap(); return _mapFilename; }
        finally { _mapFilename = prev; }
    }

    /// <summary>Ignore world interaction until the mouse buttons are released. Call
    /// when the editor is opened via a click so that click doesn't bubble into the
    /// world (place a unit, paint ground, etc.) on the entry frame.</summary>
    public void SuppressClicksUntilRelease() => _suppressClicksUntilRelease = true;

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
            var def = new GrassTypeDef
            {
                Id = t.Id, Name = t.Name,
                Scale = t.Scale > 0f ? t.Scale : 1f,
                Density = t.Density > 0f ? t.Density : 1f,
                DefaultTint   = new Color(t.DefR, t.DefG, t.DefB, t.DefA),
                CorruptedTint = new Color(t.CorR, t.CorG, t.CorB, t.CorA),
            };
            if (t.SpritePaths != null)
                foreach (var p in t.SpritePaths) def.SpritePaths.Add(p);
            _grassTypes.Add(def);
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

    /// <summary>Set by Game1 each frame (before calling Update/Draw) when the cursor
    /// is over the persistent gameplay HUD — top menu buttons, spell bar, time
    /// controls — which keeps rendering on top of the full-screen map editor but
    /// lives outside the editor's own side panel. Without this, a click on e.g. the
    /// "Crafting" button also fell through to the world underneath and painted/
    /// placed there, since IsMouseOverPanel only knew about the editor's own rects.</summary>
    public bool OverGameplayHud = false;

    public bool IsMouseOverPanel(int screenW, int screenH)
    {
        if (OverGameplayHud) return true;

        // MousePos, not the raw Mouse state — it's the canonical per-frame cursor
        // (and the `mousepos` dev override patches only MousePos, so headless
        // hover tests see the same panel gating a real mouse would).
        var mouse = _eb._input.MousePos;
        return IsPanelAt((int)mouse.X, (int)mouse.Y, screenW, screenH);
    }

    /// <summary>Pure point test against the editor's own UI surfaces — the right
    /// side panel plus the Zones tab's left village panel. This is the map-editor
    /// layer's ContainsMouse in the UI router (its footprint: the editor is NOT a
    /// blocking blanket; the world area stays paintable underneath).</summary>
    public bool IsPanelAt(int mx, int my, int screenW, int screenH)
    {
        int panelX = screenW - PanelWidth - 10;
        int panelY = 10;
        int panelH = screenH - 20;
        if (mx >= panelX && mx < panelX + PanelWidth &&
            my >= panelY && my < panelY + panelH)
            return true;

        // The Zones tab's left village panel is UI too — without this, clicks on it
        // would bleed into the world (drag zones, place objects) underneath it.
        var leftPanel = ZoneLeftPanelRect(screenH);
        return leftPanel.HasValue &&
               mx >= leftPanel.Value.X && mx < leftPanel.Value.Right &&
               my >= leftPanel.Value.Y && my < leftPanel.Value.Bottom;
    }

    /// <summary>True if any sub-popup is open over the map editor and should
    /// own input — texture file browser, color picker, dropdown. Used to gate
    /// the hand-rolled scroll / click handlers in per-tab updaters so they
    /// don't fire under a modal overlay.</summary>
    private bool IsAnyPopupBlocking() =>
        _textureBrowser.IsOpen
        || (_eb != null && (_eb.IsColorPickerOpen || _eb.IsDropdownOpen));

    /// <summary>Set by the host each frame: true only when the bare map editor (no
    /// sub-editor popup focused) owns WASD for camera panning. When a sub-editor
    /// like the object editor is focused, this is false so WASD navigates that
    /// editor's list instead of moving the map camera.</summary>
    public bool CameraInputEnabled = true;

    // ========================================================================
    //  Update
    // ========================================================================

    /// <summary>Whether the env object def editor overlay is open (blocks map interaction).</summary>
    public void SetItemRegistry(Data.Registries.ItemRegistry? items)
    {
        _itemRegistry = items;
        _envObjectEditor?.SetItemRegistry(items);
    }

    public void SetSpellRegistry(Data.Registries.SpellRegistry? spells)
    {
        _envObjectEditor?.SetSpellRegistry(spells);
    }

    public void SetCorpseSettings(Data.Registries.CorpseSettings? settings, Render.SpriteAtlas? corpsesAtlas)
    {
        _envObjectEditor?.SetCorpseSettings(settings, corpsesAtlas);
    }

    public void SetGameData(Data.GameData data) => _gameData = data;

    /// <summary>Restore the last-open tab from per-machine settings. Called once at
    /// init (right after SetGameData), not from the open handlers — the editor can be
    /// opened via F11, the HUD button, or the pause menu, so init is the one place
    /// that covers them all. Clamps to the valid enum range — a stale/hand-edited
    /// settings file could hold an out-of-range value.</summary>
    public void RestoreTabFromSettings()
    {
        if (_gameData == null) return;
        int saved = _gameData.Settings.General.MapEditorLastTab;
        if (System.Enum.IsDefined(typeof(MapEditorTab), saved))
            ActiveTab = (MapEditorTab)saved;
    }

    public void SetPlacedUnits(List<PlacedUnit> units)
    {
        _placedUnits.Clear();
        _placedUnits.AddRange(units);
    }

    /// <summary>Get the placed units list for loading into Game1.</summary>
    public List<PlacedUnit> PlacedUnits => _placedUnits;

    public bool IsEnvObjectEditorOpen => _envObjectEditor != null && _envObjectEditor.IsOpen;

    public void Update(int screenW, int screenH)
    {
        // Update env object editor if open (blocks all normal input)
        if (_envObjectEditor != null && _envObjectEditor.IsOpen)
        {
            _envObjectEditor.Update();
            _eb.SetMouseOverUI(); // block game-world clicks while overlay is open
            _prevMouse = _eb._input.Mouse;
            _prevKb = _eb._input.Kb;
            _prevScrollValue = _prevMouse.ScrollWheelValue;
            return;
        }

        var mouse = _eb._input.Mouse;
        var kb = _eb._input.Kb;
        float dt = 1f / 60f; // fixed timestep assumption

        // Swallow the click that opened the editor: until both buttons release,
        // skip all world interaction so the entry click can't place/paint.
        if (_suppressClicksUntilRelease)
        {
            if (mouse.LeftButton == ButtonState.Released && mouse.RightButton == ButtonState.Released)
                _suppressClicksUntilRelease = false;
            _prevMouse = mouse;
            _prevKb = kb;
            _prevScrollValue = mouse.ScrollWheelValue;
            return;
        }

        // If wall editor overlay is open, skip normal input processing
        if (_wallEditor != null && _wallEditor.IsOpen)
        {
            _eb.SetMouseOverUI(); // block game-world clicks while overlay is open
            _prevMouse = mouse;
            _prevKb = kb;
            _prevScrollValue = mouse.ScrollWheelValue;
            return;
        }

        // Block all clicks when any popup/overlay is open (texture browser, color picker, dropdown).
        // Shared with sub-tab updaters via IsAnyPopupBlocking() so per-tab
        // scroll handlers can gate themselves too.
        bool popupBlocking = IsAnyPopupBlocking();

        bool leftClick = !popupBlocking && mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released;
        bool leftDown = !popupBlocking && mouse.LeftButton == ButtonState.Pressed;
        bool leftUp = mouse.LeftButton == ButtonState.Released && _prevMouse.LeftButton == ButtonState.Pressed;
        bool rightClick = !popupBlocking && mouse.RightButton == ButtonState.Pressed && _prevMouse.RightButton == ButtonState.Released;
        bool rightDown = !popupBlocking && mouse.RightButton == ButtonState.Pressed;
        bool rightUp = mouse.RightButton == ButtonState.Released && _prevMouse.RightButton == ButtonState.Pressed;

        int panelX = screenW - PanelWidth - 10;
        int panelY = 10;
        bool overPanel = IsMouseOverPanel(screenW, screenH);

        // INF03: Set mouse-over-UI flag when over the editor panel
        if (overPanel && _eb != null)
            _eb.SetMouseOverUI();

        // Status timer
        if (_statusTimer > 0) _statusTimer -= dt;

        // M31: Suppress hotkeys while the keyboard is captured by a UI element —
        // an active text field, an open combo's filter box (typing a digit there
        // used to switch tabs and strand an invisible open dropdown), or the
        // color picker's value boxes.
        bool textEditing = _eb != null && _eb.IsKeyboardCaptured;

        if (!textEditing)
        {
            // --- Tab switching via keyboard (1-9) ---
            for (int i = 0; i < 9; i++)
            {
                Keys key = Keys.D1 + i;
                if (kb.IsKeyDown(key) && _prevKb.IsKeyUp(key))
                    ActiveTab = (MapEditorTab)i;
            }
        }

        // --- Tab switching via click ---
        // Gate on the actual panel rect (IsPanelAt), NOT overPanel: overPanel
        // folds in OverGameplayHud (cursor on the HUD rows drawn over the
        // editor, kept true so clicks there don't paint), and the menu-button
        // row overlaps tab row 1's y-band — a click on it used to reach here
        // with a NEGATIVE relX that integer division truncated toward zero,
        // silently selecting column 0 (the Ground tab).
        if (leftClick && IsPanelAt(mouse.X, mouse.Y, screenW, screenH))
        {
            int tabY1 = panelY;
            int tabY2 = panelY + TabRowHeight;
            int tabW1 = PanelWidth / TabRow1.Length;
            int tabW2 = PanelWidth / TabRow2.Length;

            if (mouse.Y >= tabY1 && mouse.Y < tabY1 + TabRowHeight)
            {
                int relX = mouse.X - panelX;
                int idx = relX >= 0 ? relX / tabW1 : -1;
                if (idx >= 0 && idx < TabRow1.Length) ActiveTab = (MapEditorTab)idx;
            }
            else if (mouse.Y >= tabY2 && mouse.Y < tabY2 + TabRowHeight)
            {
                int relX = mouse.X - panelX;
                int idx = relX >= 0 ? relX / tabW2 : -1;
                if (idx >= 0 && idx < TabRow2.Length) ActiveTab = (MapEditorTab)(idx + TabRow1.Length);
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

            // M16: Smooth WASD camera. Arrow keys are NO LONGER camera keys (RM37
            // reverted) — they're free for editor list navigation. Gated on
            // CameraInputEnabled so only the bare map editor pans; when a sub-editor
            // (object editor) is focused, WASD navigates its list instead.
            if (CameraInputEnabled)
            {
                float cam_accel = CamAcceleration;
                if (kb.IsKeyDown(Keys.LeftShift) || kb.IsKeyDown(Keys.RightShift)) cam_accel *= 5;
                if (kb.IsKeyDown(Keys.W)) _camVelY -= cam_accel * dt;
                if (kb.IsKeyDown(Keys.S) && !kb.IsKeyDown(Keys.LeftControl)) _camVelY += cam_accel * dt;
                if (kb.IsKeyDown(Keys.A)) _camVelX -= cam_accel * dt;
                if (kb.IsKeyDown(Keys.D)) _camVelX += cam_accel * dt;
            }

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
        // Gate on popupBlocking so a sub-popup (texture file browser, color
        // picker, dropdown) captures scroll first. InputState.IsScrollConsumed
        // can't be used here because the sidebar reads raw mouse in this
        // immediate-mode pass — gating on the shared flag would bail spuriously.
        int scrollDelta = mouse.ScrollWheelValue - _prevScrollValue;
        if (scrollDelta != 0 && overPanel && !popupBlocking)
        {
            int tabIdx = (int)ActiveTab;
            _tabScroll[tabIdx] = MathF.Max(0, _tabScroll[tabIdx] - scrollDelta * 0.2f);
        }

        bool ctrlDown = kb.IsKeyDown(Keys.LeftControl) || kb.IsKeyDown(Keys.RightControl);

        // --- Save (Ctrl+S) — suppress when text field is active ---
        if (!textEditing && ctrlDown && kb.IsKeyDown(Keys.S) && _prevKb.IsKeyUp(Keys.S))
            SaveMap();

        // --- Load (Ctrl+L) — suppress when text field is active ---
        if (!textEditing && ctrlDown && kb.IsKeyDown(Keys.L) && _prevKb.IsKeyUp(Keys.L))
            LoadMap();

        // --- Undo (Ctrl+Z) — suppress when text field is active ---
        if (!textEditing && ctrlDown && kb.IsKeyDown(Keys.Z) && _prevKb.IsKeyUp(Keys.Z))
        {
            PerformUndo();
            _statusMessage = $"Undo ({_undoStack.Count} remaining)";
            _statusTimer = 1.5f;
        }

        // --- Zone copy/paste (Ctrl+C / Ctrl+V, Zones tab only). Must stay above the
        // SelectionStateHash snapshot below so a paste's selection change abandons
        // any in-progress text-field edit. ---
        if (!textEditing && ctrlDown && ActiveTab == MapEditorTab.Zones)
        {
            if (kb.IsKeyDown(Keys.C) && _prevKb.IsKeyUp(Keys.C) &&
                SelectedZoneIndex >= 0 && SelectedZoneIndex < _zoneSystem.Count)
            {
                _zoneClipboard = CloneZone(_zoneSystem.Zones[SelectedZoneIndex]);
                _statusMessage = $"Copied zone '{_zoneClipboard.Name}'";
                _statusTimer = 1.5f;
            }
            if (kb.IsKeyDown(Keys.V) && _prevKb.IsKeyUp(Keys.V) && _zoneClipboard != null)
            {
                var copy = CloneZone(_zoneClipboard);
                copy.Id = NextZoneId();
                copy.Name = NextCopyName(_zoneClipboard.Name);
                // Center on the mouse; over the side panel there's no meaningful
                // world position, so fall back to the camera center.
                Vec2 at = overPanel
                    ? _camera.Position
                    : _camera.ScreenToWorld(new Vector2(mouse.X, mouse.Y), screenW, screenH);
                copy.X = at.X;
                copy.Y = at.Y;
                SelectedZoneIndex = _zoneSystem.Add(copy);
                PushUndo(new UndoZonePlace { Owner = this, ZoneId = copy.Id });
                _statusMessage = $"Pasted zone '{copy.Name}'";
                _statusTimer = 1.5f;
            }
        }

        // --- Delete selected zone (Delete key, Zones tab only). ---
        if (!textEditing && ActiveTab == MapEditorTab.Zones &&
            kb.IsKeyDown(Keys.Delete) && _prevKb.IsKeyUp(Keys.Delete))
        {
            DeleteZone(SelectedZoneIndex);
        }

        // Snapshot selection state — if any per-tab updater changes it below,
        // abandon the active text-field edit. The tabs reuse static field ids
        // ("trig_name", "region_x", …) across objects, so an in-progress buffer
        // would otherwise be committed into the NEWLY selected object during
        // this same frame's Draw.
        int selectionHashBefore = SelectionStateHash();

        // Clicks on the tab rows or the bottom bar belong to those bars — never
        // to tab content that happens to be SCROLLED underneath them. The
        // hand-rolled per-tab hit-tests compute item Y from scroll offsets with
        // no bounds check, so without this a click on "Save" could also add or
        // delete an invisible list item, and the click that switches tabs was
        // re-processed by the NEW tab's updater at a scrolled position.
        bool rawLeftClick = leftClick;
        {
            int contentTopY = panelY + TabRowHeight * 2 + 2;
            int bottomBarTop = panelY + (screenH - 20) - 92;
            if (overPanel && (mouse.Y < contentTopY || mouse.Y >= bottomBarTop))
                leftClick = false;
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
            case MapEditorTab.Units:
                UpdateUnitsTab(mouse, kb, leftClick, overPanel, panelX, panelY, screenW, screenH);
                break;
            case MapEditorTab.Zones:
                UpdateZonesTab(mouse, leftClick, leftDown, leftUp, overPanel, screenW, screenH);
                break;
            case MapEditorTab.ProcGen:
                UpdateProcGenTab(mouse, leftDown, leftUp, overPanel, screenW, screenH, dt);
                break;
        }

        // Selection changed (list click, world click, tab hotkey…) → drop any
        // in-progress text-field edit so it can't commit into the new object.
        if (_eb != null && SelectionStateHash() != selectionHashBefore)
            _eb.ClearActiveField();

        // Update texture file browser input
        _textureBrowser.Update(_eb, mouse, _prevMouse, kb, _prevKb);

        // Save / Load / Undo button clicks (mirrors the Draw-time button layout).
        // Uses the RAW click — the bar-region suppression above only applies to
        // tab-content hit-tests.
        UpdateBottomBarClicks(rawLeftClick, mouse, panelX, panelY, screenH);

        // Keep the persisted "last tab" in sync in-memory every frame the editor is
        // open, so ANY settings save path (Exiting, the settings window, dev command,
        // DualSave) writes the current tab — not the stale default. SaveIfChanged
        // means updating an already-correct value is free.
        if (_gameData != null)
            _gameData.Settings.General.MapEditorLastTab = (int)ActiveTab;

        _prevMouse = mouse;
        _prevKb = kb;
        _prevScrollValue = mouse.ScrollWheelValue;
    }

    /// <summary>Combined hash of every per-tab selection index (plus the active
    /// tab). Compared before/after the per-tab updaters: any change abandons the
    /// active text-field edit, because the tabs reuse static field ids across
    /// objects and a live buffer would commit into the newly selected object.</summary>
    private int SelectionStateHash()
    {
        var h = new HashCode();
        h.Add((int)ActiveTab);
        h.Add(SelectedGroundType);
        h.Add(SelectedGrassType);
        h.Add(SelectedEnvDefIndex);
        h.Add(SelectedWallType);
        h.Add(SelectedRoadTexDef);
        h.Add(SelectedRoadIndex);
        h.Add(SelectedRoadPoint);
        h.Add(SelectedJunctionIndex);
        h.Add(SelectedRegionIndex);
        h.Add(SelectedPatrolRoute);
        h.Add(SelectedWaypointIndex);
        h.Add(SelectedZoneIndex);
        h.Add(SelectedTriggerDefIndex);
        h.Add(SelectedTriggerInstanceIndex);
        h.Add(SelectedConditionIndex);
        h.Add(SelectedEffectIndex);
        h.Add(_triggerSubSection);
        h.Add(_selectedUnitDefIdx);
        h.Add(SelectedProcGenStyle);
        return h.ToHashCode();
    }

    /// <summary>
    /// Edge-detect clicks on the Save / Load / Undo buttons in the bottom bar.
    /// Called from Update so we compare against the previous-frame _prevMouse
    /// *before* it's overwritten — doing this in Draw would always compare the
    /// current mouse against itself and never fire.
    /// </summary>
    private void UpdateBottomBarClicks(bool leftClick, MouseState mouse, int panelX, int panelY, int screenH)
    {
        if (!leftClick) return;

        int panelH = screenH - 20;
        int bottomH = 90;
        int buttonRowY = panelY + panelH - bottomH + 2 + FieldHeight + 2;

        if (mouse.Y < buttonRowY || mouse.Y >= buttonRowY + ButtonHeight) return;

        int btnW3 = (PanelWidth - Margin * 2 - 8) / 3;
        int relX = mouse.X - (panelX + Margin);

        if (relX >= 0 && relX < btnW3)
        {
            SaveMap();
        }
        else if (relX >= btnW3 + 4 && relX < btnW3 * 2 + 4)
        {
            LoadMap();
        }
        else if (relX >= (btnW3 + 4) * 2 && relX < (btnW3 + 4) * 2 + btnW3)
        {
            PerformUndo();
            _statusMessage = $"Undo ({_undoStack.Count} remaining)";
            _statusTimer = 1.5f;
        }
    }

    // ========================================================================
    //  Draw
    // ========================================================================

    /// <summary>World-space overlays for the active tab (brush cursor, debug
    /// grids, road/region handles, zone shapes). MUST be called outside the tab
    /// content scissor clip — the GPU discards anything drawn outside it.</summary>
    private void DrawWorldOverlaysForActiveTab(int screenW, int screenH)
    {
        bool overPanel = IsMouseOverPanel(screenW, screenH);
        switch (ActiveTab)
        {
            case MapEditorTab.Ground:
            case MapEditorTab.Walls:
                if (!overPanel) DrawBrushCursor(screenW, screenH);
                break;
            case MapEditorTab.Grass:
                if (_grassGridDebugEnabled && _grassMap.Length > 0)
                    DrawGrassGridOverlay(screenW, screenH);
                if (!overPanel) DrawBrushCursor(screenW, screenH);
                break;
            case MapEditorTab.Objects:
                if (_objectPaintMode && !overPanel) DrawBrushCursor(screenW, screenH);
                if (!_objectPaintMode && !overPanel && !IsAnyPopupBlocking())
                    DrawObjectPlacementGhost(screenW, screenH);
                if (_showCollisions) DrawCollisionOverlay(screenW, screenH);
                break;
            case MapEditorTab.Roads:
                DrawRoadOverlays(screenW, screenH);
                break;
            case MapEditorTab.Regions:
                DrawRegionOverlays(screenW, screenH);
                break;
            case MapEditorTab.Zones:
                DrawZoneOverlays(screenW, screenH);
                break;
            case MapEditorTab.ProcGen:
                if (!overPanel) DrawProcGenBrushCursor(screenW, screenH);
                break;
        }
    }

    /// <summary>Semi-transparent ghost of the selected object under the cursor
    /// (Objects tab, single-place mode): grey where CanPlaceObject accepts the
    /// spot, red where it doesn't — same check and default scale the click-to-
    /// place path uses. Group selections preview their highest-weight member
    /// (the actual placement re-rolls per click, so the ghost can't match it).</summary>
    private void DrawObjectPlacementGhost(int screenW, int screenH)
    {
        // Never ResolveObjectDefIndex() here — it re-rolls the weighted random
        // group pick per call, so the ghost would flicker between group members
        // every frame.
        int defIdx = SelectedEnvDefIndex;
        if (IsEnvGroupSelected)
        {
            var members = GetSelectedGroupMembers();
            if (members.Count == 0) return;
            defIdx = members[0].defIdx;
            float bestW = members[0].weight;
            for (int i = 1; i < members.Count; i++)
                if (members[i].weight > bestW) { defIdx = members[i].defIdx; bestW = members[i].weight; }
        }
        if (defIdx < 0 || defIdx >= _envSystem.DefCount) return;

        // MousePos, not the raw MouseState: identical for a real cursor, but it
        // also honors the dev `mousepos` override so headless drives can test.
        Vec2 worldPos = _camera.ScreenToWorld(_eb._input.MousePos, screenW, screenH);
        var screenPos = _camera.WorldToScreen(worldPos, 0f, screenW, screenH);
        Render.EnvGhostRenderer.Draw(Scope, _envSystem, defIdx, screenPos, _camera.Zoom,
            _envSystem.CanPlaceObject(defIdx, worldPos.X, worldPos.Y),
            Render.EnvGhostRenderer.EditorValidTint, _pixel);
    }

    public void Draw(int screenW, int screenH)
    {
        int panelX = screenW - PanelWidth - 10;
        int panelY = 10;
        int panelH = screenH - 20;

        // World overlays draw first — under the panels and OUTSIDE the tab
        // scissor clip. (They can't live in the tab draw methods: the tab body
        // is scissor-clipped to the panel rect, which silently discarded the
        // brush cursor / grass grid / collision ellipses / road & region
        // overlays everywhere except inside the panel itself.)
        DrawWorldOverlaysForActiveTab(screenW, screenH);

        // Placed unit markers are world content too (visible on all tabs):
        // they must draw BEFORE the panel so a marker whose screen position
        // falls behind the side panel doesn't paint on top of the UI.
        DrawPlacedUnitMarkers(screenW, screenH);

        // Panel background
        Scope.Draw(_pixel, new Rectangle(panelX, panelY, PanelWidth, panelH), BgColor);

        // Tab rows
        DrawTabRows(panelX, panelY);

        // Separator under tabs
        int tabsBottom = panelY + TabRowHeight * 2;
        Scope.Draw(_pixel, new Rectangle(panelX, tabsBottom, PanelWidth, 1), SeparatorColor);

        // Content area (reserve 92px for bottom bar: filename + buttons + shortcuts + status)
        int contentY = tabsBottom + 2;
        int contentH = panelH - (tabsBottom - panelY) - 2 - 92;

        // Scissor-clip the tab content panel so partially-scrolled list items
        // can't spill above the section header or below the bottom bar. Each
        // tab is hand-rolled (no DrawScrollableList wrapper) and only culls
        // *fully*-offscreen items, so without this clip a half-scrolled top
        // entry would draw its background up into the tab row. Dropdowns
        // inside tabs are deferred to DrawDropdownOverlays() after the tab
        // panel finishes, so clipping the tab body doesn't truncate them.
        var tabContentRect = new Rectangle(panelX, contentY, PanelWidth, contentH);
        _eb.BeginClip(tabContentRect);

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
            case MapEditorTab.Units:
                DrawUnitsTab(panelX, contentY, contentH, screenW, screenH);
                break;
            case MapEditorTab.Zones:
                DrawZonesTab(panelX, contentY, contentH);
                break;
            case MapEditorTab.ProcGen:
                DrawProcGenTab(panelX, contentY, contentH);
                break;
        }

        _eb.EndClip();

        // The left village panel draws after EndClip (the right-panel scissor would hide
        // it) and above the zone overlays drawn at the top of this method.
        if (ActiveTab == MapEditorTab.Zones)
            DrawZoneLeftPanel(screenW, screenH);

        // Bottom bar: map filename, Save/Load buttons, undo info, status message
        int bottomH = 90; // height of the bottom section
        int bottomY = panelY + panelH - bottomH;
        Scope.Draw(_pixel, new Rectangle(panelX, bottomY, PanelWidth, 1), SeparatorColor);
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

        // Save / Load / Undo buttons. Click handling lives in Update() (see
        // UpdateBottomBarClicks) — doing it here would compare against _prevMouse
        // after it was already overwritten, which is always-false.
        int btnW3 = (PanelWidth - Margin * 2 - 8) / 3;
        DrawButtonRect("Save", panelX + Margin, bottomY, btnW3, ButtonHeight, ButtonBg);
        DrawButtonRect("Load", panelX + Margin + btnW3 + 4, bottomY, btnW3, ButtonHeight, ButtonBg);
        DrawButtonRect($"Undo ({_undoStack.Count})", panelX + Margin + (btnW3 + 4) * 2, bottomY, btnW3, ButtonHeight,
            _undoStack.Count > 0 ? AccentColor : ButtonBg);
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

        // "Clear All Units" confirmation
        if (_eb != null && _confirmClearUnits)
        {
            if (_eb.DrawConfirmDialog("Clear All Units",
                $"Remove all {_placedUnits.Count} placed units? (Undoable with Ctrl+Z)",
                ref _confirmClearUnits))
            {
                PushUndo(new UndoUnitClearAll
                {
                    Units = _placedUnits,
                    Cleared = new List<PlacedUnit>(_placedUnits),
                });
                _placedUnits.Clear();
            }
        }

        // Dropdown overlays (drawn last, on top of everything)
        if (_eb != null)
        {
            _eb.DrawDropdownOverlays();
            // Color picker popup — must come after all other UI so the swatch
            // popups (grass tints, etc.) actually render and accept input.
            _eb.DrawColorPickerPopup();
        }
    }

    // ========================================================================
    //  Tab Rows
    // ========================================================================

    private void DrawTabRows(int panelX, int panelY)
    {
        var mouse = _eb._input.Mouse;

        // Row 1: Ground, Grass, Objects, Walls
        int tabW1 = PanelWidth / TabRow1.Length;
        for (int i = 0; i < TabRow1.Length; i++)
        {
            var tab = (MapEditorTab)i;
            bool active = ActiveTab == tab;
            var bg = active ? TabActiveColor : TabInactiveColor;
            var rect = new Rectangle(panelX + i * tabW1, panelY, tabW1, TabRowHeight);

            bool hovered = IsInRect(mouse, rect);
            if (!active && hovered) bg = ButtonHoverColor;
            Scope.Draw(_pixel, rect, bg);

            if (active)
                Scope.Draw(_pixel, new Rectangle(rect.X, rect.Y + TabRowHeight - 2, tabW1, 2), new Color(180, 140, 80));

            DrawTextCentered(TabRow1[i], rect, TextColor);
        }

        // Row 2: Roads, Regions, Triggers, Units, Zones
        int tabW2 = PanelWidth / TabRow2.Length;
        int row2Y = panelY + TabRowHeight;
        for (int i = 0; i < TabRow2.Length; i++)
        {
            var tab = (MapEditorTab)(i + TabRow1.Length);
            bool active = ActiveTab == tab;
            var bg = active ? TabActiveColor : TabInactiveColor;
            var rect = new Rectangle(panelX + i * tabW2, row2Y, tabW2, TabRowHeight);

            bool hovered = IsInRect(mouse, rect);
            if (!active && hovered) bg = ButtonHoverColor;
            Scope.Draw(_pixel, rect, bg);

            if (active)
                Scope.Draw(_pixel, new Rectangle(rect.X, rect.Y + TabRowHeight - 2, tabW2, 2), new Color(180, 140, 80));

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

    /// <summary>
    /// Vertex-map key matching <see cref="UndoGroundStroke"/>'s encoding
    /// (vx = key % 100000, vy = key / 100000).
    /// </summary>
    private static long GroundVertexKey(int vx, int vy) => (long)vy * 100000 + vx;

    /// <summary>
    /// Set one ground vertex, recording its previous value into <paramref name="oldVals"/>
    /// for undo. Returns true if the value actually changed. Does NOT fire the
    /// texture-update callback (callers batch that).
    /// </summary>
    private bool SetGroundVertexRecorded(int vx, int vy, byte typeIdx, Dictionary<long, byte> oldVals)
    {
        if (vx < 0 || vx >= _groundSystem.VertexW || vy < 0 || vy >= _groundSystem.VertexH)
            return false;
        byte oldVal = _groundSystem.GetVertex(vx, vy);
        if (oldVal == typeIdx) return false;
        oldVals.TryAdd(GroundVertexKey(vx, vy), oldVal);
        _groundSystem.SetVertex(vx, vy, typeIdx);
        return true;
    }

    /// <summary>Resolve a ground-type name to its live index in the ground system,
    /// or -1 if no type has that name. Case-insensitive; matches Name or Id.</summary>
    private int ResolveGroundTypeIndex(string name)
    {
        if (string.IsNullOrEmpty(name)) return -1;
        for (int i = 0; i < _groundSystem.TypeCount; i++)
        {
            var d = _groundSystem.GetTypeDef(i);
            if (string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(d.Id, name, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Stamp an auto-ground patch under a placed object, driven by
    /// <paramref name="s"/>: no-op unless it's enabled and its ground type resolves.
    /// Thin wrapper over <see cref="StampGroundPatch"/> — this is the single entry
    /// point shared by the Objects tab and every ProcGen pool.
    /// </summary>
    private bool StampAutoGround(AutoGroundSettings s, float worldX, float worldY, Dictionary<long, byte> oldVals)
    {
        if (!s.Enabled) return false;
        int typeIdx = ResolveGroundTypeIndex(s.TypeName);
        if (typeIdx < 0) return false;
        return StampGroundPatch(worldX, worldY, typeIdx, s.Size, s.Noise, oldVals);
    }

    /// <summary>
    /// Stamp a ground patch: a circle of radius <paramref name="size"/> at world
    /// (worldX, worldY) of ground type <paramref name="typeIdx"/>, plus
    /// <paramref name="noise"/> extra tiles grown organically off the patch edge for
    /// a ragged border. Old values are recorded into <paramref name="oldVals"/> for
    /// undo. Returns true if anything changed. Does NOT fire the texture-update
    /// callback — the caller invokes it once per placement / per stroke frame.
    /// </summary>
    private bool StampGroundPatch(float worldX, float worldY, int typeIdxInt, int size, int noise,
        Dictionary<long, byte> oldVals)
    {
        if (typeIdxInt < 0 || typeIdxInt >= _groundSystem.TypeCount)
            return false;

        int vx = (int)MathF.Round(worldX);
        int vy = (int)MathF.Round(worldY);
        byte typeIdx = (byte)typeIdxInt;
        int r = Math.Max(0, size);
        bool changed = false;

        // patch = every tile considered part of the blob (whether or not its value
        // changed) so noise growth never re-picks an interior tile.
        var patch = new HashSet<long>();

        // Core circular patch.
        for (int dy = -r; dy <= r; dy++)
        {
            for (int dx = -r; dx <= r; dx++)
            {
                if (dx * dx + dy * dy > r * r) continue;
                int cx = vx + dx, cy = vy + dy;
                if (cx < 0 || cx >= _groundSystem.VertexW || cy < 0 || cy >= _groundSystem.VertexH) continue;
                patch.Add(GroundVertexKey(cx, cy));
                if (SetGroundVertexRecorded(cx, cy, typeIdx, oldVals)) changed = true;
            }
        }

        // Ragged edge: grow `noise` tiles outward, each picked at random
        // from the current set of tiles adjacent to (but outside) the patch.
        if (noise > 0 && patch.Count > 0)
        {
            var candidates = new HashSet<long>();
            void Consider(int nx, int ny)
            {
                if (nx < 0 || nx >= _groundSystem.VertexW || ny < 0 || ny >= _groundSystem.VertexH) return;
                long k = GroundVertexKey(nx, ny);
                if (!patch.Contains(k)) candidates.Add(k);
            }
            void AddNeighbours(int x, int y) { Consider(x + 1, y); Consider(x - 1, y); Consider(x, y + 1); Consider(x, y - 1); }

            foreach (long k in patch) AddNeighbours((int)(k % 100000), (int)(k / 100000));

            var pickList = new List<long>();
            for (int i = 0; i < noise && candidates.Count > 0; i++)
            {
                pickList.Clear();
                pickList.AddRange(candidates);
                long pick = pickList[Random.Shared.Next(pickList.Count)];
                candidates.Remove(pick);
                patch.Add(pick);
                int px = (int)(pick % 100000), py = (int)(pick / 100000);
                if (SetGroundVertexRecorded(px, py, typeIdx, oldVals)) changed = true;
                AddNeighbours(px, py);
            }
        }

        return changed;
    }

    private void DrawGroundTab(int panelX, int contentY, int contentH, int screenW, int screenH)
    {
        int viewBottom = contentY + contentH; // tab clip bottom, before the header advances contentY
        DrawSectionHeader(panelX, ref contentY, $"Ground Types ({_groundSystem.TypeCount})");

        var mouse = _eb._input.Mouse;
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
                Scope.Draw(_pixel, btnRect, bgColor);

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
            Scope.Draw(_pixel, new Rectangle(panelX, addY, PanelWidth, 1), SeparatorColor);
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
                _textureBrowser.Open(GamePaths.Resolve("assets/Environment/Ground"), def.TexturePath, path =>
                {
                    def.TexturePath = path;
                    _groundSystem.LoadTextures(_device);
                });
            }
            addY += FieldHeight + 2;

            // Corrupted variant: dropdown of all OTHER type IDs (or "(none)").
            // Death fog rolls a per-second chance to swap this type to the chosen
            // variant on each vertex inside it. Leave blank if the type should
            // never corrupt (e.g. cobblestone).
            var corrIds = new List<string> { "(none)" };
            for (int i = 0; i < _groundSystem.TypeCount; i++)
            {
                if (i == SelectedGroundType) continue;
                corrIds.Add(_groundSystem.GetTypeDef(i).Id);
            }
            string curCorr = string.IsNullOrEmpty(def.CorruptedTypeId) ? "(none)" : def.CorruptedTypeId;
            string newCorr = _eb.DrawCombo("ground_corr", "Corrupted", curCorr, corrIds.ToArray(), panelX + Margin, addY, fw);
            if (newCorr != curCorr)
                def.CorruptedTypeId = newCorr == "(none)" ? "" : newCorr;
            addY += FieldHeight + 2;
        }

        // Brush size
        addY += 4;
        DrawBrushSizeControl(panelX, addY);

        // Info
        addY += ButtonHeight + 8;
        Scope.Draw(_pixel, new Rectangle(panelX, addY, PanelWidth, 1), SeparatorColor);
        addY += 4;
        DrawSmallText($"World: {_groundSystem.WorldW}x{_groundSystem.WorldH}", panelX + Margin, addY, TextDim);
        addY += LineHeight;
        DrawSmallText($"Vertices: {_groundSystem.VertexW}x{_groundSystem.VertexH}", panelX + Margin, addY, TextDim);
        addY += LineHeight;
        DrawSmallText("Left-drag to paint | Q/E brush size", panelX + Margin, addY, TextDim);
        // (Brush cursor drawn by DrawWorldOverlaysForActiveTab, outside the clip.)

        DrawTabScrollbar(panelX, contentY, viewBottom, addY + LineHeight, ref _tabScroll[0]);
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

            // Show/Hide Cell Grid toggle (one row below eraser)
            int gridBtnY = eraserY + ButtonHeight + 4;
            if (mouse.Y >= gridBtnY && mouse.Y < gridBtnY + ButtonHeight &&
                mouse.X >= panelX + Margin && mouse.X < panelX + PanelWidth - Margin)
            {
                _grassGridDebugEnabled = !_grassGridDebugEnabled;
            }

            // Type list (starts after eraser + grid toggle)
            int listY = gridBtnY + ButtonHeight + 4;
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
                _onGrassTypesChanged?.Invoke();
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
                    if (v == 0) continue; // empty
                    int typeIdx = v - 1; // 1-based to 0-based
                    if (typeIdx == SelectedGrassType) _grassMap[gi] = 0;
                    else if (typeIdx > SelectedGrassType) _grassMap[gi] = (byte)(v - 1);
                }
                SelectedGrassType = Math.Min(SelectedGrassType, _grassTypes.Count - 1);
                _onGrassTypesChanged?.Invoke();
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

        // Right-drag to erase grass regardless of the selected type (mirrors the
        // walls tab's right-drag erase). Lets you wipe grass without first
        // selecting the eraser tool.
        // Gate the right-drag erase on the popup state (mirrors Update's rightDown)
        // so grass isn't wiped while a color picker / dropdown / texture browser is open.
        bool rightDown = !IsAnyPopupBlocking() && mouse.RightButton == ButtonState.Pressed;
        bool rightUp = mouse.RightButton == ButtonState.Released && _prevMouse.RightButton == ButtonState.Pressed;
        if (rightDown && !overPanel && _grassMap.Length > 0)
        {
            if (!_painting)
                _grassStrokeOld = new Dictionary<int, byte>();
            _painting = true;
            PaintGrass(mouse, screenW, screenH, erase: true);
        }
        if (rightUp)
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

    private void PaintGrass(MouseState mouse, int screenW, int screenH, bool erase = false)
    {
        if (_grassW == 0 || _grassH == 0) return;

        Vec2 worldPos = _camera.ScreenToWorld(new Vector2(mouse.X, mouse.Y), screenW, screenH);
        // Grass cells use cellSize (e.g. 0.8) — must match GrassRenderer mapping
        float cs = _grassCellSize > 0f ? _grassCellSize : 0.8f;
        int cx = (int)MathF.Floor(worldPos.X / cs);
        int cy = (int)MathF.Floor(worldPos.Y / cs);

        byte paintValue;
        if (erase || _grassEraserSelected)
            paintValue = 0; // 0 = no grass (previously 255 which the renderer treated as grass type 254)
        else if (SelectedGrassType >= 0 && SelectedGrassType < _grassTypes.Count)
            paintValue = (byte)(SelectedGrassType + 1); // 1-based: 0 reserved for empty
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

    /// <summary>
    /// Scan assets/Environment/Grass/ for PNG sprites. Returned paths are project-
    /// relative (e.g. "assets/Environment/Grass/GreenGrass1.png") — cached per
    /// draw call so the directory isn't scanned every frame inside the sprite
    /// slot UI loop.
    /// </summary>
    private string[] _grassSpriteCache = System.Array.Empty<string>();
    private float _grassSpriteCacheAge = 999f;
    private string[] GetAvailableGrassSprites()
    {
        // Refresh at most every 2 seconds in case the user drops a new PNG in.
        _grassSpriteCacheAge += 1f / 60f;
        if (_grassSpriteCacheAge < 2f && _grassSpriteCache.Length > 0)
            return _grassSpriteCache;

        _grassSpriteCacheAge = 0f;
        string dir = Core.GamePaths.Resolve("assets/Environment/Grass");
        if (!System.IO.Directory.Exists(dir))
        {
            _grassSpriteCache = System.Array.Empty<string>();
            return _grassSpriteCache;
        }

        var files = System.IO.Directory.GetFiles(dir, "*.png");
        var rel = new string[files.Length];
        for (int i = 0; i < files.Length; i++)
            rel[i] = Core.GamePaths.MakeRelative(files[i]);
        System.Array.Sort(rel, System.StringComparer.OrdinalIgnoreCase);
        _grassSpriteCache = rel;
        return _grassSpriteCache;
    }

    private void DrawGrassTab(int panelX, int contentY, int contentH, int screenW, int screenH)
    {
        int viewBottom = contentY + contentH; // tab clip bottom, before the header advances contentY
        DrawSectionHeader(panelX, ref contentY, $"Grass Types ({_grassTypes.Count})");

        var mouse = _eb._input.Mouse;
        float scroll = _tabScroll[1];
        int y = contentY - (int)scroll;

        // Eraser entry
        {
            var btnRect = new Rectangle(panelX + Margin, y, PanelWidth - Margin * 2, ButtonHeight);
            var bg = _grassEraserSelected ? HighlightColor : (IsInRect(mouse, btnRect) ? ButtonHoverColor : Color.Transparent);
            if (bg != Color.Transparent)
                Scope.Draw(_pixel, btnRect, bg);
            // X swatch for eraser
            Scope.Draw(_pixel, new Rectangle(panelX + Margin + 4, y + 4, 14, 14), new Color(180, 60, 60));
            DrawSmallText("Eraser", panelX + Margin + 24, y + 3, TextColor);
        }
        y += ButtonHeight + 4;

        // Show Cell Grid toggle
        {
            var btnRect = new Rectangle(panelX + Margin, y, PanelWidth - Margin * 2, ButtonHeight);
            var bg = _grassGridDebugEnabled ? HighlightColor : (IsInRect(mouse, btnRect) ? ButtonHoverColor : Color.Transparent);
            if (bg != Color.Transparent)
                Scope.Draw(_pixel, btnRect, bg);
            Scope.Draw(_pixel, new Rectangle(panelX + Margin + 4, y + 4, 14, 14), new Color(200, 200, 120));
            DrawSmallText(_grassGridDebugEnabled ? "Hide Cell Grid" : "Show Cell Grid",
                panelX + Margin + 24, y + 3, TextColor);
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
                Scope.Draw(_pixel, btnRect, bg);

            // Default tint swatch (left), corrupted tint swatch (right of it).
            Scope.Draw(_pixel, new Rectangle(panelX + Margin + 4, y + 4, 14, 14), gt.DefaultTint);
            Scope.Draw(_pixel, new Rectangle(panelX + Margin + 22, y + 4, 14, 14), gt.CorruptedTint);

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
            Scope.Draw(_pixel, new Rectangle(panelX, y, PanelWidth, 1), SeparatorColor);
            y += 4;
            var gt = _grassTypes[SelectedGrassType];
            int fw = PanelWidth - Margin * 2;
            bool changed = false;

            string newName = _eb.DrawTextField("grass_name", "Name", gt.Name, panelX + Margin, y, fw);
            if (newName != gt.Name) { gt.Name = newName; changed = true; }
            y += FieldHeight + 2;

            // Default tint — multiplied with the sprite while healthy. White = no tint.
            DrawSmallText("Default Tint:", panelX + Margin, y, AccentColor);
            var defHdr = new HdrColor(gt.DefaultTint.R, gt.DefaultTint.G, gt.DefaultTint.B, gt.DefaultTint.A, 1.0f);
            if (_eb.DrawColorSwatch("grass_defaultTint", panelX + Margin + 90, y, 40, 18, ref defHdr, hideIntensity: true))
            {
                gt.DefaultTint = new Color(defHdr.R, defHdr.G, defHdr.B, defHdr.A);
                changed = true;
            }
            y += FieldHeight + 2;

            // Corrupted tint — destination tint of the 10s fade once the underlying
            // ground vertex flips. Renderer lerps Default → Corrupted over the fade.
            DrawSmallText("Corrupted Tint:", panelX + Margin, y, AccentColor);
            var corHdr = new HdrColor(gt.CorruptedTint.R, gt.CorruptedTint.G, gt.CorruptedTint.B, gt.CorruptedTint.A, 1.0f);
            if (_eb.DrawColorSwatch("grass_corruptedTint", panelX + Margin + 90, y, 40, 18, ref corHdr, hideIntensity: true))
            {
                gt.CorruptedTint = new Color(corHdr.R, corHdr.G, corHdr.B, corHdr.A);
                changed = true;
            }
            y += FieldHeight + 2;

            // --- Per-type tuft scale (multiplier on base cell-size footprint) ---
            float newScale = _eb.DrawFloatField("grass_scale", "Scale", gt.Scale, panelX + Margin, y, fw, 0.1f);
            if (newScale < 0.1f) newScale = 0.1f;
            if (MathF.Abs(newScale - gt.Scale) > 0.001f) { gt.Scale = newScale; changed = true; }
            y += FieldHeight + 2;

            // --- Per-type density. <1 = probability of a tuft; >=1 = tuft count per cell. ---
            float newDensity = _eb.DrawFloatField("grass_density", "Density", gt.Density, panelX + Margin, y, fw, 0.1f);
            if (newDensity < 0f) newDensity = 0f;
            if (MathF.Abs(newDensity - gt.Density) > 0.001f) { gt.Density = newDensity; changed = true; }
            y += FieldHeight + 6;

            // --- Sprite slots (up to MaxSprites per type) ---
            DrawSmallText($"Sprites (up to {GrassTypeDef.MaxSprites}):", panelX + Margin, y, AccentColor);
            y += LineHeight;

            // Build a filename list of available grass sprites on disk for the dropdown.
            var available = GetAvailableGrassSprites();
            var options = new string[available.Length];
            for (int i = 0; i < available.Length; i++)
                options[i] = System.IO.Path.GetFileName(available[i]);

            // Keep the list padded to MaxSprites so every slot gets a dropdown; empty
            // strings serialize as "(none)" and render as "unused".
            while (gt.SpritePaths.Count < GrassTypeDef.MaxSprites) gt.SpritePaths.Add("");

            for (int si = 0; si < GrassTypeDef.MaxSprites; si++)
            {
                string currentPath = gt.SpritePaths[si];
                string currentName = string.IsNullOrEmpty(currentPath)
                    ? ""
                    : System.IO.Path.GetFileName(currentPath);

                string pickedName = _eb.DrawCombo(
                    $"grass_sprite_{si}", $"Slot {si + 1}", currentName, options,
                    panelX + Margin, y, fw, allowNone: true);

                if (pickedName != currentName)
                {
                    if (string.IsNullOrEmpty(pickedName))
                    {
                        gt.SpritePaths[si] = "";
                    }
                    else
                    {
                        for (int oi = 0; oi < options.Length; oi++)
                        {
                            if (options[oi] == pickedName)
                            {
                                gt.SpritePaths[si] = available[oi];
                                break;
                            }
                        }
                    }
                    changed = true;
                }
                y += FieldHeight + 2;
            }

            if (changed) _onGrassTypesChanged?.Invoke();
        }
        else if (!_grassEraserSelected && SelectedGrassType >= 0 && SelectedGrassType < _grassTypes.Count)
        {
            // Fallback if no EditorBase
            Scope.Draw(_pixel, new Rectangle(panelX, y, PanelWidth, 1), SeparatorColor);
            y += 4;
            var gt = _grassTypes[SelectedGrassType];
            DrawSmallText($"Name: {gt.Name}", panelX + Margin, y, TextBright); y += LineHeight;
            int assigned = 0;
            foreach (var p in gt.SpritePaths) if (!string.IsNullOrEmpty(p)) assigned++;
            DrawSmallText($"Sprites: {assigned}/{GrassTypeDef.MaxSprites}", panelX + Margin, y, TextColor); y += LineHeight;
        }

        // Brush size
        y += 4;
        DrawBrushSizeControl(panelX, y);
        y += ButtonHeight + 4;
        DrawSmallText($"Grass map: {_grassW}x{_grassH}", panelX + Margin, y, TextDim);
        // (Grid overlay + brush cursor drawn by DrawWorldOverlaysForActiveTab, outside the clip.)

        DrawTabScrollbar(panelX, contentY, viewBottom, y + LineHeight, ref _tabScroll[1]);
    }

    /// <summary>
    /// Debug overlay for the Grass tab: draws cell boundaries over the visible
    /// area and semi-transparent fill on painted cells (tinted by grass type).
    /// Lets the user see how tuft sprites map back to the underlying grid.
    /// </summary>
    private void DrawGrassGridOverlay(int screenW, int screenH)
    {
        float cs = _grassCellSize > 0f ? _grassCellSize : 0.8f;

        // View frustum in world space (cell coords, clamped to map bounds)
        float zoom = _camera.Zoom;
        float yRatio = _camera.YRatio;
        float viewLeft   = _camera.Position.X - screenW / (2f * zoom) - cs;
        float viewRight  = _camera.Position.X + screenW / (2f * zoom) + cs;
        float viewTop    = _camera.Position.Y - screenH / (2f * zoom * yRatio) - cs;
        float viewBottom = _camera.Position.Y + screenH / (2f * zoom * yRatio) + cs;

        int cx0 = Math.Max(0, (int)MathF.Floor(viewLeft / cs));
        int cy0 = Math.Max(0, (int)MathF.Floor(viewTop  / cs));
        int cx1 = Math.Min(_grassW - 1, (int)MathF.Ceiling(viewRight  / cs));
        int cy1 = Math.Min(_grassH - 1, (int)MathF.Ceiling(viewBottom / cs));

        var lineColor = new Color(255, 255, 255, 60);
        // Fill painted cells with a tint from their grass type's base color.
        for (int cy = cy0; cy <= cy1; cy++)
        {
            for (int cx = cx0; cx <= cx1; cx++)
            {
                byte v = _grassMap[cy * _grassW + cx];
                if (v == 0) continue;
                int typeIdx = v - 1;
                if (typeIdx < 0 || typeIdx >= _grassTypes.Count) continue;

                var gt = _grassTypes[typeIdx];
                var fill = new Color(gt.DefaultTint.R, gt.DefaultTint.G, gt.DefaultTint.B, (byte)80);
                FillGrassCellQuad(cx, cy, cs, screenW, screenH, fill);
            }
        }

        // Cell boundary lines.
        for (int cy = cy0; cy <= cy1 + 1; cy++)
        {
            var a = _camera.WorldToScreen(new Vec2(cx0 * cs, cy * cs), 0f, screenW, screenH);
            var b = _camera.WorldToScreen(new Vec2((cx1 + 1) * cs, cy * cs), 0f, screenW, screenH);
            DrawLine(a, b, lineColor);
        }
        for (int cx = cx0; cx <= cx1 + 1; cx++)
        {
            var a = _camera.WorldToScreen(new Vec2(cx * cs, cy0 * cs), 0f, screenW, screenH);
            var b = _camera.WorldToScreen(new Vec2(cx * cs, (cy1 + 1) * cs), 0f, screenW, screenH);
            DrawLine(a, b, lineColor);
        }
    }

    private void FillGrassCellQuad(int cx, int cy, float cs, int screenW, int screenH, Color fill)
    {
        // Cell in world space, projected to 4 screen points (parallelogram).
        var tl = _camera.WorldToScreen(new Vec2(cx * cs,       cy * cs      ), 0f, screenW, screenH);
        var tr = _camera.WorldToScreen(new Vec2((cx + 1) * cs, cy * cs      ), 0f, screenW, screenH);
        var br = _camera.WorldToScreen(new Vec2((cx + 1) * cs, (cy + 1) * cs), 0f, screenW, screenH);
        var bl = _camera.WorldToScreen(new Vec2(cx * cs,       (cy + 1) * cs), 0f, screenW, screenH);

        // SpriteBatch doesn't draw filled quads directly. For axis-aligned cells in
        // an iso camera, the projected quad is a parallelogram (tl-tr-br-bl). We
        // approximate the fill by scan-drawing horizontal 1-pixel strips between
        // the top and bottom edges. Cheap enough for ~hundreds of visible cells.
        int yTop = (int)MathF.Min(tl.Y, tr.Y);
        int yBot = (int)MathF.Max(bl.Y, br.Y);
        for (int y = yTop; y <= yBot; y++)
        {
            float t = yBot == yTop ? 0f : (y - yTop) / (float)(yBot - yTop);
            float xL = MathHelper.Lerp(tl.X, bl.X, t);
            float xR = MathHelper.Lerp(tr.X, br.X, t);
            int ix = (int)MathF.Min(xL, xR);
            int iw = (int)MathF.Abs(xR - xL);
            if (iw > 0)
                Scope.Draw(_pixel, new Rectangle(ix, y, iw, 1), fill);
        }
    }

    // ====================================================================
    //  OBJECTS TAB
    // ====================================================================

    /// <summary>
    /// Vertical height of a shared Auto-Ground controls block (as drawn by
    /// <see cref="DrawAutoGroundControls"/>). Single source of truth so any Draw
    /// layout and the matching Update hit-test stay in lockstep — the per-control
    /// increments in DrawAutoGroundControls must sum to this.
    /// </summary>
    private int AutoGroundControlsHeight(AutoGroundSettings s)
    {
        if (_eb == null) return 0;
        int h = LineHeight + 2; // "Auto Ground" checkbox row
        if (s.Enabled)
            h += (ButtonHeight + 2)  // Size stepper
               + (FieldHeight + 2)   // Ground dropdown
               + (FieldHeight + 2);  // Noise tiles
        h += 4;                      // trailing gap before the separator
        return h;
    }

    private void UpdateObjectsTab(MouseState mouse, KeyboardState kb, bool leftClick, bool leftDown, bool leftUp,
        bool rightClick, bool overPanel, int panelX, int panelY, int screenW, int screenH)
    {
        // Access rightDown/rightUp from mouse state. Gate rightDown on the popup
        // state like the Update-level leftClick/rightClick (an open dropdown/picker/
        // texture browser must make the world-removal paths inert too — otherwise a
        // right-drag deletes objects while an overlay is up). rightUp only ends a
        // gesture, so it stays ungated.
        bool rightDown = !IsAnyPopupBlocking() && mouse.RightButton == ButtonState.Pressed;
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

            // Def list (or group list for M17). Account for the Auto-Ground
            // controls block drawn between the mode toggle and the list.
            int listY = modeY + ButtonHeight + 4 + AutoGroundControlsHeight(_objectsAutoGround);
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
            else if (_objGridDrawY > 0 && _objGridCellW > 0)
            {
                // Grid cell hit-test against the layout cached by DrawObjectsTab
                // (same scheme as the Units tab).
                int colStride = _objGridCellW + ThumbGridGap;
                int rowStride = _objGridCellH + ThumbGridGap;
                int relX = mouse.X - _objGridDrawX;
                int relY = mouse.Y - _objGridDrawY;
                // Content-space Y: the grid scrolls continuously (by pixels),
                // so add the raw scroll before the row math.
                int conY = relY + (int)_envListScroll;
                if (relX >= 0 && relY >= 0 && relY < _objGridViewH
                    && relX % colStride < _objGridCellW && conY % rowStride < _objGridCellH)
                {
                    int col = relX / colStride;
                    int row = conY / rowStride;
                    int i = row * ThumbGridCols + col;
                    if (col < ThumbGridCols && i >= 0 && i < filteredDefs.Count)
                        SelectedEnvDefIndex = filteredDefs[i];
                }
            }
        }

        // Scroll env list
        int scrollDelta2 = mouse.ScrollWheelValue - _prevScrollValue;
        if (scrollDelta2 != 0 && overPanel && !IsAnyPopupBlocking())
            _envListScroll = MathF.Max(0, _envListScroll - scrollDelta2 * 0.2f);

        // M17: Resolve def index (may be a group selection using weighted random)
        int resolvedDefIndex = ResolveObjectDefIndex();

        // Place/paint on world (specific def or group mode; never the
        // no-selection sentinel)
        if (resolvedDefIndex >= 0 || IsEnvGroupSelected)
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
                            float placeScale = GetRandomPlacementScale(defToPlace);
                            // Suppress the OnCollisionsDirty callback during the add — it
                            // triggers Sim.RebuildPathfinder which is O(grid+objects) and
                            // not needed in the editor (AI isn't running). We stamp the
                            // new tree's collision incrementally below for O(radius²) cost
                            // instead of the full O(total_objects) rebake.
                            var prevHandler = _envSystem.OnCollisionsDirty;
                            _envSystem.OnCollisionsDirty = null;
                            int newIdx;
                            try
                            {
                                newIdx = _envSystem.AddObject((ushort)defToPlace, worldPos.X, worldPos.Y, placeScale, persistent: true);
                            }
                            finally
                            {
                                _envSystem.OnCollisionsDirty = prevHandler;
                            }
                            // Auto-ground: stamp a ground patch under the object.
                            // Bundle it with the object placement so one undo
                            // reverts both.
                            var groundOld = new Dictionary<long, byte>();
                            bool groundChanged = StampAutoGround(_objectsAutoGround, worldPos.X, worldPos.Y, groundOld);
                            if (groundChanged) _onVertexMapChanged?.Invoke();
                            UndoAction placeUndo = new UndoObjectBatchPlace { Env = _envSystem, ObjectIndices = new List<int> { newIdx } };
                            if (groundChanged)
                            {
                                var comp = new UndoComposite();
                                comp.Actions.Add(placeUndo);
                                comp.Actions.Add(new UndoGroundStroke
                                {
                                    Ground = _groundSystem,
                                    OldValues = groundOld,
                                    OnChanged = _onVertexMapChanged
                                });
                                PushUndo(comp);
                            }
                            else PushUndo(placeUndo);
                            AutoCreateTriggerInstance(newIdx); // RM06
                            // RM04 incremental: stamp just this object's collision into
                            // the tier cost fields. No need to rebuild everything else.
                            _envSystem.StampObjectCollisionAt(_tileGrid, newIdx);
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
                    if (_autoGroundStrokeOld == null)
                        _autoGroundStrokeOld = new();
                    PaintObjectsBatch(mouse, screenW, screenH);
                }
                if (leftUp)
                    FinalizeBatchPlaceStroke();
            }
        }

        // Right-click to remove (single mode: drag to wipe nearest; paint mode: batch brush)
        if (!_objectPaintMode)
        {
            // Single mode: hold right and drag to continuously remove the nearest
            // object (any category) under the cursor. Previously this was a single
            // right-click removing one object at a time. Removals accumulate into
            // one batch undo finalized on mouse-up.
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
                    // Suppress per-remove pathfinder rebuild (same reasoning as
                    // single placement). One rebake fires on mouse-up below; the
                    // MapEditor→exit transition also rebuilds for the session.
                    var prevHandler = _envSystem.OnCollisionsDirty;
                    _envSystem.OnCollisionsDirty = null;
                    try
                    {
                        AutoRemoveTriggerInstance(closest); // RM07
                        _envSystem.RemoveObject(closest);
                    }
                    finally
                    {
                        _envSystem.OnCollisionsDirty = prevHandler;
                    }
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
                    // No rebake here: nothing in the editor reads the cost field
                    // between edits, and the MapEditor→gameplay transition (Game1)
                    // rebuilds pathfinding once on exit. A full rebake here was a
                    // ~265 ms hitch at the end of every erase stroke on the big map.
                }
                _batchRemovedObjects = null;
            }
        }
        else
        {
            // Paint-mode right-click-drag: wipe every object of the currently
            // selected category within the brush radius. Matches the "eraser
            // brush" behaviour from the old C++ editor.
            if (rightDown && !overPanel)
            {
                if (_batchRemovedObjects == null)
                    _batchRemovedObjects = new();
                Vec2 worldPos = _camera.ScreenToWorld(new Vector2(mouse.X, mouse.Y), screenW, screenH);

                float radius = BrushRadius;
                float radSq = radius * radius;

                // --- PERF FIXES ---
                // (1) Precompute the per-def category filter once — IsInSelectedObjectType
                //     rebuilds the category list each call; doing that per-tree was the
                //     dominant cost for large brushes.
                int defCount = _envSystem.DefCount;
                Span<bool> defInCategory = defCount <= 256
                    ? stackalloc bool[defCount]
                    : new bool[defCount];
                for (int d = 0; d < defCount; d++) defInCategory[d] = IsInSelectedObjectType(d);

                // (2) Suppress the per-remove OnCollisionsDirty callback (which
                //     rebuilds the pathfinder per object). The MapEditor→gameplay
                //     exit transition (Game1) rebuilds once for the whole session.
                var prevHandler = _envSystem.OnCollisionsDirty;
                _envSystem.OnCollisionsDirty = null;

                var sw = System.Diagnostics.Stopwatch.StartNew();
                long tFilter = 0, tRemove = 0, tTrigger = 0;
                int iterated = 0, removed = 0;
                try
                {
                    // Iterate backwards because RemoveObject shifts indices (RemoveAt).
                    for (int i = _envSystem.ObjectCount - 1; i >= 0; i--)
                    {
                        iterated++;
                        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                        var obj = _envSystem.GetObject(i);
                        bool inCat = defInCategory[obj.DefIndex];
                        float dx = obj.X - worldPos.X;
                        float dy = obj.Y - worldPos.Y;
                        bool inBrush = dx * dx + dy * dy <= radSq;
                        tFilter += System.Diagnostics.Stopwatch.GetTimestamp() - t0;

                        if (!inCat || !inBrush) continue;

                        long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                        _batchRemovedObjects.Add((obj.DefIndex, obj.X, obj.Y, obj.Scale, obj.Seed));
                        AutoRemoveTriggerInstance(i);
                        tTrigger += System.Diagnostics.Stopwatch.GetTimestamp() - t1;

                        long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
                        _envSystem.RemoveObject(i);
                        tRemove += System.Diagnostics.Stopwatch.GetTimestamp() - t2;

                        removed++;
                    }
                }
                finally
                {
                    _envSystem.OnCollisionsDirty = prevHandler;
                }

                sw.Stop();
                if (removed > 0)
                {
                    double tickToMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    Core.DebugLog.Log("editor",
                        $"brush_delete: iter={iterated} rm={removed} " +
                        $"total={sw.ElapsedTicks * tickToMs:F2}ms " +
                        $"filter={tFilter * tickToMs:F2}ms " +
                        $"trigger={tTrigger * tickToMs:F2}ms " +
                        $"remove={tRemove * tickToMs:F2}ms");
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
                    // No rebake here — the MapEditor→gameplay exit transition
                    // rebuilds pathfinding once; nothing reads the cost field
                    // mid-edit. Avoids a ~265 ms hitch per erase stroke.
                }
                _batchRemovedObjects = null;
            }
        }
    }

    /// <summary>
    /// True if the given def index belongs to the currently selected category
    /// tab (e.g. "Trees", "Buildings"). Used by the eraser brush so right-click-
    /// drag wipes everything in that category within the brush radius — not just
    /// the specific def/tree type highlighted in the list.
    /// Special cases: "All" matches every def; "Groups" matches any def that's
    /// part of a group.
    /// </summary>
    private bool IsInSelectedObjectType(int defIdx)
    {
        if (defIdx < 0 || defIdx >= _envSystem.DefCount) return false;

        var categories = GetEnvCategories();
        if (SelectedEnvCategory < 0 || SelectedEnvCategory >= categories.Count) return false;

        string cat = categories[SelectedEnvCategory];
        if (cat == "All") return true;

        var def = _envSystem.GetDef(defIdx);
        if (cat == "Groups") return !string.IsNullOrEmpty(def.Group);
        return def.Category == cat;
    }

    /// <summary>
    /// M17: Resolve the currently selected def index, handling group mode with weighted random.
    /// Returns -1 if nothing valid is selected.
    /// </summary>
    private int ResolveObjectDefIndex()
    {
        if (SelectedEnvDefIndex == EnvNoSelection)
            return -1;
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
    /// PERF: suppresses OnCollisionsDirty inside the loop (fires a pathfinder rebuild
    /// per tree otherwise) and stamps each object's collision incrementally; the
    /// MapEditor→gameplay exit transition rebuilds pathfinding once for the session.
    /// Group painting: each candidate position independently weighted-random-picks a
    /// def from the selected group, so one stroke scatters a mix. Grid spacing uses
    /// the smallest collision radius in the group so smaller trees get their natural
    /// density; larger trees naturally sparsen via CanPlaceObject rejection.
    /// </summary>
    private void PaintObjectsBatch(MouseState mouse, int screenW, int screenH)
    {
        // Build the per-stroke candidate pool (either [SelectedDef, 1.0] for single-
        // def mode, or every group member with its GroupWeight).
        var members = GetSelectedGroupMembers();
        if (members.Count == 0) return;

        // Normalize weights and find the tightest spacing any member needs.
        float totalWeight = 0;
        float minRadius = float.MaxValue;
        foreach (var (idx, w) in members)
        {
            totalWeight += w;
            var d = _envSystem.GetDef(idx);
            float r = d.CollisionRadius > 0 ? d.CollisionRadius : d.PlacementScale;
            if (r < minRadius) minRadius = r;
        }
        if (minRadius == float.MaxValue) minRadius = 1f;
        float spacing = Math.Max(1f, minRadius * 2.2f);

        Vec2 worldPos = _camera.ScreenToWorld(new Vector2(mouse.X, mouse.Y), screenW, screenH);

        var prevHandler = _envSystem.OnCollisionsDirty;
        _envSystem.OnCollisionsDirty = null;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        long tCandidates = 0, tCanPlace = 0, tAdd = 0, tTrigger = 0;
        int candidates = 0, placed = 0;
        bool groundChanged = false;

        try
        {
            for (int dy = -BrushRadius; dy <= BrushRadius; dy++)
            {
                for (int dx = -BrushRadius; dx <= BrushRadius; dx++)
                {
                    if (dx * dx + dy * dy > BrushRadius * BrushRadius) continue;
                    candidates++;

                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    float ox = dx * spacing + (dy % 2 == 0 ? 0 : spacing * 0.5f);
                    float oy = dy * spacing * 0.866f;
                    float jitter = spacing * 0.25f;
                    ox += (Random.Shared.NextSingle() - 0.5f) * 2f * jitter;
                    oy += (Random.Shared.NextSingle() - 0.5f) * 2f * jitter;
                    float px = worldPos.X + ox;
                    float py = worldPos.Y + oy;

                    // Weighted random pick from the group members.
                    float roll = Random.Shared.NextSingle() * totalWeight;
                    float accum = 0;
                    int defToPlace = members[^1].defIdx;
                    foreach (var (idx, w) in members)
                    {
                        accum += w;
                        if (roll <= accum) { defToPlace = idx; break; }
                    }
                    tCandidates += System.Diagnostics.Stopwatch.GetTimestamp() - t0;

                    // Placement check uses the picked def's own collision+placement radius.
                    long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
                    bool canPlace = _envSystem.CanPlaceObject(defToPlace, px, py);
                    tCanPlace += System.Diagnostics.Stopwatch.GetTimestamp() - t2;
                    if (!canPlace) continue;

                    long t3 = System.Diagnostics.Stopwatch.GetTimestamp();
                    float paintScale = GetRandomPlacementScale(defToPlace);
                    int newIdx = _envSystem.AddObject((ushort)defToPlace, px, py, paintScale, persistent: true);
                    _batchPlacedObjects?.Add(((ushort)defToPlace, px, py, paintScale, 0f, newIdx));
                    // Incremental stamp: skip the per-stroke full rebake on
                    // mouse-up by keeping tier fields in sync as we go. O(r²)
                    // per object vs O(total_objects) on stroke end.
                    _envSystem.StampObjectCollisionAt(_tileGrid, newIdx);
                    tAdd += System.Diagnostics.Stopwatch.GetTimestamp() - t3;

                    long t4 = System.Diagnostics.Stopwatch.GetTimestamp();
                    AutoCreateTriggerInstance(newIdx); // RM06
                    tTrigger += System.Diagnostics.Stopwatch.GetTimestamp() - t4;

                    // Auto-ground patch under this object (accumulated for one
                    // stroke-wide undo; texture is flushed once after the loop).
                    if (_objectsAutoGround.Enabled && _autoGroundStrokeOld != null
                        && StampAutoGround(_objectsAutoGround, px, py, _autoGroundStrokeOld))
                        groundChanged = true;

                    placed++;
                }
            }
        }
        finally
        {
            _envSystem.OnCollisionsDirty = prevHandler;
        }

        if (groundChanged) _onVertexMapChanged?.Invoke();

        sw.Stop();
        if (placed > 0)
        {
            double tickToMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            Core.DebugLog.Log("editor",
                $"brush_place: cand={candidates} placed={placed} pool={members.Count} total={sw.ElapsedTicks * tickToMs:F2}ms " +
                $"setup={tCandidates * tickToMs:F2}ms " +
                $"canPlace={tCanPlace * tickToMs:F2}ms " +
                $"add={tAdd * tickToMs:F2}ms " +
                $"trigger={tTrigger * tickToMs:F2}ms " +
                $"objects={_envSystem.ObjectCount}");
        }
    }

    /// <summary>
    /// Returns the (defIndex, weight) pool the paint brush should sample from,
    /// based on the current Objects-tab selection.
    /// - Specific def selected → single entry with weight 1.
    /// - Group selected (SelectedEnvDefIndex is -(groupIndex+1)) → every def whose
    ///   Group matches the selected group, each with its GroupWeight.
    /// Called once per paint tick so we don't rescan DefCount per candidate.
    /// </summary>
    private List<(int defIdx, float weight)> GetSelectedGroupMembers()
    {
        var result = new List<(int, float)>();

        if (SelectedEnvDefIndex == EnvNoSelection)
            return result;

        if (SelectedEnvDefIndex >= 0)
        {
            result.Add((SelectedEnvDefIndex, 1f));
            return result;
        }

        int groupIdx = -(SelectedEnvDefIndex + 1);
        var groups = GetEnvGroups();
        if (groupIdx < 0 || groupIdx >= groups.Count) return result;
        string groupName = groups[groupIdx];

        for (int i = 0; i < _envSystem.DefCount; i++)
        {
            var d = _envSystem.GetDef(i);
            if (d.Group == groupName)
                result.Add((i, MathF.Max(0.001f, d.GroupWeight)));
        }
        return result;
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
                if (SelectedEnvDefIndex >= 0)
                    _envObjectEditor.Open(SelectedEnvDefIndex);
                else
                    _envObjectEditor.Open();
            }
            contentY += ButtonHeight + 4;
        }

        var mouse = _eb._input.Mouse;

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
            Scope.Draw(_pixel, btnRect, bg);
            DrawTextCentered(categories[i], btnRect, TextColor);
        }
        contentY += catRows * (ButtonHeight + 2) + 2;

        // Mode toggle: Single | Paint
        int halfW = (PanelWidth - Margin * 2) / 2;
        {
            var singleRect = new Rectangle(panelX + Margin, contentY, halfW - 1, ButtonHeight);
            var paintRect = new Rectangle(panelX + Margin + halfW, contentY, halfW - 1, ButtonHeight);
            Scope.Draw(_pixel, singleRect, !_objectPaintMode ? TabActiveColor : TabInactiveColor);
            Scope.Draw(_pixel, paintRect, _objectPaintMode ? TabActiveColor : TabInactiveColor);
            DrawTextCentered("Single", singleRect, TextColor);
            DrawTextCentered("Paint", paintRect, TextColor);
        }
        contentY += ButtonHeight + 4;

        // Auto-ground controls: stamp a ground patch under each placed object
        // (e.g. dirt under trees on grassland). Applies in single and paint modes.
        if (_eb != null)
            DrawAutoGroundControls(_objectsAutoGround, "obj", panelX, ref contentY);

        // Separator
        Scope.Draw(_pixel, new Rectangle(panelX, contentY - 2, PanelWidth, 1), SeparatorColor);

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
                    Scope.Draw(_pixel, btnRect, bgColor);

                // Count defs in group
                int defCount = 0;
                for (int d = 0; d < _envSystem.DefCount; d++)
                    if (_envSystem.GetDef(d).Group == groups[i]) defCount++;

                DrawSmallText($"{groups[i]} ({defCount} defs)", panelX + Margin + 4, itemY + 3, selected ? TextBright : TextColor);
            }

            // Clamp + thin scrollbar for the group list (the def grid clamps
            // its own scroll; groups reuse _envListScroll as a plain row list).
            int groupsContentH = groups.Count * (ButtonHeight + 2);
            _envListScroll = MathF.Min(_envListScroll, Math.Max(0, groupsContentH - listAreaH));
            _eb.DrawVScrollbar(panelX + PanelWidth - 6, contentY, listAreaH, groupsContentH, _envListScroll);

            // Selected group info
            if (IsEnvGroupSelected)
            {
                int groupIdx = -(SelectedEnvDefIndex + 1);
                if (groupIdx >= 0 && groupIdx < groups.Count)
                {
                    int propY = contentTop + contentH - 140;
                    Scope.Draw(_pixel, new Rectangle(panelX, propY - 4, PanelWidth, 1), SeparatorColor);
                    propY += 2;
                    DrawSmallText($"Group: {groups[groupIdx]}", panelX + Margin, propY, TextBright); propY += LineHeight;
                    DrawSmallText("Uses weighted random selection", panelX + Margin, propY, TextColor); propY += LineHeight;
                    DrawSmallText(_objectPaintMode ? "Left-drag to paint (random)" : "Click to place (random)", panelX + Margin, propY, TextDim);
                }
            }
        }
        else
        {
            // Normal def grid: 6-wide sprite thumbnails (hover a cell for the
            // name), same scheme as the Units tab.
            var filteredDefs = GetFilteredEnvDefs(categories);

            int gridX = panelX + Margin;
            int gridW = PanelWidth - Margin * 2;
            int cellW = (gridW - (ThumbGridCols - 1) * ThumbGridGap) / ThumbGridCols;
            int cellH = cellW;
            int rowStride = cellH + ThumbGridGap;
            int totalRows = (filteredDefs.Count + ThumbGridCols - 1) / ThumbGridCols;

            // Clamp the wheel-driven scroll to the end of the grid (the wheel
            // handler in Update only clamps at 0). Pixel-exact so the grid
            // scrolls continuously with the wheel, not a row at a time.
            float maxObjScroll = Math.Max(0, totalRows * rowStride - listAreaH);
            _envListScroll = Math.Clamp(_envListScroll, 0, maxObjScroll);
            int scrollPx = (int)_envListScroll;
            int firstRow = scrollPx / rowStride;
            int subRowOff = scrollPx % rowStride; // partial-row offset — rows glide, clip catches the spill

            // Cache for Update hit-testing
            _objGridDrawX = gridX;
            _objGridDrawY = contentY;
            _objGridCellW = cellW;
            _objGridCellH = cellH;
            _objGridViewH = listAreaH;

            // MousePos, not raw .Mouse: the mousepos dev override patches only
            // MousePos, and with a real mouse the two are identical.
            int mx = (int)_eb._input.MousePos.X, my = (int)_eb._input.MousePos.Y;
            int hoveredIdx = -1;
            Rectangle hoveredCell = default;
            // Nested clip: partially-scrolled rows must not bleed above the
            // grid or into the selected-def properties below it.
            _eb.BeginClip(new Rectangle(gridX, contentY, gridW, listAreaH));
            for (int r = 0; ; r++)
            {
                int row = firstRow + r;
                if (row >= totalRows) break;
                int cellY = contentY + r * rowStride - subRowOff;
                if (cellY >= contentY + listAreaH) break;
                for (int c = 0; c < ThumbGridCols; c++)
                {
                    int i = row * ThumbGridCols + c;
                    if (i >= filteredDefs.Count) break;

                    int defIdx = filteredDefs[i];
                    var def = _envSystem.GetDef(defIdx);
                    var cell = new Rectangle(gridX + c * (cellW + ThumbGridGap), cellY, cellW, cellH);
                    bool selected = defIdx == SelectedEnvDefIndex;
                    bool hovered = cell.Contains(mx, my) && my >= contentY && my < contentY + listAreaH;
                    if (hovered) { hoveredIdx = i; hoveredCell = cell; }

                    Scope.Draw(_pixel, cell, selected ? new Color(60, 60, 100, 220)
                        : hovered ? new Color(40, 40, 70, 180) : new Color(25, 25, 35, 160));
                    DrawEnvDefThumb(defIdx, def, cell);
                    if (selected)
                        _eb.DrawBorder(cell, new Color(255, 230, 160));
                    else if (hovered)
                        _eb.DrawBorder(cell, new Color(120, 120, 170));
                }
            }
            _eb.EndClip();

            // Thin scrollbar in the panel's right margin, spanning the grid viewport.
            _eb.DrawVScrollbar(panelX + PanelWidth - 6, contentY, listAreaH,
                totalRows * rowStride, _envListScroll);

            // Hover tooltip — queued globally, drawn topmost after the clip closes.
            if (hoveredIdx >= 0 && hoveredIdx < filteredDefs.Count)
            {
                var hovDef = _envSystem.GetDef(filteredDefs[hoveredIdx]);
                DrawGridCellTooltip($"[{hovDef.Category}] {hovDef.Name}", hoveredCell);
            }

            // Selected def properties
            if (SelectedEnvDefIndex >= 0 && SelectedEnvDefIndex < _envSystem.DefCount)
            {
                int propY = contentTop + contentH - 140;
                Scope.Draw(_pixel, new Rectangle(panelX, propY - 4, PanelWidth, 1), SeparatorColor);
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

        // (Brush cursor + collision overlay drawn by DrawWorldOverlaysForActiveTab,
        // outside the clip.)
    }

    /// <summary>Whether the "Show Collisions" checkbox is active on the Objects tab.</summary>
    public bool ShowObjectCollisions => _showCollisions;

    /// <summary>
    /// RM08: Draw isometric ellipses for each placed object's collision radius.
    /// Y is compressed by camera YRatio. When Alt is held, show object names.
    /// </summary>
    private void DrawCollisionOverlay(int screenW, int screenH)
    {
        var kb = _eb._input.Kb;
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

        // Paint walls (left-click uses selected type; RM11 right-click erases = type 0)
        if (leftDown && !overPanel)
        {
            if (!_painting)
                _wallStrokeOld = new Dictionary<int, (byte type, short hp)>();
            _painting = true;
            PaintWalls(mouse, screenW, screenH, SelectedWallType);
        }
        if (leftUp)
            FinalizeWallStroke();

        // RM11: Right-click to erase walls regardless of selected type
        if (rightDown && !overPanel)
        {
            if (!_painting)
                _wallStrokeOld = new Dictionary<int, (byte type, short hp)>();
            _painting = true;
            PaintWalls(mouse, screenW, screenH, 0);
        }
        if (rightUp)
            FinalizeWallStroke();
    }

    /// <summary>Paint (or erase) walls under the brush. <paramref name="wallType"/> == 0
    /// clears; otherwise stamps that type. Records pre-stroke values into
    /// _wallStrokeOld for undo.</summary>
    private void PaintWalls(MouseState mouse, int screenW, int screenH, int wallType)
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

                if (wallType == 0)
                    _wallSystem.ClearWall(tx, ty);
                else
                    _wallSystem.SetWall(tx, ty, (byte)wallType);
            }
        }
    }

    /// <summary>End of a wall paint/erase stroke: push the accumulated undo, rebuild the
    /// cost field, re-bake walls. Shared by the left (paint) and right (erase) mouse-up.</summary>
    private void FinalizeWallStroke()
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

    private void DrawWallsTab(int panelX, int contentY, int contentH, int screenW, int screenH)
    {
        int viewBottom = contentY + contentH; // tab clip bottom, before the header advances contentY
        DrawSectionHeader(panelX, ref contentY, $"Walls ({_wallSystem.DefCount} types)");

        var mouse = _eb._input.Mouse;
        float scroll = _tabScroll[3];
        int y = contentY - (int)scroll;

        // Erase entry
        {
            bool selected = SelectedWallType == 0;
            var btnRect = new Rectangle(panelX + Margin, y, PanelWidth - Margin * 2, ButtonHeight);
            var bg = selected ? HighlightColor : (IsInRect(mouse, btnRect) ? ButtonHoverColor : Color.Transparent);
            if (bg != Color.Transparent) Scope.Draw(_pixel, btnRect, bg);
            Scope.Draw(_pixel, new Rectangle(panelX + Margin + 4, y + 4, 14, 14), DangerColor);
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
            if (bg != Color.Transparent) Scope.Draw(_pixel, btnRect, bg);

            // Color swatch
            Scope.Draw(_pixel, new Rectangle(panelX + Margin + 4, y + 4, 14, 14), def.Color);
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
        // (Brush cursor drawn by DrawWorldOverlaysForActiveTab, outside the clip.)

        DrawTabScrollbar(panelX, contentY, viewBottom, y + LineHeight, ref _tabScroll[3]);
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
                road.Points.Add(new RoadControlPoint { Position = worldPos, Width = _roadNewPointWidth });
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

            // Drag junctions — regardless of road selection, as long as the
            // click didn't grab a control point. (Requiring SelectedRoadIndex
            // < 0 made junctions permanently un-draggable once a road had ever
            // been selected, since nothing resets the road selection to -1.)
            if (leftClick && !_roadPlaceMode && _draggingPoint < 0)
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
        int viewBottom = contentY + contentH; // tab clip bottom, before the header advances contentY
        DrawSectionHeader(panelX, ref contentY, $"Roads ({_roadSystem.RoadCount} roads, {_roadSystem.JunctionCount} junctions)");

        var mouse = _eb._input.Mouse;
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
            if (bg != Color.Transparent) Scope.Draw(_pixel, btnRect, bg);

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
            Scope.Draw(_pixel, new Rectangle(panelX, y, PanelWidth, 1), SeparatorColor);
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
                    _textureBrowser.Open(GamePaths.Resolve("assets/Environment/Roads"), roadTexPath, path =>
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

                // New Point Width — persisted and actually used when placing
                // points (the field previously passed a constant and discarded
                // the return; new points were hardcoded to width 2).
                float newPtW = _eb.DrawFloatField("road_newPtW", "New Pt Width", _roadNewPointWidth, panelX + Margin, y, fw, 0.1f);
                _roadNewPointWidth = MathF.Max(0.1f, newPtW);
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
        Scope.Draw(_pixel, new Rectangle(panelX, y, PanelWidth, 1), SeparatorColor);
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
        Scope.Draw(_pixel, new Rectangle(panelX, y, PanelWidth, 1), SeparatorColor);
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
            Scope.Draw(_pixel, new Rectangle(panelX, y, PanelWidth, 1), SeparatorColor);
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

        // (Road control points/junction overlays drawn by
        // DrawWorldOverlaysForActiveTab, outside the clip.)

        DrawTabScrollbar(panelX, contentY, viewBottom, y + LineHeight, ref _tabScroll[4]);
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
                Scope.Draw(_pixel, new Rectangle((int)sp.X - sz / 2, (int)sp.Y - sz / 2, sz, sz), RoadPointColor);

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
            Scope.Draw(_pixel, new Rectangle((int)sp.X - sz / 2, (int)sp.Y - sz / 2, sz, sz), JunctionColor);
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

        // World interaction: drag regions (M26: 10 handle types + viewport click-to-select)
        if (!overPanel)
        {
            Vec2 worldPos = _camera.ScreenToWorld(new Vector2(mouse.X, mouse.Y), screenW, screenH);

            // Scale handle tolerance with zoom so handles stay clickable at any zoom level
            // 8 screen pixels converted to world-space
            float handleTol = 8f / _camera.Zoom;

            if (leftClick)
            {
                _activeHandle = RegionHandle.None;
                _draggingRegion = -1;

                // First: try handles on the currently-selected region (highest priority)
                if (SelectedRegionIndex >= 0 && SelectedRegionIndex < _triggerSystem.Regions.Count)
                {
                    var region = _triggerSystem.Regions[SelectedRegionIndex];
                    _activeHandle = HitTestRegionHandles(region, worldPos, handleTol);
                    if (_activeHandle != RegionHandle.None)
                    {
                        _draggingRegion = SelectedRegionIndex;
                        _draggingRegionResize = _activeHandle != RegionHandle.Body;
                        if (_activeHandle == RegionHandle.Body)
                            _regionDragOffset = new Vec2(region.X - worldPos.X, region.Y - worldPos.Y);
                    }
                }

                // Second: if no handle hit on selected, try clicking any region body to select it
                if (_activeHandle == RegionHandle.None)
                {
                    var regions = _triggerSystem.Regions;
                    for (int i = 0; i < regions.Count; i++)
                    {
                        if (i == SelectedRegionIndex) continue; // already checked above
                        var r = regions[i];
                        if (r.ContainsPoint(worldPos))
                        {
                            SelectedRegionIndex = i;
                            _activeHandle = RegionHandle.Body;
                            _draggingRegion = i;
                            _draggingRegionResize = false;
                            _regionDragOffset = new Vec2(r.X - worldPos.X, r.Y - worldPos.Y);
                            break;
                        }
                    }
                }
            }

            if (leftDown && _draggingRegion >= 0 && _draggingRegion < _triggerSystem.Regions.Count)
            {
                var region = _triggerSystem.RegionsMut[_draggingRegion];

                switch (_activeHandle)
                {
                    case RegionHandle.Body:
                        region.X = worldPos.X + _regionDragOffset.X;
                        region.Y = worldPos.Y + _regionDragOffset.Y;
                        break;

                    case RegionHandle.CircleRadius:
                        region.Radius = MathF.Max(1f, (worldPos - new Vec2(region.X, region.Y)).Length());
                        break;

                    default: // Edge/corner resize — rect math shared with the Zones tab
                    {
                        float x = region.X, y = region.Y, hw = region.HalfW, hh = region.HalfH;
                        ApplyRectHandleDrag(_activeHandle, worldPos, ref x, ref y, ref hw, ref hh);
                        region.X = x; region.Y = y; region.HalfW = hw; region.HalfH = hh;
                        break;
                    }
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
        int viewBottom = contentY + contentH; // tab clip bottom, before the header advances contentY
        DrawSectionHeader(panelX, ref contentY, $"Regions ({_triggerSystem.Regions.Count})");

        var mouse = _eb._input.Mouse;
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
            if (bg != Color.Transparent) Scope.Draw(_pixel, btnRect, bg);

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
            Scope.Draw(_pixel, new Rectangle(panelX, y, PanelWidth, 1), SeparatorColor);
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
        Scope.Draw(_pixel, new Rectangle(panelX, y, PanelWidth, 1), SeparatorColor);
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
                Scope.Draw(_pixel, prBtnRect, prBg);
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

        // (Region overlays drawn by DrawWorldOverlaysForActiveTab, outside the clip.)

        DrawTabScrollbar(panelX, contentY, viewBottom, y + ButtonHeight + 4, ref _tabScroll[5]);
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
                    Scope.Draw(_pixel, new Rectangle(rx, ry, rw, rh), fill);
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
                Scope.Draw(_pixel, new Rectangle((int)sp.X - sz / 2, (int)sp.Y - sz / 2, sz, sz), WaypointColor);

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
        Scope.Draw(_pixel, new Rectangle(cx - halfSz, cy - halfSz, halfSz * 2, halfSz * 2), color);
        // Border
        DrawRectBorder(cx - halfSz, cy - halfSz, halfSz * 2, halfSz * 2, new Color(0, 0, 0, 180));
    }

    // ====================================================================
    //  ZONES TAB
    // ====================================================================

    /// <summary>World interaction for the Zones tab: rubber-band creation while "+ Draw
    /// Zone" is armed, otherwise click-to-select + region-style body/handle dragging.
    /// Panel widgets (right panel and left village panel) handle their own clicks in
    /// Draw via EditorBase, so this only owns the world.</summary>
    private void UpdateZonesTab(MouseState mouse, bool leftClick, bool leftDown, bool leftUp,
        bool overPanel, int screenW, int screenH)
    {
        var leftPanel = ZoneLeftPanelRect(screenH);
        bool overLeftPanel = leftPanel.HasValue && IsInRect(mouse, leftPanel.Value);

        if (!overPanel && !overLeftPanel && !IsAnyPopupBlocking())
        {
            Vec2 worldPos = _camera.ScreenToWorld(new Vector2(mouse.X, mouse.Y), screenW, screenH);
            float handleTol = 8f / _camera.Zoom;

            if (_zoneDrawMode)
            {
                // Rubber-band creation: press anchors a corner, release creates the zone.
                if (leftClick)
                {
                    _zoneRubberBanding = true;
                    _zoneDragStartWorld = worldPos;
                }
                if (_zoneRubberBanding && leftUp)
                {
                    _zoneRubberBanding = false;
                    float x0 = MathF.Min(_zoneDragStartWorld.X, worldPos.X);
                    float x1 = MathF.Max(_zoneDragStartWorld.X, worldPos.X);
                    float y0 = MathF.Min(_zoneDragStartWorld.Y, worldPos.Y);
                    float y1 = MathF.Max(_zoneDragStartWorld.Y, worldPos.Y);
                    if (x1 - x0 >= 2f && y1 - y0 >= 2f)
                    {
                        var kind = (ZoneKind)_zoneCreateKindIdx;
                        SelectedZoneIndex = _zoneSystem.Add(new MapZone
                        {
                            Id = NextZoneId(),
                            Name = $"{kind} {_zoneSystem.Count + 1}",
                            Kind = kind,
                            X = (x0 + x1) / 2f,
                            Y = (y0 + y1) / 2f,
                            HalfW = (x1 - x0) / 2f,
                            HalfH = (y1 - y0) / 2f,
                        });
                        _zoneDrawMode = false;
                    }
                    // Too small = accidental click; stay armed for another try.
                }
            }
            else
            {
                if (leftClick)
                {
                    _zoneHandle = RegionHandle.None;
                    _draggingZone = -1;
                    var zones = _zoneSystem.Zones;

                    // First: handles on the currently-selected zone (highest priority)
                    if (SelectedZoneIndex >= 0 && SelectedZoneIndex < zones.Count)
                    {
                        var z = zones[SelectedZoneIndex];
                        _zoneHandle = HitTestRectHandles(z.X, z.Y, z.HalfW, z.HalfH, worldPos, handleTol);
                        if (_zoneHandle != RegionHandle.None)
                        {
                            _draggingZone = SelectedZoneIndex;
                            if (_zoneHandle == RegionHandle.Body)
                                _zoneDragOffset = new Vec2(z.X - worldPos.X, z.Y - worldPos.Y);
                        }
                    }

                    // Second: click any other zone body to select + start a body drag
                    if (_zoneHandle == RegionHandle.None)
                    {
                        for (int i = 0; i < zones.Count; i++)
                        {
                            if (i == SelectedZoneIndex) continue;
                            if (!zones[i].ContainsPoint(worldPos)) continue;
                            SelectedZoneIndex = i;
                            _zoneHandle = RegionHandle.Body;
                            _draggingZone = i;
                            _zoneDragOffset = new Vec2(zones[i].X - worldPos.X, zones[i].Y - worldPos.Y);
                            break;
                        }
                    }

                    // Drag started: snapshot the rect as this press's undo state.
                    if (_draggingZone >= 0)
                    {
                        var dz = zones[_draggingZone];
                        _zonePendingUndo = new UndoZoneEdit
                        {
                            Owner = this, ZoneId = dz.Id,
                            X = dz.X, Y = dz.Y, HalfW = dz.HalfW, HalfH = dz.HalfH,
                        };
                    }
                }

                if (leftDown && _draggingZone >= 0 && _draggingZone < _zoneSystem.Count)
                {
                    var z = _zoneSystem.ZonesMut[_draggingZone];
                    if (_zoneHandle == RegionHandle.Body)
                    {
                        z.X = worldPos.X + _zoneDragOffset.X;
                        z.Y = worldPos.Y + _zoneDragOffset.Y;
                    }
                    else
                    {
                        float x = z.X, y = z.Y, hw = z.HalfW, hh = z.HalfH;
                        ApplyRectHandleDrag(_zoneHandle, worldPos, ref x, ref y, ref hw, ref hh);
                        z.X = x; z.Y = y; z.HalfW = hw; z.HalfH = hh;
                    }

                    // First frame the drag actually moves something: commit the snapshot,
                    // so a plain click-select never costs an undo step.
                    if (_zonePendingUndo != null
                        && (z.X != _zonePendingUndo.X || z.Y != _zonePendingUndo.Y
                            || z.HalfW != _zonePendingUndo.HalfW || z.HalfH != _zonePendingUndo.HalfH))
                    {
                        PushUndo(_zonePendingUndo);
                        _zonePendingUndo = null;
                    }
                }
            }
        }

        // Always clear drag state on release, even when the cursor ends over a panel
        // (a rubber-band released there is a cancel).
        if (leftUp)
        {
            _draggingZone = -1;
            _zoneHandle = RegionHandle.None;
            _zoneRubberBanding = false;
            _zonePendingUndo = null;
        }
    }

    private void DrawZonesTab(int panelX, int contentY, int contentH)
    {
        DrawSectionHeader(panelX, ref contentY, $"Zones ({_zoneSystem.Count})");
        if (_eb == null) return;

        int y = contentY;
        int fw = PanelWidth - Margin * 2;

        // New-zone controls
        string newKind = _eb.DrawCombo("zone_new_kind", "Zone Kind", CachedZoneKindNames[_zoneCreateKindIdx],
            CachedZoneKindNames, panelX + Margin, y, fw);
        int nk = Array.IndexOf(CachedZoneKindNames, newKind);
        if (nk >= 0) _zoneCreateKindIdx = nk;
        y += FieldHeight + 4;

        if (_eb.DrawButton(_zoneDrawMode ? "[Drag out the zone in the world...]" : "+ Draw Zone",
            panelX + Margin, y, fw, ButtonHeight, _zoneDrawMode ? AccentColor : (Color?)null))
            _zoneDrawMode = !_zoneDrawMode;
        y += ButtonHeight + 8;

        Scope.Draw(_pixel, new Rectangle(panelX, y, PanelWidth, 1), SeparatorColor);
        y += 4;

        // Zone list — rows are invisible buttons (EditorBase edge-detects the click)
        // with a kind-colored chip + left-aligned label drawn on top.
        var zones = _zoneSystem.Zones;
        for (int i = 0; i < zones.Count; i++)
        {
            if (y > contentY + contentH - 260)
            {
                DrawSmallText($"... {zones.Count - i} more", panelX + Margin, y, TextDim);
                y += LineHeight;
                break;
            }
            bool selected = i == SelectedZoneIndex;
            if (_eb.DrawButton("", panelX + Margin, y, fw, ButtonHeight,
                selected ? HighlightColor : Color.Transparent))
                SelectedZoneIndex = i;
            Scope.Draw(_pixel, new Rectangle(panelX + Margin + 3, y + 4, 12, ButtonHeight - 8),
                ZoneColors.Base(zones[i].Kind));
            DrawSmallText($"{zones[i].Name} ({zones[i].Kind})", panelX + Margin + 20, y + 3,
                selected ? TextBright : TextColor);
            y += ButtonHeight + 2;
        }
        y += 4;

        // Selected zone properties. Field ids embed the index so switching selection
        // changes the id and drops text-field focus (avoids input bleeding between zones).
        if (SelectedZoneIndex >= 0 && SelectedZoneIndex < _zoneSystem.Count)
        {
            if (_eb.DrawButton("Delete Zone", panelX + Margin, y, 110, ButtonHeight, DangerColor))
            {
                DeleteZone(SelectedZoneIndex);
                return; // list changed — re-layout next frame
            }
            y += ButtonHeight + 8;

            Scope.Draw(_pixel, new Rectangle(panelX, y, PanelWidth, 1), SeparatorColor);
            y += 4;
            var z = _zoneSystem.ZonesMut[SelectedZoneIndex];
            int idx = SelectedZoneIndex;

            z.Name = _eb.DrawTextField($"zone_name_{idx}", "Name", z.Name, panelX + Margin, y, fw);
            y += FieldHeight + 2;

            string kindStr = _eb.DrawCombo($"zone_kind_{idx}", "Kind", z.Kind.ToString(),
                CachedZoneKindNames, panelX + Margin, y, fw);
            if (Enum.TryParse<ZoneKind>(kindStr, out var parsedKind)) z.Kind = parsedKind;
            y += FieldHeight + 2;

            z.X = _eb.DrawFloatField($"zone_x_{idx}", "Center X", z.X, panelX + Margin, y, fw, 1f);
            y += FieldHeight + 2;
            z.Y = _eb.DrawFloatField($"zone_y_{idx}", "Center Y", z.Y, panelX + Margin, y, fw, 1f);
            y += FieldHeight + 2;
            z.HalfW = MathF.Max(1f, _eb.DrawFloatField($"zone_hw_{idx}", "Half W", z.HalfW, panelX + Margin, y, fw, 1f));
            y += FieldHeight + 2;
            z.HalfH = MathF.Max(1f, _eb.DrawFloatField($"zone_hh_{idx}", "Half H", z.HalfH, panelX + Margin, y, fw, 1f));
            y += FieldHeight + 4;

            DrawSmallText(z.Kind == ZoneKind.Village
                ? "Village config: see left panel"
                : "Spawn config: see left panel", panelX + Margin, y, TextDim);
        }
        else
        {
            DrawSmallText("Pick a kind, click + Draw Zone,", panelX + Margin, y, TextDim);
            y += LineHeight;
            DrawSmallText("then drag a rectangle in the world.", panelX + Margin, y, TextDim);
        }
    }

    /// <summary>Rect of the left-side zone config panel, or null when it isn't showing
    /// (Zones tab with a zone selected only). Single source for Draw,
    /// UpdateZonesTab world-input gating and IsMouseOverPanel.</summary>
    private Rectangle? ZoneLeftPanelRect(int screenH)
    {
        if (ActiveTab != MapEditorTab.Zones) return null;
        if (SelectedZoneIndex < 0 || SelectedZoneIndex >= _zoneSystem.Count) return null;
        return new Rectangle(10, 10, 300, Math.Min(screenH - 20, 560));
    }

    /// <summary>The zone config panel on the LEFT side of the screen. Village zones get
    /// name + population + a live contents summary; the other kinds get the periodic
    /// spawn table (def / per-minute / max-alive rows). Drawn post-clip (the right-panel
    /// scissor would hide it entirely).</summary>
    private void DrawZoneLeftPanel(int screenW, int screenH)
    {
        var rectOpt = ZoneLeftPanelRect(screenH);
        if (rectOpt == null || _eb == null) return;
        var rect = rectOpt.Value;
        var z = _zoneSystem.ZonesMut[SelectedZoneIndex];
        int idx = SelectedZoneIndex;

        Scope.Draw(_pixel, rect, BgColor);
        DrawRectBorder(rect.X, rect.Y, rect.Width, rect.Height, SeparatorColor);
        Scope.Draw(_pixel, new Rectangle(rect.X, rect.Y, rect.Width, HeaderHeight), HeaderBg);
        DrawSmallText($"{z.Kind}: {z.Name}", rect.X + Margin, rect.Y + 6, TextBright);

        if (z.Kind != ZoneKind.Village)
        {
            DrawZoneSpawnPanel(rect, z, idx);
            return;
        }

        int x = rect.X + Margin;
        int fw = rect.Width - Margin * 2;
        int y = rect.Y + HeaderHeight + 6;

        z.Name = _eb.DrawTextField($"zone_vname_{idx}", "Name", z.Name, x, y, fw);
        y += FieldHeight + 6;

        DrawSmallText("Population (spawned at load)", x, y, AccentColor);
        y += LineHeight;
        z.Population.Peasant = Math.Max(0, _eb.DrawIntField($"zone_pop_peasant_{idx}", "Peasants", z.Population.Peasant, x, y, fw));
        y += FieldHeight + 2;
        z.Population.Hunter = Math.Max(0, _eb.DrawIntField($"zone_pop_hunter_{idx}", "Hunters", z.Population.Hunter, x, y, fw));
        y += FieldHeight + 2;
        z.Population.Militia = Math.Max(0, _eb.DrawIntField($"zone_pop_militia_{idx}", "Militia", z.Population.Militia, x, y, fw));
        y += FieldHeight + 2;
        z.Population.Watchdog = Math.Max(0, _eb.DrawIntField($"zone_pop_watchdog_{idx}", "Watchdogs", z.Population.Watchdog, x, y, fw));
        y += FieldHeight + 6;

        Scope.Draw(_pixel, new Rectangle(rect.X, y, rect.Width, 1), SeparatorColor);
        y += 4;

        // Live contents summary — what's authored inside the rect right now. Shows
        // load-time contents (placed objects/units), which is what's being edited.
        DrawSmallText("Inside the zone:", x, y, AccentColor);
        y += LineHeight;

        _zoneContentCounts.Clear();
        for (int oi = 0; oi < _envSystem.ObjectCount; oi++)
        {
            var obj = _envSystem.GetObject(oi);
            var def = _envSystem.GetDef(obj.DefIndex);
            if (!def.IsBuilding) continue;
            if (!z.ContainsPoint(new Vec2(obj.X, obj.Y))) continue;
            string key = string.IsNullOrEmpty(def.Name) ? def.Id : def.Name;
            _zoneContentCounts.TryGetValue(key, out int c);
            _zoneContentCounts[key] = c + 1;
        }
        y = DrawZoneContentCounts("Buildings", rect, x, y);

        _zoneContentCounts.Clear();
        foreach (var pu in _placedUnits)
        {
            if (!z.ContainsPoint(new Vec2(pu.X, pu.Y))) continue;
            string key = pu.IsCorpse ? $"{pu.UnitDefId} (corpse)" : pu.UnitDefId;
            _zoneContentCounts.TryGetValue(key, out int c);
            _zoneContentCounts[key] = c + 1;
        }
        y = DrawZoneContentCounts("Units", rect, x, y);
    }

    /// <summary>Render the accumulated _zoneContentCounts as an indented "Nx name" list,
    /// truncating at the panel bottom. Returns the new layout y.</summary>
    private int DrawZoneContentCounts(string label, Rectangle panelRect, int x, int y)
    {
        int total = 0;
        foreach (var kv in _zoneContentCounts) total += kv.Value;
        DrawSmallText($"{label} ({total}):", x, y, TextColor);
        y += LineHeight;
        foreach (var kv in _zoneContentCounts)
        {
            if (y > panelRect.Bottom - LineHeight * 2)
            {
                DrawSmallText("  ...", x, y, TextDim);
                return y + LineHeight;
            }
            DrawSmallText($"  {kv.Value}x {kv.Key}", x, y, TextDim);
            y += LineHeight;
        }
        return y + 2;
    }

    // Combo option caches for the zone spawn panel (unit ids are static per session,
    // foragable env def ids follow the per-map def list).
    private string[]? _zoneWolfIdOptions, _zoneDeerIdOptions, _zoneAnimalIdOptions;
    private string[]? _zoneForagableIdOptions;
    private int _zoneForagableDefCount = -1;

    /// <summary>Unit dropdown per zone kind: WolfPack → wolves only, DeerHerd → deer only,
    /// AnimalPack → every other Animal-faction def. Wolf/deer membership = the pack
    /// archetype where set, else the def id (DireWolf/JuvWolf carry no archetype).</summary>
    private string[] GetZoneUnitIdOptions(ZoneKind kind)
    {
        if (_gameData == null) return Array.Empty<string>();
        if (_zoneWolfIdOptions == null)
        {
            List<string> wolves = new(), deer = new(), rest = new();
            foreach (var id in _gameData.Units.GetIDs())
            {
                var def = _gameData.Units.Get(id);
                if (def == null || def.Faction != "Animal") continue;
                if (def.Archetype == "WolfPack" || id.Contains("wolf", StringComparison.OrdinalIgnoreCase))
                    wolves.Add(id);
                else if (def.Archetype == "DeerHerd" || id.Contains("deer", StringComparison.OrdinalIgnoreCase))
                    deer.Add(id);
                else
                    rest.Add(id);
            }
            _zoneWolfIdOptions = wolves.ToArray();
            _zoneDeerIdOptions = deer.ToArray();
            _zoneAnimalIdOptions = rest.ToArray();
        }
        return kind switch
        {
            ZoneKind.WolfPack => _zoneWolfIdOptions,
            ZoneKind.DeerHerd => _zoneDeerIdOptions!,
            _ => _zoneAnimalIdOptions!,
        };
    }

    private string[] GetZoneForagableIdOptions()
    {
        if (_zoneForagableIdOptions == null || _zoneForagableDefCount != _envSystem.DefCount)
        {
            var list = new List<string>();
            for (int i = 0; i < _envSystem.DefCount; i++)
            {
                var d = _envSystem.GetDef(i);
                if (d.IsForagable) list.Add(d.Id);
            }
            _zoneForagableIdOptions = list.ToArray();
            _zoneForagableDefCount = _envSystem.DefCount;
        }
        return _zoneForagableIdOptions;
    }

    /// <summary>How many of the entry's def the zone currently holds — live env objects
    /// for Foraging zones (shared with the running game), editor-placed units for the
    /// animal kinds (runtime spawns aren't map data).</summary>
    private int CountZoneSpawnTargets(MapZone z, ZoneSpawnEntry e, bool forage)
    {
        if (forage)
        {
            int defIdx = _envSystem.FindDef(e.DefId);
            if (defIdx < 0) return 0;
            return _envSystem.CountActiveOfDefInRect(defIdx,
                z.X - z.HalfW, z.Y - z.HalfH, z.X + z.HalfW, z.Y + z.HalfH);
        }
        int n = 0;
        foreach (var pu in _placedUnits)
            if (!pu.IsCorpse && pu.UnitDefId == e.DefId && z.ContainsPoint(new Vec2(pu.X, pu.Y)))
                n++;
        return n;
    }

    /// <summary>Body of the left panel for WolfPack/DeerHerd/Foraging zones: the periodic
    /// spawn table. Each row = def combo + spawns-per-minute + max-alive + remove. Field
    /// ids embed zone AND row index so selection/row changes drop text-field focus.</summary>
    private void DrawZoneSpawnPanel(Rectangle rect, MapZone z, int idx)
    {
        if (_eb == null) return;
        int x = rect.X + Margin;
        int fw = rect.Width - Margin * 2;
        int y = rect.Y + HeaderHeight + 6;

        z.Name = _eb.DrawTextField($"zone_vname_{idx}", "Name", z.Name, x, y, fw);
        y += FieldHeight + 6;

        bool forage = z.Kind == ZoneKind.Foraging;
        string[] options = forage ? GetZoneForagableIdOptions() : GetZoneUnitIdOptions(z.Kind);

        DrawSmallText(forage ? "Item spawning" : "Unit spawning", x, y, AccentColor);
        y += LineHeight;

        for (int row = 0; row < z.Spawns.Count; row++)
        {
            // Each row needs 3 fields + a button; truncate instead of overflowing the panel.
            if (y > rect.Bottom - (FieldHeight * 3 + ButtonHeight + LineHeight * 2 + 14))
            {
                DrawSmallText($"... {z.Spawns.Count - row} more (panel full)", x, y, TextDim);
                y += LineHeight;
                break;
            }

            var e = z.Spawns[row];
            e.DefId = _eb.DrawCombo($"zone_spawn_def_{idx}_{row}", forage ? "Item" : "Unit",
                e.DefId, options, x, y, fw);
            y += FieldHeight + 2;
            e.PerMinute = MathF.Max(0f, _eb.DrawFloatField($"zone_spawn_rate_{idx}_{row}",
                "Per minute", e.PerMinute, x, y, fw, 0.1f));
            y += FieldHeight + 2;
            e.MaxAlive = Math.Max(1, _eb.DrawIntField($"zone_spawn_max_{idx}_{row}",
                "Max alive", e.MaxAlive, x, y, fw));
            y += FieldHeight + 2;

            if (_eb.DrawButton("Remove", x, y, 70, ButtonHeight, DangerColor))
            {
                z.Spawns.RemoveAt(row);
                return; // list changed — re-layout next frame
            }
            DrawSmallText($"{(forage ? "in zone now" : "placed in zone")}: {CountZoneSpawnTargets(z, e, forage)}",
                x + 78, y + 4, TextDim);
            y += ButtonHeight + 4;

            Scope.Draw(_pixel, new Rectangle(rect.X, y, rect.Width, 1), SeparatorColor);
            y += 4;
        }

        if (y <= rect.Bottom - ButtonHeight - LineHeight * 2
            && _eb.DrawButton(forage ? "+ Add Item" : "+ Add Unit", x, y, 110, ButtonHeight))
        {
            z.Spawns.Add(new ZoneSpawnEntry
            {
                DefId = options.Length > 0 ? options[0] : "",
                PerMinute = 1f,
                MaxAlive = 5,
            });
        }
    }

    /// <summary>World overlays for the Zones tab: every zone as a transparent kind-colored
    /// fill with a darker opaque border (selected = brighter fill + near-white border +
    /// drag handles), plus the rubber-band preview while drawing a new zone.</summary>
    private void DrawZoneOverlays(int screenW, int screenH) {
       var handleColor = new Color(255, 255, 100, 220);
        var handleActiveColor = new Color(255, 120, 60, 255);
        const int handleSz = 6;

        var zones = _zoneSystem.Zones;
        for (int i = 0; i < zones.Count; i++)
        {
            var z = zones[i];
            bool selected = i == SelectedZoneIndex;

            var tl = _camera.WorldToScreen(new Vec2(z.X - z.HalfW, z.Y - z.HalfH), 0, screenW, screenH);
            var br = _camera.WorldToScreen(new Vec2(z.X + z.HalfW, z.Y + z.HalfH), 0, screenW, screenH);
            int rx = (int)tl.X, ry = (int)tl.Y;
            int rw = (int)(br.X - tl.X), rh = (int)(br.Y - tl.Y);
            if (rw <= 0 || rh <= 0) continue;

            Scope.Draw(_pixel, new Rectangle(rx, ry, rw, rh), ZoneColors.Fill(z.Kind, selected));
            // 2px opaque border: outer + 1px inset
            var border = ZoneColors.Border(z.Kind, selected);
            DrawRectBorder(rx, ry, rw, rh, border);
            if (rw > 2 && rh > 2)
                DrawRectBorder(rx + 1, ry + 1, rw - 2, rh - 2, border);

            if (selected)
            {
                int cx = rx + rw / 2;
                int cy = ry + rh / 2;

                // 4 corners
                DrawRegionHandleSquare(rx, ry, handleSz, _zoneHandle == RegionHandle.NW ? handleActiveColor : handleColor);
                DrawRegionHandleSquare(rx + rw, ry, handleSz, _zoneHandle == RegionHandle.NE ? handleActiveColor : handleColor);
                DrawRegionHandleSquare(rx + rw, ry + rh, handleSz, _zoneHandle == RegionHandle.SE ? handleActiveColor : handleColor);
                DrawRegionHandleSquare(rx, ry + rh, handleSz, _zoneHandle == RegionHandle.SW ? handleActiveColor : handleColor);

                // 4 edge midpoints
                DrawRegionHandleSquare(cx, ry, handleSz, _zoneHandle == RegionHandle.N ? handleActiveColor : handleColor);
                DrawRegionHandleSquare(rx + rw, cy, handleSz, _zoneHandle == RegionHandle.E ? handleActiveColor : handleColor);
                DrawRegionHandleSquare(cx, ry + rh, handleSz, _zoneHandle == RegionHandle.S ? handleActiveColor : handleColor);
                DrawRegionHandleSquare(rx, cy, handleSz, _zoneHandle == RegionHandle.W ? handleActiveColor : handleColor);

                // Body center indicator
                DrawRegionHandleSquare(cx, cy, handleSz - 1, _zoneHandle == RegionHandle.Body ? handleActiveColor : new Color(100, 255, 100, 180));
            }

            // Label at center
            var labelPos = _camera.WorldToScreen(new Vec2(z.X, z.Y), 0, screenW, screenH);
            DrawSmallText($"{z.Name}", (int)(labelPos.X - 20), (int)(labelPos.Y - 10), TextBright);
        }

        // Rubber-band preview while dragging out a new zone
        if (_zoneRubberBanding && _eb != null)
        {
            var mouse = _eb._input.Mouse;
            Vec2 cur = _camera.ScreenToWorld(new Vector2(mouse.X, mouse.Y), screenW, screenH);
            float x0 = MathF.Min(_zoneDragStartWorld.X, cur.X);
            float x1 = MathF.Max(_zoneDragStartWorld.X, cur.X);
            float y0 = MathF.Min(_zoneDragStartWorld.Y, cur.Y);
            float y1 = MathF.Max(_zoneDragStartWorld.Y, cur.Y);
            var kind = (ZoneKind)_zoneCreateKindIdx;

            var tl = _camera.WorldToScreen(new Vec2(x0, y0), 0, screenW, screenH);
            var br = _camera.WorldToScreen(new Vec2(x1, y1), 0, screenW, screenH);
            int rx = (int)tl.X, ry = (int)tl.Y;
            int rw = (int)(br.X - tl.X), rh = (int)(br.Y - tl.Y);
            if (rw > 0 && rh > 0)
            {
                Scope.Draw(_pixel, new Rectangle(rx, ry, rw, rh), ZoneColors.Fill(kind, selected: true));
                DrawRectBorder(rx, ry, rw, rh, ZoneColors.Border(kind, selected: true));
            }
        }
    }

    /// <summary>Dev-command hook (`select` while the map editor is open): select a zone
    /// by index, id, or name and switch to the Zones tab, so headless drives can
    /// exercise the selected-state UI. Returns a description of the match, or null.</summary>
    public string? DevSelectZone(string token)
    {
        var zones = _zoneSystem.Zones;
        int found = -1;
        if (int.TryParse(token, out int idx) && idx >= 0 && idx < zones.Count)
            found = idx;
        else
            for (int i = 0; i < zones.Count; i++)
                if (string.Equals(zones[i].Id, token, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(zones[i].Name, token, StringComparison.OrdinalIgnoreCase))
                { found = i; break; }
        if (found < 0) return null;
        ActiveTab = MapEditorTab.Zones;
        SelectedZoneIndex = found;
        return $"zone {zones[found].Id} ({zones[found].Name})";
    }

    /// <summary>Dev-select an env object def on the Objects tab by index, id, or
    /// name (case-insensitive), for headless driving — the grid click path needs
    /// real mouse state. Returns null if nothing matched.</summary>
    public string? DevSelectObjectDef(string token)
    {
        int found = -1;
        if (int.TryParse(token, out int idx) && idx >= 0 && idx < _envSystem.DefCount)
            found = idx;
        else
            for (int i = 0; i < _envSystem.DefCount; i++)
            {
                var d = _envSystem.GetDef(i);
                if (string.Equals(d.Id, token, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(d.Name, token, StringComparison.OrdinalIgnoreCase))
                { found = i; break; }
            }
        if (found < 0) return null;
        ActiveTab = MapEditorTab.Objects;
        SelectedEnvDefIndex = found;
        var def = _envSystem.GetDef(found);
        return $"object def {def.Id} ({def.Name})";
    }

    /// <summary>Deep copy of a zone for the clipboard — Population and Spawns are
    /// reference types, so they must be cloned too. Keep in sync when MapZone grows
    /// fields (same rule as LoadZones/SaveZones).</summary>
    private static MapZone CloneZone(MapZone z) => new()
    {
        Id = z.Id,
        Name = z.Name,
        Kind = z.Kind,
        X = z.X,
        Y = z.Y,
        HalfW = z.HalfW,
        HalfH = z.HalfH,
        Population = new ZonePopulation
        {
            Peasant = z.Population.Peasant,
            Hunter = z.Population.Hunter,
            Militia = z.Population.Militia,
            Watchdog = z.Population.Watchdog,
        },
        Spawns = z.Spawns.Select(s => new ZoneSpawnEntry
        {
            DefId = s.DefId,
            PerMinute = s.PerMinute,
            MaxAlive = s.MaxAlive,
        }).ToList(),
    };

    /// <summary>Name for a pasted zone: "{base} Copy", then "{base} Copy 1", "Copy 2"…
    /// An existing " Copy"/" Copy N" tail on the source is stripped first, so copying
    /// a copy enumerates ("Village Copy" → "Village Copy 1", not "… Copy Copy").</summary>
    private string NextCopyName(string source)
    {
        string baseName = StripCopySuffix(source);
        string candidate = $"{baseName} Copy";
        for (int n = 1; ZoneNameTaken(candidate); n++)
            candidate = $"{baseName} Copy {n}";
        return candidate;
    }

    private static string StripCopySuffix(string name)
    {
        if (name.EndsWith(" Copy")) return name[..^5];
        int idx = name.LastIndexOf(" Copy ", StringComparison.Ordinal);
        if (idx >= 0 && int.TryParse(name[(idx + 6)..], out _)) return name[..idx];
        return name;
    }

    private bool ZoneNameTaken(string name)
    {
        for (int i = 0; i < _zoneSystem.Count; i++)
            if (_zoneSystem.Zones[i].Name == name) return true;
        return false;
    }

    /// <summary>Remove a zone with undo support, fixing up the selection. Shared by
    /// the Delete key and the "Delete Zone" button.</summary>
    private void DeleteZone(int idx)
    {
        if (idx < 0 || idx >= _zoneSystem.Count) return;
        var z = _zoneSystem.Zones[idx];
        PushUndo(new UndoZoneRemove { Owner = this, Zone = z, Index = idx });
        _zoneSystem.Remove(idx);
        SelectedZoneIndex = Math.Min(SelectedZoneIndex, _zoneSystem.Count - 1);
        _statusMessage = $"Deleted zone '{z.Name}'";
        _statusTimer = 1.5f;
    }

    /// <summary>First free "zone_N" id (ids stay unique even after deletions).</summary>
    private string NextZoneId()
    {
        for (int n = 0; ; n++)
        {
            string id = $"zone_{n}";
            bool taken = false;
            for (int i = 0; i < _zoneSystem.Count; i++)
                if (_zoneSystem.Zones[i].Id == id) { taken = true; break; }
            if (!taken) return id;
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

        // (No wheel handler here: the generic per-tab handler in Update()
        // already scrolls _tabScroll[6] — a second one doubled the rate.)
    }

    private void DrawTriggersTab(int panelX, int contentY, int contentH)
    {
        int viewBottom = contentY + contentH; // tab clip bottom, before the header advances contentY
        DrawSectionHeader(panelX, ref contentY, "Triggers");

        var mouse = _eb._input.Mouse;

        // Sub-section tabs: Defs | Instances
        int halfW = (PanelWidth - Margin * 2) / 2;
        {
            var defsRect = new Rectangle(panelX + Margin, contentY, halfW - 1, ButtonHeight);
            var instRect = new Rectangle(panelX + Margin + halfW, contentY, halfW - 1, ButtonHeight);
            Scope.Draw(_pixel, defsRect, _triggerSubSection == 0 ? TabActiveColor : TabInactiveColor);
            Scope.Draw(_pixel, instRect, _triggerSubSection == 1 ? TabActiveColor : TabInactiveColor);
            DrawTextCentered($"Defs ({_triggerSystem.Triggers.Count})", defsRect, TextColor);
            DrawTextCentered($"Instances ({_triggerSystem.Instances.Count})", instRect, TextColor);
        }
        contentY += ButtonHeight + 4;
        Scope.Draw(_pixel, new Rectangle(panelX, contentY - 2, PanelWidth, 1), SeparatorColor);

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
                if (bg != Color.Transparent) Scope.Draw(_pixel, btnRect, bg);

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
                Scope.Draw(_pixel, new Rectangle(panelX, y, PanelWidth, 1), SeparatorColor);
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
                    var effMouse = _eb._input.Mouse;
                    // Gate on the popup state: an effect's Faction/PostBehavior combo
                    // dropdown expands downward over the effect rows below it, so
                    // without this a click on a dropdown item also re-selects the
                    // effect underneath.
                    if (!IsAnyPopupBlocking() && IsInRect(effMouse, effRect) && effMouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released)
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
                if (bg != Color.Transparent) Scope.Draw(_pixel, btnRect, bg);

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
                Scope.Draw(_pixel, new Rectangle(panelX, y, PanelWidth, 1), SeparatorColor);
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

        DrawTabScrollbar(panelX, contentY, viewBottom, y + LineHeight, ref _tabScroll[6]);
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
    //  UNITS TAB
    // ====================================================================

    private void UpdateUnitsTab(MouseState mouse, KeyboardState kb, bool leftClick,
        bool overPanel, int panelX, int panelY, int screenW, int screenH)
    {
        if (_gameData == null) return;

        // Click on a grid cell to select a unit def (uses cached layout from Draw)
        if (leftClick && overPanel && _unitGridDrawY > 0 && _unitGridCellW > 0)
        {
            var unitIds = GetFilteredUnitIds();
            int colStride = _unitGridCellW + ThumbGridGap;
            int rowStride = _unitGridCellH + ThumbGridGap;
            int relX = mouse.X - _unitGridDrawX;
            int relY = mouse.Y - _unitGridDrawY;
            // Content-space Y: the grid scrolls continuously (by pixels), so
            // add the raw scroll before the row math — quantizing the scroll
            // to rows would misalign clicks when scrolled mid-row.
            int conY = relY + (int)_tabScroll[(int)MapEditorTab.Units];
            if (relX >= 0 && relY >= 0 && relY < _unitGridViewH
                && relX % colStride < _unitGridCellW && conY % rowStride < _unitGridCellH)
            {
                int col = relX / colStride;
                int row = conY / rowStride;
                int idx = row * ThumbGridCols + col;
                if (col < ThumbGridCols && idx >= 0 && idx < unitIds.Count)
                    _selectedUnitDefIdx = idx;
            }
        }

        // Click on world to place selected unit
        if (leftClick && !overPanel && _selectedUnitDefIdx >= 0)
        {
            var unitIds = GetFilteredUnitIds();
            if (_selectedUnitDefIdx < unitIds.Count)
            {
                Vec2 worldPos = _camera.ScreenToWorld(new Vector2(mouse.X, mouse.Y), screenW, screenH);
                var unitDef = _gameData.Units.Get(unitIds[_selectedUnitDefIdx]);
                _placedUnits.Add(new PlacedUnit
                {
                    UnitDefId = unitIds[_selectedUnitDefIdx],
                    X = worldPos.X,
                    Y = worldPos.Y,
                    Faction = unitDef.Faction ?? "Undead",
                    PatrolRouteId = _unitPatrolRoute,
                    IsCorpse = _placeAsCorpse,
                });
                PushUndo(new UndoUnitPlace { Units = _placedUnits });
            }
        }

        // Right-drag to delete placed units: while held, continuously remove the
        // nearest unit under the cursor so you can sweep over several. Previously
        // this was a single right-click per unit. Gated on the popup state so the
        // sweep doesn't delete units while a Faction/Patrol dropdown is open.
        bool rightDown = !IsAnyPopupBlocking() && mouse.RightButton == ButtonState.Pressed;
        if (rightDown && !overPanel && _placedUnits.Count > 0)
        {
            Vec2 worldPos = _camera.ScreenToWorld(new Vector2(mouse.X, mouse.Y), screenW, screenH);
            float bestDist = 4f * 4f; // generous click radius
            int bestIdx = -1;
            for (int i = 0; i < _placedUnits.Count; i++)
            {
                float dx = _placedUnits[i].X - worldPos.X, dy = _placedUnits[i].Y - worldPos.Y;
                float d = dx * dx + dy * dy;
                if (d < bestDist) { bestDist = d; bestIdx = i; }
            }
            if (bestIdx >= 0)
            {
                PushUndo(new UndoUnitRemove { Units = _placedUnits, Removed = _placedUnits[bestIdx], RemovedIndex = bestIdx });
                _placedUnits.RemoveAt(bestIdx);
            }
        }
    }

    private void DrawUnitsTab(int panelX, int contentY, int contentH, int screenW, int screenH)
    {
        if (_gameData == null || _eb == null) return;

        int x = panelX + Margin;
        int w = PanelWidth - Margin * 2;
        int curY = contentY + 4;

        // Faction filter. Changing it re-filters the list, so the selection
        // index (which points into the FILTERED list) must reset — otherwise it
        // silently re-points at a different unit.
        string[] factionLabels = { "All", "Undead", "Human", "Animal" };
        string curFaction = factionLabels[_unitFactionFilter];
        string newFaction = _eb.DrawCombo("unit_faction", "Faction", curFaction, factionLabels, x, curY, w);
        for (int fi = 0; fi < factionLabels.Length; fi++)
        {
            if (factionLabels[fi] == newFaction && fi != _unitFactionFilter)
            {
                _unitFactionFilter = fi;
                _selectedUnitDefIdx = -1;
            }
        }
        curY += 24;

        // Patrol route selector
        var routes = _triggerSystem.PatrolRoutes;
        if (routes.Count > 0)
        {
            var routeNames = new string[routes.Count + 1];
            routeNames[0] = "(none)";
            for (int ri = 0; ri < routes.Count; ri++)
                routeNames[ri + 1] = string.IsNullOrEmpty(routes[ri].Name) ? routes[ri].Id : routes[ri].Name;
            string curRoute = string.IsNullOrEmpty(_unitPatrolRoute) ? "(none)" : _unitPatrolRoute;
            string newRoute = _eb.DrawCombo("unit_patrol", "Patrol", curRoute, routeNames, x, curY, w);
            _unitPatrolRoute = newRoute == "(none)" ? "" : newRoute;
            // Resolve name back to ID if needed
            if (!string.IsNullOrEmpty(_unitPatrolRoute))
            {
                for (int ri = 0; ri < routes.Count; ri++)
                {
                    string rLabel = string.IsNullOrEmpty(routes[ri].Name) ? routes[ri].Id : routes[ri].Name;
                    if (rLabel == _unitPatrolRoute) { _unitPatrolRoute = routes[ri].Id; break; }
                }
            }
            curY += 24;
        }

        // Place-as-corpse toggle: when set, clicking the world drops a dead body
        // of the selected unit instead of a living one.
        _placeAsCorpse = _eb.DrawCheckbox("Place as corpse", _placeAsCorpse, x, curY, w);
        curY += 24;

        // Unit def grid: 6-wide sprite thumbnails (hover a cell for the name)
        // instead of one-per-row text — far more defs visible at once.
        var unitIds = GetFilteredUnitIds();
        _eb.DrawText("Unit Defs:", new Vector2(x, curY), EditorBase.TextBright);
        curY += 18;

        int listH = contentH - (curY - contentY) - 100;
        int cellW = (w - (ThumbGridCols - 1) * ThumbGridGap) / ThumbGridCols;
        int cellH = cellW;
        int rowStride = cellH + ThumbGridGap;
        int totalRows = (unitIds.Count + ThumbGridCols - 1) / ThumbGridCols;

        // Clamp the wheel-driven scroll to the end of the grid (the generic
        // wheel handler in Update only clamps at 0). Pixel-exact so the grid
        // scrolls continuously with the wheel, not a row at a time.
        float maxUnitScroll = Math.Max(0, totalRows * rowStride - listH);
        _tabScroll[(int)MapEditorTab.Units] = Math.Clamp(_tabScroll[(int)MapEditorTab.Units], 0, maxUnitScroll);
        int scrollPx = (int)_tabScroll[(int)MapEditorTab.Units];
        int firstRow = scrollPx / rowStride;
        int subRowOff = scrollPx % rowStride; // partial-row offset — rows glide, clip catches the spill

        // Cache for Update hit-testing
        _unitGridDrawX = x;
        _unitGridDrawY = curY;
        _unitGridCellW = cellW;
        _unitGridCellH = cellH;
        _unitGridViewH = listH;

        // MousePos, not raw .Mouse: the mousepos dev override patches only
        // MousePos, and with a real mouse the two are identical.
        int mx = (int)_eb._input.MousePos.X, my = (int)_eb._input.MousePos.Y;
        int hoveredIdx = -1;
        Rectangle hoveredCell = default;
        // Nested clip: partially-scrolled rows must not bleed above the grid
        // or into the placed-units section below it.
        _eb.BeginClip(new Rectangle(x, curY, w, listH));
        for (int r = 0; ; r++)
        {
            int row = firstRow + r;
            if (row >= totalRows) break;
            int cellY = curY + r * rowStride - subRowOff;
            if (cellY >= curY + listH) break;
            for (int c = 0; c < ThumbGridCols; c++)
            {
                int idx = row * ThumbGridCols + c;
                if (idx >= unitIds.Count) break;

                var cell = new Rectangle(x + c * (cellW + ThumbGridGap), cellY, cellW, cellH);
                var def = _gameData.Units.Get(unitIds[idx]);
                bool selected = idx == _selectedUnitDefIdx;
                bool hovered = cell.Contains(mx, my) && my >= curY && my < curY + listH;
                if (hovered) { hoveredIdx = idx; hoveredCell = cell; }

                Scope.Draw(_pixel, cell, selected ? new Color(60, 60, 100, 220)
                    : hovered ? new Color(40, 40, 70, 180) : new Color(25, 25, 35, 160));
                DrawUnitThumb(def, unitIds[idx], cell);
                if (selected)
                    _eb.DrawBorder(cell, new Color(255, 230, 160));
                else if (hovered)
                    _eb.DrawBorder(cell, new Color(120, 120, 170));
            }
        }
        _eb.EndClip();

        // Thin scrollbar in the panel's right margin, spanning the grid viewport.
        _eb.DrawVScrollbar(panelX + PanelWidth - 6, curY, listH,
            totalRows * rowStride, _tabScroll[(int)MapEditorTab.Units]);
        curY += listH;

        // Hover tooltip — queued globally, drawn topmost after the clip closes.
        if (hoveredIdx >= 0 && hoveredIdx < unitIds.Count)
        {
            var def = _gameData.Units.Get(unitIds[hoveredIdx]);
            string tip = def != null ? $"{def.DisplayName} [{def.Faction}]" : unitIds[hoveredIdx];
            DrawGridCellTooltip(tip, hoveredCell);
        }

        // Placed units count
        _eb.DrawText($"Placed: {_placedUnits.Count} units", new Vector2(x, curY + 4), EditorBase.TextBright);
        curY += 20;

        // Clear all button — confirmed (one mis-click otherwise destroyed every
        // placed unit on the map) and undoable.
        if (_eb.DrawButton("Clear All Units", x, curY, 120, 20, EditorBase.DangerColor)
            && _placedUnits.Count > 0)
            _confirmClearUnits = true;
    }

    // Confirm flag for "Clear All Units" (dialog drawn at end of Draw).
    private bool _confirmClearUnits;

    private List<string> GetFilteredUnitIds()
    {
        if (_gameData == null) return new();
        var allIds = _gameData.Units.GetIDs();
        if (_unitFactionFilter == 0) return new List<string>(allIds);

        string filterFaction = _unitFactionFilter switch { 1 => "Undead", 2 => "Human", 3 => "Animal", _ => "" };
        var filtered = new List<string>();
        foreach (var id in allIds)
        {
            var def = _gameData.Units.Get(id);
            if (def != null && def.Faction == filterFaction)
                filtered.Add(id);
        }
        return filtered;
    }

    /// <summary>Representative frame for a unit's grid thumbnail: Idle (fall
    /// back Walk, then any anim), facing 60° (down-right, the unit editor's
    /// portrait angle), first keyframe.</summary>
    private static SpriteFrame? GetUnitThumbFrame(Data.Registries.UnitDef def)
    {
        var sd = def.SpriteData;
        if (sd == null) return null;
        var anim = sd.GetAnim("Idle") ?? sd.GetAnim("Walk");
        if (anim == null)
            foreach (var a in sd.Animations.Values) { anim = a; break; }
        if (anim == null) return null;
        var kfs = anim.GetAngle(60);
        if (kfs == null || kfs.Count == 0)
            foreach (var (_, v) in anim.AngleFrames)
                if (v.Count > 0) { kfs = v; break; }
        if (kfs == null || kfs.Count == 0) return null;
        return kfs[0].Frame;
    }

    /// <summary>Sprite thumbnail fitted+centered in a grid cell; defs without
    /// a resolvable sprite fall back to the unit's initial so the cell stays
    /// readable (and indices stay dense for hit-testing).</summary>
    private void DrawUnitThumb(Data.Registries.UnitDef? def, string unitId, Rectangle cell)
    {
        var frame = def != null ? GetUnitThumbFrame(def) : null;
        Texture2D? tex = null;
        if (frame.HasValue && def?.Sprite != null)
        {
            int atlasIdx = AtlasDefs.ResolveAtlasName(def.Sprite.AtlasName);
            if (atlasIdx >= 0 && atlasIdx < _game._atlases.Length && _game._atlases[atlasIdx] != null)
                tex = _game._atlases[atlasIdx].GetTextureForFrame(frame.Value);
        }
        if (tex == null || frame!.Value.Rect.Width <= 0 || frame.Value.Rect.Height <= 0)
        {
            string name = def?.DisplayName ?? unitId;
            string letter = string.IsNullOrEmpty(name) ? "?" : name[..1].ToUpperInvariant();
            var ls = _eb!.MeasureText(letter, _smallFont);
            _eb.DrawText(letter, new Vector2(cell.X + (cell.Width - (int)ls.X) / 2,
                cell.Y + (cell.Height - (int)ls.Y) / 2), TextColor, _smallFont);
            return;
        }
        var rect = frame.Value.Rect;
        float scale = Math.Min((cell.Width - 4f) / rect.Width, (cell.Height - 4f) / rect.Height);
        scale = Math.Min(scale, 3f); // don't blow tiny sprites up into pixel mush
        _eb!.DrawTexture(tex, new Vector2(cell.Center.X, cell.Center.Y), rect, Color.White,
            0f, new Vector2(rect.Width / 2f, rect.Height / 2f), scale, SpriteEffects.None);
    }

    /// <summary>Env-def sprite thumbnail fitted+centered in a grid cell (frame 0
    /// for animated spritesheets); defs with no texture fall back to the def's
    /// initial so the cell stays readable.</summary>
    private void DrawEnvDefThumb(int defIdx, EnvironmentObjectDef def, Rectangle cell)
    {
        var tex = _envSystem.GetDefTexture(defIdx);
        if (tex == null)
        {
            string letter = string.IsNullOrEmpty(def.Name) ? "?" : def.Name[..1].ToUpperInvariant();
            var ls = _eb!.MeasureText(letter, _smallFont);
            _eb.DrawText(letter, new Vector2(cell.X + (cell.Width - (int)ls.X) / 2,
                cell.Y + (cell.Height - (int)ls.Y) / 2), TextColor, _smallFont);
            return;
        }
        // Placeholder textures are a flat sheet with no frames — slicing them
        // by AnimFrames produces invisible slivers (see IsUsingPlaceholder).
        var rect = _envSystem.IsUsingPlaceholder(defIdx)
            ? new Rectangle(0, 0, tex.Width, tex.Height)
            : def.GetAnimFrameRect(tex.Width, tex.Height, 0);
        if (rect.Width <= 0 || rect.Height <= 0) return;
        float scale = Math.Min((cell.Width - 4f) / rect.Width, (cell.Height - 4f) / rect.Height);
        scale = Math.Min(scale, 3f); // don't blow tiny sprites up into pixel mush
        _eb!.DrawTexture(tex, new Vector2(cell.Center.X, cell.Center.Y), rect, Color.White,
            0f, new Vector2(rect.Width / 2f, rect.Height / 2f), scale, SpriteEffects.None);
    }

    /// <summary>Dev-command hook ("map_scroll"): set the active tab's list
    /// scroll in pixels, standing in for the mouse wheel in headless runs.
    /// The next Draw clamps it like any wheel input.</summary>
    public void DevSetScroll(float px)
    {
        if (ActiveTab == MapEditorTab.Objects) _envListScroll = Math.Max(0, px);
        else _tabScroll[(int)ActiveTab] = Math.Max(0, px);
    }

    /// <summary>Thin scrollbar at the panel's right edge for a scrolling tab
    /// body. viewTop = where the scrollable content starts (post-header),
    /// viewBottom = the tab clip bottom, contentBottom = the bottom of the last
    /// laid-out row in screen space (i.e. already offset by -scroll). Also
    /// max-clamps the scroll so the wheel can't run endlessly past the end
    /// (the generic wheel handler only clamps at 0). Tabs that cull rows with
    /// an early break under-report contentBottom, so the clamp only ever
    /// trails the true end — it never blocks reaching it.</summary>
    private void DrawTabScrollbar(int panelX, int viewTop, int viewBottom, int contentBottom, ref float scroll)
    {
        int viewH = viewBottom - viewTop;
        float contentH = contentBottom + scroll - viewTop;
        scroll = Math.Clamp(scroll, 0f, Math.Max(0f, contentH - viewH));
        _eb?.DrawVScrollbar(panelX + PanelWidth - 6, viewTop, viewH, contentH, scroll);
    }

    /// <summary>Name tooltip above the hovered grid cell, via the global
    /// tooltip queue so it draws topmost and OUTSIDE the tab's BeginClip
    /// scissor (drawn inline it was hardware-clipped to the panel). Suppressed
    /// while a popup/dropdown/color-picker covers the grid — the Tooltip band
    /// draws above the Popup band, so an inline request would paint over them.</summary>
    private void DrawGridCellTooltip(string text, Rectangle anchorCell)
    {
        if (!Game1.Popups.IsEmpty || (_eb != null && (_eb.IsColorPickerOpen || _eb.IsDropdownOpen)))
            return;
        Game1.Tooltips.RequestText(text, anchorCell);
    }

    private void DrawPlacedUnitMarkers(int screenW, int screenH)
    {
        foreach (var pu in _placedUnits)
        {
            var sp = _camera.WorldToScreen(new Vec2(pu.X, pu.Y), 0f, screenW, screenH);
            float r = 6f;
            Color markerColor = pu.Faction switch
            {
                "Human" => new Color(100, 150, 255, 200),
                "Animal" => new Color(100, 200, 100, 200),
                _ => new Color(200, 100, 255, 200) // Undead = purple
            };

            // Text stays readable (the marker shape, not the text, carries the
            // faction/corpse coding). Dimming the label made corpse names unreadable.
            Color labelColor = markerColor;
            if (pu.IsCorpse)
            {
                // Corpses: a bone-grey cross instead of a faction diamond so dead
                // bodies read differently from living placements at a glance.
                markerColor = new Color(170, 165, 150, 220);
                labelColor = markerColor;
                Scope.Draw(_pixel, new Rectangle((int)(sp.X - r), (int)(sp.Y - 1), (int)(r * 2), 2), markerColor);
                Scope.Draw(_pixel, new Rectangle((int)(sp.X - 1), (int)(sp.Y - r), 2, (int)(r * 2)), markerColor);
            }
            else
            {
                // Diamond marker
                Scope.Draw(_pixel, new Rectangle((int)(sp.X - r), (int)(sp.Y - r / 2), (int)(r * 2), (int)r), markerColor);
            }
            // Label
            if (_smallFont != null)
            {
                string label = _gameData.Units.NameOf(pu.UnitDefId);
                if (label.Length > 10) label = label[..10];
                // Explicitly tag corpses so they're identifiable like living units are.
                if (pu.IsCorpse) label += " (corpse)";
                Scope.DrawString(_smallFont, label, new Vector2((int)(sp.X + 8), (int)(sp.Y - 6)), labelColor);
            }
        }
    }

    // ====================================================================
    //  PROCGEN TAB
    // ====================================================================

    /// <summary>Lazy one-shot load of the global style registry. Called from the
    /// tab and from SaveMap (so saving without ever opening the tab round-trips
    /// the file instead of clobbering it with an empty list).</summary>
    private void EnsureProcGenStylesLoaded()
    {
        if (_procGenStylesLoaded) return;
        _procGenStylesLoaded = true;
        try
        {
            ProcGenStyle.LoadAll(GamePaths.Resolve("data/procgen_styles.json"), _procGenStyles);
        }
        catch (Exception ex)
        {
            DebugLog.Log("editor", $"procgen styles load error: {ex.Message}");
        }
    }

    /// <summary>Every env def id, for the style pool combos. Cached until the def
    /// list changes (same invalidation as GetZoneForagableIdOptions).</summary>
    private string[] GetProcGenDefIdOptions()
    {
        if (_procGenDefIdOptions == null || _procGenDefIdOptionCount != _envSystem.DefCount)
        {
            var list = new List<string>(_envSystem.DefCount);
            for (int i = 0; i < _envSystem.DefCount; i++)
                list.Add(_envSystem.GetDef(i).Id);
            _procGenDefIdOptions = list.ToArray();
            _procGenDefIdOptionCount = _envSystem.DefCount;
        }
        return _procGenDefIdOptions;
    }

    /// <summary>Dev-server hook (procgen_paint): paint a style at a world point for
    /// a simulated duration, as if the brush were held there — exercises the exact
    /// PaintProcGen path the editor uses. Returns a result summary string.</summary>
    public string DevPaintProcGen(string styleName, Vec2 pos, float seconds)
    {
        EnsureProcGenStylesLoaded();
        var style = _procGenStyles.FirstOrDefault(
            s => s.Name.Equals(styleName, StringComparison.OrdinalIgnoreCase));
        if (style == null)
            return $"style '{styleName}' not found ({_procGenStyles.Count} styles loaded)";

        int before = _envSystem.ObjectCount;
        foreach (var cat in style.Categories) cat.Accum = 0f;
        int ticks = Math.Max(1, (int)(seconds * 60f));
        for (int i = 0; i < ticks; i++)
            PaintProcGen(style, pos, 1f / 60f);
        return $"placed {_envSystem.ObjectCount - before} objects over {ticks} ticks";
    }

    private void UpdateProcGenTab(MouseState mouse, bool leftDown, bool leftUp,
        bool overPanel, int screenW, int screenH, float dt)
    {
        EnsureProcGenStylesLoaded();

        // Default to the first style so the tab is paint-ready on open.
        if (SelectedProcGenStyle < 0 && _procGenStyles.Count > 0)
            SelectedProcGenStyle = 0;

        bool styleSelected = SelectedProcGenStyle >= 0 && SelectedProcGenStyle < _procGenStyles.Count;
        if (styleSelected && leftDown && !overPanel)
        {
            _batchPlacedObjects ??= new();
            _autoGroundStrokeOld ??= new();
            Vec2 worldPos = _camera.ScreenToWorld(new Vector2(mouse.X, mouse.Y), screenW, screenH);
            PaintProcGen(_procGenStyles[SelectedProcGenStyle], worldPos, dt);
        }
        else if (styleSelected)
        {
            // Not painting: drop fractional attempts so a long pause can't burst
            // a backlog of placements on the next press.
            foreach (var cat in _procGenStyles[SelectedProcGenStyle].Categories) cat.Accum = 0f;
        }

        if (leftUp)
            FinalizeBatchPlaceStroke();
    }

    /// <summary>Finalize a batch object-placement stroke (Objects paint or ProcGen tab):
    /// build an UndoObjectBatchPlace from _batchPlacedObjects, composite it with an
    /// UndoGroundStroke from _autoGroundStrokeOld when auto-ground was stamped during the
    /// stroke, push, and reset both accumulators. Tier cost fields were kept current via
    /// StampObjectCollisionAt inside the stroke loop, so no full rebake here. Call on
    /// left-mouse-up.</summary>
    private void FinalizeBatchPlaceStroke()
    {
        if (_batchPlacedObjects == null) return;
        if (_batchPlacedObjects.Count > 0)
        {
            var batchUndo = new UndoObjectBatchPlace
            {
                Env = _envSystem,
                ObjectIndices = new List<int>(_batchPlacedObjects.Select(b => b.objIdx))
            };
            // Bundle any auto-ground stamped during the stroke so one undo reverts both
            // the objects and the ground under them.
            if (_autoGroundStrokeOld != null && _autoGroundStrokeOld.Count > 0)
            {
                var comp = new UndoComposite();
                comp.Actions.Add(batchUndo);
                comp.Actions.Add(new UndoGroundStroke
                {
                    Ground = _groundSystem,
                    OldValues = _autoGroundStrokeOld,
                    OnChanged = _onVertexMapChanged
                });
                PushUndo(comp);
            }
            else PushUndo(batchUndo);
        }
        _batchPlacedObjects = null;
        _autoGroundStrokeOld = null;
    }

    /// <summary>One held-brush tick: each category accrues density*5 placement
    /// attempts per second; each attempt tries up to ProcGenTries random points in
    /// the brush disc and places unless another object from the same category is
    /// closer than 8/sqrt(density) (or physical collision rejects the spot).</summary>
    private void PaintProcGen(ProcGenStyle style, Vec2 center, float dt)
    {
        // Bound the per-frame burst so a frame hitch can't queue thousands of
        // attempts; overflow beyond the cap is dropped, fractions carry over.
        const int maxPerFrame = 400;

        // Suppress the per-AddObject pathfinder rebuild for the whole tick and
        // stamp collisions incrementally (same pattern as PaintObjectsBatch).
        var prevHandler = _envSystem.OnCollisionsDirty;
        _envSystem.OnCollisionsDirty = null;
        bool groundChanged = false;
        try
        {
            foreach (var cat in style.Categories)
            {
                cat.Accum += cat.Density * ProcGenRatePerDensity * dt;
                int n = Math.Min((int)cat.Accum, maxPerFrame);
                cat.Accum = Math.Min(cat.Accum - n, 1f);
                if (n <= 0) continue;

                // Resolve ids fresh each tick — the env object editor can reorder
                // defs mid-session, so cached indices would go stale.
                var pool = ResolveProcGenDefs(cat.DefIds);
                groundChanged |= PlaceProcGenPool(pool, n, ProcGenStyle.MinDistance(cat.Density), center, cat.AutoGround);
            }
        }
        finally
        {
            _envSystem.OnCollisionsDirty = prevHandler;
        }

        // Flush the ground texture once per tick if any auto-ground was stamped
        // (StampGroundPatch deliberately doesn't fire the callback itself).
        if (groundChanged) _onVertexMapChanged?.Invoke();
    }

    private List<int> ResolveProcGenDefs(List<string> defIds)
    {
        var result = new List<int>(defIds.Count);
        foreach (var id in defIds)
        {
            int idx = _envSystem.FindDef(id);
            if (idx >= 0) result.Add(idx);
        }
        return result;
    }

    /// <summary>Place a pool's objects. Returns true if any auto-ground was stamped
    /// (recorded into <see cref="_autoGroundStrokeOld"/> for the stroke's undo); the
    /// caller flushes the ground texture once per tick.</summary>
    private bool PlaceProcGenPool(List<int> pool, int attempts, float minDist, Vec2 center,
        AutoGroundSettings autoGround)
    {
        if (pool.Count == 0 || attempts <= 0) return false;

        var poolSet = new HashSet<int>(pool);
        float minDistSq = minDist * minDist;
        bool groundChanged = false;

        for (int a = 0; a < attempts; a++)
        {
            for (int t = 0; t < ProcGenTries; t++)
            {
                // Uniform random point in the brush disc (sqrt for area-uniformity).
                float ang = Random.Shared.NextSingle() * MathF.Tau;
                float rad = ProcGenBrushRadius * MathF.Sqrt(Random.Shared.NextSingle());
                float px = center.X + MathF.Cos(ang) * rad;
                float py = center.Y + MathF.Sin(ang) * rad;

                // Density rule: nothing placed from the same pool within minDist.
                bool tooClose = false;
                for (int i = 0; i < _envSystem.ObjectCount; i++)
                {
                    var obj = _envSystem.GetObject(i);
                    if (!poolSet.Contains(obj.DefIndex)) continue;
                    float dx = obj.X - px, dy = obj.Y - py;
                    if (dx * dx + dy * dy < minDistSq) { tooClose = true; break; }
                }
                if (tooClose) continue;

                int defIdx = pool[Random.Shared.Next(pool.Count)];
                float scale = GetRandomPlacementScale(defIdx);
                if (!_envSystem.CanPlaceObject(defIdx, px, py, scale)) continue;

                int newIdx = _envSystem.AddObject((ushort)defIdx, px, py, scale, persistent: true);
                _batchPlacedObjects?.Add(((ushort)defIdx, px, py, scale, 0f, newIdx));
                _envSystem.StampObjectCollisionAt(_tileGrid, newIdx);
                AutoCreateTriggerInstance(newIdx); // RM06

                // Per-pool auto-ground under this object (accumulated for one
                // stroke-wide undo; texture flushed once per tick by the caller).
                if (autoGround.Enabled && _autoGroundStrokeOld != null
                    && StampAutoGround(autoGround, px, py, _autoGroundStrokeOld))
                    groundChanged = true;

                break; // placed — move to the next attempt
            }
        }

        return groundChanged;
    }

    private void DrawProcGenTab(int panelX, int contentY, int contentH)
    {
        EnsureProcGenStylesLoaded();
        int viewBottom = contentY + contentH; // tab clip bottom, before the header advances contentY
        DrawSectionHeader(panelX, ref contentY, $"ProcGen Styles ({_procGenStyles.Count})");
        if (_eb == null) return;

        int y = contentY - (int)_tabScroll[(int)MapEditorTab.ProcGen];
        int fw = PanelWidth - Margin * 2;

        if (_eb.DrawButton("+ New Style", panelX + Margin, y, fw, ButtonHeight))
        {
            _procGenStyles.Add(ProcGenStyle.NewDefault($"Style {_procGenStyles.Count + 1}"));
            SelectedProcGenStyle = _procGenStyles.Count - 1;
        }
        y += ButtonHeight + 8;

        Scope.Draw(_pixel, new Rectangle(panelX, y, PanelWidth, 1), SeparatorColor);
        y += 4;

        // Style list — invisible row buttons with the name drawn on top (zones-list style).
        for (int i = 0; i < _procGenStyles.Count; i++)
        {
            bool selected = i == SelectedProcGenStyle;
            if (_eb.DrawButton("", panelX + Margin, y, fw, ButtonHeight,
                selected ? HighlightColor : Color.Transparent))
                SelectedProcGenStyle = i;
            DrawSmallText(_procGenStyles[i].Name, panelX + Margin + 8, y + 3,
                selected ? TextBright : TextColor);
            y += ButtonHeight + 2;
        }
        y += 4;

        if (SelectedProcGenStyle < 0 || SelectedProcGenStyle >= _procGenStyles.Count)
        {
            DrawSmallText("Create or select a style, then hold", panelX + Margin, y, TextDim);
            y += LineHeight;
            DrawSmallText("LMB in the world to paint.", panelX + Margin, y, TextDim);
            return;
        }

        var st = _procGenStyles[SelectedProcGenStyle];
        int idx = SelectedProcGenStyle;

        if (_eb.DrawButton("Delete Style", panelX + Margin, y, 110, ButtonHeight, DangerColor))
        {
            _procGenStyles.RemoveAt(idx);
            SelectedProcGenStyle = Math.Min(idx, _procGenStyles.Count - 1);
            return; // list changed — re-layout next frame
        }
        if (_eb.DrawButton("Copy Style", panelX + Margin + 116, y, 110, ButtonHeight))
        {
            var copy = st.Clone();
            copy.Name = $"{st.Name} copy";
            _procGenStyles.Insert(idx + 1, copy);
            SelectedProcGenStyle = idx + 1;
            return; // list changed — re-layout next frame
        }
        y += ButtonHeight + 8;

        Scope.Draw(_pixel, new Rectangle(panelX, y, PanelWidth, 1), SeparatorColor);
        y += 4;

        st.Name = _eb.DrawTextField($"procgen_name_{idx}", "Style Name", st.Name, panelX + Margin, y, fw);
        y += FieldHeight + 6;

        // Divider between the style name and the category list.
        Scope.Draw(_pixel, new Rectangle(panelX, y, PanelWidth, 1), SeparatorColor);
        y += 4;

        // One config block per category. Removing a category re-lays out next frame.
        for (int c = 0; c < st.Categories.Count; c++)
        {
            if (c > 0)
            {
                Scope.Draw(_pixel, new Rectangle(panelX, y, PanelWidth, 1), SeparatorColor);
                y += 4;
            }
            y = DrawProcGenCategorySection(idx, c, st.Categories[c], panelX, y, out bool removed);
            if (removed)
            {
                st.Categories.RemoveAt(c);
                return; // list changed — re-layout next frame
            }
        }

        Scope.Draw(_pixel, new Rectangle(panelX, y, PanelWidth, 1), SeparatorColor);
        y += 4;

        if (_eb.DrawButton("+ Category", panelX + Margin, y, 110, ButtonHeight))
            st.Categories.Add(new ProcGenCategory { Name = $"Category {st.Categories.Count + 1}" });
        y += ButtonHeight + 8;

        DrawSmallText("Hold LMB in the world to paint.", panelX + Margin, y, TextDim);

        // (Skipped on the early "list changed" returns above — one frame only.)
        DrawTabScrollbar(panelX, contentY, viewBottom, y + LineHeight,
            ref _tabScroll[(int)MapEditorTab.ProcGen]);
    }

    /// <summary>One category's config block: editable name + remove button, a density
    /// field (with derived spacing/rate readout), a row per pool member (combo +
    /// remove), an add button, and the shared auto-ground controls. Field ids embed
    /// the style index and category index so selection changes drop text focus.
    /// Sets <paramref name="removed"/> when the user clicked this category's remove
    /// button — the caller deletes it and re-lays out.</summary>
    private int DrawProcGenCategorySection(int styleIdx, int catIdx, ProcGenCategory cat,
        int panelX, int y, out bool removed)
    {
        removed = false;
        var eb = _eb!;
        int fw = PanelWidth - Margin * 2;

        // Header: editable category name + remove-category button.
        cat.Name = eb.DrawTextField($"procgen_cat_name_{styleIdx}_{catIdx}", "Category Name", cat.Name,
            panelX + Margin, y, fw - 46);
        if (eb.DrawButton("X", panelX + Margin + fw - 40, y, 40, FieldHeight, DangerColor))
        {
            removed = true;
            return y;
        }
        y += FieldHeight + 4;

        cat.Density = MathF.Max(0.01f, eb.DrawFloatField($"procgen_cat_density_{styleIdx}_{catIdx}",
            "Density", cat.Density, panelX + Margin, y, fw, 1f));
        y += FieldHeight + 2;
        DrawSmallText($"min spacing {ProcGenStyle.MinDistance(cat.Density):0.0}, " +
            $"{cat.Density * ProcGenRatePerDensity:0}/s", panelX + Margin, y, TextDim);
        y += LineHeight;

        string[] options = GetProcGenDefIdOptions();
        for (int row = 0; row < cat.DefIds.Count; row++)
        {
            cat.DefIds[row] = eb.DrawCombo($"procgen_cat_def_{styleIdx}_{catIdx}_{row}", $"Object {row + 1}",
                cat.DefIds[row], options, panelX + Margin, y, fw - 46);
            if (eb.DrawButton("X", panelX + Margin + fw - 40, y, 40, FieldHeight, DangerColor))
            {
                cat.DefIds.RemoveAt(row);
                return y; // list changed — re-layout next frame
            }
            y += FieldHeight + 2;
        }

        if (options.Length > 0 && eb.DrawButton("+ Add Object", panelX + Margin, y, 110, ButtonHeight))
            cat.DefIds.Add(options[0]);
        y += ButtonHeight + 6;

        // Per-category auto-ground toggle + controls (shared with the Objects tab).
        DrawAutoGroundControls(cat.AutoGround, $"pg_{styleIdx}_{catIdx}", panelX, ref y);

        return y;
    }

    /// <summary>World-space brush cursor for the ProcGen tab: a fixed radius-5
    /// circle outline at the mouse (the brush isn't tile-snapped like the grid
    /// brushes, so a ring beats the square-tile cursor).</summary>
    private void DrawProcGenBrushCursor(int screenW, int screenH)
    {
        var mouse = _eb._input.Mouse;
        Vec2 c = _camera.ScreenToWorld(new Vector2(mouse.X, mouse.Y), screenW, screenH);

        const int segs = 48;
        Vector2 prev = default;
        for (int i = 0; i <= segs; i++)
        {
            float ang = i / (float)segs * MathF.Tau;
            var s = _camera.WorldToScreen(new Vec2(
                c.X + MathF.Cos(ang) * ProcGenBrushRadius,
                c.Y + MathF.Sin(ang) * ProcGenBrushRadius), 0, screenW, screenH);
            var pt = new Vector2(s.X, s.Y);
            if (i > 0) DrawLine(prev, pt, BrushCursorEdge);
            prev = pt;
        }
    }

    // ====================================================================
    //  SAVE / LOAD
    // ====================================================================

    private void SaveMap()
    {
        try
        {
            string mapsDir = GamePaths.Resolve(GamePaths.MapsDir);
            Directory.CreateDirectory(mapsDir);
            string path = Path.Combine(mapsDir, _mapFilename + ".json");

            // Atomic: stream to .tmp, rename over the map on Commit — a crash
            // mid-save must not destroy the 55 MB default.json.
            using var stream = Core.AtomicFile.CreateStream(path);
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
                if (!string.IsNullOrEmpty(gt.CorruptedTypeId))
                    writer.WriteString("corruptedTypeId", gt.CorruptedTypeId);
                if (gt.MovementTerrain != Necroking.World.TerrainType.Open)
                    writer.WriteString("movementTerrain", gt.MovementTerrain.ToString());
                // Save tintColor when it diverges from white so PNGs stay the
                // authoritative palette source for un-tinted types.
                if (gt.TintColor != Microsoft.Xna.Framework.Color.White)
                {
                    writer.WriteStartObject("tintColor");
                    writer.WriteNumber("r", gt.TintColor.R);
                    writer.WriteNumber("g", gt.TintColor.G);
                    writer.WriteNumber("b", gt.TintColor.B);
                    if (gt.TintColor.A != 255) writer.WriteNumber("a", gt.TintColor.A);
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            // Ground vertex map (revert runtime corruption so dev paint is preserved on save)
            writer.WriteStartObject("groundMap");
            writer.WriteNumber("width", _groundSystem.VertexW);
            writer.WriteNumber("height", _groundSystem.VertexH);
            writer.WriteString("tilesBase64", Convert.ToBase64String(_groundSystem.GetVertexMapForSave()));
            writer.WriteEndObject();

            // Grass types
            writer.WriteStartArray("grassTypes");
            foreach (var gt in _grassTypes)
            {
                writer.WriteStartObject();
                writer.WriteString("id", gt.Id);
                writer.WriteString("name", gt.Name);

                writer.WriteStartObject("defaultTint");
                writer.WriteNumber("r", gt.DefaultTint.R);
                writer.WriteNumber("g", gt.DefaultTint.G);
                writer.WriteNumber("b", gt.DefaultTint.B);
                writer.WriteNumber("a", gt.DefaultTint.A);
                writer.WriteEndObject();

                writer.WriteStartObject("corruptedTint");
                writer.WriteNumber("r", gt.CorruptedTint.R);
                writer.WriteNumber("g", gt.CorruptedTint.G);
                writer.WriteNumber("b", gt.CorruptedTint.B);
                writer.WriteNumber("a", gt.CorruptedTint.A);
                writer.WriteEndObject();

                writer.WriteStartArray("spritePaths");
                foreach (var p in gt.SpritePaths)
                    if (!string.IsNullOrEmpty(p)) writer.WriteStringValue(p);
                writer.WriteEndArray();

                writer.WriteNumber("scale", gt.Scale);
                writer.WriteNumber("density", gt.Density);

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

            // Env defs are now saved separately to data/env_defs.json
            // Save them alongside the map save for convenience
            MapData.SaveEnvDefs(GamePaths.Resolve("data/env_defs.json"), _envSystem);

            // ProcGen styles are a global authoring registry like env defs. Load
            // first if the tab was never opened so an untouched registry survives.
            EnsureProcGenStylesLoaded();
            ProcGenStyle.SaveAll(GamePaths.Resolve("data/procgen_styles.json"), _procGenStyles);

            // Placed objects. Only persistent ones (editor-placed / map-loaded /
            // player-built) — gameplay spawns (zone foragables, village stamps,
            // creature drops) would otherwise accumulate in the map JSON forever.
            writer.WriteStartArray("placedObjects");
            for (int i = 0; i < _envSystem.ObjectCount; i++)
            {
                var obj = _envSystem.GetObject(i);
                if (!obj.Persistent) continue;
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

            // --- Placed units ---
            writer.WriteStartArray("placedUnits");
            foreach (var pu in _placedUnits)
            {
                writer.WriteStartObject();
                writer.WriteString("unitDefId", pu.UnitDefId);
                writer.WriteNumber("x", pu.X);
                writer.WriteNumber("y", pu.Y);
                if (!string.IsNullOrEmpty(pu.Faction))
                    writer.WriteString("faction", pu.Faction);
                if (!string.IsNullOrEmpty(pu.PatrolRouteId))
                    writer.WriteString("patrolRouteId", pu.PatrolRouteId);
                if (pu.IsCorpse)
                    writer.WriteBoolean("isCorpse", true);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
            writer.Flush();
            stream.Commit(); // payload complete — dispose will swap it in atomically

            // Sidecars (triggers / roads / zones) — one reader+writer in MapSidecars
            MapSidecars.SaveTriggers(Path.Combine(mapsDir, _mapFilename + "_triggers.json"), _triggerSystem);
            MapSidecars.SaveRoads(Path.Combine(mapsDir, _mapFilename + "_roads.json"), _roadSystem);
            MapSidecars.SaveZones(Path.Combine(mapsDir, _mapFilename + "_zones.json"), _zoneSystem);

            _statusMessage = $"Saved: {path}";
            _statusTimer = 3f;
            DebugLog.Log("editor", $"Map saved to {path}");

            // Files saved directly to canonical location
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
            string mapsDir = GamePaths.Resolve(GamePaths.MapsDir);
            string mapPath = Path.Combine(mapsDir, _mapFilename + ".json");
            string triggerPath = Path.Combine(mapsDir, _mapFilename + "_triggers.json");
            string roadsPath = Path.Combine(mapsDir, _mapFilename + "_roads.json");
            string zonesPath = Path.Combine(mapsDir, _mapFilename + "_zones.json");

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

            // Load env defs from canonical location, then map data (placed objects, walls, units)
            _placedUnits.Clear();
            MapData.LoadEnvDefs(GamePaths.Resolve("data/env_defs.json"), _envSystem);
            MapData.Load(mapPath, _groundSystem, _envSystem, _wallSystem, _placedUnits);

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
                    var def = new GrassTypeDef
                    {
                        Id = t.Id, Name = t.Name,
                        Scale = t.Scale > 0f ? t.Scale : 1f,
                        Density = t.Density > 0f ? t.Density : 1f,
                        DefaultTint   = new Color(t.DefR, t.DefG, t.DefB, t.DefA),
                        CorruptedTint = new Color(t.CorR, t.CorG, t.CorB, t.CorA),
                    };
                    if (t.SpritePaths != null)
                        foreach (var p in t.SpritePaths) def.SpritePaths.Add(p);
                    _grassTypes.Add(def);
                }
            }

            // Load triggers
            MapSidecars.LoadTriggers(triggerPath, _triggerSystem);

            // Load roads
            MapSidecars.LoadRoads(roadsPath, _roadSystem);

            // Load zones. Explicit Clear first: LoadZones no-ops on a missing file,
            // and stale zones must not survive a map switch.
            _zoneSystem.Clear();
            SelectedZoneIndex = -1;
            MapSidecars.LoadZones(zonesPath, _zoneSystem);

            // Reload env textures
            _envSystem.LoadTextures(_device);

            // Notify listeners. Load swaps the grass map array reference and may
            // bring new grass types, so it needs the full type resync.
            _onVertexMapChanged?.Invoke();
            _onGrassTypesChanged?.Invoke();

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

    // (Sidecar savers + WriteCondition/WriteEffect moved to Data/MapSidecars —
    // reader and writer now live together, fixing the shape/junction round-trip drift.)

    // ====================================================================
    //  BRUSH SYSTEM (shared)
    // ====================================================================

    /// <summary>Shared "-  Label: N  +" integer stepper row. Draws the label + a
    /// left minus / right plus button spanning the panel, applies a click (inert while
    /// an overlay owns input so a dropdown drawn over the steppers doesn't nudge the
    /// value), and returns the (clamped) new value. Callers own the value binding.</summary>
    private int DrawStepperRow(string label, int value, int panelX, int y, int min = 0, int max = 20)
    {
        var mouse = _eb._input.Mouse;

        DrawSmallText($"{label}: {value}", panelX + Margin + 40, y + 3, TextColor);

        var minusRect = new Rectangle(panelX + Margin, y, 30, ButtonHeight);
        Scope.Draw(_pixel, minusRect, IsInRect(mouse, minusRect) ? ButtonHoverColor : ButtonBg);
        DrawTextCentered("-", minusRect, TextColor);

        var plusRect = new Rectangle(panelX + PanelWidth - Margin - 30, y, 30, ButtonHeight);
        Scope.Draw(_pixel, plusRect, IsInRect(mouse, plusRect) ? ButtonHoverColor : ButtonBg);
        DrawTextCentered("+", plusRect, TextColor);

        if (!IsAnyPopupBlocking() && _eb._input.LeftPressed)
        {
            if (IsInRect(mouse, minusRect)) value = Math.Max(min, value - 1);
            if (IsInRect(mouse, plusRect)) value = Math.Min(max, value + 1);
        }
        return value;
    }

    private void DrawBrushSizeControl(int panelX, int y)
        => BrushRadius = DrawStepperRow("Brush", BrushRadius, panelX, y);

    private void DrawAutoGroundSizeControl(AutoGroundSettings s, int panelX, int y)
        => s.Size = DrawStepperRow("Size", s.Size, panelX, y);

    /// <summary>
    /// Shared Auto-Ground controls block: an "Auto Ground" checkbox that, when on,
    /// reveals a size stepper, a ground-type dropdown, and a noise-tiles field —
    /// editing <paramref name="s"/> in place and advancing <paramref name="contentY"/>.
    /// Used by both the Objects tab (one global instance) and each ProcGen pool.
    /// <paramref name="idSuffix"/> must be unique per caller so text/combo focus ids
    /// don't collide (e.g. "obj", "pg_0_large"). Height must match
    /// <see cref="AutoGroundControlsHeight"/>.
    /// </summary>
    private void DrawAutoGroundControls(AutoGroundSettings s, string idSuffix, int panelX, ref int contentY)
    {
        var eb = _eb!;
        s.Enabled = eb.DrawCheckbox("Auto Ground", s.Enabled, panelX + Margin, contentY);
        contentY += LineHeight + 2;
        if (s.Enabled)
        {
            // Size: patch radius in ground tiles (+/- stepper).
            DrawAutoGroundSizeControl(s, panelX, contentY);
            contentY += ButtonHeight + 2;

            // Ground type dropdown (stored by name).
            int gtCount = _groundSystem.TypeCount;
            var gtNames = new string[gtCount];
            for (int i = 0; i < gtCount; i++) gtNames[i] = _groundSystem.GetTypeDef(i).Name;
            // Default to "Dirt" (else the first type) when unset or stale — placing
            // dirt under trees is the common case.
            if (ResolveGroundTypeIndex(s.TypeName) < 0 && gtCount > 0)
                s.TypeName = DefaultAutoGroundTypeName(gtNames);
            string picked = eb.DrawCombo($"auto_ground_type_{idSuffix}", "Ground", s.TypeName, gtNames,
                panelX + Margin, contentY, PanelWidth - Margin * 2);
            if (picked != s.TypeName) s.TypeName = picked;
            contentY += FieldHeight + 2;

            // Noise tiles: ragged-edge tiles grown off the patch.
            s.Noise = eb.DrawIntField($"auto_ground_noise_{idSuffix}", "Noise Tiles", s.Noise,
                panelX + Margin, contentY, PanelWidth - Margin * 2);
            s.Noise = Math.Clamp(s.Noise, 0, 500);
            contentY += FieldHeight + 2;
        }
        contentY += 4;
    }

    /// <summary>Pick the default auto-ground type name: "Dirt" if present, else the
    /// first type. <paramref name="gtNames"/> is assumed non-empty.</summary>
    private string DefaultAutoGroundTypeName(string[] gtNames)
    {
        for (int i = 0; i < _groundSystem.TypeCount; i++)
        {
            var d = _groundSystem.GetTypeDef(i);
            if (string.Equals(d.Id, "dirt", StringComparison.OrdinalIgnoreCase)
                || string.Equals(d.Name, "Dirt", StringComparison.OrdinalIgnoreCase))
                return d.Name;
        }
        return gtNames[0];
    }

    private void DrawBrushCursor(int screenW, int screenH)
    {
        var mouse = _eb._input.Mouse;
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
                    Scope.Draw(_pixel, new Rectangle(rx, ry, rw, rh), BrushCursorFill);
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
        // Delegates to the shared EditorBase section-header bar (consolidation B7:
        // one header look across all editors). The local draw below is only a
        // degraded fallback for the no-EditorBase case.
        if (_eb != null) { _eb.DrawSectionHeader(text, panelX, ref y, PanelWidth); return; }
        Scope.Draw(_pixel, new Rectangle(panelX, y, PanelWidth, 22), HeaderBg);
        DrawSmallText(text, panelX + Margin, y + 3, TextBright);
        y += 24;
    }

    private void DrawSmallText(string text, int x, int y, Color color)
    {
        var font = _smallFont ?? _font;
        if (font != null)
            Scope.DrawString(font, text, new Vector2(x, y), color);
    }

    private void DrawTextCentered(string text, Rectangle rect, Color color)
    {
        var font = _smallFont ?? _font;
        if (font == null) return;
        var size = font.MeasureString(text);
        Scope.DrawString(font, text,
            new Vector2((int)(rect.X + (rect.Width - size.X) / 2f), (int)(rect.Y + (rect.Height - size.Y) / 2f)), color);
    }

    private void DrawButtonRect(string text, int x, int y, int w, int h, Color bg)
    {
        var mouse = _eb._input.Mouse;
        var rect = new Rectangle(x, y, w, h);
        bool hovered = IsInRect(mouse, rect);
        Scope.Draw(_pixel, rect, hovered ? ButtonHoverColor : bg);
        DrawTextCentered(text, rect, TextColor);
    }

    private void DrawRectBorder(int x, int y, int w, int h, Color color)
    {
        Necroking.Render.DrawUtils.DrawRectBorder(_spriteBatch, _pixel, new Rectangle(x, y, w, h), color);
    }

    private void DrawLine(Vector2 a, Vector2 b, Color color)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 1f) return;

        float angle = MathF.Atan2(dy, dx);
        Scope.Draw(_pixel, new Rectangle((int)a.X, (int)a.Y, (int)len, 1),
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

    /// <summary>Hit-test all drag handles on a region. Returns the matched handle, or None.</summary>
    private static RegionHandle HitTestRegionHandles(TriggerRegion region, Vec2 worldPos, float handleTol)
    {
        if (region.Shape == RegionShape.Rectangle)
            return HitTestRectHandles(region.X, region.Y, region.HalfW, region.HalfH, worldPos, handleTol);

        // Circle
        float dist = (worldPos - new Vec2(region.X, region.Y)).Length();
        if (MathF.Abs(dist - region.Radius) < handleTol) return RegionHandle.CircleRadius;
        if (dist <= region.Radius) return RegionHandle.Body;
        return RegionHandle.None;
    }

    /// <summary>Hit-test the 8 resize handles + body of a center/half-extent rectangle.
    /// Shared by trigger regions and zones.</summary>
    private static RegionHandle HitTestRectHandles(float cx, float cy, float halfW, float halfH,
        Vec2 worldPos, float handleTol)
    {
        float l = cx - halfW;
        float r = cx + halfW;
        float t = cy - halfH;
        float b = cy + halfH;

        // Test corners first (smallest targets)
        if (Vec2Near(worldPos, new Vec2(l, t), handleTol)) return RegionHandle.NW;
        if (Vec2Near(worldPos, new Vec2(r, t), handleTol)) return RegionHandle.NE;
        if (Vec2Near(worldPos, new Vec2(r, b), handleTol)) return RegionHandle.SE;
        if (Vec2Near(worldPos, new Vec2(l, b), handleTol)) return RegionHandle.SW;
        // Edge midpoints
        if (Vec2Near(worldPos, new Vec2(cx, t), handleTol)) return RegionHandle.N;
        if (Vec2Near(worldPos, new Vec2(r, cy), handleTol)) return RegionHandle.E;
        if (Vec2Near(worldPos, new Vec2(cx, b), handleTol)) return RegionHandle.S;
        if (Vec2Near(worldPos, new Vec2(l, cy), handleTol)) return RegionHandle.W;
        // Body (inside rect)
        if (worldPos.X >= l && worldPos.X <= r && worldPos.Y >= t && worldPos.Y <= b)
            return RegionHandle.Body;
        return RegionHandle.None;
    }

    /// <summary>Apply an edge/corner resize drag to a center/half-extent rectangle. The
    /// dragged side follows the cursor while the opposite side stays put; each axis is
    /// clamped to a 1-unit minimum extent. Shared by trigger regions and zones (Body and
    /// CircleRadius are the callers' business).</summary>
    private static void ApplyRectHandleDrag(RegionHandle handle, Vec2 worldPos,
        ref float x, ref float y, ref float halfW, ref float halfH)
    {
        bool north = handle is RegionHandle.N or RegionHandle.NW or RegionHandle.NE;
        bool south = handle is RegionHandle.S or RegionHandle.SW or RegionHandle.SE;
        bool west = handle is RegionHandle.W or RegionHandle.NW or RegionHandle.SW;
        bool east = handle is RegionHandle.E or RegionHandle.NE or RegionHandle.SE;

        if (north)
        {
            float oldBottom = y + halfH;
            float newTop = worldPos.Y;
            if (newTop < oldBottom - 1f)
            {
                y = (newTop + oldBottom) / 2f;
                halfH = (oldBottom - newTop) / 2f;
            }
        }
        else if (south)
        {
            float oldTop = y - halfH;
            float newBottom = worldPos.Y;
            if (newBottom > oldTop + 1f)
            {
                y = (oldTop + newBottom) / 2f;
                halfH = (newBottom - oldTop) / 2f;
            }
        }

        if (west)
        {
            float oldRight = x + halfW;
            float newLeft = worldPos.X;
            if (newLeft < oldRight - 1f)
            {
                x = (newLeft + oldRight) / 2f;
                halfW = (oldRight - newLeft) / 2f;
            }
        }
        else if (east)
        {
            float oldLeft = x - halfW;
            float newRight = worldPos.X;
            if (newRight > oldLeft + 1f)
            {
                x = (oldLeft + newRight) / 2f;
                halfW = (newRight - oldLeft) / 2f;
            }
        }
    }

    // ---- Environment helpers ----

    private static readonly Random _placementRng = new();
    private float GetRandomPlacementScale(int defIndex)
    {
        if (defIndex < 0 || defIndex >= _envSystem.DefCount) return 1f;
        var def = _envSystem.GetDef(defIndex);
        if (def.ScaleMin >= def.ScaleMax) return def.ScaleMin;
        return def.ScaleMin + (float)_placementRng.NextDouble() * (def.ScaleMax - def.ScaleMin);
    }

    private List<string> GetEnvCategories()
    {
        var list = new List<string> { "All" };
        foreach (var c in _envSystem.DistinctCategories())
            if (c != "All") list.Add(c);
        // M17: Add "Groups" option if any defs have a group
        if (_envSystem.DistinctGroups().Count > 0)
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
    private List<string> GetEnvGroups() => _envSystem.DistinctGroups();


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

    // Canonical WorldQuery scan with the match-all filter — editor removal must
    // see every placed object regardless of runtime state.
    private int FindClosestObject(Vec2 worldPos, float maxDist)
        => _game._sim.Query.NearestEnvObject(worldPos, maxDist, new GameSystems.EnvAny());
}
