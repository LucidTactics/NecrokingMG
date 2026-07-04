# Zones — authored map areas (villages, wolf packs, deer herds)

Rectangular authored map areas ("zones") drawn in the **map editor's Zones tab**, saved to
a `data/maps/<map>_zones.json` sidecar, and applied **once at map load** by
`Game1.ApplyZones`. Today there are three kinds (`Village`, `WolfPack`, `DeerHerd`) and
zones are load-time-only: after `ApplyZones` runs, nothing else reads them during gameplay
(though `_zoneSystem` stays populated on `Game1` all game, so a runtime system *could*).
There is **no in-game (non-editor) click/selection of zones** — selection exists only in
the editor.

## Files

### `Necroking/Game/ZoneTypes.cs`  ← the data model
- `ZoneKind` enum (`Village`, `WolfPack`, `DeerHerd`) — extend here for a new kind (doc
  comment says: also extend `ZoneColors`).
- `MapZone` — `Id` ("zone_N"), `Name`, `Kind`, center `X/Y` + `HalfW/HalfH` (same rect
  convention as `TriggerRegion`), `ContainsPoint(Vec2)`, and `Population`
  (`ZonePopulation`: Peasant/Hunter/Militia/Watchdog counts — Village kind only).
- `ZoneSystem` — the plain list owner (`Zones`, `ZonesMut`, `Add/Remove/Clear/SetZones`).
  Shared by editor (author/save) and game load (apply); cleared + reloaded per map like
  triggers/roads.
- `ZoneColors` — kind → editor overlay fill/border colors.

**Look/edit here when…** adding a new zone kind, adding per-zone config fields (e.g. spawn
tables), or changing the rect/containment model.

### `Necroking/Game1.Zones.cs`  ← load-time application
`Game1.ApplyZones()` — called once per map load after placed units + legacy villages spawn:
- `ApplyVillageZone` — creates a `Village` (`_villageSystem.Add`), adopts human units
  inside the rect (`VillageId` tag), spawns `Population` via `SpawnZoneGroup` →
  `SpawnUnit(defId, p)` + `ScatterSpotInRect` (deterministic LCG, walkability-checked
  scatter at 90% of half-extents).
- `ApplyAnimalZone(z, archetype)` — groups already-placed wild wolves/deer of the matching
  archetype (`ArchetypeRegistry.WolfPack` / `.DeerHerd`) inside the rect into ONE
  pre-formed squad (`_sim.Squads.CreateSquad`, `sq.Members.Add(u.Id)`, `SquadId` stamp),
  pre-empting SquadSystem's lazy proximity clustering. **It does not spawn animals** — it
  only groups map-placed ones — and it does **not** record a zone→squad mapping.

**Look/edit here when…** changing what a zone does at load, or adding a new kind's apply
branch (the `switch (z.Kind)` in `ApplyZones`).

### `Necroking/Data/MapData.cs` — `LoadZones(path, zones)`
Reads the `<map>_zones.json` sidecar (missing file = silent no-op). Camel-case fields:
`id, name, kind (enum string), x, y, halfW, halfH, population{peasant,hunter,militia,
watchdog}`. **New per-zone config must be added here AND in the editor's `SaveZones`.**

### `Necroking/Editor/MapEditorWindow.cs` — Zones tab (author/select/edit/save)
- `UpdateZonesTab` — world interaction: rubber-band "+ Draw Zone" creation, click-to-select
  (handles on selected zone first, then any zone body), region-style body/corner drag
  (`HitTestRectHandles` / `ApplyRectHandleDrag`).
- `DrawZonesTab` — right panel: kind combo + draw button + zone list + selected-zone
  property fields (name/kind/x/y/halfW/halfH). Field ids embed the selection index
  (`zone_name_{idx}`) so switching selection drops text-field focus — copy this pattern.
- `ZoneLeftPanelRect` / `DrawZoneLeftPanel` — the LEFT side per-kind config panel,
  currently **gated to `ZoneKind.Village` only**: name + population int fields + a live
  "inside the zone" contents summary (buildings from `_envSystem`, units from
  `_placedUnits`). **This is the template/home for any new per-zone config UI**
  (per-type spawn rates etc.) — widen the kind gate.
- `SaveZones(mapsDir)` — writes the sidecar with `Utf8JsonWriter` (population only for
  Village). `DevSelectZone` (via `Game1.Dev.cs`) selects a zone headlessly for testing.

**Look/edit here when…** zone selection/drag feels wrong, adding config UI for a zone
kind, or persisting new zone fields.

## Lifecycle / wiring
- `Game1.cs` map-load block: `_zoneSystem.Clear()` before every load; then
  `MapData.LoadZones(GamePaths.Resolve($"{GamePaths.MapsDir}/{mapName}_zones.json"), _zoneSystem)`
  for file-backed maps. `ApplyZones()` runs after all unit spawning.
- `_zoneSystem` is a `readonly` field on `Game1` (NOT GameSession-owned) — safe to read
  from runtime systems every tick; it always reflects the current map.
- As of 2026-07 no `*_zones.json` exists in `data/maps/` — the system is live but the
  default map has no authored zones yet.

## Pitfalls
- Doc comments in `ZoneTypes.cs`/`Game1.Zones.cs` say `assets/maps/…` — stale; the real
  path is `data/maps/<map>_zones.json` (`GamePaths.MapsDir`).
- `population` is only serialized for Village zones (`SaveZones` gate) — a new kind's
  config needs its own save/load branch or an unconditional block.
- `ApplyAnimalZone` keeps no zone→squad map; a runtime spawner that must add newly spawned
  animals to "the zone's squad" has to record the squad id itself (or leave `SquadId=0`
  and let SquadSystem lazily cluster them).
- Zones are load-time-only today — any "periodic per-zone" behavior is new runtime code;
  hook it in the `Game1.cs` sim-tick block next to `_envSystem.UpdateForagables(dt)`.

## Related areas
- [world.md](world.md) — env objects/foragables that a foraging-zone spawner would create
  (`EnvironmentSystem.AddObject` / `CollectForagable` / `IsObjectVisible`).
- [ai.md](ai.md) — `ArchetypeRegistry` ids used by `ApplyAnimalZone`; squads.
- [editor.md](editor.md) — `EditorBase` field widgets used by the zone panels.
- game1-partials.md — map-load sequence + `SpawnUnit`.
