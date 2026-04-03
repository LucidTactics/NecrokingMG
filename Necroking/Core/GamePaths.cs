using System;
using System.IO;

namespace Necroking.Core;

/// <summary>
/// Centralized path resolution for data files and assets.
/// The exe runs from bin/Publish/ or bin/Debug/. The project root
/// (where assets/, data/, resources/ live) is two levels up.
/// All game paths are resolved relative to Root.
/// </summary>
public static class GamePaths
{
    /// <summary>
    /// Absolute path to the project root directory (where assets/, data/, bin/ live).
    /// </summary>
    public static string Root { get; private set; } = "";

    /// <summary>Call once at startup to detect the project root.</summary>
    public static void DetectRoot()
    {
        // From bin/Publish/ or bin/Debug/ → ../../
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string candidate = Path.GetFullPath(Path.Combine(baseDir, "..", ".."));
        if (Directory.Exists(Path.Combine(candidate, "data")) &&
            Directory.Exists(Path.Combine(candidate, "assets")))
        {
            Root = candidate;
        }
        else
        {
            // Fallback: data/assets next to exe (deployed build)
            Root = baseDir;
        }
        DebugLog.Log("startup", $"Project root: {Root}");
    }

    /// <summary>Resolve a project-relative path to an absolute path.</summary>
    public static string Resolve(string relativePath) => Path.Combine(Root, relativePath);

    // --- Data files ---
    public const string DataDir = "data";
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
    public const string EnvDefsJson = "data/env_defs.json";

    // --- Map files (now in data/maps/) ---
    public const string DefaultMapJson = "data/maps/default.json";
    public const string MapsDir = "data/maps";

    // --- Asset directories ---
    public const string AssetsDir = "assets";
    public const string SpritesDir = "assets/Sprites";
    public const string EnvironmentDir = "assets/Environment";
    public const string GroundDir = "assets/Environment/Ground";
    public const string RoadsDir = "assets/Environment/Roads";
    public const string EffectsDir = "assets/Effects";
    public const string FontsDir = "assets/fonts";
    public const string UIDefsDir = "assets/UI/definitions";

    // --- Resources ---
    public const string ResourcesDir = "resources";

    // --- Log files (relative to exe, not project root) ---
    public const string LogDir = "log";
    public const string CombatLog = "log/combat.log";
    public const string ScenarioLog = "log/scenario.log";
    public const string ScreenshotDir = "log/screenshots";
}
