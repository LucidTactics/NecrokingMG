using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Lib;
using Necroking.Movement;
using Necroking.World;

namespace Necroking.AI;

/// <summary>
/// Mushroom-foraging AI for horde minions tagged <c>forager</c> (zombie boars and
/// the larger zombie deer). Foragers in the necromancer's horde peel off from
/// formation to hunt down and eat nearby mushrooms. Each mushroom eaten is
/// removed from the world and stored in that individual boar's belly
/// (Simulation's per-unit belly store, keyed by unit id); when the boar dies
/// they all burst back out onto the ground — see Simulation.SpitBoarBellyOnDeath.
///
/// Design — a passive-ish sweep, mirroring <see cref="CorpseEatAI"/>:
///   - Runs AFTER the archetype AI pass (which set the boar's follow velocity)
///     and BEFORE UpdateMovement, so overriding PreferredVel steers the boar to
///     the mushroom this frame. When a boar has no mushroom to chase we leave its
///     HordeMinion follow velocity untouched, so it keeps up with the horde.
///   - Only acts on Undead HordeMinion boars that are Following, not in combat,
///     and have no target — combat and horde commands always win.
///   - Leashed to the necromancer: a boar that has strayed too far stops foraging
///     and lets HordeMinion pull it home, so boars don't chain-chase mushrooms off
///     across the map.
///   - Eaten mushrooms are Destroyed (not CollectForagable'd) so they do NOT
///     respawn while sitting in the belly — the world's mushroom count is
///     conserved: it vanishes when eaten and reappears (spread out) on death.
/// </summary>
public static class BoarForageAI
{
    private const float ForageRange = 12f;    // how far a boar will spot & chase a mushroom
    private const float EatRadius = 1.2f;     // close enough to start eating
    private const float EatDuration = 0.4f;   // seconds to chew one mushroom — a quick graze
    private const float LeashFromNecro = 18f; // beyond this from the necromancer, drop foraging and
                                              // follow the horde — so you can walk a boar away from a
                                              // mushroom patch even when it's standing on food.

    public static void Update(Simulation sim, float dt)
    {
        var env = sim.EnvironmentSystem;
        if (env == null) return;
        var gameData = sim.GameData;
        if (gameData == null) return;

        var units = sim.UnitsMut;

        bool haveNecro = sim.NecromancerIndex >= 0;
        Vec2 necroPos = haveNecro ? units[sim.NecromancerIndex].Position : Vec2.Zero;

        for (int u = 0; u < units.Count; u++)
        {
            if (!units[u].Alive) continue;
            if (units[u].Faction != Faction.Undead) continue;
            if (units[u].Archetype != ArchetypeRegistry.HordeMinion) continue;
            if (units[u].Routine != 0) continue;            // 0 == HordeMinion Following
            if (units[u].InCombat) continue;                // combat takes precedence
            if (!units[u].Target.IsNone) continue;          // heading to fight something
            if (units[u].IsLockedByAction()) continue;

            var def = gameData.Units.Get(units[u].UnitDefID);
            if (def == null || !def.Tags.Contains("forager")) continue;

            var myPos = units[u].Position;

            // Leash: if we've drifted too far from the master, quit foraging and let
            // HordeMinion.Following pull us back into formation.
            if (haveNecro && (myPos - necroPos).LengthSq() > LeashFromNecro * LeashFromNecro)
            {
                units[u].BoarEatTimer = 0f;
                continue;
            }

            // Mid-chew: graze in place, tick the timer, consume on completion.
            if (units[u].BoarEatTimer > 0f)
            {
                sim.AIForageGraze(u, dt);
                units[u].BoarEatTimer -= dt;
                if (units[u].BoarEatTimer > 0f) continue;

                // Timer done — the mushroom may have moved out of reach or been
                // grabbed by the player meanwhile, so re-find the nearest in bite range.
                int eatIdx = FindNearestMushroom(sim, myPos, EatRadius);
                if (eatIdx >= 0)
                {
                    var eObj = env.Objects[eatIdx];
                    ushort defIdx = eObj.DefIndex;
                    env.DestroyObject(eatIdx);             // gone from the world; now in the belly
                    sim.AddBoarBelly(units[u].Id, defIdx);
                    units[u].BellyMushrooms = (byte)System.Math.Min(byte.MaxValue, units[u].BellyMushrooms + 1);
                    Game1.Instance?.OnForagerAte(new Vec2(eObj.X, eObj.Y));   // pickup pop
                    DebugLog.Log("scenario", $"[BoarForage] boar#{units[u].Id} ate mushroom (belly={units[u].BellyMushrooms})");
                }
                continue;
            }

            // Look for the nearest mushroom to go eat.
            int targetIdx = FindNearestMushroom(sim, myPos, ForageRange);
            if (targetIdx < 0) continue;                    // none nearby — stay with the horde

            var mObj = env.Objects[targetIdx];
            var mPos = new Vec2(mObj.X, mObj.Y);
            if ((mPos - myPos).LengthSq() <= EatRadius * EatRadius)
            {
                units[u].MoveTarget = mPos;                 // remember it so the graze faces the mushroom
                sim.AIForageGraze(u, dt);
                units[u].BoarEatTimer = EatDuration;        // arrived — start chewing
            }
            else
            {
                sim.AIForageMove(u, mPos, dt);              // trot over to it
            }
        }
    }

    /// <summary>Visible mushroom foragables (see <see cref="IsMushroom"/>) — the
    /// caller-side filter for the canonical WorldQuery env scan. Mushrooms are
    /// foragables whose art lives under assets/Environment/Mushrooms/ (Deathcap,
    /// Ghostcap, Magic/Poison Mushroom, Rotgill, Toadstool) — distinguishes them
    /// from berries, logs and dropped potions.</summary>
    private readonly struct EnvMushrooms : IEnvQueryFilter
    {
        public bool Match(EnvironmentSystem env, int i)
            => env.IsObjectVisible(i) && IsMushroom(env.Defs[env.Objects[i].DefIndex]);
    }

    /// <summary>Nearest visible mushroom foragable within <paramref name="maxDist"/> of
    /// <paramref name="fromPos"/>, or -1.</summary>
    private static int FindNearestMushroom(Simulation sim, Vec2 fromPos, float maxDist)
        => sim.Query.NearestEnvObject(fromPos, maxDist, new EnvMushrooms());

    public static bool IsMushroom(EnvironmentObjectDef def) =>
        def.IsForagable
        && def.TexturePath != null
        && def.TexturePath.IndexOf("/Mushrooms/", System.StringComparison.OrdinalIgnoreCase) >= 0;
}
