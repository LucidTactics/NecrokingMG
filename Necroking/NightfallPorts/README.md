# NightfallPorts

Faithful ports of systems from the **Nightfall Rogue** project
(`../NightfallRogue`) — a separate, extensively-tested C# codebase. When a
Nightfall system is better-tested or more capable than its Necroking equivalent,
it gets re-implemented here rather than reinvented.

**This folder accepts Nightfall ports only.** Don't drop unrelated code here — a
new gameplay system belongs in `Game/` (namespace `Necroking.GameSystems`) per
[docs/code-map.md](../../docs/code-map.md).

Porting guidelines:
- Keep the port **self-contained** — prefer holding per-unit state inside the
  port (e.g. a `Dictionary<uint, …>` keyed by `Unit.Id`) over adding fields to
  the shared `Unit` model, so a port can be added/removed without touching the
  engine's data model.
- Reuse existing engine primitives where they already exist (`Unit.Z` for
  height, `AnimController` for playback, `Unit.Jumping` to suppress AI/ORCA).
- Comment each ported helper with its Nightfall origin (file + function) so the
  two can be diffed later.
- Namespace: `Necroking.NightfallPorts`.

## Contents

- **`RogueJump.cs`** — a scripted parabolic leap ported from
  `Unit_MoveSpecial.ProcessJumpAction`. Its defining trait is that it owns no
  dedicated jump sprites: it **abuses partial states of existing animations** —
  `Standup` seeked to its midpoint for the launch spring, `Fall` held for the
  airborne arc, `Standup` from frame 0 for the landing — so it works on any unit.
  This is the capability the engine's `Game/JumpSystem.cs` lacks (it needs
  authored `JumpTakeoff`/`JumpLoop`/`JumpLand` clips). Trigger it for a look via
  the `roguejump` dev command (see `Game1.Dev.cs`).
