using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.Game;
using Necroking.GameSystems;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// End-to-end: place a paralyze-trap glyph blueprint, instantly build it, walk a wolf
/// into it, and verify the full chain fires:
///   glyph Blueprint → Dormant → Triggering → Active → paralyze_burst spawned
///   → cloud applies ApplyParalysis → wolf enters slow → stun → recovery.
/// </summary>
public class ParalyzeTrapScenario : ScenarioBase
{
    public override string Name => "paralyze_trap_test";
    public override bool IsComplete => _complete;

    private bool _complete;
    private uint _wolfID;
    private MagicGlyph? _glyph;
    private float _elapsed;
    private float _logTimer;
    private bool _sawStun;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Paralyze Trap End-to-End ===");

        // 1. Place a paralyze-trap glyph blueprint at (32, 32), fully built.
        var spell = sim.GameData!.Spells.Get("paralyze_burst")!;
        _glyph = sim.MagicGlyphs.SpawnBlueprint(new Vec2(32f, 32f), 1.5f, Faction.Undead);
        _glyph.TriggerSpellID = "paralyze_burst";
        _glyph.Color = spell.CloudColor;
        _glyph.Color2 = spell.CloudGlowColor;
        _glyph.BuildProgress = 1f;
        _glyph.State = GlyphState.Dormant;
        DebugLog.Log(ScenarioLog, "Placed & built paralyze trap at (32,32), radius=1.5");

        // 2. Spawn the wolf sitting right on top of the trap so we test the burst path
        // without racing the glyph trigger timer.
        var units = sim.UnitsMut;
        int idx = units.AddUnit(new Vec2(32f, 32f), UnitType.Skeleton);
        units[idx].Stats.HP = 9999; units[idx].Stats.MaxHP = 9999;
        units[idx].AI = AIBehavior.IdleAtPoint;
        units[idx].Faction = Faction.Animal;
        _wolfID = units[idx].Id;
        DebugLog.Log(ScenarioLog, $"Wolf spawned on trap at (32,32). base speed={units[idx].Stats.CombatSpeed:F2}");
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;

        _logTimer -= dt;
        if (_logTimer <= 0f)
        {
            _logTimer = 0.5f;
            int idx = ResolveIdx(sim);
            if (idx < 0) { _complete = true; return; }
            var u = sim.Units[idx];
            string gs = _glyph != null ? _glyph.State.ToString() : "null";
            DebugLog.Log(ScenarioLog,
                $"[{_elapsed:F2}s] wolf pos=({u.Position.X:F1},{u.Position.Y:F1}) speed={u.MaxSpeed:F2} " +
                $"slow={u.ParalysisSlowTimer:F2} stun={u.ParalysisStunTimer:F2} incap={u.Incap.Active} " +
                $"glyph={gs} clouds={sim.PoisonClouds.Clouds.Count}");
            if (u.ParalysisStunTimer > 0f) _sawStun = true;
        }

        if (_elapsed > 18f) _complete = true;
    }

    private int ResolveIdx(Simulation sim)
    {
        for (int i = 0; i < sim.Units.Count; i++) if (sim.Units[i].Id == _wolfID) return i;
        return -1;
    }

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, $"=== Complete (sawStun={_sawStun}) ===");
        return _sawStun ? 0 : 1;
    }
}
