# Dev control server — `Necroking/Dev/` + `Game1.Dev*.cs`

The in-process HTTP control channel that drives the running game for the dev/preview
workflow (`window.dev('cmd', [...], {...})` → POST → executed on the game main thread).
Only active when the game is launched with `--devserver <port>` (the supervisor in
`tools/devserver.py` owns the process; see CLAUDE.md "Dev Control Server" and
`docs/devpreview.md`). **Transport lives in `Dev/`; every command handler lives in
`Game1.Dev.cs`.**

## Files

### `Necroking/Dev/DevServer.cs`
Transport only — knows nothing about game state.
- **`DevCommand`** — one received command. Fields: `Cmd` (string), `Args` (string[]),
  `Opts` (`Dictionary<string,string>`, case-insensitive). Helpers: `OptBool(key)`,
  `Opt(key)`, `Complete(json)` (completes the awaiting HTTP response — call exactly
  once), `Result` (Task the HTTP thread waits on). `FromElement(JsonElement, fallbackCmd)`
  builds a command from a `{cmd,args,opts}` JSON object — shared by the HTTP parser and
  the batch runner so a batch step behaves identically to a stand-alone command. Args
  elements may be strings or numbers; all normalised to strings.
- **`DevServer`** — `HttpListener` on `http://localhost:<port>/` (localhost only).
  `Start()`/`Stop()`, a background `ListenLoop` that enqueues onto a
  `ConcurrentQueue<DevCommand>`, and **`Drain(Action<DevCommand> execute)`** — the
  main-thread pump that runs every queued command through the executor. The executor is
  responsible for completing each command (immediately, or later for deferred ops).
- **Response helpers (use these from handlers):** `DevServer.Ok(string)` →
  `{"ok":true,"result":"..."}`; `DevServer.OkRaw(json)` → wraps an already-formed JSON
  object/value as the result payload; `DevServer.Error(string)` →
  `{"ok":false,"error":"..."}`.
- Parsing also accepts the URL path as the command for empty-body requests (`GET /ping`).
- HTTP response **blocks up to 15 s** for the main thread to complete the command —
  long-running work must be deferred (see screenshots / batch below), not blocked on.

**Look/edit here when…** changing the wire protocol, the request/response JSON shape,
the port/host binding, the queue/drain mechanism, or the 15 s timeout. **Not** for adding
a command verb.

### `Necroking/Dev/DevScript.cs`
Data structures for batch scripts (no logic).
- **`DevScriptStep`** — one step: either a `Cmd` to run, or a wait
  (`WaitSimSecs`/`WaitRealSecs`/`WaitFrames`). `IsWait => Cmd == null`.
- **`DevJob`** — a queued batch: `Steps`, `Cursor`, remaining-wait counters, `InFlight`
  (a started command whose result isn't ready yet, e.g. a screenshot), `Results` (raw
  JSON per completed step), `Done`/`Canceled`. Only one job runs at a time.

**Look/edit here when…** adding a new batch step kind or job-state field. The runner that
advances it lives in `Game1.Dev.cs` (`ParseDevScript`, `UpdateDevScript`, `DevJobStatusJson`).

### `Necroking/Game1.Dev.cs` — THE command dispatch (this is where new verbs go)
`partial class Game1`. **`void ExecuteDevCommand(Necroking.Dev.DevCommand c)`** is one
big `try { switch (c.Cmd) { … } } catch { c.Complete(Error(ex.Message)); }`. Runs on the
**game main thread** (drained in `Update`), so touching `_sim`, `_camera`, `_gameData`,
UI panels, editors etc. directly is safe — the HTTP thread only queues. Each `case`
**must** call `c.Complete(...)` exactly once (or defer — see screenshots).

Existing verbs (grouped): liveness (`ping`, `state`), settings (`setting`/`set_setting`),
cheats/diagnostics (`godmode`, `cooldowns`, `fog`, `corpses`, `locomotion`/`loco`),
spawning (`spawn`, `spawn_def`, `spawn_horde`, `place_obj`, `reanim_at`), units & combat
(`units`, `unit`, `damage`, `kill`, `remove`, `set_ai`, `move`, `walk_necro`, `set_hp`,
`set_mana`, `set_necro_type`, `zombify`, `cast`, `fireball`, `combat_log`, `mark`/`unmark`,
`hover*`), camera/time (`camera`, `free_camera`, `speed`, `pause`, `resume`), flow
(`start_game`, `menu`, `window`), UI/editor preview (`panels`, `panel`, `tab`, `overlay`,
`select`, `ui_job_board`, `ui_grave_roster`, `ed_*`), worker economy (`assign_worker`,
`unassign_worker`, `stock_add`, `jobs`, `worker_demo`, `worker_scene`, `respace_graves`),
batch (`batch`, `job`), data injection (`add_data`/`add_json` → `DevAddData`),
discovery (`help`/`commands`), and `screenshot`. Unknown verb → `default:` returns
`Error($"unknown cmd: ...")`.

**Helpers in this file** (reuse them rather than re-parsing):
- `static float DevFloat(string)` — invariant-culture float parse for args.
- `List<int> DevResolveUnits(string token)` — the selector resolver
  (`all`/`*`/`necro`/faction/index/`id:<n>`/UnitDef id/UnitType). Use for any
  `<selector>` arg; `string.Join(" ", c.Args, …)` to assemble multi-token selectors.
- `string DevUnitJson(int idx)` — per-unit JSON (used by `units`/`unit`).
- `bool SetUiPanel(name)`, `bool ApplyPanelTab(tab)`, `string? SetOverlay(name, action)`,
  `string? SelectEditorEntry(token)` — the panel/overlay/select plumbing.
- `string BuildDevStateJson()` — the `state` snapshot (in `Game1.DevData.cs`).
- `ParseDevScript`, `UpdateDevScript`, `DevJobStatusJson` — batch-job runner;
  `UpdateDevScript(dt, rawDt)` is called from `Update` when `_devJob != null` (Game1.cs).
- Settings path walk: `TryGetSettingByPath` / `TrySetSettingByPath`.

**Look/edit here when…** ADDING A NEW DEV COMMAND VERB. Add one `case "your_verb":` to
the switch, do the work against `_sim`/`_camera`/`_gameData`/UI, and `c.Complete(...)`.
For a deferred result (needs a rendered frame), stash a pending field and complete later
from `Draw` — follow the `_pendingDevScreenshot` / `_pendingDevScreenshotCmd` pattern
(fields declared in `Game1.cs` ~445). Add the verb's one-line signature to the
`help`/`commands` array too. **No change to `tools/devserver.py` is needed** — `/cmd`
forwards `{cmd,args,opts}` verbatim (and you must not edit that supervisor; it forces a
restart — ask the user first).

### `Necroking/Game1.DevData.cs`
`partial class Game1`. **`void DevAddData(DevCommand c)`** — runtime injection of a
registry entry (spell/unit/item/buff/weapon/armor/shield/potion/flipbook) from JSON into
the live game (never saved to disk); selects it in the open editor if matching. Also
houses `BuildDevStateJson()` (the `state` payload). Its own inner `switch` has a
`default:` for unknown data kinds.

**Look/edit here when…** changing the `add_data`/`add_json` verb, supporting a new
registry type for live injection, or changing the `state` JSON snapshot fields.

## Wiring (in `Game1.cs`)
- `LaunchArgs.DevServerPort` (parsed in `Program.cs` from `--devserver <port>`;
  `LaunchArgs.Headless` from `--headless`). The server is created+started only when
  `DevServerPort > 0`, in `LoadContent` (`Game1.cs` ~2006): `_devServer = new DevServer(...)`,
  `_devServer.Start()`, `Exiting += … _devServer?.Stop()`.
- **`Update` (Game1.cs ~2041)** calls `_devServer?.Drain(ExecuteDevCommand)` first, so
  commands run even while the window is unfocused and regardless of menu state.
  `_devJob` is stepped at ~2152 (`UpdateDevScript`).
- Deferred-result fields (`_pendingDevScreenshot*`, `_devJob`, `_devJobSeq`) live near
  `Game1.cs` ~445 and are consumed in `Draw`.

## Pitfalls / gotchas
- **Always `c.Complete(...)` exactly once per command** (every branch, including error
  branches). A handler that returns without completing makes the HTTP request hang until
  the 15 s timeout. Completing twice is harmless (`TrySetResult`) but a smell.
- **Don't block the main thread** in a handler — the HTTP side waits at most 15 s. Long /
  frame-dependent work must be deferred (stash a pending field, complete from `Draw`) or
  expressed as a batch job that polls.
- **`Args` are always strings** (numbers JSON-normalised). Use `DevFloat`/`int.TryParse`;
  don't assume types.
- **Reuse `DevResolveUnits`** for `<selector>` args — don't re-implement selector parsing.
- **Index-shifting:** removing units/objects shifts indices. Iterate backwards or sort
  descending (see `remove`, `respace_graves`).
- **Do NOT edit `tools/devserver.py`** to add a command — it's a transparent forwarder; a
  new verb is pure C# in `ExecuteDevCommand`. Editing the supervisor forces a restart
  (memory: "Don't edit the dev python server"). Rebuild+relaunch via
  `/game/restart {"build":true}` after adding a case.
- After adding a verb, add its signature to the `help`/`commands` list so discovery stays
  accurate.

## Related areas
- [game1-partials.md](game1-partials.md) — `ExecuteDevCommand` is itself a Game1 partial;
  most handlers call into the systems documented there (spell cast, reanim, spawn).
- [jobs-workers.md](jobs-workers.md) — the worker-economy dev verbs drive `WorkerSystem`,
  `JobBoardUI`, `GraveRosterUI`.
- `Scenario/` (not yet documented) — the coded `--scenario` harness uses the same
  primitives; the dev server is the live/interactive equivalent.
- CLAUDE.md "Dev Control Server" + `docs/devpreview.md` — the workflow, MCP tools, and
  the supervisor topology.
