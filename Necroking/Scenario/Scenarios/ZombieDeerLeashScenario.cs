using Necroking.AI;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Movement;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Reproduces the user's report: zombie deer chases a wild deer, the wild deer
/// runs far, the zombie deer should give up at the leash radius and return —
/// but per the user it chases forever. Test: necromancer stationary, zombie
/// deer in horde, wild female deer placed nearby that will flee on sight.
/// After 15s the zombie deer must either be back in Following/Returning, or
/// inside the leash boundary.
/// </summary>
public class ZombieDeerLeashScenario : ScenarioBase
{
    public override string Name => "zombie_deer_leash";

    private uint _necroId, _zombieDeerId, _wildDeerId;
    private float _elapsed;
    private bool _complete;
    private bool _fail;
    private string _failReason = "";

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Zombie Deer Leash Test ===");

        var units = sim.UnitsMut;

        int nIdx = sim.SpawnUnitByID("necromancer", new Vec2(10f, 10f));
        units[nIdx].Archetype = 0;
        units[nIdx].AI = AIBehavior.IdleAtPoint;
        _necroId = units[nIdx].Id;
        sim.SetNecromancerIndex(nIdx);

        int zIdx = sim.SpawnUnitByID("ZombieFemaleDeer", new Vec2(12f, 10f));
        if (zIdx < 0)
        {
            DebugLog.Log(ScenarioLog, "FAIL: could not spawn ZombieFemaleDeer");
            _fail = true; _complete = true; return;
        }
        units[zIdx].Archetype = ArchetypeRegistry.HordeMinion;
        units[zIdx].Faction = Faction.Undead;
        sim.Horde.AddUnit(units[zIdx].Id);
        _zombieDeerId = units[zIdx].Id;

        // Wild deer placed far enough that the necromancer's IdleAtPoint AI
        // (which auto-chases enemies within 10 units) doesn't take the bait.
        // The zombie deer's awareness is wider — once it's added to the horde,
        // the horde aggro scan picks up the wild deer from the necromancer's
        // position and assigns it as the zombie deer's chase target.
        int dIdx = sim.SpawnUnitByID("FemaleDeer", new Vec2(25f, 10f));
        if (dIdx < 0)
        {
            DebugLog.Log(ScenarioLog, "FAIL: could not spawn FemaleDeer");
            _fail = true; _complete = true; return;
        }
        _wildDeerId = units[dIdx].Id;

        ZoomOnLocation(15f, 10f, 32f);
        DebugLog.Log(ScenarioLog, $"necro@(10,10) zombieDeer@(12,10) wildDeer@(18,10) leash={sim.Horde.LeashRadius}");
    }

    public override void OnTick(Simulation sim, float dt)
    {
        if (_complete) return;
        _elapsed += dt;

        int zIdx = FindById(sim.Units, _zombieDeerId);
        int dIdx = FindById(sim.Units, _wildDeerId);
        int nIdx2 = FindById(sim.Units, _necroId);
        if (zIdx < 0 || dIdx < 0 || nIdx2 < 0) { _complete = true; return; }

        // Stationary necromancer: zero his velocity each frame so the IdleAtPoint
        // AI doesn't drag him east toward the (out-of-AI-range) wild deer, and
        // ORCA collisions from the chasing minion don't shove him. This matches
        // "the necromancer runs far from the chase" geometrically — he stays
        // put while the zombie deer chases far east.
        sim.UnitsMut[nIdx2].PreferredVel = Vec2.Zero;
        sim.UnitsMut[nIdx2].Velocity = Vec2.Zero;
        sim.UnitsMut[nIdx2].Position = new Vec2(10f, 10f);

        if (_elapsed % 1f < dt)
        {
            var z = sim.Units[zIdx];
            float distToCenter = (z.Position - sim.Horde.CircleCenter).Length();
            int nIdx = FindById(sim.UnitsMut, _necroId);
            var nPos = nIdx >= 0 ? sim.Units[nIdx].Position : new Vec2(-999, -999);
            DebugLog.Log(ScenarioLog,
                $"t={_elapsed:F1}s zombie@({z.Position.X:F1},{z.Position.Y:F1}) " +
                $"hordeCenter@({sim.Horde.CircleCenter.X:F1},{sim.Horde.CircleCenter.Y:F1}) " +
                $"necro@({nPos.X:F1},{nPos.Y:F1}) " +
                $"distFromHorde={distToCenter:F1}/{sim.Horde.LeashRadius:F1} " +
                $"routine={z.Routine}");
        }

        if (_elapsed > 15f)
        {
            var z = sim.Units[zIdx];
            float distToCenter = (z.Position - sim.Horde.CircleCenter).Length();
            float leash = sim.Horde.LeashRadius;
            // Routine 0=Following, 3=Returning are OK. Routine 1=Chasing, 2=Engaged
            // are only OK if still within the leash boundary.
            bool stillChasing = z.Routine == 1 || z.Routine == 2;
            if (stillChasing && distToCenter > leash * 1.2f)
            {
                _fail = true;
                _failReason = $"After 15s zombie deer is {distToCenter:F1}u from horde center " +
                    $"(leash = {leash:F1}) and STILL Routine={z.Routine}. Should have given up.";
            }
            DebugLog.Log(ScenarioLog,
                $"END t={_elapsed:F1}s zombie@distFromHorde={distToCenter:F1}/{leash:F1} routine={z.Routine}");
            _complete = true;
        }
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        if (_fail) { DebugLog.Log(ScenarioLog, "FAIL: " + _failReason); return 1; }
        DebugLog.Log(ScenarioLog, "PASS: zombie deer respected leash");
        return 0;
    }

    private static int FindById(Necroking.Movement.UnitArrays units, uint id)
    {
        for (int i = 0; i < units.Count; i++)
            if (units[i].Id == id) return i;
        return -1;
    }
}
