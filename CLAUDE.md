# Necroking (MonoGame C#) - Claude Code Instructions

## Git Policy
- **Commits**: OK to commit freely when a feature is known to be working. Good initiative.
- **Branches**: Always commit to master. User doesn't understand branch management well.
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

## Bash Commands
A `PreToolUse` hook (`tools/hooks/bash_prompt_guard.py`) gates every Bash call. Keep commands guard-friendly (full detail in [tools/hooks/CLAUDE.md](tools/hooks/CLAUDE.md)):
- **Read-only runs free.** No mutation = auto-allowed. Compound commands pass silently only when *every* segment (split on `&&`/`||`/`;`/`|`/newline) is read-only or allow-listed.
- **Avoid, or expect a prompt/denial:** `>`/`>>` redirection, `$(...)` command substitution, heredocs, and mutating forms (`sed -i`, `find -delete`/`-exec`, `git push`, process/power-control). Non-allow-listed commands that *can* mutate are denied by default.
- **Do:** prefer tools `Grep`/`Glob`/`Read` over `grep`/`find`/`cat` (clickable, faster). When the gate blocks legitimate work, fix it via an allow rule or catalogue entry — don't work around it silently.

## Directory Layout
```
NecrokingMG/
  assets/          (sprites, textures, UI, effects, fonts, items, maps/)
  data/            (game-data JSON: registries, env_defs, spellbar)
  resources/       (shaders .fx, fonts .spritefont, Content.mgcb)
  bin/             (build output — exe + runtime DLLs only)
    Publish/
    Debug/
  Necroking/       (C# source code, .csproj — the only project; folder map in docs/code-map.md)
  tools/
  todos/           (temporary research/task notes for future sessions)
  docs/            (on-demand reference: stable info to look up, not in context each session)
```

## File Conventions
- C# source in `Necroking/`, organized by subsystem — per-folder purposes and "where does new code go" in [docs/code-map.md](docs/code-map.md)
- Main game loop in `Necroking/Game1.cs`, entry point in `Necroking/Program.cs`
- Assets at root `assets/` (Environment/, Effects/, Items/, Sprites/, UI/, fonts/)
- Game data at root `data/` (JSON registries, settings)
- Maps in `assets/maps/` (default.json, triggers, roads, wall_defs; gitignored, Drive-synced with the other assets)
- Shaders and fonts in `resources/`
- Tools/scripts in `tools/`
- All paths resolved via `GamePaths.Resolve()` — no DualSave, no file copying to build output
- **Asset paths must be relative** (e.g. `assets/Environment/Trees/Oak1.png`), never absolute (e.g. `E:/Nightfall/NecrokingMG/assets/...`). This applies to JSON data files (`env_defs.json`, etc.), C# code, and editor-saved paths. `GamePaths.Resolve()` converts relative paths to absolute at runtime.

## Map Content Lives In The Map, Not In Code
**If the user asks for something to be placed in the world — a building, foragable, prop, decoration, unit — add it to the map JSON, not to a code path that spawns it at startup.** Hardcoded startup spawns step on the player's map edits: they save the map without the object, restart, and the code re-spawns it. The save *worked*; the load just stomps it.

- **Adding world content** → edit `assets/maps/default.json` directly (or via the in-game map editor and save). Use `tools/` scripts when the JSON is too large to edit interactively.
- **Don't** write `_envSystem.AddObject(...)` or `SpawnUnit(...)` calls in `LoadContent` / `LoadGame` / startup paths unless the user explicitly asks for "always re-spawn this on game start regardless of the map." If unsure, ask.
- **Exception — true fallbacks:** the necromancer fallback at the start of `LoadGame` ("if no necromancer in placed units, spawn Wretched at map center") is fine because it only fires when the map provides nothing. Distinguish "fill in what's missing" from "always add on top of what's there."
- **Past offenders (now removed):** `SpawnStarterMushroom` and `SpawnStarterBlightAltar` in `Game1.cs` unconditionally inserted a Deathcap and a Blight Altar near the necromancer every launch, making them un-deletable via the map editor. Both removed 2026-05-13.

## UI Text Rendering
- SpriteBatch uses `SamplerState.PointClamp` — text drawn at sub-pixel positions gets aliasing artifacts
- **Always round text positions to integer pixels**: `new Vector2((int)x, (int)y)`
- `EditorBase.DrawText` already rounds internally, but any direct `DrawString` calls (e.g. in `Game1.cs`) must round manually
- When centering text with `MeasureString`, the division produces floats — cast to `int` before passing to draw

## Architecture & Code Organization

### Direct over Inject

Don't dependency inject functions, instead use static references. Instead of SetCallbacks(ListSaveGames), just call
`Game1.Instance.ListSaveGames` or `_g.ListSaveGames` directly. We don't need these dependency injections,
this is a very straightforward game project not a big corpo architectural monster.

Also prefer passing Game1 and refer to items in Game1 rather than passing a long list of things that are in Game1.
Game1 will always be there.

Use delegates for map/reduce or other such functional jobs, but not for dependency injection.

### Before Writing New Code or editing Json data files such as adding spells or editing the map file
1. **Locate first — use the `locate-behavior` skill, don't freestyle grep.** Any time you
   need to find *where* a behavior lives or *where new code should go* (which file/function
   is responsible, where to add a feature, what file to create), invoke `/locate-behavior`
   **before** reaching for Grep/Glob. It routes through a self-extending architecture map
   and you leave the map better than you found it — ad-hoc grepping skips that and is the
   thing to avoid. Grep/Glob/LSP are for *verifying* a symbol the skill pointed you at, not
   for the initial "where is this" search.
2. **Use `locate-behavior` again after scope changed significantly!** — we always want a well-grounded understanding of the code we work on.
3. **Always use `locate-behaviour` before every refactor** - Even if you think you have enough context right now ask it again, refactors shouldn't be done lightly. It might even find wider refactor opportunities for you or potential problems that invalidates the refactor.
