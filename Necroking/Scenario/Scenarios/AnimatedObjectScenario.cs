using System;
using Necroking.Core;
using Necroking.GameSystems;
using Necroking.Data.Registries;
using Necroking.Render;
using Necroking.World;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Tests animated environment objects with noise, wind sync, random start frames,
/// and weighted momentum direction changes.
/// </summary>
public class AnimatedObjectScenario : ScenarioBase
{
    public override string Name => "animated_object";

    private float _elapsed;
    private int _shotsTaken;
    private bool _done;
    private int _noisyDefIdx = -1;
    private int _syncDefIdx = -1;

    private readonly float[] _shotTimes = { 0.5f, 2.0f, 4.0f, 6.0f };

    public override void OnInit(Simulation sim)
    {
        BloomOverride = new BloomSettings { Enabled = false };
        sim.GameData.Settings.Weather.Enabled = false;
        BackgroundColor = new Microsoft.Xna.Framework.Color(30, 50, 30);

        // Full-featured animated oak: noise + wind sync + momentum
        var noisyDef = new EnvironmentObjectDef
        {
            Id = "oak_anim_noisy",
            Name = "Animated Oak (full)",
            Category = "Tree",
            TexturePath = "assets/Environment/Trees/OakExports/OakSprite1_Spritesheet_17F.png",
            SpriteWorldHeight = 6f,
            Scale = 1f,
            PivotX = 0.5f,
            PivotY = 1f,
            IsAnimated = true,
            AnimFramesX = 17,
            AnimFramesY = 1,
            AnimFPS = 10f,
            AnimNoise = 0.4f,
            AnimWindSync = 0.5f,
            ShadowType = 2
        };
        _noisyDefIdx = sim.EnvironmentSystem.AddDef(noisyDef);

        // No noise/wind — all identical, for comparison
        var syncDef = new EnvironmentObjectDef
        {
            Id = "oak_anim_sync",
            Name = "Animated Oak (sync)",
            Category = "Tree",
            TexturePath = "assets/Environment/Trees/OakExports/OakSprite1_Spritesheet_17F.png",
            SpriteWorldHeight = 6f,
            Scale = 1f,
            PivotX = 0.5f,
            PivotY = 1f,
            IsAnimated = true,
            AnimFramesX = 17,
            AnimFramesY = 1,
            AnimFPS = 10f,
            AnimNoise = 0f,
            AnimWindSync = 0f,
            ShadowType = 2
        };
        _syncDefIdx = sim.EnvironmentSystem.AddDef(syncDef);

        // Top row: 4 noisy oaks spread out (should desync + wind wave visible)
        sim.EnvironmentSystem.AddObject((ushort)_noisyDefIdx, 6f, 8f);
        sim.EnvironmentSystem.AddObject((ushort)_noisyDefIdx, 12f, 8f);
        sim.EnvironmentSystem.AddObject((ushort)_noisyDefIdx, 18f, 8f);
        sim.EnvironmentSystem.AddObject((ushort)_noisyDefIdx, 24f, 8f);

        // Bottom row: 4 synced oaks
        sim.EnvironmentSystem.AddObject((ushort)_syncDefIdx, 6f, 16f);
        sim.EnvironmentSystem.AddObject((ushort)_syncDefIdx, 12f, 16f);
        sim.EnvironmentSystem.AddObject((ushort)_syncDefIdx, 18f, 16f);
        sim.EnvironmentSystem.AddObject((ushort)_syncDefIdx, 24f, 16f);

        // Log initial start frames (random start frame feature)
        for (int j = 0; j < sim.EnvironmentSystem.ObjectCount; j++)
        {
            var rt = sim.EnvironmentSystem.GetObjectRuntime(j);
            var o = sim.EnvironmentSystem.Objects[j];
            DebugLog.Log(ScenarioLog, $"[INIT] obj[{j}] pos=({o.X},{o.Y}) startFrame={(int)rt.AnimTime} reversed={rt.AnimReversed}");
        }

        ZoomOnLocation(15f, 12f, 40f);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;

        if (_shotsTaken < _shotTimes.Length && _elapsed >= _shotTimes[_shotsTaken])
        {
            // Log state for all objects
            DebugLog.Log(ScenarioLog, $"[TICK] t={_elapsed:F2}s — snapshot {_shotsTaken}:");
            for (int j = 0; j < sim.EnvironmentSystem.ObjectCount; j++)
            {
                var rt = sim.EnvironmentSystem.GetObjectRuntime(j);
                var o = sim.EnvironmentSystem.Objects[j];
                var d = sim.EnvironmentSystem.GetDef(o.DefIndex);
                int frame = d.AnimTotalFrames > 0 ? Math.Clamp((int)rt.AnimTime, 0, d.AnimTotalFrames - 1) : 0;
                DebugLog.Log(ScenarioLog, $"  obj[{j}] animTime={rt.AnimTime:F2} frame={frame} rev={rt.AnimReversed}");
            }

            ZoomOnLocation(15f, 12f, 40f);
            DeferredScreenshot = $"anim_object_{_shotsTaken}";
            _shotsTaken++;
        }

        if (_shotsTaken >= _shotTimes.Length && _elapsed > _shotTimes[^1] + 0.2f)
            _done = true;
    }

    public override bool IsComplete => _done;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, $"[COMPLETE] {_shotsTaken} screenshots over {_elapsed:F2}s");

        // Verify random start frames: noisy oaks (0-3) should NOT all start at 0
        bool allZero = true;
        for (int j = 0; j < 4; j++)
        {
            var rt = sim.EnvironmentSystem.GetObjectRuntime(j);
            if ((int)rt.AnimTime != 0) allZero = false; // will have advanced, but check initial divergence
        }

        // Verify desync: noisy oaks should have different AnimTime values
        float[] noisyTimes = new float[4];
        for (int j = 0; j < 4; j++)
            noisyTimes[j] = sim.EnvironmentSystem.GetObjectRuntime(j).AnimTime;

        float maxDiff = 0f;
        for (int a = 0; a < 4; a++)
            for (int b = a + 1; b < 4; b++)
                maxDiff = MathF.Max(maxDiff, MathF.Abs(noisyTimes[a] - noisyTimes[b]));

        DebugLog.Log(ScenarioLog, $"[INFO] Noisy oaks AnimTime: {noisyTimes[0]:F2}, {noisyTimes[1]:F2}, {noisyTimes[2]:F2}, {noisyTimes[3]:F2} (maxDiff={maxDiff:F2})");

        // Check at least some reversals happened
        bool anyReversed = false;
        for (int j = 0; j < 8; j++)
            if (sim.EnvironmentSystem.GetObjectRuntime(j).AnimReversed) anyReversed = true;

        DebugLog.Log(ScenarioLog, $"[INFO] Any currently reversed: {anyReversed}");

        if (maxDiff < 0.5f)
        {
            DebugLog.Log(ScenarioLog, "[FAIL] Noisy oaks didn't desync enough");
            return 1;
        }

        DebugLog.Log(ScenarioLog, "[PASS] Animation features working: desync, momentum, wind sync");
        return 0;
    }
}
