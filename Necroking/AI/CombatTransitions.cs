using Necroking.Core;

namespace Necroking.AI;

/// <summary>
/// Shared combat-routine state transitions used by archetype handlers that
/// follow the canonical "chase / engage / return" pattern (HordeMinionHandler,
/// WolfPackHandler's Fighting routine, DeerHerdHandler's FightBack, etc.).
///
/// Each handler used to open-code its own exit conditions and forget cases —
/// which is how the "horde unit stands still while target kites" and "chaser
/// drags the horde across the map" bugs shipped. Routines now call these
/// helpers at the top and get the common exits for free; handler-unique
/// behavior lives below the helper call.
///
/// Helpers are static, allocation-free, and return true when they transitioned
/// state (caller should return immediately — the routine was changed).
/// </summary>
public static class CombatTransitions
{
    /// <summary>
    /// Canonical exits from an Engaged (melee) routine:
    ///   - Target dead → Returning (or Chasing if frenzied with more targets).
    ///   - Target alive but out of melee range → Chasing.
    ///   - Leashed too far from horde center → Returning (skipped if frenzied).
    ///
    /// Returns true if a transition was applied. Caller should `return;`
    /// immediately in that case. Otherwise the routine continues with its
    /// handler-specific behavior (AttackTarget, etc.).
    ///
    /// <paramref name="chasingRoutine"/> and <paramref name="returningRoutine"/>
    /// are the handler's routine enum values for those states — different
    /// handlers use different byte indices for "chase" and "return".
    ///
    /// <paramref name="leashRadius"/> zero disables the leash check (for
    /// handlers whose combat routines aren't horde-leashed).
    /// </summary>
    public static bool StandardEngagedExits(
        ref AIContext ctx,
        byte chasingRoutine,
        byte returningRoutine,
        float meleeHysteresis = 1.2f,
        float leashRadius = 0f,
        Vec2 leashCenter = default)
    {
        bool frenzied = ctx.Units[ctx.UnitIndex].Frenzied;

        // Dead target
        if (!SubroutineSteps.IsTargetAlive(ref ctx))
        {
            if (frenzied)
            {
                int next = SubroutineSteps.FindClosestEnemy(ref ctx, 30f);
                if (next >= 0)
                {
                    ctx.Units[ctx.UnitIndex].Target = CombatTarget.Unit(ctx.Units[next].Id);
                    ctx.Routine = chasingRoutine;
                    ctx.Units[ctx.UnitIndex].EngagedTarget = CombatTarget.None;
                    return true;
                }
                // else: no enemies nearby — stay Engaged, handler will idle.
                return false;
            }
            ctx.Routine = returningRoutine;
            ctx.Units[ctx.UnitIndex].Target = CombatTarget.None;
            ctx.Units[ctx.UnitIndex].EngagedTarget = CombatTarget.None;
            ctx.Units[ctx.UnitIndex].InCombat = false;
            return true;
        }

        // Alive but moved out of melee range — chase again. Hysteresis (1.2×)
        // prevents flipping at the boundary when a target hovers just at range.
        // Also clear PendingAttack + PostAttackTimer: a queued swing on a target
        // that's now out of range will never resolve visually AND keeps the unit
        // pinned via the movement-lockout (Simulation.UpdateMovement zeroes
        // Velocity while PendingAttack or PostAttackTimer is set).
        int tIdx = SubroutineSteps.ResolveTarget(ref ctx);
        if (tIdx >= 0)
        {
            float dist = (ctx.Units[tIdx].Position - ctx.MyPos).Length();
            float meleeRange = SubroutineSteps.GetMeleeRange(ref ctx, tIdx);
            if (dist > meleeRange * meleeHysteresis)
            {
                ctx.Routine = chasingRoutine;
                ctx.Units[ctx.UnitIndex].EngagedTarget = CombatTarget.None;
                ctx.Units[ctx.UnitIndex].PendingAttack = CombatTarget.None;
                ctx.Units[ctx.UnitIndex].PostAttackTimer = 0f;
                return true;
            }
        }

        // Leash
        if (leashRadius > 0f && !frenzied)
        {
            float distToCenter = (ctx.MyPos - leashCenter).Length();
            if (distToCenter > leashRadius * 1.5f)
            {
                ctx.Routine = returningRoutine;
                ctx.Units[ctx.UnitIndex].Target = CombatTarget.None;
                ctx.Units[ctx.UnitIndex].EngagedTarget = CombatTarget.None;
                ctx.Units[ctx.UnitIndex].PendingAttack = CombatTarget.None;
                ctx.Units[ctx.UnitIndex].PostAttackTimer = 0f;
                ctx.Units[ctx.UnitIndex].InCombat = false;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Canonical exits from a Chasing (move-toward-target) routine:
    ///   - Target dead → Returning.
    ///   - Leashed too far from horde center → Returning (skipped if frenzied).
    ///
    /// Does NOT do the "target in melee range → Engaged" transition — that's
    /// typically coupled with the handler's own MoveToward logic and lives in
    /// the handler.
    /// </summary>
    public static bool StandardChasingExits(
        ref AIContext ctx,
        byte returningRoutine,
        float leashRadius = 0f,
        Vec2 leashCenter = default)
    {
        bool frenzied = ctx.Units[ctx.UnitIndex].Frenzied;

        if (!SubroutineSteps.IsTargetAlive(ref ctx))
        {
            ctx.Routine = returningRoutine;
            ctx.Units[ctx.UnitIndex].Target = CombatTarget.None;
            ctx.Units[ctx.UnitIndex].EngagedTarget = CombatTarget.None;
            ctx.Units[ctx.UnitIndex].PendingAttack = CombatTarget.None;
            ctx.Units[ctx.UnitIndex].PostAttackTimer = 0f;
            return true;
        }

        if (leashRadius > 0f && !frenzied)
        {
            float distToCenter = (ctx.MyPos - leashCenter).Length();
            if (distToCenter > leashRadius * 1.5f)
            {
                ctx.Routine = returningRoutine;
                ctx.Units[ctx.UnitIndex].Target = CombatTarget.None;
                ctx.Units[ctx.UnitIndex].EngagedTarget = CombatTarget.None;
                ctx.Units[ctx.UnitIndex].PendingAttack = CombatTarget.None;
                ctx.Units[ctx.UnitIndex].PostAttackTimer = 0f;
                DebugLog.Log("horde_aggro",
                    $"  [unit {ctx.MyId}] leash break while chasing: distToCenter={distToCenter:F1} > leash*1.5={leashRadius * 1.5f:F1}");
                return true;
            }
        }

        return false;
    }
}
