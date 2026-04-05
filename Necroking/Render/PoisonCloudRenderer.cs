using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Core;
using Necroking.GameSystems;

namespace Necroking.Render;

public class PoisonCloudRenderer
{
    private SpriteBatch _spriteBatch = null!;
    private Texture2D _glowTex = null!;
    private Camera25D _camera = null!;
    private Renderer _renderer = null!;
    private Dictionary<string, Flipbook> _flipbooks = null!;
    private float _gameTime;

    public void SetContext(SpriteBatch spriteBatch, Texture2D glowTex,
        Camera25D camera, Renderer renderer, Dictionary<string, Flipbook> flipbooks,
        float gameTime)
    {
        _spriteBatch = spriteBatch;
        _glowTex = glowTex;
        _camera = camera;
        _renderer = renderer;
        _flipbooks = flipbooks;
        _gameTime = gameTime;
    }

    public void DrawAlpha(PoisonCloudSystem cloudSystem)
    {
        foreach (var cloud in cloudSystem.Clouds)
            if (cloud.Alive) DrawCloudFog(cloud);
    }

    public void DrawAdditive(PoisonCloudSystem cloudSystem)
    {
        foreach (var cloud in cloudSystem.Clouds)
            if (cloud.Alive) DrawCloudGlow(cloud);
    }

    private Flipbook? GetCloudFlipbook()
    {
        if (_flipbooks != null && _flipbooks.TryGetValue("cloud03", out var fb) && fb.IsLoaded)
            return fb;
        return null;
    }

    private float GetIntensity(PoisonCloud cloud)
    {
        return cloud.Phase switch
        {
            CloudPhase.Eruption => 0.7f + 0.3f * cloud.PhaseProgress,
            CloudPhase.Spread => 1.0f,
            CloudPhase.Decay => MathF.Max(0.1f, 1f - cloud.PhaseProgress * 0.7f),
            _ => 0f
        };
    }

    private void DrawCloudFog(PoisonCloud cloud)
    {
        var fb = GetCloudFlipbook();
        if (fb == null) return;

        var center = _renderer.WorldToScreen(cloud.Position, 0f, _camera);
        float screenRadius = cloud.CurrentRadius * _camera.Zoom;
        float intensity = GetIntensity(cloud);

        // Outer ring: large puffs, low alpha, slow drift
        DrawPuffRing(fb, cloud, center, screenRadius, intensity,
            count: 8, distFrac: 0.5f, sizeFrac: 0.7f,
            alpha: 0.35f, speed: 0.08f, rotSpeed: 0.12f,
            color: new Color(70, 150, 45), frameOff: 0);

        // Mid ring: medium puffs, moderate alpha
        DrawPuffRing(fb, cloud, center, screenRadius, intensity,
            count: 6, distFrac: 0.25f, sizeFrac: 0.55f,
            alpha: 0.45f, speed: 0.1f, rotSpeed: 0.18f,
            color: new Color(90, 180, 55), frameOff: 17);

        // Inner: dense core puffs
        DrawPuffRing(fb, cloud, center, screenRadius, intensity,
            count: 4, distFrac: 0.08f, sizeFrac: 0.45f,
            alpha: 0.55f, speed: 0.1f, rotSpeed: 0.22f,
            color: new Color(110, 210, 65), frameOff: 33);
    }

    private void DrawPuffRing(Flipbook fb, PoisonCloud cloud, Vector2 center,
        float screenRadius, float intensity,
        int count, float distFrac, float sizeFrac,
        float alpha, float speed, float rotSpeed,
        Color color, int frameOff)
    {
        float nb = cloud.NoiseOffset;

        for (int i = 0; i < count; i++)
        {
            float baseAngle = i * MathF.PI * 2f / count;
            float np = nb + i * 7.31f + frameOff * 0.13f;

            // Noise-driven position
            float nx = SimplexNoise.Noise2D(np + _gameTime * speed, np * 0.7f + _gameTime * speed * 0.7f);
            float ny = SimplexNoise.Noise2D(np * 1.3f + _gameTime * speed * 0.8f, np * 0.5f - _gameTime * speed * 0.5f);

            float dist = screenRadius * distFrac * (0.6f + 0.4f * MathF.Abs(nx));
            float angle = baseAngle + _gameTime * speed * 0.5f + nx * 0.3f;
            float ox = MathF.Cos(angle) * dist + nx * screenRadius * 0.1f;
            float oy = MathF.Sin(angle) * dist * _camera.YRatio + ny * screenRadius * 0.07f;

            // Noise-modulated alpha
            float an = SimplexNoise.Noise2D(np * 2.1f + _gameTime * 0.2f, np * 1.7f + _gameTime * 0.15f);
            float a = intensity * (alpha + 0.15f * MathF.Max(0f, an));

            // Flipbook frame with time offset per puff
            float t = _gameTime * 0.8f + np * 0.5f;
            int frame = fb.GetFrameAtTime(t);
            var src = fb.GetFrameRect(frame);

            float pxSize = screenRadius * sizeFrac * (0.8f + 0.2f * ny);
            float scale = pxSize * 2f / src.Width;
            if (scale < 0.01f) continue;

            float rot = _gameTime * rotSpeed + np;
            var origin = new Vector2(src.Width * 0.5f, src.Height * 0.5f);
            var pos = new Vector2(center.X + ox, center.Y + oy);

            int ai = (int)(a * 255f);
            ai = Math.Clamp(ai, 0, 255);
            var c = new Color(color.R, color.G, color.B, ai);

            _spriteBatch.Draw(fb.Texture, pos, src, c, rot, origin, scale, SpriteEffects.None, 0f);
        }
    }

    private void DrawCloudGlow(PoisonCloud cloud)
    {
        var fb = GetCloudFlipbook();
        if (fb == null) return;

        var center = _renderer.WorldToScreen(cloud.Position, 0f, _camera);
        float screenRadius = cloud.CurrentRadius * _camera.Zoom;

        float gi = cloud.Phase switch
        {
            CloudPhase.Eruption => 0.5f + 0.5f * cloud.PhaseProgress,
            CloudPhase.Spread => 0.6f,
            CloudPhase.Decay => MathF.Max(0f, 0.6f - cloud.PhaseProgress * 0.5f),
            _ => 0f
        };
        if (gi < 0.01f) return;

        float cn = SimplexNoise.Noise2D(cloud.NoiseOffset * 3f + _gameTime * 0.4f, cloud.NoiseOffset * 2f + _gameTime * 0.25f);
        float pulse = 0.7f + 0.3f * cn;

        float t = _gameTime * 0.5f + cloud.NoiseOffset;
        int frame = fb.GetFrameAtTime(t);
        var src = fb.GetFrameRect(frame);
        float coreSize = screenRadius * 0.5f * pulse;
        float scale = coreSize * 2f / src.Width;
        if (scale < 0.01f) return;

        var origin = new Vector2(src.Width * 0.5f, src.Height * 0.5f);
        float rot = _gameTime * 0.05f + cloud.NoiseOffset;

        int ga = (int)(gi * 70f * pulse);
        ga = Math.Clamp(ga, 0, 180);
        _spriteBatch.Draw(fb.Texture, center, src, new Color(80, 255, 40, ga),
            rot, origin, scale, SpriteEffects.None, 0f);

        // Brighter inner core
        float innerScale = scale * 0.4f;
        int ca = (int)(gi * 50f * pulse);
        ca = Math.Clamp(ca, 0, 150);
        _spriteBatch.Draw(fb.Texture, center, src, new Color(120, 255, 60, ca),
            -rot * 1.3f, origin, innerScale, SpriteEffects.None, 0f);
    }
}
