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
the anim's effect frame (`ctx.AnimMeta` lookup, covers the 50%-fallback case). It was
initially DEFEATED by the AnimMeta-on-recreate bug below; that is now fixed
(`WireSimCallbacks` re-installs AnimMeta), so the derived window is effective in real
sessions. The silent `PostShotFollowThrough` fallback path remains for units without meta.

# FIXED — Set-once sim back-reference lost on GameSession recreate: `Simulation.SetAnimMeta` (found 2026-07-19, fixed)

`WireSimCallbacks()` in `Necroking/Game1.cs` now re-installs `_sim.SetAnimMeta(_animMeta)`
on every session recreate (with an explanatory comment), so fresh `Simulation`s no longer
run with null AnimMeta and `RangedUnitHandler.ShotWindowSec` derives the real
effect-frame window instead of the flat 0.6s fallback. Kept here as the worked example of
the "silent-null optional wiring" anti-pattern: a `?`-nullable set-once field lost on
recreate fails silently — `WireSimCallbacks` is the designated re-wire hook and must get
EVERY such back-reference.

# MOSTLY FIXED — GetCurrentFrame / GetCurrentFrameIndex hand-kept twins (found 2026-07-19, fixed in `b4d9872`)

`Necroking/Render/AnimController.cs`: both now call the shared
`LogicalFrameFromDurations` helper for the cumulative-ms frame walk, and
`AnimMetaLoader.ExpandAtlasKeyframes` rebuilds atlas keyframe lists to logical order so
the 8-logical-vs-5-unique `sprites` freeze is gone (see animation.md "Sprite-atlas
keyframes"). STILL DUPLICATED between the two: the reverse-playback time-mirror block and
the tick-fallback floor walk — unify when next touched. Related pre-existing skews still
live: `UnitEditorWindow.StepAnim`'s tick fallback compares an ms-mode `AnimTime` against
tick `Keyframe.Time`s (note: expanded rows now store cumulative start-ms in `.Time`, so
semantics are MIXED across anims); `AnimMetaLoader` reads `loop_start`/`loop_end` but the
exporter writes `loop_start_index`/`loop_end_index` (fields never populated, zero
consumers).

# weapon_attach dev command duplicates ComputeWeaponAttach's resolution chain (found 2026-07-20)

`Necroking/Game1.Dev.cs` `case "weapon_attach"` re-implements, inline, the exact
resolution steps of `GameRenderer.Units.cs` `ComputeWeaponAttach` (StateToAnimName →
ResolveAngle → GetCurrentFrameIndex → MetaKey lookup → `WeaponPointResolver.TryResolve`)
because ComputeWeaponAttach is private to GameRenderer and returns only the final world
points. Two-implementations-in-separate-files violation with the worst failure mode for a
diagnostic: if the renderer's resolution changes, the dev command reports a stale truth.
Fix direction: extract a shared static "resolve weapon frame" helper (natural home:
`Render/WeaponPointResolver.cs`) that returns the intermediates, and have both call it.

# attachedFlame mode: dead data + editor shows dead fields (found 2026-07-20)

`WeaponParticleVisual.AttachedFlame` (Data/Registries/BuffRegistry.cs) ignores
`SpawnRate`/`ParticleLifetime`/`MoveSpeed`/`MoveDir*`/`RangeMin`/`RenderBehind`/`Color.A`
and repurposes `RangeMax` as the flame's 0..1 hilt→tip position, but:
- `Editor/SpellEditorWindow.cs` (weapon-particle block, `bf_wp_*` fields) draws every
  spawn-mode row regardless of the Attached Flame checkbox, and the `RangeMax` label
  still reads "Range Max". Fix: gate the dead rows on `!wp.AttachedFlame`, relabel
  RangeMax in attached mode.
- `data/buffs.json` `buff_4`/`buff_4_copy`/`buff_4_copy_copy` still carry the dead
  spawn-mode values (spawnRate 20, lifetime 0.8, moveDirZ 1, …) — confusing to tuners.
  Only prune once the editor gating exists (unchecking attachedFlame would otherwise
  resurrect class defaults silently).
The three buff_4* defs are color-only clones (purple/green/yellow) referenced by ~16
spells' `castingBuffID` + `Game1.cs` `TableChannelBuffId` + `CastPointDebugScenario` —
deliberate authored variants, NOT a consolidation target unless a per-spell color
override field is added.

# BuffPreview pulsing-outline preview diverged from the in-game outline (found 2026-07-20)

`Editor/BuffPreview.cs` `DrawPulsingOutline` still draws its stick-figure silhouette 8×
at directional offsets (`OutlineDirs`) and duplicates `DrawSpriteOutline`'s pulse math
(t formula, width lerp, 0.5 floor, HDR color lerp) — while the game
(`GameRenderer.Units.cs` `DrawSpriteOutline`, commit `5d11baa`) is a single-pass
OutlineFlat.fx dilation union. At wide radii the preview shows the faceted multi-shadow
look the game no longer has. Preview-only fidelity issue (the preview has no sprite
texture to dilate); at minimum single-source the pulse math if either side is touched.

# RemoveCastingBuffAll strips ANY weapon-particle buff, not just casting buffs (latent, found 2026-07-20)

`Necroking/Game1.Spells.cs` `RemoveCastingBuffAll` — doc-comment says "buff_4 variants"
but the predicate is `def.HasWeaponParticle`. Benign today (casting effects are the only
weapon-particle buffs), but the first non-casting weapon-particle buff (e.g. a flaming
weapon enchant) will be silently removed at every cast end. Fix direction: tag casting
buffs explicitly (id prefix or a `IsCastingEffect` flag) and match on that.

# Wrong-list weapon lookup for ranged pending attacks (latent, found 2026-07-19)

`Game1.Animation.cs` `ComputeWeaponCycleSeconds(unitIdx, weaponIdx)` and
`Simulation.GetAttackAnimDurationSec(unitIdx, weaponIdx)` both index
`Stats.MeleeWeapons[weaponIdx]` unconditionally — but when `PendingWeaponIsRanged` is
true, `PendingWeaponIdx` indexes `Stats.RangedWeapons`. Callers pass the ranged idx in
(archetype attack-override block, line ~606). Benign today only because the OOB/mismatch
falls back to defaults (1 round / 1.0s); becomes a real bug for any unit with both weapon
lists. Both helpers need an `isRanged` parameter.
