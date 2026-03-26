using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Movement;

namespace Necroking.Scenario.Scenarios;

public class CombatTestScenario : ScenarioBase
{
    public override string Name => "combat_test";
    private float _elapsed;
    private bool _complete;
    private int _initialUndead, _initialHuman;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Combat Test Scenario ===");

        // Spawn a skeleton and a soldier
        var units = sim.UnitsMut;
        units.AddUnit(new Vec2(8f, 10f), UnitType.Skeleton);
        units.AddUnit(new Vec2(12f, 10f), UnitType.Soldier);

        _initialUndead = 1;
        _initialHuman = 1;

        DebugLog.Log(ScenarioLog, $"Spawned {units.Count} units: {_initialUndead} undead, {_initialHuman} human");
        ZoomOnLocation(10f, 10f, 64f);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;

        // Check if combat resolved
        int undead = 0, human = 0;
        for (int i = 0; i < sim.Units.Count; i++)
        {
            if (!sim.Units.Alive[i]) continue;
            if (sim.Units.Faction[i] == Faction.Undead) undead++;
            else human++;
        }

        if (undead == 0 || human == 0 || _elapsed > 30f)
        {
            DebugLog.Log(ScenarioLog, $"Combat resolved at t={_elapsed:F1}s: {undead} undead, {human} human alive");
            _complete = true;
        }
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "=== Combat Test Complete ===");
        // Pass if at least one side won
        int alive = 0;
        for (int i = 0; i < sim.Units.Count; i++)
            if (sim.Units.Alive[i]) alive++;
        bool pass = alive > 0;
        DebugLog.Log(ScenarioLog, pass ? "PASS" : "FAIL (no survivors)");
        return pass ? 0 : 1;
    }
}
