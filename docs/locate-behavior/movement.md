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

### `Necroking/World/Pathfinder.cs` — sector flow fields + portal-graph routing + imaginary chunks
The whole pathfinder (~2300 lines, one class `Pathfinder`). Map is split into 64×64-tile
**sectors** (`SectorSize`), 3 unit-size tiers (`TerrainCosts.NumSizeTiers`).
- **Portal graph** (commit `ccfeadc`, replaced the old `_sectorConnected`/`GetRoute` hop-BFS
  + border-mask scheme — those symbols are deleted): portals = maximal contiguous
  tier-passable border spans chopped to ≤16 tiles (`struct Portal`, `_portals[tier]` flat
  list, `_sectorPortals`). Extracted eagerly in `Rebuild()` (`BuildPortals`/
  `ExtractSectorSpans`/`ScanBorderSpans`), re-extracted per touched sector ring in
  `InvalidateRegion`.
- **Portal cost matrices**: `GetPortalMatrix(sectorIdx, tier)` — lazy intra-sector
  portal-to-portal cost matrix (`PortalMatrix`, `_portalMatrixCache`). **Uniform-cost
  sectors** (`_sectorUniformCost`, maintained by `ComputeSectorUniformCost` in
  BuildPortals/InvalidateRegion) get an **analytic octile matrix — no Dijkstras, no
  budget charge** (open ground is the most portal-dense case and needed none). Mixed
  sectors run one window Dijkstra per portal (`RunWindowDijkstra` with null dirs =
  cost-only), **budget-charged per row and resumable** (`_partialMatrixCache` persists
  finished rows; ≥1 row progress per call, no livelock), dropped on region invalidation.
- **Routing** (commit `f202cb4`): `GetPortalRoute` / `SeedPortalRoute` /
  `ResumePortalRoute` — **corridor-bounded resumable A\*** over portal nodes (TWO nodes
  per portal, near/far side), seeded from the destination side, octile heuristic toward
  the *requesting* unit's sector, early-out when that sector's portals settle
  (+2-sector stop-slack). Cached per (destSector, tier) in `_routeCache` as a **sparse**
  `PortalRoute` (Dictionary node→{G,Settled} + resolved-sector set); budget aborts
  resume from the persisted frontier. Memory scales with the corridor, not the map.
- **Per-sector Dijkstra flow**: `ComputeSectorFlow` (8-dir, diagonal corner-cut checks,
  escape propagation into tier-inflated tiles) + `BuildDirectionField` (plateau
  fallback). Entry variants: `GetFlowToTile` (same sector) and `GetFlowToPortalSet`
  (FlowKey type 3 — one field per (unit sector, dest sector, tier), seeding every
  **settled** finite-cost portal's span tiles at remaining-cost + along-span slope; all
  units heading to the same dest sector share one field).
- **Flow cache**: `_flowCache` keyed by `FlowKey` (sector, target type/data, tier);
  evicted entries drop to `_staleFlowCache` and can serve as budget-defer fallback.
  Live eviction is age-based: `EvictStaleFlowFields` (called from Simulation cleanup,
  600 frames).
- **Budgeted pathfinding**: `BudgetedPathfinding` / `DijkstraBudgetMsPerTick` (pushed
  from settings each tick), `BeginTick` drains `_pendingRequests` (priority = how badly
  the stale flow steers vs the straight line, see `EnqueueMiss`), `HasDijkstraBudget`,
  `_lastQueryDeferred` distinguishes "deferred → beeline this tick" from "genuinely
  unpathable → imag chunk".
- **Main API**: `GetDirection(unitPos, targetPos, frame, sizeTier, unitId)` — fallback
  ladder: persistent imaginary chunk → **straight-line shortcut** (`HasLineOfSight`,
  supercover DDA on the tier cost field, ≤ `LosMaxTiles`=160; clear line → beeline,
  `PathDecision.LineOfSight` — answers 60-80% of queries on open maps so the flow
  machinery only runs where the straight path is blocked) → tile/portal-set flow →
  BFS to nearest tile with valid flow → lower-tier fallback → imaginary chunk →
  boundary escape → beeline.
  Each branch is recorded per-unit as a `PathDecision` (debug overlay via
  `GetUnitDecision`).
- **Imaginary chunk** (stuck / concave-trap escape): `GetLocalChunkDirection` runs a
  64×64 unit-centered Dijkstra seeded from the target tile (if inside) or the borders
  facing the target; result stored per-unit in `_unitImagChunks` and reused
  (`ImagChunkPersist`) until the unit reaches the target tile, leaves the chunk, or the
  flow dies; target moved → `RecomputeImaginaryChunkFlow` within same bounds.
  `RunEscapePropagation` / `BuildChunkDirectionField` are the chunk-local twins of the
  sector versions. Per-unit state (`_unitImagChunks`, decisions) is keyed by unit **Id**
  (uint) — the old index-keying leak is fixed — and `Simulation` calls
  `ClearImaginaryChunk(id)` on unit removal.
- Diagnostics: `Diag*` static counters (incl. `DiagMissPortalFlow`), dumped into
  `log/perf.log` by Simulation's slow-tick logger (`pf={...}` fields; `pflow:` = portal
  flow misses, `dj_ms` = budget spend, `pend` = deferred requests).
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

### `Necroking/Movement/UnitModel.cs` — Unit data model + UnitArrays
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
- **Player (necromancer) movement is computed in `UpdateAI` → `AIBehavior.PlayerControlled`
  case, NOT in `UpdateMovement`.** Input arrives via `SetNecromancerInput(moveDir, running)`
  (stored in `_necroMoveInput`/`_necroRunning`, pushed each frame from `Game1.cs` Update's
  WASD block before `Tick`). That case does the sprint ramp + speed calc, sets `MaxSpeed`,
  then applies `_units[i].PreferredVel = _necroMoveInput * speed` — this is the one line
  that drives the player. **The player movement-gate precedent lives right here**: the case
  already zeroes `PreferredVel` when `CorpseInteractPhase != 0`, and the pre-switch guards
  zero it for `PendingAttack`/`InCombat`. To add a "can't move while casting" gate, force
  `PreferredVel = Vec2.Zero` (and, for an *instant* stop rather than a decel-capped glide,
  also zero `Velocity`) in this case when the cast flag is set. The cast-in-progress flag
  (`_pendingCastAnim`) is a `Game1` field, so plumb it in like the existing inputs via a new
  `Simulation.SetNecromancerCasting(bool)` setter called next to `SetNecromancerInput`.
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
`Necroking/Game/JumpSystem.cs` (JumpPhase state machine),
`Necroking/Game/TrampleSystem.cs` (charge velocity + follow-through), and the
dodge-hop lerp inline in `Simulation.Tick`.

### Debug / tests
`Render/DebugDraw.cs` (flow/chunk/decision overlays), the `pf={}` perf-log line in
`Simulation.Tick`, `Scenario/Scenarios/PathfindingTestScenario.cs`,
`MoveToPointScenario`, `WallGateScenario`/`WallTrapScenario`, `SummonLagScenario`.
`Algorithm/AStar.cs` (`AStar.FindWorld`) is used **only** by WallGateScenario — not part
of the runtime stack.

## Pitfalls
- Routing work is on-demand + budget-capped (`DijkstraBudgetMsPerTick`, default from
  `Settings.Performance`): a moving destination (e.g. followers chasing a sprinting
  player) creates a new route + flow key every time the dest crosses a sector border —
  route A\* resumes cheaply, but flow fields and matrices are recomputed. Watch
  `dj_ms`/`pend`/`pflow` in `log/perf.log`; `PortalRouteScaleScenario` is the guardrail.
- The imag-chunk fallback costs about the same as the Dijkstra the budget deferred —
  never run it on the `_lastQueryDeferred` path (comments in `GetDirection` explain).
- ORCA reads raw `Position`; `RenderPos`/`RenderOffset` are cosmetic only.
- Wall collision uses `Radius*0.7` while ORCA uses full `Radius` — deliberate 1-tile-gap
  clearance; changing either breaks gates.
- High game speed: accel caps use `capDt` clamp and movement sub-steps exist specifically
  to prevent snap-turns and wall tunneling — keep them if refactoring integration.

Related: [ai.md](ai.md) (who writes PreferredVel/MoveTarget), [combat.md](combat.md)
(InCombat plants units), [game1-partials.md](game1-partials.md) (input → `_necroMoveInput`).
