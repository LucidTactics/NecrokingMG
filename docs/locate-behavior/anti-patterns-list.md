*Contains list of found anti patterns in the codebase.*
# Gameplay in Game1.

At least these two are done in animation code:
1. ExecuteSpellEffect(spell, i, pca.Target, pca.Slot, _pendingSpell);
2. _sim.TryResolvePendingAttackAtImpact(i);

# Table/necro-bench crafting gated on animation completion

`Game1.Animation.cs` `UpdateAnimations` — the `CorpseInteractPhase` state machine — runs
gameplay-critical logic inside the animation tick, gated on `animData.Ctrl.IsAnimFinished`.

- **case 5 (PutDown) — FIXED** (the egregious one). Was: on `IsAnimFinished`, transfer the
  carried corpse into the table slot, remove it from the sim, reset carry state, AND (commit
  `4f1e851`) fire `StartTableCraft` — so a corpse only crafted once the PutDown *animation*
  finished. Now: `Game1.BeginCorpsePutDown` sets the visual phase and **schedules** the
  transfer+craft on the sim clock via `_sim.ScheduledEvents` (fired in `Simulation.Tick`);
  `Game1.CompleteCorpsePutDown` does the gameplay; the animation is fitted to the same
  duration and merely reflects it. Both the table-load and the ground-drop go through this.
  The imbue-table craft loop is fitted to `ProcessTime` via `AnimTiming.FitChannel`. This is
  the worked example for the **[Canonical resolution](anti-patterns.md)** — copy it.
- **case 4 (Pickup)** and **cases 1/2/3 (WorkStart/Loop/End bagging) — STILL COUPLED.** They
  still advance `CorpseInteractPhase` and consume corpses off `IsAnimFinished` / the anim-tick
  `BaggingTimer >= BaggingDuration` (const `2.0f`). Bagging's payload (`bc.Bagged = true`) still
  fires in case 3 on `IsAnimFinished`; note case 2 is shared with handler-driven trap building,
  so converting it touches AI handlers. Fix the same way when you're next in here: schedule the
  phase payloads via `ScheduledEvents` and fit the anim via `AnimTiming` (see the canonical
  resolution). Don't fix one and leave the matched pair — that's a sync-bug waiting to happen.
