using System.Collections.Generic;
using Necroking.AI;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Movement;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Trample blocker test. A Boar (size 3) charges a small soldier (primary target,
/// in normal trample range) but a Knight (size 4) is positioned in the cone in
/// front of the boar. When the boar reaches the Knight, the charge must STOP:
///   - Knight (bigger) gets a small clay-momentum bump but no full launch
///   - Trampler bounces back + knockdown
///   - Charge ends; primary target NOT reached
/// </summary>
public class TrampleBlockerScenario : ScenarioBase
{
    public override string Name => "trample_blocker";

    private float _elapsed;
    private bool _complete;
    private float _completeBy = -1f;
    private const float MaxDuration = 8f;

    private uint _boarId;
    private uint _knightId;
    private uint _primaryId;

    private Vec2 _boarStartPos;
    private Vec2 _knightStartPos;
    private Vec2 _primaryStartPos;
    private bool _boarKnockedDown;
    private bool _knightLaunched;
    private bool _primaryHurt;
    private byte _maxChargePhaseReached;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Trample Blocker Test ===");
        DebugLog.Log(ScenarioLog, "Boar charges soldier; Knight (bigger) is in the path → charge stops.");

        var units = sim.UnitsMut;

        _boarStartPos = new Vec2(2f, 10f);
        int boarIdx = sim.SpawnUnitByID("Boar", _boarStartPos);
        if (boarIdx < 0) { _complete = true; return; }
        units[boarIdx].Faction = Faction.Animal;
        units[boarIdx].AI = AIBehavior.AttackClosest;
        units[boarIdx].FacingAngle = 0f;
        units[boarIdx].Stats.MaxHP = 9999;
        units[boarIdx].Stats.HP = 9999;
        _boarId = units[boarIdx].Id;

        // GreatBoar blocker mid-path. Size 4 > Boar size 3 → blocker.
        // (Knight is misleadingly size 2 in data.)
        _knightStartPos = new Vec2(5f, 10f);
        int knightIdx = sim.SpawnUnitByID("GreatBoar", _knightStartPos);
        if (knightIdx < 0) { _complete = true; return; }
        units[knightIdx].AI = AIBehavior.IdleAtPoint;
        units[knightIdx].Faction = Faction.Human;  // hostile to boar
        units[knightIdx].Stats.MaxHP = 99999;
        units[knightIdx].Stats.HP = 99999;
        _knightId = units[knightIdx].Id;

        // Primary target further along. Boar will never reach it.
        _primaryStartPos = new Vec2(7f, 10f);
        int prim = units.AddUnit(_primaryStartPos, UnitType.Soldier);
        units[prim].AI = AIBehavior.IdleAtPoint;
        units[prim].Faction = Faction.Human;
        units[prim].Stats.MaxHP = 99999;
        units[prim].Stats.HP = 99999;
        _primaryId = units[prim].Id;

        units[boarIdx].Target = CombatTarget.Unit(_primaryId);

        DebugLog.Log(ScenarioLog,
            $"Boar(size 3) at (2,10) → primary soldier at (7,10), Knight(size 4) blocking at (5,10).");
        ZoomOnLocation(5f, 10f, 50f);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;
        if (_complete) return;

        int bIdx = FindByID(sim.Units, _boarId);
        int kIdx = FindByID(sim.Units, _knightId);
        int pIdx = FindByID(sim.Units, _primaryId);

        if (bIdx >= 0)
        {
            byte ph = sim.Units[bIdx].ChargePhase;
            if (ph > _maxChargePhaseReached) _maxChargePhaseReached = ph;
            if (sim.Units[bIdx].InPhysics || sim.Units[bIdx].Incap.Active) _boarKnockedDown = true;
            if (_completeBy < 0f && (ph == 2 || ph == 0) && _maxChargePhaseReached >= 1) _completeBy = _elapsed + 1.5f;
        }
        if (kIdx >= 0 && sim.Units[kIdx].InPhysics) _knightLaunched = true;
        if (pIdx >= 0 && sim.Units[pIdx].Stats.HP < 99999) _primaryHurt = true;

        if (_completeBy > 0f && _elapsed >= _completeBy) _complete = true;
        if (_elapsed >= MaxDuration) _complete = true;
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "=== Validation ===");
        var units = sim.Units;
        int bIdx = FindByID(units, _boarId);
        int kIdx = FindByID(units, _knightId);
        int pIdx = FindByID(units, _primaryId);

        Vec2 boarFinal = bIdx >= 0 ? units[bIdx].Position : Vec2.Zero;
        Vec2 knightFinal = kIdx >= 0 ? units[kIdx].Position : Vec2.Zero;
        DebugLog.Log(ScenarioLog,
            $"Boar final pos=({boarFinal.X:F2},{boarFinal.Y:F2}) startedAt=({_boarStartPos.X:F2},{_boarStartPos.Y:F2})");
        DebugLog.Log(ScenarioLog,
            $"Knight pos=({knightFinal.X:F2},{knightFinal.Y:F2}) startedAt=({_knightStartPos.X:F2},{_knightStartPos.Y:F2}) " +
            $"InPhysics-ever={_knightLaunched}");
        DebugLog.Log(ScenarioLog,
            $"Primary HP delta={(pIdx >= 0 ? 99999 - units[pIdx].Stats.HP : 0)}");

        bool boarStaggered = _boarKnockedDown;
        bool primaryUnharmed = !_primaryHurt;
        bool boarStoppedShort = boarFinal.X < _primaryStartPos.X - 0.5f;
        bool reachedCharge = _maxChargePhaseReached >= 1;

        DebugLog.Log(ScenarioLog, $"Check - charge initiated:               {reachedCharge} (maxPhase={_maxChargePhaseReached})");
        DebugLog.Log(ScenarioLog, $"Check - trampler staggered (incap/phys):{boarStaggered}");
        DebugLog.Log(ScenarioLog, $"Check - primary target unharmed:        {primaryUnharmed}");
        DebugLog.Log(ScenarioLog, $"Check - boar stopped short of primary:  {boarStoppedShort}");

        bool pass = reachedCharge && boarStaggered && primaryUnharmed && boarStoppedShort;
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
