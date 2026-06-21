# Necroking (MonoGame C#) - Claude Code Instructions

## North Star: Satisfaction
This is the lens for designing and refining **every** system — combat, spells, reanimation, crafting, automation. Before building or changing a system, ask: *does this satisfy?*

**Satisfaction = anticipation that gets rewarded.** The build-up is a promise; the payoff has to keep it, and keep it in proportion. A sword's wind-up makes the strike land harder; a fireball's travel builds toward its impact.
- Fireball that ignites enemies and hurls their bodies back → the payoff honors the anticipation. Deeply satisfying.
- Fireball that ticks a little HP and kills nothing → the anticipation was a lie. Flat.

**Anticipation runs on real-world intuition.** Players unconsciously predict outcomes by analogy to the physical world: heavy things hit hard, fire spreads and lingers, explosions throw bodies, a big swing carries weight. When a system behaves the way reality "should," the payoff feels *earned*. When it violates that intuition — a massive blow that moves nothing — it feels fake, no matter how correct the math underneath is.

**Cut the boring and the annoying.** Tedious, repetitive, or fiddly actions are never satisfying however well-engineered. If a task is a chore, make it feel good or automate it away.

**Protect pacing; design out frustration.** Some grind is fine, but the player's time is precious. Learning should be fun, and big moments must not bog down — never make a large battle crawl by waiting on units' animations in sequence (the AoW4 problem). Keep the density of satisfying events high.

**The test for any feature:** What anticipation does it build, and does the payoff honor that anticipation — visibly, physically, audibly? A system that computes a correct result but doesn't *show* the consequence will feel flat regardless of how solid the simulation is.

## Git Policy
- **Commits**: OK to commit freely when a feature is known to be working. Good initiative.
- **Push**: Always ask the user for permission before pushing. Never push without explicit approval.

### Collaborator / Drive-sync git discipline (IMPORTANT)
This repo is shared with a **collaborator (a friend)**, and that friend's machine mirrors its whole
workspace to a shared **Google Drive** folder instead of relying on git. So "the other machine"
below means **the friend's Drive-synced clone**, not a second machine of the user's own.
(Separately, the user sometimes runs **multiple Claude sessions against this same working
directory** — that's the same tree, no git/Drive sync between them; the risk there is concurrent
edits/builds colliding, not stale commits. The rules below are about the Drive-synced friend.)

**Drive sync is NOT a substitute for git** — it has already produced a broken `origin/master` where
committed code referenced symbols whose defining files were never committed/pushed, so the remote
did not compile. The assistant must actively manage git for the user, who may not be comfortable
with git:

1. **Build before every push.** Run `dotnet build Necroking/Necroking.csproj` and confirm it
   succeeds. **Never push code that does not build.** If a relevant scenario exists, run it too
   (`bin/Debug/Necroking.exe --scenario <name> --headless --speed 10`).
2. **Check for untracked/uncommitted files before committing.** Run `git status` and read it.
   New `.cs` files are the usual culprit — a commit that references a new symbol but omits the file
   that *defines* it yields a remote that won't compile. Stage all related changes together so a
   feature's code and the definitions it needs land in the **same commit** (review what's new, then
   `git add -A`). Do not leave a feature half-committed.
3. **Remind the user to push after a working feature is committed.** A local commit the other
   machine can't see is the failure mode here; Drive "syncing" can silently lose or partial it.
   Say something like: *"This is committed and builds — want me to push it to origin now?"*
4. **If `git status` shows the branch is ahead of origin, surface it.** e.g. *"You have N commits
   not yet pushed — push so the other machine stays in sync?"* Still ask permission before the
   actual push (per the rule above), but **do prompt** — don't let working work sit unpushed.
5. **Pull before starting fresh work.** If a pull won't fast-forward (histories diverged because
   someone pushed from the other machine), stop and tell the user rather than force-resolving.

### Per-machine user settings (do NOT commit them)
As of 2026-06-16, the **per-machine user files live in the `user settings/` folder** at the project
root, which is **`.gitignore`d** — never shared via git. These are: `settings.json` (ESC-menu
settings), `weather.json` (weather presets), and `spellbar.json` (spell-bar loadout). The game
**seeds** each from its shipped `data/` default on first run (`GamePaths.SeededUserFile`), then writes
only to the user copy — so **`data/settings.json` / `data/weather.json` / `data/spellbar.json` no
longer churn**.

**One-time migration when an older clone syncs (the other machine):** after pulling, if `git status`
shows any of those three **`data/*.json`** files modified (the machine's old local values), run the
game once — it copies them into `user settings/` automatically — then
`git checkout -- data/settings.json data/weather.json data/spellbar.json` to drop the now-redundant
tracked changes. The machine's settings are now local. (If they're already clean, nothing to do.)
Never re-add `user settings/` to git or write these back into `data/`.

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
  docs/            (on-demand reference: stable info to look up, not in context each session)
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

## Map Content Lives In The Map, Not In Code
**If the user asks for something to be placed in the world — a building, foragable, prop, decoration, unit — add it to the map JSON, not to a code path that spawns it at startup.** Hardcoded startup spawns step on the player's map edits: they save the map without the object, restart, and the code re-spawns it. The save *worked*; the load just stomps it.

- **Adding world content** → edit `data/maps/default.json` directly (or via the in-game map editor and save). Use `tools/` scripts when the JSON is too large to edit interactively.
- **Don't** write `_envSystem.AddObject(...)` or `SpawnUnit(...)` calls in `LoadContent` / `LoadGame` / startup paths unless the user explicitly asks for "always re-spawn this on game start regardless of the map." If unsure, ask.
- **Exception — true fallbacks:** the necromancer fallback at the start of `LoadGame` ("if no necromancer in placed units, spawn Wretched at map center") is fine because it only fires when the map provides nothing. Distinguish "fill in what's missing" from "always add on top of what's there."
- **Past offenders (now removed):** `SpawnStarterMushroom` and `SpawnStarterBlightAltar` in `Game1.cs` unconditionally inserted a Deathcap and a Blight Altar near the necromancer every launch, making them un-deletable via the map editor. Both removed 2026-05-13.

## UI Text Rendering
- SpriteBatch uses `SamplerState.PointClamp` — text drawn at sub-pixel positions gets aliasing artifacts
- **Always round text positions to integer pixels**: `new Vector2((int)x, (int)y)`
- `EditorBase.DrawText` already rounds internally, but any direct `DrawString` calls (e.g. in `Game1.cs`) must round manually
- When centering text with `MeasureString`, the division produces floats — cast to `int` before passing to draw

## Importing UI Designs (CSS / HTML / JSX mocks)
Before reproducing any HTML/CSS/JSX design (Claude Design or otherwise) in MonoGame, read [todos/css_rendering.md](todos/css_rendering.md). It covers what translates cleanly, what doesn't, and why `Necroking/Render/UIGfx.cs` is a flagged failed-attempt reference rather than a reusable utility.

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

## Dev Control Server (drive the running game via the preview interface)

For interactive, one-off checks — spawn units, set up a situation, move the
camera, open a UI panel, screenshot, read state — **drive the *running* game
through the preview interface.** This is far faster than the
write-scenario→rebuild→run loop and the preferred way to verify almost anything
visual.

**ALWAYS use the Claude preview MCP tools — not Bash/curl.** The preview tools
(`preview_start`, `preview_eval`, `preview_screenshot`, ...) are approved by name,
so they run **without per-command permission prompts**; raw `curl`/Bash to the
server prompts on every call and is slower. Reach for curl only if the preview
tools are genuinely unavailable.

**Surface without the `Claude_Preview` tools (e.g. the VS Code extension):** those
`mcp__Claude_Preview__*` tools are wired up **only in the desktop Claude Code app**.
For every other surface this repo ships its own project MCP server, **`necroking`**
(`.mcp.json` → `tools/necro_mcp.py`, dependency-free / stdlib-only). It exposes typed,
no-shell tools — `necro_status`, `necro_start`, `necro_cmd` (the main driver — same
`{cmd,args,opts}` commands as `ExecuteDevCommand`), `necro_screenshot` (returns the
frame inline as an image), `necro_restart`, `necro_stop` — that drive the *same*
supervisor. **Prefer these** when `Claude_Preview` is absent; they're the safe
equivalent (no bash to compose, allowlisted by name). If for some reason the
`necroking` server isn't loaded, fall back to `tools/devctl.py` (one allowlisted bash
command: `python tools/devctl.py shot fight` → `Read` the printed PNG). Full reference:
[docs/devpreview.md](docs/devpreview.md). New `.mcp.json` servers need a one-time trust
approval and a Claude Code reload to appear.

**Topology:** preview MCP tool → Python supervisor (`tools/devserver.py`, port
8777, persistent) → game's in-process HTTP listener (`Necroking/Dev/DevServer.cs`,
port 8778, enabled by `--devserver`). The supervisor owns the game process, so the
exe can be rebuilt + relaunched **without restarting the supervisor**.

**Workflow:**
1. `preview_start("necroking-dev")` → `serverId` (launches the supervisor; the page
   auto-starts the game headless at 1280x720). **Do not run `python tools/devserver.py`
   from Bash yourself** — `preview_start` owns the supervisor. The launch config lives
   in gitignored `.claude/launch.json`; on a fresh clone it won't exist, so create it
   first with exactly this (then call `preview_start`):
   ```json
   {"version":"0.0.1","configurations":[{"name":"necroking-dev","runtimeExecutable":"python","runtimeArgs":["tools/devserver.py"],"port":8777}]}
   ```
   (use `python3` or `py` as `runtimeExecutable` if `python` isn't on PATH).
2. `preview_eval(id, "window.dev('panel',['spell_editor'])")` — `window.dev(cmd,
   args, opts)` POSTs to `/cmd`, awaits the game, and returns the JSON result. Chain
   steps in an async IIFE.
3. `preview_screenshot(id)` — captures the live view (the dashboard frame refreshes
   ~1 Hz via `/frame`). Use this to confirm what a panel/entry looks like.
4. **After a C# change**, rebuild + relaunch through the supervisor:
   `preview_eval(id, "fetch('/game/restart',{method:'POST',body:'{\"build\":true}'}).then(r=>r.json())")`
   — build errors come back in the JSON (`build.errors`).

**Stopping the game (do NOT taskkill):** when the exe is locked for a build, or
when you're done driving it, **stop the game through the server, never kill the
process** — `preview_eval(id, "fetch('/game/stop',{method:'POST',body:'{}'})")`.
The supervisor owns the process (Windows Job Object) and the headless game is
hidden from the taskbar, so a force-killed PID orphans the supervisor's bookkeeping
and a forgotten game idles invisibly. `/game/restart {"build":true}` already stops
it for you. The supervisor itself can stay up (cheap; holds the pinned A/B frame).

**Game commands** (see `ExecuteDevCommand` in `Game1.cs`):
- `ping` · `state` — liveness / JSON snapshot of game state
- `help` (alias `commands`) — list every dev command + the selector syntax. Run
  this to discover what's available without reading the source.
- `start_game [map]` — load a map into gameplay
- `spawn <type> <x> <y>` · `camera <x> <y> [zoom]` · `speed <n>` · `pause` · `resume`
- `screenshot [name]` with `opts`: `no_ui`, `no_ground`, `downsample_to` (`"WxH"` |
  `"full"`; default 640x360)
- `menu <new_game|test_map|scenarios|main_menu|quit>` — press a main-menu button
- **Units & combat** (the same primitives scenarios use, exposed live — added 2026-06-21):
  - **Selectors** — most commands below take a `<selector>` resolving to one or more
    units: `all`/`*`, `necro`, a faction (`undead`/`human`/`animal`), a bare index
    (`9`), `id:<n>` (unit Id), or a UnitDef id (`skeleton`) / UnitType name
    (`Soldier`), case-insensitive. Faction/def/type/`all` match ALIVE units only.
  - `spawn_def <unitID> <x> <y> [count]` — spawn by **UnitDef id** (full def
    stats/faction/AI), unlike `spawn` which takes a bare `UnitType`. `count` spawns
    a small line. Def ids are lowercase (`skeleton`, `soldier`, `zombiewolf`).
  - `units [selector]` — list matched units as JSON (idx, id, type, def, faction,
    ai, x/y, hp/maxHp, mana, alive, inCombat). `unit <selector>` dumps the first match.
  - `combat_log [n]` — last N combat-log entries (attacker/defender/outcome/damage).
  - `damage <selector> <amount>` · `kill <selector>` · `remove <selector>` (delete).
  - `set_ai <selector> <AIBehavior>` · `move <selector> <x> <y>` (AI=MoveToPoint).
  - `set_hp <selector> <hp> [maxHp]` · `set_mana <selector|necro> <mana> [maxMana]`.
  - `mark <selector|clear>` · `unmark [selector]` — draw a persistent white outline
    box around matching units (independent of mouse hover) so a screenshot can point
    at a specific unit. `mark clear` / argless `unmark` removes all marks.
  - `cast <spellID> <x> <y>` — necromancer casts via the full player pipeline (may
    fail on mana/cooldown/range — `set_mana necro 9999` first if needed).
  - `fireball <x> <y> [dmg] [radius] [name]` — spawn a projectile directly
    (deterministic, no mana/anim gating).
- **Batch scripts with waits** (`batch` / `job`) — run a sequence of commands with
  timed waits between them so screenshots land at exact moments. The game steps the
  script over its own update loop (sim clock, exactly like a scenario's `OnTick`), so
  `batch` returns a `jobId` immediately and you **poll `job`** for progress/results
  (never blocks past the HTTP timeout). `opts.script` is an array of steps:
  `{cmd,args,opts}` (any game command) · `{wait:<simSecs>}` (frozen while paused) ·
  `{wait_real:<secs>}` · `{wait_frames:<n>}` · `{shot:"name", ...screenshotOpts}`
  (sugar for a screenshot step). `job` returns `{done,step,total,results:[...]}`
  where `results[i]` is the raw response of command step i (screenshot steps yield
  their PNG path); `job cancel` aborts. Example:
  ```js
  const {result:{jobId}} = await window.dev('batch',[],{script:[
    {cmd:'camera',args:[x,y,48]}, {cmd:'speed',args:[4]},
    {shot:'t0'}, {wait:2.0}, {shot:'t2'}, {wait:2.0}, {shot:'t4'},
    {cmd:'units',args:['all']},
  ]});
  let st; do { await new Promise(r=>setTimeout(r,300)); st=(await window.dev('job',[jobId])).result; } while(!st.done);
  // st.results holds each step's reply; PNGs are at bin/Debug/log/screenshots/<name>.png
  ```
- **UI panels** (added 2026-06-21):
  - `panels` — list every previewable panel, its tabs, the overlays, and the
    current state. **Run this first** to discover valid names.
  - `panel <name> [tab]` — switch to a UI panel; auto-starts a default game for
    panels that need a world. Names: `main_menu`, `scenarios`, `game`, `pause`,
    `settings`, `unit_editor`, `spell_editor`, `map_editor`, `ui_editor`,
    `item_editor`. Optional 2nd arg sets a tab on it.
  - `tab <name>` — set the active tab on the open panel (Settings: Bloom/Shadow/…;
    Map editor: Ground/Grass/Objects/Walls/…; UI editor: NineSlices/Elements/Widgets).
  - `overlay <name> [open|close|toggle]` — in-game overlays: `inventory`,
    `character_stats`, `skill_book`, `grimoire`, `character_sheet`.
  - `select <name|id|index>` — select an entry in the open editor (unit/spell/item/ui)
    so its preview/detail renders. Accepts a numeric index, a def id, or a display
    name. e.g. `panel spell_editor` then `select Fireball`.

**Screenshots — two ways:**
- `preview_screenshot(id)` captures the whole **dashboard page** (live game frame +
  command log). Best for a quick glance or to watch progress.
- To **analyze just the game frame**, run `window.dev('screenshot',['name'], opts)`
  via `preview_eval` — it returns the path and the PNG lands at
  `bin/Debug/log/screenshots/<name>.png`. Then **`Read` that file** (the Read tool is
  approved → no prompt). You get the clean frame at the downsample size, no dashboard
  chrome — this is the right way to actually inspect a result.
- `opts`: `{no_ui:true}` hides the HUD; `{no_ground:true}` drops ground+grass (the
  scenario black look); `{downsample_to:"WxH"}` or `"full"` picks the returned size
  (default 640x360 = half the 1280x720 render — small but readable).

**Add a new command when a test needs one — do this freely; it's the point.** If a
check needs a verb the server doesn't have, ADD IT instead of working around it. One
`case` in `ExecuteDevCommand` (`Game1.cs`) + a rebuild; the `/cmd` channel forwards
`{cmd,args,opts}` verbatim so **no `tools/devserver.py` change is needed** (and you
must not edit it — that forces a supervisor restart; ask the user first if you think
it truly must change). Pattern:

```csharp
// in ExecuteDevCommand(Necroking.Dev.DevCommand c), Game1.cs:
case "kill_faction":                       // window.dev('kill_faction',['Human'])
{
    if (c.Args.Length < 1) { c.Complete(Necroking.Dev.DevServer.Error("need <faction>")); break; }
    var fac = Enum.Parse<Data.Faction>(c.Args[0], true);
    int n = 0;
    for (int i = _sim.Units.Count - 1; i >= 0; i--)   // backwards: RemoveUnit shifts indices
        if (_sim.Units[i].Faction == fac) { _sim.UnitsMut.RemoveUnit(i); n++; }
    c.Complete(Necroking.Dev.DevServer.Ok($"removed {n}"));
    break;
}
```
- Runs on the **game main thread** (drained in `Update`), so touching `_sim`,
  `_camera`, `_gameData`, UI panels, etc. directly is safe — the HTTP thread only
  queues.
- Args: `c.Args[i]` (positional strings); `DevFloat(s)` parses a float;
  `c.Opt("k")` / `c.OptBool("k")` read named opts from the `opts` object.
- Reply: `c.Complete(DevServer.Ok("msg"))`, `DevServer.Error("msg")`, or
  `DevServer.OkRaw("<json>")` to return a structured object (see how `state` builds
  its JSON).
- **Deferred results** (need a rendered frame, like a screenshot): stash a pending
  field and call `c.Complete(...)` later from `Draw` instead of blocking — follow the
  `_pendingDevScreenshot` path.
- After adding: rebuild via `/game/restart {"build":true}`, then call it with
  `window.dev('your_verb',[...],{...})` (or `window.devRaw({cmd,args,opts})`).

**Gotchas:**
- **Spawn faction is implied by `UnitType`**: Skeleton/Abomination → Undead; the rest
  (Soldier/Knight/Militia/Archer) → Human. A human spawned next to the necromancer
  will attack and can kill it — spawn at a distance, or spawn undead for a friendly
  scene. Types: Necromancer, Skeleton, Abomination, Militia, Soldier, Knight, Archer,
  Dynamic.
- **World coordinates** (Vec2). Read `state` for the necromancer's `x,y` and anchor
  spawns/camera off it (default map necromancer ≈ 2096,1882).
- A screenshot is captured one frame later in `Draw`; `window.dev` awaits until the
  PNG is written, so the returned path is ready to `Read` immediately.
- `preview_eval` awaits the returned promise and serialises it to JSON; wrap
  multi-step sequences in an `async`-IIFE and `return` the final value.

**Recipe — set up a fight, speed it up, analyze it:**
```js
// preview_eval(id, "<this>")
(async()=>{
  await window.dev('menu',['new_game']);
  const s = await window.dev('state'); const x=s.result.necromancer.x, y=s.result.necromancer.y;
  await window.dev('spawn',['Skeleton',x-3,y]);
  await window.dev('spawn',['Soldier',x+3,y]);
  await window.dev('camera',[x,y,48]);
  await window.dev('speed',[4]);
  return await window.dev('screenshot',['fight']);   // then Read bin/Debug/log/screenshots/fight.png
})()
```
**Recipe — inspect an editor entry:** `window.dev('panel',['spell_editor'])` then
`window.dev('select',['Fireball'])`, then `preview_screenshot(id)`.

## Test Scenarios (CLI) — archived to `docs/testing-scenarios.md`
Coded, headless regression scenarios (`--scenario <name>`) are now a **secondary**
workflow — the Dev Control Server above is the primary driver. Full reference (how
to run, the scenario list, how to write one, logging + visual-testing guidance)
lives in **[docs/testing-scenarios.md](docs/testing-scenarios.md)**. Reach for it
when you need a *repeatable regression test* rather than a live one-off check; for
anything interactive, prefer the dev server (and add a `/cmd` command if one is
missing).

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

**Always prefer the dedicated `Grep` and `Glob` tools over `grep`/`find`/`rg` run
through the Bash tool, and prefer the `Read` tool over `cat`/`head`/`tail`.** The
dedicated tools integrate with the permission UI (no per-command confirmation
prompts), return clickable file links, and are faster. Only drop to a Bash search
script when a dedicated tool genuinely can't do the job.

## Todos Directory (`todos/`)
Temporary research notes and task summaries for future sessions. Each file covers one topic with context, what's done, what's left, and how to debug. Check this directory at the start of relevant work — complete items get deleted. Not for permanent knowledge (use memory for that) or code TODOs (use comments).

## Docs Directory (`docs/`)
On-demand **reference** material: stable, still-useful info that doesn't need to be
in CLAUDE.md (and thus in context) every session — workflows that are now secondary,
deep how-to guides, subsystem references. Unlike `todos/`, these don't get deleted
when "done"; they're looked up when relevant. When something in CLAUDE.md is good to
keep but no longer a primary driver, move it here and leave a one-line pointer in
CLAUDE.md. Current contents: [docs/testing-scenarios.md](docs/testing-scenarios.md)
(the coded `--scenario` test harness); [docs/devpreview.md](docs/devpreview.md)
(driving the live game via the `necroking` MCP server / `tools/devctl.py` fallback
when the `Claude_Preview` tools are absent, e.g. in VS Code).

## C++ Migration

This project migrated from ../Necroking, refer to its files when trying to reimplement features that worked there.