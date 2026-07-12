using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Necroking.Core;
using Necroking.GameSystems;
using Necroking.Lib;
using Necroking.Movement;
using Necroking.World;

namespace Necroking;

/// <summary>
/// Village loading: reads <c>assets/maps/&lt;map&gt;_villages.json</c> and materialises each
/// village — its structures (reusing existing building env-defs), its buried corpses, its
/// people (peasants, hunters, militia, watchdogs), and the inter-village militia patrols.
///
/// Split into two phases to fit the map-load pipeline:
///   • <see cref="LoadVillageStructures"/> runs BEFORE collisions are baked, so village
///     buildings are stamped into the pathfinding grid in the same pass as the map.
///   • <see cref="LoadVillagePopulation"/> runs AFTER the simulation is initialised and
///     placed units have spawned, so unit spawning + patrol routes have everything ready.
///
/// Village membership is tagged onto <see cref="Unit.VillageId"/>; the runtime coordination
/// lives in <see cref="VillageSystem"/>.
/// </summary>
public partial class Game1
{
    private readonly VillageSystem _villageSystem = new();
    private VillageFileDto? _pendingVillages;

    // ── JSON DTOs ──
    private sealed class VillageFileDto
    {
        public List<VillageDto> Villages { get; set; } = new();
        public List<PatrolDto> Patrols { get; set; } = new();
    }
    private sealed class VillageDto
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public float X { get; set; }
        public float Y { get; set; }
        public float Radius { get; set; } = 25f;
        public List<string> Neighbors { get; set; } = new();
        public List<StructDto> Structures { get; set; } = new();
        public List<StructDto> Graves { get; set; } = new();
        public int Corpses { get; set; }
        public float CorpseCenterDx { get; set; }
        public float CorpseCenterDy { get; set; }
        public PopDto Population { get; set; } = new();
    }
    private sealed class StructDto
    {
        public string DefId { get; set; } = "";
        public float Dx { get; set; }
        public float Dy { get; set; }
        public float Scale { get; set; } = 1f;
    }
    private sealed class PopDto
    {
        public int Peasant { get; set; }
        public int Hunter { get; set; }
        public int Militia { get; set; }
        public int Watchdog { get; set; }
    }
    private sealed class PatrolDto
    {
        public string From { get; set; } = "";
        public string To { get; set; } = "";
        public string SoldierDef { get; set; } = "soldier";
        public int Count { get; set; } = 2;
    }

    /// <summary>Phase 1: parse the file, register villages, resolve neighbours, and place all
    /// structures + gravestones as environment objects. Must run before the collision bake.</summary>
    private void LoadVillageStructures(string mapName)
    {
        _villageSystem.Clear();
        _pendingVillages = null;

        string path = GamePaths.Resolve($"{GamePaths.MapsDir}/{mapName}_villages.json");
        if (!File.Exists(path)) return;

        VillageFileDto? file;
        try
        {
            file = JsonSerializer.Deserialize<VillageFileDto>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception e)
        {
            DebugLog.Log("startup", $"[villages] failed to parse {path}: {e.Message}");
            return;
        }
        if (file == null || file.Villages.Count == 0) return;
        _pendingVillages = file;

        // Create village runtime entries (index == spawn order == VillageId).
        foreach (var vd in file.Villages)
            _villageSystem.Add(new Village
            {
                Name = vd.Id,
                Center = new Vec2(vd.X, vd.Y),
                Radius = vd.Radius,
            });

        // Resolve neighbour links now that every village has an id.
        foreach (var vd in file.Villages)
        {
            var v = _villageSystem.Get(_villageSystem.FindByName(vd.Id));
            if (v == null) continue;
            foreach (var nb in vd.Neighbors)
            {
                int nid = _villageSystem.FindByName(nb);
                if (nid >= 0) v.Neighbors.Add(nid);
            }
        }

        // Stamp structures + graves.
        int placed = 0;
        foreach (var vd in file.Villages)
        {
            foreach (var s in vd.Structures) placed += PlaceStructure(s, vd);
            foreach (var s in vd.Graves) placed += PlaceStructure(s, vd);
        }
        DebugLog.Log("startup", $"[villages] {file.Villages.Count} villages, {placed} structures placed");
    }

    private int PlaceStructure(StructDto s, VillageDto vd)
    {
        int di = _envSystem.FindDef(s.DefId);
        if (di < 0)
        {
            DebugLog.Log("startup", $"[villages] unknown structure def '{s.DefId}' in {vd.Id}");
            return 0;
        }
        _envSystem.AddObject((ushort)di, vd.X + s.Dx, vd.Y + s.Dy, s.Scale <= 0 ? 1f : s.Scale);
        return 1;
    }

    /// <summary>Phase 2: spawn villagers, buried corpses, and inter-village patrols. Runs after
    /// the sim is initialised and the map's own units have spawned.</summary>
    private void LoadVillagePopulation(string mapName)
    {
        if (_pendingVillages == null) return;
        var grid = _sim.Grid;
        uint rng = 0x1234567u;
        int spawned = 0;

        for (int vi = 0; vi < _pendingVillages.Villages.Count; vi++)
        {
            var vd = _pendingVillages.Villages[vi];
            var center = new Vec2(vd.X, vd.Y);
            float inner = vd.Radius * 0.35f, outer = vd.Radius * 0.95f;

            spawned += SpawnGroup("peasant", vd.Population.Peasant, vi, center, inner, outer, grid, ref rng);
            spawned += SpawnGroup("hunter", vd.Population.Hunter, vi, center, inner, outer, grid, ref rng);
            spawned += SpawnGroup("militia", vd.Population.Militia, vi, center, inner, outer, grid, ref rng);
            spawned += SpawnGroup("watchdog", vd.Population.Watchdog, vi, center, inner, outer, grid, ref rng);

            // Buried dead — raiseable corpses clustered in the graveyard.
            var graveCenter = new Vec2(vd.X + vd.CorpseCenterDx, vd.Y + vd.CorpseCenterDy);
            for (int c = 0; c < vd.Corpses; c++)
            {
                Vec2 p = ScatterSpot(grid, graveCenter, 0.5f, 4f, ref rng);
                SpawnUnit("peasant", p);
                int idx = _sim.Units.Count - 1;
                _sim.SpawnCorpseFromUnit(idx);
            }
        }

        SpawnPatrols(_pendingVillages, grid, ref rng);
        DebugLog.Log("startup", $"[villages] spawned {spawned} villagers across {_pendingVillages.Villages.Count} villages");
        _pendingVillages = null;
    }

    /// <summary>The one villager-group spawn loop shared by the legacy villages
    /// path and the zone-village path (SpawnZoneGroup, Game1.Zones.cs): scatter a
    /// spot, spawn the unit, tag VillageId + SpawnPosition. The scatter region is
    /// the only variance — the <paramref name="zoneRect"/> when given, otherwise
    /// the (center, inner, outer) annulus.</summary>
    private int SpawnGroupCore(string defId, int count, int villageId, TileGrid? grid,
        ref uint rng, MapZone? zoneRect, Vec2 center, float inner, float outer)
    {
        for (int k = 0; k < count; k++)
        {
            Vec2 p = zoneRect != null
                ? ScatterSpotInRect(grid, zoneRect, ref rng)
                : ScatterSpot(grid, center, inner, outer, ref rng);
            SpawnUnit(defId, p);
            int idx = _sim.Units.Count - 1;
            _sim.UnitsMut[idx].VillageId = (short)villageId;
            _sim.UnitsMut[idx].SpawnPosition = p;
        }
        return count;
    }

    private int SpawnGroup(string defId, int count, int villageId, Vec2 center,
        float inner, float outer, TileGrid? grid, ref uint rng)
        => SpawnGroupCore(defId, count, villageId, grid, ref rng, null, center, inner, outer);

    private void SpawnPatrols(VillageFileDto file, TileGrid? grid, ref uint rng)
    {
        foreach (var pd in file.Patrols)
        {
            int a = _villageSystem.FindByName(pd.From);
            int b = _villageSystem.FindByName(pd.To);
            var va = _villageSystem.Get(a);
            var vb = _villageSystem.Get(b);
            if (va == null || vb == null) continue;

            var route = new PatrolRoute { Id = $"village_patrol_{pd.From}_{pd.To}", Loop = true };
            route.Waypoints.Add(va.Center);
            route.Waypoints.Add(vb.Center);
            _triggerSystem.PatrolRoutesMut.Add(route);
            int ri = _triggerSystem.PatrolRoutes.Count - 1;

            for (int k = 0; k < pd.Count; k++)
            {
                Vec2 p = ScatterSpot(grid, va.Center, 2f, 6f, ref rng);
                SpawnUnit(pd.SoldierDef, p);
                int idx = _sim.Units.Count - 1;
                _sim.UnitsMut[idx].Archetype = AI.ArchetypeRegistry.PatrolSoldier;
                _sim.UnitsMut[idx].PatrolRouteIdx = ri;
                _sim.UnitsMut[idx].PatrolWaypointIdx = 0;
                _sim.UnitsMut[idx].MoveTarget = route.Waypoints[0];
                _sim.UnitsMut[idx].Routine = 0;
                _sim.UnitsMut[idx].Subroutine = 0;
                _sim.UnitsMut[idx].SpawnPosition = p;
            }
        }
    }

    /// <summary>Deterministic search for a walkable point in an annulus around <paramref name="center"/>.
    /// Falls back to the centre if nothing walkable is found in a handful of tries.
    /// (Shared retry/walkability core: <see cref="ScatterSpots"/>.)</summary>
    private static Vec2 ScatterSpot(TileGrid? grid, Vec2 center, float minR, float maxR, ref uint rng)
        => ScatterSpots.InAnnulus(grid, center, minR, maxR, ref rng);
}
