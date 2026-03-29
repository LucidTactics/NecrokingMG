using System.Text.Json.Serialization;

namespace Necroking.Data.Registries;

public class WeatherEffects
{
    [JsonPropertyName("brightness")] public float Brightness { get; set; } = 1.0f;
    [JsonPropertyName("contrast")] public float Contrast { get; set; } = 1.0f;
    [JsonPropertyName("saturation")] public float Saturation { get; set; } = 1.0f;
    [JsonPropertyName("tintR")] public float TintR { get; set; } = 1.0f;
    [JsonPropertyName("tintG")] public float TintG { get; set; } = 1.0f;
    [JsonPropertyName("tintB")] public float TintB { get; set; } = 1.0f;
    [JsonPropertyName("tintStrength")] public float TintStrength { get; set; }
    [JsonPropertyName("ambientR")] public float AmbientR { get; set; } = 1.0f;
    [JsonPropertyName("ambientG")] public float AmbientG { get; set; } = 1.0f;
    [JsonPropertyName("ambientB")] public float AmbientB { get; set; } = 1.0f;
    [JsonPropertyName("vignetteStrength")] public float VignetteStrength { get; set; }
    [JsonPropertyName("vignetteRadius")] public float VignetteRadius { get; set; } = 0.7f;
    [JsonPropertyName("vignetteSoftness")] public float VignetteSoftness { get; set; } = 0.4f;
    [JsonPropertyName("fogDensity")] public float FogDensity { get; set; }
    [JsonPropertyName("fogR")] public float FogR { get; set; } = 0.5f;
    [JsonPropertyName("fogG")] public float FogG { get; set; } = 0.55f;
    [JsonPropertyName("fogB")] public float FogB { get; set; } = 0.6f;
    [JsonPropertyName("fogSpeed")] public float FogSpeed { get; set; } = 1.0f;
    [JsonPropertyName("fogScale")] public float FogScale { get; set; } = 1.0f;
    [JsonPropertyName("hazeStrength")] public float HazeStrength { get; set; }
    [JsonPropertyName("hazeR")] public float HazeR { get; set; } = 0.5f;
    [JsonPropertyName("hazeG")] public float HazeG { get; set; } = 0.55f;
    [JsonPropertyName("hazeB")] public float HazeB { get; set; } = 0.6f;
    [JsonPropertyName("lightningEnabled")] public bool LightningEnabled { get; set; }
    [JsonPropertyName("lightningMinInterval")] public float LightningMinInterval { get; set; } = 4.0f;
    [JsonPropertyName("lightningMaxInterval")] public float LightningMaxInterval { get; set; } = 12.0f;
    [JsonPropertyName("rainDensity")] public float RainDensity { get; set; }
    [JsonPropertyName("rainSpeed")] public float RainSpeed { get; set; } = 800.0f;
    [JsonPropertyName("rainWindAngle")] public float RainWindAngle { get; set; } = 10.0f;
    [JsonPropertyName("rainAlpha")] public float RainAlpha { get; set; } = 0.35f;
    [JsonPropertyName("rainLength")] public float RainLength { get; set; } = 15.0f;
    [JsonPropertyName("rainFarOpacity")] public float RainFarOpacity { get; set; } = 0.5f;
    [JsonPropertyName("rainNearScale")] public float RainNearScale { get; set; } = 1.5f;
    [JsonPropertyName("rainSplashScale")] public float RainSplashScale { get; set; } = 1.0f;
    [JsonPropertyName("windStrength")] public float WindStrength { get; set; }
    [JsonPropertyName("windAngle")] public float WindAngle { get; set; }
}

public class WeatherPresetDef : IHasId
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("effects")] public WeatherEffects Effects { get; set; } = new();
}

public class WeatherRegistry : RegistryBase<WeatherPresetDef>
{
    protected override string RootKey => "presets";
}
