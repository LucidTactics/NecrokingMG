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

        // Outline (blinks via pulse, fades out over Duration)
        public HdrColor OutlineColor;
        public HdrColor OutlinePulseColor;
        public float OutlineWidth;
        public float OutlinePulseWidth;
        public float OutlinePulseSpeed;

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
    }

    private class Instance
    {
        public int InstanceId;          // stable handle so a deferred unit spawn can attach its outline
        public uint UnitId;
        public Vec2 Ground;
        public float Scale;
        public float Age;
        public float OutlineStartAge;   // outline FADE-OUT clock starts here (set when the unit attaches)
        public float OutlineFadeIn;     // corpse-phase outline FADE-IN window (0->1 over this); 0 = none
        public float MorphHold;         // hold the death pose this long (clouds build) before the morph
        public bool HasUnit;            // false while only the corpse is present (outline fades in on it)
        public float Life;   // removed when Age >= Life (the longest layer)
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
    public int Begin(uint unitId, Vec2 ground, float scale, string? configId, float outlineFadeIn = 0f, float morphHold = 0f)
    {
        if (!_configs.TryGetValue(configId ?? "", out var cfg))
            cfg = _configs.TryGetValue("reanim_classic", out var def) ? def : default;
        // Instance lives until the longest layer finishes: outline fade, light ramp, or the
        // last puff (spawn window + the puff's own lifetime).
        float life = MathF.Max(cfg.OutlineDuration, MathF.Max(cfg.LightDuration,
            cfg.SpawnWindow + MathF.Max(cfg.CloudLifetime, cfg.DustLifetime)));
        if (life <= 0f) return 0;

        int id = _nextInstanceId++;
        var inst = new Instance { InstanceId = id, UnitId = unitId, Ground = ground, Scale = scale,
            Cfg = cfg, Life = life, OutlineFadeIn = outlineFadeIn, MorphHold = morphHold, HasUnit = unitId != uint.MaxValue };

        // Pre-schedule the cloud + dust puffs across the spawn window so new puffs keep
        // appearing as the unit rises, then linger + fade over their own lifetimes.
        for (int i = 0; i < cfg.CloudCount; i++)
            inst.Clouds.Add(MakePuff(cfg.CloudColor, cfg.CloudWorldSize, cfg.CloudLifetime,
                cfg.CloudRise, cfg.SpawnWindow, scale, scatter: 0.85f));
        for (int i = 0; i < cfg.DustCount; i++)
            inst.Dust.Add(MakePuff(cfg.DustColor, cfg.DustWorldSize, cfg.DustLifetime,
                cfg.DustRise, cfg.SpawnWindow, scale, scatter: 1.05f));

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
            inst.Life = MathF.Max(inst.Life, inst.OutlineStartAge + inst.Cfg.OutlineDuration);
            return;
        }
    }

    private Puff MakePuff(HdrColor color, float worldSize, float lifetime, float rise,
        float spawnWindow, float scale, float scatter)
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
        };
    }

    public void Update(float dt)
    {
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            var inst = _active[i];
            inst.Age += dt;
            for (int p = 0; p < inst.Clouds.Count; p++) { var q = inst.Clouds[p]; if (inst.Age >= q.Delay) q.Age += dt; inst.Clouds[p] = q; }
            for (int p = 0; p < inst.Dust.Count; p++) { var q = inst.Dust[p]; if (inst.Age >= q.Delay) q.Age += dt; inst.Dust[p] = q; }
            if (inst.Age >= inst.Life)
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
            // Linear fade of the outline alpha across its own (longer) duration, measured
            // from when the unit attached (so a deferred rise gets the full-strength outline).
            float fade = MathF.Max(0f, 1f - (inst.Age - inst.OutlineStartAge) / inst.Cfg.OutlineDuration);
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
            c1 = ScaleAlpha(inst.Cfg.OutlineColor, fadeIn);
            c2 = ScaleAlpha(inst.Cfg.OutlinePulseColor, fadeIn);
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
                float a = q.Alpha.Evaluate(t) * (q.Color.A / 255f);
                if (a <= 0.003f) continue;
                float height = q.Rise * EaseOut(t);
                var world = inst.Ground + q.Ground;
                var screenPos = _renderer.WorldToScreen(world, height, _camera);
                int frame = _cloud.GetFrameAtNormalizedTime((t * cyc + q.FramePhase) % 1f);
                float scale = (q.WorldSize * zoom) / frameW;
                var color = ColorUtils.Premultiply(q.Color.R, q.Color.G, q.Color.B, a);

                int idx = _visibleDust.Count;
                _visibleDust.Add(new DustDraw { ScreenPos = screenPos, Src = _cloud.GetFrameRect(frame), Scale = scale, Rot = q.RotSpeed * inst.Age, Color = color });
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
                    float a = q.Alpha.Evaluate(t);
                    if (a <= 0.003f) continue;
                    float height = q.Rise * EaseOut(t);
                    var world = inst.Ground + q.Ground;
                    var sp = _renderer.WorldToScreen(world, height, _camera);
                    int frame = _cloud.GetFrameAtNormalizedTime((t * cyc + q.FramePhase) % 1f);
                    var src = _cloud.GetFrameRect(frame);
                    float scale = (q.WorldSize * zoom) / frameW;
                    var origin = new Vector2(src.Width * 0.5f, src.Height * 0.5f);
                    _batch.Draw(tex, sp, src, q.Color.ToHdrVertex(a), q.RotSpeed * inst.Age, origin, scale, SpriteEffects.None, 0f);
                }
            }
        }
    }

    private static float EaseOut(float t) => 1f - (1f - t) * (1f - t);

    // ---- 5 preset variants (all undead-green, varying execution) ----

    private static List<ReanimConfig> BuildPresets()
    {
        // helper colors
        HdrColor Green(byte r, byte g, byte b, byte a, float i) => new(r, g, b, a, i);

        var list = new List<ReanimConfig>();

        // 1. Classic — balanced reference
        list.Add(new ReanimConfig
        {
            Id = "reanim_classic", OutlineDuration = 3.0f, LightDuration = 3.0f, SpawnWindow = 2.1f,
            OutlineColor = Green(60, 255, 130, 255, 1.6f), OutlinePulseColor = Green(20, 160, 80, 200, 1.0f),
            OutlineWidth = 1.5f, OutlinePulseWidth = 3.0f, OutlinePulseSpeed = 2.0f,
            LightColor = Green(40, 230, 120, 255, 2.0f), LightWorldSize = 3.0f, LightAlpha = new BezierCurve(0f, 1f, 1f, 0f),
            CloudColor = Green(70, 230, 110, 255, 1.6f), CloudWorldSize = 1.4f, CloudCount = 6, CloudRise = 1.4f, CloudLifetime = 1.8f,
            DustColor = Green(70, 60, 50, 220, 1.0f), DustWorldSize = 1.1f, DustCount = 5, DustRise = 0.8f, DustLifetime = 1.6f,
        });

        // 2. Burst — intense / fast, max bloom, dense bright clouds
        list.Add(new ReanimConfig
        {
            Id = "reanim_burst", OutlineDuration = 2.4f, LightDuration = 2.4f, SpawnWindow = 1.7f,
            OutlineColor = Green(120, 255, 140, 255, 2.6f), OutlinePulseColor = Green(60, 255, 120, 220, 1.6f),
            OutlineWidth = 2.0f, OutlinePulseWidth = 4.0f, OutlinePulseSpeed = 4.0f,
            LightColor = Green(90, 255, 140, 255, 3.6f), LightWorldSize = 4.0f, LightAlpha = new BezierCurve(0f, 1f, 0.6f, 0f),
            CloudColor = Green(140, 255, 110, 255, 2.6f), CloudWorldSize = 1.7f, CloudCount = 11, CloudRise = 2.0f, CloudLifetime = 1.6f,
            DustColor = Green(80, 70, 55, 180, 1.0f), DustWorldSize = 1.0f, DustCount = 4, DustRise = 1.0f, DustLifetime = 1.3f,
        });

        // 3. Grave Smoke — heavy dust, dim glow, slow ominous outline
        list.Add(new ReanimConfig
        {
            Id = "reanim_smoke", OutlineDuration = 6.2f, LightDuration = 5.0f, SpawnWindow = 2.0f, PuffAnimCycles = 1.4f,
            OutlineColor = Green(50, 200, 100, 230, 1.2f), OutlinePulseColor = Green(20, 110, 60, 160, 0.8f),
            OutlineWidth = 2.0f, OutlinePulseWidth = 4.5f, OutlinePulseSpeed = 0.9f,
            LightColor = Green(30, 170, 90, 230, 1.3f), LightWorldSize = 3.2f, LightAlpha = new BezierCurve(0f, 0.5f, 0.5f, 0f),
            CloudColor = Green(55, 170, 85, 220, 1.1f), CloudWorldSize = 1.7f, CloudCount = 8, CloudRise = 1.0f, CloudLifetime = 6.0f,
            DustColor = Green(55, 50, 45, 220, 1.0f), DustWorldSize = 1.95f, DustCount = 9, DustRise = 0.9f, DustLifetime = 6.0f,
        });

        // 4. Soul Wisps — many small additive wisps, thin bright fast-blink outline, minimal dust
        list.Add(new ReanimConfig
        {
            Id = "reanim_wisps", OutlineDuration = 3.0f, LightDuration = 3.0f, SpawnWindow = 2.1f,
            OutlineColor = Green(150, 255, 180, 255, 2.4f), OutlinePulseColor = Green(40, 220, 130, 120, 1.4f),
            OutlineWidth = 1.0f, OutlinePulseWidth = 2.0f, OutlinePulseSpeed = 5.0f,
            LightColor = Green(60, 240, 150, 255, 2.2f), LightWorldSize = 2.8f, LightAlpha = new BezierCurve(0f, 1f, 0.9f, 0f),
            CloudColor = Green(110, 255, 160, 255, 2.2f), CloudWorldSize = 0.8f, CloudCount = 16, CloudRise = 2.2f, CloudLifetime = 1.7f,
            DustColor = Green(60, 60, 55, 150, 1.0f), DustWorldSize = 0.8f, DustCount = 3, DustRise = 1.0f, DustLifetime = 1.4f,
        });

        // 5. Slow Ritual — 4s, slowly swelling light, sparse slow-rising clouds, wide slow outline
        list.Add(new ReanimConfig
        {
            Id = "reanim_ritual", OutlineDuration = 4.0f, LightDuration = 4.0f, SpawnWindow = 2.8f,
            OutlineColor = Green(50, 210, 110, 230, 1.3f), OutlinePulseColor = Green(20, 140, 70, 180, 0.9f),
            OutlineWidth = 2.0f, OutlinePulseWidth = 5.0f, OutlinePulseSpeed = 0.6f,
            LightColor = Green(40, 200, 110, 240, 1.8f), LightWorldSize = 3.4f, LightAlpha = new BezierCurve(0f, 0.7f, 1f, 0f),
            CloudColor = Green(60, 200, 100, 240, 1.5f), CloudWorldSize = 1.3f, CloudCount = 7, CloudRise = 1.6f, CloudLifetime = 2.6f,
            DustColor = Green(60, 55, 50, 220, 1.0f), DustWorldSize = 1.2f, DustCount = 7, DustRise = 1.0f, DustLifetime = 2.4f,
        });

        return list;
    }
}
