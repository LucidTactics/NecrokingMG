using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Core;
using Necroking.Lib;

namespace Necroking.Render;

/// <summary>
/// Ground fog as a VOLUME units rise out of — the depth-stamped wisp technique
/// (todos/render-pipeline-design.md §10.4).
///
/// Two cooperating layers per fog bank:
///  - BACK BLANKET: large, very soft ellipses submitted to WorldLayer.FogBack —
///    drawn behind all Y-sorted bodies for continuous ambient coverage. No unit
///    interaction needed; it's behind them.
///  - WISPS: short, ground-hugging cloud puffs submitted to WorldLayer.FogWisps
///    with Materials.FogWisp (alpha + DepthRead). They depth-test against the
///    unit silhouettes stamped by the fog occluder pass, so a wisp drifting in
///    front of a unit swallows its legs (wisps only reach fog height) while the
///    torso occludes wisps behind it — per-pixel, no whole-quad popping.
///
/// Fog banks are spawned by gameplay/dev commands (`groundfog` dev command,
/// scenarios; weather/map hooks can SpawnBank later). Wisps live in one pooled
/// list, drift slowly, fade in/out over their lifetime, and respawn while their
/// bank is alive.
/// </summary>
public class GroundFogSystem
{
    private struct Bank
    {
        public Vec2 Center;
        public float Radius;
        public float Density;   // 0..1 — drives wisp quota and alpha
        public float Ttl;       // seconds; <0 = until cleared
        public bool Alive;
    }

    private struct Wisp
    {
        public Vec2 Pos;
        public Vec2 Drift;      // world units/sec
        public float Age;
        public float Lifetime;
        public float WorldSize;
        public float FramePhase;
        public float RotSpeed;
        public int Bank;
        public bool Alive;
    }

    private readonly List<Bank> _banks = new();
    private readonly List<Wisp> _wisps = new();
    private readonly Random _rng = new(0xF06);

    private SpriteBatch? _batch;
    private Camera25D? _camera;
    private Renderer? _renderer;
    private Flipbook? _cloud;
    private Texture2D? _glow;
    private float _gameTime;

    // Fog look constants. Colors are premultiplied at draw; ambient-tinted so
    // fog dims at night like everything else.
    private static readonly Color FogColor = new(188, 198, 202);
    private const float WispMaxAlpha = 0.55f;    // per-wisp ceiling at density 1
    private const float BlanketAlpha = 0.22f;    // back blanket ceiling at density 1
    private const float WispMinSize = 2.5f;      // world units
    private const float WispMaxSize = 5.5f;
    private const float WispMinLife = 5f;        // seconds
    private const float WispMaxLife = 11f;

    public bool HasActiveBanks => _banks.Count > 0;

    public void SetContext(SpriteBatch batch, Camera25D camera, Renderer renderer,
        Flipbook cloud, Texture2D glow, float gameTime)
    {
        _batch = batch;
        _camera = camera;
        _renderer = renderer;
        _cloud = cloud;
        _glow = glow;
        _gameTime = gameTime;
    }

    /// <summary>Spawn a fog bank. ttl &lt; 0 = persists until Clear/RemoveBank.</summary>
    public void SpawnBank(Vec2 center, float radius, float density, float ttl = -1f)
    {
        _banks.Add(new Bank
        {
            Center = center,
            Radius = MathF.Max(1f, radius),
            Density = Math.Clamp(density, 0.05f, 1f),
            Ttl = ttl,
            Alive = true,
        });
    }

    public void Clear()
    {
        _banks.Clear();
        _wisps.Clear();
    }

    public int BankCount => _banks.Count;

    /// <summary>Advance banks and wisps. Call once per frame before collection.</summary>
    public void Update(float dt)
    {
        if (_banks.Count == 0 && _wisps.Count == 0) return;

        // Bank TTLs
        for (int i = _banks.Count - 1; i >= 0; i--)
        {
            var b = _banks[i];
            if (b.Ttl >= 0f)
            {
                b.Ttl -= dt;
                if (b.Ttl <= 0f) { _banks.RemoveAt(i); ReindexWisps(i); continue; }
                _banks[i] = b;
            }
        }

        // Wisp aging + drift; expire wisps whose bank died (they finish their
        // fade-out naturally via Age but stop respawning).
        for (int i = _wisps.Count - 1; i >= 0; i--)
        {
            var w = _wisps[i];
            w.Age += dt;
            w.Pos += w.Drift * dt;
            if (w.Age >= w.Lifetime) { _wisps.RemoveAt(i); continue; }
            _wisps[i] = w;
        }

        // Respawn toward each bank's quota. Quota scales with area × density,
        // capped so a huge dense bank can't runaway the wisp count (the perf
        // knob from the design: fog cost = wisp overdraw, nothing else).
        for (int bi = 0; bi < _banks.Count; bi++)
        {
            var b = _banks[bi];
            int quota = Math.Clamp((int)(b.Radius * b.Radius * b.Density * 1.4f), 4, 160);
            int have = 0;
            for (int i = 0; i < _wisps.Count; i++)
                if (_wisps[i].Bank == bi) have++;
            // Stagger spawns (a few per frame) so a new bank fades in rather
            // than popping fully formed.
            int spawn = Math.Min(quota - have, 4);
            for (int s = 0; s < spawn; s++)
                _wisps.Add(MakeWisp(bi, b));
        }
    }

    private void ReindexWisps(int removedBank)
    {
        for (int i = 0; i < _wisps.Count; i++)
        {
            var w = _wisps[i];
            if (w.Bank == removedBank) w.Bank = -1;          // orphan: fades out, no respawn
            else if (w.Bank > removedBank) w.Bank--;
            _wisps[i] = w;
        }
    }

    private Wisp MakeWisp(int bankIdx, in Bank b)
    {
        // Uniform position inside the bank disc (sqrt for area-uniformity).
        float ang = (float)(_rng.NextDouble() * Math.PI * 2);
        float r = b.Radius * MathF.Sqrt((float)_rng.NextDouble());
        var pos = new Vec2(b.Center.X + MathF.Cos(ang) * r, b.Center.Y + MathF.Sin(ang) * r * 0.8f);
        // Slow ambient drift + per-wisp jitter (wind hookup can replace this).
        var drift = new Vec2(0.12f + (float)_rng.NextDouble() * 0.15f,
                             ((float)_rng.NextDouble() - 0.5f) * 0.08f);
        return new Wisp
        {
            Pos = pos,
            Drift = drift,
            Age = 0f,
            Lifetime = WispMinLife + (float)_rng.NextDouble() * (WispMaxLife - WispMinLife),
            WorldSize = WispMinSize + (float)_rng.NextDouble() * (WispMaxSize - WispMinSize),
            FramePhase = (float)_rng.NextDouble(),
            RotSpeed = ((float)_rng.NextDouble() - 0.5f) * 0.25f,
            Bank = bankIdx,
            Alive = true,
        };
    }

    /// <summary>Smooth fade-in (first 25%) / fade-out (last 35%) opacity envelope.</summary>
    private static float WispEnvelope(float t)
    {
        float inT = Math.Clamp(t / 0.25f, 0f, 1f);
        float outT = Math.Clamp((1f - t) / 0.35f, 0f, 1f);
        float a = inT * inT * (3f - 2f * inT);
        float o = outT * outT * (3f - 2f * outT);
        return a * o;
    }

    // Cached callback for the blanket (drawn via callback so it can use
    // non-uniform scale — a ground-plane ellipse, not a round puff).
    private SpriteDrawCallback? _cbBlanket;

    /// <summary>Submit the back blankets into the world pass (WorldLayer.FogBack —
    /// behind all Y-sorted bodies).</summary>
    public void CollectBack(SpriteQueuePass world, Color ambient)
    {
        if (_banks.Count == 0 || _glow == null || _camera == null || _renderer == null) return;
        _cbBlanket ??= (SpriteScope s, int bi, int _) => DrawBlanket(s, bi, _lastAmbient);
        _lastAmbient = ambient;
        for (int bi = 0; bi < _banks.Count; bi++)
            world.SubmitCallback(WorldLayer.FogBack, _banks[bi].Center.Y, _cbBlanket, bi, 0);
    }

    private Color _lastAmbient = Color.White;

    private void DrawBlanket(SpriteScope batch, int bi, Color ambient)
    {
        if (bi >= _banks.Count || _glow == null) return;
        var b = _banks[bi];
        var sp = _renderer!.WorldToScreen(b.Center, 0f, _camera!);
        float alpha = MathHelper.Clamp(BlanketAlpha * b.Density, 0f, 1f);
        // Straight alpha; the draw surface encodes it for the open material.
        var col = new Color(
            FogColor.R * ambient.R / 255, FogColor.G * ambient.G / 255,
            FogColor.B * ambient.B / 255, (int)(alpha * 255));
        // Ground-plane ellipse: X spans the bank, Y squashed by the isometric ratio.
        float pxW = b.Radius * 2.4f * _camera!.Zoom;
        float pxH = pxW * _camera.YRatio * 0.55f;
        var origin = new Vector2(_glow.Width * 0.5f, _glow.Height * 0.5f);
        batch.Draw(_glow, sp, null, col, 0f, origin,
            new Vector2(pxW / _glow.Width, pxH / _glow.Height), SpriteEffects.None, 0f);
    }

    /// <summary>Submit the depth-tested wisps into the effects pass
    /// (WorldLayer.FogWisps, Materials.FogWisp). Wisps sort back-to-front among
    /// themselves via the key's depth bits; the GPU depth test against the unit
    /// stamps does the unit interleave.</summary>
    public void CollectWisps(SpriteQueuePass fx, Color ambient, int screenW, int screenH)
    {
        if (_wisps.Count == 0 || _cloud?.Texture == null || _camera == null || _renderer == null) return;

        var tex = _cloud.Texture!;
        int frameW = tex.Width / Math.Max(_cloud.Cols, 1);
        float zoom = _camera.Zoom;
        float camY = _camera.Position.Y;

        for (int i = 0; i < _wisps.Count; i++)
        {
            var w = _wisps[i];
            float t = w.Age / w.Lifetime;
            float density = (w.Bank >= 0 && w.Bank < _banks.Count) ? _banks[w.Bank].Density : 0.5f;
            float alpha = WispMaxAlpha * density * WispEnvelope(t);
            if (alpha <= 0.004f) continue;

            // Slight rise off the ground so wisps read as a layer, not a decal.
            float height = 0.25f + 0.35f * MathF.Sin((w.FramePhase + t) * MathF.PI);
            var sp = _renderer.WorldToScreen(w.Pos, height, _camera);
            if (sp.X < -80 || sp.X > screenW + 80 || sp.Y < -80 || sp.Y > screenH + 80) continue;

            int frame = _cloud.GetFrameAtNormalizedTime((t * 2f + w.FramePhase) % 1f);
            var src = _cloud.GetFrameRect(frame);
            float scale = (w.WorldSize * zoom) / frameW;

            // Straight alpha; the queue flush encodes it for the FogWisp material.
            var col = new Color(
                FogColor.R * ambient.R / 255, FogColor.G * ambient.G / 255,
                FogColor.B * ambient.B / 255, (int)(MathHelper.Clamp(alpha, 0f, 1f) * 255));

            // Forward-biased sort Y (half a wisp ahead), same convention as the
            // reanim dust: the wisp's visual mass hangs below/ahead of its center.
            float sortY = w.Pos.Y + w.WorldSize * 0.5f;

            fx.SubmitSprite(WorldLayer.FogWisps, sortY, tex, sp, src, col,
                w.RotSpeed * _gameTime, new Vector2(src.Width * 0.5f, src.Height * 0.5f),
                scale, SpriteEffects.None, Materials.FogWisp,
                layerDepth: GameRenderer.FogDepthForY(sortY, camY));
        }
    }
}
