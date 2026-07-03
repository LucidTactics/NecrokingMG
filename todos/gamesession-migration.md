# GameSession migration — own all per-game memory in one recreatable object

## Goal
Eliminate the whole *class* of "forgot to clear/dispose on map reload" leaks (wall defs,
flipbooks, ground/env textures were all instances of it) by giving every per-game resource
a single owner: `GameSession` (`Necroking/Game/GameSession.cs`). Game1 holds one `_session`
and exposes its members via **forwarding properties**, so existing `_envSystem.Foo()` /
`_g._envSystem` call sites are unchanged. `StartGame` does `_session.Dispose(); _session = new()`:
Dispose() frees GPU/native resources, the reassignment drops all references to the previous
map's managed state. **Rule: a new per-game resource that isn't in GameSession can still leak.**

## The pattern (per system)
1. Move the field into `GameSession` (`public readonly Foo X = new();`).
2. Replace the Game1 field with a forwarding property: `internal Foo _foo => _session.X;`
3. If it owns GPU/native resources, dispose them in `GameSession.Dispose()`.
4. Delete its now-redundant per-load `ClearX`/`Reset` call from `StartGame`.
5. Build + run + screenshot + `mem` to verify.

Reassignable fields (not readonly-object) need care — a get-only forwarding property breaks
`_x = ...` assignments. Either give the property a setter that writes `_session.X`, or move
the reassignment site to poke `_session.X` directly.

## Lifetime classification
**App-lifetime — DO NOT move (reused across games; recreating would reload shaders/atlases
or break renderers):** `_gameData`, `_atlases`, fonts, `_widgetRenderer`, `_camera`,
`_renderer`, `_bloom`, `_weatherRenderer`, `_hudRenderer`, `_characterStatsUI`,
`_lightningRenderer`, `_poisonCloudRenderer`, `_deathFogRenderer`, `_reanimFx`,
`_glyphRenderer`, `_debugDraw`, `_buffVisuals`, `_shadowRenderer`, editors, `_glowTex`,
`_mainMenuBg`, `_rng`, spell-bar state.

**Per-game — migrate into GameSession:**
- [x] `_groundSystem` (Ground) — owns textures (ClearTypes disposes)
- [x] `_envSystem` (Env) — owns textures + corruption variants (ClearDefs disposes)
- [x] `_wallSystem` (Wall) — defs list (the headline leak)
- [x] `_roadSystem` (Road) — managed
- [x] `_sim` (Simulation) — big managed state; forwarding property `_sim => _session.Sim`.
      Managed-only (no GPU), so no Dispose wiring; dropping the session reference frees it.
      The `census` dev command reads its collections via `GameSession.Census()`.
- [ ] `_fogOfWar` (FogOfWarSystem) — owns 4 world-sized RTs + circle tex (has Dispose()).
- [ ] `_grassRenderer` (GrassTuftRenderer) — `_texCache` textures (has Dispose()).
- [ ] `_villageSystem` (Game1.Villages.cs, private readonly)
- [ ] `_wakeSystem` (WadingWakeSystem) — variant textures (DisposeAllVariants()).
- [ ] `_deathFog` (DeathFogSystem)
- [ ] `_triggerSystem`, `_dayNightSystem`, `_workerSystem`
- [ ] `_reanimMorph` (ReanimMorph) — cache holds 3 textures per morph; NEVER cleared today
      (bounded by unit-type count, but should be owned + disposed here).
- [ ] `_flipbooks` (Dictionary, reassigned) — Unload() each on dispose; reassignable → needs setter or move.
- [ ] `_groundVertexMapTex` (Texture2D, reassigned) — dispose on session dispose; reassignable.

## Verify each stage
`necro_start` → `start_game testmap` / `default` a few times alternating → `screenshot` (world
must render: ground + env objects + units) → `mem` (managed heap flat across reloads, private
bounded). The `mem` dev command reports managed-before/after a forced compacting GC.

## Watch out
- `Initialize()` must not populate per-game systems before the first StartGame recreate (checked
  for ground/env/wall — they load in StartGame; re-check for each new system moved).
- Renderers that hold RTs but are reused across games stay app-lifetime — do not move them.
- Don't recreate `_session` anywhere except StartGame.
