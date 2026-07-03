using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Necroking.GameSystems;
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
    public readonly Simulation Sim = new();
    public readonly GroundSystem Ground = new();
    public readonly EnvironmentSystem Env = new();
    public readonly WallSystem Wall = new();
    public readonly RoadSystem Road = new();

    /// <summary>Live count of every game-object collection this session owns — units broken
    /// down by type/faction, env objects by def, plus the misc per-game collections that are
    /// prone to silent accumulation (wall defs were the canary). Returns a human-readable
    /// multi-line report. THIS is the single place to extend when a new per-game collection is
    /// added to the session — keep it in step with the fields above.</summary>
    public string Census()
    {
        var sb = new StringBuilder();

        // Units — grouped by def id ("object type") and by faction.
        var byType = new Dictionary<string, int>();
        var byFaction = new Dictionary<string, int>();
        for (int i = 0; i < Sim.Units.Count; i++)
        {
            var u = Sim.Units[i];
            string t = string.IsNullOrEmpty(u.UnitDefID) ? "(none)" : u.UnitDefID;
            byType[t] = byType.GetValueOrDefault(t) + 1;
            string f = u.Faction.ToString();
            byFaction[f] = byFaction.GetValueOrDefault(f) + 1;
        }
        sb.Append($"units: {Sim.Units.Count}");
        if (byFaction.Count > 0)
            sb.Append("  [" + string.Join(", ",
                byFaction.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}")) + "]");
        sb.Append('\n');
        foreach (var kv in byType.OrderByDescending(kv => kv.Value))
            sb.Append($"    {kv.Key}: {kv.Value}\n");

        // Env objects — grouped by def id.
        var envByDef = new Dictionary<string, int>();
        for (int i = 0; i < Env.ObjectCount; i++)
        {
            string id = Env.GetDef(Env.GetObject(i).DefIndex)?.Id ?? "(none)";
            envByDef[id] = envByDef.GetValueOrDefault(id) + 1;
        }
        sb.Append($"env objects: {Env.ObjectCount}  (defs: {Env.DefCount})\n");
        foreach (var kv in envByDef.OrderByDescending(kv => kv.Value))
            sb.Append($"    {kv.Key}: {kv.Value}\n");

        // Misc per-game collections (accumulation canaries — wall defs especially).
        sb.Append($"corpses: {Sim.Corpses.Count}\n");
        sb.Append($"soul orbs: {Sim.SoulOrbs.Count}\n");
        sb.Append($"pending raises: {Sim.PendingZombieRaises.Count}\n");
        sb.Append($"squads: {Sim.Squads.Squads.Count}\n");
        sb.Append($"projectiles: {Sim.Projectiles.Projectiles.Count}\n");
        sb.Append($"poison clouds: {Sim.PoisonClouds.Clouds.Count}\n");
        sb.Append($"lightning fx: {Sim.Lightning.Strikes.Count + Sim.Lightning.Zaps.Count + Sim.Lightning.Beams.Count + Sim.Lightning.Drains.Count}\n");
        sb.Append($"magic glyphs: {Sim.MagicGlyphs.Glyphs.Count}\n");
        sb.Append($"ground types: {Ground.TypeCount}\n");
        sb.Append($"wall defs: {Wall.Defs.Count}\n");
        sb.Append($"roads: {Road.RoadCount}  junctions: {Road.JunctionCount}\n");

        return sb.ToString();
    }

    public void Dispose()
    {
        // Free GPU/native resources the session owns. These clear-methods dispose the
        // textures they hold (ground/env), so calling them here — rather than relying on
        // GC, which never frees unmanaged GPU memory — is what actually reclaims VRAM.
        // Safe to call on a never-loaded session (empty collections → no-ops).
        // Sim holds only managed state; dropping the session reference lets the GC reclaim it.
        Ground.ClearTypes();
        Env.ClearDefs();
        Env.ClearObjects();
        Wall.ClearDefs();
    }
}
