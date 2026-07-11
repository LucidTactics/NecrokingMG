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
                    if (def != null) env.AddDef(def);
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

                    env.AddObject((ushort)defIdx, x, y, scale, seed, persistent: true);
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

    /// <summary>Serializer options for <see cref="EnvironmentObjectDef"/> — the ONE
    /// definition of the env_defs.json field set (camelCase property names,
    /// HdrColor as {r,g,b,a,intensity}, HarmonizeSettings via its canonical
    /// Read/WriteValue, category-dependent randomFlip default via the def's
    /// OnDeserialized hook). Replaces the old split hand-written pair
    /// (ParseEnvDef here / EnvironmentObjectDef.WriteJson in EnvironmentSystem.cs).</summary>
    internal static readonly JsonSerializerOptions EnvDefJson = new()
    {
        WriteIndented = true,
        NewLine = "\n", // LF, not CRLF — stable diffs across machines/collaborators.
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        // Write &, <, >, + literally instead of & etc. — avoids noisy diffs.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new Necroking.Core.HdrColorJsonConverter(), new Necroking.Editor.HarmonizeSettingsJsonConverter() },
    };

    /// <summary>Load env defs from a standalone JSON file (flat array format,
    /// wrapped {"envDefs":[...]} also accepted). This is the canonical source —
    /// loaded before the map. Verified against the old hand-written parser:
    /// identical defs for every entry of the 2026-07 env_defs.json.</summary>
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
                if (def != null && !string.IsNullOrEmpty(def.Id))
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

    /// <summary>Save env defs to a standalone JSON file (flat array format) —
    /// atomic + if-changed via <see cref="Core.JsonFile.WriteStringIfChanged"/>.</summary>
    public static bool SaveEnvDefs(string path, EnvironmentSystem env)
    {
        try
        {
            var list = new List<EnvironmentObjectDef>(env.DefCount);
            for (int i = 0; i < env.DefCount; i++)
            {
                var def = env.GetDef(i);
                // Match the historical writer: a harmonize block with no effect is
                // dropped (the old save omitted it; the next load produced null).
                if (def.Harmonize is { HasEffect: false }) def.Harmonize = null;
                if (def.HarmonizeCorrupt is { HasEffect: false }) def.HarmonizeCorrupt = null;
                list.Add(def);
            }
            string json = JsonSerializer.Serialize(list, EnvDefJson);
            return Core.JsonFile.WriteStringIfChanged(path, json);
        }
        catch (Exception ex)
        {
            DebugLog.Log("error", $"SaveEnvDefs failed for '{path}': {ex.Message}");
            return false;
        }
    }

    // Trigger / zone / road sidecar load+save moved to MapSidecars (one
    // reader+writer per file; the split pair here had drifted into round-trip
    // bugs — circle regions loading as rectangles, junctions never restored).

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

    /// <summary>One env def from a JSON element — via the attribute-based
    /// serializer (EnvDefJson). Null on deserialize failure.</summary>
    private static EnvironmentObjectDef? ParseEnvDef(JsonElement ed)
        => JsonSerializer.Deserialize<EnvironmentObjectDef>(ed.GetRawText(), EnvDefJson);
}
