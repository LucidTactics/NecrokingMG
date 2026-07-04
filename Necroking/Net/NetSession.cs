using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using LiteNetLib;
using LiteNetLib.Utils;

namespace Necroking.Net;

// ⚠️ NETWORKING CORE — brittle by design. Read Net/README.md before changing
// anything here. Key invariants:
//   • Single-threaded: every LiteNetLib event fires inside Update() → PollEvents(),
//     called from Game1.Update on the main thread. Never enable UnsyncedEvents.
//   • No references to game systems. The game talks to this class only through
//     its public surface; glue lives in Game1.Net.cs.
//   • Wire format lives in NetProtocol.cs — changing it requires a ConnectionKey bump.

public enum NetMode { Off, Host, Client }

/// <summary>
/// The whole multiplayer session: transport (LiteNetLib), peer bookkeeping,
/// player-id assignment, join/leave handshake, and the 20 Hz player-state relay.
/// Host = listen server: it is player 0 and rebroadcasts every client's state to
/// all other clients. Clients know the host's IP and connect directly.
/// </summary>
public sealed class NetSession
{
    private readonly EventBasedNetListener _listener = new();
    private NetManager? _manager;
    private readonly NetDataWriter _writer = new();

    /// <summary>Remote players by player id — everyone in the session except ourselves.
    /// The game reads this to spawn/despawn/drive ghost units.</summary>
    public IReadOnlyDictionary<byte, RemotePlayer> RemotePlayers => _remotePlayers;
    private readonly Dictionary<byte, RemotePlayer> _remotePlayers = new();

    public NetMode Mode { get; private set; } = NetMode.Off;

    /// <summary>Our player id. Host is always 0; clients get theirs from Welcome.
    /// 255 = not assigned yet (client connecting/handshaking).</summary>
    public byte LocalPlayerId { get; private set; } = UnassignedId;
    private const byte UnassignedId = 255;

    /// <summary>What our own character looks like — sent in Hello/Welcome so the far
    /// side spawns the right ghost. The game updates this before states are sent.</summary>
    public string LocalUnitDefId { get; set; } = "necromancer";

    /// <summary>True once states can flow (host running, or client welcomed).</summary>
    public bool ReadyToSend => Mode == NetMode.Host
        || (Mode == NetMode.Client && LocalPlayerId != UnassignedId && _hostPeer != null);

    /// <summary>Client-side: the connection to the host.</summary>
    private NetPeer? _hostPeer;

    /// <summary>Host-side: connected client peers → their assigned player ids.</summary>
    private readonly Dictionary<NetPeer, byte> _peerIds = new();
    private byte _nextPlayerId = 1;

    /// <summary>Recent human-readable events, newest last. Shown in the multiplayer menu.</summary>
    public IReadOnlyList<string> Log => _log;
    private readonly List<string> _log = new();

    private double _now;

    public NetSession()
    {
        _listener.ConnectionRequestEvent += OnConnectionRequest;
        _listener.PeerConnectedEvent += OnPeerConnected;
        _listener.PeerDisconnectedEvent += OnPeerDisconnected;
        _listener.NetworkReceiveEvent += OnReceive;
        _listener.NetworkErrorEvent += (endPoint, error) => AddLog($"socket error: {error}");
    }

    // ────────────────────────────────────────────────────────── lifecycle ──

    /// <summary>Start hosting. bindIp "" or "0.0.0.0" binds all adapters (recommended).</summary>
    public bool StartHost(string bindIp, int port)
    {
        Stop();
        _manager = new NetManager(_listener) { IPv6Enabled = true };
        bool ok;
        if (string.IsNullOrWhiteSpace(bindIp) || bindIp.Trim() == "0.0.0.0")
        {
            ok = _manager.Start(port);
        }
        else if (IPAddress.TryParse(bindIp.Trim(), out var addr))
        {
            ok = _manager.Start(addr, IPAddress.IPv6Any, port);
        }
        else
        {
            AddLog($"invalid bind IP '{bindIp}'");
            _manager = null;
            return false;
        }

        if (!ok)
        {
            AddLog($"failed to bind UDP port {port} (already in use?)");
            _manager = null;
            return false;
        }

        Mode = NetMode.Host;
        LocalPlayerId = 0;
        AddLog($"hosting on UDP port {port}");
        return true;
    }

    /// <summary>Connect to a host at ip:port. Accepts a hostname too (DNS-resolved).</summary>
    public bool Connect(string hostIp, int port)
    {
        Stop();
        hostIp = hostIp.Trim();
        if (hostIp.Length == 0)
        {
            AddLog("no host IP entered");
            return false;
        }
        _manager = new NetManager(_listener) { IPv6Enabled = true };
        if (!_manager.Start())
        {
            AddLog("failed to open client socket");
            _manager = null;
            return false;
        }
        Mode = NetMode.Client;
        LocalPlayerId = UnassignedId;
        try
        {
            _manager.Connect(hostIp, port, NetProtocol.ConnectionKey);
        }
        catch (Exception e)
        {
            AddLog($"connect failed: {e.Message}");
            Stop();
            return false;
        }
        AddLog($"connecting to {hostIp}:{port} ...");
        return true;
    }

    /// <summary>Tear everything down (both modes). Safe to call repeatedly.</summary>
    public void Stop()
    {
        if (_manager != null)
        {
            _manager.Stop(); // sends disconnects to peers
            _manager = null;
            AddLog(Mode == NetMode.Host ? "stopped hosting" : "disconnected");
        }
        Mode = NetMode.Off;
        LocalPlayerId = UnassignedId;
        _hostPeer = null;
        _peerIds.Clear();
        _remotePlayers.Clear();
        _nextPlayerId = 1;
    }

    /// <summary>Pump the network. Call once per frame from the main thread —
    /// every event handler below runs inside this call.</summary>
    public void Update(double now)
    {
        _now = now;
        _manager?.PollEvents();
    }

    // ─────────────────────────────────────────────────────────── sending ──

    /// <summary>Send our character's state. Called by the game at NetProtocol.SendHz.
    /// Client → host; host → all clients. Sequenced: late packets are dropped.</summary>
    public void SendLocalState(float x, float y, float z, float velX, float velY, float facing)
    {
        if (!ReadyToSend || _manager == null) return;
        var state = new NetPlayerState
        {
            PlayerId = LocalPlayerId,
            X = x, Y = y, Z = z, VelX = velX, VelY = velY, Facing = facing,
        };
        _writer.Reset();
        state.Write(_writer);
        if (Mode == NetMode.Client)
            _hostPeer?.Send(_writer, DeliveryMethod.Sequenced);
        else
            _manager.SendToAll(_writer, DeliveryMethod.Sequenced);
    }

    // ──────────────────────────────────────────────────────────── events ──

    private void OnConnectionRequest(ConnectionRequest request)
    {
        if (Mode != NetMode.Host) { request.Reject(); return; }
        if (_peerIds.Count >= NetProtocol.MaxPlayers - 1)
        {
            AddLog("rejected a connection: session full");
            request.Reject();
            return;
        }
        request.AcceptIfKey(NetProtocol.ConnectionKey);
    }

    private void OnPeerConnected(NetPeer peer)
    {
        if (Mode == NetMode.Client)
        {
            _hostPeer = peer;
            AddLog("connected to host, joining...");
            // Introduce ourselves; host answers with Welcome.
            _writer.Reset();
            _writer.Put((byte)NetMsg.Hello);
            _writer.Put(LocalUnitDefId);
            peer.Send(_writer, DeliveryMethod.ReliableOrdered);
        }
        // Host: wait for the client's Hello before assigning an id.
    }

    private void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
    {
        if (Mode == NetMode.Host)
        {
            if (_peerIds.TryGetValue(peer, out byte id))
            {
                _peerIds.Remove(peer);
                _remotePlayers.Remove(id);
                AddLog($"player {id} left ({info.Reason})");
                // Tell everyone else.
                _writer.Reset();
                _writer.Put((byte)NetMsg.PlayerLeft);
                _writer.Put(id);
                foreach (var kv in _peerIds)
                    kv.Key.Send(_writer, DeliveryMethod.ReliableOrdered);
            }
        }
        else if (Mode == NetMode.Client && peer == _hostPeer)
        {
            AddLog($"lost connection to host ({info.Reason})");
            Stop();
        }
    }

    private void OnReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
    {
        try
        {
            if (reader.AvailableBytes < 1) return;
            var msg = (NetMsg)reader.GetByte();
            switch (msg)
            {
                case NetMsg.Hello when Mode == NetMode.Host:
                    HandleHello(peer, reader);
                    break;
                case NetMsg.Welcome when Mode == NetMode.Client:
                    HandleWelcome(reader);
                    break;
                case NetMsg.PlayerJoined when Mode == NetMode.Client:
                {
                    byte id = reader.GetByte();
                    string defId = reader.GetString();
                    if (id != LocalPlayerId)
                    {
                        _remotePlayers[id] = new RemotePlayer(id, defId);
                        AddLog($"player {id} joined");
                    }
                    break;
                }
                case NetMsg.PlayerLeft when Mode == NetMode.Client:
                {
                    byte id = reader.GetByte();
                    _remotePlayers.Remove(id);
                    AddLog($"player {id} left");
                    break;
                }
                case NetMsg.State:
                    HandleState(peer, reader);
                    break;
            }
        }
        catch (Exception e)
        {
            // A malformed packet must never take the game down.
            AddLog($"bad packet dropped ({e.GetType().Name})");
        }
        finally
        {
            reader.Recycle();
        }
    }

    private void HandleHello(NetPeer peer, NetDataReader reader)
    {
        string defId = reader.GetString();
        byte id = AllocatePlayerId();
        _peerIds[peer] = id;
        _remotePlayers[id] = new RemotePlayer(id, defId);
        AddLog($"player {id} joined ({peer.Address})");

        // Welcome the newcomer: their id + everyone already here (including us, id 0).
        _writer.Reset();
        _writer.Put((byte)NetMsg.Welcome);
        _writer.Put(id);
        _writer.Put((byte)_peerIds.Count); // host + other clients = everyone except newcomer
        _writer.Put((byte)0);
        _writer.Put(LocalUnitDefId);
        foreach (var kv in _peerIds)
        {
            if (kv.Value == id) continue;
            _writer.Put(kv.Value);
            _writer.Put(_remotePlayers.TryGetValue(kv.Value, out var rp) ? rp.UnitDefId : "necromancer");
        }
        peer.Send(_writer, DeliveryMethod.ReliableOrdered);

        // Announce the newcomer to everyone else.
        _writer.Reset();
        _writer.Put((byte)NetMsg.PlayerJoined);
        _writer.Put(id);
        _writer.Put(defId);
        foreach (var kv in _peerIds)
            if (kv.Value != id)
                kv.Key.Send(_writer, DeliveryMethod.ReliableOrdered);
    }

    private void HandleWelcome(NetDataReader reader)
    {
        LocalPlayerId = reader.GetByte();
        int count = reader.GetByte();
        for (int i = 0; i < count; i++)
        {
            byte id = reader.GetByte();
            string defId = reader.GetString();
            if (id != LocalPlayerId)
                _remotePlayers[id] = new RemotePlayer(id, defId);
        }
        AddLog($"joined as player {LocalPlayerId} ({count} other player(s) here)");
    }

    private void HandleState(NetPeer peer, NetDataReader reader)
    {
        var state = NetPlayerState.Read(reader);

        if (Mode == NetMode.Host)
        {
            // Trust the peer's assigned id, not the packet's (cheap spoof guard).
            if (!_peerIds.TryGetValue(peer, out byte id)) return;
            state.PlayerId = id;
            if (_remotePlayers.TryGetValue(id, out var rp))
                rp.AddSnapshot(state, _now);

            // Relay to every other client, re-serialized with the trusted id.
            _writer.Reset();
            state.Write(_writer);
            foreach (var kv in _peerIds)
                if (kv.Value != id)
                    kv.Key.Send(_writer, DeliveryMethod.Sequenced);
        }
        else
        {
            if (state.PlayerId == LocalPlayerId) return;
            if (_remotePlayers.TryGetValue(state.PlayerId, out var rp))
                rp.AddSnapshot(state, _now);
        }
    }

    // ─────────────────────────────────────────────────────────── helpers ──

    private byte AllocatePlayerId()
    {
        // 1..254, skipping ids still in use (id 0 = host, 255 = unassigned).
        for (int tries = 0; tries < 254; tries++)
        {
            byte id = _nextPlayerId;
            _nextPlayerId = (byte)(_nextPlayerId >= 254 ? 1 : _nextPlayerId + 1);
            if (!_remotePlayers.ContainsKey(id)) return id;
        }
        return 254; // unreachable with MaxPlayers = 8
    }

    private void AddLog(string line)
    {
        _log.Add(line);
        if (_log.Count > 50) _log.RemoveAt(0);
    }

    /// <summary>One-line status for the UI.</summary>
    public string StatusLine => Mode switch
    {
        // ASCII only: the game font has no em-dash glyph (renders as '?').
        NetMode.Host => $"HOSTING - {_peerIds.Count} player(s) connected",
        NetMode.Client when LocalPlayerId != UnassignedId =>
            $"CONNECTED as player {LocalPlayerId} - {_remotePlayers.Count} other player(s)",
        NetMode.Client => "connecting...",
        _ => "offline",
    };

    /// <summary>LAN IPv4 addresses of this machine, for display in the host UI.
    /// (The public internet IP can't be discovered locally — the host has to look
    /// it up, e.g. via whatismyip — so the UI just says that.)</summary>
    public static List<string> GetLocalIPv4s()
    {
        var result = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                        result.Add(addr.Address.ToString());
            }
        }
        catch { /* purely informational */ }
        return result;
    }
}
