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
}
