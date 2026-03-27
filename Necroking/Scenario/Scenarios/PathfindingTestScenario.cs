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
    private const float TestTimeout = 30f; // seconds per test
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

        // Dense forest: randomly scattered wall tiles at ~40% density
        // No clear path — unit must weave through gaps between trees
        int ox = 10, oy = 10;
        int forestW = 40, forestH = 40;
        var rng = new Random(42); // deterministic seed for reproducibility

        for (int y = 0; y < forestH; y++)
        {
            for (int x = 0; x < forestW; x++)
            {
                // Clear start area (top-left 4x4) and end area (bottom-right 4x4)
                bool isStart = x < 4 && y < 4;
                bool isEnd = x >= forestW - 4 && y >= forestH - 4;

                if (isStart || isEnd)
                    grid.SetTerrain(ox + x, oy + y, TerrainType.Open);
                else if (rng.NextDouble() < 0.40) // 40% tree density
                    grid.SetTerrain(ox + x, oy + y, TerrainType.Wall);
                else
                    grid.SetTerrain(ox + x, oy + y, TerrainType.Open);
            }
        }

        grid.RebuildCostField();
        sim.RebuildPathfinder();

        // Spawn unit top-left, target bottom-right
        Vec2 startPos = new(ox + 2f, oy + 2f);
        Vec2 target = new(ox + forestW - 2f, oy + forestH - 2f);

        int idx = units.AddUnit(startPos, UnitType.Skeleton);
        units.AI[idx] = AIBehavior.MoveToPoint;
        units.MoveTarget[idx] = target;
        units.MaxSpeed[idx] = UnitSpeed;
        units.Stats[idx].CombatSpeed = UnitSpeed; // Simulation overwrites MaxSpeed from Stats each tick
        units.Faction[idx] = Faction.Undead;

        _testUnitIndices.Add(idx);
        _testTargets.Add(target);
        _testStartTime = _elapsed;

        ZoomOnLocation(ox + forestW / 2f, oy + forestH / 2f, 10f);
        DebugLog.Log(ScenarioLog, $"Spawned skeleton at ({startPos.X:F1},{startPos.Y:F1}), target ({target.X:F1},{target.Y:F1})");
    }

    // -------------------------------------------------------------------
    // Test 2: Cup at chunk boundary — unit must use imaginary chunk to escape
    // The cup straddles the chunk edge at x=64. The cup opening faces away
    // from the target, so the unit MUST exit through the chunk boundary.
    //
    //   Chunk 0 (x<64)  |  Chunk 1 (x>=64)
    //                    |
    //        +-----------+
    //        |  C        |   (C = unit inside cup)
    //        +-----------+
    //                    |
    //                    |        T  (target far right in chunk 1)
    //
    // The cup bottom+left+right walls are in chunk 0, but the right wall
    // extends into chunk 1. The opening is at the top (low Y).
    // Target is in chunk 1 below the cup — unit must go up, exit cup,
    // then navigate right and down to reach target.
    // -------------------------------------------------------------------
    private void SetupTest2(Simulation sim)
    {
        _testUnitIndices.Clear();
        _testTargets.Clear();
        _testNames.Add("Cup trap at chunk boundary");
        DebugLog.Log(ScenarioLog, "--- Test 2: Cup at chunk boundary ---");

        var grid = sim.Grid;
        var units = sim.UnitsMut;
        ClearAllUnits(units);

        // Clear area
        for (int y = 0; y < 100; y++)
            for (int x = 0; x < 130; x++)
                if (grid.InBounds(x, y))
                    grid.SetTerrain(x, y, TerrainType.Open);

        // Cup straddling chunk boundary at x=64
        // Cup is 12 wide (x=58..70), 8 tall (y=30..38), open at top (y=30)
        int cupLeft = 58, cupRight = 70, cupTop = 30, cupBottom = 38;

        // Bottom wall
        for (int x = cupLeft; x <= cupRight; x++)
            grid.SetTerrain(x, cupBottom, TerrainType.Wall);
        // Left wall
        for (int y = cupTop + 1; y <= cupBottom; y++)
            grid.SetTerrain(cupLeft, y, TerrainType.Wall);
        // Right wall
        for (int y = cupTop + 1; y <= cupBottom; y++)
            grid.SetTerrain(cupRight, y, TerrainType.Wall);
        // Top is OPEN — the cup opening

        grid.RebuildCostField();
        sim.RebuildPathfinder();

        // Unit inside cup (centered), target far below and to the right
        Vec2 startPos = new(64f, 35f); // inside cup, right at chunk boundary
        Vec2 target = new(90f, 50f);   // in chunk 1, below and right

        int idx = units.AddUnit(startPos, UnitType.Skeleton);
        units.AI[idx] = AIBehavior.MoveToPoint;
        units.MoveTarget[idx] = target;
        units.MaxSpeed[idx] = UnitSpeed;
        units.Stats[idx].CombatSpeed = UnitSpeed;
        units.Faction[idx] = Faction.Undead;

        _testUnitIndices.Add(idx);
        _testTargets.Add(target);
        _testStartTime = _elapsed;

        ZoomOnLocation(64f, 40f, 10f);
        DebugLog.Log(ScenarioLog, $"Cup at chunk boundary x=64, cup=({cupLeft},{cupTop})-({cupRight},{cupBottom})");
        DebugLog.Log(ScenarioLog, $"Unit at ({startPos.X:F1},{startPos.Y:F1}), target at ({target.X:F1},{target.Y:F1})");
    }

    // -------------------------------------------------------------------
    // Test 3: Multi-chunk pathfinding with mountain ranges blocking direct routes
    //
    // Layout (4 chunks wide = 256 tiles):
    //
    //   Chunk(0,0) | Chunk(1,0) | Chunk(2,0) | Chunk(3,0)
    //   -----------+------------+------------+-----------
    //   Chunk(0,1) | Chunk(1,1) | Chunk(2,1) | Chunk(3,1)
    //
    //   S = start (chunk 0,0)
    //   T = target (chunk 3,1)
    //
    //   Mountain range 1: vertical wall from chunk(1,0) down into chunk(1,1)
    //     with a gap only at the very bottom of chunk(1,1)
    //   Mountain range 2: vertical wall from chunk(2,1) up into chunk(2,0)
    //     with a gap only at the very top of chunk(2,0)
    //
    //   Required path: S → right through chunk(0,0) → down through gap at
    //   bottom of mountain 1 → right → up through gap at top of mountain 2 → T
    //
    // This tests that the pathfinder chooses the correct chunks to route through.
    // -------------------------------------------------------------------
    private void SetupTest3(Simulation sim)
    {
        _testUnitIndices.Clear();
        _testTargets.Clear();
        _testNames.Add("Multi-chunk mountain ranges");
        DebugLog.Log(ScenarioLog, "--- Test 3: Multi-chunk mountain ranges ---");

        var grid = sim.Grid;
        var units = sim.UnitsMut;
        ClearAllUnits(units);

        // Clear area (4x2 chunks = 256x128 tiles)
        for (int y = 0; y < 128; y++)
            for (int x = 0; x < 256; x++)
                if (grid.InBounds(x, y))
                    grid.SetTerrain(x, y, TerrainType.Open);

        // Mountain range 1: vertical wall at x=64 (chunk boundary 0|1)
        // Runs from y=0 down to y=120, gap at y=121..127 (bottom of chunk 1,1)
        for (int y = 0; y <= 120; y++)
            grid.SetTerrain(64, y, TerrainType.Wall);

        // Mountain range 2: vertical wall at x=192 (chunk boundary 2|3)
        // Runs from y=8 down to y=127, gap at y=0..7 (top of chunk 2,0)
        for (int y = 8; y <= 127; y++)
            grid.SetTerrain(192, y, TerrainType.Wall);

        grid.RebuildCostField();
        sim.RebuildPathfinder();

        Vec2 startPos = new(10f, 32f);   // chunk (0,0)
        Vec2 target = new(220f, 96f);    // chunk (3,1)

        int idx = units.AddUnit(startPos, UnitType.Skeleton);
        units.AI[idx] = AIBehavior.MoveToPoint;
        units.MoveTarget[idx] = target;
        units.MaxSpeed[idx] = UnitSpeed;
        units.Stats[idx].CombatSpeed = UnitSpeed;
        units.Faction[idx] = Faction.Undead;

        _testUnitIndices.Add(idx);
        _testTargets.Add(target);
        _testStartTime = _elapsed;

        ZoomOnLocation(128f, 64f, 4f);
        DebugLog.Log(ScenarioLog, $"Mountain range at x=64 (gap at bottom), x=192 (gap at top)");
        DebugLog.Log(ScenarioLog, $"Unit at ({startPos.X:F1},{startPos.Y:F1}), target at ({target.X:F1},{target.Y:F1})");
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
