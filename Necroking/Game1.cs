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

public partial class Game1 : Microsoft.Xna.Framework.Game
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
    /// <summary>Nearest Empty Grave (IsWorkerHome def) under the cursor, or -1.</summary>
    private int FindGraveUnderCursor(Vec2 mouseWorld, float clickRange = 1.6f)
    {
        if (_envSystem == null) return -1;
        int best = -1; float bestSq = clickRange * clickRange;
        for (int i = 0; i < _envSystem.ObjectCount; i++)
        {
            var def = _envSystem.GetDef(_envSystem.GetObject(i).DefIndex);
            var rt = _envSystem.GetObjectRuntime(i);
            if (!def.IsWorkerHome || !rt.Alive || rt.BuildProgress < 1f) continue;
            var o = _envSystem.GetObject(i);
            float sq = (new Vec2(o.X, o.Y) - mouseWorld).LengthSq();
            if (sq < bestSq) { bestSq = sq; best = i; }
        }
        return best;
    }

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
        // Slot click: deposit into an open crafting table, else use a consumable
        // (e.g. Skillpoint Potion). Handled via the layer callback because the
        // inventory is a modal layer — PopupManager consumes inside-panel clicks
        // before Game1.Update's own handlers run.
        _inventoryUI.OnSlotClicked = slotIdx =>
        {
            var s = _inventory.GetSlot(slotIdx);
            if (s.IsEmpty) return;
            if (_tableMenuUI.IsVisible)
                _tableMenuUI.TryDepositItem(s.ItemId);
            else if (TryConsumeInventoryItem(s.ItemId))
                _inventory.RemoveItem(s.ItemId, 1);
        };
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
        _graveRosterUI.Init(_spriteBatch, _pixel, _widgetRenderer, _workerSystem);
        _jobBoardUI.Init(_spriteBatch, _pixel, _widgetRenderer, _workerSystem);
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
    private Microsoft.Xna.Framework.Graphics.Effect? _morphSdfEffect; // reanimation SDF body morph
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

    // Window mode (Alt+Enter toggles). Default false = the borderless windowed
    // "fullscreen" set up in the constructor (with the +1 height trick that keeps
    // the OS from treating it as exclusive fullscreen, which breaks screenshots).
    // true = a normal resizable bordered window.
    private bool _windowedMode;
    private int _windowedW = 1280, _windowedH = 720;
    private bool _handlingResize;
    private Color _ambientColor = Color.White; // weather ambient tint, applied to lit sprites before bloom
    private DayNightSystem _dayNightSystem = new();
    private LightningRenderer _lightningRenderer = new();
    private PoisonCloudRenderer _poisonCloudRenderer = new();
    private DeathFogRenderer _deathFogRenderer = new();
    private ReanimEffectSystem _reanimFx = new();
    private Render.ReanimMorph _reanimMorph = new();   // SDF body-morph data cache for the reanimation rise
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

    // Per-slot "just activated" flash timers (seconds remaining), decayed in real
    // time. Set when a slot successfully fires a spell; the HUD draws a fading
    // highlight so a keypress visibly lights up its hotbar slot. Duration is owned
    // by HUDRenderer.SlotFlashDuration (single source of truth for the fade math).
    private readonly float[] _primarySlotFlash = new float[4];
    private readonly float[] _secondarySlotFlash = new float[6];

    // Dev cursor override for headless hover testing (set via the `mousepos` dev command).
    private Microsoft.Xna.Framework.Vector2? _devMouseOverride;
    private int _spellDropdownSlot = -1;
    private int _secondaryDropdownSlot = -1;
    private int _channelingSlot = -1;
    // Which bar the channeling slot belongs to. The hold key differs by bar (primary
    // Q/E/LMB/RMB vs secondary D1..D6), so the slot index alone can't identify the input.
    private bool _channelingIsSecondary;
    private readonly Dictionary<string, Texture2D?> _itemTextureCache = new();
    private int _hoveredObjectIdx = -1;
    private int _hoveredCorpseIdx = -1;
    private int _hoveredUnitIdx = -1;
    // Screen-space outline boxes for the hovered world object, captured during the
    // sprite draw pass (exact sprite bounds) and drawn in the HUD overlay pass.
    // Reset to null each frame at the top of Draw. See ShowHoverHighlight setting.
    private Rectangle? _hoverBoxObject, _hoverBoxCorpse, _hoverBoxUnit;
    // --- Hover-highlight style variant cycling (design test harness; cycle with 'H') ----
    // 13 states: 0-11 = 3 shapes (Circle / Corners / Rectangle) × 4 line styles
    // (Thick-Solid / Thin-Solid / Thick-Faint / Thin-Faint); 12 = Off.
    // shape = variant / 4, style = variant % 4. Default 11 ≈ the original look (Rect, thin, faint).
    private int _hoverHighlightVariant = 11;
    private float _hoverVariantLabelTimer;     // seconds left to show the "which variant" toast
    // Dev: pin the hovered unit (headless testing has no real mouse). uint.MaxValue = off.
    private uint _devForceHoverUnitId = uint.MaxValue;
    // Dev: pin the hovered env object by index (headless variant testing). -1 = off.
    private int _devForceHoverObjectIdx = -1;
    // Dev: detach the camera from the necromancer so dev 'camera' commands stick
    // (lets headless testing pan freely WITHOUT killing the necromancer / triggering game-over).
    private bool _devFreeCamera;
    // Dev-marked units (via the 'mark' dev command): persistent white boxes,
    // independent of mouse hover and the ShowHoverHighlight setting. Keyed by
    // stable unit Id; their on-screen boxes are recaptured each frame in Draw.
    private readonly HashSet<uint> _devMarkedUnitIds = new();
    private readonly List<Rectangle> _devMarkBoxes = new();
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
        => castAnim == "ImbueGround" || castAnim == "Raise" || castAnim == "ImbueTable";

    /// <summary>Resolve the Start/Loop/Finish anim states for a channeled cast.
    /// Raise has no Finish (finish == null → go straight to Idle after the loop).</summary>
    private static void GetChannelStates(string castAnim, out AnimState start, out AnimState loop, out AnimState? finish)
    {
        switch (castAnim)
        {
            case "Raise":
                start = AnimState.RaiseStart; loop = AnimState.RaiseLoop; finish = null; break;
            case "ImbueTable":
                // The over-the-corpse "working at a table" animation, but cast on a
                // loose ground corpse (no table). Start→Loop→Finish like ImbueGround.
                start = AnimState.ImbueTableStart; loop = AnimState.ImbueTableLoop; finish = AnimState.ImbueTableFinish; break;
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
    // Dev/test: persistent synthetic editor mouse (ed_mouse down/up, ed_mouse_off) for click-leak tests.
    // Held across frames so press/release edges survive multiple Updates-per-Draw.
    private bool _devMouseActive, _devMouseDown;
    private int _devMouseX, _devMouseY;
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

    // Lean dev control server (Necroking/Dev/DevServer.cs). Non-null only when
    // launched with --devserver <port>. Commands are queued on its listener
    // thread and executed here on the main thread via ExecuteDevCommand.
    private Necroking.Dev.DevServer? _devServer;
    private bool _taskbarHidden;                   // headless: off-screen window's taskbar button dropped once
    private string? _pendingDevScreenshot;        // set by a screenshot cmd, consumed in Draw
    private Necroking.Dev.DevCommand? _pendingDevScreenshotCmd; // completed once the PNG is written
    private int _devShotW, _devShotH;             // downsample target for the pending shot (0 = native)
    private bool _devShotNoUi, _devShotNoGround;  // suppress UI / ground for the pending shot's frame
    private Necroking.Dev.DevJob? _devJob;        // active batch script (stepped each frame in Update)
    private int _devJobSeq;                        // id counter for batch jobs
    private Vec2? _devWalkTarget;                  // dev "walk_necro" goal; drives WASD-equivalent input, cancelled by any WASD press
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
    private readonly Game.Jobs.WorkerSystem _workerSystem = new();
    private readonly UI.GraveRosterUI _graveRosterUI = new();
    private readonly UI.JobBoardUI _jobBoardUI = new();
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

        // Recreate screen-sized render targets when the window resizes (e.g. the
        // user drags the edge in windowed mode). Fog-of-war targets are world-sized
        // so they're untouched; only bloom + weather follow the back buffer.
        Window.ClientSizeChanged += OnClientSizeChanged;

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

    /// <summary>Alt+Enter: flip between the borderless windowed "fullscreen" and a
    /// normal resizable window.</summary>
    private void ToggleWindowMode() => ApplyWindowMode(!_windowedMode);

    /// <summary>Apply windowed (bordered, resizable) or borderless-fullscreen mode.
    /// Borderless keeps the +1 height trick so the OS doesn't switch to exclusive
    /// fullscreen (which breaks screenshots and the dev-server frame capture).</summary>
    private void ApplyWindowMode(bool windowed)
    {
        if (LaunchArgs.Headless) return; // headless owns its own tiny hidden window
        _windowedMode = windowed;
        var dm = GraphicsAdapter.DefaultAdapter.CurrentDisplayMode;
        if (windowed)
        {
            Window.IsBorderless = false;
            Window.AllowUserResizing = true;
            _graphics.IsFullScreen = false;
            _graphics.PreferredBackBufferWidth = _windowedW;
            _graphics.PreferredBackBufferHeight = _windowedH;
            _graphics.ApplyChanges();
            // Center on the primary display.
            Window.Position = new Point(
                Math.Max(0, (dm.Width - _windowedW) / 2),
                Math.Max(0, (dm.Height - _windowedH) / 2));
        }
        else
        {
            // Borderless windowed "fullscreen" with the +1 height trick.
            Window.AllowUserResizing = false;
            Window.IsBorderless = true;
            _graphics.IsFullScreen = false;
            _graphics.PreferredBackBufferWidth = dm.Width;
            _graphics.PreferredBackBufferHeight = dm.Height + 1;
            _graphics.ApplyChanges();
            Window.Position = Point.Zero;
        }
        RefreshScreenSizedTargets();
    }

    /// <summary>Resize the back-buffer-sized render targets (bloom, weather) and the
    /// renderer's screen dims to the current viewport. Cheap no-ops before those
    /// systems are initialized, so it's safe to call early.</summary>
    private void RefreshScreenSizedTargets()
    {
        if (GraphicsDevice == null) return;
        int w = GraphicsDevice.Viewport.Width, h = GraphicsDevice.Viewport.Height;
        if (w <= 0 || h <= 0) return;
        _renderer.SetScreenSize(w, h);
        _bloom.Resize(GraphicsDevice, w, h);
        _weatherRenderer.Resize(w, h);
    }

    private void OnClientSizeChanged(object? sender, EventArgs e)
    {
        if (_handlingResize || GraphicsDevice == null) return;
        _handlingResize = true;
        try
        {
            int w = Window.ClientBounds.Width, h = Window.ClientBounds.Height;
            if (w <= 0 || h <= 0) return;
            // In windowed mode, follow the user's drag and remember the new size so
            // toggling back to windowed later restores it.
            if (_windowedMode)
            {
                _windowedW = w; _windowedH = h;
                if (_graphics.PreferredBackBufferWidth != w || _graphics.PreferredBackBufferHeight != h)
                {
                    _graphics.PreferredBackBufferWidth = w;
                    _graphics.PreferredBackBufferHeight = h;
                    _graphics.ApplyChanges();
                }
            }
            RefreshScreenSizedTargets();
        }
        finally { _handlingResize = false; }
    }

    protected override void Initialize()
    {
        DebugLog.Clear("startup");
        DebugLog.Log("startup", "=== Necroking MG Startup ===");

        // Register AI archetypes
        AI.ArchetypeRegistry.Register(AI.ArchetypeRegistry.PlayerControlled, "PlayerControlled", new AI.PlayerControlledHandler());
        AI.ArchetypeRegistry.Register(AI.ArchetypeRegistry.WolfPack, "WolfPack", new AI.WolfPackHandler());
        AI.ArchetypeRegistry.Register(AI.ArchetypeRegistry.RatPack, "RatPack", new AI.RatPackHandler());
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
        AI.ArchetypeRegistry.Register(AI.ArchetypeRegistry.Worker, "Worker", new AI.WorkerHandler());
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

    private void StartGame(string mapName = "default")
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
        // Reset the worker job system: reload jobs.json, wipe stockpiles + assignments
        // so a fresh game doesn't inherit the previous session's piles or priorities.
        _workerSystem.Reset();
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

        // Load map. mapName selects which map under assets/maps/ to load
        // ("default", "testmap", ...); the trigger/road companions follow the
        // same "<mapName>_triggers.json" / "<mapName>_roads.json" convention.
        // Special name "empty_test" synthesizes a tiny grass-only map in code
        // (no JSON file) so technical-behavior tests don't fight the regular
        // map's content. See the "empty_test_map" menu dev command.
        var placedUnits = new List<Data.PlacedUnit>();
        string mapPath = GamePaths.Resolve($"assets/maps/{mapName}.json");
        if (mapName == "empty_test")
        {
            DebugLog.Log("startup", "Empty test map: synthesizing grass-only grid + debug necromancer");
            _groundSystem.Init(WorldSize, WorldSize);
            // One grass ground type at index 0 — the vertex map defaults to 0,
            // so the whole map renders as grass with no further work.
            _groundSystem.AddGroundType(new World.GroundTypeDef
            {
                Id = "grass",
                Name = "Grass",
                TexturePath = "assets/Environment/Ground/GroundGrass1.png",
            });
            // Pathfinder needs a non-zero map; the fog grid sizes off the
            // ground dims (mirrors the regular load path).
            _deathFog.Init(_groundSystem.WorldW, _groundSystem.WorldH, cellSize: 4);
            placedUnits.Add(new Data.PlacedUnit
            {
                UnitDefId = "necromancer_debug",
                X = WorldSize * 0.5f,
                Y = WorldSize * 0.5f,
            });
        }
        else if (File.Exists(mapPath))
        {
            DebugLog.Log("startup", $"Loading map '{mapName}' from file...");
            // Load env defs from canonical location (before map, so placed objects can resolve IDs)
            MapData.LoadEnvDefs(GamePaths.Resolve(GamePaths.EnvDefsJson), _envSystem);
            // Parse the 55MB map JSON exactly once — Load returns the grass info from
            // the same JsonDocument it already parsed, avoiding a redundant disk+parse pass.
            MapData.Load(mapPath, _groundSystem, _envSystem, _wallSystem, placedUnits,
                out var grassInfo);
            MapData.LoadTriggers(GamePaths.Resolve($"assets/maps/{mapName}_triggers.json"), _triggerSystem);
            MapData.LoadRoads(GamePaths.Resolve($"assets/maps/{mapName}_roads.json"), _roadSystem);
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
            // Editor-placed corpse: spawn the unit (so it resolves its def/sprite/scale),
            // then immediately convert it to a corpse and drop it from the unit array.
            if (pu.IsCorpse)
                _sim.SpawnCorpseFromUnit(lastIdx);
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
        // Empty test map is for Claude's behavior tests — closer zoom so floating
        // text (cast-failure alerts, damage numbers, ActionLabels) is legible in
        // the downsampled screenshots that get sent back to the model. Regular
        // maps keep the 24f default tuned for human play.
        _camera.Zoom = mapName == "empty_test" ? 48f : 24f;

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

        // Test maps: pre-load no-path test spells so the necromancer can
        // immediately cast and exercise failure modes (OutOfRange /
        // NotEnoughMana / OnCooldown) without granting paths or touching
        // the player's saved spellbar.json. Regular maps are deliberately
        // left alone — they ship with an empty default spellbar.
        if (mapName == "testmap" || mapName == "empty_test")
        {
            // OutOfRange/NotEnoughMana/OnCooldown test projectile stays on the primary bar (Q).
            _spellBarState.Slots[0] = new SpellBarSlot { SpellID = "test_projectile" };
            // The canonical reanimation spell on number-key slot 1 (the SECONDARY bar, cast by
            // D1) so it can be cast on a corpse. The debug necromancer has every path/mana.
            _secondaryBarState.Slots[0] = new SpellBarSlot { SpellID = "reanimate_corpse" };
        }

        // Empty test map: top up the necromancer's mana pool so high-cost
        // spells are castable out of the gate. NecroState.MaxMana is separate
        // from the UnitDef's maxMana field (which only affects non-necro mana
        // users like priests), so the debug def's 999 doesn't reach here on
        // its own.
        if (mapName == "empty_test")
        {
            _sim.NecroState.MaxMana = 999f;
            _sim.NecroState.Mana = 999f;
            _sim.NecroState.ManaRegen = 20f;
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
        // Default the editor's Save target to whichever map was just loaded, so
        // editing after "Play Test Map" saves to testmap.json — not default.json.
        _mapEditor.SetMapFilename(mapName);
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

    /// <summary>Use a consumable inventory item by id. Currently supports skill-point
    /// potions (grant SkillPointAmount to the SkillPointPool). Returns true if the
    /// item was consumed, in which case the caller decrements the stack by one.</summary>
    private bool TryConsumeInventoryItem(string itemId)
    {
        var def = _gameData.Items.Get(itemId);
        if (def == null) return false;

        if (def.SkillPointAmount > 0 && !string.IsNullOrEmpty(def.SkillPointPool))
        {
            _skillBookState.AddSkillPoints(def.SkillPointPool, def.SkillPointAmount);
            DebugLog.Log("items",
                $"Consumed '{itemId}': +{def.SkillPointAmount} '{def.SkillPointPool}' skill points " +
                $"(now {_skillBookState.GetSkillPoints(def.SkillPointPool)})");
            // Reuse the skill-learn toast for feedback; empty SkillId just opens the
            // skill book (where the new points are visible) if clicked.
            _skillLearnToasts.Add(new SkillLearnToast
            {
                Header = "Skill Points",
                SkillName = $"+{def.SkillPointAmount} {def.SkillPointPool}",
                SkillId = "",
                Timer = 0f,
                Duration = 5f,
            });
            return true;
        }

        return false;
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

    // --- Dev server command execution (main thread) ---------------------------
    // Each case rides Game1's existing APIs / the same Simulation primitives that
    // scenarios use, so there's a single source of truth for world manipulation.

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

    protected override void LoadContent()
    {
        _spriteBatch = new SpriteBatch(GraphicsDevice);
        _pixel = TextureUtil.GetWhitePixel(GraphicsDevice);
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
        // Glyphs missing from the bitmap font (em-dashes, curly quotes, accents in
        // data/item text, etc.) otherwise make MeasureString/DrawString THROW,
        // crashing the whole game on a hover tooltip. A DefaultCharacter degrades
        // unsupported glyphs to '?' instead of throwing.
        _font.DefaultCharacter = '?';
        _smallFont.DefaultCharacter = '?';
        _largeFont.DefaultCharacter = '?';
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
        try { _morphSdfEffect = Content.Load<Microsoft.Xna.Framework.Graphics.Effect>("MorphSDF"); }
        catch (Exception ex) { _morphSdfEffect = null; DebugLog.Log("startup", $"MorphSDF not loaded: {ex.Message}"); }
        // Route sim-layer reanimations (potion / on-death / table-craft) through the composite
        // reanimation pipeline, the same one spells use (headless sims fall back to a direct spawn).
        _sim.ReanimHandler = OnSimReanimReady;
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

        // Worker job system: brain that assigns grave workers to jobs.
        _workerSystem.Bind(_sim, _envSystem, _gameData);
        _workerSystem.Reset();
        _sim.Workers = _workerSystem;
        // Reanimate job spawns through the canonical reanim pipeline (green rise effect).
        _workerSystem.SpawnWorkerUnit = (defId, pos) =>
            QueueReanimRise(defId, -1, "reanim_smoke", posOverride: pos);

        // Lean dev control server: only when launched with --devserver <port>.
        if (LaunchArgs.DevServerPort > 0)
        {
            _devServer = new Necroking.Dev.DevServer(LaunchArgs.DevServerPort);
            _devServer.Start();
            Exiting += (_, _) => _devServer?.Stop();
        }

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
        // Drain dev-server commands first so they run even if the window is
        // unfocused (we bail out below in that case) and regardless of menu state.
        _devServer?.Drain(ExecuteDevCommand);

        // Headless (dev-server / scenario) runs keep an off-screen window; drop its
        // taskbar button so the supervisor-owned game doesn't clutter the taskbar.
        // MainWindowHandle isn't valid the instant the window is created, so retry
        // each frame until it applies (HideFromTaskbar returns true once done).
        if (LaunchArgs.Headless && !_taskbarHidden)
            _taskbarHidden = Core.WindowChrome.HideFromTaskbar();

        var kb = Keyboard.GetState();
        var mouse = Mouse.GetState();

        // Window unfocused or minimized. IsActive is MonoGame's canonical
        // window-focus flag. Exempt scenario / headless runs — automated runs often
        // lack window focus, and freezing them would break the scenario test harness.
        bool unfocused = !IsActive && _activeScenario == null && LaunchArgs.Scenario == null && !LaunchArgs.Headless && _devServer == null;

        // Default behaviour: freeze entirely while unfocused — skip all input so
        // background clicks (taskbar, other apps) read by the global Mouse.GetState()
        // aren't consumed by the game, and skip the simulation tick. We still advance
        // _prevMouse/_prevKb to the real states so the click that refocuses the
        // window isn't seen as an in-game press.
        bool runWhenUnfocused = _gameData?.Settings.General.RunWhenUnfocused ?? false;
        if (unfocused && !runWhenUnfocused)
        {
            _prevKb = kb;
            _prevMouse = mouse;
            base.Update(gameTime);
            return;
        }

        if (unfocused)
        {
            // "Run when unfocused" is on: keep simulating, but feed NEUTRAL input
            // (no buttons, no keys, cursor parked at screen centre) to the rest of
            // Update so background clicks/keys aren't consumed and the camera doesn't
            // edge-drift. The real states still flow into _prevMouse/_prevKb at the
            // normal exit points, preserving the refocus-click protection above.
            var neutral = new MouseState(
                GraphicsDevice.Viewport.Width / 2, GraphicsDevice.Viewport.Height / 2,
                mouse.ScrollWheelValue,
                ButtonState.Released, ButtonState.Released, ButtonState.Released,
                ButtonState.Released, ButtonState.Released);
            _input.Capture(neutral, neutral, new KeyboardState(), new KeyboardState());
        }
        else
        {
            _input.Capture(mouse, _prevMouse, kb, _prevKb);
        }

        // Dev-only cursor override (the `mousepos` dev command): headless runs have no
        // real mouse, so force MousePos to a fixed screen point to exercise hover UI
        // (tooltips, hover highlights) from the dev server. No-op when unset.
        if (_devMouseOverride.HasValue)
            _input.MousePos = _devMouseOverride.Value;

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

        // Decay spell-bar activation flashes in REAL time (rawDt) so the press
        // feedback fades consistently regardless of game pause/speed.
        for (int i = 0; i < _primarySlotFlash.Length; i++)
            if (_primarySlotFlash[i] > 0f) _primarySlotFlash[i] = MathF.Max(0f, _primarySlotFlash[i] - rawDt);
        for (int i = 0; i < _secondarySlotFlash.Length; i++)
            if (_secondarySlotFlash[i] > 0f) _secondarySlotFlash[i] = MathF.Max(0f, _secondarySlotFlash[i] - rawDt);

        // Step the active dev batch script (if any) over the same sim/real clock a
        // scenario's OnTick uses, so scripted waits + screenshots land deterministically.
        if (_devJob != null) UpdateDevScript(dt, rawDt);

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

                // Play Test Map button — loads assets/maps/testmap.json
                if (mouse.X >= menuX && mouse.X < menuX + btnW && mouse.Y >= menuY && mouse.Y < menuY + btnH)
                {
                    StartGame("testmap");
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

            // Scroll (one grid row at a time)
            {
                int screenW2 = GraphicsDevice.Viewport.Width;
                int screenH2 = GraphicsDevice.Viewport.Height;
                GetScenarioGridLayout(screenW2, screenH2, out int cols, out _, out _, out _, out _, out _, out _);
                if (_input.ScrollDelta > 0) _scenarioScrollOffset = Math.Max(0, _scenarioScrollOffset - cols);
                if (_input.ScrollDelta < 0) _scenarioScrollOffset += cols;
            }

            if (_input.LeftPressed)
            {
                int screenW2 = GraphicsDevice.Viewport.Width;
                int screenH2 = GraphicsDevice.Viewport.Height;
                GetScenarioGridLayout(screenW2, screenH2, out int cols, out int btnW, out int btnH, out int btnGap, out int gridX, out int menuY, out int rowsVisible);

                var names = new List<string>(ScenarioRegistry.GetNames());
                names.Reverse(); // Newest first (must match draw order)
                int visibleCount = Math.Min(names.Count - _scenarioScrollOffset, rowsVisible * cols);
                for (int i = 0; i < visibleCount; i++)
                {
                    int nameIdx = i + _scenarioScrollOffset;
                    if (nameIdx >= names.Count) break;
                    int col = i % cols, row = i / cols;
                    int bx = gridX + col * (btnW + btnGap);
                    int by = menuY + row * (btnH + btnGap);
                    if (mouse.X >= bx && mouse.X < bx + btnW && mouse.Y >= by && mouse.Y < by + btnH)
                    {
                        StartScenario(names[nameIdx]);
                        _prevKb = kb;
                        _prevMouse = mouse;
                        base.Update(gameTime);
                        return;
                    }
                }

                // Back button (centered below the grid)
                int usedRows = (visibleCount + cols - 1) / cols;
                int backY = menuY + usedRows * (btnH + btnGap) + 10;
                int backW = 320;
                int backX = screenW2 / 2 - backW / 2;
                if (mouse.X >= backX && mouse.X < backX + backW && mouse.Y >= backY && mouse.Y < backY + btnH)
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

        // Alt+Enter: toggle between borderless "fullscreen" and a resizable window.
        if (!anyTextInputActive
            && (_input.IsKeyDown(Keys.LeftAlt) || _input.IsKeyDown(Keys.RightAlt))
            && _input.WasKeyPressed(Keys.Enter))
            ToggleWindowMode();

        // 'I' key toggles inventory (lazy-inits the UI family on first open)
        if (!anyTextInputActive && _input.WasKeyPressed(Keys.I) && _menuState == MenuState.None)
        {
            EnsureInventoryUIsInitialized();
            _inventoryUI.Toggle(GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height);
        }

        // 'O' key toggles the job board (wOrk/jObs)
        if (!anyTextInputActive && _input.WasKeyPressed(Keys.O) && _menuState == MenuState.None)
        {
            EnsureInventoryUIsInitialized();
            _jobBoardUI.Toggle(GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height);
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
                _unitInfoPanel.ShowForUnit(_sim.Units[_sim.NecromancerIndex].Id);
        }

        // 'H' = cycle the hover-highlight style variant (design test harness:
        // 20 shape×line-style variants + an Off state; a toast names the active one).
        if (!anyTextInputActive && _input.WasKeyPressed(Keys.H) && _menuState == MenuState.None)
        {
            _hoverHighlightVariant = (_hoverHighlightVariant + 1) % 21;
            _hoverVariantLabelTimer = 2.75f;
        }
        if (_hoverVariantLabelTimer > 0f) _hoverVariantLabelTimer -= _rawDt;

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
                    _unitInfoPanel.ShowForUnit(_sim.Units[best].Id);
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
            { _menuState = MenuState.MapEditor; _paused = false; _mapEditor.SuppressClicksUntilRelease(); }
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
        {
            var editMouse = mouse;
            if (_devMouseActive) // ed_mouse: persistent synthetic editor mouse for click-leak tests
                editMouse = new MouseState(_devMouseX, _devMouseY, mouse.ScrollWheelValue,
                    _devMouseDown ? ButtonState.Pressed : ButtonState.Released,
                    ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released);
            _editorUi.UpdateInput(editMouse, _prevMouse, kb, _prevKb, screenW, screenH, gameTime, _input);
        }
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
        else if (necroIdx >= 0 && !_devFreeCamera)
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
                bool wasdHeld = moveDir.LengthSq() > 0.01f;
                if (wasdHeld) moveDir = moveDir.Normalized();

                // Dev "walk_necro" auto-walk: drives the same movement input toward
                // a goal point. Any WASD press cancels it so manual control always
                // wins (avoids the player fighting an invisible auto-walk).
                if (_devWalkTarget.HasValue)
                {
                    if (wasdHeld)
                    {
                        _devWalkTarget = null;
                    }
                    else
                    {
                        var toGoal = _devWalkTarget.Value - _sim.Units[necroIdx].Position;
                        if (toGoal.LengthSq() <= 0.25f) // ~0.5 world units = arrived
                            _devWalkTarget = null;
                        else
                            moveDir = toGoal.Normalized();
                    }
                }

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
                    var facingDir = Movement.FacingUtil.ForwardDir(mu[necroIdx]);
                    JumpSystem.BeginJumpAttack(mu, necroIdx, mu[necroIdx].Position + facingDir * 4f);
                }
            }

            // --- Beam/drain channel-hold ---
            if (_channelingSlot >= 0)
            {
                bool stillHeld;
                if (_channelingIsSecondary)
                {
                    // Secondary bar slots 0..5 are held with number keys D1..D6.
                    stillHeld = _channelingSlot >= 0 && _channelingSlot <= 5
                        && _input.IsKeyDown((Keys)((int)Keys.D1 + _channelingSlot));
                }
                else
                {
                    stillHeld = _channelingSlot switch
                    {
                        0 => _input.IsKeyDown(Keys.Q),
                        1 => _input.IsKeyDown(Keys.E),
                        2 => _input.LeftDown,
                        3 => _input.RightDown,
                        _ => false
                    };
                }
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
                    && _pendingCastAnim == null  // NoValidTarget also means "busy mid-cast" — don't stamp a stray melee then
                    && _sim.Units[necroIdx].PendingAttack.IsNone)
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

            // --- Corpse hover detection (for the reanimation info tooltip) ---
            // Picks the nearest corpse under the cursor. Same gating as ground
            // objects: opt-in toggle + suppressed over UI. Skips bodies that are
            // mid-dissolve, consumed, bagged, or being carried (not pickable).
            _hoveredCorpseIdx = -1;
            {
                var tcfg = _gameData.Settings.Tooltips;
                if (tcfg.ShowCorpseInfo && !_input.MouseOverUI)
                {
                    float pr = tcfg.GroundPickRadius;
                    float bcd = pr * pr;
                    var corpses = _sim.Corpses;
                    for (int ci = 0; ci < corpses.Count; ci++)
                    {
                        var cp = corpses[ci];
                        if (cp.ConsumedBySummon || cp.Dissolving || cp.Bagged
                            || cp.DraggedByUnitID != GameConstants.InvalidUnit) continue;
                        float cdx = cp.Position.X - mouseWorld.X, cdy = cp.Position.Y - mouseWorld.Y;
                        float cd = cdx * cdx + cdy * cdy;
                        if (cd < bcd) { bcd = cd; _hoveredCorpseIdx = ci; }
                    }
                }
            }

            // --- Hovered unit (for the outline-box highlight) ---
            // Independent of the auto-stat sheet so the box works even when that's
            // off. Same gating: opt-in toggle + suppressed over UI.
            _hoveredUnitIdx = -1;
            if (_gameData.Settings.Tooltips.ShowHoverHighlight && !_input.MouseOverUI)
            {
                float pr = _gameData.Settings.Tooltips.HoverPickRadius;
                float bud = pr * pr;
                for (int i = 0; i < _sim.Units.Count; i++)
                {
                    var u = _sim.Units[i];
                    if (!u.Alive) continue;
                    float udx = u.Position.X - mouseWorld.X, udy = u.Position.Y - mouseWorld.Y;
                    float d2 = udx * udx + udy * udy;
                    if (d2 < bud) { bud = d2; _hoveredUnitIdx = i; }
                }
            }

            // Dev force-hover: pin the highlight to a chosen unit for headless variant testing.
            if (_devForceHoverUnitId != uint.MaxValue)
            {
                _hoveredUnitIdx = -1;
                for (int i = 0; i < _sim.Units.Count; i++)
                    if (_sim.Units[i].Alive && _sim.Units[i].Id == _devForceHoverUnitId) { _hoveredUnitIdx = i; break; }
            }
            // Dev force-hover an env object by index (headless variant testing has no real mouse).
            if (_devForceHoverObjectIdx >= 0 && _devForceHoverObjectIdx < _envSystem.ObjectCount)
                _hoveredObjectIdx = _devForceHoverObjectIdx;

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
                    uint hoveredId = _sim.Units[hoveredUnit].Id;
                    if (_unitInfoPanel.UnitId != hoveredId)
                        _unitInfoPanel.ShowForUnitTransient(hoveredId);
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
                else
                {
                    // Left-click an Empty Grave → open its worker-assignment roster.
                    int clickedGrave = FindGraveUnderCursor(mouseWorld);
                    if (clickedGrave >= 0)
                    {
                        EnsureInventoryUIsInitialized();
                        _graveRosterUI.OpenForGrave(clickedGrave, screenW, screenH);
                        _input.ConsumeMouse();
                    }
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
            _workerSystem.Update(dt);
            ApplyBlightBombImpacts();
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
        _graveRosterUI.Update(_input);
        _jobBoardUI.Update(_input);
        // Inventory slot clicks (table deposit / consumable use) are dispatched via
        // _inventoryUI.OnSlotClicked, set in EnsureInventoryUIsInitialized — the
        // inventory is a modal layer whose inside-panel clicks PopupManager consumes
        // before this point, so they can't be handled inline here.
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
            var options = Necroking.Core.JsonDefaults.Indented;
            string json = System.Text.Json.JsonSerializer.Serialize(doc, options);
            System.IO.Directory.CreateDirectory(GamePaths.Resolve(GamePaths.UserSettingsDir));
            Core.AtomicFile.WriteAllText(GamePaths.Resolve(GamePaths.UserSpellBarJson), json);
        }
        catch (Exception ex) { DebugLog.Log("error", $"SaveSpellBars failed: {ex.Message}"); }
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

    /// <summary>Floating alert text above the necromancer — the shared channel for
    /// every cast-failure reason ("Horde Full", "Out of Range", "Not Enough Mana",
    /// "Need Death 1", …). Renders red via the DamageNumber alert path.</summary>
    private void SpawnCastFailText(int necroIdx, string message)
    {
        if (necroIdx < 0 || necroIdx >= _sim.Units.Count) return;
        if (string.IsNullOrEmpty(message)) return;
        _damageNumbers.Add(new DamageNumber
        {
            WorldPos = _sim.Units[necroIdx].Position,
            Damage = 0,
            Timer = 0f,
            Height = 2f,
            IsPoison = false,
            PickupText = message,
            IsAlert = true,
        });
    }

    private void SpawnHordeCapText(int necroIdx) => SpawnCastFailText(necroIdx, "Horde Full");

    /// <summary>Floating feedback when a cast fails because the necromancer lacks the
    /// spell's magic-path requirement — names the path(s) it needs (e.g.
    /// "Need Death 1") so it's never mistaken for a mana shortfall.</summary>
    private void SpawnMissingPathText(int necroIdx, string spellId)
    {
        string need = GameSystems.SpellCaster.DescribeMissingPath(
            _gameData.Spells.Get(spellId), _gameData, _sim.Units, necroIdx);
        SpawnCastFailText(necroIdx, string.IsNullOrEmpty(need) ? "Path Locked" : $"Need {need}");
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

    private int FindNecromancer() => _sim.Units.FindAliveNecromancerIndex();

    // "CastingEffect Green" buff — visual glow shown on the necromancer while it
    // channels a reanimation at the necro table.
    private const string TableChannelBuffId = "buff_4_copy";

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

    // ── Raw-corpse carry (body bag mothballed; see GameConstants.UseBodyBag) ──

    // --- Persistent frame-centroid cache (data/frame_centroids.json) ---
    private Dictionary<string, Vector2>? _persistedCentroids;
    private bool _centroidsDirty;
    private bool _bulkCentroidBake;
    private Dictionary<Microsoft.Xna.Framework.Graphics.Texture2D, int>? _texToAtlasIdx;

    // ═══════════════════════════════════════
    //  Gameplay Debug Visualizations (F7)
    // ═══════════════════════════════════════

    // Sortable item for merged unit+object depth sorting
    private readonly List<DepthItem> _depthItems = new(256); // reused each frame

    internal enum DepthItemType : byte { Unit, EnvObject, CloudPuff, GrassTuft, DeathFogPuff, ReanimDust }

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

    private readonly Dictionary<int, float> _dissolveLoggedSeeds = new();

    // 8-direction offsets: N, NE, E, SE, S, SW, W, NW
    private static readonly float[][] _outlineDirs =
    {
        new[] { 0f, -1f }, new[] { 1f, -1f }, new[] { 1f, 0f }, new[] { 1f, 1f },
        new[] { 0f,  1f }, new[] {-1f,  1f }, new[] {-1f, 0f }, new[] {-1f,-1f }
    };

    // Hardcoded ghost outline params matching C++
    private static readonly HdrColor _ghostColor1 = new(140, 200, 255, 45, 1.0f);
    private static readonly HdrColor _ghostColor2 = new(170, 215, 255, 60, 1.1f);

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

    protected override void UnloadContent()
    {
        _widgetRenderer.Shutdown();
        // _pixel is the shared TextureUtil.GetWhitePixel cache — do NOT dispose it here.
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
