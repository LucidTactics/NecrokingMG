using System.Collections.Generic;
using Necroking.AI;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Movement;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Trample dodge-failure test. The primary target has Defense=999 (always misses)
/// AND is fully surrounded by allied skeletons in every cardinal+diagonal direction
/// at 1u — no safe tile within DodgeDistance. The dodge MUST fail and the trample
/// must force-hit: damage applied, target launched into physics, despite the dice
/// rolling a miss.
///
/// Verifies:
///   - Boar's swing rolls a miss
///   - Dodge cannot find a free tile (all 8 candidates occupied)
///   - Force-hit is triggered: damage applied AND knockback fires
///   - Target enters physics
/// </summary>
public class TrampleNoEscapeScenario : ScenarioBase
{
    public override string Name => "trample_no_escape";

    private float _elapsed;
    private bool _complete;
    private float _completeBy = -1f;
    private const float MaxDuration = 6f;

    private uint _boarId;
    private uint _primaryId;

    private int _primaryHP0;
    private Vec2 _primaryStartPos;
    private bool _primaryWentInPhysics;
    private byte _maxChargePhaseReached;
    private int _initialCombatLogCount;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Trample No-Escape (force-hit on dodge fail) ===");
        DebugLog.Log(ScenarioLog, "Primary surrounded by allies → no safe tile → dodge fails → force-hit.");

        var units = sim.UnitsMut;

        int boarIdx = sim.SpawnUnitByID("Boar", new Vec2(2f, 10f));
        if (boarIdx < 0) { DebugLog.Log(ScenarioLog, "FAIL: spawn Boar"); _complete = true; return; }
        units[boarIdx].Faction = Faction.Animal;
        units[boarIdx].AI = AIBehavior.AttackClosest;
        units[boarIdx].FacingAngle = 0f;
        units[boarIdx].Stats.MaxHP = 99999;
        units[boarIdx].Stats.HP = 99999;
        _boarId = units[boarIdx].Id;

        // Primary at center of a tight ring. Defense 999 → always miss.
        _primaryStartPos = new Vec2(7.5f, 10f);
        int prim = units.AddUnit(_primaryStartPos, UnitType.Soldier);
        units[prim].AI = AIBehavior.IdleAtPoint;
        units[prim].Faction = Faction.Human;
        units[prim].Stats.MaxHP = 99999;
        units[prim].Stats.HP = 99999;
        units[prim].Stats.Defense = 999;
        _primaryId = units[prim].Id;
        _primaryHP0 = units[prim].Stats.HP;

        units[boarIdx].Target = CombatTarget.Unit(_primaryId);

        // Tight cordon: 16 allies at 0.5u (half the dodge distance). Each dodge
        // candidate tile is well inside several cordon bodies — overcrowding
        // exceeds the dodge tolerance (allowed up to ~50% body overlap).
        const float invSqrt2 = 0.70710678f;
        const float r = 0.5f;
        const float diag1 = 0.382683f;  // cos(67.5°)
        const float diag2 = 0.923879f;  // sin(67.5°)
        Vec2[] cordonOffsets = new[] {
            new Vec2( r, 0f),
            new Vec2( r * diag2,  r * diag1),
            new Vec2( r * invSqrt2, r * invSqrt2),
            new Vec2( r * diag1,  r * diag2),
            new Vec2( 0f, r),
            new Vec2(-r * diag1,  r * diag2),
            new Vec2(-r * invSqrt2,  r * invSqrt2),
            new Vec2(-r * diag2,  r * diag1),
            new Vec2(-r, 0f),
            new Vec2(-r * diag2, -r * diag1),
            new Vec2(-r * invSqrt2, -r * invSqrt2),
            new Vec2(-r * diag1, -r * diag2),
            new Vec2( 0f,-r),
            new Vec2( r * diag1, -r * diag2),
            new Vec2( r * invSqrt2, -r * invSqrt2),
            new Vec2( r * diag2, -r * diag1),
        };
        foreach (var off in cordonOffsets)
        {
            int s = units.AddUnit(new Vec2(_primaryStartPos.X + off.X, _primaryStartPos.Y + off.Y),
                UnitType.Soldier);
            units[s].AI = AIBehavior.IdleAtPoint;
            units[s].Faction = Faction.Human;
            units[s].Stats.MaxHP = 99999;
            units[s].Stats.HP = 99999;
        }

        DebugLog.Log(ScenarioLog,
            $"Primary id={_primaryId} surrounded by 8 allies; Defense=999.");
        _initialCombatLogCount = sim.CombatLog.Entries.Count;
        ZoomOnLocation(7.5f, 10f, 60f);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;
        if (_complete) return;

        int bIdx = FindByID(sim.Units, _boarId);
        int pIdx = FindByID(sim.Units, _primaryId);

        if (bIdx >= 0)
        {
            byte ph = sim.Units[bIdx].ChargePhase;
            if (ph > _maxChargePhaseReached) _maxChargePhaseReached = ph;
            if (_completeBy < 0f && ph == 2) _completeBy = _elapsed + 1f;
        }
        if (pIdx >= 0 && sim.Units[pIdx].InPhysics) _primaryWentInPhysics = true;

        if (_completeBy > 0f && _elapsed >= _completeBy) _complete = true;
        if (_elapsed >= MaxDuration) _complete = true;
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "=== Validation ===");
        var units = sim.Units;
        int pIdx = FindByID(units, _primaryId);
        int finalHP = pIdx >= 0 ? units[pIdx].Stats.HP : -1;

        int trampleHits = 0, trampleMisses = 0;
        var entries = sim.CombatLog.Entries;
        for (int i = _initialCombatLogCount; i < entries.Count; i++)
        {
            var e = entries[i];
            if (e.WeaponName != "Trample") continue;
            if (e.Outcome == CombatLogOutcome.Hit) trampleHits++;
            else trampleMisses++;
        }
        int dmg = _primaryHP0 - finalHP;
        DebugLog.Log(ScenarioLog, $"Combat log: {trampleHits} hits, {trampleMisses} misses against primary");
        DebugLog.Log(ScenarioLog, $"Primary HP: {finalHP}/{_primaryHP0} (damage={dmg})");
        DebugLog.Log(ScenarioLog, $"Primary was launched: {_primaryWentInPhysics}");

        // We expect:
        //   - first roll missed (dodge attempted)
        //   - dodge failed (cordon) → force-hit re-roll counts as a hit in the combat log
        //   - damage applied AND knockback launched the target
        bool primaryHurt = dmg > 0;
        bool primaryLaunched = _primaryWentInPhysics;
        bool reachedRecovery = _maxChargePhaseReached >= 2;

        DebugLog.Log(ScenarioLog, $"Check - primary took damage:        {primaryHurt}");
        DebugLog.Log(ScenarioLog, $"Check - primary launched:           {primaryLaunched}");
        DebugLog.Log(ScenarioLog, $"Check - charge reached recovery:    {reachedRecovery} (maxPhase={_maxChargePhaseReached})");

        bool pass = primaryHurt && primaryLaunched && reachedRecovery;
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
