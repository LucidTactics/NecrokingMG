using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.GameSystems;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Simple visual test for the sparse-tuft grass system: spawns a single necromancer
/// to anchor the view, fills the surrounding grass cells, takes screenshots at two
/// zoom levels. Follows the FogOfWarScenario setup (bloom off, weather off, brown
/// bg) so the tuft sprites are clearly visible.
/// </summary>
public class GrassTuftTestScenario : ScenarioBase
{
    public override string Name => "grass_tufts";
    public override int GridSize => 64;
    public override bool WantsGrass => true;

    private float _elapsed;
    private int _shotsTaken;
    private bool _done;

    private readonly (float time, string name, float zoom)[] _shots =
    {
        (0.5f, "tufts_wide",  40f),
        (1.5f, "tufts_close", 100f),
    };

    public override void OnInit(Simulation sim)
    {
        BloomOverride = new BloomSettings { Enabled = false };
        sim.GameData.Settings.Weather.Enabled = false;
        BackgroundColor = new Microsoft.Xna.Framework.Color(90, 70, 45);

        sim.UnitsMut.AddUnit(new Vec2(32, 32), UnitType.Necromancer);

        // Fill a 16x16 grass patch around the necromancer with type 0 (green).
        if (GrassMap != null)
        {
            float cellSize = sim.GameData.Settings.Grass.CellSize;
            if (cellSize <= 0f) cellSize = 0.8f;
            int cx0 = (int)((32f - 8f) / cellSize);
            int cy0 = (int)((32f - 8f) / cellSize);
            int cx1 = (int)((32f + 8f) / cellSize);
            int cy1 = (int)((32f + 8f) / cellSize);
            for (int cy = cy0; cy <= cy1; cy++)
                for (int cx = cx0; cx <= cx1; cx++)
                    SetGrassType(cx, cy, 0);
        }

        DebugLog.Log(ScenarioLog, "[INIT] grass_tufts: necro at (32,32), grass filled 24..40");
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
