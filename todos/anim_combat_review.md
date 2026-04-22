# Animation + AI + Combat Review

**Scope:** animation pipeline, AI archetype handlers (routines/subroutines), combat engine (attack queue, damage, overrides), and their interactions.
**No code changes in this pass.** This is a diagnostic + proposal document.

---

## 0. Architecture snapshot

```
Frame:
  Simulation.Update(dt):
    UpdateAI         → handlers write Target, PreferredVel, RoutineAnim, EngagedTarget
    UpdateMovement   → ORCA + accel curve write Velocity, Position
    UpdatePhysics    → airborne/knockback
    UpdateCombat     → derive InCombat; tick per-weapon cooldowns + PostAttackTimer;
                       queue PendingAttack (weapon scan, per-weapon archetype)
    TickBuffs        → Incap lifecycle (Active/Recovering), knockdown, poison
  Game1.UpdateAnimations(dt):
    for each unit:
      if JumpPhase != 0: JumpSystem.TickUnit → continue    ← skips the rest
      if archetype > 0:
        set OverrideAnim from PendingAttack or pre-roll
        AnimResolver.Resolve → picks winner(override vs routine), ForceState
        LocomotionScaling.ComputeLocomotionPlayback → overwrites PlaybackSpeed
        AnimController.Update(dt)
      else: legacy path with RequestState/ForceState
      ConsumeActionMoment → ResolvePendingAttack at effect_time
```

**Two-channel anim resolver**
- `RoutineAnim` (priority 0-1, persistent) set by AI handlers.
- `OverrideAnim` (priority 2-3, temporary) set by combat / damage / physics.
- `OverrideStarted` tracks whether the controller has entered the override state so we can auto-expire it when the controller moves on.

**Handler model**
- `IArchetypeHandler` per unit-type: `WolfPackHandler`, `HordeMinionHandler`, `CombatUnitHandler`, `DeerHerdHandler`, `RangedUnitHandler`, `PlayerControlledHandler`.
- Each handler drives `Routine` (high-level state) and `Subroutine` (step within state) using byte indices.
- `SubroutineSteps` is a static-method library (MoveToTarget, AttackTarget, Disengage, WaitForCooldown, SetLocomotionAnim, …) reused across handlers.

---

## Progress log

- **Round 1 (commits 9247c71)** — §1.1 – §1.4 addressed. See §5.9 closed as a downstream effect of §1.1.
- **Group A (commit bb9dc69)** — §5.3 effect_time load warnings, §5.4 buff-anim validation logs, §5.5 attack refund on dead target, §5.6 SetOverride same-priority-until-started, §5.7 Incap RecoverTimer=-1 re-init, §5.10 RoutineAnim=Idle in AttackTarget, §5.12 Jump phase 3/4 safety timeouts.
- **Group B (commit c54a0ae)** — §5.11 InCombat single-writer + `JustEnteredCombat` / `JustLeftCombat` edge events; §5.8 amortized minions wake on urgent events (hit, combat edge).
- **Group C (commit 78766fc)** — §5.2 `Weapon.Priority` field + stable-sort at load; editor exposes it; ties break by list order for backward compat.
- **Group D (commit 89041a9)** — §5.1 `LocomotionProfile` consolidates thresholds/hysteresis for SetLocomotionAnim and LocomotionScaling; Game1 only overwrites PlaybackSpeed for locomotion states so attack-anim compression survives.
- **Group E (deferred)** — §5.13 Timers container and §5.14 IAttackArchetype interface are large architectural refactors that don't fix concrete bugs. See scope notes below; recommend gating on design agreement before implementing.
- **Architecture tier (commits fdc4629, 08e3064)** — debug overlay showing per-unit anim state, `AnimInvariants` DEBUG-only assert suite, `AnimController` edge flags (`JustEntered/JustExited/JustHitEffectFrame/JustFinished`) replacing destructive `ConsumeActionMoment`, `OverrideKind` enum (OneShot/Hold/TimedHold) replacing implicit `Duration` encoding, `Unit.OverrideAnim` encapsulated as `{ get; internal set; }` so cross-assembly direct writes are a compile error.
- **Horde bug pass (commit 72e7d8d)** — two user-reported bugs fixed: "minion stood still fighting fleeing enemy" (UpdateEngaged had no out-of-melee exit) and "chaser dragged horde across map" (UpdateChasing had no leash check). Extracted `CombatTransitions.StandardEngagedExits` + `StandardChasingExits` helper so the canonical exits (target dead / target out of melee / leash break) are authored once. HordeMinionHandler delegates; future handlers (Wolf Fighting, Deer FightBack) can plug in the same helper. Added regression scenarios `horde_engaged_kiting`, `horde_chase_leash`, `horde_target_teleport` — the teleport scenario uncovered a follow-on bug where a stale `PendingAttack` from the engaged phase pinned the chaser via the movement-lockout; helper now clears `PendingAttack` + `PostAttackTimer` on transition.
- **OverrideHandle API** — `AnimResolver.SetOverride` now returns an `OverrideHandle`; paired `ClearIfOwned(unit, handle)` lets a caller safely tear down its override without racing a higher-priority preemption. The caller's handle is invalidated on preempt / auto-expire, so the Clear is a guaranteed no-op in that case. Callers that don't need safe teardown can ignore the return.

### Group E — scope & recommendation

**§5.13 Timers container (≈200–300 LOC, 50+ touch-sites)**
- Replace per-field timers (`AttackCooldown`, `PostAttackTimer`, `StatusSymbolTimer`, `BaggingTimer`, `PoisonTickTimer`, `SubroutineTimer`, `JumpTimer`, `Incap.RecoverTimer`, per-weapon `Cooldown`, etc.) with a `TimerBag` indexed by enum key.
- Each timer key has a documented owner (which system writes) and read semantics.
- Central per-tick decrement loop instead of ad-hoc decrements scattered across Simulation/BuffSystem/JumpSystem/PotionSystem.
- **Pros:** single debug view of every timer on a unit; easier to add new timers; one-writer invariant becomes enforceable.
- **Cons:** touches every timer read/write in the codebase; adds an indirection layer on hot code paths; risks regressing working timer logic without a corresponding bug to fix.
- **Recommendation:** defer. Revisit if we add more timers (retarget cooldowns, stance timers, ability-charge timers) — right now it's pre-factoring.

**§5.14 IAttackArchetype interface (≈200 LOC)**
- New interface `IAttackArchetype` with `CanInitiate(unit, target, weapon)`, `Initiate(…)`, `OnResolve(…)`.
- Implementations: `NormalMeleeArchetype` (current default path), `PounceArchetype` (wraps `JumpSystem.BeginPounce` + landing resolution).
- Simulation.UpdateCombat's weapon scan dispatches to the archetype instead of inline if-branches; JumpSystem becomes an internal utility called by `PounceArchetype`.
- **Pros:** adding future archetypes (Charge, Knockback, Stomp) is a new class, not an edit to Simulation. Clean integration point.
- **Cons:** refactor with no current behavioral win (only one non-None archetype exists). Risks changing combat semantics subtly (order of field writes, exact timing of JumpSystem init).
- **Recommendation:** defer until we're adding the second non-None archetype. Then doing 5.14 *becomes* the integration pattern for the new one.

## 1. Confirmed bugs (must-fix)

### 1.1 `OverrideStarted` isn't reset on direct `OverrideAnim` assignment
**Where:** [Game1.cs:3197-3219](Necroking/Game1.cs#L3197-L3219) writes `_sim.UnitsMut[i].OverrideAnim = AnimRequest.Combat(...)` directly every frame; [AnimResolver.SetOverride](Necroking/Render/AnimResolver.cs#L96-L107) is the only place that resets `OverrideStarted`. So every call site that skips `SetOverride` leaves `OverrideStarted` stuck at whatever value it was.
**Failure mode:** previous attack's `OverrideStarted=true` carries into the next attack's first frame, and AnimResolver's "mismatch + started → clear" branch wipes the fresh override one frame after it's assigned. Causes a 1-frame Idle flash between attacks; worse, in bad timing causes the new attack to *skip its first anim cycle entirely* because the mismatch clear happens on the post-transition-to-Idle frame.
**Same bug class:** [DamageSystem.cs:80](Necroking/Game/DamageSystem.cs#L80) (BlockReact), [DamageSystem.cs:85](Necroking/Game/DamageSystem.cs#L85) (Death), [Simulation.cs:2081/2130/2137](Necroking/Game/Simulation.cs#L2081) (dodge/block), [PhysicsSystem.cs:97](Necroking/Game/PhysicsSystem.cs#L97) (Fall), [PotionSystem.cs:324](Necroking/Game/PotionSystem.cs#L324) all directly assign `OverrideAnim` and bypass the `OverrideStarted` reset.
**Recommended fix scope:** the Combat / pre-roll / damage sites should funnel through `SetOverride`, OR AnimResolver should detect "state changed under me" by remembering the state the override was last seen playing (a generation/request ID, see §6 industry section).

### 1.2 Pounce landing callback has no guard against the underlying `PendingAttack` being lost
**Where:** [JumpSystem.cs:307-324](Necroking/GameSystems/JumpSystem.cs#L307-L324). `FireLandingCallback` gates itself on `JumpAttackFired`, but does not verify `PendingAttack.IsUnit` or `PendingWeaponIdx >= 0`. `sim.ResolvePendingAttack` early-returns if the target is None, silently swallowing the pounce damage.
**Failure mode:** any code path that clears `PendingAttack` mid-pounce (e.g. Disengage in a wolf's hit-and-run phase timer, target death, or a future AI routine that resets combat state) makes the pounce land and do nothing with no warning.

### 1.3 Legacy `WolfPhase == WolfDisengage` code path clears `PendingAttack`/`PostAttackTimer` but isn't actually dead
**Where:** [Simulation.cs:2410-2462](Necroking/Game/Simulation.cs#L2410-L2462). This is the pre-archetype-handler wolf state machine. Archetype wolves (all of them per the current data) don't hit this code, but `selfManagesCombat` at [Simulation.cs:591-594](Necroking/Game/Simulation.cs#L591-L594) explicitly routes `WolfHitAndRun` / `WolfHitAndRunIsolated` / `WolfOpportunist` AIs through it. If any scenario or config still assigns those AIBehaviors instead of the Archetype, you get two combat state machines fighting (the legacy one clears `PendingAttack`, the archetype one tries to use it).
**Recommendation:** delete legacy wolf code if no live units reach it; otherwise, document the dispatch so both paths can coexist without corrupting each other.

### 1.4 `DamageSystem.Apply` overwrites `OverrideAnim` for BlockReact even if the victim is in the middle of a scripted jump
**Where:** [DamageSystem.cs:80](Necroking/Game/DamageSystem.cs#L80). We guard against overwriting while `Incap.Active`, but not while `JumpPhase != 0`. A unit mid-pounce can get hit by a simultaneous AoE or counter-attack, which sets `OverrideAnim = BlockReact`. JumpSystem doesn't consult OverrideAnim (it directly forces JumpTakeoff/Loop/Land on the controller each frame), so visually you get the jump anim — but the moment JumpPhase reaches 0, AnimResolver runs, sees a live BlockReact override, and pops the landed unit into a block-react pose mid-recovery.
**Recommendation:** in DamageSystem, `if (JumpPhase != 0) skip the visual override` (same treatment as Incap.Active).

---

## 2. Fragile / easy-to-break interactions

### 2.1 JumpSystem has no timeout for Phase 3 (Landing) or Phase 4 (Recovery)
**Where:** [JumpSystem.cs:235-272](Necroking/GameSystems/JumpSystem.cs#L235-L272). Takeoff has a 3s safety timeout. Landing/Recovery rely entirely on `ctrl.ConsumeActionMoment()` and the anim's PlayOnceTransition auto-switch. If anim metadata is missing (effect_time=0), Landing can stick until the fallback `t >= 1f` fires; Recovery waits for the controller to leave the land anim which requires the anim to actually complete. A broken/absent JumpLand sprite hangs the unit in phase 4 with `PreferredVel=Zero` — AI is locked out by the `if (JumpPhase != 0) break;` guard in every handler.
**Why it matters:** any art/data regression makes a unit unplayable with no in-game indicator.

### 2.2 `Incap.Recovering` + `RecoverTimer` handshake is fragile
**Where:** [AnimResolver.cs:24-29](Necroking/Render/AnimResolver.cs#L24-L29) initializes `RecoverTimer` from the real anim duration, but only when `Recovering && RecoverTimer < 0`. [BuffSystem.cs:102](Necroking/Game/BuffSystem.cs#L102) sets `Recovering=true` but I don't see the initialization to `-1` cleanly guarded; if a new incap buff lands on a unit whose `Incap` struct was copied from a previous recovery, `RecoverTimer` may already be 0 and the resolver won't re-initialize it.
**Why it matters:** knockdowns that instant-recover, or refuse to recover, depending on which incap preceded.

### 2.3 `InCombat` is re-derived every frame from `EngagedTarget + range`; handlers read it as if it's sticky
**Where:** derivation at [Simulation.cs:1798-1815](Necroking/Game/Simulation.cs#L1798-L1815); read as a stable predicate at [WolfPackHandler.cs:106](Necroking/AI/WolfPackHandler.cs#L106) (retarget-on-hit gate), [Simulation.cs:1898](Necroking/Game/Simulation.cs#L1898) (melee weapon eligibility), [Game1.cs:3207](Necroking/Game1.cs#L3207) (pre-roll branch).
**Failure mode:** during one tick `InCombat` can flip false mid-frame if movement has pushed the target out of range. The pre-roll branch in Game1 runs *after* AI+Movement+Combat, so its `InCombat` read is authoritative for that frame — but the AI handler already made its decision using the previous frame's `InCombat`. This is a one-frame-stale problem that routinely produces "unit queues attack, then immediately re-queues because InCombat false" oscillations.
**Why it matters:** attack-queue flapping in close-range chases, especially when ORCA is pushing units around.

### 2.4 `SetOverride` treats *any* priority-equal request as replacement
**Where:** [AnimResolver.cs:96-107](Necroking/Render/AnimResolver.cs#L96-L107). A stun buff (Priority=2) and a combat attack (Priority=2) can interleave: a dodge fires, then a hit lands the same frame, then another dodge — each resets `OverrideStarted=false`, so by the time the third call lands the controller hasn't actually entered any of those states yet. Last-writer-wins with no "did the previous one get to play at all" check.
**Recommendation:** block replacement while `!OverrideStarted` unless the new request has *strictly* higher priority.

### 2.5 `ConsumeActionMoment` can fire during a pre-roll while no `PendingAttack` is queued
**Where:** [Game1.cs:3357-3372](Necroking/Game1.cs#L3357-L3372). Pre-roll kicks Attack1 before the weapon cooldown expires so effect_time aligns with the cooldown end. If dt jitter pushes `_animTime >= effect_time` one frame *before* `AttackCooldown` hits 0, ConsumeActionMoment fires, `PendingAttack.IsNone` → no damage resolves, `_actionMomentFired=true` for the rest of this anim. The queued attack (which arrives a frame later) then plays to completion without resolving damage because the flag is already consumed.
**Why it matters:** ghost attacks with no damage output, visually indistinguishable from a swing-and-miss.

### 2.6 Weapon list order silently encodes attack priority
**Where:** [Simulation.cs:1882-1911](Necroking/Game/Simulation.cs#L1882-L1911). Whichever weapon is listed first and off-cooldown fires. For wolves, Bite (idx 0) always beats Pounce (idx 1) when both are in range. Editor UX doesn't indicate this; reordering the JSON changes combat behavior invisibly.

### 2.7 `PostAttackTimer` is owned by two systems
**Where:** set by [Simulation.cs:1867/1908/1947](Necroking/Game/Simulation.cs#L1867), ticked by [Simulation.cs:1783-1784](Necroking/Game/Simulation.cs#L1783-L1784), *also* zeroed by [SubroutineSteps.Disengage](Necroking/AI/SubroutineSteps.cs#L198-L221), [WolfPhase == WolfDisengage](Necroking/Game/Simulation.cs#L2431), and [Simulation.Disengage](Necroking/Game/Simulation.cs#L2537).
**Why it matters:** whoever disengages last wins, and there's no single record of "this timer is for X". If Simulation ticks it to 0 in the combat phase and AI Disengage zeroes it in the next frame's AI phase, the two writes compete for meaning.

### 2.8 Hardcoded magic numbers gate behavior transitions
Scan of numbers that should be data-driven:
- [WolfPackHandler.cs:390](Necroking/AI/WolfPackHandler.cs#L390) — `SubroutineTimer > 0.1f` gate on ExecuteAttack→Disengage (below ~60ms cooldowns, disengage fires before the attack registers).
- [WolfPackHandler.cs:49](Necroking/AI/WolfPackHandler.cs#L49) — `DisengageDistance = 4f`.
- [WolfPackHandler.cs:50](Necroking/AI/WolfPackHandler.cs#L50) — `IdleRoamRadius = 10f`.
- [WolfPackHandler.cs:92](Necroking/AI/WolfPackHandler.cs#L92) — `RetargetOnHitWindow = 2.0f`.
- [WolfPackHandler.cs:52-53](Necroking/AI/WolfPackHandler.cs#L52-L53) — SitDuration, SleepDetectionScale, StandupDuration.
- [SubroutineSteps.cs:192-195](Necroking/AI/SubroutineSteps.cs#L192-L195) — `minTime = 0.2f` default.
- [DamageSystem.cs:94](Necroking/Game/DamageSystem.cs#L94) — poison re-tick at 3s (should be per-poison-type).
- [JumpSystem.cs:34-38](Necroking/GameSystems/JumpSystem.cs#L34-L38) — AirHorizontalSpeed, MinAirDuration, DefaultArcPeak, TakeoffSafetyTimeout.

None of these are per-unit or per-weapon. A "fast biter" archetype can't have shorter disengage timers; a "lazy stalker" wolf can't have longer alert windows; etc.

### 2.9 `IsFacingTarget` runs before range check; dead weight when far
**Where:** [Simulation.cs:1877-1890](Necroking/Game/Simulation.cs#L1877-L1890). Facing is validated before any weapon-range check, so a unit 100 tiles away still runs facing logic per-frame.

### 2.10 `HoldAnim` / `IncapHoldAnim` doesn't validate against the sprite
**Where:** [BuffSystem.cs:52-70](Necroking/Game/BuffSystem.cs#L52-L70). A buff JSON that names a `Knockdown` anim will silently fall back to Idle if the unit's sprite lacks that anim — so the "incapped" unit stands there normally instead of going prone.

---

## 3. Skating / flicker / bad-transition causes

### 3.1 Fixed flicker (done this conversation): SetLocomotionAnim without hysteresis
**Where:** [SubroutineSteps.cs:389+](Necroking/AI/SubroutineSteps.cs#L389). Velocity oscillating ±0.1 around the 0.25 Idle/Walk boundary caused RoutineAnim to flip every frame → AnimResolver.ForceState → SwitchState → `_animTime=0` → walk animation frozen on frames 0-1. Fixed with proportional hysteresis bands around each tier boundary, remembered via the unit's previous `RoutineAnim.State`.

### 3.2 LocomotionScaling wipes combat override playback speed
**Where:** [Game1.cs:3236-3237](Necroking/Game1.cs#L3236-L3237). For non-locomotion states, `ComputeLocomotionPlayback` returns `1.0`, and Game1 unconditionally writes it to `ctrl.PlaybackSpeed`. If AnimResolver just set `PlaybackSpeed` to a compressed attack speed (e.g. `2.3` because the anim is being stretched to fit a short weapon cycle), this line overwrites it to `1.0` every frame. **Compression in Attack1 effectively does not work.**
**Fix direction:** LocomotionScaling should early-out and *not write* to PlaybackSpeed for non-locomotion states, leaving the winner's speed intact.

### 3.3 Reverse walk in AnimController doesn't check `_currentState == Walk` consistently
**Where:** [AnimController.cs:405](Necroking/Render/AnimController.cs#L405) and [Game1.cs:3225](Necroking/Game1.cs#L3225). The reverse flag is set from `velocity.Dot(facingDir) < -0.3`. If the unit is strafing or slightly off-axis, the dot product may flip over ORCA jitter, causing the walk anim to alternate forward/backward — visible as foot-stomping.

### 3.4 `SwitchState` resets `_actionMomentFired=false`
**Where:** [AnimController.cs:307-316](Necroking/Render/AnimController.cs#L307-L316). This is correct when a new attack starts. But when a *pre-roll* Attack1 auto-transitions to Idle (PlayOnceTransition) and the actual attack re-forces Attack1, `_actionMomentFired` resets — and the effect fires twice (first during pre-roll with nothing queued, second during the real attack). See §2.5.

### 3.5 `MoveToward` chooses tierSpeed from `actualSpeed` when preferred is non-trivial
**Where:** [SubroutineSteps.cs:380-382](Necroking/AI/SubroutineSteps.cs#L380-L382). At start of motion, `PreferredVel` is full-speed but `Velocity` is still accelerating from 0. The Walk/Jog threshold is hit before the foot stride can catch up. Low-accel units show a brief "running-in-place-at-frame-0" look during the first 100-200ms of motion, because the anim state changes faster than the anim can play one cycle.

### 3.6 Handlers don't consistently write RoutineAnim when stationary-in-range
**Where:** [SubroutineSteps.AttackTarget](Necroking/AI/SubroutineSteps.cs#L174-L190) writes PreferredVel=0 inside melee range but does not call `SetLocomotionAnim`. So RoutineAnim retains whatever the last non-zero mover set (e.g. Walk), even though the unit is now stationary. Most of the time this is hidden behind the combat override; when OverrideAnim auto-expires between attacks and RoutineAnim wins, the unit does a phantom Walk in place for one frame.

### 3.7 `Carry` locomotion scaling bypasses the threshold model
**Where:** [LocomotionScaling.cs:72-78](Necroking/Render/LocomotionScaling.cs#L72-L78). Carry scales `speed/baseSpeed` directly, no hysteresis. A carrying unit that stops gets `0/baseSpeed = 0` → clamped to WalkFloor (0.25x) — so the carry anim keeps playing at 25% speed while the unit stands still. That's by design (small residual motion animating the pose) but surprising.

---

## 4. Consolidation opportunities — integrated, data-driven solutions

This is the section the user flagged as the key theme. The current codebase has handler-per-archetype with a lot of similar-but-not-identical sequences. The big wins are moving those to data.

### 4.1 Per-archetype combat state machines are 70% identical
Compare `WolfPackHandler.UpdateFighting` and `HordeMinionHandler` combat paths. Both do: `pick target → move into range → face → attack → wait → re-engage`. The differences are **data** (disengage distance, subroutine timings, whether to cooldown in place or back away), but they're implemented as **divergent code**.

**Proposal:** a single **CombatLoopStep** library (or `TryXxx` static methods like SubroutineSteps already does) plus a per-unit `CombatProfile` data record:
```
CombatProfile:
  engageStrategy:    { Direct | Hit-and-run | Guard | Kiting }
  disengageDist:     float (0 for charge, 4 for wolf, 10 for archer)
  postAttackBehavior: { Hold | Back | Strafe | Reposition }
  retargetWindow:    float
  minExecuteTime:    float  // replaces hardcoded 0.1f
```
Each handler becomes: `EvaluateRoutine` for policy-level decisions + `SubroutineSteps.RunCombatLoop(profile)` for the mechanical part. See §6 industry notes on behavior-tree tasks / steering libraries.

### 4.2 Pounce is wired as a one-off; the "scripted leap" concept is general
JumpSystem is already fairly general (two kinds: NecromancerAttack, Pounce). But integration is scattered: Simulation.InitiatePounceWithWeapon, JumpSystem.BeginPounce, JumpSystem.FireLandingCallback → ResolvePendingAttack. If we add a third kind (e.g. spider leap, ambush drop) it'll need another bespoke integration. **Data-driven weapon archetypes already exist** (`WeaponArchetype.Pounce`) — extending the weapon-archetype enum to `{ None, Pounce, Charge, Knockback, Stomp, … }` and letting WeaponStats carry the arc/range/anim params makes the whole thing one integration path.

### 4.3 Alert/awareness logic is duplicated inside each handler's `EvaluateRoutine`
Wolves, deer, hordes, peasants all interpret AlertState → next routine. Same idiomatic "if hit, promote alert; if aggressive, enter fighting; if unaware, return to default" sequence repeated. Could be a shared `AlertPolicy` table per unit-def with action mappings (`OnHit → { Flee | Fight | Wake | Ignore }`).

### 4.4 Locomotion selection, playback speed, and reverse playback are three systems that all read velocity
- `SetLocomotionAnim` (hysteresis on speed)
- `LocomotionScaling` (playback rate as function of speed)
- `SetReversePlayback` (forward/backward decision from velocity dot facing)

All run in different files, on different timing. A single `LocomotionProfile.Compute(unit, velocity, prev)` → `(state, playbackSpeed, reverse)` is a more robust entrypoint and removes the mid-frame race where one has updated and another hasn't.

### 4.5 `OverrideAnim` is set by 8+ call sites, each direct assignment
[Game1.cs:3205/3218](Necroking/Game1.cs#L3205), [DamageSystem.cs:80/85/156](Necroking/Game/DamageSystem.cs#L80), [Simulation.cs:2081/2130/2137](Necroking/Game/Simulation.cs#L2081), [PhysicsSystem.cs:97](Necroking/Game/PhysicsSystem.cs#L97), [BuffSystem.cs:66/120/148](Necroking/Game/BuffSystem.cs#L66), [DeerHerdHandler.cs:363](Necroking/AI/DeerHerdHandler.cs#L363), [PotionSystem.cs:324](Necroking/Game/PotionSystem.cs#L324). Every one of these bypasses the `SetOverride` reset logic (§1.1).
**Proposal:** make `OverrideAnim` private/internal, route everything through `AnimResolver.SetOverride` (or equivalent). Then the lifecycle logic can evolve in one place — including an optional request-ID for proper "did this one actually play" tracking (see §6).

### 4.6 PostAttackTimer / AttackCooldown / weapon.Cooldown / JumpSystem.JumpTimer / SubroutineTimer / StatusSymbolTimer / BaggingTimer — six separate per-unit timer fields
Each one is one-off code. A simple `Timers` container with enum keys (or indexed array) would let handlers use a single API and make timer bugs easier to find.

### 4.7 Handler routines/subroutines are opaque byte indices
`ctx.Routine = 2; ctx.Subroutine = 1; ctx.SubroutineTimer = 0f;` is repeated across dozens of sites. Each handler re-defines routine constants with overlapping indices (RoutineIdleRoaming=0 in wolves, something else =0 in deer). A `RoutineTransition(routine, subroutine)` helper with debug-friendly name lookup would reduce boilerplate and make debug visualization trivial.

### 4.8 The "pre-roll attack anim so effect_time lines up with cooldown end" concept is tied to `AnimState.Attack1` only
**Where:** [Game1.cs:3212-3218](Necroking/Game1.cs#L3212-L3218). Hardcoded `AnimState.Attack1`. Wolves use AttackBite (override), but a unit whose first weapon uses Attack2 would have its pre-roll play Attack1 (wrong anim). Generalize to `ResolvePendingAttackAnim(weapon 0)`.

---

## 5. Risk matrix — detailed breakdowns

Each row below walks the same flow: **trigger** (what the designer/player/data does to hit it) → **what the code does** (frame-by-frame, where it lives) → **what the player sees** → **minimal fix** (patch in place) vs **integrated fix** (data-driven refactor).

---

### 5.1 New unit with unique locomotion curve (Risk: H, fragility cost recurring per new unit)

**Trigger:** a new unit (giant, crawler, swarm insect) is added with `CombatSpeed` much smaller or larger than existing units, or a unique accel curve.

**What happens now:**
- [SubroutineSteps.cs:389+](Necroking/AI/SubroutineSteps.cs#L389) computes `jogThreshold = 4 + baseSpeed/3` and `runThreshold = 6 + 2*baseSpeed/3`. For a `CombatSpeed = 2` snail, jog=4.67, run=7.33 — both above the snail's max speed. The snail will *never* enter Jog or Run, which may or may not be the design intent, but nobody explicitly said so.
- Hysteresis bands (fix just shipped) scale with `baseSpeed * 0.05`, so a snail's band is 0.1 wide while a wolf's is 0.6 wide. Either too tight (snail re-flaps at 0.3/0.4 boundary) or way too wide (unit refuses to transition to Walk until speed > 1.5).
- [LocomotionScaling.cs:29-30](Necroking/Render/LocomotionScaling.cs#L29-L30) duplicates the same threshold formulas. Any change to one must be mirrored to the other — easy to miss.

**Player sees:** unit locked in Walk animation at all speeds, or switches gaits at wrong speeds, or feet slide (stride doesn't match ground speed).

**Minimal fix:** add per-unit-def overrides for `jogThreshold` and `runThreshold` and hysteresis band sizes. Fall back to the formula if not set.

**Integrated fix:** a `LocomotionProfile` data asset on each `UnitDef`:
```
LocomotionProfile {
  tiers: [{ state: Walk, enter: 0.25, exit: 0.15, cycleSeconds: ? },
          { state: Jog,  enter: 4.5,  exit: 3.5,  cycleSeconds: ? },
          { state: Run,  enter: 7.5,  exit: 6.0,  cycleSeconds: ? }]
  backwards: { threshold: -0.3, state: Walk }
}
```
Handler + LocomotionScaling both consume this one asset. Industry analogue: Unreal's Blend Space samples, just flattened to a table. **Fixes two bugs with one refactor** (consolidation per user's theme).

---

### 5.2 Designer flips two weapons in a unit's weapon list (Risk: H, silent behavior change)

**Trigger:** designer reorders `"weapons": ["weapon_bite", "weapon_pounce"]` to `"weapons": ["weapon_pounce", "weapon_bite"]` in the editor or JSON, intending no behavior change — "it's still the same two weapons, right?".

**What happens now:**
- [Simulation.cs:1882-1911](Necroking/Game/Simulation.cs#L1882) scans weapons in list order and fires the *first* one off-cooldown and in-range. With Pounce at index 0: whenever Pounce is off-cooldown and the target is in the 3-8 tile band, Pounce fires. Bite never gets a chance while Pounce is eligible. With Bite at index 0 (current): Bite fires in melee, Pounce fires at medium range.
- Comment at [Simulation.cs:1850](Necroking/Game/Simulation.cs#L1850) says this is intentional, but the editor surfaces no indication.
- Because Pounce has a 9s cooldown vs Bite's 3s, flipping the order changes attack tempo by ~3x.

**Player sees:** wolf spends most of combat circling at pounce range instead of closing. Damage output drops. Feels like a broken AI.

**Minimal fix:** add an explicit `priority` int on `WeaponStats` (higher = checked first), default to 0. Editor sorts display by priority. List-order coincidence is no longer the mechanism.

**Integrated fix:** `CombatProfile` (§4.1) defines attack selection rules as data — "prefer pounce when dist > 3, else bite", "prefer the highest-damage weapon off cooldown", "cycle through weapons". Weapons become a set, not a sequence. Industry analogue: *Monster Hunter* weapon movelists use explicit priority tables and conditions, never implicit ordering.

---

### 5.3 Animation asset shipped without `effect_time` metadata (Risk: H, late-catch regression)

**Trigger:** artist exports `Wolf_AttackBite.png` / `.animationmeta` but the meta file has `effect_time_ms: 0` or the field is missing. Sprites look fine in the asset viewer, so it ships.

**What happens now:**
- [AnimController.HasReachedActionMoment](Necroking/Render/AnimController.cs#L554-L566) — when effectMs<=0, falls through to `_animTime >= totalTicks * 0.5f`. So attacks resolve at 50% of anim duration regardless of what the artist intended.
- [JumpSystem.TickTakeoffApproach](Necroking/GameSystems/JumpSystem.cs#L181-L203) — `ConsumeActionMoment` is the preferred liftoff trigger. If effect_time is 0, the action moment fires at 50% of anim, which is often wrong for a takeoff pose. Fallback is a 3-second `TakeoffSafetyTimeout` — player sees unit "winding up" for 3 full seconds before liftoff.
- [JumpSystem.cs:91-94](Necroking/GameSystems/JumpSystem.cs#L91-L94) — baseline_ms uses `takeoffEffect`; if 0, baseline is (takeoffTotal + loopTotal + 0). The compression math underestimates flight duration and the land anim fires early.

**Player sees:** attacks that "miss" visually but damage registers halfway through the anim. OR pounces that hang in takeoff for 3 seconds. OR attacks that come out faster than the animation suggests.

**Minimal fix:** at scenario load (or asset load), log a loud warning for any animation marked as an attack/takeoff/land with `effect_time <= 0`. Make JumpSystem's fallback a short hold (~0.3s) rather than 3s.

**Integrated fix:** treat `effect_time` as required metadata and fail the asset load in editor/dev builds if missing for an attack state. Ship-build can use defaults (center of anim) with telemetry. Industry analogue: Unreal's AnimNotify system enforces that attack timing come from explicit notifies; without a notify, the attack just doesn't exist.

---

### 5.4 Buff references an anim the unit sprite doesn't have (Risk: M, silent visual failure)

**Trigger:** a designer creates a "Stun" buff with `IncapHoldAnim: "Knockdown"`, then applies it to a ranged unit whose sprite has only Idle/Walk/Attack1 (no Knockdown).

**What happens now:**
- [BuffSystem.cs:52-70](Necroking/Game/BuffSystem.cs#L52-L70) parses the name into the `AnimState.Knockdown` enum — succeeds syntactically regardless of whether the sprite has that anim.
- The incap override is set. AnimResolver forces the unit into AnimState.Knockdown.
- AnimController.ResolveAnimForState at [AnimController.cs:208-227](Necroking/Render/AnimController.cs#L208-L227) tries to load the anim, falls back through `Knockdown → Death`, and if neither exists, returns Idle.
- Unit is "incapacitated" but plays Idle animation — looks like it's just standing around while taking damage.

**Player sees:** stunned units walking around or standing normally. Gameplay feels broken.

**Minimal fix:** at buff application time, call `AnimController.HasAnim(state)` on the target's sprite and log a warning if missing. Fall back to a universal "Hit" pose.

**Integrated fix:** the `UnitDef` declares its **anim catalog** — which states the sprite supports. The buff/ability system validates at load time that every referenced state exists in every unit-def that can receive the buff. Same pattern as Unreal's `AnimMontage` slot validation. Also lets the editor show only valid anim references in dropdowns.

---

### 5.5 `PendingAttack` target dies between queue and resolve (Risk: M, swallowed damage + ghost swing)

**Trigger:** Unit A queues an attack on Unit B (sets PendingAttack, Bite.Cooldown=3s). On the next AI tick, Unit B dies (killed by a third party, ally spell, etc.). Unit A's attack anim keeps playing; at effect_time, `ResolvePendingAttack` is called.

**What happens now:**
- [Simulation.ResolvePendingAttack](Necroking/Game/Simulation.cs#L1974) resolves the target from `PendingAttack`; if target is dead, `ResolveUnitTarget` returns -1, and the method exits silently.
- Unit A's AttackCooldown remains at 3s from the queue — unit has "used" a swing it can't apply.
- PostAttackTimer remains at animDur, so Unit A is locked out of other actions for 0.8s.
- Because Unit B is dead, `InCombat` is now false (EngagedTarget dead). WolfPackHandler's `FightExecuteAttack` subroutine's transition gate `AttackCooldown > 0 && PostAttackTimer ≤ 0 && SubroutineTimer > 0.1f` may still fire, sending the wolf into Disengage with no target.

**Player sees:** unit performs a phantom swing in open air, then awkwardly backs away. 3s later it can attack again. Feels like AI latency.

**Minimal fix:** in ResolvePendingAttack, after the target-dead no-op, refund the attack: clear cooldown and PostAttackTimer so the attacker can immediately retarget. Also add a log.

**Integrated fix:** **target-locked commits** (industry pattern) — once PendingAttack is queued, keep the attacker's target reference as a weak handle and automatically retarget in the same frame if the original died. The anim still plays but points at the new target. *Dark Souls* / *Monster Hunter* do this for locked-on attacks: the attack redirects to the closest remaining enemy in arc.

**ALSO:** added logging in §1.2 fix surfaces this regression via `[LandingCallback] PendingAttack was cleared…` — same mechanism can help here.

---

### 5.6 Multiple overrides stacking in one frame (Risk: M, after §1.1 fix)

**Trigger:** unit is mid-attack (Attack1 override). Takes a hit → DamageSystem assigns BlockReact override. Simultaneously, a stun buff assigns Stunned override. Same frame.

**What happens now (post §1.1 fix):**
- All three now route through `SetOverride`. Each call resets `OverrideStarted=false` and checks priority.
- Attack1 (Priority=2) → BlockReact (Priority=2, same, replaces) → Stunned (Priority=3, higher, replaces). Final OverrideAnim = Stunned. 
- This frame's AnimResolver sees Stunned, ForceState(Stunned), ctrl plays Stunned. Correct.
- But between frames, if the assignment *order* is (Attack1, Stunned, BlockReact), we get Stunned replaced by BlockReact (same priority, last-writer-wins). The hit reaction overwrites the stun visually, even though the stun should dominate.

**Player sees:** a stunned unit playing a hit-react animation instead of a stun pose.

**Minimal fix:** in `SetOverride`, replace "same priority replaces" with "same priority replaces only if current `OverrideStarted`". A not-yet-played priority-2 override can't be overwritten by another priority-2 one. Forces the ordering to be "highest priority wins, then first-come first-served within a priority".

**Integrated fix:** **priority lanes** — each override channel is a stack ordered by priority; a new request is inserted at its priority rank, not overwritten. When one pops, the next-highest takes over. Industry analogue: Unreal's `AnimMontage` slots with `BlendWeight` — multiple montages can be queued and the engine picks the winner each frame. Also cleanly supports "extend the current hit-react by another 0.2s" by stacking.

---

### 5.7 `Incap` struct copied/overwritten between buffs (Risk: M, instant-recovery or stuck-knockdown)

**Trigger:** unit gets knocked down (Knockdown buff, Incap set with `HoldAnim=Knockdown`, `Recovering=false`, `RecoverTimer=0`). Before recovery starts, a Stun buff is applied (another Incap with `HoldAnim=Stunned`).

**What happens now:**
- [BuffSystem.cs:45-70](Necroking/Game/BuffSystem.cs#L45-L70) — the second Incap assignment overwrites the first. If the unit had been about to enter recovery (Recovering transition in TickBuffs), that state is lost. `RecoverTimer=0` in the new Incap means `AnimResolver` at [AnimResolver.cs:24-29](Necroking/Render/AnimResolver.cs#L24-L29) initializes RecoverTimer from anim duration *only* when `RecoverTimer < 0`. At 0 it doesn't re-init; recovery completes instantly.
- Alternatively, if the two buffs have overlapping durations, TickBuffs's "buff expired without early recovery trigger" path fires when one buff ends, but both Incap slots share the one state field — the second buff's hold anim (Stunned) is discarded when Knockdown's recovery animation plays.

**Player sees:** either a unit that pops up instantly from knockdown despite a remaining stun, or a unit stuck in Knockdown pose after the knockdown buff has expired because the stun's Incap state "won".

**Minimal fix:** initialize `RecoverTimer = -1f` whenever a new Incap is assigned. Verify TickBuffs' recovery trigger checks *all* incapacitating buffs, not just one.

**Integrated fix:** one `IncapState` per unit backed by a **stack of incap contributors** (knockdown, stun, paralysis). Each contributor has its own duration. The unit stays incapped while any contributor is active, using the highest-priority hold anim. Only on the last contributor's expiry does recovery fire. Industry analogue: *WoW* / *Dota 2* debuff stacking — "hard CC" is tracked per source with priority, single-pose rendering pulls from the stack.

---

### 5.8 AI amortized tick misses a state promotion during combat (Risk: M, one-frame-to-one-AI-cycle delay)

**Trigger:** `AmortizedAI=true`, `AIUpdateInterval=6` frames. A minion in Following state (amortized, every 6 frames) gets a hit that should promote it to Chasing.

**What happens now:**
- [HordeMinionHandler.cs:62](Necroking/AI/HordeMinionHandler.cs#L62) — `if (lowUrgency && !ctx.IsAmortizeTick) return;` — skips the handler entirely.
- The hit event (`HitReacting=true`, `LastAttackerID` set) sits on the Unit for up to 6 frames. During that time the handler's Following logic doesn't re-evaluate threat.
- Meanwhile, Simulation.UpdateCombat *does* set `InCombat=true` if the target is now in range, but the handler's routine is still Following — so its subroutine logic runs the Following step (march to slot) with an unrelated target.

**Player sees:** minion takes a hit, ignores it for ~100ms, then reacts. Feels like input/AI lag in combat.

**Minimal fix:** bypass amortization when `HitReacting || InCombat` — high-urgency signal always forces a real tick.

**Integrated fix:** **event-driven AI wakeups** — the AI system subscribes to a set of "urgent events" (OnHit, OnTargetAcquired, OnBuffApplied), and any fire bumps the unit to "this frame" scheduling regardless of amortization bucket. Industry analogue: *Halo 2*'s BT "stimulus system", most modern AI frameworks (Unreal AIPerception) have an event bus that bypasses perception cadence for combat-relevant events.

---

### 5.9 dt spike pushes `_animTime` past effect_time before cooldown ends (Risk: FIXED in §1.1)

**Status:** this pass's `ConsumeActionMoment` pre-check prevents ghost attacks. Leaving the analysis here for documentation:

Before the fix, on a frame where `dt` was 40ms (spike from 16ms, e.g. GC pause), pre-roll's `_animTime` could jump from 380ms to 420ms in one step, crossing effect_time (400ms) in a frame where `AttackCooldown` was still >0. `ConsumeActionMoment` returned true, marked the moment fired, but `PendingAttack.IsNone` so no damage resolved. The real attack, queued next frame, never got to fire its action moment because `_actionMomentFired=true`. The anim played its full cycle without registering damage.

Now (post-fix): `ConsumeActionMoment` is only called if `hasPendingCast || hasPendingAttack`. No pending consumer → no consumption → the moment stays "unfired", and the real attack (when it arrives) gets its resolution.

---

### 5.10 Unit standing still but RoutineAnim stuck in Walk (Risk: L, subtle visual polish)

**Trigger:** wolf chases target, stops (in range), starts attacking. AttackTarget subroutine sets `PreferredVel=0` but does not update `RoutineAnim`. RoutineAnim stays Walk from the chase.

**What happens now:**
- OverrideAnim (Combat(Attack1)) wins over Walk → visually correct during attack.
- Between attacks, OverrideAnim auto-expires. Winner flips to Walk. Unit is at velocity 0 but ctrl plays Walk → locomotion scaling clamps to WalkFloor (0.25x speed). Unit appears to walk-in-place for the cooldown window.

**Player sees:** between swings, the unit's feet are walking in place. Low-grade ugly.

**Minimal fix:** [SubroutineSteps.AttackTarget](Necroking/AI/SubroutineSteps.cs#L174-L190) — when `dist <= attackRange`, also call `SetLocomotionAnim(ref ctx, 0)`.

**Integrated fix:** handler no longer directly writes RoutineAnim. A single end-of-handler step computes RoutineAnim from `PreferredVel + current subroutine intent`. "Intent" is metadata the subroutine sets (Moving / Stationary / AttackRecovery / Feeding / …). **Removes every ad-hoc RoutineAnim write in SubroutineSteps.** Industry analogue: locomotion state in motion-matching systems is a derived property, never hand-written.

---

### 5.11 InCombat re-derived every frame → handlers read stale value (Risk: M, one-frame race)

**Trigger:** two wolves surround a deer. Wolf A is in melee, Wolf B is 1.1 tiles outside melee. Deer moves 0.2 tiles between AI and Combat phases.

**What happens now:**
- AI phase (Wolf B's handler): reads `InCombat=false` (from last frame), decides to keep chasing. Sets PreferredVel toward deer.
- Movement phase: Wolf B moves, deer doesn't move yet.
- Combat phase [Simulation.cs:1798-1815](Necroking/Game/Simulation.cs#L1798-L1815): recomputes InCombat using *current* positions. Wolf B is now in range → InCombat=true.
- Combat phase attack scan: Wolf B's InCombat is true, Bite is off-cooldown — queues PendingAttack. Attack anim fires next frame.
- Wolf A meanwhile: InCombat was true last frame, so handler queued an attack. After deer moved, InCombat might be false now. Attack anim plays anyway (PendingAttack doesn't un-queue).

**Player sees:** occasional "attack an empty space" or "move through the target to attack" glitches in close-quarters combat. Usually not noticeable with fast-moving targets because positions settle within a tick.

**Minimal fix:** add explicit `OnEnterCombat` / `OnExitCombat` hooks on InCombat transitions, so handlers can subscribe to the edges rather than read the level. Cached with one writer.

**Integrated fix:** **one-writer invariant** — Combat phase (only) writes InCombat. Handlers read what combat wrote *last* frame (stable). For the most reactive use cases, add an explicit event bus. Industry analogue: *Overwatch* architecture (Tim Ford, GDC 2017) — every flag has exactly one system that writes it, and edge-transitions fire events consumed next frame.

---

### 5.12 JumpSystem Phase 3/4 has no timeout (Risk: M, unit lockout on missing assets)

**Trigger:** unit initiates a pounce, but the JumpLand anim asset fails to load (corrupt .png, missing .animationmeta).

**What happens now:**
- [JumpSystem.TickLanding](Necroking/GameSystems/JumpSystem.cs#L235-L257) fires on `ConsumeActionMoment || t >= 1f`. If the land anim has no meta, HasReachedActionMoment falls back to 50% of tick-based total, which still fires. But if the *anim* is missing entirely, ctrl.ForceState(JumpLand) sets the state to JumpLand anyway (AnimController doesn't validate), but rendering falls back to Idle.
- Bigger risk: [JumpSystem.TickRecovery](Necroking/GameSystems/JumpSystem.cs#L259-L272) checks `ctrl.CurrentState != landAnim` to call EndJump. If somehow the state never transitions away from landAnim (e.g. because the PlayOnceTransition loop never completes), EndJump never fires. JumpPhase=4 forever. All AI handlers have `if (JumpPhase != 0) break` guards — unit is locked out of AI and combat permanently.

**Player sees:** a unit stuck in a jump-recovery pose mid-combat, refusing to do anything.

**Minimal fix:** Phase 3/4 both get safety timeouts (e.g. 2.0s). If exceeded, force EndJump regardless of anim state.

**Integrated fix:** JumpSystem gets a phase-timeout table as data, one per kind (`{ Pounce: takeoff=3s, airborne=2s, landing=1s, recovery=1.5s }`). All phases share a timeout path. Industry analogue: any robust state machine in AAA has "watchdog" timers per state.

---

### 5.13 PostAttackTimer has multiple owners (Risk: L-M, diverging behavior across AIs)

**Trigger:** wolf attacks, Simulation sets `PostAttackTimer=0.8s`. Wolf handler decides to Disengage. `Disengage()` at [SubroutineSteps.cs:203](Necroking/AI/SubroutineSteps.cs#L203) zeros PostAttackTimer explicitly. Meanwhile Simulation.UpdateCombat continues to decrement PostAttackTimer at [Simulation.cs:1783-1784](Necroking/Game/Simulation.cs#L1783-L1784).

**What happens now:**
- Order matters: AI runs first. Disengage zeros PostAttackTimer. Combat decrement is a no-op.
- The "intent" of PostAttackTimer is "wait for attack anim to finish". If AI zeros it early, the next combat scan may try to queue another attack *during* the animation — which is blocked by `PostAttackTimer > 0` check. But AI already zeroed it, so the block lifts. Combat fires next PendingAttack; AnimController is still playing Attack1; Override stays Attack1 via SetOverride (post-§1.1); effect moment may have already fired. Weird timing.

**Player sees:** occasionally a second attack queues while the first anim is still resolving, leading to a visibly-dropped swing.

**Minimal fix:** one writer. Either AI or Combat owns the timer; pick Combat. AI can *check* the timer to decide whether to disengage yet, not zero it.

**Integrated fix:** a **Timers container** (§4.6) with semantic keys like `ATTACK_LOCKOUT`, `COMBAT_COOLDOWN`. Each timer has a documented owner. The container exposes read-only views to non-owners. Industry analogue: explicit capabilities / access tokens in an ECS.

---

### 5.14 Weapon archetype enum is a one-off extension point (Risk: L, scales poorly)

**Trigger:** designer wants to add "Charge" attack (run at target, knock aside, hit at impact) alongside existing Pounce.

**What happens now:**
- [Enums.cs WeaponArchetype](Necroking/Data/Enums.cs) would need a new `Charge` value.
- New if-branch in Simulation.UpdateCombat's weapon selection loop to handle `Archetype == Charge`.
- New initiation method paralleling `InitiatePounceWithWeapon`.
- JumpSystem doesn't know about Charge; new system or extension of JumpSystem.
- Editor UI needs new fields for charge-specific params.

**Each new archetype is ~200 lines of integration code** duplicated with small differences.

**Minimal fix:** add Charge; accept the duplication.

**Integrated fix:** generalize the archetype interface. Each archetype is a `IAttackArchetype` with methods:
```
bool CanInitiate(unit, target, weapon) → bool
void Initiate(unit, target, weapon) → sets up state, may hand off to JumpSystem
void OnResolve(unit, target, weapon) → damage application, side effects
```
Each archetype is a class registered in a dictionary keyed by enum. New archetype = new class, no edits to Simulation. Industry analogue: Unreal GAS's `GameplayAbility` — each ability is a class with standard lifecycle hooks, new abilities plug in without engine changes.

---

## 6. Industry practice — how AAA handles each of these

(Concise references — full citations in the industry briefing.)

### Locomotion without flicker (§3.1, §4.4)
The shipped AAA answer is **blend spaces with sync markers**, not state switches. Unreal's **1D Blend Space** + **Sync Markers** (`LeftFootDown` / `RightFootDown`) normalizes phase across Walk/Jog/Run samples — no discrete state transitions fire, so no `_animTime` reset to trip over. Unity's equivalent is Animator **Sync Groups**. *Uncharted 4*, *TLOU2*, *Hellblade* all use this.
**Relevance:** current state-switch + hysteresis approach is the "FSM-era" pattern. For a 2D MonoGame project, going to a real blend-space system is probably overkill, but **preserving phase across SwitchState** (don't reset `_animTime` when transitioning Walk↔Jog↔Run, just re-anchor the foot cycle) is the cheap imitation.

### Foot sliding / skating (§3.5, §4.4)
Three standard layers, stacked:
1. **Cadence-matched playback rate** — `PlayRate = actualSpeed / clipImpliedSpeed`. This is what LocomotionScaling does. Good baseline; breaks down past ±1.5x.
2. **Root motion** — *Dark Souls*, *For Honor*, *Assassin's Creed*. Anim drives translation. Not a great fit for ORCA-authoritative RTS-style movement.
3. **Motion Matching** — *For Honor* (Simon Clavet, GDC 2016), Unreal 5 built-in. Frame-by-frame database query on velocity + pose → picks the next clip. Eliminates foot-sliding by construction; too heavy for this project.
**Pragmatic fix for this codebase:** ensure LocomotionScaling doesn't overwrite non-locomotion playback speeds (§3.2), authoring-side ensure Walk/Jog/Run clips start at the same foot-phase so `SwitchState → _animTime=0` doesn't desync, and if investing more, add foot IK pinning (two-bone IK on stance frames).

### Override channels (§1.1, §2.4, §4.5)
Unreal's **AnimMontage** slot model is the reference. Each montage play returns a **handle / request ID** — the code that started it owns the cancel, and `OnCompleted` / `OnInterrupted` dispatchers fire exactly once. Unity Animator **layers with avatar masks** is the alternative.
**Standard failure modes named and fixed:**
- **Stale overrides** → request ID / generation counter (caller can tell "did my request ever play")
- **Double-fire notifies** → `ConsumedTag` on notifies
- **Phantom transitions** → request TTLs
**Relevance:** the `OverrideStarted` bool is a poor-man's ID. Replacing it with a monotonic `OverrideRequestId` — where AnimResolver records the ID of the request it saw last match and any new assignment gets a fresh ID — cleanly handles the double-replacement and "did it play" cases.

### Scripted leaps / pounce (§1.2, §2.1, §4.2)
Unreal 5 **Motion Warping** (`RootMotionModifier` with named warp targets: Takeoff, Apex, Contact, Recovery) is the modern standard. Anim author places notifies at key frames; runtime stretches the middle to hit the physical target. *Dark Souls* lunge, *DMC* launcher, *Monster Hunter* aerial attacks all use bespoke implementations of the same pattern.
**Standard fallback when metadata missing:** games default to computed flight time (`t = 2*v_y/g` for a ballistic arc) plus a 150-250ms blend-to-landing. Necroking already has anim-time compression logic — needs a cleaner fallback path and a Phase 3/4 timeout as backstop.

### Attack queuing, cancel windows (§2.5, §2.7, §4.6)
Named terminology: **Startup / Active / Recovery** frames. **Cancel windows** (named frame ranges where specific inputs interrupt recovery). **Input buffer** (6-12 frames, ~100-200ms). **Commit flag** (what can interrupt what, usually a priority-based cancel matrix).
Standard data shape:
```
AttackData:
  startup_ms, active_ms, recovery_ms
  hitbox_frames: [int]
  cancelWindows: [{ frameStart, frameEnd, cancelable_into: [AttackId] }]
  commitPriority: int
```
**Relevance:** `PostAttackTimer` conflates startup+active+recovery into one timer. Splitting it would let AI correctly decide "I can cancel into a pounce now" without guessing at timing. Shipping games call this **"the frame data"** — and it's *always* data-driven.

### Behavior Trees vs HFSM (§4.1, §4.3)
- **HFSM / handler code** (current) — small teams, small unit count. *StarCraft*, *AoE*, *Factorio*.
- **Behavior Trees** — *Halo 2* (Damian Isla, GDC 2005), most AAA since. Data-driven, designer-authorable, standard in Unreal.
- **Utility AI** — *The Sims*, *RDR*, *Guild Wars 2*. Best for competing-priorities scenarios.
- **GOAP** — *F.E.A.R.*, *Tomb Raider 2013*. Emergent planning; heavy tooling.

**Rubric for this project:** stay with handlers until ~8+ distinct unit behaviors; then move to **lightweight data-driven BTs** (JSON-defined; no visual editor needed). *Factorio*, *RimWorld*, *They Are Billions* did exactly that.

### Steering + step library (§4.1, §4.3, §4.6)
The industry names:
- **Behavior Tree Leaf Tasks** — `MoveToTarget`, `FaceTarget`, `WaitForAnim`, `CheckInRange` — reusable across any BT.
- **Steering Behaviors** (Craig Reynolds, 1999) — Seek, Flee, Arrive, Pursue, Wander compose into locomotion.
- **Smart Objects** — *The Sims*, *Horizon Zero Dawn* — environmental nodes advertise actions; agents query.

**Relevance:** `SubroutineSteps` is already 70% of the way there (it *is* a step library). The remaining 30% is:
1. Handlers stop hand-coding subroutine indices → compose steps.
2. `RoutineProfile` as data — per-archetype JSON defines which steps to run in which order.

### Hold-at-end / freeze final frame (AnimResolver.ForceStateAtEnd)
Three industry models:
1. **ClampForever** (Unity) / **Animation Asset Loop=false + End-state** (Unreal). Engine holds final pose indefinitely. Default for death/knockdown.
2. **Dedicated "End Pose" state** — state machine transitions from `Death_Play` to `Death_Held` at `normalizedTime >= 0.99`. Used when cleanup logic needs a distinct state.
3. **Sample-and-freeze** — evaluate pose at `duration - epsilon`, cache, reapply.

**Relevance:** `ForceStateAtEnd(_animTime = 999999)` is a hack version of (1). Works but is fragile in the face of missing metadata. Cleaner would be a `HoldAtEnd` flag on the `AnimRequest` itself — "play this, on reach last frame, stay there".

### Derived flags vs cached state (§2.3)
**AAA convention:** cached state with explicit `OnEnterX` / `OnExitX` transitions. Reasons:
- Determinism for networking/replay (*Overwatch*, *Rocket League* — Tim Ford, GDC 2017).
- Event hooks fire once, not every frame (music stingers, UI flashes).
- Cheaper — flag read vs per-system re-derivation.
**One-frame-stale problem** is fixed by:
- Fixed system update order (ECS-style).
- Double-buffered state (write to `next`, read from `current`, swap at frame boundary — standard in lockstep RTS like *StarCraft*).
- Event queues.

**Relevance:** `InCombat` being derived every frame across multiple readers is the exact anti-pattern AAA explicitly avoids. Cheap fix: cache it with one writer (UpdateCombat) and an explicit `BecameInCombat / BecameOutOfCombat` event.

---

## 7. Suggested priority order

**Round 1 — hard bugs (this week)**
1. §1.1 `OverrideStarted` not reset on direct override assignment (and the 8 call sites that bypass SetOverride). Single root-cause for the most visible glitches.
2. §1.2 Pounce landing callback missing `PendingAttack` guard + a fallback-damage path when target is gone.
3. §1.4 DamageSystem BlockReact override during JumpPhase.
4. §2.1 JumpSystem Phase 3/4 safety timeouts.
5. §3.2 LocomotionScaling not overwriting non-locomotion playback speed (fixes compression silently being disabled).
6. §2.5 + §3.4 Pre-roll / ConsumeActionMoment race (ghost attacks).

**Round 2 — fragile foundations (next week or two)**
7. §4.5 / §1.1 make `OverrideAnim` private, route everything through a single entry point. Add a request-ID generation counter (industry pattern).
8. §2.3 cache `InCombat` with explicit transitions (`OnEnterCombat`/`OnExitCombat`) — lets handlers stop reading a racy flag.
9. §2.7 single-ownership of `PostAttackTimer` — pick one writer.
10. §3.6 every AI step that stops a unit should also call `SetLocomotionAnim(Idle)`.

**Round 3 — consolidation (when you want to scale)**
11. §4.1 `CombatProfile` data record; handlers compose `CombatLoopStep` calls instead of inlining.
12. §4.2 extend `WeaponArchetype` enum so future leap-like attacks go through one integration path.
13. §4.6 unified `Timers` container.
14. §2.8 move hardcoded magic numbers to per-archetype / per-weapon data.
15. Industry ref — blend-space-with-sync-markers for 2D locomotion would eliminate §3.1, §3.3, §3.5 root causes. Non-trivial port; probably not worth until frame flicker is otherwise solved.

---

## 8. Things that look scary but are probably fine

Documented here so we don't accidentally touch them.

- **AnimResolver priority tie-breaker logic** ([AnimResolver.cs:63-70](Necroking/Render/AnimResolver.cs#L63-L70)) has a redundant branch (the `else` at line 70 is unreachable), but is correct by accident. Leave.
- **ForceStateAtEnd with `_animTime=999999`** works because PlayOnceHold clamps to totalMs on next Update, and GetCurrentFrame's tick-fallback clamps to the last keyframe. Only breaks if meta is totally absent AND tick data is also absent, which is already an error state.
- **Weapon list order encoding priority** (§2.6) is intentional per comment at [Simulation.cs:1850](Necroking/Game/Simulation.cs#L1850). It's a smell but not a bug — as long as the editor surfaces the order clearly.
- **Amortized AI** for low-urgency routines is by design and working.
