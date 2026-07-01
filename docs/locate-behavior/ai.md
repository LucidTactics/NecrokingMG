# AI — unit behavior archetypes & routines

How every non-player unit decides what to do each tick. The pattern is a
**handler-per-archetype** registry: one stateless singleton `IArchetypeHandler`
per archetype, dispatched by a `byte` archetype id on the unit. All per-unit state
lives in `UnitArrays` (the SoA), passed in via the `ref struct AIContext`.

```
Archetype (IArchetypeHandler singleton) — drives behavior
  → Routine (byte index)    — high-level mode (Following, Chasing, …)
    → Subroutine (byte index) — step within a routine
```

## Dispatch chain (read this first)

1. **`Necroking/AI/IArchetypeHandler.cs`** — the interface (`Update`, `OnSpawn`,
   `GetRoutineName`, `GetSubroutineName`) **and** `ArchetypeRegistry` (the
   name→id→handler table). Archetype ids are `const byte` (None=0, PlayerControlled=1,
   HordeMinion=2 … Worker=12). `ArchetypeRegistry.FromName(string)` is the **single
   source of truth** mapping a `UnitDef.Archetype` string → byte id; `Get(byte)` returns
   the handler.
2. **Registration** — `Necroking/Game1.cs` `~line 718` ("Register AI archetypes"):
   `ArchetypeRegistry.Register(id, "Name", new XHandler())` for every archetype. **Add
   your handler registration here.**
3. **Per-tick dispatch** — `Necroking/Game/Simulation.cs` `~line 837`: the unit loop does
   `ArchetypeRegistry.Get(_units[i].Archetype)?.Update(ref ctx)`. The `AIContext` is built
   by `Simulation.BuildAIContext(i, dt, …)` (`~line 3327`) — this is the **full** context
   (includes `Workers`, `EnvSystem`, `Pathfinder`, `Quadtree`). A separate
   `PlayerControlled` re-dispatch lives at `~line 909`.
4. **Archetype assignment at spawn** — two spawn paths, both resolve the same way:
   - `Game1.SpawnUnit(unitDefID, pos)` — `Necroking/Game1.cs` `~line 1815`: reads
     `unitDef.Archetype`, `FromName` → `UnitsMut[idx].Archetype = id`, then calls
     `handler.OnSpawn(ref ctx)`. **NOTE: the OnSpawn `AIContext` here is *partial*** — it
     does **not** set `Workers` or `EnvSystem`. Don't depend on those in `OnSpawn`; only
     set routine/phase scalars there (mirror `WorkerHandler.OnSpawn`).
   - `Simulation.SpawnZombieMinion` / convert path — `Necroking/Game/Simulation.cs`
     `~line 3941`: `FromName(def.Archetype)` overrides the default archetype.

   **Archetype is keyed by the `UnitDef.Archetype` string in `data/units.json`** — NOT by
   UnitDefID and NOT by a separate AIBehavior enum. (Legacy units with no `Archetype` fall
   back to the `AIBehavior` enum in `unitDef.AI`; new behavior should use Archetype.)

## Files

### `Necroking/AI/IArchetypeHandler.cs`
The interface + `ArchetypeRegistry` (id constants, `Register`/`Get`/`GetName`/`FromName`).
**Edit here** to add a new archetype id constant and its `FromName` case.

### `Necroking/AI/AIContext.cs`
`ref struct AIContext` — everything a handler reads each frame: `UnitIndex`, `Units`
(UnitArrays), `Dt`, `Pathfinder`, `Quadtree`, `EnvSystem`, `Workers`, `Horde`, `GameData`,
plus convenience accessors `MyPos`, `MyMaxSpeed`, `MyId`, `Routine`/`Subroutine`/timers.
Also `UnitAlertState` enum.

### `Necroking/AI/SubroutineSteps.cs`
The **reusable atomic step library** every handler composes from (static, zero-alloc).
The move-to-target primitives live here:
- **`MoveToward(ref ctx, Vec2 target, float speed)`** — the core pathfinding+anim step.
  Uses `ctx.Pathfinder.GetDirection` when >3u away, else straight-line; sets
  `PreferredVel` + locomotion anim. **This is the MoveTo primitive for a custom handler.**
- **`MoveToPosition(ref ctx, speed)`** drives toward `Units[i].MoveTarget`;
  **`MoveToPosition_Arrived(ref ctx, threshold)`** is the arrival test. (`WorkerHandler.MoveTo`
  is a tiny wrapper: set `MoveTarget`, call `MoveToPosition`, `FaceTowards`.)
- `SetEffort` (walk/jog/sprint cap), `SetIdle`, `FacePosition`, `FindClosestEnemy`,
  combat steps (`AttackTarget`, `Disengage`), wander/roam.

### `Necroking/AI/CorpsePuppetHandler.cs`  ← **implemented (archetype `CorpsePuppet`, id 13)**
The raised puppet FSM. Registered in `Game1.cs` (`~line 735`). Each `Update`: `ws.FindDepositBuilding(JobResources.Corpse, MyPos)`; if `<0` idle; else `MoveToPosition_Arrived(1.6f)`, then on arrival `ws.Deposit(pile, JobResources.Corpse, 1)` and — only if it returned >0 — `ws.RecordPiledCorpse(pile, Units[i].UnitDefID)` + set `Units[i].PendingDespawn = true` (reaped with NO loose corpse by `Simulation.RemoveDeadUnits`, pre-pass `Simulation.cs ~line 3663`). **The corpse-type it deposits is `Units[i].UnitDefID`** (the puppet's own def) — to deposit as the ORIGINAL unit type, pass a different id to `RecordPiledCorpse` (needs the source unit-def stored on the unit at raise time).

### `Necroking/AI/WorkerHandler.cs`  ← the deposit/collect FSM template
The grave-worker FSM. Phase-based (`WorkerPhase` byte on the unit). The
**Collect→corpse→deposit** flow is exactly the corpse_puppet's behavior minus the player
ownership:
- `GoToSource` → `MoveTo` + `MoveToPosition_Arrived(SourceRange)`.
- `GoToStorage` (`~line 237`) → `ws.FindDepositBuilding(carry, MyPos)`, `MoveTo`, on arrival:
  `ws.RecordPiledCorpseFromUnit(i, building)` then `ws.ConsumeCarriedCorpse(i)` then
  `ws.Deposit(building, "Corpse", 1)`. This is the precise deposit-into-pile sequence.
- Private `MoveTo(ref ctx, target)` wrapper (`~line 348`).

### Other handlers (reference)
- `HordeMinionHandler.cs` — routine-switch template (Following/Chasing/Engaged/…),
  good model for `GetRoutineName`/`GetSubroutineName` and `OnSpawn`.
- `CombatUnitHandler.cs` / `RangedUnitHandler.cs` — constructed with an archetype id arg
  (one class, several registered ids). `PlayerControlledHandler.cs`, `WolfPackHandler.cs`,
  `RatPackHandler.cs`, `DeerHerdHandler.cs`, `CorpseEatAI.cs`.
- `AwarenessSystem.cs` (enemy detection → sets prey `AlertState`/`AlertTarget`),
  `CombatTransitions.cs` (shared chase/engage exit checks), `WorkRoutine.cs`
  (channel/work-timer step), `AIContext.cs`.

### `Necroking/AI/BoarForageAI.cs`, `Necroking/AI/CorpseEatAI.cs`  ← the sweep-override style
Static `Update(Simulation sim, float dt)` layers that run after the archetype pass and override
`PreferredVel`. See "Second AI style" below — this is the pattern to copy for a player-wolf
pack-hunt behavior (it can reach `sim.NecromancerIndex` / `sim.EnvironmentSystem`, which
`AIContext` cannot).

## Second AI style — post-archetype "sweep" overrides (BoarForageAI / CorpseEatAI)

Not every behavior is an `IArchetypeHandler`. There's a **second pattern** for behaviors
that layer on top of the horde-follow archetype rather than replacing it: a **static class
with `Update(Simulation sim, float dt)`**, iterating all units itself, called from
`Simulation.Update` **after** `UpdateAI` (the archetype pass) and **before** `UpdateMovement`.
By overriding `PreferredVel` that frame it steers the unit; by leaving it alone it defers to
the archetype's follow velocity.

- **`Necroking/AI/BoarForageAI.cs`** — `public static void Update(Simulation sim, float dt)`.
  The template: iterates `sim.UnitsMut`, filters to `Faction.Undead` + `Archetype ==
  ArchetypeRegistry.HordeMinion` + `Routine == 0` (Following) + not `InCombat` + no `Target`,
  reads `sim.NecromancerIndex`/`sim.EnvironmentSystem`, leashes to the necromancer, and
  overrides movement toward a foragable. **This is the closest existing analog to a
  player-wolf pack-hunt behavior.**
- **`Necroking/AI/CorpseEatAI.cs`** — same shape (`Update(Simulation, dt)`), called at
  `Simulation.cs ~line 694`.
- **Wiring** — `Necroking/Game/Simulation.cs`: `PhaseStart(); AI.BoarForageAI.Update(this, dt);
  PhaseEnd("boar_forage");` at `~line 523` (inside `Update`, right after `UpdateAI(dt)` at
  `~line 518`). **Add a new sweep's call here** the same way.
- **Why this style for the wolf pack-hunt**: it needs the **necromancer position**
  (`sim.NecromancerIndex` → `sim.Units[idx].Position` — only reachable via a `Simulation`
  handle, NOT `AIContext`) to compute the "far side of the deer, opposite the necro," and it
  should only kick in for **player-owned** (Undead) horde wolves while they're Following/idle,
  yielding to combat and explicit horde commands. Filter by a wolf tag/def (`def.Tags.Contains
  ("wolf")` — ZombieWolf has tag `"wolf"`) exactly as BoarForage filters `"forager"`.

### Player zombie wolves run as HordeMinion, not WolfPack
`WolfPackHandler` (archetype `WolfPack`, id 3) is for **wild** wolves (`data/units.json` `Wolf`
`archetype:"WolfPack"`). The player's raised **`ZombieWolf`** def has **no `archetype` field**,
so it takes the horde-follow default (`HordeMinion`, like zombie boars) via the reanimate spawn
path (`Simulation.SpawnZombieMinion`). So "add a new pack-hunting AI for the player's wolves"
does NOT mean editing `WolfPackHandler` — that governs wild predators. Two implementation routes:
1. **Sweep override (recommended, matches BoarForageAI)** — new static `WolfPackHuntAI.Update
   (sim, dt)` layered on HordeMinion wolves; no new archetype, no `units.json` change.
2. **New archetype** — give ZombieWolf `"archetype":"PackHunter"`, add the id + `FromName` case
   in `AI/IArchetypeHandler.cs`, register in `Game1.cs ~line 984`, write a
   `PackHunterHandler : IArchetypeHandler`. But this loses free horde-follow/formation and can't
   see the necromancer position without a Sim handle — heavier for this feature.

Prey detection / vision reference (from `DeerHerdHandler`): the **deer's** vision is its
`Units[i].DetectionRange` (from `UnitDef.DetectionRange`); the deer flee when a hostile is within
`DetectionRange * AlertThresholdFraction` (0.9). To "spread out beyond the deer's vision so they
don't detect the wolves," the wolves must stay outside the **target deer's** `DetectionRange`
until positioned — read the deer's `DetectionRange`, not the wolf's. `AwarenessSystem.cs` is the
shared enemy-detection pass that sets each prey's `AlertState`/`AlertTarget`.

## Where corpse-pile deposit & self-removal live (cross-area)

- **`Necroking/Game/Jobs/WorkerSystem.cs`** (ctx.Workers) — already has everything:
  `FindDepositBuilding("Corpse", pos)` (matches env defs with `storedResource:"Corpse"`,
  i.e. `corpse_pile`), `Deposit(objIdx, "Corpse", 1)`, `RecordPiledCorpse(objIdx, unitDefId)`
  / `RecordPiledCorpseFromUnit`, and `FindNearestCorpseObj`. `corpse_pile` itself: `data/env_defs.json`
  id `corpse_pile`, `storedResource:"Corpse"`, `storageCap:20`. To find the nearest pile,
  call `FindDepositBuilding("Corpse", ctx.MyPos)` (it already returns nearest-with-room).
- **Self-removal from the world** — `Necroking/Game/Simulation.cs`
  **`RemoveUnitTracked(int idx)`** (`~line 188`) — the canonical remove (swap-pop +
  repairs `_necromancerIdx`). A per-tick handler can't call `Simulation` directly from
  `AIContext` (no Sim handle exposed); see Pitfalls for the deposit-then-remove approach.

## Corpse Puppet spell — type selection (implemented, cross-area)

The **Corpse Puppet** spell (`data/spells.json`, `summonUnitID: "corpse_puppet"`,
`summonTargetReq: "Corpse"`) currently raises a **dedicated `corpse_puppet` unit def**
(archetype `CorpsePuppet`), NOT a zombie variant of the target:
- In `Game1.Spells.cs` `ExecuteSummonSpell` single-corpse branch, `summonUnitID` comes from
  `pending.SummonUnitID`. Zombie-type-from-corpse resolution only happens when
  `string.IsNullOrEmpty(summonUnitID)` (`~line 72`), calling
  `Game.TableCraftingSystem.ResolveZombieUnitID(gameData, corpse.UnitDefID)`. Because the
  spell sets a non-empty `corpse_puppet`, that resolution is **skipped** — the raise uses the
  fixed puppet def via `QueueReanimRise(summonUnitID, …)` → `SpawnZombieMinion`.
- **To raise the ZOMBIE variant of the target** instead: leave `summonUnitID` empty on the
  spell (so `ResolveZombieUnitID` picks the zombie type), then **override the raised unit's
  archetype to `CorpsePuppet`** after spawn. Options: add a `SpellDef` flag (e.g.
  `overrideArchetype`) honored in `QueueReanimRise`/its spawn callback, or key off the spell
  id; either way call `ArchetypeRegistry.FromName("CorpsePuppet")` → set
  `UnitsMut[idx].Archetype` on the freshly-spawned unit (the `PendingReanimRise.OnSpawned`
  hook, `Game1.Spells.cs`, is the clean injection point).
- **`ResolveZombieUnitID`** (`Game/TableCraftingSystem.cs`) reads `sourceDef.ZombieTypeID`
  (a unit id or a unit-group id → `UnitGroups.PickRandom`). This is the single source of
  truth for "what does a corpse of type X raise into."

## Corpse-deposit type — where the puppet becomes a corpse (cross-area)

`CorpsePuppetHandler.Update` deposits with `ws.RecordPiledCorpse(pile, ctx.Units[i].UnitDefID)`
— so it piles as its **own** def (`corpse_puppet`, i.e. effectively a zombie/generic corpse).
`RecordPiledCorpse(objIdx, unitDefId)` (`Game/Jobs/WorkerSystem.cs`) pushes that def onto the
pile's type stack; `TakePiledCorpse`/`PiledCorpseLines` read it back for withdraw/UI.
**To deposit as the ORIGINAL unit type**: store the source corpse's `UnitDefID` on the unit at
raise time (a new field on `UnitArrays`, set in the reanim spawn callback) and pass THAT id to
`RecordPiledCorpse` instead of `Units[i].UnitDefID`. The abstract `Deposit(pile,"Corpse",1)`
count stays the same; only the recorded identity changes.

## Pitfalls / gotchas

- **Archetype = string in the def, resolved by `FromName`.** Forgetting the `FromName`
  case means the unit silently gets `None` (no handler, stands still). Add the case + the
  `const byte`.
- **`OnSpawn`'s AIContext is partial** (no `Workers`/`EnvSystem`) — only the `Update` path
  (`BuildAIContext`) has them. Do world queries in `Update`, not `OnSpawn`.
- **Self-removal can't go through `AIContext`** — there's no `Simulation` reference on the
  context. Cleanest options: (a) **reuse `ConsumeCarriedCorpse`-style flow** by giving the
  puppet a carried-corpse id so `WorkerSystem` removes it — but the puppet *is* the body, it
  isn't carrying one; or (b) **add a small removal hook**: set a flag/marker on the unit (e.g.
  a new `DespawnRequested` bool on `UnitArrays`) that `Simulation`'s post-AI sweep honors with
  `RemoveUnitTracked`. Don't `RemoveAt` the units list mid-AI-loop (it's iterating by index
  and uses swap-pop) — defer removal to after the loop. Mirror how dissolving/dead units are
  reaped. Check `Simulation` for an existing per-frame "remove dead/marked units" pass before
  adding a new flag.
- **`FindDepositBuilding` returns nearest-with-room only** — if every `corpse_pile` is full
  (`storageCap:20`) it returns -1; handle the no-pile case (idle/wander) instead of NRE.
- **`Deposit` clamps to room and returns the accepted amount** — if it returns 0 the pile was
  full; don't remove the puppet in that case (it didn't get stored).
- **Record before deposit** — call `RecordPiledCorpse(pile, UnitDefID)` so the body can be
  withdrawn later as the same type (matches `WorkerHandler.GoToStorage` ordering).
- **Build-gated buildings** — `FindDepositBuilding` skips piles with `BuildProgress < 1`
  (the `Built` check). A blueprint pile won't be a valid target.

## Related areas
- [jobs-workers.md](jobs-workers.md) — `WorkerSystem` (the deposit/pile/stockpile brain) and
  `WorkerHandler` (the FSM template).
- [corpses.md](corpses.md) — `Corpse` data model; note a "pile" of loose corpses ≠ the
  `corpse_pile` *building* stockpile (the puppet deposits into the building's abstract
  "Corpse" count, not as a loose body).
- game1-partials.md — `Game1.cs` archetype registration + `SpawnUnit`; `Game1.Spells.cs`
  `ExecuteSummonSpell`.
