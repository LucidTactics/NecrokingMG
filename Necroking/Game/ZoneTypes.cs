using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Necroking.Core;

namespace Necroking.GameSystems;

/// <summary>What a map zone does when the game loads. Extend here + <see cref="ZoneColors"/>
/// when adding a new zone kind.</summary>
public enum ZoneKind : byte
{
    Village = 0,   // humans + structures inside become a Village (VillageSystem)
    WolfPack = 1,  // wild wolves inside form one squad (SquadSystem)
    DeerHerd = 2,  // wild deer inside form one squad (SquadSystem)
    Foraging = 3,  // periodically spawns foragable env objects (Spawns list)
}

/// <summary>Village-zone spawn config: extra villagers spawned inside the zone at map load,
/// on top of any hand-placed units. Mirrors the legacy villages-json population block.</summary>
public class ZonePopulation
{
    public int Peasant { get; set; }
    public int Hunter { get; set; }
    public int Militia { get; set; }
    public int Watchdog { get; set; }
}

/// <summary>One row of a zone's periodic spawn table (see <see cref="MapZone.Spawns"/>):
/// keep up to <see cref="MaxAlive"/> of <see cref="DefId"/> alive inside the rect,
/// refilling at <see cref="PerMinute"/> spawns per minute. On animal zones DefId is a
/// unit def id (units.json); on Foraging zones it's a foragable env def id (env_defs.json).</summary>
public class ZoneSpawnEntry
{
    public string DefId { get; set; } = "";
    public float PerMinute { get; set; } = 1f;
    public int MaxAlive { get; set; } = 5;
}

/// <summary>
/// An authored rectangular map zone (center + half-extents, same convention as
/// <see cref="TriggerRegion"/>). Drawn and edited in the map editor's Zones tab,
/// persisted to <c>assets/maps/&lt;map&gt;_zones.json</c>, and applied once at game
/// load (village creation / squad grouping) by <c>Game1.ApplyZones</c>.
/// </summary>
public class MapZone
{
    public string Id { get; set; } = "";      // unique per map ("zone_N")
    public string Name { get; set; } = "";    // display name (village name for Village zones)
    public ZoneKind Kind { get; set; } = ZoneKind.Village;
    public float X { get; set; }
    public float Y { get; set; }
    public float HalfW { get; set; } = 10f;
    public float HalfH { get; set; } = 10f;
    /// <summary>Village zones only: villagers spawned inside the rect at load.</summary>
    public ZonePopulation Population { get; set; } = new();
    /// <summary>WolfPack/DeerHerd/Foraging zones: periodic runtime spawn table
    /// (applied by Game1.UpdateZoneSpawns, ignored on Village zones).</summary>
    public List<ZoneSpawnEntry> Spawns { get; set; } = new();

    public bool ContainsPoint(Vec2 p) =>
        p.X >= X - HalfW && p.X <= X + HalfW && p.Y >= Y - HalfH && p.Y <= Y + HalfH;
}

/// <summary>Owns the map's authored zones. Shared between the map editor (author/save)
/// and the game load path (apply) — same lifecycle as TriggerSystem regions: cleared and
/// reloaded per map.</summary>
public class ZoneSystem
{
    private List<MapZone> _zones = new();

    public IReadOnlyList<MapZone> Zones => _zones;
    public List<MapZone> ZonesMut => _zones;
    public int Count => _zones.Count;

    public void SetZones(List<MapZone> zones) => _zones = zones;
    public void Clear() => _zones.Clear();
    public int Add(MapZone z) { _zones.Add(z); return _zones.Count - 1; }
    public void Remove(int idx) { if (idx >= 0 && idx < _zones.Count) _zones.RemoveAt(idx); }
}

/// <summary>Single source of truth for zone kind → editor overlay colors.
/// Fill is transparent, border is the same hue darker and opaque; the selected
/// variants brighten both (border most) so the active zone clearly lights up.</summary>
public static class ZoneColors
{
    public static Color Base(ZoneKind k) => k switch
    {
        ZoneKind.Village => new Color(235, 185, 60),    // amber
        ZoneKind.WolfPack => new Color(205, 65, 65),    // crimson
        ZoneKind.DeerHerd => new Color(110, 205, 110),  // leaf green
        ZoneKind.Foraging => new Color(170, 115, 220),  // mushroom violet
        _ => new Color(255, 0, 255),                    // magenta = unknown kind
    };

    public static Color Fill(ZoneKind k, bool selected = false)
    {
        var b = Base(k);
        return Color.FromNonPremultiplied(b.R, b.G, b.B, selected ? 90 : 45);
    }

    public static Color Border(ZoneKind k, bool selected = false)
    {
        var b = Base(k);
        return selected
            ? new Color((b.R + 255) / 2, (b.G + 255) / 2, (b.B + 255) / 2, 255)
            : new Color((int)(b.R * 0.55f), (int)(b.G * 0.55f), (int)(b.B * 0.55f), 255);
    }
}
