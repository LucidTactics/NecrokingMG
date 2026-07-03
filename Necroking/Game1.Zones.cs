using System;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Movement;
using Necroking.World;

namespace Necroking;

/// <summary>
/// Zone application: reads the zones authored in the map editor (loaded from
/// <c>assets/maps/&lt;map&gt;_zones.json</c> into <see cref="_zoneSystem"/>) and applies them
/// once after all units have spawned:
///   • <see cref="ZoneKind.Village"/> — creates a <see cref="Village"/>, adopts the human
///     units inside the rect (tags <see cref="Unit.VillageId"/>) and spawns the zone's
///     configured population inside it. Structure membership stays geometric (anything
///     inside the rect belongs; computed by ContainsPoint on demand).
///   • <see cref="ZoneKind.WolfPack"/> / <see cref="ZoneKind.DeerHerd"/> — groups the wild
///     animals of the matching archetype inside the rect into one pre-formed squad, instead
///     of the lazy proximity clustering SquadSystem would otherwise do.
///
/// Runs AFTER <see cref="LoadVillagePopulation"/>, so legacy villages-json villagers are
/// already tagged and can't be stolen by a zone (the VillageId &lt; 0 guard); zone village
/// ids simply append after the legacy ones.
/// </summary>
public partial class Game1
{
    private readonly ZoneSystem _zoneSystem = new();

    /// <summary>Apply every authored zone. Call once per map load, after placed units and
    /// legacy villages have spawned.</summary>
    private void ApplyZones()
    {
        if (_zoneSystem.Count == 0) return;
        var grid = _sim.Grid;
        uint rng = 0x2468ACEu;
        int villages = 0, squads = 0;

        foreach (var z in _zoneSystem.Zones)
        {
            switch (z.Kind)
            {
                case ZoneKind.Village:
                    ApplyVillageZone(z, grid, ref rng);
                    villages++;
                    break;
                case ZoneKind.WolfPack:
                    if (ApplyAnimalZone(z, AI.ArchetypeRegistry.WolfPack)) squads++;
                    break;
                case ZoneKind.DeerHerd:
                    if (ApplyAnimalZone(z, AI.ArchetypeRegistry.DeerHerd)) squads++;
                    break;
            }
        }
        DebugLog.Log("startup", $"[zones] applied {_zoneSystem.Count} zones: {villages} villages, {squads} animal squads");
    }

    /// <summary>Create a Village from a zone: adopt the human units already inside the rect,
    /// then spawn the configured extra population inside it.</summary>
    private void ApplyVillageZone(MapZone z, TileGrid? grid, ref uint rng)
    {
        // Village.Name is the FindByName key — use the unique zone id, the display
        // name lives on the zone itself.
        int vid = _villageSystem.Add(new Village
        {
            Name = z.Id,
            Center = new Vec2(z.X, z.Y),
            Radius = MathF.Max(z.HalfW, z.HalfH),
        });

        // Adopt pre-existing (map-placed) human units inside the rect. Legacy villages-json
        // villagers already carry a VillageId and are left alone.
        int adopted = 0;
        for (int i = 0; i < _sim.Units.Count; i++)
        {
            var u = _sim.Units[i];
            if (!u.Alive || u.Faction != Faction.Human || u.VillageId >= 0) continue;
            if (!z.ContainsPoint(u.Position)) continue;
            _sim.UnitsMut[i].VillageId = (short)vid;
            adopted++;
        }

        // Spawn the zone's configured population inside the rect (same def ids as the
        // legacy villages path).
        int spawned = 0;
        spawned += SpawnZoneGroup("peasant", z.Population.Peasant, vid, z, grid, ref rng);
        spawned += SpawnZoneGroup("hunter", z.Population.Hunter, vid, z, grid, ref rng);
        spawned += SpawnZoneGroup("militia", z.Population.Militia, vid, z, grid, ref rng);
        spawned += SpawnZoneGroup("watchdog", z.Population.Watchdog, vid, z, grid, ref rng);

        DebugLog.Log("startup", $"[zones] village '{z.Name}' ({z.Id}): adopted {adopted}, spawned {spawned}");
    }

    /// <summary>Mirror of the legacy SpawnGroup (Game1.Villages.cs) but scattering inside
    /// the zone rect instead of an annulus.</summary>
    private int SpawnZoneGroup(string defId, int count, int villageId, MapZone z,
        TileGrid? grid, ref uint rng)
    {
        for (int k = 0; k < count; k++)
        {
            Vec2 p = ScatterSpotInRect(grid, z, ref rng);
            SpawnUnit(defId, p);
            int idx = _sim.Units.Count - 1;
            _sim.UnitsMut[idx].VillageId = (short)villageId;
            _sim.UnitsMut[idx].SpawnPosition = p;
        }
        return count;
    }

    /// <summary>Deterministic search for a walkable point inside the zone rect (90% of the
    /// half-extents so spawns hug the interior). Falls back to the zone center.</summary>
    private static Vec2 ScatterSpotInRect(TileGrid? grid, MapZone z, ref uint rng)
    {
        for (int a = 0; a < 24; a++)
        {
            rng = rng * 1664525u + 1013904223u;
            float fx = ((rng % 1000u) / 1000f - 0.5f) * 2f;
            rng = rng * 1664525u + 1013904223u;
            float fy = ((rng % 1000u) / 1000f - 0.5f) * 2f;
            Vec2 p = new Vec2(z.X + fx * z.HalfW * 0.9f, z.Y + fy * z.HalfH * 0.9f);
            if (grid == null || AI.SubroutineSteps.IsPointWalkable(grid, p, 0.5f)) return p;
        }
        return new Vec2(z.X, z.Y);
    }

    /// <summary>Group the wild animals of <paramref name="archetype"/> inside the zone into
    /// one pre-formed squad. Returns false when the zone is empty (no squad created — an
    /// empty squad would just be culled by SquadSystem.Recompute).</summary>
    private bool ApplyAnimalZone(MapZone z, byte archetype)
    {
        AI.Squad? sq = null;
        for (int i = 0; i < _sim.Units.Count; i++)
        {
            var u = _sim.Units[i];
            if (!u.Alive || u.Archetype != archetype || u.SquadId != 0) continue;
            if (!z.ContainsPoint(u.Position)) continue;
            sq ??= _sim.Squads.CreateSquad(u.Faction, archetype);
            sq.Members.Add(u.Id);
            _sim.UnitsMut[i].SquadId = sq.Id;
        }
        if (sq != null)
            DebugLog.Log("startup", $"[zones] '{z.Name}' ({z.Id}): squad {sq.Id} with {sq.Members.Count} members");
        return sq != null;
    }
}
