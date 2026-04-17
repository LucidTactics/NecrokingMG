using System;
using System.Collections.Generic;
using Necroking.Core;

namespace Necroking.World;

/// <summary>
/// Uniform bucket-grid spatial index over EnvironmentSystem's placed objects.
/// Rebuilt only when the env set changes (tree placed/destroyed/collected), so
/// per-frame ORCA queries are cheap.
///
/// Index stores the object's collision-centre bucket. Queries widen the lookup
/// range by <see cref="MaxObjRadius"/> so objects whose body straddles a bucket
/// boundary are still returned.
/// </summary>
public class EnvSpatialIndex
{
    private const float CellSize = 4f;

    private readonly List<List<int>> _buckets = new();
    private int _w, _h;

    /// <summary>Largest collision radius currently in the index. Used to widen queries.</summary>
    public float MaxObjRadius { get; private set; }

    /// <summary>Entry data for a bucket: world-space centre + effective radius.</summary>
    public readonly struct Entry
    {
        public readonly int ObjectIndex;
        public readonly float CX, CY;
        public readonly float Radius;
        public Entry(int idx, float cx, float cy, float r) { ObjectIndex = idx; CX = cx; CY = cy; Radius = r; }
    }

    private readonly List<Entry> _entries = new();

    public void Rebuild(EnvironmentSystem env, int worldW, int worldH)
    {
        _w = Math.Max(1, (int)MathF.Ceiling(worldW / CellSize));
        _h = Math.Max(1, (int)MathF.Ceiling(worldH / CellSize));
        int total = _w * _h;

        // Grow bucket list if needed; always clear each bucket in-place.
        while (_buckets.Count < total) _buckets.Add(new List<int>());
        for (int i = 0; i < total; i++) _buckets[i].Clear();
        _entries.Clear();
        MaxObjRadius = 0f;

        for (int i = 0; i < env.ObjectCount; i++)
        {
            var rt = env.GetObjectRuntime(i);
            if (rt.Collected || !rt.Alive) continue;

            var obj = env.GetObject(i);
            var def = env.GetDef(obj.DefIndex);
            if (def.CollisionRadius <= 0f) continue;

            float es = def.Scale * obj.Scale;
            float cx = obj.X + def.CollisionOffsetX * es;
            float cy = obj.Y + def.CollisionOffsetY * es;
            float cr = def.CollisionRadius * es;

            int entryIdx = _entries.Count;
            _entries.Add(new Entry(i, cx, cy, cr));
            if (cr > MaxObjRadius) MaxObjRadius = cr;

            int bx = (int)(cx / CellSize);
            int by = (int)(cy / CellSize);
            if (bx < 0 || bx >= _w || by < 0 || by >= _h) continue;
            _buckets[by * _w + bx].Add(entryIdx);
        }
    }

    /// <summary>
    /// Append every entry whose bucket could contain an object within
    /// <paramref name="radius"/> of <paramref name="pos"/>. Caller must still
    /// apply the real circle distance check — buckets overshoot.
    /// </summary>
    public void QueryRadius(Vec2 pos, float radius, List<Entry> results)
    {
        if (_w == 0 || _h == 0) return;

        float r = radius + MaxObjRadius;
        int minBx = Math.Max(0, (int)MathF.Floor((pos.X - r) / CellSize));
        int maxBx = Math.Min(_w - 1, (int)MathF.Floor((pos.X + r) / CellSize));
        int minBy = Math.Max(0, (int)MathF.Floor((pos.Y - r) / CellSize));
        int maxBy = Math.Min(_h - 1, (int)MathF.Floor((pos.Y + r) / CellSize));

        for (int by = minBy; by <= maxBy; by++)
        {
            int rowBase = by * _w;
            for (int bx = minBx; bx <= maxBx; bx++)
            {
                var bucket = _buckets[rowBase + bx];
                for (int k = 0; k < bucket.Count; k++)
                    results.Add(_entries[bucket[k]]);
            }
        }
    }
}
