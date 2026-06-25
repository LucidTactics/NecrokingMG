using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.Game;
using Necroking.GameSystems;
using Necroking.Movement;

namespace Necroking.AI;

/// <summary>
/// Corpse Eater AI — wolves/bears autonomously consume aged corpses near them
/// when the Corpse Eater skill is unlocked. Per-unit stack-counter caps the
/// number of corpses each individual can eat (1 for Corpse Eater, 2 for
/// Improved Corpse Eating). Eats one corpse per call when the unit is idle
/// and a valid corpse is within EatRadius.
///
/// Scope choices:
///   - Passive only. We don't pathfind to far corpses or override the horde
///     handler's formation slot. If a wolf wanders close enough on its own,
///     it eats. Active pursuit can be layered on later by setting a routine
///     subroutine that HordeMinionHandler respects, but the basic mechanic
///     (cap, age gate, buff grant) is identical and worth shipping first.
///   - Wolves and bears only — checked via UnitDef.Tags containing "wolf" or
///     "bear". The skill description names these two; other monster tags
///     (boar, deer) don't qualify even though they're zombies.
///   - Eats any corpse (enemy, ally, neutral) >10s old that isn't bagged,
///     dissolving, or already marked consumed.
/// </summary>
public static class CorpseEatAI
{
    private const float MinCorpseAge = 10f;
    private const float EatRadius = 1.5f;        // close-enough distance for opportunistic eat
    private const float EatDuration = 2.0f;      // wall-clock seconds the eat animation holds
    private const float DefaultMaxStacks = 1;    // fallback if BookState payload is 0

    public static void Update(Simulation sim, float dt)
    {
        var bookState = sim.SkillBook;
        if (bookState == null) return;
        if (!bookState.IsAIUnlocked("corpse_eat")) return;

        var gameData = sim.GameData;
        if (gameData == null) return;

        int payload = bookState.GetAIPayload("corpse_eat");
        int maxStacks = payload > 0 ? payload : (int)DefaultMaxStacks;

        // Resolve the per-tier buff once — paying the registry lookup for every
        // eligible unit each tick would be wasteful for what's a constant.
        string buffId = payload >= 2 ? "buff_corpse_meal_improved" : "buff_corpse_meal";
        var buffDef = gameData.Buffs.Get(buffId);
        if (buffDef == null) return;

        var units = sim.UnitsMut;
        var corpses = sim.CorpsesMut;

        for (int u = 0; u < units.Count; u++)
        {
            if (!units[u].Alive) continue;
            if (units[u].Faction != Faction.Undead) continue;
            if (units[u].CorpsesEaten >= maxStacks) continue;
            if (units[u].IsLockedByAction()) continue;
            if (units[u].InCombat) continue;          // combat takes precedence
            if (units[u].CarryingCorpseID >= 0) continue;
            if (units[u].BaggingCorpseID >= 0) continue;

            var def = gameData.Units.Get(units[u].UnitDefID);
            if (def == null) continue;
            if (!(def.Tags.Contains("wolf") || def.Tags.Contains("bear"))) continue;

            // Tick a pending eat. When the timer finishes, finalize: mark the
            // corpse consumed, apply the meal buff, advance the per-unit
            // counter. The corpse may have already dissolved or been carried
            // off — in that case the eat aborts with no buff (the unit just
            // wasted EatDuration seconds, which is acceptable).
            if (units[u].CorpseEatTimer > 0f)
            {
                units[u].CorpseEatTimer -= dt;
                if (units[u].CorpseEatTimer > 0f) continue;

                int eatenId = units[u].CorpseEatTargetID;
                int corpseIdx = sim.FindCorpseIndexByID(eatenId);
                units[u].CorpseEatTargetID = -1;
                if (corpseIdx < 0) continue;
                if (corpses[corpseIdx].Dissolving || corpses[corpseIdx].ConsumedBySummon) continue;

                sim.ConsumeCorpse(corpseIdx);
                BuffSystem.ApplyBuff(units, u, buffDef, gameData);
                units[u].CorpsesEaten = (byte)System.Math.Min(byte.MaxValue, units[u].CorpsesEaten + 1);
                DebugLog.Log("scenario", $"[CorpseEat] {units[u].UnitDefID}#{u} ate corpse#{eatenId} (count={units[u].CorpsesEaten}/{maxStacks})");
                continue;
            }

            // Idle path — look for a corpse to start eating. Linear scan is
            // fine: corpse counts stay in the low tens in typical play.
            float bestDistSq = EatRadius * EatRadius;
            int bestIdx = -1;
            var myPos = units[u].Position;
            for (int c = 0; c < corpses.Count; c++)
            {
                var corpse = corpses[c];
                if (corpse.Age < MinCorpseAge) continue;
                if (corpse.Bagged) continue;
                if (corpse.Dissolving || corpse.ConsumedBySummon) continue;
                float dx = corpse.Position.X - myPos.X;
                float dy = corpse.Position.Y - myPos.Y;
                float d2 = dx * dx + dy * dy;
                if (d2 < bestDistSq) { bestDistSq = d2; bestIdx = c; }
            }
            if (bestIdx >= 0)
            {
                units[u].CorpseEatTargetID = corpses[bestIdx].CorpseID;
                units[u].CorpseEatTimer = EatDuration;
                DebugLog.Log("scenario", $"[CorpseEat] {units[u].UnitDefID}#{u} begins eating corpse#{corpses[bestIdx].CorpseID} (age={corpses[bestIdx].Age:F1}s)");
            }
        }
    }
}
