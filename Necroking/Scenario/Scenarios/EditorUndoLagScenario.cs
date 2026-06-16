using System.Diagnostics;
using Necroking.Core;
using Necroking.GameSystems;
using Necroking.World;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Measures map-editor object placement vs undo cost on a full-size (4097x4097)
/// grid — the same dimensions as the real default.json map — to confirm and
/// quantify the reported undo/placement lag. Pure timing, no rendering; results
/// go to scenario.log.
///
/// What it isolates:
///  - one OnCollisionsDirty fire == one Simulation.RebuildPathfinder (the cost
///    an object Add/Remove triggers when the callback is NOT suppressed)
///  - editor single placement (suppressed callback + incremental stamp) — fast
///  - current undo of one object (un-suppressed → one full rebuild)
///  - current undo of an N-object paint stroke (un-suppressed → N rebuilds)
///  - the fix (suppress during undo, one RebakeObjectCollisions afterwards)
/// </summary>
public class EditorUndoLagScenario : ScenarioBase
{
    public override string Name => "editor_undo_lag";
    public override int GridSize => 4097; // match the real default.json map size

    private bool _complete;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Editor Undo/Placement Lag Measurement ===");

        var env = sim.EnvironmentSystem;
        var grid = sim.Grid;
        if (env == null) { DebugLog.Log(ScenarioLog, "FAIL: no environment system"); _complete = true; return; }

        DebugLog.Log(ScenarioLog, $"Grid: {grid.Width}x{grid.Height} = {(long)grid.Width * grid.Height:N0} tiles");

        // A collision-bearing def, like a tree.
        int defIdx = env.AddDef(new EnvironmentObjectDef
        {
            Id = "lag_tree", Name = "Lag Tree", Category = "Tree",
            CollisionRadius = 1.0f, SpriteWorldHeight = 2f, Scale = 1f
        });

        // Populate ~950 collision objects (default.json has 953 placed) scattered
        // across the map, with the dirty callback suppressed during bulk setup.
        const int N = 950;
        var rng = new System.Random(12345);
        for (int i = 0; i < N; i++)
            env.AddObject((ushort)defIdx, rng.Next(grid.Width), rng.Next(grid.Height), 1f);
        DebugLog.Log(ScenarioLog, $"Placed {env.ObjectCount} collision objects");

        // Wire OnCollisionsDirty exactly like the editor does (Game1 LoadGame).
        env.OnCollisionsDirty = () => sim.RebuildPathfinder();

        double ToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
        var sw = new Stopwatch();

        // Baseline: one full pathfinder rebuild == one OnCollisionsDirty fire.
        sw.Restart();
        sim.RebuildPathfinder();
        sw.Stop();
        double oneRebuild = ToMs(sw.ElapsedTicks);
        DebugLog.Log(ScenarioLog, $"[baseline]  one RebuildPathfinder (== one un-suppressed Add/Remove) = {oneRebuild:F1} ms");

        // Editor single placement: callback suppressed + incremental stamp.
        {
            var prev = env.OnCollisionsDirty; env.OnCollisionsDirty = null;
            sw.Restart();
            int idx = env.AddObject((ushort)defIdx, 100f, 100f, 1f);
            env.StampObjectCollisionAt(grid, idx);
            sw.Stop();
            DebugLog.Log(ScenarioLog, $"[place]     single placement (suppressed + incremental stamp) = {ToMs(sw.ElapsedTicks):F3} ms");
            env.RemoveObject(idx); // suppressed cleanup to keep count stable
            env.OnCollisionsDirty = prev;
        }

        // CURRENT single-object undo: RemoveObject fires the un-suppressed callback.
        {
            sw.Restart();
            env.RemoveObject(env.ObjectCount - 1);
            sw.Stop();
            DebugLog.Log(ScenarioLog, $"[undo x1]   current single undo (un-suppressed → 1 rebuild) = {ToMs(sw.ElapsedTicks):F1} ms");
        }

        // CURRENT batch undo of a B-object stroke: B un-suppressed callbacks.
        const int B = 10;
        {
            sw.Restart();
            for (int i = 0; i < B; i++)
                env.RemoveObject(env.ObjectCount - 1);
            sw.Stop();
            DebugLog.Log(ScenarioLog, $"[undo xB]   current batch undo of {B} objects (un-suppressed → {B} rebuilds) = {ToMs(sw.ElapsedTicks):F1} ms");
        }

        // FIXED batch undo: suppress during the removals, one RebakeObjectCollisions
        // afterwards (RebuildCostField + BakeCollisions — what the editor's erase
        // path already does; note it skips BakeWalls/envIndex/pathfinder.Rebuild).
        {
            sw.Restart();
            var prev = env.OnCollisionsDirty; env.OnCollisionsDirty = null;
            for (int i = 0; i < B; i++)
                env.RemoveObject(env.ObjectCount - 1);
            env.OnCollisionsDirty = prev;
            grid.RebuildCostField();
            env.BakeCollisions(grid);
            sw.Stop();
            DebugLog.Log(ScenarioLog, $"[undo xB*]  batch undo of {B} objects (suppress + 1 rebake) = {ToMs(sw.ElapsedTicks):F1} ms");
        }

        // ACTUAL fix now in PerformUndo: suppress the callback and do NO rebake
        // (the MapEditor→gameplay exit transition rebuilds pathfinding once).
        {
            var prev0 = env.OnCollisionsDirty; env.OnCollisionsDirty = null;
            for (int i = 0; i < B; i++)
                env.AddObject((ushort)defIdx, rng.Next(grid.Width), rng.Next(grid.Height), 1f);
            env.OnCollisionsDirty = prev0;

            sw.Restart();
            var prev = env.OnCollisionsDirty; env.OnCollisionsDirty = null;
            for (int i = 0; i < B; i++)
                env.RemoveObject(env.ObjectCount - 1);
            env.OnCollisionsDirty = prev;
            sw.Stop();
            DebugLog.Log(ScenarioLog, $"[undo xB**] ACTUAL fix: suppress + NO rebake (exit rebuilds) = {ToMs(sw.ElapsedTicks):F3} ms");
        }

        DebugLog.Log(ScenarioLog, "=== done ===");
        _complete = true;
    }

    public override void OnTick(Simulation sim, float dt) { }
    public override bool IsComplete => _complete;
    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "Lag measurement complete");
        return 0;
    }
}
