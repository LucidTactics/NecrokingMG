using System;
using System.Collections.Generic;
using System.Text;
using Necroking.Core;

namespace Necroking.Game;

/// <summary>
/// One unit of deferred work: "do X after N seconds", declared as a named subclass so
/// active tasks can be listed and logged (dev command 'tasks', DebugLog channel "tasks").
/// This is the project's uniform delayed-execution primitive — coroutine-like, but a
/// class hierarchy instead of anonymous functions, precisely so we can see what's queued.
///
/// Declare a sealed subclass next to the domain code it belongs to (e.g.
/// <c>CorpsePutDownTask</c> in Game1.Crafting.cs), carrying the ids/indices it needs as
/// fields. Per "direct over inject", <see cref="Fire"/> calls <c>Game1.Instance</c> / sim
/// methods directly — no injected delegates. Fire must re-validate its targets: ids and
/// indices go stale, and a task that outlives its context should be a safe no-op.
///
/// Repeating work (fire every N seconds, volley follow-up shots, periodic scans) calls
/// <see cref="Repeat"/> from inside <see cref="Fire"/> to re-arm itself.
/// </summary>
public abstract class ScheduledTask
{
    /// <summary>Identity for <see cref="ScheduledTasks.Cancel(ulong)"/>. Assigned by
    /// Schedule; stable across <see cref="Repeat"/> re-arms.</summary>
    public ulong Handle { get; internal set; }

    /// <summary>Seconds until <see cref="Fire"/>, on the clock of the scheduler that owns
    /// this task. Readable by holders that render a countdown.</summary>
    public float SecondsLeft { get; internal set; }

    internal float RepeatDelay = -1f;
    internal bool AddedDuringTick;

    /// <summary>Short display name for logs / the 'tasks' dev command. Defaults to the
    /// subclass type name; override to add identifying detail (unit/spell ids).</summary>
    public virtual string Describe() => GetType().Name;

    /// <summary>The deferred work. Runs when the timer elapses; re-validate targets first.
    /// Call <see cref="Repeat"/> in here to re-arm instead of completing.</summary>
    protected internal abstract void Fire();

    /// <summary>Re-arm this task to fire again <paramref name="delaySeconds"/> from now.
    /// Only meaningful while inside <see cref="Fire"/>; the re-armed task never fires
    /// again within the same tick.</summary>
    protected void Repeat(float delaySeconds) => RepeatDelay = delaySeconds;
}

/// <summary>
/// The "fire a gameplay event later" half of the timing-vs-animation pattern (its sibling
/// is <see cref="Necroking.Render.AnimTiming"/>, the "make the animation fit the clock"
/// half). A queue of <see cref="ScheduledTask"/>s advanced by whatever clock the owner
/// ticks it with — the sim instance (<c>Simulation.Tasks</c>) runs on the sim clock from
/// <see cref="Simulation.Tick"/>, so it is deterministic, runs headless (scenarios, no
/// rendering), and dies with the Simulation on map reload.
///
/// Use this instead of gating gameplay on <c>AnimController.IsAnimFinished</c>, and
/// instead of hand-ticked countdown fields on persistent objects (see the anti-patterns
/// doc). When an action should resolve after some seconds of "play", schedule the
/// resolution here and let the animation merely reflect the same duration via
/// <see cref="Necroking.Render.AnimTiming"/> — the gameplay fires on its own timer
/// whether or not the animation is on screen, finished early, or hitched.
/// </summary>
public sealed class ScheduledTasks
{
    private readonly List<ScheduledTask> _tasks = new();
    private ulong _nextHandle = 1;
    private bool _ticking;

    /// <summary>Number of tasks still waiting to fire (diagnostics / tests).</summary>
    public int PendingCount => _tasks.Count;

    /// <summary>Schedule <paramref name="task"/> to fire <paramref name="delaySeconds"/>
    /// from now (measured on the clock passed to <see cref="Tick"/>). A delay ≤ 0 fires
    /// on the next tick. Returns the task's handle, usable with
    /// <see cref="Cancel(ulong)"/> (the task reference works too).</summary>
    public ulong Schedule(ScheduledTask task, float delaySeconds)
    {
        if (task == null) throw new ArgumentNullException(nameof(task));
        task.Handle = _nextHandle++;
        task.SecondsLeft = delaySeconds;
        task.AddedDuringTick = _ticking;
        _tasks.Add(task);
        DebugLog.Log("tasks", $"schedule {task.Describe()} in {delaySeconds:0.##}s (#{task.Handle})");
        return task.Handle;
    }

    /// <summary>Cancel a still-pending task by the handle <see cref="Schedule"/> returned.
    /// No-op (returns false) if it already fired or the handle is unknown. Fire should
    /// still re-validate its target — a task that outlives its context is cheaper to
    /// guard against in Fire than to chase down every cancel site.</summary>
    public bool Cancel(ulong handle)
    {
        for (int i = 0; i < _tasks.Count; i++)
            if (_tasks[i].Handle == handle)
            {
                DebugLog.Log("tasks", $"cancel {_tasks[i].Describe()} (#{handle})");
                _tasks.RemoveAt(i);
                return true;
            }
        return false;
    }

    /// <summary>Cancel a still-pending task by reference. Same semantics as the handle
    /// overload.</summary>
    public bool Cancel(ScheduledTask task) => task != null && Cancel(task.Handle);

    /// <summary>Drop every pending task without firing (e.g. loading a new game / map).</summary>
    public void Clear() => _tasks.Clear();

    /// <summary>One line per pending task ("Name 1.25s (#7)"), for the 'tasks' dev
    /// command / logging which tasks are currently active.</summary>
    public string DescribeActive()
    {
        if (_tasks.Count == 0) return "(no scheduled tasks)";
        var sb = new StringBuilder();
        for (int i = 0; i < _tasks.Count; i++)
        {
            if (i > 0) sb.Append('\n');
            sb.Append($"{_tasks[i].Describe()} {_tasks[i].SecondsLeft:0.##}s (#{_tasks[i].Handle})");
        }
        return sb.ToString();
    }

    /// <summary>Advance all timers by <paramref name="dt"/>; fire and remove any that
    /// reach zero, in schedule order. A Fire may itself Schedule (or Repeat) — the new
    /// task waits for a later tick, it never fires re-entrantly within this one, even
    /// with a delay ≤ 0. Called once per owner tick (sim instance:
    /// <see cref="Simulation.Tick"/>).</summary>
    public void Tick(float dt)
    {
        // Two passes so a Fire that schedules (appends) can't have its timer decremented
        // or be fired this same tick. AddedDuringTick marks appends made mid-sweep.
        _ticking = true;
        int due = _tasks.Count;
        for (int i = 0; i < due; i++)
            _tasks[i].SecondsLeft -= dt;
        for (int i = 0; i < _tasks.Count; )
        {
            var t = _tasks[i];
            if (t.AddedDuringTick || t.SecondsLeft > 0f) { i++; continue; }
            _tasks.RemoveAt(i);
            t.RepeatDelay = -1f;
            DebugLog.Log("tasks", $"fire {t.Describe()} (#{t.Handle})");
            t.Fire();
            if (t.RepeatDelay >= 0f)
            {
                // Re-arm in place at the list tail: keeps the handle, skips this tick.
                t.SecondsLeft = t.RepeatDelay;
                t.AddedDuringTick = true;
                _tasks.Add(t);
            }
        }
        _ticking = false;
        for (int i = 0; i < _tasks.Count; i++)
            _tasks[i].AddedDuringTick = false;
    }
}
