# Code Map — what each C# folder is for

All C# lives in the single project `Necroking/Necroking.csproj` (there is no separate
editor project; `tools/` is Python). This doc says what each folder under `Necroking/`
is for and **where new code should go**.

**How to read it:**
- The rules are **prescriptive for new code, descriptive for existing code.** The flagged
  oddities are known-and-tolerated — nothing is being moved or renamed — but they are
  *not* templates to imitate.
- This doc maps folder → purpose. For behavior → file routing ("where is X handled?"),
  use the `locate-behavior` skill and [locate-behavior/overview.md](locate-behavior/overview.md).
  Detailed boundary rules live in class-level doc comments (`Game1.cs`, `Game/Simulation.cs`,
  `Game/GameSession.cs`, `World/EnvironmentSystem.cs`, …) — this doc points at them rather
  than restating them.
- Namespace convention: `Necroking.<FolderPath>`. Exceptions are flagged inline; the big
  one is the `Game/` folder (three namespaces — see its section).

## All folders at a glance

| Folder | Namespace(s) | Purpose |
|---|---|---|
| `Core/` | `Necroking.Core` | Engine-agnostic primitives everyone depends on (Vec2, GameClock, GamePaths, JSON/file utils) |
| `Algorithm/` | `Necroking.Algorithm` | Pure, standalone algorithms with no game-state knowledge (generic A*, scatter packing) |
| `Spatial/` | `Necroking.Spatial` | Generic spatial-query data structures (Quadtree, AABB) |
| `Data/` | `Necroking.Data` | Game-data model + `GameData` aggregate, map file schema, shared enums |
| `Data/Registries/` | `Necroking.Data.Registries` | The id-keyed `data/*.json` registries on `RegistryBase<TDef>` |
| `World/` | `Necroking.World` | Terrain/environment substrate: pathfinder, env objects, ground, walls, roads |
| `Movement/` | `Necroking.Movement` | Unit data model + how intent becomes motion (Locomotion, ORCA) |
| `Game/` | `Necroking.Game` / `Necroking.GameSystems` / `Necroking` ⚠ | The gameplay core: `Simulation`, `GameSession`, and most per-tick gameplay systems |
| `Game/Combat/` | `Necroking.GameSystems.Combat` ⚠ | Shared combat math (melee range, intercept) |
| `Game/Jobs/` | `Necroking.Game.Jobs` | Worker economy: WorkerSystem brain, job defs/state |
| `Game/SkillEffects/` | `Necroking.Game.SkillEffects` | Skill/passive effect application |
| `GameSystems/` | `Necroking.GameSystems` ⚠ | Four more gameplay systems; historical spillover of the same namespace as `Game/` |
| `AI/` | `Necroking.AI` | Per-unit AI: archetype handlers deciding what a unit *wants* to do |
| `Render/` | `Necroking.Render` | Everything wired into the draw pipeline: atlases, camera, effects, anim state, HUD |
| `UI/` | `Necroking.UI` | Player-facing overlays/panels with hit-testing, plus widget infra |
| `Necroking/` (root) | `Necroking` | The app shell: `Program`, `Game1.*` partials (glue), `GameRenderer.*` partials (Draw) |
| `Editor/` | `Necroking.Editor` | In-game developer/content editors on `EditorBase` |
| `Dev/` | `Necroking.Dev` | `--devserver` HTTP transport (the pipe, not the verbs) |
| `Net/` | `Necroking.Net` | Multiplayer transport/protocol — isolated and brittle, read its README first |
| `Scenario/` | `Necroking.Scenario` | The coded headless-test harness (`--scenario`) |
| `Scenario/Scenarios/` | `Necroking.Scenario.Scenarios` | One file per checked-in regression scenario (~126) |

## Where do I put new code?

- **New gameplay system (per-tick world behavior)** → file in **`Game/`**, namespace
  **`Necroking.GameSystems`**. Matches `Simulation`'s namespace and where the bulk already
  lives. The `GameSystems/` *folder* is legacy — never add files there.
- **New AI behavior/archetype** → `AI/`, a handler on `IArchetypeHandler`. AI sets intent
  (targets, `PreferredVel`); execution stays in Locomotion/Simulation.
- **New player-facing overlay/panel** → `UI/` — not `Render/`, not `Game/`, not `Editor/`,
  despite the flagged precedents in each.
- **New developer/content editor** → `Editor/`, built on `EditorBase`.
- **New `data/*.json` registry** → `Data/Registries/` on `RegistryBase<TDef>`. New pure
  data schema/type → `Data/`.
- **Pure generic algorithm** (no game types) → `Algorithm/`. Spatial query structure →
  `Spatial/`. Game-bound routing/terrain → `World/`.
- **Engine-agnostic primitive** everyone may need → `Core/` (must reference no game types).
- **New draw pass, atlas handling, or visual-only sim** → `Render/`.
- **New dev-command verb** → `Game1.Dev.cs` `ExecuteDevCommand` — NOT `Dev/` (transport only).
- **New regression test** → `Scenario/Scenarios/`, one file per scenario
  (see [testing-scenarios.md](testing-scenarios.md)).
- **New per-game state/resource** → owned by `GameSession`/`Simulation`, never a `Game1`
  field (see the class docs on `Game1.cs` and `GameSession.cs`).
- **Networking** → only when the task is explicitly multiplayer; read
  [Necroking/Net/README.md](../Necroking/Net/README.md) first.
- **Movement feel** (effort→velocity, facing, movement anim) → `Movement/Locomotion.cs`.
  **New unit data field** → `Movement/UnitSystem.cs` (the unit *model*, despite the name).

## Folder reference

### `Core/`
**Purpose:** the lowest layer — engine-agnostic primitives and utilities everything else
depends on: `Vec2`, `SimplexNoise`, `GameClock` (WorldDt/pause authority), `GamePaths`,
`HdrColor`/`ColorUtils`, `InputState`, `AtomicFile`/`JsonFile`/`JsonClone`, `DebugLog`.
**What goes here:** small primitives with zero game-type references.
**Not here:** anything that knows about units, spells, maps → `Data/` or `Game/`.
`CombatTarget` here is just a lightweight target reference; combat *resolution* is in
`Game/Combat` + `Simulation`.

### `Algorithm/`
**Purpose:** standalone, dependency-light solvers — generic `AStar` (grid + world variants)
and `ScatterPacking`. Pure computation, no game state.
**Not here:** the actual unit router is `World/Pathfinder.cs` (world-graph-bound);
query data structures are `Spatial/`.

### `Spatial/`
**Purpose:** generic spatial-partitioning structures — `Quadtree` + `AABB` for
range/neighbor queries.
**Not here:** specialized indices live with their domain — e.g. `World/EnvSpatialIndex.cs`
(env objects) stays in `World/`.

### `Data/` and `Data/Registries/`
**Purpose:** the authored-content layer. `Data/` holds the model types and the `GameData`
aggregate that loads everything, plus `MapData` (map file schema/load-save), shared enums
(`Enums.cs`, `CombatTypes.cs`), and `PlacedUnit`. `Data/Registries/` holds one class per
id-keyed `data/*.json` file (units, spells, buffs, weapons, …), all built on
`RegistryBase<TDef>` — the single JSON load/save engine (atomic, save-if-changed).
`GameSettings.cs` is the per-machine `user settings/settings.json` model.
**What goes here:** def schemas and their loaders. Pure data — no per-frame behavior.
**Not here:** systems that *consume* the defs (→ `Game/`), editors that author them
(→ `Editor/`). Routine JSON content edits use the `edit-game-data` skill, not code.

### `World/`
**Purpose:** the static world substrate the sim runs on: `Pathfinder` (sector flow-field
router — the real one), `EnvironmentSystem` (env-object defs + placed instances; see its
class doc), `GroundSystem`, `TileGrid`, `WallSystem`, `RoadSystem`, `FlowField`,
`EnvSpatialIndex`.
**Not here:** dynamic unit/combat state (→ `Simulation`), generic algorithms (→ `Algorithm/`).

### `Movement/`
**Purpose:** the unit data model and how intent becomes motion. `Locomotion.cs` is THE
single home for effort→speed, movement animation, and facing selection; `Orca.cs` is the
local collision-avoidance solver.
**Known oddity — don't imitate:** `UnitSystem.cs` is misnamed — it is the unit **data
model** (`Unit`, the SoA `UnitArrays`, `UnitUtil`, `NecromancerState`), not a system; its
header says to treat it as "UnitModel.cs". The movement *tick* itself is
`Simulation.UpdateMovement`, which calls into this folder.
**Not here:** deciding *where* a unit wants to go (→ `AI/`), path routing (→ `World/Pathfinder.cs`).

### `Game/` — the gameplay core
**Purpose:** `Simulation.cs` (the headless, deterministic world model — the
Simulation-vs-Game1 boundary rule is in its class doc and `Game1.cs`'s), `GameSession.cs`
(per-game state owner, recreated each map load), and most of the per-tick gameplay
systems: damage, buffs, horde, physics, triggers, spells (`SpellCasting`,
`SpellEffectSystem`), poison clouds, lightning, glyphs, villages, day/night, foragables,
potions, crafting, inventory, projectiles, player resources, combat log.
**What goes here:** new gameplay systems — in this folder, namespace `Necroking.GameSystems`.

**⚠ The namespace split (historical — the one big irregularity):** this folder spans
three namespaces: ~20 files (including `Simulation`) are `Necroking.GameSystems`, ~12 are
`Necroking.Game`, and `GameSession.cs`/`SpellBarBindings.cs` are root `Necroking`.
The top-level `GameSystems/` folder holds four more files of the same
`Necroking.GameSystems` namespace. **Treat "GameSystems" as a namespace, not the folder** —
the folder split carries no meaning. New systems: `Game/` folder + `GameSystems` namespace.

**Known oddity — don't imitate:** `InventoryUI`, `BuildingMenuUI`, `CraftingMenuUI`,
`TableCraftMenuUI` are player UI living here; new panels go in `UI/`.

#### `Game/Combat/`
Shared combat math: `MeleeRangeUtil` (the canonical "am I in range" formula used by both
Simulation and AI) and `InterceptUtil`. ⚠ Namespace is `Necroking.GameSystems.Combat`,
not `Necroking.Game.Combat`.

#### `Game/Jobs/`
The worker economy: `WorkerSystem` (the "brain" — policy/dispatch; per-unit FSM lives in
the AI handler), `JobRegistry`/`JobDef`/`JobState`.

#### `Game/SkillEffects/`
Skill/passive effect application (`SkillEffects.cs`).

### `GameSystems/`
Four gameplay systems (`TrampleSystem`, `JumpSystem`, `DeathFogSystem`,
`WeaponBonusEffect`) that share the `Necroking.GameSystems` namespace with the `Game/`
folder — see the namespace-split note above. **Legacy folder: don't add files here.**
Trample/Jump (with `Game/PhysicsSystem`) form the "scripted motion" cluster — systems
that own a unit's movement while active.

### `AI/`
**Purpose:** per-unit AI — `IArchetypeHandler` plus one handler per archetype (wolves,
deer, villagers, workers, ranged, corpse puppets, …), shared move primitives
(`SubroutineSteps`), squads (`SquadSystem`), awareness.
**Boundary vs Movement:** AI decides *what the unit wants* (intent, targets, effort,
`PreferredVel`); `Movement/` + `Simulation` turn that into actual motion. "Should it
chase/flee?" → here. "How does that become velocity/facing/animation?" → `Movement/`.

### `Render/`
**Purpose:** everything wired into the GPU draw pipeline: `RenderPipeline`, materials,
sprite atlases/queue, `Camera25D`, bloom/god rays/fog-of-war, shadows, fonts, per-unit
animation state (`AnimController`/`AnimResolver` — presentation-tier, see the "rules of
the road" banner), effect systems, and visual-only sims (`WadingWakeSystem`,
`GroundFogSystem`).
**Boundary vs UI:** `Render/` = drawn as part of the frame pass sequence; `UI/` =
overlay/widget panels with hit-testing.
**Known oddity — don't imitate:** `HUDRenderer` and `CharacterStatsUI` are player UI
panels living here; new panels go in `UI/`.

### `UI/`
**Purpose:** player-facing overlays and panels — grimoire, skill book, unit info sheet,
job board, grave roster, tooltips — plus the shared widget infrastructure:
`RuntimeWidgetRenderer` (draws the UI-editor widget defs at runtime), `UIHitRegistry`,
`NineSlice`, `PopupManager`.
**Not here:** developer content editors (→ `Editor/`), pipeline-level drawing (→ `Render/`).

### Root `Necroking/` — the app shell
**Purpose:** `Program.cs` plus two partial classes split by concern, each file carrying a
`// Game1 partial:` / `// GameRenderer partial:` banner:
- `Game1.*` — the MonoGame `Game` subclass: app lifecycle, input, menus, and the Update
  orchestrator. Glue only — the rule is in the class doc on `Game1.cs`: app-lifetime
  objects live on Game1, per-game state in `GameSession`/`Simulation`, and if a method
  here grows real logic it's in the wrong place. Routing map:
  [locate-behavior/game1-partials.md](locate-behavior/game1-partials.md).
- `GameRenderer.*` — the entire Draw pipeline (extracted from Game1), reaching state via
  a back-reference.

### `Editor/`
**Purpose:** the in-game immediate-mode developer/content editors — map, unit, spell,
item, wall, env-object, UI-widget, settings — all built on `EditorBase` (shared panels,
fields, focus, reflection-driven property rendering).
**Boundary vs UI:** editors author content (edit the JSON registries live) for
developers; `UI/` is what players see.
**Known oddity — don't imitate:** `MultiplayerWindow.cs` is a player-facing pause-menu
window that lives here because it's built on the editor widget base.

### `Dev/`
**Purpose:** the `--devserver` HTTP control channel — `DevServer` (HTTP → queued
commands) and `DevScript` (batch jobs). **Transport only:** the command verbs live in
`Game1.Dev.cs` `ExecuteDevCommand`; add new verbs there, not here. See
[devpreview.md](devpreview.md).

### `Net/`
**Purpose:** the multiplayer transport and wire protocol (`NetProtocol`, `NetSession`,
`RemotePlayer`). Deliberately isolated and brittle: field order in the packet code IS the
wire format (bump `ConnectionKey` on change), everything assumes single-threaded
`PollEvents`, and the dependency points one way — nothing here may reference
`Simulation`/`Game1`/UI. **Do not touch unless the task is explicitly multiplayer**; read
[Necroking/Net/README.md](../Necroking/Net/README.md) first. Game-facing glue lives in
`Game1.Net.cs`; the UI in `Editor/MultiplayerWindow.cs`.

### `Scenario/` and `Scenario/Scenarios/`
**Purpose:** the coded headless regression harness run via `--scenario <name>`:
`ScenarioBase` (opt-in flags for UI/grass/ground), `ScenarioRegistry`,
`ScenarioScreenshot`. `Scenarios/` holds the concrete tests, one file each (~126).
Test-only — the shipping game never depends on it. See
[testing-scenarios.md](testing-scenarios.md); prefer a `drive-game` command for one-off
checks over a new scenario.
