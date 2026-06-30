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
| Render/ (effects only) | [render.md](render.md) ◐ | **Partial** — the visual-effect/flipbook systems (`EffectManager`, `Flipbook`, `ReanimEffectSystem`, particle systems). Atlases, ground shader, bloom, shadows, fonts, HUD widgets still TODO |
| Jobs & workers | [jobs-workers.md](jobs-workers.md) ✅ | Worker economy — Job Board UI (`JobBoardUI`) + Grave Roster UI (`GraveRosterUI`), `WorkerSystem` (grave assignment, stockpiles, the pool→jobs `Dispatch` auto-assigner), `JobDef/JobState/JobRegistry`, `AI/WorkerHandler` FSM |
| Dev control server | [dev.md](dev.md) ✅ | The `--devserver` HTTP control channel: transport (`Dev/DevServer.cs` `DevServer`/`DevCommand`, `Dev/DevScript.cs` batch jobs) + every command verb in `Game1.Dev.cs` (`ExecuteDevCommand` switch), `Game1.DevData.cs` (`DevAddData`/state). **Where to add a new dev verb.** |

## Subsystems (under `Necroking/`) — most not yet documented

| Folder / area | Tentative responsibility (verify) | Doc |
|---------------|-----------------------------------|-----|
| `Game1.*` (root) | Top-level `Game1` partial class: loop, glue, player-facing entry points | game1-partials.md ✅ |
| `Core/` | Foundational types & sim core — `Vec2`, `Simulation`, units/corpses state, `DebugLog`, constants | (not yet documented) |
| `Data/` | Game-data model + JSON registries (spells, units, items, potions, buffs, weapons, armor) under `Data/Registries/` | (not yet documented) |
| `Game/` | Gameplay systems — spell targeting (`SpellCasting`), spell effects (`SpellEffectSystem`), crafting/table-craft, inventory, building menus, horde caps | (not yet documented) |
| `Render/` | Rendering subsystems — atlases, shadows, bloom, font manager, widget renderer, HUD renderer | [render.md](render.md) ◐ (effects only) |
| `UI/` | Overlays & panels — inventory, grimoire, skill book, character stats/sheet, unit info | (not yet documented) |
| `World/` | World/environment systems — env objects, foragables, walls, roads | (not yet documented) |
| `Movement/` | Pathfinding / steering / movement routines | (not yet documented) |
| `AI/` | Unit AI behaviors & routines (combat, routines like craft-at-table) | (not yet documented) |
| `GameSystems/` | Discrete systems — `DeathFogSystem`, etc. | (not yet documented) |
| `Spatial/` | Spatial partitioning / grid queries | (not yet documented) |
| `Algorithm/` | Standalone algorithms | (not yet documented) |
| `Editor/` | In-game immediate-mode editors (unit / spell / map / UI / item) | (not yet documented) |
| `Dev/` | Dev control server — `DevServer`, `DevCommand` (HTTP → `ExecuteDevCommand`) | (not yet documented) |
| `Scenario/` | Coded headless test scenarios (~125 files, `--scenario <name>`) | (not yet documented) |

## Behavior → area quick index

Use this to pick a starting area. When the routed area isn't documented yet, document it.

- **A spell does the wrong thing / adding a spell** → game1-partials.md (`Game1.Spells.cs`) + `Game/` (SpellCasting, SpellEffectSystem) + `Data/Registries/` (SpellRegistry). See also `docs/spells.md`.
- **Crafting / gathering / table craft** → game1-partials.md (`Game1.Crafting.cs`) + `Game/` (table-craft system).
- **Workers / jobs — assigning workers, job board, grave roster, auto-dispatch** → [jobs-workers.md](jobs-workers.md) (`UI/JobBoardUI.cs`, `UI/GraveRosterUI.cs`, `Game/Jobs/WorkerSystem.cs`, `AI/WorkerHandler.cs`).
- **Animation / cast-phase timing / attack anims** → game1-partials.md (`Game1.Animation.cs`) + `Render/` (anim controller/atlases).
- **Rendering looks wrong (world / units / corpses / HUD / spellbar)** → game1-partials.md (`Game1.Render.*`) + `Render/`.
- **HUD / spellbar / on-screen overlays** → game1-partials.md (`Game1.Render.HUD.cs`) + `UI/`.
- **Inventory / grimoire / skill book / character sheet panels** → `UI/`.
- **Unit stats / spell / item / map / UI editors** → `Editor/`.
- **Game data values (units, items, spells JSON)** → `Data/` + `data/*.json` (use the `edit-game-data` skill to edit those).
- **Unit AI / routines / combat decisions** → `AI/`.
- **Pathfinding / movement** → `Movement/`.
- **Environment objects / foragables / walls / world content** → `World/` (and map content lives in `data/maps/`, not code).
- **Dev/test commands driving the running game** → game1-partials.md (`Game1.Dev.cs` → `ExecuteDevCommand`) + `Dev/`.
- **Headless regression tests** → `Scenario/` (and `docs/testing-scenarios.md`).

## When you extend the map

1. Pick the area, glob its folder, read/LSP the files, write `<area>.md` in the
   [game1-partials.md](game1-partials.md) format.
2. Move its row to "Documented areas", mark the doc ✅ here, and tighten its behavior→area
   entries above with the real file names you found.
3. Commit the new doc (the skill is shared).
