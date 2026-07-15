using System;
using System.Collections.Generic;
using System.Globalization;
using Necroking.Core;

namespace Necroking.Data.Registries;

/// <summary>Which spell property a mastery bonus modifies.</summary>
public enum MasteryEffect { Fatigue, Free, Damage, Range, Aoe, Duration, Buff }

/// <summary>One parsed line of a spell's <c>masteryBonuses</c> list.</summary>
public sealed class MasteryBonus
{
    /// <summary>True for "x:" lines — the magnitude scales with the caster's
    /// levels above the requirement. False for "+N:" threshold lines.</summary>
    public bool PerLevel;
    /// <summary>Threshold lines: the bonus is active once x >= Level.</summary>
    public int Level = 1;
    public MasteryEffect Effect;
    public float Amount;
    /// <summary>Amount is a percentage of the base value (a trailing '%').</summary>
    public bool Percent;
    /// <summary>Buff effect: the buff id to apply.</summary>
    public string BuffId = "";
    /// <summary>Buff effect: apply to the caster instead of the spell target.</summary>
    public bool SelfTarget;
    /// <summary>The source line, for tooltips/diagnostics.</summary>
    public string Raw = "";
}

/// <summary>
/// The spell "mastery" mini-language: per-spell bonuses granted for casting with
/// path levels ABOVE the spell's primary requirement (x = caster level − required
/// level, primary path only). Designed to be written by hand in
/// <c>data/spells.json</c> (and the in-game spell editor) like a modding language.
///
/// <para>Each line of <see cref="SpellDef.MasteryBonuses"/> is
/// <c>"&lt;trigger&gt;: &lt;effect&gt;"</c>:</para>
/// <list type="bullet">
///   <item><c>+N:</c> — threshold, active once x >= N (e.g. <c>+2: free</c>)</item>
///   <item><c>x:</c> — per-level, magnitude multiplied by x (e.g. <c>x: damage +5</c>
///     shows as "Damage: 15 + 5x" in the tooltip)</item>
/// </list>
/// <para>Effects:</para>
/// <list type="bullet">
///   <item><c>fatigue -30%</c> — fatigue (mana) cost reduction. Thresholds don't
///     stack: the highest active reduction wins. A per-level fatigue line reduces
///     by Amount×x %. Clamped to 100%.</item>
///   <item><c>free</c> — the spell costs nothing (threshold form).</item>
///   <item><c>damage +5</c> / <c>damage +25%</c> — bonus damage (flat or % of base).</item>
///   <item><c>range +2</c> / <c>range +10%</c> — bonus cast range.</item>
///   <item><c>aoe +1.5</c> / <c>aoe +25%</c> — bonus area radius.</item>
///   <item><c>duration +3</c> / <c>duration +50%</c> — Buff/Debuff spells: the
///     applied buff lasts longer (seconds or % of the buff's base duration).</item>
///   <item><c>buff &lt;buffId&gt;</c> / <c>buff &lt;buffId&gt; self</c> — additionally
///     apply a buff to the spell's target (or the caster with <c>self</c>).</item>
/// </list>
/// Unparseable lines are skipped with a DebugLog warning (and shown raw in the
/// tooltip so a typo is visible rather than silently dead).
/// </summary>
public static class SpellMastery
{
    /// <summary>Parse one masteryBonuses line. Returns false (error set) on a
    /// line that doesn't match the grammar.</summary>
    public static bool TryParse(string line, out MasteryBonus bonus, out string error)
    {
        bonus = new MasteryBonus { Raw = line ?? "" };
        error = "";
        if (string.IsNullOrWhiteSpace(line)) { error = "empty line"; return false; }

        int colon = line.IndexOf(':');
        if (colon <= 0) { error = "missing ':' after trigger"; return false; }

        // --- trigger ---
        string trig = line.Substring(0, colon).Trim().TrimStart('+');
        if (trig.Equals("x", StringComparison.OrdinalIgnoreCase) || trig == "*")
        {
            bonus.PerLevel = true;
        }
        else if (int.TryParse(trig, NumberStyles.Integer, CultureInfo.InvariantCulture, out int lvl) && lvl >= 1)
        {
            bonus.Level = lvl;
        }
        else { error = $"bad trigger '{trig}' (want +N or x)"; return false; }

        // --- effect ---
        var tokens = line.Substring(colon + 1).Trim()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0) { error = "missing effect"; return false; }

        string kind = tokens[0].ToLowerInvariant();
        switch (kind)
        {
            case "free":
                bonus.Effect = MasteryEffect.Free;
                return true;

            case "buff":
                if (tokens.Length < 2) { error = "buff needs a buff id"; return false; }
                bonus.Effect = MasteryEffect.Buff;
                bonus.BuffId = tokens[1];
                bonus.SelfTarget = tokens.Length >= 3
                    && tokens[2].Equals("self", StringComparison.OrdinalIgnoreCase);
                return true;

            case "fatigue":   bonus.Effect = MasteryEffect.Fatigue;  break;
            case "damage":    bonus.Effect = MasteryEffect.Damage;   break;
            case "range":     bonus.Effect = MasteryEffect.Range;    break;
            case "aoe":       bonus.Effect = MasteryEffect.Aoe;      break;
            case "duration":  bonus.Effect = MasteryEffect.Duration; break;
            default:
                error = $"unknown effect '{kind}'";
                return false;
        }

        // Numeric effects: "<kind> [+|-]<num>[%]"
        if (tokens.Length < 2) { error = $"'{kind}' needs an amount"; return false; }
        string num = tokens[1];
        if (num.EndsWith("%")) { bonus.Percent = true; num = num.Substring(0, num.Length - 1); }
        if (!float.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out float amount))
        {
            error = $"bad amount '{tokens[1]}'";
            return false;
        }
        // Fatigue is a REDUCTION — "-30%" and "30%" mean the same thing.
        bonus.Amount = bonus.Effect == MasteryEffect.Fatigue ? MathF.Abs(amount) : amount;
        return true;
    }

    /// <summary>Parse a whole masteryBonuses list, logging (once per call) any
    /// bad lines. Bad lines are dropped from the result.</summary>
    public static List<MasteryBonus> ParseAll(IReadOnlyList<string>? lines, string spellIdForLog = "")
    {
        var result = new List<MasteryBonus>();
        if (lines == null) return result;
        foreach (var line in lines)
        {
            if (TryParse(line, out var b, out string err)) result.Add(b);
            else DebugLog.Log("warn", $"spell '{spellIdForLog}': bad masteryBonuses line '{line}': {err}");
        }
        return result;
    }

    /// <summary>Multiplier on the spell's mana/fatigue cost at x levels above the
    /// requirement: 1 = full cost, 0 = free. Threshold fatigue lines don't stack —
    /// the highest active reduction wins; a per-level line contributes Amount×x.
    /// An active <c>free</c> line zeroes the cost outright.</summary>
    public static float FatigueCostMultiplier(IReadOnlyList<MasteryBonus> bonuses, int x)
    {
        if (x <= 0 || bonuses.Count == 0) return 1f;
        float reduction = 0f;
        foreach (var b in bonuses)
        {
            switch (b.Effect)
            {
                case MasteryEffect.Free:
                    if (!b.PerLevel && x >= b.Level) return 0f;
                    break;
                case MasteryEffect.Fatigue:
                    float r = b.PerLevel ? b.Amount * x : (x >= b.Level ? b.Amount : 0f);
                    if (r > reduction) reduction = r;
                    break;
            }
        }
        return Math.Clamp(1f - reduction / 100f, 0f, 1f);
    }

    /// <summary>Apply every active bonus of one numeric kind to a base stat value.
    /// Per-level lines add Amount×x (or Amount%×x of base); threshold lines add
    /// Amount once when reached. Different lines stack additively.</summary>
    public static float ApplyStat(IReadOnlyList<MasteryBonus> bonuses, MasteryEffect kind,
        float baseValue, int x)
    {
        if (x <= 0 || bonuses.Count == 0) return baseValue;
        float bonus = 0f;
        foreach (var b in bonuses)
        {
            if (b.Effect != kind) continue;
            float steps = b.PerLevel ? x : (x >= b.Level ? 1f : 0f);
            if (steps <= 0f) continue;
            bonus += (b.Percent ? baseValue * b.Amount / 100f : b.Amount) * steps;
        }
        return baseValue + bonus;
    }

    /// <summary>Active buff-application perks (<c>buff &lt;id&gt; [self]</c> lines
    /// whose threshold x has reached). Executed by SpellEffectSystem after the
    /// spell's own effect.</summary>
    public static void CollectBuffPerks(IReadOnlyList<MasteryBonus> bonuses, int x,
        List<MasteryBonus> outActive)
    {
        if (x <= 0) return;
        foreach (var b in bonuses)
            if (b.Effect == MasteryEffect.Buff && !b.PerLevel && x >= b.Level)
                outActive.Add(b);
    }

    // ═══════════════════════════════════════
    //  Tooltip text
    // ═══════════════════════════════════════

    /// <summary>Human-readable description of one bonus line's effect (without
    /// the trigger prefix) — shared by the in-game tooltip and the editor.</summary>
    public static string DescribeEffect(MasteryBonus b)
    {
        string pct = b.Percent ? "%" : "";
        return b.Effect switch
        {
            MasteryEffect.Free => "free to cast",
            MasteryEffect.Fatigue => $"fatigue cost -{b.Amount:0.#}%",
            MasteryEffect.Damage => $"damage +{b.Amount:0.#}{pct}",
            MasteryEffect.Range => $"range +{b.Amount:0.#}{pct}",
            MasteryEffect.Aoe => $"area +{b.Amount:0.#}{pct}",
            MasteryEffect.Duration => b.Percent
                ? $"duration +{b.Amount:0.#}%" : $"duration +{b.Amount:0.#}s",
            MasteryEffect.Buff => b.SelfTarget
                ? $"also gain {b.BuffId}" : $"also applies {b.BuffId}",
            _ => b.Raw,
        };
    }
}
