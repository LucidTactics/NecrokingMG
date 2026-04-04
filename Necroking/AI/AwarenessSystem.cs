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
                    // Scan for threats
                    int threatIdx = FindClosestThreat(units, i, detectionRange);
                    if (threatIdx >= 0)
                    {
                        units[i].AlertState = (byte)UnitAlertState.Alert;
                        units[i].AlertTimer = 0f;
                        units[i].AlertTarget = units[threatIdx].Id;

                        // Group propagation
                        float groupRadius = units[i].GroupAlertRadius;
                        if (groupRadius > 0f)
                            PropagateAlert(units, i, threatIdx, groupRadius);
                    }
                    break;
                }

                case (byte)UnitAlertState.Alert:
                {
                    units[i].AlertTimer += dt;
                    int threatIdx = ResolveThreat(units, i);

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
                    int threatIdx = ResolveThreat(units, i);
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

    private static int FindClosestThreat(UnitArrays units, int unitIdx, float maxRange)
    {
        float bestDist = maxRange * maxRange;
        int bestIdx = -1;
        var myFaction = units[unitIdx].Faction;
        var myPos = units[unitIdx].Position;

        for (int j = 0; j < units.Count; j++)
        {
            if (j == unitIdx || !units[j].Alive) continue;
            if (units[j].Faction == myFaction) continue;

            // Adjust range based on target movement state
            float effectiveRange = maxRange;
            if (units[j].IsSneaking) effectiveRange *= 0.5f;
            else if (units[j].Velocity.LengthSq() > units[j].MaxSpeed * units[j].MaxSpeed * 0.8f)
                effectiveRange *= 1.5f; // running = easier to detect

            float d = (units[j].Position - myPos).LengthSq();
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
        uint targetId = units[unitIdx].AlertTarget;
        if (targetId == GameConstants.InvalidUnit) return -1;
        for (int j = 0; j < units.Count; j++)
            if (units[j].Id == targetId && units[j].Alive) return j;
        return -1;
    }

    private static void PropagateAlert(UnitArrays units, int alerterIdx, int threatIdx, float radius)
    {
        float r2 = radius * radius;
        var alerterPos = units[alerterIdx].Position;
        var alerterFaction = units[alerterIdx].Faction;

        for (int j = 0; j < units.Count; j++)
        {
            if (j == alerterIdx || !units[j].Alive) continue;
            if (units[j].Faction != alerterFaction) continue;
            if (units[j].AlertState != (byte)UnitAlertState.Unaware) continue;
            if (units[j].DetectionRange <= 0f) continue;

            float d = (units[j].Position - alerterPos).LengthSq();
            if (d <= r2)
            {
                units[j].AlertState = (byte)UnitAlertState.Alert;
                units[j].AlertTimer = 0f;
                units[j].AlertTarget = units[threatIdx].Id;
            }
        }
    }
}
