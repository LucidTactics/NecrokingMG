---
name: drive-game
description: Drive the RUNNING Necroking game live to verify things visually — spawn units, set up fights, move the camera, open UI panels/editors, screenshot, and read game state — without the edit→rebuild→run loop. Use when — "verify a visual change", "spawn a unit / set up a fight", "screenshot the running game", "drive the live game", "open a panel/editor live", "check what X looks like in-game", "see/interact with the game window".
---

# Drive the live game

Drive the *running* game to verify almost anything visual — far faster than the
write-scenario→rebuild→run loop. Spawn units, set up a situation, move the camera, open a
UI panel, screenshot, read state.

## Topology

```
Claude --(MCP tool | python)--> supervisor (:8777) --proxy--> game (:8778)
```

- Supervisor = `tools/devserver.py` (port 8777, persistent, owns the game process via a
  Windows Job Object). Do NOT edit it — add control in C# instead (see below). Ask the
  user before any change to it.
- Game in-process HTTP listener = `Necroking/Dev/DevServer.cs` (port 8778, enabled by
  `--devserver`). Commands run on the game main thread, drained in `Update`.
- The supervisor owns the game, so the exe can be rebuilt + relaunched **without
  restarting the supervisor**.

## Which interface to use (prefer the highest available tier)

1. **`Claude_Preview` MCP tools** (`preview_start`, `preview_eval`, `preview_screenshot`, …)
   — wired up **only in the desktop Claude Code app**. Approved by name, no per-command
   prompts.
2. **`necroking` project MCP server** (`.mcp.json` → `tools/necro_mcp.py`, stdlib-only) —
   the primary path on **every other surface** (e.g. the VS Code extension). Typed,
   no-shell tools: `necro_status`, `necro_start`, `necro_cmd` (main driver, same
   `{cmd,args,opts}` commands), `necro_screenshot` (frame returned inline as image),
   `necro_restart`, `necro_stop`.
3. **`tools/devctl.py`** — CLI fallback (one allowlisted bash command) when the `necroking`
   server isn't loaded. Same `tools/necro_devlib.py` under the hood.

All three auto-start the supervisor and game from a cold start. **ALWAYS prefer the MCP
tools over raw Bash/curl** — they're allowlisted by name (no prompts) and faster. A new
`.mcp.json` server needs a one-time trust approval + Claude Code reload before its tools
appear.

## Claude_Preview workflow (desktop app only)

JavaScript-based: `preview_start("necroking-dev")` → `preview_eval(id,
"window.dev(cmd,args,opts)")` → `preview_screenshot(id)`. The full JS reference — the
`.claude/launch.json` bootstrap, `window.dev` semantics, the `/game/restart` +
`/game/stop` fetch calls, batch polling, recipes — lives in
[preview-server.md](preview-server.md). Read it before driving through this tier.

## necroking MCP / devctl.py quick reference

`necro_cmd {cmd,args,opts}` forwards the same commands as `window.dev`. Because `/cmd`
forwards `{cmd,args,opts}` verbatim, **no MCP-server change is needed when a new game
command is added** — add it in C# and `necro_restart` with build.

devctl.py (run from repo root; use `py`/`python3` if `python` isn't on PATH):
```bash
python tools/devctl.py status                 # supervisor + game status JSON
python tools/devctl.py up [--windowed] [--map default]
python tools/devctl.py cmd state              # JSON snapshot (necromancer x/y/mana, etc.)
python tools/devctl.py cmd menu new_game
python tools/devctl.py cmd spawn Skeleton 2090 1882
python tools/devctl.py cmd camera 2096 1882 48   # x y zoom
python tools/devctl.py cmd speed 4
python tools/devctl.py cmd help               # list every game dev command + selectors
python tools/devctl.py shot fight no_ui=true downsample_to=full   # prints "SHOT: <abspath>"
python tools/devctl.py raw '{"cmd":"units","args":["all"]}'
python tools/devctl.py restart --build        # stop -> rebuild -> start (after a C# change)
python tools/devctl.py down                   # stop game (leave supervisor up)
python tools/devctl.py kill-server            # stop game AND supervisor
```
`cmd <gamecmd> [args...] [key=value...]`: bare tokens → positional args, `key=value` → opts.

## Game commands (`ExecuteDevCommand` in Game1.cs)

- `ping` · `state` — liveness / JSON snapshot of game state
- `help` (alias `commands`) — list every dev command + selector syntax. Run this to
  discover what's available without reading the source.
- `start_game [map]` — load a map into gameplay, use empty_test_map for most tests
- `spawn <type> <x> <y>` · `camera <x> <y> [zoom]` · `speed <n>` · `pause` · `resume`
- `screenshot [name]` with `opts`: `no_ui`, `no_ground`, `downsample_to` (`"WxH"` | `"full"`;
  default 640x360)
- `menu <new_game|test_map|scenarios|main_menu|quit>` — press a main-menu button
- `window <show|hide|toggle>` — **flip the running game between headless and a visible,
  interactive window WITHOUT restarting.** The headless game always runs and renders; it's
  parked off-screen with no taskbar button. `show` moves it on-screen (bordered + focused)
  so the user can play; `hide` parks it again. **When the user asks to "see"/"access"/
  "interact with" the game, use `window show` — do NOT stop+restart.** A windowed *restart*
  is only needed when the game is fully stopped (`game_running:false`); if it's already
  running, just `window show`.

### Units & combat

- **Selectors** — most commands take a `<selector>` resolving to one or more units:
  `all`/`*`, `necro`, a faction (`undead`/`human`/`animal`), a bare index (`9`),
  `id:<n>` (unit Id), or a UnitDef id (`skeleton`) / UnitType name (`Soldier`),
  case-insensitive. Faction/def/type/`all` match ALIVE units only.
- `spawn_def <unitID> <x> <y> [count]` — spawn by **UnitDef id** (full def stats/faction/AI),
  unlike `spawn` which takes a bare `UnitType`. `count` spawns a small line. Def ids are
  lowercase (`skeleton`, `soldier`, `zombiewolf`).
- `units [selector]` — list matched units as JSON (idx, id, type, def, faction, ai, x/y,
  hp/maxHp, mana, alive, inCombat). `unit <selector>` dumps the first match.
- `combat_log [n]` — last N combat-log entries (attacker/defender/outcome/damage).
- `damage <selector> <amount>` · `kill <selector>` · `remove <selector>` (delete).
- `set_ai <selector> <AIBehavior>` · `move <selector> <x> <y>` (AI=MoveToPoint).
- `set_hp <selector> <hp> [maxHp]` · `set_mana <selector|necro> <mana> [maxMana]`.
- `mark <selector|clear>` · `unmark [selector]` — draw a persistent white outline box
  around matching units (independent of mouse hover) so a screenshot can point at one.
  `mark clear` / argless `unmark` removes all marks.
- `cast <spellID> <x> <y>` — necromancer casts via the full player pipeline (may fail on
  mana/cooldown/range — `set_mana necro 9999` first if needed).
- `fireball <x> <y> [dmg] [radius] [name]` — spawn a projectile directly (deterministic, no
  mana/anim gating).

### Batch scripts with waits (`batch` / `job`)

Run a sequence of commands with timed waits so screenshots land at exact moments. The game
steps the script over its own update loop (sim clock, like a scenario's `OnTick`), so
`batch` returns a `jobId` immediately and you **poll `job`** for progress/results (never
blocks past the HTTP timeout). `opts.script` is an array of steps: `{cmd,args,opts}` (any
game command) · `{wait:<simSecs>}` (frozen while paused) · `{wait_real:<secs>}` ·
`{wait_frames:<n>}` · `{shot:"name", ...screenshotOpts}` (sugar for a screenshot step).
`job` returns `{done,step,total,results:[...]}` where `results[i]` is the raw response of
step i (screenshot steps yield their PNG path); `job cancel` aborts.
Via `necro_cmd` (or `devctl.py raw` with the same JSON):
```json
{"cmd":"batch","opts":{"script":[
  {"cmd":"camera","args":[2096,1882,48]}, {"cmd":"speed","args":[4]},
  {"shot":"t0"}, {"wait":2.0}, {"shot":"t2"}, {"wait":2.0}, {"shot":"t4"},
  {"cmd":"units","args":["all"]}
]}}
```
returns `{jobId}`; then poll `{"cmd":"job","args":[<jobId>]}` (`python tools/devctl.py
cmd job <jobId>`) every ~0.3 s until `done:true`. `results` holds each step's reply;
PNGs at `bin/Devbuild/log/screenshots/<name>.png`. (JS polling form:
[preview-server.md](preview-server.md).)

### UI panels & overlays

- `panels` — list every previewable panel, its tabs, the overlays, the current state. Run
  this first to discover valid names.
- `panel <name> [tab]` — switch to a UI panel; auto-starts a default game for panels that
  need a world. Names: `main_menu`, `scenarios`, `game`, `pause`, `settings`, `unit_editor`,
  `spell_editor`, `map_editor`, `ui_editor`, `item_editor`. Optional 2nd arg sets a tab.
- `tab <name>` — set the active tab on the open panel (Settings: Bloom/Shadow/…; Map editor:
  Ground/Grass/Objects/Walls/…; UI editor: NineSlices/Elements/Widgets).
- `overlay <name> [open|close|toggle]` — in-game overlays: `inventory`, `character_stats`,
  `skill_book`, `grimoire`, `character_sheet`.
- `select <name|id|index>` — select an entry in the open editor so its preview/detail
  renders. Accepts a numeric index, a def id, or a display name. e.g. `panel spell_editor`
  then `select Fireball`.

## Screenshots — two ways

- `preview_screenshot(id)` (or `necro_screenshot`) captures the whole **dashboard page**
  (live frame + command log). Best for a quick glance / watching progress.
- To **analyze just the game frame**, run the `screenshot` game command —
  `necro_cmd {"cmd":"screenshot","args":["name"],"opts":{...}}` or
  `python tools/devctl.py shot name no_ui=true` — it returns the path and the PNG lands at
  `bin/Devbuild/log/screenshots/<name>.png` (the preview builds into its own bin/Devbuild
  folder; the reply/`necro_status` are the source of truth for the exact path — Read that,
  don't reconstruct it). Then **`Read` that file** (Read is approved → no prompt). Clean
  frame at the downsample size, no dashboard chrome — the right way to actually inspect a
  result.
- `opts`: `{no_ui:true}` hides the HUD; `{no_ground:true}` drops ground+grass (scenario
  black look — good for verification, terrain is noise for the model); `{downsample_to:"WxH"}`
  or `"full"` picks the returned size (default 640x360 = half the 1280x720 render).

## Adding a new command — do this freely; it's the point

If a check needs a verb the server doesn't have, ADD IT. One `case` in `ExecuteDevCommand`
(`Game1.cs`) + a rebuild; the `/cmd` channel forwards `{cmd,args,opts}` verbatim, so **no
`tools/devserver.py` change is needed** (don't edit it).

```csharp
// in ExecuteDevCommand(Necroking.Dev.DevCommand c), Game1.cs:
case "kill_faction":                       // devctl: cmd kill_faction Human
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
- Runs on the **game main thread** (drained in `Update`), so touching `_sim`, `_camera`,
  `_gameData`, UI panels, etc. directly is safe — the HTTP thread only queues.
- Args: `c.Args[i]` (positional strings); `DevFloat(s)` parses a float; `c.Opt("k")` /
  `c.OptBool("k")` read named opts.
- Reply: `c.Complete(DevServer.Ok("msg"))`, `DevServer.Error("msg")`, or
  `DevServer.OkRaw("<json>")` for a structured object (see how `state` builds its JSON).
- **Deferred results** (need a rendered frame, like a screenshot): stash a pending field and
  call `c.Complete(...)` later from `Draw` instead of blocking — follow the
  `_pendingDevScreenshot` path.
- After adding: rebuild via `necro_restart` (or `python tools/devctl.py restart --build`),
  then call it with `necro_cmd {"cmd":"your_verb","args":[...],"opts":{...}}` or
  `python tools/devctl.py cmd your_verb ...`.

## Gotchas

- **Stop the game via the server, NEVER taskkill.** When the exe is locked for a build, or
  you're done driving it, stop the game through the server: `necro_stop` /
  `python tools/devctl.py down` (preview JS form: [preview-server.md](preview-server.md)).
  The supervisor owns the process (Windows Job Object) and the headless game is hidden from
  the taskbar, so a force-killed PID orphans bookkeeping and a forgotten game idles
  invisibly. `necro_restart` / `restart --build` already stops it for you. The supervisor
  itself can stay up (cheap; holds the pinned frame).
- **Spawn faction is implied by `UnitType`**: Skeleton/Abomination → Undead (friendly to the
  necromancer); the rest (Soldier/Knight/Militia/Archer) → Human (will attack and can kill
  the necromancer). Spawn humans at a distance, or spawn undead for a friendly scene. Types:
  Necromancer, Skeleton, Abomination, Militia, Soldier, Knight, Archer, Dynamic.
- **World coordinates (Vec2).** Read `state` for the necromancer's `x,y` and anchor
  spawns/camera off it (default map necromancer ≈ 2096,1882).
- A screenshot is captured one frame later in `Draw`; the command reply only returns once
  the PNG is written, so the returned path is ready to `Read` immediately.

## Recipes

Set up a fight, speed it up, analyze it — read `state` first for the necromancer's `x,y`
(e.g. 2096,1882), then everything else is **one `batch`** (via `necro_cmd`, or
`devctl.py raw '<json>'`):
```bash
python tools/devctl.py cmd state     # → necromancer x,y
```
```json
{"cmd":"batch","opts":{"script":[
  {"cmd":"menu","args":["new_game"]},
  {"cmd":"spawn","args":["Skeleton",2093,1882]},
  {"cmd":"spawn","args":["Soldier",2099,1882]},
  {"cmd":"camera","args":[2096,1882,48]},
  {"cmd":"speed","args":[4]},
  {"wait":2.0},
  {"shot":"fight"}
]}}
```
Poll `{"cmd":"job","args":[<jobId>]}` until `done:true`, then `Read`
`bin/Devbuild/log/screenshots/fight.png`.

Inspect an editor entry — one batch:
`{"cmd":"batch","opts":{"script":[{"cmd":"panel","args":["spell_editor"]},{"cmd":"select","args":["Fireball"]},{"shot":"spell_editor"}]}}`.

(JS versions of these recipes for the Claude_Preview tier:
[preview-server.md](preview-server.md).)
