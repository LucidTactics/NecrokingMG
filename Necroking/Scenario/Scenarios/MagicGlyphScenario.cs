using System;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.GameSystems;
using Necroking.Movement;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Visual test for the procedural magic circle/glyph shader.
/// Places several glyphs in different states to verify rendering.
/// </summary>
public class MagicGlyphScenario : ScenarioBase
{
    public override string Name => "magic_glyph";

    private float _elapsed;
    private bool _complete;
    private int _screenshotPhase;

    private const float CX = 32f, CY = 32f;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Magic Glyph Scenario ===");
        DebugLog.Log(ScenarioLog, "Testing: procedural magic circle shader rendering");

        // Disable weather and bloom for clean visual test
        if (sim.GameData?.Settings.Weather != null)
            sim.GameData.Settings.Weather.Enabled = false;
        BloomOverride = new BloomSettings { Enabled = false };

        // Spawn a dormant glyph at center
        var glyph = sim.MagicGlyphs.SpawnGlyph(new Vec2(CX, CY), 1.5f, Faction.Undead);
        glyph.Color = new HdrColor(140, 80, 200, 255, 1.5f);
        glyph.Color2 = new HdrColor(200, 160, 255, 255, 2.0f);
        glyph.SymbolCount = 6;
        glyph.Damage = 10;
        DebugLog.Log(ScenarioLog, $"Spawned glyph at ({CX}, {CY}), radius=1.5");

        // Spawn a second glyph — triggering state to see charge → burst → decay
        var glyph2 = sim.MagicGlyphs.SpawnGlyph(new Vec2(CX + 5f, CY), 1.5f, Faction.Undead);
        glyph2.Color = new HdrColor(80, 200, 80, 255, 1.5f);
        glyph2.Color2 = new HdrColor(160, 255, 160, 255, 2.0f);
        glyph2.TriggerDuration = 1.5f;
        glyph2.ActiveDuration = 4f;
        glyph2.State = GlyphState.Triggering; // Start charging
        glyph2.StateTimer = 0f;
        DebugLog.Log(ScenarioLog, "Spawned second glyph (green, charging → burst)");

        ZoomOnLocation(CX + 2.5f, CY, 80f);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;

        // Screenshots at different times to catch different visual states
        // Screenshots without moving the camera
        if (_screenshotPhase == 0 && _elapsed >= 0.5f)
        {
            DeferredScreenshot = "glyph_dormant";
            _screenshotPhase++;
        }
        else if (_screenshotPhase == 1 && _elapsed >= 2.5f)
        {
            DeferredScreenshot = "glyph_burst";
            _screenshotPhase++;
        }
        else if (_screenshotPhase == 2 && _elapsed >= 4.5f)
        {
            DeferredScreenshot = "glyph_decay";
            _screenshotPhase++;
        }

        if (_elapsed > 8f) _complete = true;
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "PASS: Visual test complete (check screenshots)");
        return 0;
    }
}
