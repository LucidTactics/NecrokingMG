### Editing data JSON (id-keyed registries)
Most `data/*.json` files (`spells.json`, `units.json`, `items.json`, `potions.json`,
`buffs.json`, `weapons.json`, `armor.json`, …) are a single list of structs each with
a unique `id`. **Don't hand-edit these large files for routine changes** — use the
**`edit-game-data` skill** (`tools/json_data.py`: read / duplicate / delete / create /
update a struct by id, preserving the game's exact formatting). Invoke `/edit-game-data`
or just run the tool. Bulk/derived changes across many entries → a one-off `tools/` script instead.

**After changing any `data/*.json`, run the roundtrip so the file matches what the game
would write:** `dotnet build Necroking/Necroking.csproj` then `bin/Debug/Necroking.exe
--roundtrip-data` (headless, loads + re-saves every `data/` registry, reports which files
changed, exits; never touches `assets/maps`). This data is **not meant to live
hand-crafted** — the game re-serializes it on every in-editor save, which reorders
properties to class-declaration order, prunes fields equal to their default, unescapes
`\uXXXX`, and writes LF. Running `--roundtrip-data` up front bakes those changes in so you
**see and commit what the data will actually look like** after being edited from in-game,
instead of a hand-authored diff the game silently rewrites later. Commit the roundtripped
result, not the raw hand-edit.