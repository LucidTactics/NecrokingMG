# AI — unit behavior archetypes & routines

How every non-player unit decides what to do each tick. The pattern is a
**handler-per-archetype** registry: one stateless singleton `IArchetypeHandler`
per archetype, dispatched by a `byte` archetype id on the unit. All per-unit state
lives in `UnitArrays` (the SoA), passed in via the `ref struct AIContext`.

**The legacy `AIBehavior` migration is DONE** (commits d08b584…d21ad4a on master,
2026-07-07): the enum is down to 5 values, the wolf phase machine and a dozen dead
behaviors are deleted, and every spawn path wires the def archetype through one canonical
helper (`Simulation.ApplyDefRuntimeFields`). See "Legacy `AIBehavior` — post-migration
state" below.

```
Archetype (IArchetypeHandler singleton) — drives behavior
  → Routine (byte index)    — high-level mode (Following, Chasing, …)
    → Subroutine (byte index) — step within a routine
```

## Dispatch chain (read this first)

1. **`Necroking/AI/IArchetypeHandler.cs`** — the interface (`Update`, `OnSpawn`,
   `GetRoutineName`, `GetSubroutineName`) **and** `ArchetypeRegistry` (the
   name→id→handler table). Archetype ids are `const byte` (None=0, PlayerControlled=1,
   HordeMinion=2 … Watchdog=14, SoloPredator=15, AmbushPredator=16).
   `ArchetypeRegistry.FromName(string)` is the **single source of truth** mapping a
   `UnitDef.Archetype` string → byte id; `Get(byte)` returns the handler.
2. **Registration** — `Necroking/Game1.cs`, the "Register AI archetypes" block (search
   `ArchetypeRegistry.Register`): one `Register(id, "Name", new XHandler())` per archetype
   (currently PlayerControlled, WolfPack, RatPack, DeerHerd, HordeMinion, PatrolSoldier,
   GuardStationary, ArmyUnit, ArcherUnit, CasterUnit→`CasterUnitHandler`, Worker,
   CorpsePuppet, Civilian→VillagerHandler, Watchdog, and
   SoloPredator/AmbushPredator→`SoloPredatorHandler(opportunist: false/true)`).
   **Add your handler registration here.**
3. **Per-tick dispatch** — `Necroking/Game/Simulation.cs` `UpdateAI(dt)`: per-unit loop,
   gates first (`!Alive` / `InPhysics` / `Jumping`/`Incap.IsLocked` → PreferredVel=0 /
   `Routing` → `SteerRout`), then if `Archetype > 0` →
   `ArchetypeRegistry.Get(archetype).Update(ref ctx)` and `continue`. The `AIContext` is
   built by `Simulation.BuildAIContext(i, dt, dayFraction, isNight)` — this is the **full**
   context (includes `Workers`, `EnvSystem`, `Pathfinder`, `Quadtree`). `Archetype == 0`
   falls through to the **residual legacy `AIBehavior` switch** (same method) — only three
   cases remain: PlayerControlled (re-dispatches to `PlayerControlledHandler` when
   `Routine != 0`), MoveToPoint, IdleAtPoint, plus the `default` AttackClosest
   horde-follow/chase brain. The old `selfManagesCombat` wolf special-casing in the
   preamble is gone. Pre-passes inside `UpdateAI`: `AwarenessSystem.Update` then
   `_villages?.Update` (village alert), and before `UpdateAI` in `Simulation.Update`:
   `_squads.Update` (SquadSystem pre-pass).
4. **Archetype assignment at spawn — one canonical path**:
   **`Simulation.ApplyDefRuntimeFields(idx, def, stats)`** (`Necroking/Game/Simulation.cs`)
   is the single def→unit runtime copy: sprite scale/size/radius/OrcaPriority, faction
   (**unconditional** — empty def faction = Undead), stats, awareness config, caster
   resources (`MaxMana`/`ManaRegen`/`SpellID`, spawn at **full mana**), and AI dispatch:
   **a def `archetype` wins** (`FromName` → `Archetype`, `handler.OnSpawn` called; the
   legacy `ai` string is ignored/inert); only archetype-less defs parse `def.AI`. Callers:
   - `Game1.SpawnUnit(unitDefID, pos)` (`Necroking/Game1.cs`) — delegates to it, then does
     necro detection (`unitDef.AI == "PlayerControlled"` string compare) and horde
     auto-enroll for archetype-less undead.
   - `Simulation.SpawnUnitByID` and `Simulation.TransformUnit` — call it directly, so
     trigger/potion-spawned and transformed units now get their def archetype wired (this
     was the old "SpawnUnitByID gap"; it's closed).
   - `Simulation.SpawnZombieMinion` / reanimate path — HordeMinion default, def
     `archetype` overrides.

   **NOTE: the OnSpawn `AIContext` in `ApplyDefRuntimeFields` is *partial*** — it does
   **not** set `Workers` or `EnvSystem`. Don't depend on those in `OnSpawn`; only set
   routine/phase scalars there (mirror `WorkerHandler.OnSpawn`).

   **Archetype is keyed by the `UnitDef.Archetype` string in `data/units.json`** — NOT by
   UnitDefID and NOT by the `AIBehavior` enum. (Archetype-less defs fall back to the
   `AIBehavior` enum in `unitDef.AI`; new behavior should always use Archetype.)

   **Placed units with a patrol route**: the `Game1.cs` map-load placed-unit loop stamps
   `Archetype = PatrolSoldier` + `PatrolRouteIdx`/`PatrolWaypointIdx`/`MoveTarget` when
   `pu.PatrolRouteId` is set — same wiring as the inter-village patrols in
   `Game1.Villages.cs` (the old `AIBehavior.Patrol` never walked the route).

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
  (one class, several registered ids: PatrolSoldier/GuardStationary/ArmyUnit for
  CombatUnitHandler; RangedUnitHandler is registered for **ArcherUnit only** —
  CasterUnit has its own `CasterUnitHandler.cs`). **`RangedUnitHandler.cs` is the archer
  brain**: routines
  Idle(0)/Alert(1)/Combat(2)/Return(3); `UpdateCombat` reads `Stats.RangedRange[w]`
  (fallback 18f), closes in (Hurry) when `dist > maxRange`, **kites** when
  `dist < maxRange*0.25` (`SubroutineSteps.MoveAwayFrom` at Walk effort), else stops;
  fires by stamping `PendingAttack` + `PendingWeaponIdx` +
  `PendingWeaponIsRanged` + `PendingRangedTarget` (never `EngagedTarget` — that runs the
  melee queue). Arrow spawns later at the anim hit frame via
  `Simulation.ResolvePendingAttack`'s ranged branch (see combat.md "Ranged / projectiles").
  `PlayerControlledHandler.cs` (exposes public routine consts —
  `RoutineCraftAtTable`, `RoutineBuildGlyph`, `RoutineWorkOnBush`, `BuildSub_*` — written
  by external systems, see "External routine writers"), `WolfPackHandler.cs` (wild wolf
  packs), `RatPackHandler.cs`, `DeerHerdHandler.cs` (female flee / male fight-back —
  absorbed FleeWhenHit/NeutralFightBack).
- `SoloPredatorHandler.cs` — **one class, two archetypes** via ctor flag:
  `SoloPredator` (id 15, `opportunist: false`) — hit-and-run engage→bite→disengage→
  wait-cooldown cycle (defs: DireWolf, JuvWolf, Boar, GreatBoar); `AmbushPredator`
  (id 16, `opportunist: true`) — circles and waits for the target to face away before
  committing (defs: Bear, GrizzlyBear). Combat subroutines mirror the deleted legacy
  `WolfPhase` states; `IsTargetFacingAway` lives here now (migrated from `Simulation`).
- `VillagerHandler.cs` (archetype `Civilian`) and `WatchdogHandler.cs` (archetype
  `Watchdog`, Guard/Bark routines) — village AI; both consult `VillageThreat.cs`
  (static undead-detection helpers) and `Game/VillageSystem.cs` (per-village alert/posture,
  updated inside `UpdateAI` before the unit loop; wiring in `Game1.Villages.cs`).
- `SquadSystem.cs` — persistent unit groups (`Squad`: `Members` (unit **ids**, not
  indices) / derived `Centroid`/`Spread`/`AliveCount`/`LeaderId`/shared alert, keyed by
  `Unit.SquadId`). API: `CreateSquad(faction, archetype)` (pre-formed, caller fills
  Members + stamps each `SquadId` — the zone-load path), `TryGet(id)`/`Get(id)`,
  `Squads` (read-only dict view), `Clear()`. `_squads.Update` runs once per frame in
  `Simulation.Update` *before* `UpdateAI`: `AssignUnassigned` (lazy clustering — any
  squad-kind unit with `SquadId==0` or a dead squad id joins the nearest same-kind squad
  within `ClusterRadius` 22u / `MaxMembers` 24, else seeds a new one), then `Recompute`
  (prunes dead members in place, recomputes derived fields, **culls squads with zero
  living members**). Ids are monotonic (`_nextId++`), never reused within a map (reset
  only by `Clear()` per map load). Handlers read their group via `AIContext.MySquad`.
  No merge/split logic exists — only lazy join + cull-on-empty.
  Read by DeerHerd/WolfPack/RatPack handlers and `WolfPackHuntAI` so groups act as one
  object instead of per-frame proximity scans. `Simulation.Squads` is the public accessor.

### Shared transition logic
- **Routine-transition choke point (commit 0b435eb)** — every `Routine` change goes
  through one of three sanctioned paths; **writing `Unit.Routine` directly is a bug**
  (skips exit cleanup; the only exception is `OnSpawn` initializers):
  - `AIContext.TransitionTo(routine, sub=0, timer=0)` (`AI/AIContext.cs`) — a handler
    changing its OWN unit. Fires the handler's `OnRoutineExit`/`OnRoutineEnter` hooks,
    stamps Subroutine+SubroutineTimer; no-op (returns false) when already in the routine.
  - `AIControl.Interrupt(units|ctx, idx, reason)` (`AI/AIControl.cs`) — the one legal way
    for an EXTERNAL system to yank a unit: fires the exit hook, clears the pin fields
    (`Target`/`EngagedTarget`/`PendingAttack`/`InCombat`), resets to routine 0. Callers:
    physics launch, worker assign/unassign, player WASD-cancel, wolf-hunt recast.
  - `AIControl.StartRoutine(units|ctx, idx, routine, sub, timer)` — external order start;
    a same-routine call RESTARTS it (full exit/enter cycle). Used by spells/crafting/
    build-UI and by handlers wanting restart semantics. Set routine parameter fields
    (MoveTarget etc.) AFTER the call — the exit hook clears the old routine's fields.
  - Per-routine cleanup lives ONCE in each handler's `OnRoutineExit` (e.g.
    `RangedUnitHandler.OnRoutineExit` clears Target/EngagedTarget on Combat exit but
    deliberately keeps `PendingAttack` so a queued arrow still fires).
  - Debugging: `ai_trace` dev command → logs every transition (unit, archetype, from→to,
    interrupt reason) to `log/ai_transition.log` (`AIControl.TraceTransitions`).
- `CombatTransitions.cs` — **the canonical routine-transition helpers**:
  `StandardEngagedExits(ref ctx, chasingRoutine, returningRoutine, meleeHysteresis, leashRadius, leashCenter)`
  and `StandardChasingExits(...)`. Handlers pass their own routine byte values (different
  handlers use different indices for "chase"/"return"). Return true = transition applied,
  caller must `return`. They centralize: target-dead → Returning (or re-acquire if
  `Frenzied`), out-of-melee (1.2× hysteresis) → Chasing, leash break → Returning; they
  **clear `PendingAttack`** on exit (a never-resolving PendingAttack pins the unit forever
  via the movement lockout) but deliberately keep `PostAttackTimer` (bounded plant so the
  swing anim finishes). Historic bugs from before this existed: "horde unit stands still
  while target kites", "chaser drags the horde across the map".
- `AwarenessSystem.cs` — enemy-detection pass (sets prey `AlertState`/`AlertTarget`,
  same-faction group alert propagation); runs at the top of `UpdateAI`, amortized via
  `_amortizedAI`/`_aiUpdateInterval`.
- `WorkRoutine.cs` — channel/work-timer step shared by craft/build/bush-work routines.

### `Necroking/AI/BoarForageAI.cs`, `Necroking/AI/CorpseEatAI.cs`, `Necroking/AI/WolfPackHuntAI.cs`  ← the sweep-override style
Static `Update(Simulation sim, float dt)` layers that run after the archetype pass and override
`PreferredVel`. See "Second AI style" below — the pattern to copy when a behavior needs a
`Simulation` handle (`sim.NecromancerIndex` / `sim.EnvironmentSystem`, which `AIContext`
cannot reach). **`WolfPackHuntAI` is implemented**: pack-hunt for the player's zombie wolves
(HordeMinion + `wolf` tag) — Flanking (circle outside the deer's `DetectionRange` to the far
side from the necromancer) then Driving (chase the deer toward the player). Gated on the
"Wolf Hunt" spell command (`Simulation._wolfHuntCmdTimer` + cast point, set in
`Game1.Spells.cs`); uses `SquadSystem` centroid/spread to stand off the whole herd.

## CasterUnit archetype — migrated (commit 11b12d8)

`AI/CasterUnitHandler.cs` now owns NPC spell-casting (registered as `CasterUnit`, id 8);
the legacy `case AIBehavior.Caster:` was **removed** from the `UpdateAI` switch (a comment
marks the spot). `Simulation.ApplyDefRuntimeFields` now copies `def.MaxMana`/`ManaRegen`/
`SpellID` onto the unit (spawns start at full mana). Path levels are still read from the
def at cast time (`UnitDef.GetPathLevel`, buffs via `BuffSystem.EffectivePathLevel`).

## Legacy `AIBehavior` — post-migration state (migrated 2026-07-07, commits d08b584…d21ad4a)

The migration the old census documented is **complete**. Current state:

**The enum** (`Necroking/Data/Enums.cs`) is reduced to:
`PlayerControlled = 0, AttackClosest, MoveToPoint, IdleAtPoint, Caster`.
`Caster` is **inert** — kept only so old def `ai` strings parse harmlessly (the real
behavior is the CasterUnit archetype). **Deleted values**: AttackClosestRetarget,
GuardKnight, AttackNecromancer, ArcherAttack, DefendPoint, Raid, Patrol, CorpseWorker,
OrderAttack, FleeWhenHit, NeutralFightBack, and all four Wolf* values.

**Deleted with them**: `Unit` fields `WolfPhase`/`WolfPhaseTimer`/`FleeTimer`/
`RetargetTimer`/`QueuedAction`, the `QueuedUnitAction` enum, and `Simulation`'s
`UpdateWolfAI`/`IsTargetFacingAway`/`FindMostIsolatedEnemy` (the facing-away test now
lives in `SoloPredatorHandler`). The legacy switch preamble no longer has the
`selfManagesCombat` wolf special-casing.

**The residual legacy switch** (`Simulation.UpdateAI`, reached when `Archetype == 0` or
`AI == PlayerControlled`) keeps only: `PlayerControlled` (routine re-dispatch + WASD),
`MoveToPoint` (dev/test primitive: dev `move`, scenarios), `IdleAtPoint` (net ghosts in
`Game1.Net.cs` + scenarios' "inert" value — still fights enemies <10u), and the
`default` AttackClosest horde-follow/chase brain (horde units without an archetype,
scenario spawns).

**Where the old behaviors went**:
- Caster → `CasterUnit` archetype (`AI/CasterUnitHandler.cs`) — see section below.
- WolfHitAndRun (DireWolf/JuvWolf/Boar/GreatBoar defs) → **SoloPredator** (id 15);
  WolfOpportunist (Bear/GrizzlyBear defs) → **AmbushPredator** (id 16); both
  `AI/SoloPredatorHandler.cs` via ctor flag. ZombieRat and WolfCubZombie defs →
  `HordeMinion`.
- Patrol → PatrolSoldier: the `Game1.cs` placed-unit map-load loop wires
  Archetype+PatrolRouteIdx+WaypointIdx+MoveTarget from `pu.PatrolRouteId` (same as the
  `Game1.Villages.cs` village patrols).
- FleeWhenHit's DamageSystem role → **`Archetype == ArchetypeRegistry.DeerHerd`**: the
  auto-`EngagedTarget` exemption in `Game/DamageSystem.cs` (both `Apply` and
  `ApplyDirect`) now keys on the archetype, since the handler owns the flee/fight-back
  reaction.

**`Unit.AI` reads still load-bearing (all PlayerControlled — the "is the player" flag):**
`UnitModel.cs` `FindAliveNecromancerIndex`, `Game1.cs` `SpawnUnit` necro detection
(string compare `unitDef.AI == "PlayerControlled"`) + horde auto-enroll exclusion,
`SpellEffectSystem` summon horde-enroll exclusion, `HordeCapTracker`, `DamageSystem`
auto-engage exclusion, `Locomotion` player gait, `Simulation` movement/ORCA/cast-plant
gates and kill-tally, the dispatch gate itself, and `Game1.Net.cs` ghosts (must be
non-PlayerControlled). Migrating PlayerControlled off the enum = replacing every one of
these reads with an Archetype check; separate refactor, not planned.

**Scenario convention**: a scenario that spawns a unit by def and drives it with legacy
AI (`u.AI = ...`, MoveToPoint, manual targets) must **set `Archetype = 0` explicitly** —
otherwise the def's archetype handler keeps dispatching and the legacy fields are
ignored. Many scenarios now do this (see `TrampleAttackScenario` / `SweepAttackScenario`
comments). Scenarios deleted with the migration: ai_behavior, order_attack, undead_raid,
raid_workers, flee_when_hit, retarget. `wolf_hit_and_run` now tests the SoloPredator
archetype (the phase machine is the Combat routine's subroutines);
`neutral_fight_back` tests MaleDeer/DeerHerd fight-back.

**Dev-command gotcha (still true)**: dev `move` sets `AI = MoveToPoint` but does NOT
clear `Archetype`, so it's silently ignored for archetype units; `set_ai` only accepts
the 5 surviving enum names.

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
- **Wiring** — `Necroking/Game/Simulation.cs` `Update`: order is `_squads.Update` →
  `UpdateAI(dt)` → `BoarForageAI.Update(this, dt)` → `WolfPackHuntAI.Update(this, dt)` →
  (later) `UpdateMovement`; `CorpseEatAI.Update` is called further down the same method.
  **Add a new sweep's call here** the same way (after `UpdateAI`, before movement).
- **Why this style for the wolf pack-hunt**: it needs the **necromancer position**
  (`sim.NecromancerIndex` → `sim.Units[idx].Position` — only reachable via a `Simulation`
  handle, NOT `AIContext`) to compute the "far side of the deer, opposite the necro," and it
  should only kick in for **player-owned** (Undead) horde wolves while they're Following/idle,
  yielding to combat and explicit horde commands. Filter by a wolf tag/def (`def.Tags.Contains
  ("wolf")` — ZombieWolf has tag `"wolf"`) exactly as BoarForage filters `"forager"`.

### Player zombie wolves run as HordeMinion, not WolfPack
`WolfPackHandler` (archetype `WolfPack`, id 3) is for **wild** wolf packs (`data/units.json`
`Wolf` `archetype:"WolfPack"`; solo predators like DireWolf are `SoloPredator` instead). The
player's raised **`ZombieWolf`** def has `archetype:"HordeMinion"` (horde-follow, like zombie
boars), spawned via the reanimate path (`Simulation.SpawnZombieMinion`). The pack-hunt
behavior for the player's wolves was
implemented as the sweep override **`AI/WolfPackHuntAI.cs`** (see above) — NOT by editing
`WolfPackHandler`, which still governs wild predators only.

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
- In `Game/SpellEffectSystem.cs` `ExecuteSummonSpell` single-corpse branch, `summonUnitID` comes from
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

## External routine writers — who sets Routine/Subroutine from OUTSIDE the handlers

Per-unit AI state (`Routine`, `Subroutine`, `SubroutineTimer` bytes/float) lives on the
`Unit` class in `Necroking/Movement/UnitModel.cs`; `AIContext.Routine/Subroutine` are just
pass-throughs. Since commit 0b435eb external systems no longer write the bytes raw — they
go through `AIControl.StartRoutine` (order start) / `AIControl.Interrupt` (cancel/yank),
see "Shared transition logic" above. Current external writers:

- `Game1.Crafting.cs` / `Game1.Spells.cs` (bush-work) / `UI/BuildingMenuUI.cs` — call
  `AIControl.StartRoutine(units, necroIdx, PlayerControlledHandler.RoutineCraftAtTable /
  RoutineWorkOnBush / RoutineBuildGlyph, …)`. Cancel path: WASD input →
  `AIControl.Interrupt(units, i, "player-move")` in `Simulation.UpdateAI`'s
  PlayerControlled branch. `Game/TableCraftingSystem.cs` *reads* these to know the player
  is channeling.
- `Game1.Spells.cs` horde-command spell — `HordeMinionHandler.CommandTo(units, idx, target)`
  / `Recall(units, idx)` intent APIs (which call `StartRoutine` with the now-public
  `RoutineCommanded`/`RoutineReturning` consts). No more raw `Routine = 4` literals.
- `Game/PhysicsSystem.cs` — `AIControl.Interrupt(units, idx, "physics-launch")` when a unit
  enters physics (knockback etc.).
- `Game/Jobs/WorkerSystem.cs` — `Interrupt(…, "worker-assign"/"worker-unassign")`
  (WorkerHandler uses its own `WorkerPhase` byte, not Routine).
- `Simulation` — `Interrupt(…, "wolf-hunt-recast")` on wolf-hunt release.

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
- **Stuck/locked units — the classic causes**: (a) a `PendingAttack` that never resolves
  pins the unit (legacy path zeroes `PreferredVel` while one is set; archetype routines must
  clear it on transitions — `CombatTransitions` does); (b) legacy-path `InCombat == true`
  zeroes `PreferredVel` (only `Fleeing`/`Routing` units are exempt) — forget to clear
  `InCombat` on routine exit and the unit freezes; (c) `Routing`/`Incap.IsLocked`/`Jumping`/`InPhysics` all
  short-circuit the AI dispatch entirely — a stale flag means the handler never runs;
  (d) routine byte written externally with no exit arc in the handler (see "External
  routine writers") leaves the unit in a state its handler never leaves.
- **Routine byte indices are per-handler, not global** — `Routine = 4` means different
  things to different handlers. Use the handler's public routine consts (e.g.
  `HordeMinionHandler.RoutineCommanded`) via `AIControl.StartRoutine`/intent APIs — never
  raw literals, and **never write `Unit.Routine` directly** (skips `OnRoutineExit` cleanup;
  see "Shared transition logic").

## Related areas
- [jobs-workers.md](jobs-workers.md) — `WorkerSystem` (the deposit/pile/stockpile brain) and
  `WorkerHandler` (the FSM template).
- [corpses.md](corpses.md) — `Corpse` data model; note a "pile" of loose corpses ≠ the
  `corpse_pile` *building* stockpile (the puppet deposits into the building's abstract
  "Corpse" count, not as a loose body).
- game1-partials.md — `Game1.cs` archetype registration + `SpawnUnit`;
  `Game/SpellEffectSystem.cs` `ExecuteSummonSpell` (moved out of `Game1.Spells.cs` 2026-07).
