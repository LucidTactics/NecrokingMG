using Necroking.AI;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Lib;
using Necroking.Movement;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Regression for the "table-crafted zombie deer chases forever" bug.
///
/// The original ZombieDeerLeashScenario forced Archetype=HordeMinion by hand,
/// so it never exercised the real reanimation path. In actual play the table
/// craft (and potion raise) spawn via Simulation.SpawnZombieMinion. Before the
/// fix those used bare SpawnUnitByID, which applies only the def's legacy AI
/// enum ("AttackClosest" for the animal zombies) and NOT its archetype — so the
/// deer ran the leash-less legacy AI and pursued a fleeing wild deer off the map.
///
/// This scenario spawns the zombie deer through SpawnZombieMinion (no manual
/// archetype patching) and asserts:
///   1. it actually received the HordeMinion archetype, and
///   2. it gives up and returns at the leash instead of chasing to infinity.
/// </summary>
public class ZombieDeerCraftLeashScenario : ScenarioBase
{
    public override string Name => "zombie_deer_craft_leash";

    private uint _necroId, _zombieDeerId, _wildDeerId;
    private float _elapsed;
    private bool _complete;
    private bool _fail;
    private string _failReason = "";
    private bool _everChased;
    private float _maxDistFromHorde;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Zombie Deer Craft-Path Leash Test ===");

        var units = sim.UnitsMut;

        int nIdx = sim.SpawnUnitByID("necromancer", new Vec2(10f, 10f));
        units[nIdx].Archetype = 0;
        units[nIdx].AI = AIBehavior.IdleAtPoint;
        _necroId = units[nIdx].Id;
        sim.SetNecromancerIndex(nIdx);

        // Spawn via the CANONICAL reanimation helper — the exact path the necro
        // table and potion raise use. No manual archetype patching here: the
        // whole point is to prove the helper wires it.
        int zIdx = sim.SpawnZombieMinion("ZombieFemaleDeer", new Vec2(12f, 10f));
        if (zIdx < 0)
        {
            DebugLog.Log(ScenarioLog, "FAIL: SpawnZombieMinion(ZombieFemaleDeer) returned -1");
            _fail = true; _complete = true; return;
        }
        _zombieDeerId = units[zIdx].Id;

        // Assert the helper actually wired the HordeMinion archetype (the bug:
        // it was 0/None, so the unit fell through to the legacy AttackClosest AI).
        if (units[zIdx].Archetype != ArchetypeRegistry.HordeMinion)
        {
            _fail = true;
            _failReason = $"zombie deer Archetype={units[zIdx].Archetype} " +
                $"(expected HordeMinion={ArchetypeRegistry.HordeMinion}) — helper did not wire archetype";
            DebugLog.Log(ScenarioLog, "FAIL: " + _failReason);
            _complete = true; return;
        }
        if (!sim.Horde.IsInHorde(units[zIdx].Id))
        {
            _fail = true;
            _failReason = "zombie deer not enrolled in horde by SpawnZombieMinion";
            DebugLog.Log(ScenarioLog, "FAIL: " + _failReason);
            _complete = true; return;
        }
        DebugLog.Log(ScenarioLog, $"zombie deer spawned: archetype=HordeMinion, in horde=true");

        // Wild deer placed inside the horde aggro radius so the aggro scan from
        // the necro's position assigns it as the zombie deer's chase target; it
        // then flees east and tries to drag the chaser past the leash. (The
        // necromancer is pinned in OnTick, so its own IdleAtPoint aggro can't
        // pull it along.)
        int dIdx = sim.SpawnUnitByID("FemaleDeer", new Vec2(18f, 10f));
        if (dIdx < 0)
        {
            DebugLog.Log(ScenarioLog, "FAIL: could not spawn FemaleDeer");
            _fail = true; _complete = true; return;
        }
        _wildDeerId = units[dIdx].Id;

        ZoomOnLocation(15f, 10f, 32f);
        DebugLog.Log(ScenarioLog,
            $"necro@(10,10) zombieDeer@(12,10) wildDeer@(18,10) " +
            $"aggro={sim.Horde.AggroRadius:F1} leash={sim.Horde.LeashRadius:F1}");
    }

    public override void OnTick(Simulation sim, float dt)
    {
        if (_complete) return;
        _elapsed += dt;

        int zIdx = FindById(sim.Units, _zombieDeerId);
        int dIdx = FindById(sim.Units, _wildDeerId);
        int nIdx2 = FindById(sim.Units, _necroId);
        if (zIdx < 0 || dIdx < 0 || nIdx2 < 0) { _complete = true; return; }

        // Keep the necromancer pinned so the horde center stays put — geometry
        // matches "necro stands still while the zombie deer chases far east."
        sim.UnitsMut[nIdx2].PreferredVel = Vec2.Zero;
        sim.UnitsMut[nIdx2].Velocity = Vec2.Zero;
        sim.UnitsMut[nIdx2].Position = new Vec2(10f, 10f);

        // Track that a real chase happened and how far it dragged the deer, so a
        // future hollow pass (deer never engaged) is visible in the log.
        {
            var zNow = sim.Units[zIdx];
            if (zNow.Routine == 1 || zNow.Routine == 2) _everChased = true;
            float d = (zNow.Position - sim.Horde.CircleCenter).Length();
            if (d > _maxDistFromHorde) _maxDistFromHorde = d;
        }

        if (_elapsed % 1f < dt)
        {
            var z = sim.Units[zIdx];
            float distToCenter = (z.Position - sim.Horde.CircleCenter).Length();
            DebugLog.Log(ScenarioLog,
                $"t={_elapsed:F1}s zombie@({z.Position.X:F1},{z.Position.Y:F1}) " +
                $"hordeCenter@({sim.Horde.CircleCenter.X:F1},{sim.Horde.CircleCenter.Y:F1}) " +
                $"distFromHorde={distToCenter:F1}/{sim.Horde.LeashRadius:F1} routine={z.Routine}");
        }

        if (_elapsed > 15f)
        {
            var z = sim.Units[zIdx];
            float distToCenter = (z.Position - sim.Horde.CircleCenter).Length();
            float leash = sim.Horde.LeashRadius;
            // Routine 1=Chasing, 2=Engaged beyond 1.2× leash means it never gave up.
            bool stillChasing = z.Routine == 1 || z.Routine == 2;
            if (stillChasing && distToCenter > leash * 1.2f)
            {
                _fail = true;
                _failReason = $"After 15s zombie deer is {distToCenter:F1}u from horde center " +
                    $"(leash={leash:F1}) and STILL Routine={z.Routine}. Chased past the leash.";
            }
            DebugLog.Log(ScenarioLog,
                $"END t={_elapsed:F1}s zombie@distFromHorde={distToCenter:F1}/{leash:F1} routine={z.Routine} " +
                $"everChased={_everChased} maxDistFromHorde={_maxDistFromHorde:F1}");
            _complete = true;
        }
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        if (_fail) { DebugLog.Log(ScenarioLog, "FAIL: " + _failReason); return 1; }
        DebugLog.Log(ScenarioLog, "PASS: craft-path zombie deer wired HordeMinion + respected leash");
        return 0;
    }

    private static int FindById(UnitArrays units, uint id)
    {
        for (int i = 0; i < units.Count; i++)
            if (units[i].Id == id) return i;
        return -1;
    }
}
