using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Necroking.Render;

public struct SpriteFrame
{
    public Rectangle Rect;
    public float PivotX, PivotY; // normalized anchor (0-1)
}

public struct Keyframe
{
    public int Time; // tick timestamp
    public SpriteFrame Frame;
}

public class AnimationData
{
    public string Name = "";
    // angle → sorted keyframes
    public Dictionary<int, List<Keyframe>> AngleFrames = new();

    public int TotalTicks()
    {
        int maxT = 0;
        foreach (var (_, kfs) in AngleFrames)
            if (kfs.Count > 0 && kfs[^1].Time > maxT)
                maxT = kfs[^1].Time;
        return maxT;
    }

    public List<Keyframe>? GetAngle(int angle) =>
        AngleFrames.TryGetValue(angle, out var kfs) ? kfs : null;
}

public class UnitSpriteData
{
    public string UnitName = "";
    public Dictionary<string, AnimationData> Animations = new();

    public AnimationData? GetAnim(string name) =>
        Animations.TryGetValue(name, out var a) ? a : null;
}

public class SpriteAtlas
{
    private Texture2D? _texture;
    private readonly Dictionary<string, UnitSpriteData> _units = new();
    private int _originalWidth, _originalHeight;
    private float _scaleX = 1f, _scaleY = 1f;
    public bool IsLoaded { get; private set; }

    public Texture2D? Texture => _texture;
    public IReadOnlyDictionary<string, UnitSpriteData> Units => _units;
    public int OriginalWidth => _originalWidth;
    public int OriginalHeight => _originalHeight;

    public UnitSpriteData? GetUnit(string name) =>
        _units.TryGetValue(name, out var u) ? u : null;

    /// <summary>Full load: reads PNG + meta from disk, creates GPU texture. Single-threaded.</summary>
    public bool Load(GraphicsDevice device, string pngPath, string metaPath)
    {
        if (!File.Exists(pngPath) || !File.Exists(metaPath)) return false;

        // Load texture
        using var stream = File.OpenRead(pngPath);
        _texture = TextureUtil.LoadPremultiplied(device, stream);
        if (_texture == null) return false;

        _originalWidth = _texture.Width;
        _originalHeight = _texture.Height;
        _scaleX = 1f;
        _scaleY = 1f;

        // Parse spritemeta
        if (!ParseMeta(metaPath))
        {
            _texture.Dispose();
            _texture = null;
            return false;
        }

        FixupYOrigin();
        RescaleAllFrames();
        IsLoaded = true;
        return true;
    }

    /// <summary>Parse metadata only (thread-safe, no GPU). Call from background thread.</summary>
    public bool ParseMetaOnly(string metaPath)
    {
        return ParseMeta(metaPath);
    }

    /// <summary>Create GPU texture from pre-read PNG bytes. Call on main thread after ParseMetaOnly.</summary>
    public bool LoadFromStream(GraphicsDevice device, Stream pngStream)
    {
        _texture = TextureUtil.LoadPremultiplied(device, pngStream);
        if (_texture == null) return false;

        _originalWidth = _texture.Width;
        _originalHeight = _texture.Height;
        _scaleX = 1f;
        _scaleY = 1f;

        FixupYOrigin();
        RescaleAllFrames();
        IsLoaded = true;
        return true;
    }

    /// <summary>Set a pre-created texture and finalize the atlas. Call on main thread after ParseMetaOnly + GPU texture creation.</summary>
    public void SetTextureAndFinalize(Texture2D texture, int width, int height)
    {
        _texture = texture;
        _originalWidth = width;
        _originalHeight = height;
        _scaleX = 1f;
        _scaleY = 1f;
        FixupYOrigin();
        RescaleAllFrames();
        IsLoaded = true;
    }

    /// <summary>Flip Y coordinates from spritemeta bottom-left to MonoGame top-left.</summary>
    private void FixupYOrigin()
    {
        if (_originalHeight <= 0) return;
        foreach (var udata in _units.Values)
            foreach (var adata in udata.Animations.Values)
                foreach (var (_, kfs) in adata.AngleFrames)
                    for (int i = 0; i < kfs.Count; i++)
                    {
                        var kf = kfs[i];
                        kf.Frame.Rect = new Rectangle(
                            kf.Frame.Rect.X,
                            _originalHeight - kf.Frame.Rect.Y - kf.Frame.Rect.Height,
                            kf.Frame.Rect.Width,
                            kf.Frame.Rect.Height);
                        kfs[i] = kf;
                    }
    }

    public void Unload()
    {
        _texture?.Dispose();
        _texture = null;
        _units.Clear();
        IsLoaded = false;
    }

    private bool ParseMeta(string metaPath)
    {
        _units.Clear();

        foreach (var rawLine in File.ReadLines(metaPath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            var tabParts = line.Split('\t');
            if (tabParts.Length < 3) continue;

            var nameParts = tabParts[0].Split('.');
            if (nameParts.Length < 5) continue;

            string unitName = nameParts[0];
            string animName = nameParts[1];
            int keyframeTime = int.Parse(nameParts[2]);
            int angle = int.Parse(nameParts[4]);

            var rectParts = tabParts[1].Split(',');
            if (rectParts.Length < 4) continue;

            var frame = new SpriteFrame
            {
                Rect = new Rectangle(
                    int.Parse(rectParts[0]), int.Parse(rectParts[1]),
                    int.Parse(rectParts[2]), int.Parse(rectParts[3]))
            };

            var pivotParts = tabParts[2].Split(',');
            if (pivotParts.Length >= 2)
            {
                frame.PivotX = float.Parse(pivotParts[0]);
                frame.PivotY = float.Parse(pivotParts[1]);
            }

            if (!_units.TryGetValue(unitName, out var udata))
            {
                udata = new UnitSpriteData { UnitName = unitName };
                _units[unitName] = udata;
            }

            if (!udata.Animations.TryGetValue(animName, out var adata))
            {
                adata = new AnimationData { Name = animName };
                udata.Animations[animName] = adata;
            }

            if (!adata.AngleFrames.TryGetValue(angle, out var kfs))
            {
                kfs = new List<Keyframe>();
                adata.AngleFrames[angle] = kfs;
            }

            kfs.Add(new Keyframe { Time = keyframeTime, Frame = frame });
        }

        // Sort keyframes by time
        foreach (var udata in _units.Values)
            foreach (var adata in udata.Animations.Values)
                foreach (var (_, kfs) in adata.AngleFrames)
                    kfs.Sort((a, b) => a.Time.CompareTo(b.Time));

        return _units.Count > 0;
    }

    private void RescaleAllFrames()
    {
        if (Math.Abs(_scaleX - 1f) < 0.001f && Math.Abs(_scaleY - 1f) < 0.001f) return;

        foreach (var udata in _units.Values)
            foreach (var adata in udata.Animations.Values)
                foreach (var (_, kfs) in adata.AngleFrames)
                    for (int i = 0; i < kfs.Count; i++)
                    {
                        var kf = kfs[i];
                        kf.Frame.Rect = new Rectangle(
                            (int)(kf.Frame.Rect.X * _scaleX),
                            (int)(kf.Frame.Rect.Y * _scaleY),
                            (int)(kf.Frame.Rect.Width * _scaleX),
                            (int)(kf.Frame.Rect.Height * _scaleY));
                        kfs[i] = kf;
                    }
    }
}
