using System;
using Microsoft.Xna.Framework;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.GameSystems;
using Necroking.Movement;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Physics test: Unit A launched into B, B knocked into C (chain reaction).
/// All 3 must experience knockdown.
/// </summary>
public class PhysicsChainScenario : ScenarioBase
{
    public override string Name => "physics_chain";
    public override bool IsComplete => _complete;

    private bool _complete;
    private float _elapsed;
    private uint _idA, _idB, _idC;
    private bool _launched;
    private bool[] _wasKnockedDown = new bool[3];

    public override void OnInit(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "=== Physics Chain Reaction Test ===");
        DebugLog.Log(ScenarioLog, "A launched toward B, B knocked into C. All 3 must knockdown.");

        if (sim.GameData != null)
            sim.GameData.Settings.Weather.Enabled = false;
        BloomOverride = new BloomSettings { Enabled = false };
        BackgroundColor = new Color(40, 35, 50);
        ZoomOnLocation(15, 15, 30);

        // A at left, B in middle, C at right — tight spacing for chain collision
        int a = sim.UnitsMut.AddUnit(new Vec2(12, 15), UnitType.Soldier);
        int b = sim.UnitsMut.AddUnit(new Vec2(14.5f, 15), UnitType.Soldier);
        int c = sim.UnitsMut.AddUnit(new Vec2(17, 15), UnitType.Soldier);

        // Give all high HP so they survive
        foreach (int idx in new[] { a, b, c })
        {
            sim.UnitsMut[idx].Stats.HP = 200;
            sim.UnitsMut[idx].Stats.MaxHP = 200;
        }

        _idA = sim.Units[a].Id;
        _idB = sim.Units[b].Id;
        _idC = sim.Units[c].Id;

        DebugLog.Log(ScenarioLog, $"A id={_idA} at (12,15), B id={_idB} at (15,15), C id={_idC} at (18,15)");
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;

        int a = FindByID(sim.Units, _idA);
        int b = FindByID(sim.Units, _idB);
        int c = FindByID(sim.Units, _idC);

        // Launch A toward B at 0.5s
        if (!_launched && _elapsed >= 0.5f)
        {
            _launched = true;
            if (a >= 0)
            {
                bool ok = sim.Physics.ApplyImpulse(sim.UnitsMut, a,
                    new Vec2(1f, 0f), 25f, 5f); // strong horizontal push toward B
                DebugLog.Log(ScenarioLog, $"[{_elapsed:F2}s] Launched A toward B, ok={ok}");
            }
            DeferredScreenshot = "physics_chain_launch";
        }

        // Track knockdowns
        if (a >= 0 && (sim.Units[a].KnockdownTimer > 0f || sim.Units[a].InPhysics) && !_wasKnockedDown[0])
        {
            _wasKnockedDown[0] = true;
            DebugLog.Log(ScenarioLog, $"[{_elapsed:F2}s] A knocked down/flying");
        }
        if (b >= 0 && (sim.Units[b].KnockdownTimer > 0f || sim.Units[b].InPhysics) && !_wasKnockedDown[1])
        {
            _wasKnockedDown[1] = true;
            DebugLog.Log(ScenarioLog, $"[{_elapsed:F2}s] B knocked down/flying (chain hit!)");
            DeferredScreenshot = "physics_chain_b_hit";
        }
        if (c >= 0 && (sim.Units[c].KnockdownTimer > 0f || sim.Units[c].InPhysics) && !_wasKnockedDown[2])
        {
            _wasKnockedDown[2] = true;
            DebugLog.Log(ScenarioLog, $"[{_elapsed:F2}s] C knocked down/flying (double chain!)");
            DeferredScreenshot = "physics_chain_c_hit";
        }

        // Log periodically
        if (_launched && (int)(_elapsed * 2) > (int)((_elapsed - dt) * 2))
        {
            string stateA = a >= 0 ? UnitState(sim.Units[a]) : "dead";
            string stateB = b >= 0 ? UnitState(sim.Units[b]) : "dead";
            string stateC = c >= 0 ? UnitState(sim.Units[c]) : "dead";
            DebugLog.Log(ScenarioLog, $"[{_elapsed:F2}s] A:{stateA} B:{stateB} C:{stateC}");
        }

        // All 3 knocked down at some point = pass
        if (_wasKnockedDown[0] && _wasKnockedDown[1] && _wasKnockedDown[2])
        {
            DebugLog.Log(ScenarioLog, $"[{_elapsed:F2}s] All 3 experienced knockdown!");
            _complete = true;
        }

        if (_elapsed > 10f) { DebugLog.Log(ScenarioLog, "TIMEOUT"); _complete = true; }
    }

    public override int OnComplete(Simulation sim)
    {
        bool pass = _wasKnockedDown[0] && _wasKnockedDown[1] && _wasKnockedDown[2];
        DebugLog.Log(ScenarioLog, $"=== Result: A={_wasKnockedDown[0]} B={_wasKnockedDown[1]} C={_wasKnockedDown[2]} → {(pass ? "PASS" : "FAIL")} ===");
        return pass ? 0 : 1;
    }

    private static string UnitState(Unit u)
    {
        if (u.InPhysics) return $"flying(Z={u.Z:F1})";
        if (u.KnockdownTimer > 0f) return $"knocked({u.KnockdownTimer:F1}s)";
        if (u.StandupTimer > 0f) return "standup";
        return "standing";
    }

    private static int FindByID(UnitArrays units, uint id)
    {
        for (int i = 0; i < units.Count; i++)
            if (units[i].Id == id && units[i].Alive) return i;
        return -1;
    }
}
