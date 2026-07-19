using Necroking.AI;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Lib;
using Necroking.Movement;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Archer-fires-arrows regression test: an archer stands 10u from a stationary
/// deer (inside bow range) and must actually SPAWN arrow projectiles — not just
/// mime the Ranged1 draw. Guards the queued-shot pipeline end to end:
/// RangedUnitHandler.TryQueueShot → Ranged1 effect frame → AttackResolver
/// FireArrowAt → ProjectileManager. The original bug: PostAttackTimer window
/// shorter than the anim's release frame, so the SwingJanitor cleared every
/// pending swing before the arrow spawned (log/combat.log full of
/// "queued swing expired unresolved", zero arrows).
///
/// The deer's archetype is stripped so it stands still — the unit under test is
/// the archer; a fleeing deer would just add flakiness. High HP on the deer so
/// kills don't end the test early.
/// </summary>
public class ArcherVsDeerScenario : ScenarioBase
{
    public override string Name => "archer_vs_deer";

    private float _elapsed;
    private bool _complete;
    private const float MaxDuration = 15f;

    private uint _archerId;
    private uint _deerId;
    private int _arrowSpawns;
    private int _prevProjectileCount;
    private int _deerStartHP;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Archer vs Deer: arrows must actually fire ===");

        var units = sim.UnitsMut;

        int a = sim.SpawnUnitByID("archer", new Vec2(22f, 32f));
        if (a < 0)
        {
            DebugLog.Log(ScenarioLog, "FAIL EARLY: SpawnUnitByID(\"archer\") returned -1");
            _complete = true;
            return;
        }
        _archerId = units[a].Id;

        int d = sim.SpawnUnitByID("FemaleDeer", new Vec2(32f, 32f)); // 10u east of the archer
        if (d < 0)
        {
            DebugLog.Log(ScenarioLog, "FAIL EARLY: SpawnUnitByID(\"FemaleDeer\") returned -1");
            _complete = true;
            return;
        }
        _deerId = units[d].Id;

        // Stationary target: legacy idle instead of DeerHerd (which would flee).
        units[d].Archetype = 0;
        units[d].AI = AIBehavior.IdleAtPoint;
        units[d].Stats.MaxHP = 9999;
        units[d].Stats.HP = 9999;
        _deerStartHP = units[d].Stats.HP;

        // Aggro through the real sentry ladder: Aggressive + AlertTarget makes
        // EvaluateSentryRoutine do the TransitionTo(Combat) + Target stamp itself.
        units[a].AlertState = (byte)UnitAlertState.Aggressive;
        units[a].AlertTarget = _deerId;

        ZoomOnLocation(27f, 32f, 50f);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;
        if (_complete) return;

        // Count spawn EVENTS (count increases), not live projectiles — arrows
        // despawn on impact so a same-frame poll can miss them.
        int count = sim.Projectiles.Projectiles.Count;
        if (count > _prevProjectileCount)
            _arrowSpawns += count - _prevProjectileCount;
        _prevProjectileCount = count;

        // Two volleys proves the cycle (fire → cooldown → fire), then stop early.
        if (_arrowSpawns >= 2) _complete = true;
        if (_elapsed >= MaxDuration) _complete = true;
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "=== Validation ===");

        int d = FindByID(sim.Units, _deerId);
        int deerHP = d >= 0 ? sim.Units[d].Stats.HP : -1;
        DebugLog.Log(ScenarioLog,
            $"Elapsed: {_elapsed:F1}s, arrow spawns: {_arrowSpawns}, " +
            $"deer HP: {deerHP}/{_deerStartHP}");

        bool fired = _arrowSpawns >= 1;
        DebugLog.Log(ScenarioLog, $"Check - archer fired at least one arrow: {fired}");

        DebugLog.Log(ScenarioLog, $"Overall: {(fired ? "PASS" : "FAIL")}");
        return fired ? 0 : 1;
    }

    private static int FindByID(UnitArrays units, uint id)
    {
        for (int i = 0; i < units.Count; i++)
            if (units[i].Id == id) return i;
        return -1;
    }
}
