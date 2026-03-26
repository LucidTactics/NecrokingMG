using System;
using Necroking.Core;
using Necroking.GameSystems;

namespace Necroking.Scenario.Scenarios;

public class GrassTestScenario : ScenarioBase
{
    public override string Name => "grass_test";
    public override bool WantsGrass => true;

    private bool _complete;
    private int _frame;
    private int _step;
    private int _screenshotCount;
    private int _gcx, _gcy; // grass grid center

    private static readonly (string name, string desc)[] Steps = {
        ("grass_single_green", "Single cell of green grass"),
        ("grass_single_dead", "Single cell of dead grass"),
        ("grass_single_tall", "Single cell of tall grass"),
        ("grass_patch_green", "3x3 patch of green grass"),
        ("grass_mixed_types", "Green, dead, tall side by side"),
        ("grass_overview", "All patches in wider shot"),
    };

    public override void OnInit(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "=== Grass Test Scenario ===");

        if (GrassMap == null)
        {
            DebugLog.Log(ScenarioLog, "ERROR: GrassMap not set");
            return;
        }

        float cellSize = 0.8f;
        float worldCenter = 32f; // scenario grid is 64x64
        _gcx = (int)(worldCenter / cellSize);
        _gcy = (int)(worldCenter / cellSize);

        DebugLog.Log(ScenarioLog, $"Grass grid center: ({_gcx}, {_gcy}), 3 types registered");
    }

    private void SetupStep()
    {
        // Clear all grass
        FillGrass(0);

        switch (_step)
        {
            case 0: // Single green
                SetGrassType(_gcx, _gcy, 0);
                ZoomOnLocation(32f, 32f, 120f);
                break;
            case 1: // Single dead
                SetGrassType(_gcx, _gcy, 1);
                ZoomOnLocation(32f, 32f, 120f);
                break;
            case 2: // Single tall
                SetGrassType(_gcx, _gcy, 2);
                ZoomOnLocation(32f, 32f, 120f);
                break;
            case 3: // 3x3 green patch
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                        SetGrassType(_gcx + dx, _gcy + dy, 0);
                ZoomOnLocation(32f, 32f, 100f);
                break;
            case 4: // Mixed types side by side
                for (int dy = -1; dy <= 1; dy++)
                    SetGrassType(_gcx - 3, _gcy + dy, 0);
                for (int dy = -1; dy <= 1; dy++)
                    SetGrassType(_gcx, _gcy + dy, 1);
                for (int dy = -1; dy <= 1; dy++)
                    SetGrassType(_gcx + 3, _gcy + dy, 2);
                ZoomOnLocation(32f, 32f, 80f);
                break;
            case 5: // Overview - larger patches
                for (int dy = -3; dy <= 3; dy++)
                    for (int dx = -12; dx <= -6; dx++)
                        SetGrassType(_gcx + dx, _gcy + dy, 0);
                for (int dy = -3; dy <= 3; dy++)
                    for (int dx = -3; dx <= 3; dx++)
                        SetGrassType(_gcx + dx, _gcy + dy, 1);
                for (int dy = -3; dy <= 3; dy++)
                    for (int dx = 6; dx <= 12; dx++)
                        SetGrassType(_gcx + dx, _gcy + dy, 2);
                ZoomOnLocation(32f, 32f, 40f);
                break;
        }
    }

    public override void OnTick(Simulation sim, float dt)
    {
        if (DeferredScreenshot != null) return;

        int frameInStep = _frame % 3;

        if (_step < Steps.Length)
        {
            if (frameInStep == 0)
            {
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
        DebugLog.Log(ScenarioLog, $"=== Grass Test Complete: {_screenshotCount} screenshots ===");
        return 0;
    }
}
