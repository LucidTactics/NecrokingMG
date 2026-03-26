using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Necroking.Core;

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

    public void SpawnSpellImpact(Vec2 pos, float scale, Color tint, string flipbookKey,
                                  float hdrIntensity = 1f, int blendMode = 0, int alignment = 0,
                                  float duration = -1f)
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
            AnchorX = alignment == 1 ? 0.5f : 0.5f,
            AnchorY = alignment == 1 ? 1f : 0.5f
        });
    }

    public void Clear() => _effects.Clear();
}
