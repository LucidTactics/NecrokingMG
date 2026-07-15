# Camera & zoom — the 2.5D projection and every zoom consumer

> There is **NO SpriteBatch transform matrix anywhere** in the codebase (grep
> `transformMatrix` = zero hits). Every world draw computes its own screen position
> and pixel size manually from the camera. That makes "does X handle zoom?" a
> per-system question — this doc is the census.

## The camera

- **`Necroking/Render/Camera25D.cs`** — `class Camera25D`, THE single home for the 2.5D
  projection. Fields: `Position` (Vec2, world units), **`Zoom` = pixels per world unit on
  the X axis** (default 32), `YRatio = 0.5` (isometric Y foreshortening), `MinZoom = 4`,
  `MaxZoom = 128`, `ZoomSpeed = 0.1` (multiplicative: `Zoom *= 1 + delta*0.1`, clamped in
  `ZoomBy`). Three projections:
  - `WorldToScreen(worldPos, worldHeight, w, h)` — lift = `worldHeight * Zoom * YRatio`.
    For anything physical (jumps, projectile altitude, corpse Z).
  - `WorldToScreenPx(worldPos, pixelHeight, w, h)` — lift in literal pixels,
    zoom-independent. For screen-space effects anchored to world points.
  - `ScreenToWorld(screenPos, w, h)` — the inverse (mouse picking).
- **`Necroking/Render/Renderer.cs`** — thin wrappers `WorldToScreen`/`WorldToScreenPx`/
  `ScreenToWorld(…, cam)` that supply the screen size; `DrawSprite`/`DrawFlipbookFrame`
  use `pixelScale = scale * cam.Zoom / 32f` (zoom 32 = 1:1 texels).
- **The THIRD height convention (sprite-rig heights)**: sprites draw
  `SpriteWorldHeight * SpriteScale * Zoom` pixels tall (**no YRatio**), so anything
  authored against the sprite rig (weapon attach points, `EffectSpawnHeight`, casting
  glow, zap/beam start anchors, carried body bags) projects via
  `WorldToScreenPx(pos, height * cam.Zoom, cam)` — NOT `WorldToScreen`, which would
  foreshorten to half height. Commented in `Render/BuffVisualSystem.cs` (weapon
  particles) and `Render/LightningRenderer.cs`; also the `DamageNumber.Height` trap in
  [render.md](render.md) "Floating text".

## How zoom changes (input paths)

- **In-game scroll-zoom** — `UI/Layers/HudLayers.cs` `WorldClickLayer.HandleInput`
  (`Id == "world"`): `_g._camera.ZoomBy(ScrollDelta / 120f)` when
  `!IsScrollConsumed && !MouseOverUI`; works while paused.
- **Map editor scroll-zoom** — `UI/Layers/HostLayers.cs` `MapEditorHostLayer.OnFrame`
  (only when no popup and cursor off the side panel).
- **Dev command** — `Game1.Dev.cs` `camera x y [zoom]` (clamps to Min/MaxZoom, sets
  `_devFreeCamera`).
- **Set on load** — `Game1.cs` `StartGame`: `Zoom = 48` for `empty_test`, else `24`;
  scenarios override via `ScenarioBase.CameraZoom`.
- **DEAD duplicates (no callers)**: `Camera25D.HandleInput` and
  `Renderer.HandleCameraInput` both contain a WASD-pan + scroll-zoom block that nothing
  invokes — don't fix zoom behavior there; the live handlers are the layers above.
- **Pixel snap**: `GameRenderer.Draw.cs` snaps `camera.Position` to the pixel grid
  (`1/Zoom`, `1/(Zoom*YRatio)`) for the Scene phase and restores the smooth position for
  the Hud phase — camera reads during scene drawing see the snapped position.

## Zoom-consumer census (all manual math; "how it handles zoom")

**Fully zoom-scaled (position + size derived from Zoom)** — the standard pattern is
`pixelH = worldH * cam.Zoom` then `scale = pixelH / texH`, or `radius * cam.Zoom`:
- Units/env/corpses: `GameRenderer.Units.cs` (`DrawSingleUnit`, `DrawSingleEnvObject`,
  occlusion boxes, view culling `screenW / (2*Zoom)`), `GameRenderer.Corpses.cs`.
- Shadows: `Render/ShadowRenderer.cs` (radius, skew length, view cull — all ×Zoom).
- Projectiles + EffectManager effects: `GameRenderer.World.cs` `DrawProjectiles`/
  `DrawEffectsFiltered` (arrow length `12 * Zoom/32`, particle `worldSize * Zoom`,
  glow fallbacks ×`Zoom/32`).
- Buff visuals: `Render/BuffVisualSystem.cs` (ground auras, orbitals, unit-effect
  flipbooks, lightning-arc radius, weapon particles — all ×Zoom; weapon particles use
  the sprite-rig height convention).
- Reanim rise/dust: `Render/ReanimEffectSystem.cs` (`WorldSize * zoom`).
- Poison clouds: `Render/PoisonCloudRenderer.cs` (`CurrentRadius * Zoom`; converts pixel
  offsets back to world via `/(Zoom*YRatio)` for depth-sort Y).
- Magic glyphs: `Render/MagicGlyphRenderer.cs` (`Radius * Zoom`, rise `2 * Zoom`).
- Death-fog puffs: `Render/DeathFogRenderer.cs`; ground-fog wisps:
  `Render/GroundFogSystem.cs`; wading wakes: `Render/WadingWakeSystem.cs` (`Size * Zoom`).
- Grass tufts: `Render/GrassTuftRenderer.cs` — plus a **zoom cull**:
  `MinZoomForGrass = 5` (grass skipped when zoomed far out).
- Roads/walls/ground fallback tiles: `GameRenderer.World.cs` (`tileW = Zoom`,
  `tileH = Zoom * YRatio`).
- Ground shader: `GameRenderer.World.cs` passes `Zoom`/`YRatio`/`CameraPos`/`ScreenSize`
  uniforms; `resources/GroundShader.fx` inverts the projection per pixel.
- Weather fog shader: `Render/WeatherRenderer.cs` `DrawFog` computes `FogOrigin`/
  `FogWorldScale` from camera → `WeatherFog.fx` noise is world-anchored (zoom-aware).
- Fog of war: `Render/FogOfWarSystem.cs` `Draw` projects the world-rect corners with
  `WorldToScreen` and stretches the RT — safe by construction.
- Hover ground markers + circle-targeting aim overlay: `GameRenderer.Units.cs`
  `DrawHoverGroundMarkers` / `DrawSpellAimCircle` — project center AND
  `center + (radius, 0)` (aim circle also `(0, radius)`), so radii track zoom exactly;
  outline thickness is a deliberate constant-px style.
- Build ghost preview: `Render/EnvGhostRenderer.cs` (`worldH * cameraZoom / frameH`,
  `PlacementRadius * cameraZoom`); callers `UI/BuildingMenuUI.cs` `DrawGhostPreview` and
  `Editor/MapEditorWindow.cs`.
- Map editor gizmos: `Editor/MapEditorWindow.cs` — region/zone circles ×Zoom, drag-handle
  tolerance `8 / Zoom` (constant screen px), camera pan speed clamped `∝ 1/Zoom`.
- Table-craft world menu: `UI/TableCraftMenuUI.cs` — `_uiScale = Zoom / BaseZoom(32)` so
  the whole panel scales like an in-world object (the world-anchored-UI precedent).
- Debug overlays: `Render/DebugDraw.cs` (tiles ×Zoom, labels only when `Zoom >= 8`),
  `GameRenderer.Hud.cs` wind-debug grid (cell size `40/Zoom` so cell count is bounded).

**Deliberately screen-space (pixel-constant by design — don't "fix" without intent)**:
- **Rain** — `Render/WeatherRenderer.cs` `DrawRainParticles`: streak dims are pixels
  (`RAIN_PX_PER_UNIT = 16`), fall speed screen-space; BUT it already has the two zoom
  adaptations: drop size `zoomDropScale = 1 + zoomT` (1× at MinZoom → 2× at MaxZoom) and
  **zoom-based density culling** `priorityThreshold = RainDensity * (0.02 + 0.98*zoomNorm²)`
  against `RAIN_REF_ZOOM = 48` (fewer drops when zoomed out). Snow/wind visuals: ABSENT.
- **Lightning/zaps/beams/drains** — `Render/LightningRenderer.cs` + `Render/HdrStripBatch.cs`:
  endpoints projected per frame, AOE telegraph radius ×Zoom, but bolt `CoreWidth`/
  `GlowWidth`, midpoint displacement and drain arc/wave amplitudes (hardcoded 20/5 px;
  `DrainVisualParams.ArcHeight` unconsumed) are **screen pixels** — the Camera25D comment
  blesses "lightning arc shapes" as zoom-independent, so at far zoom bolts look fatter
  relative to the world.
- **Unit health bars** — `GameRenderer.Units.cs` `DrawHealthBar`: fixed `30×3` px bar;
  only the vertical offset (`spriteWorldH * SpriteScale * Zoom * 0.9 + 5`) scales.
- **Damage numbers / floating text** — `GameRenderer.World.cs` `DrawDamageNumbers`: text
  scale from settings (`DamageNumberSize/16`), not zoom; position/lift via `WorldToScreen`
  (drift `Height` is world units, so drift SPEED on screen scales with zoom).
- **Status ?/! glyphs** — `GameRenderer.Units.cs` `DrawSingleUnit` `sp_upper`: font size
  fixed, anchor offset partially zoom-scaled (`0.25 * Zoom * YRatio`).
- **Hover-marker / aim-circle line thickness** — constant px (documented choice).
- Cursor tooltips, HUD chrome, combat log — pure screen-space, zoom-irrelevant.

## Look/edit here when…
- "Zoom feels wrong / clamp / speed" → `Render/Camera25D.cs` (`ZoomBy`, Min/Max/ZoomSpeed);
  input sites in `UI/Layers/HudLayers.cs` + `UI/Layers/HostLayers.cs`.
- "Effect X doesn't scale with zoom" → find its renderer above; the fix is usually
  `worldSize * cam.Zoom` instead of a px constant (or the reverse if it should be
  screen-space — check the Camera25D header comment for which convention applies).
- "Thing floats above/below its anchor as I zoom" → wrong height convention: physical
  heights → `WorldToScreen`; sprite-rig heights → `WorldToScreenPx(pos, h * Zoom)`.
- "Add zoom-based culling/LOD" → precedents: rain `priorityThreshold` (WeatherRenderer),
  grass `MinZoomForGrass` (GrassTuftRenderer), DebugDraw's `Zoom >= 8` label gate.

## Related
- [render.md](render.md) — draw pipeline, damage-number height trap, lightning ribbons.
- [game1-partials.md](game1-partials.md) — `_camera` field lives on Game1; camera follow +
  free-cam pan in `Game1.cs` Update.
