using Necroking.AI;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Movement;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Reproduces the "horde unit stands still while target kites away" bug.
///
/// Setup: one undead horde minion engaged with a soldier that actively walks
/// away. The minion should transition Engaged → Chasing once the target leaves
/// melee range. Before the fix, the minion stood still in the "past melee but
/// within attackRange * 1.5" dead zone because UpdateEngaged had no exit for
/// "target alive but out of melee range."
///
/// Assertion: minion must not stand still (PreferredVel ~ 0) for extended
/// frames while the target is clearly out of melee range.
/// </summary>
public class HordeEngagedKitingScenario : ScenarioBase
{
    public override string Name => "horde_engaged_kiting";

    private uint _minionId, _soldierId;
    private float _elapsed;
    private bool _complete;
    private int _standStillFrames;
    private bool _fail;
    private string _failReason = "";

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Horde Engaged-Kiting Regression ===");

        var units = sim.UnitsMut;

        int mIdx = sim.SpawnUnitByID("Skeleton", new Vec2(10f, 10f));
        units[mIdx].Archetype = ArchetypeRegistry.HordeMinion;
        units[mIdx].Faction = Faction.Undead;
        units[mIdx].Stats.MaxHP = 500;
        units[mIdx].Stats.HP = 500;
        sim.Horde.AddUnit(units[mIdx].Id);
        _minionId = units[mIdx].Id;

        int sIdx = units.AddUnit(new Vec2(11.5f, 10f), UnitType.Soldier);
        units[sIdx].Archetype = 0;   // disable archetype AI (handler would override vel)
        units[sIdx].AI = AIBehavior.IdleAtPoint;
        units[sIdx].Faction = Faction.Human;
        units[sIdx].Stats.MaxHP = 500;
        units[sIdx].Stats.HP = 500;
        _soldierId = units[sIdx].Id;

        ZoomOnLocation(15f, 10f, 60f);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;
        var units = sim.UnitsMut;
        int mIdx = FindById(units, _minionId);
        int sIdx = FindById(units, _soldierId);
        if (mIdx < 0 || sIdx < 0) { _complete = true; return; }

        // Keep the soldier pinned exactly in the minion's attack-target DEAD BAND
        // (attackRange, attackRange*1.5]. That's the band where SubroutineSteps.
        // AttackTarget sets PreferredVel=0 waiting for the next swing — if the
        // minion's Routine is Engaged with no transition out, it stays still even
        // though the target is effectively out of melee range. Position each frame
        // to 2.5u east of the minion (skeleton melee ≈ 1.5 after radii+length, so
        // 2.5 is in the dead band).
        var mPos = sim.Units[mIdx].Position;
        units[sIdx].Position = new Vec2(mPos.X + 2.5f, mPos.Y);
        units[sIdx].Velocity = Vec2.Zero;
        units[sIdx].Target = CombatTarget.None;
        units[sIdx].EngagedTarget = CombatTarget.None;
        units[sIdx].InCombat = false;
        units[sIdx].PendingAttack = CombatTarget.None;
        units[sIdx].PostAttackTimer = 0f;
        units[sIdx].AttackCooldown = 0f;

        var m = sim.Units[mIdx];
        var s = sim.Units[sIdx];
        float dist = (s.Position - m.Position).Length();
        bool outOfRange = dist > 2.5f;
        bool standingStill = m.PreferredVel.LengthSq() < 0.01f;

        if (outOfRange && standingStill && m.Alive && _elapsed > 1f)
        {
            _standStillFrames++;
            if (_standStillFrames > 30)
            {
                _fail = true;
                _failReason = $"Minion stood still for {_standStillFrames} frames while target was {dist:F2}u away "
                    + $"(routine={m.Routine}, engaged={m.EngagedTarget.IsUnit}, inCombat={m.InCombat})";
                _complete = true;
                return;
            }
        }
        else _standStillFrames = 0;

        if (_elapsed % 1f < dt)
        {
            DebugLog.Log(ScenarioLog,
                $"t={_elapsed:F1}s dist={dist:F2} routine={m.Routine} " +
                $"engaged={m.EngagedTarget.IsUnit} inCombat={m.InCombat} " +
                $"v={m.PreferredVel.Length():F2}");
        }
        if (_elapsed > 6f) _complete = true;
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "=== Summary ===");
        if (_fail) { DebugLog.Log(ScenarioLog, "FAIL: " + _failReason); return 1; }
        DebugLog.Log(ScenarioLog, "PASS: minion did not stand still while target fled");
        return 0;
    }

    private static int FindById(UnitArrays units, uint id)
    {
        for (int i = 0; i < units.Count; i++)
            if (units[i].Id == id) return i;
        return -1;
    }
}
