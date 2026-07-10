using System;
using System.Collections.Generic;
using Necroking.Core;
using Necroking.Data;
using Necroking.Movement;
using Necroking.Spatial;

namespace Necroking.GameSystems;

/// <summary>
/// Damage types determine how damage is applied after formula calculation.
/// Physical → HP reduction. Poison → poison stack accumulation. Fatigue → drains
/// the unit's Fatigue meter (capped at 100) instead of HP; never kills directly.
/// </summary>
public enum DamageType : byte
{
    Physical,
    Poison,
    Fatigue,
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
    // --- Reaction (flinch / dodge) animation tuning ---
    /// <summary>How long the BlockReact flinch shows on the legacy render path.</summary>
    public const float HitReactShowSeconds = 0.35f;
    /// <summary>A unit can't start another reaction anim (flinch OR dodge) within
    /// this window. Prevents focus fire / repeated whiffs from perpetually
    /// twitching a unit so it can never act.</summary>
    public const float ReactionCooldownSeconds = 0.6f;
    /// <summary>Duration of the cosmetic white sprite flash on a physical impact.
    /// This is the feedback channel that survives every reaction-anim suppression
    /// (fleeing, mid-attack, cooldown) — a hit always visibly lands.</summary>
    public const float HitFlashSeconds = 0.15f;

    /// <summary>
    /// Shared gates for both reaction anims (BlockReact flinch, Dodge). A reaction
    /// is skipped when the unit is:
    ///   - knocked down / mid-jump (would pop the hold/jump anim),
    ///   - fleeing or routing (it should keep running, not react — a fleeing unit
    ///     keeps its run cycle even when hit or whiffed at),
    ///   - still inside the shared reaction cooldown (twitch guard).
    /// </summary>
    private static bool ReactionAllowed(UnitArrays units, int idx)
    {
        if (idx < 0 || idx >= units.Count) return false;
        if (units[idx].Incap.Active || units[idx].JumpPhase != 0) return false;
        if (units[idx].Fleeing || units[idx].Routing) return false;
        if (units[idx].ReactionCooldownTimer > 0f) return false;
        return true;
    }

    /// <summary>
    /// Apply the on-hit flinch (BlockReact) to a unit, honoring all suppression rules
    /// in ONE place — always call this instead of poking AnimResolver.SetOverride
    /// directly for a hit reaction (gates: see ReactionAllowed). Poison/fatigue never
    /// call this (they're DoT/meters, not impacts), so they don't flinch.
    ///
    /// The flinch goes in at Reaction priority (1), BELOW Combat (2): a unit mid-swing
    /// keeps its attack anim — the reaction request is simply rejected — and the
    /// shared cooldown is only started when the reaction actually won the slot, so
    /// the first hit after the swing ends still flinches.
    /// </summary>
    public static void ApplyHitReactAnim(UnitArrays units, int idx)
    {
        if (idx < 0 || idx >= units.Count) return;
        // Cosmetic flash fires on EVERY physical impact, before the anim gates —
        // a hit whose flinch is suppressed must still read as a hit.
        units[idx].HitFlashTimer = HitFlashSeconds;

        if (!ReactionAllowed(units, idx)) return;

        var handle = Render.AnimResolver.SetOverride(units[idx],
            Render.AnimRequest.Reaction(Render.AnimState.BlockReact));
        if (!handle.IsValid && units[idx].Archetype > 0) return; // lost to a live attack anim

        units[idx].HitReactTimer = HitReactShowSeconds;
        units[idx].ReactionCooldownTimer = ReactionCooldownSeconds;
    }

    /// <summary>
    /// Apply the whiffed-at Dodge reaction anim (attacker missed the defender).
    /// Same single-choke-point contract and gates as ApplyHitReactAnim — call this,
    /// never SetOverride(Dodge) directly. Purely visual: the caller owns the
    /// gameplay side (Dodging flag, Harassment) unconditionally.
    /// NOTE: TrampleSystem's dodge-hop does NOT route through here — that dodge is
    /// a movement-owning gameplay action (Combat priority, owns the hop), not a
    /// cosmetic reaction; it starts the shared cooldown itself.
    /// </summary>
    public static void ApplyDodgeAnim(UnitArrays units, int idx)
    {
        if (!ReactionAllowed(units, idx)) return;

        var handle = Render.AnimResolver.SetOverride(units[idx],
            Render.AnimRequest.Reaction(Render.AnimState.Dodge));
        if (!handle.IsValid && units[idx].Archetype > 0) return; // lost to a live attack anim

        units[idx].ReactionCooldownTimer = ReactionCooldownSeconds;
    }

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
    /// <summary>THE toughness formula — the single place damage meets hide. The
    /// first <paramref name="toughness"/> points of post-armor damage are halved:
    /// net = D − min(D, T)/2. Small hits get cut in half, big hits lose at most
    /// T/2 — so a tough beast shrugs chip damage but never becomes immune.
    /// Callers apply armor (and any piercing/AP/armor-defeating fraction, which
    /// cuts toughness by the same fraction) BEFORE calling this.</summary>
    public static int MitigateByToughness(int postArmorDamage, float toughness)
    {
        if (postArmorDamage <= 0) return 0;
        if (toughness <= 0f) return postArmorDamage;
        return postArmorDamage - Math.Min(postArmorDamage, (int)toughness) / 2;
    }

    public static void Apply(UnitArrays units, int targetIdx, int rawDamage,
        DamageType type, DamageFlags flags, List<DamageEvent> damageEvents,
        int attackerIdx = -1)
    {
        if (targetIdx < 0 || targetIdx >= units.Count || !units[targetIdx].Alive) return;
        if (rawDamage <= 0) return;

        // Ghost mode: immune to all damage
        if (units[targetIdx].GhostMode) return;

        // Armor block then toughness halving (unless ArmorNegating, which skips both)
        int finalDamage = rawDamage;
        if ((flags & DamageFlags.ArmorNegating) == 0)
        {
            int postArmor = rawDamage - units[targetIdx].Stats.Armor.BodyProtection;
            float toughness = BuffSystem.GetModifiedStat(units, targetIdx,
                BuffStat.Toughness, units[targetIdx].Stats.Toughness);
            finalDamage = Math.Max(1, MitigateByToughness(postArmor, toughness));
        }

        // Apply based on damage type
        switch (type)
        {
            case DamageType.Physical:
                units[targetIdx].Stats.HP -= finalDamage;
                units[targetIdx].HitReacting = true;
                // Flinch (BlockReact) — gated by ApplyHitReactAnim (skips fleeing /
                // knocked-down / mid-jump / refractory units). HitReacting stays set
                // unconditionally so AI flee/retarget reactions still fire.
                ApplyHitReactAnim(units, targetIdx);
                if (units[targetIdx].Stats.HP <= 0)
                    Kill(units, targetIdx);
                break;

            case DamageType.Poison:
                units[targetIdx].PoisonStacks += finalDamage;
                units[targetIdx].HitReacting = true;
                if (units[targetIdx].PoisonTickTimer <= 0f)
                    units[targetIdx].PoisonTickTimer = 3f;
                DebugLog.Log("ai", $"[DmgSys] Poison applied to unit#{targetIdx} " +
                    $"stacks+={finalDamage} total={units[targetIdx].PoisonStacks} " +
                    $"hitReact=true faction={units[targetIdx].Faction}");
                break;

            case DamageType.Fatigue:
                // Fatigue caps at 100 (defense roll uses (100 - Fatigue), and a fully-
                // fatigued unit has 0 defense). No HitReacting / BlockReact — fatigue
                // doesn't trigger combat-hit anims because it isn't HP loss.
                units[targetIdx].Fatigue = MathF.Min(100f, units[targetIdx].Fatigue + finalDamage);
                break;
        }

        // Set attacker for AI aggro/flee
        StampAttacker(units, targetIdx, attackerIdx);

        // Visual damage number — physical and fatigue both surface a number (fatigue
        // shows in blue so the player can tell stamina drain from HP loss). Poison
        // stack-adds are silent; the green number is reserved for HP-drain DoT ticks
        // so the player can't confuse "stacks piling on" with "HP actually lost".
        if (type == DamageType.Physical)
        {
            damageEvents.Add(DamageEvent.Create(units[targetIdx].RenderPos, finalDamage));
        }
        else if (type == DamageType.Fatigue)
        {
            damageEvents.Add(DamageEvent.Create(units[targetIdx].RenderPos, finalDamage, isFatigue: true));
        }
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

        StampAttacker(units, targetIdx, attackerIdx);

        damageEvents.Add(DamageEvent.Create(units[targetIdx].RenderPos, netDamage));

        if (units[targetIdx].Stats.HP <= 0)
            Kill(units, targetIdx);
    }

    /// <summary>
    /// Finalize a unit's death: zero HP, clear Alive, force the Death anim, and
    /// snap-to-final-frame if the unit dies prone (MarkDeathFromProne). The ONLY
    /// sanctioned way to flip Alive=false — corpse creation, kill tallies, and
    /// attribution stay centralized in Simulation.RemoveDeadUnits, which keys off
    /// Alive == false + LastAttackerID.
    /// </summary>
    public static void Kill(UnitArrays units, int idx)
    {
        if (idx < 0 || idx >= units.Count) return;
        units[idx].Stats.HP = 0;
        units[idx].Alive = false;
        Render.AnimResolver.SetOverride(units[idx], Render.AnimRequest.Forced(Render.AnimState.Death));
        MarkDeathFromProne(units, idx);
    }

    /// <summary>
    /// Stamp attribution (LastAttackerID) and auto-engage the victim onto its
    /// attacker so the melee queue runs. Shared tail of Apply and ApplyDirect.
    /// DeerHerd prey is exempt from auto-engage (was AIBehavior.FleeWhenHit): the
    /// handler owns its flee/fight-back reaction and a stamped EngagedTarget would
    /// drag a fleeing deer into the melee queue. Deliberately does NOT flinch —
    /// Apply's Physical branch flinches via ApplyHitReactAnim; ApplyDirect's melee
    /// callers flinch themselves.
    /// </summary>
    private static void StampAttacker(UnitArrays units, int targetIdx, int attackerIdx)
    {
        if (attackerIdx < 0 || attackerIdx >= units.Count) return;

        units[targetIdx].LastAttackerID = units[attackerIdx].Id;

        if (units[targetIdx].EngagedTarget.IsNone
            && units[targetIdx].Archetype != AI.ArchetypeRegistry.DeerHerd
            && units[targetIdx].AI != AIBehavior.PlayerControlled)
        {
            units[targetIdx].EngagedTarget = CombatTarget.Unit(units[attackerIdx].Id);
            units[targetIdx].Target = units[targetIdx].EngagedTarget;
        }
    }

    /// <summary>
    /// If the unit is dying while already knocked down (incap'd), snap the Death
    /// anim to its final frame instead of playing it from standing pose —
    /// AnimResolver's HoldAtEnd path handles this as long as Active=true and
    /// Recovering=false. Without this, a prone dying unit would visibly stand up
    /// to play Death.
    /// </summary>
    private static void MarkDeathFromProne(UnitArrays units, int targetIdx)
    {
        if (!units[targetIdx].Incap.Active) return;
        var incap = units[targetIdx].Incap;
        incap.HoldAtEnd = true;
        incap.Recovering = false;
        units[targetIdx].Incap = incap;
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
        qt.QueryRadiusByFaction(center, radius, FactionMaskExt.AllExcept(ownerFaction), nearbyIDs);
        foreach (uint uid in nearbyIDs)
        {
            int idx = UnitUtil.ResolveUnitIndex(units, uid);
            if (idx < 0 || !units[idx].Alive) continue;
            Apply(units, idx, damage, type, flags, damageEvents);
        }
    }
}
