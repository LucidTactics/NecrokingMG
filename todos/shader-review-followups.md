# Shader review — deferred follow-ups (2026-07-03)

Context: a 7-agent deep review of all 19 shaders in `resources/` found ~15 real issues;
all bugs and cheap fixes were applied the same day (see git log). These are the items
consciously deferred — each needs either a product decision, visual tuning, or a
measurement before acting. Delete entries as they're resolved or rejected.

## Done 2026-07-03 (second reshape pass)

- ~~Scoped batch-suspend helper~~ → `Necroking/Render/EffectBatch.cs`: canonical
  Scene/HUD pass states + BeginEffect/EndEffectResume{Scene,Hud}; all 9 game-side
  sandwich sites converted (UIShaders intentionally left on its injected-restore design).
- ~~Hoist constant effect parameters~~ → Wading + MorphSDF look constants set once at
  load (Game1); Bloom Gaussian kernels precomputed in CreateTargets. (EffectParameter
  ref caching deliberately skipped — would add a second feeding idiom for marginal gain.)
- ~~MagicGlyphRenderer per-glyph batch flips~~ → one Immediate batch around the loop.
- ~~MagicCircle/WeatherFog/GroundShader-foam tuning constants~~ → named `static const`
  blocks, values transcribed exactly (no look change).
- ~~GodRayRenderer ToArray() per flush~~ → pooled scratch array.

## Worth doing, needs its own session

- **MagicCircle pixel-space AA rework** — all edge widths are hardcoded in normalized
  UV (`Ring(dist, 0.90, 0.076)` etc.), so lines blur when the quad is large and vanish
  when small. Rework to pixel-space widths (like UICircleEffect's `EdgeAA`) with
  drive-game before/after screenshots at several zooms. Highest player-visible payoff
  of the deferred list. Related: the activation flare is LDR-capped (`alpha = saturate`
  then `color * alpha` with color ≤ 1) — the data requests ×4–6 intensity that renders
  as ×1.5; if a real flare is wanted, scale `color` above 1 before output.

- **Fog-of-war full-RT passes every frame** (`FogOfWarSystem.cs` Update): smooth-lerp,
  explored-max, pack-R, pack-G each cover up to 2048² every frame even when vision is
  static (~17M px writes/frame). Cheap win: run max+pack at the circle redraw's 2-frame
  cadence, or skip when converged (frames-since-vision-changed > FadeTime/dt). Measure
  on integrated GPU first.

- **GodRayRenderer per-sublayer flush** — `FlushTriangles` allocates `_triVerts.ToArray()`
  and issues a draw per sublayer (~20/ray) though Intensity is constant per ray. One
  flush per ray collapses it. Minor (god rays are rare); do if profiling shows it.

## Latent traps — fine today, will bite a future caller

- **UIShaders.cs hardcoded batch resume** — every Draw* method resumes with
  `Begin(Deferred, _defaultBlend, _defaultSampler, null, null)`, discarding any caller
  transform/scissor/blend. All current call sites match the default; the first use
  inside a scissored scroll panel or transformed batch will misplace the quad and drop
  the scissor. Fix when UI shaders get a second production consumer: capture+restore
  the caller's batch state (or take it as a parameter).
- **FogSmooth 8-bit stall at uncapped FPS** — at ~240fps the temporal lerp rate is
  small enough that the 8-bit RT quantizes to a stall, leaving a ~14% grey veil over
  visible areas. Irrelevant at fixed-60; fix (16-bit RT or floor the rate so
  `src*Rate >= 1/255`) before any frame-rate unlock or uncapped benchmark.
- **Fog-of-war doesn't cover beyond the world rect** — off-map area gets no fog when
  zoomed out. Fine while nothing is drawn out there.
- **Wading units stamp their full silhouette as fog occluders** — submerged legs
  (alpha-faded by the wading shader) still occlude reanim smoke. Corner case
  (fog + shoreline overlap).
- **UICircleEffect "GlowRadius == Radius disables glow" is not quite true** when
  GlowColor.a > 0 — a faint sub-pixel rim survives in the AA band. All current
  disable-callers also pass transparent glow, so invisible today.
- **MorphSDF t=0 re-threshold pop on soft-edged art** — the morph re-thresholds the
  silhouette at alpha≥128; hard-edged pixel art is unaffected, but wispy/AA'd death
  frames would pop when the morph engages.

## Decisions rejected during the review (don't redo)

- Shared `common.fxh` include — touches all 19 files + the MGCB build surface for
  ~8 duplicated header lines; sync-comments on the duplicated noise functions cover it.
- BloomCombine saturation params — deleted instead of implemented; the shader's intent
  is the plain C++ additive port.
- Converting OutlineFlat to premultiplied — its straight-alpha output is correct for
  its NonPremultiplied/Additive batches; a header warning comment is the whole fix.
- SDF über-shader unifying UI/VFX shaders — framework, not utility (CLAUDE.md
  consolidation rule: structural variance).
- Gamma-space bloom — matches the C++ original; linearizing is a look change, decide
  deliberately if ever.
- Karis-weighted first bloom downsample (anti-firefly) — not visibly needed for this
  art style; revisit only if bright HDR cores shimmer under camera motion.
- WeatherFog neutralized Brightness/Tint blocks — pinned neutral by C# (applied
  pre-bloom as ambient instead); kept for the weather editor to re-enable.
