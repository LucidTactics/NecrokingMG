using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Core;
using Necroking.Data.Registries;
using Necroking.Lib;
using XnaEffect = Microsoft.Xna.Framework.Graphics.Effect;

namespace Necroking.Render;

/// <summary>
/// World-space light-scatter halos ("inbuilt bloom"): effects register point /
/// polyline emitters each frame and this system draws soft additive halos
/// around them INSIDE the HDR scene RT, pre-bloom — modulated by one shared,
/// world-anchored scrolling mist field (ScatterGlow.fx) and a global density
/// scalar driven by the weather's fog density. Screen-space bloom models the
/// EYE; this models the AIR around a light source.
///
/// Deliberate design constraints:
/// - IMMEDIATE MODE: emitters are registered during draw collection and cleared
///   every frame. No lifecycle, no registry to leak — a dead fireball simply
///   stops calling AddPoint. Register per EFFECT, not per particle (the air
///   low-pass-filters the source; N overlapping halos ≈ 1 bigger halo at N× cost).
/// - TOGGLEABLE: Performance.ScatterGlow off (or the shader failing to load)
///   turns every Add* into an early-out — near-zero cost when disabled.
/// - REMOVABLE: the feature is this file + resources/ScatterGlow.fx. Touch
///   points elsewhere: one pipeline pass + one gate condition
///   (GameRenderer.Pipeline.cs), two PerformanceSettings fields, the SCATTER
///   SpellDef field group, the "TestShape" SpellEffectSystem case, one Game1
///   field + load block, and the per-effect Add* call sites.
/// - ZOOM: emitter radius, mist feature size, and wind speed are WORLD units
///   (realism model, docs/vfx-zoom-audit.md — no px floors, no hybrid curves).
///   Halos draw screen-circular (a volumetric ball of lit air, not a ground decal).
/// - BLOOM INDEPENDENCE: halo output is kept below the bloom-extract knee
///   (threshold 0.8) so it adds scene light directly rather than being
///   re-amplified by the eye layer.
///
/// Occlusion: quads carry GameRenderer.FogDepthForY depth and draw with
/// DepthRead, so the unit silhouettes stamped by the FogDepthOccluders pass
/// clip halos per-pixel (same mechanism as the reanim fog / ground-fog wisps).
/// </summary>
public sealed class ScatterGlowSystem
{
    private Game1 _g = null!;
    private XnaEffect? _effect;

    // ─── Emitters (cleared every frame) ───
    // Stored in SCREEN space: registration happens during draw collection (the
    // camera is final for the frame), and beam paths only exist as screen-space
    // polylines. Radius converts world→px at Add time; depth is FogDepthForY.
    private struct Halo
    {
        public Vector2 ScreenPos;
        public float RadiusPx;
        public Color Rgb;        // straight color, A ignored
        public float Strength;   // 0..1, folded into vertex alpha
        public float Depth;      // FogDepthForY value for the DepthRead test
    }
    private readonly List<Halo> _halos = new(64);

    // Safety valve: emitters past the cap are dropped, never accumulated — a
    // mistaken per-particle registration can't melt the frame. Surfaced by the
    // 'scatterglow' dev command.
    private const int MaxHalosPerFrame = 512;
    private int _droppedThisFrame;

    // ─── Test shapes (the ScatterGlow review spells; category "TestShape") ───
    private struct TestShape
    {
        public bool IsCircle;
        public Vec2 A, B;        // line endpoints; circle uses A = center
        public float TtlLeft;
        public SpellDef Spell;   // live ref: F10 editor tweaks apply to shapes already in flight
    }
    private readonly List<TestShape> _shapes = new(4);
    private const float TestShapeTtl = 8f;
    private const float TestShapeFadeSecs = 0.6f;
    private const float TestLineWorldWidth = 0.14f;   // "thin bright rectangle"
    private const float TestCircleWorldRadius = 1.0f;
    private static readonly Color TestOrange = new(255, 150, 50);
    private const float TestEmissionIntensity = 4.0f; // HDR multiplier for the Solid technique

    // ─── Draw scratch ───
    private readonly List<VertexPositionColorTexture> _haloVerts = new(256);
    private readonly List<VertexPositionColorTexture> _solidVerts = new(16);
    private VertexPositionColorTexture[] _flushScratch = new VertexPositionColorTexture[256];
    private float _time;

    // One/One additive: rgb accumulates into the HDR RT, alpha out is 0 so the
    // RT's alpha channel stays untouched. (BlendState.Additive is SrcAlpha/One —
    // the shader already folds strength/fade into rgb, so alpha must not double-apply.)
    private static readonly BlendState AddOneOne = new()
    {
        ColorSourceBlend = Blend.One,
        ColorDestinationBlend = Blend.One,
        AlphaSourceBlend = Blend.One,
        AlphaDestinationBlend = Blend.One,
    };

    public void Init(Game1 game, XnaEffect? effect)
    {
        _g = game;
        _effect = effect;
    }

    /// <summary>Feature is on and drawable this frame.</summary>
    public bool Active => _effect != null && _g._gameData.Settings.Performance.ScatterGlow;

    /// <summary>True when the FogDepthOccluders pass should stamp unit depth for
    /// us. Uses LAST frame's emitter presence — the stamp pass runs before this
    /// frame's registration; a one-frame stamp lag on the first glowing frame is
    /// invisible.</summary>
    public bool NeedsDepthStamps => Active && _hadEmittersLastFrame;
    private bool _hadEmittersLastFrame;

    public int LastHaloCount { get; private set; }
    public int LastDroppedCount { get; private set; }

    // Mist shaping — live-tunable via `scatterglow mist <gamma> <floor> [knee]`
    // while values are being dialed in; bake into a texture once approved.
    public float MistGamma = 0.45f;
    public float MistFloor = 0.38f;
    public float MistKnee = 0.3f;

    // ─────────────────────────── Emitter API ───────────────────────────

    /// <summary>Register a point light-scatter emitter for this frame. One call
    /// per EFFECT (torch, fireball, sigil), not per particle. Radius in world
    /// units; strength 0..1; height = physical lift (same units as WorldToScreen).</summary>
    public void AddPoint(Vec2 worldPos, float radius, Color rgb, float strength = 1f, float height = 0f)
    {
        if (!Active || radius <= 0f || strength <= 0f) return;
        var cam = _g._camera;
        AddScreenHalo(_g._renderer.WorldToScreen(worldPos, height, cam),
            radius * cam.Zoom, rgb, strength,
            GameRenderer.FogDepthForY(worldPos.Y, cam.Position.Y));
    }

    private void AddScreenHalo(Vector2 screenPos, float radiusPx, Color rgb, float strength, float depth)
    {
        if (_halos.Count >= MaxHalosPerFrame) { _droppedThisFrame++; return; }
        _halos.Add(new Halo
        {
            ScreenPos = screenPos, RadiusPx = radiusPx, Rgb = rgb,
            Strength = MathHelper.Clamp(strength, 0f, 1f), Depth = depth,
        });
    }

    /// <summary>Register a point emitter from a SpellDef's SCATTER fields — the
    /// single SpellDef→emitter conversion point (no-op when scatterRadius is 0).</summary>
    public void AddSpellPoint(SpellDef spell, Vec2 pos, float strengthScale = 1f, float height = 0f)
        => AddPoint(pos, spell.ScatterRadius,
            new Color(spell.ScatterColor.R, spell.ScatterColor.G, spell.ScatterColor.B),
            spell.ScatterStrength * strengthScale, height);

    /// <summary>Register a polyline emitter (beams): splats point halos along the
    /// path. Splat spacing is radius-relative, so brightness per world unit stays
    /// constant; strength is pre-dimmed for the additive overlap of neighbors.</summary>
    public void AddPolyline(IReadOnlyList<Vec2> worldPts, float radius, Color rgb, float strength = 1f, float height = 0f)
    {
        if (!Active || radius <= 0f || strength <= 0f || worldPts.Count < 2) return;
        float spacing = radius * 0.55f;
        // Dense overlap so the summed ridge reads as one continuous tube (0.75
        // spacing beaded visibly at zoom 32); ~0.42 keeps the ridge near a
        // single halo's peak brightness.
        float splatStrength = strength * 0.42f;
        float carry = 0f;
        for (int i = 1; i < worldPts.Count; i++)
        {
            Vec2 a = worldPts[i - 1], b = worldPts[i];
            float segLen = (b - a).Length();
            if (segLen <= 1e-4f) continue;
            float t = carry;
            while (t < segLen)
            {
                AddPoint(a + (b - a) * (t / segLen), radius, rgb, splatStrength, height);
                t += spacing;
            }
            carry = t - segLen;
        }
        AddPoint(worldPts[^1], radius, rgb, splatStrength, height);
    }

    /// <summary>Register a polyline emitter from a SCREEN-space path (lightning
    /// bolts — their jittered shape only exists in screen px). Radius stays a
    /// WORLD unit; splat depth lerps between the endpoints' world Ys so the
    /// sheath depth-sorts correctly along its length.</summary>
    public void AddPolylineScreen(IReadOnlyList<Vector2> screenPts, float worldRadius,
        Color rgb, float strength, float worldYStart, float worldYEnd)
    {
        if (!Active || worldRadius <= 0f || strength <= 0f || screenPts.Count < 2) return;
        var cam = _g._camera;
        float radiusPx = worldRadius * cam.Zoom;
        float spacingPx = radiusPx * 0.55f;
        float splatStrength = strength * 0.42f;   // same overlap dimming as AddPolyline
        float camY = cam.Position.Y;

        // Total arc length first, so each splat can lerp its depth by arc fraction.
        float totalLen = 0f;
        for (int i = 1; i < screenPts.Count; i++)
            totalLen += (screenPts[i] - screenPts[i - 1]).Length();
        if (totalLen <= 1e-3f) return;

        float arc = 0f, carry = 0f;
        for (int i = 1; i < screenPts.Count; i++)
        {
            Vector2 a = screenPts[i - 1], b = screenPts[i];
            float segLen = (b - a).Length();
            if (segLen <= 1e-3f) continue;
            float t = carry;
            while (t < segLen)
            {
                float frac = (arc + t) / totalLen;
                float depth = GameRenderer.FogDepthForY(
                    MathHelper.Lerp(worldYStart, worldYEnd, frac), camY);
                AddScreenHalo(a + (b - a) * (t / segLen), radiusPx, rgb, splatStrength, depth);
                t += spacingPx;
            }
            carry = t - segLen;
            arc += segLen;
        }
    }

    // ─────────────────────────── Test shapes ───────────────────────────

    /// <summary>Spawn a review primitive from a "TestShape" spell cast: a thin
    /// bright orange line from the caster's hand to the target, or a disc at the
    /// target (spell id containing "circle"). Emission is hardcoded; the scatter
    /// halo reads the spell's live SCATTER fields so the F10 editor tunes it.</summary>
    public void SpawnTestShape(SpellDef spell, Vec2 origin, Vec2 target)
    {
        bool circle = spell.Id.Contains("circle", StringComparison.OrdinalIgnoreCase);
        _shapes.Add(new TestShape
        {
            IsCircle = circle,
            A = circle ? target : origin,
            B = target,
            TtlLeft = TestShapeTtl,
            Spell = spell,
        });
    }

    // ─────────────────────────── Draw ───────────────────────────

    /// <summary>Pipeline hook (CustomPass "ScatterGlow"): runs after the HDR
    /// effects queue and LightningTris (so every effect has registered), while
    /// the scene RT + its depth buffer are still bound, before bloom extract.
    /// Manages its own device state like HdrStripBatch.DrawAll.</summary>
    public void Draw(RenderContext ctx)
    {
        float dt = (float)ctx.GameTime.ElapsedGameTime.TotalSeconds;
        _time = (_time + dt) % 3600f;

        // Age test shapes even when disabled so they still expire.
        for (int i = _shapes.Count - 1; i >= 0; i--)
        {
            var s = _shapes[i];
            s.TtlLeft -= dt;
            if (s.TtlLeft <= 0f) { _shapes.RemoveAt(i); continue; }
            _shapes[i] = s;
        }

        if (!Active)
        {
            _halos.Clear();
            _hadEmittersLastFrame = false;
            LastHaloCount = 0;
            return;
        }

        // Test shapes register their scatter halos here (self-contained — no
        // external call site needed for the review spells).
        for (int i = 0; i < _shapes.Count; i++)
            RegisterShapeHalos(_shapes[i]);

        _hadEmittersLastFrame = _halos.Count > 0 || _shapes.Count > 0;
        LastHaloCount = _halos.Count;
        LastDroppedCount = _droppedThisFrame;
        _droppedThisFrame = 0;

        if (_halos.Count == 0 && _shapes.Count == 0) return;

        var cam = _g._camera;
        float camY = cam.Position.Y;
        _haloVerts.Clear();
        _solidVerts.Clear();

        for (int i = 0; i < _shapes.Count; i++)
            BuildShapeEmission(_shapes[i], ctx);

        for (int i = 0; i < _halos.Count; i++)
        {
            var h = _halos[i];
            float rPx = h.RadiusPx;
            var sp = h.ScreenPos;
            if (sp.X < -rPx || sp.X > ctx.ScreenW + rPx || sp.Y < -rPx || sp.Y > ctx.ScreenH + rPx)
                continue;
            var col = new Color(h.Rgb.R, h.Rgb.G, h.Rgb.B,
                (byte)Math.Clamp((int)(h.Strength * 255f + 0.5f), 0, 255));
            AddQuad(_haloVerts, sp, rPx, rPx, 0f, h.Depth, col, uvExtent: 1f);
        }
        _halos.Clear();

        if (_haloVerts.Count == 0 && _solidVerts.Count == 0) return;

        var device = ctx.Device;
        var vp = device.Viewport;
        // Same construction as MonoGame's SpriteEffect so vertex z compares
        // against the SpriteBatch-stamped unit depth silhouettes.
        var wvp = Matrix.CreateOrthographicOffCenter(0, vp.Width, vp.Height, 0, 0, -1);

        device.BlendState = AddOneOne;
        device.DepthStencilState = DepthStencilState.DepthRead;
        device.RasterizerState = RasterizerState.CullNone;

        // MGFX on GL zeroes uniforms — set every one, every frame.
        float fogDensity = _g._weatherRenderer.GetEffectiveEffects()?.FogDensity ?? 0f;
        float density = _g._gameData.Settings.Performance.ScatterGlowStrength
            * (0.7f + 0.9f * MathHelper.Clamp(fogDensity, 0f, 1f));
        _effect!.Parameters["WorldViewProjection"]?.SetValue(wvp);
        _effect.Parameters["Time"]?.SetValue(_time);
        _effect.Parameters["Density"]?.SetValue(density);
        _effect.Parameters["MistStrength"]?.SetValue(0.75f);
        _effect.Parameters["MistGamma"]?.SetValue(MistGamma);
        _effect.Parameters["MistFloor"]?.SetValue(MistFloor);
        _effect.Parameters["MistKnee"]?.SetValue(MistKnee);
        _effect.Parameters["WorldOrigin"]?.SetValue(
            new Vector2(cam.Position.X - vp.Width * 0.5f / cam.Zoom,
                        cam.Position.Y - vp.Height * 0.5f / (cam.Zoom * cam.YRatio)));
        _effect.Parameters["WorldPerPixel"]?.SetValue(
            new Vector2(1f / cam.Zoom, 1f / (cam.Zoom * cam.YRatio)));
        _effect.Parameters["SolidIntensity"]?.SetValue(TestEmissionIntensity);

        FlushList(device, _solidVerts, "ScatterSolid");
        FlushList(device, _haloVerts, "ScatterHalo");
    }

    private void FlushList(GraphicsDevice device, List<VertexPositionColorTexture> verts, string technique)
    {
        int count = verts.Count - verts.Count % 3;
        if (count < 3) return;
        var tech = _effect!.Techniques[technique];
        if (tech == null) return;
        _effect.CurrentTechnique = tech;
        if (_flushScratch.Length < count)
            _flushScratch = new VertexPositionColorTexture[Math.Max(count, _flushScratch.Length * 2)];
        verts.CopyTo(0, _flushScratch, 0, count);
        foreach (var pass in _effect.CurrentTechnique.Passes)
        {
            pass.Apply();
            device.DrawUserPrimitives(PrimitiveType.TriangleList, _flushScratch, 0, count / 3);
        }
    }

    private void RegisterShapeHalos(in TestShape s)
    {
        var spell = s.Spell;
        if (spell.ScatterRadius <= 0f) return;
        float fade = MathHelper.Clamp(s.TtlLeft / TestShapeFadeSecs, 0f, 1f);
        var rgb = new Color(spell.ScatterColor.R, spell.ScatterColor.G, spell.ScatterColor.B);
        float strength = spell.ScatterStrength * fade;
        if (s.IsCircle)
        {
            AddPoint(s.A, spell.ScatterRadius, rgb, strength);
        }
        else
        {
            _polyScratch2[0] = s.A;
            _polyScratch2[1] = s.B;
            AddPolyline(_polyScratch2, spell.ScatterRadius, rgb, strength);
        }
    }
    private readonly Vec2[] _polyScratch2 = new Vec2[2];

    private void BuildShapeEmission(in TestShape s, RenderContext ctx)
    {
        var cam = _g._camera;
        float fade = MathHelper.Clamp(s.TtlLeft / TestShapeFadeSecs, 0f, 1f);
        var col = new Color(TestOrange.R, TestOrange.G, TestOrange.B,
            (byte)Math.Clamp((int)(fade * 255f + 0.5f), 0, 255));
        float z = GameRenderer.FogDepthForY(s.A.Y, cam.Position.Y);

        if (s.IsCircle)
        {
            var sp = _g._renderer.WorldToScreen(s.A, 0f, cam);
            AddQuad(_solidVerts, sp, TestCircleWorldRadius * cam.Zoom,
                TestCircleWorldRadius * cam.Zoom, 0f, z, col, uvExtent: 1f);
        }
        else
        {
            var spA = _g._renderer.WorldToScreen(s.A, 0f, cam);
            var spB = _g._renderer.WorldToScreen(s.B, 0f, cam);
            var mid = (spA + spB) * 0.5f;
            var d = spB - spA;
            float len = d.Length();
            if (len < 1e-3f) return;
            float rot = MathF.Atan2(d.Y, d.X);
            // uvExtent 0 → TexCoord all zero → the Solid PS keeps a hard fill.
            AddQuad(_solidVerts, mid, len * 0.5f, TestLineWorldWidth * cam.Zoom * 0.5f,
                rot, z, col, uvExtent: 0f);
        }
    }

    /// <summary>Append a rotated quad (2 tris) centered at <paramref name="center"/>
    /// (screen px), half-extents hx/hy, depth z. TexCoords span ±uvExtent.</summary>
    private static void AddQuad(List<VertexPositionColorTexture> verts, Vector2 center,
        float hx, float hy, float rot, float z, Color col, float uvExtent)
    {
        float c = MathF.Cos(rot), s = MathF.Sin(rot);
        var ax = new Vector2(c, s) * hx;    // local +x axis, scaled
        var ay = new Vector2(-s, c) * hy;   // local +y axis, scaled
        var p00 = center - ax - ay;
        var p10 = center + ax - ay;
        var p11 = center + ax + ay;
        var p01 = center - ax + ay;
        float u = uvExtent;
        var v00 = new VertexPositionColorTexture(new Vector3(p00, z), col, new Vector2(-u, -u));
        var v10 = new VertexPositionColorTexture(new Vector3(p10, z), col, new Vector2(u, -u));
        var v11 = new VertexPositionColorTexture(new Vector3(p11, z), col, new Vector2(u, u));
        var v01 = new VertexPositionColorTexture(new Vector3(p01, z), col, new Vector2(-u, u));
        verts.Add(v00); verts.Add(v10); verts.Add(v11);
        verts.Add(v00); verts.Add(v11); verts.Add(v01);
    }
}
