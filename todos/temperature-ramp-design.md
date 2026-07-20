# Temperature-ramp recoloring for HDR flipbooks — design of record

Status: being implemented (this session). Supersedes the interim
`temperatureGradient`/`temperatureMax` fields that briefly existed on
FlipbookRef (never shipped in data).

## Concept

`-temperature` EXR flipbooks are grayscale heat fields (linear half-float,
values 0..~37). Rendering = heat → chroma via a 1D ramp LUT, heat → luminance
via the scene-unit encode (TempGradient.fx, already landed). Recoloring is
done by running the **color harmonizer over the 256x1 ramp**, not over the
sheet — instant, LDR-safe (heat carries all HDR), and reuses the exact
HarmonizeSettings schema/UI used by env objects and UI widgets.

## Data model (single source of truth)

`FlipbookRef.TemperatureRamp` (nullable nested object, json `temperatureRamp`;
presence = feature on; only affects `Flipbook.IsHdr` sheets drawn additive):

```json
"temperatureRamp": {
  "max": 2.0,                 // heat value mapped to ramp top (per-sheet knob)
  "harmonize": { ... },       // optional HarmonizeSettings (canonical converter)
  "stops": [ {"t":0,"r":..}]  // ADVANCED: override the built-in fire base ramp
}
```

- Base ramp: one built-in "fire" stop list (GradientLut.FireStops). `stops`
  overrides it for exotic multi-band looks; normally null.
- No separate ramps registry — the harmonizer provides the variation. Revisit
  only if authored base ramps proliferate.

## Pipeline

1. `GradientLut.Get(device, ramp)` → 256x1 Color LUT: bake stops (ramp.Stops ??
   FireStops), then `ColorHarmonizer.TransformPixels` if `Harmonize.HasEffect`.
   Memoized by full recipe key; process-lifetime cache (LUTs are ~1KB).
2. Draw sites (projectiles via per-frame SpellDef lookup, EffectManager
   one-shots via an `Effect.TempRamp` reference copied at spawn) pick material:
   `TempGradient` (ramp + IsHdr + additive) > `HdrTexAdditive/Alpha` (IsHdr) >
   pass default. Params (TempMax + LUT on slot 1) set before PushMaterial —
   MorphSdf pattern, perDrawParams material.

## Editor UI

`DrawTemperatureRampSection` appended to every FlipbookRef section:
- manual `DrawFlipbookRefSection` calls it directly;
- reflection sections get it via a FlipbookRef-specific hook on
  ReflectionPropertyRenderer (invoked after DrawNestedObject).
Rows (only when the selected flipbook IsHdr): enable checkbox (creates/clears
the ramp object) · Max float · base+result ramp preview strips (draw the baked
LUT textures) · harmonize target color + hue/sat/lum strengths + HCL toggle.
Spell preview pane shows changes live (LUT rebake is 256 px).

## Non-goals (now)

- Buff visuals (four separate def classes) — extend later if wanted.
- Ramp in texture-browser/manager previews.
- Ramp-over-lifetime animation (Fallout-4 style cooling) — natural extension:
  add a second LUT row / ramp list later.
- Gradient/outline extras of HarmonizeSettings are not exposed in the ramp UI
  (meaningless for a 1px strip); recipes keep them ignored.
