using System;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.World;

namespace Necroking.Scenario.Scenarios;

public class BuildingPlacementScenario : ScenarioBase
{
    public override string Name => "building_placement";
    private bool _complete;
    private int _tickCount;
    private int _initialObjectCount;
    private int _placedObjectIndex = -1;
    private float _placedX, _placedY;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Building Placement Scenario ===");
        DebugLog.Log(ScenarioLog, "Testing environment object def creation and placement");

        // Ensure environment system exists
        var envSystem = sim.EnvironmentSystem;
        if (envSystem == null)
        {
            DebugLog.Log(ScenarioLog, "ERROR: EnvironmentSystem is null, cannot test building placement");
            _complete = true;
            return;
        }

        _initialObjectCount = envSystem.ObjectCount;
        DebugLog.Log(ScenarioLog, $"Initial state: {envSystem.DefCount} defs, {_initialObjectCount} placed objects");

        // Add a building definition
        var buildingDef = new EnvironmentObjectDef
        {
            Id = "test_tower",
            Name = "Test Tower",
            Category = "Buildings",
            IsBuilding = true,
            PlayerBuildable = true,
            BuildingMaxHP = 200,
            BuildingProtection = 5,
            BuildingDefaultOwner = 0,
            CollisionRadius = 1.5f,
            SpriteWorldHeight = 6f,
            Scale = 1f
        };

        int defIdx = envSystem.AddDef(buildingDef);
        DebugLog.Log(ScenarioLog, $"Added building def '{buildingDef.Id}' at def index {defIdx}");
        DebugLog.Log(ScenarioLog, $"  IsBuilding={buildingDef.IsBuilding}, MaxHP={buildingDef.BuildingMaxHP}, " +
            $"Protection={buildingDef.BuildingProtection}, CollisionRadius={buildingDef.CollisionRadius}");

        // Place the building
        _placedX = 15f;
        _placedY = 15f;
        _placedObjectIndex = envSystem.AddObject((ushort)defIdx, _placedX, _placedY, 1f);
        DebugLog.Log(ScenarioLog, $"Placed building at ({_placedX}, {_placedY}), object index={_placedObjectIndex}");
        DebugLog.Log(ScenarioLog, $"After placement: {envSystem.ObjectCount} placed objects (was {_initialObjectCount})");

        // Verify the placed object data
        if (_placedObjectIndex >= 0 && _placedObjectIndex < envSystem.ObjectCount)
        {
            var obj = envSystem.GetObject(_placedObjectIndex);
            var rt = envSystem.GetObjectRuntime(_placedObjectIndex);
            DebugLog.Log(ScenarioLog, $"Placed object data: DefIndex={obj.DefIndex}, pos=({obj.X},{obj.Y}), " +
                $"scale={obj.Scale}, objectID={obj.ObjectID}");
            DebugLog.Log(ScenarioLog, $"Runtime data: HP={rt.HP}, Owner={rt.Owner}, Alive={rt.Alive}");
        }

        ZoomOnLocation(_placedX, _placedY, 48f);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _tickCount++;
        if (_tickCount >= 10)
            _complete = true;
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "=== Building Placement Validation ===");

        var envSystem = sim.EnvironmentSystem;
        if (envSystem == null)
        {
            DebugLog.Log(ScenarioLog, "FAIL: EnvironmentSystem is null");
            return 1;
        }

        // Check 1: Object count increased
        bool countIncreased = envSystem.ObjectCount > _initialObjectCount;
        DebugLog.Log(ScenarioLog, $"Object count increased: {_initialObjectCount} → {envSystem.ObjectCount} → {(countIncreased ? "PASS" : "FAIL")}");

        // Check 2: Object is at correct position
        bool positionCorrect = false;
        if (_placedObjectIndex >= 0 && _placedObjectIndex < envSystem.ObjectCount)
        {
            var obj = envSystem.GetObject(_placedObjectIndex);
            float dx = Math.Abs(obj.X - _placedX);
            float dy = Math.Abs(obj.Y - _placedY);
            positionCorrect = dx < 0.01f && dy < 0.01f;
            DebugLog.Log(ScenarioLog, $"Position correct: placed=({_placedX},{_placedY}), actual=({obj.X},{obj.Y}) → {(positionCorrect ? "PASS" : "FAIL")}");
        }
        else
        {
            DebugLog.Log(ScenarioLog, $"Position check: FAIL (invalid object index {_placedObjectIndex})");
        }

        // Check 3: Runtime data is correct
        bool runtimeCorrect = false;
        if (_placedObjectIndex >= 0 && _placedObjectIndex < envSystem.ObjectCount)
        {
            var rt = envSystem.GetObjectRuntime(_placedObjectIndex);
            runtimeCorrect = rt.HP == 200 && rt.Owner == 0 && rt.Alive;
            DebugLog.Log(ScenarioLog, $"Runtime data: HP={rt.HP}(exp 200), Owner={rt.Owner}(exp 0), Alive={rt.Alive}(exp true) → {(runtimeCorrect ? "PASS" : "FAIL")}");
        }

        // Check 4: Def can be found by ID
        int foundIdx = envSystem.FindDef("test_tower");
        bool defFound = foundIdx >= 0;
        DebugLog.Log(ScenarioLog, $"Def lookup by ID 'test_tower': index={foundIdx} → {(defFound ? "PASS" : "FAIL")}");

        bool pass = countIncreased && positionCorrect && runtimeCorrect && defFound;
        DebugLog.Log(ScenarioLog, $"Overall: {(pass ? "PASS" : "FAIL")}");
        return pass ? 0 : 1;
    }
}
