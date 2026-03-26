using System;
using System.Collections.Generic;
using Necroking.Core;
using Necroking.World;

namespace Necroking.Algorithm;

public struct AStarResult
{
    public bool Found;
    public List<GridCoord> Path;
    public float TotalCost;
}

public struct AStarResultWorld
{
    public bool Found;
    public List<Vec2> Waypoints;
    public float TotalCost;
}

public static class AStar
{
    private static readonly (int dx, int dy, float cost)[] Neighbors =
    {
        (1, 0, 1.0f), (-1, 0, 1.0f), (0, 1, 1.0f), (0, -1, 1.0f),
        (1, 1, 1.414f), (-1, 1, 1.414f), (1, -1, 1.414f), (-1, -1, 1.414f)
    };

    public static AStarResult Find(Func<int, int, byte> getCost, int gridW, int gridH,
                                    GridCoord start, GridCoord goal, int maxSearchTiles = 0)
    {
        var result = new AStarResult { Found = false, Path = new List<GridCoord>() };

        if (start == goal)
        {
            result.Found = true;
            result.Path.Add(start);
            return result;
        }

        var gScore = new Dictionary<long, float>();
        var cameFrom = new Dictionary<long, long>();
        var openSet = new SortedSet<(float fScore, long key)>();

        long Key(int x, int y) => ((long)y << 32) | (uint)x;

        float Heuristic(GridCoord a, GridCoord b)
        {
            int dx = Math.Abs(a.X - b.X);
            int dy = Math.Abs(a.Y - b.Y);
            return Math.Max(dx, dy) + 0.414f * Math.Min(dx, dy);
        }

        long startKey = Key(start.X, start.Y);
        gScore[startKey] = 0f;
        openSet.Add((Heuristic(start, goal), startKey));

        int expanded = 0;

        while (openSet.Count > 0)
        {
            var (_, currentKey) = openSet.Min;
            openSet.Remove(openSet.Min);

            int cx = (int)(uint)currentKey;
            int cy = (int)(currentKey >> 32);

            if (cx == goal.X && cy == goal.Y)
            {
                // Reconstruct path
                long k = currentKey;
                while (cameFrom.ContainsKey(k))
                {
                    result.Path.Add(new GridCoord((int)(uint)k, (int)(k >> 32)));
                    k = cameFrom[k];
                }
                result.Path.Add(start);
                result.Path.Reverse();
                result.Found = true;
                result.TotalCost = gScore[currentKey];
                return result;
            }

            expanded++;
            if (maxSearchTiles > 0 && expanded >= maxSearchTiles) break;

            float currentG = gScore[currentKey];

            foreach (var (dx, dy, moveCost) in Neighbors)
            {
                int nx = cx + dx;
                int ny = cy + dy;
                if (nx < 0 || nx >= gridW || ny < 0 || ny >= gridH) continue;

                byte tileCost = getCost(nx, ny);
                if (tileCost == 255) continue;

                // For diagonal movement, check both adjacent cardinal tiles are passable
                // (no corner-cutting through walls)
                if (dx != 0 && dy != 0)
                {
                    if (getCost(cx + dx, cy) >= 255) continue;
                    if (getCost(cx, cy + dy) >= 255) continue;
                }

                float tentativeG = currentG + moveCost * tileCost;
                long nKey = Key(nx, ny);

                if (!gScore.TryGetValue(nKey, out float existingG) || tentativeG < existingG)
                {
                    gScore[nKey] = tentativeG;
                    cameFrom[nKey] = currentKey;
                    float f = tentativeG + Heuristic(new GridCoord(nx, ny), goal);
                    openSet.Add((f, nKey));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// World-space convenience wrapper: converts world positions to grid coords,
    /// runs A*, then converts resulting path back to world-space waypoints.
    /// </summary>
    public static AStarResultWorld FindWorld(TileGrid grid, Vec2 startWorld, Vec2 goalWorld,
                                              int sizeTier = 0, int maxSearchTiles = 0)
    {
        var start = grid.WorldToGrid(startWorld);
        var goal = grid.WorldToGrid(goalWorld);

        var tileResult = Find(
            (x, y) => grid.GetCost(x, y, sizeTier),
            grid.Width, grid.Height, start, goal, maxSearchTiles);

        var result = new AStarResultWorld
        {
            Found = tileResult.Found,
            TotalCost = tileResult.TotalCost,
            Waypoints = new List<Vec2>()
        };

        if (tileResult.Found)
        {
            foreach (var gc in tileResult.Path)
                result.Waypoints.Add(grid.GridToWorld(gc.X, gc.Y));
        }

        return result;
    }
}
