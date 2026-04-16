# Necroking (MonoGame C#) - Claude Code Instructions

## Git Policy
- **Commits**: OK to commit freely when a feature is known to be working. Good initiative.
- **Push**: Always ask the user for permission before pushing. Never push without explicit approval.

## Build
```bash
dotnet build Necroking/Necroking.csproj
```
Output goes to `bin/Debug/`. For Release: `dotnet build Necroking/Necroking.csproj -c Release`

### Publish (self-contained, for distribution)
Creates a self-contained build for people without .NET installed.
**NEVER use `-c Debug` or `-c Release` with `dotnet publish --self-contained`** — it pollutes those output folders with runtime DLLs (`hostfxr.dll`, `coreclr.dll`) and breaks the normal exe. Always use `-c Publish`:
```bash
dotnet publish Necroking/Necroking.csproj -c Publish -r win-x64 --self-contained -o bin/Publish
```
The exe references `assets/` and `data/` at the project root (two levels up from `bin/Publish/`) — no file copying needed.

## Directory Layout
```
NecrokingMG/
  assets/          (sprites, textures, UI, effects, fonts, items)
  data/            (ALL JSON: game data, maps/, env_defs, spellbar)
  resources/       (shaders .fx, fonts .spritefont, Content.mgcb)
  bin/             (build output — exe + runtime DLLs only)
    Publish/
    Debug/
  Necroking/       (C# source code, .csproj)
  Necroking.Editor/
  tools/
  todos/           (temporary research/task notes for future sessions)
```

## File Conventions
- C# source in `Necroking/`, organized by subsystem (Algorithm, Core, Data, Editor, Game, Movement, Render, Scenario, Spatial, UI, World)
- Main game loop in `Necroking/Game1.cs`, entry point in `Necroking/Program.cs`
- Assets at root `assets/` (Environment/, Effects/, Items/, Sprites/, UI/, fonts/)
- Game data at root `data/` (JSON registries, maps/, settings)
- Maps in `data/maps/` (default.json, triggers, roads, wall_defs)
- Shaders and fonts in `resources/`
- Tools/scripts in `tools/`
- All paths resolved via `GamePaths.Resolve()` — no DualSave, no file copying to build output
- **Asset paths must be relative** (e.g. `assets/Environment/Trees/Oak1.png`), never absolute (e.g. `E:/Nightfall/NecrokingMG/assets/...`). This applies to JSON data files (`env_defs.json`, etc.), C# code, and editor-saved paths. `GamePaths.Resolve()` converts relative paths to absolute at runtime.

## Code Style
- Use `Vec2` (custom type in `Core/`) for world positions, `Vector2` (MonoGame/XNA) for screen positions
- Debug logging via `Core/DebugLog.cs` — file-based to `log/` directory, never console
- Editors use immediate mode UI in `Editor/`
- Shaders in `Necroking/assets/shaders/`, GLSL/HLSL

## UI Text Rendering
- SpriteBatch uses `SamplerState.PointClamp` — text drawn at sub-pixel positions gets aliasing artifacts
- **Always round text positions to integer pixels**: `new Vector2((int)x, (int)y)`
- `EditorBase.DrawText` already rounds internally, but any direct `DrawString` calls (e.g. in `Game1.cs`) must round manually
- When centering text with `MeasureString`, the division produces floats — cast to `int` before passing to draw

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
dotnet run --project Necroking/Necroking.csproj -- --scenario <name> --timeout <seconds>
```
Or run the built executable directly:
```bash
bin/Debug/Necroking.exe --scenario <name> --timeout <seconds>
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
- `empty_map` — Verifies empty map has nothing spawned
- `spell_test` — Fires 5 fireballs at enemies, validates projectile spawning and damage
- `combat_log` — Validates combat log entries are written
- `ai_behavior` — Tests archer, guard, and attack-necro AI behaviors
- `building_placement` — Places and validates a building
- `ground_test` — Ground rendering screenshot test
- `order_attack` — 12 skeletons march to target, fight 2 soldiers, return to horde
- `god_ray` — Caster AI fires god ray spells at skeleton cluster
- `priest_battle` — Priest caster + 5 soldiers vs 20 skeletons
- `patrol_encounter` — 6 skeleton camp attacked by 4 patrolling soldiers
- `wall_test` — Wall patterns placed on grid, pathfinding rebuilt
- `wall_trap` — 10 soldiers trapped inside wall ring, validates all survive
- `wall_gate` — Wall ring with north gate, soldiers navigate through via A*
- `UIEmpty` — [UI test] Verifies UI/HUD is visible on empty map
- `grass_test` — Isolated grass rendering: single cells, patches, mixed types (6 screenshots)
- `corpse_worker` — Skeleton worker + corpses + grinder building setup
- `raid_workers` — 5 skeleton outposts with workers raided by 5 soldiers
- `undead_raid` — Undead base produces skeletons, raids peasant village with huts
- `shadow_test` — Shadow consistency across L-shaped wall and units (4 screenshots)
- `grass_wall_depth` — Depth ordering between grass and walls (4 screenshots)
- `road_rim` — Road rendering test with different edge softness/rim settings (4 screenshots)
- `flee_when_hit` — FleeWhenHit AI: deer flees 15 units from attacker when engaged
- `neutral_fight_back` — NeutralFightBack AI: peaceful until hit, then fights attacker
- `wolf_hit_and_run` — WolfHitAndRun AI: 4-phase engage→attack→disengage→cooldown cycle
- `move_to_point` — MoveToPoint AI: unit pathfinds to destination
- `retarget` — AttackClosestRetarget AI: periodic 2s retargeting to closest enemy
- `horde_follow` — Horde formation following necromancer + engagement with enemies

### Creating a new scenario
1. Create `Necroking/Scenario/Scenarios/MyScenario.cs`
2. Extend `ScenarioBase` (defined in `Necroking/Scenario/ScenarioBase.cs`):
   - `Name` — property returning a short identifier string
   - `OnInit(Simulation sim)` — spawn units, set up state
   - `OnTick(Simulation sim, float dt)` — per-frame checks
   - `IsComplete` — property returning true when done
   - `OnComplete(Simulation sim)` — final validation, return 0=pass / non-zero=fail
3. Register it in `Necroking/Scenario/ScenarioRegistry.cs` — add a `Register(...)` call in the static constructor
4. Build and test: `dotnet build Necroking/Necroking.csproj && bin/Debug/Necroking.exe --scenario my_scenario --timeout 30 --headless --speed 10`

### UI test scenarios
- For scenarios that need HUD/UI rendered, extend `UIScenarioBase` instead of `ScenarioBase`
- UI test names should start with "UI" (e.g. `UIEmpty`, `UISpellBar`)
- Default scenarios suppress all UI/HUD rendering for clean visual output

### Screenshots
- Set `DeferredScreenshot = "name"` to request a screenshot (taken by main loop after rendering)
- Screenshots are saved to `log/screenshots/<name>.png` (relative to executable directory)
- The screenshot directory is cleared on each scenario run
- Use `Read` tool on the PNG path to visually verify screenshots
- Use `ZoomOnLocation(worldX, worldY, zoom)` to move the camera before each screenshot
- **Zoom guidance for render tests**: always include at least one tightly-zoomed shot of the smallest testable element (a single unit, a single wall tile, etc.) so rendering issues are easy to spot. Use zoom 100-128 for close-ups, 40-80 for medium shots, 20-40 for overviews

### Testing preference
When asked to write an automated test, **always prefer creating a scenario first** (in-engine test with real rendering/systems/screenshots). Only fall back to external scripting (Python tools, etc.) if the scenario system can't handle what's needed — e.g., pure data validation, offline analysis, or things that don't require the game running.

### Scenario logging
When creating a new scenario, **spend real effort designing what to log**. The scenario log is your primary debugging tool — if a scenario fails or behaves unexpectedly, the log is how you figure out why without re-running.

- Use `DebugLog.Log(ScenarioLog, ...)` for all scenario logging (writes to `log/scenario.log`)
- **Log liberally** — the scenario log doesn't clutter gameplay logs, so there's no cost to over-logging
- When in doubt, **log more rather than less**. It's much easier to skip past extra log lines than to re-run a scenario because you didn't log enough
- Log at every decision point: what the scenario is about to do and why
- Log state transitions: "waiting for X", "X happened, now checking Y", "condition met, moving to phase Z"
- Log measured values with context: not just `damage=5` but `damage=5 (expected 3-8, attacker=Skeleton str=10, defender=Soldier prot=12)`
- Log counts and summaries: unit counts per faction each tick, total damage dealt, rounds elapsed
- On completion, log a detailed summary of what happened — this makes pass/fail output much more useful
- Combat log output goes to `log/combat.log` — check it with `cat bin/Debug/log/combat.log`

### Scenario tips
- No necromancer is spawned in scenarios; use `sim.UnitsMut.AddUnit(pos, type)` to spawn units
- Unit indices can shift after dead units are removed (swap-and-pop) — check by faction count, not stored indices
- Available unit types: Skeleton, Soldier, Knight, Archer, Militia, Abomination, Necromancer, Dynamic (via UnitType enum)
- Scenarios render with no grass, no ground, no weather — just the background color and sprites
- Default background is dark muddy brown (45,35,25) — good contrast for sprites with black outlines
- Use `--bgcolor R,G,B` to change background when testing sprites that blend into the default (e.g. `--bgcolor 80,80,100` for lighter, `--bgcolor 0,0,0` for pure black)
- If a screenshot looks wrong because sprites are hard to see, try a different background color

### Visual feature testing strategy
When a scenario tests a primarily visual feature (spell effects, particles, rendering), visual clarity is the main constraint for debugging. Follow this progression:

**1. Set up for maximum visibility**
- Choose a background color that contrasts with the feature being tested. Green spell? Use dark purple (`--bgcolor 40,30,50`). Fire effect? Use dark blue. Think about units and other elements in the scene too
- Disable weather (`sim.GameData.Settings.Weather.Enabled = false`) — weather fog/rain obscures the feature and can be mistaken for it
- Disable bloom for initial testing (`BloomOverride = new BloomSettings { Enabled = false }`) — bloom smears pixel boundaries and makes it impossible to tell if the raw rendering is correct

**2. Test the simplest version first**
- Start with the minimal renderable unit: a single particle, one puff, one sprite. Confirm it draws at all, animates correctly, and responds to parameters
- Only after the basic element works, layer up to the full system (multiple particles, rings, glow passes)
- If something doesn't render, add a visible fallback (e.g. a red rectangle) so you can distinguish "not rendering" from "rendering invisibly"

**3. Add complexity incrementally**
- Once the single element works, add the multi-particle system
- Then re-enable bloom and verify it enhances rather than destroys the visual
- Then test with weather enabled if the feature should coexist with weather

**4. Test depth ordering last**
- As a final step, add a game object (tree) positioned behind the feature but visually overlapping, and another object just in front of it
- Verify the feature renders in the correct depth order using Y-sorting or whatever depth system is appropriate
- These test objects add visual clutter, so only add them after the feature itself is confirmed working

## Auto-accept Patterns
- Reading any file in the project
- Editing files in `Necroking/`, `tools/`
- Creating new files in `Necroking/`, `tools/`
- Running `dotnet build`
- Running Python scripts in `tools/`
- Running `ls`, `mkdir` for directory inspection
- Glob and Grep searches within the project
- Running scenario tests via `Necroking.exe --scenario`

## Bash

Try to avoid using multi bash commands like cs XXX && git info, they force unnecesary user confirmations!

## Todos Directory (`todos/`)
Temporary research notes and task summaries for future sessions. Each file covers one topic with context, what's done, what's left, and how to debug. Check this directory at the start of relevant work — complete items get deleted. Not for permanent knowledge (use memory for that) or code TODOs (use comments).

## C++ Migration

This project migrated from ../Necroking, refer to its files when trying to reimplement features that worked there.