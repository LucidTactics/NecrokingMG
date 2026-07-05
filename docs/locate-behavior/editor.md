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
- **Editor-wide keyboard shortcuts** (Ctrl+S save / Ctrl+L load / Ctrl+Z undo) sit in one
  block in `Update()` right after `bool ctrlDown = …`, each gated on `!textEditing`
  (`textEditing = _eb.IsKeyboardCaptured`). New global/per-tab chords go here — key edges
  via `kb.IsKeyDown(K) && _prevKb.IsKeyUp(K)`.
- **Save**: `SaveMap()` writes `assets/maps/<map>.json` with `Utf8JsonWriter` — env objects
  go in the `placedObjects` array (`defId, x, y, scale, seed`) straight from
  `_envSystem.Objects`, so anything placed via `AddObject` is persisted automatically. Env
  *defs* save separately via `MapData.SaveEnvDefs("data/env_defs.json")`; zones to the
  `SaveZones` sidecar (see [zones.md](zones.md)).
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

**Look/edit here when…** adding a map-editor tab/category, changing brush painting or
placement spacing, changing what SaveMap persists, building def-picker UI in a tab panel,
adding a new density/procedural placement brush (ProcGen tab, `PaintProcGen`/`PlaceProcGenPool`),
or working on **auto-ground** (the `_autoGround*` fields + `StampAutoGround`/`StampGroundPatch`
+ `UndoGroundStroke` bundling).
For **any new world-clicking tab**, gate world-affecting input on `!overPanel` (and reuse
`overPanel`/`popupBlocking` computed once in `Update()`) — do not re-derive panel-hover per tab.

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

### Look / edit here when…
- **"copy/duplicate/paste a widget or child drops properties"** → `CloneWidget` / `CloneChild`
  in `UIEditorWindow.cs`. Add the missing field assignment there.
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

## Related areas
- Runtime widget rendering/layout: `UI/RuntimeWidgetRenderer.cs`, `UI/WidgetLayoutUtils.cs`
  (see [ui.md](ui.md) for the runtime overlays/panels that consume widgets).
- Harmonize/recolor payload: `Editor/HarmonizeSettings.cs`, `Editor/ColorHarmonizer.cs`.
