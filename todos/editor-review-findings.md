# Editor Windows Full Review — Findings (2026-07-03)

Multi-agent review of all editor windows + sub-components (~30k lines, 25 files) against:
input consumption, dropdown/scroll display bugs, field focus consistency, preview refresh,
architecture/helpers, misc bugs. All findings below were verified against actual code
(file:line evidence was checked; four highest-impact claims re-verified by hand).

Status: **findings only — no fixes applied yet.** Work top-down; the systemic fixes
(S1–S4) each kill an entire class of window-level bugs.

---

## SYSTEMIC ROOT CAUSES (fix once in EditorBase, kills dozens of bugs)

### S1. Draw-order input layering: popups/dialogs don't block widgets drawn earlier in the frame
EditorBase widgets process input during Draw and read **raw MouseState**, never
`InputState` consumption. `UpdateInput` resets `_inputLayer` each frame
(EditorBase.cs:304), pre-raising only for open combo dropdowns / held presses / the color
picker. Anything that raises the layer *during its own draw call* (DrawConfirmDialog
EditorBase.cs:2228, WadingEditorPopup.cs:174, SpellEditor popups, env-editor dialogs)
only blocks widgets drawn *after* it — the whole editor beneath already ran at layer 0.
PopupManager's modal stack consumes `InputState` only → protects game world, not sibling
EditorBase widgets.

**Verified leak sites (click/scroll under an open modal):**
- Confirm dialogs — all callers unguarded: UnitEditorWindow.cs:591-655,
  SpellEditorWindow.cs:253-260, ItemEditorWindow.cs:131-139,
  EnvObjectEditorWindow.cs:427-429/2142-2153. Clicking "Confirm" (screen center) also
  presses whatever field/list row is beneath; list selection can even change which def
  the confirm deletes (env editor reads `_selectedDef` live).
- SpellEditor buff/flipbook manager popups: layer raised only after main panels drew
  (SpellEditorWindow.cs:236-250). Wheel over popup scrolls hidden detail panel AND
  consumes the global scroll flag so the popup itself can't scroll there.
- UnitEditor group editor + wading popup: no pre-panel raise (UnitEditorWindow.cs:324-325
  covers only `_activeSubEditor`; extend to `_groupEditorOpen || _wadingEditor.IsOpen ||
  _confirmDelete*`). The weapon/armor/shield sub-editor got the correct fix; never propagated.
- Map editor panels stay live under env-object/wall editor overlays
  (MapEditorWindow draws panels :939-968, overlays after :1022-1031; Update is gated
  :641-675 but Draw-time widgets are not).
- SpellEditor popups' `InputLayer = 0` force (SpellEditorWindow.cs:651-653, 901-903)
  additionally defeats the dropdown pre-raise and TextureFileBrowser's raise — popup
  widgets live under an open dropdown / file browser.

**Fix shape:** hosts raise the layer *before* drawing panels whenever any popup/dialog
flag is set (one condition per window), and popups draw their own widgets with `layer:`
args (WadingEditorPopup already does this correctly). Longer-term: widgets consult the
PopupManager stack or a base-offset layer concept instead of save/force-0/restore.

### S2. Scissor clip is draw-only — scrolled-off widgets remain invisibly clickable
`BeginClip` sets GPU scissor only; `IsHovered` (EditorBase.cs:201-207) never consults the
scissor stack. Every scrolled panel = field of invisible live controls.
- SettingsWindow.cs:159-223 — scrolled rows sit under the tab row / Back button; a click
  on a tab both toggles an invisible checkbox (press) and switches tab (release).
- ItemEditorWindow.cs:383 — scrolled reflection fields overlap top-bar Save/X.
- UIEditorWindow.cs:2258/1462/1754 — detail controls overlap the tab bar; invisible
  Browse/Delete/Harmonize buttons fire.
- EnvObjectEditorWindow.cs:1199 — fields hit-test under title-bar Save/X; invisible
  +/- spinners mutate def values.
- MapEditorWindow: same class via hand-rolled Update hit-tests with no content-rect bound
  (e.g. :1105-1139, :3394-3524) — clicking Save can also add/delete a ground type; the
  click that switches tabs is re-processed by the NEW tab's updater same frame
  (:715-734 before :822-851).

**Fix shape:** make `IsHovered` intersect with the current scissor rect when a clip is
active (one change, fixes all windows), and bound MapEditor Update hit-tests to the
content rect.

### S3. Text-field edit buffers keyed by fieldId string, not object identity → edits commit into the wrong object
Buffer commits on outside-click/deactivate (EditorBase.cs:808-815, 871-879, 971-973).
`DrawScrollableList` clears the active field on selection change (EditorBase.cs:727-728)
— every *other* selection path doesn't:
- MapEditorWindow: nearly every tab uses static ids (`trig_name`, `region_x`,
  `road_name`, `ground_name`, `inst_*`, …) with press-based selection in Update → typing
  on A then clicking B writes A's buffer into B. The Zones tab embeds the index in the id
  precisely to avoid this (:4083-4084) — pattern never propagated.
- SpellEditorWindow: constant `"sp"` reflection prefix + `fb_*`/`bf_*` ids (:520, :778+).
- ItemEditorWindow: `"item"`/`"pot"`/`recipe_{i}_*` prefixes (:389/:445/:470).
- UIEditorWindow child tree: `_selectedChildIdx` set on press (:3651-3662), never calls
  `ClearActiveField`; `ch_*`/`co_*` fields commit into the new child.
- WallEditorWindow 3×3 segment grid (:672-677) + segment-agnostic ids (latent, see W1).
- UnitEditor row deletes shift index-keyed ids (`weap_{i}_*`) mid-frame (low).

**Fix shape:** embed the selected object's id in field prefixes (reflection renderer:
`$"{prefix}_{def.Id}"`), and/or call `ClearActiveField()` on every selection-change path
(tree clicks, tab switches, grid clicks).

### S4. DrawButton fires on release with no press-origin capture
EditorBase.cs:530-535. Press anywhere, drag onto a button, release → fires. Combined
with press-based lists/fields, one physical click can fire two widgets. Verified
consequences: UIEditor child drag-reorder released over "Del" deletes the child
(:2510-2531 + buttons :2538-2559); map-editor world-paint drags released over the panel.
The framework already patches this ad-hoc for dropdown/picker dismissal
(`_dropdownHoldingMousePress`). **Fix:** record pressed-widget id on press; only the
widget that owns the press may fire on release (standard IMGUI "active id").

### S5. Hand-maintained clone functions silently drop fields
- `CloneUnit` (UnitEditorWindow.cs:3770-3849): drops Morale, all detection/alert fields,
  combat overrides, PlayerForm/Paths, entire locomotion-calibration block, wading data,
  Tags… Copying a calibrated quadruped yields a biped-defaults copy.
- `CloneWeapon` (:3851-3870): drops Archetype, Priority, CooldownRounds, all
  Pounce/Trample/Sweep params — copying a trample weapon yields plain melee.
- `CloneItem` (ItemEditorWindow.cs:569-580): drops SkillPointPool/Amount; doesn't clone
  the linked PotionDef.
- `CopySelectedDef` (EnvObjectEditorWindow.cs:1881-1958): drops Harmonize,
  HarmonizeCorrupt, glyph/fog/berry fields (10 total).
**Fix:** JSON serialize/deserialize round-trip clone helper (registry defs are already
JSON-serializable), delete the manual copies.

### S6. Shared base classes have near-zero adoption
- `EditorListState` — **zero references codebase-wide** despite doc naming
  SpellEditor/ItemEditor/UnitEditor/EnvObjectEditor as targets; each hand-rolls
  selection/filter/scroll state (UnitEditor 4×).
- `EditorWindow` — only SpellEditorWindow inherits, and even it bypasses `DrawTopBar` +
  `HandleStandardShortcuts` (duplicated save logic :277-281 vs :355-359). Unit/Item/Map/
  UIEditor/EnvObject hand-roll chrome, dirty flag, Ctrl+S (several check LeftControl only),
  status toast.
- UnitEditor weapon/armor/shield sub-editors + CRUD are structural triplicates (~800 lines);
  four settings tab files are copy-paste siblings in 3 divergent styles; SpellPreview/
  BuffPreview duplicate RT lifecycle + primitives (~200 lines); ColorPickerPopup duplicates
  EditorBase draw/button/text-edit widgets (~250 lines); TextureFileBrowser hand-rolls
  list/scroll (~75 lines) — the source of its display bugs.
- Settings tabs' flat POCOs are exactly what EditorAttributes + ReflectionPropertyRenderer
  were built for; annotating them would delete most of the 4 tab files AND fix the dirty
  bug (SET1) for free.

---

## TOP FUNCTIONAL BUGS (per window)

### Wall editor (effectively dead feature)
- **W1.** `_ui.InputLayer = 1` at WallEditorWindow.cs:151 blocks **its own** widgets (all
  hardcoded layer 0): Save, X, def list, New/Delete, all fields, color swatch — none can
  ever click. Own comment at :297-300 admits it. Meanwhile the parent map editor stays
  live (S1). Two agents independently confirmed.
- **W2.** Save writes `assets/maps/{map}_walldefs.json` (:865-867) that **nothing loads**
  (only reader of wall defs is MapData.Load reading name/maxHP/protection). Segments are
  also never rendered in-game (GameRenderer.World.cs:289-298 draws placeholder rects).
  The entire segment-editing UI edits data with no renderer and no persistence.
  → **Decide: real feature (add renderer + load path) or delete ~700 lines.**
- **W3.** X button sets `_open=false` without `Close()` → stale modal layer eats world
  clicks until ESC (:187-191). Latent behind W1.
- **W4.** Sprite-path field commits the `"..."`-truncated display string into the def
  (:741-751). Latent behind W1.

### Settings window
- **SET1.** Environment/Weather/General/Horde tabs call `MarkDirty()` unconditionally
  every Draw (SettingsWindow.cs:188-194, 404-417) + `Update` saves whenever dirty (:88-104)
  → **settings.json (and weather.json) rewritten to disk ~60×/sec** while those tabs are
  open. The 5 inline tabs do change-detected MarkDirty correctly. VERIFIED BY HAND.
- **SET2.** Environment tab R/G/B int fields are dead: unconditional
  `HdrToColorJson(baseHdr, grass.BaseColor)` at SettingsEnvironmentTab.cs:44 (and :60)
  copies the frame-start snapshot back over the int-field edits every frame. VERIFIED BY
  HAND. Correct swatch→ints chaining exists in SettingsGeneralTab.cs:124-145.
- **SET3.** Weather tab: stale `ActivePreset` id → header shows preset X while edits
  mutate preset 0 (SettingsWeatherTab.cs:93-110).
- **SET4.** Scrollbar: hot-highlight uses pre-drag rect; 5px hit overlap with content
  fields (SettingsWindow.cs:242-277).

### Map editor
- **M1.** World overlays (brush cursor, grass grid, collision, road/region overlays) are
  drawn **inside** the panel scissor clip (`BeginClip(tabContentRect)` :937 wraps all tab
  draws; DrawBrushCursor called at :1385/:1780/:2712/:2990, DrawRoadOverlays :3353,
  DrawRegionOverlays :3813, etc.) → invisible in the world / overdraw panel. Zones tab
  hoists its overlays out (:909-913 comment proves awareness); 6 older tabs never fixed.
  VERIFIED BY HAND (clip structure).
- **M2.** Units tab list cannot scroll: wheel writes `_tabScroll[7]`, tab reads
  `_unitListScroll` which is **never assigned** (:223, reads :4900/:5012).
- **M3.** `SelectedEnvDefIndex = -1` sentinel collides with group encoding `-(g+1)` →
  fresh Objects tab + world click places a random group-0 object (:118, :1946,
  :1974 — note the `< -0` typo).
- **M4.** "Clear All Units" — no confirm, no undo (:5037-5038).
- **M5.** Undo stack stores raw object indices that go stale after removals
  (:345-382); UndoUnitPlace removes the *last* unit blindly (:396-403).
- **M6.** Update duplicates Draw layout by hand with admitted drift ("rough layout
  calculation", Regions tab off-by-1px separator, Draw culls lists Update doesn't →
  divergent hitboxes on long lists) (:3437-3511, :3651, :3751).
- **M7.** Roads "New Pt Width" field passes constant `2f` and discards the return —
  does nothing (:3258, placement hardcodes 2f :3094).
- **M8.** Junction drag unreachable once a road was ever selected (`SelectedRoadIndex`
  never returns to -1, :3123); junction list rows not clickable (:3316-3324).
- **M9.** Save path: hardcoded `assets/maps` (:5109, :5290) contradicts GamePaths' own
  "now in data/maps/" comment (GamePaths.cs:99-101), CLAUDE.md, and the untracked
  `data/maps/` dir; assets/ is gitignored so map edits don't travel via git. Route
  through GamePaths.MapsDir + reconcile.
- **M10.** Clicking Save while a text field is active saves the pre-edit value
  (mouse path not gated on textEditing, :885).
- **M11.** Unit-def selection indexes the filtered list; changing the faction filter
  silently re-points it (:4962-4964). Hotkey leak: digits/Q/E/B fire while typing in a
  combo filter (gated only on IsTextInputActive which is false for combos, :701-767) —
  a typed digit switches tabs and strands an invisible open dropdown (layer 2 lock).

### UI editor
- **U1.** Undo/redo (`RestoreState` :1087-1105) never invalidates nine-slice instances or
  harmonized texture caches → Ctrl+Z visibly does nothing until an unrelated edit rebakes.
- **U2.** Silent `catch { }` on all three loaders (:410, :470, :617) + Save-All always
  writes all three files (:1332-1341) → a corrupt widgets.json becomes `{"widgets":[]}`
  on next Ctrl+S. **Data-loss hazard.** Surface load errors; refuse to save a file that
  failed to load.
- **U3.** ESC-close resets the wrong instance: `Game1.cs:833` calls
  `_editorUi.ResetAllState()` but the UI editor IS its own EditorBase (`_uiEditor`) —
  stale focus survives close/reopen and blocks Ctrl+S/undo/nav until a stray click.
- **U4.** Tab switch leaves the active field focused forever (handler :1247-1261 clears
  only combo ids) — same symptom as U3.
- **U5.** Rename orphans harmonized caches (`el:`/`bg:` keys by id; :1760, :2263) →
  preview silently falls back to raw texture; nine-slice rename invalidates the NEW id
  (no-op) and leaks the stale instance (:1467). Copy path has the fix (:2222-2226).
- **U6.** Nine-slice border/texture edits don't invalidate `_harmonizedNineSlices`
  (InvalidateNineSlice clears only `_nsInstances`, :1025-1032).
- **U7.** Save never reloads RuntimeWidgetRenderer (loaded once at Game1.cs:423) → in-game
  HUD keeps pre-edit definitions until restart. Add a reload-on-save.
- **U8.** Three path text fields discard their return value — typing does nothing
  (:1795 el_img, :2304 wd_bgimg, :2333 wd_stimg).
- **U9.** Widget-detail scroll extent hardcodes `+800` (:2628) → long child props
  unreachable / blank overscroll.
- **U10.** Harmonize live-regen throttle dead (`if/else` both zero the timer,
  :1188-1194) → full CPU rebake every mouse-move frame while picking.
- **U11.** Preview-Hide mode: draw uses collapsed rects, hit-test uses un-collapsed →
  clicking a drawn child selects a hidden one; resize handles float (:3180-3205, :3274).
- **U12.** Undo after save doesn't re-mark dirty (:1145-1161). `OverrideElement`
  invisible to override UI/green dot/Reset (:2937-3016). BG-image-only widgets get no
  tint/harmonize UI though runtime supports it (:2460). SaveNineSlices strips the
  `harmonize` property the runtime reads (latent data loss).

### Spell editor + previews
- **SP1.** Buff preview dirtied every frame (`SetBuff` in both branches,
  SpellEditorWindow.cs:853-863 — VERIFIED BY HAND) → `SyncOrbs` re-runs per frame:
  **orbital orbs never orbit** (reset to base angle each frame) and lightning arcs
  regenerate at framerate, making JitterHz dead (BuffPreview.cs:93-146, 588-614).
  Fix: dirty-on-edit via the MarkDirty override (which currently only touches
  `_spellPreview`, :1660-1664).
- **SP2.** Flipbook edits never reach the runtime `_flipbooks` dictionary (built once in
  StartGame, Game1.cs:1295-1306) → preview + game render stale flipbooks; "Apply &
  Close" applies nothing. Add a re-register on edit/save.
- **SP3.** "+ New" flipbook/buff ids are `prefix + Count` with no existence check +
  RegistryBase.Add is an upsert → after deletions, + New can silently **overwrite an
  existing def** (:715-725, :968-977).
- **SP4.** `SpellPreview.Resize` runs after `RenderToTarget` in the same frame → blank
  frame + RT/bloom churn every frame during a live resize (:1595-1611 vs :191-193).
- **SP5.** Minor: `Unload` leaks bloom renderer; `_elapsed` unbounded in BuffPreview;
  dead `_lastBuffPreviewIdx`/`IntBlendOptions`/`_listScrolls`; buff edits restart the
  unrelated spell preview.

### Unit editor
- **UN1.** Weapon-point edits: (a) enabled-but-inert when animationmeta exists (meta wins
  on read; UI writes dead data + sets dirty, :1230-1318); (b) writes keyed by raw editor
  yaw (30/60/300) while the runtime resolver reads the resolved sprite angle →
  edits invisible to the game on new-scheme sprites (WeaponPointResolver.cs:87-88).
- **UN2.** `GetFrameCountForCurrentAnim` (:1692-1713) uses the raw angle its three
  siblings were fixed to resolve → returns 0 on new-scheme atlases; "Set All Frames"
  writes a 1-entry duration list.
- **UN3.** Effect-ms "X" clear button drawn on top of the int field's "+" button — both
  fire on one click (:1635-1654); "-" with no override creates a 0ms override.
- **UN4.** `SyncPreviewToSelected` early-returns for sprite-less units → stale anim state
  and stale `_previewAnimName` keys timing edits of the new unit (:3596-3601).
- **UN5.** Minor: faction dots drawn outside list clip (:760-776); paste names
  "X (Copy) (Paste)"; `1f/60f` timer; status text overlaps Groups button; per-frame
  Dictionary alloc in BuildRuntimeTimingOverrides (:1150-1155).

### Env-object editor
- **E1.** Edge Tweaker: CWD-relative `File.Exists`/`File.Create(def.TexturePath)`
  (:2293, :2378 — bypasses GamePaths.Resolve; writes to wrong location from bin/Publish)
  AND saves the **premultiplied** texture back over the source PNG → soft edges darken
  progressively with each save cycle. **Asset corruption.**
- **E2.** Deleting a def: `RemoveAt` shifts all later DefIndex references → placed
  objects render wrong sprites; former-last index crashes (EnvironmentSystem.cs:1328
  unguarded); removed texture never disposed; `_corruptHarmonized` cache mis-keyed.
- **E3.** `CorruptedSprite` path edits don't invalidate the harmonized-corrupt bake
  (:1613-1626 — every other mutation path correctly calls ReloadDefTexture).
- **E4.** TintColor edits invisible in preview (preview draws Color.White, :631/:980).
- **E5.** Ctrl+S ignores the Corpse tab (HandleKeyboard always SaveDefs, :1834) while
  the Save button branches on mode — flashes success without saving corpse work.
- **E6.** ImGui-isms render literally: "Browse##corr" label drawn as-is (:1615);
  DrawCheckbox given an id string as its visible label (:1661).
- **E7.** `_edgeTweakerPreview` leaked on every open/reload; keystroke-level rebake churn
  on the texture path field; write-only preview state fields (:75-87).

### Item editor
- **I1.** No guard for confirm-dialog weakness (S1) + Ctrl+S/C/V fire behind the modal
  (:148-201 not gated on `_deleteConfirmOpen`).
- **I2.** Never calls `DrawColorPickerPopup` → latent invisible-modal soft-lock if any
  item/potion def gains an HdrColor (add the one line now).
- **I3.** Potion def created from an item never re-syncs name/icon on later rename
  (fields are [EditorHide] on PotionDef, :408-420).

### Shared popups
- **P1.** TextureFileBrowser filter field can never be focused: browser raises layer ≥1;
  `DrawSearchField` hard-gates on `!IsInputBlocked(0)` (TextureFileBrowser.cs:209,
  EditorBase.cs:1599). Dead feature; buttons got a layer-0 workaround, the field didn't.
- **P2.** Browser rows span the full popup width under the preview panel — invisible
  rows clickable there; scrollbar drawn at `px+PopupW-10` then painted over by the
  preview background → never visible (:215, :247, :296-304).
- **P3.** Browser traversal above root reachable (Open starts at current value's dir even
  outside root; `..` gated on `current != root`) + `GamePaths.MakeRelative` returns
  absolute paths unchanged when outside Root → **absolute paths persisted into JSON**
  (:89-91, :441-445, :348; GamePaths.cs:71).
- **P4.** Browser preview `_textureCache` never disposed — unbounded VRAM growth; stale
  after asset sync (:33, :381-394). Stale `_prevScrollValue` on Open → jump-scroll;
  offset not re-clamped on list shrink.
- **P5.** ColorPicker: ESC during eyedropper or while typing a value box cancels the
  ENTIRE popup and reverts (PopupManager routes ESC to OnCancel which ignores
  `_dropperActive`/`_editingField`, :208-216; in-popup ESC branches are dead).
- **P6.** Eyedropper samples the **unflushed** back buffer mid-HUD-pass → picks pixels
  of the invisible world behind the editor panels; full back-buffer readback every
  frame while active; first frame samples black (:231-245; EditorBase.cs:1567).
- **P7.** Picker value boxes: no select-all on focus (typing appends); commit-on-switch
  asymmetric (clicking an earlier-drawn box discards the edit, later-drawn commits)
  (:400-417). Hex field does select-all — inconsistent within the same popup.
- **P8.** Picker typing invisible to `IsTextInputActive` (private `_editingField`) →
  Ctrl+S etc. fire while typing in the picker.
- **P9.** WadingEditorPopup restores LinearClamp after its shader pass — HUD canonically
  runs PointClamp → blurry text for the rest of the frame (WadingEditorPopup.cs:430-433;
  use EffectBatch.BeginEffect/EndEffectResumeHud).
- **P10.** Picker (608px) taller than small windows → OK/Cancel unreachable, ESC-revert
  only exit; wading popup 900×600 unclamped.
- **P11.** DrawColorPickerPopup ownership scattered: double-drawn per frame with wall
  editor open (WallEditorWindow.cs:217 + MapEditorWindow.cs:1046 — input edges evaluated
  twice); UIEditor draws picker BEFORE browser/dropdowns (:1312 vs :1326) — inverted
  z-order; GameRenderer draws it only for Unit/Spell editors. Centralize.
- **P12.** TextureFileBrowser.Update ignores 4 of 5 params; every host maintains
  dedicated prev-state fields to feed it (dead plumbing in 5 editors).

### Framework (EditorBase/EditorWindow) misc
- **F1.** ESC while typing in a text field ALSO closes the whole editor: fields push no
  modal layer; PopupManager routes ESC to the editor layer's OnCancelAction while
  HandleTextInput independently deactivates the field — two actions on one ESC
  (EditorBase.cs:1995-2001; Game1.cs:830-836; comment at Game1.cs:3033-3040 is wrong).
- **F2.** Nested editors double-draw overlays: `_pendingDropdown` cleared only in
  UpdateInput → wall/env editor + map editor both call DrawDropdownOverlays (render-only
  double-draw; also picker double-draw P11).
- **F3.** `DrawSliderFloat` value box: no select-all on focus (EditorBase.cs:1698-1704)
  — the one EditorBase field violating the one-click-typable rule; heaviest exposure in
  harmonize sliders + settings tabs where identical-looking boxes behave differently.
- **F4.** `DrawTextArea`: no select-all, no click-to-position (cursor to end).
- **F5.** Int/float fields set select-all logically but never draw the selection
  highlight (only DrawTextField draws it) — user can't see it.
- **F6.** `EditorWindow.HandleStandardShortcuts` doc claims "Escape → WantsClose"; not
  implemented. Several windows check LeftControl only for Ctrl+S.
- **F7.** Two parallel color-edit paths: `DrawHdrColorField` (RGBA int rows) vs swatch→
  picker. Consolidate on the swatch.
- **F8.** Combo filter typing: no key-repeat (backspace); `IsTextInputActive` false for
  combos is load-bearing for hotkey gates (see M11) — consider `IsDropdownOpen` in gates
  or a combined `IsKeyboardCaptured` property.
- **F9.** No clipboard (Ctrl+C/V/X) in text fields at all — Ctrl+A exists. Windows:
  `System.Windows.Forms` unavailable; MonoGame `SDL_GetClipboardText` via
  `Sdl.ClipboardText` or TextInput event is the usual route. Nice-to-have.
- **F10.** ReflectionPropertyRenderer: display-name→id first-match in registry dropdown
  (:325-333); HdrColor `SetValue` every frame; `anyVisibleInGroup` dead logic;
  `_expandedSections` unused; no delete for created nested objects.

### What checked out FINE (don't re-litigate)
- Game↔editor boundary: world input is properly gated (`editorActive`, PopupManager,
  geometric checks). No editor→world click leaks found; camera-pan-while-typing refuted
  (one negligible frame). The real leaks are editor-internal (S1).
- DrawCombo/DrawDropdownOverlays: layout shared between click + render, flip-up,
  40% cap, scroll consume — solid. DrawScrollableList: clipped, click-clears-field,
  keyboard nav — solid.
- Fixed-timestep click-edge handling (`_drawSnapMouse`/EndDrawFrame) — correct and
  well-commented. Every editor calls EndDrawFrame/DrawDropdownOverlays exactly once.
- Spell editor data path: reflection panel + MarkDirty chain complete for spell edits;
  clone helpers thorough; preview renders into an RT (no bleed). SpellPreview clipping
  clean.
- Env editor texture reload/dispose chain (ReloadDefTexture) — no leak on re-bake.
- Save paths for settings correctly target gitignored `user settings/`.

---

## SUGGESTED FIX ORDER
1. **S1 input-layer contract** (pre-panel raise per window + `layer:` args in popups) —
   kills the biggest UX-visible class. Include W1 (wall editor un-deadening).
2. **S2 clip-aware IsHovered** (one framework change) + bound MapEditor Update hit-tests.
3. **Data-loss trio:** U2 (silent catch + save-all), E1 (edge tweaker premultiply/CWD),
   P3 (absolute paths). Plus SET1/SET2 (trivial).
4. **S3 field-id identity** (embed def id in prefixes; ClearActiveField on all selection
   paths).
5. **Preview refresh:** SP1 (buff preview dirty-on-edit), SP2 (flipbook runtime reload),
   U1/U5/U6 (central "def changed → invalidate caches for id" helper), E3, U7.
6. **S5 serialize-round-trip clones.**
7. **S4 press-origin capture** in DrawButton.
8. Decide wall-segment feature fate (W2). Map editor M1–M11 sweep.
9. Adoption/consolidation pass (S6): EditorWindow + EditorListState everywhere,
   settings tabs → reflection renderer, sub-editor triplication, browser → shared list
   widgets, picker → EditorBase fields.
