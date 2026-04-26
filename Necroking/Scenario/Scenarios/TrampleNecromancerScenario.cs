using System.Collections.Generic;
using Necroking.AI;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Movement;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Reproduces a player-reported bug: "boar trample fires against the necromancer
/// but nothing happens — boar doesn't move into necromancer's tile, necromancer
/// isn't thrown back, no damage."
///
/// The necromancer is special: AI=PlayerControlled, Faction=Undead, ORCA-skipped.
/// This scenario spawns a real necromancer (via SetNecromancerIndex) and a boar
/// at trample range, lets the boar's AttackClosest AI pick the necromancer, and
/// verifies the same outcome the soldier scenario expects: charge → impact →
/// follow-through → necromancer launched into physics.
/// </summary>
public class TrampleNecromancerScenario : ScenarioBase
{
    public override string Name => "trample_necromancer";

    private float _elapsed;
    private bool _complete;
    private float _completeBy = -1f;
    private const float MaxDuration = 25f;
    private int _chargesObserved;
    private int _chargesWithFollowThrough;
    private int _chargesWithDamage;
    private int _necroHpAtChargeStart = -1;

    private uint _boarId;
    private uint _necroId;

    private Vec2 _necroStartPos;
    private Vec2 _boarStartPos;
    private float _necroMaxDisp;
    private bool _necroWentInPhysics;
    private bool _followThroughObserved;
    private bool _boarPassedNecroX;
    private byte _maxChargePhaseReached;
    private byte _lastChargePhase;
    private int _initialCombatLogCount;
    private int _necroHP0;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Trample vs. Necromancer Test ===");
        DebugLog.Log(ScenarioLog, "Repro for: trample fires but boar doesn't move / necro isn't launched / no damage.");

        var units = sim.UnitsMut;

        // Spawn necromancer with the same setup the real game uses.
        _necroStartPos = new Vec2(8f, 10f);
        int necroIdx = units.AddUnit(_necroStartPos, UnitType.Necromancer);
        units[necroIdx].AI = AIBehavior.PlayerControlled;
        units[necroIdx].Stats.MaxHP = 999;
        units[necroIdx].Stats.HP = 999;
        _necroId = units[necroIdx].Id;
        _necroHP0 = units[necroIdx].Stats.HP;
        sim.SetNecromancerIndex(necroIdx);
        DebugLog.Log(ScenarioLog,
            $"Necromancer: id={_necroId} pos=({_necroStartPos.X:F2},{_necroStartPos.Y:F2}) " +
            $"size={units[necroIdx].Size} radius={units[necroIdx].Radius:F2} " +
            $"AI={units[necroIdx].AI} Faction={units[necroIdx].Faction}");

        // Boar 6u west of necromancer — at the OUTER EDGE of the trample window
        // (TrampleMaxRange=6). This is where the running-necromancer-outruns-chase
        // bug surfaces: with TrampleMaxChaseDistance=4 and necro running at ~9u/s
        // vs boar at 11.5u/s, the boar's 4u of allowed chase only closes ~0.87u
        // of the 6u gap before MaxChase trips. Boar gives up far from the necro.
        _boarStartPos = new Vec2(2f, 10f);
        int boarIdx = sim.SpawnUnitByID("Boar", _boarStartPos);
        if (boarIdx < 0) { DebugLog.Log(ScenarioLog, "FAIL: could not spawn Boar"); _complete = true; return; }
        units[boarIdx].Faction = Faction.Animal;
        units[boarIdx].AI = AIBehavior.AttackClosest;
        units[boarIdx].FacingAngle = 0f;
        units[boarIdx].Stats.MaxHP = 9999;
        units[boarIdx].Stats.HP = 9999;
        _boarId = units[boarIdx].Id;

        // Force the boar to target the necromancer directly so AttackClosest
        // can't pick something else.
        units[boarIdx].Target = CombatTarget.Unit(_necroId);

        DebugLog.Log(ScenarioLog,
            $"Boar: id={_boarId} pos=({_boarStartPos.X:F2},{_boarStartPos.Y:F2}) " +
            $"size={units[boarIdx].Size} radius={units[boarIdx].Radius:F2} " +
            $"combatSpeed={units[boarIdx].Stats.CombatSpeed:F1}");

        // Surround the necromancer with 4 skeletons (a typical defensive horde
        // formation). They're size 2, smaller than the boar, so they shouldn't
        // block the charge — they should be transit-trampled. But their ORCA
        // dance might make the necromancer harder to reach.
        int s1 = units.AddUnit(new Vec2(_necroStartPos.X - 0.7f, _necroStartPos.Y), UnitType.Skeleton);
        int s2 = units.AddUnit(new Vec2(_necroStartPos.X + 0.7f, _necroStartPos.Y), UnitType.Skeleton);
        int s3 = units.AddUnit(new Vec2(_necroStartPos.X, _necroStartPos.Y - 0.7f), UnitType.Skeleton);
        int s4 = units.AddUnit(new Vec2(_necroStartPos.X, _necroStartPos.Y + 0.7f), UnitType.Skeleton);
        foreach (int s in new[] { s1, s2, s3, s4 })
        {
            units[s].AI = AIBehavior.IdleAtPoint;
            units[s].Faction = Faction.Undead;
            units[s].Stats.MaxHP = 9999;
            units[s].Stats.HP = 9999;
        }
        DebugLog.Log(ScenarioLog, "Spawned 4 skeleton hordies around necromancer (size 2, transit fodder).");

        _initialCombatLogCount = sim.CombatLog.Entries.Count;
        ZoomOnLocation(8f, 10f, 48f);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;
        if (_complete) return;

        // Drive necromancer with player input — RUNNING east (away from boar).
        // This is the actual repro: a running necro (speed = 5 * 1.8 = 9u/s)
        // pulls away from the boar (charge speed 11.5u/s) just slowly enough that
        // the boar never closes within TrampleImpactRange before exhausting
        // TrampleMaxChaseDistance=4u. The boar gives up and fires impact-in-place
        // at its own position, far from the necro.
        sim.SetNecromancerInput(new Vec2(1f, 0f), running: true);

        int bIdx = FindByID(sim.Units, _boarId);
        int nIdx = FindByID(sim.Units, _necroId);

        if (bIdx >= 0)
        {
            byte phase = sim.Units[bIdx].ChargePhase;
            if (phase == 3) _followThroughObserved = true;
            if (phase != _lastChargePhase)
            {
                DebugLog.Log(ScenarioLog,
                    $"t={_elapsed:F2}s: Boar phase {_lastChargePhase}→{phase} " +
                    $"pos=({sim.Units[bIdx].Position.X:F2},{sim.Units[bIdx].Position.Y:F2}) " +
                    $"vel=({sim.Units[bIdx].Velocity.X:F2},{sim.Units[bIdx].Velocity.Y:F2}) " +
                    $"InPhysics={sim.Units[bIdx].InPhysics} " +
                    $"PendingAttack={(sim.Units[bIdx].PendingAttack.IsNone ? "none" : "yes")} " +
                    $"PostAtkTimer={sim.Units[bIdx].PostAttackTimer:F2}");
                if (phase == 1 && _lastChargePhase == 0)
                {
                    _chargesObserved++;
                    _necroHpAtChargeStart = nIdx >= 0 ? sim.Units[nIdx].Stats.HP : -1;
                    DebugLog.Log(ScenarioLog,
                        $"  charge #{_chargesObserved} begins; necro HP={_necroHpAtChargeStart}");
                }
                if (phase == 3 && nIdx >= 0)
                {
                    _chargesWithFollowThrough++;
                    DebugLog.Log(ScenarioLog,
                        $"  on impact: necro pos=({sim.Units[nIdx].Position.X:F2},{sim.Units[nIdx].Position.Y:F2}) " +
                        $"InPhysics={sim.Units[nIdx].InPhysics} HP={sim.Units[nIdx].Stats.HP}/{_necroHP0}");
                }
                if (phase == 2 && nIdx >= 0 && _necroHpAtChargeStart >= 0)
                {
                    int hpNow = sim.Units[nIdx].Stats.HP;
                    if (hpNow < _necroHpAtChargeStart) _chargesWithDamage++;
                    DebugLog.Log(ScenarioLog,
                        $"  charge complete: necro HP {_necroHpAtChargeStart}→{hpNow} (delta={_necroHpAtChargeStart - hpNow})");
                    _necroHpAtChargeStart = -1;
                }
                _lastChargePhase = phase;
                if (phase > _maxChargePhaseReached) _maxChargePhaseReached = phase;
            }
            if (sim.Units[bIdx].Position.X >= _necroStartPos.X) _boarPassedNecroX = true;
        }

        if (nIdx >= 0)
        {
            if (sim.Units[nIdx].InPhysics) _necroWentInPhysics = true;
            float disp = (sim.Units[nIdx].Position - _necroStartPos).Length();
            if (disp > _necroMaxDisp) _necroMaxDisp = disp;
        }

        // Keep running for several charges so we can spot intermittent failures.
        if (_chargesObserved >= 5) _complete = true;
        if (_elapsed >= MaxDuration) _complete = true;
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "=== Validation ===");
        var units = sim.Units;
        int nIdx = FindByID(units, _necroId);
        int bIdx = FindByID(units, _boarId);
        int finalHP = nIdx >= 0 ? units[nIdx].Stats.HP : -1;
        Vec2 finalBoarPos = bIdx >= 0 ? units[bIdx].Position : Vec2.Zero;

        int trampleHits = 0, trampleAttempts = 0;
        var entries = sim.CombatLog.Entries;
        for (int i = _initialCombatLogCount; i < entries.Count; i++)
        {
            if (entries[i].WeaponName != "Trample") continue;
            trampleAttempts++;
            if (entries[i].Outcome == CombatLogOutcome.Hit) trampleHits++;
        }

        DebugLog.Log(ScenarioLog, $"Trample combat-log entries: {trampleAttempts} attempts ({trampleHits} hits)");
        DebugLog.Log(ScenarioLog, $"Necro final HP: {finalHP}/{_necroHP0} (damage={_necroHP0 - finalHP})");
        DebugLog.Log(ScenarioLog, $"Necro maxDisp: {_necroMaxDisp:F2}");
        DebugLog.Log(ScenarioLog, $"Boar final pos: ({finalBoarPos.X:F2},{finalBoarPos.Y:F2}) (necro startX={_necroStartPos.X:F2})");
        DebugLog.Log(ScenarioLog, $"Max ChargePhase observed: {_maxChargePhaseReached}");

        DebugLog.Log(ScenarioLog,
            $"Charges observed: {_chargesObserved}, with follow-through: {_chargesWithFollowThrough}, with damage: {_chargesWithDamage}");

        bool chargeFired = trampleAttempts > 0;
        bool reachedFollowThrough = _followThroughObserved;
        bool necroLaunched = _necroWentInPhysics;
        bool necroDisplaced = _necroMaxDisp > 0.5f;
        bool boarDroveThrough = _boarPassedNecroX;
        bool reachedRecovery = _maxChargePhaseReached >= 2;
        // Mechanics check: every observed charge must reach follow-through. We do
        // NOT require damage on every charge — that's dice-roll dependent and a
        // legitimate miss against a high-Defense target shouldn't fail the test.
        // The "necro launched / displaced" + per-charge follow-through covers the
        // physical fix; damage is incidental.
        bool everyChargeWorked = _chargesObserved > 0
                              && _chargesWithFollowThrough == _chargesObserved;

        DebugLog.Log(ScenarioLog, $"Check - charge fired:                 {chargeFired}");
        DebugLog.Log(ScenarioLog, $"Check - follow-through reached:       {reachedFollowThrough}");
        DebugLog.Log(ScenarioLog, $"Check - necromancer launched:         {necroLaunched}");
        DebugLog.Log(ScenarioLog, $"Check - necromancer displaced > 0.5u: {necroDisplaced}");
        DebugLog.Log(ScenarioLog, $"Check - boar drove past necro X:      {boarDroveThrough}");
        DebugLog.Log(ScenarioLog, $"Check - reached recovery (>=2):       {reachedRecovery}");

        DebugLog.Log(ScenarioLog, $"Check - every observed charge worked: {everyChargeWorked}");

        bool pass = chargeFired && reachedFollowThrough && necroLaunched
                  && necroDisplaced && boarDroveThrough && reachedRecovery
                  && everyChargeWorked;
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
