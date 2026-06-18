using System.Collections.Generic;
using Necroking.AI;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Movement;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Verifies two combat-animation fixes from the combat/anim review:
///
///   1. FLEE FLINCH SUPPRESSION — a unit that is actively fleeing must NOT play the
///      hit-react (BlockReact) flinch when struck; it keeps running. We spook a wild
///      deer, then repeatedly damage it while it flees and assert its HitReactTimer
///      never goes positive while Fleeing (and that it keeps moving).
///
///   2. PLANT WHILE ATTACKING — a melee unit engaged in combat is hard-locked
///      (Velocity == 0) for the whole attack cycle, so it doesn't slide while the
///      swing/pre-roll animation plays. Two fighters are pinned in melee and we assert
///      the attacker's velocity stays ~0 once combat is sustained.
///
/// Both test subjects have their HP topped up each tick so the fight/flee runs the
/// full duration.
/// </summary>
public class CombatAnimReviewScenario : ScenarioBase
{
    public override string Name => "combat_anim_review";

    private uint _necroId, _deerId, _humanId, _undeadId;
    private float _elapsed;
    private bool _complete;
    private bool _fail;
    private readonly List<string> _failReasons = new();

    // Flee-flinch tracking. We only assert against SUSTAINED fleeing (streak > grace)
    // so the initial spook flinch (a grazing deer hit cold should flinch, then bolt)
    // and accel-from-rest at flee-start don't count as violations — the bug is an
    // ONGOING flee getting interrupted by a flinch.
    private const float FleeGrace = 0.6f;
    private bool _deerFlinchedWhileSustainedFlee;
    private float _deerFleeStreak;
    private float _maxFleeStreak;
    private float _deerMaxDistFromStart;
    private Vec2 _deerStart;
    private float _spookTimer;

    // Plant tracking
    private float _humanInCombatTime;
    private float _maxVelWhileSustainedCombat;
    private bool _humanEverInCombat;

    private readonly List<DamageEvent> _scratchEvents = new();

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Combat Anim Review (flee flinch + plant) ===");

        var units = sim.UnitsMut;

        int nIdx = sim.SpawnUnitByID("necromancer", new Vec2(10f, 10f));
        units[nIdx].Archetype = 0;
        units[nIdx].AI = AIBehavior.IdleAtPoint;
        _necroId = units[nIdx].Id;
        sim.SetNecromancerIndex(nIdx);

        // Wild deer just inside the necromancer's threat range so it alerts/flees.
        // SpawnUnitByID only applies the legacy AI enum, so wire the DeerHerd archetype
        // + awareness fields by hand (Game1's real spawn pipeline does this for map
        // deer). This tests the actual wild-deer flee path, not the legacy FleeWhenHit.
        int dIdx = sim.SpawnUnitByID("FemaleDeer", new Vec2(17f, 10f));
        if (dIdx < 0) { Fail("could not spawn FemaleDeer"); _complete = true; return; }
        units[dIdx].Archetype = ArchetypeRegistry.DeerHerd;
        var deerDef = sim.GameData?.Units.Get("FemaleDeer");
        if (deerDef != null)
        {
            units[dIdx].DetectionRange = deerDef.DetectionRange;
            units[dIdx].DetectionBreakRange = deerDef.DetectionBreakRange;
            units[dIdx].AlertDuration = deerDef.AlertDuration;
            units[dIdx].AlertEscalateRange = deerDef.AlertEscalateRange;
            units[dIdx].GroupAlertRadius = deerDef.GroupAlertRadius;
        }
        _deerId = units[dIdx].Id;
        _deerStart = units[dIdx].Position;
        DebugLog.Log(ScenarioLog, $"deer archetype={units[dIdx].Archetype} (DeerHerd={ArchetypeRegistry.DeerHerd}) ai={units[dIdx].AI} detRange={units[dIdx].DetectionRange}");

        // Two fighters far away, adjacent so they immediately melee.
        int hIdx = sim.SpawnUnitByID("soldier", new Vec2(40f, 10f));
        int uIdx = sim.SpawnUnitByID("skeleton", new Vec2(40.9f, 10f));
        if (hIdx < 0 || uIdx < 0) { Fail("could not spawn fighters"); _complete = true; return; }
        _humanId = units[hIdx].Id;
        _undeadId = units[uIdx].Id;

        ZoomOnLocation(25f, 10f, 24f);
        DebugLog.Log(ScenarioLog, "necro@(10,10) deer@(17,10) fighters@(40,10)/(40.9,10)");
    }

    public override void OnTick(Simulation sim, float dt)
    {
        if (_complete) return;
        _elapsed += dt;

        int nIdx = FindById(sim.Units, _necroId);
        int dIdx = FindById(sim.Units, _deerId);
        int hIdx = FindById(sim.Units, _humanId);
        int uIdx = FindById(sim.Units, _undeadId);
        if (nIdx < 0 || dIdx < 0 || hIdx < 0 || uIdx < 0) { _complete = true; return; }

        // Pin necromancer in place (it's the deer's threat anchor).
        sim.UnitsMut[nIdx].PreferredVel = Vec2.Zero;
        sim.UnitsMut[nIdx].Velocity = Vec2.Zero;
        sim.UnitsMut[nIdx].Position = new Vec2(10f, 10f);

        // Keep all three test subjects alive for the full run.
        TopUpHp(sim, dIdx);
        TopUpHp(sim, hIdx);
        TopUpHp(sim, uIdx);

        // --- Flee flinch test ---
        var deer = sim.Units[dIdx];
        bool deerFleeing = deer.Fleeing;
        if (deerFleeing) { _deerFleeStreak += dt; if (_deerFleeStreak > _maxFleeStreak) _maxFleeStreak = _deerFleeStreak; }
        else _deerFleeStreak = 0f;

        // Spook + keep hitting the deer ~4×/sec, attributed to the necromancer (also
        // keeps re-triggering the flee as the deer tries to calm).
        _spookTimer -= dt;
        if (_spookTimer <= 0f)
        {
            _spookTimer = 0.25f;
            _scratchEvents.Clear();
            DamageSystem.Apply(sim.UnitsMut, dIdx, 5, DamageType.Physical,
                DamageFlags.ArmorNegating, _scratchEvents, nIdx);
        }

        // Invariant: once the deer has been fleeing continuously past the grace window,
        // a hit must NOT produce a flinch.
        if (deerFleeing && _deerFleeStreak > FleeGrace)
        {
            if (sim.Units[dIdx].HitReactTimer > 0f)
                _deerFlinchedWhileSustainedFlee = true;
        }
        // "Keeps running" proof: distance covered from spawn (robust to the natural
        // velocity dip when the deer reverses flee direction).
        float dist = (sim.Units[dIdx].Position - _deerStart).Length();
        if (dist > _deerMaxDistFromStart) _deerMaxDistFromStart = dist;

        // --- Plant test ---
        var human = sim.Units[hIdx];
        if (human.InCombat)
        {
            _humanEverInCombat = true;
            _humanInCombatTime += dt;
            // Ignore the entry transient; once combat is sustained the unit must be planted.
            if (_humanInCombatTime > 0.5f)
            {
                float v = human.Velocity.Length();
                if (v > _maxVelWhileSustainedCombat) _maxVelWhileSustainedCombat = v;
            }
        }
        else
        {
            _humanInCombatTime = 0f;
        }

        if (_elapsed % 1f < dt)
        {
            DebugLog.Log(ScenarioLog,
                $"t={_elapsed:F1}s deer[fleeing={deerFleeing} hitReactT={sim.Units[dIdx].HitReactTimer:F2} " +
                $"vel={sim.Units[dIdx].Velocity.Length():F1} routine={sim.Units[dIdx].Routine}] " +
                $"human[inCombat={human.InCombat} vel={human.Velocity.Length():F2} " +
                $"pend={(!human.PendingAttack.IsNone)} postAtk={human.PostAttackTimer:F2}]");
        }

        if (_elapsed > 14f)
        {
            EndAndValidate();
            _complete = true;
        }
    }

    private void EndAndValidate()
    {
        if (_maxFleeStreak <= FleeGrace)
            Fail($"deer never fled continuously past {FleeGrace:F1}s (max streak {_maxFleeStreak:F2}) — flee invariant untested");
        if (_deerFlinchedWhileSustainedFlee)
            Fail("deer played the hit-react flinch while sustained-fleeing (bug 1 NOT fixed)");
        if (_deerMaxDistFromStart < 10f)
            Fail($"deer only covered {_deerMaxDistFromStart:F1}u while fleeing+hit (should keep running away)");

        if (!_humanEverInCombat)
            Fail("fighters never engaged — plant invariant untested");
        if (_humanEverInCombat && _maxVelWhileSustainedCombat > 0.5f)
            Fail($"fighter moved at {_maxVelWhileSustainedCombat:F2} while sustained InCombat (bug 2 NOT fixed)");

        DebugLog.Log(ScenarioLog,
            $"SUMMARY maxFleeStreak={_maxFleeStreak:F2} flinchedWhileSustainedFlee={_deerFlinchedWhileSustainedFlee} " +
            $"deerMaxDist={_deerMaxDistFromStart:F1} | humanEverInCombat={_humanEverInCombat} " +
            $"maxVelSustainedCombat={_maxVelWhileSustainedCombat:F2}");
    }

    private static void TopUpHp(Simulation sim, int idx)
    {
        sim.UnitsMut[idx].Stats.MaxHP = 100000;
        sim.UnitsMut[idx].Stats.HP = 100000;
    }

    private void Fail(string reason) { _fail = true; _failReasons.Add(reason); }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        if (_fail)
        {
            foreach (var r in _failReasons) DebugLog.Log(ScenarioLog, "FAIL: " + r);
            return 1;
        }
        DebugLog.Log(ScenarioLog, "PASS: flee keeps running (no flinch) + melee units plant while attacking");
        return 0;
    }

    private static int FindById(UnitArrays units, uint id)
    {
        for (int i = 0; i < units.Count; i++)
            if (units[i].Id == id) return i;
        return -1;
    }
}
