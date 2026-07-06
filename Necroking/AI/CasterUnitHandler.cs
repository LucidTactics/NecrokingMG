using System;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.GameSystems;
using Necroking.Movement;

namespace Necroking.AI;

/// <summary>
/// Caster unit handler — NPC spellcasters (e.g. the Priest). Migrated from the
/// legacy AIBehavior.Caster block in Simulation.UpdateAI: stands its ground and
/// casts its def's spell (Unit.SpellID) at enemies, gated by mana, cooldown,
/// spell range, and magic-path requirements (BuffSystem.EffectivePathLevel, so
/// path buffs count). Unlike the legacy block it will also close to spell range
/// when the target is too far, and returns to its spawn like other archetypes.
///
/// Routines (mirrors RangedUnitHandler):
///   0 = IdleRoaming  — wander near spawn at low speed
///   1 = Alert        — noticed threat
///   2 = Combat       — cast from range, advance if out of range
///   3 = Return       — go back to spawn position
///
/// Casters don't kite — they stand and cast (same stance the ranged handler
/// gave them before this handler existed).
/// </summary>
public class CasterUnitHandler : IArchetypeHandler
{
    private const byte RoutineIdle = 0;
    private const byte RoutineAlert = 1;
    private const byte RoutineCombat = 2;
    private const byte RoutineReturn = 3;

    private const float DefaultRange = 18f;  // fallback if the spell def is missing

    public void OnSpawn(ref AIContext ctx)
    {
        ctx.Units[ctx.UnitIndex].SpawnPosition = ctx.MyPos;
        ctx.Routine = RoutineIdle;
        ctx.Subroutine = 0;
        ctx.SubroutineTimer = 0f;
    }

    public void OnRoutineExit(ref AIContext ctx, byte oldRoutine, byte newRoutine)
    {
        // Combat owns the target.
        if (oldRoutine == RoutineCombat)
        {
            ctx.Units[ctx.UnitIndex].Target = CombatTarget.None;
            ctx.Units[ctx.UnitIndex].EngagedTarget = CombatTarget.None;
        }
    }

    public void Update(ref AIContext ctx)
    {
        int i = ctx.UnitIndex;

        // Mana regen + spell cooldown tick every frame, whatever the routine
        // (the legacy Caster block owned these; no other system ticks unit mana —
        // the necromancer's pool lives separately in NecroState).
        ctx.Units[i].Mana = MathF.Min(ctx.Units[i].MaxMana,
            ctx.Units[i].Mana + ctx.Units[i].ManaRegen * ctx.Dt);
        ctx.Units[i].SpellCooldownTimer = MathF.Max(0f, ctx.Units[i].SpellCooldownTimer - ctx.Dt);

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
            ctx.TransitionTo(RoutineAlert);
            return;
        }

        if (alert == (byte)UnitAlertState.Aggressive && ctx.Routine <= RoutineAlert)
        {
            if (ctx.AlertTarget != GameConstants.InvalidUnit)
            {
                ctx.TransitionTo(RoutineCombat);
                ctx.Units[ctx.UnitIndex].Target = CombatTarget.Unit(ctx.AlertTarget);
                return;
            }
        }

        // Self-acquire: an enemy inside spell range is engaged even without an
        // awareness escalation. The legacy Caster block zapped anything in range
        // regardless of alert state — this keeps that behavior (and covers
        // scenario-spawned casters with no awareness config).
        if (ctx.Routine <= RoutineAlert)
        {
            var spell = GetSpell(ref ctx);
            if (spell != null)
            {
                int enemy = SubroutineSteps.FindClosestEnemy(ref ctx, spell.Range);
                if (enemy >= 0)
                {
                    ctx.TransitionTo(RoutineCombat);
                    ctx.Units[ctx.UnitIndex].Target = CombatTarget.Unit(ctx.Units[enemy].Id);
                    return;
                }
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
                // OnRoutineExit(Combat) clears Target/EngagedTarget.
                ctx.TransitionTo(RoutineReturn);
                ctx.AlertState = (byte)UnitAlertState.Unaware;
                ctx.AlertTarget = GameConstants.InvalidUnit;
            }
        }

        if (alert == (byte)UnitAlertState.Unaware && ctx.Routine == RoutineAlert)
        {
            ctx.TransitionTo(RoutineIdle);
        }
    }

    private static void UpdateIdle(ref AIContext ctx)
    {
        SubroutineSteps.SetEffort(ref ctx, MoveEffort.Walk, 0.5f);
        SubroutineSteps.IdleRoam(ref ctx, 6f);
    }

    private static void UpdateAlert(ref AIContext ctx)
    {
        SubroutineSteps.AlertStance(ref ctx);
    }

    private static void UpdateCombat(ref AIContext ctx)
    {
        int targetIdx = SubroutineSteps.ResolveTarget(ref ctx);
        if (targetIdx < 0) return;

        int i = ctx.UnitIndex;
        var spell = GetSpell(ref ctx);
        float maxRange = spell != null && spell.Range > 0f ? spell.Range : DefaultRange;
        float dist = (ctx.Units[targetIdx].Position - ctx.MyPos).Length();

        if (dist > maxRange)
        {
            // Out of range — jog up to casting distance.
            SubroutineSteps.SetEffort(ref ctx, MoveEffort.Hurry);
            SubroutineSteps.MoveToward(ref ctx, ctx.Units[targetIdx].Position, ctx.MyMaxSpeed);
            return;
        }

        // In range: stand and cast when mana + cooldown + paths allow. Facing
        // falls to UpdateFacingAngles priority 3 (stationary with a Target →
        // face it) between casts.
        ctx.Units[i].PreferredVel = Vec2.Zero;
        SubroutineSteps.SetLocomotionAnim(ref ctx);

        if (spell != null)
            TryCast(ref ctx, targetIdx, spell);
    }

    private static void UpdateReturn(ref AIContext ctx)
    {
        ctx.Units[ctx.UnitIndex].EngagedTarget = CombatTarget.None;
        ctx.Units[ctx.UnitIndex].InCombat = false;

        Vec2 returnPos = ctx.Units[ctx.UnitIndex].SpawnPosition;
        if ((ctx.MyPos - returnPos).Length() > 2f)
        {
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

    /// <summary>This unit's spell def (Unit.SpellID), or null when it has none /
    /// no mana pool — a caster without a castable spell just behaves like a
    /// stand-and-idle unit.</summary>
    private static SpellDef? GetSpell(ref AIContext ctx)
    {
        int i = ctx.UnitIndex;
        if (ctx.Units[i].MaxMana <= 0f) return null;
        string spellId = ctx.Units[i].SpellID;
        if (string.IsNullOrEmpty(spellId) || ctx.GameData == null) return null;
        return ctx.GameData.Spells.Get(spellId);
    }

    /// <summary>Cast the spell at the target if every gate passes: path
    /// requirements (buff-aware effective levels), scaled mana cost, and
    /// cooldown. Migrated verbatim from the legacy AIBehavior.Caster block.</summary>
    private static void TryCast(ref AIContext ctx, int targetIdx, SpellDef spell)
    {
        int i = ctx.UnitIndex;
        if (ctx.Units[i].SpellCooldownTimer > 0f) return;
        var lightning = ctx.Lightning;
        if (lightning == null) return;

        // Path-aware gating: the caster must meet primary+secondary path
        // requirements; cost is scaled down by primary mastery. Effective
        // levels so path buffs (and god mode's AllPaths floor) count.
        var units = ctx.Units;
        var gameData = ctx.GameData;
        var casterDef = gameData.Units.Get(units[i].UnitDefID);
        Func<MagicPath, int> casterLevel = p => BuffSystem.EffectivePathLevel(units, i, casterDef, p);

        if (!spell.MeetsPathRequirements(casterLevel)) return;
        float effectiveCost = spell.EffectiveManaCost(casterLevel);
        if (ctx.Units[i].Mana < effectiveCost) return;

        var style = spell.BuildStrikeStyle();
        var visual = spell.StrikeVisualType == "GodRay"
            ? StrikeVisual.GodRay : StrikeVisual.Lightning;
        var tFilter = Enum.TryParse<SpellTargetFilter>(spell.TargetFilter, out var tf)
            ? tf : SpellTargetFilter.AnyEnemy;

        if (spell.StrikeTargetUnit)
        {
            // Instant zap: caster hand to target unit
            Vec2 casterPos = ctx.Units[i].EffectSpawnPos2D;
            float casterHeight = ctx.Units[i].EffectSpawnHeight;
            Vec2 targetPos = ctx.Units[targetIdx].Position;

            // Target center of sprite body instead of feet
            float targetH = 1.8f;
            var tDef = gameData.Units.Get(ctx.Units[targetIdx].UnitDefID);
            if (tDef != null) targetH = tDef.SpriteWorldHeight;
            targetH *= ctx.Units[targetIdx].SpriteScale;

            lightning.SpawnZap(casterPos, targetPos, spell.ZapDuration, style,
                casterHeight, targetH * 0.5f);

            // Apply direct damage to target, attributed to the caster.
            if (ctx.DamageEvents != null)
                DamageSystem.Apply(units, targetIdx, spell.Damage,
                    DamageType.Physical, DamageFlags.ArmorNegating, ctx.DamageEvents, i);
        }
        else
        {
            // Sky strike: telegraph then AOE (damage flows through the
            // lightning damage list drained in Simulation.Update).
            lightning.SpawnStrike(ctx.Units[targetIdx].Position,
                spell.TelegraphDuration, spell.StrikeDuration,
                spell.AoeRadius, spell.Damage, style, spell.Id, visual,
                spell.BuildGodRayParams(), tFilter,
                spell.TelegraphVisible, ctx.Units[i].Id);
        }

        ctx.Units[i].Mana -= effectiveCost;
        ctx.Units[i].SpellCooldownTimer = spell.Cooldown;

        // Apply casting buff
        if (!string.IsNullOrEmpty(spell.CastingBuffID))
        {
            var castBuff = gameData.Buffs.Get(spell.CastingBuffID);
            if (castBuff != null) BuffSystem.ApplyBuff(units, i, castBuff, gameData);
        }

        // Face target (rate-capped by unit TurnSpeed — no instant snap, so the
        // caster visibly turns to its target as the spell winds up).
        SubroutineSteps.FacePosition(ref ctx, ctx.Units[targetIdx].Position);
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
