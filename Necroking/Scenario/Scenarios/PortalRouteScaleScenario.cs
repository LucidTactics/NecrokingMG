using System;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Movement;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Regression guard for portal-graph routing at real-map scale (4096 tiles =
/// 64x64 sectors) with budgeted pathfinding. The original portal router ran
/// an exhaustive graph Dijkstra per destination sector: on 4096 sectors that
/// meant thousands of lazy matrix computes, budget-aborted and RESTARTED
/// every tick — dj_ms pegged at the budget forever, the pending queue never
/// drained, and every cross-sector flow stayed deferred (units beelined
/// permanently while tick time ramped). This scenario reproduces the load —
/// a follower continuously pathing to a target that keeps crossing sector
/// borders — and asserts the fixed behavior: the corridor-bounded resumable
/// A* converges, flows land in cache, and the budget is mostly idle.
///
/// PASS criteria:
///  (a) after a 5s warmup, average pathfinder Dijkstra ms/tick &lt; 5 with a
///      10ms budget, and the pending-request count returns to ~0 within 2s
///      of every destination-sector change;
///  (b) flow cache hits &gt; misses over the last 10s.
/// </summary>
public class PortalRouteScaleScenario : ScenarioBase
{
    public override string Name => "portal_route_scale";
    public override int GridSize => 4096; // 64x64 sectors — real-map scale
    public override bool IsComplete => _complete;

    private const float DurationS = 20f;
    private const float WarmupS = 5f;          // cold-start matrices/routes excluded from the dj average
    private const float HitWindowStartS = 10f; // hits>misses judged over the last 10s
    private const float PendingRecoverS = 2f;  // pending must drain within this after a dest-sector change
    private const float BudgetMs = 10f;
    private const float TargetSpeed = 20f;     // tiles/s -> crosses a 64-tile sector border every ~3.2s
    private const float FollowerSpeed = 18f;   // slightly slower: never catches up, paths cross-sector all run

    private bool _complete;
    private float _elapsed;
    private int _targetIdx = -1, _followerIdx = -1;

    // Saved settings (restored in OnComplete — Game1 saves settings on exit,
    // so a scenario must not leak its budget override into the user's file).
    private bool _savedBudgeted;
    private float _savedBudgetMs;

    // Measurements
    private double _djSum;
    private int _djTicks;
    private double _djMax;
    private long _hits, _misses;
    private int _lastDestSector = -1;
    private int _destChanges;             // post-warmup dest-sector changes
    private int _watchesCompleted;        // recovery watches that resolved (ok or failed)
    private int _recoveryFailures;        // watches where pending never drained in time
    private float _watchStart = -1f;      // active recovery watch start time (-1 = none)
    private float _worstRecoverS;
    private int _logCountdown;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Portal Route Scale (4096x4096, 64x64 sectors) ===");

        // Budgeted pathfinding at 10ms — Simulation re-pushes these from
        // settings every tick, so the settings object is the only lever.
        var perf = sim.GameData!.Settings.Performance;
        _savedBudgeted = perf.BudgetedPathfinding;
        _savedBudgetMs = perf.DijkstraBudgetMsPerTick;
        perf.BudgetedPathfinding = true;
        perf.DijkstraBudgetMsPerTick = BudgetMs;

        var units = sim.UnitsMut;
        units.Clear();

        // Target: auto-moves east across ~6 sector borders over the run.
        _targetIdx = units.AddUnit(new Vec2(500f, 200f), UnitType.Skeleton);
        units[_targetIdx].AI = AIBehavior.MoveToPoint;
        units[_targetIdx].MoveTarget = new Vec2(500f + TargetSpeed * DurationS, 200f);
        units[_targetIdx].Stats.CombatSpeed = TargetSpeed; // MaxSpeed derives from this via Locomotion.UpdateSpeeds
        units[_targetIdx].Faction = Faction.Undead;

        // Follower: starts ~2.3 sectors behind, re-targets the target every
        // tick — the dest sector changes whenever the target crosses a border.
        _followerIdx = units.AddUnit(new Vec2(350f, 200f), UnitType.Skeleton);
        units[_followerIdx].AI = AIBehavior.MoveToPoint;
        units[_followerIdx].MoveTarget = units[_targetIdx].Position;
        units[_followerIdx].Stats.CombatSpeed = FollowerSpeed; // MaxSpeed derives from this via Locomotion.UpdateSpeeds
        units[_followerIdx].Faction = Faction.Undead;

        // Mirror Game1's map-load pipeline (tiered cost fields + portal build).
        sim.RebuildPathfinder();

        ZoomOnLocation(450f, 200f, 2f);
        DebugLog.Log(ScenarioLog, $"Budget: {BudgetMs:F0}ms/tick. Target (500,200)->({500f + TargetSpeed * DurationS:F0},200) @ {TargetSpeed:F0}t/s; follower @ {FollowerSpeed:F0}t/s re-targeting every tick.");
    }

    public override void OnTick(Simulation sim, float dt)
    {
        if (_complete) return;
        _elapsed += dt;

        // Keep the follower chasing the live target position.
        if (_followerIdx < sim.Units.Count && sim.Units[_followerIdx].Alive &&
            _targetIdx < sim.Units.Count && sim.Units[_targetIdx].Alive)
        {
            sim.UnitsMut[_followerIdx].MoveTarget = sim.Units[_targetIdx].Position;
        }

        // --- Per-tick measurements (OnTick runs after sim.Tick, so the Diag
        // statics hold THIS tick's values). ---
        float dj = sim.Pathfinder.DiagDijkstraMsThisTick;
        int pend = sim.Pathfinder.DiagPendingRequestCount;

        if (_elapsed >= WarmupS)
        {
            _djSum += dj;
            _djTicks++;
            if (dj > _djMax) _djMax = dj;
        }
        if (_elapsed >= HitWindowStartS)
        {
            _hits += World.Pathfinder.DiagFlowCacheHits;
            _misses += World.Pathfinder.DiagFlowCacheMisses;
        }

        // --- Dest-sector change tracking + pending-recovery watch ---
        var tpos = sim.Units[_targetIdx].Position;
        int destSector = (int)(tpos.Y / World.Pathfinder.SectorSize) * ((GridSize + World.Pathfinder.SectorSize - 1) / World.Pathfinder.SectorSize)
                       + (int)(tpos.X / World.Pathfinder.SectorSize);
        if (destSector != _lastDestSector)
        {
            if (_lastDestSector >= 0 && _elapsed >= WarmupS)
            {
                _destChanges++;
                if (_watchStart < 0f) _watchStart = _elapsed; // one watch at a time; overlaps keep the older (stricter) start
                DebugLog.Log(ScenarioLog, $"[t={_elapsed:F1}s] dest sector changed -> {destSector} (pend={pend}, dj={dj:F2}ms)");
            }
            _lastDestSector = destSector;
        }
        if (_watchStart >= 0f)
        {
            if (pend <= 1)
            {
                float took = _elapsed - _watchStart;
                if (took > _worstRecoverS) _worstRecoverS = took;
                _watchesCompleted++;
                _watchStart = -1f;
            }
            else if (_elapsed - _watchStart > PendingRecoverS)
            {
                DebugLog.Log(ScenarioLog, $"[t={_elapsed:F1}s] PENDING RECOVERY FAILED: pend={pend} still >1 {PendingRecoverS:F0}s after dest change");
                _recoveryFailures++;
                _watchesCompleted++;
                _watchStart = -1f;
            }
        }

        // 1s heartbeat for post-mortem debugging.
        if (--_logCountdown <= 0)
        {
            _logCountdown = 60;
            DebugLog.Log(ScenarioLog, $"  t={_elapsed:F1}s dj={dj:F2}ms pend={pend} cache={sim.Pathfinder.FlowCacheSize} " +
                $"hits={World.Pathfinder.DiagFlowCacheHits} miss={World.Pathfinder.DiagFlowCacheMisses} tick={sim.LastTickMs:F2}ms");
        }

        if (_elapsed >= DurationS) _complete = true;
    }

    public override int OnComplete(Simulation sim)
    {
        // Restore the user's budget settings before Game1's exit-save runs.
        if (sim.GameData != null)
        {
            sim.GameData.Settings.Performance.BudgetedPathfinding = _savedBudgeted;
            sim.GameData.Settings.Performance.DijkstraBudgetMsPerTick = _savedBudgetMs;
        }

        double djAvg = _djTicks > 0 ? _djSum / _djTicks : 0;
        DebugLog.Log(ScenarioLog, "=== Portal Route Scale Results ===");
        DebugLog.Log(ScenarioLog, $"  post-warmup dj_ms: avg={djAvg:F2} max={_djMax:F2} (n={_djTicks}, budget={BudgetMs:F0}ms)");
        DebugLog.Log(ScenarioLog, $"  dest-sector changes={_destChanges}, recovery watches={_watchesCompleted}, failures={_recoveryFailures}, worst recover={_worstRecoverS:F2}s");
        DebugLog.Log(ScenarioLog, $"  last-10s flow cache: hits={_hits} misses={_misses}");

        bool djOk = djAvg < 5.0;
        // Require the run to have actually exercised dest changes; an
        // unfinished watch at scenario end is not counted either way.
        bool pendOk = _recoveryFailures == 0 && _destChanges >= 3;
        bool hitOk = _hits > _misses;

        DebugLog.Log(ScenarioLog, $"  dj avg < 5ms: {(djOk ? "PASS" : "FAIL")}");
        DebugLog.Log(ScenarioLog, $"  pending drains <= {PendingRecoverS:F0}s after dest change (>=3 changes): {(pendOk ? "PASS" : "FAIL")}");
        DebugLog.Log(ScenarioLog, $"  hits > misses (last 10s): {(hitOk ? "PASS" : "FAIL")}");

        bool pass = djOk && pendOk && hitOk;
        DebugLog.Log(ScenarioLog, $"Overall: {(pass ? "PASS" : "FAIL")}");
        return pass ? 0 : 1;
    }
}
