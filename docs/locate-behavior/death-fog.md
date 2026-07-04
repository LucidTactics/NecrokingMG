# Death fog / corruption / blight spread

The "death fog" scalar field and everything it corrupts (ground vertices, grass tufts,
trees). This is a **simulation** system, but its per-frame tick is driven from the *visual*
`UpdateAnimations` path — which is the source of the "corruption spreads while paused in the
map editor" class of bug (see Pitfalls).

## Files

### `Necroking/GameSystems/DeathFogSystem.cs`
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
- **Look/edit here when**: corruption ticks when it shouldn't, or you need to gate the fog
  spread — this is the call site, and it is *outside* the sim/editor gate (see Pitfalls).

### `Necroking/Game1.cs` — the update-loop gate
- **`dt = _paused ? 0f : MathF.Min(rawDt, 1f/20f) * _timeScale;`** (~line 2741) — the ONLY
  place `_paused` zeroes the game clock. Note it does **not** consult `editorActive`.
- **`bool editorActive = … || _menuState == MenuState.MapEditor || …;`** (~line 3290) +
  **`if (!_paused && !editorActive) { … }`** (~line 3301, closes ~3822) — the real
  "editors pause the game" gate. Wraps player input, `_sim.Tick(dt)`, workers, day/night,
  `_envSystem.UpdateAnimations`, zone spawns, projectiles — none run in the map editor.
- **`UpdateAnimations(dt)`** is called ~line 3825, **after / outside** that block, so it
  runs in every menu state including the map editor.
- `Game1.SyncCorruptionSettings` (~line 1804) pushes `_gameData.Settings.Corruption.*` into
  `_deathFog`/`_groundSystem`/`_grassRenderer` each tick.

### `Necroking/World/GroundSystem.cs`
Owns the ground vertex map and the corrupted state. `CorruptVertex(vx,vy)` (called by
`TickGroundCorruption`), `AdvanceCorruptionFades(dt)`, `CorruptionDirty`/`UploadDirtyRect`,
`OnVertexCorrupted` callback (wired in `Game1.cs` to `OnGroundVertexCorruptedForGrass`, which
fades nearby grass tufts to their `CorruptedTint`).

## Pitfalls / gotchas

- **Corruption spread is NOT gated by the map-editor pause.** The sim gate
  `if (!_paused && !editorActive)` (Game1.cs ~3301) stops `_sim.Tick` in the map editor, but
  `_deathFog.Update` is invoked from `UpdateAnimations(dt)` which is called *outside* that
  block. `dt` is only zeroed by `_paused` (Game1.cs ~2741), and entering the map editor sets
  `editorActive`, **not** `_paused` (see the `_paused = false` on the MapEditor transitions).
  So in the map editor `dt` is a live frame delta and fog keeps diffusing + corrupting ground.
  Fix options: gate the `_deathFog.Update`/corruption block in `UpdateAnimations` behind
  `!editorActive` (compute/pass an `editorActive` flag), pass `dt=0` to the fog tick when in an
  editor, or move the fog tick inside the sim block (but that also freezes it when `_paused`
  for inspect, which may or may not be wanted — corruption fades in `UpdateAnimations` are
  intentionally kept running for pure-visual smoothness, so gate the *spread* specifically,
  not the visual fades).
- `DeathFogSystem.Update` early-returns on `dt <= 0f`, so any gate that yields dt=0 is enough
  to stop spread — but the grass/ground *fade* advances also take `dt` and will likewise
  freeze if you zero the whole `UpdateAnimations` dt. Prefer a targeted flag.

## Related

- [world.md](world.md) — `EnvironmentSystem` objects carry the emit/absorb rates.
- [game1-partials.md](game1-partials.md) — `Game1.Animation.cs` `UpdateAnimations`, the
  update-loop structure, `_paused` vs `editorActive`.
- [editor.md](editor.md) — map editor menu state / `MenuState.MapEditor`.
