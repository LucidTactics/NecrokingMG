# json-spells — `data/spells.json` (Summon category focus)

Notes for authoring/editing `data/spells.json` entries. One list of `SpellDef` structs
keyed by unique `id`. Schema = `Necroking/Data/Registries/SpellRegistry.cs` (`SpellDef`,
field-by-field with `[JsonPropertyName]` + editor attrs). Enums = `Necroking/Data/Enums.cs`.
Effect logic = `Necroking/Game/SpellEffectSystem.cs`.

**Edit with `tools/json_data.py` (edit-game-data skill), not by hand**, then
`dotnet build …` + `bin/Debug/Necroking.exe --roundtrip-data` and commit the roundtripped
result (the game reorders/prunes fields on save).

## Category dispatch
`category` (string) selects the effect switch in `SpellEffectSystem.Execute`. Values:
`Projectile · Buff · Debuff · Summon · Strike · Beam · Drain · Cloud · Sacrifice · Blight · Toggle`.
Bad category string = inert spell (parsed at use sites; validated at load via `EnumJson.Check`).

## Summon category fields (only these are read for `"category":"Summon"`)
| JSON field | Type / enum | Meaning / gotcha |
|---|---|---|
| `summonUnitID` | Units-registry id (string) | Unit to spawn. **Empty + `summonTargetReq:"Corpse"`** = raise that corpse's own zombie variant. For a from-nothing horde, set an explicit id (e.g. `"skeleton"`). |
| `summonQuantity` | int, default 1 | Units per cast. This is the "horde" knob. Extra spawns get a 1-unit random-angle offset (`SpellEffectSystem` q>0 loop). **Clamped by the horde cap** (see gotcha). |
| `summonTargetReq` | enum `None`/`Corpse`/`UnitType`/`CorpseAOE` | `None` = summon from nothing (no corpse needed). `Corpse` = consume/raise one targeted corpse. `CorpseAOE` = raise up to `summonQuantity` corpses in an area. `UnitType` = target a specific living unit type. |
| `summonMode` | enum `Spawn`/`Transform` | `Spawn` = new unit(s). `Transform` = turn the targeted unit into the summon (needs a unit target). |
| `spawnLocation` | enum `NearestTargetToMouse`/`NearestTargetToCaster`/`AdjacentToCaster`/`AtTargetLocation` | Where they appear. `AtTargetLocation` = at the cursor point (used by the multi-summon hordes). |
| `summonAsPuppet` | bool | Raised body walks to nearest Corpse Pile instead of fighting (leave `summonUnitID` empty). |
| `summonFlipbook` | `FlipbookRef?` | Effect at each spawn point. `flipbookID:""` = none. Corpse raises play the reanim smoke instead and suppress this. |
| `reanimationEffectID` | string | Effect for corpse-raise rise (Corpse/CorpseAOE path via `QueueReanimRise`). |

Shared fields still apply: `range`, `manaCost`, `cooldown`, `primaryPath`/`primaryLevel`
(+ optional secondary) for path-gating & mana mastery discount, `hidden` (hide from
grimoire), `school`, `tileTemplate`, `icon`, `description`, `masteryBonuses`.

## Existing models to copy
- **`debug_skeleton_summon`** — the exact "horde of skeletons from nothing":
  `summonUnitID:"skeleton"`, `summonQuantity:20`, `spawnLocation:"AtTargetLocation"`,
  no `summonTargetReq` (→ `None`), `hidden:true`, `manaCost:0`, `range:9999`. A real
  player spell = same shape with `hidden` off, a real `manaCost`/`cooldown`/`range`,
  `primaryLevel` raised, and an `icon`.
- **`debug_militia_summon`** — same pattern, `summonQuantity:10`.
- **`summon_skeleton`** / **`raise_skeleton`** — single from-nothing vs from-corpse.

## Gotchas
- **Horde cap clamps quantity.** `SpellEffectSystem.ExecuteSummonSpell` computes
  `HordeCapTracker.CategoryFor(summonUnitID)` and clamps `summonQuantity` to
  `HordeCapTracker.Available(...)`. If the skeleton category cap is (say) 12, a
  `summonQuantity:20` cast spawns at most 12. `SpellCaster.TryStartSpellCast` refuses the
  cast entirely (`HordeCapFull`) when 0 slots are available. Raise the cap or accept the clamp.
- **From-nothing summons** (`summonTargetReq:None`) enroll into `sim.Horde` and play
  `SpawnSummonEffect`. **Corpse raises** go through `game.QueueReanimRise` (deferred spawn,
  rise anim at the grave) — different path, no summon flipbook.
- Pure-data spells need **no C# change** — only a new *category* or behavior touches
  `SpellRegistry.cs` + `SpellCasting.cs` + `SpellEffectSystem.cs` (all three).
