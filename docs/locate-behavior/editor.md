# Editor/ — in-game immediate-mode editors

`Necroking/Editor/` holds the debug/authoring editors (unit, spell, item, map, wall,
env-object, settings, and the **UI/widget editor**). All extend `EditorBase` (immediate-mode
DrawButton/DrawText/DrawCombo helpers, text rounding). Each `*EditorWindow.cs` owns its own
working-copy data model, undo stack, and JSON load/save.

This doc covers (a) the **shared editable-text-field / focus mechanism** in `EditorBase`
(reused by every editor), and (b) the **UI/widget editor** in depth (the data-driven
`data/ui/widgets.json` editor). Other editors are stubs here — expand when needed.

## Shared editable-text-field & focus mechanism (`Editor/EditorBase.cs`)

All editors share ONE focused-input mechanism on the `EditorBase` instance. This is an
**immediate-mode, return-the-value** design (no per-field model binding), which is the
source of the "typed text bleeds into the next selected object" bug class.

### Focus / buffer state (single-valued, on the EditorBase instance)
- `protected string? _activeFieldId` — the *only* focus token. A **per-panel-slot string
  id** (e.g. `"unit_id"`, `"envdef_name"`, `"ns_id"`), NOT tied to the selected object's
  identity. Suffix conventions: `_combo` = dropdown, `_textarea` = multi-line.
- `private string _inputBuffer` — the single edit buffer for whichever field is active.
- Cursor/selection: `_cursorPos`, `_selectionStart`, `_selectAll`, `_draggingSelection`,
  `_activeFieldInputX/W`; text-area: `_textAreaCursorPos`, `_textAreaScrollOffset`.
- Helpers: `IsFieldActive(fieldId)`, `IsTextInputActive`, `ClearActiveField()` (just
  `_activeFieldId = null`), `IsDropdownOpen`.

### The field widget: `DrawTextField(fieldId, label, value, x, y, w) -> string`
(Siblings: `DrawIntField`, `DrawFloatField`, `DrawTextArea`, `DrawSearchField`, `DrawCombo`,
the vec2/area variants — all follow the identical activate/return pattern.)
- **Activate** (first click while `!isActive`): sets `_activeFieldId = fieldId`,
  **captures `_inputBuffer = value` once**, select-all. `value` is read into the buffer
  ONLY on this transition.
- **While active**: draws and returns `_inputBuffer` — it **never re-reads `value`**.
  `return isActive ? _inputBuffer : value;`
- **Commit is implicit / caller-driven**: the function returns `_inputBuffer` every frame
  it's active; the *caller* writes it back (`if (newId != def.Id) def.Id = newId;`). There
  is no explicit "commit to model" step inside `DrawTextField`.
- **Deactivation paths that end editing:** Enter/Tab/Escape in `HandleTextInput` (sets
  `_activeFieldId = null`, returns), or a click **outside** the field's rect
  (`... && !hovered` branch sets `_activeFieldId = null`). Escape does NOT restore the old
  value — the buffer was already being returned each frame, so the last returned value
  stuck unless the caller only commits on blur.

### Keyboard: `HandleTextInput(gameTime)`
Called from the update tick only when `_activeFieldId != null` (see the `_activeFieldId !=
null → HandleTextInput` line in the main update). Edits `_inputBuffer` in place; Enter/Tab
close single-line fields, Escape closes, key-repeat via `_keyRepeatTimer`/`_lastRepeatingKey`.
**Text-field clipboard (commit 9d3c626) lives here**: Ctrl+A/C/X/V on the single-line
buffer via `TextCopy.ClipboardService`; other Ctrl+letter chords are swallowed. This only
runs while a field is active — editors gate their own global chords on
`EditorBase.IsKeyboardCaptured` (= `IsTextInputActive || IsDropdownOpen ||
_colorPicker.IsEditingText`) so object-level Ctrl+C/V shortcuts never collide with it.

### Selection change — the bug site (per-editor, e.g. `UnitEditorWindow.cs`)
Each editor owns its own selection index and list; `EditorBase.DrawScrollableList(panelId,
items, selectedIdx, …)` just **returns the clicked index**. The editor reacts by swapping
which `def` it passes to the property panel — e.g. `UnitEditorWindow` sets
`_selectedIdx = IndexOf(...)` in its `DrawScrollableList` handler. **Nothing clears
`_activeFieldId` or `_inputBuffer` on this change.**

**Root cause of the clobber:** field ids are static per-slot strings, so after a selection
change the property panel next frame calls e.g. `DrawTextField("unit_id", …, newDef.Id, …)`.
`_activeFieldId` is still `"unit_id"` (still "active") and `_inputBuffer` still holds the
text typed for the OLD object — so `DrawTextField` returns the stale buffer and the caller
(`if (newId != newDef.Id) newDef.Id = newId;`) writes it into the NEW object. The buffer is
bound to the *slot id*, not the *object*, and no flush happens on selection change.

### Single-point-of-fix candidates
- **Flush-on-selection-change:** call `ClearActiveField()` (and ideally reset `_inputBuffer`)
  wherever an editor changes its selection index — but that's N call sites (every editor's
  list handler), so it's fragile as a shared fix.
- **Bind buffer to field identity (preferred, one place):** in `DrawTextField` (and the
  sibling field widgets), when active, detect that the incoming `value` no longer matches the
  value the buffer was captured from — i.e. the underlying model changed under an active
  field — and **abandon** the buffer (re-capture from the new `value` or deactivate) rather
  than returning the stale buffer. Requires storing the "captured-from" value (or an owner
  token) alongside `_inputBuffer`. This fixes every editor at once in `EditorBase`.
- **Owner token:** make `_activeFieldId` include the selected object's identity (or store a
  parallel `_activeFieldOwner`), so a selection change makes `IsFieldActive` false for the
  new object and the field falls back to returning `value`.

### Look / edit here when…
- **"typed text in a field applies to the next selected object / bleeds across selection"**
  → `EditorBase.DrawTextField` (activate/return + the `_inputBuffer`/`_activeFieldId` state)
  and the per-editor selection handler (e.g. `UnitEditorWindow.DrawScrollableList` block).
- **"a field won't commit / commits on wrong event"** → `HandleTextInput` (Enter/Tab/Esc)
  and the caller's `if (new != old) model = new;` write-back pattern.
- **Adding a new editable field type** → add a `DrawXField(fieldId, label, value, …)` in
  `EditorBase` following the activate-once / return-`_inputBuffer`-while-active contract.
- **Tab / Shift+Tab focus cycling between fields** → no field-order registry exists (as of
  2026-07); everything needed is in `EditorBase.cs`. `FieldCore(fieldId, display, inputRect)`
  is the single per-frame chokepoint every single-line field draws through
  (`DrawTextField`/`DrawIntField`/`DrawFloatField`/`DrawSearchField`/the slider value box) —
  record each drawn `fieldId` (+rect) there into an ordered per-frame list; promote/clear it
  in `EndDrawFrame` (the `_openComboDrawnThisFrame` reset is the precedent; `EndDrawFrame` is
  menuState-gated in `GameRenderer.Draw.cs` but runs every frame an editor is open, which is
  the only time fields exist). The Tab keypress lands in `HandleTextInput`'s
  `key == Keys.Enter || Keys.Tab` deactivate branch (`shift` bool already computed there);
  to move focus, set a pending-focus field id that `FieldCore` consumes on the next Draw by
  calling `FocusTextField` (the one activation helper — it already does select-all +
  `_textFieldLayer.Panel`), rather than activating from stale rects in Update. Commit needs
  no extra work: callers write the returned buffer back every active frame, so Tab-away
  commits exactly like Enter does today. Textareas (`_textarea` suffix) have a separate
  Tab-closes branch and their own activation path (not `FocusTextField`) — separate decision.

## Map editor (`Editor/MapEditorWindow.cs`) — tabs, object brushes, map save

One ~6400-line file; single partial-free class. Structure to know when **adding a tab**:

- **`MapEditorTab` enum** (top of file: `Ground, Grass, Objects, Walls, Roads, Regions,
  Triggers, Units, Zones`) + `ActiveTab` field.
- **Per-tab plumbing that must all be extended together** (add a case/slot in each):
  `TabRow1`/`TabRow2` static string arrays (two-row tab bar; widths = `PanelWidth / row.Length`),
  `_tabScroll = new float[9]` (size = tab count), keyboard `1-9` switch loop (`for (int i = 0;
  i < 9; …) ActiveTab = (MapEditorTab)i`), click hit-test in `Update` (maps row/col →
  `(MapEditorTab)(idx + TabRow1.Length)` for row 2), the `switch (ActiveTab)` in `Update`
  (→ `Update<X>Tab`), the `switch` in `Draw` (→ `Draw<X>Tab`), and
  `DrawWorldOverlaysForActiveTab` (world-space brush cursor / zone overlays). Also the
  `canAdjustBrush` gate (Q/E, `[`/`]` adjust `BrushRadius`, clamp 0..20) if the new tab brushes.
- **Objects tab = the env-object brush precedent.** `UpdateObjectsTab`: category buttons
  (`GetEnvCategories()` = distinct `def.Category` + "All" + "Groups"), def list
  (`GetFilteredEnvDefs`), Single vs Paint mode (`_objectPaintMode`, 'B' hotkey). Painting:
  `PaintObjectsBatch` runs **every frame while leftDown** — hex-grid offsets over
  `BrushRadius` (world units, circle test `dx²+dy²≤r²`), jitter, weighted-random def pick
  (`GetSelectedGroupMembers` — env-def `Group`/`GroupWeight` = the existing "set of defs with
  weights" concept), spacing rejection via `_envSystem.CanPlaceObject(def, x, y)`
  (collisionRadius·scale + placementRadius vs every live object), then
  `_envSystem.AddObject((ushort)def, x, y, GetRandomPlacementScale(def))`. **Perf pattern**:
  null out `_envSystem.OnCollisionsDirty` around the loop (else per-object pathfinder
  rebuild) and call `_envSystem.StampObjectCollisionAt(_tileGrid, newIdx)` per placement.
  **Undo**: accumulate into `_batchPlacedObjects`, push `UndoObjectBatchPlace` on mouse-up
  (`UndoObjectBatchRemove` for the right-drag eraser). `AutoCreateTriggerInstance(newIdx)`
  after every add (RM06).
- **World-space overlays (brush cursor, collision ellipses, zone rects — and any placement
  ghost)** draw via `DrawWorldOverlaysForActiveTab(screenW, screenH)`, called at the **top of
  `Draw()`** — deliberately BEFORE the panel background and OUTSIDE the tab scissor clip
  (overlays put inside a `Draw<X>Tab` get clipped to the panel rect). It computes `overPanel`
  itself and switches on `ActiveTab` (Objects case: paint-mode `DrawBrushCursor` +
  `_showCollisions` → `DrawCollisionOverlay`). World→screen inside the editor =
  `_camera.WorldToScreen(new Vec2(x, y), 0f, screenW, screenH)`; mouse→world =
  `_camera.ScreenToWorld(...)`. Textured draws use `Scope` (`Render.SpriteScope`, the full
  `Draw(tex, pos, srcRect, tint, rot, origin, scale, fx, depth)` overload).
- **Placement ghost precedent (runtime building menu):** `UI/BuildingMenuUI.cs`
  `DrawGhostPreview` — semi-transparent def sprite at the cursor, tinted by
  `_envSystem.CanPlaceObject`: `worldH = def.SpriteWorldHeight * def.Scale`, `scale = worldH *
  camera.Zoom / tex.Height`, `origin = (PivotX*texW, PivotY*texH)`, tint alpha ~76, plus a
  `PlacementRadius` circle via `Render.DrawUtils.DrawCircleOutline`. Called from `Game1.cs`
  (~line 968) during world draw. Copy this math for an editor-side ghost; note it ignores
  animated-sheet source rects (world draw in `GameRenderer.Units.cs` `DrawSingleObject` uses
  `def.GetAnimFrameRect` when `def.IsAnimated`) and the editor's final placement scale is
  `GetRandomPlacementScale` (`ScaleMin..ScaleMax`), which a ghost can't predict exactly.
- **Editor-wide keyboard shortcuts** (Ctrl+S save / Ctrl+L load / Ctrl+Z undo) sit in one
  block in `Update()` right after `bool ctrlDown = …`, each gated on `!textEditing`
  (`textEditing = _eb.IsKeyboardCaptured`). New global/per-tab chords go here — key edges
  via `kb.IsKeyDown(K) && _prevKb.IsKeyUp(K)`.
- **Bottom bar (panel footer: filename field + Save/Load/Undo buttons + shortcuts hint +
  status line).** Draw lives at the end of `Draw(screenW, screenH)` ("Bottom bar:" comment):
  `bottomH = 90`, `bottomY = panelY + panelH - bottomH`, then a `DrawTextField("map_filename",…)`
  row, a 3-button row via `DrawButtonRect` (draw-only, no hit-test), a `DrawSmallText`
  shortcuts hint, and the fading `_statusMessage`. **Clicks are NOT handled in Draw** —
  `UpdateBottomBarClicks(leftClick, mouse, panelX, panelY, screenH)` (called from `Update`)
  edge-detects the button row by recomputing `buttonRowY = panelY + panelH - bottomH + 2 +
  FieldHeight + 2` and splitting X into three `btnW3` bands → `SaveMap()` / `LoadMap()` /
  `PerformUndo()`. **The bar height constant lives in FOUR places that must change together
  when the bar grows:** the content-area reserve `contentH = … - 92` in `Draw`, `bottomH = 90`
  in `Draw`, `bottomH = 90` in `UpdateBottomBarClicks`, and the click-ownership guard
  `bottomBarTop = panelY + (screenH - 20) - 92` in `Update` (the block that zeroes `leftClick`
  for clicks on the tab rows / bottom bar so scrolled tab content underneath never sees them).
  Ctrl+S / Ctrl+L in the `Update` shortcut block call the same `SaveMap`/`LoadMap`.
- **Panel-button draw/click split census (the "buttons diverge" bug class).** Three click
  mechanisms coexist in `MapEditorWindow.cs`:
  1. **`_eb.Draw*` EditorBase widgets** (`DrawButton`/`DrawCombo`/`DrawCheckbox`/`DrawTextField`,
     60+ call sites) — fused draw+hit-test during `Draw`, fire on release via
     `InputState.PressStartPos`/`DrawPrevMouse`. The **Zones tab is almost entirely this**
     (incl. "Delete Zone" mutating the list mid-Draw — the proven in-file precedent that
     panel-local actions may run during Draw). `EditorBase.DrawButton(text,x,y,w,h,bg,layer)`
     + clip-aware `HitTest(rect)` are the shared primitives.
  2. **Draw-only `DrawButtonRect(text,x,y,w,h,bg)`** (private, bottom of file) **+ a
     hand-rolled y-math hit-test in the matching `Update<X>Tab`** — THE divergence source.
     Draw and Update each own a parallel copy of the layout formula that must agree
     byte-for-byte, e.g. `DrawGroundTab` receives `contentY` and advances it via
     `DrawSectionHeader(ref contentY, …)` while `UpdateGroundTab` independently recomputes
     `contentY = panelY + TabRowHeight*2 + HeaderHeight + 6`. Sites: Ground/Grass
     "+ Add Type"/"Delete", Roads/Regions/Triggers "+ Add"/"Delete"/place-mode toggles,
     bottom-bar Save/Load/Undo/Reload (`UpdateBottomBarClicks`). The comment at the
     bottom-bar draw ("doing it here would compare against `_prevMouse` after it was already
     overwritten") refers to MapEditorWindow's OWN raw-MouseState edge detection — it does
     NOT apply to `_eb.DrawButton`, which uses InputState's Draw-frame snapshot.
  3. **Cached-layout instance fields** (Units/Objects thumbnail grids: `_unitListDrawY` etc.
     written in Draw, hit-tested in next frame's Update — one-frame-stale by design).
  **Refactoring a tab to single-source layout:** make a pure per-tab layout step (method
  returning a struct of named `Rectangle`s, or convert to `_eb.DrawButton`) computed from
  `(panelX, contentY, contentH, _tabScroll[tab], selection)` and consumed by BOTH
  `Draw<X>Tab` and `Update<X>Tab` — geometry is cheap, call it twice per frame (avoids the
  stale-field pattern). Precedents for the layout-struct shape: `UI/MainMenuScreen.cs` /
  `PauseMenuScreen.cs` / `ScenarioListScreen.cs` private `BuildLayout`, and
  `UI/HUDRenderer.cs` `TimeControlLayout` (one formula shared by draw + hit-test + hit
  rects). Keep Update-side actions honoring the shared gates (`leftClick` pre-zeroed for
  tab-row/bottom-bar clicks, `overPanel`, popup blocking); actions that flip `_menuState` or
  rebuild the world (Load/Reload) should stay in Update, not fire mid-Draw. it clears + re-reads
  ground types, env defs/objects, walls, grass, triggers, roads, zones and `_placedUnits`
  from `assets/maps/<_mapFilename>.json` (+ sidecars) and clears `_undoStack` — but never
  touches `_sim` (no unit respawn, no player move, no `StartGame`). A "reload the world as a
  new game" action must go through `Game1.StartGame` + the save/load state pipeline instead
  (see [save-load.md](save-load.md) "In-memory split & the load-time side-effect census").
- **Save**: `SaveMap()` writes `assets/maps/<map>.json` with `Utf8JsonWriter` — env objects
  go in the `placedObjects` array (`defId, x, y, scale, seed`) straight from
  `_envSystem.Objects`, so anything placed via `AddObject` is persisted automatically —
  **including gameplay-spawned objects** (zone spawns, village structures, boar drops,
  player buildings), which then re-load AND re-spawn next run = compounding map growth.
  See [world.md](world.md) "Runtime env-object spawn census" for the call-site list and
  where a don't-save flag goes. Units are immune: `placedUnits` is written from the
  editor's own `_placedUnits` list only, never from live sim units. Env *defs* save
  separately via `MapData.SaveEnvDefs("data/env_defs.json")`; zones to the `SaveZones`
  sidecar (see [zones.md](zones.md)).
- **Units tab = the placeable-unit picker + world place/delete.** All in
  `MapEditorWindow.cs`: `DrawUnitsTab` (faction-filter combo → patrol combo → place-as-corpse
  checkbox → the scrolling **text list** of unit defs from `GetFilteredUnitIds()` →
  placed-count + Clear All), `UpdateUnitsTab` (panel click → `_selectedUnitDefIdx`; world
  left-click → append `PlacedUnit` + `UndoUnitPlace`; right-drag sweep-delete →
  `UndoUnitRemove`). **Draw/Update split via cached layout fields**: `DrawUnitsTab` stores
  `_unitListDrawY`/`_unitListItemH`/`_unitListVisibleCount`; `UpdateUnitsTab` hit-tests with
  those (one-frame-stale by design — change the layout in Draw and Update follows next frame,
  but both must agree on geometry). Scroll = the shared `_tabScroll[(int)MapEditorTab.Units]`
  (wheel handled generically in `Update`; `DrawUnitsTab` clamps the max itself since the
  generic handler only clamps at 0). **`_selectedUnitDefIdx` indexes the FILTERED list** —
  reset it to -1 whenever the filter changes (the faction-combo handler already does).
  `DrawPlacedUnitMarkers` = the always-on world markers + unit-name labels over every
  placed unit (faction-colored diamond / corpse cross + `Units.NameOf` text at
  `WorldToScreen(pu) + (8,-6)`, no sprites). Called near the top of `MapEditorWindow.Draw`
  deliberately BEFORE the panel background so the panel occludes markers behind it — but
  the minimap draws EARLIER still (`MinimapLayer`, HudTop band 450, vs the editor layer's
  Editor band 700), so these labels paint over the minimap unless rect-suppressed against
  `MinimapHUD.Bounds(screenW)` (see [ui.md](ui.md) "Draw-order trap").
- **Drawing a unit sprite thumbnail in editor UI** (the unit-editor preview pattern,
  `UnitEditorWindow.DrawPreviewSprite`): `def.Sprite` (`SpriteRef {AtlasName, SpriteName}`)
  → `AtlasDefs.ResolveAtlasName` → `Game1._atlases[idx]` (internal, reachable via the
  editor's `_game`) → `atlas.GetUnit(name)` = `UnitSpriteData`; **`UnitDef.SpriteData` is
  already pre-resolved at load** (`Game1.LoadContent` sets it for every def), so for a
  static thumbnail: `def.SpriteData.GetAnim("Idle")` → `AngleFrames` (prefer angle 60 =
  down-right, fall back to any) → first `Keyframe.Frame` (`SpriteFrame {Rect, PivotX/Y,
  TextureIndex}`) → `atlas.GetTextureForFrame(frame)` → `_eb.DrawTexture(tex, pos,
  frame.Rect, …)`. Only the atlas texture lookup still needs the atlas object; the anim
  data does not. No AnimController needed for a static frame.
- **No editor grid-of-image-thumbnails picker exists yet** (as of 2026-07): the nearest
  precedents are UIEditorWindow's RI15 inline 18px texture thumbnails in a *list*, the wall
  editor's 3x3 *button* grid, and `ReflectionPropertyRenderer.DrawCheckboxGridField`
  (text checkbox grid). `TextureFileBrowser` is a file list + single preview, not a grid.
- **Hover-tooltip helper (canonical, use this — do NOT hand-roll):** `Game1.Tooltips`
  (static `UI/TooltipSystem.cs`, public field on `Game1`) is a frame-scoped request queue
  drained by `TooltipHostLayer` in the topmost `UIBand.Tooltip` **after every scissor clip
  closes** — so an editor can request a tooltip from inside its `BeginClip` tab body and the
  box still draws unclipped on top. Two simple paths + an escape hatch:
  `RequestText(string, Rectangle anchor)` (one line, centered above the anchor rect, flips
  below when clipped), `RequestLines(params string[])` (multi-line, cursor-anchored), and
  `RequestCustom(Action<UICtx>)` (defer an arbitrary draw — e.g. a `UI/RichTip.cs` box).
  Request during Draw or Update; requests never survive the frame. **Precedent:**
  `MapEditorWindow.DrawGridCellTooltip(text, anchorCell)` → `Game1.Tooltips.RequestText(...)`,
  gated on `Game1.Popups.IsEmpty && !_eb.IsColorPickerOpen && !_eb.IsDropdownOpen` so the
  tooltip (which sits ABOVE the Popup band) doesn't paint over an open dropdown/color-picker.
  **`DrawGridCellTooltip` is single-line + uncolored** (RequestText is the ONLY rect-anchored
  path; there is NO colored rect-anchored overload) and is **shared by the Objects tab
  (`DrawObjectsTab`) and Units tab (`DrawUnitsTab`)** — to give the Units grid a coloured
  multi-line tooltip (e.g. a grey `UnitDef.Description` line under the name, mirroring
  `HUDRenderer.DrawUnitTooltip`), do NOT edit `DrawGridCellTooltip` (it would break the Objects
  tooltip). Instead, in `DrawUnitsTab`'s hover block, build a `List<(string,Color)>` and call the
  cursor-anchored coloured overload `Game1.Tooltips.RequestLines(lines)` directly, re-copying the
  same popup/dropdown suppression guard. Note the anchoring changes from "centred above the cell"
  to "at the cursor" — that matches the spell/HUD tooltips. Greys = `Necroking.UI.SpellTooltip.Dim`
  (150,150,170) / white = `.Text` (needs `using Necroking.UI;`; MapEditorWindow lacks it).
  Hover detection = `EditorBase.HitTest(rect)` (clip-aware: rect AND inside `_activeClip`, so
  a scrolled-out row won't false-trigger); cursor = `EditorBase.MousePos`. The old
  "editors hand-roll rect + `DrawText`" pattern and the private `HUDRenderer.DrawCursorTooltip`
  are both superseded by this service.
- **Def-dropdown precedent**: `DrawZoneSpawnPanel` rows use `_eb.DrawCombo` with cached
  filtered id arrays (`GetZoneForagableIdOptions` filters `_envSystem` defs by `IsForagable`,
  cache invalidated on `DefCount` change) — copy this for any "pick an env def" UI.
- **ProcGen tab = density-based placement brush (the newest tab, after Zones).**
  `MapEditorTab.ProcGen`; presets are `Editor/ProcGenStyle.cs` (`ProcGenStyle`: `Name`,
  `LargeDefIds`/`LargeDensity`, `SmallDefIds`/`SmallDensity` — two independently-spaced env-def
  pools), a global authoring registry loaded/saved as `data/procgen_styles.json`
  (`EnsureProcGenStylesLoaded`, `ProcGenStyle.LoadAll`/`SaveAll`, same "global registry like env
  defs" precedent as zones). `UpdateProcGenTab` → held-brush accrual `PaintProcGen` (fixed
  radius-5 disc `ProcGenBrushRadius`, `ProcGenRatePerDensity` = 5 attempts/sec per density
  point, fractional accum per pool so partial ticks carry over, capped at `maxPerFrame`=400) →
  `PlaceProcGenPool` (`ProcGenTries`=6 random-point-in-disc retries per attempt, same-pool
  min-spacing rejection via `ProcGenStyle.MinDistance(density)` = `8/sqrt(density)`, then the
  same `_envSystem.CanPlaceObject`/`AddObject`/`StampObjectCollisionAt`/
  `AutoCreateTriggerInstance` sequence as `PaintObjectsBatch`). Same batch-undo precedent
  (`_batchPlacedObjects` → `PushUndo(UndoObjectBatchPlace)` on left-mouse-up). Dev-server hook
  `DevPaintProcGen(styleName, pos, seconds)` exercises the exact same `PaintProcGen` path
  headless (ticks it at 60/s for N simulated seconds) — use this over hand-rolling a test.
  **Click guarding**: identical to every other tab — `UpdateProcGenTab` only paints on
  `leftDown && !overPanel`; `overPanel` is computed **once** in the shared `Update()` dispatcher
  via `IsMouseOverPanel(screenW, screenH)` (checks the standard right-side tab panel rect, plus
  the Zones-only left-village-panel rect) and threaded down as a parameter to every
  `Update<X>Tab`, so a click on the tab bar/style list/pool combos never also paints in the
  world underneath. `leftDown`/`leftClick` themselves are already gated on
  `!IsAnyPopupBlocking()` upstream (open dropdown/color-picker/texture-browser), so ProcGen
  gets that for free too. ProcGen has no left-side panel of its own (unlike Zones), so it needs
  no `IsMouseOverPanel` change — it reuses the existing right-panel check as-is.

- **Auto-ground (Objects tab) = stamp a ground patch under each painted object.** Toggle +
  controls live in the Objects tab; the state is a set of `MapEditorWindow` instance fields:
  `_autoGround` (bool toggle), `_autoGroundType` (int index into `_groundSystem` type list),
  `_autoGroundSize` (int patch radius in ground-vertex units), `_autoGroundNoise` (int #
  ragged-edge tiles), plus `_autoGroundTypeInit` (one-time default-to-"Dirt" resolve) and
  `_autoGroundStrokeOld` (`Dictionary<long,byte>?` accumulating pre-stamp vertex values for
  one stroke's undo). The **core stamp routine is `StampAutoGround(worldX, worldY, oldVals)`** —
  builds a circular patch of `_autoGroundType` at radius `_autoGroundSize`, grows
  `_autoGroundNoise` extra edge tiles, records old values into `oldVals` for undo via
  `SetGroundVertexRecorded`/`GroundVertexKey`, and returns whether anything changed. It reads
  the four `_autoGround*` instance fields directly and does NOT fire the texture-rebuild
  callback (`_onVertexMapChanged`) — the caller flushes that once per placement/stroke.
  **Callers today (both in `UpdateObjectsTab`):** single-click place (bundles the ground
  stamp with the object placement into an `UndoComposite` of `UndoObjectPlace` +
  `UndoGroundStroke`), and paint-mode via `PaintObjectsBatch` (accumulates into
  `_autoGroundStrokeOld`, bundled with `UndoObjectBatchPlace` into an `UndoComposite` on
  mouse-up). **UI**: `_autoGround = _eb.DrawCheckbox("Auto Ground", …)` gates a sub-section —
  `DrawAutoGroundSizeControl` (the ± size stepper, clamp 0..20), a ground-type `DrawCombo`
  over `_groundSystem` type names (default-resolves "Dirt" once via `_autoGroundTypeInit`),
  and a `DrawIntField("auto_ground_noise", …)`. `AutoGroundSectionHeight()` reserves the
  vertical space so the def list below shifts down when the toggle is on.
- **To add per-category autoground to ProcGen (the requested change):**
  `StampAutoGround` currently hardcodes reading the four `_autoGround*` instance fields, so it
  can't be reused as-is with different settings per pool. **Extract the parameterized core**
  — e.g. `StampGroundPatch(worldX, worldY, int typeIdx, int size, int noise, Dictionary<long,byte> oldVals)`
  — and make the existing `StampAutoGround` a thin wrapper that early-outs on `!_autoGround`
  and forwards the instance fields. Then `PlaceProcGenPool` (which already places each object
  and has the `_batchPlacedObjects`/`_autoGroundStrokeOld` accumulator pattern available) can
  call `StampGroundPatch` with per-pool settings. **Per-category storage** = new fields on
  `Editor/ProcGenStyle.cs` (`ProcGenStyle` has exactly two "categories": the Large and Small
  pools) — add e.g. `Large/SmallAutoGround{Enabled,Type,Size,Noise}` (note: ground *type* is
  an index into `_groundSystem`, but ProcGenStyle is a global registry — store the ground
  **type name** string, not the volatile index, and resolve it at stamp time, mirroring how
  the def pools store `List<string>` ids and resolve via `FindDef`/`ResolveProcGenDefs` each
  tick). Extend `ProcGenStyle.SaveAll`/`LoadAll` for the new fields, and add UI rows in the
  ProcGen tab's per-pool panel section of `DrawProcGenTab`. **Undo**: thread the ProcGen
  mouse-up path (in `UpdateProcGenTab`, ~5697) the same way Objects does — accumulate ground
  old-values into `_autoGroundStrokeOld` during `PlaceProcGenPool` and bundle an
  `UndoGroundStroke` with the `UndoObjectBatchPlace` into an `UndoComposite` on `leftUp`
  (today ProcGen pushes only `UndoObjectBatchPlace`, no ground). Also call `_onVertexMapChanged`
  once after `PaintProcGen` when any ground changed (mirror `PaintObjectsBatch`).

### Map-editor scrolling & scrollbars (census, 2026-07)

- **Panel geometry** (top of `Draw(screenW, screenH)`): `panelX = screenW - PanelWidth(320) - 10`,
  `panelY = 10`, `panelH = screenH - 20`. Tab content viewport = `tabContentRect =
  (panelX, contentY, PanelWidth, contentH)` where `contentY = panelY + TabRowHeight*2 + 2`
  and `contentH = panelH - TabRowHeight*2 - 2 - 92` (92px bottom bar). The whole tab body is
  scissor-clipped: `_eb.BeginClip(tabContentRect)` … `_eb.EndClip()` around the
  `switch (ActiveTab)` in `Draw`.
- **Scroll state**: `_tabScroll = new float[10]` (indexed by `(int)MapEditorTab`) + the
  Objects tab's separate `_envListScroll`. **Generic wheel handler** in `Update()`
  ("--- Scroll per-tab ---" block): `_tabScroll[(int)ActiveTab] -= scrollDelta * 0.2f`,
  clamped **at 0 only**, gated on `overPanel && !popupBlocking`.
- **Per-tab census** (how each tab scrolls; viewport = the list area a scrollbar spans):
  - *Ground* `_tabScroll[0]`, *Grass* `_tabScroll[1]`, *Walls* `_tabScroll[3]`, *Roads*
    `_tabScroll[4]`, *Regions* `_tabScroll[5]`, *ProcGen* `_tabScroll[9]` — **whole tab body
    scrolls** (every y = `contentY + … - (int)scroll` in both `Draw<X>Tab` and
    `Update<X>Tab`); viewport ≈ the full `tabContentRect` below the section header.
    **No max clamp** (can over-scroll past the end) and **no content height is measured**.
  - *Objects* `_envListScroll` — the 6-wide thumbnail grid (`ThumbGridCols`). Viewport:
    `x = _objGridDrawX = panelX + Margin`, `y = _objGridDrawY`, `w = PanelWidth - Margin*2`,
    `h = listAreaH = contentH - (contentY - contentTop) - 160` (160px reserved for the
    selected-def property block). Max-clamped in `DrawObjectsTab` (`maxObjScroll`). Has its
    **own wheel handler** in `UpdateObjectsTab` (the generic handler writes the unused
    `_tabScroll[2]`). Group mode ("Groups" category) reuses `_envListScroll` as a row list.
  - *Triggers* `_tabScroll[6]` — defs/instances row lists; draw stops at
    `contentY + contentH - 300` (property area). ⚠️ **Double-scroll bug**: `UpdateTriggersTab`
    has its own wheel handler AND the generic one both fire per frame, so tab 6 scrolls at
    2× rate (0.4/notch).
  - *Units* `_tabScroll[(int)MapEditorTab.Units]` (=7) — thumbnail grid like Objects.
    Viewport: `x = _unitGridDrawX = panelX + Margin`, `y = _unitGridDrawY` (below
    faction/patrol combos + corpse checkbox + label), `h = listH = contentH - (curY -
    contentY) - 100`. Max-clamped in `DrawUnitsTab` (`maxUnitScroll`). Uses the generic
    wheel handler.
  - *Zones* — **does not scroll**: the list truncates with a `"... N more"` row at
    `contentY + contentH - 260`.
- **Canonical scrollbar — single source of truth is `Necroking/UI/VScrollbar.cs`**
  (static `Necroking.UI.VScrollbar`): pure geometry + palette (`TrackColor`/`ThumbColor`/
  `ThumbHotColor`, `Width = 5`, `Fits`, `TrackRect`, proportional `ThumbRect` with
  `MinThumbH = 20` and overscroll-clamped ratio, padded `HitRect`, `ScrollFromDrag`).
  Unitless — callers own input reading and rect fills.
- **EditorBase wrappers** (`Editor/EditorBase.cs`):
  - `DrawVScrollbar(x, y, viewH, contentH, scroll)` — **indicator-only** overload (pure
    draw). Callers: the dropdown overlay (`DrawDropdownOverlays`) and `DrawTextArea`.
  - `DrawVScrollbar(string id, x, y, viewH, contentH, scroll, layer)` — **interactive
    draggable** variant: thumb-drag + track-click-jump (thumb centres on cursor), generous
    `VScrollbarHitRect` hit zone, hot-highlight, returns the clamped scroll (0 when content
    fits), inert under `IsInputBlocked`, calls `SetMouseOverUI`. Drag state
    `_vscrollDragId`/`_vscrollDragGrabOffset` lives in EditorBase (mouse-up anywhere ends
    the drag). Callers: `DrawScrollableList`, `ScrollPanel.End`, `SettingsWindow`
    (per-tab, `TabScrollIds` — its old hand-rolled `_scrollbarDragTab` bar is gone),
    `MapEditorWindow` (Objects grid + Groups, Units grid, generic per-tab), `TextureFileBrowser`.
  - `BeginScrollPanel(panelId, rect, …)` / `ScrollPanel.End(curY)` — the full scroll-panel
    scope: wheel (`HandlePanelScroll` clamped by last frame's `SetPanelContentHeight`),
    scissor clip, content-height measurement, and the draggable bar; offset owned by
    EditorBase `_scrollOffsets[panelId]`. Prefer this for new scrollable editor panels;
    hand-rolling the height math has double-counted the offset before (feedback loop).
- **Draggable scrollbar OUTSIDE EditorBase (raw HUD menus): the scenario-list precedent.**
  `UI/ScenarioListScreen.cs` (and its twin `UI/LoadMenuScreen.cs`): the `View` struct
  carries `ScrollX/ScrollY/ScrollViewH/ScrollContentH` + `ClampScroll`, built by the
  screen's private `BuildLayout`; **input** in the screen's `Update()` (offset =
  `Game1._scenarioScrollPx`, drag state `_scrollDragging`/`_scrollGrabOffset` on the
  screen; thumb-grab + track-jump + wheel via the shared `VScrollbar` statics, row clicks
  skipped while dragging, clicks gated to the scroll window); **draw** via
  `MenuDraw.Scrollbar` (`UI/MenuCommon.cs`) + scissor-clipped rows. Copy this pattern for
  any main-menu family list.
- **Wheel-scroll logic census**: canonical = `EditorBase.HandlePanelScroll` (rect +
  maxScroll overload, or id-keyed panelId+viewH overload backed by `SetPanelContentHeight`;
  respects `IsInputBlocked`/`_scrollConsumed`, calls `ConsumeScroll`+`SetMouseOverUI`) —
  used by Spell/Item/Unit/EnvObject/UIEditor detail panels, SettingsWindow,
  TextureFileBrowser. `DrawScrollableList` has its own inline wheel handler (0.15
  sensitivity, per-`panelId` `_scrollOffsets` dict, `GetScrollOffset`/`ScrollListToItem`).
  MapEditorWindow's generic per-tab handler bypasses both (see census above). Runtime UI
  (non-editor) scrolls rows without bars: `UI/GrimoireOverlay.cs` `_scrollRow` (rebinds
  widget tiles), `UI/GraveRosterUI.cs` `_scroll` (int rows), `UI/UILayer.cs` `OnScroll`
  virtual + consume; the widget-def `IsScrollbar`/`ScrollbarProportional` fields (RI20)
  are authored/serialized (`UIEditorWindow`/`UIDefsIO`) but have no runtime bar-drawing
  consumer.

**Look/edit here when…** adding a map-editor tab/category, changing brush painting or
placement spacing, changing what SaveMap persists, building def-picker UI in a tab panel,
adding scrollbars / changing list scrolling in the map editor (the census above),
adding a new density/procedural placement brush (ProcGen tab, `PaintProcGen`/`PlaceProcGenPool`),
or working on **auto-ground** (the `_autoGround*` fields + `StampAutoGround`/`StampGroundPatch`
+ `UndoGroundStroke` bundling).
For **any new world-clicking tab**, gate world-affecting input on `!overPanel` (and reuse
`overPanel`/`popupBlocking` computed once in `Update()`) — do not re-derive panel-hover per tab.

## Spell editor & unit editor — where the editable fields live

Two different field-declaration models; know which you're in before adding a field or
per-field UI (tooltips, validation):

### Spell editor (`Editor/SpellEditorWindow.cs`) — reflection-driven
- **The field list is NOT in the editor** — it's the `[EditorField(...)]` attributes on
  `SpellDef` properties in `Data/Registries/SpellRegistry.cs` (~166 annotations; `Group`,
  `Order`, `Step`, `[EditorCombo]`, `[EditorVisible]`, `[EditorRegistryDropdown]`,
  `[EditorCheckboxGrid]` — attribute classes in `Editor/EditorAttributes.cs`).
- Rendered by **`Editor/ReflectionPropertyRenderer.cs`**: `SpellEditorWindow.DrawDetailPanel`
  calls `_renderer.DrawAnnotatedProperties("sp", def, …)`, which iterates the cached layout
  and calls `DrawField(fieldId, entry, obj, x, ref curY, w)` per property — **`DrawField` is
  the single per-row chokepoint** (row rect = `(x, rowYbefore, w, curY-rowYbefore)`).
  `EditorFieldAttribute` has **no tooltip property** (as of 2026-07).
- Custom (non-reflection) spell sections still hand-drawn in `SpellEditorWindow.cs`:
  `DrawFlipbookRefSection`, `DrawSpellPreviewSection`, the buff/flipbook manager popups
  (`DrawBuffManagerPopup`/`DrawBuffDetail`, `DrawFlipbookManagerPopup`).

### Unit editor (`Editor/UnitEditorWindow.cs`) — hand-rolled sections
- No reflection. `DrawRightPanel` calls per-section methods, each a run of
  `_ui.DrawTextField/DrawIntField/DrawFloatField/DrawCheckbox/DrawCombo` calls (~103 total):
  `DrawNameIdFields`, `DrawIdentitySection`, `DrawStatsSection`,
  `DrawLocomotionCalibrationSection`, `DrawCombatOverridesSection`, `DrawCasterSection`,
  `DrawEquipmentSection`, `DrawColorSection`, `DrawAnimTimingSection`,
  `DrawWeaponPointSection`; sub-editors `DrawWeaponDetail`/`DrawArmorDetail`/
  `DrawShieldDetail` (via `RegistryCrudPanel`), `DrawGroupEditor`/`DrawGroupDetail`.
- Rows are `RowH`-tall at `(x, y, w)` known at each call site — per-field hover UI must be
  added per call (or via a shared helper).

### Per-field hover tooltips — what exists
- **Canonical service**: `Game1.Tooltips` (`UI/TooltipSystem.cs`) — see the Map-editor
  section's "Hover-tooltip helper" bullet. Draws topmost after scissor clips.
- **Editor-row precedent**: `Editor/SettingsWindow.cs` private
  `RowTip(x, y, w, plain, tech)` — `_ui.HitTestCursor(rowRect)` gate + suppression when
  `!Game1.Popups.IsEmpty || _ui.IsColorPickerOpen || _ui.IsDropdownOpen`, then
  `Game1.Tooltips.RequestLines(plain, tech)`. Called after each settings row.
- **Dropdown OPTION tooltips already exist**: `DrawCombo`'s `optionTooltipFor:` callback +
  `Editor/DefTips.cs` (canonical def-summary builders, dispatched by
  `ReflectionPropertyRenderer.DrawRegistryDropdown` via `DefTips.ForRegistryEntry`). This is
  option-hover text, NOT per-field help.
- **To add per-field tips to the spell editor**: add a `Tooltip` string to
  `EditorFieldAttribute`, carry it on `FieldEntry` (built in
  `ReflectionPropertyRenderer.GetOrBuildLayout`), hit-test the row in `DrawField` — one hook
  covers every reflected field (also item editor, which shares the renderer). Unit editor
  needs a per-call helper (promote `RowTip` into `EditorBase`).

**Look/edit here when…** adding/reordering a spell-editor field (annotate `SpellDef` in
`SpellRegistry.cs`), adding a unit-editor field (the section method in `UnitEditorWindow.cs`),
adding per-field hover help (the `DrawField` chokepoint / `RowTip` precedent above).

## UI / widget editor

### `Editor/UIEditorWindow.cs` — main window + all data models + clone logic
The whole widget editor plus the editor working-copy model classes live at the top of this
one (~3900-line) file:

- **Model classes (editor working copies; the full field list a deep copy must cover):**
  - `UIEditorNineSliceDef` — a 9-slice: `Id, Texture, Border{Left,Right,Top,Bottom},
    TileEdges, Harmonize` (HarmonizeSettings recolor).
  - `UIEditorTextRegion` — text layout/style: `X,Y,W,H, Align, VAlign, FontFamily, FontSize,
    FontColor[], WordWrap, LineSpacing, CharSpacing, Bold, BoldStrength, TextOutlineColor[],
    TextOutlineWidth, OutlineOffset{X,Y}`.
  - `UIEditorElementDef` — an element: `Id, Type, NineSlice, NineSliceScale, ImagePath,
    Width, Height, TintColor[], TextRegion, DefaultText, Harmonize, StrokeThickness,
    StrokeColor[], StrokeMode`.
  - `UIEditorTints` — button state colors: `Normal[], Hovered[], Pressed[], Disabled[]`.
  - `ChildOverrideEntry` (RI22 nested overrides) — `ChildIndex, Override{X,Y,W,H},
    OverrideDefaultText, OverrideElement, OverrideIgnoreLayout`.
  - `UIEditorChildDef` — a child slot: `Name, Element, Widget (child-widget ref), X, Y,
    Width, Height, Anchor, SizeMode, Interactive, DefaultText, Tints, IgnoreLayout,
    NineSliceScale, HasTextOverride, TextOverride, List<ChildOverrideEntry> ChildOverrides`.
  - `UIEditorWidgetDef` — the widget: `Id, Background, BackgroundImagePath, Stencil,
    StencilImagePath, Frame, Width, Height, AutoSizeHeight, BackgroundScale, FrameScale,
    FrameInsetR, BackgroundTint[], StencilTint[], FrameTint[], BackgroundInset, StencilInset,
    FrameInset, BgHarmonize, StencilHarmonize, FrameHarmonize, Modal, Layout, LayoutPadding,
    LayoutSpacing, LayoutPad{Top,Bottom,Left,Right}, LayoutSpacing{X,Y}, Scroll,
    ScrollContentW, ScrollContentH, ScrollStep, IsScrollbar, ScrollbarProportional,
    List<UIEditorChildDef> Children`.
  - Data lists on the window: `_nineSlices`, `_elements`, `_widgets`.

- **Copy / paste / duplicate (the clone logic):**
  - `CloneWidget(UIEditorWidgetDef orig)` — clones a top-level widget. Called by the
    **"Copy" widget button** (in `DrawWidgetsTab`, ~line 2201): `CloneWidget` then
    `copy.Id = orig.Id + "_copy"` and appended to `_widgets`. Recurses children via
    `CloneChild`.
  - `CloneChild(UIEditorChildDef ch)` — clones one child (deep-clones `Tints`, `TextOverride`,
    and `ChildOverrides`). Called by both widget clone and child copy/paste.
  - `CopySelectedChild(def)` → sets `_childClipboard = CloneChild(...)`. Bound to the
    child-panel **"Copy" button** (~2524) and **Ctrl+C** (~3746).
  - `PasteChild(def)` → `CloneChild(_childClipboard)`, `Name += "_copy"`, circular-ref
    checked via `WouldCreateCircularRef`, appended to `def.Children`. Bound to **"Paste"
    button** (~2530) and **Ctrl+V** (~3752).
  - `WouldCreateCircularRef(parentId, candidateId)` — BFS guard so pasting a child-widget
    ref can't create a cycle.
  - Undo/redo: `EditorSnapshot` serializes all three lists to JSON (`_undoStack`); the
    editor is memento-based, so undo/redo does a **full deep round-trip** unlike the manual clones.

- **⚠️ The clones are hand-written field-by-field and MISS fields** (see pitfalls).

### `Editor/UIEditorWindow.Helpers.cs` — supporting draw/layout helpers (no clone logic here)
The copy/duplicate logic is entirely in the main file; helpers hold preview/layout only.

### `Editor/HarmonizeSettings.cs`, `Editor/ColorHarmonizer.cs`, `Editor/ColorPickerPopup.cs`
`HarmonizeSettings` is the recolor/tint payload referenced by nine-slice/element/widget
harmonize fields. If a clone omits a `Harmonize`/`*Harmonize` field, the copy silently loses
its recolor.

### Runtime side (not the editor): `UI/RuntimeWidgetRenderer.cs`, `UI/WidgetLayoutUtils.cs`
`data/ui/widgets.json` is consumed at runtime by `RuntimeWidgetRenderer` (draws) and
`WidgetLayoutUtils` (layout). The editor's `*Def` classes are working copies serialized
to/from that JSON. When adding a field to a `*Def`, it must also be handled in the runtime
model and both save + clone paths.

### Reference fields (id-refs to other defs) — where they're rendered/clicked
All cross-def reference fields in the widget editor are **`EditorBase.DrawCombo` calls in
`UIEditorWindow.cs`** (eager press→drag→release dropdowns; `DrawCombo` itself is in
`Editor/EditorBase.cs`). Census by combo field-id:
- Element → nine-slice: `"el_ns"` in `DrawElementDetail`.
- Widget → frame/background/stencil nine-slice: `"wd_frame"` / `"wd_bg"` / `"wd_stencil"`
  in `DrawWidgetDetail`.
- Child → element / child-widget: `"ch_elem"` / `"ch_widget"` in `DrawChildProperties`.
- Add-child pickers: `"addchild_elem"` / `"addchild_wgt"` (widget detail, child list foot).
**Jump-to-referenced-def**: a partial affordance exists — `GoToChildTarget(child)` in
`UIEditorWindow.cs` (switches `ActiveTab` + `SelectedIndex` to the element/sub-widget a
child references, clearing child selection), fired by a `"-> Go to {target}"` `DrawButton`
at the top of `DrawChildProperties`. It covers **child → element/widget refs only**; the
nine-slice combos (`el_ns`/`wd_frame`/`wd_bg`/`wd_stencil`) have no jump. There is also a
public `SelectWidgetById(id)` test hook (Widgets tab only). A double-click-to-navigate
feature on the combos would have to coexist with `DrawCombo`'s open-on-press gesture
(no double-click detection exists anywhere; `Core/InputState.cs` has only single-frame
`LeftPressed`/`LeftReleased` edges + `PressStartPos`), and should route through
`GoToChildTarget` / a generalized select-by-id rather than a new selection path.

### Look / edit here when…
- **"copy/duplicate/paste a widget or child drops properties"** → `CloneWidget` / `CloneChild`
  in `UIEditorWindow.cs`. Add the missing field assignment there.
- **Reference fields (nine-slice/element/child-widget id-refs), jump-to-referenced-def** →
  the DrawCombo census above.
- **Widget/element/child field list** → the model classes at the top of `UIEditorWindow.cs`.
- **Adding a new widget property** → add to the `*Def` class, the JSON save/load, `CloneWidget`
  or `CloneChild`, and the runtime `UI/RuntimeWidgetRenderer.cs` / `WidgetLayoutUtils.cs`.

## Pitfalls / gotchas (UI editor clone)

- **The manual clones are NOT deep copies of the whole model — they omit fields.**
  `CloneWidget` copies only a subset of `UIEditorWidgetDef` and **omits**:
  `BackgroundImagePath, Stencil, StencilImagePath, Frame, AutoSizeHeight, FrameScale,
  FrameInsetR, StencilTint, FrameTint, BackgroundInset, StencilInset, FrameInset,
  BgHarmonize, StencilHarmonize, FrameHarmonize`. So a copied widget loses its stencil/frame
  nine-slice refs, all frame/stencil tints & insets, and all three harmonize recolors.
- **`CloneChild` also drops fields**: it copies only part of the nested `TextOverride`
  (`UIEditorTextRegion`) — it omits `FontFamily, LineSpacing, CharSpacing, Bold, BoldStrength,
  TextOutlineColor, TextOutlineWidth, OutlineOffsetX, OutlineOffsetY`. It does NOT copy the
  element/nine-slice defs themselves (`Element`/`Widget` are string refs, which is correct —
  they alias the shared def by id, not deep-clone it).
- **A true deep copy would just JSON round-trip** (as `EditorSnapshot`/undo already does),
  which is the robust fix and avoids the field-drift these hand-written clones suffer.
- **Byte-array fields alias unless `.Clone()`d.** The existing code correctly calls
  `(byte[])x.Clone()` on `BackgroundTint`, the `Tints` quartet, and `FontColor` — any new
  color field must do the same or the copy will share the array with the original.
- **Id/name re-mapping is manual and shallow:** widget copy sets `Id = orig.Id + "_copy"`;
  paste sets `Name += "_copy"`. There is **no uniqueness check** — copying twice yields two
  `_copy` ids. Child `Widget`/`Element` string refs are intentionally NOT re-mapped (they
  point at shared defs), but a self-referential paste is guarded by `WouldCreateCircularRef`.
- **Nested children recurse fine** (`CloneWidget`→`CloneChild` per child; child-widget refs
  are by id so no infinite recursion), but grand-child data only exists as `ChildOverrides`
  on the child, which `CloneChild` does deep-copy.

## Editor entry-click ghost-replay — a mouse-opened editor replayed the previous close click (FIXED 2026-07)

The immediate-mode editors **bypass the router's click-consumption entirely**: `EditorBase`
widgets read mouse state during Draw and never check `InputState.IsMouseConsumed` /
`UILayer.InputGranted`. `EditorBase.DrawButton` fires **on release**
(`hovered && released-edge && rect.Contains(_pressStartPos)`) — a multi-frame gesture that
per-frame consumption never protected anyway.

**The bug (root-caused, now fixed):** the insta-close only ever happened on the **2nd+**
mouse-open of an editor. Closing an editor via its `[X]` flips `_menuState` **mid-Draw**,
which skipped the gated `EndDrawFrame`, freezing `EditorBase`'s (then-private)
`_mouse`/`_prevMouse`/`_pressStartPos` as a **complete ghost of the closing click**
(released@X-position / prev pressed / press-start inside the `[X]` rect). On the next
launcher-click open, `_editorUi.UpdateInput` hadn't run that frame (menuState was still
`None` at `Game1.cs:3281`), so the first Draw used the frozen ghost and `DrawButton`
**replayed the old `[X]` click** — same stale hit-test position = the X's own rect — closing
the editor the frame it opened. First open was safe (frozen state = default); **F10 was safe**
because it sets `_menuState` *before* line 3281, so `UpdateInput` ran with fresh mouse.

**The fix (implemented, builds, drive-verified — this is now reality):** `EditorBase` keeps
**no private mouse history at all**. `Necroking/Core/InputState.cs` owns everything:
- `DrawPrevMouse` — snapshotted once per Draw by `InputState.SnapshotDrawFrame()`, called
  **unconditionally** at the end of `GameRenderer.DrawHudBlock` (`GameRenderer.Draw.cs`) — so
  it can never freeze stale the way the old gated `EndDrawFrame` did.
- `PressStartPos` — stamped in `InputState.Capture` on the physical `LeftPressed` edge.
- `ConsumeGesture()` — invalidates the in-flight press (`PressStartPos = (-1,-1)`), so
  `DrawButton`'s `rect.Contains(_pressStartPos)` test fails until the next physical press.

`EditorBase._mouse`/`_prevMouse`/`_pressStartPos` are now **read-through properties over
`_input`** (`_prevMouse => _input.DrawPrevMouse`, `_pressStartPos => _input.PressStartPos`);
`EditorBase.EndDrawFrame` keeps only the dropdown reconcile. Both `_editorUi` and `_uiEditor`
are wired to Game1's central `InputState` in the `Game1` ctor. The gesture is invalidated on
**any `_menuState` transition**: `Game1.Update` compares `_menuState` against
`_menuStateAtLastCapture` (field ~`Game1.cs:650`) *before* `_input.Capture` (~line 2785) and
calls `ConsumeGesture()`; `ToggleEditorWindow` (~line 4196) also calls it for same-frame
coverage.

**Do NOT reintroduce the old band-aid.** `MapEditorWindow.SuppressClicksUntilRelease` still
exists but only for the map editor's own canvas paint logic — it is **no longer the model to
generalize**. The earlier "generalize SuppressClicksUntilRelease into EditorBase"
recommendation was superseded by the `InputState` consolidation above; centralize any new
mouse-history need in `InputState`, not in per-editor flags.

**Close paths of the full-screen editors** (there is NO outside-click-close): `[X]` button →
`WantsClose`, polled by `EditorHostLayer.Draw` (`UI/Layers/HostLayers.cs`) right after each
editor's Draw; ESC → `EditorHostLayer.OnCancel` (Closable); toggling F-key/launcher again.
Note the `[X]`-flips-`_menuState`-mid-Draw shape is what made this class of bug possible.

**Look/edit here when:** an editor opens and instantly closes/saves when opened by mouse, an
entry click paints/edits on frame one, or you're wiring a new mouse path that opens/closes an
editor — invalidate the gesture via `InputState.ConsumeGesture()` (as `ToggleEditorWindow`
and the `_menuState`-transition check already do), don't add a per-editor suppress flag.

## Related areas
- Runtime widget rendering/layout: `UI/RuntimeWidgetRenderer.cs`, `UI/WidgetLayoutUtils.cs`
  (see [ui.md](ui.md) for the runtime overlays/panels that consume widgets).
- Harmonize/recolor payload: `Editor/HarmonizeSettings.cs`, `Editor/ColorHarmonizer.cs`.
- Click consumption / router dispatch: [ui.md](ui.md) "THE UIRouter" + "MouseOverUI / UI
  hit-testing" (`Core/InputState.cs` `ConsumeMouse`, `UI/UILayer.cs` `HandleInput` template).

## Consolidation update (2026-07-07)

- **Single-line fields**: `EditorBase` FieldCore owns click/drag-select/commit
  for DrawTextField/DrawIntField/DrawFloatField/DrawSearchField — add new field
  types as wrappers over it, don't hand-roll activation.
- **Section headers**: `EditorBase.DrawSectionHeader` (Rule/Bar/Label styles).
  MapEditorWindow keeps its own small-font variant deliberately.
- **Registry sub-editors**: `Editor/RegistryCrudPanel<TDef>` = list+select+
  New/Copy/Delete/Save+clipboard over `RegistryBase<TDef>`; weapon/armor/shield
  sub-editors use it. The spell-editor buff/flipbook manager POPUPS are NOT on
  it (modal chrome + apply-on-close semantics = structural variance; see
  docs/consolidation-review/dossiers/editor-parallel-subeditors.md).
- **UI widget def files**: `Editor/UIDefsIO.cs` is the one reader+writer used by
  both UIEditorWindow and RuntimeWidgetRenderer.
