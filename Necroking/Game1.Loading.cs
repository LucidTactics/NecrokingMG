// Game1 partial: the boot loading queue (MenuState.Loading). The heavy startup
// work that used to run monolithically inside Initialize/LoadContent — game-data
// JSON, atlas decode, GPU uploads, shaders, renderer/system wiring, editor
// construction — is queued here as labeled steps. Update runs ONE step per
// presented frame (TickLoading), so the window appears immediately and
// UI/LoadingScreen.cs can show what's currently loading. Everything runs on the
// main thread: GPU uploads need the GL context, and the step bodies were written
// for single-threaded startup. Step ORDER encodes the dependencies — see the
// comments on each step before reordering.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.Core;
using Necroking.Render;
using Necroking.Game;
using Necroking.GameSystems;
using Necroking.Editor;
using Necroking.Lib;
using Necroking.UI;

namespace Necroking;

public partial class Game1
{
    private readonly List<(string Label, Action Run)> _loadingSteps = new();
    private int _loadingStep;
    // Set by the Draw path after the current label has been presented. A step only
    // runs once its label was actually on screen, so the centered text always names
    // the step that is blocking the frame right now — not the one that just finished.
    private bool _loadingLabelDrawn;

    internal string LoadingStatus => _loadingStep < _loadingSteps.Count
        ? _loadingSteps[_loadingStep].Label : "Starting";
    internal float LoadingProgress => _loadingSteps.Count == 0 ? 1f
        : (float)_loadingStep / _loadingSteps.Count;
    internal void MarkLoadingFrameDrawn() => _loadingLabelDrawn = true;

    /// <summary>Run the next queued startup step — at most one per presented frame.
    /// Called from the MenuState.Loading early-out at the top of Update. Flips to
    /// the main menu only when the queue fully drains: the menu's buttons
    /// dereference windows the last step constructs (settings/load-game).</summary>
    internal void TickLoading()
    {
        if (!_loadingLabelDrawn || _loadingStep >= _loadingSteps.Count) return;
        var (label, run) = _loadingSteps[_loadingStep];
        run();
        LogTiming($"[load {_loadingStep + 1}/{_loadingSteps.Count}] {label}");
        _loadingStep++;
        _loadingLabelDrawn = false;
        if (_loadingStep >= _loadingSteps.Count)
        {
            _loadingSteps.Clear();
            DebugLog.Log("startup", "=== Startup loading complete ===");
            _menuState = MenuState.MainMenu;
        }
    }

    /// <summary>Queue every heavy startup step. Called at the end of the slimmed
    /// Initialize; the steps execute later, one per frame, from TickLoading.</summary>
    private void BuildLoadingSteps()
    {
        void Step(string label, Action run) => _loadingSteps.Add((label, run));

        // No dependencies — first so the loading screen gets its backdrop ASAP
        // (LoadingScreen falls back to a flat fill while _mainMenuBg is null).
        Step("Loading menu background", () =>
        {
            string menuBgPath = GamePaths.Resolve(Path.Combine("assets", "UI", "Background", "VampireBackground.png"));
            if (File.Exists(menuBgPath))
                _mainMenuBg = TextureUtil.LoadPremultiplied(GraphicsDevice, menuBgPath);
        });

        Step("Loading game data", () =>
        {
            _gameData.Load();
            _inventory = new Inventory(20, _gameData.Items);
            SkillBookDefs.Load();
            _skillBookState.InitFromDefs();
            // Early bind so scenarios (which skip StartGame) still have state. The
            // spell-bar Slots may be null at this point — re-bind happens in
            // StartGame once the bar is allocated. AddSpellToBarEffect handles null
            // Slots gracefully. (The overlay's Init ran in LoadContent.)
            _skillBookOverlay.Bind(_skillBookState, _inventory, _gameData,
                _spellBarState, _sim);
            LogTiming($"GameData loaded: {_gameData.Units.Count} units, {_gameData.Spells.Count} spells, {_gameData.Weapons.Count} weapons, {_gameData.Items.Count} items");
        });

        // --- Sprite atlases -------------------------------------------------
        // The scan is a cheap directory listing, done now so the atlas list is
        // known up front and each GPU upload can be its own labeled step. The
        // decode/meta/upload state below is shared across steps via this closure.
        AtlasDefs.ScanSpritesDirectory();
        int atlasCount = AtlasDefs.TotalCount;
        _atlases = new SpriteAtlas[atlasCount];
        for (int i = 0; i < atlasCount; i++)
            _atlases[i] = new SpriteAtlas();

        var decodedPixels = new Color[atlasCount][];
        var decodedW = new int[atlasCount];
        var decodedH = new int[atlasCount];
        var metaParsed = new bool[atlasCount];

        // Per-atlas work list: base sheet at index 0, overflow __N sheets after.
        var extSheets = new List<(string png, string meta)>[atlasCount];
        for (int i = 0; i < atlasCount; i++)
            extSheets[i] = new List<(string, string)>(AtlasDefs.FindExtensionSheets(AtlasDefs.Names[i]));
        var extDecoded = new (Color[] pixels, int w, int h, bool decoded)[atlasCount][];
        for (int i = 0; i < atlasCount; i++)
            extDecoded[i] = new (Color[], int, int, bool)[extSheets[i].Count];

        Step("Reading sprite sheets", () =>
        {
            // Flat parallel decode of every PNG (base + all extension sheets) so the
            // largest single atlas isn't gated by its own extension on the same thread.
            // Meta parsing is fast and runs serially after decode (extension meta has
            // to be parsed after the base meta, per atlas).
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
        });

        Step("Loading animation metadata", () =>
        {
            // Fresh asset log per process so it doesn't grow unbounded across runs (mirrors the
            // perf-log Clear in Simulation.Init). Prior runs' AnimMeta/buff warnings are discarded.
            DebugLog.Clear("asset");

            // Animation metadata must load BEFORE the GPU-upload steps: the stride
            // calibration pass (which runs per atlas in the upload steps, while
            // decoded pixels are still live) reads per-gait cycle durations from it.
            foreach (string name in AtlasDefs.Names)
            {
                string metaPath = GamePaths.Resolve($"assets/Sprites/{name}.animationmeta");
                if (File.Exists(metaPath))
                    AnimMetaLoader.Load(metaPath, _animMeta);
                foreach (string extMeta in AtlasDefs.FindExtensionAnimMeta(name))
                    AnimMetaLoader.Load(extMeta, _animMeta);
            }
            // Validate effect_time ONCE over the fully-loaded dict (not per-file inside Load — that
            // was O(files × keys) and dumped tens of thousands of duplicate warnings into asset.log).
            AnimMetaLoader.ValidateEffectTimes(_animMeta);
            // Rebuild atlas keyframe lists to logical frame order (meta 'sprites'
            // mapping, repeats allowed) — must run after ALL spritemeta parsing
            // and before GPU upload/finalize so duplicated keyframes get the
            // same Y-flip/bbox treatment as originals. Then stitch split-export
            // clip pairs (Standup+Standup2) into single clips — stitch runs
            // AFTER expansion so both halves are already in logical order.
            foreach (var atlas in _atlases)
                if (atlas != null)
                {
                    AnimMetaLoader.ExpandAtlasKeyframes(atlas, _animMeta);
                    AnimMetaLoader.StitchSplitClips(atlas, _animMeta);
                }
            LogTiming($"Animation metadata: {_animMeta.Count} entries");
            _sim.SetAnimMeta(_animMeta);
        });

        // One step per atlas: upload decoded pixels to GPU (fast — just SetData, no
        // PNG decode). Stride calibration runs here BEFORE pixels are freed, so it
        // can scan the source rgba without a GPU readback. This sequential loop is
        // the dominant startup cost, hence the per-atlas progress labels.
        int strideCacheHits = 0, strideCacheBuilds = 0;
        for (int i = 0; i < atlasCount; i++)
        {
            int a = i; // capture a stable copy per step
            Step($"Uploading sprites: {AtlasDefs.Names[a]} ({a + 1}/{atlasCount})", () =>
            {
                if (decodedPixels[a] != null && metaParsed[a])
                {
                    var tex = TextureUtil.CreateTextureFromPixels(GraphicsDevice,
                        decodedPixels[a], decodedW[a], decodedH[a]);
                    _atlases[a].SetTextureAndFinalize(tex, decodedW[a], decodedH[a]);

                    // Calibrate stride per unit. Y-coords in the spritemeta have been
                    // flipped by SetTextureAndFinalize (top-left origin), matching the
                    // pixel buffer layout we're handing to StrideCalibration.
                    string atlasName = AtlasDefs.Names[a];
                    string pngPath = GamePaths.Resolve($"assets/Sprites/{atlasName}.png");
                    string smPath  = GamePaths.Resolve($"assets/Sprites/{atlasName}.spritemeta");
                    string amPath  = GamePaths.Resolve($"assets/Sprites/{atlasName}.animationmeta");
                    bool cacheHit = Render.StrideCalibration.CalibrateAtlas(_atlases[a], atlasName,
                        pngPath, smPath, amPath, decodedPixels[a], decodedW[a], decodedH[a], _animMeta);
                    if (cacheHit) strideCacheHits++; else strideCacheBuilds++;

                    decodedPixels[a] = null!; // free memory after calibration is done with it
                }
                // Attach extension sheets in the order they were decoded (matches the
                // TextureIndex assigned by ParseExtensionMeta).
                foreach (var ext in extDecoded[a])
                {
                    if (!ext.decoded || ext.pixels == null) continue;
                    var extTex = TextureUtil.CreateTextureFromPixels(GraphicsDevice,
                        ext.pixels, ext.w, ext.h);
                    _atlases[a].AttachExtensionTexture(extTex, ext.w, ext.h);
                }
            });
        }

        Step("Wiring unit sprites", () =>
        {
            LogTiming($"Atlases GPU upload + stride calibration: {atlasCount} ({string.Join(", ", AtlasDefs.Names)}) — strideCacheHits={strideCacheHits} builds={strideCacheBuilds}");

            // Wire each UnitDef's runtime SpriteData reference now that both registries
            // and atlases exist. Lets LocomotionProfile.FromUnit reach stride
            // calibration without separately plumbing atlas access through AI / render
            // call sites.
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
            int corpsesIdx = AtlasDefs.ResolveAtlasName("Corpses");
            if (corpsesIdx >= 0 && corpsesIdx < _atlases.Length)
                _gameData.Corpse.ApplyToAtlas(_atlases[corpsesIdx]);

            _loadMenuSaves = ListSaveGames();
        });

        Step("Loading fonts", () =>
        {
            // TrueType fonts via FontStashSharp (dynamic sizing). The three
            // SpriteFonts load in LoadContent — the loading screen itself needs them.
            _fontManager.LoadFontsFromDirectory(GamePaths.Resolve(GamePaths.FontsDir));
            if (_fontManager.HasFonts)
            {
                // Prefer "Standard" as default, fall back to first loaded
                if (_fontManager.FontFamilies.Any(f => f == "Standard"))
                    _fontManager.SetDefault("Standard");
            }
        });

        Step("Compiling shaders", () =>
        {
            _shadowRenderer.Init(GraphicsDevice);

            _bloom.Init(GraphicsDevice, Content,
                _graphics.PreferredBackBufferWidth, _graphics.PreferredBackBufferHeight);

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
            try
            {
                _morphSdfEffect = Content.Load<Microsoft.Xna.Framework.Graphics.Effect>("MorphSDF");
                // Constant look parameters, set once (MGFX on GL ignores .fx initializers;
                // dynamic params — MorphT, GreenFill, OutlineColor, OutlinePulse, textures —
                // are set per draw in DrawReanimMorph). Bulge is the amoeba swell that opens
                // bridge gaps (was 4.0 — gentler swell reads as a quiet wisp, and the green
                // gap-fill is dimmed at the call site so it doesn't glow).
                _morphSdfEffect.Parameters["Bulge"]?.SetValue(2.0f);
                _morphSdfEffect.Parameters["EdgeSoftness"]?.SetValue(1.5f); // AA band, px
                _morphSdfEffect.Parameters["OutlineWidth"]?.SetValue(1.2f); // px
            }
            catch (Exception ex) { _morphSdfEffect = null; DebugLog.Log("startup", $"MorphSDF not loaded: {ex.Message}"); }
            try { _depthCutoutEffect = Content.Load<Microsoft.Xna.Framework.Graphics.Effect>("DepthCutout"); }
            catch (Exception ex) { _depthCutoutEffect = null; DebugLog.Log("startup", $"DepthCutout not loaded: {ex.Message}"); }
            try {
                _wadingEffect = Content.Load<Microsoft.Xna.Framework.Graphics.Effect>("Wading");
                var pnames = string.Join(",", _wadingEffect.Parameters.Select(p => p.Name));
                DebugLog.Log("startup", $"Wading loaded. params=[{pnames}]");
                // Constant look parameters, set once (MGFX on GL ignores .fx initializers).
                // The per-frame waterline/frame-UV params are set in DrawWadingSpriteFrame
                // and the WadingEditorPopup preview — both share this Effect instance.
                _wadingEffect.Parameters["FoamHalfWidth"]?.SetValue(0.05f);    // half-width of the foam band, local V
                _wadingEffect.Parameters["TopFoamHalfWidth"]?.SetValue(0.05f);
                _wadingEffect.Parameters["UnderwaterAlpha"]?.SetValue(0.0f);   // submerged pixels fully hidden
                _wadingEffect.Parameters["FoamColor"]?.SetValue(new Vector3(0.88f, 0.94f, 0.96f));
            }
            catch (Exception ex) { _wadingEffect = null; DebugLog.Log("startup", $"Wading NOT loaded: {ex.Message}"); }
            try { _hdrIntensityEffect = Content.Load<Microsoft.Xna.Framework.Graphics.Effect>("HdrIntensity"); }
            catch (Exception ex) { _hdrIntensityEffect = null; DebugLog.Log("startup", $"HdrIntensity not loaded: {ex.Message}"); }
            {
                Microsoft.Xna.Framework.Graphics.Effect? scatterFx = null;
                try { scatterFx = Content.Load<Microsoft.Xna.Framework.Graphics.Effect>("ScatterGlow"); }
                catch (Exception ex) { DebugLog.Log("startup", $"ScatterGlow not loaded: {ex.Message}"); }
                _scatterGlow.Init(this, scatterFx);   // null effect → system stays inert
            }
            try
            {
                _hdrSpriteEffect = Content.Load<Microsoft.Xna.Framework.Graphics.Effect>("HdrSprite");
                if (_hdrSpriteEffect != null)
                {
                    _hdrSpriteEffect.Parameters["MaxIntensity"]?.SetValue(HdrColor.MaxHdrIntensity);
                    _hdrSpriteEffect.Parameters["MaxAlphaIntensity"]?.SetValue(HdrColor.MaxHdrAlphaIntensity);
                    _hdrSpriteEffect.Parameters["AlphaMode"]?.SetValue(0f);
                }
            }
            catch (Exception ex) { _hdrSpriteEffect = null; DebugLog.Log("startup", $"HdrSprite not loaded: {ex.Message}"); }

            // Register the effect-backed materials now that shaders are loaded
            // (Materials is the canonical pass-state registry for the render
            // pipeline — see todos/render-pipeline-design.md).
            Render.Materials.InitEffectMaterials(_wadingEffect, _dissolveTreeEffect,
                _hdrSpriteEffect, _depthCutoutEffect, _morphSdfEffect, _outlineFlatEffect);

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
        });

        Step("Initializing renderers", () =>
        {
            _weatherRenderer.Init(_graphics.PreferredBackBufferWidth, _graphics.PreferredBackBufferHeight);
            _weatherRenderer.SetDayNight(_dayNightSystem);

            _grassRenderer.Init(GraphicsDevice);
            Necroking.Render.MagicPathIcons.SetDevice(GraphicsDevice);
            _lightningRenderer.Init(_spriteBatch, _pixel, _glowTex, this, _camera, _renderer, GraphicsDevice, _hdrIntensityEffect);
            // When a ground vertex newly corrupts, fade nearby grass tufts toward
            // their CorruptedTint over GrassTuftRenderer.CorruptionFadeDuration.
            _groundSystem.OnVertexCorrupted = OnGroundVertexCorruptedForGrass;
            LogTiming("Renderers initialized (weather, grass, lightning)");
        });

        Step("Preparing game systems", () =>
        {
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

            // Wire the foragable subsystem now that all its dependencies exist
            // (inventory from the game-data step, pickup sound just above).
            // Callbacks bridge back to Game1-private state (damage numbers, skill book).
            _foragables.Bind(this, _camera, _renderer,
                _inventory, _effectManager, _pickupSound,
                onPickup: OnForagablePickedUp,
                onLearnTrigger: OnForagableLearnTrigger);

            // Worker job system: brain that assigns grave workers to jobs.
            _workerSystem.Bind(this, _gameData);
            _workerSystem.Reset();

            // Install the Game1→Simulation back-references onto the current Sim. Also called
            // from StartGame after every session recreation (see WireSimCallbacks).
            WireSimCallbacks();
            // Reanimate job spawns through the canonical reanim pipeline (green rise effect).
            _workerSystem.SpawnWorkerUnit = (defId, pos) =>
                QueueReanimRise(defId, -1, "", posOverride: pos);  // "" → the unit's own effect (else reanim_smoke)
        });

        Step("Building editors", () =>
        {
            // Init property editor infrastructure
            _editorUi.SetContext(_pixel, _font, _smallFont, _largeFont);
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
            _net = new Necroking.Net.NetSession();
            _multiplayerWindow = new MultiplayerWindow(_editorUi);
            _multiplayerWindow.SetSession(_net);
            // Clean disconnect on quit so peers see us leave immediately instead of timing out.
            Exiting += (s, e) => _net.Stop();

            _saveGameWindow = new SaveGameWindow(_editorUi);
            _loadGameWindow = new UI.LoadGameWindow(_editorUi);

            _settingsWindow = new SettingsWindow(_editorUi);
            System.IO.Directory.CreateDirectory(GamePaths.Resolve(GamePaths.UserSettingsDir));
            _settingsWindow.SetGameData(_gameData, GamePaths.Resolve(GamePaths.UserSettingsJson), GamePaths.Resolve(GamePaths.UserWeatherJson));
            _settingsWindow.SetDayNightSystem(_dayNightSystem);
            LogTiming("Editors initialized");
        });
    }
}
