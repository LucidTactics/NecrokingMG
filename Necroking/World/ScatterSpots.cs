using System;
using Necroking.Core;

namespace Necroking.World;

/// <summary>
/// Deterministic "find a walkable spawn spot" scatter — the single retry core
/// behind village/zone population placement (previously triplicated as
/// ScatterSpot / ScatterSpotInRect / ScatterSpotNear). Mechanics are shared:
/// 24 tries, the classic LCG (×1664525 +1013904223) advanced by ref so call
/// sites keep their deterministic streams, IsPointWalkable at radius 0.5f,
/// fall back to the region center when nothing walkable turns up. Callers own
/// only the sampled region (annulus or rect/box).
///
/// DETERMINISM: the draw order, LCG constants, and per-shape candidate math are
/// byte-identical to the legacy copies — map population layouts must not shift.
/// </summary>
internal static class ScatterSpots
{
    private const int Tries = 24;
    private const float WalkProbeRadius = 0.5f;

    private enum Shape { Annulus, Rect }

    /// <summary>Walkable point in the annulus [minR, maxR] around
    /// <paramref name="center"/>; the center itself when all tries fail.</summary>
    public static Vec2 InAnnulus(TileGrid? grid, Vec2 center, float minR, float maxR, ref uint rng)
        => Scatter(Shape.Annulus, grid, center, minR, maxR, ref rng);

    /// <summary>Walkable point in the axis-aligned rect of half-extents
    /// (<paramref name="halfW"/>, <paramref name="halfH"/>) ×
    /// <paramref name="extentScale"/> around <paramref name="center"/>; the
    /// center itself when all tries fail. A square box (halfW == halfH ==
    /// radius) is the "near a point" case. extentScale is a separate factor —
    /// NOT pre-multiplied into the half-extents — so the candidate arithmetic
    /// stays bit-identical to the legacy zone copy ((fx·halfW)·scale).</summary>
    public static Vec2 InRect(TileGrid? grid, Vec2 center, float halfW, float halfH,
        ref uint rng, float extentScale = 1f)
        => Scatter(Shape.Rect, grid, center, halfW, halfH, ref rng, extentScale);

    private static Vec2 Scatter(Shape shape, TileGrid? grid, Vec2 center,
        float p0, float p1, ref uint rng, float extentScale = 1f)
    {
        for (int a = 0; a < Tries; a++)
        {
            Vec2 p;
            if (shape == Shape.Annulus)
            {
                rng = rng * 1664525u + 1013904223u;
                float ang = (rng % 62832u) / 10000f;
                rng = rng * 1664525u + 1013904223u;
                float r = p0 + (rng % 1000u) / 1000f * (p1 - p0);
                p = center + new Vec2(MathF.Cos(ang) * r, MathF.Sin(ang) * r);
            }
            else
            {
                rng = rng * 1664525u + 1013904223u;
                float fx = ((rng % 1000u) / 1000f - 0.5f) * 2f;
                rng = rng * 1664525u + 1013904223u;
                float fy = ((rng % 1000u) / 1000f - 0.5f) * 2f;
                p = new Vec2(center.X + fx * p0 * extentScale, center.Y + fy * p1 * extentScale);
            }
            if (grid == null || AI.SubroutineSteps.IsPointWalkable(grid, p, WalkProbeRadius))
                return p;
        }
        return center;
    }
}
