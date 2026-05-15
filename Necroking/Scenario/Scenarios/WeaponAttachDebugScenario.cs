using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.GameSystems;
using Necroking.Movement;
using Necroking.Render;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Visual validation for the WeaponPointResolver. Spawns one necromancer per
/// sprite yaw plus one mid-attack, and re-centres the camera close on each
/// in turn so a single screenshot fills the screen with one unit. Cyan dot =
/// resolved weapon hilt (WeaponBase), yellow dot = resolved tip (WeaponTip),
/// yellow line connects them.
///
/// Use to verify exporter mount points line up with the visible staff, and
/// that the line tracks correctly across animation frames (not stuck on
/// frame 0).
/// </summary>
public class WeaponAttachDebugScenario : ScenarioBase
{
    public override string Name => "weapon_attach_debug";

    private float _phaseTimer;
    private int _phase;
    private bool _complete;
    private int _attackerIdx = -1;
    private bool _cameraSettled;

    private struct Pose
    {
        public string Label;
        public float WorldYaw;
        public Vec2 Pos;
    }

    private readonly List<Pose> _poses = new();

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Weapon Attach Debug Scenario ===");
        DebugLog.Log(ScenarioLog, "Each screenshot focuses on a single necromancer; cyan=hilt, yellow=tip.");

        ShowWeaponAttachDebug = true;
        // Light grey so the dark robe silhouette is unambiguous and the
        // coloured dots stand out.
        BackgroundColor = new Color(170, 165, 180);
        BloomOverride = new BloomSettings { Enabled = false };

        var units = sim.UnitsMut;

        // Idle necromancers, one per authored yaw, spread far enough that
        // the close-up camera framing for one doesn't catch the next.
        var yaws = new (string label, float worldYaw)[]
        {
            ("E",  0f),
            ("SE", 45f),
            ("S",  90f),
            ("N",  270f),
            ("NE", 315f),
        };

        for (int i = 0; i < yaws.Length; i++)
        {
            var pos = new Vec2(10f + i * 8f, 12f);
            int idx = units.AddUnit(pos, UnitType.Necromancer);
            units[idx].AI = AIBehavior.IdleAtPoint;
            units[idx].FacingAngle = yaws[i].worldYaw;
            _poses.Add(new Pose { Label = yaws[i].label, WorldYaw = yaws[i].worldYaw, Pos = pos });
            DebugLog.Log(ScenarioLog, $"Idle necromancer #{idx} yaw={yaws[i].worldYaw}° ({yaws[i].label}) at ({pos.X:F1},{pos.Y:F1})");
        }

        // Attacker necromancer faces SE (45°) so we hit the same sprite yaw
        // the user's editor screenshot was at — easiest comparison.
        _attackerIdx = units.AddUnit(new Vec2(10f, 30f), UnitType.Necromancer);
        units[_attackerIdx].AI = AIBehavior.AttackClosest;
        sim.SetNecromancerIndex(_attackerIdx);

        int dummyIdx = units.AddUnit(new Vec2(11.2f, 30.5f), UnitType.Soldier);
        units[dummyIdx].AI = AIBehavior.IdleAtPoint;
        units[dummyIdx].Stats.MaxHP = 9999;
        units[dummyIdx].Stats.HP = 9999;
        DebugLog.Log(ScenarioLog, $"Attacker necromancer #{_attackerIdx} at (10,30) vs dummy soldier #{dummyIdx}");

        // Camera gets re-aimed in OnTick at the start of each phase.
    }

    public override void OnTick(Simulation sim, float dt)
    {
        if (_complete) return;
        _phaseTimer += dt;

        // Wait for any pending screenshot to flush before advancing.
        if (DeferredScreenshot != null) return;

        // Each idle yaw gets two beats:
        //   1) settle: aim camera at the current pose, wait one tick so the
        //      camera override has been applied to _camera before we capture.
        //   2) shoot:  defer screenshot, advance to next pose.
        // Without the settle beat, ZoomOnLocation moves the camera in the
        // same Update that triggers DeferredScreenshot — so the captured
        // image is one phase ahead of the filename.
        if (_phase < _poses.Count)
        {
            if (!_cameraSettled)
            {
                var p = _poses[_phase];
                ZoomOnLocation(p.Pos.X, p.Pos.Y - 1.5f, 64f);
                _cameraSettled = true;
                _phaseTimer = 0f;
                return;
            }

            if (_phaseTimer >= 0.4f)
            {
                var p = _poses[_phase];
                DeferredScreenshot = $"weapon_attach_idle_{p.Label}";
                DebugLog.Log(ScenarioLog, $"Screenshot {DeferredScreenshot} (yaw={p.WorldYaw}°)");
                _phase++;
                _phaseTimer = 0f;
                _cameraSettled = false;
            }
            return;
        }

        // Attacker phase: aim camera at attacker once, then take 5
        // timestamped screenshots during the Attack1 swing.
        if (_phase == _poses.Count)
        {
            ZoomOnLocation(10.6f, 28.5f, 64f);
            _phase++;
            _phaseTimer = 0f;
            return;
        }

        int attackPhase = _phase - _poses.Count - 1; // 0..4 (subtract 1 for the camera-settle phase)
        if (attackPhase < 5)
        {
            // Spread the 5 attack screenshots across the Attack1 swing.
            float spacing = 0.15f;
            if (_phaseTimer >= spacing)
            {
                DeferredScreenshot = $"weapon_attach_attack_t{attackPhase + 1}";
                DebugLog.Log(ScenarioLog, $"Screenshot {DeferredScreenshot}");
                _phase++;
                _phaseTimer = 0f;
            }
            return;
        }

        _complete = true;
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, $"Weapon attach debug complete, {_phase} screenshots.");
        return 0;
    }
}
