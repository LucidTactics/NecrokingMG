using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Tests that SpriteScale from UnitDef is applied correctly.
/// Spawns units at 0.5x, 1.0x, 1.5x, and 2.0x scale side by side.
/// Screenshots verify visual size differences.
/// </summary>
public class SpriteScaleScenario : ScenarioBase
{
    public override string Name => "sprite_scale";

    private bool _complete;
    private int _frame;
    private int _step;
    private float _cx, _cy;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, $"=== Scenario: {Name} ===");
        DebugLog.Log(ScenarioLog, "Testing SpriteScale at 0.5x, 1.0x, 1.5x, 2.0x");

        _cx = 15f;
        _cy = 15f;

        float[] scales = { 0.5f, 1.0f, 1.5f, 2.0f };
        for (int s = 0; s < scales.Length; s++)
        {
            float x = _cx - 6f + s * 4f;
            int idx = sim.UnitsMut.AddUnit(new Vec2(x, _cy), UnitType.Skeleton);
            sim.UnitsMut[idx].SpriteScale = scales[s];
            DebugLog.Log(ScenarioLog, $"Skeleton at ({x:F1}, {_cy:F1}) scale={scales[s]:F1} -> actual SpriteScale={sim.Units[idx].SpriteScale:F1}");
        }

        // Also spawn soldiers at different scales on second row
        for (int s = 0; s < scales.Length; s++)
        {
            float x = _cx - 6f + s * 4f;
            int idx = sim.UnitsMut.AddUnit(new Vec2(x, _cy + 5f), UnitType.Soldier);
            sim.UnitsMut[idx].SpriteScale = scales[s];
            DebugLog.Log(ScenarioLog, $"Soldier at ({x:F1}, {_cy + 5f:F1}) scale={scales[s]:F1} -> actual SpriteScale={sim.Units[idx].SpriteScale:F1}");
        }

        // Disable AI so units stand still
        for (int i = 0; i < sim.Units.Count; i++)
            sim.UnitsMut[i].AI = AIBehavior.IdleAtPoint;

        ZoomOnLocation(_cx, _cy + 2.5f, 40f);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        if (_complete) return;
        if (DeferredScreenshot != null) return;

        if (_frame == 2)
        {
            ZoomOnLocation(_cx, _cy + 2.5f, 40f);
        }
        else if (_frame == 4)
        {
            DeferredScreenshot = "scale_overview";
            _step++;
        }
        else if (_frame == 6)
        {
            // Close-up on the two smallest and two largest
            ZoomOnLocation(_cx - 6f, _cy, 80f);
        }
        else if (_frame == 8)
        {
            DeferredScreenshot = "scale_small";
            _step++;
        }
        else if (_frame == 10)
        {
            ZoomOnLocation(_cx + 2f, _cy, 80f);
        }
        else if (_frame == 12)
        {
            DeferredScreenshot = "scale_large";
            _step++;
        }
        else if (_frame == 14)
        {
            // Verify scale values are correct
            for (int i = 0; i < sim.Units.Count; i++)
            {
                DebugLog.Log(ScenarioLog, $"Unit {i}: type={sim.Units[i].Type} scale={sim.Units[i].SpriteScale:F2}");
            }
            _complete = true;
        }
        _frame++;
    }

    public override bool IsComplete => _complete && DeferredScreenshot == null;

    public override int OnComplete(Simulation sim)
    {
        // Verify we have units at different scales
        bool has05 = false, has10 = false, has15 = false, has20 = false;
        for (int i = 0; i < sim.Units.Count; i++)
        {
            float s = sim.Units[i].SpriteScale;
            if (System.MathF.Abs(s - 0.5f) < 0.01f) has05 = true;
            if (System.MathF.Abs(s - 1.0f) < 0.01f) has10 = true;
            if (System.MathF.Abs(s - 1.5f) < 0.01f) has15 = true;
            if (System.MathF.Abs(s - 2.0f) < 0.01f) has20 = true;
        }

        bool pass = has05 && has10 && has15 && has20;
        DebugLog.Log(ScenarioLog, $"Scale validation: 0.5x={has05} 1.0x={has10} 1.5x={has15} 2.0x={has20} -> {(pass ? "PASS" : "FAIL")}");
        DebugLog.Log(ScenarioLog, $"=== sprite_scale complete: {_step} screenshots ===");
        return pass ? 0 : 1;
    }
}
