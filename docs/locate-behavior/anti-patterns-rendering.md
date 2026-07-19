# Rendering Anti-Patterns
*Rendering-specific anti patterns to avoid and principles to follow. The generic
(everywhere) anti patterns live in [anti-patterns.md](anti-patterns.md); this file is the
draw-layer counterpart. Same discipline: egregious ones get refactored on sight and told to
the main claude; regular ones get logged in [anti-patterns-list.md](anti-patterns-list.md)
and raised as fix candidates when relevant to the caller's request.*

Each principle below was paid for by a real bug (or a whole campaign of them). The deep
reference for each lives in a subsystem doc — [render.md](render.md), [camera.md](camera.md),
[../vfx-zoom-audit.md](../vfx-zoom-audit.md), [../known-platform-bugs.md](../known-platform-bugs.md),
[../../todos/rendering_pitfalls.md](../../todos/rendering_pitfalls.md). This file is the
"what not to do" index that points at them.

---

## Principle: every VFX quantity is a WORLD quantity; zoom only changes pixels-per-world-unit

The realism model (project decision 2026-07-16, full protocol in
[../vfx-zoom-audit.md](../vfx-zoom-audit.md)): a beam has a *world* width, a puff a *world*
size, a light a fixed *world* distance of air it lights. Zoom changes only how many pixels
that world occupies — never the world quantity, never the intensity. This was the single
most expensive rendering campaign (~20 commits: rain, health bars, damage numbers, beams,
drains, bloom, god ray) and it recurs, so it leads the list. Conventions: [camera.md](camera.md).

### **Egregious Anti Pattern**: a screen-space pixel constant baked into world geometry
A hardcoded px literal (width, radius, offset, drift speed, anchor) in a `Render/*` draw path
or a `data/spells.json` visual field that does NOT scale with zoom. **Data-authored px values
are ALWAYS "at zoom 32"** — scale them by `Zoom/32` (linear `FxScale()` for world-tracking
geometry, `SoftZoomScale(refZoom) = sqrt(Zoom/refZoom)` for legibility-softened pixel effects
like rain/damage-numbers). The audit unit is **every constant, not every system** — the real
misses were never a missing `*Zoom` on a size, they were **offsets, anchors, and directions**:
the god ray's screen-space convergence point, the drain wave's hardcoded 5px amplitude, damage
numbers rising in world units under a fixed text size. For each literal answer: unit
(px@32 / world / screen-fraction / UV)? anchor (world point / sprite-rig / screen)? motion
(world units/sec on screen)? Evidence: `8eb2680` (the pass), `28bfbed` ("13 violations from
the constants-level audit"), `d4feb5c` ("anchor-convention trio"), `5519d18`/`3959b54` (god-ray
sky anchor in world units).

### **Anti Pattern**: two scaling policies inside ONE effect
Every dimension of a single VFX (widths, arc heights, wave amplitudes, cloud sizes, drift
speeds, anchor offsets, scroll speeds) must scale the SAME way. The drain shipped with linear
structure + sqrt-damped clouds "to look better" and the parts visibly **detached** across
zoom. **One effect = one policy.** A `*fxScale` on the width while the anchor offset stays
screen-space is exactly how "scales correctly" and "converges to a wandering point" coexist in
the same effect.

### **Anti Pattern**: a "middle-ground" zoom curve papering over a wrong model
Every compromise tried during the campaign — sqrt damping, scale-floor clamps, per-octave
intensity compensation, one-sided biases — later proved to be a wrong model wearing a tuning
knob, and each cost a full report→diagnose→rip-out cycle. **If the correct model seems to need
a compromise, the architecture is wrong — fix that.** (The bloom needed virtual-resolution
rendering, not blend weights: `dc7821e`/`0e58ee3`.) The only sanctioned deviation is a physical
limit (e.g. a blur-resolution floor), and its fallback must ALSO be derived from the model
(energy dimming), not invented. HDR trap that invalidated four successive bloom fixes: with
intensities ~10× over the tonemap knee, blend *weights* barely move a glow's visible edge —
only *footprint* changes do, so any weight-interpolation scheme reads as thresholds.

### **Anti Pattern**: wrong height convention (sprite floats off its anchor as you zoom)
Three height conventions exist ([camera.md](camera.md) header): physical heights (jumps,
projectile altitude, corpse Z) → `WorldToScreen(pos, worldHeight)` (lift = `h*Zoom*YRatio`);
sprite-rig heights (weapon attach, `EffectSpawnHeight`, casting glow, zap/beam start anchors) →
`WorldToScreenPx(pos, height*Zoom)` (NO YRatio — sprites draw `SpriteWorldHeight*Scale*Zoom`
tall). Using `WorldToScreen` for a sprite-rig anchor foreshortens it to half height and it
drifts off the rig as zoom changes. Symptom "thing floats above/below its anchor as I zoom" =
this. See also the `DamageNumber.Height` trap in [render.md](render.md) "Floating text".

**Do this instead:** run the New-VFX checklist in [../vfx-zoom-audit.md](../vfx-zoom-audit.md)
before committing ANY new or changed visual — classify every constant, one policy per effect,
paused screenshots at zoom 8/32/128 plus a motion pair if anything moves spatially.

---

## Principle: draw colors are STRAIGHT alpha, drawn through a `SpriteScope`

Textures are premultiplied at load and scene batches blend with `AlphaBlend`, but every
call-site color is straight alpha since 2026-07-04 — the draw layer converts once per material
(`Material.Tint`, consulting `Materials.Open`). This is the most *re-derived* rendering bug in
the repo: fix commits span months (`d626422` HdrSprite/WeatherFog/Wading 2.5× dark + UIGradient
fringes, `fa4098a` rain, `e457c47` glow rects, `40329a3`/`fddeb57` hover tint, `ab34130`), and
the entire SpriteScope-lockdown refactor (`5abc6e3`) exists **only because MinimapHUD
re-derived the premult bug 12 days after the last migration** by holding its own `SpriteBatch`.
Full map: [render.md](render.md) "Premultiplied alpha"; canonical usage:
[../standard_patterns.md](../standard_patterns.md) "Draw colors".

### **Anti Pattern**: hand-encoding a draw tint
Calling `Color.FromNonPremultiplied`, `ColorUtils.Premultiply`, or scaling RGB by alpha
(`color * t`) for a **draw tint** — all now double-convert (dimmer, not washed out). Author
`new Color(r,g,b,128)` as "that hue at 50%"; fade with `ColorUtils.Fade(c, t)` (scales A only).
The only sanctioned hand-encodings are the native-encoding islands ([render.md](render.md)
lists them: `BuffVisualSystem.EncodeColor` and the A=0 additive trick, HDR vertex packs via
`Core/HdrColor.cs`, `SetData` texel buffers which stay premult) — those go through `scope.Batch`
deliberately.

### **Egregious Anti Pattern**: a raw `SpriteBatch` circulating outside render plumbing
Storing, receiving (`Init(GraphicsDevice, SpriteBatch, …)`), or exposing a raw `SpriteBatch` in
any UI/editor/game class. `Game1._spriteBatch` is private since the lockdown; draw through
`SpriteScope` — immediate-mode code uses the canonical accessor
`private SpriteScope Scope => Game1.Instance.Scope;` (or `_g.Scope`). It must be a **property,
never a cached field** — the resume material is computed from `Materials.Open` at access time;
a stored scope resumes into the wrong material. For a `PushMaterial/PopMaterial` or
`Suspend/Resume` cycle, capture ONE scope in a local for the whole cycle. Gate:
`python tools/check_spritebatch_scope.py` flags any raw `SpriteBatch` token outside the
allowlist — run it after touching draw code.

---

## Principle: a RenderTarget's contents do NOT survive an unbind (MonoGame `DiscardContents`)

MonoGame RTs default to `RenderTargetUsage.DiscardContents`; `SetRenderTarget` may clear the
target to black on bind. You CANNOT clear a RT, unbind it, rebind it, and expect the clear (or
prior content) to survive. Reference: [../../todos/rendering_pitfalls.md](../../todos/rendering_pitfalls.md)
MonoGame #1.

### **Anti Pattern**: clear/accumulate on a RT across a rebind assuming contents persist
`5fada53`: the bloom upsample additively blended each mip onto its downsampled content, but
rebinding the mip **wiped** it — so only the deepest mip survived and *raising Iterations
dimmed the bloom* (5→6 made it vanish) while Scatter acted as a brightness knob. **Do this
instead:** mark any target you re-bind mid-composite `RenderTargetUsage.PreserveContents`, or
never unbind between clear and draw. `BloomRenderer` (`Render/Bloom.cs`) is the worked example
of the preserved-chain; its `_haloScratch` is the scratch-RT construction for true lerp
semantics.

---

## Principle: set EVERY shader uniform on EVERY draw (MGFX-on-GL defaults every uniform to 0)

MonoGame's MGFX-on-OpenGL path zero-initialises every shader uniform per draw; a uniform you
set once (or last frame) reads 0 now. Reference: [render.md](render.md) "Adding a new .fx
shader" + `memory/mgfx_shader_gotchas.md`.

### **Anti Pattern**: setting a shader parameter once and relying on it later
Set-once init of a shader param, or assuming a param set before a batch survives to the draw.
Every consumer sets all uniforms every draw. Corollary when porting from C++ raylib: raylib
*batched* `SetShaderValue` so only the LAST value survived a flush (the god ray's 16 sublayers
all rendered at one intensity — [../../todos/rendering_pitfalls.md](../../todos/rendering_pitfalls.md)
C++ #1). MonoGame's `DrawUserPrimitives` is immediate, so per-draw params actually apply —
**don't port the C++ "use a flat uniform because batching ate it" workaround**; and C++-tuned
HDR intensity values in `data/spells.json` are inflated (retune DOWN, ~5–10×), not compensated
for with clamps.

---

## Principle: things that must interleave/occlude share ONE sort-key & depth formula

The Y-sort queue (`WorldLayer` bands + packed `SortKey`, [render.md](render.md) "Submission")
is the only transparency-ordering mechanism. Two draw families that need to sort *against each
other* must compute their sort key the same way — this is single-source-of-truth applied to
depth.

### **Anti Pattern**: two co-sorted families using different SortY / depth formulas
`6957f1d`: reanim dust used `SortY = world.Y + WorldSize*0.5` while additive clouds used raw
`world.Y`; the ~1-unit bias exceeded the per-puff Y-scatter, so **all dust sorted in front of
all clouds** instead of interleaving by scatter. Fixed to one shared
`world.Y + FrontSortBias*scale` key. Same class: `97ccb14` (beam-sheath mist depth). **Do this
instead:** one shared sort-key helper for families that intermix.

### **Anti Pattern**: a DepthRead material that doesn't set `layerDepth` via `FogDepthForY`
A DepthRead consumer (fog wisps, depth-tested reanim particles) MUST set `layerDepth` via
`FogDepthForY(worldY, cameraY)` or the depth test against the unit-silhouette stamps is
garbage (stamps map larger Y → smaller depth). And the occluder stamps only exist when
`Performance.DepthSortedFog` OR a ground-fog bank is active — a new depth-testing consumer must
OR its own "is active" flag into the `FogDepthOccluders` gate ([render.md](render.md) "Where a
new full-res DepthRead additive pass goes").

### **Anti Pattern**: drawing into the wrong layer / after the pass that should contain it
`0db1831`: map-editor markers drew after the HUD and so rendered ABOVE other HUD elements;
the fix gave them their own `MapEditorMarkersLayer` seat in the router. Put a draw in the
correct `WorldLayer` band / `UILayer` seat rather than emitting it late and relying on call
order (the UIRouter makes "drawn on top ⇔ clicked first" structural — see
[../standard_patterns.md](../standard_patterns.md) "UI layers & click routing").

---

## Platform-bug reflexes (not our bug, but ours to route around)

- **Never use `Blend.InverseBlendFactor`.** MonoGame/DX corrupts the destination at small
  factors (`f≈0.01` should keep ~99% of dst but guts it), which reads as a *threshold* artifact
  in whatever consumes it (bloom collapsing in a narrow zoom band past each mip octave). Build
  any fractional mix from the weighted-additive shape `src*BlendFactor + dst*One`, accumulating
  into a cleared scratch RT when true lerp semantics are needed (`BloomRenderer.GetWeightBlend`
  / `_haloScratch`). Full write-up + how it was bisected: `4890aaa`,
  [../known-platform-bugs.md](../known-platform-bugs.md).
- **Round text positions to integer pixels.** SpriteBatch runs `SamplerState.PointClamp`, so
  `DrawString` at a sub-pixel position aliases. `EditorBase.DrawText` rounds internally, but any
  direct `DrawString` (e.g. `Game1.cs`) and every `MeasureString`-centering (the `/2` yields
  floats) must cast to `int` first — `new Vector2((int)x, (int)y)`. Standing rule in
  [CLAUDE.md](../../CLAUDE.md) "UI Text Rendering".
- **When observation contradicts your math, bisect the pipeline empirically** (toggle passes,
  pin inputs, screenshot between stages) instead of theorizing. Two identical-math frames
  rendering differently means a platform lie, not a subtle equation — that's how
  `InverseBlendFactor` was caught ([../vfx-zoom-audit.md](../vfx-zoom-audit.md) step 8).

---

## Related
- [anti-patterns.md](anti-patterns.md) — the generic (everywhere) anti patterns + principles.
- [anti-patterns-list.md](anti-patterns-list.md) — known live instances in the code.
- [render.md](render.md) — draw pipeline, premult map, sort queue, bloom internals, effects.
- [camera.md](camera.md) — the zoom census: every consumer and which are deliberately screen-space.
- [../vfx-zoom-audit.md](../vfx-zoom-audit.md) — the zoom-correctness protocol + New-VFX checklist.
- [../known-platform-bugs.md](../known-platform-bugs.md) — framework/OS bugs and workarounds.
- [../../todos/rendering_pitfalls.md](../../todos/rendering_pitfalls.md) — C++ raylib → MonoGame porting gotchas.
