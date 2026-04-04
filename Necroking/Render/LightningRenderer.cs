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
                    // Collect for separate god ray pass
                    float sH = _graphicsDevice.Viewport.Height;
                    _godRayRenderer.PendingGodRays.Add((new Vector2(sp.X - 200f, sp.Y - sH * 0.6f), sp,
                        strike.Style, strike.GodRay, _gameTime, strike.EffectTimer, strike.EffectDuration));
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

    /// <summary>
    /// Draw god rays in a separate pass. Must be called AFTER the additive SpriteBatch.End().
    /// </summary>
    public void DrawGodRays() => _godRayRenderer.DrawAll();

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
