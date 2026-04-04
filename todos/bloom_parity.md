# Bloom Pipeline C++ Parity

## Status: In Progress

The bloom pipeline was overhauled to match C++ structurally (soft knee extract, bicubic upsample, additive combine). God rays render with the same code as C++ god_ray.h. However, the visual output still differs — C# god rays are brighter/whiter in the center with less bloom spread than C++.

## What's Done
- BloomExtract.fx: soft knee matching C++ bloom_prefilter.fs
- BloomCombine.fx: simple additive matching C++ bloom_composite.fs
- BloomUpsampleBicubic.fx: Catmull-Rom matching C++ bloom_upsample_bicubic.fs
- Bloom.cs: bicubic upsample wired into mip chain, extra Gaussian blur removed
- GodRayRenderer.cs: triangle-based trapezoids, HDR intensity shader per sublayer
- Settings: copied C++ bloom values (threshold 1.21, softKnee 0.25, intensity 5.48)

## What Still Differs
- God ray center is whiter than C++ — C++ shows warm golden center, C# shows pure white
- Bloom glow spread is less prominent in C# — the soft halo around the beam is narrower
- Two overlapping god rays don't blend as smoothly as C++

## Investigation Notes
- All shader math, blend modes, HDR RT format, and bloom settings verified identical
- Bicubic upsample shader loads and is applied during upsample chain
- The bloom combine is additive (`scene + bloom * intensity`) matching C++ exactly
- Possible causes of remaining difference:
  - HLSL bicubic shader may produce slightly different filtering than GLSL (frac vs fract, coord math)
  - The disabled Gaussian blur pass (step 3.5 in Bloom.cs, `if (false &&`) may have been providing needed softness
  - Screen resolution differences affect mip chain sizes and bloom spread
  - C++ weather post-processing (fog, brightness, tint, vignette) applied after bloom may subtly affect appearance

## How to Debug
1. Add bloom debug mode to visualize each mip level side-by-side (C# vs C++ screenshots)
2. Compare bloom extract output at a known pixel to verify soft knee produces same values
3. Test with the extra Gaussian blur re-enabled to see if it helps
4. Try increasing bloom iterations or scatter to get more spread


## NEW FINDINGS
Try to fix things like below and see if it helps.

- HdrColor.ToScaledColor shouldn't be used except to preview the color in color picker, it being used Everywere is a big reason blending cannot be done well.
- FlipbookRef settings like BlendMode isn't passed along. Need to check that all such values are actually used properly. This is a very big change.