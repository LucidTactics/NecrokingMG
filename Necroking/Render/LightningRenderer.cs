using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Movement;

namespace Necroking.Render;

public class LightningRenderer
{
    private SpriteBatch _spriteBatch = null!;
    private Texture2D _pixel = null!;
    private Texture2D _glowTex = null!;
    private Simulation _sim = null!;
    private Camera25D _camera = null!;
    private Renderer _renderer = null!;
    private GraphicsDevice _graphicsDevice = null!;
    private float _gameTime;

    public void Init(SpriteBatch spriteBatch, Texture2D pixel, Texture2D glowTex,
                     Simulation sim, Camera25D camera, Renderer renderer, GraphicsDevice graphicsDevice)
    {
        _spriteBatch = spriteBatch;
        _pixel = pixel;
        _glowTex = glowTex;
        _sim = sim;
        _camera = camera;
        _renderer = renderer;
        _graphicsDevice = graphicsDevice;
    }

    public void SetGameTime(float gameTime) => _gameTime = gameTime;

    public void Draw()
    {
        // Draw active strikes
        foreach (var strike in _sim.Lightning.Strikes)
        {
            if (!strike.Alive) continue;

            if (strike.TelegraphTimer < strike.TelegraphDuration)
            {
                // Telegraph: pulsing circle on ground
                var sp = _renderer.WorldToScreen(strike.TargetPos, 0f, _camera);
                float pulse = 0.5f + 0.5f * MathF.Sin(strike.TelegraphTimer * 20f);
                float radius = strike.AoeRadius * _camera.Zoom * pulse;
                byte alpha = (byte)(100 * pulse);
                _spriteBatch.Draw(_glowTex, sp, null, new Color((byte)255, (byte)200, (byte)100, alpha),
                    0f, new Vector2(32f, 32f), new Vector2(radius * 2 / 32f, radius * _camera.YRatio / 32f), SpriteEffects.None, 0f);
            }
            else
            {
                var sp = _renderer.WorldToScreen(strike.TargetPos, 0f, _camera);
                float fade = 1f - strike.EffectTimer / strike.EffectDuration;

                if (strike.Visual == StrikeVisual.GodRay)
                {
                    // God ray: beam from sky to ground
                    float sH = _graphicsDevice.Viewport.Height;
                    DrawGodRay(new Vector2(sp.X - 200f, sp.Y - sH * 0.6f), sp,
                        strike.Style, strike.GodRay, _gameTime, strike.EffectTimer, strike.EffectDuration);
                }
                else
                {
                    // Lightning effect: bright flash (radial glow, not rectangle)
                    float radius = strike.AoeRadius * _camera.Zoom;
                    byte coreAlpha = (byte)(255 * fade);
                    var coreColor = strike.Style.CoreColor.ToScaledColor();
                    _spriteBatch.Draw(_glowTex, sp, null,
                        new Color(coreColor.R, coreColor.G, coreColor.B, coreAlpha),
                        0f, new Vector2(32f, 32f), new Vector2(radius / 32f, radius * _camera.YRatio * 0.5f / 32f),
                        SpriteEffects.None, 0f);

                    // Procedural lightning bolt from sky (start above top of screen)
                    float skyY = -50f; // well above screen edge
                    float skyX = sp.X - 50f + (sp.Y - skyY) * 0.05f; // slight angle
                    DrawLightningBolt(new Vector2(skyX, skyY), sp, strike.Style, fade);
                }
            }
        }

        // Draw active zaps
        foreach (var zap in _sim.Lightning.Zaps)
        {
            if (!zap.Alive) continue;
            var startSp = _renderer.WorldToScreen(zap.StartPos, zap.StartHeight, _camera);
            var endSp = _renderer.WorldToScreen(zap.EndPos, zap.EndHeight, _camera);
            float fade = 1f - zap.Timer / zap.Duration;
            DrawLightningBolt(startSp, endSp, zap.Style, fade);
        }

        // Draw active beams
        foreach (var beam in _sim.Lightning.Beams)
        {
            if (!beam.Alive) continue;
            int casterIdx = UnitUtil.ResolveUnitIndex(_sim.Units, beam.CasterID);
            int targetIdx = UnitUtil.ResolveUnitIndex(_sim.Units, beam.TargetID);
            if (casterIdx < 0 || targetIdx < 0) continue;

            var startSp = _renderer.WorldToScreen(_sim.Units[casterIdx].EffectSpawnPos2D,
                _sim.Units[casterIdx].EffectSpawnHeight, _camera);
            var endSp = _renderer.WorldToScreen(_sim.Units[targetIdx].Position, 1f, _camera);
            DrawLightningBolt(startSp, endSp, beam.Style, 1f);
        }

        // Draw active drains
        foreach (var drain in _sim.Lightning.Drains)
        {
            if (!drain.Alive) continue;
            int casterIdx = UnitUtil.ResolveUnitIndex(_sim.Units, drain.CasterID);
            if (casterIdx < 0) continue;

            Vec2 targetPos = drain.TargetCorpseIdx >= 0 ? drain.CorpsePos : Vec2.Zero;
            int targetIdx = UnitUtil.ResolveUnitIndex(_sim.Units, drain.TargetID);
            if (targetIdx >= 0) targetPos = _sim.Units[targetIdx].Position;

            var startSp = _renderer.WorldToScreen(_sim.Units[casterIdx].EffectSpawnPos2D,
                _sim.Units[casterIdx].EffectSpawnHeight, _camera);
            var endSp = _renderer.WorldToScreen(targetPos, 1f, _camera);

            // Draw multiple tendrils with sway
            for (int t = 0; t < drain.TendrilCount; t++)
            {
                float offset = (t - drain.TendrilCount / 2f) * 8f;
                float sway = MathF.Sin(drain.Elapsed * 3f + t * 2f) * 6f;
                var swayStart = new Vector2(startSp.X + offset, startSp.Y);
                var swayEnd = new Vector2(endSp.X + sway, endSp.Y);
                DrawTendril(swayStart, swayEnd, drain.CoreColor, drain.GlowColor, drain.Elapsed);
            }
        }
    }

    private void DrawLightningBolt(Vector2 start, Vector2 end, LightningStyle style, float fade)
    {
        // Generate main bolt via recursive midpoint displacement
        uint seed = (uint)(start.X * 1000 + end.Y * 7 + _gameTime * 60);
        var mainPoints = GenerateBoltPoints(start, end, style.Subdivisions, style.Displacement, ref seed);
        if (mainPoints.Count < 2) return;

        // Generate branches from the main bolt
        var branches = GenerateBranches(mainPoints, style, ref seed);

        byte coreAlpha = (byte)(fade * 255);
        var coreColor = style.CoreColor.ToScaledColor();
        var glowColor = style.GlowColor.ToScaledColor();

        // Draw branches first (behind main bolt), with reduced width/alpha
        float branchCoreW = style.CoreWidth * style.BranchDecay;
        float branchGlowW = style.GlowWidth * style.BranchDecay;
        byte branchCoreA = (byte)(coreAlpha * 0.7f);
        byte branchGlowA = (byte)(coreAlpha * 0.2f);

        foreach (var branch in branches)
        {
            DrawBoltPolyline(branch, coreColor, glowColor,
                branchCoreW * fade, branchGlowW * fade, branchCoreA, branchGlowA);
        }

        // Draw main bolt on top
        DrawBoltPolyline(mainPoints, coreColor, glowColor,
            style.CoreWidth * fade, style.GlowWidth * fade, coreAlpha, (byte)(coreAlpha * 0.4f));
    }

    /// <summary>
    /// Recursive midpoint displacement: each iteration doubles the point count,
    /// inserting displaced midpoints between every pair.
    /// Uses pre-allocated capacity to reduce GC pressure.
    /// </summary>
    private static List<Vector2> GenerateBoltPoints(Vector2 start, Vector2 end,
        int subdivisions, float displacement, ref uint seed)
    {
        // Final point count = 2^subdivisions + 1. Pre-allocate to avoid resizing.
        int finalCount = (1 << subdivisions) + 1;
        var points = new List<Vector2>(finalCount) { start, end };

        for (int iter = 0; iter < subdivisions; iter++)
        {
            var newPoints = new List<Vector2>(points.Count * 2);
            for (int i = 0; i < points.Count - 1; i++)
            {
                newPoints.Add(points[i]);

                // Midpoint with perpendicular displacement
                var mid = (points[i] + points[i + 1]) * 0.5f;
                var seg = points[i + 1] - points[i];
                float segLen = seg.Length();
                if (segLen < 0.5f) { continue; }
                var perp = new Vector2(-seg.Y / segLen, seg.X / segLen);

                seed = seed * 1103515245 + 12345;
                float offset = ((seed % 1000) / 500f - 1f) * segLen * displacement;
                newPoints.Add(mid + perp * offset);
            }
            newPoints.Add(points[^1]);
            points = newPoints;
        }
        return points;
    }

    /// <summary>
    /// Generate forking branches from the main bolt. Branches spawn from the middle
    /// 50% of the bolt (25%-75%), each forking at a natural angle off the main path.
    /// </summary>
    private static List<List<Vector2>> GenerateBranches(List<Vector2> mainPoints,
        LightningStyle style, ref uint seed)
    {
        var branches = new List<List<Vector2>>();
        if (style.MaxBranches <= 0 || style.BranchChance <= 0f) return branches;

        int count = mainPoints.Count;
        int startIdx = count / 4;       // 25%
        int endIdx = count * 3 / 4;     // 75%

        for (int i = startIdx; i < endIdx && branches.Count < style.MaxBranches; i++)
        {
            seed = seed * 1103515245 + 12345;
            float roll = (seed % 1000) / 1000f;
            if (roll > style.BranchChance) continue;

            // Branch direction: tangent + perpendicular * 0.8 (random side)
            Vector2 tangent;
            if (i + 1 < count)
                tangent = mainPoints[i + 1] - mainPoints[i];
            else
                tangent = mainPoints[i] - mainPoints[i - 1];
            float tanLen = tangent.Length();
            if (tanLen < 0.1f) continue;
            tangent /= tanLen;

            var perp = new Vector2(-tangent.Y, tangent.X);
            seed = seed * 1103515245 + 12345;
            float side = (seed % 2 == 0) ? 1f : -1f;

            var branchDir = tangent + perp * side * 0.8f;
            float bdLen = branchDir.Length();
            if (bdLen > 0.01f) branchDir /= bdLen;

            // Branch length: fraction of remaining main bolt distance
            float remainDist = (mainPoints[^1] - mainPoints[i]).Length();
            float branchLen = remainDist * style.BranchLength;
            if (branchLen < 5f) continue;

            Vector2 branchEnd = mainPoints[i] + branchDir * branchLen;

            // Generate branch bolt with fewer subdivisions, no further branching
            int branchSubdiv = Math.Max(1, style.Subdivisions - 2);
            var branchPoints = GenerateBoltPoints(mainPoints[i], branchEnd,
                branchSubdiv, style.Displacement, ref seed);
            branches.Add(branchPoints);
        }
        return branches;
    }

    /// <summary>
    /// Draw a polyline as glow + core segments.
    /// </summary>
    private void DrawBoltPolyline(List<Vector2> points, Color coreColor, Color glowColor,
        float coreWidth, float glowWidth, byte coreAlpha, byte glowAlpha)
    {
        for (int i = 0; i < points.Count - 1; i++)
        {
            var segDir = points[i + 1] - points[i];
            float segLen = segDir.Length();
            if (segLen < 0.5f) continue;
            float angle = MathF.Atan2(segDir.Y, segDir.X);

            // Glow (wider, dimmer)
            _spriteBatch.Draw(_pixel, points[i], null,
                new Color(glowColor.R, glowColor.G, glowColor.B, glowAlpha),
                angle, new Vector2(0, 0.5f), new Vector2(segLen, glowWidth),
                SpriteEffects.None, 0f);

            // Core (narrow, bright)
            _spriteBatch.Draw(_pixel, points[i], null,
                new Color(coreColor.R, coreColor.G, coreColor.B, coreAlpha),
                angle, new Vector2(0, 0.5f), new Vector2(segLen, coreWidth),
                SpriteEffects.None, 0f);
        }
    }

    private static float GodRayNoise(float y, float x, float t, float scale, float speed)
    {
        float s1 = MathF.Sin(y * scale + t * speed * 2.1f + x * 0.3f);
        float s2 = MathF.Sin(y * scale * 1.7f - t * speed * 1.4f + x * 0.5f);
        float s3 = MathF.Sin(y * scale * 0.6f + t * speed * 0.8f - x * 0.2f);
        return (s1 * s2 + s3) * 0.5f + 0.5f;
    }

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

        var core = style.CoreColor.ToScaledColor();
        var glow = style.GlowColor.ToScaledColor();
        var mid = new Color((byte)((core.R + glow.R) / 2), (byte)((core.G + glow.G) / 2),
                            (byte)((core.B + glow.B) / 2), (byte)((core.A + glow.A) / 2));

        float cw = style.CoreWidth;
        float gw = style.GlowWidth;

        // 4 layers from outer glow to inner core
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

                    // Draw quad as two segments (left half, right half)
                    float midX0 = cx0;
                    float midX1 = cx1;
                    float sliceH = y1 - y0;
                    if (sliceH < 0.5f) continue;

                    // Left side of trapezoid
                    _spriteBatch.Draw(_pixel, new Vector2(midX0 - hw0, y0), null, sliceColor,
                        0f, Vector2.Zero, new Vector2(hw0 * 2, sliceH), SpriteEffects.None, 0f);
                }
            }

            // Ground aura ellipse
            float auraW = widthBottom * 1.1f;
            float auraH = widthBottom * 0.35f;
            float auraAlpha = baseAlpha * lAlpha * 0.4f;
            byte ga = (byte)(lc.A * MathF.Min(1f, auraAlpha));
            Color auraColor = new(lc.R, lc.G, lc.B, ga);

            _spriteBatch.Draw(_pixel, new Vector2(ground.X - auraW, ground.Y - auraH * 0.5f), null,
                auraColor, 0f, Vector2.Zero, new Vector2(auraW * 2, auraH), SpriteEffects.None, 0f);
        }
    }

    private void DrawTendril(Vector2 start, Vector2 end, HdrColor coreColor, HdrColor glowColor, float time)
    {
        var dir = end - start;
        float length = dir.Length();
        if (length < 1f) return;
        var norm = dir / length;
        var perp = new Vector2(-norm.Y, norm.X);

        int segments = Math.Max(3, (int)(length / 20f));
        var points = new Vector2[segments + 1];
        points[0] = start;
        points[segments] = end;

        for (int i = 1; i < segments; i++)
        {
            float t = i / (float)segments;
            var basePos = Vector2.Lerp(start, end, t);
            float arc = MathF.Sin(t * MathF.PI) * 20f; // arc height
            float wave = MathF.Sin(time * 4f + t * 8f) * 5f;
            points[i] = basePos + perp * (arc + wave);
        }

        var core = coreColor.ToScaledColor();
        var glow = glowColor.ToScaledColor();

        for (int i = 0; i < segments; i++)
        {
            var segDir = points[i + 1] - points[i];
            float segLen = segDir.Length();
            if (segLen < 0.5f) continue;
            float angle = MathF.Atan2(segDir.Y, segDir.X);

            _spriteBatch.Draw(_pixel, points[i], null,
                new Color(glow.R, glow.G, glow.B, (byte)120),
                angle, new Vector2(0, 0.5f), new Vector2(segLen, 4f), SpriteEffects.None, 0f);
            _spriteBatch.Draw(_pixel, points[i], null,
                new Color(core.R, core.G, core.B, (byte)200),
                angle, new Vector2(0, 0.5f), new Vector2(segLen, 1.5f), SpriteEffects.None, 0f);
        }
    }
}
