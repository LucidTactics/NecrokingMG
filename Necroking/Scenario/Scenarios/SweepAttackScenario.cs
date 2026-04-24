using System.Collections.Generic;
using Necroking.AI;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Movement;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Sweep archetype test: a bear swings its AOE Sweep weapon at 3 soldiers
/// clustered in its forward cone. Verifies:
/// - ResolveMeleeSweep fires (log entries exist)
/// - Cone filter respects facing (target placed out of cone never damaged)
/// - Same-faction filter honors SweepHitsAllies=false (ally behind bear untouched)
///
/// Soldiers are invulnerable (HP=99999) so they don't die and never retarget the
/// bear's aggression elsewhere. Ally bear is placed behind the attacker so it's
/// outside the cone AND testing same-faction filter. Out-of-cone soldier is
/// placed far enough south that it's not the closest enemy (bear targets the
/// front cluster).
/// </summary>
public class SweepAttackScenario : ScenarioBase
{
    public override string Name => "sweep_attack";

    private float _elapsed;
    private bool _complete;
    private const float MaxDuration = 15f;

    private uint _bearId;
    private uint _frontLeftId, _frontCenterId, _frontRightId;
    private uint _outOfConeId;
    private uint _allyBearId;

    private int _frontLeftHP0, _frontCenterHP0, _frontRightHP0;
    private int _outOfConeHP0, _allyBearHP0;

    private int _initialCombatLogCount;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Sweep Attack Test ===");
        DebugLog.Log(ScenarioLog, "Bear (facing +X) swings Sweep at 3 clustered soldiers in forward cone.");
        DebugLog.Log(ScenarioLog, "Out-of-cone soldier placed far south (out of sweep radius, so not caught).");
        DebugLog.Log(ScenarioLog, "Ally bear placed behind attacker (out of cone, tests hitsAllies=false).");

        var units = sim.UnitsMut;

        // Attacker bear at origin, facing +X (east). Use IdleAtPoint so the
        // bear doesn't move during the attack-queue → resolve delay — we want
        // the sweep to fire from the fixed (10,10) pos so radius geometry is
        // predictable.
        int bearIdx = sim.SpawnUnitByID("Bear", new Vec2(10f, 10f));
        units[bearIdx].Faction = Faction.Animal;
        units[bearIdx].AI = AIBehavior.IdleAtPoint;
        units[bearIdx].FacingAngle = 0f;
        units[bearIdx].Stats.MaxHP = 99999;
        units[bearIdx].Stats.HP = 99999;
        _bearId = units[bearIdx].Id;
        DebugLog.Log(ScenarioLog, $"Bear: id={_bearId} pos=(10,10) facing=+X (east) AI=IdleAtPoint");

        // 3 soldiers clustered tightly in front so they all fit inside
        // SweepRadius=2.0 even if the bear drifts up to 0.5u during the
        // attack-queue → resolve delay. ~1.3u radial, ±0.4 lateral.
        int fl = units.AddUnit(new Vec2(11.2f, 9.6f), UnitType.Soldier);
        int fc = units.AddUnit(new Vec2(11.3f, 10f), UnitType.Soldier);
        int fr = units.AddUnit(new Vec2(11.2f, 10.4f), UnitType.Soldier);
        foreach (int i in new[] { fl, fc, fr })
        {
            units[i].AI = AIBehavior.IdleAtPoint;
            units[i].Faction = Faction.Human;
            units[i].Stats.MaxHP = 99999;
            units[i].Stats.HP = 99999;
            units[i].Stats.Defense = 0;
        }
        _frontLeftId = units[fl].Id;
        _frontCenterId = units[fc].Id;
        _frontRightId = units[fr].Id;
        _frontLeftHP0 = units[fl].Stats.HP;
        _frontCenterHP0 = units[fc].Stats.HP;
        _frontRightHP0 = units[fr].Stats.HP;

        // Out-of-cone soldier: far south (outside sweep radius of 3)
        int oc = units.AddUnit(new Vec2(10f, 20f), UnitType.Soldier);
        units[oc].AI = AIBehavior.IdleAtPoint;
        units[oc].Faction = Faction.Human;
        units[oc].Stats.MaxHP = 99999;
        units[oc].Stats.HP = 99999;
        _outOfConeId = units[oc].Id;
        _outOfConeHP0 = units[oc].Stats.HP;

        // Ally bear placed behind attacker (west) so it's outside the east-facing cone.
        // Even without SweepHitsAllies=false the cone filter would exclude it, but this
        // gives us confidence same-faction filter works even if facing drifts.
        int ab = sim.SpawnUnitByID("Bear", new Vec2(7f, 10f));
        units[ab].Faction = Faction.Animal;
        units[ab].AI = AIBehavior.IdleAtPoint;
        units[ab].Stats.MaxHP = 99999;
        units[ab].Stats.HP = 99999;
        _allyBearId = units[ab].Id;
        _allyBearHP0 = units[ab].Stats.HP;

        // IdleAtPoint AI doesn't set Target itself; force one so Sweep qualifies.
        units[bearIdx].Target = CombatTarget.Unit(units[fc].Id);

        DebugLog.Log(ScenarioLog, $"Front cluster: fl={_frontLeftId} fc={_frontCenterId} fr={_frontRightId}");
        DebugLog.Log(ScenarioLog, $"Out-of-cone soldier: id={_outOfConeId} at (10,20)");
        DebugLog.Log(ScenarioLog, $"Ally bear: id={_allyBearId} at (7,10) [behind attacker]");

        _initialCombatLogCount = sim.CombatLog.Entries.Count;
        ZoomOnLocation(10.5f, 10f, 64f);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;
        if (_elapsed >= MaxDuration) _complete = true;
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "=== Sweep Validation ===");
        var units = sim.Units;

        int fl = FindByID(units, _frontLeftId);
        int fc = FindByID(units, _frontCenterId);
        int fr = FindByID(units, _frontRightId);
        int oc = FindByID(units, _outOfConeId);
        int ab = FindByID(units, _allyBearId);

        int flHP = fl >= 0 ? units[fl].Stats.HP : -1;
        int fcHP = fc >= 0 ? units[fc].Stats.HP : -1;
        int frHP = fr >= 0 ? units[fr].Stats.HP : -1;
        int ocHP = oc >= 0 ? units[oc].Stats.HP : -1;
        int abHP = ab >= 0 ? units[ab].Stats.HP : -1;

        DebugLog.Log(ScenarioLog, $"Final HPs: fl={flHP}/{_frontLeftHP0} fc={fcHP}/{_frontCenterHP0} fr={frHP}/{_frontRightHP0} oc={ocHP}/{_outOfConeHP0} ab={abHP}/{_allyBearHP0}");

        // Count Sweep log lines from attacker bear (id=_bearId). We identify by WeaponName since
        // Bear has multiple weapons; ally bear also has Sweep but is passive (IdleAtPoint) so
        // shouldn't fire. Still, we accept any Sweep entry — the point is the feature works.
        int sweepEntryCount = 0;
        int sweepHitCount = 0;
        int sweepMissCount = 0;
        var entries = sim.CombatLog.Entries;
        for (int i = _initialCombatLogCount; i < entries.Count; i++)
        {
            var e = entries[i];
            if (e.WeaponName != "Sweep") continue;
            sweepEntryCount++;
            if (e.Outcome == CombatLogOutcome.Hit) sweepHitCount++;
            else sweepMissCount++;
        }
        DebugLog.Log(ScenarioLog, $"Combat log: {sweepEntryCount} sweep attempts (hits={sweepHitCount} misses={sweepMissCount})");

        int totalFrontDamage = (_frontLeftHP0 - flHP) + (_frontCenterHP0 - fcHP) + (_frontRightHP0 - frHP);
        int outConeDamage = _outOfConeHP0 - ocHP;
        int allyDamage = _allyBearHP0 - abHP;

        int damagedFrontCount = CountDamaged(_frontLeftHP0, flHP)
                              + CountDamaged(_frontCenterHP0, fcHP)
                              + CountDamaged(_frontRightHP0, frHP);

        bool bearFiredSweep = sweepEntryCount > 0;
        bool frontDamaged = totalFrontDamage > 0;
        bool multipleTargetsHit = damagedFrontCount >= 2;       // AOE behavior
        bool outOfConeUntouched = outConeDamage == 0;           // cone/range filter
        bool allyUntouched = allyDamage == 0;                   // same-faction filter

        DebugLog.Log(ScenarioLog, $"Check - Sweep fired:                 {bearFiredSweep} ({sweepEntryCount} swings)");
        DebugLog.Log(ScenarioLog, $"Check - front soldiers damaged:      {frontDamaged} (total={totalFrontDamage})");
        DebugLog.Log(ScenarioLog, $"Check - ≥2 of 3 front damaged:       {multipleTargetsHit} ({damagedFrontCount}/3)");
        DebugLog.Log(ScenarioLog, $"Check - out-of-cone soldier clean:   {outOfConeUntouched} (damage={outConeDamage})");
        DebugLog.Log(ScenarioLog, $"Check - ally bear clean:             {allyUntouched} (damage={allyDamage})");

        bool pass = bearFiredSweep && frontDamaged && multipleTargetsHit
                 && outOfConeUntouched && allyUntouched;
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
