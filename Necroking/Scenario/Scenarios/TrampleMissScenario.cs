using System.Collections.Generic;
using Necroking.AI;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Movement;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Trample miss-case test (post-dodge model). The primary target has Defense=999
/// so every dice roll misses. With the new dodge mechanic, the target tries to
/// hop to a free tile within 1u when missed; in this open-field setup plenty of
/// safe tiles exist, so the target SHOULD dodge every time and never get hit.
///
/// Verifies:
///   - Boar's swing always rolls a miss
///   - Target dodges (moves > 0.5u from start position via the snappy hop)
///   - Target NEVER enters physics (no force-hit launch — safe tile available)
///   - Target takes 0 damage
///   - Boar still completes its charge cycle (impact → follow-through → recovery)
/// </summary>
public class TrampleMissScenario : ScenarioBase
{
    public override string Name => "trample_miss";

    private float _elapsed;
    private bool _complete;
    private const float MaxDuration = 8f;

    private uint _boarId;
    private uint _primaryId;

    private int _primaryHP0;
    private Vec2 _primaryStartPos;
    private float _primaryMaxDisp;
    private bool _primaryWentInPhysics;
    private bool _followThroughObserved;
    private bool _boarPassedTargetX;
    private byte _maxChargePhaseReached;
    private byte _lastChargePhase;
    private int _initialCombatLogCount;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Trample Miss Test ===");
        DebugLog.Log(ScenarioLog, "Primary target has Defense=999 — the boar's tusk roll will miss every time.");
        DebugLog.Log(ScenarioLog, "Verify: impact still fires, target launched anyway, boar drives through.");

        var units = sim.UnitsMut;

        int boarIdx = sim.SpawnUnitByID("Boar", new Vec2(2f, 10f));
        if (boarIdx < 0)
        {
            DebugLog.Log(ScenarioLog, "FAIL: could not spawn Boar");
            _complete = true;
            return;
        }
        units[boarIdx].Faction = Faction.Animal;
        units[boarIdx].AI = AIBehavior.AttackClosest;
        units[boarIdx].FacingAngle = 0f;
        units[boarIdx].Stats.MaxHP = 99999;
        units[boarIdx].Stats.HP = 99999;
        _boarId = units[boarIdx].Id;
        DebugLog.Log(ScenarioLog,
            $"Boar: id={_boarId} pos=(2,10) size={units[boarIdx].Size} " +
            $"radius={units[boarIdx].Radius:F2} combatSpeed={units[boarIdx].Stats.CombatSpeed:F1}");

        // Primary target: high defense → all dice rolls miss. Stationary so the
        // charge always reaches it cleanly.
        int prim = units.AddUnit(new Vec2(7.5f, 10f), UnitType.Soldier);
        units[prim].AI = AIBehavior.IdleAtPoint;
        units[prim].Faction = Faction.Human;
        units[prim].Stats.MaxHP = 99999;
        units[prim].Stats.HP = 99999;
        units[prim].Stats.Defense = 999; // un-hittable
        _primaryId = units[prim].Id;
        _primaryHP0 = units[prim].Stats.HP;
        _primaryStartPos = units[prim].Position;

        units[boarIdx].Target = CombatTarget.Unit(units[prim].Id);

        DebugLog.Log(ScenarioLog,
            $"Primary: id={_primaryId} at ({_primaryStartPos.X:F2},{_primaryStartPos.Y:F2}) " +
            $"radius={units[prim].Radius:F2} Defense=999");
        _initialCombatLogCount = sim.CombatLog.Entries.Count;
        ZoomOnLocation(8f, 10f, 48f);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;
        if (_complete) return;

        int bIdx = FindByID(sim.Units, _boarId);
        int pIdx = FindByID(sim.Units, _primaryId);

        if (bIdx >= 0)
        {
            byte phase = sim.Units[bIdx].ChargePhase;
            if (phase == 3) _followThroughObserved = true;
            if (phase != _lastChargePhase)
            {
                DebugLog.Log(ScenarioLog,
                    $"t={_elapsed:F2}s: Boar ChargePhase {_lastChargePhase} → {phase} " +
                    $"pos=({sim.Units[bIdx].Position.X:F2},{sim.Units[bIdx].Position.Y:F2}) " +
                    $"traveled={sim.Units[bIdx].ChargeTraveled:F2}");
                if (phase == 3 && pIdx >= 0)
                {
                    DebugLog.Log(ScenarioLog,
                        $"  on impact tick: primary pos=({sim.Units[pIdx].Position.X:F2},{sim.Units[pIdx].Position.Y:F2}) " +
                        $"InPhysics={sim.Units[pIdx].InPhysics} HP={sim.Units[pIdx].Stats.HP}/{_primaryHP0}");
                }
                _lastChargePhase = phase;
                if (phase > _maxChargePhaseReached) _maxChargePhaseReached = phase;
            }
            if (sim.Units[bIdx].Position.X >= _primaryStartPos.X) _boarPassedTargetX = true;
        }

        if (pIdx >= 0)
        {
            if (sim.Units[pIdx].InPhysics) _primaryWentInPhysics = true;
            float disp = (sim.Units[pIdx].Position - _primaryStartPos).Length();
            if (disp > _primaryMaxDisp) _primaryMaxDisp = disp;
        }

        if (_elapsed >= MaxDuration) _complete = true;
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "=== Validation ===");
        var units = sim.Units;
        int pIdx = FindByID(units, _primaryId);
        int finalHP = pIdx >= 0 ? units[pIdx].Stats.HP : -1;

        // Count miss vs hit entries against the primary target.
        int trampleHits = 0, trampleMisses = 0;
        var entries = sim.CombatLog.Entries;
        for (int i = _initialCombatLogCount; i < entries.Count; i++)
        {
            var e = entries[i];
            if (e.WeaponName != "Trample") continue;
            if (e.Outcome == CombatLogOutcome.Hit) trampleHits++;
            else trampleMisses++;
        }
        DebugLog.Log(ScenarioLog, $"Combat log: {trampleHits} hits, {trampleMisses} misses against primary");
        DebugLog.Log(ScenarioLog, $"Primary final HP: {finalHP}/{_primaryHP0} (damage={_primaryHP0 - finalHP})");

        // With peek+force-hit, a successful dodge writes nothing to the combat
        // log (peek skips all side effects, dodge returns before force-hit).
        // So "no entries against the primary" is the correct miss-and-dodge
        // signature. A force-hit (dodge failed) WOULD log a "hit".
        bool noHitsLogged = trampleHits == 0;
        bool primaryDodged = !_primaryWentInPhysics;     // safe tile available → dodged → never in physics
        bool primaryHopped = _primaryMaxDisp > 0.5f;      // dodge hop moved them
        bool primaryUnharmed = (_primaryHP0 - finalHP) == 0;
        bool followThroughHappened = _followThroughObserved;
        bool boarDroveThrough = _boarPassedTargetX;
        bool reachedRecovery = _maxChargePhaseReached >= 2;

        DebugLog.Log(ScenarioLog, $"Check - no hits logged:              {noHitsLogged} (hits={trampleHits}, misses={trampleMisses})");
        DebugLog.Log(ScenarioLog, $"Check - primary dodged (no physics): {primaryDodged}");
        DebugLog.Log(ScenarioLog, $"Check - primary hopped > 0.5u:       {primaryHopped} (maxDisp={_primaryMaxDisp:F2})");
        DebugLog.Log(ScenarioLog, $"Check - primary took 0 damage:       {primaryUnharmed}");
        DebugLog.Log(ScenarioLog, $"Check - follow-through observed:     {followThroughHappened}");
        DebugLog.Log(ScenarioLog, $"Check - boar drove past primary X:   {boarDroveThrough}");
        DebugLog.Log(ScenarioLog, $"Check - charge reached recovery:     {reachedRecovery} (maxPhase={_maxChargePhaseReached})");

        bool pass = noHitsLogged && primaryDodged && primaryHopped && primaryUnharmed
                  && followThroughHappened && boarDroveThrough && reachedRecovery;
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
