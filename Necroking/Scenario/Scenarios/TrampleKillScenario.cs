using System.Collections.Generic;
using Necroking.AI;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Movement;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Trample-kills-target test: a Boar charges a 1-HP soldier head-on. The trample
/// impact damage will kill them outright. Verifies that the resulting CORPSE
/// inherits the knockback velocity and flies (instead of just collapsing in
/// place where the soldier stood).
///
/// Mirrors the spell-knockback "physics first, damage second" pattern — the
/// impulse fires before the damage roll so the corpse picks up the body's
/// velocity on RemoveDeadUnits.
/// </summary>
public class TrampleKillScenario : ScenarioBase
{
    public override string Name => "trample_kill";

    private float _elapsed;
    private bool _complete;
    private float _completeBy = -1f;
    private const float MaxDuration = 6f;

    private uint _boarId;
    private uint _victimId;
    private Vec2 _victimStartPos;
    private int _initialCorpseCount;
    private float _maxCorpseDispFromVictimStart;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Trample-Kill: corpse should fly ===");

        var units = sim.UnitsMut;

        int boarIdx = sim.SpawnUnitByID("Boar", new Vec2(2f, 10f));
        if (boarIdx < 0) { _complete = true; return; }
        units[boarIdx].Faction = Faction.Animal;
        units[boarIdx].AI = AIBehavior.AttackClosest;
        units[boarIdx].FacingAngle = 0f;
        units[boarIdx].Stats.MaxHP = 9999;
        units[boarIdx].Stats.HP = 9999;
        _boarId = units[boarIdx].Id;

        // 1-HP, 0-Defense soldier in trample range. Defense=0 ensures the boar's
        // tusk roll always hits (no dodge); 1 HP guarantees the impact kills.
        _victimStartPos = new Vec2(7f, 10f);
        int v = units.AddUnit(_victimStartPos, UnitType.Soldier);
        units[v].AI = AIBehavior.IdleAtPoint;
        units[v].Faction = Faction.Human;
        units[v].Stats.MaxHP = 9999;
        units[v].Stats.HP = 1;
        units[v].Stats.Defense = 0;
        _victimId = units[v].Id;

        units[boarIdx].Target = CombatTarget.Unit(_victimId);
        _initialCorpseCount = sim.Corpses.Count;
        ZoomOnLocation(7f, 10f, 50f);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;
        if (_complete) return;

        // Track each corpse's max distance from the victim's starting position.
        for (int i = _initialCorpseCount; i < sim.Corpses.Count; i++)
        {
            var c = sim.Corpses[i];
            float disp = (c.Position - _victimStartPos).Length();
            if (disp > _maxCorpseDispFromVictimStart) _maxCorpseDispFromVictimStart = disp;
        }

        int bIdx = FindByID(sim.Units, _boarId);
        if (bIdx >= 0 && _completeBy < 0f && sim.Units[bIdx].ChargePhase == 2)
            _completeBy = _elapsed + 2.5f; // give corpse time to fly + land

        if (_completeBy > 0f && _elapsed >= _completeBy) _complete = true;
        if (_elapsed >= MaxDuration) _complete = true;
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "=== Validation ===");
        var units = sim.Units;

        bool victimDied = FindByID(units, _victimId) < 0;
        int newCorpses = sim.Corpses.Count - _initialCorpseCount;
        DebugLog.Log(ScenarioLog,
            $"Victim died: {victimDied}, new corpses: {newCorpses}, " +
            $"corpse maxDisp from victim start: {_maxCorpseDispFromVictimStart:F2}");

        bool gotCorpse = newCorpses > 0;
        bool corpseFlew = _maxCorpseDispFromVictimStart > 1.0f; // flew at least 1u
        DebugLog.Log(ScenarioLog, $"Check - victim died:                  {victimDied}");
        DebugLog.Log(ScenarioLog, $"Check - corpse created:               {gotCorpse}");
        DebugLog.Log(ScenarioLog, $"Check - corpse flew > 1u from start:  {corpseFlew}");

        bool pass = victimDied && gotCorpse && corpseFlew;
        DebugLog.Log(ScenarioLog, $"Overall: {(pass ? "PASS" : "FAIL")}");
        return pass ? 0 : 1;
    }

    private static int FindByID(UnitArrays units, uint id)
    {
        for (int i = 0; i < units.Count; i++)
            if (units[i].Id == id) return i;
        return -1;
    }
}
