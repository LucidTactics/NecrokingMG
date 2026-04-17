using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Core;
using Necroking.Data.Registries;

namespace Necroking.Render;

/// <summary>
/// Renders sparse painterly grass tufts as textured billboards scattered across
/// the grass map. Each visible grass cell rolls against a density threshold;
/// cells that pass draw one tuft sprite — picked deterministically from the
/// cell's grass type sprite list via a position hash — with jittered scale,
/// horizontal flip, and subtle per-tuft brightness variation.
///
/// Replaces the per-blade triangle system (old GrassRenderer). The 2.5D
/// prerendered aesthetic this game targets is better served by fewer, larger
/// painterly tuft sprites than by simulating thousands of individual blades.
/// </summary>
/// <summary>
/// Per-type data the renderer needs: sprite paths, rendered-size multiplier, and
/// density (fraction of painted cells that render a tuft, 0..1).
/// </summary>
public readonly struct GrassTypeRender
{
    public readonly IReadOnlyList<string> SpritePaths;
    public readonly float Scale;
    public readonly float Density;
    public GrassTypeRender(IReadOnlyList<string> paths, float scale, float density)
    {
        SpritePaths = paths;
        Scale = scale;
        Density = density;
    }
}

public class GrassTuftRenderer
{
    // Don't bother drawing when zoomed out so far that tufts would be sub-pixel.
    private const float MinZoomForGrass = 5f;

    // Base rendered tuft size, as a multiple of the grass cell size. 1.0 means a
    // tuft's footprint exactly matches one cell — grass cells align cleanly with
    // the pathability tile grid. Per-type scales (GrassTypeDef.Scale) multiply on
    // top of this for types that should render bigger/smaller.
    private const float TuftSizeCellMultiplier = 1.0f;

    // Safety cap on tufts per cell. Prevents runaway configs (a user typing 100 in
    // the density field would otherwise spawn 100 sprites per cell per frame).
    private const int MaxTuftsPerCell = 20;

    // Per-type sprite texture lists. [typeIndex] -> list of Texture2D (1-5 entries).
    // Parallel to _typeScales / _typeDensities. Rebuilt by SetGrassTypes when the
    // editor changes any of the type fields.
    private readonly List<Texture2D[]> _typeTextures = new();
    private readonly List<float> _typeScales = new();
    private readonly List<float> _typeDensities = new();

    // Shared texture cache keyed by project-relative path. Multiple types can
    // reference the same sprite; each texture is only loaded once.
    private readonly Dictionary<string, Texture2D> _texCache = new();

    private GraphicsDevice? _device;

    public void Init(GraphicsDevice device)
    {
        _device = device;
    }

    /// <summary>
    /// Update the per-type sprite lists and scale factors. Called by Game1 whenever
    /// the editor changes grass types (add/remove type, pick new sprite, adjust
    /// scale). Loads any paths not already in the cache.
    /// </summary>
    public void SetGrassTypes(IReadOnlyList<GrassTypeRender> types)
    {
        if (_device == null) return;

        _typeTextures.Clear();
        _typeScales.Clear();
        _typeDensities.Clear();
        foreach (var t in types)
        {
            var list = new List<Texture2D>();
            foreach (var rawPath in t.SpritePaths)
            {
                if (string.IsNullOrEmpty(rawPath)) continue;
                if (!_texCache.TryGetValue(rawPath, out var tex))
                {
                    try
                    {
                        tex = TextureUtil.LoadPremultiplied(_device, rawPath);
                        _texCache[rawPath] = tex;
                    }
                    catch (Exception ex)
                    {
                        DebugLog.Log("error", $"GrassTuftRenderer: failed to load {rawPath}: {ex.Message}");
                        continue;
                    }
                }
                list.Add(tex);
            }
            _typeTextures.Add(list.ToArray());
            _typeScales.Add(t.Scale > 0f ? t.Scale : 1f);
            _typeDensities.Add(MathF.Max(0f, t.Density));
        }
    }

    /// <summary>
    /// Per-tuft instance data cached by AddTuftsToDepthList and read by
    /// DrawSingleTuft. Everything a sprite draw needs, already flattened so the
    /// per-item draw call does minimal work.
    /// </summary>
    private struct TuftInstance
    {
        public Texture2D Tex;
        public float Wx, Wy;     // world-space anchor (bottom centre, for Y-sort)
        public float WorldSize;
        public Color Tint;
        public bool Flip;
    }

    private readonly List<TuftInstance> _visibleTufts = new(1024);
    private Camera25D? _drawCamera;
    private int _drawScreenW, _drawScreenH;

    /// <summary>
    /// Pre-compute every visible tuft and push a DepthItem for each into the
    /// shared sort list. Mirrors PoisonCloudRenderer.AddPuffsToDepthList — it's
    /// what lets grass Y-sort against units so a tuft in front of a unit
    /// correctly occludes its feet.
    /// </summary>
    internal void AddTuftsToDepthList(
        Camera25D camera,
        int screenW, int screenH,
        byte[] grassMap, int grassW, int grassH,
        GrassSettings settings,
        Color? ambientColor,
        List<Game1.DepthItem> depthItems)
    {
        _visibleTufts.Clear();
        _drawCamera = camera;
        _drawScreenW = screenW;
        _drawScreenH = screenH;

        if (_typeTextures.Count == 0) return;
        if (grassMap.Length == 0 || grassW == 0) return;
        if (camera.Zoom < MinZoomForGrass) return;

        float cellSize = settings.CellSize > 0 ? settings.CellSize : 1.0f;
        float zoom = camera.Zoom;
        float yRatio = camera.YRatio;

        float tuftWorldSize = cellSize * TuftSizeCellMultiplier;

        float pad = tuftWorldSize;
        float viewLeft = camera.Position.X - screenW / (2f * zoom) - pad;
        float viewRight = camera.Position.X + screenW / (2f * zoom) + pad;
        float viewTop = camera.Position.Y - screenH / (2f * zoom * yRatio) - pad;
        float viewBottom = camera.Position.Y + screenH / (2f * zoom * yRatio) + pad;

        int cx0 = Math.Max(0, (int)MathF.Floor(viewLeft / cellSize));
        int cy0 = Math.Max(0, (int)MathF.Floor(viewTop / cellSize));
        int cx1 = Math.Min(grassW - 1, (int)MathF.Ceiling(viewRight / cellSize));
        int cy1 = Math.Min(grassH - 1, (int)MathF.Ceiling(viewBottom / cellSize));

        Color ambient = ambientColor ?? Color.White;

        for (int cy = cy0; cy <= cy1; cy++)
        {
            for (int cx = cx0; cx <= cx1; cx++)
            {
                int idx = cy * grassW + cx;
                if (idx < 0 || idx >= grassMap.Length) continue;
                byte cellVal = grassMap[idx];
                if (cellVal == 0) continue;

                int typeIdx = cellVal - 1;
                if (typeIdx < 0 || typeIdx >= _typeTextures.Count) continue;

                var typeTextures = _typeTextures[typeIdx];
                if (typeTextures.Length == 0) continue;
                float typeScale = typeIdx < _typeScales.Count ? _typeScales[typeIdx] : 1f;
                float typeDensity = typeIdx < _typeDensities.Count ? _typeDensities[typeIdx] : 1f;

                int tuftCount = (int)typeDensity;
                float frac = typeDensity - tuftCount;
                if (frac > 0f)
                {
                    uint hFrac = TileHash(cx, cy, -1);
                    if (HashToFloat(hFrac) < frac) tuftCount++;
                }
                if (tuftCount > MaxTuftsPerCell) tuftCount = MaxTuftsPerCell;
                if (tuftCount <= 0) continue;

                for (int ti = 0; ti < tuftCount; ti++)
                {
                    uint h = TileHash(cx, cy, ti);
                    var tex = typeTextures[(int)((h * 2654435761u) % (uint)typeTextures.Length)];

                    float fx = HashToFloat(h * 7919u);
                    float fy = HashToFloat(h * 1103515245u);
                    float fs = HashToFloat(h * 340573321u);
                    bool flip = (h & 1u) == 0u;

                    float worldSize = tuftWorldSize * typeScale * (0.8f + fs * 0.4f);
                    float wx = (cx + fx) * cellSize;
                    float wy = (cy + fy) * cellSize;
                    Color tint = TintWithAmbient(ambient, 0.9f + fs * 0.2f);

                    _visibleTufts.Add(new TuftInstance
                    {
                        Tex = tex,
                        Wx = wx,
                        Wy = wy,
                        WorldSize = worldSize,
                        Tint = tint,
                        Flip = flip,
                    });

                    // Sort by the tuft's visual bottom, not its anchor. The sprite
                    // is drawn with origin (W/2, H * 0.75) — i.e. 25% of the sprite
                    // extends below the anchor at wy — so the true feet are lower
                    // than wy. Bushes / env objects sort by their own feet (obj.Y),
                    // so aligning here avoids tufts sorting as "equal" with a bush
                    // whose feet are actually below them.
                    float sortY = wy + 0.25f * worldSize;
                    depthItems.Add(new Game1.DepthItem
                    {
                        Y = sortY,
                        Type = Game1.DepthItemType.GrassTuft,
                        Index = _visibleTufts.Count - 1,
                    });
                }
            }
        }
    }

    /// <summary>
    /// Draw a single tuft previously collected by AddTuftsToDepthList. Called
    /// from Game1's merged Y-sort loop in whatever depth order it picks.
    /// </summary>
    public void DrawSingleTuft(SpriteBatch spriteBatch, int index)
    {
        if (_drawCamera == null) return;
        if (index < 0 || index >= _visibleTufts.Count) return;

        var t = _visibleTufts[index];
        float zoom = _drawCamera.Zoom;
        float yRatio = _drawCamera.YRatio;

        float sx = (t.Wx - _drawCamera.Position.X) * zoom + _drawScreenW * 0.5f;
        float sy = (t.Wy - _drawCamera.Position.Y) * zoom * yRatio + _drawScreenH * 0.5f;

        float spriteSizePx = t.WorldSize * zoom;
        var origin = new Vector2(t.Tex.Width * 0.5f, t.Tex.Height * 0.75f);
        float drawScale = spriteSizePx / t.Tex.Width;

        var effects = t.Flip ? SpriteEffects.FlipHorizontally : SpriteEffects.None;
        spriteBatch.Draw(t.Tex, new Vector2(sx, sy), null, t.Tint,
            0f, origin, drawScale, effects, 0f);
    }

    private static Color TintWithAmbient(Color amb, float brightness)
    {
        return new Color(
            (byte)Math.Clamp((int)(amb.R * brightness), 0, 255),
            (byte)Math.Clamp((int)(amb.G * brightness), 0, 255),
            (byte)Math.Clamp((int)(amb.B * brightness), 0, 255),
            (byte)255);
    }

    // Same hash as the old GrassRenderer — keeps tuft placement stable if we
    // ever re-enable the old system alongside for comparison.
    private static uint TileHash(int tx, int ty, int idx)
    {
        uint h = (uint)tx * 374761393u + (uint)ty * 668265263u + (uint)idx * 2654435769u;
        h = (h ^ (h >> 13)) * 1274126177u;
        h ^= h >> 16;
        return h;
    }

    private static float HashToFloat(uint h) => (h & 0xFFFFFFu) / (float)0xFFFFFFu;

    public void Dispose()
    {
        foreach (var kv in _texCache) kv.Value?.Dispose();
        _texCache.Clear();
        _typeTextures.Clear();
    }
}
