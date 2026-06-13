using System;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Movement;
using Necroking.World;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Visual test for water-shed (exit-splash) depth sorting. A necromancer
/// wades through a shallow-water band and walks straight toward the camera
/// (+Y / foreground). On exiting the water it sheds drip particles at the
/// shoreline; as the body keeps advancing into the foreground it should
/// draw OVER those drips (they're anchored where they were shed, now behind
/// the unit in depth) — instead of the old behaviour where the drips always
/// rendered in front of the body.
///
/// Fixed camera framing the shoreline + the foreground lane so a sequence of
/// timed screenshots captures the unit walking down past its own shed water.
/// Review log/screenshots/water_shed_t*.png: early frames show drips in front
/// at the moment of shedding; later frames should show the body occluding
/// the drips left behind.
/// </summary>
public class WaterShedDepthScenario : ScenarioBase
{
    public override string Name => "water_shed_depth";
    public override bool WantsGround => true;
    public override int GridSize => 32;

    // Shallow-water band (horizontal strip spanning full width).
    private const int WaterY0 = 6;
    private const int WaterY1 = 11;

    private uint _unitId;
    private float _elapsed;
    private float _captureClock;
    private int _shotsTaken;
    private bool _complete;

    // Capture cadence: a screenshot every CaptureInterval seconds.
    private const float CaptureInterval = 0.15f;
    private const int MaxShots = 24;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Water Shed Depth Scenario ===");
        if (GroundSystem == null) throw new InvalidOperationException("Requires WantsGround");

        BloomOverride = new Data.Registries.BloomSettings { Enabled = false };
        // Fog of war darkens the unexplored centre to a black box — kill it so
        // the unit and its shed drips are visible for the depth comparison.
        if (sim.GameData != null) sim.GameData.Settings.FogOfWar.Mode = 0;

        int shallowIdx = GroundSystem.FindType("shallow_water");
        DebugLog.Log(ScenarioLog, $"shallow_water type index = {shallowIdx}");
        if (shallowIdx < 0) { DebugLog.Log(ScenarioLog, "FAIL: shallow_water not registered"); _complete = true; return; }

        // Paint a full-width shallow-water band Y0..Y1. +1 on the upper vertex
        // bound so every tile row in the band has all 4 corners as water.
        int vw = GroundSystem.VertexW;
        int vh = GroundSystem.VertexH;
        for (int vy = WaterY0; vy <= WaterY1 + 1 && vy < vh; vy++)
            for (int vx = 0; vx < vw; vx++)
                GroundSystem.SetVertex(vx, vy, (byte)shallowIdx);

        // Necromancer starts inside the water and walks straight DOWN-screen
        // (+Y, toward the camera) out onto dry land. Slow so the shed drips
        // get several frames of the body advancing past them.
        var startPos = new Vec2(16f, 9f);
        var target = new Vec2(16f, 18f);
        int idx = sim.UnitsMut.AddUnit(startPos, UnitType.Necromancer);
        sim.UnitsMut[idx].AI = AIBehavior.MoveToPoint;
        sim.UnitsMut[idx].MoveTarget = target;
        // CombatSpeed (not MaxSpeed — that's overwritten from Stats each tick).
        // Moderate: fast enough to clear the band and advance a few units onto
        // dry land, slow enough that the body's march past the shed drips spans
        // several captured frames within the drips' ~1s lifetime.
        sim.UnitsMut[idx].Stats.CombatSpeed = 2.0f;
        _unitId = sim.UnitsMut[idx].Id;

        // Fixed camera zoomed tight on the shoreline (Y~11) so the shed drips
        // and the body advancing past them are both large enough to read.
        ZoomOnLocation(16f, 12.0f, 130f);

        DebugLog.Log(ScenarioLog, $"Necromancer id={_unitId} start (16,8) -> (16,24), water band Y={WaterY0}..{WaterY1}.");
    }

    private static int FindByID(UnitArrays units, uint id)
    {
        for (int i = 0; i < units.Count; i++)
            if (units[i].Id == id) return i;
        return -1;
    }

    public override void OnTick(Simulation sim, float dt)
    {
        if (_complete) return;
        _elapsed += dt;

        // Don't advance the capture clock while a screenshot is pending.
        if (DeferredScreenshot != null) return;

        int idx = FindByID(sim.UnitsMut, _unitId);
        float y = idx >= 0 ? sim.Units[idx].Position.Y : -1f;

        // Only start the capture sequence once the unit is at the shoreline
        // (~Y11) so every shot covers the exit-and-advance window where the
        // shed drips and the advancing body overlap on screen.
        if (_shotsTaken == 0 && y < 11.0f) { _captureClock = 0f; return; }
        _captureClock += dt;

        if (_shotsTaken < MaxShots && _captureClock >= _shotsTaken * CaptureInterval)
        {
            DeferredScreenshot = $"water_shed_t{_shotsTaken:D2}";
            DebugLog.Log(ScenarioLog, $"Shot {_shotsTaken:D2} at t={_elapsed:F2}s unitY={y:F2}");
            _shotsTaken++;
            return;
        }

        if (_shotsTaken >= MaxShots)
            _complete = true;
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, $"Water shed depth complete, {_shotsTaken} screenshots.");
        return 0;
    }
}
