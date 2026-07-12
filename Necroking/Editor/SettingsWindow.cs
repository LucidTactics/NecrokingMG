using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Necroking.Data;

namespace Necroking.Editor;

/// <summary>
/// Full tabbed settings editor with Bloom, Shadow, Environment, Weather, General, and Movement tabs.
/// Replaces the flat DrawSettings list in Game1. Uses EditorBase widgets for interactive editing.
/// </summary>
public class SettingsWindow
{
    private readonly EditorBase _ui;
    private GameData _gameData = null!;
    private string _settingsJsonPath = "";
    private string _weatherJsonPath = "";
    private GameSystems.DayNightSystem? _dayNightSystem;

    // Tab state
    private enum Tab { Bloom, Shadow, Environment, Weather, General, Horde, FogOfWar, Corruption, Tooltips, Animation }
    private Tab _activeTab = Tab.Bloom;
    private static readonly string[] TabNames = { "Bloom", "Shadow", "Environ", "Weather", "General", "Horde", "Fog", "Corrupt", "Tooltips", "Anim" };

    // Scroll state per tab (keyed by tab name)
    private readonly float[] _tabScroll = new float[10];

    // Scrollbar drag state: which tab's thumb is being dragged (-1 = none) and
    // the pixel offset between the cursor and the thumb top captured at grab time.

    // Stable per-tab scroll ids for EditorBase.HandlePanelScroll's content-height
    // cache (which clamps to the end before drawing, avoiding overshoot snap-back).
    private static readonly string[] TabScrollIds =
        { "set_tab0", "set_tab1", "set_tab2", "set_tab3", "set_tab4", "set_tab5", "set_tab6", "set_tab7", "set_tab8", "set_tab9" };

    // Track whether we need to save after a frame (dirty flag)
    private bool _dirty;
    private bool _weatherDirty;

    /// <summary>Set to true when the user clicks Back or presses ESC.</summary>
    public bool WantsClose { get; set; }

    /// <summary>The tab identifiers selectable via <see cref="SetActiveTab"/>
    /// (the enum names — "Bloom", "Shadow", "Environment", ...). Exposed so the
    /// dev control channel can list and switch settings tabs.</summary>
    public static string[] TabIds => Enum.GetNames(typeof(Tab));

    /// <summary>Switch the active settings tab by name (case-insensitive,
    /// matches the <see cref="Tab"/> enum). Returns false for an unknown name.
    /// Used by the dev server to preview each options tab.</summary>
    public bool SetActiveTab(string name)
    {
        if (Enum.TryParse<Tab>(name, true, out var tab))
        {
            _activeTab = tab;
            return true;
        }
        return false;
    }

    // Panel dimensions
    private const int PanelW = 600;
    private const int PanelH = 500;

    // Shadow mode options for combo
    private static readonly string[] ShadowModeOptions = { "Ellipse", "Shader" };

    public SettingsWindow(EditorBase ui)
    {
        _ui = ui;
    }

    public void SetGameData(GameData gameData, string settingsJsonPath, string weatherJsonPath = "")
    {
        _gameData = gameData;
        _settingsJsonPath = settingsJsonPath;
        _weatherJsonPath = weatherJsonPath;
    }

    public void SetDayNightSystem(GameSystems.DayNightSystem system) => _dayNightSystem = system;

    /// <summary>
    /// Called each frame from Game1.Update when MenuState == Settings.
    /// Handles scroll, ESC to close, and auto-save on dirty.
    /// </summary>
    public void Update(int screenW, int screenH, GameTime gameTime)
    {
        // Auto-save when dirty. The delegated tabs mark dirty every frame they
        // are displayed (they write through to the settings objects live), so
        // debounce the check — Save itself is if-changed (JsonFile), meaning
        // the disk is only touched when a value actually changed.
        _autoSaveTimer += (float)gameTime.ElapsedGameTime.TotalSeconds;
        if (_autoSaveTimer < AutoSaveInterval) return;
        _autoSaveTimer = 0f;

        if (_dirty)
        {
            _dirty = false;
            _gameData.Settings.Save(_settingsJsonPath);
        }
        if (_weatherDirty)
        {
            _weatherDirty = false;
            if (!string.IsNullOrEmpty(_weatherJsonPath))
            {
                _gameData.Weather.Save(_weatherJsonPath);
            }
        }
    }

    // Auto-save debounce (seconds between dirty checks).
    private const float AutoSaveInterval = 0.5f;
    private float _autoSaveTimer;

    /// <summary>
    /// Called each frame from Game1.Draw when MenuState == Settings.
    /// Draws the full settings panel with tabs and content.
    /// </summary>
    public void Draw(int screenW, int screenH)
    {
        // Dark overlay (only if setting enabled)
        if (_gameData.Settings.General.PauseDimBackground)
            _ui.DrawRect(new Rectangle(0, 0, screenW, screenH), new Color(0, 0, 0, 180));

        int panelX = (screenW - PanelW) / 2;
        int panelY = (screenH - PanelH) / 2;

        // Panel background
        _ui.DrawRect(new Rectangle(panelX, panelY, PanelW, PanelH), EditorBase.PanelBg);
        _ui.DrawBorder(new Rectangle(panelX, panelY, PanelW, PanelH), EditorBase.PanelBorder);

        // Top accent line
        _ui.DrawRect(new Rectangle(panelX, panelY, PanelW, 3), EditorBase.AccentColor);

        // Title
        string title = "SETTINGS";
        var titleSize = _ui.MeasureText(title);
        _ui.DrawText(title, new Vector2(panelX + PanelW / 2f - titleSize.X / 2f, panelY + 8), EditorBase.TextBright);

        // Tab buttons
        int tabY = panelY + 32;
        int tabCount = TabNames.Length;
        int tabW = (PanelW - 20) / tabCount;
        int tabH = 26;
        int tabStartX = panelX + 10;

        for (int i = 0; i < tabCount; i++)
        {
            int tx = tabStartX + i * tabW;
            bool isActive = (int)_activeTab == i;
            Color tabBg = isActive ? EditorBase.ItemSelected : EditorBase.ButtonBg;

            if (_ui.DrawButton(TabNames[i], tx, tabY, tabW - 2, tabH, tabBg))
            {
                _activeTab = (Tab)i;
            }
        }

        // Content area
        int contentX = panelX + 15;
        int contentY = tabY + tabH + 8;
        int contentW = PanelW - 30;
        int contentH = PanelH - (contentY - panelY) - 45; // leave room for Back button

        // Draw content background
        _ui.DrawRect(new Rectangle(contentX - 5, contentY - 2, contentW + 10, contentH + 4), new Color(20, 20, 35, 200));

        // Clip the content area for scrolling
        var clipRect = new Rectangle(contentX - 5, contentY - 2, contentW + 10, contentH + 4);
        _ui.BeginClip(clipRect);

        // Handle scroll wheel. Must go through EditorBase.HandlePanelScroll (raw
        // mouse delta + EditorBase's own _scrollConsumed flag) — NOT
        // _input.ScrollDelta/IsScrollConsumed. PopupManager.RouteInput already
        // consumed the InputState scroll earlier this frame because the settings
        // modal layer covers the full screen (top.ContainsMouse is always true),
        // so _input.IsScrollConsumed is permanently true here and wheel scrolling
        // would never fire. The id-keyed overload clamps to the end using last
        // frame's content height (recorded below), so the wheel stops at the edge.
        int tabIdx = (int)_activeTab;
        _ui.HandlePanelScroll(clipRect, ref _tabScroll[tabIdx], TabScrollIds[tabIdx], contentH);

        int scrollOffset = (int)_tabScroll[tabIdx];
        int y = contentY - scrollOffset;

        // Draw the active tab content
        int totalContentHeight;
        switch (_activeTab)
        {
            case Tab.Bloom:
                totalContentHeight = DrawBloomTab(contentX, y, contentW);
                break;
            case Tab.Shadow:
                totalContentHeight = DrawShadowTab(contentX, y, contentW);
                break;
            case Tab.Environment:
                totalContentHeight = SettingsEnvironmentTab.Draw(_ui, _gameData.Settings.Grass, contentX, y, contentW);
                MarkDirty();
                break;
            case Tab.Weather:
                totalContentHeight = SettingsWeatherTab.Draw(_ui, _gameData.Settings.Weather, _gameData, contentX, y, contentW, _dayNightSystem);
                MarkDirty();
                _weatherDirty = true;
                break;
            case Tab.General:
                totalContentHeight = DrawGeneralTab(contentX, y, contentW);
                break;
            case Tab.Horde:
                totalContentHeight = DrawHordeTab(contentX, y, contentW);
                break;
            case Tab.FogOfWar:
                totalContentHeight = DrawFogOfWarTab(contentX, y, contentW);
                break;
            case Tab.Corruption:
                totalContentHeight = DrawCorruptionTab(contentX, y, contentW);
                break;
            case Tab.Tooltips:
                totalContentHeight = DrawTooltipsTab(contentX, y, contentW);
                break;
            case Tab.Animation:
                totalContentHeight = DrawAnimationTab(contentX, y, contentW);
                break;
            default:
                totalContentHeight = 0;
                break;
        }

        // Remember this frame's extent so next frame's wheel clamp is accurate.
        _ui.SetPanelContentHeight(TabScrollIds[tabIdx], totalContentHeight);

        // Clamp scroll so we don't scroll past the content
        float maxScroll = Math.Max(0, totalContentHeight - contentH);
        if (_tabScroll[tabIdx] > maxScroll) _tabScroll[tabIdx] = maxScroll;

        _ui.EndClip();

        // Canonical draggable scrollbar (shared EditorBase implementation);
        // no-op (and resets scroll to 0) when the content fits.
        if (totalContentHeight > contentH)
        {
            _tabScroll[tabIdx] = _ui.DrawVScrollbar(TabScrollIds[tabIdx],
                panelX + PanelW - 15, contentY, contentH, totalContentHeight, _tabScroll[tabIdx]);
        }

        // Back button at bottom
        int backW = 100;
        int backH = 28;
        int backX = panelX + (PanelW - backW) / 2;
        int backY = panelY + PanelH - 38;
        if (_ui.DrawButton("Back", backX, backY, backW, backH))
        {
            WantsClose = true;
        }

        // Draw color picker popup (must be after all content drawing, before dropdowns)
        _ui.DrawColorPickerPopup();

        // Draw dropdown overlays (must be after all content drawing)
        _ui.DrawDropdownOverlays();

        // Mark mouse over UI so the game doesn't process clicks behind the panel
        _ui.SetMouseOverUI();
    }

    // ----------------------------------------------------------------
    //  Bloom Tab
    // ----------------------------------------------------------------
    private int DrawBloomTab(int x, int y, int w)
    {
        int startY = y;
        int rowH = 26;
        var bloom = _gameData.Settings.Bloom;

        // SET01: Enabled
        bool enabled = _ui.DrawCheckbox("Enabled", bloom.Enabled, x, y);
        if (enabled != bloom.Enabled) { bloom.Enabled = enabled; MarkDirty(); }
        y += rowH;

        y += 6; // spacing

        // SET02: Threshold (0.0 - 2.0)
        float threshold = _ui.DrawSliderFloat("set_bloom_threshold", "Threshold", bloom.Threshold, 0f, 2f, x, y, w);
        if (MathF.Abs(threshold - bloom.Threshold) > 0.0001f) { bloom.Threshold = threshold; MarkDirty(); }
        y += rowH;

        // SET03: SoftKnee (0.0 - 1.0)
        float softKnee = _ui.DrawFloatField("set_bloom_softknee", "Soft Knee", bloom.SoftKnee, x, y, w, 0.05f);
        if (MathF.Abs(softKnee - bloom.SoftKnee) > 0.0001f) { bloom.SoftKnee = Math.Clamp(softKnee, 0f, 1f); MarkDirty(); }
        y += rowH;

        // SET04: Intensity (0.0 - 15.0)
        float intensity = _ui.DrawSliderFloat("set_bloom_intensity", "Intensity", bloom.Intensity, 0f, 15f, x, y, w);
        if (MathF.Abs(intensity - bloom.Intensity) > 0.0001f) { bloom.Intensity = intensity; MarkDirty(); }
        y += rowH;

        // SET05: Scatter (0.0 - 1.0)
        float scatter = _ui.DrawFloatField("set_bloom_scatter", "Scatter", bloom.Scatter, x, y, w, 0.05f);
        if (MathF.Abs(scatter - bloom.Scatter) > 0.0001f) { bloom.Scatter = Math.Clamp(scatter, 0f, 1f); MarkDirty(); }
        y += rowH;

        // SET06: Iterations (2 - 8)
        int iterations = _ui.DrawIntField("set_bloom_iterations", "Iterations", bloom.Iterations, x, y, w);
        iterations = Math.Clamp(iterations, 2, 8);
        if (iterations != bloom.Iterations) { bloom.Iterations = iterations; MarkDirty(); }
        y += rowH;

        // SET07: BicubicUpsampling
        bool bicubic = _ui.DrawCheckbox("Bicubic Upsampling", bloom.BicubicUpsampling, x, y);
        if (bicubic != bloom.BicubicUpsampling) { bloom.BicubicUpsampling = bicubic; MarkDirty(); }
        y += rowH;

        y += 6; // spacing

        // SET08: Tonemap (shoulder rolloff instead of hard clip at 1.0)
        bool tonemap = _ui.DrawCheckbox("Tonemap", bloom.Tonemap, x, y);
        if (tonemap != bloom.Tonemap) { bloom.Tonemap = tonemap; MarkDirty(); }
        y += rowH;

        // SET09: Tonemap Shoulder (0.0 - 0.95) — rolloff start; below it the image is untouched
        float shoulder = _ui.DrawFloatField("set_bloom_tm_shoulder", "Shoulder", bloom.TonemapShoulder, x, y, w, 0.05f);
        if (MathF.Abs(shoulder - bloom.TonemapShoulder) > 0.0001f) { bloom.TonemapShoulder = Math.Clamp(shoulder, 0f, 0.95f); MarkDirty(); }
        y += rowH;

        // SET10: Tonemap White Point (1.1 - 16) — HDR value that maps to pure white
        float whitePoint = _ui.DrawSliderFloat("set_bloom_tm_whitepoint", "White Point", bloom.TonemapWhitePoint, 1.1f, 16f, x, y, w);
        if (MathF.Abs(whitePoint - bloom.TonemapWhitePoint) > 0.0001f) { bloom.TonemapWhitePoint = whitePoint; MarkDirty(); }
        y += rowH;

        // SET11: Tonemap Desaturate (0 - 1) — 0 keeps glow colored, 1 bleaches hot cores to white
        float desat = _ui.DrawSliderFloat("set_bloom_tm_desat", "Desaturate", bloom.TonemapDesaturate, 0f, 1f, x, y, w);
        if (MathF.Abs(desat - bloom.TonemapDesaturate) > 0.0001f) { bloom.TonemapDesaturate = desat; MarkDirty(); }
        y += rowH;

        return y - startY;
    }

    // ----------------------------------------------------------------
    //  Shadow Tab
    // ----------------------------------------------------------------
    private int DrawShadowTab(int x, int y, int w)
    {
        int startY = y;
        int rowH = 26;
        var shadow = _gameData.Settings.Shadow;

        // SET67: Enabled
        bool enabled = _ui.DrawCheckbox("Enabled", shadow.Enabled, x, y);
        if (enabled != shadow.Enabled) { shadow.Enabled = enabled; MarkDirty(); }
        y += rowH;

        y += 6; // spacing

        // SET68: SunAngle (0 - 360)
        float sunAngle = _ui.DrawSliderFloat("set_shadow_sunangle", "Sun Angle", shadow.SunAngle, 0f, 360f, x, y, w);
        if (MathF.Abs(sunAngle - shadow.SunAngle) > 0.01f) { shadow.SunAngle = sunAngle; MarkDirty(); }
        y += rowH;

        // SET69: LengthScale (0.0 - 2.0)
        float lengthScale = _ui.DrawFloatField("set_shadow_lengthscale", "Length Scale", shadow.LengthScale, x, y, w, 0.05f);
        if (MathF.Abs(lengthScale - shadow.LengthScale) > 0.0001f) { shadow.LengthScale = Math.Clamp(lengthScale, 0f, 2f); MarkDirty(); }
        y += rowH;

        // SET70: Opacity (0.0 - 1.0)
        float opacity = _ui.DrawSliderFloat("set_shadow_opacity", "Opacity", shadow.Opacity, 0f, 1f, x, y, w);
        if (MathF.Abs(opacity - shadow.Opacity) > 0.0001f) { shadow.Opacity = opacity; MarkDirty(); }
        y += rowH;

        // SET71: Squash (0.0 - 0.5)
        float squash = _ui.DrawFloatField("set_shadow_squash", "Squash", shadow.Squash, x, y, w, 0.01f);
        if (MathF.Abs(squash - shadow.Squash) > 0.0001f) { shadow.Squash = Math.Clamp(squash, 0f, 0.5f); MarkDirty(); }
        y += rowH;

        // SET72: UnitShadowMode combo
        string currentMode = shadow.UnitShadowMode >= 0 && shadow.UnitShadowMode < ShadowModeOptions.Length
            ? ShadowModeOptions[shadow.UnitShadowMode]
            : ShadowModeOptions[0];
        string newMode = _ui.DrawCombo("set_shadow_mode", "Shadow Mode", currentMode, ShadowModeOptions, x, y, w);
        int newModeIdx = Array.IndexOf(ShadowModeOptions, newMode);
        if (newModeIdx >= 0 && newModeIdx != shadow.UnitShadowMode) { shadow.UnitShadowMode = newModeIdx; MarkDirty(); }
        y += rowH;

        return y - startY;
    }

    // ----------------------------------------------------------------
    //  General Tab (delegates to SettingsGeneralTab)
    // ----------------------------------------------------------------
    private int DrawGeneralTab(int x, int y, int w)
    {
        int startY = y;
        int height = SettingsGeneralTab.Draw(_ui, _gameData.Settings.General, _gameData.Settings.Performance, _gameData.Settings.Combat, x, y, w);
        MarkDirty(); // any interaction marks dirty; auto-save on next Update
        return height;
    }

    // ----------------------------------------------------------------
    //  Horde / Movement Tab (delegates to SettingsHordeTab)
    // ----------------------------------------------------------------
    private int DrawHordeTab(int x, int y, int w)
    {
        int startY = y;
        int height = SettingsHordeTab.Draw(_ui, _gameData.Settings.Horde, x, y, w);
        MarkDirty();
        return height;
    }

    // ----------------------------------------------------------------
    //  Fog of War tab
    // ----------------------------------------------------------------
    // Index order must match the FogOfWarMode enum
    // (Off = 0, Explored = 1, FogOfWar = 2, Hybrid = 3).
    private static readonly string[] FogModeNames = { "Off", "Explored", "Fog of War", "Hybrid" };

    private int DrawFogOfWarTab(int x, int y, int w)
    {
        int startY = y;
        int rowH = 26;
        var fog = _gameData.Settings.FogOfWar;

        // Mode combo
        string currentMode = fog.Mode >= 0 && fog.Mode < FogModeNames.Length ? FogModeNames[fog.Mode] : FogModeNames[0];
        string newMode = _ui.DrawCombo("set_fog_mode", "Mode", currentMode, FogModeNames, x, y, w);
        int newModeIdx = Array.IndexOf(FogModeNames, newMode);
        if (newModeIdx >= 0 && newModeIdx != fog.Mode) { fog.Mode = newModeIdx; MarkDirty(); }
        y += rowH;

        y += 6;

        // Default sight range (for units with 0 detectionRange)
        float sightRange = _ui.DrawFloatField("set_fog_sight", "Default Sight", fog.DefaultSightRange, x, y, w, 1f);
        if (MathF.Abs(sightRange - fog.DefaultSightRange) > 0.01f) { fog.DefaultSightRange = MathF.Max(1f, sightRange); MarkDirty(); }
        y += rowH;

        // Unexplored alpha
        float unexploredA = _ui.DrawFloatField("set_fog_unexplored", "Unexplored Alpha", fog.UnexploredAlpha, x, y, w, 0.05f);
        if (MathF.Abs(unexploredA - fog.UnexploredAlpha) > 0.001f) { fog.UnexploredAlpha = Math.Clamp(unexploredA, 0f, 1f); MarkDirty(); }
        y += rowH;

        // Fogged alpha
        float foggedA = _ui.DrawFloatField("set_fog_fogged", "Fogged Alpha", fog.FoggedAlpha, x, y, w, 0.05f);
        if (MathF.Abs(foggedA - fog.FoggedAlpha) > 0.001f) { fog.FoggedAlpha = Math.Clamp(foggedA, 0f, 1f); MarkDirty(); }
        y += rowH;

        y += 10;

        // Info text
        _ui.DrawText("Off: Full map vision", new Vector2(x, y), EditorBase.TextDim);
        y += 16;
        _ui.DrawText("Explored: Black until scouted, then permanent", new Vector2(x, y), EditorBase.TextDim);
        y += 16;
        _ui.DrawText("Fog of War: Unseen=black, fogged=grey, visible=full", new Vector2(x, y), EditorBase.TextDim);
        y += 20;

        return y - startY;
    }

    // ----------------------------------------------------------------
    //  Corruption tab — death-fog gameplay + visual tunables.
    //  Values flow back into the live systems each frame via
    //  Game1.SyncCorruptionSettings, so changes take effect immediately.
    // ----------------------------------------------------------------
    private int DrawCorruptionTab(int x, int y, int w)
    {
        int startY = y;
        int rowH = 26;
        var c = _gameData.Settings.Corruption;

        DrawSectionHeader("Trees", x, ref y, w);

        float treeHeal = _ui.DrawFloatField("set_corr_treeheal", "Heal Rate (stress/s)", c.TreeHealRate, x, y, w, 0.5f);
        if (MathF.Abs(treeHeal - c.TreeHealRate) > 0.001f) { c.TreeHealRate = MathF.Max(0f, treeHeal); MarkDirty(); }
        y += rowH;

        float treeThr = _ui.DrawFloatField("set_corr_treethr", "Threshold (stress)", c.TreeThreshold, x, y, w, 1f);
        if (MathF.Abs(treeThr - c.TreeThreshold) > 0.001f) { c.TreeThreshold = MathF.Max(0.1f, treeThr); MarkDirty(); }
        y += rowH;

        float treeAbsorb = _ui.DrawFloatField("set_corr_treeabsorb", "Corrupted Absorb (fog/s)", c.TreeCorruptedAbsorbRate, x, y, w, 0.05f);
        if (MathF.Abs(treeAbsorb - c.TreeCorruptedAbsorbRate) > 0.001f) { c.TreeCorruptedAbsorbRate = MathF.Max(0f, treeAbsorb); MarkDirty(); }
        y += rowH;

        float treeFade = _ui.DrawFloatField("set_corr_treefade", "Dissolve Duration (s)", c.TreeFadeDuration, x, y, w, 0.5f);
        if (MathF.Abs(treeFade - c.TreeFadeDuration) > 0.001f) { c.TreeFadeDuration = MathF.Max(0.1f, treeFade); MarkDirty(); }
        y += rowH;

        DrawSectionHeader("Ground", x, ref y, w);

        float groundRate = _ui.DrawFloatField("set_corr_groundrate", "Max Rate at d=1 (/s)", c.GroundMaxRatePerSec, x, y, w, 0.01f);
        if (MathF.Abs(groundRate - c.GroundMaxRatePerSec) > 0.0001f) { c.GroundMaxRatePerSec = Math.Clamp(groundRate, 0f, 1f); MarkDirty(); }
        y += rowH;

        float groundFade = _ui.DrawFloatField("set_corr_groundfade", "Fade Duration (s)", c.GroundFadeDuration, x, y, w, 0.5f);
        if (MathF.Abs(groundFade - c.GroundFadeDuration) > 0.001f) { c.GroundFadeDuration = MathF.Max(0.1f, groundFade); MarkDirty(); }
        y += rowH;

        DrawSectionHeader("Grass", x, ref y, w);

        float grassFade = _ui.DrawFloatField("set_corr_grassfade", "Tint Fade Duration (s)", c.GrassFadeDuration, x, y, w, 0.5f);
        if (MathF.Abs(grassFade - c.GrassFadeDuration) > 0.001f) { c.GrassFadeDuration = MathF.Max(0.1f, grassFade); MarkDirty(); }
        y += rowH;

        DrawSectionHeader("Fog Simulation", x, ref y, w);

        float diff = _ui.DrawFloatField("set_corr_diff", "Diffusion Rate (<0.25)", c.DiffusionRate, x, y, w, 0.01f);
        if (MathF.Abs(diff - c.DiffusionRate) > 0.0001f) { c.DiffusionRate = Math.Clamp(diff, 0f, 0.249f); MarkDirty(); }
        y += rowH;

        float src = _ui.DrawFloatField("set_corr_src", "Source Rate Scale", c.SourceRateScale, x, y, w, 0.1f);
        if (MathF.Abs(src - c.SourceRateScale) > 0.001f) { c.SourceRateScale = MathF.Max(0f, src); MarkDirty(); }
        y += rowH;

        float sink = _ui.DrawFloatField("set_corr_sink", "Sink Rate Scale", c.SinkRateScale, x, y, w, 0.1f);
        if (MathF.Abs(sink - c.SinkRateScale) > 0.001f) { c.SinkRateScale = MathF.Max(0f, sink); MarkDirty(); }
        y += rowH;

        DrawSectionHeader("Fog Visual (death-fog overlay)", x, ref y, w);

        float vis = _ui.DrawFloatField("set_corr_vis", "Visibility Threshold", c.FogVisibilityThreshold, x, y, w, 0.005f);
        if (MathF.Abs(vis - c.FogVisibilityThreshold) > 0.0001f) { c.FogVisibilityThreshold = Math.Clamp(vis, 0f, 1f); MarkDirty(); }
        y += rowH;

        float sat = _ui.DrawFloatField("set_corr_sat", "Saturation Density", c.FogSaturationDensity, x, y, w, 0.05f);
        if (MathF.Abs(sat - c.FogSaturationDensity) > 0.0001f) { c.FogSaturationDensity = MathF.Max(0.01f, sat); MarkDirty(); }
        y += rowH;

        float maxA = _ui.DrawSliderFloat("set_corr_maxa", "Max Alpha", c.FogMaxAlpha, 0f, 1f, x, y, w);
        if (MathF.Abs(maxA - c.FogMaxAlpha) > 0.001f) { c.FogMaxAlpha = maxA; MarkDirty(); }
        y += rowH;

        float cyc = _ui.DrawFloatField("set_corr_cyc", "Flipbook Cycle (s)", c.FogFlipbookCycleSeconds, x, y, w, 0.25f);
        if (MathF.Abs(cyc - c.FogFlipbookCycleSeconds) > 0.001f) { c.FogFlipbookCycleSeconds = MathF.Max(0.1f, cyc); MarkDirty(); }
        y += rowH;

        float puffSize = _ui.DrawFloatField("set_corr_puffsize", "Puff Size (× cell)", c.FogPuffWorldSizeMultiplier, x, y, w, 0.1f);
        if (MathF.Abs(puffSize - c.FogPuffWorldSizeMultiplier) > 0.001f) { c.FogPuffWorldSizeMultiplier = MathF.Max(0.1f, puffSize); MarkDirty(); }
        y += rowH;

        float jit = _ui.DrawSliderFloat("set_corr_jit", "Position Jitter", c.FogPositionJitter, 0f, 1f, x, y, w);
        if (MathF.Abs(jit - c.FogPositionJitter) > 0.001f) { c.FogPositionJitter = jit; MarkDirty(); }
        y += rowH;

        // Fog tint colour swatch
        _ui.DrawText("Fog Tint:", new Vector2(x, y), EditorBase.AccentColor);
        var tintHdr = new Core.HdrColor((byte)c.FogTint.R, (byte)c.FogTint.G, (byte)c.FogTint.B, (byte)c.FogTint.A, 1.0f);
        if (_ui.DrawColorSwatch("set_corr_tint", x + 90, y, 40, 18, ref tintHdr, hideIntensity: true))
        {
            c.FogTint.R = tintHdr.R; c.FogTint.G = tintHdr.G; c.FogTint.B = tintHdr.B; c.FogTint.A = tintHdr.A;
            MarkDirty();
        }
        y += rowH;

        return y - startY;
    }

    // ----------------------------------------------------------------
    //  Tooltips tab — how the unit stat sheet is surfaced. Intentionally
    //  exposes the raw tuning knobs (pick radius, pause) so we can iterate
    //  on the hover/inspect feel aggressively without touching code.
    // ----------------------------------------------------------------
    /// <summary>Animation-feel settings (Settings.Animation → per-machine
    /// 'user settings/settings.json'). Cast-plant knobs: casting brakes the
    /// player to a stop before the cast anim starts — see
    /// todos/player_cast_plant.md for the design + decision log.</summary>
    private int DrawAnimationTab(int x, int y, int w)
    {
        int startY = y;
        int rowH = 26;
        var a = _gameData.Settings.Animation;

        DrawSectionHeader("Player Casting", x, ref y, w);

        // Q3: recovery frames cancel into locomotion when movement is held.
        bool tail = _ui.DrawCheckbox("Cancel cast recovery into movement (hold a direction)", a.CastTailCancel, x, y);
        if (tail != a.CastTailCancel) { a.CastTailCancel = tail; MarkDirty(); }
        y += rowH;

        // How hard the cast brake stops the run (multiplies normal deceleration).
        float brake = _ui.DrawSliderFloat("set_anim_castbrake", "Cast Brake (x decel)", a.CastBrakeMultiplier, 1f, 5f, x, y, w);
        if (MathF.Abs(brake - a.CastBrakeMultiplier) > 0.0001f) { a.CastBrakeMultiplier = brake; MarkDirty(); }
        y += rowH;

        // How fast the body pivots to the aim point while casting (x turn speed).
        float turn = _ui.DrawSliderFloat("set_anim_castturn", "Cast Turn Boost (x turn speed)", a.CastTurnBoost, 1f, 6f, x, y, w);
        if (MathF.Abs(turn - a.CastTurnBoost) > 0.0001f) { a.CastTurnBoost = turn; MarkDirty(); }
        y += rowH;

        // Anim-start gate as a multiple of base (walking) speed: 1.15 = casts start
        // at walking pace or slower; lower = must brake closer to a full stop.
        float gate = _ui.DrawSliderFloat("set_anim_castgate", "Cast Start Gate (x walk speed)", a.CastPlantGateSpeedMult, 0.2f, 2f, x, y, w);
        if (MathF.Abs(gate - a.CastPlantGateSpeedMult) > 0.0001f) { a.CastPlantGateSpeedMult = gate; MarkDirty(); }
        y += rowH;

        y += 6;
        DrawSectionHeader("Hit Feedback", x, ref y, w);

        // White flash on damaged units — the feedback that survives suppressed
        // flinches (fleeing / mid-attack / reaction-cooldown units).
        bool flash = _ui.DrawCheckbox("Flash units white when hit", a.HitFlashEnabled, x, y);
        if (flash != a.HitFlashEnabled) { a.HitFlashEnabled = flash; MarkDirty(); }
        y += rowH;

        float fi = _ui.DrawSliderFloat("set_anim_flashint", "Hit Flash Intensity", a.HitFlashIntensity, 0.2f, 1f, x, y, w);
        if (MathF.Abs(fi - a.HitFlashIntensity) > 0.0001f) { a.HitFlashIntensity = fi; MarkDirty(); }
        y += rowH;

        return y - startY;
    }

    private int DrawTooltipsTab(int x, int y, int w)
    {
        int startY = y;
        int rowH = 26;
        var t = _gameData.Settings.Tooltips;

        DrawSectionHeader("Unit Stat Sheet", x, ref y, w);

        // Main toggle: hover-to-show vs press-L-to-inspect.
        bool autoShow = _ui.DrawCheckbox("Auto-show on hover (Factorio-style)", t.AutoShowUnitStats, x, y);
        if (autoShow != t.AutoShowUnitStats) { t.AutoShowUnitStats = autoShow; MarkDirty(); }
        y += rowH;

        // Pick radius — how close the cursor must be to grab a unit.
        float pick = _ui.DrawSliderFloat("set_tip_pickradius", "Cursor Pick Radius", t.HoverPickRadius, 0.25f, 5f, x, y, w);
        if (MathF.Abs(pick - t.HoverPickRadius) > 0.0001f) { t.HoverPickRadius = pick; MarkDirty(); }
        y += rowH;

        // Pause-on-inspect only matters in press-L mode (auto-show never pauses).
        if (!t.AutoShowUnitStats)
        {
            bool pause = _ui.DrawCheckbox("Pause game when inspecting (L)", t.PauseOnManualInspect, x, y);
            if (pause != t.PauseOnManualInspect) { t.PauseOnManualInspect = pause; MarkDirty(); }
            y += rowH;
        }

        y += 6;
        DrawSectionHeader("Ground Objects", x, ref y, w);

        // Buildings / structures hover tooltip.
        bool bInfo = _ui.DrawCheckbox("Show building info on hover", t.ShowBuildingInfo, x, y);
        if (bInfo != t.ShowBuildingInfo) { t.ShowBuildingInfo = bInfo; MarkDirty(); }
        y += rowH;

        // Foragable ground-item hover tooltip.
        bool iInfo = _ui.DrawCheckbox("Show ground item info on hover", t.ShowGroundItemInfo, x, y);
        if (iInfo != t.ShowGroundItemInfo) { t.ShowGroundItemInfo = iInfo; MarkDirty(); }
        y += rowH;

        // Corpse hover tooltip.
        bool cInfo = _ui.DrawCheckbox("Show corpse info on hover", t.ShowCorpseInfo, x, y);
        if (cInfo != t.ShowCorpseInfo) { t.ShowCorpseInfo = cInfo; MarkDirty(); }
        y += rowH;

        // Lightweight unit hover tooltip (name, HP, membership). Suppressed while
        // the auto stat sheet is on — that panel already shows everything.
        bool uInfo = _ui.DrawCheckbox("Show unit info on hover", t.ShowUnitInfo, x, y);
        if (uInfo != t.ShowUnitInfo) { t.ShowUnitInfo = uInfo; MarkDirty(); }
        y += rowH;

        // Pick radius for ground objects (buildings + items).
        float gpick = _ui.DrawSliderFloat("set_tip_groundpick", "Ground Pick Radius", t.GroundPickRadius, 0.5f, 6f, x, y, w);
        if (MathF.Abs(gpick - t.GroundPickRadius) > 0.0001f) { t.GroundPickRadius = gpick; MarkDirty(); }
        y += rowH;

        // Highlight the world object under the cursor.
        bool hlBox = _ui.DrawCheckbox("Highlight object under cursor", t.ShowHoverHighlight, x, y);
        if (hlBox != t.ShowHoverHighlight) { t.ShowHoverHighlight = hlBox; MarkDirty(); }
        y += rowH;

        // Per-category marker style: separate shape + line style for buildings vs everything else,
        // each encoded as shape*4 + lineStyle. Defaults: buildings = Diamond (iso footprint), the
        // rest = Circle (RTS ring).
        if (t.ShowHoverHighlight)
        {
            string[] shapeNames = { "Circle", "Corners", "Rectangle", "Ground Box", "Diamond Box" };
            string[] styleNames = { "Thick Solid", "Thin Solid", "Thick Faint", "Thin Faint" };

            void MarkerRows(string id, Func<int> get, Action<int> set)
            {
                int v = System.Math.Clamp(get(), 0, 19);
                int shape = v / 4, style = v % 4;
                string ns = _ui.DrawCombo(id + "_shape", "Shape", shapeNames[shape], shapeNames, x + 12, y, w - 12);
                int nsi = System.Array.IndexOf(shapeNames, ns); if (nsi < 0) nsi = shape;
                y += rowH;
                string nst = _ui.DrawCombo(id + "_style", "Line style", styleNames[style], styleNames, x + 12, y, w - 12);
                int nsti = System.Array.IndexOf(styleNames, nst); if (nsti < 0) nsti = style;
                y += rowH;
                int nv = nsi * 4 + nsti;
                if (nv != v) { set(nv); MarkDirty(); }
            }

            _ui.DrawText("Buildings", new Vector2(x, y), EditorBase.TextColor); y += 18;
            MarkerRows("hl_bld", () => t.HoverHighlightBuilding, vv => t.HoverHighlightBuilding = vv);
            _ui.DrawText("Other objects (units, corpses, items)", new Vector2(x, y), EditorBase.TextColor); y += 18;
            MarkerRows("hl_rest", () => t.HoverHighlightRest, vv => t.HoverHighlightRest = vv);
        }

        y += 6;
        DrawSectionHeader("World Position Debug", x, ref y, w);

        // Bottom-left readout of the exact world position under the cursor.
        bool worldDbg = _ui.DrawCheckbox("Show world position info (bottom-left)", t.ShowWorldHoverDebug, x, y);
        if (worldDbg != t.ShowWorldHoverDebug) { t.ShowWorldHoverDebug = worldDbg; MarkDirty(); }
        y += rowH;

        y += 10;

        // Info text explaining the two modes.
        if (t.AutoShowUnitStats)
        {
            _ui.DrawText("Hover any unit to show its stat sheet.", new Vector2(x, y), EditorBase.TextDim);
            y += 16;
            _ui.DrawText("Move off the unit to dismiss. No pause.", new Vector2(x, y), EditorBase.TextDim);
            y += 16;
        }
        else
        {
            _ui.DrawText("Press 'L' over a unit to pin its stat sheet.", new Vector2(x, y), EditorBase.TextDim);
            y += 16;
            _ui.DrawText("Press 'L' again to close.", new Vector2(x, y), EditorBase.TextDim);
            y += 16;
        }
        _ui.DrawText("'U' always opens the necromancer's sheet.", new Vector2(x, y), EditorBase.TextDim);
        y += 20;

        return y - startY;
    }

    private void DrawSectionHeader(string label, int x, ref int y, int w)
        => _ui.DrawSectionHeader(label, x, ref y, w);

    // ----------------------------------------------------------------
    //  Stub tabs (placeholder for future implementation)
    // ----------------------------------------------------------------
    private int DrawStubTab(int x, int y, int w, string tabName)
    {
        int startY = y;
        y += 20;
        _ui.DrawText($"{tabName} settings", new Vector2(x, y), EditorBase.TextBright);
        y += 30;
        _ui.DrawText("Coming soon", new Vector2(x, y), EditorBase.TextDim);
        y += 30;
        return y - startY;
    }

    // ----------------------------------------------------------------
    //  Helpers
    // ----------------------------------------------------------------
    private void MarkDirty()
    {
        _dirty = true;
    }
}
