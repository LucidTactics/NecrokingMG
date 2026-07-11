using Necroking.Core;

namespace Necroking.AI;

/// <summary>
/// Shared combat-routine state transitions for archetype handlers that follow
/// the canonical "chase / engage / return" pattern. Current user:
/// HordeMinionHandler. (WolfPackHandler's Fighting and DeerHerdHandler's
/// FightBack do NOT use these — their exit semantics are structurally different
/// (time-of-day routines / stance cycles, no chase-return split) and were
/// deliberately never migrated. The sentry archetypes' shared ladder lives in
/// <see cref="SentryTransitions"/>.)
///
/// Each handler used to open-code its own exit conditions and forget cases —
/// which is how the "horde unit stands still while target kites" and "chaser
/// drags the horde across the map" bugs shipped. Routines now call these
/// helpers at the top and get the common exits for free; handler-unique
/// behavior lives below the helper call.
///
/// Helpers are static, allocation-free, and return true when they transitioned
/// state (caller should return immediately — the routine was changed).
///
/// Routine changes go through <see cref="AIContext.TransitionTo"/>, so the handler's
/// OnRoutineExit/OnRoutineEnter hooks fire as well. The explicit field clears here are
/// kept as the shared policy for any handler using these helpers, hooks or not —
/// double-clearing is idempotent.
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
                    ctx.Units[ctx.UnitIndex].EngagedTarget = CombatTarget.None;
                    ctx.Units[ctx.UnitIndex].Target = CombatTarget.Unit(ctx.Units[next].Id);
                    ctx.TransitionTo(chasingRoutine);
                    return true;
                }
                // else: no enemies nearby — stay Engaged, handler will idle.
                return false;
            }
            ctx.Units[ctx.UnitIndex].Target = CombatTarget.None;
            ctx.Units[ctx.UnitIndex].EngagedTarget = CombatTarget.None;
            ctx.Units[ctx.UnitIndex].InCombat = false;
            ctx.TransitionTo(returningRoutine);
            return true;
        }

        // Alive but moved out of melee range — chase again. Hysteresis (1.2×)
        // prevents flipping at the boundary when a target hovers just at range.
        // Keep PendingAttack: the queued swing rides to its animation's impact
        // frame, where it resolves honestly — Hit/Miss if the target is still
        // near, a logged Whiff if it escaped (TryResolvePendingAttackAtImpact).
        // Clearing it here produced silent phantom swings: the anim finished
        // (PostAttackTimer is kept) but the dice never rolled and nothing was
        // logged. The old "never-resolving PendingAttack pins the unit" worry is
        // covered now — the impact frame is guaranteed inside the PostAttackTimer
        // window, and the SwingJanitor (Game1.Animation) clears anything that
        // slips through when the window expires.
        int tIdx = SubroutineSteps.ResolveTarget(ref ctx);
        if (tIdx >= 0)
        {
            float dist = (ctx.Units[tIdx].Position - ctx.MyPos).Length();
            float meleeRange = SubroutineSteps.GetMeleeRange(ref ctx, tIdx);
            if (dist > meleeRange * meleeHysteresis)
            {
                ctx.Units[ctx.UnitIndex].EngagedTarget = CombatTarget.None;
                ctx.TransitionTo(chasingRoutine);
                return true;
            }
        }

        // Leash — fire exactly at leashRadius (the red F7 circle). The earlier
        // `× 1.5` headroom was removed: the user wants "cross the leash → return"
        // semantics with no fuzzy band beyond the visible boundary.
        if (leashRadius > 0f && !frenzied)
        {
            float distToCenter = (ctx.MyPos - leashCenter).Length();
            if (distToCenter > leashRadius)
            {
                ctx.Units[ctx.UnitIndex].Target = CombatTarget.None;
                ctx.Units[ctx.UnitIndex].EngagedTarget = CombatTarget.None;
                // Keep PendingAttack AND PostAttackTimer: the in-progress swing
                // finishes planted and resolves (Hit/Miss/Whiff) at its impact
                // frame before the unit runs home (see the out-of-melee branch).
                ctx.Units[ctx.UnitIndex].InCombat = false;
                ctx.TransitionTo(returningRoutine);
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
            ctx.Units[ctx.UnitIndex].Target = CombatTarget.None;
            ctx.Units[ctx.UnitIndex].EngagedTarget = CombatTarget.None;
            ctx.Units[ctx.UnitIndex].PendingAttack = CombatTarget.None;
            // Keep PostAttackTimer — let the swing finish planted (see StandardEngagedExits).
            ctx.TransitionTo(returningRoutine);
            return true;
        }

        if (leashRadius > 0f && !frenzied)
        {
            float distToCenter = (ctx.MyPos - leashCenter).Length();
            if (distToCenter > leashRadius)
            {
                ctx.Units[ctx.UnitIndex].Target = CombatTarget.None;
                ctx.Units[ctx.UnitIndex].EngagedTarget = CombatTarget.None;
                // Keep PendingAttack + PostAttackTimer — the swing finishes planted
                // and resolves at its impact frame (see StandardEngagedExits).
                ctx.TransitionTo(returningRoutine);
                DebugLog.Log("horde_aggro",
                    $"  [unit {ctx.MyId}] leash break while chasing: distToCenter={distToCenter:F1} > leash={leashRadius:F1}");
                return true;
            }
        }

        return false;
    }
}
