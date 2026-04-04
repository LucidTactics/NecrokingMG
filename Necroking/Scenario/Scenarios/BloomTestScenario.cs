using System;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.GameSystems;
using Necroking.Movement;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Tests that the bloom post-processing effect is visible.
/// Spawns lightning strikes and fireballs to create bright additive-blend content,
/// then takes screenshots with bloom enabled and disabled for comparison.
/// </summary>
public class BloomTestScenario : ScenarioBase
{
    public override string Name => "bloom_test";
    public override bool WantsGround => true;

    private float _elapsed;
    private bool _complete;
    private int _frame;
    private int _step;
    private int _screenshotCount;

    // Spawn state
    private float _effectSpawnTimer;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Bloom Test Scenario ===");
        DebugLog.Log(ScenarioLog, "Testing bloom post-processing with bright effects");

        // Enable bloom with aggressive settings for clear visibility
        BloomOverride = new BloomSettings
        {
            Enabled = true,
            Threshold = 0.4f,
            SoftKnee = 0.5f,
            Intensity = 2.5f,
            Scatter = 0.7f,
            Iterations = 4,
            BicubicUpsampling = true
        };
        DebugLog.Log(ScenarioLog, $"Bloom override: threshold={BloomOverride.Threshold}, intensity={BloomOverride.Intensity}, scatter={BloomOverride.Scatter}, iterations={BloomOverride.Iterations}");

        var units = sim.UnitsMut;

        // Spawn a necromancer at center
        int necroIdx = units.AddUnit(new Vec2(10f, 10f), UnitType.Necromancer);
        units[necroIdx].AI = AIBehavior.PlayerControlled;
        sim.SetNecromancerIndex(necroIdx);

        // Spawn some idle skeletons as targets / scene furniture
        for (int i = 0; i < 6; i++)
        {
            float angle = i * MathF.PI * 2f / 6f;
            float dist = 6f;
            var pos = new Vec2(10f + MathF.Cos(angle) * dist, 10f + MathF.Sin(angle) * dist);
            int idx = units.AddUnit(pos, UnitType.Skeleton);
            units[idx].AI = AIBehavior.IdleAtPoint;
            units[idx].MoveTarget = pos;
        }

        // Spawn some soldiers on the other side
        for (int i = 0; i < 4; i++)
        {
            var pos = new Vec2(20f + i * 2f, 10f);
            int idx = units.AddUnit(pos, UnitType.Soldier);
            units[idx].AI = AIBehavior.IdleAtPoint;
            units[idx].MoveTarget = pos;
        }

        DebugLog.Log(ScenarioLog, $"Spawned {units.Count} units");
        ZoomOnLocation(14f, 10f, 32f);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;

        // Spawn bright effects continuously for the first few seconds
        if (_elapsed < 6f)
        {
            _effectSpawnTimer -= dt;
            if (_effectSpawnTimer <= 0f)
            {
                SpawnBrightEffects(sim);
                _effectSpawnTimer = 0.5f;
            }
        }

        if (DeferredScreenshot != null) return;

        // Screenshot sequence:
        // Step 0: Wait for effects, frame 0-2
        // Step 1: "bloom_on" screenshot at ~1.5s with bloom enabled
        // Step 2: Disable bloom
        // Step 3: "bloom_off" screenshot at ~3.0s with bloom disabled
        // Step 4: Re-enable bloom with lower threshold
        // Step 5: "bloom_strong" screenshot with strong effects
        // Step 6: Done

        int frameInStep = _frame % 3;

        switch (_step)
        {
            case 0:
                // Wait for first effects to render
                if (_elapsed >= 1.5f) _step = 1;
                break;

            case 1:
                if (frameInStep == 0)
                {
                    ZoomOnLocation(14f, 10f, 32f);
                    DebugLog.Log(ScenarioLog, "Step 1: Taking bloom_on screenshot (bloom enabled)");
                }
                else if (frameInStep == 2)
                {
                    DeferredScreenshot = "bloom_on";
                    _screenshotCount++;
                    _step = 2;
                }
                break;

            case 2:
                // Disable bloom for comparison shot
                BloomOverride = new BloomSettings
                {
                    Enabled = false,
                    Threshold = 0.4f,
                    Intensity = 2.5f,
                    Scatter = 0.7f,
                    Iterations = 4
                };
                DebugLog.Log(ScenarioLog, "Step 2: Bloom disabled for comparison");
                _step = 3;
                break;

            case 3:
                if (frameInStep == 0)
                {
                    ZoomOnLocation(14f, 10f, 32f);
                    DebugLog.Log(ScenarioLog, "Step 3: Taking bloom_off screenshot (bloom disabled)");
                }
                else if (frameInStep == 2)
                {
                    DeferredScreenshot = "bloom_off";
                    _screenshotCount++;
                    _step = 4;
                }
                break;

            case 4:
                // Re-enable bloom with very strong settings and low threshold
                BloomOverride = new BloomSettings
                {
                    Enabled = true,
                    Threshold = 0.2f,
                    SoftKnee = 0.5f,
                    Intensity = 4.0f,
                    Scatter = 1.0f,
                    Iterations = 6
                };
                DebugLog.Log(ScenarioLog, "Step 4: Bloom re-enabled with strong settings (threshold=0.2, intensity=4.0)");
                _step = 5;
                break;

            case 5:
                if (frameInStep == 0)
                {
                    ZoomOnLocation(14f, 10f, 32f);
                    DebugLog.Log(ScenarioLog, "Step 5: Taking bloom_strong screenshot");
                }
                else if (frameInStep == 2)
                {
                    DeferredScreenshot = "bloom_strong";
                    _screenshotCount++;
                    _step = 6;
                }
                break;

            case 6:
                _complete = true;
                break;
        }

        _frame++;
    }

    private void SpawnBrightEffects(Simulation sim)
    {
        // Spawn lightning strikes at various positions (very bright in additive mode)
        var style = new LightningStyle
        {
            CoreColor = new HdrColor(255, 255, 255, 255, 4f),
            GlowColor = new HdrColor(140, 180, 255, 200, 2.5f),
            CoreWidth = 3f,
            GlowWidth = 12f
        };

        // Lightning strike near center
        float offsetX = (MathF.Sin(_elapsed * 2f) * 4f);
        float offsetY = (MathF.Cos(_elapsed * 3f) * 3f);
        sim.Lightning.SpawnStrike(
            new Vec2(12f + offsetX, 10f + offsetY),
            telegraphDuration: 0.2f,
            effectDuration: 0.8f,
            aoeRadius: 3f,
            damage: 0,
            style: style,
            spellID: "bloom_test_strike",
            visual: StrikeVisual.Lightning
        );

        // Zap between two points (bright beam)
        sim.Lightning.SpawnZap(
            new Vec2(8f, 8f),
            new Vec2(16f + offsetX, 12f + offsetY),
            duration: 0.6f,
            style: style
        );

        // Another zap in a different direction
        var greenStyle = new LightningStyle
        {
            CoreColor = new HdrColor(100, 255, 100, 255, 3f),
            GlowColor = new HdrColor(40, 200, 40, 200, 2f),
            CoreWidth = 2f,
            GlowWidth = 10f
        };
        sim.Lightning.SpawnZap(
            new Vec2(14f, 6f),
            new Vec2(10f + offsetY, 14f - offsetX),
            duration: 0.5f,
            style: greenStyle
        );

        // Fire a fireball for explosion effect
        int necroIdx = sim.NecromancerIndex;
        if (necroIdx >= 0 && necroIdx < sim.Units.Count && sim.Units[necroIdx].Alive)
        {
            var necroPos = sim.Units[necroIdx].Position;
            float angle = _elapsed * 1.5f;
            var target = new Vec2(14f + MathF.Cos(angle) * 8f, 10f + MathF.Sin(angle) * 6f);
            sim.Projectiles.SpawnFireball(necroPos, target, Faction.Undead,
                sim.Units[necroIdx].Id, 0, 1.5f, "Fireball");
        }
    }

    public override bool IsComplete => _complete && DeferredScreenshot == null;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "=== Bloom Test Validation ===");
        DebugLog.Log(ScenarioLog, $"Screenshots taken: {_screenshotCount}");
        DebugLog.Log(ScenarioLog, $"Expected: 3 (bloom_on, bloom_off, bloom_strong)");
        DebugLog.Log(ScenarioLog, $"Check log/screenshots/ for visual comparison");

        bool pass = _screenshotCount >= 3;
        DebugLog.Log(ScenarioLog, pass ? "PASS" : "FAIL (not all screenshots taken)");
        return pass ? 0 : 1;
    }
}
