using System;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Necroking.Render;

/// <summary>
/// Minimal decoders for the two non-PNG formats in the flipbook library
/// (assets/Effects/Flipbooks): Unity-exported OpenEXR and Targa. Deliberately
/// NOT general-purpose — they support exactly the shapes those files use and
/// throw on anything else (TextureCache negative-caches the failure):
///  • EXR: v2 single-part scanline, UNCOMPRESSED, half-float channels
///    (A)BGR(A). Linear HDR is clamped and sRGB-encoded so the preview
///    matches the LDR .tga twin of the same animation.
///  • TGA: type 2 (uncompressed truecolor), 24/32 bpp, bottom-up rows.
/// Output is premultiplied Color data, same convention as
/// TextureUtil.LoadPremultiplied.
/// </summary>
public static class ExrTgaTextures
{
    public static Texture2D LoadExrPremultiplied(GraphicsDevice device, string path)
    {
        byte[] data = File.ReadAllBytes(path);
        if (data.Length < 8 || BitConverter.ToInt32(data, 0) != 0x01312f76)
            throw new InvalidDataException("not an EXR file");
        int version = data[4];
        if (version != 2 || (BitConverter.ToInt32(data, 4) & ~0xff) != 0)
            throw new InvalidDataException("unsupported EXR version/flags (need v2 single-part scanline)");

        // --- Header: attribute list terminated by an empty name ---
        int off = 8;
        string[] channels = Array.Empty<string>();
        int compression = -1;
        int xMin = 0, yMin = 0, xMax = -1, yMax = -1;
        while (data[off] != 0)
        {
            string name = ReadCString(data, ref off);
            string type = ReadCString(data, ref off);
            int size = BitConverter.ToInt32(data, off); off += 4;

            if (type == "chlist")
            {
                // These files carry a WRONG declared size (the exporter didn't
                // count all channels), so parse entries up to the terminator
                // and resume after it rather than trusting `size`.
                var names = new System.Collections.Generic.List<string>();
                while (data[off] != 0)
                {
                    string ch = ReadCString(data, ref off);
                    int pixelType = BitConverter.ToInt32(data, off);
                    if (pixelType != 1)
                        throw new InvalidDataException($"EXR channel '{ch}' is not half-float");
                    off += 16; // pixelType + pLinear/reserved + x/y sampling
                    names.Add(ch);
                }
                off++; // chlist terminator
                channels = names.ToArray(); // storage order (alphabetical)
            }
            else
            {
                switch (name)
                {
                    case "compression": compression = data[off]; break;
                    case "dataWindow":
                        xMin = BitConverter.ToInt32(data, off);
                        yMin = BitConverter.ToInt32(data, off + 4);
                        xMax = BitConverter.ToInt32(data, off + 8);
                        yMax = BitConverter.ToInt32(data, off + 12);
                        break;
                    case "lineOrder":
                        if (data[off] != 0)
                            throw new InvalidDataException("unsupported EXR line order");
                        break;
                }
                off += size;
            }
        }
        off++; // header terminator

        if (compression != 0)
            throw new InvalidDataException($"unsupported EXR compression {compression} (need uncompressed)");
        int w = xMax - xMin + 1, h = yMax - yMin + 1;
        if (w <= 0 || h <= 0 || channels.Length == 0)
            throw new InvalidDataException("bad EXR dimensions/channels");
        int r = Array.IndexOf(channels, "R"), g = Array.IndexOf(channels, "G"),
            b = Array.IndexOf(channels, "B"), a = Array.IndexOf(channels, "A");
        if (r < 0 || g < 0 || b < 0)
            throw new InvalidDataException("EXR lacks RGB channels");

        off += h * 8; // scanline offset table — blocks follow sequentially

        // --- Scanline blocks: y, byte count, then planar half rows per channel ---
        var pixels = new Color[w * h];
        int rowBytes = w * 2;
        for (int line = 0; line < h; line++)
        {
            int y = BitConverter.ToInt32(data, off) - yMin; off += 8; // y + size
            if (y < 0 || y >= h) throw new InvalidDataException("EXR scanline out of range");
            int rowBase = off;
            for (int x = 0; x < w; x++)
            {
                float rf = ReadHalf(data, rowBase + r * rowBytes + x * 2);
                float gf = ReadHalf(data, rowBase + g * rowBytes + x * 2);
                float bf = ReadHalf(data, rowBase + b * rowBytes + x * 2);
                float af = a >= 0 ? Math.Clamp(ReadHalf(data, rowBase + a * rowBytes + x * 2), 0f, 1f) : 1f;
                pixels[y * w + x] = new Color(
                    (byte)(LinearToSrgb(rf) * af * 255f + 0.5f),
                    (byte)(LinearToSrgb(gf) * af * 255f + 0.5f),
                    (byte)(LinearToSrgb(bf) * af * 255f + 0.5f),
                    (byte)(af * 255f + 0.5f));
            }
            off += channels.Length * rowBytes;
        }

        var tex = new Texture2D(device, w, h);
        tex.SetData(pixels);
        return tex;
    }

    public static Texture2D LoadTgaPremultiplied(GraphicsDevice device, string path)
    {
        byte[] data = File.ReadAllBytes(path);
        if (data.Length < 18) throw new InvalidDataException("truncated TGA");
        int idLen = data[0];
        int imageType = data[2];
        if (imageType != 2)
            throw new InvalidDataException($"unsupported TGA type {imageType} (need 2, uncompressed truecolor)");
        int w = BitConverter.ToUInt16(data, 12);
        int h = BitConverter.ToUInt16(data, 14);
        int bpp = data[16];
        if (bpp != 24 && bpp != 32)
            throw new InvalidDataException($"unsupported TGA depth {bpp}");
        bool topDown = (data[17] & 0x20) != 0;

        int bytesPer = bpp / 8;
        int src = 18 + idLen;
        var pixels = new Color[w * h];
        for (int row = 0; row < h; row++)
        {
            int y = topDown ? row : h - 1 - row;
            for (int x = 0; x < w; x++)
            {
                byte bb = data[src], gg = data[src + 1], rr = data[src + 2];
                byte aa = bytesPer == 4 ? data[src + 3] : (byte)255;
                pixels[y * w + x] = new Color(
                    (byte)(rr * aa / 255), (byte)(gg * aa / 255), (byte)(bb * aa / 255), aa);
                src += bytesPer;
            }
        }

        var tex = new Texture2D(device, w, h);
        tex.SetData(pixels);
        return tex;
    }

    private static string ReadCString(byte[] data, ref int off)
    {
        int end = Array.IndexOf(data, (byte)0, off);
        string s = System.Text.Encoding.ASCII.GetString(data, off, end - off);
        off = end + 1;
        return s;
    }

    private static float ReadHalf(byte[] data, int off)
        => (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(data, off));

    /// <summary>Clamp linear HDR to [0,1] and sRGB-encode, matching how the
    /// LDR twin of the same flipbook looks.</summary>
    private static float LinearToSrgb(float v)
    {
        v = Math.Clamp(v, 0f, 1f);
        return v <= 0.0031308f ? v * 12.92f : 1.055f * MathF.Pow(v, 1f / 2.4f) - 0.055f;
    }
}
