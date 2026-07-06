# World — environment objects, foragables, world placement

Everything that lives *in the map* but isn't a unit or a corpse: trees, props,
buildings, and **foragables** (mushrooms, branches). The env-object store is the
single source of truth; foragable pickup, respawn, and world spawning all go
through it. Map content is authored in `data/maps/*.json` + `data/env_defs.json`,
not spawned from startup code (see CLAUDE.md "Map Content Lives In The Map").

## Files

### `Necroking/World/EnvironmentSystem.cs`  ← the env-object store (defs + placed instances)
Parallel arrays: `_defs` (`EnvironmentObjectDef`, the types) and `_objects`
(`PlacedObject`, world instances) + `_objectRuntime` (`PlacedObjectRuntime`,
mutable per-instance state: `Collected`, `RespawnTimer`, `HP`, `StoredAmount`, …).
Key API:
- **`AddObject(ushort defIndex, float x, float y, scale, seed)`** → returns the new
  object index. **This is how you spawn a world object at runtime** (e.g. spit a
  mushroom back onto the ground). Fires `OnCollisionsDirty` if the def has collision.
- **`FindDef(string id)`** → def index for an env-def id (e.g. `"deathcap"` /
  whatever the mushroom def's `Id` is). Returns -1 if missing. Cast to `ushort` for `AddObject`.
- **`RemoveObject(index)`** — hard remove (shifts arrays; **invalidates indices**).
  `DestroyObject(objIdx)` — soft (`Alive=false`, preserves indices).
- **`CollectForagable(int objIdx)`** → returns the `ForagableType` string and marks
  the runtime `Collected=true` + starts `RespawnTimer=def.RespawnTime`. **This is the
  canonical "consume a foragable" call** — a boar eating a mushroom should call this
  (it handles collision-dirty + respawn bookkeeping), NOT `RemoveObject`.
- **`UpdateForagables(dt)`** — ticks respawn timers; un-collects when timer elapses
  (`RespawnTime <= 0` = single-use, stays gone).
- `ObjectCount`, `Objects` (`IReadOnlyList<PlacedObject>`), `Defs`
  (`IReadOnlyList<EnvironmentObjectDef>`), `IsObjectVisible(objIdx)` (false when
  collected/dead/hidden — filter on this before treating an object as present).
- **Foragable def fields** on `EnvironmentObjectDef`: `IsForagable` (bool),
  `ForagableType` (resource string e.g. `"Mushroom"`), `RespawnTime` (s),
  `ScaleMin/ScaleMax`. Data lives in `data/env_defs.json`.

### `Necroking/Game/ForagableSystem.cs`  ← the PLAYER's foragable pickup mechanic
Right-click / auto-pickup arc-flight that pulls a nearby foragable into the
necromancer's inventory. `FindNearest(pos, maxDist)` (scans visible `IsForagable`
objects), `StartCollection(objIdx)` (calls `_env.CollectForagable`, caches the def
texture, adds an in-flight arc), `Update` (arc tick → `Inventory.AddItem` + dust puff +
pickup sound + learn-trigger on landing), `Draw` (renders the flying sprites),
`TickAutoPickup`. Wired via `Bind(...)` from `Game1.LoadContent` — triggers:
right-click (`Game1.WorldClicks.cs` `HandleWorldRightClick` step 1), auto-pickup
(`Game1.cs` update loop via `TickAutoPickup`), crafting forwarding
(`Game1.Crafting.cs`). **This is the player path only** — a boar eating
a mushroom does NOT go through here; it calls `env.CollectForagable` directly. But
`FindNearest`'s scan loop is the exact template for "find nearest mushroom near me."
**Rendering split**: the *in-flight arc* is drawn by `ForagableSystem.Draw()` inside the
`ForagablesDamageNumbers` CustomPass (`GameRenderer.Pipeline.cs`, top-of-scene alpha
pass); the *mushroom/log sitting in the world* (plus proximity wiggle, scale pulse, and
mouse-hover brighten) is drawn by `GameRenderer.Units.cs` `DrawSingleEnvObject` in the
World SpriteQueuePass, gated by `IsObjectVisible`. The wiggle/hover effects are
visual-only — pickup selection is proximity-based (`FindNearest` from the necromancer),
not mouse-based.

### Other World/ files (context, not needed for this task)
`Pathfinder.cs` (A*/steering used via `AIContext.Pathfinder`), `FlowField.cs`,
`EnvSpatialIndex.cs` (spatial index over env objects — faster than the linear scan
if boars/mushrooms get numerous; ORCA-only consumer), `WallSystem.cs`, `RoadSystem.cs`,
`TileGrid.cs`, `GroundSystem.cs`.

### World queries — `_sim.Query` (WorldQuery, 2026-07-06)
"Nearest X / X under cursor / all X in radius" over units, env objects, and corpses has
ONE canonical home: **`Necroking/Game/WorldQuery.cs`**, owned by `Simulation`
(`_sim.Query`). Struct-generic filters (`EnvForagables`, `EnvWorkerHomes`,
`EnvByDefIndex`, `CorpseExclude.Free`, caller-side structs for odd gates). Bounded unit
queries ride the per-tick quadtree; UI/paused picks use the linear methods (quadtree is
stale outside `Simulation.Tick`). Don't write new ad-hoc `bestSq` scans — and when
touching the remaining unmigrated ones (WorkerSystem finds, CorpseInteractionManager,
SpellCasting/Projectile corpse scans, VillageThreat, BoarForageAI), migrate them here.

## Runtime env-object spawn census & the map-save pollution loop

`MapEditorWindow.SaveMap()` writes **every** live object in `_envSystem.Objects` into the
map's `placedObjects` array — `PlacedObject` has no "who spawned me" flag — so any
gameplay-spawned object present at save time gets baked into the map JSON and re-loaded
next run, compounding. The non-editor `AddObject` call sites (the census):
- `Game1.Zones.cs` `TryPlaceSpaced` (zone foragable spawns via `TrySpawnZoneForagable`;
  note it first tries `TryReviveForagableInRect`, which reuses an existing object)
- `Game1.Villages.cs` `PlaceStructure` (village structures/graves stamped **every load**
  from `assets/maps/<map>_villages.json` — double-compounds if also saved into placedObjects)
- `Game/Simulation.cs` boar belly spit in `RemoveDeadUnits`
- `UI/BuildingMenuUI.cs` player-built buildings (also `AddObjectAsBlueprint` path)
- `Game1.Dev.cs` dev commands; `Scenario/` (never saved, irrelevant)
Units do NOT have this problem: `SaveMap` writes only the editor's own `_placedUnits`
list (set from map JSON at load, edited only by the Units tab) — live sim units are never
merged back. A "gameplay-spawned, don't save" flag belongs on the `PlacedObject` struct
(`EnvironmentSystem.cs`) + an `AddObject` parameter; filter in `SaveMap`'s placedObjects
loop. Editor-placed callers: `MapData.Load`, `MapEditorWindow` paint/single/procgen + its
undo-redo re-adds (`UndoObjectRemove`/batch structs call `Env.AddObject`).

## Death → corpse → drop path (cross-area: Simulation)

- **`Necroking/Game/Simulation.cs` `RemoveDeadUnits()`** (`~line 3664`) — the per-unit
  death handler, called once per tick (`Simulation.cs ~line 685`). For each dead unit it
  builds a corpse (`MakeCorpseFromUnit(i)`, `~line 203`), handles reanim-on-death /
  physics-velocity transfer, then `RemoveUnitTracked(i)` (swap-pop, reverse loop). **This
  is where "when a boar dies, spit out its belly mushrooms" hooks in** — right before
  `RemoveUnitTracked(i)`, while `_units[i].Position` and the belly counter are still valid.
  Env spawning is available here (`_envSystem` field on `Simulation`).
- There is **no generic item-drop-on-death system** to reuse — corpses are the only
  death output today. Spitting mushrooms is new logic; put it in a small helper called
  from `RemoveDeadUnits` (mirror `BroadcastRatPanicOnDeath(i)`, which is exactly this
  "do something extra on death by archetype, before the swap-pop" pattern).

## Spread-out / anti-stack placement (cross-area: HordeSystem)

The existing **anti-stacking packing** in the codebase is the Vogel sunflower spiral in
`Necroking/Game/HordeSystem.cs` `ComputeSlotPosition(slotIndex)` (`~line 517`):
```
r = SlotSpacing * sqrt(slotIndex + 0.5)
angle = facing + slotIndex * GoldenAngle   // GoldenAngle = 2.39996322972865
pos = center + (cos angle, sin angle) * r
```
This gives even, non-stacking radial packing with constant density — the closest thing to
"hex packing with noise" already in the project, and the right precedent to copy. Add small
random jitter to `r`/`angle` for the "random noise" the feature asks for. There is **no
existing hex-grid or Poisson helper** — a new static utility (see below) is warranted.

## Where new code goes (boar eats mushrooms; spits them on death)

### (a) Wander + seek + eat AI  → new `Necroking/AI/BoarForageAI.cs` (static, like `CorpseEatAI`)
`CorpseEatAI.cs` is the near-exact template: a static `Update(Simulation sim, float dt)`
that scans units, gates by tag/faction, runs a per-unit eat timer, and consumes a target.
Copy it for mushrooms:
- Gate: `def.Tags.Contains("boar")` + `Faction.Undead` (player's horde).
- Find nearest **visible foragable** whose `def.ForagableType == "Mushroom"` (reuse the
  `ForagableSystem.FindNearest` scan shape, or `EnvSpatialIndex`), within an eat radius.
- Wander when none in range; **steer toward** a mushroom when one is found. Steering isn't
  in `CorpseEatAI` (it's passive) — for active seek use the move primitives in
  `Necroking/AI/SubroutineSteps.cs` (`MoveToward(ref ctx, target, speed)`), which means the
  boar needs its own **archetype handler** (`IArchetypeHandler`) rather than a passive
  static sweep, so it gets an `AIContext` with `Pathfinder`/`EnvSystem`. See `ai.md` for
  registering a new archetype (`ArchetypeRegistry` id + `FromName` case + `Game1.cs ~line
  718` registration). On arrival call `sim`/`env.CollectForagable(objIdx)` and increment the
  belly. **Decide up front**: passive-only (static sweep, no seek — ship fast, like
  CorpseEat's documented scope choice) vs. active seek (new archetype handler). Active seek
  is what the feature asks for → new archetype handler is the right home.
- Wire the per-tick call next to `AI.CorpseEatAI.Update(this, dt)` (`Simulation.cs ~line
  680`) only if you go the passive-static route; an archetype handler is dispatched
  automatically by the unit loop and needs no manual wiring.

### (b) Per-unit belly storage → new field(s) on `Necroking/Movement/UnitModel.cs`
Per-unit state is Struct-of-Arrays in `UnitModel.cs` (this is where `CorpsesEaten` /
`CorpseEatTimer` / `CorpseEatTargetID` live). Add a `byte BellyMushrooms;` counter (mirror
`CorpsesEaten`). A plain count is enough if all belly mushrooms are the same def; if boars
can eat different foragable types and must spit back the exact types, store a small
per-unit list keyed by unit id in a side dictionary on the boar system (SoA structs can't
hold a `List<>` cheaply) — a count + a single def id covers the common case.

### (c) On-death spit-out + spread placement
- **Hook**: new helper `SpitBellyMushroomsOnDeath(int deadIdx)` in `Simulation.cs`, called
  from `RemoveDeadUnits()` right before `RemoveUnitTracked(i)` (beside `BroadcastRatPanicOnDeath`).
  It reads the belly count, resolves the mushroom def index via `_envSystem.FindDef("<mushroom
  id>")`, and calls `_envSystem.AddObject(defIdx, x, y, scale)` once per stored mushroom at
  spread positions around `_units[deadIdx].Position`.
- **Spread math**: new static utility, e.g. `Necroking/Algorithm/ScatterPacking.cs` (the
  `Algorithm/` folder is the home for standalone algorithms) — a
  `IEnumerable<Vec2> HexPack(Vec2 center, int count, float spacing, System.Random rng, float
  jitter)` that lays points on a hex/axial ring pattern (or the Vogel spiral from
  `HordeSystem.ComputeSlotPosition`) and adds `rng`-scaled noise so mushrooms don't stack.
  Keep it caller-agnostic (returns positions; caller spawns). This is reusable (spell AoE
  scatter, drops) so make it the standard, not a one-off inside Simulation.

## Pitfalls / gotchas
- **`EnvironmentSystem` is SESSION-OWNED — never cache it across map loads.**
  `Game1._envSystem` is a forwarding property to `_session.Env` (`Game1.cs`), and
  `StartGame` does `_session.Dispose(); _session = new GameSession()` — Dispose clears
  the env's defs/objects AND disposes its textures. Any system that copies the
  `EnvironmentSystem` reference at Bind/Init time (rather than live-reading
  `_game._envSystem` each use, the pattern `_sim => _game._sim` established in commit
  978ce27) ends up scanning a disposed, empty env after the first `StartGame`.
  Historical trap: 978ce27 fixed the stale-`Simulation` half of this in
  `ForagableSystem`/`WorkerSystem` but its comment wrongly claimed `_env` was
  "a persistent Game1 field" — it is not.
- **Don't `RemoveObject` a foragable to "eat" it** — use `CollectForagable(objIdx)`: it sets
  `Collected` + `RespawnTimer` and fires collision-dirty. `RemoveObject` shifts every later
  index and skips the respawn machinery.
- **Env object indices are unstable** across `RemoveObject`/`AddObject` and across frames —
  never cache an `objIdx` between ticks; re-find by scanning, or key by `ObjectID`/`ObjectID`
  string. (Foragable AI should re-`FindNearest` each decision, as `ForagableSystem` does.)
- **Filter on `IsObjectVisible(objIdx)`** before treating a foragable as present — a collected
  (not-yet-respawned) mushroom is still in `_objects` but invisible/uncollectable.
- **Respawn will resurrect eaten mushrooms** if the def has `RespawnTime > 0`. If a boar eats a
  mushroom and it later respawns *and* the boar spits it out on death, you double the mushroom.
  For belly mushrooms you likely want to eat foragables whose def is single-use, or accept the
  respawn as intended. Confirm the mushroom def's `RespawnTime` in `data/env_defs.json`.
- **Death loop is a reverse swap-pop** (`for i = count-1 .. 0`, `RemoveUnitTracked` swaps last
  into `i`). Do all per-unit death work (spit mushrooms, read belly, read position) **before**
  `RemoveUnitTracked(i)`; never spawn/remove *units* mid-loop. Spawning *env objects* mid-loop
  is fine (different array). Mirror `BroadcastRatPanicOnDeath`'s placement.
- **`OnSpawn`'s `AIContext` is partial** (no `EnvSystem`/`Workers`) — if you make boars a new
  archetype, do all mushroom/world queries in `Update`, not `OnSpawn` (see `ai.md`).
- **Map-content rule**: the *mushrooms in the world* the boars eat should be authored in
  `data/maps/default.json`, not spawned at startup from code (CLAUDE.md). Only the *spit-out*
  spawn (a runtime consequence of death) belongs in code.

## Related areas
- [ai.md](ai.md) — archetype handler pattern (for active seek), `SubroutineSteps.MoveToward`,
  `CorpseEatAI` as the passive-eat template, `ArchetypeRegistry` registration.
- [corpses.md](corpses.md) — the corpse produced on death (separate from the mushroom spit-out).
- game1-partials.md — `Game1.cs` archetype registration + `SpawnUnit`; env system is bound there.
