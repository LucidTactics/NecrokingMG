using System.Collections.Generic;

namespace Necroking.Net;

// ⚠️ Part of the isolated networking core — see Net/README.md before modifying.

/// <summary>
/// One remote player as seen by this machine: identity plus a short buffer of
/// timestamped state snapshots. Snapshots are stamped with LOCAL arrival time
/// (no clock sync — at 8 players / 20 Hz an arrival-time buffer is plenty), and
/// <see cref="Sample"/> interpolates between the two snapshots straddling
/// "now − InterpDelay". No extrapolation: if the buffer runs dry we hold the
/// newest position (ghosts sliding through walls looks worse than a brief freeze).
/// </summary>
public sealed class RemotePlayer
{
    public byte Id { get; }
    /// <summary>UnitDef id the remote player told us they look like (e.g. "necromancer").</summary>
    public string UnitDefId { get; }

    private struct Snapshot
    {
        public double T;
        public NetPlayerState State;
    }

    private readonly List<Snapshot> _snaps = new(16);

    public RemotePlayer(byte id, string unitDefId)
    {
        Id = id;
        UnitDefId = unitDefId;
    }

    public bool HasData => _snaps.Count > 0;

    public void AddSnapshot(in NetPlayerState state, double now)
    {
        _snaps.Add(new Snapshot { T = now, State = state });
        // Keep ~1s of history; drop older (list stays tiny, O(n) removal is fine).
        while (_snaps.Count > 0 && now - _snaps[0].T > 1.0)
            _snaps.RemoveAt(0);
    }

    /// <summary>Interpolated state at render time (now − InterpDelay).
    /// Returns false until the first snapshot arrives.</summary>
    public bool Sample(double now, out NetPlayerState state)
    {
        state = default;
        if (_snaps.Count == 0) return false;

        double t = now - NetProtocol.InterpDelay;

        // Before the oldest sample → clamp to oldest.
        if (t <= _snaps[0].T) { state = _snaps[0].State; return true; }

        // Find the pair straddling t.
        for (int i = _snaps.Count - 1; i >= 0; i--)
        {
            if (_snaps[i].T <= t)
            {
                if (i == _snaps.Count - 1)
                {
                    // Newer than everything we have → hold newest, zero velocity if
                    // it's stale so the ghost doesn't look like it's moving in place.
                    state = _snaps[i].State;
                    if (now - _snaps[i].T > 0.5)
                    {
                        state.VelX = 0f;
                        state.VelY = 0f;
                    }
                    return true;
                }
                var a = _snaps[i];
                var b = _snaps[i + 1];
                float f = (float)((t - a.T) / (b.T - a.T));
                state = b.State; // non-lerped fields (ids, anim) from the newer sample
                state.X = a.State.X + (b.State.X - a.State.X) * f;
                state.Y = a.State.Y + (b.State.Y - a.State.Y) * f;
                state.Z = a.State.Z + (b.State.Z - a.State.Z) * f;
                state.VelX = a.State.VelX + (b.State.VelX - a.State.VelX) * f;
                state.VelY = a.State.VelY + (b.State.VelY - a.State.VelY) * f;
                state.Facing = LerpAngleDeg(a.State.Facing, b.State.Facing, f);
                return true;
            }
        }

        state = _snaps[0].State;
        return true;
    }

    private static float LerpAngleDeg(float a, float b, float f)
    {
        float d = ((b - a) % 360f + 540f) % 360f - 180f; // shortest signed delta
        return a + d * f;
    }
}
