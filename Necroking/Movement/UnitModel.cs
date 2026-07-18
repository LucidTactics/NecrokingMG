// The unit DATA MODEL, not a system: the Unit class, the SoA UnitArrays store,
// UnitUtil helpers, and NecromancerState. Behavior lives elsewhere
// (Simulation's tick phases, AI/, Movement/Locomotion).
using System;
using System.Collections.Generic;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.GameSystems;
using Necroking.Lib;
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
    /// <summary>Recovery-anim playback rate (1=normal). &lt;1 slows the rise; used by
    /// reanimation's half-speed standup. 0 is treated as 1 (default-init structs).</summary>
    public float RecoverPlaybackSpeed;

    /// <summary>True only for the hold the fatigue-collapse system installs
    /// (Simulation.UpdateUnconsciousState). Lets the wake path release ITS hold without
    /// clearing a coincident buff-driven knockdown that also uses HoldAnim=Knockdown —
    /// the anim alone can't distinguish the two owners.</summary>
    public bool CollapseOwned;

    /// <summary>Is the unit currently incapacitated or recovering?</summary>
    public readonly bool IsLocked => Active || Recovering;
}

public enum UnitStatusSymbol : byte
{
    None = 0,
    Notice = 1,  // "?" — unit has spotted a threat (e.g. deer freezes and watches)
    React = 2,   // "!" — unit is reacting (fleeing, charging, fighting back)
}

// MoveEffort enum lives in Locomotion.cs (same namespace) — the single home
// for effort / speed / gait / facing logic.

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

    /// <summary>Memoized UnitDef lookup — see <see cref="UnitUtil.ResolveDef"/>.
    /// The string-keyed registry Get was being paid per moving unit per frame
    /// (accel caps, turn speed). Reference-keyed on UnitDefID so a morph
    /// (UnitDefID reassignment) re-resolves automatically.</summary>
    public Data.Registries.UnitDef CachedDef;
    public string? CachedDefKey;

    /// <summary>AI-declared locomotion intent — set via
    /// <see cref="Locomotion.SetEffort"/>, never directly. Pure INTENT: the
    /// actual speed cap is derived from it (with ramping and modifiers) by
    /// <see cref="Locomotion.UpdateSpeeds"/> each tick.</summary>
    public MoveEffort MoveEffort;

    /// <summary>Current ramped effort multiplier (1 = base CombatSpeed, up to
    /// the effort's jog/sprint multiplier). Integrated toward the MoveEffort
    /// target at the unit's physical accel/decel rate by
    /// <see cref="Locomotion.UpdateSpeeds"/> — the generalized form of the old
    /// player-only sprint ramp. Owned by Locomotion; read-only elsewhere.</summary>
    public float EffortMult = 1f;

    /// <summary>Optional routine speed cap (fraction of effort-max speed, 1 =
    /// none), persisted from <see cref="Locomotion.SetEffort"/>'s
    /// routineCapMult so "lazy stroll" caps survive amortized AI frames.
    /// Owned by Locomotion.</summary>
    public float RoutineSpeedCap = 1f;

    /// <summary>This frame's smoothed locomotion vector,
    /// Lerp(Velocity, PreferredVel, LocoLerpFactor) — the single input for
    /// movement-animation tier selection (by length) and movement facing (by
    /// direction). Written post-movement by Locomotion each tick.</summary>
    public Vec2 LocoVector;

    /// <summary>Hysteresis flag for the necromancer's facing source. True =
    /// face velocity direction (jog/run); false = face mouse direction (walk).
    /// Enter true when speed crosses JogThreshold + hysteresis upward, exit
    /// back to false when speed drops below JogThreshold − hysteresis. Only
    /// read for PlayerControlled units in Locomotion.UpdateFacing.</summary>
    public bool FaceVelocityMode;
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

    /// <summary>Cosmetic Y-only sink applied when the unit is wading.
    /// Written each frame by the pre-draw wading pass (see Game1.
    /// UpdateWadingSinkOffsets); zero when not wading or for units that
    /// don't sink. Positive value = sprite renders below the unit's
    /// world Y (sinks toward the south on screen).
    ///
    /// Separated from <see cref="RenderOffset"/> so it composes cleanly
    /// with the attack lunge: LungeSystem unconditionally resets
    /// RenderOffset to zero between attacks; the sink shouldn't get
    /// stomped along with it. They combine in <see cref="RenderPos"/>
    /// so callers don't need to know about either.</summary>
    public float WadingSinkOffsetY;

    /// <summary>Convenience: Position + RenderOffset + wading sink. Use
    /// this everywhere a visual should follow cosmetic offsets. Gameplay
    /// code uses Position.</summary>
    public Vec2 RenderPos => new Vec2(
        Position.X + RenderOffset.X,
        Position.Y + RenderOffset.Y + WadingSinkOffsetY);

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
    /// <summary>True while the unit is collapsed from exhaustion (Fatigue reached 100,
    /// manual p.61). Managed by Simulation's fatigue tick: it drives the unit into the
    /// Incap (prone) state so all the existing Incap gates (movement/facing/attack)
    /// apply, recovers fatigue faster while down, and wakes below the wake threshold.</summary>
    public bool Unconscious;
    /// <summary>Permanent battle wounds (limb loss). Applied by slashing limb hits
    /// that cost >= 50% max HP; stat penalties are baked into Stats on application.</summary>
    public Data.Affliction Afflictions;
    /// <summary>True while the unit has broken and is fleeing the battle (morale rout).
    /// Driven by MoraleSystem; a routing unit ignores its normal AI and sprints away
    /// from the nearest threat until it rallies (RoutTimer elapsed + no threat near).</summary>
    public bool Routing;
    /// <summary>Minimum time remaining before a routing unit may rally.</summary>
    public float RoutTimer;
    public CombatTarget Target = CombatTarget.None;

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
    /// <summary>Ranged-weapon reload, owned by RangedUnitHandler. Separate from
    /// AttackCooldown because Simulation.UpdateCombat overwrites that field from
    /// the unit's MELEE weapons every tick — an archer carrying a dagger had its
    /// longbow reload wiped each frame and fired as fast as the anim allowed.</summary>
    public float RangedCooldown;
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
    // RatPack fear: seconds of "panic" remaining (set when a nearby packmate dies).
    // While >0 the rat's reflexive skitter retreats farther. Decays in the handler.
    public float PanicTimer;
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

    /// <summary>True when the unit is mid-scripted-action and should ignore
    /// manual position / facing input. Covers corpse pickup/putdown, channeling
    /// at a craft bench (WorkRoutine phases), incapacitation buffs (stun/freeze),
    /// and mid-jump frames. Add new "the player is committed for a moment"
    /// states here so input gating stays in one place — currently consumed by
    /// Game1's mouse-facing path to stop the body rotating during corpse
    /// placement. Routine != 0 is intentionally NOT included: the existing
    /// WASD-cancels-routine logic in Simulation needs the raw input to fire.</summary>
    public bool IsLockedByAction()
        => CorpseInteractPhase != 0 || Incap.IsLocked || JumpPhase != 0;

    /// <summary>True when the unit can no longer sustain a channeled cast (Beam/Drain
    /// hold-channel): any incapacitation (stun / knockdown / sleep / freeze), physics
    /// displacement (knockback launch), a paralysis stun, or a jump. The casting
    /// system (LightningSystem's per-tick caster check) cuts the unit's beams/drains
    /// the frame this turns true — channel-interrupt rules live here, not per-spell,
    /// so every current and future channel breaks consistently.</summary>
    public bool IsChannelBroken()
        => Incap.Active || InPhysics || ParalysisStunTimer > 0f || JumpPhase != 0;
    /// <summary>When non-negative during a PutDown (CorpseInteractPhase==5), the corpse
    /// will be loaded into env-object[PutDownTableIdx]'s first empty corpse slot at
    /// anim completion instead of being placed on the ground at LerpStartPos.
    /// Set by Game1's F-press handler when cursor + range gates pass; cleared at completion.</summary>
    public int PutDownTableIdx = -1;

    // Building interaction
    public int BuildTargetIdx = -1;         // env object index being built (-1 = none)
    public int BuildGlyphId = -1;           // stable glyph Id being built (-1 = none)
    public float BuildTimer;                // elapsed time during building
    public int CraftTableIdx = -1;          // env object index of the craft-table currently being channeled (-1 = none)

    // Bush-work routine (Poison Berries ability). Set when the player initiates
    // a bush-work action; cleared on completion or cancel.
    public int BushWorkObjIdx = -1;         // env object index of the berry bush being worked on (-1 = none)
    public string BushWorkBuffID = "";      // buff applied to the eater on consume (e.g. buff_poison_dot)
    public string BushWorkItemID = "";      // inventory item consumed on successful completion (e.g. potion_poison)

    // Spawn/Raid/Patrol
    public int SpawnBuildingIdx = -1;
    public int RaidTargetIdx = -1;
    public int PatrolRouteIdx = -1;
    public int PatrolWaypointIdx;

    // Worker job system (P0/P1). A unit is a "worker" while
    // Archetype == ArchetypeRegistry.Worker — housed in an Empty Grave and
    // driven by WorkerHandler. See Necroking/Game/Jobs/WorkerSystem.cs.
    public int WorkerHomeObjIdx = -1;     // env object index of the housing Empty Grave (-1 = not a worker)
    public byte WorkerPrevArchetype;      // archetype to restore on unassign
    public string WorkerJobId = "";       // JobDef.id assigned by the dispatcher ("" = idle)
    public byte WorkerPhase;              // WorkerHandler FSM phase
    public int WorkerTargetObjIdx = -1;   // env object the worker is moving to (source / storage)
    public string WorkerCarryType = "";   // resource currently carried ("" = empty-handed)
    public int WorkerCarryAmount;         // amount carried

    // Caster
    public float Mana;
    public float MaxMana;
    public float ManaRegen;
    public string SpellID = "";
    /// <summary>Per-spell cooldowns, lazily allocated — only casting units ever carry one.
    /// Written by the shared SpellCaster pipeline (via <see cref="UnitCasterResources"/>),
    /// ticked by the unit's archetype handler (CasterUnitHandler). The player necromancer's
    /// cooldowns live on <see cref="NecromancerState.SpellCooldowns"/> instead.</summary>
    public Dictionary<string, float>? SpellCooldowns;
    /// <summary>AI channel hold for Beam/Drain spells: seconds left to keep channeling. The
    /// caster's handler stands still while &gt; 0 and cancels its beams/drains when it expires.
    /// Player channels are keyed to the held spell-bar slot (Game1._channelingSlot) instead.</summary>
    public float ChannelTimer;

    // Engagement & combat state
    public CombatTarget EngagedTarget = CombatTarget.None;
    public float PostAttackTimer;

    // Ghost mode
    public bool GhostMode;
    /// <summary>Flat MaxSpeed (wu/s) for a GhostMode unit; 0 = the dev-fly default
    /// (Locomotion.GhostModeSpeed + sprint doubling). Set on spirit-walk spirits.</summary>
    public float GhostSpeedOverride;

    // Dodge / React (set on combat events, cleared each tick)
    public bool Dodging;
    /// <summary>One-tick edge flag: this unit took damage this frame. Consumed by AI
    /// (flee/retarget reactions). The flinch ANIMATION is driven separately by
    /// <see cref="HitReactTimer"/> so it can be suppressed (fleeing) / gated
    /// (refractory) without affecting the AI's read of "was I just hit".</summary>
    public bool HitReacting;
    public bool BlockReacting;
    /// <summary>True while this unit is actively fleeing/running away (morale rout is
    /// tracked by <see cref="Routing"/>; prey flee — DeerHerd RoutineFleeing,
    /// FleeWhenHit — set this). A fleeing unit does NOT play the hit-react flinch
    /// (it keeps running). Owned by the AI handlers / flee paths.</summary>
    public bool Fleeing;
    /// <summary>Seconds remaining to display the BlockReact flinch. Drives the legacy
    /// render path; set by DamageSystem.ApplyHitReactAnim (which also sets the
    /// archetype OverrideAnim). 0 = not flinching.</summary>
    public float HitReactTimer;
    /// <summary>Seconds remaining on the white hit-flash sprite tint. Set on every
    /// physical impact (DamageSystem.ApplyHitReactAnim, before its anim gates), so a
    /// hit whose flinch anim is suppressed (fleeing, mid-attack, cooldown) still
    /// visibly reads as a hit. Cosmetic only — no gameplay reads this.</summary>
    public float HitFlashTimer;
    /// <summary>Shared reaction cooldown: while > 0 the unit can't START a new
    /// reaction anim (BlockReact flinch OR Dodge), so sustained/focus-fire damage
    /// and repeated whiffs can't twitch-lock it out of acting. Set whenever a
    /// reaction anim actually plays (DamageSystem.ApplyHitReactAnim /
    /// ApplyDodgeAnim, TrampleSystem dodge-hop); ticked down each frame.</summary>
    public float ReactionCooldownTimer;

    // Stuck detection for ORCA nudge — seconds spent stuck (dt-accumulated, speed/FPS-invariant)
    public float StuckTime;

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

    /// <summary>Persistent squad membership (see <see cref="AI.SquadSystem"/>): the id of the
    /// herd/pack/patrol this unit belongs to, or 0 for "no squad". Assigned once, shortly after
    /// spawn, by <see cref="AI.SquadSystem"/>'s proximity clustering of same-faction, same-archetype
    /// units — so deer that spawn near each other share a herd, wolves a pack, soldiers a patrol.
    /// Stable across unit swap-and-pop because it's an id, not an index. The squad's live centroid,
    /// spread, alert and membership are recomputed each frame and read by the AI to keep the group
    /// together, flee as one, and let hunters flank the whole pack rather than a single animal.</summary>
    public uint SquadId;

    /// <summary>Index into VillageSystem's village list (-1 = not a village member).
    /// Set at spawn for peasants, hunters, militia and watchdogs that belong to a
    /// village. Village AI (VillagerHandler / WatchdogHandler) and VillageSystem read
    /// this to coordinate warning, mustering, and fleeing.</summary>
    public short VillageId = -1;

    /// <summary>Time spent in the current flee since entering Fleeing routine.
    /// Used by DeerHerdHandler to ramp effort (Hurry for first 2s, then Sprint).
    /// Reset to 0 whenever Routine becomes Fleeing OR is not Fleeing — so a deer
    /// that stops fleeing and starts again gets the 2s ramp from scratch.</summary>
    public float FleeElapsed;

    /// <summary>True once the unit has completed its first attack in the current
    /// combat. Used by WolfPackHandler to skip the Walk→Hurry→Sprint stalk ramp
    /// on re-engages within a hit-and-run cycle — the predator only stalks on
    /// initial contact; subsequent passes after the wait-cooldown go straight
    /// to Sprint. Reset to false when the unit leaves the Fighting routine for
    /// any reason (target dead, alert dropped, etc.) so a fresh combat starts
    /// with the stalk again.</summary>
    public bool FightCommitted;

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

    /// <summary>Multiplier on this unit's horde aggro/engagement range (see
    /// <see cref="Data.UnitDef.AggroRangeScale"/>). 1 = normal; &lt;1 = timid. Defaults to 1 so units
    /// created without a spawn-copy (scenarios, etc.) keep full range.</summary>
    public float AggroRangeScale = 1f;
    public float GroupAlertRadius;

    /// <summary>Pack-hunt "herded" cheat (see <see cref="AI.WolfPackHuntAI"/>): when a hunting wolf
    /// first engages this prey it stamps a fixed flee direction (toward the necromancer) and a short
    /// timer here. While <see cref="HerdedTimer"/> &gt; 0 the deer flee AI runs <see cref="HerdedDir"/>
    /// instead of "directly away from the nearest wolf", so the pack can steer the first bolt toward
    /// your horde. Applied once per prey (<see cref="HerdedApplied"/>) — a kick, not a leash.</summary>
    public Vec2 HerdedDir;
    public float HerdedTimer;
    public bool HerdedApplied;

    // Status symbol above head (? for notice, ! for react). Ticked down by Simulation.
    public byte StatusSymbol;        // 0=none, 1=Notice (?), 2=React (!)
    public float StatusSymbolTimer;  // Seconds remaining before symbol clears

    // Potion effects
    public int PoisonStacks;
    public float PoisonTickTimer;
    public float CloudExposureTime; // Continuous time in poison clouds; PoisonCloudSystem resets it once the unit is outside every cloud (plague needs SUSTAINED exposure)
    public bool ZombieOnDeath;
    // Set by AI (e.g. CorpsePuppetHandler) to vanish the unit with NO corpse — reaped
    // by Simulation.RemoveDeadUnits' pre-pass via RemoveUnitTracked. Distinct from death
    // (Alive=false), which spawns a corpse.
    public bool PendingDespawn;
    public float ParalysisSlowTimer;
    public float ParalysisStunTimer;
    public bool Frenzied;

    // Per-unit weapon bonus effects layered on top of weapon defs (e.g. potion
    // buffs from table-crafted zombies). Lazy-allocated — null when empty so the
    // common "no bonuses" case doesn't pay an allocation. See WeaponBonusEffect.cs.
    public List<WeaponBonusEffect>? BonusEffects;

    /// <summary>Number of corpses this unit has eaten via the Corpse Eater AI
    /// behavior. Capped at the SkillBookState payload (1 for Corpse Eater, 2
    /// for Improved Corpse Eating). Persisted on the Unit so the cap follows
    /// the individual zombie; once it's full it never eats another corpse,
    /// even after a respec.</summary>
    public byte CorpsesEaten;
    /// <summary>Eat-cycle timer (seconds left in the eating animation). > 0 means
    /// the unit is mid-eat and other AI should leave it alone. When it ticks to
    /// 0, the buff is granted and the corpse marked consumed.</summary>
    public float CorpseEatTimer;
    /// <summary>CorpseID currently being eaten (matches CorpseEatTimer's lifetime).
    /// -1 when not eating. Used to validate the corpse still exists when the
    /// timer expires.</summary>
    public int CorpseEatTargetID = -1;

    /// <summary>Number of mushrooms this (zombie boar) unit has eaten and stored in its
    /// belly. Purely a display/quick-check counter — the actual eaten mushroom def
    /// indices live in Simulation's per-unit belly store so the exact species pop back
    /// out on death. See <see cref="AI.BoarForageAI"/>.</summary>
    public byte BellyMushrooms;
    /// <summary>Eat-cycle timer (seconds left) while a foraging boar is chewing a
    /// mushroom. > 0 means the boar is holding still mid-eat; when it ticks to 0 the
    /// mushroom is consumed into the belly.</summary>
    public float BoarEatTimer;

    /// <summary>Pack-hunt AI (see <see cref="AI.WolfPackHuntAI"/>): the SQUAD id of the deer HERD
    /// this player-controlled zombie wolf is currently hunting, or 0 when not hunting. (The pack
    /// hunts the whole herd — its centroid + extent — not one animal, so this is a squad id, not a
    /// unit id.)</summary>
    public uint WolfHuntTargetId;
    /// <summary>Pack-hunt phase: 0 = flanking (circling to the far side of the prey,
    /// staying outside its vision), 1 = driving (chasing the prey toward the necromancer).</summary>
    public byte WolfHuntPhase;
    /// <summary>Seconds spent flanking on the current prey — a safety timeout so a pack
    /// that can't reach its slots (blocked path, wandering prey) eventually commits to the chase.</summary>
    public float WolfHuntTimer;

    /// <summary>For a corpse-puppet raise: the def id of the ORIGINAL corpse this puppet
    /// was raised from (e.g. "militia"), so when it deposits itself into a Corpse Pile it
    /// piles as that original body — not as the zombie variant it currently wears. Empty
    /// for a normal unit; CorpsePuppetHandler falls back to <see cref="UnitDefID"/> when unset.</summary>
    public string PuppetSourceDefID;
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

    /// <summary>Index of the live player-controlled necromancer, or -1 if none. This is
    /// the "is an alive necromancer present" liveness scan (game-over / live check),
    /// distinct from Simulation.NecromancerIndex which is a cached slot used by the HUD.</summary>
    public int FindAliveNecromancerIndex()
    {
        for (int i = 0; i < _units.Count; i++)
            if (_units[i].Alive && _units[i].AI == AIBehavior.PlayerControlled)
                return i;
        return -1;
    }

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

    /// <summary>Memoized per-unit UnitDef lookup. Replaces per-frame
    /// string-keyed registry Gets on hot paths (accel caps, turn speed).
    /// Reference-compares the cached key against UnitDefID: a morph assigns a
    /// new string instance, which misses once and re-caches — always correct,
    /// no explicit invalidation needed.</summary>
    public static Data.Registries.UnitDef ResolveDef(Unit u, Data.GameData gameData)
    {
        if (!ReferenceEquals(u.CachedDefKey, u.UnitDefID))
        {
            u.CachedDef = gameData.Units.Get(u.UnitDefID);
            u.CachedDefKey = u.UnitDefID;
        }
        return u.CachedDef;
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

    /// <summary>Tiered dice roll. Attack-vs-defense HIT rolls (melee + ranged)
    /// always use tier 4 for both sides — the open-ended die guarantees any
    /// attacker can eventually connect. Damage/protection rolls (and other
    /// contests: knockdown, morale, spell penetration) use the owning unit's
    /// tier (<see cref="UnitStats.Drn"/>).
    ///   1: d3 closed
    ///   2: d6 closed
    ///   3: d6, a 6 adds one extra d6 (max one reroll, so ≤12)
    ///   4: d6 open-ended (every 6 explodes into another die)
    ///   -1: 0 deterministic
    /// Replaced the old Dominions 2d6-open DRN (2026-07), so averages are roughly
    /// half what the ported Dominions formulas were tuned for.</summary>
    public static int RollDRN(int level)
    {
        switch (level)
        {
            case 1:
                return _rng.Next(1, 4);
            case 3:
            {
                int roll = _rng.Next(1, 7);
                if (roll == 6) roll += _rng.Next(1, 7);
                return roll;
            }
            case 4:
            {
                int total = 0;
                int r;
                do { r = _rng.Next(1, 7); total += r; } while (r == 6);
                return total;
            }
            case -1:
                return 0;
            default: // level 2 (and any unassigned/out-of-range value)
                return _rng.Next(1, 7);
        }
    }
}

/// <summary>The mana pool + per-spell cooldown store behind the shared SpellCaster
/// pipeline, so the player necromancer (<see cref="NecromancerState"/>) and AI caster
/// units (<see cref="UnitCasterResources"/>) pay for and cool down spells through the
/// same code path (TryStartSpellCast deducts / stamps, RefundSpellCast undoes).</summary>
public interface ICasterResources
{
    float Mana { get; set; }
    float GetCooldown(string spellId);
    void SetCooldown(string spellId, float seconds);
    void ClearCooldown(string spellId);
}

/// <summary>ICasterResources over a plain unit's caster fields (Unit.Mana + the
/// lazily-allocated Unit.SpellCooldowns dict) — lets AI casters run the same
/// SpellCaster pipeline as the player necromancer. Allocated per cast at the
/// Game1 drain site; casts are rare so the small allocation is fine.</summary>
public sealed class UnitCasterResources : ICasterResources
{
    private readonly UnitArrays _units;
    private readonly int _idx;

    public UnitCasterResources(UnitArrays units, int idx) { _units = units; _idx = idx; }

    public float Mana
    {
        get => _units[_idx].Mana;
        set => _units[_idx].Mana = value;
    }

    public float GetCooldown(string spellId)
    {
        var cds = _units[_idx].SpellCooldowns;
        return cds != null && cds.TryGetValue(spellId, out float cd) ? cd : 0f;
    }

    public void SetCooldown(string spellId, float seconds) =>
        (_units[_idx].SpellCooldowns ??= new())[spellId] = seconds;

    public void ClearCooldown(string spellId) =>
        _units[_idx].SpellCooldowns?.Remove(spellId);

    private static readonly List<string> _tickScratch = new();

    /// <summary>Tick a unit's per-spell cooldowns down (rate 1, unlike the
    /// necromancer's buff-scaled <see cref="NecromancerState.TickCooldowns"/>).
    /// Static scratch keeps the per-frame handler tick allocation-free —
    /// safe because AI handlers run single-threaded from Simulation.Tick.</summary>
    public static void TickCooldowns(Dictionary<string, float>? cds, float dt)
    {
        if (cds == null || cds.Count == 0) return;
        _tickScratch.Clear();
        foreach (var key in cds.Keys) _tickScratch.Add(key);
        for (int k = 0; k < _tickScratch.Count; k++)
            cds[_tickScratch[k]] = MathF.Max(0f, cds[_tickScratch[k]] - dt);
    }
}

public class NecromancerState : ICasterResources
{
    public float Mana { get; set; } = 10f;
    public float MaxMana { get; set; } = 50f;
    public float ManaRegen { get; set; } = 1f;
    /// <summary>Per-frame additive bonus on top of ManaRegen. Set by Game1 each
    /// tick from passive skill effects whose condition can change at runtime
    /// (e.g. Death Fog Consumption: +2 while standing in death fog). Reset to
    /// zero by the caller before the conditions are re-evaluated.</summary>
    public float BonusManaRegen { get; set; }
    public Dictionary<string, float> SpellCooldowns { get; set; } = new();
    public Dictionary<string, int> Inventory { get; set; } = new();

    /// <summary>Base spell-cooldown recharge rate (1.0 = real time). Effective rate
    /// is this scaled by buff modifiers on the necromancer (e.g. god mode multiplies
    /// it by 10 → spells cool down 10× faster). Cooldowns tick down by dt × rate;
    /// see <see cref="TickCooldowns"/>. Higher = faster recharge.</summary>
    public float CooldownRate { get; set; } = 1f;

    /// <summary>Max permanent undead minions of UndeadCategory.Monster the
    /// player can field at once (zombie animals/monsters). Skill-tree nodes
    /// raise this. Starts at 1 so the player can summon one starter pet
    /// before any investment. Player necromancer + temporary summons don't
    /// count. See HordeCapTracker for the count logic.</summary>
    public int MonsterCap { get; set; } = 1;

    /// <summary>Max permanent undead minions of UndeadCategory.Human the
    /// player can field at once (skeletons, abominations). Starts at 0 —
    /// human raising is gated behind an early skill-tree node.</summary>
    public int HumanCap { get; set; } = 0;

    /// <summary>Tick all spell cooldowns down. <paramref name="rate"/> is the
    /// effective cooldown rate (base <see cref="CooldownRate"/> after buff modifiers)
    /// — cooldowns drop by dt × rate, so rate=10 recharges 10× faster. A rate ≤ 0
    /// freezes cooldowns.</summary>
    public void TickCooldowns(float dt, float rate = 1f)
    {
        if (rate <= 0f) return;
        float step = dt * rate;
        var keys = new List<string>(SpellCooldowns.Keys);
        foreach (var key in keys)
            SpellCooldowns[key] = MathF.Max(0f, SpellCooldowns[key] - step);
    }

    public float GetCooldown(string id) =>
        SpellCooldowns.TryGetValue(id, out float cd) ? cd : 0f;

    public void SetCooldown(string id, float seconds) => SpellCooldowns[id] = seconds;

    public void ClearCooldown(string id) => SpellCooldowns.Remove(id);
}
