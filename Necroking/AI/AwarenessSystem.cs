using Necroking.Core;
using Necroking.Data;
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
    /// <summary>Run awareness pass for all units. Call once per frame before AI updates.</summary>
    public static void Update(UnitArrays units, float dt, int frameNumber)
    {
        for (int i = 0; i < units.Count; i++)
        {
            if (!units.Alive[i]) continue;
            if (units.Archetype[i] == ArchetypeRegistry.PlayerControlled) continue;
            if (units.Archetype[i] == ArchetypeRegistry.None) continue;

            float detectionRange = units.DetectionRange[i];
            float breakRange = units.DetectionBreakRange[i];
            if (detectionRange <= 0f) continue; // no awareness

            byte alertState = units.AlertState[i];
            Vec2 myPos = units.Position[i];

            switch (alertState)
            {
                case (byte)UnitAlertState.Unaware:
                {
                    // Scan for threats
                    int threatIdx = FindClosestThreat(units, i, detectionRange);
                    if (threatIdx >= 0)
                    {
                        units.AlertState[i] = (byte)UnitAlertState.Alert;
                        units.AlertTimer[i] = 0f;
                        units.AlertTarget[i] = units.Id[threatIdx];

                        // Group propagation
                        float groupRadius = units.GroupAlertRadius[i];
                        if (groupRadius > 0f)
                            PropagateAlert(units, i, threatIdx, groupRadius);
                    }
                    break;
                }

                case (byte)UnitAlertState.Alert:
                {
                    units.AlertTimer[i] += dt;
                    int threatIdx = ResolveThreat(units, i);

                    if (threatIdx < 0)
                    {
                        // Threat gone — calm down
                        units.AlertState[i] = (byte)UnitAlertState.Unaware;
                        units.AlertTarget[i] = GameConstants.InvalidUnit;
                        break;
                    }

                    float dist = (units.Position[threatIdx] - myPos).Length();

                    // Break range check
                    if (dist > breakRange)
                    {
                        units.AlertState[i] = (byte)UnitAlertState.Unaware;
                        units.AlertTarget[i] = GameConstants.InvalidUnit;
                        break;
                    }

                    // Escalation: alert duration expired or threat very close
                    float alertDuration = units.AlertDuration[i];
                    float escalateRange = units.AlertEscalateRange[i];
                    if (units.AlertTimer[i] >= alertDuration || (escalateRange > 0 && dist <= escalateRange))
                    {
                        units.AlertState[i] = (byte)UnitAlertState.Aggressive;
                    }
                    break;
                }

                case (byte)UnitAlertState.Aggressive:
                {
                    // Stay aggressive until threat leaves break range
                    int threatIdx = ResolveThreat(units, i);
                    if (threatIdx < 0)
                    {
                        units.AlertState[i] = (byte)UnitAlertState.Unaware;
                        units.AlertTarget[i] = GameConstants.InvalidUnit;
                        break;
                    }

                    float dist = (units.Position[threatIdx] - myPos).Length();
                    if (dist > breakRange)
                    {
                        units.AlertState[i] = (byte)UnitAlertState.Unaware;
                        units.AlertTarget[i] = GameConstants.InvalidUnit;
                    }
                    break;
                }
            }
        }
    }

    private static int FindClosestThreat(UnitArrays units, int unitIdx, float maxRange)
    {
        float bestDist = maxRange * maxRange;
        int bestIdx = -1;
        var myFaction = units.Faction[unitIdx];
        var myPos = units.Position[unitIdx];

        for (int j = 0; j < units.Count; j++)
        {
            if (j == unitIdx || !units.Alive[j]) continue;
            if (units.Faction[j] == myFaction) continue;

            // Adjust range based on target movement state
            float effectiveRange = maxRange;
            if (units.IsSneaking[j]) effectiveRange *= 0.5f;
            else if (units.Velocity[j].LengthSq() > units.MaxSpeed[j] * units.MaxSpeed[j] * 0.8f)
                effectiveRange *= 1.5f; // running = easier to detect

            float d = (units.Position[j] - myPos).LengthSq();
            if (d < bestDist && d <= effectiveRange * effectiveRange)
            {
                bestDist = d;
                bestIdx = j;
            }
        }
        return bestIdx;
    }

    private static int ResolveThreat(UnitArrays units, int unitIdx)
    {
        uint targetId = units.AlertTarget[unitIdx];
        if (targetId == GameConstants.InvalidUnit) return -1;
        for (int j = 0; j < units.Count; j++)
            if (units.Id[j] == targetId && units.Alive[j]) return j;
        return -1;
    }

    private static void PropagateAlert(UnitArrays units, int alerterIdx, int threatIdx, float radius)
    {
        float r2 = radius * radius;
        var alerterPos = units.Position[alerterIdx];
        var alerterFaction = units.Faction[alerterIdx];

        for (int j = 0; j < units.Count; j++)
        {
            if (j == alerterIdx || !units.Alive[j]) continue;
            if (units.Faction[j] != alerterFaction) continue;
            if (units.AlertState[j] != (byte)UnitAlertState.Unaware) continue;
            if (units.DetectionRange[j] <= 0f) continue;

            float d = (units.Position[j] - alerterPos).LengthSq();
            if (d <= r2)
            {
                units.AlertState[j] = (byte)UnitAlertState.Alert;
                units.AlertTimer[j] = 0f;
                units.AlertTarget[j] = units.Id[threatIdx];
            }
        }
    }
}
