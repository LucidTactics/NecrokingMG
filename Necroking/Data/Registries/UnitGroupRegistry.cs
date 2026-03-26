using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Necroking.Data.Registries;

public class UnitGroupEntry
{
    [JsonPropertyName("unitDefID")] public string UnitDefID { get; set; } = "";
    [JsonPropertyName("weight")] public float Weight { get; set; } = 1.0f;
}

public class UnitGroupDef : IHasId
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("entries")] public List<UnitGroupEntry> Entries { get; set; } = new();
}

public class UnitGroupRegistry : RegistryBase<UnitGroupDef>
{
    protected override string RootKey => "unit_groups";

    private static readonly Random _rng = new();

    public string? PickRandom(string groupID)
    {
        var def = Get(groupID);
        if (def == null || def.Entries.Count == 0) return null;

        float totalWeight = 0f;
        foreach (var e in def.Entries) totalWeight += e.Weight;

        float roll = (float)_rng.NextDouble() * totalWeight;
        float accum = 0f;
        foreach (var e in def.Entries)
        {
            accum += e.Weight;
            if (roll <= accum) return e.UnitDefID;
        }
        return def.Entries[^1].UnitDefID;
    }
}
