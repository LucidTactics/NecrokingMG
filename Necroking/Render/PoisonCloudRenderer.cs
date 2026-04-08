using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Core;
using Necroking.GameSystems;

namespace Necroking.Render;

internal class PoisonCloudRenderer
{
    private SpriteBatch _spriteBatch = null!;
    private Texture2D _glowTex = null!;
    private Camera25D _camera = null!;
    private Renderer _renderer = null!;
    private Dictionary<string, Flipbook> _flipbooks = null!;
    private float _gameTime;

    // Pre-computed puff data for Y-sorted rendering
    private struct PuffData
    {
        public Vector2 ScreenPos;
        public Rectangle SrcRect;
        public float Scale;
        public float Rotation;
        public Color Color;
        public float WorldY;  // For depth sorting
    }

    // Indexed by [cloudIndex][puffIndex]
    private readonly List<List<PuffData>> _puffCache = new();

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

    private Flipbook? GetCloudFlipbook()
    {
        if (_flipbooks != null && _flipbooks.TryGetValue("cloud03", out var fb) && fb.IsLoaded)
            return fb;
        return null;
    }

    /// <summary>
    /// Cloud intensity: 0-1 value controlling overall visibility.
    /// Eruption ramps up, Spread holds at full, Decay fades out with quadratic curve.
    /// </summary>
    private float GetIntensity(PoisonCloud cloud)
    {
        return cloud.Phase switch
        {
            CloudPhase.Eruption => 0.7f + 0.3f * cloud.PhaseProgress,
            CloudPhase.Spread => 1.0f,
            CloudPhase.Decay => MathF.Max(0f, (1f - cloud.PhaseProgress) * (1f - cloud.PhaseProgress)),
            _ => 0f
        };
    }

    /// <summary>
    /// Create a properly premultiplied color for use with BlendState.AlphaBlend.
    /// This is the key to correct fade-out: Color * alpha premultiplies RGB by alpha.
    /// </summary>
    private static Color PremultipliedColor(int r, int g, int b, float alpha)
    {
        alpha = MathHelper.Clamp(alpha, 0f, 1f);
        return new Color(r, g, b) * alpha;
    }

    /// <summary>
    /// Pre-compute all puff positions and add DepthItems to the sort list.
    /// Call this during DrawUnitsAndObjects before the sort.
    /// </summary>
    public void AddPuffsToDepthList(PoisonCloudSystem cloudSystem, List<Game1.DepthItem> items)
    {
        var fb = GetCloudFlipbook();
        if (fb == null) return;

        // Ensure cache has enough entries
        while (_puffCache.Count < cloudSystem.Clouds.Count)
            _puffCache.Add(new List<PuffData>());

        for (int ci = 0; ci < cloudSystem.Clouds.Count; ci++)
        {
            var cloud = cloudSystem.Clouds[ci];
            if (!cloud.Alive) continue;

            if (ci >= _puffCache.Count) _puffCache.Add(new List<PuffData>());
            _puffCache[ci].Clear();

            float screenRadius = cloud.CurrentRadius * _camera.Zoom;
            float intensity = GetIntensity(cloud);

            // Skip rendering when nearly invisible
            if (intensity < 0.005f) continue;

            // Derive ring shades from base color: outer=darker, middle=base, inner=brighter
            int cr = cloud.ColorR, cg = cloud.ColorG, cb = cloud.ColorB;
            int outerR = cr * 3 / 4, outerG = cg * 3 / 4, outerB = cb * 3 / 4;
            int innerR = Math.Min(cr + 20, 255), innerG = Math.Min(cg + 30, 255), innerB = Math.Min(cb + 10, 255);

            // Three rings of puffs: outer (large, dim), middle, inner (small, bright)
            GenerateRingPuffs(fb, cloud, screenRadius, intensity, _puffCache[ci],
                count: 8, distFrac: 0.5f, sizeFrac: 0.7f,
                baseAlpha: 0.35f, speed: 0.08f, rotSpeed: 0.12f,
                r: outerR, g: outerG, b: outerB, frameOff: 0);

            GenerateRingPuffs(fb, cloud, screenRadius, intensity, _puffCache[ci],
                count: 6, distFrac: 0.25f, sizeFrac: 0.55f,
                baseAlpha: 0.45f, speed: 0.1f, rotSpeed: 0.18f,
                r: cr, g: cg, b: cb, frameOff: 17);

            GenerateRingPuffs(fb, cloud, screenRadius, intensity, _puffCache[ci],
                count: 4, distFrac: 0.08f, sizeFrac: 0.45f,
                baseAlpha: 0.55f, speed: 0.1f, rotSpeed: 0.22f,
                r: innerR, g: innerG, b: innerB, frameOff: 33);

            // Glow puffs at center
            GenerateGlowPuffs(fb, cloud, screenRadius, _puffCache[ci]);

            // Add each puff as a depth item
            for (int pi = 0; pi < _puffCache[ci].Count; pi++)
            {
                items.Add(new Game1.DepthItem
                {
                    Y = _puffCache[ci][pi].WorldY,
                    Type = Game1.DepthItemType.CloudPuff,
                    Index = ci,
                    SubIndex = pi
                });
            }
        }
    }

    /// <summary>
    /// Draw a single pre-computed puff by cloud and puff index.
    /// Called from the Y-sorted draw loop.
    /// </summary>
    public void DrawSinglePuff(int cloudIndex, int puffIndex)
    {
        if (cloudIndex < 0 || cloudIndex >= _puffCache.Count) return;
        var puffs = _puffCache[cloudIndex];
        if (puffIndex < 0 || puffIndex >= puffs.Count) return;

        var fb = GetCloudFlipbook();
        if (fb == null) return;

        var p = puffs[puffIndex];
        if (p.Scale < 0.01f) return;

        var origin = new Vector2(p.SrcRect.Width * 0.5f, p.SrcRect.Height * 0.5f);
        _spriteBatch.Draw(fb.Texture, p.ScreenPos, p.SrcRect, p.Color,
            p.Rotation, origin, p.Scale, SpriteEffects.None, 0f);
    }

    // Legacy entry points kept for interface compatibility
    public void DrawAlpha(PoisonCloudSystem cloudSystem) { }
    public void DrawAdditive(PoisonCloudSystem cloudSystem) { }

    private void GenerateRingPuffs(Flipbook fb, PoisonCloud cloud,
        float screenRadius, float intensity, List<PuffData> output,
        int count, float distFrac, float sizeFrac,
        float baseAlpha, float speed, float rotSpeed,
        int r, int g, int b, int frameOff)
    {
        float nb = cloud.NoiseOffset;
        var center = _renderer.WorldToScreen(cloud.Position, 0f, _camera);

        for (int i = 0; i < count; i++)
        {
            float baseAngle = i * MathF.PI * 2f / count;
            float np = nb + i * 7.31f + frameOff * 0.13f;

            // Simplex noise for organic movement
            float nx = SimplexNoise.Noise2D(np + _gameTime * speed, np * 0.7f + _gameTime * speed * 0.7f);
            float ny = SimplexNoise.Noise2D(np * 1.3f + _gameTime * speed * 0.8f, np * 0.5f - _gameTime * speed * 0.5f);

            float dist = screenRadius * distFrac * (0.6f + 0.4f * MathF.Abs(nx));
            float angle = baseAngle + _gameTime * speed * 0.5f + nx * 0.3f;
            float ox = MathF.Cos(angle) * dist + nx * screenRadius * 0.1f;
            float oy = MathF.Sin(angle) * dist * _camera.YRatio + ny * screenRadius * 0.07f;

            // Alpha noise for per-puff variation
            float an = SimplexNoise.Noise2D(np * 2.1f + _gameTime * 0.2f, np * 1.7f + _gameTime * 0.15f);

            // Final alpha: intensity controls the fade, baseAlpha is per-ring opacity
            // intensity² gives accelerating fade-out during decay
            float alpha = intensity * intensity * (baseAlpha + intensity * 0.15f * MathF.Max(0f, an));

            // Animated flipbook frame
            float t = _gameTime * 0.8f + np * 0.5f;
            int frame = fb.GetFrameAtTime(t);
            var src = fb.GetFrameRect(frame);

            // Size stays constant — only alpha controls fade (no shrink = no bright dot convergence)
            float pxSize = screenRadius * sizeFrac * (0.8f + 0.2f * ny);
            float scale = pxSize * 2f / src.Width;

            float rot = _gameTime * rotSpeed + np;

            // Sort by southern visual edge for correct depth ordering
            float worldOffsetY = oy / (_camera.Zoom * _camera.YRatio);
            float worldCenterY = cloud.Position.Y + worldOffsetY;
            float pixelRadius = scale * src.Height * 0.5f;
            float worldExtentY = pixelRadius / (_camera.Zoom * _camera.YRatio);
            float sortY = worldCenterY + worldExtentY;

            // CRITICAL: Use premultiplied alpha color for correct BlendState.AlphaBlend rendering.
            // Color * alpha correctly scales RGB channels proportional to alpha,
            // so at low alpha the puffs blend smoothly to transparent instead of showing bright fringing.
            output.Add(new PuffData
            {
                ScreenPos = new Vector2(center.X + ox, center.Y + oy),
                SrcRect = src,
                Scale = scale,
                Rotation = rot,
                Color = PremultipliedColor(r, g, b, alpha),
                WorldY = sortY,
            });
        }
    }

    private void GenerateGlowPuffs(Flipbook fb, PoisonCloud cloud,
        float screenRadius, List<PuffData> output)
    {
        // Glow intensity: builds during eruption, steady in spread, fades quickly in decay
        float gi = cloud.Phase switch
        {
            CloudPhase.Eruption => 0.5f + 0.5f * cloud.PhaseProgress,
            CloudPhase.Spread => 0.6f,
            CloudPhase.Decay => MathF.Max(0f, 0.6f * (1f - cloud.PhaseProgress * cloud.PhaseProgress)),
            _ => 0f
        };
        if (gi < 0.01f) return;

        var center = _renderer.WorldToScreen(cloud.Position, 0f, _camera);

        float cn = SimplexNoise.Noise2D(cloud.NoiseOffset * 3f + _gameTime * 0.4f,
            cloud.NoiseOffset * 2f + _gameTime * 0.25f);
        float pulse = 0.7f + 0.3f * cn;

        float t = _gameTime * 0.5f + cloud.NoiseOffset;
        int frame = fb.GetFrameAtTime(t);
        var src = fb.GetFrameRect(frame);
        float coreSize = screenRadius * 0.5f * pulse;
        float scale = coreSize * 2f / src.Width;
        if (scale < 0.01f) return;

        float rot = _gameTime * 0.05f + cloud.NoiseOffset;

        // Sort by southern visual edge
        float glowPixelR = scale * src.Height * 0.5f;
        float glowExtentY = glowPixelR / (_camera.Zoom * _camera.YRatio);
        float glowSortY = cloud.Position.Y + glowExtentY;

        int gr = cloud.GlowR, gg = cloud.GlowG, gb = cloud.GlowB;
        int coreR = Math.Min(gr + 40, 255), coreG = gg, coreB = Math.Min(gb + 20, 255);

        // Main glow — premultiplied
        float mainAlpha = gi * gi * pulse * 0.27f; // ~70/255 at full
        output.Add(new PuffData
        {
            ScreenPos = center,
            SrcRect = src,
            Scale = scale,
            Rotation = rot,
            Color = PremultipliedColor(gr, gg, gb, mainAlpha),
            WorldY = glowSortY,
        });

        // Inner bright core — premultiplied
        float innerScale = scale * 0.4f;
        float innerAlpha = gi * pulse * 0.2f; // ~50/255 at full
        float innerPixelR = innerScale * src.Height * 0.5f;
        float innerExtentY = innerPixelR / (_camera.Zoom * _camera.YRatio);
        output.Add(new PuffData
        {
            ScreenPos = center,
            SrcRect = src,
            Scale = innerScale,
            Rotation = -rot * 1.3f,
            Color = PremultipliedColor(coreR, coreG, coreB, innerAlpha),
            WorldY = cloud.Position.Y + innerExtentY,
        });
    }
}
