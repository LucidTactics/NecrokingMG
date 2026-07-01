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
