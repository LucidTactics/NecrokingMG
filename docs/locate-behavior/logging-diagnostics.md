# Logging & diagnostics — combat log, log files, perf/startup timings, census

Where in-game message logs, file logging, and performance/startup diagnostics live.
There is **no in-game log panel yet** — the combat log renders as fading HUD lines, and
everything else goes to `log/*.log` files only.

## `Necroking/Core/DebugLog.cs` — the ONE file-logging primitive
What lives here: `static class DebugLog` — `Log(tag, message)` appends to `log/<tag>.log`
(relative to the exe CWD, set in `Program.Main`), `Clear(tag)` truncates. That's the whole
API; every log file in the game goes through it. **There is no in-memory store** — anything
that wants to show recent log lines in-game needs a ring buffer added here (the single
chokepoint) or in the callers.
Known tags (grep `DebugLog.Log("` for the census): `error` (written by `Core/AtomicFile.cs`,
`Core/JsonFile.cs`, `Core/JsonClone.cs`, `Editor/ReflectionPropertyRenderer.cs` — the
"errors" channel), `combat` (CombatLog below + `BuffSystem`/`Simulation`), `startup`,
`perf`, `ai`, `ai_transition`, `horde_aggro`, `wolf_retarget`, `jump`, `editor`,
`scenario`, `asset`, `table`, `skillbook`.
**Unhandled exceptions** don't go through DebugLog: `Program.Main` (`Necroking/Program.cs`,
catch block ~line 207) writes `log/crash.log` and the process dies.
Look/edit here when: capturing errors/log lines for an in-game panel (add the ring buffer
here), changing where log files go, adding a tag.

## `Necroking/Game/CombatLog.cs` — the combat message list (data)
What lives here: `CombatLogEntry` (Timestamp, attacker/defender names+factions,
`CombatLogOutcome` Hit/Miss/Blocked/Whiff/NoteOnly, weapon, full roll breakdown
Attack/Defense/Damage/Prot DRN pairs, `NetDamage`, hit location, free-text `Note`) and
`CombatLog` — a capped in-memory list (`MaxEntries = 200`, oldest dropped) exposed as
`IReadOnlyList<CombatLogEntry> Entries`; `AddEntry` also mirrors a formatted multi-line
breakdown to `log/combat.log` via `DebugLog` (`WriteEntryToFile`).
Owner: `Simulation._combatLog`, public **`Simulation.CombatLog`** (`Game/Simulation.cs`);
cleared in `Simulation.Init`. Writers: `Simulation` attack resolution + whiffs +
NoteOnly events, `Game/BuffSystem.cs`.
On-screen renderer: **`UI/HUDRenderer.cs` `DrawCombatLog(screenW, screenH, sim, gameData)`**
— bottom-left fading lines anchored at `screenH - 40`, gated on
`Settings.General.CombatLogEnabled` / `CombatLogLines` / `CombatLogFadeTime`
(`Data/Registries/GameSettings.cs`, toggles in `Editor/SettingsGeneralTab.cs`). It formats
one summary string per entry from the structured fields.
Look/edit here when: adding a combat-log panel/history view (read `sim.CombatLog.Entries`),
changing what a combat event records, or the HUD fading lines.

## Frame timings (live fields, not just logs)
- **Sim tick**: `Simulation.LastTickMs` + per-phase `Simulation.LastPhaseMs`
  (Dictionary phase→ms: ai/movement/physics/combat/pathfinder/…) — measured every tick in
  `Simulation.Tick`. The **perf-spike logger** right after it dumps a one-line phase +
  pathfinder-diagnostics breakdown to `log/perf.log` whenever a tick ≥ 3ms
  (`Game/Simulation.cs` ~line 900-941; pathfinder counters = `World/Pathfinder.cs`
  `Diag*` statics).
- **Frame/draw**: `Game1._rawDt` (FPS = 1/rawDt), `_drawMsAvg`, `_groundMsAvg`,
  `_gpuPresentMsAvg` — rendered as the bottom-left perf readout in
  `GameRenderer.Draw.cs` (~line 301, gated `_g._showPerfReadout`), which is the reference
  for how to compose an on-screen timing line. The `perf` dev command
  (`Game1.Dev.cs` ~line 64-95) aggregates frames/fps/sim/draw/present/gc as JSON.
- **Render batch counts**: `_worldPass.LastItemCount/LastBatchCount`, `_fxPass.*`
  (same readout line).

## Startup timings
- `Program.ProcessStartStopwatch` / `Program.ProcessStartTime` (`Necroking/Program.cs`) —
  process-spawn→Main timing.
- `Game1._startupTimer` + **`Game1.LogTiming(step)`** (`Necroking/Game1.cs` ~line 485) —
  per-step `[Xms +Yms] step` lines into `log/startup.log` during `LoadContent` (tag
  `startup`, cleared each launch ~line 1195). File-only; no in-memory list — a stats panel
  would parse `log/startup.log` or capture in `LogTiming`/DebugLog.

## Live world counts — `GameSession.Census()`
`Necroking/Game/GameSession.cs` `Census()` returns a multi-line string report of every
per-game collection: **units by def/faction, env objects by def**, corpses, projectiles,
wall defs, … — the accumulation canaries. Surfaced by the `census` dev command; `mem`
(`Game1.Dev.cs`) adds managed-heap + process memory. This is the ready-made source for a
"game stats" panel (string report today; return structured data if a UI needs columns).

## Cross-links
- [game1-partials.md](game1-partials.md) — GameClock dt domains (RawDt for perf readouts),
  Program.cs launch args, GameSession.
- [ui.md](ui.md) — HUDRenderer, panel patterns for showing any of this in-game.
- [combat.md](combat.md) — the attack resolution that writes CombatLog entries.
