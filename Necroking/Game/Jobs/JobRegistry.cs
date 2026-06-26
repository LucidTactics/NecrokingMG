using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Necroking.Core;

namespace Necroking.Game.Jobs;

/// <summary>
/// Loads and indexes the job templates from data/jobs.json. Mirrors the
/// hand-rolled System.Text.Json parsing style used by MapData / the other
/// registries (no attribute coupling, tolerant of missing fields).
/// </summary>
public class JobRegistry
{
    private readonly List<JobDef> _defs = new();
    private readonly Dictionary<string, JobDef> _byId = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<JobDef> Defs => _defs;
    public JobDef? Get(string id) =>
        id != null && _byId.TryGetValue(id, out var d) ? d : null;

    public void Load(string relativePath = "data/jobs.json")
    {
        _defs.Clear();
        _byId.Clear();

        string path = GamePaths.Resolve(relativePath);
        if (!File.Exists(path))
        {
            DebugLog.Log("jobs", $"[JobRegistry] jobs file not found: {path}");
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var je in doc.RootElement.EnumerateArray())
                _defs.Add(ParseDef(je));
        }
        catch (Exception ex)
        {
            DebugLog.Log("jobs", $"[JobRegistry] failed to parse {path}: {ex.Message}");
        }

        foreach (var d in _defs)
            if (!string.IsNullOrEmpty(d.Id)) _byId[d.Id] = d;

        DebugLog.Log("jobs", $"[JobRegistry] loaded {_defs.Count} jobs");
    }

    private static JobDef ParseDef(JsonElement e)
    {
        var d = new JobDef();
        if (e.TryGetProperty("id", out var v)) d.Id = v.GetString() ?? "";
        if (e.TryGetProperty("displayName", out v)) d.DisplayName = v.GetString() ?? "";
        if (e.TryGetProperty("icon", out v)) d.Icon = v.GetString() ?? "";
        if (e.TryGetProperty("archetype", out v))
            d.Archetype = string.Equals(v.GetString(), "Process", StringComparison.OrdinalIgnoreCase)
                ? JobArchetype.Process : JobArchetype.Collect;
        if (e.TryGetProperty("buildingDefId", out v)) d.BuildingDefId = v.GetString() ?? "";
        if (e.TryGetProperty("workerSlotsPerBuilding", out v)) d.WorkerSlotsPerBuilding = v.GetInt32();
        if (e.TryGetProperty("requiredCapability", out v)) d.RequiredCapability = v.GetString() ?? "";
        if (e.TryGetProperty("collectKind", out v)) d.CollectKind = v.GetString() ?? "foragable";
        if (e.TryGetProperty("sourceForagableType", out v)) d.SourceForagableType = v.GetString() ?? "";
        if (e.TryGetProperty("storeResource", out v)) d.StoreResource = v.GetString() ?? "";
        if (e.TryGetProperty("outputChoice", out v)) d.OutputChoice = v.GetBoolean();
        if (e.TryGetProperty("processTime", out v)) d.ProcessTime = v.GetSingle();
        if (e.TryGetProperty("spawnsUnit", out v)) d.SpawnsUnit = v.GetBoolean();
        if (e.TryGetProperty("spawnUnitDefId", out v)) d.SpawnUnitDefId = v.GetString() ?? "skeleton";

        if (e.TryGetProperty("inputs", out var inputs) && inputs.ValueKind == JsonValueKind.Array)
            foreach (var ie in inputs.EnumerateArray())
                d.Inputs.Add(new JobResourceAmount
                {
                    Resource = ie.TryGetProperty("resource", out var r) ? r.GetString() ?? "" : "",
                    Amount = ie.TryGetProperty("amount", out var a) ? a.GetInt32() : 1,
                });

        if (e.TryGetProperty("outputs", out var outputs) && outputs.ValueKind == JsonValueKind.Array)
            foreach (var oe in outputs.EnumerateArray())
                d.Outputs.Add(new JobOutput
                {
                    Id = oe.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "",
                    DisplayName = oe.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : "",
                    Amount = oe.TryGetProperty("amount", out var am) ? am.GetInt32() : 1,
                });

        return d;
    }
}
