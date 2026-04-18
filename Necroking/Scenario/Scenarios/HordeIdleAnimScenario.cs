using System;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Reproduces the "horde members stuck in Walk animation while stationary" bug.
/// Spawns a necromancer and 12 skeletons enrolled in the horde, waits for them
/// to settle into their slots, then logs per-frame Velocity / PreferredVel /
/// anim-state for the first 5 skeletons so we can see why Velocity never
/// actually drops below the 0.25 idle threshold even though the AI intends
/// to hold still.
/// </summary>
public class HordeIdleAnimScenario : ScenarioBase
{
    public override string Name => "horde_idle_anim";
    public override bool IsComplete => _complete;

    private bool _complete;
    private float _elapsed;
    private float _logTimer;
    private readonly uint[] _skelIds = new uint[12];

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Horde Idle Anim Repro ===");

        var units = sim.UnitsMut;
        // Spawn a stationary "necromancer-like" unit to anchor the horde.
        int nIdx = units.AddUnit(new Vec2(32f, 32f), UnitType.Necromancer);
        units[nIdx].AI = AIBehavior.IdleAtPoint;
        units[nIdx].Faction = Faction.Undead;
        sim.SetNecromancerIndex(nIdx);

        // Spawn 12 skeletons in a small cluster — ORCA pressure will be high.
        for (int i = 0; i < 12; i++)
        {
            float a = i * MathF.Tau / 12f;
            var pos = new Vec2(32f + MathF.Cos(a) * 1.5f, 32f + MathF.Sin(a) * 1.5f);
            int idx = units.AddUnit(pos, UnitType.Skeleton);
            units[idx].Faction = Faction.Undead;
            units[idx].Archetype = AI.ArchetypeRegistry.HordeMinion;
            sim.Horde.AddUnit(units[idx].Id);
            _skelIds[i] = units[idx].Id;
        }

        DebugLog.Log(ScenarioLog, $"Horde size: {sim.Horde.HordeUnits.Count}");
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;
        _logTimer -= dt;

        // Start logging after settle, then every 0.5s for the rest of the run.
        if (_elapsed >= 2f && _logTimer <= 0f)
        {
            _logTimer = 0.5f;
            for (int k = 0; k < 5; k++)
            {
                int idx = ResolveIdx(sim, _skelIds[k]);
                if (idx < 0) continue;
                var u = sim.Units[idx];
                DebugLog.Log(ScenarioLog,
                    $"[t={_elapsed:F2}s] skel{k} pos=({u.Position.X:F2},{u.Position.Y:F2}) " +
                    $"|vel|={u.Velocity.Length():F2}  |ema|={u.VelocityEMA.Length():F2}  " +
                    $"|pref|={u.PreferredVel.Length():F2}");
            }
        }

        if (_elapsed > 8f) _complete = true;
    }

    private int ResolveIdx(Simulation sim, uint id)
    {
        for (int i = 0; i < sim.Units.Count; i++) if (sim.Units[i].Id == id) return i;
        return -1;
    }

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "=== Horde Idle Anim Repro complete ===");
        return 0;
    }
}
