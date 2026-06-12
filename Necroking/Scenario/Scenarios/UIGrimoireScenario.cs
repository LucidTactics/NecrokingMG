using Microsoft.Xna.Framework;
using Necroking.Core;
using Necroking.GameSystems;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// [UI test] First-pass renders of the GodMenu3 imports: GrimoireWindow +
/// the three spell tooltips, at fixed crops for capture comparison.
/// Run with --resolution 1700x1250. Screenshot: ui_grimoire.png.
/// </summary>
public class UIGrimoireScenario : ScenarioBase
{
    public override string Name => "UIGrimoire";
    public override bool WantsWidgetRenderer => true;

    private float _elapsed;
    private int _phase;
    private bool _complete;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== UI Grimoire Scenario ===");
        ZoomOnLocation(10f, 10f, 32f);
        BackgroundColor = new Color(58, 62, 72);
        BloomOverride = new Data.Registries.BloomSettings { Enabled = false };

        CustomUIDraw = (batch, screenW, screenH) =>
        {
            if (WidgetRenderer == null) return;
            WidgetRenderer.DrawWidget("GrimoireWindow", 10, 10);
            WidgetRenderer.DrawWidget("SpiritFormTip", 730, 10);
            WidgetRenderer.DrawWidget("ShadowBoltTip", 730, 300);
            WidgetRenderer.DrawWidget("SummonWolvesTip", 730, 660);
        };
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;
        if (_phase == 0 && _elapsed > 0.5f)
        {
            DeferredScreenshot = "ui_grimoire";
            _phase = 1;
        }
        else if (_phase == 1 && DeferredScreenshot == null && LaunchArgs.Headless)
        {
            _complete = true;
        }
    }

    public override bool IsComplete => _complete;
    public override int OnComplete(Simulation sim) => 0;
}
