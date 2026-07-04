# Movement Systems Review — findings & plans (2026-07-03)

## STATUS 2026-07-04 — implementation done through Phase 3

- **Phase 1 (correctness)** — commit `ecdffe8`. Everything in P0 + the P1 ORCA/budget
  fixes below is DONE.
- **Phase 2 (perf)** — commit `def083d`. Dirty-region rebuild (P0-C2), flow-key
  quantization (P2-1 light: 2x2 tile buckets + 8-tile border bands), allocation churn
  (P2-2..8), speed-aware ORCA query radius (P1-ORCA-3). DONE.
- **Phase 3 (structural)** — dead-code deletion (`e8c3617`), window-Dijkstra
  consolidation into one `RunWindowDijkstra` core with pooled scratch + stale-pop skip,
  RVO2-canonical LP restructure (fail-index LP2D, hard static lines in LP3, no
  recursion/alloc), `__ChargeWallCollision` goto replaced by an extracted
  `ResolveWallCollision` pass, sub-stepped accel integration (fixes 8x-slower
  accel/turn at x8), swept trample scan + impact trigger (fixes phase-through at x8).

### Deferred-items status (final, 2026-07-04)
1. **Portal-set flows / portal graph — DONE (commit ccfeadc).** True portal graph:
   <=16-tile contiguous border-span portals, lazy budget-charged intra-sector cost
   matrices, dest-seeded Dijkstra routing with TWO nodes per portal (near/far side —
   single-sided costs made border tiles point at each other), portal-set flow fields
   keyed (unit sector, dest sector, tier) with remaining-cost + along-span-slope
   seeding. Old hop BFS / connectivity bitmask / multiborder masks deleted. 12/12.
2. **Full MovementSystem class extraction — INTENTIONALLY STOPPED.** All functional
   payoffs landed (collision pass extracted, goto gone, accel sub-stepped, hand-back
   contract fixed); the remainder is relocating the gather/solve block behind a
   7-scratch-parameter method signature — arrangement, not behavior. Do it only if/when
   UpdateMovement needs surgery again anyway.
3. **Legacy UpdateWolfAI deletion — BLOCKED, new finding: the comment claiming
   WolfHitAndRunScenario is its only user is STALE. The Boar def itself ships
   `"ai": "WolfHitAndRun"` (units.json), so the legacy machine drives live boars in
   gameplay. Deletion requires migrating boar AI to an archetype handler (BoarForageAI
   exists as a starting point) + in-game behavior verification via drive-game — a
   gameplay task, not a mechanical deletion.
4. **Pre-existing trample scenario failures — RESOLVED 2026-07-04.** Root cause was
   neither the routine-transition change nor def application: the Boar def is size 2
   while trample requires a STRICTLY smaller target, and every common unit (skeleton,
   soldier, rat) is also size 2 — the regular Boar's Trample weapon was dead in all of
   gameplay, not just tests. Fix (user-approved): Boar restored to size 3 in
   units.json (visuals unaffected; spriteScale is separate). Additionally
   trample_no_escape's ally-cordon setup was inherently fragile (the boar's own sweep
   + knockback chain reactions bowl the cordon away before the dodge rolls) — rewritten
   as a wall pocket, which can't be displaced and exercises the dodge's IsSpotBlocked
   wall check. Suite now 12/12.
5. **Progress-based stuck detection — DONE (minimal robust version):** StuckTime now
   also accrues when the collision pass eats >80% of the intended displacement
   (wall-blocked), not just when ORCA output is near zero (solver-blocked) — wall
   grinding, crowd jams, and env pins all feed the same constraint-checked escape
   bias. **Walls as ORCA half-plane constraints (P4-5) remains open** — revisit only
   if wall-adjacent dead-end steering shows up in play; the stuck escape now covers
   the stall case.

Full review of the movement stack: sector pathfinding, inter-chunk routing, imaginary-chunk
escape, ORCA, wall collision, and connected systems (quadtree, horde formation, physics,
trample, unit lifecycle). Four parallel deep-review agents + main-session verification of the
load-bearing claims. **Nothing implemented yet** — this doc is the work list.

Verdict in one line: the architecture (sector flow fields + connectivity BFS + ORCA) is sound
and the ORCA math is a faithful RVO2 transcription; the debt is concentrated in (1) a
swap-and-pop index-keying bug class, (2) lifecycle/hand-off hygiene, (3) full-map rebuilds
triggered by tiny gameplay events, (4) ORCA integration assumptions, and (5) flow-cache key
fragmentation.

---

## P0 — Confirmed player-visible bugs

### A. Swap-and-pop index-keying bug class (3 systems affected)
`UnitArrays.RemoveUnit` (Necroking/Movement/UnitSystem.cs:596-609) swap-and-pops; only
`_idToIndex` and `_necromancerIdx` (Simulation.cs:201-206) are repaired. Systems keying
per-unit state by raw index break on every death:

1. **PhysicsSystem** — `PhysicsBody.UnitIdx` (PhysicsSystem.cs:30-43) never remapped.
   Stale-index guard at :172-176 silently deletes the body **without clearing
   `units[..].InPhysics`** (only `Land()` :334 clears it). Any unit airborne at the list
   tail when another unit dies → frozen mid-air statue forever (AI skips at Simulation.cs:932,
   movement at :1622). If a spawn refills the slot first, the body drives the *wrong* unit.
   The dying unit's own body IS handled (Simulation.cs:3942-3946) — only the swapped survivor
   is not. **Fix:** store `uint UnitId` in PhysicsBody, resolve via `UnitUtil.ResolveUnitIndex`
   per frame (TrampleSystem already does this with `ChargeTargetId`); clear `InPhysics` when a
   body is dropped without landing.
2. **Pathfinder** — `_unitImagChunks` / `_unitDecisions` keyed by index (Pathfinder.cs:68,71);
   `ClearImaginaryChunk` (:1774) has **zero callers** (verified). Dead unit's imaginary chunk
   is inherited by the swapped-in unit → one frame of wrong steering or a wasted ~4ms
   `RecomputeImaginaryChunkFlow` inside a window centered on the dead unit's old position.
   **Fix:** key both dicts by `Unit.Id`.
3. **Stuck-nudge side parity** — `i % 2` (Simulation.cs:1814-1817) flips a unit's sidestep
   direction mid-maneuver when a death reindexes it. Also: head-on pairs with opposite
   parity sidestep in the SAME world direction (perp of `d` for even == perp of `-d` for odd)
   → crab-walk lockstep, never separate. **Fix:** derive side from `Id & 1` (stable) or
   `sign(cross(prefDir, toObstacle))`.

**Plan:** one "stable-Id sweep" — PhysicsBody Id-keying + InPhysics clear, Pathfinder dicts by
Id, nudge parity by Id. Small, independent, high payoff.

### B. HordeSystem.RemoveUnit is never called — permanent slot leak
`HordeSystem.RemoveUnit` (HordeSystem.cs:197) has zero callers (verified). Dead minions stay
in `_hordeUnits`; `FreeSlot` (:169) unreachable; `_nextSlot` monotonic →
`EffectiveRadius = SlotSpacing*√(_nextSlot-1+0.5)` (:79-81) **grows forever**, inflating
EngagementRange/LeashRadius/AggroRadius (:108-137). Maintain 40 minions through 200 replacement
summons → formation radius ~2.4× design; horde aggros/leashes at ranges the player never chose.
Comment at :50-53 ("slots freed by death are reused") describes code that can never run.
**Fix:** call `_horde.RemoveUnit(_units[i].Id)` in `Simulation.RemoveDeadUnits` (both the
PendingDespawn pre-pass and the death loop), or prune in `UpdateStates` when
`ResolveUnitIndex` returns -1.

### C. Full ~450ms pathfinder rebuild fired by tiny gameplay events
`OnCollisionsDirty` → `Simulation.RebuildPathfinder` (Simulation.cs:4237-4250): full wall bake +
cost field + ALL env collisions re-stamped + env index + `Pathfinder.Rebuild()` (clears every
route/flow/stale cache + all imag chunks + reconnects all 3 tiers). ~450ms on the default
4097² map (MapEditorWindow.cs:469-476), plus the hidden cost: every moving unit becomes a
flow-cache miss next tick. Gameplay triggers (EnvironmentSystem.cs):
- :563 object placed (incl. `SpitBoarBellyOnDeath` Simulation.cs:3528-3536 calling AddObject
  **in a loop** — boar death with full belly = N×450ms in one frame)
- :578 removed, :592/:992 destroyed (building killed)
- :648 **foragable collected** (picking one deathcap = 450ms hitch; ForagableSystem.cs:110,
  WorkerHandler.cs:153)
- :676 **foragable respawn timer** (world spontaneously hitches when a mushroom regrows)
- BoarForageAI.cs:89 per mushroom eaten

**Plan (staged):**
1. Stopgap: defer + coalesce — set a dirty flag, run at most one rebuild per tick (kills the
   boar N× case), or batch like the editor does.
2. Real fix: give `OnCollisionsDirty` a payload `(cx, cy, r, added/removed)`. Adds →
   `StampObjectCollisionAt` (EnvironmentSystem.cs:1105-1118, already exists) + evict only
   FlowKeys whose SectorX/Y intersect (FlowKey embeds sector coords, Pathfinder.cs:55-62) +
   re-scan connectivity only for touched sectors (border scans are O(64)); drop routes only if
   connectivity bits flipped. Removals → per-tile refcount un-stamp or re-bake affected
   sectors only. Keep full rebuild for map load / editor exit.

### D. Blocked-tile escape / dodge / deep-water traps (Simulation.cs)
1. **Escape picks first row, not nearest tile** (:1954-1975): `&& !found` on the outer `dy`
   loop exits after the first row (starting at dy=-20) containing any free tile → units shoved
   up to 20 tiles NORTH through intervening walls at 1 tile/frame. `bestDist2` tracking is a
   lie. **Fix:** expanding Chebyshev rings, stop at first ring with a hit (also fixes the
   41×41×IsBlocked per-frame cost, ~6-7K grid reads per stuck unit); cache the escape target
   per stuck episode.
2. **`TrampleSystem.TryDodge` never checks walls/env** (:411-451): dodge hop (lerp bypasses
   all collision, Simulation.cs:621-638) can deposit units inside walls; recovery is then
   bug D1. Class doc claims "free tile" — it isn't checked. **Fix:** add
   `IsBlocked(candidate, r*0.7f)` + env-index overlap to the candidate filter.
3. **`MaxSpeed *= 0` bricks recovery** (TileGrid.cs:33-34 DeepWater/Wall → 0.0f;
   Simulation.cs:1602): escape push = `MaxSpeed*3 = 0` (:1980, :2026), stuck detector
   `speed < 0.1*0` never true (:1808) → unit knocked into deep water is permanently frozen.
   **Fix:** floor escape push at an absolute speed (e.g. `max(MaxSpeed*3, 2f)`); treat 0-mult
   tiles as blocked for pathfinding rather than as speed modulation.
4. **Player ignores terrain speed** (:998-1004 vs :1581-1603 vs :1693-1699): `MaxSpeed` is
   modulated after `PreferredVel` was built, and the skipOrca path has no speed clamp — shallow
   water does nothing to the necromancer. **Fix:** clamp `newVel` magnitude to `MaxSpeed`
   post-accel (also future-proofs every ORCA-bypassing path).
5. **StuckTime never cleared on scripted hand-offs** (freeze :1652-1658, charge goto :1630,
   `PhysicsSystem.Land` :331-336, `TrampleSystem.EndCharge` :590-602, `JumpSystem.EndJump`
   :301-309) → phantom sidestep right after resuming. **Fix:** clear in hand-back sites or
   centrally via a WasScripted flag.

---

## P1 — ORCA integration (core math verified faithful to RVO2; these are the seams)

1. **Broken reciprocity vs non-running neighbors** (Simulation.cs:1684-1698 idle shortcut,
   :1748-1751 priority, Orca.cs:78-85): idle/parked units skip ORCA entirely but movers still
   split responsibility as if they'd reciprocate — pair sum as low as **0.1** (high-priority
   mover vs parked minion) → plowing through formations, grinding overlap resolved at 0.1×.
   Same class: `InPhysics` ragdolls, trample chargers, jumpers, skipOrca necromancer
   (cross-faction gets only 0.5 vs player who takes 0). **Fix:** at gather time set
   `IsStatic = true` (or a `Reciprocates=false` flag keeping real velocity but responsibility
   1.0) for any neighbor that won't run the solver this tick. Machinery already exists.
2. **Stuck-nudge overwrites the ORCA solution** (Simulation.cs:1825): post-solve blend of RAW
   `PreferredVel` + perpendicular discards every constraint — rams units into the very
   neighbors/trees ORCA forbade, env-escape shoves back next tick → the chokepoint jitter.
   **Fix:** apply the perpendicular bias to `PreferredVel` *before* `ComputeORCAVelocity`
   (change the goal, not the answer); keep post-hoc override only at extreme StuckTime.
3. **Neighbor query radius decoupled from speed/horizon** (Simulation.cs:1702:
   `max(R*5, 3f)` ≈ 3 tiles vs TimeHorizon 3s, speeds to 6, dt to 0.4 at x8): avoidance is
   always late-and-sharp; at x8 a fast pair covers ~4 tiles/tick > radius → clean unit-unit
   tunneling (walls are sub-stepped, unit circles are not). The static reach check at :1775
   is a no-op (env query already truncated to 3 tiles). **Fix:**
   `queryRadius = max(current, MaxSpeed * ~1s + maxUnitRadius)`.
4. **LP3 fallback deviations from RVO2** (Orca.cs:227-231, :246-292): (a) static-obstacle
   lines are not kept hard in the fallback (RVO2 seeds projLines with them and never relaxes
   them) → under crowd pressure at a tree, returned velocity can press INTO the tree; (b)
   LP2D auto-recurses into LP3 on projected lines (semantically meaningless; RVO2 restores
   tempResult instead) — this recursion is the sole reason for the "deliberate" per-iteration
   list allocation (comment :49-53). **Fix:** restructure to canonical RVO2 form (LP2D returns
   fail index; LP3 inner failure restores tempResult; statics ordered first + numStaticLines
   param). Deletes the recursion, the allocation, and the semantic bug in one refactor.
5. **Colliding-branch degenerate push is wrong-signed** (Orca.cs:143-151): zero-w fallback
   `pushDir = relPos.Normalized()` points AT the neighbor (feasible side = into B); exactly
   coincident pairs both pick `(1,0)` and translate together forever. **Fix:** negate; break
   ties by Id parity.
6. Minor: `ORCAParams.MaxNeighbors` dead (real caps TopK=10 :1615, MaxStatic=6 :1769);
   unused `sqrt` at Orca.cs:106; TimeHorizon=3 triplicated (:1793, :1775, ORCAParams.Default);
   `Orca.Det` duplicates `Vec2.Cross`; sequential-update asymmetry (lower indices see
   this-tick state, higher see last-tick).

## P1 — Pathfinder budget & thrash

1. **Imag-chunk Dijkstras never charged to the budget**: `ChargeDijkstraMs` only in the three
   flow builders (Pathfinder.cs:576, 663, 807); `GetLocalChunkDirection` (:823) and
   `RecomputeImaginaryChunkFlow` (:1013) charge nothing, so N units recomputing chunks pass
   `HasDijkstraBudget()` every tick — unbounded spike, exactly what the budget exists to stop
   (header comment :124-126 knows the cost). **Fix:** same stopwatch+charge pattern.
   (The *gating* invariant — deferred queries must not fall into imag chunk — was verified to
   hold in all branches; the hole is only the charging side.)
2. **Unreachable targets re-run a full 64×64 Dijkstra EVERY TICK per unit** (:983 returns
   before the store at :988): walled-off-pocket target → cached all-None flow (cheap hit) →
   imag chunk → full Array.Fill + Dijkstra → Vec2.Zero → beeline into wall → repeat forever,
   uncharged. Ten such units ≈ tens of ms/tick. **Fix:** memoize failure (store the chunk or a
   per-unit "failed for (tx,ty)" marker) + charge the cost.
3. **Stale-cache FrameAccessed dead write** (:550-554, :603-609, :702-708): `CachedFlow` is a
   struct; the touch mutates a copy → actively-used stale entries still age out. Write back or
   delete the line.
4. **Plateau fallback can create 2-cycles** (BuildDirectionField :486-505 and twin :1252-1271):
   equal-cost plateau tiles can point at each other → in-place jitter. Tie-break toward lower
   idx / epsilon direction bias so the plateau graph is acyclic.
5. **Diagonal-only sector connections unroutable** (BuildConnectivity :191-241 tests 4 cardinal
   borders; GetRoute BFS 4-dir): passage crossing exactly at a 4-sector corner → HopDist -1 →
   per-tick imag-chunk crawling. Also: "any one border tile pair" connectivity marks internally
   split sectors as connected (units survive via the expensive BFS-fallback ladder). Inherent
   to the mask scheme — see portal-graph design item.

---

## P2 — Performance (steady-state churn & fragmentation)

1. **Flow-cache key fragmentation — the biggest cost driver.** `GetFlowToTile` keys on exact
   target tile (:529); `GetFlowToMultiBorder` includes exact `clampedIdx` (:682-684). Every
   unit chasing a moving target re-runs a full sector Dijkstra each time the target crosses a
   tile; units with targets one tile apart share nothing. Each entry = 4KB + 1-4ms.
   **Short term:** quantize keys (4×4 buckets for tile flows; 8-tile bands for clamped border
   bias). **Real fix:** portal-set flows (below).
2. **Quadtree rebuild allocation churn**: `new Vec2[]/uint[]/byte[]` per tick
   (Simulation.cs:833-835) + `Subdivide` allocating 3 int[] + Entry[total] per internal node
   per tick (Quadtree.cs:218-228) ≈ 2-3 MB/s Gen0 at 600 units. Dead units are also inserted
   (no Alive filter :836-841). **Fix:** persistent buffers + reusable scratch Entry[] (or
   convert to the EnvSpatialIndex bucket-grid pattern, which is already allocation-free and
   simpler; return indices not ids — valid all tick, saves a ResolveUnitIndex per neighbor).
   Also: stale tree kept when count hits 0 (:832 early-out before clear).
3. **envEntries.Sort closure** (Simulation.cs:1762-1767): capturing lambda + delegate alloc per
   moving unit per tick — the EXACT pattern the adjacent top-K rewrite (:1705-1711) was built
   to eliminate (~24KB GC/tick). Reuse the top-K insertion for the 6 statics.
4. **`float[] probes = {...}` allocates per blocked probe per sub-step** (:2059, :2082) —
   hundreds of arrays/frame for a crowd grinding a wall at x8. Make static readonly.
5. **Per-frame registry lookups**: `_gameData?.Units.Get(UnitDefID)` (string dict) per moving
   unit per frame (:1845), same at :992 and FacingUtil.ResolveTurnSpeed (:71). Cache def
   reference (or the 3 floats) on Unit at spawn.
6. **PhysicsSystem.CheckUnitCollisions linear-scans all units per body** (PhysicsSystem.cs:254)
   — use the quadtree.
7. **Idle units pay escape/env checks every frame** (:1946, :2004-2006): 600 parked skeletons =
   600 env queries + 600 IsBlocked doing nothing. Amortize (every N frames staggered) for
   units with delta==0 that were clear last frame.
8. **Dijkstra internals**: hoist scratch buffers (16KB float[] + PQ + bool[]/Queue for the
   3 fallback BFSes per GetDirection worst case, :1560, :1647, :1697); skip stale PQ pops
   (`if (pri > cost[idx]) continue`); `Stopwatch.StartNew` per call → `GetTimestamp` deltas;
   cache-hit LRU write re-inserts into dict per unit per tick (:533) — make CachedFlow a class.
9. **Horde `UpdateStates` quadtree query per Engaged unit per tick** (HordeSystem.cs:427-429)
   at 1.5× EngagementRange just to answer "any enemy near?" — share a 2Hz scan like :453.
10. **Trample/charge at x8**: point-sampled scan + impact trigger leapfrogged when dt=0.4
    (charger moves 6-8 wu/tick vs ~1 wu radii) → phased-through victims, orbit-overshoot.
    Sub-step TickCharge like wall collision (:2033-2037) or swept-circle query.
11. **capDt clamp makes accel/turn 8× slower in game-time at x8** (:1875-1882): sim is not
    speed-invariant; x8 hordes wade through glue on direction changes. **Fix:** widen the
    existing sub-step loop to cover ORCA-resolve + accel-integrate per sub-step (bounded
    ≤1/20s each) — fixes trample too.

---

## P3 — Dead code & debt (safe deletes / small cleanups)

- **`FlowFieldManager` + `FlowField`** (World/FlowField.cs:30-161): zero callers of
  GetFlowField/SampleDirection/InvalidateAll; Simulation still allocates it (:103), Inits
  (:345), and ticks `EvictIfNeeded()` on a permanently empty dict (:777). Would be ~84MB per
  destination if ever used (whole 4097² map). Delete; **keep `FlowDir`/`FlowDirUtil`**
  (lines 7-28) — Pathfinder depends on them.
- **`EvictFlowFields`** (Pathfinder.cs:1901) and **`EvictRoutes`** (:1959): no callers (and
  O(n²) shape). Either wire as size caps or delete. Note `_routeCache` currently has NO
  eviction at all; `_unitImagChunks` (4KB each) persist until Rebuild.
- **`ClearImaginaryChunk`** (:1774): no callers — resolved by the Id-keying fix (P0-A2).
- **`GetFlowToBorder` single-border fallback is unreachable** (:1509-1514): connectivity
  symmetry guarantees the mask loop always sets ≥1 bit. Delete (~85 lines incl.
  ProcessPendingRequest case 0) or comment as intentional armor.
- **5 near-duplicate window-Dijkstra copies** (~500 lines): ComputeSectorFlow +
  BuildDirectionField (:309-510) / GetLocalChunkDirection body (:939-975) /
  RecomputeImaginaryChunkFlow (:1102-1141) / RunEscapePropagation (:1159-1214) /
  BuildChunkDirectionField (:1219-1276). A bug (e.g. the plateau 2-cycle) must be fixed in 5
  places. Consolidate to one `RunWindowDijkstra(baseX, baseY, w, h, goals, goalCosts, tier,
  cost[])` + one direction-field builder.
- **`Unit.MoveTime` is dead** (written :1632/:1657, declared UnitSystem.cs:369, never read;
  comment at :1628 is actively misleading). Delete.
- **`UpdateWolfAI` legacy machine** (~200 lines, Simulation.cs:3573+): only live user is
  WolfHitAndRunScenario (comment :957-961 admits it). Port scenario to archetype, delete.
- **3 copies of MoveToward**: SubroutineSteps.cs:375-402 ≡ Simulation.cs:3541-3563 (same 3f/
  0.01f thresholds, differ only in anim request); the inline MoveToPoint variant
  (Simulation.cs:1114-1126) DIVERGES — no 3-tile beeline band (thrashes flow machinery inside
  3 tiles), no distance normalization on approach; looks like oversight. Extract one
  `Steering.MoveToward`, layer anim on top, fold MoveToPoint onto it.
- **Duplicate Dodging clear** (:451 and :509-512 — second full-unit loop is pure waste).
- **`AStar.cs`**: test-only oracle for WallGateScenario — keep, add "test-only" header comment.
- **Static diagnostics on instance state** (Pathfinder.cs:1284-1306): cross-pollutes two
  Simulations (scenario runners); `s_keysEverSeen` static but cleared by any instance Rebuild.
- **Magic numbers** → named constants: stuck 0.33/1.6/0.3+0.5/10%; escape 20/×3/1-tile;
  wall 0.7 (`WallClearanceFactor`); probes 0.1-0.3; ORCA R*5/3f/TopK 10/MaxStatic 6/
  horizon 3; capDt 1/20; pathfind gate 3f; evict 600.
- **Misc**: `IsBlocked` assumes TileSize==1 (Simulation.cs:2167 raw coords, neighbors divide);
  truncation-vs-floor mismatch Pathfinder :830 vs TileGrid.WorldToGrid; boundary escape can
  count off-map as an escape boundary (:1707-1721); BFS fallback returns wall-blind straight
  line to found tile (:1583-1591, :1666); trample writes FacingAngle raw bypassing FacingUtil
  (TrampleSystem.cs:70,195,233 — contradicts FacingUtil.cs:7-14 doc); flying knockback victims
  turn to face flight dir (UpdateFacingAngles misses InPhysics skip, :2184-2192); env
  penetration push not depth-proportional (:2024-2029, 1-tile pop at x8 for 0.01 overlap);
  ChargeTraveled undercounts (TrampleSystem.cs:191-193); Returning→Following arrive check
  tests raw slot but unit steers to slot+DiscreteOffset up to 2.0 (HordeSystem.cs:444-446 vs
  :185-186); Chasing vs Engaged use different leash geometries (:386 center-based vs :414
  slot-based) while comments claim one rule; horde DiscreteOffset only stabilizes cache keys
  while the necromancer is PARKED (circle center slides per tick when moving → key churn).

---

## P4 — Larger design opportunities (bigger refactors, in rough order of value)

1. **Portal-set flows** (collapses P2-1 fragmentation): instead of Manhattan-biased border
   masks keyed by clamped target tile, compute flows toward the *portal set* leading to the
   next sector on the route, seeding `goalInitCosts` with each portal's remaining distance
   (ComputeSectorFlow already supports seeded goal costs). Field becomes target-independent
   per (sector, next-hop, tier) → every unit heading the same way shares one field; removes
   the lateral/extended-mask heuristics and `clampedIdx` from the key; removes the
   geometric-bias-vs-terrain-cost artifact ("stubborn straight-ish routes" on rough terrain).
2. **True portal graph (HPA*-lite)**: one node per contiguous passable border span, edges
   weighted by cached intra-sector portal-to-portal cost. Fixes: 1-tile gap weighing same as
   64-tile open border (routes thread chokepoints), hop count ignoring terrain cost, false
   connectivity of internally-split sectors, diagonal-corner blindness. Incremental step
   without full HPA*: weight sector-BFS edges by border width + center-to-center cost.
3. **MovementSystem extraction**: UpdateMovement is ~550 lines / 9 jobs / one goto
   (`__ChargeWallCollision` :1938). Seams: ResolveDesiredVelocity → ApplyStuckNudge →
   IntegrateAccel → ResolveCollision, plus an explicit per-unit movement-authority enum
   (Normal|Physics|Charge|Dodge|Jump) making the hand-back contract (reset Velocity/
   PreferredVel/StuckTime) ONE function instead of five ad-hoc sites (the P0-A/D5 bugs are
   precisely hand-off bugs). Directly enables whole-pipeline sub-stepping (fixes capDt +
   trample at x8).
4. **Progress-based stuck detection**: current detector compares ORCA output speed to
   MaxSpeed — blind to walls (not ORCA constraints → wall-pressed units NEVER flagged) and
   false-positives on slow gaits. Track actual displacement toward MoveTarget over a sliding
   window instead; covers walls and crowds with one mechanism.
5. **Walls as ORCA constraints (optional, pairs with #3)**: add nearby wall faces as static
   half-plane lines → ORCA stops steering into wall-adjacent dead ends it can't see; the
   0.7 wall radius becomes a real documented clearance margin instead of a hack. (Minimal
   alternative: keep two radii, name the constant, add wall knowledge to stuck detection.)
6. **Imag-chunk unification**: after the window-Dijkstra consolidation, an imaginary chunk is
   just "a flow field over an arbitrary window." Cache by (quantized window origin, quantized
   target, tier) instead of per-unit → clumps of trapped units share one compute, and the
   per-unit-index lifetime problem disappears structurally.
7. **Memory note**: budgeted pathfinding is OFF by default (GameSettings.cs:197) — no Dijkstra
   cap, no size cap (evictors dead), only 600-frame age-out. Large battles with many distinct
   moving targets ≈ thousands of 4KB entries + build time. Portal-set flows shrink this an
   order of magnitude.

---

## Suggested phasing

- **Phase 1 — correctness sweep** (each small & independent): stable-Id sweep (P0-A),
  horde RemoveUnit (P0-B), rebuild defer+coalesce stopgap (P0-C1), escape ring-scan (P0-D1),
  dodge wall check (P0-D2), MaxSpeed floor (P0-D3), player speed clamp (P0-D4), StuckTime
  clears (P0-D5), nudge→PreferredVel pre-solve (P1-ORCA-2), non-reciprocating neighbor flag
  (P1-ORCA-1), colliding pushDir sign (P1-ORCA-5), budget charging + unreachable memoization
  (P1-PF-1/2). Verify with drive-game + WallGate/WallTrap/SummonLag scenarios; consider a new
  scenario for the knockback-freeze and deep-water cases.
- **Phase 2 — performance**: dirty-region rebuild (P0-C2), flow-key quantization (P2-1 short),
  quadtree/env-sort/probes/def-cache alloc fixes (P2-2..7), Dijkstra internals (P2-8),
  query-radius fix (P1-ORCA-3).
- **Phase 3 — structural**: window-Dijkstra consolidation + dead-code deletes (P3),
  RVO2-canonical LP restructure (P1-ORCA-4), MovementSystem extraction + whole-pipeline
  sub-stepping (P4-3, fixes P2-10/11), then portal-set flows (P4-1) and optionally the portal
  graph (P4-2).

## Pre-existing scenario failures found during Phase 1 verification (NOT from these changes)

`trample_miss` and `trample_no_escape` fail on unmodified master with the same signature:
the boar NEVER initiates its charge (maxPhase=0 for the whole run, zero combat-log
entries). Verified by stash-and-rerun on baseline — identical failure. Both scenarios
drive the boar via legacy `AIBehavior.AttackClosest`; suspicion: a recent AI change
(e.g. the routine-transition choke point, commit 0b435eb) broke charge initiation on the
legacy path. Also odd: the spawned "Boar" logs size=2 radius=0.50 combatSpeed=0.9, while
older trample logs show selfR=0.60 — the def may not be applying. All other 10 movement
scenarios pass. Investigate as its own task.

Cross-check note: two brief-claims were DISPROVEN during review — horde slot-reassignment
churn (slots are permanent; no churn mechanism exists) and diverging beeline thresholds
between the two main MoveToward copies (both exactly 3f/0.01f; the divergence is the third
inline MoveToPoint variant). Everything else above was verified against the working tree.
