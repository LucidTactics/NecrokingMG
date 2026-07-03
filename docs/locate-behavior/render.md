# Render/ — rendering subsystems

> **Coverage: PARTIAL.** Documented: the **draw-dispatch pipeline** (top-level `Draw`,
> batch/shader/state model — see next section) and the **visual-effect / flipbook**
> systems. Still undocumented: sprite atlases & frames internals, shadows, `FontManager`,
> `HUDRenderer`, the spellbar widget, `RuntimeWidgetRenderer`. Extend this file when you touch those.

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
