using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.Core;
using Necroking.Render;
using Necroking.Movement;
using Necroking.Game;
using Necroking.GameSystems;
using Necroking.World;
using Necroking.Scenario;
using Necroking.Editor;
using Necroking.UI;

namespace Necroking;

public enum MenuState { MainMenu, None, PauseMenu, Settings, UnitEditor, SpellEditor, MapEditor, UIEditor, ItemEditor, ScenarioList }

public class Game1 : Microsoft.Xna.Framework.Game
{
    private const int WorldSize = 64; // start small, load map for real size

    private GraphicsDeviceManager _graphics;
    private SpriteBatch _spriteBatch = null!;
    private Texture2D _pixel = null!;
    private Texture2D _glowTex = null!;
    private Texture2D? _mainMenuBg;
    private SpriteFont? _font;
    private SpriteFont? _smallFont;
    private SpriteFont? _largeFont;
    private ShadowRenderer _shadowRenderer = new();
    private HUDRenderer _hudRenderer = new();

    // Data
    private GameData _gameData = new();

    // Simulation
    private Simulation _sim = new();
    private Inventory _inventory = null!;
    private Render.FontManager _fontManager = new();
    private RuntimeWidgetRenderer _widgetRenderer = new();
    private Game.InventoryUI _inventoryUI = new();
    private Game.BuildingMenuUI _buildingMenuUI = new();
    private CraftingMenuUI _craftingMenu = new();
    private System.Diagnostics.Stopwatch? _startupTimer;
    private long _startupLastMs;
    private void LogTiming(string step)
    {
        if (_startupTimer == null) return;
        long now = _startupTimer.ElapsedMilliseconds;
        DebugLog.Log("startup", $"  [{now}ms +{now - _startupLastMs}ms] {step}");
        _startupLastMs = now;
    }
    private GroundSystem _groundSystem = new();
    private EnvironmentSystem _envSystem = new();
    private WallSystem _wallSystem = new();
    private RoadSystem _roadSystem = new();
    private TriggerSystem _triggerSystem = new();

    // Grass
    private byte[] _grassMap = Array.Empty<byte>();
    private int _grassW, _grassH;
    private Color[] _grassBaseColors = Array.Empty<Color>();
    private Color[] _grassTipColors = Array.Empty<Color>();
    private string[] _grassTypeIds = Array.Empty<string>();
    private string[] _grassTypeNames = Array.Empty<string>();
    private GrassRenderer _grassRenderer = new();

    // Rendering
    private Renderer _renderer = new();
    private Camera25D _camera = new();
    private SpriteAtlas[] _atlases = new SpriteAtlas[(int)AtlasID.Count];
    private Dictionary<uint, UnitAnimData> _unitAnims = new(); // keyed by stable unit ID
    private Dictionary<int, UnitAnimData> _corpseAnims = new(); // keyed by corpse ID
    private Dictionary<string, Flipbook> _flipbooks = new(); // keyed by flipbook ID
    private Dictionary<string, AnimationMeta> _animMeta = new(); // animation metadata
    private Microsoft.Xna.Framework.Graphics.Effect? _groundEffect;
    private Microsoft.Xna.Framework.Graphics.Effect? _outlineFlatEffect;
    private Texture2D? _groundVertexMapTex;
    private EffectManager _effectManager = new();
    private BloomRenderer _bloom = new();
    private WeatherRenderer _weatherRenderer = new();
    private LightningRenderer _lightningRenderer = new();
    private DebugDraw _debugDraw = new();
    private List<DamageNumber> _damageNumbers = new();

    // Game state
    private MenuState _menuState = MenuState.MainMenu;
    private bool _gameWorldLoaded;
    private bool _paused;
    private bool _gameOver;
    private float _gameTime;
    private float _timeScale = 1f;
    private PendingSpellCast _pendingSpell = new();
    private SpellBarState _spellBarState = new();
    private SpellBarState _secondaryBarState = new();
    private int _spellDropdownSlot = -1;
    private int _secondaryDropdownSlot = -1;
    private int _channelingSlot = -1;
    private string[] _potionSlots = new string[2] { "", "" };
    private int _activePotionSlot = -1;
    private int _potionDropdownSlot = -1;
    private readonly Dictionary<string, Texture2D?> _itemTextureCache = new();
    private float _spellDropdownScroll;
    private int _hoveredObjectIdx = -1;
    private int _prevScrollValue;
    private KeyboardState _prevKb;
    private MouseState _prevMouse;
    private float _rawDt;
    private bool _mouseOverUI;
    private float _editorPanTime; // ramp-up timer for editor camera panning

    // Pending projectiles (multi-projectile delay)
    private struct PendingProjectileGroup
    {
        public string SpellID;
        public Vec2 Origin;
        public Vec2 Target;
        public int Remaining;
        public float Timer;
        public float Interval;
    }
    private readonly List<PendingProjectileGroup> _pendingProjectiles = new();

    // Editors
    private MapEditorWindow _mapEditor = new();
    private UIEditorWindow _uiEditor = new();
    private EditorBase _editorUi = new();
    private UnitEditorWindow _unitEditor = null!;
    private SpellEditorWindow _spellEditor = null!;
    private ItemEditorWindow _itemEditor = null!;
    private SettingsWindow _settingsWindow = null!;

    // Random
    private readonly Random _rng = new();

    // Collision debug
    private CollisionDebugMode _collisionDebugMode = CollisionDebugMode.Off;

    // Gameplay debug (F7): 0=Off, 1=Horde, 2=Unit Info
    private int _gameplayDebugMode;

    // Scenario state
    private ScenarioBase? _activeScenario;
    private int _scenarioScrollOffset;

    // Per-unit animation data
    internal struct UnitAnimData
    {
        public AnimController Ctrl;
        public AtlasID AtlasID;
        public float RefFrameHeight;
        public string CachedDefID;
    }

    private struct CollectingForagable
    {
        public int ObjIdx;           // environment object index
        public Vec2 StartPos;        // world position where object was
        public float StartHeight;    // initial upward pop height
        public Vec2 TargetPos;       // necromancer position at time of collection
        public float Timer;          // 0..ArcDuration
        public float ArcDuration;    // total flight time
        public string ResourceType;  // what to add to inventory on complete
        public Texture2D? Texture;   // cached texture for rendering
        public float BaseScale;      // original render scale
        public float PivotX, PivotY; // texture pivot
    }

    private readonly List<CollectingForagable> _collectingForagables = new();
    private const float ForagableArcDuration = 0.35f;
    private const float ForagableWiggleRange = 3f;     // start wiggling at this distance
    private const float ForagableAutoPickupRange = 1.5f;
    private float _autoPickupCooldown;
    private SoundEffect? _pickupSound;

    private struct DamageNumber
    {
        public Vec2 WorldPos;
        public int Damage;
        public float Timer;
        public string? PickupText; // non-null = this is a pickup notification, not damage
        public float Height;
        public bool IsPoison;
    }

    public Game1()
    {
        _graphics = new GraphicsDeviceManager(this);
        _graphics.GraphicsProfile = GraphicsProfile.HiDef;

        if (LaunchArgs.ResolutionW > 0 && LaunchArgs.ResolutionH > 0)
        {
            // Windowed mode at specified resolution
            _graphics.PreferredBackBufferWidth = LaunchArgs.ResolutionW;
            _graphics.PreferredBackBufferHeight = LaunchArgs.ResolutionH;
            _graphics.IsFullScreen = false;
        }
        else
        {
            // Windowed borderless "fullscreen" with +1 height trick:
            // Prevents OS from treating it as exclusive fullscreen, which breaks screenshots
            var adapter = GraphicsAdapter.DefaultAdapter;
            _graphics.PreferredBackBufferWidth = adapter.CurrentDisplayMode.Width;
            _graphics.PreferredBackBufferHeight = adapter.CurrentDisplayMode.Height + 1;
            _graphics.IsFullScreen = false;
            Window.IsBorderless = true;
        }

        Content.RootDirectory = "resources";
        IsMouseVisible = true;

        // Save settings (to local bin/settings/) and weather presets when the game exits
        Exiting += (_, _) =>
        {
            string localDir = GamePaths.Resolve(GamePaths.LocalSettingsDir);
            System.IO.Directory.CreateDirectory(localDir);
            _gameData.Settings.Save(GamePaths.Resolve(GamePaths.LocalSettingsJson));
            _gameData.Weather.Save(GamePaths.Resolve(GamePaths.WeatherJson));
        };

        if (LaunchArgs.Headless)
        {
            // Hide window for headless mode — use larger resolution if user specifies one
            Window.IsBorderless = true;
            _graphics.PreferredBackBufferWidth = LaunchArgs.ResolutionW > 0 ? LaunchArgs.ResolutionW : 320;
            _graphics.PreferredBackBufferHeight = LaunchArgs.ResolutionH > 0 ? LaunchArgs.ResolutionH : 240;
            _graphics.IsFullScreen = false;
        }
    }

    protected override void Initialize()
    {
        DebugLog.Clear("startup");
        DebugLog.Log("startup", "=== Necroking MG Startup ===");

        // Register AI archetypes
        AI.ArchetypeRegistry.Register(AI.ArchetypeRegistry.WolfPack, "WolfPack", new AI.WolfPackHandler());
        AI.ArchetypeRegistry.Register(AI.ArchetypeRegistry.DeerHerd, "DeerHerd", new AI.DeerHerdHandler());
        AI.ArchetypeRegistry.Register(AI.ArchetypeRegistry.HordeMinion, "HordeMinion", new AI.HordeMinionHandler());
        AI.ArchetypeRegistry.Register(AI.ArchetypeRegistry.PatrolSoldier, "PatrolSoldier",
            new AI.CombatUnitHandler(AI.ArchetypeRegistry.PatrolSoldier));
        AI.ArchetypeRegistry.Register(AI.ArchetypeRegistry.GuardStationary, "GuardStationary",
            new AI.CombatUnitHandler(AI.ArchetypeRegistry.GuardStationary));
        AI.ArchetypeRegistry.Register(AI.ArchetypeRegistry.ArmyUnit, "ArmyUnit",
            new AI.CombatUnitHandler(AI.ArchetypeRegistry.ArmyUnit));
        AI.ArchetypeRegistry.Register(AI.ArchetypeRegistry.ArcherUnit, "ArcherUnit",
            new AI.RangedUnitHandler(AI.ArchetypeRegistry.ArcherUnit));
        AI.ArchetypeRegistry.Register(AI.ArchetypeRegistry.CasterUnit, "CasterUnit",
            new AI.RangedUnitHandler(AI.ArchetypeRegistry.CasterUnit));
        _startupTimer = System.Diagnostics.Stopwatch.StartNew();
        _startupLastMs = 0;

        if (LaunchArgs.Headless)
            Window.Position = new Point(-10000, -10000);
        else if (LaunchArgs.ResolutionW <= 0 && LaunchArgs.ResolutionH <= 0)
            Window.Position = new Point(0, 0);

        // Load game data
        _gameData.Load();
        _inventory = new Inventory(20, _gameData.Items);
        LogTiming($"GameData loaded: {_gameData.Units.Count} units, {_gameData.Spells.Count} spells, {_gameData.Weapons.Count} weapons, {_gameData.Items.Count} items");

        // Init renderer
        _renderer.Init(_graphics.PreferredBackBufferWidth, _graphics.PreferredBackBufferHeight);

        // Scan for sprite sheets and load atlases (parallel file read + sequential GPU upload)
        AtlasDefs.ScanSpritesDirectory();
        int atlasCount = AtlasDefs.TotalCount;
        _atlases = new SpriteAtlas[atlasCount];

        // Phase 1: Read PNG files, decode to pixels, and parse metadata in parallel (no GPU)
        var pngBytes = new byte[atlasCount][];
        var decodedPixels = new Color[atlasCount][];
        var decodedW = new int[atlasCount];
        var decodedH = new int[atlasCount];
        var metaParsed = new bool[atlasCount];
        for (int i = 0; i < atlasCount; i++)
            _atlases[i] = new SpriteAtlas();

        System.Threading.Tasks.Parallel.For(0, atlasCount, i =>
        {
            string name = AtlasDefs.Names[i];
            string pngPath = GamePaths.Resolve($"assets/Sprites/{name}.png");
            string metaPath = GamePaths.Resolve($"assets/Sprites/{name}.spritemeta");
            if (File.Exists(pngPath))
            {
                pngBytes[i] = File.ReadAllBytes(pngPath);
                // Decode PNG to raw pixels on background thread (CPU-heavy part)
                var (pixels, w, h) = TextureUtil.DecodePngPremultiplied(pngBytes[i]);
                decodedPixels[i] = pixels;
                decodedW[i] = w;
                decodedH[i] = h;
            }
            if (File.Exists(metaPath))
                metaParsed[i] = _atlases[i].ParseMetaOnly(metaPath);
        });
        LogTiming("Atlas PNG decode + metadata parsed (parallel)");

        // Phase 2: Upload decoded pixels to GPU (fast — just SetData, no PNG decode)
        for (int i = 0; i < atlasCount; i++)
        {
            if (decodedPixels[i] != null && metaParsed[i])
            {
                var tex = TextureUtil.CreateTextureFromPixels(GraphicsDevice,
                    decodedPixels[i], decodedW[i], decodedH[i]);
                _atlases[i].SetTextureAndFinalize(tex, decodedW[i], decodedH[i]);
                decodedPixels[i] = null!; // free memory
            }
        }
        LogTiming($"Atlases GPU upload: {atlasCount} ({string.Join(", ", AtlasDefs.Names)})");

        base.Initialize();
    }

    private void StartGame()
    {
        _gameWorldLoaded = false;
        _gameOver = false;
        _damageNumbers.Clear();
        _unitAnims.Clear();
        _corpseAnims.Clear();
        _effectManager.Clear();
        _pendingProjectiles.Clear();

        // Clear world systems for clean reload (prevents doubling on second play)
        _envSystem.OnCollisionsDirty = null;
        _envSystem.ClearObjects();
        _envSystem.ClearDefs();
        _groundSystem.ClearTypes();
        _roadSystem.Init();

        // Load flipbooks
        foreach (var fbId in _gameData.Flipbooks.GetIDs())
        {
            var fbDef = _gameData.Flipbooks.Get(fbId);
            if (fbDef == null || string.IsNullOrEmpty(fbDef.Path)) continue;
            if (!File.Exists(fbDef.Path)) continue;
            var fb = new Flipbook();
            if (fb.Load(GraphicsDevice, fbDef.Path, fbDef.Cols, fbDef.Rows, fbDef.DefaultFPS))
                _flipbooks[fbId] = fb;
        }
        LogTiming($"Flipbooks loaded: {_flipbooks.Count}");

        // Load animation metadata from all atlas spritemeta files
        foreach (string name in AtlasDefs.Names)
        {
            string metaPath = GamePaths.Resolve($"assets/Sprites/{name}.animationmeta");
            if (File.Exists(metaPath))
                AnimMetaLoader.Load(metaPath, _animMeta);
        }
        LogTiming($"Animation metadata: {_animMeta.Count} entries");

        // Load animations.json timing overrides (stub)
        if (File.Exists(GamePaths.Resolve(GamePaths.AnimationsJson)))
            DebugLog.Log("startup", "animations.json found");

        // weapon_points.json is now loaded by GameData.Load() into each UnitDef.WeaponPoints

        // Load map
        var placedUnits = new List<Data.PlacedUnit>();
        string mapPath = GamePaths.Resolve(GamePaths.DefaultMapJson);
        if (File.Exists(mapPath))
        {
            DebugLog.Log("startup", "Loading map from file...");
            // Load env defs from canonical location (before map, so placed objects can resolve IDs)
            MapData.LoadEnvDefs(GamePaths.Resolve(GamePaths.EnvDefsJson), _envSystem);
            MapData.Load(mapPath, _groundSystem, _envSystem, _wallSystem, placedUnits);
            MapData.LoadTriggers(GamePaths.Resolve("data/maps/default_triggers.json"), _triggerSystem);
            MapData.LoadRoads(GamePaths.Resolve("data/maps/default_roads.json"), _roadSystem);
            LogTiming($"Map loaded: ground={_groundSystem.WorldW}x{_groundSystem.WorldH}, objects={_envSystem.ObjectCount}, defs={_envSystem.DefCount}");

            // Load grass map
            try
            {
                string mapJson = File.ReadAllText(mapPath);
                using var mapDoc = System.Text.Json.JsonDocument.Parse(mapJson);
                var grassInfo = MapData.LoadGrass(mapDoc.RootElement);
                if (grassInfo.HasValue)
                {
                    var gi = grassInfo.Value;
                    _grassW = gi.Width;
                    _grassH = gi.Height;
                    _grassMap = gi.Cells;
                    _grassBaseColors = new Color[gi.Types.Length];
                    _grassTipColors = new Color[gi.Types.Length];
                    _grassTypeIds = new string[gi.Types.Length];
                    _grassTypeNames = new string[gi.Types.Length];
                    for (int i = 0; i < gi.Types.Length; i++)
                    {
                        _grassBaseColors[i] = new Color(gi.Types[i].BaseR, gi.Types[i].BaseG, gi.Types[i].BaseB);
                        _grassTipColors[i] = new Color(gi.Types[i].TipR, gi.Types[i].TipG, gi.Types[i].TipB);
                        _grassTypeIds[i] = gi.Types[i].Id ?? $"grass_{i}";
                        _grassTypeNames[i] = gi.Types[i].Name ?? $"Grass {i}";
                    }
                    DebugLog.Log("startup", $"Grass map: {_grassW}x{_grassH}, {gi.Types.Length} types");
                }
            }
            catch (Exception ex) { DebugLog.Log("startup", $"Grass load error: {ex.Message}"); }
        }
        else
        {
            _groundSystem.Init(WorldSize, WorldSize);
            DebugLog.Log("startup", "No map file found, using empty grid");
        }

        int worldW = _groundSystem.WorldW > 0 ? _groundSystem.WorldW : WorldSize;
        int worldH = _groundSystem.WorldH > 0 ? _groundSystem.WorldH : WorldSize;
        _wallSystem.Init(worldW, worldH, worldW);

        // Load textures
        _groundSystem.LoadTextures(GraphicsDevice);
        _groundVertexMapTex = _groundSystem.CreateVertexMapTexture(GraphicsDevice);
        _envSystem.LoadTextures(GraphicsDevice);
        LogTiming($"Ground textures: {_groundSystem.TypeCount}, Env textures: {_envSystem.DefCount}, VertexMap: {(_groundVertexMapTex != null ? "OK" : "NONE")}");

        // Init simulation with map size
        _sim.Init(worldW, worldH, _gameData);
        _sim.SetEnvironmentSystem(_envSystem);
        _sim.SetWallSystem(_wallSystem);
        _sim.SetTriggerSystem(_triggerSystem);

        // Wire collision change callback so pathfinding rebuilds when objects change state
        _envSystem.OnCollisionsDirty = () => _sim.RebuildPathfinder();

        // Bake wall and environment object collisions into the tile grid cost field
        _wallSystem.BakeWalls(_sim.Grid);
        _envSystem.BakeCollisions(_sim.Grid);
        LogTiming($"Baked collisions: {_envSystem.ObjectCount} objects, grid {worldW}x{worldH}");

        // Spawn placed units from map data
        float center = worldW * 0.5f;
        foreach (var pu in placedUnits)
        {
            SpawnUnit(pu.UnitDefId, new Vec2(pu.X, pu.Y));
            int lastIdx = _sim.Units.Count - 1;
            if (!string.IsNullOrEmpty(pu.Faction))
            {
                _sim.UnitsMut[lastIdx].Faction = pu.Faction switch
                {
                    "Human" => Faction.Human,
                    "Animal" => Faction.Animal,
                    _ => Faction.Undead
                };
            }
            if (!string.IsNullOrEmpty(pu.PatrolRouteId))
            {
                _sim.UnitsMut[lastIdx].AI = AIBehavior.Patrol;
                for (int pri = 0; pri < _triggerSystem.PatrolRoutes.Count; pri++)
                {
                    if (_triggerSystem.PatrolRoutes[pri].Id == pu.PatrolRouteId)
                    { _sim.UnitsMut[lastIdx].PatrolRouteIdx = pri; break; }
                }
            }
        }
        if (placedUnits.Count > 0)
            LogTiming($"Spawned {placedUnits.Count} placed units");

        // Always ensure necromancer exists
        if (_sim.NecromancerIndex < 0)
        {
            SpawnUnit("necromancer", new Vec2(center, center));
            DebugLog.Log("startup", "No necromancer in placed units, spawned default at map center");
        }

        _camera.Position = _sim.NecromancerIndex >= 0
            ? _sim.Units[_sim.NecromancerIndex].Position : new Vec2(center, center);
        _camera.Zoom = 24f;

        // Pass placed units to map editor so markers are visible
        _mapEditor.SetPlacedUnits(placedUnits);

        // Load spell bar from data file
        // Load both spell bars from single JSON read
        _spellBarState.Slots = new SpellBarSlot[4];
        _secondaryBarState.Slots = new SpellBarSlot[4];
        for (int si = 0; si < 4; si++)
        {
            _spellBarState.Slots[si] = new SpellBarSlot { SpellID = "" };
            _secondaryBarState.Slots[si] = new SpellBarSlot { SpellID = "" };
        }
        try
        {
            string sbJson = File.ReadAllText(GamePaths.Resolve(GamePaths.SpellBarJson));
            using var sbDoc = System.Text.Json.JsonDocument.Parse(sbJson);
            LoadSpellBarSlots(sbDoc.RootElement, "slots", _spellBarState);
            LoadSpellBarSlots(sbDoc.RootElement, "secondary", _secondaryBarState);
        }
        catch (Exception ex)
        {
            DebugLog.Log("startup", $"Failed to load spellbar.json: {ex.Message}");
            _spellBarState.Slots = new[] {
                new SpellBarSlot { SpellID = "summon_skeleton_copy_copy" },
                new SpellBarSlot { SpellID = "summon_skeleton_copy" },
                new SpellBarSlot { SpellID = "raise_zombie" },
                new SpellBarSlot { SpellID = "" }
            };
        }

        // Init map editor with live systems
        _mapEditor.Init(
            _groundSystem, _envSystem, _triggerSystem, _camera,
            _spriteBatch, _pixel, _font, _smallFont, GraphicsDevice,
            onVertexMapChanged: () =>
            {
                _groundVertexMapTex?.Dispose();
                _groundVertexMapTex = _groundSystem.CreateVertexMapTexture(GraphicsDevice);
            },
            wallSystem: _wallSystem,
            roadSystem: _roadSystem,
            tileGrid: _sim.Grid,
            onGrassMapChanged: SyncGrassFromEditor,
            editorBase: _editorUi);
        _mapEditor.SetItemRegistry(_gameData.Items);
        _mapEditor.SetSpellRegistry(_gameData.Spells);
        _mapEditor.SetGameData(_gameData);

        // Feed grass data to map editor
        if (_grassMap.Length > 0)
        {
            var grassTypeInfos = new MapData.GrassTypeInfo[_grassBaseColors.Length];
            for (int gi = 0; gi < grassTypeInfos.Length; gi++)
            {
                grassTypeInfos[gi] = new MapData.GrassTypeInfo
                {
                    Id = _grassTypeIds != null && gi < _grassTypeIds.Length ? _grassTypeIds[gi] : $"grass_{gi}",
                    Name = _grassTypeNames != null && gi < _grassTypeNames.Length ? _grassTypeNames[gi] : $"Grass {gi}",
                    BaseR = _grassBaseColors[gi].R, BaseG = _grassBaseColors[gi].G, BaseB = _grassBaseColors[gi].B,
                    TipR = _grassTipColors[gi].R, TipG = _grassTipColors[gi].G, TipB = _grassTipColors[gi].B
                };
            }
            _mapEditor.SetGrassData(_grassMap, _grassW, _grassH, grassTypeInfos, _gameData.Settings.Grass.CellSize);
        }

        Window.Title = "Necroking";
        LogTiming("Game world loaded");
        DebugLog.Log("startup", $"=== Total startup: {_startupTimer?.ElapsedMilliseconds ?? 0}ms ===");
        _gameWorldLoaded = true;
        _menuState = MenuState.None;

        // Starting inventory
        foreach (var item in _gameData.Settings.StartingInventory)
            _inventory.AddItem(item.ItemId, item.Quantity);
    }

    /// <summary>
    /// Sync grass data from the map editor back to Game1's rendering arrays.
    /// Called by the editor whenever the grass map or grass type properties change.
    /// </summary>
    private void SyncGrassFromEditor()
    {
        // The editor may have a new/different grass map reference (e.g. after editor Load)
        var editorMap = _mapEditor.GetGrassMap();
        if (editorMap != _grassMap && editorMap.Length > 0)
        {
            _grassMap = editorMap;
            _grassW = _mapEditor.GrassW;
            _grassH = _mapEditor.GrassH;
        }

        // Sync grass type colors from editor definitions
        var types = _mapEditor.GrassTypes;
        if (types.Count > 0)
        {
            if (_grassBaseColors.Length != types.Count)
                _grassBaseColors = new Color[types.Count];
            if (_grassTipColors.Length != types.Count)
                _grassTipColors = new Color[types.Count];

            for (int i = 0; i < types.Count; i++)
            {
                _grassBaseColors[i] = new Color(types[i].BaseR, types[i].BaseG, types[i].BaseB);
                _grassTipColors[i] = new Color(types[i].TipR, types[i].TipG, types[i].TipB);
            }
        }
    }

    private void StartScenario(string scenarioName)
    {
        var scenario = ScenarioRegistry.Create(scenarioName);
        if (scenario == null)
        {
            DebugLog.Log("scenario", $"Failed to create scenario: {scenarioName}");
            return;
        }

        _activeScenario = scenario;

        // UI scenarios need a larger resolution in headless mode
        if (LaunchArgs.Headless && scenario.WantsUI && _graphics.PreferredBackBufferWidth < 800)
        {
            _graphics.PreferredBackBufferWidth = 800;
            _graphics.PreferredBackBufferHeight = 600;
            _graphics.ApplyChanges();
        }

        _gameWorldLoaded = false;
        _gameOver = false;
        _damageNumbers.Clear();
        _unitAnims.Clear();
        _corpseAnims.Clear();
        _effectManager.Clear();

        // Init simulation with a grid sized to the scenario's needs
        int gridSize = scenario.GridSize;
        _groundSystem.ClearTypes();
        _groundSystem.Init(gridSize, gridSize);
        _envSystem.Init(gridSize);
        _envSystem.OnCollisionsDirty = () => _sim.RebuildPathfinder();
        _wallSystem.Init(gridSize, gridSize, gridSize);
        // Add a default wall def so scenarios can render walls
        _wallSystem.Defs.Add(new World.WallVisualDef { Name = "Stone", Color = new Color(130, 130, 130, 255), MaxHP = 100 });
        _sim.Init(gridSize, gridSize, _gameData);
        _sim.SetEnvironmentSystem(_envSystem);
        _sim.SetWallSystem(_wallSystem);
        _sim.SetTriggerSystem(_triggerSystem);

        // Ensure spell bar state is initialized for HUD safety
        if (_spellBarState.Slots == null)
            _spellBarState.Slots = new SpellBarSlot[4] { new(), new(), new(), new() };
        if (_secondaryBarState.Slots == null)
            _secondaryBarState.Slots = new SpellBarSlot[4] { new(), new(), new(), new() };

        // Load ground data for scenarios that want it
        if (scenario.WantsGround)
        {
            _groundSystem.AddGroundType(new World.GroundTypeDef { Id = "grass", Name = "Grass", TexturePath = "assets/Environment/Ground/GroundGrass1.png" });
            _groundSystem.AddGroundType(new World.GroundTypeDef { Id = "dirt", Name = "Dirt", TexturePath = "assets/Environment/Ground/GroundDirt1.png" });
            _groundSystem.AddGroundType(new World.GroundTypeDef { Id = "cobblestone", Name = "Cobblestone", TexturePath = "assets/Environment/Ground/GroundCobblestone1.png" });
            _groundSystem.FillAll(0); // Default to grass
            _groundSystem.LoadTextures(GraphicsDevice);
            _groundVertexMapTex = _groundSystem.CreateVertexMapTexture(GraphicsDevice);
            DebugLog.Log("scenario", $"Ground setup: types={_groundSystem.TypeCount}, vertexMap={(_groundVertexMapTex != null ? "OK" : "NONE")}, effect={(_groundEffect != null ? "OK" : "NONE")}");
        }

        // Setup grass data for scenarios that want it
        if (scenario.WantsGrass)
        {
            int grassCellsPerUnit = 2; // ~0.8 cell size → ~1.25 cells per unit, round to 2
            int gw = gridSize * grassCellsPerUnit;
            int gh = gridSize * grassCellsPerUnit;
            _grassMap = new byte[gw * gh];
            _grassW = gw;
            _grassH = gh;
            // Default 3 grass types: green, dead, tall
            _grassBaseColors = new Color[] {
                new(46, 102, 20), new(100, 80, 40), new(30, 90, 30)
            };
            _grassTipColors = new Color[] {
                new(100, 166, 50), new(160, 140, 80), new(60, 180, 60)
            };
            // Fill with no grass (0)
            Array.Fill(_grassMap, (byte)0);
            scenario.GrassMap = _grassMap;
            scenario.GrassW = gw;
            scenario.GrassH = gh;
            DebugLog.Log("scenario", $"Grass setup: {gw}x{gh}, 3 types");
        }

        // Give scenario access to road system and inventory
        scenario.RoadSystem = _roadSystem;
        scenario.Inventory = _inventory;
        scenario.ItemRegistry = _gameData.Items;
        scenario.InventoryUI = _inventoryUI;

        // Init map editor with scenario systems (needed for editor screenshot scenarios)
        _mapEditor.Init(
            _groundSystem, _envSystem, _triggerSystem, _camera,
            _spriteBatch, _pixel, _font, _smallFont, GraphicsDevice,
            onVertexMapChanged: () =>
            {
                _groundVertexMapTex?.Dispose();
                _groundVertexMapTex = _groundSystem.CreateVertexMapTexture(GraphicsDevice);
            },
            wallSystem: _wallSystem,
            roadSystem: _roadSystem,
            tileGrid: _sim.Grid,
            onGrassMapChanged: SyncGrassFromEditor,
            editorBase: _editorUi);
        _mapEditor.SetItemRegistry(_gameData.Items);

        // Initialize the scenario
        scenario.OnInit(_sim);

        // Reload env textures in case the scenario added new defs
        _envSystem.LoadTextures(GraphicsDevice);

        // Wire up UnitEditorAccessor for AnimButtonTestScenario
        if (scenario is Scenario.Scenarios.AnimButtonTestScenario animTest)
            animTest.UnitEditor = new Scenario.Scenarios.UnitEditorAccessor(_unitEditor);

        // Wire up BloomRenderer reference for bloom debug
        if (scenario is Scenario.Scenarios.BloomDebugScenario bloomDbg)
            bloomDbg.BloomRef = _bloom;

        // Apply camera override
        if (scenario.HasCameraOverride)
        {
            _camera.Position = new Vec2(scenario.CameraX, scenario.CameraY);
            _camera.Zoom = scenario.CameraZoom;
        }

        // Apply weather override from scenario
        if (scenario.WeatherPreset != null)
        {
            _gameData.Settings.Weather.Enabled = true;
            _gameData.Settings.Weather.ActivePreset = scenario.WeatherPreset;
        }

        Window.Title = $"Necroking - Scenario: {scenarioName}";
        DebugLog.Log("scenario", $"Started scenario: {scenarioName}");
        _gameWorldLoaded = true;
        _menuState = MenuState.None;
    }

    private void SpawnUnit(string unitDefID, Vec2 pos)
    {
        int idx = _sim.UnitsMut.AddUnit(pos, UnitType.Dynamic);
        _sim.UnitsMut[idx].UnitDefID = unitDefID;

        var unitDef = _gameData.Units.Get(unitDefID);
        if (unitDef == null) return;

        _sim.UnitsMut[idx].SpriteScale = unitDef.SpriteScale;
        _sim.UnitsMut[idx].OrcaPriority = unitDef.OrcaPriority;
        _sim.UnitsMut[idx].Radius = unitDef.Radius;
        _sim.UnitsMut[idx].Size = unitDef.Size;

        // Build full stats from equipment
        var builtStats = _gameData.Units.BuildStats(unitDefID, _gameData.Weapons, _gameData.Armors, _gameData.Shields);
        _sim.UnitsMut[idx].Stats = builtStats;
        _sim.UnitsMut[idx].MaxSpeed = builtStats.CombatSpeed;

        // Faction
        _sim.UnitsMut[idx].Faction = unitDef.Faction switch
        {
            "Human" => Faction.Human,
            "Animal" => Faction.Animal,
            _ => Faction.Undead
        };

        // AI — use new archetype system if specified, otherwise legacy AI enum
        if (!string.IsNullOrEmpty(unitDef.Archetype))
        {
            // Resolve archetype name to ID
            byte archetypeId = unitDef.Archetype switch
            {
                "WolfPack" => AI.ArchetypeRegistry.WolfPack,
                "DeerHerd" => AI.ArchetypeRegistry.DeerHerd,
                "PatrolSoldier" => AI.ArchetypeRegistry.PatrolSoldier,
                "GuardStationary" => AI.ArchetypeRegistry.GuardStationary,
                "ArmyUnit" => AI.ArchetypeRegistry.ArmyUnit,
                "CasterUnit" => AI.ArchetypeRegistry.CasterUnit,
                "ArcherUnit" => AI.ArchetypeRegistry.ArcherUnit,
                "Civilian" => AI.ArchetypeRegistry.Civilian,
                "HordeMinion" => AI.ArchetypeRegistry.HordeMinion,
                _ => AI.ArchetypeRegistry.None
            };
            _sim.UnitsMut[idx].Archetype = archetypeId;

            // Initialize awareness config from UnitDef
            _sim.UnitsMut[idx].DetectionRange = unitDef.DetectionRange;
            _sim.UnitsMut[idx].DetectionBreakRange = unitDef.DetectionBreakRange;
            _sim.UnitsMut[idx].AlertDuration = unitDef.AlertDuration;
            _sim.UnitsMut[idx].AlertEscalateRange = unitDef.AlertEscalateRange;
            _sim.UnitsMut[idx].GroupAlertRadius = unitDef.GroupAlertRadius;

            // Call OnSpawn for the archetype handler
            var handler = AI.ArchetypeRegistry.Get(archetypeId);
            if (handler != null)
            {
                float dayCycleLength = 360f;
                float dayFraction = (_sim.GameTime % dayCycleLength) / dayCycleLength;
                var ctx = new AI.AIContext
                {
                    UnitIndex = idx, Units = _sim.UnitsMut, Dt = 0, FrameNumber = 0,
                    GameData = _gameData, Pathfinder = _sim.Pathfinder,
                    Horde = _sim.Horde, TriggerSystem = _triggerSystem,
                    GameTime = _sim.GameTime, DayTime = dayFraction, IsNight = dayFraction >= 0.5f,
                };
                handler.OnSpawn(ref ctx);
            }
        }
        else
        {
            _sim.UnitsMut[idx].AI = Enum.TryParse<AIBehavior>(unitDef.AI, out var parsedAI)
                ? parsedAI : AIBehavior.AttackClosest;
        }

        // If necromancer, record index in simulation
        if (unitDef.AI == "PlayerControlled")
        {
            _sim.SetNecromancerIndex(idx);
        }

        // Auto-enroll undead non-necromancer units in the horde (skip if archetype handles it)
        if (_sim.Units[idx].Archetype == 0 && _sim.Units[idx].Faction == Faction.Undead
            && _sim.Units[idx].AI != AIBehavior.PlayerControlled)
            _sim.Horde.AddUnit(_sim.Units[idx].Id);

        // Set up animation
        if (unitDef.Sprite != null)
        {
            var atlasId = AtlasDefs.ResolveAtlasName(unitDef.Sprite.AtlasName);
            var spriteData = _atlases[(int)atlasId].GetUnit(unitDef.Sprite.SpriteName);
            if (spriteData != null)
            {
                var ctrl = new AnimController();
                ctrl.Init(spriteData);
                ctrl.ForceState(AnimState.Idle);

                // Wire animation metadata
                if (_animMeta.Count > 0)
                    ctrl.SetAnimMeta(_animMeta, unitDef.Sprite.SpriteName);

                // Wire per-unit animation timing overrides (from unit editor)
                if (unitDef.AnimTimings.Count > 0)
                {
                    var overrides = new Dictionary<string, AnimTimingOverride>();
                    foreach (var (anim, ov) in unitDef.AnimTimings)
                        overrides[anim] = new AnimTimingOverride
                        {
                            FrameDurationsMs = new List<int>(ov.FrameDurationsMs),
                            EffectTimeMs = ov.EffectTimeMs
                        };
                    ctrl.SetAnimTimings(overrides);
                }

                float refH = 128f;
                var idleAnim = spriteData.GetAnim("Idle");
                if (idleAnim != null)
                {
                    var kfs = idleAnim.GetAngle(30);
                    if (kfs != null && kfs.Count > 0)
                        refH = kfs[0].Frame.Rect.Height;
                }

                _unitAnims[_sim.Units[idx].Id] = new UnitAnimData
                {
                    Ctrl = ctrl,
                    AtlasID = atlasId,
                    RefFrameHeight = refH,
                    CachedDefID = unitDefID
                };
            }
        }
    }

    /// <summary>
    /// Execute the full summon spell logic matching C++ Simulation::executeSpellCast for Summon category.
    /// Handles all SummonTargetReq types: None, Corpse, UnitType, CorpseAOE.
    /// Handles SummonMode: Spawn, Transform.
    /// </summary>
    private void ExecuteSummonSpell(SpellDef spell, PendingSpellCast pending, Vec2 necroPos, int necroIdx)
    {
        if (spell.SummonTargetReq == "CorpseAOE")
        {
            // AOE corpse raise: iterate corpses in range, resolve zombie type per corpse
            int raised = 0;
            for (int i = 0; i < _sim.Corpses.Count && raised < spell.SummonQuantity; i++)
            {
                var corpse = _sim.Corpses[i];
                if (corpse.Dissolving || corpse.ConsumedBySummon) continue;
                if (corpse.DraggedByUnitID != GameConstants.InvalidUnit) continue;
                float dist = (corpse.Position - pending.TargetPos).Length();
                if (dist > spell.AoeRadius) continue;

                // Resolve zombie type from corpse's UnitDef
                var corpseDef = _gameData.Units.Get(corpse.UnitDefID);
                if (corpseDef == null || string.IsNullOrEmpty(corpseDef.ZombieTypeID)) continue;

                // Resolve actual unit ID: check unit registry first, then groups
                string resolvedID;
                if (_gameData.Units.Get(corpseDef.ZombieTypeID) != null)
                    resolvedID = corpseDef.ZombieTypeID;
                else
                    resolvedID = _gameData.UnitGroups.PickRandom(corpseDef.ZombieTypeID) ?? "";
                if (string.IsNullOrEmpty(resolvedID)) continue;

                var spawnPos = corpse.Position;
                float corpseFacing = corpse.FacingAngle;
                _sim.ConsumeCorpse(i);

                SpawnUnit(resolvedID, spawnPos);
                int idx = _sim.Units.Count - 1;
                if (idx >= 0)
                {
                    _sim.UnitsMut[idx].FacingAngle = corpseFacing;
                    _sim.UnitsMut[idx].StandupTimer = 1.5f;
                    // Add to horde if undead
                    if (_sim.Units[idx].Faction == Faction.Undead &&
                        _sim.Units[idx].AI != AIBehavior.PlayerControlled)
                        _sim.Horde.AddUnit(_sim.Units[idx].Id);
                    raised++;
                }

                // Spawn summon effect at each corpse location
                SpawnSummonEffect(spell, spawnPos);
            }
        }
        else
        {
            // Single corpse consume (Corpse targeting)
            float corpseFacing = -1f; // -1 = no corpse consumed
            string summonUnitID = pending.SummonUnitID;

            if (spell.SummonTargetReq == "Corpse" && pending.TargetCorpseIdx >= 0)
            {
                // Resolve zombie type from corpse if summonUnitID is empty
                if (string.IsNullOrEmpty(summonUnitID) && pending.TargetCorpseIdx < _sim.Corpses.Count)
                {
                    var corpse = _sim.Corpses[pending.TargetCorpseIdx];
                    var corpseDef = _gameData.Units.Get(corpse.UnitDefID);
                    if (corpseDef != null && !string.IsNullOrEmpty(corpseDef.ZombieTypeID))
                    {
                        if (_gameData.Units.Get(corpseDef.ZombieTypeID) != null)
                            summonUnitID = corpseDef.ZombieTypeID;
                        else
                            summonUnitID = _gameData.UnitGroups.PickRandom(corpseDef.ZombieTypeID) ?? "";
                    }
                }
                if (pending.TargetCorpseIdx < _sim.Corpses.Count)
                {
                    corpseFacing = _sim.Corpses[pending.TargetCorpseIdx].FacingAngle;
                    _sim.ConsumeCorpse(pending.TargetCorpseIdx);
                }
            }

            if (spell.SummonMode == "Transform" && pending.TargetUnitID != GameConstants.InvalidUnit)
            {
                // Transform mode: replace existing unit with the summon unit
                int targetIdx = _sim.ResolveUnitID(pending.TargetUnitID);
                if (targetIdx >= 0 && !string.IsNullOrEmpty(summonUnitID))
                {
                    var targetPos = _sim.Units[targetIdx].Position;
                    _sim.TransformUnit(targetIdx, summonUnitID);

                    // Rebuild animation for the transformed unit
                    RebuildUnitAnim(targetIdx, summonUnitID);

                    // Spawn summon effect at target position
                    SpawnSummonEffect(spell, targetPos);
                }
            }
            else
            {
                // Spawn mode
                if (string.IsNullOrEmpty(summonUnitID)) return;

                Vec2 spawnPos;
                switch (spell.SpawnLocation)
                {
                    case "NearestTargetToMouse":
                        spawnPos = pending.TargetPos;
                        break;
                    case "NearestTargetToCaster":
                        spawnPos = pending.TargetPos;
                        break;
                    case "AdjacentToCaster":
                    {
                        float angle = _rng.Next(360) * MathF.PI / 180f;
                        spawnPos = necroPos + new Vec2(MathF.Cos(angle) * 2f, MathF.Sin(angle) * 2f);
                        break;
                    }
                    case "AtTargetLocation":
                        spawnPos = pending.TargetPos;
                        break;
                    default:
                        spawnPos = pending.TargetPos;
                        break;
                }

                for (int q = 0; q < spell.SummonQuantity; q++)
                {
                    var unitSpawnPos = spawnPos;
                    if (q > 0)
                    {
                        // Offset additional spawns slightly
                        float angle = _rng.Next(360) * MathF.PI / 180f;
                        unitSpawnPos = spawnPos + new Vec2(MathF.Cos(angle) * 1f, MathF.Sin(angle) * 1f);
                    }

                    SpawnUnit(summonUnitID, unitSpawnPos);
                    int idx = _sim.Units.Count - 1;
                    if (idx >= 0)
                    {
                        // Inherit corpse rotation for reanimated units
                        if (corpseFacing >= 0f)
                        {
                            _sim.UnitsMut[idx].FacingAngle = corpseFacing;
                            _sim.UnitsMut[idx].StandupTimer = 1.5f;
                        }
                        // Add to horde if undead
                        if (_sim.Units[idx].Faction == Faction.Undead &&
                            _sim.Units[idx].AI != AIBehavior.PlayerControlled)
                            _sim.Horde.AddUnit(_sim.Units[idx].Id);
                    }
                }

                // Spawn summon effect at the primary spawn location
                SpawnSummonEffect(spell, spawnPos);
            }
        }
    }

    /// <summary>
    /// Spawn the visual summon flipbook effect at a given position.
    /// </summary>
    private void SpawnSummonEffect(SpellDef spell, Vec2 pos)
    {
        if (spell.SummonFlipbook == null || string.IsNullOrEmpty(spell.SummonFlipbook.FlipbookID)) return;

        var fb = spell.SummonFlipbook;
        var tint = fb.Color.ToScaledColor();
        int blendMode = fb.BlendMode == "Additive" ? 1 : 0;
        int alignment = fb.Alignment == "Upright" ? 1 : 0;
        float duration = fb.Duration >= 0f ? fb.Duration : 0.4f;

        _effectManager.SpawnSpellImpact(pos, fb.Scale, tint, fb.FlipbookID,
            fb.Color.Intensity, blendMode, alignment, duration);
    }

    /// <summary>
    /// Rebuild animation data for a unit (e.g. after transform).
    /// </summary>
    private void RebuildUnitAnim(int unitIdx, string unitDefID)
    {
        var unitDef = _gameData.Units.Get(unitDefID);
        if (unitDef?.Sprite == null) return;

        var atlasId = AtlasDefs.ResolveAtlasName(unitDef.Sprite.AtlasName);
        var spriteData = _atlases[(int)atlasId].GetUnit(unitDef.Sprite.SpriteName);
        if (spriteData == null) return;

        var ctrl = new AnimController();
        ctrl.Init(spriteData);
        ctrl.ForceState(AnimState.Idle);

        if (_animMeta.Count > 0)
            ctrl.SetAnimMeta(_animMeta, unitDef.Sprite.SpriteName);

        float refH = 128f;
        var idleAnim = spriteData.GetAnim("Idle");
        if (idleAnim != null)
        {
            var kfs = idleAnim.GetAngle(30);
            if (kfs != null && kfs.Count > 0)
                refH = kfs[0].Frame.Rect.Height;
        }

        _unitAnims[_sim.Units[unitIdx].Id] = new UnitAnimData
        {
            Ctrl = ctrl,
            AtlasID = atlasId,
            RefFrameHeight = refH,
            CachedDefID = unitDefID
        };
    }

    protected override void LoadContent()
    {
        _spriteBatch = new SpriteBatch(GraphicsDevice);
        _pixel = new Texture2D(GraphicsDevice, 1, 1);
        _pixel.SetData(new[] { Color.White });

        // Create radial glow texture (64x64 with smooth quadratic falloff)
        _glowTex = new Texture2D(GraphicsDevice, 64, 64);
        var glowData = new Color[64 * 64];
        for (int gy = 0; gy < 64; gy++)
            for (int gx = 0; gx < 64; gx++)
            {
                float dx = (gx - 31.5f) / 31.5f;
                float dy = (gy - 31.5f) / 31.5f;
                float dist = MathF.Sqrt(dx * dx + dy * dy);
                float alpha = MathF.Max(0, 1f - dist);
                alpha *= alpha; // quadratic falloff for soft glow
                glowData[gy * 64 + gx] = new Color((byte)255, (byte)255, (byte)255, (byte)(alpha * 255));
            }
        _glowTex.SetData(glowData);

        LogTiming("Glow texture created");

        // Shadow renderer (BasicEffect for parallelogram quads)
        _shadowRenderer.Init(GraphicsDevice);

        // Load main menu background
        string menuBgPath = GamePaths.Resolve(Path.Combine("assets", "UI", "Background", "VampireBackground.png"));
        if (File.Exists(menuBgPath))
        {
            _mainMenuBg = TextureUtil.LoadPremultiplied(GraphicsDevice, menuBgPath);
        }

        LogTiming("Menu background loaded");

        _font = Content.Load<SpriteFont>("DefaultFont");
        _smallFont = Content.Load<SpriteFont>("SmallFont");
        _largeFont = Content.Load<SpriteFont>("LargeFont");
        _debugDraw.SetFont(_smallFont);
        _hudRenderer.Init(_spriteBatch, _pixel, _font, _smallFont);

        // Load TrueType fonts via FontStashSharp (dynamic sizing)
        _fontManager.LoadFontsFromDirectory(GamePaths.Resolve(GamePaths.FontsDir));
        if (_fontManager.HasFonts)
        {
            // Prefer "Standard" as default, fall back to first loaded
            if (_fontManager.FontFamilies.Any(f => f == "Standard"))
                _fontManager.SetDefault("Standard");
        }
        LogTiming("Fonts loaded");

        _bloom.Init(GraphicsDevice, Content,
            _graphics.PreferredBackBufferWidth, _graphics.PreferredBackBufferHeight);
        LogTiming("Bloom initialized");

        try { _groundEffect = Content.Load<Microsoft.Xna.Framework.Graphics.Effect>("GroundShader"); }
        catch (Exception ex) { _groundEffect = null; DebugLog.Log("startup", $"GroundShader not loaded: {ex.Message}"); }

        try { _outlineFlatEffect = Content.Load<Microsoft.Xna.Framework.Graphics.Effect>("OutlineFlat"); }
        catch (Exception ex) { _outlineFlatEffect = null; DebugLog.Log("startup", $"OutlineFlat not loaded: {ex.Message}"); }

        {
            Microsoft.Xna.Framework.Graphics.Effect? fogEffect = null;
            try { fogEffect = Content.Load<Microsoft.Xna.Framework.Graphics.Effect>("WeatherFog"); }
            catch (Exception ex) { DebugLog.Log("startup", $"WeatherFog not loaded: {ex.Message}"); }
            _weatherRenderer.LoadEffect(fogEffect);
        }
        _weatherRenderer.Init(_graphics.PreferredBackBufferWidth, _graphics.PreferredBackBufferHeight);

        _grassRenderer.Init(GraphicsDevice);
        _lightningRenderer.Init(_spriteBatch, _pixel, _glowTex, _sim, _camera, _renderer, GraphicsDevice);
        LogTiming("Renderers initialized (weather, grass, lightning)");

        // Init UI editor (read-only viewer, doesn't depend on game systems)
        _uiEditor.Init(_spriteBatch, _pixel, _font, _smallFont);
        _uiEditor.SetFontManager(_fontManager);
        string uiDefPath = GamePaths.Resolve(GamePaths.UIDefsDir);
        if (Directory.Exists(uiDefPath))
            _uiEditor.LoadDefinitions(uiDefPath);
        LogTiming("UI editor initialized");

        // Runtime widget renderer + inventory UI
        _widgetRenderer.Init(GraphicsDevice, _spriteBatch, _fontManager);
        if (Directory.Exists(uiDefPath))
            _widgetRenderer.LoadDefinitions(uiDefPath);
        _inventoryUI.Init(_widgetRenderer, _inventory, _gameData.Items);
        _buildingMenuUI.Init(_widgetRenderer, _envSystem, _inventory, _gameData.Items,
            _graphics.PreferredBackBufferHeight, _spriteBatch, _pixel);
        _craftingMenu.Init(_widgetRenderer, _inventory, _gameData.Items, _gameData,
            _graphics.PreferredBackBufferHeight, _spriteBatch, _pixel);
        LogTiming("Inventory & Building UI initialized");

        // Load audio
        try
        {
            string pickupPath = GamePaths.Resolve("assets/Audio/Interaction/PickupPop.wav");
            if (System.IO.File.Exists(pickupPath))
            {
                using var stream = System.IO.File.OpenRead(pickupPath);
                _pickupSound = SoundEffect.FromStream(stream);
            }
        }
        catch { /* audio is optional */ }

        // Init property editor infrastructure
        _editorUi.SetContext(_spriteBatch, _pixel, _font, _smallFont, _largeFont);
        _unitEditor = new UnitEditorWindow(_editorUi);
        _unitEditor.SetGameData(_gameData);
        _unitEditor.SetAtlases(_atlases, GraphicsDevice);
        _unitEditor.SetAnimMeta(_animMeta);
        _spellEditor = new SpellEditorWindow(_editorUi);
        _spellEditor.SetGameData(_gameData);
        _itemEditor = new ItemEditorWindow(_editorUi);
        _itemEditor.SetGameData(_gameData);
        _settingsWindow = new SettingsWindow(_editorUi);
        System.IO.Directory.CreateDirectory(GamePaths.Resolve(GamePaths.LocalSettingsDir));
        _settingsWindow.SetGameData(_gameData, GamePaths.Resolve(GamePaths.LocalSettingsJson), GamePaths.Resolve(GamePaths.WeatherJson));
        LogTiming("Editors initialized");
        DebugLog.Log("startup", $"=== LoadContent complete ===");
    }

    protected override void Update(GameTime gameTime)
    {
        var kb = Keyboard.GetState();
        var mouse = Mouse.GetState();
        _rawDt = (float)gameTime.ElapsedGameTime.TotalSeconds;
        float rawDt = _rawDt;
        float dt = MathF.Min(rawDt, 1f / 20f) * _timeScale;
        _gameTime += dt;

        // --- Auto-start scenario from command line ---
        if (_menuState == MenuState.MainMenu && LaunchArgs.Scenario != null)
        {
            string scenName = LaunchArgs.Scenario;
            LaunchArgs.Scenario = null; // Only auto-start once
            StartScenario(scenName);
            _prevKb = kb;
            _prevMouse = mouse;
            base.Update(gameTime);
            return;
        }

        // --- Main menu ---
        if (_menuState == MenuState.MainMenu)
        {
            if (mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released)
            {
                int screenW2 = GraphicsDevice.Viewport.Width;
                int screenH2 = GraphicsDevice.Viewport.Height;
                int btnW = 320, btnH = 55, btnGap = 18;
                int menuX = screenW2 / 2 - btnW / 2;
                int menuY = screenH2 / 2 + 20;

                // Play button
                if (mouse.X >= menuX && mouse.X < menuX + btnW && mouse.Y >= menuY && mouse.Y < menuY + btnH)
                {
                    StartGame();
                    _prevKb = kb;
                    _prevMouse = mouse;
                    base.Update(gameTime);
                    return;
                }
                menuY += btnH + btnGap;

                // Scenarios button
                if (mouse.X >= menuX && mouse.X < menuX + btnW && mouse.Y >= menuY && mouse.Y < menuY + btnH)
                {
                    _menuState = MenuState.ScenarioList;
                    _scenarioScrollOffset = 0;
                    _prevKb = kb;
                    _prevMouse = mouse;
                    base.Update(gameTime);
                    return;
                }
                menuY += btnH + btnGap;

                // Quit button
                if (mouse.X >= menuX && mouse.X < menuX + btnW && mouse.Y >= menuY && mouse.Y < menuY + btnH)
                    Exit();
            }
            _prevKb = kb;
            _prevMouse = mouse;
            base.Update(gameTime);
            return;
        }

        // --- Scenario list ---
        if (_menuState == MenuState.ScenarioList)
        {
            // Escape to go back
            if (WasKeyPressed(kb, Keys.Escape))
            {
                _menuState = MenuState.MainMenu;
                _prevKb = kb;
                _prevMouse = mouse;
                base.Update(gameTime);
                return;
            }

            // Scroll
            int scenScrollDelta = mouse.ScrollWheelValue - _prevScrollValue;
            _prevScrollValue = mouse.ScrollWheelValue;
            if (scenScrollDelta > 0) _scenarioScrollOffset = Math.Max(0, _scenarioScrollOffset - 1);
            if (scenScrollDelta < 0) _scenarioScrollOffset++;

            if (mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released)
            {
                int screenW2 = GraphicsDevice.Viewport.Width;
                int screenH2 = GraphicsDevice.Viewport.Height;
                int btnW = 320, btnH = 45, btnGap = 12;
                int menuX = screenW2 / 2 - btnW / 2;
                int menuY = screenH2 / 4 + 60;

                var names = new List<string>(ScenarioRegistry.GetNames());
                int visibleCount = Math.Min(names.Count - _scenarioScrollOffset, (screenH2 - menuY - 80) / (btnH + btnGap));
                for (int i = 0; i < visibleCount; i++)
                {
                    int nameIdx = i + _scenarioScrollOffset;
                    if (nameIdx >= names.Count) break;
                    if (mouse.X >= menuX && mouse.X < menuX + btnW && mouse.Y >= menuY && mouse.Y < menuY + btnH)
                    {
                        StartScenario(names[nameIdx]);
                        _prevKb = kb;
                        _prevMouse = mouse;
                        base.Update(gameTime);
                        return;
                    }
                    menuY += btnH + btnGap;
                }

                // Back button
                menuY += 10;
                if (mouse.X >= menuX && mouse.X < menuX + btnW && mouse.Y >= menuY && mouse.Y < menuY + btnH)
                    _menuState = MenuState.MainMenu;
            }
            _prevKb = kb;
            _prevMouse = mouse;
            base.Update(gameTime);
            return;
        }

        // Check if any editor text field is active (persists from previous frame, safe to read before UpdateInput)
        bool anyTextInputActive = (_editorUi != null && _editorUi.IsTextInputActive)
            || (_menuState == MenuState.UIEditor && _uiEditor.IsTextInputActive);

        // --- F7 gameplay debug toggle (Off → Horde → Unit Info → Off) ---
        if (!anyTextInputActive && WasKeyPressed(kb, Keys.F7))
            _gameplayDebugMode = (_gameplayDebugMode + 1) % 3;

        // --- F8 collision debug toggle ---
        if (!anyTextInputActive && WasKeyPressed(kb, Keys.F8))
        {
            _collisionDebugMode = (CollisionDebugMode)(((int)_collisionDebugMode + 1) % (int)CollisionDebugMode.Count);
        }

        // --- F9-F12 editor toggles ---
        if (!anyTextInputActive && WasKeyPressed(kb, Keys.F9))
        {
            _menuState = _menuState == MenuState.UnitEditor ? MenuState.None : MenuState.UnitEditor;
            _editorUi.ClearActiveField();
        }
        if (!anyTextInputActive && WasKeyPressed(kb, Keys.F10))
        {
            _menuState = _menuState == MenuState.SpellEditor ? MenuState.None : MenuState.SpellEditor;
            _editorUi.ClearActiveField();
        }
        if (!anyTextInputActive && WasKeyPressed(kb, Keys.F11))
            _menuState = _menuState == MenuState.MapEditor ? MenuState.None : MenuState.MapEditor;
        if (!anyTextInputActive && WasKeyPressed(kb, Keys.F12))
            _menuState = _menuState == MenuState.UIEditor ? MenuState.None : MenuState.UIEditor;

        // 'I' key toggles inventory
        if (!anyTextInputActive && WasKeyPressed(kb, Keys.I) && _menuState == MenuState.None)
            _inventoryUI.Toggle(GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height);

        // --- Pause menu button clicks ---
        if (_menuState == MenuState.PauseMenu && mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released)
        {
            int sw = GraphicsDevice.Viewport.Width;
            int sh = GraphicsDevice.Viewport.Height;
            int boxW2 = 350;
            int boxY2 = (sh - 450) / 2 + 60;
            int btnW2 = 280, btnH2 = 40, btnGap2 = 10;
            int menuX2 = (sw - btnW2) / 2;
            int y2 = boxY2;

            // Resume
            if (mouse.X >= menuX2 && mouse.X < menuX2 + btnW2 && mouse.Y >= y2 && mouse.Y < y2 + btnH2)
            { _menuState = MenuState.None; _paused = false; }
            y2 += btnH2 + btnGap2;
            // Unit Editor
            if (mouse.X >= menuX2 && mouse.X < menuX2 + btnW2 && mouse.Y >= y2 && mouse.Y < y2 + btnH2)
            { _menuState = MenuState.UnitEditor; _paused = false; }
            y2 += btnH2 + btnGap2;
            // Spell Editor
            if (mouse.X >= menuX2 && mouse.X < menuX2 + btnW2 && mouse.Y >= y2 && mouse.Y < y2 + btnH2)
            { _menuState = MenuState.SpellEditor; _paused = false; }
            y2 += btnH2 + btnGap2;
            // Map Editor
            if (mouse.X >= menuX2 && mouse.X < menuX2 + btnW2 && mouse.Y >= y2 && mouse.Y < y2 + btnH2)
            { _menuState = MenuState.MapEditor; _paused = false; }
            y2 += btnH2 + btnGap2;
            // UI Editor
            if (mouse.X >= menuX2 && mouse.X < menuX2 + btnW2 && mouse.Y >= y2 && mouse.Y < y2 + btnH2)
            { _menuState = MenuState.UIEditor; _paused = false; }
            y2 += btnH2 + btnGap2;
            // Item Editor
            if (mouse.X >= menuX2 && mouse.X < menuX2 + btnW2 && mouse.Y >= y2 && mouse.Y < y2 + btnH2)
            { _menuState = MenuState.ItemEditor; _paused = false; }
            y2 += btnH2 + btnGap2;
            // Settings
            if (mouse.X >= menuX2 && mouse.X < menuX2 + btnW2 && mouse.Y >= y2 && mouse.Y < y2 + btnH2)
            { _menuState = MenuState.Settings; }
            y2 += btnH2 + btnGap2 + 10;
            // Main Menu
            if (mouse.X >= menuX2 && mouse.X < menuX2 + btnW2 && mouse.Y >= y2 && mouse.Y < y2 + btnH2)
            { _menuState = MenuState.MainMenu; _paused = false; _gameWorldLoaded = false; }
            y2 += btnH2 + btnGap2;
            // Quit
            if (mouse.X >= menuX2 && mouse.X < menuX2 + btnW2 && mouse.Y >= y2 && mouse.Y < y2 + btnH2)
                Exit();
        }

        // --- Settings window close handling ---
        if (_menuState == MenuState.Settings && _settingsWindow.WantsClose)
        {
            _settingsWindow.WantsClose = false;
            _menuState = MenuState.PauseMenu;
        }

        // --- ESC toggles pause menu / closes editor ---
        // When a text field is active, Escape is consumed by EditorBase.HandleTextInput to deactivate the field.
        // When a popup (color picker, texture browser, confirm dialog) is open, don't close the editor.
        bool popupOpen = _editorUi.IsColorPickerOpen || _editorUi.IsDropdownOpen
            || (_menuState == MenuState.UIEditor && (_uiEditor.IsColorPickerOpen || _uiEditor.IsDropdownOpen));
        if (!anyTextInputActive && !popupOpen && WasKeyPressed(kb, Keys.Escape))
        {
            if (_menuState == MenuState.Settings)
            {
                _menuState = MenuState.PauseMenu;
            }
            else if (_menuState == MenuState.UnitEditor || _menuState == MenuState.SpellEditor ||
                _menuState == MenuState.MapEditor || _menuState == MenuState.UIEditor ||
                _menuState == MenuState.ItemEditor)
            {
                _menuState = MenuState.None;
            }
            else if (_menuState == MenuState.PauseMenu)
            {
                _menuState = MenuState.None;
                _paused = false;
            }
            else if (_menuState == MenuState.None && _buildingMenuUI.IsVisible)
            {
                _buildingMenuUI.Close();
            }
            else if (_menuState == MenuState.None && _craftingMenu.IsVisible)
            {
                _craftingMenu.Close();
            }
            else if (_menuState == MenuState.None && _inventoryUI.IsVisible)
            {
                _inventoryUI.Close();
            }
            else if (_menuState == MenuState.None)
            {
                _menuState = MenuState.PauseMenu;
                _paused = true;
            }
        }

        // --- Editor updates ---
        int screenW = GraphicsDevice.Viewport.Width;
        int screenH = GraphicsDevice.Viewport.Height;
        // Update EditorBase input first so _eb has current mouse/keyboard state for all editors
        if (_menuState == MenuState.UnitEditor || _menuState == MenuState.SpellEditor || _menuState == MenuState.MapEditor || _menuState == MenuState.Settings || _menuState == MenuState.ItemEditor)
            _editorUi.UpdateInput(mouse, _prevMouse, kb, _prevKb, screenW, screenH, gameTime);
        if (_menuState == MenuState.MapEditor && _gameWorldLoaded)
            _mapEditor.Update(screenW, screenH);
        if (_menuState == MenuState.Settings)
            _settingsWindow.Update(screenW, screenH, gameTime);
        if (_menuState == MenuState.UIEditor)
            _uiEditor.Update(screenW, screenH);

        // --- Camera ---
        _renderer.SetScreenSize(screenW, screenH);

        // Camera follows necromancer, or free pan in editors/scenarios
        int necroIdx = FindNecromancer();
        bool editorOpen = _menuState == MenuState.UnitEditor || _menuState == MenuState.SpellEditor
            || _menuState == MenuState.MapEditor || _menuState == MenuState.UIEditor;
        if (editorOpen)
        {
            // Editors: free camera — map editor handles its own WASD via smooth camera in MapEditorWindow.Update
            // Other editors: allow arrow key panning (suppressed when a text field is active)
            if (_menuState != MenuState.MapEditor && !anyTextInputActive)
            {
                Vec2 camMove = Vec2.Zero;
                if (kb.IsKeyDown(Keys.Up) || kb.IsKeyDown(Keys.W)) camMove.Y -= 1f;
                if (kb.IsKeyDown(Keys.Down) || kb.IsKeyDown(Keys.S)) camMove.Y += 1f;
                if (kb.IsKeyDown(Keys.Left) || kb.IsKeyDown(Keys.A)) camMove.X -= 1f;
                if (kb.IsKeyDown(Keys.Right) || kb.IsKeyDown(Keys.D)) camMove.X += 1f;
                if (camMove.LengthSq() > 0.01f)
                {
                    _editorPanTime += rawDt;
                    float ramp = 1f + 2f * MathF.Min(_editorPanTime / 2f, 1f); // 1x → 3x over 2 seconds
                    float camSpeed = 400f / MathF.Max(1f, _camera.Zoom) * ramp;
                    _camera.Position += camMove.Normalized() * camSpeed * rawDt;
                }
                else
                {
                    _editorPanTime = 0f; // reset when not panning
                }
            }
        }
        else if (necroIdx >= 0)
        {
            var necroPos = _sim.Units[necroIdx].Position;
            var diff = necroPos - _camera.Position;
            _camera.Position += diff * MathF.Min(1f, 5f * rawDt);
        }
        else if (_activeScenario != null || _menuState == MenuState.None)
        {
            // Free camera pan with arrow keys (scenarios or when no necromancer)
            float camSpeed = 400f / MathF.Max(1f, _camera.Zoom);
            Vec2 camMove = Vec2.Zero;
            if (kb.IsKeyDown(Keys.Up)) camMove.Y -= 1f;
            if (kb.IsKeyDown(Keys.Down)) camMove.Y += 1f;
            if (kb.IsKeyDown(Keys.Left)) camMove.X -= 1f;
            if (kb.IsKeyDown(Keys.Right)) camMove.X += 1f;
            if (camMove.LengthSq() > 0.01f)
                _camera.Position += camMove.Normalized() * camSpeed * rawDt;
        }

        // Scroll zoom (always active)
        int scrollDelta = mouse.ScrollWheelValue - _prevScrollValue;
        _prevScrollValue = mouse.ScrollWheelValue;
        bool scrollOverUI = _mouseOverUI
            || (_menuState == MenuState.MapEditor && _mapEditor.IsMouseOverPanel(screenW, screenH));
        if (scrollDelta != 0 && (_menuState == MenuState.None || _menuState == MenuState.MapEditor) && !scrollOverUI)
            _camera.ZoomBy(scrollDelta / 120f);

        // Editors pause the game
        bool editorActive = _menuState == MenuState.UnitEditor || _menuState == MenuState.SpellEditor
            || _menuState == MenuState.MapEditor || _menuState == MenuState.UIEditor;

        // --- IsMouseOverUI: reset each frame, then test UI elements ---
        _mouseOverUI = false;
        if (_menuState == MenuState.None)
        {
            // Primary spell bar area (4 slots, 50x50, centered bottom)
            int uiSlotW = 50, uiSlotH = 50;
            int uiSlotY = screenH - 95;
            int uiBarX = screenW / 2 - 110;
            if (mouse.X >= uiBarX && mouse.X < uiBarX + 4 * (uiSlotW + 4) && mouse.Y >= uiSlotY && mouse.Y < uiSlotY + uiSlotH)
                _mouseOverUI = true;

            // Secondary spell bar area (4 slots, 35x35, above primary)
            int secW = 35, secH = 35;
            int secY = uiSlotY - secH - 6;
            int secBarX = screenW / 2 - 80;
            if (mouse.X >= secBarX && mouse.X < secBarX + 4 * (secW + 4) && mouse.Y >= secY && mouse.Y < secY + secH)
                _mouseOverUI = true;

            // Spell dropdown open
            if (_spellDropdownSlot >= 0 || _secondaryDropdownSlot >= 0 || _potionDropdownSlot >= 0)
                _mouseOverUI = true;

            // Inventory window
            if (_inventoryUI.ContainsMouse(mouse.X, mouse.Y))
                _mouseOverUI = true;

            // Building menu
            if (_buildingMenuUI.ContainsMouse(mouse.X, mouse.Y))
                _mouseOverUI = true;
            if (_craftingMenu.ContainsMouse(mouse.X, mouse.Y))
                _mouseOverUI = true;

            // Building placement panel (left side, editor mode)
            // (handled by BuildingMenuUI.ContainsMouse above)

            // Time control buttons (bottom-right)
            if (_gameData.Settings.General.ShowTimeControls)
            {
                const int tcBtnW = 32, tcBtnH = 22, tcGap = 2, tcNum = 6;
                int tcTotalW = tcNum * tcBtnW + (tcNum - 1) * tcGap;
                int tcX = screenW - tcTotalW - 10;
                int tcY = screenH - 52;
                if (mouse.X >= tcX && mouse.X < tcX + tcTotalW && mouse.Y >= tcY && mouse.Y < tcY + tcBtnH)
                    _mouseOverUI = true;
            }
        }

        if (!_paused && !editorActive)
        {
            // --- Player input ---
            Vec2 mouseWorld = _camera.ScreenToWorld(new Vector2(mouse.X, mouse.Y), screenW, screenH);

            if (necroIdx >= 0)
            {
                // WASD movement
                Vec2 moveDir = Vec2.Zero;
                if (kb.IsKeyDown(Keys.W)) moveDir.Y -= 1f;
                if (kb.IsKeyDown(Keys.S)) moveDir.Y += 1f;
                if (kb.IsKeyDown(Keys.A)) moveDir.X -= 1f;
                if (kb.IsKeyDown(Keys.D)) moveDir.X += 1f;
                if (moveDir.LengthSq() > 0.01f) moveDir = moveDir.Normalized();

                bool running = kb.IsKeyDown(Keys.LeftShift) || kb.IsKeyDown(Keys.RightShift);
                _sim.SetNecromancerInput(moveDir, running);

                // Mouse facing
                var necroPos = _sim.Units[necroIdx].Position;
                var toMouse = mouseWorld - necroPos;
                if (toMouse.LengthSq() > 0.01f)
                {
                    float mouseAngle = MathF.Atan2(toMouse.Y, toMouse.X) * 180f / MathF.PI;
                    _sim.SetNecromancerFacing(mouseAngle);
                }
            }

            // --- Jump input (Space = jump attack) ---
            if (WasKeyPressed(kb, Keys.Space) && necroIdx >= 0)
            {
                var mu = _sim.UnitsMut;
                bool canJump = mu[necroIdx].Alive && !mu[necroIdx].Jumping
                    && mu[necroIdx].KnockdownTimer <= 0f && !_pendingSpell.Active;
                if (canJump)
                {
                    float facingRad = mu[necroIdx].FacingAngle * MathF.PI / 180f;
                    var facingDir = new Vec2(MathF.Cos(facingRad), MathF.Sin(facingRad));
                    mu[necroIdx].Jumping = true;
                    mu[necroIdx].JumpTimer = 0f;
                    mu[necroIdx].JumpHeight = 0f;
                    mu[necroIdx].JumpStartPos = mu[necroIdx].Position;
                    mu[necroIdx].JumpEndPos = mu[necroIdx].Position + facingDir * 4f;
                    mu[necroIdx].JumpIsAttack = true;
                    mu[necroIdx].JumpAttackFired = false;
                    mu[necroIdx].JumpDuration = 1f;
                }
            }

            // --- Beam/drain channel-hold ---
            if (_channelingSlot >= 0)
            {
                bool stillHeld = _channelingSlot switch
                {
                    0 => kb.IsKeyDown(Keys.Q),
                    1 => kb.IsKeyDown(Keys.E),
                    2 => mouse.LeftButton == ButtonState.Pressed,
                    3 => mouse.RightButton == ButtonState.Pressed,
                    _ => false
                };
                if (!stillHeld)
                {
                    if (necroIdx >= 0)
                    {
                        _sim.Lightning.CancelBeamsForCaster(_sim.Units[necroIdx].Id);
                        _sim.Lightning.CancelDrainsForCaster(_sim.Units[necroIdx].Id);
                    }
                    _channelingSlot = -1;
                }
            }

            // --- Spell bar click interaction ---
            if (mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released)
            {
                int slotW2 = 50, slotH2 = 50;
                int slotY2 = screenH - 95;
                bool clickedSlot = false;

                if (_spellDropdownSlot >= 0)
                {
                    // Check if click is in dropdown
                    int ddX = screenW / 2 - 110 + _spellDropdownSlot * (slotW2 + 4);
                    int ddY = slotY2 - 20; // above the slot
                    var spellIDs = _gameData.Spells.GetIDs();
                    int ddItemH = 20;
                    int ddH = (spellIDs.Count + 1) * ddItemH; // +1 for "None"

                    if (mouse.X >= ddX && mouse.X < ddX + 160 && mouse.Y >= ddY - ddH && mouse.Y < ddY)
                    {
                        int itemIdx = (ddY - mouse.Y) / ddItemH;
                        if (itemIdx == 0)
                            _spellBarState.Slots[_spellDropdownSlot].SpellID = "";
                        else if (itemIdx - 1 < spellIDs.Count)
                            _spellBarState.Slots[_spellDropdownSlot].SpellID = spellIDs[itemIdx - 1];
                        clickedSlot = true;
                    }
                    _spellDropdownSlot = -1;
                }
                else
                {
                    // Check if click is on a spell slot
                    for (int s = 0; s < 4; s++)
                    {
                        int slotX2 = screenW / 2 - 110 + s * (slotW2 + 4);
                        if (mouse.X >= slotX2 && mouse.X < slotX2 + slotW2 &&
                            mouse.Y >= slotY2 && mouse.Y < slotY2 + slotH2)
                        {
                            _spellDropdownSlot = s;
                            clickedSlot = true;
                            break;
                        }
                    }
                }

                if (clickedSlot) goto SkipSpellCast;

                // Also check secondary bar click
                int secSlotW2 = 35, secSlotH2 = 35;
                int secSlotY2 = screenH - 95 - secSlotH2 - 6;
                if (_secondaryDropdownSlot >= 0)
                {
                    // Check if click is in secondary dropdown
                    int sddX = screenW / 2 - 80 + _secondaryDropdownSlot * (secSlotW2 + 4);
                    int sddY = secSlotY2 - 20;
                    var secSpellIDs = _gameData.Spells.GetIDs();
                    int sddItemH = 20;
                    int sddH = (secSpellIDs.Count + 1) * sddItemH;

                    if (mouse.X >= sddX && mouse.X < sddX + 160 && mouse.Y >= sddY - sddH && mouse.Y < sddY)
                    {
                        int sddIdx = (sddY - mouse.Y) / sddItemH;
                        if (sddIdx == 0)
                            _secondaryBarState.Slots[_secondaryDropdownSlot].SpellID = "";
                        else if (sddIdx - 1 < secSpellIDs.Count)
                            _secondaryBarState.Slots[_secondaryDropdownSlot].SpellID = secSpellIDs[sddIdx - 1];
                    }
                    _secondaryDropdownSlot = -1;
                }
                else
                {
                    for (int ss = 0; ss < 4; ss++)
                    {
                        int ssX = screenW / 2 - 80 + ss * (secSlotW2 + 4);
                        if (mouse.X >= ssX && mouse.X < ssX + secSlotW2 &&
                            mouse.Y >= secSlotY2 && mouse.Y < secSlotY2 + secSlotH2)
                        {
                            _secondaryDropdownSlot = ss;
                            goto SkipSpellCast;
                        }
                    }
                }

                // --- Potion slot dropdown interaction ---
                int potionBaseX = screenW / 2 - 80 + 4 * (secSlotW2 + 4) + 8;
                int potionSlotY = secSlotY2;
                if (_potionDropdownSlot >= 0)
                {
                    // Check if click is in potion dropdown
                    int pddX = potionBaseX + _potionDropdownSlot * (secSlotW2 + 4);
                    int pddY = potionSlotY - 20;
                    var allPotionIds = _gameData.Potions.GetIDs();
                    int pddItemH = 20;
                    int pddH = (allPotionIds.Count + 1) * pddItemH;

                    if (mouse.X >= pddX && mouse.X < pddX + 160 && mouse.Y >= pddY - pddH && mouse.Y < pddY)
                    {
                        int pddIdx = (pddY - mouse.Y) / pddItemH;
                        if (pddIdx == 0)
                            _potionSlots[_potionDropdownSlot] = "";
                        else if (pddIdx - 1 < allPotionIds.Count)
                        {
                            var pdef = _gameData.Potions.Get(allPotionIds[pddIdx - 1]);
                            _potionSlots[_potionDropdownSlot] = pdef?.ItemID ?? "";
                        }
                    }
                    _potionDropdownSlot = -1;
                }
                else
                {
                    // Check if click is on a potion slot
                    for (int ps = 0; ps < 2; ps++)
                    {
                        int psX = potionBaseX + ps * (secSlotW2 + 4);
                        if (mouse.X >= psX && mouse.X < psX + secSlotW2 &&
                            mouse.Y >= potionSlotY && mouse.Y < potionSlotY + secSlotH2)
                        {
                            _potionDropdownSlot = ps;
                            goto SkipSpellCast;
                        }
                    }
                }
            }

            // --- Spell casting (Q = slot 0, E = slot 1, LClick = slot 2, RClick = slot 3) ---
            for (int slot = 0; slot < 4; slot++)
            {
                bool pressed = slot switch
                {
                    0 => WasKeyPressed(kb, Keys.Q),
                    1 => WasKeyPressed(kb, Keys.E),
                    2 => !_mouseOverUI && mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released,
                    3 => !_mouseOverUI && mouse.RightButton == ButtonState.Pressed && _prevMouse.RightButton == ButtonState.Released,
                    _ => false
                };

                if (!pressed || slot >= _spellBarState.Slots.Length) continue;
                string spellId = _spellBarState.Slots[slot].SpellID;
                if (string.IsNullOrEmpty(spellId) || necroIdx < 0) continue;

                // --- Melee & Gather: special built-in ability ---
                if (spellId == "melee_gather")
                {
                    TryMeleeOrGather(necroIdx, mouseWorld);
                    continue;
                }

                var result = SpellCaster.TryStartSpellCast(spellId, _gameData.Spells, _sim.NecroState,
                    _sim.Units, necroIdx, mouseWorld, _sim.Corpses, _pendingSpell, _gameData);

                if (result == CastResult.Success)
                {
                    var spell = _gameData.Spells.Get(spellId);
                    if (spell == null) continue;
                    var necroPos = _sim.Units[necroIdx].Position;
                    var necroUid = _sim.Units[necroIdx].Id;
                    var effectOrigin = _sim.Units[necroIdx].EffectSpawnPos2D;

                    switch (spell.Category)
                    {
                        case "Projectile":
                            // Fire first projectile immediately (from weapon tip)
                            SpawnSpellProjectile(spell, effectOrigin, mouseWorld, necroUid);
                            // Queue remaining with delay
                            if (spell.Quantity > 1)
                            {
                                _pendingProjectiles.Add(new PendingProjectileGroup
                                {
                                    SpellID = spellId,
                                    Origin = effectOrigin,
                                    Target = mouseWorld,
                                    Remaining = spell.Quantity - 1,
                                    Timer = 0f,
                                    Interval = spell.ProjectileDelay > 0f ? spell.ProjectileDelay : 0.1f
                                });
                            }
                            break;

                        case "Buff":
                        case "Debuff":
                            // Apply buff to target or self
                            if (!string.IsNullOrEmpty(spell.BuffID))
                            {
                                var buffDef = _gameData.Buffs.Get(spell.BuffID);
                                if (buffDef != null)
                                    BuffSystem.ApplyBuff(_sim.UnitsMut, necroIdx, buffDef);
                            }
                            break;

                        case "Strike":
                        {
                            var style = new LightningStyle
                            {
                                CoreColor = spell.StrikeCoreColor,
                                GlowColor = spell.StrikeGlowColor,
                                CoreWidth = spell.StrikeCoreWidth,
                                GlowWidth = spell.StrikeGlowWidth,
                                Displacement = spell.StrikeDisplacement,
                                MaxBranches = spell.StrikeBranches
                            };
                            var sVis = spell.StrikeVisualType == "GodRay" ? StrikeVisual.GodRay : StrikeVisual.Lightning;
                            var sGrp = new GodRayParams { EdgeSoftness = spell.GodRayEdgeSoftness,
                                NoiseSpeed = spell.GodRayNoiseSpeed, NoiseStrength = spell.GodRayNoiseStrength,
                                NoiseScale = spell.GodRayNoiseScale };
                            Enum.TryParse<SpellTargetFilter>(spell.TargetFilter, out var sTF);

                            if (spell.StrikeTargetUnit)
                            {
                                // Zap: caster weapon tip to nearest enemy near mouse
                                var casterEffPos = effectOrigin;
                                float casterH = _sim.Units[necroIdx].EffectSpawnHeight;
                                // Find nearest enemy to mouse
                                int enemy = -1;
                                float bestDist = spell.Range * spell.Range;
                                for (int ui = 0; ui < _sim.Units.Count; ui++)
                                {
                                    if (!_sim.Units[ui].Alive || _sim.Units[ui].Faction == _sim.Units[necroIdx].Faction) continue;
                                    float d = (mouseWorld - _sim.Units[ui].Position).LengthSq();
                                    if (d < bestDist) { bestDist = d; enemy = ui; }
                                }
                                if (enemy >= 0)
                                {
                                    var targetPos = _sim.Units[enemy].Position;
                                    float targetH = 1.0f;
                                    var tDef = _gameData.Units.Get(_sim.Units[enemy].UnitDefID);
                                    if (tDef != null) targetH = tDef.SpriteWorldHeight * 0.5f;

                                    _sim.Lightning.SpawnZap(casterEffPos, targetPos,
                                        spell.ZapDuration > 0 ? spell.ZapDuration : spell.StrikeDuration,
                                        style, casterH, targetH);
                                    // Apply damage + show damage number
                                    _sim.DealDamage(enemy, spell.Damage);
                                    _damageNumbers.Add(new DamageNumber { WorldPos = targetPos, Damage = spell.Damage, Timer = 0f, Height = targetH });
                                }
                            }
                            else
                            {
                                // Sky strike at mouse position
                                _sim.Lightning.SpawnStrike(mouseWorld, spell.TelegraphDuration,
                                    spell.StrikeDuration, spell.AoeRadius, spell.Damage,
                                    style, spell.Id, sVis, sGrp, sTF);
                            }
                            break;
                        }

                        case "Summon":
                            ExecuteSummonSpell(spell, _pendingSpell, necroPos, necroIdx);
                            break;

                        case "Beam":
                        {
                            // Find target unit near mouse
                            int targetIdx = FindClosestEnemyToPoint(mouseWorld, 3f);
                            if (targetIdx >= 0)
                            {
                                _sim.Lightning.SpawnBeam(necroUid, _sim.Units[targetIdx].Id,
                                    spell.Id, spell.Damage, spell.BeamTickRate, spell.BeamRetargetRadius,
                                    new LightningStyle { CoreColor = spell.BeamCoreColor, GlowColor = spell.BeamGlowColor,
                                        CoreWidth = spell.BeamCoreWidth, GlowWidth = spell.BeamGlowWidth,
                                        Displacement = spell.BeamDisplacement, MaxBranches = spell.BeamBranches });
                                _channelingSlot = slot;
                            }
                            break;
                        }

                        case "Drain":
                        {
                            int targetIdx2 = FindClosestEnemyToPoint(mouseWorld, 5f);
                            if (targetIdx2 >= 0)
                            {
                                _sim.Lightning.SpawnDrain(necroUid, _sim.Units[targetIdx2].Id,
                                    spell.Id, spell.Damage, spell.DrainTickRate, spell.DrainHealPercent,
                                    spell.DrainCorpseHP, spell.DrainReversed, spell.DrainMaxDuration,
                                    spell.DrainTendrilCount, spell.DrainArcHeight, spell.DrainCoreColor, spell.DrainGlowColor);
                                _channelingSlot = slot;
                            }
                            break;
                        }

                        case "Command":
                        {
                            // Order Attack: send horde minions to attack-move toward target
                            // They stay in the horde and auto-return when area is clear or timeout
                            for (int ci = 0; ci < _sim.Units.Count; ci++)
                            {
                                if (!_sim.Units[ci].Alive) continue;
                                if (_sim.Units[ci].Faction != Faction.Undead) continue;
                                if (_sim.Units[ci].Archetype != AI.ArchetypeRegistry.HordeMinion) continue;

                                _sim.UnitsMut[ci].Routine = 4; // RoutineCommanded
                                _sim.UnitsMut[ci].Subroutine = 0;
                                _sim.UnitsMut[ci].SubroutineTimer = 0f;
                                _sim.UnitsMut[ci].MoveTarget = mouseWorld;
                                _sim.UnitsMut[ci].Target = CombatTarget.None;
                                _sim.UnitsMut[ci].EngagedTarget = CombatTarget.None;
                            }
                            break;
                        }

                        case "Toggle":
                        {
                            // Toggle effect on necromancer
                            if (spell.ToggleEffect == "ghost_mode")
                                _sim.UnitsMut[necroIdx].GhostMode = !_sim.Units[necroIdx].GhostMode;
                            break;
                        }
                    }

                    // Apply casting buff if defined
                    if (!string.IsNullOrEmpty(spell.CastingBuffID))
                    {
                        var castBuff = _gameData.Buffs.Get(spell.CastingBuffID);
                        if (castBuff != null) BuffSystem.ApplyBuff(_sim.UnitsMut, necroIdx, castBuff);
                    }
                }
                else if (slot == 2 && result == CastResult.NoValidTarget)
                {
                    // LClick on empty slot or failed cast = melee attack
                    if (necroIdx >= 0 && !_sim.Units[necroIdx].PendingAttack.IsNone == false)
                    {
                        int meleeTarget = FindClosestEnemyToPoint(
                            _sim.Units[necroIdx].Position, 2f);
                        if (meleeTarget >= 0)
                        {
                            _sim.UnitsMut[necroIdx].Target = CombatTarget.Unit(_sim.Units[meleeTarget].Id);
                            _sim.UnitsMut[necroIdx].PendingAttack = CombatTarget.Unit(_sim.Units[meleeTarget].Id);
                            _sim.UnitsMut[necroIdx].AttackCooldown = 2f;
                        }
                    }
                }
            }

            SkipSpellCast:

            // --- Ghost mode toggle (G) ---
            if (WasKeyPressed(kb, Keys.G) && necroIdx >= 0)
                _sim.UnitsMut[necroIdx].GhostMode = !_sim.Units[necroIdx].GhostMode;

            // --- Secondary spell bar (keys 1-4) ---
            if (necroIdx >= 0)
            {
                Keys[] secKeys = { Keys.D1, Keys.D2, Keys.D3, Keys.D4 };
                for (int sk = 0; sk < 4; sk++)
                {
                    if (!WasKeyPressed(kb, secKeys[sk])) continue;
                    if (sk >= _secondaryBarState.Slots.Length) continue;
                    string secSpellId = _secondaryBarState.Slots[sk].SpellID;
                    if (string.IsNullOrEmpty(secSpellId)) continue;
                    var secResult = SpellCaster.TryStartSpellCast(secSpellId, _gameData.Spells, _sim.NecroState,
                        _sim.Units, necroIdx, mouseWorld, _sim.Corpses, _pendingSpell, _gameData);
                    if (secResult == CastResult.Success)
                    {
                        var spell2 = _gameData.Spells.Get(secSpellId);
                        if (spell2 == null) continue;
                        var necroPos2 = _sim.Units[necroIdx].Position;
                        var necroUid2 = _sim.Units[necroIdx].Id;
                        var effectOrigin2 = _sim.Units[necroIdx].EffectSpawnPos2D;
                        switch (spell2.Category)
                        {
                            case "Projectile":
                                SpawnSpellProjectile(spell2, effectOrigin2, mouseWorld, necroUid2);
                                if (spell2.Quantity > 1)
                                {
                                    _pendingProjectiles.Add(new PendingProjectileGroup
                                    {
                                        SpellID = secSpellId,
                                        Origin = effectOrigin2,
                                        Target = mouseWorld,
                                        Remaining = spell2.Quantity - 1,
                                        Timer = 0f,
                                        Interval = spell2.ProjectileDelay > 0f ? spell2.ProjectileDelay : 0.1f
                                    });
                                }
                                break;
                            case "Buff": case "Debuff":
                                if (!string.IsNullOrEmpty(spell2.BuffID)) { var bd2 = _gameData.Buffs.Get(spell2.BuffID); if (bd2 != null) BuffSystem.ApplyBuff(_sim.UnitsMut, necroIdx, bd2); }
                                break;
                            case "Strike":
                            {
                                var sv2 = spell2.StrikeVisualType == "GodRay" ? StrikeVisual.GodRay : StrikeVisual.Lightning;
                                var gr2 = new GodRayParams { EdgeSoftness = spell2.GodRayEdgeSoftness, NoiseSpeed = spell2.GodRayNoiseSpeed, NoiseStrength = spell2.GodRayNoiseStrength, NoiseScale = spell2.GodRayNoiseScale };
                                Enum.TryParse<SpellTargetFilter>(spell2.TargetFilter, out var tf2);
                                _sim.Lightning.SpawnStrike(mouseWorld, spell2.TelegraphDuration, spell2.StrikeDuration, spell2.AoeRadius, spell2.Damage,
                                    new LightningStyle { CoreColor = spell2.StrikeCoreColor, GlowColor = spell2.StrikeGlowColor, CoreWidth = spell2.StrikeCoreWidth, GlowWidth = spell2.StrikeGlowWidth,
                                        Displacement = spell2.StrikeDisplacement, MaxBranches = spell2.StrikeBranches },
                                    spell2.Id, sv2, gr2, tf2);
                                break;
                            }
                            case "Summon":
                                ExecuteSummonSpell(spell2, _pendingSpell, necroPos2, necroIdx);
                                break;
                            case "Command":
                                for (int ci = 0; ci < _sim.Units.Count; ci++)
                                {
                                    if (!_sim.Units[ci].Alive) continue;
                                    if (_sim.Units[ci].Faction != Faction.Undead) continue;
                                    if (_sim.Units[ci].Archetype != AI.ArchetypeRegistry.HordeMinion) continue;
                                    _sim.UnitsMut[ci].Routine = 4; // RoutineCommanded
                                    _sim.UnitsMut[ci].Subroutine = 0;
                                    _sim.UnitsMut[ci].SubroutineTimer = 0f;
                                    _sim.UnitsMut[ci].MoveTarget = mouseWorld;
                                    _sim.UnitsMut[ci].Target = CombatTarget.None;
                                    _sim.UnitsMut[ci].EngagedTarget = CombatTarget.None;
                                }
                                break;
                            case "Toggle":
                                if (spell2.ToggleEffect == "ghost_mode")
                                    _sim.UnitsMut[necroIdx].GhostMode = !_sim.Units[necroIdx].GhostMode;
                                break;
                        }

                        // Apply casting buff if defined
                        if (!string.IsNullOrEmpty(spell2.CastingBuffID))
                        {
                            var castBuff2 = _gameData.Buffs.Get(spell2.CastingBuffID);
                            if (castBuff2 != null) BuffSystem.ApplyBuff(_sim.UnitsMut, necroIdx, castBuff2);
                        }
                    }
                }
            }

            // --- Auto-populate potion slots from inventory ---
            {
                var potionItemIds = new List<string>();
                foreach (var pid in _gameData.Potions.GetIDs())
                {
                    var pdef = _gameData.Potions.Get(pid);
                    if (pdef != null && !string.IsNullOrEmpty(pdef.ItemID) && _inventory.GetItemCount(pdef.ItemID) > 0)
                        potionItemIds.Add(pdef.ItemID);
                }
                for (int pk = 0; pk < 2; pk++)
                {
                    // Clear slot if item is gone from inventory
                    if (!string.IsNullOrEmpty(_potionSlots[pk]) && _inventory.GetItemCount(_potionSlots[pk]) <= 0)
                        _potionSlots[pk] = "";
                    // Fill empty slot with next available potion not already assigned
                    if (string.IsNullOrEmpty(_potionSlots[pk]))
                    {
                        foreach (var itemId in potionItemIds)
                        {
                            bool alreadyAssigned = false;
                            for (int other = 0; other < 2; other++)
                                if (other != pk && _potionSlots[other] == itemId) alreadyAssigned = true;
                            if (!alreadyAssigned) { _potionSlots[pk] = itemId; break; }
                        }
                    }
                }
            }

            // --- Potion slots (keys 5, 6) — select for throwing ---
            if (necroIdx >= 0)
            {
                Keys[] potionKeys = { Keys.D5, Keys.D6 };
                for (int pk = 0; pk < 2; pk++)
                {
                    if (!WasKeyPressed(kb, potionKeys[pk])) continue;
                    if (string.IsNullOrEmpty(_potionSlots[pk])) continue;
                    if (_inventory.GetItemCount(_potionSlots[pk]) <= 0) continue;
                    _activePotionSlot = (_activePotionSlot == pk) ? -1 : pk;
                }
            }

            // Potion throw on left-click when a potion slot is active
            if (_activePotionSlot >= 0 && necroIdx >= 0 && !_mouseOverUI
                && mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released)
            {
                string potionItemId = _potionSlots[_activePotionSlot];
                var potionDef = FindPotionByItemId(potionItemId);
                if (potionDef != null)
                {
                    var necroPos = _sim.Units[necroIdx].Position;
                    float dist = (mouseWorld - necroPos).Length();

                    if (dist < 1.0f)
                    {
                        // Self-target: apply directly
                        if (_inventory.GetItemCount(potionItemId) > 0)
                        {
                            _inventory.RemoveItem(potionItemId, 1);
                            PotionSystem.ApplyPotionEffect(potionDef.Id, _gameData.Potions, _gameData.Buffs,
                                necroIdx, _sim.UnitsMut, _sim.Units[necroIdx].Faction,
                                _sim.PendingZombieRaises, _sim.CorpsesMut, necroPos);
                        }
                    }
                    else
                    {
                        PotionSystem.TryThrowPotion(potionDef.Id, _gameData.Potions, _inventory,
                            _sim.UnitsMut, necroIdx, mouseWorld, _sim.Corpses, _sim.Projectiles);
                    }
                }
                _activePotionSlot = -1;
            }

            // Cancel potion selection on right-click
            if (_activePotionSlot >= 0 && mouse.RightButton == ButtonState.Pressed && _prevMouse.RightButton == ButtonState.Released)
                _activePotionSlot = -1;

            // --- Corpse drag (F key) ---
            if (WasKeyPressed(kb, Keys.F) && necroIdx >= 0)
            {
                int currentDrag = _sim.Units[necroIdx].DraggingCorpseIdx;
                if (currentDrag >= 0)
                {
                    if (currentDrag < _sim.Corpses.Count)
                        _sim.CorpsesMut[currentDrag].DraggedByUnitID = GameConstants.InvalidUnit;
                    _sim.UnitsMut[necroIdx].DraggingCorpseIdx = -1;
                }
                else
                {
                    var np3 = _sim.Units[necroIdx].Position;
                    float bcd = 3f * 3f; int bci = -1;
                    for (int ci = 0; ci < _sim.Corpses.Count; ci++)
                    {
                        if (_sim.Corpses[ci].Dissolving || _sim.Corpses[ci].ConsumedBySummon) continue;
                        if (_sim.Corpses[ci].DraggedByUnitID != GameConstants.InvalidUnit) continue;
                        float cdd = (_sim.Corpses[ci].Position - np3).LengthSq();
                        if (cdd < bcd) { bcd = cdd; bci = ci; }
                    }
                    if (bci >= 0)
                    {
                        _sim.UnitsMut[necroIdx].DraggingCorpseIdx = bci;
                        _sim.CorpsesMut[bci].DraggedByUnitID = _sim.Units[necroIdx].Id;
                    }
                }
            }

            // --- Building hover detection ---
            _hoveredObjectIdx = -1;
            {
                float bhd = 2f * 2f;
                for (int oi = 0; oi < _envSystem.ObjectCount; oi++)
                {
                    var obj = _envSystem.GetObject(oi);
                    if (!_envSystem.Defs[obj.DefIndex].IsBuilding) continue;
                    float hdx = obj.X - mouseWorld.X, hdy = obj.Y - mouseWorld.Y;
                    float hd = hdx * hdx + hdy * hdy;
                    if (hd < bhd) { bhd = hd; _hoveredObjectIdx = oi; }
                }
            }

            // --- Building placement toggle (B) ---
            if (WasKeyPressed(kb, Keys.B))
            {
                if (_craftingMenu.IsVisible) _craftingMenu.Close();
                _buildingMenuUI.Toggle(screenW, screenH);
            }

            // --- Crafting menu toggle (C) ---
            if (WasKeyPressed(kb, Keys.C))
            {
                if (_buildingMenuUI.IsVisible) _buildingMenuUI.Close();
                _craftingMenu.Toggle(screenW, screenH);
            }

            // --- Building placement click (world click to place) ---
            if (_buildingMenuUI.IsPlacementActive
                && !_buildingMenuUI.ContainsMouse(mouse.X, mouse.Y)
                && mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released)
            {
                _buildingMenuUI.TryPlace(mouseWorld.X, mouseWorld.Y);
            }

            // --- Foragable collection (right-click within 2 units of necromancer) ---
            if (!_mouseOverUI && mouse.RightButton == ButtonState.Pressed && _prevMouse.RightButton == ButtonState.Released
                && _sim.NecromancerIndex >= 0)
            {
                int bestIdx = FindNearestForagable(_sim.Units[_sim.NecromancerIndex].Position, 2f);
                if (bestIdx >= 0)
                    StartForagableCollection(bestIdx);
            }

            // --- Auto-pickup foragables ---
            if (_gameData.Settings.General.AutoPickupForagables && _sim.NecromancerIndex >= 0)
            {
                _autoPickupCooldown -= dt;
                if (_autoPickupCooldown <= 0f)
                {
                    int autoIdx = FindNearestForagable(_sim.Units[_sim.NecromancerIndex].Position, ForagableAutoPickupRange);
                    if (autoIdx >= 0)
                    {
                        StartForagableCollection(autoIdx);
                        _autoPickupCooldown = 0.3f; // stagger auto-pickups
                    }
                }
            }

            // --- Time controls (keyboard) ---
            if (WasKeyPressed(kb, Keys.OemPlus) || WasKeyPressed(kb, Keys.Add))
                _timeScale = MathF.Min(_timeScale * 2f, 8f);
            if (WasKeyPressed(kb, Keys.OemMinus) || WasKeyPressed(kb, Keys.Subtract))
                _timeScale = MathF.Max(_timeScale * 0.5f, 0.25f);
            if (WasKeyPressed(kb, Keys.D0))
                _timeScale = 1f;

            // --- Time controls (mouse click on buttons) ---
            if (_gameData.Settings.General.ShowTimeControls
                && mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released)
            {
                ReadOnlySpan<float> tcSpeeds = stackalloc float[] { 0.1f, 0.25f, 0.5f, 1.0f, 1.5f, 2.0f };
                const int tcBtnW = 32, tcBtnH = 22, tcGap = 2, tcNum = 6;
                int tcTotalW = tcNum * tcBtnW + (tcNum - 1) * tcGap;
                int tcBaseX = screenW - tcTotalW - 10;
                int tcBaseY = screenH - 52;
                for (int s = 0; s < tcNum; s++)
                {
                    int bx = tcBaseX + s * (tcBtnW + tcGap);
                    if (mouse.X >= bx && mouse.X < bx + tcBtnW && mouse.Y >= tcBaseY && mouse.Y < tcBaseY + tcBtnH)
                    {
                        _timeScale = tcSpeeds[s];
                        break;
                    }
                }
            }

            // --- Tick pending projectiles ---
            TickPendingProjectiles(dt);

            // --- Simulate ---
            _sim.Tick(dt);
            _weatherRenderer.Update(dt, _gameData);
            _envSystem.UpdateForagables(dt);
            _envSystem.UpdateTraps(dt, _sim.Units);
            ProcessTrapFireEvents();

            // --- Scenario tick ---
            if (_activeScenario != null)
            {
                _activeScenario.OnTick(_sim, dt);

                // Apply menu state requests from scenario (for editor screenshots)
                if (_activeScenario.RequestedMenuState != null)
                {
                    var requested = _activeScenario.RequestedMenuState;
                    _activeScenario.RequestedMenuState = null;
                    if (Enum.TryParse<MenuState>(requested, true, out var state))
                        _menuState = state;
                }

                // Apply camera overrides from scenario each tick
                if (_activeScenario.HasCameraOverride)
                {
                    _camera.Position = new Vec2(_activeScenario.CameraX, _activeScenario.CameraY);
                    _camera.Zoom = _activeScenario.CameraZoom;
                }

                // Apply inventory UI requests from scenario
                if (_activeScenario.RequestOpenInventory)
                {
                    _activeScenario.RequestOpenInventory = false;
                    _inventoryUI.Open(screenW, screenH);
                }
                if (_activeScenario.RequestCloseInventory)
                {
                    _activeScenario.RequestCloseInventory = false;
                    _inventoryUI.Close();
                }

                // Apply weather preset override from scenario
                if (_activeScenario.WeatherPreset != null)
                {
                    if (_gameData.Settings.Weather.ActivePreset != _activeScenario.WeatherPreset)
                    {
                        _gameData.Settings.Weather.Enabled = true;
                        _gameData.Settings.Weather.ActivePreset = _activeScenario.WeatherPreset;
                    }
                }

                // Apply collision debug override from scenario
                if (_activeScenario.CollisionDebugOverride.HasValue)
                {
                    _collisionDebugMode = _activeScenario.CollisionDebugOverride.Value;
                    _activeScenario.CollisionDebugOverride = null;
                }

                if (_activeScenario.IsComplete)
                {
                    int result = _activeScenario.OnComplete(_sim);
                    string scenarioName = _activeScenario.Name;
                    DebugLog.Log("scenario", $"Scenario '{scenarioName}' completed with result: {(result == 0 ? "PASS" : "FAIL")} (code={result})");
                    Console.Error.WriteLine(result == 0 ? $"SCENARIO PASS: {scenarioName}" : $"SCENARIO FAIL: {scenarioName} (code={result})");
                    _activeScenario = null;

                    // Auto-exit if launched from command line
                    if (LaunchArgs.Headless)
                    {
                        Environment.ExitCode = result;
                        Exit();
                        return;
                    }

                    _menuState = MenuState.MainMenu;
                    _gameWorldLoaded = false;
                    _prevKb = kb;
                    _prevMouse = mouse;
                    base.Update(gameTime);
                    return;
                }
            }

            // Collect damage events for floating numbers
            foreach (var dmg in _sim.DamageEvents)
            {
                _damageNumbers.Add(new DamageNumber
                {
                    WorldPos = dmg.Position,
                    Damage = dmg.Damage,
                    Timer = 0f,
                    Height = dmg.Height,
                    IsPoison = dmg.IsPoison
                });
            }
        }

        // --- Scenario tick when editor is active (editors pause normal sim but scenarios must still tick) ---
        if (editorActive && _activeScenario != null)
        {
            _activeScenario.OnTick(_sim, 1f / 60f);

            if (_activeScenario.RequestedMenuState != null)
            {
                var requested = _activeScenario.RequestedMenuState;
                _activeScenario.RequestedMenuState = null;
                if (Enum.TryParse<MenuState>(requested, true, out var state))
                    _menuState = state;
            }

            if (_activeScenario.RequestSelectFirst)
            {
                _activeScenario.RequestSelectFirst = false;
                if (_menuState == MenuState.UnitEditor) _unitEditor.SelectFirst();
                else if (_menuState == MenuState.SpellEditor) _spellEditor.SelectFirst();
                else if (_menuState == MenuState.UIEditor) _uiEditor.SelectedIndex = 0;
            }

            // Apply map editor tab switch from scenario
            if (_activeScenario.RequestedMapTab != null)
            {
                var tabName = _activeScenario.RequestedMapTab;
                _activeScenario.RequestedMapTab = null;
                if (_menuState == MenuState.MapEditor && Enum.TryParse<MapEditorTab>(tabName, true, out var mapTab))
                    _mapEditor.ActiveTab = mapTab;
            }

            // Apply UI editor tab switch from scenario
            if (_activeScenario.RequestedUITab != null)
            {
                var tabName = _activeScenario.RequestedUITab;
                _activeScenario.RequestedUITab = null;
                if (_menuState == MenuState.UIEditor && Enum.TryParse<UIEditorTab>(tabName, true, out var uiTab))
                    _uiEditor.ActiveTab = uiTab;
            }

            // Inventory UI requests from scenario
            if (_activeScenario.RequestOpenInventory)
            {
                _activeScenario.RequestOpenInventory = false;
                _inventoryUI.Open(screenW, screenH);
            }
            if (_activeScenario.RequestCloseInventory)
            {
                _activeScenario.RequestCloseInventory = false;
                _inventoryUI.Close();
            }

            // Open weapon sub-editor popup
            if (_activeScenario.RequestOpenWeaponSub)
            {
                _activeScenario.RequestOpenWeaponSub = false;
                if (_menuState == MenuState.UnitEditor)
                    _unitEditor.OpenWeaponSubEditor();
            }

            // Open buff manager popup
            if (_activeScenario.RequestOpenBuffManager)
            {
                _activeScenario.RequestOpenBuffManager = false;
                if (_menuState == MenuState.SpellEditor)
                    _spellEditor.OpenBuffManager();
            }

            if (_activeScenario.IsComplete)
            {
                int result = _activeScenario.OnComplete(_sim);
                string scenarioName = _activeScenario.Name;
                DebugLog.Log("scenario", $"Scenario '{scenarioName}' completed with result: {(result == 0 ? "PASS" : "FAIL")} (code={result})");
                Console.Error.WriteLine(result == 0 ? $"SCENARIO PASS: {scenarioName}" : $"SCENARIO FAIL: {scenarioName} (code={result})");
                _activeScenario = null;
                if (LaunchArgs.Headless)
                {
                    Environment.ExitCode = result;
                    Exit();
                    return;
                }
                _menuState = MenuState.MainMenu;
                _gameWorldLoaded = false;
                _prevKb = kb;
                _prevMouse = mouse;
                base.Update(gameTime);
                return;
            }
        }

        // --- Update animations (scaled by timeScale so they match game speed) ---
        UpdateAnimations(dt);

        // --- Update damage numbers ---
        for (int i = _damageNumbers.Count - 1; i >= 0; i--)
        {
            var dn = _damageNumbers[i];
            dn.Timer += dt;
            dn.Height += _gameData.Settings.General.DamageNumberSpeed * dt;
            if (dn.Timer > _gameData.Settings.General.DamageNumberFadeTime)
                _damageNumbers.RemoveAt(i);
            else
                _damageNumbers[i] = dn;
        }

        // --- Game over check ---
        if (_gameOver)
        {
            if (WasKeyPressed(kb, Keys.R))
            {
                _gameOver = false;
                StartGame();
            }
        }
        else if (FindNecromancer() < 0 && !_paused && _gameWorldLoaded && _activeScenario == null)
        {
            _gameOver = true;
        }

        // Update inventory UI after all sim/scenario ticks (so slot sync sees latest inventory state)
        _inventoryUI.Update(mouse, kb);
        _buildingMenuUI.Update(mouse, _prevMouse, screenW, screenH);
        _craftingMenu.Update(mouse, _prevMouse, screenW, screenH, dt);

        _prevKb = kb;
        _prevMouse = mouse;
        base.Update(gameTime);
    }

    /// <summary>Process trap fire events from EnvironmentSystem.UpdateTraps — cast spells and apply damage.</summary>
    private void ProcessTrapFireEvents()
    {
        foreach (var evt in _envSystem.TrapFireEvents)
        {
            var spell = _gameData.Spells.Get(evt.SpellId);
            if (spell == null) continue;

            // Set proper cooldown from spell def
            _envSystem.SetTrapCooldown(evt.ObjectIndex, spell.Cooldown);

            var targetPos = _sim.Units[evt.TargetUnitIdx].Position;
            float targetH = 1.0f;
            var tDef = _gameData.Units.Get(_sim.Units[evt.TargetUnitIdx].UnitDefID);
            if (tDef != null) targetH = tDef.SpriteWorldHeight * 0.5f;

            if (spell.Category == "Strike" && spell.StrikeTargetUnit)
            {
                // Zap from trap to target
                var style = new LightningStyle
                {
                    CoreColor = spell.StrikeCoreColor,
                    GlowColor = spell.StrikeGlowColor,
                    CoreWidth = spell.StrikeCoreWidth,
                    GlowWidth = spell.StrikeGlowWidth,
                    Displacement = spell.StrikeDisplacement,
                    MaxBranches = spell.StrikeBranches
                };
                _sim.Lightning.SpawnZap(evt.TrapPos, targetPos,
                    spell.ZapDuration > 0 ? spell.ZapDuration : 0.2f,
                    style, 0.3f, targetH);
                _sim.DealDamage(evt.TargetUnitIdx, spell.Damage);
                _damageNumbers.Add(new DamageNumber
                {
                    WorldPos = targetPos, Damage = spell.Damage, Timer = 0f, Height = targetH
                });
            }
            else if (spell.Category == "Strike")
            {
                // Ground strike at target position
                var style = new LightningStyle
                {
                    CoreColor = spell.StrikeCoreColor,
                    GlowColor = spell.StrikeGlowColor,
                    CoreWidth = spell.StrikeCoreWidth,
                    GlowWidth = spell.StrikeGlowWidth,
                    Displacement = spell.StrikeDisplacement,
                    MaxBranches = spell.StrikeBranches
                };
                var sVis = spell.StrikeVisualType == "GodRay" ? StrikeVisual.GodRay : StrikeVisual.Lightning;
                _sim.Lightning.SpawnStrike(targetPos, spell.TelegraphDuration,
                    spell.StrikeDuration, spell.AoeRadius, spell.Damage,
                    style, spell.Id, sVis);
            }
        }
    }

    private static void LoadSpellBarSlots(System.Text.Json.JsonElement root, string key, SpellBarState bar)
    {
        if (!root.TryGetProperty(key, out var arr)) return;
        int si = 0;
        foreach (var slot in arr.EnumerateArray())
        {
            if (si >= bar.Slots.Length) break;
            bar.Slots[si] = new SpellBarSlot { SpellID = slot.GetProperty("spellID").GetString() ?? "" };
            si++;
        }
        DebugLog.Log("startup", $"SpellBar '{key}' loaded: {string.Join(", ", bar.Slots.Select(s => s.SpellID))}");
    }

    private bool WasKeyPressed(KeyboardState current, Keys key) =>
        current.IsKeyDown(key) && _prevKb.IsKeyUp(key);

    private PotionDef? FindPotionByItemId(string itemId)
    {
        foreach (var id in _gameData.Potions.GetIDs())
        {
            var p = _gameData.Potions.Get(id);
            if (p != null && p.ItemID == itemId) return p;
        }
        return null;
    }

    /// <summary>
    /// Melee & Gather: try to melee attack an enemy near mouse, otherwise try to collect a foragable.
    /// </summary>
    private void TryMeleeOrGather(int necroIdx, Vec2 mouseWorld)
    {
        var necroPos = _sim.Units[necroIdx].Position;

        // Check melee cooldown
        if (_sim.Units[necroIdx].AttackCooldown <= 0f && _sim.Units[necroIdx].PostAttackTimer <= 0f)
        {
            // Find closest enemy near the mouse that is within melee range of the necromancer
            var stats = _sim.Units[necroIdx].Stats;
            int weaponLen = stats.MeleeWeapons.Count > 0 ? stats.MeleeWeapons[0].Length : stats.Length;
            float meleeRange = 1.0f + weaponLen * 0.15f; // base melee range + weapon reach

            int bestEnemy = -1;
            float bestDist = float.MaxValue;
            for (int i = 0; i < _sim.Units.Count; i++)
            {
                if (i == necroIdx || !_sim.Units[i].Alive) continue;
                if (_sim.Units[i].Faction == _sim.Units[necroIdx].Faction) continue; // skip friendlies

                float distToMouse = (mouseWorld - _sim.Units[i].Position).Length();
                float distToNecro = (necroPos - _sim.Units[i].Position).Length();

                // Must be near the mouse click AND within melee range of necro
                if (distToMouse < 3f && distToNecro <= meleeRange && distToMouse < bestDist)
                {
                    bestDist = distToMouse;
                    bestEnemy = i;
                }
            }

            if (bestEnemy >= 0)
            {
                // Initiate melee attack
                _sim.UnitsMut[necroIdx].PendingAttack = CombatTarget.Unit(_sim.Units[bestEnemy].Id);
                float cooldown = _gameData.Units.Get(_sim.Units[necroIdx].UnitDefID)?.AttackCooldown
                    ?? _gameData.Settings.Combat.AttackCooldown;
                _sim.UnitsMut[necroIdx].AttackCooldown = cooldown;
                return;
            }
        }

        // No melee target — try foragable collection
        int bestForage = FindNearestForagable(necroPos, 2f);
        if (bestForage >= 0)
            StartForagableCollection(bestForage);
    }

    private int FindNearestForagable(Vec2 fromPos, float maxDist)
    {
        float bestDist = maxDist;
        int bestIdx = -1;
        for (int fi = 0; fi < _envSystem.ObjectCount; fi++)
        {
            if (!_envSystem.IsObjectVisible(fi)) continue;
            var def = _envSystem.Defs[_envSystem.Objects[fi].DefIndex];
            if (!def.IsForagable) continue;
            var obj = _envSystem.Objects[fi];
            float dist = (new Vec2(obj.X, obj.Y) - fromPos).Length();
            if (dist < bestDist) { bestDist = dist; bestIdx = fi; }
        }
        return bestIdx;
    }

    /// <summary>Start a foragable collection with arc animation instead of instant pickup.</summary>
    private void StartForagableCollection(int objIdx)
    {
        if (_sim.NecromancerIndex < 0) return;
        string? resourceType = _envSystem.CollectForagable(objIdx);
        if (resourceType == null) return;

        var obj = _envSystem.Objects[objIdx];
        var def = _envSystem.Defs[obj.DefIndex];
        var tex = _envSystem.GetDefTexture(obj.DefIndex);

        float worldH = def.SpriteWorldHeight * obj.Scale * def.Scale;
        float pixelH = worldH * _camera.Zoom;
        float baseScale = tex != null ? pixelH / tex.Height : 1f;

        _collectingForagables.Add(new CollectingForagable
        {
            ObjIdx = objIdx,
            StartPos = new Vec2(obj.X, obj.Y),
            StartHeight = 0f,
            TargetPos = _sim.Units[_sim.NecromancerIndex].Position,
            Timer = 0f,
            ArcDuration = ForagableArcDuration,
            ResourceType = resourceType,
            Texture = tex,
            BaseScale = baseScale,
            PivotX = def.PivotX,
            PivotY = def.PivotY,
        });
    }

    /// <summary>Update collecting foragable arcs. Called each frame.</summary>
    private void UpdateCollectingForagables(float dt)
    {
        for (int i = _collectingForagables.Count - 1; i >= 0; i--)
        {
            var cf = _collectingForagables[i];
            cf.Timer += dt;

            // Update target to follow necromancer
            if (_sim.NecromancerIndex >= 0)
                cf.TargetPos = _sim.Units[_sim.NecromancerIndex].Position;

            _collectingForagables[i] = cf;

            if (cf.Timer >= cf.ArcDuration)
            {
                // Complete collection — add to inventory
                _inventory.AddItem(cf.ResourceType);

                // Pop effect at character
                _effectManager.SpawnDustPuff(cf.TargetPos);

                // Pickup sound
                _pickupSound?.Play(0.3f, 0f, 0f);

                // Floating pickup text (green, rising)
                _damageNumbers.Add(new DamageNumber
                {
                    WorldPos = cf.TargetPos,
                    Damage = 0, // we'll use a special marker
                    Timer = 0f,
                    Height = 2f,
                    IsPoison = false,
                    PickupText = cf.ResourceType
                });

                _collectingForagables.RemoveAt(i);
            }
        }
    }

    /// <summary>Draw collecting foragable arcs (objects flying toward character).</summary>
    private void DrawCollectingForagables()
    {
        foreach (var cf in _collectingForagables)
        {
            if (cf.Texture == null) continue;
            float t = cf.Timer / cf.ArcDuration; // 0..1

            // Position: lerp from start to target
            Vec2 pos = cf.StartPos + (cf.TargetPos - cf.StartPos) * t;

            // Height: arc upward then down (parabola peaking at t=0.3)
            float arcHeight = 2f * (1f - (t - 0.3f) * (t - 0.3f) / 0.49f);
            if (arcHeight < 0f) arcHeight = 0f;

            // Scale: shrink from 100% to 40%
            float scale = cf.BaseScale * (1f - t * 0.6f);

            // Rotation: spin faster over time
            float rotation = t * t * 6f;

            var sp = _renderer.WorldToScreen(pos, arcHeight, _camera);
            var origin = new Vector2(cf.PivotX * cf.Texture.Width, cf.PivotY * cf.Texture.Height);
            _spriteBatch.Draw(cf.Texture, sp, null, Color.White, rotation, origin, scale, SpriteEffects.None, 0f);
        }
    }

    private Texture2D? GetItemTextureByPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (_itemTextureCache.TryGetValue(path, out var cached)) return cached;
        try
        {
            var tex = TextureUtil.LoadPremultiplied(GraphicsDevice, GamePaths.Resolve(path));
            _itemTextureCache[path] = tex;
            return tex;
        }
        catch { _itemTextureCache[path] = null; return null; }
    }

    private Texture2D? GetItemTexture(string itemId)
    {
        if (string.IsNullOrEmpty(itemId)) return null;
        if (_itemTextureCache.TryGetValue(itemId, out var cached)) return cached;
        var item = _gameData.Items.Get(itemId);
        if (item == null || string.IsNullOrEmpty(item.Icon)) { _itemTextureCache[itemId] = null; return null; }
        try
        {
            var tex = TextureUtil.LoadPremultiplied(GraphicsDevice, GamePaths.Resolve(item.Icon));
            _itemTextureCache[itemId] = tex;
            return tex;
        }
        catch { _itemTextureCache[itemId] = null; return null; }
    }

    private int FindClosestEnemyToPoint(Vec2 worldPos, float maxRange)
    {
        float bestDist = maxRange * maxRange;
        int bestIdx = -1;
        for (int i = 0; i < _sim.Units.Count; i++)
        {
            if (!_sim.Units[i].Alive) continue;
            // Enemy = different faction from necromancer (undead)
            if (_sim.Units[i].Faction == Faction.Undead) continue;
            float d = (_sim.Units[i].Position - worldPos).LengthSq();
            if (d < bestDist) { bestDist = d; bestIdx = i; }
        }
        return bestIdx;
    }

    private int FindNecromancer()
    {
        for (int i = 0; i < _sim.Units.Count; i++)
            if (_sim.Units[i].Alive && _sim.Units[i].AI == AIBehavior.PlayerControlled)
                return i;
        return -1;
    }

    private void SpawnSpellProjectile(SpellDef spell, Vec2 origin, Vec2 target, uint ownerUid)
    {
        _sim.Projectiles.SpawnFireball(origin, target,
            Faction.Undead, ownerUid, spell.Damage, spell.AoeRadius, spell.DisplayName);
        var projs = _sim.Projectiles.Projectiles;
        if (projs.Count > 0)
        {
            var lastProj = projs[projs.Count - 1];
            if (spell.ProjectileFlipbook != null)
            {
                lastProj.FlipbookID = spell.ProjectileFlipbook.FlipbookID;
                lastProj.ParticleScale = spell.ProjectileFlipbook.Scale;
                lastProj.ParticleColor = spell.ProjectileFlipbook.Color;
            }
            if (spell.HitEffectFlipbook != null)
            {
                lastProj.HitEffectFlipbookID = spell.HitEffectFlipbook.FlipbookID;
                lastProj.HitEffectScale = spell.HitEffectFlipbook.Scale;
                lastProj.HitEffectColor = spell.HitEffectFlipbook.Color;
            }
        }
    }

    private void TickPendingProjectiles(float dt)
    {
        for (int i = _pendingProjectiles.Count - 1; i >= 0; i--)
        {
            var pg = _pendingProjectiles[i];
            pg.Timer += dt;
            if (pg.Timer >= pg.Interval)
            {
                pg.Timer -= pg.Interval;
                pg.Remaining--;

                var spell = _gameData.Spells.Get(pg.SpellID);
                if (spell != null)
                {
                    int necroIdx = FindNecromancer();
                    uint ownerUid = necroIdx >= 0 ? _sim.Units[necroIdx].Id : 0;
                    Vec2 origin = necroIdx >= 0 ? _sim.Units[necroIdx].EffectSpawnPos2D : pg.Origin;
                    SpawnSpellProjectile(spell, origin, pg.Target, ownerUid);
                }

                if (pg.Remaining <= 0)
                {
                    _pendingProjectiles.RemoveAt(i);
                    continue;
                }
            }
            _pendingProjectiles[i] = pg;
        }
    }

    private void DrawSoulOrbs()
    {
        var orbs = _sim.SoulOrbs;
        for (int i = 0; i < orbs.Count; i++)
        {
            var orb = orbs[i];
            var sp = _renderer.WorldToScreen(orb.Position, 0.5f, _camera);

            // Outer purple glow
            float outerR = 6f;
            _spriteBatch.Draw(_pixel, new Vector2(sp.X - outerR, sp.Y - outerR), null,
                Color.FromNonPremultiplied(120, 40, 180, 80), 0f, Vector2.Zero,
                new Vector2(outerR * 2, outerR * 2), SpriteEffects.None, 0f);

            // Inner white bright
            float innerR = 2f;
            _spriteBatch.Draw(_pixel, new Vector2(sp.X - innerR, sp.Y - innerR), null,
                Color.FromNonPremultiplied(255, 255, 255, 200), 0f, Vector2.Zero,
                new Vector2(innerR * 2, innerR * 2), SpriteEffects.None, 0f);
        }
    }

    private void UpdateAnimations(float dt)
    {
        for (int i = 0; i < _sim.Units.Count; i++)
        {
            if (!_sim.Units[i].Alive) continue;

            uint uid = _sim.Units[i].Id;

            if (!_unitAnims.TryGetValue(uid, out var animData))
            {
                // Try to init from defID
                string defID = _sim.Units[i].UnitDefID;
                var unitDef = _gameData.Units.Get(defID);
                if (unitDef?.Sprite == null) continue;
                var atlasId = AtlasDefs.ResolveAtlasName(unitDef.Sprite.AtlasName);
                var spriteData = _atlases[(int)atlasId].GetUnit(unitDef.Sprite.SpriteName);
                if (spriteData == null) continue;

                var ctrl = new AnimController();
                ctrl.Init(spriteData);
                if (_animMeta.Count > 0)
                    ctrl.SetAnimMeta(_animMeta, unitDef.Sprite.SpriteName);
                animData = new UnitAnimData { Ctrl = ctrl, AtlasID = atlasId, RefFrameHeight = 128f, CachedDefID = defID };

                var idleAnim = spriteData.GetAnim("Idle");
                if (idleAnim != null)
                {
                    var kfs = idleAnim.GetAngle(30);
                    if (kfs != null && kfs.Count > 0)
                        animData.RefFrameHeight = kfs[0].Frame.Rect.Height;
                }
                _unitAnims[uid] = animData;
            }

            // --- Jump state machine ---
            if (_sim.Units[i].Jumping)
            {
                var mu = _sim.UnitsMut;
                mu[i].JumpTimer += dt;
                float t = MathF.Min(mu[i].JumpTimer / mu[i].JumpDuration, 1f);
                mu[i].Position = mu[i].JumpStartPos + (mu[i].JumpEndPos - mu[i].JumpStartPos) * t;
                mu[i].Velocity = Vec2.Zero;
                mu[i].JumpHeight = t >= 1f ? 0f : 4f * 0.8f * t * (1f - t);

                float takeoffEnd = mu[i].JumpDuration * 0.35f;
                float landStart = mu[i].JumpDuration - 0.25f;
                var current = animData.Ctrl.CurrentState;
                bool isAttack = mu[i].JumpIsAttack;
                var midAnim = isAttack ? AnimState.JumpAttackSetup : AnimState.JumpLoop;
                var landAnim = isAttack ? AnimState.JumpAttackHit : AnimState.JumpLand;

                if (mu[i].JumpTimer < takeoffEnd)
                {
                    if (current != AnimState.JumpTakeoff) animData.Ctrl.ForceState(AnimState.JumpTakeoff);
                }
                else if (mu[i].JumpTimer < landStart)
                {
                    if (current != midAnim) animData.Ctrl.ForceState(midAnim);
                }
                else
                {
                    if (current != landAnim && t < 1f) animData.Ctrl.ForceState(landAnim);
                    if (isAttack && t >= 1f && !mu[i].JumpAttackFired)
                    {
                        mu[i].JumpAttackFired = true;
                        _sim.ResolvePendingAttack(i); // reuse melee resolution
                    }
                    if (t >= 1f && current != landAnim)
                    {
                        mu[i].Jumping = false;
                        mu[i].JumpHeight = 0f;
                    }
                }
                if (mu[i].JumpTimer > mu[i].JumpDuration + 1.5f)
                {
                    mu[i].Jumping = false;
                    mu[i].JumpHeight = 0f;
                    animData.Ctrl.ForceState(AnimState.Idle);
                }
                animData.Ctrl.Update(dt);
                _unitAnims[uid] = animData;
                continue;
            }

            // Determine animation state
            AnimState targetState;
            if (_sim.Units[i].StandupTimer > 0f)
                targetState = AnimState.Standup;
            else if (_sim.Units[i].Dodging)
                targetState = AnimState.Dodge;
            else if (!_sim.Units[i].PendingAttack.IsNone)
                targetState = AnimState.Attack1;
            else if (_sim.Units[i].HitReacting)
                targetState = AnimState.BlockReact;
            else if (_sim.Units[i].Archetype == AI.ArchetypeRegistry.DeerHerd
                && _sim.Units[i].Routine == 6 /* RoutineFeeding */
                && _sim.Units[i].Subroutine == 1 /* FeedEating */)
                targetState = AnimState.Feeding;
            else if (_sim.Units[i].BlockReacting)
                targetState = AnimState.BlockReact;
            else if (_sim.Units[i].PostAttackTimer > 0f)
                targetState = AnimState.Block;
            else if (_sim.Units[i].InCombat && _sim.Units[i].AttackCooldown > 0f)
            {
                // Pre-roll: start attack animation early so effect time aligns with cooldown expiry
                float cooldownRemaining = _sim.Units[i].AttackCooldown;
                float effectTime = animData.Ctrl.GetEffectTimeSeconds(AnimState.Attack1);
                float animDur = animData.Ctrl.GetTotalDurationSeconds(AnimState.Attack1);
                float lockout = _gameData.Settings.Combat.PostAttackLockout;
                float speed = (animDur > 0f && lockout > 0f) ? MathF.Max(1f, animDur / lockout) : 1f;
                float preRollTime = effectTime > 0f ? effectTime / speed : 0f;

                if (preRollTime > 0f && cooldownRemaining <= preRollTime)
                    targetState = AnimState.Attack1; // start wind-up early
                else
                    targetState = AnimState.Block;
            }
            else
            {
                float speed = _sim.Units[i].Velocity.Length();
                float baseSpeed = _sim.Units[i].Stats.CombatSpeed;
                float walkThreshold = 0.25f;
                float jogThreshold = 4f + baseSpeed / 3f;
                float runThreshold = 6f + 2f * baseSpeed / 3f;

                if (speed <= walkThreshold)
                    targetState = AnimState.Idle;
                else if (speed < jogThreshold)
                    targetState = AnimState.Walk;
                else if (speed < runThreshold)
                    targetState = AnimState.Jog;
                else
                    targetState = AnimState.Run;

                // Debug: log state transitions
                if (animData.Ctrl.CurrentState != targetState && speed > walkThreshold)
                {
                    string defId = _sim.Units[i].UnitDefID ?? "?";
                    DebugLog.Log("anim", $"Unit {i} ({defId}): {animData.Ctrl.CurrentState}->{targetState} speed={speed:F1} base={baseSpeed:F1} walk<{jogThreshold:F1} jog<{runThreshold:F1}");
                }
            }

            // Reverse walk playback when moving backward relative to facing
            float facingRad = _sim.Units[i].FacingAngle * MathF.PI / 180f;
            var facingDir = new Vec2(MathF.Cos(facingRad), MathF.Sin(facingRad));
            var vel = _sim.Units[i].Velocity;
            bool movingBackward = false;
            if (vel.LengthSq() > 0.1f)
            {
                float dot = vel.Normalized().Dot(facingDir);
                movingBackward = dot < -0.3f;
            }
            animData.Ctrl.SetReversePlayback(movingBackward);

            // When transitioning into attack, calculate playback speed to fit within lockout
            if (targetState == AnimState.Attack1 && animData.Ctrl.CurrentState != AnimState.Attack1)
            {
                float lockout = _gameData.Settings.Combat.PostAttackLockout;
                float animDur = animData.Ctrl.GetTotalDurationSeconds(AnimState.Attack1);
                if (animDur > 0f && lockout > 0f)
                    animData.Ctrl.PlaybackSpeed = MathF.Max(1f, animDur / lockout);
            }

            animData.Ctrl.RequestState(targetState);
            animData.Ctrl.Update(dt);

            // Resolve pending attacks at action moment
            if (animData.Ctrl.ConsumeActionMoment() && !_sim.Units[i].PendingAttack.IsNone)
                _sim.ResolvePendingAttack(i);

            _unitAnims[uid] = animData;
        }

        // Update EffectSpawnPos2D / EffectSpawnHeight from weapon tip data
        UpdateEffectSpawnPositions();

        _effectManager.Update(dt);
        UpdateCollectingForagables(dt);
    }

    /// <summary>
    /// Compute each unit's weapon-tip world position for use as spell/effect origin.
    /// Priority: 1) weapon point tip from UnitDef  2) facing-based fallback
    /// </summary>
    private void UpdateEffectSpawnPositions()
    {
        var mu = _sim.UnitsMut;
        for (int i = 0; i < _sim.Units.Count; i++)
        {
            if (!_sim.Units[i].Alive) continue;

            uint uid = _sim.Units[i].Id;
            if (!_unitAnims.TryGetValue(uid, out var animData)) continue;

            string defID = _sim.Units[i].UnitDefID;
            var unitDef = _gameData.Units.Get(defID);
            bool foundWeaponTip = false;

            if (unitDef != null && unitDef.WeaponPoints.Count > 0 && animData.RefFrameHeight > 0f)
            {
                string animName = AnimController.StateToAnimName(animData.Ctrl.CurrentState);
                if (unitDef.WeaponPoints.TryGetValue(animName, out var yawDict))
                {
                    int spriteAngle = animData.Ctrl.ResolveAngle(_sim.Units[i].FacingAngle, out bool flipX);
                    string yawKey = spriteAngle.ToString();
                    if (yawDict.TryGetValue(yawKey, out var frames))
                    {
                        int frameIdx = animData.Ctrl.GetCurrentFrameIndex(_sim.Units[i].FacingAngle);
                        if (frameIdx >= 0 && frameIdx < frames.Count)
                        {
                            var wpf = frames[frameIdx];
                            bool tipSet = wpf.Tip.X != 0f || wpf.Tip.Y != 0f;
                            if (tipSet)
                            {
                                float flipMul = flipX ? -1f : 1f;
                                float worldH = (unitDef.SpriteWorldHeight > 0 ? unitDef.SpriteWorldHeight : 1.8f)
                                               * _sim.Units[i].SpriteScale;
                                float worldScale = worldH / animData.RefFrameHeight;

                                float tipDx = wpf.Tip.X * worldScale * flipMul;
                                mu[i].EffectSpawnPos2D = new Vec2(
                                    _sim.Units[i].Position.X + tipDx,
                                    _sim.Units[i].Position.Y);

                                float unitHeight = _sim.Units[i].JumpHeight;
                                mu[i].EffectSpawnHeight = unitHeight - wpf.Tip.Y * worldScale
                                    * _camera.Zoom / _camera.HeightScale;

                                foundWeaponTip = true;
                            }
                        }
                    }
                }
            }

            if (!foundWeaponTip)
            {
                // Fallback: offset in facing direction
                float facingRad = _sim.Units[i].FacingAngle * MathF.PI / 180f;
                float radius = _sim.Units[i].Radius;
                mu[i].EffectSpawnPos2D = _sim.Units[i].Position
                    + new Vec2(MathF.Cos(facingRad), MathF.Sin(facingRad)) * radius * 1.5f;
                mu[i].EffectSpawnHeight = 0.6f;
            }
        }
    }

    protected override void Draw(GameTime gameTime)
    {
        int screenW = GraphicsDevice.Viewport.Width;
        int screenH = GraphicsDevice.Viewport.Height;

        // --- Main menu ---
        if (_menuState == MenuState.MainMenu)
        {
            GraphicsDevice.Clear(new Color(20, 15, 30));
            _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
            DrawMainMenu(screenW, screenH);
            _spriteBatch.End();
            base.Draw(gameTime);
            return;
        }

        if (_menuState == MenuState.ScenarioList)
        {
            GraphicsDevice.Clear(new Color(20, 15, 30));
            _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
            DrawScenarioList(screenW, screenH);
            _spriteBatch.End();
            base.Draw(gameTime);
            return;
        }

        // Snap camera to pixel grid to prevent subpixel shimmer on ground/sprites
        // X pixel size = 1/Zoom, Y pixel size = 1/(Zoom*YRatio) due to isometric compression
        var realCameraPos = _camera.Position;
        float pixelSizeX = 1f / _camera.Zoom;
        float pixelSizeY = 1f / (_camera.Zoom * _camera.YRatio);
        _camera.Position = new Vec2(
            MathF.Round(realCameraPos.X / pixelSizeX) * pixelSizeX,
            MathF.Round(realCameraPos.Y / pixelSizeY) * pixelSizeY);

        // Set weather renderer context for this frame
        _weatherRenderer.SetContext(_spriteBatch, _pixel, _glowTex, _camera, _gameTime, _gameData, GraphicsDevice);

        // Begin bloom scene capture
        var bloomSettings = _activeScenario?.BloomOverride ?? _gameData.Settings.Bloom;
        bool useBloom = _bloom.IsInitialized && bloomSettings.Enabled;
        if (useBloom)
            _bloom.BeginScene(GraphicsDevice);
        else
            GraphicsDevice.Clear(new Color(30, 30, 40));

        // AlphaBlend with premultiplied-alpha textures (loaded via TextureUtil.LoadPremultiplied)
        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp);

        if (!useBloom)
            GraphicsDevice.Clear(new Color(30, 30, 40));

        // --- Ground ---
        DrawGround();

        // --- Roads ---
        DrawRoads();

        // --- Ground-layer objects (traps — render above dirt, below grass) ---
        DrawGroundLayerObjects();

        // --- Walls ---
        DrawWalls();

        // --- Grass overlay ---
        DrawGrass();

        // --- Shadows ---
        _shadowRenderer.Draw(GraphicsDevice, _spriteBatch, _glowTex, _camera, _renderer, _sim, _gameData, _unitAnims, _atlases, _envSystem);

        // --- Corpses ---
        DrawCorpses();

        // --- Units + Environment objects (merged Y-sort for correct depth) ---
        DrawUnitsAndObjects();

        // --- Projectiles ---
        DrawProjectiles();
        DrawSoulOrbs();

        // --- Potion throw range indicator ---
        if (_activePotionSlot >= 0 && _sim.NecromancerIndex >= 0)
        {
            string potionItemId = _potionSlots[_activePotionSlot];
            var potionDef = FindPotionByItemId(potionItemId);
            if (potionDef != null)
            {
                _debugDraw.EnsurePixel(GraphicsDevice);
                var necroPos = _sim.Units[_sim.NecromancerIndex].Position;
                float range = potionDef.ThrowRange + 1f;
                _debugDraw.DrawCircle(_spriteBatch, _renderer, _camera,
                    necroPos, range, new Color(180, 200, 100, 100), 48);
            }
        }

        // --- Rain (world-space, depth-sorted with scene objects) ---
        _weatherRenderer.DrawRain(screenW, screenH);

        _spriteBatch.End();

        // --- Additive blend pass (effects, lightning) ---
        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.Additive, SamplerState.LinearClamp);
        DrawEffects();
        _lightningRenderer.SetGameTime(_gameTime);
        _lightningRenderer.Draw();

        // Bloom debug: draw test HDR shapes (multiple additive layers to exceed 1.0)
        if (_activeScenario is Scenario.Scenarios.BloomDebugScenario bloomDebug && bloomDebug.DrawTestShapes)
        {
            foreach (var (wx, wy, sz, col, label) in Scenario.Scenarios.BloomDebugScenario.TestShapes)
            {
                var screenPos = _renderer.WorldToScreen(new Vec2(wx, wy), 0f, _camera);
                int pixSz = (int)(sz * _camera.Zoom);
                var rect = new Rectangle((int)screenPos.X - pixSz / 2, (int)screenPos.Y - pixSz / 2, pixSz, pixSz);

                // Draw the shape multiple times additively to push values above 1.0
                int layers = label.Contains("3x") ? 5 : 1;
                for (int l = 0; l < layers; l++)
                    _spriteBatch.Draw(_pixel, rect, col);
            }
        }

        _spriteBatch.End();

        // --- Alpha blend pass (collecting foragables + damage numbers on top) ---
        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp);
        DrawCollectingForagables();
        DrawDamageNumbers();
        _spriteBatch.End();

        // End bloom and composite
        if (useBloom)
            _bloom.EndScene(GraphicsDevice, _spriteBatch, bloomSettings);

        // --- Collision debug overlay (after world, before HUD) ---
        if (_collisionDebugMode != CollisionDebugMode.Off)
        {
            _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
            _debugDraw.DrawCollisionDebug(_spriteBatch, GraphicsDevice, _sim, _camera, _renderer,
                _collisionDebugMode, _envSystem, _sim.Pathfinder);
            _spriteBatch.End();
        }

        // --- Gameplay debug overlay (F7) ---
        if (_gameplayDebugMode > 0)
        {
            _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
            if (_gameplayDebugMode == 1)
                DrawHordeDebug();
            else if (_gameplayDebugMode == 2)
                DrawUnitInfoDebug();
            _spriteBatch.End();
        }

        // --- Map editor: Alt shows object names ---
        if (_menuState == MenuState.MapEditor && Keyboard.GetState().IsKeyDown(Keys.LeftAlt))
        {
            _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
            for (int oi = 0; oi < _envSystem.ObjectCount; oi++)
            {
                var obj = _envSystem.GetObject(oi);
                var def = _envSystem.GetDef(obj.DefIndex);
                var sp = _renderer.WorldToScreen(new Vec2(obj.X, obj.Y), 0f, _camera);
                // Only draw if on screen
                if (sp.X > -100 && sp.X < screenW + 100 && sp.Y > -100 && sp.Y < screenH + 100)
                {
                    string label = !string.IsNullOrEmpty(def.Name) ? def.Name : def.Id;
                    if (!string.IsNullOrEmpty(label))
                    {
                        var textSize = _smallFont != null ? _smallFont.MeasureString(label) : new Vector2(label.Length * 6, 12);
                        var textPos = new Vector2(sp.X - textSize.X * 0.5f, sp.Y + 4);
                        // Dark background for readability
                        _spriteBatch.Draw(_pixel, new Rectangle((int)textPos.X - 2, (int)textPos.Y - 1, (int)textSize.X + 4, (int)textSize.Y + 2), new Color(0, 0, 0, 160));
                        if (_smallFont != null)
                            _spriteBatch.DrawString(_smallFont, label, textPos, new Color(220, 220, 255));
                    }
                }
            }
            _spriteBatch.End();
        }

        // Restore real camera position (smooth, for input/HUD)
        _camera.Position = realCameraPos;

        // --- HUD (drawn after bloom so it's not affected) ---
        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);

        // --- Weather effects (fog/haze/brightness — rain draws in scene pass) ---
        _weatherRenderer.DrawFog(screenW, screenH);

        bool showUI = _activeScenario == null || _activeScenario.WantsUI;
        if (showUI)
            DrawHUD(screenW, screenH);

        // Inventory UI (widget-based, drawn over HUD)
        if (showUI)
            _inventoryUI.Draw();

        // Building menu UI (widget-based)
        if (showUI)
        {
            _buildingMenuUI.DrawMenu();
            _craftingMenu.Draw();

            // Ghost preview for building placement
            if (_buildingMenuUI.IsPlacementActive)
            {
                var ms = Mouse.GetState();
                Vec2 mw = _camera.ScreenToWorld(new Vector2(ms.X, ms.Y), screenW, screenH);
                var sp = _renderer.WorldToScreen(mw, 0f, _camera);
                _buildingMenuUI.DrawGhostPreview(_spriteBatch, _pixel, mw, sp, _camera, _renderer);
            }
        }

        if (_gameOver && showUI)
            DrawGameOver(screenW, screenH);
        else if (_menuState == MenuState.PauseMenu)
            DrawPauseMenu(screenW, screenH);
        else if (_menuState == MenuState.Settings)
            _settingsWindow.Draw(screenW, screenH);
        if (_menuState == MenuState.UnitEditor)
        {
            _unitEditor.Draw(screenW, screenH, gameTime);
            // U23: Handle close request from the editor's [X] button
            if (_unitEditor.WantsClose)
            {
                _unitEditor.WantsClose = false;
                _menuState = MenuState.None;
            }
        }
        else if (_menuState == MenuState.SpellEditor)
        {
            _spellEditor.Draw(screenW, screenH, gameTime);
            if (_spellEditor.WantsClose)
            {
                _spellEditor.WantsClose = false;
                _menuState = MenuState.None;
            }
        }
        else if (_menuState == MenuState.MapEditor)
        {
            _mapEditor.Draw(screenW, screenH);
        }
        else if (_menuState == MenuState.UIEditor)
        {
            _uiEditor.Draw(screenW, screenH);
        }
        else if (_menuState == MenuState.ItemEditor)
        {
            _itemEditor.Draw(screenW, screenH, gameTime);
            if (_itemEditor.WantsClose)
            {
                _itemEditor.WantsClose = false;
                _menuState = MenuState.None;
            }
        }

        // Draw color picker popup overlay (must be after all editor drawing, on top)
        if (_menuState == MenuState.UnitEditor || _menuState == MenuState.SpellEditor)
            _editorUi.DrawColorPickerPopup();

        if (_font != null && showUI)
        {
            string dbg = $"Zoom:{_camera.Zoom:F0} Pos:({_camera.Position.X:F0},{_camera.Position.Y:F0}) Speed:{_timeScale:F1}x FPS:{(_rawDt > 0 ? 1f / _rawDt : 0):F0}";
            DrawText(_smallFont, dbg, new Vector2(10, screenH - 18), new Color(120, 120, 120));
        }

        // Show collision debug mode label when active
        if (_collisionDebugMode != CollisionDebugMode.Off && _smallFont != null)
        {
            string label = $"[F8] Collision Debug: {DebugDraw.GetModeLabel(_collisionDebugMode)}";
            DrawText(_smallFont, label, new Vector2(10, screenH - 36), new Color(255, 200, 80));
        }

        _spriteBatch.End();

        base.Draw(gameTime);

        // Handle deferred screenshots from scenarios
        if (_activeScenario?.DeferredScreenshot != null)
        {
            ScenarioScreenshot.TakeScreenshot(GraphicsDevice, _activeScenario.DeferredScreenshot);
            _activeScenario.DeferredScreenshot = null;
        }
    }

    private void DrawGround()
    {
        int worldW = _groundSystem.WorldW > 0 ? _groundSystem.WorldW : WorldSize;
        int worldH = _groundSystem.WorldH > 0 ? _groundSystem.WorldH : WorldSize;

        // Try shader-based ground rendering
        if (_groundEffect != null && _groundVertexMapTex != null && _groundSystem.TypeCount > 0)
        {
            DrawGroundShader(worldW, worldH);
            return;
        }

        // Fallback: per-tile texture rendering
        float viewLeft = _camera.Position.X - _renderer.ScreenW / (2f * _camera.Zoom) - 1;
        float viewRight = _camera.Position.X + _renderer.ScreenW / (2f * _camera.Zoom) + 1;
        float viewTop = _camera.Position.Y - _renderer.ScreenH / (_camera.Zoom * _camera.YRatio) - 1;
        float viewBottom = _camera.Position.Y + _renderer.ScreenH / (_camera.Zoom * _camera.YRatio) + 1;

        int minX = Math.Max(0, (int)viewLeft);
        int maxX = Math.Min(worldW - 1, (int)viewRight);
        int minY = Math.Max(0, (int)viewTop);
        int maxY = Math.Min(worldH - 1, (int)viewBottom);

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                var sp = _renderer.WorldToScreen(new Vec2(x, y), 0f, _camera);
                float tileW = _camera.Zoom;
                float tileH = _camera.Zoom * _camera.YRatio;

                byte groundType = _groundSystem.GetVertex(x, y);
                var tex = _groundSystem.GetTexture(groundType);

                if (tex != null)
                {
                    // Tile the ground texture — use float dest rect via Vector2 position + scale
                    float texScale = 8f;
                    int srcX = (int)((x % texScale) / texScale * tex.Width);
                    int srcY = (int)((y % texScale) / texScale * tex.Height);
                    int srcW = (int)(tex.Width / texScale);
                    int srcH = (int)(tex.Height / texScale);
                    if (srcW < 1) srcW = 1;
                    if (srcH < 1) srcH = 1;
                    var srcRect = new Rectangle(srcX, srcY, srcW, srcH);
                    // Use float-based destination to avoid jitter
                    var destPos = new Vector2(sp.X, sp.Y);
                    var destScale = new Vector2((tileW + 0.5f) / srcW, (tileH + 0.5f) / srcH);
                    _spriteBatch.Draw(tex, destPos, srcRect, Color.White, 0f, Vector2.Zero, destScale, SpriteEffects.None, 0f);
                }
                else
                {
                    var color = groundType switch
                    {
                        0 => new Color(55, 95, 45),
                        1 => new Color(90, 75, 50),
                        2 => new Color(70, 65, 55),
                        _ => new Color(55, 95, 45)
                    };
                    _spriteBatch.Draw(_pixel, sp, null, color, 0f, Vector2.Zero, new Vector2(tileW + 0.5f, tileH + 0.5f), SpriteEffects.None, 0f);
                }
            }
        }
    }

    private void DrawGroundShader(int worldW, int worldH)
    {
        // End the current SpriteBatch and start a new one with the ground shader
        _spriteBatch.End();

        // Set shader parameters
        _groundEffect!.Parameters["CameraPos"]?.SetValue(new Vector2(_camera.Position.X, _camera.Position.Y));
        _groundEffect.Parameters["Zoom"]?.SetValue(_camera.Zoom);
        _groundEffect.Parameters["YRatio"]?.SetValue(_camera.YRatio);
        _groundEffect.Parameters["ScreenSize"]?.SetValue(new Vector2(_renderer.ScreenW, _renderer.ScreenH));
        _groundEffect.Parameters["WorldSize"]?.SetValue(new Vector2(_groundSystem.VertexW, _groundSystem.VertexH));
        _groundEffect.Parameters["TypeWarpStrength"]?.SetValue(_groundSystem.TypeWarpStrength);
        _groundEffect.Parameters["UvWarpAmp"]?.SetValue(_groundSystem.UvWarpAmp);
        _groundEffect.Parameters["UvWarpFreq"]?.SetValue(_groundSystem.UvWarpFreq);

        // Bind ground type textures via Effect.Parameters (named texture params, not register slots)
        string[] texParamNames = { "GroundTexture0", "GroundTexture1", "GroundTexture2" };
        for (int i = 0; i < Math.Min(_groundSystem.TypeCount, 3); i++)
        {
            var tex = _groundSystem.GetTexture(i);
            if (tex != null)
                _groundEffect.Parameters[texParamNames[i]]?.SetValue(tex);
        }

        _spriteBatch.Begin(SpriteSortMode.Immediate, BlendState.Opaque, SamplerState.PointClamp,
            null, null, _groundEffect);

        // SpriteBatch.Draw binds _groundVertexMapTex to slot 0 (= TilemapSampler)
        _spriteBatch.Draw(_groundVertexMapTex!, new Rectangle(0, 0, _renderer.ScreenW, _renderer.ScreenH), Color.White);
        _spriteBatch.End();

        // Resume normal SpriteBatch (premultiplied alpha)
        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp);
    }

    private void DrawGrass()
    {
        _grassRenderer.Draw(
            GraphicsDevice,
            _spriteBatch,
            _camera,
            _renderer.ScreenW, _renderer.ScreenH,
            _grassMap, _grassW, _grassH,
            _grassBaseColors, _grassTipColors,
            _gameData.Settings.Grass,
            _gameTime);
    }

    private void DrawRoads()
    {
        var roads = _roadSystem.Roads;
        var junctions = _roadSystem.Junctions;
        if (roads.Count == 0 && junctions.Count == 0) return;

        var roadColor = new Color(100, 90, 80);

        // Draw road segments using Catmull-Rom interpolation
        foreach (var road in roads)
        {
            var pts = road.Points;
            if (pts.Count < 2) continue;

            for (int seg = 0; seg < pts.Count - 1; seg++)
            {
                // Get the 4 control points for Catmull-Rom (clamping at edges)
                var p0 = pts[Math.Max(0, seg - 1)].Position;
                var p1 = pts[seg].Position;
                var p2 = pts[seg + 1].Position;
                var p3 = pts[Math.Min(pts.Count - 1, seg + 2)].Position;

                float w1 = pts[seg].Width;
                float w2 = pts[seg + 1].Width;

                const int subdivisions = 10;
                Vec2 prev = p1;
                float prevW = w1;

                for (int s = 1; s <= subdivisions; s++)
                {
                    float t = s / (float)subdivisions;
                    var cur = RoadSystem.CatmullRom(p0, p1, p2, p3, t);
                    float curW = w1 + (w2 - w1) * t;

                    // Convert to screen space
                    var screenA = _renderer.WorldToScreen(prev, 0f, _camera);
                    var screenB = _renderer.WorldToScreen(cur, 0f, _camera);

                    float dx = screenB.X - screenA.X;
                    float dy = screenB.Y - screenA.Y;
                    float segLen = MathF.Sqrt(dx * dx + dy * dy);
                    if (segLen < 0.1f) { prev = cur; prevW = curW; continue; }

                    float angle = MathF.Atan2(dy, dx);
                    float avgWidth = (prevW + curW) * 0.5f * _camera.Zoom;

                    _spriteBatch.Draw(_pixel, screenA, null, roadColor,
                        angle, new Vector2(0, 0.5f), new Vector2(segLen + 1f, avgWidth), SpriteEffects.None, 0f);

                    prev = cur;
                    prevW = curW;
                }
            }
        }

        // Draw junctions as filled circle approximations
        foreach (var junc in junctions)
        {
            var sp = _renderer.WorldToScreen(junc.Position, 0f, _camera);
            float radius = junc.Radius * _camera.Zoom;
            int r = Math.Max(2, (int)radius);

            // Draw circle as a series of horizontal lines
            for (int dy = -r; dy <= r; dy++)
            {
                float halfW = MathF.Sqrt(r * r - dy * dy);
                int x0 = (int)(sp.X - halfW);
                int w = (int)(halfW * 2f);
                if (w < 1) w = 1;
                _spriteBatch.Draw(_pixel, new Rectangle(x0, (int)sp.Y + dy, w, 1), roadColor);
            }
        }
    }

    private void DrawWalls()
    {
        if (_wallSystem.Width == 0 || _wallSystem.Height == 0 || _wallSystem.DefCount == 0) return;

        // View culling bounds (same approach as DrawGround)
        float viewLeft = _camera.Position.X - _renderer.ScreenW / (2f * _camera.Zoom) - 1;
        float viewRight = _camera.Position.X + _renderer.ScreenW / (2f * _camera.Zoom) + 1;
        float viewTop = _camera.Position.Y - _renderer.ScreenH / (_camera.Zoom * _camera.YRatio) - 1;
        float viewBottom = _camera.Position.Y + _renderer.ScreenH / (_camera.Zoom * _camera.YRatio) + 1;

        int minX = Math.Max(0, (int)viewLeft);
        int maxX = Math.Min(_wallSystem.Width - 1, (int)viewRight);
        int minY = Math.Max(0, (int)viewTop);
        int maxY = Math.Min(_wallSystem.Height - 1, (int)viewBottom);

        float tileW = _camera.Zoom;
        float tileH = _camera.Zoom * _camera.YRatio;

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                if (!_wallSystem.IsAlive(x, y)) continue;

                byte wallType = _wallSystem.GetWallType(x, y);
                if (wallType == 0 || wallType > _wallSystem.DefCount) continue;

                var def = _wallSystem.Defs[wallType - 1];
                var sp = _renderer.WorldToScreen(new Vec2(x, y), 0f, _camera);

                // Draw colored rectangle as placeholder (using def's Color)
                // Make wall tiles slightly taller to give a wall appearance
                float wallH = tileH * 1.5f;
                _spriteBatch.Draw(_pixel, new Vector2(sp.X, sp.Y - wallH + tileH), null,
                    def.Color, 0f, Vector2.Zero,
                    new Vector2(tileW + 0.5f, wallH), SpriteEffects.None, 0f);

                // Draw a darker top edge for depth effect
                var darkColor = new Color(
                    (byte)(def.Color.R * 0.6f),
                    (byte)(def.Color.G * 0.6f),
                    (byte)(def.Color.B * 0.6f),
                    def.Color.A);
                _spriteBatch.Draw(_pixel, new Vector2(sp.X, sp.Y - wallH + tileH), null,
                    darkColor, 0f, Vector2.Zero,
                    new Vector2(tileW + 0.5f, 2f), SpriteEffects.None, 0f);
            }
        }
    }

    private void DrawCorpses()
    {
        foreach (var corpse in _sim.Corpses)
        {
            var unitDef = _gameData.Units.Get(corpse.UnitDefID);
            if (unitDef?.Sprite == null) continue;
            var atlasId = AtlasDefs.ResolveAtlasName(unitDef.Sprite.AtlasName);
            var atlas = _atlases[(int)atlasId];
            if (!atlas.IsLoaded) continue;

            // Get or create corpse anim controller
            if (!_corpseAnims.TryGetValue(corpse.CorpseID, out var cad))
            {
                var spriteData = atlas.GetUnit(unitDef.Sprite.SpriteName);
                if (spriteData == null) continue;
                var ctrl = new AnimController();
                ctrl.Init(spriteData);
                ctrl.RequestState(AnimState.Death);

                float refH = 128f;
                var idle = spriteData.GetAnim("Idle");
                if (idle != null) { var kfs = idle.GetAngle(30); if (kfs != null && kfs.Count > 0) refH = kfs[0].Frame.Rect.Height; }

                cad = new UnitAnimData { Ctrl = ctrl, AtlasID = atlasId, RefFrameHeight = refH, CachedDefID = corpse.UnitDefID };
                _corpseAnims[corpse.CorpseID] = cad;
            }

            if (!cad.Ctrl.IsAnimFinished) cad.Ctrl.Update(1f / 60f); // fixed timestep for corpse anims

            int alphaInt = 255;
            if (corpse.Dissolving)
            {
                float t = corpse.DissolveTimer / 2f;
                float a = 255f * (1f - t);
                if ((int)(corpse.DissolveTimer * 8f) % 2 == 0) a *= 0.3f;
                alphaInt = (int)MathUtil.Clamp(a, 0f, 255f);
            }
            byte alpha = (byte)alphaInt;

            var fr = cad.Ctrl.GetCurrentFrame(corpse.FacingAngle);
            if (fr.Frame != null)
            {
                float worldH = (unitDef.SpriteWorldHeight > 0 ? unitDef.SpriteWorldHeight : 1.8f) * corpse.SpriteScale;
                float pixelH = worldH * _camera.Zoom;
                float scale = pixelH / cad.RefFrameHeight;

                var sp = _renderer.WorldToScreen(corpse.Position, 0f, _camera);
                // Highlight dragged corpse with brighter tint
                Color corpseTint;
                if (corpse.DraggedByUnitID != GameConstants.InvalidUnit)
                {
                    float af = alpha / 255f;
                    corpseTint = new Color((byte)(Math.Min(255, alpha + 80) * af), (byte)(alpha / 2 * af), (byte)(alpha * af), alpha);
                }
                else
                    corpseTint = new Color(alpha, alpha, alpha, alpha);
                DrawSpriteFrame(atlas, fr.Frame.Value, sp, scale, fr.FlipX, corpseTint);
            }
        }
    }

    // ═══════════════════════════════════════
    //  Gameplay Debug Visualizations (F7)
    // ═══════════════════════════════════════

    private void DrawHordeDebug()
    {
        _debugDraw.EnsurePixel(GraphicsDevice);
        int screenW = GraphicsDevice.Viewport.Width, screenH = GraphicsDevice.Viewport.Height;
        var horde = _sim.Horde;
        var settings = horde.Settings;

        // Formation circle
        _debugDraw.DrawCircle(_spriteBatch, _renderer, _camera,
            horde.CircleCenter, settings.CircleRadius, new Color(100, 200, 100, 120));

        // Engagement range
        _debugDraw.DrawCircle(_spriteBatch, _renderer, _camera,
            horde.CircleCenter, settings.EngagementRange, new Color(255, 200, 80, 80));

        // Leash radius
        _debugDraw.DrawCircle(_spriteBatch, _renderer, _camera,
            horde.CircleCenter, settings.LeashRadius, new Color(255, 80, 80, 60));

        // Formation facing arrow
        var facingDir = new Vec2(MathF.Cos(horde.CircleFacing), MathF.Sin(horde.CircleFacing));
        var centerSp = _camera.WorldToScreen(horde.CircleCenter, 0f, screenW, screenH);
        var facingEnd = _camera.WorldToScreen(horde.CircleCenter + facingDir * 3f, 0f, screenW, screenH);
        _debugDraw.DrawArrow(_spriteBatch, centerSp, facingEnd, new Color(100, 255, 100, 200));

        // Unit slots and connections
        foreach (var unit in horde.HordeUnits)
        {
            int unitIdx = -1;
            for (int j = 0; j < _sim.Units.Count; j++)
                if (_sim.Units[j].Id == unit.UnitID) { unitIdx = j; break; }
            if (unitIdx < 0 || !_sim.Units[unitIdx].Alive) continue;

            var unitPos = _sim.Units[unitIdx].Position;
            var unitSp = _camera.WorldToScreen(unitPos, 0f, screenW, screenH);

            // Line to slot target
            if (horde.GetTargetPosition(unit.UnitID, out Vec2 slotPos))
            {
                var slotSp = _camera.WorldToScreen(slotPos, 0f, screenW, screenH);
                // Slot marker (small cross)
                _spriteBatch.Draw(_pixel, new Rectangle((int)slotSp.X - 3, (int)slotSp.Y, 7, 1), new Color(100, 200, 100, 150));
                _spriteBatch.Draw(_pixel, new Rectangle((int)slotSp.X, (int)slotSp.Y - 3, 1, 7), new Color(100, 200, 100, 150));
                // Line from unit to slot
                Color lineCol = unit.State switch
                {
                    HordeUnitState.Following => new Color(100, 200, 100, 100),
                    HordeUnitState.Engaged => new Color(255, 80, 80, 150),
                    HordeUnitState.Chasing => new Color(255, 200, 80, 150),
                    HordeUnitState.Returning => new Color(80, 150, 255, 150),
                    _ => new Color(150, 150, 150, 100)
                };
                _debugDraw.DrawLine(_spriteBatch, unitSp, slotSp, lineCol);
            }

            // State label
            if (_smallFont != null)
            {
                string stateLabel = unit.State.ToString();
                _spriteBatch.DrawString(_smallFont, stateLabel,
                    new Vector2(unitSp.X + 8, unitSp.Y - 16), new Color(200, 200, 200, 200));
            }
        }

        // Mode label
        if (_smallFont != null)
            _spriteBatch.DrawString(_smallFont, "[F7] Debug: Horde",
                new Vector2(10, 26), new Color(100, 255, 100, 200));
    }

    private void DrawUnitInfoDebug()
    {
        _debugDraw.EnsurePixel(GraphicsDevice);
        int screenW = GraphicsDevice.Viewport.Width, screenH = GraphicsDevice.Viewport.Height;

        for (int i = 0; i < _sim.Units.Count; i++)
        {
            if (!_sim.Units[i].Alive) continue;

            var pos = _sim.Units[i].Position;
            var sp = _camera.WorldToScreen(pos, 0f, screenW, screenH);
            float speed = _sim.Units[i].Velocity.Length();
            float maxSpeed = _sim.Units[i].MaxSpeed;

            // Line to target
            var target = _sim.Units[i].Target;
            if (target.IsUnit)
            {
                int tIdx = -1;
                for (int j = 0; j < _sim.Units.Count; j++)
                    if (_sim.Units[j].Id == target.UnitID) { tIdx = j; break; }
                if (tIdx >= 0 && _sim.Units[tIdx].Alive)
                {
                    var tSp = _camera.WorldToScreen(_sim.Units[tIdx].Position, 0f, screenW, screenH);
                    _debugDraw.DrawLine(_spriteBatch, sp, tSp, new Color(255, 100, 100, 100));
                }
            }

            // Velocity vector
            if (speed > 0.1f)
            {
                var velEnd = _camera.WorldToScreen(pos + _sim.Units[i].Velocity.Normalized() * 1.5f, 0f, screenW, screenH);
                _debugDraw.DrawArrow(_spriteBatch, sp, velEnd, new Color(80, 200, 255, 150));
            }

            if (_smallFont == null) continue;

            // Get animation state
            string animLabel = "?";
            uint uid = _sim.Units[i].Id;
            if (_unitAnims.TryGetValue(uid, out var animData))
                animLabel = animData.Ctrl.CurrentState.ToString();

            string aiLabel = "?";
            var aiArchetypeId = _sim.Units[i].Archetype;
            aiLabel = AI.ArchetypeRegistry.GetName(aiArchetypeId);
            var aiArchetype = AI.ArchetypeRegistry.Get(aiArchetypeId);

            if (aiArchetype != null) {
                
                string routineLabel = aiArchetype.GetRoutineName(_sim.Units[i].Routine);

                aiLabel = $"{aiLabel} - {routineLabel}";
            }
                

            // Text above head: AI label + velocity | anim state | max speed
            string info = $"{aiLabel}\nv:{speed:F1} {animLabel} ms:{maxSpeed:F1}";
            var textPos = new Vector2(sp.X - info.Length * 3, sp.Y - 28);
            _spriteBatch.DrawString(_smallFont, info, textPos, new Color(255, 255, 200, 220));
        }

        // Mode label
        if (_smallFont != null)
            _spriteBatch.DrawString(_smallFont, "[F7] Debug: Unit Info",
                new Vector2(10, 26), new Color(80, 200, 255, 200));
    }

    private static string GetRoutineName(byte archetype, byte routine) => archetype switch
    {
        AI.ArchetypeRegistry.WolfPack => routine switch { 0 => "IdleRoam", 1 => "Sleep", 2 => "Fight", _ => $"R{routine}" },
        AI.ArchetypeRegistry.DeerHerd => routine switch { 0 => "IdleRoam", 1 => "Sleep", 2 => "Alert", 3 => "Flee", 4 => "Calm", 5 => "FightBack", _ => $"R{routine}" },
        AI.ArchetypeRegistry.HordeMinion => routine switch { 0 => "Follow", 1 => "Chase", 2 => "Engage", 3 => "Return", _ => $"R{routine}" },
        AI.ArchetypeRegistry.PatrolSoldier or AI.ArchetypeRegistry.GuardStationary or AI.ArchetypeRegistry.ArmyUnit =>
            routine switch { 0 => "Idle", 1 => "Alert", 2 => "Combat", 3 => "Return", _ => $"R{routine}" },
        AI.ArchetypeRegistry.ArcherUnit or AI.ArchetypeRegistry.CasterUnit =>
            routine switch { 0 => "Idle", 1 => "Alert", 2 => "Combat", 3 => "Return", _ => $"R{routine}" },
        _ => $"R{routine}"
    };

    private static string GetSubroutineName(byte archetype, byte routine, byte sub) => archetype switch
    {
        AI.ArchetypeRegistry.WolfPack when routine == 2 => sub switch { 0 => "Approach", 1 => "Strike", 2 => "Disengage", 3 => "Cooldown", _ => $"S{sub}" },
        AI.ArchetypeRegistry.WolfPack => sub switch { 0 => "Walk", 1 => "Idle", _ => $"S{sub}" },
        AI.ArchetypeRegistry.DeerHerd when routine == 5 => sub switch { 0 => "Chase", 1 => "Attack", _ => $"S{sub}" },
        AI.ArchetypeRegistry.DeerHerd => sub switch { 0 => "Walk", 1 => "Idle", _ => $"S{sub}" },
        AI.ArchetypeRegistry.HordeMinion => $"S{sub}",
        AI.ArchetypeRegistry.PatrolSoldier when routine == 0 => sub switch { 0 => "Walking", 1 => "Waiting", _ => $"S{sub}" },
        _ when routine == 2 => sub switch { 0 => "Chase", 1 => "Attack", _ => $"S{sub}" },
        _ => sub switch { 0 => "Walk", 1 => "Idle", _ => $"S{sub}" },
    };

    /// <summary>Draw ground-layer objects (traps) — above dirt, below grass/units.</summary>
    private void DrawGroundLayerObjects()
    {
        for (int i = 0; i < _envSystem.ObjectCount; i++)
        {
            if (!_envSystem.IsObjectVisible(i)) continue;
            var obj = _envSystem.Objects[i];
            var def = _envSystem.Defs[obj.DefIndex];
            if (def.Category != "Traps") continue;
            DrawSingleEnvObject(i);
        }
    }

    // Sortable item for merged unit+object depth sorting
    private readonly List<DepthItem> _depthItems = new(256); // reused each frame

    private struct DepthItem : IComparable<DepthItem>
    {
        public float Y;
        public bool IsUnit;
        public int Index;
        public int CompareTo(DepthItem other) => Y.CompareTo(other.Y);
    }

    private void DrawUnitsAndObjects()
    {
        // View culling bounds
        float viewMargin = 20f;
        float viewLeft = _camera.Position.X - _renderer.ScreenW / (2f * _camera.Zoom) - viewMargin;
        float viewRight = _camera.Position.X + _renderer.ScreenW / (2f * _camera.Zoom) + viewMargin;
        float viewTop = _camera.Position.Y - _renderer.ScreenH / (_camera.Zoom * _camera.YRatio) - viewMargin;
        float viewBottom = _camera.Position.Y + _renderer.ScreenH / (_camera.Zoom * _camera.YRatio) + viewMargin;

        // Build merged sort list (reuse cached list to avoid per-frame allocation)
        _depthItems.Clear();
        var items = _depthItems;

        // Add units
        for (int i = 0; i < _sim.Units.Count; i++)
            if (_sim.Units[i].Alive)
                items.Add(new DepthItem { Y = _sim.Units[i].Position.Y, IsUnit = true, Index = i });

        // Add environment objects (with view culling, skip collected foragables, skip ground-layer objects)
        for (int i = 0; i < _envSystem.ObjectCount; i++)
        {
            if (!_envSystem.IsObjectVisible(i)) continue;
            var obj = _envSystem.Objects[i];
            var def = _envSystem.Defs[obj.DefIndex];
            if (def.Category == "Traps") continue; // drawn in ground layer pass
            if (obj.X < viewLeft || obj.X > viewRight || obj.Y < viewTop || obj.Y > viewBottom)
                continue;
            if (_envSystem.GetDefTexture(obj.DefIndex) == null) continue;
            items.Add(new DepthItem { Y = obj.Y, IsUnit = false, Index = i });
        }

        items.Sort();

        foreach (var item in items)
        {
            if (item.IsUnit)
                DrawSingleUnit(item.Index);
            else
                DrawSingleEnvObject(item.Index);
        }
    }

    private void DrawSingleUnit(int i)
    {
        uint uid = _sim.Units[i].Id;
        if (!_unitAnims.TryGetValue(uid, out var animData)) return;

        var unitDef = _gameData.Units.Get(_sim.Units[i].UnitDefID);
        if (unitDef == null) return;

        var atlas = _atlases[(int)animData.AtlasID];
        if (!atlas.IsLoaded) return;

        var fr = animData.Ctrl.GetCurrentFrame(_sim.Units[i].FacingAngle);
        if (fr.Frame == null) return;

        float worldH = (unitDef.SpriteWorldHeight > 0 ? unitDef.SpriteWorldHeight : 1.8f) * _sim.Units[i].SpriteScale;
        float pixelH = worldH * _camera.Zoom;
        float scale = pixelH / animData.RefFrameHeight;

        Color tint = _sim.Units[i].Faction == Faction.Undead
            ? new Color(190, 210, 190)
            : new Color(210, 195, 185);

        // Apply buff tinting
        foreach (var buff in _sim.Units[i].ActiveBuffs)
        {
            var buffDef = _gameData.Buffs.Get(buff.BuffDefID);
            if (buffDef?.UnitTint != null && buffDef.UnitTint.A > 0)
            {
                var bt = buffDef.UnitTint;
                float blend = bt.A / 255f;
                tint = new Color(
                    (byte)(tint.R * (1f - blend) + bt.R * blend),
                    (byte)(tint.G * (1f - blend) + bt.G * blend),
                    (byte)(tint.B * (1f - blend) + bt.B * blend));
            }
        }

        // Ghost mode: semi-transparent blue-shifted sprite
        if (_sim.Units[i].GhostMode)
            tint = Color.FromNonPremultiplied(
                Math.Min(255, (int)(tint.R * 0.7f + 80)),
                Math.Min(255, (int)(tint.G * 0.7f + 100)),
                Math.Min(255, (int)(tint.B * 0.7f + 120)), 100);

        float heightOffset = _sim.Units[i].JumpHeight;
        var sp = _renderer.WorldToScreen(_sim.Units[i].Position, heightOffset, _camera);

        // Pulsing outline: draw sprite 8 times at directional offsets behind the unit
        DrawUnitPulsingOutline(i, atlas, fr.Frame.Value, sp, scale, fr.FlipX);

        // Ghost mode: subtle blue pulsing outline
        if (_sim.Units[i].GhostMode)
            DrawGhostOutline(atlas, fr.Frame.Value, sp, scale, fr.FlipX);

        DrawSpriteFrame(atlas, fr.Frame.Value, sp, scale, fr.FlipX, tint);
        DrawHPBar(i, sp);

        // --- Feature 1: Weapon point text during attack ---
        if (!_sim.Units[i].PendingAttack.IsNone)
        {
            var stats = _sim.Units[i].Stats;
            string weaponName = stats.MeleeWeapons.Count > 0 ? stats.MeleeWeapons[0].Name : "Unarmed";
            if (!string.IsNullOrEmpty(weaponName) && _smallFont != null)
            {
                var weaponPos = new Vector2(sp.X + 10, sp.Y - 55);
                DrawText(_smallFont, weaponName, weaponPos, Color.FromNonPremultiplied(255, 220, 140, 220));
            }
        }

        // --- Feature 2: Buff indicator dots above HP bar ---
        if (_sim.Units[i].ActiveBuffs.Count > 0)
        {
            float dotStartX = sp.X - (_sim.Units[i].ActiveBuffs.Count * 5f) / 2f;
            float dotY = sp.Y - 52f;
            int dotIdx = 0;
            foreach (var buff in _sim.Units[i].ActiveBuffs)
            {
                var buffDef = _gameData.Buffs.Get(buff.BuffDefID);
                Color dotColor;
                if (buffDef?.UnitTint != null && buffDef.UnitTint.A > 0)
                    dotColor = Color.FromNonPremultiplied(buffDef.UnitTint.R, buffDef.UnitTint.G, buffDef.UnitTint.B, 220);
                else
                    dotColor = Color.FromNonPremultiplied(100, 200, 100, 220);
                _spriteBatch.Draw(_pixel, new Rectangle((int)(dotStartX + dotIdx * 5), (int)dotY, 4, 4), dotColor);
                dotIdx++;
            }
        }
    }

    private void DrawSingleEnvObject(int i)
    {
        var obj = _envSystem.Objects[i];
        var def = _envSystem.Defs[obj.DefIndex];
        var tex = _envSystem.GetObjectTexture(i, out float alpha);
        if (tex == null) return;

        // Always compute scale from the main def texture so trap sprites render at same size
        var mainTex = _envSystem.GetDefTexture(obj.DefIndex);
        float refHeight = mainTex != null ? mainTex.Height : tex.Height;

        float worldH = def.SpriteWorldHeight * obj.Scale * def.Scale;
        float pixelH = worldH * _camera.Zoom;
        float scale = pixelH / refHeight;

        var screenPos = _renderer.WorldToScreen(new Vec2(obj.X, obj.Y), 0f, _camera);
        var origin = new Vector2(def.PivotX * tex.Width, def.PivotY * tex.Height);

        float rotation = 0f;
        Color tint = alpha >= 1f ? Color.White : new Color(alpha, alpha, alpha, alpha);

        // Foragable proximity effects
        if (def.IsForagable && _sim.NecromancerIndex >= 0)
        {
            Vec2 objPos = new Vec2(obj.X, obj.Y);
            Vec2 necroPos = _sim.Units[_sim.NecromancerIndex].Position;
            float dist = (objPos - necroPos).Length();

            if (dist < ForagableWiggleRange)
            {
                // Wiggle: sinusoidal rotation, intensifies with proximity
                float proximity = 1f - (dist / ForagableWiggleRange); // 0 at edge, 1 at necro
                float wiggleAngle = MathF.Sin(_gameTime * 8f + obj.Seed * 10f) * 0.08f * proximity;
                rotation = wiggleAngle;

                // Scale pulse: subtle breathe effect
                float pulse = 1f + MathF.Sin(_gameTime * 4f + obj.Seed * 5f) * 0.03f * proximity;
                scale *= pulse;
            }

            // Mouse hover highlight: brighten + enlarge when cursor is over the object
            var mouseWorld = _renderer.ScreenToWorld(new Vector2(Mouse.GetState().X, Mouse.GetState().Y), _camera);
            float mouseDist = (objPos - new Vec2(mouseWorld.X, mouseWorld.Y)).Length();
            if (mouseDist < 1.2f && dist < ForagableWiggleRange)
            {
                scale *= 1.1f;
                tint = new Color(1.3f, 1.3f, 1.3f, 1f); // brighten
            }
        }

        _spriteBatch.Draw(tex, screenPos, null, tint, rotation, origin, scale, SpriteEffects.None, 0f);
    }

    private void DrawSpriteFrame(SpriteAtlas atlas, SpriteFrame frame, Vector2 screenPos,
                                  float scale, bool flipX, Color tint)
    {
        if (atlas.Texture == null) return;

        float pivotX = flipX ? (1f - frame.PivotX) : frame.PivotX;
        // Spritemeta pivots use bottom-left origin — Y needs to be flipped for top-left rendering
        float pivotY = 1f - frame.PivotY;

        var origin = new Vector2(pivotX * frame.Rect.Width, pivotY * frame.Rect.Height);
        var effects = flipX ? SpriteEffects.FlipHorizontally : SpriteEffects.None;
        _spriteBatch.Draw(atlas.Texture, screenPos, frame.Rect, tint, 0f, origin, scale, effects, 0f);
    }

    // 8-direction offsets: N, NE, E, SE, S, SW, W, NW
    private static readonly float[][] _outlineDirs =
    {
        new[] { 0f, -1f }, new[] { 1f, -1f }, new[] { 1f, 0f }, new[] { 1f, 1f },
        new[] { 0f,  1f }, new[] {-1f,  1f }, new[] {-1f, 0f }, new[] {-1f,-1f }
    };

    // Hardcoded ghost outline params matching C++
    private static readonly HdrColor _ghostColor1 = new(140, 200, 255, 45, 1.0f);
    private static readonly HdrColor _ghostColor2 = new(170, 215, 255, 60, 1.1f);

    /// <summary>
    /// Draw a pulsing outline around a sprite using the OutlineFlat shader.
    /// Renders the sprite 8 times at directional offsets with a flat color.
    /// </summary>
    private void DrawSpriteOutline(SpriteAtlas atlas, SpriteFrame frame, Vector2 screenPos,
                                    float scale, bool flipX, HdrColor color1, HdrColor color2,
                                    float outlineWidth, float pulseWidth, float pulseSpeed,
                                    int blendMode)
    {
        if (atlas.Texture == null || _outlineFlatEffect == null) return;

        float t = 0.5f + 0.5f * MathF.Sin(_gameTime * pulseSpeed * 2f * MathF.PI);

        float offset = outlineWidth + (pulseWidth - outlineWidth) * t;
        if (offset < 0.5f) return;

        float colR = MathHelper.Lerp(color1.R / 255f, color2.R / 255f, t);
        float colG = MathHelper.Lerp(color1.G / 255f, color2.G / 255f, t);
        float colB = MathHelper.Lerp(color1.B / 255f, color2.B / 255f, t);
        float colA = MathHelper.Lerp(color1.A / 255f, color2.A / 255f, t);
        float intensity = MathHelper.Lerp(color1.Intensity, color2.Intensity, t);

        _outlineFlatEffect.Parameters["OutlineColor"]?.SetValue(
            new Vector4(colR * intensity, colG * intensity, colB * intensity, colA));

        _spriteBatch.End();
        var blend = blendMode == 1 ? BlendState.Additive : BlendState.NonPremultiplied;
        _spriteBatch.Begin(SpriteSortMode.Deferred, blend, SamplerState.LinearClamp, effect: _outlineFlatEffect);

        float pivotX = flipX ? (1f - frame.PivotX) : frame.PivotX;
        float pivotY = 1f - frame.PivotY;
        var origin = new Vector2(pivotX * frame.Rect.Width, pivotY * frame.Rect.Height);
        var effects = flipX ? SpriteEffects.FlipHorizontally : SpriteEffects.None;

        for (int d = 0; d < 8; d++)
        {
            float dx = _outlineDirs[d][0] * offset;
            float dy = _outlineDirs[d][1] * offset;
            _spriteBatch.Draw(atlas.Texture, new Vector2(screenPos.X + dx, screenPos.Y + dy),
                frame.Rect, Color.White, 0f, origin, scale, effects, 0f);
        }

        _spriteBatch.End();
        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp);
    }

    private void DrawUnitPulsingOutline(int unitIdx, SpriteAtlas atlas, SpriteFrame frame,
                                         Vector2 screenPos, float scale, bool flipX)
    {
        foreach (var buff in _sim.Units[unitIdx].ActiveBuffs)
        {
            var buffDef = _gameData.Buffs.Get(buff.BuffDefID);
            if (buffDef != null && buffDef.HasPulsingOutline && buffDef.PulsingOutline != null)
            {
                var po = buffDef.PulsingOutline;
                DrawSpriteOutline(atlas, frame, screenPos, scale, flipX,
                    po.Color, po.PulseColor, po.OutlineWidth, po.PulseWidth, po.PulseSpeed, po.BlendMode);
                return;
            }
        }
    }

    private void DrawGhostOutline(SpriteAtlas atlas, SpriteFrame frame,
                                    Vector2 screenPos, float scale, bool flipX)
    {
        DrawSpriteOutline(atlas, frame, screenPos, scale, flipX,
            _ghostColor1, _ghostColor2, 1.0f, 1.5f, 0.8f, 0);
    }

    private void DrawHPBar(int unitIdx, Vector2 screenPos)
    {
        var stats = _sim.Units[unitIdx].Stats;
        if (stats.MaxHP <= 0) return;

        float hpRatio = (float)stats.HP / stats.MaxHP;
        if (hpRatio >= 1f) return; // don't show full HP bars

        // Position HP bar above the unit based on its sprite height
        var unitDef = _gameData.Units.Get(_sim.Units[unitIdx].UnitDefID);
        float spriteWorldH = (unitDef != null && unitDef.SpriteWorldHeight > 0)
            ? unitDef.SpriteWorldHeight : 1.8f;
        float spriteScale = _sim.Units[unitIdx].SpriteScale;
        float barOffset = spriteWorldH * spriteScale * _camera.Zoom * 0.9f + 5f;

        float barW = 30f;
        float barH = 3f;
        float barX = screenPos.X - barW / 2f;
        float barY = screenPos.Y - barOffset;

        _spriteBatch.Draw(_pixel, new Rectangle((int)barX - 1, (int)barY - 1, (int)barW + 2, (int)barH + 2), new Color(0, 0, 0, 160));

        Color hpColor = hpRatio > 0.5f ? new Color(60, 180, 60) : (hpRatio > 0.25f ? new Color(200, 180, 40) : new Color(200, 40, 40));
        _spriteBatch.Draw(_pixel, new Rectangle((int)barX, (int)barY, (int)(barW * hpRatio), (int)barH), hpColor);
    }

    private void DrawSpellCategoryIcon(string category, int cx, int cy)
    {
        switch (category)
        {
            case "Projectile":
                for (int dy2 = -6; dy2 <= 6; dy2++)
                    for (int dx2 = -6; dx2 <= 6; dx2++)
                        if (dx2 * dx2 + dy2 * dy2 <= 36)
                            _spriteBatch.Draw(_pixel, new Rectangle(cx + dx2, cy + dy2, 1, 1), new Color(255, 140, 30, 200));
                break;
            case "Buff":
                for (int dy2 = -6; dy2 <= 6; dy2++)
                    for (int dx2 = -6; dx2 <= 6; dx2++)
                        if (dx2 * dx2 + dy2 * dy2 <= 36)
                            _spriteBatch.Draw(_pixel, new Rectangle(cx + dx2, cy + dy2, 1, 1), new Color(60, 200, 60, 200));
                break;
            case "Strike":
            {
                var lc = new Color(255, 230, 50, 220);
                _spriteBatch.Draw(_pixel, new Rectangle(cx + 2, cy - 8, 3, 5), lc);
                _spriteBatch.Draw(_pixel, new Rectangle(cx - 2, cy - 3, 6, 2), lc);
                _spriteBatch.Draw(_pixel, new Rectangle(cx - 3, cy - 1, 3, 5), lc);
                _spriteBatch.Draw(_pixel, new Rectangle(cx - 4, cy + 4, 4, 2), lc);
                break;
            }
            case "Summon":
                for (int dy2 = -6; dy2 <= 6; dy2++)
                    for (int dx2 = -6; dx2 <= 6; dx2++)
                        if (dx2 * dx2 + dy2 * dy2 <= 36)
                            _spriteBatch.Draw(_pixel, new Rectangle(cx + dx2, cy + dy2, 1, 1), new Color(160, 60, 200, 200));
                break;
            case "Beam":
                _spriteBatch.Draw(_pixel, new Rectangle(cx - 8, cy - 1, 16, 3), new Color(60, 120, 255, 220));
                _spriteBatch.Draw(_pixel, new Rectangle(cx - 6, cy - 2, 12, 1), new Color(100, 160, 255, 150));
                _spriteBatch.Draw(_pixel, new Rectangle(cx - 6, cy + 2, 12, 1), new Color(100, 160, 255, 150));
                break;
            case "Drain":
                _spriteBatch.Draw(_pixel, new Rectangle(cx - 8, cy - 1, 16, 3), new Color(220, 40, 40, 220));
                _spriteBatch.Draw(_pixel, new Rectangle(cx - 6, cy - 2, 12, 1), new Color(255, 80, 80, 150));
                _spriteBatch.Draw(_pixel, new Rectangle(cx - 6, cy + 2, 12, 1), new Color(255, 80, 80, 150));
                break;
            default:
                _spriteBatch.Draw(_pixel, new Rectangle(cx - 2, cy - 2, 4, 4), new Color(180, 180, 180, 150));
                break;
        }
    }

    private void DrawProjectiles()
    {
        foreach (var proj in _sim.Projectiles.Projectiles)
        {
            if (!proj.Alive) continue;
            var sp = _renderer.WorldToScreen(proj.Position, proj.Height, _camera);

            if (proj.Type == ProjectileType.Arrow)
            {
                // Oriented arrow shaft
                float angle = MathF.Atan2(proj.Velocity.Y * _camera.YRatio, proj.Velocity.X);
                float len = 12f * _camera.Zoom / 32f;
                _spriteBatch.Draw(_pixel, sp, null, new Color(200, 180, 120),
                    angle, new Vector2(0, 0.5f), new Vector2(len, 1.5f), SpriteEffects.None, 0f);
                // Arrowhead
                _spriteBatch.Draw(_pixel, sp, null, new Color(160, 140, 100),
                    angle, new Vector2(-2f, 1.5f), new Vector2(4f, 3f), SpriteEffects.None, 0f);
            }
            else if (proj.Type == ProjectileType.Potion && !string.IsNullOrEmpty(proj.IconTexturePath))
            {
                // Potion bottle tumbling through the air
                var tex = GetItemTextureByPath(proj.IconTexturePath);
                if (tex != null)
                {
                    float worldSize = proj.ParticleScale * 1.2f;
                    float pixelSize = worldSize * _camera.Zoom;
                    float scale = pixelSize / MathF.Max(tex.Width, tex.Height);
                    var origin = new Vector2(tex.Width / 2f, tex.Height / 2f);
                    float tumble = proj.Age * 6f; // fast spin
                    _spriteBatch.Draw(tex, sp, null, Color.White,
                        tumble, origin, scale, SpriteEffects.None, 0f);
                }
                else
                {
                    // Fallback colored dot
                    float glowSize = 5f * _camera.Zoom / 32f;
                    _spriteBatch.Draw(_pixel, sp, null, new Color(100, 200, 100, 200),
                        0f, new Vector2(0.5f, 0.5f), glowSize, SpriteEffects.None, 0f);
                }
            }
            else
            {
                // Spell projectile — try flipbook rendering
                string fbId = proj.FlipbookID;
                if (!string.IsNullOrEmpty(fbId) && _flipbooks.TryGetValue(fbId, out var fb) && fb.IsLoaded)
                {
                    int frameIdx = fb.GetFrameAtTime(proj.Age);
                    var srcRect = fb.GetFrameRect(frameIdx);
                    // Scale: worldSize * zoom / framePixelSize
                    float worldSize = proj.ParticleScale * 1.5f; // world units
                    float pixelSize = worldSize * _camera.Zoom;
                    float scale = pixelSize / srcRect.Width;
                    var origin = new Vector2(srcRect.Width / 2f, srcRect.Height / 2f);
                    var color = proj.ParticleColor.ToScaledColor();
                    _spriteBatch.Draw(fb.Texture, sp, srcRect, Color.FromNonPremultiplied(color.R, color.G, color.B, color.A),
                        proj.Age * 2f, origin, scale, SpriteEffects.None, 0f);
                }
                else
                {
                    // Fallback glow dot
                    float glowSize = 6f * _camera.Zoom / 32f;
                    _spriteBatch.Draw(_pixel, sp, null, Color.FromNonPremultiplied(255, 120, 40, 200),
                        0f, new Vector2(0.5f, 0.5f), glowSize, SpriteEffects.None, 0f);
                }

                // Trail segments
                float trailLen = 4f * _camera.Zoom / 32f;
                for (int t = 1; t <= 3; t++)
                {
                    var trailPos = _renderer.WorldToScreen(
                        proj.Position - proj.Velocity.Normalized() * (t * 0.3f),
                        proj.Height - proj.VelocityZ * t * 0.02f, _camera);
                    byte alpha = (byte)(120 / t);
                    float taf = alpha / 255f;
                    _spriteBatch.Draw(_pixel, trailPos, null, new Color((byte)(255 * taf), (byte)(100 * taf), (byte)(30 * taf), alpha),
                        0f, new Vector2(0.5f, 0.5f), trailLen / t, SpriteEffects.None, 0f);
                }
            }
        }
    }

    private void DrawEffects()
    {
        foreach (var eff in _effectManager.Effects)
        {
            if (!eff.Alive) continue;
            float t = eff.Age / eff.Lifetime;
            float alpha = eff.AlphaCurve.Evaluate(t);
            float scale = eff.ScaleCurve.Evaluate(t) * _camera.Zoom / 32f;

            var sp = _renderer.WorldToScreen(eff.Position, 0f, _camera);

            // Try flipbook
            if (!string.IsNullOrEmpty(eff.FlipbookKey) && _flipbooks.TryGetValue(eff.FlipbookKey, out var fb) && fb.IsLoaded)
            {
                int frameIdx = fb.GetFrameAtTime(eff.Age);
                var srcRect = fb.GetFrameRect(frameIdx);
                var origin = new Vector2(srcRect.Width * eff.AnchorX, srcRect.Height * eff.AnchorY);
                // Scale relative to world size
                float worldSize = scale * 2f; // scale curve gives world units
                float pixelSize = worldSize * _camera.Zoom;
                float fbScale = pixelSize / srcRect.Width;
                var color = new Color(eff.Tint.R, eff.Tint.G, eff.Tint.B, (byte)(alpha * 255));
                _spriteBatch.Draw(fb.Texture, sp, srcRect, color, 0f, origin, fbScale, SpriteEffects.None, 0f);
            }
            else
            {
                // Fallback glow (radial gradient circle)
                byte a = (byte)(alpha * 200);
                float glowSize = scale * _camera.Zoom * 0.5f / 32f; // divide by half-texture-size to match world scale
                _spriteBatch.Draw(_glowTex, sp, null, new Color(eff.Tint.R, eff.Tint.G, eff.Tint.B, a),
                    0f, new Vector2(32f, 32f), glowSize, SpriteEffects.None, 0f);
            }
        }

        // Spawn effects from projectile impacts
        foreach (var impact in _sim.Projectiles.Impacts)
        {
            string fbId = impact.HitEffectFlipbookID;
            if (!string.IsNullOrEmpty(fbId))
            {
                _effectManager.SpawnSpellImpact(impact.Position, impact.HitEffectScale,
                    impact.HitEffectColor.ToScaledColor(), fbId);
            }
            else if (impact.AoeRadius > 0)
            {
                _effectManager.SpawnExplosion(impact.Position, impact.AoeRadius);
            }
        }
    }

    private void DrawDamageNumbers()
    {
        if (_font == null) return;
        var dnSettings = _gameData.Settings.General;
        if (!dnSettings.DamageNumbersEnabled) return;
        var dnColor = dnSettings.DamageNumberColor;
        float dnScale = dnSettings.DamageNumberSize / 16f; // normalize against default 16

        foreach (var dn in _damageNumbers)
        {
            float fade = 1f - dn.Timer / dnSettings.DamageNumberFadeTime;
            if (fade <= 0f) continue;
            var sp = _renderer.WorldToScreen(dn.WorldPos, dn.Height, _camera);
            byte alpha = (byte)(255 * fade);

            // Pickup text or damage number
            string text = dn.PickupText != null ? $"+{dn.PickupText}" : dn.Damage.ToString();
            var size = _font.MeasureString(text) * dnScale;
            var pos = new Vector2(sp.X - size.X / 2f, sp.Y - size.Y / 2f);

            // Shadow pass
            var shadowColor = new Color((byte)0, (byte)0, (byte)0, alpha);
            _spriteBatch.DrawString(_font, text, new Vector2(pos.X + 1f, pos.Y + 1f), shadowColor,
                0f, Vector2.Zero, dnScale, SpriteEffects.None, 0f);

            // Text pass — pickup=gold, poison=green, otherwise use DamageNumberColor
            Color color;
            if (dn.PickupText != null)
                color = Color.FromNonPremultiplied(255, 220, 100, alpha);
            else if (dn.IsPoison)
                color = Color.FromNonPremultiplied(40, 200, 40, alpha);
            else
                color = Color.FromNonPremultiplied(dnColor.R, dnColor.G, dnColor.B, alpha);
            _spriteBatch.DrawString(_font, text, pos, color,
                0f, Vector2.Zero, dnScale, SpriteEffects.None, 0f);
        }
    }

    private void DrawHUD(int screenW, int screenH)
    {
        _hudRenderer.Draw(screenW, screenH, _sim, _gameData,
            _inventory, _inventoryUI.IsVisible,
            _spellBarState, _secondaryBarState,
            _spellDropdownSlot, _secondaryDropdownSlot,
            _timeScale, _hoveredObjectIdx, _envSystem,
            DrawSpellCategoryIcon,
            _potionSlots, _activePotionSlot, GetItemTexture,
            _potionDropdownSlot);
    }

    private void DrawPauseMenu(int screenW, int screenH)
    {
        _spriteBatch.Draw(_pixel, new Rectangle(0, 0, screenW, screenH), new Color(0, 0, 0, 150));

        int boxW = 350;
        int boxH = 450;
        int boxX = (screenW - boxW) / 2;
        int boxY = (screenH - boxH) / 2;
        _spriteBatch.Draw(_pixel, new Rectangle(boxX, boxY, boxW, boxH), new Color(30, 30, 50, 235));
        _spriteBatch.Draw(_pixel, new Rectangle(boxX, boxY, boxW, 3), new Color(100, 100, 180));

        if (_largeFont != null)
        {
            string title = "PAUSED";
            var titleSize = _largeFont.MeasureString(title);
            DrawText(_largeFont, title, new Vector2(boxX + boxW / 2f - titleSize.X / 2f, boxY + 15), Color.White);
        }

        // Menu items
        var mouse = Mouse.GetState();
        int btnW = 280, btnH = 40, btnGap = 10;
        int menuX = boxX + (boxW - btnW) / 2;
        int menuY = boxY + 60;

        DrawMenuButton("Resume", menuX, ref menuY, btnW, btnH, btnGap, mouse);
        DrawMenuButton("Unit Editor (F9)", menuX, ref menuY, btnW, btnH, btnGap, mouse);
        DrawMenuButton("Spell Editor (F10)", menuX, ref menuY, btnW, btnH, btnGap, mouse);
        DrawMenuButton("Map Editor (F11)", menuX, ref menuY, btnW, btnH, btnGap, mouse);
        DrawMenuButton("UI Editor (F12)", menuX, ref menuY, btnW, btnH, btnGap, mouse);
        DrawMenuButton("Item Editor", menuX, ref menuY, btnW, btnH, btnGap, mouse);
        DrawMenuButton("Settings", menuX, ref menuY, btnW, btnH, btnGap, mouse);

        menuY += 10;
        DrawMenuButton("Main Menu", menuX, ref menuY, btnW, btnH, btnGap, mouse);
        DrawMenuButton("Quit", menuX, ref menuY, btnW, btnH, btnGap, mouse);

        // Controls reference
        if (_smallFont != null)
        {
            string[] controls = {
                "WASD - Move     Space - Jump",
                "Q/E/LC/RC - Cast spells",
                "Shift - Run    G - Ghost mode",
                "+/- - Speed   Scroll - Zoom"
            };
            for (int i = 0; i < controls.Length; i++)
                DrawText(_smallFont, controls[i], new Vector2(boxX + 20, menuY + 10 + i * 16), new Color(140, 140, 160));
        }
    }


    private void DrawMainMenu(int screenW, int screenH)
    {
        // Background image (scaled to fill, centered)
        if (_mainMenuBg != null)
        {
            float bgScale = MathF.Max((float)screenW / _mainMenuBg.Width,
                                      (float)screenH / _mainMenuBg.Height);
            float bgW = _mainMenuBg.Width * bgScale;
            float bgH = _mainMenuBg.Height * bgScale;
            _spriteBatch.Draw(_mainMenuBg,
                new Rectangle((int)((screenW - bgW) * 0.5f), (int)((screenH - bgH) * 0.5f),
                              (int)bgW, (int)bgH),
                Color.White);
        }
        else
        {
            _spriteBatch.Draw(_pixel, new Rectangle(0, 0, screenW, screenH), new Color(20, 15, 30));
        }
        // Dark overlay for contrast
        _spriteBatch.Draw(_pixel, new Rectangle(0, 0, screenW, screenH), new Color(0, 0, 0, 120));

        // Title
        if (_largeFont != null)
        {
            string title = "NECROKING";
            var titleSize = _largeFont.MeasureString(title);
            int titleY = screenH / 5;
            // Shadow
            DrawText(_largeFont, title, new Vector2(screenW / 2f - titleSize.X / 2f + 3, titleY + 3), new Color(0, 0, 0, 180));
            DrawText(_largeFont, title, new Vector2(screenW / 2f - titleSize.X / 2f, titleY), new Color(220, 180, 100));

            string subtitle = "Rise of the Undead";
            var subSize = _font?.MeasureString(subtitle) ?? Vector2.Zero;
            DrawText(_font, subtitle, new Vector2(screenW / 2f - subSize.X / 2f, titleY + 30), new Color(180, 160, 120, 200));
        }

        // Menu buttons
        var mouse = Mouse.GetState();
        int btnW = 320, btnH = 55, btnGap = 18;
        int menuX = screenW / 2 - btnW / 2;
        int menuY = screenH / 2 + 20;

        DrawMenuButton("Play", menuX, ref menuY, btnW, btnH, btnGap, mouse);
        DrawMenuButton("Scenarios", menuX, ref menuY, btnW, btnH, btnGap, mouse);
        DrawMenuButton("Quit", menuX, ref menuY, btnW, btnH, btnGap, mouse);

        // Version info
        DrawText(_smallFont, "MonoGame Port v0.1", new Vector2(10, screenH - 20), new Color(80, 80, 100));
    }

    private void DrawScenarioList(int screenW, int screenH)
    {
        // Same background as main menu
        if (_mainMenuBg != null)
        {
            float bgScale = MathF.Max((float)screenW / _mainMenuBg.Width,
                                      (float)screenH / _mainMenuBg.Height);
            float bgW = _mainMenuBg.Width * bgScale;
            float bgH = _mainMenuBg.Height * bgScale;
            _spriteBatch.Draw(_mainMenuBg,
                new Rectangle((int)((screenW - bgW) * 0.5f), (int)((screenH - bgH) * 0.5f),
                              (int)bgW, (int)bgH),
                Color.White);
        }
        else
        {
            _spriteBatch.Draw(_pixel, new Rectangle(0, 0, screenW, screenH), new Color(20, 15, 30));
        }
        // Dark overlay for contrast
        _spriteBatch.Draw(_pixel, new Rectangle(0, 0, screenW, screenH), new Color(0, 0, 0, 120));

        // Title
        if (_largeFont != null)
        {
            string title = "SCENARIOS";
            var titleSize = _largeFont.MeasureString(title);
            int titleY = screenH / 6;
            DrawText(_largeFont, title, new Vector2(screenW / 2f - titleSize.X / 2f + 3, titleY + 3), new Color(0, 0, 0, 180));
            DrawText(_largeFont, title, new Vector2(screenW / 2f - titleSize.X / 2f, titleY), new Color(180, 220, 100));
        }

        if (_font != null)
        {
            string subtitle = "Select a scenario to run";
            var subSize = _font.MeasureString(subtitle);
            DrawText(_font, subtitle, new Vector2(screenW / 2f - subSize.X / 2f, screenH / 6 + 35), new Color(140, 140, 160));
        }

        // Scenario buttons
        var mouse = Mouse.GetState();
        int btnW = 320, btnH = 45, btnGap = 12;
        int menuX = screenW / 2 - btnW / 2;
        int menuY = screenH / 4 + 60;

        var names = new List<string>(ScenarioRegistry.GetNames());
        int visibleCount = Math.Min(names.Count - _scenarioScrollOffset, (screenH - menuY - 80) / (btnH + btnGap));
        for (int i = 0; i < visibleCount; i++)
        {
            int nameIdx = i + _scenarioScrollOffset;
            if (nameIdx >= names.Count) break;
            DrawMenuButton(names[nameIdx], menuX, ref menuY, btnW, btnH, btnGap, mouse);
        }

        // Back button
        menuY += 10;
        DrawMenuButton("< Back", menuX, ref menuY, btnW, btnH, btnGap, mouse);

        // Scroll hint
        if (names.Count > visibleCount + _scenarioScrollOffset)
            DrawText(_smallFont, "Scroll for more...", new Vector2(screenW / 2f - 50, screenH - 40), new Color(100, 100, 120));
    }

    private void DrawMenuButton(string text, int x, ref int y, int w, int h, int gap, MouseState mouse)
    {
        bool hover = mouse.X >= x && mouse.X < x + w && mouse.Y >= y && mouse.Y < y + h;
        Color bg = hover ? new Color(90, 60, 120, 240) : new Color(60, 40, 80, 220);
        _spriteBatch.Draw(_pixel, new Rectangle(x, y, w, h), bg);
        _spriteBatch.Draw(_pixel, new Rectangle(x, y, w, 2), new Color(220, 180, 100, hover ? 255 : 120));
        _spriteBatch.Draw(_pixel, new Rectangle(x, y + h - 2, w, 2), new Color(220, 180, 100, hover ? 255 : 60));

        if (_font != null)
        {
            var textSize = _font.MeasureString(text);
            DrawText(_font, text, new Vector2(x + w / 2f - textSize.X / 2f, y + (h - textSize.Y) / 2f),
                new Color(255, 245, 220));
        }
        y += h + gap;
    }

    private void DrawGameOver(int screenW, int screenH)
    {
        _spriteBatch.Draw(_pixel, new Rectangle(0, 0, screenW, screenH), new Color(0, 0, 0, 160));
        if (_largeFont != null)
        {
            string title = "NECROMANCER FALLEN";
            var size = _largeFont.MeasureString(title);
            DrawText(_largeFont, title, new Vector2(screenW / 2f - size.X / 2f, screenH / 2f - 30), new Color(200, 50, 50));
        }
        if (_font != null)
        {
            string sub = "Press R to restart";
            var size = _font.MeasureString(sub);
            DrawText(_font, sub, new Vector2(screenW / 2f - size.X / 2f, screenH / 2f + 10), new Color(180, 180, 200));
        }
    }

    private void DrawText(SpriteFont? font, string text, Vector2 pos, Color color)
    {
        if (font != null)
            _spriteBatch.DrawString(font, text, pos, color);
    }

    protected override void UnloadContent()
    {
        _widgetRenderer.Shutdown();
        _pixel?.Dispose();
        _glowTex?.Dispose();
        _mainMenuBg?.Dispose();
        _groundVertexMapTex?.Dispose();
        foreach (var atlas in _atlases)
            atlas?.Texture?.Dispose();
        _envSystem.OnCollisionsDirty = null;
        base.UnloadContent();
    }
}

public struct SpellBarSlot { public string SpellID; }
public struct SpellBarState { public SpellBarSlot[] Slots; }
