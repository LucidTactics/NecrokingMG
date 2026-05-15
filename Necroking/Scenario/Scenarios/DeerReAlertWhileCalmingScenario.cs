using Necroking.AI;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Movement;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Reproduces a reported bug: when a deer is in the Calming routine
/// (transitioning back to idle after a flee/alert), a fresh threat that walks
/// into detection range fails to re-trigger the deer. The deer just stands
/// there finishing its calm timer.
///
/// Scenario phases:
///   t = 0.0 .. 2.0   Soldier near deer → Alert → Flee
///   t = 2.0 .. 10.0  Soldier teleported far away → deer enters Calming
///   t = 10.0 .. 20.0 Soldier teleported back close while deer is still
///                    in Calming → SHOULD re-Alert/Flee; bug = stays Calming.
///
/// Per-tick log captures Routine, AlertState, AlertTarget, distance,
/// SubroutineTimer. The OnComplete validator checks whether the deer's routine
/// changed away from Calming during phase 3.
/// </summary>
public class DeerReAlertWhileCalmingScenario : ScenarioBase
{
    public override string Name => "deer_realert_while_calming";

    private float _elapsed;
    private bool _complete;
    private const float TotalDuration = 30f;

    private uint _deerID;
    private uint _soldierID;

    private Vec2 _deerStartPos = new(32f, 32f);
    private Vec2 _soldierStartPos = new(37f, 32f);  // 5u — well within detection range 12
    private Vec2 _soldierFarPos   = new(80f, 32f);  // far away
    private Vec2 _soldierAttackPos = new(35f, 32f); // very close — used in phase 2

    // Phase 0 = initial close soldier, waiting for deer to alert/flee.
    // Phase 1 = soldier teleported far, waiting for deer to enter Calming.
    // Phase 2 = soldier teleported back close while deer is Calming, watching for re-alert.
    private int _phase = -1;

    private byte _lastRoutine = 255;
    private byte _lastAlertState = 255;
    private float _logTimer;

    // Tracking flags for OnComplete validation.
    private bool _everReachedCalming;
    private float _phase2StartTime = -1f;
    private bool _routineChangedDuringPhase2;
    private byte _routineSeenAtPhase2Start = 255;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Deer Re-Alert While Calming ===");
        DebugLog.Log(ScenarioLog, "Repros the bug: deer in Calming routine doesn't react to new threats.");

        int deerIdx = sim.SpawnUnitByID("FemaleDeer", _deerStartPos);
        if (deerIdx < 0)
        {
            DebugLog.Log(ScenarioLog, "ERROR: failed to spawn FemaleDeer");
            _complete = true;
            return;
        }
        // SpawnUnitByID doesn't wire archetype / awareness fields the way
        // Game1.SpawnUnit does. Apply the missing DeerHerd plumbing manually
        // so the scenario actually exercises the deer AI.
        var deerDef = sim.GameData?.Units.Get("FemaleDeer");
        if (deerDef != null)
        {
            sim.UnitsMut[deerIdx].Archetype = ArchetypeRegistry.DeerHerd;
            sim.UnitsMut[deerIdx].DetectionRange = deerDef.DetectionRange;
            sim.UnitsMut[deerIdx].DetectionBreakRange = deerDef.DetectionBreakRange;
            sim.UnitsMut[deerIdx].AlertDuration = deerDef.AlertDuration;
            sim.UnitsMut[deerIdx].AlertEscalateRange = deerDef.AlertEscalateRange;
            sim.UnitsMut[deerIdx].GroupAlertRadius = deerDef.GroupAlertRadius;
            sim.UnitsMut[deerIdx].SpawnPosition = _deerStartPos;
            var handler = ArchetypeRegistry.Get(ArchetypeRegistry.DeerHerd);
            if (handler != null)
            {
                var ctx = new AIContext
                {
                    UnitIndex = deerIdx, Units = sim.UnitsMut, Dt = 0, FrameNumber = 0,
                    GameData = sim.GameData, Pathfinder = sim.Pathfinder,
                    Horde = sim.Horde, GameTime = 0, DayTime = 0.25f, IsNight = false,
                };
                handler.OnSpawn(ref ctx);
            }
        }
        _deerID = sim.Units[deerIdx].Id;
        DebugLog.Log(ScenarioLog,
            $"Spawned FemaleDeer at ({_deerStartPos.X:F1},{_deerStartPos.Y:F1}) " +
            $"id={_deerID} archetype={sim.Units[deerIdx].Archetype} " +
            $"detRange={sim.Units[deerIdx].DetectionRange:F1} " +
            $"detBreak={sim.Units[deerIdx].DetectionBreakRange:F1}");

        int soldierIdx = sim.UnitsMut.AddUnit(_soldierStartPos, UnitType.Soldier);
        sim.UnitsMut[soldierIdx].AI = AIBehavior.AttackClosest;
        sim.UnitsMut[soldierIdx].Faction = Faction.Human;
        _soldierID = sim.Units[soldierIdx].Id;
        DebugLog.Log(ScenarioLog,
            $"Spawned Soldier at ({_soldierStartPos.X:F1},{_soldierStartPos.Y:F1}) id={_soldierID}");

        _phase = 0;
        ZoomOnLocation(40f, 32f, 12f);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;

        // Phase transitions — teleport the soldier to drive the test.
        int soldierIdx = FindByID(sim.Units, _soldierID);
        if (soldierIdx < 0) return;
        int deerIdx = FindByID(sim.Units, _deerID);
        if (deerIdx < 0) { _complete = true; return; }

        byte routine = sim.Units[deerIdx].Routine;

        // Pin the soldier each tick to its current phase position — otherwise
        // its AttackClosest AI keeps pathing toward the deer and the controlled
        // teleports get smeared out.
        Vec2 pinTarget = _phase switch
        {
            0 => _soldierStartPos,
            1 => _soldierFarPos,
            2 => _soldierAttackPos,
            _ => _soldierStartPos,
        };
        TeleportSoldier(sim, soldierIdx, pinTarget);

        // Phase transitions are state-driven, not time-driven, so the test
        // works regardless of detection-range tuning and flee duration.
        if (_phase == 0 && (routine == 2 /*Alert*/ || routine == 3 /*Fleeing*/))
        {
            _phase = 1;
            TeleportSoldier(sim, soldierIdx, _soldierFarPos);
            DebugLog.Log(ScenarioLog,
                $"[{_elapsed:F2}s] PHASE 1: deer reacted ({GetRoutineName(routine)}). " +
                $"Soldier teleported FAR → ({_soldierFarPos.X:F1},{_soldierFarPos.Y:F1}) — waiting for deer to enter Calming.");
        }
        else if (_phase == 1 && routine == 4 /*Calming*/)
        {
            _phase = 2;
            _phase2StartTime = _elapsed;
            _routineSeenAtPhase2Start = routine;
            TeleportSoldier(sim, soldierIdx, _soldierAttackPos);
            DebugLog.Log(ScenarioLog,
                $"[{_elapsed:F2}s] PHASE 2: deer entered Calming. " +
                $"Soldier teleported BACK close → ({_soldierAttackPos.X:F1},{_soldierAttackPos.Y:F1}) — " +
                $"deer SHOULD re-Alert/Flee. Bug = stays Calming.");
            DebugLog.Log(ScenarioLog,
                $"  Deer pos=({sim.Units[deerIdx].Position.X:F1},{sim.Units[deerIdx].Position.Y:F1}) " +
                $"distToSoldier={(_soldierAttackPos - sim.Units[deerIdx].Position).Length():F1} " +
                $"alertState={GetAlertName(sim.Units[deerIdx].AlertState)}");
        }

        // Tick-level state-transition logging.
        byte curRoutine = sim.Units[deerIdx].Routine;
        byte curAlert = sim.Units[deerIdx].AlertState;

        if (curRoutine != _lastRoutine)
        {
            DebugLog.Log(ScenarioLog,
                $"[{_elapsed:F2}s] DEER ROUTINE: {GetRoutineName(_lastRoutine)} → {GetRoutineName(curRoutine)} " +
                $"alert={GetAlertName(curAlert)} pos=({sim.Units[deerIdx].Position.X:F1},{sim.Units[deerIdx].Position.Y:F1}) " +
                $"subTimer={sim.Units[deerIdx].SubroutineTimer:F2}");
            _lastRoutine = curRoutine;

            if (curRoutine == 4 /*RoutineCalming*/)
                _everReachedCalming = true;
            // Any routine change away from Calming during phase 2 is evidence
            // the deer reacted to the re-introduced threat.
            if (_phase == 2 && _routineSeenAtPhase2Start == 4 && curRoutine != 4)
                _routineChangedDuringPhase2 = true;
        }

        if (curAlert != _lastAlertState)
        {
            DebugLog.Log(ScenarioLog,
                $"[{_elapsed:F2}s] DEER ALERT: {GetAlertName(_lastAlertState)} → {GetAlertName(curAlert)} " +
                $"routine={GetRoutineName(curRoutine)} alertTarget={sim.Units[deerIdx].AlertTarget}");
            _lastAlertState = curAlert;
        }

        // Periodic position/state snapshot.
        _logTimer -= dt;
        if (_logTimer <= 0f)
        {
            _logTimer = 1.0f;
            float distToSoldier = (sim.Units[soldierIdx].Position - sim.Units[deerIdx].Position).Length();
            DebugLog.Log(ScenarioLog,
                $"  t={_elapsed:F1}s phase={_phase} deer routine={GetRoutineName(curRoutine)} " +
                $"alert={GetAlertName(curAlert)} subTimer={sim.Units[deerIdx].SubroutineTimer:F2} " +
                $"deerPos=({sim.Units[deerIdx].Position.X:F1},{sim.Units[deerIdx].Position.Y:F1}) " +
                $"soldierPos=({sim.Units[soldierIdx].Position.X:F1},{sim.Units[soldierIdx].Position.Y:F1}) " +
                $"dist={distToSoldier:F1}");
        }

        if (_elapsed >= TotalDuration) _complete = true;
    }

    /// <summary>Force the soldier to a position and stop any in-flight pathing
    /// so it doesn't immediately rush back toward the deer.</summary>
    private void TeleportSoldier(Simulation sim, int idx, Vec2 pos)
    {
        sim.UnitsMut[idx].Position = pos;
        sim.UnitsMut[idx].PreferredVel = Vec2.Zero;
        sim.UnitsMut[idx].MoveTarget = pos;
        sim.UnitsMut[idx].Target = CombatTarget.None;
        sim.UnitsMut[idx].PendingAttack = CombatTarget.None;
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "=== Validation ===");
        DebugLog.Log(ScenarioLog, $"Deer ever reached Calming: {_everReachedCalming}");
        DebugLog.Log(ScenarioLog, $"Deer was in Calming at phase 2 start: {_routineSeenAtPhase2Start == 4} (routine={GetRoutineName(_routineSeenAtPhase2Start)})");
        DebugLog.Log(ScenarioLog, $"Deer routine changed during phase 2: {_routineChangedDuringPhase2}");

        int deerIdx = FindByID(sim.Units, _deerID);
        if (deerIdx >= 0)
        {
            DebugLog.Log(ScenarioLog,
                $"Deer final state: routine={GetRoutineName(sim.Units[deerIdx].Routine)} " +
                $"alertState={GetAlertName(sim.Units[deerIdx].AlertState)} " +
                $"pos=({sim.Units[deerIdx].Position.X:F1},{sim.Units[deerIdx].Position.Y:F1})");
        }

        // Test PASSES if the deer reacted to the second threat (left Calming).
        // Test FAILS (i.e. bug confirmed) if deer was in Calming when soldier
        // returned and stayed there.
        bool buggyBehavior = _everReachedCalming
            && _routineSeenAtPhase2Start == 4
            && !_routineChangedDuringPhase2;
        if (buggyBehavior)
        {
            DebugLog.Log(ScenarioLog, "BUG REPRODUCED: deer in Calming did NOT react to nearby threat.");
            return 1;
        }
        DebugLog.Log(ScenarioLog, "Deer reacted correctly (or did not enter Calming) — no bug.");
        return 0;
    }

    private static int FindByID(UnitArrays units, uint id)
    {
        for (int i = 0; i < units.Count; i++)
            if (units[i].Id == id) return i;
        return -1;
    }

    private static string GetRoutineName(byte r) => r switch
    {
        0 => "IdleRoaming",
        1 => "Sleeping",
        2 => "Alert",
        3 => "Fleeing",
        4 => "Calming",
        5 => "FightBack",
        6 => "Feeding",
        255 => "(none)",
        _ => $"R{r}",
    };

    private static string GetAlertName(byte a) => a switch
    {
        0 => "Unaware",
        1 => "Alert",
        2 => "Aggressive",
        255 => "(none)",
        _ => $"A{a}",
    };
}
