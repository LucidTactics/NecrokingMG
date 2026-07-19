## Multiplayer Networking (`Necroking/Net/`) — DO NOT CASUALLY EDIT
The multiplayer transport/protocol core lives in **`Necroking/Net/`** and is deliberately
isolated and **brittle**: field order in the packet code IS the wire format, and everything
assumes single-threaded polling from `Game1.Update`. **Do not modify anything under
`Necroking/Net/` unless the task is explicitly about multiplayer networking** — read
[Necroking/Net/README.md](../../Necroking/Net/README.md) first; it has the invariants (wire
format + `ConnectionKey` bump rule, no game-system references from that folder, poll-model
threading). Game-facing glue lives in `Necroking/Game1.Net.cs` (ghost units, send loop)
and `Necroking/UI/MultiplayerWindow.cs` (pause-menu UI) — those are normal code and
safe to change.
