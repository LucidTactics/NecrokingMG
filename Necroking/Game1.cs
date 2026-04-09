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

    // Carried body bag offset and scale (pixel offsets on top of hilt position)
    private const float CarryOffsetX = 4.5f;
    private const float CarryOffsetY = 8.5f;
    private const float CarryBagScale = 3.4f;
    private Dictionary<string, Flipbook> _flipbooks = new(); // keyed by flipbook ID
    private Dictionary<string, AnimationMeta> _animMeta = new(); // animation metadata
    private Microsoft.Xna.Framework.Graphics.Effect? _groundEffect;
    private Microsoft.Xna.Framework.Graphics.Effect? _outlineFlatEffect;
    private Microsoft.Xna.Framework.Graphics.Effect? _hdrIntensityEffect;
    private Microsoft.Xna.Framework.Graphics.Effect? _hdrSpriteEffect;
    private Texture2D? _groundVertexMapTex;
    private EffectManager _effectManager = new();
    private BuffVisualSystem _buffVisuals = new();
    private readonly List<Data.Registries.BuffDef> _wpDefsCache = new(); // reused per-unit in DrawSingleUnit
    private BloomRenderer _bloom = new();
    private WeatherRenderer _weatherRenderer = new();
    private DayNightSystem _dayNightSystem = new();
    private LightningRenderer _lightningRenderer = new();
    private PoisonCloudRenderer _poisonCloudRenderer = new();
    private MagicGlyphRenderer _glyphRenderer = new();
    private DebugDraw _debugDraw = new();
    private GameSystems.SpellEffectSystem _spellEffects = new();
    private Game.TrapPlacementManager _trapManager = new();
    private List<GameSystems.DamageNumber> _damageNumbers = new();

    // Game state
    private MenuState _menuState = MenuState.MainMenu;
    private bool _gameWorldLoaded;
    private bool _paused;
    private bool _gameOver;
    private float _gameTime;
    private float _timeScale = 1f;

    // Glyph trap placement mode
    // _trapPlacementActive moved to TrapPlacementManager
    private PendingSpellCast _pendingSpell = new();
    private PendingCastAnim? _pendingCastAnim;
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
    private KeyboardState _prevKb;
    private MouseState _prevMouse;
    private float _rawDt;
    private readonly InputState _input = new();
    private float _editorPanTime; // ramp-up timer for editor camera panning

    // Pending spell cast with animation delay (Spell1 animation → action moment → execute)
    private struct PendingCastAnim
    {
        public string SpellID;
        public Vec2 Target;
        public int Slot;           // spellbar slot that was used
        public bool IsSecondary;   // secondary spellbar
        public string? CastingBuffID; // to remove on animation end
    }

    // Pending projectiles (multi-projectile delay)
    private readonly List<GameSystems.PendingProjectileGroup> _pendingProjectiles = new();

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

    // DamageNumber and PendingProjectileGroup moved to GameSystems.SpellEffectSystem

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
            _gameData.Settings.Save(GamePaths.Resolve(GamePaths.SettingsJson));
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
        AI.ArchetypeRegistry.Register(AI.ArchetypeRegistry.PlayerControlled, "PlayerControlled", new AI.PlayerControlledHandler());
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
        _dayNightSystem.Init(_gameData.Settings.DayNight);

        // Load flipbooks
        foreach (var fbId in _gameData.Flipbooks.GetIDs())
        {
            var fbDef = _gameData.Flipbooks.Get(fbId);
            if (fbDef == null || string.IsNullOrEmpty(fbDef.Path)) continue;
            var resolvedPath = GamePaths.Resolve(fbDef.Path);
            if (!File.Exists(resolvedPath)) continue;
            var fb = new Flipbook();
            if (fb.Load(GraphicsDevice, resolvedPath, fbDef.Cols, fbDef.Rows, fbDef.DefaultFPS))
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

        // Load flipbooks (needed for cloud effects, hit effects, etc.)
        if (_flipbooks.Count == 0)
        {
            foreach (var fbId in _gameData.Flipbooks.GetIDs())
            {
                var fbDef = _gameData.Flipbooks.Get(fbId);
                if (fbDef == null || string.IsNullOrEmpty(fbDef.Path)) continue;
                var resolvedPath = GamePaths.Resolve(fbDef.Path);
                if (!File.Exists(resolvedPath)) continue;
                var fb = new Flipbook();
                if (fb.Load(GraphicsDevice, resolvedPath, fbDef.Cols, fbDef.Rows, fbDef.DefaultFPS))
                    _flipbooks[fbId] = fb;
            }
        }

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

        // Wire up graphics context for blend test
        if (scenario is Scenario.Scenarios.BlendTestScenario blendTest)
        {
            blendTest.Device = GraphicsDevice;
            blendTest.Batch = _spriteBatch;
        }

        // Wire up god ray render test
        if (scenario is Scenario.Scenarios.GodRayRenderTestScenario godRayTest)
        {
            godRayTest.Device = GraphicsDevice;
            godRayTest.SpriteBatchRef = _spriteBatch;
            godRayTest.GodRayRef = _lightningRenderer.GetGodRayRenderer();
            godRayTest.HdrEffect = _hdrIntensityEffect;
        }

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
                    Horde = _sim.Horde, TriggerSystem = _triggerSystem, MagicGlyphs = _sim.MagicGlyphs,
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

                if (unitDef.AttackAnim != null)
                    ctrl.SetAttackAnimOverride(unitDef.AttackAnim);

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
                if (corpse.BaggedByUnitID != GameConstants.InvalidUnit) continue; // mid-bagging
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
    private void SpawnCastEffect(SpellDef spell, Vec2 pos)
    {
        if (spell.CastFlipbook == null || string.IsNullOrEmpty(spell.CastFlipbook.FlipbookID)) return;

        var fb = spell.CastFlipbook;
        var tint = fb.Color.ToColor();
        int blendMode = fb.BlendMode == "Additive" ? 1 : 0;
        int alignment = fb.Alignment == "Upright" ? 1 : 0;
        float duration = fb.Duration >= 0f ? fb.Duration : 0.4f;

        _effectManager.SpawnSpellImpact(pos, fb.Scale, tint, fb.FlipbookID,
            fb.Color.Intensity, blendMode, alignment, duration);
    }

    /// <summary>
    /// Execute a spell's effect (projectile, buff, strike, etc.). Called either immediately
    /// (no casting buff) or at the Spell1 animation action moment (deferred cast).
    /// </summary>
    private void ExecuteSpellEffect(SpellDef spell, int necroIdx, Vec2 target, int slot)
    {
        // Cast flipbook effect at caster position
        SpawnCastEffect(spell, _sim.Units[necroIdx].EffectSpawnPos2D);

        // Delegate to SpellEffectSystem — all category logic lives there
        var result = _spellEffects.Execute(spell, _sim, _gameData, necroIdx, target, slot,
            _damageNumbers,
            SpawnSpellProjectile,
            (sp, cIdx) => ExecuteSummonSpell(sp, _pendingSpell, _sim.Units[cIdx].Position, cIdx));

        // Apply side effects that SpellEffectSystem can't own (Game1 state)
        if (result.ChannelingSlot >= 0)
            _channelingSlot = result.ChannelingSlot;
        if (result.PendingProjectile.HasValue)
            _pendingProjectiles.Add(result.PendingProjectile.Value);
    }

    /// <summary>Remove a specific casting buff from a unit.</summary>
    private void RemoveCastingBuff(int unitIdx, string? castingBuffID)
    {
        if (string.IsNullOrEmpty(castingBuffID)) return;
        var buffs = _sim.UnitsMut[unitIdx].ActiveBuffs;
        for (int b = buffs.Count - 1; b >= 0; b--)
        {
            if (buffs[b].BuffDefID == castingBuffID)
            {
                buffs.RemoveAt(b);
                break;
            }
        }
    }

    /// <summary>Remove all casting effect buffs from a unit (buff_4 variants).</summary>
    private void RemoveCastingBuffAll(int unitIdx)
    {
        var buffs = _sim.UnitsMut[unitIdx].ActiveBuffs;
        for (int b = buffs.Count - 1; b >= 0; b--)
        {
            var def = _gameData.Buffs.Get(buffs[b].BuffDefID);
            if (def != null && def.HasWeaponParticle)
                buffs.RemoveAt(b);
        }
    }

    private void SpawnSummonEffect(SpellDef spell, Vec2 pos)
    {
        if (spell.SummonFlipbook == null || string.IsNullOrEmpty(spell.SummonFlipbook.FlipbookID)) return;

        var fb = spell.SummonFlipbook;
        var tint = fb.Color.ToColor();
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

        if (unitDef.AttackAnim != null)
            ctrl.SetAttackAnimOverride(unitDef.AttackAnim);

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
        _buffVisuals.SetPixel(_pixel);

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
                byte a = (byte)(alpha * 255);
                glowData[gy * 64 + gx] = new Color(a, a, a, a); // premultiplied
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
        _hudRenderer.SetInput(_input);

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
        try { _hdrIntensityEffect = Content.Load<Microsoft.Xna.Framework.Graphics.Effect>("HdrIntensity"); }
        catch (Exception ex) { _hdrIntensityEffect = null; DebugLog.Log("startup", $"HdrIntensity not loaded: {ex.Message}"); }
        try
        {
            _hdrSpriteEffect = Content.Load<Microsoft.Xna.Framework.Graphics.Effect>("HdrSprite");
            if (_hdrSpriteEffect != null)
            {
                _hdrSpriteEffect.Parameters["MaxIntensity"]?.SetValue(HdrColor.MaxHdrIntensity);
                _hdrSpriteEffect.Parameters["AlphaMode"]?.SetValue(0f);
            }
        }
        catch (Exception ex) { _hdrSpriteEffect = null; DebugLog.Log("startup", $"HdrSprite not loaded: {ex.Message}"); }

        {
            Microsoft.Xna.Framework.Graphics.Effect? glyphEffect = null;
            try { glyphEffect = Content.Load<Microsoft.Xna.Framework.Graphics.Effect>("MagicCircle"); }
            catch (Exception ex) { DebugLog.Log("startup", $"MagicCircle not loaded: {ex.Message}"); }
            _glyphRenderer.LoadEffect(glyphEffect);
        }

        {
            Microsoft.Xna.Framework.Graphics.Effect? fogEffect = null;
            try { fogEffect = Content.Load<Microsoft.Xna.Framework.Graphics.Effect>("WeatherFog"); }
            catch (Exception ex) { DebugLog.Log("startup", $"WeatherFog not loaded: {ex.Message}"); }
            _weatherRenderer.LoadEffect(fogEffect);
        }
        _weatherRenderer.Init(_graphics.PreferredBackBufferWidth, _graphics.PreferredBackBufferHeight);
        _weatherRenderer.SetDayNight(_dayNightSystem);

        _grassRenderer.Init(GraphicsDevice);
        _lightningRenderer.Init(_spriteBatch, _pixel, _glowTex, _sim, _camera, _renderer, GraphicsDevice, _hdrIntensityEffect);
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
        _settingsWindow.SetGameData(_gameData, GamePaths.Resolve(GamePaths.SettingsJson), GamePaths.Resolve(GamePaths.WeatherJson));
        _settingsWindow.SetDayNightSystem(_dayNightSystem);
        LogTiming("Editors initialized");
        DebugLog.Log("startup", $"=== LoadContent complete ===");
    }

    protected override void Update(GameTime gameTime)
    {
        var kb = Keyboard.GetState();
        var mouse = Mouse.GetState();
        _input.Capture(mouse, _prevMouse, kb, _prevKb);
        _rawDt = (float)gameTime.ElapsedGameTime.TotalSeconds;
        float rawDt = _rawDt;
        float dt = _paused ? 0f : MathF.Min(rawDt, 1f / 20f) * _timeScale;
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
            if (_input.LeftPressed)
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
            if (_input.WasKeyPressed(Keys.Escape))
            {
                _menuState = MenuState.MainMenu;
                _prevKb = kb;
                _prevMouse = mouse;
                base.Update(gameTime);
                return;
            }

            // Scroll
            if (_input.ScrollDelta > 0) _scenarioScrollOffset = Math.Max(0, _scenarioScrollOffset - 1);
            if (_input.ScrollDelta < 0) _scenarioScrollOffset++;

            if (_input.LeftPressed)
            {
                int screenW2 = GraphicsDevice.Viewport.Width;
                int screenH2 = GraphicsDevice.Viewport.Height;
                int btnW = 320, btnH = 45, btnGap = 12;
                int menuX = screenW2 / 2 - btnW / 2;
                int menuY = screenH2 / 4 + 60;

                var names = new List<string>(ScenarioRegistry.GetNames());
                names.Reverse(); // Newest first (must match draw order)
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
        if (!anyTextInputActive && _input.WasKeyPressed(Keys.F7))
            _gameplayDebugMode = (_gameplayDebugMode + 1) % 3;

        // --- F8 collision debug toggle ---
        if (!anyTextInputActive && _input.WasKeyPressed(Keys.F8))
        {
            _collisionDebugMode = (CollisionDebugMode)(((int)_collisionDebugMode + 1) % (int)CollisionDebugMode.Count);
        }

        // --- F9-F12 editor toggles ---
        if (!anyTextInputActive && _input.WasKeyPressed(Keys.F9))
        {
            _menuState = _menuState == MenuState.UnitEditor ? MenuState.None : MenuState.UnitEditor;
            _editorUi.ClearActiveField();
        }
        if (!anyTextInputActive && _input.WasKeyPressed(Keys.F10))
        {
            _menuState = _menuState == MenuState.SpellEditor ? MenuState.None : MenuState.SpellEditor;
            _editorUi.ClearActiveField();
        }
        if (!anyTextInputActive && _input.WasKeyPressed(Keys.F11))
            _menuState = _menuState == MenuState.MapEditor ? MenuState.None : MenuState.MapEditor;
        if (!anyTextInputActive && _input.WasKeyPressed(Keys.F12))
            _menuState = _menuState == MenuState.UIEditor ? MenuState.None : MenuState.UIEditor;

        // 'I' key toggles inventory
        if (!anyTextInputActive && _input.WasKeyPressed(Keys.I) && _menuState == MenuState.None)
            _inventoryUI.Toggle(GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height);

        // --- Pause menu button clicks ---
        if (_menuState == MenuState.PauseMenu && _input.LeftPressed)
        {
            int sw = GraphicsDevice.Viewport.Width;
            int sh = GraphicsDevice.Viewport.Height;
            int btnW2 = 280, btnH2 = 40, btnGap2 = 10;
            int pauseBtnCount = 9;
            int pauseControlLines = 4;
            int pauseBoxH = 60 + pauseBtnCount * (btnH2 + btnGap2) + 10 + pauseControlLines * 16 + 20;
            int boxY2 = (sh - pauseBoxH) / 2 + 60;
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
            _editorUi.ResetAllState();
            _menuState = MenuState.PauseMenu;
        }

        // --- ESC toggles pause menu / closes editor ---
        // When a text field is active, Escape is consumed by EditorBase.HandleTextInput to deactivate the field.
        // When a popup (color picker, texture browser, confirm dialog) is open, don't close the editor.
        bool popupOpen = _editorUi.IsColorPickerOpen || _editorUi.IsDropdownOpen
            || (_menuState == MenuState.UIEditor && (_uiEditor.IsColorPickerOpen || _uiEditor.IsDropdownOpen));
        if (!anyTextInputActive && !popupOpen && _input.WasKeyPressed(Keys.Escape))
        {
            if (_menuState == MenuState.Settings)
            {
                _editorUi.ResetAllState();
                _menuState = MenuState.PauseMenu;
            }
            else if (_menuState == MenuState.UnitEditor || _menuState == MenuState.SpellEditor ||
                _menuState == MenuState.MapEditor || _menuState == MenuState.UIEditor ||
                _menuState == MenuState.ItemEditor)
            {
                _editorUi.ResetAllState();
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
            _editorUi.UpdateInput(mouse, _prevMouse, kb, _prevKb, screenW, screenH, gameTime, _input);
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
                if (_input.IsKeyDown(Keys.Up) || _input.IsKeyDown(Keys.W)) camMove.Y -= 1f;
                if (_input.IsKeyDown(Keys.Down) || _input.IsKeyDown(Keys.S)) camMove.Y += 1f;
                if (_input.IsKeyDown(Keys.Left) || _input.IsKeyDown(Keys.A)) camMove.X -= 1f;
                if (_input.IsKeyDown(Keys.Right) || _input.IsKeyDown(Keys.D)) camMove.X += 1f;
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
            if (_input.IsKeyDown(Keys.Up)) camMove.Y -= 1f;
            if (_input.IsKeyDown(Keys.Down)) camMove.Y += 1f;
            if (_input.IsKeyDown(Keys.Left)) camMove.X -= 1f;
            if (_input.IsKeyDown(Keys.Right)) camMove.X += 1f;
            if (camMove.LengthSq() > 0.01f)
                _camera.Position += camMove.Normalized() * camSpeed * rawDt;
        }

        // --- IsMouseOverUI: test UI element bounds ---
        if (_menuState == MenuState.None)
        {
            int mx = mouse.X, my = mouse.Y;

            // Spell bars — use HUDRenderer layout (single source of truth)
            var pri = _hudRenderer.GetPrimaryBarLayout(screenH);
            if (_hudRenderer.HitTestBarSlot(screenW, pri.barY, pri.slotW, pri.slotH, pri.centerOffset, mx, my) >= 0)
                _input.MouseOverUI = true;
            var sec = _hudRenderer.GetSecondaryBarLayout(screenH);
            if (_hudRenderer.HitTestBarSlot(screenW, sec.barY, sec.slotW, sec.slotH, sec.centerOffset, mx, my) >= 0)
                _input.MouseOverUI = true;

            // Spell dropdown open
            if (_spellDropdownSlot >= 0 || _secondaryDropdownSlot >= 0 || _potionDropdownSlot >= 0)
                _input.MouseOverUI = true;

            // Inventory, building, crafting
            if (_inventoryUI.ContainsMouse(mx, my))
                _input.MouseOverUI = true;
            if (_buildingMenuUI.ContainsMouse(mx, my))
                _input.MouseOverUI = true;
            if (_craftingMenu.ContainsMouse(mx, my))
                _input.MouseOverUI = true;

            // Time controls
            if (_gameData.Settings.General.ShowTimeControls
                && _hudRenderer.HitTestTimeControls(screenW, screenH, mx, my) != -1)
                _input.MouseOverUI = true;
        }

        // Scroll zoom (always active, but not when over UI)
        bool scrollOverUI = _input.MouseOverUI
            || (_menuState == MenuState.MapEditor && _mapEditor.IsMouseOverPanel(screenW, screenH));
        if (_input.ScrollDelta != 0 && (_menuState == MenuState.None || _menuState == MenuState.MapEditor) && !scrollOverUI)
            _camera.ZoomBy(_input.ScrollDelta / 120f);

        // Editors pause the game
        bool editorActive = _menuState == MenuState.UnitEditor || _menuState == MenuState.SpellEditor
            || _menuState == MenuState.MapEditor || _menuState == MenuState.UIEditor;

        if (!_paused && !editorActive)
        {
            // --- Player input ---
            Vec2 mouseWorld = _camera.ScreenToWorld(new Vector2(mouse.X, mouse.Y), screenW, screenH);

            if (necroIdx >= 0)
            {
                // WASD movement
                Vec2 moveDir = Vec2.Zero;
                if (_input.IsKeyDown(Keys.W)) moveDir.Y -= 1f;
                if (_input.IsKeyDown(Keys.S)) moveDir.Y += 1f;
                if (_input.IsKeyDown(Keys.A)) moveDir.X -= 1f;
                if (_input.IsKeyDown(Keys.D)) moveDir.X += 1f;
                if (moveDir.LengthSq() > 0.01f) moveDir = moveDir.Normalized();

                bool running = _input.IsKeyDown(Keys.LeftShift) || _input.IsKeyDown(Keys.RightShift);
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
            if (_input.WasKeyPressed(Keys.Space) && necroIdx >= 0)
            {
                var mu = _sim.UnitsMut;
                bool canJump = mu[necroIdx].Alive && !mu[necroIdx].Jumping
                    && !mu[necroIdx].Incap.IsLocked && !_pendingSpell.Active;
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
                    0 => _input.IsKeyDown(Keys.Q),
                    1 => _input.IsKeyDown(Keys.E),
                    2 => _input.LeftDown,
                    3 => _input.RightDown,
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
            if (_input.LeftPressed)
            {
                var priLayout = _hudRenderer.GetPrimaryBarLayout(screenH);
                bool clickedSlot = false;

                if (_spellDropdownSlot >= 0)
                {
                    var spellIDs = _gameData.Spells.GetIDs();
                    int itemIdx = _hudRenderer.HitTestSpellDropdown(screenW,
                        priLayout.barY, priLayout.slotW, priLayout.centerOffset,
                        _spellDropdownSlot, spellIDs.Count, mouse.X, mouse.Y);
                    if (itemIdx >= 0)
                    {
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
                    int s = _hudRenderer.HitTestBarSlot(screenW,
                        priLayout.barY, priLayout.slotW, priLayout.slotH, priLayout.centerOffset,
                        mouse.X, mouse.Y);
                    if (s >= 0)
                    {
                        _spellDropdownSlot = s;
                        clickedSlot = true;
                    }
                }

                if (clickedSlot) goto SkipSpellCast;

                // Also check secondary bar click
                var secLayout = _hudRenderer.GetSecondaryBarLayout(screenH);
                if (_secondaryDropdownSlot >= 0)
                {
                    var secSpellIDs = _gameData.Spells.GetIDs();
                    int sddIdx = _hudRenderer.HitTestSpellDropdown(screenW,
                        secLayout.barY, secLayout.slotW, secLayout.centerOffset,
                        _secondaryDropdownSlot, secSpellIDs.Count, mouse.X, mouse.Y);
                    if (sddIdx >= 0)
                    {
                        if (sddIdx == 0)
                            _secondaryBarState.Slots[_secondaryDropdownSlot].SpellID = "";
                        else if (sddIdx - 1 < secSpellIDs.Count)
                            _secondaryBarState.Slots[_secondaryDropdownSlot].SpellID = secSpellIDs[sddIdx - 1];
                    }
                    _secondaryDropdownSlot = -1;
                }
                else
                {
                    int ss = _hudRenderer.HitTestBarSlot(screenW,
                        secLayout.barY, secLayout.slotW, secLayout.slotH, secLayout.centerOffset,
                        mouse.X, mouse.Y);
                    if (ss >= 0)
                    {
                        _secondaryDropdownSlot = ss;
                        goto SkipSpellCast;
                    }
                }

                // --- Potion slot dropdown interaction ---
                // Potion slots sit to the right of the 4 secondary spell slots
                int potionBaseX = screenW / 2 - secLayout.centerOffset + 4 * (secLayout.slotW + 4) + 8;
                if (_potionDropdownSlot >= 0)
                {
                    var allPotionIds = _gameData.Potions.GetIDs();
                    // Potion dropdown uses same layout as spell dropdown but offset to potion position
                    int potSlotX = potionBaseX + _potionDropdownSlot * (secLayout.slotW + 4);
                    int ddH = (allPotionIds.Count + 1) * 20;
                    int ddY = secLayout.barY - 10;
                    int ddLeft = potSlotX - 2;

                    if (mouse.X >= ddLeft && mouse.X < ddLeft + 164 && mouse.Y >= ddY - ddH && mouse.Y < ddY)
                    {
                        int pddIdx = (ddY - mouse.Y) / 20;
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
                    for (int ps = 0; ps < 2; ps++)
                    {
                        int psX = potionBaseX + ps * (secLayout.slotW + 4);
                        if (mouse.X >= psX && mouse.X < psX + secLayout.slotW &&
                            mouse.Y >= secLayout.barY && mouse.Y < secLayout.barY + secLayout.slotH)
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
                    0 => _input.WasKeyPressed(Keys.Q),
                    1 => _input.WasKeyPressed(Keys.E),
                    2 => !_input.MouseOverUI && _input.LeftPressed,
                    3 => !_input.MouseOverUI && _input.RightPressed,
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

                // Can't cast while a spell animation is playing
                if (_pendingCastAnim != null) continue;

                var result = SpellCaster.TryStartSpellCast(spellId, _gameData.Spells, _sim.NecroState,
                    _sim.Units, necroIdx, mouseWorld, _sim.Corpses, _pendingSpell, _gameData);

                if (result == CastResult.Success)
                {
                    var spell = _gameData.Spells.Get(spellId);
                    if (spell == null) continue;

                    // Apply casting buff immediately (visual starts right away)
                    if (!string.IsNullOrEmpty(spell.CastingBuffID))
                    {
                        var castBuff = _gameData.Buffs.Get(spell.CastingBuffID);
                        if (castBuff != null) BuffSystem.ApplyBuff(_sim.UnitsMut, necroIdx, castBuff);

                        // Defer spell execution to Spell1 animation action moment
                        _pendingCastAnim = new PendingCastAnim
                        {
                            SpellID = spellId,
                            Target = mouseWorld,
                            Slot = slot,
                            CastingBuffID = spell.CastingBuffID
                        };

                        // Request Spell1 animation on necromancer
                        uint necroUid = _sim.Units[necroIdx].Id;
                        if (_unitAnims.TryGetValue(necroUid, out var necroAnim))
                        {
                            necroAnim.Ctrl.RequestState(AnimState.Spell1);
                            _unitAnims[necroUid] = necroAnim;
                        }
                    }
                    else
                    {
                        // No casting buff → execute immediately (legacy behavior)
                        ExecuteSpellEffect(spell, necroIdx, mouseWorld, slot);
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
            if (_input.WasKeyPressed(Keys.G) && necroIdx >= 0)
                _sim.UnitsMut[necroIdx].GhostMode = !_sim.Units[necroIdx].GhostMode;

            // --- Glyph trap placement (T key) ---
            _trapManager.Update(_input, _sim, necroIdx, mouseWorld);

            // --- Secondary spell bar (keys 1-4) ---
            if (necroIdx >= 0)
            {
                Keys[] secKeys = { Keys.D1, Keys.D2, Keys.D3, Keys.D4 };
                for (int sk = 0; sk < 4; sk++)
                {
                    if (!_input.WasKeyPressed(secKeys[sk])) continue;
                    if (sk >= _secondaryBarState.Slots.Length) continue;
                    string secSpellId = _secondaryBarState.Slots[sk].SpellID;
                    if (string.IsNullOrEmpty(secSpellId)) continue;
                    if (_pendingCastAnim != null) continue;

                    var secResult = SpellCaster.TryStartSpellCast(secSpellId, _gameData.Spells, _sim.NecroState,
                        _sim.Units, necroIdx, mouseWorld, _sim.Corpses, _pendingSpell, _gameData);
                    if (secResult == CastResult.Success)
                    {
                        var spell2 = _gameData.Spells.Get(secSpellId);
                        if (spell2 == null) continue;

                        if (!string.IsNullOrEmpty(spell2.CastingBuffID))
                        {
                            var castBuff2 = _gameData.Buffs.Get(spell2.CastingBuffID);
                            if (castBuff2 != null) BuffSystem.ApplyBuff(_sim.UnitsMut, necroIdx, castBuff2);

                            _pendingCastAnim = new PendingCastAnim
                            {
                                SpellID = secSpellId,
                                Target = mouseWorld,
                                Slot = sk,
                                IsSecondary = true,
                                CastingBuffID = spell2.CastingBuffID
                            };

                            uint necroUid2 = _sim.Units[necroIdx].Id;
                            if (_unitAnims.TryGetValue(necroUid2, out var necroAnim2))
                            {
                                necroAnim2.Ctrl.RequestState(AnimState.Spell1);
                                _unitAnims[necroUid2] = necroAnim2;
                            }
                        }
                        else
                        {
                            ExecuteSpellEffect(spell2, necroIdx, mouseWorld, sk);
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
                    if (!_input.WasKeyPressed(potionKeys[pk])) continue;
                    if (string.IsNullOrEmpty(_potionSlots[pk])) continue;
                    if (_inventory.GetItemCount(_potionSlots[pk]) <= 0) continue;
                    _activePotionSlot = (_activePotionSlot == pk) ? -1 : pk;
                }
            }

            // Potion throw on left-click when a potion slot is active
            if (_activePotionSlot >= 0 && necroIdx >= 0 && !_input.MouseOverUI
                && _input.LeftPressed)
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
            if (_activePotionSlot >= 0 && _input.RightPressed)
                _activePotionSlot = -1;

            // --- Corpse interaction (F key) ---
            if (_input.WasKeyPressed(Keys.F))
                Game.CorpseInteractionManager.TryInteract(_sim, necroIdx);

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
            if (_input.WasKeyPressed(Keys.B))
            {
                if (_craftingMenu.IsVisible) _craftingMenu.Close();
                _buildingMenuUI.Toggle(screenW, screenH);
            }

            // --- Crafting menu toggle (C) ---
            if (_input.WasKeyPressed(Keys.C))
            {
                if (_buildingMenuUI.IsVisible) _buildingMenuUI.Close();
                _craftingMenu.Toggle(screenW, screenH);
            }

            // --- Building placement click (world click to place) ---
            if (_buildingMenuUI.IsPlacementActive
                && !_buildingMenuUI.ContainsMouse(mouse.X, mouse.Y)
                && _input.LeftPressed)
            {
                _buildingMenuUI.TryPlace(mouseWorld.X, mouseWorld.Y);
            }

            // --- Foragable collection (right-click within 2 units of necromancer) ---
            if (!_input.MouseOverUI && _input.RightPressed
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
            if (_input.WasKeyPressed(Keys.OemPlus) || _input.WasKeyPressed(Keys.Add))
                _timeScale = MathF.Min(_timeScale * 2f, 8f);
            if (_input.WasKeyPressed(Keys.OemMinus) || _input.WasKeyPressed(Keys.Subtract))
                _timeScale = MathF.Max(_timeScale * 0.5f, 0.25f);
            if (_input.WasKeyPressed(Keys.D0))
                _timeScale = 1f;

            // --- Time controls (mouse click on buttons) ---
            if (_gameData.Settings.General.ShowTimeControls && _input.LeftPressed)
            {
                int tcHit = _hudRenderer.HitTestTimeControls(screenW, screenH, mouse.X, mouse.Y);
                if (tcHit == -2)
                    _paused = !_paused;
                else if (tcHit >= 0)
                {
                    _timeScale = HUDRenderer.TimeControlSpeeds[tcHit];
                    _paused = false;
                }
            }

            // --- Tick pending projectiles ---
            TickPendingProjectiles(dt);

            // --- Simulate ---
            _sim.Tick(dt);
            _dayNightSystem.Update(dt, _gameData);
            _sim.MagicGlyphs.Update(dt, _sim.UnitsMut, _sim.Quadtree, _sim.PoisonClouds, _gameData.Spells);
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

            // Apply spell select-by-name from scenario
            if (_activeScenario.RequestSelectSpellByName != null)
            {
                var name = _activeScenario.RequestSelectSpellByName;
                _activeScenario.RequestSelectSpellByName = null;
                if (_menuState == MenuState.SpellEditor)
                    _spellEditor.SelectByName(name);
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
            if (_input.WasKeyPressed(Keys.R))
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
        _inventoryUI.Update(_input);
        _buildingMenuUI.Update(_input, screenW, screenH);
        _craftingMenu.Update(_input, screenW, screenH, dt);

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

    /// <summary>
    /// Map a pending attack's chosen weapon to an AnimState. Reads the weapon's
    /// AnimName field; falls back to "Ranged1" for ranged and "Attack1" for melee.
    /// Custom anim names (e.g. "AttackBite") map onto Attack1/Ranged1 — the
    /// per-unit AttackAnim override on AnimController handles the actual sprite swap.
    /// </summary>
    private static AnimState ResolvePendingAttackAnim(Data.UnitStats stats,
        int weaponIdx, bool isRanged, byte archetype)
    {
        string? animName = null;
        if (isRanged)
        {
            if (weaponIdx >= 0 && weaponIdx < stats.RangedWeapons.Count)
                animName = stats.RangedWeapons[weaponIdx].AnimName;
        }
        else
        {
            if (weaponIdx >= 0 && weaponIdx < stats.MeleeWeapons.Count)
                animName = stats.MeleeWeapons[weaponIdx].AnimName;
        }

        // Legacy archer fallback: if archetype is ArcherUnit but pending fields weren't
        // set (e.g. unarmed test scenarios), still play Ranged1.
        bool effectiveRanged = isRanged || archetype == AI.ArchetypeRegistry.ArcherUnit;

        if (string.IsNullOrEmpty(animName))
            return effectiveRanged ? AnimState.Ranged1 : AnimState.Attack1;

        return animName switch
        {
            "Attack1"  => AnimState.Attack1,
            "Attack2"  => AnimState.Attack2,
            "Attack3"  => AnimState.Attack3,
            "Ranged1"  => AnimState.Ranged1,
            "Spell1"   => AnimState.Spell1,
            "Special1" => AnimState.Special1,
            _          => effectiveRanged ? AnimState.Ranged1 : AnimState.Attack1,
        };
    }

    private static readonly Random _projRng = new();

    private void SpawnSpellProjectile(SpellDef spell, Vec2 origin, Vec2 target, uint ownerUid)
    {
        _sim.Projectiles.SpawnFireball(origin, target,
            Faction.Undead, ownerUid, spell.Damage, spell.AoeRadius, spell.DisplayName);
        var projs = _sim.Projectiles.Projectiles;
        if (projs.Count > 0)
        {
            var lastProj = projs[projs.Count - 1];

            // Tag projectile with spell ID for physics knockback lookup on impact
            lastProj.SpellID = spell.Id;

            // Apply trajectory type
            var traj = Enum.TryParse<Trajectory>(spell.Trajectory, true, out var t) ? t : Trajectory.Lob;
            var dir = (target - origin).Normalized();
            float speed = spell.ProjectileSpeed > 0 ? spell.ProjectileSpeed : ProjectileManager.MagicSpeed;

            switch (traj)
            {
                case Trajectory.DirectFire:
                {
                    float theta = 5f * MathF.PI / 180f;
                    lastProj.Velocity = dir * speed * MathF.Cos(theta);
                    lastProj.VelocityZ = speed * MathF.Sin(theta);
                    lastProj.BaseDirection = dir;
                    lastProj.IsLob = false;
                    break;
                }
                case Trajectory.Swirly:
                {
                    float theta = 5f * MathF.PI / 180f;
                    lastProj.Velocity = dir * speed * MathF.Cos(theta);
                    lastProj.VelocityZ = speed * MathF.Sin(theta);
                    lastProj.BaseDirection = dir;
                    lastProj.IsLob = false;
                    lastProj.SwirlFreq = 3f + (float)_projRng.NextDouble() * 5f;
                    lastProj.SwirlAmplitude = 0.5f + (float)_projRng.NextDouble() * 1.5f;
                    lastProj.SwirlPhase = (float)_projRng.NextDouble() * 2f * MathF.PI;
                    break;
                }
                case Trajectory.Homing:
                {
                    float theta = 5f * MathF.PI / 180f;
                    lastProj.Velocity = dir * speed * MathF.Cos(theta);
                    lastProj.VelocityZ = speed * MathF.Sin(theta);
                    lastProj.BaseDirection = dir;
                    lastProj.IsLob = false;
                    lastProj.TargetPos = target;
                    lastProj.HomingStrength = 5f;
                    break;
                }
                case Trajectory.HomingSwirly:
                {
                    float theta = 5f * MathF.PI / 180f;
                    lastProj.Velocity = dir * speed * MathF.Cos(theta);
                    lastProj.VelocityZ = speed * MathF.Sin(theta);
                    lastProj.BaseDirection = dir;
                    lastProj.IsLob = false;
                    lastProj.TargetPos = target;
                    lastProj.HomingStrength = 5f;
                    lastProj.SwirlFreq = 3f + (float)_projRng.NextDouble() * 5f;
                    lastProj.SwirlAmplitude = 0.5f + (float)_projRng.NextDouble() * 1.5f;
                    lastProj.SwirlPhase = (float)_projRng.NextDouble() * 2f * MathF.PI;
                    break;
                }
                // Lob is the default from SpawnFireball — no changes needed
            }

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
                lastProj.HitEffectBlendMode = spell.HitEffectFlipbook.BlendMode == "Additive" ? 1 : 0;
                lastProj.HitEffectAlignment = spell.HitEffectFlipbook.Alignment == "Upright" ? 1 : 0;
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
                if (unitDef.AttackAnim != null)
                    ctrl.SetAttackAnimOverride(unitDef.AttackAnim);
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

            // Force out of work anims if interaction was cancelled (WASD override)
            if (_sim.Units[i].CorpseInteractPhase == 0)
            {
                var cur = animData.Ctrl.CurrentState;
                if (cur == AnimState.WorkStart || cur == AnimState.WorkLoop || cur == AnimState.WorkEnd
                    || cur == AnimState.Pickup || cur == AnimState.PutDown)
                    animData.Ctrl.ForceState(AnimState.Idle);
            }

            // --- Corpse interaction state machine ---
            // PlayOnceHold states: ForceState on entry, IsAnimFinished for completion
            if (_sim.Units[i].CorpseInteractPhase != 0)
            {
                byte phase = _sim.Units[i].CorpseInteractPhase;
                const float BaggingDuration = 2.0f;

                switch (phase)
                {
                    case 1: // WorkStart (PlayOnceHold)
                        if (animData.Ctrl.CurrentState != AnimState.WorkStart)
                            animData.Ctrl.ForceState(AnimState.WorkStart);
                        if (animData.Ctrl.IsAnimFinished)
                        {
                            _sim.UnitsMut[i].CorpseInteractPhase = 2;
                            _sim.UnitsMut[i].BaggingTimer = 0f;
                            animData.Ctrl.ForceState(AnimState.WorkLoop);
                        }
                        break;

                    case 2: // WorkLoop (Loop — timer driven)
                        if (animData.Ctrl.CurrentState != AnimState.WorkLoop)
                            animData.Ctrl.ForceState(AnimState.WorkLoop);
                        // Corpse bagging drives timer here; trap building is driven by handler
                        if (_sim.Units[i].Routine == 0) // not in a handler routine
                        {
                            _sim.UnitsMut[i].BaggingTimer += dt;
                            {
                                var bc = _sim.FindCorpseByID(_sim.Units[i].BaggingCorpseID);
                                if (bc != null)
                                    bc.BaggingProgress = Math.Min(1f, _sim.Units[i].BaggingTimer / BaggingDuration);
                            }
                            if (_sim.Units[i].BaggingTimer >= BaggingDuration)
                            {
                                _sim.UnitsMut[i].CorpseInteractPhase = 3;
                                animData.Ctrl.ForceState(AnimState.WorkEnd);
                            }
                        }
                        // else: handler controls timer and transitions CorpseInteractPhase
                        break;

                    case 3: // WorkEnd (PlayOnceHold)
                        if (animData.Ctrl.CurrentState != AnimState.WorkEnd)
                            animData.Ctrl.ForceState(AnimState.WorkEnd);
                        if (animData.Ctrl.IsAnimFinished)
                        {
                            if (_sim.Units[i].Routine == 0) // corpse bagging
                            {
                                var bc = _sim.FindCorpseByID(_sim.Units[i].BaggingCorpseID);
                                if (bc != null)
                                {
                                    bc.Bagged = true;
                                    bc.BaggingProgress = 0f;
                                    bc.BaggedByUnitID = GameConstants.InvalidUnit;
                                }
                                _sim.UnitsMut[i].BaggingCorpseID = -1;
                            }
                            _sim.UnitsMut[i].CorpseInteractPhase = 0;
                            animData.Ctrl.ForceState(AnimState.Idle);
                        }
                        break;

                    case 4: // Pickup — body bag tracks hilt visually via DrawCarriedBodyBag
                        if (animData.Ctrl.CurrentState != AnimState.Pickup)
                            animData.Ctrl.ForceState(AnimState.Pickup);
                        {
                            var cc = _sim.FindCorpseByID(_sim.Units[i].CarryingCorpseID);
                            if (cc != null)
                            {
                                cc.Position = _sim.Units[i].Position; // keep world pos synced for logic
                                cc.FacingAngle = _sim.Units[i].FacingAngle;
                            }
                        }
                        if (animData.Ctrl.IsAnimFinished)
                        {
                            _sim.UnitsMut[i].CorpseInteractPhase = 0;
                            animData.Ctrl.ForceState(AnimState.Carry);
                            animData.Ctrl.PlaybackSpeed = 0f; // freeze until unit moves
                        }
                        break;

                    case 5: // PutDown — body bag tracks hilt visually via DrawCarriedBodyBag
                        if (animData.Ctrl.CurrentState != AnimState.PutDown)
                            animData.Ctrl.ForceState(AnimState.PutDown);
                        {
                            var cc = _sim.FindCorpseByID(_sim.Units[i].CarryingCorpseID);
                            if (cc != null)
                                cc.FacingAngle = _sim.Units[i].FacingAngle;
                        }
                        if (animData.Ctrl.IsAnimFinished)
                        {
                            // Place corpse at pre-computed drop position
                            var cc = _sim.FindCorpseByID(_sim.Units[i].CarryingCorpseID);
                            if (cc != null)
                            {
                                cc.Position = cc.LerpStartPos;
                                cc.DraggedByUnitID = GameConstants.InvalidUnit;
                            }
                            _sim.UnitsMut[i].CarryingCorpseID = -1;
                            _sim.UnitsMut[i].CorpseInteractPhase = 0;
                            animData.Ctrl.ForceState(AnimState.Idle);
                        }
                        break;

                    default:
                        _sim.UnitsMut[i].CorpseInteractPhase = 0;
                        break;
                }

                animData.Ctrl.Update(dt);
                _unitAnims[uid] = animData;
                continue;
            }

            // --- Two-channel animation for archetype units ---
            if (_sim.Units[i].Archetype > 0)
            {
                // Archetype units use the RoutineAnim/OverrideAnim two-channel system.
                // AI handlers set RoutineAnim, combat/damage sets OverrideAnim.
                // AnimResolver picks the winner based on priority.

                // Combat engine overrides: pending attacks get priority 2 override
                if (!_sim.Units[i].PendingAttack.IsNone)
                {
                    var atkState = ResolvePendingAttackAnim(_sim.Units[i].Stats,
                        _sim.Units[i].PendingWeaponIdx, _sim.Units[i].PendingWeaponIsRanged,
                        _sim.Units[i].Archetype);
                    float lockout = _gameData.Settings.Combat.PostAttackLockout;
                    float animDur = animData.Ctrl.GetTotalDurationSeconds(atkState);
                    float spd = (animDur > 0f && lockout > 0f) ? MathF.Max(1f, animDur / lockout) : 1f;
                    _sim.UnitsMut[i].OverrideAnim = AnimRequest.Combat(atkState, spd);
                }
                else if (_sim.Units[i].InCombat && _sim.Units[i].AttackCooldown > 0f)
                {
                    // Pre-roll: start attack animation early
                    float cooldownRemaining = _sim.Units[i].AttackCooldown;
                    float effectTime = animData.Ctrl.GetEffectTimeSeconds(AnimState.Attack1);
                    float animDur = animData.Ctrl.GetTotalDurationSeconds(AnimState.Attack1);
                    float lockout = _gameData.Settings.Combat.PostAttackLockout;
                    float spd = (animDur > 0f && lockout > 0f) ? MathF.Max(1f, animDur / lockout) : 1f;
                    float preRollTime = effectTime > 0f ? effectTime / spd : 0f;
                    if (preRollTime > 0f && cooldownRemaining <= preRollTime)
                        _sim.UnitsMut[i].OverrideAnim = AnimRequest.Combat(AnimState.Attack1, spd);
                }

                // Reverse walk playback
                float facingRad2 = _sim.Units[i].FacingAngle * MathF.PI / 180f;
                var facingDir2 = new Vec2(MathF.Cos(facingRad2), MathF.Sin(facingRad2));
                var vel2 = _sim.Units[i].Velocity;
                bool backward2 = vel2.LengthSq() > 0.1f && vel2.Normalized().Dot(facingDir2) < -0.3f;
                animData.Ctrl.SetReversePlayback(backward2);

                AnimResolver.Resolve(_sim.UnitsMut[i], animData.Ctrl, dt);
                animData.Ctrl.Update(dt);
            }
            else
            {
            // --- Legacy animation selection for non-archetype units ---
            AnimState targetState;
            if (_sim.Units[i].InPhysics)
                targetState = AnimState.Fall;
            else if (_sim.Units[i].Incap.Active && !_sim.Units[i].Incap.Recovering)
                targetState = _sim.Units[i].Incap.HoldAnim;
            else if (_sim.Units[i].Incap.Recovering)
            {
                targetState = _sim.Units[i].Incap.RecoverAnim;
                // Set real recovery timer from actual animation duration (first frame only)
                if (_sim.Units[i].Incap.RecoverTimer < 0f)
                {
                    float realDuration = animData.Ctrl.GetTotalDurationSeconds(targetState);
                    if (realDuration <= 0f) realDuration = _sim.Units[i].Incap.RecoverTime; // fallback
                    var incap = _sim.Units[i].Incap;
                    incap.RecoverTimer = realDuration;
                    _sim.UnitsMut[i].Incap = incap;
                }
            }
            else if (_sim.Units[i].Dodging)
                targetState = AnimState.Dodge;
            else if (!_sim.Units[i].PendingAttack.IsNone)
                targetState = ResolvePendingAttackAnim(_sim.Units[i].Stats,
                    _sim.Units[i].PendingWeaponIdx, _sim.Units[i].PendingWeaponIsRanged,
                    _sim.Units[i].Archetype);
            else if (_sim.Units[i].HitReacting)
                targetState = AnimState.BlockReact;
            else if (_sim.Units[i].BlockReacting)
                targetState = AnimState.BlockReact;
            else if (_sim.Units[i].PostAttackTimer > 0f)
                targetState = AnimState.Block;
            else if (_sim.Units[i].InCombat && _sim.Units[i].AttackCooldown > 0f)
            {
                float cooldownRemaining = _sim.Units[i].AttackCooldown;
                float effectTime = animData.Ctrl.GetEffectTimeSeconds(AnimState.Attack1);
                float animDur = animData.Ctrl.GetTotalDurationSeconds(AnimState.Attack1);
                float lockout = _gameData.Settings.Combat.PostAttackLockout;
                float speed = (animDur > 0f && lockout > 0f) ? MathF.Max(1f, animDur / lockout) : 1f;
                float preRollTime = effectTime > 0f ? effectTime / speed : 0f;

                if (preRollTime > 0f && cooldownRemaining <= preRollTime)
                    targetState = AnimState.Attack1;
                else
                    targetState = AnimState.Block;
            }
            else if (_sim.Units[i].GhostMode)
                targetState = AnimState.Hover;
            else
            {
                float speed = _sim.Units[i].Velocity.Length();
                float baseSpeed = _sim.Units[i].Stats.CombatSpeed;
                float jogThreshold = 4f + baseSpeed / 3f;
                float runThreshold = 6f + 2f * baseSpeed / 3f;

                bool carrying = _sim.Units[i].CarryingCorpseID >= 0;
                if (carrying)
                    targetState = AnimState.Carry;
                else if (speed <= 0.25f)
                    targetState = AnimState.Idle;
                else if (speed < jogThreshold)
                    targetState = AnimState.Walk;
                else if (speed < runThreshold)
                    targetState = AnimState.Jog;
                else
                    targetState = AnimState.Run;
            }

            // Reverse walk playback
            float facingRad = _sim.Units[i].FacingAngle * MathF.PI / 180f;
            var facingDir = new Vec2(MathF.Cos(facingRad), MathF.Sin(facingRad));
            var vel = _sim.Units[i].Velocity;
            bool movingBackward = vel.LengthSq() > 0.1f && vel.Normalized().Dot(facingDir) < -0.3f;
            animData.Ctrl.SetReversePlayback(movingBackward);

            if (targetState == AnimState.Carry)
            {
                float speed = _sim.Units[i].Velocity.Length();
                float baseSpeed = _sim.Units[i].Stats.CombatSpeed;
                float speedRatio = baseSpeed > 0f ? speed / baseSpeed : 0f;
                animData.Ctrl.PlaybackSpeed = Math.Clamp(speedRatio, 0f, 1.5f);
            }
            if (targetState == AnimState.Attack1 && animData.Ctrl.CurrentState != AnimState.Attack1)
            {
                float lockout = _gameData.Settings.Combat.PostAttackLockout;
                float animDur = animData.Ctrl.GetTotalDurationSeconds(AnimState.Attack1);
                if (animDur > 0f && lockout > 0f)
                    animData.Ctrl.PlaybackSpeed = MathF.Max(1f, animDur / lockout);
            }

            var currentAnim = animData.Ctrl.CurrentState;
            // ForceState needed to break out of PlayOnceHold animations
            bool needsForce = (currentAnim == AnimState.Sit || currentAnim == AnimState.Sleep
                || currentAnim == AnimState.Fall || currentAnim == AnimState.Knockdown)
                && currentAnim != targetState;
            // Also force INTO Fall/Knockdown from any state
            needsForce |= (targetState == AnimState.Fall || targetState == AnimState.Knockdown)
                && currentAnim != targetState;
            if (_sim.Units[i].Incap.HoldAtEnd && _sim.Units[i].Incap.Active
                && !_sim.Units[i].Incap.Recovering && currentAnim != targetState)
            {
                animData.Ctrl.ForceStateAtEnd(targetState);
                var incap = _sim.Units[i].Incap;
                incap.HoldAtEnd = false; // only snap once
                _sim.UnitsMut[i].Incap = incap;
            }
            else if (needsForce)
                animData.Ctrl.ForceState(targetState);
            else
                animData.Ctrl.RequestState(targetState);
            animData.Ctrl.Update(dt);
            } // end legacy path

            // Action moment handling: route to melee attack or spell cast
            if (animData.Ctrl.ConsumeActionMoment())
            {
                if (_pendingCastAnim != null && i == FindNecromancer()
                    && animData.Ctrl.CurrentState == AnimState.Spell1)
                {
                    // Spell cast action moment: execute the deferred spell
                    var pca = _pendingCastAnim.Value;
                    var spell = _gameData.Spells.Get(pca.SpellID);
                    if (spell != null)
                        ExecuteSpellEffect(spell, i, pca.Target, pca.Slot);
                    _pendingCastAnim = null;
                }
                else if (!_sim.Units[i].PendingAttack.IsNone)
                {
                    _sim.ResolvePendingAttack(i);
                }
            }

            _unitAnims[uid] = animData;
        }

        // Spell1 is PlayOnceTransition — it switches to Idle when done, so we can't check
        // CurrentState == Spell1 after the fact. Instead, detect that the necromancer left
        // Spell1 (no longer in Spell1) while _pendingCastAnim is still set.
        if (_pendingCastAnim != null)
        {
            int necroIdx = FindNecromancer();
            if (necroIdx >= 0)
            {
                uint nUid = _sim.Units[necroIdx].Id;
                bool stillCasting = _unitAnims.TryGetValue(nUid, out var nAnim)
                    && nAnim.Ctrl.CurrentState == AnimState.Spell1;
                if (!stillCasting)
                {
                    // Spell1 ended (transitioned away) — execute spell if action moment didn't fire
                    var pca = _pendingCastAnim.Value;
                    var spell = _gameData.Spells.Get(pca.SpellID);
                    if (spell != null)
                        ExecuteSpellEffect(spell, necroIdx, pca.Target, pca.Slot);
                    _pendingCastAnim = null;
                    RemoveCastingBuffAll(necroIdx);
                }
            }
            else
            {
                // Necromancer gone — clear pending cast
                _pendingCastAnim = null;
            }
        }

        // Update EffectSpawnPos2D / EffectSpawnHeight from weapon tip data
        UpdateEffectSpawnPositions();

        _effectManager.Update(dt);
        _buffVisuals.Update(dt, _sim.Units, _gameData.Buffs, _gameTime);
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

    /// <summary>
    /// Compute weapon hilt/tip world positions for buff weapon particle spawning.
    /// </summary>
    private WeaponAttachRuntime ComputeWeaponAttach(int unitIdx, UnitDef unitDef, UnitAnimData animData)
    {
        var result = new WeaponAttachRuntime();
        if (unitDef.WeaponPoints.Count == 0 || animData.RefFrameHeight <= 0f) return result;

        string animName = AnimController.StateToAnimName(animData.Ctrl.CurrentState);
        if (!unitDef.WeaponPoints.TryGetValue(animName, out var yawDict)) return result;

        int spriteAngle = animData.Ctrl.ResolveAngle(_sim.Units[unitIdx].FacingAngle, out bool flipX);
        string yawKey = spriteAngle.ToString();
        if (!yawDict.TryGetValue(yawKey, out var frames)) return result;

        int frameIdx = animData.Ctrl.GetCurrentFrameIndex(_sim.Units[unitIdx].FacingAngle);
        if (frameIdx < 0 || frameIdx >= frames.Count) return result;

        var wpf = frames[frameIdx];
        bool hiltSet = wpf.Hilt.X != 0f || wpf.Hilt.Y != 0f;
        bool tipSet = wpf.Tip.X != 0f || wpf.Tip.Y != 0f;
        if (!hiltSet && !tipSet) return result;

        float flipMul = flipX ? -1f : 1f;
        float worldH = (unitDef.SpriteWorldHeight > 0 ? unitDef.SpriteWorldHeight : 1.8f)
                       * _sim.Units[unitIdx].SpriteScale;
        float worldScale = worldH / animData.RefFrameHeight;
        float unitHeight = _sim.Units[unitIdx].JumpHeight;

        result.HiltWorld = new Vec2(
            _sim.Units[unitIdx].Position.X + wpf.Hilt.X * worldScale * flipMul,
            _sim.Units[unitIdx].Position.Y);
        result.HiltHeight = unitHeight - wpf.Hilt.Y * worldScale * _camera.Zoom / _camera.HeightScale;
        result.HiltBehind = wpf.Hilt.Behind;

        result.TipWorld = new Vec2(
            _sim.Units[unitIdx].Position.X + wpf.Tip.X * worldScale * flipMul,
            _sim.Units[unitIdx].Position.Y);
        result.TipHeight = unitHeight - wpf.Tip.Y * worldScale * _camera.Zoom / _camera.HeightScale;
        result.TipBehind = wpf.Tip.Behind;

        result.Valid = true;
        return result;
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
        var clearColor = _activeScenario?.BackgroundColor
            ?? LaunchArgs.BgColor
            ?? new Color(30, 30, 40);
        if (useBloom)
            _bloom.BeginScene(GraphicsDevice);
        else
            GraphicsDevice.Clear(clearColor);

        // AlphaBlend with premultiplied-alpha textures (loaded via TextureUtil.LoadPremultiplied)
        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp);

        if (!useBloom)
            GraphicsDevice.Clear(clearColor);

        // --- Ground ---
        if (_activeScenario == null || _activeScenario.WantsGround)
            DrawGround();

        // --- Roads ---
        DrawRoads();

        // --- Ground-layer objects (traps — render above dirt, below grass) ---
        DrawGroundLayerObjects();

        // --- Magic glyphs (ground level, after traps, before walls) ---
        _glyphRenderer.SetContext(_spriteBatch, _pixel, _glowTex, _camera, _renderer, _flipbooks, _gameTime);
        _glyphRenderer.DrawGround(_sim.MagicGlyphs);

        // Build progress bars for blueprint glyphs
        foreach (var g in _sim.MagicGlyphs.Glyphs)
        {
            if (g.State == GameSystems.GlyphState.Blueprint && g.BuildProgress > 0f && g.Alive)
            {
                var gsp = _renderer.WorldToScreen(g.Position, 0f, _camera);
                DrawBuildProgressBar(gsp, g.BuildProgress, g.Radius);
            }
        }

        // --- Walls ---
        DrawWalls();

        // --- Grass overlay ---
        DrawGrass();

        // --- Shadows ---
        _shadowRenderer.Draw(GraphicsDevice, _spriteBatch, _glowTex, _camera, _renderer, _sim, _gameData, _unitAnims, _atlases, _envSystem);

        // --- Corpses ---
        DrawCorpses();

        // --- Units + Environment objects + Poison cloud puffs (merged Y-sort for correct depth) ---
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

        // Spawn new effects from impacts (once per frame, before drawing)
        SpawnImpactEffects();

        // --- Alpha-blended HDR effects (clouds, smoke) ---
        _hdrSpriteEffect?.Parameters["AlphaMode"]?.SetValue(1f);
        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp,
            effect: _hdrSpriteEffect);
        DrawEffectsFiltered(0);
        _spriteBatch.End();

        // --- Additive HDR pass (effects + fireball projectiles) ---
        _hdrSpriteEffect?.Parameters["AlphaMode"]?.SetValue(0f);
        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.Additive, SamplerState.LinearClamp,
            effect: _hdrSpriteEffect);
        DrawProjectilesHdr();
        DrawEffectsFiltered(1);
        _spriteBatch.End();

        // --- Additive blend pass (lightning, energy columns, debug shapes) ---
        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.Additive, SamplerState.LinearClamp);
        _glyphRenderer.DrawEnergyColumns(_sim.MagicGlyphs);
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

        // --- God ray pass (alpha blend + HDR intensity shader) ---
        _lightningRenderer.DrawGodRays();

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
        if (_menuState == MenuState.MapEditor && _input.IsKeyDown(Keys.LeftAlt))
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
                Vec2 mw = _camera.ScreenToWorld(_input.MousePos, screenW, screenH);
                var sp = _renderer.WorldToScreen(mw, 0f, _camera);
                _buildingMenuUI.DrawGhostPreview(_spriteBatch, _pixel, mw, sp, _camera, _renderer);
            }

            // Ghost preview for glyph trap placement (T key)
            if (_trapManager.IsActive)
            {
                Vec2 mw = _camera.ScreenToWorld(_input.MousePos, screenW, screenH);
                var sp = _renderer.WorldToScreen(mw, 0f, _camera);
                const float glyphR = 1.125f;
                bool canPlace = _sim.MagicGlyphs.CanPlace(mw, glyphR);
                float radiusPx = glyphR * _camera.Zoom;
                Render.DrawUtils.DrawCircleOutline(_spriteBatch, _pixel, sp, radiusPx,
                    canPlace ? new Color(50, 200, 50, 120) : new Color(200, 50, 50, 120), 24);
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
                _editorUi.ResetAllState();
                _menuState = MenuState.None;
            }
        }
        else if (_menuState == MenuState.SpellEditor)
        {
            _spellEditor.Draw(screenW, screenH, gameTime);
            if (_spellEditor.WantsClose)
            {
                _spellEditor.WantsClose = false;
                _editorUi.ResetAllState();
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
                _editorUi.ResetAllState();
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
            // Don't render corpses attached to a unit — drawn on unit in DrawSingleUnit
            // (covers carried phase 0, pickup phase 4, putdown phase 5)
            if (corpse.Bagged && corpse.DraggedByUnitID != GameConstants.InvalidUnit)
                continue;

            // Bagged corpses render as BodyBag from Corpses atlas
            if (corpse.Bagged)
            {
                DrawBaggedCorpse(corpse);
                continue;
            }

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
                if (_animMeta.Count > 0)
                    ctrl.SetAnimMeta(_animMeta, unitDef.Sprite.SpriteName);
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
                ctrl.RequestState(corpse.InPhysics ? AnimState.Fall : AnimState.Death);

                float refH = 128f;
                var idle = spriteData.GetAnim("Idle");
                if (idle != null) { var kfs = idle.GetAngle(30); if (kfs != null && kfs.Count > 0) refH = kfs[0].Frame.Rect.Height; }

                cad = new UnitAnimData { Ctrl = ctrl, AtlasID = atlasId, RefFrameHeight = refH, CachedDefID = corpse.UnitDefID };
                _corpseAnims[corpse.CorpseID] = cad;
            }

            // When corpse lands from knockback arc, snap to final death frame
            if (!corpse.InPhysics && cad.Ctrl.CurrentState == AnimState.Fall)
                cad.Ctrl.ForceStateAtEnd(AnimState.Death);

            if (!cad.Ctrl.IsAnimFinished && !_paused)
                cad.Ctrl.Update(MathF.Min(_rawDt, 1f / 20f) * _timeScale);

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

                var sp = _renderer.WorldToScreen(corpse.Position, corpse.Z, _camera);
                Color corpseTint = new Color(alpha, alpha, alpha, alpha);
                DrawSpriteFrame(atlas, fr.Frame.Value, sp, scale, fr.FlipX, corpseTint);
            }

            // Draw bagging progress bar
            if (corpse.BaggedByUnitID != GameConstants.InvalidUnit && corpse.BaggingProgress > 0f)
            {
                var sp = _renderer.WorldToScreen(corpse.Position, 0f, _camera);
                DrawBaggingProgressBar(sp, corpse.BaggingProgress);
            }
        }
    }

    private FrameResult GetBodyBagFrame(float facingAngle)
    {
        var corpsesAtlasId = AtlasDefs.ResolveAtlasName("Corpses");
        int atlasIdx = (int)corpsesAtlasId;
        if (atlasIdx >= _atlases.Length || !_atlases[atlasIdx].IsLoaded) return default;
        var corpsesAtlas = _atlases[atlasIdx];
        var bodyBagSprite = corpsesAtlas.GetUnit("BodyBag");
        if (bodyBagSprite == null) return default;

        var iconAnim = bodyBagSprite.GetAnim("Icon");
        if (iconAnim == null) return default;

        // Resolve facing angle to sprite angle + flip using AnimController's angle sectors
        var tmpCtrl = new AnimController();
        int spriteAngle = tmpCtrl.ResolveAngle(facingAngle, out bool flipX);

        var kfs = iconAnim.GetAngle(spriteAngle) ?? iconAnim.GetAngle(30);
        if (kfs == null || kfs.Count == 0) return default;

        return new FrameResult { Frame = kfs[0].Frame, FlipX = flipX };
    }

    private float GetBodyBagRefHeight()
    {
        var corpsesAtlasId = AtlasDefs.ResolveAtlasName("Corpses");
        int atlasIdx = (int)corpsesAtlasId;
        if (atlasIdx >= _atlases.Length || !_atlases[atlasIdx].IsLoaded) return 128f;
        var bodyBagSprite = _atlases[atlasIdx].GetUnit("BodyBag");
        if (bodyBagSprite == null) return 128f;
        var iconAnim = bodyBagSprite.GetAnim("Icon");
        if (iconAnim != null) { var kfs = iconAnim.GetAngle(30); if (kfs != null && kfs.Count > 0) return kfs[0].Frame.Rect.Height; }
        return 128f;
    }

    private void DrawBaggedCorpse(Corpse corpse)
    {
        var fr = GetBodyBagFrame(corpse.FacingAngle);
        if (fr.Frame == null) return;

        var corpsesAtlasId = AtlasDefs.ResolveAtlasName("Corpses");
        var corpsesAtlas = _atlases[(int)corpsesAtlasId];

        float refH = GetBodyBagRefHeight();
        float worldH = 3.6f * corpse.SpriteScale;
        float pixelH = worldH * _camera.Zoom;
        float scale = pixelH / refH;

        var sp = _renderer.WorldToScreen(corpse.Position, 0f, _camera);
        DrawSpriteFrame(corpsesAtlas, fr.Frame.Value, sp, scale, fr.FlipX, Color.White);
    }

    private void DrawBaggedCorpseAt(Vector2 screenPos, float facingAngle)
    {
        var fr = GetBodyBagFrame(facingAngle);
        if (fr.Frame == null) return;

        var corpsesAtlasId = AtlasDefs.ResolveAtlasName("Corpses");
        int atlasIdx = (int)corpsesAtlasId;
        if (atlasIdx >= _atlases.Length || !_atlases[atlasIdx].IsLoaded) return;
        var corpsesAtlas = _atlases[atlasIdx];

        float refH = GetBodyBagRefHeight();
        float scale = (3.6f * _camera.Zoom) / refH; // same size as ground body bags
        DrawSpriteFrame(corpsesAtlas, fr.Frame.Value, screenPos, scale, fr.FlipX, Color.White);
    }

    private void DrawBaggingProgressBar(Vector2 screenPos, float progress)
    {
        float barW = 26f;
        float barH = 3f;
        float barX = screenPos.X - barW / 2f;
        float barY = screenPos.Y - 18f;

        _spriteBatch.Draw(_pixel, new Rectangle((int)barX - 1, (int)barY - 1, (int)barW + 2, (int)barH + 2), new Color(0, 0, 0, 180));
        _spriteBatch.Draw(_pixel, new Rectangle((int)barX, (int)barY, (int)(barW * progress), (int)barH), new Color(220, 180, 40));
    }



    private void DrawBuildProgressBar(Vector2 screenPos, float progress, float worldRadius = 0f)
    {
        float barW, barY;
        if (worldRadius > 0f)
        {
            float screenRadiusX = worldRadius * _camera.Zoom;
            float screenRadiusY = screenRadiusX * _camera.YRatio;
            barW = screenRadiusX * 2f;
            barY = screenPos.Y - screenRadiusY - 6f; // above the top of the circle
        }
        else
        {
            barW = 30f;
            barY = screenPos.Y - 22f;
        }
        float barH = 3f;
        float barX = screenPos.X - barW / 2f;

        _spriteBatch.Draw(_pixel, new Rectangle((int)barX - 1, (int)barY - 1, (int)barW + 2, (int)barH + 2), new Color(0, 0, 0, 180));
        _spriteBatch.Draw(_pixel, new Rectangle((int)barX, (int)barY, (int)(barW * progress), (int)barH), new Color(80, 180, 220));
    }

    private void DrawCarriedBodyBag(int unitIdx, Vector2 unitScreenPos, float unitScale, float facingAngle)
    {
        var fr = GetBodyBagFrame(facingAngle);
        if (fr.Frame == null) return;

        var corpsesAtlasId = AtlasDefs.ResolveAtlasName("Corpses");
        int atlasIdx = (int)corpsesAtlasId;
        if (atlasIdx >= _atlases.Length || !_atlases[atlasIdx].IsLoaded) return;
        var corpsesAtlas = _atlases[atlasIdx];

        float refH = GetBodyBagRefHeight();
        float bagScale = (CarryBagScale * _camera.Zoom) / refH;

        // Flip-aware offset: X offset flips with the sprite
        bool flipX = fr.FlipX;
        float ofsX = flipX ? -CarryOffsetX : CarryOffsetX;

        // Position at weapon hilt point if available
        var unitDef = _gameData.Units.Get(_sim.Units[unitIdx].UnitDefID);
        if (unitDef != null && _unitAnims.TryGetValue(_sim.Units[unitIdx].Id, out var animData))
        {
            var attach = ComputeWeaponAttach(unitIdx, unitDef, animData);
            if (attach.Valid)
            {
                var hiltScreen = _renderer.WorldToScreen(attach.HiltWorld, attach.HiltHeight, _camera);
                hiltScreen.X += ofsX;
                hiltScreen.Y += CarryOffsetY;
                DrawSpriteFrame(corpsesAtlas, fr.Frame.Value, hiltScreen, bagScale, fr.FlipX, Color.White);
                return;
            }
        }

        // Fallback: offset-based positioning
        float angleDeg = ((facingAngle % 360f) + 360f) % 360f;
        float offsetPx = 8f * unitScale;
        float hDir = (angleDeg > 90f && angleDeg < 270f) ? -1f : 1f;
        float bagX = unitScreenPos.X + offsetPx * hDir * 0.66f + ofsX;

        float spriteWorldH = (unitDef != null && unitDef.SpriteWorldHeight > 0) ? unitDef.SpriteWorldHeight : 1.8f;
        float spritePixelH = spriteWorldH * _sim.Units[unitIdx].SpriteScale * _camera.Zoom;
        float bagY = unitScreenPos.Y - spritePixelH * 0.35f + CarryOffsetY;

        DrawSpriteFrame(corpsesAtlas, fr.Frame.Value, new Vector2(bagX, bagY), bagScale, fr.FlipX, Color.White);
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

    internal enum DepthItemType : byte { Unit, EnvObject, CloudPuff }

    internal struct DepthItem : IComparable<DepthItem>
    {
        public float Y;
        public DepthItemType Type;
        public int Index;       // Unit index, env object index, or cloud index
        public int SubIndex;    // For cloud puffs: puff index within the cloud
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
                items.Add(new DepthItem { Y = _sim.Units[i].Position.Y, Type = DepthItemType.Unit, Index = i });

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
            items.Add(new DepthItem { Y = obj.Y, Type = DepthItemType.EnvObject, Index = i });
        }

        // Add poison cloud puffs
        _poisonCloudRenderer.SetContext(_spriteBatch, _glowTex, _camera, _renderer, _flipbooks, _gameTime);
        _poisonCloudRenderer.AddPuffsToDepthList(_sim.PoisonClouds, items);

        items.Sort();

        foreach (var item in items)
        {
            switch (item.Type)
            {
                case DepthItemType.Unit:
                    DrawSingleUnit(item.Index);
                    break;
                case DepthItemType.EnvObject:
                    DrawSingleEnvObject(item.Index);
                    break;
                case DepthItemType.CloudPuff:
                    _poisonCloudRenderer.DrawSinglePuff(item.Index, item.SubIndex);
                    break;
            }
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

        float heightOffset = _sim.Units[i].JumpHeight + _sim.Units[i].Z;
        var sp = _renderer.WorldToScreen(_sim.Units[i].Position, heightOffset, _camera);

        // Compute weapon attachment for weapon particle buff visuals
        var weaponAttach = ComputeWeaponAttach(i, unitDef, animData);

        // Update weapon particle emitters (like C++, phase 0 only)
        {
            _wpDefsCache.Clear();
            foreach (var ab in _sim.Units[i].ActiveBuffs)
            {
                var bd = _gameData.Buffs.Get(ab.BuffDefID);
                if (bd != null && bd.HasWeaponParticle && bd.WeaponParticle != null)
                    _wpDefsCache.Add(bd);
            }
            if (_wpDefsCache.Count > 0 || _buffVisuals.HasEmitters(i))
                _buffVisuals.UpdateWeaponParticles(i, _rawDt * _timeScale, _gameTime, _wpDefsCache, weaponAttach, _gameData.Buffs);
        }

        // Buff visuals: phase 0 (behind sprite)
        _buffVisuals.DrawUnit(i, _sim.Units[i].Position, 0, _gameTime,
            _spriteBatch, _camera, _renderer, _flipbooks, _gameData.Buffs, _sim.Units,
            atlas, fr.Frame.Value, scale, fr.FlipX,
            _sim.Units[i].EffectSpawnPos2D, _sim.Units[i].EffectSpawnHeight);

        // Pulsing outline: draw sprite 8 times at directional offsets behind the unit
        DrawUnitPulsingOutline(i, atlas, fr.Frame.Value, sp, scale, fr.FlipX);

        // Ghost mode: subtle blue pulsing outline
        if (_sim.Units[i].GhostMode)
            DrawGhostOutline(atlas, fr.Frame.Value, sp, scale, fr.FlipX);

        // Carried body bag rendering (phase-aware: respects effect_ms action moment)
        byte cPhase = _sim.Units[i].CorpseInteractPhase;
        bool hasCorpse = _sim.Units[i].CarryingCorpseID >= 0
            && (cPhase == 0 || cPhase == 4 || cPhase == 5);
        // Use the same angle resolution as sprite rendering to determine front/back
        int sprAngle = animData.Ctrl.ResolveAngle(_sim.Units[i].FacingAngle, out _);
        bool facingAway = sprAngle == 300; // sprite angle 300 = back view
        bool drawBagAtHilt = false; // whether to draw on unit (vs at ground)

        if (hasCorpse)
        {
            if (cPhase == 0)
                drawBagAtHilt = true; // fully carried
            else if (cPhase == 4) // Pickup: ground until action moment, then hilt
                drawBagAtHilt = animData.Ctrl.HasReachedActionMoment();
            else if (cPhase == 5) // PutDown: hilt until action moment, then ground
                drawBagAtHilt = !animData.Ctrl.HasReachedActionMoment();
        }

        if (hasCorpse && drawBagAtHilt && facingAway)
            DrawCarriedBodyBag(i, sp, scale, _sim.Units[i].FacingAngle);
        if (hasCorpse && !drawBagAtHilt)
        {
            // Draw at ground position (corpse's world pos)
            var cc = _sim.FindCorpseByID(_sim.Units[i].CarryingCorpseID);
            if (cc != null)
            {
                var groundSp = _renderer.WorldToScreen(cc.LerpStartPos, 0f, _camera);
                DrawBaggedCorpseAt(groundSp, cc.FacingAngle);
            }
        }

        DrawSpriteFrame(atlas, fr.Frame.Value, sp, scale, fr.FlipX, tint);

        // Carried body bag: draw IN FRONT if facing toward camera
        if (hasCorpse && drawBagAtHilt && !facingAway)
            DrawCarriedBodyBag(i, sp, scale, _sim.Units[i].FacingAngle);

        // Buff visuals: phase 1 (in front of sprite)
        _buffVisuals.DrawUnit(i, _sim.Units[i].Position, 1, _gameTime,
            _spriteBatch, _camera, _renderer, _flipbooks, _gameData.Buffs, _sim.Units,
            atlas, fr.Frame.Value, scale, fr.FlipX,
            _sim.Units[i].EffectSpawnPos2D, _sim.Units[i].EffectSpawnHeight);

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
            var mouseWorld = _renderer.ScreenToWorld(_input.MousePos, _camera);
            float mouseDist = (objPos - new Vec2(mouseWorld.X, mouseWorld.Y)).Length();
            if (mouseDist < 1.2f && dist < ForagableWiggleRange)
            {
                scale *= 1.1f;
                tint = new Color(1.3f, 1.3f, 1.3f, 1f); // brighten
            }
        }

        // Blueprint visual: semi-transparent with blue tint
        var rt = _envSystem.GetObjectRuntime(i);
        if (rt.BuildProgress < 1f)
        {
            float bpAlpha = 0.35f + 0.15f * rt.BuildProgress; // 0.35 → 0.5 as progress increases
            tint = new Color(0.5f * bpAlpha, 0.7f * bpAlpha, 1f * bpAlpha, bpAlpha);
        }

        _spriteBatch.Draw(tex, screenPos, null, tint, rotation, origin, scale, SpriteEffects.None, 0f);

        // Build progress bar for unbuilt objects
        if (rt.BuildProgress > 0f && rt.BuildProgress < 1f)
            DrawBuildProgressBar(screenPos, rt.BuildProgress);
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
            case "Cloud":
                // Poison cloud icon: hazy green circle
                for (int dy2 = -7; dy2 <= 7; dy2++)
                    for (int dx2 = -7; dx2 <= 7; dx2++)
                    {
                        int dsq = dx2 * dx2 + dy2 * dy2;
                        if (dsq <= 49)
                        {
                            int alpha = dsq < 16 ? 200 : (dsq < 36 ? 140 : 80);
                            _spriteBatch.Draw(_pixel, new Rectangle(cx + dx2, cy + dy2, 1, 1),
                                new Color(80, 200, 60, alpha));
                        }
                    }
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
            // Fireballs are drawn in the additive HDR pass (DrawProjectilesHdr)
            if (proj.Type == ProjectileType.Fireball) continue;

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
                // Clean out later.
                throw new Exception($"Missing ProjectileType: {proj.Type}");
            }
        }
    }

    /// <summary>Draw fireball projectiles with HDR intensity (called in additive HdrSprite pass).</summary>
    private void DrawProjectilesHdr()
    {
        foreach (var proj in _sim.Projectiles.Projectiles)
        {
            if (!proj.Alive || proj.Type != ProjectileType.Fireball) continue;
            var sp = _renderer.WorldToScreen(proj.Position, proj.Height, _camera);

            string fbId = proj.FlipbookID;
            if (!string.IsNullOrEmpty(fbId) && _flipbooks.TryGetValue(fbId, out var fb) && fb.IsLoaded)
            {
                int frameIdx = fb.GetFrameAtTime(proj.Age);
                var srcRect = fb.GetFrameRect(frameIdx);
                float worldSize = proj.ParticleScale * 1.5f;
                float pixelSize = worldSize * _camera.Zoom;
                float scale = pixelSize / srcRect.Width;
                var origin = new Vector2(srcRect.Width / 2f, srcRect.Height / 2f);

                // Trail: draw 2 previous frames behind with lower alpha, then main sprite
                Vec2 velDir = proj.Velocity.Normalized();
                for (int trail = 2; trail >= 0; trail--)
                {
                    float trailOffset = trail * 0.4f * _camera.Zoom;
                    float trailAlpha = (trail == 0) ? 1.0f : (trail == 1) ? 0.5f : 0.25f;
                    float trailScale = (trail == 0) ? 1.0f : (trail == 1) ? 0.8f : 0.6f;

                    int trailFrame = fb.GetFrameAtTime(proj.Age - trail * 0.05f);
                    Rectangle trailSrc = fb.GetFrameRect(trailFrame);

                    Vector2 trailPos = new Vector2(
                        sp.X - velDir.X * trailOffset,
                        sp.Y - velDir.Y * trailOffset * _camera.YRatio
                    );

                    var color = HdrColor.ToHdrVertex(proj.ParticleColor.ToColor(), trailAlpha, proj.ParticleColor.Intensity);
                    _spriteBatch.Draw(fb.Texture, trailPos, trailSrc, color,
                        proj.Age * 2f, origin, scale * trailScale, SpriteEffects.None, 0f);
                }
            }
            else
            {
                // Fallback glow dot
                float glowSize = 6f * _camera.Zoom / 32f;
                var color = HdrColor.ToHdrVertex(new Color(255, 120, 40), 200f / 255f, 1f);
                _spriteBatch.Draw(_pixel, sp, null, color,
                    0f, new Vector2(0.5f, 0.5f), glowSize, SpriteEffects.None, 0f);

                // Trail segments
                float trailLen = 4f * _camera.Zoom / 32f;
                for (int t = 1; t <= 3; t++)
                {
                    var trailPos = _renderer.WorldToScreen(
                        proj.Position - proj.Velocity.Normalized() * (t * 0.3f),
                        proj.Height - proj.VelocityZ * t * 0.02f, _camera);
                    float trailAlpha = (120f / t) / 255f;
                    var tColor = HdrColor.ToHdrVertex(new Color(255, 100, 30), trailAlpha, 1f);
                    _spriteBatch.Draw(_pixel, trailPos, null, tColor,
                        0f, new Vector2(0.5f, 0.5f), trailLen / t, SpriteEffects.None, 0f);
                }
            }
        }
    }

    /// <summary>Draw effects matching the given blend mode (0=alpha, 1=additive).</summary>
    private void DrawEffectsFiltered(int blendMode)
    {
        foreach (var eff in _effectManager.Effects)
        {
            if (!eff.Alive || eff.BlendMode != blendMode) continue;
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
                Color color = blendMode == 0
                    ? HdrColor.ToHdrVertexAlpha(eff.Tint, alpha, eff.HdrIntensity)
                    : HdrColor.ToHdrVertex(eff.Tint, alpha, eff.HdrIntensity);
                _spriteBatch.Draw(fb.Texture, sp, srcRect, color, 0f, origin, fbScale, SpriteEffects.None, 0f);
            }
            else
            {
                // Fallback glow (radial gradient circle)
                float glowAlpha = alpha * (200f / 255f);
                Color color = blendMode == 0
                    ? HdrColor.ToHdrVertexAlpha(eff.Tint, glowAlpha, eff.HdrIntensity)
                    : HdrColor.ToHdrVertex(eff.Tint, glowAlpha, eff.HdrIntensity);
                float glowSize = scale * _camera.Zoom * 0.5f / 32f;
                _spriteBatch.Draw(_glowTex, sp, null, color,
                    0f, new Vector2(32f, 32f), glowSize, SpriteEffects.None, 0f);
            }
        }
    }

    /// <summary>Spawn new effects from projectile impacts (called once per frame, blend-mode independent).</summary>
    private void SpawnImpactEffects()
    {
        foreach (var impact in _sim.Projectiles.Impacts)
        {
            string fbId = impact.HitEffectFlipbookID;
            if (!string.IsNullOrEmpty(fbId))
            {
                _effectManager.SpawnSpellImpact(impact.Position, impact.HitEffectScale,
                    impact.HitEffectColor.ToColor(), fbId, hdrIntensity: impact.HitEffectColor.Intensity,
                    blendMode: impact.HitEffectBlendMode, alignment: impact.HitEffectAlignment);
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
            _potionDropdownSlot, _paused);
    }

    private void DrawPauseMenu(int screenW, int screenH)
    {
        if (_gameData.Settings.General.PauseDimBackground)
            _spriteBatch.Draw(_pixel, new Rectangle(0, 0, screenW, screenH), new Color(0, 0, 0, 150));

        int boxW = 350;
        int btnCount = 9; // Resume + 6 editors + Main Menu + Quit
        int btnH2 = 40, btnGap2 = 10;
        int controlLines = 4;
        int boxH = 60 + btnCount * (btnH2 + btnGap2) + 10 + controlLines * 16 + 20;
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
        int btnW = 280, btnH = 40, btnGap = 10;
        int menuX = boxX + (boxW - btnW) / 2;
        int menuY = boxY + 60;

        DrawMenuButton("Resume", menuX, ref menuY, btnW, btnH, btnGap);
        DrawMenuButton("Unit Editor (F9)", menuX, ref menuY, btnW, btnH, btnGap);
        DrawMenuButton("Spell Editor (F10)", menuX, ref menuY, btnW, btnH, btnGap);
        DrawMenuButton("Map Editor (F11)", menuX, ref menuY, btnW, btnH, btnGap);
        DrawMenuButton("UI Editor (F12)", menuX, ref menuY, btnW, btnH, btnGap);
        DrawMenuButton("Item Editor", menuX, ref menuY, btnW, btnH, btnGap);
        DrawMenuButton("Settings", menuX, ref menuY, btnW, btnH, btnGap);

        menuY += 10;
        DrawMenuButton("Main Menu", menuX, ref menuY, btnW, btnH, btnGap);
        DrawMenuButton("Quit", menuX, ref menuY, btnW, btnH, btnGap);

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
        int btnW = 320, btnH = 55, btnGap = 18;
        int menuX = screenW / 2 - btnW / 2;
        int menuY = screenH / 2 + 20;

        DrawMenuButton("Play", menuX, ref menuY, btnW, btnH, btnGap);
        DrawMenuButton("Scenarios", menuX, ref menuY, btnW, btnH, btnGap);
        DrawMenuButton("Quit", menuX, ref menuY, btnW, btnH, btnGap);

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
        int btnW = 320, btnH = 45, btnGap = 12;
        int menuX = screenW / 2 - btnW / 2;
        int menuY = screenH / 4 + 60;

        var names = new List<string>(ScenarioRegistry.GetNames());
        names.Reverse(); // Newest first
        int visibleCount = Math.Min(names.Count - _scenarioScrollOffset, (screenH - menuY - 80) / (btnH + btnGap));
        for (int i = 0; i < visibleCount; i++)
        {
            int nameIdx = i + _scenarioScrollOffset;
            if (nameIdx >= names.Count) break;
            DrawMenuButton(names[nameIdx], menuX, ref menuY, btnW, btnH, btnGap);
        }

        // Back button
        menuY += 10;
        DrawMenuButton("< Back", menuX, ref menuY, btnW, btnH, btnGap);

        // Scroll hint
        if (names.Count > visibleCount + _scenarioScrollOffset)
            DrawText(_smallFont, "Scroll for more...", new Vector2(screenW / 2f - 50, screenH - 40), new Color(100, 100, 120));
    }

    private void DrawMenuButton(string text, int x, ref int y, int w, int h, int gap)
    {
        int mx = (int)_input.MousePos.X, my = (int)_input.MousePos.Y;
        bool hover = mx >= x && mx < x + w && my >= y && my < y + h;
        Color bg = hover ? new Color(90, 60, 120, 240) : new Color(60, 40, 80, 220);
        _spriteBatch.Draw(_pixel, new Rectangle(x, y, w, h), bg);
        _spriteBatch.Draw(_pixel, new Rectangle(x, y, w, 2), new Color(220, 180, 100, hover ? 255 : 120));
        _spriteBatch.Draw(_pixel, new Rectangle(x, y + h - 2, w, 2), new Color(220, 180, 100, hover ? 255 : 60));

        if (_font != null)
        {
            var textSize = _font.MeasureString(text);
            DrawText(_font, text, new Vector2((int)(x + w / 2f - textSize.X / 2f), (int)(y + (h - textSize.Y) / 2f)),
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
