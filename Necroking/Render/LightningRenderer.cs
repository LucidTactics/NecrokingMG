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
    private GodRayRenderer _godRayRenderer = null!;

    public void Init(SpriteBatch spriteBatch, Texture2D pixel, Texture2D glowTex,
                     Simulation sim, Camera25D camera, Renderer renderer, GraphicsDevice graphicsDevice,
                     Microsoft.Xna.Framework.Graphics.Effect? hdrIntensityEffect = null)
    {
        _spriteBatch = spriteBatch;
        _pixel = pixel;
        _glowTex = glowTex;
        _sim = sim;
        _camera = camera;
        _renderer = renderer;
        _graphicsDevice = graphicsDevice;
        _godRayRenderer = new GodRayRenderer();
        _godRayRenderer.Init(graphicsDevice, hdrIntensityEffect);
    }

    public void SetGameTime(float gameTime) => _gameTime = gameTime;

    public void Draw()
    {
        _godRayRenderer.PendingGodRays.Clear();

        // Draw active strikes
        foreach (var strike in _sim.Lightning.Strikes)
        {
            if (!strike.Alive) continue;

            if (strike.TelegraphTimer < strike.TelegraphDuration)
            {
                // Telegraph: pulsing circle on ground (only if visible)
                if (strike.TelegraphVisible)
                {
                    var sp = _renderer.WorldToScreen(strike.TargetPos, 0f, _camera);
                    float pulse = 0.5f + 0.5f * MathF.Sin(strike.TelegraphTimer * 20f);
                    float radius = strike.AoeRadius * _camera.Zoom * pulse;
                    byte alpha = (byte)(100 * pulse);
                    _spriteBatch.Draw(_glowTex, sp, null, new Color((byte)255, (byte)200, (byte)100, alpha),
                        0f, new Vector2(32f, 32f), new Vector2(radius * 2 / 32f, radius * _camera.YRatio / 32f), SpriteEffects.None, 0f);
                }
            }
            else
            {
                var sp = _renderer.WorldToScreen(strike.TargetPos, 0f, _camera);
                float fade = 1f - strike.EffectTimer / strike.EffectDuration;

                if (strike.Visual == StrikeVisual.GodRay)
                {
                    // Collect for separate god ray pass
                    float sH = _graphicsDevice.Viewport.Height;
                    _godRayRenderer.PendingGodRays.Add((new Vector2(sp.X - 200f, sp.Y - sH * 0.6f), sp,
                        strike.Style, strike.GodRay, _gameTime, strike.EffectTimer, strike.EffectDuration));
                }
                else
                {
                    // Lightning effect: bright flash (radial glow, not rectangle)
                    float radius = strike.AoeRadius * _camera.Zoom;
                    var splashColor = HdrColor.ToHdrVertex(strike.Style.CoreColor.ToColor(), fade, strike.Style.CoreColor.Intensity);
                    _spriteBatch.Draw(_glowTex, sp, null, splashColor,
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

            DrawDrainTendrils(_spriteBatch, _pixel, startSp, endSp, drain.Visuals, drain.Elapsed);
        }
    }

    /// <summary>
    /// Draw god rays in a separate pass. Must be called AFTER the additive SpriteBatch.End().
    /// </summary>
    public void DrawGodRays() => _godRayRenderer.DrawAll();
    public GodRayRenderer GetGodRayRenderer() => _godRayRenderer;

    private void DrawLightningBolt(Vector2 start, Vector2 end, LightningStyle style, float fade)
        => DrawLightningBoltStatic(_spriteBatch, _pixel, start, end, style, fade, _gameTime);

    /// <summary>
    /// Draw a procedural lightning bolt with branches. Callable from any SpriteBatch context.
    /// Requires HdrSprite.fx active on the batch for proper HDR encoding.
    /// </summary>
    public static void DrawLightningBoltStatic(SpriteBatch batch, Texture2D pixel,
        Vector2 start, Vector2 end, LightningStyle style, float fade, float gameTime,
        float widthScale = 1f)
    {
        // JitterHz: control how often the bolt reshapes (0 = every frame)
        float jitterTime = gameTime;
        if (style.JitterHz > 0.01f)
            jitterTime = MathF.Floor(gameTime * style.JitterHz) / style.JitterHz;

        // FlickerHz: brightness pulsing between FlickerMin and FlickerMax
        float flicker = 1f;
        if (style.FlickerHz > 0.01f && style.FlickerMax > style.FlickerMin)
        {
            float t = MathF.Sin(gameTime * style.FlickerHz * 2f * MathF.PI) * 0.5f + 0.5f;
            flicker = style.FlickerMin + (style.FlickerMax - style.FlickerMin) * t;
        }
        float effectiveFade = fade * flicker;

        uint seed = (uint)(start.X * 1000 + end.Y * 7 + jitterTime * 60);
        var mainPoints = GenerateBoltPoints(start, end, style.Subdivisions, style.Displacement, ref seed);
        if (mainPoints.Count < 2) return;

        var branches = GenerateBranches(mainPoints, style, ref seed);

        float branchCoreW = style.CoreWidth * style.BranchDecay * widthScale;
        float branchGlowW = style.GlowWidth * style.BranchDecay * widthScale;

        foreach (var branch in branches)
        {
            DrawBoltPolylineStatic(batch, pixel, branch, style.CoreColor, style.GlowColor,
                branchCoreW * effectiveFade, branchGlowW * effectiveFade, effectiveFade * 0.7f, effectiveFade * 0.2f);
        }

        DrawBoltPolylineStatic(batch, pixel, mainPoints, style.CoreColor, style.GlowColor,
            style.CoreWidth * widthScale * effectiveFade, style.GlowWidth * widthScale * effectiveFade,
            effectiveFade, effectiveFade * 0.4f);
    }

    /// <summary>
    /// Recursive midpoint displacement: each iteration doubles the point count,
    /// inserting displaced midpoints between every pair.
    /// Uses pre-allocated capacity to reduce GC pressure.
    /// </summary>
    public static List<Vector2> GenerateBoltPoints(Vector2 start, Vector2 end,
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
    public static List<List<Vector2>> GenerateBranches(List<Vector2> mainPoints,
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
    /// <summary>
    /// Draw a polyline as glow + core segments using HDR vertex encoding.
    /// Alpha values are 0-1 fade multipliers (encoded into HDR vertex format for HdrSprite.fx).
    /// </summary>
    public static void DrawBoltPolylineStatic(SpriteBatch batch, Texture2D pixel,
        List<Vector2> points, HdrColor coreHdr, HdrColor glowHdr,
        float coreWidth, float glowWidth, float coreFade, float glowFade)
    {
        var glowColor = HdrColor.ToHdrVertex(glowHdr.ToColor(), glowFade, glowHdr.Intensity);
        var coreColor = HdrColor.ToHdrVertex(coreHdr.ToColor(), coreFade, coreHdr.Intensity);

        for (int i = 0; i < points.Count - 1; i++)
        {
            var segDir = points[i + 1] - points[i];
            float segLen = segDir.Length();
            if (segLen < 0.5f) continue;
            float angle = MathF.Atan2(segDir.Y, segDir.X);

            batch.Draw(pixel, points[i], null, glowColor,
                angle, new Vector2(0, 0.5f), new Vector2(segLen, glowWidth),
                SpriteEffects.None, 0f);

            batch.Draw(pixel, points[i], null, coreColor,
                angle, new Vector2(0, 0.5f), new Vector2(segLen, coreWidth),
                SpriteEffects.None, 0f);
        }
    }


    /// <summary>
    /// Draw a complete drain effect (multiple tendrils with sway and pulse).
    /// Callable from any SpriteBatch context with HdrSprite.fx active.
    /// </summary>
    public static void DrawDrainTendrils(SpriteBatch batch, Texture2D pixel,
        Vector2 start, Vector2 end, DrainVisualParams v, float elapsed)
    {
        float pulse = 1f + v.PulseStrength * MathF.Sin(elapsed * v.PulseHz * 2f * MathF.PI);

        for (int t = 0; t < v.TendrilCount; t++)
        {
            float offset = (t - v.TendrilCount / 2f) * v.SwayAmplitude;
            float sway = MathF.Sin(elapsed * v.SwayHz * 2f * MathF.PI + t * 2f) * v.SwayAmplitude * 0.75f;
            var swayStart = new Vector2(start.X + offset, start.Y);
            var swayEnd = new Vector2(end.X + sway, end.Y);
            DrawTendrilStatic(batch, pixel, swayStart, swayEnd, v.CoreColor, v.GlowColor, elapsed,
                glowWidth: v.GlowWidth * pulse, coreWidth: v.CoreWidth * pulse);
        }
    }

    private void DrawTendril(Vector2 start, Vector2 end, HdrColor coreColor, HdrColor glowColor, float time)
        => DrawTendrilStatic(_spriteBatch, _pixel, start, end, coreColor, glowColor, time);

    /// <summary>
    /// Draw an arcing tendril (used for drain spells). Callable from any SpriteBatch context.
    /// Requires HdrSprite.fx active on the batch for proper HDR encoding.
    /// </summary>
    public static void DrawTendrilStatic(SpriteBatch batch, Texture2D pixel,
        Vector2 start, Vector2 end, HdrColor coreColor, HdrColor glowColor, float time,
        float glowWidth = 4f, float coreWidth = 1.5f)
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
            float arc = MathF.Sin(t * MathF.PI) * 20f;
            float wave = MathF.Sin(time * 4f + t * 8f) * 5f;
            points[i] = basePos + perp * (arc + wave);
        }

        var glowVtx = HdrColor.ToHdrVertex(glowColor.ToColor(), 120f / 255f, glowColor.Intensity);
        var coreVtx = HdrColor.ToHdrVertex(coreColor.ToColor(), 200f / 255f, coreColor.Intensity);

        for (int i = 0; i < segments; i++)
        {
            var segDir = points[i + 1] - points[i];
            float segLen = segDir.Length();
            if (segLen < 0.5f) continue;
            float angle = MathF.Atan2(segDir.Y, segDir.X);

            batch.Draw(pixel, points[i], null, glowVtx,
                angle, new Vector2(0, 0.5f), new Vector2(segLen, glowWidth), SpriteEffects.None, 0f);
            batch.Draw(pixel, points[i], null, coreVtx,
                angle, new Vector2(0, 0.5f), new Vector2(segLen, coreWidth), SpriteEffects.None, 0f);
        }
    }
}
