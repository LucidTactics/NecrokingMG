using System;
using Necroking.Core;

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
/// Archers try to maintain distance; casters stand and cast.
/// Both fall back to melee if cornered.
/// </summary>
public class RangedUnitHandler : IArchetypeHandler
{
    private const byte RoutineIdle = 0;
    private const byte RoutineAlert = 1;
    private const byte RoutineCombat = 2;
    private const byte RoutineReturn = 3;

    private const float DefaultRange = 18f;  // fallback if unit has no RangedRange stat
    private const float TooCloseFrac = 0.25f; // back away if within this fraction of max range
    private const float DefaultCooldown = 2f;
    private const int DefaultDamage = 8;
    private const int DefaultPrecision = 10;

    private readonly byte _archetypeId;

    public RangedUnitHandler(byte archetypeId) { _archetypeId = archetypeId; }

    public void OnSpawn(ref AIContext ctx)
    {
        ctx.Units[ctx.UnitIndex].SpawnPosition = ctx.MyPos;
        ctx.Routine = RoutineIdle;
        ctx.Subroutine = 0;
        ctx.SubroutineTimer = 0f;
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
        byte alert = ctx.AlertState;

        if (alert >= (byte)UnitAlertState.Alert && ctx.Routine == RoutineIdle)
        {
            ctx.Routine = RoutineAlert;
            ctx.SubroutineTimer = 0f;
            return;
        }

        if (alert == (byte)UnitAlertState.Aggressive && ctx.Routine <= RoutineAlert)
        {
            if (ctx.AlertTarget != GameConstants.InvalidUnit)
            {
                ctx.Units[ctx.UnitIndex].Target = CombatTarget.Unit(ctx.AlertTarget);
                ctx.Routine = RoutineCombat;
                ctx.SubroutineTimer = 0f;
                return;
            }
        }

        if (ctx.Routine == RoutineCombat && !SubroutineSteps.IsTargetAlive(ref ctx))
        {
            float range = ctx.Units[ctx.UnitIndex].DetectionRange;
            int next = SubroutineSteps.FindClosestEnemy(ref ctx, range > 0 ? range : 15f);
            if (next >= 0)
                ctx.Units[ctx.UnitIndex].Target = CombatTarget.Unit(ctx.Units[next].Id);
            else
            {
                ctx.Routine = RoutineReturn;
                ctx.Units[ctx.UnitIndex].Target = CombatTarget.None;
                ctx.AlertState = (byte)UnitAlertState.Unaware;
                ctx.AlertTarget = GameConstants.InvalidUnit;
            }
        }

        if (alert == (byte)UnitAlertState.Unaware && ctx.Routine == RoutineAlert)
        {
            ctx.Routine = RoutineIdle;
            ctx.SubroutineTimer = 0f;
        }
    }

    private static void UpdateIdle(ref AIContext ctx)
    {
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
        float tooClose = maxRange * TooCloseFrac;

        float dist = (ctx.Units[targetIdx].Position - ctx.MyPos).Length();

        // Tick cooldown locally so the AI doesn't depend on the legacy combat queue.
        if (ctx.Units[i].AttackCooldown > 0f)
            ctx.Units[i].AttackCooldown = MathF.Max(0f, ctx.Units[i].AttackCooldown - ctx.Dt);

        if (dist > maxRange)
        {
            // Out of range — close in
            SubroutineSteps.MoveToward(ref ctx, ctx.Units[targetIdx].Position, ctx.MySpeed);
            return;
        }

        if (dist < tooClose && _archetypeId == ArchetypeRegistry.ArcherUnit)
        {
            // Archer kites — casters stand their ground
            SubroutineSteps.MoveAwayFrom(ref ctx, ctx.Units[targetIdx].Position, tooClose * 1.5f);
        }
        else
        {
            ctx.Units[i].PreferredVel = Vec2.Zero;
        }

        // Fire ranged attack while in range and off cooldown.
        // Note: ranged units never set EngagedTarget — that path runs the melee resolver.
        if (_archetypeId == ArchetypeRegistry.ArcherUnit
            && ctx.Units[i].AttackCooldown <= 0f
            && ctx.Units[i].PendingAttack.IsNone
            && ctx.Projectiles != null)
        {
            int damage = stats.RangedDmg.Count > 0 ? stats.RangedDmg[0] : DefaultDamage;
            float cooldown = stats.RangedCooldownTime.Count > 0 ? stats.RangedCooldownTime[0] : DefaultCooldown;
            bool volley = dist > maxRange * 0.4f;
            ctx.Projectiles.SpawnArrow(ctx.MyPos, ctx.Units[targetIdx].Position,
                ctx.Units[i].Faction, ctx.Units[i].Id, damage, volley, DefaultPrecision);
            ctx.Units[i].AttackCooldown = cooldown;
        }
    }

    private static void UpdateReturn(ref AIContext ctx)
    {
        ctx.Units[ctx.UnitIndex].EngagedTarget = CombatTarget.None;
        ctx.Units[ctx.UnitIndex].InCombat = false;

        Vec2 returnPos = ctx.Units[ctx.UnitIndex].SpawnPosition;
        if ((ctx.MyPos - returnPos).Length() > 2f)
            SubroutineSteps.MoveToward(ref ctx, returnPos, ctx.MySpeed * 0.5f);
        else
        {
            ctx.Routine = RoutineIdle;
            ctx.Subroutine = 0;
            ctx.SubroutineTimer = 0f;
            ctx.Units[ctx.UnitIndex].PreferredVel = Vec2.Zero;
        }
    }

    public string GetRoutineName(byte routine) => routine switch
    {
        RoutineIdle => "IdleRoaming",
        RoutineAlert => "Alert",
        RoutineCombat => "Combat",
        RoutineReturn => "Return",
        _ => $"Unknown({routine})"
    };

    public string GetSubroutineName(byte routine, byte subroutine) =>
        subroutine == 0 ? "Default" : $"Unknown({subroutine})";
}
