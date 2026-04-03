using System;
using Necroking.Core;
using Necroking.Data;
using Necroking.Movement;
using Necroking.World;

namespace Necroking.AI;

/// <summary>
/// Reusable atomic behavior steps used by all archetype routines.
/// Static methods — no per-unit allocations. Each step has an Execute and IsComplete pair.
///
/// Steps read/write UnitArrays via AIContext. They use SubroutineTimer for timing
/// and PreferredVel for movement output.
///
/// Common steps:
///   MoveToTarget     — pathfind toward current combat target
///   MoveToPosition   — pathfind toward a world position (stored in MoveTarget)
///   MoveAwayFrom     — back away from alert target to a safe distance
///   AttackTarget     — stay in melee range, let combat system handle strikes
///   WaitDuration     — idle for SubroutineTimer seconds
///   WaitForCooldown  — idle until attack cooldown expires
///   Wander           — random movement within radius of spawn point
///   FleeFromThreat   — pathfind away from alert target
///   Idle             — stand still, do nothing
/// </summary>
public static class SubroutineSteps
{
    // ═══════════════════════════════════════
    //  Movement
    // ═══════════════════════════════════════

    /// <summary>Pathfind toward current combat target unit.</summary>
    public static void MoveToTarget(ref AIContext ctx)
    {
        int targetIdx = ResolveTarget(ref ctx);
        if (targetIdx < 0) { ctx.Units.PreferredVel[ctx.UnitIndex] = Vec2.Zero; return; }
        MoveToward(ref ctx, ctx.Units.Position[targetIdx], ctx.MySpeed);
    }

    public static bool MoveToTarget_InRange(ref AIContext ctx, float range)
    {
        int targetIdx = ResolveTarget(ref ctx);
        if (targetIdx < 0) return false;
        return (ctx.Units.Position[targetIdx] - ctx.MyPos).LengthSq() <= range * range;
    }

    /// <summary>Pathfind toward MoveTarget position.</summary>
    public static void MoveToPosition(ref AIContext ctx, float speed)
    {
        MoveToward(ref ctx, ctx.Units.MoveTarget[ctx.UnitIndex], speed);
    }

    public static bool MoveToPosition_Arrived(ref AIContext ctx, float threshold = 1f)
    {
        return (ctx.Units.MoveTarget[ctx.UnitIndex] - ctx.MyPos).LengthSq() <= threshold * threshold;
    }

    /// <summary>Back away from alert target to maintain distance.</summary>
    public static void MoveAwayFrom(ref AIContext ctx, Vec2 threatPos, float desiredDist)
    {
        float dist = (ctx.MyPos - threatPos).Length();
        if (dist < desiredDist && dist > 0.01f)
        {
            var awayDir = (ctx.MyPos - threatPos) * (1f / dist);
            Vec2 fleeDest = ctx.MyPos + awayDir * (desiredDist - dist + 3f);
            MoveToward(ref ctx, fleeDest, ctx.MySpeed);
        }
        else
            ctx.Units.PreferredVel[ctx.UnitIndex] = Vec2.Zero;
    }

    /// <summary>Walk-then-idle roaming pattern. Walk to random point at walkSpeed,
    /// idle for random duration, repeat. Uses Subroutine 0=walking, 1=idle.
    /// All movement goes through pathfinding.</summary>
    public static void IdleRoam(ref AIContext ctx, float roamRadius, float walkSpeedFraction = 0.3f)
    {
        int i = ctx.UnitIndex;
        Vec2 center = ctx.Units.SpawnPosition[i];

        if (ctx.Subroutine == 0) // walking to point
        {
            Vec2 target = ctx.Units.MoveTarget[i];
            MoveToward(ref ctx, target, ctx.MySpeed * walkSpeedFraction);
            if ((target - ctx.MyPos).LengthSq() < 1.5f)
            {
                ctx.Subroutine = 1;
                ctx.SubroutineTimer = 3f + (i % 6) + ((ctx.FrameNumber + i * 3) % 5); // 3-13s idle
                ctx.Units.PreferredVel[i] = Vec2.Zero;
            }
        }
        else // idle at point
        {
            ctx.Units.PreferredVel[i] = Vec2.Zero;
            ctx.SubroutineTimer -= ctx.Dt;
            if (ctx.SubroutineTimer <= 0f)
            {
                ctx.Subroutine = 0;
                float angle = ((ctx.FrameNumber * 7 + i * 13) % 628) / 100f;
                float dist = roamRadius * (0.2f + ((ctx.FrameNumber * 3 + i * 17) % 80) / 100f);
                ctx.Units.MoveTarget[i] = center + new Vec2(MathF.Cos(angle) * dist, MathF.Sin(angle) * dist);
            }
        }
    }

    /// <summary>Random wander within radius of a center point.</summary>
    public static void Wander(ref AIContext ctx, Vec2 center, float radius, float speed)
    {
        int i = ctx.UnitIndex;
        // Pick new wander target when timer expires or arrived
        ctx.SubroutineTimer -= ctx.Dt;
        var toTarget = ctx.Units.MoveTarget[i] - ctx.MyPos;
        if (ctx.SubroutineTimer <= 0f || toTarget.LengthSq() < 1f)
        {
            // Pick random point within radius of center
            float angle = ((ctx.FrameNumber * 7 + i * 13) % 628) / 100f; // deterministic pseudo-random
            float dist = radius * (0.3f + ((ctx.FrameNumber * 3 + i * 17) % 70) / 100f);
            ctx.Units.MoveTarget[i] = center + new Vec2(MathF.Cos(angle) * dist, MathF.Sin(angle) * dist);
            ctx.SubroutineTimer = 3f + (i % 5); // 3-7 seconds between wander points
        }
        MoveToward(ref ctx, ctx.Units.MoveTarget[i], speed * 0.3f);
    }

    // ═══════════════════════════════════════
    //  Combat
    // ═══════════════════════════════════════

    /// <summary>Stay near target, let combat system handle attack.</summary>
    public static void AttackTarget(ref AIContext ctx)
    {
        int targetIdx = ResolveTarget(ref ctx);
        if (targetIdx < 0) { ctx.Units.PreferredVel[ctx.UnitIndex] = Vec2.Zero; return; }

        float dist = (ctx.Units.Position[targetIdx] - ctx.MyPos).Length();
        float attackRange = GetMeleeRange(ref ctx, targetIdx);

        if (dist > attackRange * 1.5f)
            MoveToward(ref ctx, ctx.Units.Position[targetIdx], ctx.MySpeed);
        else
            ctx.Units.PreferredVel[ctx.UnitIndex] = Vec2.Zero;

        // Set engaged target so combat system fires
        if (ctx.Units.EngagedTarget[ctx.UnitIndex].IsNone)
            ctx.Units.EngagedTarget[ctx.UnitIndex] = ctx.Units.Target[ctx.UnitIndex];
    }

    public static bool AttackTarget_CooldownStarted(ref AIContext ctx, float minTime = 0.2f)
    {
        return ctx.Units.AttackCooldown[ctx.UnitIndex] > 0 && ctx.SubroutineTimer > minTime;
    }

    /// <summary>Clear engagement, back away from target.</summary>
    public static void Disengage(ref AIContext ctx, float backoffDist)
    {
        int i = ctx.UnitIndex;
        ctx.Units.EngagedTarget[i] = CombatTarget.None;
        ctx.Units.PendingAttack[i] = CombatTarget.None;
        ctx.Units.PostAttackTimer[i] = 0f;

        int targetIdx = ResolveTarget(ref ctx);
        if (targetIdx >= 0)
        {
            float dist = (ctx.MyPos - ctx.Units.Position[targetIdx]).Length();
            if (dist < backoffDist)
            {
                var awayDir = dist > 0.01f
                    ? (ctx.MyPos - ctx.Units.Position[targetIdx]) * (1f / dist)
                    : new Vec2(1, 0);
                ctx.Units.PreferredVel[i] = awayDir * ctx.MySpeed;
            }
            else
                ctx.Units.PreferredVel[i] = Vec2.Zero;
        }
        else
            ctx.Units.PreferredVel[i] = Vec2.Zero;
    }

    public static bool Disengage_Complete(ref AIContext ctx, float backoffDist)
    {
        int targetIdx = ResolveTarget(ref ctx);
        if (targetIdx < 0) return true;
        return (ctx.MyPos - ctx.Units.Position[targetIdx]).Length() >= backoffDist;
    }

    // ═══════════════════════════════════════
    //  Waiting
    // ═══════════════════════════════════════

    /// <summary>Stand still, decrement SubroutineTimer.</summary>
    public static void Wait(ref AIContext ctx)
    {
        ctx.Units.PreferredVel[ctx.UnitIndex] = Vec2.Zero;
        ctx.SubroutineTimer -= ctx.Dt;
    }

    public static bool Wait_Done(ref AIContext ctx) => ctx.SubroutineTimer <= 0f;

    /// <summary>Wait until attack cooldown expires.</summary>
    public static void WaitForCooldown(ref AIContext ctx)
    {
        // Maintain some distance from target while waiting
        int targetIdx = ResolveTarget(ref ctx);
        if (targetIdx >= 0)
        {
            float dist = (ctx.MyPos - ctx.Units.Position[targetIdx]).Length();
            float desiredDist = GetMeleeRange(ref ctx, targetIdx) + 2f;
            if (dist < desiredDist - 0.5f)
            {
                var awayDir = dist > 0.01f
                    ? (ctx.MyPos - ctx.Units.Position[targetIdx]) * (1f / dist)
                    : new Vec2(1, 0);
                ctx.Units.PreferredVel[ctx.UnitIndex] = awayDir * ctx.MySpeed * 0.5f;
            }
            else
                ctx.Units.PreferredVel[ctx.UnitIndex] = Vec2.Zero;
        }
        else
            ctx.Units.PreferredVel[ctx.UnitIndex] = Vec2.Zero;
    }

    public static bool WaitForCooldown_Ready(ref AIContext ctx)
    {
        return ctx.Units.AttackCooldown[ctx.UnitIndex] <= 0f;
    }

    /// <summary>Stand still and face the alert target (used for alert states).</summary>
    public static void AlertStance(ref AIContext ctx)
    {
        int i = ctx.UnitIndex;
        ctx.Units.PreferredVel[i] = Vec2.Zero;
        uint targetId = ctx.AlertTarget;
        if (targetId == GameConstants.InvalidUnit) return;
        for (int j = 0; j < ctx.Units.Count; j++)
        {
            if (ctx.Units.Id[j] == targetId && ctx.Units.Alive[j])
            {
                var dir = ctx.Units.Position[j] - ctx.MyPos;
                if (dir.LengthSq() > 0.01f)
                    ctx.Units.FacingAngle[i] = MathF.Atan2(dir.Y, dir.X) * 180f / MathF.PI;
                break;
            }
        }
    }

    /// <summary>Stand still, do nothing (used for sleeping, etc.).</summary>
    public static void Idle(ref AIContext ctx)
    {
        ctx.Units.PreferredVel[ctx.UnitIndex] = Vec2.Zero;
    }

    // ═══════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════

    /// <summary>Pathfind toward a position (shared by all movement steps).</summary>
    public static void MoveToward(ref AIContext ctx, Vec2 target, float speed)
    {
        int i = ctx.UnitIndex;
        Vec2 myPos = ctx.MyPos;
        float dist = (target - myPos).Length();

        if (dist > 3f && ctx.Pathfinder?.Grid != null)
        {
            int sizeTier = TerrainCosts.SizeToTier(ctx.Units.Size[i]);
            Vec2 dir = ctx.Pathfinder.GetDirection(myPos, target, (uint)ctx.FrameNumber, sizeTier, i);
            ctx.Units.PreferredVel[i] = dir * speed;
        }
        else if (dist > 0.01f)
        {
            ctx.Units.PreferredVel[i] = (target - myPos) * (1f / dist) * speed;
        }
        else
            ctx.Units.PreferredVel[i] = Vec2.Zero;
    }

    /// <summary>Find closest enemy unit (different faction, alive).</summary>
    public static int FindClosestEnemy(ref AIContext ctx, float maxRange)
    {
        float bestDist = maxRange * maxRange;
        int bestIdx = -1;
        var myFaction = ctx.MyFaction;
        var myPos = ctx.MyPos;

        for (int j = 0; j < ctx.Units.Count; j++)
        {
            if (j == ctx.UnitIndex || !ctx.Units.Alive[j]) continue;
            if (ctx.Units.Faction[j] == myFaction) continue;
            float d = (ctx.Units.Position[j] - myPos).LengthSq();
            if (d < bestDist) { bestDist = d; bestIdx = j; }
        }
        return bestIdx;
    }

    /// <summary>Resolve current combat target to unit index.</summary>
    public static int ResolveTarget(ref AIContext ctx)
    {
        var target = ctx.Units.Target[ctx.UnitIndex];
        if (!target.IsUnit) return -1;
        for (int j = 0; j < ctx.Units.Count; j++)
            if (ctx.Units.Id[j] == target.UnitID && ctx.Units.Alive[j]) return j;
        return -1;
    }

    /// <summary>Resolve the alert target to a unit index.</summary>
    public static int ResolveAlertTarget(ref AIContext ctx)
    {
        uint targetId = ctx.AlertTarget;
        if (targetId == GameConstants.InvalidUnit) return -1;
        for (int j = 0; j < ctx.Units.Count; j++)
            if (ctx.Units.Id[j] == targetId && ctx.Units.Alive[j]) return j;
        return -1;
    }

    /// <summary>Check if current target is alive.</summary>
    public static bool IsTargetAlive(ref AIContext ctx)
    {
        return ResolveTarget(ref ctx) >= 0;
    }

    public static float GetMeleeRange(ref AIContext ctx, int targetIdx)
    {
        float baseRange = ctx.GameData?.Settings.Combat.MeleeRange ?? 1.5f;
        return baseRange + ctx.Units.Stats[ctx.UnitIndex].Length * 0.15f
            + ctx.Units.Radius[ctx.UnitIndex] + ctx.Units.Radius[targetIdx];
    }
}
