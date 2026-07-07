# Dossier: Nearest-unit / threat-target queries

Concept judged: does the codebase have N implementations of "nearest unit matching X",
and should they route through `Necroking/Game/WorldQuery.cs`? Should the quadtree back all of them?

## Canonical home: confirmed

`Necroking/Game/WorldQuery.cs:126` is not an *attempted* canonical home — it is the
documented one, and the migration is mostly done:

> "Central world-query engine: nearest-of / under-cursor / all-in-radius over units, env
> objects, and corpses. One canonical implementation of the 'best-distance scan with
> filters' that used to be re-written ad hoc per call site."

Already routed through it (no action needed):
- `Simulation.FindClosestEnemy` (Simulation.cs:3442) → `Query.NearestEnemyOf` (one-line forwarder; `FindBestEnemyTarget`:3434 is a thin CombatTarget wrapper over it).
- `SpellEffectSystem.FindClosestEnemy`/`FindClosestAlly` (SpellEffectSystem.cs:583-589) → `Query.NearestEnemyToPoint` / `NearestAllyToPoint`.
- `Game1.FindClosestEnemyToPoint` (Game1.cs:4299) → `Query.NearestEnemyToPoint`.

Its class doc also answers the quadtree question directly: **two unit paths on purpose** —
quadtree-backed for radius-bounded sim-tick queries (tree rebuilt at tick start; STALE while
paused / in map editor / for units spawned mid-tick), linear for UI-safe/unbounded queries.
So the goal is *not* "quadtree everywhere"; it is "route through WorldQuery and let the
call site pick the documented path". Env/corpse scans are linear behind the facade by
contract ("if profiling ever says otherwise, a spatial index drops in here with zero
call-site changes").

---

## Finding 1 — CONSOLIDATE (medium): the quadtree nearest-enemy scan exists 3× outside WorldQuery

Same intent ("nearest living enemy of unit i within range"), same mechanics
(QueryRadiusByFaction + ResolveUnitIndex alive re-check + best-distance loop), three copies:

| Copy | Evidence | Notes |
|---|---|---|
| `WorldQuery.NearestEnemyOf` | WorldQuery.cs:140 | Canonical. Falls back to linear when unbounded or quadtree empty. |
| `Simulation.FindNearestEnemyIndex` | Simulation.cs:3172 | Byte-for-byte the same scan (uses `_moraleScratch`). Single caller: morale rout threat check at :3135. Already diverged slightly: no linear fallback when the quadtree is empty. |
| `SubroutineSteps.FindClosestEnemy` | AI/SubroutineSteps.cs:478 | The AI-layer copy — enemy acquisition for **12 call sites** across archetype handlers (CombatUnitHandler:115, CasterUnitHandler:105/118, RangedUnitHandler:102/162, HordeMinionHandler:240/439, DeerHerdHandler:247, SoloPredatorHandler:110/122, WolfPackHandler:178, CombatTransitions:58, RatPackHandler:302). Linear fallback when `ctx.Quadtree == null` (minimal contexts). |
| `SpellVisualTestScenario.FindClosestEnemy` | Scenario/Scenarios/SpellVisualTestScenario.cs:680 | Hand-rolled linear point-scan ≡ `Query.NearestEnemyToPoint(pos, r, Faction.Undead)`. Test code, trivial. |

Why it exists: `AIContext` (AI/AIContext.cs:14) carries `Quadtree` but not the `WorldQuery`,
so the AI layer re-implemented the loop; `FindNearestEnemyIndex` looks like a straggler the
`NearestEnemyOf` migration missed (the WorldQuery doc even says "Sim-tick semantics of the
old Simulation.FindClosestEnemy" — the sibling got migrated, this one didn't).

**Proposed merge** (canonical: WorldQuery):
```csharp
// AIContext.cs — Simulation already exposes Query; AIContext already carries Quadtree,
// so the "no Simulation internals" argument doesn't block this:
public GameSystems.WorldQuery? Query;              // set in BuildAIContext (Simulation.cs:3252 and :3710)

// SubroutineSteps.cs — forward, keep the existing loop only as the null-Query fallback:
public static int FindClosestEnemy(ref AIContext ctx, float maxRange)
    => ctx.Query != null ? ctx.Query.NearestEnemyOf(ctx.UnitIndex, maxRange) : /* existing linear fallback */;

// Simulation.cs:3172 — delete body:
private int FindNearestEnemyIndex(int i, float radius) => Query.NearestEnemyOf(i, radius);
```
Call-site categories to migrate: (a) morale rout (Simulation.cs:3135) — unchanged, wrapper
forwards; (b) 12 archetype AI call sites — unchanged, wrapper forwards; (c) minimal-context
constructors (AIControl.cs:84/116, DeerReAlertWhileCalmingScenario.cs:85) — keep null
fallback; (d) SpellVisualTestScenario:680 → `sim.Query.NearestEnemyToPoint`.
`_moraleScratch` stays (still used by morale at :3084/:3086).

Effort: **S–M**. Risk: **low** — semantics match (only diff: WorldQuery falls back to
linear on an empty quadtree, which is strictly more correct). Verify with existing AI
scenarios (trample_*, deer re-alert) since enemy acquisition is on the hot path.

Severity rationale (medium): live combat-targeting logic; a rule change (e.g. "ignore
stealthed/despawning units") currently needs three edits and one copy has already drifted.
Not high because the copies are small, pinned to shared helpers (ResolveUnitIndex,
FactionMaskExt), and behaviorally aligned today.

## Finding 2 — CONSOLIDATE (low): VillageThreat.FindNearestUndead re-implements the faction-masked linear scan

`AI/VillageThreat.cs:16` is exactly `WorldQuery.NearestUnitLinear(pos, range,
Faction.Undead.Bit())` — currently only reachable as `NearestAllyToPoint`
(WorldQuery.cs:210), whose name lies for this use ("nearest unit of exactly faction X",
not "ally"). Call sites: WatchdogHandler.cs:40, VillagerHandler.cs:43/118.

Merge: after Finding 1 lands (Query on AIContext), the body becomes a forwarder. Add an
honestly-named alias first:
```csharp
// WorldQuery.cs
public int NearestOfFaction(Vec2 pos, float range, Faction f, int excludeIdx = -1)
    => NearestUnitLinear(pos, range, f.Bit(), excludeIdx);   // NearestAllyToPoint becomes a synonym
```
Keep the `VillageThreat` wrapper — it documents real game policy (villages fear the undead
specifically, not cross-faction wildlife); caller owns the data, WorldQuery owns the scan.
Bonus: at horde scale (hundreds of undead) this per-villager linear scan is the one that
would actually benefit from the quadtree mask path (`NearestUnit` with `Faction.Undead.Bit()`)
— that swap becomes a one-liner once routed. Effort **S**, risk low.

## Finding 3 — CONSOLIDATE (low): trap targeting re-implements NearestEnemyToPoint

`World/EnvironmentSystem.cs:1120 FindTrapTarget` is a 12-line linear scan ≡
`Query.NearestEnemyToPoint(trapPos, 2.5f, trapFaction)`. Sole caller: `UpdateTraps`
(EnvironmentSystem.cs:1039), invoked from Game1.cs:3688 as
`_envSystem.UpdateTraps(_clock.WorldDt, _sim.Units)` — `_sim.Query` is in hand; pass it as a
third parameter (env system is below Simulation in layering, so pass the query in rather
than reaching up). The owner→faction mapping (`trapOwner == 0 ? Undead : Human`) stays with
the caller — that's trap policy, not scan mechanics. Also future-proofs traps for the
spatial-index drop-in the WorldQuery doc promises. Effort **S**, risk low.

## Finding 4 — KEEP_SEPARATE (low): AwarenessSystem.FindClosestThreat has a variable per-candidate range

`AI/AwarenessSystem.cs:149`: the *distance bound itself* varies per candidate — sneaking
targets ×0.5, running targets ×1.5, with the quadtree query widened ×1.5 to not miss
runners. WorldQuery's `IUnitQueryFilter` gates candidates but cannot vary the bound;
squeezing this through a filter struct would force the filter to recompute distance inside
`Match` and hide the detection-modifier logic. That is structural variance (a different
query shape: variable-radius nearest), not a data-level filter — per CLAUDE.md, don't
abstract it. AwarenessSystem is also a standalone static system fed `(UnitArrays, Quadtree)`
directly. Leave as is; at most add a cross-reference comment.

## Finding 5 — KEEP_SEPARATE (low): squad-scoped picks are not world queries (labeler over-match)

- `AI/WolfPackHuntAI.cs:250 NearestQuarry` — nearest over a **squad-member ID list**
  (8-line loop over `squad.Members`), returns a unit *Id* not an index. WorldQuery indexes
  the world; supporting "nearest among these IDs" would be a new API for two tiny loops.
- `AI/RatPackHandler.cs:259 PickGangUpTarget` — target-*selection policy* (join a packmate's
  existing victim → alert target → nearest enemy), not a distance scan; its actual
  nearest-enemy fallback already delegates to `SubroutineSteps.FindClosestEnemy` (:302), so
  it inherits Finding 1's consolidation for free. The no-squad packmate scan is a
  policy-filtered scan of pack members, not a threat query.

## Finding 6 — KEEP_SEPARATE (low): FindAliveNecromancerIndex is not a distance query (labeler over-match)

`Movement/UnitModel.cs:572` is a first-match liveness scan (no position, no distance), and
its doc comment already disambiguates it from `Simulation.NecromancerIndex` (the HUD cache).
Nothing to merge.

---

## Verdict on the framing question

WorldQuery **is** the canonical home and most of the codebase already respects it. The real
gaps are three copies of the quadtree enemy scan (Finding 1) plus two small linear scans
(Findings 2–3), all mechanically routable. The quadtree should **not** back all of them:
WorldQuery's two-path contract (quadtree = sim-tick fresh, linear = UI-safe/paused-safe) is
deliberate and load-bearing; route the stragglers through the facade and upgrade individual
paths (village-threat scan is the best quadtree candidate) behind it.
