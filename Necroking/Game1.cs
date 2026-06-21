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
    private CharacterStatsUI _characterStatsUI = new();
    private UI.UnitInfoPanel _unitInfoPanel = new();
    private UI.GrimoireOverlay _grimoireOverlay = new();
    private bool _pausedByInspect;
    private UI.SkillBookOverlay _skillBookOverlay = new();
    private SkillBookState _skillBookState = new();
    private GameSystems.DeathFogSystem _deathFog = new();

    private struct SkillLearnToast
    {
        public string Header;   // e.g. "Recipe Learned"
        public string SkillName;
        public string SkillId;  // for clicking through to the right tab
        public float Timer;     // seconds shown so far
        public float Duration;  // seconds total
    }
    private readonly List<SkillLearnToast> _skillLearnToasts = new();
    private UIShaders _uiShaders = null!;

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
    private Game.TableCraftMenuUI _tableMenuUI = new();
    /// <summary>True after Inventory/Building/Crafting/Table menu UIs have been
    /// fully initialized. Initialization is deferred from LoadContent to first-use
    /// (see EnsureInventoryUIsInitialized) — these four UIs collectively cost
    /// ~250ms during startup but most launches never open them. Triggered on the
    /// first input/scenario event that touches an inventory-family UI.</summary>
    private bool _inventoryUIsInitialized;
    /// <summary>UI definitions directory path captured during LoadContent so
    /// EnsureInventoryUIsInitialized can hand it to RuntimeWidgetRenderer when
    /// the deferred init fires (the widget renderer is only used by inventory
    /// family UIs, so its init happens with theirs).</summary>
    private string? _widgetRendererUiDefPath;
    private System.Diagnostics.Stopwatch? _startupTimer;

    /// <summary>Deferred init for Inventory/Building/Crafting/Table menu UIs.
    /// Called lazily on the first frame any of those UIs needs to be drawn or
    /// updated. Idempotent — second call is a no-op via the flag.</summary>
    private void EnsureInventoryUIsInitialized()
    {
        if (_inventoryUIsInitialized) return;
        _inventoryUIsInitialized = true;
        // Initialize the widget renderer first (its definitions get loaded from
        // the UI defs dir captured at startup). Without this, the four UIs below
        // would crash on their first LayoutWidget call.
        _widgetRenderer.Init(GraphicsDevice, _spriteBatch, _fontManager);
        if (_widgetRendererUiDefPath != null && Directory.Exists(_widgetRendererUiDefPath))
            _widgetRenderer.LoadDefinitions(_widgetRendererUiDefPath);
        _inventoryUI.Init(_widgetRenderer, _inventory, _gameData.Items, _spriteBatch, _pixel);
        _buildingMenuUI.Init(_widgetRenderer, _envSystem, _inventory, _gameData.Items,
            _graphics.PreferredBackBufferHeight, _spriteBatch, _pixel,
            _sim.MagicGlyphs, _gameData.Spells, _sim);
        _craftingMenu.Init(_widgetRenderer, _inventory, _gameData.Items, _gameData,
            _graphics.PreferredBackBufferHeight, _spriteBatch, _pixel);
        _craftingMenu.SetSkillBook(_skillBookState);
        _tableMenuUI.Init(_widgetRenderer, _envSystem, _inventory, _gameData.Items,
            _sim.PlayerResources, _spriteBatch, _pixel, _font);
        _tableMenuUI.SetSkillBook(_skillBookState);
        _tableMenuUI.StartCraftCallback = (envIdx) => StartTableCraft(envIdx);
        _tableMenuUI.DrawUnitIconCallback = (defId, rect) => DrawUnitIdleSprite(defId, rect);
        _unitInfoPanel.Init(_widgetRenderer, _gameData);
        _grimoireOverlay.Init(_widgetRenderer, _gameData,
            spell => SpellCaster.HasSpellRequirements(spell, _gameData, _sim.UnitsMut, FindNecromancer())
                // Potion throw-spells (ConsumesItem) stay hidden until the player has
                // actually seen that potion in their inventory at least once.
                && (string.IsNullOrEmpty(spell.ConsumesItem) || _inventory.HasEverSeen(spell.ConsumesItem)));
        ValidatePotionAbilities();
        _unitInfoPanel.DrawUnitIconCallback = (defId, rect) => DrawUnitIdleSprite(defId, rect);
        _unitInfoPanel.OnClosed = () =>
        {
            if (_pausedByInspect) { _paused = false; _pausedByInspect = false; }
        };
        Necroking.Core.DebugLog.Log("startup", "  [LazyInit] Inventory/Building/Crafting/Table UIs initialized on demand");
    }

    private bool _uiEditorInitialized;
    /// <summary>Deferred init for the UI editor (F12 / menu). LoadDefinitions bakes
    /// every harmonized widget/element texture (~4s of CPU work) — a dev-only tool
    /// that most launches never open, so it's paid on first open, not at startup.</summary>
    private void EnsureUIEditorInitialized()
    {
        if (_uiEditorInitialized) return;
        _uiEditorInitialized = true;
        _uiEditor.Init(_spriteBatch, _pixel, _font, _smallFont);
        _uiEditor.SetFontManager(_fontManager);
        string uiDefPath = GamePaths.Resolve(GamePaths.UIDefsDir);
        if (Directory.Exists(uiDefPath))
            _uiEditor.LoadDefinitions(uiDefPath);
        Necroking.Core.DebugLog.Log("startup", "  [LazyInit] UI editor initialized on demand");
    }

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
    private string[] _grassTypeIds = Array.Empty<string>();
    private string[] _grassTypeNames = Array.Empty<string>();
    private Color[] _grassDefaultTints = Array.Empty<Color>();
    private Color[] _grassCorruptedTints = Array.Empty<Color>();
    // Per-type sprite paths (parallel array to the color arrays). One entry per grass
    // type; each entry is a list of 0-5 project-relative PNG paths. Drives the tuft
    // renderer. Populated from the map editor (SyncGrassFromEditor) or the scenario
    // setup path.
    private string[][] _grassTypeSpritePaths = Array.Empty<string[]>();
    private float[] _grassTypeScales = Array.Empty<float>();
    private float[] _grassTypeDensities = Array.Empty<float>();
    private GrassTuftRenderer _grassRenderer = new();

    // Rendering
    private Renderer _renderer = new();
    private Camera25D _camera = new();
    private SpriteAtlas[] _atlases = new SpriteAtlas[0]; // rebuilt in LoadContent from AtlasDefs.TotalCount
    private Dictionary<uint, UnitAnimData> _unitAnims = new(); // keyed by stable unit ID
    private Dictionary<int, UnitAnimData> _corpseAnims = new(); // keyed by corpse ID

    // Carried body bag offset and scale (pixel offsets on top of hilt position)
    private const float CarryOffsetX = 4.5f;
    private const float CarryOffsetY = 8.5f;
    private const float CarryBagScale = 3.4f;
    // Raw-corpse carry: nudge the centroid-pegged corpse vertically off the hands
    // (negative = up, so the body rests slightly on top of the hands). Tunable.
    private const float CarriedCorpseHandOffsetY = -2f;
    // Cache of opaque-pixel centroids per (atlas texture, frame rect), in frame-local
    // top-left pixels. Computed once via GetData; used to balance carried corpses.
    private readonly Dictionary<(Microsoft.Xna.Framework.Graphics.Texture2D, Rectangle), Vector2> _frameCentroidCache = new();
    private bool _autostartExitPending; // --autostart headless: exit once world is loaded
    private Dictionary<string, Flipbook> _flipbooks = new(); // keyed by flipbook ID
    private Dictionary<string, AnimationMeta> _animMeta = new(); // animation metadata
    private Microsoft.Xna.Framework.Graphics.Effect? _groundEffect;
    private Microsoft.Xna.Framework.Graphics.Effect? _dissolveTreeEffect;
    private Microsoft.Xna.Framework.Graphics.Effect? _outlineFlatEffect;
    private Microsoft.Xna.Framework.Graphics.Effect? _wadingEffect;
    private Microsoft.Xna.Framework.Graphics.Effect? _hdrIntensityEffect;
    private readonly Render.WadingWakeSystem _wakeSystem = new();
    /// <summary>Cached gameplay delta from the last Update tick. Drives
    /// frame-rate-independent systems that need dt during the Draw pass
    /// (e.g. wading wake particles). Respects pause and time scale.</summary>
    private float _frameDt;
    private Microsoft.Xna.Framework.Graphics.Effect? _hdrSpriteEffect;
    private Texture2D? _groundVertexMapTex;
    private EffectManager _effectManager = new();
    private BuffVisualSystem _buffVisuals = new();
    private readonly List<Data.Registries.BuffDef> _wpDefsCache = new(); // reused per-unit in DrawSingleUnit
    private BloomRenderer _bloom = new();
    private WeatherRenderer _weatherRenderer = new();
    private FogOfWarSystem _fogOfWar = new();
    private Color _ambientColor = Color.White; // weather ambient tint, applied to lit sprites before bloom
    private DayNightSystem _dayNightSystem = new();
    private LightningRenderer _lightningRenderer = new();
    private PoisonCloudRenderer _poisonCloudRenderer = new();
    private DeathFogRenderer _deathFogRenderer = new();
    private MagicGlyphRenderer _glyphRenderer = new();
    private DebugDraw _debugDraw = new();
    private GameSystems.SpellEffectSystem _spellEffects = new();
    private List<GameSystems.DamageNumber> _damageNumbers = new();

    // Game state
    private MenuState _menuState = MenuState.MainMenu;
    /// <summary>Last frame's <see cref="_menuState"/>, snapshotted at end of
    /// Update. Used to detect transitions — specifically MapEditor → anything
    /// else, where the suppressed per-click pathfinder rebuilds during the
    /// editor session need to fire once so gameplay resumes with current
    /// collision data.</summary>
    private MenuState _prevMenuState = MenuState.MainMenu;
    private bool _gameWorldLoaded;
    private bool _paused;
    private bool _gameOver;
    private float _gameTime;
    private float _timeScale = 1f;

    // Glyph trap placement mode
    private PendingSpellCast _pendingSpell = new();
    private PendingCastAnim? _pendingCastAnim;
    private SpellBarState _spellBarState = new();
    private SpellBarState _secondaryBarState = new();
    private int _spellDropdownSlot = -1;
    private int _secondaryDropdownSlot = -1;
    private int _channelingSlot = -1;
    private readonly Dictionary<string, Texture2D?> _itemTextureCache = new();
    private int _hoveredObjectIdx = -1;
    private KeyboardState _prevKb;
    private MouseState _prevMouse;
    private float _rawDt;

    /// <summary>F2 — overlay raw waterness, computed waterline V, slope, and
    /// the body-bbox bounds on each wading unit. Tuning helper for the per-
    /// direction WadingFractionByDirection values.</summary>
    private bool _waterDebug;
    // Bottom-left perf/zoom readout (frame/sim/draw/present ms). Off by default —
    // toggle with F3 when debugging; it otherwise just clutters the screen.
    private bool _showPerfReadout;

    // Per-frame perf timers — populated each Draw, smoothed via EMA for the
    // HUD readout so the numbers don't jitter. Stale frames keep the EMA
    // value so a paused game shows the last working number.
    private readonly System.Diagnostics.Stopwatch _drawStopwatch = new();
    private readonly System.Diagnostics.Stopwatch _groundDrawStopwatch = new();
    private double _drawMsAvg;
    private double _groundMsAvg;
    private double _gpuPresentMsAvg;
    private readonly InputState _input = new();
    private readonly Necroking.UI.PopupManager _popups = new();
    /// <summary>Process-wide accessor — popups call <c>Game1.Popups.Push(this)</c>
    /// on open and <c>Pop</c> on close. Static because the alternative is
    /// threading the manager through 20+ existing UI constructors. Lifetime
    /// matches the Game1 instance; assigned in the ctor.</summary>
    public static Necroking.UI.PopupManager Popups { get; private set; } = null!;

    // Modal-stack adapters for the top-level editor windows. Each is
    // pushed/popped to keep the stack in sync with <see cref="_menuState"/>;
    // OnCancel transitions the menu state back so PopupManager's ESC routing
    // closes the editor exactly like the old Game1 ESC branch did — except
    // PopupManager handles the entire stack uniformly, so opening sub-popups
    // (eg env-object editor on top of map editor) and closing them with ESC
    // no longer leaks through to the parent editor.
    private readonly Necroking.UI.ActionModalLayer _unitEditorLayer  = new() { LightDismiss = false, IsBlocking = true };
    private readonly Necroking.UI.ActionModalLayer _spellEditorLayer = new() { LightDismiss = false, IsBlocking = true };
    private readonly Necroking.UI.ActionModalLayer _mapEditorLayer   = new() { LightDismiss = false, IsBlocking = true };
    private readonly Necroking.UI.ActionModalLayer _uiEditorLayer    = new() { LightDismiss = false, IsBlocking = true };
    private readonly Necroking.UI.ActionModalLayer _itemEditorLayer  = new() { LightDismiss = false, IsBlocking = true };
    private readonly Necroking.UI.ActionModalLayer _settingsLayer    = new() { LightDismiss = false, IsBlocking = true };
    private readonly Necroking.UI.ActionModalLayer _pauseMenuLayer   = new() { LightDismiss = false, IsBlocking = true };

    // Pending spell cast with animation delay (Spell1 animation → action moment → execute)
    private struct PendingCastAnim
    {
        public string SpellID;
        public Vec2 Target;
        public int Slot;           // spellbar slot that was used
        public bool IsSecondary;   // secondary spellbar
        public string? CastingBuffID; // to remove on animation end

        // Channeled casts (CastAnim ImbueGround/Raise): a Start→Loop→Finish state
        // machine. The effect fires at the END of the loop. Empty/"Spell1" = the
        // legacy single-shot path (effect on the Spell1 effect frame).
        public string CastAnim;
        public byte ChannelPhase;     // 0=Start, 1=Loop, 2=Finish
        public float ChannelElapsed;  // seconds since the cast began
        public float LoopElapsed;     // seconds spent in the Loop phase
        public float CastTime;        // total target cast time (start + loop)
        public bool Executed;         // effect already fired (end of loop)
    }

    private static bool IsChanneledCast(string? castAnim)
        => castAnim == "ImbueGround" || castAnim == "Raise";

    /// <summary>Resolve the Start/Loop/Finish anim states for a channeled cast.
    /// Raise has no Finish (finish == null → go straight to Idle after the loop).</summary>
    private static void GetChannelStates(string castAnim, out AnimState start, out AnimState loop, out AnimState? finish)
    {
        switch (castAnim)
        {
            case "Raise":
                start = AnimState.RaiseStart; loop = AnimState.RaiseLoop; finish = null; break;
            default: // "ImbueGround"
                start = AnimState.ImbueGroundStart; loop = AnimState.ImbueGroundLoop; finish = AnimState.ImbueGroundFinish; break;
        }
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

    // Wind debug (F6): shows gust heatmap + direction arrow
    private bool _windDebug;

    // Scenario state
    private ScenarioBase? _activeScenario;
    private int _scenarioScrollOffset;

    // Per-unit animation data
    internal struct UnitAnimData
    {
        public AnimController Ctrl;
        public int AtlasID; // index into AtlasDefs.Names
        public float RefFrameHeight;
        public string CachedDefID;
    }

    // Foragable arc-pickup state was moved to Game.ForagableSystem (the first
    // Game1 subsystem split, 2026-05-13). Game1 owns the instance and the
    // pickup-sound asset; everything else lives in the system.
    private SoundEffect? _pickupSound;
    private readonly Game.ForagableSystem _foragables = new();
    /// <summary>Wiggle/hover proximity for *idle* on-map foragables (not the
    /// in-flight arc visuals — those live in ForagableSystem). Stays in Game1
    /// because it's read by the on-map render pass, not by the pickup system.</summary>
    private const float ForagableWiggleRange = 3f;

    // DamageNumber and PendingProjectileGroup moved to GameSystems.SpellEffectSystem

    public Game1()
    {
        Popups = _popups;

        // Wire the top-level editor modal layers. Each layer's OnCancel
        // transitions _menuState back; ReconcileTopLevelEditorLayers (run
        // before _popups.RouteInput each frame) keeps the stack in sync
        // with the current menu state, so any keybind / button / pause-
        // menu path that flips the state also flips stack membership.
        _unitEditorLayer.OnCancelAction  = () => { _editorUi?.ResetAllState(); _menuState = MenuState.None; };
        _spellEditorLayer.OnCancelAction = () => { _editorUi?.ResetAllState(); _menuState = MenuState.None; };
        _mapEditorLayer.OnCancelAction   = () => { _editorUi?.ResetAllState(); _menuState = MenuState.None; };
        _uiEditorLayer.OnCancelAction    = () => { _editorUi?.ResetAllState(); _menuState = MenuState.None; };
        _itemEditorLayer.OnCancelAction  = () => { _editorUi?.ResetAllState(); _menuState = MenuState.None; };
        // Settings is reachable only from the pause menu; ESC returns there.
        _settingsLayer.OnCancelAction    = () => { _editorUi?.ResetAllState(); _menuState = MenuState.PauseMenu; };
        // Pause menu: ESC unpauses back to gameplay.
        _pauseMenuLayer.OnCancelAction   = () => { _menuState = MenuState.None; _paused = false; };

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

        // Save settings (to the per-machine 'user settings/', gitignored), weather
        // presets, and spell bar slot assignments when the game exits. Atomic writes.
        Exiting += (_, _) =>
        {
            System.IO.Directory.CreateDirectory(GamePaths.Resolve(GamePaths.UserSettingsDir));
            _gameData.Settings.Save(GamePaths.Resolve(GamePaths.UserSettingsJson));
            _gameData.Weather.Save(GamePaths.Resolve(GamePaths.UserWeatherJson));
            SaveSpellBars();
        };

        if (LaunchArgs.Headless)
        {
            // Hide window for headless mode — use larger resolution if user specifies one
            Window.IsBorderless = true;
            _graphics.PreferredBackBufferWidth = LaunchArgs.ResolutionW > 0 ? LaunchArgs.ResolutionW : 320;
            _graphics.PreferredBackBufferHeight = LaunchArgs.ResolutionH > 0 ? LaunchArgs.ResolutionH : 240;
            _graphics.IsFullScreen = false;
        }

        // Unlock frame-rate up front (must happen before GraphicsDevice is created
        // — toggling SynchronizeWithVerticalRetrace mid-run is unreliable on the
        // DesktopGL backend). Lets benchmark scenarios measure raw GPU throughput.
        if (LaunchArgs.NoVsync)
        {
            _graphics.SynchronizeWithVerticalRetrace = false;
            IsFixedTimeStep = false;
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
        // The pre-LoadContent gap = OS process spawn + .NET runtime init +
        // MonoGame window/GL setup + JIT of code reached during Initialize.
        // We can split it: ProcessStartTime is when the OS forked the process;
        // ProcessStartStopwatch was reset at Main's first instruction.
        // Difference between those = pure runtime/load overhead before any of
        // our code ran. From there to here is mostly MonoGame setup.
        long mainToLoadContentMs = Program.ProcessStartStopwatch.ElapsedMilliseconds;
        long osToMainMs = (long)(DateTime.UtcNow - Program.ProcessStartTime.ToUniversalTime()).TotalMilliseconds - mainToLoadContentMs;
        DebugLog.Log("startup", $"  [pre-LoadContent: ~{osToMainMs + mainToLoadContentMs}ms total — process spawn+runtime: {Math.Max(0, osToMainMs)}ms, MonoGame init+JIT: {mainToLoadContentMs}ms]");

        if (LaunchArgs.Headless)
            Window.Position = new Point(-10000, -10000);
        else if (LaunchArgs.ResolutionW <= 0 && LaunchArgs.ResolutionH <= 0)
            Window.Position = new Point(0, 0);

        // Load game data
        _gameData.Load();
        _inventory = new Inventory(20, _gameData.Items);
        SkillBookDefs.Load();
        _skillBookState.InitFromDefs();
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

        // Build per-atlas work list: base sheet at index 0, overflow __N sheets after.
        // Each entry: (atlasIdx, pngPath, metaPath, isExtension).
        var extSheets = new List<(string png, string meta)>[atlasCount];
        for (int i = 0; i < atlasCount; i++)
            extSheets[i] = new List<(string, string)>(AtlasDefs.FindExtensionSheets(AtlasDefs.Names[i]));

        // Flat parallel decode of every PNG (base + all extension sheets) so the
        // largest single atlas isn't gated by its own extension on the same thread.
        // Meta parsing is fast and runs serially after decode (extension meta has
        // to be parsed after the base meta, per atlas).
        var extDecoded = new (Color[] pixels, int w, int h, bool decoded)[atlasCount][];
        for (int i = 0; i < atlasCount; i++)
            extDecoded[i] = new (Color[], int, int, bool)[extSheets[i].Count];

        // Build flat work list: (atlasIdx, extIdx=-1 for base, pngPath).
        var decodeJobs = new List<(int ai, int ei, string png)>(atlasCount * 2);
        for (int i = 0; i < atlasCount; i++)
        {
            string name = AtlasDefs.Names[i];
            decodeJobs.Add((i, -1, GamePaths.Resolve($"assets/Sprites/{name}.png")));
            for (int e = 0; e < extSheets[i].Count; e++)
                decodeJobs.Add((i, e, extSheets[i][e].png));
        }

        // BENCHMARK: per-job timing across the flat decode pool. Each job tries
        // the .pcache (zstd-compressed pre-decoded RGBA) first; on miss falls
        // back to PNG decode and writes a fresh cache for next launch.
        var decodeBench = new (string label, int sizeMb, long readMs, long decodeMs, long pmaMs, long totalMs, int threadId, bool skia, bool cacheHit, bool wroteCache)[decodeJobs.Count];
        var phaseStart = System.Diagnostics.Stopwatch.StartNew();
        System.Threading.Tasks.Parallel.For(0, decodeJobs.Count, j =>
        {
            var (ai, ei, png) = decodeJobs[j];
            string label = ei < 0 ? AtlasDefs.Names[ai] : $"{AtlasDefs.Names[ai]}__{ei + 1}";
            if (!File.Exists(png)) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int tid = System.Threading.Thread.CurrentThread.ManagedThreadId;

            // FAST PATH: try the zstd-decoded pixel cache.
            long t0 = sw.ElapsedMilliseconds;
            if (Render.AtlasCache.TryLoad(png, out var cachedPixels, out int cw, out int ch))
            {
                long readMs = sw.ElapsedMilliseconds - t0;
                if (ei < 0)
                {
                    decodedPixels[ai] = cachedPixels;
                    decodedW[ai] = cw;
                    decodedH[ai] = ch;
                }
                else
                {
                    extDecoded[ai][ei] = (cachedPixels, cw, ch, true);
                }
                int sizeMbCache = (int)(new FileInfo(Render.AtlasCache.GetCachePath(png)).Length / (1024 * 1024));
                decodeBench[j] = (label, sizeMbCache, readMs, 0, 0, sw.ElapsedMilliseconds, tid, true, true, false);
                return;
            }

            // SLOW PATH: cache miss. Decode the PNG and write a fresh cache.
            t0 = sw.ElapsedMilliseconds;
            byte[] bytes = File.ReadAllBytes(png);
            long pngReadMs = sw.ElapsedMilliseconds - t0;
            int sizeMb = bytes.Length / (1024 * 1024);
            var (pixels, w, h, decTicks, pmaTicks, skia) = TextureUtil.DecodePngPremultipliedTimed(bytes);
            long decodeMs = decTicks * 1000 / System.Diagnostics.Stopwatch.Frequency;
            long pmaMs = pmaTicks * 1000 / System.Diagnostics.Stopwatch.Frequency;
            if (ei < 0)
            {
                decodedPixels[ai] = pixels;
                decodedW[ai] = w;
                decodedH[ai] = h;
            }
            else
            {
                extDecoded[ai][ei] = (pixels, w, h, true);
            }
            // Write cache for next launch (best-effort; failure logs to startup but doesn't abort).
            Render.AtlasCache.Save(png, pixels, w, h);
            decodeBench[j] = (label, sizeMb, pngReadMs, decodeMs, pmaMs, sw.ElapsedMilliseconds, tid, skia, false, true);
        });
        long phaseWallMs = phaseStart.ElapsedMilliseconds;

        // Meta parse: sequential per atlas (base before extensions). Cheap.
        for (int i = 0; i < atlasCount; i++)
        {
            string name = AtlasDefs.Names[i];
            string metaPath = GamePaths.Resolve($"assets/Sprites/{name}.spritemeta");
            if (File.Exists(metaPath))
                metaParsed[i] = _atlases[i].ParseMetaOnly(metaPath);
            for (int e = 0; e < extSheets[i].Count; e++)
            {
                string extMeta = extSheets[i][e].meta;
                bool metaOk = File.Exists(extMeta) && _atlases[i].ParseExtensionMeta(extMeta);
                if (!metaOk) extDecoded[i][e] = (extDecoded[i][e].pixels, extDecoded[i][e].w, extDecoded[i][e].h, false);
            }
        }
        int extCount = extSheets.Sum(l => l.Count);
        LogTiming($"Atlas PNG decode + metadata parsed (flat parallel, {atlasCount} base + {extCount} ext)");
        // Aggregate parallelism across the flat decode pool.
        long sumWork = 0; var threadSet = new HashSet<int>();
        foreach (var b in decodeBench) { sumWork += b.totalMs; threadSet.Add(b.threadId); }
        int cacheHits = 0, cacheWrites = 0;
        foreach (var b in decodeBench) { if (b.cacheHit) cacheHits++; if (b.wroteCache) cacheWrites++; }
        DebugLog.Log("startup",
            $"  [BENCH] flat decode pool: wall={phaseWallMs}ms sumWork={sumWork}ms parallelism={(double)sumWork / Math.Max(1, phaseWallMs):F2}x threads={threadSet.Count} cacheHits={cacheHits}/{decodeJobs.Count} cacheWrites={cacheWrites}");
        foreach (var b in decodeBench)
            DebugLog.Log("startup",
                $"  [BENCH] {b.label,-22} {b.sizeMb,3}MB tid={b.threadId,2} read={b.readMs,4}ms decode={b.decodeMs,5}ms pma={b.pmaMs,5}ms total={b.totalMs,5}ms {(b.cacheHit ? "CACHE-HIT" : b.skia ? "skia" : "stb")}{(b.wroteCache && !b.cacheHit ? " (wrote cache)" : "")}");

        // Load animation metadata BEFORE GPU upload so the stride calibration pass
        // (which runs in the upload loop, while decoded pixels are still live) can
        // read per-gait cycle durations from animationmeta. Animationmeta is CPU-
        // only — no dependency on GPU textures.
        foreach (string name in AtlasDefs.Names)
        {
            string metaPath = GamePaths.Resolve($"assets/Sprites/{name}.animationmeta");
            if (File.Exists(metaPath))
                AnimMetaLoader.Load(metaPath, _animMeta);
            foreach (string extMeta in AtlasDefs.FindExtensionAnimMeta(name))
                AnimMetaLoader.Load(extMeta, _animMeta);
        }
        LogTiming($"Animation metadata: {_animMeta.Count} entries");
        _sim.SetAnimMeta(_animMeta);

        // Phase 2: Upload decoded pixels to GPU (fast — just SetData, no PNG decode).
        // Stride calibration runs per-atlas in this loop, BEFORE pixels are freed,
        // so it can scan the source rgba without a GPU readback. Cache hit skips
        // the pixel scan; only the first launch (or asset edit) pays the cost.
        int strideCacheHits = 0, strideCacheBuilds = 0;
        for (int i = 0; i < atlasCount; i++)
        {
            if (decodedPixels[i] != null && metaParsed[i])
            {
                var tex = TextureUtil.CreateTextureFromPixels(GraphicsDevice,
                    decodedPixels[i], decodedW[i], decodedH[i]);
                _atlases[i].SetTextureAndFinalize(tex, decodedW[i], decodedH[i]);

                // Calibrate stride per unit. Y-coords in the spritemeta have been
                // flipped by SetTextureAndFinalize (top-left origin), matching the
                // pixel buffer layout we're handing to StrideCalibration.
                string atlasName = AtlasDefs.Names[i];
                string pngPath = GamePaths.Resolve($"assets/Sprites/{atlasName}.png");
                string smPath  = GamePaths.Resolve($"assets/Sprites/{atlasName}.spritemeta");
                string amPath  = GamePaths.Resolve($"assets/Sprites/{atlasName}.animationmeta");
                bool cacheHit = Render.StrideCalibration.CalibrateAtlas(_atlases[i], atlasName,
                    pngPath, smPath, amPath, decodedPixels[i], decodedW[i], decodedH[i], _animMeta);
                if (cacheHit) strideCacheHits++; else strideCacheBuilds++;

                decodedPixels[i] = null!; // free memory after calibration is done with it
            }
            // Attach extension sheets in the order they were decoded (matches the
            // TextureIndex assigned by ParseExtensionMeta).
            foreach (var ext in extDecoded[i])
            {
                if (!ext.decoded || ext.pixels == null) continue;
                var extTex = TextureUtil.CreateTextureFromPixels(GraphicsDevice,
                    ext.pixels, ext.w, ext.h);
                _atlases[i].AttachExtensionTexture(extTex, ext.w, ext.h);
            }
        }
        LogTiming($"Atlases GPU upload + stride calibration: {atlasCount} ({string.Join(", ", AtlasDefs.Names)}) — strideCacheHits={strideCacheHits} builds={strideCacheBuilds}");

        // Wire each UnitDef's runtime SpriteData reference now that both registries
        // (loaded in _gameData.Load above) and atlases (just uploaded) exist. Lets
        // LocomotionProfile.FromUnit reach stride calibration without separately
        // plumbing atlas access through AI / render call sites.
        int spriteWireCount = 0;
        foreach (var def in _gameData.Units.All())
        {
            if (def.Sprite == null || string.IsNullOrEmpty(def.Sprite.AtlasName)) continue;
            int aIdx = AtlasDefs.ResolveAtlasName(def.Sprite.AtlasName);
            if (aIdx < 0 || aIdx >= _atlases.Length) continue;
            def.SpriteData = _atlases[aIdx].GetUnit(def.Sprite.SpriteName);
            if (def.SpriteData != null) spriteWireCount++;
        }
        LogTiming($"UnitDef→SpriteData wired for {spriteWireCount}/{_gameData.Units.Count} units");

        // Push corpse.json pivot overrides into the BodyBag/Icon atlas frames now
        // that the Corpses atlas exists. Spritemeta provides the defaults; this
        // step lets the editor tune per-angle hand-attach points without re-export.
        {
            int corpsesIdx = AtlasDefs.ResolveAtlasName("Corpses");
            if (corpsesIdx >= 0 && corpsesIdx < _atlases.Length)
                _gameData.Corpse.ApplyToAtlas(_atlases[corpsesIdx]);
        }

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
        // Reset per-game skill book progress (learned set + event tally) so
        // returning to the main menu and starting a new game wipes prior unlocks.
        _skillBookState.InitFromDefs();
        _skillLearnToasts.Clear();

        // Clear world systems for clean reload (prevents doubling on second play)
        _envSystem.OnCollisionsDirty = null;
        _envSystem.ClearObjects();
        _envSystem.ClearDefs();
        _groundSystem.ClearTypes();
        // Wipe per-cell grass corruption fades — the renderer instance persists
        // across new-games, so without this stale fade values from the previous
        // session would render the grass already-corrupted on map load.
        _grassRenderer.ClearAllFades();
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
        // Now that flipbooks are loaded, hand the dictionary to systems
        // that need to look up the trail / splash / rain-splash animations.
        _wakeSystem.Init(_flipbooks);

        // Animation metadata is loaded once in Initialize() and reused across
        // main-game and scenario flows.

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
            // Parse the 55MB map JSON exactly once — Load returns the grass info from
            // the same JsonDocument it already parsed, avoiding a redundant disk+parse pass.
            MapData.Load(mapPath, _groundSystem, _envSystem, _wallSystem, placedUnits,
                out var grassInfo);
            MapData.LoadTriggers(GamePaths.Resolve("assets/maps/default_triggers.json"), _triggerSystem);
            MapData.LoadRoads(GamePaths.Resolve("assets/maps/default_roads.json"), _roadSystem);
            LogTiming($"Map loaded: ground={_groundSystem.WorldW}x{_groundSystem.WorldH}, objects={_envSystem.ObjectCount}, defs={_envSystem.DefCount}");

            // Death fog: coarse grid sized to the map. Auto-tag tree assets as
            // sinks so we don't need to edit ~40 JSON entries by hand.
            _deathFog.Init(_groundSystem.WorldW, _groundSystem.WorldH, cellSize: 4);
            GameSystems.DeathFogSystem.AutoTagTreesAsSinks(_envSystem, absorbRate: 6f);

            // Unpack grass data returned from the same map JSON parse.
            if (grassInfo.HasValue)
            {
                var gi = grassInfo.Value;
                _grassW = gi.Width;
                _grassH = gi.Height;
                _grassMap = gi.Cells;
                _grassTypeIds = new string[gi.Types.Length];
                _grassTypeNames = new string[gi.Types.Length];
                _grassTypeSpritePaths = new string[gi.Types.Length][];
                _grassTypeScales = new float[gi.Types.Length];
                _grassTypeDensities = new float[gi.Types.Length];
                _grassDefaultTints = new Color[gi.Types.Length];
                _grassCorruptedTints = new Color[gi.Types.Length];
                for (int i = 0; i < gi.Types.Length; i++)
                {
                    _grassTypeIds[i] = gi.Types[i].Id ?? $"grass_{i}";
                    _grassTypeNames[i] = gi.Types[i].Name ?? $"Grass {i}";
                    _grassTypeSpritePaths[i] = gi.Types[i].SpritePaths ?? Array.Empty<string>();
                    _grassTypeScales[i] = gi.Types[i].Scale > 0f ? gi.Types[i].Scale : 1f;
                    _grassTypeDensities[i] = gi.Types[i].Density > 0f ? gi.Types[i].Density : 1f;
                    _grassDefaultTints[i]   = new Color(gi.Types[i].DefR, gi.Types[i].DefG, gi.Types[i].DefB, gi.Types[i].DefA);
                    _grassCorruptedTints[i] = new Color(gi.Types[i].CorR, gi.Types[i].CorG, gi.Types[i].CorB, gi.Types[i].CorA);
                }
                DebugLog.Log("startup", $"Grass map: {_grassW}x{_grassH}, {gi.Types.Length} types");
            }
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
        // Now that the ground types are populated, bake a wake-particle
        // gradient variant per unique water tint so swamp shallow water
        // produces a swamp-green wake instead of the default shoreline cyan.
        _wakeSystem.InitWaterVariants(_groundSystem);
        _envSystem.LoadTextures(GraphicsDevice);
        // NOTE: corpse-carry centroids are computed lazily on first carry (see
        // GetFrameCentroid). Pre-baking them all here stalled Start-Game ~13s —
        // each GetData read-back on the huge unit atlases is ~85ms and there are
        // ~200 death frames. One brief hitch per carried corpse type is the
        // cheaper trade until centroids can be baked offline into the spritemeta.
        LogTiming($"Ground textures: {_groundSystem.TypeCount}, Env textures: {_envSystem.DefCount}, VertexMap: {(_groundVertexMapTex != null ? "OK" : "NONE")}");

        // Init simulation with map size
        _sim.Init(worldW, worldH, _gameData);
        _sim.SetEnvironmentSystem(_envSystem);
        _sim.SetWallSystem(_wallSystem);
        _sim.SetTriggerSystem(_triggerSystem);
        _sim.SetSkillBook(_skillBookState);

        // Wire collision change callback so pathfinding rebuilds when objects change state
        _envSystem.OnCollisionsDirty = () => _sim.RebuildPathfinder();

        // Stamp pathfinding terrain from ground vertex types (e.g. water-textured
        // tiles become ShallowWater/DeepWater for the cost field). Must happen
        // BEFORE BakeWalls so walls win on shared tiles, and BEFORE
        // RebuildPathfinder so the cost-field rebuild picks up the new terrain.
        _groundSystem.StampTerrainOnto(_sim.Grid);

        // Bake walls + env (and build the env spatial index used by ORCA).
        // RebuildPathfinder now bakes walls itself (before the cost field) and
        // populates the env index, so a separate BakeWalls call here would just
        // re-walk the whole 4096² grid redundantly.
        _sim.RebuildPathfinder();
        LogTiming($"Baked collisions: {_envSystem.ObjectCount} objects, grid {worldW}x{worldH}");

        // Fog of war
        _fogOfWar.Init(worldW, worldH, GraphicsDevice, Content);

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

        // Always ensure the player unit exists. The player starts as the
        // Wretched form — every other "necromancer-type" UnitDef is reached
        // via the Metamorphosis skill tree (Become Pale Acolyte, Become Wight,
        // etc.). Across the codebase we still refer to the player unit as
        // "the necromancer" regardless of which PlayerForm def it currently is.
        if (_sim.NecromancerIndex < 0)
        {
            SpawnUnit("wretched", new Vec2(center, center));
            DebugLog.Log("startup", "No necromancer in placed units, spawned Wretched default at map center");
        }

        _camera.Position = _sim.NecromancerIndex >= 0
            ? _sim.Units[_sim.NecromancerIndex].Position : new Vec2(center, center);
        _sim.Horde.CircleCenter = _sim.NecromancerIndex >= 0
           ? _sim.Units[_sim.NecromancerIndex].Position : new Vec2(center, center);
        _camera.Zoom = 24f;

        // Pass placed units to map editor so markers are visible
        _mapEditor.SetPlacedUnits(placedUnits);

        // Load spell bar from data file
        // Load both spell bars from single JSON read
        _spellBarState.Slots = new SpellBarSlot[4];
        _secondaryBarState.Slots = new SpellBarSlot[6]; // 1-6 (was 4 spells + 2 potion slots)
        for (int si = 0; si < 4; si++) _spellBarState.Slots[si] = new SpellBarSlot { SpellID = "" };
        for (int si = 0; si < 6; si++) _secondaryBarState.Slots[si] = new SpellBarSlot { SpellID = "" };
        try
        {
            // Per-machine spell-bar loadout: gitignored 'user settings/', seeded from
            // the shipped default data/spellbar.json on first run.
            string sbJson = File.ReadAllText(GamePaths.SeededUserFile(
                GamePaths.UserSpellBarJson, GamePaths.Resolve(GamePaths.SpellBarJson)));
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
                // Incremental: patch only the brush-sized dirty rect into the
                // existing texture (a few hundred texels) instead of allocating
                // and uploading a fresh 4097x4097 (~67 MB) texture every painted
                // frame. Fall back to a full rebuild only if the texture is
                // missing or its size no longer matches the vertex map.
                if (_groundVertexMapTex == null
                    || !_groundSystem.UploadDirtyRect(_groundVertexMapTex))
                {
                    _groundVertexMapTex?.Dispose();
                    _groundVertexMapTex = _groundSystem.CreateVertexMapTexture(GraphicsDevice);
                }
            },
            wallSystem: _wallSystem,
            roadSystem: _roadSystem,
            tileGrid: _sim.Grid,
            onGrassMapChanged: SyncGrassMapReference,
            editorBase: _editorUi,
            onGrassTypesChanged: SyncGrassFromEditor);
        _mapEditor.SetItemRegistry(_gameData.Items);
        _mapEditor.SetSpellRegistry(_gameData.Spells);
        _mapEditor.SetGameData(_gameData);
        {
            int corpsesIdx = AtlasDefs.ResolveAtlasName("Corpses");
            var corpsesAtlas = (corpsesIdx >= 0 && corpsesIdx < _atlases.Length) ? _atlases[corpsesIdx] : null;
            _mapEditor.SetCorpseSettings(_gameData.Corpse, corpsesAtlas);
        }

        // Feed grass data to map editor
        if (_grassMap.Length > 0)
        {
            var grassTypeInfos = new MapData.GrassTypeInfo[_grassTypeIds.Length];
            for (int gi = 0; gi < grassTypeInfos.Length; gi++)
            {
                var dt = gi < _grassDefaultTints.Length ? _grassDefaultTints[gi] : Color.White;
                var ct = gi < _grassCorruptedTints.Length ? _grassCorruptedTints[gi] : new Color((byte)80, (byte)60, (byte)70, (byte)255);
                grassTypeInfos[gi] = new MapData.GrassTypeInfo
                {
                    Id = _grassTypeIds[gi] ?? $"grass_{gi}",
                    Name = gi < _grassTypeNames.Length && _grassTypeNames[gi] != null ? _grassTypeNames[gi] : $"Grass {gi}",
                    SpritePaths = gi < _grassTypeSpritePaths.Length ? _grassTypeSpritePaths[gi] : Array.Empty<string>(),
                    Scale = gi < _grassTypeScales.Length ? _grassTypeScales[gi] : 1f,
                    Density = gi < _grassTypeDensities.Length ? _grassTypeDensities[gi] : 1f,
                    DefR = dt.R, DefG = dt.G, DefB = dt.B, DefA = dt.A,
                    CorR = ct.R, CorG = ct.G, CorB = ct.B, CorA = ct.A,
                };
            }
            _mapEditor.SetGrassData(_grassMap, _grassW, _grassH, grassTypeInfos, _gameData.Settings.Grass.CellSize);
            PushGrassSpritesToRenderer();
        }

        Window.Title = "Necroking";
        // Warm the widget-UI family here, inside the load phase: LoadDefinitions
        // bakes every harmonized texture (~seconds of CPU work) — paying it
        // lazily froze the game on the FIRST UI keypress (I/C/U/O) instead.
        EnsureInventoryUIsInitialized();
        LogTiming("Game world loaded");
        DebugLog.Log("startup", $"=== Total startup: {_startupTimer?.ElapsedMilliseconds ?? 0}ms ===");
        _gameWorldLoaded = true;
        _menuState = MenuState.None;

        // Starting inventory
        foreach (var item in _gameData.Settings.StartingInventory)
            _inventory.AddItem(item.ItemId, item.Quantity);

        _skillBookOverlay.Bind(_skillBookState, _inventory, _gameData,
            _spellBarState, _secondaryBarState, _sim);

    }

    /// <summary>Auto-learn a skill if not already learned, and surface a corner
    /// toast on success. Used by gameplay triggers (pickups, milestones, etc.).</summary>
    private void TryAutoLearn(string skillId, string header)
    {
        bool learned = _skillBookState.LearnFree(skillId, new Game.SkillEffects.SkillEffectContext
        {
            Inventory = _inventory,
            GameData = _gameData,
            PrimaryBar = _spellBarState,
            SecondaryBar = _secondaryBarState,
            BookState = _skillBookState,
            Sim = _sim,
        });
        if (!learned) return;
        var def = _skillBookState.FindSkill(skillId);
        if (def == null) return;
        _skillLearnToasts.Add(new SkillLearnToast
        {
            Header = header,
            SkillName = def.Name,
            SkillId = skillId,
            Timer = 0f,
            Duration = 5f,
        });
    }

    /// <summary>Geometry helper — same layout numbers used in DrawSkillLearnToasts.
    /// Called from the input pass so clicks land on what was just rendered.</summary>
    private Rectangle GetSkillLearnToastRect(int sw, int sh, int stackIndex)
    {
        const int toastW = 280, toastH = 56, padR = 16, padB = 16, gap = 6;
        int yCursor = sh - padB - toastH - stackIndex * (toastH + gap);
        return new Rectangle(sw - padR - toastW, yCursor, toastW, toastH);
    }

    /// <summary>Hit-test corner toasts and route a left-click to opening the skill
    /// book on the relevant tab. Called from the UI input pass.</summary>
    private void UpdateSkillLearnToastInput(int sw, int sh)
    {
        if (_skillLearnToasts.Count == 0) return;
        int mx = (int)_input.MousePos.X;
        int my = (int)_input.MousePos.Y;
        // Iterate top of stack downward to mirror draw order — most recent toast
        // is the bottom slot (stackIndex 0).
        for (int i = 0; i < _skillLearnToasts.Count; i++)
        {
            // Toasts are drawn from the most recent (last-added) up the stack.
            // Slot 0 = newest = bottom rect.
            int stackSlot = _skillLearnToasts.Count - 1 - i;
            var rect = GetSkillLearnToastRect(sw, sh, stackSlot);
            if (rect.Contains(mx, my))
            {
                _input.MouseOverUI = true;
                if (_input.LeftPressed && !_input.IsMouseConsumed)
                {
                    var t = _skillLearnToasts[i];
                    int tabIdx = SkillBookDefs.FindTabIndexFor(t.SkillId);
                    _skillBookOverlay.Open();
                    if (tabIdx >= 0) _skillBookOverlay.SetActiveTab(tabIdx);
                    _skillLearnToasts.RemoveAt(i);
                    _input.ConsumeMouse();
                }
                return;
            }
        }
    }

    private void UpdateSkillLearnToasts(float dt)
    {
        for (int i = _skillLearnToasts.Count - 1; i >= 0; i--)
        {
            var t = _skillLearnToasts[i];
            t.Timer += dt;
            if (t.Timer >= t.Duration) _skillLearnToasts.RemoveAt(i);
            else _skillLearnToasts[i] = t;
        }
    }

    /// <summary>Bottom-right stack of "Recipe Learned" / "Skill Unlocked" toasts.
    /// Each toast slides in (first 0.25s), holds, then fades out (last 0.6s).</summary>
    private void DrawSkillLearnToasts(int sw, int sh)
    {
        if (_skillLearnToasts.Count == 0 || _font == null) return;
        var f = _font!;
        var sf = _smallFont ?? f;

        const int toastW = 280, toastH = 56, padR = 16, padB = 16, gap = 6;
        int yCursor = sh - padB - toastH;

        // Palette matches the SkillBookPanel's grimoire chrome.
        var leatherDark = new Color(26, 13, 8);
        var leatherMid  = new Color(42, 26, 18);
        var gold        = new Color(218, 184,  96);
        var goldDim     = new Color(108,  84,  40);
        var parchment   = new Color(196, 174, 128);

        for (int i = _skillLearnToasts.Count - 1; i >= 0; i--)
        {
            var t = _skillLearnToasts[i];
            float life = t.Timer / t.Duration;
            float alpha = 1f;
            if (life < 0.1f) alpha = life / 0.1f;          // slide in
            else if (life > 0.85f) alpha = (1f - life) / 0.15f; // fade out
            alpha = MathHelper.Clamp(alpha, 0f, 1f);
            int slideX = (int)((1f - alpha) * 30); // also slides slightly from the right

            int x = sw - padR - toastW + slideX;
            int y = yCursor;
            byte a = (byte)(255 * alpha);

            var rect = new Rectangle(x, y, toastW, toastH);
            // Drop shadow
            _spriteBatch.Draw(_pixel, new Rectangle(rect.X + 3, rect.Y + 3, rect.Width, rect.Height),
                new Color((byte)0, (byte)0, (byte)0, (byte)(160 * alpha)));
            _spriteBatch.Draw(_pixel, rect, new Color(leatherMid, alpha));
            // Top gold accent band
            _spriteBatch.Draw(_pixel, new Rectangle(rect.X, rect.Y, rect.Width, 2),
                new Color(gold, alpha));
            // Border
            DrawToastBorder(rect, new Color(goldDim, alpha));

            // Text — header + skill name
            string header = t.Header;
            string body = t.SkillName;
            // Sanitize for the embedded ASCII-only SpriteFont.
            header = SanitizeAscii(header);
            body = SanitizeAscii(body);
            DrawTextRounded(sf, header,
                new Vector2(rect.X + 14, rect.Y + 8),
                new Color(gold, alpha));
            DrawTextRounded(f, body,
                new Vector2(rect.X + 14, rect.Y + 24),
                new Color(parchment, alpha));

            yCursor -= toastH + gap;
        }
    }

    private void DrawToastBorder(Rectangle r, Color c)
    {
        _spriteBatch.Draw(_pixel, new Rectangle(r.X, r.Y, r.Width, 1), c);
        _spriteBatch.Draw(_pixel, new Rectangle(r.X, r.Bottom - 1, r.Width, 1), c);
        _spriteBatch.Draw(_pixel, new Rectangle(r.X, r.Y, 1, r.Height), c);
        _spriteBatch.Draw(_pixel, new Rectangle(r.Right - 1, r.Y, 1, r.Height), c);
    }

    private void DrawTextRounded(SpriteFont f, string text, Vector2 pos, Color color)
        => _spriteBatch.DrawString(f, text, new Vector2((int)pos.X, (int)pos.Y), color);

    private static string SanitizeAscii(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        bool needs = false;
        for (int i = 0; i < text.Length; i++)
            if (text[i] > 126 || (text[i] < 32 && text[i] != '\n')) { needs = true; break; }
        if (!needs) return text;
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text) sb.Append(ch >= 32 && ch <= 126 ? ch : '?');
        return sb.ToString();
    }

    /// <summary>
    /// Sync grass data from the map editor back to Game1's rendering arrays.
    /// Called by the editor whenever the grass map or grass type properties change.
    /// </summary>
    /// <summary>
    /// Cheap cell-paint notification: the grass map array is shared with the
    /// renderer and read live each frame, so painting cells needs no work beyond
    /// re-pointing at the editor's array if it was swapped (it isn't during a
    /// paint stroke — only after Add-first-type or Load, which both go through
    /// the full <see cref="SyncGrassFromEditor"/> path). O(1), safe to call per
    /// painted frame.
    /// </summary>
    private void SyncGrassMapReference()
    {
        var editorMap = _mapEditor.GetGrassMap();
        if (editorMap != _grassMap && editorMap.Length > 0)
        {
            _grassMap = editorMap;
            _grassW = _mapEditor.GrassW;
            _grassH = _mapEditor.GrassH;
        }
    }

    private void SyncGrassFromEditor()
    {
        // The editor may have a new/different grass map reference (e.g. after editor Load)
        SyncGrassMapReference();

        // Sync grass type properties from editor definitions.
        var types = _mapEditor.GrassTypes;

        _grassTypeSpritePaths = new string[types.Count][];
        _grassTypeScales = new float[types.Count];
        _grassTypeDensities = new float[types.Count];
        _grassDefaultTints = new Color[types.Count];
        _grassCorruptedTints = new Color[types.Count];
        if (_grassTypeIds.Length != types.Count) _grassTypeIds = new string[types.Count];
        if (_grassTypeNames.Length != types.Count) _grassTypeNames = new string[types.Count];

        for (int i = 0; i < types.Count; i++)
        {
            _grassTypeIds[i] = types[i].Id;
            _grassTypeNames[i] = types[i].Name;
            _grassTypeSpritePaths[i] = types[i].SpritePaths.ToArray();
            _grassTypeScales[i] = types[i].Scale > 0f ? types[i].Scale : 1f;
            _grassTypeDensities[i] = MathF.Max(0f, types[i].Density);
            _grassDefaultTints[i] = types[i].DefaultTint;
            _grassCorruptedTints[i] = types[i].CorruptedTint;
        }

        PushGrassSpritesToRenderer();
    }

    /// <summary>
    /// Push the current _grassTypeSpritePaths + _grassTypeScales + _grassTypeDensities
    /// to GrassTuftRenderer so its texture cache and per-type tables reflect the live
    /// grass types. Called from SyncGrassFromEditor and StartScenario after grass data
    /// is set up.
    /// </summary>
    /// <summary>Push <see cref="Data.Registries.CorruptionSettings"/> values into
    /// the live systems that own them. Cheap (a handful of float assignments) so
    /// it's safe to call every frame — that way live edits via the Settings UI
    /// take effect immediately on the next gameplay tick. The systems hold their
    /// own copies in their hot paths; this method is the one place those copies
    /// get refreshed from settings.json.</summary>
    private void SyncCorruptionSettings()
    {
        if (_gameData == null) return;
        var c = _gameData.Settings.Corruption;

        _deathFog.CorruptionHealRate = c.TreeHealRate;
        _deathFog.CorruptionThreshold = c.TreeThreshold;
        _deathFog.CorruptedAbsorbRate = c.TreeCorruptedAbsorbRate;
        _deathFog.CorruptionTransitionDuration = c.TreeFadeDuration;
        _deathFog.GroundCorruptionMaxRate = c.GroundMaxRatePerSec;
        _deathFog.DiffusionRate = c.DiffusionRate;
        _deathFog.SourceRateScale = c.SourceRateScale;
        _deathFog.SinkRateScale = c.SinkRateScale;

        _groundSystem.CorruptionFadeDuration = c.GroundFadeDuration;
        _grassRenderer.CorruptionFadeDuration = c.GrassFadeDuration;

        _deathFogRenderer.VisibilityThreshold = c.FogVisibilityThreshold;
        _deathFogRenderer.SaturationDensity = c.FogSaturationDensity;
        _deathFogRenderer.MaxAlpha = c.FogMaxAlpha;
        _deathFogRenderer.FlipbookCycleSeconds = c.FogFlipbookCycleSeconds;
        _deathFogRenderer.PuffWorldSizeMultiplier = c.FogPuffWorldSizeMultiplier;
        _deathFogRenderer.PositionJitter = c.FogPositionJitter;
        _deathFogRenderer.FogTint = new Microsoft.Xna.Framework.Color(
            (byte)c.FogTint.R, (byte)c.FogTint.G, (byte)c.FogTint.B, (byte)c.FogTint.A);
    }

    /// <summary>Translate a newly-corrupted ground vertex into the grass cells
    /// it sits under and start their fade. A vertex (vx, vy) is the corner of up
    /// to four world tiles spanning [vx-1, vx+1) × [vy-1, vy+1); we mark every
    /// grass cell touching that 2×2 world region. StartCellFade is idempotent,
    /// so multiple adjacent vertex flips don't reset progress already in flight.</summary>
    private void OnGroundVertexCorruptedForGrass(int vx, int vy)
    {
        if (_grassMap.Length == 0 || _grassW == 0) return;
        float cellSize = _gameData?.Settings.Grass.CellSize ?? 1f;
        if (cellSize <= 0f) cellSize = 1f;

        float wx0 = vx - 1f, wx1 = vx + 1f;
        float wy0 = vy - 1f, wy1 = vy + 1f;
        int cx0 = Math.Max(0, (int)MathF.Floor(wx0 / cellSize));
        int cy0 = Math.Max(0, (int)MathF.Floor(wy0 / cellSize));
        int cx1 = Math.Min(_grassW - 1, (int)MathF.Floor((wx1 - 0.0001f) / cellSize));
        int cy1 = Math.Min(_grassH - 1, (int)MathF.Floor((wy1 - 0.0001f) / cellSize));
        for (int cy = cy0; cy <= cy1; cy++)
        {
            for (int cx = cx0; cx <= cx1; cx++)
            {
                int idx = cy * _grassW + cx;
                if ((uint)idx >= (uint)_grassMap.Length) continue;
                if (_grassMap[idx] == 0) continue; // empty cell, no tuft to fade
                _grassRenderer.StartCellFade(idx);
            }
        }
    }

    private void PushGrassSpritesToRenderer()
    {
        var list = new List<Render.GrassTypeRender>(_grassTypeSpritePaths.Length);
        for (int i = 0; i < _grassTypeSpritePaths.Length; i++)
        {
            var paths = _grassTypeSpritePaths[i] ?? Array.Empty<string>();
            float scale = i < _grassTypeScales.Length && _grassTypeScales[i] > 0f ? _grassTypeScales[i] : 1f;
            float density = i < _grassTypeDensities.Length ? _grassTypeDensities[i] : 1f;
            Color def = i < _grassDefaultTints.Length ? _grassDefaultTints[i] : Color.White;
            Color cor = i < _grassCorruptedTints.Length ? _grassCorruptedTints[i] : new Color((byte)80, (byte)60, (byte)70, (byte)255);
            list.Add(new Render.GrassTypeRender(paths, scale, density, def, cor));
        }
        _grassRenderer.SetGrassTypes(list);
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
        // Init systems that consume the flipbook dictionary. LoadGame does
        // this for main-game runs; scenarios use this code path instead.
        _wakeSystem.Init(_flipbooks);

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
        _sim.SetSkillBook(_skillBookState);
        _fogOfWar.Init(gridSize, gridSize, GraphicsDevice, Content);

        // Ensure spell bar state is initialized for HUD safety
        if (_spellBarState.Slots == null)
            _spellBarState.Slots = new SpellBarSlot[4] { new(), new(), new(), new() };
        if (_secondaryBarState.Slots == null)
            _secondaryBarState.Slots = new SpellBarSlot[6] { new(), new(), new(), new(), new(), new() };
        // UI tests can seed the bars (StartGame's spellbar.json load doesn't run).
        if (scenario.DebugPrimarySpells != null)
            for (int i = 0; i < 4 && i < scenario.DebugPrimarySpells.Length; i++)
                _spellBarState.Slots[i] = new SpellBarSlot { SpellID = scenario.DebugPrimarySpells[i] };
        if (scenario.DebugSecondarySpells != null)
            for (int i = 0; i < 6 && i < scenario.DebugSecondarySpells.Length; i++)
                _secondaryBarState.Slots[i] = new SpellBarSlot { SpellID = scenario.DebugSecondarySpells[i] };

        // Load ground data for scenarios that want it
        if (scenario.WantsGround)
        {
            _groundSystem.AddGroundType(new World.GroundTypeDef { Id = "grass", Name = "Grass", TexturePath = "assets/Environment/Ground/GroundGrass1.png" });
            _groundSystem.AddGroundType(new World.GroundTypeDef { Id = "dirt", Name = "Dirt", TexturePath = "assets/Environment/Ground/GroundDirt1.png" });
            _groundSystem.AddGroundType(new World.GroundTypeDef { Id = "cobblestone", Name = "Cobblestone", TexturePath = "assets/Environment/Ground/GroundCobblestone1.png" });
            _groundSystem.AddGroundType(new World.GroundTypeDef { Id = "shallow_water", Name = "Shallow Water", TexturePath = "assets/Environment/Ground/ShallowWater.png", MovementTerrain = World.TerrainType.ShallowWater });
            _groundSystem.AddGroundType(new World.GroundTypeDef { Id = "deep_water", Name = "Deep Water", TexturePath = "assets/Environment/Ground/DeepWater.png", MovementTerrain = World.TerrainType.DeepWater });
            _groundSystem.FillAll(0); // Default to grass
            scenario.GroundSystem = _groundSystem;
            // Scenario.OnInit may paint additional types into the vertex map;
            // we (re)build the vertex map texture AFTER OnInit (see below).
            _groundSystem.LoadTextures(GraphicsDevice);
            _groundVertexMapTex = _groundSystem.CreateVertexMapTexture(GraphicsDevice);
            _wakeSystem.InitWaterVariants(_groundSystem);
            DebugLog.Log("scenario", $"Ground setup: types={_groundSystem.TypeCount}, vertexMap={(_groundVertexMapTex != null ? "OK" : "NONE")}, effect={(_groundEffect != null ? "OK" : "NONE")}");
        }

        // Benchmark mode: unlock framerate so GPU cost shows up in the timing.
        if (scenario.BenchmarkMode)
        {
            IsFixedTimeStep = false;
            _graphics.SynchronizeWithVerticalRetrace = false;
            _graphics.ApplyChanges();
        }
        // Mirror current state back so the scenario can verify post-OnInit.
        scenario.VsyncEnabled = _graphics.SynchronizeWithVerticalRetrace;
        scenario.FixedTimeStepEnabled = IsFixedTimeStep;

        // Setup grass data for scenarios that want it
        if (scenario.WantsGrass)
        {
            // Use settings.CellSize so scenarios share the same grass grid layout as
            // the editor / main game. For a default CellSize of 1.0, a 64-unit world
            // gets a 64x64 grass grid (one cell per pathability tile).
            float cs = _gameData.Settings.Grass.CellSize > 0f ? _gameData.Settings.Grass.CellSize : 1.0f;
            int gw = (int)MathF.Ceiling(gridSize / cs);
            int gh = (int)MathF.Ceiling(gridSize / cs);
            _grassMap = new byte[gw * gh];
            _grassW = gw;
            _grassH = gh;
            // Default 3 grass types — no per-type tint distinction in scenarios.
            string[] defaultSprites = { "assets/Environment/Grass/GreenGrass1.png" };
            _grassTypeIds = new[] { "grass_0", "grass_1", "grass_2" };
            _grassTypeNames = new[] { "Grass 0", "Grass 1", "Grass 2" };
            _grassTypeSpritePaths = new[] { defaultSprites, defaultSprites, defaultSprites };
            _grassTypeScales = new[] { 1f, 1f, 1f };
            _grassTypeDensities = new[] { 1f, 1f, 1f };
            var defaultCorrupted = new Color((byte)80, (byte)60, (byte)70, (byte)255);
            _grassDefaultTints = new[] { Color.White, Color.White, Color.White };
            _grassCorruptedTints = new[] { defaultCorrupted, defaultCorrupted, defaultCorrupted };
            Array.Fill(_grassMap, (byte)0);
            scenario.GrassMap = _grassMap;
            scenario.GrassW = gw;
            scenario.GrassH = gh;
            PushGrassSpritesToRenderer();
            DebugLog.Log("scenario", $"Grass setup: {gw}x{gh}, cellSize={cs}, 3 types");
        }

        // Give scenario access to road system and inventory
        scenario.RoadSystem = _roadSystem;
        scenario.Inventory = _inventory;
        scenario.ItemRegistry = _gameData.Items;
        scenario.InventoryUI = _inventoryUI;
        scenario.SkillBookOverlay = _skillBookOverlay;
        scenario.UIShaders = _uiShaders;
        scenario.Atlases = _atlases;
        scenario.Font = _font;
        scenario.SmallFont = _smallFont;
        scenario.PixelTexture = _pixel;
        if (scenario.WantsWidgetRenderer)
        {
            EnsureInventoryUIsInitialized();
            scenario.WidgetRenderer = _widgetRenderer;
            scenario.DrawUnitSprite = (defId, rect) => DrawUnitIdleSprite(defId, rect);
        }

        // Init map editor with scenario systems (needed for editor screenshot scenarios)
        _mapEditor.Init(
            _groundSystem, _envSystem, _triggerSystem, _camera,
            _spriteBatch, _pixel, _font, _smallFont, GraphicsDevice,
            onVertexMapChanged: () =>
            {
                // Incremental: patch only the brush-sized dirty rect into the
                // existing texture (a few hundred texels) instead of allocating
                // and uploading a fresh 4097x4097 (~67 MB) texture every painted
                // frame. Fall back to a full rebuild only if the texture is
                // missing or its size no longer matches the vertex map.
                if (_groundVertexMapTex == null
                    || !_groundSystem.UploadDirtyRect(_groundVertexMapTex))
                {
                    _groundVertexMapTex?.Dispose();
                    _groundVertexMapTex = _groundSystem.CreateVertexMapTexture(GraphicsDevice);
                }
            },
            wallSystem: _wallSystem,
            roadSystem: _roadSystem,
            tileGrid: _sim.Grid,
            onGrassMapChanged: SyncGrassMapReference,
            editorBase: _editorUi,
            onGrassTypesChanged: SyncGrassFromEditor);
        _mapEditor.SetItemRegistry(_gameData.Items);

        // Initialize the scenario
        scenario.OnInit(_sim);

        // Reload env textures in case the scenario added new defs
        _envSystem.LoadTextures(GraphicsDevice);

        // Rebuild the vertex map texture if the scenario painted ground in OnInit.
        if (scenario.WantsGround)
        {
            _groundSystem.LoadTextures(GraphicsDevice);
            _groundVertexMapTex?.Dispose();
            _groundVertexMapTex = _groundSystem.CreateVertexMapTexture(GraphicsDevice);
            _wakeSystem.InitWaterVariants(_groundSystem);
            _groundSystem.StampTerrainOnto(_sim.Grid);
            _sim.RebuildPathfinder();
        }

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

        // Initialize awareness config from UnitDef (always, regardless of archetype)
        _sim.UnitsMut[idx].DetectionRange = unitDef.DetectionRange;
        _sim.UnitsMut[idx].DetectionBreakRange = unitDef.DetectionBreakRange;
        _sim.UnitsMut[idx].AlertDuration = unitDef.AlertDuration;
        _sim.UnitsMut[idx].AlertEscalateRange = unitDef.AlertEscalateRange;
        _sim.UnitsMut[idx].GroupAlertRadius = unitDef.GroupAlertRadius;

        // AI — use new archetype system if specified, otherwise legacy AI enum
        if (!string.IsNullOrEmpty(unitDef.Archetype))
        {
            // Resolve archetype name to ID (single source of truth — see
            // ArchetypeRegistry.FromName, shared with Simulation.SpawnZombieMinion).
            byte archetypeId = AI.ArchetypeRegistry.FromName(unitDef.Archetype);
            _sim.UnitsMut[idx].Archetype = archetypeId;

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
            var spriteData = _atlases[atlasId].GetUnit(unitDef.Sprite.SpriteName);
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
                    var kfs = PickIdleFrames(idleAnim);
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

                // Resolve zombie type from corpse's UnitDef. Shared with
                // TableCraftingSystem so the same source corpse always raises
                // into the same unit class regardless of how it was triggered.
                string resolvedID = Game.TableCraftingSystem.ResolveZombieUnitID(_gameData, corpse.UnitDefID);
                if (string.IsNullOrEmpty(resolvedID)) continue;

                // Per-corpse cap check — AOE may mix categories. Skip corpses
                // whose category is full but keep iterating to consume others
                // whose category still has room.
                var aoeCat = HordeCapTracker.CategoryFor(_gameData, resolvedID);
                if (aoeCat != UndeadCategory.None
                    && HordeCapTracker.Available(_sim.Units, _gameData, _sim.NecroState, aoeCat) <= 0)
                    continue;

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
                // Resolve zombie type from corpse if summonUnitID is empty.
                // Shared helper: see comment on the AOE branch above.
                if (string.IsNullOrEmpty(summonUnitID) && pending.TargetCorpseIdx < _sim.Corpses.Count)
                {
                    var corpse = _sim.Corpses[pending.TargetCorpseIdx];
                    summonUnitID = Game.TableCraftingSystem.ResolveZombieUnitID(_gameData, corpse.UnitDefID);
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

                // Cap-limited summon count: spawn min(SummonQuantity, available
                // slots in the resolved category). Pre-check in SpellCaster
                // already refused when available=0; this clamps the multi-spawn
                // case so we never overshoot the cap.
                int spawnQty = spell.SummonQuantity;
                var spawnCat = HordeCapTracker.CategoryFor(_gameData, summonUnitID);
                if (spawnCat != UndeadCategory.None)
                {
                    int avail = HordeCapTracker.Available(_sim.Units, _gameData, _sim.NecroState, spawnCat);
                    if (avail < spawnQty) spawnQty = avail;
                }
                for (int q = 0; q < spawnQty; q++)
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
        var spriteData = _atlases[atlasId].GetUnit(unitDef.Sprite.SpriteName);
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
            var kfs = PickIdleFrames(idleAnim);
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
        _hudRenderer.Init(_spriteBatch, _pixel, _font, _smallFont, _widgetRenderer);
        _hudRenderer.SetInput(_input);
        _characterStatsUI.Init(_spriteBatch, _pixel, _font, _smallFont);
        // Note: _uiShaders is initialized later after Content.Load path -- we
        // set it on the panel below after Load completes.
        _skillBookOverlay.Init(_widgetRenderer, _spriteBatch, _pixel);
        // Early bind so scenarios (which skip StartGame) still have state. The spell-
        // bar Slots may be null at this point — re-bind happens in StartGame once the
        // bars are allocated. AddSpellToBarEffect handles null Slots gracefully.
        _skillBookOverlay.Bind(_skillBookState, _inventory, _gameData,
            _spellBarState, _secondaryBarState, _sim);

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

        try {
            _dissolveTreeEffect = Content.Load<Microsoft.Xna.Framework.Graphics.Effect>("DissolveTree");
            DebugLog.Log("startup", $"DissolveTree shader loaded — params: {string.Join(", ", _dissolveTreeEffect.Parameters.Cast<Microsoft.Xna.Framework.Graphics.EffectParameter>().Select(p => p.Name))}");
        }
        catch (Exception ex) { _dissolveTreeEffect = null; DebugLog.Log("startup", $"DissolveTree shader not loaded: {ex.Message}"); }

        _uiShaders = new UIShaders(GraphicsDevice, _pixel, BlendState.AlphaBlend, SamplerState.PointClamp);
        _uiShaders.Load(Content);

        try { _outlineFlatEffect = Content.Load<Microsoft.Xna.Framework.Graphics.Effect>("OutlineFlat"); }
        catch (Exception ex) { _outlineFlatEffect = null; DebugLog.Log("startup", $"OutlineFlat not loaded: {ex.Message}"); }
        try {
            _wadingEffect = Content.Load<Microsoft.Xna.Framework.Graphics.Effect>("Wading");
            var pnames = string.Join(",", _wadingEffect.Parameters.Select(p => p.Name));
            DebugLog.Log("startup", $"Wading loaded. params=[{pnames}]");
        }
        catch (Exception ex) { _wadingEffect = null; DebugLog.Log("startup", $"Wading NOT loaded: {ex.Message}"); }
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
        Necroking.Render.MagicPathIcons.SetDevice(GraphicsDevice);
        _lightningRenderer.Init(_spriteBatch, _pixel, _glowTex, _sim, _camera, _renderer, GraphicsDevice, _hdrIntensityEffect);
        // When a ground vertex newly corrupts, fade nearby grass tufts toward
        // their CorruptedTint over GrassTuftRenderer.CorruptionFadeDuration.
        _groundSystem.OnVertexCorrupted = OnGroundVertexCorruptedForGrass;
        LogTiming("Renderers initialized (weather, grass, lightning)");

        // UI editor (F12 / menu) is a dev-only tool — its LoadDefinitions bakes
        // every harmonized texture (~4s). Deferred to first open via
        // EnsureUIEditorInitialized() so it never costs startup time.
        string uiDefPath = GamePaths.Resolve(GamePaths.UIDefsDir);
        LogTiming("UI editor init deferred");

        // Runtime widget renderer + inventory family UIs are deferred to
        // first-needed via EnsureInventoryUIsInitialized(). The widget renderer
        // is exclusively consumed by Inventory/Building/Crafting/Table UIs —
        // no other system uses it. Together they cost ~250 ms at startup;
        // most launches never open any of them so this is pure savings.
        _widgetRendererUiDefPath = uiDefPath;

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

        // Wire the foragable subsystem now that all its dependencies exist.
        // Callbacks bridge back to Game1-private state (damage numbers, skill book).
        _foragables.Bind(_envSystem, _sim, _camera, _renderer, _spriteBatch,
            _inventory, _effectManager, _pickupSound,
            onPickup: OnForagablePickedUp,
            onLearnTrigger: OnForagableLearnTrigger);

        // Init property editor infrastructure
        _editorUi.SetContext(_spriteBatch, _pixel, _font, _smallFont, _largeFont);
        _unitEditor = new UnitEditorWindow(_editorUi);
        _unitEditor.SetGameData(_gameData);
        _unitEditor.SetAtlases(_atlases, GraphicsDevice);
        _unitEditor.SetAnimMeta(_animMeta);
        // Hand the wading effect + camera Y-ratio to the wading sub-editor
        // so its preview applies the actual shader (matching in-game look).
        _unitEditor.SetWadingShader(_wadingEffect, _camera.YRatio);
        _spellEditor = new SpellEditorWindow(_editorUi);
        _spellEditor.SetGameData(_gameData);
        _spellEditor.SetHdrEffect(_hdrSpriteEffect);
        _spellEditor.SetFlipbooks(_flipbooks);
        _spellEditor.SetContent(Content);
        _itemEditor = new ItemEditorWindow(_editorUi);
        _itemEditor.SetGameData(_gameData);
        _settingsWindow = new SettingsWindow(_editorUi);
        System.IO.Directory.CreateDirectory(GamePaths.Resolve(GamePaths.UserSettingsDir));
        _settingsWindow.SetGameData(_gameData, GamePaths.Resolve(GamePaths.UserSettingsJson), GamePaths.Resolve(GamePaths.UserWeatherJson));
        _settingsWindow.SetDayNightSystem(_dayNightSystem);
        LogTiming("Editors initialized");
        DebugLog.Log("startup", $"=== LoadContent complete ===");
    }

    protected override void Update(GameTime gameTime)
    {
        var kb = Keyboard.GetState();
        var mouse = Mouse.GetState();

        // Window unfocused or minimized: skip all input handling so background
        // clicks (taskbar, other apps) read by the global Mouse.GetState() aren't
        // consumed by the game. We still advance _prevMouse/_prevKb to the real
        // states so the click that refocuses the window isn't seen as an in-game
        // press. IsActive is MonoGame's canonical window-focus flag.
        // Exempt scenario / headless runs — automated runs often lack window
        // focus, and freezing them would break the scenario test harness.
        if (!IsActive && _activeScenario == null && LaunchArgs.Scenario == null && !LaunchArgs.Headless)
        {
            _prevKb = kb;
            _prevMouse = mouse;
            base.Update(gameTime);
            return;
        }

        _input.Capture(mouse, _prevMouse, kb, _prevKb);

        // Modal stack input routing. Runs *before* anything else reads input so:
        //   - ESC is dispatched to the topmost popup, never two layers at once.
        //   - Outside-clicks on light-dismiss popups (dropdowns / popovers)
        //     close them and swallow the click.
        // Popups Push themselves on open, Pop on close. When the stack is empty
        // this call is a no-op and input flows to gameplay normally. See
        // Necroking/UI/PopupManager.cs for the contract.
        ReconcileTopLevelEditorLayers();
        _popups.RouteInput(_input);

        // MapEditor → gameplay transition: fire the suppressed pathfinder
        // rebuild once. Per-click placement skips it (would be O(N) per
        // click for a populated map) so we batch into a single rebuild when
        // the editor closes. Catches all exit routes — ESC, pause-menu Resume,
        // main-menu, etc. — because we react to the state change, not the
        // closing action itself.
        if (_prevMenuState == MenuState.MapEditor && _menuState != MenuState.MapEditor)
        {
            // Re-derive pathfinding terrain from the (possibly re-painted)
            // vertex map BEFORE the env-driven rebuild — otherwise newly
            // painted shallow/deep water tiles won't have their cost field
            // updated and units would walk through deep water until the next
            // map load.
            _groundSystem.StampTerrainOnto(_sim.Grid);
            _envSystem.OnCollisionsDirty?.Invoke();
        }
        _prevMenuState = _menuState;

        _rawDt = (float)gameTime.ElapsedGameTime.TotalSeconds;
        float rawDt = _rawDt;
        float dt = _paused ? 0f : MathF.Min(rawDt, 1f / 20f) * _timeScale;
        _gameTime += dt;
        _frameDt = dt;

        // --- Diagnostic: auto-click Start Game from command line (--autostart) ---
        if (_menuState == MenuState.MainMenu && LaunchArgs.AutoStart)
        {
            LaunchArgs.AutoStart = false; // once
            StartGame();
            DebugLog.Log("startup", "[autostart] StartGame() returned — world loaded");
            if (LaunchArgs.BakeCentroids) BakeAllCorpseCentroids();
            if (LaunchArgs.Headless) _autostartExitPending = true; // exit on next frame
            _prevKb = kb;
            _prevMouse = mouse;
            base.Update(gameTime);
            return;
        }

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

        // --- F2 water debug toggle ---
        if (!anyTextInputActive && _input.WasKeyPressed(Keys.F2))
            _waterDebug = !_waterDebug;

        // --- F3 toggles the bottom-left perf/zoom readout ---
        if (!anyTextInputActive && _input.WasKeyPressed(Keys.F3))
            _showPerfReadout = !_showPerfReadout;

        // --- F5 death-fog debug overlay (F9 was requested but is taken by the
        // unit editor toggle; F5 was free) ---
        if (!anyTextInputActive && _input.WasKeyPressed(Keys.F5))
            _deathFog.ToggleDebug();

        // --- F6 wind debug toggle ---
        if (!anyTextInputActive && _input.WasKeyPressed(Keys.F6))
            _windDebug = !_windDebug;

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
        {
            if (_menuState == MenuState.UIEditor) _menuState = MenuState.None;
            else { EnsureUIEditorInitialized(); _menuState = MenuState.UIEditor; }
        }

        // 'I' key toggles inventory (lazy-inits the UI family on first open)
        if (!anyTextInputActive && _input.WasKeyPressed(Keys.I) && _menuState == MenuState.None)
        {
            EnsureInventoryUIsInitialized();
            _inventoryUI.Toggle(GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height);
        }

        // 'Tab' key toggles character stats
        if (!anyTextInputActive && _input.WasKeyPressed(Keys.Tab) && _menuState == MenuState.None)
            _characterStatsUI.Toggle();

        // 'K' = the Ability Upgrades skill book (tabbed school trees).
        if (!anyTextInputActive && _input.WasKeyPressed(Keys.K) && _menuState == MenuState.None)
            _skillBookOverlay.Toggle();

        // 'J' = spell grimoire (phase 1: display only)
        if (!anyTextInputActive && _input.WasKeyPressed(Keys.J) && _menuState == MenuState.None)
        {
            EnsureInventoryUIsInitialized();
            _grimoireOverlay.Toggle();
        }

        // 'U' = character sheet for the player necromancer (current form)
        if (!anyTextInputActive && _input.WasKeyPressed(Keys.U) && _menuState == MenuState.None)
        {
            EnsureInventoryUIsInitialized();
            if (_unitInfoPanel.IsVisible)
                _unitInfoPanel.Hide();
            else if (_sim.NecromancerIndex >= 0)
                _unitInfoPanel.ShowForUnit(_sim.NecromancerIndex);
        }

        // 'O' = inspect the unit under the cursor (press-to-inspect mode; may
        // auto-pause while open, closing restores only the pause WE set).
        // Disabled when auto-show-on-hover is on — the hover logic below owns
        // the panel in that mode. Both modes share the configurable pick radius.
        var tipCfg = _gameData.Settings.Tooltips;
        if (!tipCfg.AutoShowUnitStats
            && !anyTextInputActive && _input.WasKeyPressed(Keys.O) && _menuState == MenuState.None)
        {
            EnsureInventoryUIsInitialized();
            if (_unitInfoPanel.IsVisible)
            {
                _unitInfoPanel.Hide();
            }
            else
            {
                int sw = GraphicsDevice.Viewport.Width, sh = GraphicsDevice.Viewport.Height;
                Vec2 cursorWorld = _camera.ScreenToWorld(new Vector2(mouse.X, mouse.Y), sw, sh);
                int best = -1;
                float pr = tipCfg.HoverPickRadius;
                float bestD2 = pr * pr; // pick radius in world units
                for (int i = 0; i < _sim.Units.Count; i++)
                {
                    var u = _sim.Units[i];
                    if (!u.Alive) continue;
                    float d2 = (u.Position - cursorWorld).LengthSq();
                    if (d2 < bestD2) { bestD2 = d2; best = i; }
                }
                if (best >= 0)
                {
                    _unitInfoPanel.ShowForUnit(best);
                    if (tipCfg.PauseOnManualInspect && !_paused) { _paused = true; _pausedByInspect = true; }
                }
            }
        }

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
            { EnsureUIEditorInitialized(); _menuState = MenuState.UIEditor; _paused = false; }
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

        // --- ESC: gameplay → pause menu only ---
        // Every other ESC case (editors, settings, sub-popups, in-game panels)
        // is handled by PopupManager.RouteInput via the IModalLayer.OnCancel
        // path — see ReconcileTopLevelEditorLayers + Game1.Popups. By the time
        // we get here, IsKeyConsumed is true unless the stack was empty, so
        // the only branch left is "no popup, no editor → pause the game".
        // Text-field ESC is handled by EditorBase.HandleTextInput (deactivates
        // the field), independent of all this.
        if (!anyTextInputActive && !_input.IsKeyConsumed(Keys.Escape) && _input.WasKeyPressed(Keys.Escape))
        {
            if (_menuState == MenuState.None)
            {
                _menuState = MenuState.PauseMenu;
                _paused = true;
            }
        }

        // --- Editor updates ---
        int screenW = GraphicsDevice.Viewport.Width;
        int screenH = GraphicsDevice.Viewport.Height;
        // Update EditorBase input first so _eb has current mouse/keyboard state for all editors
        // WASD/arrow ownership: only the BARE map editor (map editor on top, no
        // sub-editor popup focused) keeps WASD for camera panning; everywhere else
        // WASD navigates the focused editor list. Arrows always navigate lists.
        bool bareMapEditor = _menuState == MenuState.MapEditor && _popups.Top == _mapEditorLayer;
        _editorUi.AllowWasdListNav = !bareMapEditor;
        if (_uiEditor != null) _uiEditor.AllowWasdListNav = true;
        if (_mapEditor != null) _mapEditor.CameraInputEnabled = bareMapEditor;

        if (_menuState == MenuState.UnitEditor || _menuState == MenuState.SpellEditor || _menuState == MenuState.MapEditor || _menuState == MenuState.Settings || _menuState == MenuState.ItemEditor)
            _editorUi.UpdateInput(mouse, _prevMouse, kb, _prevKb, screenW, screenH, gameTime, _input);
        if (_menuState == MenuState.MapEditor && _gameWorldLoaded)
            _mapEditor.Update(screenW, screenH);
        if (_menuState == MenuState.Settings)
            _settingsWindow.Update(screenW, screenH, gameTime);
        if (_menuState == MenuState.UIEditor)
        {
            EnsureUIEditorInitialized(); // safety net: idempotent, covers any entry path
            // UIEditorWindow IS-A EditorBase with its own private _input field.
            // Without this UpdateInput call its _input.Mouse stays default
            // (released) and LeftJustPressed never fires — tab clicks, swatch
            // clicks, etc. all go nowhere. Mirrors the pattern used for
            // _editorUi above; the trailing _input arg shares Game1's
            // captured InputState so the UI editor sees real mouse data.
            _uiEditor.UpdateInput(mouse, _prevMouse, kb, _prevKb, screenW, screenH, gameTime, _input);
            _uiEditor.Update(screenW, screenH);
        }

        // --- Camera ---
        _renderer.SetScreenSize(screenW, screenH);

        // Camera follows necromancer, or free pan in editors/scenarios
        int necroIdx = FindNecromancer();
        bool editorOpen = _menuState == MenuState.UnitEditor || _menuState == MenuState.SpellEditor
            || _menuState == MenuState.MapEditor || _menuState == MenuState.UIEditor
            || _menuState == MenuState.ItemEditor;
        if (editorOpen)
        {
            // Editors no longer free-pan with WASD/arrows — those keys belong to
            // editor list navigation now (handled in EditorBase.DrawScrollableList).
            // The map editor still pans with WASD via MapEditorWindow.Update, gated
            // on CameraInputEnabled (true only for the bare map editor); arrows
            // there are free for list nav.
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
            if (_hudRenderer.HitTestBarSlot(screenW, sec.barY, sec.slotW, sec.slotH, sec.centerOffset, mx, my, slotCount: 6) >= 0)
                _input.MouseOverUI = true;

            // Spell dropdown open
            if (_spellDropdownSlot >= 0 || _secondaryDropdownSlot >= 0)
                _input.MouseOverUI = true;

            // Inventory, building, crafting
            if (_inventoryUI.ContainsMouse(mx, my))
                _input.MouseOverUI = true;
            if (_characterStatsUI.ContainsMouse(screenW, screenH, mx, my, _sim))
                _input.MouseOverUI = true;
            if (_buildingMenuUI.ContainsMouse(mx, my))
                _input.MouseOverUI = true;
            if (_craftingMenu.ContainsMouse(mx, my))
                _input.MouseOverUI = true;
            if (_grimoireOverlay.ContainsMouse(mx, my))
                _input.MouseOverUI = true;
            if (_skillBookOverlay.ContainsMouse(mx, my))
                _input.MouseOverUI = true;

            // Skill-learn corner toasts (clickable to jump to the relevant tab)
            UpdateSkillLearnToastInput(screenW, screenH);

            // Time controls
            if (_gameData.Settings.General.ShowTimeControls
                && _hudRenderer.HitTestTimeControls(screenW, screenH, mx, my) != -1)
                _input.MouseOverUI = true;

            // Core-menu buttons (top-right)
            if (_hudRenderer.HitTestMenuButtons(screenW, mx, my) != -1)
                _input.MouseOverUI = true;

            // Aggression bar — hovering blocks world clicks; a left click snaps the
            // level to the nearest node (same control as Shift+Q / Shift+E).
            if (GetAggressionBarLayout(screenW, screenH, out var aggroBar, out var aggroNodes))
            {
                var aggroHover = aggroBar;
                aggroHover.Inflate(0, 8);
                if (aggroHover.Contains(mx, my))
                {
                    _input.MouseOverUI = true;
                    if (_input.LeftPressed)
                    {
                        _sim.Horde.AggressionLevel = NearestAggroNode(aggroNodes, mx);
                        _input.ConsumeMouse();
                    }
                }
            }
        }

        // Scroll zoom.
        //   Gameplay: zoom when cursor isn't over any UI element.
        //   Map editor: zoom when cursor is over the world area (not the
        //     sidebar) AND only when the map editor itself is the top of the
        //     popup stack — any sub-popup (texture file browser, color picker,
        //     env editor, etc.) sits above and should own the scroll instead.
        //     We can't gate on _input.MouseOverUI here because PopupManager
        //     sets it whenever _mapEditorLayer is on the stack (full-screen
        //     ContainsMouse), which would block zoom even in the world area.
        if (_input.ScrollDelta != 0)
        {
            bool canZoomGameplay = _menuState == MenuState.None
                && !_input.MouseOverUI
                && !_input.IsScrollConsumed;
            bool canZoomMapEditor = _menuState == MenuState.MapEditor
                && _popups.Top == _mapEditorLayer
                && !_mapEditor.IsMouseOverPanel(screenW, screenH);
            if (canZoomGameplay || canZoomMapEditor)
                _camera.ZoomBy(_input.ScrollDelta / 120f);
        }

        // Editors pause the game
        bool editorActive = _menuState == MenuState.UnitEditor || _menuState == MenuState.SpellEditor
            || _menuState == MenuState.MapEditor || _menuState == MenuState.UIEditor
            || _menuState == MenuState.ItemEditor;

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

                // Mouse facing — skipped when the necromancer is mid-scripted-
                // action (e.g. corpse pickup/putdown, channeling at bench). The
                // movement subsystem already zeros velocity in those states, but
                // facing would otherwise still snap to the cursor every frame
                // and swing the bag around mid-animation. WASD input is left
                // ungated so cancel-by-WASD on routines still works through
                // Simulation's existing path.
                if (!_sim.Units[necroIdx].IsLockedByAction())
                {
                    var necroPos = _sim.Units[necroIdx].Position;
                    var toMouse = mouseWorld - necroPos;
                    if (toMouse.LengthSq() > 0.01f)
                    {
                        float mouseAngle = MathF.Atan2(toMouse.Y, toMouse.X) * 180f / MathF.PI;
                        _sim.SetNecromancerFacing(mouseAngle);
                    }
                }
            }

            // --- Jump input (Space = jump attack) ---
            if (_input.WasKeyPressed(Keys.Space) && necroIdx >= 0)
            {
                var mu = _sim.UnitsMut;
                bool canJump = mu[necroIdx].Alive && mu[necroIdx].JumpPhase == 0
                    && !mu[necroIdx].Incap.IsLocked && !_pendingSpell.Active;
                if (canJump)
                {
                    float facingRad = mu[necroIdx].FacingAngle * MathF.PI / 180f;
                    var facingDir = new Vec2(MathF.Cos(facingRad), MathF.Sin(facingRad));
                    JumpSystem.BeginJumpAttack(mu, necroIdx, mu[necroIdx].Position + facingDir * 4f);
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
                        SaveSpellBars();
                        clickedSlot = true;
                        _input.ConsumeMouse();
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
                        // Clicking a slot opens the grimoire to pick a spell for it.
                        int slot = s;
                        EnsureInventoryUIsInitialized();
                        _grimoireOverlay.OpenForAssign(id =>
                        {
                            _spellBarState.Slots[slot].SpellID = id;
                            SaveSpellBars();
                        });
                        clickedSlot = true;
                        _input.ConsumeMouse();
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
                        SaveSpellBars();
                        _secondaryDropdownSlot = -1;
                        _input.ConsumeMouse();
                        goto SkipSpellCast;
                    }
                    _secondaryDropdownSlot = -1;
                }
                else
                {
                    int ss = _hudRenderer.HitTestBarSlot(screenW,
                        secLayout.barY, secLayout.slotW, secLayout.slotH, secLayout.centerOffset,
                        mouse.X, mouse.Y, slotCount: 6);
                    if (ss >= 0)
                    {
                        int slot = ss;
                        EnsureInventoryUIsInitialized();
                        _grimoireOverlay.OpenForAssign(id =>
                        {
                            _secondaryBarState.Slots[slot].SpellID = id;
                            SaveSpellBars();
                        });
                        _input.ConsumeMouse();
                        goto SkipSpellCast;
                    }
                }
            }

            // --- Aggression bar: Shift+E raises, Shift+Q lowers. The shift guard
            // below stops Q/E from also casting slots 0/1 while adjusting. ---
            bool aggrShift = _input.IsKeyDown(Keys.LeftShift) || _input.IsKeyDown(Keys.RightShift);
            if (aggrShift && _input.WasKeyPressed(Keys.E)) _sim.Horde.AggressionLevel++;
            if (aggrShift && _input.WasKeyPressed(Keys.Q)) _sim.Horde.AggressionLevel--;

            // --- Spell casting ---
            // Primary bar: Q = slot 0, E = slot 1, LClick = slot 2, RClick = slot 3
            // Secondary bar: D1-D4 = slots 0-3
            // Both bars share the same dispatch via DispatchSpellCast; the LMB
            // melee-fallback is primary-only and lives just after this loop.
            for (int slot = 0; slot < 4; slot++)
            {
                bool pressed = slot switch
                {
                    0 => !aggrShift && _input.WasKeyPressed(Keys.Q),
                    1 => !aggrShift && _input.WasKeyPressed(Keys.E),
                    2 => !_input.MouseOverUI && _input.LeftPressed,
                    3 => !_input.MouseOverUI && _input.RightPressed,
                    _ => false
                };
                if (!pressed || slot >= _spellBarState.Slots.Length) continue;
                string spellId = _spellBarState.Slots[slot].SpellID;
                var result = DispatchSpellCast(spellId, necroIdx, slot, mouseWorld, isSecondary: false);

                // LMB on empty/failed primary slot = melee swing at nearest enemy.
                if (slot == 2 && result == CastResult.NoValidTarget && necroIdx >= 0
                    && !_sim.Units[necroIdx].PendingAttack.IsNone == false)
                {
                    int meleeTarget = FindClosestEnemyToPoint(_sim.Units[necroIdx].Position, 2f);
                    if (meleeTarget >= 0)
                    {
                        _sim.UnitsMut[necroIdx].Target = CombatTarget.Unit(_sim.Units[meleeTarget].Id);
                        _sim.UnitsMut[necroIdx].PendingAttack = CombatTarget.Unit(_sim.Units[meleeTarget].Id);
                        _sim.UnitsMut[necroIdx].AttackCooldown = 2f;
                    }
                }
            }

            SkipSpellCast:

            // --- Ghost mode toggle (G) ---
            if (_input.WasKeyPressed(Keys.G) && necroIdx >= 0)
                _sim.UnitsMut[necroIdx].GhostMode = !_sim.Units[necroIdx].GhostMode;

            // --- God mode toggle (Shift+P) ---
            // Cheat / debug toggle. Applies/removes buff_god_mode on the
            // necromancer; the buff's effects (path-9, +caps, +mana, +regen,
            // 2x movement) come from data/buffs.json. Duration=0 on the def
            // means the buff is Permanent until removed.
            if (_input.WasKeyPressed(Keys.P) && necroIdx >= 0
                && (_input.IsKeyDown(Keys.LeftShift) || _input.IsKeyDown(Keys.RightShift)))
            {
                ToggleGodMode(necroIdx);
            }

            // --- Add Skillpoints ---
            // Cheat / debug to test skill tree.
            if (_input.WasKeyPressed(Keys.O) && necroIdx >= 0
                                             && (_input.IsKeyDown(Keys.LeftShift) || _input.IsKeyDown(Keys.RightShift)))
            {
                CheatAddAllSkillcounters(necroIdx, 10);
            }
            

            // --- Secondary spell bar (keys 1-4) ---
            if (necroIdx >= 0)
            {
                Keys[] secKeys = { Keys.D1, Keys.D2, Keys.D3, Keys.D4, Keys.D5, Keys.D6 };
                for (int sk = 0; sk < 6; sk++)
                {
                    if (!_input.WasKeyPressed(secKeys[sk])) continue;
                    if (sk >= _secondaryBarState.Slots.Length) continue;
                    string secSpellId = _secondaryBarState.Slots[sk].SpellID;
                    DispatchSpellCast(secSpellId, necroIdx, sk, mouseWorld, isSecondary: true);
                }
            }

            // Potions are now Construction spells (assignable to any spell slot,
            // keys 1-6 / Q E LC RC) — the old dedicated potion slots 5-6 and their
            // throw-on-click flow are gone; casting routes through CastPotionSpell.

            // --- Corpse interaction (F key) ---
            // Table-load takes precedence over normal PutDown when carrying a corpse
            // AND the cursor is over a table within InteractRange of the necromancer.
            // Full table → blocked (no PutDown either; corpse stays held).
            if (_input.WasKeyPressed(Keys.F))
            {
                bool tableHandled = false;
                if (necroIdx >= 0 && _sim.Units[necroIdx].CarryingCorpseID >= 0
                    && _sim.Units[necroIdx].CorpseInteractPhase == 0
                    && _envSystem != null)
                {
                    var necroPos = _sim.Units[necroIdx].Position;
                    int tableIdx = Game.TableSystem.FindTableUnderCursorInRange(_envSystem, mouseWorld, necroPos);
                    if (tableIdx >= 0)
                    {
                        int corpseId = _sim.Units[necroIdx].CarryingCorpseID;
                        var cc = _sim.FindCorpseByID(corpseId);
                        bool corpseEligible = cc != null
                            && Game.TableCraftingSystem.IsCorpseEligibleForTable(_gameData, cc.UnitDefID);
                        var ts = _envSystem.GetTableState(tableIdx);
                        int emptySlot = ts.FindEmptyCorpseSlot();

                        DebugLog.Log("table",
                            $"[F-press] tableIdx={tableIdx} corpseDef='{cc?.UnitDefID ?? "null"}' " +
                            $"eligible={corpseEligible} emptyCorpseSlot={emptySlot}");

                        if (corpseEligible)
                        {
                            if (emptySlot >= 0)
                            {
                                if (cc != null)
                                    cc.LerpStartPos = Game.TableSystem.GetSpawnPos(_envSystem, tableIdx);
                                _sim.UnitsMut[necroIdx].PutDownTableIdx = tableIdx;
                                _sim.UnitsMut[necroIdx].CorpseInteractPhase = 5;
                                DebugLog.Log("table", $"[F-press] Started PutDown anim → table {tableIdx}");
                            }
                            else
                            {
                                DebugLog.Log("table", $"[F-press] BLOCKED: table {tableIdx} corpse slots full");
                            }
                            tableHandled = true;
                        }
                        else
                        {
                            DebugLog.Log("table",
                                $"[F-press] Corpse '{cc?.UnitDefID ?? "null"}' not eligible — falling through to normal PutDown");
                        }
                    }
                    else if (_sim.Units[necroIdx].CarryingCorpseID >= 0)
                    {
                        // Carrying but no table under cursor — log only when this would have mattered.
                        DebugLog.Log("table",
                            $"[F-press] Carrying but no table found under cursor at ({mouseWorld.X:F1},{mouseWorld.Y:F1}) " +
                            $"within necroRange={Game.TableSystem.InteractRange} cursorRange={Game.TableSystem.CursorRange}");
                    }
                }
                if (!tableHandled)
                    Game.CorpseInteractionManager.TryInteract(_sim, necroIdx);
            }

            // --- Ground-object hover detection (buildings + foragable items) ---
            // Picks the nearest hoverable env object under the cursor for the HUD
            // info tooltip. Each kind is gated by its own Tooltips toggle, and the
            // whole thing is suppressed when the cursor is over UI (a popup, HUD
            // bar, etc.) so tooltips don't show through panels.
            _hoveredObjectIdx = -1;
            {
                var tcfg = _gameData.Settings.Tooltips;
                if ((tcfg.ShowBuildingInfo || tcfg.ShowGroundItemInfo) && !_input.MouseOverUI)
                {
                    float pr = tcfg.GroundPickRadius;
                    float bhd = pr * pr;
                    for (int oi = 0; oi < _envSystem.ObjectCount; oi++)
                    {
                        var obj = _envSystem.GetObject(oi);
                        var d = _envSystem.Defs[obj.DefIndex];
                        bool hoverable = (d.IsBuilding && tcfg.ShowBuildingInfo)
                                       || ((d.IsForagable || d.IsBerryBush) && tcfg.ShowGroundItemInfo);
                        if (!hoverable) continue;
                        // Collected foragables are invisible while respawning — don't
                        // surface a tooltip for something that isn't drawn.
                        if (d.IsForagable && _envSystem.GetObjectRuntime(oi).Collected) continue;
                        float hdx = obj.X - mouseWorld.X, hdy = obj.Y - mouseWorld.Y;
                        float hd = hdx * hdx + hdy * hdy;
                        if (hd < bhd) { bhd = hd; _hoveredObjectIdx = oi; }
                    }
                }
            }

            // --- Unit auto-hover stat sheet (Factorio-style; opt-in via Tooltips) ---
            // The cursor "carries" the stat sheet: hover a unit to show it, move off
            // to dismiss. Never pauses. The auto-shown panel is transient (not a
            // popup) so MouseOverUI stays clean — letting us re-pick and hide as the
            // cursor moves. We only ever touch the panel when it's our transient view
            // (or nothing's shown), so a pinned 'U'/'O' sheet is left alone.
            if (_gameData.Settings.Tooltips.AutoShowUnitStats)
            {
                EnsureInventoryUIsInitialized();
                bool ownsPanel = !_unitInfoPanel.IsVisible || _unitInfoPanel.IsTransient;
                // Suppress over real UI (other popups, HUD bars) or while parked on
                // the panel itself (so the user can hover stat cells for breakdowns).
                bool overUI = _input.MouseOverUI
                              || (_unitInfoPanel.IsVisible && _unitInfoPanel.ContainsMouse(mouse.X, mouse.Y));
                int hoveredUnit = -1;
                if (ownsPanel && !overUI)
                {
                    float pr = _gameData.Settings.Tooltips.HoverPickRadius;
                    float bestD2 = pr * pr;
                    for (int i = 0; i < _sim.Units.Count; i++)
                    {
                        var u = _sim.Units[i];
                        if (!u.Alive) continue;
                        float udx = u.Position.X - mouseWorld.X, udy = u.Position.Y - mouseWorld.Y;
                        float d2 = udx * udx + udy * udy;
                        if (d2 < bestD2) { bestD2 = d2; hoveredUnit = i; }
                    }
                }

                if (!ownsPanel)
                {
                    // A pinned sheet is up — leave it be.
                }
                else if (hoveredUnit >= 0)
                {
                    if (_unitInfoPanel.UnitIndex != hoveredUnit)
                        _unitInfoPanel.ShowForUnitTransient(hoveredUnit);
                }
                else if (_unitInfoPanel.IsVisible && !overUI)
                {
                    // Cursor left all units (and isn't parked on the panel) — dismiss.
                    _unitInfoPanel.Hide();
                }
            }

            // --- Building placement toggle (B) ---
            if (_input.WasKeyPressed(Keys.B))
            {
                EnsureInventoryUIsInitialized();
                if (_craftingMenu.IsVisible) _craftingMenu.Close();
                _buildingMenuUI.Toggle(screenW, screenH);
            }

            // --- Crafting menu toggle (C) ---
            if (_input.WasKeyPressed(Keys.C))
            {
                EnsureInventoryUIsInitialized();
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

            // --- Left-click table to reopen its craft menu ---
            // Only fires when no build placement is active and click hasn't been
            // consumed by another UI element (e.g. the menu itself, inventory).
            if (!_input.MouseOverUI && _input.LeftPressed && !_input.IsMouseConsumed
                && !_buildingMenuUI.IsPlacementActive && _envSystem != null)
            {
                int clickedTable = Game.TableSystem.FindTableUnderCursor(_envSystem, mouseWorld);
                if (clickedTable >= 0)
                {
                    EnsureInventoryUIsInitialized();
                    _tableMenuUI.OpenForTable(clickedTable, screenW, screenH, _camera, _renderer);
                    _input.ConsumeMouse();
                }
            }

            // --- Foragable collection (right-click within 2 units of necromancer) ---
            if (!_input.MouseOverUI && _input.RightPressed
                && _sim.NecromancerIndex >= 0)
            {
                int bestIdx = _foragables.FindNearest(_sim.Units[_sim.NecromancerIndex].Position, 2f);
                if (bestIdx >= 0)
                    _foragables.StartCollection(bestIdx);
            }

            // --- Auto-pickup foragables ---
            _foragables.TickAutoPickup(dt, _gameData.Settings.General.AutoPickupForagables);

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

            // --- Core-menu buttons (top-right) click ---
            if (_input.LeftPressed && !_input.IsMouseConsumed)
            {
                int mbHit = _hudRenderer.HitTestMenuButtons(screenW, mouse.X, mouse.Y);
                if (mbHit >= 0)
                {
                    ToggleCoreMenu(mbHit, screenW, screenH);
                    _input.ConsumeMouse();
                }
            }

            // --- Tick pending projectiles ---
            TickPendingProjectiles(dt);

            // --- Simulate ---
            _sim.Tick(dt);
            FinalizeBushWorkIfPending();
            _dayNightSystem.Update(dt, _gameData);
            _sim.MagicGlyphs.Update(dt, _sim.UnitsMut, _sim.Quadtree, _sim.PoisonClouds, _gameData.Spells);
            _weatherRenderer.Update(dt, _gameData);
            _envSystem.UpdateForagables(dt);
            _envSystem.UpdateBerryBushes(dt);
            _envSystem.UpdateAnimations(dt, _gameTime);
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
                    EnsureInventoryUIsInitialized();
                    _inventoryUI.Open(screenW, screenH);
                }
                if (_activeScenario.RequestCloseInventory)
                {
                    _activeScenario.RequestCloseInventory = false;
                    _inventoryUI.Close();
                }
                if (_activeScenario.RequestOpenSkillBook)
                {
                    _activeScenario.RequestOpenSkillBook = false;
                    _skillBookOverlay.Open();
                }
                if (_activeScenario.RequestCloseSkillBook)
                {
                    _activeScenario.RequestCloseSkillBook = false;
                    _skillBookOverlay.Close();
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
                    IsPoison = dmg.IsPoison,
                    IsFatigue = dmg.IsFatigue
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

            // Select a specific UI-editor widget by id from scenario (Widgets tab)
            if (_activeScenario.RequestSelectUIWidgetById != null)
            {
                var wid = _activeScenario.RequestSelectUIWidgetById;
                _activeScenario.RequestSelectUIWidgetById = null;
                if (_menuState == MenuState.UIEditor) _uiEditor.SelectWidgetById(wid);
            }

            // Inventory UI requests from scenario
            if (_activeScenario.RequestOpenInventory)
            {
                _activeScenario.RequestOpenInventory = false;
                EnsureInventoryUIsInitialized();
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
        _grimoireOverlay.Update(_input, screenW, screenH);
        _tableMenuUI.Update(_input);

        // Inventory→table item transfer: while the table menu is open, clicking a
        // filled inventory slot deposits the item into the table's first empty
        // item slot (and decrements the inventory by 1). Inventory must be visible
        // for slot rects to be valid.
        if (_tableMenuUI.IsVisible && _inventoryUI.IsVisible
            && _input.LeftPressed && !_input.IsMouseConsumed)
        {
            int mx = (int)_input.MousePos.X, my = (int)_input.MousePos.Y;
            if (_inventoryUI.TryGetSlotIndexAt(mx, my, out int slotIdx))
            {
                var s = _inventory.GetSlot(slotIdx);
                if (!s.IsEmpty && _tableMenuUI.TryDepositItem(s.ItemId))
                    _input.ConsumeMouse();
            }
        }
        _skillBookOverlay.Update(_input, screenW, screenH, gameTime.TotalGameTime.TotalSeconds);

        // Cursor swap: hand when hovering interactive UI, arrow otherwise
        bool overInteractiveUI = _input.MouseOverUI || _editorUi.IsMouseOverUI;
        Mouse.SetCursor(overInteractiveUI ? MouseCursor.Hand : MouseCursor.Arrow);

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
                var style = spell.BuildStrikeStyle();
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
                var style = spell.BuildStrikeStyle();
                var sVis = spell.StrikeVisualType == "GodRay" ? StrikeVisual.GodRay : StrikeVisual.Lightning;
                _sim.Lightning.SpawnStrike(targetPos, spell.TelegraphDuration,
                    spell.StrikeDuration, spell.AoeRadius, spell.Damage,
                    style, spell.Id, sVis, telegraphVisible: spell.TelegraphVisible);
            }
            else if (spell.Category == "Cloud")
            {
                // Spawn cloud at the trap position (not the target — the trap itself is
                // where the burst originates). Same code path as a caster casting the spell.
                _spellEffects.ExecuteCloud(spell, _sim, evt.TrapPos);
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

    /// <summary>Persist the current primary + secondary spell bar slot
    /// assignments to spellbar.json. Called whenever the player edits a slot
    /// via the in-game dropdown, and on game exit. Atomic write — partial
    /// writes can't corrupt the file.</summary>
    private void SaveSpellBars()
    {
        try
        {
            var doc = new Dictionary<string, object>
            {
                ["slots"] = _spellBarState.Slots.Select(s => new Dictionary<string, string>
                {
                    ["spellID"] = s.SpellID ?? ""
                }).Cast<object>().ToList(),
                ["secondary"] = _secondaryBarState.Slots.Select(s => new Dictionary<string, string>
                {
                    ["spellID"] = s.SpellID ?? ""
                }).Cast<object>().ToList(),
            };
            var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
            string json = System.Text.Json.JsonSerializer.Serialize(doc, options);
            System.IO.Directory.CreateDirectory(GamePaths.Resolve(GamePaths.UserSettingsDir));
            Core.AtomicFile.WriteAllText(GamePaths.Resolve(GamePaths.UserSpellBarJson), json);
        }
        catch (Exception ex) { DebugLog.Log("error", $"SaveSpellBars failed: {ex.Message}"); }
    }

    /// <summary>Cast a potion-spell: drink it (self, cursor near the necromancer)
    /// or throw it (at the cursor), consuming the inventory item — the same logic
    /// the old potion hotkeys used, now reachable from any spell slot.</summary>
    private void CastPotionSpell(string potionId, string itemId, int necroIdx, Vec2 mouseWorld)
    {
        if (necroIdx < 0 || _inventory.GetItemCount(itemId) <= 0) return;
        var potionDef = _gameData.Potions.Get(potionId);
        if (potionDef == null) return;

        var necroPos = _sim.Units[necroIdx].Position;
        if ((mouseWorld - necroPos).Length() < 1.0f)
        {
            // Self-target: drink.
            _inventory.RemoveItem(itemId, 1);
            PotionSystem.ApplyPotionEffect(potionDef.Id, _gameData.Potions, _gameData.Buffs,
                necroIdx, _sim.UnitsMut, _sim.Units[necroIdx].Faction,
                _sim.PendingZombieRaises, _sim.CorpsesMut, necroPos);
        }
        else
        {
            PotionSystem.TryThrowPotion(potionDef.Id, _gameData.Potions, _inventory,
                _sim.UnitsMut, necroIdx, mouseWorld, _sim.Corpses, _sim.Projectiles);
        }
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
        => _foragables.FindNearest(fromPos, maxDist);

    /// <summary>
    /// Start (or resume) a craft on the given table. Spends essence on the FIRST
    /// start of a fresh craft (ts.Crafting=false). When called while ts.Crafting=true,
    /// no essence is spent — this is the resume path after the player walked away
    /// from a paused channel. Always (re)assigns the necromancer to RoutineCraftAtTable.
    /// Returns true if the craft is now active.
    /// </summary>
    private bool StartTableCraft(int envIdx)
    {
        if (envIdx < 0 || _envSystem == null) return false;
        int necroIdx = _sim.NecromancerIndex;
        if (necroIdx < 0) return false;

        // No-op if the necromancer is already in the craft routine for THIS table —
        // double-clicking Start mid-channel would otherwise reset WalkToSite and
        // restart the walk-up animation.
        if (_sim.Units[necroIdx].Routine == AI.PlayerControlledHandler.RoutineCraftAtTable
            && _sim.Units[necroIdx].CraftTableIdx == envIdx)
            return true;

        var def = _envSystem.Defs[_envSystem.GetObject(envIdx).DefIndex];
        var ts = _envSystem.GetTableState(envIdx);

        if (!ts.Crafting)
        {
            // Fresh craft — gate on inputs + spend essence.
            if (!ts.HasAnyCorpse()) return false;

            // Horde-cap gate: peek at the corpse that's about to be raised and
            // refuse if the resulting unit's category is full. Mirrors the spell
            // pre-check so essence isn't spent on a craft that would produce
            // nothing. The actual completion in TableCraftingSystem.CompleteCraft
            // re-checks under the same conditions (state may have shifted).
            int peekSlot = -1;
            for (int i = 0; i < ts.CorpseSlots.Length; i++)
                if (!ts.CorpseSlots[i].IsEmpty) { peekSlot = i; break; }
            if (peekSlot >= 0)
            {
                string peekZombie = Game.TableCraftingSystem.ResolveZombieUnitID(
                    _gameData, ts.CorpseSlots[peekSlot].SourceUnitDefID);
                if (!string.IsNullOrEmpty(peekZombie))
                {
                    var peekCat = HordeCapTracker.CategoryFor(_gameData, peekZombie);
                    if (peekCat != UndeadCategory.None
                        && HordeCapTracker.Available(_sim.Units, _gameData, _sim.NecroState, peekCat) <= 0)
                    {
                        SpawnHordeCapText(necroIdx);
                        return false;
                    }
                }
            }

            if (!_sim.PlayerResources.SpendEssence(def.EssenceCost)) return false;
            ts.Crafting = true;
            ts.CraftTimer = 0f;
            ts.LoopBudget = 0f; // recomputed render-side once the imbue loop starts
        }
        // else: resume — essence already spent on first start; just reassign channeler.

        ts.ChannelerUnitID = _sim.Units[necroIdx].Id;
        _sim.UnitsMut[necroIdx].CraftTableIdx = envIdx;
        _sim.UnitsMut[necroIdx].Routine = AI.PlayerControlledHandler.RoutineCraftAtTable;
        _sim.UnitsMut[necroIdx].Subroutine = AI.PlayerControlledHandler.BuildSub_WalkToSite;
        _sim.UnitsMut[necroIdx].BuildTimer = 0f;
        return true;
    }

    /// <summary>Single dispatch path for both spell bars. Handles built-in
    /// ability intercepts (melee_gather, poison_berries_*) before falling
    /// through to the normal SpellCaster + casting-buff + pending-anim pipeline.
    /// Returns the cast result so callers can react (e.g. LMB melee fallback).</summary>
    private CastResult DispatchSpellCast(string spellId, int necroIdx, int slot,
        Vec2 mouseWorld, bool isSecondary)
    {
        if (string.IsNullOrEmpty(spellId) || necroIdx < 0) return CastResult.NoValidTarget;

        // Built-in abilities short-circuit the normal spell pipeline.
        if (TryDispatchBuiltinAbility(spellId, necroIdx, mouseWorld))
            return CastResult.Success;

        // Potion-spells (ConsumesItem set) run through the existing PotionSystem
        // throw/drink path + inventory consume, not the normal spell pipeline.
        var spellDef = _gameData.Spells.Get(spellId);
        if (spellDef != null && !string.IsNullOrEmpty(spellDef.ConsumesItem))
        {
            CastPotionSpell(spellId, spellDef.ConsumesItem, necroIdx, mouseWorld);
            return CastResult.Success;
        }

        // Can't cast a real spell while one is mid-animation.
        if (_pendingCastAnim != null) return CastResult.NoValidTarget;

        var result = SpellCaster.TryStartSpellCast(spellId, _gameData.Spells, _sim.NecroState,
            _sim.Units, necroIdx, mouseWorld, _sim.Corpses, _pendingSpell, _gameData);
        if (result == CastResult.HordeCapFull)
            SpawnHordeCapText(necroIdx);
        if (result != CastResult.Success) return result;

        // Tally a player spell cast for the skill-book milestone (mirrors the
        // monster_kill / human_kill counters). Magic-tree skills cost "cast_spell"
        // events, so each successful real-spell cast advances them. Built-in
        // abilities and potion-throws short-circuit above and don't count.
        _sim.PlayerEvents.Tally(PlayerEventTracker.Keys.CastSpell);

        var spell = _gameData.Spells.Get(spellId);
        if (spell == null) return result;

        if (IsChanneledCast(spell.CastAnim))
        {
            // Channeled reanimation cast (Start→Loop→Finish). Effect fires at the
            // end of the loop; the necromancer faces the target for the duration.
            if (!string.IsNullOrEmpty(spell.CastingBuffID))
            {
                var cb = _gameData.Buffs.Get(spell.CastingBuffID);
                if (cb != null) BuffSystem.ApplyBuff(_sim.UnitsMut, necroIdx, cb);
            }

            var dir = mouseWorld - _sim.Units[necroIdx].Position;
            if (dir.LengthSq() > 0.0001f)
                _sim.UnitsMut[necroIdx].FacingAngle = MathF.Atan2(dir.Y, dir.X) * 180f / MathF.PI;

            _pendingCastAnim = new PendingCastAnim
            {
                SpellID = spellId, Target = mouseWorld, Slot = slot, IsSecondary = isSecondary,
                CastingBuffID = spell.CastingBuffID,
                CastAnim = spell.CastAnim, ChannelPhase = 0,
                ChannelElapsed = 0f, LoopElapsed = 0f, CastTime = spell.CastTime, Executed = false,
            };

            GetChannelStates(spell.CastAnim, out var startS, out _, out _);
            uint nUid = _sim.Units[necroIdx].Id;
            if (_unitAnims.TryGetValue(nUid, out var nAnim))
            {
                nAnim.Ctrl.ForceState(startS);
                _unitAnims[nUid] = nAnim;
            }
        }
        else if (!string.IsNullOrEmpty(spell.CastingBuffID))
        {
            // Defer execution to the Spell1 animation event.
            var castBuff = _gameData.Buffs.Get(spell.CastingBuffID);
            if (castBuff != null) BuffSystem.ApplyBuff(_sim.UnitsMut, necroIdx, castBuff);

            _pendingCastAnim = new PendingCastAnim
            {
                SpellID = spellId,
                Target = mouseWorld,
                Slot = slot,
                IsSecondary = isSecondary,
                CastingBuffID = spell.CastingBuffID,
            };

            uint necroUid = _sim.Units[necroIdx].Id;
            if (_unitAnims.TryGetValue(necroUid, out var necroAnim))
            {
                necroAnim.Ctrl.RequestState(AnimState.Spell1);
                _unitAnims[necroUid] = necroAnim;
            }
        }
        else
        {
            // No casting buff → execute immediately (legacy behavior).
            ExecuteSpellEffect(spell, necroIdx, mouseWorld, slot);
        }
        return CastResult.Success;
    }

    /// <summary>Built-in abilities don't live in spells.json — they're hard-wired
    /// IDs that bypass the SpellCaster pipeline entirely. Returns true if the id
    /// was a built-in (handled or rejected); false if the caller should fall
    /// through to normal spell dispatch.</summary>
    private bool TryDispatchBuiltinAbility(string spellId, int necroIdx, Vec2 mouseWorld)
    {
        if (spellId == "melee_gather")
        {
            TryMeleeOrGather(necroIdx, mouseWorld);
            return true;
        }
        if (PoisonBerryAbilities.TryGetValue(spellId, out var pb))
        {
            TryStartPoisonBerries(necroIdx, mouseWorld, pb.buffID, pb.itemID);
            return true;
        }
        return false;
    }

    /// <summary>Built-in poison-berries abilities: spell id → (buff applied to the
    /// eater, potion item consumed). Single source of truth for both casting
    /// (<see cref="TryDispatchBuiltinAbility"/>) and the grimoire "seen materials"
    /// gate: each spell def MUST declare consumesItem == itemID so the ability
    /// stays hidden until the player has seen the potion. <see
    /// cref="ValidatePotionAbilities"/> enforces that at load.</summary>
    private static readonly Dictionary<string, (string buffID, string itemID)> PoisonBerryAbilities = new()
    {
        ["poison_berries_poison"]    = ("buff_poison_dot",     "potion_poison"),
        ["poison_berries_paralysis"] = ("buff_paralysis_slow", "potion_paralysis"),
    };

    /// <summary>Guard against the "skill visible before its material is seen"
    /// regression: every built-in potion ability must declare consumesItem ==
    /// the potion it actually consumes, and that item must exist. The grimoire
    /// gate keys off consumesItem, so a missing/mismatched value silently leaks
    /// the ability into the menu before the player has seen the potion. Logs a
    /// loud warning (rather than throwing) so a data slip is caught in the log
    /// without bricking the game.</summary>
    private void ValidatePotionAbilities()
    {
        foreach (var (spellId, pb) in PoisonBerryAbilities)
        {
            var def = _gameData.Spells.Get(spellId);
            if (def == null)
            {
                DebugLog.Log("startup", $"[ValidatePotionAbilities] WARNING: built-in ability '{spellId}' has no spell def in spells.json");
                continue;
            }
            if (def.ConsumesItem != pb.itemID)
                DebugLog.Log("startup", $"[ValidatePotionAbilities] WARNING: '{spellId}' consumesItem='{def.ConsumesItem}' but ability consumes '{pb.itemID}' — the 'seen materials' gate will not hide it correctly. Set consumesItem to '{pb.itemID}' in spells.json.");
            if (_gameData.Items.Get(pb.itemID) == null)
                DebugLog.Log("startup", $"[ValidatePotionAbilities] WARNING: '{spellId}' consumes item '{pb.itemID}' which is not in the item registry.");
        }
    }

    /// <summary>Player clicked a target while holding the Poison Berries ability.
    /// Picks the nearest berry bush within range of the mouse, validates that
    /// the bush is in Berries state, that the player has the matching potion,
    /// and starts the work routine. Does NOT consume the potion — consumption
    /// only happens when the routine completes successfully (see
    /// <see cref="FinalizeBushWorkIfPending"/>).</summary>
    private void TryStartPoisonBerries(int necroIdx, Vec2 mouseWorld, string buffID, string itemID)
    {
        if (necroIdx < 0) return;
        if (_inventory.GetItemCount(itemID) <= 0)
        {
            DebugLog.Log("ai", $"[PoisonBerries] no {itemID} in inventory — ignored");
            return;
        }

        // Two-stage pick: prefer the bush closest to the cursor (small radius);
        // if the click was nowhere near a bush, fall back to the bush closest to
        // the necromancer within a larger range so the ability is forgiving.
        int bushIdx = FindBerryBushNear(mouseWorld, 4f);
        if (bushIdx < 0)
        {
            var necroPos = _sim.Units[necroIdx].Position;
            bushIdx = FindBerryBushNear(necroPos, 20f);
        }
        if (bushIdx < 0)
        {
            DebugLog.Log("ai", "[PoisonBerries] no Berries-state berry bush near cursor or player");
            return;
        }

        var u = _sim.UnitsMut[necroIdx];
        u.Routine = AI.PlayerControlledHandler.RoutineWorkOnBush;
        u.Subroutine = AI.PlayerControlledHandler.BuildSub_WalkToSite;
        u.BushWorkObjIdx = bushIdx;
        u.BushWorkBuffID = buffID;
        u.BushWorkItemID = itemID;
        u.BuildTimer = 0f;
        u.CorpseInteractPhase = 0;
        DebugLog.Log("ai", $"[PoisonBerries] start: bushIdx={bushIdx} buff={buffID} item={itemID}");
    }

    /// <summary>Pick a berry bush in Berries state nearest to <paramref name="worldPos"/>.
    /// Returns -1 if no eligible bush is within <paramref name="maxRadius"/>.</summary>
    private int FindBerryBushNear(Vec2 worldPos, float maxRadius)
    {
        int best = -1;
        float bestD = maxRadius * maxRadius;
        for (int i = 0; i < _envSystem.ObjectCount; i++)
        {
            var def = _envSystem.GetDef(_envSystem.GetObject(i).DefIndex);
            if (!def.IsBerryBush) continue;
            var rt = _envSystem.GetObjectRuntime(i);
            if (!rt.Alive || rt.BerryState != World.BerryState.Berries) continue;
            var obj = _envSystem.GetObject(i);
            float dx = obj.X - worldPos.X, dy = obj.Y - worldPos.Y;
            float d = dx * dx + dy * dy;
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    /// <summary>Post-AI hook. Fires the moment WorkLoop completes (signalled
    /// by Subroutine == BushSub_AwaitFinalize) — applies the bush poison and
    /// consumes one of the matching potion immediately, then hands the routine
    /// back to WorkEnd so the standup animation plays out. The BushWork*
    /// fields stay populated through standup; they're cleared by the AI
    /// handler's CancelBushWork on Phase.Done.</summary>
    private void FinalizeBushWorkIfPending()
    {
        int necroIdx = _sim.NecromancerIndex;
        if (necroIdx < 0) return;
        if (_sim.Units[necroIdx].Routine != AI.PlayerControlledHandler.RoutineWorkOnBush) return;
        if (_sim.Units[necroIdx].Subroutine != AI.PlayerControlledHandler.BushSub_AwaitFinalize) return;

        int bushIdx = _sim.Units[necroIdx].BushWorkObjIdx;
        string buffID = _sim.Units[necroIdx].BushWorkBuffID;
        string itemID = _sim.Units[necroIdx].BushWorkItemID;

        bool applied = bushIdx >= 0 && _envSystem.PoisonBerryBush(bushIdx, buffID);
        if (applied && !string.IsNullOrEmpty(itemID))
            _inventory.RemoveItem(itemID, 1);

        // Continue to WorkEnd so the standup plays normally.
        var u = _sim.UnitsMut[necroIdx];
        u.Subroutine = AI.WorkRoutine.WorkEnd;
    }

    /// <summary>Start a foragable collection with arc animation instead of instant pickup.</summary>
    private void StartForagableCollection(int objIdx) => _foragables.StartCollection(objIdx);

    /// <summary>Game1 hook fired by ForagableSystem after a pickup lands.
    /// Spawns the floating green pickup text in the damage-numbers list.</summary>
    private void OnForagablePickedUp(Vec2 worldPos, string resourceType)
    {
        _damageNumbers.Add(new DamageNumber
        {
            WorldPos = worldPos,
            Damage = 0,
            Timer = 0f,
            Height = 2f,
            IsPoison = false,
            PickupText = resourceType,
        });
    }

    private void CheatAddAllSkillcounters(int necroIdx, int amount) 
    {
       if (necroIdx < 0 || _gameData == null) return;

       foreach (var et in SkillBookState.EVENT_TYPES) {
          _skillBookState.Events.Tally(et, amount);
       }

       foreach (var sp in SkillBookState.SKILL_POINT_TYPES) {
          _skillBookState.AddSkillPoints(sp, amount);
       }

       // Potions needed for metamorphosis.
       _inventory.AddItem("potion_death_evolution", amount);
    }

    /// <summary>Floating "Horde Full" text above the necromancer when a summon
    /// is refused for hitting the cap. Reuses the DamageNumber PickupText
    /// channel so the existing renderer + fade-out apply unchanged.</summary>
    /// <summary>Shift+P cheat. Applies / removes buff_god_mode on the
    /// necromancer. On apply, also tops mana up to the new effective cap so
    /// the +999 mana effect is felt immediately rather than slowly filling
    /// at the buffed regen rate.</summary>
    private void ToggleGodMode(int necroIdx)
    {
        if (necroIdx < 0 || _gameData == null) return;
        const string godBuffId = "buff_god_mode";
        if (BuffSystem.HasBuff(_sim.Units, necroIdx, godBuffId))
        {
            BuffSystem.RemoveBuff(_sim.UnitsMut, necroIdx, godBuffId);
            // Clamp mana back down to the base cap since the +999 just went
            // away — without this, Mana would stay at e.g. 1049 against a 50
            // MaxMana and read weirdly in the HUD.
            if (_sim.NecroState.Mana > _sim.NecroState.MaxMana)
                _sim.NecroState.Mana = _sim.NecroState.MaxMana;
            return;
        }
        var def = _gameData.Buffs.Get(godBuffId);
        if (def == null)
        {
            DebugLog.Log("ai", "[GodMode] buff_god_mode missing from buffs.json — toggle ignored.");
            return;
        }
        BuffSystem.ApplyBuff(_sim.UnitsMut, necroIdx, def);
        // Instant top-up to the buffed cap.
        float maxManaEff = _sim.NecroState.MaxMana
            + BuffSystem.SumExtraAdd(_sim.Units, necroIdx, "MaxMana");
        _sim.NecroState.Mana = maxManaEff;
    }

    private void SpawnHordeCapText(int necroIdx)
    {
        if (necroIdx < 0 || necroIdx >= _sim.Units.Count) return;
        _damageNumbers.Add(new DamageNumber
        {
            WorldPos = _sim.Units[necroIdx].Position,
            Damage = 0,
            Timer = 0f,
            Height = 2f,
            IsPoison = false,
            PickupText = "Horde Full",
            IsAlert = true,
        });
    }

    /// <summary>Game1 hook fired by ForagableSystem on every pickup. First
    /// mushroom of any kind teaches the root Paralysis potion recipe.</summary>
    private void OnForagableLearnTrigger(string resourceType)
    {
        if (resourceType == "Mushroom"
            || resourceType == "MagicMushroom"
            || resourceType == "PoisonMushroom"
            || resourceType == "Ghostcap"
            || resourceType == "Rotgill")
        {
            TryAutoLearn("skill_paralysis", "Recipe Learned");
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

    /// <summary>Per-unit attack cycle in seconds: weapon.CooldownRounds × RoundDuration.
    /// Falls back to 1 round when the unit has no melee weapon defined.</summary>
    private float ComputeWeaponCycleSeconds(int unitIdx, int weaponIdx)
    {
        float round = _gameData.Settings.Combat.RoundDuration;
        int cdRounds = 1;
        var stats = _sim.Units[unitIdx].Stats;
        if (weaponIdx >= 0 && weaponIdx < stats.MeleeWeapons.Count)
            cdRounds = Math.Max(1, stats.MeleeWeapons[weaponIdx].CooldownRounds);
        return cdRounds * round;
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

    private void SpawnSpellProjectile(SpellDef spell, Vec2 origin, Vec2 target, uint ownerUid, float spawnHeight)
    {
        _sim.Projectiles.SpawnFireball(origin, target,
            Faction.Undead, ownerUid, spell.Damage, spell.AoeRadius, spell.DisplayName,
            spawnHeight: spawnHeight);
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
                    float spawnH = necroIdx >= 0 ? _sim.Units[necroIdx].EffectSpawnHeight : 0.6f;
                    SpawnSpellProjectile(spell, origin, pg.Target, ownerUid, spawnH);
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

    /// <summary>Drive a channeled reanimation cast: Start → Loop → (Finish). The
    /// spell effect fires at the END of the loop; total loop time = CastTime minus
    /// the Start duration, with a minimum of one full loop cycle. The necromancer
    /// faces the target throughout. Raise has no Finish → straight to Idle.</summary>
    private void UpdateChanneledCast(float dt)
    {
        if (_pendingCastAnim == null) return;
        int necroIdx = FindNecromancer();
        if (necroIdx < 0) { _pendingCastAnim = null; return; }
        uint uid = _sim.Units[necroIdx].Id;
        if (!_unitAnims.TryGetValue(uid, out var anim)) { _pendingCastAnim = null; return; }
        var ctrl = anim.Ctrl;

        var pca = _pendingCastAnim.Value;
        GetChannelStates(pca.CastAnim, out var startS, out var loopS, out var finishS);

        // Keep facing the target for the whole channel.
        var dir = pca.Target - _sim.Units[necroIdx].Position;
        if (dir.LengthSq() > 0.0001f)
            _sim.UnitsMut[necroIdx].FacingAngle = MathF.Atan2(dir.Y, dir.X) * 180f / MathF.PI;

        pca.ChannelElapsed += dt;

        switch (pca.ChannelPhase)
        {
            case 0: // Start (play once, hold at end)
                if (ctrl.CurrentState != startS) ctrl.ForceState(startS);
                if (ctrl.IsAnimFinished)
                {
                    pca.ChannelPhase = 1;
                    pca.LoopElapsed = 0f;
                    ctrl.ForceState(loopS);
                }
                break;

            case 1: // Loop — fire the effect at the end of the loop
                if (ctrl.CurrentState != loopS) ctrl.ForceState(loopS);
                pca.LoopElapsed += dt;
                float startDur = pca.ChannelElapsed - pca.LoopElapsed;
                float oneCycle = MathF.Max(0.05f, ctrl.CurrentAnimDurationMs / 1000f);
                float loopTarget = MathF.Max(pca.CastTime - startDur, oneCycle);
                if (pca.LoopElapsed >= loopTarget)
                {
                    var spell = _gameData.Spells.Get(pca.SpellID);
                    if (spell != null) ExecuteSpellEffect(spell, necroIdx, pca.Target, pca.Slot);
                    if (finishS.HasValue)
                    {
                        pca.ChannelPhase = 2;
                        ctrl.ForceState(finishS.Value);
                    }
                    else
                    {
                        ctrl.ForceState(AnimState.Idle);
                        RemoveCastingBuffAll(necroIdx);
                        _pendingCastAnim = null;
                        return;
                    }
                }
                break;

            case 2: // Finish (play once)
                if (finishS.HasValue && ctrl.CurrentState != finishS.Value) ctrl.ForceState(finishS.Value);
                if (ctrl.IsAnimFinished)
                {
                    ctrl.ForceState(AnimState.Idle);
                    RemoveCastingBuffAll(necroIdx);
                    _pendingCastAnim = null;
                    return;
                }
                break;
        }

        _pendingCastAnim = pca;
    }

    // "CastingEffect Green" buff — visual glow shown on the necromancer while it
    // channels a reanimation at the necro table.
    private const string TableChannelBuffId = "buff_4_copy";

    /// <summary>Apply/remove the green casting glow on the necromancer based on
    /// whether it's actively channeling (imbuing) at a craft table. Idempotent —
    /// covers every start/cancel/complete path in one place.</summary>
    private void UpdateTableChannelBuff()
    {
        int necroIdx = FindNecromancer();
        if (necroIdx < 0) return;
        bool channeling = _sim.Units[necroIdx].CraftTableIdx >= 0
            && _sim.Units[necroIdx].CorpseInteractPhase != 0;
        bool has = BuffSystem.HasBuff(_sim.UnitsMut, necroIdx, TableChannelBuffId);
        if (channeling && !has)
        {
            var b = _gameData.Buffs.Get(TableChannelBuffId);
            if (b != null) BuffSystem.ApplyBuff(_sim.UnitsMut, necroIdx, b);
        }
        else if (!channeling && has)
        {
            BuffSystem.RemoveBuff(_sim.UnitsMut, necroIdx, TableChannelBuffId);
        }
    }

    private void UpdateAnimations(float dt)
    {
        for (int i = 0; i < _sim.Units.Count; i++)
        {
            if (!_sim.Units[i].Alive) continue;

            uint uid = _sim.Units[i].Id;

            // Drop the cached anim if the unit's def was swapped (e.g. necromancer
            // morph via Metamorphosis skill). Otherwise the controller stays bound
            // to the old atlas + sprite and the visible form never updates.
            if (_unitAnims.TryGetValue(uid, out var existing)
                && existing.CachedDefID != _sim.Units[i].UnitDefID)
                _unitAnims.Remove(uid);

            if (!_unitAnims.TryGetValue(uid, out var animData))
            {
                // Try to init from defID
                string defID = _sim.Units[i].UnitDefID;
                var unitDef = _gameData.Units.Get(defID);
                if (unitDef?.Sprite == null) continue;
                var atlasId = AtlasDefs.ResolveAtlasName(unitDef.Sprite.AtlasName);
                var spriteData = _atlases[atlasId].GetUnit(unitDef.Sprite.SpriteName);
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
                    var kfs = PickIdleFrames(idleAnim);
                    if (kfs != null && kfs.Count > 0)
                        animData.RefFrameHeight = kfs[0].Frame.Rect.Height;
                }
                _unitAnims[uid] = animData;
            }

            // --- Jump state machine (voluntary jumps: necromancer attack, wolf pounce) ---
            if (_sim.Units[i].JumpPhase != 0)
            {
                if (JumpSystem.TickUnit(dt, _sim.UnitsMut, i, animData.Ctrl, _sim))
                {
                    _unitAnims[uid] = animData;
                    continue;
                }
            }

            // Force out of work anims if interaction was cancelled (WASD override)
            if (_sim.Units[i].CorpseInteractPhase == 0)
            {
                var cur = animData.Ctrl.CurrentState;
                if (cur == AnimState.WorkStart || cur == AnimState.WorkLoop || cur == AnimState.WorkEnd
                    || cur == AnimState.Pickup || cur == AnimState.PutDown
                    || cur == AnimState.ImbueTableStart || cur == AnimState.ImbueTableLoop || cur == AnimState.ImbueTableFinish)
                {
                    animData.Ctrl.ForceState(AnimState.Idle);
                    animData.Ctrl.PlaybackSpeed = 1f; // clear any channel time-stretch
                }
            }

            // --- Corpse interaction state machine ---
            // PlayOnceHold states: ForceState on entry, IsAnimFinished for completion
            if (_sim.Units[i].CorpseInteractPhase != 0)
            {
                byte phase = _sim.Units[i].CorpseInteractPhase;
                const float BaggingDuration = 2.0f;

                // Reanimating a corpse on a craft table uses the ImbueTable
                // animation set instead of the generic Work set. Keyed off the
                // unit's active craft-table index so only table channeling swaps.
                bool imbueTable = _sim.Units[i].CraftTableIdx >= 0;
                AnimState wStart = imbueTable ? AnimState.ImbueTableStart : AnimState.WorkStart;
                AnimState wLoop  = imbueTable ? AnimState.ImbueTableLoop  : AnimState.WorkLoop;
                AnimState wEnd   = imbueTable ? AnimState.ImbueTableFinish : AnimState.WorkEnd;

                // Fit the whole Start+Loop+Finish into the table's ProcessTime: the
                // loop is the flexible middle, and if the natural total exceeds
                // ProcessTime the playback is time-stretched (frame-rate accelerated)
                // to fit, keeping at least one full loop cycle. Computed each frame
                // (cheap) so it tracks ProcessTime edits live.
                if (imbueTable && _envSystem != null
                    && _sim.Units[i].CraftTableIdx < _envSystem.ObjectCount)
                {
                    int tIdx = _sim.Units[i].CraftTableIdx;
                    var tdef = _envSystem.Defs[_envSystem.GetObject(tIdx).DefIndex];
                    float pt = tdef.ProcessTime;
                    float sD = animData.Ctrl.AnimDurationMsFor(AnimState.ImbueTableStart) / 1000f;
                    float lC = animData.Ctrl.AnimDurationMsFor(AnimState.ImbueTableLoop) / 1000f;
                    float fD = animData.Ctrl.AnimDurationMsFor(AnimState.ImbueTableFinish) / 1000f;
                    float baseTotal = sD + lC + fD;
                    float spd = (pt > 0.01f && baseTotal > pt) ? baseTotal / pt : 1f;
                    float budget = baseTotal > pt ? lC / spd : MathF.Max(lC, pt - sD - fD);
                    animData.Ctrl.PlaybackSpeed = spd;
                    _envSystem.GetTableState(tIdx).LoopBudget = budget;
                }

                switch (phase)
                {
                    case 1: // Start (PlayOnceHold)
                        if (animData.Ctrl.CurrentState != wStart)
                            animData.Ctrl.ForceState(wStart);
                        if (animData.Ctrl.IsAnimFinished)
                        {
                            _sim.UnitsMut[i].CorpseInteractPhase = 2;
                            _sim.UnitsMut[i].BaggingTimer = 0f;
                            animData.Ctrl.ForceState(wLoop);
                        }
                        break;

                    case 2: // Loop (Loop — timer driven)
                        if (animData.Ctrl.CurrentState != wLoop)
                            animData.Ctrl.ForceState(wLoop);
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
                                animData.Ctrl.ForceState(wEnd);
                            }
                        }
                        // else: handler controls timer and transitions CorpseInteractPhase
                        break;

                    case 3: // End/Finish (PlayOnceHold)
                        if (animData.Ctrl.CurrentState != wEnd)
                            animData.Ctrl.ForceState(wEnd);
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
                            animData.Ctrl.PlaybackSpeed = 1f; // clear any channel time-stretch
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
                            // Dispatch on PutDownTableIdx: if a table was targeted at F-press
                            // time, load the corpse into its slot and remove the corpse from
                            // the sim. Otherwise, place on ground at LerpStartPos as before.
                            int tableIdx = _sim.Units[i].PutDownTableIdx;
                            int corpseId = _sim.Units[i].CarryingCorpseID;
                            var cc = _sim.FindCorpseByID(corpseId);

                            if (tableIdx >= 0 && _envSystem != null && cc != null
                                && Game.TableSystem.LoadCorpseIntoTable(_envSystem, tableIdx, cc) >= 0)
                            {
                                int ci = _sim.FindCorpseIndexByID(corpseId);
                                if (ci >= 0) _sim.CorpsesMut.RemoveAt(ci);
                                // Auto-open the table menu so the player can pick items
                                // and start crafting without an extra click.
                                int sw = _graphics.PreferredBackBufferWidth;
                                int sh = _graphics.PreferredBackBufferHeight;
                                EnsureInventoryUIsInitialized();
                                _tableMenuUI.OpenForTable(tableIdx, sw, sh, _camera, _renderer);
                            }
                            else if (cc != null)
                            {
                                // Ground drop (or table-load fell through e.g. slot taken).
                                // Land flat at the drop point — zero Z/physics so the
                                // settled draw lands exactly where the put-down draw was.
                                cc.Position = cc.LerpStartPos;
                                cc.Z = 0f;
                                cc.InPhysics = false;
                                cc.DraggedByUnitID = GameConstants.InvalidUnit;
                            }

                            _sim.UnitsMut[i].CarryingCorpseID = -1;
                            _sim.UnitsMut[i].CorpseInteractPhase = 0;
                            _sim.UnitsMut[i].PutDownTableIdx = -1;
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

                // Combat engine overrides: pending attacks get priority 2 override.
                // Attack anim plays at its natural ms timing unless it won't fit in
                // the weapon's cycle (CooldownRounds × RoundDuration), in which case
                // it's compressed to fit.
                if (!_sim.Units[i].PendingAttack.IsNone)
                {
                    var atkState = ResolvePendingAttackAnim(_sim.Units[i].Stats,
                        _sim.Units[i].PendingWeaponIdx, _sim.Units[i].PendingWeaponIsRanged,
                        _sim.Units[i].Archetype);
                    float animDur = animData.Ctrl.GetTotalDurationSeconds(atkState);
                    float cycle = ComputeWeaponCycleSeconds(i, _sim.Units[i].PendingWeaponIdx);
                    float spd = (animDur > cycle && cycle > 0f) ? animDur / cycle : 1f;
                    AnimResolver.SetOverride(_sim.UnitsMut[i], AnimRequest.Combat(atkState, spd));
                }
                else if (_sim.Units[i].InCombat && _sim.Units[i].AttackCooldown > 0f)
                {
                    // Pre-roll: start attack animation early so its effect_time lines up
                    // with the end of the cooldown. Use the FIRST non-pounce weapon's
                    // anim (most units have one melee weapon; wolves have Bite then Pounce
                    // and Bite is the in-melee attack).
                    int preRollWeaponIdx = 0;
                    for (int w = 0; w < _sim.Units[i].Stats.MeleeWeapons.Count; w++)
                    {
                        if (_sim.Units[i].Stats.MeleeWeapons[w].Archetype != Data.WeaponArchetype.Pounce)
                        { preRollWeaponIdx = w; break; }
                    }
                    var preRollState = ResolvePendingAttackAnim(_sim.Units[i].Stats,
                        preRollWeaponIdx, false, _sim.Units[i].Archetype);
                    float cooldownRemaining = _sim.Units[i].AttackCooldown;
                    float effectTime = animData.Ctrl.GetEffectTimeSeconds(preRollState);
                    float animDur = animData.Ctrl.GetTotalDurationSeconds(preRollState);
                    float cycle = ComputeWeaponCycleSeconds(i, preRollWeaponIdx);
                    float spd = (animDur > cycle && cycle > 0f) ? animDur / cycle : 1f;
                    float preRollTime = effectTime > 0f ? effectTime / spd : 0f;
                    if (preRollTime > 0f && cooldownRemaining <= preRollTime)
                        AnimResolver.SetOverride(_sim.UnitsMut[i], AnimRequest.Combat(preRollState, spd));
                }

                // Cancel a stale attack swing that would otherwise "bleed" into a chase:
                // once the unit is actually moving and no longer attacking (no pending
                // swing, post-attack lockout elapsed, not in melee), drop the one-shot
                // attack override so locomotion shows instead of the swing sliding along.
                // The swing still plays fully while the unit is planted (PostAttackTimer
                // / InCombat keep Velocity at 0); this only fires once it starts moving.
                {
                    var ovNow = _sim.Units[i].OverrideAnim;
                    bool ovIsAttack = ovNow.IsActive &&
                        (ovNow.State == AnimState.Attack1 || ovNow.State == AnimState.Attack2 || ovNow.State == AnimState.Attack3);
                    bool notAttacking = _sim.Units[i].PendingAttack.IsNone
                        && _sim.Units[i].PostAttackTimer <= 0f
                        && !_sim.Units[i].InCombat;
                    if (ovIsAttack && notAttacking && _sim.Units[i].Velocity.LengthSq() > 1.0f)
                        AnimResolver.ClearOverride(_sim.UnitsMut[i]);
                }

                // Reverse walk playback
                float facingRad2 = _sim.Units[i].FacingAngle * MathF.PI / 180f;
                var facingDir2 = new Vec2(MathF.Cos(facingRad2), MathF.Sin(facingRad2));
                var vel2 = _sim.Units[i].Velocity;
                bool backward2 = vel2.LengthSq() > 0.1f && vel2.Normalized().Dot(facingDir2) < -0.3f;
                animData.Ctrl.SetReversePlayback(backward2);

                AnimResolver.Resolve(_sim.UnitsMut[i], animData.Ctrl, dt);

                // Locomotion playback scaling — applied after Resolve so we know the
                // final state the controller landed on. Re-applied every frame because
                // AnimController.SwitchState resets _playbackSpeed to 1.0 on transitions.
                // Only overwrite PlaybackSpeed for actual locomotion states; for attack /
                // spell / jump states, AnimResolver's compression-speed from the winning
                // override must stick through ctrl.Update.
                var curState = animData.Ctrl.CurrentState;
                bool isLocoState = curState == AnimState.Walk || curState == AnimState.Jog
                    || curState == AnimState.Run || curState == AnimState.Carry;
                if (isLocoState)
                {
                    float locoSpeed = _sim.Units[i].Velocity.Length();
                    var locoDef = _gameData.Units.Get(_sim.Units[i].UnitDefID);
                    var locoProfile = locoDef != null
                        ? LocomotionProfile.FromUnit(locoDef)
                        : LocomotionProfile.FromBaseSpeed(_sim.Units[i].Stats.CombatSpeed);
                    animData.Ctrl.PlaybackSpeed = LocomotionScaling.ComputeLocomotionPlayback(
                        animData.Ctrl, locoProfile, curState, locoSpeed);
                }
                animData.Ctrl.Update(dt);

                // Cosmetic attack lunge — writes Unit.RenderOffset based on attack anim
                // progress. All draw sites read Position + RenderOffset via unit.RenderPos.
                LungeSystem.Update(_sim.UnitsMut[i], animData.Ctrl);

                // DEBUG-only invariant checks. No-op in production; fires at the exact
                // frame a rule is violated (easier to diagnose than "the anim got weird
                // 10 frames ago").
                AnimInvariants.Check(_sim.Units[i], animData.Ctrl);
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
            // Flinch driven by HitReactTimer (set by DamageSystem.ApplyHitReactAnim,
            // which already skipped fleeing / prone / refractory units, and is never
            // set for poison) — not the raw HitReacting/BlockReacting flags. Keeps the
            // legacy render in lockstep with the archetype OverrideAnim path.
            else if (_sim.Units[i].HitReactTimer > 0f)
                targetState = AnimState.BlockReact;
            else if (_sim.Units[i].PostAttackTimer > 0f)
                targetState = AnimState.Block;
            else if (_sim.Units[i].InCombat && _sim.Units[i].AttackCooldown > 0f)
            {
                float cooldownRemaining = _sim.Units[i].AttackCooldown;
                float effectTime = animData.Ctrl.GetEffectTimeSeconds(AnimState.Attack1);
                float animDur = animData.Ctrl.GetTotalDurationSeconds(AnimState.Attack1);
                float cycle = ComputeWeaponCycleSeconds(i, 0);
                float speed = (animDur > cycle && cycle > 0f) ? animDur / cycle : 1f;
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

            // Locomotion playback scaling (Walk/Jog/Run/Carry) — keeps foot-cycle
            // frequency matched to actual velocity so anims don't skate.
            {
                float locoSpeed = _sim.Units[i].Velocity.Length();
                var locoDef = _gameData.Units.Get(_sim.Units[i].UnitDefID);
                var locoProfile = locoDef != null
                    ? LocomotionProfile.FromUnit(locoDef)
                    : LocomotionProfile.FromBaseSpeed(_sim.Units[i].Stats.CombatSpeed);
                animData.Ctrl.PlaybackSpeed = LocomotionScaling.ComputeLocomotionPlayback(
                    animData.Ctrl, locoProfile, targetState, locoSpeed);
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

            // Action-moment handling via edge flags.
            //
            // JustHitEffectFrame fires on the single tick where _animTime crosses the
            // current state's effect_time_ms (or 50% in tick-based fallback). Unlike
            // the old ConsumeActionMoment model, reading this flag is non-destructive:
            // every interested system inspects the same flag and decides whether it's
            // the intended consumer. Pre-roll can't "steal" an action moment from the
            // real attack anymore — the pre-roll simply has no queued consumer when
            // the flag fires.
            if (animData.Ctrl.JustHitEffectFrame)
            {
                bool hasPendingCast = _pendingCastAnim != null && i == FindNecromancer()
                    && animData.Ctrl.CurrentState == AnimState.Spell1;
                bool hasPendingAttack = !_sim.Units[i].PendingAttack.IsNone;

                if (hasPendingCast)
                {
                    var pca = _pendingCastAnim.Value;
                    var spell = _gameData.Spells.Get(pca.SpellID);
                    if (spell != null)
                        ExecuteSpellEffect(spell, i, pca.Target, pca.Slot);
                    _pendingCastAnim = null;
                }
                else if (hasPendingAttack)
                {
                    _sim.ResolvePendingAttack(i);
                }
            }

            _unitAnims[uid] = animData;
        }

        // Channeled reanimation casts run their own Start→Loop→Finish machine.
        if (_pendingCastAnim != null && IsChanneledCast(_pendingCastAnim.Value.CastAnim))
        {
            UpdateChanneledCast(dt);
        }
        // Spell1 is PlayOnceTransition — it switches to Idle when done, so we can't check
        // CurrentState == Spell1 after the fact. Instead, detect that the necromancer left
        // Spell1 (no longer in Spell1) while _pendingCastAnim is still set.
        else if (_pendingCastAnim != null)
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

        // Casting glow while channeling a reanimation at the necro table.
        UpdateTableChannelBuff();

        // --autostart headless diagnostic: world is loaded, exit.
        if (_autostartExitPending) Exit();

        // Update EffectSpawnPos2D / EffectSpawnHeight from weapon tip data
        UpdateEffectSpawnPositions();

        _effectManager.Update(dt);
        _buffVisuals.Update(dt, _sim.Units, _gameData.Buffs, _gameTime);
        _foragables.Update(dt);
        UpdateSkillLearnToasts(dt);
        SyncCorruptionSettings();

        // Death Fog Consumption passive: while the necromancer stands in any
        // non-zero death-fog density, add +2 to their mana regen this tick.
        // BonusManaRegen is consumed by the next Simulation.Update.
        var necroState = _sim.NecroState;
        necroState.BonusManaRegen = 0f;
        if (_skillBookState.HasPassive("death_fog_consumption") && _sim.NecromancerIndex >= 0)
        {
            var necroPos = _sim.Units[_sim.NecromancerIndex].Position;
            if (_deathFog.Sample(necroPos.X, necroPos.Y) > 0.01f)
                necroState.BonusManaRegen = 2f;
        }

        _deathFog.Update(_envSystem, dt, _groundSystem);

        // Advance per-vertex visual fades for newly corrupted grass vertices.
        // Internally rate-limits texture re-uploads so we don't push pixels
        // every frame just to bump fade values by ~1/60.
        _groundSystem.AdvanceCorruptionFades(dt);

        // Advance per-cell grass-tuft tint fades (10s lerp from default to
        // corrupted tint, started when a ground vertex under the cell flips).
        _grassRenderer.AdvanceFades(dt);

        // Ground corruption rolls inside DeathFogSystem may have flipped vertices —
        // push the dirty rect into the existing vertex map texture (partial
        // SetData) instead of disposing and re-allocating a 67 MB texture.
        if (_groundSystem.CorruptionDirty)
        {
            bool partialOk = _groundVertexMapTex != null
                && _groundSystem.UploadDirtyRect(_groundVertexMapTex);
            if (!partialOk)
            {
                _groundVertexMapTex?.Dispose();
                _groundVertexMapTex = _groundSystem.CreateVertexMapTexture(GraphicsDevice);
            }
        }
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

            if (unitDef != null && unitDef.Sprite != null && animData.RefFrameHeight > 0f)
            {
                string animName = AnimController.StateToAnimName(animData.Ctrl.CurrentState);
                int spriteAngle = animData.Ctrl.ResolveAngle(_sim.Units[i].FacingAngle, out bool flipX);
                int frameIdx = animData.Ctrl.GetCurrentFrameIndex(_sim.Units[i].FacingAngle);

                AnimationMeta? meta = null;
                _animMeta.TryGetValue(AnimMetaLoader.MetaKey(unitDef.Sprite.SpriteName, animName), out meta);

                if (WeaponPointResolver.TryResolve(unitDef, meta, animName, spriteAngle, frameIdx,
                        animData.RefFrameHeight, out var wpf, out _))
                {
                    bool tipSet = wpf.Tip.X != 0f || wpf.Tip.Y != 0f;
                    if (tipSet)
                    {
                        float flipMul = flipX ? -1f : 1f;
                        float worldH = (unitDef.SpriteWorldHeight > 0 ? unitDef.SpriteWorldHeight : 1.8f)
                                       * _sim.Units[i].SpriteScale;
                        float worldScale = worldH / animData.RefFrameHeight;

                        // Spawn position follows the visible weapon tip — if the
                        // unit is lunged, the projectile spawns from where the
                        // weapon visually is.
                        var spawnBase = _sim.Units[i].RenderPos;
                        float tipDx = wpf.Tip.X * worldScale * flipMul;
                        mu[i].EffectSpawnPos2D = new Vec2(spawnBase.X + tipDx, spawnBase.Y);

                        float unitHeight = _sim.Units[i].Z;
                        mu[i].EffectSpawnHeight = unitHeight - wpf.Tip.Y * worldScale;

                        foundWeaponTip = true;
                    }
                }
            }

            if (!foundWeaponTip)
            {
                // Fallback: offset in facing direction. Use RenderPos so spawn follows lunge.
                float facingRad = _sim.Units[i].FacingAngle * MathF.PI / 180f;
                float radius = _sim.Units[i].Radius;
                mu[i].EffectSpawnPos2D = _sim.Units[i].RenderPos
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
        if (unitDef.Sprite == null || animData.RefFrameHeight <= 0f) return result;

        string animName = AnimController.StateToAnimName(animData.Ctrl.CurrentState);
        int spriteAngle = animData.Ctrl.ResolveAngle(_sim.Units[unitIdx].FacingAngle, out bool flipX);
        int frameIdx = animData.Ctrl.GetCurrentFrameIndex(_sim.Units[unitIdx].FacingAngle);

        AnimationMeta? meta = null;
        _animMeta.TryGetValue(AnimMetaLoader.MetaKey(unitDef.Sprite.SpriteName, animName), out meta);

        if (!WeaponPointResolver.TryResolve(unitDef, meta, animName, spriteAngle, frameIdx,
                animData.RefFrameHeight, out var wpf, out _)) return result;

        bool hiltSet = wpf.Hilt.X != 0f || wpf.Hilt.Y != 0f;
        bool tipSet = wpf.Tip.X != 0f || wpf.Tip.Y != 0f;
        if (!hiltSet && !tipSet) return result;

        float flipMul = flipX ? -1f : 1f;
        float worldH = (unitDef.SpriteWorldHeight > 0 ? unitDef.SpriteWorldHeight : 1.8f)
                       * _sim.Units[unitIdx].SpriteScale;
        float worldScale = worldH / animData.RefFrameHeight;
        float unitHeight = _sim.Units[unitIdx].Z;

        // Weapon attach points follow the sprite's cosmetic offset — so the weapon
        // lunges with the unit on attack. Gameplay never reads these.
        var unitRender = _sim.Units[unitIdx].RenderPos;
        result.HiltWorld = new Vec2(
            unitRender.X + wpf.Hilt.X * worldScale * flipMul,
            unitRender.Y);
        result.HiltHeight = unitHeight - wpf.Hilt.Y * worldScale;
        result.HiltBehind = wpf.Hilt.Behind;

        result.TipWorld = new Vec2(
            unitRender.X + wpf.Tip.X * worldScale * flipMul,
            unitRender.Y);
        result.TipHeight = unitHeight - wpf.Tip.Y * worldScale;
        result.TipBehind = wpf.Tip.Behind;

        result.Valid = true;
        return result;
    }

    /// <summary>
    /// Scenario debug overlay: draw a green dot at each unit's resolved weapon
    /// hilt and a red dot at the tip, with a magenta line connecting them.
    /// Use this to visually validate that the WeaponPointResolver lines up
    /// with the visible weapon in the rendered sprite.
    /// </summary>
    private void DrawWeaponAttachDebug()
    {
        _debugDraw.EnsurePixel(GraphicsDevice);
        for (int i = 0; i < _sim.Units.Count; i++)
        {
            if (!_sim.Units[i].Alive) continue;
            uint uid = _sim.Units[i].Id;
            if (!_unitAnims.TryGetValue(uid, out var animData)) continue;
            var unitDef = _gameData.Units.Get(_sim.Units[i].UnitDefID);
            if (unitDef == null) continue;

            var attach = ComputeWeaponAttach(i, unitDef, animData);
            if (!attach.Valid) continue;

            var hiltSp = _renderer.WorldToScreenPx(attach.HiltWorld, attach.HiltHeight * _camera.Zoom, _camera);
            var tipSp  = _renderer.WorldToScreenPx(attach.TipWorld,  attach.TipHeight  * _camera.Zoom, _camera);

            // Magenta line / lime hilt / red tip — saturated channels that
            // don't collide with the sprite's natural palette so the dots
            // stay visible against any pose.
            _debugDraw.DrawLine(_spriteBatch, hiltSp, tipSp, new Color(255, 0, 255, 220));
            DrawDebugDot(hiltSp, new Color(60, 255, 60));
            DrawDebugDot(tipSp,  new Color(255, 40, 40));
        }
    }

    private void DrawDebugDot(Vector2 pos, Color color)
    {
        int r = 3;
        for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
                if (dx * dx + dy * dy <= r * r)
                    _spriteBatch.Draw(_pixel, new Rectangle((int)pos.X + dx, (int)pos.Y + dy, 1, 1), color);
    }

    protected override void Draw(GameTime gameTime)
    {
        _drawStopwatch.Restart();
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

        // Update fog of war render targets (before bloom, since this changes render targets)
        {
            bool fogActive = (FogOfWarMode)_gameData.Settings.FogOfWar.Mode != FogOfWarMode.Off;
            bool editorOpen = _menuState != MenuState.None && _menuState != MenuState.MainMenu;
            if (fogActive && !editorOpen)
                _fogOfWar.Update(_spriteBatch, _sim.Units, _gameData.Settings.FogOfWar, _rawDt);
            else
                // Update isn't running this frame, but IsVisible (which culls enemy
                // sprites/shadows/projectiles) keys off the fog system's cached mode.
                // Keep it in sync with the live setting so turning fog Off immediately
                // reveals all enemies instead of leaving them culled against stale fog.
                _fogOfWar.SyncMode(_gameData.Settings.FogOfWar);
        }

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

        // Compute ambient color from weather (brightness + tint) for lit sprite tinting
        _ambientColor = _weatherRenderer.GetAmbientColor();

        // AlphaBlend with premultiplied-alpha textures (loaded via TextureUtil.LoadPremultiplied)
        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp);

        if (!useBloom)
            GraphicsDevice.Clear(clearColor);

        // --- Ground ---
        if (_activeScenario == null || _activeScenario.WantsGround)
        {
            DrawGround();
            // Perf scenarios can ask Game1 to redraw the ground N extra times to
            // stress the GPU past the 16.67ms vsync budget. The redraws happen
            // BEFORE the rest of the scene so they overwrite each other; only the
            // last write contributes visually.
            if (_activeScenario != null && _activeScenario.ExtraGroundDrawsPerFrame > 0
                && _groundEffect != null && _groundVertexMapTex != null && _groundSystem.TypeCount > 0)
            {
                int worldW2 = _groundSystem.WorldW > 0 ? _groundSystem.WorldW : WorldSize;
                int worldH2 = _groundSystem.WorldH > 0 ? _groundSystem.WorldH : WorldSize;
                for (int e = 0; e < _activeScenario.ExtraGroundDrawsPerFrame; e++)
                    DrawGroundShader(worldW2, worldH2);
            }
        }

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
            // Show progress bar from the moment the glyph is placed (even at 0%), not only
            // once construction has begun — so players can see "trap placed, awaiting build".
            if (g.State == GameSystems.GlyphState.Blueprint && g.BuildProgress < 1f && g.Alive)
            {
                var gsp = _renderer.WorldToScreen(g.Position, 0f, _camera);
                DrawBuildProgressBar(gsp, g.BuildProgress, g.Radius);
            }
        }

        // --- Walls ---
        DrawWalls();

        // --- Wading sink offsets ---
        // Compute Unit.WadingSinkOffsetY for every unit before any visual
        // pass reads it. Must run before _shadowRenderer.Draw (which reads
        // RenderPos to position shadows) and before DrawUnitsAndObjects
        // (which reads RenderPos for sprites, buffs, damage numbers, etc.).
        UpdateWadingSinkOffsets();

        // --- Shadows ---
        // Grass is no longer drawn here — tufts are merged into the unit Y-sort
        // inside DrawUnitsAndObjects so they can render in front of / behind
        // units based on world Y.
        _shadowRenderer.Draw(GraphicsDevice, _spriteBatch, _glowTex, _camera, _renderer, _sim, _gameData, _unitAnims, _atlases, _envSystem, _fogOfWar, _groundSystem, _deathFog);

        // --- Corpses ---
        DrawCorpses();

        // --- Units + Environment objects + Poison cloud puffs (merged Y-sort for correct depth) ---
        DrawUnitsAndObjects();

        // --- Projectiles ---
        DrawProjectiles();
        DrawSoulOrbs();
        // (Death-fog puffs render inside DrawUnitsAndObjects' merged Y-sort
        // pass so they correctly occlude / are occluded by units & env objects
        // based on relative ground Y — see DepthItemType.DeathFogPuff.)

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
        // Lightning bolts and drains use HDR vertex encoding — draw in this HDR batch
        _lightningRenderer.SetGameTime(_gameTime);
        _lightningRenderer.Draw();
        _spriteBatch.End();

        // --- Additive blend pass (energy columns, debug shapes) ---
        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.Additive, SamplerState.LinearClamp);
        _glyphRenderer.DrawEnergyColumns(_sim.MagicGlyphs);

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
        _foragables.Draw();
        DrawDamageNumbers();
        _spriteBatch.End();

        // End bloom and composite
        if (useBloom)
            _bloom.EndScene(GraphicsDevice, _spriteBatch, bloomSettings);

        // --- Fog of war overlay (after bloom, before HUD) ---
        // Skip entirely when Off or when any editor is open
        {
            bool fogActive = (FogOfWarMode)_gameData.Settings.FogOfWar.Mode != FogOfWarMode.Off;
            bool editorOpen = _menuState != MenuState.None && _menuState != MenuState.MainMenu;
            if (fogActive && !editorOpen)
            {
                // Draw fog overlay (RTs already updated before bloom pass)
                _fogOfWar.Draw(_spriteBatch, _camera, _renderer, screenW, screenH, _gameData.Settings.FogOfWar);
            }
        }

        // --- Collision debug overlay (after world, before HUD) ---
        if (_collisionDebugMode != CollisionDebugMode.Off)
        {
            _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
            _debugDraw.DrawCollisionDebug(_spriteBatch, GraphicsDevice, _sim, _camera, _renderer,
                _collisionDebugMode, _envSystem, _sim.Pathfinder);
            _spriteBatch.End();
        }

        // --- Weapon attach debug overlay (scenario opt-in) ---
        if (_activeScenario != null && _activeScenario.ShowWeaponAttachDebug)
        {
            _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
            DrawWeaponAttachDebug();
            _spriteBatch.End();
        }

        // --- Death-fog debug overlay (F5) ---
        if (_deathFog.DebugVisible)
        {
            _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
            _deathFog.DrawDebug(_spriteBatch, _pixel, _renderer, _camera);

            // Per-corruptable-object stress label: "stress/threshold" when stress > 0,
            // or "DEAD" once corrupted. Skip clean trees to reduce overlay clutter.
            float threshold = _deathFog.CorruptionThreshold;
            int corrW = GraphicsDevice.Viewport.Width;
            int corrH = GraphicsDevice.Viewport.Height;
            for (int oi = 0; oi < _envSystem.ObjectCount; oi++)
            {
                var obj = _envSystem.GetObject(oi);
                var def = _envSystem.GetDef(obj.DefIndex);
                if (!def.IsCorruptable) continue;
                var rt = _envSystem.GetObjectRuntime(oi);
                if (!rt.Alive) continue;
                bool dying = rt.CorruptionTime > 0f && !rt.Corrupted;
                if (!rt.Corrupted && !dying && rt.CorruptionStress <= 0.01f) continue;

                var sp = _renderer.WorldToScreen(new Vec2(obj.X, obj.Y), 0f, _camera);
                if (sp.X < -50 || sp.X > corrW + 50 || sp.Y < -50 || sp.Y > corrH + 50) continue;

                string label;
                Color labelColor;
                if (rt.Corrupted)
                {
                    label = "DEAD";
                    labelColor = new Color(255, 100, 100);
                }
                else if (dying)
                {
                    float dur = _deathFog.CorruptionTransitionDuration;
                    label = $"DYING {rt.CorruptionTime:F1}/{dur:F0}s";
                    labelColor = new Color(255, 160, 80);
                }
                else
                {
                    label = $"{rt.CorruptionStress:F1}/{threshold:F0}";
                    labelColor = new Color(255, 220, 120);
                }

                if (_smallFont != null)
                {
                    var size = _smallFont.MeasureString(label);
                    var pos = new Vector2((int)(sp.X - size.X * 0.5f), (int)(sp.Y - 16));
                    _spriteBatch.Draw(_pixel,
                        new Rectangle((int)pos.X - 2, (int)pos.Y - 1, (int)size.X + 4, (int)size.Y + 2),
                        new Color(0, 0, 0, 180));
                    _spriteBatch.DrawString(_smallFont, label, pos, labelColor);
                }
            }

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

        // --- Wind debug overlay (F6) ---
        if (_windDebug)
        {
            try
            {
                _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
                DrawWindDebug(screenW, screenH);
                _spriteBatch.End();
            }
            catch (Exception ex)
            {
                Core.DebugLog.Log("error", $"Wind debug crash: {ex}");
                _windDebug = false;
                try { _spriteBatch.End(); } catch { }
            }
        }

        // --- Alt shows object names (+ animation debug for animated objects) ---
        if ((_menuState == MenuState.MapEditor || _menuState == MenuState.None) && _input.IsKeyDown(Keys.LeftAlt))
        {
            _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
            for (int oi = 0; oi < _envSystem.ObjectCount; oi++)
            {
                if (!_envSystem.IsObjectVisible(oi)) continue;
                var obj = _envSystem.GetObject(oi);
                var def = _envSystem.GetDef(obj.DefIndex);
                var sp = _renderer.WorldToScreen(new Vec2(obj.X, obj.Y), 0f, _camera);
                // Only draw if on screen
                if (sp.X > -100 && sp.X < screenW + 100 && sp.Y > -100 && sp.Y < screenH + 100)
                {
                    string label = !string.IsNullOrEmpty(def.Name) ? def.Name : def.Id;
                    // Animated objects: append frame info after the name
                    if (def.IsAnimated && def.AnimTotalFrames > 1)
                    {
                        var rt = _envSystem.GetObjectRuntime(oi);
                        int frame = Math.Clamp((int)rt.AnimTime, 0, def.AnimTotalFrames - 1);
                        string dir = rt.AnimReversed ? "<" : ">";
                        label = $"{label}  F{frame}/{def.AnimTotalFrames - 1} {dir}";
                    }
                    if (!string.IsNullOrEmpty(label))
                    {
                        var textSize = _smallFont != null ? _smallFont.MeasureString(label) : new Vector2(label.Length * 6, 12);
                        var textPos = new Vector2((int)(sp.X - textSize.X * 0.5f), (int)(sp.Y + 4));
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
        if (showUI)
            DrawAggressionBar(screenW, screenH);

        // Inventory UI (widget-based, drawn over HUD)
        if (showUI)
        {
            _inventoryUI.Draw(screenW, screenH);
            _tableMenuUI.Draw();
        }

        // Character stats panel (Tab)
        if (showUI)
            _characterStatsUI.Draw(screenW, screenH, _sim, _gameData.Buffs, ref _spellBarState, _input, _gameData, _skillBookState);

        // Unit info sheet (U = player character, O = inspect under cursor)
        if (showUI)
            _unitInfoPanel.Draw(screenW, screenH, _sim);

        // Spell grimoire (J)
        if (showUI)
            _grimoireOverlay.Draw(screenW, screenH);

        // Skill book panel (K) — tabbed Potions/Necromancy/Magic/Metamorphosis trees.
        if (showUI)
            _skillBookOverlay.Draw(screenW, screenH);

        // Bottom-right "Recipe Learned" toasts — drawn even when the panel is closed.
        if (showUI)
            DrawSkillLearnToasts(screenW, screenH);

        // Scenario custom UI hook — for shader-test scenarios that draw raw
        // geometry without a real panel.
        if (_activeScenario?.CustomUIDraw != null)
            _activeScenario.CustomUIDraw(_spriteBatch, screenW, screenH);

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

        // Immediate-mode editor UI reads click edges during Draw, but Update can run
        // several times per Draw under fixed-timestep catch-up (slow frames), which
        // would collapse the one-frame press edge before Draw sees it and drop most
        // clicks. Snapshot the mouse once per Draw so edges are measured against the
        // previous Draw, not the previous Update. _editorUi backs Settings + the
        // unit/spell/map/item editors; _uiEditor is its own EditorBase instance.
        if (_menuState == MenuState.Settings || _menuState == MenuState.UnitEditor
            || _menuState == MenuState.SpellEditor || _menuState == MenuState.MapEditor
            || _menuState == MenuState.ItemEditor)
            _editorUi.EndDrawFrame();
        else if (_menuState == MenuState.UIEditor)
            _uiEditor.EndDrawFrame();

        if (_font != null && showUI && _showPerfReadout)
        {
            double frameMs = _rawDt > 0 ? _rawDt * 1000.0 : 0.0;
            double simMs = _sim.LastTickMs;
            // gpuish: total frame minus CPU portions ≈ what the GPU + vsync is
            // costing us. Useful as a quick "are we GPU bound?" gauge.
            double gpuish = System.Math.Max(0, frameMs - _drawMsAvg - simMs);
            string dbg = $"Zoom:{_camera.Zoom:F0} Pos:({_camera.Position.X:F0},{_camera.Position.Y:F0}) Speed:{_timeScale:F1}x FPS:{(_rawDt > 0 ? 1f / _rawDt : 0):F0} | frame:{frameMs:F1}ms sim:{simMs:F2} draw:{_drawMsAvg:F2} ground:{_groundMsAvg:F2} present:{_gpuPresentMsAvg:F2} gpuish:{gpuish:F2}";
            DrawText(_smallFont, dbg, new Vector2(10, screenH - 18), new Color(120, 120, 120));
        }

        // Show collision debug mode label when active
        if (_collisionDebugMode != CollisionDebugMode.Off && _smallFont != null)
        {
            string label = $"[F8] Collision Debug: {DebugDraw.GetModeLabel(_collisionDebugMode)}";
            DrawText(_smallFont, label, new Vector2(10, screenH - 36), new Color(255, 200, 80));
        }
        if (_waterDebug && _smallFont != null)
        {
            DrawText(_smallFont, "[F2] Water Debug", new Vector2(10, screenH - 54), new Color(120, 220, 255));
        }

        _spriteBatch.End();

        _drawStopwatch.Stop();
        // EMA so the HUD doesn't jitter frame-to-frame.
        const double EmaAlpha = 0.1;
        double drawMs = _drawStopwatch.Elapsed.TotalMilliseconds;
        _drawMsAvg = _drawMsAvg * (1.0 - EmaAlpha) + drawMs * EmaAlpha;

        // Feed perf scenarios raw per-frame samples (EMA hides bench detail).
        if (_activeScenario is Scenario.Scenarios.PerfWaterScenario perf)
        {
            perf.LastDrawMs = drawMs;
            perf.LastFrameMs = _rawDt * 1000.0;
        }

        // Handle deferred screenshots from scenarios BEFORE the present so
        // GetBackBufferData reads the just-rendered frame. (This used to
        // sit after `return;` below, which made it dead code and silently
        // disabled the screenshot path for scenarios that didn't directly
        // call TakeScreenshot.)
        if (_activeScenario?.DeferredScreenshot != null)
        {
            ScenarioScreenshot.TakeScreenshot(GraphicsDevice, _activeScenario.DeferredScreenshot);
            _activeScenario.DeferredScreenshot = null;
        }

        // Time the Present()/blit done by base.Draw — anything that doesn't
        // overlap with CPU work shows up here. Present blocks until the GPU
        // can accept the frame, so this approximates GPU+vsync wait time.
        var presentSw = System.Diagnostics.Stopwatch.StartNew();
        base.Draw(gameTime);
        presentSw.Stop();
        _gpuPresentMsAvg = _gpuPresentMsAvg * (1.0 - EmaAlpha)
                         + presentSw.Elapsed.TotalMilliseconds * EmaAlpha;
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
        _groundDrawStopwatch.Restart();
        // End the current SpriteBatch and start a new one with the ground shader
        _spriteBatch.End();

        // Set shader parameters
        _groundEffect!.Parameters["AmbientColor"]?.SetValue(new Vector3(_ambientColor.R / 255f, _ambientColor.G / 255f, _ambientColor.B / 255f));
        _groundEffect.Parameters["CameraPos"]?.SetValue(new Vector2(_camera.Position.X, _camera.Position.Y));
        _groundEffect.Parameters["Zoom"]?.SetValue(_camera.Zoom);
        _groundEffect.Parameters["YRatio"]?.SetValue(_camera.YRatio);
        _groundEffect.Parameters["ScreenSize"]?.SetValue(new Vector2(_renderer.ScreenW, _renderer.ScreenH));
        _groundEffect.Parameters["WorldSize"]?.SetValue(new Vector2(_groundSystem.VertexW, _groundSystem.VertexH));
        _groundEffect.Parameters["TypeWarpStrength"]?.SetValue(_groundSystem.TypeWarpStrength);
        _groundEffect.Parameters["UvWarpAmp"]?.SetValue(_groundSystem.UvWarpAmp);
        _groundEffect.Parameters["UvWarpFreq"]?.SetValue(_groundSystem.UvWarpFreq);
        _groundEffect.Parameters["Time"]?.SetValue(_gameTime);

        // Per-type uniforms: tint (defaults white) and water-animation flag.
        // Shader treats array slots 0..7; unused slots are harmless defaults.
        // Per-ground-type uniform arrays. Indexed by ground-type id (0..31)
        // matching the bottom 5 bits of the tilemap byte; the top 3 bits hold
        // the texture-slot id (0..7) which drives the shader cascade. The
        // tint/iswater arrays must match the shader's array length (32 in
        // GroundShader.fx).
        const int MaxGroundTypes = 16;
        const int MaxTextureSlots = 8;
        var tintArr = new Vector4[MaxGroundTypes];
        var waterArr = new float[MaxGroundTypes];
        for (int i = 0; i < MaxGroundTypes; i++) { tintArr[i] = Vector4.One; waterArr[i] = 0f; }
        int typeCap = Math.Min(_groundSystem.TypeCount, MaxGroundTypes);
        for (int i = 0; i < typeCap; i++)
        {
            var def = _groundSystem.GetTypeDef(i);
            tintArr[i] = def.TintColor.ToVector4();
            waterArr[i] = (def.MovementTerrain == Necroking.World.TerrainType.ShallowWater
                        || def.MovementTerrain == Necroking.World.TerrainType.DeepWater) ? 1f : 0f;
        }
        _groundEffect.Parameters["TintColors"]?.SetValue(tintArr);
        _groundEffect.Parameters["IsWaterType"]?.SetValue(waterArr);

        // Bind unique ground textures via Effect.Parameters (named texture params, not register slots).
        // Shader cascade supports MaxTextureSlots unique texture slots; types past those reuse slot 0 fallback.
        string[] texParamNames = {
            "GroundTexture0", "GroundTexture1", "GroundTexture2", "GroundTexture3",
            "GroundTexture4", "GroundTexture5", "GroundTexture6", "GroundTexture7",
        };
        int slotCap = Math.Min(_groundSystem.UniqueTextureCount, Math.Min(texParamNames.Length, MaxTextureSlots));
        for (int i = 0; i < slotCap; i++)
        {
            var tex = _groundSystem.GetUniqueTexture(i);
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

        _groundDrawStopwatch.Stop();
        double groundDrawMs = _groundDrawStopwatch.Elapsed.TotalMilliseconds;
        const double GroundEmaAlpha = 0.1;
        _groundMsAvg = _groundMsAvg * (1.0 - GroundEmaAlpha) + groundDrawMs * GroundEmaAlpha;

        if (_activeScenario is Scenario.Scenarios.PerfWaterScenario perf)
            perf.LastGroundMs = groundDrawMs;
    }


    private void DrawRoads()
    {
        var roads = _roadSystem.Roads;
        var junctions = _roadSystem.Junctions;
        if (roads.Count == 0 && junctions.Count == 0) return;

        var roadColor = MultiplyColor(new Color(100, 90, 80), _ambientColor);

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
                var wallColor = MultiplyColor(def.Color, _ambientColor);
                _spriteBatch.Draw(_pixel, new Vector2(sp.X, sp.Y - wallH + tileH), null,
                    wallColor, 0f, Vector2.Zero,
                    new Vector2(tileW + 0.5f, wallH), SpriteEffects.None, 0f);

                // Draw a darker top edge for depth effect
                var darkColor = new Color(
                    (byte)(wallColor.R * 0.6f),
                    (byte)(wallColor.G * 0.6f),
                    (byte)(wallColor.B * 0.6f),
                    wallColor.A);
                _spriteBatch.Draw(_pixel, new Vector2(sp.X, sp.Y - wallH + tileH), null,
                    darkColor, 0f, Vector2.Zero,
                    new Vector2(tileW + 0.5f, 2f), SpriteEffects.None, 0f);
            }
        }
    }

    /// <summary>Keep the modal stack in sync with <see cref="_menuState"/>:
    /// push the matching top-level editor layer when its state becomes
    /// active, pop it when the state changes away. Called every frame
    /// just before <see cref="Necroking.UI.PopupManager.RouteInput"/> so
    /// the freshly-pushed layer participates in this frame's ESC / click
    /// routing immediately. The layer's panel rect is the full viewport
    /// since editors paint full-screen.</summary>
    private void ReconcileTopLevelEditorLayers()
    {
        int sw = GraphicsDevice.Viewport.Width;
        int sh = GraphicsDevice.Viewport.Height;
        var fullScreen = new Rectangle(0, 0, sw, sh);

        Sync(_unitEditorLayer,  _menuState == MenuState.UnitEditor);
        Sync(_spellEditorLayer, _menuState == MenuState.SpellEditor);
        Sync(_mapEditorLayer,   _menuState == MenuState.MapEditor);
        Sync(_uiEditorLayer,    _menuState == MenuState.UIEditor);
        Sync(_itemEditorLayer,  _menuState == MenuState.ItemEditor);
        Sync(_settingsLayer,    _menuState == MenuState.Settings);
        Sync(_pauseMenuLayer,   _menuState == MenuState.PauseMenu);

        void Sync(Necroking.UI.ActionModalLayer layer, bool open)
        {
            layer.Panel = fullScreen;
            bool onStack = _popups.Contains(layer);
            if (open && !onStack) _popups.Push(layer);
            else if (!open && onStack) _popups.Pop(layer);
        }
    }

    /// <summary>Pre-draw pass: write Unit.WadingSinkOffsetY for every
    /// alive unit based on its current waterness and per-unit (or default)
    /// sink magnitude. Runs before shadow / sprite / buff passes so they
    /// all see the consistent sunken RenderPos. Cheap: per-unit it's a
    /// single waterness sample + scale.</summary>
    private void UpdateWadingSinkOffsets()
    {
        if (_groundSystem == null) return;
        for (int i = 0; i < _sim.Units.Count; i++)
        {
            if (!_sim.Units[i].Alive)
            {
                _sim.UnitsMut[i].WadingSinkOffsetY = 0f;
                continue;
            }
            var unitDef = _gameData.Units.Get(_sim.Units[i].UnitDefID);
            // Negative WadingSinkWorld = explicit "no sink" opt-out.
            float maxSink = unitDef != null && unitDef.WadingSinkWorld != 0f
                ? unitDef.WadingSinkWorld
                : Render.WadingWakeSystem.DefaultMaxSinkWorld;
            if (maxSink <= 0f)
            {
                _sim.UnitsMut[i].WadingSinkOffsetY = 0f;
                continue;
            }
            float waternessRaw = _groundSystem.SampleWaternessSmoothed(
                _sim.Units[i].Position, Render.WadingConfig.KernelRadius);
            float waterness = MathHelper.Clamp(
                (waternessRaw - Render.WadingConfig.ShorelineMidpoint) * 2f, 0f, 1f);
            _sim.UnitsMut[i].WadingSinkOffsetY =
                Render.WadingWakeSystem.ComputeSinkOffset(waterness, maxSink);
        }
    }

    private void DrawCorpses()
    {
        foreach (var corpse in _sim.Corpses)
        {
            // Don't render corpses attached to a unit — drawn on unit in DrawSingleUnit
            // (covers carried phase 0, pickup phase 4, putdown phase 5). Applies to
            // both the bagged-bag flow and the raw-corpse carry flow.
            if (corpse.DraggedByUnitID != GameConstants.InvalidUnit)
                continue;

            // Bagged corpses render as BodyBag from Corpses atlas
            if (corpse.Bagged)
            {
                DrawBaggedCorpse(corpse);
                continue;
            }

            // A corpse that was carried + dropped keeps its exact carried pose
            // (frozen angle + centroid anchor), so the settled draw matches the
            // put-down draw with no jump. Skip while flying (physics) so a
            // knocked-back dropped corpse still tumbles via the normal path.
            if (corpse.CarryDisplayAngle >= 0 && !corpse.InPhysics)
            {
                DrawCorpseCarriedFrame(corpse, _renderer.WorldToScreen(corpse.Position, corpse.Z, _camera));
                continue;
            }

            var unitDef = _gameData.Units.Get(corpse.UnitDefID);
            if (unitDef?.Sprite == null) continue;
            var atlasId = AtlasDefs.ResolveAtlasName(unitDef.Sprite.AtlasName);
            var atlas = _atlases[atlasId];
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
                if (idle != null) { var kfs = PickIdleFrames(idle); if (kfs != null && kfs.Count > 0) refH = kfs[0].Frame.Rect.Height; }

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
                Color corpseTint = MultiplyColor(new Color(alpha, alpha, alpha, alpha), _ambientColor);
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
        int atlasIdx = corpsesAtlasId;
        if (atlasIdx >= _atlases.Length || !_atlases[atlasIdx].IsLoaded) return default;
        var corpsesAtlas = _atlases[atlasIdx];
        var bodyBagSprite = corpsesAtlas.GetUnit("BodyBag");
        if (bodyBagSprite == null) return default;

        var iconAnim = bodyBagSprite.GetAnim("Icon");
        if (iconAnim == null) return default;

        // Use the static angle resolver so the body-bag picks the right scheme
        // (Old 30/60/300 vs New 0/45/90/270/315) from what's actually authored
        // in this anim. A throwaway AnimController without Init defaults to
        // Old, which silently misses on the now-new-scheme Corpses atlas.
        int spriteAngle = AnimController.ResolveAngleFor(iconAnim, facingAngle, out bool flipX);

        var kfs = iconAnim.GetAngle(spriteAngle);
        if (kfs == null || kfs.Count == 0) return default;

        return new FrameResult { Frame = kfs[0].Frame, FlipX = flipX };
    }

    /// <summary>Pick the keyframe list for the unit's Idle anim using the same
    /// angle-preference fallback that AnimController and DrawUnitIdleSprite use.
    /// Old atlases (VampireFaction, Navarre_Units) author angles 30/60/300,
    /// newer atlases (NecromancerEvolutions) use 0/45/90/270/315 — without this
    /// fallback, a hardcoded GetAngle(30) returns null on the new atlases and
    /// RefFrameHeight stays at its 128 default, scaling units to roughly half
    /// the correct on-screen size.</summary>
    private static List<Render.Keyframe>? PickIdleFrames(Render.AnimationData idle)
    {
        foreach (int pref in new[] { 30, 0, 45, 60, 315, 90, 270, 300 })
        {
            var kfs = idle.GetAngle(pref);
            if (kfs != null && kfs.Count > 0) return kfs;
        }
        // Last resort: any authored angle.
        foreach (var (_, frames) in idle.AngleFrames)
            if (frames.Count > 0) return frames;
        return null;
    }

    private float GetBodyBagRefHeight()
    {
        var corpsesAtlasId = AtlasDefs.ResolveAtlasName("Corpses");
        int atlasIdx = corpsesAtlasId;
        if (atlasIdx >= _atlases.Length || !_atlases[atlasIdx].IsLoaded) return 128f;
        var bodyBagSprite = _atlases[atlasIdx].GetUnit("BodyBag");
        if (bodyBagSprite == null) return 128f;
        var iconAnim = bodyBagSprite.GetAnim("Icon");
        if (iconAnim != null) { var kfs = PickIdleFrames(iconAnim); if (kfs != null && kfs.Count > 0) return kfs[0].Frame.Rect.Height; }
        return 128f;
    }

    private void DrawBaggedCorpse(Corpse corpse)
    {
        var fr = GetBodyBagFrame(corpse.FacingAngle);
        if (fr.Frame == null) return;

        var corpsesAtlasId = AtlasDefs.ResolveAtlasName("Corpses");
        var corpsesAtlas = _atlases[corpsesAtlasId];

        // Bag size is the SAME everywhere it appears (carry / ground / table) —
        // CarryBagScale is the canonical world-height. Doesn't multiply by
        // corpse.SpriteScale: the bag visual shouldn't grow/shrink with the
        // source unit's stature (a bear corpse and a soldier corpse get visually
        // identical bags). The unbagged dead-body sprite still uses SpriteScale
        // — only the bagged form is uniform.
        float refH = GetBodyBagRefHeight();
        float scale = (CarryBagScale * _camera.Zoom) / refH;

        var sp = _renderer.WorldToScreen(corpse.Position, 0f, _camera);
        DrawSpriteFrame(corpsesAtlas, fr.Frame.Value, sp, scale, fr.FlipX, _ambientColor);
    }

    private void DrawBaggedCorpseAt(Vector2 screenPos, float facingAngle, float rotation = 0f)
    {
        var fr = GetBodyBagFrame(facingAngle);
        if (fr.Frame == null) return;

        var corpsesAtlasId = AtlasDefs.ResolveAtlasName("Corpses");
        int atlasIdx = corpsesAtlasId;
        if (atlasIdx >= _atlases.Length || !_atlases[atlasIdx].IsLoaded) return;
        var corpsesAtlas = _atlases[atlasIdx];

        float refH = GetBodyBagRefHeight();
        float scale = (CarryBagScale * _camera.Zoom) / refH; // matches carry / ground bag size
        if (rotation == 0f)
        {
            DrawSpriteFrame(corpsesAtlas, fr.Frame.Value, screenPos, scale, fr.FlipX, _ambientColor);
        }
        else
        {
            // Inline rotation path — DrawSpriteFrame doesn't expose rotation yet
            // (only the table overlay needs it). Replicates DrawSpriteFrame's pivot
            // math + adds the rotation argument. Keep this branch tight; if a
            // second caller ever wants rotation, promote this into DrawSpriteFrame.
            var frame = fr.Frame.Value;
            var tex = corpsesAtlas.GetTextureForFrame(frame);
            if (tex == null) return;
            float pivotX = fr.FlipX ? (1f - frame.PivotX) : frame.PivotX;
            float pivotY = 1f - frame.PivotY;
            var origin = new Vector2(pivotX * frame.Rect.Width, pivotY * frame.Rect.Height);
            var effects = fr.FlipX ? SpriteEffects.FlipHorizontally : SpriteEffects.None;
            _spriteBatch.Draw(tex, screenPos, frame.Rect, _ambientColor, rotation, origin, scale, effects, 0f);
        }
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
        int atlasIdx = corpsesAtlasId;
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
                // The bag's spritemeta pivot (0.5, 0.15) sits at the visible bag's
                // natural anchor — the artist put the pivot ON the bag's visible
                // center, NOT at the frame's geometric bottom. So drawing at the
                // hilt screen position lands the visible bag's center directly on
                // the hilt; no anchor-to-center correction needed.
                //
                // HiltHeight is in world units, but the hilt's screen offset must
                // match the sprite's drawn scale (full Zoom — sprites aren't
                // yRatio'd, because the artist baked iso perspective into art).
                // Standard WorldToScreen subtracts `HiltHeight * Zoom * YRatio`,
                // yielding only half the correct offset. WorldToScreenPx takes
                // literal pixels and skips the yRatio fold — this restores the
                // pre-`421fdd3` behavior of `HiltHeight * Zoom / HeightScale`.
                var hiltScreen = _renderer.WorldToScreenPx(attach.HiltWorld, attach.HiltHeight * _camera.Zoom, _camera);
                hiltScreen.X += ofsX;
                hiltScreen.Y += CarryOffsetY; // small fine-tune; can be negative to nudge bag up
                DrawSpriteFrame(corpsesAtlas, fr.Frame.Value, hiltScreen, bagScale, fr.FlipX, _ambientColor);
                return;
            }
        }

        // Fallback: offset-based positioning (when weapon attach data is missing).
        // No centerCorrection because the bag's pivot already sits on the visible
        // bag's center. Estimate hilt at ~30% of unit height (mid-torso) to land
        // the bag near where the hand would be.
        float angleDeg = ((facingAngle % 360f) + 360f) % 360f;
        float offsetPx = 8f * unitScale;
        float hDir = (angleDeg > 90f && angleDeg < 270f) ? -1f : 1f;
        float bagX = unitScreenPos.X + offsetPx * hDir * 0.66f + ofsX;

        float spriteWorldH = (unitDef != null && unitDef.SpriteWorldHeight > 0) ? unitDef.SpriteWorldHeight : 1.8f;
        float spritePixelH = spriteWorldH * _sim.Units[unitIdx].SpriteScale * _camera.Zoom;
        float bagY = unitScreenPos.Y - spritePixelH * 0.30f + CarryOffsetY;

        DrawSpriteFrame(corpsesAtlas, fr.Frame.Value, new Vector2(bagX, bagY), bagScale, fr.FlipX, _ambientColor);
    }

    // ── Raw-corpse carry (body bag mothballed; see GameConstants.UseBodyBag) ──

    /// <summary>Resolve the final death-pose frame for a corpse's unit def at a
    /// facing angle. refH is the unit's Idle height so corpse scale matches the
    /// living unit (and the on-ground corpse). Returns false if unavailable.</summary>
    private bool TryGetCorpseDeathFrame(string unitDefID, float facingAngle,
        out SpriteAtlas atlas, out SpriteFrame frame, out bool flipX, out float refH)
    {
        atlas = default!; frame = default; flipX = false; refH = 128f;
        var unitDef = _gameData.Units.Get(unitDefID);
        if (unitDef?.Sprite == null) return false;
        var atlasId = AtlasDefs.ResolveAtlasName(unitDef.Sprite.AtlasName);
        int ai = atlasId;
        if (ai < 0 || ai >= _atlases.Length || !_atlases[ai].IsLoaded) return false;
        atlas = _atlases[ai];
        var spriteData = atlas.GetUnit(unitDef.Sprite.SpriteName);
        if (spriteData == null) return false;
        var death = spriteData.GetAnim("Death");
        if (death == null) return false;
        int spriteAngle = AnimController.ResolveAngleFor(death, facingAngle, out flipX);
        var kfs = death.GetAngle(spriteAngle);
        if (kfs == null || kfs.Count == 0) return false;
        frame = kfs[kfs.Count - 1].Frame; // final death pose (settled corpse)
        var idle = spriteData.GetAnim("Idle");
        if (idle != null) { var ik = PickIdleFrames(idle); if (ik != null && ik.Count > 0) refH = ik[0].Frame.Rect.Height; }
        return true;
    }

    /// <summary>Draw a corpse's death sprite at a screen position, optionally
    /// rotated (used for the table overlay + putdown lerp). Mirrors the body-bag
    /// draw path so it slots in wherever DrawBaggedCorpseAt was used.</summary>
    private void DrawCorpseSpriteAt(string unitDefID, Vector2 screenPos, float facingAngle, float spriteScale, float rotation = 0f)
    {
        if (!TryGetCorpseDeathFrame(unitDefID, facingAngle, out var atlas, out var frame, out bool flipX, out float refH))
            return;
        var tex = atlas.GetTextureForFrame(frame);
        if (tex == null) return;
        var unitDef = _gameData.Units.Get(unitDefID);
        float worldH = (unitDef != null && unitDef.SpriteWorldHeight > 0 ? unitDef.SpriteWorldHeight : 1.8f) * spriteScale;
        float scale = (worldH * _camera.Zoom) / refH;
        float pivotX = flipX ? (1f - frame.PivotX) : frame.PivotX;
        float pivotY = 1f - frame.PivotY; // spritemeta pivots are bottom-left origin
        var origin = new Vector2(pivotX * frame.Rect.Width, pivotY * frame.Rect.Height);
        var effects = flipX ? SpriteEffects.FlipHorizontally : SpriteEffects.None;
        _spriteBatch.Draw(tex, screenPos, frame.Rect, _ambientColor, rotation, origin, scale, effects, 0f);
    }

    /// <summary>Opaque-pixel centroid of a frame, in frame-local top-left pixels.
    /// Used to balance a carried corpse on the carrier's hands. Cached two ways:
    /// in-memory by (texture, rect), and on disk in <c>data/frame_centroids.json</c>
    /// keyed by (atlas name, page, rect) — so the ~85ms GetData read-back on the
    /// huge unit atlases is paid at most once per frame, ever, across all runs.</summary>
    private Vector2 GetFrameCentroid(Microsoft.Xna.Framework.Graphics.Texture2D tex, SpriteFrame frame)
    {
        var key = (tex, frame.Rect);
        if (_frameCentroidCache.TryGetValue(key, out var cached)) return cached;

        if (_persistedCentroids == null) LoadPersistedCentroids();
        string? pkey = CentroidKeyFor(tex, frame);
        if (pkey != null && _persistedCentroids!.TryGetValue(pkey, out var persisted))
        {
            _frameCentroidCache[key] = persisted;
            return persisted;
        }

        int w = frame.Rect.Width, h = frame.Rect.Height;
        Vector2 result = new Vector2(w * 0.5f, h * 0.5f);
        if (w > 0 && h > 0)
        {
            var data = new Color[w * h];
            tex.GetData(0, frame.Rect, data, 0, data.Length);
            double sx = 0, sy = 0; long n = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (data[y * w + x].A > 16) { sx += x; sy += y; n++; }
            if (n > 0) result = new Vector2((float)(sx / n), (float)(sy / n));
        }
        _frameCentroidCache[key] = result;
        if (pkey != null)
        {
            _persistedCentroids![pkey] = result;
            _centroidsDirty = true;
            // Persist immediately on a genuinely-new frame (rare) so it survives a
            // crash. Suppressed during the bulk bake, which saves once at the end.
            if (!_bulkCentroidBake) SavePersistedCentroids();
        }
        return result;
    }

    // --- Persistent frame-centroid cache (data/frame_centroids.json) ---
    private Dictionary<string, Vector2>? _persistedCentroids;
    private bool _centroidsDirty;
    private bool _bulkCentroidBake;
    private Dictionary<Microsoft.Xna.Framework.Graphics.Texture2D, int>? _texToAtlasIdx;
    private static string CentroidCachePath => Core.GamePaths.Resolve("data/frame_centroids.json");

    /// <summary>Stable disk key for a frame: atlas name + page index + rect. Independent
    /// of the runtime Texture2D identity so it survives across runs. Null if the
    /// texture isn't part of a loaded atlas.</summary>
    private string? CentroidKeyFor(Microsoft.Xna.Framework.Graphics.Texture2D tex, in SpriteFrame frame)
    {
        int ai = AtlasIdxForTexture(tex);
        if (ai < 0) return null;
        string name = ai < Core.AtlasDefs.Names.Length ? Core.AtlasDefs.Names[ai] : ai.ToString();
        var r = frame.Rect;
        return $"{name}#{frame.TextureIndex}#{r.X},{r.Y},{r.Width},{r.Height}";
    }

    private int AtlasIdxForTexture(Microsoft.Xna.Framework.Graphics.Texture2D tex)
    {
        if (_texToAtlasIdx == null)
        {
            _texToAtlasIdx = new();
            for (int i = 0; i < _atlases.Length; i++)
            {
                var a = _atlases[i];
                if (a == null || !a.IsLoaded) continue;
                foreach (var t in a.Textures)
                    if (t != null) _texToAtlasIdx[t] = i;
            }
        }
        return _texToAtlasIdx.TryGetValue(tex, out int idx) ? idx : -1;
    }

    private void LoadPersistedCentroids()
    {
        _persistedCentroids = new();
        try
        {
            string path = CentroidCachePath;
            if (!File.Exists(path)) return;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                var s = p.Value.GetString();
                if (string.IsNullOrEmpty(s)) continue;
                int comma = s.IndexOf(',');
                if (comma < 0) continue;
                if (float.TryParse(s.AsSpan(0, comma), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float cx) &&
                    float.TryParse(s.AsSpan(comma + 1), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float cy))
                    _persistedCentroids[p.Name] = new Vector2(cx, cy);
            }
        }
        catch { /* corrupt/missing cache → recompute lazily */ }
    }

    private void SavePersistedCentroids()
    {
        if (_persistedCentroids == null || !_centroidsDirty) return;
        try
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var map = new Dictionary<string, string>(_persistedCentroids.Count);
            foreach (var kv in _persistedCentroids)
                map[kv.Key] = kv.Value.X.ToString(ci) + "," + kv.Value.Y.ToString(ci);
            var json = System.Text.Json.JsonSerializer.Serialize(map,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(CentroidCachePath, json);
            _centroidsDirty = false;
        }
        catch { /* read-only data dir → cache stays in-memory only */ }
    }

    /// <summary>Compute + persist every unit's final death-frame centroid so the disk
    /// cache is complete and no carry ever stalls on a GetData read-back. Run once
    /// offline via <c>--bake-centroids</c>; the resulting file ships with the build.</summary>
    private void BakeAllCorpseCentroids()
    {
        if (_gameData?.Units == null) return;
        if (_persistedCentroids == null) LoadPersistedCentroids();
        _bulkCentroidBake = true;
        int total = 0;
        foreach (var def in _gameData.Units.All())
        {
            if (def?.Sprite == null) continue;
            int ai = Core.AtlasDefs.ResolveAtlasName(def.Sprite.AtlasName);
            if (ai < 0 || ai >= _atlases.Length || !_atlases[ai].IsLoaded) continue;
            var death = _atlases[ai].GetUnit(def.Sprite.SpriteName)?.GetAnim("Death");
            if (death == null) continue;
            foreach (var (_, kfs) in death.AngleFrames)
            {
                if (kfs == null || kfs.Count == 0) continue;
                var frame = kfs[kfs.Count - 1].Frame;
                var tex = _atlases[ai].GetTextureForFrame(frame);
                if (tex == null) continue;
                GetFrameCentroid(tex, frame); // computes + marks dirty
                total++;
            }
        }
        _bulkCentroidBake = false;
        SavePersistedCentroids();
        DebugLog.Log("startup", $"[bake] corpse centroids: {total} frames -> {CentroidCachePath}");
    }

    /// <summary>Draw the carried corpse balanced on the carrier's hands. Resolves
    /// orientation through the CARRIER's own controller (lockstep with the
    /// necromancer's facing), records that exact angle+flip on the corpse so it
    /// survives the drop unchanged, then renders the centroid-pegged frame.</summary>
    private void DrawCarriedCorpse(int unitIdx, Vector2 unitScreenPos)
    {
        var cc = _sim.FindCorpseByID(_sim.Units[unitIdx].CarryingCorpseID);
        if (cc == null) return;
        if (!_unitAnims.TryGetValue(_sim.Units[unitIdx].Id, out var carrierAnim)) return;

        var corpseDef = _gameData.Units.Get(cc.UnitDefID);
        if (corpseDef?.Sprite == null) return;
        var atlasId = AtlasDefs.ResolveAtlasName(corpseDef.Sprite.AtlasName);
        int ai = atlasId;
        if (ai < 0 || ai >= _atlases.Length || !_atlases[ai].IsLoaded) return;
        var death = _atlases[ai].GetUnit(corpseDef.Sprite.SpriteName)?.GetAnim("Death");
        if (death == null) return;

        // Resolve through the CARRIER's controller (same scheme + hysteresis as the
        // necromancer's body) so they snap together. Fall back to the corpse's own
        // resolution only if its art lacks that angle (e.g. a different-scheme
        // animal). Freeze the result on the corpse so the dropped pose matches.
        float carryFacing = _sim.Units[unitIdx].FacingAngle;
        int angle = carrierAnim.Ctrl.ResolveAngle(carryFacing, out bool flipX);
        if (death.GetAngle(angle) is not { Count: > 0 })
            angle = AnimController.ResolveAngleFor(death, carryFacing, out flipX);
        cc.CarryDisplayAngle = angle;
        cc.CarryDisplayFlip = flipX;

        // Hand/carry anchor — weapon hilt (the carry pose holds both hands out front).
        Vector2 pos = unitScreenPos;
        var carrierDef = _gameData.Units.Get(_sim.Units[unitIdx].UnitDefID);
        if (carrierDef != null)
        {
            var attach = ComputeWeaponAttach(unitIdx, carrierDef, carrierAnim);
            if (attach.Valid)
                pos = _renderer.WorldToScreenPx(attach.HiltWorld, attach.HiltHeight * _camera.Zoom, _camera);
        }
        pos.Y += CarriedCorpseHandOffsetY;

        DrawCorpseCarriedFrame(cc, pos);
    }

    /// <summary>Render a corpse using its frozen carry orientation
    /// (<see cref="Corpse.CarryDisplayAngle"/>/Flip), centroid-pegged to
    /// <paramref name="screenPos"/>. Shared by the carry, the ground put-down,
    /// and the settled-on-ground draw so all three are pixel-identical — no jump
    /// at hand-off and the placed corpse keeps its carried pose.</summary>
    private void DrawCorpseCarriedFrame(Corpse cc, Vector2 screenPos)
    {
        if (cc.CarryDisplayAngle < 0) return;
        var corpseDef = _gameData.Units.Get(cc.UnitDefID);
        if (corpseDef?.Sprite == null) return;
        var atlasId = AtlasDefs.ResolveAtlasName(corpseDef.Sprite.AtlasName);
        int ai = atlasId;
        if (ai < 0 || ai >= _atlases.Length || !_atlases[ai].IsLoaded) return;
        var atlas = _atlases[ai];
        var spriteData = atlas.GetUnit(corpseDef.Sprite.SpriteName);
        var death = spriteData?.GetAnim("Death");
        var kfs = death?.GetAngle(cc.CarryDisplayAngle);
        if (kfs == null || kfs.Count == 0) return;
        var frame = kfs[kfs.Count - 1].Frame;
        var tex = atlas.GetTextureForFrame(frame);
        if (tex == null) return;

        float refH = 128f;
        var idle = spriteData!.GetAnim("Idle");
        if (idle != null) { var ik = PickIdleFrames(idle); if (ik != null && ik.Count > 0) refH = ik[0].Frame.Rect.Height; }
        float worldH = (corpseDef.SpriteWorldHeight > 0 ? corpseDef.SpriteWorldHeight : 1.8f) * cc.SpriteScale;
        float scale = (worldH * _camera.Zoom) / refH;

        // Dissolve fade (mirrors DrawCorpses) so a placed corpse still fades when consumed.
        int alphaInt = 255;
        if (cc.Dissolving)
        {
            float t = cc.DissolveTimer / 2f;
            float a = 255f * (1f - t);
            if ((int)(cc.DissolveTimer * 8f) % 2 == 0) a *= 0.3f;
            alphaInt = (int)MathUtil.Clamp(a, 0f, 255f);
        }
        byte alpha = (byte)alphaInt;
        Color tint = MultiplyColor(new Color(alpha, alpha, alpha, alpha), _ambientColor);

        bool flipX = cc.CarryDisplayFlip;
        var centroid = GetFrameCentroid(tex, frame);
        float originX = flipX ? (frame.Rect.Width - centroid.X) : centroid.X;
        var origin = new Vector2(originX, centroid.Y);
        var effects = flipX ? SpriteEffects.FlipHorizontally : SpriteEffects.None;
        _spriteBatch.Draw(tex, screenPos, frame.Rect, tint, 0f, origin, scale, effects, 0f);
    }

    /// <summary>Carried visual anchored on the carrier — bag or raw corpse per
    /// GameConstants.UseBodyBag.</summary>
    private void DrawCarriedVisual(int unitIdx, Vector2 unitScreenPos, float unitScale)
    {
        if (GameConstants.UseBodyBag)
            DrawCarriedBodyBag(unitIdx, unitScreenPos, unitScale, _sim.Units[unitIdx].FacingAngle);
        else
            DrawCarriedCorpse(unitIdx, unitScreenPos);
    }

    /// <summary>Carried visual at an explicit screen position (table-putdown lerp
    /// or ground drop) — bag or raw corpse per GameConstants.UseBodyBag.</summary>
    private void DrawCarriedVisualAt(int unitIdx, Vector2 screenPos, float facingAngle, float rotation = 0f)
    {
        if (GameConstants.UseBodyBag)
        {
            DrawBaggedCorpseAt(screenPos, facingAngle, rotation);
            return;
        }
        var cc = _sim.FindCorpseByID(_sim.Units[unitIdx].CarryingCorpseID);
        if (cc != null)
            DrawCorpseSpriteAt(cc.UnitDefID, screenPos, facingAngle, cc.SpriteScale, rotation);
    }

    // ═══════════════════════════════════════
    //  Gameplay Debug Visualizations (F7)
    // ═══════════════════════════════════════

    private void DrawWindDebug(int screenW, int screenH)
    {
        if (_pixel == null || _renderer == null) return;

        // Sample wind at a grid of world positions and draw colored quads
        // Scale cell size with zoom so we never have too many cells on screen
        float cellSize = MathF.Max(2f, 40f / MathF.Max(_camera.Zoom, 1f));

        // Get view bounds in world space
        var topLeft = _renderer.ScreenToWorld(Vector2.Zero, _camera);
        var bottomRight = _renderer.ScreenToWorld(new Vector2(screenW, screenH), _camera);
        float minX = MathF.Floor(topLeft.X / cellSize) * cellSize;
        float minY = MathF.Floor(topLeft.Y / cellSize) * cellSize;
        float maxX = MathF.Ceiling(bottomRight.X / cellSize) * cellSize;
        float maxY = MathF.Ceiling(bottomRight.Y / cellSize) * cellSize;

        // Safety cap: limit to ~2500 cells max
        int maxCells = 50;
        if ((maxX - minX) / cellSize > maxCells) maxX = minX + maxCells * cellSize;
        if ((maxY - minY) / cellSize > maxCells) maxY = minY + maxCells * cellSize;

        float windAngle = 0f;
        for (float wy = minY; wy < maxY; wy += cellSize)
        {
            for (float wx = minX; wx < maxX; wx += cellSize)
            {
                float gust = EnvironmentSystem.SampleWind(wx, wy, _gameTime, out windAngle);
                if (gust < 0.01f) continue; // skip fully still cells

                var sp = _renderer.WorldToScreen(new Vec2(wx, wy), 0f, _camera);
                float halfPx = cellSize * _camera.Zoom * 0.5f;
                int px = (int)(sp.X - halfPx);
                int py = (int)(sp.Y - halfPx * _camera.YRatio);
                int pw = (int)(cellSize * _camera.Zoom);
                int ph = (int)(cellSize * _camera.Zoom * _camera.YRatio);

                // Color: blue(still) → yellow → white(peak)
                byte r = (byte)(55 + (int)(200 * gust));
                byte g = (byte)(55 + (int)(200 * gust));
                byte b = (byte)(55 + (int)(80 * (1f - gust)));
                byte a = (byte)(40 + (int)(80 * gust));
                _spriteBatch.Draw(_pixel, new Rectangle(px, py, pw, ph), new Color(r, g, b, a));
            }
        }

        // Direction arrow in top-left corner
        float arrowLen = 40f;
        float arrowX = 60f;
        float arrowY = 60f;

        // Arrow body
        float adx = MathF.Cos(windAngle) * arrowLen;
        float ady = MathF.Sin(windAngle) * arrowLen;
        DrawDebugLine(new Vector2(arrowX - adx * 0.5f, arrowY - ady * 0.5f),
                      new Vector2(arrowX + adx * 0.5f, arrowY + ady * 0.5f), Color.White);
        // Arrowhead
        float headAngle1 = windAngle + 2.5f;
        float headAngle2 = windAngle - 2.5f;
        float headLen = 12f;
        var tip = new Vector2(arrowX + adx * 0.5f, arrowY + ady * 0.5f);
        DrawDebugLine(tip, tip + new Vector2(MathF.Cos(headAngle1) * headLen, MathF.Sin(headAngle1) * headLen), Color.White);
        DrawDebugLine(tip, tip + new Vector2(MathF.Cos(headAngle2) * headLen, MathF.Sin(headAngle2) * headLen), Color.White);

        // Background circle for arrow
        _spriteBatch.Draw(_pixel, new Rectangle((int)arrowX - 45, (int)arrowY - 45, 90, 90), new Color(0, 0, 0, 120));

        // Label
        if (_smallFont != null)
        {
            float angleDeg = windAngle * 180f / MathF.PI;
            _spriteBatch.DrawString(_smallFont, $"Wind {angleDeg:F0} deg",
                new Vector2(16, 108), Color.White);
            _spriteBatch.DrawString(_smallFont, "F6: Wind Debug",
                new Vector2(16, 122), new Color(180, 180, 180));
        }
    }

    private void DrawDebugLine(Vector2 a, Vector2 b, Color color)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 0.5f) return;
        float angle = MathF.Atan2(dy, dx);
        _spriteBatch.Draw(_pixel, a, null, color, angle, Vector2.Zero, new Vector2(len, 1f), SpriteEffects.None, 0f);
    }

    /// <summary>Draw one F7 horde ring (a circle outline) plus a label at its north
    /// edge showing the ring's name + world radius. Concentric rings stack their
    /// labels at different heights so all three stay readable.</summary>
    private void DrawHordeRing(Vec2 center, float radius, Color color, string label, int screenW, int screenH)
    {
        _debugDraw.DrawCircle(_spriteBatch, _renderer, _camera, center, radius, color, 48);
        if (_smallFont == null || radius < 0.5f) return;
        var top = _camera.WorldToScreen(center + new Vec2(0f, -radius), 0f, screenW, screenH);
        var sz = _smallFont.MeasureString(label);
        var labelColor = new Color(color.R, color.G, color.B, (byte)255);
        _spriteBatch.DrawString(_smallFont, label,
            new Vector2((int)(top.X - sz.X / 2f), (int)(top.Y - sz.Y - 1)), labelColor);
    }

    private void DrawHordeDebug()
    {
        _debugDraw.EnsurePixel(GraphicsDevice);
        int screenW = GraphicsDevice.Viewport.Width, screenH = GraphicsDevice.Viewport.Height;
        var horde = _sim.Horde;
        var settings = horde.Settings;

        // Three horde rings, each labeled with its world radius so they're easy to
        // tell apart (they're concentric, so the labels stack by radius up the north
        // edge). Alpha kept high enough that all three read clearly over the ground.
        //   formation (green) = EffectiveRadius           — where units stand (√N)
        //   engage    (orange)= formation + EngagementOffset — horde grabs enemies here
        //   leash     (red)   = engage + LeashOffset         — chaser force-returned here
        DrawHordeRing(horde.CircleCenter, horde.EffectiveRadius, new Color(110, 230, 110, 200),
            $"formation {horde.EffectiveRadius:F0}", screenW, screenH);
        DrawHordeRing(horde.CircleCenter, horde.EngagementRange, new Color(255, 185, 60, 210),
            $"engage {horde.EngagementRange:F0}", screenW, screenH);
        DrawHordeRing(horde.CircleCenter, horde.LeashRadius, new Color(255, 70, 70, 210),
            $"leash {horde.LeashRadius:F0}", screenW, screenH);

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

            if (aiArchetype != null)
            {
                string routineLabel = aiArchetype.GetRoutineName(_sim.Units[i].Routine);
                aiLabel = $"{aiLabel} - {routineLabel}";
            }

            // Build the info string. Base line always shows AI + velocity + anim +
            // max speed. Extra lines appear only when the corresponding state is
            // interesting (non-default) — keeps idle units uncluttered while making
            // a unit in a weird state (stuck in Knockdown, dangling OverrideAnim,
            // lost PendingAttack, etc.) obvious at a glance.
            var sb = new System.Text.StringBuilder();
            sb.Append(aiLabel).Append('\n');
            // eff + ms are the two values SetEffort writes (MoveEffort + the derived
            // MaxSpeed cap) — show both so slow-stroll routines (e.g. Walk×0.5) are
            // legible at a glance.
            sb.Append($"v:{speed:F1} {animLabel} eff:{_sim.Units[i].MoveEffort} ms:{maxSpeed:F1}");

            var ov = _sim.Units[i].OverrideAnim;
            if (ov.IsActive)
            {
                string dur = ov.Duration < 0 ? "loop" : (ov.Duration == 0 ? "once" : $"{ov.Duration:F1}s");
                sb.Append($"\nov:{ov.State} p{ov.Priority} {dur}");
                if (_sim.Units[i].OverrideStarted) sb.Append(" *");
            }

            var inc = _sim.Units[i].Incap;
            if (inc.Active || inc.Recovering)
            {
                sb.Append($"\nincap:{(inc.Active ? "A" : "")}{(inc.Recovering ? "R" : "")}");
                sb.Append($" hold={inc.HoldAnim} rec={inc.RecoverAnim}");
                if (inc.RecoverTimer != 0f) sb.Append($" t={inc.RecoverTimer:F2}");
                if (inc.HoldAtEnd) sb.Append(" @end");
            }

            if (!_sim.Units[i].PendingAttack.IsNone)
            {
                uint tgtId = _sim.Units[i].PendingAttack.IsUnit ? _sim.Units[i].PendingAttack.UnitID : 0;
                // ASCII-only: the small SpriteFont is authored with a limited glyph
                // range; any char outside that range throws ArgumentException on
                // DrawString and crashes the game (non-ASCII arrow was the bug).
                sb.Append($"\npend:>{tgtId} w{_sim.Units[i].PendingWeaponIdx}");
                if (_sim.Units[i].PendingWeaponIsRanged) sb.Append(" R");
                if (_sim.Units[i].CurrentAttackLungeDist > 0f)
                    sb.Append($" lunge={_sim.Units[i].CurrentAttackLungeDist:F2}");
            }

            if (_sim.Units[i].JumpPhase != 0)
            {
                string phaseName = _sim.Units[i].JumpPhase switch
                {
                    1 => "Takeoff", 2 => "Airborne", 3 => "Landing", 4 => "Recovery",
                    _ => $"P{_sim.Units[i].JumpPhase}"
                };
                sb.Append($"\njump:{phaseName} Z={_sim.Units[i].Z:F2}");
            }

            float rox = _sim.Units[i].RenderOffset.X, roy = _sim.Units[i].RenderOffset.Y;
            if (rox * rox + roy * roy > 0.0001f)
                sb.Append($"\nrenderOff:({rox:F2},{roy:F2})");

            if (_sim.Units[i].InCombat || _sim.Units[i].AttackCooldown > 0f
                || _sim.Units[i].PostAttackTimer > 0f)
            {
                sb.Append("\ncd:");
                if (_sim.Units[i].InCombat) sb.Append("InCombat ");
                if (_sim.Units[i].AttackCooldown > 0f) sb.Append($"a{_sim.Units[i].AttackCooldown:F1} ");
                if (_sim.Units[i].PostAttackTimer > 0f) sb.Append($"p{_sim.Units[i].PostAttackTimer:F1}");
            }

            string info = sb.ToString();
            // Approximate width for anchoring — SpriteFont.MeasureString is accurate
            // but per-unit MeasureString every frame is hot-path waste.
            var textPos = new Vector2(sp.X - info.Length, sp.Y - 28);
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

    internal enum DepthItemType : byte { Unit, EnvObject, CloudPuff, GrassTuft, DeathFogPuff }

    internal struct DepthItem : IComparable<DepthItem>
    {
        public float Y;
        public DepthItemType Type;
        public int Index;       // Unit index, env object index, or cloud index
        public int SubIndex;    // For cloud puffs: puff index within the cloud
        // Sort by Y first, then by (Type, Index, SubIndex) as tiebreaker so items
        // with equal Y always order deterministically. .NET's List<T>.Sort is
        // introsort — unstable — so without a tiebreaker, tufts with Y equal to
        // a bush's Y would flicker in front of / behind it as the camera pans.
        public int CompareTo(DepthItem other)
        {
            int c = Y.CompareTo(other.Y);
            if (c != 0) return c;
            c = ((byte)Type).CompareTo((byte)other.Type);
            if (c != 0) return c;
            c = Index.CompareTo(other.Index);
            if (c != 0) return c;
            return SubIndex.CompareTo(other.SubIndex);
        }
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

        // Add units (view-culled, same bounds as env objects below). Use RenderPos
        // so a lunging unit re-sorts against its neighbors naturally — a forward-
        // lunging sprite that crosses another unit's Y draws in front during the
        // lunge. The 20-unit margin covers sprite overhang (sprites are a few world
        // units tall and shorter than the trees culled with the same bounds), so a
        // unit just off-screen whose head still pokes in isn't clipped early.
        // Without this, off-screen units each ran a full DrawSingleUnit every frame —
        // the dominant Draw cost on a populated map, especially with fog off.
        for (int i = 0; i < _sim.Units.Count; i++)
        {
            if (!_sim.Units[i].Alive) continue;
            var rp = _sim.Units[i].RenderPos;
            if (rp.X < viewLeft || rp.X > viewRight || rp.Y < viewTop || rp.Y > viewBottom)
                continue;
            items.Add(new DepthItem { Y = rp.Y, Type = DepthItemType.Unit, Index = i });
        }

        // Add environment objects (with view culling, skip collected foragables, skip ground-layer objects)
        for (int i = 0; i < _envSystem.ObjectCount; i++)
        {
            if (!_envSystem.IsObjectVisible(i)) continue;
            var obj = _envSystem.Objects[i];
            var def = _envSystem.Defs[obj.DefIndex];
            if (def.Category == "Traps") continue; // drawn in ground layer pass
            if (obj.X < viewLeft || obj.X > viewRight || obj.Y < viewTop || obj.Y > viewBottom)
                continue;
            // Note: defs whose sprites failed to load get a placeholder texture in EnvironmentSystem,
            // so GetDefTexture is non-null and the object still appears.
            items.Add(new DepthItem { Y = obj.Y, Type = DepthItemType.EnvObject, Index = i });
        }

        // Add poison cloud puffs
        _poisonCloudRenderer.SetContext(_spriteBatch, _glowTex, _camera, _renderer, _flipbooks, _gameTime);
        _poisonCloudRenderer.AddPuffsToDepthList(_sim.PoisonClouds, items);

        // Add grass tufts — Y-sorted with units so a tuft "in front" (higher Y)
        // correctly renders over a unit's feet, and one "behind" (lower Y) is
        // drawn first and hidden by the unit.
        _grassRenderer.AddTuftsToDepthList(
            _camera, _renderer.ScreenW, _renderer.ScreenH,
            _grassMap, _grassW, _grassH,
            _gameData.Settings.Grass, _ambientColor, items);

        // Add death-fog puffs — Y-sorted with units so puffs visually drift in
        // front of / behind characters depending on their relative ground Y.
        // Mirrors PoisonCloudRenderer's depth-list integration.
        if (_flipbooks != null && _flipbooks.TryGetValue("cloud03", out var deathFogFb))
        {
            _deathFogRenderer.SetContext(_spriteBatch, _camera, _renderer, deathFogFb, _gameTime);
            _deathFogRenderer.AddPuffsToDepthList(_deathFog, _renderer.ScreenW, _renderer.ScreenH, items);
        }

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
                case DepthItemType.GrassTuft:
                    _grassRenderer.DrawSingleTuft(_spriteBatch, item.Index);
                    break;
                case DepthItemType.DeathFogPuff:
                    _deathFogRenderer.DrawSinglePuff(item.Index);
                    break;
            }
        }
    }

    private void DrawSingleUnit(int i)
    {
        // Fog of war: hide non-undead units (and their buffs, which draw inside
        // this method) when they're not currently in any undead's detection range.
        if (_sim.Units[i].Faction != Faction.Undead && !_fogOfWar.IsVisible(_sim.Units[i].Position))
            return;

        uint uid = _sim.Units[i].Id;
        if (!_unitAnims.TryGetValue(uid, out var animData)) return;

        var unitDef = _gameData.Units.Get(_sim.Units[i].UnitDefID);
        if (unitDef == null) return;

        var atlas = _atlases[animData.AtlasID];
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

        // Apply weather ambient light
        tint = MultiplyColor(tint, _ambientColor);

        float heightOffset = _sim.Units[i].Z;
        // Use RenderPos (Position + RenderOffset) so lunge and any future cosmetic
        // offsets propagate to every visual attached to this unit: sprite, weapon,
        // shield, status symbols, health bar, buff visuals, damage numbers, etc.
        var renderPos = _sim.Units[i].RenderPos;
        var sp = _renderer.WorldToScreen(renderPos, heightOffset, _camera);
        // For drawing above unit.
        var sp_upper = _renderer.WorldToScreen(renderPos, heightOffset + _sim.Units[i].CollisionHeight, _camera);

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
        _buffVisuals.DrawUnit(i, renderPos, 0, _gameTime,
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
        int putdownTableIdx = _sim.Units[i].PutDownTableIdx;
        bool tableBoundPutdown = cPhase == 5 && putdownTableIdx >= 0
            && _envSystem != null && putdownTableIdx < _envSystem.ObjectCount;
        bool hasCorpse = _sim.Units[i].CarryingCorpseID >= 0
            && (cPhase == 0 || cPhase == 4 || cPhase == 5);
        // facingAway = the unit's back is toward the camera, so the carried corpse
        // renders *behind* the sprite. Keyed off the RESOLVED sprite angle (not the
        // raw mouse angle) so the render order flips exactly when the sprite flips —
        // with the same buckets + hysteresis — instead of jittering on tiny mouse
        // moves. Back angles: new scheme N=270 / NE-NW=315, old scheme up=300.
        // Everything else (E/W=0, S=90, SE/SW=45, old 30/60) is front → on top.
        int sprAngle = animData.Ctrl.ResolveAngle(_sim.Units[i].FacingAngle, out _);
        bool facingAway = sprAngle == 270 || sprAngle == 315 || sprAngle == 300;
        bool drawBagAtHilt = false; // whether to draw on unit (vs at ground)

        if (hasCorpse && !tableBoundPutdown)
        {
            if (cPhase == 0)
                drawBagAtHilt = true; // fully carried
            else if (cPhase == 4) // Pickup: ground until action moment, then hilt
                drawBagAtHilt = animData.Ctrl.HasReachedActionMoment();
            else if (cPhase == 5) // PutDown: hilt until action moment, then ground
                drawBagAtHilt = !animData.Ctrl.HasReachedActionMoment();
        }

        // Pre-compute table-bound PutDown lerp target so we can draw it on the
        // correct side of the unit (back vs front) by Y-sort convention.
        Vector2? tableLerpScreen = null;
        float tableLerpRotation = 0f;
        if (tableBoundPutdown && hasCorpse)
        {
            // t = anim progress: 0 at PutDown start (bag at hand), 1 at completion
            // (bag on table). MathHelper.Lerp handles position and rotation.
            float t = animData.Ctrl.TimeFraction;

            // Source pose: hilt + carry offsets (mirrors DrawCarriedBodyBag).
            Vector2 sourcePos = sp; // fallback to unit screen pos if attach invalid
            var attach = ComputeWeaponAttach(i, unitDef, animData);
            if (attach.Valid)
                sourcePos = _renderer.WorldToScreenPx(attach.HiltWorld, attach.HiltHeight * _camera.Zoom, _camera);
            var bagFr = GetBodyBagFrame(_sim.Units[i].FacingAngle);
            float ofsX = bagFr.FlipX ? -CarryOffsetX : CarryOffsetX;
            sourcePos.X += ofsX;
            sourcePos.Y += CarryOffsetY;

            // Destination pose: table-overlay anchor (mirrors DrawSingleEnvObject's
            // table body-bag block). Same lift formula keeps position consistent
            // when the lerp finishes and the env overlay takes over.
            var tableObj = _envSystem.GetObject(putdownTableIdx);
            var tableDef = _envSystem.Defs[tableObj.DefIndex];
            float tableWorldH = tableDef.SpriteWorldHeight * tableObj.Scale * tableDef.Scale;
            float bagLift = tableWorldH * tableDef.PivotY * 1.22f;
            Vector2 destPos = _renderer.WorldToScreen(new Vec2(tableObj.X, tableObj.Y), bagLift, _camera);

            tableLerpScreen = Vector2.Lerp(sourcePos, destPos, t);
            tableLerpRotation = MathHelper.Lerp(0f, -MathF.PI / 12f, t);
        }

        if (hasCorpse && drawBagAtHilt && facingAway)
            DrawCarriedVisual(i, sp, scale);
        // Table-bound PutDown: draw the lerped corpse BEHIND the unit when facing away.
        if (tableBoundPutdown && tableLerpScreen.HasValue && facingAway)
            DrawCarriedVisualAt(i, tableLerpScreen.Value, _sim.Units[i].FacingAngle, tableLerpRotation);
        if (hasCorpse && !drawBagAtHilt && !tableBoundPutdown)
        {
            // Ground PutDown: draw at the corpse's drop point. In corpse mode use
            // the frozen carry frame (centroid-pegged) so it's identical to the
            // settled corpse that takes over at anim-finish — no hand-off jump.
            var cc = _sim.FindCorpseByID(_sim.Units[i].CarryingCorpseID);
            if (cc != null)
            {
                var groundSp = _renderer.WorldToScreen(cc.LerpStartPos, 0f, _camera);
                if (!GameConstants.UseBodyBag && cc.CarryDisplayAngle >= 0)
                    DrawCorpseCarriedFrame(cc, groundSp);
                else
                    DrawCarriedVisualAt(i, groundSp, cc.FacingAngle);
            }
        }

        // Wading visual: see Render/WadingState.cs for the math + constants.
        // All per-unit wading parameters (waterness, waterline V, top cut V,
        // diagonal slope, sprite angle) are computed in one place; the same
        // struct is used by the shadow renderer for consistency.
        WadingState wading = WadingState.Compute(
            _sim.Units[i].Position, _sim.Units[i].FacingAngle,
            fr.Frame.Value, unitDef, animData.Ctrl, _groundSystem, _camera.YRatio);
        if (wading.Active)
        {
            // World height that puts a particle drawn at the unit's foot
            // position level with the visual waterline cut on the sprite.
            // pivotFlippedV - waterlineV is the V distance from the cut up
            // to the pivot in sprite-V; multiplying by worldH gives the
            // equivalent world height, and dividing by YRatio undoes the
            // isometric squish that WorldToScreen applies to world Y.
            float pivotFlippedV = 1f - fr.Frame.Value.PivotY;
            float wakeLiftWorldH = (pivotFlippedV - wading.WaterlineV) * worldH / _camera.YRatio;

            // BACK pass — trail particles render behind the sprite so the
            // body covers anything drifting into its silhouette. Also runs
            // the per-frame update + edge-detect for entry splash spawning.
            float bodyLen = unitDef.BodyLengthWorld > 0f
                ? unitDef.BodyLengthWorld
                : (unitDef.IsQuadruped ? Render.WadingWakeSystem.QuadrupedDefaultBodyLength : 0f);
            // Use RenderPos (Position + sink offset) so wake particles
            // spawn at the body's *visual* footprint — when the unit sinks
            // into deep water, the trail and bow wave follow the sunken
            // body instead of floating above it at the sim Y.
            _wakeSystem.UpdateAndDrawBack(
                i, _frameDt,
                _sim.Units[i].RenderPos, _sim.Units[i].Velocity,
                _sim.Units[i].FacingAngle, bodyLen,
                wakeLiftWorldH, true,
                _spriteBatch, _pixel, _renderer, _camera);

            // Sprite with waterline fade. Top cut V = -1 sentinel disables the
            // top cut in the shader (used for 3/4 facings where the back-cut
            // line never read cleanly). Top slope always 0 — top cut only ever
            // applies on cardinal facings, which have no body-axis tilt.
            DrawWadingSpriteFrame(atlas, fr.Frame.Value, sp, scale, fr.FlipX, tint,
                                  wading.WaterlineV, wading.TopWaterlineV,
                                  wading.Slope, 0f);

            // FRONT pass — bow wave + entry splash render in front of the
            // sprite. Needed because for N-facing motion the "ahead of
            // unit" position projects to the same screen Y range as the
            // visible body; drawing front-class particles after the sprite
            // keeps the front foam crescent visible.
            _wakeSystem.DrawFront(i, _spriteBatch, _renderer, _camera);
        }
        else
        {
            // Out of water but live particles may still be fading. The back
            // pass advances + dims the remaining tail and catches the
            // exit-splash edge; fast-exits if no state.
            float bodyLen = unitDef.BodyLengthWorld > 0f
                ? unitDef.BodyLengthWorld
                : (unitDef.IsQuadruped ? Render.WadingWakeSystem.QuadrupedDefaultBodyLength : 0f);
            _wakeSystem.UpdateAndDrawBack(
                i, _frameDt,
                _sim.Units[i].RenderPos, _sim.Units[i].Velocity,
                _sim.Units[i].FacingAngle, bodyLen,
                0f, false,
                _spriteBatch, _pixel, _renderer, _camera);

            DrawSpriteFrame(atlas, fr.Frame.Value, sp, scale, fr.FlipX, tint);

            // Any lingering front-class particles (a bow wave fading out
            // as the unit steps onto land) also need the after-sprite pass.
            _wakeSystem.DrawFront(i, _spriteBatch, _renderer, _camera);
        }

        // F2 water debug overlay — render after the sprite so it's not occluded.
        if (_waterDebug && _smallFont != null)
            DrawWaterDebugOverlay(i, fr.Frame.Value, sp, pixelH, wading);

        // Carried visual: draw IN FRONT if facing toward camera
        if (hasCorpse && drawBagAtHilt && !facingAway)
            DrawCarriedVisual(i, sp, scale);
        // Table-bound PutDown: draw the lerped corpse IN FRONT when facing toward camera.
        if (tableBoundPutdown && tableLerpScreen.HasValue && !facingAway)
            DrawCarriedVisualAt(i, tableLerpScreen.Value, _sim.Units[i].FacingAngle, tableLerpRotation);

        // Buff visuals: phase 1 (in front of sprite)
        _buffVisuals.DrawUnit(i, renderPos, 1, _gameTime,
            _spriteBatch, _camera, _renderer, _flipbooks, _gameData.Buffs, _sim.Units,
            atlas, fr.Frame.Value, scale, fr.FlipX,
            _sim.Units[i].EffectSpawnPos2D, _sim.Units[i].EffectSpawnHeight);

        DrawHPBar(i, sp);

        // --- Status symbol (? / !) above head during notice/react events ---
        if (_sim.Units[i].StatusSymbol != 0 && _largeFont != null)
        {
            const float SymScale = 0.7f;   // ~30% smaller than _largeFont default
            const byte SymAlpha = 128;      // ~0.5 alpha
            string sym = _sim.Units[i].StatusSymbol == (byte)UnitStatusSymbol.Notice ? "?" : "!";
            Color symColor = _sim.Units[i].StatusSymbol == (byte)UnitStatusSymbol.Notice
                ? Color.FromNonPremultiplied(255, 240, 80, SymAlpha)   // yellow ?
                : Color.FromNonPremultiplied(255, 80, 60, SymAlpha);   // red !
            Color outline = Color.FromNonPremultiplied(0, 0, 0, SymAlpha);
            var textSize = _largeFont.MeasureString(sym);
            int symX = (int)(sp_upper.X - textSize.X * 0.5f);
            int symY = (int)(sp_upper.Y - textSize.Y - 0.25f * _camera.Zoom * _camera.YRatio);
            var symPos = new Vector2(symX, symY);

            // Black outline (8-way offset) for contrast and bolder look
            for (int ox = -2; ox <= 2; ox++)
                for (int oy = -2; oy <= 2; oy++)
                    if ((ox != 0 || oy != 0) && ox * ox + oy * oy <= 4)
                        _spriteBatch.DrawString(_largeFont, sym,
                            symPos + new Vector2(ox, oy), outline,
                            0f, Vector2.Zero, SymScale, SpriteEffects.None, 0f);

            // Faux-bold: draw colored fill twice with 1px horizontal offset
            _spriteBatch.DrawString(_largeFont, sym, symPos, symColor,
                0f, Vector2.Zero, SymScale, SpriteEffects.None, 0f);
            _spriteBatch.DrawString(_largeFont, sym, symPos + new Vector2(1, 0), symColor,
                0f, Vector2.Zero, SymScale, SpriteEffects.None, 0f);
        }

        // --- Feature 1: Action label above head during a committed attack/spell ---
        // Read from the generic ActionLabel field. Every archetype commit point
        // (standard melee, sweep, pounce, trample BeginCharge, ranged, spell cast)
        // writes this field — the renderer doesn't need to know about each path.
        if (_sim.Units[i].ActionLabelTimer > 0f
            && !string.IsNullOrEmpty(_sim.Units[i].ActionLabel)
            && _smallFont != null)
        {
            var weaponPos = new Vector2(sp.X + 10, sp.Y - 55);
            DrawText(_smallFont, _sim.Units[i].ActionLabel, weaponPos, Color.FromNonPremultiplied(255, 220, 140, 220));
        }

    }

    private void DrawSingleEnvObject(int i)
    {
        var obj = _envSystem.Objects[i];
        var def = _envSystem.Defs[obj.DefIndex];

        // Dissolve transition: between threshold-cross and full corruption, render
        // through the dissolve shader instead of the regular path. Shader needs
        // both textures bound; existing path can't carry a second sampler. Falls
        // through to the regular draw if either texture / the shader is missing.
        var rtCheck = _envSystem.GetObjectRuntime(i);
        if (rtCheck.CorruptionTime > 0f && !rtCheck.Corrupted && _dissolveTreeEffect != null)
        {
            if (DrawDissolvingTree(i, rtCheck)) return;
        }

        var tex = _envSystem.GetObjectTexture(i, out float alpha, out bool isOverride);
        if (tex == null) return;

        // Always compute scale from the main def texture so trap sprites render at same size.
        // For corrupted/override sprites we scale relative to the override texture itself
        // (it's a single frame, not a spritesheet, so refHeight should be its full height).
        var mainTex = _envSystem.GetDefTexture(obj.DefIndex);
        float refHeight = isOverride ? tex.Height : (mainTex != null ? mainTex.Height : tex.Height);

        // Animated spritesheet: use per-frame dimensions.
        // Skip slicing for the placeholder texture (single 32x32 swatch) and for
        // single-frame override textures (corrupted/trap sprites).
        bool usingPlaceholder = _envSystem.IsUsingPlaceholder(obj.DefIndex);
        Rectangle? sourceRect = null;
        float frameW = tex.Width;
        float frameH = tex.Height;
        if (def.IsAnimated && def.AnimTotalFrames > 1 && !usingPlaceholder && !isOverride)
        {
            int totalFrames = def.AnimTotalFrames;
            float animTime = _envSystem.GetObjectRuntime(i).AnimTime;
            int frame = Math.Clamp((int)animTime, 0, totalFrames - 1);
            sourceRect = def.GetAnimFrameRect(tex.Width, tex.Height, frame);
            frameW = sourceRect.Value.Width;
            frameH = sourceRect.Value.Height;
            refHeight = frameH; // scale relative to frame height, not full sheet
        }

        float worldH = def.SpriteWorldHeight * obj.Scale * def.Scale;
        float pixelH = worldH * _camera.Zoom;
        float scale = pixelH / refHeight;

        var screenPos = _renderer.WorldToScreen(new Vec2(obj.X, obj.Y), 0f, _camera);
        // Random per-instance horizontal flip (deterministic from seed). Mirror
        // the pivot's X so the sprite's base stays anchored at the same world point.
        bool flipX = _envSystem.ShouldFlipObject(i);
        var origin = new Vector2((flipX ? (1f - def.PivotX) : def.PivotX) * frameW, def.PivotY * frameH);

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

        // Apply weather ambient light
        tint = MultiplyColor(tint, _ambientColor);

        _spriteBatch.Draw(tex, screenPos, sourceRect, tint, rotation, origin, scale,
            flipX ? SpriteEffects.FlipHorizontally : SpriteEffects.None, 0f);

        // Build progress bar for unbuilt objects — visible from the moment of placement
        // (empty bar at 0%) so players can see "placed, awaiting construction".
        if (rt.BuildProgress < 1f)
            DrawBuildProgressBar(screenPos, rt.BuildProgress);

        // Body bag overlay for craft-tables with a corpse loaded. Drawn immediately
        // after the table sprite (deferred SpriteBatch = call order = render order),
        // so the bag is always layered on top of the table within this object's
        // depth slot. Other Y-sorted objects still occlude correctly via the outer
        // depth-list pass.
        //
        // Lift parametrization: tableWorldH × pivotY locates the visual TOP of the
        // sprite in world-elevation (artists use pivotY=0.93 to anchor near the
        // base, so 0.93×height is the height above pivot to reach the sprite's
        // top edge). The 0.92 trim pulls the bag down a hair so it overlaps the
        // tabletop instead of floating above the rim. No magic constants — every
        // factor is sourced from def fields the artist already tuned.
        if (Game.TableSystem.IsTable(def))
        {
            var ts = _envSystem.GetTableState(i);
            if (ts.HasAnyCorpse())
            {
                for (int s = 0; s < ts.CorpseSlots.Length; s++)
                {
                    if (ts.CorpseSlots[s].IsEmpty) continue;
                    float tableWorldH = def.SpriteWorldHeight * obj.Scale * def.Scale;
                    // Lift = pivotY × 1.22 (slightly higher on the tabletop).
                    // Rotation = -π/12 (CCW ~15°) — small bump back from -π/15
                    // to align with the table's true long-axis angle.
                    float bagLift = tableWorldH * def.PivotY * 1.22f;
                    var bagScreen = _renderer.WorldToScreen(new Vec2(obj.X, obj.Y), bagLift, _camera);
                    var slot = ts.CorpseSlots[s];
                    // Same lift + rotation as the body bag, but render the actual
                    // corpse sprite when the bag is mothballed.
                    if (GameConstants.UseBodyBag)
                        DrawBaggedCorpseAt(bagScreen, slot.FacingAngle, -MathF.PI / 12f);
                    else
                        DrawCorpseSpriteAt(slot.SourceUnitDefID, bagScreen, slot.FacingAngle, slot.SpriteScale, -MathF.PI / 12f);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Render a corruption-transitioning tree via the dissolve shader. Returns
    /// true if drawn; false if the caller should fall back to the regular path
    /// (e.g. live or dead texture missing).
    /// </summary>
    private bool DrawDissolvingTree(int i, in PlacedObjectRuntime rt)
    {
        var obj = _envSystem.Objects[i];
        var def = _envSystem.Defs[obj.DefIndex];

        var liveTex = _envSystem.GetDefTexture(obj.DefIndex);
        var deadTex = _envSystem.GetCorruptedTexture(i);
        if (liveTex == null || deadTex == null) return false;
        if (_envSystem.IsUsingPlaceholder(obj.DefIndex)) return false;

        // Frame 0 of the live spritesheet — we lock to frame 0 throughout the
        // dissolve so the live half doesn't keep animating as it fades.
        Rectangle frame0 = def.IsAnimated && def.AnimTotalFrames > 1
            ? def.GetAnimFrameRect(liveTex.Width, liveTex.Height, 0)
            : new Rectangle(0, 0, liveTex.Width, liveTex.Height);

        // Dest rect is sized to the dead texture (which should match per-frame
        // dimensions of the live sheet — see env_defs.json oak entries).
        float worldH = def.SpriteWorldHeight * obj.Scale * def.Scale;
        float pixelH = worldH * _camera.Zoom;
        float scale = pixelH / deadTex.Height;
        var screenPos = _renderer.WorldToScreen(new Vec2(obj.X, obj.Y), 0f, _camera);
        var origin = new Vector2(def.PivotX * deadTex.Width, def.PivotY * deadTex.Height);

        Color tint = MultiplyColor(Color.White, _ambientColor);

        // Set shader params. LiveFrameUV = frame 0 in normalized UV space.
        float u0 = frame0.X / (float)liveTex.Width;
        float v0 = frame0.Y / (float)liveTex.Height;
        float u1 = (frame0.X + frame0.Width)  / (float)liveTex.Width;
        float v1 = (frame0.Y + frame0.Height) / (float)liveTex.Height;
        float threshold = MathHelper.Clamp(rt.CorruptionTime / MathF.Max(_deathFog.CorruptionTransitionDuration, 0.01f), 0f, 1f);

        // Set effect parameters before Begin (they upload at Apply time).
        // Bind LiveSampler texture via the parameter system AND directly on the
        // GraphicsDevice slot — DesktopGL is finicky about which path actually
        // takes effect; doing both is harmless and one of them should win.
        _dissolveTreeEffect!.Parameters["LiveSampler"]?.SetValue(liveTex);
        _dissolveTreeEffect.Parameters["LiveTexture"]?.SetValue(liveTex);
        _dissolveTreeEffect.Parameters["LiveFrameUV"]?.SetValue(new Vector4(u0, v0, u1, v1));
        _dissolveTreeEffect.Parameters["Threshold"]?.SetValue(threshold);
        _dissolveTreeEffect.Parameters["Seed"]?.SetValue(obj.Seed);
        _dissolveTreeEffect.Parameters["DebugMode"]?.SetValue(_deathFog.DebugVisible ? 1f : 0f);

        // Throttled per-instance log so we can confirm threshold animates over time.
        if (!_dissolveLoggedSeeds.TryGetValue(i, out var lastLogged) ||
            MathF.Abs(threshold - lastLogged) >= 0.1f || (threshold >= 0.99f && lastLogged < 0.99f))
        {
            _dissolveLoggedSeeds[i] = threshold;
            DebugLog.Log("startup", $"Dissolve frame: obj {i} ({def.Id}) t={threshold:F3} CorruptionTime={rt.CorruptionTime:F3} liveTex={liveTex.Width}x{liveTex.Height} deadTex={deadTex.Width}x{deadTex.Height}");
        }

        // Flush the env-objects batch and start an Immediate batch with our effect.
        _spriteBatch.End();
        _spriteBatch.Begin(SpriteSortMode.Immediate, BlendState.AlphaBlend, SamplerState.LinearClamp,
            null, null, _dissolveTreeEffect);

        // Belt-and-suspenders: also bind directly to GraphicsDevice slot 1.
        GraphicsDevice.Textures[1] = liveTex;
        GraphicsDevice.SamplerStates[1] = SamplerState.LinearClamp;

        _spriteBatch.Draw(deadTex, screenPos, null, tint, 0f, origin, scale, SpriteEffects.None, 0f);
        _spriteBatch.End();
        // Restore the wrapping batch — the scene-pass Begin (Game1.cs ~line 4220)
        // uses LinearClamp, NOT PointClamp. Restoring with PointClamp would alter
        // the rest of the scene's sampler state.
        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp);
        return true;
    }
    private readonly Dictionary<int, float> _dissolveLoggedSeeds = new();

    /// <summary>
    /// Draw a unit's Idle (or fallback) first-frame sprite scaled to fit inside
    /// `dest`. Used by the table craft menu to show what's loaded in each corpse
    /// slot. Returns silently if the def is missing or its atlas isn't loaded —
    /// caller can render its own placeholder when nothing was drawn.
    /// </summary>
    private void DrawUnitIdleSprite(string unitDefId, Rectangle dest)
    {
        if (string.IsNullOrEmpty(unitDefId) || _gameData == null)
        {
            DebugLog.Log("table", $"[DrawUnitIdleSprite] aborted: defId='{unitDefId}' gameData={_gameData != null}");
            return;
        }
        var unitDef = _gameData.Units.Get(unitDefId);
        if (unitDef?.Sprite == null)
        {
            DebugLog.Log("table", $"[DrawUnitIdleSprite] '{unitDefId}': unitDef={unitDef != null} sprite={unitDef?.Sprite != null}");
            return;
        }
        var atlasId = AtlasDefs.ResolveAtlasName(unitDef.Sprite.AtlasName);
        if (atlasId >= _atlases.Length)
        {
            DebugLog.Log("table", $"[DrawUnitIdleSprite] '{unitDefId}': atlasId={atlasId} out of range (atlases={_atlases.Length})");
            return;
        }
        var atlas = _atlases[atlasId];
        if (!atlas.IsLoaded)
        {
            DebugLog.Log("table", $"[DrawUnitIdleSprite] '{unitDefId}': atlas '{unitDef.Sprite.AtlasName}' not loaded");
            return;
        }

        var spriteData = atlas.GetUnit(unitDef.Sprite.SpriteName);
        if (spriteData == null)
        {
            DebugLog.Log("table", $"[DrawUnitIdleSprite] '{unitDefId}': spriteName '{unitDef.Sprite.SpriteName}' not in atlas");
            return;
        }

        // Prefer the dedicated "Icon" pose — a single camera-facing frame
        // (yaw 45 faces the viewer; verified visually against the atlas).
        // Units without an Icon pose fall back to Idle with the angle
        // preference list (different units author different angle keys —
        // old scheme: 30/60/300; new scheme: 0/45/90/270/315), then to ANY
        // authored angle (mirrors AnimController.ResolveFallbackAngle).
        var anim = spriteData.GetAnim("Icon");
        int[] anglePrefs = anim != null
            ? new[] { 45, 0, 315, 90, 270 }
            : new[] { 30, 0, 45, 60, 315, 90, 270, 300 };
        anim ??= spriteData.GetAnim("Idle");
        if (anim == null)
        {
            DebugLog.Log("table", $"[DrawUnitIdleSprite] '{unitDefId}': no Icon/Idle anim in spriteData");
            return;
        }
        System.Collections.Generic.List<Render.Keyframe>? kfs = null;
        foreach (int pref in anglePrefs)
        {
            kfs = anim.GetAngle(pref);
            if (kfs != null && kfs.Count > 0) break;
        }
        if (kfs == null || kfs.Count == 0)
        {
            // Last resort: take whatever is in the dictionary first.
            foreach (var (_, frames) in anim.AngleFrames)
            {
                if (frames.Count > 0) { kfs = frames; break; }
            }
        }
        if (kfs == null || kfs.Count == 0)
        {
            DebugLog.Log("table", $"[DrawUnitIdleSprite] '{unitDefId}': no usable angle keyframes (authored angles: {string.Join(",", anim.AngleFrames.Keys)})");
            return;
        }

        var frame = kfs[0].Frame;
        var tex = atlas.GetTextureForFrame(frame);
        if (tex == null)
        {
            DebugLog.Log("table", $"[DrawUnitIdleSprite] '{unitDefId}': frame texture null");
            return;
        }

        // Fit-inside scale: clamp to the smaller axis so the sprite preserves
        // aspect ratio and never crops out of the slot rect.
        float fitW = (float)(dest.Width - 4) / frame.Rect.Width;
        float fitH = (float)(dest.Height - 4) / frame.Rect.Height;
        float scale = MathF.Min(fitW, fitH);

        // Centered draw — origin at sprite center so we can position by box center.
        var origin = new Vector2(frame.Rect.Width / 2f, frame.Rect.Height / 2f);
        var center = new Vector2(dest.X + dest.Width / 2f, dest.Y + dest.Height / 2f);
        _spriteBatch.Draw(tex, center, frame.Rect, Color.White, 0f, origin, scale,
            SpriteEffects.None, 0f);
    }

    private void DrawSpriteFrame(SpriteAtlas atlas, SpriteFrame frame, Vector2 screenPos,
                                  float scale, bool flipX, Color tint)
    {
        var tex = atlas.GetTextureForFrame(frame);
        if (tex == null) return;

        float pivotX = flipX ? (1f - frame.PivotX) : frame.PivotX;
        // Spritemeta pivots use bottom-left origin — Y needs to be flipped for top-left rendering
        float pivotY = 1f - frame.PivotY;

        var origin = new Vector2(pivotX * frame.Rect.Width, pivotY * frame.Rect.Height);
        var effects = flipX ? SpriteEffects.FlipHorizontally : SpriteEffects.None;
        _spriteBatch.Draw(tex, screenPos, frame.Rect, tint, 0f, origin, scale, effects, 0f);
    }

    /// <summary>Draw a unit sprite with the wading shader applied — fades alpha
    /// below <paramref name="waterlineV"/> (V coord, 0=top, 1=bottom) and adds a
    /// foam smear at the line. Wraps the call in End()/Begin(effect)/End()/
    /// Begin() so other sprites this frame keep using the default batch. Caller
    /// should only invoke when the unit is actually in shallow water — every
    /// invocation pays the cost of two batch transitions.</summary>
    /// <summary>F2 overlay: draws the body bbox and the computed waterline
    /// cut directly on top of the rendered unit, plus a small text label
    /// with the raw + remapped waterness, slope, and fractions in play. Used
    /// for tuning per-unit WadingFractionByDirection values without bouncing
    /// between game and JSON editor.</summary>
    private void DrawWaterDebugOverlay(int unitIdx, SpriteFrame frame, Vector2 sp,
                                        float pixelH, in WadingState wading)
    {
        var unit = _sim.Units[unitIdx];
        // Body bbox bounds in screen Y.
        float pivotFlippedV = 1f - frame.PivotY;
        float bodyTopY = sp.Y + (frame.BodyTopV - pivotFlippedV) * pixelH;
        float bodyBotY = sp.Y + (frame.BodyBottomV - pivotFlippedV) * pixelH;

        // Estimate body width in screen px (no real frame X bounds — use the
        // sprite frame's PixelW. Approximation: assume body roughly spans the
        // frame minus 20% padding on each side; close enough for an overlay).
        float pixelW = frame.Rect.Width * (pixelH / frame.Rect.Height);
        float bodyHalfW = pixelW * 0.4f;
        var bboxCol = new Color(80, 200, 255, 110);
        DrawRectOutline(new Rectangle(
            (int)(sp.X - bodyHalfW), (int)bodyTopY,
            (int)(bodyHalfW * 2),    (int)(bodyBotY - bodyTopY)),
            bboxCol);

        // Bottom waterline (with slope) as a short line across the body.
        if (wading.Active)
        {
            float waterY = sp.Y + (wading.WaterlineV - pivotFlippedV) * pixelH;
            // Slope is dV/dU in local frame UV; convert: dY/dX in screen.
            float slopeYpx = wading.Slope * pixelH / pixelW;
            float lineHalfW = bodyHalfW;
            var lineCol = new Color(255, 100, 100, 200);
            DrawLine(new Vector2(sp.X - lineHalfW, waterY - lineHalfW * slopeYpx),
                     new Vector2(sp.X + lineHalfW, waterY + lineHalfW * slopeYpx),
                     lineCol, 2);

            // Top waterline (if active).
            if (wading.TopWaterlineV >= 0f)
            {
                float topY = sp.Y + (wading.TopWaterlineV - pivotFlippedV) * pixelH;
                var topCol = new Color(255, 180, 100, 200);
                DrawLine(new Vector2(sp.X - lineHalfW, topY - lineHalfW * slopeYpx),
                         new Vector2(sp.X + lineHalfW, topY + lineHalfW * slopeYpx),
                         topCol, 2);
            }
        }

        // Text label above the sprite.
        var unitDef = _gameData.Units.Get(unit.UnitDefID);
        string topStr = wading.TopWaterlineV >= 0f ? $"topV={wading.TopWaterlineV:F2}" : "topV=-";
        string label = wading.Active
            ? $"w={wading.Waterness:F2} V={wading.WaterlineV:F2} {topStr} s={wading.Slope:F2} ang={wading.SpriteAngle}"
            : $"w=0  (dry)  ang={wading.SpriteAngle}";
        DrawText(_smallFont, label,
            new Vector2((int)(sp.X - 60), (int)(bodyTopY - 14)),
            new Color(255, 255, 255, 220));
    }

    /// <summary>Draw a 1px outline rectangle.</summary>
    private void DrawRectOutline(Rectangle r, Color c)
    {
        _spriteBatch.Draw(_pixel, new Rectangle(r.X, r.Y, r.Width, 1), c);
        _spriteBatch.Draw(_pixel, new Rectangle(r.X, r.Y + r.Height - 1, r.Width, 1), c);
        _spriteBatch.Draw(_pixel, new Rectangle(r.X, r.Y, 1, r.Height), c);
        _spriteBatch.Draw(_pixel, new Rectangle(r.X + r.Width - 1, r.Y, 1, r.Height), c);
    }

    /// <summary>Draw a 2D line by rotating the _pixel sprite. Cheap, AA-free.</summary>
    private void DrawLine(Vector2 a, Vector2 b, Color c, int thickness)
    {
        var d = b - a;
        float len = d.Length();
        if (len < 0.5f) return;
        float angle = MathF.Atan2(d.Y, d.X);
        _spriteBatch.Draw(_pixel, a, null, c, angle, Vector2.Zero,
            new Vector2(len, thickness), SpriteEffects.None, 0f);
    }

    private void DrawWadingSpriteFrame(SpriteAtlas atlas, SpriteFrame frame, Vector2 screenPos,
                                        float scale, bool flipX, Color tint,
                                        float waterlineCenterV, float topWaterlineCenterV,
                                        float waterlineSlope, float topWaterlineSlope)
    {
        var tex = atlas.GetTextureForFrame(frame);
        if (tex == null) return;
        if (_wadingEffect == null)
        {
            // Fall back to normal draw if shader missing — at least the unit is
            // still visible; just no waterline effect.
            DrawSpriteFrame(atlas, frame, screenPos, scale, flipX, tint);
            return;
        }

        // Atlas U/V range of this frame — shader uses them to normalize the
        // incoming atlas texCoord into local 0..1 frame UV.
        float atlasW = (float)tex.Width;
        float atlasH = (float)tex.Height;
        float frameLeftU = frame.Rect.X / atlasW;
        float frameRightU = (frame.Rect.X + frame.Rect.Width) / atlasW;
        float frameTopV = frame.Rect.Y / atlasH;
        float frameBotV = (frame.Rect.Y + frame.Rect.Height) / atlasH;
        _wadingEffect.Parameters["FrameLeftU"]?.SetValue(frameLeftU);
        _wadingEffect.Parameters["FrameRightU"]?.SetValue(frameRightU);
        _wadingEffect.Parameters["FrameTopV"]?.SetValue(frameTopV);
        _wadingEffect.Parameters["FrameBottomV"]?.SetValue(frameBotV);

        // No flipX correction on the slope: SpriteBatch flipping reverses
        // texCoord.x sweep through the atlas frame, which gives the shader a
        // naturally-reversed localU. Passing slope as-is and letting the
        // reversed localU flip it produces the right output diagonal for
        // mirrored sprites. (Earlier code negated here, which double-flipped
        // and caused NW to show the same diagonal direction as NE.)
        _wadingEffect.Parameters["WaterlineCenterV"]?.SetValue(waterlineCenterV);
        _wadingEffect.Parameters["WaterlineSlope"]?.SetValue(waterlineSlope);
        _wadingEffect.Parameters["TopWaterlineCenterV"]?.SetValue(topWaterlineCenterV);
        _wadingEffect.Parameters["TopWaterlineSlope"]?.SetValue(topWaterlineSlope);
        _wadingEffect.Parameters["FoamHalfWidth"]?.SetValue(0.05f);
        _wadingEffect.Parameters["TopFoamHalfWidth"]?.SetValue(0.05f);
        _wadingEffect.Parameters["UnderwaterAlpha"]?.SetValue(0.0f);
        _wadingEffect.Parameters["FoamColor"]?.SetValue(new Vector3(0.88f, 0.94f, 0.96f));

        _spriteBatch.End();
        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp,
            null, null, _wadingEffect);

        float pivotX = flipX ? (1f - frame.PivotX) : frame.PivotX;
        float pivotY = 1f - frame.PivotY;
        var origin = new Vector2(pivotX * frame.Rect.Width, pivotY * frame.Rect.Height);
        var effects = flipX ? SpriteEffects.FlipHorizontally : SpriteEffects.None;
        _spriteBatch.Draw(tex, screenPos, frame.Rect, tint, 0f, origin, scale, effects, 0f);

        _spriteBatch.End();
        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp);
    }

    /// <summary>Multiply two colors component-wise (for ambient tinting).</summary>
    private static Color MultiplyColor(Color a, Color b)
    {
        return new Color(
            (byte)(a.R * b.R / 255),
            (byte)(a.G * b.G / 255),
            (byte)(a.B * b.B / 255),
            (byte)(a.A * b.A / 255));
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
        var tex = atlas.GetTextureForFrame(frame);
        if (tex == null || _outlineFlatEffect == null) return;

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
            _spriteBatch.Draw(tex, new Vector2(screenPos.X + dx, screenPos.Y + dy),
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
        int maxHp = BuffSystem.EffectiveMaxHP(_sim.Units, unitIdx);
        if (maxHp <= 0) return;

        float hpRatio = (float)stats.HP / maxHp;
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
            // Fog of war: hide projectile if its current tile isn't visible.
            if (!_fogOfWar.IsVisible(proj.Position)) continue;

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
            if (!_fogOfWar.IsVisible(proj.Position)) continue;
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
            // Fog of war: hide damage numbers whose position is in fog. This covers
            // the "from non-undead" case — numbers pinned to hidden enemies don't
            // render, while numbers appearing on your own (visible) units do.
            if (!_fogOfWar.IsVisible(dn.WorldPos)) continue;
            var sp = _renderer.WorldToScreen(dn.WorldPos, dn.Height, _camera);
            byte alpha = (byte)(255 * fade);

            // Pickup text or damage number. Alerts (e.g. "Horde Full") render
            // raw — no "+" prefix — since they're not a numeric gain.
            string text;
            if (dn.PickupText != null)
                text = dn.IsAlert ? dn.PickupText : $"+{dn.PickupText}";
            else
                text = dn.Damage.ToString();
            var size = _font.MeasureString(text) * dnScale;
            var pos = new Vector2(sp.X - size.X / 2f, sp.Y - size.Y / 2f);

            // Shadow pass
            var shadowColor = new Color((byte)0, (byte)0, (byte)0, alpha);
            _spriteBatch.DrawString(_font, text, new Vector2(pos.X + 1f, pos.Y + 1f), shadowColor,
                0f, Vector2.Zero, dnScale, SpriteEffects.None, 0f);

            // Text pass — alert=red, pickup=gold, poison=green, fatigue=blue, else DamageNumberColor
            Color color;
            if (dn.IsAlert)
                color = Color.FromNonPremultiplied(255, 80, 80, alpha);
            else if (dn.PickupText != null)
                color = Color.FromNonPremultiplied(255, 220, 100, alpha);
            else if (dn.IsPoison)
                color = Color.FromNonPremultiplied(40, 200, 40, alpha);
            else if (dn.IsFatigue)
                color = Color.FromNonPremultiplied(80, 140, 255, alpha);
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
            DrawSpellCategoryIcon, BuildMenuOpenMask(), _paused);
    }

    /// <summary>Bitmask of which core menus are open, by HUDRenderer.Menu* index,
    /// for highlighting the top-right menu buttons.</summary>
    private int BuildMenuOpenMask()
    {
        int m = 0;
        if (_inventoryUI.IsVisible)     m |= 1 << HUDRenderer.MenuInventory;
        if (_craftingMenu.IsVisible)    m |= 1 << HUDRenderer.MenuCrafting;
        if (_buildingMenuUI.IsVisible)  m |= 1 << HUDRenderer.MenuBuilding;
        if (_grimoireOverlay.IsVisible) m |= 1 << HUDRenderer.MenuGrimoire;
        if (_skillBookOverlay.IsVisible) m |= 1 << HUDRenderer.MenuSkills;
        if (_characterStatsUI.IsVisible) m |= 1 << HUDRenderer.MenuCharacter;
        return m;
    }

    /// <summary>Toggle a core menu by its HUDRenderer.Menu* index — the click-side
    /// mirror of the keyboard shortcuts (I/C/B/J/K/Tab), including the
    /// building↔crafting mutual-close.</summary>
    private void ToggleCoreMenu(int idx, int screenW, int screenH)
    {
        EnsureInventoryUIsInitialized();
        switch (idx)
        {
            case HUDRenderer.MenuInventory:
                _inventoryUI.Toggle(screenW, screenH);
                break;
            case HUDRenderer.MenuCrafting:
                if (_buildingMenuUI.IsVisible) _buildingMenuUI.Close();
                _craftingMenu.Toggle(screenW, screenH);
                break;
            case HUDRenderer.MenuBuilding:
                if (_craftingMenu.IsVisible) _craftingMenu.Close();
                _buildingMenuUI.Toggle(screenW, screenH);
                break;
            case HUDRenderer.MenuGrimoire:
                _grimoireOverlay.Toggle();
                break;
            case HUDRenderer.MenuSkills:
                _skillBookOverlay.Toggle();
                break;
            case HUDRenderer.MenuCharacter:
                _characterStatsUI.Toggle();
                break;
        }
    }

    /// <summary>Draw the aggression bar (Shift+Q/Shift+E controlled) centered just
    /// above the secondary spell bar, with a warm token on the active level's node.
    /// The bar size AND the node positions are read live from the widget def, so the
    /// token tracks any resize / re-spacing of the bar in the UI editor. Both
    /// DrawWidget and DrawCircle manage their own SpriteBatch, so this runs outside
    /// any active batch (right after DrawHUD).</summary>
    private void DrawAggressionBar(int screenW, int screenH)
    {
        if (!GetAggressionBarLayout(screenW, screenH, out var bar, out var nodes)) return;

        _widgetRenderer.DrawWidget("AggressionBar", bar.X, bar.Y);

        // Token: the dot lands on the active node no matter how the bar is sized or
        // spaced (layout read live, see GetAggressionBarLayout). DrawWidget left the
        // batch closed; DrawCircle does its own Begin/End.
        int level = Math.Clamp(_sim.Horde.AggressionLevel, 0, nodes.Count - 1);
        var nr = nodes[level];
        var center = new Vector2(nr.X + nr.Width / 2f, nr.Y + nr.Height / 2f);
        float radius = MathF.Max(4f, nr.Width * 0.32f); // token scales with the node
        var fill = new Color(255, 196, 64); // vivid gold accent
        _uiShaders.DrawCircle(_spriteBatch, center, radius, radius * 1.7f, fill, fill, new Color(255, 196, 64, 120));

        DrawAggressionTooltip(screenW, screenH, bar, nodes);
    }

    // Aggression level names + one-line descriptions, indexed 0 (least) .. 4 (most).
    private static readonly string[] AggroNames =
        { "Defensive", "Cautious", "Balanced", "Aggressive", "Bloodthirsty" };
    private static readonly string[] AggroDescs =
    {
        "Hold tight - strike only enemies at the formation's edge.",
        "Engage threats just beyond the formation.",
        "Balanced engagement and leash (default).",
        "Press forward - wider engagement, longer leash.",
        "Engagement and leash doubled - chase enemies far.",
    };

    /// <summary>Screen-space layout of the aggression bar: the bar rect and each
    /// level node's rect (index 0 = leftmost = least aggressive), read live from the
    /// widget def so input hit-testing and drawing share one source of truth.
    /// Returns false when the bar is hidden (a menu is open) or the def is missing.</summary>
    private bool GetAggressionBarLayout(int screenW, int screenH,
        out Rectangle bar, out System.Collections.Generic.List<Rectangle> nodes)
    {
        bar = default;
        nodes = null!;
        if (_menuState != MenuState.None) return false;

        var barDef = _widgetRenderer.GetWidgetDef("AggressionBar");
        if (barDef == null) return false;

        var sec = _hudRenderer.GetSecondaryBarLayout(screenH);
        int x = (screenW - barDef.Width) / 2;
        int y = sec.barY - barDef.Height - 6; // sit just above the secondary (1-6) bar
        bar = new Rectangle(x, y, barDef.Width, barDef.Height);

        nodes = new System.Collections.Generic.List<Rectangle>();
        foreach (var c in barDef.Children.Where(c => c.Widget == "CircularToggle").OrderBy(c => c.X))
            nodes.Add(new Rectangle(x + c.X, y + c.Y, c.Width, c.Height));
        return nodes.Count > 0;
    }

    /// <summary>Index of the node whose center is closest (by X) to the cursor —
    /// lets a click anywhere along the bar snap to the nearest level.</summary>
    private static int NearestAggroNode(System.Collections.Generic.List<Rectangle> nodes, int mx)
    {
        int best = 0, bestD = int.MaxValue;
        for (int i = 0; i < nodes.Count; i++)
        {
            int d = Math.Abs(nodes[i].Center.X - mx);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    /// <summary>Hover tooltip for the aggression bar: names the level the cursor is
    /// over (the one a click would select) and what it does. Called right after the
    /// token's DrawCircle, which leaves a SpriteBatch open — so we draw into that
    /// active batch rather than starting our own (a nested Begin would throw).</summary>
    private void DrawAggressionTooltip(int screenW, int screenH,
        Rectangle bar, System.Collections.Generic.List<Rectangle> nodes)
    {
        if (_smallFont == null) return;

        var hover = bar;
        hover.Inflate(0, 8); // a little vertical slack makes the thin bar easier to hit
        int mx = (int)_input.MousePos.X, my = (int)_input.MousePos.Y;
        if (!hover.Contains(mx, my)) return;

        int idx = Math.Clamp(NearestAggroNode(nodes, mx), 0, AggroNames.Length - 1);
        string title = $"Aggression: {AggroNames[idx]}";
        string desc = AggroDescs[idx];
        string hint = "Click to set  -  Shift+Q / Shift+E";

        int lineH = _smallFont.LineSpacing;
        int pad = 8;
        int innerW = (int)MathF.Ceiling(MathF.Max(_smallFont.MeasureString(title).X,
            MathF.Max(_smallFont.MeasureString(desc).X, _smallFont.MeasureString(hint).X)));
        int w = innerW + pad * 2;
        int h = pad * 2 + lineH * 3 + 4;

        int tx = bar.X + (bar.Width - w) / 2;
        int ty = bar.Y - h - 6;
        tx = Math.Clamp(tx, 4, Math.Max(4, screenW - w - 4));
        if (ty < 4) ty = bar.Bottom + 6; // flip below if it would clip off the top

        _spriteBatch.Draw(_pixel, new Rectangle(tx, ty, w, h), new Color(20, 16, 12, 235));
        _spriteBatch.Draw(_pixel, new Rectangle(tx, ty, w, 2), new Color(120, 95, 60));
        int cy = ty + pad;
        DrawText(_smallFont, title, new Vector2(tx + pad, cy), new Color(255, 210, 130));
        cy += lineH;
        DrawText(_smallFont, desc, new Vector2(tx + pad, cy), new Color(210, 200, 185));
        cy += lineH + 4;
        DrawText(_smallFont, hint, new Vector2(tx + pad, cy), new Color(140, 130, 115));
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
