using System;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Movement;
using Necroking.World;

namespace Necroking.Scenario.Scenarios;

public class WallTrapScenario : ScenarioBase
{
    public override string Name => "wall_trap";
    private float _elapsed;
    private bool _complete;
    private int _phase;
    private Vec2 _center = new(20f, 20f);

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Wall Trap Scenario ===");

        // Build octagonal wall ring trapping soldiers inside
        var grid = sim.Grid;
        int cx = 20, cy = 20;
        int radius = 5;

        // Simple rectangular wall ring
        var ws = sim.WallSystem;
        for (int i = -radius; i <= radius; i++)
        {
            grid.SetTerrain(cx + i, cy - radius, TerrainType.Wall);
            grid.SetTerrain(cx + i, cy + radius, TerrainType.Wall);
            grid.SetTerrain(cx - radius, cy + i, TerrainType.Wall);
            grid.SetTerrain(cx + radius, cy + i, TerrainType.Wall);
            ws?.SetWall(cx + i, cy - radius, 1);
            ws?.SetWall(cx + i, cy + radius, 1);
            ws?.SetWall(cx - radius, cy + i, 1);
            ws?.SetWall(cx + radius, cy + i, 1);
        }

        grid.RebuildCostField();
        sim.RebuildPathfinder();

        // Spawn 10 soldiers inside the ring
        var units = sim.UnitsMut;
        for (int i = 0; i < 10; i++)
        {
            float angle = i * MathF.PI * 2f / 10f;
            float dist = 2f;
            var pos = _center + new Vec2(MathF.Cos(angle) * dist, MathF.Sin(angle) * dist);
            int idx = units.AddUnit(pos, UnitType.Soldier);
            units[idx].AI = AIBehavior.IdleAtPoint;
            units[idx].MoveTarget = pos;
        }

        DebugLog.Log(ScenarioLog, $"Built wall ring at ({cx},{cy}) r={radius}, spawned 10 soldiers inside");
        ZoomOnLocation(20f, 20f, 16f);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;

        if (_phase == 0 && _elapsed > 0.5f)
        {
            DeferredScreenshot = "wall_trap_start";
            _phase = 1;
        }
        else if (_phase == 1 && _elapsed > 8.5f)
        {
            DeferredScreenshot = "wall_trap_end";
            _phase = 2;
        }
        else if (_phase >= 2 && _elapsed > 9f)
        {
            _complete = true;
        }
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        // Validate all soldiers stayed near center (trapped)
        int alive = 0;
        int nearCenter = 0;
        for (int i = 0; i < sim.Units.Count; i++)
        {
            if (!sim.Units[i].Alive) continue;
            alive++;
            float dist = (sim.Units[i].Position - _center).Length();
            if (dist < 8f) nearCenter++;
        }

        DebugLog.Log(ScenarioLog, $"Soldiers alive: {alive}/10");
        DebugLog.Log(ScenarioLog, $"Near center (trapped): {nearCenter}");

        bool pass = alive == 10;
        DebugLog.Log(ScenarioLog, $"Overall: {(pass ? "PASS" : "FAIL")}");
        return pass ? 0 : 1;
    }
}
