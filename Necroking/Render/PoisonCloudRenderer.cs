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

    /// <summary>
    /// Pre-compute all puff positions and add DepthItems to the sort list.
    /// Call this during DrawUnitsAndObjects before the sort.
    /// DepthItem must have Type=CloudPuff, Index=cloudIndex, SubIndex=puffIndex.
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

            // Generate puffs for all three rings + glow
            GenerateRingPuffs(fb, cloud, screenRadius, intensity, _puffCache[ci],
                count: 8, distFrac: 0.5f, sizeFrac: 0.7f,
                alpha: 0.35f, speed: 0.08f, rotSpeed: 0.12f,
                color: new Color(70, 150, 45), frameOff: 0);

            GenerateRingPuffs(fb, cloud, screenRadius, intensity, _puffCache[ci],
                count: 6, distFrac: 0.25f, sizeFrac: 0.55f,
                alpha: 0.45f, speed: 0.1f, rotSpeed: 0.18f,
                color: new Color(90, 180, 55), frameOff: 17);

            GenerateRingPuffs(fb, cloud, screenRadius, intensity, _puffCache[ci],
                count: 4, distFrac: 0.08f, sizeFrac: 0.45f,
                alpha: 0.55f, speed: 0.1f, rotSpeed: 0.22f,
                color: new Color(110, 210, 65), frameOff: 33);

            // Glow puffs (center)
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

            if (LogNextFrame)
                LogPuffDepths(ci);
        }

        if (LogNextFrame)
            LogNextFrame = false;
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

    /// Set to true to log puff depths on next frame, auto-resets
    public static bool LogNextFrame;

    // Legacy entry points (no longer used when Y-sorted, but kept for flexibility)
    public void DrawAlpha(PoisonCloudSystem cloudSystem) { }
    public void DrawAdditive(PoisonCloudSystem cloudSystem) { }

    /// <summary>
    /// Log puff Y values for debugging depth sort issues.
    /// </summary>
    public void LogPuffDepths(int cloudIndex)
    {
        if (cloudIndex < 0 || cloudIndex >= _puffCache.Count) return;
        var puffs = _puffCache[cloudIndex];
        DebugLog.Log("scenario", $"  Cloud {cloudIndex}: {puffs.Count} puffs");
        float minY = float.MaxValue, maxY = float.MinValue;
        for (int i = 0; i < puffs.Count; i++)
        {
            if (puffs[i].WorldY < minY) minY = puffs[i].WorldY;
            if (puffs[i].WorldY > maxY) maxY = puffs[i].WorldY;
        }
        DebugLog.Log("scenario", $"  Puff Y range: {minY:F2} to {maxY:F2}");
        // Log each puff with its ring and visual world extent
        for (int i = 0; i < puffs.Count; i++)
        {
            string ring = i < 8 ? "outer" : (i < 14 ? "mid" : (i < 18 ? "inner" : "glow"));
            // Visual world-space Y extent of this puff
            float pixelRadius = puffs[i].Scale * puffs[i].SrcRect.Height * 0.5f;
            float worldExtentY = pixelRadius / (_camera.Zoom * _camera.YRatio);
            float visualMinY = puffs[i].WorldY - worldExtentY;
            float visualMaxY = puffs[i].WorldY + worldExtentY;
            DebugLog.Log("scenario", $"    puff[{i}] {ring} sortY={puffs[i].WorldY:F2}  visualY=[{visualMinY:F2}..{visualMaxY:F2}]  worldExtent={worldExtentY:F2}");
        }
    }

    private void GenerateRingPuffs(Flipbook fb, PoisonCloud cloud,
        float screenRadius, float intensity, List<PuffData> output,
        int count, float distFrac, float sizeFrac,
        float alpha, float speed, float rotSpeed,
        Color color, int frameOff)
    {
        float nb = cloud.NoiseOffset;
        var center = _renderer.WorldToScreen(cloud.Position, 0f, _camera);

        for (int i = 0; i < count; i++)
        {
            float baseAngle = i * MathF.PI * 2f / count;
            float np = nb + i * 7.31f + frameOff * 0.13f;

            float nx = SimplexNoise.Noise2D(np + _gameTime * speed, np * 0.7f + _gameTime * speed * 0.7f);
            float ny = SimplexNoise.Noise2D(np * 1.3f + _gameTime * speed * 0.8f, np * 0.5f - _gameTime * speed * 0.5f);

            float dist = screenRadius * distFrac * (0.6f + 0.4f * MathF.Abs(nx));
            float angle = baseAngle + _gameTime * speed * 0.5f + nx * 0.3f;
            float ox = MathF.Cos(angle) * dist + nx * screenRadius * 0.1f;
            float oy = MathF.Sin(angle) * dist * _camera.YRatio + ny * screenRadius * 0.07f;

            float an = SimplexNoise.Noise2D(np * 2.1f + _gameTime * 0.2f, np * 1.7f + _gameTime * 0.15f);
            float a = intensity * (alpha + 0.15f * MathF.Max(0f, an));

            float t = _gameTime * 0.8f + np * 0.5f;
            int frame = fb.GetFrameAtTime(t);
            var src = fb.GetFrameRect(frame);

            float pxSize = screenRadius * sizeFrac * (0.8f + 0.2f * ny);
            float scale = pxSize * 2f / src.Width;
            if (scale < 0.01f) continue;

            float rot = _gameTime * rotSpeed + np;

            int ai = (int)(a * 255f);
            ai = Math.Clamp(ai, 0, 255);

            // Sort by southern visual edge: center Y + half the puff's world-space height
            // This ensures fog covers objects inside it rather than sorting behind them
            float worldOffsetY = oy / (_camera.Zoom * _camera.YRatio);
            float worldCenterY = cloud.Position.Y + worldOffsetY;
            float pixelRadius = scale * src.Height * 0.5f;
            float worldExtentY = pixelRadius / (_camera.Zoom * _camera.YRatio);
            float sortY = worldCenterY + worldExtentY;

            output.Add(new PuffData
            {
                ScreenPos = new Vector2(center.X + ox, center.Y + oy),
                SrcRect = src,
                Scale = scale,
                Rotation = rot,
                Color = new Color(color.R, color.G, color.B, ai),
                WorldY = sortY,
            });
        }
    }

    private void GenerateGlowPuffs(Flipbook fb, PoisonCloud cloud,
        float screenRadius, List<PuffData> output)
    {
        float gi = cloud.Phase switch
        {
            CloudPhase.Eruption => 0.5f + 0.5f * cloud.PhaseProgress,
            CloudPhase.Spread => 0.6f,
            CloudPhase.Decay => MathF.Max(0f, 0.6f - cloud.PhaseProgress * 0.5f),
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

        int ga = (int)(gi * 70f * pulse);
        ga = Math.Clamp(ga, 0, 180);

        // Sort by southern visual edge
        float glowPixelR = scale * src.Height * 0.5f;
        float glowExtentY = glowPixelR / (_camera.Zoom * _camera.YRatio);
        float glowSortY = cloud.Position.Y + glowExtentY;

        // Main glow
        output.Add(new PuffData
        {
            ScreenPos = center,
            SrcRect = src,
            Scale = scale,
            Rotation = rot,
            Color = new Color(80, 255, 40, ga),
            WorldY = glowSortY,
        });

        // Inner core
        float innerScale = scale * 0.4f;
        int ca = (int)(gi * 50f * pulse);
        ca = Math.Clamp(ca, 0, 150);
        float innerPixelR = innerScale * src.Height * 0.5f;
        float innerExtentY = innerPixelR / (_camera.Zoom * _camera.YRatio);
        output.Add(new PuffData
        {
            ScreenPos = center,
            SrcRect = src,
            Scale = innerScale,
            Rotation = -rot * 1.3f,
            Color = new Color(120, 255, 60, ca),
            WorldY = cloud.Position.Y + innerExtentY,
        });
    }
}
