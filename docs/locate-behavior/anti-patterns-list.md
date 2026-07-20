*Contains list of found anti patterns in the codebase.*
# Gameplay in Game1.

At least these two are done in animation code:
1. ExecuteSpellEffect(spell, i, pca.Target, pca.Slot, _pendingSpell);
2. _sim.TryResolvePendingAttackAtImpact(i);

# Table/necro-bench crafting gated on animation completion

`Game1.Animation.cs` `UpdateAnimations` — the `CorpseInteractPhase` state machine — runs
gameplay-critical logic inside the animation tick, gated on `animData.Ctrl.IsAnimFinished`.

- **case 5 (PutDown) — FIXED** (the egregious one). Was: on `IsAnimFinished`, transfer the
  carried corpse into the table slot, remove it from the sim, reset carry state, AND (commit
  `4f1e851`) fire `StartTableCraft` — so a corpse only crafted once the PutDown *animation*
  finished. Now: `Game1.BeginCorpsePutDown` sets the visual phase and **schedules** the
  transfer+craft on the sim clock via `_sim.Tasks` / `CorpsePutDownTask` (fired in `Simulation.Tick`);
  `Game1.CompleteCorpsePutDown` does the gameplay; the animation is fitted to the same
  duration and merely reflects it. Both the table-load and the ground-drop go through this.
  The imbue-table craft loop is fitted to `ProcessTime` via `AnimTiming.FitChannel`. This is
  the worked example for the **[Canonical resolution](anti-patterns.md)** — copy it.
- **case 4 (Pickup)** and **cases 1/2/3 (WorkStart/Loop/End bagging) — STILL COUPLED.** They
  still advance `CorpseInteractPhase` and consume corpses off `IsAnimFinished` / the anim-tick
  `BaggingTimer >= BaggingDuration` (const `2.0f`). Bagging's payload (`bc.Bagged = true`) still
  fires in case 3 on `IsAnimFinished`; note case 2 is shared with handler-driven trap building,
  so converting it touches AI handlers. Fix the same way when you're next in here: schedule the
  phase payloads via `_sim.Tasks` (`ScheduledTask` subclasses) and fit the anim via `AnimTiming`
  (see the canonical resolution). Don't fix one and leave the matched pair — that's a sync-bug
  waiting to happen.

# Hand-ticked countdown fields on persistent objects (port to ScheduledTasks)

The delayed-execution framework (`Necroking/Game/ScheduledTasks.cs`, see the principle in
[anti-patterns.md](anti-patterns.md)) landed 2026-07-19; wave 1 ported `CorpsePutDownTask`,
`ReanimRiseTask`, `ProjectileVolleyTask`. Known remaining hand-ticked timers to port when
next touched (full plan: `todos/scheduled-tasks-framework.md`):

- Sim-clock repeating accumulators ("every N seconds do a scan"): `Game1.Zones.cs`
  `_zoneSpawnTickTimer`; `Game/Jobs/WorkerSystem.cs` `_dispatchTimer`; `Game/HordeSystem.cs`
  `_aggroScanTimer`; `Game/Simulation.cs` `_moraleCheckTimer` / `_fatigueRegenTimer` /
  `_harassmentDecayTimer` / `_wolfHuntCmdTimer`; `Game1.Net.cs` `_netSendTimer`;
  `Game/ForagableSystem.cs` `_autoPickupCooldown`; `Game/TriggerSystem.cs` cooldown/period timers.
- Raw-dt UI countdowns in `Game1.cs` (`_hoverVariantLabelTimer`, `_depthFogToastTimer`,
  `_gpuWarnToastTimer`, `_devChannelHoldTimer`) — need a Game1-owned real-time `ScheduledTasks`
  instance ticked on `_rawDt`; these are render-queried countdowns, so port by holding the task
  reference and reading `SecondsLeft`.
- Editor `_statusTimer` copy-pasted across ≥4 editor windows (`ItemEditorWindow`,
  `MapEditorWindow`, `UnitEditorWindow`, `WallEditorWindow`) even though the `EditorWindow` base
  already has `StatusTimer` — single-source-of-truth violation; consolidate onto the base.

NOT anti-patterns (stay put per the exception): buff `RemainingDuration`, per-unit bulk timer
arrays in `Simulation`, potion per-unit poison/paralysis timers, projectile/cloud TTLs,
env respawn/trap timers, render-side TTLs, spell cooldown dictionaries (queryable state).

# Swing-expiry window: melee and ranged stamp it from DIFFERENT sources (found 2026-07-19)

The invariant "an attack's impact frame is guaranteed inside the `PostAttackTimer` window"
(relied on by `AI/CombatTransitions.cs` + `AI/HordeMinionHandler.cs`, enforced by the
SwingJanitor in `Game1.Animation.cs`) has TWO implementations that drifted:

- **Melee** (Simulation attack-selection loop): `PostAttackTimer = min(cycle,
  GetAttackAnimDurationSec(i, w))` — window derived from the actual attack-anim length,
  invariant holds by construction.
- **Ranged** (`AI/RangedUnitHandler.cs` `TryQueueShot`): `PostAttackTimer =
  PostShotFollowThrough` (flat `0.6f`, shortened for kiting) — NOT tied to the Ranged1
  anim's time-to-effect-frame. Any ranged anim whose effect frame lands after 600ms
  (e.g. `NavarreLightInfantry_Archer` Ranged1: 1330ms clip, `effect_time_ms:0` → 50%
  fallback = 665ms) has EVERY shot janitor-cleared before the arrow spawns.

Same-behavior-two-implementations violation. Fix direction: single-source the window
(a ranged-aware `GetAttackAnimDurationSec` twin, or fit the anim into the window via
`AnimTiming`/compression like the archetype attack-override already does for cycles).

**Update (commit `3766a9d`):** `RangedUnitHandler.ShotWindowSec` now derives the window from
the anim's effect frame (`ctx.AnimMeta` lookup, covers the 50%-fallback case) — but it is
DEFEATED in practice by the AnimMeta-loss bug below (`ctx.AnimMeta == null` → silent
`PostShotFollowThrough` fallback), so archer shots still expire unresolved.

# Set-once sim back-reference lost on GameSession recreate: `Simulation.SetAnimMeta` (found 2026-07-19)

`_sim.SetAnimMeta(_animMeta)` is called EXACTLY ONCE, from the startup load step in
`Necroking/Game1.Loading.cs` (~line 238) — on the ctor-era `Simulation`. But every
`StartGame`/`StartScenario` runs `ResetWorldState()` → `_session = new GameSession()` →
a fresh `Simulation` whose `_animMeta` is null, and `WireSimCallbacks()` (the designated
"re-install Game1→Sim back-references after session recreate" hook) re-wires
`ReanimHandler`/`OnForagerAte`/`Workers` but NOT AnimMeta. Consequence: in every real play
session and every scenario, `AIContext.AnimMeta == null`, so `RangedUnitHandler.ShotWindowSec`
falls back to the flat 0.6s window (archer arrows janitor-killed — the live "queued shots
never resolve into FireArrowAt" bug) and any other AI effect-time lookup silently degrades.
This is the "silent-null optional wiring" anti-pattern (cf. the `?.`-invoked DI delegates in
anti-patterns.md): a `?`-nullable set-once field whose loss produces no error.
**Fix: add `_sim.SetAnimMeta(_animMeta)` to `WireSimCallbacks()` in `Necroking/Game1.cs`**
(guard for LoadContent ordering: `_animMeta` may be empty before load — call is harmless).
Diagnostic: `[SetAnimMeta] N entries` in `log/jump.log` appears once at startup only.

# GetCurrentFrame / GetCurrentFrameIndex are hand-kept twins that drifted (found 2026-07-19)

`Necroking/Render/AnimController.cs`: `GetCurrentFrame` and `GetCurrentFrameIndex` each
reimplement the reverse-playback mirror + cumulative-ms frame walk + tick fallback. They
drifted: `GetCurrentFrame` clamps to the (unique-frame) keyframe list while
`GetCurrentFrameIndex` returns the raw logical index — the drawn sprite freezes while
markers/timing keep advancing (the 8-logical-vs-5-unique animationmeta `sprites` bug).
When fixing, extract ONE shared "resolve logical frame index" helper both call. Related
pre-existing skews in the same cluster: `UnitEditorWindow.StepAnim`'s tick fallback compares
an ms-mode `AnimTime` against tick `Keyframe.Time`s; `AnimMetaLoader` reads
`loop_start`/`loop_end` but the exporter writes `loop_start_index`/`loop_end_index`
(fields never populated, zero consumers).

# Wrong-list weapon lookup for ranged pending attacks (latent, found 2026-07-19)

`Game1.Animation.cs` `ComputeWeaponCycleSeconds(unitIdx, weaponIdx)` and
`Simulation.GetAttackAnimDurationSec(unitIdx, weaponIdx)` both index
`Stats.MeleeWeapons[weaponIdx]` unconditionally — but when `PendingWeaponIsRanged` is
true, `PendingWeaponIdx` indexes `Stats.RangedWeapons`. Callers pass the ranged idx in
(archetype attack-override block, line ~606). Benign today only because the OOB/mismatch
falls back to defaults (1 round / 1.0s); becomes a real bug for any unit with both weapon
lists. Both helpers need an `isRanged` parameter.
