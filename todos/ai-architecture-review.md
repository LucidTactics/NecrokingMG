# AI System Architecture Review (2026-07-03)

Executive review of the Archetype → Routine → SubRoutine unit-AI system, informed by a
survey of shipped game-AI architectures (HFSM, behavior trees / Halo 2 / Unreal, GOAP /
F.E.A.R., HTN / Killzone, utility AI / The Sims, and RimWorld's job system). Full source
links at the bottom. Recommendations are prioritized; Phase 1 items are the "stop the
locked-behavior bugs" set.

## Verdict

The **shape** of the architecture is right and industry-proven — it is structurally
RimWorld's ThinkTree (archetype) → Job (routine) → Toils (subroutine) system, which ships
the same "engine assigns a job and mostly forgets" contract at colony-sim scale. Handlers
as stateless singletons with per-unit state in `UnitArrays` is the correct
performance-first design. `CombatTransitions` and `SubroutineSteps` show the right
consolidation instincts. Amortized AI with urgent-event bypass is a real version of
RimWorld's two-frequency brain.

The debt is that **liveness is achieved by hand-written discipline instead of structural
mechanisms**. Every locked-behavior bug so far fits one pattern: an exit path that had to
be remembered, wasn't, and nothing else could ever fire. The research consensus (RimWorld,
Halo 2, F.E.A.R., Killzone all converge on this): *liveness never comes from writing
correct transitions — it comes from structural mechanisms that assume transitions will be
wrong* (engine-checked guards, expiry, tripwires, an always-reachable recovery state).
Necroking currently has approximately zero of these mechanisms; `CombatTransitions` is a
manual patch over one instance of the general problem.

## Diagnosis — why behavior locks

1. **Classic FSM exit-edge problem.** Each routine hand-authors its own exits inside its
   update function. A failure mode nobody wrote an edge for (target despawned mid-step,
   path blocked, env object consumed) leaves the unit in the state forever. This is the
   documented reason the industry moved from FSMs to BTs/armored job systems.
2. **No routine lifecycle (enter/exit hooks).** Transitions are raw field assignments, so
   cleanup (`Target`/`EngagedTarget`/`PendingAttack`/`InCombat`, effort, anim) must be
   re-remembered at every transition site. `CombatTransitions`'s own comments document two
   shipped bugs from forgetting exactly this.
3. **External writers bypass the handler entirely.** At least six systems zero or set
   `Routine`/`Subroutine` directly: `PhysicsSystem.cs:109`, `WorkerSystem.cs:220,236`,
   `Game1.Villages.cs:217`, `Simulation.cs:309,1039,4171`, WASD-cancel in
   `Simulation.UpdateAI`, and `Game1.Spells.cs:580` which writes the **raw literal**
   `Routine = 4` that must match `HordeMinionHandler`'s private numbering by convention.
   None run any exit cleanup; each is a bespoke partial reset.
4. **No watchdog, no thrash detection, no exception firewall.** The only timeout in the
   system is `HordeMinionHandler.CommandTimeout` — one hand-rolled case of what should be
   a per-routine engine service. An oscillation (A→B→A every tick) is invisible until a
   player reports "the wolf is vibrating".
5. **Three coexisting AI styles**: archetype handlers; the legacy `AIBehavior` switch
   (player + scenario wolves) with its own stuck-unit hazards (`PendingAttack` /
   `InCombat` movement pins); and post-pass sweep AIs (`BoarForageAI`, `WolfPackHuntAI`,
   `CorpseEatAI`) that override `PreferredVel` after the archetype pass — these exist only
   because `AIContext` can't reach Simulation-level data (necromancer index, env system).
   The priority contract between the three layers is implicit in update order.
6. **Two FSM conventions + state blob.** `Routine`/`Subroutine` vs. side-channel phase
   bytes (`WorkerPhase`, `WolfPhase`, `CorpseInteractPhase`), and ~50 behavior-specific
   fields flat on `Unit` with no grouping and no "what do I reset" story.

## Recommendations

### Phase 1 — armor (small diffs, kills the locked-behavior bug class)

**R1. One transition choke point + lifecycle hooks.** Add to the dispatch layer:
`AIContext.TransitionTo(byte routine, byte subroutine = 0)` which (a) calls the handler's
`OnRoutineExit(ref ctx, oldRoutine)`, (b) sets routine/sub/timer, (c) calls
`OnRoutineEnter(ref ctx, newRoutine)`. Handlers implement exit cleanup **once per
routine** (Engaged.Exit clears PendingAttack/EngagedTarget/etc.). Convert handlers and
`CombatTransitions` to use it. This is the single highest-value change: cleanup can no
longer be skipped by an unusual transition path. (Pattern: HFSM standard practice,
RimWorld toil cleanup.)

**R2. One canonical external-interrupt API.** Replace the six scattered direct writes with
`AIInterrupt.Seize(ref ctx)` / handler method `OnInterrupted(ref ctx)` — the only legal way
for physics, worker-assignment, villages, spells, and WASD-cancel to yank a unit out of
its routine. It runs R1's exit hook, clears the combat/pin fields, and resets to the
handler's idle routine. Also give handlers **intent APIs** for orders: e.g.
`HordeMinionHandler.Command(ref ctx, Vec2 target)` instead of `Game1.Spells.cs` writing
`Routine = 4`. At minimum, make the routine consts public and replace the raw literals
today. (Pattern: RimWorld `TryTakeOrderedJob`; order ≠ execution.)

**R3. Watchdog + thrash tripwire in the dispatcher** (`Simulation.UpdateAI`, outside
handlers, so every archetype gets it for free):
- Per-unit `RoutineAge` (seconds since last routine change, reset by R1's choke point).
  Handlers declare `MaxRoutineDuration(byte routine)` (0 = unlimited, but default e.g. 60s
  for anything non-idle). Expiry → log `[watchdog] unit X stuck in Y/Z for Ns` → force
  reset to idle via R2. (Pattern: RimWorld `expiryInterval`.)
- Thrash counter: routine changes per unit per rolling ~2s window; over threshold (~8) →
  log which routines cycled → force idle **with a short cooldown** before the unit may
  re-enter the same routine. Turns invisible oscillation into a log line. (Pattern:
  RimWorld 10-jobs-per-tick tripwire; the cooldown is the anti-retry-loop half.)

**R4. Typed end reasons.** R1's transition takes an optional `RoutineEndReason`
(Succeeded / TargetInvalid / Timeout / Interrupted / Errored / Thrash). Log it in the
debug channel and surface it in the dev unit dump next to routine/sub names. When a
locked-unit report comes in, the last N transitions with reasons is the whole diagnosis.
(Pattern: RimWorld `JobCondition`.)

**R5. Exception firewall.** try/catch around `handler.Update(ref ctx)` in the dispatcher →
log with archetype/routine/subroutine names → force-reset the unit to idle. One buggy
subroutine should be a log line, not a bricked unit (and not a crashed sim). Cost: one
try/catch per unit-tick, negligible. (Pattern: RimWorld `TryStartErrorRecoverJob`.)

### Phase 2 — consolidation (removes the three-styles debt)

**R6. Engine-checked validity, not per-subroutine vigilance.** Declarative per-routine
requirement flags checked by the dispatcher *before* handler logic runs:
`[Flags] RoutineNeeds { LiveTarget, Horde, WorkerSystem, EnvObject … }` in a small
per-handler table. Any unmet need → end routine with `TargetInvalid` via R1. Flags (not
delegates) keep it zero-alloc on the hot path. This removes the biggest remaining stuck
class: "subroutine forgot to check its target still exists." (Pattern: RimWorld
`FailOn*` / `globalFailConditions`, UE decorator observer-aborts, F.E.A.R. precondition
revalidation.)

**R7. Give `AIContext` the missing handles and fold the sweep AIs.** The sweeps exist
solely because `AIContext` lacks `NecromancerIndex`/Simulation services. Add what's
missing (necro position/index is trivial), then `WolfPackHuntAI` becomes routines on the
wolf's handler, `BoarForageAI`/`CorpseEatAI` become routines on theirs. Until then,
document the layer-priority contract (who may override `PreferredVel` after whom) in one
place. This ends the "two brains fight over one unit" bug source.

**R8. Retire the legacy `AIBehavior` switch.** Player becomes a real archetype dispatch
(the sprint-ramp block moves into `PlayerControlledHandler` or a pre-step); scenario
wolves (`WolfHitAndRun*`) migrate or get quarantined behind scenarios explicitly. The
legacy path's `PendingAttack`/`InCombat` movement pins are a documented stuck-unit hazard
and the code is dead weight for every non-scenario unit.

**R9. One FSM convention.** `WorkerPhase` → `Routine`(=job kind)/`Subroutine`(=phase);
`WolfPhase`/`CorpseInteractPhase` likewise fold into routine/sub of their owners. Then the
watchdog, thrash tripwire, names, and dev dump cover workers too — today the entire worker
FSM is invisible to all Routine-level tooling.

**R10. Group per-behavior `Unit` fields into structs** (`WorkerState`, `BuildState`,
`ChargeState`, …) so "reset worker state" is `u.Worker = default` — makes R1/R2 cleanup
obviously complete instead of a field checklist.

### Phase 3 — only if/when needed

- **Order vs execution split** (RimWorld Job vs JobDriver): a small serializable
  `UnitOrder` (kind + targets) with execution state rebuilt from it. Pays off for
  save/load of mid-routine units and queued orders. R2's intent APIs are the on-ramp.
- **Utility-scored routine selection with hysteresis** (Sims advertisements + Dave Mark's
  ~25% commitment bonus) — when buildings/foragables should *advertise* work to units
  instead of systems enumerating. Fits "map content lives in the map."
- **Skip planners** (GOAP/HTN): your routines are authored step sequences; RimWorld ships
  without a planner. A priority/utility selector over authored routines is the ceiling.

## Note: homogeneous archetypes (user constraint, 2026-07-03)

Unlike RimWorld pawns, all units of an archetype behave identically in the same situation
(no per-unit traits/skills/priorities). This *simplifies* the plan rather than changing it:
- RimWorld's per-pawn variance lives in the selection layer; Phase 1/2 armor is all in the
  execution/liveness layer, which is unaffected by homogeneity.
- R3 durations and R6 validity flags become **static per-archetype tables** (one
  `MaxRoutineDuration[routine]` / `RoutineNeeds[routine]` per handler) — no per-unit data,
  cheaper than RimWorld's per-job stamping.
- Further validates skipping utility scoring/advertisements (Phase 3) and BTs — those earn
  their cost when individuals need different behavior mixes.
- Only wrinkle: identical units decide identically *simultaneously* (all wolves pick the
  same target). Fixes: reservations (first claimant wins) + tie-break by unit index; the
  existing AI amortization already staggers decision ticks for free.
- Debugging benefit: any unit of an archetype reproduces a bug → every stuck report is
  deterministic and `--scenario`-testable.

## What NOT to change

- Don't rewrite to behavior trees. BTs solve transition-explosion by re-arbitrating every
  tick, but you'd pay a re-architecture for a property (structural liveness) you can get
  with R1/R3/R5/R6 inside the current design. RimWorld proves the FSM-shaped job system is
  fine *when armored*.
- Keep stateless handlers + per-unit state, `ref struct` context, amortization, and the
  `SubroutineSteps` step library — all of these match or beat the reference architectures.
- Keep `CombatTransitions` — after R1 it becomes a set of shared exit *policies* invoked
  through the choke point rather than a special case.

## Research sources (digest highlights)

- **RimWorld job system** — the blueprint for Phase 1: engine-checked
  `FailOn*`/`globalFailConditions`, `expiryInterval` watchdog, `JobCondition` typed end
  reasons, error-recover Wait job (bail-to-idle w/ cooldown), 10-jobs-per-tick thrash
  tripwire, 300-tick frozen-toil guard, exception firewall, `suspendable` resume-vs-restart,
  pre-toil reservations. See: roxxploxx "How Pawns Think" wiki; RW-Decompile
  `Verse.AI/JobDriver.cs`, `Verse.AI/Pawn_JobTracker.cs`.
- **Halo 2** (Isla, GDC 2005 "Handling Complexity in the Halo 2 AI") — prioritized-list
  arbitration (behaviors re-win control each evaluation), stimulus "impulses" (events
  raise candidates, never force transitions), per-behavior cooldown memory, validated
  behavior-set presets ("Styles") because ad-hoc configs "leave AI paralyzed".
- **Unreal BTs** — observer-aborts: the running branch cannot outlive its guard condition;
  event-driven guard evaluation instead of per-tick polling.
- **F.E.A.R.** (Orkin, "Three States and a Plan") — entire execution layer is 3 states
  (GoTo/Animate/UseSmartObject); every action's preconditions revalidated during
  execution; plans are disposable. Strongest form of "separate deciding from doing".
- **Killzone 2/3 HTN** — replan at 5 Hz (a plan lives ≤200ms before revalidation) with
  explicit "continue current plan" nodes as first-class hysteresis.
- **The Sims** — objects advertise interactions (content scales without agent changes);
  pick-among-top-N not argmax; player orders and autonomy share one queue with priority.
- **Dave Mark / IAUS** — ~25% commitment bonus to the incumbent behavior as cheap
  anti-dither.

Full link list: Isla GDC05 proceeding (gamedeveloper.com), Orkin GDC06 PDF (gamedevs.org),
Guerrilla Killzone bot pages, GameAIPro chapters 6/9/12/29 (gameaipro.com), RimWorld
RW-Decompile on GitHub, Stanford CS123 HFSM/BT lecture, Curvature utility wiki.
