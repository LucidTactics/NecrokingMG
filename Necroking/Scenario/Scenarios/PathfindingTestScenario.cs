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

        // Dense forest with unit INSIDE it. ~25% density so navigable but challenging.
        // Surrounded by solid wall border so unit can't just walk around.
        int ox = 10, oy = 10;
        int forestW = 40, forestH = 40;
        var rng = new Random(42); // deterministic seed

        // First: solid wall border enclosing the entire forest
        for (int x = ox - 1; x <= ox + forestW; x++)
        {
            grid.SetTerrain(x, oy - 1, TerrainType.Wall);
            grid.SetTerrain(x, oy + forestH, TerrainType.Wall);
        }
        for (int y = oy - 1; y <= oy + forestH; y++)
        {
            grid.SetTerrain(ox - 1, y, TerrainType.Wall);
            grid.SetTerrain(ox + forestW, y, TerrainType.Wall);
        }

        // Interior: scattered trees at 25% density
        for (int y = 0; y < forestH; y++)
        {
            for (int x = 0; x < forestW; x++)
            {
                // Clear start area (top-left 3x3) and end area (bottom-right 3x3)
                bool isStart = x < 3 && y < 3;
                bool isEnd = x >= forestW - 3 && y >= forestH - 3;

                if (isStart || isEnd)
                    grid.SetTerrain(ox + x, oy + y, TerrainType.Open);
                else if (rng.NextDouble() < 0.25) // 25% — navigable but tricky
                    grid.SetTerrain(ox + x, oy + y, TerrainType.Wall);
                else
                    grid.SetTerrain(ox + x, oy + y, TerrainType.Open);
            }
        }

        grid.RebuildCostField();
        sim.RebuildPathfinder();

        // Unit starts inside top-left corner, target inside bottom-right corner
        Vec2 startPos = new(ox + 1.5f, oy + 1.5f);
        Vec2 target = new(ox + forestW - 1.5f, oy + forestH - 1.5f);

        int idx = units.AddUnit(startPos, UnitType.Skeleton);
        units.AI[idx] = AIBehavior.MoveToPoint;
        units.MoveTarget[idx] = target;
        units.MaxSpeed[idx] = UnitSpeed;
        units.Stats[idx].CombatSpeed = UnitSpeed;
        units.Faction[idx] = Faction.Undead;

        _testUnitIndices.Add(idx);
        _testTargets.Add(target);
        _testStartTime = _elapsed;

        ZoomOnLocation(ox + forestW / 2f, oy + forestH / 2f, 10f);
        DebugLog.Log(ScenarioLog, $"Dense forest {forestW}x{forestH} enclosed by walls, 25% interior density");
        DebugLog.Log(ScenarioLog, $"Unit at ({startPos.X:F1},{startPos.Y:F1}), target ({target.X:F1},{target.Y:F1})");
    }

    // -------------------------------------------------------------------
    // Test 2: Cup in adjacent chunk — unit needs imaginary chunk to escape
    //
    // Chunk boundary is at x=64.
    // The unit is in chunk 0 (x<64), placed at x=63 (just inside chunk 0).
    // The cup walls are ALL in chunk 1 (x>=64):
    //
    //   Chunk 0        |  Chunk 1
    //     (unit's      |
    //      sector)     |
    //                  |  +------+
    //              C . |  |      |    (cup walls in chunk 1)
    //                  |  +------+
    //                  |
    //         T        |              (target is in chunk 0, south)
    //
    // The unit is at x=63, y=32 (chunk 0).
    // Cup walls: right wall at x=65, top at y=30, bottom at y=34 (all in chunk 1).
    // Left side of cup is the chunk boundary itself (x=64) which isn't a wall.
    // The cup opens to the RIGHT (east, deeper into chunk 1).
    //
    // From chunk 0's flow field, there are no obstacles — it doesn't see chunk 1's
    // walls. So the flow field says "go south toward target." But physically the
    // unit can't go south because the cup's bottom wall at y=34 (in chunk 1) blocks
    // it when it steps across the boundary.
    //
    // The unit needs an imaginary chunk to see both sectors and find the exit
    // (go right through the cup opening, then south).
    // -------------------------------------------------------------------
    private void SetupTest2(Simulation sim)
    {
        _testUnitIndices.Clear();
        _testTargets.Clear();
        _testNames.Add("Cup in adjacent chunk");
        DebugLog.Log(ScenarioLog, "--- Test 2: Cup in adjacent chunk ---");

        var grid = sim.Grid;
        var units = sim.UnitsMut;
        ClearAllUnits(units);

        // Clear area
        for (int y = 0; y < 100; y++)
            for (int x = 50; x < 100; x++)
                if (grid.InBounds(x, y))
                    grid.SetTerrain(x, y, TerrainType.Open);

        // Cup walls entirely in chunk 1 (x >= 64)
        // Creates a pocket that traps the unit when it's near x=63
        // Cup: top wall y=29, bottom wall y=35, right wall x=70
        // Left side is open (the chunk boundary at x=64)
        // Cup opening is to the right (x=71+)

        // Top wall (y=29, x=64..70)
        for (int x = 64; x <= 70; x++)
            grid.SetTerrain(x, 29, TerrainType.Wall);
        // Bottom wall (y=35, x=64..70)
        for (int x = 64; x <= 70; x++)
            grid.SetTerrain(x, 35, TerrainType.Wall);
        // Right wall (x=70, y=30..34) — closes the cup on the right
        for (int y = 30; y <= 34; y++)
            grid.SetTerrain(70, y, TerrainType.Wall);
        // Left side (x=64) is NOT walled — it's the chunk boundary
        // But we add walls at x=64 for y outside the opening to create the pocket
        // Wall above cup opening
        for (int y = 20; y <= 29; y++)
            grid.SetTerrain(64, y, TerrainType.Wall);
        // Wall below cup opening
        for (int y = 35; y <= 45; y++)
            grid.SetTerrain(64, y, TerrainType.Wall);
        // This means the only way through x=64 is the cup opening at y=30..34

        grid.RebuildCostField();
        sim.RebuildPathfinder();

        // Unit INSIDE the cup (in chunk 1)
        Vec2 startPos = new(67f, 32f);
        // Target is ALSO in chunk 1 but outside the cup, below the bottom wall
        // The cup bottom wall is at y=35, so target at y=45 is in the same chunk but unreachable
        // from inside the cup via same-sector flow. Unit must leave chunk 1 (go left into chunk 0),
        // navigate around the wall at x=64, and re-enter chunk 1 below the cup.
        Vec2 target = new(67f, 45f);

        int idx = units.AddUnit(startPos, UnitType.Skeleton);
        units.AI[idx] = AIBehavior.MoveToPoint;
        units.MoveTarget[idx] = target;
        units.MaxSpeed[idx] = UnitSpeed;
        units.Stats[idx].CombatSpeed = UnitSpeed;
        units.Faction[idx] = Faction.Undead;

        _testUnitIndices.Add(idx);
        _testTargets.Add(target);
        _testStartTime = _elapsed;

        ZoomOnLocation(64f, 35f, 8f);
        DebugLog.Log(ScenarioLog, $"Cup walls in chunk 1 (x>=64), opening at y=30..34");
        DebugLog.Log(ScenarioLog, $"Wall at x=64 blocks passage except through cup at y=30..34");
        DebugLog.Log(ScenarioLog, $"Unit at ({startPos.X:F1},{startPos.Y:F1}) in chunk 0, target at ({target.X:F1},{target.Y:F1}) in chunk 0 south");
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
