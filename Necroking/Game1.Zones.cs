using System;
using System.Collections.Generic;
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
    internal readonly ZoneSystem _zoneSystem = new();

    // Periodic zone-spawn state (see UpdateZoneSpawns). Keyed by "zoneId|defId" so it
    // survives list reordering in the editor; cleared per map load in ApplyZones.
    private readonly Dictionary<string, float> _zoneSpawnAccum = new();
    // Zone id → the squad ("herd") owning that zone's animals. The herd — not rect
    // containment — is what the spawn cap counts. A culled squad id (whole herd dead)
    // simply stops resolving; the next spawn founds a fresh herd.
    private readonly Dictionary<string, uint> _zoneSquadIds = new();
    private float _zoneSpawnTickTimer;
    private uint _zoneSpawnRng = 0x9E3779B9u;

    /// <summary>Apply every authored zone. Call once per map load, after placed units and
    /// legacy villages have spawned.</summary>
    private void ApplyZones()
    {
        _zoneSpawnAccum.Clear();
        _zoneSquadIds.Clear();
        _zoneSpawnTickTimer = 0f;
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
                case ZoneKind.AnimalPack:
                    if (ApplyAnimalZone(z, null)) squads++;
                    break;
            }
            FillZoneSpawnsAtStart(z, grid);
        }
        DebugLog.Log("startup", $"[zones] applied {_zoneSystem.Count} zones: {villages} villages, {squads} animal squads");
    }

    /// <summary>Map-load pre-fill: top each spawn entry up to half its cap (rounded up),
    /// counting whatever the map already provides (hand-placed animals adopted into the
    /// herd, placed foragables), so areas aren't empty at game start.</summary>
    private void FillZoneSpawnsAtStart(MapZone z, TileGrid? grid)
    {
        if (z.Kind == ZoneKind.Village || z.Spawns.Count == 0) return;
        bool forage = z.Kind == ZoneKind.Foraging;
        foreach (var e in z.Spawns)
        {
            if (string.IsNullOrEmpty(e.DefId) || e.MaxAlive <= 0) continue;
            int target = (e.MaxAlive + 1) / 2;
            for (int guard = 0; guard < target; guard++)
                if (!(forage ? TrySpawnZoneForagable(z, e, grid, target) : TrySpawnZoneUnit(z, e, grid, target)))
                    break;
        }
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

    /// <summary>Group the wild animals inside the zone into one pre-formed squad —
    /// units of <paramref name="archetype"/>, or any Animal-faction unit when null
    /// (the generic AnimalPack kind). Returns false when the zone is empty (no squad
    /// created — an empty squad would just be culled by SquadSystem.Recompute). The
    /// squad is recorded as the zone's herd and given the zone rect as its territory.</summary>
    private bool ApplyAnimalZone(MapZone z, byte? archetype)
    {
        AI.Squad? sq = null;
        for (int i = 0; i < _sim.Units.Count; i++)
        {
            var u = _sim.Units[i];
            if (!u.Alive || u.SquadId != 0) continue;
            if (archetype.HasValue ? u.Archetype != archetype.Value : u.Faction != Faction.Animal) continue;
            if (!z.ContainsPoint(u.Position)) continue;
            sq ??= _sim.Squads.CreateSquad(u.Faction, archetype ?? u.Archetype);
            sq.Members.Add(u.Id);
            _sim.UnitsMut[i].SquadId = sq.Id;
        }
        if (sq != null)
        {
            ApplyZoneTerritory(sq, z);
            _zoneSquadIds[z.Id] = sq.Id;
            DebugLog.Log("startup", $"[zones] '{z.Name}' ({z.Id}): squad {sq.Id} with {sq.Members.Count} members");
        }
        return sq != null;
    }

    /// <summary>Stamp the zone rect onto the squad as its roaming territory (see
    /// SubroutineSteps.IdleRoam). Re-applied on every zone spawn so an editor-moved
    /// zone re-anchors its herd.</summary>
    private static void ApplyZoneTerritory(AI.Squad sq, MapZone z)
    {
        sq.HasTerritory = true;
        sq.TerritoryCenter = new Vec2(z.X, z.Y);
        sq.TerritoryHalfW = z.HalfW;
        sq.TerritoryHalfH = z.HalfH;
    }

    /// <summary>Periodic zone spawning: each WolfPack/DeerHerd/Foraging zone with a
    /// <see cref="MapZone.Spawns"/> table refills its def up to MaxAlive at PerMinute
    /// spawns per minute. Ticked from the sim block at 1 Hz — spawn rates are
    /// per-minute, so sub-second resolution buys nothing.</summary>
    private void UpdateZoneSpawns(float dt)
    {
        if (_zoneSystem.Count == 0) return;
        _zoneSpawnTickTimer += dt;
        if (_zoneSpawnTickTimer < 1f) return;
        float step = _zoneSpawnTickTimer;
        _zoneSpawnTickTimer = 0f;

        var grid = _sim.Grid;
        foreach (var z in _zoneSystem.Zones)
        {
            if (z.Kind == ZoneKind.Village || z.Spawns.Count == 0) continue;
            bool forage = z.Kind == ZoneKind.Foraging;
            foreach (var e in z.Spawns)
            {
                if (string.IsNullOrEmpty(e.DefId) || e.PerMinute <= 0f || e.MaxAlive <= 0) continue;
                string key = z.Id + "|" + e.DefId;
                _zoneSpawnAccum.TryGetValue(key, out float acc);
                acc += e.PerMinute / 60f * step;
                while (acc >= 1f)
                {
                    bool spawned = forage ? TrySpawnZoneForagable(z, e, grid) : TrySpawnZoneUnit(z, e, grid);
                    if (!spawned)
                    {
                        // At cap (or no valid def/spot): hold exactly one pending spawn so
                        // a pickup/death is refilled promptly but never as a burst.
                        acc = 1f;
                        break;
                    }
                    acc -= 1f;
                }
                _zoneSpawnAccum[key] = acc;
            }
        }
    }

    /// <summary>Spawn one unit of the entry's def into the zone's herd, unless the herd
    /// already holds <paramref name="cap"/> (default MaxAlive) of that def. The herd —
    /// wherever it has roamed — is what the cap counts, not rect containment. New members
    /// spawn beside the pack; if the whole herd died (squad culled), the next spawn founds
    /// a fresh herd scattered in the rect.</summary>
    private bool TrySpawnZoneUnit(MapZone z, ZoneSpawnEntry e, TileGrid? grid, int cap = -1)
    {
        if (_gameData.Units.Get(e.DefId) == null) return false;
        if (cap < 0) cap = e.MaxAlive;

        AI.Squad? sq = null;
        if (_zoneSquadIds.TryGetValue(z.Id, out uint sqId) && !_sim.Squads.TryGet(sqId, out sq))
            sq = null; // culled — whole herd dead

        int alive = 0;
        if (sq != null)
            foreach (uint uid in sq.Members)
                if (_sim.UnitsMut.TryGetIndex(uid, out int mi)
                    && _sim.Units[mi].Alive && _sim.Units[mi].UnitDefID == e.DefId)
                    alive++;
        if (alive >= cap) return false;

        // Beside the pack when it exists; scattered in the rect when founding a herd.
        // (A freshly created squad's Centroid is zero until the next Recompute — the
        // AliveCount guard also covers that window.)
        Vec2 p = sq != null && sq.AliveCount > 0
            ? ScatterSpotNear(grid, sq.Centroid, 3f, ref _zoneSpawnRng)
            : ScatterSpotInRect(grid, z, ref _zoneSpawnRng);
        SpawnUnit(e.DefId, p);
        int idx = _sim.Units.Count - 1;

        if (sq == null)
        {
            sq = _sim.Squads.CreateSquad(_sim.Units[idx].Faction, _sim.Units[idx].Archetype);
            _zoneSquadIds[z.Id] = sq.Id;
        }
        // Explicit membership (bypasses MaxMembers, pre-empts lazy proximity clustering)
        // + refresh the territory in case the zone rect was edited since herd creation.
        sq.Members.Add(_sim.Units[idx].Id);
        _sim.UnitsMut[idx].SquadId = sq.Id;
        ApplyZoneTerritory(sq, z);

        DebugLog.Log("zones", $"[zones] '{z.Name}' ({z.Id}): spawned {e.DefId} into squad {sq.Id} ({alive + 1}/{cap})");
        return true;
    }

    /// <summary>Deterministic search for a walkable point within <paramref name="radius"/>
    /// of a pack's position (mirror of ScatterSpotInRect for a circle).</summary>
    private static Vec2 ScatterSpotNear(TileGrid? grid, Vec2 center, float radius, ref uint rng)
    {
        for (int a = 0; a < 24; a++)
        {
            rng = rng * 1664525u + 1013904223u;
            float fx = ((rng % 1000u) / 1000f - 0.5f) * 2f;
            rng = rng * 1664525u + 1013904223u;
            float fy = ((rng % 1000u) / 1000f - 0.5f) * 2f;
            Vec2 p = new Vec2(center.X + fx * radius, center.Y + fy * radius);
            if (grid == null || AI.SubroutineSteps.IsPointWalkable(grid, p, 0.5f)) return p;
        }
        return center;
    }

    // Scratch list for CountActiveOfDefInRect position collection (1 Hz tick, no alloc).
    private readonly List<Vec2> _zoneForagePosScratch = new();

    /// <summary>Spawn one foragable env object of the entry's def inside the zone,
    /// unless MaxAlive uncollected ones are already there. Prefers reviving a spent
    /// single-use instance over adding a fresh object. Fresh spots scatter randomly
    /// but keep away from the def's existing objects — full spacing first, then half,
    /// then any valid spot, so a crowded zone degrades instead of stalling.</summary>
    private bool TrySpawnZoneForagable(MapZone z, ZoneSpawnEntry e, TileGrid? grid, int cap = -1)
    {
        int defIdx = _envSystem.FindDef(e.DefId);
        if (defIdx < 0) return false;
        if (cap < 0) cap = e.MaxAlive;

        float minX = z.X - z.HalfW, maxX = z.X + z.HalfW;
        float minY = z.Y - z.HalfH, maxY = z.Y + z.HalfH;
        var taken = _zoneForagePosScratch;
        taken.Clear();
        int active = _envSystem.CountActiveOfDefInRect(defIdx, minX, minY, maxX, maxY, taken);
        if (active >= cap) return false;

        if (!_envSystem.TryReviveForagableInRect(defIdx, minX, minY, maxX, maxY))
        {
            // Spacing target: spread MaxAlive items over the rect area (half the
            // side of one item's share of the area).
            float spacing = 0.5f * MathF.Sqrt(4f * z.HalfW * z.HalfH / Math.Max(1, e.MaxAlive));
            if (!TryPlaceSpaced(z, defIdx, taken, spacing, grid)
                && !TryPlaceSpaced(z, defIdx, taken, spacing * 0.5f, grid)
                && !TryPlaceSpaced(z, defIdx, taken, 0f, grid))
                return false; // zone too crowded for a valid spot — retry next tick
        }
        DebugLog.Log("zones", $"[zones] '{z.Name}' ({z.Id}): spawned foragable {e.DefId} ({active + 1}/{cap})");
        return true;
    }

    /// <summary>One pass of scatter attempts requiring at least <paramref name="minDist"/>
    /// from every position in <paramref name="taken"/>. Places the object on success.</summary>
    private bool TryPlaceSpaced(MapZone z, int defIdx, List<Vec2> taken, float minDist, TileGrid? grid)
    {
        float distSq = minDist * minDist;
        for (int a = 0; a < 12; a++)
        {
            Vec2 p = ScatterSpotInRect(grid, z, ref _zoneSpawnRng);
            if (!_envSystem.CanPlaceObject(defIdx, p.X, p.Y)) continue;
            bool tooClose = false;
            for (int i = 0; i < taken.Count && !tooClose; i++)
            {
                float dx = taken[i].X - p.X, dy = taken[i].Y - p.Y;
                tooClose = dx * dx + dy * dy < distSq;
            }
            if (tooClose) continue;
            _envSystem.AddObject((ushort)defIdx, p.X, p.Y);
            return true;
        }
        return false;
    }
}
