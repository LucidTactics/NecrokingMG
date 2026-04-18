using System;
using Microsoft.Xna.Framework;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.GameSystems;
using Necroking.Movement;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Physics test: single unit hit by explosion, goes flying, lands, stands up.
/// </summary>
public class PhysicsSingleScenario : ScenarioBase
{
    public override string Name => "physics_single";
    public override bool IsComplete => _complete;

    private bool _complete;
    private float _elapsed;
    private uint _unitID;
    private Vec2 _startPos;
    private bool _launched;
    private bool _wasAirborne;
    private bool _landed;
    private bool _stoodUp;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "=== Physics Single Unit Test ===");
        DebugLog.Log(ScenarioLog, "Spawn soldier, apply explosion, verify flight→land→standup");

        if (sim.GameData != null)
            sim.GameData.Settings.Weather.Enabled = false;
        BloomOverride = new BloomSettings { Enabled = false };
        BackgroundColor = new Color(40, 35, 50);
        ZoomOnLocation(15, 15, 40);

        _startPos = new Vec2(15, 15);
        int idx = sim.UnitsMut.AddUnit(_startPos, UnitType.Soldier);
        sim.UnitsMut[idx].Stats.HP = 100;
        sim.UnitsMut[idx].Stats.MaxHP = 100;
        _unitID = sim.Units[idx].Id;
        DebugLog.Log(ScenarioLog, $"Spawned soldier at {_startPos}, id={_unitID}");
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;

        int idx = FindByID(sim.Units, _unitID);
        if (idx < 0) { _complete = true; return; }

        var u = sim.Units[idx];

        // Launch at 0.5s
        if (!_launched && _elapsed >= 0.5f)
        {
            _launched = true;
            bool ok = sim.Physics.ApplyImpulse(sim.UnitsMut, idx,
                new Vec2(1f, 0.5f), 15f, 8f);
            DebugLog.Log(ScenarioLog, $"[{_elapsed:F2}s] Applied impulse, launched={ok}");
            DeferredScreenshot = "physics_single_launch";
        }

        // Track state transitions
        if (_launched && u.Z > 0.5f && !_wasAirborne)
        {
            _wasAirborne = true;
            DebugLog.Log(ScenarioLog, $"[{_elapsed:F2}s] Airborne! Z={u.Z:F2} vel=({u.Velocity.X:F1},{u.Velocity.Y:F1})");
            DeferredScreenshot = "physics_single_airborne";
        }

        if (_wasAirborne && !_landed && u.Z <= 0f && !u.InPhysics)
        {
            _landed = true;
            float dist = (u.Position - _startPos).Length();
            DebugLog.Log(ScenarioLog, $"[{_elapsed:F2}s] Landed! dist={dist:F1} knocked={u.Incap.IsLocked}");
            DeferredScreenshot = "physics_single_landed";
        }

        if (_landed && !_stoodUp && !u.Incap.IsLocked && true)
        {
            _stoodUp = true;
            DebugLog.Log(ScenarioLog, $"[{_elapsed:F2}s] Stood up! pos=({u.Position.X:F1},{u.Position.Y:F1})");
            DeferredScreenshot = "physics_single_standup";
        }

        // Log periodically
        if (_launched && (int)(_elapsed * 4) > (int)((_elapsed - dt) * 4))
        {
            DebugLog.Log(ScenarioLog, $"[{_elapsed:F2}s] Z={u.Z:F2} inPhysics={u.InPhysics} " +
                $"knocked={u.Incap.IsLocked} standup={u.Incap.RecoverTimer:F1} " +
                $"pos=({u.Position.X:F1},{u.Position.Y:F1})");
        }

        if (_stoodUp) _complete = true;
        if (_elapsed > 10f)
        {
            DebugLog.Log(ScenarioLog, "TIMEOUT");
            _complete = true;
        }
    }

    public override int OnComplete(Simulation sim)
    {
        bool pass = _wasAirborne && _landed && _stoodUp;
        DebugLog.Log(ScenarioLog, $"=== Result: airborne={_wasAirborne} landed={_landed} stoodUp={_stoodUp} → {(pass ? "PASS" : "FAIL")} ===");
        return pass ? 0 : 1;
    }

    private static int FindByID(UnitArrays units, uint id)
    {
        for (int i = 0; i < units.Count; i++)
            if (units[i].Id == id && units[i].Alive) return i;
        return -1;
    }
}
