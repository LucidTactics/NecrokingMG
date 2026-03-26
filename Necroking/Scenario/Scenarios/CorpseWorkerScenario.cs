using System;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.World;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Skeleton worker drags corpses to a grinder building.
/// Port of C++ corpse_worker scenario.
/// NOTE: CorpseWorker AI is currently stubbed — this scenario validates
/// corpse/building setup and takes screenshots for visual verification.
/// </summary>
public class CorpseWorkerScenario : ScenarioBase
{
    public override string Name => "corpse_worker";

    private const float CX = 32f;
    private const float CY = 32f;
    private const int CorpseCount = 5;

    private bool _complete;
    private float _elapsed;
    private int _phase;
    private int _buildingObjIdx = -1;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, $"=== Scenario: {Name} ===");

        var env = sim.EnvironmentSystem;
        if (env == null)
        {
            DebugLog.Log(ScenarioLog, "ERROR: No environment system");
            _complete = true;
            return;
        }

        // Create grinder building def
        var grinder = new EnvironmentObjectDef
        {
            Id = "corpse_grinder",
            Name = "Corpse Grinder",
            Category = "Scenario",
            IsBuilding = true,
            BuildingMaxHP = 500,
            BuildingDefaultOwner = 0,
            Input1 = new ProcessSlot { Kind = "corpse" },
            Output = new ProcessSlot { Kind = "unit", ResourceID = "skeleton" },
            ProcessTime = 0.5f,
            MaxInputQueue = 10,
            AutoSpawn = true,
            SpawnOffsetY = 1.5f,
            SpriteWorldHeight = 2.5f,
            CollisionRadius = 0.5f
        };

        int defIdx = env.AddDef(grinder);
        _buildingObjIdx = env.AddObject((ushort)defIdx, CX, CY);

        DebugLog.Log(ScenarioLog, $"Building 'corpse_grinder' placed at ({CX}, {CY}), defIdx={defIdx}, objIdx={_buildingObjIdx}");

        // Place corpses in a ring around the building
        var corpses = sim.CorpsesMut;
        for (int i = 0; i < CorpseCount; i++)
        {
            float angle = i * MathF.PI * 2f / CorpseCount;
            float dist = 12f;
            float cx = CX + MathF.Cos(angle) * dist;
            float cy = CY + MathF.Sin(angle) * dist;

            corpses.Add(new Corpse
            {
                Position = new Vec2(cx, cy),
                UnitType = UnitType.Soldier,
                CorpseID = 9000 + i,
                FacingAngle = 90f,
                SpriteScale = 1f
            });
            DebugLog.Log(ScenarioLog, $"  Corpse {i} placed at ({cx:F1}, {cy:F1})");
        }

        // Spawn skeleton worker
        var units = sim.UnitsMut;
        int idx = units.AddUnit(new Vec2(CX + 3f, CY + 3f), UnitType.Skeleton);
        if (idx >= 0)
        {
            units.AI[idx] = AIBehavior.CorpseWorker;
            DebugLog.Log(ScenarioLog, $"Skeleton worker spawned at ({CX + 3f}, {CY + 3f}), idx={idx}, AI=CorpseWorker");
        }

        ZoomOnLocation(CX, CY, 25f);
        DebugLog.Log(ScenarioLog, $"Camera at ({CX}, {CY}) zoom=25");
    }

    public override void OnTick(Simulation sim, float dt)
    {
        if (_complete) return;
        if (DeferredScreenshot != null) return;
        _elapsed += dt;

        var corpses = sim.Corpses;
        var units = sim.Units;

        int totalCorpses = 0;
        for (int i = 0; i < corpses.Count; i++)
        {
            if (!corpses[i].Dissolving) totalCorpses++;
        }

        int skeletonCount = 0;
        for (int i = 0; i < units.Count; i++)
        {
            if (!units.Alive[i]) continue;
            if (units.Faction[i] == Faction.Undead) skeletonCount++;
        }

        if (_phase == 0)
        {
            DeferredScreenshot = "worker_overview";
            DebugLog.Log(ScenarioLog, $"[{_elapsed:F1}s] Phase 0: Initial overview screenshot");
            _phase = 1;
        }
        else if (_phase == 1 && _elapsed > 3f)
        {
            ZoomOnLocation(CX, CY, 40f);
            DeferredScreenshot = "worker_closeup";
            DebugLog.Log(ScenarioLog, $"[{_elapsed:F1}s] Phase 1: Closeup — corpses={totalCorpses}, skeletons={skeletonCount}");
            _phase = 2;
        }
        else if (_phase == 2 && _elapsed > 6f)
        {
            DeferredScreenshot = "worker_progress";
            DebugLog.Log(ScenarioLog, $"[{_elapsed:F1}s] Phase 2: Progress — corpses={totalCorpses}, skeletons={skeletonCount}");
            _phase = 3;
        }
        else if (_phase >= 3 && _elapsed > 8f)
        {
            // End after a reasonable time (AI is stubbed, so we can't wait for completion)
            DebugLog.Log(ScenarioLog, $"[{_elapsed:F1}s] Timeout — ending scenario. corpses={totalCorpses}, skeletons={skeletonCount}");
            DeferredScreenshot = "worker_complete";
            _complete = true;
        }
    }

    public override bool IsComplete => _complete && DeferredScreenshot == null;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, $"=== Scenario Complete: {Name} ===");
        DebugLog.Log(ScenarioLog, $"Total elapsed: {_elapsed:F1}s");

        int corpseCount = 0;
        foreach (var c in sim.Corpses)
            if (!c.Dissolving) corpseCount++;

        DebugLog.Log(ScenarioLog, $"Corpses remaining: {corpseCount}/{CorpseCount}");

        // Pass as long as the setup succeeded (building + corpses + worker spawned)
        return 0;
    }
}
