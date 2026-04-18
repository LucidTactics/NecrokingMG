using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Reproduces "cast poison_burst at a wolf". Spawns a stationary wolf, spawns a poison_burst
/// cloud centered on it, and logs every stack change + HP drop tick-by-tick so we can see
/// exactly what the player sees as floating green numbers.
/// </summary>
public class PoisonDrainScenario : ScenarioBase
{
    public override string Name => "poison_drain";
    public override bool IsComplete => _complete;

    private bool _complete;
    private bool _spawned;
    private uint _unitID;
    private int _lastStacks = -1;
    private int _lastHP = -1;
    private float _elapsed;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Poison Burst @ Wolf (stack+HP trace) ===");

        var units = sim.UnitsMut;
        int idx = units.AddUnit(new Vec2(32f, 32f), UnitType.Skeleton);
        units[idx].Stats.HP = 9999;
        units[idx].Stats.MaxHP = 9999;
        units[idx].AI = AIBehavior.IdleAtPoint;
        units[idx].Faction = Faction.Animal; // so undead cloud damages it
        _unitID = units[idx].Id;
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;

        if (!_spawned && _elapsed >= 0.2f)
        {
            _spawned = true;
            var spell = sim.GameData?.Spells.Get("poison_burst");
            if (spell == null) { DebugLog.Log(ScenarioLog, "ERR: poison_burst spell missing"); _complete = true; return; }

            int idx = ResolveIdx(sim);
            var pos = sim.Units[idx].Position;
            sim.PoisonClouds.SpawnCloud(pos, spell, Faction.Undead);
            // Spell's initial AoE burst (same path ExecuteCloud uses)
            if (spell.Damage > 0)
            {
                DamageSystem.ApplyAoE(sim.UnitsMut, sim.Quadtree, pos, spell.CloudRadius,
                    spell.Damage, DamageType.Poison,
                    DamageFlags.ArmorNegating | DamageFlags.DefenseNegating,
                    Faction.Undead, sim.DamageEventsMut);
            }
            DebugLog.Log(ScenarioLog, $"[{_elapsed:F2}s] Cast poison_burst. spell.Damage={spell.Damage} cloudTickDmg={spell.CloudTickDamage} cloudDuration={spell.CloudDuration}");
            _lastStacks = sim.Units[idx].PoisonStacks;
            _lastHP = sim.Units[idx].Stats.HP;
            DebugLog.Log(ScenarioLog, $"[{_elapsed:F2}s] After cast: stacks={_lastStacks} HP={_lastHP}");
        }

        if (_spawned)
        {
            int idx = ResolveIdx(sim);
            if (idx < 0) { _complete = true; return; }
            int curStacks = sim.Units[idx].PoisonStacks;
            int curHP = sim.Units[idx].Stats.HP;
            if (curStacks != _lastStacks || curHP != _lastHP)
            {
                int ds = curStacks - _lastStacks;
                int dh = _lastHP - curHP;
                string kind = (ds > 0 && dh == 0) ? "STACK+"
                            : (ds < 0 && dh > 0) ? "DOT"
                            : "?";
                DebugLog.Log(ScenarioLog,
                    $"[{_elapsed:F2}s] {kind}  stacks {_lastStacks}->{curStacks} (Δ{(ds >= 0 ? "+" : "")}{ds})  HP {_lastHP}->{curHP} (lost {dh})");
                _lastStacks = curStacks;
                _lastHP = curHP;
            }
            if ((curStacks == 0 && sim.PoisonClouds.Clouds.Count == 0) || _elapsed > 90f) _complete = true;
        }
    }

    private int ResolveIdx(Simulation sim)
    {
        for (int i = 0; i < sim.Units.Count; i++)
            if (sim.Units[i].Id == _unitID) return i;
        return -1;
    }

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "=== Poison Drain Test Complete ===");
        return 0;
    }
}
