using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Core;

namespace Necroking.Render;

/// <summary>
/// Instance-owned path → Texture2D get-or-load memoization around
/// <see cref="TextureUtil.LoadPremultiplied"/>. Consolidates the ~6 hand-rolled copies
/// (Game1 item textures, RuntimeWidgetRenderer/UIEditorWindow/TextureFileBrowser widget
/// textures, GrassTuftRenderer sprites, EnvironmentSystem override sprites) that had
/// drifted on negative-caching, logging, and resolved-vs-raw keys.
///
/// One policy for every site (the safe merge of the prior behaviors):
///  • resolve (GamePaths.Resolve unless already rooted) → File.Exists gate →
///    LoadPremultiplied on the RESOLVED path (drops the old unresolved-path coupling);
///  • NEGATIVE CACHING: a missing file or a decode exception caches null, so a bad path
///    is probed once, not every frame. (Matches the Game1/EnvironmentSystem copies; the
///    editor/grass copies used to re-probe — call <see cref="Clear"/> to re-arm them.)
///  • the device is passed per call, so a device assigned after construction is always
///    seen; a request before the device exists returns null WITHOUT caching (retries).
///  • missing/failed loads log to the optional channel (null = silent, preserving the
///    sites that never logged).
///
/// Caller owns lifetime: call <see cref="DisposeAll"/> wherever the old backing dict was
/// disposed (RuntimeWidgetRenderer.Shutdown, TextureFileBrowser close, GrassTuftRenderer
/// Dispose). Sites that never disposed their dict (UIEditorWindow, EnvironmentSystem)
/// keep that behavior — they simply don't call DisposeAll.
/// </summary>
public sealed class TextureCache
{
    private readonly Dictionary<string, Texture2D?> _cache = new();
    private readonly string? _logChannel;

    public TextureCache(string? logChannel = null)
    {
        _logChannel = logChannel;
    }

    /// <summary>Get the cached texture for <paramref name="path"/>, loading (and caching,
    /// including a null on failure) on first request. Null path/device → null.</summary>
    public Texture2D? GetOrLoad(GraphicsDevice? device, string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (_cache.TryGetValue(path, out var cached)) return cached;

        if (device == null)
        {
            Log($"texture '{path}' requested before GraphicsDevice ready");
            return null; // do not cache — retry once a device exists
        }

        string resolved = Path.IsPathRooted(path) ? path : GamePaths.Resolve(path);
        if (!File.Exists(resolved))
        {
            Log($"texture missing: '{path}' (resolved '{resolved}')");
            _cache[path] = null;
            return null;
        }
        try
        {
            var tex = TextureUtil.LoadPremultiplied(device, resolved);
            _cache[path] = tex;
            return tex;
        }
        catch (Exception ex)
        {
            Log($"failed to load texture '{path}': {ex.Message}");
            _cache[path] = null;
            return null;
        }
    }

    /// <summary>Drop all entries (positive and negative) without disposing — e.g. an
    /// editor refreshing its listing wants missing paths re-probed. Positive textures may
    /// still be referenced by in-flight draws, so this does NOT dispose them.</summary>
    public void Clear() => _cache.Clear();

    /// <summary>Dispose every cached texture and clear. Call from the owner's
    /// Shutdown/close path.</summary>
    public void DisposeAll()
    {
        foreach (var tex in _cache.Values) tex?.Dispose();
        _cache.Clear();
    }

    private void Log(string msg)
    {
        if (_logChannel != null) DebugLog.Log(_logChannel, $"TextureCache: {msg}");
    }
}
