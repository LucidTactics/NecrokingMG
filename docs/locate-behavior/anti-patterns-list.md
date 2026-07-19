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
  transfer+craft on the sim clock via `_sim.Tasks` / `CorpsePutDownTask` (fired in `Simulation.Tick`);
  `Game1.CompleteCorpsePutDown` does the gameplay; the animation is fitted to the same
  duration and merely reflects it. Both the table-load and the ground-drop go through this.
  The imbue-table craft loop is fitted to `ProcessTime` via `AnimTiming.FitChannel`. This is
  the worked example for the **[Canonical resolution](anti-patterns.md)** — copy it.
- **case 4 (Pickup)** and **cases 1/2/3 (WorkStart/Loop/End bagging) — STILL COUPLED.** They
  still advance `CorpseInteractPhase` and consume corpses off `IsAnimFinished` / the anim-tick
  `BaggingTimer >= BaggingDuration` (const `2.0f`). Bagging's payload (`bc.Bagged = true`) still
  fires in case 3 on `IsAnimFinished`; note case 2 is shared with handler-driven trap building,
  so converting it touches AI handlers. Fix the same way when you're next in here: schedule the
  phase payloads via `_sim.Tasks` (`ScheduledTask` subclasses) and fit the anim via `AnimTiming`
  (see the canonical resolution). Don't fix one and leave the matched pair — that's a sync-bug
  waiting to happen.

# Hand-ticked countdown fields on persistent objects (port to ScheduledTasks)

The delayed-execution framework (`Necroking/Game/ScheduledTasks.cs`, see the principle in
[anti-patterns.md](anti-patterns.md)) landed 2026-07-19; wave 1 ported `CorpsePutDownTask`,
`ReanimRiseTask`, `ProjectileVolleyTask`. Known remaining hand-ticked timers to port when
next touched (full plan: `todos/scheduled-tasks-framework.md`):

- Sim-clock repeating accumulators ("every N seconds do a scan"): `Game1.Zones.cs`
  `_zoneSpawnTickTimer`; `Game/Jobs/WorkerSystem.cs` `_dispatchTimer`; `Game/HordeSystem.cs`
  `_aggroScanTimer`; `Game/Simulation.cs` `_moraleCheckTimer` / `_fatigueRegenTimer` /
  `_harassmentDecayTimer` / `_wolfHuntCmdTimer`; `Game1.Net.cs` `_netSendTimer`;
  `Game/ForagableSystem.cs` `_autoPickupCooldown`; `Game/TriggerSystem.cs` cooldown/period timers.
- Raw-dt UI countdowns in `Game1.cs` (`_hoverVariantLabelTimer`, `_depthFogToastTimer`,
  `_gpuWarnToastTimer`, `_devChannelHoldTimer`) — need a Game1-owned real-time `ScheduledTasks`
  instance ticked on `_rawDt`; these are render-queried countdowns, so port by holding the task
  reference and reading `SecondsLeft`.
- Editor `_statusTimer` copy-pasted across ≥4 editor windows (`ItemEditorWindow`,
  `MapEditorWindow`, `UnitEditorWindow`, `WallEditorWindow`) even though the `EditorWindow` base
  already has `StatusTimer` — single-source-of-truth violation; consolidate onto the base.

NOT anti-patterns (stay put per the exception): buff `RemainingDuration`, per-unit bulk timer
arrays in `Simulation`, potion per-unit poison/paralysis timers, projectile/cloud TTLs,
env respawn/trap timers, render-side TTLs, spell cooldown dictionaries (queryable state).
