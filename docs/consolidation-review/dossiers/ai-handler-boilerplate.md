# Dossier: AI archetype handler boilerplate & shared transitions

Concept judged: whether the 13 `IArchetypeHandler` implementations under `Necroking/AI/`
duplicate enough same-intent logic to warrant a base-handler abstraction, and whether the
labeler's specific claims (hand-rolled combat exits vs `CombatTransitions`, duplicated
steering math, duplicated resolve helpers) hold up in the code.

Files read in full: `IArchetypeHandler.cs`, `CombatTransitions.cs`, `CombatUnitHandler.cs`,
`RangedUnitHandler.cs`, `CasterUnitHandler.cs`, `SoloPredatorHandler.cs`, `RatPackHandler.cs`,
`WatchdogHandler.cs`, `WolfPackHandler.cs`, `VillagerHandler.cs`, `SubroutineSteps.cs`,
`BoarForageAI.cs`; targeted reads of `DeerHerdHandler.cs`, `HordeMinionHandler.cs`,
`Simulation.cs:3268-3300`, `docs/locate-behavior/ai.md`.

Headline: the labeler's blanket "all 12 handlers hand-roll near-identical everything" is an
over-match — most handlers are genuinely different state machines — but inside it hides one
real, high-value finding: **four handlers share a byte-for-byte identical "sentry" skeleton
that has already started to diverge (frenzy handling exists in only one copy).**

---

## Finding 1 — Sentry skeleton quadruplicated across Combat/Ranged/Caster/SoloPredator handlers

**Verdict: CONSOLIDATE — severity: high**

Four handlers implement the exact same alert-driven `Idle(0) → Alert(1) → Combat(2) →
Return(3)` state machine; only the *Combat routine internals* (the genuinely handler-specific
part) differ:

| Handler | Archetypes covered | Skeleton lines |
|---|---|---|
| `CombatUnitHandler.cs` | PatrolSoldier, GuardStationary, ArmyUnit | OnSpawn :52, EvaluateRoutine :85, UpdateAlert :211, UpdateReturn :246, names :280 |
| `RangedUnitHandler.cs` | ArcherUnit | OnSpawn :47, EvaluateRoutine :79, UpdateAlert :127, UpdateReturn :245, names :266 |
| `CasterUnitHandler.cs` | CasterUnit | OnSpawn :36, EvaluateRoutine :76, UpdateAlert :142, UpdateReturn :175, names :281 |
| `SoloPredatorHandler.cs` | SoloPredator, AmbushPredator | OnSpawn :56, EvaluateRoutine :86, UpdateAlert :150, UpdateReturn :283, names :315 |

Copy-identical pieces (verified line-by-line):

- **`EvaluateRoutine` ladder** — `Alert ≥ Alert && Routine==Idle → Alert`;
  `Aggressive && Routine ≤ Alert && AlertTarget valid → Combat + Target=AlertTarget`;
  `Combat && !IsTargetAlive → FindClosestEnemy reacquire else Return + AlertState=Unaware`;
  `Unaware && Routine==Alert → Idle`. Caster :76-134 and SoloPredator :86-142 are literally
  the same function plus a self-acquire block; Ranged :79-118 is the same minus self-acquire;
  CombatUnit :85-136 is the same plus frenzy widening.
- **`UpdateAlert`** — one line, `SubroutineSteps.AlertStance(ref ctx)`, in all four.
- **`UpdateReturn`** — clear `EngagedTarget`/`InCombat`, threat-aware Sprint/Walk effort,
  `MoveToward(SpawnPosition)`, transition to Idle at 2f. Ranged :245-264 and Caster :175-192
  are character-identical; SoloPredator :283-300 identical; CombatUnit :246-278 identical
  plus a frenzy bail-out at the top.
- **`OnSpawn`** — identical 4 lines (`SpawnPosition = MyPos; Routine=Idle; Sub=0; Timer=0`).
- **`GetRoutineName`** — identical `IdleRoaming/Alert/Combat/Return` switch in three; CombatUnit
  varies only the idle label.

### Divergence already shipping (why severity is high)

- **Frenzy**: `CombatUnitHandler.EvaluateRoutine` (:111-128) widens the reacquire search and
  refuses to Return while `Frenzied`; `UpdateReturn` (:249-253) bails to Idle when Frenzied.
  **None of Ranged/Caster/SoloPredator handle `Frenzied` at all** — a frenzied archer, priest,
  or dire wolf calmly walks home when its target dies. Classic copy-drift: the feature was
  added to one copy of the shared logic.
- **Reacquire ranges silently differ**: CombatUnit falls back to 12f, Ranged/Caster to 15f,
  SoloPredator uses `AggroRange` (10f). Possibly intended, but nothing marks it as such.
- The codebase already recognizes this failure mode: `CombatTransitions.cs:10-14` documents
  that open-coded exits are "how the 'horde unit stands still while target kites'... bugs
  shipped."

### Proposed canonical design

**Not a base class.** Per CLAUDE.md consolidation design and the existing house pattern
(`CombatTransitions` — static helpers, `return true` = transitioned, caller returns), add a
sibling static helper, e.g. `Necroking/AI/SentryTransitions.cs`:

```csharp
public readonly struct SentryConfig {          // caller owns the data
    public float SelfAcquireRange;             // 0 = disabled (CombatUnit/Ranged); spell.Range (Caster); AggroRange (SoloPredator)
    public float ReacquireRange;               // fallback when DetectionRange==0: 12f / 15f / AggroRange
    public bool  FrenzyAware;                  // CombatUnit true today; flip others deliberately later
}
// The Idle/Alert/Combat/Return ladder. True = transitioned, caller returns.
public static bool EvaluateSentryRoutine(ref AIContext ctx, in SentryConfig cfg);
// Threat-aware return-to-spawn; transitions to routine 0 on arrival.
public static void UpdateReturn(ref AIContext ctx, bool frenzyBails);
// The shared 4-line OnSpawn body.
public static void SpawnAtIdle(ref AIContext ctx);
```

The shared component owns the mechanics (the ladder, the return walk, alert stance); each
handler keeps its `Update` dispatch, its whole Combat routine, its `OnRoutineExit` (these
differ **deliberately** — `RangedUnitHandler.cs:55-64` keeps `PendingAttack` so a queued
arrow still fires, per `docs/locate-behavior/ai.md:174-176` — do NOT unify exit hooks), and
its name switches.

### Migration list

1. `CombatUnitHandler` — EvaluateRoutine, UpdateAlert, UpdateReturn, OnSpawn (`FrenzyAware=true`).
2. `RangedUnitHandler` — same (self-acquire 0).
3. `CasterUnitHandler` — same, keeping its extra self-acquire spell-range probe via `SelfAcquireRange` (must re-fetch spell each tick as today) and its per-frame mana/cooldown tick outside the helper.
4. `SoloPredatorHandler` — same (`SelfAcquireRange=AggroRange`).

Behavior preserved exactly via config defaults; the frenzy question ("should frenzied
archers/predators also refuse to Return?") becomes a one-flag decision instead of three
re-implementations. Test coverage exists: `--scenario` suite (trample_blocker,
trample_necromancer pin these archetypes; HordeTargetTeleportScenario pins CombatTransitions).

**Effort: M** (one new ~120-line file, four mechanical migrations). **Risk: moderate** —
gameplay-visible if a config value is transcribed wrong, but each migrated block is
line-comparable against the original and scenarios exist.

---

## Finding 2 — "Several handlers hand-roll their own combat exits" (CombatTransitions under-adoption)

**Verdict: KEEP_SEPARATE — severity: low**

Only `HordeMinionHandler.cs:318,350` calls `StandardChasingExits`/`StandardEngagedExits`.
The labeler reads this as partial consolidation with stragglers. Verified against the actual
non-users:

- `WolfPackHandler` Fighting (:364-475) has **no Chasing/Returning routines at all** — its
  exits are "alert dropped → time-of-day routine (Sleep vs Roam, with standup sequencing)"
  and "retarget onto whoever hit me" (:98-257). `StandardEngagedExits`' contract (out-of-melee
  → chase routine byte, leash → return routine byte) has nothing to bind to.
- `DeerHerdHandler` FightBack (:702-764) is a Stance/Charge cycle whose only exit is
  "target dead → Calming" (:704-707, and :328-330,:372-374 in evaluation). No chase, no leash.
- `RatPackHandler` Fighting exits to Scurry on `targetGone || calm` (:92-119) with gang-up
  reacquire — again no chase/return split.
- `SoloPredatorHandler`'s dead-target/break-range exits are covered by Finding 1's helper, not
  by CombatTransitions (no leash, subroutine cycle).

This is exactly the structural variance CLAUDE.md says not to abstract: different state
machines with different exit *semantics*, not the same exits re-typed. Forcing them through
`StandardEngagedExits` would grow it a parameter per handler.

One cheap fix worth doing: the doc comment at `CombatTransitions.cs:6-8` claims
"WolfPackHandler's Fighting routine, DeerHerdHandler's FightBack, etc." use these helpers —
they never have. Reword to name `HordeMinionHandler` only, so future consolidators aren't
sent chasing adoption that was correctly never done. (`docs/locate-behavior/ai.md:179` is
accurate and needs no change.)

---

## Finding 3 — Steering/resolve helper duplication (MoveToward vs AIForageMove/AIWolfHuntMove; ResolveTarget vs ResolveAlertTarget)

**Verdict: KEEP_SEPARATE — severity: low (labeler over-match; already consolidated)**

- `Simulation.AIForageMove` (:3273-3278) and `AIWolfHuntMove` (:3284-3289) contain **no seek
  math**: each is 3 lines that build an `AIContext` (via the shared `BuildAIContext`, :3252)
  and call `SubroutineSteps.SetEffort` + `SubroutineSteps.MoveToward` — the single canonical
  seek/pathfind step (`SubroutineSteps.cs:406-431`). Their own doc comments say they exist to
  *reuse* the shared step from the sweep-style AIs (`BoarForageAI`/`WolfPackHuntAI`), which
  run outside the archetype pass and legitimately need a `Simulation` handle
  (`docs/locate-behavior/ai.md:194-197` blesses this as "the sweep-override style").
  They differ only in effort choice; merging them into one `AIMove(i, target, effort, dt)`
  would save ~4 lines and one doc comment — below the consolidation-worthwhile bar.
- `ResolveTarget` vs `ResolveAlertTarget` (`SubroutineSteps.cs:514-525`): both are one-line
  forwarders to the same canonical `UnitUtil.ResolveUnitIndex`, resolving *different fields*
  (`Unit.Target` vs `ctx.AlertTarget`). Nothing to merge.

---

## Finding 4 — OnSpawn / GetRoutineName / GetSubroutineName boilerplate across all 13 handlers

**Verdict: KEEP_SEPARATE — severity: low** (except the four sentry handlers, absorbed by Finding 1)

- `OnSpawn`: the 4-line initializer looks copy-pasted but carries real per-handler variance:
  WolfPack/DeerHerd pick the initial routine by `ctx.IsNight` (WolfPackHandler.cs:64,
  DeerHerdHandler.cs:98); RatPack/WolfPack also seed `MoveTarget`; HordeMinion enrolls in the
  horde (:70-71); Worker sets `WorkerPhase` instead (:38); CorpsePuppet is a no-op (:29);
  PlayerControlled skips `SpawnPosition`. A shared helper would need flags for each — a
  framework, not a utility. The sentry four get `SpawnAtIdle` from Finding 1; leave the rest.
- `GetRoutineName`/`GetSubroutineName`: per-handler switches over that handler's own private
  byte constants, used only for debug/inspection display. Each handler owning its own names
  IS the single source of truth (the constants live in the same file). Drift cost is a wrong
  debug label, not gameplay. Converting to static `string[]` tables would be cosmetic.

---

## Constraint check

No overlap with `Necroking/Net/` (untouched), renderer rules (no draw code involved), or
map-content policy. Finding 1's helper lives in `Necroking/AI/` beside `CombatTransitions.cs`
per `docs/code-map.md` conventions and matches the codebase's own blessed pattern for exactly
this problem.
