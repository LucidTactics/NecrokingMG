using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;

namespace Necroking.Render;

public class WeatherRenderer
{
    private int _screenW, _screenH;
    private float _flashIntensity;
    private float _flashTimer = 5.0f;
    private float _doubleFlashTimer = -1.0f;
    private readonly Random _rng = new();

    // Dependencies set via SetContext each frame
    private SpriteBatch _spriteBatch = null!;
    private Texture2D _pixel = null!;
    private Texture2D _glowTex = null!;
    private Camera25D _camera = null!;
    private float _gameTime;
    private GameData _gameData = null!;
    private Microsoft.Xna.Framework.Graphics.Effect? _fogEffect;
    private GraphicsDevice _graphicsDevice = null!;

    public void Init(int screenW, int screenH)
    {
        _screenW = screenW;
        _screenH = screenH;
    }

    public void Resize(int screenW, int screenH)
    {
        _screenW = screenW;
        _screenH = screenH;
    }

    /// <summary>
    /// Load the fog shader effect. Call from Game1.LoadContent.
    /// </summary>
    public void LoadEffect(Microsoft.Xna.Framework.Graphics.Effect? fogEffect)
    {
        _fogEffect = fogEffect;
    }

    /// <summary>
    /// Set per-frame context references. Call before DrawRain/DrawFog each frame.
    /// </summary>
    public void SetContext(SpriteBatch spriteBatch, Texture2D pixel, Texture2D glowTex,
        Camera25D camera, float gameTime, GameData gameData, GraphicsDevice graphicsDevice)
    {
        _spriteBatch = spriteBatch;
        _pixel = pixel;
        _glowTex = glowTex;
        _camera = camera;
        _gameTime = gameTime;
        _gameData = gameData;
        _graphicsDevice = graphicsDevice;
    }

    /// <summary>
    /// Draw rain in the scene pass so it depth-sorts with world objects.
    /// Call during the main AlphaBlend SpriteBatch pass, after DrawUnitsAndObjects.
    /// </summary>
    public void DrawRain(int screenW, int screenH)
    {
        if (!_gameData.Settings.Weather.Enabled) return;
        string presetId = _gameData.Settings.Weather.ActivePreset;
        if (string.IsNullOrEmpty(presetId)) return;
        var preset = _gameData.Weather.Get(presetId);
        if (preset == null) return;
        var fx = preset.Effects;
        if (fx.RainDensity <= 0f) return;

        DrawRainParticles(fx, screenW, screenH);
    }

    /// <summary>
    /// Draw fog/haze/brightness overlay. Called in the HUD pass after bloom.
    /// Manages its own SpriteBatch state (ends and restarts the current batch for shader use).
    /// </summary>
    public void DrawFog(int screenW, int screenH)
    {
        // Check if weather is enabled and has a preset
        if (!_gameData.Settings.Weather.Enabled) return;
        string presetId = _gameData.Settings.Weather.ActivePreset;
        if (string.IsNullOrEmpty(presetId)) return;
        var preset = _gameData.Weather.Get(presetId);
        if (preset == null) return;
        var fx = preset.Effects;

        // Rain draws in scene pass via DrawRain() for depth sorting with world objects.

        // Weather overlay (fog, haze, brightness, tint, vignette, lightning flash)
        if (fx.FogDensity > 0.01f || fx.HazeStrength > 0.01f || fx.Brightness < 0.95f
            || fx.TintStrength > 0.01f || fx.VignetteStrength > 0.01f || _flashIntensity > 0.01f)
        {
            if (_fogEffect != null)
            {
                // End current SpriteBatch to switch to shader mode
                _spriteBatch.End();

                // Compute world-space mapping for fog anchoring
                float halfWorldW = screenW * 0.5f / _camera.Zoom;
                float halfWorldH = screenH * 0.5f / (_camera.Zoom * _camera.YRatio);
                var fogOrigin = new Vector2(_camera.Position.X - halfWorldW, _camera.Position.Y - halfWorldH);
                var fogWorldScale = new Vector2(halfWorldW * 2.0f, halfWorldH * 2.0f);

                // Set shader uniforms - fog
                _fogEffect.Parameters["FogDensity"]?.SetValue(fx.FogDensity);
                _fogEffect.Parameters["FogColor"]?.SetValue(new Vector3(fx.FogR, fx.FogG, fx.FogB));
                _fogEffect.Parameters["FogSpeed"]?.SetValue(fx.FogSpeed);
                _fogEffect.Parameters["FogScaleU"]?.SetValue(fx.FogScale);
                _fogEffect.Parameters["Time"]?.SetValue(_gameTime);
                _fogEffect.Parameters["FogWorldOrigin"]?.SetValue(fogOrigin);
                _fogEffect.Parameters["FogWorldScale"]?.SetValue(fogWorldScale);
                _fogEffect.Parameters["HazeStrength"]?.SetValue(fx.HazeStrength);
                _fogEffect.Parameters["HazeColor"]?.SetValue(new Vector3(fx.HazeR, fx.HazeG, fx.HazeB));
                _fogEffect.Parameters["Brightness"]?.SetValue(fx.Brightness);

                // Tint
                _fogEffect.Parameters["TintColor"]?.SetValue(new Vector3(fx.TintR, fx.TintG, fx.TintB));
                _fogEffect.Parameters["TintStrength"]?.SetValue(fx.TintStrength);

                // Vignette
                _fogEffect.Parameters["VignetteStrength"]?.SetValue(fx.VignetteStrength);
                _fogEffect.Parameters["VignetteRadius"]?.SetValue(fx.VignetteRadius);
                _fogEffect.Parameters["VignetteSoftness"]?.SetValue(fx.VignetteSoftness);
                _fogEffect.Parameters["Resolution"]?.SetValue(new Vector2(screenW, screenH));

                // Lightning flash
                _fogEffect.Parameters["FlashIntensity"]?.SetValue(_flashIntensity);

                // Draw fullscreen quad with fog shader (premultiplied alpha output)
                _spriteBatch.Begin(SpriteSortMode.Immediate, BlendState.AlphaBlend, SamplerState.PointClamp,
                    null, null, _fogEffect);
                _spriteBatch.Draw(_pixel, new Rectangle(0, 0, screenW, screenH), Color.White);
                _spriteBatch.End();

                // Resume normal SpriteBatch for remaining HUD drawing
                _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
            }
            else
            {
                // Fallback: flat-color overlays (no shader available)
                if (fx.FogDensity > 0.01f || fx.HazeStrength > 0.01f)
                {
                    float fogAlpha = MathF.Min(fx.FogDensity * 0.5f + fx.HazeStrength * 0.3f, 0.4f);
                    byte fR = (byte)(fx.FogR * 255), fG = (byte)(fx.FogG * 255), fB = (byte)(fx.FogB * 255);
                    _spriteBatch.Draw(_pixel, new Rectangle(0, 0, screenW, screenH),
                        new Color(fR, fG, fB, (byte)(fogAlpha * 255)));
                }
                if (fx.Brightness < 0.95f)
                {
                    float darkAmount = 1f - fx.Brightness;
                    _spriteBatch.Draw(_pixel, new Rectangle(0, 0, screenW, screenH),
                        new Color((byte)0, (byte)0, (byte)0, (byte)(darkAmount * 180)));
                }
                if (fx.TintStrength > 0.01f)
                {
                    // CPU fallback: darken channels where tint < 1 by overlaying black with per-channel alpha
                    // This approximates multiplicative tint: result *= lerp(1, TintColor, TintStrength)
                    float rMul = 1f - (1f - MathF.Min(fx.TintR, 1f)) * fx.TintStrength;
                    float gMul = 1f - (1f - MathF.Min(fx.TintG, 1f)) * fx.TintStrength;
                    float bMul = 1f - (1f - MathF.Min(fx.TintB, 1f)) * fx.TintStrength;
                    // Use darkest channel as overlay alpha, tint the overlay toward the tint color
                    float darkest = MathF.Min(MathF.Min(rMul, gMul), bMul);
                    if (darkest < 0.99f)
                    {
                        byte alpha = (byte)((1f - darkest) * 255);
                        // Overlay color: channels that should stay bright get the tint color, others black
                        byte oR = (byte)(MathUtil.Clamp((1f - rMul) / (1f - darkest), 0f, 1f) * 255);
                        byte oG = (byte)(MathUtil.Clamp((1f - gMul) / (1f - darkest), 0f, 1f) * 255);
                        byte oB = (byte)(MathUtil.Clamp((1f - bMul) / (1f - darkest), 0f, 1f) * 255);
                        _spriteBatch.Draw(_pixel, new Rectangle(0, 0, screenW, screenH),
                            new Color(oR, oG, oB, alpha));
                    }
                }
                if (_flashIntensity > 0.01f)
                {
                    byte flashA = (byte)(MathUtil.Clamp(_flashIntensity * 0.8f, 0f, 1f) * 255);
                    _spriteBatch.Draw(_pixel, new Rectangle(0, 0, screenW, screenH),
                        new Color(flashA, flashA, flashA, flashA));
                }
            }
        }
    }

    public void TriggerFlash() => _flashIntensity = 1f;

    /// <summary>
    /// Update lightning flash timer. Call once per frame from Game1.Update().
    /// </summary>
    public void Update(float dt, GameData gameData)
    {
        _gameData = gameData;
        if (!_gameData.Settings.Weather.Enabled) return;
        string presetId = _gameData.Settings.Weather.ActivePreset;
        if (string.IsNullOrEmpty(presetId)) return;
        var preset = _gameData.Weather.Get(presetId);
        if (preset == null) return;
        var fx = preset.Effects;

        UpdateLightning(fx, dt);
    }

    private void UpdateLightning(WeatherEffects fx, float dt)
    {
        if (!fx.LightningEnabled)
        {
            _flashIntensity = 0f;
            return;
        }

        // Exponential decay
        _flashIntensity *= MathF.Exp(-5.0f * dt);
        if (_flashIntensity < 0.01f) _flashIntensity = 0f;

        // Double-flash follow-up
        if (_doubleFlashTimer > 0f)
        {
            _doubleFlashTimer -= dt;
            if (_doubleFlashTimer <= 0f)
            {
                _flashIntensity = 0.6f + (float)_rng.NextDouble() * 0.2f;
                _doubleFlashTimer = -1f;
            }
        }

        // Main flash timer
        _flashTimer -= dt;
        if (_flashTimer <= 0f)
        {
            _flashIntensity = 0.8f + (float)_rng.NextDouble() * 0.2f;
            float range = fx.LightningMaxInterval - fx.LightningMinInterval;
            _flashTimer = fx.LightningMinInterval + (float)_rng.NextDouble() * range;

            // 30% chance of double-flash
            if (_rng.NextDouble() < 0.3)
                _doubleFlashTimer = 0.12f + (float)_rng.NextDouble() * 0.08f;
        }
    }

    private void DrawRainParticles(WeatherEffects fx, int screenW, int screenH)
    {
        const float RAIN_CELL_SIZE = 2.0f;
        const int RAIN_DROPS_PER_CELL = 8;
        const float RAIN_FALL_HEIGHT = 20.0f;
        const float RAIN_SPLASH_TIME = 0.20f;
        const float RAIN_WIND_DRIFT_SCALE = 0.06f;
        const float RAIN_FADE_BAND = 15.0f;
        const float RAIN_REF_ZOOM = 48.0f;
        const int MAX_RAIN = 6000;
        const int MAX_SPLASHES = 800;

        float zoom = _camera.Zoom;
        float yRatio = _camera.YRatio;
        float heightScale = _camera.HeightScale;
        float minZoom = _camera.MinZoom;
        float maxZoom = _camera.MaxZoom;
        float baseFallRate = fx.RainSpeed / 60.0f;
        float windAngleRad = fx.RainWindAngle * MathF.PI / 180.0f;
        float windSin = MathF.Sin(windAngleRad);
        float globalTime = _gameTime;

        // Zoom-based drop size: 2x at max zoom in, 1x at max zoom out
        float zoomT = MathUtil.Clamp((zoom - minZoom) / (maxZoom - minZoom), 0f, 1f);
        float zoomDropScale = 1.0f + zoomT; // 1.0 at min zoom, 2.0 at max zoom

        float halfScreenW = screenW * 0.5f;
        float halfScreenH = screenH * 0.5f;
        float worldHalfW = halfScreenW / zoom;
        float worldHalfH = halfScreenH / (zoom * yRatio);
        float marginW = RAIN_FALL_HEIGHT * RAIN_WIND_DRIFT_SCALE + 2.0f;
        float marginH = RAIN_FALL_HEIGHT / (zoom * yRatio) * heightScale + 2.0f;

        float worldMinX = _camera.Position.X - worldHalfW - marginW;
        float worldMaxX = _camera.Position.X + worldHalfW + marginW;
        float worldMinY = _camera.Position.Y - worldHalfH - marginH;
        float worldMaxY = _camera.Position.Y + worldHalfH + marginH;

        int cellMinX = (int)MathF.Floor(worldMinX / RAIN_CELL_SIZE);
        int cellMaxX = (int)MathF.Ceiling(worldMaxX / RAIN_CELL_SIZE);
        int cellMinY = (int)MathF.Floor(worldMinY / RAIN_CELL_SIZE);
        int cellMaxY = (int)MathF.Ceiling(worldMaxY / RAIN_CELL_SIZE);

        float zoomNorm = MathUtil.Clamp((zoom - minZoom) / (RAIN_REF_ZOOM - minZoom), 0f, 1f);
        float priorityThreshold = fx.RainDensity * (0.02f + 0.98f * zoomNorm * zoomNorm);

        int totalCells = (cellMaxX - cellMinX) * (cellMaxY - cellMinY);
        int estimatedDrops = totalCells * RAIN_DROPS_PER_CELL;
        if (estimatedDrops > 0)
            priorityThreshold = MathF.Min(priorityThreshold, (float)MAX_RAIN / estimatedDrops);

        int rainCount = 0, splashCount = 0;

        for (int cy = cellMinY; cy < cellMaxY; cy++)
        for (int cx = cellMinX; cx < cellMaxX; cx++)
        for (int idx = 0; idx < RAIN_DROPS_PER_CELL; idx++)
        {
            uint h0 = RainHash(cx, cy, idx);
            if (HashFloat(h0) >= priorityThreshold) continue;

            float localX = HashFloat(RainHash(cx, cy, idx + 1000));
            float localY = HashFloat(RainHash(cx, cy, idx + 2000));
            float phaseOffset = HashFloat(RainHash(cx, cy, idx + 3000));
            float speedVar = 0.8f + HashFloat(RainHash(cx, cy, idx + 4000)) * 0.4f;

            float wx = (cx + localX) * RAIN_CELL_SIZE;
            float wy = (cy + localY) * RAIN_CELL_SIZE;

            float fallTime = RAIN_FALL_HEIGHT / (baseFallRate * speedVar);
            float cycleDuration = fallTime + RAIN_SPLASH_TIME;
            float cyclePhase = (globalTime / cycleDuration + phaseOffset) % 1.0f;
            float fallFraction = fallTime / cycleDuration;

            var groundScreen = _camera.WorldToScreen(new Vec2(wx, wy), 0f, screenW, screenH);
            float depth = MathUtil.Clamp(groundScreen.Y / screenH, 0f, 1f);

            float distMin = MathF.Min(
                MathF.Min(wx - worldMinX, worldMaxX - wx),
                MathF.Min(wy - worldMinY, worldMaxY - wy));
            float distFade = MathUtil.Clamp(distMin / RAIN_FADE_BAND, 0f, 1f);

            if (cyclePhase < fallFraction)
            {
                if (rainCount >= MAX_RAIN) continue;
                rainCount++;

                float fallProgress = cyclePhase / fallFraction;
                float currentHeight = RAIN_FALL_HEIGHT * (1.0f - fallProgress);

                // Streak length in height units, scaled by zoom (2x at max zoom, 1x at min)
                float baseStreakH = fx.RainLength * zoomDropScale / heightScale;
                float streakH = baseStreakH * (1.0f + (fx.RainNearScale - 1.0f) * depth);
                float topHeight = currentHeight + streakH;
                float botHeight = currentHeight;

                // Wind drift at top and bottom of streak
                float driftTop = windSin * (RAIN_FALL_HEIGHT - topHeight) * RAIN_WIND_DRIFT_SCALE;
                float driftBot = windSin * (RAIN_FALL_HEIGHT - botHeight) * RAIN_WIND_DRIFT_SCALE;

                // Project to screen using direct math (matches C++ projection)
                float halfSW = screenW * 0.5f;
                float halfSH = screenH * 0.5f;
                float projX = zoom;
                float projY = zoom * yRatio;
                float topSX = (wx + driftTop - _camera.Position.X) * projX + halfSW;
                float topSY = (wy - _camera.Position.Y) * projY + halfSH - topHeight * heightScale;
                float botSX = (wx + driftBot - _camera.Position.X) * projX + halfSW;
                float botSY = (wy - _camera.Position.Y) * projY + halfSH - botHeight * heightScale;
                var topSp = new Vector2(topSX, topSY);
                var botSp = new Vector2(botSX, botSY);

                if (topSp.X < -50 || topSp.X > screenW + 50 || botSp.Y < -50 || topSp.Y > screenH + 50)
                    continue;

                byte colR = (byte)(170 + (int)(30 * depth));
                byte colG = (byte)(185 + (int)(25 * depth));
                byte colB = (byte)(215 + (int)(15 * depth));
                float thickness = (1.0f + 0.5f * depth) * zoomDropScale;

                float alphaVal = fx.RainAlpha * (fx.RainFarOpacity + (1f - fx.RainFarOpacity) * depth)
                    * distFade * (0.7f + HashFloat(h0) * 0.3f);
                byte alpha = (byte)(MathUtil.Clamp(alphaVal, 0f, 1f) * 255);
                if (alpha == 0) continue;

                float dx = botSp.X - topSp.X, dy = botSp.Y - topSp.Y;
                float sLen = MathF.Sqrt(dx * dx + dy * dy);
                if (sLen < 0.5f) continue;

                float af = alpha / 255f;
                _spriteBatch.Draw(_pixel, topSp, null,
                    new Color((byte)(colR * af), (byte)(colG * af), (byte)(colB * af), alpha),
                    MathF.Atan2(dy, dx), Vector2.Zero,
                    new Vector2(sLen, thickness), SpriteEffects.None, 0f);
            }
            else
            {
                if (splashCount >= MAX_SPLASHES) continue;
                splashCount++;

                float splashProgress = (cyclePhase - fallFraction) / (1f - fallFraction);
                float radius = (1.5f + 2.5f * depth) * (zoom / 32f) * splashProgress * fx.RainSplashScale;
                float splashAlpha = fx.RainAlpha * (1f - splashProgress) * distFade * 0.6f;
                byte sAlpha = (byte)(MathUtil.Clamp(splashAlpha, 0f, 1f) * 255);
                if (sAlpha == 0) continue;

                float eW = radius * 2f, eH = radius * yRatio * 2f;
                _spriteBatch.Draw(_glowTex, new Rectangle(
                    (int)(groundScreen.X - eW * 0.5f), (int)(groundScreen.Y - eH * 0.5f),
                    (int)MathF.Max(1, eW), (int)MathF.Max(1, eH)),
                    null, Color.FromNonPremultiplied(200, 215, 235, sAlpha),
                    0f, Vector2.Zero, SpriteEffects.None, 0f);
            }
        }
    }

    private static uint RainHash(int cx, int cy, int idx)
    {
        uint h = (uint)cx * 374761393u + (uint)cy * 668265263u + (uint)idx * 2654435769u;
        h = (h ^ (h >> 13)) * 1274126177u;
        h = h ^ (h >> 16);
        return h;
    }

    private static float HashFloat(uint h) => (float)(h & 0x00FFFFFFu) / (float)0x01000000u;
}
