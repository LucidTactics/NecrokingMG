using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Graphics.PackedVector;
using Necroking.Core;
using Necroking.GameSystems;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// GPU blend state verification: renders known colors with known blend states
/// to an HDR render target, reads back pixels, and compares against expected values.
/// Validates our assumptions about BlendState.Additive, NonPremultiplied, and Opaque.
/// </summary>
public class BlendTestScenario : ScenarioBase
{
    public override string Name => "blend_test";

    private bool _complete;
    private bool _testsRan;
    private int _pass, _fail;

    // Set by Game1 before first tick
    public GraphicsDevice? Device;
    public SpriteBatch? Batch;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Blend State Verification ===");
        DebugLog.Log(ScenarioLog, "Testing MonoGame blend states against expected GPU math");
    }

    public override void OnTick(Simulation sim, float dt)
    {
        if (_complete) return;

        if (!_testsRan && Device != null && Batch != null)
        {
            RunAllTests();
            _testsRan = true;
            _complete = true;
        }
    }

    private void RunAllTests()
    {
        // Use PreserveContents so RT data survives across SetRenderTarget calls
        const int sz = 2;
        var rt = new RenderTarget2D(Device!, sz, sz, false,
            SurfaceFormat.HalfVector4, DepthFormat.None,
            0, RenderTargetUsage.PreserveContents);
        var rt2 = new RenderTarget2D(Device!, sz, sz, false,
            SurfaceFormat.HalfVector4, DepthFormat.None,
            0, RenderTargetUsage.PreserveContents);

        // 1x1 white texture
        var white = new Texture2D(Device!, 1, 1);
        white.SetData(new[] { Color.White });
        var fullRect = new Rectangle(0, 0, sz, sz);

        DebugLog.Log(ScenarioLog, "");
        DebugLog.Log(ScenarioLog, "--- Test 1: Additive + alpha tint (bloom scatter) ---");
        TestAdditiveAlphaTint(rt, white, fullRect);

        DebugLog.Log(ScenarioLog, "");
        DebugLog.Log(ScenarioLog, "--- Test 2: Additive + RGB tint (scatter alt) ---");
        TestAdditiveRgbTint(rt, white, fullRect);

        DebugLog.Log(ScenarioLog, "");
        DebugLog.Log(ScenarioLog, "--- Test 3: NonPremultiplied (god ray blend) ---");
        TestNonPremultiplied(rt, white, fullRect);

        DebugLog.Log(ScenarioLog, "");
        DebugLog.Log(ScenarioLog, "--- Test 4: Opaque blend ---");
        TestOpaque(rt, white, fullRect);

        DebugLog.Log(ScenarioLog, "");
        DebugLog.Log(ScenarioLog, "--- Test 5: Multiple additive passes ---");
        TestMultipleAdditive(rt, white, fullRect);

        DebugLog.Log(ScenarioLog, "");
        DebugLog.Log(ScenarioLog, "--- Test 6: RT-to-RT additive (bloom upsample pattern) ---");
        TestRtToRtAdditive(rt, rt2, fullRect);

        DebugLog.Log(ScenarioLog, "");
        DebugLog.Log(ScenarioLog, "--- Test 7: HDR accumulation ---");
        TestHdrAccumulation(rt, white, fullRect);

        DebugLog.Log(ScenarioLog, "");
        DebugLog.Log(ScenarioLog, "--- Test 8: HDR source texture (bloom-like downsample) ---");
        TestHdrSourceTexture(rt, rt2, fullRect);

        DebugLog.Log(ScenarioLog, "");
        DebugLog.Log(ScenarioLog, "--- Test 9: HDR additive with HDR source (bloom upsample) ---");
        TestHdrAdditiveFromHdr(rt, rt2, fullRect);

        rt.Dispose();
        rt2.Dispose();
        white.Dispose();

        DebugLog.Log(ScenarioLog, "");
        DebugLog.Log(ScenarioLog, "--- Test 10: Pixel bleed (downsample + upsample) ---");
        TestPixelBleed();

        DebugLog.Log(ScenarioLog, "");
        DebugLog.Log(ScenarioLog, $"=== Results: {_pass} passed, {_fail} failed ===");
    }

    // --- Test implementations ---

    private void TestAdditiveAlphaTint(RenderTarget2D rt, Texture2D white, Rectangle rect)
    {
        // Clear to (0.5, 0.5, 0.5, 1.0), draw white with Additive + tint alpha=128
        // Sprite shader: output = tex * vertexColor = (1,1,1,1) * (1,1,1,0.502) = (1,1,1,0.502)
        // Additive = SRC_ALPHA, ONE:
        //   result.rgb = src.rgb * src.a + dst.rgb = 1.0 * 0.502 + 0.5 = 1.002
        //   result.a   = src.a * src.a + dst.a = 0.502 * 0.502 + 1.0 = 1.252
        Device!.SetRenderTarget(rt);
        Device.Clear(new Color(0.5f, 0.5f, 0.5f, 1.0f));
        Batch!.Begin(SpriteSortMode.Immediate, BlendState.Additive, SamplerState.PointClamp);
        Batch.Draw(white, rect, new Color(255, 255, 255, 128));
        Batch.End();
        Device.SetRenderTarget(null);

        var px = ReadPixel(rt);
        DebugLog.Log(ScenarioLog, $"  Tint: Color(255,255,255,128)  Bg: (0.5, 0.5, 0.5, 1.0)");
        DebugLog.Log(ScenarioLog, $"  Result: R={px.X:F4} G={px.Y:F4} B={px.Z:F4} A={px.W:F4}");

        LogCheck(ApproxEq(px.X, 1.002f, 0.03f), $"R≈1.0 => SRC_ALPHA blend (src*0.502 + dst)");
        LogCheck(ApproxEq(px.W, 1.252f, 0.03f), $"A≈1.25 (alpha accumulates)");
    }

    private void TestAdditiveRgbTint(RenderTarget2D rt, Texture2D white, Rectangle rect)
    {
        // Same but scatter in RGB: tint = Color(128,128,128,255)
        // Sprite shader: output = tex * vertexColor = (1,1,1,1) * (0.502,0.502,0.502,1) = (0.502,0.502,0.502,1)
        // Additive = SRC_ALPHA, ONE:
        //   result.rgb = 0.502 * 1.0 + 0.5 = 1.002
        //   result.a   = 1.0 * 1.0 + 1.0 = 2.0
        Device!.SetRenderTarget(rt);
        Device.Clear(new Color(0.5f, 0.5f, 0.5f, 1.0f));
        Batch!.Begin(SpriteSortMode.Immediate, BlendState.Additive, SamplerState.PointClamp);
        Batch.Draw(white, rect, new Color(128, 128, 128, 255));
        Batch.End();
        Device.SetRenderTarget(null);

        var px = ReadPixel(rt);
        DebugLog.Log(ScenarioLog, $"  Tint: Color(128,128,128,255)  Bg: (0.5, 0.5, 0.5, 1.0)");
        DebugLog.Log(ScenarioLog, $"  Result: R={px.X:F4} G={px.Y:F4} B={px.Z:F4} A={px.W:F4}");

        LogCheck(ApproxEq(px.X, 1.002f, 0.03f), $"R≈1.0 (same RGB result as alpha tint)");
        LogCheck(ApproxEq(px.W, 2.0f, 0.05f), $"A≈2.0 (alpha accumulates MUCH faster with RGB tint!)");
    }

    private void TestNonPremultiplied(RenderTarget2D rt, Texture2D white, Rectangle rect)
    {
        // NonPremultiplied = SRC_ALPHA, INV_SRC_ALPHA (standard alpha blend)
        // Clear to (0.5, 0.3, 0.2, 1.0), draw white with alpha=128
        // output = (1,1,1,0.502)
        // result.rgb = 1.0*0.502 + (0.5,0.3,0.2)*0.498 = (0.751, 0.651, 0.601)
        Device!.SetRenderTarget(rt);
        Device.Clear(new Color(0.5f, 0.3f, 0.2f, 1.0f));
        Batch!.Begin(SpriteSortMode.Immediate, BlendState.NonPremultiplied, SamplerState.PointClamp);
        Batch.Draw(white, rect, new Color(255, 255, 255, 128));
        Batch.End();
        Device.SetRenderTarget(null);

        var px = ReadPixel(rt);
        DebugLog.Log(ScenarioLog, $"  Tint: Color(255,255,255,128)  Bg: (0.5, 0.3, 0.2, 1.0)");
        DebugLog.Log(ScenarioLog, $"  Result: R={px.X:F4} G={px.Y:F4} B={px.Z:F4} A={px.W:F4}");

        LogCheck(ApproxEq(px.X, 0.751f, 0.03f), $"R≈0.75 (src*a + dst*(1-a))");
        LogCheck(ApproxEq(px.Y, 0.651f, 0.03f), $"G≈0.65");
        LogCheck(ApproxEq(px.Z, 0.601f, 0.03f), $"B≈0.60");
    }

    private void TestOpaque(RenderTarget2D rt, Texture2D white, Rectangle rect)
    {
        // Opaque = ONE, ZERO: result = src, dst completely replaced
        Device!.SetRenderTarget(rt);
        Device.Clear(new Color(0.5f, 0.5f, 0.5f, 1.0f));
        Batch!.Begin(SpriteSortMode.Immediate, BlendState.Opaque, SamplerState.PointClamp);
        Batch.Draw(white, rect, new Color(200, 100, 50, 128));
        Batch.End();
        Device.SetRenderTarget(null);

        var px = ReadPixel(rt);
        float expR = 200f / 255f, expG = 100f / 255f, expB = 50f / 255f;
        DebugLog.Log(ScenarioLog, $"  Tint: Color(200,100,50,128)  Bg: (0.5, 0.5, 0.5, 1.0)");
        DebugLog.Log(ScenarioLog, $"  Result: R={px.X:F4} G={px.Y:F4} B={px.Z:F4} A={px.W:F4}");

        LogCheck(ApproxEq(px.X, expR, 0.02f), $"R≈{expR:F3} (dst replaced)");
        LogCheck(ApproxEq(px.Y, expG, 0.02f), $"G≈{expG:F3}");
        LogCheck(ApproxEq(px.Z, expB, 0.02f), $"B≈{expB:F3}");
    }

    private void TestMultipleAdditive(RenderTarget2D rt, Texture2D white, Rectangle rect)
    {
        // 3 additive passes on same RT (don't unbind between them)
        // Each pass: src = (1,1,1,0.502), result.rgb += 1.0 * 0.502
        Device!.SetRenderTarget(rt);
        Device.Clear(new Color(0, 0, 0, 0)); // (0,0,0,0)

        for (int i = 0; i < 3; i++)
        {
            Batch!.Begin(SpriteSortMode.Immediate, BlendState.Additive, SamplerState.PointClamp);
            Batch.Draw(white, rect, new Color(255, 255, 255, 128));
            Batch.End();

            var pi = ReadPixelInPlace(rt);
            DebugLog.Log(ScenarioLog, $"  Pass {i + 1}: R={pi.X:F4} A={pi.W:F4}");
        }
        Device.SetRenderTarget(null);

        var px = ReadPixel(rt);
        float expected = 0.502f * 3;
        DebugLog.Log(ScenarioLog, $"  Final: R={px.X:F4} A={px.W:F4}");
        LogCheck(ApproxEq(px.X, expected, 0.05f), $"R≈{expected:F3} (3 * 0.502 linear accumulation)");
    }

    private void TestRtToRtAdditive(RenderTarget2D rt, RenderTarget2D rt2, Rectangle rect)
    {
        // Simulate bloom upsample: draw rt (as texture) onto rt2 with Additive + scatter
        // This tests whether the SOURCE texture's alpha affects the blend result

        // Fill rt with known values: RGB=0.6, A=1.0
        Device!.SetRenderTarget(rt);
        Device.Clear(new Color(0.6f, 0.6f, 0.6f, 1.0f));
        Device.SetRenderTarget(null);

        // Fill rt2 with background: RGB=0.2, A=1.0
        Device!.SetRenderTarget(rt2);
        Device.Clear(new Color(0.2f, 0.2f, 0.2f, 1.0f));

        // Draw rt onto rt2 with Additive + alpha scatter
        Batch!.Begin(SpriteSortMode.Immediate, BlendState.Additive, SamplerState.PointClamp);
        Batch.Draw(rt, new Rectangle(0, 0, rt2.Width, rt2.Height), new Color(255, 255, 255, 128));
        Batch.End();
        Device.SetRenderTarget(null);

        var px = ReadPixel(rt2);
        // src pixel = rt_color * tint = (0.6,0.6,0.6,1.0) * (1,1,1,0.502) = (0.6, 0.6, 0.6, 0.502)
        // Additive: result.rgb = 0.6 * 0.502 + 0.2 = 0.501
        // result.a = 0.502 * 0.502 + 1.0 = 1.252
        DebugLog.Log(ScenarioLog, $"  src RT: (0.6, 0.6, 0.6, 1.0)  dst RT: (0.2, 0.2, 0.2, 1.0)");
        DebugLog.Log(ScenarioLog, $"  Additive + tint alpha=128");
        DebugLog.Log(ScenarioLog, $"  Result: R={px.X:F4} A={px.W:F4}");

        LogCheck(ApproxEq(px.X, 0.501f, 0.03f),
            $"R≈0.50 (src.rgb * tint.a + dst.rgb, src alpha=1.0 not accumulated)");

        // Now test with a source that already has alpha > 1.0
        // First make rt have alpha > 1.0 by drawing additively onto it
        Device.SetRenderTarget(rt);
        // rt still has (0.6, 0.6, 0.6, 1.0) — draw white additively to bump alpha
        Batch!.Begin(SpriteSortMode.Immediate, BlendState.Additive, SamplerState.PointClamp);
        Batch.Draw(new Texture2D(Device, 1, 1) { }, rect, new Color(0, 0, 0, 255));
        Batch.End();

        // Hmm, that won't work cleanly. Let me just read what alpha ended up in rt2
        // and do a second pass from rt2 back to rt
        Device.SetRenderTarget(null);

        DebugLog.Log(ScenarioLog, $"");
        DebugLog.Log(ScenarioLog, $"  Chain test: rt2 (alpha={px.W:F4}) → rt with additive scatter");
        Device.SetRenderTarget(rt);
        Device.Clear(new Color(0.1f, 0.1f, 0.1f, 1.0f));
        Batch!.Begin(SpriteSortMode.Immediate, BlendState.Additive, SamplerState.PointClamp);
        Batch.Draw(rt2, new Rectangle(0, 0, rt.Width, rt.Height), new Color(255, 255, 255, 128));
        Batch.End();
        Device.SetRenderTarget(null);

        var px2 = ReadPixel(rt);
        // src pixel from rt2 = (px.X, px.X, px.X, px.W) * tint(1,1,1,0.502)
        //                    = (px.X, px.X, px.X, px.W * 0.502)
        // Additive: result.rgb = px.X * (px.W * 0.502) + 0.1
        float srcAlpha = px.W * (128f / 255f);
        float expectedR = px.X * srcAlpha + 0.1f;
        float simpleExpected = px.X * 0.502f + 0.1f; // if alpha was clamped to 1.0
        DebugLog.Log(ScenarioLog, $"  Result: R={px2.X:F4} A={px2.W:F4}");
        DebugLog.Log(ScenarioLog, $"    If alpha accumulates: R = {px.X:F4} * {srcAlpha:F4} + 0.1 = {expectedR:F4}");
        DebugLog.Log(ScenarioLog, $"    If alpha clamped to 1: R = {px.X:F4} * 0.502 + 0.1 = {simpleExpected:F4}");

        if (ApproxEq(px2.X, expectedR, 0.03f))
            LogCheck(false, $"R≈{expectedR:F3} — ALPHA ACCUMULATION AMPLIFIES BLOOM!");
        else if (ApproxEq(px2.X, simpleExpected, 0.03f))
            LogCheck(true, $"R≈{simpleExpected:F3} — alpha clamped, no amplification");
        else
            LogCheck(false, $"R={px2.X:F4} — unexpected value (neither clamped nor accumulated)");
    }

    private void TestHdrAccumulation(RenderTarget2D rt, Texture2D white, Rectangle rect)
    {
        // Verify HDR render target preserves values > 1.0
        Device!.SetRenderTarget(rt);
        Device.Clear(new Color(0, 0, 0, 0));

        // 5 additive passes of white * alpha=128 → should reach ~2.51
        for (int i = 0; i < 5; i++)
        {
            Batch!.Begin(SpriteSortMode.Immediate, BlendState.Additive, SamplerState.PointClamp);
            Batch.Draw(white, rect, new Color(255, 255, 255, 128));
            Batch.End();
        }
        Device.SetRenderTarget(null);

        var px = ReadPixel(rt);
        DebugLog.Log(ScenarioLog, $"  5x additive white(alpha=128): R={px.X:F4} A={px.W:F4}");
        LogCheck(px.X > 1.5f, $"HDR preserved: R={px.X:F4} > 1.5 (expected ~2.51)");
    }

    private void TestHdrSourceTexture(RenderTarget2D rt, RenderTarget2D rt2, Rectangle rect)
    {
        // Test: fill rt with HDR values (R=3.0), then draw it onto rt2 with Opaque.
        // This simulates bloom downsample — does the HDR value survive the SpriteBatch draw?
        Device!.SetRenderTarget(rt);
        Device.Clear(new Color(0, 0, 0, 0));
        // Build up R=3.0 by drawing white additively 6 times (6 * 0.502 ≈ 3.01)
        var white = new Texture2D(Device, 1, 1);
        white.SetData(new[] { Color.White });
        for (int i = 0; i < 6; i++)
        {
            Batch!.Begin(SpriteSortMode.Immediate, BlendState.Additive, SamplerState.PointClamp);
            Batch.Draw(white, rect, new Color(255, 255, 255, 128));
            Batch.End();
        }
        Device.SetRenderTarget(null);
        var srcPx = ReadPixel(rt);
        DebugLog.Log(ScenarioLog, $"  Source HDR RT: R={srcPx.X:F4} A={srcPx.W:F4}");
        DebugLog.Log(ScenarioLog, $"  Source RT format: {rt.Format}");

        // Now draw rt onto rt2 with Opaque (like bloom downsample)
        Device.SetRenderTarget(rt2);
        Device.Clear(Color.Black);
        Batch!.Begin(SpriteSortMode.Immediate, BlendState.Opaque, SamplerState.LinearClamp);
        Batch.Draw(rt, rect, Color.White);
        Batch.End();
        Device.SetRenderTarget(null);

        var dstPx = ReadPixel(rt2);
        DebugLog.Log(ScenarioLog, $"  After Opaque blit to rt2: R={dstPx.X:F4} A={dstPx.W:F4}");
        DebugLog.Log(ScenarioLog, $"  Dest RT format: {rt2.Format}");

        // HDR value should be preserved — if it's clamped to 1.0, that's the bug
        LogCheck(dstPx.X > 2.5f, $"HDR preserved through Opaque blit: R={dstPx.X:F4} (expected ≈{srcPx.X:F4})");
        LogCheck(ApproxEq(dstPx.X, srcPx.X, 0.05f), $"Value matches source: {dstPx.X:F4} ≈ {srcPx.X:F4}");
        white.Dispose();
    }

    private void TestHdrAdditiveFromHdr(RenderTarget2D rt, RenderTarget2D rt2, Rectangle rect)
    {
        // Test: fill rt with HDR value (R=2.0), fill rt2 with (R=0.5),
        // then draw rt onto rt2 with Additive + scatter alpha=128.
        // This is the exact bloom upsample operation with HDR source textures.
        Device!.SetRenderTarget(rt);
        Device.Clear(new Color(0, 0, 0, 0));
        var white = new Texture2D(Device, 1, 1);
        white.SetData(new[] { Color.White });
        // Build up R=2.0 (4 * 0.502 ≈ 2.008)
        for (int i = 0; i < 4; i++)
        {
            Batch!.Begin(SpriteSortMode.Immediate, BlendState.Additive, SamplerState.PointClamp);
            Batch.Draw(white, rect, new Color(255, 255, 255, 128));
            Batch.End();
        }
        Device.SetRenderTarget(null);
        var srcPx = ReadPixel(rt);
        DebugLog.Log(ScenarioLog, $"  Source HDR RT: R={srcPx.X:F4} G={srcPx.Y:F4} A={srcPx.W:F4}");

        // Fill rt2 with (0.5, 0.5, 0.5, 1.0)
        Device.SetRenderTarget(rt2);
        Device.Clear(new Color(0.5f, 0.5f, 0.5f, 1.0f));

        // Draw HDR source (rt) onto rt2 with Additive + scatter
        Batch!.Begin(SpriteSortMode.Immediate, BlendState.Additive, SamplerState.PointClamp);
        Batch.Draw(rt, rect, new Color(255, 255, 255, 128));
        Batch.End();
        Device.SetRenderTarget(null);

        var dstPx = ReadPixel(rt2);
        // src pixel = (2.0, 2.0, 2.0, srcPx.W) * tint(1,1,1,0.502) = (2.0, 2.0, 2.0, srcPx.W * 0.502)
        // Additive (SRC_ALPHA, ONE):
        //   result.rgb = 2.0 * (srcPx.W * 0.502) + 0.5
        float srcAlpha = srcPx.W * (128f / 255f);
        float expectedR = srcPx.X * srcAlpha + 0.5f;
        float expectedIfClamped = srcPx.X * 0.502f + 0.5f; // if src values clamped to 1.0
        float expectedIfClampedTo1 = 1.0f * srcAlpha + 0.5f; // if RGB clamped to 1.0
        DebugLog.Log(ScenarioLog, $"  Additive HDR→LDR dst with scatter alpha=128:");
        DebugLog.Log(ScenarioLog, $"  Result: R={dstPx.X:F4} A={dstPx.W:F4}");
        DebugLog.Log(ScenarioLog, $"    If HDR src preserved:  R = {srcPx.X:F4} * {srcAlpha:F4} + 0.5 = {expectedR:F4}");
        DebugLog.Log(ScenarioLog, $"    If src RGB clamped:    R = 1.0 * {srcAlpha:F4} + 0.5 = {expectedIfClampedTo1:F4}");
        DebugLog.Log(ScenarioLog, $"    If src alpha clamped:  R = {srcPx.X:F4} * 0.502 + 0.5 = {expectedIfClamped:F4}");

        if (ApproxEq(dstPx.X, expectedR, 0.05f))
            LogCheck(true, $"HDR source fully preserved (R={dstPx.X:F4} ≈ {expectedR:F4})");
        else if (ApproxEq(dstPx.X, expectedIfClampedTo1, 0.05f))
            LogCheck(false, $"HDR RGB CLAMPED to 1.0! (R={dstPx.X:F4} ≈ {expectedIfClampedTo1:F4})");
        else if (ApproxEq(dstPx.X, expectedIfClamped, 0.05f))
            LogCheck(false, $"HDR alpha CLAMPED to 1.0! (R={dstPx.X:F4} ≈ {expectedIfClamped:F4})");
        else
            LogCheck(false, $"Unexpected R={dstPx.X:F4}");

        white.Dispose();
    }

    private void TestPixelBleed()
    {
        // Simulate bloom mip chain: bright center pixel in 8x8 → downsample → upsample
        // This tests how a single bright pixel spreads through the bloom pipeline
        const int S0 = 8, S1 = 4, S2 = 2;

        var mip0 = new RenderTarget2D(Device!, S0, S0, false,
            SurfaceFormat.HalfVector4, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
        var mip1 = new RenderTarget2D(Device!, S1, S1, false,
            SurfaceFormat.HalfVector4, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
        var mip2 = new RenderTarget2D(Device!, S2, S2, false,
            SurfaceFormat.HalfVector4, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);

        // Create a 1x1 bright pixel texture (HDR white)
        var brightPixel = new Texture2D(Device!, 1, 1);
        brightPixel.SetData(new[] { Color.White });

        // Step 1: Clear mip0 to black, draw a single bright pixel at center
        Device!.SetRenderTarget(mip0);
        Device.Clear(Color.Black);
        Batch!.Begin(SpriteSortMode.Immediate, BlendState.Opaque, SamplerState.PointClamp);
        Batch.Draw(brightPixel, new Rectangle(S0 / 2 - 1, S0 / 2 - 1, 2, 2), Color.White);
        Batch.End();
        Device.SetRenderTarget(null);

        DebugLog.Log(ScenarioLog, "  Setup: 2x2 bright white pixels at center of 8x8 black RT");
        DumpRT(mip0, "mip0 (source)");

        // Step 2: Downsample mip0 → mip1 (8x8 → 4x4) with LinearClamp + Opaque
        Device.SetRenderTarget(mip1);
        Device.Clear(Color.Black);
        Batch!.Begin(SpriteSortMode.Immediate, BlendState.Opaque, SamplerState.LinearClamp);
        Batch.Draw(mip0, new Rectangle(0, 0, S1, S1), Color.White);
        Batch.End();
        Device.SetRenderTarget(null);

        DebugLog.Log(ScenarioLog, "");
        DebugLog.Log(ScenarioLog, "  Downsample 8x8 → 4x4 (Opaque + LinearClamp):");
        DumpRT(mip1, "mip1 (down)");

        // Step 3: Downsample mip1 → mip2 (4x4 → 2x2)
        Device.SetRenderTarget(mip2);
        Device.Clear(Color.Black);
        Batch!.Begin(SpriteSortMode.Immediate, BlendState.Opaque, SamplerState.LinearClamp);
        Batch.Draw(mip1, new Rectangle(0, 0, S2, S2), Color.White);
        Batch.End();
        Device.SetRenderTarget(null);

        DebugLog.Log(ScenarioLog, "");
        DebugLog.Log(ScenarioLog, "  Downsample 4x4 → 2x2 (Opaque + LinearClamp):");
        DumpRT(mip2, "mip2 (down)");

        // Step 4: Upsample mip2 → mip1 (2x2 → 4x4) with Additive + scatter
        // mip1 still has the downsample content — upsample adds on top (like bloom)
        byte scatterAlpha = 128; // scatter ≈ 0.5
        Device.SetRenderTarget(mip1);
        Batch!.Begin(SpriteSortMode.Immediate, BlendState.Additive, SamplerState.LinearClamp);
        Batch.Draw(mip2, new Rectangle(0, 0, S1, S1), new Color((byte)255, (byte)255, (byte)255, scatterAlpha));
        Batch.End();
        Device.SetRenderTarget(null);

        DebugLog.Log(ScenarioLog, "");
        DebugLog.Log(ScenarioLog, "  Upsample 2x2 → 4x4 (Additive + scatter alpha=128):");
        DumpRT(mip1, "mip1 (up)");

        // Step 5: Upsample mip1 → mip0 (4x4 → 8x8) with Additive + scatter
        Device.SetRenderTarget(mip0);
        Batch!.Begin(SpriteSortMode.Immediate, BlendState.Additive, SamplerState.LinearClamp);
        Batch.Draw(mip1, new Rectangle(0, 0, S0, S0), new Color((byte)255, (byte)255, (byte)255, scatterAlpha));
        Batch.End();
        Device.SetRenderTarget(null);

        DebugLog.Log(ScenarioLog, "");
        DebugLog.Log(ScenarioLog, "  Upsample 4x4 → 8x8 (Additive + scatter alpha=128):");
        DumpRT(mip0, "mip0 (final)");

        // Compute total energy at each step
        float e0 = SumRT(mip0);
        DebugLog.Log(ScenarioLog, "");
        DebugLog.Log(ScenarioLog, $"  Total energy in final mip0: {e0:F4}");

        mip0.Dispose();
        mip1.Dispose();
        mip2.Dispose();
        brightPixel.Dispose();
    }

    private void DumpRT(RenderTarget2D rt, string label)
    {
        var data = new HalfVector4[rt.Width * rt.Height];
        rt.GetData(data);
        DebugLog.Log(ScenarioLog, $"    {label} ({rt.Width}x{rt.Height}):");
        for (int y = 0; y < rt.Height; y++)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("      ");
            for (int x = 0; x < rt.Width; x++)
            {
                var v = data[y * rt.Width + x].ToVector4();
                sb.AppendFormat("{0,7:F3}", v.X);
            }
            DebugLog.Log(ScenarioLog, sb.ToString());
        }
    }

    private float SumRT(RenderTarget2D rt)
    {
        var data = new HalfVector4[rt.Width * rt.Height];
        rt.GetData(data);
        float sum = 0;
        for (int i = 0; i < data.Length; i++)
            sum += data[i].ToVector4().X;
        return sum;
    }

    // --- Helpers ---

    private Vector4 ReadPixel(RenderTarget2D rt)
    {
        var data = new HalfVector4[rt.Width * rt.Height];
        rt.GetData(data);
        int center = (rt.Height / 2) * rt.Width + rt.Width / 2;
        return data[center].ToVector4();
    }

    private Vector4 ReadPixelInPlace(RenderTarget2D rt)
    {
        // Read pixel while RT is still bound — need to unbind, read, rebind
        Device!.SetRenderTarget(null);
        var result = ReadPixel(rt);
        Device.SetRenderTarget(rt);
        return result;
    }

    private static bool ApproxEq(float a, float b, float tolerance) =>
        MathF.Abs(a - b) <= tolerance;

    private void LogCheck(bool pass, string msg)
    {
        if (pass) _pass++; else _fail++;
        DebugLog.Log(ScenarioLog, $"  [{(pass ? "PASS" : "FAIL")}] {msg}");
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, $"Blend test: {_pass} passed, {_fail} failed");
        Console.Error.WriteLine($"BLEND TEST: {_pass} passed, {_fail} failed");
        return _fail > 0 ? 1 : 0;
    }
}
