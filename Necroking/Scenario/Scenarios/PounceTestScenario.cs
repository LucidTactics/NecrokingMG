using System;
using Necroking.AI;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Movement;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Phase 1 pounce test: a wolf approaches a soldier and pounces on first engage.
/// Verifies the full jump state machine (TakeoffApproach → Airborne → Landing → Recovery)
/// and that the wolf lands in melee range.
/// </summary>
public class PounceTestScenario : ScenarioBase
{
    public override string Name => "pounce_test";

    private float _elapsed;
    private bool _complete;
    private const float MaxDuration = 12f;

    private uint _wolfId;
    private uint _soldierId;

    // Phase history: max phase reached + time of each transition
    private byte _maxPhaseReached;
    private byte _lastPhase;
    private float _phase1StartTime = -1f;  // TakeoffApproach
    private float _phase2StartTime = -1f;  // Airborne (liftoff moment)
    private float _phase3StartTime = -1f;  // Landing anim
    private float _phase4StartTime = -1f;  // Recovery
    private float _phase0ResumeTime = -1f; // back to None

    // Peak Z reached during flight (quick sanity check on the arc)
    private float _peakZ;

    // Record landing position for validation
    private Vec2 _landingPos;
    private bool _landedRecorded;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Pounce Test ===");
        DebugLog.Log(ScenarioLog, "One wolf approaches one soldier. On first engage, wolf should pounce.");
        DebugLog.Log(ScenarioLog, "Expected phase sequence: 0 → 1 (TakeoffApproach) → 2 (Airborne) → 3 (Landing) → 4 (Recovery) → 0");

        var units = sim.UnitsMut;

        // Spawn a real Wolf unit (UnitDef "Wolf") so it has the JumpTakeoff/JumpLoop/JumpLand anims
        int wolfIdx = sim.SpawnUnitByID("Wolf", new Vec2(10f, 20f));
        units[wolfIdx].Archetype = ArchetypeRegistry.WolfPack;
        units[wolfIdx].Faction = Faction.Animal;
        // SpawnUnitByID doesn't populate awareness config (Game1 spawn pipeline does that) —
        // set it directly from the UnitDef so awareness/alert escalation works.
        var wolfDef = sim.GameData?.Units.Get("Wolf");
        if (wolfDef != null)
        {
            units[wolfIdx].DetectionRange = wolfDef.DetectionRange;
            units[wolfIdx].DetectionBreakRange = wolfDef.DetectionBreakRange;
            units[wolfIdx].AlertDuration = wolfDef.AlertDuration;
            units[wolfIdx].AlertEscalateRange = wolfDef.AlertEscalateRange;
            units[wolfIdx].GroupAlertRadius = wolfDef.GroupAlertRadius;
        }
        _wolfId = units[wolfIdx].Id;
        DebugLog.Log(ScenarioLog, $"Wolf: id={_wolfId} pos=(10,20) archetype=WolfPack faction=Animal detectionRange={units[wolfIdx].DetectionRange:F1}");

        // Spawn a soldier 8 units away (plenty of room to enter pounce window)
        int soldIdx = units.AddUnit(new Vec2(18f, 20f), UnitType.Soldier);
        units[soldIdx].AI = AIBehavior.IdleAtPoint; // stationary target
        units[soldIdx].Faction = Faction.Human;
        units[soldIdx].Stats.MaxHP = 500;  // don't die from the landing-hit so we can observe recovery
        units[soldIdx].Stats.HP = 500;
        _soldierId = units[soldIdx].Id;
        DebugLog.Log(ScenarioLog, $"Soldier: id={_soldierId} pos=(18,20) AI=IdleAtPoint faction=Human");

        DebugLog.Log(ScenarioLog, $"Initial distance: 8 units. Pounce window is [1..3], so wolf should pounce once it closes to ~3 units.");

        ZoomOnLocation(14f, 20f, 40f);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;
        var units = sim.Units;

        int wIdx = FindByID(units, _wolfId);
        int sIdx = FindByID(units, _soldierId);
        if (wIdx < 0 || sIdx < 0)
        {
            DebugLog.Log(ScenarioLog, $"t={_elapsed:F2}s: wolf or soldier removed, ending scenario");
            _complete = true;
            return;
        }

        var wolf = units[wIdx];
        var soldier = units[sIdx];

        // Track jump phase transitions
        byte phase = wolf.JumpPhase;
        if (phase != _lastPhase)
        {
            DebugLog.Log(ScenarioLog,
                $"t={_elapsed:F2}s: JumpPhase {PhaseName(_lastPhase)}({_lastPhase}) → {PhaseName(phase)}({phase}) " +
                $"pos=({wolf.Position.X:F2},{wolf.Position.Y:F2}) Z={wolf.Z:F2} " +
                $"distToTarget={(soldier.Position - wolf.Position).Length():F2}");

            switch (phase)
            {
                case 1: _phase1StartTime = _elapsed; break;
                case 2: _phase2StartTime = _elapsed; break;
                case 3: _phase3StartTime = _elapsed; break;
                case 4: _phase4StartTime = _elapsed; break;
                case 0 when _lastPhase != 0: _phase0ResumeTime = _elapsed; break;
            }
            _lastPhase = phase;
        }
        if (phase > _maxPhaseReached) _maxPhaseReached = phase;

        // Track peak Z during flight
        if (wolf.Z > _peakZ) _peakZ = wolf.Z;

        // Record landing position the first time phase goes to 4 (Recovery = touchdown fired)
        if (phase == 4 && !_landedRecorded)
        {
            _landingPos = wolf.Position;
            _landedRecorded = true;
            float distAtLanding = (soldier.Position - wolf.Position).Length();
            DebugLog.Log(ScenarioLog, $"t={_elapsed:F2}s: touchdown at ({wolf.Position.X:F2},{wolf.Position.Y:F2}), dist to soldier={distAtLanding:F2}");
        }

        // Complete once wolf has fully returned to phase 0 (post-pounce) OR timeout
        if (_maxPhaseReached >= 4 && phase == 0 && _phase0ResumeTime > 0f)
        {
            DebugLog.Log(ScenarioLog, $"t={_elapsed:F2}s: pounce cycle complete, ending scenario");
            _complete = true;
        }

        if (_elapsed >= MaxDuration)
        {
            DebugLog.Log(ScenarioLog, $"t={_elapsed:F2}s: max duration reached, ending scenario");
            _complete = true;
        }
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "=== Pounce Validation ===");
        DebugLog.Log(ScenarioLog, $"Max JumpPhase reached: {PhaseName(_maxPhaseReached)}({_maxPhaseReached})");
        DebugLog.Log(ScenarioLog, $"Phase timings: takeoff-approach={_phase1StartTime:F2}s, airborne={_phase2StartTime:F2}s, landing={_phase3StartTime:F2}s, recovery={_phase4StartTime:F2}s, back-to-idle={_phase0ResumeTime:F2}s");
        DebugLog.Log(ScenarioLog, $"Peak Z during flight: {_peakZ:F2} (expected ~2.0)");

        var units = sim.Units;
        int wIdx = FindByID(units, _wolfId);
        int sIdx = FindByID(units, _soldierId);
        float finalDist = -1f;
        if (wIdx >= 0 && sIdx >= 0)
        {
            finalDist = (units[sIdx].Position - units[wIdx].Position).Length();
            DebugLog.Log(ScenarioLog, $"Final wolf pos=({units[wIdx].Position.X:F2},{units[wIdx].Position.Y:F2}) soldier pos=({units[sIdx].Position.X:F2},{units[sIdx].Position.Y:F2}) dist={finalDist:F2}");
        }

        if (_landedRecorded)
        {
            DebugLog.Log(ScenarioLog, $"Landing pos=({_landingPos.X:F2},{_landingPos.Y:F2})");
        }

        // Checks
        bool reachedAirborne = _maxPhaseReached >= 2;
        bool reachedLanding = _maxPhaseReached >= 3;
        bool reachedRecovery = _maxPhaseReached >= 4;
        bool arcReasonable = _peakZ > 1.0f && _peakZ < 5.0f;
        bool landedInMeleeRange = _landedRecorded
            && (units[sIdx].Position - _landingPos).Length() < 2.0f;

        DebugLog.Log(ScenarioLog, $"Check - reached Airborne: {reachedAirborne}");
        DebugLog.Log(ScenarioLog, $"Check - reached Landing:  {reachedLanding}");
        DebugLog.Log(ScenarioLog, $"Check - reached Recovery: {reachedRecovery}");
        DebugLog.Log(ScenarioLog, $"Check - arc peak in range (1.0-5.0): {arcReasonable} (peakZ={_peakZ:F2})");
        DebugLog.Log(ScenarioLog, $"Check - landed in melee range (<2.0 from soldier): {landedInMeleeRange}");

        bool pass = reachedAirborne && reachedLanding && reachedRecovery && arcReasonable && landedInMeleeRange;
        DebugLog.Log(ScenarioLog, $"Overall: {(pass ? "PASS" : "FAIL")}");
        return pass ? 0 : 1;
    }

    private static string PhaseName(byte phase) => phase switch
    {
        0 => "None",
        1 => "TakeoffApproach",
        2 => "Airborne",
        3 => "Landing",
        4 => "Recovery",
        _ => $"Unknown({phase})"
    };

    private static int FindByID(UnitArrays units, uint id)
    {
        for (int i = 0; i < units.Count; i++)
            if (units[i].Id == id) return i;
        return -1;
    }
}
