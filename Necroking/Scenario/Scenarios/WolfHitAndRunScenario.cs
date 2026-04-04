using System;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Movement;

namespace Necroking.Scenario.Scenarios;

public class WolfHitAndRunScenario : ScenarioBase
{
    public override string Name => "wolf_hit_and_run";
    private float _elapsed;
    private bool _complete;
    private const float TestDuration = 20f;
    private const float MaxDuration = 25f;

    // Wolf tracking
    private uint[] _wolfIds = new uint[3];
    private byte[] _maxPhaseReached = new byte[3]; // track highest phase each wolf reached
    private int[] _phaseTransitions = new int[3];  // count phase transitions per wolf
    private byte[] _lastPhase = new byte[3];       // last observed phase per wolf

    // Soldier tracking
    private uint[] _soldierIds = new uint[2];

    // Combat tracking
    private int _lastCombatLogCount;
    private bool _combatDetected;
    private float _logTimer;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Wolf Hit-and-Run Scenario ===");
        DebugLog.Log(ScenarioLog, "Testing WolfHitAndRun AI: Engage(0) -> Attacking(1) -> Disengage(2) -> WaitCooldown(3) -> Engage(0)");

        var units = sim.UnitsMut;

        // Spawn 3 wolves (Skeleton units with WolfHitAndRun AI, Animal faction) around (10, 15)
        Vec2[] wolfPositions = { new Vec2(9f, 14f), new Vec2(10f, 16f), new Vec2(11f, 15f) };
        for (int w = 0; w < 3; w++)
        {
            int idx = units.AddUnit(wolfPositions[w], UnitType.Skeleton);
            units[idx].AI = AIBehavior.WolfHitAndRun;
            units[idx].Faction = Faction.Animal;
            _wolfIds[w] = units[idx].Id;
            _maxPhaseReached[w] = 0;
            _phaseTransitions[w] = 0;
            _lastPhase[w] = 0;
            DebugLog.Log(ScenarioLog, $"Wolf {w}: spawned at ({wolfPositions[w].X:F1}, {wolfPositions[w].Y:F1}), id={_wolfIds[w]}, AI=WolfHitAndRun, Faction=Animal");
        }

        // Spawn 2 soldiers (AttackClosest AI) at (20, 15) as targets
        Vec2[] soldierPositions = { new Vec2(20f, 14.5f), new Vec2(20f, 15.5f) };
        for (int s = 0; s < 2; s++)
        {
            int idx = units.AddUnit(soldierPositions[s], UnitType.Soldier);
            units[idx].AI = AIBehavior.AttackClosest;
            _soldierIds[s] = units[idx].Id;
            DebugLog.Log(ScenarioLog, $"Soldier {s}: spawned at ({soldierPositions[s].X:F1}, {soldierPositions[s].Y:F1}), id={_soldierIds[s]}, AI=AttackClosest");
        }

        DebugLog.Log(ScenarioLog, $"Total units spawned: {units.Count}");
        DebugLog.Log(ScenarioLog, $"Distance wolves->soldiers: ~10 units (should close quickly)");
        ZoomOnLocation(15f, 15f, 30f);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;
        var units = sim.Units;

        // Track wolf phase transitions every tick
        for (int w = 0; w < 3; w++)
        {
            int idx = FindByID(units, _wolfIds[w]);
            if (idx < 0) continue;

            byte currentPhase = units[idx].WolfPhase;
            if (currentPhase != _lastPhase[w])
            {
                _phaseTransitions[w]++;
                DebugLog.Log(ScenarioLog, $"t={_elapsed:F2}s: Wolf {w} (id={_wolfIds[w]}) phase transition: {PhaseName(_lastPhase[w])}({_lastPhase[w]}) -> {PhaseName(currentPhase)}({currentPhase})");
                _lastPhase[w] = currentPhase;
            }

            if (currentPhase > _maxPhaseReached[w])
                _maxPhaseReached[w] = currentPhase;
        }

        // Detect combat
        int combatLogCount = sim.CombatLog.Entries.Count;
        if (combatLogCount > _lastCombatLogCount)
        {
            if (!_combatDetected)
            {
                _combatDetected = true;
                DebugLog.Log(ScenarioLog, $"t={_elapsed:F2}s: First combat detected! ({combatLogCount - _lastCombatLogCount} new entries)");
            }
            _lastCombatLogCount = combatLogCount;
        }

        // Periodic detailed logging every 2 seconds
        _logTimer -= dt;
        if (_logTimer <= 0f)
        {
            _logTimer = 2f;
            LogDetailedState(sim);
        }

        // Complete when test duration elapsed or all wolves/soldiers dead
        if (_elapsed >= TestDuration)
            _complete = true;

        // Early completion if all wolves or all soldiers are dead
        int aliveWolves = 0, aliveSoldiers = 0;
        for (int w = 0; w < 3; w++)
        {
            int idx = FindByID(units, _wolfIds[w]);
            if (idx >= 0 && units[idx].Alive) aliveWolves++;
        }
        for (int s = 0; s < 2; s++)
        {
            int idx = FindByID(units, _soldierIds[s]);
            if (idx >= 0 && units[idx].Alive) aliveSoldiers++;
        }
        if ((aliveWolves == 0 || aliveSoldiers == 0) && _elapsed > 3f)
        {
            DebugLog.Log(ScenarioLog, $"t={_elapsed:F2}s: Early completion - wolves alive: {aliveWolves}, soldiers alive: {aliveSoldiers}");
            _complete = true;
        }

        if (_elapsed >= MaxDuration)
            _complete = true;
    }

    private void LogDetailedState(Simulation sim)
    {
        var units = sim.Units;
        DebugLog.Log(ScenarioLog, $"--- Status at t={_elapsed:F1}s ---");

        for (int w = 0; w < 3; w++)
        {
            int idx = FindByID(units, _wolfIds[w]);
            if (idx < 0)
            {
                DebugLog.Log(ScenarioLog, $"  Wolf {w} (id={_wolfIds[w]}): DEAD/REMOVED");
                continue;
            }

            var pos = units[idx].Position;
            byte phase = units[idx].WolfPhase;
            float phaseTimer = units[idx].WolfPhaseTimer;
            bool inCombat = units[idx].InCombat;
            float cooldown = units[idx].AttackCooldown;

            // Find distance to nearest soldier
            float nearestDist = float.MaxValue;
            for (int s = 0; s < 2; s++)
            {
                int sIdx = FindByID(units, _soldierIds[s]);
                if (sIdx >= 0 && units[sIdx].Alive)
                {
                    float d = (units[sIdx].Position - pos).Length();
                    if (d < nearestDist) nearestDist = d;
                }
            }

            DebugLog.Log(ScenarioLog,
                $"  Wolf {w} (id={_wolfIds[w]}): pos=({pos.X:F1},{pos.Y:F1}) phase={PhaseName(phase)}({phase}) " +
                $"timer={phaseTimer:F2} inCombat={inCombat} cooldown={cooldown:F2} " +
                $"distToTarget={nearestDist:F1} maxPhase={_maxPhaseReached[w]} transitions={_phaseTransitions[w]}");
        }

        for (int s = 0; s < 2; s++)
        {
            int idx = FindByID(units, _soldierIds[s]);
            if (idx < 0)
            {
                DebugLog.Log(ScenarioLog, $"  Soldier {s} (id={_soldierIds[s]}): DEAD/REMOVED");
                continue;
            }
            var pos = units[idx].Position;
            bool alive = units[idx].Alive;
            var hp = units[idx].Stats.HP;
            var maxHp = units[idx].Stats.MaxHP;
            DebugLog.Log(ScenarioLog, $"  Soldier {s} (id={_soldierIds[s]}): pos=({pos.X:F1},{pos.Y:F1}) alive={alive} HP={hp}/{maxHp}");
        }

        DebugLog.Log(ScenarioLog, $"  Combat log entries: {sim.CombatLog.Entries.Count}");
    }

    private static string PhaseName(byte phase) => phase switch
    {
        0 => "Engage",
        1 => "Attacking",
        2 => "Disengage",
        3 => "WaitCooldown",
        _ => $"Unknown({phase})"
    };

    private int FindByID(UnitArrays units, uint id)
    {
        for (int i = 0; i < units.Count; i++)
            if (units[i].Id == id) return i;
        return -1;
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "=== Wolf Hit-and-Run Validation ===");

        var units = sim.Units;
        int combatEntries = sim.CombatLog.Entries.Count;

        // Log final state for each wolf
        byte overallMaxPhase = 0;
        int overallMaxTransitions = 0;
        bool anyWolfCycled = false;

        for (int w = 0; w < 3; w++)
        {
            int idx = FindByID(units, _wolfIds[w]);
            string status = idx >= 0 && units[idx].Alive ? "ALIVE" : "DEAD";
            DebugLog.Log(ScenarioLog,
                $"Wolf {w} (id={_wolfIds[w]}): {status}, maxPhase={PhaseName(_maxPhaseReached[w])}({_maxPhaseReached[w]}), " +
                $"transitions={_phaseTransitions[w]}");

            if (_maxPhaseReached[w] > overallMaxPhase)
                overallMaxPhase = _maxPhaseReached[w];
            if (_phaseTransitions[w] > overallMaxTransitions)
                overallMaxTransitions = _phaseTransitions[w];

            // A wolf has cycled if it reached phase 2 (Disengage) or 3 (WaitCooldown)
            if (_maxPhaseReached[w] >= 2)
                anyWolfCycled = true;
        }

        // Validation 1: At least 1 wolf went through at least one cycle (reached phase 2 or 3)
        bool phasePass = anyWolfCycled;
        DebugLog.Log(ScenarioLog, $"Check 1 - Wolf phase cycling (any reached Disengage or WaitCooldown): " +
            $"maxPhaseReached={overallMaxPhase}, anyWolfCycled={anyWolfCycled} -> {(phasePass ? "PASS" : "FAIL")}");

        // Validation 2: Combat should have occurred
        bool combatPass = combatEntries > 0;
        DebugLog.Log(ScenarioLog, $"Check 2 - Combat occurred: entries={combatEntries} -> {(combatPass ? "PASS" : "FAIL")}");

        // Summary
        int aliveWolves = 0, aliveSoldiers = 0;
        for (int w = 0; w < 3; w++)
        {
            int idx = FindByID(units, _wolfIds[w]);
            if (idx >= 0 && units[idx].Alive) aliveWolves++;
        }
        for (int s = 0; s < 2; s++)
        {
            int idx = FindByID(units, _soldierIds[s]);
            if (idx >= 0 && units[idx].Alive) aliveSoldiers++;
        }

        DebugLog.Log(ScenarioLog, $"Final: {aliveWolves}/3 wolves alive, {aliveSoldiers}/2 soldiers alive");
        DebugLog.Log(ScenarioLog, $"Total combat log entries: {combatEntries}");
        DebugLog.Log(ScenarioLog, $"Total phase transitions across all wolves: {_phaseTransitions[0] + _phaseTransitions[1] + _phaseTransitions[2]}");
        DebugLog.Log(ScenarioLog, $"Elapsed time: {_elapsed:F1}s");

        bool pass = phasePass && combatPass;
        DebugLog.Log(ScenarioLog, $"Overall: {(pass ? "PASS" : "FAIL")}");
        return pass ? 0 : 1;
    }
}
