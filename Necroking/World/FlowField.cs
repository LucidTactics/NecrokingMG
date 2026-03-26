using System;
using System.Collections.Generic;
using Necroking.Core;

namespace Necroking.World;

public enum FlowDir : byte
{
    None = 0, N, NE, E, SE, S, SW, W, NW
}

public static class FlowDirUtil
{
    private static readonly Vec2[] Dirs =
    {
        Vec2.Zero,                        // None
        new(0, -1),                       // N
        new(0.707f, -0.707f),             // NE
        new(1, 0),                        // E
        new(0.707f, 0.707f),              // SE
        new(0, 1),                        // S
        new(-0.707f, 0.707f),             // SW
        new(-1, 0),                       // W
        new(-0.707f, -0.707f),            // NW
    };

    public static Vec2 ToVec(FlowDir d) => Dirs[(int)d];
}

public class FlowField
{
    public GridCoord Destination;
    public float[] IntegrationField = Array.Empty<float>();
    public FlowDir[] DirectionField = Array.Empty<FlowDir>();
    public uint FrameAccessed;
    public bool Dirty;
}

public class FlowFieldManager
{
    private TileGrid? _grid;
    private readonly Dictionary<long, FlowField> _cache = new();

    public void Init(TileGrid grid) { _grid = grid; }

    private static long MakeKey(int dx, int dy) => ((long)dy << 32) | (uint)dx;

    public FlowField? GetFlowField(GridCoord destination, uint currentFrame)
    {
        if (_grid == null) return null;
        long key = MakeKey(destination.X, destination.Y);

        if (_cache.TryGetValue(key, out var ff))
        {
            ff.FrameAccessed = currentFrame;
            return ff;
        }

        ff = new FlowField { Destination = destination, FrameAccessed = currentFrame };
        int size = _grid.Width * _grid.Height;
        ff.IntegrationField = new float[size];
        ff.DirectionField = new FlowDir[size];
        Array.Fill(ff.IntegrationField, GameConstants.InfCost);

        ComputeIntegrationField(ff);
        ComputeDirectionField(ff);

        _cache[key] = ff;
        return ff;
    }

    public void InvalidateAll() { _cache.Clear(); }

    public void EvictIfNeeded(int maxCached = 16)
    {
        while (_cache.Count > maxCached)
        {
            long oldestKey = 0;
            uint oldestFrame = uint.MaxValue;
            foreach (var (k, v) in _cache)
                if (v.FrameAccessed < oldestFrame) { oldestFrame = v.FrameAccessed; oldestKey = k; }
            _cache.Remove(oldestKey);
        }
    }

    public Vec2 SampleDirection(FlowField field, Vec2 worldPos)
    {
        if (_grid == null) return Vec2.Zero;
        int gx = (int)MathF.Floor(worldPos.X);
        int gy = (int)MathF.Floor(worldPos.Y);
        if (!_grid.InBounds(gx, gy)) return Vec2.Zero;
        return FlowDirUtil.ToVec(field.DirectionField[_grid.Index(gx, gy)]);
    }

    private void ComputeIntegrationField(FlowField ff)
    {
        if (_grid == null) return;
        int w = _grid.Width, h = _grid.Height;
        var dest = ff.Destination;
        if (!_grid.InBounds(dest.X, dest.Y)) return;

        ff.IntegrationField[_grid.Index(dest.X, dest.Y)] = 0f;
        var queue = new Queue<(int x, int y)>();
        queue.Enqueue((dest.X, dest.Y));

        int[] dx = { 1, -1, 0, 0 };
        int[] dy = { 0, 0, 1, -1 };

        while (queue.Count > 0)
        {
            var (cx, cy) = queue.Dequeue();
            float currentCost = ff.IntegrationField[_grid.Index(cx, cy)];

            for (int d = 0; d < 4; d++)
            {
                int nx = cx + dx[d], ny = cy + dy[d];
                if (!_grid.InBounds(nx, ny)) continue;
                byte tileCost = _grid.GetCost(nx, ny);
                if (tileCost == 255) continue;

                float newCost = currentCost + tileCost;
                int ni = _grid.Index(nx, ny);
                if (newCost < ff.IntegrationField[ni])
                {
                    ff.IntegrationField[ni] = newCost;
                    queue.Enqueue((nx, ny));
                }
            }
        }
    }

    private void ComputeDirectionField(FlowField ff)
    {
        if (_grid == null) return;
        int w = _grid.Width, h = _grid.Height;

        (int dx, int dy, FlowDir dir)[] neighbors =
        {
            (0, -1, FlowDir.N), (1, -1, FlowDir.NE), (1, 0, FlowDir.E), (1, 1, FlowDir.SE),
            (0, 1, FlowDir.S), (-1, 1, FlowDir.SW), (-1, 0, FlowDir.W), (-1, -1, FlowDir.NW)
        };

        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int idx = _grid.Index(x, y);
                float bestCost = ff.IntegrationField[idx];
                FlowDir bestDir = FlowDir.None;

                foreach (var (ndx, ndy, dir) in neighbors)
                {
                    int nx = x + ndx, ny = y + ndy;
                    if (!_grid.InBounds(nx, ny)) continue;
                    float nc = ff.IntegrationField[_grid.Index(nx, ny)];
                    if (nc < bestCost) { bestCost = nc; bestDir = dir; }
                }

                ff.DirectionField[idx] = bestDir;
            }
    }
}
