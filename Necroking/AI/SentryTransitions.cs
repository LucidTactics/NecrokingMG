using Necroking.Core;
using Necroking.Lib;
using Necroking.Movement;

namespace Necroking.AI;

/// <summary>
/// Per-handler knobs for the shared sentry ladder (<see cref="SentryTransitions"/>).
/// The caller owns the data; the helper owns the mechanics.
/// </summary>
public readonly struct SentryConfig
{
    /// <summary>Engage an enemy inside this range even without an awareness
    /// escalation (legacy "zap anything in range" behavior). 0 = disabled.
    /// CombatUnit/Ranged: 0; Caster: spell.Range (re-fetched each tick);
    /// SoloPredator: AggroRange.</summary>
    public readonly float SelfAcquireRange;

    /// <summary>Search radius for reacquiring a new target when the current one
    /// dies mid-combat. The handler computes its own fallback (CombatUnit:
    /// DetectionRange or 12; Ranged/Caster: DetectionRange or 15; SoloPredator:
    /// AggroRange). Frenzied units search at least <see cref="FrenzySearchRange"/>.</summary>
    public readonly float ReacquireRange;

    /// <summary>Reset Subroutine to 0 when a new target is acquired after a kill
    /// (CombatUnit re-enters chase; SoloPredator re-enters engage). Ranged/Caster
    /// keep their current subroutine (e.g. an archer stays kiting).</summary>
    public readonly bool ReacquireResetsSubroutine;

    /// <summary>Also zero SubroutineTimer on reacquire (SoloPredator only).</summary>
    public readonly bool ReacquireResetsTimer;

    public SentryConfig(float selfAcquireRange, float reacquireRange,
        bool reacquireResetsSubroutine = false, bool reacquireResetsTimer = false)
    {
        SelfAcquireRange = selfAcquireRange;
        ReacquireRange = reacquireRange;
        ReacquireResetsSubroutine = reacquireResetsSubroutine;
        ReacquireResetsTimer = reacquireResetsTimer;
    }
}

/// <summary>
/// Shared skeleton for the "sentry" archetypes — units that idle at a post,
/// escalate through the awareness system, fight, and walk home:
/// CombatUnitHandler (PatrolSoldier/GuardStationary/ArmyUnit), RangedUnitHandler
/// (ArcherUnit), CasterUnitHandler (CasterUnit), SoloPredatorHandler
/// (SoloPredator/AmbushPredator). All four use the same routine byte layout,
/// which these helpers assume: 0 = Idle, 1 = Alert, 2 = Combat, 3 = Return.
///
/// Owns the Idle→Alert→Combat→Return ladder, the threat-aware return walk, and
/// the spawn-at-idle initializer. Handler-specific behavior — the whole Combat
/// routine, idle flavor (patrol/guard/roam), and OnRoutineExit field policy
/// (deliberately different per handler, e.g. Ranged keeps PendingAttack so a
/// queued arrow still fires) — stays in the handlers.
///
/// Frenzy lives HERE so every sentry archetype gets it (it used to exist only
/// in CombatUnitHandler — a frenzied archer/priest/predator calmly walked home
/// when its target died): frenzied units search wide on a kill instead of
/// returning, and bail out of Return back to Idle.
/// </summary>
public static class SentryTransitions
{
    // The shared routine byte layout (all four sentry handlers use these values).
    public const byte RoutineIdle = 0;
    public const byte RoutineAlert = 1;
    public const byte RoutineCombat = 2;
    public const byte RoutineReturn = 3;

    /// <summary>Minimum reacquire search radius for frenzied units.</summary>
    public const float FrenzySearchRange = 30f;

    /// <summary>The shared 4-line OnSpawn body: stamp SpawnPosition, start at Idle.</summary>
    public static void SpawnAtIdle(ref AIContext ctx)
    {
        ctx.Units[ctx.UnitIndex].SpawnPosition = ctx.MyPos;
        ctx.Routine = RoutineIdle;
        ctx.Subroutine = 0;
        ctx.SubroutineTimer = 0f;
    }

    /// <summary>
    /// The sentry routine ladder, evaluated at the top of every Update:
    ///   - AlertState ≥ Alert while Idle → Alert routine.
    ///   - Aggressive with a valid AlertTarget → Combat, Target stamped.
    ///   - Enemy inside SelfAcquireRange while Idle/Alert → Combat (when enabled).
    ///   - Combat with a dead target → reacquire (frenzied: wide search) or
    ///     Return + awareness reset. Frenzied units with no targets stay in
    ///     Combat and recheck next tick.
    ///   - Unaware while Alert → Idle.
    /// Routine changes go through <see cref="AIContext.TransitionTo"/> so the
    /// handler's exit/enter hooks fire (Combat exits clear the melee-lock fields
    /// per each handler's own OnRoutineExit policy).
    /// </summary>
    public static void EvaluateSentryRoutine(ref AIContext ctx, in SentryConfig cfg)
    {
        byte alert = ctx.AlertState;

        // Alert → enter Alert routine
        if (alert >= (byte)UnitAlertState.Alert && ctx.Routine == RoutineIdle)
        {
            ctx.TransitionTo(RoutineAlert);
            return;
        }

        // Aggressive → enter Combat
        if (alert == (byte)UnitAlertState.Aggressive && ctx.Routine <= RoutineAlert)
        {
            if (ctx.AlertTarget != GameConstants.InvalidUnit)
            {
                ctx.TransitionTo(RoutineCombat);
                ctx.Units[ctx.UnitIndex].Target = CombatTarget.Unit(ctx.AlertTarget);
                return;
            }
        }

        // Self-acquire: an enemy inside range is engaged even without an
        // awareness escalation (covers scenario-spawned units with no
        // awareness config, and the legacy in-range aggro behaviors).
        if (cfg.SelfAcquireRange > 0f && ctx.Routine <= RoutineAlert)
        {
            int enemy = SubroutineSteps.FindClosestEnemy(ref ctx, cfg.SelfAcquireRange);
            if (enemy >= 0)
            {
                ctx.TransitionTo(RoutineCombat);
                ctx.Units[ctx.UnitIndex].Target = CombatTarget.Unit(ctx.Units[enemy].Id);
                return;
            }
        }

        // Target dead in combat → reacquire or return (unless frenzied)
        if (ctx.Routine == RoutineCombat && !SubroutineSteps.IsTargetAlive(ref ctx))
        {
            // Frenzied units search wider and never return to leash.
            bool frenzied = ctx.Units[ctx.UnitIndex].Frenzied;
            float searchRange = frenzied
                ? System.MathF.Max(cfg.ReacquireRange, FrenzySearchRange)
                : cfg.ReacquireRange;
            int next = SubroutineSteps.FindClosestEnemy(ref ctx, searchRange);
            if (next >= 0)
            {
                ctx.Units[ctx.UnitIndex].Target = CombatTarget.Unit(ctx.Units[next].Id);
                if (cfg.ReacquireResetsSubroutine) ctx.Subroutine = 0;
                if (cfg.ReacquireResetsTimer) ctx.SubroutineTimer = 0f;
            }
            else if (!frenzied)
            {
                // The handler's OnRoutineExit(Combat) clears its melee-lock fields.
                ctx.TransitionTo(RoutineReturn);
                ctx.AlertState = (byte)UnitAlertState.Unaware;
                ctx.AlertTarget = GameConstants.InvalidUnit;
            }
            // else frenzied with no targets: stay in combat routine, recheck next tick
        }

        // Threat gone → back to idle
        if (alert == (byte)UnitAlertState.Unaware && ctx.Routine == RoutineAlert)
        {
            ctx.TransitionTo(RoutineIdle);
        }
    }

    /// <summary>
    /// The shared Return routine: walk back to SpawnPosition (Sprint while
    /// threats are still detected, Walk when clear), transition to Idle within
    /// 2 units. Frenzied units never return — they bail straight back to Idle
    /// (where self-acquire / awareness re-engages them).
    /// </summary>
    public static void UpdateReturn(ref AIContext ctx)
    {
        if (ctx.Units[ctx.UnitIndex].Frenzied)
        {
            ctx.TransitionTo(RoutineIdle);
            return;
        }

        ctx.Units[ctx.UnitIndex].EngagedTarget = CombatTarget.None;
        ctx.Units[ctx.UnitIndex].InCombat = false;

        Vec2 returnPos = ctx.Units[ctx.UnitIndex].SpawnPosition;
        if ((ctx.MyPos - returnPos).Length() > 2f)
        {
            // Effort depends on whether threats are still detected. Awareness
            // state Alert/Aggressive means enemies haven't fully cleared yet —
            // retreat under pursuit warrants Sprint. Otherwise a stroll home.
            bool stillThreatened = ctx.AlertState >= (byte)UnitAlertState.Alert;
            SubroutineSteps.SetEffort(ref ctx, stillThreatened ? MoveEffort.Sprint : MoveEffort.Walk);
            SubroutineSteps.MoveToward(ref ctx, returnPos, ctx.MyMaxSpeed);
        }
        else
        {
            ctx.TransitionTo(RoutineIdle);
            ctx.Units[ctx.UnitIndex].PreferredVel = Vec2.Zero;
        }
    }
}
