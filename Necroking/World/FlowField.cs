using Necroking.Core;

namespace Necroking.World;

// NOTE: the legacy whole-map FlowField/FlowFieldManager that used to live here
// (pre-sector-pathfinder) was deleted 2026-07-04 — it had zero gameplay callers,
// was never invalidated on wall changes, and a single GetFlowField call on the
// 4097² default map would have allocated ~84MB per destination. The sector
// pathfinder (Pathfinder.cs) is the one flow-field system. Only the shared
// direction enum + vector table remain.

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
