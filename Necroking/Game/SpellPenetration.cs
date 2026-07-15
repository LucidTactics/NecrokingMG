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
                // Effective (buff-inclusive) level, matching spell gating and mana
                // cost — a grant_path/AllPaths buff must also raise penetration.
                pen += BuffSystem.EffectivePathLevel(units, casterIdx, def, pri.Path);
        }
        pen += spell.ResistDifficultyMod;
        return pen;
    }

    /// <summary>Caster-side DRN tier for a spell contest (damage roll, MR
    /// penetration): the spell's authored drn override when set (1-4), else the
    /// caster's own tier, else the default tier 2 (casterIdx may be -1 —
    /// trap-fired spells, or the caster died before resolution; note a spell
    /// override survives caster death, keeping the spell's dice deterministic).</summary>
    public static int CasterRollTier(SpellDef? spell, UnitArrays units, int casterIdx)
    {
        if (spell != null && spell.Drn > 0) return spell.Drn > 4 ? 4 : spell.Drn;
        return casterIdx >= 0 && casterIdx < units.Count ? units[casterIdx].Stats.Drn : 2;
    }

    /// <summary>Roll the opposed penetration-vs-MR check. True = the spell gets
    /// through. Target MR includes buff/debuff modifiers. The caster side rolls
    /// at <see cref="CasterRollTier"/> (spell drn override, else the unit's
    /// tier); the target always rolls its own tier.</summary>
    public static bool Penetrates(UnitArrays units, int casterIdx, int targetIdx, int penetration,
        SpellDef? spell = null)
    {
        if (targetIdx < 0 || targetIdx >= units.Count) return true;
        int mr = (int)BuffSystem.GetModifiedStat(units, targetIdx, BuffStat.MagicResist,
            units[targetIdx].Stats.MagicResist);
        return penetration + UnitUtil.RollDRN(CasterRollTier(spell, units, casterIdx))
             > mr + UnitUtil.RollDRN(units[targetIdx].Stats.Drn);
    }

    /// <summary>Convenience gate: returns true if the spell should affect the
    /// target — either it isn't MR-checked, or it penetrated the target's MR.</summary>
    public static bool Affects(GameData gameData, UnitArrays units, int casterIdx, int targetIdx, SpellDef spell)
    {
        if (spell == null || !spell.ChecksMagicResist) return true;
        return Penetrates(units, casterIdx, targetIdx, Compute(gameData, units, casterIdx, spell), spell);
    }
}
