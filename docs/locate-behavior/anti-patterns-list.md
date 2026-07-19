*Contains list of found anti patterns in the codebase.*
# Gameplay in Game1.

At least these two are done in animation code:
1. ExecuteSpellEffect(spell, i, pca.Target, pca.Slot, _pendingSpell);
2. _sim.TryResolvePendingAttackAtImpact(i);

# Table/necro-bench crafting gated on animation completion (EGREGIOUS)

`Game1.Animation.cs` `UpdateAnimations` — the `CorpseInteractPhase` state machine — runs
gameplay-critical logic inside the animation tick, gated on `animData.Ctrl.IsAnimFinished`:

- **case 5 (PutDown)**: on `IsAnimFinished`, transfers the carried corpse into the table
  slot (`TableSystem.LoadCorpseIntoTable`), removes the corpse from the sim
  (`_sim.CorpsesMut.RemoveAt`), resets carry state, AND (commit `4f1e851`) fires
  `StartTableCraft(tableIdx)` — which spends essence and queues a zombie raise. So dropping
  a corpse on the bench only crafts once the PutDown *animation* finishes; craft start is a
  side effect of an animation edge.
- **case 4 (Pickup)** and **cases 1/2/3 (WorkStart/Loop/End bagging)** similarly advance
  `CorpseInteractPhase` and consume corpses off `IsAnimFinished` / anim-driven timers.

Fix direction: move the phase timing into gameplay (sim) code with explicit timers (like
`TableCraftingSystem` already does for the craft loop via `BaggingDuration`/`LoopBudget`),
and have the animation merely *reflect* the phase. The corpse transfer + `StartTableCraft`
should fire from a gameplay timer/phase transition, not from `IsAnimFinished`.
