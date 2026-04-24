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
    private enum Tab { Bloom, Shadow, Environment, Weather, General, Horde, FogOfWar }
    private Tab _activeTab = Tab.Bloom;
    private static readonly string[] TabNames = { "Bloom", "Shadow", "Environ", "Weather", "General", "Horde", "Fog" };

    // Scroll state per tab (keyed by tab name)
    private readonly float[] _tabScroll = new float[7];

    // Track whether we need to save after a frame (dirty flag)
    private bool _dirty;
    private bool _weatherDirty;

    /// <summary>Set to true when the user clicks Back or presses ESC.</summary>
    public bool WantsClose { get; set; }

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
        // Auto-save when dirty
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

        // Handle scroll
        int tabIdx = (int)_activeTab;
        int mx = (int)_ui._input.MousePos.X, my = (int)_ui._input.MousePos.Y;
        if (clipRect.Contains(mx, my) && !_ui._input.IsScrollConsumed && _ui._input.ScrollDelta != 0)
        {
            _tabScroll[tabIdx] -= _ui._input.ScrollDelta * 0.3f;
            if (_tabScroll[tabIdx] < 0) _tabScroll[tabIdx] = 0;
            _ui._input.ConsumeScroll();
        }

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
            default:
                totalContentHeight = 0;
                break;
        }

        // Clamp scroll so we don't scroll past the content
        float maxScroll = Math.Max(0, totalContentHeight - contentH);
        if (_tabScroll[tabIdx] > maxScroll) _tabScroll[tabIdx] = maxScroll;

        _ui.EndClip();

        // Draw scrollbar if content overflows
        if (totalContentHeight > contentH)
        {
            int sbX = panelX + PanelW - 18;
            int sbY = contentY;
            int sbH = contentH;
            _ui.DrawRect(new Rectangle(sbX, sbY, 8, sbH), new Color(30, 30, 50));

            float scrollFraction = maxScroll > 0 ? _tabScroll[tabIdx] / maxScroll : 0f;
            float thumbFraction = (float)contentH / totalContentHeight;
            int thumbH = Math.Max(20, (int)(sbH * thumbFraction));
            int thumbY = sbY + (int)(scrollFraction * (sbH - thumbH));
            _ui.DrawRect(new Rectangle(sbX, thumbY, 8, thumbH), EditorBase.AccentColor);
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
    private static readonly string[] FogModeNames = { "Off", "Explored", "Fog of War" };

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
