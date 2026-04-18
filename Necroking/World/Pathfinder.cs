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

public struct PathDecisionInfo
{
    public PathDecision Decision;
    public int BfsTargetLocalX;
    public int BfsTargetLocalY;
    public int FallbackTier;
}

public class Pathfinder
{
    public const int SectorSize = 64;

    private TileGrid? _grid;
    private int _sectorCountX, _sectorCountY;

    // Per-tier sector connectivity: [tier][sectorIdx][direction]
    private bool[,,]? _sectorConnected;

    // Sector-level BFS route cache: key = destSector * tiers + tier
    private readonly Dictionary<int, SectorRoute> _routeCache = new();

    // Per-sector flow field cache
    private readonly Dictionary<FlowKey, CachedFlow> _flowCache = new();

    private struct FlowKey : IEquatable<FlowKey>
    {
        public int SectorX, SectorY, TargetType, TargetData, TargetData2, SizeTier;

        public bool Equals(FlowKey o) =>
            SectorX == o.SectorX && SectorY == o.SectorY &&
            TargetType == o.TargetType && TargetData == o.TargetData &&
            TargetData2 == o.TargetData2 && SizeTier == o.SizeTier;

        public override bool Equals(object? obj) => obj is FlowKey k && Equals(k);

        public override int GetHashCode()
        {
            unchecked
            {
                long h = (uint)SectorX;
                h = h * 31 + (uint)SectorY;
                h = h * 31 + (uint)TargetType;
                h = h * 31 + (uint)TargetData;
                h = h * 31 + (uint)TargetData2;
                h = h * 31 + (uint)SizeTier;
                return h.GetHashCode();
            }
        }
    }

    // Per-unit imaginary chunk state
    private readonly Dictionary<int, ImaginaryChunk> _unitImagChunks = new();

    // Per-unit decision tracking (for debug)
    private readonly Dictionary<int, PathDecisionInfo> _unitDecisions = new();

    private struct SectorRoute
    {
        public sbyte[] NextDir;
        public short[] HopDist;
        public uint FrameAccessed;
    }

    private struct CachedFlow
    {
        public FlowDir[] Dirs;
        public uint FrameAccessed;
    }

    private class ImaginaryChunk
    {
        public FlowDir[] Dirs = Array.Empty<FlowDir>();
        public int BaseX, BaseY;
        public int ChunkW, ChunkH;
        public int TargetTX, TargetTY;
        public bool Active;
    }

    // 8-directional offsets
    private static readonly int[] Dx8 = { 0, 1, 1, 1, 0, -1, -1, -1 };
    private static readonly int[] Dy8 = { -1, -1, 0, 1, 1, 1, 0, -1 };
    private static readonly float[] StepMul8 = { 1f, 1.41421f, 1f, 1.41421f, 1f, 1.41421f, 1f, 1.41421f };
    private static readonly FlowDir[] FlowDirs8 =
    {
        FlowDir.N, FlowDir.NE, FlowDir.E, FlowDir.SE,
        FlowDir.S, FlowDir.SW, FlowDir.W, FlowDir.NW
    };

    // 4-directional offsets for sector BFS
    private static readonly int[] Dx4 = { 0, 1, 0, -1 };
    private static readonly int[] Dy4 = { -1, 0, 1, 0 };
    private static readonly int[] Opposite4 = { 2, 3, 0, 1 };

    // --- Budgeted pathfinding (optional) ---
    // When enabled, synchronous GetFlow* calls check a per-tick Dijkstra time
    // budget. If the budget is already spent, the miss is enqueued (with a
    // divergence-based priority) and the caller gets a stale-cache entry if
    // one exists, else an empty flow (callers fall through to imaginary chunk
    // or beeline). Each BeginTick() then drains the queue, highest-priority
    // first, until the budget for that tick is exhausted.
    public bool BudgetedPathfinding;
    public float DijkstraBudgetMsPerTick = 3.0f;
    private float _dijkstraMsThisTick;
    // Set by GetFlow* when they defer a request due to budget. GetDirection
    // clears this at entry, then on a null-flow result checks the flag to tell
    // "genuinely unpathable" (run imaginary-chunk fallback, which is correct
    // but ~4ms per call) apart from "just deferred" (the unit can beeline
    // this tick; BeginTick will fill in the flow next tick). Critical: imag
    // chunk costs roughly the same as the Dijkstra we were trying to avoid,
    // so running it as the defer-fallback completely negates the budget.
    private bool _lastQueryDeferred;
    public float DiagDijkstraMsThisTick => _dijkstraMsThisTick;
    public int DiagPendingRequestCount => _pendingRequests.Count;
    public int DiagStaleCacheSize => _staleFlowCache.Count;

    // Evicted entries move here rather than being deleted outright, so they
    // still inform priority scoring and can serve as a fallback while the
    // queue catches up. They get purged in the same pass but with a looser
    // age-out (see EvictStaleFlowFields).
    private readonly Dictionary<FlowKey, CachedFlow> _staleFlowCache = new();

    private struct PendingRequest
    {
        public Vec2 UnitPos;
        public Vec2 TargetPos;
        public float Priority; // higher = more urgent (divergence)
    }
    private readonly Dictionary<FlowKey, PendingRequest> _pendingRequests = new();

    public int SectorCountX => _sectorCountX;
    public int SectorCountY => _sectorCountY;
    public TileGrid? Grid => _grid;

    /// <summary>Get imaginary chunk bounds for debug visualization. Returns (baseX, baseY, w, h, active) or null.</summary>
    public (int baseX, int baseY, int w, int h, bool active)? GetImaginaryChunkInfo(int unitIdx)
    {
        if (_unitImagChunks.TryGetValue(unitIdx, out var ic) && ic.Active)
            return (ic.BaseX, ic.BaseY, ic.ChunkW, ic.ChunkH, ic.Active);
        return null;
    }

    /// <summary>Get all active imaginary chunk unit indices for debug overlay.</summary>
    public IEnumerable<int> GetActiveImaginaryChunkUnits()
    {
        foreach (var kv in _unitImagChunks)
            if (kv.Value.Active) yield return kv.Key;
    }

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
        _unitImagChunks.Clear();
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

        while (queue.Count > 0)
        {
            int curr = queue.Dequeue();
            int cx = curr % _sectorCountX;
            int cy = curr / _sectorCountX;
            short nextDist = (short)(route.HopDist[curr] + 1);

            for (int d = 0; d < 4; d++)
            {
                if (_sectorConnected == null || !_sectorConnected[tier, curr, d]) continue;
                int nx = cx + Dx4[d], ny = cy + Dy4[d];
                if (nx < 0 || nx >= _sectorCountX || ny < 0 || ny >= _sectorCountY) continue;
                int neighbor = ny * _sectorCountX + nx;
                if (route.HopDist[neighbor] >= 0) continue;
                route.HopDist[neighbor] = nextDist;
                route.NextDir[neighbor] = (sbyte)Opposite4[d];
                queue.Enqueue(neighbor);
            }
        }

        _routeCache[routeKey] = route;
        return route;
    }

    // =========================================================================
    // Sector Dijkstra with escape propagation
    // =========================================================================

    /// <summary>
    /// Core Dijkstra within a sector. Computes flow directions from goal tiles.
    /// Includes escape propagation for tier-inflated tiles.
    /// </summary>
    private CachedFlow ComputeSectorFlow(int sx, int sy, List<int> goalLocalIndices, int tier, uint frame,
                                          List<float>? goalInitCosts = null)
    {
        DiagDijkstraInvocations++;
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

        for (int i = 0; i < goalLocalIndices.Count; i++)
        {
            int g = goalLocalIndices[i];
            int lx = g % SectorSize, ly = g / SectorSize;
            if (lx >= sectorW || ly >= sectorH) continue;
            int gx = baseX + lx, gy = baseY + ly;
            if (!_grid.InBounds(gx, gy)) continue;
            // Accept if tier-passable OR base-passable (for tier-inflated target tiles)
            if (_grid.GetCost(gx, gy, tier) != 255 || _grid.GetCost(gx, gy) != 255)
            {
                float initCost = (goalInitCosts != null && i < goalInitCosts.Count) ? goalInitCosts[i] : 0f;
                if (initCost < cost[g])
                {
                    cost[g] = initCost;
                    openList.Enqueue(g, initCost);
                }
            }
        }

        // 8-directional Dijkstra
        while (openList.Count > 0)
        {
            int idx = openList.Dequeue();
            float c = cost[idx];
            int lx = idx % SectorSize, ly = idx / SectorSize;

            for (int d = 0; d < 8; d++)
            {
                int nlx = lx + Dx8[d], nly = ly + Dy8[d];
                if (nlx < 0 || nlx >= sectorW || nly < 0 || nly >= sectorH) continue;

                int gx = baseX + nlx, gy = baseY + nly;
                byte nc = _grid.GetCost(gx, gy, tier);
                if (nc == 255) continue;

                // Diagonal corner-cutting
                if (d % 2 == 1)
                {
                    int cax = lx + Dx8[d], cay = ly;
                    int cbx = lx, cby = ly + Dy8[d];
                    if (cax >= 0 && cax < sectorW && _grid.GetCost(baseX + cax, baseY + cay, tier) == 255) continue;
                    if (cby >= 0 && cby < sectorH && _grid.GetCost(baseX + cbx, baseY + cby, tier) == 255) continue;
                }

                int nidx = nly * SectorSize + nlx;
                float newCost = c + nc * StepMul8[d];
                if (newCost < cost[nidx])
                {
                    cost[nidx] = newCost;
                    openList.Enqueue(nidx, newCost);
                }
            }
        }

        // === Escape propagation ===
        // Extend costs into tier-impassable but base-passable tiles so units
        // in inflated zones get proper directions toward the goal.
        {
            var escapePQ = new PriorityQueue<int, float>();
            for (int ly = 0; ly < sectorH; ly++)
            {
                for (int lx = 0; lx < sectorW; lx++)
                {
                    int idx = ly * SectorSize + lx;
                    if (cost[idx] >= GameConstants.InfCost) continue;

                    for (int d = 0; d < 8; d++)
                    {
                        int nlx = lx + Dx8[d], nly = ly + Dy8[d];
                        if (nlx < 0 || nlx >= sectorW || nly < 0 || nly >= sectorH) continue;
                        int nidx = nly * SectorSize + nlx;
                        if (cost[nidx] < GameConstants.InfCost) continue; // already has cost
                        int ngx = baseX + nlx, ngy = baseY + nly;
                        if (_grid.GetCost(ngx, ngy, tier) != 255) continue; // not inflated
                        if (_grid.GetCost(ngx, ngy) == 255) continue; // truly impassable

                        float newCost = cost[idx] + StepMul8[d];
                        if (newCost < cost[nidx])
                        {
                            cost[nidx] = newCost;
                            escapePQ.Enqueue(nidx, newCost);
                        }
                    }
                }
            }

            while (escapePQ.Count > 0)
            {
                int idx = escapePQ.Dequeue();
                float c = cost[idx];
                int lx = idx % SectorSize, ly = idx / SectorSize;

                for (int d = 0; d < 8; d++)
                {
                    int nlx = lx + Dx8[d], nly = ly + Dy8[d];
                    if (nlx < 0 || nlx >= sectorW || nly < 0 || nly >= sectorH) continue;
                    int nidx = nly * SectorSize + nlx;
                    if (cost[nidx] < GameConstants.InfCost) continue;
                    int ngx = baseX + nlx, ngy = baseY + nly;
                    if (_grid.GetCost(ngx, ngy) == 255) continue; // truly impassable

                    float newCost = c + StepMul8[d];
                    if (newCost < cost[nidx])
                    {
                        cost[nidx] = newCost;
                        escapePQ.Enqueue(nidx, newCost);
                    }
                }
            }
        }

        // Build direction field
        BuildDirectionField(flow.Dirs, cost, baseX, baseY, sectorW, sectorH, tier);
        return flow;
    }

    /// <summary>
    /// Build direction field from integration cost array.
    /// Each tile points toward the neighbor with strictly lower cost.
    /// Includes plateau fallback for flat-cost regions.
    /// </summary>
    private void BuildDirectionField(FlowDir[] dirs, float[] cost, int baseX, int baseY,
                                      int sectorW, int sectorH, int tier)
    {
        if (_grid == null) return;

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
                    int nlx = lx + Dx8[d], nly = ly + Dy8[d];
                    if (nlx < 0 || nlx >= sectorW || nly < 0 || nly >= sectorH) continue;

                    // Diagonal corner-cutting
                    if (d % 2 == 1)
                    {
                        int cax = lx + Dx8[d], cay = ly;
                        int cbx = lx, cby = ly + Dy8[d];
                        if (cax >= 0 && cax < sectorW && _grid.GetCost(baseX + cax, baseY + cay, tier) == 255) continue;
                        if (cby >= 0 && cby < sectorH && _grid.GetCost(baseX + cbx, baseY + cby, tier) == 255) continue;
                    }

                    float nc = cost[nly * SectorSize + nlx];
                    if (nc < bestCost)
                    {
                        bestCost = nc;
                        bestDir = FlowDirs8[d];
                    }
                }

                // Plateau fallback: pick lowest-cost neighbor even if not strictly cheaper
                if (bestDir == FlowDir.None && cost[idx] > 0f)
                {
                    float lowest = GameConstants.InfCost;
                    for (int d = 0; d < 8; d++)
                    {
                        int nlx = lx + Dx8[d], nly = ly + Dy8[d];
                        if (nlx < 0 || nlx >= sectorW || nly < 0 || nly >= sectorH) continue;

                        if (d % 2 == 1)
                        {
                            int cax = lx + Dx8[d], cay = ly;
                            int cbx = lx, cby = ly + Dy8[d];
                            if (cax >= 0 && cax < sectorW && _grid.GetCost(baseX + cax, baseY + cay, tier) == 255) continue;
                            if (cby >= 0 && cby < sectorH && _grid.GetCost(baseX + cbx, baseY + cby, tier) == 255) continue;
                        }

                        float nc = cost[nly * SectorSize + nlx];
                        if (nc < lowest) { lowest = nc; bestDir = FlowDirs8[d]; }
                    }
                }

                dirs[idx] = bestDir;
            }
        }
    }

    // =========================================================================
    // Flow field caching
    // =========================================================================

    private static FlowKey MakeFlowKey(int sx, int sy, int targetType, int targetData, int targetData2, int tier)
    {
        return new FlowKey
        {
            SectorX = sx, SectorY = sy,
            TargetType = targetType, TargetData = targetData,
            TargetData2 = targetData2, SizeTier = tier
        };
    }

    private CachedFlow GetFlowToTile(int sx, int sy, int localTX, int localTY, int tier, uint frame,
                                     Vec2 unitPos = default, Vec2 targetPos = default)
    {
        var key = MakeFlowKey(sx, sy, 1, localTY * SectorSize + localTX, -1, tier);
        if (_flowCache.TryGetValue(key, out var cached))
        {
            DiagFlowCacheHits++;
            cached.FrameAccessed = frame;
            _flowCache[key] = cached;
            return cached;
        }
        DiagFlowCacheMisses++;
        DiagMissTile++;
        if (s_keysEverSeen.Add(key)) DiagMissNewKey++; else DiagMissEvicted++;

        if (_grid == null) return new CachedFlow { Dirs = new FlowDir[SectorSize * SectorSize] };

        // Budget gate: if over this tick's Dijkstra allowance, defer and hand
        // back the stale entry if one exists — callers that can't use stale
        // will fall through to imaginary-chunk fallback.
        if (!HasDijkstraBudget())
        {
            EnqueueMiss(key, sx, sy, unitPos, targetPos);
            _lastQueryDeferred = true;
            if (_staleFlowCache.TryGetValue(key, out var stale))
            {
                stale.FrameAccessed = frame;
                return stale;
            }
            return default;
        }

        int baseX = sx * SectorSize, baseY = sy * SectorSize;
        int targetGlobalX = baseX + localTX;
        int targetGlobalY = baseY + localTY;

        var goals = new List<int>();
        var goalCosts = new List<float>();

        // Seed the actual target tile if base-passable
        if (_grid.InBounds(targetGlobalX, targetGlobalY) &&
            _grid.GetCost(targetGlobalX, targetGlobalY) != 255)
        {
            goals.Add(localTY * SectorSize + localTX);
            goalCosts.Add(0f);
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var flow = ComputeSectorFlow(sx, sy, goals, tier, frame, goalCosts);
        sw.Stop();
        ChargeDijkstraMs((float)sw.Elapsed.TotalMilliseconds);

        _flowCache[key] = flow;
        _staleFlowCache.Remove(key);
        return flow;
    }

    private CachedFlow GetFlowToBorder(int sx, int sy, int borderDir, int tier, uint frame,
                                       Vec2 unitPos = default, Vec2 targetPos = default)
    {
        var key = MakeFlowKey(sx, sy, 0, borderDir, -1, tier);
        if (_flowCache.TryGetValue(key, out var cached))
        {
            DiagFlowCacheHits++;
            cached.FrameAccessed = frame;
            _flowCache[key] = cached;
            return cached;
        }
        DiagFlowCacheMisses++;
        DiagMissBorder++;
        if (s_keysEverSeen.Add(key)) DiagMissNewKey++; else DiagMissEvicted++;

        if (_grid == null) return new CachedFlow { Dirs = new FlowDir[SectorSize * SectorSize] };

        if (!HasDijkstraBudget())
        {
            EnqueueMiss(key, sx, sy, unitPos, targetPos);
            _lastQueryDeferred = true;
            if (_staleFlowCache.TryGetValue(key, out var stale))
            {
                stale.FrameAccessed = frame;
                return stale;
            }
            return default;
        }

        int baseX = sx * SectorSize, baseY = sy * SectorSize;
        int endX = Math.Min(baseX + SectorSize, _grid.Width);
        int endY = Math.Min(baseY + SectorSize, _grid.Height);

        var goals = new List<int>();
        switch (borderDir)
        {
            case 0: // North
                for (int x = baseX; x < endX; x++)
                    if (_grid.GetCost(x, baseY, tier) != 255 &&
                        baseY > 0 && _grid.GetCost(x, baseY - 1, tier) != 255)
                        goals.Add(0 * SectorSize + (x - baseX));
                break;
            case 1: // East
                for (int y = baseY; y < endY; y++)
                {
                    int x = endX - 1;
                    if (_grid.GetCost(x, y, tier) != 255 &&
                        x + 1 < _grid.Width && _grid.GetCost(x + 1, y, tier) != 255)
                        goals.Add((y - baseY) * SectorSize + (x - baseX));
                }
                break;
            case 2: // South
                for (int x = baseX; x < endX; x++)
                {
                    int y = endY - 1;
                    if (_grid.GetCost(x, y, tier) != 255 &&
                        y + 1 < _grid.Height && _grid.GetCost(x, y + 1, tier) != 255)
                        goals.Add((y - baseY) * SectorSize + (x - baseX));
                }
                break;
            case 3: // West
                for (int y = baseY; y < endY; y++)
                    if (_grid.GetCost(baseX, y, tier) != 255 &&
                        baseX > 0 && _grid.GetCost(baseX - 1, y, tier) != 255)
                        goals.Add((y - baseY) * SectorSize + 0);
                break;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var flow = ComputeSectorFlow(sx, sy, goals, tier, frame);

        // Post-process: border goal tiles get exit direction
        FlowDir[] borderExitDir = { FlowDir.N, FlowDir.E, FlowDir.S, FlowDir.W };
        FlowDir exitDir = borderExitDir[borderDir];
        foreach (int g in goals)
        {
            if (flow.Dirs[g] == FlowDir.None)
                flow.Dirs[g] = exitDir;
        }
        sw.Stop();
        ChargeDijkstraMs((float)sw.Elapsed.TotalMilliseconds);

        _flowCache[key] = flow;
        _staleFlowCache.Remove(key);
        return flow;
    }

    /// <summary>
    /// Multi-border flow with lateral/extended masks and distance weighting.
    /// Primary borders get cost 0, lateral borders get half-sector penalty,
    /// extended borders get full-sector penalty. Manhattan distance to clamped
    /// target position biases the flow toward the geometrically correct exit.
    /// </summary>
    private CachedFlow GetFlowToMultiBorder(int sx, int sy, byte borderMask, byte lateralMask,
                                             byte extendedMask, int tier, uint frame,
                                             int clampedLocalTX = -1, int clampedLocalTY = -1,
                                             Vec2 unitPos = default, Vec2 targetPos = default)
    {
        int combinedMask = borderMask | (lateralMask << 4) | (extendedMask << 8);
        int clampedIdx = (clampedLocalTX >= 0 && clampedLocalTY >= 0)
            ? clampedLocalTY * SectorSize + clampedLocalTX : -1;
        var key = MakeFlowKey(sx, sy, 2, combinedMask, clampedIdx, tier);

        if (_flowCache.TryGetValue(key, out var cached))
        {
            DiagFlowCacheHits++;
            cached.FrameAccessed = frame;
            _flowCache[key] = cached;
            return cached;
        }
        DiagFlowCacheMisses++;
        DiagMissMultiBorder++;
        if (s_keysEverSeen.Add(key)) DiagMissNewKey++; else DiagMissEvicted++;

        if (_grid == null) return new CachedFlow { Dirs = new FlowDir[SectorSize * SectorSize] };

        if (!HasDijkstraBudget())
        {
            EnqueueMiss(key, sx, sy, unitPos, targetPos);
            _lastQueryDeferred = true;
            if (_staleFlowCache.TryGetValue(key, out var stale))
            {
                stale.FrameAccessed = frame;
                return stale;
            }
            return default;
        }

        int baseX = sx * SectorSize, baseY = sy * SectorSize;
        int endX = Math.Min(baseX + SectorSize, _grid.Width);
        int endY = Math.Min(baseY + SectorSize, _grid.Height);

        float lateralPenalty = SectorSize * 0.5f;
        float extendedPenalty = SectorSize * 1.0f;
        bool hasClampedTarget = clampedLocalTX >= 0 && clampedLocalTY >= 0;

        var goals = new List<int>();
        var goalCosts = new List<float>();
        var goalBorderDir = new List<int>();

        byte fullMask = (byte)(borderMask | lateralMask | extendedMask);
        for (int bDir = 0; bDir < 4; bDir++)
        {
            if (((fullMask >> bDir) & 1) == 0) continue;
            float basePenalty = 0f;
            if (((extendedMask >> bDir) & 1) != 0) basePenalty = extendedPenalty;
            else if (((lateralMask >> bDir) & 1) != 0) basePenalty = lateralPenalty;

            switch (bDir)
            {
                case 0: // North
                    for (int x = baseX; x < endX; x++)
                    {
                        if (_grid.GetCost(x, baseY, tier) != 255 &&
                            baseY > 0 && _grid.GetCost(x, baseY - 1, tier) != 255)
                        {
                            int lx = x - baseX, ly = 0;
                            float distCost = hasClampedTarget ? (Math.Abs(lx - clampedLocalTX) + Math.Abs(ly - clampedLocalTY)) : 0f;
                            goals.Add(ly * SectorSize + lx);
                            goalCosts.Add(basePenalty + distCost);
                            goalBorderDir.Add(0);
                        }
                    }
                    break;
                case 1: // East
                    for (int y = baseY; y < endY; y++)
                    {
                        int x = endX - 1;
                        if (_grid.GetCost(x, y, tier) != 255 &&
                            x + 1 < _grid.Width && _grid.GetCost(x + 1, y, tier) != 255)
                        {
                            int lx = x - baseX, ly = y - baseY;
                            float distCost = hasClampedTarget ? (Math.Abs(lx - clampedLocalTX) + Math.Abs(ly - clampedLocalTY)) : 0f;
                            goals.Add(ly * SectorSize + lx);
                            goalCosts.Add(basePenalty + distCost);
                            goalBorderDir.Add(1);
                        }
                    }
                    break;
                case 2: // South
                    for (int x = baseX; x < endX; x++)
                    {
                        int y = endY - 1;
                        if (_grid.GetCost(x, y, tier) != 255 &&
                            y + 1 < _grid.Height && _grid.GetCost(x, y + 1, tier) != 255)
                        {
                            int lx = x - baseX, ly = y - baseY;
                            float distCost = hasClampedTarget ? (Math.Abs(lx - clampedLocalTX) + Math.Abs(ly - clampedLocalTY)) : 0f;
                            goals.Add(ly * SectorSize + lx);
                            goalCosts.Add(basePenalty + distCost);
                            goalBorderDir.Add(2);
                        }
                    }
                    break;
                case 3: // West
                    for (int y = baseY; y < endY; y++)
                    {
                        if (_grid.GetCost(baseX, y, tier) != 255 &&
                            baseX > 0 && _grid.GetCost(baseX - 1, y, tier) != 255)
                        {
                            int lx = 0, ly = y - baseY;
                            float distCost = hasClampedTarget ? (Math.Abs(lx - clampedLocalTX) + Math.Abs(ly - clampedLocalTY)) : 0f;
                            goals.Add(ly * SectorSize + lx);
                            goalCosts.Add(basePenalty + distCost);
                            goalBorderDir.Add(3);
                        }
                    }
                    break;
            }
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var flow = ComputeSectorFlow(sx, sy, goals, tier, frame, goalCosts);

        // Post-process: border goal tiles get their respective exit direction
        FlowDir[] borderExitDir = { FlowDir.N, FlowDir.E, FlowDir.S, FlowDir.W };
        for (int i = 0; i < goals.Count; i++)
        {
            int dir = goalBorderDir[i];
            bool isPrimary = ((borderMask >> dir) & 1) != 0;
            if (isPrimary || flow.Dirs[goals[i]] == FlowDir.None)
                flow.Dirs[goals[i]] = borderExitDir[dir];
        }
        sw.Stop();
        ChargeDijkstraMs((float)sw.Elapsed.TotalMilliseconds);

        _flowCache[key] = flow;
        _staleFlowCache.Remove(key);
        return flow;
    }

    // =========================================================================
    // Imaginary chunk: unit-centered Dijkstra for escaping sector-boundary traps
    // =========================================================================

    /// <summary>
    /// Create a 64x64 chunk centered on the unit, run Dijkstra from
    /// target tile (if inside) or border tiles facing the target (if outside).
    /// Returns direction at unit's position. Stores chunk per-unit for persistence.
    /// </summary>
    private Vec2 GetLocalChunkDirection(Vec2 unitPos, Vec2 targetPos, int tier, int unitIdx = -1, bool activate = true)
    {
        DiagImagChunkComputes++;
        var _diagSw = System.Diagnostics.Stopwatch.StartNew();
        try {
        if (_grid == null) return Vec2.Zero;

        int unitTX = (int)(unitPos.X / GameConstants.TileSize);
        int unitTY = (int)(unitPos.Y / GameConstants.TileSize);
        int targetTX = (int)(targetPos.X / GameConstants.TileSize);
        int targetTY = (int)(targetPos.Y / GameConstants.TileSize);

        // Center the chunk on the unit, clamped to map bounds
        int halfSize = SectorSize / 2;
        int baseX = Math.Max(0, unitTX - halfSize);
        int baseY = Math.Max(0, unitTY - halfSize);
        int endX = Math.Min(_grid.Width, baseX + SectorSize);
        int endY = Math.Min(_grid.Height, baseY + SectorSize);
        int chunkW = endX - baseX;
        int chunkH = endY - baseY;
        if (chunkW <= 0 || chunkH <= 0) return Vec2.Zero;

        int localUX = Math.Clamp(unitTX - baseX, 0, chunkW - 1);
        int localUY = Math.Clamp(unitTY - baseY, 0, chunkH - 1);

        int cells = SectorSize * SectorSize;
        float[] cost = new float[cells];
        Array.Fill(cost, GameConstants.InfCost);

        var openList = new PriorityQueue<int, float>();

        bool targetInChunk = targetTX >= baseX && targetTX < endX &&
                             targetTY >= baseY && targetTY < endY;

        if (targetInChunk)
        {
            int localTX = targetTX - baseX;
            int localTY = targetTY - baseY;
            int goalIdx = localTY * SectorSize + localTX;
            int gx = baseX + localTX, gy = baseY + localTY;
            if (_grid.GetCost(gx, gy, tier) != 255 || _grid.GetCost(gx, gy) != 255)
            {
                cost[goalIdx] = 0f;
                openList.Enqueue(goalIdx, 0f);
            }
        }
        else
        {
            // Seed from chunk borders facing the target direction
            bool seedN = targetTY < baseY;
            bool seedS = targetTY >= endY;
            bool seedW = targetTX < baseX;
            bool seedE = targetTX >= endX;
            if (!seedN && !seedS && !seedE && !seedW)
                seedN = seedS = seedE = seedW = true;

            if (seedN && baseY > 0)
            {
                for (int lx = 0; lx < chunkW; lx++)
                {
                    int gx = baseX + lx, gy = baseY;
                    if (_grid.GetCost(gx, gy, tier) == 255) continue;
                    if (_grid.GetCost(gx, gy - 1) != 255)
                    {
                        int idx = 0 * SectorSize + lx;
                        cost[idx] = 0f;
                        openList.Enqueue(idx, 0f);
                    }
                }
            }
            if (seedS && endY < _grid.Height)
            {
                for (int lx = 0; lx < chunkW; lx++)
                {
                    int gx = baseX + lx, gy = endY - 1;
                    if (_grid.GetCost(gx, gy, tier) == 255) continue;
                    if (_grid.GetCost(gx, gy + 1) != 255)
                    {
                        int idx = (chunkH - 1) * SectorSize + lx;
                        cost[idx] = 0f;
                        openList.Enqueue(idx, 0f);
                    }
                }
            }
            if (seedW && baseX > 0)
            {
                for (int ly = 0; ly < chunkH; ly++)
                {
                    int gx = baseX, gy = baseY + ly;
                    if (_grid.GetCost(gx, gy, tier) == 255) continue;
                    if (_grid.GetCost(gx - 1, gy) != 255)
                    {
                        int idx = ly * SectorSize + 0;
                        cost[idx] = 0f;
                        openList.Enqueue(idx, 0f);
                    }
                }
            }
            if (seedE && endX < _grid.Width)
            {
                for (int ly = 0; ly < chunkH; ly++)
                {
                    int gx = endX - 1, gy = baseY + ly;
                    if (_grid.GetCost(gx, gy, tier) == 255) continue;
                    if (_grid.GetCost(gx + 1, gy) != 255)
                    {
                        int idx = ly * SectorSize + (chunkW - 1);
                        cost[idx] = 0f;
                        openList.Enqueue(idx, 0f);
                    }
                }
            }
        }

        if (openList.Count == 0) return Vec2.Zero;

        // Dijkstra within chunk
        while (openList.Count > 0)
        {
            int idx = openList.Dequeue();
            float c = cost[idx];
            int lx = idx % SectorSize, ly = idx / SectorSize;

            for (int d = 0; d < 8; d++)
            {
                int nlx = lx + Dx8[d], nly = ly + Dy8[d];
                if (nlx < 0 || nlx >= chunkW || nly < 0 || nly >= chunkH) continue;

                int gx = baseX + nlx, gy = baseY + nly;
                byte nc = _grid.GetCost(gx, gy, tier);
                if (nc == 255) continue;

                // Diagonal corner-cutting
                if (d % 2 == 1)
                {
                    int cAlx = lx + Dx8[d], cAly = ly;
                    int cBlx = lx, cBly = ly + Dy8[d];
                    if (cAlx >= 0 && cAlx < chunkW && _grid.GetCost(baseX + cAlx, baseY + cAly, tier) == 255) continue;
                    if (cBly >= 0 && cBly < chunkH && _grid.GetCost(baseX + cBlx, baseY + cBly, tier) == 255) continue;
                }

                int nidx = nly * SectorSize + nlx;
                float newCost = c + nc * StepMul8[d];
                if (newCost < cost[nidx])
                {
                    cost[nidx] = newCost;
                    openList.Enqueue(nidx, newCost);
                }
            }
        }

        // Escape propagation for tier-inflated tiles
        RunEscapePropagation(cost, baseX, baseY, chunkW, chunkH, tier);

        // Build direction field
        var dirs = new FlowDir[cells];
        BuildChunkDirectionField(dirs, cost, baseX, baseY, chunkW, chunkH, tier);

        // Extract direction at unit's tile
        int unitTileIdx = localUY * SectorSize + localUX;
        if (cost[unitTileIdx] >= GameConstants.InfCost) return Vec2.Zero;

        FlowDir bestDir = dirs[unitTileIdx];

        // Store chunk per-unit for persistence
        if (unitIdx >= 0 && bestDir != FlowDir.None)
        {
            if (!_unitImagChunks.TryGetValue(unitIdx, out var ic))
            {
                ic = new ImaginaryChunk();
                _unitImagChunks[unitIdx] = ic;
            }
            ic.Dirs = dirs;
            ic.BaseX = baseX;
            ic.BaseY = baseY;
            ic.ChunkW = chunkW;
            ic.ChunkH = chunkH;
            ic.TargetTX = targetTX;
            ic.TargetTY = targetTY;
            ic.Active = activate;
        }

        return FlowDirUtil.ToVec(bestDir);
        }
        finally { DiagImagChunkMs += _diagSw.Elapsed.TotalMilliseconds; }
    }

    /// <summary>
    /// Recompute flow within an existing imaginary chunk's bounds for a new target.
    /// </summary>
    private Vec2 RecomputeImaginaryChunkFlow(ImaginaryChunk ic, Vec2 unitPos, Vec2 targetPos, int tier)
    {
        DiagImagChunkRecomputes++;
        var _diagSw = System.Diagnostics.Stopwatch.StartNew();
        try {
        if (_grid == null) return Vec2.Zero;

        int unitTX = (int)(unitPos.X / GameConstants.TileSize);
        int unitTY = (int)(unitPos.Y / GameConstants.TileSize);
        int targetTX = (int)(targetPos.X / GameConstants.TileSize);
        int targetTY = (int)(targetPos.Y / GameConstants.TileSize);

        int baseX = ic.BaseX, baseY = ic.BaseY;
        int chunkW = ic.ChunkW, chunkH = ic.ChunkH;
        int endX = baseX + chunkW, endY = baseY + chunkH;

        int localUX = Math.Clamp(unitTX - baseX, 0, chunkW - 1);
        int localUY = Math.Clamp(unitTY - baseY, 0, chunkH - 1);

        tier = Math.Clamp(tier, 0, TerrainCosts.NumSizeTiers - 1);
        int cells = SectorSize * SectorSize;
        float[] cost = new float[cells];
        Array.Fill(cost, GameConstants.InfCost);

        var openList = new PriorityQueue<int, float>();

        bool targetInChunk = targetTX >= baseX && targetTX < endX &&
                             targetTY >= baseY && targetTY < endY;

        if (targetInChunk)
        {
            int localTX = targetTX - baseX;
            int localTY = targetTY - baseY;
            int goalIdx = localTY * SectorSize + localTX;
            int gx = baseX + localTX, gy = baseY + localTY;
            if (_grid.GetCost(gx, gy, tier) != 255 || _grid.GetCost(gx, gy) != 255)
            {
                cost[goalIdx] = 0f;
                openList.Enqueue(goalIdx, 0f);
            }
        }
        else
        {
            bool seedN = targetTY < baseY;
            bool seedS = targetTY >= endY;
            bool seedW = targetTX < baseX;
            bool seedE = targetTX >= endX;
            if (!seedN && !seedS && !seedE && !seedW)
                seedN = seedS = seedE = seedW = true;

            if (seedN && baseY > 0)
                for (int lx = 0; lx < chunkW; lx++)
                {
                    int gx = baseX + lx, gy = baseY;
                    if (_grid.GetCost(gx, gy, tier) == 255) continue;
                    if (_grid.GetCost(gx, gy - 1) != 255)
                    { int idx = lx; cost[idx] = 0f; openList.Enqueue(idx, 0f); }
                }

            if (seedS && endY < _grid.Height)
                for (int lx = 0; lx < chunkW; lx++)
                {
                    int gx = baseX + lx, gy = endY - 1;
                    if (_grid.GetCost(gx, gy, tier) == 255) continue;
                    if (_grid.GetCost(gx, gy + 1) != 255)
                    { int idx = (chunkH - 1) * SectorSize + lx; cost[idx] = 0f; openList.Enqueue(idx, 0f); }
                }

            if (seedW && baseX > 0)
                for (int ly = 0; ly < chunkH; ly++)
                {
                    int gx = baseX, gy = baseY + ly;
                    if (_grid.GetCost(gx, gy, tier) == 255) continue;
                    if (_grid.GetCost(gx - 1, gy) != 255)
                    { int idx = ly * SectorSize; cost[idx] = 0f; openList.Enqueue(idx, 0f); }
                }

            if (seedE && endX < _grid.Width)
                for (int ly = 0; ly < chunkH; ly++)
                {
                    int gx = endX - 1, gy = baseY + ly;
                    if (_grid.GetCost(gx, gy, tier) == 255) continue;
                    if (_grid.GetCost(gx + 1, gy) != 255)
                    { int idx = ly * SectorSize + (chunkW - 1); cost[idx] = 0f; openList.Enqueue(idx, 0f); }
                }
        }

        if (openList.Count == 0) return Vec2.Zero;

        // Dijkstra
        while (openList.Count > 0)
        {
            int idx = openList.Dequeue();
            float c = cost[idx];
            int lx = idx % SectorSize, ly = idx / SectorSize;

            for (int d = 0; d < 8; d++)
            {
                int nlx = lx + Dx8[d], nly = ly + Dy8[d];
                if (nlx < 0 || nlx >= chunkW || nly < 0 || nly >= chunkH) continue;

                int gx = baseX + nlx, gy = baseY + nly;
                byte nc = _grid.GetCost(gx, gy, tier);
                if (nc == 255) continue;

                if (d % 2 == 1)
                {
                    int cAlx = lx + Dx8[d], cAly = ly;
                    int cBlx = lx, cBly = ly + Dy8[d];
                    if (cAlx >= 0 && cAlx < chunkW && _grid.GetCost(baseX + cAlx, baseY + cAly, tier) == 255) continue;
                    if (cBly >= 0 && cBly < chunkH && _grid.GetCost(baseX + cBlx, baseY + cBly, tier) == 255) continue;
                }

                int nidx = nly * SectorSize + nlx;
                float newCost = c + nc * StepMul8[d];
                if (newCost < cost[nidx])
                {
                    cost[nidx] = newCost;
                    openList.Enqueue(nidx, newCost);
                }
            }
        }

        // Escape propagation
        RunEscapePropagation(cost, baseX, baseY, chunkW, chunkH, tier);

        // Build direction field
        var dirs = new FlowDir[cells];
        BuildChunkDirectionField(dirs, cost, baseX, baseY, chunkW, chunkH, tier);

        // Update chunk state
        ic.Dirs = dirs;
        ic.TargetTX = targetTX;
        ic.TargetTY = targetTY;

        int unitTileIdx = localUY * SectorSize + localUX;
        if (cost[unitTileIdx] >= GameConstants.InfCost) return Vec2.Zero;

        return FlowDirUtil.ToVec(ic.Dirs[unitTileIdx]);
        }
        finally { DiagImagChunkMs += _diagSw.Elapsed.TotalMilliseconds; }
    }

    /// <summary>
    /// Escape propagation: extend costs into tier-impassable but base-passable tiles.
    /// </summary>
    private void RunEscapePropagation(float[] cost, int baseX, int baseY, int chunkW, int chunkH, int tier)
    {
        if (_grid == null) return;

        var escapePQ = new PriorityQueue<int, float>();
        for (int ly = 0; ly < chunkH; ly++)
        {
            for (int lx = 0; lx < chunkW; lx++)
            {
                int idx = ly * SectorSize + lx;
                if (cost[idx] >= GameConstants.InfCost) continue;

                for (int d = 0; d < 8; d++)
                {
                    int nlx = lx + Dx8[d], nly = ly + Dy8[d];
                    if (nlx < 0 || nlx >= chunkW || nly < 0 || nly >= chunkH) continue;
                    int nidx = nly * SectorSize + nlx;
                    if (cost[nidx] < GameConstants.InfCost) continue;
                    int ngx = baseX + nlx, ngy = baseY + nly;
                    if (_grid.GetCost(ngx, ngy, tier) != 255) continue; // not inflated
                    if (_grid.GetCost(ngx, ngy) == 255) continue; // truly impassable

                    float newCost = cost[idx] + StepMul8[d];
                    if (newCost < cost[nidx])
                    {
                        cost[nidx] = newCost;
                        escapePQ.Enqueue(nidx, newCost);
                    }
                }
            }
        }

        while (escapePQ.Count > 0)
        {
            int idx = escapePQ.Dequeue();
            float c = cost[idx];
            int lx = idx % SectorSize, ly = idx / SectorSize;

            for (int d = 0; d < 8; d++)
            {
                int nlx = lx + Dx8[d], nly = ly + Dy8[d];
                if (nlx < 0 || nlx >= chunkW || nly < 0 || nly >= chunkH) continue;
                int nidx = nly * SectorSize + nlx;
                if (cost[nidx] < GameConstants.InfCost) continue;
                int ngx = baseX + nlx, ngy = baseY + nly;
                if (_grid.GetCost(ngx, ngy) == 255) continue;

                float newCost = c + StepMul8[d];
                if (newCost < cost[nidx])
                {
                    cost[nidx] = newCost;
                    escapePQ.Enqueue(nidx, newCost);
                }
            }
        }
    }

    /// <summary>
    /// Build direction field for a chunk (same as sector but with chunk dimensions).
    /// </summary>
    private void BuildChunkDirectionField(FlowDir[] dirs, float[] cost, int baseX, int baseY,
                                           int chunkW, int chunkH, int tier)
    {
        if (_grid == null) return;

        for (int ly = 0; ly < chunkH; ly++)
        {
            for (int lx = 0; lx < chunkW; lx++)
            {
                int idx = ly * SectorSize + lx;
                if (cost[idx] >= GameConstants.InfCost) continue;

                float bc = cost[idx];
                FlowDir bd = FlowDir.None;

                for (int d = 0; d < 8; d++)
                {
                    int nlx = lx + Dx8[d], nly = ly + Dy8[d];
                    if (nlx < 0 || nlx >= chunkW || nly < 0 || nly >= chunkH) continue;

                    if (d % 2 == 1)
                    {
                        int cax = lx + Dx8[d], cay = ly;
                        int cbx = lx, cby = ly + Dy8[d];
                        if (cax >= 0 && cax < chunkW && _grid.GetCost(baseX + cax, baseY + cay, tier) == 255) continue;
                        if (cby >= 0 && cby < chunkH && _grid.GetCost(baseX + cbx, baseY + cby, tier) == 255) continue;
                    }

                    float nc = cost[nly * SectorSize + nlx];
                    if (nc < bc) { bc = nc; bd = FlowDirs8[d]; }
                }

                // Plateau fallback
                if (bd == FlowDir.None && cost[idx] > 0f)
                {
                    float lowest = GameConstants.InfCost;
                    for (int d = 0; d < 8; d++)
                    {
                        int nlx = lx + Dx8[d], nly = ly + Dy8[d];
                        if (nlx < 0 || nlx >= chunkW || nly < 0 || nly >= chunkH) continue;

                        if (d % 2 == 1)
                        {
                            int cax = lx + Dx8[d], cay = ly;
                            int cbx = lx, cby = ly + Dy8[d];
                            if (cax >= 0 && cax < chunkW && _grid.GetCost(baseX + cax, baseY + cay, tier) == 255) continue;
                            if (cby >= 0 && cby < chunkH && _grid.GetCost(baseX + cbx, baseY + cby, tier) == 255) continue;
                        }

                        float nc = cost[nly * SectorSize + nlx];
                        if (nc < lowest) { lowest = nc; bd = FlowDirs8[d]; }
                    }
                }

                dirs[idx] = bd;
            }
        }
    }

    // =========================================================================
    // Main API
    // =========================================================================

    // Diagnostic counters — Simulation resets these at the start of each Tick and
    // sums them into LastPhaseMs so a profiling scenario can see pathfinder load.
    public static int DiagCallsThisTick;
    public static double DiagTotalMsThisTick;
    public static int DiagDijkstraInvocations;     // how many full ComputeSectorFlow runs happened
    public static int DiagFlowCacheHits;           // cache returned without recomputing
    public static int DiagFlowCacheMisses;         // had to call ComputeSectorFlow
    public static int DiagImagChunkComputes;       // GetLocalChunkDirection full sweeps
    public static int DiagImagChunkRecomputes;     // RecomputeImaginaryChunkFlow sweeps
    public static double DiagImagChunkMs;          // wall-clock spent in imag-chunk paths
    // Miss-cause breakdown (reset per tick):
    //   DiagMissNewKey      = key never requested before in this run
    //   DiagMissEvicted     = key was in cache earlier but got evicted before this request
    //   DiagCacheSize       = flow-cache entry count at start of tick
    //   DiagCacheEvictions  = entries evicted this tick (LRU pressure)
    public static int DiagMissNewKey;
    public static int DiagMissEvicted;
    public static int DiagCacheSize;
    public static int DiagCacheEvictions;
    // Per-flow-type miss counters (reset per tick):
    public static int DiagMissTile;           // GetFlowToTile (same-sector, target tile as goal)
    public static int DiagMissBorder;         // GetFlowToBorder (cross-sector via single border)
    public static int DiagMissMultiBorder;    // GetFlowToMultiBorder (cross-sector with weighted borders)
    // Tracks every FlowKey ever seen. Used to tell "new-key" vs "was-here-got-evicted".
    private static readonly System.Collections.Generic.HashSet<FlowKey> s_keysEverSeen = new();

    public Vec2 GetDirection(Vec2 unitPos, Vec2 targetPos, uint frame, int sizeTier = 0, int unitIdx = -1)
    {
        var diagSw = System.Diagnostics.Stopwatch.StartNew();
        DiagCallsThisTick++;
        try
        {
        if (_grid == null || _sectorConnected == null) return Vec2.Zero;
        sizeTier = Math.Clamp(sizeTier, 0, TerrainCosts.NumSizeTiers - 1);

        // Reset the deferral flag for this query; GetFlow* sets it when it
        // returns stale/empty flow due to a spent per-tick Dijkstra budget.
        _lastQueryDeferred = false;

        // --- Per-unit persistent imaginary chunk ---
        if (unitIdx >= 0 && _unitImagChunks.TryGetValue(unitIdx, out var existingIc) && existingIc.Active)
        {
            int uTX = (int)(unitPos.X / GameConstants.TileSize);
            int uTY = (int)(unitPos.Y / GameConstants.TileSize);
            int tTX = (int)(targetPos.X / GameConstants.TileSize);
            int tTY = (int)(targetPos.Y / GameConstants.TileSize);

            int lcX = uTX - existingIc.BaseX;
            int lcY = uTY - existingIc.BaseY;

            // Exit: unit reached target tile
            if (uTX == tTX && uTY == tTY)
            {
                existingIc.Active = false;
            }
            // Exit: unit left chunk bounds
            else if (lcX < 0 || lcX >= existingIc.ChunkW || lcY < 0 || lcY >= existingIc.ChunkH)
            {
                existingIc.Active = false;
            }
            else
            {
                // Target moved? Recompute flow within existing bounds.
                // Skip the recompute (same cost as a flow Dijkstra) when over
                // budget and fall through to the normal flow path, which will
                // either cache-hit or beeline. Keeps the per-tick cost bounded
                // even during a summon burst where dozens of units have moved
                // horde slots this tick.
                if (tTX != existingIc.TargetTX || tTY != existingIc.TargetTY)
                {
                    if (!HasDijkstraBudget())
                    {
                        existingIc.Active = false;
                    }
                    else
                    {
                        Vec2 dir = RecomputeImaginaryChunkFlow(existingIc, unitPos, targetPos, sizeTier);
                        if (dir.LengthSq() > 0.001f)
                        {
                            RecordDecision(unitIdx, PathDecision.ImagChunkRecompute);
                            return dir;
                        }
                        existingIc.Active = false;
                    }
                }
                else
                {
                    // Same target — read stored flow
                    FlowDir fd = existingIc.Dirs[lcY * SectorSize + lcX];
                    Vec2 dir = FlowDirUtil.ToVec(fd);
                    if (dir.LengthSq() > 0.001f)
                    {
                        RecordDecision(unitIdx, PathDecision.ImagChunkPersist);
                        return dir;
                    }
                    existingIc.Active = false;
                }
            }
        }

        WorldToSector(unitPos.X, unitPos.Y, out int unitSX, out int unitSY);
        WorldToSector(targetPos.X, targetPos.Y, out int targetSX, out int targetSY);

        int unitSector = SectorIdx(unitSX, unitSY);
        int targetSector = SectorIdx(targetSX, targetSY);

        CachedFlow flow = default;
        bool hasFlow = false;

        if (unitSector == targetSector)
        {
            // Same sector: use tile flow to target
            int localTX = Math.Clamp((int)(targetPos.X / GameConstants.TileSize) - unitSX * SectorSize, 0, SectorSize - 1);
            int localTY = Math.Clamp((int)(targetPos.Y / GameConstants.TileSize) - unitSY * SectorSize, 0, SectorSize - 1);
            var tileFlow = GetFlowToTile(unitSX, unitSY, localTX, localTY, sizeTier, frame, unitPos, targetPos);

            int localUX = Math.Clamp((int)(unitPos.X / GameConstants.TileSize) - unitSX * SectorSize, 0, SectorSize - 1);
            int localUY = Math.Clamp((int)(unitPos.Y / GameConstants.TileSize) - unitSY * SectorSize, 0, SectorSize - 1);

            if (tileFlow.Dirs != null)
            {
                FlowDir tfd = tileFlow.Dirs[localUY * SectorSize + localUX];
                Vec2 tileDir = FlowDirUtil.ToVec(tfd);

                if (tileDir.LengthSq() > 0.001f)
                {
                    flow = tileFlow;
                    hasFlow = true;
                }
                else
                {
                    // No tile flow at unit tile — use imaginary chunk
                    Vec2 localDir = _lastQueryDeferred ? Vec2.Zero : GetLocalChunkDirection(unitPos, targetPos, sizeTier, unitIdx);
                    if (localDir.LengthSq() > 0.001f)
                    {
                        RecordDecision(unitIdx, PathDecision.SameSectorImagChunk);
                        return localDir;
                    }
                    // Beeline
                    var bee = targetPos - unitPos;
                    float beeLen = bee.Length();
                    RecordDecision(unitIdx, PathDecision.Unreachable);
                    return beeLen > 0.01f ? bee * (1f / beeLen) : Vec2.Zero;
                }
            }
        }
        else
        {
            // Different sector: use sector-level BFS routing
            var route = GetRoute(targetSector, sizeTier, frame);
            short myDist = route.HopDist[unitSector];

            if (myDist < 0)
            {
                // Unreachable for this tier — try lower tier fallback
                for (int fallback = sizeTier - 1; fallback >= 0; fallback--)
                {
                    var fbRoute = GetRoute(targetSector, fallback, frame);
                    if (fbRoute.HopDist[unitSector] >= 0)
                    {
                        short fbDist = fbRoute.HopDist[unitSector];
                        byte borderMask = 0;
                        for (int d = 0; d < 4; d++)
                        {
                            if (!_sectorConnected[fallback, unitSector, d]) continue;
                            int nx = unitSX + Dx4[d], ny = unitSY + Dy4[d];
                            if (nx < 0 || nx >= _sectorCountX || ny < 0 || ny >= _sectorCountY) continue;
                            int neighbor = SectorIdx(nx, ny);
                            if (fbRoute.HopDist[neighbor] >= 0 && fbRoute.HopDist[neighbor] < fbDist)
                                borderMask |= (byte)(1 << d);
                        }
                        if (borderMask != 0)
                        {
                            int clTX = Math.Clamp((int)(targetPos.X / GameConstants.TileSize) - unitSX * SectorSize, 0, SectorSize - 1);
                            int clTY = Math.Clamp((int)(targetPos.Y / GameConstants.TileSize) - unitSY * SectorSize, 0, SectorSize - 1);
                            flow = GetFlowToMultiBorder(unitSX, unitSY, borderMask, 0, 0, fallback, frame, clTX, clTY, unitPos, targetPos);
                            hasFlow = true;
                            break;
                        }
                    }
                }

                if (!hasFlow)
                {
                    // Try imaginary chunk before beeline
                    Vec2 localDir = _lastQueryDeferred ? Vec2.Zero : GetLocalChunkDirection(unitPos, targetPos, sizeTier, unitIdx);
                    if (localDir.LengthSq() > 0.001f)
                    {
                        RecordDecision(unitIdx, PathDecision.UnreachableImagChunk);
                        return localDir;
                    }
                    var dd = targetPos - unitPos;
                    float len = dd.Length();
                    RecordDecision(unitIdx, PathDecision.Unreachable);
                    return len > 0.01f ? dd * (1f / len) : Vec2.Zero;
                }
            }
            else
            {
                // Build border masks: primary (closer), lateral (same), extended (+1)
                byte borderMask = 0;
                byte lateralMask = 0;
                byte extendedMask = 0;

                for (int d = 0; d < 4; d++)
                {
                    if (!_sectorConnected[sizeTier, unitSector, d]) continue;
                    int nx = unitSX + Dx4[d], ny = unitSY + Dy4[d];
                    if (nx < 0 || nx >= _sectorCountX || ny < 0 || ny >= _sectorCountY) continue;
                    int neighbor = SectorIdx(nx, ny);
                    if (route.HopDist[neighbor] < 0) continue;
                    if (route.HopDist[neighbor] < myDist)
                        borderMask |= (byte)(1 << d);
                    else if (route.HopDist[neighbor] == myDist)
                        lateralMask |= (byte)(1 << d);
                    else if (route.HopDist[neighbor] == myDist + 1)
                        extendedMask |= (byte)(1 << d);
                }

                if (borderMask != 0)
                {
                    int clampedLTX = Math.Clamp((int)(targetPos.X / GameConstants.TileSize) - unitSX * SectorSize, 0, SectorSize - 1);
                    int clampedLTY = Math.Clamp((int)(targetPos.Y / GameConstants.TileSize) - unitSY * SectorSize, 0, SectorSize - 1);
                    flow = GetFlowToMultiBorder(unitSX, unitSY, borderMask, lateralMask, extendedMask,
                                                sizeTier, frame, clampedLTX, clampedLTY, unitPos, targetPos);
                    hasFlow = true;
                }
                else
                {
                    // Fallback to single border
                    flow = GetFlowToBorder(unitSX, unitSY, route.NextDir[unitSector], sizeTier, frame, unitPos, targetPos);
                    hasFlow = true;
                }
            }
        }

        if (!hasFlow || flow.Dirs == null)
        {
            // Deferred cross-sector flow: beeline rather than falling into the
            // imag-chunk fallback (same cost as the Dijkstra we just deferred)
            // or returning zero (which made units walk-in-place at high
            // density — PreferredVel=0 but Walk anim still playing).
            if (_lastQueryDeferred)
            {
                var beeD = targetPos - unitPos;
                float beeDLen = beeD.Length();
                if (beeDLen > 0.01f)
                {
                    RecordDecision(unitIdx, PathDecision.Beeline);
                    return beeD * (1f / beeDLen);
                }
                RecordDecision(unitIdx, PathDecision.None);
                return Vec2.Zero;
            }
            Vec2 localDir = GetLocalChunkDirection(unitPos, targetPos, sizeTier, unitIdx);
            if (localDir.LengthSq() > 0.001f)
            {
                RecordDecision(unitIdx, PathDecision.NoFlow);
                return localDir;
            }
            RecordDecision(unitIdx, PathDecision.None);
            return Vec2.Zero;
        }

        int finalLocalX = Math.Clamp((int)(unitPos.X / GameConstants.TileSize) - unitSX * SectorSize, 0, SectorSize - 1);
        int finalLocalY = Math.Clamp((int)(unitPos.Y / GameConstants.TileSize) - unitSY * SectorSize, 0, SectorSize - 1);

        FlowDir finalFd = flow.Dirs[finalLocalY * SectorSize + finalLocalX];
        Vec2 finalDir = FlowDirUtil.ToVec(finalFd);

        if (finalDir.LengthSq() < 0.001f)
        {
            // BFS fallback: find nearest tile with valid flow
            int sectorW = Math.Min(SectorSize, _grid.Width - unitSX * SectorSize);
            int sectorH = Math.Min(SectorSize, _grid.Height - unitSY * SectorSize);

            Vec2 bestDir = Vec2.Zero;
            {
                var visited = new bool[SectorSize * SectorSize];
                var bfsQueue = new Queue<int>();
                int startIdx = finalLocalY * SectorSize + finalLocalX;
                visited[startIdx] = true;
                bfsQueue.Enqueue(startIdx);
                float bestDist2 = 1e18f;

                while (bfsQueue.Count > 0 && bestDir.LengthSq() < 0.001f)
                {
                    int cur = bfsQueue.Dequeue();
                    int clx = cur % SectorSize, cly = cur / SectorSize;

                    for (int d = 0; d < 8; d++)
                    {
                        int nlx = clx + Dx8[d], nly = cly + Dy8[d];
                        if (nlx < 0 || nlx >= sectorW || nly < 0 || nly >= sectorH) continue;
                        int nidx = nly * SectorSize + nlx;
                        if (visited[nidx]) continue;
                        visited[nidx] = true;

                        FlowDir nfd = flow.Dirs[nidx];
                        if (nfd != FlowDir.None)
                        {
                            float fdx = nlx - finalLocalX;
                            float fdy = nly - finalLocalY;
                            float d2 = fdx * fdx + fdy * fdy;
                            if (d2 < bestDist2)
                            {
                                bestDist2 = d2;
                                float l = MathF.Sqrt(d2);
                                bestDir = new Vec2(fdx / l, fdy / l);
                            }
                            continue;
                        }

                        // Expand through base-passable tiles
                        int ngx = unitSX * SectorSize + nlx;
                        int ngy = unitSY * SectorSize + nly;
                        if (_grid.GetCost(ngx, ngy) != 255)
                            bfsQueue.Enqueue(nidx);
                    }
                }
            }

            if (bestDir.LengthSq() > 0.001f)
            {
                RecordDecision(unitIdx, PathDecision.BFSFallback);
                return bestDir;
            }

            // Tier fallback: try lower tiers
            for (int fallbackTier = sizeTier - 1; fallbackTier >= 0; fallbackTier--)
            {
                CachedFlow fbFlow;
                if (unitSector == targetSector)
                {
                    int localTX = Math.Clamp((int)(targetPos.X / GameConstants.TileSize) - unitSX * SectorSize, 0, SectorSize - 1);
                    int localTY = Math.Clamp((int)(targetPos.Y / GameConstants.TileSize) - unitSY * SectorSize, 0, SectorSize - 1);
                    fbFlow = GetFlowToTile(unitSX, unitSY, localTX, localTY, fallbackTier, frame, unitPos, targetPos);
                }
                else
                {
                    var fbRoute = GetRoute(targetSector, fallbackTier, frame);
                    if (fbRoute.HopDist[unitSector] < 0) continue;
                    short fbDist = fbRoute.HopDist[unitSector];
                    byte fbBorderMask = 0, fbLateralMask = 0;
                    for (int d = 0; d < 4; d++)
                    {
                        if (!_sectorConnected[fallbackTier, unitSector, d]) continue;
                        int nx = unitSX + Dx4[d], ny = unitSY + Dy4[d];
                        if (nx < 0 || nx >= _sectorCountX || ny < 0 || ny >= _sectorCountY) continue;
                        int neighbor = SectorIdx(nx, ny);
                        if (fbRoute.HopDist[neighbor] < 0) continue;
                        if (fbRoute.HopDist[neighbor] < fbDist) fbBorderMask |= (byte)(1 << d);
                        else if (fbRoute.HopDist[neighbor] == fbDist) fbLateralMask |= (byte)(1 << d);
                    }
                    if (fbBorderMask == 0) continue;
                    int clTX = Math.Clamp((int)(targetPos.X / GameConstants.TileSize) - unitSX * SectorSize, 0, SectorSize - 1);
                    int clTY = Math.Clamp((int)(targetPos.Y / GameConstants.TileSize) - unitSY * SectorSize, 0, SectorSize - 1);
                    fbFlow = GetFlowToMultiBorder(unitSX, unitSY, fbBorderMask, fbLateralMask, 0, fallbackTier, frame, clTX, clTY, unitPos, targetPos);
                }

                if (fbFlow.Dirs == null) continue;

                // BFS within fallback tier flow
                Vec2 fbBestDir = Vec2.Zero;
                float fbBestDist2 = 1e18f;
                var fbVisited = new bool[SectorSize * SectorSize];
                var fbBfsQueue = new Queue<int>();
                int fbStart = finalLocalY * SectorSize + finalLocalX;
                fbVisited[fbStart] = true;
                fbBfsQueue.Enqueue(fbStart);

                while (fbBfsQueue.Count > 0 && fbBestDir.LengthSq() < 0.001f)
                {
                    int cur = fbBfsQueue.Dequeue();
                    int clx = cur % SectorSize, cly = cur / SectorSize;
                    for (int d = 0; d < 8; d++)
                    {
                        int nlx = clx + Dx8[d], nly = cly + Dy8[d];
                        if (nlx < 0 || nlx >= sectorW || nly < 0 || nly >= sectorH) continue;
                        int nidx = nly * SectorSize + nlx;
                        if (fbVisited[nidx]) continue;
                        fbVisited[nidx] = true;
                        if (fbFlow.Dirs[nidx] != FlowDir.None)
                        {
                            float fdx = nlx - finalLocalX, fdy = nly - finalLocalY;
                            float d2 = fdx * fdx + fdy * fdy;
                            if (d2 < fbBestDist2) { fbBestDist2 = d2; float l = MathF.Sqrt(d2); fbBestDir = new Vec2(fdx / l, fdy / l); }
                            continue;
                        }
                        int ngx = unitSX * SectorSize + nlx, ngy = unitSY * SectorSize + nly;
                        if (_grid.GetCost(ngx, ngy) != 255) fbBfsQueue.Enqueue(nidx);
                    }
                }

                if (fbBestDir.LengthSq() > 0.001f)
                {
                    RecordDecision(unitIdx, PathDecision.TierFallback, -1, -1, fallbackTier);
                    return fbBestDir;
                }
            }

            // Try imaginary chunk
            {
                Vec2 localDir = _lastQueryDeferred ? Vec2.Zero : GetLocalChunkDirection(unitPos, targetPos, sizeTier, unitIdx);
                if (localDir.LengthSq() > 0.001f)
                {
                    RecordDecision(unitIdx, PathDecision.ImagChunkFallback);
                    return localDir;
                }
            }

            // Boundary escape: BFS toward nearest sector boundary
            {
                Vec2 boundaryDir = Vec2.Zero;
                float boundaryDist2 = 1e18f;
                var bv = new bool[SectorSize * SectorSize];
                var bq = new Queue<int>();
                int si = finalLocalY * SectorSize + finalLocalX;
                bv[si] = true;
                bq.Enqueue(si);

                while (bq.Count > 0 && boundaryDir.LengthSq() < 0.001f)
                {
                    int cur = bq.Dequeue();
                    int clx = cur % SectorSize, cly = cur / SectorSize;
                    for (int d = 0; d < 8; d++)
                    {
                        int nlx = clx + Dx8[d], nly = cly + Dy8[d];
                        if (nlx < 0 || nlx >= sectorW || nly < 0 || nly >= sectorH)
                        {
                            // This tile is at the boundary
                            float fdx = clx - finalLocalX, fdy = cly - finalLocalY;
                            float d2 = fdx * fdx + fdy * fdy;
                            if (d2 > 0.001f && d2 < boundaryDist2)
                            {
                                boundaryDist2 = d2;
                                float l = MathF.Sqrt(d2);
                                boundaryDir = new Vec2(fdx / l, fdy / l);
                            }
                            continue;
                        }
                        int nidx = nly * SectorSize + nlx;
                        if (bv[nidx]) continue;
                        bv[nidx] = true;
                        int ngx = unitSX * SectorSize + nlx, ngy = unitSY * SectorSize + nly;
                        if (_grid.GetCost(ngx, ngy) != 255) bq.Enqueue(nidx);
                    }
                }

                if (boundaryDir.LengthSq() > 0.001f)
                {
                    RecordDecision(unitIdx, PathDecision.BoundaryEscape);
                    return boundaryDir;
                }
            }

            // Last resort: beeline
            var diff = targetPos - unitPos;
            float diffLen = diff.Length();
            RecordDecision(unitIdx, PathDecision.Beeline);
            return diffLen > 0.01f ? diff * (1f / diffLen) : Vec2.Zero;
        }

        // Normal flow direction
        RecordDecision(unitIdx, unitSector == targetSector ? PathDecision.TileFlow : PathDecision.BorderFlow);
        return finalDir;
        }
        finally
        {
            DiagTotalMsThisTick += diagSw.Elapsed.TotalMilliseconds;
        }
    }

    // --- Decision tracking ---

    private void RecordDecision(int unitIdx, PathDecision decision, int bfsLX = -1, int bfsLY = -1, int fbTier = -1)
    {
        if (unitIdx >= 0)
            _unitDecisions[unitIdx] = new PathDecisionInfo
            {
                Decision = decision,
                BfsTargetLocalX = bfsLX,
                BfsTargetLocalY = bfsLY,
                FallbackTier = fbTier
            };
    }

    public PathDecisionInfo? GetUnitDecision(int unitIdx)
    {
        return _unitDecisions.TryGetValue(unitIdx, out var info) ? info : null;
    }

    public void ClearImaginaryChunk(int unitIdx)
    {
        _unitImagChunks.Remove(unitIdx);
    }

    // --- Budgeted pathfinding ---

    /// <summary>
    /// Called once per simulation tick at the start of the pathfinder phase.
    /// Resets the per-tick Dijkstra budget, then drains the deferred request
    /// queue (highest priority first) until the budget is spent. Remaining
    /// entries are cleared — callers that still need them will re-enqueue on
    /// this tick's queries if the work hasn't happened via another path.
    /// </summary>
    public void BeginTick(uint frame)
    {
        _dijkstraMsThisTick = 0f;
        if (!BudgetedPathfinding || _pendingRequests.Count == 0)
        {
            _pendingRequests.Clear();
            return;
        }

        // Materialize + sort by priority desc. Small allocation per tick, but
        // the queue is tiny (only the overflow from the previous tick).
        var sorted = new List<KeyValuePair<FlowKey, PendingRequest>>(_pendingRequests);
        sorted.Sort((a, b) => b.Value.Priority.CompareTo(a.Value.Priority));

        foreach (var (key, _) in sorted)
        {
            if (_dijkstraMsThisTick >= DijkstraBudgetMsPerTick) break;
            if (_flowCache.ContainsKey(key)) continue;  // already computed via another path
            ProcessPendingRequest(key, frame);
        }
        _pendingRequests.Clear();
    }

    private void ProcessPendingRequest(FlowKey key, uint frame)
    {
        // FlowKey round-trips: TargetData encodes per-type params, invertibly.
        switch (key.TargetType)
        {
            case 0: // border
                GetFlowToBorder(key.SectorX, key.SectorY, key.TargetData, key.SizeTier, frame);
                break;
            case 1: // tile
                int tLY = key.TargetData / SectorSize;
                int tLX = key.TargetData - tLY * SectorSize;
                GetFlowToTile(key.SectorX, key.SectorY, tLX, tLY, key.SizeTier, frame);
                break;
            case 2: // multi-border
                byte bm = (byte)(key.TargetData & 0xF);
                byte lm = (byte)((key.TargetData >> 4) & 0xF);
                byte em = (byte)((key.TargetData >> 8) & 0xF);
                int clampIdx = key.TargetData2;
                int clampTX = -1, clampTY = -1;
                if (clampIdx >= 0)
                {
                    clampTY = clampIdx / SectorSize;
                    clampTX = clampIdx - clampTY * SectorSize;
                }
                GetFlowToMultiBorder(key.SectorX, key.SectorY, bm, lm, em,
                    key.SizeTier, frame, clampTX, clampTY);
                break;
        }
    }

    /// <summary>
    /// Returns true when budgeted pathfinding is disabled, or when some budget
    /// remains this tick. Callers on a cache miss should use this to decide
    /// whether to compute synchronously or enqueue the request.
    /// </summary>
    private bool HasDijkstraBudget()
    {
        if (!BudgetedPathfinding) return true;
        return _dijkstraMsThisTick < DijkstraBudgetMsPerTick;
    }

    /// <summary>
    /// Record elapsed ms from a synchronous Dijkstra against the per-tick budget.
    /// </summary>
    private void ChargeDijkstraMs(float ms) => _dijkstraMsThisTick += ms;

    /// <summary>
    /// Enqueue a deferred flow request. Priority ~= 1 - dot(staleFlow, straight),
    /// so misses whose stale fallback steers badly against the goal get processed
    /// first. Requests with no stale fallback get a medium priority — the caller
    /// can fall through to the imaginary-chunk fallback, so they aren't as urgent
    /// as a stale flow that's actually wrong.
    /// </summary>
    private void EnqueueMiss(FlowKey key, int sx, int sy, Vec2 unitPos, Vec2 targetPos)
    {
        float priority = 1.0f; // default for no-stale-data case
        if (_staleFlowCache.TryGetValue(key, out var stale))
        {
            Vec2 sFlow = SampleFlow(stale, unitPos, sx, sy);
            Vec2 toTgt = targetPos - unitPos;
            float len = toTgt.Length();
            if (len > 0.01f && sFlow.LengthSq() > 0.001f)
            {
                Vec2 straight = toTgt * (1f / len);
                priority = 1f - (sFlow.X * straight.X + sFlow.Y * straight.Y);
            }
        }
        if (_pendingRequests.TryGetValue(key, out var existing))
        {
            if (priority > existing.Priority)
                _pendingRequests[key] = new PendingRequest { UnitPos = unitPos, TargetPos = targetPos, Priority = priority };
        }
        else
        {
            _pendingRequests[key] = new PendingRequest { UnitPos = unitPos, TargetPos = targetPos, Priority = priority };
        }
    }

    // --- Helpers ---

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
            FlowKey oldestKey = default;
            uint oldestFrame = uint.MaxValue;
            foreach (var (k, v) in _flowCache)
                if (v.FrameAccessed < oldestFrame) { oldestFrame = v.FrameAccessed; oldestKey = k; }
            if (_flowCache.TryGetValue(oldestKey, out var victim))
                _staleFlowCache[oldestKey] = victim;
            _flowCache.Remove(oldestKey);
            DiagCacheEvictions++;
        }
    }

    /// <summary>
    /// Age-based eviction: remove flow-cache entries that no unit has looked at
    /// for <paramref name="maxAgeFrames"/> ticks. FrameAccessed is bumped on every
    /// cache hit, so an entry only becomes stale if nothing is currently using it.
    /// Handles the shared-field case correctly (the field stays fresh as long as
    /// any unit is reading it) without needing per-unit refcounting.
    /// </summary>
    public void EvictStaleFlowFields(uint currentFrame, uint maxAgeFrames)
    {
        List<FlowKey>? toRemove = null;
        foreach (var (k, v) in _flowCache)
        {
            // Subtraction is uint-safe: FrameAccessed was set <= currentFrame when the
            // entry was created or last hit, so currentFrame - FrameAccessed is the age.
            if (currentFrame - v.FrameAccessed > maxAgeFrames)
                (toRemove ??= new List<FlowKey>()).Add(k);
        }
        if (toRemove != null)
        {
            foreach (var k in toRemove)
            {
                if (_flowCache.TryGetValue(k, out var victim))
                    _staleFlowCache[k] = victim;
                _flowCache.Remove(k);
                DiagCacheEvictions++;
            }
        }

        // Loose age-out for stale entries — anything that hasn't been rescued
        // back to live within 2x the live age-out is unlikely to be reused.
        uint staleMax = maxAgeFrames * 2u;
        List<FlowKey>? staleToRemove = null;
        foreach (var (k, v) in _staleFlowCache)
            if (currentFrame - v.FrameAccessed > staleMax)
                (staleToRemove ??= new List<FlowKey>()).Add(k);
        if (staleToRemove != null)
            foreach (var k in staleToRemove)
                _staleFlowCache.Remove(k);
    }

    /// <summary>Current flow-cache entry count. Diagnostic probe.</summary>
    public int FlowCacheSize => _flowCache.Count;

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
