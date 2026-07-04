using System.Collections.Generic;
using Necroking.AI;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Movement;
using Necroking.Render;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Regression for the "sliding deer" bug class (anim/movement interaction audit R1/R2):
/// a fleeing (moving) unit must NEVER carry a movement-locked override anim (Dodge,
/// BlockReact, attack states…) — those play while planted or not at all.
///
/// Exercises all three layers on a fleeing wild deer under sustained abuse:
///   1. Hits (DamageSystem.Apply → flinch path) — gated by Fleeing + reaction cooldown.
///   2. Whiffs (DamageSystem.ApplyDodgeAnim — the melee-miss dodge path) — same gates.
///   3. Raw AnimResolver.SetOverride(Reaction(Dodge)) bypassing the helper gates —
///      must be rejected by the structural movement gate in SetOverride itself.
///
/// Invariant, every tick: if the deer is clearly moving and no plant mechanism is
/// active, OverrideAnim must not hold a movement-locked state.
///
/// Sanity (so a dead reaction lane can't silently "pass"): before the abuse starts,
/// one dodge request on the STANDING deer must be accepted.
/// </summary>
public class DeerFleeNoSlideScenario : ScenarioBase
{
    public override string Name => "deer_flee_no_slide";

    private uint _necroId, _deerId;
    private float _elapsed;
    private bool _complete;
    private bool _fail;
    private readonly List<string> _failReasons = new();

    private const float MovingSpeed = 0.8f;   // clearly moving, well above the gate's 0.3
    private const float AbuseStart = 4.5f;    // hits/whiffs begin here
    private const float RunSeconds = 14f;

    private bool _sanityDodgeTried;
    private bool _sanityDodgeAccepted;
    private bool _sanitySkipped;
    private float _abuseTimer;
    private int _slideFrames;
    private string _firstSlide = "";
    private float _fleeStreak, _maxFleeStreak;
    private Vec2 _deerStart;
    private float _deerMaxDist;

    private readonly List<DamageEvent> _scratchEvents = new();

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Deer Flee No-Slide (movement gate + reaction lane) ===");

        var units = sim.UnitsMut;

        int nIdx = sim.SpawnUnitByID("necromancer", new Vec2(10f, 10f));
        units[nIdx].Archetype = 0;
        units[nIdx].AI = AIBehavior.IdleAtPoint;
        _necroId = units[nIdx].Id;
        sim.SetNecromancerIndex(nIdx);

        // Deer far outside detection range so it stands still for the sanity check;
        // the flee is then triggered purely by damage attribution. Same hand-wiring
        // as combat_anim_review (SpawnUnitByID doesn't apply archetype fields).
        int dIdx = sim.SpawnUnitByID("FemaleDeer", new Vec2(45f, 10f));
        if (dIdx < 0) { Fail("could not spawn FemaleDeer"); _complete = true; return; }
        units[dIdx].Archetype = ArchetypeRegistry.DeerHerd;
        var deerDef = sim.GameData.Units.Get("FemaleDeer");
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

        ZoomOnLocation(35f, 10f, 30f);
        DebugLog.Log(ScenarioLog, "necro@(10,10) deer@(45,10) — sanity dodge, then abuse from t=4.5s");
    }

    public override void OnTick(Simulation sim, float dt)
    {
        if (_complete) return;
        _elapsed += dt;

        int nIdx = FindById(sim.Units, _necroId);
        int dIdx = FindById(sim.Units, _deerId);
        if (nIdx < 0 || dIdx < 0) { _complete = true; return; }

        sim.UnitsMut[nIdx].PreferredVel = Vec2.Zero;
        sim.UnitsMut[nIdx].Velocity = Vec2.Zero;
        sim.UnitsMut[nIdx].Position = new Vec2(10f, 10f);
        TopUpHp(sim, dIdx);

        var deer = sim.Units[dIdx];

        // --- Sanity: one dodge on the standing deer must be ACCEPTED ---
        if (!_sanityDodgeTried && _elapsed > 1f && _elapsed < AbuseStart - 0.5f)
        {
            if (deer.Velocity.LengthSq() < 0.01f)
            {
                _sanityDodgeTried = true;
                DamageSystem.ApplyDodgeAnim(sim.UnitsMut, dIdx);
                _sanityDodgeAccepted = sim.Units[dIdx].OverrideAnim.IsActive
                    && sim.Units[dIdx].OverrideAnim.State == AnimState.Dodge;
                DebugLog.Log(ScenarioLog,
                    $"t={_elapsed:F1}s sanity dodge on standing deer accepted={_sanityDodgeAccepted}");
            }
            else if (_elapsed > AbuseStart - 0.6f)
            {
                _sanitySkipped = true; // deer never stood still — don't fail, just note
                _sanityDodgeTried = true;
            }
        }

        // --- Abuse phase: hit + whiff + raw-bypass every 0.25s while it flees ---
        if (_elapsed >= AbuseStart)
        {
            _abuseTimer -= dt;
            if (_abuseTimer <= 0f)
            {
                _abuseTimer = 0.25f;
                _scratchEvents.Clear();
                DamageSystem.Apply(sim.UnitsMut, dIdx, 5, DamageType.Physical,
                    DamageFlags.ArmorNegating, _scratchEvents, nIdx);      // hit → flinch path
                DamageSystem.ApplyDodgeAnim(sim.UnitsMut, dIdx);           // whiff → dodge path
                AnimResolver.SetOverride(sim.UnitsMut[dIdx],
                    AnimRequest.Reaction(AnimState.Dodge));                // raw bypass → R1 gate
            }
        }

        if (deer.Fleeing) { _fleeStreak += dt; if (_fleeStreak > _maxFleeStreak) _maxFleeStreak = _fleeStreak; }
        else _fleeStreak = 0f;
        float dist = (deer.Position - _deerStart).Length();
        if (dist > _deerMaxDist) _deerMaxDist = dist;

        // --- THE invariant: moving + unplanted ⇒ no movement-locked override ---
        bool planted = !deer.PendingAttack.IsNone || deer.PostAttackTimer > 0f
            || deer.InCombat || deer.Incap.Active || deer.JumpPhase != 0 || deer.DodgeTimer > 0f;
        if (deer.Velocity.Length() > MovingSpeed && !planted
            && deer.OverrideAnim.IsActive
            && AnimController.IsMovementLocked(deer.OverrideAnim.State))
        {
            _slideFrames++;
            if (_firstSlide.Length == 0)
                _firstSlide = $"t={_elapsed:F2}s state={deer.OverrideAnim.State} vel={deer.Velocity.Length():F2}";
        }

        if (_elapsed % 1f < dt)
        {
            DebugLog.Log(ScenarioLog,
                $"t={_elapsed:F1}s deer[fleeing={deer.Fleeing} vel={deer.Velocity.Length():F1} " +
                $"ov={(deer.OverrideAnim.IsActive ? deer.OverrideAnim.State.ToString() : "-")} " +
                $"reactCd={deer.ReactionCooldownTimer:F2}] slideFrames={_slideFrames}");
        }

        if (_elapsed > RunSeconds)
        {
            EndAndValidate();
            _complete = true;
        }
    }

    private void EndAndValidate()
    {
        if (_sanitySkipped)
            DebugLog.Log(ScenarioLog, "NOTE: sanity dodge skipped (deer never stood still)");
        else if (_sanityDodgeTried && !_sanityDodgeAccepted)
            Fail("dodge on a STANDING deer was rejected — reaction lane is dead, invariant vacuous");
        if (!_sanityDodgeTried)
            Fail("sanity dodge never attempted");

        if (_maxFleeStreak < 2f)
            Fail($"deer never sustained a flee (max streak {_maxFleeStreak:F2}s) — moving invariant untested");
        if (_deerMaxDist < 8f)
            Fail($"deer only covered {_deerMaxDist:F1}u — flee didn't actually run");
        if (_slideFrames > 0)
            Fail($"{_slideFrames} frame(s) of movement-locked override while moving+unplanted (first: {_firstSlide})");

        DebugLog.Log(ScenarioLog,
            $"SUMMARY sanityAccepted={_sanityDodgeAccepted}{(_sanitySkipped ? "(skipped)" : "")} " +
            $"maxFleeStreak={_maxFleeStreak:F2} maxDist={_deerMaxDist:F1} slideFrames={_slideFrames}");
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
        DebugLog.Log(ScenarioLog, "PASS: fleeing deer never carries a movement-locked override while moving");
        return 0;
    }

    private static int FindById(UnitArrays units, uint id)
    {
        for (int i = 0; i < units.Count; i++)
            if (units[i].Id == id) return i;
        return -1;
    }
}
