using System;

namespace Necroking.Data;

/// <summary>
/// Dominions weapon damage type. Determines how a weapon interacts with the
/// damage-vs-protection calculation (manual p.60-61):
///   Slashing — +25% damage AFTER protection is deducted; can chop limbs/heads
///              on a leg/arm/head hit that costs the target >= 50% of max HP.
///   Piercing — reduces Protection by 15% BEFORE the calc (stacks with the
///              ArmorPiercing ability's 50% for 65% total).
///   Blunt    — +25% damage on HEAD hits BEFORE protection is deducted.
/// </summary>
public enum WeaponDamageType : byte
{
    Slashing,
    Piercing,
    Blunt,
}

/// <summary>
/// Infers a weapon's Dominions damage type and two-handedness from its display
/// name. Per design: existing weapons carry no explicit type field, so we map
/// from the name (sword → slashing, mace → blunt, spear → piercing, etc.).
/// Default for an unrecognized name is Slashing (the most common melee type).
///
/// Two-handed detection is intentionally CONSERVATIVE — only names that are
/// unambiguously two-handed get the +125%-Strength bonus, to avoid silently
/// buffing one-handed weapons. Add a name to the keyword sets to reclassify.
/// </summary>
public static class WeaponClassifier
{
    // Order of checks: blunt, then piercing, then slashing. A name matching none
    // falls through to Slashing.
    private static readonly string[] BluntKeywords =
    {
        "club", "mace", "hammer", "maul", "staff", "kick", "fist", "punch",
        "unarmed", "smash", "bash", "flail", "trample", "stomp", "cudgel",
        "quarterstaff",
    };

    private static readonly string[] PiercingKeywords =
    {
        "spear", "pike", "lance", "dagger", "bite", "tusk", "fang", "sting",
        "javelin", "arrow", "bolt", "beak", "horn", "antler", "gore", "trident",
        "needle", "pick", "stiletto", "rapier",
    };

    private static readonly string[] SlashingKeywords =
    {
        "sword", "axe", "blade", "claw", "slash", "scythe", "glaive", "halberd",
        "saber", "sabre", "cleaver", "hook", "talon", "scimitar", "katana",
        "falchion", "sickle", "sweep", "rend",
    };

    // Unambiguously two-handed weapon names only.
    private static readonly string[] TwoHandedKeywords =
    {
        "greatsword", "great sword", "greataxe", "great axe", "greatclub",
        "maul", "halberd", "pike", "glaive", "zweihander", "two-hand",
        "twohand", "longspear", "warhammer", "war hammer", "polearm",
    };

    public static WeaponDamageType Classify(string? name)
    {
        if (string.IsNullOrEmpty(name)) return WeaponDamageType.Slashing;
        string n = name.ToLowerInvariant();

        if (ContainsAny(n, BluntKeywords)) return WeaponDamageType.Blunt;
        // Slashing is checked before piercing because some slashing names
        // (halberd, glaive) would otherwise be miscaught; piercing keywords are
        // distinct enough that order after slashing is safe.
        if (ContainsAny(n, SlashingKeywords)) return WeaponDamageType.Slashing;
        if (ContainsAny(n, PiercingKeywords)) return WeaponDamageType.Piercing;
        return WeaponDamageType.Slashing;
    }

    public static bool IsTwoHanded(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        return ContainsAny(name.ToLowerInvariant(), TwoHandedKeywords);
    }

    private static bool ContainsAny(string haystack, string[] needles)
    {
        for (int i = 0; i < needles.Length; i++)
            if (haystack.Contains(needles[i])) return true;
        return false;
    }
}
