using System;
using System.Collections.Generic;
using Necroking.Core;
using Necroking.Data;
using Necroking.Movement;
using Necroking.Spatial;

namespace Necroking.GameSystems;

/// <summary>
/// Damage types determine how damage is applied after formula calculation.
/// Physical → HP reduction. Poison → poison stack accumulation.
/// </summary>
public enum DamageType : byte
{
    Physical,
    Poison,
}

/// <summary>
/// Flags controlling which parts of the damage formula are skipped.
/// </summary>
[Flags]
public enum DamageFlags : byte
{
    None = 0,
    ArmorNegating = 1,    // Bypass armor/protection reduction
    DefenseNegating = 2,  // Bypass defense check (spells always have this)
}

/// <summary>
/// Unified damage system. All damage in the game flows through here.
///
/// Physical damage: raw → armor reduction → HP loss + DamageEvent
/// Poison damage:   raw → armor reduction → poison stacks + DamageEvent (green)
///
/// Armor reduction is skipped when ArmorNegating flag is set.
/// Defense checks are handled by the caller (melee combat) not here.
/// </summary>
public static class DamageSystem
{
    /// <summary>
    /// Apply damage through the full formula: armor reduction → type-specific application.
    /// Use for spell damage, trap damage, cloud ticks, etc.
    /// </summary>
    /// <param name="units">Unit array (mutable)</param>
    /// <param name="targetIdx">Index of unit receiving damage</param>
    /// <param name="rawDamage">Pre-reduction damage amount</param>
    /// <param name="type">Physical (HP loss) or Poison (stack accumulation)</param>
    /// <param name="flags">ArmorNegating, DefenseNegating</param>
    /// <param name="damageEvents">List to append visual damage events to</param>
    /// <param name="attackerIdx">Optional attacker for aggro/LastAttackerID</param>
    public static void Apply(UnitArrays units, int targetIdx, int rawDamage,
        DamageType type, DamageFlags flags, List<DamageEvent> damageEvents,
        int attackerIdx = -1)
    {
        if (targetIdx < 0 || targetIdx >= units.Count || !units[targetIdx].Alive) return;
        if (rawDamage <= 0) return;

        // Ghost mode: immune to all damage
        if (units[targetIdx].GhostMode) return;

        // Armor reduction (unless ArmorNegating)
        int finalDamage = rawDamage;
        if ((flags & DamageFlags.ArmorNegating) == 0)
        {
            int prot = units[targetIdx].Stats.NaturalProt + units[targetIdx].Stats.Armor.BodyProtection;
            finalDamage = Math.Max(1, rawDamage - prot);
        }

        // Apply based on damage type
        switch (type)
        {
            case DamageType.Physical:
                units[targetIdx].Stats.HP -= finalDamage;
                units[targetIdx].HitReacting = true;
                if (units[targetIdx].Stats.HP <= 0)
                {
                    units[targetIdx].Stats.HP = 0;
                    units[targetIdx].Alive = false;
                }
                break;

            case DamageType.Poison:
                units[targetIdx].PoisonStacks += finalDamage;
                units[targetIdx].HitReacting = true;
                if (units[targetIdx].PoisonTickTimer <= 0f)
                    units[targetIdx].PoisonTickTimer = 3f;
                break;
        }

        // Set attacker for AI aggro/flee
        if (attackerIdx >= 0 && attackerIdx < units.Count)
        {
            units[targetIdx].LastAttackerID = units[attackerIdx].Id;

            if (units[targetIdx].EngagedTarget.IsNone
                && units[targetIdx].AI != AIBehavior.FleeWhenHit
                && units[targetIdx].AI != AIBehavior.PlayerControlled)
            {
                units[targetIdx].EngagedTarget = CombatTarget.Unit(units[attackerIdx].Id);
                units[targetIdx].Target = units[targetIdx].EngagedTarget;
            }
        }

        // Visual damage number
        damageEvents.Add(new DamageEvent
        {
            Position = units[targetIdx].Position,
            Damage = finalDamage,
            Height = 1.5f,
            IsPoison = type == DamageType.Poison,
        });
    }

    /// <summary>
    /// Apply pre-calculated damage (armor already handled by caller, e.g. melee combat).
    /// Only does HP reduction, death check, attacker tracking, and damage event.
    /// </summary>
    public static void ApplyDirect(UnitArrays units, int targetIdx, int netDamage,
        List<DamageEvent> damageEvents, int attackerIdx = -1)
    {
        if (targetIdx < 0 || targetIdx >= units.Count || !units[targetIdx].Alive) return;

        if (units[targetIdx].GhostMode) netDamage = 0;

        units[targetIdx].Stats.HP -= netDamage;

        if (attackerIdx >= 0 && attackerIdx < units.Count)
        {
            units[targetIdx].LastAttackerID = units[attackerIdx].Id;

            if (units[targetIdx].EngagedTarget.IsNone
                && units[targetIdx].AI != AIBehavior.FleeWhenHit
                && units[targetIdx].AI != AIBehavior.PlayerControlled)
            {
                units[targetIdx].EngagedTarget = CombatTarget.Unit(units[attackerIdx].Id);
                units[targetIdx].Target = units[targetIdx].EngagedTarget;
            }
        }

        damageEvents.Add(new DamageEvent
        {
            Position = units[targetIdx].Position,
            Damage = netDamage,
            Height = 1.5f,
        });

        if (units[targetIdx].Stats.HP <= 0)
        {
            units[targetIdx].Alive = false;
            units[targetIdx].Stats.HP = 0;
        }
    }

    /// <summary>
    /// Apply damage to all enemies within an AoE radius. Shared helper used by
    /// Cloud spells, glyph traps, and any future AoE damage source.
    /// </summary>
    public static void ApplyAoE(UnitArrays units, Quadtree qt, Vec2 center, float radius,
        int damage, DamageType type, DamageFlags flags,
        Faction ownerFaction, List<DamageEvent> damageEvents)
    {
        if (damage <= 0) return;

        var nearbyIDs = new List<uint>();
        qt.QueryRadius(center, radius, nearbyIDs);
        foreach (uint uid in nearbyIDs)
        {
            int idx = UnitUtil.ResolveUnitIndex(units, uid);
            if (idx < 0 || !units[idx].Alive) continue;
            if (units[idx].Faction == ownerFaction) continue;
            Apply(units, idx, damage, type, flags, damageEvents);
        }
    }
}
