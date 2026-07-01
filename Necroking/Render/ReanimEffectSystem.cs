using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Core;

namespace Necroking.Render;

/// <summary>
/// Composite "rise from the grave" reanimation effect, played for ~3s while a
/// corpse stands up into an undead unit. Four layers, all driven by a single
/// <see cref="ReanimConfig"/> so the 5 test variants are just parameter sets:
///   1. a blinking undead-green OUTLINE on the unit that fades out (rendered via the
///      unit draw's outline hook — see Game1.Render.Units.DrawReanimOutline);
///   2. an additive green diffuse LIGHT behind/around the unit (alpha ramps 0→1→0);
///   3. additive green CLOUD puffs (Cloud03_8x8) that spawn at the ground and rise;
///   4. opaque dark DUST puffs that spawn at the ground and rise, Y-sorted with units.
///
/// Layers 2+3 render in the additive HDR pass (<see cref="DrawAdditive"/>); layer 4
/// is contributed to the merged Y-sort depth list (<see cref="AddDustToDepthList"/> /
/// <see cref="DrawSingleDust"/>), mirroring <see cref="DeathFogRenderer"/>.
/// </summary>
internal class ReanimEffectSystem
{
    /// <summary>One variant's parameters. Colors are HdrColor (RGB + intensity for bloom).</summary>
    public struct ReanimConfig
    {
        public string Id;
        // Per-layer timings, decoupled so each layer can linger independently.
        public float OutlineDuration;   // outline blinks then fades 1->0 over this
        public float LightDuration;     // light alpha curve plays over this
        public float SpawnWindow;       // how long new cloud/dust puffs keep appearing
        public float PuffAnimCycles;    // flipbook loops over a puff's lifetime (1=default speed, >1=faster billow)

        // Outline (blinks via pulse). Suppressed during the corpse morph, then on the risen unit
        // it fades IN over OutlineFadeInDur and fades OUT over the rest of OutlineDuration.
        public HdrColor OutlineColor;
        public HdrColor OutlinePulseColor;
        public float OutlineWidth;
        public float OutlinePulseWidth;
        public float OutlinePulseSpeed;
        public float OutlineFadeInDur;  // effect-time to bloom the outline in on the risen unit (0 = instant/full)

        // Additive diffuse light behind the unit
        public HdrColor LightColor;
        public float LightWorldSize;    // world-unit diameter of the glow
        public BezierCurve LightAlpha;  // ramps 0→1→0 across the effect

        // Additive green cloud puffs
        public HdrColor CloudColor;
        public float CloudWorldSize;
        public int CloudCount;          // puffs spawned across the rise
        public float CloudRise;         // world height gained over a puff's life
        public float CloudLifetime;

        // Opaque dark dust puffs (Y-sorted)
        public HdrColor DustColor;
        public float DustWorldSize;
        public int DustCount;
        public float DustRise;
        public float DustLifetime;
        public float DustMaxAlpha;      // opacity ceiling for the dust layer (0 or unset = uncapped)
    }

    private struct Puff
    {
        public Vec2 Ground;     // ground world position (with scatter)
        public float Delay;     // seconds before it starts
        public float Age;       // seconds since started (-Delay while waiting)
        public float Lifetime;
        public float Rise;      // world height gained over life
        public float WorldSize;
        public HdrColor Color;
        public float FramePhase;
        public float RotSpeed;
        public BezierCurve Alpha;
        public float MaxAlpha;  // opacity ceiling; the evaluated alpha never exceeds this (1 = uncapped)
    }

    private class Instance
    {
        public int InstanceId;          // stable handle so a deferred unit spawn can attach its outline
        public uint UnitId;
        public Vec2 Ground;
        public float Scale;
        // Two decoupled clocks: Age drives the RISE layers (outline + pose-morph build-up +
        // light) at RiseSpeed so they stay in step with the body getting up; FogAge drives
        // the SMOKE puffs at FogSpeed so the cloud can linger (or rush) independently.
        public float Age;
        public float FogAge;
        public float OutlineStartAge;   // outline FADE-OUT clock starts here (set when the unit attaches)
        public float OutlineFadeIn;     // corpse-phase outline FADE-IN window (0->1 over this); 0 = none
        public float MorphHold;         // hold the death pose this long (clouds build) before the morph
        public bool HasUnit;            // false while only the corpse is present (outline fades in on it)
        public float RiseLife;          // removed once Age >= RiseLife (outline/light/build-up done)...
        public float FogLife;           // ...AND FogAge >= FogLife (last puff faded)
        public float RiseSpeed = 1f;    // rise-clock multiplier (standup/outline/morph)
        public float FogSpeed = 1f;     // fog-clock multiplier (cloud + dust puffs)
        public ReanimConfig Cfg;
        public readonly List<Puff> Clouds = new();
        public readonly List<Puff> Dust = new();
    }

    // Fade-in/out curves shared by the cloud + dust puffs (rise then fall).
    private static readonly BezierCurve PuffAlpha = new(0f, 1f, 0.9f, 0f);

    private readonly List<Instance> _active = new();
    private int _nextInstanceId = 1;   // monotonic; 0 is reserved as "no handle"
    private readonly Dictionary<string, ReanimConfig> _configs = new();
    private readonly Random _rng = new(1234);

    // Render context (set each frame, like DeathFogRenderer)
    private SpriteBatch? _batch;
    private Camera25D? _camera;
    private Renderer? _renderer;
    private Flipbook? _cloud;
    private Texture2D? _glow;

    public ReanimEffectSystem()
    {
        foreach (var c in BuildPresets())
            _configs[c.Id] = c;
    }

    public bool HasConfig(string id) => _configs.ContainsKey(id);

    /// <summary>Begin a reanimation effect at a ground spot. configId selects the variant;
    /// falls back to "reanim_classic" if unknown/empty. Returns a stable instance id; pass
    /// <see cref="GameConstants.InvalidUnit"/> for unitId when the unit hasn't spawned yet
    /// and call <see cref="SetUnitId"/> once it does, so the outline attaches on the rise.</summary>
    public int Begin(uint unitId, Vec2 ground, float scale, string? configId,
        float outlineFadeIn = 0f, float morphHold = 0f, float riseSpeed = 1f, float fogSpeed = 1f)
    {
        if (!_configs.TryGetValue(configId ?? "", out var cfg))
            cfg = _configs.TryGetValue("reanim_smoke", out var def) ? def : default;
        // Rise layers (outline build-up/fade, light) live on the Age clock; the smoke puffs
        // live on the FogAge clock. The instance is removed only once BOTH have finished. All
        // durations are in effect-time; each clock advances at its own speed, so riseSpeed and
        // fogSpeed shorten their halves of the effect independently.
        float riseLife = MathF.Max(outlineFadeIn, MathF.Max(cfg.OutlineDuration, cfg.LightDuration));
        bool hasPuffs = cfg.CloudCount > 0 || cfg.DustCount > 0;
        float fogLife = hasPuffs ? cfg.SpawnWindow + MathF.Max(cfg.CloudLifetime, cfg.DustLifetime) : 0f;
        if (riseLife <= 0f && fogLife <= 0f) return 0;

        int id = _nextInstanceId++;
        var inst = new Instance { InstanceId = id, UnitId = unitId, Ground = ground, Scale = scale,
            Cfg = cfg, RiseLife = riseLife, FogLife = fogLife,
            RiseSpeed = MathF.Max(0.05f, riseSpeed), FogSpeed = MathF.Max(0.05f, fogSpeed),
            OutlineFadeIn = outlineFadeIn, MorphHold = morphHold, HasUnit = unitId != uint.MaxValue };

        // Pre-schedule the cloud + dust puffs across the spawn window so new puffs keep
        // appearing as the unit rises, then linger + fade over their own lifetimes.
        for (int i = 0; i < cfg.CloudCount; i++)
            inst.Clouds.Add(MakePuff(cfg.CloudColor, cfg.CloudWorldSize, cfg.CloudLifetime,
                cfg.CloudRise, cfg.SpawnWindow, scale, scatter: 0.85f));
        for (int i = 0; i < cfg.DustCount; i++)
            inst.Dust.Add(MakePuff(cfg.DustColor, cfg.DustWorldSize, cfg.DustLifetime,
                cfg.DustRise, cfg.SpawnWindow, scale, scatter: 1.05f, maxAlpha: cfg.DustMaxAlpha));

        _active.Add(inst);
        return id;
    }

    /// <summary>Attach a now-spawned unit to a running effect that was begun with a
    /// placeholder unit id, so its outline starts on the rise. Restarts the outline fade
    /// from now and extends the instance life to fit the outline if needed.</summary>
    public void SetUnitId(int instanceId, uint unitId)
    {
        if (instanceId <= 0) return;
        for (int i = 0; i < _active.Count; i++)
        {
            if (_active[i].InstanceId != instanceId) continue;
            var inst = _active[i];
            inst.UnitId = unitId;
            inst.HasUnit = true;
            inst.OutlineStartAge = inst.Age;   // outline fades over its full duration from NOW
            inst.RiseLife = MathF.Max(inst.RiseLife, inst.OutlineStartAge + inst.Cfg.OutlineDuration);
            return;
        }
    }

    private Puff MakePuff(HdrColor color, float worldSize, float lifetime, float rise,
        float spawnWindow, float scale, float scatter, float maxAlpha = 1f)
    {
        float ox = ((float)_rng.NextDouble() * 2f - 1f) * scatter * scale;
        float oy = ((float)_rng.NextDouble() * 2f - 1f) * scatter * 0.5f * scale;
        return new Puff
        {
            Ground = new Vec2(ox, oy),
            Delay = (float)_rng.NextDouble() * spawnWindow,
            Age = 0f,
            Lifetime = lifetime,
            Rise = rise,
            WorldSize = worldSize * (0.75f + (float)_rng.NextDouble() * 0.5f) * scale,
            Color = color,
            FramePhase = (float)_rng.NextDouble(),
            RotSpeed = ((float)_rng.NextDouble() * 0.5f + 0.1f) * (_rng.Next(2) == 0 ? 1f : -1f),
            Alpha = PuffAlpha,
            MaxAlpha = maxAlpha > 0f ? maxAlpha : 1f,
        };
    }

    public void Update(float dt)
    {
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            var inst = _active[i];
            inst.Age += dt * inst.RiseSpeed;       // rise clock: outline + morph + light
            float fdt = dt * inst.FogSpeed;        // fog clock: cloud + dust puffs
            inst.FogAge += fdt;
            // Puffs gate + age on the fog clock, so their whole appear→rise→fade timeline
            // scales with FogSpeed (a higher FogSpeed makes the smoke billow + clear sooner).
            for (int p = 0; p < inst.Clouds.Count; p++) { var q = inst.Clouds[p]; if (inst.FogAge >= q.Delay) q.Age += fdt; inst.Clouds[p] = q; }
            for (int p = 0; p < inst.Dust.Count; p++) { var q = inst.Dust[p]; if (inst.FogAge >= q.Delay) q.Age += fdt; inst.Dust[p] = q; }
            if (inst.Age >= inst.RiseLife && inst.FogAge >= inst.FogLife)
                _active.RemoveAt(i);
        }
    }

    public void Clear() => _active.Clear();

    /// <summary>Outline params for a rising unit, with the fade-out applied, or false
    /// if this unit has no active reanimation. Called from the unit draw.</summary>
    public bool TryGetOutline(uint unitId, out HdrColor c1, out HdrColor c2,
        out float width, out float pulseWidth, out float pulseSpeed)
    {
        c1 = default; c2 = default; width = 0; pulseWidth = 0; pulseSpeed = 0;
        for (int i = 0; i < _active.Count; i++)
        {
            var inst = _active[i];
            if (inst.UnitId != unitId) continue;
            // The outline only appears AFTER the morph finishes — which is when the unit attaches.
            // From that moment it blooms IN over OutlineFadeInDur, then fades OUT over the rest of
            // OutlineDuration. All in effect-time (measured from attach) so it stays synced to the rise.
            float since = inst.Age - inst.OutlineStartAge;
            float fadeIn = MathF.Min(inst.Cfg.OutlineFadeInDur, inst.Cfg.OutlineDuration);
            float fade = (fadeIn > 0f && since < fadeIn)
                ? since / fadeIn
                : MathF.Max(0f, 1f - (since - fadeIn) / MathF.Max(0.01f, inst.Cfg.OutlineDuration - fadeIn));
            c1 = ScaleAlpha(inst.Cfg.OutlineColor, fade);
            c2 = ScaleAlpha(inst.Cfg.OutlinePulseColor, fade);
            width = inst.Cfg.OutlineWidth;
            pulseWidth = inst.Cfg.OutlinePulseWidth;
            pulseSpeed = inst.Cfg.OutlinePulseSpeed;
            return true;
        }
        return false;
    }

    /// <summary>Outline params for a corpse that's reanimating (the unit hasn't spawned yet),
    /// keyed by effect instance id, with the outline alpha fading IN (0->1 over the spawn delay).
    /// Returns false once the unit has attached — the outline moves to the unit (fade out) then.</summary>
    public bool TryGetCorpseOutline(int instanceId, out HdrColor c1, out HdrColor c2,
        out float width, out float pulseWidth, out float pulseSpeed, out float morphT)
    {
        c1 = default; c2 = default; width = 0; pulseWidth = 0; pulseSpeed = 0; morphT = 0f;
        if (instanceId <= 0) return false;
        for (int i = 0; i < _active.Count; i++)
        {
            var inst = _active[i];
            if (inst.InstanceId != instanceId || inst.HasUnit) continue;
            float fadeIn = inst.OutlineFadeIn > 0f
                ? MathHelper.Clamp(inst.Age / inst.OutlineFadeIn, 0f, 1f) : 1f;
            // Pose morph is delayed: hold the death frame for MorphHold seconds (clouds build up),
            // then morph over the remaining build-up window. The outline keeps fading in over the
            // full window (fadeIn), so the green energy builds while the body still lies in death.
            float morphWin = inst.OutlineFadeIn - inst.MorphHold;
            morphT = (inst.MorphHold > 0f && morphWin > 0f)
                ? MathHelper.Clamp((inst.Age - inst.MorphHold) / morphWin, 0f, 1f) : fadeIn;
            // No outline while the body morphs — the silhouette reshapes "quietly", then the green
            // glow blooms in on the finished zombie (see TryGetOutline). Alpha 0 kills the shader's
            // traced outline but leaves the morph's own fill/green-gap energy intact.
            c1 = ScaleAlpha(inst.Cfg.OutlineColor, 0f);
            c2 = ScaleAlpha(inst.Cfg.OutlinePulseColor, 0f);
            width = inst.Cfg.OutlineWidth;
            pulseWidth = inst.Cfg.OutlinePulseWidth;
            pulseSpeed = inst.Cfg.OutlinePulseSpeed;
            return true;
        }
        return false;
    }

    private static HdrColor ScaleAlpha(HdrColor c, float fade)
        => new(c.R, c.G, c.B, (byte)(c.A * fade), c.Intensity);

    // ---- Render context ----

    public void SetContext(SpriteBatch batch, Camera25D camera, Renderer renderer,
        Flipbook? cloud, Texture2D? glow)
    {
        _batch = batch; _camera = camera; _renderer = renderer; _cloud = cloud; _glow = glow;
    }

    // ---- Dust: contributed to the merged Y-sort depth list (opaque, alpha) ----

    private struct DustDraw { public Vector2 ScreenPos; public Rectangle Src; public float Scale; public float Rot; public Color Color; }
    private readonly List<DustDraw> _visibleDust = new(256);

    public void AddDustToDepthList(List<Game1.DepthItem> items)
    {
        _visibleDust.Clear();
        if (_camera == null || _renderer == null || _cloud == null || !_cloud.IsLoaded) return;
        var tex = _cloud.Texture!;
        int frameW = tex.Width / Math.Max(_cloud.Cols, 1);
        float zoom = _camera.Zoom;

        foreach (var inst in _active)
        {
            float cyc = inst.Cfg.PuffAnimCycles > 0f ? inst.Cfg.PuffAnimCycles : 1f;
            for (int p = 0; p < inst.Dust.Count; p++)
            {
                var q = inst.Dust[p];
                if (q.Age <= 0f || q.Age >= q.Lifetime) continue;
                float t = q.Age / q.Lifetime;
                float a = PuffOpacity(q, t, additive: false);
                if (a <= 0.003f) continue;
                float height = q.Rise * EaseOut(t);
                var world = inst.Ground + q.Ground;
                var screenPos = _renderer.WorldToScreen(world, height, _camera);
                int frame = _cloud.GetFrameAtNormalizedTime((t * cyc + q.FramePhase) % 1f);
                float scale = (q.WorldSize * zoom) / frameW;
                var color = ColorUtils.Premultiply(q.Color.R, q.Color.G, q.Color.B, a);

                int idx = _visibleDust.Count;
                _visibleDust.Add(new DustDraw { ScreenPos = screenPos, Src = _cloud.GetFrameRect(frame), Scale = scale, Rot = q.RotSpeed * inst.FogAge, Color = color });
                items.Add(new Game1.DepthItem { Y = world.Y + q.WorldSize * 0.5f, Type = Game1.DepthItemType.ReanimDust, Index = idx });
            }
        }
    }

    public void DrawSingleDust(int index)
    {
        if (_batch == null || _cloud?.Texture == null) return;
        if (index < 0 || index >= _visibleDust.Count) return;
        var d = _visibleDust[index];
        if (d.Scale < 0.01f) return;
        var origin = new Vector2(d.Src.Width * 0.5f, d.Src.Height * 0.5f);
        _batch.Draw(_cloud.Texture, d.ScreenPos, d.Src, d.Color, d.Rot, origin, d.Scale, SpriteEffects.None, 0f);
    }

    // ---- Light + green clouds: additive HDR pass ----

    public void DrawAdditive()
    {
        if (_batch == null || _camera == null || _renderer == null) return;
        float zoom = _camera.Zoom;

        foreach (var inst in _active)
        {
            float cyc = inst.Cfg.PuffAnimCycles > 0f ? inst.Cfg.PuffAnimCycles : 1f;
            // (2) diffuse light glow, behind/around the unit's feet
            if (_glow != null && inst.Cfg.LightWorldSize > 0f)
            {
                float lightT = inst.Cfg.LightDuration > 0f ? inst.Age / inst.Cfg.LightDuration : 1f;
                float la = inst.Cfg.LightAlpha.Evaluate(MathHelper.Clamp(lightT, 0f, 1f));
                if (la > 0.003f)
                {
                    var sp = _renderer.WorldToScreen(inst.Ground, 0.5f, _camera);
                    float scale = (inst.Cfg.LightWorldSize * zoom) / _glow.Width;
                    var origin = new Vector2(_glow.Width * 0.5f, _glow.Height * 0.5f);
                    _batch.Draw(_glow, sp, null, inst.Cfg.LightColor.ToHdrVertex(la), 0f, origin, scale, SpriteEffects.None, 0f);
                }
            }

            // (3) additive green cloud puffs
            if (_cloud != null && _cloud.IsLoaded)
            {
                var tex = _cloud.Texture!;
                int frameW = tex.Width / Math.Max(_cloud.Cols, 1);
                for (int p = 0; p < inst.Clouds.Count; p++)
                {
                    var q = inst.Clouds[p];
                    if (q.Age <= 0f || q.Age >= q.Lifetime) continue;
                    float t = q.Age / q.Lifetime;
                    float a = PuffOpacity(q, t, additive: true);
                    if (a <= 0.003f) continue;
                    float height = q.Rise * EaseOut(t);
                    var world = inst.Ground + q.Ground;
                    var sp = _renderer.WorldToScreen(world, height, _camera);
                    int frame = _cloud.GetFrameAtNormalizedTime((t * cyc + q.FramePhase) % 1f);
                    var src = _cloud.GetFrameRect(frame);
                    float scale = (q.WorldSize * zoom) / frameW;
                    var origin = new Vector2(src.Width * 0.5f, src.Height * 0.5f);
                    // layerDepth from the puff's ground Y — only used when the depth-sorted-fog batch is
                    // active (DepthStencilState.DepthRead); ignored otherwise. MUST match the occluder
                    // stamp's mapping (GameRenderer.FogDepthForY): larger Y -> smaller depth.
                    float ld = MathHelper.Clamp(1f - world.Y * 0.005f, 0f, 1f);
                    _batch.Draw(tex, sp, src, q.Color.ToHdrVertex(a), q.RotSpeed * inst.FogAge, origin, scale, SpriteEffects.None, ld);
                }
            }
        }
    }

    // ---- Depth-sorted particle pass (Performance.DepthSortedFog): clouds + dust interleaved ----

    private struct ParticleDraw
    {
        public Texture2D Tex; public Vector2 ScreenPos, Origin; public Rectangle Src;
        public float Scale, Rot; public Color Color; public bool Additive; public float SortY, LayerDepth;
    }
    private readonly List<ParticleDraw> _sortScratch = new();
    private static float FogDepth(float y) => MathHelper.Clamp(1f - y * 0.005f, 0f, 1f);

    /// <summary>Draw ALL active reanim particles — the diffuse light + green cloud puffs (additive) and
    /// the dark dust puffs (alpha) — in ONE Y-sorted sequence, flipping blend per puff, so bright and
    /// dark puffs interleave by spawn position instead of the clouds always drawing over the dust.
    /// DepthStencilState.DepthRead, so units already stamped into the depth buffer occlude them. Manages
    /// its own batches (ends on exit); the caller re-Begins its own batch afterward. Used only on the
    /// DepthSortedFog path — the OFF path still uses DrawAdditive + the Y-sorted dust depth list.</summary>
    public void DrawSortedParticles(Microsoft.Xna.Framework.Graphics.Effect? hdrEffect)
    {
        if (_batch == null || _camera == null || _renderer == null || _cloud?.Texture == null) return;
        float zoom = _camera.Zoom;
        var tex = _cloud.Texture!;
        int frameW = tex.Width / Math.Max(_cloud.Cols, 1);
        _sortScratch.Clear();

        foreach (var inst in _active)
        {
            float cyc = inst.Cfg.PuffAnimCycles > 0f ? inst.Cfg.PuffAnimCycles : 1f;

            // Diffuse light glow (additive) — sorted at the grave position.
            if (_glow != null && inst.Cfg.LightWorldSize > 0f)
            {
                float lightT = inst.Cfg.LightDuration > 0f ? inst.Age / inst.Cfg.LightDuration : 1f;
                float la = inst.Cfg.LightAlpha.Evaluate(MathHelper.Clamp(lightT, 0f, 1f));
                if (la > 0.003f)
                {
                    var lp = _renderer.WorldToScreen(inst.Ground, 0.5f, _camera);
                    float lscale = (inst.Cfg.LightWorldSize * zoom) / _glow.Width;
                    _sortScratch.Add(new ParticleDraw {
                        Tex = _glow, ScreenPos = lp, Src = new Rectangle(0, 0, _glow.Width, _glow.Height),
                        Origin = new Vector2(_glow.Width * 0.5f, _glow.Height * 0.5f), Scale = lscale, Rot = 0f,
                        Color = inst.Cfg.LightColor.ToHdrVertex(la), Additive = true,
                        SortY = inst.Ground.Y, LayerDepth = FogDepth(inst.Ground.Y) });
                }
            }

            for (int p = 0; p < inst.Clouds.Count; p++) AddPuff(inst.Clouds[p], inst, cyc, tex, frameW, zoom, additive: true);
            for (int p = 0; p < inst.Dust.Count; p++)   AddPuff(inst.Dust[p], inst, cyc, tex, frameW, zoom, additive: false);
        }

        if (_sortScratch.Count == 0) return;
        _sortScratch.Sort(static (a, b) => a.SortY.CompareTo(b.SortY));   // back (small Y) -> front (large Y)

        bool? curAdd = null;
        for (int i = 0; i < _sortScratch.Count; i++)
        {
            var d = _sortScratch[i];
            if (curAdd != d.Additive)
            {
                if (curAdd != null) _batch.End();
                _batch.Begin(SpriteSortMode.Deferred, d.Additive ? BlendState.Additive : BlendState.AlphaBlend,
                    SamplerState.LinearClamp, DepthStencilState.DepthRead, RasterizerState.CullNone,
                    d.Additive ? hdrEffect : null);
                curAdd = d.Additive;
            }
            _batch.Draw(d.Tex, d.ScreenPos, d.Src, d.Color, d.Rot, d.Origin, d.Scale, SpriteEffects.None, d.LayerDepth);
        }
        if (curAdd != null) _batch.End();
    }

    // One puff -> a draw descriptor in _sortScratch. Mirrors DrawAdditive's cloud math (additive, HDR
    // colour) and AddDustToDepthList's dust math (alpha, premultiplied colour, sorted a half-size
    // forward). Same cloud03 sheet either way; only blend + colour differ.
    private void AddPuff(in Puff q, Instance inst, float cyc, Texture2D tex, int frameW, float zoom, bool additive)
    {
        if (q.Age <= 0f || q.Age >= q.Lifetime) return;
        float t = q.Age / q.Lifetime;
        float a = PuffOpacity(q, t, additive);
        if (a <= 0.003f) return;
        float height = q.Rise * EaseOut(t);
        var world = inst.Ground + q.Ground;
        var sp = _renderer!.WorldToScreen(world, height, _camera!);
        int frame = _cloud!.GetFrameAtNormalizedTime((t * cyc + q.FramePhase) % 1f);
        var src = _cloud.GetFrameRect(frame);
        float scale = (q.WorldSize * zoom) / frameW;
        float sortY = additive ? world.Y : world.Y + q.WorldSize * 0.5f;
        _sortScratch.Add(new ParticleDraw {
            Tex = tex, ScreenPos = sp, Src = src, Origin = new Vector2(src.Width * 0.5f, src.Height * 0.5f),
            Scale = scale, Rot = q.RotSpeed * inst.FogAge, Additive = additive,
            Color = additive ? q.Color.ToHdrVertex(a) : ColorUtils.Premultiply(q.Color.R, q.Color.G, q.Color.B, a),
            SortY = sortY, LayerDepth = FogDepth(sortY) });
    }

    private static float EaseOut(float t) => 1f - (1f - t) * (1f - t);

    // Canonical puff opacity. Additive layers keep the raw curve alpha (brightness lives in the HDR
    // intensity); alpha layers fold in Color.A. Both are then clamped to the puff's MaxAlpha ceiling —
    // the per-layer "cap out at N% opacity" knob (MaxAlpha 1 = uncapped). Single source of truth for
    // every draw path (sorted pass, additive clouds, Y-sorted dust).
    private static float PuffOpacity(in Puff q, float t, bool additive)
    {
        float a = additive ? q.Alpha.Evaluate(t) : q.Alpha.Evaluate(t) * (q.Color.A / 255f);
        return MathF.Min(a, q.MaxAlpha);
    }

    // ---- The single canonical reanimation effect ("Grave Smoke") ----

    private static List<ReanimConfig> BuildPresets()
    {
        // helper colors
        HdrColor Green(byte r, byte g, byte b, byte a, float i) => new(r, g, b, a, i);

        var list = new List<ReanimConfig>();

        // The one reanimation effect — heavy dust, dim glow, slow ominous outline. Every
        // reanimation path (spell / potion / on-death / table-craft) uses this single preset.
        list.Add(new ReanimConfig
        {
            Id = "reanim_smoke", OutlineDuration = 6.2f, LightDuration = 5.0f, SpawnWindow = 0.5f, PuffAnimCycles = 1.4f,
            OutlineColor = Green(50, 200, 100, 230, 1.2f), OutlinePulseColor = Green(20, 110, 60, 160, 0.8f),
            OutlineWidth = 2.0f, OutlinePulseWidth = 4.5f, OutlinePulseSpeed = 0.9f, OutlineFadeInDur = 1.0f,
            LightColor = Green(30, 170, 90, 230, 1.3f), LightWorldSize = 3.2f, LightAlpha = new BezierCurve(0f, 0.5f, 0.5f, 0f),
            CloudColor = Green(55, 170, 85, 220, 1.1f), CloudWorldSize = 2.04f, CloudCount = 14, CloudRise = 1.0f, CloudLifetime = 6.5f,
            DustColor = Green(55, 50, 45, 220, 1.0f), DustWorldSize = 1.95f, DustCount = 16, DustRise = 0.9f, DustLifetime = 6.5f, DustMaxAlpha = 0.5f,
        });

        // Cloudless variant — identical green outline + light + pose-morph rise as
        // reanim_smoke, but no cloud/dust puffs (the dense smoke plume). For raises that
        // shouldn't kick up smoke (e.g. the corpse puppet). A spell opts in by setting its
        // reanimationEffectID to "reanim_nosmoke"; everything else keeps the full smoke.
        list.Add(new ReanimConfig
        {
            Id = "reanim_nosmoke", OutlineDuration = 6.2f, LightDuration = 5.0f, SpawnWindow = 2.0f, PuffAnimCycles = 1.4f,
            OutlineColor = Green(50, 200, 100, 230, 1.2f), OutlinePulseColor = Green(20, 110, 60, 160, 0.8f),
            OutlineWidth = 2.0f, OutlinePulseWidth = 4.5f, OutlinePulseSpeed = 0.9f, OutlineFadeInDur = 1.0f,
            LightColor = Green(30, 170, 90, 230, 1.3f), LightWorldSize = 3.2f, LightAlpha = new BezierCurve(0f, 0.5f, 0.5f, 0f),
            CloudColor = Green(55, 170, 85, 220, 1.1f), CloudWorldSize = 1.7f, CloudCount = 0, CloudRise = 1.0f, CloudLifetime = 6.0f,
            DustColor = Green(55, 50, 45, 220, 1.0f), DustWorldSize = 1.95f, DustCount = 0, DustRise = 0.9f, DustLifetime = 6.0f,
        });

        return list;
    }
}
