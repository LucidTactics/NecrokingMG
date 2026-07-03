using System;
using System.Text.Json;
using System.IO;

namespace Necroking.Core;

/// <summary>
/// Static helper for loading and saving standalone JSON objects (POCOs)
/// that are not registry-backed (i.e., single-object holders like GameSettings,
/// CorpseSettings, WadingDefaultsFile). Routes through standard File I/O,
/// JSON deserialization, and AtomicFile write-safety.
/// </summary>
public static class JsonFile
{
    /// <summary>
    /// Load a JSON file and deserialize it to an object of type T.
    /// Returns true on success; false if file doesn't exist, deserialization fails, or an exception occurs.
    /// The deserialized object is returned via the <paramref name="value"/> out parameter.
    /// </summary>
    public static bool Load<T>(string path, JsonSerializerOptions? opts, out T? value) where T : class
    {
        value = null;
        if (!File.Exists(path)) return false;
        try
        {
            string json = File.ReadAllText(path);
            value = JsonSerializer.Deserialize<T>(json, opts);
            return value != null;
        }
        catch (Exception ex)
        {
            DebugLog.Log("error", $"Failed to load {path}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Save an object to a JSON file using atomic write (tmp + rename).
    /// Returns true on success; false on exception.
    /// </summary>
    public static bool Save<T>(string path, T value, JsonSerializerOptions? opts) where T : class
    {
        try
        {
            string json = JsonSerializer.Serialize(value, opts);
            return AtomicFile.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            DebugLog.Log("error", $"Failed to save {path}: {ex.Message}");
            return false;
        }
    }

    // Last JSON this process wrote (or verified on disk) per path — lets
    // SaveIfChanged skip disk writes when nothing actually changed.
    private static readonly System.Collections.Generic.Dictionary<string, string> _lastWritten = new();

    /// <summary>
    /// Like <see cref="Save{T}"/>, but only touches the disk when the
    /// serialized JSON differs from what was last written to (or first found
    /// at) <paramref name="path"/>. Use for editor auto-save loops that mark
    /// dirty liberally — prevents rewriting an unchanged file every frame.
    /// Returns true when the file is up to date (whether or not a write happened).
    /// </summary>
    public static bool SaveIfChanged<T>(string path, T value, JsonSerializerOptions? opts) where T : class
    {
        try
        {
            string json = JsonSerializer.Serialize(value, opts);
            return WriteStringIfChanged(path, json);
        }
        catch (Exception ex)
        {
            DebugLog.Log("error", $"Failed to save {path}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Atomic write that skips the disk when <paramref name="content"/> matches
    /// what was last written to (or first found at) <paramref name="path"/>.
    /// Returns true when the file is up to date.
    /// </summary>
    public static bool WriteStringIfChanged(string path, string content)
    {
        if (_lastWritten.TryGetValue(path, out var prev) && prev == content)
            return true;
        if (!_lastWritten.ContainsKey(path) && File.Exists(path))
        {
            // First save this session: seed from disk so an unchanged
            // model doesn't rewrite an identical file.
            try
            {
                if (File.ReadAllText(path) == content)
                {
                    _lastWritten[path] = content;
                    return true;
                }
            }
            catch { /* unreadable — fall through to write */ }
        }
        if (!AtomicFile.WriteAllText(path, content)) return false;
        _lastWritten[path] = content;
        return true;
    }
}
