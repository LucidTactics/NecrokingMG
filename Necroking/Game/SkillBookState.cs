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

    /// <summary>Per-tab hidden skill-point pools. Currently only "potions" is awarded
    /// (one per potion crafted) — other tabs default to 0 until their own earn paths
    /// are wired. Used by SkillCost.Type == "skillpoints" (cost.Id = tab name).</summary>
    private readonly Dictionary<string, int> _skillPoints = new();

    /// <summary>Potions unlocked by the skill tree. Empty until the player learns
    /// the root, then grows as branches are spent. The crafting menu filters its
    /// recipe list by this set.</summary>
    private readonly HashSet<string> _unlockedPotions = new();

    /// <summary>Boolean passive flags toggled on by passive_stat skills. Lookup
    /// by the effectArg string (e.g. "efficient_tinctures"). Cheaper than a real
    /// passive stack since these are simple on/off perks.</summary>
    private readonly HashSet<string> _passiveFlags = new();

    public bool IsLearned(string skillId) => _learned.Contains(skillId);

    public int  GetSkillPoints(string pool)             => _skillPoints.TryGetValue(pool, out var v) ? v : 0;
    public void AddSkillPoints(string pool, int amount) { _skillPoints[pool] = GetSkillPoints(pool) + amount; }

    public bool IsPotionUnlocked(string potionId) => _unlockedPotions.Contains(potionId);
    public void UnlockPotion(string potionId)     => _unlockedPotions.Add(potionId);
    public IReadOnlyCollection<string> UnlockedPotions => _unlockedPotions;

    public bool HasPassive(string flag) => _passiveFlags.Contains(flag);
    public void SetPassive(string flag) => _passiveFlags.Add(flag);

    // Metamorphosis active-ability progression. Each use of the matching action
    // grants +1 max stat (capped). The capped accumulator is separate from
    // gameplay max-HP/max-mana because those come from the live UnitDef and can
    // change with morphs — we layer this delta on top inside Simulation.
    public const int CorpseEatingHPCap     = 5;
    public const int SoulConsumptionManaCap = 10;
    public int CorpseEatingBonus { get; private set; }
    public int SoulConsumptionBonus { get; private set; }
    public bool TryGrantCorpseEatingBonus()
    {
        if (CorpseEatingBonus >= CorpseEatingHPCap) return false;
        CorpseEatingBonus++; return true;
    }
    public bool TryGrantSoulConsumptionBonus()
    {
        if (SoulConsumptionBonus >= SoulConsumptionManaCap) return false;
        SoulConsumptionBonus++; return true;
    }

    /// <summary>Initialize from defs: pre-marks any skill flagged StartLearned. Call
    /// after SkillBookDefs.Load() at startup, and again when starting a new game.</summary>
    public void InitFromDefs()
    {
        _learned.Clear();
        _skillPoints.Clear();
        _unlockedPotions.Clear();
        _passiveFlags.Clear();
        Events.Reset();
        foreach (var tab in SkillBookDefs.Tabs)
            foreach (var s in tab.Skills)
                if (s.StartLearned) _learned.Add(s.Id);
    }

    /// <summary>True if every Parents entry (AND) is learned AND at least one
    /// ParentsAny entry (OR) is learned, if any are listed. Empty lists pass
    /// trivially. Independent of exclusion checks (see <see cref="IsExcluded"/>).</summary>
    public bool ArePrereqsMet(SkillDef def)
    {
        foreach (var p in def.Parents)
            if (!_learned.Contains(p)) return false;
        if (def.ParentsAny.Count > 0)
        {
            bool anyMet = false;
            foreach (var p in def.ParentsAny)
                if (_learned.Contains(p)) { anyMet = true; break; }
            if (!anyMet) return false;
        }
        return true;
    }

    /// <summary>True if any mutex partner of <paramref name="def"/> is already
    /// learned. Such a skill is blocked from being learned regardless of cost.</summary>
    public bool IsExcluded(SkillDef def)
    {
        foreach (var e in def.ExclusiveOf)
            if (_learned.Contains(e)) return true;
        return false;
    }

    /// <summary>If the skill is currently blocked by an exclusivity partner,
    /// returns the name of the partner that's blocking it (for error toasts).
    /// Empty when the skill is not excluded.</summary>
    public string ExclusionBlocker(SkillDef def)
    {
        foreach (var e in def.ExclusiveOf)
        {
            if (_learned.Contains(e))
            {
                var other = FindSkill(e);
                return other?.Name ?? e;
            }
        }
        return "";
    }

    public bool CanAffordCost(in SkillCost c, Inventory inv)
    {
        if (c.Type == "item")        return inv.GetItemCount(c.Id) >= c.Amount;
        if (c.Type == "event")       return Events.Get(c.Id) >= c.Amount;
        if (c.Type == "skillpoints") return GetSkillPoints(c.Id) >= c.Amount;
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
        => !IsLearned(def.Id) && ArePrereqsMet(def) && !IsExcluded(def) && !CanAfford(def, inv);

    /// <summary>The skill is fully unlockable right now.</summary>
    public bool IsAvailableAffordable(SkillDef def, Inventory inv)
        => !IsLearned(def.Id) && ArePrereqsMet(def) && !IsExcluded(def) && CanAfford(def, inv);

    /// <summary>(learned, total) for one tab — used in the tab header.</summary>
    public (int learned, int total) GetProgress(SkillTab tab)
    {
        int n = 0;
        foreach (var s in tab.Skills) if (_learned.Contains(s.Id)) n++;
        return (n, tab.Skills.Count);
    }

    /// <summary>Find a skill by id across all tabs. Returns null if not found.</summary>
    public SkillDef? FindSkill(string id)
    {
        foreach (var tab in SkillBookDefs.Tabs)
        {
            int idx = tab.IndexOf(id);
            if (idx >= 0) return tab.Skills[idx];
        }
        return null;
    }

    /// <summary>Learn a skill for free (no cost deduction, no prereq check) — for
    /// gameplay triggers like "picking up your first mushroom teaches you Healing
    /// Brew." Idempotent: re-calling on an already-learned skill is a no-op.</summary>
    public bool LearnFree(string skillId, SkillEffectContext ctx)
    {
        if (IsLearned(skillId)) return false;
        var def = FindSkill(skillId);
        if (def == null) return false;
        if (!SkillEffectRegistry.Apply(def.Effect, ctx, def.EffectArg)) return false;
        _learned.Add(def.Id);
        DebugLog.Log("skillbook", $"Auto-learned: {def.Id} ({def.Name})");
        return true;
    }

    /// <summary>Learn a skill: deduct item costs, run the effect, mark learned.
    /// Returns false (and changes nothing) if not currently affordable/available.</summary>
    public bool TryLearn(SkillDef def, SkillEffectContext ctx)
    {
        if (IsLearned(def.Id)) return false;
        if (!ArePrereqsMet(def)) return false;
        if (IsExcluded(def)) return false;
        if (!CanAfford(def, ctx.Inventory)) return false;

        // Deduct item + skillpoint costs (events are milestones, not consumed).
        foreach (var c in def.Costs)
        {
            if (c.Type == "item")        ctx.Inventory.RemoveItem(c.Id, c.Amount);
            else if (c.Type == "skillpoints") AddSkillPoints(c.Id, -c.Amount);
        }

        if (!SkillEffectRegistry.Apply(def.Effect, ctx, def.EffectArg))
        {
            DebugLog.Log("skillbook", $"Effect '{def.Effect}' for skill '{def.Id}' returned false; learn aborted.");
            // Roll back deductions (best-effort).
            foreach (var c in def.Costs)
            {
                if (c.Type == "item")        ctx.Inventory.AddItem(c.Id, c.Amount);
                else if (c.Type == "skillpoints") AddSkillPoints(c.Id, c.Amount);
            }
            return false;
        }

        _learned.Add(def.Id);
        DebugLog.Log("skillbook", $"Learned: {def.Id} ({def.Name})");
        return true;
    }
}
