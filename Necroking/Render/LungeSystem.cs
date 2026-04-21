using System;
using Necroking.Core;
using Necroking.Movement;

namespace Necroking.Render;

/// <summary>
/// Cosmetic attack-lunge. Writes <see cref="Unit.RenderOffset"/> each frame during
/// an attack animation so the sprite visually commits forward toward the target
/// at effect_time and decays back by the end of the anim. The simulation
/// position (<see cref="Unit.Position"/>) is never touched — pathfinding, ORCA,
/// AI ranges and collisions all stay on the raw position.
///
/// Curve:
///   t ∈ [0, tEffect]       : 0 → 1  (commit, smoothstep)
///   t ∈ [tEffect, 1]       : 1 → 0  (recover, smoothstep)
/// where t = animTime / totalDuration and tEffect = effectTime / totalDuration.
///
/// Direction: unit facing (same as what IsFacingTarget gates attacks on, so it
/// always points at the intended target at commit).
///
/// Suppressed when:
///   - JumpPhase != 0 (pounce/jump owns position; no extra cosmetic offset).
///   - Incap.Active (prone units can't lunge).
///   - ctrl state isn't an attack state (no attack running).
/// </summary>
public static class LungeSystem
{
    public static void Update(Unit unit, AnimController ctrl)
    {
        // Fast path: if nothing cosmetic is happening, keep offset at zero.
        if (unit.JumpPhase != 0 || unit.Incap.Active
            || unit.CurrentAttackLungeDist <= 0f
            || !IsAttackState(ctrl.CurrentState))
        {
            unit.RenderOffset = Vec2.Zero;
            if (!IsAttackState(ctrl.CurrentState)) unit.CurrentAttackLungeDist = 0f;
            return;
        }

        float totalMs = ctrl.GetTotalDurationSeconds(ctrl.CurrentState) * 1000f;
        float effectMs = ctrl.GetEffectTimeSeconds(ctrl.CurrentState) * 1000f;
        if (totalMs <= 0f)
        {
            unit.RenderOffset = Vec2.Zero;
            return;
        }

        // ctrl.AnimTime is ms-based when meta has durations. If meta is missing,
        // effectMs will be 0 → center the peak.
        float animTime = ctrl.AnimTime;
        float t = MathUtil.Clamp(animTime / totalMs, 0f, 1f);
        float tEffect = effectMs > 0f ? MathUtil.Clamp(effectMs / totalMs, 0.05f, 0.95f) : 0.5f;

        float curve;
        if (t <= tEffect)
        {
            // Commit: 0 → 1 over [0, tEffect]
            float u = t / tEffect;
            curve = Smoothstep(u);
        }
        else
        {
            // Recover: 1 → 0 over [tEffect, 1]
            float u = (t - tEffect) / (1f - tEffect);
            curve = 1f - Smoothstep(u);
        }

        float facingRad = unit.FacingAngle * MathF.PI / 180f;
        var dir = new Vec2(MathF.Cos(facingRad), MathF.Sin(facingRad));
        unit.RenderOffset = dir * (curve * unit.CurrentAttackLungeDist);
    }

    private static bool IsAttackState(AnimState s) =>
        s == AnimState.Attack1 || s == AnimState.Attack2 || s == AnimState.Attack3
        || s == AnimState.Ranged1 || s == AnimState.Special1;

    private static float Smoothstep(float x)
    {
        x = MathUtil.Clamp(x, 0f, 1f);
        return x * x * (3f - 2f * x);
    }
}
