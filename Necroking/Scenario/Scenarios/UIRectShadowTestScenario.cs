using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Core;
using Necroking.GameSystems;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Isolated test for UIRectShadow.fx. Draws several inner rects on a light
/// parchment background, each wrapped in its own outer-padded shadow quad.
/// Also tests inset shadows on the right side.
/// </summary>
public class UIRectShadowTestScenario : ScenarioBase
{
    public override string Name => "UIRectShadowTest";
    // WantsUI is false -> scenario HUD is hidden; the CustomUIDraw hook
    // still runs so we can isolate-test our shader output.

    private float _elapsed;
    private int _phase;
    private bool _complete;
    private int _failCode;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== UIRectShadow shader isolation test ===");
        ZoomOnLocation(10f, 10f, 32f);
        BackgroundColor = new Color(232, 220, 192); // parchment
        BloomOverride = new Data.Registries.BloomSettings { Enabled = false };

        CustomUIDraw = (batch, screenW, screenH) =>
        {
            if (UIShaders == null) return;

            // Paint our own background so transparency in the shadows is
            // visible; the scenario BackgroundColor isn't applied here.
            batch.Draw(UIShaders.GetPixel(), new Rectangle(0, 0, screenW, screenH),
                new Color(232, 220, 192));

            // --- Left half: drop shadows ---
            int cx = screenW / 4;
            int cy = screenH / 3;

            // Label
            // (skip: no font here)

            // Shadow 1: soft small shadow under a small card
            int cardW = 160; int cardH = 90;
            int soft1 = 16;
            var inner1 = new Rectangle(cx - cardW / 2, cy - cardH / 2, cardW, cardH);
            var outer1 = new Rectangle(inner1.X - soft1, inner1.Y - soft1,
                                       inner1.Width + soft1 * 2, inner1.Height + soft1 * 2);
            UIShaders.DrawDropShadow(batch, outer1, inner1,
                new Color(58, 30, 20), new Color(0, 0, 0, 150), soft1);

            // Shadow 2: bigger shadow under a larger card
            int cx2 = cx; int cy2 = cy + 180;
            cardW = 240; cardH = 120;
            int soft2 = 32;
            var inner2 = new Rectangle(cx2 - cardW / 2, cy2 - cardH / 2, cardW, cardH);
            var outer2 = new Rectangle(inner2.X - soft2, inner2.Y - soft2,
                                       inner2.Width + soft2 * 2, inner2.Height + soft2 * 2);
            UIShaders.DrawDropShadow(batch, outer2, inner2,
                new Color(120, 80, 50), new Color(0, 0, 0, 200), soft2);

            // Shadow 3: shadow with a transparent fill (just the shadow,
            // caller would draw the card on top separately)
            int cx3 = cx; int cy3 = cy + 380;
            cardW = 200; cardH = 100;
            int soft3 = 24;
            var inner3 = new Rectangle(cx3 - cardW / 2, cy3 - cardH / 2, cardW, cardH);
            var outer3 = new Rectangle(inner3.X - soft3, inner3.Y - soft3,
                                       inner3.Width + soft3 * 2, inner3.Height + soft3 * 2);
            UIShaders.DrawDropShadow(batch, outer3, inner3,
                Color.Transparent, new Color(0, 0, 0, 180), soft3);
            // And draw the card on top
            batch.Draw(UIShaders.GetPixel(), inner3, new Color(180, 40, 40));

            // --- Right half: inset shadows ---
            int rx = (screenW * 3) / 4;
            int ry = screenH / 3;

            // Inset 1: small soft
            var r1 = new Rectangle(rx - 120, ry - 80, 240, 160);
            batch.Draw(UIShaders.GetPixel(), r1, new Color(232, 220, 192));
            UIShaders.DrawInsetShadow(batch, r1, new Color(0, 0, 0, 180), 20);

            // Inset 2: bigger, deeper
            int ry2 = ry + 220;
            var r2 = new Rectangle(rx - 160, ry2 - 80, 320, 160);
            batch.Draw(UIShaders.GetPixel(), r2, new Color(140, 110, 70));
            UIShaders.DrawInsetShadow(batch, r2, new Color(20, 10, 5, 220), 40);

            // Inset 3: narrow tall (test non-square aspect)
            int ry3 = ry + 440;
            var r3 = new Rectangle(rx - 60, ry3 - 90, 120, 180);
            batch.Draw(UIShaders.GetPixel(), r3, new Color(200, 160, 100));
            UIShaders.DrawInsetShadow(batch, r3, new Color(0, 0, 0, 180), 30);
        };
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;
        if (_phase == 0 && _elapsed > 0.5f)
        {
            if (UIShaders?.RectShadow == null)
            {
                DebugLog.Log(ScenarioLog, "FAIL: RectShadow effect did not load");
                _failCode = 1;
                _complete = true;
                return;
            }
            DeferredScreenshot = "ui_rect_shadow_test";
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
