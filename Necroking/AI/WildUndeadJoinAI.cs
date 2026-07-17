using Necroking.Core;
using Necroking.GameSystems;
using Necroking.Lib;

namespace Necroking.AI;

/// <summary>
/// Wild-undead recruitment sweep. Map-placed undead spawn as the WildUndead
/// archetype — free agents that idle-roam near their spawn point and fight
/// through the sentry ladder, with no dependence on the necromancer existing.
/// This sweep is the one bridge back to the player: when the necromancer walks
/// up to a wild undead it joins the horde (the old "undead you spawn on top of
/// get enrolled" behavior, now earned by proximity).
///
/// Sweep pattern per <see cref="BoarForageAI"/>: static Update called from
/// Simulation.Update after the archetype AI pass, because it needs
/// sim.NecromancerIndex (unreachable from AIContext). Keyed strictly on the
/// WildUndead archetype — NOT "undead && not in horde" — so multiplayer ghost
/// units (Faction.Undead, archetype 0, deliberately hordeless) never match.
/// </summary>
public static class WildUndeadJoinAI
{
    /// <summary>Necromancer within this range of a wild undead → it joins the horde.</summary>
    public const float JoinRadius = 5f;

    public static void Update(Simulation sim, float dt)
    {
        int necroIdx = sim.NecromancerIndex;
        if (necroIdx < 0) return;
        var units = sim.UnitsMut;
        if (!units[necroIdx].Alive) return;
        var necroPos = units[necroIdx].Position;

        for (int u = 0; u < units.Count; u++)
        {
            if (units[u].Archetype != ArchetypeRegistry.WildUndead) continue;
            if (!units[u].Alive) continue;
            if ((units[u].Position - necroPos).LengthSq() > JoinRadius * JoinRadius) continue;

            // Clean brain swap: Interrupt fires the sentry handler's exit hooks
            // and clears the combat pins before the horde takes the unit over.
            AIControl.Interrupt(units, u, "wild-undead-join");
            sim.EnrollInHorde(u);
            DebugLog.Log("horde",
                $"[WildUndeadJoin] unit {units[u].Id} ({units[u].UnitDefID}) joined the horde");
        }
    }
}
