using System;
using Necroking.Core;
using Necroking.Movement;

namespace Necroking.AI;

/// <summary>
/// Rat pack AI archetype: low-IQ aggressive vermin. Charges in erratically, bites,
/// then reflexively skitters back a short distance (a fear reflex keyed off its OWN
/// attack — NOT off attack-cooldown like the wolf) and immediately rushes back in.
///
/// "Scared but aggressive": it stays in the target's face (doesn't kite at range like
/// a wolf) but flinches after every bite. Brave in numbers — swarm aggro comes free
/// from AwarenessSystem's group-alert propagation (GroupAlertRadius), and it gangs up
/// on whatever enemy a packmate is already biting. When a packmate dies nearby it
/// panics (PanicTimer) and its skitters retreat farther for a moment.
///
/// Routines:
///   0 = Scurry   — idle: erratic wander near spawn
///   1 = Fighting — dart-in / bite / skitter-out cycle
///
/// Fighting subroutines:
///   0 = RushIn   — reckless erratic sprint at the target
///   1 = Bite     — strike once (entered only when the attack is ready)
///   2 = Skitter  — reflexive hop back (farther while panicked), then back to RushIn
/// </summary>
public class RatPackHandler : IArchetypeHandler
{
    // Routine indices
    private const byte RoutineScurry = 0;
    private const byte RoutineFighting = 1;

    // Fighting subroutine indices
    private const byte FightRushIn = 0;
    private const byte FightBite = 1;
    private const byte FightSkitter = 2;

    // Tuning
    private const float ScurryRadius = 8f;        // idle wander radius around spawn
    private const float SkitterDist = 2.0f;       // normal reflexive hop-back distance
    private const float PanicSkitterDist = 5.0f;  // farther retreat while panicked
    private const float JitterAmplitudeRad = 0.38f; // ~22° erratic weave on the approach
    private const float JitterFreq = 7f;          // weave oscillation speed

    public void OnSpawn(ref AIContext ctx)
    {
        ctx.Units[ctx.UnitIndex].SpawnPosition = ctx.MyPos;
        ctx.Units[ctx.UnitIndex].MoveTarget = ctx.MyPos;
        ctx.Routine = RoutineScurry;
        ctx.Subroutine = 0;
        ctx.SubroutineTimer = 0f;
    }

    public void Update(ref AIContext ctx)
    {
        // Panic decays over time.
        if (ctx.Units[ctx.UnitIndex].PanicTimer > 0f)
            ctx.Units[ctx.UnitIndex].PanicTimer = MathF.Max(0f, ctx.Units[ctx.UnitIndex].PanicTimer - ctx.Dt);

        EvaluateRoutine(ref ctx);

        switch (ctx.Routine)
        {
            case RoutineScurry:  UpdateScurry(ref ctx); break;
            case RoutineFighting: UpdateFighting(ref ctx); break;
        }
    }

    private void EvaluateRoutine(ref AIContext ctx)
    {
        byte alert = ctx.AlertState;

        // Already fighting.
        if (ctx.Routine == RoutineFighting)
        {
            bool targetGone = !SubroutineSteps.IsTargetAlive(ref ctx);
            bool calm = alert == (byte)UnitAlertState.Unaware;

            // Lost our bite target (e.g. it died, or got dropped when a packmate fell)
            // but a threat is still around → re-acquire and keep fighting rather than
            // standing down. Without this the cycle would coast on a stale velocity.
            if (targetGone && !calm)
            {
                int re = PickGangUpTarget(ref ctx);
                if (re >= 0)
                {
                    ctx.Units[ctx.UnitIndex].Target = CombatTarget.Unit(ctx.Units[re].Id);
                    ctx.AlertTarget = ctx.Units[re].Id;
                    ctx.Units[ctx.UnitIndex].EngagedTarget = CombatTarget.None;
                    ctx.Subroutine = FightRushIn;
                    ctx.SubroutineTimer = 0f;
                    return;
                }
            }

            if (targetGone || calm)
            {
                ctx.Units[ctx.UnitIndex].Target = CombatTarget.None;
                ctx.Units[ctx.UnitIndex].EngagedTarget = CombatTarget.None;
                ctx.Units[ctx.UnitIndex].PreferredVel = Vec2.Zero; // don't coast away
                ctx.Routine = RoutineScurry;
                ctx.Subroutine = 0;
                ctx.SubroutineTimer = 0f;
            }
            return;
        }

        // Aggressive (awareness escalated) OR spooked (just took a hit) → fight back.
        // Rats are aggressive: they attack rather than flee. Gang up on whatever a
        // packmate is already biting, else the nearest threat.
        bool spooked = ctx.Units[ctx.UnitIndex].HitReacting;
        if (alert == (byte)UnitAlertState.Aggressive || spooked)
        {
            int targetIdx = PickGangUpTarget(ref ctx);
            if (targetIdx >= 0)
            {
                ctx.Units[ctx.UnitIndex].Target = CombatTarget.Unit(ctx.Units[targetIdx].Id);
                ctx.AlertState = (byte)UnitAlertState.Aggressive;
                ctx.AlertTarget = ctx.Units[targetIdx].Id;
                ctx.Routine = RoutineFighting;
                ctx.Subroutine = FightRushIn;
                ctx.SubroutineTimer = 0f;
                ctx.Units[ctx.UnitIndex].ShowStatusSymbol(UnitStatusSymbol.React, 1.0f);
            }
        }
    }

    // ═══════════════════════════════════════
    //  Routine: Scurry (idle)
    // ═══════════════════════════════════════

    private static void UpdateScurry(ref AIContext ctx)
    {
        // Quick, jittery wander — vermin never quite hold still.
        SubroutineSteps.SetEffort(ref ctx, MoveEffort.Walk);
        SubroutineSteps.IdleRoam(ref ctx, ScurryRadius);
        if (ctx.Subroutine == 0) ApplyErraticJitter(ref ctx); // weave while moving
    }

    // ═══════════════════════════════════════
    //  Routine: Fighting (dart-in / bite / skitter-out)
    // ═══════════════════════════════════════

    private void UpdateFighting(ref AIContext ctx)
    {
        // Belt-and-suspenders: never drive the fight cycle without a live target —
        // a stale PreferredVel would otherwise coast the rat off the map. (EvaluateRoutine
        // re-acquires or stands us down; here we just hold still for this frame.)
        if (SubroutineSteps.ResolveTarget(ref ctx) < 0)
        {
            ctx.Units[ctx.UnitIndex].PreferredVel = Vec2.Zero;
            return;
        }

        ctx.SubroutineTimer += ctx.Dt;

        switch (ctx.Subroutine)
        {
            case FightRushIn:
            {
                if (ctx.Units[ctx.UnitIndex].JumpPhase != 0) break;
                int targetIdx = SubroutineSteps.ResolveTarget(ref ctx);
                if (targetIdx < 0) break;

                float dist = (ctx.Units[targetIdx].Position - ctx.MyPos).Length();
                float range = SubroutineSteps.GetMeleeRange(ref ctx, targetIdx);
                bool ready = ctx.Units[ctx.UnitIndex].AttackCooldown <= 0f;
                if (dist <= range && ready)
                {
                    ctx.Subroutine = FightBite;
                    ctx.SubroutineTimer = 0f;
                    break;
                }

                // Reckless rush — full Sprint, no stalk ramp, erratic weave. If already
                // in range but the bite isn't ready, keep pressing into the target (the
                // rat stays in its face; it doesn't kite back to wait like a wolf).
                SubroutineSteps.SetEffort(ref ctx, MoveEffort.Sprint);
                SubroutineSteps.MoveToTarget(ref ctx);
                ApplyErraticJitter(ref ctx);
                // Safety: a rush must never end up moving AWAY from the target. The
                // pathfinder's per-unit-index direction cache can go stale right after a
                // swap-and-pop unit removal (a dying packmate), briefly handing this rat
                // the dead one's outbound direction — which would coast it off the map.
                // Snap back to a direct approach whenever the result points away.
                Vec2 toT = ctx.Units[targetIdx].Position - ctx.MyPos;
                if (toT.LengthSq() > 0.01f && ctx.Units[ctx.UnitIndex].PreferredVel.Dot(toT) < 0f)
                    ctx.Units[ctx.UnitIndex].PreferredVel = toT.Normalized() * ctx.MyMaxSpeed;
                break;
            }

            case FightBite:
            {
                SubroutineSteps.AttackTarget(ref ctx);
                // Skitter the instant the bite commits. We only enter Bite when the
                // attack was ready, so AttackCooldown>0 here means THIS bite fired
                // (not leftover cooldown). PostAttackTimer<=0 → strike anim finished.
                if (ctx.Units[ctx.UnitIndex].AttackCooldown > 0f
                    && ctx.Units[ctx.UnitIndex].PostAttackTimer <= 0f
                    && ctx.SubroutineTimer > 0.1f)
                {
                    ctx.Subroutine = FightSkitter;
                    ctx.SubroutineTimer = 0f;
                    ctx.Units[ctx.UnitIndex].EngagedTarget = CombatTarget.None;
                }
                break;
            }

            case FightSkitter:
            {
                // Bolt back, scared. Farther while a packmate-death panic is active.
                float backoff = ctx.Units[ctx.UnitIndex].PanicTimer > 0f
                    ? PanicSkitterDist : SkitterDist;
                SubroutineSteps.SetEffort(ref ctx, MoveEffort.Sprint);
                SubroutineSteps.Disengage(ref ctx, backoff);
                if (SubroutineSteps.Disengage_Complete(ref ctx, backoff))
                {
                    // Rush right back in — no cooldown wait, unlike the wolf.
                    ctx.Subroutine = FightRushIn;
                    ctx.SubroutineTimer = 0f;
                }
                break;
            }
        }
    }

    // ═══════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════

    /// <summary>Wiggle the current PreferredVel by an oscillating ± angle so the
    /// approach reads as erratic scurrying rather than a straight beeline. Per-unit
    /// phase (via UnitIndex) keeps a swarm out of sync.</summary>
    private static void ApplyErraticJitter(ref AIContext ctx)
    {
        var pv = ctx.Units[ctx.UnitIndex].PreferredVel;
        if (pv.LengthSq() < 0.01f) return;
        float ang = MathF.Sin(ctx.GameTime * JitterFreq + ctx.UnitIndex * 1.7f) * JitterAmplitudeRad;
        float c = MathF.Cos(ang), s = MathF.Sin(ang);
        ctx.Units[ctx.UnitIndex].PreferredVel = new Vec2(pv.X * c - pv.Y * s, pv.X * s + pv.Y * c);
    }

    /// <summary>Gang-up target selection: prefer the enemy a nearby packmate is already
    /// biting (pile-on), so rats converge instead of scattering. Falls back to the
    /// awareness-detected alert target, then the nearest enemy. Linear scan — only runs
    /// when (re)acquiring a target, not per frame.</summary>
    private static int PickGangUpTarget(ref AIContext ctx)
    {
        var myFaction = ctx.MyFaction;

        // Squad-driven pile-on: join whatever enemy a packmate is already biting. Exact membership
        // via the squad, so the whole pack converges on one victim instead of scattering — no
        // per-frame radius scan. Falls back to a proximity scan only when the rat has no squad.
        var squad = ctx.MySquad;
        if (squad != null)
        {
            var members = squad.Members;
            for (int m = 0; m < members.Count; m++)
            {
                if (!ctx.Units.TryGetIndex(members[m], out int j)) continue;
                if (j == ctx.UnitIndex || !ctx.Units[j].Alive) continue;
                if (!ctx.Units[j].Target.IsUnit) continue;
                int ti = UnitUtil.ResolveUnitIndex(ctx.Units, ctx.Units[j].Target.UnitID);
                if (ti >= 0 && ctx.Units[ti].Alive && ctx.Units[ti].Faction != myFaction)
                    return ti; // join the pile
            }
        }
        else
        {
            float gangR = ctx.Units[ctx.UnitIndex].GroupAlertRadius;
            if (gangR <= 0f) gangR = ctx.Units[ctx.UnitIndex].DetectionRange;
            float gangR2 = gangR * gangR;
            var myPos = ctx.MyPos;

            for (int j = 0; j < ctx.Units.Count; j++)
            {
                if (j == ctx.UnitIndex || !ctx.Units[j].Alive) continue;
                if (ctx.Units[j].Archetype != ArchetypeRegistry.RatPack) continue;
                if (ctx.Units[j].Faction != myFaction) continue;
                if ((ctx.Units[j].Position - myPos).LengthSq() > gangR2) continue;
                if (!ctx.Units[j].Target.IsUnit) continue;
                int ti = UnitUtil.ResolveUnitIndex(ctx.Units, ctx.Units[j].Target.UnitID);
                if (ti >= 0 && ctx.Units[ti].Alive && ctx.Units[ti].Faction != myFaction)
                    return ti; // join the pile
            }
        }

        int alertIdx = SubroutineSteps.ResolveAlertTarget(ref ctx);
        if (alertIdx >= 0) return alertIdx;
        return SubroutineSteps.FindClosestEnemy(ref ctx, ctx.Units[ctx.UnitIndex].DetectionRange);
    }

    public string GetRoutineName(byte routine) => routine switch
    {
        RoutineScurry => "Scurry",
        RoutineFighting => "Fighting",
        _ => $"Unknown({routine})"
    };

    public string GetSubroutineName(byte routine, byte subroutine) => routine switch
    {
        RoutineFighting => subroutine switch
        {
            FightRushIn => "RushIn",
            FightBite => "Bite",
            FightSkitter => "Skitter",
            _ => $"Unknown({subroutine})"
        },
        _ => subroutine == 0 ? "Default" : $"Unknown({subroutine})"
    };
}
