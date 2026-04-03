using System;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.World;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Tests shadow consistency across walls and units.
/// Port of C++ shadow_test scenario.
/// </summary>
public class ShadowTestScenario : ScenarioBase
{
    public override string Name => "shadow_test";

    private bool _complete;
    private int _frame;
    private int _step;
    private int _screenshotCount;
    private float _centerX, _centerY;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, $"=== Scenario: {Name} ===");
        DebugLog.Log(ScenarioLog, "Testing shadow consistency across walls and units.");

        var ws = sim.WallSystem;
        if (ws == null) { DebugLog.Log(ScenarioLog, "FAIL: No wall system"); return; }

        int cx = WallSystem.SnapToWallGrid(ws.Width / 2);
        int cy = WallSystem.SnapToWallGrid(ws.Height / 2);
        int step = WallSystem.WallStep;
        float halfStep = step * 0.5f;

        _centerX = cx + halfStep;
        _centerY = cy + halfStep;

        // L-shaped wall
        for (int i = -2; i <= 2; i++)
        {
            int wx = cx + i * step;
            ws.SetWall(wx, cy, 1);
            sim.Grid.SetTerrain(wx, cy, TerrainType.Wall);
        }
        for (int i = 1; i <= 3; i++)
        {
            int wy = cy + i * step;
            ws.SetWall(cx + 2 * step, wy, 1);
            sim.Grid.SetTerrain(cx + 2 * step, wy, TerrainType.Wall);
        }
        DebugLog.Log(ScenarioLog, $"Placed L-shaped wall at center ({cx}, {cy})");

        // Skeletons to the left
        float unitY = _centerY + 2f;
        for (int i = 0; i < 4; i++)
        {
            var pos = new Vec2(_centerX - 8f - i * 2f, unitY + i * 1.5f);
            sim.UnitsMut.AddUnit(pos, UnitType.Skeleton);
        }

        // Soldiers to the right
        float wallRight = (cx + 2 * step) + halfStep + 6f;
        for (int i = 0; i < 3; i++)
        {
            var pos = new Vec2(wallRight + i * 2.5f, _centerY + 4f + i * 2f);
            sim.UnitsMut.AddUnit(pos, UnitType.Soldier);
        }

        // Knight near wall corner
        sim.UnitsMut.AddUnit(new Vec2(_centerX + 2f * step + halfStep + 3f, _centerY + 3f * step), UnitType.Knight);

        DebugLog.Log(ScenarioLog, "Spawned units for shadow comparison");

        // Environment objects — tree and building for shadow pivot testing
        var env = sim.EnvironmentSystem;
        if (env != null)
        {
            var treeDef = new EnvironmentObjectDef
            {
                Id = "shadow_tree", Name = "Shadow Tree",
                TexturePath = GamePaths.Resolve("assets/Environment/Trees/BranchlessTree1.png"),
                SpriteWorldHeight = 6f,
                PivotX = 0.5f, PivotY = 1f,
                Scale = 1f
            };
            int treeDefIdx = env.AddDef(treeDef);
            env.AddObject((ushort)treeDefIdx, _centerX - 4f, _centerY + 6f);
            DebugLog.Log(ScenarioLog, "Placed tree for shadow test");

            var buildingDef = new EnvironmentObjectDef
            {
                Id = "shadow_house", Name = "Shadow House",
                TexturePath = GamePaths.Resolve("assets/Environment/Buildings/House1.png"),
                SpriteWorldHeight = 8f,
                PivotX = 0.5f, PivotY = 1f,
                IsBuilding = true, Scale = 1f
            };
            int houseDefIdx = env.AddDef(buildingDef);
            env.AddObject((ushort)houseDefIdx, _centerX + 6f, _centerY + 6f);
            DebugLog.Log(ScenarioLog, "Placed building for shadow test");
        }

        ZoomOnLocation(_centerX + 2f, _centerY + 4f, 40f);
    }

    private struct ShadowStep
    {
        public string Name;
        public string Desc;
        public float CamX, CamY, Zoom;
    }

    public override void OnTick(Simulation sim, float dt)
    {
        if (_complete) return;
        if (DeferredScreenshot != null) return;

        int step = WallSystem.WallStep;

        ShadowStep[] steps = {
            new() { Name = "shadow_overview", Desc = "Overview: walls + units with shadows",
                    CamX = _centerX + 2f, CamY = _centerY + 4f, Zoom = 32f },
            new() { Name = "shadow_wall_close", Desc = "Close-up: wall shadows",
                    CamX = _centerX, CamY = _centerY, Zoom = 60f },
            new() { Name = "shadow_units_close", Desc = "Close-up: unit shadows near wall",
                    CamX = _centerX - 6f, CamY = _centerY + 3f, Zoom = 60f },
            new() { Name = "shadow_corner", Desc = "Wall corner with knight",
                    CamX = _centerX + 2f * step + 2f, CamY = _centerY + 3f * step, Zoom = 50f },
            new() { Name = "shadow_envobj", Desc = "Env objects: tree and building shadows",
                    CamX = _centerX + 1f, CamY = _centerY + 6f, Zoom = 40f },
            new() { Name = "shadow_tree_close", Desc = "Close-up: tree shadow pivot",
                    CamX = _centerX - 4f, CamY = _centerY + 6f, Zoom = 80f },
            new() { Name = "shadow_house_close", Desc = "Close-up: building shadow pivot",
                    CamX = _centerX + 6f, CamY = _centerY + 6f, Zoom = 60f },
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
        DebugLog.Log(ScenarioLog, $"=== Shadow test complete: {_screenshotCount} screenshots ===");
        return 0;
    }
}
