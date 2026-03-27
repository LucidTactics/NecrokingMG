using System;
using Necroking.Core;
using Necroking.GameSystems;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Tests grass blade rendering at different zoom levels with multiple grass types
/// and wind animation. Verifies blades render with proper colors, shapes, and sway.
/// </summary>
public class GrassBladeTestScenario : ScenarioBase
{
    public override string Name => "grass_blade_test";
    public override bool WantsGrass => true;
    public override bool WantsGround => true;

    private bool _complete;
    private int _frame;
    private int _step;
    private int _screenshotCount;
    private int _gcx, _gcy; // grass grid center
    private int _windDelayFrames; // frames to wait between wind screenshots
    private bool _windSetupDone; // whether wind step 2 grass is already placed

    private static readonly (string name, string desc, float zoom)[] Steps =
    {
        ("blade_close_green",   "Close-up green grass blades",         120f),
        ("blade_close_dead",    "Close-up dead grass blades",          120f),
        ("blade_close_tall",    "Close-up tall grass blades",          120f),
        ("blade_medium_patch",  "Medium zoom: 7x7 green patch",        60f),
        ("blade_medium_mixed",  "Medium zoom: all 3 types side-by-side", 50f),
        ("blade_far_overview",  "Far zoom: large grass field",          25f),
        ("blade_wind_frame1",   "Wind animation frame 1",              80f),
        ("blade_wind_frame2",   "Wind animation frame 2 (later time)", 80f),
    };

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Grass Blade Test Scenario ===");

        if (GrassMap == null)
        {
            DebugLog.Log(ScenarioLog, "ERROR: GrassMap not set");
            return;
        }

        float cellSize = 0.8f;
        float worldCenter = 32f;
        _gcx = (int)(worldCenter / cellSize);
        _gcy = (int)(worldCenter / cellSize);

        DebugLog.Log(ScenarioLog, $"Grass grid: {GrassW}x{GrassH}, center: ({_gcx},{_gcy}), 3 types");
    }

    private void SetupStep()
    {
        // Clear all grass
        FillGrass(0);

        switch (_step)
        {
            case 0: // Close-up green: 3x3 patch
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                        SetGrassType(_gcx + dx, _gcy + dy, 0);
                ZoomOnLocation(32f, 32f, Steps[_step].zoom);
                break;

            case 1: // Close-up dead: 3x3 patch
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                        SetGrassType(_gcx + dx, _gcy + dy, 1);
                ZoomOnLocation(32f, 32f, Steps[_step].zoom);
                break;

            case 2: // Close-up tall: 3x3 patch
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                        SetGrassType(_gcx + dx, _gcy + dy, 2);
                ZoomOnLocation(32f, 32f, Steps[_step].zoom);
                break;

            case 3: // Medium zoom: 7x7 green patch
                for (int dy = -3; dy <= 3; dy++)
                    for (int dx = -3; dx <= 3; dx++)
                        SetGrassType(_gcx + dx, _gcy + dy, 0);
                ZoomOnLocation(32f, 32f, Steps[_step].zoom);
                break;

            case 4: // Medium mixed: green, dead, tall side by side
                for (int dy = -3; dy <= 3; dy++)
                    for (int dx = -8; dx <= -3; dx++)
                        SetGrassType(_gcx + dx, _gcy + dy, 0);
                for (int dy = -3; dy <= 3; dy++)
                    for (int dx = -2; dx <= 2; dx++)
                        SetGrassType(_gcx + dx, _gcy + dy, 1);
                for (int dy = -3; dy <= 3; dy++)
                    for (int dx = 3; dx <= 8; dx++)
                        SetGrassType(_gcx + dx, _gcy + dy, 2);
                ZoomOnLocation(32f, 32f, Steps[_step].zoom);
                break;

            case 5: // Far overview: large field with all types
                for (int dy = -10; dy <= 10; dy++)
                    for (int dx = -15; dx <= -5; dx++)
                        SetGrassType(_gcx + dx, _gcy + dy, 0);
                for (int dy = -10; dy <= 10; dy++)
                    for (int dx = -4; dx <= 4; dx++)
                        SetGrassType(_gcx + dx, _gcy + dy, 1);
                for (int dy = -10; dy <= 10; dy++)
                    for (int dx = 5; dx <= 15; dx++)
                        SetGrassType(_gcx + dx, _gcy + dy, 2);
                ZoomOnLocation(32f, 32f, Steps[_step].zoom);
                break;

            case 6: // Wind frame 1
            case 7: // Wind frame 2 (same layout, different gameTime)
                for (int dy = -4; dy <= 4; dy++)
                    for (int dx = -6; dx <= 6; dx++)
                        SetGrassType(_gcx + dx, _gcy + dy, 0);
                ZoomOnLocation(32f, 32f, Steps[_step].zoom);
                break;
        }
    }

    public override void OnTick(Simulation sim, float dt)
    {
        if (_complete) return;
        if (DeferredScreenshot != null) return;

        // Special handling for wind frame 2: wait 90 frames (~1.5s) for visible sway difference
        if (_step == 7 && _windDelayFrames > 0)
        {
            _windDelayFrames--;
            if (_windDelayFrames == 0)
            {
                // Now take the wind frame 2 screenshot
                DeferredScreenshot = Steps[_step].name;
                _screenshotCount++;
                DebugLog.Log(ScenarioLog, $"[{_step + 1}/{Steps.Length}] {Steps[_step].name} - {Steps[_step].desc}");
                _step++;
            }
            _frame++;
            return;
        }

        int frameInStep = _frame % 3;

        if (_step < Steps.Length)
        {
            if (frameInStep == 0)
            {
                // For wind frame 2, grass is already set up from frame 1 -- just keep the camera
                if (_step == 7 && !_windSetupDone)
                {
                    SetupStep();
                    _windSetupDone = true;
                    _windDelayFrames = 90; // wait ~1.5 seconds for wind to shift
                    _frame++;
                    return;
                }
                SetupStep();
            }
            else if (frameInStep == 2)
            {
                DeferredScreenshot = Steps[_step].name;
                _screenshotCount++;
                DebugLog.Log(ScenarioLog, $"[{_step + 1}/{Steps.Length}] {Steps[_step].name} - {Steps[_step].desc}");
                _step++;
            }
        }

        if (_step >= Steps.Length && DeferredScreenshot == null)
            _complete = true;

        _frame++;
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, $"=== Grass Blade Test Complete: {_screenshotCount} screenshots ===");
        return 0;
    }
}
