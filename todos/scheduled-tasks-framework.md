# Unified delayed-execution framework (ScheduledTasks)

Goal: one uniform framework for "do X after N seconds" / repeating-interval work,
Unity-coroutine-like but **class-based**: a `ScheduledTask` base class with declared
subclasses per behavior, so active tasks are trackable and loggable by name. No anonymous
`Action` scheduling. Persistent objects should stop carrying ad-hoc countdown fields
unless the timer IS the object's lifetime (projectile TTL, buff duration).

## Existing starts incorporated

- `Necroking/Game/ScheduledEvents.cs` — the seed. Generic sim-clock scheduler with
  handles, Cancel, two-pass no-reentrancy Tick, deterministic (ticked inside
  `Simulation.Tick`, phase "scheduled_events"). Weakness: bare `Action`s — untrackable.
  **Evolved in place** into the new framework (renamed file/class), not duplicated.
- `_pendingReanimRises` (Game1.Spells.cs) — one-shot delayed multi-step resolution.
- `_pendingProjectiles` (Game1.cs/Game1.Spells.cs/SpellEffectSystem.cs) — repeating
  interval variant (volley follow-up shots) → motivates `Repeat()` support.
- `Necroking/Render/AnimTiming.cs` — untouched sibling (animation *fits* the scheduled
  duration). The pairing stays the canonical timing-vs-animation pattern.

## Phase 1 — Framework  [DONE]

`Necroking/Game/ScheduledTasks.cs` (git mv from ScheduledEvents.cs):

- `abstract class ScheduledTask` — `Handle`, `SecondsLeft`, `virtual Describe()`
  (defaults to type name), `protected internal abstract Fire()`,
  `protected Repeat(float)` to re-arm from inside Fire (repeating tasks).
- `sealed class ScheduledTasks` (scheduler) — `Schedule(task, delaySeconds)`,
  `Cancel(handle)` / `Cancel(task)`, `Clear()`, `Tick(dt)`, `PendingCount`,
  `DescribeActive()`. Contracts preserved from ScheduledEvents: fire in schedule
  order, deterministic, a task scheduled during Tick never fires that same tick
  (now also true for delay ≤ 0, which the old code let slip through).
- `Simulation`: field/property renamed → `_tasks` / `Sim.Tasks`; tick point unchanged
  (before table-craft tick, phase renamed "scheduled_tasks").
- Logging: `DebugLog` channel `"tasks"` on schedule/fire; dev command `tasks`
  (Game1.Dev.cs) dumps `DescribeActive()`.

## Phase 2 — Anti-patterns doc  [DONE]

`docs/locate-behavior/anti-patterns.md`: new principle — time management never lives as
countdown fields in persistent objects; use `Sim.Tasks` with a declared subclass.
Exception: timers thematic to the object's own lifetime (projectile TTL, buff
RemainingDuration, per-unit bulk arrays) stay in the object. Updated the existing
canonical-resolution section's ScheduledEvents references.

## Phase 3 — Porting wave 1 (sim-clock call sites)  [DONE]

- `CorpsePutDownTask` (Game1.Crafting.cs) — was the lone `Schedule(Action)` caller.
- `ReanimRiseTask` (Game1.Spells.cs) — replaces `_pendingReanimRises` +
  `TickPendingReanimRises` (was ticked from Game1.Animation.cs on WorldDt; same clock,
  so semantics keep). Side fix: the old list was never cleared on map reload —
  sim-owned tasks die with the Simulation instance.
- `ProjectileVolleyTask` (SpellEffectSystem.cs) — replaces `PendingProjectileGroup` +
  `_pendingProjectiles` + `TickPendingProjectiles` + its StartGame clear. Repeating via
  `Repeat(Interval)`. Cursor re-aim now samples `_cursorAimWorld` at fire time (same
  observable result as per-frame re-aim: the aim only mattered when a shot fired).

## Phase 4 — Later porting waves  [TODO]

Sim-clock repeating accumulators ("every N seconds do a scan") → small repeating
subclasses:
- `Game1.Zones.cs` `_zoneSpawnTickTimer`
- `Game/Jobs/WorkerSystem.cs` `_dispatchTimer`
- `Game/HordeSystem.cs` `_aggroScanTimer`
- `Game/Simulation.cs` `_moraleCheckTimer`, `_fatigueRegenTimer`,
  `_harassmentDecayTimer`, `_wolfHuntCmdTimer`
- `Game1.Net.cs` `_netSendTimer`
- `Game/ForagableSystem.cs` `_autoPickupCooldown`, `Game/TriggerSystem.cs` timers

Real-time (raw-dt) side — needs a second `ScheduledTasks` instance owned by Game1,
ticked with `_rawDt` next to the toast-timer block (~Game1.cs:2855), cleared in
StartGame. Candidates: `_hoverVariantLabelTimer`, `_depthFogToastTimer`,
`_gpuWarnToastTimer`, `_devChannelHoldTimer`, skill-learn toasts. Note these are
*queried* countdowns (render reads remaining each frame) — port by holding a task
reference and reading `SecondsLeft`, or leave as-is if that reads worse.

Cleanup opportunity spotted: editor `_statusTimer` copy-pasted across ≥4 editor windows
though `EditorWindow.StatusTimer` exists in the base — consolidate during a later wave.

## NOT to port (timers thematic to the owning object — stay put)

Buff `RemainingDuration`, per-unit bulk arrays in Simulation (DodgeTimer, RoutTimer,
KnockdownCheckTimer, …), PotionSystem per-unit poison/paralysis, projectile lifetimes,
PoisonCloud ticks, env respawn/trap timers, render-side TTLs (GroundFog, ScatterGlow,
weather flashes), spell cooldown dictionaries (queryable state, not fire-once events).
