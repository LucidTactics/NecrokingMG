using System;
using Necroking.AI;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Movement;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Pounce reach test: a wolf pounces a target that teleports away mid-flight.
/// Verifies that with the new combined-radius landing check, the pounce misses
/// cleanly (no damage) when the target outruns the leap.
///
/// Method: spawn wolf + soldier in pounce range. Watch for the wolf to enter
/// JumpPhase==2 (Airborne) — at that moment, teleport the soldier 20u away.
/// At landing, the soldier should be far out of reach (dist > Radius+Radius+0.5),
/// the pounce should miss, and soldier HP should be unchanged.
/// </summary>
public class PounceOutrunScenario : ScenarioBase
{
    public override string Name => "pounce_outrun";

    private float _elapsed;
    private bool _complete;
    private const float MaxDuration = 8f;

    private uint _wolfId;
    private uint _soldierId;
    private int _soldierHP0;
    private bool _teleportedAway;
    private byte _maxPhaseReached;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Pounce Outrun Test ===");
        DebugLog.Log(ScenarioLog, "Wolf pounces; target teleports away mid-flight; pounce should miss.");

        var units = sim.UnitsMut;

        // Wolf in pounce range of soldier.
        int wolfIdx = sim.SpawnUnitByID("Wolf", new Vec2(2f, 10f));
        if (wolfIdx < 0)
        {
            // Fallback: any unit with a Pounce weapon. Most predator wildlife has one.
            wolfIdx = sim.SpawnUnitByID("DireWolf", new Vec2(2f, 10f));
        }
        if (wolfIdx < 0) { DebugLog.Log(ScenarioLog, "FAIL: spawn Wolf/DireWolf"); _complete = true; return; }
        units[wolfIdx].Faction = Faction.Animal;
        units[wolfIdx].AI = AIBehavior.AttackClosest;
        units[wolfIdx].FacingAngle = 0f;
        units[wolfIdx].Stats.MaxHP = 9999;
        units[wolfIdx].Stats.HP = 9999;
        _wolfId = units[wolfIdx].Id;

        // Soldier 6u east — squarely in the wolf's PounceMaxRange (default 8).
        int s = units.AddUnit(new Vec2(8f, 10f), UnitType.Soldier);
        units[s].AI = AIBehavior.IdleAtPoint;
        units[s].Faction = Faction.Human;
        units[s].Stats.MaxHP = 9999;
        units[s].Stats.HP = 9999;
        units[s].Stats.Defense = 0;  // would-be hit always lands if target is in reach
        _soldierId = units[s].Id;
        _soldierHP0 = units[s].Stats.HP;

        units[wolfIdx].Target = CombatTarget.Unit(_soldierId);

        DebugLog.Log(ScenarioLog,
            $"Wolf id={_wolfId} R={units[wolfIdx].Radius:F2}, soldier id={_soldierId} R={units[s].Radius:F2}");
        ZoomOnLocation(8f, 10f, 48f);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;
        if (_complete) return;

        int wIdx = FindByID(sim.Units, _wolfId);
        int sIdx = FindByID(sim.Units, _soldierId);
        if (wIdx < 0 || sIdx < 0) { _complete = true; return; }

        byte phase = sim.Units[wIdx].JumpPhase;
        if (phase > _maxPhaseReached) _maxPhaseReached = phase;

        // First tick the wolf is Airborne (phase 2): yank the soldier 20u away.
        if (!_teleportedAway && phase == 2)
        {
            sim.UnitsMut[sIdx].Position = new Vec2(_elapsed > 0f ? 30f : 30f, 10f);
            _teleportedAway = true;
            DebugLog.Log(ScenarioLog,
                $"t={_elapsed:F2}s: wolf airborne, teleporting soldier to (30, 10) — should be out of pounce reach");
        }

        // End ~1.5s after wolf returns to phase 0 (recovery done).
        if (phase == 0 && _maxPhaseReached >= 4 && _elapsed > 1f)
        {
            _complete = true;
        }
        if (_elapsed >= MaxDuration) _complete = true;
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "=== Validation ===");
        var units = sim.Units;
        int sIdx = FindByID(units, _soldierId);
        int finalHP = sIdx >= 0 ? units[sIdx].Stats.HP : -1;
        int dmg = _soldierHP0 - finalHP;

        DebugLog.Log(ScenarioLog, $"Pounce phase reached: {_maxPhaseReached} (wanted ≥ 4)");
        DebugLog.Log(ScenarioLog, $"Teleport fired: {_teleportedAway}");
        DebugLog.Log(ScenarioLog, $"Soldier final HP: {finalHP}/{_soldierHP0} (damage={dmg})");

        bool pouncedAndLanded = _maxPhaseReached >= 4;
        bool teleportFired = _teleportedAway;
        bool missed = dmg == 0;  // <-- the actual feature under test

        DebugLog.Log(ScenarioLog, $"Check - pounce ran through landing:  {pouncedAndLanded}");
        DebugLog.Log(ScenarioLog, $"Check - mid-flight teleport fired:   {teleportFired}");
        DebugLog.Log(ScenarioLog, $"Check - soldier took NO damage:      {missed}");

        bool pass = pouncedAndLanded && teleportFired && missed;
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
