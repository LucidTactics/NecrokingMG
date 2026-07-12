using System;
using System.Collections.Generic;
using Necroking.Core;
using Necroking.Lib;

namespace Necroking.Algorithm;

/// <summary>
/// Standalone spatial-scatter helpers: lay out N points around a center so they
/// don't overlap. The canonical "spread a pile of things on the ground" utility —
/// e.g. mushrooms bursting out of a dead zombie boar's belly.
///
/// <see cref="HexPack"/> uses a hexagonal spiral (the densest regular circle
/// packing) so points fill outward evenly, plus per-point random jitter so the
/// result reads as a natural scatter rather than a rigid lattice.
/// </summary>
public static class ScatterPacking
{
    // Axial (q, r) hex directions, used to walk each ring of the spiral.
    private static readonly (int q, int r)[] AxialDirs =
    {
        (1, 0), (1, -1), (0, -1), (-1, 0), (-1, 1), (0, 1),
    };

    /// <summary>
    /// Return <paramref name="count"/> world positions hex-packed around
    /// <paramref name="center"/>, each nudged by up to <paramref name="jitter"/>
    /// world units of random noise so nothing lands exactly on the lattice.
    /// The first point is the center itself; the rest spiral outward ring by ring
    /// with neighbours <paramref name="spacing"/> apart.
    /// </summary>
    public static List<Vec2> HexPack(Vec2 center, int count, float spacing, float jitter, Random rng)
    {
        var result = new List<Vec2>(Math.Max(0, count));
        if (count <= 0) return result;

        result.Add(Jittered(center, jitter, rng));

        int ring = 1;
        while (result.Count < count)
        {
            // Start each ring at the cell `ring` steps along direction 4, then walk
            // the six sides. This is the standard red-blob hex-ring traversal.
            int q = AxialDirs[4].q * ring;
            int r = AxialDirs[4].r * ring;
            for (int side = 0; side < 6 && result.Count < count; side++)
            {
                for (int step = 0; step < ring && result.Count < count; step++)
                {
                    Vec2 p = center + AxialToWorld(q, r, spacing);
                    result.Add(Jittered(p, jitter, rng));
                    q += AxialDirs[side].q;
                    r += AxialDirs[side].r;
                }
            }
            ring++;
        }
        return result;
    }

    // Pointy-top axial → world offset (in units of `spacing`).
    private static Vec2 AxialToWorld(int q, int r, float spacing)
    {
        float x = spacing * (1.7320508f * (q + r * 0.5f)); // sqrt(3) * (q + r/2)
        float y = spacing * (1.5f * r);
        return new Vec2(x, y);
    }

    private static Vec2 Jittered(Vec2 p, float jitter, Random rng)
    {
        if (jitter <= 0f) return p;
        float jx = ((float)rng.NextDouble() * 2f - 1f) * jitter;
        float jy = ((float)rng.NextDouble() * 2f - 1f) * jitter;
        return new Vec2(p.X + jx, p.Y + jy);
    }
}
