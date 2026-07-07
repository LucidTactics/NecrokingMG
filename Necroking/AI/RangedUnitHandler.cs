using System;
using Necroking.Core;
using Necroking.Movement;

namespace Necroking.AI;

/// <summary>
/// Ranged unit handler for Archers and Casters.
/// Maintains distance from enemies while attacking. Uses spell/ranged attacks.
///
/// Routines:
///   0 = IdleRoaming  — wander near spawn at 30% speed
///   1 = Alert        — noticed threat
///   2 = Combat       — attack from range, kite if enemy gets close
///   3 = Return       — go back to position
///
/// Archers kite (Combat Subroutine 1): when the CLOSEST enemy — not necessarily the
/// shoot target — presses inside KiteEnterFrac of max range, they jog away from it
/// between shots and only stop to draw when the bow is ready, resuming the retreat
/// after the release. Hysteresis (enter 0.35 / exit 0.5 of max range) stops the
/// stand↔kite flip-flop at the boundary. Casters stand and cast.
/// </summary>
public class RangedUnitHandler : IArchetypeHandler
{
    private const byte RoutineIdle = 0;
    private const byte RoutineAlert = 1;
    private const byte RoutineCombat = 2;
    private const byte RoutineReturn = 3;

    // Combat subroutines
    private const byte SubEngage = 0;
    private const byte SubKite = 1;

    private const float DefaultRange = 18f;  // fallback if unit has no RangedRange stat
    private const float DefaultCooldown = 5f;
    private const float KiteEnterFrac = 0.35f; // start kiting when a threat is inside this × max range
    private const float KiteExitFrac = 0.5f;   // stop kiting once every threat is beyond this × max range
    // Movement lockout after the arrow releases — just the bow's follow-through.
    // Deliberately short: the old cooldown*0.5 lockout planted the archer for half
    // its reload, which is exactly the window kiting needs to move in.
    private const float PostShotFollowThrough = 0.6f;

    private readonly byte _archetypeId;

    public RangedUnitHandler(byte archetypeId) { _archetypeId = archetypeId; }

    public void OnSpawn(ref AIContext ctx) => SentryTransitions.SpawnAtIdle(ref ctx);

    public void OnRoutineExit(ref AIContext ctx, byte oldRoutine, byte newRoutine)
    {
        // Combat owns the target. (PendingAttack is deliberately kept — a queued arrow
        // still fires via the animation system regardless of routine.)
        if (oldRoutine == RoutineCombat)
        {
            ctx.Units[ctx.UnitIndex].Target = CombatTarget.None;
            ctx.Units[ctx.UnitIndex].EngagedTarget = CombatTarget.None;
        }
    }

    public void Update(ref AIContext ctx)
    {
        EvaluateRoutine(ref ctx);

        switch (ctx.Routine)
        {
            case RoutineIdle:   UpdateIdle(ref ctx); break;
            case RoutineAlert:  UpdateAlert(ref ctx); break;
            case RoutineCombat: UpdateCombat(ref ctx); break;
            case RoutineReturn: UpdateReturn(ref ctx); break;
        }
    }

    private static void EvaluateRoutine(ref AIContext ctx)
    {
        // Shared sentry ladder (no self-acquire; reacquire falls back to 15u,
        // keeping the current subroutine so an archer mid-kite stays kiting).
        float range = ctx.Units[ctx.UnitIndex].DetectionRange;
        var cfg = new SentryConfig(
            selfAcquireRange: 0f,
            reacquireRange: range > 0 ? range : 15f);
        SentryTransitions.EvaluateSentryRoutine(ref ctx, cfg);
    }

    private static void UpdateIdle(ref AIContext ctx)
    {
        // Roaming archer/caster: lazy stroll at half walk speed.
        SubroutineSteps.SetEffort(ref ctx, MoveEffort.Walk, 0.5f);
        SubroutineSteps.IdleRoam(ref ctx, 6f);
    }

    private static void UpdateAlert(ref AIContext ctx)
    {
        SubroutineSteps.AlertStance(ref ctx);
    }

    private void UpdateCombat(ref AIContext ctx)
    {
        int targetIdx = SubroutineSteps.ResolveTarget(ref ctx);
        if (targetIdx < 0) return;

        int i = ctx.UnitIndex;
        ref var stats = ref ctx.Units[i].Stats;
        float maxRange = stats.RangedRange.Count > 0 ? stats.RangedRange[0] : DefaultRange;
        float dist = (ctx.Units[targetIdx].Position - ctx.MyPos).Length();

        // Tick cooldown locally so the AI doesn't depend on the legacy melee combat queue.
        if (ctx.Units[i].AttackCooldown > 0f)
            ctx.Units[i].AttackCooldown = MathF.Max(0f, ctx.Units[i].AttackCooldown - ctx.Dt);

        // A queued shot plants the unit (movement gate on PendingAttack) until the
        // anim's action moment releases the arrow — keep turning toward the target
        // so the draw tracks it.
        if (!ctx.Units[i].PendingAttack.IsNone)
        {
            ctx.Units[i].PreferredVel = Vec2.Zero;
            SubroutineSteps.FacePosition(ref ctx, ctx.Units[targetIdx].Position);
            return;
        }

        if (_archetypeId == ArchetypeRegistry.ArcherUnit)
        {
            // Kite state — hysteresis on the CLOSEST enemy (not the shoot target,
            // so a flanker triggers the retreat too).
            float kiteEnter = maxRange * KiteEnterFrac;
            float kiteExit = maxRange * KiteExitFrac;
            int threatIdx = SubroutineSteps.FindClosestEnemy(ref ctx, kiteExit + 2f);
            float threatDist = threatIdx >= 0
                ? (ctx.Units[threatIdx].Position - ctx.MyPos).Length() : float.MaxValue;

            if (ctx.Subroutine == SubEngage && threatDist < kiteEnter)
                ctx.Subroutine = SubKite;
            else if (ctx.Subroutine == SubKite && threatDist > kiteExit)
                ctx.Subroutine = SubEngage;

            // Fire whenever the bow is ready and the target's in range — kiting or
            // not. The queued shot plants us this tick; the kite resumes after the
            // release (stop-turn-shoot-run, the classic kite rhythm).
            if (dist <= maxRange && TryQueueShot(ref ctx, targetIdx, dist, maxRange))
                return;

            if (ctx.Subroutine == SubKite && threatIdx >= 0)
            {
                // Jog away from the threat while the bow recharges. Pathfound
                // retreat (MoveAwayFrom) so the archer doesn't back into trees.
                SubroutineSteps.SetEffort(ref ctx, MoveEffort.Hurry);
                SubroutineSteps.MoveAwayFrom(ref ctx, ctx.Units[threatIdx].Position, kiteExit);
                return;
            }
        }

        if (dist > maxRange)
        {
            // Out of range — close in with Hurry effort (jog up to firing line,
            // not a full Sprint commit since they want to stop and shoot).
            SubroutineSteps.SetEffort(ref ctx, MoveEffort.Hurry);
            SubroutineSteps.MoveToward(ref ctx, ctx.Units[targetIdx].Position, ctx.MyMaxSpeed);
            return;
        }

        // In range, weapon recharging (or caster): hold position. Facing falls to
        // UpdateFacingAngles priority 3 (stationary with a Target → face it).
        ctx.Units[i].PreferredVel = Vec2.Zero;
        SubroutineSteps.SetLocomotionAnim(ref ctx);
    }

    /// <summary>Queue a ranged attack via PendingAttack so the animation system plays
    /// the attack anim and triggers the actual arrow spawn at the action moment
    /// (Simulation.ResolvePendingAttack handles the ranged-vs-melee dispatch).
    /// Ranged units never set EngagedTarget — that path runs the melee combat queue.
    /// Returns true when a shot was queued (the unit is planted for the draw).</summary>
    private static bool TryQueueShot(ref AIContext ctx, int targetIdx, float dist, float maxRange)
    {
        int i = ctx.UnitIndex;
        if (ctx.Units[i].AttackCooldown > 0f
            || !ctx.Units[i].PendingAttack.IsNone
            || ctx.Units[i].PostAttackTimer > 0f)
            return false;

        ref var stats = ref ctx.Units[i].Stats;

        // Pick the first ranged weapon in range. Mirrors C++ pendingRangedWeaponIdx pattern.
        int chosen = -1;
        for (int w = 0; w < stats.RangedWeapons.Count; w++)
        {
            float wRange = w < stats.RangedRange.Count ? stats.RangedRange[w] : maxRange;
            if (dist <= wRange) { chosen = w; break; }
        }
        if (chosen < 0 && stats.RangedWeapons.Count > 0) chosen = 0;

        float cooldown = (chosen >= 0 && chosen < stats.RangedCooldownTime.Count)
            ? stats.RangedCooldownTime[chosen] : DefaultCooldown;

        ctx.Units[i].PendingAttack = ctx.Units[i].Target;
        ctx.Units[i].PendingWeaponIdx = chosen;
        ctx.Units[i].PendingWeaponIsRanged = true;
        ctx.Units[i].PendingRangedTarget = ctx.Units[targetIdx].Id;
        ctx.Units[i].AttackCooldown = cooldown;
        ctx.Units[i].PostAttackTimer = PostShotFollowThrough;
        ctx.Units[i].PreferredVel = Vec2.Zero;
        SubroutineSteps.FacePosition(ref ctx, ctx.Units[targetIdx].Position);
        if (chosen >= 0 && chosen < stats.RangedWeapons.Count)
        {
            ctx.Units[i].ActionLabel = stats.RangedWeapons[chosen].Name;
            ctx.Units[i].ActionLabelTimer = cooldown * 0.5f;
        }
        return true;
    }

    private static void UpdateReturn(ref AIContext ctx) => SentryTransitions.UpdateReturn(ref ctx);

    public string GetRoutineName(byte routine) => routine switch
    {
        RoutineIdle => "IdleRoaming",
        RoutineAlert => "Alert",
        RoutineCombat => "Combat",
        RoutineReturn => "Return",
        _ => $"Unknown({routine})"
    };

    public string GetSubroutineName(byte routine, byte subroutine) => routine switch
    {
        RoutineCombat => subroutine switch
        {
            SubEngage => "Engage",
            SubKite => "Kiting",
            _ => $"Unknown({subroutine})"
        },
        _ => subroutine == 0 ? "Default" : $"Unknown({subroutine})"
    };
}
