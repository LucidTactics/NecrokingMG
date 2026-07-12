using System;
using Microsoft.Xna.Framework;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.Game;
using Necroking.GameSystems;
using Necroking.Lib;
using Necroking.Movement;
using Necroking.Render;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Visual validation for corpse-carry depth ordering: spawns 8 skeleton workers
/// in a circle, each facing outward (E, SE, S, SW, W, NW, N, NE), each
/// carrying a fresh corpse. The screenshot should show the corpse rendering
/// IN FRONT OF the worker for E/SE/S/SW/W (facing toward camera) and BEHIND
/// the worker for NW/N/NE (facing away from camera).
/// </summary>
public class CorpseCarryDepthScenario : ScenarioBase
{
    public override string Name => "corpse_carry_depth";

    private float _elapsed;
    private bool _complete;
    private bool _shotTaken;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Corpse Carry Depth Scenario ===");
        BackgroundColor = new Color(220, 215, 225);
        BloomOverride = new BloomSettings { Enabled = false };
        WeatherPreset = "clear";

        var units = sim.UnitsMut;
        var corpses = sim.CorpsesMut;

        // 8 worker positions around a circle, each facing radially outward.
        // World coords are Y-down, so 0=E, 90=S, 180=W, 270=N.
        var poses = new (string label, float facing)[]
        {
            ("E",   0f),
            ("SE",  45f),
            ("S",   90f),
            ("SW",  135f),
            ("W",   180f),
            ("NW",  225f),
            ("N",   270f),
            ("NE",  315f),
        };

        const float centerX = 16f;
        const float centerY = 16f;
        const float radius = 4f;

        int nextCorpseId = 1;
        for (int i = 0; i < poses.Length; i++)
        {
            float rad = poses[i].facing * MathF.PI / 180f;
            float dx = MathF.Cos(rad);
            float dy = MathF.Sin(rad);
            var pos = new Vec2(centerX + dx * radius, centerY + dy * radius);

            int workerIdx = units.AddUnit(pos, UnitType.Skeleton);
            units[workerIdx].AI = AIBehavior.IdleAtPoint;
            units[workerIdx].FacingAngle = poses[i].facing;

            // Spawn a dummy corpse and hand it to the worker so the carry-bag
            // renderer fires. CorpseInteractPhase=0 means "fully carried".
            var corpse = new Corpse
            {
                Position = pos,
                UnitType = UnitType.Soldier,
                UnitDefID = "soldier",
                FacingAngle = poses[i].facing,
                SpriteScale = 1f,
                CorpseID = nextCorpseId++,
                Bagged = true,
                DraggedByUnitID = units[workerIdx].Id,
            };
            corpses.Add(corpse);
            units[workerIdx].CarryingCorpseID = corpse.CorpseID;
            units[workerIdx].CorpseInteractPhase = 0;

            DebugLog.Log(ScenarioLog,
                $"Worker #{workerIdx} at ({pos.X:F1},{pos.Y:F1}) facing {poses[i].label} ({poses[i].facing}°) " +
                $"carrying corpse #{corpse.CorpseID}");
        }

        ZoomOnLocation(centerX, centerY - 1f, 32f);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        if (_complete) return;
        _elapsed += dt;
        if (DeferredScreenshot != null) return;

        if (!_shotTaken && _elapsed >= 0.5f)
        {
            DeferredScreenshot = "corpse_carry_depth";
            DebugLog.Log(ScenarioLog, "Screenshot taken — verify corpse-behind-unit for N/NE/NW.");
            _shotTaken = true;
            return;
        }
        if (_shotTaken && _elapsed >= 1.0f)
            _complete = true;
    }

    public override bool IsComplete => _complete;
    public override int OnComplete(Simulation sim) => 0;
}
