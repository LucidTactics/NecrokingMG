# Anti Patterns
*anti patterns to avoid and principles to follow*

*This file holds the **generic** (everywhere) anti patterns — mostly UI/gameplay-structure
ones, since those recur across the whole codebase. **Rendering-specific** anti patterns (zoom
/ camera correctness, premultiplied-alpha color encoding, render targets, shaders, sort keys)
live in their own counterpart: [anti-patterns-rendering.md](anti-patterns-rendering.md) — read
it too before touching any draw-layer code.*

*Egregious anti patterns should typically be refactored whenever found even if not asked to by the user, always, tell the main claude about these when found, and log them in [anti-patterns-list.md](anti-patterns-list.md).*
*Regular anti patterns should be documented in [anti-patterns-list.md](anti-patterns-list.md) whenever found, and if its relevant to the caller claudes request bring these up as potential refactors or fixes as he goes.*
*All anti pattern potential that the caller claude could be thought to do as it tries to solve the problem asked for should be brought up and explain what it should try to do instead in this case.*

## Principle: Single Source of Truth
Every distinct behavior or pattern should have one canonical implementation. Before writing new code, check whether an existing system, utility, or pattern already solves the problem. The goal is fewer pieces of code doing the same function — when a bug is fixed, it's fixed in one place.

### Anti Pattern: Two functions being used for the same thing so needs to be kept in sync or its a bug.
These are fine sometimes when its done close to each other to make an implementation simpler, its never fine when its done in separate files.

### **Egregious Anti Pattern**: Rendering and click handling being done on different implementations
This one is a very egregious and common anti pattern so needs special mention. Never ever tolerate the click handling functions to define their own areas to look for clicks separate from what the draw calls work on.

Example pseudocode:
`def Render(): DrawButton(rect(10, 10, 180, 30))`
`def Update(): if ClickInRect(16, 10, 180, 30): DoSomething()`
This is very bad since it still works, but it feels very bad since parts of the button wont be clickable. These sort of UI issues are so egregiously bad that we should never accept this sort of behaviour.

Always generate a list of positions that then gets reused for both drawing and click handling.

## Principle: Animations should never affect gameplay
The entire game should be playable in theory with no sprites or animations. any behaviour that depends on animation timings etc should be fixed, so the behaviour runs on separate timings outside of animation code.

### **Anti Pattern**: Waiting for animation to be done
This makes us dependent on the animation timings, refactor this to put the timings in the gameplay code instead and adjust animation speed to match the actual gameplay data.

### **Egregious Anti Pattern**: Putting gameplay function calls in animation code
This is especially bad since its very hard to track what the gameplay code actually does when you do this. Since now when you set an animation you not only set the visuals you are also declaring a gameplay function.

### Canonical resolution: the timing-vs-animation abstraction (USE THIS)
There is now one canonical way to sever animation↔gameplay coupling. It has two halves that
compose; reach for them whenever you'd otherwise write `if (ctrl.IsAnimFinished) { …gameplay… }`
or read an animation clock to decide *when* something happens:

1. **Fire the gameplay event later** — `Necroking.Game.ScheduledTasks` (`Necroking/Game/ScheduledTasks.cs`),
   reachable as `_sim.Tasks`. Declare a named `ScheduledTask` subclass and
   `_sim.Tasks.Schedule(new MyTask {…}, delaySeconds)` — it fires on the **sim clock** (ticked in
   `Simulation.Tick`, before the table-craft tick — deterministic, runs headless). Returns a handle
   for `Cancel`. `Fire()` should re-validate its target (ids can go stale) and, per "direct over
   inject", carries ids/indices as fields and calls `Game1.Instance`/sim methods directly rather
   than injected delegates. See the "uniform delayed execution" principle below for the full rules.
2. **Make the animation reflect that clock** — `Necroking.Render.AnimTiming`
   (`Necroking/Render/AnimTiming.cs`). `FitOneShot(ctrl, state, targetSeconds)` returns the
   `PlaybackSpeed` that makes one play of a clip last exactly `targetSeconds`; `FitChannel(...)` fits a
   Start→Loop→Finish triple into a target and hands back the Loop-phase `loopBudget`. `NaturalSeconds`
   reads a clip's own length when *that* is the duration you want. Apply the returned speed right
   before the per-frame `ctrl.Update`.

**The rule:** a gameplay system owns the duration (a designer value like a table's `ProcessTime`, or a
clip's natural length picked once); the animation is *fitted* to it and never advances it. Gameplay
never waits on `IsAnimFinished`.

**Worked example — necro-bench corpse drop (the fix that retired the egregious entry below):**
`Game1.BeginCorpsePutDown` sets the visual PutDown phase and schedules a `CorpsePutDownTask` at the clip's
`NaturalSeconds`; `Game1.CompleteCorpsePutDown` (fired from the sim clock) does the corpse transfer,
`StartTableCraft`, and clears the phase — the animation returns to Idle on its own. The craft *loop* itself
is fitted to `ProcessTime` via `AnimTiming.FitChannel` in `Game1.Animation.cs` (imbue-table block), with
`TableCraftingSystem.Tick` completing on the same `LoopBudget`. `ChannelPlaybackSpeed` (reanimation casts)
also delegates to `FitChannel` — one source of truth for the fit math.

## Principle: Delayed execution goes through the ScheduledTask framework, not timer fields on persistent objects

All "do X after N seconds" / "do X every N seconds" behavior has ONE canonical home:
`Necroking.Game.ScheduledTasks` (`Necroking/Game/ScheduledTasks.cs`), reachable as `_sim.Tasks`
for sim-clock work. You declare a **named `ScheduledTask` subclass** (never an anonymous
lambda/Action) next to the domain code it serves, carrying the ids it needs as fields; its
`Fire()` re-validates targets and calls `Game1.Instance`/sim directly. Repeating work re-arms
itself with `Repeat(seconds)` from inside `Fire()`. Because every task is a named class, the
active queue is inspectable — the `tasks` dev command lists them, and the `"tasks"` DebugLog
channel traces schedule/fire/cancel. Sim tasks die with the Simulation on map reload, so no
per-system Clear-on-restart bookkeeping.

Worked examples to copy: `CorpsePutDownTask` (Game1.Crafting.cs, one-shot),
`ReanimRiseTask` (Game1.Spells.cs, one-shot with spawn payload),
`ProjectileVolleyTask` (Game/SpellEffectSystem.cs, repeating via `Repeat`).

### **Anti Pattern**: Hand-ticked countdown fields on normal persistent objects
A long-lived system/Game1 holding `_someTimer -= dt; if (_someTimer <= 0f) DoThing();` (or the
accumulate-to-interval variant) and a hand-written Tick call threaded through the frame. Each one
is an invisible mini-scheduler: unlistable, unloggable, needs its own clear-on-restart, and
usually copy-pastes the same decrement/compare boilerplate. When you find one — or are about to
write one — use `_sim.Tasks` with a declared task subclass instead (a repeating task via
`Repeat` for the every-N-seconds scans). Known remaining instances are censused in
[anti-patterns-list.md](anti-patterns-list.md).

### Exception: timers that ARE the object's lifetime stay in the object
A duration that is **thematically the object's own state** should live in the object, not the
scheduler: a projectile's flight time, a buff's `RemainingDuration`, a poison cloud's TTL, a
spell's channel duration, per-unit bulk timer arrays in `Simulation` (DodgeTimer, RoutTimer, …)
ticked in tight loops. These are queryable state ("how much is left?") owned by an entity whose
death must take the timer with it — porting them would split state from its owner (and per-task
heap objects in hot per-unit loops would regress perf). The test: *if the object died right now,
would the timer be meaningless?* Yes → keep it in the object. If instead the timer is "the system
does something later/periodically", it belongs in `ScheduledTasks`.

## Principle: No dependence injection class patterns
Call functions directly instead. Passing functions into objects should only be done when we add
local information to the function, such as `void ValidateDef(UnitDef def, Action<string> report)`.

### **Anti Pattern**: Passing lots of functions in constructor or initializer field
Look for functions being stored in big long lived classes, and those functions refers to big global classes we
should just call directly instead of passing them as functions.
This just confuses us when we try to look for what calls what, or what code this class is calling.

Example field thats set on game start, note this one even has tooltip saying this is superflous,
as we are binding it to a known function.
```cs
    /// <summary>Game1 wires this to its SpawnUnit so the brain can create units
    /// without referencing Game1.</summary>
    public Action<string, Vec2>? SpawnWorkerUnit;
```
Its only set here, which is very dangerous since if this part for some reason get missed during initialization
we will not call this function since its called using `?.`, so it will fail silently.
```cs
_workerSystem.SpawnWorkerUnit = (defId, pos) =>
    QueueReanimRise(defId, -1, "", posOverride: pos);  // "" → the unit's own effect (else reanim_smoke)
```
