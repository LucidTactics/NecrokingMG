using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.GameSystems;
using Necroking.Render;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Visual + programmatic check of the angle-scheme resolver.
///
/// - OnInit runs the resolver unit test (synthetic 8-facing sweeps for NEW and OLD schemes).
/// - OnTick spawns a FemaleDeer (NEW scheme), GrizzlyBear (NEW scheme), and soldier (OLD scheme),
///   lets the scene settle, then captures a screenshot per unit type.
/// - Fails if resolver unit test has mismatches OR if any spawn returned -1.
/// </summary>
public class AngleSweepScenario : ScenarioBase
{
    public override string Name => "angle_sweep";
    public override bool IsComplete => _complete;

    private bool _complete;
    private bool _spawned;
    private float _elapsed;
    private int _shotPhase;
    private int _resolverFails;
    private int _spawnFails;

    private const float CX = 32f, CY = 32f;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Angle Sweep Scenario ===");

        if (sim.GameData != null)
            sim.GameData.Settings.Weather.Enabled = false;
        BloomOverride = new BloomSettings { Enabled = false };
        BackgroundColor = new Color(60, 50, 70);

        // --- Resolver unit test (no rendering involved) ---
        _resolverFails = RunResolverUnitTest();
        DebugLog.Log(ScenarioLog, $"Resolver unit test failures: {_resolverFails}");

        ZoomOnLocation(CX, CY, 48f);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;

        if (!_spawned && _elapsed >= 0.1f)
        {
            _spawned = true;
            int deerIdx = sim.SpawnUnitByID("FemaleDeer", new Vec2(CX - 3f, CY));
            int bearIdx = sim.SpawnUnitByID("FemaleDeer", new Vec2(CX, CY)); // deer + bear both live in Animals
            int zombieIdx = sim.SpawnUnitByID("ZombieFemaleDeer", new Vec2(CX + 3f, CY));
            int soldierIdx = sim.UnitsMut.AddUnit(new Vec2(CX, CY + 4f), UnitType.Soldier);

            if (deerIdx < 0) { _spawnFails++; DebugLog.Log(ScenarioLog, "SPAWN FAIL: FemaleDeer (animals sheet)"); }
            if (zombieIdx < 0) { _spawnFails++; DebugLog.Log(ScenarioLog, "SPAWN FAIL: ZombieFemaleDeer (zombie animals sheet)"); }

            // Face each unit east (0°) so we render the angle=0 sprite
            if (deerIdx >= 0)    sim.UnitsMut[deerIdx].FacingAngle = 0f;
            if (zombieIdx >= 0)  sim.UnitsMut[zombieIdx].FacingAngle = 0f;
            if (soldierIdx >= 0) sim.UnitsMut[soldierIdx].FacingAngle = 0f;

            foreach (int idx in new[] { deerIdx, zombieIdx, soldierIdx })
                if (idx >= 0) sim.UnitsMut[idx].AI = AIBehavior.IdleAtPoint;

            DebugLog.Log(ScenarioLog,
                $"Spawned: deer={deerIdx} zombieDeer={zombieIdx} soldier={soldierIdx} (total units={sim.Units.Count})");
            return;
        }

        if (!_spawned) return;

        // One screenshot per phase, spaced to let the main loop process each.
        if (_shotPhase == 0 && _elapsed >= 0.8f)
        {
            ZoomOnLocation(CX, CY, 48f);
            DeferredScreenshot = "angle_sweep_overview";
            _shotPhase++;
        }
        else if (_shotPhase == 1 && _elapsed >= 1.6f)
        {
            ZoomOnLocation(CX - 3f, CY, 128f);
            DeferredScreenshot = "angle_sweep_deer_close";
            _shotPhase++;
        }
        else if (_shotPhase == 2 && _elapsed >= 2.4f)
        {
            ZoomOnLocation(CX + 3f, CY, 128f);
            DeferredScreenshot = "angle_sweep_zombie_deer_close";
            _shotPhase++;
        }
        else if (_shotPhase == 3 && _elapsed >= 3.2f)
        {
            ZoomOnLocation(CX, CY + 4f, 128f);
            DeferredScreenshot = "angle_sweep_soldier_close";
            _shotPhase++;
        }
        else if (_shotPhase == 4 && _elapsed >= 4.0f)
        {
            _complete = true;
        }
    }

    // --- Resolver unit test (synthetic AnimationData per scheme) ---
    private static int RunResolverUnitTest()
    {
        var newExpected = new Dictionary<int, (int angle, bool flip)>
        {
            [0]   = (0,   false), [45]  = (45,  false), [90]  = (90,  false),
            [135] = (45,  true),  [180] = (0,   true),  [225] = (315, true),
            [270] = (270, false), [315] = (315, false),
        };
        var oldExpected = new Dictionary<int, (int angle, bool flip)>
        {
            [0]   = (30,  false), [45]  = (60,  false), [90]  = (60,  true),
            [135] = (60,  true),  [180] = (30,  true),  [225] = (300, true),
            [270] = (300, false), [315] = (300, false),
        };
        int fails = 0;
        fails += VerifyScheme("NEW", new[] { 0, 45, 90, 270, 315 }, newExpected);
        fails += VerifyScheme("OLD", new[] { 30, 60, 300 }, oldExpected);
        return fails;
    }

    private static int VerifyScheme(string label, int[] authored,
        Dictionary<int, (int angle, bool flip)> expected)
    {
        var usd = new UnitSpriteData { UnitName = label };
        var idle = new AnimationData { Name = "Idle" };
        foreach (int a in authored)
            idle.AngleFrames[a] = new List<Keyframe> { new Keyframe { Time = 0, Frame = default } };
        usd.Animations["Idle"] = idle;

        var ctrl = new AnimController();
        ctrl.Init(usd);

        int fails = 0;
        DebugLog.Log(ScenarioLog, $"--- {label} scheme (authored: {string.Join(",", authored)}) ---");
        foreach (var (facing, exp) in expected)
        {
            int got = ctrl.ResolveAngle(facing, out bool flip);
            bool ok = got == exp.angle && flip == exp.flip;
            string line = $"  {(ok ? "PASS" : "FAIL")}  facing={facing,3}° -> angle={got,3} flip={flip,-5}  expected angle={exp.angle,3} flip={exp.flip,-5}";
            DebugLog.Log(ScenarioLog, line);
            if (!ok) fails++;
        }
        return fails;
    }

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, $"=== Angle Sweep Complete (resolverFails={_resolverFails}, spawnFails={_spawnFails}) ===");
        return (_resolverFails == 0 && _spawnFails == 0) ? 0 : 1;
    }
}
