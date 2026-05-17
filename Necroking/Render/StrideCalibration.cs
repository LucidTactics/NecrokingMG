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
    /// version are treated as misses and rebuilt.
    /// v2: added IdleFootSpreadPx measurement for quadruped body-width subtraction.
    /// v3: switched gait stride measurement from 90th-percentile to max across
    ///     frames. Locomotion anims are smooth (no outlier weapon-thrust frames
    ///     like attack anims have), so max captures the true peak-stride frame
    ///     instead of clipping it as an outlier.
    /// v4: strip fraction now measured from content vertical extent (topmost to
    ///     bottommost non-transparent rows), not from bounding rect. Avoids
    ///     transparent padding at the top of the rect skewing the strip into
    ///     unintended body parts (tail, cape, etc).
    /// v5: gait strip fraction lowered from 50% to 25% (matching idle). The
    ///     wolf body silhouette at 50%-of-content was still dominating the
    ///     measurement, masking actual paw motion. With both gait + idle at
    ///     25% the subtraction (walk_extent - idle_extent) measures consistent
    ///     paw-only regions and reflects real leg-swing amplitude. May miss
    ///     lifted back-paws in run gaits — revisit if that becomes visible.
    /// v6: per-gait strip fractions — Walk + Idle still at 25% (consistent for
    ///     the subtraction, tight to ground), Jog + Run back to 50% (catches
    ///     lifted trailing paw at peak stride in faster gaits).
    /// v7: Jog + Run strip lowered from 50% to 40% — 50% was still catching
    ///     too much body silhouette on quadrupeds, 40% empirically captures
    ///     lifted paws without being overrun by the body.
    /// v8: gait stride is now the ENVELOPE of leg positions across the whole
    ///     cycle (max rightmost-in-body-frame across frames − min leftmost-
    ///     in-body-frame across frames), instead of max single-frame inter-
    ///     paw spread. The old approach under-measured quadruped walks where
    ///     legs are phase-staggered: no single frame had both legs at peak
    ///     extremes simultaneously, so per-frame spread was always < 2A
    ///     (the true leg amplitude). The envelope captures the full 2A
    ///     because the front-leg's extreme-forward frame and the rear-leg's
    ///     extreme-back frame are different frames, but their extremes are
    ///     both visible across the cycle.</summary>
    public const int AlgorithmVersion = 8;

    /// <summary>Strip fraction for Walk + Idle — both use 25% to keep their
    /// measurements like-for-like (the body-subtraction walk-idle is comparing
    /// regions of the same vertical proportion). Tight enough to avoid the
    /// body silhouette that dominates 50%+ strips on quadrupeds.</summary>
    private const float WalkStripFraction = 0.25f;

    /// <summary>Strip fraction for Jog + Run — 40%, because the trailing paw
    /// lifts toward knee height at peak stride in faster gaits. Tight 25%
    /// would miss the lifted paw; 50% pulls in too much body silhouette.
    /// 40% is the empirical middle ground that captures lifted paws without
    /// being overrun by the body.</summary>
    private const float RunStripFraction = 0.40f;

    /// <summary>Legacy alias: callers that don't differentiate by gait get
    /// the conservative (smaller) value. Used by tests / generic call paths.</summary>
    private const float BottomStripFraction = WalkStripFraction;

    // v3: Removed StridePercentile in favor of max-across-frames. For locomotion
    // anims (walk/jog/run) the cycle is smooth and there are no outlier frames
    // to filter out — the percentile was systematically clipping the genuine
    // peak-stride frame and biasing stride low by ~half the true amplitude.
    // (For attack anims with weapon thrust outliers, we'd want the percentile
    // back, but those don't go through StrideCalibration.)

    /// <summary>Per-leg duty cycle used for biped walks (and the formula's
    /// default). Each leg is planted half the cycle, swinging the other half.
    /// During its planted phase a leg traverses 2A in body frame (peak-to-peak
    /// amplitude), so body covers 2A/d per cycle = 4A for d=0.5. The "× 2 strides
    /// per cycle" identity comes from this: with `walk_extent = 2A`, cycle_dist
    /// = walk_extent / d = 2 × walk_extent. Per-unit dutyCycle override (e.g.
    /// 0.75 for typical quadruped lateral walks) reshapes the formula
    /// accordingly.</summary>
    public const float DefaultDutyCycle = 0.5f;

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

        /// <summary>Horizontal extent of the bottom 25% of the Idle anim (east
        /// yaw). For a biped this is roughly stance width (a few px). For a
        /// quadruped this is roughly body length (front-to-back paw spread at
        /// rest). Subtracted from each gait's measured stride for units flagged
        /// <c>IsQuadruped=true</c> in their UnitDef — strips out the
        /// body-length component that contaminates the leg-stride measurement.
        /// Zero if no Idle anim is authored.</summary>
        public float IdleFootSpreadPx;
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

        // Walk first — it's the anchor for the sanity check on Jog/Run. Walk
        // scans the same 25% strip as Idle so the subtraction (walk - idle) is
        // measuring comparable regions. Jog/Run use 50% to catch lifted paws
        // at peak stride in faster gaits where the trailing foot leaves the
        // ground.
        MeasureGait(cal.Walk, unitName, spriteData, "Walk", WalkStripFraction,
            atlasPixels, atlasWidth, atlasHeight, animMeta, out int yaw);
        cal.MeasuredYaw = yaw;

        MeasureGait(cal.Jog, unitName, spriteData, "Jog", RunStripFraction,
            atlasPixels, atlasWidth, atlasHeight, animMeta, out _);
        MeasureGait(cal.Run, unitName, spriteData, "Run", RunStripFraction,
            atlasPixels, atlasWidth, atlasHeight, animMeta, out _);

        // Idle foot-spread: bottom 25% horizontal extent of the Idle anim's east
        // yaw. Captures stance width for bipeds (~small) and body length for
        // quadrupeds (~large) — when subtracted from a quadruped's gait stride
        // measurements, strips out the body-length contamination that otherwise
        // makes 4-legged "stride spread" look much bigger than the actual leg
        // stride that drives ground motion.
        cal.IdleFootSpreadPx = MeasureIdleFootSpread(spriteData,
            atlasPixels, atlasWidth, atlasHeight);

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

    /// <summary>Measure the horizontal extent of the bottom 25% of the Idle anim's
    /// content vertical extent (NOT bounding rect — see MeasureFrameFootSpread).
    /// Picks the first authored Idle keyframe at yaw=0 (or any yaw if east is
    /// missing). Returns 0 if Idle is missing or empty — callers treat 0 as
    /// "no idle data, skip body subtraction."</summary>
    private static float MeasureIdleFootSpread(UnitSpriteData spriteData,
        Color[] pixels, int atlasW, int atlasH)
    {
        var idle = spriteData.GetAnim("Idle");
        if (idle == null) return 0f;
        var kfs = idle.GetAngle(0);
        if (kfs == null || kfs.Count == 0)
        {
            foreach (var (_, list) in idle.AngleFrames)
            {
                if (list.Count > 0) { kfs = list; break; }
            }
            if (kfs == null) return 0f;
        }
        // Tighter strip (25%) than the gait scan (50%) — Idle's feet are firmly
        // on the ground so we don't need the headroom for lifted-foot capture.
        // Max across frames catches the most extended pose if Idle breathes / shifts.
        int maxExtent = 0;
        foreach (var kf in kfs)
        {
            var r = kf.Frame.Rect;
            if (r.Width <= 0 || r.Height <= 0) continue;
            var (l, rPx) = MeasureFrameFootBounds(pixels, atlasW, atlasH, r, 0.25f);
            if (l < 0) continue;
            int ext = rPx - l;
            if (ext > maxExtent) maxExtent = ext;
        }
        return maxExtent;
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
        float stripFraction,
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

        // Envelope-of-leg-positions across the cycle (v8). For each frame, get
        // the leftmost & rightmost non-transparent pixel in the bottom strip,
        // converted to pivot-relative ("body frame") coordinates. Then take
        // the GLOBAL min(leftmost_body) and max(rightmost_body) across all
        // frames. The envelope's width = the full peak-to-peak amplitude of
        // leg motion in body frame, regardless of whether any single frame
        // captures both extremes. Solves the quadruped under-measurement
        // where phase-staggered legs never extend simultaneously.
        int globalLeftBody = int.MaxValue;
        int globalRightBody = int.MinValue;
        float heightSum = 0f;
        int heightCount = 0;
        bool anyHit = false;
        foreach (var kf in kfs)
        {
            var r = kf.Frame.Rect;
            if (r.Width <= 0 || r.Height <= 0) continue;
            heightSum += r.Height;
            heightCount++;
            var (l, rPx) = MeasureFrameFootBounds(atlasPixels, atlasWidth, atlasHeight, r, stripFraction);
            if (l < 0) continue;
            // Convert atlas-X to pivot-relative ("body frame") X so frames at
            // different atlas positions can be compared like-for-like.
            float pivotAtlasX = r.X + kf.Frame.PivotX * r.Width;
            int lBody  = l   - (int)pivotAtlasX;
            int rBody  = rPx - (int)pivotAtlasX;
            if (lBody < globalLeftBody) globalLeftBody = lBody;
            if (rBody > globalRightBody) globalRightBody = rBody;
            anyHit = true;
        }
        if (!anyHit) return;

        g.StridePx = globalRightBody - globalLeftBody;
        g.AvgPixelHeight = heightCount > 0 ? heightSum / heightCount : 0f;

        if (animMeta != null
            && animMeta.TryGetValue(AnimMetaLoader.MetaKey(unitName, animName), out var meta))
        {
            int totalMs = meta.TotalDurationMs();
            g.CycleSeconds = totalMs > 0 ? totalMs / 1000f : 0f;
        }
    }

    /// <summary>For a single sprite frame, scan the bottom `stripFraction` of
    /// the frame's CONTENT vertical extent (not the bounding rect) and return
    /// the leftmost / rightmost non-transparent pixel X (in atlas coords) in
    /// that strip. Returns (-1, -1) if the frame is empty. Callers can take
    /// (right - left) for single-frame spread, or aggregate across frames
    /// (with pivot-relative coordinates) for envelope measurement.</summary>
    private static (int left, int right) MeasureFrameFootBounds(
        Color[] pixels, int atlasW, int atlasH, Rectangle rect,
        float stripFraction = BottomStripFraction)
    {
        // First pass: find the content's vertical extent (top-most and bottom-most
        // rows with any non-transparent pixel). This is what defines "the
        // silhouette" for strip-fraction purposes.
        int xStart = Math.Max(0, rect.X);
        int xEnd = Math.Min(atlasW, rect.X + rect.Width);
        int yStart = Math.Max(0, rect.Y);
        int yEnd = Math.Min(atlasH, rect.Y + rect.Height);
        int contentTop = -1, contentBot = -1;
        for (int y = yStart; y < yEnd; y++)
        {
            int rowOffset = y * atlasW;
            bool hasPixel = false;
            for (int x = xStart; x < xEnd; x++)
            {
                if (pixels[rowOffset + x].A > 0) { hasPixel = true; break; }
            }
            if (hasPixel)
            {
                if (contentTop < 0) contentTop = y;
                contentBot = y;
            }
        }
        if (contentTop < 0) return (-1, -1); // empty frame

        int contentHeight = contentBot - contentTop + 1;
        int rowsToScan = Math.Max(1, (int)(contentHeight * stripFraction));
        int firstRowFromTop = contentBot - rowsToScan + 1;
        if (firstRowFromTop < contentTop) firstRowFromTop = contentTop;

        int leftmost = int.MaxValue;
        int rightmost = int.MinValue;
        for (int y = firstRowFromTop; y <= contentBot; y++)
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

        if (leftmost == int.MaxValue) return (-1, -1);
        return (leftmost, rightmost);
    }

    // =========================================================================
    //  Runtime conversion (pixel stride → world velocity)
    // =========================================================================

    /// <summary>Convert a measured stride (in pixels) to a "feet-lock velocity"
    /// in world units per second. The pixel→world conversion uses the unit's
    /// effective rendered height = <paramref name="spriteWorldHeight"/> ×
    /// <paramref name="spriteScale"/>, mirroring how the renderer actually sizes
    /// the sprite on the map (see <c>ShadowRenderer.cs:232</c>:
    /// <c>worldH = SpriteWorldHeight × SpriteScale</c>). Omitting SpriteScale
    /// would silently overstate cycle distance for any unit drawn at less than
    /// 1.0× scale (e.g. Wretched at 0.9 would overstate by 11%), causing the
    /// playback rate to underestimate and feet to drag behind body motion.
    /// Returns 0 if any input is missing — caller treats 0 as "unknown" and
    /// falls back to the legacy code path.</summary>
    /// <summary>Compute the "suggested CombatSpeed" for a unit — the body
    /// velocity at which the walk anim plays such that one full cycle takes
    /// <paramref name="targetCycleSeconds"/> while feet stay perfectly locked
    /// to the ground. If <paramref name="targetCycleSeconds"/> is 0 or
    /// negative, falls back to the artist's authored cycle (= the
    /// <see cref="ResolveAnimVel"/> value for the gait). Returns 0 when
    /// calibration data is missing — caller should hide the suggestion in
    /// that case rather than display "0".</summary>
    public static float ResolveSuggestedCombatSpeed(
        GaitCalibration g, float spriteWorldHeight, float spriteScale,
        float bodySubtractionPx, float dutyCycle, float targetCycleSeconds)
    {
        if (g.StridePx <= 0f || g.AvgPixelHeight <= 0f
            || spriteWorldHeight <= 0f || spriteScale <= 0f
            || dutyCycle <= 0f || dutyCycle >= 1f)
            return 0f;
        float effectiveStridePx = MathF.Max(g.StridePx - bodySubtractionPx, 1f);
        float effectiveWorldHeight = spriteWorldHeight * spriteScale;
        float pixelsPerWorldUnit = g.AvgPixelHeight / effectiveWorldHeight;
        float cycleDistanceWorld = (effectiveStridePx / dutyCycle) / pixelsPerWorldUnit;
        // Use target cycle if specified, otherwise the artist's authored cycle.
        float t = targetCycleSeconds > 0f ? targetCycleSeconds : g.CycleSeconds;
        if (t <= 0f) return 0f;
        return cycleDistanceWorld / t;
    }

    public static float ResolveAnimVel(GaitCalibration g, float spriteWorldHeight,
        float spriteScale = 1f, float bodySubtractionPx = 0f,
        float dutyCycle = DefaultDutyCycle)
    {
        if (g.StridePx <= 0f || g.CycleSeconds <= 0f
            || g.AvgPixelHeight <= 0f || spriteWorldHeight <= 0f || spriteScale <= 0f
            || dutyCycle <= 0f || dutyCycle >= 1f)
            return 0f;
        // Subtract body-width contamination for quadrupeds (caller passes the
        // unit's IdleFootSpreadPx when the def is flagged IsQuadruped). Clamped
        // to a small positive value so a pathological subtraction (idle wider
        // than gait stride) doesn't drive the result to zero or negative.
        float effectiveStridePx = MathF.Max(g.StridePx - bodySubtractionPx, 1f);
        float effectiveWorldHeight = spriteWorldHeight * spriteScale;
        float pixelsPerWorldUnit = g.AvgPixelHeight / effectiveWorldHeight;
        // cycle_distance = stride / dutyCycle. Biped d=0.5 → × 2 (matches the
        // original "× StridesPerCycle" formula). Quadruped lateral walk d=0.75
        // → × 1.33, accounting for the fact that with 75% duty, the per-leg
        // amplitude in body frame is traversed in 0.75T (not 0.5T), so body
        // covers stride/0.75 per cycle, not stride/0.5.
        float cycleDistanceWorld = (effectiveStridePx / dutyCycle) / pixelsPerWorldUnit;
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
        public float IdleFootSpreadPx { get; set; }
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
                    IdleFootSpreadPx = entry.IdleFootSpreadPx,
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
                    IdleFootSpreadPx = cal.IdleFootSpreadPx,
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
