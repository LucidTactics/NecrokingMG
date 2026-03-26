using System;
using Microsoft.Xna.Framework;

namespace Necroking;

public static class LaunchArgs
{
    public static string? Scenario;
    public static int Timeout = 30;
    public static int Speed = 1;
    public static bool Headless;
    public static Color? BgColor;
    public static int ResolutionW;
    public static int ResolutionH;

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
    [STAThread]
    static void Main(string[] args)
    {
        LaunchArgs.Parse(args);
        using var game = new Game1();
        game.Run();
    }
}
