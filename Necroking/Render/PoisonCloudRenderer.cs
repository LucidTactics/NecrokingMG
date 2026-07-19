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
        public Texture2D? Tex; // null = the cloud flipbook sheet (glow puffs use the radial glow tex)
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
        return ColorUtils.Premultiply(r, g, b, alpha);
    }

    /// <summary>
    /// Additive inside the same premultiplied AlphaBlend batch: RGB carries color × strength,
    /// A=0 turns One/InvSrcAlpha into pure dst+src (same trick as BuffVisualSystem.EncodeColor /
    /// ReanimEffectSystem.PremultAdditive). Lets the center glow actually EMIT light while still
    /// Y-sorting per-puff among the dark ring puffs and units. Ceiling: a channel clips at 255
    /// (~1.0 effective intensity) — deliberate here; the poison glow is a faint haze, not a
    /// reanimation ritual.
    /// </summary>
    private static Color PremultAdditive(int r, int g, int b, float strength)
    {
        return new Color(
            (byte)Math.Clamp(r * strength, 0f, 255f),
            (byte)Math.Clamp(g * strength, 0f, 255f),
            (byte)Math.Clamp(b * strength, 0f, 255f),
            (byte)0);
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
                r: outerR, g: outerG, b: outerB, frameOff: 0, isOuter: true);

            GenerateRingPuffs(fb, cloud, screenRadius, intensity, _puffCache[ci],
                count: 6, distFrac: 0.25f, sizeFrac: 0.55f,
                baseAlpha: 0.45f, speed: 0.1f, rotSpeed: 0.18f,
                r: cr, g: cg, b: cb, frameOff: 17);

            // Inner ring is ADDITIVE: the small bright center puffs emit a gentle light that
            // Y-interleaves with the dark outer/middle puffs (which keep providing the
            // occluding body). Lower baseAlpha than the old alpha version — additive reads
            // brighter at the same strength, and this should stay a haze glow, not a flare.
            GenerateRingPuffs(fb, cloud, screenRadius, intensity, _puffCache[ci],
                count: 4, distFrac: 0.08f, sizeFrac: 0.45f,
                baseAlpha: 0.35f, speed: 0.1f, rotSpeed: 0.22f,
                r: innerR, g: innerG, b: innerB, frameOff: 33, additive: true);

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
        _spriteBatch.Draw(p.Tex ?? fb.Texture, p.ScreenPos, p.SrcRect, p.Color,
            p.Rotation, origin, p.Scale, SpriteEffects.None, 0f);
    }

    // Legacy entry points kept for interface compatibility
    public void DrawAlpha(PoisonCloudSystem cloudSystem) { }
    public void DrawAdditive(PoisonCloudSystem cloudSystem) { }

    private void GenerateRingPuffs(Flipbook fb, PoisonCloud cloud,
        float screenRadius, float intensity, List<PuffData> output,
        int count, float distFrac, float sizeFrac,
        float baseAlpha, float speed, float rotSpeed,
        int r, int g, int b, int frameOff, bool additive = false, bool isOuter = false)
    {
        float nb = cloud.NoiseOffset;
        var center = _renderer.WorldToScreen(cloud.Position, 0f, _camera);

        var palette = cloud.Palette;
        bool usePalette = palette != null && palette.Count > 0;
        // Palette mode: fixed per-ring counts by the tier's weight ratios (largest-remainder),
        // seeded-shuffled so which ANGLE gets which family varies per cloud, not the counts.
        if (usePalette)
            BuildRingAssignment(palette!, count,
                isOuter ? PaletteTier.Outer : PaletteTier.Body, nb * 7.7f + frameOff);

        for (int i = 0; i < count; i++)
        {
            float baseAngle = i * MathF.PI * 2f / count;
            float np = nb + i * 7.31f + frameOff * 0.13f;

            bool puffAdditive = additive;
            int pr = r, pg = g, pb = b;
            float pIntensity = 1f;
            if (usePalette)
            {
                var entry = palette![_ringAssign[i]];
                pr = entry.Color.R; pg = entry.Color.G; pb = entry.Color.B;
                pIntensity = entry.Color.Intensity;
                puffAdditive = entry.Additive;
            }

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
            // Palette-mode emissive puffs draw slightly smaller so overlaps don't fuse into
            // one orb — but only slightly: the sort key includes visual extent, so shrinking
            // them too far (0.65 tried) sinks them behind the full-size dark smoke entirely.
            if (usePalette && puffAdditive) pxSize *= 0.85f;
            float scale = pxSize * 2f / src.Width;

            float rot = _gameTime * rotSpeed + np;

            // Sort by southern visual edge for correct depth ordering. Additive puffs get a
            // small camera-ward bias (reanim's FrontSortBias pattern): without it the big dark
            // body puffs — sorted by their larger southern extents — draw over the small glow
            // puffs and multiply the emitted light away. The bias puts the glow in front of
            // most (not all) of the dark mass so it reads while staying part of the cloud.
            float worldOffsetY = oy / (_camera.Zoom * _camera.YRatio);
            float worldCenterY = cloud.Position.Y + worldOffsetY;
            float pixelRadius = scale * src.Height * 0.5f;
            float worldExtentY = pixelRadius / (_camera.Zoom * _camera.YRatio);
            float sortY = worldCenterY + worldExtentY;
            if (puffAdditive) sortY += cloud.CurrentRadius * 0.35f;

            // CRITICAL: Use premultiplied alpha color for correct BlendState.AlphaBlend rendering.
            // Color * alpha correctly scales RGB channels proportional to alpha,
            // so at low alpha the puffs blend smoothly to transparent instead of showing bright fringing.
            output.Add(new PuffData
            {
                ScreenPos = new Vector2(center.X + ox, center.Y + oy),
                SrcRect = src,
                Scale = scale,
                Rotation = rot,
                Color = puffAdditive ? PremultAdditive(pr, pg, pb, alpha * pIntensity)
                                     : PremultipliedColor(pr, pg, pb, alpha),
                WorldY = sortY,
            });
        }
    }

    private enum PaletteTier { Outer, Body, Glow }

    // Reused scratch: palette index per puff for the ring being generated.
    private readonly List<int> _ringAssign = new(16);

    /// <summary>Fill _ringAssign with exactly `count` palette indices in the tier's weight
    /// ratios — FIXED counts via largest-remainder rounding, not per-puff random rolls —
    /// then seeded-Fisher-Yates shuffle so which slot gets which family varies per cloud
    /// (seed is stable per cloud+ring, so the layout doesn't flicker between frames).</summary>
    private void BuildRingAssignment(List<Data.Registries.CloudPaletteEntry> palette,
        int count, PaletteTier tier, float seed)
    {
        _ringAssign.Clear();
        int n = palette.Count;
        Span<float> w = stackalloc float[n];
        Span<int> quota = stackalloc int[n];
        Span<float> rem = stackalloc float[n];

        float total = 0f;
        for (int i = 0; i < n; i++)
        {
            var e = palette[i];
            float wi = tier switch
            {
                PaletteTier.Outer => e.OuterWeight >= 0f ? e.OuterWeight : e.Weight,
                PaletteTier.Glow => e.GlowWeight,
                _ => e.Weight,
            };
            w[i] = MathF.Max(0f, wi);
            total += w[i];
        }
        if (total <= 0f) { for (int i = 0; i < count; i++) _ringAssign.Add(0); return; }

        int assigned = 0;
        for (int i = 0; i < n; i++)
        {
            float exact = count * w[i] / total;
            quota[i] = (int)exact;
            rem[i] = exact - quota[i];
            assigned += quota[i];
        }
        while (assigned < count)
        {
            int best = 0; float bestRem = -1f;
            for (int i = 0; i < n; i++) if (rem[i] > bestRem) { bestRem = rem[i]; best = i; }
            quota[best]++; rem[best] = -2f; assigned++;
        }

        for (int i = 0; i < n; i++)
            for (int k = 0; k < quota[i]; k++) _ringAssign.Add(i);

        uint s = (uint)(seed * 1013904223f) | 1u;   // LCG seeded from the cloud+ring
        for (int i = _ringAssign.Count - 1; i > 0; i--)
        {
            s = s * 1664525u + 1013904223u;
            int j = (int)(s % (uint)(i + 1));
            (_ringAssign[i], _ringAssign[j]) = (_ringAssign[j], _ringAssign[i]);
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

        // The glow puffs are LIGHTS, not vapor: draw them with the shared radial glow
        // texture (hot center -> smooth falloff, same sprite the reanim ritual light
        // uses) instead of a smoke flipbook frame — a wispy smoke shape never reads
        // as emission no matter how bright its tint.
        if (_glowTex == null) return;
        var src = new Rectangle(0, 0, _glowTex.Width, _glowTex.Height);
        float coreSize = screenRadius * 0.5f * pulse;
        float scale = coreSize * 2f / src.Width;
        if (scale < 0.01f) return;

        float rot = 0f;   // radial — rotation is meaningless

        // Sort by southern visual edge + the same camera-ward bias as the additive ring
        // puffs, so the center glow isn't smothered by the dark body drawn over it.
        float glowPixelR = scale * src.Height * 0.5f;
        float glowExtentY = glowPixelR / (_camera.Zoom * _camera.YRatio);
        float glowSortY = cloud.Position.Y + glowExtentY + cloud.CurrentRadius * 0.35f;

        int gr = cloud.GlowR, gg = cloud.GlowG, gb = cloud.GlowB;
        int coreR = Math.Min(gr + 40, 255), coreG = gg, coreB = Math.Min(gb + 20, 255);
        float mainIntensity = 1f, coreIntensity = 1f;

        // Palette glow: when any entry carries a GlowWeight, the two glow puffs split by
        // those shares (fixed counts, seeded placement) instead of using CloudGlowColor.
        var glowPal = cloud.Palette;
        if (glowPal != null && glowPal.Count > 0)
        {
            bool hasGlowEntries = false;
            for (int i = 0; i < glowPal.Count; i++) if (glowPal[i].GlowWeight > 0f) { hasGlowEntries = true; break; }
            if (hasGlowEntries)
            {
                BuildRingAssignment(glowPal, 2, PaletteTier.Glow, cloud.NoiseOffset * 3.3f);
                var eMain = glowPal[_ringAssign[0]];
                var eCore = glowPal[_ringAssign[1]];
                gr = eMain.Color.R; gg = eMain.Color.G; gb = eMain.Color.B; mainIntensity = eMain.Color.Intensity;
                coreR = eCore.Color.R; coreG = eCore.Color.G; coreB = eCore.Color.B; coreIntensity = eCore.Color.Intensity;
            }
        }

        // Hot core: a light saturates toward white at its center; the saturated hue
        // lives in the falloff (the main puff). Applies to palette and legacy alike.
        coreR += (255 - coreR) * 2 / 5;
        coreG += (255 - coreG) * 2 / 5;
        coreB += (255 - coreB) * 2 / 5;

        // Light the surrounding AIR, like reanim's ritual light (RegisterScatter):
        // the pre-bloom scatter halo on the air around the cloud is most of what
        // makes the glow read as an actual light source. Once per cloud per frame
        // (this runs from AddPuffsToDepthList).
        Game1.Instance?._scatterGlow.AddPoint(cloud.Position, cloud.CurrentRadius * 0.9f,
            new Color(gr, gg, gb), 0.5f * gi * pulse, height: 0.5f);

        // Main glow — A=0 additive so it emits instead of just tinting. Strength kept low
        // (a bit above the old alpha values) so the cloud reads as faintly luminous haze.
        float mainStrength = gi * gi * pulse * 0.40f * mainIntensity;
        output.Add(new PuffData
        {
            ScreenPos = center,
            SrcRect = src,
            Scale = scale,
            Rotation = rot,
            Color = PremultAdditive(gr, gg, gb, mainStrength),
            WorldY = glowSortY,
            Tex = _glowTex,
        });

        // Inner bright core — A=0 additive, white-shifted, still restrained.
        float innerScale = scale * 0.4f;
        float innerStrength = gi * pulse * 0.30f * coreIntensity;
        float innerPixelR = innerScale * src.Height * 0.5f;
        float innerExtentY = innerPixelR / (_camera.Zoom * _camera.YRatio);
        output.Add(new PuffData
        {
            ScreenPos = center,
            SrcRect = src,
            Scale = innerScale,
            Rotation = 0f,
            Color = PremultAdditive(coreR, coreG, coreB, innerStrength),
            WorldY = cloud.Position.Y + innerExtentY + cloud.CurrentRadius * 0.35f,
            Tex = _glowTex,
        });
    }
}
