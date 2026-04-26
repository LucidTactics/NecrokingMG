using System.Collections.Generic;
using Necroking.AI;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Movement;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Repro for: "GreatBoar knocks himself down most of the time when he charges."
/// Spawns a GreatBoar (size 4, radius 0.85, combatSpeed 9) charging a soldier
/// and watches for the boar entering an Incap (knockdown) state at any point
/// during or after the charge.
/// </summary>
public class TrampleGreatBoarScenario : ScenarioBase
{
    public override string Name => "trample_greatboar";

    private float _elapsed;
    private bool _complete;
    private float _completeBy = -1f;
    private const float MaxDuration = 12f;

    private uint _boarId;
    private uint _targetId;

    private bool _boarKnockedDown;
    private bool _boarEnteredPhysics;
    private byte _maxChargePhaseReached;
    private byte _lastChargePhase;
    private bool _followThroughObserved;
    private float _boarMaxDispFromCharge;
    private Vec2 _chargeStartPos;
    private bool _boarDamagedTarget;
    private int _targetHP0;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== GreatBoar Trample Self-Knockdown Test ===");

        var units = sim.UnitsMut;

        int boarIdx = sim.SpawnUnitByID("GreatBoar", new Vec2(2f, 10f));
        if (boarIdx < 0) { DebugLog.Log(ScenarioLog, "FAIL: could not spawn GreatBoar"); _complete = true; return; }
        units[boarIdx].Faction = Faction.Animal;
        units[boarIdx].AI = AIBehavior.AttackClosest;
        units[boarIdx].FacingAngle = 0f;
        units[boarIdx].Stats.MaxHP = 99999;
        units[boarIdx].Stats.HP = 99999;
        _boarId = units[boarIdx].Id;
        _chargeStartPos = units[boarIdx].Position;
        DebugLog.Log(ScenarioLog,
            $"GreatBoar: id={_boarId} pos=({_chargeStartPos.X:F2},{_chargeStartPos.Y:F2}) " +
            $"size={units[boarIdx].Size} radius={units[boarIdx].Radius:F2} " +
            $"combatSpeed={units[boarIdx].Stats.CombatSpeed:F1}");

        // Soldier target — squarely in trample window. Plus a cluster of nearby
        // soldiers within TrampleRadius+TrampleRadius*1.5 of the impact, so the
        // radial AOE launches them and chain-collisions can cascade onto the boar.
        int t = units.AddUnit(new Vec2(7f, 10f), UnitType.Soldier);
        units[t].AI = AIBehavior.IdleAtPoint;
        units[t].Faction = Faction.Human;
        units[t].Stats.MaxHP = 99999;
        units[t].Stats.HP = 99999;
        _targetId = units[t].Id;
        _targetHP0 = units[t].Stats.HP;
        units[boarIdx].Target = CombatTarget.Unit(_targetId);
        DebugLog.Log(ScenarioLog,
            $"Soldier: id={_targetId} pos=(7,10) size={units[t].Size} radius={units[t].Radius:F2}");

        // Cluster of soldiers around the primary — these get caught by the radial.
        var clusterPositions = new[] {
            new Vec2(7.5f, 10.7f), new Vec2(7.5f, 9.3f),
            new Vec2(8.3f, 10f),   new Vec2(6.5f, 10f),
            new Vec2(8.0f, 10.6f), new Vec2(8.0f, 9.4f),
        };
        foreach (var pos in clusterPositions)
        {
            int s = units.AddUnit(pos, UnitType.Soldier);
            units[s].AI = AIBehavior.IdleAtPoint;
            units[s].Faction = Faction.Human;
            units[s].Stats.MaxHP = 99999;
            units[s].Stats.HP = 99999;
        }
        DebugLog.Log(ScenarioLog, $"Spawned {clusterPositions.Length} cluster soldiers around primary.");

        ZoomOnLocation(7f, 10f, 48f);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;
        if (_complete) return;

        int bIdx = FindByID(sim.Units, _boarId);
        int tIdx = FindByID(sim.Units, _targetId);
        if (bIdx < 0) { _complete = true; return; }

        var boar = sim.Units[bIdx];

        // Self-knockdown detection: any time the boar enters Incap.Active or InPhysics.
        if (boar.Incap.Active && !_boarKnockedDown)
        {
            _boarKnockedDown = true;
            DebugLog.Log(ScenarioLog,
                $"t={_elapsed:F2}s: !!! BOAR KNOCKED DOWN !!! " +
                $"Incap.Active=true pos=({boar.Position.X:F2},{boar.Position.Y:F2}) " +
                $"phase={boar.ChargePhase} InPhysics={boar.InPhysics}");
        }
        if (boar.InPhysics && !_boarEnteredPhysics)
        {
            _boarEnteredPhysics = true;
            DebugLog.Log(ScenarioLog,
                $"t={_elapsed:F2}s: !!! BOAR ENTERED PHYSICS !!! " +
                $"pos=({boar.Position.X:F2},{boar.Position.Y:F2}) phase={boar.ChargePhase}");
        }

        if (boar.ChargePhase == 3) _followThroughObserved = true;
        if (boar.ChargePhase != _lastChargePhase)
        {
            DebugLog.Log(ScenarioLog,
                $"t={_elapsed:F2}s: GreatBoar phase {_lastChargePhase}→{boar.ChargePhase} " +
                $"pos=({boar.Position.X:F2},{boar.Position.Y:F2}) " +
                $"vel=({boar.Velocity.X:F2},{boar.Velocity.Y:F2}) " +
                $"InPhysics={boar.InPhysics} Incap={boar.Incap.Active}");
            if (boar.ChargePhase == 3 && tIdx >= 0)
            {
                DebugLog.Log(ScenarioLog,
                    $"  on impact: target pos=({sim.Units[tIdx].Position.X:F2},{sim.Units[tIdx].Position.Y:F2}) " +
                    $"InPhysics={sim.Units[tIdx].InPhysics} HP={sim.Units[tIdx].Stats.HP}/{_targetHP0}");
            }
            _lastChargePhase = boar.ChargePhase;
            if (boar.ChargePhase > _maxChargePhaseReached) _maxChargePhaseReached = boar.ChargePhase;
        }

        if (tIdx >= 0 && sim.Units[tIdx].Stats.HP < _targetHP0) _boarDamagedTarget = true;

        // End ~2 seconds after the charge ends — long enough to catch a delayed
        // knockdown landing animation if the boar gets blasted.
        if (_completeBy < 0f && _maxChargePhaseReached >= 2 && _lastChargePhase == 2)
            _completeBy = _elapsed + 2f;
        if (_completeBy > 0f && _elapsed >= _completeBy) _complete = true;
        if (_elapsed >= MaxDuration) _complete = true;
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "=== Validation ===");
        DebugLog.Log(ScenarioLog, $"Boar knocked down at any point:    {_boarKnockedDown}");
        DebugLog.Log(ScenarioLog, $"Boar entered physics:              {_boarEnteredPhysics}");
        DebugLog.Log(ScenarioLog, $"Follow-through observed:           {_followThroughObserved}");
        DebugLog.Log(ScenarioLog, $"Max ChargePhase observed:          {_maxChargePhaseReached}");
        DebugLog.Log(ScenarioLog, $"Boar damaged target:               {_boarDamagedTarget}");

        // Pass = boar successfully tramples WITHOUT knocking himself down or entering physics.
        bool pass = !_boarKnockedDown && !_boarEnteredPhysics
                  && _followThroughObserved && _boarDamagedTarget;
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
