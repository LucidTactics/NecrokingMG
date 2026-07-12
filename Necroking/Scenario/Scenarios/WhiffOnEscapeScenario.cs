using Necroking.AI;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Lib;
using Necroking.Movement;
using Necroking.Render;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Verifies commit-bound melee choreography end-to-end: every queued swing must
/// produce a combat-log outcome — Hit/Miss when the target is still there at the
/// animation's impact frame, Whiff when it escaped during the windup — and never
/// resolve silently or land damage on a target far out of reach.
///
/// Driven deterministically like ChaseAttackAnimScenario: a zombie deer engages a
/// dummy soldier held in melee range. Alternating cycles:
///   - ESCAPE cycle: the instant a swing is queued (PendingAttack set), the dummy
///     is teleported 6u away (beyond reach + ImpactWhiffTolerance) → the impact
///     frame must log a Whiff, no damage.
///   - STAY cycle: the dummy stays planted → the impact frame must log Hit/Miss.
///
/// Pass criteria: ≥2 whiffs from escape cycles, ≥1 Hit/Miss from stay cycles,
/// every swing accounted for (log entries == queued swings), and zero damage
/// dealt on escape cycles.
/// </summary>
public class WhiffOnEscapeScenario : ScenarioBase
{
    public override string Name => "whiff_on_escape";

    private uint _necroId, _attackerId, _dummyId;
    private float _elapsed;
    private bool _complete;
    private int _resultCode = -1;

    private const float TestDuration = 30f;
    private const float MeleeOffset = 1.3f;   // in melee range
    private const float EscapeOffset = 6.0f;  // beyond reach + whiff tolerance

    private bool _escapeCycle = true;   // alternate: first swing escapes, next stays
    private bool _swingQueued;          // saw PendingAttack for the current swing
    private bool _dummyEscaped;         // teleported the dummy for this swing
    private int _escapeSwings, _staySwings;
    private int _logCountAtSwingStart;
    private int _initialLogCount;
    private int _whiffs, _hitsOrMisses, _unaccounted, _escapeDamage;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Whiff-on-Escape Test (deterministic) ===");
        var units = sim.UnitsMut;

        int nIdx = sim.SpawnUnitByID("necromancer", new Vec2(10f, 10f));
        units[nIdx].Archetype = 0;
        units[nIdx].AI = AIBehavior.IdleAtPoint;
        _necroId = units[nIdx].Id;
        sim.SetNecromancerIndex(nIdx);

        int zIdx = sim.SpawnZombieMinion("ZombieFemaleDeer", new Vec2(12f, 10f));
        if (zIdx < 0) { Fail("spawn zombie deer failed"); return; }
        units[zIdx].Stats.MaxHP = 100000; units[zIdx].Stats.HP = 100000;
        units[zIdx].FacingAngle = 0f;
        _attackerId = units[zIdx].Id;

        int dIdx = sim.SpawnUnitByID("soldier", new Vec2(12f + MeleeOffset, 10f));
        if (dIdx < 0) { Fail("spawn dummy failed"); return; }
        units[dIdx].Faction = Faction.Human;
        units[dIdx].Archetype = 0;
        units[dIdx].AI = AIBehavior.IdleAtPoint;
        units[dIdx].Stats.MaxHP = 100000; units[dIdx].Stats.HP = 100000;
        _dummyId = units[dIdx].Id;

        _initialLogCount = sim.CombatLog.Entries.Count;
        ZoomOnLocation(14f, 10f, 28f);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        if (_complete) return;
        _elapsed += dt;

        int nIdx = FindById(sim.Units, _necroId);
        int aIdx = FindById(sim.Units, _attackerId);
        int dIdx = FindById(sim.Units, _dummyId);
        if (nIdx < 0 || aIdx < 0 || dIdx < 0) { Fail("unit vanished"); return; }

        // Pin the necromancer behind the attacker (horde-center anchor, no leash).
        Vec2 ap = sim.Units[aIdx].Position;
        sim.UnitsMut[nIdx].Position = ap - new Vec2(2f, 0f);
        sim.UnitsMut[nIdx].Velocity = Vec2.Zero;
        sim.UnitsMut[nIdx].PreferredVel = Vec2.Zero;

        // Keep both alive; dummy never fights back or moves on its own.
        sim.UnitsMut[aIdx].Stats.HP = 100000;
        sim.UnitsMut[dIdx].Stats.HP = 100000;
        sim.UnitsMut[dIdx].Velocity = Vec2.Zero;
        sim.UnitsMut[dIdx].PreferredVel = Vec2.Zero;
        sim.UnitsMut[dIdx].AttackCooldown = 10f; // dummy must not swing (its entries would pollute counting)

        bool pending = !sim.Units[aIdx].PendingAttack.IsNone;

        if (!_swingQueued)
        {
            // Waiting for a swing: hold the dummy in melee range.
            sim.UnitsMut[dIdx].Position = ap + new Vec2(MeleeOffset, 0f);
            if (pending)
            {
                _swingQueued = true;
                _dummyEscaped = false;
                _logCountAtSwingStart = sim.CombatLog.Entries.Count;
                if (_escapeCycle) _escapeSwings++; else _staySwings++;
                DebugLog.Log(ScenarioLog,
                    $"t={_elapsed:F2}s swing #{_escapeSwings + _staySwings} queued ({(_escapeCycle ? "ESCAPE" : "STAY")} cycle)");
            }
        }

        if (_swingQueued)
        {
            if (_escapeCycle)
            {
                // Teleport out the moment the swing is committed, and STAY out.
                sim.UnitsMut[dIdx].Position = ap + new Vec2(EscapeOffset, 0f);
                _dummyEscaped = true;
            }
            else
            {
                sim.UnitsMut[dIdx].Position = ap + new Vec2(MeleeOffset, 0f);
            }

            // Swing complete: PendingAttack resolved (or cleared) AND lockout done.
            if (!pending && sim.Units[aIdx].PostAttackTimer <= 0f)
            {
                var entries = sim.CombatLog.Entries;
                int gained = entries.Count - _logCountAtSwingStart;
                CombatLogOutcome? outcome = gained > 0 ? entries[entries.Count - 1].Outcome : null;
                int dmg = gained > 0 ? entries[entries.Count - 1].NetDamage : 0;
                DebugLog.Log(ScenarioLog,
                    $"t={_elapsed:F2}s swing resolved: logGained={gained} outcome={outcome?.ToString() ?? "NONE"} dmg={dmg} escaped={_dummyEscaped}");

                if (gained == 0) _unaccounted++;
                else if (_dummyEscaped)
                {
                    if (outcome == CombatLogOutcome.Whiff) _whiffs++;
                    else { _unaccounted++; if (dmg > 0) _escapeDamage++; }
                }
                else if (outcome == CombatLogOutcome.Hit || outcome == CombatLogOutcome.Miss)
                    _hitsOrMisses++;

                _swingQueued = false;
                _escapeCycle = !_escapeCycle;
            }
        }

        if (_elapsed >= TestDuration) Finish(sim);
    }

    private void Finish(Simulation sim)
    {
        int totalSwings = _escapeSwings + _staySwings;
        DebugLog.Log(ScenarioLog, "=== Validation ===");
        DebugLog.Log(ScenarioLog, $"Swings queued: {totalSwings} (escape={_escapeSwings} stay={_staySwings})");
        DebugLog.Log(ScenarioLog, $"Whiffs: {_whiffs} (want >=2)  Hit/Miss: {_hitsOrMisses} (want >=1)");
        DebugLog.Log(ScenarioLog, $"Unaccounted swings: {_unaccounted} (want 0)  Damage on escaped swings: {_escapeDamage} (want 0)");

        bool pass = _whiffs >= 2 && _hitsOrMisses >= 1 && _unaccounted == 0 && _escapeDamage == 0;
        DebugLog.Log(ScenarioLog, $"Overall: {(pass ? "PASS" : "FAIL")}");
        _resultCode = pass ? 0 : 1;
        _complete = true;
    }

    private void Fail(string reason)
    {
        DebugLog.Log(ScenarioLog, $"FAIL: {reason}");
        _resultCode = 1;
        _complete = true;
    }

    private static int FindById(UnitArrays units, uint id)
    {
        for (int i = 0; i < units.Count; i++)
            if (units[i].Id == id && units[i].Alive) return i;
        return -1;
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        if (_resultCode < 0) { DebugLog.Log(ScenarioLog, "FAIL: timed out before completing"); return 1; }
        return _resultCode;
    }
}
