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
- Editor `_statusTimer` copy-pasted across SIX editor windows (`ItemEditorWindow`,
  `MapEditorWindow`, `UnitEditorWindow`, `WallEditorWindow`, `UIEditorWindow`,
  `EnvObjectEditorWindow` — recounted 2026-07-22) even though the `EditorWindow` base
  already has `StatusTimer`/`SetStatus` + a base fade-draw (`Editor/EditorWindow.cs`).
  Single-source-of-truth violation; consolidate onto the base. Caveat: not all six inherit
  `EditorWindow` (`EnvObjectEditorWindow`/`WallEditorWindow` are `IModalLayer` classes,
  `UIEditorWindow` extends `EditorBase`), so consolidation may mean moving
  StatusTimer/SetStatus down to `EditorBase` or a small shared struct.

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
- **FIXED (verified 2026-07-22):** `Editor/SpellEditorWindow.cs` now gates the spawn-mode
  rows on `!wp.AttachedFlame` and relabels RangeMax "Flame Pos" in attached mode
  (~1437-1463).
- STILL OPEN: `data/buffs.json` `buff_4`/`buff_4_copy`/`buff_4_copy_copy` still carry the
  dead spawn-mode values (spawnRate 20, lifetime 0.8, moveDirZ 1, …) — confusing to
  tuners. Pruning is now unblocked (the editor gating exists, so unchecking attachedFlame
  no longer silently resurrects class defaults on hidden rows).
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

# MOSTLY FIXED — Immortal zero-tick corpse drains + unlimited-by-default channels (found 2026-07-21, corpse drains fixed in `427034b`)

**Update (commit `427034b`):** corpse-drain validity is now a per-frame rule in
`LightningSystem.Update` (a drain that never ticks — `DamagePerTick <= 0` or corpse
dissolved — can't live forever), and `spell.BeamMaxDuration` is wired into `SpawnBeam`
for real casts. **Residual:** a `MaxDuration == 0` beam from the `beam <spellID> <selector>`
dev verb still lives until its caster dies (nothing releases it), with
`DrawBeamHitEffects` drawing statelessly per frame. The principle below stands.

<details><summary>Original entry</summary>

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
</details>

# FIXED — Spawn-then-re-archetype blocks duplicated per path, bypassing OnSpawn (found 2026-07-22, fixed in `427034b`)

Was: three call sites hand-rolled "spawn a unit, then overwrite its Archetype + stamp the
fields the new handler's OnSpawn would have set". Now: `AI.AIControl.ReassignArchetype(units,
idx, archetypeId, reason)` is the ONE helper (clears routine state, swaps archetype, fires the
new handler's OnSpawn), and all three sites call it — `Game1.cs` map-placed patrol units
(~1718), `Game1.Villages.cs` `SpawnPatrols` (~225), `Game1.cs` `MakeUnitWild` (~2396).
Verified 2026-07-22 during the init audit. Use ReassignArchetype for ANY future
re-archetyping; never hand-stamp OnSpawn fields.

# FIXED — Session wiring duplicated across the two world-entry paths (found 2026-07-22, fixed in `f421e1c`)

**Fixed in `f421e1c`:** the `_sim.Set*` back-reference block + env collision-dirty callbacks
folded into `WireSessionSystems()`, called by both StartGame and StartScenario after
`_sim.Init`; the `onVertexMapChanged` lambda extracted to `RefreshGroundVertexMapDirtyRect()`.
Add any NEW sim back-reference to `WireSessionSystems`/`WireSimCallbacks`, never inline.

<details><summary>Original entry</summary>

`ResetWorldState()` (commit `c54b712`) unified the world-entry CLEAR, but the post-reset
session WIRING is still hand-duplicated between `Game1.StartGame` (~1662-1675) and
`Game1.StartScenario` (~2168-2179): the `_sim.SetEnvironmentSystem` / `SetWallSystem` /
`SetTriggerSystem` / `SetVillageSystem` / `SetSkillBook` block plus the two
`_envSystem.OnCollisionsDirty` / `OnCollisionRegionDirty` lambdas. `WireSimCallbacks()` — whose
own doc-comment declares it "the designated re-wire hook [that] must get EVERY such
back-reference" — covers only `Workers` + `SetAnimMeta`. A new sim back-ref added to one path
silently misses the other (the exact `c54b712`/SetAnimMeta failure shape). Also duplicated
between the paths: the identical `onVertexMapChanged` dirty-rect lambda passed to
`_mapEditor.Init` (~1846-1861 vs ~2278-2293).
Fix direction: fold the `_sim.Set*` family + env collision callbacks into `WireSimCallbacks`
(they only need the live `_sim`/`_envSystem` forwarders, both valid post-recreate), or a
`WireSessionSystems()` helper both paths call after `_sim.Init`; extract the vertex-map lambda
into a private `OnGroundVertexMapChanged()` method.
</details>

# FIXED — StartScenario's inline flipbook loop drifted from ReloadFlipbooksFromRegistry (found 2026-07-22, fixed in `f421e1c`)

**Fixed in `f421e1c`:** both paths now use the shared loader (guarded so batch scenario runs
don't re-decode the EXR library per scenario).

<details><summary>Original entry</summary>

`Game1.StartScenario` (~2146-2159) hand-copies the flipbook load loop instead of calling
`Game1.ReloadFlipbooksFromRegistry()` (~563). The copies have drifted: the shared method
gained a per-def try/catch ("a malformed file must not kill StartGame — skip the one def and
log") that the scenario copy NEVER got — a malformed flipbook (e.g. an unsupported `.exr`)
crashes every `--scenario` run while normal play shrugs it off. The scenario copy also never
disposes/refreshes stale defs (guarded by `_flipbooks.Count == 0`).
Fix direction: replace the block with `if (_flipbooks.Count == 0) ReloadFlipbooksFromRegistry();`
— the method already ends with the `_wakeSystem.Init(_flipbooks)` the next line repeats (keep
the guard if reload-per-scenario cost matters for batch runs).
</details>

# FIXED — Map-editor one-time init only runs on the StartGame path (found 2026-07-22, fixed in `f421e1c`)

**Fixed in `f421e1c`:** the editor one-time feed moved to `InitMapEditorForWorld()` shared by
both paths, and `WriteSaveGame` now refuses while a scenario is active (`CaptureSaveData`
stays unguarded for the in-memory round-trip scenarios).

<details><summary>Original entry</summary>

`StartGame` gives `_mapEditor` its full init: `SetItemRegistry` + `SetSpellRegistry` +
`SetGameData` + `RestoreTabFromSettings` + `SetMapFilename` + `SetCorpseSettings` +
`SetGrassData`. `StartScenario` calls only `_mapEditor.Init(...)` + `SetItemRegistry` — so a
cold `--scenario` boot (editor screenshot scenarios open the map editor) runs the editor with
no GameData/spell registry/corpse settings and skips the `2f19f1d` tab restore. Same
"one-time init in one of N entry paths" class as `2f19f1d` itself. Related: StartScenario
never sets `_currentMapName` (or the editor Save target), so a save written during/after a
scenario records the PREVIOUS map's name — loading it rebuilds the wrong world.
Fix direction: move the editor one-time feed (GameData/registries/RestoreTabFromSettings) to a
single init point both paths share (e.g. right after `_mapEditor.Init`, or app-startup once
GameData exists), and have StartScenario stamp `_currentMapName = "scenario:" + name` or block
WriteSaveGame while `_activeScenario != null`.
</details>

# Strength / Encumbrance buffs never reach combat — raw stat reads in AttackResolver (found 2026-07-22)

`BuffStat.Strength` exists, is displayed as buffed (`UI/UnitInfoPanel.cs` ~329,
`UI/CharacterStatsUI.cs`), and **3 authored buffs in `data/buffs.json` modify it** — but the
two gameplay consumers read it RAW, so a Strength buff shows on the panel and does nothing:
- `Game/Combat/AttackResolver.cs` ~459: melee damage roll `strContribution = atkStats.Strength`
  (×1.25 two-handed) — Attack/Defense/Toughness at ~378/~474 are routed through
  `GetModifiedStat` at the SAME site; Strength was simply missed.
- `Game/Combat/AttackResolver.cs` ~654-659: knockdown STR contest, both sides raw.
Latent twin: `AttackResolver.cs` ~354 fatigue gain reads `atkStats.Encumbrance` raw
(`BuffStat.Encumbrance` exists, panel shows it buffed; no authored buffs yet).
Fix: `BuffSystem.GetModifiedStat(units, idx, BuffStat.Strength, atkStats.Strength)` at both
sites (c916a31 rule: when a stat is buffable, audit ALL readers).

# CombatSpeed buffs missed by the effort/sprint speed helpers (found 2026-07-22)

`Locomotion.UpdateSpeeds` (the single MaxSpeed writer) routes CombatSpeed through
`GetModifiedStat` (~292), and **7 authored buffs modify CombatSpeed** — but the side helpers
read it raw, so speed buffs don't apply on those paths:
- `Movement/Locomotion.cs` `ResolveEffortSpeed` (~175) — routines clamp `PreferredVel` with an
  UNBUFFED cap, so a speed-buffed unit's routine caps its own speed below its buffed MaxSpeed.
- `Movement/Locomotion.cs` `SprintTopSpeed` (~183) — pounce/trample charge speeds ignore
  speed buffs.
- Minor: `Game1.Animation.cs` ~339 cast-plant gate (`CombatSpeed * CastPlantGateSpeedMult`)
  raw — a heavily speed-buffed necro takes longer/inconsistently to pass the plant gate; and
  `Locomotion.cs` ~238 uses raw CombatSpeed as the ramp-rate denominator while accel IS buffed.
Fix: use the buff-list overload `BuffSystem.GetModifiedStat(u.ActiveBuffs, BuffStat.CombatSpeed,
u.Stats.CombatSpeed)` (BuffSystem.cs ~549 — no UnitArrays needed) in the helpers, guarded by
`ActiveBuffs.Count > 0` like UpdateSpeeds does.

# EnrollInHorde is a 4th spawn-then-re-archetype site (found 2026-07-22)

`Game/Simulation.cs` `EnrollInHorde` (~3217) hand-stamps `Archetype` + `Routine =
HordeMinionHandler.RoutineFollowing` + `Subroutine = 0` instead of calling
`AI.AIControl.ReassignArchetype` (the `427034b` helper). Two hazards: (a) when the def
resolves a NON-HordeMinion archetype, `RoutineFollowing` (byte 1) is a per-handler byte
applied to a different handler; (b) the new handler's `OnSpawn` never fires, so any setup
field added there silently misses this conversion path. Note `ReassignArchetype`'s minimal
ctx has `Horde == null`, so `HordeMinionHandler.OnSpawn`'s `ctx.Horde?.AddUnit` no-ops —
keep the explicit `_horde.AddUnit` after the call.
Fix: replace the three stamp lines with
`AI.AIControl.ReassignArchetype(_units, idx, arch, "enroll in horde")`.

# MOSTLY FIXED — Raw routine literal in HUD: `Routine == 4 // RoutineCommanded` (found 2026-07-22, HUD fixed)

`GameRenderer.Hud.cs` now compares `AI.HordeMinionHandler.RoutineCommanded` (verified
2026-07-22 during the UI audit). Residual: Scenario files still compare raw routine
literals (e.g. `DeerReAlertWhileCalmingScenario.cs` `curRoutine == 4 /*RoutineCalming*/`)
— lower priority, tests only.

# FIXED — Wrong-list weapon lookup for ranged pending attacks (found 2026-07-19, fixed 2026-07-21)

`Game1.Animation.cs` `ComputeWeaponCycleSeconds(unitIdx, weaponIdx, isRanged)` and
`Simulation.GetAttackAnimDurationSec(unitIdx, weaponIdx, isRanged)` now take the flag; the
archetype attack-override block passes `PendingWeaponIsRanged`. Ranged cycles come from
`Stats.RangedCooldownTime[weaponIdx]` (seconds — the same clock RangedUnitHandler locks
between shots), ranged anim names from `Stats.RangedWeapons[weaponIdx].AnimName`.

# MOSTLY FIXED — Editor key-repeat timer accumulated catch-up dt (found 2026-07-21, fixed)

`Editor/EditorBase.cs` `HandleTextInput` now repeats on the WALL CLOCK
(`_keyRepeatNextAt`/`_lastRepeatingKey`, 400ms initial delay then 30ms cadence via
`Environment.TickCount64` — inherently ≤1 repeat per real elapsed window), so
fixed-timestep catch-up bursts no longer duplicate chars. Residual: typing is still
**polled** (`_kb.GetPressedKeys()` per Update, no `Game.Window.TextInput` char events), so
a tap that goes down+up inside a single long frame is still dropped. Robust fix if it
recurs: switch char entry to `Window.TextInput` events, keep polling for nav keys only.

# Debounced flush does full-registry synchronous reload on the render thread (found 2026-07-21)

`Editor/SpellEditorWindow.cs` `MarkDirty()` override + `FlushPendingFlipbookReload`
(~lines 1833-1864): flipbook-manager edits debounce 400ms (`_fbReloadDueAt`) then call
`Game1.ReloadFlipbooksFromRegistry()` — which **reloads EVERY flipbook def from disk
synchronously**, incl. full `.exr` HDR decode (`Flipbook.Load` →
`ExrTgaTextures.LoadExrHdr`, whole-file read + per-pixel half-float conversion). Text
fields commit per keystroke, so any >400ms typing pause fires a full reload mid-edit → a
multi-hundred-ms stall → the key-repeat entry above turns it into dropped/repeated chars
(and the repeats call MarkDirty again, re-arming the loop). A debounce defers work; it
doesn't shrink it. Fix directions: reload ONLY the edited def (keyed rebuild of one
`_flipbooks` entry); and/or hold the flush while `_ui.IsTextInputActive`; the close-path
`force:true` flush already guarantees consistency on exit. Related per-keystroke churn:
the manager preview `_fbPreviewCache.GetOrLoad(fd.Path)` runs every frame keyed on the
live path (one disk probe per keystroke while typing `fb_path`), and the texture browser's
Up/Down nav decodes `.exr` previews synchronously per keypress (`SelectFile`).

# Sub-pixel DrawString positions in GameRenderer.Hud.cs (found 2026-07-22)

The PointClamp text-rounding rule (CLAUDE.md "UI Text Rendering") is violated at three
sites in `Necroking/GameRenderer.Hud.cs` — its private `DrawText` helpers (~822/~828) do
NOT round, unlike `HUDRenderer.Text` (~1044) and `EditorBase.DrawText` which do:
- `DrawGameOver` (~812, ~818): title + "Press R to restart" centered with
  `screenW / 2f - size.X / 2f` floats — the exact MeasureString-centering case CLAUDE.md
  calls out. Fix: cast to `(int)` (or round inside the `DrawText` helpers).
- Save-preview card inventory quantity (~775-777): `br - size` where `size` is a float
  `MeasureString` — sub-pixel bottom-right-aligned text. Fix: `(int)(br.X - size.X)` etc.
- F7 unit-info debug overlay (~404): `new Vector2(sp.X - info.Length, sp.Y - 28)` with
  float screen pos `sp` (debug-only, minor; also uses `info.Length` chars as pixels).
Elsewhere the rule is respected (CharacterStatsUI, MenuCommon, TableCraftMenuUI,
MapEditorWindow.DrawTextCentered, TooltipSystem, WidgetLayoutUtils all round; damage
numbers in `GameRenderer.World.cs` draw at float positions deliberately — smooth-moving
scaled world text, leave them).

# DrawTimeControls re-declares the speed presets (found 2026-07-22)

`UI/HUDRenderer.cs` `DrawTimeControls` (~945) declares
`stackalloc float[] { 0.1f, 0.25f, 0.5f, 1.0f, 1.5f, 2.0f }` instead of reading the
public canonical `TimeControlSpeeds` (~699) that `TimeControlsLayer.OnPointer` applies —
two copies of the presets ~250 lines apart that must stay in sync (a changed preset would
draw one speed and set another). Fix: iterate `TimeControlSpeeds` in the draw.

# Dead authored VFX fields — persisted in data, consumed by nothing (census 2026-07-21)

Authored/serialized fields that silently do nothing (same class as the FIXED StandupTimer
dead field; each is a "why doesn't my data change anything" trap):
- **`Effect.Alignment` (0=ground/1=upright)** — set from `FlipbookRef.Alignment`
  (`Render/EffectManager.cs` `SpawnSpellImpact` call sites) and mirrored through
  `Projectile.HitEffectAlignment` (`Game/SpellEffectSystem.cs`, `Game/PotionSystem.cs`),
  but **no draw path reads it** — `Render/SpellVfxDraw.cs` `DrawEffects`/`DrawFlipbookRefLoop`
  draw every effect screen-facing with rotation 0. "Ground" renders identically to
  "Upright". Fix direction: consume it in `SpellVfxDraw` (ground = squash Y by
  `camera.YRatio` for the iso ellipse, the `DrawHoverGroundMarkers` precedent) or delete
  the field + the enum validation in `SpellRegistry.cs`.
- **`beamChain*` / `strikeChain*` / `chainQuantity`** on `SpellDef` — declared, persisted
  in spells.json, consumed by NOTHING (planned chain lightning; already flagged in
  render.md's beam section).
