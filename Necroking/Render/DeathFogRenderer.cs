using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Core;
using Necroking.GameSystems;

namespace Necroking.Render;

/// <summary>
/// Visualises <see cref="DeathFogSystem"/>'s scalar density field as a sparse
/// grid of cloud-spritesheet puffs — one per active cell, with per-cell hash
/// jitter so the grid pattern doesn't show. Density modulates alpha and scale,
/// the flipbook is sampled at a per-cell phase so neighbours drift independently
/// ("slow shifting"), and active-set iteration keeps work proportional to fog
/// footprint instead of map size.
///
/// Integrates with the merged Y-sort pass (mirrors <see cref="PoisonCloudRenderer"/>):
/// AddPuffsToDepthList precomputes every visible puff, DrawSinglePuff renders
/// one from the depth-sorted loop. That gives correct interleaving with units
/// and env objects — a unit "south" (higher Y) of a fog puff draws over it,
/// "north" of it gets occluded.
/// </summary>
internal class DeathFogRenderer
{
    public float VisibilityThreshold { get; set; } = 0.02f;
    public float SaturationDensity { get; set; } = 1.0f;
    public float MaxAlpha { get; set; } = 0.20f;
    public float FlipbookCycleSeconds { get; set; } = 3f;
    public float PuffWorldSizeMultiplier { get; set; } = 1.5f;
    public float PositionJitter { get; set; } = 0.4f;
    public Color FogTint { get; set; } = new Color((byte)185, (byte)210, (byte)180, (byte)255);

    private SpriteBatch? _spriteBatch;
    private Camera25D? _camera;
    private Renderer? _renderer;
    private Flipbook? _flipbook;
    private float _gameTime;

    /// <summary>One pre-computed sprite draw — populated by AddPuffsToDepthList,
    /// consumed by DrawSinglePuff. Mirrors PoisonCloudRenderer.PuffData so the
    /// per-item draw call stays minimal.</summary>
    private struct PuffData
    {
        public Vector2 ScreenPos;
        public Rectangle SrcRect;
        public float Scale;
        public float Rotation;
        public Color Color;
    }
    private readonly List<PuffData> _visiblePuffs = new(512);

    public void SetContext(SpriteBatch spriteBatch, Camera25D camera, Renderer renderer,
        Flipbook? flipbook, float gameTime)
    {
        _spriteBatch = spriteBatch;
        _camera = camera;
        _renderer = renderer;
        _flipbook = flipbook;
        _gameTime = gameTime;
    }

    /// <summary>Walk the active fog cells, build a PuffData for each visible
    /// cell with density above the threshold, and append a DepthItem per puff
    /// so it Y-sorts against units/env objects/grass.</summary>
    public void AddPuffsToDepthList(DeathFogSystem fog, int screenW, int screenH,
        List<Game1.DepthItem> depthItems)
    {
        _visiblePuffs.Clear();
        if (_camera == null || _renderer == null) return;
        if (_flipbook == null || !_flipbook.IsLoaded) return;
        if (fog.Density.Length == 0) return;

        var fb = _flipbook;
        var tex = fb.Texture!;
        int totalFrames = fb.TotalFrames;

        int cellSize = fog.CellSize;
        int width = fog.Width;
        var density = fog.Density;

        float puffWorld = cellSize * PuffWorldSizeMultiplier;
        float halfCell = cellSize * 0.5f;
        float jitterRange = halfCell * PositionJitter;

        float zoom = _camera.Zoom;
        float yRatio = _camera.YRatio;
        float pad = puffWorld;
        float viewLeft   = _camera.Position.X - screenW / (2f * zoom) - pad;
        float viewRight  = _camera.Position.X + screenW / (2f * zoom) + pad;
        float viewTop    = _camera.Position.Y - screenH / (2f * zoom * yRatio) - pad;
        float viewBottom = _camera.Position.Y + screenH / (2f * zoom * yRatio) + pad;

        float invSat = 1f / MathF.Max(SaturationDensity, 0.001f);
        float frameW = (float)tex.Width  / Math.Max(fb.Cols, 1);
        float baseScale = (puffWorld * zoom) / frameW;

        foreach (int idx in fog.ActiveCells)
        {
            float d = density[idx];
            if (d < VisibilityThreshold) continue;

            int cx = idx % width;
            int cy = idx / width;

            uint h = CellHash(cx, cy);
            float jx = (HashToFloat(h * 374761393u) * 2f - 1f) * jitterRange;
            float jy = (HashToFloat(h * 668265263u) * 2f - 1f) * jitterRange;
            float wx = cx * cellSize + halfCell + jx;
            float wy = cy * cellSize + halfCell + jy;

            if (wx < viewLeft || wx > viewRight || wy < viewTop || wy > viewBottom) continue;

            float t = MathF.Min(d * invSat, 1f);
            float alpha = MaxAlpha * t;
            float scale = baseScale * (0.85f + 0.3f * t);

            float phaseOffset = HashToFloat(h * 2654435761u) * totalFrames;
            float cyclePos = (_gameTime / MathF.Max(FlipbookCycleSeconds, 0.01f)) * totalFrames + phaseOffset;
            int frame = (int)(cyclePos % totalFrames);
            if (frame < 0) frame += totalFrames;
            var src = fb.GetFrameRect(frame);

            float rotSpeed = (0.12f + 0.25f * HashToFloat(h * 1103515245u)) * ((h & 1u) == 0 ? 1f : -1f);
            float rotation = _gameTime * rotSpeed + HashToFloat(h * 7919u) * MathF.PI * 2f;

            var screenPos = _renderer.WorldToScreen(new Vec2(wx, wy), 0f, _camera);

            var color = ColorUtils.Premultiply(FogTint.R, FogTint.G, FogTint.B, alpha);

            int puffIdx = _visiblePuffs.Count;
            _visiblePuffs.Add(new PuffData
            {
                ScreenPos = screenPos,
                SrcRect = src,
                Scale = scale,
                Rotation = rotation,
                Color = color,
            });

            // Sort by the puff's southern visual extent so a puff with its
            // visible bottom past a unit's feet renders in front. Half-puff
            // world height (≈ cellSize * mult * 0.5) approximates the bottom
            // edge — same logic PoisonCloudRenderer uses.
            float sortY = wy + puffWorld * 0.5f;
            depthItems.Add(new Game1.DepthItem
            {
                Y = sortY,
                Type = Game1.DepthItemType.DeathFogPuff,
                Index = puffIdx,
            });
        }
    }

    /// <summary>Draw a single pre-computed fog puff. Called from the merged
    /// Y-sort loop in whatever depth order it picks.</summary>
    public void DrawSinglePuff(int index)
    {
        if (_spriteBatch == null || _flipbook == null) return;
        if (index < 0 || index >= _visiblePuffs.Count) return;
        var p = _visiblePuffs[index];
        if (p.Scale < 0.01f) return;
        var origin = new Vector2(p.SrcRect.Width * 0.5f, p.SrcRect.Height * 0.5f);
        _spriteBatch.Draw(_flipbook.Texture, p.ScreenPos, p.SrcRect, p.Color,
            p.Rotation, origin, p.Scale, SpriteEffects.None, 0f);
    }

    private static uint CellHash(int cx, int cy)
    {
        uint h = (uint)cx * 374761393u + (uint)cy * 668265263u;
        h = (h ^ (h >> 13)) * 1274126177u;
        h ^= h >> 16;
        return h;
    }

    private static float HashToFloat(uint h) => (h & 0xFFFFFFu) / (float)0xFFFFFFu;
}
