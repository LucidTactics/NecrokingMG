using System.Collections.Generic;
using Necroking.AI;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Movement;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Repro for: "GreatBoar charge knocks down EVERYONE in the horde, including
/// units that never physically touched the boar (e.g. the necromancer at the
/// back)." Expected: only units actually overlapping the boar's body during the
/// charge should be knocked.
///
/// Scene: GreatBoar to the west, charging into a packed horde line. The horde
/// is 4 skeletons in a tight east-west column with the necromancer at the back
/// (far east, well past the boar's reach). The boar's TrampleMaxRange targets
/// the front skeleton; everything else should be untouched (or only the directly
/// adjacent ones).
/// </summary>
public class TrampleHordeKnockdownScenario : ScenarioBase
{
    public override string Name => "trample_horde_knockdown";

    private float _elapsed;
    private bool _complete;
    private float _completeBy = -1f;
    private const float MaxDuration = 6f;

    private uint _boarId;
    private uint _necroId;
    private readonly List<uint> _frontSkeletonIds = new();   // expected: knocked (touching boar at impact)
    private readonly List<uint> _backSkeletonIds = new();    // expected: NOT knocked (never touch boar)

    private Vec2 _boarStartPos;
    private Vec2 _boarMaxPos;       // boar's furthest x position during charge
    // "Was ever knocked" tracking — Incap state can recover before scenario end,
    // so we poll every tick and mark the flag the first time it goes true.
    private readonly HashSet<uint> _everKnocked = new();
    private readonly HashSet<uint> _everInPhysics = new();

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Trample Horde Knockdown Test ===");
        DebugLog.Log(ScenarioLog, "GreatBoar charges a packed horde line; only directly-touched units should be knocked.");

        var units = sim.UnitsMut;

        // GreatBoar to the west — charging east.
        _boarStartPos = new Vec2(2f, 10f);
        int boarIdx = sim.SpawnUnitByID("GreatBoar", _boarStartPos);
        if (boarIdx < 0) { DebugLog.Log(ScenarioLog, "FAIL: spawn GreatBoar"); _complete = true; return; }
        units[boarIdx].Faction = Faction.Animal;
        units[boarIdx].AI = AIBehavior.AttackClosest;
        units[boarIdx].FacingAngle = 0f;
        units[boarIdx].Stats.MaxHP = 99999;
        units[boarIdx].Stats.HP = 99999;
        _boarId = units[boarIdx].Id;

        // Front skeletons — directly in the boar's path. These SHOULD be knocked.
        // Two skeletons clustered around the impact point (~x=7).
        AddFrontSkeleton(units, new Vec2(7f, 10f));         // primary target
        AddFrontSkeleton(units, new Vec2(7f, 10.7f));        // adjacent (touching boar at impact)
        AddFrontSkeleton(units, new Vec2(7f, 9.3f));         // adjacent

        // Back skeletons — well clear of impact point, behind the necromancer.
        // These should NOT be knocked.
        AddBackSkeleton(units, new Vec2(11f, 10.7f));
        AddBackSkeleton(units, new Vec2(11f, 10f));
        AddBackSkeleton(units, new Vec2(11f, 9.3f));

        // Necromancer at the back of the formation — ~4u east of the impact zone.
        // Way clear of boar's body. Should NOT be knocked.
        int necroIdx = units.AddUnit(new Vec2(11f, 10f), UnitType.Necromancer);
        units[necroIdx].AI = AIBehavior.PlayerControlled;
        units[necroIdx].Faction = Faction.Undead;
        units[necroIdx].Stats.MaxHP = 9999;
        units[necroIdx].Stats.HP = 9999;
        _necroId = units[necroIdx].Id;
        sim.SetNecromancerIndex(necroIdx);
        // Note: the back-skeleton at (11, 10) overlaps necro position; reposition.
        if (_backSkeletonIds.Count > 0)
        {
            for (int i = 0; i < units.Count; i++)
            {
                if (units[i].Id == _backSkeletonIds[1])
                {
                    units[i].Position = new Vec2(11.7f, 10f);
                    break;
                }
            }
        }

        // Force boar to target the front skeleton so AI doesn't pick necro.
        if (_frontSkeletonIds.Count > 0)
            units[boarIdx].Target = CombatTarget.Unit(_frontSkeletonIds[0]);

        DebugLog.Log(ScenarioLog,
            $"GreatBoar id={_boarId} radius={units[boarIdx].Radius:F2} at (2,10), targeting front skel id={_frontSkeletonIds[0]}");
        DebugLog.Log(ScenarioLog, $"Front skeletons (should be knocked): {string.Join(",", _frontSkeletonIds)}");
        DebugLog.Log(ScenarioLog, $"Back skeletons + necro (should NOT be knocked): {string.Join(",", _backSkeletonIds)}, necro={_necroId}");

        _boarMaxPos = _boarStartPos;
        ZoomOnLocation(8f, 10f, 36f);
    }

    private void AddFrontSkeleton(UnitArrays units, Vec2 pos)
    {
        int s = units.AddUnit(pos, UnitType.Skeleton);
        units[s].AI = AIBehavior.IdleAtPoint;
        units[s].Faction = Faction.Undead;
        units[s].Stats.MaxHP = 9999;
        units[s].Stats.HP = 9999;
        _frontSkeletonIds.Add(units[s].Id);
    }

    private void AddBackSkeleton(UnitArrays units, Vec2 pos)
    {
        int s = units.AddUnit(pos, UnitType.Skeleton);
        units[s].AI = AIBehavior.IdleAtPoint;
        units[s].Faction = Faction.Undead;
        units[s].Stats.MaxHP = 9999;
        units[s].Stats.HP = 9999;
        _backSkeletonIds.Add(units[s].Id);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;
        if (_complete) return;

        int bIdx = FindByID(sim.Units, _boarId);
        if (bIdx >= 0)
        {
            if (sim.Units[bIdx].Position.X > _boarMaxPos.X) _boarMaxPos = sim.Units[bIdx].Position;
            // End ~1s after boar enters recovery (ChargePhase=2) so all collateral physics settles.
            if (_completeBy < 0f && sim.Units[bIdx].ChargePhase == 2)
                _completeBy = _elapsed + 1.5f;
        }

        // Per-tick "was ever knocked" snapshot for everyone we care about.
        for (int i = 0; i < sim.Units.Count; i++)
        {
            uint id = sim.Units[i].Id;
            if (sim.Units[i].Incap.Active || HadKnockdownBuff(sim.Units[i])) _everKnocked.Add(id);
            if (sim.Units[i].InPhysics) _everInPhysics.Add(id);
        }

        if (_completeBy > 0f && _elapsed >= _completeBy) _complete = true;
        if (_elapsed >= MaxDuration) _complete = true;
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "=== Validation ===");
        DebugLog.Log(ScenarioLog, $"Boar's furthest position: ({_boarMaxPos.X:F2},{_boarMaxPos.Y:F2})");

        var units = sim.Units;

        int frontKnocked = 0;
        foreach (uint id in _frontSkeletonIds)
        {
            int idx = FindByID(units, id);
            if (idx < 0) continue;
            bool everKnocked = _everKnocked.Contains(id);
            bool everInPhysics = _everInPhysics.Contains(id);
            DebugLog.Log(ScenarioLog,
                $"  Front skel id={id} finalPos=({units[idx].Position.X:F2},{units[idx].Position.Y:F2}) " +
                $"everKnocked={everKnocked} everInPhysics={everInPhysics}");
            if (everKnocked || everInPhysics) frontKnocked++;
        }

        int backKnocked = 0;
        foreach (uint id in _backSkeletonIds)
        {
            int idx = FindByID(units, id);
            if (idx < 0) continue;
            bool everKnocked = _everKnocked.Contains(id);
            bool everInPhysics = _everInPhysics.Contains(id);
            DebugLog.Log(ScenarioLog,
                $"  Back  skel id={id} finalPos=({units[idx].Position.X:F2},{units[idx].Position.Y:F2}) " +
                $"everKnocked={everKnocked} everInPhysics={everInPhysics}");
            if (everKnocked || everInPhysics) backKnocked++;
        }

        int nIdx = FindByID(units, _necroId);
        bool necroEverKnocked = _everKnocked.Contains(_necroId);
        bool necroEverInPhysics = _everInPhysics.Contains(_necroId);
        DebugLog.Log(ScenarioLog,
            $"  Necromancer id={_necroId} finalPos=({(nIdx >= 0 ? units[nIdx].Position.X : -1):F2}," +
            $"{(nIdx >= 0 ? units[nIdx].Position.Y : -1):F2}) " +
            $"everKnocked={necroEverKnocked} everInPhysics={necroEverInPhysics}");

        DebugLog.Log(ScenarioLog, $"Front knocked: {frontKnocked}/{_frontSkeletonIds.Count} (≥1 expected — they touched the boar)");
        DebugLog.Log(ScenarioLog, $"Back knocked: {backKnocked}/{_backSkeletonIds.Count} (must be 0)");
        DebugLog.Log(ScenarioLog, $"Necromancer knocked: {necroEverKnocked || necroEverInPhysics} (must be FALSE)");

        bool frontHit = frontKnocked > 0;          // body-contact slam still works
        bool backUntouched = backKnocked == 0;     // chain physics not cascading
        bool necroSafe = !necroEverKnocked && !necroEverInPhysics;
        bool pass = frontHit && backUntouched && necroSafe;
        DebugLog.Log(ScenarioLog, $"Overall: {(pass ? "PASS" : "FAIL")}");
        return pass ? 0 : 1;
    }

    /// <summary>Returns true if the unit currently has the buff_knockdown buff
    /// applied. Catches "Incap.Active just cleared but buff was applied during the
    /// scenario" — in practice we just check Incap.Active right now since the
    /// scenario ends shortly after impact, before recovery completes.</summary>
    private static bool HadKnockdownBuff(in Unit u)
    {
        for (int i = 0; i < u.ActiveBuffs.Count; i++)
            if (u.ActiveBuffs[i].BuffDefID == "buff_knockdown") return true;
        return false;
    }

    private static int FindByID(UnitArrays units, uint id)
    {
        for (int i = 0; i < units.Count; i++)
            if (units[i].Id == id) return i;
        return -1;
    }
}
