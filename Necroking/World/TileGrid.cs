using System;
using System.Collections.Generic;
using Necroking.Core;

namespace Necroking.World;

public enum TerrainType : byte { Open = 0, Rough = 1, Water = 2, Wall = 3, Count }

public static class TerrainCosts
{
    public const int NumSizeTiers = 3;
    public static readonly float[] SizeTierRadius = { 0.50f, 1.25f, 1.75f };

    public static float GetCost(TerrainType t) => t switch
    {
        TerrainType.Open => 1f,
        TerrainType.Rough => 3f,
        TerrainType.Water => 10f,
        TerrainType.Wall => 255f,
        _ => 1f
    };

    public static int SizeToTier(int unitSize) => unitSize switch
    {
        <= 2 => 0,
        <= 4 => 1,
        _ => 2
    };
}

public class TileGrid
{
    public int Width { get; private set; }
    public int Height { get; private set; }

    private TerrainType[] _terrain = Array.Empty<TerrainType>();
    private float[] _heightMap = Array.Empty<float>();
    private byte[] _costField = Array.Empty<byte>();
    private byte[][] _costFieldTier = new byte[TerrainCosts.NumSizeTiers][];

    public void Init(int w, int h)
    {
        Width = w;
        Height = h;
        int size = w * h;
        _terrain = new TerrainType[size];
        _heightMap = new float[size];
        _costField = new byte[size];
        for (int i = 0; i < TerrainCosts.NumSizeTiers; i++)
            _costFieldTier[i] = new byte[size];
        Array.Fill(_costField, (byte)1);
    }

    public bool InBounds(int x, int y) => x >= 0 && x < Width && y >= 0 && y < Height;
    public int Index(int x, int y) => y * Width + x;

    public void SetTerrain(int x, int y, TerrainType t)
    {
        if (InBounds(x, y)) _terrain[Index(x, y)] = t;
    }

    public TerrainType GetTerrain(int x, int y) =>
        InBounds(x, y) ? _terrain[Index(x, y)] : TerrainType.Wall;

    public float GetHeightAt(int x, int y) =>
        InBounds(x, y) ? _heightMap[Index(x, y)] : 0f;

    public float GetHeightInterp(float worldX, float worldY)
    {
        int x = (int)MathF.Floor(worldX);
        int y = (int)MathF.Floor(worldY);
        float fx = worldX - x;
        float fy = worldY - y;

        float h00 = GetHeightAt(x, y);
        float h10 = GetHeightAt(x + 1, y);
        float h01 = GetHeightAt(x, y + 1);
        float h11 = GetHeightAt(x + 1, y + 1);

        float h0 = h00 + (h10 - h00) * fx;
        float h1 = h01 + (h11 - h01) * fx;
        return h0 + (h1 - h0) * fy;
    }

    public byte GetCost(int x, int y, int tier = 0)
    {
        if (!InBounds(x, y)) return 255;
        if (tier == 0) return _costField[Index(x, y)];
        return _costFieldTier[tier] != null ? _costFieldTier[tier][Index(x, y)] : _costField[Index(x, y)];
    }

    public void RebuildTieredCostFields()
    {
        for (int t = 0; t < TerrainCosts.NumSizeTiers; t++)
        {
            if (_costFieldTier[t] == null) _costFieldTier[t] = new byte[Width * Height];
            Array.Copy(_costField, _costFieldTier[t], _costField.Length);
        }
    }

    public void RebuildCostField()
    {
        for (int i = 0; i < Width * Height; i++)
        {
            float c = TerrainCosts.GetCost(_terrain[i]);
            _costField[i] = c >= 255f ? (byte)255 : (byte)Math.Min(254, Math.Max(1, (int)c));
        }
    }

    public void StampImpassableCircle(float worldX, float worldY, float radius)
    {
        int minTX = Math.Max(0, (int)MathF.Floor(worldX - radius));
        int maxTX = Math.Min(Width - 1, (int)MathF.Ceiling(worldX + radius));
        int minTY = Math.Max(0, (int)MathF.Floor(worldY - radius));
        int maxTY = Math.Min(Height - 1, (int)MathF.Ceiling(worldY + radius));

        float r2 = radius * radius;
        for (int ty = minTY; ty <= maxTY; ty++)
            for (int tx = minTX; tx <= maxTX; tx++)
            {
                float dx = tx + 0.5f - worldX;
                float dy = ty + 0.5f - worldY;
                if (dx * dx + dy * dy <= r2)
                    _costField[Index(tx, ty)] = 255;
            }
    }

    public GridCoord WorldToGrid(Vec2 worldPos) =>
        new((int)MathF.Floor(worldPos.X / GameConstants.TileSize),
            (int)MathF.Floor(worldPos.Y / GameConstants.TileSize));

    public Vec2 GridToWorld(int gx, int gy) =>
        new(gx * GameConstants.TileSize + 0.5f, gy * GameConstants.TileSize + 0.5f);

    public TerrainType[] GetTerrainArray() => _terrain;
    public float[] GetHeightArray() => _heightMap;
    public byte[] GetCostArray() => _costField;
}
