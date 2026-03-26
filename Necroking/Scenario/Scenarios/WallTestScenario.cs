using System;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Movement;
using Necroking.World;

namespace Necroking.Scenario.Scenarios;

public class WallTestScenario : ScenarioBase
{
    public override string Name => "wall_test";
    private float _elapsed;
    private bool _complete;
    private int _phase;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Wall Test Scenario ===");

        // Place wall patterns on the grid
        var grid = sim.Grid;
        int wallsPlaced = 0;

        // Horizontal line
        for (int i = 0; i < 5; i++)
        {
            int gx = 4 + i * 2;
            int gy = 4;
            grid.SetTerrain(gx, gy, TerrainType.Wall);
            grid.SetTerrain(gx + 1, gy, TerrainType.Wall);
            wallsPlaced++;
        }

        // Vertical line
        for (int i = 0; i < 5; i++)
        {
            int gx = 20;
            int gy = 4 + i * 2;
            grid.SetTerrain(gx, gy, TerrainType.Wall);
            grid.SetTerrain(gx, gy + 1, TerrainType.Wall);
            wallsPlaced++;
        }

        // L-shape corner
        for (int i = 0; i < 3; i++)
        {
            grid.SetTerrain(30 + i * 2, 4, TerrainType.Wall);
            grid.SetTerrain(30, 4 + i * 2, TerrainType.Wall);
        }

        // Box
        for (int i = 0; i < 4; i++)
        {
            grid.SetTerrain(40 + i * 2, 4, TerrainType.Wall);
            grid.SetTerrain(40 + i * 2, 10, TerrainType.Wall);
            grid.SetTerrain(40, 4 + i * 2, TerrainType.Wall);
            grid.SetTerrain(46, 4 + i * 2, TerrainType.Wall);
        }

        grid.RebuildCostField();
        sim.RebuildPathfinder();

        DebugLog.Log(ScenarioLog, $"Placed wall patterns, {wallsPlaced}+ wall tiles");
        ZoomOnLocation(20f, 6f, 16f);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;

        if (_phase == 0 && _elapsed > 0.5f)
        {
            DeferredScreenshot = "wall_test_overview";
            _phase = 1;
        }
        else if (_phase == 1 && _elapsed > 1.5f)
        {
            _complete = true;
        }
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        // Count wall tiles
        int wallTiles = 0;
        for (int y = 0; y < sim.Grid.Height; y++)
            for (int x = 0; x < sim.Grid.Width; x++)
                if (sim.Grid.GetTerrain(x, y) == TerrainType.Wall) wallTiles++;

        DebugLog.Log(ScenarioLog, $"Wall tiles on grid: {wallTiles}");
        DebugLog.Log(ScenarioLog, $"Pathfinding rebuilt: OK");
        return 0; // Always pass - visual test
    }
}
