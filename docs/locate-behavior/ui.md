# UI/ — overlays, panels & the selected-unit sheet

**Draw-surface convention (SpriteScope lockdown, 2026-07-17):** ALL UI draw code goes
through a `SpriteScope` (`private SpriteScope Scope => Game1.Instance.Scope;` — a
PROPERTY, never a cached field, and never a stored/Init-passed `SpriteBatch`) and
authors **STRAIGHT-alpha colors** — a raw `SpriteBatch.Draw` would send colors to the
premultiplied `AlphaBlend` state unconverted (silently fine while opaque, wrong at the
first translucent color). The raw batch is reachable only via `Scope.Batch` +
`Scope.EncodeTint(color)` for FontStashSharp `DrawString` extensions (precedent:
`WidgetLayoutUtils.DrawTextBlock`, `RuntimeWidgetRenderer.DrawText`) and sanctioned
Begin/End cycles (`EditorBase.BeginClip/EndClip`). `Game1._spriteBatch` is private and
`tools/check_spritebatch_scope.py` enforces zero raw-`SpriteBatch` mentions outside
render plumbing — run it after touching draw code. Full premult map:
[render.md](render.md) "Premultiplied alpha".

Player-facing overlays and info panels. Most panels are built on the **runtime widget
renderer** (`UI/RuntimeWidgetRenderer.cs`, driven by JSON widget defs) and sit in the
**UIRouter** (below). Each panel owns its own show/hide and hit-test; there is **no
central "which panel for this entity" dispatcher** — the choice is distributed across
`Game1.cs` input handlers and the HUD tooltip renderer (see below).

## THE UIRouter — one z-ordered list for clicks, hover, and drawing

**`UI/UIRouter.cs` + `UI/UILayer.cs` + `UI/Layers/*.cs`** — every clickable/drawable UI
surface is a `UILayer` in one list, ordered by `UIBand` (World=0 → Hud → Panels → Overlay
→ HudTop → Toast → Menu → Editor → Popup → Tooltip) + registration order within a band.
Registered once in the **`Game1` ctor**; layers gate themselves with live `Visible`
getters (no per-frame push/pop syncing).

- **Input**: `_uiRouter.DispatchInput(_input, ctx)` — ONE call in `Game1.Update` (after
  `RebuildUIHitRects` + `GateWorld`) walks layers **top-down**; nothing is pre-consumed —
  each layer's standard template (`UILayer.HandleInput`) applies the old modal rules
  (inside-click consume / LightDismiss / Blocking / CloseOnOutsideClick gated on
  `!MouseOverUI`) at its own turn. ESC goes to the topmost visible `Closable` layer.
  The router stamps per layer: `InputGranted` (input reached me un-consumed), `IsHovered`
  (I own the cursor — the layer a click would land in), `HoverStolen` (someone else does).
- **Hover/click sync guarantee**: a widget may only hover-highlight when its layer
  `IsHovered` — `PanelLayer` masks press edges (`!InputGranted`) and parks `MousePos`
  off-screen (`HoverStolen`) around the wrapped panel's Update AND Draw, so a covered
  panel/button can neither light up nor act where a click wouldn't reach it.
- **Drawing**: `DrawHudBlock` (`GameRenderer.Draw.cs`) calls `_uiRouter.Draw(ctx)` — the
  same list bottom-up. "Move X above Y" = change its band, never a moved draw call.
- **Adapters** (`UI/Layers/`): `PanelLayer` wraps any `IModalLayer` panel (+ optional
  update/draw delegates, drag→mouse-capture provider); `HudLayers.cs` holds the HUD widget
  layers (spell bar, time controls, aggression bar, `HudChromeLayer` draw-only chrome) and
  the **HudTop** rows (`CoreMenuButtonsLayer`, `EditorLauncherLayer` — above panels AND the
  blocking skill book, below toasts); `HostLayers.cs` holds `MenuHostLayer`
  (pause/settings/multiplayer + game-over draw), `MapEditorLayer` (the map editor as its
  own NON-blocking seat: footprint = its side panel via `MapEditorWindow.IsPanelAt`, world
  stays paintable underneath, owns map-editor scroll-zoom + ESC + draw), `UIEditorLayer`
  (the UI editor as its own BLOCKING modal seat: dim swallows outside clicks, but
  ContainsMouse = its centered window via `UIEditorWindow.IsPanelAt` so hover ownership is
  exact), `EditorHostLayer` (the remaining unit/spell/item full-screen editors as one
  opaque blocking seat), and `ModalStackLayer` (the editors' transient sub-popup stack,
  still `Game1.Popups`/`PopupManager` — editor-internal only).
- **Dev verbs**: `ui_click <sx> <sy> [right]` injects a click through the real dispatch
  (reports registry hit id, hover-owner layer id, consumed); `ui_key escape` drives the
  ESC chain; `ui_rects` dumps the hit registry.

**Look/edit here when:** anything about click routing, consumption, hover gating, z-order,
adding a new panel/overlay (give it a `PanelLayer` seat in the `Game1` ctor), or moving
something above/below something else.

### Cursor-tooltip z-order trap (Hud band internals)
All cursor tooltips (spell-slot hover `DrawSpellSlotTooltip`, object/belly/corpse/unit
tooltips) are drawn INSIDE `HUDRenderer.Draw`, which is called by **`HudChromeLayer`**
(`UI/Layers/HudLayers.cs`) — registered FIRST in the Hud band (`Game1` ctor, ~line 890),
so `SpellBarLayer`, `TimeControlsLayer` and **`AggressionBarLayer`** (registered after,
same band) all draw ON TOP of those tooltips. The aggro bar sits just above the spell bar,
so the spell-slot tooltip can render behind it. The **`UIBand.Tooltip` (=900) band is now
seated by `TooltipHostLayer`** (`UI/Layers/HudLayers.cs`, registered in the `Game1` ctor),
which drains the global `Game1.Tooltips` (`UI/TooltipSystem.cs`) request queue topmost after
every other layer + scissor clip — the canonical draw-only topmost tooltip service any
HUD/panel/editor code can `RequestText`/`RequestLines`/`RequestCustom` into. Note
`HudChromeLayer.Draw` parks `MousePos` off-screen when a
higher band owns the hover (so `_hoverSlotSpell` never gets set under a covering panel) —
any extracted tooltip layer keeps working because the hover *state* is still populated
during the Hud-band `DrawSpellBar`, and tooltip draws are no-ops when that state is null.

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
- `Necroking/UI/HUDRenderer.cs` → `DrawObjectTooltip(hoveredIdx, envSystem, sim, gameData,
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

## Programmer/debug hover cursor tooltips (name / type / state on hover)

There is **no reflection-based "programmer tooltip"** — the term is descriptive. The actual
"what is this object" hover readouts are the **cursor-tooltip family in
`Necroking/UI/HUDRenderer.cs`** (each builds a `string[]`/`List<string>` and calls the
shared `DrawCursorTooltip(lines, screenW, screenH)`):
- `DrawObjectTooltip` (env objects/buildings: name/id, `HP:`, `Owner:`, process state, pile contents),
  `DrawCorpseTooltip` (corpse name), `DrawBellyTooltip` (forager unit belly), and
  **`DrawUnitTooltip`** (units — this now exists).
- **Dispatch:** all four are called from `HUDRenderer.Draw` (the DrawHUD signature ~line 160-166,
  calls ~line 195-198). Hovered ids are threaded through `GameRenderer.Hud.cs` `DrawHUD`
  (~line 604) from the `_g._hovered*Idx` fields.
- **Line coloring:** `DrawCursorTooltip(string[], …)` → `Game1.Tooltips.RequestLines(params
  string[])` renders every line in the default color. To emit a **grey sub-line** (like a
  description), build a `List<(string, Color)>` and call the colored overload
  `RequestLines(IReadOnlyList<(string,Color)>)` instead — grey convention = `SpellTooltip.Dim`
  (`new(150,150,170)`). `DrawUnitTooltip` currently uses the plain `string[]` path (name / `HP:` /
  membership), so adding a colored line means switching it to the tuple overload.

### The shared spell-tooltip builder (`UI/SpellTooltip.cs`)
`Necroking/UI/SpellTooltip.cs` — `static SpellTooltip.BuildLines(SpellDef, GameData, sim,
casterIdx, inventory?)` = the ONE builder producing `List<(string Text, Color Color)>` for BOTH
the spell bar (`HUDRenderer.DrawSpellSlotTooltip`) and the grimoire. Color palette is on the
class: `Text`, `Dim` (grey sub-lines), `Header`, `Reached`, `Locked`. **The spell Description
line** (commit `49d2c0e`) is `lines.Add((sp.Description, Dim))` right after the name + school/
category kind line — this is the exact "grey tooltip line" to mirror for units. `SpellDef.Description`
= `[JsonPropertyName("description")]` string in `Data/Registries/SpellRegistry.cs`, declared right
below Name/ID/Category with `[EditorField(Order=3)]`.

**Look/edit here when:** mirroring the spell grey-description line onto another tooltip, or
changing spell tooltip colors/lines.

**Hovered indices are computed in `Necroking/Game1.cs` Update (~lines 3470-3541):**
`_hoveredObjectIdx`, `_hoveredCorpseIdx`, `_hoveredUnitIdx` (+ `_hoveredBellyUnitId` derived).
Each pick is individually gated on its `Settings.Tooltips.*` toggle and `!_input.MouseOverUI`.

**Editor-mode suppression (this is the gate to lift for map-editor hover-inspect):** the whole
hover-picking block lives inside `if (!_paused && !editorActive)` at **`Game1.cs` ~line 3097**,
where `editorActive` (~3093) is true for `MenuState.MapEditor` (and the other editors). So in
the map editor every `_hovered*Idx` stays `-1` and no cursor tooltip is ever built. To inspect
in the map editor you must run the hover-pick block when `_menuState == MenuState.MapEditor`
(e.g. move the pick block out of the `!editorActive` guard, or add a MapEditor-specific pick).
Second concern: the HUD (`DrawHUD`, `GameRenderer.Draw.cs` ~line 449) draws while
`showUI`, but the **map editor draws on top afterward** (`GameRenderer.Draw.cs` ~line 534,
`_mapEditor.Draw`), so tooltips would render *behind* the editor panel — draw them after the
editor, or only over the non-panel world area. `_input.MouseOverUI` is also set by the editor
panel, so a raw `!MouseOverUI` gate may already exclude the panel region (good) but you still
need z-order over the world.

**A separate "WORLD HOVER" debug readout** exists too: `GameRenderer.Hud.cs`
`DrawWorldHoverInfo` (bottom-left box: death-fog level, world pos, fog cell). Gated on
`Settings.Tooltips.ShowWorldHoverDebug` **and explicitly `if (_g._menuState != MenuState.None)
return;`** (~line 553) — its own editor suppression, independent of the pick-block gate above.

**Membership data for "in player horde / village / faction" (per unit):**
- Faction: `Unit.Faction` (`Necroking/Movement/UnitModel.cs`, enum `Faction { Undead, Human, Animal }`
  in `Data/Enums.cs`). "Player horde" = `Faction.Undead` **and** in the horde (below).
- Horde: `sim.Horde` (`Game/HordeSystem.cs`) → `IsInHorde(uint id)`; `GetUnitState(id)` for
  Following/Chasing/Returning if wanted. Only meaningful for `Faction.Undead`.
- Village: `Unit.VillageId` (short, -1 = none, `UnitModel.cs`) → `sim.Villages?.Get(villageId)?.Name`
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

## Panel open/close toggling & dock sides

Each panel owns its own `IsVisible` + `Toggle()`/`Close()`/`Hide()`; its `PanelLayer`
seat in the router reads that live (panels NO LONGER push/pop `Game1.Popups` — that
stack is editor-internal now). Per-side exclusivity is `Game1.CloseSameSidePanels(side,
opening)` with its panel→side table, called from all toggle entry points.

**Toggle entry points (all three must route through any new exclusivity logic):**
1. **Keyboard, `Necroking/Game1.cs` Update** (gated `_menuState == MenuState.None` +
   `!anyTextInputActive`): 'I' inventory (~3059), 'O' job board (~3067), 'Tab' character
   stats (~3075), 'K' skill book (~3079), 'J' grimoire (~3083), 'U' unit-info pin (~3090);
   'B' building + 'C' crafting live further down (~3621/3629) *with* the inline
   mutual-close (`if (_craftingMenu.IsVisible) _craftingMenu.Close();` etc.).
2. **Mouse menu buttons — `Necroking/GameRenderer.Hud.cs` `ToggleCoreMenu(idx, screenW,
   screenH)`** — the click-side mirror of the shortcuts, switch on `HUDRenderer.Menu*`
   index; contains a **second copy** of the building↔crafting mutual-close.
   `BuildMenuOpenMask()` right above it is the "which menus are open" bitmask for button
   highlighting.
3. **Dev commands — `Necroking/Game1.Dev.cs`** `ui …` verbs (~1900-1940) toggle
   inventory/charstats/skillbook/grimoire directly (no mutual-close).

**Which side each panel docks to (implicit in each panel's own position math — verified):**
- **Left:** `CraftingMenuUI` (`_screenX = -12`), `BuildingMenuUI` (`_screenX = -12`),
  `GrimoireOverlay` (`Layout`: `_x = 0`), `CharacterStatsUI` (`const AnchorX = 10`, spans
  three sub-panels rightward), `JobBoardUI` (`_x = 40` on open, draggable).
- **Right:** `UnitInfoPanel` (`_panelX = screenW - PanelW - 12`, set in `Draw`) — currently
  the only right-side panel.
- **Center:** `InventoryUI` (`(screenW - _widgetW)/2`, draggable), `SkillBookOverlay`
  (`_x = (sw - _w)/2`), `GraveRosterUI` (centered), `MultiplayerWindow` (pause menu).

**Close APIs are heterogeneous:** `Close()` on InventoryUI/CraftingMenuUI/BuildingMenuUI/
JobBoardUI/GraveRosterUI/CharacterStatsUI/SkillBookOverlay, `Hide()` on
GrimoireOverlay/UnitInfoPanel. Always close via the panel's own method — visibility is
the panel's own flag; the router reads it live.

**Look/edit here when:** adding per-side panel exclusivity ("one left + one right panel at
a time"), changing which key opens what, or the menu-button click behavior. Natural shape:
one helper in `Game1.cs` (e.g. `CloseSidePanels(side, except)`) with a hardcoded
panel→side table, called from all three entry points — replacing the duplicated
building↔crafting inline closes. Gotchas: `GrimoireOverlay.Hide()` clears assign mode
(`_onPick`) — force-closing it cancels a pending spellbar assignment (acceptable);
`UnitInfoPanel` transient auto-hover shows/hides every frame (`ShowForUnitTransient`, not
on the popup stack) — exclude transient shows from exclusivity or hover will slam other
right-side panels shut.

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

## MouseOverUI / UI hit-testing — who writes the flag

`InputState.MouseOverUI` (`Necroking/Core/InputState.cs`) is reset in `Capture()` at the
top of `Game1.Update`, then derived centrally in **`Game1.RebuildUIHitRects`**: the router
walks its layers top-down calling each visible layer's `AppendHitRects` into the
`UIHitRegistry` (`_uiHits`) — `ModalStackLayer` catalogues editor sub-popups, blocking
seats (`EditorHostLayer`, `MenuHostLayer`, skill book) add full-screen blankets, panels
add their `HitBounds`/`ContainsMouse` probe — plus the persistent HUD's fine-grained rects
via `HUDRenderer.AppendHitRects` (+ toast/aggro extras). `MouseOverUI = _uiHits.Hit(mx,my)`.
Remaining writers: **`InputState.ConsumeMouse()`** still sets it ("consumed" and "over UI"
deliberately collapse — see `Game1.WorldClicks.cs` header), and **editors** propagate their
one-frame-stale `_mouseOverEditorUI` (`EditorBase`, via `SetMouseOverUI()` from widget
draws) into the flag at the start of the next frame's `EditorBase.Update`.
Blocking blankets are why `Game1.HoverBlockedByUI` and the map-editor scroll-zoom gate
special-case via `MapEditorWindow.IsMouseOverPanel` + `_popups.IsEmpty` instead of
reading `MouseOverUI`.

**Stale-rect pattern:** several hit-tests use rects cached during the previous `Draw`
(`CharacterStatsUI._lastBoundsRect` for the 2-arg IModalLayer overload, SkillBook
`_tileRects`, EditorBase flag). The clean pattern is `GrimoireOverlay.Layout(screenH)` —
called in both Update and Draw so hit-test and pixels can never diverge.

**`ContainsMouse` implementers (all `_visible/IsVisible && rect`):** `InventoryUI`,
`CraftingMenuUI`, `BuildingMenuUI`, `TableCraftMenuUI` (all in `UI/`, widget-def rect
`_screenX/_screenY/_widgetW/_widgetH`; building/crafting inherit it from `SideListMenu`),
`GrimoireOverlay`, `SkillBookOverlay`,
`UnitInfoPanel`, `JobBoardUI`, `GraveRosterUI` (in `UI/`), `CharacterStatsUI` (in `Render/`,
two overloads), plus editor layers (`ColorPickerPopup`, `TextureFileBrowser` cached rect,
full-screen `EnvObjectEditorWindow`/`WallEditorWindow`). `UnitInfoPanel` in transient
auto-hover mode is deliberately NOT on the popup stack so it doesn't claim `MouseOverUI`
(the auto-hover block in `Game1.cs` checks its `ContainsMouse` explicitly instead).

**Look/edit here when:** a world click leaks through a panel (or a panel eats world
clicks), or hover picks are wrongly suppressed — start from the router walk
(`UIRouter.DispatchInput`) and the layer's band/`ContainsMouse`.

## Other panels in UI/ (brief)
- `UI/JobBoardUI.cs`, `UI/GraveRosterUI.cs` — worker economy (see [jobs-workers.md](jobs-workers.md)).
- `UI/GrimoireOverlay.cs` / `GrimoirePanel.cs`, `UI/SkillBookOverlay.cs` — spellbook / skills (Grimoire positioning above).
- `UI/RuntimeWidgetRenderer.cs` — JSON-widget layout/draw engine every panel uses.
- `UI/PopupManager.cs` — the EDITOR-INTERNAL sub-popup stack (`Game1.Popups`), seated in
  the router via `ModalStackLayer`. Game panels no longer use it.
- `UI/UIRouter.cs`, `UI/UILayer.cs`, `UI/Layers/` — the unified layer system (top section).
- `UI/StatTooltips.cs`, `UI/ResourceTooltip.cs`, `UI/RichTip.cs`, `UI/NineSlice.cs`,
  `UI/WidgetLayoutUtils.cs` — shared tooltip/layout helpers.
- Inventory/crafting/building menus live in `UI/` too: `UI/InventoryUI.cs`,
  `UI/BuildingMenuUI.cs`, `UI/CraftingMenuUI.cs`, `UI/TableCraftMenuUI.cs` (the first two
  side menus share `UI/SideListMenu.cs` — see next section).

## The inventory window (`InventoryUI`) + the slot Inventory model

**`Necroking/UI/InventoryUI.cs`** — the center-docked, draggable inventory window ('I').
Widget-based (`"EquipmentWindow"` widget, instance `"inventory"`); binds the slot model to
widget children.
- **Slot rects / hit-test**: `TryGetSlotIndexAt(mx, my, out slotIdx)` — the ONE public
  slot hit-test (also used by `TableCraftMenuUI`); both it and the hover highlight in
  `Draw` derive rects from `WidgetLayoutUtils.ComputeLayoutRects(def, _screenX, _screenY)`
  + `_slotChildIndices` (mapping slot i → widget child index, built in `Init`/
  `EnsureSlotChildren`). No cached rects — recomputed per call, so layout and clicks
  can't desync.
- **Slot input**: `Update(input)` — window drag first (title-bar rect = top 90px →
  `_dragging`/`IsDragging`), then `input.LeftPressed` over a filled slot fires
  `OnSlotClicked(slotIdx)`. The callback is wired in `Game1.EnsureInventoryUIsInitialized`
  (deposit into open craft table, else `TryConsumeInventoryItem`). There is a
  `// Dragging (future)` marker — item drag between slots does NOT exist yet, only
  window drag.
- **Router seat**: `PanelLayer` registered in the `Game1` ctor (~ln 999), Panels band,
  with drag provider `() => _inventoryUI.IsDragging` — while that returns true and
  LeftDown, `UIRouter.SetCapture` keeps ALL mouse input on this layer (`PanelLayer.cs`
  ~ln 85). **This is the existing drag→mouse-capture precedent** any in-panel item drag
  should reuse (make the provider also return true during an item drag).
- **Slot visuals**: `SyncSlots()` swaps each child between `"Item Slot"` /
  `"Item Slot_Empty"` widgets and sets quantity text + icon
  (`_renderer.SetImage(subId, "child_1", itemDef.Icon)`). A cursor "drag ghost" icon =
  `RuntimeWidgetRenderer.DrawIcon(iconPath, x, y, w, h)` drawn at the end of
  `InventoryUI.Draw` (Panels band draws above the Hud-band spellbar).
- Hover tooltip = `RichTip` deferred via `Game1.Tooltips.RequestCustom`.

**`Necroking/Game/Inventory.cs`** (`Necroking.GameSystems.Inventory`) — the slot data
model (`InventorySlot { ItemId, Quantity }`, fixed `SlotCount`, default 20; the player
instance is `Game1._inventory`). Mutation API: `AddItem` (stack-then-empty packing),
`RemoveItem`, `SetSlot` (verbatim write, save restore), **`MoveSlot(from, to)` — already
implements drag-reorder semantics: merges stacks of the same item (respecting MaxStack)
else swaps the two slots.** Also `HasRoomFor`, `FindSlot`, `GetItemCount`, `_everSeen`
(grow-only "has the player ever held this" set gating potion spells in the grimoire).

**Look/edit here when:** inventory slot click/drag behavior, slot rendering, reordering
items, dropping an inventory item onto another panel/the spellbar (start the drag in
`InventoryUI.Update`, resolve the drop on release using the target's hit-test, e.g.
`HUDRenderer.HitTestBarSlot`).

## Side-list menus — building placement & potion crafting (`SideListMenu`)

**`Necroking/UI/SideListMenu.cs`** — abstract base for the left-anchored (x=-12,
stretch-to-screen-height) "pick an item" side menus. Subclasses:
**`UI/BuildingMenuUI.cs`** (the build menu — env defs with `PlayerBuildable`; click arms
ghost placement, `TryPlace` from `Game1.WorldClicks.cs`; glyph-trap defs spawn a
`MagicGlyph` blueprint casting `TrapSpellId` instead of an env object) and
**`UI/CraftingMenuUI.cs`** (potions). `TableCraftMenuUI` is deliberately NOT a subclass.

- **The base owns the mechanics**: open/close/toggle, the widget child pool
  (`EnsureItemChildren`/`SyncItems` — clones the item template child N times at Open),
  `ComputeItemRects` (**the single copy of the item layout math** read by BOTH the click
  hit-test `HandleItemClick` and the overlay draw `DrawItemOverlays`, so they can't
  desync), hover/selected/can't-afford overlays, `IModalLayer` footprint. Subclasses own
  content: `ItemCount`/`BindItem`/`CanAfford`/`OnItemClicked`/`DrawItemExtras`.
- **`DrawItemOverlays()` returns the hovered item index** — the designed tooltip hook.
  `CraftingMenuUI.Draw` is the worked precedent: `int hoveredIdx = DrawItemOverlays();`
  then builds a `RichTip` and defers it via `Game1.Tooltips.RequestCustom` (topmost band).
  `BuildingMenuUI.DrawMenu` currently discards the return value (no tooltip yet).
- **Layout comes from the widget def** in `data/ui/widgets.json` (`"BuildingMenu"` panel +
  `"BuildingItem"` 218x68 row template; `"CraftingMenu"`/`"CraftingItem"`). The real layout
  engine is `WidgetLayoutUtils.ComputeLayoutRects` (shared by `RuntimeWidgetRenderer`
  drawing AND the UI editor) — it already supports `layout: "horizontal"` **with row
  wrapping** (wraps when `curX + cw > W - padR`). **Pitfall:** `SideListMenu.ComputeItemRects`
  hand-mirrors only the VERTICAL case — switching a menu widget to horizontal/grid layout
  without rewriting `ComputeItemRects` (best: delegate to `WidgetLayoutUtils.ComputeLayoutRects`
  and pick out `_itemChildIndices`) leaves clicks/hover on stale vertical positions while
  the pixels draw as a grid.
- Item icons bind by path: `_renderer.SetImage(subId, "Icon1", path)` (any image path;
  `RuntimeWidgetRenderer.DrawIcon(path, x, y, w, h)` is the code-driven equivalent, both
  via the shared texture cache). `EnvironmentObjectDef` has **no Icon field** — building
  art = `def.TexturePath` (the world sprite, non-square).

**Look/edit here when:** changing the build/crafting menu layout (list→grid), item row
binding/cost display, click-to-select/placement arming, or adding hover tooltips to rows.

**Pitfall — stale per-session refs after exit-to-main-menu → re-enter:** these UIs are
init'd ONCE per process (`Game1.EnsureInventoryUIsInitialized`, guarded by
`_inventoryUIsInitialized`, never reset) but their `Init` **captures per-session objects
into private fields**: `BuildingMenuUI.Init(_widgetRenderer, _envSystem, …, _sim.MagicGlyphs,
_gameData.Spells, _sim)` and `TableCraftMenuUI.Init(…, _envSystem, …, _sim.PlayerResources, …)`.
`_envSystem`/`_sim` on Game1 are forwarding properties onto `_session` (`GameSession`), and
`StartGame` does `_session.Dispose(); _session = new GameSession()` — `Dispose` calls
`Env.ClearDefs()` (DefCount→0, textures disposed). So after returning to the main menu and
re-entering, the build menu's captured `_envSystem` is the DEAD session's: `Open()` caches an
empty `_buildableDefIndices` → **empty build menu**; its `_sim`/`_glyphs` would mutate the dead
world. `_inventory`/`_items`/`_widgetRenderer` are app-lifetime — fine. Fix direction: don't
capture session-bound objects in one-shot-init'd UIs — read live via `Game1.Instance._envSystem`
/`_sim` (project convention "Direct over Inject"), or re-point the refs after `_session = new`
in `StartGame`. Closures over `this` (e.g. `GrimoireOverlay`'s predicate using `_sim.UnitsMut`)
re-evaluate the property per call and are safe.

## Menu screens (main/pause) & the Settings/Multiplayer/Save submenus

One class per full-screen menu in `UI/`: `MainMenuScreen.cs` (title screen), `PauseMenuScreen.cs`
(ESC menu), `ScenarioListScreen.cs`. (There is NO `LoadMenuScreen.cs` anymore — `MenuState.LoadMenu`
is drawn by `UI/LoadGameWindow.cs`, a `WantsClose` window like Save/Multiplayer, from `MenuHostLayer`.)
Each screen has a private `BuildLayout` that is
the single source of truth its `Draw` and `Update`/`HandleClick` both consume. Shared button
identities/style live in `UI/MenuCommon.cs`: `MenuButtonId` enum (ids are SHARED across screens —
e.g. `LoadGame`/`Quit`/`Settings` exist once), `MenuButton` struct, static `MenuDraw`
(`Button`/`Panel`/`Backdrop`/`Scrollbar`/`Text`).

**Two dispatch families — main menu vs pause menu differ:**
- **Main/scenario menus**: `Game1.Update` early-returns per state (`_mainMenu.Update()` etc.),
  and `GameRenderer.Draw` early-outs with its own SpriteBatch (`Materials.Hud.Begin`) —
  they bypass the UIRouter entirely.
- **Pause menu + its submenus** (`MenuState.PauseMenu`/`Settings`/`Multiplayer`/`SaveMenu`/`LoadMenu`):
  drawn by `MenuHostLayer` in `UI/Layers/HostLayers.cs` (Menu band, `Blocking`, `Closable`;
  `OnCancel` = ESC walk-back). Clicks: pause buttons via `_g._pauseMenu.HandleClick()` from
  Game1.Update's PauseMenu block; the submenus are `WantsClose`-style windows updated from
  Game1.Update state gates (`_settingsWindow.Update`, plus `_editorUi.UpdateInput` — the
  Settings window is EditorBase-widget based).

**Settings window open/close flow** (`Editor/SettingsWindow.cs`):
- Open = just `_g._menuState = MenuState.Settings` (PauseMenuScreen `MenuButtonId.Settings` case;
  dev verb `settings` in `Game1.Dev.cs` does the same). The window itself is app-lifetime —
  created/wired once in `Game1.LoadContent` (`SetGameData` with user-settings paths,
  `SetDayNightSystem`); it holds NO per-session refs (edits `_gameData.Settings` live,
  auto-saves debounced; `_dayNightSystem` is an app-level Game1 field).
- Close = TWO paths, both currently **hardcode return to PauseMenu**: the `WantsClose` block in
  `Game1.cs` Update ("Settings window close handling": resets flag, `_editorUi.ResetAllState()`,
  `_menuState = MenuState.PauseMenu`) and `MenuHostLayer.OnCancel` (ESC). To open Settings from
  another root menu, route the close through `Game1._backMenuState` — the field the load menu
  already uses (stamped `MainMenu`/`PauseMenu` every frame while on those screens;
  `LoadMenuScreen.Open`/`Update` is the precedent).
- Draw-frame housekeeping: `GameRenderer.Draw.cs` `DrawHudBlock` calls `_editorUi.EndDrawFrame()`
  when `_menuState == Settings` (skipping it causes the ghost-click replay bug — see
  [editor.md](editor.md)).
- With `_menuState == Settings` the FULL world pipeline runs (no early-out): fine over a paused
  world; from the main menu the backdrop is the empty `GameSession` world (exists from app start),
  not the menu backdrop — cosmetic, consider `MenuDraw.Backdrop` in `MenuHostLayer.Draw` when
  `!_gameWorldLoaded`.

### Modal background-dim census (who darkens the screen behind them)

- **Menu family — ONE shared dim, drawn centrally:** `MenuHostLayer.Draw` (`UI/Layers/HostLayers.cs`)
  calls `MenuDraw.ModalDim` before its switch, covering PauseMenu/Settings/Multiplayer/SaveMenu/
  LoadMenu uniformly. Shared color = `UI/MenuCommon.cs` `MenuDraw.ModalDimColor` (0,0,0,180). Skipped
  when `!_gameWorldLoaded` (Settings/LoadMenu then draw the opaque `Backdrop` instead) and on game
  over (own dim). Individual menu windows must NOT draw their own fullscreen rect — stacked alphas
  double-darken. (Per-window rects + the `PauseDimBackground` setting/"Dim Background on Pause"
  checkbox removed 2026-07-16; the dim is always on.)
- **Self-dimmers outside the Menu band (draw their own):** `UI/SkillBookOverlay.cs` `Draw` (Overlay
  band; uses `MenuDraw.ModalDimColor`). Full-screen editors each dim too, with drifting alphas:
  `Editor/UIEditorWindow.cs` (180), `ItemEditorWindow` (180), `SpellEditorWindow` (180; sub-popups
  100), `WallEditorWindow` (180), `UnitEditorWindow` (160; sub-popups 120), `EnvObjectEditorWindow`
  (150), `ColorPickerPopup` (160), `WadingEditorPopup` (150), `EditorBase.DrawConfirmDialog` (150).
  Game over = `GameRenderer.Hud.cs` `DrawGameOver` (160).
- **Opaque menu backdrop:** `UI/MenuCommon.cs` `MenuDraw.Backdrop` (bg image cover-scaled + alpha-120
  dim) — main menu / scenario list, and drawn by `MenuHostLayer` under Settings/LoadMenu when
  `!_gameWorldLoaded`.

**Layout pitfall (main menu):** `MainMenuScreen.BuildLayout` stacks 55px buttons + 12px gaps +
25px group gaps starting at `screenH/2 - 75`; the comment warns the stack must fit at 720p and
it is already tight — adding a button means shrinking heights/gaps or raising the start Y.

## Corner toasts — the bottom-right skill-learn toast stack

The only toast/notification system in the game is currently **skill-specific**, split
across three files (a generalization would consolidate the model into its own `UI/` class,
mirroring the `TooltipSystem`/`Game1.Tooltips` "Game1-owned queue + dedicated layer" shape
— but note tooltips are frame-scoped, toasts persist across frames):

- **Model + queue — `Necroking/Game1.cs`**: struct `SkillLearnToast { Header, SkillName,
  SkillId, Timer, Duration }` + `internal readonly List<SkillLearnToast> _skillLearnToasts`
  (fields near the top of the class). Cleared in the StartGame per-game reset block
  (next to `_skillBookState.InitFromDefs()`).
- **Who pushes**: `Game1.TryAutoLearn(skillId, header)` (adds a toast when `LearnFree`
  succeeds; callers = `Game1.Crafting.cs` "Recipe Learned" + dev verb `learn_skill` in
  `Game1.Dev.cs`) and `Game1.TryConsumeInventoryItem` ("Skill Points" toast with **empty
  SkillId** — click then just opens the book). Manual learns via the skill-book UI
  (`SkillBookState.TryLearn`) deliberately do NOT toast.
- **Logic/draw — `Necroking/GameRenderer.Hud.cs`** (all on `GameRenderer`):
  `GetSkillLearnToastRect(sw,sh,stackIndex)` = the ONE geometry source (280x56, padR/padB
  16, gap 6, stacked up from bottom-right) shared by draw, hit-test, and registry;
  `AppendSkillToastHitRects` (ids `toast.skill_learn.{i}`, called from
  `Game1.RebuildUIHitRects` under `if (HudVisible)` — this is what makes `MouseOverUI`
  true over a toast); `SkillToastIndexAt` (click hit-test; list is drawn newest-last-added
  at the BOTTOM rect, so list idx i ↔ stack slot `Count-1-i`); `ActivateSkillToast`
  (click action: `SkillBookDefs.FindTabIndexFor(SkillId)` → `_skillBookOverlay.Open()` +
  `SetActiveTab`, then remove); `UpdateSkillLearnToasts(dt)` (expiry — ticked from
  `Game1.Animation.cs` `UpdateAnimations` on **`_clock.WorldDt`**, so toasts FREEZE while
  paused / in editors); `DrawSkillLearnToasts` (slide-in first 10% of life, fade-out last
  15%, grimoire leather/gold palette, `DrawTextRounded` int-rounds positions,
  `SanitizeAscii` for the ASCII-only SpriteFont).
- **Router seat — `SkillToastLayer` in `Necroking/UI/Layers/HudLayers.cs`**: `Band =
  UIBand.Toast` (500, above HudTop 450 so a toast overlapping the button rows wins the
  click); registered in the `Game1` ctor right after `MinimapLayer`. Input gate `Visible =
  HudVisible && _menuState == None && count > 0`; draw gate `VisibleForDraw =
  ShowUIForDraw` (draw self-guards on count) — input and draw gates differ. Its own
  `AppendHitRects` is deliberately EMPTY (rects catalogued centrally in
  `RebuildUIHitRects`); `ContainsMouse`/`OnPointer`/`Draw` all delegate to the
  `GameRenderer` methods above.

**Look/edit here when:** changing toast look/duration/stacking, adding a new toast
trigger, or generalizing into a game-wide notification system (keep: one geometry
function, the hit-registry append, the per-game clear, the Toast band seat).

## The minimap (top-right, fog-of-war aware, editor-interactive)

**`Necroking/UI/MinimapHUD.cs`** — class `MinimapHUD` (`Game1._minimap`, init'd in
`LoadContent`). 192px map of a **centered window** of the world (NOT the whole map):
`DesiredWindow` = `ViewRange` (384) world units centered on the necromancer in normal
play, or on `map_center` (public `Vec2`, follows the free camera / minimap drags) in the
map editor; clamped to the map. The baked window is `_winX/_winY/_winW/_winH` (private;
`baked_map_center` exposes its center).
- **Size — per-mode**: static `CurrentSize` (=`EditorSize` 288 in the map editor, else
  `BaseSize` 192) is the ONE size accessor and it is **doubly loaded** — it is both the
  on-screen rect size (`Bounds` width/height) AND the texture resolution (`_terrainTex`/
  `_fogTex`; the `Bake`/`RefreshFogTexture` pixel loops iterate the baked `_texSize`).
  `Draw` rebakes when `_terrainTex.Width != CurrentSize`, so mode switches re-alloc
  correctly. `Bottom = Top + BaseSize` is deliberately base-size (its only consumer,
  horde caps, never shows in the editor).
- **Position**: static `Bounds(screenW)` — the ONE placement source draw, hit rect, and
  click mapping all derive from. Already **per-mode**: normal play = top-right
  `(screenW - RightMargin(8) - CurrentSize, Top(66))`; map editor = docked left of the
  editor panel, `x = MapEditorWindow.PanelLeftX(screenW) - EditorPanelGap(8) - CurrentSize`.
  Consts `Top`/`Bottom` are also read by `HUDRenderer.DrawHordeCaps` (horde caps anchor
  below it in normal play).
- **Draw**: `Bake` (terrain texture from `GroundSystem` vertex map + darkened natural
  obstacles; rebaked on drift/`RebakeFrames`/session change), `RefreshFogTexture` (fog
  overlay), `DrawBuildingMarkers`/`DrawUnitMarkers` (live, fog-gated for non-undead),
  `DrawCameraViewport` (outline of the camera's world rect). Fog is editor-aware:
  `fogOn = fog.Mode != Off && _menuState != MenuState.MapEditor` (mirrors the world
  pass skip — the old "fogs in the editor" gotcha is fixed).
  All draws go through `Scope` (straight-alpha TINT constants — see the draw-surface
  convention at the top of this file); the `FogUnexplored`/`FogExplored` constants are
  **`SetData` TEXEL data** and stay premult-encoded by convention, same as loaded PNGs
  — don't "fix" them to straight alpha.
- **Per-faction marker colors** — the canonical palette is **`Render/FactionColors.cs`**
  (shared single source with the map editor's placed-unit labels); `MinimapHUD.cs` keeps
  private `UndeadColor`/`HumanColor`/`AnimalColor`/`PlayerColor` aliases of it for terse
  call sites. `DrawUnitMarkers` switches on the `Unit.Faction` **enum** (`Data/Enums.cs`);
  `DrawBuildingMarkers` reuses Undead/Human. Change a color in FactionColors, not here.
- **Screen→world**: public `TryScreenToWorld(mx,my,screenW,out world)` (bounds-checked)
  and `TryScreenToWorldNoBoundsCheck` — the inverse marker mapping against the baked
  window; both used by `MinimapLayer` drags.
- **Router seat + input**: `MinimapLayer` in `UI/Layers/HudLayers.cs` — HudTop band.
  `Visible => HudVisible && _menuState == MenuState.MapEditor` (so it takes input ONLY in
  the map editor; in normal play it stays draw-only because the follow-cam would lerp a
  click-jump straight back); `VisibleForDraw => _gameWorldLoaded && ShowUIForDraw` (draws
  in both modes). `ContainsMouse`/`AppendHitRects` use `MinimapHUD.Bounds`. Editor click
  controls (`OnPointer`/`OnFrame`): left press+drag = move camera (`_g._camera.Position =
  world`); right press+drag = move camera AND recenter the minimap window (`map_center`),
  panning the view. Its id `"hud.minimap"` matters: `Game1.RebuildUIHitRects` sets the map
  editor's `OverGameplayHud` from `_uiHits.Hit(mx,my,"hud.")`, which is what stops the
  editor's immediate-mode painting under it; `MapEditorHostLayer.OnFrame`
  (`UI/Layers/HostLayers.cs`) also excludes `MinimapHUD.Bounds` from camera scroll-zoom.
- **Camera jump**: `Camera25D.Position` (public `Vec2`, the world point at screen center)
  is written directly — no smoothing. Safe in map-editor mode; in normal play the
  follow-cam (`Game1.cs` Update, `necroIdx >= 0 && !_devFreeCamera` branch) lerps back to
  the necromancer every frame, so a minimap jump there gets undone.
- **Draw-order trap — world labels over the minimap**: `MinimapLayer` draws at HudTop
  (450), but the map editor host layer draws at Editor (700), i.e. LATER in the router's
  bottom-up draw walk — so anything `MapEditorWindow.Draw` paints (notably
  `DrawPlacedUnitMarkers` unit-name labels) lands ON TOP of the minimap. Suppress by
  rect-testing against `MinimapHUD.Bounds(screenW)` (the exclusion precedent is
  `MapEditorHostLayer.OnFrame` in `UI/Layers/HostLayers.cs`, which excludes `Bounds` from
  camera scroll-zoom). The Alt-key name labels (`GameRenderer.Draw.cs`
  `DrawAltNameLabels`, "AltLabels" CustomPass) run in the post phase BEFORE the Hud
  phase, so in normal play the opaque minimap already covers them — no suppress needed
  there.

**Look/edit here when:** moving/resizing the minimap (incl. per-mode size/placement —
`CurrentSize`/`Bounds` and the bake/fog loops in `MinimapHUD.cs`), minimap markers/colors,
minimap fog behavior, changing the editor click/drag controls (`MinimapLayer`), or
something (editor world labels) draws on top of the minimap.

## Cross-links
- [anti-patterns-ui.md](anti-patterns-ui.md) — the recurring UI/editor/input traps as don't-do
  anti patterns (scroll/layout math, stale-session refs, input-gesture & router ownership,
  hit-test math, hotkeys, tooltip z-order/clipping, transient-vs-persisted settings). Read
  before UI/input work.
- [corpses.md](corpses.md) — corpse data model, pile gather/withdraw (`TryTakeCorpseFromPile`,
  `FindCorpsePileUnderCursor` in `Game1.cs`).
- [world.md](world.md) — env objects, foragables, boar belly spit-on-death.
- [jobs-workers.md](jobs-workers.md) — `WorkerSystem` stockpiles behind pile contents.
- [render.md](render.md) — HUD/effects rendering.
