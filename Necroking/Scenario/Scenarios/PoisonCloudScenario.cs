using System;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.GameSystems;
using Necroking.Movement;

namespace Necroking.Scenario.Scenarios;

public class PoisonCloudScenario : ScenarioBase
{
    public override string Name => "poison_cloud";

    private float _elapsed;
    private bool _complete;
    private bool _cloudSpawned;
    private float _spawnTimer = 0.2f;
    private int _initialEnemyCount;
    private int _screenshotPhase;
    private int _peakPoisoned;
    private int _peakPlagued;
    private int _peakDead;

    private const float CX = 32f, CY = 32f;
    private const int EnemyCount = 10;
    private const int CorpseCount = 4;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Poison Cloud Scenario ===");
        DebugLog.Log(ScenarioLog, "Testing: single cloud puff flipbook rendering");

        // Disable weather and bloom for clean visual test
        if (sim.GameData?.Settings.Weather != null)
            sim.GameData.Settings.Weather.Enabled = false;
        BloomOverride = new BloomSettings { Enabled = true, Threshold = 0.9f, Intensity = 0.8f, Scatter = 0.5f };

        var units = sim.UnitsMut;

        // Spawn a few enemies so we can see the cloud against something
        for (int i = 0; i < EnemyCount; i++)
        {
            float angle = i * MathF.PI * 2f / EnemyCount;
            float dist = 2f + (i % 3) * 0.8f;
            var pos = new Vec2(CX + MathF.Cos(angle) * dist, CY + MathF.Sin(angle) * dist);
            int idx = units.AddUnit(pos, UnitType.Soldier);
            units[idx].AI = AIBehavior.IdleAtPoint;
        }
        _initialEnemyCount = EnemyCount;

        // Place corpses around the cluster
        for (int i = 0; i < CorpseCount; i++)
        {
            float angle = i * MathF.PI * 2f / CorpseCount + 0.3f;
            float dist = 3.5f;
            var pos = new Vec2(CX + MathF.Cos(angle) * dist, CY + MathF.Sin(angle) * dist);
            sim.CorpsesMut.Add(new Corpse
            {
                Position = pos,
                UnitType = UnitType.Skeleton,
                UnitDefID = "skeleton",
                CorpseID = i,
                FacingAngle = 90f,
                SpriteScale = 1f
            });
        }

        DebugLog.Log(ScenarioLog, "Weather disabled, bloom enabled");
        ZoomOnLocation(CX, CY, 40f);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;

        // Spawn cloud after brief delay
        if (!_cloudSpawned)
        {
            _spawnTimer -= dt;
            if (_spawnTimer <= 0f)
            {
                var spellDef = sim.GameData?.Spells.Get("poison_cloud");
                if (spellDef != null)
                {
                    sim.PoisonClouds.SpawnCloud(new Vec2(CX, CY), spellDef, Faction.Undead);
                    _cloudSpawned = true;
                    DebugLog.Log(ScenarioLog, $"[{_elapsed:F1}s] Spawned poison cloud");
                }
                else
                {
                    DebugLog.Log(ScenarioLog, "ERROR: poison_cloud spell not found!");
                    _complete = true;
                }
            }
            return;
        }

        // Screenshots at key moments
        if (_screenshotPhase == 0 && _elapsed >= 1.0f)
        {
            ZoomOnLocation(CX, CY, 60f);
            DeferredScreenshot = "cloud_single_early";
            DebugLog.Log(ScenarioLog, $"[{_elapsed:F1}s] Screenshot: early (zoom 60)");
            _screenshotPhase++;
        }
        else if (_screenshotPhase == 1 && _elapsed >= 2.5f)
        {
            ZoomOnLocation(CX, CY, 100f);
            DeferredScreenshot = "cloud_single_closeup";
            DebugLog.Log(ScenarioLog, $"[{_elapsed:F1}s] Screenshot: closeup (zoom 100)");
            _screenshotPhase++;
        }
        else if (_screenshotPhase == 2 && _elapsed >= 5.0f)
        {
            ZoomOnLocation(CX, CY, 40f);
            DeferredScreenshot = "cloud_single_spread";
            DebugLog.Log(ScenarioLog, $"[{_elapsed:F1}s] Screenshot: spread phase (zoom 40)");
            _screenshotPhase++;
        }
        else if (_screenshotPhase == 3 && _elapsed >= 8.0f)
        {
            DeferredScreenshot = "cloud_single_late";
            DebugLog.Log(ScenarioLog, $"[{_elapsed:F1}s] Screenshot: late phase");
            _screenshotPhase++;
        }

        // Log state periodically
        if ((int)(_elapsed * 2) != (int)((_elapsed - dt) * 2) && _elapsed > 1f)
        {
            int alive = 0, poisoned = 0, plagued = 0;
            for (int i = 0; i < sim.Units.Count; i++)
            {
                if (!sim.Units[i].Alive) continue;
                alive++;
                if (sim.Units[i].PoisonStacks > 0) poisoned++;
                foreach (var b in sim.Units[i].ActiveBuffs)
                    if (b.BuffDefID == "buff_plagued") { plagued++; break; }
            }

            int dead = _initialEnemyCount - alive;
            if (dead > _peakDead) _peakDead = dead;
            if (poisoned > _peakPoisoned) _peakPoisoned = poisoned;
            if (plagued > _peakPlagued) _peakPlagued = plagued;

            int cloudCount = sim.PoisonClouds.Clouds.Count;
            var cloudInfo = cloudCount > 0
                ? $"radius={sim.PoisonClouds.Clouds[0].CurrentRadius:F1}, " +
                  $"potency={sim.PoisonClouds.Clouds[0].Potency:F2}, " +
                  $"phase={sim.PoisonClouds.Clouds[0].Phase}, " +
                  $"consumed={sim.PoisonClouds.Clouds[0].CorpsesConsumed}"
                : "EXPIRED";

            DebugLog.Log(ScenarioLog, $"[{_elapsed:F1}s] alive={alive}/{_initialEnemyCount}, " +
                $"poisoned={poisoned}, plagued={plagued}, cloud: {cloudInfo}");
        }

        // Complete after cloud expires or timeout
        bool cloudDone = _cloudSpawned && sim.PoisonClouds.Clouds.Count == 0 && _elapsed > 5f;
        if (cloudDone || _elapsed > 25f)
            _complete = true;
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "\n=== RESULTS ===");
        DebugLog.Log(ScenarioLog, $"Peak dead: {_peakDead}, Peak poisoned: {_peakPoisoned}, Peak plagued: {_peakPlagued}");
        DebugLog.Log(ScenarioLog, $"Duration: {_elapsed:F1}s");

        bool hasDamage = _peakDead > 0 || _peakPoisoned > 0;
        if (hasDamage)
        {
            DebugLog.Log(ScenarioLog, "PASS: Cloud dealt damage");
            return 0;
        }

        DebugLog.Log(ScenarioLog, "FAIL: Cloud dealt no damage");
        return 1;
    }
}
