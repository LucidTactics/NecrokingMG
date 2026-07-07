using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Editor;
using Necroking.Render;

namespace Necroking.UI;

/// <summary>
/// Shared widget resource cache used by both <see cref="RuntimeWidgetRenderer"/> (batch
/// bake at load) and the UI editor (incremental live bake). Owns the four backing stores
/// that both sides previously mirrored — raw textures (via <see cref="TextureCache"/>),
/// lazily-built <see cref="NineSlice"/> instances, and the harmonized (per-pixel
/// color-shifted) texture / nine-slice caches keyed by "prefix|id" — plus the
/// harmonized-first lookup and nine-slice construction. This is the cache MECHANICS only;
/// the harmonize BAKE control flow stays in each caller (structurally different: one is a
/// three-phase parallel batch, the other an incremental dispose-old live edit), driving
/// the harmonized stores through the Store/Remove/TryGet methods here.
///
/// Device is read through a provider so a device assigned after construction is seen; the
/// nine-slice def is resolved through a caller-supplied lookup (the def list is the
/// caller's live data). Teardown is exposed as granular pieces because the two callers
/// compose them differently (runtime Shutdown unloads harmonized nine-slices; the editor's
/// InvalidateAllDerivedCaches clears them WITHOUT unload — they share the harmonized
/// textures it just disposed).
/// </summary>
public sealed class WidgetResourceCache
{
    private readonly Func<GraphicsDevice?> _device;
    private readonly Func<string, UIEditorNineSliceDef?> _resolveNsDef;

    private readonly TextureCache _textures;
    private readonly Dictionary<string, NineSlice> _nsInstances = new();
    private readonly Dictionary<string, Texture2D> _harmonizedTextures = new();
    private readonly Dictionary<string, NineSlice> _harmonizedNineSlices = new();

    public WidgetResourceCache(Func<GraphicsDevice?> deviceProvider,
        Func<string, UIEditorNineSliceDef?> resolveNsDef, string? logChannel = null)
    {
        _device = deviceProvider;
        _resolveNsDef = resolveNsDef;
        _textures = new TextureCache(logChannel);
    }

    // ── Raw resources ──────────────────────────────────────────────────────
    public Texture2D? GetOrLoadTexture(string? path) => _textures.GetOrLoad(_device(), path);

    /// <summary>Lazily build + cache the base NineSlice for <paramref name="nsId"/> from
    /// the caller's def (null if no device / unknown id / load failure).</summary>
    public NineSlice? GetOrLoadNineSlice(string? nsId)
    {
        var device = _device();
        if (string.IsNullOrEmpty(nsId) || device == null) return null;
        if (_nsInstances.TryGetValue(nsId, out var ns)) return ns;

        var def = _resolveNsDef(nsId);
        if (def == null) return null;

        var nsDef = new NineSliceDef
        {
            Id = def.Id, TexturePath = def.Texture,
            BorderLeft = def.BorderLeft, BorderRight = def.BorderRight,
            BorderTop = def.BorderTop, BorderBottom = def.BorderBottom,
            TileEdges = def.TileEdges,
        };
        ns = new NineSlice();
        if (!ns.Load(device, nsDef)) return null;
        _nsInstances[nsId] = ns;
        return ns;
    }

    // ── Harmonized-first lookups ("prefix|id"; empty prefix → bare id) ──────
    public Texture2D? GetTexture(string? path, string cachePrefix = "")
    {
        if (string.IsNullOrEmpty(path)) return null;
        string key = string.IsNullOrEmpty(cachePrefix) ? path : cachePrefix + "|" + path;
        return _harmonizedTextures.TryGetValue(key, out var h) ? h : GetOrLoadTexture(path);
    }

    public NineSlice? GetNineSlice(string? nsId, string cachePrefix = "")
    {
        if (string.IsNullOrEmpty(nsId)) return null;
        string key = string.IsNullOrEmpty(cachePrefix) ? nsId : cachePrefix + "|" + nsId;
        return _harmonizedNineSlices.TryGetValue(key, out var h) ? h : GetOrLoadNineSlice(nsId);
    }

    // ── Harmonized store/lookup (callers own the bake; this owns the storage) ─
    public bool TryGetHarmonizedTexture(string key, out Texture2D tex) => _harmonizedTextures.TryGetValue(key, out tex!);
    public void StoreHarmonizedTexture(string key, Texture2D tex) => _harmonizedTextures[key] = tex;
    public void RemoveHarmonizedTexture(string key) => _harmonizedTextures.Remove(key);
    public void StoreHarmonizedNineSlice(string key, NineSlice ns) => _harmonizedNineSlices[key] = ns;
    public void RemoveHarmonizedNineSlice(string key) => _harmonizedNineSlices.Remove(key);

    /// <summary>Editor live-edit: drop the cached base NineSlice so it rebuilds from the
    /// current def next lookup.</summary>
    public void InvalidateNineSliceInstance(string nsId)
    {
        if (_nsInstances.TryGetValue(nsId, out var old))
        {
            old.Unload();
            _nsInstances.Remove(nsId);
        }
    }

    // ── Granular teardown (callers compose to match their exact semantics) ──
    public void ClearNineSliceInstances()
    {
        foreach (var ns in _nsInstances.Values) ns.Unload();
        _nsInstances.Clear();
    }

    public void ClearHarmonizedTextures()
    {
        foreach (var tex in _harmonizedTextures.Values) tex.Dispose();
        _harmonizedTextures.Clear();
    }

    /// <summary>Clear the harmonized nine-slice cache, optionally Unloading each first.
    /// Runtime Shutdown unloads (they own their textures); the editor clears without
    /// unload because those nine-slices reference harmonized textures disposed separately.</summary>
    public void ClearHarmonizedNineSlices(bool unload)
    {
        if (unload)
            foreach (var ns in _harmonizedNineSlices.Values) ns.Unload();
        _harmonizedNineSlices.Clear();
    }

    public void DisposeRawTextures() => _textures.DisposeAll();
}
