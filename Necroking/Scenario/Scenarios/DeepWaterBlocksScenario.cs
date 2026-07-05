using System;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Movement;
using Necroking.World;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Verifies DeepWater tiles block unit movement (pathfinder sees them as
/// impassable, wall-collision system clips against them).
///
/// Setup: 32×32 grid, deep-water bar painted X=14..17 spanning full height
/// — no way around. Spawn a Skeleton at (5, 16) with MoveToPoint AI heading
/// to (27, 16) on the far side. Run 6s.
///
/// Pass: unit moves east, gets blocked at the bar (final X < 14), no oscillation
/// across the bar.
/// Fail: unit's X ever reaches 14 or beyond — it crossed into deep water.
/// </summary>
public class DeepWaterBlocksScenario : ScenarioBase
{
    public override string Name => "deep_water_blocks";
    public override bool WantsGround => true;
    public override int GridSize => 32;

    private uint _unitId;
    private float _startX = 5.5f;
    private float _maxX;
    private float _elapsed;
    private bool _complete;

    private const float DurationSec = 6f;
    private const int WaterBarX0 = 14;
    private const int WaterBarX1 = 17;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Deep Water Blocks Movement Scenario ===");
        if (GroundSystem == null) throw new InvalidOperationException("Requires WantsGround");

        int grassIdx = GroundSystem.FindType("grass");
        int deepIdx  = GroundSystem.FindType("deep_water");
        DebugLog.Log(ScenarioLog, $"Ground type indices: grass={grassIdx} deep={deepIdx}");
        if (deepIdx < 0)
        {
            DebugLog.Log(ScenarioLog, "FAIL: deep_water type not registered");
            _complete = true;
            return;
        }

        // Paint vertical deep-water bar from x=WaterBarX0 to x=WaterBarX1, full height.
        // The +1 in the upper bound ensures every tile in [X0,X1] has all 4 corner
        // vertices set to deep_water (worst-of-4 → DeepWater terrain → cost 255).
        int vw = GroundSystem.VertexW;
        int vh = GroundSystem.VertexH;
        for (int vy = 0; vy < vh; vy++)
            for (int vx = WaterBarX0; vx <= WaterBarX1 + 1 && vx < vw; vx++)
                GroundSystem.SetVertex(vx, vy, (byte)deepIdx);

        // Spawn unit on west side with MoveToPoint AI heading east.
        var target = new Vec2(27f, 16f);
        var startPos = new Vec2(_startX, 16f);
        int idx = sim.UnitsMut.AddUnit(startPos, UnitType.Skeleton);
        sim.UnitsMut[idx].AI = AIBehavior.MoveToPoint;
        sim.UnitsMut[idx].MoveTarget = target;
        sim.UnitsMut[idx].Stats.CombatSpeed = 5f; // MaxSpeed derives from this via Locomotion.UpdateSpeeds
        _unitId = sim.UnitsMut[idx].Id;
        _maxX = _startX;

        ZoomOnLocation(16f, 16f, 28f);
        DebugLog.Log(ScenarioLog, $"Spawned skeleton id={_unitId} at ({_startX:F1},16) heading to ({target.X:F1},16). Bar X={WaterBarX0}..{WaterBarX1}.");
    }

    private static int FindByID(UnitArrays units, uint id)
    {
        for (int i = 0; i < units.Count; i++)
            if (units[i].Id == id) return i;
        return -1;
    }

    public override void OnTick(Simulation sim, float dt)
    {
        if (_complete) return;
        _elapsed += dt;

        int idx = FindByID(sim.UnitsMut, _unitId);
        if (idx >= 0)
        {
            float x = sim.Units[idx].Position.X;
            if (x > _maxX) _maxX = x;
        }

        if (_elapsed >= DurationSec)
            _complete = true;
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        int idx = FindByID(sim.UnitsMut, _unitId);
        var finalPos = idx >= 0 ? sim.Units[idx].Position : new Vec2(-1f, -1f);

        byte costWest    = sim.Grid.GetCost(WaterBarX0 - 2, 16);
        byte costBarLo   = sim.Grid.GetCost(WaterBarX0,     16);
        byte costBarMid  = sim.Grid.GetCost(WaterBarX0 + 1, 16);
        byte costEast    = sim.Grid.GetCost(WaterBarX1 + 2, 16);
        var terrainWest    = sim.Grid.GetTerrain(WaterBarX0 - 2, 16);
        var terrainBarLo   = sim.Grid.GetTerrain(WaterBarX0,     16);
        var terrainBarMid  = sim.Grid.GetTerrain(WaterBarX0 + 1, 16);
        var terrainEast    = sim.Grid.GetTerrain(WaterBarX1 + 2, 16);

        DebugLog.Log(ScenarioLog, "=== Result ===");
        DebugLog.Log(ScenarioLog, $"Grid terrain at y=16: x={WaterBarX0 - 2} {terrainWest}(cost={costWest})  x={WaterBarX0} {terrainBarLo}(cost={costBarLo})  x={WaterBarX0 + 1} {terrainBarMid}(cost={costBarMid})  x={WaterBarX1 + 2} {terrainEast}(cost={costEast})");
        DebugLog.Log(ScenarioLog, $"Unit start: ({_startX},16) final: ({finalPos.X:F2},{finalPos.Y:F2}) maxX={_maxX:F2}");

        // Validation:
        // 1. Cost field must show DeepWater tiles as 255.
        // 2. Unit must have moved east meaningfully (>= startX + 3).
        // 3. Unit's maxX must NOT have reached the bar (< WaterBarX0).
        if (costBarMid != 255)
        {
            DebugLog.Log(ScenarioLog, $"FAIL — deep water tile at ({WaterBarX0 + 1},16) has cost {costBarMid}, expected 255 (StampTerrainOnto didn't run or didn't see deep_water)");
            return 10;
        }
        if (_maxX < _startX + 3f)
        {
            DebugLog.Log(ScenarioLog, $"FAIL — unit didn't move east enough (maxX={_maxX:F2}, expected at least {_startX + 3f})");
            return 11;
        }
        if (_maxX >= WaterBarX0)
        {
            DebugLog.Log(ScenarioLog, $"FAIL — unit entered deep-water bar (maxX={_maxX:F2} >= barX0={WaterBarX0})");
            return 12;
        }

        DebugLog.Log(ScenarioLog, $"PASS — unit advanced to maxX={_maxX:F2} and was blocked by deep water at X={WaterBarX0}");
        return 0;
    }
}
