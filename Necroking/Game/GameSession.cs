using System;
using Necroking.World;

namespace Necroking;

/// <summary>
/// Owns the per-game world state — the systems that are rebuilt from scratch on every
/// map load. Game1 holds one <see cref="GameSession"/> and exposes its members through
/// forwarding properties, so existing <c>_envSystem.Foo()</c> call sites are unchanged.
///
/// The whole point: <see cref="StartGame"/> does <c>_session.Dispose(); _session = new()</c>,
/// which (a) frees the GPU/native resources the old session owned via <see cref="Dispose"/>,
/// and (b) drops every reference to the previous map's managed state so the GC reclaims it.
/// Nothing carries over from one map to the next, and a newly-added per-game resource can
/// only leak if it's created outside this object — which is the one rule to remember.
///
/// This is being migrated incrementally: systems move in one at a time (Game1 field →
/// GameSession field + forwarding property), keeping the build green at every step. Systems
/// still living on Game1 are app-lifetime (renderers, editors, GameData) or not yet moved.
/// </summary>
public sealed class GameSession : IDisposable
{
    // --- Per-game world systems (parameterless; configured via their Init/Load in StartGame) ---
    public readonly GroundSystem Ground = new();
    public readonly EnvironmentSystem Env = new();
    public readonly WallSystem Wall = new();
    public readonly RoadSystem Road = new();

    public void Dispose()
    {
        // Free GPU/native resources the session owns. These clear-methods dispose the
        // textures they hold (ground/env), so calling them here — rather than relying on
        // GC, which never frees unmanaged GPU memory — is what actually reclaims VRAM.
        // Safe to call on a never-loaded session (empty collections → no-ops).
        Ground.ClearTypes();
        Env.ClearDefs();
        Env.ClearObjects();
        Wall.ClearDefs();
    }
}
