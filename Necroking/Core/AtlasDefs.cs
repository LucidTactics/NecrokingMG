using System;
using System.Collections.Generic;
using System.IO;

namespace Necroking.Core;

public enum AtlasID { Vampire = 0, Navarre = 1, Simple = 2, Zombies = 3, Count = 4 }

public static class AtlasDefs
{
    // Default hardcoded names (fallback)
    private static readonly string[] DefaultNames =
    {
        "VampireFaction",
        "Navarre_Units",
        "simple_sprites",
        "Navarre_Zombies"
    };

    // Dynamic names — refreshed from disk
    private static string[] _dynamicNames = DefaultNames;

    /// <summary>All known atlas names (refreshed from disk on ScanSpritesDirectory).</summary>
    public static string[] Names => _dynamicNames;

    /// <summary>
    /// Scan the sprites directory for .spritemeta files and rebuild the atlas list.
    /// New atlases are appended after the default ones.
    /// Files named "&lt;base&gt;__N.spritemeta" (e.g. ZombieAnimals__1.spritemeta) are
    /// overflow extensions of the base sheet — they do NOT appear in the atlas list.
    /// The atlas loader picks them up via <see cref="FindExtensionSheets"/>.
    /// </summary>
    public static void ScanSpritesDirectory(string? spritesDir = null)
    {
        spritesDir ??= GamePaths.Resolve(GamePaths.SpritesDir);
        var names = new List<string>(DefaultNames);

        if (Directory.Exists(spritesDir))
        {
            foreach (var metaPath in Directory.GetFiles(spritesDir, "*.spritemeta"))
            {
                string name = Path.GetFileNameWithoutExtension(metaPath);
                // Overflow sheet — hidden, loaded as extension of its base.
                if (IsExtensionName(name)) continue;
                // Check that a matching PNG exists
                string pngPath = Path.Combine(spritesDir, name + ".png");
                if (!File.Exists(pngPath)) continue;

                // Don't duplicate defaults
                if (!names.Contains(name))
                    names.Add(name);
            }
        }

        _dynamicNames = names.ToArray();
    }

    /// <summary>True if the sprite-sheet basename ends with "__N" where N is digits,
    /// indicating it's an overflow extension of a base sheet (e.g. ZombieAnimals__1).</summary>
    public static bool IsExtensionName(string name)
    {
        int i = name.LastIndexOf("__", StringComparison.Ordinal);
        if (i < 0 || i + 2 >= name.Length) return false;
        for (int k = i + 2; k < name.Length; k++)
            if (name[k] < '0' || name[k] > '9') return false;
        return true;
    }

    /// <summary>Enumerate "&lt;baseName&gt;__1.spritemeta", __2, … in order, stopping
    /// at the first missing number. Both the png and spritemeta must exist.</summary>
    public static IEnumerable<(string pngPath, string metaPath)> FindExtensionSheets(
        string baseName, string? spritesDir = null)
    {
        spritesDir ??= GamePaths.Resolve(GamePaths.SpritesDir);
        if (!Directory.Exists(spritesDir)) yield break;
        for (int n = 1; ; n++)
        {
            string extName = $"{baseName}__{n}";
            string pngPath = Path.Combine(spritesDir, extName + ".png");
            string metaPath = Path.Combine(spritesDir, extName + ".spritemeta");
            if (!File.Exists(pngPath) || !File.Exists(metaPath)) yield break;
            yield return (pngPath, metaPath);
        }
    }

    /// <summary>Enumerate "&lt;baseName&gt;__N.animationmeta" in order, stopping at
    /// the first missing number.</summary>
    public static IEnumerable<string> FindExtensionAnimMeta(string baseName, string? spritesDir = null)
    {
        spritesDir ??= GamePaths.Resolve(GamePaths.SpritesDir);
        if (!Directory.Exists(spritesDir)) yield break;
        for (int n = 1; ; n++)
        {
            string path = Path.Combine(spritesDir, $"{baseName}__{n}.animationmeta");
            if (!File.Exists(path)) yield break;
            yield return path;
        }
    }

    public static AtlasID ResolveAtlasName(string name)
    {
        for (int i = 0; i < _dynamicNames.Length; i++)
            if (_dynamicNames[i] == name) return (AtlasID)i;
        return AtlasID.Vampire;
    }

    /// <summary>Total number of atlases (may exceed AtlasID.Count if new ones were found).</summary>
    public static int TotalCount => _dynamicNames.Length;
}
