# UI Best-Practices Audit — Opportunities

Graded against the **`ui-widget-design`** skill (`.claude/skills/ui-widget-design/SKILL.md`).
Reproduce/track with **`python tools/ui_audit.py`**. Snapshot: 45 widgets, 329 elements,
15 nine-slices.

## Top-line state

The **live** UI is in good shape — the recent refactors landed: sub-widgets
(`SkillBookTab`, `GrimPathTab/SchoolTab`, `SpellSlot`, `EquipSlot`, `SkillTile`,
`GrimoireSpellTile`), horizontal/vertical layout groups (`TabBar`, `PathTabBar`,
`SchoolTabBar`, the `Unit*Section`/`*Box` vertical auto-size stacks), `SizeMode` fill,
and nine-slice frames. **0 exact-duplicate elements, 0 true orphans.**

The remaining problems are concentrated in **fable-import leftovers** that were never
cleaned up, plus a few live spots that missed the treatment. Ordered by impact ÷ risk:

---

## Tier 1 — Delete dead import widgets  ·  *redundancy*  ·  biggest win, lowest risk

`tools/ui_audit.py` proves these are unreachable from any rendered widget (no code ref,
not nested anywhere):

| widget | children | notes |
|---|---|---|
| `SummonWolvesTip` | 116 | spell-tooltip import, never wired |
| `ShadowBoltTip` | 53 | " |
| `SpiritFormTip` | 35 | " |

**161 elements exist *only* to serve these three** (the `SPT_*` family + per-size
`baked_RenaiThinFrame`/`dragonpattern` bakes). Deleting the 3 widgets + their 161
exclusive elements drops the element library **329 → ~168**. Git history + the
`importing-unity-ui` skill preserve the reproduction knowledge.
*Action:* remove the 3 widgets from `widgets.json`; run `ui_audit.py` and delete the
elements it then reports as orphan.

## Tier 2 — Resolve the reference-only `*Window` statics  ·  *flat / redundancy*

Kept as "import reference" but **not actually drawn** (the live versions are the `*Dyn`
widgets — confirmed in `UnitInfoPanel.cs:15`, `ResourceTooltip.cs:13`):

- `UnitTooltipWindow` (190 children, 187 elements flat) → superseded by `UnitSheetDyn`.
- `ResourceTooltipWindow` (22 flat) → superseded by `ResourceTooltipDyn`.

*Decision needed:* keep as on-disk reference, or archive/delete (git + the skill already
capture the import knowledge; the live `*Dyn` widgets are the working copy). If removed,
several more `*Window`-exclusive elements free up. Recommend **archive to a separate file
or delete** — a 190-child flat widget in the live file is the thing that makes the editor
list unsearchable.

## Tier 3 — Nine-slice the per-size-baked swatch families  ·  *nine-slice / redundancy*

Same art baked as **N separate PNGs at N sizes** — the canonical "should be one
nine-slice" anti-pattern. Families still used by **live** widgets:

- **`Swatch1.3`** — title swatch, **6** baked elements across 5 live unit-sheet sections
  (`UnitDescSection`, `UnitEquipmentTitle`, `UnitAttackTitle`, `UnitAbilitiesSection`,
  `UnitStatsGrid`) → one nine-slice (horizontal caps, like `ribbon6`), drawn at each
  width. Collapses 6 elements + 6 PNGs → 1 + 1.
- **`BlueSwath_row`** (3–4×) + **`BlueSwath_statbox`/`eqbox`/`atbox`/`abbox`** (1× each) —
  the stat/equip/attack/ability row + box backings, baked per size → one tiling nine-slice
  (`tileEdges:true`) drawn at each box size.
- `RenaiThinBar` (2×, `CB_TitleBar`/`SBT_7`).

The big reference-only families (`baked_RenaiThinFrame` ×15, `baked_dragonpattern` ×10,
`baked_BrightBlueSwathNoBG` ×7, `baked_Swatch1.3` ×4) **vanish with Tier 1/2** — don't
nine-slice them, just delete with their widgets. Note `RenaiThinBorder` nine-slice already
exists and is what the `RenaiThinFrame` bakes should have been.

## Tier 4 — Flatten-to-structure the live stat grid  ·  *flat / layout / size-inherit*

- **`UnitStatsGrid`** (56 flat element children, no layout, no sub-widgets) is **live**
  inside `UnitSheetDyn` — the one section that stayed flat while `UnitEquipmentBox` /
  `UnitAttackBox` already use the **`UnitStatRow` sub-widget**. *Action:* rebuild
  `UnitStatsGrid` as a **vertical layout** of `UnitStatRow` instances (per-row content via
  `overrideDefaultText`/`SetText`), matching its sibling sections. Removes ~40 elements.
- **`UnitStatRow`** itself (16 flat elements, no `SizeMode`) — a reused sub-widget, but its
  backing/swatch should `fill` (Tier 3 swatch) and it shares no label/value element across
  cells yet. Minor; do alongside Tier 3/4.

## Tier 5 — Cross-window shared-image de-dup  ·  *redundancy*  ·  lower value

Same source PNG wrapped by multiple element ids, differing only by harmonize/tint
(`ui_audit.py` "source images wrapped by >1 element"). Mostly **cross-window icons**:
`Chaos24`/`Nature24`/`Death24`/`Spirit24` etc. exist as both a spell-tile icon (`SBT_`/
`SWT_`) **and** a grimoire path-tab icon (`Grim_PathTab_*`) with different recolors.
- Where the recolor matches → collapse to one shared icon element (use `overrideElement`).
- Where recolors genuinely differ → that's the **per-element harmonize** limitation
  (harmonize is baked per element id). A *per-instance harmonize override* doesn't exist;
  adding one (mirroring `overrideElement`/`SetElementTint`) would let one icon element
  serve every recolor — worth it only if this de-dup is pursued broadly.
- `Parchment-2-Tile.png` (6× as `SBT_*`/`SWT_22` tab parchments) — most are in the
  Tier-1/2 dead/reference set and disappear with them.

## Tier 6 — Code & tooling  ·  *maintainability*

- **`UIEditorWindow.cs` (~3.5k lines)** — see `memory/ui_editor_streamline.md`: extract
  `GetHarmonizedOrOriginalTexture` (×4), `GetHarmonizedOrOriginalNineSlice` (×8),
  `DrawWidgetLayers` (dup), `UpdateTintFromSwatch` (×7); split into ~9 files. Do when next
  touching the editor substantially.
- **`tools/gen_grimoire_dyn.py` is stale** — writes the old `assets/UI/definitions/` paths
  and copies tab chrome wholesale (pre-sub-widget). Fix the paths + regenerate to the new
  sub-widget structure, or delete it (the grimoire is now maintained directly + via
  `tools/grimoire_tabs_to_subwidgets.py`).
- **Editor exposure of `overrideElement`** — the new per-instance element swap has no
  editor UI yet (data + render only). Add a child-panel control if hand-editing tabs/slots
  becomes common.

---

## Already good (don't touch — these ARE the standard)

`GrimoireDyn` + `PathTabBar`/`SchoolTabBar`/`GrimPathTab`/`GrimSchoolTab`; `SkillBookWindow`
+ `TabBar`/`SkillBookTab`/`SkillTile`; `SpellSlot`; `EquipSlot`; `UnitSheetDyn` + its
`Unit*Section`/`*Box` vertical auto-size stacks; `ResourceTooltipDyn`. These are the
reference implementations the skill points to.

## Suggested order

1. Tier 1 (delete 3 dead Tips + orphaned elements) — mechanical, ~halves the library.
2. Tier 2 (decide reference statics).
3. Tier 4 (`UnitStatsGrid` → rows) + Tier 3 swatch nine-slice together (same files).
4. Tier 5 / Tier 6 opportunistically.
