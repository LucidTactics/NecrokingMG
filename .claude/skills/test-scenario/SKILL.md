---
name: test-scenario
description: Write or run a coded, headless regression scenario (--scenario) that exercises real rendering, systems, and screenshots for repeatable verification. Use when / Triggers — "write a regression test/scenario", "run a scenario", "add a repeatable headless test", "I need a deterministic rendering test", or any time you want a checked-in, re-runnable test rather than a one-off check.
---

# Test Scenarios (CLI)

Coded scenarios run the game headless, execute a scripted setup, and check log
output / screenshots. Reach for one when you need a **repeatable, headless
regression test** with real rendering/systems/screenshots, or when a check is
easier to express as code than as a live command sequence.

**For interactive/one-off checks, prefer the `drive-game` skill** (drive the
running game live). If a scenario-only capability is missing from the dev server,
prefer **adding a command to `ExecuteDevCommand` (`Necroking/Game1.cs`)** over
writing a new scenario — the `/cmd` channel forwards `{cmd,args,opts}` verbatim,
so no python-server edit is needed.

## Running a scenario
```bash
dotnet run --project Necroking/Necroking.csproj -- --scenario <name> --timeout <seconds>
```
Or run the built exe directly:
```bash
bin/Debug/Necroking.exe --scenario <name> --timeout <seconds>
```
Flags:
- `--scenario <name>` — required, selects which scenario to run
- `--timeout <seconds>` — optional (default 30), wall-clock time before force-quit
- `--speed <N>` — optional (default 1), N simulation ticks per frame for faster playback. Fixed 1/60s timestep per tick, so behavior is identical to speed 1. Attacks resolve instantly at speed > 1 (no animation wait)
- `--bgcolor R,G,B` — optional, set background color (default 45,35,25 dark muddy brown)
- `--headless` — optional, hides the game window (screenshots still work)

Behavior: opens a window (or runs hidden with `--headless`), runs the scenario,
exits automatically. Exit code 0 = pass, non-zero = fail. Stderr prints
`SCENARIO PASS` / `SCENARIO FAIL` with a summary.

**Prefer `--headless --speed 10`** to avoid stealing focus and finish faster.
Only omit `--headless` when the user wants to visually watch it play out.

## Available scenarios
The authoritative set is the `Register(...)` calls in
`Necroking/Scenario/ScenarioRegistry.cs` — check there for the current list
(names like `combat_test`, `skirmish`, `empty_map`, `spell_test`, `ai_behavior`,
`wall_test`, `grass_test`, `shadow_test`, `horde_follow`, etc.).

## Creating a new scenario
1. Create `Necroking/Scenario/Scenarios/MyScenario.cs`
2. Extend `ScenarioBase` (`Necroking/Scenario/ScenarioBase.cs`):
   - `Name` — short identifier string
   - `OnInit(Simulation sim)` — spawn units, set up state
   - `OnTick(Simulation sim, float dt)` — per-frame checks
   - `IsComplete` — true when done
   - `OnComplete(Simulation sim)` — final validation, return 0=pass / non-zero=fail
3. Register it in `Necroking/Scenario/ScenarioRegistry.cs` — add a `Register(...)` call in the static constructor
4. Build and test:
   ```bash
   dotnet build Necroking/Necroking.csproj && bin/Debug/Necroking.exe --scenario my_scenario --timeout 30 --headless --speed 10
   ```

### UI test scenarios
- For scenarios that need HUD/UI rendered, extend `UIScenarioBase` instead of `ScenarioBase`.
- UI test names should start with `UI` (e.g. `UIEmpty`, `UISpellBar`).
- Default scenarios suppress all UI/HUD rendering for clean visual output.

## Screenshots
- Set `DeferredScreenshot = "name"` to request a screenshot (taken by the main loop after rendering).
- Saved to `log/screenshots/<name>.png` (relative to the executable directory). The directory is cleared on each run.
- Use the `Read` tool on the PNG path to visually verify.
- Use `ZoomOnLocation(worldX, worldY, zoom)` to move the camera before each shot.
- **Zoom guidance for render tests**: always include at least one tightly-zoomed shot of the smallest testable element (single unit, single wall tile). Zoom 100-128 for close-ups, 40-80 for medium, 20-40 for overviews.

## Logging — design it deliberately
The scenario log is your primary debugging tool; if a scenario misbehaves, the
log is how you diagnose it without re-running.
- Use `DebugLog.Log(ScenarioLog, ...)` for all scenario logging (writes to `log/scenario.log`).
- **Log liberally** — it doesn't clutter gameplay logs. When in doubt, log more; skipping log lines is cheaper than a re-run.
- Log at every decision point (what it's about to do and why) and every state transition ("waiting for X", "X happened, now checking Y", "condition met, phase Z").
- Log measured values with context: not `damage=5` but `damage=5 (expected 3-8, attacker=Skeleton str=10, defender=Soldier prot=12)`.
- Log counts/summaries: unit counts per faction each tick, total damage, rounds elapsed. On completion, log a detailed summary.
- Combat log goes to `log/combat.log` — check `bin/Debug/log/combat.log`.

## Scenario tips
- No necromancer is spawned in scenarios; spawn via `sim.UnitsMut.AddUnit(pos, type)`.
- Unit indices shift after dead units are removed (swap-and-pop) — check by faction count, not stored indices.
- Unit types (UnitType enum): Skeleton, Soldier, Knight, Archer, Militia, Abomination, Necromancer, Dynamic.
- Scenarios render with no grass, no ground, no weather — just background color and sprites.
- Default background (45,35,25) gives good contrast for black-outlined sprites. Use `--bgcolor` when sprites blend in (e.g. `--bgcolor 80,80,100` lighter, `--bgcolor 0,0,0` pure black).

## Visual feature testing strategy
For primarily visual features (spell effects, particles, rendering), visual
clarity is the main debugging constraint. Progress in this order:

1. **Set up for maximum visibility.** Pick a contrasting background (green spell → dark purple `--bgcolor 40,30,50`; fire → dark blue). Disable weather (`sim.GameData.Settings.Weather.Enabled = false`) — fog/rain obscures and can be mistaken for the feature. Disable bloom initially (`BloomOverride = new BloomSettings { Enabled = false }`) — it smears pixel boundaries and hides whether raw rendering is correct.
2. **Test the simplest version first.** Start with the minimal renderable unit (single particle, one puff, one sprite); confirm it draws, animates, and responds to params. Add a visible fallback (red rectangle) to tell "not rendering" from "rendering invisibly".
3. **Add complexity incrementally.** Layer up to the full multi-particle system, then re-enable bloom and verify it enhances rather than destroys the visual, then test with weather if it should coexist.
4. **Test depth ordering last.** Add a game object (tree) behind the feature but visually overlapping, and another just in front. Verify correct Y-sort depth order. Add these clutter objects only after the feature itself works.
