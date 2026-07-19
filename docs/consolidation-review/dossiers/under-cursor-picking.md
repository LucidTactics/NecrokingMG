# Dossier: World under-cursor picking / click-target resolution

Judge verdict on the labeling-pass claim that the game has "N ways to resolve what's
under the cursor" and needs ONE world-pick API.

## Headline

**The consolidation the evidence asks for already happened — today.**
`Necroking/Game/WorldQuery.cs` (`_sim.Query`, owned by `Simulation`, dated 2026-07-06 in
`docs/locate-behavior/world.md:62` and `docs/standard_patterns.md:68`) is the declared
single canonical home for "nearest X / X under cursor / all X in radius" over units, env
objects, and corpses, with struct-generic filters (`EnvForagables`, `EnvWorkerHomes`,
`EnvByDefIndex`, `EnvBerryBushes`, `CorpseExclude.Free`) and an explicit contract that
"under cursor" = `Nearest*(mouseWorld, pickRadius)`. Most of the labeler's cited methods
are now thin named wrappers over it:

| Cited method | Reality |
|---|---|
| `Game1.WorldClicks.cs:20 FindGraveUnderCursor` | one-liner → `_sim.Query.NearestEnvObject(…, EnvWorkerHomes)` |
| `Game1.WorldClicks.cs:24 FindCorpsePileUnderCursor` | → `_sim.Query.NearestEnvObject(…, pileDef)` |
| `Game1.WorldClicks.cs:46 FindNearestCorpsePileInRange` | → `_sim.Query.NearestEnvObject(…, StockedCorpsePiles)` — the documented caller-side filter escape hatch (WorldQuery.cs:13-15) |
| corpse hover `_hoveredCorpseIdx` (`Game1.cs:2580`) | → `_sim.Query.NearestCorpse(…, CorpseExclude.Free)` |
| unit hover (`Game1.cs:2586`) | → `_sim.Query.UnitUnderCursor(…)` |
| `Game1.Crafting.cs:28 FindNearestForagable` | → `_foragables.FindNearest` → `_sim.Query.NearestEnvObject(…, EnvForagables)` (`Game/ForagableSystem.cs:88`) |
| `Game1.cs:4299 FindClosestEnemyToPoint` | one-liner → `_sim.Query.NearestEnemyToPoint(…)` |

What remains is a short list of pre-WorldQuery stragglers (the project's own
`standard_patterns.md:78-81` already tracks a migration list; the ones below are
click-resolution-specific and NOT on that list).

---

## Finding 1 — "One ranked world-pick API (kind mask)" mega-proposal — KEEP_SEPARATE (severity: low)

**Claim:** the game "cycles through finders sequentially on click — inefficient and N
ways to do one thing"; should there be one `point → ranked pickable, filtered by kind
mask` API?

**Verdict: KEEP_SEPARATE.** The sequential chain in
`Game1.WorldClicks.cs:85-119 HandleWorldLeftClick` (placement → panel routes
[table/grave] → pile gather → enemy attack) and `:121-140 HandleWorldRightClick`
(foragable → pile → enemy) is a **priority chain, not duplication**:

- The *mechanics* (best-distance scan with filter) already have one implementation —
  WorldQuery. Each chain step supplies only a filter and an action. That is exactly the
  CLAUDE.md consolidation rule: shared component owns mechanics, caller owns data.
- The variance between steps is structural: different pick radii per kind
  (`clickRange 1.6f` for buildings, `HoverPickRadius` for units, necro-relative `2f`
  for foragables — note foragables pick nearest to the *necromancer*, not the cursor,
  per `world.md:53`), different consume/feedback semantics per branch ("Pile Empty" /
  "Too Far" own the click even on failure, WorldClicks.cs:142-162, 173-188), and
  branch-specific gating (carrying state, placement mode, mid-cast). A ranked kind-mask
  pick would have to re-expose all of that per kind — a framework, not a utility.
- Efficiency is a non-issue: the scans run only on the frame of an actual click, and
  each is a linear pass over a few hundred entries (WorldQuery.cs:119-121 explicitly
  reserves the right to drop a spatial index behind the facade with zero call-site
  changes).
- The code already carries the right future design note for when the chain grows:
  `Game1.WorldClicks.cs:53-59` — "When a third building panel arrives, consider one
  nearest-interactable pick + an env-def-tag → open-action dictionary instead of
  per-route picks." That is the correct trigger point; pre-building it now for two
  routes is premature.
- `GameRenderer.Units.cs:1226 PickHoveredObject` is genuinely a different mechanism
  (screen-space drawn-marker footprint hit-testing for buildings + world radius for
  ground items, for tooltips/highlight) and should not be folded into WorldQuery.

## Finding 2 — Click-to-melee resolved twice with divergent range formulas — CONSOLIDATE (severity: high)

Two implementations of "player orders the necromancer to melee the enemy at the cursor":

1. **`Game1.WorldClicks.cs:167 TryAttackClick`** (LMB/RMB world click):
   pick = `FindClosestEnemyToPoint(mouseWorld, Tooltips.HoverPickRadius)` (WorldQuery);
   reach gate = `GameSystems.Combat.MeleeRangeUtil.Compute` — comment says "same SSOT
   formula the AI and sim use"; stamps `Target` + `PendingAttack`;
   `AttackCooldown = 2f` **hardcoded**; "Too Far" feedback.
2. **`Game1.cs:4084 TryMeleeOrGather`** (the `melee_gather` built-in ability,
   dispatched from `Game1.Spells.cs:352`, live on the spell bar per
   `UI/HUDRenderer.cs:415-482`): hand-rolled `bestSq` scan over all units (violates
   `standard_patterns.md:68` "NEVER write a new bestSq scan");
   mouse radius **hardcoded 3f**; melee range **hand-derived**
   `1.0f + MeleeWeapons[0].Length * 0.15f` (Game1.cs:4093-4094) — base 1.0 vs the SSOT's
   `Settings.Combat.MeleeRange` (default 0.8) + **unit** Length·0.15 + both radii
   (`Game/Combat/MeleeRangeUtil.cs:19-24`); cooldown from unit def / settings; stamps
   only `PendingAttack` (no `Target`); silent on failure, falls through to forage.

This is already-diverged gameplay logic: keyboard melee and click melee reach different
distances and set different cooldowns for the same player intent, and
`standard_patterns.md:66-67` names `MeleeRangeUtil.Compute` the single source for
"am I close enough to melee". Both callers are in Game1 partials; no Net/ contact.

**Canonical design:** one helper on the Game1 combat-input surface, e.g.

```csharp
// Pick enemy at cursor (WorldQuery), gate by MeleeRangeUtil.Compute (SSOT),
// stamp Target+PendingAttack+def cooldown. Feedback optional per caller.
private bool TryOrderMeleeAtCursor(int necroIdx, Vec2 mouseWorld,
    float pickRadius, bool feedbackOnFail)
```

- `TryAttackClick` → calls it with `HoverPickRadius`, `feedbackOnFail: true`
  (keeps its consume-the-click semantics), and inherits the def-based cooldown
  (fixing the hardcoded `2f` — flag to user, tiny behavior change).
- `TryMeleeOrGather` → calls it first (respecting its existing
  `AttackCooldown/PostAttackTimer` gate), falls to forage on false.
- Decide one pick radius (HoverPickRadius vs 3f) — recommend HoverPickRadius, it is
  the user-tunable setting (`Editor/SettingsWindow.cs:650`).

**Call sites to migrate:** `TryAttackClick` (WorldClicks.cs:167), `TryMeleeOrGather`
(Game1.cs:4084). **Effort: S. Risk: low** (two call sites; behavior deltas are the
point — verify with a quick drive-game melee check both via click and via the
melee_gather slot).

## Finding 3 — `FindBerryBushNear` is an unmigrated scan whose WorldQuery filter already exists, unused — CONSOLIDATE (severity: low)

`Game1.cs:4133 FindBerryBushNear` hand-rolls the env scan (IsBerryBush + rt.Alive +
`BerryState == Berries`, exclusive r²) — semantically **identical** to
`_sim.Query.NearestEnvObject(pos, r, new EnvBerryBushes())`; the `EnvBerryBushes`
filter (`Game/WorldQuery.cs:89-97`) exists and has **zero callers** — it was evidently
written for this migration and the call site never switched.

**Migration:** replace the body with the one-liner (keep the named wrapper, matching
`FindGraveUnderCursor` style). Call sites unchanged: `Game1.Spells.cs:471,474`
(poison-berries ability). **Effort: S (one method body). Risk: minimal.**

## Finding 4 — `TryPickTetherEnd` hand-rolls corpse+unit nearest scans — CONSOLIDATE (severity: low)

`Game1.cs:150 TryPickTetherEnd` scans corpses (gate = Consumed|Dissolving|Bagged,
optionally +Dragged — exactly `CorpseExclude.Free` / `Free & ~Dragged`) then units
(Alive, any faction — exactly `UnitUnderCursor`'s gate) for the single nearest endpoint
within `RopeAttachRadius`. Both halves duplicate WorldQuery logic; only the
cross-collection "nearest of either" comparison is novel. Note the sibling rope path at
`Game1.cs:272` already uses `_sim.Query.NearestCorpse(from, RopeAttachRadius,
CorpseExclude.Free)` — same file, two styles.

**Migration:** call `_sim.Query.NearestCorpse(pos, RopeAttachRadius, includeClaimed ?
Free & ~Dragged : Free)` and `_sim.Query.UnitUnderCursor(pos, RopeAttachRadius)`, then
compare the two winners' `LengthSq` to pick the endpoint (distance recompute from the
returned index is trivial). Callers unchanged: `Game1.cs:245`, `Game1.cs:3553`,
`Game1.Dev.cs:1348`. **Effort: S. Risk: low** (pure refactor; identical gates verified
against `WorldQuery.IsExcluded`, Game1.cs:156-173).

---

## Constraints check

- No `Necroking/Net/` contact anywhere in these findings.
- No renderer changes (Finding 1 explicitly leaves `PickHoveredObject` alone).
- No map-content implications.

## Note for the standard-patterns list

Findings 2-4 should be appended to the "still-unmigrated scans" list in
`docs/standard_patterns.md:78-81` / `docs/locate-behavior/world.md:68-70` if not
fixed immediately — the current list (WorkerSystem, CorpseInteractionManager,
SpellCasting/Projectile, VillageThreat/BoarForageAI) omits `TryMeleeOrGather`,
`FindBerryBushNear`, and `TryPickTetherEnd`.
