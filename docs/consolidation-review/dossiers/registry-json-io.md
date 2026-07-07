# Dossier: JSON registry / settings file I/O bypassing RegistryBase

Concept judged: "hand-rolled JSON load/save that should reuse `RegistryBase` or `Core/JsonFile`".
Verdict summary: **the labeling evidence is half-stale.** The settings-object half of the claim
(CorpseSettings, GameSettings) was already consolidated onto `Core.JsonFile` — those are done.
The real, live duplication is elsewhere: **split reader/writer pairs** (map sidecars, env defs)
and **two parallel parsers of the same UI-def files**. Verifying the map sidecars turned up
**two confirmed round-trip bugs** caused by exactly the drift this concept predicts.

Canonical infrastructure that already exists:
- `Necroking/Data/Registries/RegistryBase.cs` — id-keyed `{rootKey:[{id,...}]}` engine:
  shared options, prune-defaults save, atomic if-changed writes (`Save` → `Core.JsonFile.WriteStringIfChanged`, RegistryBase.cs:192).
- `Necroking/Core/JsonFile.cs` — `Load<T>` / `Save<T>` / `SaveIfChanged<T>` for single-object POCOs, atomic via `Core/AtomicFile.cs`.
- `Necroking/Core/JsonDefaults.cs` — shared `JsonSerializerOptions` presets.

---

## Finding 1 — Map sidecars: loader in MapData.cs, saver in MapEditorWindow.cs (CONSOLIDATE, high)

Same intent (persist triggers / zones / roads sidecar files), **two implementations per file
living in different source files**: the field list exists once as `TryGetProperty` reads and
once as `Utf8JsonWriter` writes, with nothing keeping them in sync.

- Load: `Necroking/Data/MapData.cs` — `LoadTriggers` (:227), `LoadZones` (:332), `LoadRoads` (:398).
- Save: `Necroking/Editor/MapEditorWindow.cs` — `SaveZones` (:6293), `SaveTriggers` (:6340), `SaveRoads` (:6434). All use bare `File.Create` (non-atomic, no if-changed).

**Two CONFIRMED divergence bugs found while verifying:**

1. **Region shape is saved but never loaded.** `SaveTriggers` writes
   `writer.WriteString("shape", r.Shape.ToString())` (MapEditorWindow.cs:6355), but
   `LoadTriggers` constructs `TriggerRegion` without ever reading `"shape"`
   (MapData.cs:243-252). `TriggerRegion.Shape` defaults to `Rectangle`
   (Necroking/Game/TriggerTypes.cs:14) and drives `ContainsPoint` (:21-26) — so **every
   circle trigger region silently becomes a rectangle on the next map load**, changing
   gameplay trigger behavior.
2. **Road junctions are parsed and then discarded.** `LoadRoads` builds a
   `List<RoadJunction> junctions` (MapData.cs:437-453) but never applies it;
   `RoadSystem.SetJunctions` (Necroking/World/RoadSystem.cs:77) has **zero callers**.
   Editor-authored junctions are written by `SaveRoads` and **vanish on every load**.

**Proposed canonical design:** these sidecars are small (KB-scale, unlike the map itself), so
attribute-annotated DTOs + `Core.JsonFile` eliminates both halves of the duplication:

```csharp
// Necroking/Data/MapSidecars.cs
class TriggersFile { public List<TriggerRegion> Regions; public List<PatrolRoute> PatrolRoutes; ... }
class ZonesFile    { public List<MapZone> Zones; }
class RoadsFile    { public List<RoadInstance> Roads; public List<RoadJunction> Junctions; }
// Load: Core.JsonFile.Load<TriggersFile>(path, JsonDefaults..., out var f)
// Save: Core.JsonFile.SaveIfChanged(path, file, JsonDefaults...)   // atomic + if-changed for free
```
Conditional emission (population only for Village zones, spawns only otherwise) can stay as a
small pre-save normalization or nullable properties with `JsonIgnoreCondition.WhenWritingNull`.

**Call sites to migrate:** `MapData.LoadTriggers/LoadZones/LoadRoads` callers (map load path in
Game1/MapEditor), `MapEditorWindow.SaveMap` → `SaveTriggers/SaveRoads/SaveZones`. Fix the two
bugs above as part of it (or immediately, independently — they're one-line fixes:
parse `"shape"`; call `roads.SetJunctions(junctions)`).

**Effort:** M. **Risk:** moderate — sidecar file shape must round-trip existing maps
(camelCase names match; enum-as-string needs `JsonStringEnumConverter`). The two bug fixes
are S and near-zero risk. Not RegistryBase: these aren't flat id-keyed registries
(nested waypoints, multi-section documents) — `JsonFile` + DTO is the right canonical home.

---

## Finding 2 — UI widget defs: two hand-rolled parsers + one hand-rolled writer (CONSOLIDATE, medium)

The same three files (`data/ui/nine_slices.json`, `elements.json`, `widgets.json`) and the
**same shared def classes** (`UIEditorNineSliceDef` / `UIEditorElementDef` /
`UIEditorWidgetDef`, defined in UIEditorWindow.cs:20/63/173) are parsed by **two independent
hand-written `JsonDocument` walkers**:

- Editor: `Necroking/Editor/UIEditorWindow.cs` `LoadNineSlices`/`LoadElements`/`LoadWidgets`
  (:420/:448/:512) + manual `Utf8JsonWriter` savers `SaveNineSlices`/`SaveElements`/`SaveWidgets`
  (:674/:700/:776), via bare `File.Create` (non-atomic, no if-changed).
- Runtime: `Necroking/UI/RuntimeWidgetRenderer.cs` `LoadNineSlices`/`LoadElements`/`LoadWidgets`
  (:792/:819/:903) — a second full parse of the identical format (~600 lines of
  `TryGetProperty` boilerplate between the two files).

**Demonstrated drift (latent):** the runtime parser reads a nine-slice `harmonize` recipe
(RuntimeWidgetRenderer.cs:810) that the editor **neither reads (:420-446) nor writes
(:674-698)** — a hand-authored nine-slice harmonize would render in-game and then be
silently stripped by the editor's next save. (`data/ui/nine_slices.json` currently contains
no `harmonize`, so it's not a live bug — yet.) Every new widget field must be added in
3 places (2 parsers + 1 writer); a miss reproduces the Finding-1 class of bug.

**Proposed canonical design:** one loader/saver, two consumers. The files are exactly
RegistryBase-shaped (`{rootKey:[{id,...}]}`), so the preferred home is three thin
`RegistryBase<T>` subclasses (RootKey = `nineSlices`/`elements`/`widgets`) after annotating
the shared def classes for System.Text.Json — this buys prune-defaults save (matching the
current "omit when default" manual writer), atomic if-changed writes, and `CloneDef` for the
editor free. A lighter fallback is a shared `UIDefsIO` static class both callers use.
Keep the editor's "load failed → disable save for that file" behavior (UIEditorWindow.cs:1420-1422).

**Call sites to migrate:** `UIEditorWindow.LoadDefinitions` (:394) + its 3 savers;
`RuntimeWidgetRenderer.LoadDefinitions` (:76). **Effort:** M (the widget/children/textOverride
nesting needs care; byte-identical output not required but field coverage must match).
**Risk:** moderate — UI-wide regression surface; verify by diffing a re-saved file against current output.

---

## Finding 3 — env_defs.json: ~80-field reader and writer in different files (INVESTIGATE, medium)

- Reader: `MapData.ParseEnvDef` (Necroking/Data/MapData.cs:569-689) — ~80 `TryGetProperty` lines.
- Writer: `EnvironmentObjectDef.WriteJson` (Necroking/World/EnvironmentSystem.cs:330-444) — the
  same ~80 fields as `writer.Write*` calls, plus `MapData.SaveEnvDefs` (:203) wrapping it with
  a bare `File.Create` (non-atomic).

Currently the two lists appear in sync (spot-checked; no dropped field found), but this is the
identical structure that produced Finding 1's confirmed bugs: add a field to `WriteJson` and
forget `ParseEnvDef` (or vice versa) and data silently drops. `env_defs.json` IS an id-keyed
array, so it's registry-shaped.

**The decision needed (why INVESTIGATE, not CONSOLIDATE):** whether to convert
`EnvironmentObjectDef` to attribute-based serialization. It needs custom converters for
`HdrColor` (`{r,g,b,a,intensity}`), `HarmonizeSettings` (has its own `Read`/`Write`), the
category-dependent `randomFlip` default (MapData.cs:685-687), and the legacy flat-array vs
wrapped `{"envDefs":[...]}` format (:177-184). A `RegistryBase` subclass with an overridden
`Load` (flat array) would also inherit prune-defaults saving — which would *change the file's
on-disk shape* (today every field is written). That output change touches a Drive-synced,
collaborator-shared file, so the design (and whether prune-defaults is wanted) should be a
deliberate call, not a drive-by. Effort M-L, risk moderate.

---

## Finding 4 — WadingDefaultsFile hand-rolls what Core.JsonFile exists for (CONSOLIDATE, low)

`Necroking/Data/WadingDefaultsFile.cs` — `Load` (:32) does its own
`File.Exists`/`ReadAllText`/`Deserialize`/catch-log, `Save` (:56) uses **non-atomic
`File.WriteAllText`** (:66) and a private options instance (:26). `Core/JsonFile.cs`'s own
doc comment (:10) names WadingDefaultsFile as an intended user — it just never migrated,
unlike its siblings.

**Merged API (drop-in):**
```csharp
public static bool Load(string path) {
    if (!Core.JsonFile.Load<WadingDefaultsJson>(path, Core.JsonDefaults.Indented, out var data) || data == null) return false;
    data.QuadrupedBottom?.EnsureDiagonalsBackfilled(); data.QuadrupedTop?.EnsureDiagonalsBackfilled();
    WadingDefaults.Apply(data.QuadrupedBottom, data.QuadrupedTop); return true;
}
public static bool Save(string path) => Core.JsonFile.Save(path, BuildJson(), Core.JsonDefaults.Indented);
```
Call sites: startup load + unit editor "Save as default" button — signatures unchanged, no
caller migration. **Effort:** S. **Risk:** trivial (properties carry `[JsonPropertyName]`,
so options-preset swap is inert).

---

## Finding 5 — Main map file (default.json) streaming writer (KEEP_SEPARATE, medium)

`MapEditorWindow.SaveMap` (Necroking/Editor/MapEditorWindow.cs:6010) and `MapData.Load`
(MapData.cs:26) hand-roll a heterogeneous multi-section document with base64 blobs on a
**streaming** `Utf8JsonWriter`. The map is ~55 MB (MapData.cs:25 comment) — serializer/
JsonNode round-trips or `JsonFile`'s string-based writes would balloon memory, and the
document isn't an id-keyed registry. Per CLAUDE.md, this is structural variance: don't
abstract it. **One safety gap worth fixing separately (not a consolidation):**
`File.Create(path)` (:6018) is non-atomic — a crash mid-save destroys the Drive-synced map.
An `AtomicFile.CreateStream(path)` (write to `.tmp`, rename on dispose) added to
`Core/AtomicFile.cs` would close it with no structural change.

---

## Finding 6 — CorpseSettings / GameSettings: evidence stale, already consolidated (KEEP_SEPARATE, low)

The labeling pass claimed these were "hand-rolled near-copies". **They aren't anymore:**
- `CorpseSettings.Load/Save` (Necroking/Data/Registries/CorpseSettings.cs:74, :82) →
  `Core.JsonFile.Load` / `Core.JsonFile.Save`.
- `GameSettingsData.Load/Save` (Necroking/Data/Registries/GameSettings.cs:388, :416) →
  `Core.JsonFile.Load` / `Core.JsonFile.SaveIfChanged` (per-frame auto-save relies on if-changed).

Single-object per-machine settings are the exact category `JsonFile` was built for;
`RegistryBase` (id-keyed collections, RootKey arrays, prune-defaults) is the wrong shape for
them. The remaining per-class field-copy blocks in `GameSettingsData.Load` (:390-408) are
data-level null-backfill for older files, not a duplicated engine. Nothing to do.

---

## Finding 7 — SkillBookData (KEEP_SEPARATE, low)

`Necroking/Data/SkillBookData.cs`: tab files (`data/skills/<tab>.json`) carry tab-level
metadata (`displayName`, `order`, `unlockRequirement`) around the skills array — not
RegistryBase shape — and `SaveLayout` (:80) is deliberately a **surgical read-modify-write**
(parse tree, patch only `x`/`y`, preserve every unknown field, :88-102). That intent —
"editor owns layout, hand-authored fields must survive" — is structurally different from
full-object serialization; forcing it through RegistryBase/JsonFile would destroy the
preserve-unknown-fields guarantee. The hand-rolled `Str`/`Int`/`Bool` load helpers are
self-contained and load-only. Minor nit (one line, not a consolidation):
`File.WriteAllText` (:102) → `Core.AtomicFile.WriteAllText`.

---

## Suggested order of work
1. Fix the two Finding-1 bugs now (2 lines: parse `"shape"`; call `SetJunctions`). Verify with a save→load round-trip of a map with a circle region + junctions.
2. Finding 4 (S, trivial risk).
3. Finding 1 sidecar DTO consolidation (M).
4. Finding 2 UI defs (M) — diff re-saved files against current output.
5. Finding 3 after the prune-defaults/file-shape decision.
6. Finding 5's `AtomicFile` stream API opportunistically.
