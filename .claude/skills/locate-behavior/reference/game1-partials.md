# Game1.* partials — top-level game loop & system orchestration

`Game1` is the single MonoGame `Game` subclass (`public partial class Game1 : Microsoft.Xna.Framework.Game`),
split across several partial files by concern. It owns the frame loop (`Initialize` → `LoadContent` →
`Update` → `Draw`), holds the live game state (`_sim`, `_camera`, `_gameData`, the editors, the UI
panels), and wires every subsystem together. Behavior found in these files is almost always
**orchestration / glue / the player-facing entry point** — the per-frame call, the input branch, the
draw pass — while the deep logic lives in `Game/`, `Render/`, `World/`, `AI/`, `Data/Registries/`, etc.
When a behavior is "wrong", start here to find *where it's invoked*, then follow the call into the
owning subsystem.

## Files

### `Necroking/Game1.cs` — main class: fields, lifecycle, input, menu state, map load/save
What lives here: the bulk of `Game1` — all the private fields/systems (`_graphics`, `_spriteBatch`,
`_sim`, `_camera`, renderers, editors, UI panels), the `MenuState` enum, the MonoGame lifecycle
(`Initialize`, `LoadContent`, `Update`), the menu/input handling inside `Update`, window-mode and
screen-target management, map/scenario start, unit spawning, spellbar save/load, melee/gather input,
and assorted gameplay helpers (god mode, cheats, cast-fail text, item textures). This is the spine
the other partials extend.
Key members: `Initialize`, `LoadContent`, `Update`, `StartGame`, `StartScenario`, `SpawnUnit`,
`EnsureInventoryUIsInitialized`, `EnsureUIEditorInitialized`, `ApplyWindowMode`, `ToggleWindowMode`,
`RefreshScreenSizedTargets`, `OnClientSizeChanged`, `SaveSpellBars`, `LoadSpellBarSlots`,
`TryMeleeOrGather`, `FindGraveUnderCursor`, `FindNecromancer`, `FindClosestEnemyToPoint`,
`TryAutoLearn`, `TryConsumeInventoryItem`, `ToggleGodMode`, `CheatAddAllSkillcounters`,
`SpawnCastFailText`, `SyncGrassMapReference`, `ReconcileTopLevelEditorLayers`, `_menuState`,
`_sim`, `_camera`, `_gameData`. Note: `ExecuteDevCommand` is *declared* used here (drained in
`Update` via `_devServer?.Drain(ExecuteDevCommand)`) but its body lives in `Game1.Dev.cs`.
Look/edit here when: the game won't start or load a map; a keypress / mouse click / menu button does
the wrong thing; fullscreen/windowed/resize behaves wrong; the necromancer or a unit spawns wrong;
spellbar slots don't persist; melee-attack or right-click-gather input is off; god mode / cheats;
a new field or system needs wiring into the loop. If a behavior is "what happens each frame" or
"what happens on this input", it's routed from here.
See also: `Necroking/Game1.Dev.cs` (the `ExecuteDevCommand` body), `Core/Simulation.cs`,
`Render/` (renderers constructed here), `Editor/` (editors), `data/maps/` (map JSON — world content
lives in the map, not in startup spawns; see CLAUDE.md).

### `Necroking/Game1.Animation.cs` — per-frame animation, cast-phase & attack-anim updates
What lives here: the per-frame animation tick — advancing sprite flipbooks for every unit, driving
channeled/cast animation state machines, table-craft channel buffs, and keeping spawned effects and
wading sink-offsets glued to their moving owners. Called from `Update`.
Key members: `UpdateAnimations`, `UpdateChanneledCast`, `UpdateTableChannelBuff`,
`UpdateEffectSpawnPositions`, `UpdateWadingSinkOffsets`, `RebuildUnitAnim`, `ComputeWeaponCycleSeconds`.
Look/edit here when: a unit's walk/attack/cast animation is stuck, mistimed, or plays the wrong
clip; a channeled cast's start/loop/finish phases are off; attack speed (weapon cycle) feels wrong;
an effect or sprite lags behind a moving unit; a unit doesn't re-animate after a def change.
See also: `Game1.Spells.cs` (cast effects + channel buff application), `Render/` (the anim
controller / atlases / flipbook data), `Data/Registries/` (unit anim metadata).

### `Necroking/Game1.Crafting.cs` — resource gathering & table crafting
What lives here: the player-side crafting/gathering glue — finding the nearest foragable, starting a
table-craft job at an environment object, and the foragable pickup / learn-trigger callbacks. Thin
orchestration over the `Game/` foragable + table-craft systems.
Key members: `StartTableCraft`, `FindNearestForagable`, `StartForagableCollection`,
`OnForagablePickedUp`, `OnForagableLearnTrigger`.
Look/edit here when: a foragable won't gather / picks the wrong target; table crafting won't start at
a crafting station; a gather doesn't grant the expected item or skill-learn. (The actual craft
recipe resolution and table mechanics live in `Game/`.)
See also: `World/` (foragable + env-object systems), `Game/` (table-craft system), `Game1.Spells.cs`
(`TryStartPoisonBerries` — a related berry-harvest path).

### `Necroking/Game1.Spells.cs` — player spell cast pipeline, effects, summon/reanimate, projectiles, horde commands
What lives here: the player spell-cast pipeline as orchestrated from `Game1` — executing summon /
reanimate spells (all `SummonTargetReq` / `SummonMode` variants), queuing and ticking delayed zombie
rises, dispatching generic spell effects, blight application, casting cast/summon visual effects,
potion-spell casting, spell-slot flash feedback, built-in (no-path) abilities, horde command/regroup
orders, poison-berry harvest, and spawning + ticking spell projectiles.
Key members: `ExecuteSummonSpell`, `ExecuteSpellEffect`, `QueueReanimRise`, `TickPendingReanimRises`,
`OnSimReanimReady`, `ApplyBlightSpell`, `ApplyBlightBombImpacts`, `SpawnCastEffect`,
`SpawnSummonEffect`, `SpawnFlipbookEffect`, `CastPotionSpell`, `FlashSpellSlot`,
`TryDispatchBuiltinAbility`, `TryCommandHorde`, `TryRegroupHorde`, `ValidatePotionAbilities`,
`TryStartPoisonBerries`, `SpawnSpellProjectile`, `TickPendingProjectiles`, `RemoveCastingBuffAll`.
Look/edit here when: a spell does the wrong thing on cast (summons the wrong unit, raises the wrong
zombie, blight misfires); a reanimate doesn't rise or rises too early/late; a projectile spell flies
wrong / never impacts; the spell-slot doesn't flash; a potion or built-in ability misbehaves; horde
"command here" / "regroup" orders go wrong; poison berries don't start. This is the orchestration
layer — **targeting** lives in `Game/SpellCasting.cs` and **effect resolution** in
`Game/SpellEffectSystem.cs`.
See also: **`docs/spells.md`** (read before adding/changing a spell — explains the three-layer
split), `Game/SpellCasting.cs`, `Game/SpellEffectSystem.cs`, `Game/SpellPenetration.cs`,
`Data/Registries/SpellRegistry.cs`, `Game1.Spells.cs` is paired with the main loop in `Game1.cs`.

### `Necroking/Game1.Render.cs` — top-level Draw flow / render orchestration
What lives here: `Draw(GameTime)` — the master render orchestration that sequences every pass
(ground → world → corpses → units → effects → HUD → menus) into render targets and composites them.
The individual passes live in the sibling `Game1.Render.*` files; this is the conductor.
Key members: `Draw`, the local `DrawWorldLabel` helper.
Look/edit here when: the overall draw order is wrong (something draws over/under what it should); a
render pass is missing or runs at the wrong time; bloom/compositing/render-target sequencing is off;
you need to add a whole new draw pass to the pipeline.
See also: `Game1.Render.World.cs`, `Game1.Render.Units.cs`, `Game1.Render.Corpses.cs`,
`Game1.Render.HUD.cs`, `Render/` (bloom, shadow, atlas, font systems).

### `Necroking/Game1.Render.World.cs` — ground, terrain, environment & world-layer rendering
What lives here: the world-layer draw passes — ground tiles (CPU + shader paths), roads, walls,
ground-layer objects, projectiles (SDR + HDR), filtered effects, impact effects, damage numbers, and
soul-orb pickups.
Key members: `DrawGround`, `DrawGroundShader`, `DrawRoads`, `DrawWalls`, `DrawGroundLayerObjects`,
`DrawProjectiles`, `DrawProjectilesHdr`, `DrawEffectsFiltered`, `SpawnImpactEffects`,
`DrawDamageNumbers`, `DrawSoulOrbs`.
Look/edit here when: the ground/terrain renders wrong (tiles, blending, the ground shader); roads or
walls look off; projectiles, impact effects, floating damage numbers, or soul orbs draw incorrectly.
See also: `Game1.Render.cs` (pass ordering), `World/` (wall/road/env data + corruption), `Render/`
(ground shader, effect systems).

### `Necroking/Game1.Render.Units.cs` — unit sprite rendering & hover highlights
What lives here: drawing units and environment objects as sprites — the per-unit draw, env-object
draw, dissolving-tree effect, idle-sprite preview draw, low-level sprite-frame blitting (incl. wading
and outline variants), and the entire hover-highlight / cursor-pick system (object marker hit-testing,
ground diamond markers, outline strokes).
Key members: `DrawUnitsAndObjects`, `DrawSingleUnit`, `DrawSingleEnvObject`, `DrawDissolvingTree`,
`DrawUnitIdleSprite`, `DrawSpriteFrame`, `DrawWadingSpriteFrame`, `DrawSpriteOutline`,
`DrawUnitPulsingOutline`, `DrawHoverHighlights`, `DrawHoverGroundMarkers`, `PickHoveredObject`,
`CursorInObjectMarker`, `HoveredObjectIsBuilding`, `DrawGroundDiamondCorners`.
Look/edit here when: a unit or environment object draws wrong (wrong frame, facing, scale, tint,
outline); the wading/water sprite effect is off; hover highlighting picks the wrong object or draws
the marker in the wrong place; the building-vs-object hover footprint is off.
See also: `Game1.Render.Corpses.cs` (corpse/morph draw), `Render/` (atlases, sprite frames),
`World/` (placed-object runtime + collision footprints).

### `Necroking/Game1.Render.Corpses.cs` — corpse, body-bag & carried-visual rendering
What lives here: drawing corpses and everything carried — corpse sprites at a death frame, the
reanimation morph effect, body-bags (on-ground, carried, bagging/build progress bars), carried
corpses/body-bags/visuals, and the death-frame centroid cache used to anchor carried corpses
(bake / load / save).
Key members: `DrawCorpses`, `DrawReanimMorph`, `DrawCorpseSpriteAt`, `TryGetCorpseDeathFrame`,
`DrawBaggedCorpse`, `DrawCarriedBodyBag`, `DrawCarriedCorpse`, `DrawCarriedVisual`,
`DrawBaggingProgressBar`, `DrawBuildProgressBar`, `BakeAllCorpseCentroids`, `LoadPersistedCentroids`,
`SavePersistedCentroids`, `AtlasIdxForTexture`.
Look/edit here when: a corpse draws at the wrong frame/orientation; the reanimation morph looks
wrong; a carried corpse or body-bag floats off the carrier (centroid issue); bagging/build progress
bars render wrong. Corpse death-frame centroids are baked to `data/frame_centroids.json` (see the
`--bake-centroids` launch arg in `Program.cs`).
See also: `Game1.Render.Units.cs` (shared sprite-frame helpers), `Game1.Spells.cs` (reanimate
queuing), `Core/` (corpse state).

### `Necroking/Game1.Render.HUD.cs` — HUD, spellbar, menus, toasts & debug overlays
What lives here: all on-screen UI drawing — the HUD/spellbar entry (`DrawHUD`), skill-learn toasts,
the aggression bar + tooltip, core-menu toggle buttons, the main menu / pause menu / scenario list /
game-over screens, generic menu-button + panel + backdrop helpers, rounded text helpers, and the
debug overlays (weapon-attach, wind, horde rings, unit-info, world-hover info).
Key members: `DrawHUD`, `DrawMainMenu`, `DrawPauseMenu`, `DrawScenarioList`, `DrawGameOver`,
`DrawAggressionBar`, `DrawAggressionTooltip`, `ToggleCoreMenu`, `BuildMenuOpenMask`,
`DrawSkillLearnToasts`, `UpdateSkillLearnToasts`, `DrawMenuButton`, `DrawMenuButtonAt`, `DrawPanel`,
`DrawMenuBackdrop`, `DrawText`, `DrawTextRounded`, `DrawWorldHoverInfo`, `DrawHordeDebug`,
`DrawUnitInfoDebug`, `DrawWindDebug`, `DrawWeaponAttachDebug`.
Look/edit here when: the spellbar or HUD draws incorrectly; a menu/pause/scenario-list/game-over
screen looks wrong or its buttons mis-hit; the aggression bar/tooltip is off; skill-learn toasts
mistime or mislay out; a debug overlay (horde rings, wind, world-hover) renders wrong; remember to
round text positions to integer pixels (CLAUDE.md → UI Text Rendering).
See also: `Render/HUDRenderer` and the spellbar renderer (the actual spellbar widget), `UI/`
(inventory/grimoire/skill-book overlays), `Game1.Render.cs` (when HUD draws in the pipeline).

### `Necroking/Game1.Dev.cs` — dev-server command dispatch (`ExecuteDevCommand`) — READ ONLY
What lives here: the body of `ExecuteDevCommand(Necroking.Dev.DevCommand c)` — a big `switch (c.Cmd)`
dispatching every dev-control verb (`ping`, `state`, `setting`, `spawn`, `camera`, `speed`, `pause`,
`menu`, `panel`, `screenshot`, units/combat/batch commands, …) that the dev preview server forwards.
Runs on the game main thread (drained from `Update` in `Game1.cs`), so it touches `_sim`, `_camera`,
`_gameData`, panels directly. **Another developer is actively editing this file — treat it as READ
ONLY; do not edit it.**
Key members: `ExecuteDevCommand` (single method, one `case` per command).
Look/edit here when: you need to find which dev command does what, or understand how a `window.dev(...)`
verb is handled. To *add* a dev command, the normal flow is one new `case` here (see CLAUDE.md → Dev
Control Server), but coordinate first since this file is being edited concurrently.
See also: `Necroking/Dev/DevServer.cs` + `DevCommand` (HTTP listener feeding this), `tools/devserver.py`
(supervisor), `docs/devpreview.md`, CLAUDE.md → "Dev Control Server". `Necroking/Game1.DevData.cs`
holds the `add_data` command's body (`DevAddData`).

### `Necroking/Game1.DevData.cs` — `add_data` dev command: inject registry entries from JSON at runtime
What lives here: `DevAddData(DevCommand c)` — the body of the `add_data`/`add_json` dev verb. Takes JSON
via `opts.json` (a single entry object, an array of entries, or a whole `{"<key>":[...]}` data-file
object) plus an optional `arg[0]` registry kind (spell/unit/item/buff/weapon/armor/shield/potion/
flipbook), deserializes each entry through the matching `_gameData` registry's `RegistryBase.AddFromJson`
(honoring per-registry converters/overrides), and adds it to the LIVE registry — **runtime only, never
written to disk**. If the matching editor panel is open it selects the freshest entry (via
`SelectEditorEntry`); `opts.open=true` switches to that editor first. The editors are immediate-mode so
their lists pick up the new entry with no extra refresh.
Key members: `DevAddData`.
Look/edit here when: you want to add a new data kind to `add_data`, change how injected entries are
surfaced in an editor, or debug why a runtime-injected spell/unit isn't appearing. The reusable
"deserialize one entry + add" primitive is `RegistryBase<TDef>.AddFromJson` in
`Data/Registries/RegistryBase.cs`.
See also: `Game1.Dev.cs` (the `case "add_data"` dispatch), `Data/Registries/RegistryBase.cs`
(`AddFromJson`), `Data/GameData.cs` (the registry container), `Editor/` (`DevSelect` on the editors).

### `Necroking/Program.cs` — entry point & launch-arg parsing
What lives here: `static void Main` (sets CWD to the exe dir, parses launch args, detects the data
root via `GamePaths.DetectRoot`, constructs and runs `Game1`, writes `log/crash.log` on an unhandled
exception) and the `LaunchArgs` static holder (`--scenario`, `--headless`, `--autostart`,
`--bake-centroids`, `--no-vsync`, `--devserver <port>`, `--resolution`, `--bgcolor`, `--unit`) plus
the process-start timing stopwatches used for startup profiling.
Key members: `Program.Main`, `Program.ProcessStartStopwatch`, `Program.ProcessStartTime`,
`LaunchArgs.Parse`, and the `LaunchArgs` flags.
Look/edit here when: adding/changing a command-line flag; the game can't find `data/`/`assets/` on
launch; startup timing/profiling; the `--devserver`, `--scenario`, or `--bake-centroids` entry paths.
See also: `Game1.cs` (`Initialize`/`LoadContent` consume `LaunchArgs`), `Core/GamePaths.cs`,
`docs/testing-scenarios.md` (the `--scenario` harness).

## Related subsystems (where the deep, non-orchestration logic lives)

The `Game1.*` partials are glue; the heavy logic lives in sibling subsystem folders, most of which are
**not yet documented in this map** (see [overview.md](overview.md) — research and add a
`reference/<area>.md` when you route into one):

- **`Necroking/Game/`** — gameplay systems: spell targeting (`SpellCasting.cs`), spell effect
  resolution (`SpellEffectSystem.cs`, `SpellPenetration.cs`), crafting/table-craft, inventory, horde
  logic. *The real spell behavior lives here, not in `Game1.Spells.cs`.* See also `docs/spells.md`.
- **`Necroking/Render/`** — rendering subsystems: atlases & sprite frames, the ground shader, bloom,
  shadows, font manager, `HUDRenderer` and the spellbar widget, effect systems. *The `Game1.Render.*`
  files only sequence and call into these.*
- **`Necroking/World/`** — environment objects, foragables, walls, roads, ground corruption, placed-
  object collision footprints.
- **`Necroking/Core/`** — `Simulation`, unit/corpse state, `Vec2`, `DebugLog`, foundational types.
- **`Necroking/Data/`** + **`Necroking/Data/Registries/`** — the game-data model and JSON registries
  (`SpellRegistry`, units, items, potions, buffs, weapons, armor). Edit the JSON via the
  `edit-game-data` skill.
- **`Necroking/Dev/`** — `DevServer` / `DevCommand` HTTP plumbing feeding `ExecuteDevCommand`.
- Other folders (`AI/`, `Movement/`, `Spatial/`, `Algorithm/`, `GameSystems/`, `Editor/`,
  `Scenario/`, `UI/`) — see [overview.md](overview.md) for tentative responsibilities; none documented
  in detail yet.
