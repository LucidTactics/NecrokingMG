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
    public static Color? BgColor;
    public static int ResolutionW;
    public static int ResolutionH;
    /// <summary>Optional unit id selector. Used by debug scenarios like
    /// <c>stride_debug</c> to pick which unit's calibration to visualize.</summary>
    public static string? Unit;

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
                case "--no-vsync":
                    NoVsync = true;
                    break;
                case "--unit" when i + 1 < args.Length:
                    Unit = args[++i];
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
        using var game = new Game1();
        game.Run();
    }
}
