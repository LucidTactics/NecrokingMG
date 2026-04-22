using Necroking.AI;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Movement;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Reproduces the "horde chaser drags the horde across the map" bug. A
/// necromancer (horde center) walks one way; a target walks the opposite way.
/// The chasing minion should respect the leash and eventually give up.
///
/// Before fix: UpdateChasing had no leash check, and SyncHordeState only
/// honored horde's Returning assignment when the target was dead.
/// </summary>
public class HordeChaseLeashScenario : ScenarioBase
{
    public override string Name => "horde_chase_leash";

    private uint _minionId, _targetId, _necroId;
    private float _elapsed;
    private bool _complete;
    private bool _fail;
    private string _failReason = "";

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Horde Chase-Leash Regression ===");

        var units = sim.UnitsMut;

        int nIdx = sim.SpawnUnitByID("necromancer", new Vec2(10f, 10f));
        units[nIdx].Archetype = 0;
        units[nIdx].AI = AIBehavior.IdleAtPoint;
        _necroId = units[nIdx].Id;
        sim.SetNecromancerIndex(nIdx);

        int mIdx = sim.SpawnUnitByID("Skeleton", new Vec2(11f, 10f));
        units[mIdx].Archetype = ArchetypeRegistry.HordeMinion;
        units[mIdx].Faction = Faction.Undead;
        units[mIdx].Stats.MaxHP = 500;
        units[mIdx].Stats.HP = 500;
        sim.Horde.AddUnit(units[mIdx].Id);
        _minionId = units[mIdx].Id;

        int tIdx = units.AddUnit(new Vec2(18f, 10f), UnitType.Soldier);
        units[tIdx].AI = AIBehavior.IdleAtPoint;
        units[tIdx].Faction = Faction.Human;
        units[tIdx].Stats.MaxHP = 10000;
        units[tIdx].Stats.HP = 10000;
        _targetId = units[tIdx].Id;

        ZoomOnLocation(15f, 10f, 40f);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;
        var units = sim.UnitsMut;
        int mIdx = FindById(units, _minionId);
        int tIdx = FindById(units, _targetId);
        int nIdx = FindById(units, _necroId);
        if (mIdx < 0 || tIdx < 0 || nIdx < 0) { _complete = true; return; }

        units[nIdx].PreferredVel = new Vec2(-4f, 0f);
        units[tIdx].PreferredVel = new Vec2(3.5f, 0f);
        units[tIdx].Target = CombatTarget.None;
        units[tIdx].EngagedTarget = CombatTarget.None;

        var m = sim.Units[mIdx];
        float distToCenter = (m.Position - sim.Horde.CircleCenter).Length();
        float leash = sim.Horde.Settings.LeashRadius;

        if (_elapsed % 1f < dt)
        {
            DebugLog.Log(ScenarioLog,
                $"t={_elapsed:F1}s minion@({m.Position.X:F1},{m.Position.Y:F1}) "
              + $"center@({sim.Horde.CircleCenter.X:F1},{sim.Horde.CircleCenter.Y:F1}) "
              + $"distToCenter={distToCenter:F2} leash={leash:F1} routine={m.Routine}");
        }

        if (_elapsed > 10f)
        {
            if (distToCenter > leash * 2f && (m.Routine == 1 || m.Routine == 2))
            {
                _fail = true;
                _failReason = $"After 10s minion is {distToCenter:F1}u from center "
                    + $"(leash={leash:F1}) still Routine={m.Routine}. "
                    + "Should have given up the chase.";
            }
            _complete = true;
        }
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "=== Summary ===");
        if (_fail) { DebugLog.Log(ScenarioLog, "FAIL: " + _failReason); return 1; }
        DebugLog.Log(ScenarioLog, "PASS: minion respected leash");
        return 0;
    }

    private static int FindById(UnitArrays units, uint id)
    {
        for (int i = 0; i < units.Count; i++)
            if (units[i].Id == id) return i;
        return -1;
    }
}
