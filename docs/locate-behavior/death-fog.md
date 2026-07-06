# Death fog / corruption / blight spread

The "death fog" scalar field and everything it corrupts (ground vertices, grass tufts,
trees). This is a **simulation** system whose per-frame tick is driven from the
`UpdateAnimations` path — which now runs on **`GameClock.WorldDt`** (0 while paused OR while
any full-screen editor is open), so the historical "corruption spreads while paused in the
map editor" bug class is closed structurally (see Pitfalls).

## Files

### `Necroking/Game/DeathFogSystem.cs`
The spread simulation itself. A coarse per-cell scalar density grid (`CellSize=4` world
units/cell) with sources, sinks, and diffusion; keeps a sparse `_active` set so per-frame
cost scales with fog footprint, not map size.
- **`Update(EnvironmentSystem env, float dt, GroundSystem? ground)`** — THE corruption/blight
  spread tick. Order: `ApplySources` (env objects with `FogEmitRate`) → `DiffuseActiveCells`
  (explicit-Euler heat equation, `DiffusionRate` k<0.25) → `ApplySinks` (trees/absorbers with
  `FogAbsorbRate`; accumulate per-instance corruption stress, flip to `CorruptedSprite` past
  `CorruptionThreshold`) → `Swap`/`FinalizeActiveSet` → `TickGroundCorruption`. **Guards
  `if (_read.Length == 0 || dt <= 0f) return;`** — so a `dt==0` frame is a genuine no-op
  (this is what pausing relies on).
- **`TickGroundCorruption(ground, dt)`** — once-per-second pass; each vertex in an active fog
  cell rolls `P(d)=GroundCorruptionMaxRate*d²` and calls `ground.CorruptVertex(vx,vy)`.
- `ApplySources`/`ApplySinks` — scan `env.Objects` each frame for emit/absorb rates.
- `AddDensity`/`PurifyArea` — spell hooks (blight bomb adds, cleanse subtracts; see
  `Game1.Spells.cs`). `Sample`/`WorldToCell` — read density at a world point.
- Tunables (`DiffusionRate`, `CorruptionHealRate/Threshold`, `GroundCorruptionMaxRate`, …) are
  public and pushed in from settings every frame by `Game1.SyncCorruptionSettings`.
- **Look/edit here when**: fog/blight spreads too fast/slow, trees don't corrupt, ground
  doesn't turn corrupted, diffusion instability, or the spread runs when it shouldn't.

### `Necroking/Game1.Animation.cs` — `UpdateAnimations(float dt)`
Drives the fog tick and all corruption *visual* fades every frame:
`SyncCorruptionSettings()` → `_deathFog.Update(_envSystem, dt, _groundSystem)` →
`_groundSystem.AdvanceCorruptionFades(dt)` → `_grassRenderer.AdvanceFades(dt)` → uploads the
dirty vertex-map rect when `_groundSystem.CorruptionDirty`. Death-fog-consumption passive
(necro mana regen while standing in fog) is here too.
- The whole method is fed **`_clock.WorldDt`** from `Game1.Update` — paused or in an editor
  means dt=0 here, and `DeathFogSystem.Update` no-ops on dt<=0. No local editor guard needed.
- **Look/edit here when**: corruption ticks when it shouldn't (check what dt domain the call
  site passes), or a new corruption-adjacent tick needs adding — give it the same WorldDt.

### `Necroking/Game1.cs` + `Necroking/Core/GameClock.cs` — the update-loop gate
- **`GameClock`** is the central time/pause authority — see
  [game1-partials.md](game1-partials.md) "GameClock — time & pause authority" for domains.
  Short version: gameplay consumes `WorldDt` (0 while paused or in editors); visuals consume
  `VisualDt`/`VisualTime`; the sim gate is `if (_clock.WorldRunning)`.
- **`UpdateAnimations(_clock.WorldDt)`** is called after/outside the sim-gate block (it must
  run its dirty-rect texture upload every frame for editor ground painting), but its dt is
  the WORLD domain, so nothing in it advances while the world is frozen.
- `Game1.SyncCorruptionSettings` (~line 1804) pushes `_gameData.Settings.Corruption.*` into
  `_deathFog`/`_groundSystem`/`_grassRenderer` each tick.

### `Necroking/World/GroundSystem.cs`
Owns the ground vertex map and the corrupted state. `CorruptVertex(vx,vy)` (called by
`TickGroundCorruption`), `AdvanceCorruptionFades(dt)`, `CorruptionDirty`/`UploadDirtyRect`,
`OnVertexCorrupted` callback (wired in `Game1.cs` to `OnGroundVertexCorruptedForGrass`, which
fades nearby grass tufts to their `CorruptedTint`).

## Pitfalls / gotchas

- **(Historical, fixed 2026-07-04)** Corruption spread used to run in the map editor:
  `UpdateAnimations` was fed the visual dt, which the editor leaves live (editors set
  `editorActive`, not `_paused`). Fixed by moving the whole `UpdateAnimations` call onto
  `GameClock.WorldDt`. If spread-in-editor ever regresses, check which dt domain the
  `UpdateAnimations(...)` call in `Game1.Update` passes — it must be `_clock.WorldDt`.
- `DeathFogSystem.Update` early-returns on `dt <= 0f`, so any domain that yields dt=0 stops
  the spread. The grass/ground *fade* advances share that dt and freeze with it — they only
  finish transitions already begun, so a frozen world showing frozen fades is correct.
- The `CorruptionDirty` dirty-rect texture upload at the end of `UpdateAnimations` must keep
  running every frame regardless of dt — the map editor's ground painting depends on it.

## Related

- [world.md](world.md) — `EnvironmentSystem` objects carry the emit/absorb rates.
- [game1-partials.md](game1-partials.md) — `Game1.Animation.cs` `UpdateAnimations`, the
  update-loop structure, `_paused` vs `editorActive`.
- [editor.md](editor.md) — map editor menu state / `MenuState.MapEditor`.
