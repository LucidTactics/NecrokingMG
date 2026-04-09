using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;

namespace Necroking.Render;

/// <summary>
/// God ray beam rendering using triangle-based trapezoid strips.
/// Matches C++ god_ray.h: layered trapezoids with HDR intensity shader,
/// edge softness sublayers, noise modulation, and ground aura ellipse.
/// </summary>
public class GodRayRenderer
{
    private GraphicsDevice _graphicsDevice = null!;
    private Microsoft.Xna.Framework.Graphics.Effect? _hdrIntensityEffect;
    private BasicEffect? _basicEffect;
    private readonly List<VertexPositionColor> _triVerts = new();

    // Pending god rays collected during lightning Draw(), rendered in DrawGodRays()
    public readonly List<(Vector2 sky, Vector2 ground, LightningStyle style, GodRayParams p,
        float elapsed, float effectTimer, float effectDuration)> PendingGodRays = new();

    public void Init(GraphicsDevice graphicsDevice,
                     Microsoft.Xna.Framework.Graphics.Effect? hdrIntensityEffect)
    {
        _graphicsDevice = graphicsDevice;
        _hdrIntensityEffect = hdrIntensityEffect;
        _basicEffect = new BasicEffect(graphicsDevice)
        {
            VertexColorEnabled = true,
            TextureEnabled = false,
            LightingEnabled = false,
        };
    }

    /// <summary>Flush pending triangle vertices with current shader state.</summary>
    private void FlushTriangles()
    {
        if (_triVerts.Count < 3) return;
        var verts = _triVerts.ToArray();
        _triVerts.Clear();
        var effect = _hdrIntensityEffect ?? (Microsoft.Xna.Framework.Graphics.Effect?)_basicEffect;
        foreach (var pass in effect!.CurrentTechnique.Passes)
        {
            pass.Apply();
            _graphicsDevice.DrawUserPrimitives(PrimitiveType.TriangleList,
                verts, 0, verts.Length / 3);
        }
    }

    /// <summary>
    /// Draw all pending god rays. Must be called AFTER the additive SpriteBatch.End() in Game1.
    /// </summary>
    public void DrawAll()
    {
        if (PendingGodRays.Count == 0) return;

        var vp = _graphicsDevice.Viewport;
        var wvp = Matrix.CreateOrthographicOffCenter(0, vp.Width, vp.Height, 0, 0, 1);

        _graphicsDevice.BlendState = BlendState.Additive;
        _graphicsDevice.DepthStencilState = DepthStencilState.None;
        _graphicsDevice.RasterizerState = RasterizerState.CullNone;

        if (_hdrIntensityEffect != null)
        {
            _hdrIntensityEffect.Parameters["WorldViewProjection"]?.SetValue(wvp);
            // Intensity is set per sublayer; MaxBright disabled (no cap, matching C++)
        }
        else
        {
            _basicEffect!.Projection = wvp;
            _basicEffect.View = Matrix.Identity;
            _basicEffect.World = Matrix.Identity;
        }

        _triVerts.Clear();
        foreach (var (sky, ground, style, p, elapsed, effectTimer, effectDuration) in PendingGodRays)
            DrawGodRay(sky, ground, style, p, elapsed, effectTimer, effectDuration);
        FlushTriangles();
    }

    /// <summary>
    /// Draw a single god ray using SpriteBatch rectangles (for use in preview/editor).
    /// Lower fidelity than triangle-based rendering but works in any SpriteBatch context.
    /// Requires HdrSprite.fx (additive mode) active on the batch for proper HDR.
    /// </summary>
    public static void DrawGodRaySpriteBatch(SpriteBatch batch, Texture2D pixel,
        Vector2 sky, Vector2 ground, LightningStyle style, GodRayParams p,
        float elapsed, float effectTimer, float effectDuration)
    {
        float shimmer = MathF.Sin(elapsed * 8f) * 0.15f + 0.85f;
        float baseAlpha = shimmer;

        if (effectDuration > 0f)
        {
            float remaining = effectDuration - effectTimer;
            if (remaining < 0.15f) baseAlpha *= MathF.Max(0f, remaining / 0.15f);
        }
        if (baseAlpha <= 0.001f) return;

        float cw = style.CoreWidth;
        float gw = style.GlowWidth;

        float[] layerT = { 1f, 0.66f, 0.33f, 0f };
        float[] layerAlphas = { 0.12f, 0.25f, 0.45f, 0.75f };
        Color[] layerColors = {
            style.GlowColor.ToColor(),
            new((byte)((style.CoreColor.R + style.GlowColor.R) / 2),
                (byte)((style.CoreColor.G + style.GlowColor.G) / 2),
                (byte)((style.CoreColor.B + style.GlowColor.B) / 2),
                (byte)((style.CoreColor.A + style.GlowColor.A) / 2)),
            style.CoreColor.ToColor(),
            style.CoreColor.ToColor()
        };
        const int Slices = 16;

        for (int li = 0; li < 4; li++)
        {
            float w = cw + (gw - cw) * layerT[li];
            float widthTop = 5f * w;
            float widthBottom = 30f * w;
            float layerA = baseAlpha * layerAlphas[li];
            if (layerA <= 0.001f) continue;

            var lc = layerColors[li];
            var color = HdrColor.ToHdrVertex(lc, layerA, style.CoreColor.Intensity);

            for (int s = 0; s < Slices; s++)
            {
                float t0 = s / (float)Slices;
                float t1 = (s + 1) / (float)Slices;

                float y0 = sky.Y + (ground.Y - sky.Y) * t0;
                float y1 = sky.Y + (ground.Y - sky.Y) * t1;
                float cx0 = sky.X + (ground.X - sky.X) * t0;
                float hw0 = widthTop + (widthBottom - widthTop) * t0;
                float sliceH = y1 - y0;
                if (sliceH < 0.5f) continue;

                // Noise modulation
                float n = 1f;
                if (p.NoiseStrength > 0.001f)
                {
                    float raw = GodRayNoise(t0 * 10f, cx0 * 0.01f, elapsed, p.NoiseScale, p.NoiseSpeed);
                    n = 1f - p.NoiseStrength * 0.6f + p.NoiseStrength * 0.6f * raw;
                }

                var sliceColor = HdrColor.ToHdrVertex(lc, layerA * n, style.CoreColor.Intensity);
                batch.Draw(pixel, new Vector2(cx0 - hw0, y0), null, sliceColor,
                    0f, Vector2.Zero, new Vector2(hw0 * 2f, sliceH), SpriteEffects.None, 0f);
            }
        }

        // Ground aura ellipse (approximated as horizontal rectangles)
        float auraW = 30f * gw * 1.1f;
        float auraH = 30f * gw * 0.35f;
        const int AuraSlices = 10;
        for (int s = 0; s < AuraSlices; s++)
        {
            float a = s / (float)AuraSlices * MathF.PI;
            float nextA = (s + 1) / (float)AuraSlices * MathF.PI;
            float sliceY = ground.Y - MathF.Sin(a) * auraH;
            float sliceH2 = MathF.Abs(MathF.Sin(nextA) - MathF.Sin(a)) * auraH;
            float sliceW = MathF.Cos(a) * auraW;
            var aColor = HdrColor.ToHdrVertex(style.CoreColor.ToColor(), baseAlpha * 0.15f, style.CoreColor.Intensity);
            if (sliceH2 < 0.5f) continue;
            batch.Draw(pixel, new Vector2(ground.X - MathF.Abs(sliceW), sliceY), null, aColor,
                0f, Vector2.Zero, new Vector2(MathF.Abs(sliceW) * 2f, sliceH2), SpriteEffects.None, 0f);
        }
    }

    // --- God ray noise (layered sine pseudo-noise, matches C++) ---

    public static float GodRayNoise(float y, float x, float t, float scale, float speed)
    {
        float s1 = MathF.Sin(y * scale + t * speed * 2.1f + x * 0.3f);
        float s2 = MathF.Sin(y * scale * 1.7f - t * speed * 1.4f + x * 0.5f);
        float s3 = MathF.Sin(y * scale * 0.6f + t * speed * 0.8f - x * 0.2f);
        return (s1 * s2 + s3) * 0.5f + 0.5f;
    }

    // --- Main god ray drawing (matches C++ drawGodRay in god_ray.h) ---

    private void DrawGodRay(Vector2 sky, Vector2 ground, LightningStyle style, GodRayParams p,
                             float elapsed, float effectTimer, float effectDuration)
    {
        float shimmer = MathF.Sin(elapsed * 8f) * 0.15f + 0.85f;
        float baseAlpha = shimmer;

        if (effectDuration > 0f)
        {
            float remaining = effectDuration - effectTimer;
            if (remaining < 0.15f) baseAlpha *= MathF.Max(0f, remaining / 0.15f);
        }
        if (baseAlpha <= 0.001f) return;

        // Raw colors — HDR intensity shader handles brightness (matches C++)
        var core = style.CoreColor.ToColor();
        var glow = style.GlowColor.ToColor();
        var mid = new Color((byte)((core.R + glow.R) / 2), (byte)((core.G + glow.G) / 2),
                            (byte)((core.B + glow.B) / 2), (byte)((core.A + glow.A) / 2));

        float cw = style.CoreWidth;
        float gw = style.GlowWidth;

        // 4 layers from outer glow to inner core (all values match C++)
        float[] layerT = { 1f, 0.66f, 0.33f, 0f };
        Color[] layerColors = { glow, mid, core, core };
        float[] layerAlphas = { 0.12f, 0.25f, 0.45f, 0.75f };
        float edgeSoft = MathF.Max(0f, MathF.Min(1f, p.EdgeSoftness));
        const int EdgeSublayers = 3;
        const int Slices = 20;

        for (int li = 0; li < 4; li++)
        {
            float w = cw + (gw - cw) * layerT[li];
            float widthTop = 5f * w;
            float widthBottom = 30f * w;
            Color lc = layerColors[li];
            float lAlpha = layerAlphas[li];

            // Draw edge sub-layers (wider, more transparent) then core layer
            for (int sub = EdgeSublayers; sub >= 0; sub--)
            {
                float expand = sub > 0 ? edgeSoft * sub / EdgeSublayers : 0f;
                float subAlphaMul = sub > 0 ? (1f / (sub + 1)) * 0.5f : 1f;
                float wMul = 1f + expand;
                float layerA = baseAlpha * lAlpha * subAlphaMul;
                if (layerA <= 0.001f) continue;

                // Use constant intensity for all sublayers — vertex alpha alone controls falloff.
                // (C++ raylib batching accidentally did this, producing smooth gradation)
                FlushTriangles();
                _hdrIntensityEffect?.Parameters["Intensity"]?.SetValue(style.CoreColor.Intensity);

                byte ca = (byte)(lc.A * MathF.Min(1f, layerA));

                for (int s = 0; s < Slices; s++)
                {
                    float t0 = s / (float)Slices;
                    float t1 = (s + 1) / (float)Slices;

                    float y0 = sky.Y + (ground.Y - sky.Y) * t0;
                    float y1 = sky.Y + (ground.Y - sky.Y) * t1;
                    float cx0 = sky.X + (ground.X - sky.X) * t0;
                    float cx1 = sky.X + (ground.X - sky.X) * t1;
                    float hw0 = (widthTop + (widthBottom - widthTop) * t0) * wMul;
                    float hw1 = (widthTop + (widthBottom - widthTop) * t1) * wMul;

                    // Noise modulation on innermost sub-layer
                    float n = 1f;
                    if (p.NoiseStrength > 0.001f && sub == 0)
                    {
                        float raw = GodRayNoise(t0 * 10f, cx0 * 0.01f, elapsed, p.NoiseScale, p.NoiseSpeed);
                        n = 1f - p.NoiseStrength * 0.6f + p.NoiseStrength * 0.6f * raw;
                    }

                    byte sliceA = (byte)(ca * n);
                    Color sliceColor = new(lc.R, lc.G, lc.B, sliceA);

                    // Two triangles forming a proper trapezoid (matches C++ DrawTriangle)
                    var tl = new Vector3(cx0 - hw0, y0, 0);
                    var tr = new Vector3(cx0 + hw0, y0, 0);
                    var bl = new Vector3(cx1 - hw1, y1, 0);
                    var br = new Vector3(cx1 + hw1, y1, 0);

                    _triVerts.Add(new VertexPositionColor(tr, sliceColor));
                    _triVerts.Add(new VertexPositionColor(tl, sliceColor));
                    _triVerts.Add(new VertexPositionColor(bl, sliceColor));
                    _triVerts.Add(new VertexPositionColor(tr, sliceColor));
                    _triVerts.Add(new VertexPositionColor(bl, sliceColor));
                    _triVerts.Add(new VertexPositionColor(br, sliceColor));
                }
            }

            // Ground aura ellipse (triangle fan, matches C++)
            float auraW = widthBottom * 1.1f;
            float auraH = widthBottom * 0.35f;
            float auraAlpha = baseAlpha * lAlpha * 0.4f;
            FlushTriangles();
            _hdrIntensityEffect?.Parameters["Intensity"]?.SetValue(style.CoreColor.Intensity);
            byte ga = (byte)(lc.A * MathF.Min(1f, auraAlpha));
            Color auraColor = new(lc.R, lc.G, lc.B, ga);
            const int AuraSegs = 20;
            var center = new Vector3(ground.X, ground.Y, 0);
            for (int seg = 0; seg < AuraSegs; seg++)
            {
                float a0 = seg / (float)AuraSegs * MathF.PI * 2f;
                float a1 = (seg + 1) / (float)AuraSegs * MathF.PI * 2f;
                var e0 = new Vector3(ground.X + MathF.Cos(a0) * auraW, ground.Y + MathF.Sin(a0) * auraH, 0);
                var e1 = new Vector3(ground.X + MathF.Cos(a1) * auraW, ground.Y + MathF.Sin(a1) * auraH, 0);
                _triVerts.Add(new VertexPositionColor(center, auraColor));
                _triVerts.Add(new VertexPositionColor(e1, auraColor));
                _triVerts.Add(new VertexPositionColor(e0, auraColor));
            }
        }
    }
}
