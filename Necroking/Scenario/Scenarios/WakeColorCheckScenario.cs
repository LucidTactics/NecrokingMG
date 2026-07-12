using System;
using Microsoft.Xna.Framework;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.GameSystems;
using Necroking.Lib;
using Necroking.Movement;
using Necroking.Render;
using Necroking.World;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Verifies the WadingWakeSystem renders particles at the calibrated foam
/// color. Sets up a shallow-water pond, spawns a unit, sends it walking
/// through the water so the trail emits, then captures screenshots at
/// fixed phases. After the run, a Python tool inspects the screenshot
/// pixels to confirm the brightest particle pixels land near the
/// shoreline foam target (159, 180, 176).
///
/// The check is observational not assertion-based — we always pass; the
/// goal is reproducible test conditions that produce a screenshot whose
/// pixel values the developer can analyze.
/// </summary>
public class WakeColorCheckScenario : ScenarioBase
{
    public override string Name => "wake_color_check";
    public override bool WantsGround => true;
    public override int GridSize => 48;

    private uint _unitId;
    private float _elapsed;
    private bool _complete;
    private int _shotPhase;

    // Pond center + size — chosen so the camera can frame a big chunk of
    // open water for clean particle sampling.
    private const float PondX = 24f;
    private const float PondY = 24f;
    private const float PondRadius = 10f;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Wake Color Check Scenario ===");
        if (GroundSystem == null) throw new InvalidOperationException("Requires WantsGround");

        // Don't override bloom — use whatever the user's game-settings
        // default is so the screenshot matches what they see in-game. The
        // perceived shoreline foam color in game depends on the bloom
        // tone mapping; calibrating against a non-bloom screenshot would
        // produce colors that don't match the user's visual reference.
        if (sim.GameData != null)
            sim.GameData.Settings.Weather.Enabled = false;

        int grassIdx = GroundSystem.FindType("grass");
        int shallowIdx = GroundSystem.FindType("shallow_water");
        if (shallowIdx < 0)
        {
            DebugLog.Log(ScenarioLog, "FAIL: shallow_water type not registered");
            _complete = true;
            return;
        }

        // Round pond of shallow water in the middle.
        int vw = GroundSystem.VertexW;
        int vh = GroundSystem.VertexH;
        for (int vy = 0; vy < vh; vy++)
        for (int vx = 0; vx < vw; vx++)
        {
            float dx = vx - PondX, dy = vy - PondY;
            float d = MathF.Sqrt(dx * dx + dy * dy);
            int type = d <= PondRadius ? shallowIdx : grassIdx;
            GroundSystem.SetVertex(vx, vy, (byte)type);
        }

        // Spawn a Skeleton in the pond moving across it so the trail
        // emits behind. Skeleton is humanoid (not quadruped) — uses the
        // scalar wading waterline, simpler reference for color sampling.
        // Slow speed → particles spread out (lower density per frame,
        // easier to sample individual particles).
        var startPos = new Vec2(PondX - PondRadius * 0.5f, PondY);
        var target = new Vec2(PondX + PondRadius * 0.5f, PondY);
        int idx = sim.UnitsMut.AddUnit(startPos, UnitType.Skeleton);
        sim.UnitsMut[idx].AI = AIBehavior.MoveToPoint;
        sim.UnitsMut[idx].MoveTarget = target;
        sim.UnitsMut[idx].Stats.CombatSpeed = 1.5f; // MaxSpeed derives from this via Locomotion.UpdateSpeeds
        _unitId = sim.UnitsMut[idx].Id;

        ZoomOnLocation(PondX, PondY, 80f);
        DebugLog.Log(ScenarioLog, $"Spawned skeleton id={_unitId} at ({startPos.X:F1},{startPos.Y:F1}), walking to ({target.X:F1},{target.Y:F1})");
    }

    public override void OnTick(Simulation sim, float dt)
    {
        if (_complete) return;
        _elapsed += dt;

        // Re-center camera on the unit each tick so it stays framed for
        // the screenshots. Use a generous zoom that captures both the
        // unit and the trail behind it.
        int uidx = FindByID(sim.UnitsMut, _unitId);
        if (uidx >= 0)
        {
            var p = sim.Units[uidx].Position;
            ZoomOnLocation(p.X, p.Y, 96f);
        }

        // After 2s of walking the entry splash is finished and a steady-
        // state trail wake is established behind the unit. Good sampling
        // moment.
        if (_shotPhase == 0 && _elapsed >= 2.0f)
        {
            DeferredScreenshot = "wake_color_check_trail";
            _shotPhase++;
            DebugLog.Log(ScenarioLog, $"Phase 0 screenshot at t={_elapsed:F2}s (steady trail)");
        }
        else if (_shotPhase == 1 && _elapsed >= 2.5f)
        {
            _complete = true;
        }
    }

    private static int FindByID(UnitArrays units, uint id)
    {
        for (int i = 0; i < units.Count; i++)
            if (units[i].Id == id) return i;
        return -1;
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "=== Wake Color Check Complete ===");
        DebugLog.Log(ScenarioLog, "Inspect log/screenshots/wake_color_check_*.png:");
        DebugLog.Log(ScenarioLog, "  - Brightest particle pixels should be near (159, 180, 176)");
        DebugLog.Log(ScenarioLog, "  - Darker interior pixels should be near (64, 88, 92)");
        DebugLog.Log(ScenarioLog, "  - Mid-gradient pixels fill the range");
        return 0;
    }
}
