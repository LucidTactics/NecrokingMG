using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Necroking.Core;
using Necroking.World;
using Necroking.GameSystems;

namespace Necroking.Data;

/// <summary>
/// The map-file schema and its JSON load/save — pure serialization, no live world state.
/// Loading a map means MapData → the per-game systems (GroundSystem, EnvironmentSystem,
/// WallSystem, placed units); the reverse when the map editor saves.
/// </summary>
public static class MapData
{
    public static bool Load(string path, GroundSystem ground, EnvironmentSystem env, WallSystem walls,
        List<PlacedUnit>? placedUnits = null)
        => Load(path, ground, env, walls, placedUnits, out _);

    /// <summary>Same as <see cref="Load(string, GroundSystem, EnvironmentSystem, WallSystem, List{PlacedUnit}?)"/>
    /// but also returns the grass map info parsed from the same JsonDocument, so callers
    /// don't have to re-read and re-parse the 55 MB map JSON just to get grass data.</summary>
    public static bool Load(string path, GroundSystem ground, EnvironmentSystem env, WallSystem walls,
        List<PlacedUnit>? placedUnits, out GrassMapInfo? grassInfo)
    {
        grassInfo = null;
        if (!File.Exists(path)) return false;

        try
        {
            DebugLog.Log("startup", $"Loading map: {path}");
            string json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            grassInfo = LoadGrass(root);

            // --- Ground types ---
            if (root.TryGetProperty("groundTypes", out var gtArr))
            {
                foreach (var gt in gtArr.EnumerateArray())
                {
                    var def = new GroundTypeDef
                    {
                        Id = gt.GetProperty("id").GetString() ?? "",
                        Name = gt.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                        TexturePath = gt.TryGetProperty("texturePath", out var tp) ? tp.GetString() ?? "" : "",
                        CorruptedTypeId = gt.TryGetProperty("corruptedTypeId", out var ct) ? ct.GetString() ?? "" : ""
                    };
                    // Parse movementTerrain (enum name, case-insensitive). Missing = Open.
                    // Valid values: Open, Rough, ShallowWater, DeepWater, Wall.
                    if (gt.TryGetProperty("movementTerrain", out var mt)
                        && mt.ValueKind == JsonValueKind.String
                        && Enum.TryParse<TerrainType>(mt.GetString(), ignoreCase: true, out var parsed))
                    {
                        def.MovementTerrain = parsed;
                    }
                    // Optional per-type tint (multiplied over the sampled texture in the
                    // shader). {r,g,b[,a]} object or [r,g,b[,a]] array, 0..255.
                    if (gt.TryGetProperty("tintColor", out var tcEl))
                    {
                        byte tr = 255, tg = 255, tb = 255, ta = 255;
                        ParseTintInto(tcEl, ref tr, ref tg, ref tb, ref ta);
                        def.TintColor = new Microsoft.Xna.Framework.Color(tr, tg, tb, ta);
                    }
                    ground.AddGroundType(def);
                }
                DebugLog.Log("startup", $"  Ground types: {ground.TypeCount}");
            }

            // --- Ground vertex map ---
            if (root.TryGetProperty("groundMap", out var gm))
            {
                int w = gm.GetProperty("width").GetInt32();
                int h = gm.GetProperty("height").GetInt32();
                // Ground system uses vertex map which is (worldW+1) x (worldH+1)
                // So world size = w-1, h-1
                int worldW = w - 1;
                int worldH = h - 1;
                ground.Init(worldW, worldH);

                string b64 = gm.GetProperty("tilesBase64").GetString() ?? "";
                byte[] data = Convert.FromBase64String(b64);
                ground.SetVertexMap(data);
                DebugLog.Log("startup", $"  Ground map: {worldW}x{worldH} ({data.Length} bytes)");
            }

            // --- Environment defs ---
            // Skip embedded envDefs — they're now loaded from data/env_defs.json before the map.
            // Fall back to embedded defs only if env system has no defs yet (legacy maps).
            if (env.DefCount == 0 && root.TryGetProperty("envDefs", out var edArr))
            {
                foreach (var ed in edArr.EnumerateArray())
                {
                    var def = ParseEnvDef(ed);
                    env.AddDef(def);
                }
                DebugLog.Log("startup", $"  Env defs (embedded fallback): {env.DefCount}");
            }

            // --- Placed objects ---
            if (root.TryGetProperty("placedObjects", out var poArr))
            {
                int orphans = 0;
                foreach (var po in poArr.EnumerateArray())
                {
                    string defId = po.GetProperty("defId").GetString() ?? "";
                    int defIdx = env.FindDef(defId);
                    if (defIdx < 0) { orphans++; continue; }

                    float x = po.GetProperty("x").GetSingle();
                    float y = po.GetProperty("y").GetSingle();
                    float scale = po.TryGetProperty("scale", out var s) ? s.GetSingle() : 1f;
                    float seed = po.TryGetProperty("seed", out var sd) ? sd.GetSingle() : -1f;

                    env.AddObject((ushort)defIdx, x, y, scale, seed);
                }
                DebugLog.Log("startup", $"  Placed objects: {env.ObjectCount}" + (orphans > 0 ? $" ({orphans} orphans skipped)" : ""));
            }

            // --- Walls ---
            if (root.TryGetProperty("walls", out var wArr))
            {
                foreach (var wd in wArr.EnumerateArray())
                {
                    var wallDef = new WallVisualDef
                    {
                        Name = wd.TryGetProperty("name", out var wn) ? wn.GetString() ?? "" : "",
                        MaxHP = wd.TryGetProperty("maxHP", out var mhp) ? mhp.GetInt32() : 100,
                        Protection = wd.TryGetProperty("protection", out var wp) ? wp.GetInt32() : 0
                    };
                    walls.Defs.Add(wallDef);
                }
                DebugLog.Log("startup", $"  Wall defs: {walls.DefCount}");
            }

            // --- Placed units ---
            if (placedUnits != null && root.TryGetProperty("placedUnits", out var puArr))
            {
                foreach (var pu in puArr.EnumerateArray())
                {
                    placedUnits.Add(new PlacedUnit
                    {
                        UnitDefId = pu.TryGetProperty("unitDefId", out var uid) ? uid.GetString() ?? "" : "",
                        X = pu.TryGetProperty("x", out var px) ? px.GetSingle() : 0,
                        Y = pu.TryGetProperty("y", out var py) ? py.GetSingle() : 0,
                        Faction = pu.TryGetProperty("faction", out var pf) ? pf.GetString() ?? "" : "",
                        PatrolRouteId = pu.TryGetProperty("patrolRouteId", out var pr) ? pr.GetString() ?? "" : "",
                        IsCorpse = pu.TryGetProperty("isCorpse", out var ic) && ic.GetBoolean(),
                    });
                }
                DebugLog.Log("startup", $"  Placed units: {placedUnits.Count}");
            }

            return true;
        }
        catch (Exception ex)
        {
            DebugLog.Log("startup", $"Map load error: {ex.Message}");
            return false;
        }
    }

    /// <summary>Load env defs from a standalone JSON file (flat array format).
    /// This is the canonical source — loaded before the map.</summary>
    public static bool LoadEnvDefs(string path, EnvironmentSystem env)
    {
        if (!File.Exists(path)) return false;
        try
        {
            string json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Handle both formats: flat array [...] or wrapped {"envDefs": [...]}
            JsonElement defsArray;
            if (root.ValueKind == JsonValueKind.Array)
                defsArray = root;
            else if (root.TryGetProperty("envDefs", out var wrapped) && wrapped.ValueKind == JsonValueKind.Array)
                defsArray = wrapped;
            else
                return false;

            foreach (var ed in defsArray.EnumerateArray())
            {
                var def = ParseEnvDef(ed);
                if (!string.IsNullOrEmpty(def.Id))
                    env.AddDef(def);
            }
            DebugLog.Log("startup", $"  Env defs loaded: {env.DefCount} (from {path})");
            return true;
        }
        catch (Exception ex)
        {
            DebugLog.Log("startup", $"  env_defs load error: {ex.Message}");
            return false;
        }
    }

    /// <summary>Save env defs to a standalone JSON file (flat array format).</summary>
    public static bool SaveEnvDefs(string path, EnvironmentSystem env)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            using var stream = File.Create(path);
            using var writer = new System.Text.Json.Utf8JsonWriter(stream, new System.Text.Json.JsonWriterOptions { Indented = true });

            writer.WriteStartArray();
            for (int i = 0; i < env.DefCount; i++)
            {
                writer.WriteStartObject();
                env.GetDef(i).WriteJson(writer);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.Flush();
            return true;
        }
        catch { return false; }
    }

    public static bool LoadTriggers(string path, TriggerSystem triggers)
    {
        if (!File.Exists(path)) return false;

        try
        {
            string json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Regions
            var regions = new List<TriggerRegion>();
            if (root.TryGetProperty("regions", out var rArr))
            {
                foreach (var r in rArr.EnumerateArray())
                {
                    regions.Add(new TriggerRegion
                    {
                        Id = r.GetProperty("id").GetString() ?? "",
                        Name = r.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                        X = r.TryGetProperty("x", out var x) ? x.GetSingle() : 0f,
                        Y = r.TryGetProperty("y", out var y) ? y.GetSingle() : 0f,
                        HalfW = r.TryGetProperty("halfW", out var hw) ? hw.GetSingle() : 5f,
                        HalfH = r.TryGetProperty("halfH", out var hh) ? hh.GetSingle() : 5f,
                        Radius = r.TryGetProperty("radius", out var rad) ? rad.GetSingle() : 5f
                    });
                }
            }
            triggers.SetRegions(regions);

            // Patrol routes
            var routes = new List<PatrolRoute>();
            if (root.TryGetProperty("patrolRoutes", out var prArr))
            {
                foreach (var pr in prArr.EnumerateArray())
                {
                    var route = new PatrolRoute
                    {
                        Id = pr.GetProperty("id").GetString() ?? "",
                        Name = pr.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                        Loop = pr.TryGetProperty("loop", out var l) ? l.GetBoolean() : true
                    };
                    if (pr.TryGetProperty("waypoints", out var wpArr))
                    {
                        foreach (var wp in wpArr.EnumerateArray())
                        {
                            route.Waypoints.Add(new Vec2(
                                wp.GetProperty("x").GetSingle(),
                                wp.GetProperty("y").GetSingle()));
                        }
                    }
                    routes.Add(route);
                }
            }
            triggers.SetPatrolRoutes(routes);

            // Trigger defs
            var defs = new List<TriggerDef>();
            if (root.TryGetProperty("triggers", out var tArr))
            {
                foreach (var t in tArr.EnumerateArray())
                {
                    var def = new TriggerDef
                    {
                        Id = t.GetProperty("id").GetString() ?? "",
                        Name = t.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                        ActiveByDefault = t.TryGetProperty("activeByDefault", out var ab) ? ab.GetBoolean() : true,
                        OneShot = t.TryGetProperty("oneShot", out var os) ? os.GetBoolean() : false,
                        MaxFireCount = t.TryGetProperty("maxFireCount", out var mfc) ? mfc.GetInt32() : 0
                    };
                    defs.Add(def);
                }
            }
            triggers.SetTriggers(defs);

            // Instances
            var instances = new List<TriggerInstance>();
            if (root.TryGetProperty("instances", out var iArr))
            {
                foreach (var inst in iArr.EnumerateArray())
                {
                    instances.Add(new TriggerInstance
                    {
                        InstanceID = inst.GetProperty("instanceID").GetString() ?? "",
                        ParentTriggerID = inst.TryGetProperty("parentTriggerID", out var pt) ? pt.GetString() ?? "" : "",
                        BoundObjectID = inst.TryGetProperty("boundObjectID", out var bo) ? bo.GetString() ?? "" : "",
                        ActiveByDefault = inst.TryGetProperty("activeByDefault", out var ab) ? ab.GetBoolean() : true,
                        AutoCreated = inst.TryGetProperty("autoCreated", out var ac) ? ac.GetBoolean() : false
                    });
                }
            }
            triggers.SetInstances(instances);

            DebugLog.Log("startup", $"  Triggers: {regions.Count} regions, {routes.Count} routes, {defs.Count} defs, {instances.Count} instances");
            return true;
        }
        catch (Exception ex)
        {
            DebugLog.Log("startup", $"Trigger load error: {ex.Message}");
            return false;
        }
    }

    /// <summary>Load authored map zones from the <c>&lt;map&gt;_zones.json</c> sidecar.
    /// Missing file is a silent no-op (zones are optional, like triggers/roads).</summary>
    public static bool LoadZones(string path, ZoneSystem zones)
    {
        if (!File.Exists(path)) return false;

        try
        {
            string json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var list = new List<MapZone>();
            if (root.TryGetProperty("zones", out var zArr))
            {
                foreach (var z in zArr.EnumerateArray())
                {
                    string kindStr = z.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "";
                    if (!Enum.TryParse<ZoneKind>(kindStr, ignoreCase: true, out var kind))
                    {
                        DebugLog.Log("startup", $"  Zone with unknown kind '{kindStr}' skipped");
                        continue;
                    }

                    var zone = new MapZone
                    {
                        Id = z.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                        Name = z.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                        Kind = kind,
                        X = z.TryGetProperty("x", out var x) ? x.GetSingle() : 0f,
                        Y = z.TryGetProperty("y", out var y) ? y.GetSingle() : 0f,
                        HalfW = z.TryGetProperty("halfW", out var hw) ? hw.GetSingle() : 10f,
                        HalfH = z.TryGetProperty("halfH", out var hh) ? hh.GetSingle() : 10f,
                    };
                    if (z.TryGetProperty("population", out var pop))
                    {
                        zone.Population.Peasant = pop.TryGetProperty("peasant", out var pp) ? pp.GetInt32() : 0;
                        zone.Population.Hunter = pop.TryGetProperty("hunter", out var ph) ? ph.GetInt32() : 0;
                        zone.Population.Militia = pop.TryGetProperty("militia", out var pm) ? pm.GetInt32() : 0;
                        zone.Population.Watchdog = pop.TryGetProperty("watchdog", out var pw) ? pw.GetInt32() : 0;
                    }
                    if (z.TryGetProperty("spawns", out var spawns))
                    {
                        foreach (var s in spawns.EnumerateArray())
                        {
                            zone.Spawns.Add(new ZoneSpawnEntry
                            {
                                DefId = s.TryGetProperty("defId", out var sd) ? sd.GetString() ?? "" : "",
                                PerMinute = s.TryGetProperty("perMinute", out var sr) ? sr.GetSingle() : 1f,
                                MaxAlive = s.TryGetProperty("maxAlive", out var sm) ? sm.GetInt32() : 5,
                            });
                        }
                    }
                    list.Add(zone);
                }
            }
            zones.SetZones(list);

            DebugLog.Log("startup", $"  Zones: {list.Count}");
            return true;
        }
        catch (Exception ex)
        {
            DebugLog.Log("startup", $"Zone load error: {ex.Message}");
            return false;
        }
    }

    public static bool LoadRoads(string path, RoadSystem roads)
    {
        if (!File.Exists(path)) return false;

        try
        {
            string json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("roads", out var rArr))
            {
                var roadList = new List<RoadInstance>();
                foreach (var r in rArr.EnumerateArray())
                {
                    var road = new RoadInstance
                    {
                        Id = r.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                        Name = r.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                        Closed = r.TryGetProperty("closed", out var cl) ? cl.GetBoolean() : false,
                        EdgeSoftness = r.TryGetProperty("edgeSoftness", out var es) ? es.GetSingle() : 0.08f,
                        TextureScale = r.TryGetProperty("textureScale", out var ts) ? ts.GetSingle() : 1f
                    };
                    if (r.TryGetProperty("points", out var pArr))
                    {
                        foreach (var p in pArr.EnumerateArray())
                        {
                            road.Points.Add(new RoadControlPoint
                            {
                                Position = new Vec2(p.GetProperty("x").GetSingle(), p.GetProperty("y").GetSingle()),
                                Width = p.TryGetProperty("width", out var w) ? w.GetSingle() : 2f
                            });
                        }
                    }
                    roadList.Add(road);
                }
                roads.SetRoads(roadList);
            }

            if (root.TryGetProperty("junctions", out var jArr))
            {
                var junctions = new List<RoadJunction>();
                foreach (var j in jArr.EnumerateArray())
                {
                    junctions.Add(new RoadJunction
                    {
                        Id = j.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                        Name = j.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                        Position = new Vec2(
                            j.TryGetProperty("x", out var x) ? x.GetSingle() : 0f,
                            j.TryGetProperty("y", out var y) ? y.GetSingle() : 0f),
                        Radius = j.TryGetProperty("radius", out var r) ? r.GetSingle() : 3f,
                        TextureScale = j.TryGetProperty("textureScale", out var ts) ? ts.GetSingle() : 1f,
                        EdgeSoftness = j.TryGetProperty("edgeSoftness", out var es) ? es.GetSingle() : 0.15f
                    });
                }
            }

            DebugLog.Log("startup", $"  Roads: {roads.RoadCount} roads, {roads.JunctionCount} junctions");
            return true;
        }
        catch (Exception ex)
        {
            DebugLog.Log("startup", $"Road load error: {ex.Message}");
            return false;
        }
    }

    public struct GrassTypeInfo
    {
        public string Id, Name;
        /// <summary>
        /// Sprite paths (relative to project root, e.g. "assets/Environment/Grass/GreenGrass1.png").
        /// Renderer picks one per cell via hash; null/empty treated as "no sprites".
        /// </summary>
        public string[] SpritePaths;
        /// <summary>
        /// Per-type rendered-size multiplier (applied on top of the base cell-size
        /// footprint). Defaults to 1.0 if missing.
        /// </summary>
        public float Scale;
        /// <summary>
        /// Per-type fraction of painted cells that render a tuft (0.0-1.0). Defaults
        /// to 1.0 if missing (every painted cell draws).
        /// </summary>
        public float Density;
        /// <summary>Healthy tint (multiplicative). Defaults to White if missing.</summary>
        public byte DefR, DefG, DefB, DefA;
        /// <summary>Corrupted tint (multiplicative). Defaults to a muted purple-grey.</summary>
        public byte CorR, CorG, CorB, CorA;
    }

    public struct GrassMapInfo
    {
        public int Width, Height;
        public byte[] Cells;
        public GrassTypeInfo[] Types;
    }

    public static GrassMapInfo? LoadGrass(JsonElement root)
    {
        if (!root.TryGetProperty("grassMap", out var gm)) return null;

        int w = gm.GetProperty("width").GetInt32();
        int h = gm.GetProperty("height").GetInt32();
        string b64 = gm.GetProperty("cellsBase64").GetString() ?? "";
        byte[] cells = Convert.FromBase64String(b64);

        var types = new List<GrassTypeInfo>();
        if (root.TryGetProperty("grassTypes", out var gtArr))
        {
            foreach (var gt in gtArr.EnumerateArray())
            {
                var info = new GrassTypeInfo
                {
                    Id = gt.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                    Name = gt.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    DefR = 255, DefG = 255, DefB = 255, DefA = 255,
                    CorR = 80, CorG = 60, CorB = 70, CorA = 255,
                };
                // Legacy "baseColor"/"tipColor" keys (per-blade colours from the
                // dead blade renderer) are intentionally not parsed — they're
                // dropped on the next save. New maps use defaultTint/corruptedTint.
                if (gt.TryGetProperty("defaultTint", out var dt)) ParseTintInto(dt, ref info.DefR, ref info.DefG, ref info.DefB, ref info.DefA);
                if (gt.TryGetProperty("corruptedTint", out var ct)) ParseTintInto(ct, ref info.CorR, ref info.CorG, ref info.CorB, ref info.CorA);

                if (gt.TryGetProperty("spritePaths", out var sp) && sp.ValueKind == JsonValueKind.Array)
                {
                    var paths = new List<string>();
                    foreach (var p in sp.EnumerateArray())
                    {
                        var s = p.GetString();
                        if (!string.IsNullOrEmpty(s)) paths.Add(s);
                    }
                    info.SpritePaths = paths.ToArray();
                }
                else
                {
                    info.SpritePaths = System.Array.Empty<string>();
                }

                info.Scale = gt.TryGetProperty("scale", out var sc) ? sc.GetSingle() : 1.0f;
                info.Density = gt.TryGetProperty("density", out var ds) ? ds.GetSingle() : 1.0f;

                types.Add(info);
            }
        }

        return new GrassMapInfo { Width = w, Height = h, Cells = cells, Types = types.ToArray() };
    }

    /// <summary>Parse an LDR colour stored as either {r,g,b[,a]} object or [r,g,b[,a]] array.</summary>
    private static void ParseTintInto(JsonElement el, ref byte r, ref byte g, ref byte b, ref byte a)
    {
        if (el.ValueKind == JsonValueKind.Array)
        {
            var arr = el.EnumerateArray().ToArray();
            if (arr.Length > 0) r = (byte)arr[0].GetInt32();
            if (arr.Length > 1) g = (byte)arr[1].GetInt32();
            if (arr.Length > 2) b = (byte)arr[2].GetInt32();
            if (arr.Length > 3) a = (byte)arr[3].GetInt32();
        }
        else if (el.ValueKind == JsonValueKind.Object)
        {
            if (el.TryGetProperty("r", out var pr)) r = (byte)pr.GetInt32();
            if (el.TryGetProperty("g", out var pg)) g = (byte)pg.GetInt32();
            if (el.TryGetProperty("b", out var pb)) b = (byte)pb.GetInt32();
            if (el.TryGetProperty("a", out var pa)) a = (byte)pa.GetInt32();
        }
    }

    private static EnvironmentObjectDef ParseEnvDef(JsonElement ed)
    {
        var def = new EnvironmentObjectDef();
        if (ed.TryGetProperty("id", out var id)) def.Id = id.GetString() ?? "";
        if (ed.TryGetProperty("name", out var name)) def.Name = name.GetString() ?? "";
        if (ed.TryGetProperty("category", out var cat)) def.Category = cat.GetString() ?? "Misc";
        if (ed.TryGetProperty("texturePath", out var tp)) def.TexturePath = tp.GetString() ?? "";
        if (ed.TryGetProperty("heightMapPath", out var hmp)) def.HeightMapPath = hmp.GetString() ?? "";
        if (ed.TryGetProperty("spriteWorldHeight", out var swh)) def.SpriteWorldHeight = swh.GetSingle();
        if (ed.TryGetProperty("worldHeight", out var wh)) def.WorldHeight = wh.GetSingle();
        if (ed.TryGetProperty("pivotX", out var px)) def.PivotX = px.GetSingle();
        if (ed.TryGetProperty("pivotY", out var py)) def.PivotY = py.GetSingle();
        if (ed.TryGetProperty("collisionRadius", out var cr)) def.CollisionRadius = cr.GetSingle();
        if (ed.TryGetProperty("collisionOffsetX", out var cox)) def.CollisionOffsetX = cox.GetSingle();
        if (ed.TryGetProperty("collisionOffsetY", out var coy)) def.CollisionOffsetY = coy.GetSingle();
        if (ed.TryGetProperty("scale", out var sc)) def.Scale = sc.GetSingle();
        if (ed.TryGetProperty("placementScale", out var ps)) def.PlacementScale = ps.GetSingle();
        if (ed.TryGetProperty("group", out var grp)) def.Group = grp.GetString() ?? "";
        if (ed.TryGetProperty("groupWeight", out var gw)) def.GroupWeight = gw.GetSingle();
        if (ed.TryGetProperty("isBuilding", out var ib)) def.IsBuilding = ib.GetBoolean();
        if (ed.TryGetProperty("playerBuildable", out var pb)) def.PlayerBuildable = pb.GetBoolean();
        if (ed.TryGetProperty("buildingMaxHP", out var bmhp)) def.BuildingMaxHP = bmhp.GetInt32();
        if (ed.TryGetProperty("buildingProtection", out var bp)) def.BuildingProtection = bp.GetInt32();
        if (ed.TryGetProperty("buildingDefaultOwner", out var bdo)) def.BuildingDefaultOwner = bdo.GetInt32();
        if (ed.TryGetProperty("costWood", out var cw)) def.CostWood = cw.GetInt32();
        if (ed.TryGetProperty("costStone", out var cs)) def.CostStone = cs.GetInt32();
        if (ed.TryGetProperty("costGold", out var cg)) def.CostGold = cg.GetInt32();
        if (ed.TryGetProperty("cost1ItemId", out var c1id)) def.Cost1ItemId = c1id.GetString() ?? "";
        if (ed.TryGetProperty("cost1Amount", out var c1a)) def.Cost1Amount = c1a.GetInt32();
        if (ed.TryGetProperty("cost2ItemId", out var c2id)) def.Cost2ItemId = c2id.GetString() ?? "";
        if (ed.TryGetProperty("cost2Amount", out var c2a)) def.Cost2Amount = c2a.GetInt32();
        if (ed.TryGetProperty("placementRadius", out var pr)) def.PlacementRadius = pr.GetSingle();
        if (ed.TryGetProperty("shadowType", out var sht)) def.ShadowType = sht.GetInt32();
        if (ed.TryGetProperty("shadowOpacityScale", out var sos)) def.ShadowOpacityScale = sos.GetSingle();
        if (ed.TryGetProperty("shadowOuterWScale", out var sows)) def.ShadowOuterWScale = sows.GetSingle();
        if (ed.TryGetProperty("shadowOuterHScale", out var sohs)) def.ShadowOuterHScale = sohs.GetSingle();
        if (ed.TryGetProperty("shadowInnerWScale", out var siws)) def.ShadowInnerWScale = siws.GetSingle();
        if (ed.TryGetProperty("shadowInnerHScale", out var sihs)) def.ShadowInnerHScale = sihs.GetSingle();
        if (ed.TryGetProperty("trapSpellId", out var tsi)) def.TrapSpellId = tsi.GetString() ?? "";
        if (ed.TryGetProperty("trapUses", out var tu)) def.TrapUses = tu.GetInt32();
        if (ed.TryGetProperty("trapTriggeredSprite", out var tts)) def.TrapTriggeredSprite = tts.GetString() ?? "";
        if (ed.TryGetProperty("trapDeployedSprite", out var tds)) def.TrapDeployedSprite = tds.GetString() ?? "";
        if (ed.TryGetProperty("trapTriggeredDuration", out var ttd)) def.TrapTriggeredDuration = ttd.GetSingle();
        if (ed.TryGetProperty("trapDeployedDuration", out var tdd)) def.TrapDeployedDuration = tdd.GetSingle();
        if (ed.TryGetProperty("trapFadeDuration", out var tfd)) def.TrapFadeDuration = tfd.GetSingle();
        if (ed.TryGetProperty("isGlyphTrap", out var igt)) def.IsGlyphTrap = igt.GetBoolean();
        if (ed.TryGetProperty("glyphRadius", out var gr)) def.GlyphRadius = gr.GetSingle();
        if (ed.TryGetProperty("boundTriggerID", out var btid)) def.BoundTriggerID = btid.GetString() ?? "";
        if (ed.TryGetProperty("processTime", out var pt)) def.ProcessTime = pt.GetSingle();
        if (ed.TryGetProperty("maxInputQueue", out var miq)) def.MaxInputQueue = miq.GetInt32();
        if (ed.TryGetProperty("maxOutputQueue", out var moq)) def.MaxOutputQueue = moq.GetInt32();
        if (ed.TryGetProperty("autoSpawn", out var ats)) def.AutoSpawn = ats.GetBoolean();
        if (ed.TryGetProperty("spawnOffsetX", out var sox)) def.SpawnOffsetX = sox.GetSingle();
        if (ed.TryGetProperty("spawnOffsetY", out var soy)) def.SpawnOffsetY = soy.GetSingle();
        if (ed.TryGetProperty("corpseSlots", out var cps)) def.CorpseSlots = cps.GetInt32();
        if (ed.TryGetProperty("itemSlots", out var its)) def.ItemSlots = its.GetInt32();
        if (ed.TryGetProperty("essenceCost", out var ec)) def.EssenceCost = ec.GetInt32();
        // Worker job system
        if (ed.TryGetProperty("hostsJob", out var hj)) def.HostsJob = hj.GetString() ?? "";
        if (ed.TryGetProperty("storedResource", out var sr)) def.StoredResource = sr.GetString() ?? "";
        if (ed.TryGetProperty("storageCap", out var scap)) def.StorageCap = scap.GetInt32();
        if (ed.TryGetProperty("isWorkerHome", out var iwh)) def.IsWorkerHome = iwh.GetBoolean();
        if (ed.TryGetProperty("workerSlots", out var wsl)) def.WorkerSlots = wsl.GetInt32();
        if (ed.TryGetProperty("isAnimated", out var ianim)) def.IsAnimated = ianim.GetBoolean();
        if (ed.TryGetProperty("animFramesX", out var afx)) def.AnimFramesX = afx.GetInt32();
        if (ed.TryGetProperty("animFramesY", out var afy)) def.AnimFramesY = afy.GetInt32();
        if (ed.TryGetProperty("animFPS", out var afps)) def.AnimFPS = afps.GetSingle();
        if (ed.TryGetProperty("animNoise", out var anoise)) def.AnimNoise = anoise.GetSingle();
        if (ed.TryGetProperty("animWindSync", out var awind)) def.AnimWindSync = awind.GetSingle();
        if (ed.TryGetProperty("isForagable", out var ifor)) def.IsForagable = ifor.GetBoolean();
        if (ed.TryGetProperty("foragableType", out var ftyp)) def.ForagableType = ftyp.GetString() ?? "";
        if (ed.TryGetProperty("respawnTime", out var rst)) def.RespawnTime = rst.GetSingle();
        if (ed.TryGetProperty("scaleMin", out var smin)) def.ScaleMin = smin.GetSingle();
        if (ed.TryGetProperty("scaleMax", out var smax)) def.ScaleMax = smax.GetSingle();
        if (ed.TryGetProperty("isBerryBush", out var ibb)) def.IsBerryBush = ibb.GetBoolean();
        if (ed.TryGetProperty("noBerrySprite", out var nbs)) def.NoBerrySprite = nbs.GetString() ?? "";
        if (ed.TryGetProperty("poisonedSprite", out var pbs)) def.PoisonedSprite = pbs.GetString() ?? "";
        if (ed.TryGetProperty("berryRespawnTime", out var brt)) def.BerryRespawnTime = brt.GetSingle();
        if (ed.TryGetProperty("fogEmitRate", out var femit)) def.FogEmitRate = femit.GetSingle();
        if (ed.TryGetProperty("fogAbsorbRate", out var fabs)) def.FogAbsorbRate = fabs.GetSingle();
        if (ed.TryGetProperty("isCorruptable", out var icor)) def.IsCorruptable = icor.GetBoolean();
        if (ed.TryGetProperty("corruptedSprite", out var csp)) def.CorruptedSprite = csp.GetString() ?? "";
        // Processing slots
        if (ed.TryGetProperty("input1", out var in1))
        {
            if (in1.TryGetProperty("kind", out var k)) def.Input1.Kind = k.GetString() ?? "";
            if (in1.TryGetProperty("resourceID", out var r)) def.Input1.ResourceID = r.GetString() ?? "";
        }
        if (ed.TryGetProperty("input2", out var in2))
        {
            if (in2.TryGetProperty("kind", out var k)) def.Input2.Kind = k.GetString() ?? "";
            if (in2.TryGetProperty("resourceID", out var r)) def.Input2.ResourceID = r.GetString() ?? "";
        }
        if (ed.TryGetProperty("output", out var outp))
        {
            if (outp.TryGetProperty("kind", out var k)) def.Output.Kind = k.GetString() ?? "";
            if (outp.TryGetProperty("resourceID", out var r)) def.Output.ResourceID = r.GetString() ?? "";
        }
        // Tint color
        if (ed.TryGetProperty("tintColor", out var tc))
        {
            byte tr = 255, tg = 255, tb = 255, ta = 255; float ti = 1f;
            if (tc.TryGetProperty("r", out var tcr)) tr = (byte)tcr.GetInt32();
            if (tc.TryGetProperty("g", out var tcg)) tg = (byte)tcg.GetInt32();
            if (tc.TryGetProperty("b", out var tcb)) tb = (byte)tcb.GetInt32();
            if (tc.TryGetProperty("a", out var tca)) ta = (byte)tca.GetInt32();
            if (tc.TryGetProperty("intensity", out var tci)) ti = tci.GetSingle();
            def.TintColor = new Necroking.Core.HdrColor(tr, tg, tb, ta, ti);
        }
        // Harmonization recipes (null when absent → harmonize disabled)
        if (ed.TryGetProperty("harmonize", out var hm))
            def.Harmonize = Necroking.Editor.HarmonizeSettings.Read(hm);
        if (ed.TryGetProperty("harmonizeCorrupt", out var hmc))
            def.HarmonizeCorrupt = Necroking.Editor.HarmonizeSettings.Read(hmc);
        // Random horizontal flip — when the field is absent (older maps), default
        // by category so organic props get variety and buildings/walls don't.
        def.RandomFlip = ed.TryGetProperty("randomFlip", out var rf)
            ? rf.GetBoolean()
            : EnvironmentObjectDef.DefaultRandomFlipForCategory(def.Category);
        return def;
    }
}
