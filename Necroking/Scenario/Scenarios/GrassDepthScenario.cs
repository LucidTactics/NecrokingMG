using System;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.GameSystems;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Verifies that grass tufts Y-sort with units: a row of necromancers arranged
/// along the Y axis with a grass strip between every pair. Grass cells with
/// lower Y should render behind the unit standing just south of them; grass
/// cells with higher Y should render in front of the unit just north of them,
/// overlapping their feet.
/// </summary>
public class GrassDepthScenario : ScenarioBase
{
    public override string Name => "grass_depth";
    public override int GridSize => 64;
    public override bool WantsGrass => true;

    private float _elapsed;
    private int _shotsTaken;
    private bool _done;

    private readonly (float time, string name, float zoom)[] _shots =
    {
        (0.5f, "grass_depth_wide",  40f),
        (1.5f, "grass_depth_close", 110f),
    };

    public override void OnInit(Simulation sim)
    {
        BloomOverride = new BloomSettings { Enabled = false };
        sim.GameData.Settings.Weather.Enabled = false;
        BackgroundColor = new Microsoft.Xna.Framework.Color(90, 70, 45);

        // Three units stacked along Y. Grass cells in the gaps between them.
        sim.UnitsMut.AddUnit(new Vec2(32, 28), UnitType.Necromancer); // top
        sim.UnitsMut.AddUnit(new Vec2(32, 32), UnitType.Necromancer); // middle
        sim.UnitsMut.AddUnit(new Vec2(32, 36), UnitType.Necromancer); // bottom

        if (GrassMap != null)
        {
            float cellSize = sim.GameData.Settings.Grass.CellSize;
            if (cellSize <= 0f) cellSize = 1.0f;

            // Paint two horizontal grass strips between the units. The strip at
            // Y≈30 should appear IN FRONT of unit at Y=28 (overlapping its feet)
            // and BEHIND unit at Y=32. The strip at Y≈34 similarly between the
            // middle and bottom units.
            foreach (var stripY in new[] { 29.5f, 33.5f })
            {
                int cy = (int)(stripY / cellSize);
                int cx0 = (int)((32f - 6f) / cellSize);
                int cx1 = (int)((32f + 6f) / cellSize);
                for (int cx = cx0; cx <= cx1; cx++)
                    for (int dy = 0; dy < 2; dy++) // 2-cell-tall strip for visibility
                        SetGrassType(cx, cy + dy, 0);
            }
        }

        DebugLog.Log(ScenarioLog, "[INIT] grass_depth: 3 necros at y={28,32,36}, 2 grass strips between");
        ZoomOnLocation(32f, 32f, _shots[0].zoom);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;

        if (_shotsTaken < _shots.Length && _elapsed >= _shots[_shotsTaken].time)
        {
            ZoomOnLocation(32f, 32f, _shots[_shotsTaken].zoom);
            DeferredScreenshot = _shots[_shotsTaken].name;
            DebugLog.Log(ScenarioLog, $"[TICK] shot {_shots[_shotsTaken].name} zoom={_shots[_shotsTaken].zoom}");
            _shotsTaken++;
        }

        if (_shotsTaken >= _shots.Length && _elapsed > _shots[^1].time + 0.3f)
            _done = true;
    }

    public override bool IsComplete => _done;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, $"[COMPLETE] {_shotsTaken} shots");
        return 0;
    }
}
