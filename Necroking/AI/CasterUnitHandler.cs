using System;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.GameSystems;
using Necroking.Lib;
using Necroking.Movement;

namespace Necroking.AI;

/// <summary>
/// Caster unit handler — NPC spellcasters (e.g. the Priest). Stands its ground and
/// casts its def's spell (Unit.SpellID) at enemies through the SAME pipeline as the
/// player necromancer: the handler pre-gates (mana, per-spell cooldown, magic paths)
/// and enqueues an AISpellCastRequest; Game1.DrainAISpellCasts runs it through
/// SpellCaster.TryStartSpellCast + SpellEffectSystem right after the tick. That gives
/// AI casters every spell category — projectiles (incl. multi-shot volleys), strikes
/// (with magic-resistance gates), clouds, buffs/debuffs, beams/drains (held via
/// Unit.ChannelTimer instead of the player's key-hold), summons.
///
/// Resources are per-unit: Unit.Mana (regenned here — nothing else ticks unit mana;
/// the necromancer's pool lives separately in NecroState) and the per-spell
/// Unit.SpellCooldowns dict, both paid/stamped by the shared pipeline.
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

    public void OnSpawn(ref AIContext ctx) => SentryTransitions.SpawnAtIdle(ref ctx);

    public void OnRoutineExit(ref AIContext ctx, byte oldRoutine, byte newRoutine)
    {
        // Combat owns the target — and any channel running against it.
        if (oldRoutine == RoutineCombat)
        {
            ctx.Units[ctx.UnitIndex].Target = CombatTarget.None;
            ctx.Units[ctx.UnitIndex].EngagedTarget = CombatTarget.None;
            if (ctx.Units[ctx.UnitIndex].ChannelTimer > 0f)
                CancelChannel(ref ctx);
        }
    }

    public void Update(ref AIContext ctx)
    {
        int i = ctx.UnitIndex;

        // Mana regen + spell cooldown tick every frame, whatever the routine
        // (no other system ticks unit mana — the necromancer's pool lives
        // separately in NecroState, ticked by Simulation).
        ctx.Units[i].Mana = MathF.Min(ctx.Units[i].MaxMana,
            ctx.Units[i].Mana + ctx.Units[i].ManaRegen * ctx.Dt);
        UnitCasterResources.TickCooldowns(ctx.Units[i].SpellCooldowns, ctx.Dt);

        // Channel hold (Beam/Drain cast through the shared pipeline armed this;
        // the player's equivalent is holding the spell-bar key). Combat handles
        // the stand-still; here we only run the clock and cut the beams/drains
        // when it expires.
        if (ctx.Units[i].ChannelTimer > 0f)
        {
            ctx.Units[i].ChannelTimer -= ctx.Dt;
            if (ctx.Units[i].ChannelTimer <= 0f)
                CancelChannel(ref ctx);
        }

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
        // Shared sentry ladder. Self-acquire at spell range — the legacy Caster
        // block zapped anything in range regardless of alert state (and this
        // covers scenario-spawned casters with no awareness config); the spell
        // is re-fetched each tick, as before. Reacquire falls back to 15u.
        var spell = GetSpell(ref ctx);
        float range = ctx.Units[ctx.UnitIndex].DetectionRange;
        var cfg = new SentryConfig(
            selfAcquireRange: spell != null ? SpellRange(ref ctx, spell) : 0f,
            reacquireRange: range > 0 ? range : 15f);
        SentryTransitions.EvaluateSentryRoutine(ref ctx, cfg);
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
        if (targetIdx < 0)
        {
            // Reacquire is handled in EvaluateRoutine; stop so a frenzied unit
            // held in Combat with no target doesn't coast on stale PreferredVel.
            SubroutineSteps.SetIdle(ref ctx);
            return;
        }

        int i = ctx.UnitIndex;

        // Mid-channel (Beam/Drain): plant and keep facing the target — walking
        // off would look wrong while the beam is attached. Update's tick ends
        // the channel; OnRoutineExit cancels it if combat ends first.
        if (ctx.Units[i].ChannelTimer > 0f)
        {
            ctx.Units[i].PreferredVel = Vec2.Zero;
            SubroutineSteps.SetLocomotionAnim(ref ctx);
            SubroutineSteps.FacePosition(ref ctx, ctx.Units[targetIdx].Position);
            return;
        }

        var spell = GetSpell(ref ctx);
        float maxRange = spell != null && spell.Range > 0f ? SpellRange(ref ctx, spell) : DefaultRange;
        float dist = (ctx.Units[targetIdx].Position - ctx.MyPos).Length();

        if (dist > maxRange)
        {
            // Out of range — jog up to casting distance.
            SubroutineSteps.SetEffort(ref ctx, MoveEffort.Hurry);
            SubroutineSteps.MoveToward(ref ctx, ctx.Units[targetIdx].Position, ctx.MyMaxSpeed);
            return;
        }

        // In range: stand and cast when mana + cooldown + paths allow. Facing
        // falls to Locomotion.UpdateFacing priority 3 (stationary with a Target →
        // face it) between casts.
        ctx.Units[i].PreferredVel = Vec2.Zero;
        SubroutineSteps.SetLocomotionAnim(ref ctx);

        if (spell != null)
            TryCast(ref ctx, targetIdx, spell);
    }

    private static void UpdateReturn(ref AIContext ctx) => SentryTransitions.UpdateReturn(ref ctx);

    /// <summary>The spell's cast range for THIS caster, including mastery range
    /// bonuses (levels above the primary path requirement) — the same scaled
    /// range TryStartSpellCast gates on, so approach/acquire distances match
    /// what a cast can actually reach.</summary>
    private static float SpellRange(ref AIContext ctx, SpellDef spell)
    {
        int i = ctx.UnitIndex;
        var casterDef = ctx.GameData?.Units.Get(ctx.Units[i].UnitDefID);
        if (casterDef == null) return spell.Range;
        var units = ctx.Units;
        return spell.ScaledRange(spell.MasteryLevels(
            p => BuffSystem.EffectivePathLevel(units, i, casterDef, p)));
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

    /// <summary>Request the spell at the target through the shared player pipeline:
    /// pre-gate cheaply (per-spell cooldown, path requirements, mana — all read-only;
    /// the drain re-checks and actually PAYS), then enqueue an AISpellCastRequest that
    /// Game1.DrainAISpellCasts validates, targets, and executes right after the tick.
    /// The cooldown/mana the drain stamps onto this unit gate the next attempt.</summary>
    private static void TryCast(ref AIContext ctx, int targetIdx, SpellDef spell)
    {
        int i = ctx.UnitIndex;
        // No request queue = bare headless sim with no Game1 drain — can't cast.
        if (ctx.SpellCasts == null) return;
        if (GetCooldown(ctx.Units[i], spell.Id) > 0f) return;

        // Path-aware gating: the caster must meet primary+secondary path
        // requirements; cost reductions come from the spell's own mastery
        // bonuses (fatigue -N% / free). Effective levels so path buffs (and
        // god mode's AllPaths floor) count.
        var units = ctx.Units;
        var casterDef = ctx.GameData.Units.Get(units[i].UnitDefID);
        Func<MagicPath, int> casterLevel = p => BuffSystem.EffectivePathLevel(units, i, casterDef, p);

        if (!spell.MeetsPathRequirements(casterLevel)) return;
        if (ctx.Units[i].Mana < spell.EffectiveManaCost(casterLevel)) return;

        // Aim point — the AI's "mouse": offensive categories aim at the enemy;
        // Buff aims at the caster itself so the shared targeting picks the ally
        // nearest to it (which may be itself).
        Vec2 aim = spell.Category == "Buff" ? ctx.MyPos : units[targetIdx].Position;
        ctx.SpellCasts.Add(new AISpellCastRequest
        {
            CasterId = ctx.MyId,
            SpellID = spell.Id,
            Target = aim,
        });

        // Face target (rate-capped by unit TurnSpeed — no instant snap, so the
        // caster visibly turns to its target as the spell winds up).
        SubroutineSteps.FacePosition(ref ctx, ctx.Units[targetIdx].Position);
    }

    /// <summary>This unit's remaining cooldown for a spell (per-spell dict, stamped
    /// by the shared pipeline at drain time; 0 when it has never cast it).</summary>
    private static float GetCooldown(Unit u, string spellId)
    {
        var cds = u.SpellCooldowns;
        return cds != null && cds.TryGetValue(spellId, out float cd) ? cd : 0f;
    }

    /// <summary>End an AI channel: zero the hold timer and cut any beams/drains this
    /// unit is sourcing. Unconditional — callers gate on ChannelTimer where it matters.</summary>
    private static void CancelChannel(ref AIContext ctx)
    {
        ctx.Units[ctx.UnitIndex].ChannelTimer = 0f;
        ctx.Lightning?.CancelBeamsForCaster(ctx.MyId);
        ctx.Lightning?.CancelDrainsForCaster(ctx.MyId);
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
