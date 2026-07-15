using System.Collections.Generic;
using Necroking.Core;
using Necroking.Data;
using Necroking.Lib;
using Necroking.Movement;
using Necroking.World;

namespace Necroking.GameSystems;

// ---------------------------------------------------------------------------
// Filter predicates. Struct-generic (`where TF : struct, I...QueryFilter`) so
// query calls in sim-tick paths are zero-alloc and the JIT monomorphizes the
// Match call — never pass a lambda/delegate here. The prebuilt structs below
// encode the game's canonical gates; a call site with an odd extra condition
// (e.g. "pile that actually holds a corpse") writes its own small struct next
// to the caller instead of widening these.
// ---------------------------------------------------------------------------

public interface IUnitQueryFilter { bool Match(Unit u, int idx); }
public interface IEnvQueryFilter { bool Match(EnvironmentSystem env, int objIdx); }
public interface ICorpseQueryFilter { bool Match(Corpse c); }

/// <summary>Corpse states to skip in a query. <see cref="Free"/> is the standard
/// "loose body on the ground" gate shared by hover picks, rope attach, and hand
/// pickup: not mid-dissolve, not consumed by a summon, not bagged, not being
/// dragged.</summary>
[System.Flags]
public enum CorpseExclude : byte
{
    None        = 0,
    Dissolving  = 1 << 0,
    Consumed    = 1 << 1,   // ConsumedBySummon
    Bagged      = 1 << 2,
    Dragged     = 1 << 3,   // DraggedByUnitID set
    Reanimating = 1 << 4,   // ReanimInstanceId != 0 (mid rise effect)
    Free        = Dissolving | Consumed | Bagged | Dragged,
}

/// <summary>Runtime-state gates for env-object queries by def index.</summary>
[System.Flags]
public enum EnvGate : byte
{
    None    = 0,
    Alive   = 1 << 0,
    Built   = 1 << 1,   // BuildProgress >= 1
    Visible = 1 << 2,   // IsObjectVisible (not collected)
    AliveBuilt = Alive | Built,
}

/// <summary>Env objects of one def, gated on runtime state (default Alive+Built).</summary>
public readonly struct EnvByDefIndex : IEnvQueryFilter
{
    private readonly int _defIndex;
    private readonly EnvGate _gate;
    public EnvByDefIndex(int defIndex, EnvGate gate = EnvGate.AliveBuilt)
    { _defIndex = defIndex; _gate = gate; }

    public bool Match(EnvironmentSystem env, int i)
    {
        if (env.GetObject(i).DefIndex != _defIndex) return false;
        var rt = env.GetObjectRuntime(i);
        if ((_gate & EnvGate.Alive) != 0 && !rt.Alive) return false;
        if ((_gate & EnvGate.Built) != 0 && rt.BuildProgress < 1f) return false;
        if ((_gate & EnvGate.Visible) != 0 && !env.IsObjectVisible(i)) return false;
        return true;
    }
}

/// <summary>Per-target detection gate: sneaking targets are detectable at
/// ×0.5 base range, running targets at ×1.5. Captures the observer position
/// because acceptance depends on distance vs the TARGET's effective range —
/// pass a query radius of RunMul × baseRange so runners beyond base range
/// are found (see <see cref="WorldQuery.NearestThreatOf"/>).</summary>
public readonly struct DetectableFrom : IUnitQueryFilter
{
    public const float SneakMul = 0.5f;
    public const float RunMul   = 1.5f;

    private readonly Vec2 _observerPos;
    private readonly float _baseRange;
    public DetectableFrom(Vec2 observerPos, float baseRange)
    { _observerPos = observerPos; _baseRange = baseRange; }

    public bool Match(Unit u, int idx)
    {
        float eff = _baseRange;
        if (u.IsSneaking) eff *= SneakMul;
        else if (u.Velocity.LengthSq() > u.MaxSpeed * u.MaxSpeed * 0.8f)
            eff *= RunMul; // running = easier to detect
        return (u.Position - _observerPos).LengthSq() <= eff * eff;
    }
}

/// <summary>Built worker homes (Empty Graves): IsWorkerHome + Alive + fully built.
/// No visibility gate — buildings are never "collected".</summary>
public readonly struct EnvWorkerHomes : IEnvQueryFilter
{
    public bool Match(EnvironmentSystem env, int i)
    {
        if (!env.GetDef(env.GetObject(i).DefIndex).IsWorkerHome) return false;
        var rt = env.GetObjectRuntime(i);
        return rt.Alive && rt.BuildProgress >= 1f;
    }
}

/// <summary>Collectable foragables: visible (not picked up) + IsForagable.
/// Deliberately no Alive/Built gate — matches the pickup rules.</summary>
public readonly struct EnvForagables : IEnvQueryFilter
{
    public bool Match(EnvironmentSystem env, int i)
        => env.IsObjectVisible(i) && env.GetDef(env.GetObject(i).DefIndex).IsForagable;
}

/// <summary>Berry bushes currently carrying berries (harvestable).</summary>
public readonly struct EnvBerryBushes : IEnvQueryFilter
{
    public bool Match(EnvironmentSystem env, int i)
    {
        if (!env.GetDef(env.GetObject(i).DefIndex).IsBerryBush) return false;
        var rt = env.GetObjectRuntime(i);
        return rt.Alive && rt.BerryState == BerryState.Berries;
    }
}

/// <summary>Match-all env filter (no gates) — for picks that must see every
/// placed object regardless of runtime state (e.g. map-editor removal).</summary>
public readonly struct EnvAny : IEnvQueryFilter
{
    public bool Match(EnvironmentSystem env, int i) => true;
}

/// <summary>
/// Central world-query engine: nearest-of / under-cursor / all-in-radius over
/// units, env objects, and corpses. One canonical implementation of the
/// "best-distance scan with filters" that used to be re-written ad hoc per
/// call site — the caller supplies only the filter.
///
/// Contracts:
/// - Returned values are TRANSIENT indices into the backing collections
///   (-1 = none found), valid this frame only. Unit and env indices shift on
///   removal, corpse list indices shift on removal — persist Unit.Id /
///   CorpseID / ObjectID across frames instead.
/// - "Under cursor" is not a separate mechanism: it is Nearest*(mouseWorld,
///   pickRadius). Range bounds are exclusive (dist &lt; range), matching the
///   scans this replaces.
/// - Two unit paths, on purpose: the *quadtree-backed* methods are for
///   radius-bounded queries on sim-tick paths (fast at horde scale, but the
///   tree is rebuilt at tick start — it is STALE while the game is paused or
///   in the map editor, and misses units spawned mid-tick; results are
///   re-checked Alive here). The *linear* methods are the UI-safe path:
///   correct at any time, microseconds at a few hundred units.
/// - Env-object and corpse queries are linear scans behind this facade;
///   if profiling ever says otherwise, a spatial index drops in here with
///   zero call-site changes.
/// - GhostMode units (spirit-walk spirits, net ghosts, dev fly) are invisible
///   to every gameplay unit query here — they can't be aggroed, threat-scanned,
///   or spell-targeted. The one exception is <see cref="UnitUnderCursor"/>
///   (hover pick), which still sees them.
/// - Lives on <see cref="Simulation"/> (per-map lifetime). Reach it as
///   _sim.Query.… every call — NEVER cache the instance in a field, same
///   rule as _sim itself (the session is recreated on map load).
/// </summary>
public sealed class WorldQuery
{
    private readonly Simulation _sim;
    // Scratch for quadtree id results — reused, so quadtree-backed queries
    // are not re-entrant (fine: the sim is single-threaded by design).
    private readonly List<uint> _idScratch = new();

    public WorldQuery(Simulation sim) { _sim = sim; }

    // ------------------------------------------------------------------ units

    /// <summary>Nearest living enemy (any other faction) of a unit. Bounded
    /// range → quadtree; maxRange 0 = unbounded → linear scan. Sim-tick
    /// semantics of the old Simulation.FindClosestEnemy.</summary>
    public int NearestEnemyOf(int unitIdx, float maxRange = 0f)
    {
        var units = _sim.Units;
        if (maxRange <= 0f || _sim.Quadtree.IsEmpty)
            return NearestUnitLinear(units[unitIdx].Position, maxRange,
                FactionMaskExt.AllExcept(units[unitIdx].Faction), unitIdx);

        _idScratch.Clear();
        _sim.Quadtree.QueryRadiusByFaction(units[unitIdx].Position, maxRange,
            FactionMaskExt.AllExcept(units[unitIdx].Faction), _idScratch);
        int best = -1;
        float bestD = maxRange * maxRange;
        for (int k = 0; k < _idScratch.Count; k++)
        {
            int idx = UnitUtil.ResolveUnitIndex(units, _idScratch[k]); // alive re-check
            if (idx < 0 || idx == unitIdx || units[idx].GhostMode) continue;
            float d = (units[idx].Position - units[unitIdx].Position).LengthSq();
            if (d < bestD) { bestD = d; best = idx; }
        }
        return best;
    }

    /// <summary>Nearest enemy detectable by unit <paramref name="unitIdx"/> given
    /// its detection range, applying the sneak/run modifiers (see
    /// <see cref="DetectableFrom"/>). The quadtree query is widened to
    /// RunMul × range so running enemies beyond base range are found.
    /// Quadtree-backed — sim-tick paths only (see class doc).</summary>
    public int NearestThreatOf(int unitIdx, float detectionRange)
    {
        var units = _sim.Units;
        Vec2 pos = units[unitIdx].Position;
        return NearestUnit(pos, detectionRange * DetectableFrom.RunMul,
            FactionMaskExt.AllExcept(units[unitIdx].Faction),
            new DetectableFrom(pos, detectionRange), excludeIdx: unitIdx);
    }

    /// <summary>Nearest living unit to a point, faction-masked + custom filter.
    /// Quadtree-backed — sim-tick paths only (see class doc).</summary>
    public int NearestUnit<TF>(Vec2 pos, float range, FactionMask mask, in TF filter,
        int excludeIdx = -1) where TF : struct, IUnitQueryFilter
    {
        var units = _sim.Units;
        if (_sim.Quadtree.IsEmpty) return -1;
        _idScratch.Clear();
        _sim.Quadtree.QueryRadiusByFaction(pos, range, mask, _idScratch);
        int best = -1;
        float bestD = range * range;
        for (int k = 0; k < _idScratch.Count; k++)
        {
            int idx = UnitUtil.ResolveUnitIndex(units, _idScratch[k]);
            if (idx < 0 || idx == excludeIdx || units[idx].GhostMode
                || !filter.Match(units[idx], idx)) continue;
            float d = (units[idx].Position - pos).LengthSq();
            if (d < bestD) { bestD = d; best = idx; }
        }
        return best;
    }

    /// <summary>All living units within radius, faction-masked, appended to
    /// <paramref name="results"/> as indices. Quadtree-backed — sim-tick paths
    /// only. Returns the number found.</summary>
    public int UnitsInRadius(Vec2 pos, float radius, FactionMask mask, List<int> results)
    {
        var units = _sim.Units;
        if (_sim.Quadtree.IsEmpty) return 0;
        _idScratch.Clear();
        _sim.Quadtree.QueryRadiusByFaction(pos, radius, mask, _idScratch);
        int found = 0;
        for (int k = 0; k < _idScratch.Count; k++)
        {
            int idx = UnitUtil.ResolveUnitIndex(units, _idScratch[k]);
            if (idx < 0) continue;
            results.Add(idx);
            found++;
        }
        return found;
    }

    /// <summary>All living units within radius, faction-masked, appended to
    /// <paramref name="results"/> as indices. Linear scan — safe from UI/paused
    /// code (e.g. the spell-aim AoE preview), unlike <see cref="UnitsInRadius"/>.</summary>
    public int UnitsInRadiusLinear(Vec2 pos, float radius, FactionMask mask, List<int> results)
    {
        var units = _sim.Units;
        float r2 = radius * radius;
        int found = 0;
        for (int i = 0; i < units.Count; i++)
        {
            if (!units[i].Alive) continue;
            if ((units[i].Faction.Bit() & mask) == 0) continue;
            if ((units[i].Position - pos).LengthSq() > r2) continue;
            results.Add(i);
            found++;
        }
        return found;
    }

    /// <summary>Nearest living unit whose faction differs from
    /// <paramref name="friendly"/>. Linear scan — safe from UI/paused code.</summary>
    public int NearestEnemyToPoint(Vec2 pos, float range, Faction friendly)
        => NearestUnitLinear(pos, range, FactionMaskExt.AllExcept(friendly));

    /// <summary>Nearest living unit of exactly <paramref name="faction"/>,
    /// optionally excluding one index (the caster). Linear scan — UI-safe.</summary>
    public int NearestAllyToPoint(Vec2 pos, float range, Faction faction, int excludeIdx = -1)
        => NearestUnitOfFaction(pos, range, faction, excludeIdx);

    /// <summary>Nearest living unit of exactly <paramref name="faction"/> —
    /// neutral-name core shared by ally scans and one-faction threat scans
    /// (e.g. the village alarm's "nearest Undead"). Linear scan — UI/AI-safe.</summary>
    public int NearestUnitOfFaction(Vec2 pos, float range, Faction faction, int excludeIdx = -1)
        => NearestUnitLinear(pos, range, faction.Bit(), excludeIdx);

    /// <summary>Nearest living unit of any faction within the pick radius —
    /// the hover/click pick. Linear scan — UI-safe, works paused. The only unit
    /// query that still sees GhostMode units (you can hover them).</summary>
    public int UnitUnderCursor(Vec2 mouseWorld, float pickRadius)
        => NearestUnitLinear(mouseWorld, pickRadius, FactionMask.All, includeGhosts: true);

    private int NearestUnitLinear(Vec2 pos, float range, FactionMask mask, int excludeIdx = -1,
        bool includeGhosts = false)
    {
        var units = _sim.Units;
        int best = -1;
        float bestD = range > 0f ? range * range : float.MaxValue;
        for (int i = 0; i < units.Count; i++)
        {
            if (i == excludeIdx || !units[i].Alive) continue;
            if (!includeGhosts && units[i].GhostMode) continue;
            if ((units[i].Faction.Bit() & mask) == 0) continue;
            float d = (units[i].Position - pos).LengthSq();
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    // ------------------------------------------------------------ env objects

    /// <summary>Nearest env object passing <paramref name="filter"/>. Object
    /// position is its placement point (X,Y). range 0 = unbounded.</summary>
    public int NearestEnvObject<TF>(Vec2 pos, float range, in TF filter)
        where TF : struct, IEnvQueryFilter
    {
        var env = _sim.EnvironmentSystem;
        if (env == null) return -1;
        int best = -1;
        float bestD = range > 0f ? range * range : float.MaxValue;
        for (int i = 0; i < env.ObjectCount; i++)
        {
            if (!filter.Match(env, i)) continue;
            var o = env.GetObject(i);
            float d = (new Vec2(o.X, o.Y) - pos).LengthSq();
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    /// <summary>Nearest env object of one def (see <see cref="EnvByDefIndex"/>).</summary>
    public int NearestEnvObject(Vec2 pos, float range, int defIndex, EnvGate gate = EnvGate.AliveBuilt)
        => NearestEnvObject(pos, range, new EnvByDefIndex(defIndex, gate));

    /// <summary>All env objects passing the filter within radius, appended to
    /// <paramref name="results"/> as indices. Returns the number found.</summary>
    public int EnvObjectsInRadius<TF>(Vec2 pos, float radius, in TF filter, List<int> results)
        where TF : struct, IEnvQueryFilter
    {
        var env = _sim.EnvironmentSystem;
        if (env == null) return 0;
        int found = 0;
        float r2 = radius * radius;
        for (int i = 0; i < env.ObjectCount; i++)
        {
            if (!filter.Match(env, i)) continue;
            var o = env.GetObject(i);
            if ((new Vec2(o.X, o.Y) - pos).LengthSq() >= r2) continue;
            results.Add(i);
            found++;
        }
        return found;
    }

    // ---------------------------------------------------------------- corpses

    /// <summary>Nearest corpse not in any <paramref name="exclude"/> state.
    /// Returns a corpse LIST index (map to CorpseID to persist).</summary>
    public int NearestCorpse(Vec2 pos, float range, CorpseExclude exclude)
    {
        var corpses = _sim.Corpses;
        int best = -1;
        float bestD = range > 0f ? range * range : float.MaxValue;
        for (int i = 0; i < corpses.Count; i++)
        {
            if (IsExcluded(corpses[i], exclude)) continue;
            float d = (corpses[i].Position - pos).LengthSq();
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    /// <summary>Nearest corpse passing a custom filter (the escape hatch for
    /// gates CorpseExclude can't express, e.g. source-def faction).</summary>
    public int NearestCorpse<TF>(Vec2 pos, float range, in TF filter)
        where TF : struct, ICorpseQueryFilter
    {
        var corpses = _sim.Corpses;
        int best = -1;
        float bestD = range > 0f ? range * range : float.MaxValue;
        for (int i = 0; i < corpses.Count; i++)
        {
            if (!filter.Match(corpses[i])) continue;
            float d = (corpses[i].Position - pos).LengthSq();
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    /// <summary>All corpses not in any exclude state within radius, appended to
    /// <paramref name="results"/> as list indices. Returns the number found.</summary>
    public int CorpsesInRadius(Vec2 pos, float radius, CorpseExclude exclude, List<int> results)
    {
        var corpses = _sim.Corpses;
        int found = 0;
        float r2 = radius * radius;
        for (int i = 0; i < corpses.Count; i++)
        {
            if (IsExcluded(corpses[i], exclude)) continue;
            if ((corpses[i].Position - pos).LengthSq() >= r2) continue;
            results.Add(i);
            found++;
        }
        return found;
    }

    // ---------------------------------------------------------------- blocking

    // Scratch for env-circle probes — reused, so blocking queries are not
    // re-entrant (same contract as _idScratch; fine, the sim is single-threaded).
    private readonly List<EnvSpatialIndex.Entry> _envScratch = new();

    /// <summary>True if a unit of the given radius can NOT stand at pos:
    /// overlaps an impassable tile (walls/deep water, probed at the movement
    /// wall-collision radius = unitRadius*0.7, same as UpdateMovement) or a
    /// static env collision circle. THE standability check for teleport/dodge
    /// landings, spawn placement, and AI destination picks. Safe any time —
    /// the grid and env index are event-rebuilt, not per-tick, so they are
    /// never stale while paused.</summary>
    public bool IsSpotBlocked(Vec2 pos, float unitRadius)
    {
        if (_sim.Grid.AabbOverlapsImpassable(pos.X, pos.Y, unitRadius * 0.7f)) return true;
        _envScratch.Clear();
        _sim.EnvIndex.QueryRadius(pos, unitRadius, _envScratch);
        foreach (var e in _envScratch)
        {
            float dx = pos.X - e.CX, dy = pos.Y - e.CY;
            float combined = unitRadius + e.Radius;
            if (dx * dx + dy * dy < combined * combined) return true;
        }
        return false;
    }

    /// <summary>Walls/terrain only — any impassable tile in the circle's tile
    /// footprint. No env circles: use <see cref="IsSpotBlocked"/> for "can a
    /// unit stand here".</summary>
    public bool IsWallBlocked(Vec2 pos, float radius)
        => _sim.Grid.AabbOverlapsImpassable(pos.X, pos.Y, radius);

    private static bool IsExcluded(Corpse c, CorpseExclude ex)
    {
        if ((ex & CorpseExclude.Dissolving) != 0 && c.Dissolving) return true;
        if ((ex & CorpseExclude.Consumed) != 0 && c.ConsumedBySummon) return true;
        if ((ex & CorpseExclude.Bagged) != 0 && c.Bagged) return true;
        if ((ex & CorpseExclude.Dragged) != 0 && c.DraggedByUnitID != GameConstants.InvalidUnit) return true;
        if ((ex & CorpseExclude.Reanimating) != 0 && c.ReanimInstanceId != 0) return true;
        return false;
    }
}
