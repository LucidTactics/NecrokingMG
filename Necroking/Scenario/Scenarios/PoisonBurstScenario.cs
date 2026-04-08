using Microsoft.Xna.Framework;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.GameSystems;

namespace Necroking.Scenario.Scenarios;

public class PoisonBurstScenario : ScenarioBase
{
    public override string Name => "poison_burst";
    public override bool IsComplete => _complete;

    private bool _complete;
    private float _elapsed;
    private bool _spawned;
    private int _screenshotIdx;

    // Screenshot schedule: time -> label
    // Eruption ~0.3s, Spread 0.3-0.5s, Decay 0.5-6.0s
    private static readonly (float time, string label)[] Screenshots = new[]
    {
        (0.4f,  "eruption"),
        (0.7f,  "spread"),
        (1.5f,  "decay_early"),
        (3.0f,  "decay_mid"),
        (4.5f,  "decay_late"),
        (5.5f,  "decay_final"),
    };

    public override void OnInit(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "=== Poison Burst Fade Test ===");
        DebugLog.Log(ScenarioLog, "Testing: eruption → spread → decay with smooth alpha fade-out");
        DebugLog.Log(ScenarioLog, "Background: dark purple (40,30,50) for contrast with green cloud");

        _elapsed = 0f;
        _spawned = false;
        _screenshotIdx = 0;

        // Disable weather and bloom for clean visual
        if (sim.GameData != null)
            sim.GameData.Settings.Weather.Enabled = false;
        BloomOverride = new BloomSettings { Enabled = false };

        // Dark purple background — high contrast with green cloud
        BackgroundColor = new Color(40, 30, 50);

        // Zoom to see cloud edges against background
        ZoomOnLocation(15, 15, 50);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;

        // Spawn cloud after a brief delay
        if (!_spawned && _elapsed >= 0.1f)
        {
            _spawned = true;
            var spell = sim.GameData?.Spells.Get("poison_burst");
            if (spell != null)
            {
                sim.PoisonClouds.SpawnCloud(new Vec2(15, 15), spell, Faction.Undead);
                DebugLog.Log(ScenarioLog, $"[{_elapsed:F2}s] Spawned poison cloud at (15,15)");
                DebugLog.Log(ScenarioLog, $"  Duration={spell.CloudDuration:F1}s, Eruption={spell.CloudEruptionDuration:F1}s, Spread={spell.CloudSpreadDuration:F1}s");
                float decayDuration = spell.CloudDuration - spell.CloudEruptionDuration - spell.CloudSpreadDuration;
                DebugLog.Log(ScenarioLog, $"  Decay phase={decayDuration:F1}s (from {spell.CloudEruptionDuration + spell.CloudSpreadDuration:F1}s to {spell.CloudDuration:F1}s)");
            }
            else
            {
                DebugLog.Log(ScenarioLog, "ERROR: poison_burst spell not found!");
                _complete = true;
                return;
            }
        }

        // Log cloud state and take screenshots
        if (_spawned && sim.PoisonClouds.Clouds.Count > 0)
        {
            var cloud = sim.PoisonClouds.Clouds[0];

            // Take scheduled screenshots
            if (_screenshotIdx < Screenshots.Length && _elapsed >= Screenshots[_screenshotIdx].time)
            {
                var (_, label) = Screenshots[_screenshotIdx];
                string name = $"poison_{_screenshotIdx}_{label}";
                DeferredScreenshot = name;

                float intensity = cloud.Phase switch
                {
                    CloudPhase.Eruption => 0.7f + 0.3f * cloud.PhaseProgress,
                    CloudPhase.Spread => 1.0f,
                    CloudPhase.Decay => (1f - cloud.PhaseProgress) * (1f - cloud.PhaseProgress),
                    _ => 0f
                };

                DebugLog.Log(ScenarioLog, $"[{_elapsed:F2}s] Screenshot '{name}'");
                DebugLog.Log(ScenarioLog, $"  Phase={cloud.Phase}, Progress={cloud.PhaseProgress:F3}, Intensity={intensity:F3}");
                DebugLog.Log(ScenarioLog, $"  Radius={cloud.CurrentRadius:F2}, Age={cloud.Age:F2}/{cloud.Duration:F1}");
                _screenshotIdx++;
            }
        }

        // Cloud removed — done
        if (_spawned && sim.PoisonClouds.Clouds.Count == 0 && _elapsed > 0.5f)
        {
            DebugLog.Log(ScenarioLog, $"[{_elapsed:F2}s] Cloud expired and removed — test complete");
            _complete = true;
        }

        // Safety timeout
        if (_elapsed > 10f)
        {
            DebugLog.Log(ScenarioLog, $"[{_elapsed:F2}s] Timeout — forcing completion");
            _complete = true;
        }
    }

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, $"=== Poison Burst Test Complete ({_screenshotIdx} screenshots taken) ===");
        DebugLog.Log(ScenarioLog, "Check screenshots for smooth fade: green cloud on purple background");
        DebugLog.Log(ScenarioLog, "  - eruption: cloud expanding, high opacity");
        DebugLog.Log(ScenarioLog, "  - spread: full size, full opacity");
        DebugLog.Log(ScenarioLog, "  - decay_early: just starting to fade");
        DebugLog.Log(ScenarioLog, "  - decay_mid: clearly faded but still visible");
        DebugLog.Log(ScenarioLog, "  - decay_late: nearly gone, very faint");
        DebugLog.Log(ScenarioLog, "  - decay_final: almost invisible or gone");
        return 0;
    }
}
