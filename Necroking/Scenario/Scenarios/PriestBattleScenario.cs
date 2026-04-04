using System;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Movement;

namespace Necroking.Scenario.Scenarios;

public class PriestBattleScenario : ScenarioBase
{
    public override string Name => "priest_battle";
    private float _elapsed;
    private bool _complete;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Priest Battle Scenario ===");

        var units = sim.UnitsMut;

        // Spawn priest (caster) with soldiers
        int priestIdx = units.AddUnit(new Vec2(8f, 10f), UnitType.Soldier);
        units[priestIdx].AI = AIBehavior.Caster;
        units[priestIdx].Mana = 30f;
        units[priestIdx].MaxMana = 50f;
        units[priestIdx].ManaRegen = 1.5f;
        units[priestIdx].SpellID = "god_ray";
        units[priestIdx].Stats.MaxHP = 60;
        units[priestIdx].Stats.HP = 60;

        // 5 soldiers backing up priest
        for (int i = 0; i < 5; i++)
        {
            int idx = units.AddUnit(new Vec2(6f + i * 1.5f, 8f), UnitType.Soldier);
            units[idx].AI = AIBehavior.AttackClosest;
        }

        // 20 skeletons spread in grid
        for (int i = 0; i < 20; i++)
        {
            float x = 20f + (i % 5) * 2f;
            float y = 8f + (i / 5) * 2f;
            int idx = units.AddUnit(new Vec2(x, y), UnitType.Skeleton);
            units[idx].AI = AIBehavior.AttackClosest;
        }

        DebugLog.Log(ScenarioLog, "Spawned priest + 5 soldiers vs 20 skeletons");
        ZoomOnLocation(14f, 10f, 20f);
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
            DebugLog.Log(ScenarioLog, $"Battle resolved at {_elapsed:F1}s: undead={undead} human={human}");
            _complete = true;
        }
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        int undead = 0, human = 0;
        for (int i = 0; i < sim.Units.Count; i++)
        {
            if (!sim.Units[i].Alive) continue;
            if (sim.Units[i].Faction == Faction.Undead) undead++;
            else human++;
        }
        DebugLog.Log(ScenarioLog, $"Survivors: undead={undead} human={human}");
        return 0; // Always pass
    }
}
