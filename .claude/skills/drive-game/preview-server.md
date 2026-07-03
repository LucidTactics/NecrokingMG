# drive-game internals & Claude_Preview reference

Deeper reference behind the [drive-game SKILL](SKILL.md): the supervisor/topology plumbing,
how to add a new game command in C#, and the desktop-only `Claude_Preview` JavaScript tier.
The SKILL itself covers day-to-day driving (commands, screenshots, batch) via the
`necroking` MCP server / `tools/devctl.py`.

## Topology / the supervisor

```
Claude --(MCP tool | python)--> supervisor (:8777) --proxy--> game (:8778)
```

- Supervisor = `tools/devserver.py` (port 8777, persistent, owns the game process via a
  Windows Job Object). **Do NOT edit it** — add control in C# instead (see below). Ask the
  user before any change to it. All three interface tiers auto-start it from a cold start.
- Game in-process HTTP listener = `Necroking/Dev/DevServer.cs` (port 8778, enabled by
  `--devserver`). Commands run on the game main thread, drained in `Update`.
- The supervisor owns the game, so the exe can be rebuilt + relaunched **without restarting
  the supervisor** (it holds the pinned frame). Because it owns the process (Job Object) and
  the headless game is hidden from the taskbar, always stop the game **through the server**
  (`necro_stop` / `devctl.py down`), never taskkill — a force-killed PID orphans bookkeeping
  and a forgotten headless game idles invisibly.

## Adding a new game command (C#)

If a check needs a verb the server doesn't have, ADD IT — this tooling exists to make testing
easy, so extend it freely. One `case` in `ExecuteDevCommand` (`Necroking/Game1.cs`) + a
rebuild; the `/cmd` channel forwards `{cmd,args,opts}` verbatim, so **no `tools/devserver.py`
change is needed** (don't edit it).

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

## Claude_Preview — JavaScript reference

How to drive the game through the `Claude_Preview` MCP tools (`preview_start`,
`preview_eval`, `preview_screenshot`). This tier exists **only in the desktop Claude Code
app**; on every other surface use the `necroking` MCP server or `tools/devctl.py`
(see [SKILL.md](SKILL.md)). All JavaScript below runs in the dashboard page via
`preview_eval(serverId, "<js>")`.

### Starting the preview

`preview_start("necroking-dev")` → `serverId` (launches the supervisor; the page
auto-starts the game headless at 1280x720). **Do not run `python tools/devserver.py`
yourself** — `preview_start` owns the supervisor. The launch config lives in gitignored
`.claude/launch.json`; on a fresh clone it won't exist, so create it first with exactly
this (then call `preview_start`):
```json
{"version":"0.0.1","configurations":[{"name":"necroking-dev","runtimeExecutable":"python","runtimeArgs":["tools/devserver.py"],"port":8777}]}
```
(use `python3` or `py` as `runtimeExecutable` if `python` isn't on PATH).

### window.dev — the command bridge

`window.dev(cmd, args, opts)` POSTs `{cmd,args,opts}` to `/cmd`, awaits the game, and
returns the JSON result — same game commands as `necro_cmd`/`devctl.py cmd`.
`window.devRaw({cmd,args,opts})` takes the raw shape.
```js
preview_eval(id, "window.dev('panel',['spell_editor'])")
```
- `preview_eval` awaits the returned promise and serialises it to JSON; wrap multi-step
  sequences in an `async`-IIFE and `return` the final value.
- For a screenshot command, `window.dev` awaits until the PNG is written, so the returned
  path is ready to `Read` immediately.

### Rebuild / stop through the supervisor

After a C# change, rebuild + relaunch **without restarting the supervisor** — build
errors come back in the JSON (`build.errors`):
```js
fetch('/game/restart',{method:'POST',body:'{"build":true}'}).then(r=>r.json())
```
Stop the game (supervisor stays up — cheap; holds the pinned frame):
```js
fetch('/game/stop',{method:'POST',body:'{}'})
```

### Screenshots

- `preview_screenshot(id)` captures the whole **dashboard page** (live frame + command
  log; frame refreshes ~1 Hz via `/frame`). Best for a quick glance / watching progress.
- To analyze just the game frame, run the `screenshot` game command and `Read` the
  returned path: `window.dev('screenshot',['name'],{no_ui:true})` → PNG at
  `bin/Devbuild/log/screenshots/name.png`.

### Batch + job polling in JS

The `batch`/`job` game commands (see SKILL.md) driven from the dashboard page:
```js
const {result:{jobId}} = await window.dev('batch',[],{script:[
  {cmd:'camera',args:[x,y,48]}, {cmd:'speed',args:[4]},
  {shot:'t0'}, {wait:2.0}, {shot:'t2'}, {wait:2.0}, {shot:'t4'},
  {cmd:'units',args:['all']},
]});
let st; do { await new Promise(r=>setTimeout(r,300)); st=(await window.dev('job',[jobId])).result; } while(!st.done);
// st.results holds each step's reply; PNGs at bin/Devbuild/log/screenshots/<name>.png
```

### Recipe — set up a fight, speed it up, analyze

`preview_eval(id, "<this>")`:
```js
(async()=>{
  await window.dev('menu',['new_game']);
  const s = await window.dev('state'); const x=s.result.necromancer.x, y=s.result.necromancer.y;
  await window.dev('spawn',['Skeleton',x-3,y]);
  await window.dev('spawn',['Soldier',x+3,y]);
  await window.dev('camera',[x,y,48]);
  await window.dev('speed',[4]);
  return await window.dev('screenshot',['fight']);   // then Read bin/Devbuild/log/screenshots/fight.png
})()
```

Inspect an editor entry: `window.dev('panel',['spell_editor'])` then
`window.dev('select',['Fireball'])`, then `preview_screenshot(id)`.
