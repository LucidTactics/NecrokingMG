# Editor/ — in-game immediate-mode editors

`Necroking/Editor/` holds the debug/authoring editors (unit, spell, item, map, wall,
env-object, settings, and the **UI/widget editor**). All extend `EditorBase` (immediate-mode
DrawButton/DrawText/DrawCombo helpers, text rounding). Each `*EditorWindow.cs` owns its own
working-copy data model, undo stack, and JSON load/save.

This doc currently covers the **UI/widget editor** in depth (the data-driven
`data/ui/widgets.json` editor). Other editors are stubs here — expand when needed.

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
