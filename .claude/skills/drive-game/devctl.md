# devctl.py — CLI fallback reference

`tools/devctl.py` is the tier-3 CLI fallback behind the [drive-game SKILL](SKILL.md), for
when the `necroking` MCP server isn't loaded. It shares `tools/necro_devlib.py` with the MCP
tools, so behavior is identical — **prefer the `necroking` MCP tools when they're available**
(allowlisted by name, no prompts, faster). It's one allowlisted bash command and auto-starts
the supervisor + game from a cold start.

Run from the repo root; use `py` or `python3` if `python` isn't on PATH.

## Lifecycle (mirrors the `necro_*` MCP tools)

| devctl.py | `necro_*` MCP tool | does |
|---|---|---|
| `status` | `necro_status` | supervisor + game status JSON |
| `up [--windowed] [--map default]` | `necro_start` | start the game |
| `restart --build` | `necro_restart` | stop → rebuild → start (after a C# change) |
| `down` | `necro_stop` | stop game, leave supervisor up |
| `kill-server` | — | stop game AND supervisor |

## Running game commands (mirrors `necro_cmd`)

Every game command (the full list is in [SKILL.md](SKILL.md)) runs through one of:
```bash
python tools/devctl.py cmd <gamecmd> [args...] [key=value...]      # bare tokens → args, key=value → opts
python tools/devctl.py raw '{"cmd":"units","args":["all"]}'        # full JSON = the necro_cmd payload
python tools/devctl.py shot <name> no_ui=true downsample_to=full   # sugar for the screenshot command; prints "SHOT: <abspath>"
```
`cmd <gamecmd> [args...] [key=value...]`: bare tokens become positional `args`, `key=value`
tokens become `opts`. The JSON passed to `raw` is exactly the `{cmd,args,opts}` payload the
`necro_cmd` MCP tool takes.

Examples:
```bash
python tools/devctl.py status                 # supervisor + game status JSON
python tools/devctl.py up [--windowed] [--map default]
python tools/devctl.py cmd state              # JSON snapshot (necromancer x/y/mana, etc.)
python tools/devctl.py cmd menu new_game
python tools/devctl.py cmd spawn Skeleton 2090 1882
python tools/devctl.py cmd camera 2096 1882 48   # x y zoom
python tools/devctl.py cmd speed 4
python tools/devctl.py cmd help               # list every game dev command + selectors
python tools/devctl.py shot fight no_ui=true downsample_to=full
python tools/devctl.py raw '{"cmd":"units","args":["all"]}'
python tools/devctl.py restart --build        # stop -> rebuild -> start (after a C# change)
python tools/devctl.py down                   # stop game (leave supervisor up)
python tools/devctl.py kill-server            # stop game AND supervisor
```

## Batch jobs via the CLI

A `batch` script (see [SKILL.md](SKILL.md)) is just a `{cmd,args,opts}` payload, so run it
with `raw` and poll the returned `jobId` with `cmd job`:
```bash
python tools/devctl.py raw '{"cmd":"batch","opts":{"script":[
  {"cmd":"camera","args":[2096,1882,48]}, {"cmd":"speed","args":[4]},
  {"shot":"t0"}, {"wait":2.0}, {"shot":"t2"}
]}}'                                          # → {jobId}
python tools/devctl.py cmd job <jobId>        # poll every ~0.3s until done:true
```
Screenshots land at `bin/Devbuild/log/screenshots/<name>.png`.
