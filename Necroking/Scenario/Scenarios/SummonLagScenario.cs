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
    // (elapsed-since-summon, tickMs, phase-snapshot-at-this-tick)
    private readonly List<(float t, double ms, Dictionary<string, double> phases)> _postSummonTicks = new();

    // Phase names we care about (read from Simulation.LastPhaseMs each tick).
    private static readonly string[] PhaseNames = {
        "quadtree", "potions", "horde_tick", "ai",
        "ai_awareness", "ai_archetype", "ai_legacy",
        "pathfinder", "pathfinder_calls",
        "pf_dijkstras", "pf_cache_hits", "pf_cache_misses",
        "pf_imag_new", "pf_imag_recompute", "pf_imag_ms",
        "movement", "physics", "horde_states", "facing", "combat",
        "projectiles", "lightning", "clouds", "cleanup",
    };

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
        // Mirror Game1's map-load pipeline: build tiered cost fields + env stamping.
        // Without this, the tier grid is all zeros and the pathfinder silently
        // degrades to the imaginary-chunk fallback on every call.
        sim.RebuildPathfinder();
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
                // Summon several tiles away from the caster so we're NOT inside the
                // caster's tier-inflation zone — matches the real game where the
                // debug summon drops skeletons offset from the necromancer.
                var casterPos = sim.Units[_necroIdx].Position;
                const float SpawnRadius = 5f;
                for (int q = 0; q < SummonCount; q++)
                {
                    float a = q * MathF.Tau / SummonCount;
                    var pos = new Vec2(casterPos.X + MathF.Cos(a) * SpawnRadius,
                                        casterPos.Y + MathF.Sin(a) * SpawnRadius);
                    int idx = sim.SpawnUnitByID("skeleton", pos);
                    if (idx >= 0)
                    {
                        sim.UnitsMut[idx].Faction = Faction.Undead;
                        // SpawnUnitByID doesn't set Archetype — Game1's spawn pipeline does.
                        // Mirror that here so the units go through the actual archetype dispatch
                        // path the real "debug summon" code exercises.
                        sim.UnitsMut[idx].Archetype = AI.ArchetypeRegistry.HordeMinion;
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
            // Snapshot phase timings (Simulation reuses its dict, so we copy).
            var snap = new Dictionary<string, double>(sim.LastPhaseMs);
            _postSummonTicks.Add((since, ms, snap));
            if (since >= PostSummonSeconds) _complete = true;
        }
    }

    public override int OnComplete(Simulation sim)
    {
        double baselineAvg = Average(_baselineTicks);
        double baselineMax = Max(_baselineTicks);

        DebugLog.Log(ScenarioLog, "--- Baseline (pre-summon) ---");
        DebugLog.Log(ScenarioLog, $"  avg={baselineAvg:F2}ms  max={baselineMax:F2}ms  n={_baselineTicks.Count}");

        // Bucket post-summon by 0.5s window and report per-phase avg/max.
        // Format per bucket:
        //   +0.00s..+0.50s  tick=avg/max   phase1=avg/max  phase2=avg/max ...
        const float BucketS = 0.5f;
        var bucketIdx = new Dictionary<int, List<(double tick, Dictionary<string, double> phases)>>();
        foreach (var (t, ms, phases) in _postSummonTicks)
        {
            int bkt = (int)(t / BucketS);
            if (!bucketIdx.TryGetValue(bkt, out var list))
                bucketIdx[bkt] = list = new();
            list.Add((ms, phases));
        }

        DebugLog.Log(ScenarioLog, $"--- Per-{BucketS:F1}s bucket: avg tick ms + top phases ---");
        foreach (var bkt in SortedKeys(bucketIdx))
        {
            var samples = bucketIdx[bkt];
            float start = bkt * BucketS;
            double tickAvg = 0, tickMax = 0;
            foreach (var (tick, _) in samples) { tickAvg += tick; if (tick > tickMax) tickMax = tick; }
            tickAvg /= samples.Count;

            // Per-phase aggregate in this bucket
            var phaseAvg = new Dictionary<string, double>();
            var phaseMax = new Dictionary<string, double>();
            foreach (var name in PhaseNames) { phaseAvg[name] = 0; phaseMax[name] = 0; }
            foreach (var (_, phases) in samples)
            {
                foreach (var name in PhaseNames)
                {
                    if (phases.TryGetValue(name, out double v))
                    {
                        phaseAvg[name] += v;
                        if (v > phaseMax[name]) phaseMax[name] = v;
                    }
                }
            }
            foreach (var name in PhaseNames) phaseAvg[name] /= samples.Count;

            // Sort phases by this bucket's avg descending; keep top 5
            var topPhases = new List<(string name, double avg, double max)>();
            foreach (var name in PhaseNames) topPhases.Add((name, phaseAvg[name], phaseMax[name]));
            topPhases.Sort((a, b) => b.avg.CompareTo(a.avg));

            string topStr = "";
            for (int k = 0; k < 5 && k < topPhases.Count; k++)
            {
                var p = topPhases[k];
                if (p.avg < 0.01) break;
                topStr += $"  {p.name}={p.avg:F2}/{p.max:F2}";
            }
            DebugLog.Log(ScenarioLog,
                $"+{start:F2}s..+{start+BucketS:F2}s  tick={tickAvg:F2}/{tickMax:F2}ms  top(avg/max):{topStr}");
        }

        return 0;
    }

    private static double Average(List<double> xs) { double s = 0; foreach (var x in xs) s += x; return xs.Count > 0 ? s / xs.Count : 0; }
    private static double Max(List<double> xs) { double m = 0; foreach (var x in xs) if (x > m) m = x; return m; }
    private static IEnumerable<int> SortedKeys<TV>(Dictionary<int, TV> d)
    { var a = new List<int>(d.Keys); a.Sort(); return a; }
}
