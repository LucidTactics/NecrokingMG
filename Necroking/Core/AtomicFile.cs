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

    /// <summary>
    /// Open a stream for an atomic write — for payloads too large to build as a
    /// string/byte[] first (e.g. the ~55 MB map JSON streamed through a
    /// Utf8JsonWriter). Writes go to a <c>.tmp</c> sibling; call
    /// <see cref="AtomicWriteStream.Commit"/> after the payload is complete, then
    /// dispose — dispose renames the temp over the target. Disposing WITHOUT
    /// Commit (e.g. unwinding on an exception mid-write) deletes the temp and
    /// leaves the original file untouched.
    /// </summary>
    public static AtomicWriteStream CreateStream(string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        return new AtomicWriteStream(path + ".tmp", path);
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

/// <summary>
/// FileStream over a <c>.tmp</c> sibling that atomically replaces the target on
/// dispose — but only after <see cref="Commit"/> has been called. See
/// <see cref="AtomicFile.CreateStream"/>.
/// </summary>
public sealed class AtomicWriteStream : FileStream
{
    private readonly string _tmpPath;
    private readonly string _targetPath;
    private bool _committed;
    private bool _finalized;

    internal AtomicWriteStream(string tmpPath, string targetPath)
        : base(tmpPath, FileMode.Create, FileAccess.Write)
    {
        _tmpPath = tmpPath;
        _targetPath = targetPath;
    }

    /// <summary>Mark the payload complete: dispose will rename the temp over the
    /// target. Call as the last step, after flushing any wrapping writer.</summary>
    public void Commit() => _committed = true;

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing); // closes the handle (flushing buffers) — must precede the rename
        if (!disposing || _finalized) return;
        _finalized = true;
        try
        {
            if (_committed)
                File.Move(_tmpPath, _targetPath, overwrite: true);
            else if (File.Exists(_tmpPath))
                File.Delete(_tmpPath);
        }
        catch (Exception ex)
        {
            DebugLog.Log("error", $"AtomicWriteStream finalize failed for '{_targetPath}': {ex.Message}");
        }
    }
}
