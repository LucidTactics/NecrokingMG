using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;

namespace Necroking.Render;

/// <summary>
/// Per-sprite locomotion stride lengths extracted from the actual rendered pixels.
/// Three gaits (Walk/Jog/Run) get an independent stride measurement in pixels; at
/// runtime these are combined with the UnitDef's SpriteWorldHeight + the sprite's
/// average pixel height to produce a world-units-per-second "feet-lock velocity"
/// per gait. The runtime playback formula then becomes simply
/// <c>playback = velocity / animVelForGait</c>, which is continuous in velocity
/// and matches foot motion to ground motion (no skating).
///
/// Measurement algorithm (per gait, per unit):
///   - Pull the east-facing (yaw=0) Walk/Jog/Run frames from <see cref="UnitSpriteData"/>.
///   - For each frame, scan the BOTTOM 50% of pixel rows for the leftmost and
///     rightmost non-transparent pixel. The horizontal extent is that frame's
///     "foot spread". Bottom 50% (not 25%) is needed for run gaits where the
///     trailing foot lifts to knee/hip height.
///   - Take the 90th-percentile of per-frame extents (not max). Drops one or two
///     anomalous frames (weapon thrust pose, extreme cape flare) without losing
///     the genuine peak stride.
///   - Stride distance = that percentile value. Cycle distance = stride × 2
///     (two strides per cycle: left foot then right foot).
///
/// Walk-anchored sanity check:
///   - Walk is the most reliable measurement (both feet on ground most frames).
///   - Jog and Run are validated against Walk by their ratio. If a measured ratio
///     falls outside the biomechanically plausible range, we extrapolate from
///     walk instead (walk × 1.5 for jog, walk × 2.2 for run). Catches cases
///     where the bottom-50% heuristic gets confused by long downward-pointing
///     weapons or unusual lifted-foot framing.
///
/// Cache:
///   - One JSON file per atlas at <c>cache/stride/{atlas}.stride.json</c>.
///   - Invalidates when: algorithm version bumps, PNG mtime/size changes,
///     spritemeta mtime/size changes, or animationmeta mtime/size changes.
///   - SpriteWorldHeight (from UnitDef) is NOT part of the cache key — pixel
///     measurements are unit-def-independent, and conversion to world units
///     happens at runtime. Editing units.json doesn't trigger recalibration.
/// </summary>
public static class StrideCalibration
{
    /// <summary>Bump when the measurement algorithm changes in any way that would
    /// produce different numbers (different percentile, different cut-off row,
    /// different cycle-distance multiplier, etc.). Existing caches with a lower
    /// version are treated as misses and rebuilt.</summary>
    public const int AlgorithmVersion = 1;

    /// <summary>Fraction of the sprite's pixel height (measured from the bottom)
    /// to scan for foot pixels. Picked so a lifted trailing foot in a run cycle
    /// is still inside the strip — knee-to-hip height on a humanoid sits roughly
    /// at 40-50% from the bottom.</summary>
    private const float BottomStripFraction = 0.50f;

    /// <summary>Percentile of per-frame foot-spread extents used as the stride
    /// length. 0.9 drops the top 10% of frames as likely outliers (weapon thrust
    /// poses, cape flare) without losing the genuine peak-stride frame.</summary>
    private const float StridePercentile = 0.90f;

    /// <summary>Each gait cycle covers two strides (left foot, right foot), so
    /// cycle_distance = stride × this constant. 2.0 is right for bipeds; some
    /// quadrupeds want ~1.6 but that's handled per-unit via override values,
    /// not by changing this constant.</summary>
    private const float StridesPerCycle = 2.0f;

    // Jog/Run extrapolation factors when measurement is missing or implausible.
    public const float JogToWalkRatio = 1.5f;
    public const float RunToWalkRatio = 2.2f;

    // Plausibility windows for measured ratios. Outside these, fall back to
    // extrapolation — measurement was probably confused.
    public const float JogMinRatio = 1.2f;
    public const float JogMaxRatio = 2.0f;
    public const float RunMinRatio = 1.6f;
    public const float RunMaxRatio = 3.0f;

    /// <summary>Per-gait stride measurement for one unit. Pixel values; world
    /// conversion happens at runtime in <see cref="ResolveAnimVel"/>.</summary>
    public class GaitCalibration
    {
        /// <summary>Measured stride distance in pixels (90th-percentile of
        /// per-frame foot-spread across the cycle). Zero if no anim authored.</summary>
        public float StridePx;

        /// <summary>True if this value was derived from walk × extrapolation
        /// ratio rather than measured directly. Either no anim was authored,
        /// or the measured ratio relative to walk was implausible.</summary>
        public bool WasExtrapolated;

        /// <summary>Total cycle duration in seconds (sum of per-frame time_ms
        /// across the gait, divided by 1000). Pulled from AnimationMeta.</summary>
        public float CycleSeconds;

        /// <summary>Average sprite pixel height across the gait's frames. Used
        /// with UnitDef.SpriteWorldHeight at runtime to convert stride pixels
        /// to world units. Stored per-gait (not per-sprite) because pose changes
        /// between gaits can shift the bounding height noticeably.</summary>
        public float AvgPixelHeight;
    }

    /// <summary>Full per-unit calibration: one entry per gait + provenance fields
    /// useful for editor display.</summary>
    public class UnitCalibration
    {
        public GaitCalibration Walk = new();
        public GaitCalibration Jog = new();
        public GaitCalibration Run = new();

        /// <summary>Which yaw was actually used for the pixel scan (0=east in
        /// the project's coordinate system; falls back to any available yaw if
        /// east isn't authored). Mostly diagnostic.</summary>
        public int MeasuredYaw;
    }

    // =========================================================================
    //  Measurement
    // =========================================================================

    /// <summary>Compute one unit's calibration from atlas pixel data + spritemeta
    /// frame rects + animationmeta timing. Returns a fully-populated
    /// <see cref="UnitCalibration"/>; gaits that weren't authored show up as
    /// extrapolated walk-anchored fallbacks.</summary>
    public static UnitCalibration MeasureUnit(
        string unitName,
        UnitSpriteData spriteData,
        Color[] atlasPixels,
        int atlasWidth,
        int atlasHeight,
        Dictionary<string, AnimationMeta>? animMeta)
    {
        var cal = new UnitCalibration();

        // Walk first — it's the anchor for the sanity check on Jog/Run.
        MeasureGait(cal.Walk, unitName, spriteData, "Walk",
            atlasPixels, atlasWidth, atlasHeight, animMeta, out int yaw);
        cal.MeasuredYaw = yaw;

        MeasureGait(cal.Jog, unitName, spriteData, "Jog",
            atlasPixels, atlasWidth, atlasHeight, animMeta, out _);
        MeasureGait(cal.Run, unitName, spriteData, "Run",
            atlasPixels, atlasWidth, atlasHeight, animMeta, out _);

        // Walk-anchored sanity check on Jog and Run: if measured stride relative
        // to walk is implausible (or wasn't measured at all), extrapolate from
        // walk. Walk itself can't be sanity-checked — it IS the anchor.
        if (cal.Walk.StridePx > 0f)
        {
            CheckRatio(cal.Walk, cal.Jog, JogToWalkRatio, JogMinRatio, JogMaxRatio);
            CheckRatio(cal.Walk, cal.Run, RunToWalkRatio, RunMinRatio, RunMaxRatio);
        }

        return cal;
    }

    /// <summary>Replace a gait's measurement with walk × ratio if it's missing
    /// or its ratio relative to walk falls outside the plausibility window.</summary>
    private static void CheckRatio(GaitCalibration walk, GaitCalibration gait,
        float defaultRatio, float minRatio, float maxRatio)
    {
        if (gait.StridePx <= 0f)
        {
            gait.StridePx = walk.StridePx * defaultRatio;
            gait.WasExtrapolated = true;
            return;
        }
        float ratio = gait.StridePx / walk.StridePx;
        if (ratio < minRatio || ratio > maxRatio)
        {
            gait.StridePx = walk.StridePx * defaultRatio;
            gait.WasExtrapolated = true;
        }
    }

    private static void MeasureGait(GaitCalibration g,
        string unitName, UnitSpriteData spriteData, string animName,
        Color[] atlasPixels, int atlasWidth, int atlasHeight,
        Dictionary<string, AnimationMeta>? animMeta, out int measuredYaw)
    {
        measuredYaw = 0;
        var anim = spriteData.GetAnim(animName);
        if (anim == null) return;

        // Prefer east (yaw=0) for stride measurement — it's the side profile
        // where stride is most legible. If east is missing, pick whatever yaw
        // is present so we still get a number.
        var kfs = anim.GetAngle(0);
        if (kfs == null || kfs.Count == 0)
        {
            foreach (var (a, list) in anim.AngleFrames)
            {
                if (list.Count > 0) { kfs = list; measuredYaw = a; break; }
            }
            if (kfs == null) return;
        }

        // Per-frame foot spread (horizontal extent of bottom 50% non-transparent pixels).
        var extents = new List<int>(kfs.Count);
        float heightSum = 0f;
        int heightCount = 0;
        foreach (var kf in kfs)
        {
            var r = kf.Frame.Rect;
            if (r.Width <= 0 || r.Height <= 0) continue;
            int ext = MeasureFrameFootSpread(atlasPixels, atlasWidth, atlasHeight, r);
            if (ext > 0) extents.Add(ext);
            heightSum += r.Height;
            heightCount++;
        }

        if (extents.Count == 0) return;

        extents.Sort();
        int idx = (int)Math.Floor(StridePercentile * (extents.Count - 1));
        if (idx < 0) idx = 0;
        if (idx >= extents.Count) idx = extents.Count - 1;
        g.StridePx = extents[idx];
        g.AvgPixelHeight = heightCount > 0 ? heightSum / heightCount : 0f;

        if (animMeta != null
            && animMeta.TryGetValue(AnimMetaLoader.MetaKey(unitName, animName), out var meta))
        {
            int totalMs = meta.TotalDurationMs();
            g.CycleSeconds = totalMs > 0 ? totalMs / 1000f : 0f;
        }
    }

    /// <summary>For a single sprite frame, scan the bottom 50% of rows and return
    /// the horizontal distance between the leftmost and rightmost non-transparent
    /// pixel. Returns 0 if the frame is empty.</summary>
    private static int MeasureFrameFootSpread(Color[] pixels, int atlasW, int atlasH,
        Rectangle rect)
    {
        int rowsToScan = Math.Max(1, (int)(rect.Height * BottomStripFraction));
        int firstRowFromTop = rect.Y + (rect.Height - rowsToScan);
        // Defensive clamps — atlas frames should always be in-bounds, but tolerate
        // off-by-ones from rescale rounding.
        if (firstRowFromTop < 0) firstRowFromTop = 0;
        int endRow = firstRowFromTop + rowsToScan;
        if (endRow > atlasH) endRow = atlasH;

        int leftmost = int.MaxValue;
        int rightmost = int.MinValue;
        int xStart = Math.Max(0, rect.X);
        int xEnd = Math.Min(atlasW, rect.X + rect.Width);

        for (int y = firstRowFromTop; y < endRow; y++)
        {
            int rowOffset = y * atlasW;
            for (int x = xStart; x < xEnd; x++)
            {
                // Premultiplied alpha: A==0 means transparent regardless of RGB.
                if (pixels[rowOffset + x].A > 0)
                {
                    if (x < leftmost) leftmost = x;
                    if (x > rightmost) rightmost = x;
                }
            }
        }

        if (leftmost == int.MaxValue) return 0;
        return rightmost - leftmost;
    }

    // =========================================================================
    //  Runtime conversion (pixel stride → world velocity)
    // =========================================================================

    /// <summary>Convert a measured stride (in pixels) to a "feet-lock velocity"
    /// in world units per second, using a unit's world height + the sprite's
    /// average pixel height as the px-per-world-unit anchor. Returns 0 if any
    /// input is missing — caller should treat 0 as "unknown" and fall back to
    /// the legacy code path.</summary>
    public static float ResolveAnimVel(GaitCalibration g, float spriteWorldHeight)
    {
        if (g.StridePx <= 0f || g.CycleSeconds <= 0f
            || g.AvgPixelHeight <= 0f || spriteWorldHeight <= 0f)
            return 0f;
        float pixelsPerWorldUnit = g.AvgPixelHeight / spriteWorldHeight;
        float cycleDistanceWorld = (g.StridePx * StridesPerCycle) / pixelsPerWorldUnit;
        return cycleDistanceWorld / g.CycleSeconds;
    }

    // =========================================================================
    //  Cache I/O
    // =========================================================================

    /// <summary>Cache file path for a given atlas name (one cache per atlas).
    /// Lives next to the .pcache files under cache/stride/.</summary>
    public static string GetCachePath(string atlasName)
    {
        return Core.GamePaths.Resolve(
            Path.Combine("cache", "stride", atlasName + ".stride.json"));
    }

    /// <summary>Compose the cache validation key from input file mtimes/sizes
    /// + the algorithm version. Any change invalidates the whole atlas's cache.
    /// Cheap to compute — just three stat calls.</summary>
    public static string ComputeSignature(string pngPath, string spriteMetaPath, string animMetaPath)
    {
        long sig(string p) => File.Exists(p)
            ? new FileInfo(p).Length ^ new FileInfo(p).LastWriteTimeUtc.Ticks
            : 0;
        return $"v{AlgorithmVersion}-{sig(pngPath):x}-{sig(spriteMetaPath):x}-{sig(animMetaPath):x}";
    }

    /// <summary>On-disk cache shape. One file per atlas.</summary>
    public class CacheFile
    {
        public string Signature { get; set; } = "";
        public Dictionary<string, CacheEntry> Units { get; set; } = new();
    }

    public class CacheEntry
    {
        public int MeasuredYaw { get; set; }
        public CacheGait Walk { get; set; } = new();
        public CacheGait Jog { get; set; } = new();
        public CacheGait Run { get; set; } = new();
    }

    public class CacheGait
    {
        public float StridePx { get; set; }
        public bool WasExtrapolated { get; set; }
        public float CycleSeconds { get; set; }
        public float AvgPixelHeight { get; set; }
    }

    public static bool TryLoad(string atlasName, string expectedSignature,
        out Dictionary<string, UnitCalibration> cache)
    {
        cache = new Dictionary<string, UnitCalibration>(StringComparer.OrdinalIgnoreCase);
        string path = GetCachePath(atlasName);
        if (!File.Exists(path)) return false;
        try
        {
            string json = File.ReadAllText(path);
            var parsed = JsonSerializer.Deserialize<CacheFile>(json);
            if (parsed == null || parsed.Signature != expectedSignature) return false;
            foreach (var (unit, entry) in parsed.Units)
            {
                cache[unit] = new UnitCalibration
                {
                    MeasuredYaw = entry.MeasuredYaw,
                    Walk = FromCache(entry.Walk),
                    Jog = FromCache(entry.Jog),
                    Run = FromCache(entry.Run),
                };
            }
            return true;
        }
        catch (Exception ex)
        {
            Core.DebugLog.Log("startup", $"  [StrideCalibration] cache load failed for {atlasName}: {ex.Message}");
            return false;
        }
    }

    public static void Save(string atlasName, string signature,
        Dictionary<string, UnitCalibration> calibrations)
    {
        string path = GetCachePath(atlasName);
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var file = new CacheFile { Signature = signature };
            foreach (var (unit, cal) in calibrations)
            {
                file.Units[unit] = new CacheEntry
                {
                    MeasuredYaw = cal.MeasuredYaw,
                    Walk = ToCache(cal.Walk),
                    Jog = ToCache(cal.Jog),
                    Run = ToCache(cal.Run),
                };
            }
            string json = JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true });
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
        catch (Exception ex)
        {
            Core.DebugLog.Log("startup", $"  [StrideCalibration] cache save failed for {atlasName}: {ex.Message}");
        }
    }

    private static GaitCalibration FromCache(CacheGait c) => new()
    {
        StridePx = c.StridePx,
        WasExtrapolated = c.WasExtrapolated,
        CycleSeconds = c.CycleSeconds,
        AvgPixelHeight = c.AvgPixelHeight,
    };

    private static CacheGait ToCache(GaitCalibration g) => new()
    {
        StridePx = g.StridePx,
        WasExtrapolated = g.WasExtrapolated,
        CycleSeconds = g.CycleSeconds,
        AvgPixelHeight = g.AvgPixelHeight,
    };

    // =========================================================================
    //  Per-atlas orchestration (cache hit-or-build, then attach to UnitSpriteData)
    // =========================================================================

    /// <summary>Calibrate every unit in the atlas, prefer-cache. On cache hit,
    /// returns true and decodedPixels is not scanned. On miss, walks every unit
    /// in the atlas through the pixel scanner and writes a fresh cache. Either
    /// way, each UnitSpriteData in the atlas ends up with its
    /// <see cref="UnitSpriteData.Calibration"/> field populated.
    ///
    /// Atlas must already be finalized (textures uploaded, Y-flip applied) so
    /// the frame rects in spritemeta align with the pixel buffer layout.</summary>
    public static bool CalibrateAtlas(
        SpriteAtlas atlas, string atlasName,
        string pngPath, string spriteMetaPath, string animMetaPath,
        Color[] decodedPixels, int decodedW, int decodedH,
        Dictionary<string, AnimationMeta>? animMeta)
    {
        string sig = ComputeSignature(pngPath, spriteMetaPath, animMetaPath);

        if (TryLoad(atlasName, sig, out var cached))
        {
            foreach (var (unitName, udata) in atlas.Units)
                if (cached.TryGetValue(unitName, out var cal))
                    udata.Calibration = cal;
            return true;
        }

        var fresh = new Dictionary<string, UnitCalibration>(StringComparer.OrdinalIgnoreCase);
        foreach (var (unitName, udata) in atlas.Units)
        {
            var cal = MeasureUnit(unitName, udata, decodedPixels, decodedW, decodedH, animMeta);
            fresh[unitName] = cal;
            udata.Calibration = cal;
        }
        Save(atlasName, sig, fresh);
        return false;
    }
}
