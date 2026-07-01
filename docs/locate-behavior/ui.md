# UI/ — overlays, panels & the selected-unit sheet

Player-facing overlays and info panels. Most panels are built on the **runtime widget
renderer** (`UI/RuntimeWidgetRenderer.cs`, driven by JSON widget defs) and register as
modal layers on `Game1.Popups` (`UI/PopupManager.cs`). Each panel owns its own show/hide
and hit-test; there is **no central "which panel for this entity" dispatcher** — the choice
is distributed across `Game1.cs` input handlers and the HUD tooltip renderer (see below).

## The selected/inspected unit sheet (right-side "unit bar")

`Necroking/UI/UnitInfoPanel.cs` — **the right-side unit info panel**. Class `UnitInfoPanel`
(a widget on the `UnitSheetDyn` JSON widget). Draws the character sheet: name/desc/portrait,
the stat grid, equipment + attack rows, and the Abilities & Buffs row, plus hover tooltips.
- `Draw(screenW, screenH, sim)` — resolves the pinned unit id → current index each frame
  (`sim.ResolveUnitID`), positions the panel at `screenW - PanelW - 12`, vertically centered,
  and renders. **This is where the right-side panel is rendered.** Bails (`Hide()`) if the
  pinned unit is gone/dead.
- `ShowForUnit(uint unitId)` — pin via 'U' (necromancer) / 'O' (inspect-under-cursor);
  pushes onto the popup stack. `ShowForUnitTransient(uint unitId)` — cursor-driven auto-hover
  view (NOT pushed; see `IsTransient`). `Hide()`.
- `Populate` / `ComputeAbilitiesLayout` / `DrawAbilitiesRow` — fill the widget from a `Unit`.
- Init'd lazily in `Game1.cs` `EnsureInventoryUIsInitialized` (field `_unitInfoPanel`), which
  also wires `DrawUnitIconCallback` (live idle sprite) and `OnClosed`.

**Look/edit here when:** changing what the selected-unit right panel shows or how it's laid
out; adding a new section/row to the sheet; **routing a different panel for a specific
selected entity** (a boar's belly, etc.) — but note the *decision* to show it lives in
`Game1.cs`, not here (below).

## Where the panel-type decision actually lives (Game1.cs)

There is no single "pick panel by entity" switch. The selected-unit sheet is shown by three
input paths in **`Necroking/Game1.cs`** (all operate on `_unitInfoPanel`):
- 'U' key → `_unitInfoPanel.ShowForUnit(necromancer.Id)` (~line 2600).
- 'O' key → picks nearest unit under cursor → `ShowForUnit(...)` (~line 2637).
- **Auto-hover** block gated on `Settings.Tooltips.AutoShowUnitStats` (~lines 3312-3356):
  scans `_sim.Units` for the nearest **unit** within `HoverPickRadius` and calls
  `ShowForUnitTransient` / `Hide`. **This only ever picks units** — env objects/buildings are
  NOT considered here. Env objects are hovered separately (`_hoveredObjectIdx`) and surfaced
  as a cursor tooltip, not a right-side panel.

So to make **a selected zombie boar show a corpse-pile-style panel instead of the unit bar**,
the branch to edit is this auto-hover block (and/or the 'O'/'U' handlers): after resolving the
hovered unit, check the unit's def tag (`forager`, or `UnitDefID`), and if it's a belly-carrier,
show the new belly panel instead of calling `ShowForUnitTransient`. There is currently no
per-entity panel router to extend — you add the branch here.

## The "corpse pile" contents panel (what to mirror)

**Corpse piles are NOT a separate right-side panel.** A corpse pile is an env object
(`corpse_pile` def, `def.IsBuilding` + `def.StoredResource == "Corpse"`); its stored bodies
live in `WorkerSystem` stockpiles, not on the object. Its "inventory panel" is a **floating
cursor tooltip**:
- `Necroking/Render/HUDRenderer.cs` → `DrawObjectTooltip(hoveredIdx, envSystem, sim, gameData,
  screenW, screenH)` — branches on env-object kind; for a building whose `StoredResource ==
  "Corpse"` it appends `"Corpses:"` + `sim.Workers.PiledCorpseLines(hoveredIdx)` (grouped
  "Skeleton x3" lines), else `"Empty"`, then `DrawCursorTooltip(lines, …)`. **This is the
  corpse-pile "inventory content" renderer to mirror.**
- Content source: `Necroking/Game/Jobs/WorkerSystem.cs` → `PiledCorpseLines(objIdx)` returns
  the grouped `List<string>`; `StoredOf`/`TakePiledCorpse` back it.
- The pile is dispatched to `DrawObjectTooltip` via the hovered-object index (`_hoveredObjectIdx`)
  wired through `GameRenderer.Hud.cs` (~line 610) into `HUDRenderer`.

**Look/edit here when:** changing what the corpse-pile tooltip lists, or building a new
belly/inventory list panel modeled on `PiledCorpseLines` + `DrawCursorTooltip`.

## Unit hover picking + a "programmer/debug" cursor tooltip for units

There is **no reflection-based "programmer tooltip"** anywhere — the term is descriptive.
The nearest existing pattern is the **cursor-tooltip family in `Necroking/Render/HUDRenderer.cs`**
that already surfaces raw-ish data fields for non-unit things on hover:
- `DrawObjectTooltip` (env objects/buildings: name/id, `HP:`, `Owner:`, process state, pile contents),
  `DrawBellyTooltip` (forager unit belly), `DrawCorpseTooltip` (corpse name). All build a
  `string[]`/`List<string>` and call the shared `DrawCursorTooltip(lines, screenW, screenH)`.
- **Dispatch:** all three are called from `HUDRenderer.Draw` (~line 195-197). To add a
  **unit** cursor tooltip, add a `DrawUnitTooltip(hoveredUnitIdx/Id, sim, gameData, …)` here
  and call it in that same block; thread the hovered id through `GameRenderer.Hud.cs`
  `DrawHUD` (~line 604) just like `_hoveredBellyUnitId` already is.

**Hovered-unit index is already computed** in `Necroking/Game1.cs` (~line 3479):
`_hoveredUnitIdx` (nearest live unit within `Tooltips.HoverPickRadius`, gated on
`Settings.Tooltips.ShowHoverHighlight`). `_hoveredBellyUnitId` (~3508) is derived from it.
Reuse `_hoveredUnitIdx` / add a `_hoveredUnitId` field the same way rather than re-scanning.

**Membership data for "in player horde / village / faction" (per unit):**
- Faction: `Unit.Faction` (`Necroking/Movement/UnitSystem.cs`, enum `Faction { Undead, Human, Animal }`
  in `Data/Enums.cs`). "Player horde" = `Faction.Undead` **and** in the horde (below).
- Horde: `sim.Horde` (`Game/HordeSystem.cs`) → `IsInHorde(uint id)`; `GetUnitState(id)` for
  Following/Chasing/Returning if wanted. Only meaningful for `Faction.Undead`.
- Village: `Unit.VillageId` (short, -1 = none, `UnitSystem.cs`) → `sim.Villages?.Get(villageId)?.Name`
  (`Game/VillageSystem.cs`). Village membership is a **per-unit tag**, set at spawn in
  `Game1.Villages.cs` (`_sim.UnitsMut[idx].VillageId = …`); the `VillageSystem` recomputes only
  aggregate counts/posture, not membership.
- There is **no separate "group" concept** beyond faction/village/horde. Basic data to show
  (per the ask): unit def display name + `Unit.Stats.Health`/max (HP) — **not** the full stat
  grid (that's the right-side `UnitInfoPanel`).

**Pitfall:** the boar-belly path already claims foragers on hover (`_hoveredBellyUnitId`,
suppressed from the stat sheet) — a new unit tooltip should skip forager units (or it'll
double up with the belly tooltip). Also gate it so it doesn't fight the auto-hover
`UnitInfoPanel` (`AutoShowUnitStats`): the stat sheet is a right-side panel, a programmer
tooltip is a cursor tooltip, so they can coexist, but decide whether both show at once.

## Data source for a boar's belly (mushrooms eaten)

Belly contents are stored per unit id in **`Necroking/Game/Simulation.cs`**:
- `_boarBellies` — `Dictionary<uint,List<ushort>>` (unit id → env def indices of eaten
  mushrooms, eat order). **Private, no public read accessor yet** — a belly panel needs a new
  getter (e.g. `IReadOnlyList<ushort> BoarBelly(uint unitId)` or a grouped-line helper mirroring
  `PiledCorpseLines`). `Unit.BellyMushrooms` (byte count) is on the SoA struct.
- Written by `Necroking/AI/BoarForageAI.cs` (`sim.AddBoarBelly` + `DestroyObject`); spat out
  on death in `Simulation.SpitBoarBellyOnDeath`. See [world.md](world.md) / [corpses.md](corpses.md).
- To label a def index → display name: `_envSystem.Defs[defIndex]` (`.Name`) or the foragable's
  `ForagableType` → `gameData.Items.Get(...).DisplayName` (as `DrawObjectTooltip` does for
  foragables).

### Where new code goes — boar belly panel
1. **`Game/Simulation.cs`** — add a public read accessor for `_boarBellies` (indices or a
   grouped `List<string>` helper mirroring `WorkerSystem.PiledCorpseLines`).
2. **`Game1.cs`** auto-hover block (~3345) — when the hovered unit is a belly-carrier
   (`gameData.Units.Get(id).Tags.Contains("forager")`, or `BellyMushrooms > 0`), suppress
   `ShowForUnitTransient` and instead show the belly panel; mirror in 'O'/'U' handlers if pinning
   is wanted.
3. **The panel itself** — cheapest is to render a belly list as a cursor tooltip via
   `HUDRenderer.DrawCursorTooltip` (mirror `DrawObjectTooltip`'s corpse branch). For a real
   right-side sheet, add a small widget-backed panel in `UI/` next to `UnitInfoPanel` (or a
   variant mode of it) — heavier.

## The Grimoire (spell list / spells menu panel)

`Necroking/UI/GrimoireOverlay.cs` — **the spell-list panel** ('J' to browse; also opened in
assign mode when a spell-bar slot is clicked). `class GrimoireOverlay : IModalLayer`, a
`RuntimeWidgetRenderer` widget (`GrimoirePanel.WidgetId == "GrimoireDyn"`, fixed `PanelW=706`,
`PanelH=1080`).
- **Screen position is computed in `Layout(int screenH)`** — sets the panel's top-left
  `_x`/`_y`: currently `_x = 16` (already left-anchored, 16px inset) and
  `_y = Math.Min(0, (screenH - PanelH) / 2)` (vertically centered, clamped so a taller-than-
  screen panel starts at y=0 rather than going negative-past-center). To flush-left it, set
  `_x = 0` here.
- **`_x`/`_y` are the single source of truth for placement AND hit-testing** — `Layout` is
  called at the top of both `Draw` (`DrawWidget(WidgetId, _x, _y, InstanceId)`) and `Update`,
  and every hit-test derives from the same `_x`/`_y`: `ContainsMouse` (the `IModalLayer`
  bounds `_x.._x+PanelW`), `HitChild`/`HitTab`/`TabRect` (via `_renderer.GetChildRect(..., _x,
  _y, ...)`), and `DebugChildCenter`. So changing the formula in `Layout` moves draw and
  clicks together — **edit in one place, no separate hit-rect to sync.**
- `GrimoirePanel.cs` (`static class GrimoirePanel`) is only the tile *binder* (filter list +
  bind the 22-tile scroll window); it holds no position. `SkillBookOverlay.cs` is a sibling
  with the same nested-tab pattern.

**Look/edit here when:** repositioning/anchoring the spell list panel, changing its size, or
its scroll/tab/tile hit-testing.

## Other panels in UI/ (brief)
- `UI/JobBoardUI.cs`, `UI/GraveRosterUI.cs` — worker economy (see [jobs-workers.md](jobs-workers.md)).
- `UI/GrimoireOverlay.cs` / `GrimoirePanel.cs`, `UI/SkillBookOverlay.cs` — spellbook / skills (Grimoire positioning above).
- `UI/RuntimeWidgetRenderer.cs` — JSON-widget layout/draw engine every panel uses.
- `UI/PopupManager.cs` — modal-layer stack (`Game1.Popups`), ESC/click routing, MouseOverUI.
- `UI/StatTooltips.cs`, `UI/ResourceTooltip.cs`, `UI/NineSlice.cs`, `UI/WidgetLayoutUtils.cs`,
  `UI/PopupManager.cs` — shared tooltip/layout helpers.
- Inventory/crafting/building menus live under `Game/` (`InventoryUI`, `BuildingMenuUI`,
  `CraftingMenu`, `TableMenuUI`), not `UI/`.

## Cross-links
- [corpses.md](corpses.md) — corpse data model, pile gather/withdraw (`TryTakeCorpseFromPile`,
  `FindCorpsePileUnderCursor` in `Game1.cs`).
- [world.md](world.md) — env objects, foragables, boar belly spit-on-death.
- [jobs-workers.md](jobs-workers.md) — `WorkerSystem` stockpiles behind pile contents.
- [render.md](render.md) — HUD/effects rendering.
