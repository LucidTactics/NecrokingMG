# Data/ — game-data model & JSON registries

The id-keyed JSON registries behind `data/*.json` (units, spells, items, weapons, armor,
shields, buffs, potions, flipbooks, weather, unit groups) and the aggregate loader/saver.
All serialization is **System.Text.Json**; there is no third-party JSON library.

## Files

- **`Necroking/Data/Registries/RegistryBase.cs`** — the single shared load/save engine.
  `RegistryBase<TDef>` (TDef: `IHasId`, parameterless ctor) holds `_defs`
  (id→def) + `_orderedIDs` (file order preserved). Key members:
  - `CreateJsonOptions()` (virtual, **no registry overrides it**) — CamelCase naming,
    `WriteIndented = true`, LF newlines, `DefaultIgnoreCondition = JsonIgnoreCondition.Never`
    (pruning happens later in `Save`, not via the serializer), relaxed escaping,
    `JsonStringEnumConverter(CamelCase)`.
  - `Load(path)` — parses the file, reads the `RootKey` array (e.g. `"units"`), calls
    `DeserializeItem` per element → `JsonSerializer.Deserialize<TDef>`. **Missing JSON
    properties fall back to the C# property-initializer defaults** on the def class —
    omitting a field whose value equals the initializer is round-trip-safe.
  - `Save(path)` — serializes each def to a `JsonObject` and runs **`PruneDefaults`**
    against a `new TDef()` baseline (also serialized): any top-level property
    `DeepEquals`-equal to the freshly-constructed default is OMITTED (except `id`;
    nested objects are kept/dropped as whole subtrees). So **a new def field with an
    initializer default only appears in JSON for entries where it's authored** — adding
    a field does not fatten every entry on the next roundtrip. Then per ordered id into
    `{ RootKey: [ … ] }` → `Core.JsonFile.WriteStringIfChanged` (atomic tmp+rename via
    `Core.AtomicFile`, skips write when text unchanged — editor auto-save loops depend
    on this).
  - `CloneDef` / `AddFromJson` — JSON round-trips through the SAME
    `SerializeItem`/`DeserializeItem`, so copy/paste fidelity == save/load fidelity, and the
    dev `add_data` command matches what `Load` produces.
  - **Look/edit here when…** changing how ANY `data/*.json` registry serializes (e.g.
    omit-default-valued fields, formatting, converters) — one change lands in every registry.

- **`Necroking/Data/GameData.cs`** — the aggregate: one property per registry
  (`Units`, `Spells`, `Items`, `Weapons`, `Armors`, `Shields`, `Buffs`, `Potions`,
  `Flipbooks`, `Weather`, `UnitGroups`, `Settings`, `Corpse`). `Load()` reads all of
  `data/*.json` (settings/weather come from the per-machine gitignored `user settings/`
  via `GamePaths.SeededUserFile`); `Save()` writes **everything** back (settings/weather
  to `user settings/`, the rest to `data/`), plus `Units.SaveWeaponPoints` →
  `data/weapon_points.json`. `GeneratePotionSpells()` injects a runtime SpellDef per
  potion — those generated spells ARE persisted into `data/spells.json` by `Save()`
  (verified: `potion_frenzy` exists in spells.json). **Look/edit here when…** adding a new
  registry file or changing what a full save touches. There is no per-registry save UI —
  every editor Save button (`UnitEditorWindow.SaveAll`, `SpellEditorWindow`,
  `ItemEditorWindow`) calls `_gameData.Save()` and rewrites all registries (if-changed).

- **`Necroking/Data/Registries/UnitRegistry.cs`** — `UnitDef` (the whole unit schema:
  identity/AI/sizes, wading config incl. nullable `DirectionalFractions`, nullable
  combat overrides, locomotion tuning, `Weapons`/`Armors`/`Shields`/`Tags` lists,
  `Paths` dict, `WeaponPoints`/`AnimTimings` dicts, runtime-only `[JsonIgnore] SpriteData`),
  plus `UnitStatsJson`, `UnitWeaponRef` + `UnitWeaponRefJsonConverter` (bare-string vs
  object form, writes minimal form for diff-free saves), `DirectionalFractions` (8-way
  wading fractions with authored-flag tracking + 4→8 lerp backfill —
  `EnsureDiagonalsBackfilled`), `UnitRegistry` itself (`RootKey = "units"`, `BuildStats`
  equipment resolution, `LoadWeaponPoints`/`SaveWeaponPoints` for the flat-key
  `weapon_points.json` sidecar). **Look/edit here when…** adding a UnitDef field, changing
  units.json shape, or touching weapon-point persistence.
  - Note: `weaponPoints` is stored BOTH inline in units.json and in
    `weapon_points.json`; on load, units.json populates first, then `LoadWeaponPoints`
    overwrites per anim-name key.
  - `UnitDef.Paths` sparseness ("only non-zero serialise") is enforced by the **editor**
    (`Editor/UnitEditorWindow.cs` removes zero entries on edit), not the serializer.

- **Adding a new per-unit combat stat** (the number combat rolls read, e.g. Morale/Attack):
  four touch points, all verified 2026-07-08 —
  1. `UnitStatsJson` (`UnitRegistry.cs`) — the JSON shape inside `UnitDef.Stats`
     (`[JsonPropertyName]` + initializer default; missing JSON = default).
  2. `UnitStats` (`Necroking/Data/CombatTypes.cs`) — the runtime stats class combat reads
     (`unit.Stats.X`).
  3. `UnitRegistry.BuildStats` — the **explicit field-by-field UnitStatsJson→UnitStats copy**
     in its object initializer; forgetting the copy line here silently zeroes the new field.
     This is the ONLY def→runtime stat copy: every spawn path (`Game1.SpawnUnit`, net ghosts,
     `Simulation.SpawnUnitByID`/`SpawnZombieMinion`/`TransformUnit`) funnels through
     `BuildStats` + `Simulation.ApplyDefRuntimeFields` (`_units[idx].Stats = stats`).
  4. Optional: `Editor/UnitEditorWindow.cs` `DrawStatsSection` (an `_ui.DrawIntField` row).
  Buffs do NOT rebuild UnitStats (modifiers are computed on-read via
  `BuffSystem.GetModifiedStat`), and unit stats are never saved — maps store `PlacedUnit`
  (def id + pos) and stats rebuild from the def each load — so there are no other drop sites.

- **Other registries** (`SpellRegistry.cs`, `ItemRegistry.cs`, `WeaponRegistry.cs`,
  `ArmorRegistry.cs`, `ShieldRegistry.cs`, `BuffRegistry.cs`, `PotionRegistry.cs`,
  `FlipbookRegistry.cs`, `WeatherRegistry.cs`, `UnitGroupRegistry.cs`) — all plain
  `RegistryBase<T>` subclasses; **none overrides `Save`/`Load`/`SerializeItem`/
  `CreateJsonOptions`** — only domain helpers on top.

- **`Necroking/Core/JsonFile.cs`** (`WriteStringIfChanged`), **`Necroking/Core/AtomicFile.cs`**
  (tmp+rename write), **`Necroking/Core/JsonDefaults.cs`** (shared `JsonSerializerOptions`
  presets, e.g. `Indented`, used by non-registry writers like `SaveWeaponPoints`).

## Serialization gotchas (for anyone changing save output)

- Default-omission is implemented as `RegistryBase.PruneDefaults` (compare each serialized
  property against a serialized `new TDef()` baseline, drop when `DeepEquals`). It was NOT
  done with `DefaultIgnoreCondition = WhenWritingDefault` because many defaults are
  non-CLR-default initializers (`Size=2`, `Radius=0.495`, `UnitType="Dynamic"`,
  `AggroRangeScale=1`, `WadingWaterlineFraction=0.35`, stats `=10`,
  `UnitAnimTimingOverride.EffectTimeMs=-1`) — CLR-zero comparison would be wrong.
- `DirectionalFractions` intercardinals track "explicitly authored" via setters; an
  authored 0 that gets omitted would be re-lerped from cardinals on the next load
  (rendering change). Treat nullable nested objects as all-or-nothing subtrees.
- Editing `data/*.json` by hand/tool → use the `edit-game-data` skill (`tools/json_data.py`).

## Related

- [editor.md](editor.md) — the in-game editors whose Save buttons call `GameData.Save()`.
- [game1-partials.md](game1-partials.md) — `Game1` owns the `GameData` instance and calls
  `Load()` at startup.

## Consolidation update (2026-07-07)

- **Data/MapSidecars.cs** = one reader+writer per map sidecar (triggers/zones/
  roads) via Core.JsonFile — fixed circle-region + junction + condition/effect
  round-trip losses. Regression scenarios: sidecar_roundtrip, env_defs_roundtrip,
  ui_defs_roundtrip, atomic_stream.
- env_defs.json: attribute-based serializer (`MapData.EnvDefJson`) replaced the
  split ParseEnvDef/WriteJson pair. Main map save + SkillBookData now atomic
  (AtomicFile stream API).
- **RegistryBase.NameOf(id)** (INamedDef) = the id->display-name lookup with
  blank-name fallback; don't hand-roll `?.DisplayName ?? id`.
