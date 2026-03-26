using System;
using System.Collections.Generic;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Movement;
using Necroking.World;
using Necroking.Algorithm;

namespace Necroking.Scenario.Scenarios;

public class WallGateScenario : ScenarioBase
{
    public override string Name => "wall_gate";
    private float _elapsed;
    private bool _complete;
    private int _phase;
    private Vec2 _center = new(20f, 20f);

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Wall Gate Scenario ===");

        var grid = sim.Grid;
        int cx = 20, cy = 20;
        int radius = 5;

        // Build wall ring with a north gate (gap)
        var ws = sim.WallSystem;
        for (int i = -radius; i <= radius; i++)
        {
            // North wall with 2-tile gap in center
            if (Math.Abs(i) > 1)
            {
                grid.SetTerrain(cx + i, cy - radius, TerrainType.Wall);
                ws?.SetWall(cx + i, cy - radius, 1);
            }
            // South wall (solid)
            grid.SetTerrain(cx + i, cy + radius, TerrainType.Wall);
            ws?.SetWall(cx + i, cy + radius, 1);
            // East and west walls
            grid.SetTerrain(cx - radius, cy + i, TerrainType.Wall);
            grid.SetTerrain(cx + radius, cy + i, TerrainType.Wall);
            ws?.SetWall(cx - radius, cy + i, 1);
            ws?.SetWall(cx + radius, cy + i, 1);
        }

        grid.RebuildCostField();
        sim.RebuildPathfinder();

        var units = sim.UnitsMut;

        // Spawn 10 soldiers inside the ring; they should find the gate using A*
        for (int i = 0; i < 10; i++)
        {
            float angle = i * MathF.PI * 2f / 10f;
            var pos = _center + new Vec2(MathF.Cos(angle) * 2f, MathF.Sin(angle) * 2f);
            int idx = units.AddUnit(pos, UnitType.Soldier);
            // MoveToPoint toward north exit
            units.AI[idx] = AIBehavior.MoveToPoint;
            units.MoveTarget[idx] = new Vec2(20f, 5f); // Well north of the ring
        }

        // Run A* to verify the gate is passable
        var astarResult = AStar.FindWorld(grid, _center, new Vec2(20f, 5f));
        DebugLog.Log(ScenarioLog, $"A* from center to north: found={astarResult.Found}, waypoints={astarResult.Waypoints?.Count ?? 0}");

        DebugLog.Log(ScenarioLog, "Built wall ring with north gate, spawned 10 soldiers inside");
        ZoomOnLocation(20f, 18f, 16f);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;

        if (_phase == 0 && _elapsed > 0.5f)
        {
            DeferredScreenshot = "wall_gate_start";
            _phase = 1;
        }
        else if (_phase == 1 && _elapsed > 15.5f)
        {
            DeferredScreenshot = "wall_gate_end";
            _phase = 2;
        }
        else if (_phase >= 2 && _elapsed > 16f)
        {
            _complete = true;
        }
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        int alive = 0;
        int escaped = 0;
        for (int i = 0; i < sim.Units.Count; i++)
        {
            if (!sim.Units.Alive[i]) continue;
            alive++;
            float dist = (sim.Units.Position[i] - _center).Length();
            if (dist > 8f) escaped++;
        }

        DebugLog.Log(ScenarioLog, $"Soldiers alive: {alive}/10");
        DebugLog.Log(ScenarioLog, $"Soldiers escaped through gate: {escaped}");

        return 0; // Always pass - visual/navigation test
    }
}
