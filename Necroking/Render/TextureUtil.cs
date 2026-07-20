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

        // The flipbook library ships .exr (HDR) and .tga alongside .png;
        // Texture2D.FromStream can't read those, so route by extension.
        switch (Path.GetExtension(resolved).ToLowerInvariant())
        {
            case ".exr": return ExrTgaTextures.LoadExrPremultiplied(device, resolved);
            case ".tga": return ExrTgaTextures.LoadTgaPremultiplied(device, resolved);
        }

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

    /// <summary>Premultiply one straight-alpha RGBA pixel: RGB *= A/255. Single source
    /// of truth for the premultiply math shared by the Stb decode paths and
    /// <see cref="PremultiplyAlpha"/>.</summary>
    private static Color PremultiplyPixel(byte r, byte g, byte b, byte a)
    {
        if (a < 255)
        {
            float af = a / 255f;
            r = (byte)(r * af);
            g = (byte)(g * af);
            b = (byte)(b * af);
        }
        return new Color(r, g, b, a);
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
            pixels[i] = PremultiplyPixel(img.Data[j], img.Data[j + 1], img.Data[j + 2], img.Data[j + 3]);
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
                pixels[i] = PremultiplyPixel(img.Data[j], img.Data[j + 1], img.Data[j + 2], img.Data[j + 3]);
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
            data[i] = PremultiplyPixel(c.R, c.G, c.B, c.A);
        }

        texture.SetData(data);
    }

    /// <summary>
    /// Get or create a cached 1x1 white texture. Subsequent calls with the same
    /// GraphicsDevice return the cached instance (lazy, device-keyed). If the device
    /// is disposed, the cache is invalidated.
    /// </summary>
    private static readonly System.Collections.Generic.Dictionary<GraphicsDevice, Texture2D> _whitePixelCache = new();

    public static Texture2D GetWhitePixel(GraphicsDevice device)
    {
        if (_whitePixelCache.TryGetValue(device, out var cached))
        {
            if (!cached.IsDisposed) return cached;
            _whitePixelCache.Remove(device);
        }
        var white = new Texture2D(device, 1, 1);
        white.SetData(new[] { Color.White });
        _whitePixelCache[device] = white;
        return white;
    }

    /// <summary>
    /// Get or create a cached radial glow texture: <paramref name="size"/>×<paramref name="size"/>,
    /// premultiplied, with a quadratic-falloff radial gradient (opaque center → transparent
    /// edge). Device+size-keyed, mirroring <see cref="GetWhitePixel"/>. This is the single
    /// home for the glow sprite that Game1, SpellPreview, and EnvObjectEditorWindow each
    /// generated byte-identically. Do NOT dispose the returned texture — it is shared/cached.
    /// </summary>
    private static readonly System.Collections.Generic.Dictionary<(GraphicsDevice, int), Texture2D> _radialGlowCache = new();

    public static Texture2D GetRadialGlow(GraphicsDevice device, int size = 64)
    {
        var key = (device, size);
        if (_radialGlowCache.TryGetValue(key, out var cached))
        {
            if (!cached.IsDisposed) return cached;
            _radialGlowCache.Remove(key);
        }
        var tex = new Texture2D(device, size, size);
        var data = new Color[size * size];
        float half = size / 2f - 0.5f; // 31.5 for size=64
        for (int gy = 0; gy < size; gy++)
            for (int gx = 0; gx < size; gx++)
            {
                float dx = (gx - half) / half;
                float dy = (gy - half) / half;
                float dist = System.MathF.Sqrt(dx * dx + dy * dy);
                float alpha = System.MathF.Max(0f, 1f - dist);
                alpha *= alpha; // quadratic falloff for soft glow
                byte a = (byte)(alpha * 255);
                data[gy * size + gx] = new Color(a, a, a, a); // premultiplied
            }
        tex.SetData(data);
        _radialGlowCache[key] = tex;
        return tex;
    }

    /// <summary>
    /// Get or create a cached tileable streak-noise texture (256×64): value noise
    /// from coarse random grids sampled bilinearly with wraparound, two octaves,
    /// the base octave stretched horizontally so features read as streaks along U.
    /// Wraps seamlessly on both axes (drain-beam scroll layers sample it with an
    /// unbounded arc-length U). Grayscale in RGB, alpha follows luminance so it
    /// works in both additive and alpha-blended passes. Deterministic (fixed seed).
    /// Do NOT dispose the returned texture — it is shared/cached.
    /// </summary>
    private static readonly System.Collections.Generic.Dictionary<GraphicsDevice, Texture2D> _streakNoiseCache = new();

    public static Texture2D GetStreakNoise(GraphicsDevice device)
    {
        if (_streakNoiseCache.TryGetValue(device, out var cached))
        {
            if (!cached.IsDisposed) return cached;
            _streakNoiseCache.Remove(device);
        }

        const int W = 256, H = 64;
        // Coarse random grids (wrap-sampled). Octave 1 is 8×4 over 256×64 —
        // long 32px-long blobs; octave 2 adds finer 8px detail.
        const int G1W = 8, G1H = 4, G2W = 32, G2H = 8;
        uint seed = 0xBEEFCAFE;
        float Rand() { seed = seed * 1103515245u + 12345u; return ((seed >> 8) & 0xFFFF) / 65535f; }
        var g1 = new float[G1W * G1H];
        var g2 = new float[G2W * G2H];
        for (int i = 0; i < g1.Length; i++) g1[i] = Rand();
        for (int i = 0; i < g2.Length; i++) g2[i] = Rand();

        // Bilinear sample of a wrap-around grid at normalized (u,v), with
        // smoothstep on the fractions so cell boundaries don't show as creases.
        static float Sample(float[] grid, int gw, int gh, float u, float v)
        {
            float x = u * gw, y = v * gh;
            int x0 = (int)System.MathF.Floor(x), y0 = (int)System.MathF.Floor(y);
            float fx = x - x0, fy = y - y0;
            fx = fx * fx * (3f - 2f * fx);
            fy = fy * fy * (3f - 2f * fy);
            int x1 = (x0 + 1) % gw, y1 = (y0 + 1) % gh;
            x0 %= gw; y0 %= gh;
            float a = grid[y0 * gw + x0], b = grid[y0 * gw + x1];
            float c = grid[y1 * gw + x0], d = grid[y1 * gw + x1];
            return (a + (b - a) * fx) * (1f - fy) + (c + (d - c) * fx) * fy;
        }

        var tex = new Texture2D(device, W, H);
        var data = new Color[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                float u = x / (float)W, v = y / (float)H;
                float n = Sample(g1, G1W, G1H, u, v) * 0.65f
                        + Sample(g2, G2W, G2H, u, v) * 0.35f;
                // Push contrast so the streaks read as distinct bright bands
                // instead of uniform gray mush.
                n = System.Math.Clamp((n - 0.35f) * 2.2f, 0f, 1f);
                byte b8 = (byte)(n * 255f);
                data[y * W + x] = new Color(b8, b8, b8, b8); // premultiplied-style
            }
        tex.SetData(data);
        _streakNoiseCache[device] = tex;
        return tex;
    }
}
