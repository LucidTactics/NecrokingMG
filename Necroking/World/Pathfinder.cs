using System;
using System.Collections.Generic;
using Necroking.Core;
using Necroking.Lib;

namespace Necroking.World;

// Classification of the path returned by GetDirection, for the per-unit debug
// overlay. Added/removed only when needed — keep the enum lean so unused
// branches don't survive as "ghost" visualization options.
public enum PathDecision : byte
{
    None = 0, ImagChunkPersist, ImagChunkRecompute, TileFlow,
    SameSectorImagChunk, BorderFlow, BFSFallback, TierFallback, ImagChunkFallback,
    BoundaryEscape, Beeline, Unreachable, UnreachableImagChunk, NoFlow, LineOfSight
}

public struct PathDecisionInfo
{
    public PathDecision Decision;
    public int BfsTargetLocalX;
    public int BfsTargetLocalY;
    public int FallbackTier;
}

/// <summary>
/// Grid pathfinding for the simulation: sector flow fields plus the hierarchical
/// portal graph (see the design block further down this file). Owned by
/// <see cref="GameSystems.Simulation"/>; deterministic and headless-safe. Steering and
/// collision avoidance are NOT here — that's Locomotion/ORCA in Movement/.
/// </summary>
public class Pathfinder
{
    public const int SectorSize = 64;

    private TileGrid? _grid;
    private int _sectorCountX, _sectorCountY;

    // =========================================================================
    // Portal graph (per tier)
    // =========================================================================
    // A portal is one maximal CONTIGUOUS run of border-tile pairs between two
    // adjacent sectors where BOTH sides are tier-passable — the exact
    // passability test the old per-side connectivity scan used. Routing over
    // portals instead of whole sectors makes inter-sector paths terrain-aware
    // (crossing a rough sector costs what its tiles cost), chokepoint-aware
    // (a 1-tile gap is one narrow portal instead of "border connected"), and
    // split-sector-aware (two internal regions of one sector simply have no
    // finite intra-sector cost between their portals, so routes can't thread
    // through the wrong region).
    private struct Portal
    {
        public int SectorA;          // owner sector (west/north side of the border)
        public int SectorB;          // neighbor sector (east/south side)
        public bool SouthBorder;     // false = A's EAST border, true = A's SOUTH border
        public short Start, End;     // inclusive local run range along the border axis (0..SectorSize-1)
        public short IdxInA, IdxInB; // position in each side's per-sector portal list
                                     // (= row/column index into that sector's portal-cost matrix)
    }

    // Local-coordinate span along one border axis; per-border span lists are
    // the source of truth the flat portal lists are rebuilt from, and give
    // InvalidateRegion a cheap "did this border's portal set change" compare.
    private struct Span { public short Start, End; }

    private List<Portal>[]? _portals;        // [tier] — flat list, portal id = list index
    private List<int>[][]? _sectorPortals;   // [tier][sectorIdx] — ids of portals touching the sector
    private List<Span>[][]? _spansE;         // [tier][sectorIdx] — spans on the sector's EAST border
    private List<Span>[][]? _spansS;         // [tier][sectorIdx] — spans on the sector's SOUTH border

    // [tier][sectorIdx] — 0 = mixed/blocked terrain (matrix needs real Dijkstras);
    // otherwise every tile of the sector is passable at this uniform cost and the
    // portal matrix is analytic (octile distance × cost — no Dijkstras at all).
    // Open grassland is simultaneously the most portal-dense case (a fully open
    // border chops into SectorSize/MaxPortalWidth portals) and the one where the
    // Dijkstras are pure waste, so this flag kills the single worst budget spike.
    private byte[][]? _sectorUniformCost;

    // Cost added per portal crossing (stepping from one side of the border to
    // the other, ~1 cardinal step at cost 1). Also keeps graph edge weights
    // strictly positive when two portals of a sector share a corner center.
    private const float PortalCrossCost = 1f;

    // Maximal portal width: longer passable runs are chopped into segments of
    // at most this many tiles. Portal costs are measured at span CENTERS, so
    // an uncapped 64-tile border portal misjudges its ends by ~32 tiles —
    // enough to route through the wrong exit. Capping bounds that error to
    // ±MaxPortalWidth/2 (the flow seeding's along-span slope covers the rest).
    private const int MaxPortalWidth = 16;

    // Intra-sector portal-to-portal cost matrix, lazily computed on the first
    // route request that expands into the sector (one window Dijkstra per
    // portal, budget-charged). Costs are in the same units the tile Dijkstra
    // accumulates (byte tile cost x StepMul8), measured center-to-center on
    // this sector's side, so they compose with flow-field seeding costs.
    private sealed class PortalMatrix
    {
        public float[] Cost = Array.Empty<float>(); // n*n; [i*N+j] = cost portal i -> portal j (list order)
        public int N;
    }
    private readonly Dictionary<int, PortalMatrix> _portalMatrixCache = new(); // key = sectorIdx * NumSizeTiers + tier

    // Per-destination remaining costs over the portal graph. Replaces the old
    // hop-count SectorRoute: true Dijkstra cost (tile-cost units) from a
    // portal to the destination tile. Each portal is TWO graph nodes
    // (nodeId = portalId * 2 + side; side 0 = SectorA's, 1 = SectorB's)
    // because remaining cost is side-dependent: standing on the side the path
    // continues into is one crossing cheaper than the far side. With a single
    // side-agnostic node, both adjacent sectors would seed the portal as an
    // "exit" at the same cost and the two border tiles would point INTO each
    // other (observed as units ping-ponging on the sector border).
    //
    // The search is a RESUMABLE, corridor-bounded A* from the destination
    // side toward the requesting unit's sector — never a whole-map Dijkstra.
    // On a 4096-sector map an exhaustive search meant thousands of lazy
    // matrix computes; budget-aborted and restarted from scratch every tick
    // it never converged (tick time ramped while every dependent flow stayed
    // deferred forever). Instead: node costs persist sparsely in Nodes; a
    // budget abort keeps all progress and the next request resumes by
    // rebuilding the open set from the unsettled frontier; a request for a
    // sector whose portals are already settled costs a dictionary probe.
    // Cached per (destSector, tier) — the first requester's dest tile seeds
    // the search; later targets in the same sector reuse it (error bounded by
    // one sector crossing, same granularity the hop scheme had). Ages out
    // with the same loose horizon flows use (EvictStaleFlowFields).
    private struct PortalNodeState
    {
        public float G;      // remaining cost to the destination (optimal once Settled)
        public bool Settled;
    }
    private sealed class PortalRoute
    {
        // Sparse, corridor-sized: only nodes the A* has touched. A node
        // absent here is unreached — unreachable if Exhausted, unknown-yet
        // otherwise. Unsettled entries are the persisted open frontier.
        public readonly Dictionary<int, PortalNodeState> Nodes = new();
        // Sectors whose flow-relevant portals are settled or provably worse
        // than the best exit by > RouteStopSlack (see ResumePortalRoute).
        public readonly HashSet<int> ResolvedSectors = new();
        // Open set ran dry: every untouched/unsettled node is unreachable,
        // so every sector is implicitly resolved.
        public bool Exhausted;
        public uint FrameAccessed;
    }
    private readonly Dictionary<int, PortalRoute> _routeCache = new(); // key = destSector * NumSizeTiers + tier

    // A* early-out slack: once the open set's best f exceeds the best settled
    // target-portal cost by this much, still-unsettled target portals can't
    // matter to the flow (their exit would be over a whole sector crossing
    // worse) and the resume stops. Bounds the corridor width off the beeline.
    private const float RouteStopSlack = SectorSize * 2f;

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

    // Per-unit imaginary chunk state. Keyed by stable Unit.Id — NOT array index.
    // UnitArrays.RemoveUnit swap-and-pops indices, so index-keyed entries would
    // silently transfer to whichever unit inherits a dead unit's slot.
    private readonly Dictionary<uint, ImaginaryChunk> _unitImagChunks = new();

    // Per-unit decision tracking (for debug). Keyed by stable Unit.Id.
    private readonly Dictionary<uint, PathDecisionInfo> _unitDecisions = new();

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
        // Negative-result memo: the last (unit tile, target tile) pair for which
        // the chunk Dijkstra failed to produce a direction. Without this, a unit
        // whose target is walled off re-ran the full 64x64 compute EVERY tick
        // (fail -> beeline into the wall -> same tiles next tick -> fail...).
        public int FailedUnitTX = -1, FailedUnitTY = -1;
        public int FailedTargetTX = -1, FailedTargetTY = -1;
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

    /// <summary>Milliseconds since a Stopwatch.GetTimestamp() sample. Replaces
    /// per-call Stopwatch.StartNew() — that allocates a Stopwatch instance on
    /// every GetDirection / flow-miss / imag-chunk call, purely for diagnostics.</summary>
    private static double ElapsedMs(long startTimestamp)
        => (System.Diagnostics.Stopwatch.GetTimestamp() - startTimestamp)
           * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

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
    public (int baseX, int baseY, int w, int h, bool active)? GetImaginaryChunkInfo(uint unitId)
    {
        if (_unitImagChunks.TryGetValue(unitId, out var ic) && ic.Active)
            return (ic.BaseX, ic.BaseY, ic.ChunkW, ic.ChunkH, ic.Active);
        return null;
    }

    /// <summary>Get all active imaginary chunk unit ids for debug overlay.</summary>
    public IEnumerable<uint> GetActiveImaginaryChunkUnits()
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
        BuildPortals();
        _portalMatrixCache.Clear();
        _partialMatrixCache.Clear();
        _routeCache.Clear();
        _flowCache.Clear();
        _staleFlowCache.Clear();
        _pendingRequests.Clear();
        _unitImagChunks.Clear();
        _unitDecisions.Clear(); // debug overlay: don't show pre-rebuild decisions
        // s_keysEverSeen tracks "has this key ever been requested in this run"
        // for miss-cause telemetry; after Rebuild all prior keys point into a
        // now-destroyed grid, so the classifier should start fresh.
        s_keysEverSeen.Clear();
        _dijkstraMsThisTick = 0f;
        _lastQueryDeferred = false;
    }

    // --- Portal extraction ---

    /// <summary>Eager whole-map portal extraction (all tiers). O(border tiles),
    /// cheap relative to the whole-grid cost rebake that precedes Rebuild().</summary>
    private void BuildPortals()
    {
        if (_grid == null) return;
        int count = _sectorCountX * _sectorCountY;
        int tiers = TerrainCosts.NumSizeTiers;
        _portals = new List<Portal>[tiers];
        _sectorPortals = new List<int>[tiers][];
        _spansE = new List<Span>[tiers][];
        _spansS = new List<Span>[tiers][];

        _sectorUniformCost = new byte[tiers][];
        for (int tier = 0; tier < tiers; tier++)
        {
            _portals[tier] = new List<Portal>();
            _sectorPortals[tier] = new List<int>[count];
            _spansE[tier] = new List<Span>[count];
            _spansS[tier] = new List<Span>[count];
            _sectorUniformCost[tier] = new byte[count];
            for (int i = 0; i < count; i++)
            {
                _sectorPortals[tier][i] = new List<int>();
                _spansE[tier][i] = new List<Span>();
                _spansS[tier][i] = new List<Span>();
            }
            for (int sy = 0; sy < _sectorCountY; sy++)
                for (int sx = 0; sx < _sectorCountX; sx++)
                {
                    ExtractSectorSpans(tier, sx, sy);
                    _sectorUniformCost[tier][SectorIdx(sx, sy)] = ComputeSectorUniformCost(tier, sx, sy);
                }
            RebuildFlatPortals(tier);
        }
    }

    /// <summary>0 if the sector mixes costs or contains any tier-impassable tile;
    /// otherwise the uniform passable cost shared by every tile. O(SectorSize²)
    /// with an early-out on the first mismatch — mixed sectors bail almost
    /// immediately, so the full scan is only paid where it buys an analytic matrix.</summary>
    private byte ComputeSectorUniformCost(int tier, int sx, int sy)
    {
        if (_grid == null) return 0;
        int baseX = sx * SectorSize, baseY = sy * SectorSize;
        int endX = Math.Min(baseX + SectorSize, _grid.Width);
        int endY = Math.Min(baseY + SectorSize, _grid.Height);
        byte c0 = _grid.GetCost(baseX, baseY, tier);
        if (c0 == 255) return 0;
        for (int y = baseY; y < endY; y++)
            for (int x = baseX; x < endX; x++)
                if (_grid.GetCost(x, y, tier) != c0) return 0;
        return c0;
    }

    /// <summary>Re-extract the E and S border spans of one sector for one
    /// tier. Returns true if either span list changed (portal set changed).
    /// Each border is owned by its west/north sector, so scanning every
    /// sector's E+S borders covers every border exactly once.</summary>
    private bool ExtractSectorSpans(int tier, int sx, int sy)
    {
        bool changed = ScanBorderSpans(tier, sx, sy, south: false);
        changed |= ScanBorderSpans(tier, sx, sy, south: true);
        return changed;
    }

    // Scratch for span extraction (single-threaded sim assumption).
    private readonly List<Span> _spanScratch = new();

    /// <summary>Append a passable run as one or more portals of at most
    /// MaxPortalWidth tiles. Pieces are distributed evenly (17 → 9+8, not
    /// 16+1) so no sliver segment masquerades as a chokepoint.</summary>
    private static void AddSpansChopped(List<Span> spans, int start, int end)
    {
        int len = end - start + 1;
        int pieces = (len + MaxPortalWidth - 1) / MaxPortalWidth;
        for (int i = 0; i < pieces; i++)
        {
            int s = start + len * i / pieces;
            int e = start + len * (i + 1) / pieces - 1;
            spans.Add(new Span { Start = (short)s, End = (short)e });
        }
    }

    private bool ScanBorderSpans(int tier, int sx, int sy, bool south)
    {
        if (_grid == null || _spansE == null || _spansS == null) return false;
        var store = south ? _spansS[tier][SectorIdx(sx, sy)] : _spansE[tier][SectorIdx(sx, sy)];
        var scratch = _spanScratch;
        scratch.Clear();

        int baseX = sx * SectorSize, baseY = sy * SectorSize;
        if (!south && sx < _sectorCountX - 1)
        {
            // East border: both columns fully exist (only the last sector
            // column can be width-clipped, and it has no east neighbor); rows
            // may be clipped at the map's bottom edge.
            int col = baseX + SectorSize - 1;
            int endY = Math.Min(baseY + SectorSize, _grid.Height);
            int runStart = -1;
            for (int y = baseY; y < endY; y++)
            {
                bool open = _grid.GetCost(col, y, tier) != 255 && _grid.GetCost(col + 1, y, tier) != 255;
                if (open) { if (runStart < 0) runStart = y; }
                else if (runStart >= 0)
                {
                    AddSpansChopped(scratch, runStart - baseY, y - 1 - baseY);
                    runStart = -1;
                }
            }
            if (runStart >= 0)
                AddSpansChopped(scratch, runStart - baseY, endY - 1 - baseY);
        }
        else if (south && sy < _sectorCountY - 1)
        {
            int row = baseY + SectorSize - 1;
            int endX = Math.Min(baseX + SectorSize, _grid.Width);
            int runStart = -1;
            for (int x = baseX; x < endX; x++)
            {
                bool open = _grid.GetCost(x, row, tier) != 255 && _grid.GetCost(x, row + 1, tier) != 255;
                if (open) { if (runStart < 0) runStart = x; }
                else if (runStart >= 0)
                {
                    AddSpansChopped(scratch, runStart - baseX, x - 1 - baseX);
                    runStart = -1;
                }
            }
            if (runStart >= 0)
                AddSpansChopped(scratch, runStart - baseX, endX - 1 - baseX);
        }

        bool changed = scratch.Count != store.Count;
        if (!changed)
            for (int i = 0; i < scratch.Count; i++)
                if (scratch[i].Start != store[i].Start || scratch[i].End != store[i].End)
                { changed = true; break; }
        if (changed)
        {
            store.Clear();
            store.AddRange(scratch);
        }
        return changed;
    }

    /// <summary>Rebuild one tier's flat portal list + per-sector id lists from
    /// the span storage. Deterministic sector-major order: a sector whose own
    /// span membership did not change keeps its list CONTENT and ORDER, so its
    /// cached portal-cost matrix stays index-aligned even though flat ids
    /// shift. Routes store per-flat-id arrays, so they must always die here.</summary>
    private void RebuildFlatPortals(int tier)
    {
        if (_portals == null || _sectorPortals == null || _spansE == null || _spansS == null) return;
        var flat = _portals[tier];
        var perSector = _sectorPortals[tier];
        flat.Clear();
        for (int i = 0; i < perSector.Length; i++) perSector[i].Clear();

        for (int sy = 0; sy < _sectorCountY; sy++)
        {
            for (int sx = 0; sx < _sectorCountX; sx++)
            {
                int a = SectorIdx(sx, sy);
                foreach (var span in _spansE[tier][a])
                {
                    int b = SectorIdx(sx + 1, sy);
                    int id = flat.Count;
                    flat.Add(new Portal
                    {
                        SectorA = a, SectorB = b, SouthBorder = false,
                        Start = span.Start, End = span.End,
                        IdxInA = (short)perSector[a].Count, IdxInB = (short)perSector[b].Count
                    });
                    perSector[a].Add(id);
                    perSector[b].Add(id);
                }
                foreach (var span in _spansS[tier][a])
                {
                    int b = SectorIdx(sx, sy + 1);
                    int id = flat.Count;
                    flat.Add(new Portal
                    {
                        SectorA = a, SectorB = b, SouthBorder = true,
                        Start = span.Start, End = span.End,
                        IdxInA = (short)perSector[a].Count, IdxInB = (short)perSector[b].Count
                    });
                    perSector[a].Add(id);
                    perSector[b].Add(id);
                }
            }
        }
        _routeCache.Clear();
    }

    /// <summary>Global tile of a portal's span center on the side facing
    /// <paramref name="sectorIdx"/> (which must be SectorA or SectorB).</summary>
    private void PortalCenterInSector(in Portal p, int sectorIdx, out int gx, out int gy)
    {
        int mid = (p.Start + p.End) / 2;
        int asx = p.SectorA % _sectorCountX, asy = p.SectorA / _sectorCountX;
        int baseX = asx * SectorSize, baseY = asy * SectorSize;
        if (!p.SouthBorder)
        {
            gy = baseY + mid;
            gx = sectorIdx == p.SectorA ? baseX + SectorSize - 1 : baseX + SectorSize;
        }
        else
        {
            gx = baseX + mid;
            gy = sectorIdx == p.SectorA ? baseY + SectorSize - 1 : baseY + SectorSize;
        }
    }

    // Scratch lists for InvalidateRegion (single-threaded sim assumption,
    // same as the rest of the pathfinder).
    private readonly List<FlowKey> _regionKeyScratch = new();
    private readonly List<uint> _regionImagScratch = new();

    /// <summary>Targeted invalidation for a collision change confined to a tile
    /// AABB (env object added/removed/collected). Replaces the full Rebuild()
    /// (~450ms on a 4097² map: whole-grid rebake + every cache cleared) with:
    /// drop flow fields of touched sectors, rescan connectivity for touched
    /// sectors, clear routes only if connectivity actually flipped, drop
    /// intersecting imaginary chunks, and reset failure memos (a removed
    /// obstacle can make a memoized-unreachable target reachable). The caller
    /// is responsible for having re-baked the tier cost fields for the region
    /// first (EnvironmentSystem.RebakeCollisionRegion).</summary>
    public void InvalidateRegion(int minTX, int minTY, int maxTX, int maxTY)
    {
        if (_grid == null || _portals == null) return;

        int s0x = Math.Clamp(minTX / SectorSize, 0, _sectorCountX - 1);
        int s1x = Math.Clamp(maxTX / SectorSize, 0, _sectorCountX - 1);
        int s0y = Math.Clamp(minTY / SectorSize, 0, _sectorCountY - 1);
        int s1y = Math.Clamp(maxTY / SectorSize, 0, _sectorCountY - 1);

        // 1. Drop flow fields (live, stale, pending) for touched sectors.
        //    Outright, not moved-to-stale: the grid changed under them, so a
        //    stale copy would steer units through the new obstacle.
        _regionKeyScratch.Clear();
        foreach (var kv in _flowCache)
            if (kv.Key.SectorX >= s0x && kv.Key.SectorX <= s1x &&
                kv.Key.SectorY >= s0y && kv.Key.SectorY <= s1y)
                _regionKeyScratch.Add(kv.Key);
        foreach (var k in _regionKeyScratch) _flowCache.Remove(k);

        _regionKeyScratch.Clear();
        foreach (var kv in _staleFlowCache)
            if (kv.Key.SectorX >= s0x && kv.Key.SectorX <= s1x &&
                kv.Key.SectorY >= s0y && kv.Key.SectorY <= s1y)
                _regionKeyScratch.Add(kv.Key);
        foreach (var k in _regionKeyScratch) _staleFlowCache.Remove(k);

        _regionKeyScratch.Clear();
        foreach (var kv in _pendingRequests)
            if (kv.Key.SectorX >= s0x && kv.Key.SectorX <= s1x &&
                kv.Key.SectorY >= s0y && kv.Key.SectorY <= s1y)
                _regionKeyScratch.Add(kv.Key);
        foreach (var k in _regionKeyScratch) _pendingRequests.Remove(k);

        // 2. Re-extract portal spans for the touched sectors plus a ±1 ring
        //    (a border's spans read one tile into each neighbor, and every
        //    border is owned by its W/N sector, so the ring covers every
        //    border a change inside the rect can affect — same reasoning the
        //    old connectivity rescan used; cheap: O(64) per border). Also
        //    drop the ring's intra-sector portal-cost matrices outright: even
        //    a span-preserving change (obstacle appearing mid-sector) alters
        //    portal-to-portal costs.
        for (int tier = 0; tier < TerrainCosts.NumSizeTiers; tier++)
        {
            bool tierChanged = false;
            for (int sy = Math.Max(0, s0y - 1); sy <= Math.Min(_sectorCountY - 1, s1y + 1); sy++)
                for (int sx = Math.Max(0, s0x - 1); sx <= Math.Min(_sectorCountX - 1, s1x + 1); sx++)
                {
                    tierChanged |= ExtractSectorSpans(tier, sx, sy);
                    int mk = SectorIdx(sx, sy) * TerrainCosts.NumSizeTiers + tier;
                    _portalMatrixCache.Remove(mk);
                    _partialMatrixCache.Remove(mk);
                    if (_sectorUniformCost != null)
                        _sectorUniformCost[tier][SectorIdx(sx, sy)] = ComputeSectorUniformCost(tier, sx, sy);
                }

            // 3. Any portal-set change shifts flat portal ids and invalidates
            //    every route's remaining costs — RebuildFlatPortals clears
            //    _routeCache (conservative, like the old connChanged logic).
            //    Span-preserving interior changes leave routes slightly stale
            //    (costs off, topology unchanged) until the route age-out;
            //    flows for the touched sectors were dropped above and get
            //    recomputed against the fresh grid, so units still steer
            //    around the new obstacle locally.
            if (tierChanged) RebuildFlatPortals(tier);
        }

        // 4. Imaginary chunks whose window intersects the rect are stale.
        //    Failure memos are reset on ALL entries — a removed obstacle can
        //    unblock a memoized-unreachable target anywhere; re-failing costs
        //    one chunk compute per affected unit, once.
        _regionImagScratch.Clear();
        foreach (var kv in _unitImagChunks)
        {
            var ic = kv.Value;
            if (ic.Active &&
                ic.BaseX <= maxTX && ic.BaseX + ic.ChunkW > minTX &&
                ic.BaseY <= maxTY && ic.BaseY + ic.ChunkH > minTY)
            {
                _regionImagScratch.Add(kv.Key);
            }
            else
            {
                ic.FailedUnitTX = -1; ic.FailedUnitTY = -1;
                ic.FailedTargetTX = -1; ic.FailedTargetTY = -1;
            }
        }
        foreach (var k in _regionImagScratch) _unitImagChunks.Remove(k);
    }

    // --- Portal-graph routing ---

    private void WorldToSector(float wx, float wy, out int sx, out int sy)
    {
        sx = Math.Clamp((int)(wx / (GameConstants.TileSize * SectorSize)), 0, _sectorCountX - 1);
        sy = Math.Clamp((int)(wy / (GameConstants.TileSize * SectorSize)), 0, _sectorCountY - 1);
    }

    private int SectorIdx(int sx, int sy) => sy * _sectorCountX + sx;

    // Goal scratch for the single-seed Dijkstras (matrix rows, dest seeding).
    private readonly List<int> _portalGoalScratch = new();
    // Graph-search PQ. Deliberately NOT _pqScratch: matrix computes run window
    // Dijkstras (which use _pqScratch) WHILE the graph PQ holds frontier nodes.
    private readonly PriorityQueue<int, float> _portalPqScratch = new();

    // Matrices interrupted by the budget mid-compute: rows [0, RowsDone) are
    // final (each row is one independent Dijkstra), the rest still InfCost.
    // Resuming continues at RowsDone — no row is ever recomputed, so even a
    // 3 ms budget makes strictly monotonic progress (≥1 row per call).
    private struct PartialMatrix { public PortalMatrix M; public int RowsDone; }
    private readonly Dictionary<int, PartialMatrix> _partialMatrixCache = new();

    /// <summary>Lazily compute (and cache) one sector's portal-to-portal cost
    /// matrix for a tier. Uniform-cost sectors (open ground — the common AND
    /// most portal-dense case) get an analytic octile matrix with no Dijkstras
    /// and no budget charge. Mixed sectors run one window Dijkstra per portal
    /// (seeded at its span center, costs read at every other portal's center;
    /// InfCost = the two portals live in different internal regions — the
    /// split-sector case hop routing got wrong), charged per ROW against the
    /// tick budget: when the budget dies mid-matrix the finished rows persist
    /// in _partialMatrixCache and the next request resumes there. Returns null
    /// when out of budget (caller defers).</summary>
    private PortalMatrix? GetPortalMatrix(int sectorIdx, int tier)
    {
        int key = sectorIdx * TerrainCosts.NumSizeTiers + tier;
        if (_portalMatrixCache.TryGetValue(key, out var m)) return m;
        if (_grid == null || _portals == null || _sectorPortals == null) return null;

        var ids = _sectorPortals[tier][sectorIdx];
        int n = ids.Count;
        var flat = _portals[tier];

        // Analytic fast path: every tile costs the same, so the cheapest
        // portal-to-portal walk is the unobstructed octile path. Free.
        byte uniform = _sectorUniformCost != null ? _sectorUniformCost[tier][sectorIdx] : (byte)0;
        if (uniform > 0)
        {
            m = new PortalMatrix { N = n, Cost = new float[n * n] };
            for (int i = 0; i < n; i++)
            {
                PortalCenterInSector(flat[ids[i]], sectorIdx, out int ax, out int ay);
                for (int j = i + 1; j < n; j++)
                {
                    PortalCenterInSector(flat[ids[j]], sectorIdx, out int bx, out int by);
                    int dx = Math.Abs(ax - bx), dy = Math.Abs(ay - by);
                    // Octile: matches RunWindowDijkstra's per-step cost model
                    // (enter-tile cost × StepMul8) on uniform ground.
                    float c = uniform * (Math.Max(dx, dy) + 0.41421f * Math.Min(dx, dy));
                    m.Cost[i * n + j] = c;
                    m.Cost[j * n + i] = c;
                }
            }
            _portalMatrixCache[key] = m;
            return m;
        }

        int startRow = 0;
        if (_partialMatrixCache.TryGetValue(key, out var partial) && partial.M.N == n)
        {
            m = partial.M;
            startRow = partial.RowsDone;
        }
        else
        {
            if (!HasDijkstraBudget()) return null; // don't even start a fresh matrix over budget
            m = new PortalMatrix { N = n, Cost = new float[n * n] };
            Array.Fill(m.Cost, GameConstants.InfCost);
        }

        int sx = sectorIdx % _sectorCountX, sy = sectorIdx / _sectorCountX;
        int baseX = sx * SectorSize, baseY = sy * SectorSize;
        int winW = Math.Min(SectorSize, _grid.Width - baseX);
        int winH = Math.Min(SectorSize, _grid.Height - baseY);

        for (int i = startRow; i < n; i++)
        {
            // ≥1 row of progress per call even when already over budget
            // (i > startRow ⇒ this call did a row) — bounded work per tick,
            // no livelock: rows done are never redone.
            if (i > startRow && !HasDijkstraBudget())
            {
                _partialMatrixCache[key] = new PartialMatrix { M = m, RowsDone = i };
                return null;
            }
            long sw0 = System.Diagnostics.Stopwatch.GetTimestamp();
            m.Cost[i * n + i] = 0f;
            PortalCenterInSector(flat[ids[i]], sectorIdx, out int cgx, out int cgy);
            _portalGoalScratch.Clear();
            _portalGoalScratch.Add((cgy - baseY) * SectorSize + (cgx - baseX));
            DiagDijkstraInvocations++;
            // dirs: null — cost-only query, skip the direction-field pass.
            if (RunWindowDijkstra(baseX, baseY, winW, winH, tier, _portalGoalScratch, null, _costScratch, null))
            {
                for (int j = 0; j < n; j++)
                {
                    if (j == i) continue;
                    PortalCenterInSector(flat[ids[j]], sectorIdx, out int qgx, out int qgy);
                    m.Cost[i * n + j] = _costScratch[(qgy - baseY) * SectorSize + (qgx - baseX)];
                }
            }
            ChargeDijkstraMs((float)ElapsedMs(sw0));
        }
        _partialMatrixCache.Remove(key);
        _portalMatrixCache[key] = m;
        return m;
    }

    /// <summary>Remaining-cost route over the portal graph for one destination,
    /// resolved lazily per requesting sector. Seeds the destination sector's
    /// portals with REAL costs from the dest tile (one window Dijkstra in
    /// that sector), then A*-expands toward <paramref name="targetSector"/>
    /// (octile heuristic), stopping as soon as that sector's flow-relevant
    /// portals are settled — matrices only get computed along the corridor,
    /// not map-wide. Everything is in tile-cost units, so the results feed
    /// straight into flow-field goal seeding. Returns null when the Dijkstra
    /// budget dies before/while computing — all progress (seeds, settled
    /// costs, frontier, matrices) persists in the cached route, so the next
    /// request RESUMES instead of restarting.</summary>
    private PortalRoute? GetPortalRoute(int destSector, int destTX, int destTY, int tier, uint frame, int targetSector)
    {
        if (_grid == null || _portals == null || _sectorPortals == null) return null;
        int routeKey = destSector * TerrainCosts.NumSizeTiers + tier;
        if (!_routeCache.TryGetValue(routeKey, out var route))
        {
            if (!HasDijkstraBudget()) return null;
            route = SeedPortalRoute(destSector, destTX, destTY, tier);
            _routeCache[routeKey] = route;
        }
        route.FrameAccessed = frame;
        if (route.Exhausted || route.ResolvedSectors.Contains(targetSector))
            return route;
        return ResumePortalRoute(route, targetSector, tier) ? route : null;
    }

    /// <summary>Create a route with just the destination-side seeds (no graph
    /// expansion yet). One budget-charged window Dijkstra in the dest sector.</summary>
    private PortalRoute SeedPortalRoute(int destSector, int destTX, int destTY, int tier)
    {
        var route = new PortalRoute();
        var flat = _portals![tier];
        var destIds = _sectorPortals![tier][destSector];
        if (destIds.Count == 0)
        {
            // Isolated destination sector: nothing to expand from — cache the
            // exhausted route so every unit heading there shares one
            // "unreachable" answer (the caller's fallback ladder takes over).
            route.Exhausted = true;
            return route;
        }

        int dsx = destSector % _sectorCountX, dsy = destSector / _sectorCountX;
        int baseX = dsx * SectorSize, baseY = dsy * SectorSize;
        int winW = Math.Min(SectorSize, _grid!.Width - baseX);
        int winH = Math.Min(SectorSize, _grid.Height - baseY);
        int localTX = Math.Clamp(destTX - baseX, 0, winW - 1);
        int localTY = Math.Clamp(destTY - baseY, 0, winH - 1);

        long sw0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _portalGoalScratch.Clear();
        _portalGoalScratch.Add(localTY * SectorSize + localTX);
        DiagDijkstraInvocations++;
        bool seeded = RunWindowDijkstra(baseX, baseY, winW, winH, tier, _portalGoalScratch, null, _costScratch, null);
        ChargeDijkstraMs((float)ElapsedMs(sw0));

        bool anyFinite = false;
        if (seeded)
        {
            foreach (int pid in destIds)
            {
                PortalCenterInSector(flat[pid], destSector, out int cgx, out int cgy);
                float c = _costScratch[(cgy - baseY) * SectorSize + (cgx - baseX)];
                if (c < GameConstants.InfCost)
                {
                    int node = pid * 2 + (flat[pid].SectorA == destSector ? 0 : 1);
                    route.Nodes[node] = new PortalNodeState { G = c };
                    anyFinite = true;
                }
            }
        }
        if (!anyFinite)
        {
            // Dest tile is walled off from every portal of its sector (or was
            // unseedable). Preserve the old hop-BFS semantics — "a route
            // exists if the sector graph connects" — by seeding all dest
            // portals at 0: units still close in, and the same-sector /
            // imaginary-chunk ladder takes over on arrival.
            route.Nodes.Clear();
            foreach (int pid in destIds)
            {
                int node = pid * 2 + (flat[pid].SectorA == destSector ? 0 : 1);
                route.Nodes[node] = new PortalNodeState { G = 0f };
            }
        }
        return route;
    }

    // Scratch for ResumePortalRoute's per-call target-node list.
    private readonly List<int> _routeTargetScratch = new();

    /// <summary>Resume the route's A* until <paramref name="targetSector"/> is
    /// resolved. Node = (portal, side). Edges: crossing to the twin side
    /// (PortalCrossCost), and walking the shared sector center-to-center to
    /// another portal's near side (intra-sector matrix, computed lazily and
    /// budget-charged). Targets are the FAR-side nodes of the target sector's
    /// portals — exactly what GetFlowToPortalSet seeds from. Returns false
    /// when the budget dies mid-search (all progress persisted; caller
    /// defers), true when the sector is resolved (all targets settled, the
    /// early-out slack tripped, or the whole graph is exhausted).</summary>
    private bool ResumePortalRoute(PortalRoute route, int targetSector, int tier)
    {
        var flat = _portals![tier];
        var targetIds = _sectorPortals![tier][targetSector];

        var targets = _routeTargetScratch;
        targets.Clear();
        int unsettled = 0;
        float bestTargetG = GameConstants.InfCost;
        foreach (int pid in targetIds)
        {
            int node = pid * 2 + (flat[pid].SectorA == targetSector ? 1 : 0);
            targets.Add(node);
            if (route.Nodes.TryGetValue(node, out var st) && st.Settled)
                bestTargetG = Math.Min(bestTargetG, st.G);
            else
                unsettled++;
        }
        if (unsettled == 0)
        {
            // Also covers a portal-less target sector: trivially resolved,
            // the flow build finds no finite portal and reports unreachable.
            route.ResolvedSectors.Add(targetSector);
            return true;
        }

        // Rebuild the open set from the persisted frontier. Correctness of
        // resuming under a DIFFERENT heuristic than previous resumes used:
        // settled g's are optimal (consistent h), frontier g's are valid
        // tentative costs — re-prioritizing them with the new target's h is
        // just A* on the residual graph.
        var pq = _portalPqScratch;
        pq.Clear();
        foreach (var kv in route.Nodes)
            if (!kv.Value.Settled)
                pq.Enqueue(kv.Key, kv.Value.G + RouteHeuristic(kv.Key, targetSector, tier));
        if (pq.Count == 0)
        {
            route.Exhausted = true;
            return true;
        }

        int pops = 0;
        while (pq.TryDequeue(out int nid, out float f))
        {
            var st = route.Nodes[nid];
            if (st.Settled) continue;
            // Early-out: pq pops in increasing f, so everything left costs
            // > bestTargetG + slack — any still-unsettled target portal is
            // irrelevant to the flow (its seed would lose by a full sector
            // crossing). Its node stays unsettled in the frontier, so a
            // later requester can still settle it.
            if (bestTargetG < GameConstants.InfCost && f > bestTargetG + RouteStopSlack)
            {
                route.ResolvedSectors.Add(targetSector);
                pq.Clear();
                return true;
            }
            if (f > st.G + RouteHeuristic(nid, targetSector, tier) + 0.001f) continue; // stale entry (relaxed since enqueue)

            int pid = nid >> 1;
            int side = nid & 1;
            var p = flat[pid];

            // Fetch the matrix BEFORE settling: settling and expanding must be
            // atomic. A settled node is never re-enqueued (the resume rebuilds
            // the open set from UNSETTLED nodes only), so bailing after the
            // settle would lose this node's out-edges from the search forever —
            // later requesters saw suboptimal costs or a false "unreachable".
            // Bailing here instead leaves the node unsettled in the frontier;
            // the next resume re-pops it and the matrix continues at its
            // persisted row (PartialMatrix), so progress is still monotonic.
            int s = side == 0 ? p.SectorA : p.SectorB;
            var m = GetPortalMatrix(s, tier);
            if (m == null) { pq.Clear(); return false; } // budget died — Nodes persist, next request resumes

            st.Settled = true;
            route.Nodes[nid] = st;

            // Twin: step across the border to the portal's other side.
            RelaxPortalNode(route, pq, pid * 2 + (side ^ 1), st.G + PortalCrossCost, targetSector, tier);

            int myIdx = side == 0 ? p.IdxInA : p.IdxInB;
            var ids = _sectorPortals[tier][s];
            for (int j = 0; j < ids.Count; j++)
            {
                int qid = ids[j];
                if (qid == pid) continue;
                float w = m.Cost[myIdx * m.N + j];
                if (w >= GameConstants.InfCost) continue;
                RelaxPortalNode(route, pq, qid * 2 + (flat[qid].SectorA == s ? 0 : 1), st.G + w, targetSector, tier);
            }

            // Target bookkeeping AFTER expansion, for the same atomicity: the
            // route object is shared by every sector requesting this dest, so
            // even the node that completes THIS resume must relax its edges
            // first — a later resume toward a farther sector routes through it.
            if (targets.Contains(nid)) // small list (sector portal count) — linear is fine
            {
                bestTargetG = Math.Min(bestTargetG, st.G);
                if (--unsettled == 0)
                {
                    route.ResolvedSectors.Add(targetSector);
                    pq.Clear();
                    return true;
                }
            }

            // Matrices dominate the cost and charge the budget themselves,
            // but a long crawl over already-cached matrices should yield too.
            if ((++pops & 63) == 0 && !HasDijkstraBudget()) { pq.Clear(); return false; }
        }

        // Open set ran dry: every reachable node is settled; anything else is
        // unreachable for good (until invalidation clears the route).
        route.Exhausted = true;
        return true;
    }

    private void RelaxPortalNode(PortalRoute route, PriorityQueue<int, float> pq, int node, float g,
                                 int targetSector, int tier)
    {
        if (route.Nodes.TryGetValue(node, out var st))
        {
            if (st.Settled || g >= st.G) return;
            st.G = g;
            route.Nodes[node] = st;
        }
        else
        {
            route.Nodes[node] = new PortalNodeState { G = g };
        }
        pq.Enqueue(node, g + RouteHeuristic(node, targetSector, tier));
    }

    /// <summary>Octile tile distance from a node's portal center to the target
    /// sector's rect. Admissible AND consistent: intra-sector edge weights are
    /// window-Dijkstra costs over tiles of cost >= 1 (so >= octile distance
    /// between the two centers), and the crossing edge (weight 1) moves the
    /// center exactly 1 tile.</summary>
    private float RouteHeuristic(int node, int targetSector, int tier)
    {
        var p = _portals![tier][node >> 1];
        PortalCenterInSector(p, (node & 1) == 0 ? p.SectorA : p.SectorB, out int gx, out int gy);
        int tsx = targetSector % _sectorCountX, tsy = targetSector / _sectorCountX;
        int bx = tsx * SectorSize, by = tsy * SectorSize;
        int dx = gx < bx ? bx - gx : Math.Max(0, gx - (bx + SectorSize - 1));
        int dy = gy < by ? by - gy : Math.Max(0, gy - (by + SectorSize - 1));
        return dx > dy ? dx + 0.41421f * dy : dy + 0.41421f * dx;
    }

    // =========================================================================
    // Window Dijkstra core (shared by sector flows and imaginary chunks)
    // =========================================================================

    // Pooled scratch for RunWindowDijkstra (single-threaded sim assumption,
    // same as the rest of the pathfinder). The cost array is pure scratch —
    // it never escapes the core or its callers. The PQ is fully drained by
    // the main pass, so the escape-propagation pass reuses the same instance.
    // `dirs` is deliberately NOT pooled: result arrays escape into _flowCache
    // and ImaginaryChunk objects, so each call needs a fresh allocation.
    private readonly float[] _costScratch = new float[SectorSize * SectorSize];
    private readonly PriorityQueue<int, float> _pqScratch = new();
    // Pooled goal-index list for the imaginary-chunk callers (the sector flow
    // callers build their own goal lists because they also carry init costs).
    private readonly List<int> _goalScratch = new();

    /// <summary>
    /// Shared window Dijkstra: fills <paramref name="cost"/> with InfCost,
    /// seeds the goal tiles (local indices with SectorSize stride) that are
    /// tier- OR base-passable (base-passable covers tier-inflated target
    /// tiles), runs the 8-dir Dijkstra with diagonal corner-cut rejection,
    /// extends costs into tier-inflated but base-passable tiles (escape
    /// propagation, uniform StepMul8 costs), and builds the direction field
    /// (with plateau fallback) into <paramref name="dirs"/> — pass null dirs
    /// for cost-only queries (portal matrices / route seeding) to skip that
    /// pass.
    /// Returns false when no goal could be seeded — cost stays all-Inf and
    /// dirs all-None. Chunk callers rely on that to preserve their historical
    /// "no seedable goal → bail without memoizing a failure" early-out.
    /// </summary>
    private bool RunWindowDijkstra(int baseX, int baseY, int winW, int winH, int tier,
                                   List<int> goals, List<float>? goalCosts,
                                   float[] cost, FlowDir[]? dirs)
    {
        if (_grid == null) return false;

        Array.Fill(cost, GameConstants.InfCost);
        var openList = _pqScratch;
        openList.Clear();

        for (int i = 0; i < goals.Count; i++)
        {
            int g = goals[i];
            int lx = g % SectorSize, ly = g / SectorSize;
            if (lx >= winW || ly >= winH) continue;
            int gx = baseX + lx, gy = baseY + ly;
            if (!_grid.InBounds(gx, gy)) continue;
            // Accept if tier-passable OR base-passable (for tier-inflated target tiles)
            if (_grid.GetCost(gx, gy, tier) != 255 || _grid.GetCost(gx, gy) != 255)
            {
                float initCost = (goalCosts != null && i < goalCosts.Count) ? goalCosts[i] : 0f;
                if (initCost < cost[g])
                {
                    cost[g] = initCost;
                    openList.Enqueue(g, initCost);
                }
            }
        }

        if (openList.Count == 0) return false;

        // 8-directional Dijkstra
        while (openList.TryDequeue(out int idx, out float pri))
        {
            if (pri > cost[idx]) continue; // stale PQ entry (tile relaxed since enqueue)
            float c = cost[idx];
            int lx = idx % SectorSize, ly = idx / SectorSize;

            for (int d = 0; d < 8; d++)
            {
                int nlx = lx + Dx8[d], nly = ly + Dy8[d];
                if (nlx < 0 || nlx >= winW || nly < 0 || nly >= winH) continue;

                int gx = baseX + nlx, gy = baseY + nly;
                byte nc = _grid.GetCost(gx, gy, tier);
                if (nc == 255) continue;

                // Diagonal corner-cutting
                if (d % 2 == 1)
                {
                    int cax = lx + Dx8[d], cay = ly;
                    int cbx = lx, cby = ly + Dy8[d];
                    if (cax >= 0 && cax < winW && _grid.GetCost(baseX + cax, baseY + cay, tier) == 255) continue;
                    if (cby >= 0 && cby < winH && _grid.GetCost(baseX + cbx, baseY + cby, tier) == 255) continue;
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
        // in inflated zones get proper directions toward the goal. Reuses the
        // main PQ — it is empty here.
        var escapePQ = openList;
        for (int ly = 0; ly < winH; ly++)
        {
            for (int lx = 0; lx < winW; lx++)
            {
                int idx = ly * SectorSize + lx;
                if (cost[idx] >= GameConstants.InfCost) continue;

                for (int d = 0; d < 8; d++)
                {
                    int nlx = lx + Dx8[d], nly = ly + Dy8[d];
                    if (nlx < 0 || nlx >= winW || nly < 0 || nly >= winH) continue;
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

        while (escapePQ.TryDequeue(out int idx, out float pri))
        {
            if (pri > cost[idx]) continue; // stale PQ entry
            float c = cost[idx];
            int lx = idx % SectorSize, ly = idx / SectorSize;

            for (int d = 0; d < 8; d++)
            {
                int nlx = lx + Dx8[d], nly = ly + Dy8[d];
                if (nlx < 0 || nlx >= winW || nly < 0 || nly >= winH) continue;
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

        // Build direction field (skipped for cost-only queries)
        if (dirs != null)
            BuildDirectionField(dirs, cost, baseX, baseY, winW, winH, tier);
        return true;
    }

    /// <summary>
    /// Core Dijkstra within a sector. Computes flow directions from goal tiles.
    /// Thin wrapper over RunWindowDijkstra: window = whole sector, clamped at
    /// the map edge for partial sectors.
    /// </summary>
    private CachedFlow ComputeSectorFlow(int sx, int sy, List<int> goalLocalIndices, int tier, uint frame,
                                          List<float>? goalInitCosts = null)
    {
        DiagDijkstraInvocations++;
        int cells = SectorSize * SectorSize;
        // Fresh dirs array per call — it goes into the flow cache (never pooled).
        var flow = new CachedFlow { Dirs = new FlowDir[cells], FrameAccessed = frame };
        if (_grid == null || goalLocalIndices.Count == 0) return flow;

        int baseX = sx * SectorSize;
        int baseY = sy * SectorSize;
        int sectorW = Math.Min(SectorSize, _grid.Width - baseX);
        int sectorH = Math.Min(SectorSize, _grid.Height - baseY);

        RunWindowDijkstra(baseX, baseY, sectorW, sectorH, tier,
                          goalLocalIndices, goalInitCosts, _costScratch, flow.Dirs);
        return flow;
    }

    /// <summary>
    /// Build direction field from integration cost array (any window; local
    /// indices use SectorSize stride). Each tile points toward the neighbor
    /// with strictly lower cost. Includes plateau fallback for flat-cost regions.
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
        // Quantize the target to a 2x2 tile bucket and seed EVERY passable tile
        // of the bucket as a cost-0 goal. Per-exact-tile keys meant a unit
        // chasing a moving target re-ran a full sector Dijkstra every time the
        // target crossed a tile line, and units with targets one tile apart
        // shared nothing — the single biggest Dijkstra-count driver. The field
        // steers to within ~1.4 tiles of the true target; callers beeline the
        // final 3 tiles, and the imag-chunk path covers the one caller that
        // doesn't. Key encodes the bucket's top-left member, so the pending-
        // request decode (ProcessPendingRequest) re-quantizes idempotently.
        localTX &= ~1;
        localTY &= ~1;
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
                // CachedFlow is a struct: write the touched copy back, or the
                // access bump is lost and an actively-used stale entry still
                // ages out of EvictStaleFlowFields mid-use.
                stale.FrameAccessed = frame;
                _staleFlowCache[key] = stale;
                return stale;
            }
            return default;
        }

        int baseX = sx * SectorSize, baseY = sy * SectorSize;

        var goals = new List<int>();
        var goalCosts = new List<float>();

        // Seed every base-passable tile of the 2x2 bucket (see quantization
        // note above) so the shared field is valid for any target inside it.
        for (int oy = 0; oy < 2; oy++)
        {
            for (int ox = 0; ox < 2; ox++)
            {
                int ltx = localTX + ox, lty = localTY + oy;
                if (ltx >= SectorSize || lty >= SectorSize) continue;
                int gx = baseX + ltx, gy = baseY + lty;
                if (_grid.InBounds(gx, gy) && _grid.GetCost(gx, gy) != 255)
                {
                    goals.Add(lty * SectorSize + ltx);
                    goalCosts.Add(0f);
                }
            }
        }

        long sw0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var flow = ComputeSectorFlow(sx, sy, goals, tier, frame, goalCosts);
        ChargeDijkstraMs((float)ElapsedMs(sw0));

        _flowCache[key] = flow;
        _staleFlowCache.Remove(key);
        return flow;
    }

    /// <summary>
    /// Portal-set flow for a cross-sector destination: seed every tile of
    /// each of the unit sector's portals that has a finite remaining cost to
    /// the destination, at goalInitCost = that remaining cost. Remaining
    /// costs and intra-window tile costs are in the same units (byte tile
    /// cost x StepMul8), so the field is exact w.r.t. the portal abstraction:
    /// it picks the globally cheaper exit (narrow-vs-wide, rough-vs-clear)
    /// instead of the old hop-count + penalty-mask heuristic — and it's
    /// shared by every unit in this sector heading to the same dest sector.
    /// <paramref name="unreachable"/> is set when the portal graph offers no
    /// route at this tier (caller runs the tier-fallback/imag-chunk ladder);
    /// a default return with unreachable=false means the request was
    /// budget-deferred (check _lastQueryDeferred).
    /// </summary>
    private CachedFlow GetFlowToPortalSet(int sx, int sy, int destSector, int tier, uint frame,
                                          Vec2 unitPos, Vec2 targetPos, out bool unreachable)
    {
        unreachable = false;
        // TargetType 3: TargetData = destination sector index. One field per
        // (unit sector, dest sector, tier) — no mask/band fragmentation.
        var key = MakeFlowKey(sx, sy, 3, destSector, -1, tier);

        if (_flowCache.TryGetValue(key, out var cached))
        {
            DiagFlowCacheHits++;
            cached.FrameAccessed = frame;
            _flowCache[key] = cached;
            return cached;
        }
        DiagFlowCacheMisses++;
        DiagMissPortalFlow++;
        if (s_keysEverSeen.Add(key)) DiagMissNewKey++; else DiagMissEvicted++;

        if (_grid == null || _portals == null || _sectorPortals == null)
            return new CachedFlow { Dirs = new FlowDir[SectorSize * SectorSize] };

        if (!HasDijkstraBudget())
        {
            EnqueueMiss(key, sx, sy, unitPos, targetPos);
            _lastQueryDeferred = true;
            if (_staleFlowCache.TryGetValue(key, out var stale))
            {
                // CachedFlow is a struct: write the touched copy back, or the
                // access bump is lost and an actively-used stale entry still
                // ages out of EvictStaleFlowFields mid-use.
                stale.FrameAccessed = frame;
                _staleFlowCache[key] = stale;
                return stale;
            }
            return default;
        }

        // Route lookup AFTER the flow-cache check: a cached field never pays
        // for a route, and a route miss (budget death mid-search) defers
        // exactly like a flow miss would. The unit's sector is the A* target:
        // the search stops once THIS sector's portals are resolved.
        int unitSector = SectorIdx(sx, sy);
        int destTX = (int)(targetPos.X / GameConstants.TileSize);
        int destTY = (int)(targetPos.Y / GameConstants.TileSize);
        var route = GetPortalRoute(destSector, destTX, destTY, tier, frame, unitSector);
        if (route == null)
        {
            EnqueueMiss(key, sx, sy, unitPos, targetPos);
            _lastQueryDeferred = true;
            if (_staleFlowCache.TryGetValue(key, out var stale))
            {
                stale.FrameAccessed = frame;
                _staleFlowCache[key] = stale;
                return stale;
            }
            return default;
        }

        var flat = _portals[tier];
        var ids = _sectorPortals[tier][unitSector];

        var goals = new List<int>();
        var goalCosts = new List<float>();
        var goalExit = new List<FlowDir>();

        foreach (int pid in ids)
        {
            var p = flat[pid];
            bool owner = p.SectorA == unitSector;
            // Seed with the FAR side's remaining cost + one crossing: "what
            // exiting here actually costs". Using this sector's own side
            // would be self-referential (it can include walking back through
            // this very sector) and made opposite border tiles of one portal
            // point into each other. A portal whose path continues back
            // through this sector carries a two-crossing penalty here, so the
            // interior route to the correct portal always beats it.
            // Only SETTLED nodes are trusted — an unsettled tentative G could
            // steer through a worse exit; unresolved-but-relevant portals are
            // > RouteStopSlack worse by the resolve contract, so skipping
            // them is safe.
            if (!route.Nodes.TryGetValue(pid * 2 + (owner ? 1 : 0), out var ns) || !ns.Settled) continue;
            float rc = ns.G + PortalCrossCost;
            FlowDir exit = p.SouthBorder ? (owner ? FlowDir.S : FlowDir.N)
                                         : (owner ? FlowDir.E : FlowDir.W);
            // Seed EVERY tile of the span on this sector's side, so wide
            // portals stay attractive across their whole width and crowds
            // don't funnel through one tile. The remaining cost is measured
            // at the span CENTER, so each tile adds |t - center| — the walk
            // to where that cost is valid (slope 1 = minimum tile cost;
            // underestimates rough spans, fine at this granularity). Without
            // the slope, span-end tiles look exactly one crossing cheap and
            // the forced-exit below fires where walking inside to a better
            // portal is strictly cheaper — units ping-ponged across borders.
            // Span-local coords are relative to SectorA's base, which the
            // B side shares along the border axis, so t works for both sides.
            int mid = (p.Start + p.End) / 2;
            for (int t = p.Start; t <= p.End; t++)
            {
                int lx, ly;
                if (!p.SouthBorder) { lx = owner ? SectorSize - 1 : 0; ly = t; }
                else { lx = t; ly = owner ? SectorSize - 1 : 0; }
                goals.Add(ly * SectorSize + lx);
                goalCosts.Add(rc + Math.Abs(t - mid));
                goalExit.Add(exit);
            }
        }

        if (goals.Count == 0)
        {
            // No portal of this sector reaches the destination at this tier —
            // genuinely unreachable via the graph (isolated sector, or the
            // unit's internal region is cut off). Same semantics the old
            // HopDist == -1 had; caller runs the fallback ladder. Not cached:
            // an all-None field would shadow later route improvements.
            unreachable = true;
            return default;
        }

        long sw0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var flow = ComputeSectorFlow(sx, sy, goals, tier, frame, goalCosts);

        // Post-process: a seeded border tile whose settled cost is still its
        // own seed (nothing cheaper reachable inside the window) must EXIT the
        // sector — BuildDirectionField's plateau fallback would otherwise
        // point it back inward. If some other portal is cheaper via an
        // interior path, the computed interior direction wins. _costScratch
        // still holds this compute's cost field (single-threaded; no Dijkstra
        // ran since ComputeSectorFlow).
        for (int i = 0; i < goals.Count; i++)
        {
            int g = goals[i];
            if (flow.Dirs[g] == FlowDir.None || _costScratch[g] >= goalCosts[i] - 0.001f)
                flow.Dirs[g] = goalExit[i];
        }
        ChargeDijkstraMs((float)ElapsedMs(sw0));

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
    private Vec2 GetLocalChunkDirection(Vec2 unitPos, Vec2 targetPos, int tier, uint unitId = GameConstants.InvalidUnit)
    {
        if (_grid == null) return Vec2.Zero;

        int unitTX = (int)(unitPos.X / GameConstants.TileSize);
        int unitTY = (int)(unitPos.Y / GameConstants.TileSize);
        int targetTX = (int)(targetPos.X / GameConstants.TileSize);
        int targetTY = (int)(targetPos.Y / GameConstants.TileSize);

        // Negative-result memo: if the last compute for this exact (unit tile,
        // target tile) pair failed, don't burn another full chunk Dijkstra —
        // nothing changed. Cleared on success and on Rebuild().
        if (unitId != GameConstants.InvalidUnit
            && _unitImagChunks.TryGetValue(unitId, out var memo)
            && memo.FailedUnitTX == unitTX && memo.FailedUnitTY == unitTY
            && memo.FailedTargetTX == targetTX && memo.FailedTargetTY == targetTY)
            return Vec2.Zero;

        DiagImagChunkComputes++;
        long _diagSw0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try {

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

        bool targetInChunk = targetTX >= baseX && targetTX < endX &&
                             targetTY >= baseY && targetTY < endY;

        // Goal seeding (chunk-specific: target tile if inside the window, else
        // target-facing border tiles). Passability of each goal tile itself
        // (tier- OR base-passable) is checked by RunWindowDijkstra; the border
        // loops additionally require the tile to be tier-passable and the
        // adjacent OUTSIDE tile to be base-passable, which is stricter and
        // stays here.
        var goals = _goalScratch;
        goals.Clear();

        if (targetInChunk)
        {
            int localTX = targetTX - baseX;
            int localTY = targetTY - baseY;
            goals.Add(localTY * SectorSize + localTX);
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
                        goals.Add(0 * SectorSize + lx);
                }
            }
            if (seedS && endY < _grid.Height)
            {
                for (int lx = 0; lx < chunkW; lx++)
                {
                    int gx = baseX + lx, gy = endY - 1;
                    if (_grid.GetCost(gx, gy, tier) == 255) continue;
                    if (_grid.GetCost(gx, gy + 1) != 255)
                        goals.Add((chunkH - 1) * SectorSize + lx);
                }
            }
            if (seedW && baseX > 0)
            {
                for (int ly = 0; ly < chunkH; ly++)
                {
                    int gx = baseX, gy = baseY + ly;
                    if (_grid.GetCost(gx, gy, tier) == 255) continue;
                    if (_grid.GetCost(gx - 1, gy) != 255)
                        goals.Add(ly * SectorSize + 0);
                }
            }
            if (seedE && endX < _grid.Width)
            {
                for (int ly = 0; ly < chunkH; ly++)
                {
                    int gx = endX - 1, gy = baseY + ly;
                    if (_grid.GetCost(gx, gy, tier) == 255) continue;
                    if (_grid.GetCost(gx + 1, gy) != 255)
                        goals.Add(ly * SectorSize + (chunkW - 1));
                }
            }
        }

        // Fresh dirs array per call — it may be stored in the ImaginaryChunk
        // (never pooled). Cost is pooled scratch; safe to read until the next
        // RunWindowDijkstra call (single-threaded).
        var dirs = new FlowDir[cells];
        float[] cost = _costScratch;
        if (!RunWindowDijkstra(baseX, baseY, chunkW, chunkH, tier, goals, null, cost, dirs))
            return Vec2.Zero; // no seedable goal — historical early-out, no failure memo

        // Extract direction at unit's tile
        int unitTileIdx = localUY * SectorSize + localUX;
        FlowDir bestDir = cost[unitTileIdx] >= GameConstants.InfCost
            ? FlowDir.None
            : dirs[unitTileIdx];

        if (unitId != GameConstants.InvalidUnit)
        {
            if (!_unitImagChunks.TryGetValue(unitId, out var ic))
            {
                ic = new ImaginaryChunk();
                _unitImagChunks[unitId] = ic;
            }
            if (bestDir != FlowDir.None)
            {
                // Store chunk per-unit for persistence; clear any failure memo.
                ic.Dirs = dirs;
                ic.BaseX = baseX;
                ic.BaseY = baseY;
                ic.ChunkW = chunkW;
                ic.ChunkH = chunkH;
                ic.TargetTX = targetTX;
                ic.TargetTY = targetTY;
                ic.Active = true;
                ic.FailedUnitTX = -1; ic.FailedUnitTY = -1;
                ic.FailedTargetTX = -1; ic.FailedTargetTY = -1;
            }
            else
            {
                // Unreachable (or plateau at the unit tile): memoize the failure
                // so the next tick's identical query is a dictionary hit, not a
                // full 64x64 Dijkstra.
                ic.Active = false;
                ic.FailedUnitTX = unitTX; ic.FailedUnitTY = unitTY;
                ic.FailedTargetTX = targetTX; ic.FailedTargetTY = targetTY;
            }
        }

        return FlowDirUtil.ToVec(bestDir);
        }
        finally
        {
            double elapsed = ElapsedMs(_diagSw0);
            DiagImagChunkMs += elapsed;
            // Charge the budget: an imag-chunk compute costs about the same as
            // the sector Dijkstra the budget exists to bound. Without this, N
            // units running chunk computes in one tick passed HasDijkstraBudget()
            // every time and the budget only throttled flow misses.
            ChargeDijkstraMs((float)elapsed);
        }
    }

    /// <summary>
    /// Recompute flow within an existing imaginary chunk's bounds for a new target.
    /// </summary>
    private Vec2 RecomputeImaginaryChunkFlow(ImaginaryChunk ic, Vec2 unitPos, Vec2 targetPos, int tier)
    {
        DiagImagChunkRecomputes++;
        long _diagSw0 = System.Diagnostics.Stopwatch.GetTimestamp();
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

        bool targetInChunk = targetTX >= baseX && targetTX < endX &&
                             targetTY >= baseY && targetTY < endY;

        // Goal seeding — same pattern as GetLocalChunkDirection (target tile if
        // inside, else target-facing borders), over the existing chunk bounds.
        // See the seeding comment there for the passability-check split.
        var goals = _goalScratch;
        goals.Clear();

        if (targetInChunk)
        {
            int localTX = targetTX - baseX;
            int localTY = targetTY - baseY;
            goals.Add(localTY * SectorSize + localTX);
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
                        goals.Add(lx);
                }

            if (seedS && endY < _grid.Height)
                for (int lx = 0; lx < chunkW; lx++)
                {
                    int gx = baseX + lx, gy = endY - 1;
                    if (_grid.GetCost(gx, gy, tier) == 255) continue;
                    if (_grid.GetCost(gx, gy + 1) != 255)
                        goals.Add((chunkH - 1) * SectorSize + lx);
                }

            if (seedW && baseX > 0)
                for (int ly = 0; ly < chunkH; ly++)
                {
                    int gx = baseX, gy = baseY + ly;
                    if (_grid.GetCost(gx, gy, tier) == 255) continue;
                    if (_grid.GetCost(gx - 1, gy) != 255)
                        goals.Add(ly * SectorSize);
                }

            if (seedE && endX < _grid.Width)
                for (int ly = 0; ly < chunkH; ly++)
                {
                    int gx = endX - 1, gy = baseY + ly;
                    if (_grid.GetCost(gx, gy, tier) == 255) continue;
                    if (_grid.GetCost(gx + 1, gy) != 255)
                        goals.Add(ly * SectorSize + (chunkW - 1));
                }
        }

        // Fresh dirs array per call — stored in the ImaginaryChunk (never pooled).
        var dirs = new FlowDir[cells];
        float[] cost = _costScratch;
        if (!RunWindowDijkstra(baseX, baseY, chunkW, chunkH, tier, goals, null, cost, dirs))
            return Vec2.Zero; // no seedable goal — historical early-out, chunk state untouched

        // Update chunk state
        ic.Dirs = dirs;
        ic.TargetTX = targetTX;
        ic.TargetTY = targetTY;

        int unitTileIdx = localUY * SectorSize + localUX;
        if (cost[unitTileIdx] >= GameConstants.InfCost) return Vec2.Zero;

        return FlowDirUtil.ToVec(ic.Dirs[unitTileIdx]);
        }
        finally
        {
            double elapsed = ElapsedMs(_diagSw0);
            DiagImagChunkMs += elapsed;
            ChargeDijkstraMs((float)elapsed); // same reasoning as GetLocalChunkDirection
        }
    }

    // (RunEscapePropagation and BuildChunkDirectionField — chunk-window copies
    //  of the sector escape pass and BuildDirectionField — were folded into
    //  RunWindowDijkstra / BuildDirectionField, 2026-07-04.)

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
    public static int DiagMissPortalFlow;     // GetFlowToPortalSet (cross-sector, portal remaining costs)
    public static int DiagLosHits;            // GetDirection answered by the straight-line shortcut
    // Tracks every FlowKey ever seen. Used to tell "new-key" vs "was-here-got-evicted".
    private static readonly System.Collections.Generic.HashSet<FlowKey> s_keysEverSeen = new();

    // Straight-line checks longer than this fall through to the flow machinery:
    // keeps the per-query cost bounded, and genuinely long hauls benefit from
    // real routing (and share flow fields with everyone heading the same way).
    private const int LosMaxTiles = 160;

    /// <summary>Is the straight segment a→b walkable at this size tier? Grid DDA
    /// (Amanatides &amp; Woo) over the tier-inflated cost field, visiting every
    /// crossed tile; an exact corner crossing requires BOTH adjacent tiles open
    /// (same no-corner-cutting rule as RunWindowDijkstra). The tier field is
    /// radius-inflated, so tile passability along the line == the unit fits.
    /// Ignores tile COST variation (a clear line through mud beelines through
    /// the mud) — same tradeoff the short-distance beeline in MoveToward makes.</summary>
    private bool HasLineOfSight(Vec2 a, Vec2 b, int tier)
    {
        if (_grid == null) return false;
        float ts = GameConstants.TileSize;
        int x0 = (int)(a.X / ts), y0 = (int)(a.Y / ts);
        int x1 = (int)(b.X / ts), y1 = (int)(b.Y / ts);
        if (!_grid.InBounds(x0, y0) || !_grid.InBounds(x1, y1)) return false;
        if (Math.Abs(x1 - x0) + Math.Abs(y1 - y0) > LosMaxTiles) return false;
        // Unit inside a tier-inflated tile: the ladder's escape propagation is
        // what knows how to get OUT — never claim LOS from in there.
        if (_grid.GetCost(x0, y0, tier) == 255) return false;

        float dx = b.X - a.X, dy = b.Y - a.Y;
        int stepX = dx > 0 ? 1 : -1, stepY = dy > 0 ? 1 : -1;
        float tMaxX = dx != 0 ? (((dx > 0 ? x0 + 1 : x0) * ts) - a.X) / dx : float.PositiveInfinity;
        float tMaxY = dy != 0 ? (((dy > 0 ? y0 + 1 : y0) * ts) - a.Y) / dy : float.PositiveInfinity;
        float tDeltaX = dx != 0 ? ts / Math.Abs(dx) : float.PositiveInfinity;
        float tDeltaY = dy != 0 ? ts / Math.Abs(dy) : float.PositiveInfinity;

        int x = x0, y = y0;
        int guard = LosMaxTiles * 2 + 4; // FP safety net — cannot loop forever
        while ((x != x1 || y != y1) && guard-- > 0)
        {
            if (Math.Abs(tMaxX - tMaxY) < 1e-6f)
            {
                // Exact corner crossing: both orthogonal neighbors must be open
                // or the unit would cut the corner between two blockers.
                if (_grid.GetCost(x + stepX, y, tier) == 255) return false;
                if (_grid.GetCost(x, y + stepY, tier) == 255) return false;
                x += stepX; y += stepY; tMaxX += tDeltaX; tMaxY += tDeltaY;
            }
            else if (tMaxX < tMaxY) { x += stepX; tMaxX += tDeltaX; }
            else { y += stepY; tMaxY += tDeltaY; }
            if (!_grid.InBounds(x, y) || _grid.GetCost(x, y, tier) == 255) return false;
        }
        return x == x1 && y == y1; // false only when the guard ran out mid-line
    }

    public Vec2 GetDirection(Vec2 unitPos, Vec2 targetPos, uint frame, int sizeTier = 0, uint unitId = GameConstants.InvalidUnit)
    {
        long diagSw0 = System.Diagnostics.Stopwatch.GetTimestamp();
        DiagCallsThisTick++;
        try
        {
        if (_grid == null || _portals == null) return Vec2.Zero;
        sizeTier = Math.Clamp(sizeTier, 0, TerrainCosts.NumSizeTiers - 1);

        // Reset the deferral flag for this query; GetFlow* sets it when it
        // returns stale/empty flow due to a spent per-tick Dijkstra budget.
        _lastQueryDeferred = false;

        // --- Per-unit persistent imaginary chunk ---
        if (unitId != GameConstants.InvalidUnit && _unitImagChunks.TryGetValue(unitId, out var existingIc) && existingIc.Active)
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
                            RecordDecision(unitId, PathDecision.ImagChunkRecompute);
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
                        RecordDecision(unitId, PathDecision.ImagChunkPersist);
                        return dir;
                    }
                    existingIc.Active = false;
                }
            }
        }

        // --- Straight-line shortcut ---
        // If the direct line to the target is tier-passable the whole way,
        // just walk it: on open ground every flow field, portal route, and
        // matrix below only reproduces this answer at ~1000× the cost (and
        // wander/flee targets churn keys so those fields rarely get reused).
        // The expensive machinery is reserved for queries where the straight
        // path is actually blocked — the only case it improves on a beeline.
        if (HasLineOfSight(unitPos, targetPos, sizeTier))
        {
            var losDir = targetPos - unitPos;
            float losLen = losDir.Length();
            if (losLen > 0.01f)
            {
                DiagLosHits++;
                RecordDecision(unitId, PathDecision.LineOfSight);
                return losDir * (1f / losLen);
            }
            return Vec2.Zero; // standing on the target
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
                    Vec2 localDir = _lastQueryDeferred ? Vec2.Zero : GetLocalChunkDirection(unitPos, targetPos, sizeTier, unitId);
                    if (localDir.LengthSq() > 0.001f)
                    {
                        RecordDecision(unitId, PathDecision.SameSectorImagChunk);
                        return localDir;
                    }
                    // Beeline
                    var bee = targetPos - unitPos;
                    float beeLen = bee.Length();
                    RecordDecision(unitId, PathDecision.Unreachable);
                    return beeLen > 0.01f ? bee * (1f / beeLen) : Vec2.Zero;
                }
            }
        }
        else
        {
            // Different sector: portal-graph routing + portal-set flow. The
            // field's border seeds carry the graph's true remaining costs, so
            // the whole heuristic mask/penalty scheme the old multi-border
            // flow needed is gone.
            flow = GetFlowToPortalSet(unitSX, unitSY, targetSector, sizeTier, frame,
                                      unitPos, targetPos, out bool unreachable);
            if (flow.Dirs != null)
            {
                hasFlow = true;
            }
            else if (unreachable)
            {
                // Unreachable for this tier — try lower tier fallback (a big
                // unit whose tier graph is severed can still follow a smaller
                // tier's field; movement squeezes via escape directions).
                for (int fallback = sizeTier - 1; fallback >= 0; fallback--)
                {
                    var fbFlow = GetFlowToPortalSet(unitSX, unitSY, targetSector, fallback, frame,
                                                    unitPos, targetPos, out _);
                    if (fbFlow.Dirs != null)
                    {
                        flow = fbFlow;
                        hasFlow = true;
                        break;
                    }
                    // Deferred mid-fallback: beeline below, NOT imag chunk
                    // (which costs what the deferred Dijkstra would have).
                    if (_lastQueryDeferred) break;
                }

                if (!hasFlow && !_lastQueryDeferred)
                {
                    // Try imaginary chunk before beeline
                    Vec2 localDir = GetLocalChunkDirection(unitPos, targetPos, sizeTier, unitId);
                    if (localDir.LengthSq() > 0.001f)
                    {
                        RecordDecision(unitId, PathDecision.UnreachableImagChunk);
                        return localDir;
                    }
                    var dd = targetPos - unitPos;
                    float len = dd.Length();
                    RecordDecision(unitId, PathDecision.Unreachable);
                    return len > 0.01f ? dd * (1f / len) : Vec2.Zero;
                }
            }
            // else: budget-deferred with no stale fallback — the !hasFlow
            // branch below beelines this tick (BeginTick fills the flow next).
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
                    RecordDecision(unitId, PathDecision.Beeline);
                    return beeD * (1f / beeDLen);
                }
                RecordDecision(unitId, PathDecision.None);
                return Vec2.Zero;
            }
            Vec2 localDir = GetLocalChunkDirection(unitPos, targetPos, sizeTier, unitId);
            if (localDir.LengthSq() > 0.001f)
            {
                RecordDecision(unitId, PathDecision.NoFlow);
                return localDir;
            }
            RecordDecision(unitId, PathDecision.None);
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
                RecordDecision(unitId, PathDecision.BFSFallback);
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
                    fbFlow = GetFlowToPortalSet(unitSX, unitSY, targetSector, fallbackTier, frame,
                                                unitPos, targetPos, out _);
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
                    RecordDecision(unitId, PathDecision.TierFallback, -1, -1, fallbackTier);
                    return fbBestDir;
                }
            }

            // Try imaginary chunk
            {
                Vec2 localDir = _lastQueryDeferred ? Vec2.Zero : GetLocalChunkDirection(unitPos, targetPos, sizeTier, unitId);
                if (localDir.LengthSq() > 0.001f)
                {
                    RecordDecision(unitId, PathDecision.ImagChunkFallback);
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
                    RecordDecision(unitId, PathDecision.BoundaryEscape);
                    return boundaryDir;
                }
            }

            // Last resort: beeline
            var diff = targetPos - unitPos;
            float diffLen = diff.Length();
            RecordDecision(unitId, PathDecision.Beeline);
            return diffLen > 0.01f ? diff * (1f / diffLen) : Vec2.Zero;
        }

        // Normal flow direction
        RecordDecision(unitId, unitSector == targetSector ? PathDecision.TileFlow : PathDecision.BorderFlow);
        return finalDir;
        }
        finally
        {
            DiagTotalMsThisTick += ElapsedMs(diagSw0);
        }
    }

    // --- Decision tracking ---

    private void RecordDecision(uint unitId, PathDecision decision, int bfsLX = -1, int bfsLY = -1, int fbTier = -1)
    {
        if (unitId != GameConstants.InvalidUnit)
            _unitDecisions[unitId] = new PathDecisionInfo
            {
                Decision = decision,
                BfsTargetLocalX = bfsLX,
                BfsTargetLocalY = bfsLY,
                FallbackTier = fbTier
            };
    }

    public PathDecisionInfo? GetUnitDecision(uint unitId)
    {
        return _unitDecisions.TryGetValue(unitId, out var info) ? info : null;
    }

    /// <summary>Drop a unit's persistent imaginary chunk and debug decision.
    /// Call when a unit is removed so the dictionaries don't accumulate
    /// entries for ids that will never query again.</summary>
    public void ClearImaginaryChunk(uint unitId)
    {
        _unitImagChunks.Remove(unitId);
        _unitDecisions.Remove(unitId);
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

        foreach (var (key, req) in sorted)
        {
            if (_dijkstraMsThisTick >= DijkstraBudgetMsPerTick) break;
            if (_flowCache.ContainsKey(key)) continue;  // already computed via another path
            ProcessPendingRequest(key, req, frame);
        }
        _pendingRequests.Clear();
    }

    private void ProcessPendingRequest(FlowKey key, in PendingRequest req, uint frame)
    {
        // FlowKey round-trips: TargetData encodes per-type params, invertibly.
        switch (key.TargetType)
        {
            // (TargetType 0 single-border and 2 multi-border flows are gone —
            // 0 deleted 2026-07-04, 2 replaced by portal-set flows 2026-07-04;
            // nothing enqueues those key types anymore.)
            case 1: // tile
                int tLY = key.TargetData / SectorSize;
                int tLX = key.TargetData - tLY * SectorSize;
                GetFlowToTile(key.SectorX, key.SectorY, tLX, tLY, key.SizeTier, frame);
                break;
            case 3: // portal-set flow: TargetData = destination sector index.
                // The stored request's TargetPos seeds the dest-side Dijkstra
                // if the route isn't cached yet (the key alone doesn't carry
                // the dest tile). May re-defer if this tick's budget dies
                // mid-drain; the re-enqueue is wiped by the Clear() above and
                // the unit re-enqueues on its next query — same semantics
                // deferred tile flows have.
                GetFlowToPortalSet(key.SectorX, key.SectorY, key.TargetData, key.SizeTier, frame,
                                   req.UnitPos, req.TargetPos, out _);
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
    // (The old size-capped EvictFlowFields/EvictRoutes were deleted 2026-07-04:
    //  neither ever had a caller, and both were O(n) full scans per evicted
    //  entry. Age-based eviction below is the one live policy.)

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

        // Routes age out on the same loose horizon. They previously had NO
        // eviction at all — a session visiting many distinct destinations
        // accumulated (sectors x tiers)-sized arrays forever.
        List<int>? routesToRemove = null;
        foreach (var (k, v) in _routeCache)
            if (currentFrame - v.FrameAccessed > staleMax)
                (routesToRemove ??= new List<int>()).Add(k);
        if (routesToRemove != null)
            foreach (var k in routesToRemove)
                _routeCache.Remove(k);
    }

    /// <summary>Current flow-cache entry count. Diagnostic probe.</summary>
    public int FlowCacheSize => _flowCache.Count;
}
