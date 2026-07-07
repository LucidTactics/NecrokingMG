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

    /// <summary>V coord (0=top of frame, 1=bottom) of the topmost non-transparent
    /// row inside the frame. Computed by <see cref="SpriteAtlas.ComputeFrameBoundingBoxes"/>
    /// after the texture is attached. Used to drive the wading waterline so a
    /// quadruped with empty space above the body still gets the cut at the
    /// right place. Defaults to 0 (body fills frame) — safe fallback when
    /// the scan hasn't run yet.</summary>
    public float BodyTopV;
    /// <summary>V coord of the bottommost non-transparent row inside the frame.
    /// Defaults to 1 (body fills frame).</summary>
    public float BodyBottomV;
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

    /// <summary>Pixel-derived stride lengths per gait (Walk/Jog/Run) measured by
    /// <see cref="StrideCalibration"/> at atlas load. Used by LocomotionScaling
    /// to produce a feet-locked playback rate. Null until calibration runs —
    /// runtime falls back to legacy scaling when null. One calibration per
    /// sprite (not per unit-def), so multiple unit-defs that share a sprite
    /// share the calibration; per-def world-height conversion happens at
    /// runtime via <see cref="StrideCalibration.ResolveAnimVel"/>.</summary>
    public StrideCalibration.UnitCalibration? Calibration;

    public AnimationData? GetAnim(string name) =>
        Animations.TryGetValue(name, out var a) ? a : null;

    /// <summary>Per-sprite-angle reference body bbox, averaged across the
    /// Idle animation's frames at that angle. Used by the wading effect
    /// to keep the waterline stable across animation frames — without
    /// this, each frame's pixel-derived bbox (legs extending, body
    /// bobbing) would shift the waterline V from frame to frame, making
    /// the visible water level appear to fluctuate on the unit's body.
    /// Lazily computed on first access. Idle is preferred as the
    /// reference because it represents the unit's neutral pose; if the
    /// sprite has no Idle anim, falls back to the first available.</summary>
    private Dictionary<int, (float topV, float bottomV)>? _refBodyBboxByAngle;

    /// <summary>Reference body bbox for a sprite angle (averaged across
    /// the reference animation's frames at that angle). Falls back to
    /// the fallback parameter if no reference data is available.</summary>
    public (float topV, float bottomV) GetReferenceBodyBbox(int spriteAngle, float fallbackTopV, float fallbackBottomV)
    {
        if (_refBodyBboxByAngle == null) ComputeReferenceBodyBboxes();
        if (_refBodyBboxByAngle!.TryGetValue(spriteAngle, out var bbox))
            return bbox;
        // No data for this exact angle — fall back to whatever we have,
        // preferring the first registered angle (usually the cardinal).
        foreach (var kv in _refBodyBboxByAngle)
            return kv.Value;
        return (fallbackTopV, fallbackBottomV);
    }

    private void ComputeReferenceBodyBboxes()
    {
        _refBodyBboxByAngle = new Dictionary<int, (float, float)>();
        // Prefer Idle as the reference. If absent, walk anim is a decent
        // alternative; otherwise just take whatever exists.
        AnimationData? refAnim = GetAnim("Idle") ?? GetAnim("Walk");
        if (refAnim == null)
        {
            foreach (var a in Animations.Values) { refAnim = a; break; }
            if (refAnim == null) return;
        }
        foreach (var (angle, kfs) in refAnim.AngleFrames)
        {
            if (kfs.Count == 0) continue;
            float topSum = 0f, bottomSum = 0f;
            int n = 0;
            foreach (var kf in kfs)
            {
                topSum += kf.Frame.BodyTopV;
                bottomSum += kf.Frame.BodyBottomV;
                n++;
            }
            if (n > 0) _refBodyBboxByAngle[angle] = (topSum / n, bottomSum / n);
        }
    }
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

        // Reimplemented on the split-phase primitives so the base-load finalize
        // sequence (clear/add lists, FixupYOrigin, RescaleAllFrames,
        // ComputeFrameBoundingBoxes, IsLoaded) lives in exactly one place —
        // SetTextureAndFinalize. Parse runs before texture creation, which is
        // equivalent to the old load-then-parse order: ParseMeta only mutates
        // _units, while the texture-tracking lists are owned solely by
        // SetTextureAndFinalize, so the two touch disjoint state. A parse failure
        // returns false without having created (or leaked) a GPU texture.
        if (!ParseMetaOnly(resolvedMeta)) return false;

        using var stream = File.OpenRead(resolvedPng);
        var tex = TextureUtil.LoadPremultiplied(device, stream);
        if (tex == null) return false;

        SetTextureAndFinalize(tex, tex.Width, tex.Height);
        return true;
    }

    /// <summary>Attach an overflow sheet (base atlas name + __N suffix). Adds its
    /// texture at the next TextureIndex and merges its spritemeta into the
    /// existing <see cref="Units"/> dictionary — new units/animations/frames get
    /// the new TextureIndex. Call AFTER <see cref="Load"/> or equivalent.
    ///
    /// NOT reimplemented on ParseExtensionMeta + AttachExtensionTexture (unlike
    /// <see cref="Load"/> on ParseMetaOnly + SetTextureAndFinalize): that split pair
    /// is structural variance, not duplication. ParseExtensionMeta pushes placeholder
    /// width/height/pending entries whose only job is to advance the TextureIndex
    /// counter; the parallel-decode pipeline discards them via SetTextureAndFinalize's
    /// list-clear before AttachExtensionTexture appends the real values. Calling the
    /// two back-to-back here (texture already in hand, no clear between) would
    /// double-add to the tracking lists and misalign the per-index heights, breaking
    /// FixupYOrigin. The immediate-texture path below is the correct single-call form.</summary>
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
        ComputeFrameBoundingBoxes(newIdx);
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
        ComputeFrameBoundingBoxes(0);
        IsLoaded = true;
    }

    /// <summary>Attach a pre-parsed extension: caller has already parsed the extension
    /// spritemeta with <see cref="ParseExtensionMeta"/> and created its GPU texture.
    /// Used by the parallel-decode pipeline in Game1 so extensions share the base-load
    /// fast path without blocking on a separate <see cref="LoadExtension"/> call.</summary>
    public void AttachExtensionTexture(Texture2D texture, int width, int height)
    {
        int newIdx = _textures.Count;
        _textures.Add(texture);
        _originalWidths.Add(width);
        _originalHeights.Add(height);
        _yFixupPending.Add(true);
        FixupYOrigin();
        ComputeFrameBoundingBoxes(newIdx);
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

    /// <summary>Scan the texture at <paramref name="textureIndex"/> and fill
    /// in <see cref="SpriteFrame.BodyTopV"/> / <see cref="SpriteFrame.BodyBottomV"/>
    /// for every frame on that texture. Body bbox is the V range of the
    /// topmost/bottommost rows containing any pixel with alpha above a small
    /// threshold. Used by the wading waterline so quadrupeds (which have a
    /// lot of empty space above the body in the frame) get the cut at the
    /// right place. Must run on the main thread (calls GetData).
    ///
    /// Idempotent — safe to call multiple times for the same index.</summary>
    public void ComputeFrameBoundingBoxes(int textureIndex)
    {
        if (textureIndex < 0 || textureIndex >= _textures.Count) return;
        var tex = _textures[textureIndex];
        if (tex == null) return;
        int W = tex.Width;
        int H = tex.Height;
        if (W <= 0 || H <= 0) return;

        var data = new Color[W * H];
        try { tex.GetData(data); }
        catch { return; }

        const byte AlphaThreshold = 16; // ignore near-transparent edges / antialias halo

        foreach (var udata in _units.Values)
            foreach (var adata in udata.Animations.Values)
                foreach (var (_, kfs) in adata.AngleFrames)
                    for (int i = 0; i < kfs.Count; i++)
                    {
                        var kf = kfs[i];
                        if (kf.Frame.TextureIndex != textureIndex) continue;
                        var rect = kf.Frame.Rect;
                        if (rect.Width <= 0 || rect.Height <= 0) continue;

                        // Clip rect to texture bounds defensively.
                        int x0 = Math.Max(0, rect.Left);
                        int y0 = Math.Max(0, rect.Top);
                        int x1 = Math.Min(W, rect.Right);
                        int y1 = Math.Min(H, rect.Bottom);
                        if (x0 >= x1 || y0 >= y1) continue;

                        int topPx = -1;
                        int bottomPx = -1;
                        for (int py = y0; py < y1; py++)
                        {
                            int rowBase = py * W;
                            bool rowHasVisible = false;
                            for (int px = x0; px < x1; px++)
                            {
                                if (data[rowBase + px].A > AlphaThreshold)
                                {
                                    rowHasVisible = true;
                                    break;
                                }
                            }
                            if (rowHasVisible)
                            {
                                if (topPx == -1) topPx = py;
                                bottomPx = py;
                            }
                        }

                        if (topPx == -1)
                        {
                            // Fully transparent frame — keep the default full-frame bbox.
                            kf.Frame.BodyTopV = 0f;
                            kf.Frame.BodyBottomV = 1f;
                        }
                        else
                        {
                            // Convert pixel rows to local V in [0..1] where 0=top of frame.
                            float h = rect.Height;
                            kf.Frame.BodyTopV = (topPx - rect.Top) / h;
                            // +1 makes the bottom edge inclusive of the last row.
                            kf.Frame.BodyBottomV = (bottomPx - rect.Top + 1) / h;
                        }
                        kfs[i] = kf;
                    }
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
                TextureIndex = textureIndex,
                // Body bbox defaults to the full frame so anything reading it
                // before the bbox scan runs gets a sane fallback (whole sprite
                // is body). ComputeFrameBoundingBoxes overwrites these.
                BodyTopV = 0f,
                BodyBottomV = 1f,
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
