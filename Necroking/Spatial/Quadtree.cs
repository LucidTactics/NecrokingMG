using System;
using System.Collections.Generic;
using Necroking.Core;

namespace Necroking.Spatial;

public struct AABB
{
    public float MinX, MinY, MaxX, MaxY;

    public AABB(float minX, float minY, float maxX, float maxY)
    {
        MinX = minX; MinY = minY; MaxX = maxX; MaxY = maxY;
    }

    public bool Contains(Vec2 p) => p.X >= MinX && p.X <= MaxX && p.Y >= MinY && p.Y <= MaxY;

    public bool Intersects(AABB o) => !(o.MaxX < MinX || o.MinX > MaxX || o.MaxY < MinY || o.MinY > MaxY);

    public bool IntersectsCircle(Vec2 center, float radius)
    {
        float closestX = MathUtil.Clamp(center.X, MinX, MaxX);
        float closestY = MathUtil.Clamp(center.Y, MinY, MaxY);
        float dx = center.X - closestX;
        float dy = center.Y - closestY;
        return (dx * dx + dy * dy) <= (radius * radius);
    }

    public Vec2 Center => new((MinX + MaxX) * 0.5f, (MinY + MaxY) * 0.5f);
}

public class Quadtree
{
    private const int MaxDepth = 8;
    private const int MaxPerLeaf = 16;

    private struct Entry
    {
        public Vec2 Pos;
        public uint Id;
    }

    private struct Node
    {
        public AABB Bounds;
        public int FirstChild; // -1 = leaf
        public int EntryStart;
        public int EntryCount;
    }

    private readonly List<Node> _nodes = new();
    private readonly List<Entry> _entries = new();

    public bool IsEmpty => _nodes.Count == 0;

    public void Build(ReadOnlySpan<Vec2> positions, ReadOnlySpan<uint> ids, AABB worldBounds)
    {
        _nodes.Clear();
        _entries.Clear();

        int count = positions.Length;
        if (count == 0) return;

        for (int i = 0; i < count; i++)
            _entries.Add(new Entry { Pos = positions[i], Id = ids[i] });

        _nodes.Add(new Node { Bounds = worldBounds, FirstChild = -1, EntryStart = 0, EntryCount = count });
        Subdivide(0, 0);
    }

    public int QueryRadius(Vec2 center, float radius, List<uint> results)
    {
        if (_nodes.Count == 0) return 0;

        int found = 0;
        float r2 = radius * radius;
        Span<int> stack = stackalloc int[64];
        int stackSize = 0;
        stack[stackSize++] = 0;

        while (stackSize > 0)
        {
            int ni = stack[--stackSize];
            var node = _nodes[ni];

            if (!node.Bounds.IntersectsCircle(center, radius)) continue;

            if (node.FirstChild == -1)
            {
                for (int i = 0; i < node.EntryCount; i++)
                {
                    var e = _entries[node.EntryStart + i];
                    var diff = e.Pos - center;
                    if (diff.LengthSq() <= r2)
                    {
                        results.Add(e.Id);
                        found++;
                    }
                }
            }
            else
            {
                for (int c = 0; c < 4 && stackSize < 64; c++)
                    stack[stackSize++] = node.FirstChild + c;
            }
        }

        return found;
    }

    public int QueryAABB(AABB area, List<uint> results)
    {
        if (_nodes.Count == 0) return 0;

        int found = 0;
        Span<int> stack = stackalloc int[64];
        int stackSize = 0;
        stack[stackSize++] = 0;

        while (stackSize > 0)
        {
            int ni = stack[--stackSize];
            var node = _nodes[ni];

            if (!node.Bounds.Intersects(area)) continue;

            if (node.FirstChild == -1)
            {
                for (int i = 0; i < node.EntryCount; i++)
                {
                    var e = _entries[node.EntryStart + i];
                    if (area.Contains(e.Pos))
                    {
                        results.Add(e.Id);
                        found++;
                    }
                }
            }
            else
            {
                for (int c = 0; c < 4 && stackSize < 64; c++)
                    stack[stackSize++] = node.FirstChild + c;
            }
        }

        return found;
    }

    private void Subdivide(int nodeIdx, int depth)
    {
        var node = _nodes[nodeIdx];
        if (node.EntryCount <= MaxPerLeaf || depth >= MaxDepth) return;

        var mid = node.Bounds.Center;
        int start = node.EntryStart;
        int total = node.EntryCount;

        int[] counts = new int[4];
        for (int i = 0; i < total; i++)
        {
            var p = _entries[start + i].Pos;
            int q = (p.X >= mid.X ? 1 : 0) + (p.Y >= mid.Y ? 2 : 0);
            counts[q]++;
        }

        int[] offsets = { 0, counts[0], counts[0] + counts[1], counts[0] + counts[1] + counts[2] };
        int[] writeIdx = { offsets[0], offsets[1], offsets[2], offsets[3] };
        var sorted = new Entry[total];

        for (int i = 0; i < total; i++)
        {
            var p = _entries[start + i].Pos;
            int q = (p.X >= mid.X ? 1 : 0) + (p.Y >= mid.Y ? 2 : 0);
            sorted[writeIdx[q]++] = _entries[start + i];
        }

        for (int i = 0; i < total; i++)
            _entries[start + i] = sorted[i];

        int firstChild = _nodes.Count;
        node.FirstChild = firstChild;
        _nodes[nodeIdx] = node;

        AABB[] childBounds =
        {
            new(node.Bounds.MinX, node.Bounds.MinY, mid.X, mid.Y),
            new(mid.X, node.Bounds.MinY, node.Bounds.MaxX, mid.Y),
            new(node.Bounds.MinX, mid.Y, mid.X, node.Bounds.MaxY),
            new(mid.X, mid.Y, node.Bounds.MaxX, node.Bounds.MaxY)
        };

        for (int q = 0; q < 4; q++)
            _nodes.Add(new Node { Bounds = childBounds[q], FirstChild = -1, EntryStart = start + offsets[q], EntryCount = counts[q] });

        for (int q = 0; q < 4; q++)
            if (counts[q] > 0)
                Subdivide(firstChild + q, depth + 1);
    }
}
