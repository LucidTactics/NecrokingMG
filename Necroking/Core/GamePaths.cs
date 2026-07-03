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

    /// <summary>Resolve a per-machine user file under 'user settings/' (gitignored),
    /// seeding it from a shipped default on first run so a fresh clone gets sensible
    /// values. <paramref name="userRelPath"/> is project-relative (e.g.
    /// UserWeatherJson); <paramref name="defaultAbsPath"/> is the absolute default
    /// source. Returns the absolute user-copy path to load from / save to.</summary>
    public static string SeededUserFile(string userRelPath, string defaultAbsPath)
    {
        string user = Resolve(userRelPath);
        if (!File.Exists(user))
        {
            Directory.CreateDirectory(Resolve(UserSettingsDir));
            if (File.Exists(defaultAbsPath)) File.Copy(defaultAbsPath, user);
        }
        return user;
    }

    /// <summary>Convert an absolute path to a project-relative path (forward slashes).
    /// Returns the original path unchanged if it's not under Root.</summary>
    public static string MakeRelative(string absolutePath)
    {
        if (string.IsNullOrEmpty(Root) || string.IsNullOrEmpty(absolutePath)) return absolutePath;
        // Normalize both through GetFullPath to resolve any ../, casing, or slash inconsistencies
        try
        {
            string fullAbs = Path.GetFullPath(absolutePath).Replace('\\', '/');
            string fullRoot = Path.GetFullPath(Root).Replace('\\', '/').TrimEnd('/') + '/';
            if (fullAbs.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                return fullAbs.Substring(fullRoot.Length);
        }
        catch { /* fall through on invalid paths */ }
        return absolutePath;
    }

    // --- Per-machine user settings ---
    // Live in 'user settings/' at the project root, which is .gitignored and NEVER
    // shared via git. Seeded from the shipped default (data/settings.json) on first
    // run; all runtime writes go here, so data/settings.json stops churning.
    public const string UserSettingsDir = "user settings";
    public const string UserSettingsJson = "user settings/settings.json";
    public const string UserWeatherJson = "user settings/weather.json";
    public const string UserSpellBarJson = "user settings/spellbar.json";

    // --- Data files ---
    public const string DataDir = "data";
    public const string SettingsJson = "data/settings.json"; // shipped default / seed only — runtime never writes it
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

    // --- Map files ---
    // NOTE: maps live in assets/maps (gitignored, synced via the collaborator's
    // Drive flow). A migration to data/maps was planned but never executed —
    // if it happens, change ONLY these two constants; all load/save sites
    // route through them.
    public const string DefaultMapJson = "assets/maps/default.json";
    public const string MapsDir = "assets/maps";

    // --- Asset directories ---
    public const string AssetsDir = "assets";
    public const string SpritesDir = "assets/Sprites";
    public const string EnvironmentDir = "assets/Environment";
    public const string GroundDir = "assets/Environment/Ground";
    public const string RoadsDir = "assets/Environment/Roads";
    public const string EffectsDir = "assets/Effects";
    public const string FontsDir = "resources/fonts";
    public const string UIDefsDir = "data/ui";

    // --- Resources ---
    public const string ResourcesDir = "resources";

    // --- Caches ---
    // Regenerable, derived artifacts (NOT hand-authored data). Pre-baked and shipped
    // so a fresh clone is fast, but everything here can be rebuilt from assets/data.
    // Kept apart from data/ so it's obvious which files are real data vs. caches.
    public const string CacheDir = "cache";
    public const string FrameCentroidsJson = "cache/frame_centroids.json"; // baked via --bake-centroids

    // --- Log files (relative to exe, not project root) ---
    public const string LogDir = "log";
    public const string CombatLog = "log/combat.log";
    public const string ScenarioLog = "log/scenario.log";
    public const string ScreenshotDir = "log/screenshots";
    
    // --- Placeholder Icons ---
    
    public const string PlaceholderIcon = "assets/UI/Icons/PlaceholderIcons/Effect36.png";
    public const string PlaceholderSpellIcon = "assets/UI/Icons/PlaceholderIcons/Spell48.png";
}
