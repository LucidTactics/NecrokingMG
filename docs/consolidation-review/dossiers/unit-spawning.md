# Dossier: Unit Spawning Paths

Concept: is there one canonical unit-spawn pipeline? Judged 2026-07-06 against the working tree.

## Verified pipeline map

```
UnitArrays.AddUnit (Movement/UnitModel.cs:582)          — slot + id allocation ONLY (single impl, fine)
  └─ Simulation.SpawnUnitByID (Game/Simulation.cs:3730) — AddUnit + def lookup + BuildStats
       + ApplyDefRuntimeFields + skill-tree intrinsic buffs
       └─ Simulation.SpawnZombieMinion (:3778)          — + Undead faction, archetype fallback,
            horde enroll, cap-category lint (documented canonical raise path)
  └─ Game1.SpawnUnit (Game1.cs:2209)                    — RE-IMPLEMENTS SpawnUnitByID's core
       (AddUnit + def lookup + BuildStats + ApplyDefRuntimeFields) instead of calling it,
       then adds necro-index, horde auto-enroll, inline anim wiring
       └─ SpawnNetGhost (Game1.Net.cs:108)              — wrapper + ghost fixups (fine)
       └─ Villages/Zones/Dev/SpellEffectSystem callers
  └─ WorkerSystem.SpawnReanimated (Game/Jobs/WorkerSystem.cs:631) — pure delegate indirection;
       wired in Game1.cs:2500 to QueueReanimRise → Simulation.cs:723 SpawnZombieMinion (fine;
       doc comment at WorkerSystem.cs:636 "wires this to its SpawnUnit" is STALE)
```

**ai.md claim verified TRUE**: `ApplyDefRuntimeFields` (Simulation.cs:3675) is the single
def→unit runtime copy; Game1.SpawnUnit (Game1.cs:2221), SpawnUnitByID (:3742) and
TransformUnit (:3858) all call it. The old "SpawnUnitByID doesn't wire archetype" gap is
closed — many scenario comments (SummonLagScenario.cs:90, PounceTestScenario.cs:55,
DeerFleeNoSlideScenario.cs:69, CombatAnimReviewScenario.cs:69) are stale comment rot.

## Finding 1 — Game1.SpawnUnit duplicates SpawnUnitByID's core (CONSOLIDATE, high)

`Game1.SpawnUnit` (Game1.cs:2209-2232) repeats AddUnit + `Units.Get` + `BuildStats` +
`ApplyDefRuntimeFields` line-for-line instead of delegating to `_sim.SpawnUnitByID`.
**They have already diverged**: SpawnUnitByID applies skill-tree intrinsic buffs
(Simulation.cs:3748-3757); Game1.SpawnUnit does not. GrantIntrinsicBuffEffect
(Game/SkillEffects/SkillEffects.cs:303) explicitly says *"New spawns are handled by the
spawn-path hook in Simulation.SpawnUnitByID"* — the author believed coverage was complete.
It is not: **summon spells** (`game.SpawnUnit` at Game/SpellEffectSystem.cs:346), dev spawns,
and map-placed units (Game1.cs:1558) bypass the hook. A summoned skeleton misses an
intrinsic buff a reanimated zombie gets. Latent live bug; the gap widens with every field
added to SpawnUnitByID.

**Canonical home**: `Simulation.SpawnUnitByID` stays the sim-level core. Merged API:

```csharp
internal int SpawnUnit(string unitDefID, Vec2 pos)
{
    int idx = _sim.SpawnUnitByID(unitDefID, pos);   // core copy + intrinsic buffs
    var def = _gameData.Units.Get(unitDefID);
    if (def == null) return idx;
    if (def.AI == "PlayerControlled") _sim.SetNecromancerIndex(idx);
    // horde auto-enroll (unchanged) + EnsureUnitAnim(idx, unitDefID)  [see Finding 2]
    return idx;
}
```

**Call sites to migrate**: none — only SpawnUnit's body changes; every caller
(Villages, Zones, Net ghost, Dev, SpellEffectSystem, map load) keeps its signature.
**Effort**: S. **Risk**: medium — intentional behavior change (Game1 spawns now receive
tag-filtered intrinsic buffs; villagers/animals unaffected unless a skill's tags match).
Run scenario suite; check SummonLag/IntrinsicBuff scenarios.

## Finding 2 — three "build AnimController from def" copies, two missing AnimTimings (CONSOLIDATE, medium)

Same ~30-line block (Init + ForceState + SetAnimMeta + SetAttackAnimOverride + RefFrameHeight
from PickIdleFrames + UnitAnimData) exists in:
1. `Game1.SpawnUnit` inline, Game1.cs:2235-2282 — **full** (includes `SetAnimTimings`, :2253-2263)
2. `RebuildUnitAnim`, Game1.Animation.cs:29-64 — **missing SetAnimTimings**
3. lazy init in the per-frame anim tick, Game1.Animation.cs:341-370 — **missing SetAnimTimings**

Copy 3 is the path that populates anims for *every* unit spawned via SpawnUnitByID /
SpawnZombieMinion (triggers, potion raises, table crafting, scenarios) and for def-swaps
(CachedDefID mismatch eviction at :337). Per-unit `AnimTimings` overrides authored in the
unit editor — including `EffectTimeMs`, which drives `JustHitEffectFrame` →
`ResolvePendingAttack` (Game1.Animation.cs:810-839), i.e. when damage lands — are silently
dropped for those units. Already-diverged gameplay-adjacent behavior.

Related but separate consumers: GameRenderer.Corpses.cs:94-101 (reanim preview; meta only —
acceptable, preview) and :155-169 (corpse anims; meta + timings, no attack override —
corpse-specific state handling follows).

**Canonical home**: one factory on Game1 (Game1.Animation.cs), e.g.
`internal UnitAnimData? BuildUnitAnimData(UnitDef def)` returning null when sprite/atlas
unresolvable; callers 1-3 use it (`EnsureUnitAnim(idx, defId)` convenience for 1 and 3;
RebuildUnitAnim becomes a two-liner). Corpse variant may share it with the attack-override
included (harmless for corpses) or stay separate.
**Call sites**: Game1.cs:2235 block, Game1.Animation.cs:29 and :341; optionally
GameRenderer.Corpses.cs:155. **Effort**: S. **Risk**: low (pure extraction; the only
behavior change is timings now applying on lazy path — the fix).

## Finding 3 — ScatterSpot / ScatterSpotInRect / ScatterSpotNear (CONSOLIDATE, low)

Three private statics with identical mechanics — 24 tries, same LCG
(`rng * 1664525u + 1013904223u`), same `IsPointWalkable(grid, p, 0.5f)` check, same
fallback-to-center — differing only in the sampled region:
- annulus: Game1.Villages.cs:226
- rect (90% half-extents): Game1.Zones.cs:147
- circle: Game1.Zones.cs:287 (doc: "mirror of ScatterSpotInRect for a circle")

Variance is data-level (the candidate-point formula), not structural — per CLAUDE.md this
is exactly the shared-mechanics/caller-owns-data case. **Canonical home**: a small static
`ScatterSpots` helper (e.g. `Necroking/World/` or next to `SubroutineSteps`) with the three
region overloads sharing one retry/walkability core (region as a switch or inlined
two-random-draws per shape; avoid delegates because of `ref uint rng`). Triplicated magic
numbers (tries=24, radius 0.5f, LCG constants) become single-source.
**Call sites**: Villages.cs:166/183/210; Zones.cs:136/265/266/343 (via TryPlaceSpaced).
**Effort**: S. **Risk**: low — keep per-call rng streams identical to preserve deterministic
layouts.

## Finding 4 — SpawnGroup (Villages) vs SpawnZoneGroup (Zones) (INVESTIGATE, low)

Game1.Villages.cs:178 and Game1.Zones.cs:131 are the same loop (scatter → SpawnUnit → set
VillageId + SpawnPosition), differing only in scatter region; Zones.cs:129 even documents
itself as a "Mirror of the legacy SpawnGroup". Zones is the editor-driven successor; the
villages `_villages.json` loader is called "legacy" in Zones.cs's own header (:23-25).
**Decision needed**: deprecate/delete the villages-json population path (leaving zones as
the single implementation) vs. unify the two 12-line loops. If any shipped map still uses
`<map>_villages.json`, keep both until it's migrated; merging two tiny loops that may soon
be one is not worth doing before that call is made.

## Finding 5 — spawn-stack layering (KEEP_SEPARATE)

`UnitArrays.AddUnit` / `SpawnUnitByID` / `SpawnZombieMinion` / `SpawnNetGhost` /
`WorkerSystem.SpawnReanimated` are **intentional layers, not duplicates**: allocation core →
def-applied sim spawn → raise-into-horde policy (with the undeadCategory cap lint at
Simulation.cs:3805 deliberately placed at that single choke point) → net-ghost fixups →
decoupling delegate. Each adds domain state on top of the same canonical core; the variance
is structural (different post-spawn policy), which CLAUDE.md says not to abstract. After
Finding 1's merge, every def-based spawn genuinely funnels through
SpawnUnitByID→ApplyDefRuntimeFields.

## Cleanup notes (no verdicts)

- `SpawnNetGhost` ignores `SpawnUnit`'s return value and recomputes `_sim.Units.Count - 1`
  (Game1.Net.cs:117-118) — use the return value. (Game1.Net.cs is safe glue; `Necroking/Net/`
  itself untouched.)
- Stale comments: WorkerSystem.cs:636 ("wires this to its SpawnUnit" — actually
  QueueReanimRise); scenario comments claiming SpawnUnitByID doesn't wire archetype
  (it does, since ApplyDefRuntimeFields); some scenarios still manually set Archetype after
  SpawnUnitByID — now redundant but harmless.
