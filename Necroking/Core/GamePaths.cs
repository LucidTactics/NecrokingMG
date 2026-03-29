namespace Necroking.Core;

/// <summary>
/// Centralized path constants for data files and assets.
/// All paths are relative to the executable directory.
/// </summary>
public static class GamePaths
{
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
