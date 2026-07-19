# Skills — skill book, trees, effects & unlock state

The player skill-tree feature ("Tome of the Necroking", K to open). Four layers:
**defs** (JSON per tab) → **loader/model** (`Data/SkillBookData.cs`) → **per-run state**
(`Game/SkillBookState.cs`) → **effects** (`Game/SkillEffects/SkillEffects.cs`), plus the
widget-driven overlay UI. Skill defs are NOT part of the `RegistryBase` id-keyed
registries — they have their own loader and their own JSON folder.

## Files

### `data/skills/*.json` — the skill trees (data)
One file per tab: `potions.json`, `monstrology.json`, `necromancy.json`, `magic.json`,
`metamorphosis.json`, `lich.json`, `construction.json` (the build-menu gate tree: Necro
Bench + Alchemist Table as free roots, the other buildables as free child `unlock_building`
nodes). Tabs are **discovered dynamically** from the folder —
dropping in a new `<tab>.json` adds a tab (order = optional `"order"` field, else the
canonical `TabIds` order, else appended). Per skill: `id`, `name`, `description`,
`x`/`y` (tree position in a logical 1280x680 space), `parents` (AND-prereq),
`parentsAny` (OR-prereq), `exclusiveOf` (mutex set), `costs`
(`type` = `item` / `event` / `skillpoints`), `effect` + `effectArg` (routes into
`SkillEffectRegistry`), `startLearned`. Per tab: optional `unlockRequirement`
(`seen_item` → `Inventory.HasEverSeen`; `morphed` → passive flag `morphed:<id>` — the
lich tab uses this).
**Look/edit here when…** adding/rebalancing a skill node, changing costs/prereqs,
re-parenting a tree, adding a whole new tab.

### `Necroking/Data/SkillBookData.cs` — loader + def model
`SkillBookDefs.Load()` (called from `Game1.LoadContent`), `SkillBookDefs.Tabs`,
`FindTabIndexFor`, `SaveLayout()` (writes only x/y back to the JSON — the in-book layout
editor's save). Types: `SkillTab` (incl. `UnlockType`/`UnlockId` tab gate, `ResolveLinks`
builds `ChildIds` + bounds), `SkillDef`, `SkillCost`.
**Look/edit here when…** adding a new SkillDef field, a new cost type, a new tab-gate type
(gate *evaluation* lives in `SkillBookOverlay.IsTabUnlocked`), or changing how trees load.

### `Necroking/Game/SkillBookState.cs` — per-run unlock state (the single mutable store)
`Game1._skillBookState` is the live instance; `Simulation.SetSkillBook` shares it with the
sim (and repoints `Events` at `Simulation.PlayerEvents`). Holds: `_learned` set,
per-tab skill-point pools (`SKILL_POINT_TYPES` = potions/monstrology), `UnlockedPotions`,
`UnlockedBuildings` (env def ids; `IsBuildingUnlocked`/`UnlockBuilding` — gates the build
menu), passive flags (`HasPassive`/`SetPassive` — also carries `morphed:<id>` and `action:<id>`
markers), `IntrinsicBuffs` (tag→buff bindings for future spawns), `UnlockedSummons`,
`_unlockedAI` (behavior→payload, monotone via `UnlockAI`), `PotionSlotsUnlocked`,
`CorpseEatingBonus`/`SoulConsumptionBonus` (+caps). Learn paths: `TryLearn` (prereqs →
exclusion → afford → deduct → run effect → roll back on failure) and `LearnFree`
(gameplay auto-grants, no cost/prereq). Predicates the UI uses: `ArePrereqsMet`,
`IsExcluded`/`ExclusionBlocker`, `CanAfford`, `IsAvailableAffordable/Unaffordable`,
`GetProgress`. `InitFromDefs()` resets everything for a new game (applies `startLearned`).
**Persisted in save games** via `ExportSave()`/`ApplySave()` ↔ `SavedSkillBook` DTO in
`Data/SaveGameData.cs` (written in `Game1.Saves.cs` `WriteSaveGame`, restored in
`ApplySaveToWorld`). The save only stores what **can't be re-derived**: `_learned` (the
source of truth), skill points, event tallies, passive flags, intrinsic-buff bindings,
and the metamorphosis bonuses. The pure unlocks — `UnlockedPotions`, `UnlockedBuildings`,
`UnlockedSummons`, `_unlockedAI`, `PotionSlotsUnlocked` — are **NOT** persisted; `ApplySave`
re-derives them by replaying each learned talent's effect (the `DerivableOnLoad` whitelist:
`unlock_potion`/`unlock_building`/`unlock_summon`/`unlock_ai_behavior`/`unlock_potion_slot`
+ `grant_path`). **So a new unlock that is a pure, idempotent function of the learned set
= add the field here + add its effect id to `DerivableOnLoad` (no DTO/save touch).** A
non-derivable state (side-effecting or earned through play) still needs the 4 touches:
field + `ExportSave` + `ApplySave` + a `[JsonPropertyName]` property on `SavedSkillBook`.
**Look/edit here when…** adding a new unlock-state kind, a new cost predicate, or wiring
"was skill X learned?" queries.

### `Necroking/Game/SkillEffects/SkillEffects.cs` — the id→effect registry (THE wiring point)
`SkillEffectRegistry` (static ctor = the full string-id → `ISkillEffect` table) +
`SkillEffectContext` (Inventory/GameData/Bar/BookState/`Sim?` — Sim is null in
test/scenario learns). Registered effects and their downstream consumers:

| effect id | what it does | consumed by (wiring) |
|---|---|---|
| `add_spell` | spell → first empty spellbar slot | spellbar itself — wired |
| `unlock_potion` | adds to `UnlockedPotions` | `UI/CraftingMenuUI.cs` recipe filter — wired |
| `unlock_building` | adds env def id to `UnlockedBuildings` (`UnlockBuildingEffect`, idempotent) | `UI/BuildingMenuUI.cs` `Open()` filters PlayerBuildable defs by the set; `TryPlace` re-checks defensively — wired. `build_unlocks` dev verb dumps the set |
| `passive_stat` | sets flag; known flags also apply necro buffs via `PassiveBuffMap` | `unholy_movement`/`unholy_strength` → buffs (wired); `death_fog_consumption` → `Game1.Animation.cs` fog-mana tick (wired); `efficient_tinctures` → `CraftingMenuUI` (wired); **any other arg is flag-only = silent stub** |
| `morph_necromancer` | `Sim.TransformUnit` to a `PlayerForm` def, records `morphed:<id>`, re-applies passive buffs | wired; `morphed:` flag also gates the lich tab |
| `metamorph_action` | sets `action:<id>` flag | `UI/CharacterStatsUI.cs` buttons + `TryConsumeNearestCorpse` (consume corpse → heal + capped max-stat bonus) — wired. NB the comment naming `Game1.PerformMetamorphActiveOnCorpse` is stale; no such method |
| `grant_intrinsic_buff` | `buff:tag1,tag2` — buff to all live matching units + recorded for spawns | spawn hook in `Simulation.SpawnUnitByID` reads `IntrinsicBuffs` — wired |
| `unlock_ai_behavior` | `behavior[:payload]` → `UnlockAI` | `AI/CorpseEatAI.cs` (`corpse_eat`) — wired for that behavior |
| `unlock_potion_slot` | bumps `PotionSlotsUnlocked` | `UI/TableCraftMenuUI.cs` — wired |
| `cap_buff` | `monster:N`/`human:N` → stacks of `buff_monster_cap`/`buff_human_cap` on necro | `Game/HordeCapTracker.cs` `GetCap` sums `MonsterCap`/`HumanCap` buff stats — wired |
| `grant_path` | code-built permanent buff on `BuffSystem.PathStat` | `EffectivePathLevel` → spell gating/mana discount — wired |
| `unlock_summon` | adds to `UnlockedSummons` | **NOTHING reads it** — `IsSummonUnlocked`/`UnlockedSummons` have zero callers outside the class; the reanimation flow does NOT filter by it despite doc comments claiming so. Effectively a stub with state |
| `compound` | `fx1=arg\|fx2=arg` runs sub-effects | wired (delegates) |
| `noop` | nothing | — |
| `unlock_unit` | `LogStubEffect` — log only | the last explicit stub; no JSON skill currently uses it |

Unknown effect ids log + **soft-pass as noop** (the learn still succeeds).
**Look/edit here when…** adding a new effect type, or wiring a stub to real gameplay.

### `Necroking/UI/SkillBookOverlay.cs` — the skill book UI
Widget-driven modal (`SkillBookWindow` chrome + `SkillTile` stamped per node, connector
lines in code; cloned from the grimoire pattern). `Bind(state, inv, gd, bar, sim)` from
`Game1` (after world load and again on rebind), `Toggle` on K (`Game1.cs` Update) / HUD
menu button (`GameRenderer.Hud.cs`) / `skillbook` dev verb (`Game1.Dev.cs`). Click →
private `TryLearn(id)` builds the `SkillEffectContext` → `_state.TryLearn`; toast on
result. Tab visibility gate = `IsTabUnlocked` (`seen_item` / `morphed`). Also hosts the
**layout editor** (`EnableLayoutEditor`, drag tiles, `SkillBookDefs.SaveLayout`). Test
hooks: `TryLearnById`, `SetActiveTab`, `SetPassiveForTest`, `SetHoverForTest`.
**Look/edit here when…** the book's rendering/hit-testing/tooltips/tab gating misbehave.

### `Necroking/UI/CharacterStatsUI.cs` — metamorph actives (+ a separate legacy skill list)
Renders the Metamorphosis action buttons when `action:corpse_eating` /
`action:soul_consumption` flags are set; `TryConsumeNearestCorpse` is the actual consume
implementation (tallies `corpses_eaten`, heals HP/mana, capped max-stat bonus).
**Gotcha:** this file also has its own hardcoded `Skills` toggle array
(`SkillKind.ActiveSpell/PassiveGhostMode/PassiveArchmage` + `ApplyArchmage`) — a
character-panel toggle list completely SEPARATE from the skill book; the book's `archmage`
passive flag does not connect to it.

### `Necroking/Game/PlayerEventTracker.cs` — event-cost tallies
Flat string-keyed counters (`Keys`: `monster_kill`, `human_kill`, `cast_spell`,
`corpses_eaten`); canonical instance = `Simulation.PlayerEvents`. `event`-type skill costs
read `Events.Get`; milestones are never consumed.

## Earn/learn entry points (Game1)
- `Game1.TryAutoLearn(skillId, header)` → `LearnFree` + corner toast; called from
  `Game1.Crafting.cs` (first-mushroom "skill_paralysis" recipe) and the dev learn verb.
- Skill points: `CraftingMenuUI` awards `("potions", 1)` per potion crafted;
  `Game1.TryConsumeInventoryItem` grants `ItemDef.SkillPointPool`/`SkillPointAmount`
  (skill-point potions); a dev verb in `Game1.cs` bulk-grants `EVENT_TYPES` +
  `SKILL_POINT_TYPES`.

## Pitfalls
- **`unlock_summon` is dead-ended** — six monstrology nodes record unlocks nothing reads.
- **`passive_stat` args without a consumer are silent stubs** — `magic.json` late nodes
  `nether_storm`, `pyre`, `archmage` set flags no code checks.
- Unknown effect ids **soft-pass**: the skill "learns" successfully and does nothing.
- SkillBookState **is saved** (`SavedSkillBook` in the save game, see above) — the old
  "per-run only" note is obsolete; v1 saves lacking the key load with a fresh book.
- **`startLearned` skills never run their effect on a *fresh* game** — `InitFromDefs` only
  fills `_learned`. A set-populating effect (`unlock_potion`, `unlock_building`) on a
  `startLearned` node silently leaves the set empty in the first session —
  use a zero-cost learnable node (empty `costs` passes `CanAfford`) or `LearnFree`
  (which DOES run the effect) instead. This is exactly why `construction.json`'s free
  roots are zero-cost learnable nodes, not `startLearned`. (Note: on the *load* path this
  no longer bites — `ApplySave`'s replay runs `DerivableOnLoad` effects for every learned
  node including `startLearned` ones — but the live first game still depends on
  `TryLearn`/`LearnFree` running the effect, so leave the construction.json structure alone.)
- Scenario learns pass `Sim = null` — effects must (and do) soft-pass without a sim.
- Skill defs bypass `RegistryBase`/`--roundtrip-data`; `SaveLayout` only rewrites x/y.

## Related
- Buff application/path levels → `Game/BuffSystem.cs` (see [data-registries.md](data-registries.md)).
- Spellbar slots → the spellbar entry in [overview.md](overview.md).
- Crafting menus consuming unlocks → `UI/CraftingMenuUI.cs` / `UI/TableCraftMenuUI.cs`.
- Scenario coverage: `UISkillBookScenario`, `UISkillLayoutScenario`, `ItemConsumeScenario`,
  `IntrinsicBuffScenario`, `PathBuffScenario`, `CorpseEaterScenario`, `SpellKillTallyScenario`.
