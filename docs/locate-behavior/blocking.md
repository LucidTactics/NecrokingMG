# Blocking & standability — "does something block here?"

The routing page for every flavor of "is this position blocked / walkable / standable /
valid for placement?". There are **three representations** of env-object blocking, all
derived from the same def fields via one shared formula, plus a walls/terrain base layer
they all sit on. Consolidated 2026-07-07 (facade + shared math; the representations
themselves are intentionally separate — they serve different perf needs).

## Entry points — use these first

- **"Can a unit stand here?"** (teleport/dodge landings, spawn placement, AI destination
  picks) → **`_sim.Query.IsSpotBlocked(pos, unitRadius)`** (`Necroking/Game/WorldQuery.cs`).
  Walls/deep-water probed at the movement wall radius (`unitRadius*0.7`, same as
  `UpdateMovement`) + static env collision circles. Safe any time, including paused —
  the grid and env index are event-rebuilt, not per-tick.
- **Walls/terrain only** → `_sim.Query.IsWallBlocked(pos, radius)`, or `TileGrid.
  OverlapsImpassable(px, py, r)` if you already hold the grid (AI code does — see
  `SubroutineSteps.IsPointWalkable`, a thin wrapper). Out-of-bounds tiles count as
  WALKABLE (the world-bounds clamp handles the edge); don't "fix" that.
- **"Can I place an env object here?"** (editor brush, building menu, zone spawns) →
  `EnvironmentSystem.CanPlaceObject(defIndex, x, y, scale)` — collision circle +
  `PlacementRadius` additive spacing vs every live object.
- **Never hand-roll** a `GetCost(...) == 255` footprint loop or the collision-circle
  math — see the single sources below.

## The single-source primitives

- **Walls + terrain walkability**: `Necroking/World/TileGrid.cs`. `WallSystem` bakes
  `TerrainType.Wall`, `GroundSystem` paints terrain; `TerrainCosts.GetCost` maps
  Wall & DeepWater → 255 (impassable) in the base `_costField`. Everything downstream
  reads this. The footprint probe is `TileGrid.OverlapsImpassable` (moved from
  `Simulation.IsBlocked`, which now delegates).
- **Env collision circle**: `EnvironmentSystem.GetCollisionCircle(def, in obj)` /
  `GetCollisionCircle(objIdx)` returning `EnvCollisionCircle {CX, CY, R}` —
  `es = def.Scale * obj.Scale; centre = (X,Y) + CollisionOffset*es; r = CollisionRadius*es`.
  Used by grid stamping, `EnvSpatialIndex.Rebuild`, dirty-region fires, `RemoveObject`,
  `CanPlaceObject`, and `DebugDraw`. **Never inline this math.**

## The three representations (why three: different consumers, different shapes)

| | Data | Built by | Read by |
|---|---|---|---|
| **A. Pathfinding** | per-size-tier cost fields in `TileGrid` (`GetCost(x,y,tier)`, env circles stamped + tier inflation via `StampImpassableCircleTier`) | `EnvironmentSystem.BakeCollisions` / `RebakeCollisionRegion` / `StampObjectCollisionAt` | `World/Pathfinder.cs` only — plans routes with body clearance |
| **B. Runtime collision** | base `_costField` (walls-only) via `TileGrid.OverlapsImpassable` + `EnvSpatialIndex` bucket grid of circles | walls: `RebuildCostField`; circles: `EnvSpatialIndex.Rebuild` | `Simulation.UpdateMovement` internals (ORCA static gather, player clipping, stuck escapes — direct `_envIndex`), `WorldQuery.IsSpotBlocked` (via `Simulation.EnvIndex`, the low-level accessor) |
| **C. Placement** | live `_objects` list, linear overlap loop | nothing (reads live state) | `EnvironmentSystem.CanPlaceObject` — editor brush (`MapEditorWindow`), `BuildingMenuUI`, zone spawns (`Game1.Zones.cs`) |

Since 2026-07-07 all three use `GetCollisionCircle` — C previously had divergent math
(unscaled offsets, no `def.Scale`), so placement spacing now matches what actually
blocks movement/pathfinding (stricter for big-scale defs, circles centred on the true
collision centre).

## Sync flow — do NOT bypass

Env collision changes must go through the event, or representations A and B silently
desync from the objects:

`EnvironmentSystem` add/remove/destroy/collect/respawn → `FireCollisionsDirty` (prefers
the region callback `OnCollisionRegionDirty`, falls back to `OnCollisionsDirty`) →
wired in `Game1.cs` to `_sim.RequestPathfinderRebuild()` / `RequestPathfinderRegionRebuild(...)`
→ requests coalesced, drained at the top of `Simulation.Tick` →
**`RebuildPathfinder` / `RebuildPathfinderRegion`** (Simulation.cs) rebuild grid stamps +
`_envIndex.Rebuild` + pathfinder cache invalidation **together**. One exception:
`StampObjectCollisionAt` (editor batch-placement incremental stamp) writes rep A
directly — only safe because the editor triggers its own rebuild afterwards.

## Related but separate

- **Unit-vs-unit** avoidance/collision is ORCA (`Movement/Orca.cs`) fed from the
  per-tick `Spatial/Quadtree.cs` — units are never "blockers" in any representation
  above. See [movement.md](movement.md).
- **Building glyph placement** adds `MagicGlyphSystem.CanPlace` on top of
  `CanPlaceObject` (`UI/BuildingMenuUI.cs`).

**Look/edit here when…** something can be walked/teleported into (or can't but should),
placement rejection feels wrong, a new system needs a "is this spot free" check, env
objects stop blocking after an add/remove path skips the dirty event, or the collision
circle drawn by the debug overlay disagrees with behavior.
