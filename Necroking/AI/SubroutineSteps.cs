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
        MoveToward(ref ctx, ctx.Units[targetIdx].Position, ctx.MyMaxSpeed);
    }

    public static bool MoveToTarget_InRange(ref AIContext ctx, float range)
    {
        int targetIdx = ResolveTarget(ref ctx);
        if (targetIdx < 0) return false;
        return Vec2.WithinRange(ctx.Units[targetIdx].Position, ctx.MyPos, range);
    }

    /// <summary>Pathfind toward MoveTarget position.</summary>
    public static void MoveToPosition(ref AIContext ctx, float speed)
    {
        MoveToward(ref ctx, ctx.Units[ctx.UnitIndex].MoveTarget, speed);
    }

    public static bool MoveToPosition_Arrived(ref AIContext ctx, float threshold = 1f)
    {
        return Vec2.WithinRange(ctx.Units[ctx.UnitIndex].MoveTarget, ctx.MyPos, threshold);
    }

    /// <summary>Back away from alert target to maintain distance.</summary>
    public static void MoveAwayFrom(ref AIContext ctx, Vec2 threatPos, float desiredDist)
    {
        float dist = (ctx.MyPos - threatPos).Length();
        if (dist < desiredDist && dist > 0.01f)
        {
            var awayDir = (ctx.MyPos - threatPos) * (1f / dist);
            Vec2 fleeDest = ctx.MyPos + awayDir * (desiredDist - dist + 3f);
            MoveToward(ref ctx, fleeDest, ctx.MyMaxSpeed);
        }
        else
            ctx.Units[ctx.UnitIndex].PreferredVel = Vec2.Zero;
    }

    /// <summary>Walk-then-idle roaming pattern. Walk to random point at walkSpeed,
    /// idle for random duration, repeat. Uses Subroutine 0=walking, 1=idle.
    /// All movement goes through pathfinding.</summary>
    public static void IdleRoam(ref AIContext ctx, float roamRadius)
    {
        int i = ctx.UnitIndex;
        Vec2 center = ctx.Units[i].SpawnPosition;
        bool territory = false;

        // Zone-owned herds roam around a shared waypoint that itself wanders the
        // territory rect, so the whole herd drifts around its zone together instead
        // of each member orbiting its own spawn point forever.
        var sq = ctx.MySquad;
        if (sq != null && sq.HasTerritory)
        {
            territory = true;
            if (ctx.GameTime >= sq.NextWaypointTime)
                PickTerritoryWaypoint(sq, ref ctx);
            center = sq.TerritoryWaypoint;
            roamRadius = MathF.Min(roamRadius,
                MathF.Max(2f, MathF.Min(sq.TerritoryHalfW, sq.TerritoryHalfH)));
        }

        if (ctx.Subroutine == 0) // walking to point
        {
            Vec2 target = ctx.Units[i].MoveTarget;
            // Move at the unit's resolved cap — the stroll speed is owned entirely by
            // the effort the caller set (e.g. SetEffort(Walk, 0.5) → half walk speed).
            // Previously this also multiplied by a separate walkSpeedFraction, which
            // double-penalised the effort system (deer "0.6" cap actually moved 0.18).
            MoveToward(ref ctx, target, ctx.MyMaxSpeed);
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

                    // Keep territory herds inside their zone rect (the waypoint sits
                    // inside it, but waypoint + roam offset can poke past the edge).
                    if (territory)
                    {
                        candidate.X = Math.Clamp(candidate.X,
                            sq!.TerritoryCenter.X - sq.TerritoryHalfW, sq.TerritoryCenter.X + sq.TerritoryHalfW);
                        candidate.Y = Math.Clamp(candidate.Y,
                            sq.TerritoryCenter.Y - sq.TerritoryHalfH, sq.TerritoryCenter.Y + sq.TerritoryHalfH);
                    }

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

    /// <summary>Pick the squad's next shared territory waypoint: a random walkable point
    /// inside the territory rect (slightly inset), valid for the next ~20-40 s. Falls back
    /// to the territory center when no walkable spot is found this tick.</summary>
    private static void PickTerritoryWaypoint(Squad sq, ref AIContext ctx)
    {
        var grid = ctx.Pathfinder?.Grid;
        uint seed = (uint)ctx.FrameNumber * 2654435761u ^ sq.Id * 2246822519u;
        Vec2 pick = sq.TerritoryCenter;
        for (int a = 0; a < 12; a++)
        {
            seed = seed * 1664525u + 1013904223u;
            float fx = ((seed % 1000u) / 1000f - 0.5f) * 2f;
            seed = seed * 1664525u + 1013904223u;
            float fy = ((seed % 1000u) / 1000f - 0.5f) * 2f;
            var cand = new Vec2(
                sq.TerritoryCenter.X + fx * sq.TerritoryHalfW * 0.8f,
                sq.TerritoryCenter.Y + fy * sq.TerritoryHalfH * 0.8f);
            if (grid == null || IsPointWalkable(grid, cand, 0.6f)) { pick = cand; break; }
        }
        sq.TerritoryWaypoint = pick;
        sq.NextWaypointTime = ctx.GameTime + 20f + seed % 20u;
    }

    /// <summary>
    /// True if a unit of the given radius can stand at worldPos without any blocked
    /// tile in its footprint. Delegates to the shared TileGrid probe so AI-picked
    /// destinations match what the movement system will actually accept.
    /// </summary>
    public static bool IsPointWalkable(World.TileGrid grid, Vec2 pos, float radius)
        => !grid.OverlapsImpassable(pos.X, pos.Y, radius);

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
        if (targetIdx < 0)
        {
            ctx.Units[ctx.UnitIndex].PreferredVel = Vec2.Zero;
            SetLocomotionAnim(ref ctx);
            return;
        }

        float dist = (ctx.Units[targetIdx].Position - ctx.MyPos).Length();
        float attackRange = GetMeleeRange(ref ctx, targetIdx);

        if (dist > attackRange * 1.5f)
        {
            MoveToward(ref ctx, ctx.Units[targetIdx].Position, ctx.MyMaxSpeed);
        }
        else
        {
            // In melee range — hold position; the central gait pass sees zero
            // intent and drops the locomotion channel to Idle.
            ctx.Units[ctx.UnitIndex].PreferredVel = Vec2.Zero;
            SetLocomotionAnim(ref ctx);
        }

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
                ctx.Units[i].PreferredVel = awayDir * ctx.MyMaxSpeed;
            }
            else
                ctx.Units[i].PreferredVel = Vec2.Zero;
        }
        else
            ctx.Units[i].PreferredVel = Vec2.Zero;
        SetLocomotionAnim(ref ctx);
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
                ctx.Units[ctx.UnitIndex].PreferredVel = awayDir * ctx.MyMaxSpeed * 0.5f;
            }
            else
                ctx.Units[ctx.UnitIndex].PreferredVel = Vec2.Zero;
        }
        else
            ctx.Units[ctx.UnitIndex].PreferredVel = Vec2.Zero;
        SetLocomotionAnim(ref ctx);
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
        int j = UnitUtil.ResolveUnitIndex(ctx.Units, ctx.AlertTarget);
        if (j < 0) return;
        // Rate-capped turn toward the alert target so the head-snap to face a
        // freshly-noticed threat respects the unit's TurnSpeed.
        Movement.FacingUtil.TurnTowardPosition(ctx.Units[i], ctx.Units[j].Position, ctx.Dt, ctx.GameData);
    }

    /// <summary>Face a specific world position — rate-capped by unit TurnSpeed.</summary>
    public static void FacePosition(ref AIContext ctx, Vec2 target)
    {
        Movement.FacingUtil.TurnTowardPosition(ctx.Units[ctx.UnitIndex], target, ctx.Dt, ctx.GameData);
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
            Vec2 dir = ctx.Pathfinder.GetDirection(myPos, target, (uint)ctx.FrameNumber, sizeTier, ctx.Units[i].Id);
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

        // Return the routine channel to locomotion; the actual tier is picked
        // centrally (post-movement) by Locomotion.UpdateLocoVectorsAndGait from
        // the smoothed loco vector.
        SetLocomotionAnim(ref ctx);
    }

    /// <summary>
    /// Mark the routine channel as locomotion. The actual Idle/Walk/Jog/Run
    /// tier is picked centrally each tick by
    /// <see cref="Locomotion.UpdateLocoVectorsAndGait"/> (which only steers
    /// loco-class RoutineAnims) — this just ensures the channel isn't stuck on
    /// a deliberate non-loco pose (Feed, Sleep, alert) after a routine ends.
    /// </summary>
    public static void SetLocomotionAnim(ref AIContext ctx)
    {
        AnimState prev = ctx.Units[ctx.UnitIndex].RoutineAnim.State;
        bool locoClass = prev == AnimState.Idle || prev == AnimState.Walk
            || prev == AnimState.Jog || prev == AnimState.Run;
        if (!locoClass)
            ctx.Units[ctx.UnitIndex].RoutineAnim = AnimRequest.Locomotion(AnimState.Idle);
    }

    /// <summary>Set routine anim to Idle and zero velocity.</summary>
    public static void SetIdle(ref AIContext ctx)
    {
        ctx.Units[ctx.UnitIndex].PreferredVel = Vec2.Zero;
        ctx.Units[ctx.UnitIndex].RoutineAnim = AnimRequest.Locomotion(AnimState.Idle);
    }

    /// <summary>Declare the unit's movement effort — thin forwarder to
    /// <see cref="Locomotion.SetEffort"/>, the single home for effort→speed.
    /// Intent only: the actual MaxSpeed (ramped, with all modifiers) is derived
    /// by Locomotion.UpdateSpeeds each tick, so it persists correctly for
    /// amortized AI that doesn't re-issue effort every frame.</summary>
    public static void SetEffort(ref AIContext ctx, MoveEffort effort, float? routineCapMult = null)
        => Locomotion.SetEffort(ctx.Units[ctx.UnitIndex], effort, routineCapMult);

    /// <summary>Compute the velocity cap a given effort would give this unit,
    /// without mutating anything (forwarder to
    /// <see cref="Locomotion.ResolveEffortSpeed"/>). Useful when a routine
    /// needs the cap for clamping <c>PreferredVel</c> but isn't changing
    /// effort itself.</summary>
    public static float ResolveEffortSpeed(ref AIContext ctx, MoveEffort effort,
        float? routineCapMult = null)
        => Locomotion.ResolveEffortSpeed(ctx.Units[ctx.UnitIndex], ctx.GameData, effort, routineCapMult);

    /// <summary>Find closest enemy unit (different faction, alive).</summary>
    // Reused across every FindClosestEnemy call to skip the allocation on the
    // AI hot path; we clear it at the start of each scan.
    private static readonly System.Collections.Generic.List<uint> _nearbyScratch = new();

    public static int FindClosestEnemy(ref AIContext ctx, float maxRange)
    {
        // Canonical scan: WorldQuery.NearestEnemyOf (quadtree-backed with its own
        // linear fallback on an empty tree). The loops below are only the fallback
        // for minimal contexts (OnSpawn / hand-built scenario contexts) that carry
        // no Query handle. maxRange <= 0 keeps the old "no range = no result"
        // semantics rather than WorldQuery's unbounded-scan meaning.
        if (ctx.Query != null && maxRange > 0f)
            return ctx.Query.NearestEnemyOf(ctx.UnitIndex, maxRange);

        float bestDist = maxRange * maxRange;
        int bestIdx = -1;
        var myFaction = ctx.MyFaction;
        var myPos = ctx.MyPos;

        // Quadtree pass: only cross-faction units come back thanks to the
        // faction mask. Falls back to a linear scan if no quadtree is attached
        // (e.g. some legacy AI paths still run without one).
        if (ctx.Quadtree != null)
        {
            _nearbyScratch.Clear();
            ctx.Quadtree.QueryRadiusByFaction(myPos, maxRange,
                FactionMaskExt.AllExcept(myFaction), _nearbyScratch);
            foreach (uint nid in _nearbyScratch)
            {
                int j = UnitUtil.ResolveUnitIndex(ctx.Units, nid);
                if (j < 0 || j == ctx.UnitIndex) continue;
                float d = (ctx.Units[j].Position - myPos).LengthSq();
                if (d < bestDist) { bestDist = d; bestIdx = j; }
            }
            return bestIdx;
        }

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
        return UnitUtil.ResolveUnitIndex(ctx.Units, target.UnitID);
    }

    /// <summary>Resolve the alert target to a unit index.</summary>
    public static int ResolveAlertTarget(ref AIContext ctx)
    {
        return UnitUtil.ResolveUnitIndex(ctx.Units, ctx.AlertTarget);
    }

    /// <summary>Check if current target is alive.</summary>
    public static bool IsTargetAlive(ref AIContext ctx)
    {
        return ResolveTarget(ref ctx) >= 0;
    }

    // Forwards to the shared MeleeRangeUtil so AI and the combat sim use one formula
    // and one fallback. NOTE: the null-GameData fallback was 1.5f here vs 0.8f in the
    // sim — now unified to 0.8f (only affects null-GameData tests; live play is unchanged).
    public static float GetMeleeRange(ref AIContext ctx, int targetIdx)
        => Necroking.GameSystems.Combat.MeleeRangeUtil.Compute(ctx.Units, ctx.UnitIndex, targetIdx, ctx.GameData);
}
