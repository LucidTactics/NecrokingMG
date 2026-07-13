using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Lib;
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
                    AddBoltStrips(new Vector2(skyX, skyY), sp, strike.Style, fade, strike.Seed);
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
            AddBoltStrips(startSp, endSp, zap.Style, fade, zap.Seed);
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
            AddBoltStrips(startSp, endSp, beam.Style, 1f, beam.Seed);
        }

        // Draw active drains (tendrils only — the cloud puffs are alpha-blended
        // and draw after the ribbons flush, in DrawTriangleEffects).
        foreach (var drain in _sim.Lightning.Drains)
        {
            if (!drain.Alive) continue;
            if (!TryGetDrainEndpoints(drain, out var startSp, out var endSp)) continue;
            AddDrainTendrilStrips(startSp, endSp, drain.Visuals, drain.Elapsed);
        }
    }

    /// <summary>Screen-space endpoints for a drain: caster hand anchor (sprite
    /// height convention, no YRatio foreshortening — same as the beam) and the
    /// target unit/corpse. False when either anchor is gone (target died
    /// mid-channel with no corpse: skip drawing rather than falling back to
    /// Vec2.Zero, which would draw tendrils to world origin).</summary>
    private bool TryGetDrainEndpoints(ActiveDrain drain, out Vector2 startSp, out Vector2 endSp)
    {
        startSp = endSp = default;
        int casterIdx = UnitUtil.ResolveUnitIndex(_sim.Units, drain.CasterID);
        if (casterIdx < 0) return false;

        int targetIdx = UnitUtil.ResolveUnitIndex(_sim.Units, drain.TargetID);
        if (targetIdx < 0 && drain.TargetCorpseIdx < 0) return false;
        Vec2 targetPos = drain.TargetCorpseIdx >= 0 ? drain.CorpsePos : Vec2.Zero;
        if (targetIdx >= 0) targetPos = _sim.Units[targetIdx].Position;

        startSp = _renderer.WorldToScreenPx(_sim.Units[casterIdx].EffectSpawnPos2D,
            _sim.Units[casterIdx].EffectSpawnHeight * _camera.Zoom, _camera);
        endSp = _renderer.WorldToScreen(targetPos, 1f, _camera);
        return true;
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
        DrawDrainClouds();
    }

    /// <summary>Drain cloud puffs, alpha-blended AFTER the tendril ribbons so
    /// they read as opaque clumps riding on the beam (additive puffs just
    /// brighten the beam and disappear into it).</summary>
    private void DrawDrainClouds()
    {
        bool any = false;
        foreach (var d in _sim.Lightning.Drains)
            if (d.Alive && d.Visuals.CloudCount > 0) { any = true; break; }
        if (!any) return;

        Flipbook? cloudFb = null;
        _game._flipbooks?.TryGetValue("cloud03", out cloudFb);

        var mat = Materials.HdrAlpha ?? Materials.Scene;
        mat.Begin(_spriteBatch);
        foreach (var drain in _sim.Lightning.Drains)
        {
            if (!drain.Alive || drain.Visuals.CloudCount <= 0) continue;
            if (!TryGetDrainEndpoints(drain, out var startSp, out var endSp)) continue;
            AddDrainCloudSprites(_spriteBatch, _glowTex, cloudFb, startSp, endSp, drain.Visuals, drain.Elapsed);
        }
        _spriteBatch.End();
    }
    public GodRayRenderer GetGodRayRenderer() => _godRayRenderer;

    /// <summary>
    /// Shared bolt-shape step (flicker, jitter-quantized reseed, midpoint
    /// displacement, branches) for the in-game ribbon path (AddBoltStrips) and the
    /// SpriteBatch path below. Returns the flicker brightness multiplier.
    /// </summary>
    private static float ComputeBoltShape(Vector2 start, Vector2 end, LightningStyle style,
        float gameTime, out List<Vector2> mainPoints, out List<List<Vector2>> branches,
        uint seedSalt = 0)
    {
        // JitterHz: how often the bolt reshapes. 0 = never — the shape stays
        // frozen (matches the buff-aura convention where Hz 0 disables the
        // behavior; want per-frame reshaping? set ~60).
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

        // With a per-instance salt the seed changes ONLY on the JitterHz clock —
        // moving endpoints just deform the current shape (displacements are
        // relative to the segment). The endpoint-derived fallback (salt 0, used by
        // static previews) re-rolls whenever an endpoint moves: fine for a
        // stationary preview, visibly frantic on a beam tracking a walking target.
        uint seed = seedSalt != 0
            ? seedSalt * 2654435761u + (uint)(jitterTime * 60f)
            : (uint)(start.X * 1000 + end.Y * 7 + jitterTime * 60);
        mainPoints = GenerateBoltPoints(start, end, style.Subdivisions, style.Displacement, ref seed);
        branches = mainPoints.Count >= 2
            ? GenerateBranches(mainPoints, style, ref seed)
            : new List<List<Vector2>>();
        return flicker;
    }

    private void AddBoltStrips(Vector2 start, Vector2 end, LightningStyle style, float fade,
        uint seedSalt = 0)
        => AddBoltStripsStatic(_strips, start, end, style, fade, _gameTime, seedSalt: seedSalt);

    /// <summary>
    /// Collect a bolt (main + branches) as miter-joined ribbons into a strip batch;
    /// the caller flushes with strips.DrawAll() after its sprite batch ends. THE
    /// single bolt rasterizer — the in-game renderer and SpellPreview both use it,
    /// so the editor preview always matches the real render.
    /// Brightness: contribution = tint.rgb * intensity * fade, with the style
    /// intensity applied via the strip batch's per-bucket Intensity uniform.
    /// </summary>
    public static void AddBoltStripsStatic(HdrStripBatch strips, Vector2 start, Vector2 end,
        LightningStyle style, float fade, float gameTime, float widthScale = 1f,
        uint seedSalt = 0)
    {
        float flicker = ComputeBoltShape(start, end, style, gameTime, out var mainPoints, out var branches, seedSalt);
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

        // Style.WidthFade controls how much the bolt's width follows the fade:
        // 1 = full coupling (width*ef, the classic collapse-to-a-thread; also the
        // pre-knob behavior), 0 = constant width with only brightness fading.
        float wf = MathHelper.Lerp(1f, ef, Math.Clamp(style.WidthFade, 0f, 1f));
        float coreW = style.CoreWidth * widthScale * wf;
        float glowW = style.GlowWidth * widthScale * wf;

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

    /// <summary>Perpendicular displacement of the tendril path at parameter t
    /// (arc + travelling wave). Shared by the ribbon rasterizer and the cloud
    /// sprites so the puffs ride exactly on the beam.</summary>
    private static float TendrilLateral(float t, float time, float arcHeight)
        => MathF.Sin(t * MathF.PI) * arcHeight + MathF.Sin(time * 4f + t * 8f) * 5f;

    /// <summary>Shared tendril shape (arc + travelling wave) for the ribbon and
    /// sprite paths. Fills outPts; empty when start/end are too close.</summary>
    private static void BuildTendrilPoints(Vector2 start, Vector2 end, float time,
        float arcHeight, List<Vector2> outPts)
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
            outPts.Add(basePos + perp * TendrilLateral(t, time, arcHeight));
        }
    }

    private void AddDrainTendrilStrips(Vector2 start, Vector2 end, DrainVisualParams v, float elapsed)
        => AddDrainTendrilStripsStatic(_strips, start, end, v, elapsed);

    /// <summary>
    /// Collect a drain effect (multiple arcing tendrils with sway and pulse) as
    /// ribbons into a strip batch. THE single tendril rasterizer — used by the
    /// in-game renderer and SpellPreview alike. start = caster anchor, end =
    /// target; flow direction and the wide/narrow ends come from v.FlowReversed
    /// and v.SourceWidthScale (Pugna funnel: narrow at the destination, wide at
    /// the life-source end, tendrils fanning out toward the source).
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

        // Source end of the flow: target (end) on a normal drain. Widths scale
        // up and the tendril fan/sway anchors there; the destination end stays
        // narrow and fixed at its anchor.
        float srcScale = MathF.Max(0.1f, v.SourceWidthScale);
        float wStartScale = v.FlowReversed ? srcScale : 1f;
        float wEndScale = v.FlowReversed ? 1f : srcScale;

        for (int t = 0; t < v.TendrilCount; t++)
        {
            float offset = (t - v.TendrilCount / 2f) * v.SwayAmplitude;
            float sway = MathF.Sin(elapsed * v.SwayHz * 2f * MathF.PI + t * 2f) * v.SwayAmplitude * 0.75f;
            var s = start;
            var e = end;
            if (v.FlowReversed) s.X += offset + sway;
            else e.X += offset + sway;
            BuildTendrilPoints(s, e, elapsed, v.ArcHeight, _tendrilPts);
            if (_tendrilPts.Count < 2) continue;

            // Same fade constants as the retired sprite path (glow 120/255, core 200/255).
            PolylineStrip.Build(glowVerts, _tendrilPts, glowTint, 120f / 255f, 120f / 255f,
                v.GlowWidth * pulse * wStartScale, v.GlowWidth * pulse * wEndScale, GlowEdgeSoft);
            PolylineStrip.Build(coreVerts, _tendrilPts, coreTint, 200f / 255f, 200f / 255f,
                v.CoreWidth * pulse * wStartScale, v.CoreWidth * pulse * wEndScale, CoreEdgeSoft);
        }
    }

    /// <summary>
    /// Cloud puffs riding the drain beam from the flow-source end toward the
    /// destination (Pugna-style). Must be called while an ALPHA-blended HDR
    /// sprite batch is open (Materials.HdrAlpha / AlphaMode=1 — colors encoded
    /// via ToHdrVertexAlpha), after the tendril ribbons have flushed, so the
    /// puffs occlude the beam instead of dissolving into it additively. Shared
    /// by the in-game renderer and SpellPreview; start = caster anchor, end =
    /// target, same as AddDrainTendrilStripsStatic.
    /// </summary>
    public static void AddDrainCloudSprites(SpriteBatch sb, Texture2D glowTex, Flipbook? cloudFb,
        Vector2 start, Vector2 end, DrainVisualParams v, float elapsed)
    {
        if (v.CloudCount <= 0 || v.CloudSize <= 0f) return;
        var dir = end - start;
        float length = dir.Length();
        if (length < 1f) return;
        var norm = dir / length;
        var perp = new Vector2(-norm.Y, norm.X);

        var tint = v.CloudColor.ToColor();
        float texHalf = glowTex.Width * 0.5f;
        var glowOrigin = new Vector2(texHalf, texHalf);
        bool useFb = cloudFb != null && cloudFb.IsLoaded && cloudFb.Texture != null;

        for (int i = 0; i < v.CloudCount; i++)
        {
            // Deterministic per-cloud variation (same LCG as the bolt shape code).
            uint h = (uint)(i + 1) * 2654435761u;
            h = h * 1103515245u + 12345u; float rPhase = (h % 1000) / 1000f;
            h = h * 1103515245u + 12345u; float rSize = (h % 1000) / 1000f;
            h = h * 1103515245u + 12345u; float rLat = (h % 1000) / 500f - 1f;

            // Progress 0→1 from source to destination: evenly staggered, with a
            // little random phase so the stream doesn't read as a marching column.
            float p = (elapsed * v.CloudSpeed + i / (float)v.CloudCount + rPhase * 0.35f) % 1f;
            // Geometric t along start→end (start = caster = destination on a normal drain).
            float t = v.FlowReversed ? p : 1f - p;

            float lateral = TendrilLateral(t, elapsed, v.ArcHeight) + rLat * v.CloudSize * 0.8f;
            var pos = Vector2.Lerp(start, end, t) + perp * lateral;

            // Fade in/out near the ends; shrink as the puff nears the narrow end.
            float fade = Math.Clamp(MathF.Min(p, 1f - p) * 6f, 0f, 1f);
            if (fade <= 0.01f) continue;
            float size = v.CloudSize * (0.75f + rSize * 0.5f) * MathHelper.Lerp(1.1f, 0.65f, p);

            if (useFb)
            {
                // Real cloud art (the death-fog sheet): a lumpy silhouette reads
                // as an opaque puff where a radial glow just reads as blur. Each
                // puff holds a stable random frame, slowly cycling.
                int frame = cloudFb!.GetFrameAtNormalizedTime((elapsed / 4f + rPhase) % 1f);
                var src = cloudFb.GetFrameRect(frame);
                var org = new Vector2(src.Width / 2f, src.Height / 2f);
                float scl = size * 2f / Math.Max(src.Width, 1);
                var flip = rLat < 0f ? SpriteEffects.FlipHorizontally : SpriteEffects.None;
                // Stacked twice: the cloud art's own alpha is soft, and a single
                // layer reads as haze over the bright beam — two layers give the
                // dense "opaque puff" body.
                var puffColor = HdrColor.ToHdrVertexAlpha(tint, fade, v.CloudColor.Intensity);
                sb.Draw(cloudFb.Texture, pos, src, puffColor, 0f, org, scl, flip, 0f);
                sb.Draw(cloudFb.Texture, pos, src, puffColor, 0f, org, scl * 0.85f, flip, 0f);
            }
            else
            {
                // Fallback (no flipbook in this context): soft outer puff + a
                // dense near-opaque core from the radial glow texture.
                sb.Draw(glowTex, pos, null,
                    HdrColor.ToHdrVertexAlpha(tint, fade * 0.55f, v.CloudColor.Intensity),
                    0f, glowOrigin, size / texHalf, SpriteEffects.None, 0f);
                sb.Draw(glowTex, pos, null,
                    HdrColor.ToHdrVertexAlpha(tint, fade,
                        MathF.Min(v.CloudColor.Intensity * 1.25f, HdrColor.MaxHdrIntensity)),
                    0f, glowOrigin, size * 0.5f / texHalf, SpriteEffects.None, 0f);
            }
        }
    }
}
