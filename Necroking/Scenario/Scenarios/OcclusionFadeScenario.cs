using System;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Lib;
using Necroking.World;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Visual test for occlusion fade: a tall tree directly in front of the
/// necromancer should turn semi-transparent (player visible through it) while
/// an identical tree off to the side stays fully opaque.
/// </summary>
public class OcclusionFadeScenario : ScenarioBase
{
    public override string Name => "occlusion_fade";

    private bool _complete;
    private int _frame;
    private int _step;
    private int _screenshotCount;
    private float _cx, _cy;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, $"=== Scenario: {Name} ===");
        DebugLog.Log(ScenarioLog, "Occlusion fade: tree in front of the necromancer goes semi-transparent.");

        _cx = 32f;
        _cy = 32f;

        sim.UnitsMut.AddUnit(new Vec2(_cx, _cy), UnitType.Necromancer);

        var env = sim.EnvironmentSystem;
        if (env == null)
        {
            DebugLog.Log(ScenarioLog, "FAIL: no environment system");
            _complete = true;
            return;
        }

        var treeDef = new EnvironmentObjectDef
        {
            Id = "occ_tree", Name = "Occlusion Tree",
            TexturePath = GamePaths.Resolve("assets/Environment/Trees/BranchlessTree1.png"),
            SpriteWorldHeight = 6f,
            PivotX = 0.5f, PivotY = 1f,
            Scale = 1f
        };
        int defIdx = env.AddDef(treeDef);
        // Occluder: just in front of (higher Y than) the necromancer, trunk on
        // top of him in screen space.
        env.AddObject((ushort)defIdx, _cx + 0.3f, _cy + 1.5f);
        // Control: same tree, well off to the side.
        env.AddObject((ushort)defIdx, _cx + 8f, _cy + 1.5f);
        DebugLog.Log(ScenarioLog, "Placed occluding tree (front of necro) + control tree (side)");

        ZoomOnLocation(_cx + 3f, _cy, 50f);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        if (_complete) return;
        if (DeferredScreenshot != null) return;

        // Let the fade lerp settle (~1.5 s).
        if (_frame < 90) { _frame++; return; }

        (string Name, string Desc, float X, float Y, float Zoom)[] steps =
        {
            ("occlusion_both", "Occluding tree (faded) vs control tree (opaque)", _cx + 4f, _cy, 40f),
            ("occlusion_close", "Close-up: necromancer visible through the faded tree", _cx, _cy, 70f),
        };

        int frameInStep = (_frame - 90) % 3;

        if (_step < steps.Length)
        {
            if (frameInStep == 0)
            {
                ZoomOnLocation(steps[_step].X, steps[_step].Y, steps[_step].Zoom);
                DebugLog.Log(ScenarioLog, $"Step {_step}: {steps[_step].Desc}");
            }
            else if (frameInStep == 2)
            {
                DeferredScreenshot = steps[_step].Name;
                _screenshotCount++;
                _step++;
            }
        }

        if (_step >= steps.Length) _complete = true;
        _frame++;
    }

    public override bool IsComplete => _complete && DeferredScreenshot == null;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, $"=== Occlusion fade test complete: {_screenshotCount} screenshots ===");
        return _screenshotCount >= 2 ? 0 : 1;
    }
}
