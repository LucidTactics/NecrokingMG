using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Necroking.Core;
using Necroking.GameSystems;
using Necroking.World;

namespace Necroking.Data;

/// <summary>
/// Reader + writer for the per-map sidecar files — <c>&lt;map&gt;_triggers.json</c>,
/// <c>&lt;map&gt;_zones.json</c> and <c>&lt;map&gt;_roads.json</c> — in ONE place.
///
/// History: the loaders used to live in <see cref="MapData"/> as hand-rolled
/// TryGetProperty walkers while the savers lived in MapEditorWindow as manual
/// Utf8JsonWriter code, and the two silently diverged (saved circle regions
/// loaded back as rectangles; road junctions, road texture/rim settings and
/// trigger conditions/effects were written but never read back). Both halves
/// are now the same attribute/converter-driven serialization of the domain
/// types, routed through <see cref="Core.JsonFile"/> (atomic, if-changed
/// writes), so a field added to the model round-trips by construction.
///
/// Field names/casing match the historical files exactly (camelCase, enums as
/// strings, waypoints as {x,y}, road points flattened to {x,y,width}, junctions
/// flattened to {id,name,x,y,...}), so pre-existing sidecars load unchanged.
/// </summary>
public static class MapSidecars
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var o = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            // Omits TriggerDef.Condition when null (matching the old saver);
            // all other properties are non-null and always written.
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            // Write &, <, >, + literally instead of & etc. — avoids noisy diffs.
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        // Tolerant ZoneKind converter must precede the generic enum converter so
        // an unknown kind (newer build's file) drops one zone, not the whole file.
        o.Converters.Add(new TolerantZoneKindConverter());
        o.Converters.Add(new JsonStringEnumConverter());
        o.Converters.Add(new Vec2Converter());
        o.Converters.Add(new RoadControlPointConverter());
        o.Converters.Add(new RoadJunctionConverter());
        o.Converters.Add(new ConditionNodeConverter());
        o.Converters.Add(new TriggerEffectConverter());
        return o;
    }

    // ------------------------------------------------------------------
    //  File shapes (root objects). Domain types serialize directly.
    // ------------------------------------------------------------------

    private sealed class TriggersFile
    {
        public List<TriggerRegion> Regions { get; set; } = new();
        public List<PatrolRoute> PatrolRoutes { get; set; } = new();
        public List<TriggerDef> Triggers { get; set; } = new();
        public List<TriggerInstance> Instances { get; set; } = new();
    }

    private sealed class ZonesFile
    {
        public List<MapZone> Zones { get; set; } = new();
    }

    private sealed class RoadsFile
    {
        public List<RoadInstance> Roads { get; set; } = new();
        public List<RoadJunction> Junctions { get; set; } = new();
    }

    // ------------------------------------------------------------------
    //  Triggers
    // ------------------------------------------------------------------

    /// <summary>Load the triggers sidecar into <paramref name="triggers"/>.
    /// Missing file is a silent no-op returning false (sidecars are optional).</summary>
    public static bool LoadTriggers(string path, TriggerSystem triggers)
    {
        if (!JsonFile.Load<TriggersFile>(path, Options, out var f) || f == null) return false;
        // The effect converter returns null for unknown types; the serializer
        // stores that null in the list, so scrub before handing to the system.
        foreach (var def in f.Triggers)
            def.Effects.RemoveAll(eff => eff == null);
        triggers.SetRegions(f.Regions);
        triggers.SetPatrolRoutes(f.PatrolRoutes);
        triggers.SetTriggers(f.Triggers);
        triggers.SetInstances(f.Instances);
        DebugLog.Log("startup", $"  Triggers: {f.Regions.Count} regions, {f.PatrolRoutes.Count} routes, {f.Triggers.Count} defs, {f.Instances.Count} instances");
        return true;
    }

    public static bool SaveTriggers(string path, TriggerSystem triggers)
        => JsonFile.SaveIfChanged(path, new TriggersFile
        {
            Regions = new List<TriggerRegion>(triggers.Regions),
            PatrolRoutes = new List<PatrolRoute>(triggers.PatrolRoutes),
            Triggers = new List<TriggerDef>(triggers.Triggers),
            Instances = new List<TriggerInstance>(triggers.Instances),
        }, Options);

    // ------------------------------------------------------------------
    //  Zones
    // ------------------------------------------------------------------

    /// <summary>Load the zones sidecar into <paramref name="zones"/>. Missing file
    /// is a silent no-op returning false — callers clear stale zones themselves.</summary>
    public static bool LoadZones(string path, ZoneSystem zones)
    {
        if (!JsonFile.Load<ZonesFile>(path, Options, out var f) || f == null) return false;
        int dropped = f.Zones.RemoveAll(z => !Enum.IsDefined(typeof(ZoneKind), z.Kind));
        if (dropped > 0)
            DebugLog.Log("startup", $"  {dropped} zone(s) with unknown kind skipped");
        zones.SetZones(f.Zones);
        DebugLog.Log("startup", $"  Zones: {f.Zones.Count}");
        return true;
    }

    public static bool SaveZones(string path, ZoneSystem zones)
        => JsonFile.SaveIfChanged(path, new ZonesFile
        {
            Zones = new List<MapZone>(zones.Zones),
        }, Options);

    // ------------------------------------------------------------------
    //  Roads
    // ------------------------------------------------------------------

    /// <summary>Load the roads sidecar into <paramref name="roads"/>.
    /// Missing file is a silent no-op returning false.</summary>
    public static bool LoadRoads(string path, RoadSystem roads)
    {
        if (!JsonFile.Load<RoadsFile>(path, Options, out var f) || f == null) return false;
        roads.SetRoads(f.Roads);
        roads.SetJunctions(f.Junctions);
        DebugLog.Log("startup", $"  Roads: {roads.RoadCount} roads, {roads.JunctionCount} junctions");
        return true;
    }

    public static bool SaveRoads(string path, RoadSystem roads)
        => JsonFile.SaveIfChanged(path, new RoadsFile
        {
            Roads = new List<RoadInstance>(roads.Roads),
            Junctions = new List<RoadJunction>(roads.Junctions),
        }, Options);

    // ------------------------------------------------------------------
    //  Converters
    // ------------------------------------------------------------------

    /// <summary>Waypoints and other bare positions persist as <c>{x,y}</c>.</summary>
    private sealed class Vec2Converter : JsonConverter<Vec2>
    {
        public override Vec2 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            float x = 0f, y = 0f;
            if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Vec2 expects an object");
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                string prop = reader.GetString() ?? "";
                reader.Read();
                if (prop.Equals("x", StringComparison.OrdinalIgnoreCase)) x = reader.GetSingle();
                else if (prop.Equals("y", StringComparison.OrdinalIgnoreCase)) y = reader.GetSingle();
                else reader.Skip();
            }
            return new Vec2(x, y);
        }

        public override void Write(Utf8JsonWriter writer, Vec2 value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber("x", value.X);
            writer.WriteNumber("y", value.Y);
            writer.WriteEndObject();
        }
    }

    /// <summary>Road control points persist flat: <c>{x,y,width}</c> (Position is inlined).</summary>
    private sealed class RoadControlPointConverter : JsonConverter<RoadControlPoint>
    {
        public override RoadControlPoint Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            float x = 0f, y = 0f, width = 2f;
            if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("RoadControlPoint expects an object");
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                string prop = reader.GetString() ?? "";
                reader.Read();
                switch (prop)
                {
                    case "x": x = reader.GetSingle(); break;
                    case "y": y = reader.GetSingle(); break;
                    case "width": width = reader.GetSingle(); break;
                    default: reader.Skip(); break;
                }
            }
            return new RoadControlPoint { Position = new Vec2(x, y), Width = width };
        }

        public override void Write(Utf8JsonWriter writer, RoadControlPoint value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber("x", value.Position.X);
            writer.WriteNumber("y", value.Position.Y);
            writer.WriteNumber("width", value.Width);
            writer.WriteEndObject();
        }
    }

    /// <summary>Junctions persist flat: <c>{id,name,x,y,radius,textureDefIndex,textureScale,edgeSoftness}</c>.</summary>
    private sealed class RoadJunctionConverter : JsonConverter<RoadJunction>
    {
        public override RoadJunction Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var j = new RoadJunction();
            float x = 0f, y = 0f;
            if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("RoadJunction expects an object");
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                string prop = reader.GetString() ?? "";
                reader.Read();
                switch (prop)
                {
                    case "id": j.Id = reader.GetString() ?? ""; break;
                    case "name": j.Name = reader.GetString() ?? ""; break;
                    case "x": x = reader.GetSingle(); break;
                    case "y": y = reader.GetSingle(); break;
                    case "radius": j.Radius = reader.GetSingle(); break;
                    case "textureDefIndex": j.TextureDefIndex = reader.GetInt32(); break;
                    case "textureScale": j.TextureScale = reader.GetSingle(); break;
                    case "edgeSoftness": j.EdgeSoftness = reader.GetSingle(); break;
                    default: reader.Skip(); break;
                }
            }
            j.Position = new Vec2(x, y);
            return j;
        }

        public override void Write(Utf8JsonWriter writer, RoadJunction value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("id", value.Id);
            writer.WriteString("name", value.Name);
            writer.WriteNumber("x", value.Position.X);
            writer.WriteNumber("y", value.Position.Y);
            writer.WriteNumber("radius", value.Radius);
            writer.WriteNumber("textureDefIndex", value.TextureDefIndex);
            writer.WriteNumber("textureScale", value.TextureScale);
            writer.WriteNumber("edgeSoftness", value.EdgeSoftness);
            writer.WriteEndObject();
        }
    }

    /// <summary>ZoneKind that maps an unrecognized name to a sentinel instead of
    /// throwing, so one zone authored by a newer build doesn't fail the whole file
    /// (the old loader skipped unknown kinds; LoadZones filters the sentinel out).</summary>
    private sealed class TolerantZoneKindConverter : JsonConverter<ZoneKind>
    {
        public const ZoneKind Unknown = (ZoneKind)255;

        public override ZoneKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String
                && Enum.TryParse<ZoneKind>(reader.GetString(), ignoreCase: true, out var kind))
                return kind;
            return Unknown;
        }

        public override void Write(Utf8JsonWriter writer, ZoneKind value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToString());
    }

    /// <summary>Polymorphic trigger condition tree, discriminated by <c>"type"</c>
    /// (AND / OR / NOT / EntersRegion / UnitsKilled / GameTime / Cooldown).
    /// Unknown types are skipped with a log rather than failing the file.</summary>
    private sealed class ConditionNodeConverter : JsonConverter<ConditionNode>
    {
        public override ConditionNode? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            return Parse(doc.RootElement);
        }

        private static ConditionNode? Parse(JsonElement e)
        {
            string type = e.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            switch (type)
            {
                case "AND":
                {
                    var c = new CondAnd();
                    if (e.TryGetProperty("children", out var ch))
                        foreach (var el in ch.EnumerateArray())
                            if (Parse(el) is { } n) c.Children.Add(n);
                    return c;
                }
                case "OR":
                {
                    var c = new CondOr();
                    if (e.TryGetProperty("children", out var ch))
                        foreach (var el in ch.EnumerateArray())
                            if (Parse(el) is { } n) c.Children.Add(n);
                    return c;
                }
                case "NOT":
                {
                    var c = new CondNot();
                    if (e.TryGetProperty("child", out var ch)) c.Child = Parse(ch);
                    return c;
                }
                case "EntersRegion":
                    return new CondEntersRegion
                    {
                        RegionID = e.TryGetProperty("regionID", out var rid) ? rid.GetString() ?? "" : "",
                        MinCount = e.TryGetProperty("minCount", out var mc) ? mc.GetInt32() : 1,
                    };
                case "UnitsKilled":
                    return new CondUnitsKilled
                    {
                        Count = e.TryGetProperty("count", out var cnt) ? cnt.GetInt32() : 1,
                        Cumulative = !e.TryGetProperty("cumulative", out var cu) || cu.GetBoolean(),
                    };
                case "GameTime":
                    return new CondGameTime
                    {
                        Time = e.TryGetProperty("time", out var ti) ? ti.GetSingle() : 0f,
                    };
                case "Cooldown":
                    return new CondCooldown
                    {
                        Interval = e.TryGetProperty("interval", out var iv) ? iv.GetSingle() : 10f,
                    };
                default:
                    DebugLog.Log("startup", $"  Unknown trigger condition type '{type}' skipped");
                    return null;
            }
        }

        public override void Write(Utf8JsonWriter writer, ConditionNode value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            switch (value)
            {
                case CondAnd a:
                    writer.WriteString("type", "AND");
                    writer.WriteStartArray("children");
                    foreach (var child in a.Children) Write(writer, child, options);
                    writer.WriteEndArray();
                    break;
                case CondOr o:
                    writer.WriteString("type", "OR");
                    writer.WriteStartArray("children");
                    foreach (var child in o.Children) Write(writer, child, options);
                    writer.WriteEndArray();
                    break;
                case CondNot n:
                    writer.WriteString("type", "NOT");
                    if (n.Child != null)
                    {
                        writer.WritePropertyName("child");
                        Write(writer, n.Child, options);
                    }
                    break;
                case CondEntersRegion er:
                    writer.WriteString("type", "EntersRegion");
                    writer.WriteString("regionID", er.RegionID);
                    writer.WriteNumber("minCount", er.MinCount);
                    break;
                case CondUnitsKilled uk:
                    writer.WriteString("type", "UnitsKilled");
                    writer.WriteNumber("count", uk.Count);
                    writer.WriteBoolean("cumulative", uk.Cumulative);
                    break;
                case CondGameTime gt:
                    writer.WriteString("type", "GameTime");
                    writer.WriteNumber("time", gt.Time);
                    break;
                case CondCooldown cd:
                    writer.WriteString("type", "Cooldown");
                    writer.WriteNumber("interval", cd.Interval);
                    break;
            }
            writer.WriteEndObject();
        }
    }

    /// <summary>Polymorphic trigger effect, discriminated by <c>"type"</c>
    /// (ActivateTrigger / DeactivateTrigger / SpawnUnits / KillUnits).
    /// Unknown types deserialize to null with a log; LoadTriggers scrubs the
    /// nulls out of each def's effects list.</summary>
    private sealed class TriggerEffectConverter : JsonConverter<TriggerEffect>
    {
        public override TriggerEffect? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            var e = doc.RootElement;
            string type = e.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            switch (type)
            {
                case "ActivateTrigger":
                    return new EffActivateTrigger
                    {
                        TriggerID = e.TryGetProperty("triggerID", out var at) ? at.GetString() ?? "" : "",
                    };
                case "DeactivateTrigger":
                    return new EffDeactivateTrigger
                    {
                        TriggerID = e.TryGetProperty("triggerID", out var dt) ? dt.GetString() ?? "" : "",
                    };
                case "SpawnUnits":
                {
                    var eff = new EffSpawnUnits
                    {
                        UnitDefID = e.TryGetProperty("unitDefID", out var ud) ? ud.GetString() ?? "" : "",
                        Count = e.TryGetProperty("count", out var c) ? c.GetInt32() : 1,
                        RegionID = e.TryGetProperty("regionID", out var rid) ? rid.GetString() ?? "" : "",
                        Position = new Vec2(
                            e.TryGetProperty("posX", out var px) ? px.GetSingle() : 0f,
                            e.TryGetProperty("posY", out var py) ? py.GetSingle() : 0f),
                        SpawnAngle = e.TryGetProperty("spawnAngle", out var sa) ? sa.GetSingle() : 0f,
                        SpawnDistance = e.TryGetProperty("spawnDistance", out var sd) ? sd.GetSingle() : 2f,
                        SpawnInterval = e.TryGetProperty("spawnInterval", out var si) ? si.GetSingle() : 0f,
                        PatrolRouteID = e.TryGetProperty("patrolRouteID", out var pr) ? pr.GetString() ?? "" : "",
                    };
                    if (e.TryGetProperty("faction", out var fa)
                        && Enum.TryParse<Faction>(fa.GetString(), ignoreCase: true, out var faction))
                        eff.Faction = faction;
                    if (e.TryGetProperty("postBehavior", out var pb)
                        && Enum.TryParse<PostSpawnBehavior>(pb.GetString(), ignoreCase: true, out var post))
                        eff.PostBehavior = post;
                    return eff;
                }
                case "KillUnits":
                    return new EffKillUnits
                    {
                        RegionID = e.TryGetProperty("regionID", out var krid) ? krid.GetString() ?? "" : "",
                        MaxKills = e.TryGetProperty("maxKills", out var mk) ? mk.GetInt32() : 0,
                    };
                default:
                    DebugLog.Log("startup", $"  Unknown trigger effect type '{type}' skipped");
                    return null;
            }
        }

        public override void Write(Utf8JsonWriter writer, TriggerEffect value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            switch (value)
            {
                case EffActivateTrigger act:
                    writer.WriteString("type", "ActivateTrigger");
                    writer.WriteString("triggerID", act.TriggerID);
                    break;
                case EffDeactivateTrigger deact:
                    writer.WriteString("type", "DeactivateTrigger");
                    writer.WriteString("triggerID", deact.TriggerID);
                    break;
                case EffSpawnUnits spawn:
                    writer.WriteString("type", "SpawnUnits");
                    writer.WriteString("unitDefID", spawn.UnitDefID);
                    writer.WriteNumber("count", spawn.Count);
                    writer.WriteString("faction", spawn.Faction.ToString());
                    writer.WriteString("regionID", spawn.RegionID);
                    writer.WriteNumber("posX", spawn.Position.X);
                    writer.WriteNumber("posY", spawn.Position.Y);
                    writer.WriteNumber("spawnAngle", spawn.SpawnAngle);
                    writer.WriteNumber("spawnDistance", spawn.SpawnDistance);
                    writer.WriteNumber("spawnInterval", spawn.SpawnInterval);
                    writer.WriteString("postBehavior", spawn.PostBehavior.ToString());
                    writer.WriteString("patrolRouteID", spawn.PatrolRouteID);
                    break;
                case EffKillUnits kill:
                    writer.WriteString("type", "KillUnits");
                    writer.WriteString("regionID", kill.RegionID);
                    writer.WriteNumber("maxKills", kill.MaxKills);
                    break;
            }
            writer.WriteEndObject();
        }
    }
}
