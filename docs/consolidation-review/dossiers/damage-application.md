# Dossier: Damage Application & Attack Resolution

Concept: do all damage paths converge on `DamageSystem` (death handling, attribution,
kill tallies), or do some subtract HP on their own?

## Verdict summary

Convergence is **much better than the evidence implied**. The labeling pass over-matched:
`DealDamage`, `ApplyDamageTick`, `TryTrampleHit`, `ExecuteSacrifice`, and the glyph/lightning
paths all delegate to `DamageSystem.Apply/ApplyDirect/ApplyAoE` or to `ResolveMeleeAttack`
(which itself ends in `ApplyDirect`). Kill tallies and corpse creation are fully centralized in
`Simulation.RemoveDeadUnits` (Simulation.cs:3454), keyed only off `Alive == false` +
`LastAttackerID`, so attribution/tally logic cannot diverge per damage source.

What *has* diverged is the small **"finalize a unit death" block** (HP=0, Alive=false,
Forced(Death) anim, `MarkDeathFromProne`) — re-implemented, each slightly differently, at
four sites.

## Verified call graph (evidence)

Converged paths — no action needed:
- `Simulation.DealDamage` (Simulation.cs:3244) — one-line wrapper: `DamageSystem.Apply(..., Physical, ArmorNegating, ...)`. Documented shortcut for magical strikes/traps. Not a duplicate formula.
- `Simulation.ResolveMeleeAttack` (Simulation.cs:2571) — full Dominions melee formula, then `DamageSystem.ApplyDirect` at :2772; weapon-coat poison via `DamageSystem.Apply` at :2793.
- `Simulation.ResolveMeleeSweep` (:2484) and `TrampleSystem.TryTrampleHit` (TrampleSystem.cs:270, via `ResolveMeleeAttackExternal` peek/forceHit) — dispatchers over `ResolveMeleeAttack`, not parallel damage code.
- `Simulation.ResolveArrowHit` (:2415) — ranged opposed roll, then `DealDamage` at :2439.
- `PoisonCloudSystem.ApplyDamageTick` (PoisonCloudSystem.cs:273) → `DamageSystem.Apply(Poison, ArmorNegating)`.
- `SpellEffectSystem` — `sim.DealDamage` (:498, :578 sacrifice-kill) and `DamageSystem.ApplyAoE` (:532). `ExecuteSacrifice`'s HP change on the *caster* is a heal (clamped add), not damage.
- `MagicGlyphSystem` (:220, :230) → `DamageSystem.ApplyAoE`.
- Lightning (Simulation.cs:815) → `DealDamage`.

Divergent sites (raw HP / Alive mutation outside DamageSystem):
1. `Simulation.TryApplyLimbChop` decapitation (Simulation.cs:2985-2987): sets `HP = 0`, `Alive = false`, `SetOverride(Forced(Death))` — **misses `MarkDeathFromProne`**. A knocked-down unit is easier to hit (defense −70%), so prone decapitation is a live case: the body visibly stands up to play Death, the exact bug MarkDeathFromProne exists to prevent (DamageSystem.cs:264 comment).
2. `PotionSystem` poison DoT tick (PotionSystem.cs:346-352): `Stats.HP -= dmg`, own green DamageEvent, own death check — **no Death anim override, no MarkDeathFromProne**. The bypass of `Apply` is *intentional and commented* (:330 — Apply's Poison type adds stacks; the tick converts stacks→HP, and must not set HitReacting or the unit re-flees every 3 s). The intent variance is legitimate; the death block duplication is not.
3. `TriggerSystem` `EffKillUnits` (TriggerSystem.cs:239): scripted region kill, raw `Alive = false` — no HP zeroing, no death anim. Corpse still appears via RemoveDeadUnits; cosmetic-only gap today.
4. Within DamageSystem itself: `Apply` (:160-166, :188-203) and `ApplyDirect` (:232-254) duplicate both the death block and the attribution/auto-engage block verbatim — including the DeerHerd archetype exemption, whose comment at :240 literally says "Same DeerHerd prey exemption as Apply — see comment there." Two copies of gameplay-policy code that already had to be edited in lockstep once.

## Findings

### F1 — Unit-death finalization duplicated at 4 sites — CONSOLIDATE (severity: medium, effort S, risk low)
Canonical home: `DamageSystem` (it already owns 2 of the 4 copies).

```csharp
/// Finalize a unit's death: zero HP, clear Alive, force the Death anim,
/// snap-to-final-frame if prone. The ONLY sanctioned way to flip Alive=false.
public static void Kill(UnitArrays units, int idx)
{
    units[idx].Stats.HP = 0;
    units[idx].Alive = false;
    Render.AnimResolver.SetOverride(units[idx], Render.AnimRequest.Forced(Render.AnimState.Death));
    MarkDeathFromProne(units, idx);
}
```

Call-site migration:
- `DamageSystem.Apply` Physical branch (:160) and `ApplyDirect` (:248) — replace inline blocks.
- `Simulation.TryApplyLimbChop` Head case (:2985) — fixes the prone-decap anim bug for free.
- `PotionSystem` poison tick (:348) — keeps its own HP subtraction + green number, calls `Kill` on death.
- `TriggerSystem.EffKillUnits` (:239) — scripted kills get proper HP=0 + death anim.
- (Optional) `AI/CorpsePuppetHandler.BeginDeposit` (:80) is NOT a death — it forces the Death anim on a live unit that later `PendingDespawn`s; leave it alone.

Risk: low — pure extraction; each replaced block is a strict subset or superset of `Kill`. The two
current *omissions* (prone-snap at decap, death anim at poison death) become behavior changes, but
they are the documented intended behavior.

### F2 — `Apply` vs `ApplyDirect` share attribution/auto-engage tail — CONSOLIDATE (severity: medium, effort S, risk low)
Same file, verbatim duplicate of the LastAttackerID + auto-EngagedTarget block with the DeerHerd
exemption (`Apply` :188-203, `ApplyDirect` :232-244). Next archetype exemption (docs/locate-behavior/ai.md:245
already tracks this pair as a coupled edit) will be added to one and forgotten in the other.
Extract `private static void StampAttacker(UnitArrays units, int targetIdx, int attackerIdx)`;
optionally make `Apply`'s Physical branch delegate its tail to `ApplyDirect`. Watch the one real
difference: `Apply` flinches (`ApplyHitReactAnim`), `ApplyDirect` deliberately does not (the melee
resolver flinches itself at Simulation.cs:2752) — keep the flinch out of the shared helper.

### F3 — Poison DoT HP drain bypasses DamageSystem — INVESTIGATE (severity: low)
PotionSystem.cs:344-353 is the only per-tick HP subtraction outside DamageSystem. The bypass is
intentional (no HitReacting, no auto-engage, no flinch, poison-green number) and commented as such.
Decision needed: (a) accept it as the debuff's own drain and just route its death through `Kill`
(F1 covers this), or (b) grow DamageSystem a `DamageType.DoT`/silent flag so the "HP loss + event +
death" trio has one owner and PotionSystem only computes the tick amount. (b) is cleaner if more
DoTs are coming (burn, bleed); (a) is fine if poison stays the only one. Do not force it through
`Apply(Physical)` — that would add flinch + auto-engage, which the comment explicitly forbids.

### F4 — Melee vs ranged resolvers — KEEP_SEPARATE (severity: low)
`ResolveMeleeAttack` (237 lines: fatigue, shields, hit location, AP/AN, limb cap, coats, knockdown)
and `ResolveArrowHit` (26 lines: precision-vs-parry roll, arc-rolled hit location, flat piercing
reduction) are both Dominions-ported but structurally different state machines — different opposed
rolls, different mitigation stacks, different side-effect sets, and both already funnel the actual
HP change into DamageSystem. Merging them would be flag soup (structural variance, per CLAUDE.md
consolidation rules). The only shared fragment (protection roll with 15% piercing) is ~3 lines —
below abstraction threshold.

## Constraints check
- No `Necroking/Net/` involvement.
- Renderer untouched; `Kill` reuses the existing `AnimResolver.SetOverride` call already present at every site.
