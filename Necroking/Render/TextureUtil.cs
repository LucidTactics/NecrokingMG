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
        using var stream = File.OpenRead(path);
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
    /// </summary>
    public static (Color[] pixels, int width, int height) DecodePngPremultiplied(byte[] pngBytes)
    {
        // Use Texture2D-compatible decoding via StbImageSharp or System.Drawing
        // MonoGame's Texture2D.FromStream uses STB internally — we decode manually
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

    /// <summary>
    /// Create a Texture2D from pre-decoded pixel data. Must be called on the main/GPU thread.
    /// </summary>
    public static Texture2D CreateTextureFromPixels(GraphicsDevice device, Color[] pixels, int width, int height)
    {
        var tex = new Texture2D(device, width, height);
        tex.SetData(pixels);
        return tex;
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
