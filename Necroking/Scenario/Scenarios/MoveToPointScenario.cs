using System;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Movement;

namespace Necroking.Scenario.Scenarios;

public class MoveToPointScenario : ScenarioBase
{
    public override string Name => "move_to_point";

    private float _elapsed;
    private bool _complete;
    private const float TestDuration = 15f;
    private const float TargetReachDist = 2f;

    private uint _unitId;
    private Vec2 _startPos;
    private Vec2 _targetPos;
    private float _logTimer;
    private float _initialDist;
    private float _closestDist;
    private bool _reachedTarget;
    private bool _movingToward; // unit made meaningful progress

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== MoveToPoint Scenario ===");
        DebugLog.Log(ScenarioLog, "Testing: Skeleton with MoveToPoint AI moves from (5,5) to (25,25)");

        var units = sim.UnitsMut;

        _startPos = new Vec2(5f, 5f);
        _targetPos = new Vec2(25f, 25f);
        _initialDist = (_targetPos - _startPos).Length();
        _closestDist = _initialDist;

        int idx = units.AddUnit(_startPos, UnitType.Skeleton);
        units[idx].AI = AIBehavior.MoveToPoint;
        units[idx].MoveTarget = _targetPos;
        _unitId = units[idx].Id;

        DebugLog.Log(ScenarioLog, $"Spawned skeleton id={_unitId} at ({_startPos.X:F1},{_startPos.Y:F1})");
        DebugLog.Log(ScenarioLog, $"MoveTarget set to ({_targetPos.X:F1},{_targetPos.Y:F1})");
        DebugLog.Log(ScenarioLog, $"Initial distance: {_initialDist:F1} units");
        DebugLog.Log(ScenarioLog, $"Pass condition: reach within {TargetReachDist:F1} units of target");

        ZoomOnLocation(15f, 15f, 30f);
    }

    private int FindByID(UnitArrays units, uint id)
    {
        for (int i = 0; i < units.Count; i++)
            if (units[i].Id == id) return i;
        return -1;
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;

        var units = sim.Units;
        int idx = FindByID(sim.UnitsMut, _unitId);

        if (idx < 0 || !units[idx].Alive)
        {
            DebugLog.Log(ScenarioLog, $"t={_elapsed:F1}s: Unit lost or dead, ending scenario");
            _complete = true;
            return;
        }

        var pos = units[idx].Position;
        float dist = (pos - _targetPos).Length();

        // Track closest distance achieved
        if (dist < _closestDist)
            _closestDist = dist;

        // Check if we've made meaningful progress (moved at least 25% closer)
        if (_initialDist - dist > _initialDist * 0.25f)
            _movingToward = true;

        // Check if target reached
        if (dist <= TargetReachDist && !_reachedTarget)
        {
            _reachedTarget = true;
            DebugLog.Log(ScenarioLog, $"t={_elapsed:F1}s: TARGET REACHED! dist={dist:F2} (threshold={TargetReachDist:F1})");
            DebugLog.Log(ScenarioLog, $"  Final pos=({pos.X:F1},{pos.Y:F1}), target=({_targetPos.X:F1},{_targetPos.Y:F1})");
        }

        // Periodic logging every 1 second
        _logTimer -= dt;
        if (_logTimer <= 0f)
        {
            _logTimer = 1f;
            var vel = units[idx].Velocity;
            var prefVel = units[idx].PreferredVel;
            float progress = (_initialDist - dist) / _initialDist * 100f;
            DebugLog.Log(ScenarioLog,
                $"t={_elapsed:F1}s: pos=({pos.X:F1},{pos.Y:F1}) dist={dist:F1} " +
                $"vel=({vel.X:F1},{vel.Y:F1}) prefVel=({prefVel.X:F1},{prefVel.Y:F1}) " +
                $"progress={progress:F0}% closest={_closestDist:F1}");
        }

        // End conditions: reached target or timeout
        if (_reachedTarget || _elapsed >= TestDuration)
            _complete = true;
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "=== MoveToPoint Validation ===");

        var units = sim.Units;
        int idx = FindByID(sim.UnitsMut, _unitId);

        float finalDist = float.MaxValue;
        if (idx >= 0 && units[idx].Alive)
        {
            var pos = units[idx].Position;
            finalDist = (pos - _targetPos).Length();
            DebugLog.Log(ScenarioLog, $"Final position: ({pos.X:F1},{pos.Y:F1})");
            DebugLog.Log(ScenarioLog, $"Final distance to target: {finalDist:F2}");
        }
        else
        {
            DebugLog.Log(ScenarioLog, "Unit not found or dead at validation time");
        }

        DebugLog.Log(ScenarioLog, $"Closest distance achieved: {_closestDist:F2}");
        DebugLog.Log(ScenarioLog, $"Made meaningful progress: {_movingToward}");
        DebugLog.Log(ScenarioLog, $"Reached target: {_reachedTarget}");
        DebugLog.Log(ScenarioLog, $"Elapsed time: {_elapsed:F1}s");

        // Check 1: Unit reached within 2 units of target
        bool reachPass = _reachedTarget || finalDist <= TargetReachDist;
        DebugLog.Log(ScenarioLog, $"Check — reached within {TargetReachDist:F1} units: {(reachPass ? "PASS" : "FAIL")} (dist={finalDist:F2})");

        // Check 2: Unit was moving toward target (sanity check)
        DebugLog.Log(ScenarioLog, $"Check — moving toward target: {(_movingToward ? "PASS" : "FAIL")}");

        bool pass = reachPass;
        DebugLog.Log(ScenarioLog, $"Overall: {(pass ? "PASS" : "FAIL")}");
        return pass ? 0 : 1;
    }
}
