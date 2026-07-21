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
- **case 4 (Pickup) and cases 1/2/3 (WorkStart/Loop/End) — FIXED 2026-07-21.** The anim
  branch is now a pure mirror of `CorpseInteractPhase`; no `IsAnimFinished` gates remain.
  The phase clocks moved to gameplay, matched-pair-consistent across both consumers:
  - `AI/WorkRoutine.cs` now owns the 1→2 and 3→0 transitions itself (BuildTimer on ctx.Dt
    vs the clip's natural length via `AnimMetaLoader.ClipSeconds` — meta-driven, runs
    headless, picks the ImbueTable variants when `CraftTableIdx >= 0`).
  - Player bagging runs in `Simulation.TickCorpseBagging` (sim clock; `CorpseBagSeconds`
    const; feeds `BaggingProgress`; fires `bc.Bagged` at the end of the WorkEnd window).
  - Pickup completes via `CorpsePickupTask` / `Game1.BeginCorpsePickup` /
    `CompleteCorpsePickup` (Game1.Crafting.cs) — the PutDown pattern's mirror, entered
    from `CorpseInteractionManager` and the corpse-pile withdraw in `Game1.cs`.

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

# FIXED — Standup duration hardcoded + dead field (found 2026-07-20, fixed)

The dead `Unit.StandupTimer` field and the duplicated per-handler `StandupDuration = 1.0f`
consts are REMOVED. Both `AI/DeerHerdHandler.cs` and `AI/WolfPackHandler.cs` now derive the
wake wait from the real clip via `SubroutineSteps.StandupSeconds(ref ctx)`
(`AI/SubroutineSteps.cs` — meta `TotalDurationMs`, 1s fallback for sprites without Standup
timing). Kept as the worked example of "derive AI wait from the clip, don't hardcode a
duplicate const" (anti-patterns.md canonical resolution).

# FIXED — Corpse AnimControllers ticked from the DRAW pass + never pruned (found 2026-07-20, fixed 2026-07-21)

`Game1.Animation.cs` `TickCorpseAnims` (called at the top of `UpdateAnimations`, WorldDt)
now advances corpse controllers on the update pass, owns the Fall→Death landing snap, and
prunes `_corpseAnims` entries whose corpse left the sim. `GameRenderer.Corpses.cs`
`DrawCorpses` only lazily CREATES controllers for visible corpses and reads them. Kept as
the worked example of "draw passes must not advance state".

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

# FIXED — RemoveCastingBuffAll strips ANY weapon-particle buff, not just casting buffs (found 2026-07-20, fixed)

`Necroking/Game1.Spells.cs` `RemoveCastingBuffAll` now matches via `IsCastingBuff` — a
buff is a casting effect iff some spell references it as `CastingBuffID` (or it's the
table-channel glow); `HasWeaponParticle` is explicitly no longer the test (verified
2026-07-21 while auditing effect lifetimes).

# Immortal zero-tick corpse drains + unlimited-by-default channels (latent, found 2026-07-21)

`Necroking/Game/LightningSystem.cs` `Update`, drain loop: for a **corpse-targeted** drain
(`TargetCorpseIdx >= 0`) the ONLY kill paths besides caster invalidation are (a)
`MaxDuration` — whose spell default `drainMaxDuration`/`beamMaxDuration` is **0 =
unlimited** — and (b) pool exhaustion / missing corpse, which are checked **only inside
the damage-tick loop**, gated `d.DamagePerTick > 0 || zeroTicks` where `zeroTicks`
deliberately excludes corpse drains. So a corpse drain with `DamagePerTick <= 0` (the
documented "visual-only sentinel" `-1`, used by the `spawn_lightning drain` dev verb)
**never ticks and never dies** unless the caster dies/cancels — its stateless per-frame
visuals (`LightningRenderer` impact flares + cloud puffs at the corpse) persist forever.
Same shape for beams: `MaxDuration 0` beams (incl. the `beam <spellID> <selector>` dev
verb, which nothing releases) live indefinitely via the retarget hop, and
`GameRenderer.World.cs` `DrawBeamHitEffects` draws their hit effect statelessly per frame
for as long as `beam.Alive`. Principle: **anything drawn statelessly off a live record
needs a guaranteed-finite record lifetime**; audit every `Spawn{Beam,Drain,Zap}` caller's
duration when adding new channel visuals.

# FIXED — Wrong-list weapon lookup for ranged pending attacks (found 2026-07-19, fixed 2026-07-21)

`Game1.Animation.cs` `ComputeWeaponCycleSeconds(unitIdx, weaponIdx, isRanged)` and
`Simulation.GetAttackAnimDurationSec(unitIdx, weaponIdx, isRanged)` now take the flag; the
archetype attack-override block passes `PendingWeaponIsRanged`. Ranged cycles come from
`Stats.RangedCooldownTime[weaponIdx]` (seconds — the same clock RangedUnitHandler locks
between shots), ranged anim names from `Stats.RangedWeapons[weaponIdx].AnimName`.
