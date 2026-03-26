using System.Collections.Generic;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.Movement;

namespace Necroking.GameSystems;

public enum CastResult { Success, NotEnoughMana, OnCooldown, NoValidTarget, OutOfRange, NoNecromancer }

public class PendingSpellCast
{
    public string SpellID = "";
    public string Category = "Projectile";
    public Vec2 TargetPos;
    public int TargetCorpseIdx = -1;
    public int TargetUnitIdx = -1;
    public uint TargetUnitID = GameConstants.InvalidUnit;
    public string SummonUnitID = "";
    public int RemainingProjectiles;
    public float ProjectileTimer;
    public string CastingBuffID = "";
    public bool Active;
}

public static class SpellCaster
{
    public static CastResult TryStartSpellCast(
        string spellID, SpellRegistry spells, NecromancerState necro,
        UnitArrays units, int necroIdx, Vec2 mouseWorld,
        IReadOnlyList<Corpse> corpses, PendingSpellCast outPending)
    {
        if (necroIdx < 0) return CastResult.NoNecromancer;

        var spell = spells.Get(spellID);
        if (spell == null) return CastResult.NoValidTarget;

        if (necro.GetCooldown(spellID) > 0f) return CastResult.OnCooldown;
        if (necro.Mana < spell.ManaCost) return CastResult.NotEnoughMana;

        // Range check
        var necroPos = units.Position[necroIdx];
        float dist = (mouseWorld - necroPos).Length();
        if (dist > spell.Range) return CastResult.OutOfRange;

        // Consume mana and set cooldown
        necro.Mana -= spell.ManaCost;
        if (spell.Cooldown > 0f)
            necro.SpellCooldowns[spellID] = spell.Cooldown;

        // Set up pending cast
        outPending.SpellID = spellID;
        outPending.Category = spell.Category;
        outPending.TargetPos = mouseWorld;
        outPending.RemainingProjectiles = spell.Quantity;
        outPending.ProjectileTimer = 0f;
        outPending.CastingBuffID = spell.CastingBuffID;
        outPending.Active = true;

        return CastResult.Success;
    }
}
