# Necroking/Net — multiplayer core (HANDLE WITH CARE)

> ⚠️ **WARNING TO FUTURE CLAUDES / CONTRIBUTORS**
>
> This folder is the game's network transport and protocol layer. It is deliberately
> **isolated from the rest of the codebase** and is **brittle by nature**: a change that
> compiles fine can silently break wire compatibility between two players' builds, or
> introduce a threading bug that only shows up over the internet.
>
> **Do not modify files in this folder** unless the task is explicitly about multiplayer
> networking and you are sure the change is what was asked for. In particular:
> - Do not "clean up", reorder, or re-type fields in `NetProtocol.cs` — the byte-for-byte
>   write/read order **is** the wire format. Both sides must agree exactly. If you change
>   the format, bump `NetProtocol.ConnectionKey` so old/new builds refuse to connect
>   instead of desyncing.
> - Do not add callbacks that touch game state from LiteNetLib events outside of
>   `PollEvents()` — everything here relies on the single-threaded poll model
>   (`NetSession.Update` is called from `Game1.Update`; all events fire there).
> - Do not add references from this folder to game systems (`Simulation`, `Game1`, UI…).
>   The dependency points one way: the game reads/writes `NetSession`'s public surface
>   (`RemotePlayers`, `SendLocalState`, …). Glue code lives in `Game1.Net.cs`, not here.

## What this is

Milestone-1 "player ghosts" networking for up to 8 players, one of whom is the **host**
(listen server). Clients connect directly to the host's IP (port forwarding required over
the internet). Transport is **LiteNetLib** (reliable-UDP, NuGet package), polled on the
main thread — no locks, no game-side threading.

- `NetProtocol.cs` — message ids, wire format for player state, connection key, port.
- `RemotePlayer.cs` — per-remote-player snapshot buffer + interpolation (render ~120 ms
  in the past, lerp between the two snapshots straddling the render time; no extrapolation).
- `NetSession.cs` — the whole session state machine: host/connect/stop, peer bookkeeping,
  player-id assignment, hello/welcome/join/leave handshake, 20 Hz state relay
  (host rebroadcasts every client's state to all other clients).

## Architecture (why it looks like this)

Host-authoritative listen server — the standard for the genre (V Rising co-op, PoE, D3/D4).
For this milestone each player is authoritative over **their own character only** and the
host relays states. When world/enemy sync comes later, the host simulates and replicates
down the same pipe; nothing here needs throwing away. Movement uses **Sequenced** delivery
(stale packets dropped), handshake/join/leave use **ReliableOrdered**.

The game-facing integration (ghost unit spawning, per-frame state stamping, the pause-menu
Multiplayer window) lives OUTSIDE this folder:
- `Necroking/Game1.Net.cs` — glue partial: polls the session, spawns/despawns ghost units,
  sends the local necromancer's state at 20 Hz.
- `Necroking/UI/MultiplayerWindow.cs` — the pause-menu UI.
