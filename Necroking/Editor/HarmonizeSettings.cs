namespace Necroking.Editor;

/// <summary>
/// Persistable harmonizer settings for a texture layer.
/// Stored per element/widget-layer in JSON. On load, the harmonized texture
/// is regenerated from these settings + the source texture.
/// </summary>
public class HarmonizeSettings
{
    public byte[] TargetColor { get; set; } = { 200, 160, 80, 255 };
    public float HueStrength { get; set; }
    public float SatStrength { get; set; }
    public float ValStrength { get; set; }
    public bool UseHcl { get; set; }

    public bool HasEffect => HueStrength > 0.001f || SatStrength > 0.001f || ValStrength > 0.001f
        || TargetColor[3] < 254; // alpha < 255 means alpha multiplier active
}
