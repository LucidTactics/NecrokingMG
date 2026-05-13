using System;
using System.Collections.Generic;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;

namespace Necroking.Game.SkillEffects;

/// <summary>
/// Context passed to a skill effect when the player learns a node.
/// Add references here as new effect types need access to more game systems.
/// </summary>
public class SkillEffectContext
{
    public Inventory Inventory = null!;
    public GameData GameData = null!;
    public SpellBarState PrimaryBar;
    public SpellBarState SecondaryBar;
    /// <summary>Required by effects that mutate book state — unlock_potion writes
    /// to BookState.UnlockedPotions, passive_stat sets passive flags.</summary>
    public SkillBookState BookState = null!;
    /// <summary>Optional. Used by gameplay effects (passive_stat applying a buff
    /// to the necromancer, morph_necromancer swapping UnitDefID, ...). Can be
    /// null in scenario / test paths that learn skills without a live sim.</summary>
    public Simulation? Sim;
}

public interface ISkillEffect
{
    /// <summary>Run the action this skill grants. Called once at the moment of unlock,
    /// after costs have been deducted. Return false if the effect failed and the
    /// learn should be rolled back. Most effects can just return true.</summary>
    bool Apply(SkillEffectContext ctx, string arg);
}

public static class SkillEffectRegistry
{
    private static readonly Dictionary<string, ISkillEffect> _effects = new();

    static SkillEffectRegistry()
    {
        Register("noop",      new NoOpEffect());
        Register("add_spell", new AddSpellToBarEffect());
        Register("unlock_potion",     new UnlockPotionEffect());
        Register("passive_stat",      new PassiveStatEffect());
        Register("morph_necromancer", new MorphNecromancerEffect());
        Register("metamorph_action",  new MetamorphActionEffect());
        // Stubs — log only. Wire to real systems when the corresponding gameplay is in.
        Register("unlock_unit",     new LogStubEffect("unlock_unit"));
        Register("unlock_building", new LogStubEffect("unlock_building"));
    }

    public static void Register(string id, ISkillEffect effect) => _effects[id] = effect;

    public static bool Apply(string id, SkillEffectContext ctx, string arg)
    {
        if (!_effects.TryGetValue(id, out var fx))
        {
            DebugLog.Log("skillbook", $"Unknown skill effect '{id}' — treating as noop.");
            return true;
        }
        return fx.Apply(ctx, arg);
    }
}

public sealed class NoOpEffect : ISkillEffect
{
    public bool Apply(SkillEffectContext ctx, string arg) => true;
}

public sealed class LogStubEffect : ISkillEffect
{
    private readonly string _kind;
    public LogStubEffect(string kind) { _kind = kind; }
    public bool Apply(SkillEffectContext ctx, string arg)
    {
        DebugLog.Log("skillbook", $"[stub] {_kind}: {arg}");
        return true;
    }
}

/// <summary>Adds the potion id (arg) to the book's UnlockedPotions set so the
/// crafting menu surfaces it. The same skill can re-fire (LearnFree on auto-
/// learn, then re-allocate from UI) without duplicating — the set is idempotent.</summary>
public sealed class UnlockPotionEffect : ISkillEffect
{
    public bool Apply(SkillEffectContext ctx, string arg)
    {
        if (string.IsNullOrEmpty(arg)) return true;
        ctx.BookState.UnlockPotion(arg);
        DebugLog.Log("skillbook", $"unlock_potion: '{arg}' added to unlocked set");
        return true;
    }
}

/// <summary>Sets a boolean passive flag (arg) on the book state AND, for known
/// passives, applies the matching gameplay effect on the necromancer:
///   - unholy_movement      → permanent buff_unholy_movement on necro
///   - unholy_strength      → permanent buff_unholy_strength on necro
///   - death_fog_consumption→ flag-only (Game1.UpdateGameplay checks the flag
///                            each tick and bumps NecroState.BonusManaRegen
///                            while the necro stands in fog density > 0)
///   - efficient_tinctures  → flag-only (CraftingMenuUI tests the flag)
/// Other args fall through as flag-only — same behaviour as before.</summary>
public sealed class PassiveStatEffect : ISkillEffect
{
    public bool Apply(SkillEffectContext ctx, string arg)
    {
        if (string.IsNullOrEmpty(arg)) return true;
        ctx.BookState.SetPassive(arg);
        DebugLog.Log("skillbook", $"passive_stat: '{arg}' flag set");

        if (ctx.Sim != null)
        {
            int necroIdx = ctx.Sim.NecromancerIndex;
            if (necroIdx >= 0)
            {
                string? buffId = arg switch
                {
                    "unholy_movement" => "buff_unholy_movement",
                    "unholy_strength" => "buff_unholy_strength",
                    _ => null,
                };
                if (buffId != null)
                {
                    var def = ctx.GameData.Buffs.Get(buffId);
                    if (def != null)
                    {
                        BuffSystem.ApplyBuff(ctx.Sim.UnitsMut, necroIdx, def);
                        DebugLog.Log("skillbook", $"passive_stat: applied {buffId} to necromancer");
                    }
                    else
                    {
                        DebugLog.Log("skillbook", $"passive_stat: buff '{buffId}' not found in registry");
                    }
                }
            }
        }
        return true;
    }
}

/// <summary>Morph the player necromancer into a different UnitDef.
/// arg = target UnitDefID. The target def must have PlayerForm=true to be a
/// valid morph target. Rebuilds stats (HP, Mana, Speed, etc.) from the new def;
/// re-applies any active passive buffs the player already learned so an
/// Unholy Strength acquired before the morph survives the transformation.
/// Records the morph in BookState passive flags as "morphed:&lt;id&gt;" so future
/// systems can introspect which form is current without inspecting Simulation.</summary>
public sealed class MorphNecromancerEffect : ISkillEffect
{
    public bool Apply(SkillEffectContext ctx, string arg)
    {
        if (string.IsNullOrEmpty(arg))
        {
            DebugLog.Log("skillbook", "morph_necromancer: empty arg");
            return false;
        }
        if (ctx.Sim == null)
        {
            DebugLog.Log("skillbook", $"morph_necromancer: no sim, can't morph to '{arg}' (test path?)");
            return true;
        }
        int idx = ctx.Sim.NecromancerIndex;
        if (idx < 0)
        {
            DebugLog.Log("skillbook", "morph_necromancer: no necromancer in sim");
            return false;
        }
        var def = ctx.GameData.Units.Get(arg);
        if (def == null)
        {
            DebugLog.Log("skillbook", $"morph_necromancer: UnitDef '{arg}' not found");
            return false;
        }
        if (!def.PlayerForm)
        {
            DebugLog.Log("skillbook", $"morph_necromancer: '{arg}' is not flagged PlayerForm — refusing");
            return false;
        }

        ctx.Sim.TransformUnit(idx, arg);
        ctx.BookState.SetPassive($"morphed:{arg}");
        DebugLog.Log("skillbook", $"morph_necromancer: necromancer morphed to '{arg}'");

        // Re-apply any passive buffs that were learned earlier in the run so a
        // morph doesn't silently strip them (TransformUnit rebuilds Stats from
        // the new def and may clear the active buff list depending on impl;
        // safer to re-apply explicitly).
        ReapplyPassiveBuff(ctx, idx, "unholy_movement", "buff_unholy_movement");
        ReapplyPassiveBuff(ctx, idx, "unholy_strength", "buff_unholy_strength");

        return true;
    }

    private static void ReapplyPassiveBuff(SkillEffectContext ctx, int idx, string flag, string buffId)
    {
        if (!ctx.BookState.HasPassive(flag)) return;
        var def = ctx.GameData.Buffs.Get(buffId);
        if (def == null) return;
        // Skip if already on the unit (idempotent reapply).
        var actives = ctx.Sim!.UnitsMut[idx].ActiveBuffs;
        for (int i = 0; i < actives.Count; i++)
            if (actives[i].BuffDefID == buffId) return;
        BuffSystem.ApplyBuff(ctx.Sim.UnitsMut, idx, def);
    }
}

/// <summary>Active-ability metamorphosis skills (Corpse Eating, Soul Consumption).
/// On learn, the skill registers a flag on BookState so Game1 can offer a corpse-
/// targeting picker in the Character Stats panel. The actual consume action runs
/// inside Game1.PerformMetamorphActiveOnCorpse(corpseIdx).</summary>
public sealed class MetamorphActionEffect : ISkillEffect
{
    public bool Apply(SkillEffectContext ctx, string arg)
    {
        if (string.IsNullOrEmpty(arg)) return true;
        ctx.BookState.SetPassive($"action:{arg}");
        DebugLog.Log("skillbook", $"metamorph_action: '{arg}' learned (target-corpse picker enabled)");
        return true;
    }
}

/// <summary>Adds the spell id (arg) to the first empty slot of the primary spell bar,
/// or secondary bar if the primary is full. Silently skips if both bars are full.</summary>
public sealed class AddSpellToBarEffect : ISkillEffect
{
    public bool Apply(SkillEffectContext ctx, string arg)
    {
        if (string.IsNullOrEmpty(arg)) return true;
        if (TryFill(ctx.PrimaryBar, arg)) return true;
        if (TryFill(ctx.SecondaryBar, arg)) return true;
        DebugLog.Log("skillbook", $"add_spell: both bars full, '{arg}' not assigned.");
        return true;
    }

    private static bool TryFill(SpellBarState bar, string spellId)
    {
        if (bar.Slots == null) return false;
        for (int i = 0; i < bar.Slots.Length; i++)
        {
            if (string.IsNullOrEmpty(bar.Slots[i].SpellID))
            {
                bar.Slots[i].SpellID = spellId;
                return true;
            }
        }
        return false;
    }
}
