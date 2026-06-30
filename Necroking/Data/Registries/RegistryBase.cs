using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Necroking.Core;

namespace Necroking.Data.Registries;

public interface IHasId
{
    string Id { get; set; }
}

public abstract class RegistryBase<TDef> where TDef : class, IHasId, new()
{
    protected readonly Dictionary<string, TDef> _defs = new();
    protected readonly List<string> _orderedIDs = new();

    protected abstract string RootKey { get; }

    public TDef? Get(string id) => _defs.GetValueOrDefault(id);
    public IReadOnlyList<string> GetIDs() => _orderedIDs;
    public int Count => _orderedIDs.Count;
    /// <summary>Iterate every loaded def in registration order. Used for post-load
    /// passes (e.g. wiring SpriteData refs after atlases load).</summary>
    public IEnumerable<TDef> All()
    {
        foreach (var id in _orderedIDs)
            if (_defs.TryGetValue(id, out var d)) yield return d;
    }

    public void Add(TDef def)
    {
        _defs[def.Id] = def;
        if (!_orderedIDs.Contains(def.Id))
            _orderedIDs.Add(def.Id);
    }

    /// <summary>Deserialize one entry from a JSON element — using this registry's
    /// own options and any per-registry <see cref="DeserializeItem"/> override, so a
    /// runtime add matches what <see cref="Load"/> would have produced — and add it
    /// (upsert by id). Returns the new def, or null on failure (with <paramref
    /// name="error"/> set). Used by the dev `add_data` command to inject a
    /// spell/unit/item/etc. into the live game without touching the JSON file.</summary>
    public TDef? AddFromJson(JsonElement elem, out string error)
    {
        error = "";
        try
        {
            var def = DeserializeItem(elem, CreateJsonOptions());
            if (def == null) { error = "deserialize returned null"; return null; }
            if (string.IsNullOrEmpty(def.Id)) { error = "entry has no \"id\""; return null; }
            Add(def);
            return def;
        }
        catch (Exception ex) { error = ex.Message; return null; }
    }

    public void AddAfter(TDef def, string afterId)
    {
        _defs[def.Id] = def;
        int idx = _orderedIDs.IndexOf(afterId);
        if (idx >= 0)
            _orderedIDs.Insert(idx + 1, def.Id);
        else
            _orderedIDs.Add(def.Id);
    }

    public void Remove(string id)
    {
        _defs.Remove(id);
        _orderedIDs.Remove(id);
    }

    /// <summary>
    /// Rename an item's ID. Returns false if newId already exists (and isn't the same item)
    /// or if oldId doesn't exist. Updates the internal dictionary and ordered list.
    /// </summary>
    public bool RenameId(string oldId, string newId)
    {
        if (string.IsNullOrEmpty(newId)) return false;
        if (oldId == newId) return true; // no-op
        if (_defs.ContainsKey(newId)) return false; // duplicate
        if (!_defs.TryGetValue(oldId, out var def)) return false; // not found

        _defs.Remove(oldId);
        def.Id = newId;
        _defs[newId] = def;

        int idx = _orderedIDs.IndexOf(oldId);
        if (idx >= 0) _orderedIDs[idx] = newId;

        return true;
    }

    protected virtual JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
        return options;
    }

    public virtual bool Load(string path)
    {
        if (!File.Exists(path)) return false;

        try
        {
            string json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty(RootKey, out var arr) || arr.ValueKind != JsonValueKind.Array)
                return false;

            _defs.Clear();
            _orderedIDs.Clear();

            var options = CreateJsonOptions();

            foreach (var elem in arr.EnumerateArray())
            {
                var def = DeserializeItem(elem, options);
                if (def == null || string.IsNullOrEmpty(def.Id)) continue;
                _defs[def.Id] = def;
                _orderedIDs.Add(def.Id);
            }
            return true;
        }
        catch (Exception ex) { DebugLog.Log("error", $"Failed to load {path}: {ex.Message}"); return false; }
    }

    public virtual bool Save(string path)
    {
        try
        {
            var options = CreateJsonOptions();
            var items = new List<object>();

            foreach (var id in _orderedIDs)
            {
                if (_defs.TryGetValue(id, out var def))
                    items.Add(SerializeItem(def, options));
            }

            var doc = new Dictionary<string, object> { [RootKey] = items };
            string json = JsonSerializer.Serialize(doc, options);
            // Atomic tmp+rename so a crash mid-write can't corrupt the registry file.
            return Core.AtomicFile.WriteAllText(path, json);
        }
        catch (Exception ex) { DebugLog.Log("error", $"Failed to save {path}: {ex.Message}"); return false; }
    }

    protected virtual TDef? DeserializeItem(JsonElement elem, JsonSerializerOptions options)
    {
        return JsonSerializer.Deserialize<TDef>(elem.GetRawText(), options);
    }

    protected virtual object SerializeItem(TDef def, JsonSerializerOptions options)
    {
        return def;
    }
}
