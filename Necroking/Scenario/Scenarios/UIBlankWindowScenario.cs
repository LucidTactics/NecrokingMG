using Microsoft.Xna.Framework;
using Necroking.Core;
using Necroking.GameSystems;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Renders the blank UnitTooltipWindow widget (leather background + thin gold
/// frame, both harmonized) centered on screen and screenshots it. This is the
/// base window for the Unity Unit Tooltip2 panel recreation — the harmonize
/// values are copied from the Unity MaterialColorSwapperHandler settings on
/// WindowInner (target 31,24,17 / sat .756 / val .588) and WindowBorder
/// (target 111,92,57 / sat .75 / val .665).
/// </summary>
public class UIBlankWindowScenario : ScenarioBase
{
    public override string Name => "UIBlankWindow";
    public override bool WantsWidgetRenderer => true;

    private const int WindowW = 468;
    private const int WindowH = 745;

    private float _elapsed;
    private int _phase;
    private bool _complete;
    private int _failCode;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== UI Blank Window Scenario ===");
        ZoomOnLocation(10f, 10f, 32f);
        // Neutral cool backdrop so the warm brown window reads clearly.
        BackgroundColor = new Color(58, 62, 72);
        BloomOverride = new Data.Registries.BloomSettings { Enabled = false };

        CustomUIDraw = (batch, screenW, screenH) =>
        {
            if (WidgetRenderer == null) return;
            int x = (screenW - WindowW) / 2;
            int y = (screenH - WindowH) / 2;
            WidgetRenderer.DrawWidget("UnitTooltipWindow", x, y);
            // Side panels (CityMenu3 ports) at fixed spots for screenshot crops
            WidgetRenderer.DrawWidget("CommanderEquipWindow", 60, 80);
            WidgetRenderer.DrawWidget("StatTooltipWindow", 950, 80);
            WidgetRenderer.DrawWidget("ResourceTooltipWindow", 950, 250);
        };
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;
        if (_phase == 0 && _elapsed > 0.5f)
        {
            if (WidgetRenderer == null)
            {
                DebugLog.Log(ScenarioLog, "FAIL: WidgetRenderer not plumbed");
                _failCode = 1;
                _complete = true;
                return;
            }
            DebugLog.Log(ScenarioLog,
                $"Requesting screenshot: window {WindowW}x{WindowH} centered, " +
                "widget=UnitTooltipWindow (bg=LeatherBackground scale 0.14, frame=RenaiThinBorder)");
            DeferredScreenshot = "ui_blank_window";
            _phase = 1;
        }
        else if (_phase == 1 && DeferredScreenshot == null)
        {
            // Headless (automated) runs finish immediately; in windowed mode stay
            // up for visual inspection until --timeout closes the game.
            if (LaunchArgs.Headless)
            {
                DebugLog.Log(ScenarioLog, "Screenshot taken, scenario complete");
                _complete = true;
            }
        }
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, $"Result: {(_failCode == 0 ? "PASS" : $"FAIL ({_failCode})")}");
        return _failCode;
    }
}
