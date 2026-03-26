# Necroking - Claude Code Instructions

## Build
```bash
cmake --build build --config Release
```
Always build in Release mode unless the user explicitly requests a Debug build.

**Important**: If the CMake cache has `CMAKE_BUILD_TYPE=Debug`, the `--config Release` flag alone does NOT switch to Release (single-config generator). You must reconfigure first:
```bash
cmake -S . -B build -DCMAKE_BUILD_TYPE=Release
```
Then rebuild. Debug builds are ~112MB vs ~5MB for Release and are far too slow for scenario iteration.

## File Conventions
- C++ source in `src/`, organized by subsystem (core, data, editor, game, movement, render, spatial, ui, world)
- Assets in `assets/` (Environment/, Effects/, Icons/, Sprites/, shaders/, UI/)
- Game data in `data/` (JSON/XML registries, maps, settings)
- Tools/scripts in `tools/` (Python utilities)

## Code Style
- Use `Vec2` (custom type) for world positions, `Vector2` (raylib) for screen positions
- Debug logging via `src/core/debug_log.h` — file-based, never console
- Editors use raygui immediate mode + custom widgets
- Shaders in `assets/shaders/`, GLSL 330

## Large File Safety
- **NEVER** attempt to read or upload files larger than 15 MB directly — this causes context overflow loops
- Before reading any data/log/binary file, check its size first (`ls -lh` or `wc -c`)
- If a file exceeds 15 MB, create a Python script in `tools/` to split it into chunks (< 10 MB each)
- For log files: process last chunk first (most recent entries are most relevant)
- For data files: split logically (e.g., by section/record) and process one chunk at a time

## Architecture & Code Organization

### Principle: Single Source of Truth
Every distinct behavior or pattern should have one canonical implementation. Before writing new code, check whether an existing system, utility, or pattern already solves the problem. The goal is fewer pieces of code doing the same function — when a bug is fixed, it's fixed in one place.

### Before Writing New Code
1. **Search first** — grep/glob for existing implementations that solve the same or a similar problem
2. **Reuse or extend** — prefer calling existing code (with different parameters or small improvements) over writing a parallel solution
3. **If nothing exists** — build it in a way that could become the standard approach for that category of problem

### When to Check with the User
- **User-facing features** (UI, graphics, game systems): If a relevant standard exists but Claude thinks a one-off solution is better, check with the user first — they may prefer consistency. If Claude thinks the standard is the right call (possibly with tweaks or improvements), proceed with own judgement.
- **Internal/deep systems** (utilities, data structures, helpers): Use own judgement. Reuse when it makes sense, create new when it doesn't.

### When Proposing a New Standard
If the user requests something that diverges from an established pattern, confirm whether this should become the new standard approach or is intentionally a one-off exception.

### Consolidation Design
When consolidating duplicate code and looking for consolidation opportunities:
- For complex structures: Shared component should own the mechanics (scroll, layout, hit-testing); caller owns the data (what items, what fields)
- Provide a **simple default path** (plain strings, int indices) and an **escape hatch** (custom render callback, item descriptors) for complex cases
- Do NOT abstract when the variance is structural (different control flow, different state machines) rather than data-level — that creates frameworks, not utilities

### Consolidation Opportunities
When encountering ad-hoc duplicate solutions during a task:
- **Small consolidation** — refactor it in place, mention it to the user
- **Large consolidation** — mention it to the user, add it to `memory/consolidation_opportunities.md` for later review

The consolidation list is reviewed periodically. Items are either consolidated or removed if consolidation isn't worthwhile.

### Standard Patterns Reference
Canonical implementations of common patterns are tracked in `memory/standard_patterns.md`. Consult this when starting work that might overlap with an existing solution. Update it when a new standard is established.

## Test Scenarios (CLI)

Automated test scenarios let you verify game behavior by running the game, executing a scenario, and checking log output.

### Running a scenario
```bash
./build/Necroking.exe --scenario <name> --timeout <seconds>
```
- `--scenario <name>` — required, selects which scenario to run
- `--timeout <seconds>` — optional (default 30), wall-clock time before force-quit
- `--speed <N>` — optional (default 1), run N simulation ticks per frame for faster playback. Uses fixed 1/60s timestep per tick so behavior is identical to speed 1. Attacks resolve instantly at speed > 1 (no animation wait)
- `--bgcolor R,G,B` — optional, set background color (default: 45,35,25 dark muddy brown)
- `--headless` — optional, hides the game window (no visual on screen, but screenshots still work)
- The game opens a window (or runs hidden with --headless), runs the scenario, then exits automatically
- Exit code 0 = pass, non-zero = fail
- Stderr prints `SCENARIO PASS` or `SCENARIO FAIL` with summary
- **Prefer --headless --speed 10** when running scenarios to avoid stealing focus and finish faster. Only omit --headless when the user wants to visually watch the scenario play out

### Available scenarios
- `combat_test` — Spawns a skeleton and soldier, lets them fight, validates combat.log
- `skirmish` — 20 skeletons vs 8 soldiers line battle
- `empty_map` — Verifies empty map has nothing spawned, saves screenshot
- `UIEmpty` — [UI test] Verifies UI/HUD is visible on empty map, saves screenshot
- `grass_test` — Isolated grass rendering: single cells, patches, mixed types (6 screenshots)

### Creating a new scenario
1. Create `src/scenario/my_scenario.h` and `src/scenario/my_scenario.cpp`
2. Extend `ScenarioBase` (defined in `src/scenario/scenario_base.h`):
   - `name()` — return a short identifier string
   - `onInit(Simulation& sim)` — spawn units, set up state
   - `onTick(Simulation& sim, float dt)` — per-frame checks
   - `isComplete()` — return true when done
   - `onComplete(Simulation& sim)` — final validation, return 0=pass / non-zero=fail
3. Add the .cpp to `GAME_SOURCES` in `CMakeLists.txt`
4. Register it in `src/scenario/scenario_registry.h` — add `#include` and an `if` branch in `createScenario()`
5. Build and test: `cmake --build build --config Release && ./build/Necroking.exe --scenario my_scenario --timeout 30`

### UI test scenarios
- For scenarios that need HUD/UI rendered, extend `UIScenarioBase` instead of `ScenarioBase`
- UI test names should start with "UI" (e.g. `UIEmpty`, `UISpellBar`)
- Default scenarios suppress all UI/HUD rendering for clean visual output

### Grass test scenarios
- Override `wantsGrass()` → `true` to enable grass rendering
- Access grass system via `grassSystem_` pointer (set by main.cpp before onInit)
- Start with `grassSystem_->fillGrassType(255)` to clear all grass, then place only what you need
- Use `setGrassType(cx, cy, typeIndex)` for individual cells — grid coords = `worldPos / cellSize()`

### Screenshots
- Use `scenarioScreenshot("name")` (from `scenario/scenario_screenshot.h`) to save PNGs
- Screenshots are saved to `build/log/screenshots/<name>.png`
- The screenshot directory is cleared on each scenario run
- Use `Read` tool on the PNG path to visually verify screenshots
- Use `zoomOnLocation(worldX, worldY, zoom)` to move the camera before each screenshot
- **Zoom guidance for render tests**: always include at least one tightly-zoomed shot of the smallest testable element (a single grass cell, a single unit, a single wall tile, etc.) so rendering issues are easy to spot. Use zoom 100-128 for close-ups, 40-80 for medium shots, 20-40 for overviews
- For multi-screenshot scenarios: start with the most isolated/zoomed views first, then build up to combinations and overview shots

### Testing preference
When asked to write an automated test, **always prefer creating a scenario first** (in-engine test with real rendering/systems/screenshots). Only fall back to external scripting (Python tools, etc.) if the scenario system can't handle what's needed — e.g., pure data validation, offline analysis, or things that don't require the game running.

### Scenario logging
When creating a new scenario, **spend real effort designing what to log**. The scenario log is your primary debugging tool — if a scenario fails or behaves unexpectedly, the log is how you figure out why without re-running.

- Use `debugLog("scenario", ...)` for all scenario logging (writes to `log/scenario.log`)
- **Log liberally** — the scenario log doesn't clutter gameplay logs, so there's no cost to over-logging
- When in doubt, **log more rather than less**. It's much easier to skip past extra log lines than to re-run a scenario because you didn't log enough
- Log at every decision point: what the scenario is about to do and why
- Log state transitions: "waiting for X", "X happened, now checking Y", "condition met, moving to phase Z"
- Log measured values with context: not just `damage=5` but `damage=5 (expected 3-8, attacker=Skeleton str=10, defender=Soldier prot=12)`
- Log counts and summaries: unit counts per faction each tick, total damage dealt, rounds elapsed
- On completion, log a detailed summary of what happened — this makes pass/fail output much more useful
- Combat log output goes to `log/combat.log` — check it with `cat build/log/combat.log`

### Scenario tips
- No necromancer is spawned in scenarios; use `sim.getUnitsMut().addUnit(pos, type)` to spawn units
- Unit indices can shift after dead units are removed (swap-and-pop) — check by faction count, not stored indices
- Available unit types: Skeleton, Soldier, Knight, Archer, Militia, Abomination (via UnitType enum)
- Scenarios render with no grass, no ground, no weather — just the background color and sprites
- Default background is dark muddy brown (45,35,25) — good contrast for sprites with black outlines
- Use `--bgcolor R,G,B` to change background when testing sprites that blend into the default (e.g. `--bgcolor 80,80,100` for lighter, `--bgcolor 0,0,0` for pure black)
- If a screenshot looks wrong because sprites are hard to see, try a different background color

## Auto-accept Patterns
- Reading any file in the project
- Editing files in `src/`, `assets/shaders/`, `tools/`
- Creating new files in `src/`, `assets/`, `tools/`, `data/`
- Running `cmake --build build`
- Running Python scripts in `tools/`
- Running `ls`, `mkdir` for directory inspection
- Glob and Grep searches within the project
- Running scenario tests via `./Necroking.exe --scenario`

## Bash

TRY TO AVOID USING COMPOSED BASIC OPERATORS LIKE cs XXX && git info, THEY FORCE UNNECESSARY USER CONFIRMATIONS!