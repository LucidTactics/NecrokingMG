using System;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Movement;

namespace Necroking.Scenario.Scenarios;

public class FleeWhenHitScenario : ScenarioBase
{
    public override string Name => "flee_when_hit";
    private float _elapsed;
    private bool _complete;
    private const float TestDuration = 8f;

    // Unit stable IDs
    private uint _deerID;
    private uint _soldierID;

    // Starting positions for validation
    private Vec2 _deerStartPos;
    private Vec2 _soldierStartPos;

    // Tracking state for logging
    private bool _fleeStartLogged;
    private bool _lastAttackerSetLogged;
    private float _logTimer;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== FleeWhenHit Scenario ===");
        DebugLog.Log(ScenarioLog, "Tests deer-like FleeWhenHit AI: unit flees away from attacker when hit");

        var units = sim.UnitsMut;

        // Spawn "deer" — a skeleton with FleeWhenHit AI and Animal faction
        // Give it extra HP so it survives long enough for the flee AI to trigger
        _deerStartPos = new Vec2(32f, 32f);
        int deerIdx = units.AddUnit(_deerStartPos, UnitType.Skeleton);
        units.AI[deerIdx] = AIBehavior.FleeWhenHit;
        units.Faction[deerIdx] = Faction.Animal;
        units.Stats[deerIdx].HP = 100;
        units.Stats[deerIdx].MaxHP = 100;
        _deerID = units.Id[deerIdx];
        DebugLog.Log(ScenarioLog, $"Deer (Skeleton/FleeWhenHit/Animal) spawned at ({_deerStartPos.X:F1}, {_deerStartPos.Y:F1}), id={_deerID}, HP={units.Stats[deerIdx].HP}");

        // Spawn soldier with AttackClosest AI — it will path toward and attack the deer
        _soldierStartPos = new Vec2(37f, 32f);
        int soldierIdx = units.AddUnit(_soldierStartPos, UnitType.Soldier);
        units.AI[soldierIdx] = AIBehavior.AttackClosest;
        _soldierID = units.Id[soldierIdx];
        DebugLog.Log(ScenarioLog, $"Soldier (AttackClosest/Human) spawned at ({_soldierStartPos.X:F1}, {_soldierStartPos.Y:F1}), id={_soldierID}");

        float initialDistance = (_deerStartPos - _soldierStartPos).Length();
        DebugLog.Log(ScenarioLog, $"Initial distance between deer and soldier: {initialDistance:F1}");
        DebugLog.Log(ScenarioLog, $"Total units spawned: {units.Count}");

        ZoomOnLocation(32f, 32f, 20f);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;

        var units = sim.Units;
        int deerIdx = FindByID(units, _deerID);
        int soldierIdx = FindByID(units, _soldierID);

        // Log positions every 0.5 seconds
        _logTimer -= dt;
        if (_logTimer <= 0f)
        {
            _logTimer = 0.5f;
            LogPositions(units, deerIdx, soldierIdx);
        }

        // Track when LastAttackerID gets set on the deer
        if (deerIdx >= 0 && !_lastAttackerSetLogged && units.LastAttackerID[deerIdx] != GameConstants.InvalidUnit)
        {
            _lastAttackerSetLogged = true;
            DebugLog.Log(ScenarioLog, $"t={_elapsed:F2}s: *** Deer LastAttackerID set to {units.LastAttackerID[deerIdx]} (soldier id={_soldierID}) ***");
        }

        // Track when FleeTimer starts counting (flee begins)
        if (deerIdx >= 0 && !_fleeStartLogged && units.FleeTimer[deerIdx] > 0f)
        {
            _fleeStartLogged = true;
            var deerPos = units.Position[deerIdx];
            float distFromStart = (deerPos - _deerStartPos).Length();
            DebugLog.Log(ScenarioLog, $"t={_elapsed:F2}s: *** Deer started fleeing! FleeTimer={units.FleeTimer[deerIdx]:F1}, pos=({deerPos.X:F1},{deerPos.Y:F1}), distFromStart={distFromStart:F1} ***");
        }

        if (_elapsed >= TestDuration)
            _complete = true;
    }

    private void LogPositions(UnitArrays units, int deerIdx, int soldierIdx)
    {
        if (deerIdx >= 0)
        {
            var deerPos = units.Position[deerIdx];
            float distFromStart = (deerPos - _deerStartPos).Length();
            bool alive = units.Alive[deerIdx];
            float fleeTimer = units.FleeTimer[deerIdx];
            uint lastAttacker = units.LastAttackerID[deerIdx];
            bool inCombat = units.InCombat[deerIdx];
            int hp = units.Stats[deerIdx].HP;

            DebugLog.Log(ScenarioLog,
                $"t={_elapsed:F1}s: Deer pos=({deerPos.X:F1},{deerPos.Y:F1}) distFromStart={distFromStart:F1} " +
                $"alive={alive} hp={hp} fleeTimer={fleeTimer:F1} lastAttacker={lastAttacker} inCombat={inCombat}");
        }
        else
        {
            DebugLog.Log(ScenarioLog, $"t={_elapsed:F1}s: Deer NOT FOUND (dead/removed)");
        }

        if (soldierIdx >= 0)
        {
            var soldierPos = units.Position[soldierIdx];
            bool alive = units.Alive[soldierIdx];
            bool inCombat = units.InCombat[soldierIdx];
            int hp = units.Stats[soldierIdx].HP;

            float distToDeer = deerIdx >= 0 ? (units.Position[deerIdx] - soldierPos).Length() : -1f;

            DebugLog.Log(ScenarioLog,
                $"t={_elapsed:F1}s: Soldier pos=({soldierPos.X:F1},{soldierPos.Y:F1}) " +
                $"alive={alive} hp={hp} inCombat={inCombat} distToDeer={distToDeer:F1}");
        }
        else
        {
            DebugLog.Log(ScenarioLog, $"t={_elapsed:F1}s: Soldier NOT FOUND (dead/removed)");
        }
    }

    private int FindByID(UnitArrays units, uint id)
    {
        for (int i = 0; i < units.Count; i++)
            if (units.Id[i] == id) return i;
        return -1;
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "=== FleeWhenHit Validation ===");

        var units = sim.Units;
        int deerIdx = FindByID(units, _deerID);
        int soldierIdx = FindByID(units, _soldierID);

        // Check 1: Did the LastAttackerID ever get set? (deer was engaged)
        bool wasEngaged = _lastAttackerSetLogged;
        DebugLog.Log(ScenarioLog, $"Deer was engaged (LastAttackerID set): {wasEngaged} -> {(wasEngaged ? "PASS" : "FAIL")}");

        // Check 2: Did the flee behavior trigger?
        bool fleeTriggered = _fleeStartLogged;
        DebugLog.Log(ScenarioLog, $"Flee behavior triggered (FleeTimer > 0): {fleeTriggered} -> {(fleeTriggered ? "PASS" : "FAIL")}");

        // Check 3: Deer fled sufficiently far from start OR far from soldier
        bool deerFled = false;
        if (deerIdx >= 0 && units.Alive[deerIdx])
        {
            var deerPos = units.Position[deerIdx];
            float distFromStart = (deerPos - _deerStartPos).Length();
            float distFromSoldier = soldierIdx >= 0 ? (deerPos - units.Position[soldierIdx]).Length() : 999f;

            DebugLog.Log(ScenarioLog, $"Deer final pos: ({deerPos.X:F1},{deerPos.Y:F1})");
            DebugLog.Log(ScenarioLog, $"Deer distance from start: {distFromStart:F1} (need > 5.0)");
            DebugLog.Log(ScenarioLog, $"Deer distance from soldier: {distFromSoldier:F1} (need > 10.0)");

            deerFled = distFromStart > 5f || distFromSoldier > 10f;
            DebugLog.Log(ScenarioLog, $"Deer fled sufficiently: {deerFled} -> {(deerFled ? "PASS" : "FAIL")}");
        }
        else
        {
            // Deer died — flee behavior might have worked but deer was killed
            DebugLog.Log(ScenarioLog, "Deer is dead/removed — checking if flee was at least attempted");
            deerFled = fleeTriggered; // Count as pass if flee triggered before death
            DebugLog.Log(ScenarioLog, $"Deer fled (flee triggered before death): {deerFled} -> {(deerFled ? "PASS" : "FAIL")}");
        }

        // Summary
        int aliveCount = 0;
        for (int i = 0; i < units.Count; i++)
            if (units.Alive[i]) aliveCount++;

        DebugLog.Log(ScenarioLog, $"Final state: {aliveCount} units alive, elapsed={_elapsed:F1}s");
        DebugLog.Log(ScenarioLog, $"Combat log entries: {sim.CombatLog.Entries.Count}");

        bool pass = wasEngaged && fleeTriggered && deerFled;
        DebugLog.Log(ScenarioLog, $"Overall: {(pass ? "PASS" : "FAIL")} (engaged={wasEngaged}, fleeTriggered={fleeTriggered}, deerFled={deerFled})");
        return pass ? 0 : 1;
    }
}
