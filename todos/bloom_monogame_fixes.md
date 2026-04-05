# Bloom & God Ray Findings

## Root Cause Found: C++ raylib batched rendering bug

The C++ god ray renders all triangle layers in a single batch. `SetShaderValue(hdrIntensity)` is called per sublayer, but raylib only applies the LAST value when the batch flushes. This means ALL god ray triangles use the aura intensity (~0.78) instead of per-sublayer values (up to ~4.89).

The C# GodRayRenderer correctly flushes triangles per sublayer (`FlushTriangles()` before each `SetShaderValue`), so each sublayer gets its intended intensity. This produces ~5x brighter output.

**Verified with pixel-level tests**: Setting C# to fixed intensity=0.782 produces pixel-identical output to C++.

## Action Items

1. **Tune HDR intensity values** — All intensity values in spells.json were cranked up to compensate for C++ ignoring them. They need to come down for C# which actually applies them. Try core ~1.5-2.0, glow ~2.0-3.0.

2. **Check ALL HDR intensity usage** — Any other effect using `HdrIntensity` shader + `SetShaderValue` pattern in C++ may have the same batching issue. Check: lightning bolts, buff visuals, spell effects with HDR.

## Bloom Pipeline — Verified Correct

The bloom pipeline itself is confirmed GPU-identical between C++ and C#:
- Blend states: identical (Additive = SRC_ALPHA, ONE in both)
- HDR texture formats: identical (RGBA16F / HalfVector4)
- Downsample/upsample pixel values: pixel-identical
- Pixel bleed patterns: pixel-identical
- BloomSampler binding: working correctly

## Cleanup Done
- Removed OverrideIntensity test hack from GodRayRenderer
- Dead code in Bloom.cs remains (unused saturation params, disabled blur block)
