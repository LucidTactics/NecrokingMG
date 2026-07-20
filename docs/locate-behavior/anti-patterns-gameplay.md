# Gameplay-Simulation Anti-Patterns
*Simulation-behavior anti patterns to avoid and principles to follow — movement, combat &
damage resolution, stats & buffs, spawning/despawning, unit grouping, orders & AI behavior.
The generic (everywhere) anti patterns live in [anti-patterns.md](anti-patterns.md); the
draw-layer and UI counterparts are [anti-patterns-rendering.md](anti-patterns-rendering.md)
and [anti-patterns-ui.md](anti-patterns-ui.md). Same discipline: egregious ones get refactored
on sight and told to the main claude; regular ones get logged in
[anti-patterns-list.md](anti-patterns-list.md) and raised as fix candidates when relevant.*

Each entry was paid for by a real bug (commit hashes cited — grep them for the full story).
Deep references: [combat.md](combat.md), [movement.md](movement.md), [ai.md](ai.md),
[../standard_patterns.md](../standard_patterns.md) (the canonical implementations these point
back to). This file is the "what not to do" index.

> **A recurring meta-shape:** most of these are the *simulation* face of two ideas — "one
> canonical implementation" and "state must run on the right clock and always get cleaned up."
> A new source of damage / a new spawn path / a new motion system / a new timed effect is where
> they bite: it silently skips a rule the shared path enforces, or leaves state that never
> clears.

---

## Clock & timing — advance gameplay on the sim clock, nowhere else

### **Egregious Anti Pattern**: consuming or advancing sim-owned state from a render / animation / paused frame
Sim state (event queues, diffusion fields, timers) must only be read-to-consume and advanced
on frames where the sim actually ticked.
- `787768e`: `Projectiles.Impacts` is a sim queue cleared at the next sim step, but
  `SpawnImpactEffects` runs in the **render** pipeline — so while paused, a stale impact caught
  in the freeze-frame re-spawned a duplicate explosion **every rendered frame**, piling up
  additively (HDR + scatter) to the 512-halo cap.
- `2403167`: `DeathFogSystem.Update` (diffusion + ground-corruption spread) was ticked from
  `UpdateAnimations`, which runs **outside** the `!_paused && !editorActive` sim gate — so
  corruption kept spreading over the map **while it was being edited** (the editor leaves
  `_paused=false`).
**Instead:** gate every world-advancing consumer on `GameClock.WorldRunning` (and add the
`EditorActive` check for editor freeze). Tick sim state with `GameClock.WorldDt`, not a raw
frame dt. Distinguish "advance the world" (gated) from "render what the world currently is"
(ungated). Clock choice = [game1-partials.md](game1-partials.md) "GameClock".

### **Anti Pattern**: timing a gameplay decision off an animation clock / the wrong event
A combat transition timed off when a swing was *queued* (or a fixed offset) instead of the
swing's actual **impact frame** silently cancels swings.
- `8b7043a`: `SoloPredatorHandler` exited `SubAttacking` 0.2s after the cooldown *started* —
  but the cooldown is stamped when the swing is queued and damage only rolls at the anim effect
  frame, so the per-tick `PendingAttack` clear cancelled most swings vs fleeing targets
  (animation played, dice never rolled). The same phantom-no-damage bug had already been fixed
  in WolfPack and RatPack handlers. `06a6ee8` removed speculative pre-rolls and added the
  `SwingJanitor` + impact-integrity check; `2325587`/`3766a9d` are the ranged twin.
**Instead:** single-source "swing resolved" on the impact frame
(`SubroutineSteps.AttackTarget_SwingFinished`); let a committed swing ride to its effect frame
(`CombatTransitions` keeps `PendingAttack`; the `SwingJanitor` reaps only truly-expired ones).
The general rule "animations must never drive gameplay" lives in [anti-patterns.md](anti-patterns.md).

---

## Movement & unit-state ownership

### **Egregious Anti Pattern**: two systems writing the same unit field each tick with no owner
`c47cf19`: the dodge lerp and jump `ApplyArc` both overwrote `Position`/`Z` every tick, so a
knockback landing during a hop/pounce was **visually ignored, then ended in a bogus knockdown
at the scripted destination** — the scripted motion raced the physics launch. Fix: `ApplyImpulse`
now cancels competing scripted motion (`DodgeTimer=0`, `JumpSystem.CancelJump`). Related:
`75142a4` made body facing and movement animation derive from one `Unit.LocoVector` so they
can't disagree.
**Instead:** one owner per unit field (`Position`/`Z`/`Velocity`/facing) per tick. When a new
motion source takes over, it must **cancel** the competing scripted motion, not overwrite it
each frame. Derive facing from the single loco vector. (Watch the same-frame-stale trap that
same fix caught: a value cached at frame/loop start — flyer speed — read after a same-frame
mutation; recompute after the mutation.)

### **Anti Pattern**: re-aiming a committed action so it *extends* its envelope
`a236f32`: pounce liftoff re-aim capped endpoint displacement but not leap **length**, so a
max-lead pounce + a full correction produced a ~13u mega-leap — and since `JumpDuration` is
fixed, the extra length inflated air speed too. `54a3b51` re-predicts at liftoff with a 3u cap.
**Instead:** a retarget/re-aim on an in-flight committed motion (pounce, leap, volley) may
**redirect or shorten, never extend** — an extending correction means the target outran it (a
miss either way). Clamp new length to committed length from the current position.

---

## Combat resolution

### **Egregious Anti Pattern**: resolving damage, death, or a strike outside the one pipeline
A new damage/death/strike source that hand-rolls its own resolution silently drops the shared
rules the pipeline enforces (dice tiers, magic resistance, Death anim, prone-snap, attribution,
damage numbers).
- `1b5e265`: one `ApplySpellDamage` for all spell hits; `c8d9f59`: one shared
  `SpellPenetration.CasterRollTier`.
- `0968f93`: `DamageSystem.Kill` unified death finalization across 4–5 sites — potion-tick and
  trigger kills previously skipped the Death anim + prone-snap.
- `32a5675`: trap strikes route through the shared `SpellEffectSystem.ExecuteStrikeFrom` —
  traps previously skipped magic resistance, the standard damage number, and the def's god-ray
  params; `3c65452`: beam/drain channels deal damage through the standard pipeline.
**Instead:** apply damage via `DamageSystem.Apply`/`ApplyDirect`, kill via `DamageSystem.Kill`
(the only sanctioned way to flip `Alive=false` — see [../standard_patterns.md](../standard_patterns.md)
"Killing a unit"), execute strikes via `SpellEffectSystem.ExecuteStrikeFrom`. **Casterless
sources (traps, environment) pass `casterIdx = -1`**, not a fake caster. Attribution/auto-engage
goes through the shared `StampAttacker`.

---

## Stats & buffs

### **Anti Pattern**: reading a buffable stat raw on some paths, or storing effects as ad-hoc fields
A stat read that bypasses the modified-stat accessor means the buff silently doesn't apply on
that path.
- `c916a31`: `MaxAcceleration` buffs only affected *some* acceleration paths ("make them affect
  ALL forms of acceleration").
- `7c2ba1a`: the `(base+Add·stacks)×Multiply^stacks / last-Set-wins` math was duplicated in
  `GetModifiedStat` (enum stats) and `GetModifiedExtra` (necro resources) → one `ModifyCore`.
- `3b92c5c`: potion weapon coats were 3 bespoke `Unit` fields + an inline `Simulation` block
  instead of the `WeaponBonusEffect` system.
**Instead:** every consumer of a buffable stat reads it through `GetModifiedStat`/`GetModifiedExtra`
(one `ModifyCore`); a timed effect lives in the buff / `WeaponBonusEffect` system, not as
bespoke unit fields. When you add a buff to a stat, audit **all** readers of that stat.

### **Egregious Anti Pattern**: a timed effect with no guaranteed expiry / an un-released override
A status with a duration that isn't guaranteed to clear sticks forever.
- `3b92c5c`: `WeaponBonusEffectSystem.Tick` was **never implemented** — non-permanent coats
  never expired, and re-drinking didn't refresh the timer (merge-aware `Add` fixed it).
- `c433fb7`: the old paralysis path never released its priority-3 `Hold(Stunned)` override, so
  archetype units could **stick in Stunned forever**; `BuffSystem` is now the **sole writer** of
  `Unit.Incap`, and buff expiry `ClearOverride`s everything it set.
**Instead:** one system owns a timed effect and is the **sole writer** of the state it touches;
it has a real expiry tick, a merge/refresh path for re-application, and its expiry releases every
override/lock it set. Prefer `ApplyBuffWithDuration` over a manual permanent-flag loop. (This is
the buff twin of the AI "pin/gate flag set without a guaranteed clear" below.)

### **Anti Pattern**: a movement/AI gate flag set without a guaranteed clear (stuck units)
ai.md's classic stuck-unit causes: a `PendingAttack` that never resolves pins the unit
(`PreferredVel` is zeroed while it's set); `InCombat==true` zeroes velocity (only
Fleeing/Routing exempt — `432df93` added the fleeing exemption); a stale
`Routing`/`Incap`/`Jumping`/`InPhysics` short-circuits AI dispatch entirely.
**Instead:** every pin/gate flag (`PendingAttack`, `InCombat`, `Routing`) needs a guaranteed
release — a transition exit hook (`CombatTransitions` clears `PendingAttack` on exit), a bounded
timer, or a janitor. Setting a lock without owning its release is the bug.

---

## Spawning, despawning, grouping & session lifecycle

### **Egregious Anti Pattern**: spawn / despawn / unit-build / enroll duplicated per path (asymmetry)
When the spawn core is re-implemented per call site, a new path silently drops a field, a buff,
or an enrollment.
- `1f4145d`: `Game1.SpawnUnit` re-implemented the sim spawn core, so summons/map/dev spawns
  **missed skill-tree intrinsic buffs**, and the build-anim block existed 3× (rebuild/lazy paths
  dropped `AnimTimings`/`EffectTimeMs`).
- `d08b584`: `SpawnUnitByID` (triggers, potions) and `TransformUnit` never wired the def's
  **archetype or awareness**, so those units ran legacy AI with **zero detection range** — "the
  same bug class that broke NPC casters." `4e51485` censused it.
- `4b33de7`: village vs zone group spawners were mirror loops → one `SpawnGroupCore`.
**Instead:** one `Simulation.SpawnUnitByID` → `ApplyDefRuntimeFields` (the single def→unit
runtime copy: archetype, awareness, faction, stats, caster resources) + one `BuildUnitAnimData`;
`Game1.SpawnUnit` delegates. Despawn via the one `Simulation.RemoveUnitTracked` (swap-pop +
necromancer-index repair + `HordeSystem.RemoveUnit`); **never `RemoveAt` a unit mid-loop** —
defer removal to a post-loop sweep. Horde enrollment has exactly three spawn-time entry points
(see [ai.md](ai.md) "Horde membership") — there is no per-frame enrollment; mirror
`SpawnZombieMinion` for any runtime conversion.

### **Anti Pattern**: per-session state / back-references re-initialized in only one world-entry path
`StartGame`/`StartScenario` recreate the `GameSession` (and its `Simulation`), so anything wired
once at startup is lost on the next world entry.
- The `SetAnimMeta` bug (logged in [anti-patterns-list.md](anti-patterns-list.md), since
  FIXED): `_sim.SetAnimMeta` was called once at startup; `WireSimCallbacks` re-wired
  ReanimHandler/Workers after a session recreate but **not** AnimMeta — so every fresh
  `Simulation` ran with null AnimMeta and archer shot-windows silently fell back and
  expired. `WireSimCallbacks` now re-installs it; the lesson stands.
- `9a07708`: `StartGame` vs `StartScenario` reset **asymmetry** — scenario reuses the session
  without recreating Road, StartGame-only resets diverge.
**Instead:** `WireSimCallbacks` (the designated re-wire hook) must re-install **everything** a
fresh `Simulation` needs; a shared reset core should own per-game cleanup so the two entry paths
can't diverge. This is the sim-side twin of the UI stale-session-ref anti-pattern
([anti-patterns-ui.md](anti-patterns-ui.md)) — a `?`-nullable set-once field lost on recreate
fails silently.

### **Anti Pattern**: a distance / threshold measured from inconsistent reference points across systems
`811f285`: the Engaged **leash measured from the unit's slot** while the F7 debug ring, the
Chasing check, and the AI-side `Standard*Exits` measured **from the circle center** — so
far-side units fought "leash + effRadius" out of position; and Returning→Following arrival tested
a different point than the actual movement target (units idled in Returning until the offset
re-rolled).
**Instead:** a leash / engage / arrival threshold that several systems must agree on is computed
from **one** canonical reference point, and arrival is tested against the **actual** movement
target. Grouping/formation logic (`HordeSystem`, `SquadSystem`) is especially prone to this —
the debug overlay, the AI exit, and the sim check must all measure the same way.

---

## Orders, priority & behavior state machine

### **Anti Pattern**: order / target / facing priority resolved by scattered per-site logic
When "which order wins / which facing wins / which target" is decided by ad-hoc checks spread
across call sites, they drift.
- `75142a4`: the facing priority ladder (cast-aim > FaceVelocity hysteresis > cursor > engaged
  target > loco vector > stationary target) centralized into `Locomotion.UpdateFacing`.
- `0b435eb`: every routine change goes through `AIContext.TransitionTo` / `AIControl.Interrupt` /
  `AIControl.StartRoutine`; **writing `Unit.Routine` directly skips `OnRoutineExit` cleanup** and
  leaves stale pin fields (and routine byte indices are **per-handler, not global** — a raw
  literal means different things to different handlers).
- `a298c3b`: one `TryOrderMeleeAtCursor` for click-melee and melee-gather.
**Instead:** priority/arbitration lives in one ladder/choke point — the facing ladder, the
`AIControl` transition APIs (use a handler's public routine consts, never raw literals), the
shared order helper.

### **Anti Pattern**: player-only globals a second actor can't reuse (behavior re-implemented + drifts)
`6ff70a1`: casting state was globalized player-only, so AI caster units had a hand-rolled
strike-only path and couldn't use projectiles/clouds/buffs/beams. De-globalized behind
`ICasterResources` (`NecromancerState` + `UnitCasterResources`) so player and AI run the **same**
`SpellCaster` + `SpellEffectSystem`. Same shape as the legacy `AIBehavior` switch running in
parallel with archetype handlers (the `d08b584…d21ad4a` migration).
**Instead:** encode a behavior against an **actor-agnostic interface**, not player-only globals,
so a second actor (AI, a trap, a scripted unit) reuses one pipeline instead of a parallel copy
that drifts. Targeting keys off the caster's faction, not a hardcoded one.

---

## Performance

### **Anti Pattern**: hand-rolled per-frame per-unit scans instead of the shared query + scan tick
`811f285` throttled an "any enemy nearby" quadtree query that ran **every frame per engaged
unit** to the shared 0.5s scan tick; the consolidation pass (`95b126d`, `2e3b5e5`, `3436aea`)
routed AI / `AwarenessSystem` / `WorkerSystem` scans through `WorldQuery`.
**Instead:** use `AIContext.Query` (`_sim.Query` / `WorldQuery`) for nearest-enemy/-object/-corpse
scans — never a fresh `for (units) { bestSq }` loop — and amortize repeated scans via the shared
scan tick (`AwarenessSystem` / `_aiUpdateInterval`). Gotcha: the **quadtree is stale outside
`Simulation.Tick`** (UI/paused code uses the linear query methods). See
[../standard_patterns.md](../standard_patterns.md) "World queries".

---

## Derived values

### **Anti Pattern**: a hardcoded gameplay constant that should be derived from a stat
`f156964`: the sprint ramp duration was a hardcoded 3s → derived as
`CombatSpeed*(mult-1)/MaxAcceleration`, so `MaxSpeed` rises exactly as fast as the Newtonian
accel cap can follow (and stays correct when the stat is buffed).
**Instead:** a duration/threshold that depends on a unit stat is **computed from that stat**, not
a magic number that silently desyncs when the stat changes or is buffed. Guard against missing
defs / zero denominators.

---

## Related
- [anti-patterns.md](anti-patterns.md) — generic (everywhere) anti patterns: the draw-vs-hit-test
  skew, animation↔gameplay coupling, the ScheduledTasks delayed-execution framework, dependency
  injection.
- [anti-patterns-rendering.md](anti-patterns-rendering.md) / [anti-patterns-ui.md](anti-patterns-ui.md)
  — the draw-layer and UI/input counterparts.
- [anti-patterns-list.md](anti-patterns-list.md) — known live instances (AnimMeta-on-recreate,
  ranged swing-window mismatch, hand-ticked timers, …).
- [combat.md](combat.md) — damage/strike/projectile resolution, the SwingJanitor, ranged shots.
- [movement.md](movement.md) — locomotion, facing, the speed pipeline, physics/pathfinding.
- [ai.md](ai.md) — archetypes, routines, transition choke point, spawn/enroll paths, WorldQuery.
- [../standard_patterns.md](../standard_patterns.md) — the canonical homes (Kill, spawn, queries,
  target-leading, buff math) these anti patterns point back to.
