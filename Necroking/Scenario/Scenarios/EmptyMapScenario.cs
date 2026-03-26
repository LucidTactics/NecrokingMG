using Necroking.Core;
using Necroking.GameSystems;

namespace Necroking.Scenario.Scenarios;

public class EmptyMapScenario : ScenarioBase
{
    public override string Name => "empty_map";
    private bool _complete;
    private int _tickCount;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Empty Map Scenario ===");
        DebugLog.Log(ScenarioLog, $"Grid: {sim.Grid.Width}x{sim.Grid.Height}");
        DebugLog.Log(ScenarioLog, $"Units: {sim.Units.Count}");
        ZoomOnLocation(10f, 10f, 32f);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _tickCount++;
        if (_tickCount >= 10)
        {
            DeferredScreenshot = "empty_map";
            _complete = true;
        }
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        bool pass = sim.Units.Count == 0;
        DebugLog.Log(ScenarioLog, $"Verification: units={sim.Units.Count} (expected 0) → {(pass ? "PASS" : "FAIL")}");
        return pass ? 0 : 1;
    }
}
