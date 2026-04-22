using Necroking.AI;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Movement;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Engaged minion's target suddenly teleports out of melee range (simulating
/// a knockback or blink). The minion's routine must transition Engaged →
/// Chasing and move to catch up, rather than staying Engaged doing nothing.
/// Exercises the CombatTransitions.StandardEngagedExits melee-range check.
/// </summary>
public class HordeTargetTeleportScenario : ScenarioBase
{
    public override string Name => "horde_target_teleport";

    private uint _minionId, _targetId;
    private float _elapsed;
    private bool _complete, _fail;
    private string _failReason = "";
    private bool _didTeleport;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Horde Target-Teleport Regression ===");

        var units = sim.UnitsMut;

        int mIdx = sim.SpawnUnitByID("Skeleton", new Vec2(10f, 10f));
        units[mIdx].Archetype = ArchetypeRegistry.HordeMinion;
        units[mIdx].Faction = Faction.Undead;
        units[mIdx].Stats.MaxHP = 500;
        units[mIdx].Stats.HP = 500;
        sim.Horde.AddUnit(units[mIdx].Id);
        _minionId = units[mIdx].Id;

        int tIdx = units.AddUnit(new Vec2(11f, 10f), UnitType.Soldier);
        units[tIdx].Archetype = 0;
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
        if (mIdx < 0 || tIdx < 0) { _complete = true; return; }

        // At t=2s the minion has had time to close to melee and enter Engaged.
        // Teleport the target 10 tiles away, simulating a knockback.
        if (!_didTeleport && _elapsed > 2f)
        {
            units[tIdx].Position = new Vec2(25f, 10f);
            units[tIdx].Velocity = Vec2.Zero;
            units[tIdx].Target = CombatTarget.None;
            units[tIdx].EngagedTarget = CombatTarget.None;
            _didTeleport = true;
            DebugLog.Log(ScenarioLog, $"t={_elapsed:F1}s: teleported target from ~11,10 to 25,10");
        }

        var m = sim.Units[mIdx];
        if (_elapsed % 0.5f < dt)
        {
            DebugLog.Log(ScenarioLog,
                $"t={_elapsed:F2}s minion@({m.Position.X:F2},{m.Position.Y:F2}) "
              + $"routine={m.Routine} eng={m.EngagedTarget.IsUnit} "
              + $"pVel={m.PreferredVel.Length():F2} vel={m.Velocity.Length():F2} "
              + $"pending={!m.PendingAttack.IsNone} postAtk={m.PostAttackTimer:F2} inCombat={m.InCombat}");
        }

        // 2 seconds after teleport, minion should have moved off its original
        // engage position. If it hasn't, it's stuck Engaged.
        if (_didTeleport && _elapsed > 4f)
        {
            // Minion should have moved noticeably east.
            if (m.Position.X < 12f)
            {
                _fail = true;
                _failReason = $"2s after teleport, minion at ({m.Position.X:F1},{m.Position.Y:F1}) "
                    + $"— routine={m.Routine}. Should have transitioned to Chasing and moved toward target.";
            }
            _complete = true;
        }
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "=== Summary ===");
        if (_fail) { DebugLog.Log(ScenarioLog, "FAIL: " + _failReason); return 1; }
        DebugLog.Log(ScenarioLog, "PASS: minion transitioned Engaged → Chasing after teleport");
        return 0;
    }

    private static int FindById(UnitArrays units, uint id)
    {
        for (int i = 0; i < units.Count; i++)
            if (units[i].Id == id) return i;
        return -1;
    }
}
