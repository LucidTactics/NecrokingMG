using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Movement;

namespace Necroking.Scenario.Scenarios;

public class SkirmishScenario : ScenarioBase
{
    public override string Name => "skirmish";
    private float _elapsed;
    private bool _complete;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Skirmish Scenario: 20 skeletons vs 8 soldiers ===");

        var units = sim.UnitsMut;
        // 20 skeletons in a line
        for (int i = 0; i < 20; i++)
            units.AddUnit(new Vec2(5f + (i % 10) * 1.2f, 14f + (i / 10) * 1.2f), UnitType.Skeleton);

        // 8 soldiers in a line
        for (int i = 0; i < 8; i++)
            units.AddUnit(new Vec2(6f + i * 1.5f, 6f), UnitType.Soldier);

        DebugLog.Log(ScenarioLog, $"Spawned {units.Count} units");
        ZoomOnLocation(10f, 10f, 32f);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;

        int undead = 0, human = 0;
        for (int i = 0; i < sim.Units.Count; i++)
        {
            if (!sim.Units[i].Alive) continue;
            if (sim.Units[i].Faction == Faction.Undead) undead++;
            else human++;
        }

        if (undead == 0 || human == 0 || _elapsed > 60f)
        {
            DebugLog.Log(ScenarioLog, $"Battle resolved at t={_elapsed:F1}s: {undead} undead, {human} human remaining");
            _complete = true;
        }
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "=== Skirmish Complete ===");
        return 0; // always pass
    }
}
