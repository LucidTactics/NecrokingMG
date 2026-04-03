using System;
using Necroking.Core;

namespace Necroking.AI;

/// <summary>
/// Deer herd AI archetype: prey animal with alert/flee behavior.
///
/// Behavior varies by sex (determined by UnitDef — FemaleDeer vs MaleDeer):
///   Female: always flees from threats, never fights
///   Male: fights back if cornered or if threat is alone, otherwise flees
///
/// Routines:
///   0 = IdleRoaming  — walk to a point at 30% speed, idle for a while, repeat within 10u of spawn
///   1 = Sleeping     — nighttime: stand still
///   2 = Alert        — freeze, face threat. If threat approaches, escalate
///   3 = Fleeing      — run away from threat. Propagates to nearby herd members
///   4 = Calming      — threat gone, gradually return to idle behavior
///   5 = FightBack    — male only: charge and attack the threat
///
/// Alert behavior:
///   - On detection: freeze in place, face threat (Alert routine)
///   - After alert duration or if threat gets within escalate range:
///     - Female: always flee
///     - Male: fight if threat is alone, flee if outnumbered
///   - Flee propagation: when one deer flees, nearby herd members also flee
///   - After fleeing far enough (break range), enter Calming then return to Idle
/// </summary>
public class DeerHerdHandler : IArchetypeHandler
{
    private const byte RoutineIdleRoaming = 0;
    private const byte RoutineSleeping = 1;
    private const byte RoutineAlert = 2;
    private const byte RoutineFleeing = 3;
    private const byte RoutineCalming = 4;
    private const byte RoutineFightBack = 5;

    // Fighting subroutines (for males)
    private const byte FightChase = 0;
    private const byte FightAttack = 1;

    private const float RoamRadius = 10f;
    private const float FleeDistance = 20f;
    private const float CalmDuration = 3f;

    public void OnSpawn(ref AIContext ctx)
    {
        ctx.Units.SpawnPosition[ctx.UnitIndex] = ctx.MyPos;
        ctx.Routine = ctx.IsNight ? RoutineSleeping : RoutineIdleRoaming;
        ctx.Subroutine = 0;
        ctx.SubroutineTimer = 0f;
    }

    public void Update(ref AIContext ctx)
    {
        EvaluateRoutine(ref ctx);

        switch (ctx.Routine)
        {
            case RoutineIdleRoaming: UpdateIdleRoaming(ref ctx); break;
            case RoutineSleeping:    SubroutineSteps.Idle(ref ctx); break;
            case RoutineAlert:       UpdateAlert(ref ctx); break;
            case RoutineFleeing:     UpdateFleeing(ref ctx); break;
            case RoutineCalming:     UpdateCalming(ref ctx); break;
            case RoutineFightBack:   UpdateFightBack(ref ctx); break;
        }
    }

    private void EvaluateRoutine(ref AIContext ctx)
    {
        byte alert = ctx.AlertState;
        bool isMale = IsMale(ref ctx);

        // Alert detected → enter Alert routine (from any non-combat routine)
        if (alert >= (byte)UnitAlertState.Alert && ctx.Routine <= RoutineSleeping)
        {
            ctx.Routine = RoutineAlert;
            ctx.Subroutine = 0;
            ctx.SubroutineTimer = 0f;
            return;
        }

        // Aggressive alert → escalate from Alert
        if (alert == (byte)UnitAlertState.Aggressive && ctx.Routine == RoutineAlert)
        {
            if (isMale && ShouldFightBack(ref ctx))
            {
                ctx.Routine = RoutineFightBack;
                ctx.Subroutine = FightChase;
                ctx.SubroutineTimer = 0f;
                ctx.Units.Target[ctx.UnitIndex] = CombatTarget.Unit(ctx.AlertTarget);
            }
            else
            {
                ctx.Routine = RoutineFleeing;
                ctx.Subroutine = 0;
                ctx.SubroutineTimer = 0f;
            }
            return;
        }

        // Threat gone while alert/fighting → calm down
        if (alert == (byte)UnitAlertState.Unaware)
        {
            if (ctx.Routine == RoutineAlert || ctx.Routine == RoutineFleeing)
            {
                ctx.Routine = RoutineCalming;
                ctx.SubroutineTimer = CalmDuration;
                ctx.Units.Target[ctx.UnitIndex] = CombatTarget.None;
                ctx.Units.EngagedTarget[ctx.UnitIndex] = CombatTarget.None;
                return;
            }
            if (ctx.Routine == RoutineFightBack && !SubroutineSteps.IsTargetAlive(ref ctx))
            {
                ctx.Routine = RoutineCalming;
                ctx.SubroutineTimer = CalmDuration;
                ctx.Units.Target[ctx.UnitIndex] = CombatTarget.None;
                ctx.Units.EngagedTarget[ctx.UnitIndex] = CombatTarget.None;
                return;
            }
        }

        // Time of day for non-alert routines
        if (ctx.Routine <= RoutineSleeping)
        {
            byte target = ctx.IsNight ? RoutineSleeping : RoutineIdleRoaming;
            if (ctx.Routine != target)
            {
                ctx.Routine = target;
                ctx.Subroutine = 0;
                ctx.SubroutineTimer = 0f;
            }
        }

        if (ctx.Routine == RoutineFightBack)
        {   
            if (!SubroutineSteps.IsTargetAlive(ref ctx))
            {
                // Target dead — return to time-of-day routine
                ctx.Units.Target[ctx.UnitIndex] = CombatTarget.None;
                ctx.Units.EngagedTarget[ctx.UnitIndex] = CombatTarget.None;
                ctx.AlertState = (byte)UnitAlertState.Unaware;
                ctx.AlertTarget = GameConstants.InvalidUnit;
                SwitchToTimeOfDayRoutine(ref ctx);
                return;
            }

            // Alert dropped (enemy left break range) — disengage
            if (ctx.AlertState == (byte)UnitAlertState.Unaware)
            {
                ctx.Units.Target[ctx.UnitIndex] = CombatTarget.None;
                ctx.Units.EngagedTarget[ctx.UnitIndex] = CombatTarget.None;
                SwitchToTimeOfDayRoutine(ref ctx);
                return;
            }
        }
    }
    private static void SwitchToTimeOfDayRoutine(ref AIContext ctx)
    {
        byte target = ctx.IsNight ? RoutineSleeping : RoutineIdleRoaming;
        if (ctx.Routine != target)
        {
            ctx.Routine = target;
            ctx.Subroutine = 0;
            ctx.SubroutineTimer = 0f;
        }
    }

    /// <summary>Male fights if threat is alone (no other enemies nearby).</summary>
    private static bool ShouldFightBack(ref AIContext ctx)
    {
        int threatCount = 0;
        var myFaction = ctx.MyFaction;
        for (int j = 0; j < ctx.Units.Count; j++)
        {
            if (!ctx.Units.Alive[j] || ctx.Units.Faction[j] == myFaction) continue;
            if ((ctx.Units.Position[j] - ctx.MyPos).LengthSq() < 15f * 15f)
                threatCount++;
        }
        return threatCount <= 1;
    }

    private static bool IsMale(ref AIContext ctx)
    {
        string defId = ctx.Units.UnitDefID[ctx.UnitIndex] ?? "";
        return defId.Contains("Male", StringComparison.OrdinalIgnoreCase)
            && !defId.Contains("Female", StringComparison.OrdinalIgnoreCase);
    }

    // ═══════════════════════════════════════

    private static void UpdateIdleRoaming(ref AIContext ctx)
    {
        SubroutineSteps.IdleRoam(ref ctx, RoamRadius);
    }

    private static void UpdateAlert(ref AIContext ctx)
    {
        SubroutineSteps.AlertStance(ref ctx);
    }

    private static void UpdateFleeing(ref AIContext ctx)
    {
        int threatIdx = SubroutineSteps.ResolveAlertTarget(ref ctx);
        if (threatIdx >= 0)
        {
            Vec2 threatPos = ctx.Units.Position[threatIdx];
            Vec2 awayDir = ctx.MyPos - threatPos;
            float dist = awayDir.Length();
            if (dist > 0.01f) awayDir *= 1f / dist;
            else awayDir = new Vec2(1, 0);

            // Pathfind to a point far away from threat
            Vec2 fleeDest = ctx.MyPos + awayDir * FleeDistance;
            SubroutineSteps.MoveToward(ref ctx, fleeDest, ctx.MySpeed);
        }
        else
        {
            // Threat gone — will be caught by EvaluateRoutine next frame
            ctx.Units.PreferredVel[ctx.UnitIndex] = Vec2.Zero;
        }
    }

    private static void UpdateCalming(ref AIContext ctx)
    {
        // Slow down gradually, then return to idle
        ctx.Units.PreferredVel[ctx.UnitIndex] = Vec2.Zero;
        ctx.SubroutineTimer -= ctx.Dt;
        if (ctx.SubroutineTimer <= 0f)
        {
            ctx.Routine = ctx.IsNight ? RoutineSleeping : RoutineIdleRoaming;
            ctx.Subroutine = 0;
            ctx.SubroutineTimer = 0f;
        }
    }

    private static void UpdateFightBack(ref AIContext ctx)
    {
        if (!SubroutineSteps.IsTargetAlive(ref ctx))
        {
            ctx.Routine = RoutineCalming;
            ctx.SubroutineTimer = CalmDuration;
            return;
        }

        ctx.SubroutineTimer += ctx.Dt;

        if (ctx.Subroutine == FightChase)
        {
            SubroutineSteps.MoveToTarget(ref ctx);
            int targetIdx = SubroutineSteps.ResolveTarget(ref ctx);
            if (targetIdx >= 0)
            {
                float range = SubroutineSteps.GetMeleeRange(ref ctx, targetIdx);
                if ((ctx.Units.Position[targetIdx] - ctx.MyPos).Length() <= range)
                {
                    ctx.Subroutine = FightAttack;
                    ctx.SubroutineTimer = 0f;
                }
            }
        }
        else // FightAttack
        {
            SubroutineSteps.AttackTarget(ref ctx);
            // Male deer doesn't disengage — keeps fighting until threat dies or leaves
        }
    }

    public string GetRoutineName(byte routine) => routine switch
    {
        RoutineIdleRoaming => "IdleRoaming",
        RoutineSleeping => "Sleeping",
        RoutineAlert => "Alert",
        RoutineFleeing => "Fleeing",
        RoutineCalming => "Calming",
        RoutineFightBack => "FightBack",
        _ => $"Unknown({routine})"
    };

    public string GetSubroutineName(byte routine, byte subroutine) => routine switch
    {
        RoutineFightBack => subroutine switch
        {
            FightChase => "FightChase",
            FightAttack => "FightAttack",
            _ => $"Unknown({subroutine})"
        },
        _ => subroutine == 0 ? "Default" : $"Unknown({subroutine})"
    };
}
