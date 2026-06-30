# Jobs & workers — the worker/job economy

The undead **worker** economy: turning idle undead into workers (housed in graves),
the runtime job list with priorities/caps, per-building stockpiles, and the dispatcher
that hands the worker pool to active jobs. Two UI layers drive it. The per-unit FSM that
actually *executes* a job lives in `AI/WorkerHandler.cs` (policy/queries here, behavior
there).

## Two distinct "assignment" concepts (don't confuse them)

1. **Worker creation — grave housing.** An idle humanoid undead is *assigned into an
   empty grave*, which flips its `Archetype` to `Worker`. This is the **Grave Roster**
   (`GraveRosterUI`), backed by `WorkerSystem.AssignWorker / UnassignWorker`. Manual,
   per-grave, one unit at a time. **No auto-assign-to-grave exists yet.**
2. **Job dispatch — pool → jobs.** Already-created workers (the whole `Worker`-archetype
   pool) are auto-distributed across *active jobs* every 0.5s, top-down by job priority,
   capped per job. This is the **Job Board** (`JobBoardUI`) + `WorkerSystem.Dispatch`.
   This runs automatically and continuously; it is not a manual action.

## Files

### `Necroking/Game/Jobs/WorkerSystem.cs`
The job-system brain. Owns the runtime `JobState` list, grave→worker assignment,
per-building stockpiles, and the coarse-tick dispatcher. Key members:
- **Grave assignment (worker creation):** `AssignWorker(uint unitId, int graveObjIdx)`,
  `UnassignWorker(uint unitId)`, `UnassignGrave(int)`, `IsEligibleWorker(int)`,
  `IsGraveOccupied(int)`, `HousedWorker(int)`, `IsWorkerHomeDef(int)`.
- **UI candidate query:** `UnassignedWorkers()` → `List<WorkerCandidate>` — eligible
  undead *not yet housed* (the Grave Roster's assignable list).
- **Dispatcher (pool → jobs, the existing auto-assignment):** `Update(float dt)` ticks
  every `DispatchInterval` (0.5s) and calls private `Dispatch()`, which clears
  `js.AssignedWorkers`, gathers all `Worker`-archetype units into `_pool`, finds active
  jobs via `IsJobActive`, sorts by `Priority`, and hands out workers up to
  `min(WorkerCap, DerivedMax)` per job via `SetWorkerJob`. **This is the auto-assignment
  to reuse — call `Dispatch()` (make it public or add a wrapper) to re-run it on demand.**
- **Job list / priority / caps:** `Jobs`, `GetJobState(string)`, `MoveJobBefore`,
  `SetCap`, `EffectiveCap(JobState)`, `DerivedMax(JobDef)`, `Reset()`.
- **Stockpiles:** `Deposit`, `Withdraw`, `StoredOf`, `TotalStored`, `JobStorage`,
  `IsStorageFull`, `FindDepositBuilding`, `FindWithdrawBuilding`, `FindHostBuilding`,
  `FindNearestSource`.
- **Process output:** `PickOutputToProduce`, `EmitProcessOutput`, `SpawnWorkerUnit` (Action
  wired by Game1 so the brain can spawn units), `StockReport()`, `CountUndead()`.
- **Job activeness:** `IsJobActive(JobState)` — gating used by the dispatcher.

**Look/edit here when…** adding/changing how workers are created or dispatched, adding an
auto-assign-back action, changing job priority/cap math, stockpile rules, or what makes a
job "active".

### `Necroking/UI/JobBoardUI.cs`
The **Job Board** modal (toggle with 'O'). Priority-ordered job tiles with: workers/cap
badge + `[-]/[+]` cap stepper, storage fill bar, `▲▼` reorder + drag-to-reorder, and
per-output maintain-stock rows for multi-output (potion) jobs. `IModalLayer`. Key members:
`Toggle`, `Update(InputState)` (hit-tests all tile buttons), `Draw`, `DrawTile`,
`TileGeo`/`Geo` (button rectangles), `DrawButton(Rectangle,label,fill)`. Talks only to
`WorkerSystem` (`_ws`).

**Look/edit here when…** adding a button to the Job Board, changing tile layout, the cap
stepper, reorder controls, or maintain-stock rows. **To add a panel-level button** (e.g.
"Re-assign workers") put it near the title/close button in `Draw` (mirror the `x` close
button at `_x + _w - 26`), reserve a `Rectangle`, draw via `DrawButton`, and hit-test it in
`Update` *before* the per-tile loop (mirror the close-button check at line ~141).

### `Necroking/UI/GraveRosterUI.cs`
The **Grave Roster** modal — opened by clicking an empty grave. Lists the housed worker
(with **Unassign**) and a scrollable list of `UnassignedWorkers()` each with an **Assign**
button that calls `_ws.AssignWorker(id, graveObjIdx)`. `IModalLayer`. Key members:
`OpenForGrave(int,int,int)`, `Update`, `Draw`, `RowActionRect`, `HousedActionRect`,
`DrawButton`.

**Look/edit here when…** changing how individual undead are housed into a specific grave,
the assign/unassign-per-grave buttons, or the candidate list rendering.

### `Necroking/AI/WorkerHandler.cs`
The per-unit worker FSM that *executes* the dispatched job (collect/process phases,
pathing, carrying). Reads queries off `WorkerSystem`. Not the place for UI or assignment
policy.

### `Necroking/Game/Jobs/JobDef.cs`, `JobState.cs`, `JobRegistry.cs`
Data model: `JobDef` (def from `data/jobs.json`: Archetype, BuildingDefId,
WorkerSlotsPerBuilding, Inputs/Outputs, SpawnsUnit, etc.), `JobState` (runtime: `Priority`,
`WorkerCap`, `AssignedWorkers`, `OutputTargets`), `JobRegistry.Load/Defs`. `OutputTarget`
(Priority, TargetStock) and `JobResources` constants live alongside.

## Wiring / gotchas
- `JobBoardUI` and `GraveRosterUI` are constructed and `Init`'d in `Game1` (search
  `JobBoardUI` / `GraveRosterUI` in the Game1 partials). `WorkerSystem.Bind` and `Reset`
  are called on startup / new-game / map-load.
- **`Dispatch()` is private and already runs every 0.5s.** "Re-assign workers if they've
  been unassigned" means re-running pool→job distribution: the cleanest hook is to make
  `Dispatch()` public (or add `public void Reassign() => Dispatch();`) and call it from a
  new Job Board button. `SetWorkerJob` deliberately *won't* re-task a worker mid-haul
  (it's carrying goods) to avoid dropping carried items — an on-demand re-dispatch inherits
  that safety.
- Note the ambiguity: if "unassigned" means *workers that lost their grave/home* (back to
  the idle pool, no longer `Worker` archetype), there is **no existing auto-re-house**
  routine — `AssignWorker` is manual per-grave. You'd build that new (iterate empty
  worker-home graves via `IsWorkerHomeDef`/`IsGraveOccupied`, pull from
  `UnassignedWorkers()`, call `AssignWorker`). The dispatcher only manages units already
  in the `Worker` pool.
- `IModalLayer` panels push/pop via `Game1.Popups`. Text goes through
  `RuntimeWidgetRenderer` (FontStashSharp) — round positions to ints (project UI rule).

## Related areas
- `AI/WorkerHandler.cs` — executes jobs (see future `AI/` doc).
- `World/EnvironmentSystem.cs` — graves/buildings the jobs/stockpiles live on.
- game1-partials.md — where the UIs are constructed/toggled and `WorkerSystem` is bound.
