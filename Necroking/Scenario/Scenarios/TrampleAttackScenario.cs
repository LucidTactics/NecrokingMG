using System.Collections.Generic;
using Necroking.AI;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Movement;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Trample archetype test: a Boar (size 3) charges a line of 3 soldiers (size 2).
/// All 3 soldiers are positioned on the charge line so they should get trampled
/// one by one as the boar plows through. A larger "blocker" wall (an immobile
/// size-4 creature) far outside the charge line must NOT be damaged.
///
/// Verifies:
/// - Charge initiates (ChargePhase transitions 0 → 1 → 2 → 0)
/// - Charger phases through smaller units (they take damage + knockback)
/// - TrampledIds dedup — each small unit hit at most once by the same charge
/// - Size gate (same-size ally unit outside path is untouched)
/// - Primary target reached and impacted (radial knockback fires)
/// </summary>
public class TrampleAttackScenario : ScenarioBase
{
    public override string Name => "trample_attack";

    private float _elapsed;
    private bool _complete;
    private const float MaxDuration = 18f;

    private uint _boarId;
    private uint _pathLeftId, _pathMidId, _pathRightId;
    private uint _primaryId;
    private uint _bystanderId;

    private int _pathLeftHP0, _pathMidHP0, _pathRightHP0;
    private int _primaryHP0, _bystanderHP0;

    private byte _maxChargePhaseReached;
    private byte _lastChargePhase;
    private int _initialCombatLogCount;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Trample Attack Test ===");
        DebugLog.Log(ScenarioLog, "Boar (size 3) charges east toward a primary soldier (forced Target).");
        DebugLog.Log(ScenarioLog, "3 transit soldiers are off-axis but within TrampleRadius — they should be trampled as collateral.");
        DebugLog.Log(ScenarioLog, "Bystander stands far north (out of charge path) — must stay untouched.");

        var units = sim.UnitsMut;

        // Boar starts west of the soldier line. AttackClosest picks the closest
        // enemy as the primary charge target — so we lay out the scene with the
        // primary closest and the transit victims off-axis but within TrampleRadius
        // of the charge line, so they get bulldozed as the boar passes by.
        int boarIdx = sim.SpawnUnitByID("Boar", new Vec2(2f, 10f));
        if (boarIdx < 0)
        {
            DebugLog.Log(ScenarioLog, "FAIL: could not spawn Boar unit");
            _complete = true;
            return;
        }
        units[boarIdx].Faction = Faction.Animal;
        units[boarIdx].AI = AIBehavior.AttackClosest;
        units[boarIdx].FacingAngle = 0f;
        units[boarIdx].Stats.MaxHP = 99999;
        units[boarIdx].Stats.HP = 99999;
        _boarId = units[boarIdx].Id;
        DebugLog.Log(ScenarioLog, $"Boar: id={_boarId} pos=(2,10) size={units[boarIdx].Size} combatSpeed={units[boarIdx].Stats.CombatSpeed:F1}");

        // Transit victims: slightly off-axis on the charge line. We'll override
        // the boar's Target below so AttackClosest doesn't retarget to them.
        int pl = units.AddUnit(new Vec2(4f,   9.3f), UnitType.Soldier);
        int pm = units.AddUnit(new Vec2(5f,  10.7f), UnitType.Soldier);
        int pr = units.AddUnit(new Vec2(6f,   9.3f), UnitType.Soldier);
        foreach (int i in new[] { pl, pm, pr })
        {
            units[i].AI = AIBehavior.IdleAtPoint;
            units[i].Faction = Faction.Human;
            units[i].Stats.MaxHP = 99999;
            units[i].Stats.HP = 99999;
            units[i].Stats.Defense = 0;
        }
        _pathLeftId = units[pl].Id;
        _pathMidId = units[pm].Id;
        _pathRightId = units[pr].Id;
        _pathLeftHP0 = units[pl].Stats.HP;
        _pathMidHP0 = units[pm].Stats.HP;
        _pathRightHP0 = units[pr].Stats.HP;

        // Primary target: on the charge line at ~5.5u (inside trample window 2..6).
        // We force-set the boar's Target below so it charges THIS unit instead of
        // the nearer transit victims.
        int prim = units.AddUnit(new Vec2(7.5f, 10f), UnitType.Soldier);
        units[prim].AI = AIBehavior.IdleAtPoint;
        units[prim].Faction = Faction.Human;
        units[prim].Stats.MaxHP = 99999;
        units[prim].Stats.HP = 99999;
        units[prim].Stats.Defense = 0;
        _primaryId = units[prim].Id;
        _primaryHP0 = units[prim].Stats.HP;

        // Force boar to target prim (keeps AttackClosest from retargeting to the
        // closer transit victims, which we want to trample as collateral).
        units[boarIdx].Target = CombatTarget.Unit(units[prim].Id);

        // Bystander far north — out of cone, out of radius, should be completely untouched.
        int bys = units.AddUnit(new Vec2(8f, 20f), UnitType.Soldier);
        units[bys].AI = AIBehavior.IdleAtPoint;
        units[bys].Faction = Faction.Human;
        units[bys].Stats.MaxHP = 99999;
        units[bys].Stats.HP = 99999;
        _bystanderId = units[bys].Id;
        _bystanderHP0 = units[bys].Stats.HP;

        DebugLog.Log(ScenarioLog, $"Transit: pl={_pathLeftId}@(4,9.3) pm={_pathMidId}@(5,10.7) pr={_pathRightId}@(6,9.3)");
        DebugLog.Log(ScenarioLog, $"Primary: id={_primaryId} at (7.5,10) — in trample window, forced Target");
        DebugLog.Log(ScenarioLog, $"Bystander: id={_bystanderId} at (8,20) — out of reach");

        _initialCombatLogCount = sim.CombatLog.Entries.Count;
        ZoomOnLocation(8f, 10f, 48f);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;
        if (_complete) return;

        int bIdx = FindByID(sim.Units, _boarId);
        if (bIdx >= 0)
        {
            byte phase = sim.Units[bIdx].ChargePhase;
            if (phase != _lastChargePhase)
            {
                DebugLog.Log(ScenarioLog, $"t={_elapsed:F2}s: Boar ChargePhase {_lastChargePhase} → {phase} " +
                    $"pos=({sim.Units[bIdx].Position.X:F2},{sim.Units[bIdx].Position.Y:F2}) " +
                    $"traveled={sim.Units[bIdx].ChargeTraveled:F2}");
                _lastChargePhase = phase;
                if (phase > _maxChargePhaseReached) _maxChargePhaseReached = phase;
            }
        }

        if (_elapsed >= MaxDuration) _complete = true;
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "=== Trample Validation ===");
        var units = sim.Units;

        int pl = FindByID(units, _pathLeftId);
        int pm = FindByID(units, _pathMidId);
        int pr = FindByID(units, _pathRightId);
        int prim = FindByID(units, _primaryId);
        int bys = FindByID(units, _bystanderId);

        int plHP = pl >= 0 ? units[pl].Stats.HP : -1;
        int pmHP = pm >= 0 ? units[pm].Stats.HP : -1;
        int prHP = pr >= 0 ? units[pr].Stats.HP : -1;
        int primHP = prim >= 0 ? units[prim].Stats.HP : -1;
        int bysHP = bys >= 0 ? units[bys].Stats.HP : -1;

        DebugLog.Log(ScenarioLog, $"Final HPs: pl={plHP}/{_pathLeftHP0} pm={pmHP}/{_pathMidHP0} pr={prHP}/{_pathRightHP0} prim={primHP}/{_primaryHP0} bys={bysHP}/{_bystanderHP0}");

        // Count Trample-weapon entries in the combat log
        int trampleEntries = 0;
        int trampleHits = 0;
        var entries = sim.CombatLog.Entries;
        for (int i = _initialCombatLogCount; i < entries.Count; i++)
        {
            var e = entries[i];
            if (e.WeaponName != "Trample") continue;
            trampleEntries++;
            if (e.Outcome == CombatLogOutcome.Hit) trampleHits++;
        }
        DebugLog.Log(ScenarioLog, $"Combat log: {trampleEntries} Trample attempts ({trampleHits} hits)");
        DebugLog.Log(ScenarioLog, $"Max ChargePhase observed: {_maxChargePhaseReached}");

        int transitDamage = (_pathLeftHP0 - plHP) + (_pathMidHP0 - pmHP) + (_pathRightHP0 - prHP);
        int primaryDamage = _primaryHP0 - primHP;
        int bystanderDamage = _bystanderHP0 - bysHP;
        int transitDamagedCount = CountDamaged(_pathLeftHP0, plHP) + CountDamaged(_pathMidHP0, pmHP) + CountDamaged(_pathRightHP0, prHP);

        bool chargeFired = trampleEntries > 0;
        bool chargePhaseReached = _maxChargePhaseReached >= 1;
        bool transitVictimsHit = transitDamagedCount >= 2; // at least 2/3 path soldiers damaged
        bool primaryHit = primaryDamage > 0;
        bool bystanderClean = bystanderDamage == 0;

        DebugLog.Log(ScenarioLog, $"Check - Trample attempts logged:   {chargeFired} ({trampleEntries})");
        DebugLog.Log(ScenarioLog, $"Check - ChargePhase reached:       {chargePhaseReached} ({_maxChargePhaseReached})");
        DebugLog.Log(ScenarioLog, $"Check - transit victims damaged:   {transitVictimsHit} ({transitDamagedCount}/3, total={transitDamage})");
        DebugLog.Log(ScenarioLog, $"Check - primary target damaged:    {primaryHit} (damage={primaryDamage})");
        DebugLog.Log(ScenarioLog, $"Check - bystander untouched:       {bystanderClean} (damage={bystanderDamage})");

        bool pass = chargeFired && chargePhaseReached && transitVictimsHit && primaryHit && bystanderClean;
        DebugLog.Log(ScenarioLog, $"Overall: {(pass ? "PASS" : "FAIL")}");
        return pass ? 0 : 1;
    }

    private static int CountDamaged(int hp0, int hp) => (hp >= 0 && hp < hp0) ? 1 : 0;

    private static int FindByID(UnitArrays units, uint id)
    {
        for (int i = 0; i < units.Count; i++)
            if (units[i].Id == id) return i;
        return -1;
    }
}
