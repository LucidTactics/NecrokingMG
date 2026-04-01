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

    public static byte[] HdrToBytes(HdrColor h)
    {
        return new[] { h.R, h.G, h.B, h.A };
    }
}
