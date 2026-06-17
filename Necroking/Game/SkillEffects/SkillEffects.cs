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
        Register("grant_intrinsic_buff", new GrantIntrinsicBuffEffect());
        Register("unlock_ai_behavior",   new UnlockAIBehaviorEffect());
        Register("unlock_potion_slot",   new UnlockPotionSlotEffect());
        Register("cap_buff",             new CapBuffEffect());
        Register("grant_path",           new GrantPathEffect());
        Register("unlock_summon",        new UnlockSummonEffect());
        Register("compound",             new CompoundEffect());
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

/// <summary>Bind a buff permanently to every unit carrying a tag set.
/// Arg format: "buff_id:tag1,tag2,..." — e.g. "buff_wolf_pounce:wolf,zombie".
/// Tags are AND-matched against UnitDef.Tags. On learn:
///   1. The binding is recorded on BookState so future spawns inherit it.
///   2. Every currently-alive matching unit gets the buff applied right now.
/// Granted weapons (from BuffDef.GrantedWeapons) layer into the unit's
/// effective weapon list via BuffSystem.ApplyBuff. The buff is permanent
/// (duration ≤ 0) so it never expires on its own.</summary>
public sealed class GrantIntrinsicBuffEffect : ISkillEffect
{
    public bool Apply(SkillEffectContext ctx, string arg)
    {
        if (string.IsNullOrEmpty(arg))
        {
            DebugLog.Log("skillbook", "grant_intrinsic_buff: empty arg");
            return true;
        }
        var (buffId, tags) = ParseArg(arg);
        if (string.IsNullOrEmpty(buffId))
        {
            DebugLog.Log("skillbook", $"grant_intrinsic_buff: missing buff id in '{arg}'");
            return false;
        }
        var buffDef = ctx.GameData.Buffs.Get(buffId);
        if (buffDef == null)
        {
            DebugLog.Log("skillbook", $"grant_intrinsic_buff: buff '{buffId}' not in registry");
            return false;
        }
        ctx.BookState.AddIntrinsicBuff(buffId, tags);

        // Apply to live units. New spawns are handled by the spawn-path hook
        // in Simulation.SpawnUnitByID — this loop covers the "buy the skill
        // mid-game; existing zombies should benefit too" case.
        if (ctx.Sim != null)
        {
            int applied = 0;
            var units = ctx.Sim.UnitsMut;
            for (int i = 0; i < units.Count; i++)
            {
                if (!units[i].Alive) continue;
                var def = ctx.GameData.Units.Get(units[i].UnitDefID);
                if (def == null || !def.HasAllTags(tags)) continue;
                GameSystems.BuffSystem.ApplyBuff(units, i, buffDef, ctx.GameData);
                applied++;
            }
            DebugLog.Log("skillbook", $"grant_intrinsic_buff: {buffId} -> [{string.Join(",", tags)}], applied to {applied} existing unit(s)");
        }
        else
        {
            DebugLog.Log("skillbook", $"grant_intrinsic_buff: {buffId} -> [{string.Join(",", tags)}] (no sim, deferred to spawn)");
        }
        return true;
    }

    private static (string buffId, List<string> tags) ParseArg(string arg)
    {
        int colon = arg.IndexOf(':');
        if (colon < 0) return (arg.Trim(), new List<string>());
        string id = arg.Substring(0, colon).Trim();
        string tagPart = arg.Substring(colon + 1);
        var tags = new List<string>();
        foreach (var t in tagPart.Split(','))
        {
            var trimmed = t.Trim();
            if (trimmed.Length > 0) tags.Add(trimmed);
        }
        return (id, tags);
    }
}

/// <summary>Unlock an AI behavior (e.g. "corpse_eat", with optional max-stack
/// payload). Arg format: "behavior" or "behavior:N" (N defaults to 1).
/// Behaviors are consulted by AI handlers at runtime — see CorpseEaterAI etc.
/// The integer payload is monotone (UnlockAI in BookState ignores downgrades),
/// so learning Corpse Eater (1) then Improved Corpse Eating (2) leaves the
/// behavior at 2 stacks; learning them out of order produces the same end
/// state.</summary>
public sealed class UnlockAIBehaviorEffect : ISkillEffect
{
    public bool Apply(SkillEffectContext ctx, string arg)
    {
        if (string.IsNullOrEmpty(arg)) return true;
        string behavior = arg;
        int payload = 1;
        int colon = arg.IndexOf(':');
        if (colon >= 0)
        {
            behavior = arg.Substring(0, colon).Trim();
            int.TryParse(arg.Substring(colon + 1), out payload);
        }
        ctx.BookState.UnlockAI(behavior, payload);
        DebugLog.Log("skillbook", $"unlock_ai_behavior: '{behavior}' (payload={payload})");
        return true;
    }
}

/// <summary>Bump <see cref="SkillBookState.PotionSlotsUnlocked"/> by <paramref name="arg"/>
/// (defaults to 1). Read by the necromancer crafting table to decide how many
/// potion-slot widgets to surface.</summary>
public sealed class UnlockPotionSlotEffect : ISkillEffect
{
    public bool Apply(SkillEffectContext ctx, string arg)
    {
        int amount = 1;
        if (!string.IsNullOrEmpty(arg)) int.TryParse(arg, out amount);
        ctx.BookState.AddPotionSlot(amount);
        DebugLog.Log("skillbook", $"unlock_potion_slot: +{amount} (total={ctx.BookState.PotionSlotsUnlocked})");
        return true;
    }
}

/// <summary>Raise a horde-cap on the necromancer. Arg format: "monster:N" or
/// "human:N" — applies N stacks of buff_monster_cap / buff_human_cap. Each
/// stack adds +1 to MonsterCap / HumanCap (see HordeCapTracker.GetCap, which
/// sums Add-type effects on those stat names). Permanent buff (duration 0)
/// so the cap stays raised for the run.</summary>
public sealed class CapBuffEffect : ISkillEffect
{
    public bool Apply(SkillEffectContext ctx, string arg)
    {
        if (ctx.Sim == null)
        {
            DebugLog.Log("skillbook", $"cap_buff: no sim, can't apply '{arg}'");
            return true; // soft-pass: skill learns, buff is reapplied next game
        }
        int idx = ctx.Sim.NecromancerIndex;
        if (idx < 0)
        {
            DebugLog.Log("skillbook", "cap_buff: no necromancer in sim");
            return true;
        }
        int colon = arg.IndexOf(':');
        string kind = colon >= 0 ? arg.Substring(0, colon).Trim() : arg.Trim();
        int n = 1;
        if (colon >= 0) int.TryParse(arg.Substring(colon + 1), out n);
        if (n < 1) n = 1;

        string buffId = kind == "human" ? "buff_human_cap" : "buff_monster_cap";
        var buffDef = ctx.GameData.Buffs.Get(buffId);
        if (buffDef == null)
        {
            DebugLog.Log("skillbook", $"cap_buff: buff '{buffId}' not in registry");
            return false;
        }
        for (int i = 0; i < n; i++)
            GameSystems.BuffSystem.ApplyBuff(ctx.Sim.UnitsMut, idx, buffDef, ctx.GameData);
        DebugLog.Log("skillbook", $"cap_buff: applied {n} stacks of {buffId} to necromancer");
        return true;
    }
}

/// <summary>Grant magic-path levels to the player necromancer via a permanent,
/// code-built buff (no buffs.json entry needed). Arg: "shock:1" or "shock"
/// (level defaults to 1). The buff carries one Add effect on
/// <see cref="GameSystems.BuffSystem.PathStat"/>, so EffectivePathLevel — and
/// therefore spell gating, mana-cost scaling, and the unit sheet — picks it up
/// immediately. Soft-passes (the skill still learns) when there's no live sim /
/// necromancer, mirroring cap_buff.</summary>
public sealed class GrantPathEffect : ISkillEffect
{
    public bool Apply(SkillEffectContext ctx, string arg)
    {
        if (string.IsNullOrEmpty(arg)) return true;

        int colon = arg.IndexOf(':');
        string pathId = colon >= 0 ? arg.Substring(0, colon).Trim() : arg.Trim();
        int level = 1;
        if (colon >= 0) int.TryParse(arg.Substring(colon + 1), out level);
        if (level < 1) level = 1;

        var path = Data.Registries.MagicPathHelpers.FromJsonId(pathId);
        if (path == Data.Registries.MagicPath.None)
        {
            DebugLog.Log("skillbook", $"grant_path: unknown path '{pathId}' in '{arg}'");
            return false;
        }

        if (ctx.Sim == null)
        {
            DebugLog.Log("skillbook", $"grant_path: no sim, '{arg}' deferred (reapplied next game)");
            return true; // soft-pass: skill learns, buff is reapplied next game
        }
        int idx = ctx.Sim.NecromancerIndex;
        if (idx < 0)
        {
            DebugLog.Log("skillbook", "grant_path: no necromancer in sim");
            return true;
        }

        var buff = new Data.Registries.BuffDef
        {
            Id = $"buff_path_{pathId}",
            DisplayName = $"+{level} {path} Path",
            Duration = 0f,    // permanent (Duration <= 0 => Permanent in ApplyBuff)
            Intrinsic = true, // no buff-bar icon / combat-log spam
            MaxStacks = 1,
            Effects =
            {
                new Data.Registries.BuffEffect
                {
                    Type = "Add",
                    Stat = GameSystems.BuffSystem.PathStat(path),
                    Value = level,
                },
            },
        };
        GameSystems.BuffSystem.ApplyBuff(ctx.Sim.UnitsMut, idx, buff, ctx.GameData);
        DebugLog.Log("skillbook", $"grant_path: +{level} {path} path to necromancer (idx={idx})");
        return true;
    }
}

/// <summary>Mark one or more UnitDef IDs as available to reanimation/summoning.
/// Arg = comma-separated id list. The reanimation flow filters its catalogue
/// by this set, so "raise this wolf corpse" only succeeds after Wolf Autopsy
/// has unlocked the ZombieWolf entry.</summary>
public sealed class UnlockSummonEffect : ISkillEffect
{
    public bool Apply(SkillEffectContext ctx, string arg)
    {
        if (string.IsNullOrEmpty(arg)) return true;
        int n = 0;
        foreach (var part in arg.Split(','))
        {
            var id = part.Trim();
            if (id.Length == 0) continue;
            ctx.BookState.UnlockSummon(id);
            n++;
        }
        DebugLog.Log("skillbook", $"unlock_summon: +{n} entries ({arg})");
        return true;
    }
}

/// <summary>Run several effects in sequence under a single skill node. Arg
/// format: "effect1=arg1|effect2=arg2|...". The arg uses '|' as the entry
/// separator and '=' between effect-name and effect-arg so commas/colons
/// (already used by individual effects' own grammars) stay free of conflict.
/// All sub-effects run with the same context; the compound returns false
/// (and the learn rolls back) if any sub-effect returns false.</summary>
public sealed class CompoundEffect : ISkillEffect
{
    public bool Apply(SkillEffectContext ctx, string arg)
    {
        if (string.IsNullOrEmpty(arg)) return true;
        foreach (var entry in arg.Split('|'))
        {
            var trimmed = entry.Trim();
            if (trimmed.Length == 0) continue;
            int eq = trimmed.IndexOf('=');
            string subEffect = eq >= 0 ? trimmed.Substring(0, eq).Trim() : trimmed;
            string subArg    = eq >= 0 ? trimmed.Substring(eq + 1) : "";
            if (!SkillEffectRegistry.Apply(subEffect, ctx, subArg)) return false;
        }
        return true;
    }
}
