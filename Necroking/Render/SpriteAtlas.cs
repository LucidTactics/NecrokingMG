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
    /// <summary>Index into <see cref="SpriteAtlas.Textures"/> identifying which
    /// backing PNG this frame's pixels live in. 0 = base sheet, 1+ = overflow
    /// sheets (atlas name + "__N" suffix). Lets a single logical atlas span
    /// multiple PNGs without callers needing to know.</summary>
    public int TextureIndex;
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
    public Dictionary<string, AnimationData> Animations = new(StringComparer.OrdinalIgnoreCase);

    public AnimationData? GetAnim(string name) =>
        Animations.TryGetValue(name, out var a) ? a : null;
}

public class SpriteAtlas
{
    private readonly List<Texture2D> _textures = new();
    private readonly Dictionary<string, UnitSpriteData> _units = new();
    // Original (pre-rescale) per-texture dimensions — used by FixupYOrigin so each
    // extension sheet's Y-flip uses ITS OWN height, not the base sheet's.
    private readonly List<int> _originalWidths = new();
    private readonly List<int> _originalHeights = new();
    // Pending-fixup marker per texture index. FixupYOrigin iterates frames and only
    // flips ones whose TextureIndex is still marked pending, so attaching an
    // extension after base load doesn't re-flip already-corrected frames.
    private readonly List<bool> _yFixupPending = new();
    private float _scaleX = 1f, _scaleY = 1f;
    public bool IsLoaded { get; private set; }

    /// <summary>Legacy accessor — returns texture 0 (base sheet). Call sites that
    /// need to draw a specific frame's pixels must use <see cref="GetTextureForFrame"/>.
    /// This still works for atlases that fit on one sheet.</summary>
    public Texture2D? Texture => _textures.Count > 0 ? _textures[0] : null;

    /// <summary>All backing textures (base + any __N overflow sheets), indexed by
    /// SpriteFrame.TextureIndex. Use for shadow pass / bulk iteration.</summary>
    public IReadOnlyList<Texture2D> Textures => _textures;

    public IReadOnlyDictionary<string, UnitSpriteData> Units => _units;

    /// <summary>Base-sheet dimensions (texture 0). Kept as singular properties for
    /// backward compat; use <see cref="GetTextureForFrame"/>.Width/.Height when you
    /// need the specific extension sheet's size.</summary>
    public int OriginalWidth => _originalWidths.Count > 0 ? _originalWidths[0] : 0;
    public int OriginalHeight => _originalHeights.Count > 0 ? _originalHeights[0] : 0;

    public UnitSpriteData? GetUnit(string name) =>
        _units.TryGetValue(name, out var u) ? u : null;

    /// <summary>Return the Texture2D that backs the given frame's pixels (respects
    /// multi-sheet __N overflow).</summary>
    public Texture2D? GetTextureForFrame(in SpriteFrame frame) =>
        (frame.TextureIndex >= 0 && frame.TextureIndex < _textures.Count)
            ? _textures[frame.TextureIndex] : null;

    /// <summary>Full load: reads PNG + meta from disk, creates GPU texture. Single-threaded.
    /// Loads as the base sheet (TextureIndex 0). Use <see cref="LoadExtension"/> afterwards
    /// to attach __N overflow sheets.</summary>
    public bool Load(GraphicsDevice device, string pngPath, string metaPath)
    {
        string resolvedPng = Path.IsPathRooted(pngPath) ? pngPath : Core.GamePaths.Resolve(pngPath);
        string resolvedMeta = Path.IsPathRooted(metaPath) ? metaPath : Core.GamePaths.Resolve(metaPath);
        if (!File.Exists(resolvedPng) || !File.Exists(resolvedMeta)) return false;

        // Load texture
        using var stream = File.OpenRead(resolvedPng);
        var tex = TextureUtil.LoadPremultiplied(device, stream);
        if (tex == null) return false;

        _textures.Clear();
        _originalWidths.Clear();
        _originalHeights.Clear();
        _yFixupPending.Clear();
        _textures.Add(tex);
        _originalWidths.Add(tex.Width);
        _originalHeights.Add(tex.Height);
        _yFixupPending.Add(true);
        _scaleX = 1f;
        _scaleY = 1f;

        // Parse spritemeta (all frames default to TextureIndex 0).
        if (!ParseMeta(metaPath, textureIndex: 0))
        {
            foreach (var t in _textures) t.Dispose();
            _textures.Clear();
            return false;
        }

        FixupYOrigin();
        RescaleAllFrames();
        IsLoaded = true;
        return true;
    }

    /// <summary>Attach an overflow sheet (base atlas name + __N suffix). Adds its
    /// texture at the next TextureIndex and merges its spritemeta into the
    /// existing <see cref="Units"/> dictionary — new units/animations/frames get
    /// the new TextureIndex. Call AFTER <see cref="Load"/> or equivalent.</summary>
    public bool LoadExtension(GraphicsDevice device, string pngPath, string metaPath)
    {
        if (!IsLoaded) return false; // base must be loaded first
        string resolvedPng = Path.IsPathRooted(pngPath) ? pngPath : Core.GamePaths.Resolve(pngPath);
        string resolvedMeta = Path.IsPathRooted(metaPath) ? metaPath : Core.GamePaths.Resolve(metaPath);
        if (!File.Exists(resolvedPng) || !File.Exists(resolvedMeta)) return false;

        using var stream = File.OpenRead(resolvedPng);
        var tex = TextureUtil.LoadPremultiplied(device, stream);
        if (tex == null) return false;

        int newIdx = _textures.Count;
        _textures.Add(tex);
        _originalWidths.Add(tex.Width);
        _originalHeights.Add(tex.Height);
        _yFixupPending.Add(true);

        if (!ParseMeta(metaPath, textureIndex: newIdx))
        {
            // parse failed — back out the texture add to stay consistent
            _textures.RemoveAt(newIdx);
            _originalWidths.RemoveAt(newIdx);
            _originalHeights.RemoveAt(newIdx);
            _yFixupPending.RemoveAt(newIdx);
            tex.Dispose();
            return false;
        }

        FixupYOrigin();
        // RescaleAllFrames is a no-op when _scaleX/Y = 1, which is the normal
        // runtime path. Extension rescale would need per-texture scales; skip.
        return true;
    }

    /// <summary>Parse metadata only (thread-safe, no GPU). Call from background thread.
    /// All frames parsed here carry TextureIndex 0 — the base sheet. Extension sheets
    /// go through <see cref="LoadExtension"/> on the main thread.</summary>
    public bool ParseMetaOnly(string metaPath)
    {
        string resolved = Path.IsPathRooted(metaPath) ? metaPath : Core.GamePaths.Resolve(metaPath);
        return ParseMeta(resolved, textureIndex: 0);
    }

    /// <summary>Set a pre-created base texture and finalize the atlas. Call on main
    /// thread after ParseMetaOnly + GPU texture creation.</summary>
    public void SetTextureAndFinalize(Texture2D texture, int width, int height)
    {
        _textures.Clear();
        _originalWidths.Clear();
        _originalHeights.Clear();
        _yFixupPending.Clear();
        _textures.Add(texture);
        _originalWidths.Add(width);
        _originalHeights.Add(height);
        _yFixupPending.Add(true);
        _scaleX = 1f;
        _scaleY = 1f;
        FixupYOrigin();
        RescaleAllFrames();
        IsLoaded = true;
    }

    /// <summary>Attach a pre-parsed extension: caller has already parsed the extension
    /// spritemeta with <see cref="ParseExtensionMeta"/> and created its GPU texture.
    /// Used by the parallel-decode pipeline in Game1 so extensions share the base-load
    /// fast path without blocking on a separate <see cref="LoadExtension"/> call.</summary>
    public void AttachExtensionTexture(Texture2D texture, int width, int height)
    {
        _textures.Add(texture);
        _originalWidths.Add(width);
        _originalHeights.Add(height);
        _yFixupPending.Add(true);
        FixupYOrigin();
    }

    /// <summary>Parse an extension spritemeta and tag its frames with the next
    /// pending TextureIndex. Must be followed by <see cref="AttachExtensionTexture"/>
    /// with the matching PNG. Thread-safe (no GPU).</summary>
    public bool ParseExtensionMeta(string metaPath)
    {
        // Next TextureIndex = current texture count + any already-parsed-but-unattached
        // extensions (tracked via _yFixupPending length).
        int nextIdx = _yFixupPending.Count;
        _yFixupPending.Add(true);
        // Growing the widths/heights lists with a placeholder keeps indices aligned
        // until AttachExtensionTexture writes the real values.
        _originalWidths.Add(0);
        _originalHeights.Add(0);
        string resolved = Path.IsPathRooted(metaPath) ? metaPath : Core.GamePaths.Resolve(metaPath);
        if (!ParseMeta(resolved, textureIndex: nextIdx, clearExisting: false))
        {
            // rollback marker so the next extension parses at the same slot
            _yFixupPending.RemoveAt(nextIdx);
            _originalWidths.RemoveAt(nextIdx);
            _originalHeights.RemoveAt(nextIdx);
            return false;
        }
        return true;
    }

    /// <summary>Flip Y coordinates from spritemeta bottom-left to MonoGame top-left.
    /// Only flips frames whose TextureIndex is still marked pending, so re-running
    /// after attaching an extension doesn't re-flip already-corrected frames.</summary>
    private void FixupYOrigin()
    {
        foreach (var udata in _units.Values)
            foreach (var adata in udata.Animations.Values)
                foreach (var (_, kfs) in adata.AngleFrames)
                    for (int i = 0; i < kfs.Count; i++)
                    {
                        var kf = kfs[i];
                        int ti = kf.Frame.TextureIndex;
                        if (ti < 0 || ti >= _yFixupPending.Count) continue;
                        if (!_yFixupPending[ti]) continue;
                        int h = _originalHeights[ti];
                        if (h <= 0) continue; // texture not yet attached — wait for next pass
                        kf.Frame.Rect = new Rectangle(
                            kf.Frame.Rect.X,
                            h - kf.Frame.Rect.Y - kf.Frame.Rect.Height,
                            kf.Frame.Rect.Width,
                            kf.Frame.Rect.Height);
                        kfs[i] = kf;
                    }
        // Clear pending markers for every texture index whose dimensions are known
        // and whose frames just got flipped.
        for (int ti = 0; ti < _yFixupPending.Count; ti++)
            if (_yFixupPending[ti] && ti < _originalHeights.Count && _originalHeights[ti] > 0)
                _yFixupPending[ti] = false;
    }

    public void Unload()
    {
        foreach (var t in _textures) t.Dispose();
        _textures.Clear();
        _originalWidths.Clear();
        _originalHeights.Clear();
        _yFixupPending.Clear();
        _units.Clear();
        IsLoaded = false;
    }

    private bool ParseMeta(string metaPath, int textureIndex, bool clearExisting = true)
    {
        if (clearExisting) _units.Clear();

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
                    int.Parse(rectParts[2]), int.Parse(rectParts[3])),
                TextureIndex = textureIndex
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
