# Dossier: Nearest-env-object / building / foragable queries

Concept: "nearest X" scans over `EnvironmentSystem` objects (buildings, foragables, berry
bushes, mushrooms) and closely-related corpse scans.

## Context: the canonical API already exists and is documented

`Necroking/Game/WorldQuery.cs` (reached as `_sim.Query`) IS the canonical filtered-nearest
engine, and `docs/locate-behavior/world.md:62-70` explicitly declares it so (dated
2026-07-06 — today) and even names the remaining unmigrated call sites:

> "Nearest X / X under cursor / all X in radius" over units, env objects, and corpses has
> ONE canonical home: Necroking/Game/WorldQuery.cs … Don't write new ad-hoc `bestSq`
> scans — and when touching the remaining unmigrated ones (WorkerSystem finds,
> CorpseInteractionManager, SpellCasting/Projectile corpse scans, VillageThreat,
> BoarForageAI), migrate them here.

The API shape is exactly what the review question asks for:
- `WorldQuery.NearestEnvObject<TF>(Vec2 pos, float range, in TF filter)` (WorldQuery.cs:237)
  with struct-generic zero-alloc filters (`IEnvQueryFilter`, WorldQuery.cs:19)
- by-def overload `NearestEnvObject(pos, range, defIndex, EnvGate)` (WorldQuery.cs:255)
- prebuilt filters: `EnvByDefIndex` (:50), `EnvWorkerHomes` (:70), `EnvForagables` (:82),
  `EnvBerryBushes` (:89); `EnvGate` flags Alive/Built/Visible (:40)
- documented escape hatch: "a call site with an odd extra condition … writes its own small
  struct next to the caller instead of widening these" (WorldQuery.cs:13-15)

So this concept is a **migration in flight**, not a missing abstraction. The findings below
are the stragglers.

## Evidence corrections (labeler over-matches)

- `ForagableSystem.FindNearest` (ForagableSystem.cs:87-88) **already delegates** to
  `_sim.Query.NearestEnvObject(fromPos, maxDist, new EnvForagables())`. Not a duplicate.
- `Game1.Crafting.cs:28 FindNearestForagable` is a one-line wrapper over
  `_foragables.FindNearest` → WorldQuery. Not a duplicate.
- `WadingEditorPopup.cs` `FindNearestForagable` **does not exist** (grep finds no
  `FindNearest*`/`FindClosest*`/env scan in that file). Labeler hallucination.
- "~15 hand-rolled linear scans" is overstated: the real remaining count is ~9 nearest
  scans + 1 count scan (below), plus one structurally-different scored search (deer).

---

## Finding 1 — WorkerSystem's six private find scans (CONSOLIDATE, high)

`Necroking/Game/Jobs/WorkerSystem.cs`:
- `FindDepositBuilding` (:344) — Built + StoredResource match + has space, nearest
- `FindWithdrawBuilding` (:363) — Built + holds ≥ minAmount, nearest
- `FindHostBuilding` (:380) — def match + Built + output room, nearest
- `FindNearestForagable(JobDef)` (:410) — Visible + IsForagable + optional ForagableType match
- `FindNearestBerryBush` (:428) — Alive + IsBerryBush + BerryState==Berries
- `FindNearestCorpseObj` (:446) — corpse scan, excludes Dissolving/Consumed/Reanimating/Dragged
- (related) `CountBuildings` (:330) — linear def+Built count

Each is a verbatim re-write of the `bestSq` scan WorldQuery centralizes. Concrete
duplication already present:
- `Built(i)` (:324) == `EnvGate.AliveBuilt` (WorldQuery.cs:44-46, 61-62).
- `FindNearestBerryBush` is an **exact semantic duplicate** of the existing
  `EnvBerryBushes` filter (WorldQuery.cs:89-97) — same three gates. If berry-eligibility
  rules change (e.g. poisoned-berry handling), there are now two places to change.
- `FindNearestCorpseObj` duplicates `WorldQuery.NearestCorpse` + `CorpseExclude` (:282,
  :27-36) with a **subtly different exclusion set: it does not exclude Bagged corpses**
  (CorpseExclude.Free does). That is either a live divergence bug (corpse-collect workers
  will path to a corpse already in a bag) or an undocumented intentional difference —
  exactly the kind of drift the canonical API exists to prevent. Migration must decide
  explicitly: `CorpseExclude.Dissolving|Consumed|Dragged|Reanimating` (parity) vs
  `Free|Reanimating` (bug fix).

### Proposed migration (canonical home: WorldQuery, filters live next to WorkerSystem)
```csharp
// in WorkerSystem.cs, per the WorldQuery escape-hatch doc:
readonly struct DepositBuildings : IEnvQueryFilter {
    readonly WorkerSystem _ws; readonly string _resource;
    public bool Match(EnvironmentSystem env, int i) =>
        _ws.Built(i)
        && string.Equals(env.GetDef(env.GetObject(i).DefIndex).StoredResource, _resource, OrdinalIgnoreCase)
        && _ws.TotalStored(i) < _ws.BuildingCap(i);
}
// then: FindDepositBuilding => _sim.Query.NearestEnvObject(from, 0f, new DepositBuildings(this, resource));
```
Same pattern for Withdraw/Host (filters capture `this` — struct holding a class ref is
fine and alloc-free). `FindNearestBerryBush` → `_sim.Query.NearestEnvObject(from, 0f,
new EnvBerryBushes())` (delete the method body). `FindNearestForagable(def)` → small
`ForagablesOfType` struct (EnvForagables + type match). `FindNearestCorpseObj` →
`NearestCorpse(from, 0f, exclude)` + map list index → `Corpses[idx].CorpseID` (the worker
path stores CorpseID; NearestCorpse returns a list index — one line at the call site).

- Call sites to migrate: all internal to WorkerSystem (private methods; public entry
  points `FindDepositBuilding`/`FindWithdrawBuilding`/`FindHostBuilding`/`FindNearestSource`
  keep their signatures — callers unaffected).
- Effort: **M** (six methods, three new filter structs, parity testing of worker jobs).
- Risk: low-medium — behavior parity is mechanical except the Bagged-corpse decision,
  which must be called out to the user (potential intentional difference).

## Finding 2 — Straggler one-off scans: Game1.FindBerryBushNear, BoarForageAI.FindNearestMushroom, MapEditorWindow.FindClosestObject (CONSOLIDATE, medium)

- `Necroking/Game1.cs:4133 FindBerryBushNear` — gate is Alive + IsBerryBush +
  BerryState==Berries: an **exact duplicate of `EnvBerryBushes`**. Replace body with
  `_sim.Query.NearestEnvObject(worldPos, maxRadius, new EnvBerryBushes())`.
  Call sites: Game1.Spells.cs:471, :474 (unchanged — wrapper keeps signature). Effort S.
- `Necroking/AI/BoarForageAI.cs:121 FindNearestMushroom` — Visible + IsForagable +
  IsMushroom(def). `sim` is in scope in the caller (Update uses `sim.AddBoarBelly`,
  `sim.AIForageMove`). Write a caller-side `EnvMushrooms : IEnvQueryFilter` struct
  (reusing the existing `IsMushroom` predicate) → `sim.Query.NearestEnvObject(myPos,
  ForageRange, new EnvMushrooms())`. Explicitly named as unmigrated in world.md:70.
  Effort S. Sim-tick path — struct filter keeps it zero-alloc as required.
- `Necroking/Editor/MapEditorWindow.cs:6990 FindClosestObject` — match-ALL objects
  (deliberately no Alive/Built/Visible gate: the editor must pick dead/collected objects
  too). Migrate via a trivial `EnvAny : IEnvQueryFilter { Match => true }` (could live in
  WorldQuery.cs as a prebuilt) through `_game._sim.Query`. Env queries in WorldQuery are
  linear, so it's editor/paused-safe per the class contract (WorldQuery.cs:119-121).
  Lowest priority of the three; fine to fold into the same sweep. Effort S.

Severity medium: two of the three duplicate gameplay eligibility gates that exist as
named filters already (berry bushes), one is cosmetic (editor pick).

## Finding 3 — DeerHerdHandler.FindNearbyBush (KEEP_SEPARATE)

`Necroking/AI/DeerHerdHandler.cs:936`. Superficially a bush scan, but it is **not a
nearest query**:
- scored selection, not min-distance: poisoned bushes get a `PoisonedBushAttractBonus`
  distance bias (doc comment :932-935);
- accepts BerryState Berries **or Poisoned** (deer can't tell), unlike `EnvBerryBushes`;
- dual-center constraint: within `BushSearchRadius` of the deer's **spawn** position AND
  ≥ `minDist` from its **current** position (:943-945, :970-974);
- semi-random iteration offset (`unitIdx*37 + frame/60`, :953-957) so the herd doesn't
  all converge on the same bush — order-dependent tie-breaking a nearest-API can't express;
- returns a pathable feed spot adjacent to the bush plus radius, not the object index alone.

Per CLAUDE.md's consolidation rule this is structural variance (different selection
algorithm, not a data-level filter). Forcing it through `NearestEnvObject` would either
lose the herd-distribution behavior or turn WorldQuery into a scoring framework. Leave it.

## Finding 4 — Should EnvSpatialIndex back the env queries? (KEEP_SEPARATE / not now)

`Necroking/World/EnvSpatialIndex.cs:16` is a **collision** index, not a general env index:
- `Rebuild` skips objects with `CollisionRadius <= 0` (:56) — many foragables/mushrooms
  would simply be absent from it;
- it skips `Collected` and dead objects (:52), while e.g. the editor pick needs everything;
- world.md:58-59 documents it as "ORCA-only consumer";
- it stores collision centres (offset from placement point), while WorldQuery measures
  from the placement point (X,Y) — answers would subtly differ.

WorldQuery's contract already reserves the upgrade path: "Env-object and corpse queries
are linear scans behind this facade; if profiling ever says otherwise, a spatial index
drops in here with zero call-site changes" (WorldQuery.cs:119-121). Env object counts are
small (linear scan = microseconds); the perf-profiling memory shows no env-query hotspot.
Do NOT wire EnvSpatialIndex in now, and do not generalize it — keep it ORCA-scoped. The
facade means this decision is reversible for free.

## Not covered here (named by world.md as same-family stragglers)

`CorpseInteractionManager`, `SpellCasting`/`Projectile` corpse scans, `VillageThreat` —
corpse/unit scans, outside this concept's env-object scope but part of the same documented
migration list; whoever does Finding 1 should check them off opportunistically.

## Constraint check
No Necroking/Net/ involvement; no rendering; no map-content changes. All migrations are
internal call-graph refactors with unchanged public signatures.
