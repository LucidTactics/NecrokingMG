using System;
using System.Collections.Generic;
using System.IO;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.World;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Regression test for the map sidecar round-trips (MapSidecars): save → load →
/// save must be lossless. Guards the two historical drift bugs (circle trigger
/// regions silently loading as rectangles; road junctions written but never
/// restored) plus the wider write-only fields (road texture/rim settings,
/// trigger conditions/effects).
///
/// Two passes:
///  1. Synthetic: builds systems exercising every persisted field (circle region,
///     condition tree, all effect types, junctions, rims, both zone flavors),
///     saves, reloads, asserts field-level equality, then saves again and
///     requires byte-identical output (fixpoint).
///  2. Real map: loads the current default map's sidecars (if present), re-saves,
///     reloads, and requires the same fixpoint. Logs counts for eyeballing.
/// Pure data test — no units, no rendering; completes on the first tick.
/// </summary>
public class SidecarRoundtripScenario : ScenarioBase
{
    public override string Name => "sidecar_roundtrip";

    private readonly List<string> _failures = new();
    private bool _done;

    public override void OnInit(Simulation sim)
    {
        string outDir = Path.Combine("log", "sidecar_roundtrip");
        Directory.CreateDirectory(outDir);

        SyntheticPass(outDir);
        RealMapPass(outDir, "default");

        _done = true;
    }

    private void Check(bool cond, string what)
    {
        if (cond)
            DebugLog.Log(ScenarioLog, $"  ok: {what}");
        else
        {
            _failures.Add(what);
            DebugLog.Log(ScenarioLog, $"  FAIL: {what}");
        }
    }

    // ------------------------------------------------------------------
    //  Pass 1: synthetic systems covering every persisted field
    // ------------------------------------------------------------------
    private void SyntheticPass(string outDir)
    {
        DebugLog.Log(ScenarioLog, "--- synthetic pass ---");

        // Triggers
        var trig = new TriggerSystem();
        trig.SetRegions(new List<TriggerRegion>
        {
            new() { Id = "r_rect", Name = "Rect", Shape = RegionShape.Rectangle, X = 10.5f, Y = 20.25f, HalfW = 3f, HalfH = 4f, Radius = 9f },
            new() { Id = "r_circ", Name = "Circle", Shape = RegionShape.Circle, X = -5.125f, Y = 7f, HalfW = 1f, HalfH = 2f, Radius = 6.5f },
        });
        trig.SetPatrolRoutes(new List<PatrolRoute>
        {
            new() { Id = "p1", Name = "Loop", Loop = true, Waypoints = { new Vec2(1.5f, 2.5f), new Vec2(3f, 4f) } },
            new() { Id = "p2", Name = "OneWay", Loop = false },
        });
        trig.SetTriggers(new List<TriggerDef>
        {
            new()
            {
                Id = "t1", Name = "Complex", ActiveByDefault = false, OneShot = true,
                MaxFireCount = 3, BoundObjectID = "obj_9",
                Condition = new CondAnd
                {
                    Children =
                    {
                        new CondEntersRegion { RegionID = "r_circ", MinCount = 2 },
                        new CondNot { Child = new CondGameTime { Time = 12.5f } },
                        new CondOr { Children = { new CondUnitsKilled { Count = 4, Cumulative = false }, new CondCooldown { Interval = 7.5f } } },
                    }
                },
                Effects =
                {
                    new EffSpawnUnits
                    {
                        UnitDefID = "wolf", Count = 5, Faction = Faction.Animal, RegionID = "r_rect",
                        Position = new Vec2(100.5f, 200.25f), SpawnAngle = 1.25f, SpawnDistance = 3.5f,
                        SpawnInterval = 0.75f, PostBehavior = PostSpawnBehavior.Patrol, PatrolRouteID = "p1",
                    },
                    new EffActivateTrigger { TriggerID = "t2" },
                    new EffDeactivateTrigger { TriggerID = "t1" },
                    new EffKillUnits { RegionID = "r_circ", MaxKills = 9 },
                }
            },
            new() { Id = "t2", Name = "Plain" },
        });
        trig.SetInstances(new List<TriggerInstance>
        {
            new() { InstanceID = "t1_i0", ParentTriggerID = "t1", BoundObjectID = "house_3", ActiveByDefault = true, AutoCreated = true },
            new() { InstanceID = "t2_i0", ParentTriggerID = "t2", ActiveByDefault = false },
        });

        // Roads
        var roads = new RoadSystem();
        roads.SetRoads(new List<RoadInstance>
        {
            new()
            {
                Id = "road_0", Name = "Main", TextureDefIndex = 2, RenderOrder = 1, Closed = true,
                EdgeSoftness = 0.12f, TextureScale = 1.5f, RimTextureDefIndex = 3, RimWidth = 0.75f,
                RimTextureScale = 2f, RimEdgeSoftness = 0.2f,
                Points = { new RoadControlPoint { Position = new Vec2(1f, 2f), Width = 2.5f }, new RoadControlPoint { Position = new Vec2(3.5f, 4.5f), Width = 3f } },
            },
        });
        roads.SetJunctions(new List<RoadJunction>
        {
            new() { Id = "junction_0", Name = "Cross", Position = new Vec2(9.5f, 8.25f), Radius = 4.5f, TextureDefIndex = 1, TextureScale = 1.25f, EdgeSoftness = 0.3f },
        });

        // Zones
        var zones = new ZoneSystem();
        zones.SetZones(new List<MapZone>
        {
            new()
            {
                Id = "zone_0", Name = "Riverwood", Kind = ZoneKind.Village, X = 50f, Y = 60f, HalfW = 25f, HalfH = 30f,
                Population = new ZonePopulation { Peasant = 6, Hunter = 2, Militia = 3, Watchdog = 1 },
            },
            new()
            {
                Id = "zone_1", Name = "Shrooms", Kind = ZoneKind.Foraging, X = -10f, Y = -20f, HalfW = 15f, HalfH = 15f,
                Spawns = { new ZoneSpawnEntry { DefId = "deathcap", PerMinute = 0.5f, MaxAlive = 8 } },
            },
        });

        // Save → load → verify → save again → fixpoint
        string t1 = Path.Combine(outDir, "syn_triggers_1.json"), t2 = Path.Combine(outDir, "syn_triggers_2.json");
        string r1 = Path.Combine(outDir, "syn_roads_1.json"), r2 = Path.Combine(outDir, "syn_roads_2.json");
        string z1 = Path.Combine(outDir, "syn_zones_1.json"), z2 = Path.Combine(outDir, "syn_zones_2.json");

        Check(MapSidecars.SaveTriggers(t1, trig), "synthetic triggers saved");
        Check(MapSidecars.SaveRoads(r1, roads), "synthetic roads saved");
        Check(MapSidecars.SaveZones(z1, zones), "synthetic zones saved");

        var trigB = new TriggerSystem();
        var roadsB = new RoadSystem();
        var zonesB = new ZoneSystem();
        Check(MapSidecars.LoadTriggers(t1, trigB), "synthetic triggers reloaded");
        Check(MapSidecars.LoadRoads(r1, roadsB), "synthetic roads reloaded");
        Check(MapSidecars.LoadZones(z1, zonesB), "synthetic zones reloaded");

        // The two historical bugs, explicitly:
        Check(trigB.Regions.Count == 2 && trigB.Regions[1].Shape == RegionShape.Circle,
            "circle region stays a circle across save/load");
        Check(roadsB.JunctionCount == 1, "junction survives save/load");

        if (roadsB.JunctionCount == 1)
        {
            var j = roadsB.GetJunction(0);
            Check(j.Id == "junction_0" && j.Name == "Cross" && j.Position.X == 9.5f && j.Position.Y == 8.25f
                && j.Radius == 4.5f && j.TextureDefIndex == 1 && j.TextureScale == 1.25f && j.EdgeSoftness == 0.3f,
                "junction fields intact");
        }
        if (roadsB.RoadCount == 1)
        {
            var rd = roadsB.GetRoad(0);
            Check(rd.TextureDefIndex == 2 && rd.RenderOrder == 1 && rd.RimTextureDefIndex == 3
                && rd.RimWidth == 0.75f && rd.RimTextureScale == 2f && rd.RimEdgeSoftness == 0.2f,
                "road texture/rim fields intact (previously write-only)");
            Check(rd.Points.Count == 2 && rd.Points[1].Position.X == 3.5f && rd.Points[1].Width == 3f,
                "road control points intact");
        }
        else Check(false, "road survives save/load");

        // Conditions/effects (previously write-only) restored:
        var def = trigB.Triggers.Count == 2 ? trigB.Triggers[0] : null;
        Check(def != null && def.BoundObjectID == "obj_9" && def.OneShot && def.MaxFireCount == 3 && !def.ActiveByDefault,
            "trigger def scalar fields intact");
        var and = def?.Condition as CondAnd;
        Check(and != null && and.Children.Count == 3
            && and.Children[0] is CondEntersRegion { RegionID: "r_circ", MinCount: 2 }
            && and.Children[1] is CondNot { Child: CondGameTime { Time: 12.5f } }
            && and.Children[2] is CondOr { Children.Count: 2 },
            "condition tree restored (previously write-only)");
        Check(def != null && def.Effects.Count == 4
            && def.Effects[0] is EffSpawnUnits
            {
                UnitDefID: "wolf", Count: 5, Faction: Faction.Animal, RegionID: "r_rect",
                SpawnAngle: 1.25f, SpawnDistance: 3.5f, SpawnInterval: 0.75f,
                PostBehavior: PostSpawnBehavior.Patrol, PatrolRouteID: "p1",
            }
            && def.Effects[1] is EffActivateTrigger { TriggerID: "t2" }
            && def.Effects[2] is EffDeactivateTrigger { TriggerID: "t1" }
            && def.Effects[3] is EffKillUnits { RegionID: "r_circ", MaxKills: 9 },
            "effects restored (previously write-only)");
        if (def is { Effects.Count: 4 } && def.Effects[0] is EffSpawnUnits sp)
            Check(sp.Position.X == 100.5f && sp.Position.Y == 200.25f, "spawn effect position intact");

        // Patrol routes / instances / zones:
        Check(trigB.PatrolRoutes.Count == 2 && trigB.PatrolRoutes[0].Waypoints.Count == 2
            && trigB.PatrolRoutes[0].Waypoints[1].X == 3f && !trigB.PatrolRoutes[1].Loop,
            "patrol routes intact");
        Check(trigB.Instances.Count == 2 && trigB.Instances[0].BoundObjectID == "house_3"
            && trigB.Instances[0].AutoCreated && !trigB.Instances[1].ActiveByDefault,
            "instances intact");
        Check(zonesB.Count == 2
            && zonesB.Zones[0].Kind == ZoneKind.Village && zonesB.Zones[0].Population.Peasant == 6
            && zonesB.Zones[0].Population.Watchdog == 1
            && zonesB.Zones[1].Kind == ZoneKind.Foraging && zonesB.Zones[1].Spawns.Count == 1
            && zonesB.Zones[1].Spawns[0].DefId == "deathcap" && zonesB.Zones[1].Spawns[0].PerMinute == 0.5f,
            "zones intact (population + spawns)");

        // Fixpoint: saving the reloaded systems must produce identical files.
        MapSidecars.SaveTriggers(t2, trigB);
        MapSidecars.SaveRoads(r2, roadsB);
        MapSidecars.SaveZones(z2, zonesB);
        Check(File.ReadAllText(t1) == File.ReadAllText(t2), "triggers save-load-save fixpoint");
        Check(File.ReadAllText(r1) == File.ReadAllText(r2), "roads save-load-save fixpoint");
        Check(File.ReadAllText(z1) == File.ReadAllText(z2), "zones save-load-save fixpoint");
    }

    // ------------------------------------------------------------------
    //  Pass 2: the real map's sidecars (skipped per-file when absent)
    // ------------------------------------------------------------------
    private void RealMapPass(string outDir, string mapName)
    {
        DebugLog.Log(ScenarioLog, $"--- real map pass ({mapName}) ---");

        RoundtripFile($"{mapName}_triggers",
            GamePaths.Resolve($"{GamePaths.MapsDir}/{mapName}_triggers.json"), outDir,
            (path, sysOut) => { var s = new TriggerSystem(); bool ok = MapSidecars.LoadTriggers(path, s); sysOut(s,
                $"{s.Regions.Count} regions, {s.PatrolRoutes.Count} routes, {s.Triggers.Count} defs, {s.Instances.Count} instances"); return ok; },
            (path, sys) => MapSidecars.SaveTriggers(path, (TriggerSystem)sys));

        RoundtripFile($"{mapName}_zones",
            GamePaths.Resolve($"{GamePaths.MapsDir}/{mapName}_zones.json"), outDir,
            (path, sysOut) => { var s = new ZoneSystem(); bool ok = MapSidecars.LoadZones(path, s); sysOut(s, $"{s.Count} zones"); return ok; },
            (path, sys) => MapSidecars.SaveZones(path, (ZoneSystem)sys));

        RoundtripFile($"{mapName}_roads",
            GamePaths.Resolve($"{GamePaths.MapsDir}/{mapName}_roads.json"), outDir,
            (path, sysOut) => { var s = new RoadSystem(); bool ok = MapSidecars.LoadRoads(path, s); sysOut(s, $"{s.RoadCount} roads, {s.JunctionCount} junctions"); return ok; },
            (path, sys) => MapSidecars.SaveRoads(path, (RoadSystem)sys));
    }

    /// <summary>Load the real sidecar, save a copy, reload the copy, save again;
    /// the two saved copies must be byte-identical (fixpoint). Never writes to
    /// the real file.</summary>
    private void RoundtripFile(string label, string realPath, string outDir,
        Func<string, Action<object, string>, bool> load, Func<string, object, bool> save)
    {
        if (!File.Exists(realPath))
        {
            DebugLog.Log(ScenarioLog, $"  {label}: no file on disk, skipped");
            return;
        }

        object? loaded = null;
        bool ok = load(realPath, (sys, counts) =>
        {
            loaded = sys;
            DebugLog.Log(ScenarioLog, $"  {label}: loaded real file — {counts}");
        });
        Check(ok && loaded != null, $"{label}: real file loads");
        if (!ok || loaded == null) return;

        string copy1 = Path.Combine(outDir, label + "_1.json");
        string copy2 = Path.Combine(outDir, label + "_2.json");
        Check(save(copy1, loaded), $"{label}: re-saved copy");

        object? reloaded = null;
        Check(load(copy1, (sys, counts) =>
        {
            reloaded = sys;
            DebugLog.Log(ScenarioLog, $"  {label}: reloaded copy — {counts}");
        }) && reloaded != null, $"{label}: copy reloads");
        if (reloaded == null) return;

        Check(save(copy2, reloaded), $"{label}: second save");
        Check(File.ReadAllText(copy1) == File.ReadAllText(copy2), $"{label}: save-load-save fixpoint");
    }

    public override void OnTick(Simulation sim, float dt) { }

    public override bool IsComplete => _done;

    public override int OnComplete(Simulation sim)
    {
        if (_failures.Count == 0)
        {
            DebugLog.Log(ScenarioLog, "All sidecar round-trip checks passed");
            return 0;
        }
        DebugLog.Log(ScenarioLog, $"{_failures.Count} sidecar round-trip check(s) FAILED:");
        foreach (var f in _failures)
            DebugLog.Log(ScenarioLog, $"  - {f}");
        return _failures.Count;
    }
}
