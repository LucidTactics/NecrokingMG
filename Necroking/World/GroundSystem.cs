using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Necroking.World;

public class GroundTypeDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string TexturePath { get; set; } = "";
    public Color TintColor { get; set; } = Color.White;
}

public class GroundSystem
{
    private int _worldW, _worldH;
    private readonly List<GroundTypeDef> _types = new();
    private readonly List<Texture2D?> _textures = new();
    private byte[] _vertexMap = Array.Empty<byte>();

    public float TypeWarpStrength = 1.8f;
    public float UvWarpAmp = 0.4f;
    public float UvWarpFreq = 0.15f;
    public int DebugMode = 0;

    public int WorldW => _worldW;
    public int WorldH => _worldH;
    public int VertexW => _worldW + 1;
    public int VertexH => _worldH + 1;

    public void Init(int worldW, int worldH)
    {
        _worldW = worldW;
        _worldH = worldH;
        _vertexMap = new byte[VertexW * VertexH];
    }

    public int AddGroundType(GroundTypeDef def)
    {
        _types.Add(def);
        _textures.Add(null);
        return _types.Count - 1;
    }

    public int TypeCount => _types.Count;
    public GroundTypeDef GetTypeDef(int idx) => _types[idx];

    public void SetVertex(int vx, int vy, byte typeIndex)
    {
        if (vx >= 0 && vx < VertexW && vy >= 0 && vy < VertexH)
            _vertexMap[vy * VertexW + vx] = typeIndex;
    }

    public byte GetVertex(int vx, int vy) =>
        vx >= 0 && vx < VertexW && vy >= 0 && vy < VertexH
            ? _vertexMap[vy * VertexW + vx] : (byte)0;

    public void FillAll(byte typeIndex) => Array.Fill(_vertexMap, typeIndex);

    public byte[] GetVertexMap() => _vertexMap;
    public void SetVertexMap(byte[] map) { if (map.Length == _vertexMap.Length) Array.Copy(map, _vertexMap, map.Length); }

    public void RemoveType(int index)
    {
        if (index < 0 || index >= _types.Count) return;
        _types.RemoveAt(index);
        _textures.RemoveAt(index);
        // Remap vertex map
        for (int i = 0; i < _vertexMap.Length; i++)
        {
            if (_vertexMap[i] == index) _vertexMap[i] = 0;
            else if (_vertexMap[i] > index) _vertexMap[i]--;
        }
    }

    public void ClearTypes() { _types.Clear(); _textures.Clear(); }

    public void LoadTextures(GraphicsDevice device)
    {
        while (_textures.Count < _types.Count) _textures.Add(null);
        for (int i = 0; i < _types.Count; i++)
        {
            if (_textures[i] != null) continue;
            string path = _types[i].TexturePath;
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) continue;
            try
            {
                using var stream = System.IO.File.OpenRead(path);
                _textures[i] = Texture2D.FromStream(device, stream);
            }
            catch { }
        }
    }

    public Texture2D? GetTexture(int typeIdx)
    {
        if (typeIdx < 0 || typeIdx >= _textures.Count) return null;
        return _textures[typeIdx];
    }

    public Texture2D? CreateVertexMapTexture(GraphicsDevice device)
    {
        if (_vertexMap.Length == 0) return null;
        // Use Color format (RGBA) to avoid Alpha8 issues with SpriteBatch
        // Pack type index into R channel, set A=255 so it's not discarded
        var tex = new Texture2D(device, VertexW, VertexH, false, SurfaceFormat.Color);
        var colorData = new Microsoft.Xna.Framework.Color[_vertexMap.Length];
        for (int i = 0; i < _vertexMap.Length; i++)
            colorData[i] = new Microsoft.Xna.Framework.Color(_vertexMap[i], (byte)0, (byte)0, (byte)255);
        tex.SetData(colorData);
        return tex;
    }
}
