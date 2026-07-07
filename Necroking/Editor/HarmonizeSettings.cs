using System.Text.Json;

namespace Necroking.Editor;

/// <summary>
/// Persistable harmonizer settings for a texture layer.
/// Stored per element/widget-layer (UI) or per env-object def in JSON. On load,
/// the harmonized texture is regenerated from these settings + the source texture
/// via <see cref="ColorHarmonizer.HarmonizeTexture"/>.
/// </summary>
public class HarmonizeSettings
{
    public byte[] TargetColor { get; set; } = { 200, 160, 80, 255 };
    public float HueStrength { get; set; }
    public float SatStrength { get; set; }
    public float ValStrength { get; set; }
    public bool UseHcl { get; set; }

    // Vertical gradient baked into the harmonized texture: pixels blend toward
    // GradColor by (yFrac * GradStrength * GradColor.a/255), top -> bottom.
    // Mirrors the Unity SpriteColorSwapper shader's VerGradD2U + GradColor.
    public byte[]? GradColor { get; set; }
    public float GradStrength { get; set; }

    // Silhouette outline baked into the harmonized texture: transparent pixels
    // within OutlineThickness (texture px) of opaque ones get OutlineColor at
    // OutlineOpacity. Mirrors the Unity shader's outline parameters.
    public byte[]? OutlineColor { get; set; }
    public float OutlineThickness { get; set; }
    public float OutlineOpacity { get; set; } = 1f;

    public bool HasGradient => GradStrength > 0.001f && GradColor is { Length: >= 4 } && GradColor[3] > 0;
    public bool HasOutline => OutlineThickness > 0.001f && OutlineColor is { Length: >= 4 } && OutlineOpacity > 0.001f;

    public bool HasEffect => HueStrength > 0.001f || SatStrength > 0.001f || ValStrength > 0.001f
        || TargetColor[3] < 254 // alpha < 255 means alpha multiplier active
        || HasGradient || HasOutline;

    /// <summary>Write this as a named JSON object: { targetColor, hueStrength, ... }.
    /// Canonical serializer — shared by the env-def and (eventually) UI-editor paths.</summary>
    public void Write(Utf8JsonWriter writer, string name)
    {
        writer.WritePropertyName(name);
        WriteValue(writer);
    }

    /// <summary>Value-only variant of <see cref="Write"/> for use inside a
    /// JsonConverter (property name already written by the serializer).</summary>
    public void WriteValue(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteStartArray("targetColor");
        for (int i = 0; i < 4; i++) writer.WriteNumberValue(i < TargetColor.Length ? TargetColor[i] : (byte)255);
        writer.WriteEndArray();
        writer.WriteNumber("hueStrength", HueStrength);
        writer.WriteNumber("satStrength", SatStrength);
        writer.WriteNumber("valStrength", ValStrength);
        writer.WriteBoolean("useHcl", UseHcl);
        if (HasGradient)
        {
            writer.WriteStartArray("gradColor");
            for (int i = 0; i < 4; i++) writer.WriteNumberValue(GradColor![i]);
            writer.WriteEndArray();
            writer.WriteNumber("gradStrength", GradStrength);
        }
        if (HasOutline)
        {
            writer.WriteStartArray("outlineColor");
            for (int i = 0; i < 4; i++) writer.WriteNumberValue(OutlineColor![i]);
            writer.WriteEndArray();
            writer.WriteNumber("outlineThickness", OutlineThickness);
            writer.WriteNumber("outlineOpacity", OutlineOpacity);
        }
        writer.WriteEndObject();
    }

    /// <summary>Parse from a JSON object element. Returns null if not an object.</summary>
    public static HarmonizeSettings? Read(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        var s = new HarmonizeSettings();
        if (el.TryGetProperty("targetColor", out var tc) && tc.ValueKind == JsonValueKind.Array)
        {
            int n = tc.GetArrayLength();
            var arr = new byte[4];
            for (int i = 0; i < 4; i++) arr[i] = i < n ? (byte)tc[i].GetInt32() : (byte)255;
            s.TargetColor = arr;
        }
        if (tc_has(el, "hueStrength", out var hv)) s.HueStrength = hv;
        if (tc_has(el, "satStrength", out var sv)) s.SatStrength = sv;
        if (tc_has(el, "valStrength", out var vv)) s.ValStrength = vv;
        if (el.TryGetProperty("useHcl", out var uh) && uh.ValueKind is JsonValueKind.True or JsonValueKind.False)
            s.UseHcl = uh.GetBoolean();
        if (el.TryGetProperty("gradColor", out var gc) && gc.ValueKind == JsonValueKind.Array && gc.GetArrayLength() >= 4)
        {
            var arr = new byte[4];
            for (int i = 0; i < 4; i++) arr[i] = (byte)gc[i].GetInt32();
            s.GradColor = arr;
        }
        if (tc_has(el, "gradStrength", out var gs)) s.GradStrength = gs;
        if (el.TryGetProperty("outlineColor", out var oc) && oc.ValueKind == JsonValueKind.Array && oc.GetArrayLength() >= 4)
        {
            var arr = new byte[4];
            for (int i = 0; i < 4; i++) arr[i] = (byte)oc[i].GetInt32();
            s.OutlineColor = arr;
        }
        if (tc_has(el, "outlineThickness", out var ot)) s.OutlineThickness = ot;
        if (tc_has(el, "outlineOpacity", out var oo)) s.OutlineOpacity = oo;
        return s;

        static bool tc_has(JsonElement e, string p, out float val)
        {
            if (e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number) { val = v.GetSingle(); return true; }
            val = 0f; return false;
        }
    }

    public HarmonizeSettings Clone() => new()
    {
        TargetColor = (byte[])TargetColor.Clone(),
        HueStrength = HueStrength, SatStrength = SatStrength, ValStrength = ValStrength, UseHcl = UseHcl,
        GradColor = (byte[]?)GradColor?.Clone(), GradStrength = GradStrength,
        OutlineColor = (byte[]?)OutlineColor?.Clone(), OutlineThickness = OutlineThickness, OutlineOpacity = OutlineOpacity,
    };
}

/// <summary>Adapter exposing <see cref="HarmonizeSettings.Read"/> /
/// <see cref="HarmonizeSettings.WriteValue"/> (the canonical hand-written
/// format) to System.Text.Json attribute-based serialization (env defs).</summary>
public class HarmonizeSettingsJsonConverter : System.Text.Json.Serialization.JsonConverter<HarmonizeSettings>
{
    public override HarmonizeSettings? Read(ref Utf8JsonReader reader, System.Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        return HarmonizeSettings.Read(doc.RootElement);
    }

    public override void Write(Utf8JsonWriter writer, HarmonizeSettings value, JsonSerializerOptions options)
        => value.WriteValue(writer);
}
