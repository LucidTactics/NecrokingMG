---
name: edit-game-data
description: Edit the Necroking data/ JSON files (spells.json, units.json, items.json, potions.json, buffs.json, weapons.json, armor.json, etc.) — any data file holding a list of unique-"id" structs. Use for read/duplicate/delete/create/update of a struct by id instead of hand-editing the large JSON. Triggers — add/clone/remove/tweak a spell, unit, item, potion, buff, weapon, or armor entry; change a field on an existing entry; look up one entry's values.
---

# Editing Necroking game data JSON

Most files in `data/` are an object with **one list-valued key** whose entries are
structs that each carry a unique string **`id`**:

```json
{ "spells": [ { "id": "fireball", ... }, { "id": "frostbolt", ... } ] }
```

(`units.json` → `units`, `potions.json` → `potions`, `buffs.json` → `buffs`,
`items.json`, `weapons.json`, `armor.json`, `shields.json`, … all follow this shape.)
These files are large — **don't hand-edit them** for routine changes. Use
`tools/json_data.py`, which edits the list in place and **preserves the exact
formatting** the game writes (2-space indent, `ensure_ascii`, no trailing newline),
so diffs stay minimal.

## When NOT to use this
- Files that aren't a single id-keyed list (e.g. `settings.json`, `weather.json`,
  `frame_centroids.json`, map files). The tool validates the shape and errors out
  if it doesn't match — that's expected; edit those by hand or with a purpose script.
- Bulk/derived changes across many entries → write a one-off script in `tools/`.

## Command

```bash
python tools/json_data.py <file.json> <op> [target_id] [args...]
```

The tool first validates the file really is a list of uniquely-`id`-keyed structs;
if not it errors without writing.

### Operations

| op | form | what it does |
|----|------|--------------|
| `list` | `list` | Print every struct's id, one per line. No write. Use to discover ids. |
| `read` | `read <id>` | Print that struct as JSON. No write. Use to inspect before editing. |
| `duplicate` | `duplicate <id> <new_id> [k=v ...]` | Deep-copy the struct, give the copy `<new_id>`, apply overrides, append. Errors if `<new_id>` exists. |
| `delete` | `delete <id>` | Remove the struct. |
| `create` | `create <new_id> [k=v ...]` | New struct `{ "id": <new_id>, ...overrides }`. Errors if id exists. |
| `update` | `update <id> [k=v ...]` | Update keys on the struct. Cannot change `id`. |

### `key=value` value parsing

- `key=null` → **deletes** the key (on `create`, the key is just not set).
- `key="null"` → the **string** `"null"`.
- `key=true` / `key=false` → boolean. `key=12` / `key=3.14` → number.
- `key="12"` → the **string** `"12"` (explicit quotes force a string).
- `key=foo` → the string `foo`.
- `key={"r":1,"g":2}` / `key=[1,2,3]` → parsed as JSON (nested object / list).

Anything that parses as JSON is taken as JSON; otherwise it's a bare string. In a
shell, quote values with spaces or JSON braces (`name="Ice Bolt"`,
`color='{"r":1,"g":2,"b":3,"a":255,"intensity":1.0}'`).

## Examples

```bash
# List every id in the file
python tools/json_data.py data/spells.json list

# Inspect an entry before changing it
python tools/json_data.py data/spells.json read fireball

# Clone a spell into a new variant, overriding a few fields
python tools/json_data.py data/spells.json duplicate fireball icebolt \
    name="Ice Bolt" damage=12 primaryPath=water

# Tweak fields on an existing entry
python tools/json_data.py data/spells.json update fireball manaCost=3 hidden=true

# Remove a key entirely / set a string vs delete
python tools/json_data.py data/spells.json update fireball secondaryPath=null

# Create from scratch
python tools/json_data.py data/buffs.json create buff_custom name="Custom" duration=5

# Delete an entry
python tools/json_data.py data/potions.json delete potion_frenzy
```

## Tips
- To find valid field names/values for a new entry, `read` an existing similar entry
  first and mirror its keys.
- To **rename** an id: `duplicate <old> <new>` then `delete <old>` (update refuses to
  touch `id`). Check nothing else references the old id (grep the codebase/data).
- After editing data the game reads at startup, restart/reload to see the change
  (drive the running game via the dev server — see CLAUDE.md).
