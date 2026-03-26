using System;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Movement;

namespace Necroking.Scenario.Scenarios;

public class PatrolEncounterScenario : ScenarioBase
{
    public override string Name => "patrol_encounter";
    private float _elapsed;
    private bool _complete;
    private bool _combatDetected;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Patrol Encounter Scenario ===");

        var units = sim.UnitsMut;

        // Skeleton camp (6 skeletons, IdleAtPoint)
        for (int i = 0; i < 6; i++)
        {
            float x = 10f + (i % 3) * 2f;
            float y = 10f + (i / 3) * 2f;
            int idx = units.AddUnit(new Vec2(x, y), UnitType.Skeleton);
            units.AI[idx] = AIBehavior.IdleAtPoint;
            units.MoveTarget[idx] = new Vec2(x, y);
        }

        // Soldier outpost with patrol route (4 soldiers, Patrol AI simulated as MoveToPoint)
        // They march toward the skeleton camp
        for (int i = 0; i < 4; i++)
        {
            int idx = units.AddUnit(new Vec2(30f, 10f + i * 1.5f), UnitType.Soldier);
            units.AI[idx] = AIBehavior.AttackClosest;
            // The soldiers will naturally seek out enemies
        }

        DebugLog.Log(ScenarioLog, "Spawned 6 skeletons (camp) + 4 soldiers (patrol)");
        ZoomOnLocation(20f, 10f, 16f);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;

        int undead = 0, human = 0;
        for (int i = 0; i < sim.Units.Count; i++)
        {
            if (!sim.Units.Alive[i]) continue;
            if (sim.Units.Faction[i] == Faction.Undead) undead++;
            else human++;
        }

        if (!_combatDetected && sim.CombatLog.Entries.Count > 0)
        {
            _combatDetected = true;
            DebugLog.Log(ScenarioLog, $"[{_elapsed:F1}s] Combat detected!");
        }

        if ((undead == 0 || human == 0) || _elapsed > 30f)
        {
            DebugLog.Log(ScenarioLog, $"Scenario ending at {_elapsed:F1}s: undead={undead} human={human}");
            _complete = true;
        }
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "=== Patrol Encounter Validation ===");
        DebugLog.Log(ScenarioLog, $"Combat detected: {_combatDetected}");
        DebugLog.Log(ScenarioLog, $"Combat log entries: {sim.CombatLog.Entries.Count}");

        bool pass = _combatDetected && sim.CombatLog.Entries.Count > 0;
        int undead = 0, human = 0;
        for (int i = 0; i < sim.Units.Count; i++)
        {
            if (!sim.Units.Alive[i]) continue;
            if (sim.Units.Faction[i] == Faction.Undead) undead++;
            else human++;
        }
        bool oneFactionDead = undead == 0 || human == 0;
        pass = pass && oneFactionDead;

        DebugLog.Log(ScenarioLog, $"One faction eliminated: {oneFactionDead}");
        DebugLog.Log(ScenarioLog, $"Overall: {(pass ? "PASS" : "FAIL")}");
        return pass ? 0 : 1;
    }
}
