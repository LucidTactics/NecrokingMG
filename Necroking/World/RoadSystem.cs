using System;
using System.Collections.Generic;
using Necroking.Core;

namespace Necroking.World;

public class RoadTextureDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string TexturePath { get; set; } = "";
}

public class RoadControlPoint
{
    public Vec2 Position { get; set; }
    public float Width { get; set; } = 2f;
}

public class RoadInstance
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int TextureDefIndex { get; set; }
    public int RenderOrder { get; set; }
    public bool Closed { get; set; }
    public float EdgeSoftness { get; set; } = 0.08f;
    public float TextureScale { get; set; } = 1f;
    public int RimTextureDefIndex { get; set; } = -1;
    public float RimWidth { get; set; } = 0.5f;
    public float RimTextureScale { get; set; } = 1f;
    public float RimEdgeSoftness { get; set; } = 0.15f;
    public List<RoadControlPoint> Points { get; set; } = new();
}

public class RoadJunction
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public Vec2 Position { get; set; }
    public float Radius { get; set; } = 3f;
    public int TextureDefIndex { get; set; }
    public float TextureScale { get; set; } = 1f;
    public float EdgeSoftness { get; set; } = 0.15f;
}

public class RoadSystem
{
    private readonly List<RoadTextureDef> _textureDefs = new();
    private readonly List<RoadInstance> _roads = new();
    private readonly List<RoadJunction> _junctions = new();

    public void Init() { }

    public int AddTextureDef(RoadTextureDef def) { _textureDefs.Add(def); return _textureDefs.Count - 1; }
    public int TextureDefCount => _textureDefs.Count;
    public RoadTextureDef GetTextureDef(int i) => _textureDefs[i];
    public IReadOnlyList<RoadTextureDef> TextureDefs => _textureDefs;

    public int AddRoad() { var r = new RoadInstance { Id = $"road_{_roads.Count}" }; _roads.Add(r); return _roads.Count - 1; }
    public void RemoveRoad(int index) { if (index >= 0 && index < _roads.Count) _roads.RemoveAt(index); }
    public int RoadCount => _roads.Count;
    public RoadInstance GetRoad(int i) => _roads[i];
    public IReadOnlyList<RoadInstance> Roads => _roads;
    public void SetRoads(List<RoadInstance> roads) { _roads.Clear(); _roads.AddRange(roads); }

    public int AddJunction(Vec2 position)
    {
        var j = new RoadJunction { Id = $"junction_{_junctions.Count}", Position = position };
        _junctions.Add(j);
        return _junctions.Count - 1;
    }
    public void RemoveJunction(int index) { if (index >= 0 && index < _junctions.Count) _junctions.RemoveAt(index); }
    public int JunctionCount => _junctions.Count;
    public RoadJunction GetJunction(int i) => _junctions[i];
    public IReadOnlyList<RoadJunction> Junctions => _junctions;

    public static Vec2 CatmullRom(Vec2 p0, Vec2 p1, Vec2 p2, Vec2 p3, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return new Vec2(
            0.5f * (2f * p1.X + (-p0.X + p2.X) * t + (2f * p0.X - 5f * p1.X + 4f * p2.X - p3.X) * t2 + (-p0.X + 3f * p1.X - 3f * p2.X + p3.X) * t3),
            0.5f * (2f * p1.Y + (-p0.Y + p2.Y) * t + (2f * p0.Y - 5f * p1.Y + 4f * p2.Y - p3.Y) * t2 + (-p0.Y + 3f * p1.Y - 3f * p2.Y + p3.Y) * t3)
        );
    }
}
