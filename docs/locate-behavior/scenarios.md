# Scenarios — coded headless tests & batch sim harnesses (`Necroking/Scenario/`)

Checked-in, re-runnable tests launched with `bin/Debug/Necroking.exe --scenario <name>
[--headless] [--timeout N]`, or from the in-game scenario menu. Deep how-to (writing,
running, screenshots) lives in [docs/testing-scenarios.md](../testing-scenarios.md);
this doc is the code map.

## Harness core

- **`Necroking/Scenario/ScenarioBase.cs`** — the abstract base every scenario extends.
  Contract: `Name`, `OnInit(Simulation)`, `OnTick(Simulation, float dt)`, `IsComplete`,
  `OnComplete(Simulation)` (returns process exit code). Opt-in plumbing via virtual
  properties/fields Game1 fills before `OnInit`: `WantsUI`/`WantsGrass`/`WantsGround`,
  `GridSize` (default **64** — the scenario world size), `DebugSpells`, `BenchmarkMode`,
  camera override (`ZoomOnLocation`), editor/menu requests (`RequestedMenuState`,
  `RequestedMapTab`, …), `DeferredScreenshot`, plumbed system refs (GroundSystem,
  RoadSystem, Inventory, Atlases, Font, WidgetRenderer…). `ScenarioLog` = the `"scenario"`
  DebugLog tag (`log/scenario.log`).
  **Look/edit here when…** a scenario needs access to a Game1-owned system it can't reach
  — add a plumbed field + fill it in `StartScenario`.
- **`Necroking/Scenario/ScenarioRegistry.cs`** — static ctor with every
  `Register("name", "Category", () => new …Scenario())` call, grouped by display category
  (Combat, AI & Movement, Rendering & VFX, …). Category is cosmetic (menu grouping);
  headless lookup is by name via `Create(name)`.
  **Look/edit here when…** adding a new scenario — one `Register` line.
- **`Necroking/Scenario/ScenarioScreenshot.cs`** — screenshot helper.
- **Run path in `Necroking/Game1.cs`**: `StartScenario(name)` does the full
  `ResetWorldState()` wipe, then inits Ground/Env/Wall/Sim/DeathFog/FogOfWar at
  `scenario.GridSize`, seeds 5 ground types and `FillAll(0)` (**flat all-grass world —
  scenarios never load `assets/maps/default.json`**), then `scenario.OnInit(_sim)`.
  Per frame, Game1's Update calls `scenario.OnTick(_sim, WorldDt)` after ONE sim tick,
  consumes the request fields, and on `IsComplete` calls `OnComplete` (exit code in
  headless mode). CLI autostart: `LaunchArgs.Scenario` consumed at MainMenu in Update.
  **Gotcha:** `--speed` (`LaunchArgs.Speed`) is parsed but consumed nowhere — scenarios
  that want fast-forward batch extra `sim.Tick(1/60f)` calls themselves inside `OnTick`
  (see BalanceMatrixScenario `_speed`).

## Headless facts scenarios rely on

- **Headless runs the FULL frame loop** — `LaunchArgs.Headless` only hides/shrinks the
  window; `Game1.Update` still calls `UpdateAnimations(_clock.WorldDt)` every frame
  (`Game1.cs` ~3482, unconditional), so anim effect frames (`JustHitEffectFrame` →
  `AttackResolver.TryResolvePendingAttackAtImpact` → arrow spawns) DO fire headless.
  `AnimController` is data-driven (`.animationmeta`), not GPU-dependent. Caveat: the anim
  pass runs once per FRAME — a scenario that batches extra `sim.Tick` calls for
  fast-forward outruns the effect frames (why balance_matrix self-resolves swings).
  Anim-triggered behavior (archer arrows) must run at 1x.
- **Pass/fail contract**: `OnComplete(sim)` returns the process exit code (0 = pass,
  nonzero = fail). Headless: `Environment.ExitCode = result; Exit()` + a stderr line
  `SCENARIO PASS: <name>` / `SCENARIO FAIL: <name> (code=N)`. Template =
  `TrampleKillScenario` (latch bools in `OnTick`, validate + `DebugLog.Log(ScenarioLog, …)`
  in `OnComplete`, hard `MaxDuration` timeout so it can't wedge).
- **Detecting projectiles**: `sim.Projectiles` = the `ProjectileManager`
  (`Simulation.cs` `Projectiles => _projectiles`); live list =
  `sim.Projectiles.Projectiles` (`IReadOnlyList<Projectile>`, `Projectile.cs`). Poll each
  `OnTick` and latch (`Count > 0` / `Type == ProjectileType.RegularHit`) — projectiles die
  on impact/ground/MaxAge 10s, so a one-shot check can miss.
- **Spawning by def id**: `sim.SpawnUnitByID("<units.json id>", pos)` → unit index or -1;
  `ApplyDefRuntimeFields` copies the def's archetype/stats, so the archetype AI runs
  as-authored. Gotchas: def *id* ≠ sprite name (the archer is id `"archer"`, sprite
  `NavarreLightInfantry_Archer`); deer = `"FemaleDeer"`/`"MaleDeer"`. Hostility is simply
  faction ≠ faction (`FactionMaskExt.AllExcept`) — Human vs Animal are enemies.
- **`ResetWorldState` wipes lose set-once sim wiring**: the fresh scenario `Simulation`
  never gets `SetAnimMeta` (see anti-patterns-list.md) — `ctx.AnimMeta` is null in
  scenarios, same as in normal play after StartGame.
- **Unknown scenario name wedges**: `StartScenario` logs "Failed to create scenario" and
  returns without exiting — the headless process idles forever. Double-check the
  `Register` name before running (see MEMORY scenario-runner-gotchas).

## The balance tournament (worked example of a batch combat-stats harness)

- **`Necroking/Scenario/Scenarios/BalanceMatrixScenario.cs`** (`balance_matrix`, Combat) —
  pits every unordered pair of the animal-zombie roster in clean arena duels and searches
  for the ~50% win-rate squad counts. The reusable mechanics:
  - **Spawn a side**: `SpawnSide` → `sim.SpawnUnitByID(defId, pos)` in a jittered
    formation grid, then per unit: set `Faction`, strip the def archetype
    (`u.Archetype = AI.ArchetypeRegistry.None`), `u.AI = AIBehavior.AttackClosest`
    (leash-less legacy arena brain; pounce/trample weapons still work), `u.Routine = 0`,
    `u.Stats.Morale = 100` (both sides fearless so stats, not morale, decide).
  - **Fast-forward + swing resolution**: `OnTick` drives `_speed` (30) extra
    `sim.Tick`+`Step` per frame. Because Game1's animation pass (which fires melee
    action-moments) runs once per FRAME, the scenario resolves swings itself each step:
    loop units, `sim.ResolvePendingAttack(i)` for any live unit with a pending attack —
    **skipping `Jumping` and `ChargePhase != 0` units** (pounce/trample resolve
    themselves at landing/impact; resolving early applies damage at liftoff).
  - **Winner detection**: count alive units per `Faction` each step; fight ends when a
    side hits 0 or `_fightTimeout` (90 game-s) → draw. Side A's faction alternates, so
    winner attribution goes through `_facA`.
  - **Bias alternation**: 8-trial cycle flips north/south spawn, spawn order (lower unit
    indices act first), and Undead/Human faction (a mild unexplained Human edge exists —
    averaged out, not trusted). Debug lever `NECRO_BALANCE_INVERT=1`.
  - **Isolation**: ONE arena at (32,32) on the flat 64×64 grid; fights run sequentially;
    `ClearWorld` between trials = `sim.UnitsMut.Clear(); sim.CorpsesMut.Clear();
    sim.Projectiles.Clear(); sim.Physics.Clear();`.
  - **Stats model**: `ConfigResult` (CountA/B, WinsA/B, Draws, AvgDuration,
    AvgWinnerSurvivors) per count-config, `PairResult` per pair, `RunResult` serialized
    (camelCase, indented) to **`bin/Debug/log/balance_results.json`** after every config
    (`WriteResults` — kill-safe partial data).
  - **Config**: env vars `NECRO_BALANCE_UNITS/BASE/TRIALS/FIGHT_TIMEOUT/SPEED`.
  - **Early stopping**: `ConfigNeedsMoreTrials` — shutout after 6 decisive, or clearly
    lopsided (>25pp from 50%) after `_minTrials`.
- **`tools/balance_report/make_report.py`** — turns `balance_results.json` into a
  self-contained HTML matrix (icons via `tools/balance_report/crop_icons.ps1` first).
  **Look/edit here when…** the report layout/coloring changes.

**Pitfall for multi-arena / parallel fights**: legacy non-horde `AttackClosest` targets
the **globally nearest enemy, unbounded** (`Simulation.FindBestEnemyTarget` →
`Query.NearestEnemyOf(i, 0)`), so simultaneous arenas on one map cross-contaminate once
any arena empties — survivors march to the next arena. Track spawned unit Ids per arena,
attribute wins by Id sets (not faction alone), and despawn finished arenas' survivors via
`unit.PendingDespawn = true` (vanishes with NO corpse in `RemoveDeadUnits`'s pre-pass).

## Related areas
- [dev.md](dev.md) — interactive one-off driving (`drive-game`) vs these checked-in tests.
- [combat.md](combat.md) — `ResolvePendingAttack`, pounce/trample resolution ownership.
- [ai.md](ai.md) — archetypes vs the legacy `AIBehavior` enum the arena strips down to.
- [logging-diagnostics.md](logging-diagnostics.md) — `DebugLog` tags scenarios write to.
