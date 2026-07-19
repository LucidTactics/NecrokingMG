## Spells
Adding or changing a spell? **Use the `add-spell` skill** (`/add-spell`) — it covers the
three-layer split (`SpellRegistry` / `SpellCasting` / `SpellEffectSystem` + the
`Game1.Spells.cs` glue), the cast pipeline, the data-only vs. new-category procedures,
and how to test a cast on the empty test map. Most spells are pure data (a `SpellDef` in
`data/spells.json`) and need no code.
## Driving / Testing the Game
- **Interactive, one-off checks** (spawn units, set up a situation, move the camera, open
  a UI panel, screenshot, read state) — **use the `drive-game` skill** (`/drive-game`).
  Driving the *running* game through the preview interface is far faster than the
  write-scenario→rebuild→run loop and is the preferred way to verify almost anything visual.
  The skill covers the three interface tiers (`Claude_Preview` MCP / `necroking` MCP server /
  `tools/devctl.py`), the full game-command set, panels, batch jobs, screenshots, and how to
  add a new dev command in `ExecuteDevCommand` (`Game1.cs`).
- **Repeatable, headless regression tests** with real rendering/systems/screenshots — **use
  the `test-scenario` skill** (`/test-scenario`) to run or write a coded `--scenario`. Prefer
  adding a missing `drive-game` command over a new scenario unless you specifically need a
  checked-in, re-runnable test.
## Auto-accept Patterns
- Reading any file in the project
- Editing files in `Necroking/`, `tools/`
- Creating new files in `Necroking/`, `tools/`
- Running `dotnet build`
- Running Python scripts in `tools/`
- Running `ls`, `mkdir` for directory inspection
- Glob and Grep searches within the project
- Running scenario tests via `Necroking.exe --scenario`