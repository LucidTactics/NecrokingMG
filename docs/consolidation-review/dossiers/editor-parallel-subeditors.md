# Dossier: Editor parallel sub-editor families (weapon/armor/shield, spell/buff, ui-tabs)

Judge pass over the labeling-pass evidence. All line numbers verified against the working
tree on 2026-07-06. Verdicts follow CLAUDE.md "Consolidation Design": shared component owns
mechanics (list, scroll, hit-test, CRUD flow); caller owns data (which registry, which
fields). Structural variance is not abstracted.

Registry API note (load-bearing for F1): every registry involved shares the full
`RegistryBase<TDef>` surface — `Get / GetIDs / Count / Add / AddAfter / Remove / CloneDef /
Save` (`Necroking/Data/Registries/RegistryBase.cs:15-195`) with `TDef : class, IHasId, new()`.
This is what makes a generic scaffold clean rather than a framework.

---

## F1. CONSOLIDATE (medium): Registry list+detail+CRUD sub-editor scaffolding — 5 instantiations

**Claim:** one intent ("browse a registry, pick an entry, edit fields, New/Copy/Delete/Save")
implemented 5 times with only data-level variance (which registry, id prefix, save path,
delete-guard, detail form).

**Instantiations verified:**

1. Weapon — `Necroking/Editor/UnitEditorWindow.cs`
   - `DrawWeaponSubEditor` (2916-2960): filter loop, `DrawScrollableList`, selection remap,
     divider, clip, detail dispatch.
   - CRUD case `SubEditor.Weapon` in `DrawSubEditorCrudButtons` (3385-3448): +New with
     `"weapon_" + HHmmss` id, Copy via `CloneWeapon` + `_copy` uniquing + `AddAfter`,
     Delete gated on `CountUnitsWithWeapon`, Save to `data/weapons.json`.
   - Ctrl+C block (377-385) / Ctrl+V block (423-434) using `_clipboardWeapon`.
   - `BuildWeaponDropdownLists` (3715-3728).
2. Armor — same file: `DrawArmorSubEditor` (3182-3225), CRUD case (3450-3512),
   Ctrl+C (386-394) / Ctrl+V (435-446), `BuildArmorDropdownLists` (3730-3743).
3. Shield — same file: `DrawShieldSubEditor` (3286-3329), CRUD case (3514-3577),
   Ctrl+C (395-403) / Ctrl+V (447-458), `BuildShieldDropdownLists` (3745-3758).
4. Buff — `Necroking/Editor/SpellEditorWindow.cs` `DrawBuffManagerPopup` (893-1043):
   modal chrome, hand-rolled scrolled list (946-976), New/Copy/Del/Apply&Close row
   (982-1031), detail dispatch.
5. Flipbook — same file, `DrawFlipbookManagerPopup` (628-838): byte-parallel to the buff
   popup (chrome 635-659, list 686-714, buttons 720-774, inline detail 776-831).

The three `DrawXSubEditor` bodies are **byte-identical** except for the registry
(`_gameData.Weapons/Armors/Shields`), the list id string, and the `DrawXDetail` call
(diff-verified by reading 2916-2960 vs 3182-3225 vs 3286-3329). The three CRUD switch
cases (~63 lines each) differ only in registry, id prefix, `CountUnitsWith*` call, and
save path. The three `BuildXDropdownLists` differ only in registry. The two
SpellEditorWindow popups differ from each other only in popup size, delete-guard style,
and detail body.

**Divergence already observed (this is why severity is medium, not low):**
- Buff list sets `_ui.SetMouseOverUI()` on hover (SpellEditorWindow.cs:957); the flipbook
  list (694-710) does not — same widget, drifted behavior.
- UnitEditor sub-editors use the shared `DrawScrollableList`; the two SpellEditor popups
  hand-roll the identical clipped scroll-list loop instead — the mechanics were
  re-implemented rather than reused.
- Flipbook Copy is a hand-written field-by-field clone (see F2) while the other four use
  `CloneDef` — exactly the drift class the repo's own comment at UnitEditorWindow.cs:3792
  says these parallel families produce ("CloneWeapon lost the entire pounce/trample/sweep
  archetype block").
- The "abandon active field on selection change" guard exists in the buff/flipbook lists
  (`ClearActiveField()` at 708, 970) but not in the weapon/armor/shield list handlers
  (2939-2943 just set `_subSelectedIdx`) — the wrong-object-commit bug documented in
  docs/locate-behavior/editor.md is fixed in 2 of 5 copies.

**Proposed canonical design** (new file `Necroking/Editor/RegistryCrudPanel.cs`):

```csharp
class RegistryCrudPanel<TDef> where TDef : class, IHasId, new() {
    // caller supplies: RegistryBase<TDef> registry, string panelId, string idPrefix,
    //   string savePath, Func<TDef,string> displayName,
    //   Action<TDef,int,int,int,int> drawDetail,            // caller-owned data/form
    //   Func<string,int> countReferences = null,            // delete guard (null = none)
    //   Action<TDef> onCopied = null                        // e.g. RegenerateAllHarmonized
    // owns: search filter, filtered list + DrawScrollableList, selection index,
    //   ClearActiveField-on-selection-change, +New/Copy/Delete/Save row (CloneDef,
    //   _copy/_paste uniquing loop, AddAfter), optional Ctrl+C/V clipboard.
}
```

Detail forms (`DrawWeaponDetail`/`DrawArmorDetail`/`DrawShieldDetail`, `DrawBuffDetail`,
the flipbook field block) stay exactly where they are — they are the caller-owned data.

**Call sites to migrate:** (a) UnitEditorWindow `switch(_activeSubEditor)` dispatch at
2900-2905 + `DrawSubEditorCrudButtons` + the Ctrl+C/V blocks + confirm-delete dispatch at
586-610 + the three `BuildXDropdownLists`; (b) SpellEditorWindow buff popup; (c)
SpellEditorWindow flipbook popup (keeps its texture-browser hook via the detail callback).
ItemEditorWindow's item list (`DrawItemList`/`DrawDetailPanel`) is a 6th candidate but has
a merged items+potions view — migrate opportunistically, not as part of the first pass.

**Effort:** M (one new ~200-line class, three windows edited; editor-only, verifiable live
via /drive-game by opening each editor and exercising New/Copy/Delete/Save).
**Risk:** low — no gameplay code, no Net/, no renderer semantics; the overlay/scroll
mechanics being unified are already the standard_patterns.md "Editor UI" contracts.

---

## F2. CONSOLIDATE (medium): Flipbook Copy hand-clones fields instead of CloneDef

`SpellEditorWindow.cs:747-751`:

```csharp
var copy = new FlipbookDef {
    Id = newId, DisplayName = src.DisplayName + " (Copy)",
    Path = src.Path, Cols = src.Cols, Rows = src.Rows, DefaultFPS = src.DefaultFPS
};
```

Covers all `FlipbookDef` fields *today* (FlipbookRegistry.cs:5-13), but directly violates
the repo standard (memory/standard_patterns.md → "NEVER write field-by-field clone
functions") that every sibling already follows (`CloneSpell`/`CloneBuff` at
SpellEditorWindow.cs:1734-1746, `CloneWeapon/Armor/Shield/Unit` at
UnitEditorWindow.cs:3800-3810, `ItemEditorWindow.cs:600` — all `registry.CloneDef`).
First new FlipbookDef field silently stops surviving Copy.

**Fix:** one-line — `var copy = _gameData.Flipbooks.CloneDef(src, newId); copy.DisplayName
= src.DisplayName + " (Copy)";` (falls out for free if F1 lands). Effort S, risk nil.

---

## F3. KEEP_SEPARATE (low): Clone* wrapper "triples" — evidence over-matched

`CloneWeapon/CloneArmor/CloneShield` (UnitEditorWindow.cs:3803-3810) and
`CloneSpell/CloneBuff` (SpellEditorWindow.cs:1734-1746) are already one-line delegations
to the canonical `RegistryBase.CloneDef` (RegistryBase.cs:67), with an explanatory comment
block (3792-3798) documenting the past consolidation. The labeling pass matched the names,
not duplication. Nothing to do (F1 would absorb the wrappers anyway).

---

## F4. CONSOLIDATE (low): SpellPreview five age/expire/remove loops

`Necroking/Editor/SpellPreview.cs:1150-1223` — `UpdateZaps/UpdateBeams/UpdateDrains/
UpdateEffects/UpdateHitEffects` are five copies of the same 12-line pattern; the only
variance is the list and the timer field names (`Timer/Duration` vs `Elapsed/MaxDuration`).
Each copy also contains a **redundant second removal pass** (e.g. 1161-1162) — the first
loop already removes dead entries — duplicated dead weight five times.

**Fix (S):** give the five element types a tiny shared shape (rename to `Timer/Duration`
or an `interface ILifetimed { float Timer {get;set;} float Duration {get;} bool Alive
{get;set;} }`) and one generic helper in SpellPreview:
`static void AgeAndExpire<T>(List<T> list, float dt) where T : ILifetimed`.
Editor-preview only; severity low (cosmetic/maintenance, no gameplay).

---

## F5. KEEP_SEPARATE (low): Spell-vs-buff preview plumbing pairs

`EnsurePreviewInitialized`/`EnsureBuffPreviewInitialized` (SpellEditorWindow.cs:1544-1551,
843-850) and `RenderSpellPreviewToTarget`/`RenderBuffPreviewToTarget` (1583-1595, 879-891)
are 7-10-line wrappers around two **different classes** (`SpellPreview` with
flipbooks/HDR-effect/content deps vs `BuffPreview`) — abstracting needs a common IPreview
interface to save ~15 lines. `UpdateSpellPreview` vs `UpdateBuffPreview` (1553-1581 vs
852-877) are **deliberately different state machines**: spell re-triggers on selection
change and pushes `UpdateSpell` every frame; buff syncs only on selection-change/dirty,
with an in-code comment (863-872, 1697-1702) explaining that the every-frame approach
froze the orbit animation. This is structural variance with documented intent — exactly
the CLAUDE.md "do NOT abstract" case.

---

## F6. KEEP_SEPARATE (low): UIEditorWindow per-tab list/detail triples

`DrawNineSliceList` (UIEditorWindow.cs:1471) / `DrawElementList` (1794) / `DrawWidgetList`
(2287) already delegate list mechanics to the shared `EditorBase.DrawScrollableList`; what
remains per-tab is genuinely per-type behavior: nine-slice overlays 18px texture thumbnails
and calls `InvalidateNineSlice` on delete (1481-1508, 1528); element list formats a `[NS]`
prefix (1800-1806); widget list has a third (Copy) button, resets child selection
(`_selectedChildIdx`/`_selectedChildPath`) on every mutation, and rebakes harmonized
textures after copy (2308-2326). The `DrawXDetail` bodies are per-type field forms —
caller-owned data. Residual overlap is the ~12-line Add/Delete button block; also note
UIEditor operates on plain `List<T>` working copies (`_nineSlices/_elements/_widgets`),
not `RegistryBase` registries, so the F1 scaffold does not apply. Not worth a framework.
(The *real* UIEditor problem — `CloneWidget`/`CloneChild` dropping fields — is already
documented in docs/locate-behavior/editor.md pitfalls and is a clone-fidelity bug, not
this concept; fix is `Core.JsonClone.Deep`.)

---

## F7. KEEP_SEPARATE (low): UnitRegistry CountUnitsWith* / Remove*FromAll

`Necroking/Data/Registries/UnitRegistry.cs:532-573` — six methods of 4-6 lines. The weapon
pair iterates `def.Weapons` (a `List<WeaponEntry>`, matching `.Id`, with `RemoveAll`
predicate) while armor/shield operate on `List<string>` with `Contains`/`Remove` — the
data shapes differ, so a shared implementation needs selector delegates that cost more
lines than they save. Trivial, stable, self-documenting registry queries.

---

## F8. KEEP_SEPARATE (low): SpellDef BuildStrikeStyle/BuildBeamStyle/BuildDrainVisuals/BuildGodRayParams

`Necroking/Data/Registries/SpellRegistry.cs:740-791` — these ARE the consolidation: the
header comment says "Style builders — single source of truth. Used by SpellEffectSystem
(game) and SpellPreview (editor)", and both consumers verifiably call them (Game1.cs:3974,
SpellEffectSystem.cs:102/116/475/477, CasterUnitHandler.cs:228/262, SpellPreview.cs:333-406).
Strike and Beam map *different, independently-tunable field groups* (`Strike*` vs `Beam*`
JSON fields) into the same `LightningStyle` struct; merging them means changing the
data-file schema (nested style blocks in spells.json), which is a data-model decision with
zero bug-risk reduction — the mapping cannot silently diverge because each field group has
exactly one builder.

---

## Verdict summary

| # | Finding | Verdict | Severity |
|---|---------|---------|----------|
| F1 | Registry list+detail+CRUD scaffold ×5 (weapon/armor/shield/buff/flipbook) | CONSOLIDATE | medium |
| F2 | Flipbook Copy hand-clone bypasses CloneDef | CONSOLIDATE | medium |
| F3 | Clone* wrappers | KEEP_SEPARATE | low |
| F4 | SpellPreview 5× age/expire loops (+dead second pass) | CONSOLIDATE | low |
| F5 | Spell-vs-buff preview plumbing | KEEP_SEPARATE | low |
| F6 | UIEditorWindow per-tab triples | KEEP_SEPARATE | low |
| F7 | UnitRegistry count/remove sextet | KEEP_SEPARATE | low |
| F8 | SpellDef style builders | KEEP_SEPARATE | low |

Migration ordering note: land F2 immediately (one line); F4 in place when SpellPreview is
next touched; F1 as its own editor-refactor task with live /drive-game verification of all
five panels (New/Copy/Delete/Save/Ctrl+C/V each). If F1 lands, add `RegistryCrudPanel` to
memory/standard_patterns.md as the canonical "registry sub-editor" pattern.
