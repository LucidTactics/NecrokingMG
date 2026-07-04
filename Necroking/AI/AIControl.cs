using Necroking.Core;
using Necroking.Movement;

namespace Necroking.AI;

/// <summary>
/// Central AI transition control — the armor around Routine changes.
///
/// A handler changes its own unit's routine via <see cref="AIContext.TransitionTo"/> (the
/// choke point: fires OnRoutineExit/OnRoutineEnter hooks, resets Subroutine/SubroutineTimer).
/// Everything else goes through here:
///
///   • <see cref="TransitionUnit"/> — a handler pushing ANOTHER unit into a routine
///     (e.g. deer herd flee propagation).
///   • <see cref="Interrupt"/> — an external system (physics launch, worker (un)assign,
///     player WASD-cancel, wolf-hunt recast) yanking a unit out of whatever it's doing.
///     Fires the exit hook, clears the combat/pin fields that are known to wedge units
///     (a never-resolving PendingAttack pins movement forever), and resets to routine 0.
///   • <see cref="StartRoutine"/> — an external system starting a specific routine
///     (player orders, spells, UI). Unlike TransitionTo, calling it while already in the
///     routine RESTARTS it (full exit/enter cycle) — a re-issued order resets its timers.
///
/// Writing Unit.Routine directly anywhere else is a bug: it skips exit cleanup, which is
/// exactly how "unit locked in place by a stale PendingAttack/EngagedTarget" bugs shipped.
/// The one exception is OnSpawn initializers (fresh unit — no old state to clean up).
/// </summary>
public static class AIControl
{
    /// <summary>When true, every routine transition is appended to log/ai_transition.log
    /// with archetype + routine names. Toggle via the `ai_trace` dev command. Off by
    /// default — DebugLog writes to disk unconditionally.</summary>
    public static bool TraceTransitions;

    /// <summary>Transition a DIFFERENT unit than the one this context is for, reusing the
    /// context's world services. Same semantics as <see cref="AIContext.TransitionTo"/>.</summary>
    public static void TransitionUnit(ref AIContext ctx, int unitIndex, byte routine,
        byte subroutine = 0, float timer = 0f)
    {
        var other = ctx;
        other.UnitIndex = unitIndex;
        other.TransitionTo(routine, subroutine, timer);
    }

    /// <summary>
    /// The canonical "external system seizes this unit" entry point. Fires the archetype's
    /// exit hook for the current routine, clears the combat pin fields
    /// (Target/EngagedTarget/PendingAttack/InCombat), and resets to routine 0 (every
    /// archetype's default/idle). <paramref name="reason"/> shows up in the ai_trace log.
    /// </summary>
    public static void Interrupt(ref AIContext ctx, string reason)
    {
        int i = ctx.UnitIndex;
        byte old = ctx.Units[i].Routine;
        var handler = ArchetypeRegistry.Get(ctx.Units[i].Archetype);

        if (old != 0)
            handler?.OnRoutineExit(ref ctx, old, 0);

        // Universal pin clears — a unit yanked by an external system must never keep a
        // queued swing or an engaged lock, whatever routine it was in.
        ctx.Units[i].Target = CombatTarget.None;
        ctx.Units[i].EngagedTarget = CombatTarget.None;
        ctx.Units[i].PendingAttack = CombatTarget.None;
        ctx.Units[i].InCombat = false;

        ctx.Units[i].Routine = 0;
        ctx.Units[i].Subroutine = 0;
        ctx.Units[i].SubroutineTimer = 0f;

        if (old != 0)
            handler?.OnRoutineEnter(ref ctx, old, 0);

        if (TraceTransitions && handler != null)
            DebugLog.Log("ai_transition",
                $"[unit {ctx.Units[i].Id}] {ArchetypeRegistry.GetName(ctx.Units[i].Archetype)}: " +
                $"{handler.GetRoutineName(old)} -> {handler.GetRoutineName(0)} (interrupt: {reason})");
    }

    /// <summary>Interrupt overload for callers that only have the unit arrays (physics,
    /// job systems, Game1 UI). Hooks must only touch unit fields, so a minimal context is
    /// sufficient — see the contract note on <see cref="IArchetypeHandler.OnRoutineExit"/>.</summary>
    public static void Interrupt(UnitArrays units, int idx, string reason)
    {
        var ctx = new AIContext { Units = units, UnitIndex = idx };
        Interrupt(ref ctx, reason);
    }

    /// <summary>
    /// External-intent routine start (player orders, spells, UI). Same as
    /// <see cref="AIContext.TransitionTo"/> except a same-routine call RESTARTS the routine
    /// (full exit/enter hook cycle + Subroutine/SubroutineTimer stamp) instead of no-oping,
    /// so a re-issued order behaves like a fresh one. Set the routine's parameter fields
    /// (MoveTarget etc.) AFTER this call — the exit hook clears the old routine's fields.
    /// </summary>
    public static void StartRoutine(ref AIContext ctx, byte routine, byte subroutine = 0, float timer = 0f)
    {
        if (ctx.TransitionTo(routine, subroutine, timer)) return;

        // Already in the routine — restart it.
        int i = ctx.UnitIndex;
        var handler = ArchetypeRegistry.Get(ctx.Units[i].Archetype);
        handler?.OnRoutineExit(ref ctx, routine, routine);
        ctx.Units[i].Subroutine = subroutine;
        ctx.Units[i].SubroutineTimer = timer;
        handler?.OnRoutineEnter(ref ctx, routine, routine);

        if (TraceTransitions && handler != null)
            DebugLog.Log("ai_transition",
                $"[unit {ctx.Units[i].Id}] {ArchetypeRegistry.GetName(ctx.Units[i].Archetype)}: " +
                $"restart {handler.GetRoutineName(routine)}");
    }

    /// <summary>StartRoutine overload for callers that only have the unit arrays.</summary>
    public static void StartRoutine(UnitArrays units, int idx, byte routine, byte subroutine = 0, float timer = 0f)
    {
        var ctx = new AIContext { Units = units, UnitIndex = idx };
        StartRoutine(ref ctx, routine, subroutine, timer);
    }
}
