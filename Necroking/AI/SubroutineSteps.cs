using System;
using Necroking.Core;
using Necroking.Data;
using Necroking.Movement;
using Necroking.Render;
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
        if (targetIdx < 0) { ctx.Units[ctx.UnitIndex].PreferredVel = Vec2.Zero; return; }
        MoveToward(ref ctx, ctx.Units[targetIdx].Position, ctx.MySpeed);
    }

    public static bool MoveToTarget_InRange(ref AIContext ctx, float range)
    {
        int targetIdx = ResolveTarget(ref ctx);
        if (targetIdx < 0) return false;
        return (ctx.Units[targetIdx].Position - ctx.MyPos).LengthSq() <= range * range;
    }

    /// <summary>Pathfind toward MoveTarget position.</summary>
    public static void MoveToPosition(ref AIContext ctx, float speed)
    {
        MoveToward(ref ctx, ctx.Units[ctx.UnitIndex].MoveTarget, speed);
    }

    public static bool MoveToPosition_Arrived(ref AIContext ctx, float threshold = 1f)
    {
        return (ctx.Units[ctx.UnitIndex].MoveTarget - ctx.MyPos).LengthSq() <= threshold * threshold;
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
            ctx.Units[ctx.UnitIndex].PreferredVel = Vec2.Zero;
    }

    /// <summary>Walk-then-idle roaming pattern. Walk to random point at walkSpeed,
    /// idle for random duration, repeat. Uses Subroutine 0=walking, 1=idle.
    /// All movement goes through pathfinding.</summary>
    public static void IdleRoam(ref AIContext ctx, float roamRadius, float walkSpeedFraction = 0.3f)
    {
        int i = ctx.UnitIndex;
        Vec2 center = ctx.Units[i].SpawnPosition;

        if (ctx.Subroutine == 0) // walking to point
        {
            Vec2 target = ctx.Units[i].MoveTarget;
            MoveToward(ref ctx, target, ctx.MySpeed * walkSpeedFraction);
            if ((target - ctx.MyPos).LengthSq() < 1.5f)
            {
                ctx.Subroutine = 1;
                ctx.SubroutineTimer = 3f + (i % 6) + ((ctx.FrameNumber + i * 3) % 5); // 3-13s idle
                SetIdle(ref ctx);
            }
        }
        else // idle at point
        {
            SetIdle(ref ctx);
            ctx.SubroutineTimer -= ctx.Dt;
            if (ctx.SubroutineTimer <= 0f)
            {
                ctx.Subroutine = 0;
                // Pick a random walkable point within roam radius. Uses the unit's
                // full-radius footprint (not just the centre tile) so wolves with
                // radius 0.5 don't target a spot whose body would clip into a tree.
                // If none of the attempts find a walkable point, leave MoveTarget
                // alone and stay idle — the unit simply tries again next tick.
                var grid = ctx.Pathfinder?.Grid;
                float unitRadius = ctx.Units[i].Radius;
                const int MaxAttempts = 12;
                bool found = false;
                for (int attempt = 0; attempt < MaxAttempts; attempt++)
                {
                    int seed = ctx.FrameNumber * 7 + i * 13 + attempt * 31;
                    float angle = (seed % 628) / 100f;
                    float dist = roamRadius * (0.2f + (((seed * 3) & 0x7F) % 80) / 100f);
                    var candidate = center + new Vec2(MathF.Cos(angle) * dist, MathF.Sin(angle) * dist);

                    if (grid != null && !IsPointWalkable(grid, candidate, unitRadius))
                        continue;

                    ctx.Units[i].MoveTarget = candidate;
                    found = true;
                    break;
                }

                // No walkable spot this tick — stay idle for a short beat and try again.
                if (!found)
                {
                    ctx.Subroutine = 1;
                    ctx.SubroutineTimer = 0.5f;
                }
            }
        }
    }

    /// <summary>
    /// True if a unit of the given radius can stand at worldPos without any blocked
    /// tile in its footprint. Mirrors Simulation.IsBlocked so AI-picked destinations
    /// match what the movement system will actually accept.
    /// </summary>
    public static bool IsPointWalkable(World.TileGrid grid, Vec2 pos, float radius)
    {
        int gx0 = (int)MathF.Floor(pos.X - radius);
        int gy0 = (int)MathF.Floor(pos.Y - radius);
        int gx1 = (int)MathF.Floor(pos.X + radius);
        int gy1 = (int)MathF.Floor(pos.Y + radius);
        for (int gy = gy0; gy <= gy1; gy++)
            for (int gx = gx0; gx <= gx1; gx++)
                if (grid.InBounds(gx, gy) && grid.GetCost(gx, gy) == 255) return false;
        return true;
    }

    /// <summary>Random wander within radius of a center point.</summary>
    public static void Wander(ref AIContext ctx, Vec2 center, float radius, float speed)
    {
        int i = ctx.UnitIndex;
        // Pick new wander target when timer expires or arrived
        ctx.SubroutineTimer -= ctx.Dt;
        var toTarget = ctx.Units[i].MoveTarget - ctx.MyPos;
        if (ctx.SubroutineTimer <= 0f || toTarget.LengthSq() < 1f)
        {
            // Pick random point within radius of center
            float angle = ((ctx.FrameNumber * 7 + i * 13) % 628) / 100f; // deterministic pseudo-random
            float dist = radius * (0.3f + ((ctx.FrameNumber * 3 + i * 17) % 70) / 100f);
            ctx.Units[i].MoveTarget = center + new Vec2(MathF.Cos(angle) * dist, MathF.Sin(angle) * dist);
            ctx.SubroutineTimer = 3f + (i % 5); // 3-7 seconds between wander points
        }
        MoveToward(ref ctx, ctx.Units[i].MoveTarget, speed * 0.3f);
    }

    // ═══════════════════════════════════════
    //  Combat
    // ═══════════════════════════════════════

    /// <summary>Stay near target, let combat system handle attack.</summary>
    public static void AttackTarget(ref AIContext ctx)
    {
        int targetIdx = ResolveTarget(ref ctx);
        if (targetIdx < 0) { ctx.Units[ctx.UnitIndex].PreferredVel = Vec2.Zero; return; }

        float dist = (ctx.Units[targetIdx].Position - ctx.MyPos).Length();
        float attackRange = GetMeleeRange(ref ctx, targetIdx);

        if (dist > attackRange * 1.5f)
            MoveToward(ref ctx, ctx.Units[targetIdx].Position, ctx.MySpeed);
        else
            ctx.Units[ctx.UnitIndex].PreferredVel = Vec2.Zero;

        // Set engaged target so combat system fires
        if (ctx.Units[ctx.UnitIndex].EngagedTarget.IsNone)
            ctx.Units[ctx.UnitIndex].EngagedTarget = ctx.Units[ctx.UnitIndex].Target;
    }

    public static bool AttackTarget_CooldownStarted(ref AIContext ctx, float minTime = 0.2f)
    {
        return ctx.Units[ctx.UnitIndex].AttackCooldown > 0 && ctx.SubroutineTimer > minTime;
    }

    /// <summary>Clear engagement, back away from target.</summary>
    public static void Disengage(ref AIContext ctx, float backoffDist)
    {
        int i = ctx.UnitIndex;
        ctx.Units[i].EngagedTarget = CombatTarget.None;
        ctx.Units[i].PendingAttack = CombatTarget.None;
        ctx.Units[i].PostAttackTimer = 0f;

        int targetIdx = ResolveTarget(ref ctx);
        if (targetIdx >= 0)
        {
            float dist = (ctx.MyPos - ctx.Units[targetIdx].Position).Length();
            if (dist < backoffDist)
            {
                var awayDir = dist > 0.01f
                    ? (ctx.MyPos - ctx.Units[targetIdx].Position) * (1f / dist)
                    : new Vec2(1, 0);
                ctx.Units[i].PreferredVel = awayDir * ctx.MySpeed;
            }
            else
                ctx.Units[i].PreferredVel = Vec2.Zero;
        }
        else
            ctx.Units[i].PreferredVel = Vec2.Zero;
    }

    public static bool Disengage_Complete(ref AIContext ctx, float backoffDist)
    {
        int targetIdx = ResolveTarget(ref ctx);
        if (targetIdx < 0) return true;
        return (ctx.MyPos - ctx.Units[targetIdx].Position).Length() >= backoffDist;
    }

    // ═══════════════════════════════════════
    //  Waiting
    // ═══════════════════════════════════════

    /// <summary>Stand still, decrement SubroutineTimer.</summary>
    public static void Wait(ref AIContext ctx)
    {
        ctx.Units[ctx.UnitIndex].PreferredVel = Vec2.Zero;
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
            float dist = (ctx.MyPos - ctx.Units[targetIdx].Position).Length();
            float desiredDist = GetMeleeRange(ref ctx, targetIdx) + 2f;
            if (dist < desiredDist - 0.5f)
            {
                var awayDir = dist > 0.01f
                    ? (ctx.MyPos - ctx.Units[targetIdx].Position) * (1f / dist)
                    : new Vec2(1, 0);
                ctx.Units[ctx.UnitIndex].PreferredVel = awayDir * ctx.MySpeed * 0.5f;
            }
            else
                ctx.Units[ctx.UnitIndex].PreferredVel = Vec2.Zero;
        }
        else
            ctx.Units[ctx.UnitIndex].PreferredVel = Vec2.Zero;
    }

    public static bool WaitForCooldown_Ready(ref AIContext ctx)
    {
        return ctx.Units[ctx.UnitIndex].AttackCooldown <= 0f;
    }

    /// <summary>Stand still and face the alert target (used for alert states).</summary>
    public static void AlertStance(ref AIContext ctx)
    {
        int i = ctx.UnitIndex;
        ctx.Units[i].PreferredVel = Vec2.Zero;
        ctx.Units[i].RoutineAnim = AnimRequest.Locomotion(AnimState.Idle);
        uint targetId = ctx.AlertTarget;
        if (targetId == GameConstants.InvalidUnit) return;
        for (int j = 0; j < ctx.Units.Count; j++)
        {
            if (ctx.Units[j].Id == targetId && ctx.Units[j].Alive)
            {
                var dir = ctx.Units[j].Position - ctx.MyPos;
                if (dir.LengthSq() > 0.01f)
                    ctx.Units[i].FacingAngle = MathF.Atan2(dir.Y, dir.X) * 180f / MathF.PI;
                break;
            }
        }
    }

    /// <summary>Face a specific world position (for waking up toward a threat).</summary>
    public static void FacePosition(ref AIContext ctx, Vec2 target)
    {
        var dir = target - ctx.MyPos;
        if (dir.LengthSq() > 0.01f)
            ctx.Units[ctx.UnitIndex].FacingAngle = MathF.Atan2(dir.Y, dir.X) * 180f / MathF.PI;
    }

    /// <summary>Stand still, do nothing (used for sleeping, etc.).</summary>
    public static void Idle(ref AIContext ctx)
    {
        ctx.Units[ctx.UnitIndex].PreferredVel = Vec2.Zero;
    }

    /// <summary>Flee in a random direction for a given distance. Call once to set MoveTarget,
    /// then use MoveToPosition + MoveToPosition_Arrived to drive movement.</summary>
    public static void SetFleeRandomTarget(ref AIContext ctx, float distance)
    {
        int i = ctx.UnitIndex;
        // Pick pseudo-random angle based on unit index and frame
        float angle = ((ctx.FrameNumber * 7 + i * 31) % 628) / 100f;
        var target = ctx.MyPos + new Vec2(MathF.Cos(angle) * distance, MathF.Sin(angle) * distance);

        // Try to pick a walkable tile (up to 5 attempts)
        var grid = ctx.Pathfinder?.Grid;
        if (grid != null)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                int tx = (int)MathF.Floor(target.X);
                int ty = (int)MathF.Floor(target.Y);
                if (tx >= 0 && ty >= 0 && grid.GetCost(tx, ty) != 255) break;
                angle += 1.26f; // ~72 degrees
                target = ctx.MyPos + new Vec2(MathF.Cos(angle) * distance, MathF.Sin(angle) * distance);
            }
        }

        ctx.Units[i].MoveTarget = target;
    }

    /// <summary>Flee away from a specific position for a given distance.</summary>
    public static void SetFleeFromTarget(ref AIContext ctx, Vec2 threatPos, float distance)
    {
        int i = ctx.UnitIndex;
        var dir = ctx.MyPos - threatPos;
        if (dir.LengthSq() > 0.01f)
            dir = dir.Normalized();
        else
            dir = new Vec2(1f, 0f); // arbitrary fallback
        ctx.Units[i].MoveTarget = ctx.MyPos + dir * distance;
    }

    // ═══════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════

    /// <summary>Pathfind toward a position (shared by all movement steps).
    /// Also sets RoutineAnim to the appropriate locomotion state.</summary>
    public static void MoveToward(ref AIContext ctx, Vec2 target, float speed)
    {
        int i = ctx.UnitIndex;
        Vec2 myPos = ctx.MyPos;
        float dist = (target - myPos).Length();

        if (dist > 3f && ctx.Pathfinder?.Grid != null)
        {
            int sizeTier = TerrainCosts.SizeToTier(ctx.Units[i].Size);
            Vec2 dir = ctx.Pathfinder.GetDirection(myPos, target, (uint)ctx.FrameNumber, sizeTier, i);
            ctx.Units[i].PreferredVel = dir * speed;
        }
        else if (dist > 0.01f)
        {
            ctx.Units[i].PreferredVel = (target - myPos) * (1f / dist) * speed;
        }
        else
        {
            ctx.Units[i].PreferredVel = Vec2.Zero;
        }

        // Set locomotion animation based on intended speed
        SetLocomotionAnim(ref ctx, speed);
    }

    /// <summary>
    /// Set the routine animation to the appropriate locomotion state based on speed.
    /// Uses the same threshold logic as the old Game1.UpdateAnimations velocity check.
    /// </summary>
    public static void SetLocomotionAnim(ref AIContext ctx, float speed)
    {
        float baseSpeed = ctx.Units[ctx.UnitIndex].Stats.CombatSpeed;
        float jogThreshold = 4f + baseSpeed / 3f;
        float runThreshold = 6f + 2f * baseSpeed / 3f;

        AnimState state;
        if (speed <= 0.25f)
            state = AnimState.Idle;
        else if (speed < jogThreshold)
            state = AnimState.Walk;
        else if (speed < runThreshold)
            state = AnimState.Jog;
        else
            state = AnimState.Run;

        ctx.Units[ctx.UnitIndex].RoutineAnim = AnimRequest.Locomotion(state);
    }

    /// <summary>Set routine anim to Idle and zero velocity.</summary>
    public static void SetIdle(ref AIContext ctx)
    {
        ctx.Units[ctx.UnitIndex].PreferredVel = Vec2.Zero;
        ctx.Units[ctx.UnitIndex].RoutineAnim = AnimRequest.Locomotion(AnimState.Idle);
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
            if (j == ctx.UnitIndex || !ctx.Units[j].Alive) continue;
            if (ctx.Units[j].Faction == myFaction) continue;
            float d = (ctx.Units[j].Position - myPos).LengthSq();
            if (d < bestDist) { bestDist = d; bestIdx = j; }
        }
        return bestIdx;
    }

    /// <summary>Resolve current combat target to unit index.</summary>
    public static int ResolveTarget(ref AIContext ctx)
    {
        var target = ctx.Units[ctx.UnitIndex].Target;
        if (!target.IsUnit) return -1;
        for (int j = 0; j < ctx.Units.Count; j++)
            if (ctx.Units[j].Id == target.UnitID && ctx.Units[j].Alive) return j;
        return -1;
    }

    /// <summary>Resolve the alert target to a unit index.</summary>
    public static int ResolveAlertTarget(ref AIContext ctx)
    {
        uint targetId = ctx.AlertTarget;
        if (targetId == GameConstants.InvalidUnit) return -1;
        for (int j = 0; j < ctx.Units.Count; j++)
            if (ctx.Units[j].Id == targetId && ctx.Units[j].Alive) return j;
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
        return baseRange + ctx.Units[ctx.UnitIndex].Stats.Length * 0.15f
            + ctx.Units[ctx.UnitIndex].Radius + ctx.Units[targetIdx].Radius;
    }
}
