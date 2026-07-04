# Overview — behavior routing map

Entry point for the **locate-behavior** skill. Match the request to an area below, then
open the listed `<area>.md`. Areas marked **(not yet documented)** have no doc yet —
**research them and add the doc** per README.md → "Self-healing", then update this file.

> Hints for undocumented areas are **tentative** (inferred from folder/namespace names,
> not yet verified). Don't trust them as fact — confirm by reading the code, and capture
> what you learn in a new `<area>.md`.

## Documented areas

| Area | Doc | Covers |
|------|-----|--------|
| Game1.* root partials | [game1-partials.md](game1-partials.md) ✅ | Frame loop, input, menu state, orchestration of every system; player spell-cast pipeline, crafting, animation tick, all rendering entry points, map load/save, dev-command dispatch |
| Render/ (pipeline + effects) | [render.md](render.md) ◐ | **The draw-dispatch pipeline** — top-level `GameRenderer.Draw` pass sequence, `EffectBatch` (canonical blend/sampler + flush-with-shader `BeginEffect`/`EndEffectResume*`), `Bloom` RT/compositing, the Y-sort depth list, `Renderer.DrawSprite`, `UIShaders` — plus the visual-effect/flipbook systems (`EffectManager`, `Flipbook`, `ReanimEffectSystem`, particles). Atlas internals, shadows, fonts, HUD widgets still TODO |
| Jobs & workers | [jobs-workers.md](jobs-workers.md) ✅ | Worker economy — Job Board UI (`JobBoardUI`) + Grave Roster UI (`GraveRosterUI`), `WorkerSystem` (grave assignment, stockpiles, the pool→jobs `Dispatch` auto-assigner), `JobDef/JobState/JobRegistry`, `AI/WorkerHandler` FSM |
| Corpses & pickup/carry | [corpses.md](corpses.md) ✅ | `Corpse` data model in `Simulation`, player pickup/carry (`CorpseInteractionManager`, F-key + corpse-hover hit-test in `Game1.cs`), foragable-style proximity gather, where "click corpse → walk → pick up" goes |
| Unit AI archetypes & routines | [ai.md](ai.md) ✅ | `IArchetypeHandler`/`ArchetypeRegistry` (name→id→handler), per-tick dispatch in `Simulation`, archetype assignment at spawn (`Game1.SpawnUnit`), `SubroutineSteps` move primitives, `WorkerHandler` FSM template, **where to add a new per-unit behavior** |
| World — env objects & foragables | [world.md](world.md) ✅ | `EnvironmentSystem` (env-def + placed-object store, `AddObject`/`FindDef`/`CollectForagable`/respawn), `ForagableSystem` (player pickup arc), death→corpse hook in `RemoveDeadUnits`, and the Vogel-spiral anti-stack packing precedent (`HordeSystem`). Where runtime world-object spawning + foragable-eat goes |
| Dev control server | [dev.md](dev.md) ✅ | The `--devserver` HTTP control channel: transport (`Dev/DevServer.cs` `DevServer`/`DevCommand`, `Dev/DevScript.cs` batch jobs) + every command verb in `Game1.Dev.cs` (`ExecuteDevCommand` switch), `Game1.DevData.cs` (`DevAddData`/state). **Where to add a new dev verb.** |
| Editor/ (UI/widget editor + shared text-field) | [editor.md](editor.md) ◐ | In-game immediate-mode editors. **Shared editable-text-field/focus mechanism** (`EditorBase.DrawTextField`, single `_activeFieldId`/`_inputBuffer`, `HandleTextInput`) — the source of "typed text bleeds into next selected object". **UI/widget editor** covered in depth — `UIEditorWindow.cs` data models + copy/paste/duplicate clone logic (`CloneWidget`/`CloneChild`, which omit fields). Other editors stubbed. |
| Combat — attack resolution & range | [combat.md](combat.md) ✅ | The `PendingAttack` → hit-frame → `ResolvePendingAttack`/`ResolveMeleeAttack` pipeline, the canonical melee-range formula (`Game/Combat/MeleeRangeUtil.cs`, shared by sim + AI), and **why range is gated at stamp-time not resolve-time** — incl. the player click→melee order in `Game1.WorldClicks.cs` `TryAttackClick` |
| UI/ — overlays & panels | [ui.md](ui.md) ✅ | The right-side selected-unit sheet (`UI/UnitInfoPanel.cs`) + where the show-panel decision lives (`Game1.cs` 'U'/'O'/auto-hover, units only), the corpse-pile "inventory" cursor tooltip (`HUDRenderer.DrawObjectTooltip` + `WorkerSystem.PiledCorpseLines`), boar belly store (`Simulation._boarBellies`). Widget renderer + popup stack. |
| Zones — authored map areas (village / wolf-pack / deer-herd rects) | [zones.md](zones.md) ✅ | `MapZone`/`ZoneKind`/`ZoneSystem` (`Game/ZoneTypes.cs`), load-time apply (`Game1.Zones.cs` `ApplyZones`), `MapData.LoadZones`, map-editor Zones tab (draw/select/drag + Village left config panel + `SaveZones` sidecar `data/maps/<map>_zones.json`) |
| Movement — pathfinding, ORCA, collision | [movement.md](movement.md) ✅ | The whole movement stack: sector flow-field pathfinder (`World/Pathfinder.cs` — sector Dijkstra, inter-sector BFS routing, imaginary-chunk stuck escape, budgeted pathfinding), ORCA solver (`Movement/Orca.cs`), `Simulation.UpdateMovement` (neighbor gather, stuck nudge, accel model, wall collision), horde formation slots (`Game/HordeSystem.cs`), quadtree/env spatial indices, scripted-movement owners (physics/jump/trample) |

## Subsystems (under `Necroking/`) — most not yet documented

| Folder / area | Tentative responsibility (verify) | Doc |
|---------------|-----------------------------------|-----|
| `Game1.*` (root) | Top-level `Game1` partial class: loop, glue, player-facing entry points | game1-partials.md ✅ |
| `Core/` | Foundational types & sim core — `Vec2`, `Simulation`, units/corpses state, `DebugLog`, constants | (not yet documented) |
| `Data/` | Game-data model + JSON registries (spells, units, items, potions, buffs, weapons, armor) under `Data/Registries/` | (not yet documented) |
| `Game/` | Gameplay systems — spell targeting (`SpellCasting`), spell effects (`SpellEffectSystem`), crafting/table-craft, inventory, building menus, horde caps | (not yet documented) |
| `Render/` | Rendering subsystems — atlases, shadows, bloom, font manager, widget renderer, HUD renderer | [render.md](render.md) ◐ (effects only) |
| `UI/` | Overlays & panels — grimoire, skill book, character stats/sheet, unit info | [ui.md](ui.md) ✅ |
| `World/` | World/environment systems — env objects, foragables, walls, roads | [world.md](world.md) ✅ |
| `Movement/` | Unit data model + ORCA solver + facing util (pathfinder itself is in `World/Pathfinder.cs`) | [movement.md](movement.md) ✅ |
| `AI/` | Unit AI behaviors & routines (combat, routines like craft-at-table) | [ai.md](ai.md) ✅ |
| `GameSystems/` | Discrete systems — `DeathFogSystem`, etc. | (not yet documented) |
| `Spatial/` | Spatial partitioning / grid queries | (not yet documented) |
| `Algorithm/` | Standalone algorithms | (not yet documented) |
| `Editor/` | In-game immediate-mode editors (unit / spell / map / UI / item) | [editor.md](editor.md) ◐ (UI/widget editor only) |
| `Dev/` | Dev control server — `DevServer`, `DevCommand` (HTTP → `ExecuteDevCommand`) | (not yet documented) |
| `Scenario/` | Coded headless test scenarios (~125 files, `--scenario <name>`) | (not yet documented) |

## Behavior → area quick index

Use this to pick a starting area. When the routed area isn't documented yet, document it.

- **A spell does the wrong thing / adding a spell** → game1-partials.md (`Game1.Spells.cs`) + `Game/` (SpellCasting, SpellEffectSystem) + `Data/Registries/` (SpellRegistry). See also `docs/spells.md`.
- **Crafting / gathering / table craft** → game1-partials.md (`Game1.Crafting.cs`) + `Game/` (table-craft system).
- **Corpses — data model, picking up / carrying a corpse, click/F-key corpse interaction, corpse piles** → [corpses.md](corpses.md) (`Game/Simulation.cs` `Corpse`, `Game/CorpseInteractionManager.cs`, `Game1.cs` input + `_hoveredCorpseIdx`, `Game/ForagableSystem.cs` for the gather pattern).
- **Workers / jobs — assigning workers, job board, grave roster, auto-dispatch** → [jobs-workers.md](jobs-workers.md) (`UI/JobBoardUI.cs`, `UI/GraveRosterUI.cs`, `Game/Jobs/WorkerSystem.cs`, `AI/WorkerHandler.cs`).
- **Animation / cast-phase timing / attack anims** → game1-partials.md (`Game1.Animation.cs`) + `Render/` (anim controller/atlases).
- **Rendering looks wrong (world / units / corpses / HUD / spellbar)** → game1-partials.md (`Game1.Render.*`) + `Render/`.
- **Draw pipeline / render order / passes / shaders-per-draw / blend-sampler state / where a new render pass or pass-dispatcher goes** → [render.md](render.md) "The draw-dispatch pipeline" (`Necroking/GameRenderer.Draw.cs` `Draw()` = the pass sequence; `Render/EffectBatch.cs` = canonical state + flush-with-shader `BeginEffect`; `Render/Bloom.cs` = RT/compositing; `_g._depthItems` Y-sort in `GameRenderer.Units.cs` = the only draw queue).
- **HUD / spellbar / on-screen overlays** → game1-partials.md (`Game1.Render.HUD.cs`) + `UI/`.
- **Spell list / spells menu panel position, anchoring, or its tab/tile hit-testing (the Grimoire)** → [ui.md](ui.md) (`UI/GrimoireOverlay.cs` → `Layout(screenH)` sets `_x`/`_y`, shared by draw + hit-test).
- **Selected/inspected unit right-side sheet, grimoire, skill book, character sheet panels; which panel shows for a selected entity; corpse-pile contents display; boar belly panel** → [ui.md](ui.md) (`UI/UnitInfoPanel.cs`, decision in `Game1.cs` 'U'/'O'/auto-hover, `HUDRenderer.DrawObjectTooltip` + `WorkerSystem.PiledCorpseLines` for pile contents, `Simulation._boarBellies` for belly data).
- **Unit stats / spell / item / map editors** → `Editor/`.
- **Editor text field: typed value bleeds into the next selected object, field focus/commit, buffer binding** → [editor.md](editor.md) (`Editor/EditorBase.cs` `DrawTextField` + single `_activeFieldId`/`_inputBuffer` + `HandleTextInput`; no flush on selection change in per-editor `DrawScrollableList` handlers — single-point-of-fix is in `EditorBase`).
- **UI/widget editor — data models, copy/paste/duplicate a widget or child, widget/element field list** → [editor.md](editor.md) (`Editor/UIEditorWindow.cs`: `CloneWidget`/`CloneChild` do the copy but omit fields; model classes `UIEditor{NineSlice,Element,Child,Widget}Def` at the top of the file; runtime consumers `UI/RuntimeWidgetRenderer.cs`/`UI/WidgetLayoutUtils.cs`).
- **Game data values (units, items, spells JSON)** → `Data/` + `data/*.json` (use the `edit-game-data` skill to edit those).
- **Melee/ranged attack resolution, "am I in range to hit", melee lands at wrong range, player click→attack order, where `PendingAttack` is stamped/resolved** → [combat.md](combat.md) (`Game/Combat/MeleeRangeUtil.cs` = the range formula; `Game/Simulation.cs` `ResolvePendingAttack`/`ResolveMeleeAttack` + attack-selection loop; `Game1.WorldClicks.cs` `TryAttackClick` = player click order; `Game1.Animation.cs` hit-frame trigger). Range is gated at **stamp** time, not resolve time.
- **Unit AI / routines / combat decisions / adding a per-unit behavior or archetype** → [ai.md](ai.md) (`AI/IArchetypeHandler.cs` registry, `AI/SubroutineSteps.cs` move primitives, `AI/WorkerHandler.cs` FSM template; register in `Game1.cs`).
- **Routine transitions / interrupting a unit's AI from outside / OnRoutineExit cleanup / "unit stuck after external system touched it"** → [ai.md](ai.md) "Shared transition logic" (`AI/AIContext.cs` `TransitionTo`, `AI/AIControl.cs` `Interrupt`/`StartRoutine`, `ai_trace` dev command). Never write `Unit.Routine` raw.
- **Archer/ranged unit AI — kiting, firing arrows, ranged weapon selection** → [ai.md](ai.md) (`AI/RangedUnitHandler.cs`) + [combat.md](combat.md) "Ranged / projectiles" for the arrow-spawn pipeline.
- **Projectiles — arrows, lobbed/arcing shots, fireballs, potion lobs, projectile collision** → [combat.md](combat.md) "Ranged / projectiles" (`Game/Projectile.cs` `ProjectileManager.SpawnArrow` volley=ballistic arc; resolve in `Game/Simulation.cs` `ResolvePendingAttack` ranged branch). Note: no wall collision, no LoS utility exists.
- **Floating text / damage numbers ("Too Far", "+N" pickups) — spawn site, drift, head-height positioning** → [render.md](render.md) "Floating text / damage numbers" (`DamageNumber` struct in `Game/SpellEffectSystem.cs`, `Game1.cs` `SpawnCastFailText` + update tick, `GameRenderer.World.cs` `DrawDamageNumbers`; note the `Height * YRatio` vs sprite-height mismatch).
- **Pathfinding / movement — units path wrong, get stuck, crowd avoidance, wall collision, formation slots, acceleration/gait feel** → [movement.md](movement.md) (`World/Pathfinder.cs` `GetDirection` = the flow-field pathfinder + imaginary-chunk stuck escape; `Movement/Orca.cs` = ORCA solver; `Game/Simulation.cs` `UpdateMovement` = per-frame gather/solve/collide; `Game/HordeSystem.cs` = formation slot targets).
- **Environment objects / foragables / mushrooms / walls / world content / runtime world-object spawning / eating foragables / on-death drops** → [world.md](world.md) (`World/EnvironmentSystem.cs` `AddObject`/`FindDef`/`CollectForagable`, `Game/ForagableSystem.cs`, death hook in `Game/Simulation.cs` `RemoveDeadUnits`; and map content lives in `data/maps/`, not code).
- **Per-game state ownership / memory leaks across map reloads / where a new per-game system or resource should live / state bleeding from one map to the next** → [game1-partials.md](game1-partials.md) (`Necroking/Game/GameSession.cs` — the recreated-each-load owner Game1 forwards to; `StartGame` does `_session.Dispose(); _session = new()`). App-vs-game classification + migration checklist in `todos/gamesession-migration.md`; `mem` dev command for spotting reload leaks.
- **Map zones / areas — village, wolf-pack, deer-herd rects; where areas are defined, edited (map editor Zones tab), saved, applied at load; per-zone config (population, spawn tables)** → [zones.md](zones.md) (`Game/ZoneTypes.cs` `MapZone`/`ZoneKind`, `Game1.Zones.cs` `ApplyZones`, `Data/MapData.cs` `LoadZones`, `Editor/MapEditorWindow.cs` Zones tab + `DrawZoneLeftPanel` + `SaveZones`). Zones are load-time-only; no in-game click path exists.
- **Dev/test commands driving the running game** → game1-partials.md (`Game1.Dev.cs` → `ExecuteDevCommand`) + `Dev/`.
- **Headless regression tests** → `Scenario/` (and `docs/testing-scenarios.md`).

## When you extend the map

1. Pick the area, glob its folder, read/LSP the files, write `<area>.md` in the
   [game1-partials.md](game1-partials.md) format.
2. Move its row to "Documented areas", mark the doc ✅ here, and tighten its behavior→area
   entries above with the real file names you found.
3. Commit the new doc (the skill is shared).
