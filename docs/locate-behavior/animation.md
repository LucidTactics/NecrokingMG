# Animation — unit anim state, the two-channel resolver, and every writer

How a unit's current animation (Idle/Walk/Attack/Dodge/Knockdown/Death/…) is chosen each
frame, and the full census of code paths that SET it. The authoritative rules-of-the-road
doc-block lives at the top of `Necroking/Render/AnimController.cs` — read it before
touching anything here.

```
RoutineAnim  (Unit field, AI/locomotion writes it raw every frame)   ─┐
                                                                       ├─ AnimResolver.Resolve
OverrideAnim (Unit field, ONLY via AnimResolver.SetOverride)         ─┘   → AnimController (per-unit, in Game1._unitAnims)
```

- **Priority scale** (higher wins; override wins ties): 0 Locomotion, 1 Action
  (Sit/Sleep/Feed/Carry), 2 Combat (attacks, Dodge*, BlockReact), 3 Forced/Hold
  (Death, Knockdown, Fall, Standup, Stunned). Build requests via the
  `AnimRequest.Locomotion/Action/Combat/Forced/Hold` factories — never raw priority ints.
  (*the melee-miss Dodge is deliberately priority 1 so it can't cancel the defender's own swing.)
- **Lifecycle** (`OverrideKind`): OneShot auto-clears when played; Hold is caller-owned
  (clear via the `OverrideHandle` + `ClearIfOwned`); TimedHold expires after Duration.
- **Two render paths** in `Game1.Animation.cs` `UpdateAnimations`: units with
  `Archetype > 0` use the two-channel resolver; `Archetype == 0` (legacy: the
  necromancer/player path, old AIBehavior units) uses a hand-rolled if/else `targetState`
  chain + `RequestState`/`ForceState`. Bugs often exist in one path and not the other.

## Files

### `Necroking/Render/AnimController.cs`
The per-unit playback machine + all the static policy tables. `enum AnimState` (the
canonical state list), `AnimRequest` + factories, `OverrideKind`, `OverrideHandle`,
`AnimPlayMode` / `GetPlayMode` (Loop / PlayOnceHold / PlayOnceTransition per state),
`GetFallbackAnimName` (Run→Walk, Knockdown→Death, Standup→Idle …; anything else falls
back to the Idle CLIP while `CurrentState` stays the requested state),
`GetStatePriority` + `IsInterruptible` (the controller's own internal queue policy used
by the legacy `RequestState` path), `StateToAnimName`, angle-sector resolution with
hysteresis (`ResolveAngle`), foot-phase carryover between locomotion states, edge flags
(`JustHitEffectFrame` = the attack hit-frame trigger, `JustFinished` — ONE-FRAME flags).
**`IsMovementLocked(state)` exists but has ZERO callers** — there is no engine-level
"this anim stops movement" enforcement; movement stopping is done by unrelated per-system
flags (PendingAttack pin, PostAttackTimer, Incap.IsLocked, InCombat legacy-zeroing).
Look/edit here when: an anim loops when it should play once, fallback clip is wrong,
sprite facing flickers, hit-frame timing (effect_time_ms) is off.

### `Necroking/Render/AnimResolver.cs`
`AnimResolver` — the per-frame arbiter. `Resolve(unit, ctrl, dt)` (called from
`UpdateAnimations` for archetype units only): ticks TimedHold expiry, tracks
`OverrideStarted`, auto-clears finished OneShots, picks winner
(override ≥ routine priority → override), then **`ForceState(winner.State)`** — the
routine channel can never interrupt a live higher/equal-priority override.
`SetOverride(unit, request)` = the ONLY legal override write (returns `OverrideHandle`);
same-priority replacement rejected until the current override actually started.
`ClearIfOwned(unit, handle)` = safe teardown; `ClearOverride(unit)` = unconditional drop.
Look/edit here when: an override never clears, a queued anim gets stolen same-frame,
priority arbitration is wrong.

### `Necroking/Movement/UnitModel.cs` (Unit fields — the state storage)
`Unit.RoutineAnim` (plain field — AI writes raw, **last-writer-holds**: it persists until
some code overwrites it, there is no auto-reset to locomotion), `Unit.OverrideAnim` /
`OverrideTimer` / `OverrideStarted` / `CurrentOverrideHandleId` (internal setters —
compile-time forced through AnimResolver). Related gates: `Incap` (`IncapState`:
`HoldAnim`/`RecoverAnim`/`IsLocked`/`HoldAtEnd`), `HitReactTimer` (legacy flinch render),
`FlinchRefractoryTimer`, `Fleeing`/`Routing` (flinch suppression), `Dodging`,
`HitReacting` (one-tick AI edge, separate from the flinch anim), `PostAttackTimer`,
`InCombat` (derived by combat phase from EngagedTarget + melee range — no other writer),
`MoveEffort` (gait bias), `JumpPhase`, `CorpseInteractPhase`.

### `Necroking/Game1.Animation.cs` — the per-frame tick (`UpdateAnimations`)
Owns `_unitAnims` (uid → `UnitAnimData{Ctrl,…}`; rebuilt by `RebuildUnitAnim`). Per unit:
- **Corpse-interact branch** (CorpseInteractPhase != 0): drives WorkStart/Loop/End,
  Pickup/Carry/PutDown by direct `ForceState`, bypassing both channels. **Anti-pattern
  (egregious):** this branch also runs *gameplay* off `Ctrl.IsAnimFinished` — case 5
  (PutDown) transfers the carried corpse into the table slot, removes it from the sim, and
  fires `StartTableCraft` (spends essence + queues a zombie raise, commit `4f1e851`); cases
  1-4 consume corpses on anim edges too. Craft start / corpse consumption should live on a
  gameplay timer, not on `IsAnimFinished`. See anti-patterns-list.md.
- **Archetype branch** (`Archetype > 0`): stamps the Combat override for `PendingAttack`
  (`ResolvePendingAttackAnim`, compressed to the weapon cycle), stamps the **attack
  pre-roll** override when `InCombat && AttackCooldown > 0` (so effect_time lands at
  cooldown end — this RE-STAMPS EVERY FRAME while the condition holds), cancels a stale
  attack swing once the unit moves and `!InCombat && PendingAttack.IsNone &&
  PostAttackTimer<=0`, then `AnimResolver.Resolve`, then locomotion playback scaling
  (`LocomotionScaling.ComputeLocomotionPlayback` — only for Walk/Jog/Run/Carry), then
  `ctrl.Update`, `LungeSystem.Update`, `AnimInvariants.Check`.
- **Legacy branch** (`Archetype == 0`): priority chain in code order — InPhysics→Fall,
  Incap hold/recover, `Dodging`→Dodge, PendingAttack→attack anim, `HitReactTimer`→
  BlockReact, `PostAttackTimer`→Block, pre-roll→Attack1/Block, GhostMode→Hover, else
  gait from max(Velocity, PreferredVel) with Carry override.
- Also here: `UpdateChanneledCast` (necromancer Imbue/Raise Start→Loop→Finish — drives
  the controller directly), `ComputeWeaponCycleSeconds`.

### `Necroking/Render/AnimInvariants.cs`
DEBUG-only (`DEBUG_ANIM_INVARIANTS`) per-frame assertions: incap↔override coherence, jump
ownership, PendingAttack coherence, stale `OverrideStarted`, incap-locked-but-moving.
Enable when hunting "anim got weird N frames ago" bugs.

## Writer census — every code path that sets a unit's animation

**RoutineAnim channel (AI-owned, raw writes):**
- `AI/SubroutineSteps.cs` — `MoveToward`/`SetLocomotionAnim` (the canonical locomotion
  write: tier from actual Velocity via `LocomotionProfile.PickTier`, Idle gated on
  `PreferredVel` > `MoveIntentEpsilon` so momentum-slide shows Idle not walk-in-place),
  `SetIdle`, `AlertStance` (Idle + face threat).
- `AI/DeerHerdHandler.cs` — Sit/Sleep (sleeping routine), Feeding (bush feed).
- `Game/Simulation.cs` `AIForageGraze` — Action(Feeding) for boar grazing (sweep-driven).

**OverrideAnim channel (via `AnimResolver.SetOverride`):**
- `Game1.Animation.cs` — attack anim for PendingAttack; attack pre-roll (Combat, both).
- `Game/DamageSystem.cs` — `ApplyHitReactAnim` → Combat(BlockReact) flinch, gated in ONE
  place (skips Incap/mid-jump, skips `Fleeing`/`Routing`/`FleeTimer>0`, skips
  `FlinchRefractoryTimer` window); death → Forced(Death) in `Apply` + `ApplyDirect`.
- `Game/Simulation.cs` — melee miss → Dodge one-shot at Priority 1 (in
  `ResolveMeleeAttack`; skips prone/mid-jump but **does NOT check Fleeing**);
  decapitation → Forced(Death) (`TryApplyLimbChop`).
- `Game/BuffSystem.cs` — incap holds `Hold(Incap.HoldAnim, prio 3)` (Knockdown/Stunned/
  Sleep buffs), recovery `Forced(Standup/RecoverAnim)`.
- `Game/PotionSystem.cs` — paralysis `Hold(Stunned, prio 3)`.
- `Game/PhysicsSystem.cs` — knockback launch → `Forced(Fall)` (+ `AIControl.Interrupt`).
- `Game/TrampleSystem.cs` — trample-dodge hop → timed Dodge (owns `DodgeTimer`
  scripted hop; suppresses the standard Simulation dodge via `suppressDodgeAnim`).
- `AI/DeerHerdHandler.cs` — wake-up `Combat(Standup)`.
- `AI/CorpsePuppetHandler.cs` — `Forced(Death)` on despawn-deposit.

**Direct controller writes (bypass both channels — the exceptions):**
- `Game/JumpSystem.cs` — ForceState(JumpTakeoff/Loop/Land…) — owns the controller
  during a jump (see AnimInvariants).
- `Game1.Animation.cs` corpse-interact branch + `UpdateChanneledCast`; `Game1.Spells.cs`
  cast start (necromancer channels).
- `GameRenderer.Corpses.cs` — corpse controllers (Death held at end), not live units.
- Editor previews (`Editor/UnitEditorWindow.cs`, `WadingEditorPopup.cs`).

## Player (necromancer) cast-anim pipeline — the `_pendingCastAnim` machine

The player is `Archetype == 0` (legacy branch) and casts bypass AnimResolver entirely —
they poke the controller directly. All state is Game1-side (`PendingCastAnim?
_pendingCastAnim` struct in `Game1.cs`, near `IsChanneledCast`/`GetChannelStates`):

- **Dispatch**: spellbar keypress in `Game1.cs` Update → `DispatchSpellCast`
  (`Game1.Spells.cs`). Mana + cooldown are committed at PRESS time
  (`SpellCaster.TryStartSpellCast`, `Game/SpellCasting.cs`); the cursor world pos is
  frozen into `PendingCastAnim.Target` at press.
- **Instant path** (`CastAnim` empty/`Spell1` + a `CastingBuffID`): `ctrl.RequestState(Spell1)`;
  the effect fires on `JustHitEffectFrame` (effect_time_ms, 50% fallback) in
  `UpdateAnimations`, with a **fallback execute** when the necromancer leaves Spell1
  without the effect frame having fired. No CastingBuffID → effect executes immediately
  on press, no anim.
- **Channeled path** (`CastAnim` = `ImbueGround`/`Raise`/`ImbueTable`):
  `ctrl.ForceState(start)` at dispatch, then `UpdateChanneledCast` (`Game1.Animation.cs`)
  drives Start→Loop→Finish; effect fires at END of the loop budget
  (`ChannelPlaybackSpeed` fits the cycle into `spell.CastTime`). It also RAW-snaps
  `FacingAngle` to the frozen target every frame — and since `UpdateAnimations` runs
  AFTER `Simulation.Tick` (`UpdateFacingAngles`), the snap wins over turn-rate-limited
  cursor facing.
- **Casting does NOT gate movement**: no plant reads `_pendingCastAnim`; WASD
  `PreferredVel` keeps flowing, so the player slides in the cast pose (cast anims are
  priority 3 + non-interruptible, so the walk `RequestState` just parks in
  `_pendingState`). Contrast: a player MELEE swing does plant — `PendingAttack` zeroes
  PreferredVel (`UpdateAI`) and Velocity (`UpdateMovement`); only the `InCombat` plant
  exempts PlayerControlled.
- One real cast at a time: `DispatchSpellCast` rejects while `_pendingCastAnim != null`;
  `TryAttackClick` (`Game1.WorldClicks.cs`) also refuses to stamp a melee attack mid-cast.
  Separate held-key channel state: `_channelingSlot` (beam/drain spells, cancelled on key
  release in `Game1.cs` Update).

### Beam/Drain hold-channel (`_channelingSlot`) — a SEPARATE machine that holds NO anim
`Beam`/`Drain` spells (e.g. `lightning_beam`) are NOT the `ImbueGround`/`Raise` channel
above. Their lifecycle:
- **Start**: `lightning_beam` has `castingBuffID` but no `castAnim`, so `DispatchSpellCast`
  takes the *deferred single-shot* branch — plays **Spell1 once** (the wind-up the caster
  is stuck in), fires the effect on the Spell1 action frame → `SpellEffectSystem.Execute`
  Beam case → `sim.Lightning.SpawnBeam(...)` + `StartChannel(...)`. `StartChannel` (player
  `slot>=0`) just sets `game._channelingSlot = slot`; AI (`slot<0`) sets `Unit.ChannelTimer`.
- **No held pose**: after the Spell1 effect frame, `_pendingCastAnim` clears and the caster
  falls back to Idle/locomotion — **nothing holds a casting frame for the beam duration**
  (the user's "returns to idle" symptom). To hold a frame you must drive the controller
  directly off `_channelingSlot` (mirror how `UpdateChanneledCast` pins Imbue), since the
  beam channel has no `_pendingCastAnim` entry and no anim state today.
- **Tick/end**: `LightningSystem.Update` (`Necroking/Game/LightningSystem.cs`, ticked from
  `Simulation.Tick` "Lightning" phase) only advances `Elapsed` and kills the beam on
  `MaxDuration`. Player release: `Game1.cs` Update `--- Beam/drain channel-hold ---` block
  calls `CancelBeamsForCaster`/`CancelDrainsForCaster` when `!SpellBarBindings.IsSlotHeld`.
  AI: `CasterUnitHandler.CancelChannel`. **`lightning_beam` sets no `beamMaxDuration`, so it
  ONLY ends on key-release.**
- **Beam/Drain deal NO per-tick damage today**: `ActiveBeam.DamagePerTick`/`TickRate`/
  `RetargetRadius`/`DamageAccumulator` are stored but the `LightningSystem.Update` beam &
  drain loops never apply damage or retarget (only `Strike` pushes `LightningDamage`). Any
  "robust channel" work that expects the beam to hurt must wire this up.
- SpellDef beam/drain fields (`BeamTickRate`, `BeamMaxDuration`, `BeamRetargetRadius`,
  `DrainMaxDuration`, `DrainBreakRange`, …) live in `Data/Registries/SpellRegistry.cs`
  (`[EditorVisible("Category","Beam"/"Drain")]`); add a `channelStopsMovement`-style field
  there and default it on for Beam/Drain.

## Frame timing — where per-frame durations (ms) come from

Playback is ms-driven when metadata exists. The chain, lowest to highest precedence:

1. **Authored metadata** — `time_ms` arrays in the sprite `animationmeta` files, loaded by
   `AnimMetaLoader.Load` (`Render/AnimationMeta.cs`) into a `"SpriteName.Category"` →
   `AnimationMeta` dict (`Game1._animMeta`); per-yaw durations live in
   `AnimYawMeta.FrameDurationsMs`. These `AnimationMeta` objects are **SHARED across every
   unit using that sprite** — never mutate them per-unit.
2. **Per-unit-def overrides** — `UnitDef.AnimTimings : Dictionary<string,
   UnitAnimTimingOverride>` (`Data/Registries/UnitRegistry.cs`; `FrameDurationsMs` +
   `EffectTimeMs`), authored in the unit editor (`Editor/UnitEditorWindow.cs` per-frame
   "Frame N ms" fields, `BuildRuntimeTimingOverrides`). Wired to the controller in
   `Game1.Animation.cs` `BuildUnitAnimData`: copies each into a per-controller
   `AnimTimingOverride` (`new List<int>(...)` — controller-owned copies) →
   `ctrl.SetAnimTimings`.
3. **Lookup at play time** — `AnimController` resolves via the private trio
   `GetEffectiveFrameDurations(spriteAngle)` / `GetEffectiveTotalDurationMs()` /
   `GetEffectiveEffectTimeMs()` (override wins, else meta yaw data). `Update` accumulates
   `_animTime` in **ms** and walks the cumulative durations to pick the frame; loop wrap,
   effect_time firing, foot-phase carryover, and `LocomotionScaling` playback scaling all
   read the same totals — so any timing hack must keep the per-frame list and the total
   consistent (frame list summing ≠ total breaks wrap/effect-frame math).

**Where per-unit timing jitter goes**: each unit has its OWN `AnimController`
(`Game1._unitAnims`), and `SetAnimTimings` lists are controller-owned — so `BuildUnitAnimData`
is the seam for per-unit (fixed-per-spawn) jittered copies; per-cycle re-roll needs a small
`AnimController` feature (jitter amplitude field + cached jittered list regenerated on loop
wrap, applied inside the `GetEffective*` trio so total/effect math stays consistent).
No fallback-mode gotcha: when NO ms data exists, playback falls back to tick counting
(`_animTime` counts ticks, `AnimationData.TotalTicks`) and ms overrides do nothing.

## Interruption / lock / cooldown mechanisms that exist

- Priority lanes + same-priority "must have started" replacement gate (AnimResolver).
- `OverrideHandle` + `ClearIfOwned` — safe teardown without racing a preemptor.
- Flinch: `HitReactShowSeconds` 0.35s + `FlinchRefractorySeconds` 0.6s + the
  fleeing/prone/jump suppression — ALL centralized in `DamageSystem.ApplyHitReactAnim`.
- Attack: `PendingAttack` pins legacy movement until the hit-frame resolves;
  `PostAttackTimer` bounded plant; stale-swing cancel in the archetype branch.
- Incap: `Incap.IsLocked` short-circuits AI dispatch (`Simulation.UpdateAI`) and zeroes
  velocity; `HoldAtEnd` snaps prone-death to the last frame.
- There is **no generic "animation lock stops movement"**: `IsMovementLocked` is dead
  code, and Dodge/BlockReact/pre-roll overrides play while the unit keeps moving.

## Pitfalls / gotchas

- **Movement and animation are independent channels** — an override replaces the walk
  clip but nothing stops `PreferredVel`, so the unit slides. Known slide-makers for a
  FLEEING archetype unit (deer): (1) melee-miss Dodge (`ResolveMeleeAttack`) has no
  Fleeing gate, unlike the flinch; (2) the attack pre-roll re-stamps a Combat override
  every frame while `InCombat && AttackCooldown>0` — and `EngagedTarget` (which derives
  InCombat) is stamped onto ANY damaged unit whose `AIBehavior != FleeWhenHit` in
  `DamageSystem.Apply/ApplyDirect`, so a hit fleeing deer with a wolf in melee range can
  be pinned in attack/idle-fallback frames while running; DeerHerd only clears
  EngagedTarget in `OnRoutineExit(FightBack)`; (3) a missing clip falls back to the
  **Idle** frames while `CurrentState` stays e.g. Dodge — looks like "stuck idle while
  moving".
- **`RoutineAnim` is last-writer-holds** — a routine that sets `Action(Sit/Feeding)` must
  overwrite it (MoveToward/SetIdle do) on every later frame/exit or the pose sticks.
- **Never write `OverrideAnim` raw** (internal setter enforces) and never pass raw
  priority ints — the knockdown-hold bug (e791330) and stale-OverrideStarted bug
  (9247c71) both came from lifecycle misuse.
- **Edge flags are one-frame** (`JustFinished`, `JustHitEffectFrame`) — read them the
  same frame after `ctrl.Update`, guard side-effects from double-fire.
- Missing `effect_time_ms` silently falls back to 50% of duration — an attack anim
  authored without it still resolves, just at the wrong moment.
- `CorpseInteractPhase` doubles as an anim-event signal (see AnimController doc-block);
  early exits leak the phase — reset explicitly.
- Legacy vs archetype duality: any hit-react / dodge / block change must be checked in
  BOTH the legacy `targetState` chain and the override-stamp sites, or the two render
  paths drift (the `HitReactTimer` comment in `Game1.Animation.cs` records one such fix).

## Related areas
- [ai.md](ai.md) — who drives `RoutineAnim` (handlers + SubroutineSteps), routine
  transition choke points, the stuck/locked-unit causes list.
- [combat.md](combat.md) — PendingAttack stamp/resolve pipeline that the attack anims
  visualize; hit-frame trigger is `JustHitEffectFrame`.
- [movement.md](movement.md) — `PreferredVel`/Velocity, the accel model the gait tiers
  and playback scaling read.
- game1-partials.md — `Game1.Animation.cs` summary entry.
