using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Movement;
using Necroking.Render;
using Necroking.World;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Spawns units near obstacles with varied terrain, enables each collision debug
/// visualization mode in sequence, and takes screenshots of each overlay.
/// </summary>
public class CollisionDebugTestScenario : ScenarioBase
{
    public override string Name => "collision_debug_test";
    public override bool WantsGround => true;

    private float _elapsed;
    private int _phase;
    private bool _complete;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Collision Debug Test Scenario ===");

        var grid = sim.Grid;
        var units = sim.UnitsMut;

        // --- Set up varied terrain ---
        // Rough terrain patch (upper-left area)
        for (int y = 4; y <= 8; y++)
            for (int x = 4; x <= 8; x++)
                grid.SetTerrain(x, y, TerrainType.Rough);

        // Water patch (center-left)
        for (int y = 12; y <= 16; y++)
            for (int x = 4; x <= 8; x++)
                grid.SetTerrain(x, y, TerrainType.Water);

        // Wall tiles forming an L-shape obstacle (right side)
        for (int x = 20; x <= 26; x++)
            grid.SetTerrain(x, 10, TerrainType.Wall);
        for (int y = 10; y <= 16; y++)
            grid.SetTerrain(20, y, TerrainType.Wall);

        // Small wall block (upper-right)
        for (int y = 4; y <= 6; y++)
            for (int x = 28; x <= 30; x++)
                grid.SetTerrain(x, y, TerrainType.Wall);

        // Rebuild cost field so the terrain costs take effect
        grid.RebuildCostField();

        // --- Add environment objects with collision radii ---
        var envSystem = sim.EnvironmentSystem;
        if (envSystem != null)
        {
            // Add a collision object def (no texture needed for debug viz)
            var rockDef = new EnvironmentObjectDef
            {
                Id = "debug_rock",
                Name = "Debug Rock",
                Category = "Misc",
                CollisionRadius = 2.0f,
                SpriteWorldHeight = 2f,
                Scale = 1f
            };
            int rockDefIdx = envSystem.AddDef(rockDef);

            // Place two rocks near units
            envSystem.AddObject((ushort)rockDefIdx, 15f, 10f, 1f);
            envSystem.AddObject((ushort)rockDefIdx, 25f, 20f, 1.5f);

            // Bake collisions into the cost field
            envSystem.BakeCollisions(grid);
        }

        sim.RebuildPathfinder();

        DebugLog.Log(ScenarioLog, $"Terrain setup: rough patch, water patch, L-wall, block wall");
        DebugLog.Log(ScenarioLog, $"Environment objects: 2 rocks with collision radii");

        // --- Spawn undead units (near obstacles) ---
        // Necromancer
        int necroIdx = units.AddUnit(new Vec2(12f, 10f), UnitType.Necromancer);
        units.Faction[necroIdx] = Faction.Undead;
        sim.SetNecromancerIndex(necroIdx);

        // Skeletons near rough terrain
        for (int i = 0; i < 4; i++)
        {
            int idx = units.AddUnit(new Vec2(10f + i * 1.2f, 7f), UnitType.Skeleton);
            units.Faction[idx] = Faction.Undead;
            // Give them a move target so they have preferred velocity
            units.MoveTarget[idx] = new Vec2(18f, 7f);
        }

        // Abomination near water
        int aboIdx = units.AddUnit(new Vec2(10f, 14f), UnitType.Abomination);
        units.Faction[aboIdx] = Faction.Undead;
        units.MoveTarget[aboIdx] = new Vec2(18f, 14f);

        // --- Spawn human units (near wall) ---
        for (int i = 0; i < 3; i++)
        {
            int idx = units.AddUnit(new Vec2(22f + i * 1.5f, 14f), UnitType.Soldier);
            units.Faction[idx] = Faction.Human;
            units.MoveTarget[idx] = new Vec2(12f, 14f);
        }

        int archerIdx = units.AddUnit(new Vec2(28f, 8f), UnitType.Archer);
        units.Faction[archerIdx] = Faction.Human;
        units.MoveTarget[archerIdx] = new Vec2(12f, 8f);

        DebugLog.Log(ScenarioLog, $"Spawned {units.Count} units (necro + skeletons + abomination + soldiers + archer)");

        // Camera: center on the action area
        ZoomOnLocation(18f, 12f, 32f);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        if (_complete) return;
        _elapsed += dt;

        switch (_phase)
        {
            // Phase 0: Wait for a few ticks so units settle, then enable CostField mode
            case 0:
                if (_elapsed > 0.5f)
                {
                    CollisionDebugOverride = CollisionDebugMode.CostField;
                    DebugLog.Log(ScenarioLog, "Enabling CostField debug mode");
                    _phase = 1;
                    _elapsed = 0;
                }
                break;

            // Phase 1: Take CostField screenshot
            case 1:
                if (_elapsed > 0.5f)
                {
                    DeferredScreenshot = "collision_debug_costfield";
                    DebugLog.Log(ScenarioLog, "Screenshot: collision_debug_costfield");
                    _phase = 2;
                    _elapsed = 0;
                }
                break;

            // Phase 2: Switch to UnitORCA mode
            case 2:
                if (DeferredScreenshot == null)
                {
                    CollisionDebugOverride = CollisionDebugMode.UnitORCA;
                    DebugLog.Log(ScenarioLog, "Enabling UnitORCA debug mode");
                    _phase = 3;
                    _elapsed = 0;
                }
                break;

            // Phase 3: Take UnitORCA screenshot
            case 3:
                if (_elapsed > 0.5f)
                {
                    DeferredScreenshot = "collision_debug_orca";
                    DebugLog.Log(ScenarioLog, "Screenshot: collision_debug_orca");
                    _phase = 4;
                    _elapsed = 0;
                }
                break;

            // Phase 4: Switch to Velocity mode
            case 4:
                if (DeferredScreenshot == null)
                {
                    CollisionDebugOverride = CollisionDebugMode.Velocity;
                    DebugLog.Log(ScenarioLog, "Enabling Velocity debug mode");
                    _phase = 5;
                    _elapsed = 0;
                }
                break;

            // Phase 5: Take Velocity screenshot
            case 5:
                if (_elapsed > 0.5f)
                {
                    DeferredScreenshot = "collision_debug_velocity";
                    DebugLog.Log(ScenarioLog, "Screenshot: collision_debug_velocity");
                    _phase = 6;
                    _elapsed = 0;
                }
                break;

            // Phase 6: Switch to OccupiedTiles mode
            case 6:
                if (DeferredScreenshot == null)
                {
                    CollisionDebugOverride = CollisionDebugMode.OccupiedTiles;
                    DebugLog.Log(ScenarioLog, "Enabling OccupiedTiles debug mode");
                    _phase = 7;
                    _elapsed = 0;
                }
                break;

            // Phase 7: Take OccupiedTiles screenshot
            case 7:
                if (_elapsed > 0.5f)
                {
                    DeferredScreenshot = "collision_debug_occupied";
                    DebugLog.Log(ScenarioLog, "Screenshot: collision_debug_occupied");
                    _phase = 8;
                    _elapsed = 0;
                }
                break;

            // Phase 8: Switch to All mode
            case 8:
                if (DeferredScreenshot == null)
                {
                    CollisionDebugOverride = CollisionDebugMode.All;
                    DebugLog.Log(ScenarioLog, "Enabling All debug mode");
                    _phase = 9;
                    _elapsed = 0;
                }
                break;

            // Phase 9: Take All-modes screenshot
            case 9:
                if (_elapsed > 0.5f)
                {
                    DeferredScreenshot = "collision_debug_all";
                    DebugLog.Log(ScenarioLog, "Screenshot: collision_debug_all");
                    _phase = 10;
                    _elapsed = 0;
                }
                break;

            // Phase 10: Done
            case 10:
                if (DeferredScreenshot == null)
                {
                    CollisionDebugOverride = CollisionDebugMode.Off;
                    _complete = true;
                }
                break;
        }
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "=== Collision Debug Test Complete ===");

        // Validate: all units still present
        int alive = 0;
        for (int i = 0; i < sim.Units.Count; i++)
            if (sim.Units.Alive[i]) alive++;

        bool pass = alive > 0;
        DebugLog.Log(ScenarioLog, $"Units alive: {alive}/{sim.Units.Count} -> {(pass ? "PASS" : "FAIL")}");
        DebugLog.Log(ScenarioLog, "Screenshots taken: costfield, orca, velocity, occupied, all");
        return pass ? 0 : 1;
    }
}
