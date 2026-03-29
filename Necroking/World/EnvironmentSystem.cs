using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Core;

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

    // Building costs
    public int CostWood { get; set; }
    public int CostStone { get; set; }
    public int CostGold { get; set; }

    // Tint color (used by color harmonizer M04)
    public HdrColor TintColor { get; set; } = new(255, 255, 255, 255, 1f);

    // Foragable properties
    public bool IsForagable { get; set; }
    public string ForagableType { get; set; } = "";     // resource type name (e.g., "Mushroom", "Branch")
    public float RespawnTime { get; set; } = 180f;      // seconds (default 3 minutes)
    public float ScaleMin { get; set; } = 0.8f;         // random scale variation min
    public float ScaleMax { get; set; } = 1.2f;         // random scale variation max
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
    public bool Collected;         // foragable has been picked up
    public float RespawnTimer;     // countdown to respawn after collection

    public PlacedObjectRuntime() { HP = 0; Owner = 1; Alive = true; Collected = false; RespawnTimer = 0f; }
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
    public void ClearDefs() { _defs.Clear(); _textures.Clear(); }
    public int ObjectCount => _objects.Count;
    public PlacedObject GetObject(int idx) => _objects[idx];
    public PlacedObjectRuntime GetObjectRuntime(int idx) => _objectRuntime[idx];
    public BuildingProcessState GetProcessState(int idx) => _processState[idx];

    /// <summary>
    /// Collect a foragable object. Returns the ForagableType string, or null if not foragable/already collected.
    /// </summary>
    public string? CollectForagable(int objIdx)
    {
        if (objIdx < 0 || objIdx >= _objects.Count) return null;
        var def = _defs[_objects[objIdx].DefIndex];
        if (!def.IsForagable) return null;
        if (objIdx >= _objectRuntime.Count) return null;
        var rt = _objectRuntime[objIdx];
        if (rt.Collected) return null;

        rt.Collected = true;
        rt.RespawnTimer = def.RespawnTime;
        _objectRuntime[objIdx] = rt;
        return def.ForagableType;
    }

    /// <summary>
    /// Update foragable respawn timers. Call each frame with dt.
    /// </summary>
    public void UpdateForagables(float dt)
    {
        for (int i = 0; i < _objectRuntime.Count; i++)
        {
            if (!_objectRuntime[i].Collected) continue;
            var rt = _objectRuntime[i];
            rt.RespawnTimer -= dt;
            if (rt.RespawnTimer <= 0f)
            {
                rt.Collected = false;
                rt.RespawnTimer = 0f;
            }
            _objectRuntime[i] = rt;
        }
    }

    /// <summary>
    /// Check if an object is currently visible (not collected).
    /// </summary>
    public bool IsObjectVisible(int objIdx)
    {
        if (objIdx < 0 || objIdx >= _objects.Count) return false;
        if (objIdx < _objectRuntime.Count && _objectRuntime[objIdx].Collected) return false;
        if (objIdx < _objectRuntime.Count && !_objectRuntime[objIdx].Alive) return false;
        return true;
    }

    /// <summary>
    /// Check whether a new object of the given def can be placed at (x,y) without
    /// overlapping the collision radius of any existing object.
    /// Returns true if placement is valid (no overlap or def has no collision).
    /// </summary>
    public bool CanPlaceObject(int defIndex, float x, float y, float scale = 1f)
    {
        if (defIndex < 0 || defIndex >= _defs.Count) return false;
        var newDef = _defs[defIndex];
        float newRadius = newDef.CollisionRadius * scale;
        float newCX = x + newDef.CollisionOffsetX;
        float newCY = y + newDef.CollisionOffsetY;

        // If the new object has no collision radius, always allow placement
        if (newRadius <= 0f) return true;

        for (int i = 0; i < _objects.Count; i++)
        {
            var obj = _objects[i];
            var existDef = _defs[obj.DefIndex];
            float existRadius = existDef.CollisionRadius * obj.Scale;
            if (existRadius <= 0f) continue;

            float existCX = obj.X + existDef.CollisionOffsetX;
            float existCY = obj.Y + existDef.CollisionOffsetY;

            float dx = newCX - existCX;
            float dy = newCY - existCY;
            float minDist = newRadius + existRadius;
            if (dx * dx + dy * dy < minDist * minDist)
                return false;
        }
        return true;
    }

    public void BakeCollisions(TileGrid grid)
    {
        // Reset tiered cost fields to base terrain before stamping obstacles
        grid.RebuildTieredCostFields();

        foreach (var obj in _objects)
        {
            var def = _defs[obj.DefIndex];
            if (def.CollisionRadius > 0)
            {
                // Scale offset and radius by def.Scale * obj.Scale (matching C++)
                float es = def.Scale * obj.Scale;
                float cx = obj.X + def.CollisionOffsetX * es;
                float cy = obj.Y + def.CollisionOffsetY * es;
                float cr = def.CollisionRadius * es;

                grid.StampImpassableCircle(cx, cy, cr);
                for (int tier = 0; tier < TerrainCosts.NumSizeTiers; tier++)
                {
                    grid.StampImpassableCircleTier(tier, cx, cy, cr);
                }
            }
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
                _textures[i] = Necroking.Render.TextureUtil.LoadPremultiplied(device, stream);
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
    /// Reload texture for a single def (e.g. after changing TexturePath in the editor).
    /// </summary>
    public void ReloadDefTexture(int defIdx)
    {
        if (_device == null || defIdx < 0 || defIdx >= _defs.Count) return;
        while (_textures.Count <= defIdx) _textures.Add(null);
        _textures[defIdx]?.Dispose();
        _textures[defIdx] = null;
        string path = _defs[defIdx].TexturePath;
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return;
        try
        {
            using var stream = System.IO.File.OpenRead(path);
            _textures[defIdx] = Necroking.Render.TextureUtil.LoadPremultiplied(_device, stream);
        }
        catch { /* skip failed loads */ }
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
