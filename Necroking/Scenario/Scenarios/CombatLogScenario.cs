using System;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Movement;

namespace Necroking.Scenario.Scenarios;

public class CombatLogScenario : ScenarioBase
{
    public override string Name => "combat_log";
    private float _elapsed;
    private bool _complete;
    private const float FightDuration = 5f;
    private const float MaxDuration = 10f;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Combat Log Scenario ===");
        DebugLog.Log(ScenarioLog, "Testing combat log entry generation from melee combat");

        var units = sim.UnitsMut;

        // Spawn two units facing each other, close enough to fight
        int skelIdx = units.AddUnit(new Vec2(10f, 10f), UnitType.Skeleton);
        int soldierIdx = units.AddUnit(new Vec2(11f, 10f), UnitType.Soldier);

        DebugLog.Log(ScenarioLog, $"Spawned skeleton at (10, 10), idx={skelIdx}, HP={units.Stats[skelIdx].MaxHP}");
        DebugLog.Log(ScenarioLog, $"Spawned soldier at (11, 10), idx={soldierIdx}, HP={units.Stats[soldierIdx].MaxHP}");
        DebugLog.Log(ScenarioLog, $"Initial combat log entries: {sim.CombatLog.Entries.Count}");

        ZoomOnLocation(10.5f, 10f, 64f);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;

        // Log periodic status
        if ((int)(_elapsed * 2) > (int)((_elapsed - dt) * 2)) // every 0.5s
        {
            int undead = 0, human = 0;
            for (int i = 0; i < sim.Units.Count; i++)
            {
                if (!sim.Units.Alive[i]) continue;
                if (sim.Units.Faction[i] == Faction.Undead) undead++;
                else human++;
            }
            DebugLog.Log(ScenarioLog, $"t={_elapsed:F1}s: combat log entries={sim.CombatLog.Entries.Count}, alive: {undead} undead / {human} human");
        }

        // End after fight duration or if one side is dead
        bool oneSideDead = true;
        bool hasUndead = false, hasHuman = false;
        for (int i = 0; i < sim.Units.Count; i++)
        {
            if (!sim.Units.Alive[i]) continue;
            if (sim.Units.Faction[i] == Faction.Undead) hasUndead = true;
            else hasHuman = true;
        }
        oneSideDead = !hasUndead || !hasHuman;

        if ((_elapsed >= FightDuration && sim.CombatLog.Entries.Count > 0) || oneSideDead || _elapsed >= MaxDuration)
        {
            _complete = true;
        }
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "=== Combat Log Validation ===");

        var entries = sim.CombatLog.Entries;
        int totalEntries = entries.Count;
        int hits = 0, misses = 0, blocks = 0;
        int totalDamage = 0;

        for (int i = 0; i < totalEntries; i++)
        {
            var e = entries[i];
            switch (e.Outcome)
            {
                case CombatLogOutcome.Hit: hits++; totalDamage += e.NetDamage; break;
                case CombatLogOutcome.Miss: misses++; break;
                case CombatLogOutcome.Blocked: blocks++; break;
            }
        }

        DebugLog.Log(ScenarioLog, $"Combat log entries: {totalEntries}");
        DebugLog.Log(ScenarioLog, $"  Hits: {hits} (total damage: {totalDamage})");
        DebugLog.Log(ScenarioLog, $"  Misses: {misses}");
        DebugLog.Log(ScenarioLog, $"  Blocks: {blocks}");

        // Log first few entries for debugging
        int logCount = Math.Min(totalEntries, 5);
        for (int i = 0; i < logCount; i++)
        {
            var e = entries[i];
            DebugLog.Log(ScenarioLog, $"  Entry[{i}]: {e.AttackerName}({e.AttackerFaction}) vs {e.DefenderName}({e.DefenderFaction}) " +
                $"→ {e.Outcome}, atk={e.AttackBase}+{e.AttackDRN} def={e.DefenseBase}+{e.DefenseDRN} " +
                $"dmg={e.DamageBase}+{e.DamageDRN} prot={e.ProtBase}+{e.ProtDRN} net={e.NetDamage} loc={e.HitLocationName}");
        }

        // Validation
        bool hasEntries = totalEntries > 0;
        bool hasHitOrMiss = hits > 0 || misses > 0 || blocks > 0;

        DebugLog.Log(ScenarioLog, $"Has combat log entries: {(hasEntries ? "PASS" : "FAIL")} ({totalEntries} entries)");
        DebugLog.Log(ScenarioLog, $"Has hit/miss outcomes: {(hasHitOrMiss ? "PASS" : "FAIL")}");

        bool pass = hasEntries && hasHitOrMiss;
        DebugLog.Log(ScenarioLog, $"Overall: {(pass ? "PASS" : "FAIL")}");
        return pass ? 0 : 1;
    }
}
