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

    public static AtlasID ResolveAtlasName(string name)
    {
        for (int i = 0; i < _dynamicNames.Length; i++)
            if (_dynamicNames[i] == name) return (AtlasID)i;
        return AtlasID.Vampire;
    }

    /// <summary>Total number of atlases (may exceed AtlasID.Count if new ones were found).</summary>
    public static int TotalCount => _dynamicNames.Length;
}
