using System;
using Necroking.AI;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Movement;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Tests the weapon-bonus Knockdown on-hit effect: a wolf with the Knockdown bonus
/// attacks a soldier, the soldier goes into knockdown (Incap + buff_knockdown),
/// defense is reduced, and after the 2 s delay + per-second recovery rolls the
/// soldier stands back up.
/// </summary>
public class KnockdownTestScenario : ScenarioBase
{
    public override string Name => "knockdown_test";

    private float _elapsed;
    private bool _complete;
    private const float MaxDuration = 20f;

    private uint _wolfId;
    private uint _soldierId;

    private bool _wasKnockedDown;
    private bool _didRecover;
    private float _knockdownBeganAt = -1f;
    private float _knockdownEndedAt = -1f;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Knockdown Test ===");
        DebugLog.Log(ScenarioLog, "Wolf with HasKnockdown bonus attacks a soldier at melee range.");

        var units = sim.UnitsMut;

        int wolfIdx = sim.SpawnUnitByID("Wolf", new Vec2(10f, 20f));
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
        units[wolfIdx].Stats.HasKnockdown = true;  // test-only flag; normally set via weapon bonus
        units[wolfIdx].Stats.Strength = 20;        // make the knockdown roll reliably succeed
        units[wolfIdx].Stats.Attack = 20;          // so the wolf actually hits the soldier
        _wolfId = units[wolfIdx].Id;
        DebugLog.Log(ScenarioLog, $"Wolf: id={_wolfId} str={units[wolfIdx].Stats.Strength} size={units[wolfIdx].Size} HasKnockdown=true");

        // Soldier 2 tiles away — close enough that wolf engages without pouncing.
        int soldIdx = units.AddUnit(new Vec2(12f, 20f), UnitType.Soldier);
        units[soldIdx].AI = AIBehavior.IdleAtPoint;
        units[soldIdx].Faction = Faction.Human;
        units[soldIdx].Stats.Strength = 8;     // weaker so the STR roll goes to the wolf
        units[soldIdx].Stats.Defense = 10;
        units[soldIdx].Stats.MaxHP = 500;      // keep alive through the full scenario
        units[soldIdx].Stats.HP = 500;
        _soldierId = units[soldIdx].Id;
        DebugLog.Log(ScenarioLog, $"Soldier: id={_soldierId} str={units[soldIdx].Stats.Strength} size={units[soldIdx].Size} defense={units[soldIdx].Stats.Defense}");

        ZoomOnLocation(11f, 20f, 50f);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;
        var units = sim.Units;

        int sIdx = FindByID(units, _soldierId);
        if (sIdx < 0)
        {
            _complete = true;
            return;
        }

        bool isKnockedDown = units[sIdx].Incap.Active;
        if (isKnockedDown && !_wasKnockedDown)
        {
            _wasKnockedDown = true;
            _knockdownBeganAt = _elapsed;
            // Find the knockdown buff duration
            float kdDuration = 0f;
            foreach (var b in units[sIdx].ActiveBuffs)
                if (b.BuffDefID == "buff_knockdown") { kdDuration = b.RemainingDuration; break; }
            DebugLog.Log(ScenarioLog, $"t={_elapsed:F2}s: soldier knocked down (buff duration={kdDuration:F1}s)");
        }
        if (!isKnockedDown && _wasKnockedDown && !_didRecover)
        {
            // Either Incap went false (recovery done) or buff was removed
            if (!units[sIdx].Incap.IsLocked)
            {
                _didRecover = true;
                _knockdownEndedAt = _elapsed;
                DebugLog.Log(ScenarioLog, $"t={_elapsed:F2}s: soldier fully recovered from knockdown (duration={_knockdownEndedAt - _knockdownBeganAt:F2}s)");
            }
        }

        if (_wasKnockedDown && _didRecover && _elapsed > _knockdownEndedAt + 0.5f)
            _complete = true;
        if (_elapsed >= MaxDuration)
        {
            DebugLog.Log(ScenarioLog, $"t={_elapsed:F2}s: timeout");
            _complete = true;
        }
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "=== Knockdown Validation ===");
        DebugLog.Log(ScenarioLog, $"Got knocked down: {_wasKnockedDown}");
        DebugLog.Log(ScenarioLog, $"Recovered: {_didRecover}");
        if (_knockdownBeganAt > 0)
            DebugLog.Log(ScenarioLog, $"Knockdown duration (elapsed scenario time): {_knockdownEndedAt - _knockdownBeganAt:F2}s");

        bool pass = _wasKnockedDown && _didRecover;
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
