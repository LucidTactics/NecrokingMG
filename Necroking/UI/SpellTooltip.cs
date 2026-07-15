using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.GameSystems;

namespace Necroking.UI;

/// <summary>
/// The ONE builder for a spell's hover tooltip — used by the spell bar
/// (HUDRenderer) and the grimoire (GrimoireOverlay), so cost/requirement/mastery
/// info can't drift between the two. Produces colored lines for
/// <see cref="TooltipSystem.RequestLines(IReadOnlyList{ValueTuple{string, Color}})"/>:
/// header stats first, then a "Mastery" section describing what each level above
/// the primary-path requirement grants, with the caster's reached bonuses lit.
/// Player-facing wording: the mana resource is presented as "fatigue".
/// </summary>
public static class SpellTooltip
{
    public static readonly Color Text = new(220, 220, 240);
    public static readonly Color Dim = new(150, 150, 170);
    public static readonly Color Header = new(235, 210, 140);   // mastery header + formulas
    public static readonly Color Reached = new(150, 235, 140);  // bonus the caster has
    public static readonly Color Locked = new(120, 120, 145);   // bonus still above the caster

    /// <summary>Build the tooltip lines for a spell. <paramref name="casterIdx"/>
    /// &lt; 0 = no caster context (all mastery lines neutral, base cost shown).
    /// <paramref name="inventory"/> adds the potion-charges line when set.</summary>
    public static List<(string Text, Color Color)> BuildLines(SpellDef sp, GameData gameData,
        Simulation sim, int casterIdx, Inventory? inventory = null)
    {
        var lines = new List<(string, Color)>();
        lines.Add((gameData.Spells.NameOf(sp.Id), Text));

        string kind = !string.IsNullOrEmpty(sp.School) ? sp.School
                    : !string.IsNullOrEmpty(sp.Category) ? sp.Category : "";
        if (kind.Length > 0) lines.Add((kind, Dim));

        // Caster context: effective path levels -> x (levels above the primary req).
        bool hasCaster = casterIdx >= 0 && casterIdx < sim.Units.Count;
        Func<MagicPath, int>? lvl = hasCaster
            ? SpellCaster.ResolveCasterLevel(
                gameData.Units.Get(sim.Units[casterIdx].UnitDefID), sim.Units, casterIdx)
            : null;
        int x = lvl != null ? sp.MasteryLevels(lvl) : 0;

        // Cost / cooldown — only the parts that apply. Cost is the EFFECTIVE
        // fatigue (what a cast actually deducts); shown as "base -> now" when a
        // mastery bonus is reducing it.
        var stats = new List<string>();
        if (sp.ManaCost > 0f)
        {
            float eff = sp.EffectiveManaCostAt(x);
            stats.Add(MathF.Abs(eff - sp.ManaCost) > 0.005f
                ? $"{sp.ManaCost:0.#} -> {eff:0.#} fatigue"
                : $"{sp.ManaCost:0.#} fatigue");
        }
        if (sp.Cooldown > 0f) stats.Add($"{sp.Cooldown:0.#}s cooldown");
        if (stats.Count > 0) lines.Add((string.Join("   ", stats), Text));

        // Path requirement + the caster's own levels.
        var pri = sp.GetPrimary();
        var sec = sp.GetSecondary();
        if (pri.HasRequirement)
        {
            string req = $"Requires {pri.Path} {pri.Level}";
            if (sec.HasRequirement) req += $", {sec.Path} {sec.Level}";
            if (lvl != null)
            {
                req += $"  (you: {lvl(pri.Path)}";
                if (sec.HasRequirement) req += $", {lvl(sec.Path)}";
                req += ")";
            }
            lines.Add((req, Dim));
        }

        // Consumable charges (potion-spells): how many the player holds.
        if (inventory != null && !string.IsNullOrEmpty(sp.ConsumesItem))
        {
            var item = gameData.Items.Get(sp.ConsumesItem);
            string itemName = item != null && item.DisplayName.Length > 0
                ? item.DisplayName : sp.ConsumesItem;
            lines.Add(($"Held: {inventory.GetItemCount(sp.ConsumesItem)} {itemName}", Text));
        }

        AppendMasterySection(lines, sp, gameData, x, hasCaster: lvl != null);
        return lines;
    }

    /// <summary>The per-level bonus section: header, "base + N x" formula lines
    /// for per-level bonuses, then the threshold perks sorted by level.</summary>
    private static void AppendMasterySection(List<(string, Color)> lines, SpellDef sp,
        GameData gameData, int x, bool hasCaster)
    {
        var bonuses = sp.GetMasteryBonuses();
        var pri = sp.GetPrimary();
        if (bonuses.Count == 0 || !pri.HasRequirement) return;

        lines.Add(("", Text));
        string hdr = $"Mastery - per {pri.Path} level above {pri.Level}";
        if (hasCaster) hdr += $"  (you: +{x})";
        lines.Add((hdr, Header));

        // Per-level scaling first: "Damage: 15 + 5x  (now 25)".
        foreach (var b in bonuses)
        {
            if (!b.PerLevel) continue;
            lines.Add(("  " + FormatScaling(sp, gameData, b, x),
                x > 0 ? Reached : (hasCaster ? Locked : Text)));
        }

        // Threshold perks, lowest level first.
        var thresholds = new List<MasteryBonus>();
        foreach (var b in bonuses) if (!b.PerLevel) thresholds.Add(b);
        thresholds.Sort((a, b) => a.Level.CompareTo(b.Level));
        foreach (var b in thresholds)
        {
            bool reached = hasCaster && x >= b.Level;
            lines.Add(($"  +{b.Level}: {DescribeEffect(gameData, b)}",
                reached ? Reached : (hasCaster ? Locked : Text)));
        }
    }

    /// <summary>Formula display for an "x:" line — "Damage: 15 + 5x", plus the
    /// caster's current total ("(now 25)") once x &gt; 0.</summary>
    private static string FormatScaling(SpellDef sp, GameData gameData, MasteryBonus b, int x)
    {
        if (b.Effect == MasteryEffect.Fatigue)
            return $"Fatigue: -{b.Amount:0.#}% per level";

        (string label, float baseVal) = b.Effect switch
        {
            MasteryEffect.Damage => ("Damage", (float)sp.Damage),
            MasteryEffect.Range => ("Range", sp.Range),
            MasteryEffect.Aoe => ("Area", sp.AoeRadius > 0 ? sp.AoeRadius : sp.CloudRadius),
            MasteryEffect.Duration => ("Duration",
                gameData.Buffs.Get(sp.BuffID)?.Duration ?? 0f),
            _ => (b.Raw, 0f),
        };
        string amount = b.Percent ? $"{b.Amount:0.#}%" : $"{b.Amount:0.#}";
        string s = $"{label}: {baseVal:0.#} + {amount}x";
        if (x > 0)
        {
            float now = SpellMastery.ApplyStat(sp.GetMasteryBonuses(), b.Effect, baseVal, x);
            s += $"  (now {now:0.#})";
        }
        return s;
    }

    /// <summary>DescribeEffect, upgraded with registry display names (a "buff
    /// buff_stoneskin" perk reads as its in-game buff name).</summary>
    private static string DescribeEffect(GameData gameData, MasteryBonus b)
    {
        if (b.Effect == MasteryEffect.Buff)
        {
            var def = gameData.Buffs.Get(b.BuffId);
            string name = def != null && def.DisplayName.Length > 0 ? def.DisplayName : b.BuffId;
            return b.SelfTarget ? $"also gain {name}" : $"also applies {name}";
        }
        return SpellMastery.DescribeEffect(b);
    }
}
