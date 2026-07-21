using System;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Core;
using Necroking.Data.Registries;

namespace Necroking.Render;

public class Flipbook
{
    /// <summary>Parse a flipbook grid token ("&lt;cols&gt;x&lt;rows&gt;", e.g.
    /// "FX_TX_Fire_Fireloop_01_4x4.png" → 4x4) out of a texture filename.
    /// Digit runs are capped at 2 and values at 64 so resolution suffixes
    /// like "_512x512" don't read as a grid; a lone "1x1" is rejected since
    /// it describes a plain image. When several tokens appear the LAST one
    /// wins (the grid conventionally trails the name).</summary>
    public static bool TryParseGridFromFileName(string path, out int cols, out int rows)
    {
        cols = 0; rows = 0;
        if (string.IsNullOrEmpty(path)) return false;
        string name = Path.GetFileNameWithoutExtension(path);
        // IgnoreCase: "3X4" tokens are as valid as "3x4" (exporter naming varies).
        var matches = System.Text.RegularExpressions.Regex.Matches(
            name, @"(?<![0-9])([0-9]{1,2})x([0-9]{1,2})(?![0-9])",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        for (int i = matches.Count - 1; i >= 0; i--)
        {
            int c = int.Parse(matches[i].Groups[1].Value);
            int r = int.Parse(matches[i].Groups[2].Value);
            if (c < 1 || r < 1 || c > 64 || r > 64 || c * r < 2) continue;
            cols = c; rows = r;
            return true;
        }
        return false;
    }

    public Texture2D? Texture { get; private set; }
    public int Cols { get; private set; } = 1;
    public int Rows { get; private set; } = 1;
    public int TotalFrames { get; private set; }
    public float FPS { get; private set; } = 30f;
    public bool IsLoaded { get; private set; }

    /// <summary>True for .exr sheets: Texture is HalfVector4 with LINEAR
    /// premultiplied HDR data. Draw sites must switch to a LinearTexture=1
    /// material (Materials.HdrTexAdditive/HdrTexAlpha) — the plain sprite path
    /// renders it washed out — and must NOT GetData&lt;Color&gt; it.</summary>
    public bool IsHdr { get; private set; }

    public bool Load(GraphicsDevice device, string path, int cols, int rows, float fps = 30f)
    {
        string resolved = Path.IsPathRooted(path) ? path : Core.GamePaths.Resolve(path);
        if (!File.Exists(resolved)) return false;

        if (Path.GetExtension(resolved).ToLowerInvariant() == ".exr")
        {
            // HDR path: keep the full float range for the bloom pipeline.
            Texture = ExrTgaTextures.LoadExrHdr(device, resolved);
            IsHdr = true;
        }
        else
        {
            // Path-based overload: routes .tga (and any future format) too.
            Texture = TextureUtil.LoadPremultiplied(device, resolved);
            IsHdr = false;
        }
        if (Texture == null) return false;

        Cols = cols;
        Rows = rows;
        TotalFrames = cols * rows;
        FPS = fps;
        IsLoaded = true;
        return true;
    }

    public bool LoadFromDef(GraphicsDevice device, FlipbookDef def)
    {
        return Load(device, def.Path, def.Cols, def.Rows, def.DefaultFPS);
    }

    public Rectangle GetFrameRect(int frameIndex)
    {
        if (!IsLoaded || TotalFrames <= 0 || Texture == null) return Rectangle.Empty;

        frameIndex %= TotalFrames;
        if (frameIndex < 0) frameIndex += TotalFrames;

        int col = frameIndex % Cols;
        int row = frameIndex / Cols;

        int frameW = Texture.Width / Cols;
        int frameH = Texture.Height / Rows;

        return new Rectangle(col * frameW, row * frameH, frameW, frameH);
    }

    public int GetFrameAtTime(float time)
    {
        if (!IsLoaded || TotalFrames <= 0 || FPS <= 0f) return 0;
        int frame = (int)(time * FPS);
        return frame % TotalFrames;
    }

    /// <summary>Map a normalized [0, 1] timeline onto the flipbook's frames,
    /// playing the animation exactly ONCE — t=0 returns frame 0, t=1
    /// (and beyond) returns the last frame. Callers that want a fixed-
    /// length animation matched to some external duration (a particle
    /// lifetime, a projectile flight time, an airborne arc) compute
    /// <c>t = age / duration</c> and pass it here. Use this instead of
    /// <see cref="GetFrameAtTime"/> when the animation should play
    /// once and stop, not loop.</summary>
    public int GetFrameAtNormalizedTime(float t)
    {
        if (!IsLoaded || TotalFrames <= 0) return 0;
        if (t <= 0f) return 0;
        if (t >= 1f) return TotalFrames - 1;
        int frame = (int)(t * TotalFrames);
        if (frame >= TotalFrames) frame = TotalFrames - 1;
        return frame;
    }

    public void Unload()
    {
        Texture?.Dispose();
        Texture = null;
        IsLoaded = false;
    }
}

public struct BezierCurve
{
    public float P0, P1, P2, P3;

    public BezierCurve(float p0, float p1, float p2, float p3)
    {
        P0 = p0; P1 = p1; P2 = p2; P3 = p3;
    }

    public float Evaluate(float t)
    {
        float u = 1f - t;
        return u * u * u * P0
             + 3f * u * u * t * P1
             + 3f * u * t * t * P2
             + t * t * t * P3;
    }
}
