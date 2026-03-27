using System;
using Microsoft.Xna.Framework;
using Necroking.Core;
using Necroking.Data.Registries;
using Necroking.GameSystems;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Draws simple bright HDR rectangles to test bloom.
/// If bloom is working, the bright rectangles should have visible glow spreading outward.
/// </summary>
public class BloomDebugScenario : ScenarioBase
{
    public override string Name => "bloom_debug";
    public override bool WantsGround => true;

    public bool DrawTestShapes = true;
    /// <summary>Set by Game1 so scenario can control bloom debug viz.</summary>
    public Render.BloomRenderer? BloomRef;

    // Bright shape positions (world coords)
    public static readonly (float x, float y, float size, Color color, string label)[] TestShapes = new[]
    {
        // Pure white at max brightness — should definitely bloom
        (10f, 8f, 2f, new Color(255, 255, 255, 255), "White 1x"),

        // Multiple additive layers to exceed 1.0
        (14f, 8f, 2f, new Color(255, 255, 200, 255), "Additive 3x"),

        // Bright yellow-green (common spell color)
        (18f, 8f, 2f, new Color(200, 255, 100, 255), "Spell Green"),

        // Dark reference that should NOT bloom
        (22f, 8f, 2f, new Color(40, 40, 60, 255), "Dark (no bloom)"),
    };

    private float _elapsed;
    private int _phase;
    private bool _complete;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Bloom Debug Scenario ===");
        DebugLog.Log(ScenarioLog, "Drawing bright test shapes to verify bloom pipeline");

        ZoomOnLocation(16f, 8f, 40f);

        // Override bloom to known-good settings
        BloomOverride = new BloomSettings
        {
            Enabled = true,
            Threshold = 0.9f,   // Only truly bright pixels bloom
            SoftKnee = 0.3f,
            Intensity = 5f,
            Scatter = 0.5f,
            Iterations = 6,
            BicubicUpsampling = true
        };
    }

    public override void OnTick(Simulation sim, float dt)
    {
        if (_complete) return;
        _elapsed += dt;

        switch (_phase)
        {
            case 0:
                if (_elapsed > 1f)
                {
                    DebugLog.Log(ScenarioLog, "Taking screenshot: bloom_debug_on (threshold=0.3, intensity=8)");
                    DeferredScreenshot = "bloom_debug_on";
                    _phase = 1;
                    _elapsed = 0;
                }
                break;
            case 1:
                if (DeferredScreenshot == null)
                {
                    // Show just the extract result (what the bloom shader extracted)
                    if (BloomRef != null) BloomRef.DebugShowExtract = true;
                    _phase = 2;
                    _elapsed = 0;
                }
                break;
            case 2:
                if (_elapsed > 0.5f)
                {
                    DebugLog.Log(ScenarioLog, "Taking screenshot: bloom_debug_extract (shows what extract pass found)");
                    DeferredScreenshot = "bloom_debug_extract";
                    _phase = 3;
                    _elapsed = 0;
                }
                break;
            case 3:
                if (DeferredScreenshot == null)
                {
                    if (BloomRef != null) BloomRef.DebugShowExtract = false;
                    BloomOverride = new BloomSettings { Enabled = false };
                    _phase = 4;
                    _elapsed = 0;
                }
                break;
            case 4:
                if (_elapsed > 0.5f)
                {
                    DebugLog.Log(ScenarioLog, "Taking screenshot: bloom_debug_off");
                    DeferredScreenshot = "bloom_debug_off";
                    _phase = 5;
                    _elapsed = 0;
                }
                break;
            case 5:
                if (DeferredScreenshot == null)
                    _complete = true;
                break;
        }
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "Bloom debug complete — compare bloom_debug_on vs bloom_debug_off");
        return 0;
    }
}
