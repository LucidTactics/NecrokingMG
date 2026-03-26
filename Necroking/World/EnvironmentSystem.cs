using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Necroking.World;

public class ProcessSlot
{
    public string Kind { get; set; } = "";
    public string ResourceID { get; set; } = "";
    public bool IsEmpty => string.IsNullOrEmpty(Kind);
}

public class QueueEntry
{
    public string ResourceID { get; set; } = "";
    public int Count { get; set; }
}

public class BuildingProcessState
{
    public List<QueueEntry> InputQueue1 { get; set; } = new();
    public List<QueueEntry> InputQueue2 { get; set; } = new();
    public List<QueueEntry> OutputQueue { get; set; } = new();
    public float ProcessTimer { get; set; }
    public bool Processing { get; set; }

    public int TotalInput1() { int t = 0; foreach (var e in InputQueue1) t += e.Count; return t; }
    public int TotalInput2() { int t = 0; foreach (var e in InputQueue2) t += e.Count; return t; }
    public int TotalOutput() { int t = 0; foreach (var e in OutputQueue) t += e.Count; return t; }
}

public class EnvironmentObjectDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "Misc";
    public string TexturePath { get; set; } = "";
    public string HeightMapPath { get; set; } = "";
    public float SpriteWorldHeight { get; set; } = 4f;
    public float WorldHeight { get; set; }
    public float PivotX { get; set; } = 0.5f;
    public float PivotY { get; set; } = 1f;
    public float CollisionRadius { get; set; }
    public float CollisionOffsetX { get; set; }
    public float CollisionOffsetY { get; set; }
    public float Scale { get; set; } = 1f;
    public float PlacementScale { get; set; } = 1f;
    public string Group { get; set; } = "";
    public float GroupWeight { get; set; } = 1f;
    public bool IsBuilding { get; set; }
    public bool PlayerBuildable { get; set; }
    public int BuildingMaxHP { get; set; } = 100;
    public int BuildingProtection { get; set; }
    public int BuildingDefaultOwner { get; set; } = 1;
    public string BoundTriggerID { get; set; } = "";
    public ProcessSlot Input1 { get; set; } = new();
    public ProcessSlot Input2 { get; set; } = new();
    public ProcessSlot Output { get; set; } = new();
    public float ProcessTime { get; set; } = 10f;
    public int MaxInputQueue { get; set; } = 10;
    public int MaxOutputQueue { get; set; } = 10;
    public bool AutoSpawn { get; set; }
    public float SpawnOffsetX { get; set; }
    public float SpawnOffsetY { get; set; } = 1.5f;
}

public struct PlacedObject
{
    public ushort DefIndex;
    public float X, Y;
    public float Scale;
    public float Seed;
    public string ObjectID;
}

public struct PlacedObjectRuntime
{
    public int HP;
    public int Owner;
    public bool Alive;

    public PlacedObjectRuntime() { HP = 0; Owner = 1; Alive = true; }
}

public class EnvironmentSystem
{
    private float _worldMaxY = 1f;
    private readonly List<string> _categories = new();
    private readonly List<string> _groups = new();
    private readonly List<EnvironmentObjectDef> _defs = new();
    private readonly List<Texture2D?> _textures = new();
    private GraphicsDevice? _device;
    private readonly List<PlacedObject> _objects = new();
    private readonly List<PlacedObjectRuntime> _objectRuntime = new();
    private readonly List<BuildingProcessState> _processState = new();
    private int _nextObjectID;

    public void Init(float worldMaxY, GraphicsDevice? device = null) { _worldMaxY = worldMaxY; _device = device; }

    public int AddDef(EnvironmentObjectDef def) { _defs.Add(def); _textures.Add(null); return _defs.Count - 1; }
    public void RemoveDef(int index) { if (index >= 0 && index < _defs.Count) { _defs.RemoveAt(index); _textures.RemoveAt(index); } }
    public int DefCount => _defs.Count;
    public EnvironmentObjectDef GetDef(int idx) => _defs[idx];
    public int FindDef(string id) { for (int i = 0; i < _defs.Count; i++) if (_defs[i].Id == id) return i; return -1; }

    public int AddObject(ushort defIndex, float x, float y, float scale = 1f, float seed = -1f)
    {
        var obj = new PlacedObject
        {
            DefIndex = defIndex, X = x, Y = y, Scale = scale,
            Seed = seed < 0 ? Random.Shared.NextSingle() : seed,
            ObjectID = $"obj_{_nextObjectID++}"
        };
        _objects.Add(obj);
        _objectRuntime.Add(new PlacedObjectRuntime { HP = _defs[defIndex].BuildingMaxHP, Owner = _defs[defIndex].BuildingDefaultOwner, Alive = true });
        _processState.Add(new BuildingProcessState());
        return _objects.Count - 1;
    }

    public void RemoveObject(int index)
    {
        if (index < 0 || index >= _objects.Count) return;
        _objects.RemoveAt(index);
        if (index < _objectRuntime.Count) _objectRuntime.RemoveAt(index);
        if (index < _processState.Count) _processState.RemoveAt(index);
    }

    public void ClearObjects() { _objects.Clear(); _objectRuntime.Clear(); _processState.Clear(); }
    public int ObjectCount => _objects.Count;
    public PlacedObject GetObject(int idx) => _objects[idx];
    public PlacedObjectRuntime GetObjectRuntime(int idx) => _objectRuntime[idx];
    public BuildingProcessState GetProcessState(int idx) => _processState[idx];

    public void BakeCollisions(TileGrid grid)
    {
        foreach (var obj in _objects)
        {
            var def = _defs[obj.DefIndex];
            if (def.CollisionRadius > 0)
                grid.StampImpassableCircle(obj.X + def.CollisionOffsetX, obj.Y + def.CollisionOffsetY, def.CollisionRadius * obj.Scale);
        }
    }

    public IReadOnlyList<EnvironmentObjectDef> Defs => _defs;
    public IReadOnlyList<PlacedObject> Objects => _objects;
    public IReadOnlyList<string> Categories => _categories;
    public IReadOnlyList<string> Groups => _groups;

    public void LoadTextures(GraphicsDevice device)
    {
        _device = device;
        // Ensure texture list matches defs
        while (_textures.Count < _defs.Count) _textures.Add(null);

        for (int i = 0; i < _defs.Count; i++)
        {
            if (_textures[i] != null) continue;
            string path = _defs[i].TexturePath;
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) continue;
            try
            {
                using var stream = System.IO.File.OpenRead(path);
                _textures[i] = Texture2D.FromStream(device, stream);
            }
            catch { /* skip failed loads */ }
        }
    }

    public Texture2D? GetDefTexture(int defIdx)
    {
        if (defIdx < 0 || defIdx >= _textures.Count) return null;
        return _textures[defIdx];
    }

    /// <summary>
    /// Get render-ordered indices (sorted by Y position for depth).
    /// </summary>
    public List<int> GetRenderOrder()
    {
        var order = new List<int>(_objects.Count);
        for (int i = 0; i < _objects.Count; i++) order.Add(i);
        order.Sort((a, b) => _objects[a].Y.CompareTo(_objects[b].Y));
        return order;
    }
}
