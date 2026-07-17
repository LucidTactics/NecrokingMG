using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.Movement;

namespace Necroking.GameSystems;

/// <summary>
/// Read-only view over the simulation's live undead population, sliced by
/// <see cref="UndeadCategory"/>. The actual cap values live on
/// <see cref="NecromancerState"/> (MonsterCap, HumanCap); this helper just
/// counts what's currently using a slot and answers "how many more of this
/// category can I summon right now?"
///
/// A unit counts against a cap iff:
///   • Alive
///   • Faction == Undead
///   • AI != PlayerControlled (necromancer evolutions don't count)
///   • UnitDef.UndeadCategory == the queried category
///
/// Temporary summons aren't implemented yet; when they are they'll either
/// stay UndeadCategory.None or carry a runtime "temporary" flag that this
/// helper can short-circuit on.
/// </summary>
public static class HordeCapTracker
{
    /// <summary>Count of alive permanent undead minions in the given category.</summary>
    public static int CountUsed(UnitArrays units, GameData gameData, UndeadCategory cat)
    {
        if (gameData == null || cat == UndeadCategory.None) return 0;
        int n = 0;
        for (int i = 0; i < units.Count; i++)
        {
            if (!units[i].Alive) continue;
            if (units[i].Faction != Faction.Undead) continue;
            if (units[i].AI == AIBehavior.PlayerControlled) continue;
            // Wild undead aren't the player's minions (yet) — they must not eat
            // the summon cap while standing around unrecruited.
            if (units[i].Archetype == AI.ArchetypeRegistry.WildUndead) continue;
            var def = gameData.Units.Get(units[i].UnitDefID);
            if (def == null) continue;
            if (def.UndeadCategory != cat) continue;
            n++;
        }
        return n;
    }

    /// <summary>Resolve the effective cap value for a category — base value
    /// from <see cref="NecromancerState"/> plus any buff "Add" effects whose
    /// Stat is "MonsterCap" or "HumanCap" active on the necromancer
    /// (<paramref name="necroIdx"/> &lt; 0 skips the buff lookup, returning
    /// the raw base value).</summary>
    public static int GetCap(UnitArrays units, int necroIdx, NecromancerState necro, UndeadCategory cat)
    {
        int baseCap = cat switch
        {
            UndeadCategory.Human => necro.HumanCap,
            UndeadCategory.Monster => necro.MonsterCap,
            _ => int.MaxValue, // None = no cap (shouldn't be queried, but safe default)
        };
        if (cat == UndeadCategory.None || necroIdx < 0) return baseCap;
        string statName = cat == UndeadCategory.Human ? "HumanCap" : "MonsterCap";
        return baseCap + (int)BuffSystem.SumExtraAdd(units, necroIdx, statName);
    }

    /// <summary>How many more of <paramref name="cat"/> the player can summon
    /// right now without going over cap. Clamped to ≥ 0.</summary>
    public static int Available(UnitArrays units, GameData gameData,
        NecromancerState necro, UndeadCategory cat)
    {
        if (cat == UndeadCategory.None) return int.MaxValue;
        int necroIdx = FindNecromancer(units);
        int cap = GetCap(units, necroIdx, necro, cat);
        int used = CountUsed(units, gameData, cat);
        int avail = cap - used;
        return avail < 0 ? 0 : avail;
    }

    /// <summary>Find the player-controlled necromancer unit, or -1.</summary>
    private static int FindNecromancer(UnitArrays units) => units.FindAliveNecromancerIndex();

    /// <summary>Resolve the UndeadCategory that <paramref name="summonUnitDefID"/>
    /// would consume. Returns None when the def is missing or the unit doesn't
    /// participate in cap enforcement (cosmetic-only undead, player forms).</summary>
    public static UndeadCategory CategoryFor(GameData gameData, string summonUnitDefID)
    {
        if (gameData == null || string.IsNullOrEmpty(summonUnitDefID)) return UndeadCategory.None;
        var def = gameData.Units.Get(summonUnitDefID);
        return def?.UndeadCategory ?? UndeadCategory.None;
    }
}
