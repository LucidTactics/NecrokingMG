using System.Collections.Generic;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Lib;
using Necroking.Movement;

namespace Necroking.AI;

/// <summary>
/// Shared awareness detection that runs before per-archetype AI updates.
/// Sets AlertState, AlertTimer, AlertTarget on each unit based on nearby threats.
///
/// Detection is affected by:
///   - Target sneaking: detection range × 0.5
///   - Target running: detection range × 1.5
///   - Per-def DetectionRange and DetectionBreakRange
///
/// AlertState flow:
///   Unaware → Alert (enemy enters detection range)
///   Alert → Aggressive (enemy gets closer or alert timer expires)
///   Alert → Unaware (enemy leaves break range)
///   Aggressive → Unaware (enemy leaves break range)
///
/// Group propagation: when a unit enters Alert, nearby same-faction units
/// within GroupAlertRadius also become Alert (unless already aware).
/// </summary>
public static class AwarenessSystem
{
    // Reused across the update to avoid allocating a list per unit.
    private static readonly List<int> _nearbyScratch = new();

    /// <summary>
    /// Run awareness pass for all units. Call once per frame before AI updates.
    /// When <paramref name="amortized"/> is true, Unaware units are only checked
    /// every <paramref name="interval"/> frames (staggered by unit index), since
    /// they don't need per-frame responsiveness. Alert/Aggressive states always
    /// run so hostile engagement stays instant.
    /// </summary>
    public static void Update(Simulation sim, float dt, bool amortized = false, int interval = 1)
    {
        var units = sim.UnitsMut;
        var query = sim.Query;
        int amortInterval = amortized && interval > 1 ? interval : 1;
        for (int i = 0; i < units.Count; i++)
        {
            if (!units[i].Alive) continue;
            if (units[i].Archetype == ArchetypeRegistry.PlayerControlled) continue;
            if (units[i].Archetype == ArchetypeRegistry.None) continue;

            float detectionRange = units[i].DetectionRange;
            float breakRange = units[i].DetectionBreakRange;
            if (detectionRange <= 0f) continue; // no awareness

            byte alertState = units[i].AlertState;
            Vec2 myPos = units[i].Position;

            switch (alertState)
            {
                case (byte)UnitAlertState.Unaware:
                {
                    // Amortize the Unaware scan when enabled — that scan is the expensive
                    // part (per-unit QueryRadius + filter). Gate on dt-accumulated WALL TIME
                    // (reusing the idle AlertTimer), NOT a sim-frame modulo: frame count is
                    // decoupled from dt, so at high timeScale a frame spans more seconds and
                    // detection latency balloons (~800ms at x8). interval/60 keeps the
                    // documented ~100ms-at-interval-6 latency invariant to game speed / FPS.
                    if (amortInterval > 1)
                    {
                        units[i].AlertTimer += dt;
                        if (units[i].AlertTimer < amortInterval / 60f)
                            break;
                        units[i].AlertTimer = 0f;
                    }

                    // Scan for threats via quadtree — only cross-faction units
                    // come back thanks to faction-aware filtering, so the inner
                    // loop is O(local density) rather than O(all units).
                    int threatIdx = query.NearestThreatOf(i, detectionRange);
                    if (threatIdx >= 0)
                    {
                        units[i].AlertState = (byte)UnitAlertState.Alert;
                        units[i].AlertTimer = 0f;
                        units[i].AlertTarget = units[threatIdx].Id;
                        units[i].ShowStatusSymbol(UnitStatusSymbol.Notice, 1.5f);

                        // Group propagation
                        float groupRadius = units[i].GroupAlertRadius;
                        if (groupRadius > 0f)
                            PropagateAlert(units, query, i, threatIdx, groupRadius);
                    }
                    break;
                }

                case (byte)UnitAlertState.Alert:
                {
                    units[i].AlertTimer += dt;
                    int threatIdx = UnitUtil.ResolveUnitIndex(units, units[i].AlertTarget);

                    if (threatIdx < 0)
                    {
                        // Threat gone — calm down
                        units[i].AlertState = (byte)UnitAlertState.Unaware;
                        units[i].AlertTarget = GameConstants.InvalidUnit;
                        break;
                    }

                    float dist = (units[threatIdx].Position - myPos).Length();

                    // Break range check
                    if (dist > breakRange)
                    {
                        units[i].AlertState = (byte)UnitAlertState.Unaware;
                        units[i].AlertTarget = GameConstants.InvalidUnit;
                        break;
                    }

                    // Escalation: alert duration expired or threat very close
                    float alertDuration = units[i].AlertDuration;
                    float escalateRange = units[i].AlertEscalateRange;
                    if (units[i].AlertTimer >= alertDuration || (escalateRange > 0 && dist <= escalateRange))
                    {
                        units[i].AlertState = (byte)UnitAlertState.Aggressive;
                    }
                    break;
                }

                case (byte)UnitAlertState.Aggressive:
                {
                    // Stay aggressive until threat leaves break range
                    int threatIdx = UnitUtil.ResolveUnitIndex(units, units[i].AlertTarget);
                    if (threatIdx < 0)
                    {
                        units[i].AlertState = (byte)UnitAlertState.Unaware;
                        units[i].AlertTarget = GameConstants.InvalidUnit;
                        break;
                    }

                    float dist = (units[threatIdx].Position - myPos).Length();
                    if (dist > breakRange)
                    {
                        units[i].AlertState = (byte)UnitAlertState.Unaware;
                        units[i].AlertTarget = GameConstants.InvalidUnit;
                    }
                    break;
                }
            }
        }
    }

    private static void PropagateAlert(UnitArrays units, WorldQuery query, int alerterIdx, int threatIdx, float radius)
    {
        var alerterPos = units[alerterIdx].Position;
        var alerterFaction = units[alerterIdx].Faction;
        uint threatId = units[threatIdx].Id;

        _nearbyScratch.Clear();
        query.UnitsInRadius(alerterPos, radius, alerterFaction.Bit(), _nearbyScratch);

        foreach (int j in _nearbyScratch)
        {
            if (j == alerterIdx) continue;
            if (units[j].AlertState != (byte)UnitAlertState.Unaware) continue;
            if (units[j].DetectionRange <= 0f) continue;

            units[j].AlertState = (byte)UnitAlertState.Alert;
            units[j].AlertTimer = 0f;
            units[j].AlertTarget = threatId;
            units[j].ShowStatusSymbol(UnitStatusSymbol.Notice, 1.5f);
        }
    }
}
