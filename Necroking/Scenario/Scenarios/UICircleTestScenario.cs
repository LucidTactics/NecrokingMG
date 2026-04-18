using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Core;
using Necroking.GameSystems;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Isolated test for UICircleEffect.fx. Draws several circles on a parchment
/// background to verify AA, vertical gradient fill, and outer glow each work.
/// </summary>
public class UICircleTestScenario : ScenarioBase
{
    public override string Name => "UICircleTest";

    private float _elapsed;
    private int _phase;
    private bool _complete;
    private int _failCode;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== UICircleEffect shader isolation test ===");
        ZoomOnLocation(10f, 10f, 32f);
        BackgroundColor = new Color(232, 220, 192);
        BloomOverride = new Data.Registries.BloomSettings { Enabled = false };

        CustomUIDraw = (batch, screenW, screenH) =>
        {
            if (UIShaders == null) return;

            // Paint parchment bg
            batch.Draw(UIShaders.GetPixel(),
                new Rectangle(0, 0, screenW, screenH), new Color(232, 220, 192));

            // Row 1: solid fills no glow
            int y1 = screenH / 4;
            int colStep = screenW / 5;
            UIShaders.DrawCircle(batch, new Vector2(colStep, y1), 40, 40,
                new Color(120, 40, 40), new Color(120, 40, 40), Color.Transparent);
            UIShaders.DrawCircle(batch, new Vector2(colStep * 2, y1), 40, 40,
                new Color(40, 90, 150), new Color(40, 90, 150), Color.Transparent);
            UIShaders.DrawCircle(batch, new Vector2(colStep * 3, y1), 40, 40,
                new Color(60, 120, 60), new Color(60, 120, 60), Color.Transparent);
            UIShaders.DrawCircle(batch, new Vector2(colStep * 4, y1), 40, 40,
                new Color(30, 20, 15), new Color(30, 20, 15), Color.Transparent);

            // Row 2: vertical gradient fills (bone-ring style)
            int y2 = y1 + 140;
            UIShaders.DrawCircle(batch, new Vector2(colStep, y2), 40, 40,
                new Color(232, 220, 192), new Color(138, 109, 50), Color.Transparent);
            UIShaders.DrawCircle(batch, new Vector2(colStep * 2, y2), 40, 40,
                new Color(255, 240, 200), new Color(60, 20, 10), Color.Transparent);
            UIShaders.DrawCircle(batch, new Vector2(colStep * 3, y2), 40, 40,
                new Color(200, 220, 255), new Color(20, 40, 80), Color.Transparent);
            UIShaders.DrawCircle(batch, new Vector2(colStep * 4, y2), 40, 40,
                new Color(255, 200, 100), new Color(90, 30, 0), Color.Transparent);

            // Row 3: solid fills with outer glow (school-color halos)
            int y3 = y2 + 160;
            UIShaders.DrawCircle(batch, new Vector2(colStep, y3), 40, 70,
                new Color(30, 20, 15), new Color(30, 20, 15),
                new Color(201, 184, 138, 220)); // bone
            UIShaders.DrawCircle(batch, new Vector2(colStep * 2, y3), 40, 70,
                new Color(30, 20, 15), new Color(30, 20, 15),
                new Color(122, 90, 146, 220)); // soul
            UIShaders.DrawCircle(batch, new Vector2(colStep * 3, y3), 40, 70,
                new Color(30, 20, 15), new Color(30, 20, 15),
                new Color(139, 26, 26, 220)); // shadow
            UIShaders.DrawCircle(batch, new Vector2(colStep * 4, y3), 40, 70,
                new Color(30, 20, 15), new Color(30, 20, 15),
                new Color(240, 208, 96, 255)); // gold

            // Row 4: bigger and smaller to test scaling
            int y4 = y3 + 170;
            UIShaders.DrawCircle(batch, new Vector2(colStep, y4), 20, 20,
                new Color(80, 40, 40), new Color(80, 40, 40), Color.Transparent);
            UIShaders.DrawCircle(batch, new Vector2(colStep * 2, y4), 30, 50,
                new Color(200, 180, 140), new Color(90, 60, 30),
                new Color(240, 200, 100, 200));
            UIShaders.DrawCircle(batch, new Vector2(colStep * 3, y4), 55, 90,
                new Color(220, 200, 160), new Color(100, 70, 40),
                new Color(139, 26, 26, 200));
            UIShaders.DrawCircle(batch, new Vector2(colStep * 4, y4), 70, 100,
                new Color(255, 240, 200), new Color(50, 30, 20),
                new Color(80, 180, 240, 180));
        };
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;
        if (_phase == 0 && _elapsed > 0.5f)
        {
            if (UIShaders?.CircleEffect == null)
            {
                DebugLog.Log(ScenarioLog, "FAIL: CircleEffect did not load");
                _failCode = 1;
                _complete = true;
                return;
            }
            DeferredScreenshot = "ui_circle_test";
            _phase = 1;
        }
        else if (_phase == 1 && DeferredScreenshot == null)
        {
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
