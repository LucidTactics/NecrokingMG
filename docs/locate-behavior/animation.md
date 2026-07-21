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
  Pickup/Carry/PutDown by direct `ForceState`, bypassing both channels. **Case 5 (PutDown)
  is FIXED** — visual-only; the corpse transfer + `StartTableCraft` fire from a scheduled
  sim task (`CorpsePutDownTask`, see anti-patterns.md Canonical resolution). **Cases 1-4
  are STILL gameplay-coupled**: they advance the phase and consume corpses off
  `Ctrl.IsAnimFinished` / the anim-tick `BaggingTimer` (const `BaggingDuration = 2.0f`
  lives inline in this branch); case 3 fires `bc.Bagged = true` on the anim edge. See
  anti-patterns-list.md.
- **Archetype branch** (`Archetype > 0`): stamps the Combat override for `PendingAttack`
  (`ResolvePendingAttackAnim`, compressed to the weapon cycle). **The speculative attack
  pre-roll is REMOVED** — attack anims start ONLY when a swing is actually queued
  ("animation ⇔ committed attack"; the old pre-roll produced phantom windups vs fleeing
  targets). Cancels a stale attack swing once the unit moves and `!InCombat &&
  PendingAttack.IsNone && PostAttackTimer<=0`, then `AnimResolver.Resolve`, then locomotion
  playback scaling (only for Walk/Jog/Run/Carry), `HoldAtEffectFrame` pin while
  `ChannelTimer > 0` (AI beam channel), then `ctrl.Update`, `LungeSystem.Update`,
  `AnimInvariants.Check`.
- **Legacy branch** (`Archetype == 0`): priority chain in code order — InPhysics→Fall,
  Incap hold/recover, `Dodging`→Dodge, PendingAttack→attack anim, `HitReactTimer`→
  BlockReact, `PostAttackTimer`→Block, combat stance Block while `InCombat &&
  AttackCooldown > 0` (no pre-roll here either), GhostMode→Hover, else gait from the
  centrally-picked loco tier with Carry override (Carry yields to a mid-cast necromancer).
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
- `Game1.Animation.cs` — attack anim for PendingAttack (Combat). (The speculative attack
  pre-roll override is removed — no override without a committed swing.)
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
  NOTE these are get-or-created AND **ticked from the DRAW pass** (`DrawCorpses` does
  `cad.Ctrl.Update(_clock.WorldDt)` once per Draw — lags under fixed-timestep catch-up),
  and `Game1._corpseAnims` entries are never pruned when a corpse leaves the sim (only
  session-Clear). Logged in anti-patterns-list.md.
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

## Sprite-atlas keyframes — spritemeta parsing & the unique-vs-logical frame split

The DRAWN frames and the TIMED frames come from two different files that do NOT agree on
frame count; know this before touching frame lookup.

- **`assets/Sprites/<Atlas>.spritemeta`** (TSV) → parsed by `SpriteAtlas.ParseMeta`
  (`Necroking/Render/SpriteAtlas.cs`). Key format `Unit.Anim.tick.?.yaw` (parts: [0] unit,
  [1] anim, [2] tick → `Keyframe.Time`, [4] angle; [3] unused). Builds
  `UnitSpriteData.Animations[anim].AngleFrames[angle] : List<Keyframe>` — **one entry per
  UNIQUE atlas frame**, sorted by Time. `AnimationData.GetAngle(angle)` is the accessor
  every renderer/editor uses. Post-parse passes mutate the same lists in place:
  `FixupYOrigin` (bottom-left → top-left Y flip, per-texture pending markers),
  `RescaleAllFrames`, `ComputeFrameBoundingBoxes` (pixel-scan BodyTopV/BottomV).
  Extension sheets (`__N`) merge in via `ParseExtensionMeta`/`LoadExtension`.
- **`assets/Sprites/<Atlas>.animationmeta`** (JSONL) → `AnimMetaLoader.Load`
  (`Necroking/Render/AnimationMeta.cs`) into `Game1._animMeta` keyed `"Sprite.Category"`.
  Per-(unit,category,yaw) lines carry **LOGICAL-frame arrays**: `time_ms` (one per logical
  frame), `markers` (`mount_pos`, one per logical frame per mount id), and `sprites`
  (logical→unique sprite-key mapping, **repeats allowed** — e.g. Wretched.Spell1 yaw 0: 8
  logical frames onto 5 unique sprites). `sprites` is loaded into `AnimYawMeta.SpriteKeys`
  and honored via the expansion pass below (commit `b4d9872`). Still-dead fields: the
  loader reads `loop_start`/`loop_end` while the exporter writes `loop_start_index`/
  `loop_end_index` (so `LoopStartIndex`/`LoopEndIndex` are never populated — also have zero
  consumers), and `frame_ticks` loads into `AnimYawMeta.FrameTicks` with zero consumers.
- **Logical-frame keyframe expansion** — `AnimMetaLoader.ExpandAtlasKeyframes(atlas,
  animMeta)` rebuilds each anim's `AngleFrames` list to LOGICAL order using `SpriteKeys`
  (repeats duplicate the unique `Keyframe`), so post-expansion `kfs.Count ==
  FrameDurationsMs.Count == marker count` — drawn frames and weapon markers share one
  timeline by construction. Expanded `Keyframe.Time` = **cumulative start-ms** (NOT the
  source tick — ticks repeat non-monotonically under the mapping); non-expanded rows keep
  tick Times, so `.Time` semantics are mixed across anims (harmless: ms-mode playback
  never reads `.Time`). Idempotent (skips lists already at logical length); unresolvable
  sprite keys skip the whole row with an `asset`-log line rather than half-expanding.
  Call sites: the "Loading animation metadata" step in `Game1.Loading.cs` (after ALL
  spritemeta parsing incl. extensions + animationmeta load, BEFORE `SetTextureAndFinalize`
  so copies get the same Y-flip/bbox treatment) and `UnitEditorWindow.RefreshAtlases`
  (newly-scanned atlases only). Atlases + `_animMeta` are process-lifetime assets loaded
  once in LoadContent — NOT part of `GameSession` — so the expansion correctly does NOT
  re-run on StartGame/StartScenario/map reload. Migration gotcha: per-unit
  `UnitDef.AnimTimings.FrameDurationsMs` overrides authored against the OLD unique-frame
  count now mismatch the logical keyframe count and fall into the defensive clamp path.
- **Load order** (`Game1.Loading.cs` LoadContent steps): decode step parses ALL spritemetas
  (base `ParseMetaOnly` + extension `ParseExtensionMeta`) → "Loading animation metadata"
  step runs the `AnimMetaLoader.Load` loop + `ValidateEffectTimes` → per-atlas GPU-upload
  steps call `SetTextureAndFinalize` (Y-flip/bbox) + `StrideCalibration.CalibrateAtlas` →
  "Wiring unit sprites" sets `UnitDef.SpriteData`. The editor has a second, minimal atlas
  path: `UnitEditorWindow.RefreshAtlases` (full `SpriteAtlas.Load`, loads NO animationmeta).
- **Frame lookup at play time**: `AnimController.GetCurrentFrame` and
  `GetCurrentFrameIndex` now share ONE cumulative-ms walk —
  `LogicalFrameFromDurations(durations, effectiveTime)` (the fix for their historical
  hand-kept-twin drift; the reverse-playback time-mirror block is still duplicated
  between them). With expansion in place, counts match and drawn frame == marker frame.
  The defensive clamp `Math.Min(frameIdx, kfs.Count-1)` in `GetCurrentFrame` REMAINS and
  must stay: count mismatch can still come from anim/meta pairing skew
  (`ResolveAnimForState` may fall back to the Idle CLIP while `ResolveMetaForState`
  resolved the requested state's meta), rows without a `sprites` mapping, failed
  expansion, or stale per-unit `AnimTimings` override lengths.
- **`Keyframe.Time` (ticks) is only read on the no-ms fallback paths**: `AnimationData.
  TotalTicks`, the tick branches of `AnimController.Update`/`GetCurrentFrame`/
  `GetCurrentFrameIndex`, and `UnitEditorWindow.StepAnim`'s fallback (which compares an
  ms-mode `AnimTime` against tick Times — pre-existing skew for meta-driven anims without
  per-unit overrides). When meta `time_ms` exists, no runtime path reads `.Time`.
- **kfs consumers census** (things that read `GetAngle` lists directly): last-frame corpse
  poses `GameRenderer.Corpses.cs` (`kfs[kfs.Count-1]`), first-frame thumbnails
  (`MapEditorWindow` units tab, `GetFrameForStateStart`, editor previews),
  `StrideCalibration.MeasureGait`/`MeasureIdleFootSpread` (min/max envelope over frames —
  count-insensitive), `UnitEditorWindow.GetFrameCountForCurrentAnim` (sizes the per-frame
  "Frame N ms" override editor + "Set All Frames"), `ShadowRenderer` (via GetCurrentFrame).

## Standup — the census of players & duration-waiters (two-clip export survey, 2026-07-20)

`AnimState.Standup` maps to clip name `"Standup"` (`StateToAnimName`), fallback `Standup→Idle`
(`GetFallbackAnimName`), play mode PlayOnceTransition (on finish `AnimController.Update` does
`SwitchState(_pendingState != _currentState ? _pendingState : Idle)` — `_pendingState` is a
*preemption queue* filled by `RequestState` while locked, NOT a chaining mechanism; there is
**no data- or code-driven "then play X" follow-up anim feature anywhere**).

Who plays Standup:
- **Reanimation rise** — `BuffSystem.BeginReanimationRise` (called from `ReanimRiseTask` in
  `Game1.Spells.cs` and `Simulation`): stamps `Forced(Standup, playbackSpeed)` + an Incap
  recovery lock whose `RecoverTimer = -1` sentinel is filled by `AnimResolver.Resolve` (and the
  legacy twin in `Game1.Animation.cs`) from `ctrl.GetTotalDurationSeconds(RecoverAnim) / speed`
  — i.e. the movement/AI lock length IS the clip length ÷ speed, read from meta at runtime.
- **Incap/knockdown recovery** — `BuffSystem.TickBuffs` (`RecoverAnim`, buff field
  `incapRecoverAnim` in `Data/Registries/BuffRegistry.cs`), fatigue collapse in `Simulation`.
- **Sleep-wake** — `AI/DeerHerdHandler.cs` `SleepWaking` (re-stamps `Combat(Standup)` every
  frame; the wait now derives from the real clip via `SubroutineSteps.StandupSeconds(ref ctx)`
  — meta `TotalDurationMs`, 1s fallback for sprites without standup timing. The old
  hardcoded per-handler `StandupDuration = 1.0f` consts and the dead `Unit.StandupTimer`
  field are REMOVED).
- **Jump abuse** — `NightfallPorts/RogueJump.cs` uses Standup **seeked to its MIDPOINT** as the
  takeoff spring (`SeekToMidpoint` = `CurrentAnimDurationMs * 0.5`) and Standup-from-frame-0 as
  the landing, and detects takeoff via the PlayOnceTransition exit edge. Any change to Standup's
  length/shape changes jump feel for units without dedicated jump clips.
- **Corpse rendering** — `GameRenderer.Corpses.cs`/`Render/ReanimMorph.cs`/`ShadowRenderer`
  morph the death pose to `GetFrameForStateStart(AnimState.Standup, yaw)` = **frame 0 only**.
- `Game1.Dev.cs` weapon_attach diag reports `HasAnim(Standup)`.

**Two-clip exports ("Standup" + "Standup2")**: the sprite exporter may split one motion into
two clips. A `Standup2` row would parse fine into `Animations["Standup2"]` + meta key
`"Unit.Standup2"` but be **unreachable at runtime** (no AnimState maps to it); it would only
show up in `UnitEditorWindow.GetAnimNamesForSprite` dropdowns. The agreed direction is a
**load-time stitch pass** next to `AnimMetaLoader.ExpandAtlasKeyframes` (run AFTER expansion,
in the same `Game1.Loading.cs` "Loading animation metadata" step): append Standup2's expanded
keyframes/durations/markers onto Standup per yaw (offset appended `Keyframe.Time` by the base
total ms), then REMOVE the Standup2 anim + meta entries so no consumer ever sees the split.
Every duration-waiter above then follows automatically because they all read
`GetTotalDurationSeconds`/meta totals — EXCEPT the hardcoded deer/wolf `StandupDuration` consts
and the RogueJump midpoint (both change meaning under a longer clip). Per-unit
`UnitDef.AnimTimings["Standup"]` overrides authored pre-stitch will mismatch the combined frame
count and fall into the defensive clamp. A runtime chain (play Standup2 on Standup finish) was
surveyed and rejected: it needs a new AnimState + AnimResolver awareness (the OneShot
auto-expire clears the override when CurrentState moves off it, letting the routine channel
yank the unit), and every waiter would end its lock one clip early.

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
  Fleeing gate, unlike the flinch; (2) a missing clip falls back to the **Idle** frames
  while `CurrentState` stays e.g. Dodge — looks like "stuck idle while moving". (The old
  slide-maker #2 — the attack pre-roll re-stamping a Combat override every frame while
  `InCombat && AttackCooldown>0` — is gone: the pre-roll was removed from both branches.)
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
