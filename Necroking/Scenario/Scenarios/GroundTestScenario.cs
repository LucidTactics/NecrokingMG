using Necroking.Core;
using Necroking.GameSystems;

namespace Necroking.Scenario.Scenarios;

public class GroundTestScenario : ScenarioBase
{
    public override string Name => "ground_test";
    public override bool WantsGround => true;

    private bool _complete;
    private int _tickCount;
    private int _screenshotsTaken;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "=== Ground Test Scenario ===");
        DebugLog.Log(ScenarioLog, $"Grid: {sim.Grid.Width}x{sim.Grid.Height}");

        // Place some different ground types for visual variety
        // The ground system is accessed via Game1, but we can log what we expect
        DebugLog.Log(ScenarioLog, "Expected ground types: grass (0), dirt (1), cobblestone (2)");
        DebugLog.Log(ScenarioLog, "Ground filled with type 0 (grass) by default");

        // Start zoomed in on a visible area
        ZoomOnLocation(32f, 32f, 48f);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _tickCount++;

        if (_tickCount == 5 && _screenshotsTaken == 0)
        {
            // Take overview screenshot
            ZoomOnLocation(32f, 32f, 48f);
            DeferredScreenshot = "ground_overview";
            _screenshotsTaken++;
            DebugLog.Log(ScenarioLog, "Taking ground_overview screenshot at tick 5");
        }
        else if (_tickCount == 10 && _screenshotsTaken == 1)
        {
            // Close-up
            ZoomOnLocation(32f, 32f, 128f);
            DeferredScreenshot = "ground_closeup";
            _screenshotsTaken++;
            DebugLog.Log(ScenarioLog, "Taking ground_closeup screenshot at tick 10");
        }
        else if (_tickCount >= 15)
        {
            _complete = true;
        }
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "=== Ground Test Complete ===");
        DebugLog.Log(ScenarioLog, $"Ticks: {_tickCount}, Screenshots: {_screenshotsTaken}");
        // Always pass - this is a visual test, check screenshots manually
        return 0;
    }
}
