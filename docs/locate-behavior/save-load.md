# Save / Load games — session snapshot to `saves/{name}.json`

The player-facing save-game feature (distinct from **map** save, which writes
`assets/maps/<map>.json` `placedObjects` via the map editor — see [editor.md](editor.md)).
A save is a JSON snapshot of the *play session* that is re-applied on top of a normal
`StartGame(mapName)` load. Currently captures map + player position/form/buffs + spellbar
+ **inventory slots** (`SavedInventorySlot`, slot-indexed) + **the skill book**
(`SavedSkillBook` — learned set, point pools, event tallies, passive flags, intrinsic-buff
bindings, metamorphosis bonuses; the pure unlocks like `UnlockedBuildings` are re-derived
from the learned talents on load, not stored — see the skill-book bullet below).
Still **not** covered: raw mana/NecromancerState, `Inventory._everSeen`, horde/world state,
and **player-placed world buildings** (those survive only via map save — see the comment in
`UI/BuildingMenuUI.cs` `TryPlace`).

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
- `Necroking/UI/LoadGameWindow.cs` — the Load Game window (`MenuState.LoadMenu`,
  reachable from main AND pause menu; hosted by `MenuHostLayer`, `_backMenuState` decides
  where Back/ESC returns). An EditorBase-drawn `WantsClose` window like SaveGameWindow —
  NOT a screen class. **Deferred-load pattern:** a row click only sets `PendingLoad`
  (clicks land during the Hud render pass where rebuilding the world isn't safe);
  `Game1.Update` (the `MenuState.LoadMenu` block, ~line 3000 in `Game1.cs`) picks it up
  next frame and runs the actual `LoadSaveGame`. `Open()` refreshes its own save list and
  sets `_menuState = MenuState.LoadMenu`.
- `Necroking/GameRenderer.Hud.cs` — the save-preview widgets both save UIs share:
  `DrawSavePreviewCard` + `DrawSaveGameText` (form portrait + spell/inventory icons from
  `SaveGameInfo`), called by `LoadGameWindow.Draw` and `UI/SaveGameWindow.cs`.
- `Necroking/Game1.cs` — StartGame owns the per-game reset (see `GameSession` +
  StartGame/StartScenario asymmetry in [game1-partials.md](game1-partials.md)).

## In-memory split & the load-time side-effect census (mode / camera)

The pipeline is already almost file-free — the file I/O is confined to two lines:

- **Save-to-state:** `Game1.Saves.cs` `GetSaveDataJson()` builds the complete in-memory
  `SaveGameData` from the live world (returns null when no necromancer).
  `WriteSaveGame(name)` = `SpiritWalkSystem.End(this)` (snap spirit back to body first) +
  `GetSaveDataJson()` + `JsonFile.Save`. Reuse `GetSaveDataJson` for any in-memory
  snapshot; remember the spirit-walk End if the snapshot can happen mid-walk.
- **Load-from-state:** `LoadSaveGame(name)` = `JsonFile.Load` + map-exists validation +
  `StartGame(save.MapName)` + `ApplySaveToWorld(save)`. The last two calls ARE the
  file-free "apply state" path — an in-memory round-trip (e.g. the map-editor Reload
  button) is just `StartGame(state.MapName); ApplySaveToWorld(state);`.

**Side effects to know when reloading without leaving the current UI mode** (verified 2026-07):
- `StartGame` (`Game1.cs`): `_camera.Position = necro pos` + `_sim.Horde.CircleCenter`
  (right after the necromancer fallback spawn), `_camera.Zoom = 24f` (48f on empty_test),
  and at the very end `_menuState = MenuState.None` + `_gameWorldLoaded = true`. It also
  re-feeds the (app-lifetime) map editor: `_mapEditor.Init(...)` (recreates the
  EnvObjectEditor/WallEditor sub-windows — their state is lost), `RestoreTabFromSettings()`
  (no-op in practice: `Settings.General.MapEditorLastTab` is rewritten on every tab click),
  `SetMapFilename(mapName)`, `SetPlacedUnits(...)`, `SetGrassData(...)`. All other map-editor
  UI state (tab scrolls, selections, brush settings, undo stack) survives untouched.
- `ApplySaveToWorld`: `_camera.Position = saved pos` (+ `Horde.CircleCenter`, which is
  world state, not camera).
- To keep the camera / stay in an editor: capture `_camera.Position`/`_camera.Zoom` before
  and restore after, and re-set `_menuState` — the MapEditor-exit pathfinder-rebuild hook in
  `Game1.Update` (`_prevMenuState == MapEditor && _menuState != MapEditor`, ~line 2682)
  compares at frame granularity, so a within-frame None→MapEditor flip never fires it
  (harmless anyway — StartGame does a full `RebuildPathfinder`).
- Precedent for a reload-consistency test: `Scenario/Scenarios/MapReloadConsistencyScenario.cs`
  calls `StartGame` twice and diffs the world.

### Menu list scrolling
- **Load menu** (`UI/LoadGameWindow.cs`): EditorBase-drawn (scroll offset id
  `"load_list"` via `_ui.SetScrollOffset`) — see [editor.md](editor.md)
  "Map-editor scrolling & scrollbars" for the shared scroll machinery.
- **Save menu** (`UI/SaveGameWindow.cs`): shows `MaxVisibleSaves = 6`, prints
  `(+N older saves)`. It draws through `EditorBase`, so use
  `EditorBase.BeginScrollPanel`/`ScrollPanel.End` (or the interactive
  `DrawVScrollbar(id, …)` overload) for wheel + clip + draggable bar.

## Player inventory & skill book (both saved now)

- **Inventory** is persisted: `Game1.Saves.cs` `SnapshotInventory()` → `SavedPlayer.Inventory`
  (`SavedInventorySlot { Slot, ItemId, Quantity }` — slot-indexed so gaps round-trip),
  restored in `ApplySaveToWorld` after `StartGame` recreates+clears `_inventory`.
  Runtime store: `Game1._inventory` (`Necroking.GameSystems.Inventory`, `Necroking/Game/Inventory.cs`).
- **Skill book** is persisted: `SaveGameData.SkillBook` (`SavedSkillBook` DTO) ←
  `SkillBookState.ExportSave()` / → `ApplySave()`. The DTO only stores what can't be
  re-derived (learned talents, skill points, event tallies, passive flags, intrinsic-buff
  bindings, metamorphosis bonuses). The pure unlocks (potions/buildings/summons/AI/potion
  slots) are **NOT** saved — `ApplySave` re-derives them by replaying each learned talent's
  effect (`DerivableOnLoad` whitelist, + the code-built `grant_path` buff). So **a new pure,
  idempotent unlock = just add its effect id to `DerivableOnLoad`** (no DTO change); a
  non-derivable state still needs a `SavedSkillBook` property + ExportSave/ApplySave lines
  (see [skills.md](skills.md)).
- **`Inventory._everSeen`** (grow-only "has the player ever held this" set, gates potion
  throw-spells in the grimoire) is still **not persisted**.

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
- [anti-patterns-init.md](anti-patterns-init.md) — load/save anti patterns (append-without-clear
  growth loops, GPU/state not reset per load, persisting derived/foreign state, saving over a
  partial load, load-time validation). Read before touching load/save code.
- [game1-partials.md](game1-partials.md) — StartGame/GameSession per-game reset; `_spellBarState`.
- [ui.md](ui.md) — inventory panel (`UI/InventoryUI.cs`) and other consumers of `_inventory`.
- [data-registries.md](data-registries.md) — `JsonFile.Save`/`JsonDefaults`, `ItemRegistry`/`ItemDef.MaxStack`.
- [editor.md](editor.md) — the unrelated **map** save (`SaveMap` → `placedObjects`).
