using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Necroking.Render;

/// <summary>
/// Collects soft-edged polyline ribbons (lightning bolts, drain tendrils) as HDR
/// triangle lists and draws them in one pass per intensity value.
///
/// Why this exists: the old path drew each polyline segment as an independent
/// rotated 1x1-pixel rectangle, which leaves wedge-shaped gaps on the outside of
/// every bend and double-bright additive overlaps on the inside. Ribbons share
/// miter-joined vertices at every point, so bends are seamless, and the cross
/// section gets a real alpha falloff instead of a hard box edge.
///
/// Pattern mirrors GodRayRenderer: collect during the additive fx callback, then
/// DrawAll() AFTER the sprite batch ends. Additive blending is order-independent,
/// so composition is identical as long as this lands on the scene RT before bloom
/// extraction. Vertices are bucketed per HDR intensity because VertexPositionColor
/// is a byte4 (can't carry >1.0 color) — each bucket becomes one DrawUserPrimitives
/// with HdrIntensity.fx's Intensity uniform set to the bucket key.
/// </summary>
public class HdrStripBatch
{
    private GraphicsDevice _device = null!;
    private Microsoft.Xna.Framework.Graphics.Effect? _hdrEffect;
    private BasicEffect? _basicEffect;
    private BasicEffect? _basicEffectTex;

    // Parallel bucket lists keyed by HDR intensity. Bucket count stays tiny (one
    // per distinct style intensity on screen), so a linear scan beats a dictionary.
    private readonly List<float> _intensities = new();
    private readonly List<List<VertexPositionColor>> _buckets = new();

    // Textured buckets (drain-beam scroll layers): same intensity keying, one
    // shared wrap-sampled texture (the streak noise) for the whole batch.
    private readonly List<float> _texIntensities = new();
    private readonly List<List<VertexPositionColorTexture>> _texBuckets = new();
    private Texture2D? _scrollTexture;

    // Reused flush buffers — same rationale as GodRayRenderer._flushScratch.
    private VertexPositionColor[] _flushScratch = new VertexPositionColor[512];
    private VertexPositionColorTexture[] _flushScratchTex = new VertexPositionColorTexture[512];

    public void Init(GraphicsDevice device, Microsoft.Xna.Framework.Graphics.Effect? hdrIntensityEffect)
    {
        _device = device;
        _hdrEffect = hdrIntensityEffect;
        _scrollTexture = TextureUtil.GetStreakNoise(device);
        // Fallback when HdrIntensity.fx failed to load: colors clamp at 1.0 (no HDR,
        // bloom stops firing) but the bolts stay visible — GodRayRenderer precedent.
        _basicEffect = new BasicEffect(device)
        {
            VertexColorEnabled = true,
            TextureEnabled = false,
            LightingEnabled = false,
        };
        _basicEffectTex = new BasicEffect(device)
        {
            VertexColorEnabled = true,
            TextureEnabled = true,
            LightingEnabled = false,
        };
    }

    /// <summary>Vertex list drawn with the given HDR intensity. Append triangle-list
    /// vertices (multiples of 3) — PolylineStrip.Build does this for ribbons.</summary>
    public List<VertexPositionColor> GetBucket(float intensity)
    {
        for (int i = 0; i < _intensities.Count; i++)
            if (_intensities[i] == intensity) return _buckets[i];
        _intensities.Add(intensity);
        var list = new List<VertexPositionColor>();
        _buckets.Add(list);
        return list;
    }

    /// <summary>Textured vertex list drawn with the given HDR intensity and the
    /// shared streak-noise scroll texture (wrap-sampled, so arc-length U can
    /// scroll unbounded). PolylineStrip.BuildTextured appends here.</summary>
    public List<VertexPositionColorTexture> GetTexturedBucket(float intensity)
    {
        for (int i = 0; i < _texIntensities.Count; i++)
            if (_texIntensities[i] == intensity) return _texBuckets[i];
        _texIntensities.Add(intensity);
        var list = new List<VertexPositionColorTexture>();
        _texBuckets.Add(list);
        return list;
    }

    /// <summary>Drop collected vertices without drawing (start-of-frame safety so a
    /// disabled draw pass can't accumulate vertices forever).</summary>
    public void Clear()
    {
        foreach (var b in _buckets) b.Clear();
        foreach (var b in _texBuckets) b.Clear();
    }

    /// <summary>Draw and clear all buckets. Must run AFTER the additive
    /// SpriteBatch.End(), while the scene RT is still bound (pre-bloom).</summary>
    public void DrawAll()
    {
        bool any = false;
        foreach (var b in _buckets) any |= b.Count >= 3;
        foreach (var b in _texBuckets) any |= b.Count >= 3;
        if (!any) return;

        var vp = _device.Viewport;
        var wvp = Matrix.CreateOrthographicOffCenter(0, vp.Width, vp.Height, 0, 0, 1);

        _device.BlendState = BlendState.Additive;
        _device.DepthStencilState = DepthStencilState.None;
        _device.RasterizerState = RasterizerState.CullNone;

        if (_hdrEffect != null)
        {
            _hdrEffect.Parameters["WorldViewProjection"]?.SetValue(wvp);
        }
        else
        {
            _basicEffect!.Projection = wvp;
            _basicEffect.View = Matrix.Identity;
            _basicEffect.World = Matrix.Identity;
            _basicEffectTex!.Projection = wvp;
            _basicEffectTex.View = Matrix.Identity;
            _basicEffectTex.World = Matrix.Identity;
        }

        for (int i = 0; i < _buckets.Count; i++)
        {
            var verts = _buckets[i];
            int count = verts.Count - verts.Count % 3;
            if (count < 3) { verts.Clear(); continue; }

            if (_flushScratch.Length < count)
                _flushScratch = new VertexPositionColor[Math.Max(count, _flushScratch.Length * 2)];
            verts.CopyTo(0, _flushScratch, 0, count);
            verts.Clear();

            _hdrEffect?.Parameters["Intensity"]?.SetValue(_intensities[i]);
            var effect = _hdrEffect ?? (Microsoft.Xna.Framework.Graphics.Effect?)_basicEffect;
            foreach (var pass in effect!.CurrentTechnique.Passes)
            {
                pass.Apply();
                _device.DrawUserPrimitives(PrimitiveType.TriangleList, _flushScratch, 0, count / 3);
            }
        }

        // Textured buckets: switch the HDR effect to its textured technique for
        // these draws (restored after), or fall back to a textured BasicEffect.
        bool anyTex = false;
        foreach (var b in _texBuckets) anyTex |= b.Count >= 3;
        if (!anyTex) return;

        var prevTechnique = _hdrEffect?.CurrentTechnique;
        var texTechnique = _hdrEffect?.Techniques["HdrIntensityTexturedTechnique"];
        if (_hdrEffect != null && texTechnique != null)
        {
            _hdrEffect.CurrentTechnique = texTechnique;
            _hdrEffect.Parameters["ScrollTexture"]?.SetValue(_scrollTexture);
        }
        else if (_hdrEffect == null)
        {
            _basicEffectTex!.Texture = _scrollTexture;
        }
        // HdrIntensity loaded but predates the textured technique: draw nothing
        // rather than crash (texTechnique null while _hdrEffect set).
        if (_hdrEffect != null && texTechnique == null)
        {
            foreach (var b in _texBuckets) b.Clear();
            return;
        }

        for (int i = 0; i < _texBuckets.Count; i++)
        {
            var verts = _texBuckets[i];
            int count = verts.Count - verts.Count % 3;
            if (count < 3) { verts.Clear(); continue; }

            if (_flushScratchTex.Length < count)
                _flushScratchTex = new VertexPositionColorTexture[Math.Max(count, _flushScratchTex.Length * 2)];
            verts.CopyTo(0, _flushScratchTex, 0, count);
            verts.Clear();

            _hdrEffect?.Parameters["Intensity"]?.SetValue(_texIntensities[i]);
            var effect = _hdrEffect ?? (Microsoft.Xna.Framework.Graphics.Effect?)_basicEffectTex;
            foreach (var pass in effect!.CurrentTechnique.Passes)
            {
                pass.Apply();
                _device.DrawUserPrimitives(PrimitiveType.TriangleList, _flushScratchTex, 0, count / 3);
            }
        }

        if (_hdrEffect != null && prevTechnique != null)
            _hdrEffect.CurrentTechnique = prevTechnique;
    }
}

/// <summary>
/// Triangulates a polyline into a miter-joined ribbon with a trapezoid alpha
/// cross-section: full alpha across the middle, linear falloff to transparent at
/// the edges. The profile is energy-preserving vs. a flat rectangle of the nominal
/// width (inner + outer extent = width), so swapping the old rectangle path for
/// this one keeps the same apparent brightness while gaining soft edges.
///
/// Static scratch lists — render-thread only (the whole pipeline is single-threaded).
/// </summary>
public static class PolylineStrip
{
    private static readonly List<Vector2> _pts = new();
    private static readonly List<float> _arc = new();
    private static readonly List<Vector2> _miter = new();

    /// <summary>Shared geometry prep: clean the points, accumulate arc length,
    /// compute miter normals — into the static scratch lists. Returns total arc
    /// length, or 0 when the path is degenerate (skip emission).</summary>
    private static float Prepare(IReadOnlyList<Vector2> points,
        float widthStart, float widthEnd, float edgeSoft)
    {
        if (points.Count < 2) return 0f;

        // Adaptive point spacing: a ribbon can't resolve kinks smaller than its
        // own width — cross-sections of neighboring points would cross and the
        // strip folds over itself (dark/pale sheet artifacts inside wide bolts).
        // Merging points closer than ~60% of the outer half-width keeps the path
        // renderable at any thickness; thin ribbons keep nearly every point.
        // (Also guards the NaN miters that zero-length segments would produce.)
        float soft0 = Math.Clamp(edgeSoft, 0f, 1f);
        float maxOuter = MathF.Max(widthStart, widthEnd) * 0.5f * (1f + soft0);
        float minDist = MathF.Max(0.5f, maxOuter * 0.6f);
        float minDistSq = minDist * minDist;

        _pts.Clear();
        _pts.Add(points[0]);
        for (int i = 1; i < points.Count - 1; i++)
            if (Vector2.DistanceSquared(points[i], _pts[^1]) >= minDistSq) _pts.Add(points[i]);
        // The endpoint must survive cleaning (a bolt has to reach its target):
        // replace the last kept point if the true end lands too close to it.
        var endPt = points[^1];
        if (_pts.Count > 1 && Vector2.DistanceSquared(endPt, _pts[^1]) < 0.25f) _pts[^1] = endPt;
        else _pts.Add(endPt);
        int n = _pts.Count;
        if (n < 2 || Vector2.DistanceSquared(_pts[0], _pts[1]) < 0.25f) return 0f;

        // Cumulative arc length drives the width/alpha lerp (points from midpoint
        // displacement are not uniformly spaced, so index-based lerp would wobble).
        _arc.Clear();
        _arc.Add(0f);
        float total = 0f;
        for (int i = 1; i < n; i++)
        {
            total += Vector2.Distance(_pts[i - 1], _pts[i]);
            _arc.Add(total);
        }
        if (total <= 0f) return 0f;

        // Per-point miter normal: average of the two segment perpendiculars, scaled
        // so ribbon width holds through the bend. Clamped to 2x width (sharp bolt
        // kinks would otherwise spike the miter point outward and cross neighbors).
        _miter.Clear();
        for (int i = 0; i < n; i++)
        {
            Vector2 dPrev = i > 0 ? _pts[i] - _pts[i - 1] : _pts[1] - _pts[0];
            Vector2 dNext = i < n - 1 ? _pts[i + 1] - _pts[i] : _pts[n - 1] - _pts[n - 2];
            dPrev.Normalize();
            dNext.Normalize();
            var perpPrev = new Vector2(-dPrev.Y, dPrev.X);
            var m = perpPrev + new Vector2(-dNext.Y, dNext.X);
            float len = m.Length();
            if (len < 1e-3f) { _miter.Add(perpPrev); continue; } // 180° reversal
            m /= len;
            _miter.Add(m / MathF.Max(Vector2.Dot(m, perpPrev), 0.5f));
        }
        return total;
    }

    /// <param name="outVerts">Triangle-list vertices are appended here.</param>
    /// <param name="points">Polyline in screen space.</param>
    /// <param name="tint">Ribbon color; tint.A folds into the alpha ramp.</param>
    /// <param name="alphaStart">Fade multiplier (0-1) at the first point.</param>
    /// <param name="alphaEnd">Fade multiplier (0-1) at the last point.</param>
    /// <param name="widthStart">Full nominal thickness (px) at the first point.</param>
    /// <param name="widthEnd">Full nominal thickness (px) at the last point — a
    /// different value tapers the ribbon along its arc length.</param>
    /// <param name="edgeSoft">0 = hard edges (flat profile), 1 = full tent profile.
    /// Falloff spans half*edgeSoft inside AND outside the nominal half-width.</param>
    public static void Build(List<VertexPositionColor> outVerts, IReadOnlyList<Vector2> points,
        Color tint, float alphaStart, float alphaEnd,
        float widthStart, float widthEnd, float edgeSoft)
        => Build(outVerts, points, tint, tint, alphaStart, alphaEnd,
            widthStart, widthEnd, edgeSoft);

    /// <summary>Gradient variant: RGB lerps from <paramref name="tintStart"/> at
    /// the first point to <paramref name="tintEnd"/> at the last, along arc length
    /// (each tint's own A folds into its end of the alpha ramp). Drives the
    /// drain beam's hotter-at-the-source color shift.</summary>
    public static void Build(List<VertexPositionColor> outVerts, IReadOnlyList<Vector2> points,
        Color tintStart, Color tintEnd, float alphaStart, float alphaEnd,
        float widthStart, float widthEnd, float edgeSoft)
    {
        float total = Prepare(points, widthStart, widthEnd, edgeSoft);
        if (total <= 0f) return;
        int n = _pts.Count;

        // Emit 3 quad bands between 4 cross-section rows per point:
        // -outer (a=0), -inner (a=full), +inner (a=full), +outer (a=0).
        float soft = Math.Clamp(edgeSoft, 0f, 1f);

        Span<Vector2> prev = stackalloc Vector2[4];
        Span<Vector2> cur = stackalloc Vector2[4];
        Color prevInnerColor = default;
        Color prevEdgeColor = default;

        for (int i = 0; i < n; i++)
        {
            float t = _arc[i] / total;
            float half = MathHelper.Lerp(widthStart, widthEnd, t) * 0.5f;
            float inner = half * (1f - soft);
            float outer = half * (1f + soft);
            float r8 = MathHelper.Lerp(tintStart.R, tintEnd.R, t);
            float g8 = MathHelper.Lerp(tintStart.G, tintEnd.G, t);
            float b8 = MathHelper.Lerp(tintStart.B, tintEnd.B, t);
            float aTint = MathHelper.Lerp(tintStart.A, tintEnd.A, t) / 255f;
            float a = MathHelper.Lerp(alphaStart, alphaEnd, t) * aTint;
            var innerColor = new Color((byte)r8, (byte)g8, (byte)b8,
                (byte)Math.Clamp((int)(a * 255f + 0.5f), 0, 255));
            var edgeColor = new Color((byte)r8, (byte)g8, (byte)b8, (byte)0);

            var p = _pts[i];
            var mtr = _miter[i];
            cur[0] = p - mtr * outer;
            cur[1] = p - mtr * inner;
            cur[2] = p + mtr * inner;
            cur[3] = p + mtr * outer;

            if (i > 0)
            {
                for (int r = 0; r < 3; r++)
                {
                    Color pc0 = (r == 0) ? prevEdgeColor : prevInnerColor;
                    Color pc1 = (r == 2) ? prevEdgeColor : prevInnerColor;
                    Color cc0 = (r == 0) ? edgeColor : innerColor;
                    Color cc1 = (r == 2) ? edgeColor : innerColor;
                    outVerts.Add(new VertexPositionColor(new Vector3(prev[r], 0f), pc0));
                    outVerts.Add(new VertexPositionColor(new Vector3(cur[r], 0f), cc0));
                    outVerts.Add(new VertexPositionColor(new Vector3(prev[r + 1], 0f), pc1));
                    outVerts.Add(new VertexPositionColor(new Vector3(cur[r], 0f), cc0));
                    outVerts.Add(new VertexPositionColor(new Vector3(cur[r + 1], 0f), cc1));
                    outVerts.Add(new VertexPositionColor(new Vector3(prev[r + 1], 0f), pc1));
                }
            }

            cur.CopyTo(prev);
            prevInnerColor = innerColor;
            prevEdgeColor = edgeColor;
        }
    }

    /// <summary>
    /// Textured variant for the drain-beam scroll layers: same ribbon geometry,
    /// but emits UVs — U is arc length in pixels offset by <paramref name="uOffsetPx"/>
    /// and divided by <paramref name="uScalePx"/> (px per texture repeat), V spans
    /// 0→1 across the ribbon. Sampled with wrap, so animating uOffsetPx scrolls
    /// the texture along the beam. Alpha still carries the trapezoid edge profile;
    /// the texture's own alpha multiplies on top in the shader.
    /// </summary>
    public static void BuildTextured(List<VertexPositionColorTexture> outVerts,
        IReadOnlyList<Vector2> points, Color tintStart, Color tintEnd,
        float alphaStart, float alphaEnd, float widthStart, float widthEnd,
        float edgeSoft, float uOffsetPx, float uScalePx)
    {
        float total = Prepare(points, widthStart, widthEnd, edgeSoft);
        if (total <= 0f) return;
        int n = _pts.Count;
        float uScale = MathF.Max(uScalePx, 1f);

        float soft = Math.Clamp(edgeSoft, 0f, 1f);

        Span<Vector2> prev = stackalloc Vector2[4];
        Span<Vector2> cur = stackalloc Vector2[4];
        Span<float> prevV = stackalloc float[4];
        Span<float> curV = stackalloc float[4];
        Color prevInnerColor = default;
        Color prevEdgeColor = default;
        float prevU = 0f;

        for (int i = 0; i < n; i++)
        {
            float t = _arc[i] / total;
            float half = MathHelper.Lerp(widthStart, widthEnd, t) * 0.5f;
            float inner = half * (1f - soft);
            float outer = half * (1f + soft);
            float r8 = MathHelper.Lerp(tintStart.R, tintEnd.R, t);
            float g8 = MathHelper.Lerp(tintStart.G, tintEnd.G, t);
            float b8 = MathHelper.Lerp(tintStart.B, tintEnd.B, t);
            float aTint = MathHelper.Lerp(tintStart.A, tintEnd.A, t) / 255f;
            float a = MathHelper.Lerp(alphaStart, alphaEnd, t) * aTint;
            var innerColor = new Color((byte)r8, (byte)g8, (byte)b8,
                (byte)Math.Clamp((int)(a * 255f + 0.5f), 0, 255));
            var edgeColor = new Color((byte)r8, (byte)g8, (byte)b8, (byte)0);

            var p = _pts[i];
            var mtr = _miter[i];
            cur[0] = p - mtr * outer;
            cur[1] = p - mtr * inner;
            cur[2] = p + mtr * inner;
            cur[3] = p + mtr * outer;

            float u = (_arc[i] + uOffsetPx) / uScale;
            // V rows by physical cross-section offset (outer edges 0 and 1).
            float vInner = outer > 0.001f ? inner / (outer * 2f) : 0f;
            curV[0] = 0f;
            curV[1] = 0.5f - vInner;
            curV[2] = 0.5f + vInner;
            curV[3] = 1f;

            if (i > 0)
            {
                for (int r = 0; r < 3; r++)
                {
                    Color pc0 = (r == 0) ? prevEdgeColor : prevInnerColor;
                    Color pc1 = (r == 2) ? prevEdgeColor : prevInnerColor;
                    Color cc0 = (r == 0) ? edgeColor : innerColor;
                    Color cc1 = (r == 2) ? edgeColor : innerColor;
                    outVerts.Add(new VertexPositionColorTexture(new Vector3(prev[r], 0f), pc0, new Vector2(prevU, prevV[r])));
                    outVerts.Add(new VertexPositionColorTexture(new Vector3(cur[r], 0f), cc0, new Vector2(u, curV[r])));
                    outVerts.Add(new VertexPositionColorTexture(new Vector3(prev[r + 1], 0f), pc1, new Vector2(prevU, prevV[r + 1])));
                    outVerts.Add(new VertexPositionColorTexture(new Vector3(cur[r], 0f), cc0, new Vector2(u, curV[r])));
                    outVerts.Add(new VertexPositionColorTexture(new Vector3(cur[r + 1], 0f), cc1, new Vector2(u, curV[r + 1])));
                    outVerts.Add(new VertexPositionColorTexture(new Vector3(prev[r + 1], 0f), pc1, new Vector2(prevU, prevV[r + 1])));
                }
            }

            cur.CopyTo(prev);
            curV.CopyTo(prevV);
            prevInnerColor = innerColor;
            prevEdgeColor = edgeColor;
            prevU = u;
        }
    }
}
