# Dossier: Steering / locomotion micro-duplication

Final-judge verification of the labeling-pass evidence for the "steer-move|unit" cluster.
Repo: `c:/Nightfall/NecrokingMG`. Canonical-pattern source: `docs/locate-behavior/ai.md`,
which explicitly names `SubroutineSteps.MoveToward` as "**the MoveTo primitive for a custom
handler**" (ai.md §`SubroutineSteps.cs`).

## Headline

The labeler's central claim (BoarForageAI/WolfPackHuntAI re-implement seek+arrive) is
**false** — those are 3-line wrappers over `SubroutineSteps`. The *real* duplicate is
inside `Simulation.cs` itself: the legacy `AIBehavior` path carries two private copies of
the exact `MoveToward` seek logic. Plus one small verbatim formula duplication in
`StrideCalibration` and one partial re-implementation in `SoloPredatorHandler`.

---

## Finding 1 — Legacy Simulation seek helpers duplicate `SubroutineSteps.MoveToward` — CONSOLIDATE (medium)

**Claim**: 3 implementations of the "pathfind if far, straight-line if near" seek primitive.

**Evidence**:

1. **Canonical**: `Necroking/AI/SubroutineSteps.cs:406-431` `MoveToward(ref ctx, target, speed)` —
   `dist > 3f && Pathfinder.Grid != null` → `TerrainCosts.SizeToTier(Size)` →
   `Pathfinder.GetDirection(myPos, target, frame, sizeTier, id)` → `PreferredVel = dir*speed`;
   else direct-normalize; else zero. Also calls `SetLocomotionAnim`.
2. **Duplicate A**: `Necroking/Game/Simulation.cs:3374-3390` private `MoveTowardPosition(i, targetPos, speed)` —
   byte-for-byte the same seek math (same `3f` threshold, same `SizeToTier`, same
   `GetDirection` signature, same direct-normalize fallback), **minus** the locomotion-anim
   reset. `Simulation.cs:3368-3371` `MoveTowardUnit` forwards to it.
   Call sites (all in the residual legacy `AIBehavior` switch in `UpdateAI`): 1131
   (IdleAtPoint fight), 1138 (IdleAtPoint return), 1171 (horde Returning), 1207 (default
   AttackClosest chase), 1226 (horde slot-follow).
3. **Duplicate B (inline)**: `Simulation.cs:1107-1119` `case AIBehavior.MoveToPoint` inlines
   a third copy (`GetDirection` + `PreferredVel = dir*MaxSpeed`), with its own arrive
   threshold (`LengthSq() > 1f`) and **no** near-distance direct-normalize branch.

**Why it matters**: this is live gameplay steering (net ghosts via `IdleAtPoint` in
`Game1.Net.cs`, archetype-less horde units, scenario spawns — ai.md documents the legacy
switch as intentionally alive). The copies have *already* diverged (anim handling; MoveToPoint's
missing near-branch means a unit 1–3u from its point pathfinds instead of walking straight).
A future change to the pathfinder call contract (e.g. the per-unit direction-cache fix noted
in `RatPackHandler.cs:194-197`) must be applied in three places.

**Canonical home**: `SubroutineSteps.MoveToward`. `Simulation` already owns
`BuildAIContext(i, dt, dayFraction, isNight)` (`Simulation.cs:3252`) and already uses exactly
this bridge pattern for `AIForageMove`/`AIWolfHuntMove` (`Simulation.cs:3273-3289`).

**Merged API sketch** (no new API needed):
```csharp
private void MoveTowardPosition(int i, Vec2 targetPos, float speed, float dt)
{
    var ctx = BuildAIContext(i, dt, 0f, false);
    AI.SubroutineSteps.MoveToward(ref ctx, targetPos, speed);
}
```
and replace the `MoveToPoint` inline block with the same call (keep its own arrive check).

**Call sites to migrate**: the 5 `MoveTowardUnit`/`MoveTowardPosition` sites + the
`MoveToPoint` inline block — all inside `Simulation.UpdateAI`. Nothing outside Simulation.cs.

**Effort**: S (one file). **Risk**: low-medium — behavioral deltas to sign off:
(a) legacy units gain the `SetLocomotionAnim` reset (almost certainly desirable — it's the
bug class "walk anim stuck" the central gait pass expects); (b) `MoveToPoint` gains the
near-distance direct-normalize branch (removes 1–3u pathfind churn). Verify with the
scenarios that drive legacy AI (`HordeChaseLeash`, net-ghost path) — `dotnet build` + existing
scenario suite covers it.

**Severity**: medium — gameplay steering logic, three copies, divergence already observed.

---

## Finding 2 — `StrideCalibration.ResolveSuggestedCombatSpeed` vs `ResolveAnimVel` — CONSOLIDATE (low)

**Evidence**: `Necroking/Render/StrideCalibration.cs:433-449` vs `451-473`. The 4-line core
is verbatim identical:
```csharp
float effectiveStridePx   = MathF.Max(g.StridePx - bodySubtractionPx, 1f);
float effectiveWorldHeight = spriteWorldHeight * spriteScale;
float pixelsPerWorldUnit  = g.AvgPixelHeight / effectiveWorldHeight;
float cycleDistanceWorld  = (effectiveStridePx / dutyCycle) / pixelsPerWorldUnit;
```
Only the divisor differs: `targetCycleSeconds` (falling back to `g.CycleSeconds`) vs
`g.CycleSeconds`. With `targetCycleSeconds <= 0` the two functions are *the same function*.
Guard sets differ slightly (`ResolveAnimVel` additionally requires `CycleSeconds > 0`, which
`ResolveSuggestedCombatSpeed` only needs on the fallback path — the refactor must preserve that).

**Callers**: `ResolveAnimVel` — `Movement/Locomotion.cs:768-770` (runtime feet-lock velocity)
and `Scenario/Scenarios/StrideDebugScenario.cs:340`. `ResolveSuggestedCombatSpeed` —
`Editor/UnitEditorWindow.cs:2067` (editor suggestion). The whole point of the editor
suggestion is that it *matches* the runtime formula — silent divergence here means the
editor recommends a CombatSpeed that skates.

**Canonical design**: extract a private helper in the same file:
```csharp
private static float CycleDistanceWorld(GaitCalibration g, float spriteWorldHeight,
    float spriteScale, float bodySubtractionPx, float dutyCycle) // 0f on invalid inputs
```
`ResolveAnimVel` = `CycleDistanceWorld(...) / g.CycleSeconds` (0-guard CycleSeconds);
`ResolveSuggestedCombatSpeed` = `CycleDistanceWorld(...) / t` with the existing `t` fallback.

**Effort**: S (one file, two functions, no caller changes). **Risk**: very low.
**Severity**: low — same file, adjacent functions; but the editor-vs-runtime coupling makes
divergence a subtle, user-visible skating bug rather than a crash.

---

## Finding 3 — `SoloPredatorHandler` re-implements `SubroutineSteps.Disengage`/`WaitForCooldown` back-off — CONSOLIDATE (low)

**Evidence**: `Necroking/AI/SoloPredatorHandler.cs`:
- `SubDisengage` (lines 232-252): clears `EngagedTarget`/`PendingAttack`/`PostAttackTimer`,
  then `awayDir = (myPos-targetPos)/dist` fallback `(1,0)`, `PreferredVel = awayDir * MaxSpeed`
  when `dist < disengageDist`, else zero + transition. This is **functionally identical** to
  `SubroutineSteps.Disengage(ref ctx, backoffDist)` (`SubroutineSteps.cs:262-286` — same three
  field clears, same awayDir formula, same `(1,0)` fallback; `ctx.MyMaxSpeed ≡ Units[i].MaxSpeed`
  per `AIContext.cs:76`) + `Disengage_Complete` for the transition test. `RatPackHandler.cs:226-233`
  already composes its skitter exactly this way — proof the primitive fits.
- `SubWaitCooldown` (lines 254-279): the back-off half (`awayDir * MaxSpeed * 0.5f` when too
  close, lines 260-264) duplicates `SubroutineSteps.WaitForCooldown`'s core
  (`SubroutineSteps.cs:309-330`, same `*0.5f`); the perpendicular *circling* branch
  (lines 270-277) is genuinely new behavior with no shared-step equivalent.
- The engage-range strafe (lines 186-190, opportunist circling) — also handler-specific.

**Proposal**: replace `SubDisengage`'s body with `SubroutineSteps.Disengage` +
`Disengage_Complete` (note: the handler's per-tick force-clear comment at 234-235 is exactly
what `Disengage` does every call, so semantics are preserved). Leave `SubWaitCooldown` as-is
**or** extract only if a second circler appears — the circle+keep-target combination is
structural variance (per CLAUDE.md, don't abstract structural variance); the back-off
sub-branch alone is too small to force through a step whose target-resolution and
field-clearing side effects differ (`WaitForCooldown` doesn't clear `PendingAttack`;
`SubWaitCooldown` must).

**Call sites**: one (SoloPredatorHandler serves both SoloPredator id 15 and AmbushPredator
id 16 via ctor flag). **Effort**: S. **Risk**: low — verify with the `wolf_hit_and_run`
scenario (ai.md says it tests exactly this subroutine machine).
**Severity**: low — one handler, ~20 lines, but it's the "predator forever planted by stale
PendingAttack" bug class the shared steps were built to fix once.

---

## Finding 4 — BoarForageAI / WolfPackHuntAI move helpers — KEEP_SEPARATE (evidence debunked)

**Labeler claim**: "`AIForageMove`/`AIWolfHuntMove` … same seek+arrive math" as SubroutineSteps.

**Reality**: `Simulation.cs:3273-3289` — both are 3-line bridges that build an `AIContext`
and call `SubroutineSteps.SetEffort` + `SubroutineSteps.MoveToward`. Their doc comments say
so explicitly ("reusing the shared movement step so pathfinding, effort, and locomotion anim
all match normal AI"). This is the **already-consolidated** state, and the bridge exists for
a documented structural reason: sweep-override AIs need a `Simulation` handle
(`NecromancerIndex`, `EnvironmentSystem`) that `AIContext` deliberately doesn't expose
(ai.md "Second AI style"). No action.

---

## Finding 5 — `Simulation.SteerRout` direct (non-pathfound) flee steering — KEEP_SEPARATE

**Evidence**: `Simulation.cs:3125-3162`. Routing units steer `away * MaxSpeed` directly,
no pathfinder. The in-code comment states the variance is deliberate: "fleeing doesn't need
a pathfind (and pathfinding every frame for a whole routed army is a perf trap); ORCA/movement
handles obstacle avoidance." It also uses `Locomotion.SetEffort` (the canonical effort home)
rather than raw writes. Similar direct awayDir/perp writes elsewhere
(`RatPackHandler.ApplyErraticJitter` post-processing at 246-253, its stale-direction safety
snap at 198-200) are intentional post-processing on top of the shared primitives, not
parallel implementations. No action.

---

## Verdict on the routing question ("should all AI movement go through SubroutineSteps?")

Yes for *seek/pathfind* movement — and the codebase already almost complies. The
architecture doc names `MoveToward` the primitive; every archetype handler and both sweep
AIs use it. The bypasses that remain are (a) the legacy `AIBehavior` switch's private copies
(Finding 1 — the one real consolidation win), and (b) deliberate direct steering for
flee/strafe/jitter where pathfinding is wrong by design (Findings 3 partial, 5). Do NOT
route short-range back-off/circling through the pathfinder — the existing shared steps for
those (`Disengage`, `MoveAwayFrom`, `WaitForCooldown`) are themselves direct-steer, which is
the correct split: shared step owns the mechanics, handler owns the state machine.

## Constraint check

No findings touch `Necroking/Net/` (the `Game1.Net.cs:100` PreferredVel write is glue code,
untouched by these proposals), the renderer submit path, or map JSON.
