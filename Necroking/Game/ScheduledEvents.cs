using System;
using System.Collections.Generic;

namespace Necroking.Game;

/// <summary>
/// The "fire a gameplay event later" half of the timing-vs-animation pattern (its sibling
/// is <see cref="Necroking.Render.AnimTiming"/>, the "make the animation fit the clock"
/// half). A sim-level queue of deferred callbacks, ticked on the simulation clock.
///
/// Use this instead of gating gameplay on <c>AnimController.IsAnimFinished</c>. When an
/// action should resolve after some seconds of "play" (a corpse dropped on the necro bench
/// finishing its put-down, then transferring + starting the craft), schedule the resolution
/// here and let the animation merely reflect the same duration via
/// <see cref="Necroking.Render.AnimTiming"/>. The gameplay fires on its own timer whether or
/// not the animation is on screen, finished early, or hitched — the two are decoupled.
///
/// Ticked from <see cref="Simulation.Tick"/> so it is deterministic and runs headless
/// (scenarios, no rendering). Callbacks are plain <see cref="Action"/>s; per the project's
/// "direct over inject" rule they typically close over ids/indices and call
/// <c>Game1.Instance</c> or sim methods directly rather than carrying injected dependencies.
/// </summary>
public sealed class ScheduledEvents
{
    private struct Entry
    {
        public float Timer;     // seconds remaining
        public Action Fire;
        public ulong Handle;
    }

    private readonly List<Entry> _entries = new();
    private ulong _nextHandle = 1;

    /// <summary>Number of events still waiting to fire (diagnostics / tests).</summary>
    public int PendingCount => _entries.Count;

    /// <summary>Schedule <paramref name="fire"/> to run <paramref name="delaySeconds"/> from
    /// now (measured on the sim clock passed to <see cref="Tick"/>). A delay ≤ 0 fires on the
    /// next tick. Returns a handle usable with <see cref="Cancel"/>.</summary>
    public ulong Schedule(float delaySeconds, Action fire)
    {
        if (fire == null) throw new ArgumentNullException(nameof(fire));
        ulong handle = _nextHandle++;
        _entries.Add(new Entry { Timer = delaySeconds, Fire = fire, Handle = handle });
        return handle;
    }

    /// <summary>Cancel a still-pending event by the handle <see cref="Schedule"/> returned.
    /// No-op (returns false) if it already fired or the handle is unknown. Callbacks should
    /// still re-validate their target on fire — a schedule that outlives its context is
    /// cheaper to guard against in the callback than to chase down every cancel site.</summary>
    public bool Cancel(ulong handle)
    {
        for (int i = 0; i < _entries.Count; i++)
            if (_entries[i].Handle == handle) { _entries.RemoveAt(i); return true; }
        return false;
    }

    /// <summary>Drop every pending event without firing (e.g. loading a new game / map).</summary>
    public void Clear() => _entries.Clear();

    /// <summary>Advance all timers by <paramref name="dt"/>; fire and remove any that reach
    /// zero, in schedule order. A callback may itself Schedule — the new entry is appended and
    /// waits for a later tick (it never fires re-entrantly this tick). Called once per
    /// <see cref="Simulation.Tick"/>.</summary>
    public void Tick(float dt)
    {
        // Two passes so a callback that schedules (appends) can't be swept up mid-iteration
        // and can't have its timer decremented twice this tick.
        int due = _entries.Count;
        for (int i = 0; i < due; i++)
        {
            var e = _entries[i];
            e.Timer -= dt;
            _entries[i] = e;
        }
        for (int i = 0; i < _entries.Count; )
        {
            if (_entries[i].Timer <= 0f)
            {
                var fire = _entries[i].Fire;
                _entries.RemoveAt(i);
                fire.Invoke();
            }
            else i++;
        }
    }
}
