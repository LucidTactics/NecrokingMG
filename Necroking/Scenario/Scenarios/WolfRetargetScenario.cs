using System;
using Necroking.AI;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Movement;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Verifies wolf retargets when its current target is out of melee reach and it
/// takes damage from a different attacker. Setup: wolf aggroes the "necromancer"
/// (Skeleton stand-in, runs away), zombie deer ally attacks the wolf. The wolf
/// should abandon the necromancer chase and switch aggro to the deer.
/// </summary>
public class WolfRetargetScenario : ScenarioBase
{
    public override string Name => "wolf_retarget";

    private float _elapsed;
    private bool _complete;
    private const float MaxDuration = 15f;

    private uint _wolfId;
    private uint _necroId;   // stand-in: Skeleton unit we manually control
    private uint _deerId;    // zombie deer ally

    private uint _initialTargetId; // target when the scenario starts
    private bool _retargetSeen;
    private float _retargetTime;
    private float _logTimer;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Wolf Retarget Test ===");
        DebugLog.Log(ScenarioLog, "Wolf chases fleeing 'necromancer'; zombie deer attacks wolf.");
        DebugLog.Log(ScenarioLog, "Expected: wolf retargets from necromancer → deer once necromancer is out of reach.");

        var units = sim.UnitsMut;

        // Wolf (archetype-based via SpawnUnitByID + Archetype set manually)
        int wolfIdx = sim.SpawnUnitByID("Wolf", new Vec2(15f, 20f));
        units[wolfIdx].Archetype = ArchetypeRegistry.WolfPack;
        units[wolfIdx].Faction = Faction.Animal;
        var wolfDef = sim.GameData?.Units.Get("Wolf");
        if (wolfDef != null)
        {
            units[wolfIdx].DetectionRange = wolfDef.DetectionRange;
            units[wolfIdx].DetectionBreakRange = wolfDef.DetectionBreakRange;
            units[wolfIdx].AlertDuration = wolfDef.AlertDuration;
            units[wolfIdx].AlertEscalateRange = wolfDef.AlertEscalateRange;
            units[wolfIdx].GroupAlertRadius = wolfDef.GroupAlertRadius;
        }
        units[wolfIdx].Stats.MaxHP = 500;
        units[wolfIdx].Stats.HP = 500;
        _wolfId = units[wolfIdx].Id;

        // "Necromancer" stand-in — placed CLOSE initially so wolf targets it first.
        // After targeting locks in, OnTick teleports it far away to simulate kiting.
        int necroIdx = units.AddUnit(new Vec2(12f, 20f), UnitType.Soldier);
        units[necroIdx].Faction = Faction.Human;
        units[necroIdx].AI = AIBehavior.MoveToPoint;
        units[necroIdx].MoveTarget = new Vec2(-100f, 20f);
        units[necroIdx].MaxSpeed = 20f;
        units[necroIdx].Stats.MaxHP = 500;
        units[necroIdx].Stats.HP = 500;
        _necroId = units[necroIdx].Id;

        // Zombie deer ally — farther than necro so wolf picks necro first, but close
        // enough (within detection range) that it'll still be spotted.
        int deerIdx = sim.SpawnUnitByID("ZombieFemaleDeer", new Vec2(22f, 20f));
        if (deerIdx >= 0)
        {
            units[deerIdx].Faction = Faction.Undead;
            units[deerIdx].AI = AIBehavior.AttackClosest;
            units[deerIdx].Stats.MaxHP = 500;
            units[deerIdx].Stats.HP = 500;
            units[deerIdx].Stats.Strength = 12;   // make sure hits land
            units[deerIdx].Stats.Attack = 14;
            _deerId = units[deerIdx].Id;
        }
        else
        {
            DebugLog.Log(ScenarioLog, "ZombieFemaleDeer not found — test cannot run");
        }

        DebugLog.Log(ScenarioLog, $"Wolf (id={_wolfId}) at (15,20), 'necromancer' (id={_necroId}) at (10,20) fleeing west, deer (id={_deerId}) at (25,20)");

        ZoomOnLocation(15f, 20f, 25f);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;
        var units = sim.Units;

        // After the wolf has locked on to the necromancer, teleport necro 12 tiles west
        // of the wolf each tick (out of melee reach, still inside DetectionBreakRange).
        // Also teleport the deer into melee range so it actually attacks the wolf.
        if (_initialTargetId != 0)
        {
            int wolfIdxForKite = FindByID(units, _wolfId);
            int necroIdxForKite = FindByID(units, _necroId);
            int deerIdxForKite = FindByID(units, _deerId);
            if (wolfIdxForKite >= 0 && necroIdxForKite >= 0 && deerIdxForKite >= 0)
            {
                var w = units[wolfIdxForKite];
                var mu = sim.UnitsMut;
                mu[necroIdxForKite].Position = new Vec2(w.Position.X - 12f, w.Position.Y);
                mu[necroIdxForKite].Velocity = Vec2.Zero;
                mu[necroIdxForKite].PreferredVel = Vec2.Zero;
                mu[deerIdxForKite].Position = new Vec2(w.Position.X + 0.9f, w.Position.Y);
                mu[deerIdxForKite].Velocity = Vec2.Zero;
                mu[deerIdxForKite].PreferredVel = Vec2.Zero;
            }
        }

        int wIdx = FindByID(units, _wolfId);
        int dIdx = FindByID(units, _deerId);
        if (wIdx < 0 || dIdx < 0) { _complete = true; return; }

        // Snapshot the wolf's initial target (whoever it first locked onto).
        if (_initialTargetId == 0 && units[wIdx].Target.IsUnit)
        {
            _initialTargetId = units[wIdx].Target.UnitID;
            DebugLog.Log(ScenarioLog, $"t={_elapsed:F2}s: Wolf initial target = id {_initialTargetId}");
        }

        // Detect retarget: current target differs from initial.
        if (!_retargetSeen && _initialTargetId != 0
            && units[wIdx].Target.IsUnit && units[wIdx].Target.UnitID != _initialTargetId)
        {
            _retargetSeen = true;
            _retargetTime = _elapsed;
            DebugLog.Log(ScenarioLog, $"t={_elapsed:F2}s: Wolf retargeted! New target id={units[wIdx].Target.UnitID}, deer id={_deerId}");
        }

        // Periodic state log
        _logTimer -= dt;
        if (_logTimer <= 0f)
        {
            _logTimer = 1f;
            int nIdx = FindByID(units, _necroId);
            float distToNecro = nIdx >= 0 ? (units[nIdx].Position - units[wIdx].Position).Length() : -1f;
            float distToDeer = (units[dIdx].Position - units[wIdx].Position).Length();
            uint curTarget = units[wIdx].Target.IsUnit ? units[wIdx].Target.UnitID : 0;
            DebugLog.Log(ScenarioLog,
                $"t={_elapsed:F2}s: wolf target={curTarget} HitReacting={units[wIdx].HitReacting} " +
                $"InCombat={units[wIdx].InCombat} LastAttacker={units[wIdx].LastAttackerID} " +
                $"distToNecro={distToNecro:F1} distToDeer={distToDeer:F1}");
        }

        // Stop 3s after retarget (observed) or at max duration
        if (_retargetSeen && _elapsed > _retargetTime + 2f) _complete = true;
        if (_elapsed >= MaxDuration) _complete = true;
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "=== Validation ===");
        DebugLog.Log(ScenarioLog, $"Wolf retargeted during scenario: {_retargetSeen}");
        if (_retargetSeen) DebugLog.Log(ScenarioLog, $"Retarget happened at t={_retargetTime:F2}s");
        bool pass = _retargetSeen;
        DebugLog.Log(ScenarioLog, $"Overall: {(pass ? "PASS" : "FAIL")}");
        return pass ? 0 : 1;
    }

    private static int FindByID(UnitArrays units, uint id)
    {
        for (int i = 0; i < units.Count; i++)
            if (units[i].Id == id) return i;
        return -1;
    }
}
