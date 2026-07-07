# Consolidation Review — 2026-07-06

A full semantic-duplication review of the C# codebase: every one of the 3,551
methods/constructors was extracted (Roslyn, `tools/MethodExtractor/`), labeled by intent
(27 Sonnet agents, taxonomy of 43 verbs × targets × mechanisms), clustered, cross-checked
against a top-down concept inventory from `docs/locate-behavior/`, and the resulting 20
concept units were each investigated and judged by a Fable agent reading the actual code.

**Result: 116 findings — 58 CONSOLIDATE, 9 INVESTIGATE, 49 KEEP_SEPARATE.**

- [verdicts.md](verdicts.md) — every finding, one-liner each, grouped by unit.
- [dossiers/](dossiers/) — one full dossier per unit: code evidence with file:line,
  proposed canonical design, migration notes.
- Actionable queue: [`memory/consolidation_opportunities.md`](../../memory/consolidation_opportunities.md).

The KEEP_SEPARATE findings are as valuable as the CONSOLIDATE ones: ~half the labeler
evidence dissolved on inspection (already-consolidated systems, intentional layering,
structural variance). Read a unit's dossier before acting on it.

## Shipped bugs found during verification

The judges confirmed these are live divergence bugs, not just style issues:

| Bug | Where | Dossier |
|---|---|---|
| Spell summons / map-placed / dev spawns silently miss skill-tree intrinsic buffs (only `SpawnUnitByID` applies them) | `Game1.cs:2209` vs `Simulation.cs:3748` | unit-spawning |
| Frenzied archers/casters/predators calmly walk home — frenzy handling exists only in `CombatUnitHandler`'s copy of the sentry skeleton | `AI/*Handler.cs` | ai-handler-boilerplate |
| Circle trigger regions silently become rectangles on load (`SaveTriggers` writes `shape`, `LoadTriggers` never reads it) | `MapEditorWindow.cs:6355` / `MapData.cs` | registry-json-io |
| Saved road junctions are parsed but never restored (`SetJunctions` has zero callers) | `RoadSystem.cs:77` | registry-json-io |
| `WeaponBonusEffect` expiry ticker doesn't exist — weapon bonus effects never expire | `Game/WeaponBonusEffect.cs` | buff-effect-application |
| Horde facing rotates the long way around (`LerpAngle` C# negative-modulo sign bug) | `HordeSystem.cs:571` | small-util-duplicates |
| Trap zaps skip magic resistance and kill attribution (hand-copied Strike path) | `Game1.cs:3971` | vfx-floating-text / casting-pipelines |
| NPC casters ignore `spell.Category` — a priest with a Cloud spell sky-strikes | `CasterUnitHandler.cs:209` | casting-pipelines |
| Unit-editor anim-timing overrides (incl. attack damage timing) never apply to trigger/potion/craft-spawned units — 2 of 3 anim-init copies drop `SetAnimTimings` | `Game1.Animation.cs:29,341` | unit-spawning |
| Click-to-melee has two diverged range/cooldown formulas (`TryAttackClick` vs `TryMeleeOrGather`) | `Game1.cs:4084` | under-cursor-picking |
| Workers ignore the bagged-corpse exclusion (`FindNearestCorpseObj` diverged from the canonical filter) | `WorkerSystem.cs:344-460` | nearest-envobject-queries |
| Main map save is non-atomic (`File.Create`) — crash mid-save corrupts the map | `MapEditorWindow.cs:6018` | registry-json-io |

## Suggested execution order

1. **Bug-adjacent consolidations first** (each fixes a live bug while consolidating):
   unit-spawning (SpawnUnit → SpawnUnitByID; one BuildUnitAnimData), casting-pipelines
   (ExecuteStrike as the one strike executor), ai-handler-boilerplate (SentryTransitions),
   registry-json-io (map sidecar DTOs), buff-effect-application (weapon coats →
   WeaponBonusEffect with a real expiry tick), small-util-duplicates (MathUtil angle helpers).
2. **Cheap S-effort wins**: under-cursor-picking stragglers, nearest-query reroutes,
   projectile arc solver, dead code deletions (PaintObjects, GetItemTexture,
   UndoObjectRemove, SettingsGeneralTab dead overloads), Lerp/IndexOf/premultiply dedups.
3. **Editor/UI mechanics extractions** (M effort, editor-only risk): RegistryCrudPanel,
   EditorBase FieldCore, DrawUtils primitive migration, SideListMenu, RichTip, TextureCache.
4. **Design decisions (INVESTIGATE) to settle with the team**: unit resource model
   (NecroState vs per-unit mana — the real "one casting pipeline" blocker), env_defs DTO
   with converters, paralysis as an Incapacitating buff, villages-json deprecation,
   DoT damage entry point, preview-vs-game trajectory 0.5f.

## Method notes (for re-running)

- `tools/MethodExtractor/` — Roslyn extractor (build with `dotnet build`, run via
  `tools/run_method_extractor.py`).
- `tools/cluster_labels.py` — joins labels + catalog into clusters/summary.
- The labeling taxonomy and workflow prompts are reproducible; labeling cost ~2.6M agent
  tokens (Sonnet), judgment ~1.8M (Fable). Re-run after major refactors, not per-change.
