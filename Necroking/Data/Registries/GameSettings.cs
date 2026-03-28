using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Necroking.Data.Registries;

public class BloomSettings
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("threshold")] public float Threshold { get; set; } = 0.8f;
    [JsonPropertyName("softKnee")] public float SoftKnee { get; set; } = 0.5f;
    [JsonPropertyName("intensity")] public float Intensity { get; set; } = 1.0f;
    [JsonPropertyName("scatter")] public float Scatter { get; set; } = 0.7f;
    [JsonPropertyName("iterations")] public int Iterations { get; set; } = 6;
    [JsonPropertyName("bicubicUpsampling")] public bool BicubicUpsampling { get; set; } = true;
}

public class GrassSettings
{
    [JsonPropertyName("baseColor")] public ColorJson BaseColor { get; set; } = new() { R = 46, G = 102, B = 20, A = 255 };
    [JsonPropertyName("tipColor")] public ColorJson TipColor { get; set; } = new() { R = 100, G = 166, B = 50, A = 255 };
    [JsonPropertyName("density")] public float Density { get; set; } = 160.0f;
    [JsonPropertyName("height")] public float Height { get; set; } = 150.0f;
    [JsonPropertyName("bladesPerCell")] public int BladesPerCell { get; set; } = 10;
    [JsonPropertyName("cellSize")] public float CellSize { get; set; } = 0.8f;
    [JsonPropertyName("windSpeed")] public float WindSpeed { get; set; } = 1.0f;
    [JsonPropertyName("windStrength")] public float WindStrength { get; set; } = 1.0f;
    [JsonPropertyName("fwidthSmoothing")] public bool FwidthSmoothing { get; set; } = true;
    [JsonPropertyName("minBladeWidth")] public bool MinBladeWidth { get; set; } = true;
}

public class WeatherSettings
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("activePreset")] public string ActivePreset { get; set; } = "";
    [JsonPropertyName("transitionSpeed")] public float TransitionSpeed { get; set; } = 1.0f;
}

public class GeneralSettings
{
    [JsonPropertyName("showTimeControls")] public bool ShowTimeControls { get; set; } = true;
    [JsonPropertyName("showUnitRadius")] public bool ShowUnitRadius { get; set; }
    [JsonPropertyName("showObjectRadius")] public bool ShowObjectRadius { get; set; }
    [JsonPropertyName("groundTypeWarp")] public float GroundTypeWarp { get; set; } = 1.8f;
    [JsonPropertyName("groundUVWarpAmp")] public float GroundUVWarpAmp { get; set; } = 0.4f;
    [JsonPropertyName("groundUVWarpFreq")] public float GroundUVWarpFreq { get; set; } = 0.15f;
    [JsonPropertyName("editorScrollSpeed")] public float EditorScrollSpeed { get; set; } = 30.0f;
    [JsonPropertyName("editorScrollAccel")] public float EditorScrollAccel { get; set; } = 6.0f;
    [JsonPropertyName("combatLogEnabled")] public bool CombatLogEnabled { get; set; } = true;
    [JsonPropertyName("combatLogLines")] public int CombatLogLines { get; set; } = 10;
    [JsonPropertyName("combatLogFadeTime")] public float CombatLogFadeTime { get; set; } = 3.0f;
    [JsonPropertyName("combatLogFontSize")] public int CombatLogFontSize { get; set; } = 12;
    [JsonPropertyName("wpRapidEdit")] public bool WpRapidEdit { get; set; }
    [JsonPropertyName("buildingsDestructible")] public bool BuildingsDestructible { get; set; } = true;
    [JsonPropertyName("buildingDepositRange")] public float BuildingDepositRange { get; set; } = 5.0f;
    [JsonPropertyName("buildingPlacementRange")] public float BuildingPlacementRange { get; set; } = 20.0f;
    [JsonPropertyName("damageNumbersEnabled")] public bool DamageNumbersEnabled { get; set; } = true;
    [JsonPropertyName("damageNumberColor")] public ColorJson DamageNumberColor { get; set; } = new() { R = 200, G = 80, B = 80, A = 255 };
    [JsonPropertyName("damageNumberSize")] public int DamageNumberSize { get; set; } = 16;
    [JsonPropertyName("damageNumberFadeTime")] public float DamageNumberFadeTime { get; set; } = 0.75f;
    [JsonPropertyName("damageNumberSpeed")] public float DamageNumberSpeed { get; set; } = 1.5f;
}

public class ShadowSettings
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("sunAngle")] public float SunAngle { get; set; } = 225.0f;
    [JsonPropertyName("lengthScale")] public float LengthScale { get; set; } = 0.6f;
    [JsonPropertyName("opacity")] public float Opacity { get; set; } = 0.35f;
    [JsonPropertyName("squash")] public float Squash { get; set; } = 0.3f;
    [JsonPropertyName("unitShadowMode")] public int UnitShadowMode { get; set; } = 1;
}

public class CombatSettings
{
    [JsonPropertyName("attackCooldown")] public float AttackCooldown { get; set; } = 3.0f;
    [JsonPropertyName("postAttackLockout")] public float PostAttackLockout { get; set; } = 1.0f;
    [JsonPropertyName("facingThreshold")] public float FacingThreshold { get; set; } = 60f;
    [JsonPropertyName("turnSpeed")] public float TurnSpeed { get; set; } = 360f;
    [JsonPropertyName("meleeRange")] public float MeleeRange { get; set; } = 0.8f;
    [JsonPropertyName("accelHalfTime")] public float AccelHalfTime { get; set; } = 1.2f;
    [JsonPropertyName("accel80Time")] public float Accel80Time { get; set; } = 3.0f;
    [JsonPropertyName("accelFullTime")] public float AccelFullTime { get; set; } = 6.0f;
}

public class HordeSettings
{
    [JsonPropertyName("circleOffset")] public float CircleOffset { get; set; } = 6.0f;
    [JsonPropertyName("circleRadius")] public float CircleRadius { get; set; } = 12.0f;
    [JsonPropertyName("positionLerp")] public float PositionLerp { get; set; } = 3.0f;
    [JsonPropertyName("rotationLerp")] public float RotationLerp { get; set; } = 1.5f;
    [JsonPropertyName("driftHz")] public float DriftHz { get; set; } = 0.2f;
    [JsonPropertyName("driftAmplitude")] public float DriftAmplitude { get; set; } = 0.7f;
    [JsonPropertyName("idleRadius")] public float IdleRadius { get; set; } = 2.0f;
    [JsonPropertyName("engagementRange")] public float EngagementRange { get; set; } = 10.0f;
    [JsonPropertyName("leashRadius")] public float LeashRadius { get; set; } = 25.0f;
    [JsonPropertyName("leashChance")] public float LeashChance { get; set; } = 0.25f;
    [JsonPropertyName("returnSpeedMult")] public float ReturnSpeedMult { get; set; } = 0.65f;
    [JsonPropertyName("velocityDirLerp")] public float VelocityDirLerp { get; set; } = 6.0f;
}

public class GameSettingsData
{
    [JsonPropertyName("bloom")] public BloomSettings Bloom { get; set; } = new();
    [JsonPropertyName("grass")] public GrassSettings Grass { get; set; } = new();
    [JsonPropertyName("weather")] public WeatherSettings Weather { get; set; } = new();
    [JsonPropertyName("general")] public GeneralSettings General { get; set; } = new();
    [JsonPropertyName("shadow")] public ShadowSettings Shadow { get; set; } = new();
    [JsonPropertyName("horde")] public HordeSettings Horde { get; set; } = new();
    [JsonPropertyName("combat")] public CombatSettings Combat { get; set; } = new();

    public bool Load(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            string json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<GameSettingsData>(json);
            if (loaded == null) return false;
            Bloom = loaded.Bloom;
            Grass = loaded.Grass;
            Weather = loaded.Weather;
            General = loaded.General;
            Shadow = loaded.Shadow;
            Horde = loaded.Horde;
            Combat = loaded.Combat;
            return true;
        }
        catch { return false; }
    }

    public bool Save(string path)
    {
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(path, JsonSerializer.Serialize(this, options));
            return true;
        }
        catch { return false; }
    }
}
