namespace Necroking.Render;

/// <summary>
/// Small integer-lattice hash + [0,1) mapping shared by the procedural scatter
/// renderers (DeathFog jitter/phase, GrassTuft tuft placement). Previously mirrored
/// byte-for-byte as private CellHash/TileHash/HashToFloat in each renderer; kept
/// here so the constants stay in sync and placement/jitter remain stable.
/// </summary>
public static class HashNoise
{
    /// <summary>2D integer cell hash (identical to the 3D form with index 0).</summary>
    public static uint CellHash(int x, int y) => CellHash(x, y, 0);

    /// <summary>3D integer cell hash: 2D lattice + an index term (e.g. per-tuft slot).</summary>
    public static uint CellHash(int x, int y, int index)
    {
        uint h = (uint)x * 374761393u + (uint)y * 668265263u + (uint)index * 2654435769u;
        h = (h ^ (h >> 13)) * 1274126177u;
        h ^= h >> 16;
        return h;
    }

    /// <summary>Map a hash to a float in [0, 1).</summary>
    public static float ToFloat(uint h) => (h & 0xFFFFFFu) / (float)0xFFFFFFu;
}
