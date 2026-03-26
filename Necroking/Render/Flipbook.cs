using System;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Core;
using Necroking.Data.Registries;

namespace Necroking.Render;

public class Flipbook
{
    public Texture2D? Texture { get; private set; }
    public int Cols { get; private set; } = 1;
    public int Rows { get; private set; } = 1;
    public int TotalFrames { get; private set; }
    public float FPS { get; private set; } = 30f;
    public bool IsLoaded { get; private set; }

    public bool Load(GraphicsDevice device, string path, int cols, int rows, float fps = 30f)
    {
        if (!File.Exists(path)) return false;

        using var stream = File.OpenRead(path);
        Texture = Texture2D.FromStream(device, stream);
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
