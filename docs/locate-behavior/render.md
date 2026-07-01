# Render/ — rendering subsystems

> **Coverage: PARTIAL.** Only the **visual-effect / flipbook** systems are documented
> here so far (researched for "spawn a visual effect at a world location"). The rest of
> `Render/` — sprite atlases & frames, the ground shader, bloom, shadows, `FontManager`,
> `HUDRenderer`, the spellbar widget, `RuntimeWidgetRenderer` — is **not yet documented**.
> Extend this file when you touch those.

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
