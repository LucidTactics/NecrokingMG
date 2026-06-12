using Microsoft.Xna.Framework;
using Necroking.Core;
using Necroking.GameSystems;
using Necroking.UI;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// [UI test] Auto-size ResourceTooltipDyn at three row counts (1, 4, 9) side
/// by side — verifies rows collapse, sections stack, image layers crop, and
/// the panel height tracks content. Screenshot: ui_resourcetip_dyn.png.
/// </summary>
public class UIResourceTipDynScenario : ScenarioBase
{
    public override string Name => "UIResourceTipDyn";
    public override bool WantsWidgetRenderer => true;

    private float _elapsed;
    private int _phase;
    private bool _complete;
    private bool _bound;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== UI Resource Tooltip Dyn Scenario ===");
        ZoomOnLocation(10f, 10f, 32f);
        BackgroundColor = new Color(58, 62, 72);
        BloomOverride = new Data.Registries.BloomSettings { Enabled = false };

        CustomUIDraw = (batch, screenW, screenH) =>
        {
            var r = WidgetRenderer;
            if (r == null) return;
            if (!_bound)
            {
                _bound = true;
                ResourceTooltip.Bind(r, "rtdA", "Gold Income", "12", ResourceTooltip.ValueGreen,
                    new[] { ResourceTooltip.Entry("Taxes", 12) },
                    "");
                ResourceTooltip.Bind(r, "rtdB", "Human Population", "20", ResourceTooltip.ValueGreen,
                    new[]
                    {
                        new ResourceTooltip.Row("(Population Cap)", "200", ResourceTooltip.ValueDefault),
                        new ResourceTooltip.Row("Total Population", "100", ResourceTooltip.ValueGreen),
                        new ResourceTooltip.Row("Allocated", "80", ResourceTooltip.ValueRed),
                        new ResourceTooltip.Row("Growth", "+10", ResourceTooltip.ValueGreen),
                    },
                    "The population of humans in your town. Only available humans can be used for new assignments.");
                ResourceTooltip.Bind(r, "rtdC", "Defense Rating", "37", ResourceTooltip.ValueDefault,
                    new[]
                    {
                        ResourceTooltip.Entry("Walls", 14),
                        ResourceTooltip.Entry("Garrison", 9),
                        ResourceTooltip.Entry("Towers", 6),
                        ResourceTooltip.Entry("Militia", 4),
                        ResourceTooltip.Entry("Moat", 3),
                        ResourceTooltip.Entry("Traps", 2),
                        ResourceTooltip.Entry("Low Morale", -1),
                        ResourceTooltip.Entry("Disrepair", -3),
                        ResourceTooltip.Entry("Siege Damage", -7),
                    },
                    "Overall fortification strength of this settlement.");
                foreach (var inst in new[] { "rtdA", "rtdB", "rtdC" })
                    DebugLog.Log(ScenarioLog,
                        $"{inst}: measured height = {r.MeasureWidgetHeight(ResourceTooltip.WidgetId, inst)}");
            }
            r.DrawWidget(ResourceTooltip.WidgetId, 60, 80, "rtdA");
            r.DrawWidget(ResourceTooltip.WidgetId, 340, 80, "rtdB");
            r.DrawWidget(ResourceTooltip.WidgetId, 620, 80, "rtdC");
        };
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;
        if (_phase == 0 && _elapsed > 0.5f)
        {
            DeferredScreenshot = "ui_resourcetip_dyn";
            _phase = 1;
        }
        else if (_phase == 1 && DeferredScreenshot == null && LaunchArgs.Headless)
        {
            DebugLog.Log(ScenarioLog, "screenshot taken, complete");
            _complete = true;
        }
    }

    public override bool IsComplete => _complete;
    public override int OnComplete(Simulation sim) => 0;
}
