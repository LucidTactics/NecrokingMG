using System;
using Microsoft.Xna.Framework;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.GameSystems;
using Necroking.Movement;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Physics test: 5 soldiers hit by radial explosion, all fly outward and recover.
/// </summary>
public class PhysicsMultiScenario : ScenarioBase
{
    public override string Name => "physics_multi";
    public override bool IsComplete => _complete;

    private bool _complete;
    private float _elapsed;
    private uint[] _unitIDs = new uint[5];
    private bool _exploded;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "=== Physics Multi Unit Test ===");
        DebugLog.Log(ScenarioLog, "Spawn 5 soldiers in cluster, radial explosion, all recover");

        if (sim.GameData != null)
            sim.GameData.Settings.Weather.Enabled = false;
        BloomOverride = new BloomSettings { Enabled = false };
        BackgroundColor = new Color(40, 35, 50);
        ZoomOnLocation(15, 15, 30);

        // Cluster of 5 soldiers around center
        Vec2[] positions = {
            new(15, 15), new(15.5f, 14.5f), new(14.5f, 14.5f),
            new(15.5f, 15.5f), new(14.5f, 15.5f)
        };
        for (int i = 0; i < 5; i++)
        {
            int idx = sim.UnitsMut.AddUnit(positions[i], UnitType.Soldier);
            sim.UnitsMut[idx].Stats.HP = 100;
            sim.UnitsMut[idx].Stats.MaxHP = 100;
            _unitIDs[i] = sim.Units[idx].Id;
        }
        DebugLog.Log(ScenarioLog, $"Spawned 5 soldiers around (15,15)");
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;

        // Explode at 0.5s
        if (!_exploded && _elapsed >= 0.5f)
        {
            _exploded = true;
            int launched = sim.Physics.ApplyRadialImpulse(sim.UnitsMut,
                new Vec2(15, 15), 3f, 20f, 10f, Faction.Undead);
            DebugLog.Log(ScenarioLog, $"[{_elapsed:F2}s] Radial explosion! Launched {launched} units");
            DeferredScreenshot = "physics_multi_explode";
        }

        // Screenshot at apex
        if (_exploded && _elapsed >= 1.2f && _elapsed < 1.3f)
            DeferredScreenshot = "physics_multi_apex";

        // Screenshot after landing
        if (_exploded && _elapsed >= 3.5f && _elapsed < 3.6f)
            DeferredScreenshot = "physics_multi_landed";

        // Check if all recovered
        if (_exploded && _elapsed >= 2f)
        {
            int standing = 0;
            for (int i = 0; i < 5; i++)
            {
                int idx = FindByID(sim.Units, _unitIDs[i]);
                if (idx >= 0 && sim.Units[idx].Alive && !sim.Units[idx].InPhysics
                    && !GameSystems.BuffSystem.IsKnockedDown(sim.Units[idx]) && sim.Units[idx].StandupTimer <= 0f)
                    standing++;
            }

            if (standing == 5)
            {
                DebugLog.Log(ScenarioLog, $"[{_elapsed:F2}s] All 5 standing!");
                DeferredScreenshot = "physics_multi_recovered";
                _complete = true;
            }
        }

        // Log periodically
        if (_exploded && (int)(_elapsed * 2) > (int)((_elapsed - dt) * 2))
        {
            int airborne = 0, knocked = 0, standing = 0;
            for (int i = 0; i < 5; i++)
            {
                int idx = FindByID(sim.Units, _unitIDs[i]);
                if (idx < 0) continue;
                if (sim.Units[idx].InPhysics) airborne++;
                else if (GameSystems.BuffSystem.IsKnockedDown(sim.Units[idx]) || sim.Units[idx].StandupTimer > 0f) knocked++;
                else standing++;
            }
            DebugLog.Log(ScenarioLog, $"[{_elapsed:F2}s] airborne={airborne} knocked={knocked} standing={standing}");
        }

        if (_elapsed > 10f) { DebugLog.Log(ScenarioLog, "TIMEOUT"); _complete = true; }
    }

    public override int OnComplete(Simulation sim)
    {
        int standing = 0;
        for (int i = 0; i < 5; i++)
        {
            int idx = FindByID(sim.Units, _unitIDs[i]);
            if (idx >= 0 && sim.Units[idx].Alive && !sim.Units[idx].InPhysics
                && !GameSystems.BuffSystem.IsKnockedDown(sim.Units[idx]) && sim.Units[idx].StandupTimer <= 0f)
                standing++;
        }
        bool pass = standing == 5;
        DebugLog.Log(ScenarioLog, $"=== Result: {standing}/5 standing → {(pass ? "PASS" : "FAIL")} ===");
        return pass ? 0 : 1;
    }

    private static int FindByID(UnitArrays units, uint id)
    {
        for (int i = 0; i < units.Count; i++)
            if (units[i].Id == id && units[i].Alive) return i;
        return -1;
    }
}
