# Rendering Pitfalls: C++ raylib vs C# MonoGame

Known differences in how the two engines handle rendering that can cause visual mismatches when porting.

## C++ raylib pitfalls

### 1. Batched SetShaderValue is silently ignored
**Severity: Critical**

raylib batches draw calls (DrawTriangle, DrawTexturePro, etc.) and only flushes them when certain state changes occur (BeginShaderMode, EndShaderMode, BeginTextureMode, EndTextureMode, BeginBlendMode, EndBlendMode, texture change).

`SetShaderValue()` does NOT flush the batch. If you call SetShaderValue between DrawTriangle calls, only the LAST value is used when the batch finally flushes. All triangles in the batch render with the same uniform value.

**Impact**: Any effect that varies a shader uniform per-draw-call (intensity, color, etc.) silently uses only the final value. The god ray had 16 sublayers with intensities from 0.5 to 4.9 but all rendered at 0.78.

**How to detect**: If a visual looks "correct" in C++ but the intensity/color values seem too high, the shader uniform might not be reaching individual draw calls.

**Workaround in C++**: Call `EndShaderMode()` + `BeginShaderMode()` between uniform changes to force a batch flush. Or use `rlDrawRenderBatchActive()`.

### 2. Blend mode also batched
**Severity: High**

Similar to SetShaderValue, if you change blend mode between batched draw calls without flushing, the blend mode active at flush time applies to everything. An effect drawn after `EndBlendMode()` might inherit the previous additive blend if no flush occurred between them.

**Impact**: The god ray in C++ likely rendered with BLEND_ADDITIVE inherited from a preceding effect, even though no explicit BeginBlendMode was called for it.

### 3. DrawTriangle uses shapes texture
DrawTriangle binds raylib's internal shapes texture (1x1 white) and sets texture coordinates from it. Custom fragment shaders that sample `texture0` will get white texels, which is usually fine but worth knowing — the texture IS being sampled.

### 4. Default vertex shader passes fragColor = vertexColor only
raylib's default vertex shader (used when LoadShader(NULL, "fragment.fs")) sets `fragColor = vertexColor`. It does NOT multiply by colDiffuse in the vertex shader. `colDiffuse` is set to (1,1,1,1) at draw time and is only used by the default fragment shader. Custom fragment shaders that don't reference colDiffuse won't see it.

## C# MonoGame pitfalls

### 1. RenderTargetUsage.DiscardContents is the default
**Severity: High**

MonoGame render targets default to `RenderTargetUsage.DiscardContents`. When you unbind a RT (SetRenderTarget(null)) and rebind it later, the GPU may discard its contents. This means:
- You CANNOT clear a RT, unbind it, rebind it, and expect the clear value to survive
- Always keep the RT bound between clear and draw operations
- Use `RenderTargetUsage.PreserveContents` if you need to unbind and rebind

**Impact**: Test code that clears in one call and draws in another will see garbage/black instead of the cleared value. The actual bloom pipeline avoids this by never unbinding between operations.

### 2. SpriteBatch flushes per-sublayer correctly (unlike raylib)
MonoGame's `DrawUserPrimitives` is an immediate draw call — no batching. Each `pass.Apply()` + `DrawUserPrimitives()` executes immediately with the current shader parameters. This means per-sublayer SetValue() calls actually work, unlike C++ raylib.

**Impact**: Effects ported from C++ that relied on the batching bug (uniform values being ignored) will look different — usually brighter or with more visible layer boundaries. The fix is to use flat/constant uniforms if the C++ version looked correct.

### 3. BlendState.Additive uses SRC_ALPHA, not ONE
MonoGame's `BlendState.Additive` is `(SourceAlpha, One)` not `(One, One)`. The source alpha DOES modulate the source RGB before adding to destination. This is the same as raylib's BLEND_ADDITIVE (`glBlendFunc(GL_SRC_ALPHA, GL_ONE)`).

### 4. Alpha accumulates in HDR render targets
With additive blend on HalfVector4 RTs, the alpha channel accumulates beyond 1.0 (e.g., after 3 additive passes: alpha = 1.25). Since Additive uses SRC_ALPHA as the source factor, this accumulated alpha amplifies subsequent passes that read from the RT. Both C++ and C# have this behavior — it's a GPU-level effect, not engine-specific.

### 5. Color struct ambiguity
`new Color(byte, byte, byte, byte)` and `new Color(int, int, int, int)` can cause ambiguity errors when mixing byte variables with integer literals. Always cast explicitly: `new Color((byte)255, (byte)255, (byte)255, scatterAlpha)`.

## General porting notes

### HDR intensity values from C++ are likely inflated
Any intensity/brightness values tuned in C++ may be compensating for the batching bug. When porting to C# where uniforms are applied correctly, these values need to be reduced significantly (often 5-10x).

### Always verify with pixel readback, not just screenshots
Screenshots show the final composited output which makes it hard to isolate differences. Use `RT.GetData<HalfVector4>()` (C#) or `glReadPixels` with `GL_FLOAT` (C++) to compare exact pixel values at each pipeline stage.

### Test scenarios exist for verification
- `blend_test` — GPU blend state verification (additive, alpha, opaque)
- `godray_render_test` — God ray pixel-level output comparison
Both exist in C# (MonoGame) and C++ (raylib) projects with identical parameters.
