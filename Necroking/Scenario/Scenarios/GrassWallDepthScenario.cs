using System;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.World;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Tests depth ordering between grass and walls.
/// Port of C++ grass_wall_depth scenario.
/// </summary>
public class GrassWallDepthScenario : ScenarioBase
{
    public override string Name => "grass_wall_depth";
    public override bool WantsGrass => true;

    private bool _complete;
    private int _frame;
    private int _step;
    private int _screenshotCount;
    private float _wallCenterX, _wallCenterY;

    private struct GWDStep
    {
        public string Name;
        public string Desc;
        public float CamX, CamY, Zoom;
    }

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, $"=== Scenario: {Name} ===");
        DebugLog.Log(ScenarioLog, "Testing depth ordering between grass and walls.");

        var ws = sim.WallSystem;
        if (ws == null) { DebugLog.Log(ScenarioLog, "FAIL: No wall system"); return; }
        if (GrassMap == null) { DebugLog.Log(ScenarioLog, "FAIL: No grass map"); return; }

        int cx = WallSystem.SnapToWallGrid(ws.Width / 2);
        int cy = WallSystem.SnapToWallGrid(ws.Height / 2);

        // Horizontal wall line
        for (int i = -3; i <= 3; i++)
        {
            int wx = cx + i * WallSystem.WallStep;
            ws.SetWall(wx, cy, 1);
            sim.Grid.SetTerrain(wx, cy, TerrainType.Wall);
        }

        _wallCenterX = cx + 1f;
        _wallCenterY = cy + 1f;

        DebugLog.Log(ScenarioLog, $"Wall center at world ({_wallCenterX:F1}, {_wallCenterY:F1}), tile ({cx}, {cy})");

        float cellSize = 0.8f;
        int gcx = (int)(_wallCenterX / cellSize);
        int gcy = (int)(_wallCenterY / cellSize);

        DebugLog.Log(ScenarioLog, $"Grass cell size={cellSize}, grid center=({gcx}, {gcy})");

        // LEFT: grass BEHIND wall only
        for (int dy = gcy - 6; dy <= gcy - 1; dy++)
            for (int dx = gcx - 12; dx <= gcx - 4; dx++)
                SetGrassType(dx, dy, 0);
        DebugLog.Log(ScenarioLog, "LEFT: grass BEHIND only");

        // RIGHT: grass IN FRONT of wall only
        for (int dy = gcy + 1; dy <= gcy + 6; dy++)
            for (int dx = gcx + 4; dx <= gcx + 12; dx++)
                SetGrassType(dx, dy, 0);
        DebugLog.Log(ScenarioLog, "RIGHT: grass IN FRONT only");

        // CENTER: grass BOTH SIDES
        for (int dy = gcy - 6; dy <= gcy + 6; dy++)
            for (int dx = gcx - 3; dx <= gcx + 3; dx++)
                SetGrassType(dx, dy, 0);
        DebugLog.Log(ScenarioLog, "CENTER: grass BOTH SIDES");

        ZoomOnLocation(_wallCenterX, _wallCenterY, 40f);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        if (_complete) return;
        if (DeferredScreenshot != null) return;

        float cellSize = 0.8f;
        float leftX = _wallCenterX - 8f * cellSize;
        float rightX = _wallCenterX + 8f * cellSize;

        GWDStep[] steps = {
            new() { Name = "gwd_overview", Desc = "Overview: left=behind, center=both, right=infront",
                    CamX = _wallCenterX, CamY = _wallCenterY, Zoom = 25f },
            new() { Name = "gwd_behind", Desc = "LEFT: Grass BEHIND wall only",
                    CamX = leftX, CamY = _wallCenterY, Zoom = 80f },
            new() { Name = "gwd_infront", Desc = "RIGHT: Grass IN FRONT of wall only",
                    CamX = rightX, CamY = _wallCenterY, Zoom = 80f },
            new() { Name = "gwd_both", Desc = "CENTER: Grass on both sides of wall",
                    CamX = _wallCenterX, CamY = _wallCenterY, Zoom = 80f },
        };

        int frameInStep = _frame % 3;

        if (_step < steps.Length)
        {
            if (frameInStep == 0)
            {
                ZoomOnLocation(steps[_step].CamX, steps[_step].CamY, steps[_step].Zoom);
                DebugLog.Log(ScenarioLog, $"Step {_step}: {steps[_step].Desc}");
            }
            else if (frameInStep == 2)
            {
                DeferredScreenshot = steps[_step].Name;
                _screenshotCount++;
                _step++;
            }
        }

        if (_step >= steps.Length) _complete = true;
        _frame++;
    }

    public override bool IsComplete => _complete && DeferredScreenshot == null;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, $"=== Grass wall depth complete: {_screenshotCount} screenshots ===");
        return 0;
    }
}
