using System;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Movement;

namespace Necroking.Scenario.Scenarios;

public class OrderAttackScenario : ScenarioBase
{
    public override string Name => "order_attack";
    private float _elapsed;
    private bool _complete;
    private int _phase; // 0=marching, 1=fighting, 2=returning, 3=done
    private bool _reachedTarget;
    private bool _fought;
    private bool _returned;
    private Vec2 _targetPos;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Order Attack Scenario ===");

        var units = sim.UnitsMut;
        _targetPos = new Vec2(30f, 10f);

        // Spawn 12 skeletons in a line with OrderAttack AI
        for (int i = 0; i < 12; i++)
        {
            float x = 8f + (i % 4) * 1.5f;
            float y = 8f + (i / 4) * 1.5f;
            int idx = units.AddUnit(new Vec2(x, y), UnitType.Skeleton);
            units.AI[idx] = AIBehavior.OrderAttack;
            units.MoveTarget[idx] = _targetPos;
            units.Target[idx] = CombatTarget.None;
        }

        // Spawn 2 soldiers at the target location
        for (int i = 0; i < 2; i++)
        {
            int idx = units.AddUnit(new Vec2(30f + i * 2f, 10f), UnitType.Soldier);
            units.AI[idx] = AIBehavior.AttackClosest;
        }

        DebugLog.Log(ScenarioLog, $"Spawned 12 skeletons + 2 soldiers, target=({_targetPos.X},{_targetPos.Y})");
        ZoomOnLocation(20f, 10f, 20f);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;

        // Count units and check progress
        int undead = 0, human = 0;
        int undeadAtTarget = 0;
        int undeadAttackClosest = 0;
        bool anyInCombat = false;

        for (int i = 0; i < sim.Units.Count; i++)
        {
            if (!sim.Units.Alive[i]) continue;
            if (sim.Units.Faction[i] == Faction.Undead)
            {
                undead++;
                float dist = (sim.Units.Position[i] - _targetPos).Length();
                if (dist < 8f) undeadAtTarget++;
                if (sim.Units.InCombat[i]) anyInCombat = true;
                if (sim.Units.AI[i] == AIBehavior.AttackClosest) undeadAttackClosest++;
            }
            else human++;
        }

        if (!_reachedTarget && undeadAtTarget >= 3)
        {
            _reachedTarget = true;
            DebugLog.Log(ScenarioLog, $"[{_elapsed:F1}s] Skeletons reached target area ({undeadAtTarget} near)");
        }

        if (!_fought && (anyInCombat || sim.CombatLog.Entries.Count > 0))
        {
            _fought = true;
            DebugLog.Log(ScenarioLog, $"[{_elapsed:F1}s] Combat detected");
        }

        if (!_returned && undeadAttackClosest >= 5 && _fought)
        {
            _returned = true;
            DebugLog.Log(ScenarioLog, $"[{_elapsed:F1}s] Skeletons returned to AttackClosest ({undeadAttackClosest} units)");
        }

        // Timeout
        if (_elapsed > 25f || (_returned && _elapsed > 3f))
        {
            DebugLog.Log(ScenarioLog, $"Scenario ending: undead={undead} human={human} returned={_returned}");
            _complete = true;
        }
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "=== Order Attack Validation ===");
        DebugLog.Log(ScenarioLog, $"Reached target: {_reachedTarget}");
        DebugLog.Log(ScenarioLog, $"Fought: {_fought}");
        DebugLog.Log(ScenarioLog, $"Returned: {_returned}");

        bool pass = _reachedTarget && _fought;
        DebugLog.Log(ScenarioLog, $"Overall: {(pass ? "PASS" : "FAIL")}");
        return pass ? 0 : 1;
    }
}
