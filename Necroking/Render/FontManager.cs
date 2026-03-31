using System;
using System.Collections.Generic;
using System.IO;
using FontStashSharp;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Necroking.Render;

/// <summary>
/// Manages runtime font loading via FontStashSharp.
/// Loads .ttf files from assets/fonts/ and provides fonts at any size on demand.
/// Replaces the pre-baked SpriteFont system for dynamic text rendering.
/// </summary>
public class FontManager
{
    private readonly Dictionary<string, FontSystem> _fontSystems = new();
    private FontSystem? _defaultSystem;
    private string _defaultFamily = "";

    /// <summary>Available font family names.</summary>
    public IReadOnlyCollection<string> FontFamilies => _fontSystems.Keys;

    /// <summary>The default font family name.</summary>
    public string DefaultFamily => _defaultFamily;

    /// <summary>Load all .ttf files from a directory. Each file becomes a font family.</summary>
    public void LoadFontsFromDirectory(string dirPath)
    {
        if (!Directory.Exists(dirPath)) return;

        foreach (var file in Directory.GetFiles(dirPath, "*.ttf"))
        {
            string familyName = Path.GetFileNameWithoutExtension(file);
            try
            {
                var fs = new FontSystem();
                fs.AddFont(File.ReadAllBytes(file));
                _fontSystems[familyName] = fs;

                // First loaded font is the default
                if (_defaultSystem == null)
                {
                    _defaultSystem = fs;
                    _defaultFamily = familyName;
                }
            }
            catch (Exception ex)
            {
                Core.DebugLog.Log("fonts", $"Failed to load font {file}: {ex.Message}");
            }
        }

        Core.DebugLog.Log("fonts", $"Loaded {_fontSystems.Count} fonts: {string.Join(", ", _fontSystems.Keys)}");
    }

    /// <summary>Set which font family is the default.</summary>
    public void SetDefault(string familyName)
    {
        if (_fontSystems.TryGetValue(familyName, out var fs))
        {
            _defaultSystem = fs;
            _defaultFamily = familyName;
        }
    }

    /// <summary>Get a font at the specified size from the given family (or default).</summary>
    public SpriteFontBase? GetFont(int size, string? family = null)
    {
        size = Math.Clamp(size, 4, 200);
        FontSystem? fs = null;
        if (!string.IsNullOrEmpty(family) && _fontSystems.TryGetValue(family, out fs))
            return fs.GetFont(size);
        if (_defaultSystem != null)
            return _defaultSystem.GetFont(size);
        return null;
    }

    /// <summary>Get the FontSystem for a family (for advanced usage).</summary>
    public FontSystem? GetFontSystem(string? family = null)
    {
        if (!string.IsNullOrEmpty(family) && _fontSystems.TryGetValue(family!, out var fs))
            return fs;
        return _defaultSystem;
    }

    /// <summary>Check if any fonts are loaded.</summary>
    public bool HasFonts => _defaultSystem != null;

    /// <summary>Get font family names as an array (for dropdowns).</summary>
    public string[] GetFamilyNames()
    {
        var names = new string[_fontSystems.Count];
        _fontSystems.Keys.CopyTo(names, 0);
        Array.Sort(names);
        return names;
    }
}
