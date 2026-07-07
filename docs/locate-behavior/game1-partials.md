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
See also: `Necroking/Game1.Dev.cs` (the `ExecuteDevCommand` body), `Game/Simulation.cs`,
`Render/` (renderers constructed here), `Editor/` (editors), `data/maps/` (map JSON — world content
lives in the map, not in startup spawns; see CLAUDE.md), and **`Necroking/Game/GameSession.cs`**
(the per-game state owner — see next).

#### Per-frame input capture + window-focus gate (top of `Game1.Update`)
The **single central place raw mouse/keyboard is read each frame** is the top of `Game1.Update`
(`Necroking/Game1.cs`, ~line 2627): it calls `Mouse.GetState()`/`Keyboard.GetState()` once, then
funnels them through **`_input.Capture(mouse, prevMouse, kb, prevKb)`** — `Necroking/Core/InputState.cs`.
`InputState.Capture` is where **click edges are computed**: `LeftPressed`/`RightPressed`
(`Pressed` now && `Released` last frame), `LeftDown`/`LeftReleased`, `ScrollDelta`, and where all
per-frame consumption flags (`MouseOverUI`, `_mouseConsumed`, …) reset. Every system reads `_input.*`
instead of calling `Mouse.GetState()` directly.
**The window-focus gate already exists here** (`Game1.cs` ~lines 2630-2684) using MonoGame's
`Game.IsActive`:
- `bool unfocused = !IsActive && _activeScenario == null && LaunchArgs.Scenario == null && !LaunchArgs.Headless;`
  (scenario/headless runs are exempt so the test harness isn't frozen).
- **Refocus-click protection is the key trick:** when `unfocused && !keepRunningUnfocused` the method
  sets `_prevMouse = mouse; _prevKb = kb;` and early-returns *before* `Capture` — so next frame the
  click that refocused the window is already "held" in prevMouse and its rising edge never fires.
- `keepRunningUnfocused` (dev server or `Settings.General.RunWhenUnfocused`) instead feeds a **neutral**
  MouseState (all buttons `Released`, cursor centred) into `Capture` so background clicks/keys aren't
  consumed while the sim keeps ticking.
- A parallel `cursorOutside` branch (focused but cursor outside the viewport) strips mouse buttons via a
  no-buttons `MouseState` so out-of-bounds clicks can't command the world.
Look/edit here when: a click made while the window is unfocused registers in-game; you need to gate
input on window focus; "run when unfocused" behaviour; where click pressed-this-frame edges come from.

#### GameClock — time & pause authority (`Necroking/Core/GameClock.cs`)
**The single place that answers "what time is flowing, how fast, and what is paused".** `Game1` owns
`_clock` and drives it two-phase from `Update`: `BeginFrame(rawDt)` at the top-of-frame dt derivation
(before the MainMenu early-returns, so menus keep accruing VisualTime for shader phases), then
`GateWorld(worldSuspended: EditorActive)` right before the sim gate (after all pause/menu input, so
pausing or entering an editor freezes the world the *same* frame). Everything else only reads.

Domains — pick by what the consumer IS, not where it runs:
- **`RawDt`** — unclamped wall delta. FPS/perf readouts, real-time decays (slot flash, toasts).
- **`RealDt`** — RawDt clamped to 1/20. Real-time UI that shouldn't jump after a hitch.
- **`VisualDt` / `VisualTime`** — 0 while paused, × TimeScale. Presentation only: shader/wind/pulse
  phase drivers, damage numbers. VisualTime is **never reset** (phase continuity) — it is NOT the
  world's age.
- **`WorldDt` / `WorldRunning`** — VisualDt, additionally 0 while a full-screen editor is open or on
  frames that never reach the gate. **All gameplay-mutating code consumes WorldDt** — the sim gate is
  `if (_clock.WorldRunning)`, and `UpdateAnimations`/death-fog/Draw-pass corpse anims take WorldDt.
  Feeding gameplay from VisualDt is the "corruption spreads in the map editor" bug class.
- **World age** — canonical on `Simulation.GameTime` (reset by `Simulation.Init` each game; the sim
  stays self-contained for headless scenarios). Dev status JSON `gameTime` reports this;
  `visualTime`/`worldRunning`/`pauseSources` are also in the payload.

Pausing is **source-flagged** (`GameClock.PauseSource`: `User` = ESC/Space/HUD, `Inspect` = press-O
unit sheet, `Dev` = dev server): `Pause(s)`/`Resume(s)` release per-source (the inspect pause
survives unrelated resumes); menu/editor buttons force-run via `ClearAllPauses()`. `StartGame`/
`StartScenario` call `_clock.OnWorldStart()` (clear pauses, TimeScale→1). Legacy fields
`_paused`/`_gameTime`/`_timeScale`/`_rawDt`/`_frameDt` on `Game1` are read-forwarders onto the clock.

Deliberately OUTSIDE the clock: the net loop (`UpdateNetwork`, wall clock, must run while
paused/in menus — `Net/README.md`), dev-server drain, editor cursor blinks, the editor-mode scenario
tick's fixed 1/60 step.
Look/edit here when: something advances while paused / in an editor (wrong domain — switch it to
WorldDt); a new update call needs a dt (pick a domain above); adding a new thing that can pause the
game (add a PauseSource); time scale or world-time reporting behaves wrong.

### `Necroking/Game/GameSession.cs` — per-game world-state owner (recreated each map load)
What lives here: `GameSession` — a single object that **owns the per-game world systems** (the ones
rebuilt from scratch on every map load). `Game1` holds one `_session` and exposes its members through
**forwarding properties** (`internal EnvironmentSystem _envSystem => _session.Env;`), so the hundreds
of existing `_envSystem.Foo()` / `_g._envSystem` call sites are unchanged. `StartGame` does
`_session.Dispose(); _session = new GameSession()`: `Dispose()` frees the old map's GPU/native
resources (ground/env textures), and the reassignment drops every reference to the previous map's
managed state — so **nothing carries over between maps and per-game memory can't leak or bleed**.
This replaced a scattered per-system `ClearObjects`/`ClearDefs`/`ClearTypes` dance in `StartGame`;
that pattern was the source of a whole class of reload leaks (wall defs were *never* cleared → each
reload stacked another full copy → OOM on a map with a large walls array; flipbooks/textures were
similar undisposed-on-reload misses).
Key rule: **a new per-game resource must be owned by `GameSession` (or disposed on reload) or it will
leak.** Migration is incremental (one system at a time: Game1 field → GameSession field + forwarding
property + dispose wiring, build green each step). Distinguish **per-game** (world systems, sim,
per-map textures/RTs) from **app-lifetime** (renderers, editors, `_gameData`, atlases, fonts — reused
across games, must NOT be recreated).
Look/edit here when: adding a per-game system/resource and deciding where it lives; a resource leaks
across map reloads or bleeds state from the previous map; the world renders blank/stale after a
reload (a system not wired to the fresh session). Current membership + the remaining migration
checklist + the app-vs-game classification live in **`todos/gamesession-migration.md`**.
`GameSession.Census()` reports a live per-type count of every collection the session owns (units by
def/faction, env objects by def, corpses/projectiles/wall-defs/… — the accumulation canaries); it's
the single place to extend when a new per-game collection is added, surfaced by the `census` dev
command.
See also: `Game1.cs` (`StartGame` recreates `_session`; forwarding properties declared near the
system fields), `World/` (the owned systems), `Necroking/Game1.Dev.cs` (`mem` dev command — managed
heap + process memory behind a forced compacting GC — and `census`, both for spotting reload leaks).

### `Necroking/Game1.Animation.cs` — per-frame animation, cast-phase & attack-anim updates
What lives here: the per-frame animation tick — advancing sprite flipbooks for every unit, driving
channeled/cast animation state machines, table-craft channel buffs, and keeping spawned effects and
wading sink-offsets glued to their moving owners. Called from `Update`.
Key members: `UpdateAnimations`, `UpdateChanneledCast`, `UpdateTableChannelBuff`,
`UpdateEffectSpawnPositions`, `UpdateWadingSinkOffsets`, `RebuildUnitAnim`, `ComputeWeaponCycleSeconds`.
**This file is where a cast ends / `_pendingCastAnim` is cleared** (set in `Game1.Spells.cs`): the
single-shot effect fires on the Spell1 `JustHitEffectFrame` (or when the necromancer leaves Spell1)
and channeled casts run their Start→Loop→Finish machine in `UpdateChanneledCast` (effect fires at end
of loop). Every terminal path clears `_pendingCastAnim = null` (+ `RemoveCastingBuffAll`). So the cast
window is exactly "`DispatchSpellCast` sets it → `UpdateAnimations`/`UpdateChanneledCast` clears it."
Look/edit here when: a unit's walk/attack/cast animation is stuck, mistimed, or plays the wrong
clip; a channeled cast's start/loop/finish phases are off; attack speed (weapon cycle) feels wrong;
an effect or sprite lags behind a moving unit; a unit doesn't re-animate after a def change; you need
to run logic on cast-start vs cast-end (gate/release something for the cast duration).
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

### `Necroking/Game1.Spells.cs` — player spell cast pipeline, deferred queues, horde commands
What lives here: the player spell-cast pipeline as orchestrated from `Game1` — dispatching casts,
queuing and ticking delayed zombie rises and staggered multi-shot projectiles, deferred blight-bomb
impacts, casting cast/summon visual effects, potion-spell casting, spell-slot flash feedback,
built-in (no-path) abilities, horde command/regroup orders, and poison-berry harvest.
Key members: `ExecuteSpellEffect` (thin glue → `SpellEffectSystem.Execute`), `QueueReanimRise`,
`TickPendingReanimRises`, `OnSimReanimReady`, `ApplyBlightBombImpacts`, `SpawnCastEffect`,
`SpawnSummonEffect`, `SpawnFlipbookEffect`, `CastPotionSpell`, `FlashSpellSlot`,
`TryDispatchBuiltinAbility`, `TryCommandHorde`, `TryRegroupHorde`, `ValidatePotionAbilities`,
`TryStartPoisonBerries`, `TickPendingProjectiles`, `RemoveCastingBuffAll`.
NOTE (2026-07): `ExecuteSummonSpell`, `SpawnSpellProjectile` (→ `SpawnProjectile`), and
`ApplyBlightSpell` (→ `ApplyBlight`) moved into `Game/SpellEffectSystem.cs` — a static class whose
`Execute(spell, game, casterIdx, target, slot)` takes `Game1` directly and owns ALL category logic.
Look/edit here when: a reanimate doesn't rise or rises too early/late; staggered multi-shot timing
is off; the spell-slot doesn't flash; a potion or built-in ability misbehaves; horde
"command here" / "regroup" orders go wrong; poison berries don't start. For a spell doing the wrong
thing on cast (wrong summon, blight misfire, projectile flies wrong), go to
`Game/SpellEffectSystem.cs`; **targeting** lives in `Game/SpellCasting.cs`.
**Cast begin + "is a cast in progress" state:** `DispatchSpellCast` is the single spell-bar/dev-`cast`
entry. After a successful `SpellCaster.TryStartSpellCast`, it sets the field **`_pendingCastAnim`**
(a `PendingCastAnim?` declared in `Game1.cs`, struct defined there too — carries `SpellID`, `Target`,
`Slot`, `CastAnim`, `CastTime`, `ChannelPhase`, `Executed`). **`_pendingCastAnim != null` is the
canonical "the necromancer is mid-cast" flag** and it self-guards re-entry (`DispatchSpellCast` bails
early while one is pending). Two flavors: channeled (`CastAnim` ImbueGround/Raise/ImbueTable → a
Start/Loop/Finish machine) and deferred single-shot (has a `CastingBuffID`, fires on the Spell1
action frame). Note: instant spells with no `CastingBuffID` and no channel execute immediately and
never set `_pendingCastAnim` — there is no cast-duration window to lock against for those. The
**necromancer already faces the target and stops during a channel** (facing pinned each frame in
`UpdateChanneledCast`), but plain WASD movement is NOT gated by a cast today. A separate
`_channelingSlot` (int, `Game1.cs`) tracks hold-to-channel beam/drain spells (set in
`SpellEffectSystem.Execute`'s Beam/Drain cases, released in `Game1.cs` Update when the slot key is
let go via `Lightning.CancelBeamsForCaster`/`CancelDrainsForCaster`) — unrelated to
`_pendingCastAnim`.
**Cast state census (updated 2026-07-07 — the pipeline is now caster-agnostic):**
`Game1._pendingSpell` is the PLAYER's `PendingSpellCast` (targeting results:
`TargetCorpseID`/`TargetUnitID`/`SummonUnitID`) — it must survive multi-second cast
anims/channels, so it stays a Game1 field, but `SpellEffectSystem.Execute` now takes the
pending record as an explicit parameter instead of reading `game._pendingSpell`; AI casts
use the `Game1._aiPendingSpell` scratch (reset per cast, consumed immediately in
`DrainAISpellCasts`). Mana + per-spell cooldowns are behind
`Movement.ICasterResources` — implemented by `NecromancerState` (player) and
`Movement.UnitCasterResources` (AI units: `Unit.Mana` + lazily-allocated
`Unit.SpellCooldowns` dict). `_pendingProjectiles` groups carry `CasterUid` (multi-shot
follows the caster's hand, player or AI; dropped if the caster dies).
Still player-only: `_pendingCastAnim` + `TickCastPlant`/`SetNecromancerCasting` (cast
anims/plant), `_channelingSlot` (key-hold channel release — AI channels use
`Unit.ChannelTimer` instead, see ai.md "CasterUnit archetype" pipeline note), slot flash,
cast-fail texts, `PlayerEvents.Tally`, horde caps (`caster is NecromancerState` gate in
`TryStartSpellCast`). AI cast entry: `Simulation.PendingAISpellCasts` →
`Game1.DrainAISpellCasts` (called right after `_sim.Tick` in Update).
There is no charge-up or combo system; the only wind-up mechanisms are the cast plant +
`CastTime` and the channel Start/Loop/Finish machine.
See also: **`docs/spells.md`** (read before adding/changing a spell — explains the three-layer
split), `Game/SpellCasting.cs`, `Game/SpellEffectSystem.cs`, `Game/SpellPenetration.cs`,
`Data/Registries/SpellRegistry.cs`, `Game1.Spells.cs` is paired with the main loop in `Game1.cs`.

> **STALE NAMING:** the `Game1.Render.*` render partials described below were **extracted
> into a separate `GameRenderer` class** (`Necroking/GameRenderer.{cs,Draw,World,Units,Corpses,Hud}.cs`)
> that holds a back-reference `_g` to `Game1`. The responsibilities map roughly:
> `Game1.Render.cs`→`GameRenderer.Draw.cs`, `Game1.Render.World.cs`→`GameRenderer.World.cs`,
> `Game1.Render.Units.cs`→`GameRenderer.Units.cs`, `Game1.Render.Corpses.cs`→`GameRenderer.Corpses.cs`,
> `Game1.Render.HUD.cs`→`GameRenderer.Hud.cs`. When editing rendering, edit the `GameRenderer.*`
> files. (Descriptions below still hold for *what* each pass does.)

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
**Pause-menu pitfall:** `DrawPauseMenu` (in `GameRenderer.Hud.cs`) draws the buttons, but the click
hit-testing lives in `Game1.cs` `Update` ("Pause menu button clicks" block) with **independently
hardcoded geometry** (`pauseBtnCount`/`btnW2`/`btnH2`/`boxY2` must mirror `btnCount`/`btnW`/`btnH`/
`boxY` in the draw). Adding/removing/reordering a pause-menu button means updating BOTH files in
lockstep or clicks land on the wrong button. Submenus opened from pause (e.g. Settings) follow the
`MenuState.Settings` + `Editor/SettingsWindow.cs` pattern: a window class driven by the shared
`EditorBase` (`_editorUi`) widgets, `WantsClose` flag polled in `Game1.Update` to return to
`MenuState.PauseMenu`.
See also: `UI/HUDRenderer` and the spellbar renderer (the actual spellbar widget), `UI/`
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
`<area>.md` when you route into one):

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
- Other folders (`AI/`, `Movement/`, `Spatial/`, `Algorithm/`, `Editor/`,
  `Scenario/`, `UI/`) — see [overview.md](overview.md) for tentative responsibilities; none documented
  in detail yet.
