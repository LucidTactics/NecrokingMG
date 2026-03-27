using System;
using System.Collections.Generic;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Movement;
using Necroking.World;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Tests pathfinding: dense trees with cups, U-shaped traps, multi-chunk corridors.
/// Verifies units reach targets within timeout using imaginary chunks, wall sliding,
/// escape propagation, border distance weighting, and stuck escape.
/// </summary>
public class PathfindingTestScenario : ScenarioBase
{
    public override string Name => "pathfinding_test";
    public override int GridSize => 256; // Test 3 needs coordinates up to ~200

    private float _elapsed;
    private int _phase;
    private bool _complete;
    private int _testNum;

    // Test tracking
    private readonly List<int> _testUnitIndices = new();
    private readonly List<Vec2> _testTargets = new();
    private readonly List<bool> _testPassed = new();
    private readonly List<string> _testNames = new();
    private float _testStartTime;
    private const float TestTimeout = 15f; // seconds per test
    private const float TargetReachDist = 2f;
    private const float UnitSpeed = 20f;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Pathfinding Test Scenario ===");
        _testNum = 0;
        _phase = 0;
        SetupTest1(sim);
    }

    // -------------------------------------------------------------------
    // Test 1: Dense trees with cup-shaped gaps
    // -------------------------------------------------------------------
    private void SetupTest1(Simulation sim)
    {
        _testUnitIndices.Clear();
        _testTargets.Clear();
        _testNames.Add("Dense trees with cups");
        DebugLog.Log(ScenarioLog, "--- Test 1: Dense trees with cups ---");

        var grid = sim.Grid;
        var units = sim.UnitsMut;

        // Clear previous units
        ClearAllUnits(units);

        // Place dense tree grid with deliberate cup-shaped gaps
        // Fill a 30x30 area with trees, leaving a winding path
        int ox = 10, oy = 10; // offset
        for (int y = 0; y < 30; y++)
        {
            for (int x = 0; x < 30; x++)
            {
                // Default: wall
                bool wall = true;

                // Create a winding path: horizontal corridors connected by vertical segments
                // Corridor 1: y=2..4, x=0..25
                if (y >= 2 && y <= 4 && x >= 0 && x <= 25) wall = false;
                // Vertical connector 1: y=4..10, x=24..26
                if (y >= 4 && y <= 10 && x >= 24 && x <= 26) wall = false;
                // Corridor 2: y=8..10, x=4..26
                if (y >= 8 && y <= 10 && x >= 4 && x <= 26) wall = false;
                // Vertical connector 2: y=10..16, x=4..6
                if (y >= 10 && y <= 16 && x >= 4 && x <= 6) wall = false;
                // Corridor 3: y=14..16, x=4..25
                if (y >= 14 && y <= 16 && x >= 4 && x <= 25) wall = false;
                // Vertical connector 3: y=16..22, x=24..26
                if (y >= 16 && y <= 22 && x >= 24 && x <= 26) wall = false;
                // Corridor 4: y=20..22, x=4..26
                if (y >= 20 && y <= 22 && x >= 4 && x <= 26) wall = false;
                // Vertical connector 4: y=22..28, x=4..6
                if (y >= 22 && y <= 28 && x >= 4 && x <= 6) wall = false;
                // Corridor 5: y=26..28, x=4..28
                if (y >= 26 && y <= 28 && x >= 4 && x <= 28) wall = false;

                // Start area: y=0..4, x=0..4
                if (y >= 0 && y <= 4 && x >= 0 && x <= 4) wall = false;
                // End area: y=26..29, x=26..29
                if (y >= 26 && y <= 29 && x >= 26 && x <= 29) wall = false;

                if (wall)
                    grid.SetTerrain(ox + x, oy + y, TerrainType.Wall);
                else
                    grid.SetTerrain(ox + x, oy + y, TerrainType.Open);
            }
        }

        grid.RebuildCostField();
        sim.RebuildPathfinder();

        // Spawn unit at start
        Vec2 startPos = new(ox + 2f, oy + 3f);
        Vec2 target = new(ox + 28f, oy + 28f);

        int idx = units.AddUnit(startPos, UnitType.Skeleton);
        units.AI[idx] = AIBehavior.MoveToPoint;
        units.MoveTarget[idx] = target;
        units.MaxSpeed[idx] = UnitSpeed;
        units.Stats[idx].CombatSpeed = UnitSpeed; // Simulation overwrites MaxSpeed from Stats each tick
        units.Faction[idx] = Faction.Undead;

        _testUnitIndices.Add(idx);
        _testTargets.Add(target);
        _testStartTime = _elapsed;

        ZoomOnLocation(ox + 15f, oy + 15f, 16f);
        DebugLog.Log(ScenarioLog, $"Spawned skeleton at ({startPos.X:F1},{startPos.Y:F1}), target ({target.X:F1},{target.Y:F1})");
    }

    // -------------------------------------------------------------------
    // Test 2: U-shaped wall trap requiring chunk exit
    // -------------------------------------------------------------------
    private void SetupTest2(Simulation sim)
    {
        _testUnitIndices.Clear();
        _testTargets.Clear();
        _testNames.Add("Cup trap requiring chunk exit");
        DebugLog.Log(ScenarioLog, "--- Test 2: Cup trap requiring chunk exit ---");

        var grid = sim.Grid;
        var units = sim.UnitsMut;
        ClearAllUnits(units);

        // Clear test 1 terrain
        for (int y = 0; y < 50; y++)
            for (int x = 0; x < 50; x++)
                grid.SetTerrain(x, y, TerrainType.Open);

        // Build U-shaped wall within a single chunk
        int cx = 20, cy = 20;

        // Bottom wall: horizontal
        for (int x = cx - 5; x <= cx + 5; x++)
            grid.SetTerrain(x, cy + 5, TerrainType.Wall);
        // Left wall: vertical
        for (int y = cy - 2; y <= cy + 5; y++)
            grid.SetTerrain(cx - 5, y, TerrainType.Wall);
        // Right wall: vertical
        for (int y = cy - 2; y <= cy + 5; y++)
            grid.SetTerrain(cx + 5, y, TerrainType.Wall);
        // The top is open (the cup opening)

        grid.RebuildCostField();
        sim.RebuildPathfinder();

        // Spawn unit inside the U, target outside at the bottom
        Vec2 startPos = new(cx, cy + 2);
        Vec2 target = new(cx, cy + 12);

        int idx = units.AddUnit(startPos, UnitType.Skeleton);
        units.AI[idx] = AIBehavior.MoveToPoint;
        units.MoveTarget[idx] = target;
        units.MaxSpeed[idx] = UnitSpeed;
        units.Stats[idx].CombatSpeed = UnitSpeed;
        units.Faction[idx] = Faction.Undead;

        _testUnitIndices.Add(idx);
        _testTargets.Add(target);
        _testStartTime = _elapsed;

        ZoomOnLocation(cx, cy + 5, 16f);
        DebugLog.Log(ScenarioLog, $"U-wall at ({cx},{cy}), unit inside at ({startPos.X:F1},{startPos.Y:F1}), target at ({target.X:F1},{target.Y:F1})");
    }

    // -------------------------------------------------------------------
    // Test 3: Multi-chunk winding corridor
    // -------------------------------------------------------------------
    private void SetupTest3(Simulation sim)
    {
        _testUnitIndices.Clear();
        _testTargets.Clear();
        _testNames.Add("Multi-chunk corridor");
        DebugLog.Log(ScenarioLog, "--- Test 3: Multi-chunk corridor ---");

        var grid = sim.Grid;
        var units = sim.UnitsMut;
        ClearAllUnits(units);

        // Clear terrain
        for (int y = 0; y < 200; y++)
            for (int x = 0; x < 200; x++)
                if (grid.InBounds(x, y))
                    grid.SetTerrain(x, y, TerrainType.Open);

        // Build walls creating a winding corridor spanning 3+ chunks (64 tiles each)
        // Wall top and bottom with turns

        // Outer walls
        for (int x = 5; x < 195; x++)
        {
            grid.SetTerrain(x, 5, TerrainType.Wall);
            grid.SetTerrain(x, 15, TerrainType.Wall);
        }

        // Create horizontal corridor at y=6..14 with vertical walls creating turns
        // Block 1: wall at x=60, y=6..12 (force turn through gap at y=13..14)
        for (int y = 6; y <= 12; y++)
            grid.SetTerrain(60, y, TerrainType.Wall);

        // Block 2: wall at x=120, y=8..14 (force turn through gap at y=6..7)
        for (int y = 8; y <= 14; y++)
            grid.SetTerrain(120, y, TerrainType.Wall);

        // Block 3: wall at x=180, y=6..12
        for (int y = 6; y <= 12; y++)
            grid.SetTerrain(180, y, TerrainType.Wall);

        grid.RebuildCostField();
        sim.RebuildPathfinder();

        Vec2 startPos = new(8f, 10f);
        Vec2 target = new(190f, 10f);

        int idx = units.AddUnit(startPos, UnitType.Skeleton);
        units.AI[idx] = AIBehavior.MoveToPoint;
        units.MoveTarget[idx] = target;
        units.MaxSpeed[idx] = UnitSpeed;
        units.Stats[idx].CombatSpeed = UnitSpeed;
        units.Faction[idx] = Faction.Undead;

        _testUnitIndices.Add(idx);
        _testTargets.Add(target);
        _testStartTime = _elapsed;

        ZoomOnLocation(100f, 10f, 4f);
        DebugLog.Log(ScenarioLog, $"Corridor from ({startPos.X:F1},{startPos.Y:F1}) to ({target.X:F1},{target.Y:F1}), 3 turns across 3+ chunks");
    }

    // -------------------------------------------------------------------

    private void ClearAllUnits(UnitArrays units)
    {
        // Use Clear() to fully reset the unit arrays instead of just marking dead.
        // Marking Alive[i]=false causes RemoveDeadUnits() to swap-remove on the next
        // simulation tick, which invalidates the indices stored in _testUnitIndices.
        units.Clear();
    }

    public override void OnTick(Simulation sim, float dt)
    {
        if (_complete) return;
        _elapsed += dt;

        float testElapsed = _elapsed - _testStartTime;

        // Check if current test units reached their targets
        bool allReached = true;
        for (int t = 0; t < _testUnitIndices.Count; t++)
        {
            int ui = _testUnitIndices[t];
            if (ui >= sim.Units.Count || !sim.Units.Alive[ui])
            {
                allReached = false;
                continue;
            }
            float dist = (sim.Units.Position[ui] - _testTargets[t]).Length();
            if (dist > TargetReachDist)
                allReached = false;
        }

        // Log position periodically (every ~0.5s of scenario time, roughly every 30 ticks at 60fps)
        if (_phase % 30 == 0 && _testUnitIndices.Count > 0)
        {
            int ui = _testUnitIndices[0];
            if (ui < sim.Units.Count && sim.Units.Alive[ui])
            {
                var pos = sim.Units.Position[ui];
                var vel = sim.Units.Velocity[ui];
                var prefVel = sim.Units.PreferredVel[ui];
                DebugLog.Log(ScenarioLog, $"  t={testElapsed:F1}s pos=({pos.X:F1},{pos.Y:F1}) vel=({vel.X:F1},{vel.Y:F1}) pref=({prefVel.X:F1},{prefVel.Y:F1}) dist={(_testTargets[0] - pos).Length():F1}");
            }
            else
            {
                DebugLog.Log(ScenarioLog, $"  t={testElapsed:F1}s unit idx={ui} INVALID (count={sim.Units.Count})");
            }
        }
        _phase++;

        // Take screenshot in mid-navigation
        if (testElapsed > 3f && testElapsed < 3.1f && DeferredScreenshot == null)
        {
            DeferredScreenshot = $"pathfinding_test{_testNum + 1}_nav";
        }

        if (allReached)
        {
            DebugLog.Log(ScenarioLog, $"Test {_testNum + 1} PASSED: all units reached target in {testElapsed:F1}s");
            _testPassed.Add(true);
            DeferredScreenshot = $"pathfinding_test{_testNum + 1}_done";
            AdvanceTest(sim);
        }
        else if (testElapsed > TestTimeout)
        {
            // Log final positions
            for (int t = 0; t < _testUnitIndices.Count; t++)
            {
                int ui = _testUnitIndices[t];
                if (ui < sim.Units.Count && sim.Units.Alive[ui])
                {
                    var pos = sim.Units.Position[ui];
                    float dist = (pos - _testTargets[t]).Length();
                    DebugLog.Log(ScenarioLog, $"  Unit {t}: pos=({pos.X:F1},{pos.Y:F1}) dist={dist:F1}");
                }
            }
            DebugLog.Log(ScenarioLog, $"Test {_testNum + 1} FAILED: timeout after {TestTimeout:F0}s");
            _testPassed.Add(false);
            DeferredScreenshot = $"pathfinding_test{_testNum + 1}_timeout";
            AdvanceTest(sim);
        }
    }

    private void AdvanceTest(Simulation sim)
    {
        _testNum++;
        _phase = 0;

        switch (_testNum)
        {
            case 1: SetupTest2(sim); break;
            case 2: SetupTest3(sim); break;
            default: _complete = true; break;
        }
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "=== Pathfinding Test Results ===");
        int passed = 0, total = _testPassed.Count;
        for (int i = 0; i < total; i++)
        {
            string result = _testPassed[i] ? "PASS" : "FAIL";
            string name = i < _testNames.Count ? _testNames[i] : $"Test {i + 1}";
            DebugLog.Log(ScenarioLog, $"  {name}: {result}");
            if (_testPassed[i]) passed++;
        }

        bool allPass = passed == total;
        DebugLog.Log(ScenarioLog, $"Overall: {passed}/{total} passed -> {(allPass ? "PASS" : "FAIL")}");
        return allPass ? 0 : 1;
    }
}
