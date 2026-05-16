using Microsoft.Xna.Framework;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.GameSystems;
using Necroking.Render;
using Necroking.World;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Visual check for the foragable log's shadow. Places one `log2` env object
/// at a fixed world spot and zooms in close so the ellipse shadow under it
/// is unmistakeable.
/// </summary>
public class LogShadowScenario : ScenarioBase
{
    public override string Name => "log_shadow";

    private float _elapsed;
    private bool _shot;
    private bool _complete;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Log Shadow Test ===");
        BackgroundColor = new Color(200, 195, 205);
        WeatherPreset = "clear";
        BloomOverride = new BloomSettings { Enabled = false };
        ZoomOnLocation(16f, 16f, 96f);

        var env = sim.EnvironmentSystem;
        if (env == null) return;
        // Scenarios don't auto-load env_defs.json (that's part of the map-load
        // path). Pull them in so the foragable `log2` resolves.
        Necroking.Data.MapData.LoadEnvDefs(
            Necroking.Core.GamePaths.Resolve(Necroking.Core.GamePaths.EnvDefsJson),
            env);
        // Note: Game1.StartScenario calls env.LoadTextures(GraphicsDevice)
        // after OnInit, so newly-added defs get their textures resolved.
        int defIdx = env.FindDef("log2");
        if (defIdx < 0)
        {
            DebugLog.Log(ScenarioLog, "FAIL: log2 def missing");
            _complete = true;
            return;
        }
        env.AddObject((ushort)defIdx, 16f, 16f);
        DebugLog.Log(ScenarioLog, $"Placed log2 at (16,16), defIdx={defIdx}");
    }

    public override void OnTick(Simulation sim, float dt)
    {
        if (_complete) return;
        _elapsed += dt;
        if (DeferredScreenshot != null) return;
        if (!_shot && _elapsed >= 0.4f)
        {
            DeferredScreenshot = "log_shadow";
            _shot = true;
            return;
        }
        if (_shot && _elapsed >= 0.8f) _complete = true;
    }

    public override bool IsComplete => _complete;
    public override int OnComplete(Simulation sim) => 0;
}
