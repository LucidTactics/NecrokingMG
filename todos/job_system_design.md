# Worker Job System — Design Spec + Build Record

Status: **IMPLEMENTED (P0–P5) 2026-06-26.** All six jobs + UI work end-to-end.
Steps: (1) survey ✅ (2) clarify ✅ (3) rough design ✅ (4) implement ✅ (5) polish ✅.

## What shipped
- **Data spine** — `data/jobs.json` + `Necroking/Game/Jobs/{JobDef,JobState,JobRegistry,WorkerSystem}.cs`.
  Building defs gained `HostsJob/StoredResource/StorageCap/IsWorkerHome/WorkerSlots`;
  `PlacedObjectRuntime.StoredAmount` mirrors the per-building stockpile.
- **Buildings** (placeholder sprites) — empty_grave, mushroom_pile, corpse_pile,
  poison_vat, harvesting_table, necro_table, alchemist_table. All `PlayerBuildable`.
- **Workers** — any humanoid undead (UndeadCategory.Human) assigned to an Empty
  Grave becomes a worker (Archetype=Worker, id 12). `WorkerHandler` (AI) drives the
  Collect + Process FSMs. `WorkerSystem` is the dispatcher + stockpile owner.
- **Jobs** — Forage Mushrooms / Collect Corpses / Poison Berries (collect kinds:
  foragable/corpse/berry); Break Down (Corpse→Essence+Reagent co-products),
  Reanimate (Corpse→unit via QueueReanimRise), Make Potions (Mushroom→one potion
  per craft by maintain-stock target, `outputChoice`).
- **UI** — `Necroking/UI/JobBoardUI.cs` ('O' hotkey): priority tiles (hidden until
  building exists), workers/cap badge, storage/population bar, ^/v + drag reorder,
  expand→cap stepper + maintain-stock target steppers. `GraveRosterUI.cs` (click a
  grave): housed worker + Unassign, assignable humanoid list + Assign.
- **Dev verbs** — `worker_scene` (full economy), `worker_demo`, `jobs` (+StockReport),
  `assign_worker`/`unassign_worker`, `place_obj`, `ui_job_board [jobId]`, `ui_grave_roster`.

## Placeholder economy (flesh out later — user flagged as TBD)
- Break Down yields Essence (global pool, clamped at MaxEssence) + a Reagent that
  nothing yet consumes. Poison goes to a poison_vat that nothing yet consumes.
- Reanimate spawns a fixed `skeleton` (spawnUnitDefId) and is gated by a soft
  `MaxUndead=150`, NOT the real HumanCap/MonsterCap horde caps. Wire HordeCapTracker
  later if reanimation should respect the necromancer's actual cap.
- Corpses are carried PHYSICALLY during Collect, then abstracted to a "Corpse"
  count in the pile; Process jobs withdraw the abstract count.
- Capability gating (`requiredCapability` / unit-type tags) is a hook only — unused.

## Remaining polish (non-blocking)
- Sprites: `tools/gen_building_sprites.py` is ready; needs the PixelLab secret at
  `E:\Nightfall\Corpobot\art_prototype\.env.secrets` (absent here). After generating,
  repoint each def's texturePath + recheck pivot/scale.
- Worker carrying a corpse overlaps the body (no carry animation) — cosmetic.
- Dedicated Hauling job + per-output drag (only ^/v + target steppers today).
- The reusable ordered-tile widget is currently inline in JobBoardUI, not extracted.

---
## Original design (for reference)


## 1. Pillars
A colony-management work layer for the necromancer's undead. The player **sets intent**
(which jobs matter, how many workers, how much to stockpile) and the colony **executes
physically** — undead walk out, gather, haul, and craft. North-star: *anticipation that
gets rewarded* — you queue priorities and watch the graveyard come to life, no
micromanagement.

## 2. Locked decisions (from clarification Q&A)
| # | Decision |
|---|----------|
| Assignment | **Auto-assign pool.** Player sets job *priority order* + per-job *worker cap*. Dispatcher fills jobs top-down from the shared idle pool; a capped/storage-full/work-exhausted job releases its workers down the priority chain. No per-individual micromanagement. |
| Logistics | **Full physical logistics.** Goods are carried building-to-building by workers (reuses corpse-carry + foragable-arc). |
| Hauling | **Producer fetches its own inputs.** No separate hauler role yet (deferred — `Dedicated Hauling` job can be added later as its own tile). |
| Worker identity | **Any humanoid undead assigned to an Empty Grave** becomes a worker. Worker count = filled graves. Roster UI on the grave picks an unassigned humanoid. |
| Sub-jobs | **Maintain-stock targets + priority order.** Per output: a target stock; produce the most-below-target first; pause when all met. Reuses the SAME ordered-tile UI component as the job board. |
| Breakdown output | **Essence + Reagents** (placeholder resources, to be fleshed out later). |
| Capability gating | Build the **hook** now (a job *may* declare a required unit-type/tag), leave it **unused** — all workers humanoid & fungible for v1. |

## 3. Data model (new)

### ResourceType (registry-backed, not a hard enum)
Stockpiled goods. Start: `Mushroom`, `Corpse`, `Essence`, `Reagent`, `Poison`, plus the
existing potion item-ids (potions stockpile by item-id). Keep it a string/id registry so
we can add types without recompiling — mirror how `ForagableType` is already a string.

### JobDef (static template, data-driven — new `data/jobs.json`)
```
id              : string         // "forage_mushrooms"
displayName     : string
icon            : string
archetype       : Collect | Process       // drives the worker state machine
buildingDefId   : string         // env def this job is tied to ("mushroom_pile")
workerSlotsPer  : int = 1         // worker capacity contributed PER building instance
requiredCapability : string = "" // unit-type or tag filter; "" = any humanoid (v1: always "")
// Collect:
sourceForagable : string         // ForagableType to gather ("Mushroom"), or "corpse"/"berry_bush"
// Process:
inputs          : [{resource, amount}]   // pulled from source stockpiles via producer-fetch
outputs         : [{resource/itemId, amount}]  // 1+ ; >1 => multi-output (sub-job targets)
processTime     : float          // seconds at the building (reuse def.ProcessTime if 1:1)
spawnsUnit      : bool           // Reanimate: output is a spawned unit, not a stockpile good
```

### JobState (runtime, per save)
```
defId
priority        : int            // player drag-order; lower = higher priority
workerCap       : int            // player-set, clamped to buildingDerivedMax
assignedWorkers : list<unitId>
outputTargets   : map<outputId, {priority, targetStock}>   // multi-output only
active          : bool (derived) // building exists && !storageFull && workAvailable && cap>0
```

### Building extension (on EnvironmentObjectDef / TableCraftState)
Reuse existing `IsBuilding`, `Input1/Output`, `ProcessTime`, `CorpseSlots`. ADD:
```
HostsJob        : string = ""    // JobDef.id this building enables ("" = none, e.g. Empty Grave is none... see below)
StoredResource  : string = ""    // ResourceType this building stockpiles ("" = none)
StorageCap      : int            // max units of StoredResource
IsWorkerHome     : bool          // Empty Grave: houses 1 worker
WorkerHomeSlots : int = 1
```
Runtime per-instance state (extend `EnvObjectInstance`/`TableCraftState`):
`StoredAmount : int`, `HousedWorkerId : int`, `ReservedBy` (which worker is mid-fetch, to
avoid two workers grabbing the last mushroom).

> Note: Empty Grave hosts **no job** — it's pure worker housing. The dispatcher's worker
> pool = every grave's `HousedWorkerId`.

## 4. Building catalog (v1)
| Building | Role | Stores | Worker slots | Sprite? |
|----------|------|--------|--------------|---------|
| **Empty Grave** | Worker home (1 each) | — | houses 1 | needs gen |
| **Mushroom Pile** | Forage storage | Mushroom | 1 forager / pile | needs gen |
| **Corpse Pile** | Corpse storage | Corpse | 1 collector / pile | needs gen |
| **Harvesting Table** | Break down corpses | Essence + Reagent | 1 / table | needs gen |
| **Necro Table** | Reanimate corpses | — (spawns units) | 1 / table | **HAS sprite** |
| **Alchemist Table** | Make potions | Potions | 1 / table | needs gen |

Building-derived worker max for a job = Σ `workerSlotsPer` over all built instances of its
`buildingDefId`. (3 mushroom piles ⇒ forage cap maxes at 3.)

## 5. Job catalog (v1)
| Job | Building | Archetype | Source → | Output | Notes |
|-----|----------|-----------|----------|--------|-------|
| Forage Mushrooms | Mushroom Pile | Collect | mushroom foragables in world | Mushroom→pile | reuses ForagableSystem arc |
| Poison Berries | (Mushroom Pile or own bldg — TBD) | Collect | berry bushes | Poison reagent | reuses PoisonBerryBush(); output dest TBD |
| Collect Corpses | Corpse Pile | Collect | corpses in world | Corpse→pile | reuses corpse-carry |
| Break Down Corpses | Harvesting Table | Process | Corpse Pile | Essence + Reagent | producer fetches a corpse |
| Reanimate Corpses | Necro Table | Process | Corpse Pile | Undead unit | reuses TableCraftingSystem + QueueReanimRise; capped by unit limit, not storage |
| Make Potions | Alchemist Table | Process | Mushroom Pile (+Reagent/Poison) | Potions (multi-output → targets) | reuses potions.json recipes |

**Shared-resource tension (intentional):** Corpse Pile feeds BOTH Break Down and Reanimate
— priority order decides which starves when corpses are scarce.

## 6. Dispatcher (the brain)
Runs on a coarse tick (~0.5 s, not per-frame). Pseudocode:
```
jobs = activeJobs().sortBy(priority)            // active = building exists, not full, work available, cap>0
for job in jobs:
    demand = min(job.workerCap, buildingDerivedMax(job), workAvailable(job))
    keep first `demand` of job's currently-assigned workers; release the rest to pool
pool = allGraveWorkers - stillAssigned
for job in jobs (highest priority first):
    while job.assigned < job.demand and pool not empty and capabilityOK(worker, job):
        assign pool.pop() to job
idleLeftovers -> return to home grave (idle anim)
```
- **Preemption: lazy.** A worker finishes its current carry/craft cycle before re-evaluation
  (no dropping goods mid-haul). Re-eval happens at cycle boundaries + on the coarse tick.
- **`workAvailable`**: Collect → any uncollected source in range / pile not full. Process →
  inputs available in source stockpile AND output stockpile not full (or unit-limit not hit).
- **Storage-full** flips a job inactive → its workers spill to the next priority job. This is
  the "high-priority capped job releases its workers" behavior from the brief.

## 7. Worker execution state machines
Reuse `AI/WorkRoutine.cs` (Walk→Start→Loop→End) for the stationary "work" phase; reuse
corpse-carry + foragable arc for hauling. New `CorpseWorker`→ generalize to a
`WorkerHandler : IArchetypeHandler` driven by the assigned JobDef.

**Collect archetype:** `Idle → reserve nearest source → travel → harvest (arc/carry) →
travel to storage building → deposit (StoredAmount++) → repeat`.

**Process archetype (producer-fetch):** `Idle → reserve inputs in source stockpile →
travel → pick up inputs → travel to processing building → WorkRoutine loop (processTime) →
emit output (stockpile deposit OR spawn unit) → repeat`.

Reservation (`ReservedBy`) prevents two workers claiming the same last corpse/mushroom or
overflowing a near-full pile.

## 8. UI (3 surfaces — see mockups)
1. **Job Board** (hotkey / HUD button): vertical list of **job tiles**, ordered by priority.
   Each tile: icon, name, `workers / cap` number badge, storage fill bar, drag handle.
   - **Drag** the tile to reorder priority.
   - **Click the number badge** → Job Detail dialogue.
   - Jobs with **no building** are hidden entirely.
2. **Job Detail dialogue:** worker-cap stepper (clamped to building max), required-capability
   line (hidden when none), and for multi-output jobs the **same ordered-tile component**
   listing outputs, each with priority + target-stock stepper. Optional: list of currently
   assigned workers.
3. **Empty Grave roster** (click a grave): currently-housed worker (with Unassign), and a
   scrollable list of **unassigned humanoid undead** to assign into this grave.

**Consolidation win:** build ONE reusable `OrderedPriorityTileList` widget (drag-reorder +
per-tile cap/target). Used by both the Job Board and the per-job output targets. Register it
in `memory/standard_patterns.md` when built.

## 9. Reuse map (don't rebuild)
| Need | Existing code |
|------|---------------|
| Building defs / process slots / storage scaffolding | `EnvironmentObjectDef`, `TableCraftState` (EnvironmentSystem.cs) |
| Corpse carry / drop | `CorpseInteractionManager`, `Corpse.DraggedByUnitID` |
| Foragable gather + arc | `ForagableSystem` (FindNearest/StartCollection) |
| Reanimate at table | `TableCraftingSystem.CompleteCraft` + `Game1.QueueReanimRise` + `reanim_smoke` |
| Potion recipes | `potions.json`, `PotionRegistry` |
| Work walk+anim FSM | `AI/WorkRoutine.cs` |
| AI dispatch slot | `AIBehavior.CorpseWorker` (stub) → new `WorkerHandler : IArchetypeHandler` |
| Building placement UI | `BuildingMenuUI` |
| Player resource (Essence) | `PlayerResources` |

## 10. Phasing plan (proposed)
- **P0 — Data spine.** ResourceType registry; `data/jobs.json` + `JobDef`/`JobState`;
  building extension fields; per-instance stockpile state. No behavior. New env defs for the
  5 missing buildings (placeholder sprites OK).
- **P1 — Vertical slice (1 collect job).** Empty Grave assignment (incl. roster UI minimal),
  Forage Mushrooms end-to-end: assign worker → forage → deposit to pile → respect storage
  cap. Dispatcher with a single job.
- **P2 — Dispatcher + 2nd collect job.** Priority/cap spill logic; add Collect Corpses.
  Verify capped-job-releases-workers.
- **P3 — Process jobs.** Break Down (Essence+Reagent placeholders), Reanimate (wire to table
  crafting), Make Potions (producer-fetch from Mushroom Pile, multi-output targets).
- **P4 — Full UI.** Job Board tiles (drag, caps, fill bars), Job Detail w/ sub-job targets,
  polished Grave roster. The reusable `OrderedPriorityTileList`.
- **P5 — Art + polish.** PixelLab sprites for the 5 buildings; balance; enable capability
  tags if desired; consider Dedicated Hauling job.

## 11. Sprite generation (PixelLab) — for P5
Pipeline confirmed: REST `POST https://api.pixellab.ai/v1/generate-image-pixflux`, bearer
secret at `E:\Nightfall\Corpobot\art_prototype\.env.secrets`, returns base64 PNG. Template:
`tools/gen_buff_icons.py`. For **map objects** (vs 96px icons): larger size, and we need
**transparent/backgroundless** output — investigate the pixflux transparent-bg option or
post-process with a bg key; use the existing **Necro Table** + graveyard props (tombstones,
crypts) as style references so the new buildings match. New script: `tools/gen_building_sprites.py`.

## 12. Open / deferred
- Exact Essence/Reagent yields + what Reagents feed (placeholder for now).
- "Poison Berries" output destination & whether it needs its own building.
- Dedicated Hauling job (separate dispatch layer) — deferred.
- Capability-tag enforcement — hook only in v1.
- Worker behavior under raid (fight/flee) — out of scope for v1, note for later.
- Worker death → grave empties → dispatcher refills from pool (should fall out naturally).
