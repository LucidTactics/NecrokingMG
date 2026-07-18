We need to validate json etc data we read is well formed and so on.

Currently we parse strings into enums at runtime which is extremely irresponsible, we should do that when we load the data and parse them into actual useful information, and print errors when things fails.

I just had an issue where I wrote a value in a spell, and it turns out that value didn't map to any of the enums so was silently dropped for the default value. That is very bad usability.

So there are two parts here:
1. Ensure we parse and validate all such enum strings in the json as we read the data, and print errors for whatever fails.
2. Move away from using these directly json mapped data structures in the hot game loops, parse strings down to enums etc and use those, that way its impossible to fail parsing at runtime, so we only get these errors on startup.

---

## Status 2026-07-17 — done for the id-keyed registries

Implemented (see commit "feat(data): load-time enum validation + typed enum views"):

- **Load-time validation**: `RegistryBase.ValidateDef` hook runs per def at the end
  of every `Load`; errors go to `log/error.log` AND the console (so
  `--roundtrip-data` surfaces them). Overridden in Spell/Unit/Weapon/Buff/Potion/
  Item registries — covers every enum-string field plus fixed vocabularies
  (school, castAnim, blightMode, toggleEffect, magic paths, archetype names, …).
  Validation never blocks Save (it's a warning pass, not `LoadHadErrors`).
- **Typed views for game code**: `CachedEnum<T>` (Necroking/Data/EnumJson.cs) —
  identity-keyed parse cache; defs expose `[JsonIgnore]` `XxxEnum` properties
  (SpellDef.CategoryEnum/TrajectoryEnum/…, UnitDef.AIEnum/FactionEnum,
  WeaponDef.ArchetypeEnum, BuffEffect.TypeEnum, BuffDef.Incap*AnimEnum). The
  string properties remain the serialized form (files stay byte-stable); all the
  runtime `Enum.TryParse`/switch-on-string sites in the cast pipeline, sim,
  buff system and renderer now use the typed views. `WeaponStats.DamageType`/
  `TwoHanded` no longer re-run name classification per combat read.

Not covered (deliberate / follow-up if wanted):
- `data/settings.json`, `weather.json`, map JSON + sidecars (not RegistryBase;
  sidecars already TryParse defensively).
- Cross-registry *reference* validation (BuffID/SummonUnitID/etc. pointing at a
  real id) — different bug class, would be a natural next step on the same
  `ValidateDef` hook.
- `BuffEffect.Stat` stays a string by design (open vocabulary: BuffStat names +
  MaxMana/AllPaths/PathShock/... resource keys).
