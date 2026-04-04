using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Xna.Framework;

namespace Necroking.Core;

public struct HdrColor
{
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
