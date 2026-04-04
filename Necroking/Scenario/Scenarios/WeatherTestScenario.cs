using System;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.GameSystems;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Tests weather effects: rain, fog, vignette, tint, brightness, and lightning flash.
/// Cycles through multiple weather presets and takes screenshots of each.
/// </summary>
public class WeatherTestScenario : ScenarioBase
{
    public override string Name => "weather_test";
    public override bool WantsGround => true;

    private float _elapsed;
    private bool _complete;
    private int _frame;
    private int _step;
    private int _screenshotCount;

    private static readonly string[] Presets = { "dreary_rain", "foggy", "thunderstorm", "dusk", "night", "evil_night" };
    private int _presetIndex;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Weather Test Scenario ===");
        DebugLog.Log(ScenarioLog, "Testing rain, fog, vignette, tint, brightness, lightning flash");

        // Start with first preset
        WeatherPreset = Presets[0];
        DebugLog.Log(ScenarioLog, $"Initial preset: {Presets[0]}");

        // Spawn some units for visual context
        var units = sim.UnitsMut;
        for (int i = 0; i < 8; i++)
        {
            float angle = i * MathF.PI * 2f / 8f;
            var pos = new Vec2(32f + MathF.Cos(angle) * 8f, 32f + MathF.Sin(angle) * 6f);
            int idx = units.AddUnit(pos, i < 4 ? UnitType.Skeleton : UnitType.Soldier);
            units[idx].AI = AIBehavior.IdleAtPoint;
            units[idx].MoveTarget = pos;
        }

        DebugLog.Log(ScenarioLog, $"Spawned {units.Count} units for visual context");
        ZoomOnLocation(32f, 32f, 40f);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;

        if (DeferredScreenshot != null) return;

        // State machine: for each preset, wait 2 frames then screenshot
        // Step 0,1: wait for first preset to render
        // Then cycle: set preset -> wait -> screenshot -> next preset
        int frameInStep = _frame % 3;

        switch (_step)
        {
            case 0:
                // Wait for initial render
                if (_elapsed >= 0.5f)
                {
                    DebugLog.Log(ScenarioLog, $"Step 1: Taking screenshot of preset '{Presets[_presetIndex]}'");
                    _step = 1;
                }
                break;

            case 1:
                // Take screenshot of current preset
                if (frameInStep == 2)
                {
                    string name = $"weather_{Presets[_presetIndex]}";
                    DeferredScreenshot = name;
                    _screenshotCount++;
                    DebugLog.Log(ScenarioLog, $"Screenshot: {name}");
                    _step = 2;
                }
                break;

            case 2:
                // Advance to next preset or close-up
                _presetIndex++;
                if (_presetIndex < Presets.Length)
                {
                    WeatherPreset = Presets[_presetIndex];
                    DebugLog.Log(ScenarioLog, $"Switching to preset: {Presets[_presetIndex]}");
                    _step = 3;
                }
                else
                {
                    // Take a close-up of rain with dreary_rain
                    WeatherPreset = "dreary_rain";
                    ZoomOnLocation(32f, 32f, 100f);
                    DebugLog.Log(ScenarioLog, "Close-up: dreary_rain at zoom 100");
                    _step = 5;
                }
                break;

            case 3:
                // Wait a frame for preset to apply
                _step = 4;
                break;

            case 4:
                // Take screenshot
                if (frameInStep == 2)
                {
                    string name = $"weather_{Presets[_presetIndex]}";
                    DeferredScreenshot = name;
                    _screenshotCount++;
                    DebugLog.Log(ScenarioLog, $"Screenshot: {name}");
                    _step = 2;
                }
                break;

            case 5:
                // Wait for close-up
                _step = 6;
                break;

            case 6:
                if (frameInStep == 2)
                {
                    DeferredScreenshot = "weather_rain_closeup";
                    _screenshotCount++;
                    DebugLog.Log(ScenarioLog, "Screenshot: weather_rain_closeup");
                    _step = 7;
                }
                break;

            case 7:
                // Thunderstorm close-up (rain + lightning)
                WeatherPreset = "thunderstorm";
                ZoomOnLocation(32f, 32f, 80f);
                DebugLog.Log(ScenarioLog, "Close-up: thunderstorm at zoom 80");
                _step = 8;
                break;

            case 8:
                // Wait for lightning flash to potentially fire (run a few frames)
                _step = 9;
                break;

            case 9:
                if (frameInStep == 2)
                {
                    DeferredScreenshot = "weather_thunderstorm_closeup";
                    _screenshotCount++;
                    DebugLog.Log(ScenarioLog, "Screenshot: weather_thunderstorm_closeup");
                    _step = 10;
                }
                break;

            case 10:
                _complete = true;
                break;
        }

        _frame++;
    }

    public override bool IsComplete => _complete && DeferredScreenshot == null;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "=== Weather Test Validation ===");
        DebugLog.Log(ScenarioLog, $"Screenshots taken: {_screenshotCount}");
        DebugLog.Log(ScenarioLog, $"Expected: {Presets.Length + 2} ({Presets.Length} presets + 2 close-ups)");
        DebugLog.Log(ScenarioLog, "Check log/screenshots/ for visual verification");

        bool pass = _screenshotCount >= Presets.Length + 2;
        DebugLog.Log(ScenarioLog, pass ? "PASS" : "FAIL (not all screenshots taken)");
        return pass ? 0 : 1;
    }
}
