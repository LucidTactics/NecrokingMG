using Necroking.Core;
using Necroking.GameSystems;

namespace Necroking.Scenario.Scenarios;

public class UIEmptyScenario : UIScenarioBase
{
    public override string Name => "UIEmpty";
    private float _elapsed;
    private bool _complete;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== UI Empty Scenario ===");
        DebugLog.Log(ScenarioLog, "Verifying UI/HUD renders on empty map");
        ZoomOnLocation(10f, 10f, 32f);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;
        if (_elapsed > 1f && !_complete)
        {
            DeferredScreenshot = "ui_empty";
            _complete = true;
        }
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "UI Empty test completed — screenshot taken");
        return 0;
    }
}
