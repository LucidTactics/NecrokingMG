# Save / Load games — session snapshot to `saves/{name}.json`

The player-facing save-game feature (distinct from **map** save, which writes
`assets/maps/<map>.json` `placedObjects` via the map editor — see [editor.md](editor.md)).
A save is a JSON snapshot of the *play session* that is re-applied on top of a normal
`StartGame(mapName)` load. Deliberately partial: it currently captures map + player
position/form/buffs + spellbar, and explicitly does **not** yet cover inventory, mana/
NecromancerState, SkillBookState, or horde/world state.

## Files

### `Necroking/Data/SaveGameData.cs` — the serializable save model
What lives here: the DTOs that define the on-disk JSON shape. `SaveGameData` (root:
`Version`, `MapName`, `SavedAtUtc`, `Player`, `SpellBar` — a flat 10-entry `List<string>`
of SpellIDs), `SavedPlayer` (`X`/`Y`/`Facing`/`FormId` + `Buffs`), `SavedBuff`
(`Id`/`Remaining`/`Permanent`/`Stacks`), and `SaveGameInfo` (a **derived** list-row DTO for
the save/load menus — never serialized; carries `FormId`+`SpellBar` for the preview card).
All use `[JsonPropertyName]`. The class XML comment is the authoritative "what is / isn't
saved yet" list.
Look/edit here when: adding a new field to what gets persisted (add a property here first,
then read/write it in `Game1.Saves.cs`). This is THE save data model.

### `Necroking/Game1.Saves.cs` — save/load orchestration (Game1 partial)
What lives here: the read/write/apply pipeline. `WriteSaveGame(name)` builds a
`SaveGameData` from the live world (necromancer `Unit` at `_sim.NecromancerIndex` →
position/facing/`UnitDefID`/`ActiveBuffs`, and `_spellBarState.Slots`) and writes via
`JsonFile.Save`. `LoadSaveGame(name)` reads the JSON, validates the map exists, calls
`StartGame(save.MapName)`, then `ApplySaveToWorld(save)`. `ApplySaveToWorld` reuses the
map-spawned necromancer (never spawns a second player), applies form via
`_sim.TransformUnit`, teleports, clears+re-applies buffs through `BuffSystem`, and
overwrites `_spellBarState.Slots`. Also: `SaveFilePath`/`SaveFileExists`/`UniqueSaveName`/
`SanitizeSaveName`/`ListSaveGames` (path + enumeration helpers), `OpenSaveMenu`,
`_currentMapName` (what a save records), `_loadMenuSaves`.
Look/edit here when: adding a new piece of state to the save — you write it in
`WriteSaveGame` and restore it in `ApplySaveToWorld` (NOT in `LoadSaveGame`, which only does
map-load + validation). Note the ordering contract in `ApplySaveToWorld`: **form transform
first** (rebuilds stats/sprite/size), then buffs (so per-stack side effects land on the
final form's stat block), then position, then spellbar. Restore steps run AFTER `StartGame`,
so anything `StartGame` initialises (e.g. `_inventory = new Inventory(...)`, `_inventory.Clear()`)
is already in place and safe to overwrite.

### `Necroking/UI/SaveGameWindow.cs` — the Save submenu UI
What lives here: `SaveGameWindow` — pause-menu → Save submenu (existing-saves list + name
field + confirm button). Pure UI over the shared `EditorBase`; file work is injected from
`Game1.Saves.cs` via `SetCallbacks`. `OnOpen(formId, spells)` seeds the preview data.
Follows the SettingsWindow/MultiplayerWindow `WantsClose`-polled pattern.
`Draw(screenW, screenH)` renders everything: preview card, the scrollable
`save_list` panel of existing-save rows (each an `EditorBase.DrawButton`), the
`save_name` text field, and the bottom `New Save`/`Overwrite Save` + `Cancel`
buttons anchored at `btnY = panelY + PanelH - 42`. **Selection tracking:** there is
no selected-index — clicking a row just copies `_name = s.Name` (highlight = row
whose `Name == _name`); the current target save IS `_sanitize(_name)`. File work is
injected as `Func<>` callbacks via `SetCallbacks` (wired in `Game1.cs` ~line 2684:
`SetCallbacks(ListSaveGames, UniqueSaveName, SaveFileExists, WriteSaveGame, SanitizeSaveName)`).
**No delete-save capability exists** — no `File.Delete` helper in `Game1.Saves.cs`,
no delete callback field/button in this window. To add a "Delete selected save"
button: add a `DeleteSaveGame(name)` method in `Game1.Saves.cs` (delete
`SaveFilePath(name)`, then refresh via `ListSaveGames`), add a `Func<string,bool>
_deleteSave` field + a param to `SetCallbacks`, wire it in `Game1.cs`, and draw a
new button in `Draw` below the confirm row (e.g. its own row under `btnY`, calling
`_deleteSave(_sanitize(_name))` then `_saves = _listSaves()`).
Look/edit here when: the save dialog's list/name-field/confirm-button behaves wrong,
or adding save-row actions (delete/rename).

### Related
- `Necroking/GameRenderer.Hud.cs` — draws the load-menu rows and the save-preview cards
  (`DrawSavePreviewCard` + `DrawSaveGameText`, form portrait + spell/inventory icons from
  `SaveGameInfo`). Layout struct `LoadMenuView` built by `BuildLoadMenuLayout(screenW,
  screenH, saveCount)` — one rect per visible row + `BackRect`; consumed by BOTH
  `DrawLoadMenu` and Game1's hit-test so they can't drift.
- `Necroking/Game1.cs` — the load menu is a `MenuState.LoadMenu` family entry (Update block
  rebuilds `BuildLoadMenuLayout` and hit-tests `view.RowRects`); StartGame owns the
  per-game reset (see `GameSession` + StartGame/StartScenario asymmetry in
  [game1-partials.md](game1-partials.md)).

### Menu list scrolling (as of 2026-07)
Neither menu scrolls — both **truncate**:
- **Load menu**: `BuildLoadMenuLayout` caps rows at what fits (`maxRows`), `DrawLoadMenu`
  prints `(+N more)`. To add scrolling, copy the **scenario-menu draggable-scrollbar
  pattern** (same raw-HUD menu family): `ScenarioMenuView` + `_scenarioScrollPx` drag/wheel
  input in `Game1.cs` + `DrawScenarioScrollbar`, all on the shared `Necroking/UI/VScrollbar.cs`
  geometry — see [editor.md](editor.md) "Map-editor scrolling & scrollbars".
- **Save menu** (`UI/SaveGameWindow.cs`): shows `MaxVisibleSaves = 6`, prints
  `(+N older saves)`. It draws through `EditorBase`, so use
  `EditorBase.BeginScrollPanel`/`ScrollPanel.End` (or the interactive
  `DrawVScrollbar(id, …)` overload) for wheel + clip + draggable bar.

## Player inventory (candidate for the next save extension)

- **Runtime store:** `Game1._inventory` (`Necroking/Game1.cs`, field `internal Inventory _inventory`),
  type **`Necroking.GameSystems.Inventory`** (`Necroking/Game/Inventory.cs`). Constructed in
  `StartGame` as `new Inventory(20, _gameData.Items)` and `Clear()`-ed on each new game.
- **`Inventory` shape:** fixed `InventorySlot[]` (`{ ItemId, Quantity }`), stackable per
  `ItemDef.MaxStack`. Read via `SlotCount` + `GetSlot(i)`; there is **no public raw-slot
  setter** — restoration today would go through `AddItem(itemId, qty)` (which re-packs/stacks
  and would NOT preserve exact slot positions/ordering). For a faithful round-trip, add a
  `SetSlot`/serialize+restore pair to `Inventory`.
- **`_everSeen`** (grow-only "has the player ever held this" set, gates potion throw-spells in
  the grimoire) is also **not persisted yet** — mirror of `SkillBookState`. Decide whether a
  save should carry it too.

## Pitfalls / gotchas
- **Two DTO edits per new field:** property in `SaveGameData.cs` + write in `WriteSaveGame` +
  restore in `ApplySaveToWorld`. Missing the restore silently drops the field on load.
- **Registry validation:** loaded ids (spells, buffs, form) are validated against the live
  registries and skipped/cleared if missing (logged under the `saves` channel). Do the same
  for item ids so a removed item doesn't break load.
- **Don't clobber user defaults:** `ApplySaveToWorld` deliberately does NOT call
  `SaveSpellBars()` — loading a save must not overwrite `user settings/spellbar.json`.
- **Restore after StartGame:** put inventory restore in `ApplySaveToWorld` (post-`StartGame`),
  because `StartGame` recreates + clears `_inventory`.

## Cross-links
- [game1-partials.md](game1-partials.md) — StartGame/GameSession per-game reset; `_spellBarState`.
- [ui.md](ui.md) — inventory panel (`UI/InventoryUI.cs`) and other consumers of `_inventory`.
- [data-registries.md](data-registries.md) — `JsonFile.Save`/`JsonDefaults`, `ItemRegistry`/`ItemDef.MaxStack`.
- [editor.md](editor.md) — the unrelated **map** save (`SaveMap` → `placedObjects`).
