using Microsoft.Xna.Framework;

namespace Necroking.Core;

/// <summary>
/// Shared color conversion utilities used by both the editor and runtime widget renderer.
/// </summary>
public static class ColorUtils
{
    /// <summary>Convert byte[] RGBA to a STRAIGHT-alpha Color. The draw surfaces
    /// (SpriteScope / the queue flush) premultiply per the open material — never
    /// pre-encode here.</summary>
    public static Color ByteColor(byte[] c, byte alphaOverride = 0)
    {
        byte a = alphaOverride > 0 ? alphaOverride : (c.Length > 3 ? c[3] : (byte)255);
        return new Color(c[0], c[1], c[2], a);
    }

    public static HdrColor BytesToHdr(byte[] c)
    {
        return new HdrColor(c[0], c[1], c[2], c.Length > 3 ? c[3] : (byte)255);
    }

    /// <summary>Multiply two colors component-wise (for ambient tinting).</summary>
    public static Color Multiply(Color a, Color b)
    {
        return new Color(
            (byte)(a.R * b.R / 255),
            (byte)(a.G * b.G / 255),
            (byte)(a.B * b.B / 255),
            (byte)(a.A * b.A / 255));
    }

    /// <summary>Scale a color's RGB by a brightness factor, with optional alpha override.</summary>
    public static Color Scale(Color c, float brightness, byte alpha = 255)
    {
        return new Color(
            (byte)MathHelper.Clamp((int)(c.R * brightness), 0, 255),
            (byte)MathHelper.Clamp((int)(c.G * brightness), 0, 255),
            (byte)MathHelper.Clamp((int)(c.B * brightness), 0, 255),
            alpha);
    }

    /// <summary>Premultiply RGB by alpha for correct blending with BlendState.AlphaBlend.</summary>
    public static Color Premultiply(int r, int g, int b, float alpha)
    {
        alpha = MathHelper.Clamp(alpha, 0f, 1f);
        return new Color(r, g, b) * alpha;
    }

    /// <summary>Premultiply a straight-alpha color (scale RGB by A). Identity for
    /// opaque colors. This is the single conversion the draw layer applies when a
    /// material's <c>PremultiplyTint</c> is set — call sites author straight alpha
    /// and never premultiply by hand.</summary>
    public static Color Premultiply(Color straight)
    {
        if (straight.A == 255) return straight;
        int a = straight.A;
        return new Color(
            (byte)(straight.R * a / 255),
            (byte)(straight.G * a / 255),
            (byte)(straight.B * a / 255),
            (byte)a);
    }

    /// <summary>Fade a straight-alpha color: scales A only, leaving RGB (hue)
    /// untouched. The straight-alpha replacement for the premult-era
    /// <c>color * t</c> idiom (which scaled all four channels).</summary>
    public static Color Fade(Color straight, float t)
    {
        t = MathHelper.Clamp(t, 0f, 1f);
        straight.A = (byte)(straight.A * t);
        return straight;
    }

    public static byte[] HdrToBytes(HdrColor h)
    {
        return new[] { h.R, h.G, h.B, h.A };
    }
}
