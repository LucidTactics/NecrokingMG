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
        // Stubs — log only. Wire to real systems when the corresponding gameplay is in.
        Register("unlock_potion",   new LogStubEffect("unlock_potion"));
        Register("unlock_unit",     new LogStubEffect("unlock_unit"));
        Register("unlock_building", new LogStubEffect("unlock_building"));
        Register("passive_stat",    new LogStubEffect("passive_stat"));
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
