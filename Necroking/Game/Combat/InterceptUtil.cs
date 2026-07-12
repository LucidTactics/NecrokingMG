using Necroking.Core;
using Necroking.Lib;

namespace Necroking.GameSystems.Combat;

/// <summary>
/// Single source for target leading — "where will this target be when my
/// projectile/leap gets there". Used by arrow ballistics (FireArrowAt) and the
/// pounce landing pick (InitiatePounceWithWeapon); any spell that launches
/// something at a moving unit should call this rather than re-deriving the
/// intercept math. Leading belongs in the CALLER (where target velocity is
/// legitimately known) — position consumers like ProjectileManager.Spawn /
/// BeginPounce take the already-led point by design.
/// </summary>
public static class InterceptUtil
{
    /// <summary>
    /// Linear intercept: predicted target position after the travel time it
    /// takes to reach it at <paramref name="travelSpeed"/>. Each iteration
    /// re-solves travel time against the previous prediction, converging for
    /// fast targets / slow projectiles; iterations=1 is the classic one-shot
    /// lead (what FireArrowAt did inline before this helper existed).
    /// Assumes the target keeps its current velocity — no pathing lookahead.
    /// </summary>
    public static Vec2 PredictPosition(Vec2 from, Vec2 targetPos, Vec2 targetVel,
        float travelSpeed, int iterations = 2)
    {
        if (travelSpeed <= 0.001f || targetVel.LengthSq() < 0.0001f)
            return targetPos;

        Vec2 predicted = targetPos;
        for (int k = 0; k < iterations; k++)
        {
            float travelTime = (predicted - from).Length() / travelSpeed;
            predicted = targetPos + targetVel * travelTime;
        }
        return predicted;
    }

    /// <summary>
    /// Cap how far leading may stretch an ability past its tuned range:
    /// the led point is allowed out to maxRange × (1 + overshootFraction)
    /// from <paramref name="from"/>, then pulled back onto that circle
    /// (direction preserved). WC3-style: abilities may reach a little past
    /// their range to catch a runner, but not arbitrarily far. 0.3 (+30%)
    /// is the agreed default; abilities that want a different allowance
    /// pass their own fraction.
    /// </summary>
    public static Vec2 ClampLeadOvershoot(Vec2 from, Vec2 ledPoint, float maxRange,
        float overshootFraction = 0.3f)
    {
        float allowed = maxRange * (1f + overshootFraction);
        Vec2 d = ledPoint - from;
        float len = d.Length();
        if (len <= allowed || len < 0.001f) return ledPoint;
        return from + d * (allowed / len);
    }
}
