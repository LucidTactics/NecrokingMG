# Bloom Pipeline — post-overhaul state & remaining tuning

## Status: pipeline overhauled 2026-07-11 (C++ parity goal superseded)

The C++-parity chase is over: the pipeline now intentionally goes *beyond* the C++
implementation (Jimenez SIGGRAPH-2014 downsampling + a shoulder tonemap), which fixes
the root cause the parity todo was circling — overbright cores hard-clipping to flat
white at the back-buffer write.

## What changed (see docs/locate-behavior/render.md "Bloom & HDR" for detail)
- BloomExtract.fx: prefilter is now a 13-tap downsample with Karis-weighted group
  averages (kills fireflies/shimmer) + the same soft-knee threshold.
- BloomDownsample.fx (new): 13-tap Jimenez filter down the mip chain (was plain
  bilinear blits).
- The extra 15-tap GaussianBlur pass on mip[0] was removed (was a spread band-aid;
  spread now comes from iterations/scatter).
- BloomCombine.fx: after `base + bloom*intensity`, an optional **shoulder tonemap** —
  identity below `tonemapShoulder` (default 0.9, scene look preserved), extended-Reinhard
  rolloff hitting pure white at `tonemapWhitePoint` (default 6). `tonemapDesaturate`
  (default 0.4) blends hue-preserving (0 = glow keeps its color at any brightness) vs
  per-channel (1 = hot cores bleach white like film).
- Settings: `bloom.tonemap/tonemapShoulder/tonemapWhitePoint/tonemapDesaturate` in
  GameSettings + sliders in the Settings window Bloom tab.

## Reference screenshots (before/after, same scenes)
`bin/Devbuild/log/screenshots/before_*.png` and `after2_*.png`
(godmode buff outline, sky_lightning bolt, raise_zombie cloud); archived copies of the
"before" set in the session scratchpad `bloom_refs/`. Result: godmode pulse peak now
warm gold (was bleached white); sky-lightning bolt shows branch/filament structure and
blue tint (was flat white column); raise cloud unchanged/slightly richer.

## What's left
- **Retune per-spell intensities** in spells.json — values were tuned against the C++
  build that ignored per-sublayer HDR intensities (see todos/rendering_pitfalls.md), so
  many read hot. With the tonemap they no longer blow out, but cores sit deep in the
  rolloff (e.g. sky_lightning strike colors at intensity 15 vs whitePoint 6). Retune
  down rather than compensating with clamps.
- **Per-spell intensity clamp** — user deferred; re-evaluate after retuning. Preferred
  approach if needed: Max-blend for a spell's own layer group (clamps within-spell
  stacking, stays additive vs the rest of the scene).
- **BuffVisualSystem is still LDR** (CPU-clamped to 255, raw scene batch) — buff auras
  can't overflow into bloom the way HdrSprite spell effects do. Migrate onto the HDR
  additive material if buffs should glow like spells.
