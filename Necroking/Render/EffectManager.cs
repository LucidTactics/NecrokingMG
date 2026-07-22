using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Necroking.Core;
using Necroking.Lib;

namespace Necroking.Render;

public class Effect
{
    public Vec2 Position;
    public float Age;
    public float Lifetime = 1f;
    public BezierCurve AlphaCurve;
    public BezierCurve ScaleCurve;
    public Color Tint = Color.White;
    public float HdrIntensity = 1f;
    public bool Alive = true;
    public string FlipbookKey = "";
    public float AnchorX = 0.5f;
    public float AnchorY = 0.5f;
    public int BlendMode; // 0=alpha, 1=additive
    public int Alignment; // 0=ground, 1=upright

    // ScatterGlow emitter (0 radius = none): DrawEffectsFiltered registers a
    // point halo each frame, scaled by the alpha curve so the lit air breathes
    // with the effect's envelope. World units.
    public float ScatterRadius;
    public Color ScatterRgb;
    public float ScatterStrength = 1f;

    // Temperature-ramp recolor (HDR flipbooks, additive only): reference to
    // the def's recipe (shared registry object — read-only). Null = normal.
    public Necroking.Data.Registries.TemperatureRamp? TempRamp;

    // Frame playback: LoopFrames cycles the clip for the whole Lifetime;
    // otherwise the clip maps once onto the Lifetime (normalized time).
    // FpsOverride > 0 replaces the flipbook's own rate for looping playback.
    public bool LoopFrames;
    public float FpsOverride;
}

public class EffectManager
{
    private readonly List<Effect> _effects = new();

    public IReadOnlyList<Effect> Effects => _effects;

    public void Update(float dt)
    {
        for (int i = _effects.Count - 1; i >= 0; i--)
        {
            var eff = _effects[i];
            if (!eff.Alive) { _effects.RemoveAt(i); continue; }
            eff.Age += dt;
            if (eff.Age >= eff.Lifetime)
            {
                eff.Alive = false;
                _effects.RemoveAt(i);
            }
        }
    }

    public void SpawnExplosion(Vec2 pos, float radius)
    {
        _effects.Add(new Effect
        {
            Position = pos,
            Lifetime = 0.4f,
            AlphaCurve = new BezierCurve(0.8f, 1f, 0.7f, 0f),
            ScaleCurve = new BezierCurve(radius * 0.5f, radius * 0.8f, radius, radius),
            Tint = new Color(180, 80, 255)
        });
    }

    public void SpawnDustPuff(Vec2 pos)
    {
        _effects.Add(new Effect
        {
            Position = pos,
            Lifetime = 0.5f,
            AlphaCurve = new BezierCurve(0f, 1f, 0.5f, 0f),
            ScaleCurve = new BezierCurve(0.2f, 0.4f, 0.5f, 0.5f),
            Tint = new Color(140, 120, 90, 200)
        });
    }

    /// <summary>Canonical FlipbookRef -> one-shot effect spawn (cast flares,
    /// summon poofs, every category's hit effect — game AND spell-editor
    /// preview). Honors the ref's full contract: Duration -1 = one playthrough
    /// at the effective FPS (or 0.4s when looping — a loop has no natural
    /// length), Loop cycles frames for the lifetime, FPS overrides the
    /// flipbook's own rate, temperature ramp passes through.</summary>
    public void SpawnFromRef(Necroking.Data.Registries.FlipbookRef? fb, Vec2 pos,
        Dictionary<string, Flipbook> flipbooks,
        float scatterRadius = 0f, Color scatterRgb = default, float scatterStrength = 1f)
    {
        if (fb == null || string.IsNullOrEmpty(fb.FlipbookID)) return;

        var tint = fb.Color.ToColor();
        int blendMode = fb.BlendMode == "Additive" ? 1 : 0;
        int alignment = fb.Alignment == "Upright" ? 1 : 0;

        // The ref's OWN scatter fields beat whatever spell-level scatter the
        // call site threaded in — per-effect authoring is the more specific
        // intent (spell editor Hit Effect section).
        if (fb.ScatterRadius > 0f)
        {
            scatterRadius = fb.ScatterRadius;
            scatterRgb = new Color(fb.Color.R, fb.Color.G, fb.Color.B);
            scatterStrength = fb.ScatterStrength;
        }

        float duration = fb.Duration;
        if (duration < 0f)
        {
            // One playthrough of the clip; loops fall back to the classic 0.4s.
            duration = 0.4f;
            if (!fb.Loop && flipbooks.TryGetValue(fb.FlipbookID, out var rtFb) && rtFb.IsLoaded)
            {
                float fps = fb.FPS > 0f ? fb.FPS : rtFb.FPS;
                if (fps > 0f) duration = rtFb.TotalFrames / fps;
            }
        }

        SpawnSpellImpact(pos, fb.Scale, tint, fb.FlipbookID,
            fb.Color.Intensity, blendMode, alignment, duration,
            scatterRadius, scatterRgb, scatterStrength,
            temperatureRamp: fb.TemperatureRamp,
            loop: fb.Loop, fpsOverride: fb.FPS);
    }

    public void SpawnSpellImpact(Vec2 pos, float scale, Color tint, string flipbookKey,
                                  float hdrIntensity = 1f, int blendMode = 0, int alignment = 0,
                                  float duration = -1f,
                                  float scatterRadius = 0f, Color scatterRgb = default,
                                  float scatterStrength = 1f,
                                  Necroking.Data.Registries.TemperatureRamp? temperatureRamp = null,
                                  bool loop = false, float fpsOverride = 0f)
    {
        _effects.Add(new Effect
        {
            Position = pos,
            Lifetime = duration >= 0f ? duration : 0.4f,
            AlphaCurve = new BezierCurve(0.8f, 1f, 0.7f, 0f),
            ScaleCurve = new BezierCurve(scale * 0.5f, scale * 0.8f, scale, scale),
            Tint = tint,
            HdrIntensity = hdrIntensity,
            FlipbookKey = flipbookKey,
            BlendMode = blendMode,
            Alignment = alignment,
            AnchorX = 0.5f,
            AnchorY = alignment == 1 ? 1f : 0.5f, // ground-aligned effects sit on their bottom edge
            ScatterRadius = scatterRadius,
            ScatterRgb = scatterRgb,
            ScatterStrength = scatterStrength,
            TempRamp = temperatureRamp,
            LoopFrames = loop,
            FpsOverride = fpsOverride,
        });
    }

    public void Clear() => _effects.Clear();
}
