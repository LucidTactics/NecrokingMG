# Movement — pathfinding, steering, ORCA avoidance, collision

The movement stack is **flow-field based, not path based**: no unit stores a path. Every
tick each moving unit asks `Pathfinder.GetDirection(pos, target)` for a unit direction
vector, AI turns it into `PreferredVel`, ORCA resolves crowding into `Velocity`, and a
wall-collision pass integrates position. "Repath" = flow-field cache invalidation
(`Simulation.RebuildPathfinder`), never per-unit replanning.

## Per-tick flow (Simulation.Tick order)

1. `RebuildQuadtree()` — rebuild unit quadtree from scratch.
2. Push `Settings.Performance` into pathfinder; `_pathfinder.BeginTick(frame)` — reset
   per-tick Dijkstra ms budget, drain deferred flow requests priority-first.
3. `UpdateAI` (legacy `AIBehavior` switch + archetype handlers) writes `PreferredVel`
   and/or `MoveTarget`; `BoarForageAI` / `WolfPackHuntAI` may override afterward.
4. Dodge-hop lerp, `TrampleSystem.TickAll`, `ApplyTerrainSpeedModulation` (scales
   `MaxSpeed` by `TerrainCosts.GetSpeedMultiplier`).
5. `UpdateMovement(dt)` — ORCA gather+solve, stuck nudge, Newtonian accel caps,
   stuck-in-tile / stuck-in-env escapes, sub-stepped wall collision, bounds clamp.
6. `_physics.Update` (knockback arcs own units with `InPhysics`), `Horde.UpdateStates`,
   `UpdateFacingAngles`, `UpdateCombat`.
7. Cleanup: `_flowFields.EvictIfNeeded()` (legacy), `_pathfinder.EvictStaleFlowFields(frame, 600)`.

## Files

### `Necroking/World/Pathfinder.cs` — sector flow fields + routing + imaginary chunks
The whole pathfinder (~2000 lines, one class `Pathfinder`). Map is split into 64×64-tile
**sectors** (`SectorSize`), 3 unit-size tiers (`TerrainCosts.NumSizeTiers`).
- **Sector connectivity**: `BuildConnectivity` → `_sectorConnected[tier, sector, 4]`
  (open tile pair across each shared edge).
- **Inter-sector routing** (the "portal" layer): `GetRoute(destSector, tier)` — 4-dir BFS
  over sectors producing `NextDir`/`HopDist` arrays, cached in `_routeCache`.
- **Per-sector Dijkstra flow**: `ComputeSectorFlow` (8-dir, diagonal corner-cut checks,
  escape propagation into tier-inflated tiles) + `BuildDirectionField` (plateau
  fallback). Entry variants: `GetFlowToTile` (same sector), `GetFlowToBorder`,
  `GetFlowToMultiBorder` (primary/lateral/extended border masks + Manhattan bias toward
  the clamped target).
- **Flow cache**: `_flowCache` keyed by `FlowKey` (sector, target type/data, tier);
  evicted entries drop to `_staleFlowCache` and can serve as budget-defer fallback.
  Live eviction is age-based: `EvictStaleFlowFields` (called from Simulation cleanup,
  600 frames). NOTE: `EvictFlowFields(maxCached)` and `EvictRoutes` exist but currently
  have **no callers**.
- **Budgeted pathfinding**: `BudgetedPathfinding` / `DijkstraBudgetMsPerTick` (pushed
  from settings each tick), `BeginTick` drains `_pendingRequests` (priority = how badly
  the stale flow steers vs the straight line, see `EnqueueMiss`), `HasDijkstraBudget`,
  `_lastQueryDeferred` distinguishes "deferred → beeline this tick" from "genuinely
  unpathable → imag chunk".
- **Main API**: `GetDirection(unitPos, targetPos, frame, sizeTier, unitIdx)` — fallback
  ladder: persistent imaginary chunk → tile/border flow → BFS to nearest tile with
  valid flow → lower-tier fallback → imaginary chunk → boundary escape → beeline.
  Each branch is recorded per-unit as a `PathDecision` (debug overlay via
  `GetUnitDecision`).
- **Imaginary chunk** (stuck / concave-trap escape): `GetLocalChunkDirection` runs a
  64×64 unit-centered Dijkstra seeded from the target tile (if inside) or the borders
  facing the target; result stored per-unit in `_unitImagChunks` and reused
  (`ImagChunkPersist`) until the unit reaches the target tile, leaves the chunk, or the
  flow dies; target moved → `RecomputeImaginaryChunkFlow` within same bounds.
  `RunEscapePropagation` / `BuildChunkDirectionField` are the chunk-local twins of the
  sector versions. `ClearImaginaryChunk(unitIdx)` exists but has **no callers** — and
  `_unitImagChunks`/`_unitDecisions` are keyed by unit **index** while `UnitArrays`
  swap-and-pops, so state can leak to a different unit after removals.
- Diagnostics: `Diag*` static counters, dumped into `log/perf.log` by Simulation's
  slow-tick logger (`pf={...}` fields).
- **Look/edit here when…** units path wrong around obstacles, get trapped in concave
  "cups", sector-boundary oscillation, pathfinding perf spikes, adding a new fallback
  or flow-target type.

### `Necroking/World/TileGrid.cs` — terrain + cost fields
`TerrainType`, `TerrainCosts` (`GetCost`, `GetSpeedMultiplier`, `SizeToTier`,
`SizeTierRadius`), `TileGrid` with base `_costField` plus per-size-tier inflated
`_costFieldTier` (rebuilt by `RebuildCostField`/`RebuildTieredCostFields`). 255 = impassable.
**Look/edit here when…** terrain passability/cost/speed, size-tier inflation.

### `Necroking/World/FlowField.cs` — LEGACY whole-map flow fields
`FlowFieldManager.GetFlowField` computes a whole-map integration+direction field. Only
`EvictIfNeeded()` is still called from Simulation cleanup; `GetFlowField` has **no
callers** — dead code kept alive by one eviction call. Also home of the shared
`FlowDir` enum + `FlowDirUtil.ToVec` (those ARE used by Pathfinder).
**Look/edit here when…** consolidating dead code; changing FlowDir vectors.

### `Necroking/Movement/Orca.cs` — ORCA/RVO velocity solver
Pure static solver: `Orca.ComputeORCAVelocity(position, currentVel, preferredVel,
neighbors, ORCAParams, dt)`. `ORCANeighbor` carries `Priority` and `IsStatic`;
responsibility split: static = 1.0 on the unit, lower priority = 0.9, higher = 0.1,
equal = 0.5. Overlapping neighbors get an invDT push-apart line (this doubles as the
unit-vs-unit collision response — there is no separate rigid unit-unit collision pass).
`LinearProgram1D/2D` + `LinearProgram3Fallback` (per-iteration list alloc is deliberate:
recursion re-enters LP2D; a shared scratch there caused a bug). `_orcaLinesScratch` is a
shared static — single-threaded assumption.
**Look/edit here when…** avoidance pushing/priority feel, units phasing through each
other, NaN velocities, parallelizing movement.

### `Necroking/Movement/UnitSystem.cs` — Unit data model + UnitArrays
`Unit` (Position/Velocity/PreferredVel/MoveTarget/MaxSpeed/Radius/OrcaPriority/
StuckTime/MoveEffort/GhostMode/InPhysics + all gameplay state), `MoveEffort` gait-bias
enum, `UnitArrays` (swap-and-pop removal with O(1) `_idToIndex`), `UnitUtil.ResolveUnitIndex`.
**Look/edit here when…** adding per-unit movement state; index-vs-id stability questions.

### `Necroking/Movement/FacingUtil.cs` — shared turn-rate-capped rotation
`FacingUtil.TurnToward` — the one rotation step used by `Simulation.UpdateFacingAngles`
and handler FacePosition calls. **Look/edit here when…** turn speed / facing snap.

### `Necroking/Game/Simulation.cs` — UpdateMovement + steering entry points
The per-frame consumer of everything above.
- `UpdateMovement(dt)`: movement gates (jump/incap/pending attack/InCombat plant),
  `GhostMode` bypass, `skipOrca` for the player, idle shortcut; ORCA neighbor gather
  (quadtree `QueryRadius(max(Radius*5, 3))`, inline top-K=10 nearest units, cross-faction
  priority equalized; + up to 6 nearest static env circles from `_envIndex`);
  `Orca.ComputeORCAVelocity`; **stuck nudge** (`StuckTime` accumulates when prefSpeed>0
  but resolved speed <10% MaxSpeed; after 0.33 s blends a perpendicular component,
  side alternating by index parity, ramp 30%→80% over 1.6 s); **Newtonian accel model**
  (forward accel / decel / lateral caps from UnitDef or `Settings.Combat`, `capDt`
  clamped to 1/20 s); player-only env-circle velocity clipping;
  **stuck-inside-blocked-tile escape** (search ≤20-tile radius for free tile, push
  MaxSpeed×3 capped at 1 tile/frame); **stuck-inside-env-object escape** (penetration
  push); **sub-stepped wall collision** (≤ half-tile probes vs tunneling, axis-independent
  X/Y with ±0.1..0.3 gap probes, wall-normal sliding, `r = Radius*0.7`), world-bounds clamp.
- `IsBlocked(px, py, r)` — AABB-of-tiles impassability probe (base cost field only).
- Steering helpers: `MoveTowardPosition`/`MoveTowardUnit` (dist>3 → `GetDirection`,
  else beeline), used by the legacy `AIBehavior` switch (`MoveToPoint`, wolf AI, etc.).
- `ApplyTerrainSpeedModulation`, `UpdateFacingAngles`, `RebuildQuadtree`.
- `RebuildPathfinder()` — the invalidation choke point: bake walls → `RebuildCostField`
  → env collisions → `_envIndex.Rebuild` → `_pathfinder.Rebuild()` (clears all caches).
**Look/edit here when…** units slide during combat, get stuck on trees/walls, tunneling
at high game speed, acceleration feel, adding a movement gate.

### `Necroking/AI/SubroutineSteps.cs` — archetype-AI move primitives
`MoveToward(ctx, target, speed)` (same pathfind-vs-beeline split + locomotion anim),
`SetLocomotionAnim`, `SetIdle`, `SetEffort` (MoveEffort → MaxSpeed), flee target setters
(`SetFleeRandomTarget`, `SetFleeFromTarget`). All archetype handlers steer through
these. See [ai.md](ai.md).
**Look/edit here when…** adding a movement primitive for handlers, arrival thresholds.

### `Necroking/Game/HordeSystem.cs` — formation movement (slot targets)
`HordeSystem`: Fibonacci/Vogel-spiral slots (`GoldenAngle`), permanent per-unit
`SlotIndex` + `DiscreteOffset` (shuffled at `NextShiftAt` — discrete so the flow-cache
key stays stable), `Tick` (circle center follows necromancer), `UpdateStates`
(Following/Chasing/Engaged/Returning + aggro scan, aggression levels), and
`GetTargetPosition(id, out slot)` — the slot position `AI/HordeMinionHandler` feeds into
`MoveToward`. Command/recall: `HordeMinionHandler.CommandTo`/`Recall` (called from
`Game1.Spells.cs`).
**Look/edit here when…** formation shape/spacing, horde chase/leash, summon-burst
pathfinder load (slot moves = new flow keys).

### `Necroking/Spatial/Quadtree.cs` + `Necroking/World/EnvSpatialIndex.cs`
`Quadtree` — units only, rebuilt from scratch every tick (`RebuildQuadtree`), queried by
ORCA gather, combat, projectiles. `EnvSpatialIndex` — static env-object circles (trees,
rocks) for ORCA statics and player clipping; rebuilt inside `RebuildPathfinder`.
**Look/edit here when…** neighbor query radius/perf, env obstacle radii.

### Scripted-movement owners (bypass ORCA/pathfinding while active)
`Necroking/Game/PhysicsSystem.cs` (2.5D knockback arcs, `InPhysics`),
`Necroking/GameSystems/JumpSystem.cs` (JumpPhase state machine),
`Necroking/GameSystems/TrampleSystem.cs` (charge velocity + follow-through), and the
dodge-hop lerp inline in `Simulation.Tick`.

### Debug / tests
`Render/DebugDraw.cs` (flow/chunk/decision overlays), the `pf={}` perf-log line in
`Simulation.Tick`, `Scenario/Scenarios/PathfindingTestScenario.cs`,
`MoveToPointScenario`, `WallGateScenario`/`WallTrapScenario`, `SummonLagScenario`.
`Algorithm/AStar.cs` (`AStar.FindWorld`) is used **only** by WallGateScenario — not part
of the runtime stack.

## Pitfalls
- Per-unit pathfinder state (`_unitImagChunks`, `_unitDecisions`) is keyed by **unit
  index**, which is recycled by `UnitArrays.RemoveUnit` swap-and-pop; nothing calls
  `ClearImaginaryChunk`. Stale/wrong-unit state after deaths is possible.
- The imag-chunk fallback costs about the same as the Dijkstra the budget deferred —
  never run it on the `_lastQueryDeferred` path (comments in `GetDirection` explain).
- ORCA reads raw `Position`; `RenderPos`/`RenderOffset` are cosmetic only.
- Wall collision uses `Radius*0.7` while ORCA uses full `Radius` — deliberate 1-tile-gap
  clearance; changing either breaks gates.
- High game speed: accel caps use `capDt` clamp and movement sub-steps exist specifically
  to prevent snap-turns and wall tunneling — keep them if refactoring integration.

Related: [ai.md](ai.md) (who writes PreferredVel/MoveTarget), [combat.md](combat.md)
(InCombat plants units), [game1-partials.md](game1-partials.md) (input → `_necroMoveInput`).
