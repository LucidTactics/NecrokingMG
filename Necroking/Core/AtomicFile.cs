using System;
using System.IO;

namespace Necroking.Core;

/// <summary>
/// Atomic file write helpers. Always go through these for any save that
/// could be interrupted (settings, registries, map cache, save games, etc.)
/// — direct File.WriteAllText / WriteAllBytes can leave a half-written file
/// if the process is killed mid-write, which is how settings.json gets
/// corrupted across a game crash.
///
/// Pattern: write the payload to a <c>.tmp</c> sibling, then atomically rename
/// it over the target. On Windows + .NET 7+ this calls ReplaceFile under the
/// hood; on POSIX it's a simple <c>rename(2)</c>. Both are atomic provided the
/// source and destination live on the same filesystem (always true here since
/// we use the same directory). The same pattern is already in
/// <see cref="Necroking.Render.AtlasCache"/> for the sprite cache.
/// </summary>
public static class AtomicFile
{
    /// <summary>Atomically write a UTF-8 text payload to <paramref name="path"/>.
    /// Returns true on success; on any IO failure logs to "error" channel and
    /// returns false (the original file, if any, is preserved untouched).</summary>
    public static bool WriteAllText(string path, string contents)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, contents);
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            DebugLog.Log("error", $"AtomicFile.WriteAllText failed for '{path}': {ex.Message}");
            return false;
        }
    }

    /// <summary>Atomically write a binary payload to <paramref name="path"/>.</summary>
    public static bool WriteAllBytes(string path, byte[] bytes)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            DebugLog.Log("error", $"AtomicFile.WriteAllBytes failed for '{path}': {ex.Message}");
            return false;
        }
    }
}
