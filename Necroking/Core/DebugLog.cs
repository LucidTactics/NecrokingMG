using System;
using System.Collections.Generic;
using System.IO;

namespace Necroking.Core;

public static class DebugLog
{
    private static bool _dirCreated;

    // In-memory tail of each tag's log so in-game UI (LogPanel) can render the
    // recent lines without re-reading log/*.log from disk. Guarded by a lock:
    // logging isn't strictly main-thread-only.
    private const int MaxRecentLines = 2000;
    private static readonly Dictionary<string, List<string>> _recent = new();
    private static readonly Dictionary<string, int> _versions = new();

    private static void EnsureDir()
    {
        if (_dirCreated) return;
        Directory.CreateDirectory("log");
        _dirCreated = true;
    }

    /// <summary>Change counter for a tag's in-memory tail — bumps on every
    /// append/clear, so UI can skip rebuilding when nothing new was logged.</summary>
    public static int Version(string tag)
    {
        lock (_recent) return _versions.GetValueOrDefault(tag);
    }

    /// <summary>Append the tag's recent lines to <paramref name="into"/>
    /// (callers clear first when replacing). Copies under the lock so a
    /// concurrent Log can't mutate the list mid-enumeration.</summary>
    public static void CopyRecent(string tag, List<string> into)
    {
        lock (_recent)
            if (_recent.TryGetValue(tag, out var lines)) into.AddRange(lines);
    }

    private static void AddRecent(string tag, string message)
    {
        lock (_recent)
        {
            if (!_recent.TryGetValue(tag, out var lines)) _recent[tag] = lines = new();
            if (message.IndexOf('\n') < 0) lines.Add(message);
            else lines.AddRange(message.Split('\n'));
            if (lines.Count > MaxRecentLines) lines.RemoveRange(0, lines.Count - MaxRecentLines);
            _versions[tag] = _versions.GetValueOrDefault(tag) + 1;
        }
    }

    public static void Clear(string tag)
    {
        EnsureDir();
        File.WriteAllText($"log/{tag}.log", string.Empty);
        lock (_recent)
        {
            if (_recent.TryGetValue(tag, out var lines)) lines.Clear();
            _versions[tag] = _versions.GetValueOrDefault(tag) + 1;
        }
    }

    public static void Log(string tag, string message)
    {
        EnsureDir();
        File.AppendAllText($"log/{tag}.log", message + "\n");
        AddRecent(tag, message);
    }

    public static void Log(string tag, string fmt, params object[] args)
    {
        Log(tag, string.Format(fmt, args));
    }
}
