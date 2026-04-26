using System;
using System.Collections.Generic;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.Render;

namespace Necroking.Movement;

public struct ActiveBuff
{
    public string BuffDefID;
    public float RemainingDuration;
    public bool Permanent;
    public List<BuffEffect> Effects;
    public int StackCount;
}

/// <summary>
/// Tracks a unit being incapacitated by a debuff (knockdown, stun, freeze, etc.).
/// The buff system manages the lifecycle; other systems just check Incap.IsLocked.
/// </summary>
public struct IncapState
{
    public bool Active;                // True while incapacitated (blocks AI/movement/combat)
    public Render.AnimState HoldAnim;  // Animation to show while incapacitated (e.g. Knockdown)
    public Render.AnimState RecoverAnim; // Animation to play on recovery (e.g. Standup)
    public float RecoverTime;          // Duration of recovery animation
    public float RecoverTimer;         // Countdown for recovery phase
    public bool Recovering;            // True during recovery animation (after incap ends)
    public bool HoldAtEnd;             // Snap to last frame of HoldAnim on entry

    /// <summary>Is the unit currently incapacitated or recovering?</summary>
    public readonly bool IsLocked => Active || Recovering;
}

public enum UnitStatusSymbol : byte
{
    None = 0,
    Notice = 1,  // "?" — unit has spotted a threat (e.g. deer freezes and watches)
    React = 2,   // "!" — unit is reacting (fleeing, charging, fighting back)
}

public static class UnitStatusSymbolExt
{
    /// <summary>Show a status symbol above the unit. Higher priority symbols (React &gt; Notice)
    /// don't get downgraded; duration is refreshed to the max of current vs new.</summary>
    public static void ShowStatusSymbol(this Unit u, UnitStatusSymbol sym, float duration)
    {
        if ((byte)sym < u.StatusSymbol) return;
        u.StatusSymbol = (byte)sym;
        if (duration > u.StatusSymbolTimer) u.StatusSymbolTimer = duration;
    }
}

public class Unit
{
    // Hot path
    public Vec2 Position;
    public Vec2 Velocity;
    public Vec2 PreferredVel;
    public float Z;             // Height above ground (0 = on ground). Used by 2.5D impulse physics.
    public bool InPhysics;      // True while physics system owns this unit's movement.
    /// <summary>Tracks whether OverrideAnim has been applied to AnimController.
    /// Public getter but internal setter — only Necroking.Render.AnimResolver
    /// mutates this, preventing the "stale OverrideStarted carries into next
    /// override" class of bug (commit 9247c71). External callers must use
    /// AnimResolver.SetOverride to queue overrides.</summary>
    public bool OverrideStarted { get; internal set; }

    /// <summary>
    /// Cosmetic XY offset applied to every visual attached to this unit (sprite,
    /// weapon, shield, shadow, health bar, damage numbers, buff auras, etc.).
    /// Written by the animation tick each frame. Gameplay systems (pathfinding,
    /// ORCA, collisions, AI ranges) must keep reading raw Position — use RenderPos
    /// only from draw / spawn-visual paths.
    ///
    /// Currently driven by the attack-lunge system: on melee swing, the unit's
    /// sprite lunges forward toward the target at effect_time and decays back
    /// by the end of the anim, while the simulation position stays put.
    /// </summary>
    public Vec2 RenderOffset;

    /// <summary>Convenience: Position + RenderOffset. Use this everywhere a
    /// visual should follow cosmetic offsets. Gameplay code uses Position.</summary>
    public Vec2 RenderPos => Position + RenderOffset;

    // Movement
    public float Radius = 0.495f;
    public float MaxSpeed = 8f;
    public int OrcaPriority;
    public Vec2 MoveTarget;

    // Identity
    public int Size = 2;
    public uint Id;
    public UnitType Type;
    public string UnitDefID = "";
    public Faction Faction;
    public AIBehavior AI = AIBehavior.AttackClosest;

    // Combat
    public UnitStats Stats = new();
    public float Fatigue;
    public CombatTarget Target = CombatTarget.None;
    public float RetargetTimer;

    // State
    public bool Alive = true;
    /// <summary>
    /// Derived in Simulation.UpdateCombat from EngagedTarget + melee range. Owned by
    /// the combat phase — no other system should write this. Handlers in the AI phase
    /// read LAST frame's value (combat runs after AI). For edge reactions prefer
    /// JustEnteredCombat / JustLeftCombat, which fire for exactly one frame on the
    /// tick where the derivation flipped.
    /// </summary>
    public bool InCombat;
    /// <summary>Set by Simulation.UpdateCombat when InCombat flips false→true this
    /// frame. Cleared at the start of the next combat derivation. One-shot edge.</summary>
    public bool JustEnteredCombat;
    /// <summary>Set by Simulation.UpdateCombat when InCombat flips true→false this
    /// frame. Cleared at the start of the next combat derivation. One-shot edge.</summary>
    public bool JustLeftCombat;
    public float FacingAngle = 90f;
    public float AttackCooldown;
    public CombatTarget PendingAttack = CombatTarget.None;
    /// <summary>LungeDist of the weapon that initiated the currently-playing attack
    /// anim. Latched here when the attack is queued (PendingWeaponIdx gets cleared at
    /// effect_time but the lunge continues into the recovery half of the anim).
    /// Cleared when the attack anim ends.</summary>
    public float CurrentAttackLungeDist;
    /// <summary>
    /// Index into the chosen weapon list for the pending attack.
    /// -1 = no specific weapon (unarmed / legacy). Use with PendingWeaponIsRanged
    /// to pick the right list (Stats.MeleeWeapons vs Stats.RangedWeapons).
    /// </summary>
    public int PendingWeaponIdx = -1;
    public bool PendingWeaponIsRanged;
    /// <summary>UnitID of the ranged target captured at queue time (target may die before action moment).</summary>
    public uint PendingRangedTarget = GameConstants.InvalidUnit;
    public uint LastAttackerID = GameConstants.InvalidUnit;
    /// <summary>Game time (seconds) at which this unit last took damage. Used by
    /// retarget-when-hit AI logic: HitReacting clears after one tick, but the
    /// opportunity to retarget spans several ticks (the unit might be briefly in
    /// melee with its current target when the hit lands).</summary>
    public float LastHitTime = -1f;
    public float FleeTimer;
    public byte WolfPhase;
    public float WolfPhaseTimer;

    // Jumping — scripted voluntary jump (JumpSystem). Z holds airborne height.
    // JumpPhase drives the state machine: 0=None, 1=TakeoffApproach, 2=Airborne,
    // 3=Landing (in air, JumpLand anim playing), 4=Recovery (on ground, JumpLand finishing).
    public bool Jumping;
    public byte JumpPhase;
    public byte JumpKind;          // 0=Generic, 1=NecromancerAttack, 2=Pounce
    public float JumpTimer;        // airborne elapsed time (set at liftoff)
    public float JumpDuration = 1f;// airborne total time (set at liftoff)
    public Vec2 JumpStartPos;      // captured at liftoff
    public Vec2 JumpEndPos;        // locked landing position
    public float JumpArcPeak = 2f; // parabola peak height
    public bool JumpAttackFired;
    public uint JumpPounceTargetId = GameConstants.InvalidUnit;
    // Anim playback speed during the jump (1 = normal). Used to compress the takeoff /
    // loop / land anims when the required flight time (dist / MaxSpeed) is shorter
    // than the baseline anim ms timings. 2.0 = anims play twice as fast, etc.
    public float JumpPlaybackSpeed = 1f;

    // Trample/Charge — scripted voluntary charge (TrampleSystem). Movement phases
    // through smaller units while damaging each one it passes over. ChargePhase:
    // 0=None, 1=Charging (homing at target position), 2=Recovery (post-impact lockout),
    // 3=FollowThrough (post-impact straight-line drive that physically occupies the
    // target's vacated tile; locks facing + direction at the moment of impact).
    public byte ChargePhase;
    public uint ChargeTargetId = GameConstants.InvalidUnit;
    public int ChargeWeaponIdx = -1;
    public float ChargeTraveled;    // cumulative distance since BeginCharge
    public float ChargeRecoveryTimer; // seconds left in ChargePhase==2
    public float ChargeFollowRemaining; // distance left in ChargePhase==3 follow-through
    public Vec2 ChargeFollowDir;        // unit-vector forward direction locked at impact
    // Per-charge deduped-victim set; lazy-allocated at BeginCharge and cleared
    // on EndCharge so the hashset survives future charges without reallocation.
    public System.Collections.Generic.HashSet<uint>? TrampledIds;

    // Trample-dodge — short snappy hop to a free tile when a trample swing misses.
    // While DodgeTimer > 0 the unit interpolates from DodgeStartPos to DodgeEndPos
    // over DodgeTimer seconds; AI / ORCA / facing all skip during the hop. The
    // Dodge anim is one-shot at this same duration.
    public float DodgeTimer;       // seconds remaining in the hop (0 = not dodging)
    public float DodgeDuration;    // total length of the hop, captured at start
    public Vec2 DodgeStartPos;
    public Vec2 DodgeEndPos;

    // Knockdown (weapon-bonus-driven). Tracks the per-second recovery-roll timer.
    // First check fires KnockdownCheckInitialDelay (2s) after knockdown begins,
    // then every KnockdownCheckInterval (1s). Value > 0 means a check is pending.
    public float KnockdownCheckTimer;

    public float CollisionHeight = 1.0f;

    // Incapacitation (knockdown, stun, freeze, etc.) — managed by buff system
    public IncapState Incap;
    // AI-driven sleep→standup timer (DeerHerd / WolfPack). Separate from Incap,
    // which is combat/debuff driven; the two don't overlap in practice.
    public float StandupTimer;
    public int Harassment;

    // Rendering
    public float SpriteScale = 1f;
    public Vec2 EffectSpawnPos2D;
    public float EffectSpawnHeight;

    // Floating action label (weapon/spell name shown above unit during a committed
    // action). Architectural: any attack/spell archetype writes both fields at its
    // commit point — the renderer polls these instead of probing per-archetype state.
    // Timer counts down in the main sim tick; reaching <= 0 clears the label.
    public string ActionLabel = "";
    public float ActionLabelTimer;

    // Buffs
    public List<ActiveBuff> ActiveBuffs = new();

    // Corpse interaction
    public int CarryingCorpseID = -1;       // CorpseID being carried (-1 = none)
    public int BaggingCorpseID = -1;        // CorpseID being bagged (-1 = none)
    public float BaggingTimer;              // elapsed time during bagging
    public byte CorpseInteractPhase;        // 0=none, 1=WorkStart, 2=WorkLoop, 3=WorkEnd, 4=Pickup, 5=PutDown

    // Building interaction
    public int BuildTargetIdx = -1;         // env object index being built (-1 = none)
    public int BuildGlyphIdx = -1;          // glyph index being built (-1 = none)
    public float BuildTimer;                // elapsed time during building

    // Spawn/Raid/Patrol
    public int SpawnBuildingIdx = -1;
    public int RaidTargetIdx = -1;
    public int PatrolRouteIdx = -1;
    public int PatrolWaypointIdx;

    // Caster
    public float Mana;
    public float MaxMana;
    public float ManaRegen;
    public float SpellCooldownTimer;
    public string SpellID = "";

    // Engagement & combat state
    public CombatTarget EngagedTarget = CombatTarget.None;
    public float PostAttackTimer;
    public float MoveTime;
    public QueuedUnitAction QueuedAction = QueuedUnitAction.None;

    // Ghost mode
    public bool GhostMode;

    // Dodge / React (set on combat events, cleared each tick)
    public bool Dodging;
    public bool HitReacting;
    public bool BlockReacting;

    // Stuck detection for ORCA nudge
    public int StuckFrames;

    // AI behavior framework (replaces WolfPhase/FleeTimer for new archetypes)
    public byte Archetype;
    public byte Routine;
    public byte Subroutine;
    public float SubroutineTimer;
    public byte AlertState;
    public float AlertTimer;
    public uint AlertTarget = GameConstants.InvalidUnit;
    public Vec2 SpawnPosition;
    public bool IsSneaking;

    // Two-channel animation system.
    // RoutineAnim is the base layer — AI handlers write it every frame (locomotion,
    // feeding, etc.) and is a normal public field.
    // OverrideAnim + OverrideTimer are the interrupt layer — writes MUST go through
    // AnimResolver.SetOverride so OverrideStarted stays coherent and the priority
    // replacement rules are enforced. Public getters, internal setters enforce this
    // at compile time across assemblies.
    public AnimRequest RoutineAnim;
    public AnimRequest OverrideAnim { get; internal set; }
    public float OverrideTimer { get; internal set; }

    /// <summary>Monotonic ID of the currently-queued override. Every successful
    /// SetOverride mints a new ID; 0 means "no override owned." Callers compare
    /// their stored OverrideHandle against this to detect whether their override
    /// was preempted or expired, without racing.</summary>
    public uint CurrentOverrideHandleId { get; internal set; }

    // Per-unit awareness config (set from UnitDef at spawn)
    public float DetectionRange;
    public float DetectionBreakRange;
    public float AlertDuration = 2f;
    public float AlertEscalateRange;
    public float GroupAlertRadius;

    // Status symbol above head (? for notice, ! for react). Ticked down by Simulation.
    public byte StatusSymbol;        // 0=none, 1=Notice (?), 2=React (!)
    public float StatusSymbolTimer;  // Seconds remaining before symbol clears

    // Potion effects
    public int PoisonStacks;
    public float PoisonTickTimer;
    public float CloudExposureTime; // Cumulative time spent in poison clouds
    public float WeaponPoisonCoatTimer;
    public int WeaponPoisonAmount;
    public bool ZombieOnDeath;
    public float WeaponZombieCoatTimer;
    public float ParalysisSlowTimer;
    public float ParalysisStunTimer;
    public bool Frenzied;
}

/// <summary>
/// Unit storage — list of Unit objects indexed by unit index.
/// </summary>
public class UnitArrays
{
    private readonly List<Unit> _units = new();
    // O(1) Id -> array-index map. Maintained in sync with the List via swap-and-pop
    // in RemoveUnit. Without this, ResolveUnitIndex was O(n), and ORCA's per-unit
    // neighbor resolve turned into O(u*neighbors*u) per tick (~1.4M comparisons at
    // u=310) — the biggest single cost after pathfinding was bounded.
    private readonly Dictionary<uint, int> _idToIndex = new();
    private uint _nextID;

    public int Count => _units.Count;

    public Unit this[int index] => _units[index];

    public bool TryGetIndex(uint id, out int index) => _idToIndex.TryGetValue(id, out index);

    public int AddUnit(Vec2 pos, UnitType type)
    {
        int idx = _units.Count;
        var u = new Unit
        {
            Position = pos,
            MoveTarget = pos,
            Id = _nextID++,
            Type = type,
            UnitDefID = type.ToString().ToLowerInvariant(),
            Faction = type <= UnitType.Abomination ? Data.Faction.Undead : Data.Faction.Human,
            SpawnPosition = pos,
        };
        _units.Add(u);
        _idToIndex[u.Id] = idx;
        return idx;
    }

    public void RemoveUnit(int index)
    {
        if (index < 0 || index >= _units.Count) return;
        int last = _units.Count - 1;
        if (index != last)
        {
            (_units[index], _units[last]) = (_units[last], _units[index]);
            // The unit that was at `last` now lives at `index`.
            _idToIndex[_units[index].Id] = index;
        }
        // The dying unit (now at `last`) drops out of the map before we pop.
        _idToIndex.Remove(_units[last].Id);
        _units.RemoveAt(last);
    }

    public void Clear()
    {
        _units.Clear();
        _idToIndex.Clear();
    }
}

public static class UnitUtil
{
    public static int ResolveUnitIndex(UnitArrays units, uint uid)
    {
        if (uid == GameConstants.InvalidUnit) return -1;
        if (!units.TryGetIndex(uid, out int idx)) return -1;
        return units[idx].Alive ? idx : -1;
    }

    private static readonly Random _rng = new();

    /// <summary>Roll a hit location based on size advantage and weapon length.</summary>
    public static HitLocation RollHitLocation(int attackerSize, int defenderSize, int weaponLength)
    {
        int roll = _rng.Next(100);
        int sizeAdvantage = attackerSize - defenderSize + weaponLength;
        // Larger attacker / longer weapon = more head hits
        int headChance = Math.Clamp(10 + sizeAdvantage * 5, 5, 30);
        int armsChance = 15;
        int chestChance = 40;
        int legsChance = 25;
        // remaining = feet
        if (roll < headChance) return HitLocation.Head;
        if (roll < headChance + armsChance) return HitLocation.Arms;
        if (roll < headChance + armsChance + chestChance) return HitLocation.Chest;
        if (roll < headChance + armsChance + chestChance + legsChance) return HitLocation.Legs;
        return HitLocation.Feet;
    }

    /// <summary>DRN: Dominions Random Number — 2d6 open-ended (exploding 6s)</summary>
    public static int RollDRN()
    {
        int total = 0;
        for (int die = 0; die < 2; die++)
        {
            int roll;
            do
            {
                roll = _rng.Next(1, 7);
                total += roll;
            } while (roll == 6);
        }
        return total;
    }
}

public class NecromancerState
{
    public float Mana { get; set; } = 10f;
    public float MaxMana { get; set; } = 50f;
    public float ManaRegen { get; set; } = 1f;
    public Dictionary<string, float> SpellCooldowns { get; set; } = new();
    public Dictionary<string, int> Inventory { get; set; } = new();

    public void TickCooldowns(float dt)
    {
        var keys = new List<string>(SpellCooldowns.Keys);
        foreach (var key in keys)
            SpellCooldowns[key] = MathF.Max(0f, SpellCooldowns[key] - dt);
    }

    public float GetCooldown(string id) =>
        SpellCooldowns.TryGetValue(id, out float cd) ? cd : 0f;
}
