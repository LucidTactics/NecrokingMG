# Render/ — rendering subsystems

> **Coverage: PARTIAL.** Documented: the **draw-dispatch pipeline** (top-level `Draw`,
> batch/shader/state model — see next section), the **rendering feature inventory**
> (every technique tried, with status), the **target feature set** (what we want the
> renderer to do), and the **visual-effect / flipbook** systems. Still undocumented:
> sprite atlases & frames internals, `FontManager`, `HUDRenderer`, the spellbar widget,
> `RuntimeWidgetRenderer`. Extend this file when you touch those.

## The draw-dispatch pipeline (how a frame is rendered)

**There is no retained scene graph and no general draw queue.** Rendering is **imperative
immediate-mode**: a fixed sequence of `SpriteBatch.Begin(...) / …Draw… / End()` blocks, run
top-to-bottom once per frame. "Layering / passes" = the *order* of those blocks. The only
queue-like structure is a per-frame **Y-sort depth list** used inside one pass (units + env
objects + particles), not a global command buffer.

### `Necroking/GameRenderer.cs` (+ `.Draw/.World/.Units/.Corpses/.Hud.cs`) — the whole pipeline
`internal sealed partial class GameRenderer` was extracted from the old `Game1.Render.*`
partials (2026-06-30). It holds a back-reference `_g` to `Game1` and reaches all state
through it (`_g._spriteBatch`, `_g._camera`, `_g._sim`, `_g._bloom`, shader effects, etc.).
`Game1.Draw(GameTime)` (MonoGame override) forwards to `GameRenderer.Draw`.

- **`GameRenderer.Draw.cs` → `Draw(GameTime)`** is the **top-level conductor / master pass
  sequence**. Read this file first for the redesign — it *is* the pass list. Order:
  main-menu/scenario-list early-outs → camera pixel-snap → fog-of-war RT update → **bloom
  scene capture begin** (`_g._bloom.BeginScene`) → **scene pass begin** (`EffectBatch.BeginScenePass`)
  → ground → roads → ground-layer objects (traps) → magic glyphs → walls → shadows → hover
  ground markers → corpses → **units+objects+particles (Y-sorted)** → projectiles → soul orbs
  → rope → rain → `End()` → HDR alpha-effect pass → HDR additive pass (effects, lightning,
  reanim particles) → additive pass (energy columns) → god rays → alpha pass (foragables,
  damage numbers) → **bloom composite** (`_g._bloom.EndScene`) → fog-of-war overlay → debug
  overlays (each its own Begin/End) → **HUD pass** (`EffectBatch.BeginHudPass`) → weather fog
  → HUD/spellbar → inventory/panels/editors/menus → perf readout → `End()` → screenshots →
  `_g.BaseDraw` (Present). Each `--- Foo ---` comment block is effectively a pass.
- **`GameRenderer.World.cs`** — ground (`DrawGround`/`DrawGroundShader`), `DrawRoads`,
  `DrawWalls`, `DrawGroundLayerObjects`, `DrawProjectiles`/`DrawProjectilesHdr`,
  `DrawEffectsFiltered(blendMode)` (iterates `_g._effectManager.Effects`), `DrawDamageNumbers`,
  `DrawSoulOrbs`, `SpawnImpactEffects`.
- **`GameRenderer.Units.cs`** — `DrawUnitsAndObjects` (builds + sorts the depth list, below),
  `DrawSingleUnit`/`DrawSingleEnvObject`, low-level blits `DrawSpriteFrame`/`DrawWadingSpriteFrame`/
  `DrawSpriteOutline`, hover-highlight/pick system.
- **`GameRenderer.Corpses.cs`** — corpses, reanim morph, body-bags, carried visuals.
- **`GameRenderer.Hud.cs`** — `DrawHUD`, menus, toasts, aggression bar, debug overlays.

### Draw submission — immediate-mode, plus one Y-sort depth list
- **Every visible thing is drawn by an immediate `_g._spriteBatch.Draw(...)` call** inside
  whichever Begin/End block owns that layer. Sprite blits are centralized in
  `Render/Renderer.cs` (`Renderer.DrawSprite(batch, atlas, frame, …)`,
  `DrawFlipbookFrame`, and `WorldToScreen`/`WorldToScreenPx` for coordinate conversion) —
  callers pass an already-`Begin`-ed batch; `Renderer` never manages batch state.
- **The one queue:** `GameRenderer.Units.cs` `DrawUnitsAndObjects()` builds
  `_g._depthItems` (`List<DepthItem>`, reused each frame; `DepthItem`/`DepthItemType` defined
  in `Game1.cs` ~line 4130) from units, env objects, poison-cloud puffs, grass tufts,
  death-fog puffs, reanim dust — each tagged with a world `Y` and a type — then
  `items.Sort()` (Y-ascending painter's order) and a `switch` dispatches each back to its
  `DrawSingleX`. This is the **only** place draw order is data-driven rather than hardcoded;
  it exists so particles/grass interleave with units by depth. A redesign's "transparent /
  sorted pass" is the natural generalization of this list.

### Shader / blend / sampler selection — `Necroking/Render/EffectBatch.cs` (the canonical state hub)
This is the **"batch-state centralization"** (commit `d626422`) and the closest thing to a
"render-pass state" abstraction today.
- `EffectBatch` holds the **canonical pass states as static fields** — `SceneBlend`/`SceneSampler`
  (AlphaBlend + LinearClamp, premultiplied-alpha) and `HudBlend`/`HudSampler` (AlphaBlend +
  PointClamp). `BeginScenePass(batch)` / `BeginHudPass(batch)` are the *definition* of those
  passes, not copies — `Draw.cs` opens both through them.
- **The flush-with-shader pattern lives here:** `BeginEffect(batch, effect, blend, sampler,
  sortMode)` does `batch.End(); batch.Begin(sortMode, blend, sampler, null, null, effect)` —
  i.e. "flush everything queued so far, then start a new batch bound to this `.fx` Effect and
  these states." Paired restores are `EndEffectResumeScene(batch)` / `EndEffectResumeHud(batch)`
  (`End()` then re-`Begin` the canonical pass). The ground shader uses exactly this:
  `DrawGroundShader` calls `EffectBatch.BeginEffect(_g._spriteBatch, _g._groundEffect,
  BlendState.Opaque, SamplerState.PointClamp)` → one `Draw` of the vertex-map texture →
  `EndEffectResumeScene`. **Why it's centralized:** effect sites used to hand-roll
  End/Begin/restore and two shipped bugs came from guessing the restore state wrong (a
  PointClamp restore leaking into the LinearClamp scene pass). Change a pass's blend/sampler
  here and every suspend site follows.
- **Direct Begin with an effect** (bypassing `EffectBatch`) is still used where the pass owns
  its own batch: the HDR effect passes in `Draw.cs` do `_g._spriteBatch.Begin(Deferred,
  BlendState.Additive|AlphaBlend, LinearClamp, effect: _g._hdrSpriteEffect)` and set
  `_hdrSpriteEffect.Parameters["AlphaMode"]` per pass; `BloomRenderer.EndScene` runs its whole
  mip chain as `Begin(Immediate, …, effect)` blocks.
- **`Necroking/Render/UIShaders.cs`** — a *parallel, deliberately-separate* suspend/restore
  mechanism for UI shaders (gradients, rect-shadow, circle). It End/Begins the batch around
  each effect using **constructor-injected** `_defaultBlend`/`_defaultSampler` restore state.
  `EffectBatch`'s docstring explicitly says *don't* fold UIShaders into it — it already solved
  the restore problem its own way.

### Shader uniforms per draw
Shaders are `Microsoft.Xna.Framework.Graphics.Effect` (aliased `XnaEffect`), loaded via
`content.Load<Effect>("Name")` from compiled `.fx` (see `resources/`). Uniforms are pushed
imperatively right before the batch: e.g. `DrawGroundShader` sets ~15
`_g._groundEffect.Parameters["…"]?.SetValue(...)` (camera, zoom, ambient, tint/water arrays,
ground textures bound to sampler slots) then does the single batched draw. `BloomRenderer`
sets `BloomThreshold`/`SoftKnee`/`BloomIntensity`/blur kernels the same way.

### Render targets / bloom / compositing — `Necroking/Render/Bloom.cs`
`BloomRenderer` owns the HDR scene RT (`SurfaceFormat.HalfVector4`, so additive effects exceed
1.0) + a mip chain. `BeginScene(device)` binds the scene RT and clears; the whole scene draws
into it; `EndScene(device, batch, settings, outputTarget)` runs prefilter→downsample→upsample→
blur→composite (each an `Immediate` batch with an `.fx`) and blits the result to `outputTarget`
(null = back buffer). When bloom is off, `Draw.cs` clears the back buffer directly and skips
Begin/EndScene. **This is the only multi-render-target work in the pipeline** — everything else
targets the current RT (scene RT or back buffer).

### Where a new pass-based dispatcher would slot in (for the redesign)
- The **pass list to formalize** is the top-to-bottom body of `GameRenderer.Draw.cs` `Draw()`.
  A Unity-like `RenderPass` abstraction (name, blend, sampler, sort mode, optional `.fx`,
  target RT, enabled predicate) would replace each `--- Foo ---` Begin/End block; `Draw()`
  becomes "iterate an ordered `List<RenderPass>`."
- **Extend `EffectBatch`, don't replace it** — it already encapsulates pass-state as data
  (`SceneBlend`/`HudBlend` + Begin helpers). A `RenderPass` struct is the natural home for
  those fields; the `BeginEffect`/`EndEffectResume*` suspend/resume becomes push/pop of a pass
  stack.
- **Generalize the depth list** (`_g._depthItems` in `DrawUnitsAndObjects`) into the redesign's
  transparent/sorted pass — it's the existing sort-by-depth submission model.
- **Bloom's RT swap** (`Bloom.BeginScene`/`EndScene`) is the template for pass-scoped render
  targets; fog-of-war (`FogOfWarSystem`) also swaps RTs before the scene pass.
- Keep sprite blits going through `Renderer.DrawSprite` so a pass system only owns
  batch/state/order, not per-sprite geometry (matches CLAUDE.md "shared component owns
  mechanics, caller owns data").

## Rendering feature inventory — everything we've tried, and its status

One entry per feature: where it lives, how it works in a sentence, status
(**working / partial / known-weak / failed-reference / absent**), gotchas. Statuses as
of 2026-07-03; the deferred-issue list behind several of them is
`todos/shader-review-followups.md`.

### Fog & visibility
- **Death fog (ground blight fog)** — WORKING. Sim: `GameSystems/DeathFogSystem.cs`
  (coarse 4-unit grid, heat-equation diffusion, sources/sinks from env-def
  `FogEmitRate`/`FogAbsorbRate`; also drives tree corruption and ground-vertex
  corruption). Render: `Render/DeathFogRenderer.cs` — **no shader**, one cloud-flipbook
  puff per active cell (density→alpha/scale, `MaxAlpha=0.20`), appended into the shared
  Y-sort depth list (`DepthItemType.DeathFogPuff`) so units occlude/are occluded by fog
  by ground Y. **No "unit rises out of the fog" clipping exists** — see target #1.
- **Fog of war** — WORKING (GPU, RT-based). `Render/FogOfWarSystem.cs`: visibility RT
  (vision circles) → temporal smooth via `FogSmooth.fx` → cumulative explored RT →
  packed combine; composited post-bloom by `FogComposite.fx`. CPU tile mirror for
  gameplay culling. Gotchas: full 2048² passes every frame even when vision is static;
  `FogSmooth` 8-bit RT stalls at uncapped FPS (grey veil).
- **Weather fog / haze / vignette** — WORKING. `Render/WeatherRenderer.cs` `DrawFog`
  via `WeatherFog.fx` in the HUD pass: world-anchored scrolling noise fog, haze,
  vignette, lightning screen-flash. Brightness/tint blocks in the shader are pinned
  neutral — scene darkening moved pre-bloom to ambient (see day/night) to avoid
  double-darkening.

### Weather & time of day
- **Weather presets** — PARTIAL. Data-driven `WeatherEffects` presets in
  `user settings/weather.json` (dreary_rain, thunderstorm, dawn, night, evil_night, …):
  brightness/contrast/saturation, tint, ambient, vignette, fog, haze, lightning
  intervals, rain params, **windStrength/windAngle (in data, consumed by nothing)**.
- **Rain** — WORKING. `WeatherRenderer.DrawRainParticles`: procedural hash-grid drop
  field, screen-space streaks + splash ellipses, zoom-scaled, wind-drifted, drawn in
  the scene pass. Cap `MAX_RAIN=6000`. **Snow: ABSENT. Wind (visual): ABSENT.**
- **Day/night cycle** — WORKING, but color-grading only. `Game/DayNightSystem.cs`:
  Dawn/Day/Dusk/Night phases each map to a weather preset, eased lerp between them,
  writes `RuntimeEffects` that `WeatherRenderer.GetEffectiveEffects` serves. Night =
  a global ambient multiply (GroundShader `AmbientColor` + sprite tints via
  `WeatherRenderer.GetAmbientColor()`), applied pre-bloom so HDR effects still glow
  against the darkened scene. **No local lighting of any kind** — see target #5.

### Partial submersion & transparency
- **Wading (units part-submerged in water)** — WORKING; the closest precedent for
  "units sticking up out of fog". `resources/Wading.fx` cuts the sprite along a
  waterline expressed in frame-local UV (center + slope, optional second cut for the
  submerged back of quadrupeds), fades below-line pixels to `UnderwaterAlpha`, draws a
  foam band on the line; fully premultiplied. Per-unit line resolution in
  `Render/WadingState.cs` (waterness sampling + per-facing profiles from
  `data/wading_defaults.json`); draw in `GameRenderer.Units.cs` `DrawWadingSpriteFrame`;
  `ShadowRenderer` reuses the same state so shadow and sprite share one waterline.
  Gotcha: wading units still stamp their full silhouette as fog depth-occluders.
- **Tree dissolve (corruption morph)** — WORKING. `DissolveTree.fx` noise-threshold
  dissolves the live sprite into the corrupted sprite per instance (Seed, EdgeSoftness).
  It is the death-fog corruption transition, **not** an occlusion fade — there is no
  "tree fades when it hides a unit" feature (target #3).
- **Depth cutout** — WORKING (narrow). `DepthCutout.fx` writes depth-only unit
  silhouettes (`DrawFogDepthOccluders`, behind the `DepthSortedFog` perf setting) so
  additive reanim smoke depth-tests against units. The only real depth-buffer use in
  the pipeline; C++ had a general depth-sprite pass (`building_depth`/`wall_depth`/
  `depth_sprite.fs`), C# does not.
- **General transparency sorting** — the Y-sort depth list (`_depthItems`) is the only
  mechanism; grass tufts, poison/fog puffs, reanim dust, ghost units (premultiplied
  tint + `DrawGhostOutline`), fading corpses all ride it.

### Emissive / additive / HDR
- **HDR sprites + additive passes** — WORKING. `HdrSprite.fx` (AlphaMode 0=additive
  /1=alpha, intensity encoded in vertex color via `Core/HdrColor.cs` `ToHdrVertex`/
  `ToHdrVertexAlpha`) and `HdrIntensity.fx` (god-ray trapezoid geometry). Scene RT is
  `HalfVector4` so additive exceeds 1.0 and bloom picks it up. **Never use
  `HdrColor.ToScaledColor()` outside the color picker** — it bleaches HDR
  (`todos/bloom_parity.md`).
- **Premultiplied-alpha convention** (commit `d626422`): scene pass = AlphaBlend +
  LinearClamp over premultiplied textures; every scene shader outputs premultiplied
  (Wading, DissolveTree, MorphSDF, MagicCircle, HdrSprite). Exception: `OutlineFlat.fx`
  outputs straight alpha (NonPremultiplied/Additive only — header warns).
- **God rays** — WORKING, known-weak parity. `Render/GodRayRenderer.cs`: layered
  trapezoid strips + edge sublayers + ground aura, per-sublayer HDR intensity. Renders
  brighter/whiter with less spread than C++ (see bloom).
- **Lightning** — WORKING. `Render/LightningRenderer.cs` + `Game/LightningSystem.cs`:
  telegraph circle, radial flash, HDR bolts and drains.
- **Bloom** — WORKING, KNOWN-WEAK vs C++. `Render/Bloom.cs` mip chain
  (`BloomExtract`/`BloomCombine`/`BloomUpsampleBicubic`). Full parity investigation in
  `todos/bloom_parity.md`; suspects: HLSL-vs-GLSL bicubic, a disabled Gaussian soften
  pass (`if (false &&` in Bloom.cs), mip sizing, `ToScaledColor` overuse. Related
  memory: C++ raylib ignored per-sublayer HDR intensities (batching bug), so C++-tuned
  intensity values in spells.json are inflated — retune down when porting
  (`todos/rendering_pitfalls.md`).

### Surfaces & world
- **Ground shader** — WORKING, feature-rich. `GroundShader.fx` (one opaque quad over
  the vertex-map texture): 8 texture slots + bit-packed type map, per-type
  `TintColors[16]` after 4-corner bilerp, animated shore foam for water types, noise UV
  warp, **corruption crossfade** (R=current/G=original/B=progress per vertex, fading
  texture AND tint AND water flag), global `AmbientColor`.
- **Shadows** — WORKING, two techniques. `Render/ShadowRenderer.cs`: ellipse mode (two
  soft glow ovals) or shader mode (sun-angle-skewed parallelogram quads sampling the
  current animation frame as silhouette); per-env-def `ShadowType`/scales, crops to
  wading waterline, crossfades reanimating corpses and corrupting trees. Wall shadows
  live in `WallSystem.DrawWalls`, not here.
- **Outlines** — WORKING. `OutlineFlat.fx` + `DrawSpriteOutline` (8 offset redraws):
  buff pulsing outlines, ghost outlines, reanim-rise attachment.
- **Magic glyphs** — WORKING, known-weak AA. `MagicCircle.fx` — fully procedural rings
  /runes/pentagram/energy; edge widths in normalized UV so it blurs large and aliases
  small; activation flare LDR-capped. Highest-payoff deferred shader rework.
- **Reanim morph** — WORKING. `MorphSDF.fx` interpolates death-pose/standup-pose SDFs
  with a mid-morph bulge + green energy fill (`Render/ReanimMorph.cs`). Gotcha: t=0
  re-threshold pop on soft-edged art.
- **Soul orbs** — WORKING but crude: two stacked non-premultiplied `_pixel` quads
  (`DrawSoulOrbs`).

### UI effects
- **`Render/UIShaders.cs`** — WORKING, the sanctioned path: `UIGradient.fx`
  (vertical/horizontal/3-stop/radial), `UIRectShadow.fx` (drop + inset), and
  `UICircleEffect.fx` (AA circle + glow ring), each with no-shader fallbacks. Policy:
  isolated test scenario per shader before real use (`todos/css_rendering.md`).
  Gotcha: the hardcoded resume `Begin` discards caller scissor/transform.
- **`Render/UIGfx.cs`** — FAILED-REFERENCE, since **removed**. The earlier
  stacked-SpriteBatch attempt at CSS effects (gradients/glows/emboss) produced
  banding/halos and was rejected; `UIShaders` replaced it. `Render/SkillTreePanel.cs`
  still carries placeholder visuals from that era.

### Absent entirely
- **Local lighting** — no point lights, no torch/lantern glow, no light-around-unit.
  "Lighting" today = global ambient multiply + emissive HDR/bloom. (Target #5.)
- **Custom vertex shaders** — C# is pixel-shader-only on SpriteBatch's built-in VS
  (see `memory/mgfx_shader_gotchas.md`); C++ had `grass.vs`/`road.vs`. Grass/roads are
  CPU-transformed sprites here.
- **Snow, visible wind** — data fields exist (`weather.json`), nothing renders them.

### Read-before-touching notes
- `todos/rendering_pitfalls.md` — C++ raylib → MonoGame porting gotchas (batched
  SetShaderValue, RT DiscardContents, Additive=SRC_ALPHA, HDR alpha accumulation).
- `memory/mgfx_shader_gotchas.md` — MGFX/OpenGL defaults all uniforms to 0: C# must
  set every uniform every draw.
- Rendering test scenarios: `blend_test`, `godray_render_test`, `BloomDebugScenario`
  (identical-parameter twins exist in C++ for pixel-readback comparison).

## Target feature set — what we want the renderer to do

The wishlist that the pass-based redesign (previous section) should be designed
against. Each item names the existing ingredient tech so a design can build on it
instead of starting fresh.

1. **Ground fog that units rise out of.** Fog should read as a volume: a unit standing
   in it shows torso/head above the fog with legs swallowed, not just "puff sprite in
   front of / behind the unit" (today's death fog is Y-sorted opaque puffs, item 1 of
   the inventory). Ingredients already built: **`Wading.fx`** proves per-sprite
   partial-height cutting with a soft band at the line (swap waterline → fog line,
   foam → fog wisp), and **`DepthCutout.fx`** proves depth-only unit silhouettes
   against a translucent layer. Likely shape: a fog layer pass whose alpha per pixel
   knows "how deep into the fog is this sprite pixel" — either the wading-style
   per-sprite cut driven by local fog density, or a screen-space fog quad
   depth-tested against sprite silhouettes.
2. **Proper transparent objects.** Anything translucent (ghosts, fog puffs, grass,
   smoke, glass-like effects) should sort correctly against units and each other. The
   Y-sort depth list is the seed; the redesign's transparent pass should generalize it
   rather than each feature hand-inserting item types.
3. **Occlusion fade.** A tree/building that hides a unit (especially the player)
   should go semi-transparent. Absent today — `DissolveTree.fx` is the corruption
   morph, not this. Needs a "what does this object occlude" test + per-object alpha,
   which the depth-list pass already has the data for.
4. **Additive/emissive objects as first-class citizens.** HDR + additive passes and
   bloom exist and work; keep them correct through any redesign (premultiplied
   convention, HalfVector4 scene RT, ambient applied pre-bloom so emissives still glow
   at night). Reach bloom parity with C++ (`todos/bloom_parity.md`) — softer, wider
   glow rather than bright-white cores.
5. **Local lighting.** Torches, lanterns, spell glows, windows at night — pools of
   light that punch through the night-time ambient darkening. Nothing exists.
   A cheap 2.5D take: a light-accumulation RT (additive glow sprites per light source)
   multiplied/screened over the darkened scene before bloom; full per-pixel normals
   are NOT the goal.
6. **Weather as a system, not one effect.** Rain works; want snow, drifting wind
   (bend grass/trees — the data fields already exist in `weather.json`), and storms
   that compose rain + wind + lightning flash + bolt strikes. Presets + day/night
   already interpolate; new effects should slot into `WeatherEffects` the same way.
7. **Day/night that changes more than color.** Keep the ambient grade, add the things
   ambient can't do: local lights mattering at night (#5), longer/softer shadows by
   sun angle (ShadowRenderer already has `SunAngle`/`LengthScale` — drive them from
   `DayNightSystem`), fog that thickens at dawn/dusk (fog density is currently
   deliberately not interpolated).
8. **Fog-of-war that scales.** Keep the RT approach but stop redrawing 2048² every
   static frame; fix the 8-bit smoothing stall (both flagged in
   `todos/shader-review-followups.md`).

## Visual effects (the "play a one-shot visual at a point" system)

### `Necroking/Render/EffectManager.cs` — general world visual-effect pool
What lives here: the lightweight visual-effect system. `class Effect` is one timed visual
(position, `Lifetime`, alpha/scale `BezierCurve`s, `Tint`, `HdrIntensity`, `FlipbookKey`,
`BlendMode` 0=alpha/1=additive, `Alignment` 0=ground/1=upright). `class EffectManager`
owns a `List<Effect>`, `Update(dt)` ages and culls them, and exposes the **spawn API**.
These are pure visuals — no gameplay/sim state.
Key members: `EffectManager.SpawnSpellImpact(pos, scale, tint, flipbookKey, hdrIntensity,
blendMode, alignment, duration)` — the general "flipbook at a world point" spawn;
`SpawnExplosion(pos, radius)`, `SpawnDustPuff(pos)` — preset one-liners; `Update`,
`Clear`, `Effects` (read-only list); the `Effect` fields above.
Look/edit here when: adding a **new kind** of generic visual effect, adding a new `SpawnX`
preset, or changing how effects fade/scale/age. **New effect-spawn methods go here.**
`SpawnDustPuff(Vec2 pos)` is the ready-made dust preset (0.5s life, brown tint, no
flipbook) — call `_effectManager.SpawnDustPuff(pos)` to kick up dust at a world point
(prior art: `Game/ForagableSystem.cs` calls it on pickup).
See also: spawned via `Game1.Spells.cs` helpers; drawn by **`GameRenderer.World.cs`**
(`DrawEffectsFiltered` iterates `_effectManager.Effects`) — NOTE: the render passes were
extracted from the old `Game1.Render.*` partials into a `GameRenderer` class
(`GameRenderer.{Draw,World,Units,Corpses,Hud}.cs`) that reaches back into `Game1` via a
`_g` field; the `_effectManager` field + its per-frame `Update` live in `Game1`
(see [game1-partials.md](game1-partials.md)).

## World-space overlay drawing (lines / bezier over the world)
- **`Necroking/Render/DrawUtils.cs`** — `DrawLine(SpriteBatch, Texture2D pixel, Vector2 a,
  Vector2 b, Color)` (a rotated-pixel segment) and `DrawCircleOutline(...)`. These take
  **screen-space** points. To draw a rope/bezier in the world, sample the curve in world
  coords, convert each point with `_g._renderer.WorldToScreen(worldPos, height, _g._camera)`
  (see `GameRenderer.World.cs`), and chain `DrawUtils.DrawLine` between consecutive screen
  points using the 1×1 white texture `_g._pixel`. `struct BezierCurve` in
  `Render/Flipbook.cs` is a 1-D 4-control-point curve (used for effect alpha/scale), not a
  2-D spatial curve — for a positional rope compute the bezier point yourself.
- **Where a new world overlay pass goes**: add a `DrawX` method in `GameRenderer.World.cs`
  (it already batches world-space primitives like `DrawProjectiles`/`DrawEffectsFiltered`
  inside the world `_g._spriteBatch.Begin(...)` block) and call it from the world section of
  `GameRenderer.Draw.cs`. Use `WorldToScreen` for every endpoint; do not draw in world units.

### `Necroking/Render/Flipbook.cs` — flipbook (sprite-sheet frame sequence)
What lives here: `class Flipbook` — loads a sprite-sheet texture (cols×rows, FPS) and
maps a frame index to a source `Rectangle`. `LoadFromDef(device, FlipbookDef)` builds one
from a registry def; `GetFrameRect(i)` returns the frame. A `flipbookKey` on an `Effect`
resolves to one of these.
Key members: `Load`, `LoadFromDef`, `GetFrameRect`, `Texture`, `Cols`/`Rows`/`TotalFrames`/`FPS`.
Look/edit here when: a flipbook plays the wrong frames/speed, or you're wiring up new
flipbook **art**. The flipbook **data** (id → sheet path, cols/rows/fps) is a `FlipbookDef`
in `Data/Registries/` (not yet documented) — that's where you register a new effect's art.

### `Necroking/Render/ReanimEffectSystem.cs` — composite reanimation "rise" effect
What lives here: a preset-driven, multi-part effect played at a grave on reanimate; it's
handle-based (returns an `FxInstanceId`) so an outline can attach to the spawning unit.
Look/edit here when: the reanimate/raise visual is wrong. Driven from `Game1.Spells.cs`
(`QueueReanimRise`/`TickPendingReanimRises`) with a preset id from `SpellRegistry`.

### Particle systems
- `Necroking/Render/BuffVisualSystem.cs` (`WPParticle`) — buff-aura particles.
- `Necroking/Render/WadingWakeSystem.cs` (`WakeParticle`) — water-wake particles behind
  units in water.

## Chain: how a visual effect reaches the screen
1. **Art/id** — a `FlipbookDef` (id → sheet) in `Data/Registries/` defines the flipbook.
2. **Spawn** — `_effectManager.SpawnSpellImpact(pos, scale, tint, flipbookID, …)` (or add a
   new `SpawnX` in `EffectManager`). Game1 wraps this in `SpawnFlipbookEffect`/`SpawnCastEffect`.
3. **Update** — `_effectManager.Update(dt)` each frame (from `Game1.Animation.cs`).
4. **Draw** — `Game1.Render.World.cs` `DrawEffectsFiltered` renders `_effectManager.Effects`.

## Related
- [game1-partials.md](game1-partials.md) — `_effectManager` field (`Game1.cs`), spawn
  helpers `SpawnFlipbookEffect`/`SpawnCastEffect` (`Game1.Spells.cs`), `SpawnImpactEffects`
  + effect drawing (`Game1.Render.World.cs`), and keyboard input (`Game1.cs` `Update`).
- `Game/` (not documented) — systems that *trigger* effects: `SpellEffectSystem`,
  `Projectile` (→ `SpawnImpactEffects`), `LightningSystem`, `PoisonCloudSystem`.
- `Data/Registries/` (not documented) — `FlipbookDef` registry (effect art/ids).
