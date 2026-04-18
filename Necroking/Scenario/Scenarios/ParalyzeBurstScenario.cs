using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.Game;
using Necroking.GameSystems;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Verifies paralyze_burst: cast at a stationary soldier, then every 0.25s log
/// MaxSpeed, Incap.Active, AttackCooldown, and the unit's current anim state.
///
/// Expected sequence:
///   t=0.0s  cast → slow phase begins (timer=8s, speed = 0.7 × base)
///   t=0..8  MaxSpeed lerps from 0.7×base down to 0
///   t=8s    stun phase begins (speed=0, Incap.Active=true, AttackCooldown=6, Stunned anim)
///   t=14s   stun ends (Incap cleared, speed restored, cooldown=0)
/// </summary>
public class ParalyzeBurstScenario : ScenarioBase
{
    public override string Name => "paralyze_burst_test";
    public override bool IsComplete => _complete;

    private bool _complete;
    private bool _cast;
    private uint _unitID;
    private float _baseSpeed;
    private float _elapsed;
    private float _logTimer;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Paralyze Burst Test ===");

        var units = sim.UnitsMut;
        int idx = units.AddUnit(new Vec2(32f, 32f), UnitType.Soldier);
        units[idx].Stats.HP = 9999;
        units[idx].Stats.MaxHP = 9999;
        units[idx].AI = AIBehavior.IdleAtPoint;
        _unitID = units[idx].Id;
        _baseSpeed = units[idx].Stats.CombatSpeed;
        DebugLog.Log(ScenarioLog, $"Spawned soldier at (32,32). baseSpeed={_baseSpeed:F2}");
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;

        if (!_cast && _elapsed >= 0.2f)
        {
            _cast = true;
            var spell = sim.GameData?.Spells.Get("paralyze_burst");
            if (spell == null) { DebugLog.Log(ScenarioLog, "ERR: paralyze_burst missing"); _complete = true; return; }
            int idx = ResolveIdx(sim);
            sim.PoisonClouds.SpawnCloud(sim.Units[idx].Position, spell, Faction.Undead);
            if (spell.Damage > 0)
            {
                DamageSystem.ApplyAoE(sim.UnitsMut, sim.Quadtree, sim.Units[idx].Position, spell.CloudRadius,
                    spell.Damage, DamageType.Poison,
                    DamageFlags.ArmorNegating | DamageFlags.DefenseNegating,
                    Faction.Undead, sim.DamageEventsMut);
            }
            // Paralyze clouds don't do the poison path above — apply directly once at burst time
            // (same branch SpellEffectSystem.ExecuteCloud takes).
            if (spell.CloudAppliesParalysis)
            {
                PotionSystem.ApplyParalysis(idx, sim.UnitsMut);
            }
            DebugLog.Log(ScenarioLog, $"[{_elapsed:F2}s] Cast paralyze_burst. slowTimer={sim.Units[idx].ParalysisSlowTimer:F2} stunTimer={sim.Units[idx].ParalysisStunTimer:F2}");
        }

        if (!_cast) return;

        _logTimer -= dt;
        if (_logTimer <= 0f)
        {
            _logTimer = 0.5f;
            int idx = ResolveIdx(sim);
            if (idx < 0) { _complete = true; return; }
            var u = sim.Units[idx];
            DebugLog.Log(ScenarioLog,
                $"[{_elapsed:F2}s] slow={u.ParalysisSlowTimer:F2} stun={u.ParalysisStunTimer:F2} " +
                $"maxSpeed={u.MaxSpeed:F2} incap={u.Incap.Active} atkCD={u.AttackCooldown:F2} " +
                $"paraFrac={PotionSystem.GetParalysisFraction(sim.Units, idx):F2}");
        }

        if (_elapsed > 18f) _complete = true;
    }

    private int ResolveIdx(Simulation sim)
    {
        for (int i = 0; i < sim.Units.Count; i++) if (sim.Units[i].Id == _unitID) return i;
        return -1;
    }

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "=== Paralyze Burst Test Complete ===");
        return 0;
    }
}
