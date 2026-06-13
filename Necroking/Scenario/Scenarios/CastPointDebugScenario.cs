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
/// Diagnostic for the spell "casting point". The user reports the casting glow
/// (buff_4 "CastingEffect", a weapon particle that spawns between WeaponBase and
/// WeaponTip at t in [0.5,1.0]) sits below the unit's visible hand.
///
/// This spawns one Wretched per authored yaw, applies the real casting buff so
/// the purple glow renders, and turns on the weapon-attach debug overlay
/// (cyan = resolved WeaponBase/hilt, yellow = resolved WeaponTip). A single
/// tightly-zoomed screenshot per yaw lets us compare three things at once:
///   - the rendered hand on the sprite
///   - the resolved base/tip dots (where the glow line lives)
///   - the actual purple glow particles
/// so we can see whether the glow lands on the hand or is offset.
/// </summary>
public class CastPointDebugScenario : ScenarioBase
{
    public override string Name => "cast_point_debug";

    private float _phaseTimer;
    private int _phase;
    private bool _complete;
    private bool _cameraSettled;

    private struct Pose { public string Label; public float WorldYaw; public Vec2 Pos; }
    private readonly List<Pose> _poses = new();

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Cast Point Debug Scenario ===");
        DebugLog.Log(ScenarioLog, "Wretched + casting glow (buff_4). cyan=WeaponBase, yellow=WeaponTip, purple=glow.");

        ShowWeaponAttachDebug = true;
        // Dark purple-blue background so the purple glow AND the dark robe both
        // read clearly. Bloom off so we see raw particle positions.
        BackgroundColor = new Color(30, 28, 45);
        BloomOverride = new BloomSettings { Enabled = false };

        var units = sim.UnitsMut;

        var yaws = new (string label, float worldYaw)[]
        {
            ("E",  0f),
            ("SE", 45f),
            ("S",  90f),
            ("N",  270f),
            ("NE", 315f),
        };

        var castBuff = sim.GameData?.Buffs.Get("buff_4");
        if (castBuff == null)
            DebugLog.Log(ScenarioLog, "WARNING: buff_4 not found in buff registry!");

        for (int i = 0; i < yaws.Length; i++)
        {
            var pos = new Vec2(10f + i * 8f, 12f);
            int idx = units.AddUnit(pos, UnitType.Necromancer);
            units[idx].UnitDefID = "wretched";          // force the early evolution form
            units[idx].AI = AIBehavior.IdleAtPoint;
            units[idx].FacingAngle = yaws[i].worldYaw;
            if (castBuff != null)
                BuffSystem.ApplyBuffWithDuration(units, idx, castBuff, 9999f, sim.GameData);
            _poses.Add(new Pose { Label = yaws[i].label, WorldYaw = yaws[i].worldYaw, Pos = pos });
            DebugLog.Log(ScenarioLog,
                $"Wretched #{idx} yaw={yaws[i].worldYaw}° ({yaws[i].label}) at ({pos.X:F1},{pos.Y:F1}) buff={(castBuff != null)}");
        }
    }

    public override void OnTick(Simulation sim, float dt)
    {
        if (_complete) return;
        _phaseTimer += dt;
        if (DeferredScreenshot != null) return;

        if (_phase < _poses.Count)
        {
            if (!_cameraSettled)
            {
                var p = _poses[_phase];
                // Zoom in tight on the upper body / hand so the hand-vs-glow
                // offset is easy to measure. Aim slightly above feet.
                ZoomOnLocation(p.Pos.X, p.Pos.Y - 1.0f, 110f);
                _cameraSettled = true;
                _phaseTimer = 0f;
                return;
            }
            // Let particles accumulate for a beat so the glow blob is populated.
            if (_phaseTimer >= 0.7f)
            {
                var p = _poses[_phase];
                DeferredScreenshot = $"cast_point_{p.Label}";
                DebugLog.Log(ScenarioLog, $"Screenshot {DeferredScreenshot} (yaw={p.WorldYaw}°)");
                _phase++;
                _phaseTimer = 0f;
                _cameraSettled = false;
            }
            return;
        }

        _complete = true;
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, $"Cast point debug complete, {_phase} screenshots.");
        return 0;
    }
}
