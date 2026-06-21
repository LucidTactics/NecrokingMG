using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Necroking.Render;

/// <summary>
/// Texture loading utilities. Handles premultiplied alpha conversion
/// so textures work correctly with BlendState.AlphaBlend.
/// </summary>
public static class TextureUtil
{
    /// <summary>
    /// Load a PNG texture and premultiply its alpha.
    /// This converts straight-alpha PNGs (from most image editors) into
    /// premultiplied-alpha format expected by MonoGame's BlendState.AlphaBlend.
    /// Eliminates white fringe around transparent edges.
    /// </summary>
    public static Texture2D LoadPremultiplied(GraphicsDevice device, string path)
    {
        // Resolve relative asset paths through GamePaths
        string resolved = Path.IsPathRooted(path) ? path : Core.GamePaths.Resolve(path);
        using var stream = File.OpenRead(resolved);
        return LoadPremultiplied(device, stream);
    }

    public static Texture2D LoadPremultiplied(GraphicsDevice device, Stream stream)
    {
        var tex = Texture2D.FromStream(device, stream);
        PremultiplyAlpha(tex);
        return tex;
    }

    /// <summary>
    /// Decode a PNG stream to raw premultiplied Color[] data on any thread (no GPU needed).
    /// Returns (pixels, width, height). Use CreateTextureFromPixels on the main thread.
    /// Uses native SkiaSharp; falls back to managed StbImageSharp on failure.
    /// </summary>
    public static (Color[] pixels, int width, int height) DecodePngPremultiplied(byte[] pngBytes)
    {
        try { return DecodePngPremultipliedSkia(pngBytes); }
        catch { return DecodePngPremultipliedStb(pngBytes); }
    }

    /// <summary>Native PNG decode via SkiaSharp. Asks Skia for premultiplied RGBA8888 directly.</summary>
    public static (Color[] pixels, int width, int height) DecodePngPremultipliedSkia(byte[] pngBytes)
    {
        using var data = SkiaSharp.SKData.CreateCopy(pngBytes);
        using var codec = SkiaSharp.SKCodec.Create(data)
            ?? throw new System.IO.InvalidDataException("SKCodec.Create returned null");
        var info = new SkiaSharp.SKImageInfo(
            codec.Info.Width, codec.Info.Height,
            SkiaSharp.SKColorType.Rgba8888,
            SkiaSharp.SKAlphaType.Premul);

        int w = info.Width, h = info.Height;
        var pixels = new Color[w * h];
        // Pin the managed array and let Skia decode straight into it. Color is
        // 4 bytes (R,G,B,A) which matches Rgba8888 byte order.
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            var result = codec.GetPixels(info, handle.AddrOfPinnedObject());
            if (result != SkiaSharp.SKCodecResult.Success && result != SkiaSharp.SKCodecResult.IncompleteInput)
                throw new System.IO.InvalidDataException($"SKCodec.GetPixels failed: {result}");
        }
        finally { handle.Free(); }
        return (pixels, w, h);
    }

    /// <summary>Managed PNG decode via StbImageSharp. Slower fallback path.</summary>
    public static (Color[] pixels, int width, int height) DecodePngPremultipliedStb(byte[] pngBytes)
    {
        using var ms = new MemoryStream(pngBytes);
        var img = StbImageSharp.ImageResult.FromStream(ms, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
        int w = img.Width, h = img.Height;
        var pixels = new Color[w * h];
        for (int i = 0; i < pixels.Length; i++)
        {
            int j = i * 4;
            byte r = img.Data[j], g = img.Data[j + 1], b = img.Data[j + 2], a = img.Data[j + 3];
            if (a < 255)
            {
                float af = a / 255f;
                r = (byte)(r * af);
                g = (byte)(g * af);
                b = (byte)(b * af);
            }
            pixels[i] = new Color(r, g, b, a);
        }
        return (pixels, w, h);
    }

    /// <summary>BENCHMARK: same as DecodePngPremultiplied but returns ticks for the
    /// native call vs any post-decode work. With the Skia path, post-decode work is
    /// zero (Skia returns premultiplied pixels directly into the pinned array). With
    /// the Stb fallback, "post" is the managed PMA loop.</summary>
    public static (Color[] pixels, int width, int height, long decodeTicks, long pmaTicks, bool usedSkia)
        DecodePngPremultipliedTimed(byte[] pngBytes)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var (pixels, w, h) = DecodePngPremultipliedSkia(pngBytes);
            return (pixels, w, h, sw.ElapsedTicks, 0, true);
        }
        catch
        {
            // Stb fallback: split decode vs PMA.
            sw.Restart();
            using var ms = new MemoryStream(pngBytes);
            var img = StbImageSharp.ImageResult.FromStream(ms, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
            long decodeT = sw.ElapsedTicks;
            int w = img.Width, h = img.Height;
            var pixels = new Color[w * h];
            long pmaStart = sw.ElapsedTicks;
            for (int i = 0; i < pixels.Length; i++)
            {
                int j = i * 4;
                byte r = img.Data[j], g = img.Data[j + 1], b = img.Data[j + 2], a = img.Data[j + 3];
                if (a < 255)
                {
                    float af = a / 255f;
                    r = (byte)(r * af);
                    g = (byte)(g * af);
                    b = (byte)(b * af);
                }
                pixels[i] = new Color(r, g, b, a);
            }
            long pmaT = sw.ElapsedTicks - pmaStart;
            return (pixels, w, h, decodeT, pmaT, false);
        }
    }

    /// <summary>
    /// Create a Texture2D from pre-decoded pixel data. Must be called on the main/GPU thread.
    /// When <paramref name="mipMap"/> is true, a full mip chain is generated CPU-side
    /// (box-downsample) and uploaded per level — MonoGame 3.8 DesktopGL has no reliable
    /// public GenerateMipmaps(), so allocating with mipMap:true alone leaves the lower
    /// levels empty. Trilinear-filtered minification (e.g. zoomed-out tiling ground)
    /// then samples the pre-averaged levels instead of aliasing the base texels.
    /// </summary>
    public static Texture2D CreateTextureFromPixels(GraphicsDevice device, Color[] pixels, int width, int height, bool mipMap = false)
    {
        if (!mipMap)
        {
            var tex0 = new Texture2D(device, width, height);
            tex0.SetData(pixels);
            return tex0;
        }

        var tex = new Texture2D(device, width, height, true, SurfaceFormat.Color);
        tex.SetData(0, null, pixels, 0, pixels.Length);

        // Box-downsample successively until the 1x1 level. Level count is
        // floor(log2(max(w,h)))+1; we stop when both dims have reached 1.
        Color[] cur = pixels;
        int cw = width, ch = height, level = 1;
        while (cw > 1 || ch > 1)
        {
            (cur, cw, ch) = BoxDownsample(cur, cw, ch);
            tex.SetData(level, null, cur, 0, cur.Length);
            level++;
        }
        return tex;
    }

    /// <summary>
    /// Halve a Color[] image to the next mip level by averaging each 2x2 block.
    /// Floor-division dimensions (min 1) so non-power-of-two textures still chain;
    /// odd edges clamp by sampling the last valid column/row. Averages straight in
    /// 8-bit RGBA — fine for the opaque ground textures this is used for.
    /// </summary>
    private static (Color[] pixels, int w, int h) BoxDownsample(Color[] src, int w, int h)
    {
        int nw = System.Math.Max(1, w / 2);
        int nh = System.Math.Max(1, h / 2);
        var dst = new Color[nw * nh];
        for (int y = 0; y < nh; y++)
        {
            int y0 = y * 2, y1 = System.Math.Min(y0 + 1, h - 1);
            for (int x = 0; x < nw; x++)
            {
                int x0 = x * 2, x1 = System.Math.Min(x0 + 1, w - 1);
                Color a = src[y0 * w + x0], b = src[y0 * w + x1];
                Color c = src[y1 * w + x0], d = src[y1 * w + x1];
                dst[y * nw + x] = new Color(
                    (a.R + b.R + c.R + d.R + 2) / 4,
                    (a.G + b.G + c.G + d.G + 2) / 4,
                    (a.B + b.B + c.B + d.B + 2) / 4,
                    (a.A + b.A + c.A + d.A + 2) / 4);
            }
        }
        return (dst, nw, nh);
    }

    /// <summary>
    /// Premultiply alpha in-place: RGB *= A/255.
    /// After this, pixels with low alpha have proportionally dark RGB,
    /// so they don't produce bright fringe when blended.
    /// </summary>
    public static void PremultiplyAlpha(Texture2D texture)
    {
        var data = new Color[texture.Width * texture.Height];
        texture.GetData(data);

        for (int i = 0; i < data.Length; i++)
        {
            var c = data[i];
            if (c.A < 255)
            {
                float a = c.A / 255f;
                data[i] = new Color(
                    (byte)(c.R * a),
                    (byte)(c.G * a),
                    (byte)(c.B * a),
                    c.A);
            }
        }

        texture.SetData(data);
    }
}
