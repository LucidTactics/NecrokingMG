using Necroking.AI;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Lib;
using Necroking.Movement;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Measures how well slow horde minions (zombie deer, combatSpeed 1.3) keep up
/// with a necromancer moving at NORMAL speed (combatSpeed 2.5). This is the case
/// the follow-effort matrix exists to fix: the deer's Walk speed (1.3) is far
/// below the necro's normal pace (2.5), so a naive "Walk when near slot" rule
/// leaves them lagging.
///
/// The necromancer is driven east at a steady normal speed via PreferredVel (the
/// same technique the leash scenarios use — real velocity, so the horde circle
/// center leads correctly and IsNecroMoving reports true). We log each deer's
/// distance to the necromancer and to its formation slot every 0.5s, and assert
/// the steady-state trailing distance stays bounded — i.e. they keep pace rather
/// than falling behind without limit.
///
/// NOTE: only exercises the "necro stopped" and "necro moving (normal)" columns
/// of the matrix. The "sprinting" column keys off NecroSprintT (the shift ramp),
/// which a scenario can't drive without real player input, so it's not covered
/// here — verify sprinting follow live.
/// </summary>
public class ZombieDeerFollowSpeedScenario : ScenarioBase
{
    public override string Name => "zombie_deer_follow_speed";

    private const int DeerCount = 4;
    private const float NecroNormalSpeed = 2.5f;   // necromancer combatSpeed (no sprint)
    private const float SettleTime = 3f;           // ignore the first few seconds (initial regroup)
    private const float MoveEndTime = 16f;         // stop the necro here, then verify settle
    private const float EndTime = 19f;

    private uint _necroId;
    private readonly uint[] _deerIds = new uint[DeerCount];
    private float _elapsed;
    private bool _complete;
    private bool _fail;
    private string _failReason = "";

    // Steady-state tracking (during movement, after settle).
    private float _maxTrailDuringMove;   // worst distance-to-necro while moving
    private float _sumTrail;
    private int _trailSamples;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Zombie Deer Follow-Speed Test ===");

        var units = sim.UnitsMut;

        // MoveToPoint walks the necro east at MaxSpeed through the real movement
        // system (so it has genuine velocity → IsNecroMoving true → horde center
        // leads). Driving PreferredVel directly does NOT work: the scenario's
        // OnTick runs AFTER sim.Tick, so the necro's own AI re-zeros it each frame.
        int nIdx = sim.SpawnUnitByID("necromancer", new Vec2(10f, 10f));
        units[nIdx].Archetype = 0;
        units[nIdx].AI = AIBehavior.MoveToPoint;
        units[nIdx].MoveTarget = new Vec2(300f, 10f); // far east — keeps walking the whole run
        units[nIdx].Stats.CombatSpeed = NecroNormalSpeed; // normal pace, no sprint (MaxSpeed derives from this)
        _necroId = units[nIdx].Id;
        sim.SetNecromancerIndex(nIdx);

        for (int i = 0; i < DeerCount; i++)
        {
            int zIdx = sim.SpawnZombieMinion("ZombieFemaleDeer", new Vec2(9f + i * 0.7f, 10f));
            if (zIdx < 0)
            {
                _fail = true; _failReason = "SpawnZombieMinion returned -1"; _complete = true; return;
            }
            _deerIds[i] = units[zIdx].Id;
        }

        DebugLog.Log(ScenarioLog,
            $"necro@(10,10) + {DeerCount} ZombieFemaleDeer. Driving necro east at normal speed {NecroNormalSpeed}.");
        DebugLog.Log(ScenarioLog,
            "deer Walk=1.3 Jog=3.9 Sprint=11.7 vs necro normal=2.5 — Walk can't keep up, Jog can.");
        ZoomOnLocation(15f, 10f, 28f);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        if (_complete) return;
        _elapsed += dt;

        int nIdx = FindById(sim.Units, _necroId);
        if (nIdx < 0) { _complete = true; return; }

        bool moving = _elapsed < MoveEndTime;
        // Halt the necro for the regroup phase by parking its MoveTarget on itself.
        if (!moving)
            sim.UnitsMut[nIdx].MoveTarget = sim.Units[nIdx].Position;

        var necroPos = sim.Units[nIdx].Position;

        // Sample trailing distance during steady-state movement (after settle).
        if (moving && _elapsed >= SettleTime)
        {
            float worst = 0f;
            foreach (uint id in _deerIds)
            {
                int di = FindById(sim.Units, id);
                if (di < 0) continue;
                float d = (sim.Units[di].Position - necroPos).Length();
                if (d > worst) worst = d;
            }
            if (worst > _maxTrailDuringMove) _maxTrailDuringMove = worst;
            _sumTrail += worst;
            _trailSamples++;
        }

        if (_elapsed % 1f < dt)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"t={_elapsed:F1}s necro=({necroPos.X:F1},{necroPos.Y:F1}) vel={sim.Units[nIdx].Velocity.Length():F1} moving={moving} | ");
            for (int i = 0; i < DeerCount; i++)
            {
                int di = FindById(sim.Units, _deerIds[i]);
                if (di < 0) { sb.Append("dead "); continue; }
                float dn = (sim.Units[di].Position - necroPos).Length();
                float dslot = sim.Horde.GetTargetPosition(_deerIds[i], out var slot)
                    ? (sim.Units[di].Position - slot).Length() : -1f;
                sb.Append($"[{i} dNecro={dn:F1} dSlot={dslot:F1} r={sim.Units[di].Routine} sub={sim.Units[di].Subroutine} eff={sim.Units[di].MoveEffort} ms={sim.Units[di].MaxSpeed:F1} spd={sim.Units[di].Velocity.Length():F1}] ");
            }
            DebugLog.Log(ScenarioLog, sb.ToString());
        }

        if (_elapsed >= EndTime)
        {
            float avgTrail = _trailSamples > 0 ? _sumTrail / _trailSamples : -1f;
            // Stopped-state: every deer should have regrouped close to the necro.
            float worstStopped = 0f;
            foreach (uint id in _deerIds)
            {
                int di = FindById(sim.Units, id);
                if (di < 0) continue;
                float d = (sim.Units[di].Position - necroPos).Length();
                if (d > worstStopped) worstStopped = d;
            }

            DebugLog.Log(ScenarioLog,
                $"END moving: worstTrail={_maxTrailDuringMove:F1} avgTrail={avgTrail:F1} | stopped: worst={worstStopped:F1}");

            // Keep-pace bound: a deer whose Jog easily outruns the necro should
            // never fall further than the formation radius + a margin. The old
            // MaxSpeed-clobbered bug made the trail grow without limit (≈0.5u/s),
            // so over this ~13s steady-state window it sails past this bound; the
            // fix holds it near the formation radius.
            const float TrailBound = 10f;
            const float StoppedBound = 10f;
            if (_maxTrailDuringMove > TrailBound)
            {
                _fail = true;
                _failReason = $"deer fell {_maxTrailDuringMove:F1}u behind moving necro (bound {TrailBound}) — not keeping pace";
            }
            else if (worstStopped > StoppedBound)
            {
                _fail = true;
                _failReason = $"after stop, worst deer still {worstStopped:F1}u from necro (bound {StoppedBound}) — failed to regroup";
            }
            _complete = true;
        }
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        if (_fail) { DebugLog.Log(ScenarioLog, "FAIL: " + _failReason); return 1; }
        DebugLog.Log(ScenarioLog, "PASS: zombie deer kept pace while moving and regrouped on stop");
        return 0;
    }

    private static int FindById(UnitArrays units, uint id)
    {
        for (int i = 0; i < units.Count; i++)
            if (units[i].Id == id) return i;
        return -1;
    }
}
