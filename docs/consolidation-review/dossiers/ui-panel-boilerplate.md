# Dossier: Game UI panel/window boilerplate (HUD panels, menus, tooltips)

Judge pass over the labeling-pass evidence for the "UI panel/window boilerplate" concept.
All line numbers verified against working tree 2026-07-06.

## Context: existing standards (verified)

- **`Necroking/UI/PopupManager.cs`** — `IModalLayer` (ContainsMouse / OnCancel / LightDismiss /
  IsBlocking / CloseOnOutsideClick / HitBounds) + modal stack + ESC/click routing. Doc comment
  explicitly scopes it: *"The interface stays small on purpose"*, and explains why Draw is NOT
  centralized (panels draw at different pipeline sites). This is the deliberate consolidation
  of the "lifecycle|window" cluster.
- **`Necroking/UI/UIHitRegistry.cs`** — central per-frame catalogue of UI hit rects;
  `MouseOverUI` derived in one place (`Game1.RebuildUIHitRects`). Also already standard.
- **`HUDRenderer.DrawCursorTooltip`** (HUDRenderer.cs:873) — shared plain cursor tooltip
  (auto-size, edge flip, bg+accent). Five builders feed it.
- **`UI/ResourceTooltip.cs` + `UI/StatTooltips.cs`** — widget-based (ResourceTooltipDyn)
  title/rows/desc tooltip binder used by the unit sheet; `StatTooltips.Place` has the
  canonical `+16/+20 flip/clamp-4` cursor placement.
- **`Render/DrawUtils.DrawRectBorder`** — shared rect border (used by BuildingMenuUI,
  CraftingMenuUI, TableCraftMenuUI via 1-line `DrawBorder` shims — already reuse).

---

## Finding 1 — BuildingMenuUI vs CraftingMenuUI: parallel side-list menu skeleton
**Verdict: CONSOLIDATE · Severity: medium · Effort: M · Risk: low-medium**

`Necroking/UI/BuildingMenuUI.cs` and `Necroking/UI/CraftingMenuUI.cs` are the same class
written twice with different item data:

| Member | BuildingMenuUI | CraftingMenuUI | Delta |
|---|---|---|---|
| `Init` (widget def, stretch to screenH) | :57-79 | :65-82 | none |
| `Open` (x=-12, y=0, re-stretch, cache ids, EnsureItemChildren, SyncItems) | :81-114 | :89-130 | item source only |
| `Close`/`Toggle`/IModalLayer props | :116-139 | :132-150 | none |
| `EnsureItemChildren` (template scan, RemoveAll, re-add N children) | :142-177 | :152-184 | child-name prefix, count source |
| `SyncItems` cost bindings (`child_0`/`Quant1`/`Icon1`/`Quant2`/`Icon2`) | :180-240 | :186-247 | data source (env def costs vs recipe) |
| `CanAfford` | :243-256 | :249-258 | cost shape |
| `Update` click loop (menuRight guard, ComputeItemRects, Contains, CanAfford gate) | :274-320 | :287-326 | click action (placement vs craft) |
| Hover highlight `(255,255,255,26)`, selected border `(100,255,100,80)`, can't-afford dim `(0,0,0,80)` | :393-428 | :359-397 | identical colors, hand-synced |
| `ContainsMouse`/`HitBounds` | :467-475 | :502-510 | none |
| `ComputeItemRects` (re-derives the widget vertical layout: padL/padT/spacY walk) | :481-504 | :512-533 | count source only |

`ComputeItemRects` is the bug carrier: both copies **re-implement RuntimeWidgetRenderer's
vertical layout math** (LayoutPadLeft/Top fallback to LayoutPadding, SpacingY fallback to
Spacing). A layout-engine change, or a fix applied to one menu, silently desyncs hit rects
in the other. The overlay colors are also duplicated and comment-free.

**NOT a third copy:** `Necroking/UI/TableCraftMenuUI.cs` was claimed as one by the labeler;
verified it is structurally different — a world-anchored, camera-zoom-scaled popover
(`RepositionAboveTable` :206, `Scaled()` :92, slot-row layout :358-406) with no
EnsureItemChildren/SyncItems/ComputeItemRects at all. Only the trivial
ContainsMouse/HitBounds/DrawBorder shims overlap. Keep it separate.

**Canonical design** — new `Necroking/UI/SideListMenu` (shared component owns mechanics,
callers own data, per CLAUDE.md consolidation design):

```csharp
abstract class SideListMenu : IModalLayer {           // owns: open/close/toggle, x=-12 anchor,
    protected abstract int ItemCount { get; }         //   stretch-to-screenH, EnsureItemChildren,
    protected abstract void BindItem(string subId, int i);   // ComputeItemRects, click hit-test,
    protected abstract bool CanAfford(int i);         //   hover/selected/dim overlays,
    protected abstract void OnItemClicked(int i);     //   ContainsMouse/HitBounds
    protected virtual void DrawItemExtras(Rectangle r, int i) {}  // craft progress bar hook
}
```
BuildingMenuUI keeps: placement mode, ghost preview, TryPlace, glyph branch.
CraftingMenuUI keeps: craft-progress state machine, CompleteCraft, potion tooltip.

**Call sites to migrate:** Game1 init (`EnsureInventoryUIsInitialized`), toggle handlers,
DrawHUD draw calls, MouseOverUI block — all unchanged if the two classes keep their public
API (`Open/Close/Toggle/ContainsMouse/HitBounds/IsVisible`) and inherit the base.
Risk: widget child-index bookkeeping (`_itemChildIndices`) must stay per-instance; test both
menus open/click/afford paths via drive-game.

---

## Finding 2 — Rich cursor tooltip skeleton x3 (+ WrapText x3, PlaceTip x4, palette x3)
**Verdict: CONSOLIDATE · Severity: medium · Effort: M · Risk: low**

Three hand-rolled "title + wrapped description + divider + right-aligned (label, value,
color) rows, cursor-anchored with edge flip" tooltips:

- `CraftingMenuUI.DrawPotionTooltip` — CraftingMenuUI.cs:408-475
- `InventoryUI.DrawItemTooltip` — InventoryUI.cs:287-352 (comment at :43 admits the
  duplication: *"Tooltip styling (matches crafting menu / character stats tooltips)"*)
- `CharacterStatsUI.DrawStatTooltip` — CharacterStatsUI.cs:578-632

Duplicated pieces, verified byte-similar:
- **Greedy word-wrap**: `CraftingMenuUI.WrapText` :478-500 and `InventoryUI.WrapText`
  :364-386 are **identical** (widget-font measure); `CharacterStatsUI.WrapText` :635-657 is
  the same algorithm over `SpriteFont`. (`EditorBase.WrapText` :1998 and
  `WidgetLayoutUtils.WrapText` :19 are two more wrap implementations over other font types.)
- **Placement**: `+16/+20, flip at edge, clamp 4` appears 4x — `CharacterStatsUI.PlaceTip`
  :660-666, `StatTooltips.Place` :131-137, inline in CraftingMenuUI :446-450 and
  InventoryUI :316-320.
- **Palette**: `TipBg(20,20,32,245)`, `TipBorder(120,120,170,240)`, `TipTitle(255,220,140)`
  etc. declared 3x (InventoryUI:44-51, CraftingMenuUI:43-51, CharacterStatsUI:216+). Any
  restyle must be hand-applied in three files or the tooltips visibly diverge.
- `InventoryUI.DrawTipBorder` :354-361 additionally hand-rolls what
  `DrawUtils.DrawRectBorder` already does (micro reuse fix, fold into the same change).

**Canonical design** — new `Necroking/UI/RichTip.cs` (static, primitive-drawn, next to
StatTooltips/ResourceTooltip):

```csharp
static class RichTip {
    record struct Row(string Label, string Value, Color Color);   // == ResourceTooltip.Row / TipLine
    static (int x, int y) Place(int mx, int my, int w, int h, int sw, int sh);
    static List<string> Wrap(Func<string, float> measure, string text, float maxW);
    static void Draw(SpriteScope scope, Texture2D pixel, ITipText text,  // measure+draw adapter:
        string title, string? subtitle, List<string> descLines,          //   SpriteFont impl +
        IReadOnlyList<Row> rows, int mx, int my, int sw, int sh);        //   RuntimeWidgetRenderer impl
    // palette constants live here, once
}
```
The only structural variance is the text backend (SpriteFont vs widget font size) — a
2-method adapter, not a framework. Design note: migrating these three to the
`ResourceTooltipDyn` widget (the other existing standard) was considered but changes
visuals and adds widget-instance plumbing; the primitive helper preserves pixels exactly.

**Call sites:** the three Draw*Tooltip methods (each keeps its own line-building — potion
ingredients w/ have/need coloring, item quantity/stack, stat buff breakdown) + delete
3 WrapText, 2 inline placements, 2 palette blocks. `StatTooltips.Place` can delegate too.

---

## Finding 3 — HUDRenderer button-row triple x2 (menu row vs editor row)
**Verdict: CONSOLIDATE · Severity: low · Effort: S · Risk: low**

`Necroking/UI/HUDRenderer.cs`: `LayoutMenuButtons`/`HitTestMenuButtons`/`DrawMenuButtons`
(:253-304) vs `LayoutEditorButtons`/`HitTestEditorButtons`/`DrawEditorButtons` (:309-360)
— verified near-verbatim copies (~55 lines x2). Deltas are pure data: label array, count,
top-Y constant, target rect array, and the two hover/open color palettes (blue vs rust).
The Layout+HitTest pairing is already the right pattern ("shared by draw + hit-test so they
never desync" — per-copy), but the two copies must be hand-synced with each other, and
:61 carries a "keep order in sync" comment. `AppendHitRects` (:654-658) also walks both.

**Canonical design** — private helper in the same file (small consolidation, refactor in
place per CLAUDE.md):

```csharp
private readonly struct ButtonRowStyle { /* openBg, hoverBg, idleBg, accentOpen, accentIdle, labelOpen, labelIdle */ }
private bool LayoutButtonRow(string[] labels, int top, int screenW, Rectangle[] rects);
private int  HitTestButtonRow(Rectangle[] rects, int mx, int my);
private void DrawButtonRow(string[] labels, Rectangle[] rects, int openMask, int mx, int my, in ButtonRowStyle style);
```
Public `HitTestMenuButtons`/`HitTestEditorButtons` stay as 1-line wrappers (Game1.cs:3030,
:3663 unchanged). Cosmetic-risk only; both rows visible on every HUD frame so drive-game
screenshot verifies.

---

## Finding 4 — HUDRenderer cursor-tooltip builders (Object/Belly/Corpse/Unit/SpellSlot)
**Verdict: KEEP_SEPARATE · Severity: low**

The labeler called `DrawObjectTooltip`/`DrawBellyTooltip`/`DrawCorpseTooltip`/
`DrawUnitTooltip` (HUDRenderer.cs:671, :748, :770, :790; plus `DrawSpellSlotTooltip` :833)
"four same-skeleton tooltips". Verified: the skeleton is **already extracted** — every one
builds a `string[]` from a different data domain (env objects w/ building/bush/foragable
branches; boar belly; corpse; unit membership; spell slot) and calls the shared
`DrawCursorTooltip(lines, sw, sh)` :873, which owns all mechanics (measure, flip, clamp,
bg, accent, text). The remaining per-method code is exactly the data the caller should own
(CLAUDE.md: don't abstract structural variance in line-building; the shared component
already owns the mechanics). This is the model consolidation, not a defect.

---

## Finding 5 — Panel lifecycle cluster (Open/Close/Toggle/ContainsMouse across 18 files)
**Verdict: KEEP_SEPARATE · Severity: low**

The n=48 "lifecycle|window" cluster (GrimoireOverlay, SkillBookOverlay, UnitInfoPanel,
InventoryUI, JobBoardUI, GraveRosterUI, MultiplayerWindow, …) is already governed by the
existing standard: `IModalLayer` + `PopupManager` (stack, ESC, click consumption,
light-dismiss/blocking semantics) + `UIHitRegistry` (central hit rects, single MouseOverUI
derivation) + `ActionModalLayer` adapter for flag-only popups. What remains per panel is
~10-15 lines of `_visible` flag + Push/Pop + a bounds rect — deliberate per the PopupManager
doc ("interface stays small on purpose"; draw sites intentionally distributed). Docs
(docs/locate-behavior/ui.md :170-215) already track the implementer list and the one known
wart (stale-rect hit tests vs the `GrimoireOverlay.Layout` clean pattern). Forcing a shared
panel base here would be a framework over structural variance (widget-backed vs primitive,
screen-anchored vs world-anchored vs transient). No action.

---

## Finding 6 — SkillBookOverlay SpellName/ItemName/UnitName/BuffName
**Verdict: KEEP_SEPARATE · Severity: low**

SkillBookOverlay.cs:744-747 — four **one-line** adapters, each over a different registry
type (`Spells`/`Items`/`Units`/`Buffs`), and not even uniform (`BuffName` falls back to
`Humanize(id)`, the others to the raw id). Consolidating requires an `IHasDisplayName`
interface across four def types (or reflection) to save three lines. Over-matched by the
labeler; the composition point (`NameList(csv, map)` :749) already reuses them properly.
The broader `DisplayName.Length > 0 ? DisplayName : id` idiom appears only ~4 more times
codebase-wide — below consolidation threshold.

---

## Finding 7 — CharacterStatsUI MakeBuffedRow / MakeBuffedRowF int/float twins
**Verdict: KEEP_SEPARATE · Severity: low**

CharacterStatsUI.cs:932-961. Verified: the twins already share the heavy lifting
(`BuffSystem.GetModifiedStat`, `AddBuffLines(..., isFloat)`); what differs between them is
the entire remaining body — int rounding + exact compare + `ToString()` vs `F1` format +
epsilon compare. Merging behind an `isFloat` flag would thread formatting conditionals
through every line for a ~10-line saving in one file, two adjacent private methods. Below
the value bar; acceptable as-is (they mirror `AddBuffLines`' existing isFloat split).

---

## Suggested order of work
1. Finding 3 (S, in-place, zero API change) — do opportunistically.
2. Finding 2 (M) — new `UI/RichTip.cs`; deletes ~120 duplicated lines across 3 files,
   removes the hand-synced palette.
3. Finding 1 (M) — `SideListMenu` base; the `ComputeItemRects` desync risk is the real
   payoff. Coordinate with any future third side menu (that's the trigger if deferred).

Constraint check: nothing here touches `Necroking/Net/`; all draw code already goes through
`SpriteScope`/`Scope` accessors (keep that in the shared helpers); no map-content or wire
format implications.
