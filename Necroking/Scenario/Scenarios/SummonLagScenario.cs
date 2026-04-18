using System;
using System.Collections.Generic;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Reproduces the "debug-summon 10 skeletons causes lag in waves" report.
/// Baselines Simulation.LastTickMs for ~1s, spawns 10 skeletons via
/// SpawnUnitByID (same code path the debug summon spell uses internally),
/// then logs per-tick ms for 6s after the summon so we can chart when
/// and for how long the lag hits.
///
/// Uses --speed 1 so real-time and scenario-time align: each sim tick is
/// the game's actual 1/60 s work. Per-tick wall-clock cost is what the
/// player feels.
/// </summary>
public class SummonLagScenario : ScenarioBase
{
    public override string Name => "summon_lag";
    public override bool IsComplete => _complete;

    private const int SummonCount = 10;
    private const float BaselineSeconds = 1f;
    private const float PostSummonSeconds = 6f;

    private bool _complete;
    private bool _summoned;
    private float _elapsed;
    private int _necroIdx = -1;
    private readonly List<double> _baselineTicks = new();
    // (elapsed-since-summon, tickMs)
    private readonly List<(float t, double ms)> _postSummonTicks = new();

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Summon Lag Repro ===");
        DebugLog.Log(ScenarioLog, $"Baseline {BaselineSeconds:F1}s -> summon {SummonCount} skeletons -> measure {PostSummonSeconds:F1}s");

        var units = sim.UnitsMut;
        _necroIdx = units.AddUnit(new Vec2(32f, 32f), UnitType.Necromancer);
        units[_necroIdx].AI = AIBehavior.PlayerControlled;
        units[_necroIdx].Faction = Faction.Undead;
        sim.SetNecromancerIndex(_necroIdx);
        DebugLog.Log(ScenarioLog, $"Necromancer at (32,32)");
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;
        double ms = sim.LastTickMs;

        if (!_summoned)
        {
            _baselineTicks.Add(ms);
            if (_elapsed >= BaselineSeconds)
            {
                // Summon — mirrors the debug spell's summon path (SpawnUnitByID
                // for each unit, spawn-offset from the caster).
                var casterPos = sim.Units[_necroIdx].Position;
                for (int q = 0; q < SummonCount; q++)
                {
                    float a = q * MathF.Tau / SummonCount;
                    var pos = new Vec2(casterPos.X + MathF.Cos(a) * 1.2f,
                                        casterPos.Y + MathF.Sin(a) * 1.2f);
                    int idx = sim.SpawnUnitByID("skeleton", pos);
                    if (idx >= 0)
                    {
                        sim.UnitsMut[idx].Faction = Faction.Undead;
                        sim.Horde.AddUnit(sim.Units[idx].Id);
                    }
                }
                _summoned = true;
                DebugLog.Log(ScenarioLog, $"[t={_elapsed:F2}s] Summoned {SummonCount} skeletons. "
                    + $"Baseline avg={Average(_baselineTicks):F2}ms max={Max(_baselineTicks):F2}ms");
                return;
            }
        }
        else
        {
            float since = _elapsed - BaselineSeconds;
            _postSummonTicks.Add((since, ms));
            if (since >= PostSummonSeconds) _complete = true;
        }
    }

    public override int OnComplete(Simulation sim)
    {
        double baselineAvg = Average(_baselineTicks);
        double baselineMax = Max(_baselineTicks);
        double threshold = MathF.Max(2.0f, (float)baselineAvg * 3f); // "lag" = >3x baseline or >2ms

        DebugLog.Log(ScenarioLog, "--- Baseline (pre-summon) ---");
        DebugLog.Log(ScenarioLog, $"  avg={baselineAvg:F2}ms  max={baselineMax:F2}ms  n={_baselineTicks.Count}");
        DebugLog.Log(ScenarioLog, $"  spike threshold = {threshold:F2}ms");

        DebugLog.Log(ScenarioLog, "--- Post-summon per-tick timings (only spikes > threshold logged) ---");
        int spikeCount = 0;
        double postMax = 0, postSum = 0;
        foreach (var (t, ms) in _postSummonTicks)
        {
            postSum += ms;
            if (ms > postMax) postMax = ms;
            if (ms > threshold)
            {
                spikeCount++;
                DebugLog.Log(ScenarioLog, $"  t+{t:F3}s  {ms:F2}ms");
            }
        }
        double postAvg = _postSummonTicks.Count > 0 ? postSum / _postSummonTicks.Count : 0;

        DebugLog.Log(ScenarioLog, "--- Summary ---");
        DebugLog.Log(ScenarioLog, $"  post-summon avg={postAvg:F2}ms max={postMax:F2}ms spikes={spikeCount}/{_postSummonTicks.Count}");

        // Bucket spikes by 100ms windows to spot "waves"
        DebugLog.Log(ScenarioLog, "--- Spike density per 0.25s bucket (post-summon) ---");
        var buckets = new Dictionary<int, (int n, double total, double max)>();
        foreach (var (t, ms) in _postSummonTicks)
        {
            if (ms <= threshold) continue;
            int bkt = (int)(t * 4); // 0.25s buckets
            if (!buckets.TryGetValue(bkt, out var b)) b = (0, 0, 0);
            buckets[bkt] = (b.n + 1, b.total + ms, MathF.Max((float)b.max, (float)ms));
        }
        foreach (var bkt in SortedKeys(buckets))
        {
            var (n, total, max) = buckets[bkt];
            float start = bkt * 0.25f;
            DebugLog.Log(ScenarioLog, $"  +{start:F2}s..+{start+0.25f:F2}s  n={n} sum={total:F2}ms max={max:F2}ms");
        }

        return 0;
    }

    private static double Average(List<double> xs) { double s = 0; foreach (var x in xs) s += x; return xs.Count > 0 ? s / xs.Count : 0; }
    private static double Max(List<double> xs) { double m = 0; foreach (var x in xs) if (x > m) m = x; return m; }
    private static IEnumerable<int> SortedKeys(Dictionary<int, (int, double, double)> d)
    { var a = new List<int>(d.Keys); a.Sort(); return a; }
}
