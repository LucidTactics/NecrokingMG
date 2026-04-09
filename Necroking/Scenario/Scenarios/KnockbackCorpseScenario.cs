using System;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Movement;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Tests that corpses continue their knockback arc when a unit dies mid-flight.
/// Fires a knockback fireball (fireball_kb) at a cluster of weak enemies.
/// Validates that corpses land at positions different from where the units died.
/// </summary>
public class KnockbackCorpseScenario : ScenarioBase
{
    public override string Name => "knockback_corpse";
    private float _elapsed;
    private bool _complete;
    private int _phase;
    private float _phaseTimer;
    private readonly Vec2 _clusterCenter = new(15f, 15f);
    private int _screenshotIdx;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Knockback Corpse Test ===");
        DebugLog.Log(ScenarioLog, "Testing that corpses fly through the air when units die mid-knockback");

        var units = sim.UnitsMut;

        // Spawn necromancer far away (caster)
        int necroIdx = units.AddUnit(new Vec2(5f, 15f), UnitType.Necromancer);
        units[necroIdx].AI = AIBehavior.PlayerControlled;
        sim.SetNecromancerIndex(necroIdx);

        // Spawn cluster of 6 weak enemies at the target area
        for (int i = 0; i < 6; i++)
        {
            float angle = i * 60f * MathF.PI / 180f;
            var pos = _clusterCenter + new Vec2(MathF.Cos(angle) * 0.8f, MathF.Sin(angle) * 0.8f);
            int idx = units.AddUnit(pos, UnitType.Skeleton);
            units[idx].AI = AIBehavior.IdleAtPoint;
            // Make them very weak so the fireball kills them
            units[idx].Stats.HP = 1;
            units[idx].Stats.MaxHP = 1;
            DebugLog.Log(ScenarioLog, $"  Enemy {i}: pos=({pos.X:F1},{pos.Y:F1}) HP=1");
        }

        DebugLog.Log(ScenarioLog, $"Total units: {units.Count}");
        ZoomOnLocation(15f, 15f, 40f);

        _phase = 0;
        _phaseTimer = 0.5f; // brief delay before firing
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;
        _phaseTimer -= dt;

        switch (_phase)
        {
            case 0: // Wait, then fire knockback fireball
                if (_phaseTimer <= 0f)
                {
                    int necroIdx = sim.NecromancerIndex;
                    if (necroIdx >= 0)
                    {
                        var from = sim.Units[necroIdx].Position;
                        DebugLog.Log(ScenarioLog, $"Firing fireball_kb from ({from.X:F1},{from.Y:F1}) to ({_clusterCenter.X:F1},{_clusterCenter.Y:F1})");

                        // Spawn fireball with high damage and tag with fireball_kb for knockback
                        sim.Projectiles.SpawnFireball(from, _clusterCenter, Faction.Undead,
                            sim.Units[necroIdx].Id, 100, 4f, "Nether Blast KB");
                        var projs = sim.Projectiles.Projectiles;
                        if (projs.Count > 0)
                            projs[projs.Count - 1].SpellID = "fireball_kb";
                    }
                    _phase = 1;
                    _phaseTimer = 0.5f; // wait for projectile to arrive
                    DebugLog.Log(ScenarioLog, "Phase 1: waiting for impact...");
                }
                break;

            case 1: // Wait for impact, then start watching corpses
                if (_phaseTimer <= 0f)
                {
                    // Log corpse state right after impact
                    DebugLog.Log(ScenarioLog, $"Post-impact: {sim.Corpses.Count} corpses");
                    int flyingCorpses = 0;
                    for (int i = 0; i < sim.Corpses.Count; i++)
                    {
                        var c = sim.Corpses[i];
                        DebugLog.Log(ScenarioLog, $"  Corpse {c.CorpseID}: pos=({c.Position.X:F1},{c.Position.Y:F1}) " +
                            $"Z={c.Z:F2} InPhysics={c.InPhysics} vel=({c.VelocityXY.X:F1},{c.VelocityXY.Y:F1}) velZ={c.VelocityZ:F1}");
                        if (c.InPhysics) flyingCorpses++;
                    }
                    DebugLog.Log(ScenarioLog, $"Flying corpses: {flyingCorpses}");

                    if (flyingCorpses > 0)
                        DeferredScreenshot = $"knockback_corpse_flying_{_screenshotIdx++}";

                    _phase = 2;
                    _phaseTimer = 3f; // wait for corpses to land
                    DebugLog.Log(ScenarioLog, "Phase 2: waiting for corpses to land...");
                }
                break;

            case 2: // Wait for all corpses to land
            {
                // Take a screenshot while corpses are mid-flight
                if (_phaseTimer > 2f && _phaseTimer - dt <= 2f)
                    DeferredScreenshot = $"knockback_corpse_midair_{_screenshotIdx++}";

                if (_phaseTimer <= 0f)
                {
                    DebugLog.Log(ScenarioLog, "Phase 3: validation");
                    _complete = true;
                }
                break;
            }
        }

        if (_elapsed > 15f)
        {
            DebugLog.Log(ScenarioLog, "TIMEOUT — forcing completion");
            _complete = true;
        }
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "=== Validation ===");

        int totalCorpses = sim.Corpses.Count;
        int corpsesFarFromCluster = 0;
        int corpsesStillFlying = 0;

        for (int i = 0; i < totalCorpses; i++)
        {
            var c = sim.Corpses[i];
            float distFromCluster = (c.Position - _clusterCenter).Length();
            bool landed = !c.InPhysics && c.Z <= 0f;
            DebugLog.Log(ScenarioLog, $"  Corpse {c.CorpseID}: final pos=({c.Position.X:F1},{c.Position.Y:F1}) " +
                $"dist={distFromCluster:F1} Z={c.Z:F2} landed={landed}");

            if (distFromCluster > 2f) corpsesFarFromCluster++;
            if (c.InPhysics) corpsesStillFlying++;
        }

        bool hasCorpses = totalCorpses >= 3; // at least some enemies died
        bool corpsesLanded = corpsesStillFlying == 0;
        bool corpsesMoved = corpsesFarFromCluster > 0; // at least one corpse flew away from cluster

        DebugLog.Log(ScenarioLog, $"Corpses total: {totalCorpses} (need >= 3) → {(hasCorpses ? "PASS" : "FAIL")}");
        DebugLog.Log(ScenarioLog, $"All landed: {corpsesStillFlying == 0} → {(corpsesLanded ? "PASS" : "FAIL")}");
        DebugLog.Log(ScenarioLog, $"Corpses moved by knockback: {corpsesFarFromCluster}/{totalCorpses} → {(corpsesMoved ? "PASS" : "FAIL")}");

        DeferredScreenshot = $"knockback_corpse_final_{_screenshotIdx++}";

        bool pass = hasCorpses && corpsesLanded && corpsesMoved;
        DebugLog.Log(ScenarioLog, $"Overall: {(pass ? "PASS" : "FAIL")}");
        return pass ? 0 : 1;
    }
}
