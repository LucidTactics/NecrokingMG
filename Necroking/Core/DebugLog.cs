using System;
using System.IO;

namespace Necroking.Core;

public static class DebugLog
{
    private static bool _dirCreated;

    private static void EnsureDir()
    {
        if (_dirCreated) return;
        Directory.CreateDirectory("log");
        _dirCreated = true;
    }

    public static void Clear(string tag)
    {
        EnsureDir();
        File.WriteAllText($"log/{tag}.log", string.Empty);
    }

    public static void Log(string tag, string message)
    {
        EnsureDir();
        File.AppendAllText($"log/{tag}.log", message + "\n");
    }

    public static void Log(string tag, string fmt, params object[] args)
    {
        Log(tag, string.Format(fmt, args));
    }
}
