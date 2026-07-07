# Dossier: Editor immediate-mode widget toolkit duplicates

Concept unit: `editor-widget-toolkit`. Judged 2026-07-06 against working tree @ master (6784cf0).

Context that matters: the codebase already has TWO stated canonical homes for this space —
`Necroking/Render/DrawUtils.cs` ("Shared drawing primitives: circle outlines, lines, etc. Used by
editors, HUD, and gameplay rendering"; its `DrawRectBorder` doc comment says it "replaces ~13
per-file DrawBorder/DrawRectOutline copies") and `Necroking/Editor/EditorBase.cs` (the editor
widget toolkit, with an entry in `memory/standard_patterns.md` → "Editor UI (EditorBase)").
The rect-border consolidation was completed (all `DrawBorder`/`DrawRectOutline` wrappers found in
UI/, GameRenderer.Units.cs, MapEditorWindow.cs delegate to `DrawUtils.DrawRectBorder`). The line /
circle / ellipse primitives were never migrated the same way — that is the largest live finding.

---

## F1 — 2D shape primitives re-implemented ~17x despite DrawUtils being canonical
**Verdict: CONSOLIDATE — severity: medium — effort: M — risk: low (visual-only)**

`Render/DrawUtils.DrawLine` (1px + thickness overloads, `DrawUtils.cs:29,40`) and
`DrawUtils.DrawCircleOutline` (`DrawUtils.cs:14`) are the canonical rotated-quad line and
segment-circle. Verified verbatim/near-verbatim private copies:

Rotated-quad line (`Atan2` + stretch 1px pixel):
- `Necroking/Editor/EditorBase.cs:509` `DrawLine` (public; body duplicates DrawUtils thickness overload with origin `Vector2.Zero` instead of `(0,0.5)`)
- `Necroking/Editor/MapEditorWindow.cs:6764` `DrawLine` (uses `len < 1f` cutoff vs canonical `0.5f`)
- `Necroking/Editor/EnvObjectEditorWindow.cs:1999` `DrawLine` (same `1f` cutoff drift)
- `Necroking/Editor/WadingEditorPopup.cs:473` `DrawLine` (float-args clone of `EditorBase.DrawLine`; has `_ui` in scope — could already call `_ui.DrawLine`)
- `Necroking/Editor/SpellPreview.cs:907` `DrawThickLine` (vector-scale, centered origin)
- `Necroking/Editor/BuffPreview.cs:702` `DrawThickLine` (Rectangle-based int truncation — subtly different rasterization)
- `Necroking/GameRenderer.Units.cs:1439` `DrawThickLine` + `:1488` `DrawLine`
- `Necroking/Render/BuffVisualSystem.cs:669` `DrawThickLine` (SpriteBatch; implicit SpriteBatch→SpriteScope conversion exists per standard_patterns.md, so DrawUtils works here)
- `Necroking/Render/DebugDraw.cs:385` `DrawLine`

Segment circle/ellipse outline:
- `Necroking/Editor/MapEditorWindow.cs:6775` `DrawCircleOutline` + `:3026` `DrawEllipseOutline`
- `Necroking/Editor/EnvObjectEditorWindow.cs:1986` `DrawCircleOutline`
- `Necroking/Editor/SpellPreview.cs:918` `DrawCircleOutline` (NOTE: multiplies Y by `CameraYRatio` — actually an iso ellipse; keep the ratio at the call site)
- `Necroking/Editor/BuffPreview.cs:673` `DrawEllipseOutline` + `:687` `DrawCircleOutline` (adaptive segment count `Max(12, r*2)`)
- `Necroking/GameRenderer.Units.cs:1422` `DrawEllipseOutline` (N=30, thickness)
- `Necroking/UI/BuildingMenuUI.cs:461` already calls `DrawUtils.DrawCircleOutline` — proof the migration pattern works.

Also `BuffPreview.cs:644 DrawFilledCircle` / `:658 DrawFilledEllipse` — the only filled-shape
scanline implementations; circle is the ellipse with rx==ry (internal pair, unify locally or move
to DrawUtils).

**Canonical design** (extend `Render.DrawUtils`):
```csharp
public static void DrawEllipseOutline(SpriteScope b, Texture2D px, Vector2 c,
    float rx, float ry, Color col, float thickness = 1f, int segments = 0) // 0 = auto Max(16, rx+ry)
public static void DrawFilledEllipse(SpriteScope b, Texture2D px, Vector2 c, float rx, float ry, Color col)
// DrawCircleOutline gains thickness; delegates to ellipse with rx==ry
```
Migration: follow the established `DrawRectBorder` precedent — each class keeps (or deletes) a
one-line private wrapper that delegates to DrawUtils; `EditorBase.DrawLine` keeps its public
signature but delegates. SpellPreview keeps `CameraYRatio` by calling the ellipse form with
`ry = radius * CameraYRatio`. Watch two sub-pixel diffs when unifying: `len < 1f` vs `0.5f`
cutoffs, and Rectangle-int vs vector-scale thickness (BuffPreview) — pick the vector-scale
canonical; differences are sub-pixel. Constraint check: all of these draw through
Scope/SpriteScope already, so the submit→sort→batch / straight-alpha rules are unaffected; no
Net/ involvement.

---

## F2 — DrawSectionHeader implemented 6x (evidence said 4; actual is 6)
**Verdict: CONSOLIDATE — severity: low — effort: S — risk: low (cosmetic)**

- `Necroking/Editor/SettingsGeneralTab.cs:204` and `SettingsHordeTab.cs:75` — **byte-identical** statics (1px divider + accent text).
- `Necroking/Editor/ItemEditorWindow.cs:576` — same divider style, caller-supplied color.
- `Necroking/Editor/UnitEditorWindow.cs:3585` — filled `PanelHeader` bar + bright text.
- `Necroking/Editor/MapEditorWindow.cs:6727` — filled `HeaderBg` bar + small text (fixed `PanelWidth`).
- `Necroking/Editor/SettingsWindow.cs:755` — bare uppercase accent label, no rule.

Same intent everywhere: "titled section divider that advances a ref y". Three visual styles exist
(rule / filled bar / bare label) — that's data-level variance, not structural.

**Canonical design** (on `EditorBase`):
```csharp
public enum SectionHeaderStyle { Rule, Bar, Label }
public void DrawSectionHeader(string text, int x, ref int y, int w,
    SectionHeaderStyle style = SectionHeaderStyle.Rule, Color? color = null)
```
Call-site categories: ~10 in MapEditorWindow, ~10 in UnitEditorWindow, 2 in ItemEditorWindow,
12 in SettingsGeneralTab, 3 in SettingsHordeTab, 12 in SettingsWindow (all mechanical renames;
the two settings-tab statics can be deleted outright). Whether the three styles should stay three
is a user-facing consistency question per CLAUDE.md ("check with the user" on UI standards) — but
consolidating the mechanics is safe either way.

## F3 — EditorBase single-line field family duplicates focus/deactivate boilerplate 4x, with real behavior drift
**Verdict: CONSOLIDATE — severity: medium — effort: M — risk: medium (input behavior)**

`Necroking/Editor/EditorBase.cs`: `DrawTextField` (:955), `DrawIntField` (:1023),
`DrawFloatField` (:1077), `DrawSearchField` (:1772) — plus the value-box inside
`DrawSliderFloat` (:1856) — each hand-roll the same activate-on-click /
deactivate-on-outside-click / DrawRect+DrawBorder+DrawFieldContent sequence around the shared
`FocusTextField` helper. The drift is already user-visible: **only `DrawTextField` supports
click-to-position-cursor and drag-selection** (:979-1002); clicking inside an active
int/float/search field re-select-alls or does nothing. That's exactly the "fix a bug in one
place" failure CLAUDE.md warns about.

**Canonical design**: one private core owning click/drag/deactivate/draw:
```csharp
private (string text, bool active, bool committed) FieldCore(
    string fieldId, string display, Rectangle inputRect, string? placeholder = null)
```
Public wrappers keep their exact signatures: text = core verbatim; search = core with
placeholder + no label; int/float = core + TryParse + stepper buttons (parse-type and step
rounding stay in the wrappers — that's the data-level variance). Int/float steppers differ
intentionally (±1 vs step-multiple rounding); keep per-wrapper. Effort M because the field-modal
interactions (`_textFieldLayer`, select-all semantics in standard_patterns.md) must be preserved;
verify with the editor open via drive-game.

Note: the labeler framed this as two pairs (text/search, int/float); the real shape is one
4-way family with a shared core.

## F4 — DrawBrushSizeControl vs DrawAutoGroundSizeControl: identical stepper
**Verdict: CONSOLIDATE — severity: low — effort: S — risk: low**

`Necroking/Editor/MapEditorWindow.cs:6577` vs `:6602` — line-for-line identical (-/+ buttons,
popup-blocking gate, clamp 0..20) except label prefix and value binding. The duplicated clamp
constants WILL diverge silently. Same-file fix:
```csharp
private int DrawStepperRow(string label, int value, int panelX, int y, int min = 0, int max = 20)
```
Callers: 4x `DrawBrushSizeControl` (assign to `BrushRadius`), 1x auto-ground (`s.Size`).

## F5 — Env def category/group distinct-queries duplicated across two windows
**Verdict: CONSOLIDATE — severity: low — effort: S — risk: low**

Same core scan ("distinct Category / distinct non-empty Group over env defs") written 5x:
- `Necroking/Editor/MapEditorWindow.cs:6901 GetEnvCategories` ("All" + cats + "Groups" sentinel)
- `MapEditorWindow.cs:6936 GetEnvGroups` (insertion order)
- `Necroking/Editor/EnvObjectEditorWindow.cs:2014 GetCategories` ("All" + cats)
- `EnvObjectEditorWindow.cs:2027 GetExistingGroups` (sorted)
- `EnvObjectEditorWindow.cs:2043 GetExistingCategories` (sorted, "Misc" fallback)

Right split per CLAUDE.md consolidation design: the env system owns the query, callers own the
decoration. Add to the environment system (the `_envSystem`/`_env` object):
```csharp
public List<string> DistinctCategories(); public List<string> DistinctGroups();
```
Callers keep their "All"/"Groups"/"Misc" sentinels, sorting, and ordering locally. Sorted-vs-
insertion-order is intentional UI ordering — leave at call sites.

## F6 — HandlePanelScroll overload pair
**Verdict: KEEP_SEPARATE — severity: low**

`Necroking/Editor/EditorBase.cs:109` vs `:138` — the id-keyed overload computes `maxScroll` from
the previous frame's cached content height and **delegates** to the base overload (:143). This is
layering, not duplication; the comment block (:123-127) documents why both exist. Labeler
over-matched.

## F7 — SettingsGeneralTab.Draw three overloads
**Verdict: KEEP_SEPARATE — severity: low**

`Necroking/Editor/SettingsGeneralTab.cs:20/25/30` — a delegation chain (each shorter overload
calls the fullest). No duplicated logic. Only the 3-arg-fullest form is called
(`SettingsWindow.cs:418`); the two shorter overloads are dead convenience code. Optional cleanup:
delete them or use optional parameters — a 5-minute dead-code trim, not a consolidation.

## F8 — RgbToHsv/HsvToRgb "in BOTH ColorHarmonizer and ColorPickerPopup"
**Verdict: KEEP_SEPARATE — severity: low — evidence is FALSE**

Single implementation: `Necroking/Editor/ColorPickerPopup.cs:114/142` (public static).
`ColorHarmonizer.cs:60-67` **calls** `ColorPickerPopup.RgbToHsv/HsvToRgb` — no second copy exists
anywhere in the project (grep verified). Placement nit only: general color math living on a popup
class would sit better in `Core.ColorUtils`, but that's a move, not a dedup.

## F9 — KeyToHexChar vs KeyToNumericChar
**Verdict: KEEP_SEPARATE — severity: low**

`Necroking/Editor/ColorPickerPopup.cs:470/479` — two ~8-line switches with genuinely different
character sets (hex: 0-9 + A-F, period blocked; numeric: 0-9 + '.' + '-') and different semantics
from the general `EditorBase.KeyToChar(key, shift)` (:2318, shift/lowercase aware). Merging into
one mode-flagged helper trades two trivially-readable tables for a conditional — a framework, not
a utility. The D0-D9/NumPad translation overlap is 4 lines.

---

## Priority order for a fixer
1. **F1** (DrawUtils primitives) — biggest count, canonical home already exists, precedent (`DrawRectBorder`) proves the wrapper-delegation migration is safe.
2. **F3** (EditorBase field core) — fixes real UX drift (cursor positioning in numeric/search fields), but needs interactive verification.
3. **F2** (DrawSectionHeader) — pure mechanical; ask user whether to also unify the 3 visual styles.
4. **F4, F5** — small same-session wins.
F6-F9: no action (F7 optionally delete dead overloads).
