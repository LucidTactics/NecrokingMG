using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.Game;
using Necroking.Movement;

namespace Necroking.GameSystems;

/// <summary>
/// Dominions magic-resistance penetration check (manual p.37/64). A spell flagged
/// <see cref="SpellDef.ChecksMagicResist"/> only affects a target if the caster's
/// penetration beats the target's Magic Resistance, rolled opposed:
///     (penetration + DRN)  vs  (MR + DRN)
/// The spell's easy/hard tag shifts penetration by ∓4 (a Dominions-style spell
/// difficulty: "Hard" is harder to resist, "Easy" is easier).
/// </summary>
public static class SpellPenetration
{
    /// <summary>Baseline penetration before caster path mastery and the easy/hard
    /// modifier. Tuned so a default caster vs a default MR-10 target is ~50/50.</summary>
    public const int BasePenetration = 10;

    /// <summary>Penetration = base + caster's level in the spell's primary path +
    /// the spell's easy/hard modifier (Hard +4, Easy −4).</summary>
    public static int Compute(GameData gameData, UnitArrays units, int casterIdx, SpellDef spell)
    {
        int pen = BasePenetration;
        if (gameData != null && casterIdx >= 0 && casterIdx < units.Count)
        {
            var def = gameData.Units.Get(units[casterIdx].UnitDefID);
            var pri = spell.GetPrimary();
            if (def != null && pri.HasRequirement)
                pen += def.GetPathLevel(pri.Path);
        }
        pen += spell.ResistDifficultyMod;
        return pen;
    }

    /// <summary>Roll the opposed penetration-vs-MR check. True = the spell gets
    /// through. Target MR includes buff/debuff modifiers.</summary>
    public static bool Penetrates(UnitArrays units, int targetIdx, int penetration)
    {
        if (targetIdx < 0 || targetIdx >= units.Count) return true;
        int mr = (int)BuffSystem.GetModifiedStat(units, targetIdx, BuffStat.MagicResist,
            units[targetIdx].Stats.MagicResist);
        return penetration + UnitUtil.RollDRN() > mr + UnitUtil.RollDRN();
    }

    /// <summary>Convenience gate: returns true if the spell should affect the
    /// target — either it isn't MR-checked, or it penetrated the target's MR.</summary>
    public static bool Affects(GameData gameData, UnitArrays units, int casterIdx, int targetIdx, SpellDef spell)
    {
        if (spell == null || !spell.ChecksMagicResist) return true;
        return Penetrates(units, targetIdx, Compute(gameData, units, casterIdx, spell));
    }
}
