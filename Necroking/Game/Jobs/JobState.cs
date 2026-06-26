using System.Collections.Generic;

namespace Necroking.Game.Jobs;

/// <summary>
/// Runtime, player-tunable state for one job: where it sits in the priority
/// order, how many workers the player wants on it, and (for multi-output
/// process jobs) the maintain-stock targets per output. Pairs 1:1 with a
/// <see cref="JobDef"/>. The dispatcher in <see cref="WorkerSystem"/> reads
/// Priority + WorkerCap and writes AssignedWorkers each tick.
/// </summary>
public class JobState
{
    public JobDef Def;

    /// <summary>Player drag-order. Lower = higher priority (filled first).</summary>
    public int Priority;

    /// <summary>Player-requested worker count. Clamped to the building-derived
    /// max (instances × WorkerSlotsPerBuilding) by the dispatcher.</summary>
    public int WorkerCap;

    /// <summary>Unit ids currently assigned by the dispatcher this tick.</summary>
    public readonly List<uint> AssignedWorkers = new();

    /// <summary>Per-output maintain-stock targets (multi-output process jobs).
    /// Key = JobOutput.Id. Value = {Priority, TargetStock}. Empty for collect jobs.</summary>
    public readonly Dictionary<string, OutputTarget> OutputTargets = new();

    public JobState(JobDef def)
    {
        Def = def;
        WorkerCap = 0;
    }
}

public struct OutputTarget
{
    public int Priority;
    public int TargetStock;
}
