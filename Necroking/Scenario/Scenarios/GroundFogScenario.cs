using System;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.World;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Visual test for the depth-stamped ground-fog volume: units standing in and
/// walking through a fog bank. The design goal — "torso above the fog, legs
/// swallowed" — hinges on wisps depth-testing against the unit occluder stamps,
/// so this scenario forces DepthSortedFog on, spawns a fog bank with units at
/// several depths inside it, and screenshots overview + close-ups.
/// </summary>
public class GroundFogScenario : ScenarioBase
{
    public override string Name => "ground_fog";

    private bool _complete;
    private int _frame;
    private int _step;
    private int _screenshotCount;
    private float _cx, _cy;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, $"=== Scenario: {Name} ===");
        DebugLog.Log(ScenarioLog, "Ground-fog volume: wisps depth-tested against unit stamps.");

        // The wisp depth test needs the occluder stamps — force the perf toggle
        // on for this run regardless of user settings.
        sim.GameData.Settings.Performance.DepthSortedFog = true;

        _cx = 32f;
        _cy = 32f;

        if (GroundFog == null)
        {
            DebugLog.Log(ScenarioLog, "FAIL: GroundFog system not plumbed");
            _complete = true;
            return;
        }

        // One dense bank; units at varying depths inside it, plus one outside
        // as a control.
        GroundFog.SpawnBank(new Vec2(_cx, _cy), radius: 10f, density: 0.85f);
        DebugLog.Log(ScenarioLog, $"Spawned fog bank at ({_cx},{_cy}) r=10 d=0.85");

        // All same faction (Human) so nobody fights or moves — the close-up
        // camera spots assume units stay on their spawn points.
        sim.UnitsMut.AddUnit(new Vec2(_cx - 3f, _cy - 3f), UnitType.Militia);   // back of bank
        sim.UnitsMut.AddUnit(new Vec2(_cx, _cy), UnitType.Knight);              // center
        sim.UnitsMut.AddUnit(new Vec2(_cx + 2f, _cy + 3f), UnitType.Soldier);   // front of bank
        sim.UnitsMut.AddUnit(new Vec2(_cx + 14f, _cy), UnitType.Militia);       // outside (control)
        DebugLog.Log(ScenarioLog, "Spawned units: militia (back), knight (center), soldier (front), militia (outside)");

        ZoomOnLocation(_cx, _cy, 36f);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        if (_complete) return;
        if (DeferredScreenshot != null) return;

        // Let the fog build up (wisps stagger-spawn 4/frame and fade in) before
        // the first shot: ~180 frames ≈ 3 s of fog accumulation.
        if (_frame < 180) { _frame++; return; }

        (string Name, string Desc, float X, float Y, float Zoom)[] steps =
        {
            ("ground_fog_overview", "Overview: bank with units at three depths", _cx, _cy, 30f),
            ("ground_fog_knight", "Close-up: knight standing center-bank (legs swallowed)", _cx, _cy, 80f),
            ("ground_fog_front", "Close-up: soldier at front edge (wisps pass in front)", _cx + 2f, _cy + 3f, 80f),
            ("ground_fog_control", "Control: militia outside the bank (no fog)", _cx + 14f, _cy, 80f),
        };

        int frameInStep = (_frame - 180) % 3;

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
        GroundFog?.Clear();
        DebugLog.Log(ScenarioLog, $"=== Ground fog test complete: {_screenshotCount} screenshots ===");
        return _screenshotCount >= 4 ? 0 : 1;
    }
}
