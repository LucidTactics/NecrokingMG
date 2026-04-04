using System;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Movement;

namespace Necroking.Scenario.Scenarios;

public class HordeFollowScenario : ScenarioBase
{
    public override string Name => "horde_follow";

    private float _elapsed;
    private bool _complete;

    // Phases: 0=movement, 1=stop+verify, 2=enemies+engagement, 3=done
    private int _phase;
    private float _phaseTimer;
    private float _logTimer;

    private const float MovementDuration = 5f;
    private const float StopVerifyDelay = 1.5f;
    private const float EngagementDuration = 10f;

    private uint _necroId;
    private readonly uint[] _skeletonIds = new uint[8];
    private int _skeletonCount;

    // Validation state
    private int _skeletonsNearNecroAfterMove;
    private int _skeletonsNearNecroTotal;
    private bool _movementPhaseValid;
    private int _engagedCount;
    private int _chasingCount;
    private bool _anyEngagement;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Horde Follow Scenario ===");
        DebugLog.Log(ScenarioLog, "Testing horde formation following: movement, regrouping, and engagement");

        var units = sim.UnitsMut;

        // Spawn necromancer at (15, 15)
        int necroIdx = units.AddUnit(new Vec2(15f, 15f), UnitType.Necromancer);
        units[necroIdx].AI = AIBehavior.PlayerControlled;
        _necroId = units[necroIdx].Id;
        sim.SetNecromancerIndex(necroIdx);
        DebugLog.Log(ScenarioLog, $"Spawned necromancer at (15, 15), idx={necroIdx}, id={_necroId}");

        // Spawn 8 skeletons near (15, 15) with AttackClosest AI
        for (int i = 0; i < 8; i++)
        {
            float x = 14f + (i % 4) * 1.0f;
            float y = 14f + (i / 4) * 1.0f;
            int idx = units.AddUnit(new Vec2(x, y), UnitType.Skeleton);
            units[idx].AI = AIBehavior.AttackClosest;
            _skeletonIds[i] = units[idx].Id;

            // Manually enroll in horde (Game1 does this normally, but scenarios bypass Game1 spawning)
            sim.Horde.AddUnit(units[idx].Id);

            DebugLog.Log(ScenarioLog, $"Spawned skeleton {i} at ({x:F1}, {y:F1}), idx={idx}, id={units[idx].Id}");
        }
        _skeletonCount = 8;

        DebugLog.Log(ScenarioLog, $"Total units: {units.Count} (1 necro + 8 skeletons)");
        DebugLog.Log(ScenarioLog, $"Horde units enrolled: {sim.Horde.HordeUnits.Count}");
        DebugLog.Log(ScenarioLog, $"Phase 0: Moving necromancer right for {MovementDuration}s");

        // Start moving necromancer to the right
        sim.SetNecromancerInput(new Vec2(1, 0), false);
        _phase = 0;
        _phaseTimer = 0f;
        _logTimer = 0f;

        ZoomOnLocation(20f, 15f, 15f);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;
        _phaseTimer += dt;

        // Periodic status logging
        _logTimer -= dt;
        if (_logTimer <= 0f)
        {
            _logTimer = 1f;
            LogStatus(sim);
        }

        switch (_phase)
        {
            case 0: // Movement phase
                TickMovementPhase(sim, dt);
                break;
            case 1: // Stop and verify regrouping
                TickStopVerifyPhase(sim, dt);
                break;
            case 2: // Enemy engagement phase
                TickEngagementPhase(sim, dt);
                break;
        }
    }

    private void TickMovementPhase(Simulation sim, float dt)
    {
        if (_phaseTimer >= MovementDuration)
        {
            DebugLog.Log(ScenarioLog, $"[{_elapsed:F1}s] Movement phase complete. Stopping necromancer.");

            // Stop the necromancer
            sim.SetNecromancerInput(Vec2.Zero, false);

            // Log necromancer position
            int necroIdx = sim.NecromancerIndex;
            if (necroIdx >= 0 && necroIdx < sim.Units.Count)
            {
                var necroPos = sim.Units[necroIdx].Position;
                DebugLog.Log(ScenarioLog, $"  Necromancer stopped at ({necroPos.X:F1}, {necroPos.Y:F1})");
                DebugLog.Log(ScenarioLog, $"  Horde circle center: ({sim.Horde.CircleCenter.X:F1}, {sim.Horde.CircleCenter.Y:F1})");
            }

            _phase = 1;
            _phaseTimer = 0f;
        }
    }

    private void TickStopVerifyPhase(Simulation sim, float dt)
    {
        if (_phaseTimer >= StopVerifyDelay)
        {
            // Verify skeletons are near the necromancer after movement
            int necroIdx = sim.NecromancerIndex;
            if (necroIdx < 0 || necroIdx >= sim.Units.Count)
            {
                DebugLog.Log(ScenarioLog, "ERROR: Necromancer not found during stop verify");
                _complete = true;
                return;
            }

            var necroPos = sim.Units[necroIdx].Position;
            var circleCenter = sim.Horde.CircleCenter;
            _skeletonsNearNecroAfterMove = 0;
            _skeletonsNearNecroTotal = 0;

            DebugLog.Log(ScenarioLog, $"[{_elapsed:F1}s] Stop+Verify: necro=({necroPos.X:F1}, {necroPos.Y:F1}), circle=({circleCenter.X:F1}, {circleCenter.Y:F1})");

            for (int i = 0; i < _skeletonCount; i++)
            {
                int idx = FindByID(sim.Units, _skeletonIds[i]);
                if (idx < 0) continue;
                _skeletonsNearNecroTotal++;

                var skelPos = sim.Units[idx].Position;
                float distToNecro = (skelPos - necroPos).Length();
                float distToCircle = (skelPos - circleCenter).Length();
                var hordeState = sim.Horde.GetUnitState(_skeletonIds[i]);

                DebugLog.Log(ScenarioLog, $"  Skeleton {i} (id={_skeletonIds[i]}): pos=({skelPos.X:F1}, {skelPos.Y:F1}), distNecro={distToNecro:F1}, distCircle={distToCircle:F1}, state={hordeState}");

                if (distToNecro < 15f)
                    _skeletonsNearNecroAfterMove++;
            }

            _movementPhaseValid = _skeletonsNearNecroAfterMove >= (_skeletonsNearNecroTotal * 0.5f);
            DebugLog.Log(ScenarioLog, $"  Skeletons near necromancer (<15 units): {_skeletonsNearNecroAfterMove}/{_skeletonsNearNecroTotal} -> {(_movementPhaseValid ? "OK" : "WEAK")}");

            // Spawn 2 soldiers at (30, 15) for engagement test
            DebugLog.Log(ScenarioLog, $"[{_elapsed:F1}s] Phase 2: Spawning 2 soldiers at (30, 15) for engagement test");
            var units = sim.UnitsMut;
            for (int i = 0; i < 2; i++)
            {
                int idx = units.AddUnit(new Vec2(30f + i * 1.5f, 15f), UnitType.Soldier);
                units[idx].AI = AIBehavior.AttackClosest;
                DebugLog.Log(ScenarioLog, $"  Spawned soldier {i} at ({30f + i * 1.5f:F1}, 15), idx={idx}, id={units[idx].Id}");
            }

            _phase = 2;
            _phaseTimer = 0f;
        }
    }

    private void TickEngagementPhase(Simulation sim, float dt)
    {
        // Track engagement states
        int currentEngaged = 0;
        int currentChasing = 0;
        int currentFollowing = 0;
        int currentReturning = 0;

        for (int i = 0; i < _skeletonCount; i++)
        {
            var state = sim.Horde.GetUnitState(_skeletonIds[i]);
            switch (state)
            {
                case HordeUnitState.Following: currentFollowing++; break;
                case HordeUnitState.Chasing: currentChasing++; break;
                case HordeUnitState.Engaged: currentEngaged++; break;
                case HordeUnitState.Returning: currentReturning++; break;
            }
        }

        if (currentEngaged > _engagedCount || currentChasing > _chasingCount)
        {
            DebugLog.Log(ScenarioLog, $"[{_elapsed:F1}s] Engagement update: following={currentFollowing}, chasing={currentChasing}, engaged={currentEngaged}, returning={currentReturning}");
        }

        _engagedCount = Math.Max(_engagedCount, currentEngaged);
        _chasingCount = Math.Max(_chasingCount, currentChasing);

        if (currentEngaged > 0 || currentChasing > 0)
            _anyEngagement = true;

        // Check combat log for hits
        int combatEntries = sim.CombatLog.Entries.Count;

        // End after engagement duration or if all enemies dead
        int humanAlive = 0;
        for (int i = 0; i < sim.Units.Count; i++)
        {
            if (sim.Units[i].Alive && sim.Units[i].Faction == Faction.Human)
                humanAlive++;
        }

        if (_phaseTimer >= EngagementDuration || (humanAlive == 0 && _phaseTimer > 2f))
        {
            DebugLog.Log(ScenarioLog, $"[{_elapsed:F1}s] Engagement phase complete.");
            DebugLog.Log(ScenarioLog, $"  Humans remaining: {humanAlive}");
            DebugLog.Log(ScenarioLog, $"  Peak engaged: {_engagedCount}, peak chasing: {_chasingCount}");
            DebugLog.Log(ScenarioLog, $"  Any engagement detected: {_anyEngagement}");
            DebugLog.Log(ScenarioLog, $"  Combat log entries: {combatEntries}");
            _complete = true;
        }
    }

    private void LogStatus(Simulation sim)
    {
        int necroIdx = sim.NecromancerIndex;
        if (necroIdx < 0 || necroIdx >= sim.Units.Count) return;

        var necroPos = sim.Units[necroIdx].Position;
        var circleCenter = sim.Horde.CircleCenter;

        int following = 0, chasing = 0, engaged = 0, returning = 0;
        for (int i = 0; i < _skeletonCount; i++)
        {
            var state = sim.Horde.GetUnitState(_skeletonIds[i]);
            switch (state)
            {
                case HordeUnitState.Following: following++; break;
                case HordeUnitState.Chasing: chasing++; break;
                case HordeUnitState.Engaged: engaged++; break;
                case HordeUnitState.Returning: returning++; break;
            }
        }

        int undeadAlive = 0, humanAlive = 0;
        for (int i = 0; i < sim.Units.Count; i++)
        {
            if (!sim.Units[i].Alive) continue;
            if (sim.Units[i].Faction == Faction.Undead) undeadAlive++;
            else humanAlive++;
        }

        DebugLog.Log(ScenarioLog, $"[{_elapsed:F1}s] phase={_phase} necro=({necroPos.X:F1},{necroPos.Y:F1}) circle=({circleCenter.X:F1},{circleCenter.Y:F1}) horde=[F:{following} C:{chasing} E:{engaged} R:{returning}] alive=[U:{undeadAlive} H:{humanAlive}]");
    }

    private static int FindByID(UnitArrays units, uint id)
    {
        for (int i = 0; i < units.Count; i++)
            if (units[i].Id == id) return i;
        return -1;
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "=== Horde Follow Validation ===");

        // Check 1: Most skeletons should be within 15 units of necromancer after movement
        bool followingPass = _skeletonsNearNecroAfterMove >= (_skeletonsNearNecroTotal + 1) / 2;
        DebugLog.Log(ScenarioLog, $"Following check: {_skeletonsNearNecroAfterMove}/{_skeletonsNearNecroTotal} within 15 units of necromancer -> {(followingPass ? "PASS" : "FAIL")}");

        // Check 2: Some skeletons should have engaged or chased enemies
        bool engagementPass = _anyEngagement;
        DebugLog.Log(ScenarioLog, $"Engagement check: engaged={_anyEngagement}, peak engaged={_engagedCount}, peak chasing={_chasingCount} -> {(engagementPass ? "PASS" : "FAIL")}");

        // Check 3: Horde units should still be enrolled
        int enrolled = sim.Horde.HordeUnits.Count;
        DebugLog.Log(ScenarioLog, $"Horde enrollment: {enrolled} units still in horde");

        // Final unit counts
        int undeadAlive = 0, humanAlive = 0;
        for (int i = 0; i < sim.Units.Count; i++)
        {
            if (!sim.Units[i].Alive) continue;
            if (sim.Units[i].Faction == Faction.Undead) undeadAlive++;
            else humanAlive++;
        }
        DebugLog.Log(ScenarioLog, $"Final state: {undeadAlive} undead, {humanAlive} human alive");
        DebugLog.Log(ScenarioLog, $"Combat log entries: {sim.CombatLog.Entries.Count}");

        bool pass = followingPass && engagementPass;
        DebugLog.Log(ScenarioLog, $"Overall: {(pass ? "PASS" : "FAIL")}");
        return pass ? 0 : 1;
    }
}
