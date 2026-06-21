# Driving the live game preview

There are three ways to drive the running game, in priority order:

1. **`Claude_Preview` MCP tools** (`preview_start`, `preview_eval`, `preview_screenshot`) —
   connected **only in the desktop Claude Code app**. Use these there (see the "Dev
   Control Server" section of CLAUDE.md).
2. **The `necroking` project MCP server** (`.mcp.json` → `tools/necro_mcp.py`) — the
   primary path on **every other surface** (the VS Code extension). Typed, no-shell tools.
   *See "MCP tools" below.*
3. **`tools/devctl.py`** — a CLI fallback (one allowlisted bash command) for when the
   `necroking` server isn't loaded. *See "CLI fallback" below.*

All three talk to the **same** persistent supervisor and game:

    Claude --(MCP tool | python)--> supervisor (:8777) --proxy--> game (:8778)

The supervisor (`tools/devserver.py`) and the supervisor/HTTP logic shared by the MCP
server and the CLI (`tools/necro_devlib.py`) auto-start the supervisor (detached) and the
game, so any of these works from a cold start. **Decision rule:** prefer the highest
available tier (1 → 2 → 3).

---

## MCP tools (`necroking` server) — primary outside the app

Registered in the repo's tracked `.mcp.json`. **Dependency-free** (stdlib-only Python),
so it runs anywhere Python does — no pip installs. Tools:

| tool | what it does |
|---|---|
| `necro_status` | supervisor + game status (does not start the game) |
| `necro_start` | ensure the game is running (`windowed`, `map` optional) |
| `necro_cmd` | run any game dev command: `{cmd, args[], opts{}}` — the main driver |
| `necro_screenshot` | capture the frame, **returned inline as an image** (`no_ui`, `no_ground`, `full`, `size`) |
| `necro_restart` | stop → optional `build` → start (after a C# change) |
| `necro_stop` | stop the game (leave the supervisor up) |

`necro_cmd` forwards the same commands as the dashboard (`ExecuteDevCommand` in Game1.cs),
e.g. `cmd='state'`; `cmd='menu' args=['new_game']`; `cmd='spawn' args=['Skeleton','2090','1882']`;
`cmd='camera' args=['2096','1882','48']`; `cmd='help'` to list everything. Because `/cmd`
forwards `{cmd,args,opts}` verbatim, **no MCP-server change is needed when a new game
command is added** — add it in C# and `necro_restart` with build.

**One-time setup note:** a new `.mcp.json` server needs a trust approval and a Claude Code
reload before its tools appear. After that the tools are allowlisted (`mcp__necroking__*`
in `.claude/settings.json`) and never prompt.

**Lifecycle (no orphans):** the MCP server *owns* the supervisor it starts, via a Windows
Job Object (`KILL_ON_JOB_CLOSE`) — just like the desktop app owns its supervisor. When the
editor/Claude Code session closes, the MCP process dies, the OS kills the supervisor, and
the supervisor's own job kills the game. So closing the editor leaves nothing running.
(The `devctl.py` CLI, by contrast, intentionally *detaches* the supervisor so it persists
between separate command invocations; stop it with `python tools/devctl.py kill-server`.)

> Why an MCP server instead of a skill or raw bash: a skill is just instructions (and
> `.claude/` is machine-local, so skills don't propagate reliably); raw bash gets composed
> into `&& curl … | parse …` pipelines that can't be safely whitelisted. A typed MCP tool
> is the same model the app's `Claude_Preview` uses — structured input, no shell, one
> name-based allow.

---

## CLI fallback (`tools/devctl.py`)

Use only if the `necroking` MCP server isn't available. One allowlisted bash command;
it uses the same `tools/necro_devlib.py` under the hood.

## Commands

All of these auto-start the supervisor and the game as needed. Run from the repo root.
If `python` isn't on PATH, use `py` or `python3` (the wrapper spawns the supervisor with
its own interpreter, so only the launching word changes).

```bash
python tools/devctl.py status                 # supervisor + game status JSON
python tools/devctl.py up                      # ensure game running (add --windowed / --map default)
python tools/devctl.py cmd state               # JSON snapshot (necromancer x/y/mana, etc.)
python tools/devctl.py cmd menu new_game       # press a main-menu button to load gameplay
python tools/devctl.py cmd spawn Skeleton 2090 1882
python tools/devctl.py cmd camera 2096 1882 48 # x y zoom
python tools/devctl.py cmd speed 4
python tools/devctl.py cmd help                # list every game dev command + selector syntax
python tools/devctl.py shot fight no_ui=true downsample_to=full   # screenshot -> prints "SHOT: <abspath>"
python tools/devctl.py raw '{"cmd":"units","args":["all"]}'        # arbitrary {cmd,args,opts}
python tools/devctl.py restart --build         # stop -> rebuild -> start (after a C# change)
python tools/devctl.py down                    # stop the game (leave supervisor up)
python tools/devctl.py kill-server             # stop game AND supervisor
```

`cmd <gamecmd> [args...] [key=value...]`: bare tokens become positional args, `key=value`
tokens become opts — same convention as the dashboard input box. The set of game commands
lives in `ExecuteDevCommand` (Game1.cs); `devctl` forwards them verbatim, so it never needs
changing when a new game command is added. Run `python tools/devctl.py cmd help` to discover them.

## Screenshots — capture then read

`shot` writes the PNG to `bin/Debug/log/screenshots/<name>.png` and prints `SHOT: <abspath>`.
**Read that path with the Read tool** to see/analyze the frame. Useful opts:
`no_ui=true` (hide HUD), `no_ground=true` (scenario black look), `downsample_to=full`
(full 1280x720; default is the game's downsample).

## Typical loop — set up a fight and look at it

```bash
python tools/devctl.py cmd menu new_game
python tools/devctl.py cmd state            # read necromancer x,y from the JSON
python tools/devctl.py cmd spawn Skeleton <x-6> <y>
python tools/devctl.py cmd spawn Soldier  <x+6> <y>   # Soldier is Human -> will fight
python tools/devctl.py cmd camera <x> <y> 48
python tools/devctl.py cmd speed 4
python tools/devctl.py shot fight
# then: Read bin/Debug/log/screenshots/fight.png
```

## After a C# change

```bash
python tools/devctl.py restart --build      # build errors come back in the JSON ("build")
```

## Etiquette / gotchas

- **Stop the game when done** (`down`) — headless runs are hidden from the taskbar, so a
  forgotten one idles invisibly. Leaving the *supervisor* up is fine (cheap; holds the
  pinned frame). Never `taskkill` — the supervisor's Job Object cleans up the game itself.
- **Spawn faction is implied by UnitType**: Skeleton/Abomination -> Undead (friendly to the
  necromancer); Soldier/Knight/Militia/Archer -> Human (will attack). Spawn humans at a
  distance or they kill the necromancer.
- **World coordinates (Vec2)** — read `cmd state` for the necromancer's x,y and anchor
  spawns/camera off it (default map necromancer ~ 2096,1882).
- **First run may prompt for permission** on a machine whose `.claude/settings.local.json`
  doesn't allowlist `python tools/devctl.py` (e.g. the friend's clone — `.claude` isn't
  synced). Approve it once; it won't ask again.
- Do **not** edit `tools/devserver.py` (its own header forbids it) — add new *game* control
  in C# (`ExecuteDevCommand` in Game1.cs) and `restart --build`; `devctl` forwards it for free.
```
