using System.Collections.Generic;
using Necroking.Core;
using Necroking.Data;
using Necroking.Game.SkillEffects;
using Necroking.GameSystems;

namespace Necroking.Game;

/// <summary>
/// Per-game state for the skill book — which skills are learned, plus the
/// event-tally tracker. No save/load yet (see SkillEventTracker for the same
/// caveat). Each instance is one playthrough.
/// </summary>
public class SkillBookState
{
    private readonly HashSet<string> _learned = new();
    public SkillEventTracker Events { get; } = new();

    public bool IsLearned(string skillId) => _learned.Contains(skillId);

    /// <summary>Initialize from defs: pre-marks any skill flagged StartLearned. Call
    /// after SkillBookDefs.Load() at startup, and again when starting a new game.</summary>
    public void InitFromDefs()
    {
        _learned.Clear();
        Events.Reset();
        foreach (var tab in SkillBookDefs.Tabs)
            foreach (var s in tab.Skills)
                if (s.StartLearned) _learned.Add(s.Id);
    }

    /// <summary>True if all parents of <paramref name="def"/> are learned (AND).</summary>
    public bool ArePrereqsMet(SkillDef def)
    {
        foreach (var p in def.Parents)
            if (!_learned.Contains(p)) return false;
        return true;
    }

    public bool CanAffordCost(in SkillCost c, Inventory inv)
    {
        if (c.Type == "item")  return inv.GetItemCount(c.Id) >= c.Amount;
        if (c.Type == "event") return Events.Get(c.Id) >= c.Amount;
        return false;
    }

    public bool CanAfford(SkillDef def, Inventory inv)
    {
        foreach (var c in def.Costs)
            if (!CanAffordCost(c, inv)) return false;
        return true;
    }

    /// <summary>The skill is visible/clickable but disabled because some cost is unmet.</summary>
    public bool IsAvailableUnaffordable(SkillDef def, Inventory inv)
        => !IsLearned(def.Id) && ArePrereqsMet(def) && !CanAfford(def, inv);

    /// <summary>The skill is fully unlockable right now.</summary>
    public bool IsAvailableAffordable(SkillDef def, Inventory inv)
        => !IsLearned(def.Id) && ArePrereqsMet(def) && CanAfford(def, inv);

    /// <summary>(learned, total) for one tab — used in the tab header.</summary>
    public (int learned, int total) GetProgress(SkillTab tab)
    {
        int n = 0;
        foreach (var s in tab.Skills) if (_learned.Contains(s.Id)) n++;
        return (n, tab.Skills.Count);
    }

    /// <summary>Learn a skill: deduct item costs, run the effect, mark learned.
    /// Returns false (and changes nothing) if not currently affordable/available.</summary>
    public bool TryLearn(SkillDef def, SkillEffectContext ctx)
    {
        if (IsLearned(def.Id)) return false;
        if (!ArePrereqsMet(def)) return false;
        if (!CanAfford(def, ctx.Inventory)) return false;

        // Deduct item costs (events are milestones, not consumed).
        foreach (var c in def.Costs)
            if (c.Type == "item") ctx.Inventory.RemoveItem(c.Id, c.Amount);

        if (!SkillEffectRegistry.Apply(def.Effect, ctx, def.EffectArg))
        {
            DebugLog.Log("skillbook", $"Effect '{def.Effect}' for skill '{def.Id}' returned false; learn aborted.");
            // Roll back item deduction (best-effort).
            foreach (var c in def.Costs)
                if (c.Type == "item") ctx.Inventory.AddItem(c.Id, c.Amount);
            return false;
        }

        _learned.Add(def.Id);
        DebugLog.Log("skillbook", $"Learned: {def.Id} ({def.Name})");
        return true;
    }
}
