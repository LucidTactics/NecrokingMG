using System;
using System.Collections.Generic;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Movement;

namespace Necroking.Scenario.Scenarios;

public class RetargetScenario : ScenarioBase
{
    public override string Name => "retarget";

    private float _elapsed;
    private bool _complete;
    private const float TestDuration = 15f;

    private uint _skeletonId;
    private uint _soldierAId;
    private uint _soldierBId;

    private float _logTimer;
    private int _targetChangeCount;
    private CombatTarget _lastObservedTarget;
    private readonly List<string> _targetHistory = new();
    private bool _soldierARemoved;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Retarget Scenario ===");
        DebugLog.Log(ScenarioLog, "Testing: Skeleton with AttackClosestRetarget AI retargets when conditions change");
        DebugLog.Log(ScenarioLog, "Setup: 1 skeleton at (15,15), soldier A at (20,15), soldier B at (25,15)");
        DebugLog.Log(ScenarioLog, "After ~5s, soldier A is killed/removed -> skeleton should retarget to B");

        var units = sim.UnitsMut;

        // Spawn skeleton with AttackClosestRetarget AI — high HP so it survives long enough to test retargeting
        int skelIdx = units.AddUnit(new Vec2(15f, 15f), UnitType.Skeleton);
        units.AI[skelIdx] = AIBehavior.AttackClosestRetarget;
        units.RetargetTimer[skelIdx] = 0f; // start ready to pick a target immediately
        units.Stats[skelIdx].MaxHP = 9999;
        units.Stats[skelIdx].HP = 9999;
        _skeletonId = units.Id[skelIdx];
        DebugLog.Log(ScenarioLog, $"Skeleton id={_skeletonId} at (15,15) with AttackClosestRetarget AI, HP=9999");

        // Spawn soldier A (closer to skeleton) with AttackClosest AI
        int solAIdx = units.AddUnit(new Vec2(20f, 15f), UnitType.Soldier);
        units.AI[solAIdx] = AIBehavior.AttackClosest;
        _soldierAId = units.Id[solAIdx];
        DebugLog.Log(ScenarioLog, $"Soldier A id={_soldierAId} at (20,15) with AttackClosest AI");

        // Spawn soldier B (farther from skeleton) with AttackClosest AI
        int solBIdx = units.AddUnit(new Vec2(25f, 15f), UnitType.Soldier);
        units.AI[solBIdx] = AIBehavior.AttackClosest;
        _soldierBId = units.Id[solBIdx];
        DebugLog.Log(ScenarioLog, $"Soldier B id={_soldierBId} at (25,15) with AttackClosest AI");

        _lastObservedTarget = CombatTarget.None;
        DebugLog.Log(ScenarioLog, $"RetargetTimer resets every 2s per simulation code");

        ZoomOnLocation(20f, 15f, 30f);
    }

    private int FindByID(UnitArrays units, uint id)
    {
        for (int i = 0; i < units.Count; i++)
            if (units.Id[i] == id) return i;
        return -1;
    }

    private string DescribeTarget(CombatTarget target)
    {
        if (target.IsNone) return "None";
        if (target.IsUnit)
        {
            uint tid = target.UnitID;
            if (tid == _soldierAId) return $"SoldierA(id={tid})";
            if (tid == _soldierBId) return $"SoldierB(id={tid})";
            return $"Unit(id={tid})";
        }
        return $"{target.Kind}({target.Value})";
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;

        var units = sim.UnitsMut;
        int skelIdx = FindByID(units, _skeletonId);

        if (skelIdx < 0 || !units.Alive[skelIdx])
        {
            DebugLog.Log(ScenarioLog, $"t={_elapsed:F1}s: Skeleton lost or dead, ending scenario");
            _complete = true;
            return;
        }

        // After ~5 seconds, kill soldier A to force retargeting
        if (_elapsed >= 5f && !_soldierARemoved)
        {
            int solAIdx = FindByID(units, _soldierAId);
            if (solAIdx >= 0 && units.Alive[solAIdx])
            {
                units.Alive[solAIdx] = false;
                DebugLog.Log(ScenarioLog, $"t={_elapsed:F1}s: KILLED Soldier A (id={_soldierAId}) to force retarget");
                DebugLog.Log(ScenarioLog, $"  Skeleton's current target: {DescribeTarget(units.Target[skelIdx])}");
                DebugLog.Log(ScenarioLog, $"  RetargetTimer: {units.RetargetTimer[skelIdx]:F2}s");
            }
            else
            {
                DebugLog.Log(ScenarioLog, $"t={_elapsed:F1}s: Soldier A already dead/missing (id={_soldierAId})");
            }
            _soldierARemoved = true;
        }

        // Track target changes
        var currentTarget = units.Target[skelIdx];
        if (currentTarget != _lastObservedTarget)
        {
            string from = DescribeTarget(_lastObservedTarget);
            string to = DescribeTarget(currentTarget);
            // Don't count None->first target as a "change" for validation,
            // but do count any target->different target
            if (!_lastObservedTarget.IsNone)
            {
                _targetChangeCount++;
                DebugLog.Log(ScenarioLog, $"t={_elapsed:F1}s: TARGET CHANGED #{_targetChangeCount}: {from} -> {to}");
            }
            else
            {
                DebugLog.Log(ScenarioLog, $"t={_elapsed:F1}s: Initial target acquired: {to}");
            }
            _targetHistory.Add($"t={_elapsed:F1}s: {from}->{to}");
            _lastObservedTarget = currentTarget;
        }

        // Periodic logging every 1 second
        _logTimer -= dt;
        if (_logTimer <= 0f)
        {
            _logTimer = 1f;
            var pos = units.Position[skelIdx];
            float retargetTimer = units.RetargetTimer[skelIdx];
            bool inCombat = units.InCombat[skelIdx];

            // Check distances to soldiers
            int solAIdx = FindByID(units, _soldierAId);
            int solBIdx = FindByID(units, _soldierBId);
            float distA = (solAIdx >= 0 && units.Alive[solAIdx])
                ? (units.Position[solAIdx] - pos).Length() : -1f;
            float distB = (solBIdx >= 0 && units.Alive[solBIdx])
                ? (units.Position[solBIdx] - pos).Length() : -1f;

            DebugLog.Log(ScenarioLog,
                $"t={_elapsed:F1}s: skelPos=({pos.X:F1},{pos.Y:F1}) target={DescribeTarget(currentTarget)} " +
                $"retargetTimer={retargetTimer:F2} inCombat={inCombat} " +
                $"distA={distA:F1} distB={distB:F1} changes={_targetChangeCount}");
        }

        if (_elapsed >= TestDuration)
            _complete = true;
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "=== Retarget Validation ===");

        DebugLog.Log(ScenarioLog, $"Target change count: {_targetChangeCount}");
        DebugLog.Log(ScenarioLog, "Target history:");
        foreach (var entry in _targetHistory)
            DebugLog.Log(ScenarioLog, $"  {entry}");

        // Check skeleton final state
        var units = sim.Units;
        int skelIdx = FindByID(sim.UnitsMut, _skeletonId);
        if (skelIdx >= 0 && units.Alive[skelIdx])
        {
            var pos = units.Position[skelIdx];
            var target = units.Target[skelIdx];
            DebugLog.Log(ScenarioLog, $"Skeleton final pos=({pos.X:F1},{pos.Y:F1}), target={DescribeTarget(target)}");
        }
        else
        {
            DebugLog.Log(ScenarioLog, "Skeleton dead/missing at validation");
        }

        // Check soldier states
        int solAIdx = FindByID(sim.UnitsMut, _soldierAId);
        bool solAAlive = solAIdx >= 0 && units.Alive[solAIdx];
        int solBIdx = FindByID(sim.UnitsMut, _soldierBId);
        bool solBAlive = solBIdx >= 0 && units.Alive[solBIdx];
        DebugLog.Log(ScenarioLog, $"Soldier A alive: {solAAlive}, Soldier B alive: {solBAlive}");

        DebugLog.Log(ScenarioLog, $"Soldier A was removed: {_soldierARemoved}");
        DebugLog.Log(ScenarioLog, $"Elapsed time: {_elapsed:F1}s");

        // Validation: skeleton changed target at least once
        bool retargetPass = _targetChangeCount >= 1;
        DebugLog.Log(ScenarioLog, $"Check — target changed at least once: {(retargetPass ? "PASS" : "FAIL")} (changes={_targetChangeCount})");

        bool pass = retargetPass;
        DebugLog.Log(ScenarioLog, $"Overall: {(pass ? "PASS" : "FAIL")}");
        return pass ? 0 : 1;
    }
}
