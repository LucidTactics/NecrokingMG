using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Core;
using Necroking.GameSystems;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Isolated test for UIGradient.fx. Draws a row of labeled rectangles, one
/// per gradient mode, on a dark background so banding / correctness is easy
/// to see. Screenshots the result so it can be reviewed visually.
/// </summary>
public class UIGradientTestScenario : UIScenarioBase
{
    public override string Name => "UIGradientTest";

    private float _elapsed;
    private int _phase;
    private bool _complete;
    private int _failCode;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== UIGradient shader isolation test ===");
        ZoomOnLocation(10f, 10f, 32f);

        // Dark background so gradients show their range clearly.
        BackgroundColor = new Color(20, 16, 12);

        // Register a custom UI hook that draws the test patches.
        CustomUIDraw = (batch, screenW, screenH) =>
        {
            if (UIShaders == null) return;

            int pad = 40;
            int availW = screenW - pad * 2;
            int patchW = (availW - 30) / 4;
            int patchH = 220;
            int y = 80;

            // Patch 1: vertical linear, bright red to dark red
            var r1 = new Rectangle(pad, y, patchW, patchH);
            UIShaders.DrawVerticalGradient(batch, r1,
                new Color(240, 90, 90), new Color(30, 10, 10));

            // Patch 2: horizontal linear, blue to yellow
            var r2 = new Rectangle(r1.Right + 10, y, patchW, patchH);
            UIShaders.DrawHorizontalGradient(batch, r2,
                new Color(40, 80, 200), new Color(240, 220, 80));

            // Patch 3: vertical 3-stop (gold->bronze->dark) matching the
            // title-plate use case from the design.
            var r3 = new Rectangle(r2.Right + 10, y, patchW, patchH);
            UIShaders.DrawVertical3StopGradient(batch, r3,
                new Color(201, 168, 96),
                new Color(138, 109, 50),
                new Color(60,  44, 22),
                0.55f);

            // Patch 4: radial, center light to edge dark
            var r4 = new Rectangle(r3.Right + 10, y, patchW, patchH);
            UIShaders.DrawRadialGradient(batch, r4,
                new Color(239, 227, 196), new Color(50, 40, 25),
                new Vector2(0.5f, 0.4f), 0.7f);

            // Second row: same gradients but tall & thin to stress the
            // math on non-square UV ratios.
            int y2 = y + patchH + 40;
            int tallH = 320;
            int tallW = patchW;
            var t1 = new Rectangle(pad, y2, tallW, tallH);
            UIShaders.DrawVerticalGradient(batch, t1,
                new Color(90, 200, 120), new Color(10, 20, 30));

            var t2 = new Rectangle(t1.Right + 10, y2, tallW, tallH);
            UIShaders.DrawHorizontalGradient(batch, t2,
                new Color(255, 120, 30), new Color(30, 10, 60));

            var t3 = new Rectangle(t2.Right + 10, y2, tallW, tallH);
            UIShaders.DrawVertical3StopGradient(batch, t3,
                new Color(180, 180, 255),
                new Color(80,  40, 120),
                new Color(10,   0,  10),
                0.3f);

            var t4 = new Rectangle(t3.Right + 10, y2, tallW, tallH);
            UIShaders.DrawRadialGradient(batch, t4,
                new Color(255, 240, 200), new Color(0, 0, 0),
                new Vector2(0.5f, 0.5f), 0.6f);
        };
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;
        if (_phase == 0 && _elapsed > 0.5f)
        {
            if (UIShaders?.Gradient == null)
            {
                DebugLog.Log(ScenarioLog, "FAIL: UIShaders.Gradient effect did not load");
                _failCode = 1;
                _complete = true;
                return;
            }
            DebugLog.Log(ScenarioLog, "Requesting screenshot of gradient test");
            DeferredScreenshot = "ui_gradient_test";
            _phase = 1;
        }
        else if (_phase == 1 && DeferredScreenshot == null)
        {
            DebugLog.Log(ScenarioLog, "Screenshot taken, scenario complete");
            _complete = true;
        }
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, $"Result: {(_failCode == 0 ? "PASS" : $"FAIL ({_failCode})")}");
        return _failCode;
    }
}
