using Microsoft.Xna.Framework;

namespace Necroking.Core;

/// <summary>
/// Shared color conversion utilities used by both the editor and runtime widget renderer.
/// </summary>
public static class ColorUtils
{
    /// <summary>Convert byte[] RGBA to premultiplied Color for correct alpha blending.</summary>
    public static Color ByteColor(byte[] c, byte alphaOverride = 0)
    {
        byte a = alphaOverride > 0 ? alphaOverride : (c.Length > 3 ? c[3] : (byte)255);
        if (a == 255)
            return new Color((int)c[0], (int)c[1], (int)c[2], 255);
        float af = a / 255f;
        return new Color((byte)(c[0] * af), (byte)(c[1] * af), (byte)(c[2] * af), a);
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

    public static byte[] HdrToBytes(HdrColor h)
    {
        return new[] { h.R, h.G, h.B, h.A };
    }
}
