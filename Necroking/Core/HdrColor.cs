using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Xna.Framework;

namespace Necroking.Core;

public struct HdrColor
{
    /// Max intensity the HdrSprite shader can decode. Must match HdrSprite.fx MaxIntensity default.
    public const float MaxHdrIntensity = 4f;

    public byte R { get; set; } = 255;
    public byte G { get; set; } = 255;
    public byte B { get; set; } = 255;
    public byte A { get; set; } = 255;
    public float Intensity { get; set; } = 1.0f;

    public HdrColor() { }
    public HdrColor(byte r, byte g, byte b, byte a = 255, float intensity = 1.0f)
    {
        R = r; G = g; B = b; A = a; Intensity = intensity;
    }

    public Color ToColor() => new(R, G, B, A);

    // DO NOT USE EXCEPT IN COLORPICKER!!!
    // NOTE: This ruins HDR scaling and bleaches out the color, need to use another way to pass intensity to get real HDR like C++!
    public Color ToScaledColor() => new(
        (byte)Math.Min(255f, R * Intensity),
        (byte)Math.Min(255f, G * Intensity),
        (byte)Math.Min(255f, B * Intensity),
        A
    );

    /// <summary>
    /// Encode for HdrSprite.fx additive mode: bake fade alpha into RGB, encode intensity in alpha byte.
    /// The shader decodes: output.rgb = tex.rgb * color.rgb * (color.a * MaxHdrIntensity).
    /// </summary>
    /// <param name="alpha">Fade multiplier in 0-1 range (e.g. 1.0 = full brightness, 0.5 = half).</param>
    public Color ToHdrVertex(float alpha)
    {
        // Clamp instead of raw byte cast: alpha comes from config-driven curves
        // and a value > 1 would wrap modulo 256 (flickering near-black frames).
        float fade = alpha * (A / 255f); // combine external fade with base alpha
        return new Color(
            (byte)Math.Clamp(R * fade, 0f, 255f),
            (byte)Math.Clamp(G * fade, 0f, 255f),
            (byte)Math.Clamp(B * fade, 0f, 255f),
            (byte)MathF.Round(Math.Clamp(Intensity / MaxHdrIntensity, 0f, 1f) * 255f)
        );
    }

    /// Overload for when base color and intensity are separate (Effect.Tint + Effect.HdrIntensity).
    /// tint.A participates in the fade, matching the instance overload above.
    public static Color ToHdrVertex(Color tint, float alpha, float hdrIntensity)
    {
        float fade = alpha * (tint.A / 255f);
        return new Color(
            (byte)Math.Clamp(tint.R * fade, 0f, 255f),
            (byte)Math.Clamp(tint.G * fade, 0f, 255f),
            (byte)Math.Clamp(tint.B * fade, 0f, 255f),
            (byte)MathF.Round(Math.Clamp(hdrIntensity / MaxHdrIntensity, 0f, 1f) * 255f)
        );
    }

    /// <summary>
    /// Encode for HdrAlpha technique: intensity baked into RGB, alpha carries real fade.
    /// Shader decodes: output.rgb = tex.rgb * color.rgb * MaxIntensity, output.a = tex.a * color.a.
    /// High-intensity channels may clip — acceptable since bloom smooths the result.
    /// RGB is rounded, not truncated: at the default intensity (scale 0.25) truncation
    /// dimmed every alpha-mode effect ~1.2% and quantized tints to 64 levels.
    /// tint.A participates in the fade, matching ToHdrVertex.
    /// </summary>
    public static Color ToHdrVertexAlpha(Color tint, float alpha, float hdrIntensity)
    {
        float scale = hdrIntensity / MaxHdrIntensity;
        return new Color(
            (byte)Math.Clamp(MathF.Round(tint.R * scale), 0f, 255f),
            (byte)Math.Clamp(MathF.Round(tint.G * scale), 0f, 255f),
            (byte)Math.Clamp(MathF.Round(tint.B * scale), 0f, 255f),
            (byte)Math.Clamp(alpha * tint.A, 0f, 255f)
        );
    }
}

public class HdrColorJsonConverter : JsonConverter<HdrColor>
{
    public override HdrColor Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var c = new HdrColor();
        using var doc = JsonDocument.ParseValue(ref reader);
        var obj = doc.RootElement;
        if (obj.TryGetProperty("r", out var r)) c.R = r.GetByte();
        if (obj.TryGetProperty("g", out var g)) c.G = g.GetByte();
        if (obj.TryGetProperty("b", out var b)) c.B = b.GetByte();
        if (obj.TryGetProperty("a", out var a)) c.A = a.GetByte();
        if (obj.TryGetProperty("intensity", out var i)) c.Intensity = i.GetSingle();
        return c;
    }

    public override void Write(Utf8JsonWriter writer, HdrColor value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("r", value.R);
        writer.WriteNumber("g", value.G);
        writer.WriteNumber("b", value.B);
        writer.WriteNumber("a", value.A);
        writer.WriteNumber("intensity", value.Intensity);
        writer.WriteEndObject();
    }
}
