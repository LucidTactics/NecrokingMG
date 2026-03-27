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
