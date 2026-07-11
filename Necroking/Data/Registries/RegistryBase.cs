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

/// <summary>A def with a human-facing display name. All registry def types
/// implement this; it powers <see cref="RegistryBase{TDef}.NameOf"/> and
/// generic editor helpers (dropdown builders) that need compile-time access
/// to DisplayName.</summary>
public interface INamedDef : IHasId
{
    string DisplayName { get; set; }
}

public abstract class RegistryBase<TDef> where TDef : class, IHasId, new()
{
    protected readonly Dictionary<string, TDef> _defs = new();
    protected readonly List<string> _orderedIDs = new();

    protected abstract string RootKey { get; }

    public TDef? Get(string id) => _defs.GetValueOrDefault(id);

    /// <summary>Display name for an id, falling back to the id itself when the
    /// def is missing OR its DisplayName is empty (defs default DisplayName to
    /// "", so a bare <c>?.DisplayName ?? id</c> renders blank — the historical
    /// hand-rolled sites disagreed on this; this is the one canonical rule).</summary>
    public string NameOf(string id)
    {
        var d = Get(id);
        return d is INamedDef n && !string.IsNullOrEmpty(n.DisplayName) ? n.DisplayName : id;
    }

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

    /// <summary>
    /// Deep-clone a def via a JSON round-trip using this registry's own
    /// serializer options. Guarantees clone fidelity equals save/load fidelity:
    /// any field that survives disk survives Copy/Paste — unlike the old
    /// hand-maintained field-by-field clone functions, which silently dropped
    /// every field added after they were written. Returns null on failure.
    /// </summary>
    public TDef? CloneDef(TDef src, string newId)
    {
        try
        {
            var options = CreateJsonOptions();
            var json = JsonSerializer.Serialize(SerializeItem(src, options), options);
            using var doc = JsonDocument.Parse(json);
            var clone = DeserializeItem(doc.RootElement, options);
            if (clone == null) return null;
            clone.Id = newId;
            return clone;
        }
        catch (Exception ex)
        {
            DebugLog.Log("error", $"CloneDef({src.Id}) failed: {ex.Message}");
            return null;
        }
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
            NewLine = "\n", // LF, not CRLF — stable diffs across machines/collaborators.
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            // Write &, <, >, + literally instead of & etc. — avoids noisy
            // diffs against hand/Python-edited saves. Safe: never embedded in HTML.
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
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
            // Baseline: what a freshly-constructed def serializes to. Any top-level
            // property whose JSON equals it is omitted from the file — the C# field
            // initializers restore it on load — so only authored values are written.
            // Diffing serialized JSON (not CLR defaults) is deliberate: many
            // initializers are non-zero (Size=2, AggroRangeScale=1, ...), so
            // JsonIgnoreCondition.WhenWritingDefault would be wrong here.
            var baseline = JsonSerializer.SerializeToNode(SerializeItem(new TDef(), options), options)!.AsObject();
            var items = new List<object>();

            foreach (var id in _orderedIDs)
            {
                if (!_defs.TryGetValue(id, out var def)) continue;
                var node = JsonSerializer.SerializeToNode(SerializeItem(def, options), options)!.AsObject();
                PruneDefaults(node, baseline);
                items.Add(node);
            }

            var doc = new Dictionary<string, object> { [RootKey] = items };
            string json = JsonSerializer.Serialize(doc, options);
            // Atomic tmp+rename so a crash mid-write can't corrupt the registry
            // file; if-changed so per-frame editor auto-save loops (weather tab)
            // don't rewrite an unchanged file 60×/sec.
            return Core.JsonFile.WriteStringIfChanged(path, json);
        }
        catch (Exception ex) { DebugLog.Log("error", $"Failed to save {path}: {ex.Message}"); return false; }
    }

    /// <summary>Drop every top-level property whose serialized value equals the
    /// freshly-constructed default's ("id" always stays). Top-level ONLY, on
    /// purpose: nested objects are kept or dropped as whole subtrees, so a
    /// partially-authored nested object is written intact and member-level
    /// omission can never change what its setters observe during load (e.g.
    /// DirectionalFractions tracks explicitly-authored members via setter flags).</summary>
    private static void PruneDefaults(System.Text.Json.Nodes.JsonObject item,
        System.Text.Json.Nodes.JsonObject baseline)
    {
        List<string>? toRemove = null;
        foreach (var (key, value) in item)
        {
            if (key == "id") continue;
            if (baseline.TryGetPropertyValue(key, out var defVal)
                && System.Text.Json.Nodes.JsonNode.DeepEquals(value, defVal))
                (toRemove ??= new()).Add(key);
        }
        if (toRemove != null)
            foreach (var key in toRemove) item.Remove(key);
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
