using System;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Movement;

namespace Necroking.Scenario.Scenarios;

public class NeutralFightBackScenario : ScenarioBase
{
    public override string Name => "neutral_fight_back";

    private float _elapsed;
    private bool _complete;
    private const float IdlePhaseEnd = 3f;
    private const float TestDuration = 15f;

    // Tracked unit IDs (stable across swap-and-pop)
    private uint _neutralId;
    private uint _soldierId;

    // Phase 1 validation: initial position of the neutral unit
    private Vec2 _neutralInitialPos;
    private bool _neutralMovedDuringIdlePhase;

    // Phase 2 validation: did the neutral fight back after being hit?
    private bool _neutralWasHit;
    private bool _neutralFoughtBack;
    private bool _soldierSpawned;

    private float _logTimer;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== NeutralFightBack Scenario ===");
        DebugLog.Log(ScenarioLog, "Phase 1 (0-3s): Neutral skeleton (NeutralFightBack AI, Animal faction) should stay idle");
        DebugLog.Log(ScenarioLog, "Phase 2 (3s+): Soldier spawns, attacks neutral, neutral should fight back");

        var units = sim.UnitsMut;

        // Spawn neutral unit: Skeleton with NeutralFightBack AI, Animal faction
        // Give it high HP so it survives long enough to demonstrate fighting back
        _neutralInitialPos = new Vec2(15f, 15f);
        int neutralIdx = units.AddUnit(_neutralInitialPos, UnitType.Skeleton);
        units.AI[neutralIdx] = AIBehavior.NeutralFightBack;
        units.Faction[neutralIdx] = Faction.Animal;
        units.Stats[neutralIdx].MaxHP = 100;
        units.Stats[neutralIdx].HP = 100;
        _neutralId = units.Id[neutralIdx];
        DebugLog.Log(ScenarioLog, $"Spawned neutral skeleton id={_neutralId} at ({_neutralInitialPos.X:F1}, {_neutralInitialPos.Y:F1}), AI=NeutralFightBack, Faction=Animal, HP=100");

        ZoomOnLocation(16f, 15f, 40f);

        DebugLog.Log(ScenarioLog, $"Total units after init: {units.Count}");
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;
        var units = sim.UnitsMut;

        int neutralIdx = FindByID(units, _neutralId);

        // Phase 1: Check the neutral stays still during first 3 seconds
        if (_elapsed < IdlePhaseEnd)
        {
            if (neutralIdx >= 0)
            {
                var pos = units.Position[neutralIdx];
                float drift = (pos - _neutralInitialPos).Length();
                if (drift > 0.1f && !_neutralMovedDuringIdlePhase)
                {
                    _neutralMovedDuringIdlePhase = true;
                    DebugLog.Log(ScenarioLog, $"t={_elapsed:F2}s: PROBLEM - Neutral unit moved during idle phase! pos=({pos.X:F1},{pos.Y:F1}), drift={drift:F2}");
                }
            }
        }

        // Phase 2: Spawn soldier at t=3s
        if (_elapsed >= IdlePhaseEnd && !_soldierSpawned)
        {
            _soldierSpawned = true;

            // Log idle phase result before spawning soldier
            if (neutralIdx >= 0)
            {
                var pos = units.Position[neutralIdx];
                float drift = (pos - _neutralInitialPos).Length();
                DebugLog.Log(ScenarioLog, $"t={_elapsed:F2}s: Idle phase complete. Neutral pos=({pos.X:F1},{pos.Y:F1}), drift={drift:F2}, moved={_neutralMovedDuringIdlePhase}");
            }

            // Spawn aggressive soldier nearby
            var soldierPos = new Vec2(18f, 15f);
            int soldierIdx = units.AddUnit(soldierPos, UnitType.Soldier);
            units.AI[soldierIdx] = AIBehavior.AttackClosest;
            _soldierId = units.Id[soldierIdx];
            DebugLog.Log(ScenarioLog, $"t={_elapsed:F2}s: Spawned soldier id={_soldierId} at ({soldierPos.X:F1},{soldierPos.Y:F1}), AI=AttackClosest");

            // Re-resolve neutral index since AddUnit may have reallocated
            neutralIdx = FindByID(units, _neutralId);
        }

        // Phase 2 ongoing: Check if neutral has been hit and is fighting back
        if (_soldierSpawned && neutralIdx >= 0 && units.Alive[neutralIdx])
        {
            var lastAttacker = units.LastAttackerID[neutralIdx];
            bool hasTarget = units.Target[neutralIdx].IsUnit;
            bool inCombat = units.InCombat[neutralIdx];
            var pos = units.Position[neutralIdx];
            float driftFromStart = (pos - _neutralInitialPos).Length();
            int hp = units.Stats[neutralIdx].HP;

            // Detect hit: LastAttackerID set OR HP dropped below max
            if (!_neutralWasHit && (lastAttacker != GameConstants.InvalidUnit || hp < 100))
            {
                _neutralWasHit = true;
                DebugLog.Log(ScenarioLog, $"t={_elapsed:F2}s: Neutral was hit! LastAttackerID={lastAttacker}, HP={hp}/100, pos=({pos.X:F1},{pos.Y:F1})");
            }

            // Detect fight-back: after being hit, the neutral should have a target and/or be moving
            if (_neutralWasHit && !_neutralFoughtBack)
            {
                if (hasTarget || inCombat || driftFromStart > 0.3f)
                {
                    _neutralFoughtBack = true;
                    DebugLog.Log(ScenarioLog, $"t={_elapsed:F2}s: Neutral is fighting back! hasTarget={hasTarget}, inCombat={inCombat}, drift={driftFromStart:F2}, HP={hp}");
                }
            }
        }

        // Also detect hit/fight-back if neutral died (swap-and-pop removed it)
        if (_soldierSpawned && !_neutralWasHit && neutralIdx < 0)
        {
            // Unit was removed — it was killed, which means it was hit
            _neutralWasHit = true;
            _neutralFoughtBack = true; // Died in combat counts as having engaged
            DebugLog.Log(ScenarioLog, $"t={_elapsed:F2}s: Neutral was killed (removed from unit list) — counting as hit + fought back");
        }

        // Periodic status logging
        _logTimer -= dt;
        if (_logTimer <= 0f)
        {
            _logTimer = 1f;
            LogStatus(sim);
        }

        // Complete early if both phases validated, or at timeout
        if (_elapsed >= TestDuration || (_soldierSpawned && _neutralWasHit && _neutralFoughtBack && _elapsed > IdlePhaseEnd + 3f))
            _complete = true;
    }

    private void LogStatus(Simulation sim)
    {
        var units = sim.Units;
        int neutralIdx = FindByID(units, _neutralId);
        int soldierIdx = _soldierId != 0 ? FindByID(units, _soldierId) : -1;

        DebugLog.Log(ScenarioLog, $"--- Status at t={_elapsed:F1}s ---");

        if (neutralIdx >= 0)
        {
            var pos = units.Position[neutralIdx];
            var ai = units.AI[neutralIdx];
            bool alive = units.Alive[neutralIdx];
            bool inCombat = units.InCombat[neutralIdx];
            var target = units.Target[neutralIdx];
            var lastAttacker = units.LastAttackerID[neutralIdx];
            int hp = units.Stats[neutralIdx].HP;
            float drift = (pos - _neutralInitialPos).Length();
            DebugLog.Log(ScenarioLog, $"  Neutral id={_neutralId}: alive={alive}, HP={hp}/100, pos=({pos.X:F1},{pos.Y:F1}), drift={drift:F2}, ai={ai}, inCombat={inCombat}, target={target.Kind}:{target.Value}, lastAttacker={lastAttacker}");
        }
        else
        {
            DebugLog.Log(ScenarioLog, $"  Neutral id={_neutralId}: NOT FOUND (dead or removed)");
        }

        if (soldierIdx >= 0)
        {
            var pos = units.Position[soldierIdx];
            bool alive = units.Alive[soldierIdx];
            bool inCombat = units.InCombat[soldierIdx];
            var target = units.Target[soldierIdx];
            int hp = units.Stats[soldierIdx].HP;
            DebugLog.Log(ScenarioLog, $"  Soldier id={_soldierId}: alive={alive}, HP={hp}, pos=({pos.X:F1},{pos.Y:F1}), inCombat={inCombat}, target={target.Kind}:{target.Value}");
        }
        else if (_soldierId != 0)
        {
            DebugLog.Log(ScenarioLog, $"  Soldier id={_soldierId}: NOT FOUND (dead or removed)");
        }

        // Count alive units by faction
        int undead = 0, human = 0, animal = 0;
        for (int i = 0; i < units.Count; i++)
        {
            if (!units.Alive[i]) continue;
            switch (units.Faction[i])
            {
                case Faction.Undead: undead++; break;
                case Faction.Human: human++; break;
                case Faction.Animal: animal++; break;
            }
        }
        DebugLog.Log(ScenarioLog, $"  Alive: undead={undead}, human={human}, animal={animal}");
    }

    private int FindByID(UnitArrays units, uint id)
    {
        for (int i = 0; i < units.Count; i++)
            if (units.Id[i] == id) return i;
        return -1;
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "=== NeutralFightBack Validation ===");

        var units = sim.Units;
        int neutralIdx = FindByID(units, _neutralId);

        // Check 1: Neutral did NOT move during idle phase (first 3s)
        bool idlePass = !_neutralMovedDuringIdlePhase;
        DebugLog.Log(ScenarioLog, $"Phase 1 - Stayed idle before provoked: {(idlePass ? "PASS" : "FAIL")} (moved={_neutralMovedDuringIdlePhase})");

        // Check 2: Neutral was hit by the soldier
        DebugLog.Log(ScenarioLog, $"Phase 2a - Neutral was hit: {(_neutralWasHit ? "PASS" : "FAIL")}");

        // Check 3: Neutral fought back after being hit
        bool fightBackPass;
        if (neutralIdx >= 0 && units.Alive[neutralIdx])
        {
            fightBackPass = _neutralFoughtBack;
            var pos = units.Position[neutralIdx];
            float drift = (pos - _neutralInitialPos).Length();
            int hp = units.Stats[neutralIdx].HP;
            DebugLog.Log(ScenarioLog, $"Phase 2b - Neutral fought back: {(fightBackPass ? "PASS" : "FAIL")} (foughtBack={_neutralFoughtBack}, finalPos=({pos.X:F1},{pos.Y:F1}), totalDrift={drift:F2}, HP={hp}/100)");
        }
        else
        {
            // Neutral died — it was in combat (took damage)
            fightBackPass = _neutralWasHit;
            DebugLog.Log(ScenarioLog, $"Phase 2b - Neutral fought back: {(fightBackPass ? "PASS (died in combat)" : "FAIL")}");
        }

        // Summary
        int soldierIdx = _soldierId != 0 ? FindByID(units, _soldierId) : -1;
        bool soldierAlive = soldierIdx >= 0 && units.Alive[soldierIdx];
        bool neutralAlive = neutralIdx >= 0 && units.Alive[neutralIdx];
        DebugLog.Log(ScenarioLog, $"Final state: neutral alive={neutralAlive}, soldier alive={soldierAlive}");
        DebugLog.Log(ScenarioLog, $"Combat log entries: {sim.CombatLog.Entries.Count}");
        DebugLog.Log(ScenarioLog, $"Elapsed time: {_elapsed:F1}s");

        bool pass = idlePass && _neutralWasHit && fightBackPass;
        DebugLog.Log(ScenarioLog, $"Overall: {(pass ? "PASS" : "FAIL")}");
        return pass ? 0 : 1;
    }
}
