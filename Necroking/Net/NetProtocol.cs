using LiteNetLib.Utils;

namespace Necroking.Net;

// ⚠️ WIRE FORMAT — see Net/README.md before touching this file.
// The Put/Get order below IS the protocol. Both ends must match exactly.
// If you change any message layout, bump ConnectionKey so mismatched builds
// refuse to connect instead of reading garbage.

/// <summary>Message discriminator — first byte of every packet.</summary>
public enum NetMsg : byte
{
    /// <summary>client → host, ReliableOrdered: [defId string]. Sent once on connect.</summary>
    Hello = 1,
    /// <summary>host → client, ReliableOrdered: [yourPlayerId byte][count byte] then per player [id byte][defId string].
    /// The list includes the host itself (id 0) and every already-connected client.</summary>
    Welcome = 2,
    /// <summary>host → clients, ReliableOrdered: [id byte][defId string].</summary>
    PlayerJoined = 3,
    /// <summary>host → clients, ReliableOrdered: [id byte].</summary>
    PlayerLeft = 4,
    /// <summary>both ways, Sequenced: PlayerState payload (see below).</summary>
    State = 5,
}

/// <summary>One player-character state sample. ~28 bytes on the wire.</summary>
public struct NetPlayerState
{
    public byte PlayerId;
    public float X, Y;        // world position (Unit.Position — never RenderPos)
    public float Z;           // height above ground (jumps)
    public float VelX, VelY;
    public float Facing;      // degrees, Unit.FacingAngle
    public byte AnimId;       // reserved for later; ghosts currently render Hover regardless

    public void Write(NetDataWriter w)
    {
        w.Put((byte)NetMsg.State);
        w.Put(PlayerId);
        w.Put(X); w.Put(Y); w.Put(Z);
        w.Put(VelX); w.Put(VelY);
        w.Put(Facing);
        w.Put(AnimId);
    }

    /// <summary>Reads the payload AFTER the NetMsg.State discriminator byte.</summary>
    public static NetPlayerState Read(NetDataReader r) => new()
    {
        PlayerId = r.GetByte(),
        X = r.GetFloat(), Y = r.GetFloat(), Z = r.GetFloat(),
        VelX = r.GetFloat(), VelY = r.GetFloat(),
        Facing = r.GetFloat(),
        AnimId = r.GetByte(),
    };
}

public static class NetProtocol
{
    /// <summary>Handshake key — LiteNetLib rejects connections whose key differs.
    /// Bump the suffix whenever the wire format changes.</summary>
    public const string ConnectionKey = "necroking-mp-v1";

    /// <summary>Default UDP port. This is what gets port-forwarded on the host's router.</summary>
    public const int DefaultPort = 9050;

    /// <summary>Host + 7 clients.</summary>
    public const int MaxPlayers = 8;

    /// <summary>State send rate (per second) for the local player's character.</summary>
    public const float SendHz = 20f;

    /// <summary>How far in the past remote players are rendered (seconds). With 20 Hz
    /// snapshots this keeps two samples buffered even across a dropped packet.</summary>
    public const double InterpDelay = 0.12;
}
