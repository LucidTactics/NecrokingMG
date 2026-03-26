using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Data.Registries;

namespace Necroking.Render;

public class WeatherRenderer
{
    private int _screenW, _screenH;
    private float _elapsedTime;
    private float _flashIntensity;

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
    /// Apply weather post-processing. Requires HLSL shader — stub for now.
    /// When shader is ported, this will:
    /// - Apply brightness/contrast/saturation
    /// - Apply tint and ambient color
    /// - Render vignette
    /// - Render fog overlay
    /// - Apply haze
    /// </summary>
    public void Apply(SpriteBatch batch, Texture2D sceneTexture, WeatherEffects effects, float dt, Camera25D cam)
    {
        _elapsedTime += dt;
        // Without shader, just draw the scene as-is
        // Shader port will handle the full post-processing chain
    }

    /// <summary>
    /// Draw rain particles. CPU-driven particle system.
    /// </summary>
    public void DrawRain(SpriteBatch batch, Texture2D pixel, WeatherEffects effects, Camera25D cam)
    {
        if (effects.RainDensity <= 0f) return;

        // Simple rain particle rendering
        int particleCount = (int)(effects.RainDensity * 200);
        var rng = new Random((int)(_elapsedTime * 100));

        for (int i = 0; i < particleCount; i++)
        {
            float x = rng.NextSingle() * _screenW;
            float baseY = rng.NextSingle() * _screenH;
            float y = (baseY + _elapsedTime * effects.RainSpeed) % _screenH;

            float windOffset = MathF.Sin(effects.RainWindAngle * MathF.PI / 180f) * effects.RainLength;

            var color = new Color(200, 200, 220, (int)(effects.RainAlpha * 255));
            batch.Draw(pixel, new Vector2(x, y), null, color,
                effects.RainWindAngle * MathF.PI / 180f,
                Vector2.Zero, new Vector2(1f, effects.RainLength),
                SpriteEffects.None, 0f);
        }
    }

    public void TriggerFlash() => _flashIntensity = 1f;
}
