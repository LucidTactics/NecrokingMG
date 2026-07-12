using System.Collections.Generic;
using Necroking.Core;
using Necroking.Data;
using Necroking.Lib;
using Necroking.Movement;
using Necroking.Render;

namespace Necroking.AI;

/// <summary>
/// Passed to archetype handlers each frame. Wraps all the state an AI needs
/// to make decisions without coupling to Simulation internals.
/// Zero allocations — this is a ref struct passed by reference.
/// </summary>
public ref struct AIContext
{
    public int UnitIndex;
    public UnitArrays Units;
    public float Dt;
    public int FrameNumber;

    // World queries (set by Simulation before AI tick)
    public GameData GameData;
    public World.Pathfinder? Pathfinder;
    public Quadtree? Quadtree;
    /// <summary>Central world-query engine (canonical nearest-of / in-radius scans —
    /// see <see cref="GameSystems.WorldQuery"/>). Set by Simulation.BuildAIContext;
    /// null in minimal contexts (OnSpawn, hand-built scenario/AIControl contexts) —
    /// steps that use it must keep a null-tolerant fallback.</summary>
    public GameSystems.WorldQuery? Query;
    public GameSystems.HordeSystem? Horde;
    /// <summary>Live squad registry (herds / packs / patrols). Handlers read their own group via
    /// <see cref="MySquad"/> for cohesion + shared-alert; recomputed before the AI pass each frame.</summary>
    public SquadSystem? Squads;
    public GameSystems.TriggerSystem? TriggerSystem;
    public GameSystems.VillageSystem? Villages;
    public World.EnvironmentSystem? EnvSystem;
    public Game.Jobs.WorkerSystem? Workers;
    public GameSystems.ProjectileManager? Projectiles;
    public GameSystems.MagicGlyphSystem? MagicGlyphs;
    /// <summary>Strike/zap spawner for casting archetypes (CasterUnitHandler). Null in
    /// minimal contexts (OnSpawn) — handlers must only cast from Update.</summary>
    public GameSystems.LightningSystem? Lightning;
    /// <summary>Spell-cast request queue for casting archetypes: enqueue an
    /// <see cref="GameSystems.AISpellCastRequest"/> and Game1 runs it through the SAME
    /// SpellCaster + SpellEffectSystem pipeline as the player right after the tick
    /// (targeting, path/mana/cooldown gates, every spell category). Null in minimal
    /// contexts; in headless runs requests are dropped unexecuted.</summary>
    public List<GameSystems.AISpellCastRequest>? SpellCasts;
    public List<GameSystems.DamageEvent>? DamageEvents;
    // Anim metadata for effect-time lookups (used by AI to time things like pounce takeoffs).
    public Dictionary<string, AnimationMeta>? AnimMeta;

    /// <summary>Player necromancer's sprint-ramp scalar (0=walking, 1=full sprint).
    /// Used by HordeMinion handler's distance-banded effort logic — the bands
    /// compress as the necro speeds up so minions escalate effort faster when
    /// they're falling behind a sprinting master.</summary>
    public float NecroSprintT;

    /// <summary>True while a Wolf Hunt spell is directing the player's wolves onto a herd. Gates
    /// <see cref="WolfPackHuntAI.WantsToFlank"/> so wolves only stalk (suppress their normal charge)
    /// during a commanded hunt; with no command they aggro deer like any other horde minion.</summary>
    public bool WolfHuntCommandActive;

    // Game clock
    public float GameTime;          // total elapsed seconds
    public float DayTime;           // 0..1 fraction of current day cycle
    public bool IsNight;            // true during night period

    // AI amortization (low-urgency state scheduling). When enabled, handlers
    // running routines that don't need per-frame reactivity should early-out
    // on frames where IsAmortizeTick is false; the unit keeps its previous
    // PreferredVel. Units are staggered via (frame + index) % interval so the
    // cost spreads evenly instead of pulsing every N frames.
    public bool AmortizedAI;
    public int AmortizationInterval;
    public readonly bool IsAmortizeTick =>
        !AmortizedAI
        || AmortizationInterval <= 1
        || ((FrameNumber + UnitIndex) % AmortizationInterval == 0);

    // Convenience accessors
    public readonly Vec2 MyPos => Units[UnitIndex].Position;
    /// <summary>The unit's resolved max-speed cap (CombatSpeed × effort multiplier ×
    /// any routine cap), as set by <see cref="SubroutineSteps.SetEffort"/>. This is the
    /// speed a routine should move at — NOT a base/current speed. Pass it straight to
    /// MoveToward; don't scale it again or you double-penalise the effort system.</summary>
    public readonly float MyMaxSpeed => Units[UnitIndex].MaxSpeed;
    public readonly Faction MyFaction => Units[UnitIndex].Faction;
    public readonly uint MyId => Units[UnitIndex].Id;
    public readonly byte Routine { get => Units[UnitIndex].Routine; set => Units[UnitIndex].Routine = value; }
    public readonly byte Subroutine { get => Units[UnitIndex].Subroutine; set => Units[UnitIndex].Subroutine = value; }
    public readonly float SubroutineTimer { get => Units[UnitIndex].SubroutineTimer; set => Units[UnitIndex].SubroutineTimer = value; }
    public readonly byte AlertState { get => Units[UnitIndex].AlertState; set => Units[UnitIndex].AlertState = value; }
    public readonly float AlertTimer { get => Units[UnitIndex].AlertTimer; set => Units[UnitIndex].AlertTimer = value; }
    public readonly uint AlertTarget { get => Units[UnitIndex].AlertTarget; set => Units[UnitIndex].AlertTarget = value; }

    /// <summary>
    /// The single legal way to change this unit's Routine. Fires the archetype handler's
    /// OnRoutineExit/OnRoutineEnter hooks around the change and stamps Subroutine +
    /// SubroutineTimer with fresh-entry values.
    ///
    /// No-op (returns false) when the unit is already in <paramref name="routine"/> —
    /// re-asserting the current routine every frame is safe and neither re-fires hooks nor
    /// clobbers Subroutine/SubroutineTimer. To restart a subroutine within the same routine,
    /// set Subroutine/SubroutineTimer directly (a step change, not a transition); to force a
    /// full re-entry of the same routine use <see cref="AIControl.StartRoutine"/>.
    ///
    /// Setting <see cref="Routine"/> directly bypasses exit cleanup and is how "unit locked
    /// by a stale PendingAttack/EngagedTarget" bugs shipped — don't.
    /// </summary>
    public bool TransitionTo(byte routine, byte subroutine = 0, float timer = 0f)
    {
        byte old = Units[UnitIndex].Routine;
        if (old == routine) return false;

        var handler = ArchetypeRegistry.Get(Units[UnitIndex].Archetype);
        handler?.OnRoutineExit(ref this, old, routine);
        Units[UnitIndex].Routine = routine;
        Units[UnitIndex].Subroutine = subroutine;
        Units[UnitIndex].SubroutineTimer = timer;
        handler?.OnRoutineEnter(ref this, old, routine);

        if (AIControl.TraceTransitions && handler != null)
            DebugLog.Log("ai_transition",
                $"[unit {Units[UnitIndex].Id}] {ArchetypeRegistry.GetName(Units[UnitIndex].Archetype)}: " +
                $"{handler.GetRoutineName(old)} -> {handler.GetRoutineName(routine)}");
        return true;
    }

    /// <summary>This unit's squad this frame, or null if it has none / squads aren't wired
    /// (some scenarios). Read-only view — the squad's fields are recomputed by SquadSystem.</summary>
    public readonly Squad? MySquad
    {
        get
        {
            var sid = Units[UnitIndex].SquadId;
            if (sid == 0 || Squads == null) return null;
            return Squads.Get(sid);
        }
    }
}

/// <summary>Alert states shared by all archetypes.</summary>
public enum UnitAlertState : byte
{
    Unaware = 0,
    Alert = 1,
    Aggressive = 2,
}
