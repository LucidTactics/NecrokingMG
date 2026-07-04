# Pathfinding & Sim Scaling Roadmap (toward ~100× units on a vast map)

Context (2026-07-04): the lag diagnostic fixed the two acute problems — GPU routing
(`cdee4e6`: hybrid laptops now auto-select the discrete GPU) and pathfinder sim spikes
(`cfed962`: LOS-first steering + analytic/resumable portal matrices; sim tick max went
34 ms → 6.3 ms optimized, 60 fps sustained on the default map). This doc holds the
**next** steps, for when unit counts grow toward ~20k spread over a much larger map.
The portal graph itself is the right hierarchy for a vast map (route memory scales with
the corridor, not the map) — the work below is about *when* and *where* that machinery
runs, not replacing it.

Ranked by leverage:

## 1. AI LOD for distant units (biggest lever)

At 100× units, most units are far from the camera/player *by definition*. Distant herds
should not run per-tick archetype AI, ORCA, pathfinding, or animation:

- Define a LOD boundary (camera distance and/or "no player-faction unit within N sectors").
- Far units tick at 1–4 Hz with coarse statistical behavior (wander drift, herd cohesion
  via squad center, no ORCA, no GetDirection — straight moves with terrain speed only).
- Near/far transitions must be hysteretic (avoid popping at the boundary).
- Combat/aggro forces a unit to near-LOD regardless of distance.

## 2. Async pathfinder worker thread (structural spike immunity)

Move ALL Dijkstra/route/matrix work off the sim thread. The sim only: enqueues requests,
reads *completed* flow fields, and beelines (LOS already exists as the fallback) while a
request is pending. The budget system then becomes queue prioritization instead of a
time cap.

- `Pathfinder` is already one self-contained class with instance scratch buffers — one
  worker thread with its own scratch is the natural unit. Job in = FlowKey + seeds;
  result out = immutable `CachedFlow` swapped into the cache with a lock or via a
  concurrent handoff queue drained in `BeginTick`.
- Invalidation fencing: `Rebuild`/`InvalidateRegion` (wall/env changes) must quiesce or
  version-stamp the worker so a stale compute can't land after a grid change (version
  counter checked on result landing is simplest).
- Multiplayer note: `Net/` is client-visualization (not lockstep), so async pathfinding
  results landing on different frames is fine.

## 3. Direction sampling cadence (per-unit repath throttling)

Even all-cache-hit `GetDirection` calls cost ~0.5–1 µs each → 20k units/tick ≈ 10–20 ms.
Re-sample each unit's flow direction every 5–10 ticks (staggered by `unitId % N` so cost
is flat), cache the direction vector on the unit, and let ORCA smooth between samples.
Force an immediate re-sample on: target change, sector crossing, stuck detection.

## 4. Quadtree incremental updates

`RebuildQuadtree()` rebuilds from scratch every tick — fine at 227 units (already shows
5–8 ms spikes in Debug), not at 20k. Options: rebuild every N ticks (positions change
slowly relative to query tolerance), incremental reinsertion of moved units only, or a
loose quadtree / spatial hash grid (cell = ORCA query radius) which makes updates O(1).

## 5. Flow-field memory pressure at scale

`_flowCache` fields are 4 KB each (64×64 FlowDir). Thousands of concurrent (sector, dest,
tier) pairs → tens of MB, fine; but eviction (`EvictStaleFlowFields`, 600-frame age-out)
should become memory-budgeted (LRU cap) instead of purely age-based once dest diversity
grows. Same for `_routeCache`/`_portalMatrixCache` (matrices are tiny; routes are sparse
— low risk, just watch `cache:`/`stale:` in the perf.log `pf={}` line).

## Rendering at scale (separate track, smaller risk)

Visible-unit count is bounded by the camera regardless of world population, so draw cost
does NOT scale with 100× units. The known draw items (independent of scale):
- **Shadow quads are per-quad `DrawUserIndexedPrimitives` calls** (~2.4 s of a 20 s trace
  inside `ShadowRenderer.DrawShadowQuad`) — batch them into the sprite queue. Top draw fix.
- World-load warmup: first sim tick ~230 ms + a ~66 ms first-route tick at ~3 s
  (cold caches + first-touch JIT). Prewarm during the load screen if it ever bothers.

## Measurement tooling (already in place — use it)

- `perf [reset]` dev command: frame/sim/draw/present avg/p50/p95/max + GC counts.
- `log/perf.log` `pf={calls,los,hits,miss,...,dj_ms,pend}` line on every ≥3 ms tick —
  `los:` shows the LOS shortcut hit count; `dj_ms` should stay ≤ ~budget+1 row.
- `pass list` / `pass off <name>` for draw-side bisection.
- The scenario guardrails: pathfinding_test, wall_gate, wall_trap, move_to_point,
  portal_route_scale, summon_lag, deer_flee_no_slide, horde_chase_leash.
