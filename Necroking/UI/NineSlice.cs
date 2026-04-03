using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Necroking.UI;

public class NineSliceDef
{
    public string Id { get; set; } = "";
    public string TexturePath { get; set; } = "";
    public int BorderLeft { get; set; }
    public int BorderRight { get; set; }
    public int BorderTop { get; set; }
    public int BorderBottom { get; set; }
    public bool TileEdges { get; set; }
}

public class NineSlice
{
    public Texture2D? Texture { get; private set; }
    public int BorderLeft { get; private set; }
    public int BorderRight { get; private set; }
    public int BorderTop { get; private set; }
    public int BorderBottom { get; private set; }
    public bool TileEdges { get; private set; }

    public bool Load(GraphicsDevice device, NineSliceDef def)
    {
        string resolved = Necroking.Core.GamePaths.Resolve(def.TexturePath);
        if (!File.Exists(resolved)) return false;
        Texture = Necroking.Render.TextureUtil.LoadPremultiplied(device, resolved);
        if (Texture == null) return false;
        BorderLeft = def.BorderLeft;
        BorderRight = def.BorderRight;
        BorderTop = def.BorderTop;
        BorderBottom = def.BorderBottom;
        TileEdges = def.TileEdges;
        return true;
    }

    /// <summary>Initialize from an already-loaded texture (for harmonized copies).</summary>
    public void LoadFromTexture(Texture2D texture, int borderL, int borderR, int borderT, int borderB, bool tileEdges)
    {
        Texture = texture;
        BorderLeft = borderL;
        BorderRight = borderR;
        BorderTop = borderT;
        BorderBottom = borderB;
        TileEdges = tileEdges;
    }

    public void Draw(SpriteBatch batch, Rectangle dest, Color? tint = null, float scale = 1f)
    {
        if (Texture == null) return;
        var c = tint ?? Color.White;
        int tw = Texture.Width;
        int th = Texture.Height;
        int bl = (int)(BorderLeft * scale);
        int br = (int)(BorderRight * scale);
        int bt = (int)(BorderTop * scale);
        int bb = (int)(BorderBottom * scale);

        // Source rectangles (9 regions)
        var srcTL = new Rectangle(0, 0, BorderLeft, BorderTop);
        var srcTR = new Rectangle(tw - BorderRight, 0, BorderRight, BorderTop);
        var srcBL = new Rectangle(0, th - BorderBottom, BorderLeft, BorderBottom);
        var srcBR = new Rectangle(tw - BorderRight, th - BorderBottom, BorderRight, BorderBottom);
        var srcT = new Rectangle(BorderLeft, 0, tw - BorderLeft - BorderRight, BorderTop);
        var srcB = new Rectangle(BorderLeft, th - BorderBottom, tw - BorderLeft - BorderRight, BorderBottom);
        var srcL = new Rectangle(0, BorderTop, BorderLeft, th - BorderTop - BorderBottom);
        var srcR = new Rectangle(tw - BorderRight, BorderTop, BorderRight, th - BorderTop - BorderBottom);
        var srcC = new Rectangle(BorderLeft, BorderTop, tw - BorderLeft - BorderRight, th - BorderTop - BorderBottom);

        // Destination rectangles
        int midW = dest.Width - bl - br;
        int midH = dest.Height - bt - bb;

        // Corners
        batch.Draw(Texture, new Rectangle(dest.X, dest.Y, bl, bt), srcTL, c);
        batch.Draw(Texture, new Rectangle(dest.X + dest.Width - br, dest.Y, br, bt), srcTR, c);
        batch.Draw(Texture, new Rectangle(dest.X, dest.Y + dest.Height - bb, bl, bb), srcBL, c);
        batch.Draw(Texture, new Rectangle(dest.X + dest.Width - br, dest.Y + dest.Height - bb, br, bb), srcBR, c);

        // Edges
        if (midW > 0)
        {
            batch.Draw(Texture, new Rectangle(dest.X + bl, dest.Y, midW, bt), srcT, c);
            batch.Draw(Texture, new Rectangle(dest.X + bl, dest.Y + dest.Height - bb, midW, bb), srcB, c);
        }
        if (midH > 0)
        {
            batch.Draw(Texture, new Rectangle(dest.X, dest.Y + bt, bl, midH), srcL, c);
            batch.Draw(Texture, new Rectangle(dest.X + dest.Width - br, dest.Y + bt, br, midH), srcR, c);
        }

        // Center
        if (midW > 0 && midH > 0)
            batch.Draw(Texture, new Rectangle(dest.X + bl, dest.Y + bt, midW, midH), srcC, c);
    }

    public void Unload()
    {
        Texture?.Dispose();
        Texture = null;
    }
}
