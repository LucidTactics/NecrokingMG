using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace Necroking.World;

public enum WallSegDir { Center = 0, N, S, E, W, NE, NW, SE, SW, Count }

public class WallSegmentDef
{
    public string SpritePath { get; set; } = "";
    public Rectangle SrcRect { get; set; }
    public Vector2 Offset { get; set; }
    public Vector2 Size { get; set; } = new(1f, 1f);
    public Vector2 Pivot { get; set; } = new(0.5f, 1f);
    public bool Enabled { get; set; }
}

public class WallVisualDef
{
    public string Name { get; set; } = "";
    public int MaxHP { get; set; } = 100;
    public int Protection { get; set; }
    public Color Color { get; set; } = new(130, 130, 130, 255);
    public WallSegmentDef[] Segments { get; set; } = new WallSegmentDef[(int)WallSegDir.Count];

    public WallVisualDef()
    {
        for (int i = 0; i < Segments.Length; i++)
            Segments[i] = new WallSegmentDef();
    }
}

public class WallSystem
{
    public const float WallScale = 4f;
    public const int WallStep = 4;

    private int _width, _height;
    private float _worldMaxY;
    private byte[] _types = Array.Empty<byte>();
    private short[] _hp = Array.Empty<short>();
    private readonly List<WallVisualDef> _defs = new();

    public int Width => _width;
    public int Height => _height;
    public List<WallVisualDef> Defs => _defs;
    public int DefCount => _defs.Count;

    public void Init(int w, int h, float worldMaxY)
    {
        _width = w;
        _height = h;
        _worldMaxY = worldMaxY;
        int size = w * h;
        _types = new byte[size];
        _hp = new short[size];
    }

    public static int SnapToWallGrid(int v) => (v / WallStep) * WallStep;

    public bool InBounds(int tx, int ty) => tx >= 0 && tx < _width && ty >= 0 && ty < _height;
    private int Idx(int tx, int ty) => ty * _width + tx;

    public void SetWall(int tx, int ty, byte typeIndex)
    {
        if (!InBounds(tx, ty) || typeIndex == 0 || typeIndex > _defs.Count) return;
        int i = Idx(tx, ty);
        _types[i] = typeIndex;
        _hp[i] = (short)_defs[typeIndex - 1].MaxHP;
    }

    public void ClearWall(int tx, int ty)
    {
        if (!InBounds(tx, ty)) return;
        int i = Idx(tx, ty);
        _types[i] = 0;
        _hp[i] = 0;
    }

    public byte GetWallType(int tx, int ty) => InBounds(tx, ty) ? _types[Idx(tx, ty)] : (byte)0;
    public int GetHP(int tx, int ty) => InBounds(tx, ty) ? _hp[Idx(tx, ty)] : 0;
    public bool IsAlive(int tx, int ty) => InBounds(tx, ty) && _types[Idx(tx, ty)] > 0 && _hp[Idx(tx, ty)] > 0;
    public bool IsWallTile(int tx, int ty) => InBounds(tx, ty) && _types[Idx(tx, ty)] > 0;

    public bool ApplyDamage(int tx, int ty, int damage)
    {
        if (!InBounds(tx, ty)) return false;
        int i = Idx(tx, ty);
        if (_types[i] == 0 || _hp[i] <= 0) return false;
        _hp[i] -= (short)damage;
        if (_hp[i] <= 0) { _hp[i] = 0; return true; }
        return false;
    }

    public void BakeWalls(TileGrid grid)
    {
        for (int ty = 0; ty < _height; ty++)
            for (int tx = 0; tx < _width; tx++)
                if (IsWallTile(tx, ty) && IsAlive(tx, ty))
                    grid.SetTerrain(tx, ty, TerrainType.Wall);
    }

    public byte[] GetTypes() => _types;
    public short[] GetHPArray() => _hp;

    public void SetFromData(byte[] types, short[] hps)
    {
        if (types.Length == _types.Length) Array.Copy(types, _types, types.Length);
        if (hps.Length == _hp.Length) Array.Copy(hps, _hp, hps.Length);
    }
}
