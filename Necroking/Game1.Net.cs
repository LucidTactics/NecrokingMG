using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Necroking.Core;
using Necroking.Data;
using Necroking.Net;

namespace Necroking;

// Game1 partial: multiplayer glue. This is the ONLY place the game touches the
// networking core (Necroking/Net/ — see Net/README.md for the do-not-touch rules).
// Responsibilities: pump NetSession once per frame, send the local necromancer's
// state at 20 Hz, and mirror NetSession.RemotePlayers into GhostMode units.
public partial class Game1
{
    internal NetSession _net = null!;
    internal Editor.MultiplayerWindow _multiplayerWindow = null!;

    // remote playerId → ghost Unit.Id (stable across UnitArrays swap-and-pop)
    private readonly Dictionary<byte, uint> _netGhosts = new();
    private float _netSendTimer;

    /// <summary>Called at the very top of Update (beside _devServer.Drain) so the
    /// connection stays alive while paused, in menus, or unfocused.</summary>
    private void UpdateNetwork(GameTime gameTime)
    {
        if (_net == null) return;
        double now = gameTime.TotalGameTime.TotalSeconds;
        _net.Update(now);

        if (!_gameWorldLoaded)
        {
            // World gone (main menu) — units were cleared with it; forget the ghosts.
            // They respawn from RemotePlayers once a world is loaded again.
            _netGhosts.Clear();
            return;
        }

        if (_net.Mode == NetMode.Off)
        {
            DespawnAllNetGhosts();
            return;
        }

        // Keep our advertised look current (used in Hello/Welcome handshakes).
        int necroIdx = FindNecromancer();
        if (necroIdx >= 0)
            _net.LocalUnitDefId = _sim.Units[necroIdx].UnitDefID;

        ReconcileNetGhosts(now);

        // Send our state at a fixed rate, independent of frame rate.
        _netSendTimer += (float)gameTime.ElapsedGameTime.TotalSeconds;
        float interval = 1f / NetProtocol.SendHz;
        if (_netSendTimer >= interval && necroIdx >= 0 && _net.ReadyToSend)
        {
            _netSendTimer = 0f;
            var u = _sim.Units[necroIdx];
            _net.SendLocalState(u.Position.X, u.Position.Y, u.Z,
                u.Velocity.X, u.Velocity.Y, u.FacingAngle);
        }
    }

    /// <summary>Spawn ghosts for new remote players, despawn ghosts whose player
    /// left, and stamp interpolated positions into the live ones.</summary>
    private void ReconcileNetGhosts(double now)
    {
        // Despawn ghosts whose remote player disappeared.
        List<byte>? toRemove = null;
        foreach (var kv in _netGhosts)
            if (!_net.RemotePlayers.ContainsKey(kv.Key))
                (toRemove ??= new List<byte>()).Add(kv.Key);
        if (toRemove != null)
            foreach (byte pid in toRemove)
                DespawnNetGhost(pid);

        foreach (var kv in _net.RemotePlayers)
        {
            var remote = kv.Value;
            // Don't spawn until the first state arrives — otherwise the ghost
            // pops in at a stale/default position and teleports.
            if (!remote.Sample(now, out var state)) continue;

            if (!_netGhosts.TryGetValue(kv.Key, out uint ghostId)
                || !_sim.Units.TryGetIndex(ghostId, out int idx))
            {
                idx = SpawnNetGhost(remote, new Vec2(state.X, state.Y));
                if (idx < 0) continue;
                _netGhosts[kv.Key] = _sim.Units[idx].Id;
            }

            // The ghost is a puppet: position/velocity/facing come straight from
            // the interpolated network state every frame. Velocity is informational
            // (GhostMode units render the Hover anim); MoveTarget/SpawnPosition are
            // pinned so its idle AI never tries to walk it anywhere.
            _sim.UnitsMut[idx].Position = new Vec2(state.X, state.Y);
            _sim.UnitsMut[idx].Z = state.Z;
            _sim.UnitsMut[idx].Velocity = new Vec2(state.VelX, state.VelY);
            _sim.UnitsMut[idx].FacingAngle = state.Facing;
            _sim.UnitsMut[idx].MoveTarget = _sim.UnitsMut[idx].Position;
            _sim.UnitsMut[idx].SpawnPosition = _sim.UnitsMut[idx].Position;
        }
    }

    /// <summary>Spawn a GhostMode puppet unit for a remote player. Returns unit index or -1.</summary>
    private int SpawnNetGhost(RemotePlayer remote, Vec2 pos)
    {
        // Remember the real player's slot: SpawnUnit overwrites the cached
        // necromancer index when the spawned def is PlayerControlled.
        int realNecro = _sim.NecromancerIndex;

        string defId = _gameData.Units.Get(remote.UnitDefId) != null ? remote.UnitDefId : "necromancer";
        if (_gameData.Units.Get(defId) == null) return -1;

        SpawnUnit(defId, pos);
        int idx = _sim.Units.Count - 1; // SpawnUnit appends
        var id = _sim.Units[idx].Id;

        _sim.UnitsMut[idx].GhostMode = true;                 // no collision, no damage, ghost tint + Hover anim
        _sim.UnitsMut[idx].AI = AIBehavior.IdleAtPoint;      // inert — must NOT be PlayerControlled
        _sim.UnitsMut[idx].Archetype = 0;                    // no archetype brain either
        _sim.Horde.RemoveUnit(id);                           // never part of the local horde

        // Restore the local player's cached slot (guard against the ghost def
        // being PlayerControlled, which made SpawnUnit repoint it).
        _sim.SetNecromancerIndex(realNecro >= 0 && realNecro < _sim.Units.Count ? realNecro : FindNecromancer());

        DebugLog.Log("net", $"spawned ghost for player {remote.Id} ({defId}) at {pos.X:0.#},{pos.Y:0.#}");
        return idx;
    }

    private void DespawnNetGhost(byte playerId)
    {
        if (_netGhosts.TryGetValue(playerId, out uint ghostId)
            && _sim.Units.TryGetIndex(ghostId, out int idx))
        {
            _sim.RemoveUnitTracked(idx);
        }
        _netGhosts.Remove(playerId);
    }

    private void DespawnAllNetGhosts()
    {
        if (_netGhosts.Count == 0) return;
        foreach (var kv in _netGhosts)
            if (_sim.Units.TryGetIndex(kv.Value, out int idx))
                _sim.RemoveUnitTracked(idx);
        _netGhosts.Clear();
    }
}
