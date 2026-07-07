# Dossier: Per-registry name lookups & clone/duplicate patterns

Concept judged: whether a generic `INamedRegistry` facade (name-of, clone-of,
count/remove-references) should unify the per-registry helpers. Verdict summary:
**no facade** — clone is already consolidated and documented; ref-counting is
structural variance; the only real wins are (a) a `NameOf(id)` helper on
`RegistryBase` and (b) one generic dropdown-list builder in `UnitEditorWindow`.

---

## Finding 1 — "id → DisplayName with fallback" one-liner, ~16 hand-rolled copies
**Verdict: CONSOLIDATE • Severity: low**

Every registry def type carries `DisplayName` (all 11 `RegistryBase<TDef>`
subclasses: Weapon/Armor/Shield/Unit/UnitGroup/Spell/Buff/Item/Potion/Weather/
Flipbook — verified in `Necroking/Data/Registries/*.cs`), but there is no shared
"give me the display name for this id" method, so callers re-derive it:

- `Necroking/UI/SkillBookOverlay.cs:744-747` — `SpellName`/`ItemName`/`UnitName`/`BuffName`,
  four private one-liners: `_gameData.X?.Get(id)` then `Has(d.DisplayName) ? d.DisplayName : id`
  (BuffName falls back to `Humanize(id)` instead).
- `Necroking/Game/Jobs/WorkerSystem.cs:110,125,518` — `def != null && !IsNullOrEmpty(def.DisplayName) ? def.DisplayName : unitDefId`
- `Necroking/UI/CraftingMenuUI.cs:424` — `_items.Get(ing.ItemId)?.DisplayName ?? ing.ItemId`
- `Necroking/UI/JobBoardUI.cs:321`, `Necroking/UI/UnitInfoPanel.cs:313`,
  `Necroking/UI/HUDRenderer.cs:846`, `Necroking/Game/SpellCasting.cs:451`
- Editors: `ItemEditorWindow.cs:290`, `SpellEditorWindow.cs:412`,
  `MapEditorWindow.cs:5611`, `UnitEditorWindow.cs:726,2667,2925,3191,3295,3686`

**Real divergence already present (the bug-risk):** roughly half the sites use
`?.DisplayName ?? id`, which returns an **empty string** when a def exists with
`DisplayName == ""` (all defs default to `""`, not null — e.g.
`UnitRegistry.cs:155`). The other half correctly check `IsNullOrEmpty`. So a
def with a blank name renders as blank text in CraftingMenuUI/ItemEditor/
SpellEditor lists but as its id in SkillBookOverlay/WorkerSystem/JobBoard.
Cosmetic only, hence low severity — but it is exactly the "fixed in one place"
class of divergence.

**Canonical home:** `Necroking/Data/Registries/RegistryBase.cs`.

**Merged API (sketch):**
```csharp
public interface INamedDef : IHasId { string DisplayName { get; set; } }
// RegistryBase<TDef> where TDef : class, INamedDef, new()   (all 11 defs qualify)
public string NameOf(string id) {
    var d = Get(id);
    return d != null && !string.IsNullOrEmpty(d.DisplayName) ? d.DisplayName : id;
}
```
Callers with extra flavor keep it at the call site (SkillBookOverlay's
`BuffName` becomes `var n = Buffs.NameOf(id); return n == id ? Humanize(id) : n;`).

**Call-site categories to migrate:** (1) SkillBookOverlay 4 helpers → delete,
call `NameOf`; (2) game-system labels (WorkerSystem, SpellCasting, HUDRenderer,
UnitInfoPanel, JobBoardUI, CraftingMenuUI); (3) editor plain lists
(ItemEditorWindow:290, SpellEditorWindow:412, MapEditorWindow:5611, UnitEditor
misc). Do **not** force-migrate the `"Name (id)"` dropdown format — that is
Finding 2.

**Effort: S** (one interface + one method + mechanical call-site edits).
**Risk: low** — display-only; behavior change is the intended fix (blank names
now show ids everywhere). No touch of Net/, renderer, or map JSON.

---

## Finding 2 — UnitEditorWindow parallel dropdown-list builders (4 near-identical copies)
**Verdict: CONSOLIDATE • Severity: low**

`Necroking/Editor/UnitEditorWindow.cs`:
- `BuildWeaponDropdownLists` (:3715), `BuildArmorDropdownLists` (:3730),
  `BuildShieldDropdownLists` (:3745) — **byte-identical except for the registry**:
  loop `GetIDs()`, format `"{DisplayName} ({id})"` (or bare id when name empty),
  emit parallel `displayNames[]`/`ids[]`.
- `BuildUnitDisplayDropdownLists` (:2837) — same shape plus a leading `""` entry
  and `[id]` bracket style instead of `(id)`.
- `BuildSpellDropdownDisplayList` (:3702) — same formatting, display list only.

`MapDisplayToId` (:3761) / `MapIdToDisplay` (:3772) are already **single** shared
implementations — the labeler's claim of duplication there is wrong; they are the
consolidation target's mechanics, not offenders.

**Canonical design:** one private generic helper in the same file (editor owns
the presentation format; registry layer stays format-free — matches CLAUDE.md
"shared component owns mechanics, caller owns data"):
```csharp
private static void BuildDropdownLists<TDef>(RegistryBase<TDef> reg,
    out string[] displayNames, out string[] ids,
    bool includeEmpty = false, string fmt = "{0} ({1})")
    where TDef : class, INamedDef, new()
```
(Builds on Finding 1's `INamedDef`.) The 3 equipment builders collapse into
calls; the Unit variant passes `includeEmpty: true` — and decide whether `[id]`
vs `(id)` bracket inconsistency is intentional (almost certainly not; unify).

**Effort: S. Risk: low** (editor-only, one file). If Finding 1 is skipped, the
generic can constrain on a local delegate instead; still worth doing.

---

## Finding 3 — UnitRegistry equipment count/remove triples
**Verdict: KEEP_SEPARATE • Severity: low**

`Necroking/Data/Registries/UnitRegistry.cs:532-573` —
`CountUnitsWithWeapon/Armor/Shield`, `RemoveWeaponFromAll/ArmorFromAll/ShieldFromAll`.
Six adjacent 4-6 line loops. The variance is **structural, not data-level**:
`def.Weapons` is a list of weapon-slot objects matched by `.Id`
(`w.Id == weaponID`, `RemoveAll(w => w.Id == ...)`), while `def.Armors` /
`def.Shields` are `List<string>` (`Contains` / `Remove`). A generic
`CountRefs(Func<UnitDef, bool>)` / `RemoveRefs(Action<UnitDef>)` would just move
the loop and push the selector to the caller — a framework, not a utility
(CLAUDE.md consolidation-design rule). Sole consumer is the unit editor's
delete-confirmation flow (`UnitEditorWindow.cs:586-610, 3425, 3490, 3554`);
maintenance surface is one screenful in one file. Not worth abstracting.

---

## Finding 4 — Registry clone / DeepClone "duplication"
**Verdict: KEEP_SEPARATE • Severity: low (evidence over-matched — already consolidated)**

The labeling pass flagged `CloneSpell/CloneBuff/CloneWeapon/CloneArmor/CloneShield`
+ UIEditor `CloneChild/CloneWidget` + `Core/JsonClone.cs` as parallel clones.
Verified reality: **this consolidation already happened and is the documented
standard** (`memory/standard_patterns.md:85-88`, `docs/locate-behavior/data-registries.md:23`):

- `RegistryBase.CloneDef` (`RegistryBase.cs:67`) is the canonical registry clone —
  JSON round-trip through the registry's **own** serializer options, so clone
  fidelity == save/load fidelity.
- The editor `Clone*` methods are deliberate 1-line adapters over `CloneDef`
  (`UnitEditorWindow.cs:3800-3810`, `SpellEditorWindow.cs:1734-1746`) adding only
  per-editor DisplayName suffixing and a null-fallback; the file comments
  (`UnitEditorWindow.cs:3792-3798`) record why hand-written copies were banned.
- `Core/JsonClone.Deep` (`JsonClone.cs:13`) is explicitly scoped to defs **not**
  owned by a RegistryBase; its header documents the split.
- `UIEditorWindow.DeepClone` (:3949) intentionally uses the **undo system's**
  serializer (`_undoJson`, `IncludeFields` — JsonClone uses `IncludeFields=false`),
  so working-copy clones match undo-snapshot fidelity. The serializer options are
  load-bearing; merging the three round-trips into one would silently change
  which fields survive a clone.

Three JSON round-trips, each with a different (correct) serializer contract and
each documented. Nothing to do.

---

## Overall judgment on the proposed `INamedRegistry` facade
Reject the full facade. Clone is already generic on `RegistryBase`;
count/remove-references is unit-specific structural logic with one caller;
only **name-of** generalizes cleanly. Ship Finding 1 (`INamedDef` + `NameOf`)
and Finding 2 (generic dropdown builder in the unit editor) as small S-effort
refactors; leave the rest alone.
