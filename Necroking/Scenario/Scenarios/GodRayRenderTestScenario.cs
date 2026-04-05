using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Graphics.PackedVector;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Render;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Renders a god ray with known parameters to an HDR render target and dumps pixel values.
/// Compares pixel-level output with C++ equivalent to find rendering differences.
/// </summary>
public class GodRayRenderTestScenario : ScenarioBase
{
    public override string Name => "godray_render_test";

    private bool _complete;
    private bool _testsRan;

    public GraphicsDevice? Device;
    public SpriteBatch? SpriteBatchRef;
    public GodRayRenderer? GodRayRef;
    public Microsoft.Xna.Framework.Graphics.Effect? HdrEffect;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== God Ray Render Test ===");
        DebugLog.Log(ScenarioLog, "Rendering god ray with fixed params to HDR RT, dumping pixels");
    }

    public override void OnTick(Simulation sim, float dt)
    {
        if (_complete) return;
        if (!_testsRan && Device != null && GodRayRef != null)
        {
            RunTest();
            _testsRan = true;
            _complete = true;
        }
    }

    private void RunTest()
    {
        const int W = 64, H = 64;

        var rt = new RenderTarget2D(Device!, W, H, false,
            SurfaceFormat.HalfVector4, DepthFormat.None,
            0, RenderTargetUsage.PreserveContents);

        // Fixed god ray parameters — no shimmer, no noise, no flicker
        var style = new LightningStyle
        {
            CoreColor = new HdrColor(255, 247, 200, 255, 7.67f),
            GlowColor = new HdrColor(254, 192, 82, 180, 11.25f),
            CoreWidth = 1.2f,
            GlowWidth = 4.5f,
        };
        var godRayParams = new GodRayParams
        {
            EdgeSoftness = 0.4f,
            NoiseSpeed = 0f,       // disable noise
            NoiseStrength = 0f,    // disable noise
            NoiseScale = 3f,
        };

        // Fixed screen positions: sky at top-center, ground at bottom-center
        var sky = new Vector2(W / 2f, 2f);
        var ground = new Vector2(W / 2f, H - 4f);

        // elapsed=0 means shimmer = sin(0)*0.15 + 0.85 = 0.85
        // effectTimer=0, effectDuration=2.0 means no fadeout
        float elapsed = 0f;
        float effectTimer = 0f;
        float effectDuration = 2f;

        DebugLog.Log(ScenarioLog, $"  RT size: {W}x{H}");
        DebugLog.Log(ScenarioLog, $"  Sky: ({sky.X}, {sky.Y})  Ground: ({ground.X}, {ground.Y})");
        DebugLog.Log(ScenarioLog, $"  Core: ({style.CoreColor.R},{style.CoreColor.G},{style.CoreColor.B},{style.CoreColor.A}) intensity={style.CoreColor.Intensity}");
        DebugLog.Log(ScenarioLog, $"  Glow: ({style.GlowColor.R},{style.GlowColor.G},{style.GlowColor.B},{style.GlowColor.A}) intensity={style.GlowColor.Intensity}");
        DebugLog.Log(ScenarioLog, $"  CoreWidth={style.CoreWidth} GlowWidth={style.GlowWidth}");
        DebugLog.Log(ScenarioLog, $"  Elapsed={elapsed} (shimmer={MathF.Sin(elapsed * 8f) * 0.15f + 0.85f:F4})");

        // --- Single triangle test: known intensity, known color ---
        DebugLog.Log(ScenarioLog, "");
        DebugLog.Log(ScenarioLog, "  === Single triangle test ===");
        {
            Device!.SetRenderTarget(rt);
            Device.Clear(Color.Black);

            var vp = Device.Viewport;
            var wvp = Matrix.CreateOrthographicOffCenter(0, W, H, 0, 0, 1);
            var effect = HdrEffect;
            if (effect != null)
            {
                effect.Parameters["WorldViewProjection"]?.SetValue(wvp);
                effect.Parameters["Intensity"]?.SetValue(5.0f);
            }
            Device.BlendState = BlendState.NonPremultiplied;
            Device.DepthStencilState = DepthStencilState.None;
            Device.RasterizerState = RasterizerState.CullNone;

            // Draw a triangle covering the full RT with color (255, 255, 255, 255)
            var verts = new VertexPositionColor[]
            {
                new(new Vector3(0, 0, 0), Color.White),
                new(new Vector3(W, 0, 0), Color.White),
                new(new Vector3(W/2f, H, 0), Color.White),
            };
            foreach (var pass in effect!.CurrentTechnique.Passes)
            {
                pass.Apply();
                Device.DrawUserPrimitives(PrimitiveType.TriangleList, verts, 0, 1);
            }
            Device.SetRenderTarget(null);

            var testData = new HalfVector4[W * H];
            rt.GetData(testData);
            var center = testData[(H / 4) * W + W / 2].ToVector4();
            DebugLog.Log(ScenarioLog, $"    Color=White(255,255,255,255), Intensity=5.0");
            DebugLog.Log(ScenarioLog, $"    Expected: R=5.0 (1.0 * 5.0)");
            DebugLog.Log(ScenarioLog, $"    Actual:   R={center.X:F4} G={center.Y:F4} B={center.Z:F4} A={center.W:F4}");

            // Test with half alpha
            Device.SetRenderTarget(rt);
            Device.Clear(Color.Black);
            effect.Parameters["Intensity"]?.SetValue(5.0f);
            var verts2 = new VertexPositionColor[]
            {
                new(new Vector3(0, 0, 0), new Color(255, 255, 255, 128)),
                new(new Vector3(W, 0, 0), new Color(255, 255, 255, 128)),
                new(new Vector3(W/2f, H, 0), new Color(255, 255, 255, 128)),
            };
            foreach (var pass in effect.CurrentTechnique.Passes)
            {
                pass.Apply();
                Device.DrawUserPrimitives(PrimitiveType.TriangleList, verts2, 0, 1);
            }
            Device.SetRenderTarget(null);

            rt.GetData(testData);
            center = testData[(H / 4) * W + W / 2].ToVector4();
            DebugLog.Log(ScenarioLog, $"    Color=White(255,255,255,128), Intensity=5.0, NonPremultiplied blend");
            DebugLog.Log(ScenarioLog, $"    Expected: R = 5.0 * 0.502 = 2.51 (src*srcAlpha on black)");
            DebugLog.Log(ScenarioLog, $"    Actual:   R={center.X:F4} G={center.Y:F4} B={center.Z:F4} A={center.W:F4}");
        }

        // Render god ray to the HDR RT
        Device!.SetRenderTarget(rt);
        Device.Clear(Color.Black);

        // Queue the god ray
        GodRayRef!.PendingGodRays.Clear();
        GodRayRef.PendingGodRays.Add((sky, ground, style, godRayParams,
            elapsed, effectTimer, effectDuration));
        GodRayRef.DrawAll();

        Device.SetRenderTarget(null);

        // Read back and dump
        var data = new HalfVector4[W * H];
        rt.GetData(data);

        // Dump center column (x = W/2) — this is the beam's core
        DebugLog.Log(ScenarioLog, "");
        DebugLog.Log(ScenarioLog, $"  Center column (x={W / 2}) R values, top to bottom:");
        for (int y = 0; y < H; y++)
        {
            var v = data[y * W + W / 2].ToVector4();
            if (v.X > 0.001f || y % 8 == 0) // log non-zero and every 8th row
                DebugLog.Log(ScenarioLog, $"    y={y,2}: R={v.X,8:F4} G={v.Y,8:F4} B={v.Z,8:F4} A={v.W,8:F4}");
        }

        // Dump a horizontal cross-section at 75% height (near ground, widest part)
        int crossY = H * 3 / 4;
        DebugLog.Log(ScenarioLog, "");
        DebugLog.Log(ScenarioLog, $"  Horizontal cross-section at y={crossY}:");
        for (int x = 0; x < W; x++)
        {
            var v = data[crossY * W + x].ToVector4();
            if (v.X > 0.001f)
                DebugLog.Log(ScenarioLog, $"    x={x,2}: R={v.X,8:F4} G={v.Y,8:F4} B={v.Z,8:F4} A={v.W,8:F4}");
        }

        // Peak value
        float maxR = 0;
        int maxX = 0, maxY = 0;
        for (int i = 0; i < data.Length; i++)
        {
            float r = data[i].ToVector4().X;
            if (r > maxR) { maxR = r; maxX = i % W; maxY = i / W; }
        }
        DebugLog.Log(ScenarioLog, "");
        DebugLog.Log(ScenarioLog, $"  Peak pixel: R={maxR:F4} at ({maxX},{maxY})");

        // Sum total energy
        float totalR = 0;
        for (int i = 0; i < data.Length; i++) totalR += data[i].ToVector4().X;
        DebugLog.Log(ScenarioLog, $"  Total R energy: {totalR:F4}");

        rt.Dispose();
    }

    public override bool IsComplete => _complete;
    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "God ray render test complete.");
        return 0;
    }
}
