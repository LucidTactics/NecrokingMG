using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.Core;
using Necroking.Render;
using Necroking.Movement;
using Necroking.GameSystems;
using Necroking.World;
using Necroking.Scenario;
using Necroking.Editor;

namespace Necroking;

public enum MenuState { MainMenu, None, PauseMenu, Settings, UnitEditor, SpellEditor, MapEditor, UIEditor, ScenarioList }

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

    // Data
    private GameData _gameData = new();

    // Simulation
    private Simulation _sim = new();
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
    private Texture2D? _groundVertexMapTex;
    private EffectManager _effectManager = new();
    private BloomRenderer _bloom = new();
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
    private float _spellDropdownScroll;
    private bool _buildingPlacementActive;
    private int _hoveredObjectIdx = -1;
    private int _buildingPlacementSelectedDef = -1;
    private List<int> _buildableDefIndices = new();
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
    private SettingsWindow _settingsWindow = null!;

    // Collision debug
    private CollisionDebugMode _collisionDebugMode = CollisionDebugMode.Off;

    // Scenario state
    private ScenarioBase? _activeScenario;
    private int _scenarioScrollOffset;

    // Per-unit animation data
    private struct UnitAnimData
    {
        public AnimController Ctrl;
        public AtlasID AtlasID;
        public float RefFrameHeight;
        public string CachedDefID;
    }

    private struct DamageNumber
    {
        public Vec2 WorldPos;
        public int Damage;
        public float Timer;
        public float Height;
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
            // Borderless fullscreen: use adapter output bounds
            var adapter = GraphicsAdapter.DefaultAdapter;
            _graphics.PreferredBackBufferWidth = adapter.CurrentDisplayMode.Width;
            _graphics.PreferredBackBufferHeight = adapter.CurrentDisplayMode.Height;
            _graphics.HardwareModeSwitch = false;
            _graphics.IsFullScreen = true;
        }

        Content.RootDirectory = "Content";
        IsMouseVisible = true;

        if (LaunchArgs.Headless)
        {
            // Hide window for headless mode
            Window.IsBorderless = true;
            _graphics.PreferredBackBufferWidth = 320;
            _graphics.PreferredBackBufferHeight = 240;
            _graphics.IsFullScreen = false;
        }
    }

    protected override void Initialize()
    {
        DebugLog.Clear("startup");
        DebugLog.Log("startup", "=== Necroking MG Startup ===");

        if (LaunchArgs.Headless)
            Window.Position = new Point(-10000, -10000);

        // Load game data
        _gameData.Load("data");
        DebugLog.Log("startup", $"GameData: {_gameData.Units.Count} units, {_gameData.Spells.Count} spells, {_gameData.Weapons.Count} weapons");

        // Init renderer
        _renderer.Init(_graphics.PreferredBackBufferWidth, _graphics.PreferredBackBufferHeight);

        // Load atlases
        for (int i = 0; i < (int)AtlasID.Count; i++)
        {
            _atlases[i] = new SpriteAtlas();
            string name = AtlasDefs.Names[i];
            _atlases[i].Load(GraphicsDevice, $"assets/Sprites/{name}.png", $"assets/Sprites/{name}.spritemeta");
        }
        DebugLog.Log("startup", "Atlases loaded");

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
        DebugLog.Log("startup", $"Flipbooks loaded: {_flipbooks.Count}");

        // Load animation metadata from all atlas spritemeta files
        foreach (string name in AtlasDefs.Names)
        {
            string metaPath = $"assets/Sprites/{name}.animationmeta";
            if (File.Exists(metaPath))
                AnimMetaLoader.Load(metaPath, _animMeta);
        }
        DebugLog.Log("startup", $"Animation metadata: {_animMeta.Count} entries");

        // Load animations.json timing overrides (stub)
        if (File.Exists("data/animations.json"))
            DebugLog.Log("startup", "animations.json found");

        // Load weapon_points.json (stub)
        if (File.Exists("data/weapon_points.json"))
            DebugLog.Log("startup", "weapon_points.json found");

        // Init world systems
        _roadSystem.Init();

        // Load map
        string mapPath = "assets/maps/default.json";
        if (File.Exists(mapPath))
        {
            DebugLog.Log("startup", "Loading map from file...");
            MapData.Load(mapPath, _groundSystem, _envSystem, _wallSystem);
            MapData.LoadTriggers("assets/maps/default_triggers.json", _triggerSystem);
            MapData.LoadRoads("assets/maps/default_roads.json", _roadSystem);
            DebugLog.Log("startup", $"Map loaded: ground={_groundSystem.WorldW}x{_groundSystem.WorldH}, objects={_envSystem.ObjectCount}");

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
        DebugLog.Log("startup", $"Ground textures: {_groundSystem.TypeCount}, Env textures: {_envSystem.DefCount}, VertexMap: {(_groundVertexMapTex != null ? "OK" : "NONE")}");

        // Init simulation with map size
        _sim.Init(worldW, worldH, _gameData);
        _sim.SetEnvironmentSystem(_envSystem);
        _sim.SetWallSystem(_wallSystem);

        // Spawn necromancer near map center
        float center = worldW * 0.5f;
        SpawnUnit("necromancer", new Vec2(center, center));

        // Spawn some test units nearby
        for (int i = 0; i < 8; i++)
            SpawnUnit("skeleton", new Vec2(center - 4 + i * 1.2f, center + 3));
        for (int i = 0; i < 5; i++)
            SpawnUnit("soldier", new Vec2(center + 10 + i * 1.5f, center - 5));
        for (int i = 0; i < 2; i++)
            SpawnUnit("knight", new Vec2(center + 12 + i * 2f, center - 8));
        for (int i = 0; i < 3; i++)
            SpawnUnit("archer", new Vec2(center + 14 + i * 1.5f, center - 3));

        _camera.Position = new Vec2(center, center);
        _camera.Zoom = 24f;

        // Load spell bar from data file
        _spellBarState.Slots = new SpellBarSlot[4];
        try
        {
            string sbJson = File.ReadAllText("data/spellbar.json");
            using var sbDoc = System.Text.Json.JsonDocument.Parse(sbJson);
            var slotsArr = sbDoc.RootElement.GetProperty("slots");
            int si = 0;
            foreach (var slot in slotsArr.EnumerateArray())
            {
                if (si >= 4) break;
                _spellBarState.Slots[si] = new SpellBarSlot { SpellID = slot.GetProperty("spellID").GetString() ?? "" };
                si++;
            }
            for (; si < 4; si++) _spellBarState.Slots[si] = new SpellBarSlot { SpellID = "" };
            DebugLog.Log("startup", $"SpellBar loaded: {_spellBarState.Slots[0].SpellID}, {_spellBarState.Slots[1].SpellID}, {_spellBarState.Slots[2].SpellID}, {_spellBarState.Slots[3].SpellID}");
        }
        catch
        {
            _spellBarState.Slots = new[] {
                new SpellBarSlot { SpellID = "fireball" },
                new SpellBarSlot { SpellID = "" },
                new SpellBarSlot { SpellID = "" },
                new SpellBarSlot { SpellID = "" }
            };
        }

        // Load secondary spell bar (keys 1-4)
        _secondaryBarState.Slots = new SpellBarSlot[4];
        for (int si = 0; si < 4; si++) _secondaryBarState.Slots[si] = new SpellBarSlot { SpellID = "" };
        try
        {
            string sbJson2 = File.ReadAllText("data/spellbar.json");
            using var sbDoc2 = System.Text.Json.JsonDocument.Parse(sbJson2);
            if (sbDoc2.RootElement.TryGetProperty("secondary", out var secArr))
            {
                int si2 = 0;
                foreach (var slot in secArr.EnumerateArray())
                {
                    if (si2 >= 4) break;
                    _secondaryBarState.Slots[si2] = new SpellBarSlot { SpellID = slot.GetProperty("spellID").GetString() ?? "" };
                    si2++;
                }
                DebugLog.Log("startup", $"SecondaryBar loaded: {_secondaryBarState.Slots[0].SpellID}, {_secondaryBarState.Slots[1].SpellID}, {_secondaryBarState.Slots[2].SpellID}, {_secondaryBarState.Slots[3].SpellID}");
            }
        }
        catch { /* secondary bar stays empty */ }

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
        DebugLog.Log("startup", "Game world loaded");
        _gameWorldLoaded = true;
        _menuState = MenuState.None;
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
        _gameWorldLoaded = false;
        _gameOver = false;
        _damageNumbers.Clear();
        _unitAnims.Clear();
        _corpseAnims.Clear();
        _effectManager.Clear();

        // Init simulation with a small grid for scenarios
        int gridSize = 64;
        _groundSystem.ClearTypes();
        _groundSystem.Init(gridSize, gridSize);
        _envSystem.Init(gridSize);
        _wallSystem.Init(gridSize, gridSize, gridSize);
        // Add a default wall def so scenarios can render walls
        _wallSystem.Defs.Add(new World.WallVisualDef { Name = "Stone", Color = new Color(130, 130, 130, 255), MaxHP = 100 });
        _sim.Init(gridSize, gridSize, _gameData);
        _sim.SetEnvironmentSystem(_envSystem);
        _sim.SetWallSystem(_wallSystem);

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

        // Give scenario access to road system
        scenario.RoadSystem = _roadSystem;

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

        // Initialize the scenario
        scenario.OnInit(_sim);

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

        Window.Title = $"Necroking - Scenario: {scenarioName}";
        DebugLog.Log("scenario", $"Started scenario: {scenarioName}");
        _gameWorldLoaded = true;
        _menuState = MenuState.None;
    }

    private void SpawnUnit(string unitDefID, Vec2 pos)
    {
        int idx = _sim.UnitsMut.AddUnit(pos, UnitType.Dynamic);
        _sim.UnitsMut.UnitDefID[idx] = unitDefID;

        var unitDef = _gameData.Units.Get(unitDefID);
        if (unitDef == null) return;

        _sim.UnitsMut.SpriteScale[idx] = unitDef.SpriteScale;
        _sim.UnitsMut.OrcaPriority[idx] = unitDef.OrcaPriority;
        _sim.UnitsMut.Radius[idx] = unitDef.Radius;
        _sim.UnitsMut.Size[idx] = unitDef.Size;

        // Build full stats from equipment
        var builtStats = _gameData.Units.BuildStats(unitDefID, _gameData.Weapons, _gameData.Armors, _gameData.Shields);
        _sim.UnitsMut.Stats[idx] = builtStats;
        _sim.UnitsMut.MaxSpeed[idx] = builtStats.CombatSpeed;

        // Faction
        _sim.UnitsMut.Faction[idx] = unitDef.Faction == "Human" ? Faction.Human : Faction.Undead;

        // AI
        _sim.UnitsMut.AI[idx] = unitDef.AI switch
        {
            "PlayerControlled" => AIBehavior.PlayerControlled,
            "AttackClosest" => AIBehavior.AttackClosest,
            "AttackNecromancer" => AIBehavior.AttackNecromancer,
            "GuardKnight" => AIBehavior.GuardKnight,
            "ArcherAttack" => AIBehavior.ArcherAttack,
            _ => AIBehavior.AttackClosest
        };

        // If necromancer, record index in simulation
        if (unitDef.AI == "PlayerControlled")
        {
            // Use reflection-free approach: set via a public method
            _sim.SetNecromancerIndex(idx);
        }

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

                float refH = 128f;
                var idleAnim = spriteData.GetAnim("Idle");
                if (idleAnim != null)
                {
                    var kfs = idleAnim.GetAngle(30);
                    if (kfs != null && kfs.Count > 0)
                        refH = kfs[0].Frame.Rect.Height;
                }

                _unitAnims[_sim.Units.Id[idx]] = new UnitAnimData
                {
                    Ctrl = ctrl,
                    AtlasID = atlasId,
                    RefFrameHeight = refH,
                    CachedDefID = unitDefID
                };
            }
        }
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

        // Load main menu background
        string menuBgPath = Path.Combine("assets", "UI", "Background", "VampireBackground.png");
        if (File.Exists(menuBgPath))
        {
            using var stream = File.OpenRead(menuBgPath);
            _mainMenuBg = Texture2D.FromStream(GraphicsDevice, stream);
        }

        _font = Content.Load<SpriteFont>("DefaultFont");
        _smallFont = Content.Load<SpriteFont>("SmallFont");
        _largeFont = Content.Load<SpriteFont>("LargeFont");

        _bloom.Init(GraphicsDevice, Content,
            _graphics.PreferredBackBufferWidth, _graphics.PreferredBackBufferHeight);

        try { _groundEffect = Content.Load<Microsoft.Xna.Framework.Graphics.Effect>("GroundShader"); }
        catch { _groundEffect = null; }

        _grassRenderer.Init(GraphicsDevice);

        // Init UI editor (read-only viewer, doesn't depend on game systems)
        _uiEditor.Init(_spriteBatch, _pixel, _font, _smallFont);
        string uiDefPath = Path.Combine("assets", "UI", "definitions");
        if (Directory.Exists(uiDefPath))
            _uiEditor.LoadDefinitions(uiDefPath);

        // Init property editor infrastructure
        _editorUi.SetContext(_spriteBatch, _pixel, _font, _smallFont, _largeFont);
        _unitEditor = new UnitEditorWindow(_editorUi);
        _unitEditor.SetGameData(_gameData);
        _unitEditor.SetAtlases(_atlases);
        _unitEditor.SetAnimMeta(_animMeta);
        _spellEditor = new SpellEditorWindow(_editorUi);
        _spellEditor.SetGameData(_gameData);
        _settingsWindow = new SettingsWindow(_editorUi);
        _settingsWindow.SetGameData(_gameData, Path.Combine("data", "settings.json"));
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

        // --- F8 collision debug toggle ---
        if (WasKeyPressed(kb, Keys.F8))
        {
            _collisionDebugMode = (CollisionDebugMode)(((int)_collisionDebugMode + 1) % (int)CollisionDebugMode.Count);
        }

        // --- F9-F12 editor toggles ---
        if (WasKeyPressed(kb, Keys.F9))
        {
            _menuState = _menuState == MenuState.UnitEditor ? MenuState.None : MenuState.UnitEditor;
            _editorUi.ClearActiveField();
        }
        if (WasKeyPressed(kb, Keys.F10))
        {
            _menuState = _menuState == MenuState.SpellEditor ? MenuState.None : MenuState.SpellEditor;
            _editorUi.ClearActiveField();
        }
        if (WasKeyPressed(kb, Keys.F11))
            _menuState = _menuState == MenuState.MapEditor ? MenuState.None : MenuState.MapEditor;
        if (WasKeyPressed(kb, Keys.F12))
            _menuState = _menuState == MenuState.UIEditor ? MenuState.None : MenuState.UIEditor;

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
        if (WasKeyPressed(kb, Keys.Escape))
        {
            if (_menuState == MenuState.Settings)
            {
                _menuState = MenuState.PauseMenu;
            }
            else if (_menuState == MenuState.UnitEditor || _menuState == MenuState.SpellEditor ||
                _menuState == MenuState.MapEditor || _menuState == MenuState.UIEditor)
            {
                _menuState = MenuState.None;
            }
            else if (_menuState == MenuState.PauseMenu)
            {
                _menuState = MenuState.None;
                _paused = false;
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
        if (_menuState == MenuState.UnitEditor || _menuState == MenuState.SpellEditor || _menuState == MenuState.MapEditor || _menuState == MenuState.Settings)
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
            // Other editors: allow arrow key panning
            if (_menuState != MenuState.MapEditor)
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
            var necroPos = _sim.Units.Position[necroIdx];
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
        if (scrollDelta != 0 && _menuState == MenuState.None && !_mouseOverUI)
            _camera.ZoomBy(scrollDelta / 120f);

        // Editors pause the game
        bool editorActive = _menuState == MenuState.UnitEditor || _menuState == MenuState.SpellEditor
            || _menuState == MenuState.MapEditor || _menuState == MenuState.UIEditor;
        bool editorInputActive = editorActive && _editorUi != null && _editorUi.IsTextInputActive;

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
            if (_spellDropdownSlot >= 0 || _secondaryDropdownSlot >= 0)
                _mouseOverUI = true;

            // Building placement panel (left side)
            if (_buildingPlacementActive && mouse.X < 180)
                _mouseOverUI = true;
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
                var necroPos = _sim.Units.Position[necroIdx];
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
                bool canJump = mu.Alive[necroIdx] && !mu.Jumping[necroIdx]
                    && mu.KnockdownTimer[necroIdx] <= 0f && !_pendingSpell.Active;
                if (canJump)
                {
                    float facingRad = mu.FacingAngle[necroIdx] * MathF.PI / 180f;
                    var facingDir = new Vec2(MathF.Cos(facingRad), MathF.Sin(facingRad));
                    mu.Jumping[necroIdx] = true;
                    mu.JumpTimer[necroIdx] = 0f;
                    mu.JumpHeight[necroIdx] = 0f;
                    mu.JumpStartPos[necroIdx] = mu.Position[necroIdx];
                    mu.JumpEndPos[necroIdx] = mu.Position[necroIdx] + facingDir * 4f;
                    mu.JumpIsAttack[necroIdx] = true;
                    mu.JumpAttackFired[necroIdx] = false;
                    mu.JumpDuration[necroIdx] = 1f;
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
                        _sim.Lightning.CancelBeamsForCaster(_sim.Units.Id[necroIdx]);
                        _sim.Lightning.CancelDrainsForCaster(_sim.Units.Id[necroIdx]);
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

                var result = SpellCaster.TryStartSpellCast(spellId, _gameData.Spells, _sim.NecroState,
                    _sim.Units, necroIdx, mouseWorld, _sim.Corpses, _pendingSpell);

                if (result == CastResult.Success)
                {
                    var spell = _gameData.Spells.Get(spellId);
                    if (spell == null) continue;
                    var necroPos = _sim.Units.Position[necroIdx];
                    var necroUid = _sim.Units.Id[necroIdx];

                    switch (spell.Category)
                    {
                        case "Projectile":
                            // Fire first projectile immediately
                            SpawnSpellProjectile(spell, necroPos, mouseWorld, necroUid);
                            // Queue remaining with delay
                            if (spell.Quantity > 1)
                            {
                                _pendingProjectiles.Add(new PendingProjectileGroup
                                {
                                    SpellID = spellId,
                                    Origin = necroPos,
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
                                GlowWidth = spell.StrikeGlowWidth
                            };
                            var sVis = spell.StrikeVisualType == "GodRay" ? StrikeVisual.GodRay : StrikeVisual.Lightning;
                            var sGrp = new GodRayParams { EdgeSoftness = spell.GodRayEdgeSoftness,
                                NoiseSpeed = spell.GodRayNoiseSpeed, NoiseStrength = spell.GodRayNoiseStrength,
                                NoiseScale = spell.GodRayNoiseScale };
                            Enum.TryParse<SpellTargetFilter>(spell.TargetFilter, out var sTF);

                            if (spell.StrikeTargetUnit)
                            {
                                // Zap: caster to nearest enemy near mouse
                                var casterEffPos = necroPos;
                                float casterH = 1.5f; // approx hand height
                                // Find nearest enemy to mouse
                                int enemy = -1;
                                float bestDist = spell.Range * spell.Range;
                                for (int ui = 0; ui < _sim.Units.Count; ui++)
                                {
                                    if (!_sim.Units.Alive[ui] || _sim.Units.Faction[ui] == _sim.Units.Faction[necroIdx]) continue;
                                    float d = (mouseWorld - _sim.Units.Position[ui]).LengthSq();
                                    if (d < bestDist) { bestDist = d; enemy = ui; }
                                }
                                if (enemy >= 0)
                                {
                                    var targetPos = _sim.Units.Position[enemy];
                                    float targetH = 1.0f;
                                    var tDef = _gameData.Units.Get(_sim.Units.UnitDefID[enemy]);
                                    if (tDef != null) targetH = tDef.SpriteWorldHeight * 0.5f;

                                    _sim.Lightning.SpawnZap(casterEffPos, targetPos,
                                        spell.ZapDuration > 0 ? spell.ZapDuration : spell.StrikeDuration,
                                        style, casterH, targetH);
                                    // Direct damage via damage event
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
                            string summonId = spell.SummonUnitID;
                            if (!string.IsNullOrEmpty(summonId))
                            {
                                for (int s2 = 0; s2 < spell.SummonQuantity; s2++)
                                {
                                    float angle = s2 * MathF.PI * 2f / spell.SummonQuantity;
                                    var offset = new Vec2(MathF.Cos(angle), MathF.Sin(angle)) * 2f;
                                    SpawnUnit(summonId, necroPos + offset);
                                }
                            }
                            break;

                        case "Beam":
                        {
                            // Find target unit near mouse
                            int targetIdx = FindClosestEnemyToPoint(mouseWorld, 3f);
                            if (targetIdx >= 0)
                            {
                                _sim.Lightning.SpawnBeam(necroUid, _sim.Units.Id[targetIdx],
                                    spell.Id, spell.Damage, spell.BeamTickRate, spell.BeamRetargetRadius,
                                    new LightningStyle { CoreColor = spell.BeamCoreColor, GlowColor = spell.BeamGlowColor,
                                        CoreWidth = spell.BeamCoreWidth, GlowWidth = spell.BeamGlowWidth });
                                _channelingSlot = slot;
                            }
                            break;
                        }

                        case "Drain":
                        {
                            int targetIdx2 = FindClosestEnemyToPoint(mouseWorld, 5f);
                            if (targetIdx2 >= 0)
                            {
                                _sim.Lightning.SpawnDrain(necroUid, _sim.Units.Id[targetIdx2],
                                    spell.Id, spell.Damage, spell.DrainTickRate, spell.DrainHealPercent,
                                    spell.DrainCorpseHP, spell.DrainReversed, spell.DrainMaxDuration,
                                    spell.DrainTendrilCount, spell.DrainArcHeight, spell.DrainCoreColor, spell.DrainGlowColor);
                                _channelingSlot = slot;
                            }
                            break;
                        }

                        case "Command":
                        {
                            // Order Attack: send all horde units to attack-move toward target
                            for (int ci = 0; ci < _sim.Units.Count; ci++)
                            {
                                if (!_sim.Units.Alive[ci]) continue;
                                if (_sim.Units.Faction[ci] != Faction.Undead) continue;
                                if (_sim.Units.AI[ci] == AIBehavior.PlayerControlled) continue;
                                if (_sim.Units.AI[ci] == AIBehavior.DefendPoint) continue;
                                if (_sim.Units.AI[ci] == AIBehavior.CorpseWorker) continue;

                                _sim.Horde.RemoveUnit(_sim.Units.Id[ci]);
                                _sim.UnitsMut.AI[ci] = AIBehavior.OrderAttack;
                                _sim.UnitsMut.MoveTarget[ci] = mouseWorld;
                                _sim.UnitsMut.Target[ci] = CombatTarget.None;
                            }
                            break;
                        }

                        case "Toggle":
                        {
                            // Toggle effect on necromancer
                            if (spell.ToggleEffect == "ghost_mode")
                                _sim.UnitsMut.GhostMode[necroIdx] = !_sim.Units.GhostMode[necroIdx];
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
                    if (necroIdx >= 0 && !_sim.Units.PendingAttack[necroIdx].IsNone == false)
                    {
                        int meleeTarget = FindClosestEnemyToPoint(
                            _sim.Units.Position[necroIdx], 2f);
                        if (meleeTarget >= 0)
                        {
                            _sim.UnitsMut.Target[necroIdx] = CombatTarget.Unit(_sim.Units.Id[meleeTarget]);
                            _sim.UnitsMut.PendingAttack[necroIdx] = CombatTarget.Unit(_sim.Units.Id[meleeTarget]);
                            _sim.UnitsMut.AttackCooldown[necroIdx] = 2f;
                        }
                    }
                }
            }

            SkipSpellCast:

            // --- Ghost mode toggle (G) ---
            if (WasKeyPressed(kb, Keys.G) && necroIdx >= 0)
                _sim.UnitsMut.GhostMode[necroIdx] = !_sim.Units.GhostMode[necroIdx];

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
                        _sim.Units, necroIdx, mouseWorld, _sim.Corpses, _pendingSpell);
                    if (secResult == CastResult.Success)
                    {
                        var spell2 = _gameData.Spells.Get(secSpellId);
                        if (spell2 == null) continue;
                        var necroPos2 = _sim.Units.Position[necroIdx];
                        var necroUid2 = _sim.Units.Id[necroIdx];
                        switch (spell2.Category)
                        {
                            case "Projectile":
                                SpawnSpellProjectile(spell2, necroPos2, mouseWorld, necroUid2);
                                if (spell2.Quantity > 1)
                                {
                                    _pendingProjectiles.Add(new PendingProjectileGroup
                                    {
                                        SpellID = secSpellId,
                                        Origin = necroPos2,
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
                                    new LightningStyle { CoreColor = spell2.StrikeCoreColor, GlowColor = spell2.StrikeGlowColor, CoreWidth = spell2.StrikeCoreWidth, GlowWidth = spell2.StrikeGlowWidth },
                                    spell2.Id, sv2, gr2, tf2);
                                break;
                            }
                            case "Summon":
                                if (!string.IsNullOrEmpty(spell2.SummonUnitID))
                                    for (int ss = 0; ss < spell2.SummonQuantity; ss++)
                                    { float a2 = ss * MathF.PI * 2f / spell2.SummonQuantity; SpawnUnit(spell2.SummonUnitID, necroPos2 + new Vec2(MathF.Cos(a2), MathF.Sin(a2)) * 2f); }
                                break;
                            case "Command":
                                for (int ci = 0; ci < _sim.Units.Count; ci++)
                                {
                                    if (!_sim.Units.Alive[ci]) continue;
                                    if (_sim.Units.Faction[ci] != Faction.Undead) continue;
                                    if (_sim.Units.AI[ci] == AIBehavior.PlayerControlled) continue;
                                    if (_sim.Units.AI[ci] == AIBehavior.DefendPoint) continue;
                                    if (_sim.Units.AI[ci] == AIBehavior.CorpseWorker) continue;
                                    _sim.Horde.RemoveUnit(_sim.Units.Id[ci]);
                                    _sim.UnitsMut.AI[ci] = AIBehavior.OrderAttack;
                                    _sim.UnitsMut.MoveTarget[ci] = mouseWorld;
                                    _sim.UnitsMut.Target[ci] = CombatTarget.None;
                                }
                                break;
                            case "Toggle":
                                if (spell2.ToggleEffect == "ghost_mode")
                                    _sim.UnitsMut.GhostMode[necroIdx] = !_sim.Units.GhostMode[necroIdx];
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

            // --- Corpse drag (F key) ---
            if (WasKeyPressed(kb, Keys.F) && necroIdx >= 0)
            {
                int currentDrag = _sim.Units.DraggingCorpseIdx[necroIdx];
                if (currentDrag >= 0)
                {
                    if (currentDrag < _sim.Corpses.Count)
                        _sim.CorpsesMut[currentDrag].DraggedByUnitID = GameConstants.InvalidUnit;
                    _sim.UnitsMut.DraggingCorpseIdx[necroIdx] = -1;
                }
                else
                {
                    var np3 = _sim.Units.Position[necroIdx];
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
                        _sim.UnitsMut.DraggingCorpseIdx[necroIdx] = bci;
                        _sim.CorpsesMut[bci].DraggedByUnitID = _sim.Units.Id[necroIdx];
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
                _buildingPlacementActive = !_buildingPlacementActive;
                if (_buildingPlacementActive)
                {
                    // Cache buildable def indices
                    _buildableDefIndices.Clear();
                    for (int di = 0; di < _envSystem.DefCount; di++)
                    {
                        if (_envSystem.Defs[di].PlayerBuildable)
                            _buildableDefIndices.Add(di);
                    }
                    _buildingPlacementSelectedDef = _buildableDefIndices.Count > 0 ? 0 : -1;
                }
            }

            // --- Building placement click ---
            if (_buildingPlacementActive && _buildingPlacementSelectedDef >= 0
                && mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released)
            {
                // Check if click is on the panel (right side) — if so, handle selection instead
                int panelW2 = 220;
                int panelX2 = screenW - panelW2 - 10;
                if (mouse.X >= panelX2)
                {
                    // Panel click — select a building
                    int itemH2 = 24;
                    int listY2 = 46;
                    int clickIdx = (mouse.Y - listY2) / itemH2;
                    if (clickIdx >= 0 && clickIdx < _buildableDefIndices.Count)
                        _buildingPlacementSelectedDef = clickIdx;
                }
                else
                {
                    // World click — place building
                    int defIdx = _buildableDefIndices[_buildingPlacementSelectedDef];
                    _envSystem.AddObject((ushort)defIdx, mouseWorld.X, mouseWorld.Y);
                }
            }

            // --- Time controls ---
            if (WasKeyPressed(kb, Keys.OemPlus) || WasKeyPressed(kb, Keys.Add))
                _timeScale = MathF.Min(_timeScale * 2f, 8f);
            if (WasKeyPressed(kb, Keys.OemMinus) || WasKeyPressed(kb, Keys.Subtract))
                _timeScale = MathF.Max(_timeScale * 0.5f, 0.25f);
            if (WasKeyPressed(kb, Keys.D0))
                _timeScale = 1f;

            // --- Tick pending projectiles ---
            TickPendingProjectiles(dt);

            // --- Simulate ---
            _sim.Tick(dt);

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
                    Height = dmg.Height
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

        // --- Update animations (even when paused for visual polish) ---
        UpdateAnimations(rawDt);

        // --- Update damage numbers ---
        for (int i = _damageNumbers.Count - 1; i >= 0; i--)
        {
            var dn = _damageNumbers[i];
            dn.Timer += rawDt;
            dn.Height += _gameData.Settings.General.DamageNumberSpeed * rawDt;
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

        _prevKb = kb;
        _prevMouse = mouse;
        base.Update(gameTime);
    }

    private bool WasKeyPressed(KeyboardState current, Keys key) =>
        current.IsKeyDown(key) && _prevKb.IsKeyUp(key);

    private int FindClosestEnemyToPoint(Vec2 worldPos, float maxRange)
    {
        float bestDist = maxRange * maxRange;
        int bestIdx = -1;
        for (int i = 0; i < _sim.Units.Count; i++)
        {
            if (!_sim.Units.Alive[i]) continue;
            // Enemy = different faction from necromancer (undead)
            if (_sim.Units.Faction[i] == Faction.Undead) continue;
            float d = (_sim.Units.Position[i] - worldPos).LengthSq();
            if (d < bestDist) { bestDist = d; bestIdx = i; }
        }
        return bestIdx;
    }

    private int FindNecromancer()
    {
        for (int i = 0; i < _sim.Units.Count; i++)
            if (_sim.Units.Alive[i] && _sim.Units.AI[i] == AIBehavior.PlayerControlled)
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
                    uint ownerUid = necroIdx >= 0 ? _sim.Units.Id[necroIdx] : 0;
                    Vec2 origin = necroIdx >= 0 ? _sim.Units.Position[necroIdx] : pg.Origin;
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
                new Color(120, 40, 180, 80), 0f, Vector2.Zero,
                new Vector2(outerR * 2, outerR * 2), SpriteEffects.None, 0f);

            // Inner white bright
            float innerR = 2f;
            _spriteBatch.Draw(_pixel, new Vector2(sp.X - innerR, sp.Y - innerR), null,
                new Color(255, 255, 255, 200), 0f, Vector2.Zero,
                new Vector2(innerR * 2, innerR * 2), SpriteEffects.None, 0f);
        }
    }

    private void UpdateAnimations(float dt)
    {
        for (int i = 0; i < _sim.Units.Count; i++)
        {
            if (!_sim.Units.Alive[i]) continue;

            uint uid = _sim.Units.Id[i];

            if (!_unitAnims.TryGetValue(uid, out var animData))
            {
                // Try to init from defID
                string defID = _sim.Units.UnitDefID[i];
                var unitDef = _gameData.Units.Get(defID);
                if (unitDef?.Sprite == null) continue;
                var atlasId = AtlasDefs.ResolveAtlasName(unitDef.Sprite.AtlasName);
                var spriteData = _atlases[(int)atlasId].GetUnit(unitDef.Sprite.SpriteName);
                if (spriteData == null) continue;

                var ctrl = new AnimController();
                ctrl.Init(spriteData);
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
            if (_sim.Units.Jumping[i])
            {
                var mu = _sim.UnitsMut;
                mu.JumpTimer[i] += dt;
                float t = MathF.Min(mu.JumpTimer[i] / mu.JumpDuration[i], 1f);
                mu.Position[i] = mu.JumpStartPos[i] + (mu.JumpEndPos[i] - mu.JumpStartPos[i]) * t;
                mu.Velocity[i] = Vec2.Zero;
                mu.JumpHeight[i] = t >= 1f ? 0f : 4f * 0.8f * t * (1f - t);

                float takeoffEnd = mu.JumpDuration[i] * 0.35f;
                float landStart = mu.JumpDuration[i] - 0.25f;
                var current = animData.Ctrl.CurrentState;
                bool isAttack = mu.JumpIsAttack[i];
                var midAnim = isAttack ? AnimState.JumpAttackSetup : AnimState.JumpLoop;
                var landAnim = isAttack ? AnimState.JumpAttackHit : AnimState.JumpLand;

                if (mu.JumpTimer[i] < takeoffEnd)
                {
                    if (current != AnimState.JumpTakeoff) animData.Ctrl.ForceState(AnimState.JumpTakeoff);
                }
                else if (mu.JumpTimer[i] < landStart)
                {
                    if (current != midAnim) animData.Ctrl.ForceState(midAnim);
                }
                else
                {
                    if (current != landAnim && t < 1f) animData.Ctrl.ForceState(landAnim);
                    if (isAttack && t >= 1f && !mu.JumpAttackFired[i])
                    {
                        mu.JumpAttackFired[i] = true;
                        _sim.ResolvePendingAttack(i); // reuse melee resolution
                    }
                    if (t >= 1f && current != landAnim)
                    {
                        mu.Jumping[i] = false;
                        mu.JumpHeight[i] = 0f;
                    }
                }
                if (mu.JumpTimer[i] > mu.JumpDuration[i] + 1.5f)
                {
                    mu.Jumping[i] = false;
                    mu.JumpHeight[i] = 0f;
                    animData.Ctrl.ForceState(AnimState.Idle);
                }
                animData.Ctrl.Update(dt);
                _unitAnims[uid] = animData;
                continue;
            }

            // Determine animation state
            AnimState targetState;
            if (_sim.Units.Dodging[i])
                targetState = AnimState.Dodge;
            else if (!_sim.Units.PendingAttack[i].IsNone)
                targetState = AnimState.Attack1;
            else if (_sim.Units.InCombat[i] && _sim.Units.AttackCooldown[i] > 0f)
                targetState = AnimState.Block;
            else if (_sim.Units.InCombat[i])
                targetState = AnimState.Attack1;
            else if (_sim.Units.Velocity[i].LengthSq() > 0.5f)
            {
                float speed = _sim.Units.Velocity[i].Length();
                float baseSpeed = _sim.Units.Stats[i].CombatSpeed;
                // Running = speed > 1.4x base (shift held), Jog = speed > 1.1x base
                if (speed > baseSpeed * 1.4f)
                    targetState = AnimState.Run;
                else if (speed > baseSpeed * 1.1f)
                    targetState = AnimState.Jog;
                else
                    targetState = AnimState.Walk;
            }
            else
                targetState = AnimState.Idle;

            // Reverse walk playback when moving backward relative to facing
            float facingRad = _sim.Units.FacingAngle[i] * MathF.PI / 180f;
            var facingDir = new Vec2(MathF.Cos(facingRad), MathF.Sin(facingRad));
            var vel = _sim.Units.Velocity[i];
            bool movingBackward = false;
            if (vel.LengthSq() > 0.1f)
            {
                float dot = vel.Normalized().Dot(facingDir);
                movingBackward = dot < -0.3f;
            }
            animData.Ctrl.SetReversePlayback(movingBackward);

            animData.Ctrl.RequestState(targetState);
            animData.Ctrl.Update(dt);

            // Resolve pending attacks at action moment
            if (animData.Ctrl.ConsumeActionMoment() && !_sim.Units.PendingAttack[i].IsNone)
                _sim.ResolvePendingAttack(i);

            _unitAnims[uid] = animData;
        }

        _effectManager.Update(dt);
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

        // Begin bloom scene capture
        var bloomSettings = _activeScenario?.BloomOverride ?? _gameData.Settings.Bloom;
        bool useBloom = _bloom.IsInitialized && bloomSettings.Enabled;
        if (useBloom)
            _bloom.BeginScene(GraphicsDevice);
        else
            GraphicsDevice.Clear(new Color(30, 30, 40));

        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp);

        if (!useBloom)
            GraphicsDevice.Clear(new Color(30, 30, 40));

        // --- Ground ---
        DrawGround();

        // --- Roads ---
        DrawRoads();

        // --- Walls ---
        DrawWalls();

        // --- Grass overlay ---
        DrawGrass();

        // --- Shadows ---
        DrawShadows();

        // --- Corpses ---
        DrawCorpses();

        // --- Units + Environment objects (merged Y-sort for correct depth) ---
        DrawUnitsAndObjects();

        // --- Projectiles ---
        DrawProjectiles();
        DrawSoulOrbs();

        _spriteBatch.End();

        // --- Additive blend pass (effects, lightning) ---
        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.Additive, SamplerState.LinearClamp);
        DrawEffects();
        DrawLightning();

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

        // --- Alpha blend pass (damage numbers on top) ---
        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp);
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
                _collisionDebugMode, _envSystem);
            _spriteBatch.End();
        }

        // Restore real camera position (smooth, for input/HUD)
        _camera.Position = realCameraPos;

        // --- HUD (drawn after bloom so it's not affected) ---
        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);

        // --- Weather effects (rain) ---
        DrawWeather(screenW, screenH);

        bool showUI = _activeScenario == null || _activeScenario.WantsUI;
        if (showUI)
            DrawHUD(screenW, screenH);

        if (_buildingPlacementActive)
            DrawBuildingPlacement(screenW, screenH);

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

        // Resume normal SpriteBatch
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

    private void DrawBuildingPlacement(int screenW, int screenH)
    {
        if (_buildableDefIndices.Count == 0)
        {
            // Show message that no buildable defs exist
            DrawText(_font, "No buildable structures available.", new Vector2(screenW - 260, 10), new Color(200, 100, 100));
            return;
        }

        // --- Right-side panel ---
        int panelW = 220;
        int panelX = screenW - panelW - 10;
        int panelY = 10;
        int itemH = 24;
        int headerH = 32;
        int panelH = headerH + _buildableDefIndices.Count * itemH + 10;

        // Background
        _spriteBatch.Draw(_pixel, new Rectangle(panelX, panelY, panelW, panelH), new Color(25, 25, 40, 230));
        _spriteBatch.Draw(_pixel, new Rectangle(panelX, panelY, panelW, headerH), new Color(50, 40, 70, 240));
        _spriteBatch.Draw(_pixel, new Rectangle(panelX, panelY + headerH, panelW, 1), new Color(100, 80, 140));

        // Title
        DrawText(_font, "Build (B)", new Vector2(panelX + 8, panelY + 6), new Color(255, 220, 140));

        // List items
        var mouse = Mouse.GetState();
        for (int i = 0; i < _buildableDefIndices.Count; i++)
        {
            int defIdx = _buildableDefIndices[i];
            var def = _envSystem.Defs[defIdx];
            int itemY = panelY + headerH + 4 + i * itemH;

            bool isSelected = (i == _buildingPlacementSelectedDef);
            bool isHovered = mouse.X >= panelX && mouse.X < panelX + panelW
                && mouse.Y >= itemY && mouse.Y < itemY + itemH;

            // Highlight
            if (isSelected)
                _spriteBatch.Draw(_pixel, new Rectangle(panelX + 2, itemY, panelW - 4, itemH), new Color(80, 60, 120, 200));
            else if (isHovered)
                _spriteBatch.Draw(_pixel, new Rectangle(panelX + 2, itemY, panelW - 4, itemH), new Color(60, 50, 80, 150));

            string label = string.IsNullOrEmpty(def.Name) ? def.Id : def.Name;
            Color textColor = isSelected ? new Color(255, 230, 160) : new Color(190, 190, 210);
            DrawText(_smallFont, label, new Vector2(panelX + 10, itemY + 4), textColor);
        }

        // --- Ghost preview at mouse position ---
        if (_buildingPlacementSelectedDef >= 0 && mouse.X < panelX)
        {
            int defIdx = _buildableDefIndices[_buildingPlacementSelectedDef];
            var def = _envSystem.Defs[defIdx];
            Vec2 mouseWorld = _camera.ScreenToWorld(new Vector2(mouse.X, mouse.Y), screenW, screenH);
            var sp = _renderer.WorldToScreen(mouseWorld, 0f, _camera);

            // Draw ghost rectangle for the building
            var tex = _envSystem.GetDefTexture(defIdx);
            if (tex != null)
            {
                float worldH = def.SpriteWorldHeight * def.Scale;
                float pixelH = worldH * _camera.Zoom;
                float scale = pixelH / tex.Height;
                var origin = new Vector2(def.PivotX * tex.Width, def.PivotY * tex.Height);
                _spriteBatch.Draw(tex, sp, null, new Color(255, 255, 255, 120), 0f, origin, scale, SpriteEffects.None, 0f);
            }
            else
            {
                // No texture — draw a placeholder ghost rectangle
                float ghostSize = 2f * _camera.Zoom;
                float ghostSizeY = ghostSize * _camera.YRatio;
                _spriteBatch.Draw(_pixel, new Rectangle(
                    (int)(sp.X - ghostSize / 2), (int)(sp.Y - ghostSizeY),
                    (int)ghostSize, (int)ghostSizeY),
                    new Color(100, 200, 100, 100));
            }

            // Label
            string name = string.IsNullOrEmpty(def.Name) ? def.Id : def.Name;
            DrawText(_smallFont, name, new Vector2(sp.X + 10, sp.Y - 20), new Color(200, 255, 200, 200));
        }
    }

    private void DrawShadows()
    {
        var shadow = _gameData.Settings.Shadow;
        if (!shadow.Enabled) return;

        float angleRad = shadow.SunAngle * MathF.PI / 180f;
        float offsetX = MathF.Cos(angleRad) * shadow.LengthScale;
        float offsetY = MathF.Sin(angleRad) * shadow.LengthScale;
        byte alphaB = (byte)(Math.Clamp(shadow.Opacity, 0f, 1f) * 255);
        var shadowColor = new Color((byte)0, (byte)0, (byte)0, alphaB);

        for (int i = 0; i < _sim.Units.Count; i++)
        {
            if (!_sim.Units.Alive[i]) continue;
            float unitRadius = _sim.Units.Radius[i];
            var worldPos = _sim.Units.Position[i];
            // Offset shadow position by sun direction (in world space, scaled by unit radius)
            var shadowWorld = new Vec2(worldPos.X + offsetX * unitRadius, worldPos.Y + offsetY * unitRadius);
            var sp = _renderer.WorldToScreen(shadowWorld, 0f, _camera);
            float r = unitRadius * _camera.Zoom * (1f - shadow.Squash * 0.5f);
            float ry = r * _camera.YRatio * (1f - shadow.Squash);

            // Ellipse shadow approximation using stretched pixel
            _spriteBatch.Draw(_pixel, new Rectangle((int)(sp.X - r), (int)(sp.Y - ry * 0.5f), (int)(r * 2), (int)ry),
                shadowColor);
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
                    corpseTint = new Color((byte)Math.Min(255, alpha + 80), (byte)(alpha / 2), (byte)alpha, alpha);
                else
                    corpseTint = new Color(alpha, alpha, alpha, alpha);
                DrawSpriteFrame(atlas, fr.Frame.Value, sp, scale, fr.FlipX, corpseTint);
            }
        }
    }

    // Sortable item for merged unit+object depth sorting
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

        // Build merged sort list
        var items = new List<DepthItem>();

        // Add units
        for (int i = 0; i < _sim.Units.Count; i++)
            if (_sim.Units.Alive[i])
                items.Add(new DepthItem { Y = _sim.Units.Position[i].Y, IsUnit = true, Index = i });

        // Add environment objects (with view culling)
        for (int i = 0; i < _envSystem.ObjectCount; i++)
        {
            var obj = _envSystem.Objects[i];
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
        uint uid = _sim.Units.Id[i];
        if (!_unitAnims.TryGetValue(uid, out var animData)) return;

        var unitDef = _gameData.Units.Get(_sim.Units.UnitDefID[i]);
        if (unitDef == null) return;

        var atlas = _atlases[(int)animData.AtlasID];
        if (!atlas.IsLoaded) return;

        var fr = animData.Ctrl.GetCurrentFrame(_sim.Units.FacingAngle[i]);
        if (fr.Frame == null) return;

        float worldH = (unitDef.SpriteWorldHeight > 0 ? unitDef.SpriteWorldHeight : 1.8f) * _sim.Units.SpriteScale[i];
        float pixelH = worldH * _camera.Zoom;
        float scale = pixelH / animData.RefFrameHeight;

        Color tint = _sim.Units.Faction[i] == Faction.Undead
            ? new Color(190, 210, 190)
            : new Color(210, 195, 185);

        // Apply buff tinting
        foreach (var buff in _sim.Units.ActiveBuffs[i])
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

        // Ghost mode: semi-transparent blue tint
        if (_sim.Units.GhostMode[i])
            tint = new Color((byte)(tint.R * 0.5f), (byte)(tint.G * 0.5f), (byte)(tint.B + 80), (byte)140);

        float heightOffset = _sim.Units.JumpHeight[i];
        var sp = _renderer.WorldToScreen(_sim.Units.Position[i], heightOffset, _camera);

        // Hit shake effect
        if (_sim.Units.HitShakeTimer[i] > 0f)
        {
            var rng = new Random((int)(_gameTime * 1000) + i);
            sp.X += (rng.NextSingle() - 0.5f) * 4f;
            sp.Y += (rng.NextSingle() - 0.5f) * 3f;
        }

        DrawSpriteFrame(atlas, fr.Frame.Value, sp, scale, fr.FlipX, tint);
        DrawHPBar(i, sp);

        // --- Feature 1: Weapon point text during attack ---
        if (!_sim.Units.PendingAttack[i].IsNone)
        {
            var stats = _sim.Units.Stats[i];
            string weaponName = stats.MeleeWeapons.Count > 0 ? stats.MeleeWeapons[0].Name : "Unarmed";
            if (!string.IsNullOrEmpty(weaponName) && _smallFont != null)
            {
                var weaponPos = new Vector2(sp.X + 10, sp.Y - 55);
                DrawText(_smallFont, weaponName, weaponPos, new Color(255, 220, 140, 220));
            }
        }

        // --- Feature 2: Buff indicator dots above HP bar ---
        if (_sim.Units.ActiveBuffs[i].Count > 0)
        {
            float dotStartX = sp.X - (_sim.Units.ActiveBuffs[i].Count * 5f) / 2f;
            float dotY = sp.Y - 52f;
            int dotIdx = 0;
            foreach (var buff in _sim.Units.ActiveBuffs[i])
            {
                var buffDef = _gameData.Buffs.Get(buff.BuffDefID);
                Color dotColor;
                if (buffDef?.UnitTint != null && buffDef.UnitTint.A > 0)
                    dotColor = new Color(buffDef.UnitTint.R, buffDef.UnitTint.G, buffDef.UnitTint.B, 220);
                else
                    dotColor = new Color(100, 200, 100, 220);
                _spriteBatch.Draw(_pixel, new Rectangle((int)(dotStartX + dotIdx * 5), (int)dotY, 4, 4), dotColor);
                dotIdx++;
            }
        }
    }

    private void DrawSingleEnvObject(int i)
    {
        var obj = _envSystem.Objects[i];
        var def = _envSystem.Defs[obj.DefIndex];
        var tex = _envSystem.GetDefTexture(obj.DefIndex);
        if (tex == null) return;

        float worldH = def.SpriteWorldHeight * obj.Scale * def.Scale;
        float pixelH = worldH * _camera.Zoom;
        float scale = pixelH / tex.Height;

        var screenPos = _renderer.WorldToScreen(new Vec2(obj.X, obj.Y), 0f, _camera);
        var origin = new Vector2(def.PivotX * tex.Width, def.PivotY * tex.Height);

        _spriteBatch.Draw(tex, screenPos, null, Color.White, 0f, origin, scale, SpriteEffects.None, 0f);
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

    private void DrawHPBar(int unitIdx, Vector2 screenPos)
    {
        var stats = _sim.Units.Stats[unitIdx];
        if (stats.MaxHP <= 0) return;

        float hpRatio = (float)stats.HP / stats.MaxHP;
        if (hpRatio >= 1f) return; // don't show full HP bars

        // Position HP bar above the unit based on its sprite height
        var unitDef = _gameData.Units.Get(_sim.Units.UnitDefID[unitIdx]);
        float spriteWorldH = (unitDef != null && unitDef.SpriteWorldHeight > 0)
            ? unitDef.SpriteWorldHeight : 1.8f;
        float spriteScale = _sim.Units.SpriteScale[unitIdx];
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
                    _spriteBatch.Draw(fb.Texture, sp, srcRect, new Color(color.R, color.G, color.B, color.A),
                        proj.Age * 2f, origin, scale, SpriteEffects.None, 0f);
                }
                else
                {
                    // Fallback glow dot
                    float glowSize = 6f * _camera.Zoom / 32f;
                    _spriteBatch.Draw(_pixel, sp, null, new Color(255, 120, 40, 200),
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
                    _spriteBatch.Draw(_pixel, trailPos, null, new Color((byte)255, (byte)100, (byte)30, alpha),
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

    private void DrawLightning()
    {
        // Draw active strikes
        foreach (var strike in _sim.Lightning.Strikes)
        {
            if (!strike.Alive) continue;

            if (strike.TelegraphTimer < strike.TelegraphDuration)
            {
                // Telegraph: pulsing circle on ground
                var sp = _renderer.WorldToScreen(strike.TargetPos, 0f, _camera);
                float pulse = 0.5f + 0.5f * MathF.Sin(strike.TelegraphTimer * 20f);
                float radius = strike.AoeRadius * _camera.Zoom * pulse;
                byte alpha = (byte)(100 * pulse);
                _spriteBatch.Draw(_glowTex, sp, null, new Color((byte)255, (byte)200, (byte)100, alpha),
                    0f, new Vector2(32f, 32f), new Vector2(radius * 2 / 32f, radius * _camera.YRatio / 32f), SpriteEffects.None, 0f);
            }
            else
            {
                var sp = _renderer.WorldToScreen(strike.TargetPos, 0f, _camera);
                float fade = 1f - strike.EffectTimer / strike.EffectDuration;

                if (strike.Visual == StrikeVisual.GodRay)
                {
                    // God ray: beam from sky to ground
                    float sH = GraphicsDevice.Viewport.Height;
                    DrawGodRay(new Vector2(sp.X - 200f, sp.Y - sH * 0.6f), sp,
                        strike.Style, strike.GodRay, _gameTime, strike.EffectTimer, strike.EffectDuration);
                }
                else
                {
                    // Lightning effect: bright flash (radial glow, not rectangle)
                    float radius = strike.AoeRadius * _camera.Zoom;
                    byte coreAlpha = (byte)(255 * fade);
                    var coreColor = strike.Style.CoreColor.ToScaledColor();
                    _spriteBatch.Draw(_glowTex, sp, null,
                        new Color(coreColor.R, coreColor.G, coreColor.B, coreAlpha),
                        0f, new Vector2(32f, 32f), new Vector2(radius / 32f, radius * _camera.YRatio * 0.5f / 32f),
                        SpriteEffects.None, 0f);

                    // Procedural lightning bolt from sky
                    DrawLightningBolt(new Vector2(sp.X - 50f, sp.Y - 400f), sp, strike.Style, fade);
                }
            }
        }

        // Draw active zaps
        foreach (var zap in _sim.Lightning.Zaps)
        {
            if (!zap.Alive) continue;
            var startSp = _renderer.WorldToScreen(zap.StartPos, zap.StartHeight, _camera);
            var endSp = _renderer.WorldToScreen(zap.EndPos, zap.EndHeight, _camera);
            float fade = 1f - zap.Timer / zap.Duration;
            DrawLightningBolt(startSp, endSp, zap.Style, fade);
        }

        // Draw active beams
        foreach (var beam in _sim.Lightning.Beams)
        {
            if (!beam.Alive) continue;
            int casterIdx = UnitUtil.ResolveUnitIndex(_sim.Units, beam.CasterID);
            int targetIdx = UnitUtil.ResolveUnitIndex(_sim.Units, beam.TargetID);
            if (casterIdx < 0 || targetIdx < 0) continue;

            var startSp = _renderer.WorldToScreen(_sim.Units.Position[casterIdx], 1.5f, _camera);
            var endSp = _renderer.WorldToScreen(_sim.Units.Position[targetIdx], 1f, _camera);
            DrawLightningBolt(startSp, endSp, beam.Style, 1f);
        }

        // Draw active drains
        foreach (var drain in _sim.Lightning.Drains)
        {
            if (!drain.Alive) continue;
            int casterIdx = UnitUtil.ResolveUnitIndex(_sim.Units, drain.CasterID);
            if (casterIdx < 0) continue;

            Vec2 targetPos = drain.TargetCorpseIdx >= 0 ? drain.CorpsePos : Vec2.Zero;
            int targetIdx = UnitUtil.ResolveUnitIndex(_sim.Units, drain.TargetID);
            if (targetIdx >= 0) targetPos = _sim.Units.Position[targetIdx];

            var startSp = _renderer.WorldToScreen(_sim.Units.Position[casterIdx], 1.5f, _camera);
            var endSp = _renderer.WorldToScreen(targetPos, 1f, _camera);

            // Draw multiple tendrils with sway
            for (int t = 0; t < drain.TendrilCount; t++)
            {
                float offset = (t - drain.TendrilCount / 2f) * 8f;
                float sway = MathF.Sin(drain.Elapsed * 3f + t * 2f) * 6f;
                var swayStart = new Vector2(startSp.X + offset, startSp.Y);
                var swayEnd = new Vector2(endSp.X + sway, endSp.Y);
                DrawTendril(swayStart, swayEnd, drain.CoreColor, drain.GlowColor, drain.Elapsed);
            }
        }
    }

    private void DrawLightningBolt(Vector2 start, Vector2 end, LightningStyle style, float fade)
    {
        // Procedural jagged lightning bolt
        var dir = end - start;
        float length = dir.Length();
        if (length < 1f) return;
        var norm = dir / length;
        var perp = new Vector2(-norm.Y, norm.X);

        int segments = Math.Max(4, (int)(length / 15f));
        var points = new Vector2[segments + 1];
        points[0] = start;
        points[segments] = end;

        // Generate jagged midpoints
        uint seed = (uint)(start.X * 1000 + end.Y * 7 + _gameTime * 60);
        for (int i = 1; i < segments; i++)
        {
            float t = i / (float)segments;
            var basePos = Vector2.Lerp(start, end, t);
            seed = seed * 1103515245 + 12345;
            float displacement = ((seed % 1000) / 500f - 1f) * style.CoreWidth * 8f * (1f - MathF.Abs(t - 0.5f) * 2f);
            points[i] = basePos + perp * displacement;
        }

        // Draw segments
        byte coreAlpha = (byte)(fade * 255);
        var coreColor = style.CoreColor.ToScaledColor();
        var glowColor = style.GlowColor.ToScaledColor();

        for (int i = 0; i < segments; i++)
        {
            var segDir = points[i + 1] - points[i];
            float segLen = segDir.Length();
            if (segLen < 0.5f) continue;
            float angle = MathF.Atan2(segDir.Y, segDir.X);

            // Glow (wider, dimmer)
            _spriteBatch.Draw(_pixel, points[i], null,
                new Color(glowColor.R, glowColor.G, glowColor.B, (byte)(coreAlpha * 0.4f)),
                angle, new Vector2(0, 0.5f), new Vector2(segLen, style.GlowWidth * fade),
                SpriteEffects.None, 0f);

            // Core (narrow, bright)
            _spriteBatch.Draw(_pixel, points[i], null,
                new Color(coreColor.R, coreColor.G, coreColor.B, coreAlpha),
                angle, new Vector2(0, 0.5f), new Vector2(segLen, style.CoreWidth * fade),
                SpriteEffects.None, 0f);
        }
    }

    private static float GodRayNoise(float y, float x, float t, float scale, float speed)
    {
        float s1 = MathF.Sin(y * scale + t * speed * 2.1f + x * 0.3f);
        float s2 = MathF.Sin(y * scale * 1.7f - t * speed * 1.4f + x * 0.5f);
        float s3 = MathF.Sin(y * scale * 0.6f + t * speed * 0.8f - x * 0.2f);
        return (s1 * s2 + s3) * 0.5f + 0.5f;
    }

    private void DrawGodRay(Vector2 sky, Vector2 ground, LightningStyle style, GodRayParams p,
                             float elapsed, float effectTimer, float effectDuration)
    {
        float shimmer = MathF.Sin(elapsed * 8f) * 0.15f + 0.85f;
        float baseAlpha = shimmer;

        if (effectDuration > 0f)
        {
            float remaining = effectDuration - effectTimer;
            if (remaining < 0.15f) baseAlpha *= MathF.Max(0f, remaining / 0.15f);
        }
        if (baseAlpha <= 0.001f) return;

        var core = style.CoreColor.ToScaledColor();
        var glow = style.GlowColor.ToScaledColor();
        var mid = new Color((byte)((core.R + glow.R) / 2), (byte)((core.G + glow.G) / 2),
                            (byte)((core.B + glow.B) / 2), (byte)((core.A + glow.A) / 2));

        float cw = style.CoreWidth;
        float gw = style.GlowWidth;

        // 4 layers from outer glow to inner core
        float[] layerT = { 1f, 0.66f, 0.33f, 0f };
        Color[] layerColors = { glow, mid, core, core };
        float[] layerAlphas = { 0.12f, 0.25f, 0.45f, 0.75f };

        float edgeSoft = MathF.Max(0f, MathF.Min(1f, p.EdgeSoftness));
        const int EdgeSublayers = 3;
        const int Slices = 20;

        for (int li = 0; li < 4; li++)
        {
            float w = cw + (gw - cw) * layerT[li];
            float widthTop = 5f * w;
            float widthBottom = 30f * w;
            Color lc = layerColors[li];
            float lAlpha = layerAlphas[li];

            // Draw edge sub-layers (wider, more transparent) then core layer
            for (int sub = EdgeSublayers; sub >= 0; sub--)
            {
                float expand = sub > 0 ? edgeSoft * sub / EdgeSublayers : 0f;
                float subAlphaMul = sub > 0 ? (1f / (sub + 1)) * 0.5f : 1f;
                float wMul = 1f + expand;
                float layerA = baseAlpha * lAlpha * subAlphaMul;
                if (layerA <= 0.001f) continue;

                byte ca = (byte)(lc.A * MathF.Min(1f, layerA));

                for (int s = 0; s < Slices; s++)
                {
                    float t0 = s / (float)Slices;
                    float t1 = (s + 1) / (float)Slices;

                    float y0 = sky.Y + (ground.Y - sky.Y) * t0;
                    float y1 = sky.Y + (ground.Y - sky.Y) * t1;
                    float cx0 = sky.X + (ground.X - sky.X) * t0;
                    float cx1 = sky.X + (ground.X - sky.X) * t1;
                    float hw0 = (widthTop + (widthBottom - widthTop) * t0) * wMul;
                    float hw1 = (widthTop + (widthBottom - widthTop) * t1) * wMul;

                    // Noise modulation on innermost sub-layer
                    float n = 1f;
                    if (p.NoiseStrength > 0.001f && sub == 0)
                    {
                        float raw = GodRayNoise(t0 * 10f, cx0 * 0.01f, elapsed, p.NoiseScale, p.NoiseSpeed);
                        n = 1f - p.NoiseStrength * 0.6f + p.NoiseStrength * 0.6f * raw;
                    }

                    byte sliceA = (byte)(ca * n);
                    Color sliceColor = new(lc.R, lc.G, lc.B, sliceA);

                    // Draw quad as two segments (left half, right half)
                    float midX0 = cx0;
                    float midX1 = cx1;
                    float sliceH = y1 - y0;
                    if (sliceH < 0.5f) continue;

                    // Left side of trapezoid
                    _spriteBatch.Draw(_pixel, new Vector2(midX0 - hw0, y0), null, sliceColor,
                        0f, Vector2.Zero, new Vector2(hw0 * 2, sliceH), SpriteEffects.None, 0f);
                }
            }

            // Ground aura ellipse
            float auraW = widthBottom * 1.1f;
            float auraH = widthBottom * 0.35f;
            float auraAlpha = baseAlpha * lAlpha * 0.4f;
            byte ga = (byte)(lc.A * MathF.Min(1f, auraAlpha));
            Color auraColor = new(lc.R, lc.G, lc.B, ga);

            _spriteBatch.Draw(_pixel, new Vector2(ground.X - auraW, ground.Y - auraH * 0.5f), null,
                auraColor, 0f, Vector2.Zero, new Vector2(auraW * 2, auraH), SpriteEffects.None, 0f);
        }
    }

    private void DrawTendril(Vector2 start, Vector2 end, HdrColor coreColor, HdrColor glowColor, float time)
    {
        var dir = end - start;
        float length = dir.Length();
        if (length < 1f) return;
        var norm = dir / length;
        var perp = new Vector2(-norm.Y, norm.X);

        int segments = Math.Max(3, (int)(length / 20f));
        var points = new Vector2[segments + 1];
        points[0] = start;
        points[segments] = end;

        for (int i = 1; i < segments; i++)
        {
            float t = i / (float)segments;
            var basePos = Vector2.Lerp(start, end, t);
            float arc = MathF.Sin(t * MathF.PI) * 20f; // arc height
            float wave = MathF.Sin(time * 4f + t * 8f) * 5f;
            points[i] = basePos + perp * (arc + wave);
        }

        var core = coreColor.ToScaledColor();
        var glow = glowColor.ToScaledColor();

        for (int i = 0; i < segments; i++)
        {
            var segDir = points[i + 1] - points[i];
            float segLen = segDir.Length();
            if (segLen < 0.5f) continue;
            float angle = MathF.Atan2(segDir.Y, segDir.X);

            _spriteBatch.Draw(_pixel, points[i], null,
                new Color(glow.R, glow.G, glow.B, (byte)120),
                angle, new Vector2(0, 0.5f), new Vector2(segLen, 4f), SpriteEffects.None, 0f);
            _spriteBatch.Draw(_pixel, points[i], null,
                new Color(core.R, core.G, core.B, (byte)200),
                angle, new Vector2(0, 0.5f), new Vector2(segLen, 1.5f), SpriteEffects.None, 0f);
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
            string text = dn.Damage.ToString();
            var size = _font.MeasureString(text) * dnScale;
            var pos = new Vector2(sp.X - size.X / 2f, sp.Y - size.Y / 2f);

            // Shadow pass
            var shadowColor = new Color((byte)0, (byte)0, (byte)0, alpha);
            _spriteBatch.DrawString(_font, text, new Vector2(pos.X + 1f, pos.Y + 1f), shadowColor,
                0f, Vector2.Zero, dnScale, SpriteEffects.None, 0f);

            // Text pass — use DamageNumberColor from settings
            var color = new Color((byte)dnColor.R, (byte)dnColor.G, (byte)dnColor.B, alpha);
            _spriteBatch.DrawString(_font, text, pos, color,
                0f, Vector2.Zero, dnScale, SpriteEffects.None, 0f);
        }
    }

    private void DrawHUD(int screenW, int screenH)
    {
        int necroIdx = FindNecromancer();

        // --- Top-left: HP bar ---
        if (necroIdx >= 0)
        {
            var stats = _sim.Units.Stats[necroIdx];
            float hpFrac = stats.MaxHP > 0 ? (float)stats.HP / stats.MaxHP : 0f;
            _spriteBatch.Draw(_pixel, new Rectangle(10, 32, 200, 16), new Color(60, 20, 20));
            _spriteBatch.Draw(_pixel, new Rectangle(10, 32, (int)(200 * hpFrac), 16), new Color(200, 40, 40));
            DrawText(_font, $"HP: {stats.HP}/{stats.MaxHP}", new Vector2(15, 33), Color.White);
        }

        // --- Top-left: Mana bar ---
        float manaFrac = _sim.NecroState.MaxMana > 0 ? _sim.NecroState.Mana / _sim.NecroState.MaxMana : 0f;
        _spriteBatch.Draw(_pixel, new Rectangle(10, 50, 200, 16), new Color(40, 40, 80));
        _spriteBatch.Draw(_pixel, new Rectangle(10, 50, (int)(200 * manaFrac), 16), new Color(80, 80, 220));
        DrawText(_font, $"Mana: {(int)_sim.NecroState.Mana}/{(int)_sim.NecroState.MaxMana}", new Vector2(15, 51), Color.White);

        // Spell bar
        int slotW = 50;
        int slotH = 50;
        int slotY = screenH - 95;
        string[] slotKeys = { "Q", "E", "LC", "RC" };
        for (int s = 0; s < 4; s++)
        {
            int slotX = screenW / 2 - 110 + s * (slotW + 4);
            bool hasSpell = s < _spellBarState.Slots.Length && !string.IsNullOrEmpty(_spellBarState.Slots[s].SpellID);
            Color slotColor = hasSpell ? new Color(50, 50, 70, 200) : new Color(30, 30, 40, 150);
            _spriteBatch.Draw(_pixel, new Rectangle(slotX, slotY, slotW, slotH), slotColor);
            _spriteBatch.Draw(_pixel, new Rectangle(slotX, slotY, slotW, 2), new Color(100, 100, 130, 200));

            if (_smallFont != null)
            {
                DrawText(_smallFont, slotKeys[s], new Vector2(slotX + 2, slotY + 2), new Color(180, 180, 200));

                if (hasSpell)
                {
                    var spell = _gameData.Spells.Get(_spellBarState.Slots[s].SpellID);
                    if (spell != null)
                    {
                        string name = spell.DisplayName.Length > 7 ? spell.DisplayName[..7] : spell.DisplayName;
                        DrawText(_smallFont, name, new Vector2(slotX + 3, slotY + slotH - 14), new Color(200, 200, 220));

                        // Spell category icon (geometric shape)
                        DrawSpellCategoryIcon(spell.Category, slotX + slotW / 2, slotY + slotH / 2 - 2);

                        // Cooldown overlay
                        float cd = _sim.NecroState.GetCooldown(spell.Id);
                        if (cd > 0f)
                        {
                            float cdFrac = MathF.Min(cd / MathF.Max(spell.Cooldown, 0.1f), 1f);
                            int cdH = (int)(slotH * cdFrac);
                            _spriteBatch.Draw(_pixel, new Rectangle(slotX, slotY + slotH - cdH, slotW, cdH),
                                new Color(0, 0, 0, 150));
                            DrawText(_smallFont, $"{cd:F1}", new Vector2(slotX + 12, slotY + 18), new Color(255, 200, 100));
                        }

                        // Not enough mana indicator
                        if (_sim.NecroState.Mana < spell.ManaCost)
                        {
                            _spriteBatch.Draw(_pixel, new Rectangle(slotX, slotY, slotW, slotH),
                                new Color(80, 0, 0, 80));
                        }
                    }
                }
            }
        }

        // Spell dropdown (if open)
        if (_spellDropdownSlot >= 0 && _smallFont != null)
        {
            int ddSlotX = screenW / 2 - 110 + _spellDropdownSlot * (slotW + 4);
            int ddItemH = 20;
            var allSpells = _gameData.Spells.GetIDs();
            int ddH = (allSpells.Count + 1) * ddItemH;
            int ddY = slotY - 10;

            // Background
            _spriteBatch.Draw(_pixel, new Rectangle(ddSlotX - 2, ddY - ddH - 2, 164, ddH + 4), new Color(20, 20, 35, 240));

            // "None" option
            DrawText(_smallFont, "(None)", new Vector2(ddSlotX + 4, ddY - ddItemH), new Color(150, 150, 170));

            // Spell options
            for (int si = 0; si < allSpells.Count; si++)
            {
                var spDef = _gameData.Spells.Get(allSpells[si]);
                int itemY = ddY - (si + 2) * ddItemH;
                string label = spDef != null ? $"{spDef.DisplayName} [{spDef.Category}]" : allSpells[si];
                Color labelColor = _spellBarState.Slots[_spellDropdownSlot].SpellID == allSpells[si]
                    ? new Color(255, 220, 100) : new Color(200, 200, 220);
                DrawText(_smallFont, label, new Vector2(ddSlotX + 4, itemY), labelColor);
            }
        }

        // --- Secondary spell bar (keys 1-4) ---
        {
            int secSlotW = 35, secSlotH = 35;
            int secSlotY = slotY - secSlotH - 6;
            string[] secSlotKeys = { "1", "2", "3", "4" };
            for (int s = 0; s < 4; s++)
            {
                int secSlotX = screenW / 2 - 80 + s * (secSlotW + 4);
                bool hasSecSpell = s < _secondaryBarState.Slots.Length && !string.IsNullOrEmpty(_secondaryBarState.Slots[s].SpellID);
                Color secColor = hasSecSpell ? new Color(45, 50, 65, 180) : new Color(25, 25, 35, 120);
                _spriteBatch.Draw(_pixel, new Rectangle(secSlotX, secSlotY, secSlotW, secSlotH), secColor);
                _spriteBatch.Draw(_pixel, new Rectangle(secSlotX, secSlotY, secSlotW, 2), new Color(90, 90, 120, 180));
                if (_smallFont != null)
                {
                    DrawText(_smallFont, secSlotKeys[s], new Vector2(secSlotX + 2, secSlotY + 1), new Color(160, 160, 180));
                    if (hasSecSpell)
                    {
                        var secSpell = _gameData.Spells.Get(_secondaryBarState.Slots[s].SpellID);
                        if (secSpell != null)
                        {
                            string sn = secSpell.DisplayName.Length > 5 ? secSpell.DisplayName[..5] : secSpell.DisplayName;
                            DrawText(_smallFont, sn, new Vector2(secSlotX + 2, secSlotY + secSlotH - 13), new Color(180, 180, 200));
                            float scd = _sim.NecroState.GetCooldown(secSpell.Id);
                            if (scd > 0f)
                            {
                                float scdFrac = MathF.Min(scd / MathF.Max(secSpell.Cooldown, 0.1f), 1f);
                                int scdH = (int)(secSlotH * scdFrac);
                                _spriteBatch.Draw(_pixel, new Rectangle(secSlotX, secSlotY + secSlotH - scdH, secSlotW, scdH), new Color(0, 0, 0, 150));
                            }
                            if (_sim.NecroState.Mana < secSpell.ManaCost)
                                _spriteBatch.Draw(_pixel, new Rectangle(secSlotX, secSlotY, secSlotW, secSlotH), new Color(80, 0, 0, 80));
                        }
                    }
                }
            }
        }

        // --- Secondary spell bar dropdown ---
        if (_secondaryDropdownSlot >= 0 && _secondaryDropdownSlot < 4 && _smallFont != null)
        {
            int secSlotW3 = 35;
            int secSlotY3 = slotY - 35 - 6;
            int sddSlotX = screenW / 2 - 80 + _secondaryDropdownSlot * (secSlotW3 + 4);
            int sddItemH = 20;
            var secSpellList = _gameData.Spells.GetIDs();
            int sddH = (secSpellList.Count + 1) * sddItemH;
            int sddY = secSlotY3 - 10;

            // Background
            _spriteBatch.Draw(_pixel, new Rectangle(sddSlotX - 2, sddY - sddH - 2, 164, sddH + 4), new Color(20, 20, 35, 240));

            // "None" option
            DrawText(_smallFont, "(None)", new Vector2(sddSlotX + 4, sddY - sddItemH), new Color(150, 150, 170));

            // Spell options
            for (int si = 0; si < secSpellList.Count; si++)
            {
                var spDef = _gameData.Spells.Get(secSpellList[si]);
                int itemY = sddY - (si + 2) * sddItemH;
                string label = spDef != null ? $"{spDef.DisplayName} [{spDef.Category}]" : secSpellList[si];
                Color labelColor = _secondaryBarState.Slots[_secondaryDropdownSlot].SpellID == secSpellList[si]
                    ? new Color(255, 220, 100) : new Color(200, 200, 220);
                DrawText(_smallFont, label, new Vector2(sddSlotX + 4, itemY), labelColor);
            }
        }

        // --- Building hover tooltip ---
        if (_hoveredObjectIdx >= 0 && _hoveredObjectIdx < _envSystem.ObjectCount && _smallFont != null)
        {
            var hovObj = _envSystem.GetObject(_hoveredObjectIdx);
            var hovDef = _envSystem.Defs[hovObj.DefIndex];
            var hovRt = _envSystem.GetObjectRuntime(_hoveredObjectIdx);
            var hovProc = _envSystem.GetProcessState(_hoveredObjectIdx);
            string ownerStr = hovRt.Owner == 0 ? "Undead" : hovRt.Owner == 1 ? "Neutral" : "Human";
            string procStr = hovProc.Processing ? $"Processing ({hovProc.ProcessTimer:F1}s)" : "Idle";
            string[] ttLines = {
                hovDef.Name.Length > 0 ? hovDef.Name : hovDef.Id,
                $"HP: {hovRt.HP}/{hovDef.BuildingMaxHP}",
                $"Owner: {ownerStr}",
                procStr
            };
            var mouse = Mouse.GetState();
            int ttX = mouse.X + 16, ttY = mouse.Y - 70;
            int ttW = 160, ttH = ttLines.Length * 16 + 8;
            _spriteBatch.Draw(_pixel, new Rectangle(ttX - 4, ttY - 4, ttW + 8, ttH + 8), new Color(15, 15, 25, 220));
            _spriteBatch.Draw(_pixel, new Rectangle(ttX - 4, ttY - 4, ttW + 8, 2), new Color(100, 100, 160));
            for (int tl = 0; tl < ttLines.Length; tl++)
                DrawText(_smallFont, ttLines[tl], new Vector2(ttX, ttY + tl * 16), new Color(220, 220, 240));
        }

        // --- Top-left: Unit counts + inventory ---
        int undead = 0, human = 0;
        for (int i = 0; i < _sim.Units.Count; i++)
        {
            if (!_sim.Units.Alive[i]) continue;
            if (_sim.Units.Faction[i] == Faction.Undead) undead++;
            else human++;
        }
        DrawText(_font, $"Enemies: {human} | Undead: {undead}", new Vector2(10, 70), Color.White);

        // Inventory
        int invY = 94;
        foreach (var (matID, count) in _sim.NecroState.Inventory)
        {
            if (count > 0)
            {
                DrawText(_font, $"{matID}: {count}", new Vector2(15, invY), new Color(200, 160, 255));
                invY += 16;
            }
        }

        // --- Bottom: Controls hint ---
        DrawText(_smallFont, "WASD: Move | Scroll: Zoom | ESC: Menu | Space: Jump | G: Ghost | Shift: Run",
            new Vector2(10, screenH - 22), new Color(120, 120, 140, 200));

        // --- Combat log (bottom-left, above controls hint) ---
        if (_gameData.Settings.General.CombatLogEnabled)
        {
            var entries = _sim.CombatLog.Entries;
            int maxLines = _gameData.Settings.General.CombatLogLines;
            float fadeTime = _gameData.Settings.General.CombatLogFadeTime;
            int logFontSize = _gameData.Settings.General.CombatLogFontSize;
            int logBaseY = screenH - 40;
            int linesDrawn = 0;

            for (int li = entries.Count - 1; li >= 0 && linesDrawn < maxLines; li--)
            {
                var e = entries[li];
                float age = _sim.GameTime - e.Timestamp;
                if (age > fadeTime * 3f) continue;
                float fade = age < fadeTime ? 1f : MathF.Max(0f, 1f - (age - fadeTime) / fadeTime);
                byte alpha = (byte)(fade * 200);

                string logLine = e.Outcome switch
                {
                    CombatLogOutcome.Hit => $"{e.AttackerName} hit {e.DefenderName} for {e.NetDamage} ({e.WeaponName})",
                    CombatLogOutcome.Miss => $"{e.AttackerName} missed {e.DefenderName}",
                    CombatLogOutcome.Blocked => $"{e.DefenderName} blocked {e.AttackerName}'s attack",
                    _ => ""
                };

                DrawText(_smallFont, logLine, new Vector2(10, logBaseY - linesDrawn * 16),
                    new Color((byte)200, (byte)200, (byte)200, alpha));
                linesDrawn++;
            }
        }
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

    private void DrawWeather(int screenW, int screenH)
    {
        // Check if weather is enabled and has a preset
        if (!_gameData.Settings.Weather.Enabled) return;
        string presetId = _gameData.Settings.Weather.ActivePreset;
        if (string.IsNullOrEmpty(presetId)) return;
        var preset = _gameData.Weather.Get(presetId);
        if (preset == null) return;
        var fx = preset.Effects;

        // Rain particles (CPU-driven)
        if (fx.RainDensity > 0f)
        {
            int particleCount = (int)(fx.RainDensity * 300);
            float windAngle = fx.RainWindAngle * MathF.PI / 180f;
            float time = _gameTime;

            for (int i = 0; i < particleCount; i++)
            {
                // Deterministic pseudo-random per particle
                float seed = (i * 7919 + 1301) % 10007 / 10007f;
                float seed2 = (i * 6271 + 3037) % 10007 / 10007f;
                float x = seed * screenW;
                float baseY = seed2 * screenH;
                float y = (baseY + time * fx.RainSpeed) % (screenH + fx.RainLength);
                x += MathF.Sin(windAngle) * (y * 0.3f);

                byte alpha = (byte)(fx.RainAlpha * 255 * (0.3f + 0.7f * seed2));
                _spriteBatch.Draw(_pixel,
                    new Vector2(x, y - fx.RainLength), null,
                    new Color((byte)180, (byte)190, (byte)220, alpha),
                    windAngle, Vector2.Zero,
                    new Vector2(1f, fx.RainLength),
                    SpriteEffects.None, 0f);
            }
        }

        // Fog/haze overlay
        if (fx.FogDensity > 0.01f || fx.HazeStrength > 0.01f)
        {
            float fogAlpha = MathF.Min(fx.FogDensity * 0.5f + fx.HazeStrength * 0.3f, 0.4f);
            byte fR = (byte)(fx.FogR * 255), fG = (byte)(fx.FogG * 255), fB = (byte)(fx.FogB * 255);
            _spriteBatch.Draw(_pixel, new Rectangle(0, 0, screenW, screenH),
                new Color(fR, fG, fB, (byte)(fogAlpha * 255)));
        }

        // Brightness/saturation tint overlay (approximate)
        if (fx.Brightness < 0.95f)
        {
            float darkAmount = 1f - fx.Brightness;
            _spriteBatch.Draw(_pixel, new Rectangle(0, 0, screenW, screenH),
                new Color((byte)0, (byte)0, (byte)0, (byte)(darkAmount * 180)));
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
}

public struct SpellBarSlot { public string SpellID; }
public struct SpellBarState { public SpellBarSlot[] Slots; }
