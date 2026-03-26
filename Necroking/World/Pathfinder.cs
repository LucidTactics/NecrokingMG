using System;
using System.Collections.Generic;
using Necroking.Core;

namespace Necroking.World;

public enum PathDecision : byte
{
    None = 0, ImagChunkPersist, ImagChunkRecompute, TileFlow, TileFlowBadBeeline,
    SameSectorImagChunk, BorderFlow, BFSFallback, TierFallback, ImagChunkFallback,
    BoundaryEscape, Beeline, Unreachable, UnreachableImagChunk, UnreachableTierFallback, NoFlow
}

public class Pathfinder
{
    public const int SectorSize = 64;

    private TileGrid? _grid;
    private int _sectorCountX, _sectorCountY;

    // Per-tier sector connectivity: [tier][sectorIdx] = {N, E, S, W}
    private bool[,,]? _sectorConnected; // [tier, sectorIdx, direction]

    // Sector-level BFS route cache: key = destSector * tiers + tier
    private readonly Dictionary<int, SectorRoute> _routeCache = new();

    // Per-sector flow field cache
    private readonly Dictionary<long, CachedFlow> _flowCache = new();

    private struct SectorRoute
    {
        public sbyte[] NextDir;   // direction to move toward dest (-1 = unreachable)
        public short[] HopDist;   // BFS distance (-1 = unreachable)
        public uint FrameAccessed;
    }

    private struct CachedFlow
    {
        public FlowDir[] Dirs;    // SectorSize * SectorSize
        public uint FrameAccessed;
    }

    public int SectorCountX => _sectorCountX;
    public int SectorCountY => _sectorCountY;
    public TileGrid? Grid => _grid;

    public void Init(TileGrid grid)
    {
        _grid = grid;
        _sectorCountX = (grid.Width + SectorSize - 1) / SectorSize;
        _sectorCountY = (grid.Height + SectorSize - 1) / SectorSize;
        Rebuild();
    }

    public void Rebuild()
    {
        BuildConnectivity();
        _routeCache.Clear();
        _flowCache.Clear();
    }

    // --- Sector connectivity ---

    private void BuildConnectivity()
    {
        if (_grid == null) return;
        int count = _sectorCountX * _sectorCountY;
        _sectorConnected = new bool[TerrainCosts.NumSizeTiers, count, 4];

        for (int tier = 0; tier < TerrainCosts.NumSizeTiers; tier++)
        {
            for (int sy = 0; sy < _sectorCountY; sy++)
            {
                for (int sx = 0; sx < _sectorCountX; sx++)
                {
                    int idx = SectorIdx(sx, sy);
                    int baseX = sx * SectorSize;
                    int baseY = sy * SectorSize;
                    int endX = Math.Min(baseX + SectorSize, _grid.Width);
                    int endY = Math.Min(baseY + SectorSize, _grid.Height);

                    // North
                    if (sy > 0)
                        for (int x = baseX; x < endX; x++)
                            if (_grid.GetCost(x, baseY, tier) != 255 && _grid.GetCost(x, baseY - 1, tier) != 255)
                            { _sectorConnected[tier, idx, 0] = true; break; }

                    // East
                    if (sx < _sectorCountX - 1)
                    {
                        int lastCol = endX - 1;
                        for (int y = baseY; y < endY; y++)
                            if (_grid.GetCost(lastCol, y, tier) != 255 && _grid.GetCost(lastCol + 1, y, tier) != 255)
                            { _sectorConnected[tier, idx, 1] = true; break; }
                    }

                    // South
                    if (sy < _sectorCountY - 1)
                    {
                        int lastRow = endY - 1;
                        for (int x = baseX; x < endX; x++)
                            if (_grid.GetCost(x, lastRow, tier) != 255 && _grid.GetCost(x, lastRow + 1, tier) != 255)
                            { _sectorConnected[tier, idx, 2] = true; break; }
                    }

                    // West
                    if (sx > 0)
                        for (int y = baseY; y < endY; y++)
                            if (_grid.GetCost(baseX, y, tier) != 255 && _grid.GetCost(baseX - 1, y, tier) != 255)
                            { _sectorConnected[tier, idx, 3] = true; break; }
                }
            }
        }
    }

    // --- Sector-level BFS routing ---

    private void WorldToSector(float wx, float wy, out int sx, out int sy)
    {
        sx = Math.Clamp((int)(wx / (GameConstants.TileSize * SectorSize)), 0, _sectorCountX - 1);
        sy = Math.Clamp((int)(wy / (GameConstants.TileSize * SectorSize)), 0, _sectorCountY - 1);
    }

    private int SectorIdx(int sx, int sy) => sy * _sectorCountX + sx;

    private SectorRoute GetRoute(int destSector, int tier, uint frame)
    {
        int routeKey = destSector * TerrainCosts.NumSizeTiers + tier;
        if (_routeCache.TryGetValue(routeKey, out var existing))
        {
            existing.FrameAccessed = frame;
            _routeCache[routeKey] = existing;
            return existing;
        }

        int totalSectors = _sectorCountX * _sectorCountY;
        var route = new SectorRoute
        {
            NextDir = new sbyte[totalSectors],
            HopDist = new short[totalSectors],
            FrameAccessed = frame
        };
        Array.Fill(route.NextDir, (sbyte)-1);
        Array.Fill(route.HopDist, (short)-1);

        var queue = new Queue<int>();
        queue.Enqueue(destSector);
        route.HopDist[destSector] = 0;

        int[] dx = { 0, 1, 0, -1 };
        int[] dy = { -1, 0, 1, 0 };
        int[] opposite = { 2, 3, 0, 1 };

        while (queue.Count > 0)
        {
            int curr = queue.Dequeue();
            int cx = curr % _sectorCountX;
            int cy = curr / _sectorCountX;
            short nextDist = (short)(route.HopDist[curr] + 1);

            for (int d = 0; d < 4; d++)
            {
                if (_sectorConnected == null || !_sectorConnected[tier, curr, d]) continue;
                int nx = cx + dx[d], ny = cy + dy[d];
                if (nx < 0 || nx >= _sectorCountX || ny < 0 || ny >= _sectorCountY) continue;
                int neighbor = ny * _sectorCountX + nx;
                if (route.HopDist[neighbor] >= 0) continue;
                route.HopDist[neighbor] = nextDist;
                route.NextDir[neighbor] = (sbyte)opposite[d];
                queue.Enqueue(neighbor);
            }
        }

        _routeCache[routeKey] = route;
        return route;
    }

    // --- Per-sector Dijkstra flow field ---

    private CachedFlow ComputeSectorFlow(int sx, int sy, List<int> goalLocalIndices, int tier, uint frame)
    {
        int cells = SectorSize * SectorSize;
        var flow = new CachedFlow { Dirs = new FlowDir[cells], FrameAccessed = frame };
        if (_grid == null || goalLocalIndices.Count == 0) return flow;

        float[] cost = new float[cells];
        Array.Fill(cost, GameConstants.InfCost);

        var openList = new PriorityQueue<int, float>();

        int baseX = sx * SectorSize;
        int baseY = sy * SectorSize;
        int sectorW = Math.Min(SectorSize, _grid.Width - baseX);
        int sectorH = Math.Min(SectorSize, _grid.Height - baseY);

        foreach (int g in goalLocalIndices)
        {
            int lx = g % SectorSize, ly = g / SectorSize;
            if (lx >= sectorW || ly >= sectorH) continue;
            int gx = baseX + lx, gy = baseY + ly;
            if (!_grid.InBounds(gx, gy)) continue;
            if (_grid.GetCost(gx, gy, tier) != 255 || _grid.GetCost(gx, gy) != 255)
            {
                cost[g] = 0f;
                openList.Enqueue(g, 0f);
            }
        }

        // 8-directional Dijkstra
        int[] ddx = { 0, 1, 1, 1, 0, -1, -1, -1 };
        int[] ddy = { -1, -1, 0, 1, 1, 1, 0, -1 };
        float[] stepMul = { 1f, 1.414f, 1f, 1.414f, 1f, 1.414f, 1f, 1.414f };

        while (openList.Count > 0)
        {
            int idx = openList.Dequeue();
            float c = cost[idx];
            int lx = idx % SectorSize, ly = idx / SectorSize;

            for (int d = 0; d < 8; d++)
            {
                int nlx = lx + ddx[d], nly = ly + ddy[d];
                if (nlx < 0 || nlx >= sectorW || nly < 0 || nly >= sectorH) continue;

                int gx = baseX + nlx, gy = baseY + nly;
                byte nc = _grid.GetCost(gx, gy, tier);
                if (nc == 255) continue;

                // Diagonal corner-cutting
                if (d % 2 == 1)
                {
                    int cax = lx + ddx[d], cay = ly;
                    int cbx = lx, cby = ly + ddy[d];
                    if (cax >= 0 && cax < sectorW && _grid.GetCost(baseX + cax, baseY + cay, tier) == 255) continue;
                    if (cby >= 0 && cby < sectorH && _grid.GetCost(baseX + cbx, baseY + cby, tier) == 255) continue;
                }

                int nidx = nly * SectorSize + nlx;
                float newCost = c + nc * stepMul[d];
                if (newCost < cost[nidx])
                {
                    cost[nidx] = newCost;
                    openList.Enqueue(nidx, newCost);
                }
            }
        }

        // Build direction field
        FlowDir[] flowDirs = { FlowDir.N, FlowDir.NE, FlowDir.E, FlowDir.SE, FlowDir.S, FlowDir.SW, FlowDir.W, FlowDir.NW };

        for (int ly = 0; ly < sectorH; ly++)
        {
            for (int lx = 0; lx < sectorW; lx++)
            {
                int idx = ly * SectorSize + lx;
                if (cost[idx] >= GameConstants.InfCost) continue;

                float bestCost = cost[idx];
                FlowDir bestDir = FlowDir.None;

                for (int d = 0; d < 8; d++)
                {
                    int nlx = lx + ddx[d], nly = ly + ddy[d];
                    if (nlx < 0 || nlx >= sectorW || nly < 0 || nly >= sectorH) continue;

                    if (d % 2 == 1)
                    {
                        int cax = lx + ddx[d], cay = ly;
                        int cbx = lx, cby = ly + ddy[d];
                        if (cax >= 0 && cax < sectorW && _grid.GetCost(baseX + cax, baseY + cay, tier) == 255) continue;
                        if (cby >= 0 && cby < sectorH && _grid.GetCost(baseX + cbx, baseY + cby, tier) == 255) continue;
                    }

                    float nc = cost[nly * SectorSize + nlx];
                    if (nc < bestCost)
                    {
                        bestCost = nc;
                        bestDir = flowDirs[d];
                    }
                }

                // Plateau fallback: pick lowest-cost neighbor even if not strictly cheaper
                if (bestDir == FlowDir.None && cost[idx] > 0f)
                {
                    float lowest = GameConstants.InfCost;
                    for (int d = 0; d < 8; d++)
                    {
                        int nlx = lx + ddx[d], nly = ly + ddy[d];
                        if (nlx < 0 || nlx >= sectorW || nly < 0 || nly >= sectorH) continue;
                        float nc = cost[nly * SectorSize + nlx];
                        if (nc < lowest) { lowest = nc; bestDir = flowDirs[d]; }
                    }
                }

                flow.Dirs[idx] = bestDir;
            }
        }

        return flow;
    }

    // --- Flow field caching ---

    private long FlowKey(int sx, int sy, int targetType, int targetData, int tier)
        => ((long)sy << 48) | ((long)sx << 32) | ((long)targetType << 24) | ((long)(targetData & 0xFFFF) << 8) | (uint)tier;

    private CachedFlow GetFlowToTile(int sx, int sy, int localTX, int localTY, int tier, uint frame)
    {
        long key = FlowKey(sx, sy, 1, localTY * SectorSize + localTX, tier);
        if (_flowCache.TryGetValue(key, out var cached))
        {
            cached.FrameAccessed = frame;
            _flowCache[key] = cached;
            return cached;
        }

        var goals = new List<int> { localTY * SectorSize + localTX };
        var flow = ComputeSectorFlow(sx, sy, goals, tier, frame);
        _flowCache[key] = flow;
        return flow;
    }

    private CachedFlow GetFlowToBorder(int sx, int sy, int borderDir, int tier, uint frame)
    {
        long key = FlowKey(sx, sy, 0, borderDir, tier);
        if (_flowCache.TryGetValue(key, out var cached))
        {
            cached.FrameAccessed = frame;
            _flowCache[key] = cached;
            return cached;
        }

        if (_grid == null) return new CachedFlow { Dirs = new FlowDir[SectorSize * SectorSize] };

        int baseX = sx * SectorSize, baseY = sy * SectorSize;
        int sectorW = Math.Min(SectorSize, _grid.Width - baseX);
        int sectorH = Math.Min(SectorSize, _grid.Height - baseY);

        // Goal = all passable tiles on the specified border
        var goals = new List<int>();
        switch (borderDir)
        {
            case 0: // North
                for (int x = 0; x < sectorW; x++) if (_grid.GetCost(baseX + x, baseY, tier) != 255) goals.Add(x);
                break;
            case 1: // East
                for (int y = 0; y < sectorH; y++) if (_grid.GetCost(baseX + sectorW - 1, baseY + y, tier) != 255) goals.Add(y * SectorSize + sectorW - 1);
                break;
            case 2: // South
                for (int x = 0; x < sectorW; x++) if (_grid.GetCost(baseX + x, baseY + sectorH - 1, tier) != 255) goals.Add((sectorH - 1) * SectorSize + x);
                break;
            case 3: // West
                for (int y = 0; y < sectorH; y++) if (_grid.GetCost(baseX, baseY + y, tier) != 255) goals.Add(y * SectorSize);
                break;
        }

        var flow = ComputeSectorFlow(sx, sy, goals, tier, frame);
        _flowCache[key] = flow;
        return flow;
    }

    private CachedFlow GetFlowToMultiBorder(int sx, int sy, byte borderMask, int tier, uint frame)
    {
        long key = FlowKey(sx, sy, 2, borderMask, tier);
        if (_flowCache.TryGetValue(key, out var cached))
        {
            cached.FrameAccessed = frame;
            _flowCache[key] = cached;
            return cached;
        }

        // Combine goals from all borders in the mask
        var goals = new List<int>();
        for (int d = 0; d < 4; d++)
        {
            if ((borderMask & (1 << d)) == 0) continue;
            var borderFlow = GetFlowToBorder(sx, sy, d, tier, frame);
            // Add all border tiles for this direction
            // (they're already computed in GetFlowToBorder, just merge goals)
        }

        // Simpler: just compute directly with all border tiles
        if (_grid == null) return new CachedFlow { Dirs = new FlowDir[SectorSize * SectorSize] };

        int baseX = sx * SectorSize, baseY = sy * SectorSize;
        int sectorW = Math.Min(SectorSize, _grid.Width - baseX);
        int sectorH = Math.Min(SectorSize, _grid.Height - baseY);

        goals.Clear();
        for (int d = 0; d < 4; d++)
        {
            if ((borderMask & (1 << d)) == 0) continue;
            switch (d)
            {
                case 0: for (int x = 0; x < sectorW; x++) if (_grid.GetCost(baseX + x, baseY, tier) != 255) goals.Add(x); break;
                case 1: for (int y = 0; y < sectorH; y++) if (_grid.GetCost(baseX + sectorW - 1, baseY + y, tier) != 255) goals.Add(y * SectorSize + sectorW - 1); break;
                case 2: for (int x = 0; x < sectorW; x++) if (_grid.GetCost(baseX + x, baseY + sectorH - 1, tier) != 255) goals.Add((sectorH - 1) * SectorSize + x); break;
                case 3: for (int y = 0; y < sectorH; y++) if (_grid.GetCost(baseX, baseY + y, tier) != 255) goals.Add(y * SectorSize); break;
            }
        }

        var flow = ComputeSectorFlow(sx, sy, goals, tier, frame);
        _flowCache[key] = flow;
        return flow;
    }

    // --- Main API ---

    public Vec2 GetDirection(Vec2 unitPos, Vec2 targetPos, uint frame, int sizeTier = 0, int unitIdx = -1)
    {
        if (_grid == null || _sectorConnected == null) return Vec2.Zero;
        sizeTier = Math.Clamp(sizeTier, 0, TerrainCosts.NumSizeTiers - 1);

        WorldToSector(unitPos.X, unitPos.Y, out int unitSX, out int unitSY);
        WorldToSector(targetPos.X, targetPos.Y, out int targetSX, out int targetSY);

        int unitSector = SectorIdx(unitSX, unitSY);
        int targetSector = SectorIdx(targetSX, targetSY);

        // --- Same sector: use tile flow ---
        if (unitSector == targetSector)
        {
            int localTX = Math.Clamp((int)(targetPos.X / GameConstants.TileSize) - unitSX * SectorSize, 0, SectorSize - 1);
            int localTY = Math.Clamp((int)(targetPos.Y / GameConstants.TileSize) - unitSY * SectorSize, 0, SectorSize - 1);
            var tileFlow = GetFlowToTile(unitSX, unitSY, localTX, localTY, sizeTier, frame);

            int localUX = Math.Clamp((int)(unitPos.X / GameConstants.TileSize) - unitSX * SectorSize, 0, SectorSize - 1);
            int localUY = Math.Clamp((int)(unitPos.Y / GameConstants.TileSize) - unitSY * SectorSize, 0, SectorSize - 1);

            if (tileFlow.Dirs != null)
            {
                FlowDir tfd = tileFlow.Dirs[localUY * SectorSize + localUX];
                Vec2 tileDir = FlowDirUtil.ToVec(tfd);
                if (tileDir.LengthSq() > 0.001f) return tileDir;
            }

            // No tile flow — beeline
            var bee = targetPos - unitPos;
            float beeLen = bee.Length();
            return beeLen > 0.01f ? bee * (1f / beeLen) : Vec2.Zero;
        }

        // --- Different sector: use sector-level BFS routing ---
        var route = GetRoute(targetSector, sizeTier, frame);
        short myDist = route.HopDist[unitSector];

        if (myDist < 0)
        {
            // Unreachable — try lower tier fallback
            for (int fallback = sizeTier - 1; fallback >= 0; fallback--)
            {
                var fbRoute = GetRoute(targetSector, fallback, frame);
                if (fbRoute.HopDist[unitSector] >= 0)
                {
                    short fbDist = fbRoute.HopDist[unitSector];
                    byte borderMask = BuildBorderMask(unitSX, unitSY, unitSector, fbRoute, fallback, fbDist);
                    if (borderMask != 0)
                    {
                        var flow = GetFlowToMultiBorder(unitSX, unitSY, borderMask, fallback, frame);
                        return SampleFlow(flow, unitPos, unitSX, unitSY);
                    }
                }
            }

            // True unreachable — beeline
            var d = targetPos - unitPos;
            float len = d.Length();
            return len > 0.01f ? d * (1f / len) : Vec2.Zero;
        }

        // Build border mask toward closer sectors
        {
            int[] dx = { 0, 1, 0, -1 };
            int[] dy = { -1, 0, 1, 0 };
            byte borderMask = 0;

            for (int d = 0; d < 4; d++)
            {
                if (!_sectorConnected[sizeTier, unitSector, d]) continue;
                int nx = unitSX + dx[d], ny = unitSY + dy[d];
                if (nx < 0 || nx >= _sectorCountX || ny < 0 || ny >= _sectorCountY) continue;
                int neighbor = SectorIdx(nx, ny);
                if (route.HopDist[neighbor] >= 0 && route.HopDist[neighbor] < myDist)
                    borderMask |= (byte)(1 << d);
            }

            if (borderMask != 0)
            {
                var flow = GetFlowToMultiBorder(unitSX, unitSY, borderMask, sizeTier, frame);
                var dir = SampleFlow(flow, unitPos, unitSX, unitSY);
                if (dir.LengthSq() > 0.001f) return dir;
            }
        }

        // Fallback: beeline
        {
            var diff = targetPos - unitPos;
            float len = diff.Length();
            return len > 0.01f ? diff * (1f / len) : Vec2.Zero;
        }
    }

    // --- Helpers ---

    private byte BuildBorderMask(int unitSX, int unitSY, int unitSector,
                                  SectorRoute route, int tier, short myDist)
    {
        int[] dx = { 0, 1, 0, -1 };
        int[] dy = { -1, 0, 1, 0 };
        byte mask = 0;

        for (int d = 0; d < 4; d++)
        {
            if (_sectorConnected == null || !_sectorConnected[tier, unitSector, d]) continue;
            int nx = unitSX + dx[d], ny = unitSY + dy[d];
            if (nx < 0 || nx >= _sectorCountX || ny < 0 || ny >= _sectorCountY) continue;
            int neighbor = SectorIdx(nx, ny);
            if (route.HopDist[neighbor] >= 0 && route.HopDist[neighbor] < myDist)
                mask |= (byte)(1 << d);
        }
        return mask;
    }

    private Vec2 SampleFlow(CachedFlow flow, Vec2 unitPos, int sx, int sy)
    {
        if (flow.Dirs == null) return Vec2.Zero;
        int localX = Math.Clamp((int)(unitPos.X / GameConstants.TileSize) - sx * SectorSize, 0, SectorSize - 1);
        int localY = Math.Clamp((int)(unitPos.Y / GameConstants.TileSize) - sy * SectorSize, 0, SectorSize - 1);
        return FlowDirUtil.ToVec(flow.Dirs[localY * SectorSize + localX]);
    }

    // --- Cache eviction ---

    public void EvictFlowFields(int maxCached = 384)
    {
        while (_flowCache.Count > maxCached)
        {
            long oldestKey = 0;
            uint oldestFrame = uint.MaxValue;
            foreach (var (k, v) in _flowCache)
                if (v.FrameAccessed < oldestFrame) { oldestFrame = v.FrameAccessed; oldestKey = k; }
            _flowCache.Remove(oldestKey);
        }
    }

    public void EvictRoutes(int maxCached = 96)
    {
        while (_routeCache.Count > maxCached)
        {
            int oldestKey = 0;
            uint oldestFrame = uint.MaxValue;
            foreach (var (k, v) in _routeCache)
                if (v.FrameAccessed < oldestFrame) { oldestFrame = v.FrameAccessed; oldestKey = k; }
            _routeCache.Remove(oldestKey);
        }
    }
}
