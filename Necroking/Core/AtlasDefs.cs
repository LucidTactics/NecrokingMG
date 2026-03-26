namespace Necroking.Core;

public enum AtlasID { Vampire = 0, Navarre = 1, Simple = 2, Zombies = 3, Count = 4 }

public static class AtlasDefs
{
    public static readonly string[] Names =
    {
        "VampireFaction",
        "Navarre_Units",
        "simple_sprites",
        "Navarre_Zombies"
    };

    public static AtlasID ResolveAtlasName(string name)
    {
        for (int i = 0; i < Names.Length; i++)
            if (Names[i] == name) return (AtlasID)i;
        return AtlasID.Vampire;
    }
}
