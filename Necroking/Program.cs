using System;
using System.IO;
using System.Reflection;
using Microsoft.Xna.Framework;

namespace Necroking;

public static class LaunchArgs
{
    public static string? Scenario;
    public static int Timeout = 30;
    public static int Speed = 1;
    public static bool Headless;
    public static bool NoVsync;
    /// <summary>Diagnostic: auto-click "Start Game" on the first menu frame and
    /// (headless) exit shortly after the world finishes loading. Used to capture
    /// startup.log timing for the Start-Game path without manual clicking.</summary>
    public static bool AutoStart;
    /// <summary>Offline bake: load the world headless, compute + persist every corpse
    /// death-frame centroid to cache/frame_centroids.json, then exit. Run once after
    /// changing unit art; the file ships with the build so carries never stall on a
    /// GetData read-back. Implies --autostart --headless.</summary>
    public static bool BakeCentroids;
    public static Color? BgColor;
    public static int ResolutionW;
    public static int ResolutionH;
    /// <summary>When &gt; 0, start the lean dev control server (Necroking/Dev/DevServer.cs)
    /// listening on this localhost port. Lets an external supervisor drive the
    /// running game (spawn, camera, screenshot, state) without rebuilding. Off
    /// in normal play.</summary>
    public static int DevServerPort;
    /// <summary>Optional unit id selector. Used by debug scenarios like
    /// <c>stride_debug</c> to pick which unit's calibration to visualize.</summary>
    public static string? Unit;
    /// <summary>Headless maintenance: load every <c>data/*.json</c> registry and
    /// immediately re-save it, then exit — no window, no GL. Normalizes the
    /// on-disk JSON to the current serializer formatting (escaping, newlines,
    /// property order) so a load→save roundtrip is visible in <c>git diff</c>.
    /// Does NOT touch map/asset files (assets/maps, env_defs, sidecars).</summary>
    public static bool RoundtripData;

    public static void Parse(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--scenario" when i + 1 < args.Length:
                    Scenario = args[++i];
                    break;
                case "--timeout" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out int t)) Timeout = t;
                    break;
                case "--speed" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out int s)) Speed = s;
                    break;
                case "--headless":
                    Headless = true;
                    break;
                case "--autostart":
                    AutoStart = true;
                    break;
                case "--bake-centroids":
                    BakeCentroids = true;
                    AutoStart = true;
                    Headless = true;
                    break;
                case "--no-vsync":
                    NoVsync = true;
                    break;
                case "--devserver" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out int dp)) DevServerPort = dp;
                    break;
                case "--unit" when i + 1 < args.Length:
                    Unit = args[++i];
                    break;
                case "--roundtrip-data":
                    RoundtripData = true;
                    break;
                case "--bgcolor" when i + 1 < args.Length:
                {
                    var parts = args[++i].Split(',');
                    if (parts.Length == 3 &&
                        byte.TryParse(parts[0], out byte r) &&
                        byte.TryParse(parts[1], out byte g) &&
                        byte.TryParse(parts[2], out byte b))
                    {
                        BgColor = new Color(r, g, b);
                    }
                    break;
                }
                case "--resolution" when i + 1 < args.Length:
                {
                    var parts = args[++i].Split('x');
                    if (parts.Length == 2 &&
                        int.TryParse(parts[0], out int w) &&
                        int.TryParse(parts[1], out int h))
                    {
                        ResolutionW = w;
                        ResolutionH = h;
                    }
                    break;
                }
            }
        }
    }
}

public static class Program
{
    /// <summary>Captured at the start of Main, so the gap to LoadContent can be
    /// reported (MonoGame window/GL init + JIT of code paths reached during the
    /// MonoGame Initialize phase). Use Process.GetCurrentProcess().StartTime to
    /// also include OS process spawn + .NET runtime init that happens BEFORE
    /// our managed code runs.</summary>
    public static System.Diagnostics.Stopwatch ProcessStartStopwatch = null!;

    /// <summary>UTC timestamp the OS process was started — earlier than Main entry
    /// by the .NET runtime warmup + assembly load time. Used together with
    /// ProcessStartStopwatch to break "pre-LoadContent" into runtime vs MonoGame
    /// portions.</summary>
    public static DateTime ProcessStartTime;

    /// <summary>Loads every data/ registry via <see cref="Necroking.Data.GameData"/>
    /// and re-saves it, reporting which files the roundtrip actually rewrote. Snapshots
    /// the whole data/ tree before and after so genuinely-reformatted files are listed
    /// (registry Save skips writing when the serialized text is byte-identical).</summary>
    static void RoundtripDataFiles()
    {
        string dataDir = Necroking.Core.GamePaths.Resolve("data");
        Console.WriteLine($"[roundtrip-data] data dir: {dataDir}");

        // Snapshot every JSON under data/ so we can report exactly what changed.
        var before = SnapshotJson(dataDir);

        var gd = new Necroking.Data.GameData();
        if (!gd.Load()) Console.WriteLine("[roundtrip-data] WARNING: GameData.Load reported a failure (continuing).");
        if (!gd.Save()) Console.WriteLine("[roundtrip-data] WARNING: GameData.Save reported a failure.");

        var after = SnapshotJson(dataDir);
        int changed = 0;
        foreach (var kv in after)
        {
            if (!before.TryGetValue(kv.Key, out var old))
                { Console.WriteLine($"[roundtrip-data]   + {kv.Key} (new)"); changed++; }
            else if (old != kv.Value)
                { Console.WriteLine($"[roundtrip-data]   ~ {kv.Key}"); changed++; }
        }
        Console.WriteLine(changed == 0
            ? "[roundtrip-data] done — no files changed (already normalized)."
            : $"[roundtrip-data] done — {changed} file(s) rewritten.");
    }

    /// <summary>Maps each data/*.json path (relative to <paramref name="dataDir"/>) to a
    /// content hash, for before/after change detection.</summary>
    static System.Collections.Generic.Dictionary<string, string> SnapshotJson(string dataDir)
    {
        var map = new System.Collections.Generic.Dictionary<string, string>();
        if (!Directory.Exists(dataDir)) return map;
        foreach (var f in Directory.EnumerateFiles(dataDir, "*.json", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(dataDir, f);
            using var sha = System.Security.Cryptography.SHA256.Create();
            map[rel] = Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(f)));
        }
        return map;
    }

    [STAThread]
    static void Main(string[] args)
    {
        ProcessStartStopwatch = System.Diagnostics.Stopwatch.StartNew();
        try { ProcessStartTime = System.Diagnostics.Process.GetCurrentProcess().StartTime; }
        catch { ProcessStartTime = DateTime.UtcNow; }

        // Set CWD to executable directory so data/ and assets/ are found
        var exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (!string.IsNullOrEmpty(exeDir))
            Directory.SetCurrentDirectory(exeDir);

        LaunchArgs.Parse(args);
        Necroking.Core.GamePaths.DetectRoot();

        // Headless data-JSON roundtrip: load + re-save every data/ registry and exit,
        // without ever building the GL context (GameData is graphics-free). Used to
        // normalize on-disk formatting to the current serializer settings.
        if (LaunchArgs.RoundtripData)
        {
            RoundtripDataFiles();
            return;
        }

        // Must run before Game1 creates the GL context — on dual-GPU laptops Windows
        // otherwise routes this OpenGL app to the integrated GPU (see GpuPreference).
        Necroking.Core.GpuPreference.EnsureHighPerformance();
        try
        {
            using var game = new Game1();
            game.Run();
        }
        catch (Exception ex)
        {
            try
            {
                var dir = Path.Combine(AppContext.BaseDirectory, "log");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "crash.log"),
                    $"{DateTime.Now:O}\n{ex}\n");
            }
            catch { /* last-ditch; nothing more we can do */ }
            throw;
        }
    }
}
