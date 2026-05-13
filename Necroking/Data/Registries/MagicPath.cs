using System;
using System.Collections.Generic;

namespace Necroking.Data.Registries;

/// <summary>
/// The 12 magic paths organised into three realms. Path values stored on units
/// (per-unit caster strength) and on spells (per-spell requirement, plus optional
/// secondary). Negative levels are treated as zero at runtime; the editor stores
/// the raw int so the user can scrub past zero. See <see cref="MagicPathHelpers"/>
/// for ordering, names, and the path → icon-asset mapping.
/// </summary>
public enum MagicPath : byte
{
    None = 0,
    // Elemental realm
    Metal, Shock, Fire, Water,
    // Outer realm
    Heavens, Earth, Chaos, Order,
    // Inner realm
    Spirit, Body, Nature, Death,
}

public enum MagicRealm : byte { Elemental, Outer, Inner }

public static class MagicPathHelpers
{
    /// <summary>Authored display order — matches the unit-editor strip layout
    /// (Elemental → Outer → Inner, left-to-right). Excludes None.</summary>
    public static readonly MagicPath[] AllInOrder =
    {
        MagicPath.Metal,   MagicPath.Shock,  MagicPath.Fire,   MagicPath.Water,
        MagicPath.Heavens, MagicPath.Earth,  MagicPath.Chaos,  MagicPath.Order,
        MagicPath.Spirit,  MagicPath.Body,   MagicPath.Nature, MagicPath.Death,
    };

    public static MagicRealm GetRealm(MagicPath p) => p switch
    {
        MagicPath.Metal or MagicPath.Shock or MagicPath.Fire or MagicPath.Water        => MagicRealm.Elemental,
        MagicPath.Heavens or MagicPath.Earth or MagicPath.Chaos or MagicPath.Order     => MagicRealm.Outer,
        MagicPath.Spirit or MagicPath.Body or MagicPath.Nature or MagicPath.Death      => MagicRealm.Inner,
        _ => MagicRealm.Elemental,
    };

    /// <summary>Lowercase string id used in JSON (e.g. "death"). Matches the file
    /// stems in assets/UI/Icons/MagicIcons except for the case folding.</summary>
    public static string ToJsonId(MagicPath p) => p switch
    {
        MagicPath.Metal => "metal",       MagicPath.Shock => "shock",
        MagicPath.Fire => "fire",         MagicPath.Water => "water",
        MagicPath.Heavens => "heavens",   MagicPath.Earth => "earth",
        MagicPath.Chaos => "chaos",       MagicPath.Order => "order",
        MagicPath.Spirit => "spirit",     MagicPath.Body => "body",
        MagicPath.Nature => "nature",     MagicPath.Death => "death",
        _ => "",
    };

    public static MagicPath FromJsonId(string? id) => string.IsNullOrEmpty(id) ? MagicPath.None : id.ToLowerInvariant() switch
    {
        "metal" => MagicPath.Metal,       "shock" => MagicPath.Shock,
        "fire" => MagicPath.Fire,         "water" => MagicPath.Water,
        "heavens" => MagicPath.Heavens,   "earth" => MagicPath.Earth,
        "chaos" => MagicPath.Chaos,       "order" => MagicPath.Order,
        "spirit" => MagicPath.Spirit,     "body" => MagicPath.Body,
        "nature" => MagicPath.Nature,     "death" => MagicPath.Death,
        _ => MagicPath.None,
    };

    /// <summary>Short single-letter tag used in the Tab stats line, e.g. "(D) 2".
    /// Lower-case where the first letter is ambiguous so the parenthesised form
    /// still reads unambiguously.</summary>
    public static string ShortTag(MagicPath p) => p switch
    {
        MagicPath.Metal => "M",   MagicPath.Shock => "S",
        MagicPath.Fire => "F",    MagicPath.Water => "W",
        MagicPath.Heavens => "H", MagicPath.Earth => "E",
        MagicPath.Chaos => "C",   MagicPath.Order => "O",
        MagicPath.Spirit => "Sp", MagicPath.Body => "B",
        MagicPath.Nature => "N",  MagicPath.Death => "D",
        _ => "?",
    };

    /// <summary>Resolve the project-relative icon path for the given pixel size.
    /// <paramref name="size"/> must be 24 or 48 to match the assets shipped in
    /// assets/UI/Icons/MagicIcons/. Returns "" for MagicPath.None.</summary>
    public static string IconPath(MagicPath p, int size)
    {
        if (p == MagicPath.None) return "";
        string stem = p switch
        {
            MagicPath.Metal => "Metal",     MagicPath.Shock => "Shock",
            MagicPath.Fire => "Fire",
            // The file ships as "water24.png" (lowercase) — match it exactly.
            MagicPath.Water => "water",
            MagicPath.Heavens => "Heavens", MagicPath.Earth => "Earth",
            MagicPath.Chaos => "Chaos",     MagicPath.Order => "Order",
            MagicPath.Spirit => "Spirit",   MagicPath.Body => "Body",
            MagicPath.Nature => "Nature",   MagicPath.Death => "Death",
            _ => "",
        };
        return $"assets/UI/Icons/MagicIcons/{stem}{size}.png";
    }

    /// <summary>Clamp a raw stored level to the gameplay-effective value: anything
    /// negative is treated as zero.</summary>
    public static int Effective(int rawLevel) => rawLevel < 0 ? 0 : rawLevel;
}

/// <summary>One half of a spell's path requirement — primary or secondary. A
/// spell may have no secondary (Path == None). Both must be met to cast; only
/// the primary's level is used for the cost-reduction formula.</summary>
public struct SpellPathReq
{
    public MagicPath Path;
    public int Level;
    public bool HasRequirement => Path != MagicPath.None && Level > 0;
}
