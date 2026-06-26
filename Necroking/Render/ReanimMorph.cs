using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Necroking.Render;

/// <summary>
/// Builds + caches the data the MorphSDF shader needs to morph a reanimating body from its death
/// pose to the standup-start pose via signed-distance-field interpolation (the "amoeba" gain/shed
/// pixels morph, vs a ghosty cross-dissolve). For a given (death frame, standup frame, flips) it
/// produces a single pivot-aligned canvas holding the two premultiplied color frames plus a packed
/// SDF texture (R = death SDF, G = standup SDF). Heavy to build (GetData read-back + a distance
/// transform), so results are cached by frame identity — typically one build per unit type/facing.
/// </summary>
internal class ReanimMorph
{
    public struct MorphData
    {
        public Texture2D? ColorA;   // death color, pivot-aligned in the canvas (premultiplied)
        public Texture2D? ColorB;   // standup-start color, same canvas
        public Texture2D? Sdf;      // R = death SDF, G = standup SDF (encoded)
        public int W, H;            // canvas size (px)
        public float PivotX, PivotY; // canvas anchor in px (maps to the corpse's screen pos / origin)
        public float MaxDist;       // SDF decode scale (px) — pass to the shader
        public bool Valid;
    }

    private const int Pad = 24;         // canvas padding so the morph can bulge/grow into it
    private const float MaxDistPx = 40f; // SDF encode/decode range (px)

    private readonly Dictionary<string, MorphData> _cache = new();

    public MorphData GetOrBuild(GraphicsDevice gd, SpriteAtlas atlas,
        in SpriteFrame death, bool deathFlip, in SpriteFrame standup, bool standupFlip)
    {
        string key = $"{death.TextureIndex}:{death.Rect.X},{death.Rect.Y},{death.Rect.Width},{death.Rect.Height},{deathFlip}|"
                   + $"{standup.TextureIndex}:{standup.Rect.X},{standup.Rect.Y},{standup.Rect.Width},{standup.Rect.Height},{standupFlip}";
        if (_cache.TryGetValue(key, out var cached)) return cached;
        var data = Build(gd, atlas, death, deathFlip, standup, standupFlip);
        _cache[key] = data;
        return data;
    }

    private MorphData Build(GraphicsDevice gd, SpriteAtlas atlas,
        in SpriteFrame death, bool deathFlip, in SpriteFrame standup, bool standupFlip)
    {
        var texD = atlas.GetTextureForFrame(death);
        var texU = atlas.GetTextureForFrame(standup);
        if (texD == null || texU == null) return default;

        int wD = death.Rect.Width, hD = death.Rect.Height;
        int wU = standup.Rect.Width, hU = standup.Rect.Height;
        if (wD <= 0 || hD <= 0 || wU <= 0 || hU <= 0) return default;
        var pixD = new Color[wD * hD];
        var pixU = new Color[wU * hU];
        texD.GetData(0, death.Rect, pixD, 0, pixD.Length);
        texU.GetData(0, standup.Rect, pixU, 0, pixU.Length);

        // Pivot in pixels (top-left origin), mirroring DrawSpriteFrame: pivotX flips with flipX,
        // pivotY is always 1 - PivotY (spritemeta uses a bottom-left origin).
        float pxD = (deathFlip ? (1f - death.PivotX) : death.PivotX) * wD;
        float pyD = (1f - death.PivotY) * hD;
        float pxU = (standupFlip ? (1f - standup.PivotX) : standup.PivotX) * wU;
        float pyU = (1f - standup.PivotY) * hU;

        // Common canvas: max extent from the pivot in each direction over both frames, + padding.
        float left = MathF.Max(pxD, pxU);
        float right = MathF.Max(wD - pxD, wU - pxU);
        float up = MathF.Max(pyD, pyU);
        float down = MathF.Max(hD - pyD, hU - pyU);
        int cw = (int)MathF.Ceiling(left + right) + 2 * Pad;
        int ch = (int)MathF.Ceiling(up + down) + 2 * Pad;
        float cpx = left + Pad;   // canvas pivot/anchor (px)
        float cpy = up + Pad;

        var colA = new Color[cw * ch];
        var colB = new Color[cw * ch];
        Place(pixD, wD, hD, deathFlip, colA, cw, ch, (int)MathF.Round(cpx - pxD), (int)MathF.Round(cpy - pyD));
        Place(pixU, wU, hU, standupFlip, colB, cw, ch, (int)MathF.Round(cpx - pxU), (int)MathF.Round(cpy - pyU));

        var sdfA = SignedDistance(colA, cw, ch);
        var sdfB = SignedDistance(colB, cw, ch);
        var sdfPacked = new Color[cw * ch];
        for (int i = 0; i < sdfPacked.Length; i++)
            sdfPacked[i] = new Color(EncodeSdf(sdfA[i]), EncodeSdf(sdfB[i]), (byte)0, (byte)255);

        var tA = new Texture2D(gd, cw, ch); tA.SetData(colA);
        var tB = new Texture2D(gd, cw, ch); tB.SetData(colB);
        var tS = new Texture2D(gd, cw, ch); tS.SetData(sdfPacked);

        return new MorphData
        {
            ColorA = tA, ColorB = tB, Sdf = tS,
            W = cw, H = ch, PivotX = cpx, PivotY = cpy, MaxDist = MaxDistPx, Valid = true,
        };
    }

    // Copy a frame's pixels into the canvas at (offX,offY), optionally horizontally flipped.
    private static void Place(Color[] src, int sw, int sh, bool flip, Color[] dst, int dw, int dh, int offX, int offY)
    {
        for (int y = 0; y < sh; y++)
        {
            int dyr = offY + y;
            if (dyr < 0 || dyr >= dh) continue;
            for (int x = 0; x < sw; x++)
            {
                int dxr = offX + x;
                if (dxr < 0 || dxr >= dw) continue;
                int sx = flip ? (sw - 1 - x) : x;
                dst[dyr * dw + dxr] = src[y * sw + sx];
            }
        }
    }

    private static byte EncodeSdf(float d)
    {
        float e = 0.5f + d / (2f * MaxDistPx);
        if (e < 0f) e = 0f; else if (e > 1f) e = 1f;
        return (byte)(e * 255f + 0.5f);
    }

    // Signed distance to the silhouette edge (alpha>=128 = inside), negative inside, in px.
    private static float[] SignedDistance(Color[] col, int w, int h)
    {
        int n = w * h;
        var inside = new bool[n];
        for (int i = 0; i < n; i++) inside[i] = col[i].A >= 128;
        var distOut = DistanceToFeature(inside, false, w, h); // distance to nearest OUTSIDE pixel
        var distIn = DistanceToFeature(inside, true, w, h);   // distance to nearest INSIDE pixel
        var sd = new float[n];
        for (int i = 0; i < n; i++) sd[i] = inside[i] ? -distOut[i] : distIn[i];
        return sd;
    }

    // 8SSEDT: distance from each cell to the nearest cell whose 'inside' equals featureValue.
    private static float[] DistanceToFeature(bool[] inside, bool featureValue, int w, int h)
    {
        const int BIG = 1 << 14;
        int n = w * h;
        var dx = new int[n];
        var dy = new int[n];
        for (int i = 0; i < n; i++)
        {
            if (inside[i] == featureValue) { dx[i] = 0; dy[i] = 0; }
            else { dx[i] = BIG; dy[i] = BIG; }
        }
        void Compare(int x, int y, int ox, int oy)
        {
            int nx = x + ox, ny = y + oy;
            if (nx < 0 || ny < 0 || nx >= w || ny >= h) return;
            int ni = ny * w + nx, i = y * w + x;
            int cdx = dx[ni] + ox, cdy = dy[ni] + oy;
            if ((long)cdx * cdx + (long)cdy * cdy < (long)dx[i] * dx[i] + (long)dy[i] * dy[i]) { dx[i] = cdx; dy[i] = cdy; }
        }
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++) { Compare(x, y, -1, 0); Compare(x, y, 0, -1); Compare(x, y, -1, -1); Compare(x, y, 1, -1); }
            for (int x = w - 1; x >= 0; x--) Compare(x, y, 1, 0);
        }
        for (int y = h - 1; y >= 0; y--)
        {
            for (int x = w - 1; x >= 0; x--) { Compare(x, y, 1, 0); Compare(x, y, 0, 1); Compare(x, y, -1, 1); Compare(x, y, 1, 1); }
            for (int x = 0; x < w; x++) Compare(x, y, -1, 0);
        }
        var d = new float[n];
        for (int i = 0; i < n; i++) d[i] = MathF.Sqrt((long)dx[i] * dx[i] + (long)dy[i] * dy[i]);
        return d;
    }

    public void Clear()
    {
        foreach (var m in _cache.Values)
        {
            m.ColorA?.Dispose(); m.ColorB?.Dispose(); m.Sdf?.Dispose();
        }
        _cache.Clear();
    }
}
