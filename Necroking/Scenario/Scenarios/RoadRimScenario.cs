using System;
using Necroking.Core;
using Necroking.GameSystems;
using Necroking.World;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Visual test for road rim rendering.
/// Creates roads with different edge softness and rim settings, takes screenshots.
/// Port of C++ road_rim scenario.
/// </summary>
public class RoadRimScenario : ScenarioBase
{
    public override string Name => "road_rim";
    public override bool WantsGround => true;

    private bool _complete;
    private float _elapsed;
    private int _screenshotPhase;

    private const float RoadCX = 32f;
    private const float RoadCY = 32f;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, $"=== Scenario: {Name} ===");

        if (RoadSystem == null)
        {
            DebugLog.Log(ScenarioLog, "ERROR: roadSystem is null");
            _complete = true;
            return;
        }

        // Clear existing roads
        while (RoadSystem.RoadCount > 0) RoadSystem.RemoveRoad(0);

        // Road 1: With rim, high road edge softness (top)
        {
            int ri = RoadSystem.AddRoad();
            var road = RoadSystem.GetRoad(ri);
            road.Name = "High road soft (0.2)";
            road.TextureDefIndex = 0;
            road.EdgeSoftness = 0.2f;
            road.TextureScale = 0.58f;
            road.RimTextureDefIndex = -1; // no rim texture loaded, just color
            road.RimWidth = 1.2f;
            road.RimEdgeSoftness = 0.2f;
            road.Points.Add(new RoadControlPoint { Position = new Vec2(RoadCX - 12, RoadCY - 6), Width = 4f });
            road.Points.Add(new RoadControlPoint { Position = new Vec2(RoadCX + 12, RoadCY - 6), Width = 4f });
        }

        // Road 2: Zero road edge softness (middle)
        {
            int ri = RoadSystem.AddRoad();
            var road = RoadSystem.GetRoad(ri);
            road.Name = "Zero road soft";
            road.TextureDefIndex = 0;
            road.EdgeSoftness = 0f;
            road.TextureScale = 0.58f;
            road.RimTextureDefIndex = -1;
            road.RimWidth = 1.2f;
            road.RimEdgeSoftness = 0.2f;
            road.Points.Add(new RoadControlPoint { Position = new Vec2(RoadCX - 12, RoadCY), Width = 4f });
            road.Points.Add(new RoadControlPoint { Position = new Vec2(RoadCX + 12, RoadCY), Width = 4f });
        }

        // Road 3: All hard edges (bottom)
        {
            int ri = RoadSystem.AddRoad();
            var road = RoadSystem.GetRoad(ri);
            road.Name = "All hard edges";
            road.TextureDefIndex = 0;
            road.EdgeSoftness = 0f;
            road.TextureScale = 0.58f;
            road.RimTextureDefIndex = -1;
            road.RimWidth = 1.2f;
            road.RimEdgeSoftness = 0f;
            road.Points.Add(new RoadControlPoint { Position = new Vec2(RoadCX - 12, RoadCY + 6), Width = 4f });
            road.Points.Add(new RoadControlPoint { Position = new Vec2(RoadCX + 12, RoadCY + 6), Width = 4f });
        }

        DebugLog.Log(ScenarioLog, $"Created 3 test roads at y={RoadCY - 6}, {RoadCY}, {RoadCY + 6}");
        ZoomOnLocation(RoadCX, RoadCY, 40f);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        if (_complete) return;
        if (DeferredScreenshot != null) return;
        _elapsed += dt;

        if (_elapsed < 0.5f) return; // let rendering settle

        if (_screenshotPhase == 0)
        {
            ZoomOnLocation(RoadCX, RoadCY, 30f);
            DeferredScreenshot = "road_rim_overview";
            _screenshotPhase = 1;
        }
        else if (_screenshotPhase == 1 && _elapsed > 1f)
        {
            ZoomOnLocation(RoadCX, RoadCY - 6, 128f);
            DeferredScreenshot = "road_rim_high_soft";
            _screenshotPhase = 2;
        }
        else if (_screenshotPhase == 2 && _elapsed > 1.5f)
        {
            ZoomOnLocation(RoadCX, RoadCY, 128f);
            DeferredScreenshot = "road_rim_zero_roadsoft";
            _screenshotPhase = 3;
        }
        else if (_screenshotPhase == 3 && _elapsed > 2f)
        {
            ZoomOnLocation(RoadCX, RoadCY + 6, 128f);
            DeferredScreenshot = "road_rim_all_hard";
            _screenshotPhase = 4;
        }
        else if (_screenshotPhase == 4 && _elapsed > 2.5f)
        {
            _complete = true;
        }
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "Road rim scenario complete. Check screenshots.");
        return 0;
    }
}
