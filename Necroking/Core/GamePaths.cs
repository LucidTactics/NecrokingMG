using System.IO;

namespace Necroking.Core;

/// <summary>
/// Centralized path constants for data files and assets.
/// All paths are relative to the executable directory.
/// </summary>
public static class GamePaths
{
    /// <summary>
    /// Path to the Necroking/ source project directory. Detected at startup.
    /// Used for dual-saving: runtime edits are written here so they survive rebuilds.
    /// Null if source tree cannot be found (e.g., deployed without source).
    /// </summary>
    public static string? SourceRoot { get; private set; }

    /// <summary>Call once at startup to detect the source tree root.</summary>
    public static void DetectSourceRoot()
    {
        // From bin/Publish/ → ../../Necroking/
        // From bin/Debug/net9.0/ → ../../../Necroking/
        string[] candidates = { "../../Necroking", "../../../Necroking" };
        foreach (var rel in candidates)
        {
            string abs = Path.GetFullPath(rel);
            if (File.Exists(Path.Combine(abs, "Necroking.csproj")))
            {
                SourceRoot = abs;
                DebugLog.Log("startup", $"Source root detected: {SourceRoot}");
                return;
            }
        }
        DebugLog.Log("startup", "Source root not found (deployed build)");
    }

    /// <summary>Get the source tree equivalent of a runtime-relative path, or null if no source root.</summary>
    public static string? ToSourcePath(string runtimeRelativePath)
    {
        if (SourceRoot == null) return null;
        return Path.Combine(SourceRoot, runtimeRelativePath);
    }

    /// <summary>Copy a runtime file to its source tree equivalent. Safe no-op if source root not found.</summary>
    public static void DualSave(string runtimePath)
    {
        var srcPath = ToSourcePath(runtimePath);
        if (srcPath == null || !File.Exists(runtimePath)) return;
        try
        {
            var dir = Path.GetDirectoryName(srcPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.Copy(runtimePath, srcPath, overwrite: true);
        }
        catch { /* source tree sync is best-effort */ }
    }


    // Data files
    public const string SettingsJson = "data/settings.json";
    public const string WeatherJson = "data/weather.json";
    public const string UnitsJson = "data/units.json";
    public const string WeaponsJson = "data/weapons.json";
    public const string ArmorsJson = "data/armor.json";
    public const string ShieldsJson = "data/shields.json";
    public const string SpellsJson = "data/spells.json";
    public const string BuffsJson = "data/buffs.json";
    public const string AnimationsJson = "data/animations.json";
    public const string FlipbooksJson = "data/flipbooks.json";
    public const string SpellBarJson = "data/spellbar.json";
    public const string WeaponPointsJson = "data/weapon_points.json";

    // Map files
    public const string DefaultMapJson = "assets/maps/default.json";
    public const string EnvDefsJson = "maps/env_defs.json";

    // Asset directories
    public const string SpritesDir = "assets/Sprites";
    public const string EnvironmentDir = "assets/Environment";
    public const string GroundDir = "assets/Environment/Ground";
    public const string RoadsDir = "assets/Environment/Roads";
    public const string ShadersDir = "assets/shaders";

    // Log files
    public const string LogDir = "log";
    public const string CombatLog = "log/combat.log";
    public const string ScenarioLog = "log/scenario.log";
    public const string ScreenshotDir = "log/screenshots";
}
