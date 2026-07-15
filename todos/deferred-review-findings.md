# Deferred findings from the 2026-07-15 correctness review

Verified findings from the 6-agent codebase review that the user chose to leave
for now ("leave what you didnt fix yet"). Each was confirmed against source at
review time — re-verify line numbers before fixing, code moves.

## Needs a design decision first

- **Boar charge: def vs Monstrology skill.** Commit `6a7d970` (zombie-parity
  balance pass) put `weapon_boar_charge` back on the ZombieBoar def, but the
  `intrinsic_buff` scenario (and the Monstrology skill design) expect charge to
  be stripped from the def and granted via `grant_intrinsic_buff`. The scenario
  FAILS on phase 1 because of this — pre-existing, not from the review fixes.
  Either update the scenario + retire the skill grant (parity wins), or strip
  the def again (skill-tree wins).

## Confirmed bugs, low urgency

- **Trample recovery isn't a movement lockout** (`TrampleSystem.cs` ~27/92;
  `Simulation.cs` UpdateMovement only skips phases 1/3). During the 0.6s
  "recovery" the unit moves normally; only attacks are blocked. The intended
  post-charge vulnerability window is cosmetic.
- **Corpse-target Summon with SummonQuantity>1 raises N units from one body**
  (`SpellEffectSystem.ExecuteSummonSpell`, single-corpse branch — every loop
  iteration queues a rise against the same `pending.TargetCorpseID`). Decide:
  one body = one raise, or spread across nearby corpses.
- **Trap owner default targets friendly undead** (`EnvironmentSystem.cs`:
  `BuildingDefaultOwner = 1` vs the trap faction rule `Owner == 0 → Undead`).
  A trap def relying on the default owner evaluates as Human-built and targets
  the player's own undead. Reconcile the two conventions (explicit trap
  owner/faction field, or placement always sets Owner).
- **`speed` dev command clears ALL pause sources** (`Game1.Dev.cs` ~263:
  `_clock.ClearAllPauses()`) including the pause menu's `User` pause and
  `Inspect`. Scoping to `PauseSource.Dev` fixes it but loses the "setting
  speed also unpauses" convenience.
- **SpriteQueue 16-bit seq saturation** (`SpriteQueue.cs` ~333): past 65535
  submissions in one pass the tiebreaker pins and equal-key sprites can
  Z-flicker (unstable List.Sort). Needs a wider sort key or secondary stable
  sort; only matters in very large horde scenes.
- **Save doesn't persist skill-book state** (`SaveGameData.cs` ~11, documented
  gap): load restores form + spellbar but wipes learned skills/points, so a
  restored spellbar can hold spells the necromancer no longer qualifies for.
  Feature work: serialize SkillBookState.

## Cosmetic / debatable (from the same review, even lower priority)

- `depthfog` dev command / H key persists `Performance.DepthSortedFog` to
  settings.json on exit — consistent with how settings work, but an A/B dev
  switch commits whatever state was last tested. Session-only shadow override
  if it ever bites (the hover equivalent was fixed in `efe8eb0`).
- Dead `SoftCircle` texture path in `WadingWakeSystem` (bow wave actually uses
  the MiniSplash flipbook; comments were fixed, dead code remains).
- `RangedUnitHandler` non-archer else-branch is dead (casters have their own
  handler) — removable.
