using Microsoft.Xna.Framework;
using Necroking.Data;

namespace Necroking.Render;

/// <summary>
/// Canonical per-faction marker/label colors, shared so a unit reads the same
/// color everywhere it's drawn as a colored marker or label — the top-right
/// minimap (<see cref="UI.MinimapHUD"/>) and the map editor's placed-unit
/// labels. Seeded from the original minimap palette:
///   undead = grey, human = gold, animal = green, player (necromancer) = white.
/// This is the single source of truth: change a faction's color here, not at a
/// call site. Colors are opaque; multiply by an alpha at the call site if a
/// site wants a faded marker.
/// </summary>
public static class FactionColors
{
    public static readonly Color Undead = new(180, 180, 190); // grey
    public static readonly Color Human  = new(255, 215, 60);  // gold
    public static readonly Color Animal = new(90, 240, 70);   // green
    /// <summary>The necromancer / player marker.</summary>
    public static readonly Color Player = Color.White;

    /// <summary>Color for a runtime faction enum value.</summary>
    public static Color For(Faction f) => f switch
    {
        Faction.Human => Human,
        Faction.Animal => Animal,
        _ => Undead,
    };

    /// <summary>Color for a def / placed-unit faction string, mirroring
    /// <c>Simulation.ParseFaction</c> ("Human"/"Animal"/anything-else — including
    /// null or "" — → Undead). A placed unit's <c>Faction</c> is "" when it
    /// inherits the def default: resolve that to the def's faction string
    /// before calling if you want the real faction rather than Undead.</summary>
    public static Color For(string? faction) =>
        faction == "Human" ? Human
        : faction == "Animal" ? Animal
        : Undead;
}
