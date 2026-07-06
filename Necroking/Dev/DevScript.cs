using System.Collections.Generic;

namespace Necroking.Dev;

/// <summary>
/// One step in a dev batch script. Exactly one of the fields is meaningful:
/// a command to run, or a wait (sim seconds / real seconds / frames). Waits let
/// a script land screenshots at exact moments — the runner advances them over the
/// game's own update loop, the same way a scenario's OnTick consumes sim time.
/// </summary>
public sealed class DevScriptStep
{
    public DevCommand? Cmd;     // non-null => run this command, then advance
    public float WaitSimSecs;   // >0 => wait this many SIM seconds (frozen while the sim is:
                                //       paused, world-suspended by a full-screen editor, or no
                                //       world loaded — the `job` poll reports simWaitBlockedBy.
                                //       Note `speed 0` clamps to 0.01, so only `pause` freezes.)
    public float WaitRealSecs;  // >0 => wait this many wall-clock seconds (ignores pause)
    public int WaitFrames;      // >0 => wait this many Update ticks (≈rendered frames; under
                                //       fixed-timestep catch-up several Updates can share one Draw)

    public bool IsWait => Cmd == null;
}

/// <summary>
/// A queued dev batch: a list of steps the game steps through across frames. The
/// HTTP "batch" command returns the job id immediately (so the request never
/// blocks past the proxy timeout); progress + per-step results are polled with
/// the "job" command. Only one job runs at a time — starting another while one is
/// running is rejected; 'job cancel' first.
/// </summary>
public sealed class DevJob
{
    public string Id = "";
    public List<DevScriptStep> Steps = new();

    public int Cursor;          // index of the NEXT step to start
    public float WaitSim;       // remaining sim-seconds on the active wait
    public float WaitReal;      // remaining real-seconds on the active wait
    public int WaitFrames;      // remaining frames on the active wait
    public DevCommand? InFlight; // a started command whose result isn't ready yet (e.g. screenshot)

    public List<string> Results = new(); // raw JSON response per completed command step
    public bool Done;
    public bool Canceled;
}
