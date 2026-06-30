# Necroking (MonoGame C#) - Claude Code Instructions

## North Star: Satisfaction
The project's design philosophy ("does this satisfy? — anticipation that gets
rewarded") lives in [docs/north-star.md](docs/north-star.md). Read it when designing
or refining a player-facing system (combat, spells, reanimation, crafting, automation).

## Git Policy
- **Commits**: OK to commit freely when a feature is known to be working. Good initiative.
- **Push**: Always ask the user for permission before pushing. Never push without explicit approval.

### Collaborator / Drive-sync — reflexes (full detail: [docs/git-discipline.md](docs/git-discipline.md))
This repo is shared with a **Drive-syncing collaborator**, so unpushed or half-committed work is the
core failure mode (a remote that won't compile because a new file wasn't committed). **Read
[docs/git-discipline.md](docs/git-discipline.md) before any push, multi-file commit, or sync** — it
covers the *why*, the build-before-push rule, and the `user settings/` migration. Keep these reflexes
without opening it:
- **Build before pushing** — never push code that doesn't `dotnet build`.
- **`git status` before committing; stage related changes together** (new `.cs` files included) so the remote compiles. Don't leave a feature half-committed.
- **After a working commit, offer to push** — *"committed and builds — push to origin now?"*
- **If the branch is ahead of origin, surface it** and prompt to push (still ask permission) so the friend's clone stays in sync.
- **Pull before fresh work**; if it won't fast-forward, stop and tell the user.
- **Never commit `user settings/`** (gitignored per-machine `settings.json`/`weather.json`/`spellbar.json`).

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

### Editing data JSON (id-keyed registries)
Most `data/*.json` files (`spells.json`, `units.json`, `items.json`, `potions.json`,
`buffs.json`, `weapons.json`, `armor.json`, …) are a single list of structs each with
a unique `id`. **Don't hand-edit these large files for routine changes** — use the
**`edit-game-data` skill** (`tools/json_data.py`: read / duplicate / delete / create /
update a struct by id, preserving the game's exact formatting). Invoke `/edit-game-data`
or just run the tool. Bulk/derived changes across many entries → a one-off `tools/` script instead.

## Map Content Lives In The Map, Not In Code
**If the user asks for something to be placed in the world — a building, foragable, prop, decoration, unit — add it to the map JSON, not to a code path that spawns it at startup.** Hardcoded startup spawns step on the player's map edits: they save the map without the object, restart, and the code re-spawns it. The save *worked*; the load just stomps it.

- **Adding world content** → edit `data/maps/default.json` directly (or via the in-game map editor and save). Use `tools/` scripts when the JSON is too large to edit interactively.
- **Don't** write `_envSystem.AddObject(...)` or `SpawnUnit(...)` calls in `LoadContent` / `LoadGame` / startup paths unless the user explicitly asks for "always re-spawn this on game start regardless of the map." If unsure, ask.
- **Exception — true fallbacks:** the necromancer fallback at the start of `LoadGame` ("if no necromancer in placed units, spawn Wretched at map center") is fine because it only fires when the map provides nothing. Distinguish "fill in what's missing" from "always add on top of what's there."
- **Past offenders (now removed):** `SpawnStarterMushroom` and `SpawnStarterBlightAltar` in `Game1.cs` unconditionally inserted a Deathcap and a Blight Altar near the necromancer every launch, making them un-deletable via the map editor. Both removed 2026-05-13.

## Spells
Adding or changing a spell? **Use the `add-spell` skill** (`/add-spell`) — it covers the
three-layer split (`SpellRegistry` / `SpellCasting` / `SpellEffectSystem` + the
`Game1.Spells.cs` glue), the cast pipeline, the data-only vs. new-category procedures,
and how to test a cast on the empty test map. Most spells are pure data (a `SpellDef` in
`data/spells.json`) and need no code.

## UI Text Rendering
- SpriteBatch uses `SamplerState.PointClamp` — text drawn at sub-pixel positions gets aliasing artifacts
- **Always round text positions to integer pixels**: `new Vector2((int)x, (int)y)`
- `EditorBase.DrawText` already rounds internally, but any direct `DrawString` calls (e.g. in `Game1.cs`) must round manually
- When centering text with `MeasureString`, the division produces floats — cast to `int` before passing to draw

## Importing UI Designs (CSS / HTML / JSX mocks)
Reproducing an HTML/CSS/JSX design (Claude Design or otherwise) in MonoGame? **Use the
`import-ui-design` skill** (`/import-ui-design`) — it covers what translates cleanly vs.
what needs a real shader, why `Necroking/Render/UIGfx.cs` is a flagged failed-attempt
reference (not a reusable utility), and the read→flat-fill→ask→shader workflow.

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
1. **Locate first — use the `locate-behavior` skill, don't freestyle grep.** Any time you
   need to find *where* a behavior lives or *where new code should go* (which file/function
   is responsible, where to add a feature, what file to create), invoke `/locate-behavior`
   **before** reaching for Grep/Glob. It routes through a self-extending architecture map
   and you leave the map better than you found it — ad-hoc grepping skips that and is the
   thing to avoid. Grep/Glob/LSP are for *verifying* a symbol the skill pointed you at, not
   for the initial "where is this" search.
2. **Search for reuse** — once located, check whether an existing system, utility, or
   pattern already solves the problem (the skill's docs often say so directly)
3. **Reuse or extend** — prefer calling existing code (with different parameters or small improvements) over writing a parallel solution
4. **If nothing exists** — build it in a way that could become the standard approach for that category of problem

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

## Bash
A `PreToolUse` / `Bash` hook (`tools/hooks/bash_prompt_guard.py`) governs Bash, so the old
"avoid `&&`-chained commands, they force confirmations" worry is gone: the hook
**force-allows** a compound command when *every* segment is individually allow-listed (it
splits on `&&`/`||`/`;`/`|`/newlines), so `cd x && git status && dotnet build` passes
silently. Three things to know:

- **Read-only commands just run.** A command that can't change state — no file-writing
  utility, no `>`/`>>` redirection, no process/power-control command, no `$()`/heredoc — is
  auto-allowed even if it isn't on the allow-list. The "can it write/mutate" catalogue lives
  in [`tools/hooks/file_write_detect.py`](tools/hooks/file_write_detect.py) (≈365 file-writing
  command names + process/power control); it's the inverse of the allow-list, so a *miss*
  there = a command wrongly waved through — **err toward adding** when you touch it. Plain
  `grep`/`cat`/`head`/`tail`/`sort`/`wc` now pass straight through; `find` is read-only until
  a mutating flag (`-delete`/`-exec`/…) makes it **prompt**.
- **Deny-by-default for the rest (aggressive by design).** Any Bash command that *can* mutate
  and isn't allow-listed is bounced back; allow-listed commands pass silently. Sensitive forms
  of allow-listed commands still prompt (`git push`, `find … -delete`). When the gate gets in
  the way, the fix is an `allow` rule, a `rule_intended_prompt` branch, or a catalogue entry —
  don't be shy. Full detail: [docs/avoid-prompting-user.md](docs/avoid-prompting-user.md).
- **Still prefer the dedicated tools for search.** `Grep`/`Glob`/`Read` return clickable links
  and are faster than `grep`/`find`/`cat` via Bash — use them even though the shell forms now
  pass the hook.

## Todos Directory (`todos/`)
Temporary research notes and task summaries for future sessions. Each file covers one topic with context, what's done, what's left, and how to debug. Check this directory at the start of relevant work — complete items get deleted. Not for permanent knowledge (use memory for that) or code TODOs (use comments).

## Docs Directory (`docs/`)
On-demand **reference** material: stable, still-useful info that doesn't need to be
in CLAUDE.md (and thus in context) every session — deep how-to guides and subsystem
references. Unlike `todos/`, these don't get deleted when "done"; they're looked up when
relevant. When something in CLAUDE.md is good to keep but no longer a primary driver, move
it here (or make it a skill if it's an invokable procedure) and leave a one-line pointer in
CLAUDE.md. Current contents:
- [docs/north-star.md](docs/north-star.md) — the design philosophy ("does this satisfy?").
- [docs/git-discipline.md](docs/git-discipline.md) — the *why* behind the Drive-sync git reflexes + the `user settings/` migration.
- [docs/avoid-prompting-user.md](docs/avoid-prompting-user.md) — how the Bash prompt-guard hook works and how to tune it (whitelist methods + reminder hooks).
- [docs/spells.md](docs/spells.md) — deep reference behind the `add-spell` skill.
- [docs/devpreview.md](docs/devpreview.md) — deep reference behind the `drive-game` skill (the three interface tiers in detail).
- [docs/testing-scenarios.md](docs/testing-scenarios.md) — deep reference behind the `test-scenario` skill.
- [docs/locate-behavior/](docs/locate-behavior/) — the architecture map + finder operating guide behind the `locate-behavior` skill (moved out of `.claude/` so the finder can self-update it without write prompts).

## C++ Migration

This project migrated from ../Necroking, refer to its files when trying to reimplement features that worked there.
