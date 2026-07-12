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
    // Read the live Simulation each frame off Game1 rather than caching the instance.
    // Game1._sim forwards to the per-game GameSession, which is recreated on every map load —
    // a cached Simulation reference would go stale after the first load and read an orphaned
    // session's (always-empty) Lightning fx, drawing nothing. Holding Game1 (a program-lifetime
    // singleton) instead keeps this renderer following the live session and dead sessions collectable.
    private Game1 _game = null!;
    private Simulation _sim => _game._sim;
    private Camera25D _camera = null!;
    private Renderer _renderer = null!;
    private GraphicsDevice _graphicsDevice = null!;
    private float _gameTime;
    private GodRayRenderer _godRayRenderer = null!;
    // Bolt/tendril ribbons: collected during Draw() (inside the additive sprite
    // batch callback), flushed post-batch in DrawTriangleEffects(). See HdrStripBatch.
    private readonly HdrStripBatch _strips = new();

    // Cross-section softness (PolylineStrip edgeSoft): the core keeps a crisp
    // flat middle with a thin anti-aliasing ramp; the glow is nearly a full tent
    // so it reads as a real falloff instead of a hard box.
    private const float CoreEdgeSoft = 0.5f;
    private const float GlowEdgeSoft = 0.9f;

    public void Init(SpriteBatch spriteBatch, Texture2D pixel, Texture2D glowTex,
                     Game1 game, Camera25D camera, Renderer renderer, GraphicsDevice graphicsDevice,
                     Microsoft.Xna.Framework.Graphics.Effect? hdrIntensityEffect = null)
    {
        _spriteBatch = spriteBatch;
        _pixel = pixel;
        _glowTex = glowTex;
        _game = game;
        _camera = camera;
        _renderer = renderer;
        _graphicsDevice = graphicsDevice;
        _godRayRenderer = new GodRayRenderer();
        _godRayRenderer.Init(graphicsDevice, hdrIntensityEffect);
        _strips.Init(graphicsDevice, hdrIntensityEffect);
    }

    public void SetGameTime(float gameTime) => _gameTime = gameTime;

    public void Draw()
    {
        _godRayRenderer.PendingGodRays.Clear();
        _strips.Clear();

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
                    AddBoltStrips(new Vector2(skyX, skyY), sp, strike.Style, fade);
                }
            }
        }

        // Draw active zaps
        foreach (var zap in _sim.Lightning.Zaps)
        {
            if (!zap.Alive) continue;
            // StartHeight is the caster's casting-anchor height (EffectSpawnHeight),
            // authored against the sprite rig — project with the sprite convention so
            // the zap channels from the hand, not foreshortened to half height.
            var startSp = _renderer.WorldToScreenPx(zap.StartPos, zap.StartHeight * _camera.Zoom, _camera);
            var endSp = _renderer.WorldToScreen(zap.EndPos, zap.EndHeight, _camera);
            float fade = 1f - zap.Timer / zap.Duration;
            AddBoltStrips(startSp, endSp, zap.Style, fade);
        }

        // Draw active beams
        foreach (var beam in _sim.Lightning.Beams)
        {
            if (!beam.Alive) continue;
            int casterIdx = UnitUtil.ResolveUnitIndex(_sim.Units, beam.CasterID);
            int targetIdx = UnitUtil.ResolveUnitIndex(_sim.Units, beam.TargetID);
            if (casterIdx < 0 || targetIdx < 0) continue;

            // Channel origin = the caster's casting anchor (weapon tip). Its height
            // is authored against the sprite rig, so project with the sprite
            // convention (height × Zoom, NO YRatio foreshortening) — exactly like
            // the casting glow (BuffVisualSystem). Plain WorldToScreen foreshortens
            // it to ~half height, channeling the beam from below the hand.
            var startSp = _renderer.WorldToScreenPx(_sim.Units[casterIdx].EffectSpawnPos2D,
                _sim.Units[casterIdx].EffectSpawnHeight * _camera.Zoom, _camera);
            var endSp = _renderer.WorldToScreen(_sim.Units[targetIdx].Position, 1f, _camera);
            AddBoltStrips(startSp, endSp, beam.Style, 1f);
        }

        // Draw active drains
        foreach (var drain in _sim.Lightning.Drains)
        {
            if (!drain.Alive) continue;
            int casterIdx = UnitUtil.ResolveUnitIndex(_sim.Units, drain.CasterID);
            if (casterIdx < 0) continue;

            int targetIdx = UnitUtil.ResolveUnitIndex(_sim.Units, drain.TargetID);
            // Target died mid-channel (no live unit, no corpse anchor): skip drawing rather
            // than falling back to Vec2.Zero, which would draw tendrils to world origin.
            if (targetIdx < 0 && drain.TargetCorpseIdx < 0) continue;
            Vec2 targetPos = drain.TargetCorpseIdx >= 0 ? drain.CorpsePos : Vec2.Zero;
            if (targetIdx >= 0) targetPos = _sim.Units[targetIdx].Position;

            // Same casting-anchor convention as the beam above (sprite height, no
            // YRatio foreshortening) so the drain channels from the caster's hand.
            var startSp = _renderer.WorldToScreenPx(_sim.Units[casterIdx].EffectSpawnPos2D,
                _sim.Units[casterIdx].EffectSpawnHeight * _camera.Zoom, _camera);
            var endSp = _renderer.WorldToScreen(targetPos, 1f, _camera);

            AddDrainTendrilStrips(startSp, endSp, drain.Visuals, drain.Elapsed);
        }
    }

    /// <summary>
    /// Triangle passes with their own device state (bolt/tendril ribbons + god rays).
    /// Must be called AFTER the additive SpriteBatch.End(), while the scene RT is
    /// still bound — additive blending makes the batch/post-batch ordering moot.
    /// </summary>
    public void DrawTriangleEffects()
    {
        _strips.DrawAll();
        _godRayRenderer.DrawAll();
    }
    public GodRayRenderer GetGodRayRenderer() => _godRayRenderer;

    /// <summary>
    /// Shared bolt-shape step (flicker, jitter-quantized reseed, midpoint
    /// displacement, branches) for the in-game ribbon path (AddBoltStrips) and the
    /// SpriteBatch path below. Returns the flicker brightness multiplier.
    /// </summary>
    private static float ComputeBoltShape(Vector2 start, Vector2 end, LightningStyle style,
        float gameTime, out List<Vector2> mainPoints, out List<List<Vector2>> branches)
    {
        // JitterHz: how often the bolt reshapes. 0 = never — the shape seeds off
        // the endpoints alone and stays frozen (matches the buff-aura convention
        // where Hz 0 disables the behavior; want per-frame reshaping? set ~60).
        float jitterTime = 0f;
        if (style.JitterHz > 0.01f)
            jitterTime = MathF.Floor(gameTime * style.JitterHz) / style.JitterHz;

        // FlickerHz: brightness pulsing between FlickerMin and FlickerMax
        float flicker = 1f;
        if (style.FlickerHz > 0.01f && style.FlickerMax > style.FlickerMin)
        {
            float t = MathF.Sin(gameTime * style.FlickerHz * 2f * MathF.PI) * 0.5f + 0.5f;
            flicker = style.FlickerMin + (style.FlickerMax - style.FlickerMin) * t;
        }

        uint seed = (uint)(start.X * 1000 + end.Y * 7 + jitterTime * 60);
        mainPoints = GenerateBoltPoints(start, end, style.Subdivisions, style.Displacement, ref seed);
        branches = mainPoints.Count >= 2
            ? GenerateBranches(mainPoints, style, ref seed)
            : new List<List<Vector2>>();
        return flicker;
    }

    private void AddBoltStrips(Vector2 start, Vector2 end, LightningStyle style, float fade)
        => AddBoltStripsStatic(_strips, start, end, style, fade, _gameTime);

    /// <summary>
    /// Collect a bolt (main + branches) as miter-joined ribbons into a strip batch;
    /// the caller flushes with strips.DrawAll() after its sprite batch ends. THE
    /// single bolt rasterizer — the in-game renderer and SpellPreview both use it,
    /// so the editor preview always matches the real render.
    /// Brightness: contribution = tint.rgb * intensity * fade, with the style
    /// intensity applied via the strip batch's per-bucket Intensity uniform.
    /// </summary>
    public static void AddBoltStripsStatic(HdrStripBatch strips, Vector2 start, Vector2 end,
        LightningStyle style, float fade, float gameTime, float widthScale = 1f)
    {
        float flicker = ComputeBoltShape(start, end, style, gameTime, out var mainPoints, out var branches);
        if (mainPoints.Count < 2) return;
        float ef = fade * flicker;
        if (ef <= 0.001f) return;

        var coreTint = style.CoreColor.ToColor();
        var glowTint = style.GlowColor.ToColor();
        // Clamp to MaxHdrIntensity for parity with the retired sprite path:
        // ToHdrVertex encoded intensity/Max into a byte, so styles authored above
        // the cap (e.g. sky_lightning's 15) have always rendered at the cap.
        var coreVerts = strips.GetBucket(MathF.Min(style.CoreColor.Intensity, HdrColor.MaxHdrIntensity));
        var glowVerts = strips.GetBucket(MathF.Min(style.GlowColor.Intensity, HdrColor.MaxHdrIntensity));

        // Width scales with fade like the old sprite path (the bolt thins as it dies).
        float coreW = style.CoreWidth * widthScale * ef;
        float glowW = style.GlowWidth * widthScale * ef;

        foreach (var branch in branches)
        {
            // Branches taper to a third of their base width and dim toward the tip,
            // so forks visibly thin out as they split off the main channel.
            float bCoreW = coreW * style.BranchDecay;
            float bGlowW = glowW * style.BranchDecay;
            PolylineStrip.Build(glowVerts, branch, glowTint, ef * 0.2f, ef * 0.1f,
                bGlowW, bGlowW * 0.35f, GlowEdgeSoft);
            PolylineStrip.Build(coreVerts, branch, coreTint, ef * 0.7f, ef * 0.35f,
                bCoreW, bCoreW * 0.35f, CoreEdgeSoft);
        }

        PolylineStrip.Build(glowVerts, mainPoints, glowTint, ef * 0.4f, ef * 0.4f,
            glowW, glowW, GlowEdgeSoft);
        PolylineStrip.Build(coreVerts, mainPoints, coreTint, ef, ef,
            coreW, coreW, CoreEdgeSoft);
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

    // Scratch for tendril polylines (render thread only).
    private static readonly List<Vector2> _tendrilPts = new();

    /// <summary>Shared tendril shape (arc + travelling wave) for the ribbon and
    /// sprite paths. Fills outPts; empty when start/end are too close.</summary>
    private static void BuildTendrilPoints(Vector2 start, Vector2 end, float time, List<Vector2> outPts)
    {
        outPts.Clear();
        var dir = end - start;
        float length = dir.Length();
        if (length < 1f) return;
        var norm = dir / length;
        var perp = new Vector2(-norm.Y, norm.X);

        int segments = Math.Max(3, (int)(length / 20f));
        for (int i = 0; i <= segments; i++)
        {
            float t = i / (float)segments;
            var basePos = Vector2.Lerp(start, end, t);
            float arc = MathF.Sin(t * MathF.PI) * 20f;
            float wave = MathF.Sin(time * 4f + t * 8f) * 5f;
            outPts.Add(basePos + perp * (arc + wave));
        }
    }

    private void AddDrainTendrilStrips(Vector2 start, Vector2 end, DrainVisualParams v, float elapsed)
        => AddDrainTendrilStripsStatic(_strips, start, end, v, elapsed);

    /// <summary>
    /// Collect a drain effect (multiple arcing tendrils with sway and pulse) as
    /// ribbons into a strip batch. THE single tendril rasterizer — used by the
    /// in-game renderer and SpellPreview alike.
    /// </summary>
    public static void AddDrainTendrilStripsStatic(HdrStripBatch strips,
        Vector2 start, Vector2 end, DrainVisualParams v, float elapsed)
    {
        float pulse = 1f + v.PulseStrength * MathF.Sin(elapsed * v.PulseHz * 2f * MathF.PI);

        var coreTint = v.CoreColor.ToColor();
        var glowTint = v.GlowColor.ToColor();
        // Same MaxHdrIntensity clamp as AddBoltStripsStatic (sprite-encode parity).
        var coreVerts = strips.GetBucket(MathF.Min(v.CoreColor.Intensity, HdrColor.MaxHdrIntensity));
        var glowVerts = strips.GetBucket(MathF.Min(v.GlowColor.Intensity, HdrColor.MaxHdrIntensity));

        for (int t = 0; t < v.TendrilCount; t++)
        {
            float offset = (t - v.TendrilCount / 2f) * v.SwayAmplitude;
            float sway = MathF.Sin(elapsed * v.SwayHz * 2f * MathF.PI + t * 2f) * v.SwayAmplitude * 0.75f;
            BuildTendrilPoints(new Vector2(start.X + offset, start.Y),
                new Vector2(end.X + sway, end.Y), elapsed, _tendrilPts);
            if (_tendrilPts.Count < 2) continue;

            // Same fade constants as the retired sprite path (glow 120/255, core 200/255).
            PolylineStrip.Build(glowVerts, _tendrilPts, glowTint, 120f / 255f, 120f / 255f,
                v.GlowWidth * pulse, v.GlowWidth * pulse, GlowEdgeSoft);
            PolylineStrip.Build(coreVerts, _tendrilPts, coreTint, 200f / 255f, 200f / 255f,
                v.CoreWidth * pulse, v.CoreWidth * pulse, CoreEdgeSoft);
        }
    }
}
