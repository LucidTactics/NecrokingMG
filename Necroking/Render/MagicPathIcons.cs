using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Core;
using Necroking.Data.Registries;

namespace Necroking.Render;

/// <summary>
/// Shared cache for the 24/48px magic-path icons. Editors and HUD overlays grab
/// textures by (path, size) so each PNG is loaded once per session. Missing
/// files return null — callers fall back to text labels.
/// </summary>
public static class MagicPathIcons
{
    // Key: (path, size) — only 24 and 48 ship today, but keying lets future
    // sizes (e.g. a 16 for the Tab stats line if 24 reads too big) co-exist
    // without a parallel field per size.
    private static readonly Dictionary<(MagicPath, int), Texture2D?> _cache = new();
    private static GraphicsDevice? _device;

    public static void SetDevice(GraphicsDevice device) => _device = device;

    public static Texture2D? Get(MagicPath path, int size)
    {
        if (path == MagicPath.None || _device == null) return null;
        var key = (path, size);
        if (_cache.TryGetValue(key, out var cached)) return cached;

        string rel = MagicPathHelpers.IconPath(path, size);
        string resolved = GamePaths.Resolve(rel);
        Texture2D? tex = null;
        if (File.Exists(resolved))
        {
            try { tex = TextureUtil.LoadPremultiplied(_device, rel); }
            catch (System.Exception ex) { DebugLog.Log("error", $"MagicPathIcons: failed to load {rel}: {ex.Message}"); }
        }
        _cache[key] = tex;
        return tex;
    }
}
