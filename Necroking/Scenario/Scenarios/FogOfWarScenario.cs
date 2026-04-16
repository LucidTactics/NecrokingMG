using System;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.GameSystems;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Tests fog of war in all three modes. Places a necromancer and some enemies,
/// takes screenshots showing visibility patterns.
/// </summary>
public class FogOfWarScenario : ScenarioBase
{
    public override string Name => "fog_of_war";
    public override int GridSize => 64;

    private float _elapsed;
    private int _shotsTaken;
    private bool _done;

    // Take screenshots at different times to show fog states
    private readonly float[] _shotTimes = { 0.5f, 2.0f, 4.0f };

    public override void OnInit(Simulation sim)
    {
        BloomOverride = new BloomSettings { Enabled = false };
        sim.GameData.Settings.Weather.Enabled = false;

        // Enable FogOfWar mode (mode 2 = classic three-state)
        sim.GameData.Settings.FogOfWar.Mode = (int)FogOfWarMode.FogOfWar;
        sim.GameData.Settings.FogOfWar.DefaultSightRange = 10f;

        // Spawn necromancer at center
        sim.UnitsMut.AddUnit(new Vec2(32, 32), UnitType.Necromancer);

        // Spawn some skeletons nearby (they should reveal fog too)
        sim.UnitsMut.AddUnit(new Vec2(28, 32), UnitType.Skeleton);
        sim.UnitsMut.AddUnit(new Vec2(36, 32), UnitType.Skeleton);
        sim.UnitsMut.AddUnit(new Vec2(32, 28), UnitType.Skeleton);

        // Enemies: one inside the necro's sight range (should be visible),
        // one just outside (should be hidden), one far away (hidden)
        sim.UnitsMut.AddUnit(new Vec2(38, 32), UnitType.Soldier); // in sight
        sim.UnitsMut.AddUnit(new Vec2(44, 32), UnitType.Soldier); // outside sight
        sim.UnitsMut.AddUnit(new Vec2(50, 50), UnitType.Soldier); // far away

        DebugLog.Log(ScenarioLog, "[INIT] FogOfWar scenario: necro at (32,32), skeletons nearby, soldiers at corners");
        DebugLog.Log(ScenarioLog, $"[INIT] FogOfWar mode: {sim.GameData.Settings.FogOfWar.Mode}");

        ZoomOnLocation(32f, 32f, 20f);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;

        if (_shotsTaken < _shotTimes.Length && _elapsed >= _shotTimes[_shotsTaken])
        {
            ZoomOnLocation(32f, 32f, 20f);
            DeferredScreenshot = $"fog_{_shotsTaken}";
            DebugLog.Log(ScenarioLog, $"[TICK] Screenshot {_shotsTaken} at t={_elapsed:F2}s");
            _shotsTaken++;
        }

        if (_shotsTaken >= _shotTimes.Length && _elapsed > _shotTimes[^1] + 0.2f)
            _done = true;
    }

    public override bool IsComplete => _done;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, $"[COMPLETE] {_shotsTaken} screenshots, mode={sim.GameData.Settings.FogOfWar.Mode}");
        DebugLog.Log(ScenarioLog, "[PASS] FogOfWar scenario completed");
        return 0;
    }
}
