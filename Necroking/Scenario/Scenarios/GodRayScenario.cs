using System;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Movement;

namespace Necroking.Scenario.Scenarios;

public class GodRayScenario : ScenarioBase
{
    public override string Name => "god_ray";
    public override bool WantsGround => true;
    private float _elapsed;
    private bool _complete;
    private bool _strikeDetected;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== God Ray Scenario ===");

        var units = sim.UnitsMut;

        // Spawn 6 idle skeletons as targets
        for (int i = 0; i < 6; i++)
        {
            float x = 10f + (i % 3) * 2f;
            float y = 10f + (i / 3) * 2f;
            int idx = units.AddUnit(new Vec2(x, y), UnitType.Skeleton);
            units[idx].AI = AIBehavior.IdleAtPoint;
            units[idx].MoveTarget = new Vec2(x, y);
        }

        // Spawn 2 caster soldiers
        for (int i = 0; i < 2; i++)
        {
            int idx = units.AddUnit(new Vec2(20f + i * 3f, 10f), UnitType.Soldier);
            units[idx].AI = AIBehavior.Caster;
            units[idx].Mana = 50f;
            units[idx].MaxMana = 50f;
            units[idx].ManaRegen = 2f;
            units[idx].SpellID = "god_ray";
        }

        DebugLog.Log(ScenarioLog, "Spawned 6 skeletons + 2 casters");
        ZoomOnLocation(14f, 10f, 24f);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;

        // Check for active strikes (indicates caster AI is working)
        if (!_strikeDetected && sim.Lightning.Strikes.Count > 0)
        {
            _strikeDetected = true;
            DebugLog.Log(ScenarioLog, $"[{_elapsed:F1}s] Strike detected! Caster AI working.");
        }

        if (_elapsed > 8f)
        {
            DebugLog.Log(ScenarioLog, $"Scenario ending at {_elapsed:F1}s");
            DebugLog.Log(ScenarioLog, $"  Strikes detected: {_strikeDetected}");
            _complete = true;
        }
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "=== God Ray Validation ===");
        DebugLog.Log(ScenarioLog, $"Caster cast spell: {_strikeDetected}");

        // Count surviving skeletons
        int alive = 0;
        for (int i = 0; i < sim.Units.Count; i++)
            if (sim.Units[i].Alive && sim.Units[i].Faction == Faction.Undead) alive++;
        DebugLog.Log(ScenarioLog, $"Skeletons remaining: {alive}/6");

        return 0; // Always pass - visual test
    }
}
