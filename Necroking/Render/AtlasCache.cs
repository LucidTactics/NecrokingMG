using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;

namespace Necroking.Render;

/// <summary>
/// Premultiplied-RGBA cache for sprite atlases. Wraps <see cref="TextureUtil.DecodePngPremultiplied"/>:
/// on cache hit, reads raw RGBA bytes (the post-Skia output) directly into a Color[] and skips
/// PNG decode entirely; on miss, decodes the PNG and writes a fresh cache for next launch.
///
/// Format choice: raw uncompressed RGBA, NOT compressed. Trade-off: cache file is much bigger
/// (2.5 GB total vs 143 MB for zstd-1) BUT the OS file cache holds it in standby memory so
/// repeat launches read from RAM at ~10-20 GB/s with zero decompress CPU work. For a dev-loop
/// workflow (many launches per day, no shipping concerns yet), this is the fastest option.
/// We can add compression closer to release when disk footprint matters.
///
/// File layout (.pcache, little-endian):
///   off   size  field
///     0     4   magic       = 0x48435045 ("EPCH")
///     4     2   version     = 2 (v1 was zstd-compressed; bumped so old caches auto-rebuild)
///     6     2   flags       = reserved
///     8     8   srcSize     = source PNG byte size
///    16     8   srcMtime    = source PNG LastWriteTime in Ticks
///    24     4   width
///    28     4   height
///    32     4   rawBytes    = w*h*4 (sanity check)
///    36     4   reserved
///    40     N   raw RGBA pixel data, premultiplied alpha (N = w*h*4)
///
/// Validation: stat the source PNG, compare srcSize + srcMtime. Either differs → miss/rebuild.
/// (Cheap — microseconds per atlas; catches any normal asset edit since copy/save updates mtime.)
/// </summary>
public static class AtlasCache
{
    private const uint Magic = 0x48435045;
    private const ushort Version = 2;
    private const string CacheDirName = "cache";
    private const int HeaderSize = 40;

    /// <summary>Set to true to force rebuild even when stored mtime/size matches.
    /// Wired up to a CLI flag if desired (e.g. --rebuild-atlas-cache).</summary>
    public static bool ForceRebuild = false;

    /// <summary>Try to load decoded RGBA pixels for the given source PNG from the
    /// .pcache sidecar file. Returns false if cache is missing, stale, corrupt,
    /// or version-mismatched — caller should fall back to PNG decode.</summary>
    public static bool TryLoad(string pngPath, out Color[] pixels, out int width, out int height)
    {
        pixels = null!; width = 0; height = 0;
        if (ForceRebuild) return false;

        string cachePath = GetCachePath(pngPath);
        if (!File.Exists(cachePath) || !File.Exists(pngPath)) return false;

        var pngInfo = new FileInfo(pngPath);
        long pngSize = pngInfo.Length;
        long pngMtime = pngInfo.LastWriteTimeUtc.Ticks;

        try
        {
            using var fs = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var br = new BinaryReader(fs);

            if (br.ReadUInt32() != Magic) return false;
            if (br.ReadUInt16() != Version) return false;
            br.ReadUInt16(); // flags
            long storedSize  = br.ReadInt64();
            long storedMtime = br.ReadInt64();
            if (storedSize != pngSize || storedMtime != pngMtime) return false;

            int w        = br.ReadInt32();
            int h        = br.ReadInt32();
            int rawBytes = br.ReadInt32();
            br.ReadInt32(); // reserved
            if (rawBytes != w * h * 4) return false;
            if (fs.Length - fs.Position < rawBytes) return false; // truncated cache

            // Read raw RGBA bytes directly into the Color[]'s byte view. Zero
            // alloc, zero copy — Color is a 4-byte blittable struct so a Span
            // reinterpret aliases the same memory. FileStream.Read fills the
            // destination span directly from disk (or OS cache).
            pixels = new Color[w * h];
            var dstSpan = MemoryMarshal.AsBytes(pixels.AsSpan());
            int totalRead = 0;
            while (totalRead < rawBytes)
            {
                int n = fs.Read(dstSpan.Slice(totalRead));
                if (n == 0) return false; // unexpected EOF
                totalRead += n;
            }
            width = w; height = h;
            return true;
        }
        catch
        {
            // Corrupt cache file or any I/O hiccup → treat as miss and rebuild.
            return false;
        }
    }

    /// <summary>Write the decoded RGBA buffer to the .pcache sidecar. Best-effort:
    /// any I/O failure is logged and swallowed — the atlas will just be re-decoded
    /// next launch.</summary>
    public static void Save(string pngPath, Color[] pixels, int width, int height)
    {
        string cachePath = GetCachePath(pngPath);
        try
        {
            string? dir = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var pngInfo = new FileInfo(pngPath);
            int rawBytes = width * height * 4;
            var rawSpan = MemoryMarshal.AsBytes(pixels.AsSpan());

            // Atomic write: write to .tmp, then rename. Avoids leaving a half-
            // written file if startup is interrupted mid-write.
            string tmp = cachePath + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write(Magic);
                bw.Write(Version);
                bw.Write((ushort)0);                          // flags
                bw.Write(pngInfo.Length);
                bw.Write(pngInfo.LastWriteTimeUtc.Ticks);
                bw.Write(width);
                bw.Write(height);
                bw.Write(rawBytes);
                bw.Write(0);                                  // reserved
                fs.Write(rawSpan);                            // raw pixel payload
            }
            if (File.Exists(cachePath)) File.Delete(cachePath);
            File.Move(tmp, cachePath);
        }
        catch (Exception ex)
        {
            Necroking.Core.DebugLog.Log("startup", $"  [AtlasCache] save failed for {pngPath}: {ex.Message}");
        }
    }

    /// <summary>Resolve the cache file path for a given source PNG.
    /// All caches now live in {projectRoot}/cache/sprites/{pngBaseName}.pcache,
    /// alongside future caches for other slow-to-load assets (game data JSON,
    /// animation metadata, etc. each in their own cache/ subfolder).</summary>
    public static string GetCachePath(string pngPath)
    {
        string baseName = Path.GetFileNameWithoutExtension(pngPath);
        return Necroking.Core.GamePaths.Resolve(
            Path.Combine("cache", "sprites", baseName + ".pcache"));
    }
}
