using System;
using System.Collections.Generic;
using Necroking.Core;
using Necroking.Data;
using Necroking.Movement;

namespace Necroking.AI;

/// <summary>
/// A persistent group of coordinating units — a deer herd, a wolf pack, a soldier patrol.
/// The single "squad" concept the AI leans on so groups behave as one object instead of a
/// bag of individuals re-discovered by proximity scans every frame:
///   • deer remember their herd and stay together (cohesion),
///   • a herd flees as one when any member is spooked (shared alert),
///   • hunters flank the whole pack — its <see cref="Centroid"/> and <see cref="Spread"/> —
///     rather than a single animal embedded in a moving cluster.
///
/// A unit's membership is a stable <see cref="Unit.SquadId"/> (an id, not an index, so it
/// survives unit swap-and-pop). The derived fields below (centroid, spread, alert, leader,
/// live membership) are recomputed once per frame by <see cref="SquadSystem"/> before the AI
/// pass, so handlers read fresh group state without any O(n²) scanning of their own.
/// </summary>
public sealed class Squad
{
    public uint Id;
    public Faction Faction;
    /// <summary>The squad "kind" — one of the <see cref="ArchetypeRegistry"/> archetype ids
    /// (DeerHerd / WolfPack / RatPack / PatrolSoldier). Members always share it.</summary>
    public byte Archetype;

    /// <summary>Authoritative membership: unit ids. Dead / removed members are pruned each
    /// frame in <see cref="SquadSystem.Update"/>; resolve to indices via UnitArrays as needed.</summary>
    public readonly List<uint> Members = new();

    // ── Derived, recomputed each frame from the live members ──
    /// <summary>Mean position of the live members — "where the pack is".</summary>
    public Vec2 Centroid;
    /// <summary>Distance from the centroid to the farthest live member — the pack's extent.
    /// This is what hunters stand off from, so they clear the whole herd's vision, not one deer's.</summary>
    public float Spread;
    /// <summary>Live member count this frame.</summary>
    public int AliveCount;
    /// <summary>Member nearest the centroid (a natural "leader"/anchor), or 0 if none.</summary>
    public uint LeaderId;
    /// <summary>Highest alert state across the members — lets a handler treat the whole squad
    /// as alerted the moment any one of them is.</summary>
    public byte AlertState;
    /// <summary>Alert target carried by the most-alerted member (what the squad is reacting to).</summary>
    public uint AlertTarget = GameConstants.InvalidUnit;
}

/// <summary>
/// Owns the game's <see cref="Squad"/>s. Runs once per frame (before the AI pass) to
///   1. assign any squad-kind unit that still has no squad to a nearby same-kind squad,
///      forming a new one if none is close — this is the "units that spawn near each other
///      belong to the same group" clustering, done lazily so it covers every spawn path
///      (map load, dev spawn, …) without hooking each one; and
///   2. recompute every squad's live membership + centroid / spread / alert / leader,
///      culling squads that have no living members left.
///
/// Cost is O(total members) per frame plus the occasional assignment scan — squads are small,
/// so this replaces the per-frame proximity scans the deer/rat/wolf AIs used to each run.
/// </summary>
public sealed class SquadSystem
{
    /// <summary>Spawn-time clustering radius: an unassigned unit joins an existing same-kind
    /// squad whose centroid is within this distance, else seeds a new one. Generous enough to
    /// gather a scattered spawn batch into one herd, tight enough that two distinct herds spawned
    /// far apart stay separate.</summary>
    public const float ClusterRadius = 22f;

    /// <summary>Upper bound on a single squad so a dense spawn doesn't collapse the whole map
    /// into one mega-herd; overflow units seed additional squads.</summary>
    public const int MaxMembers = 24;

    private readonly Dictionary<uint, Squad> _squads = new();
    private uint _nextId = 1;

    // Scratch reused across frames (single-threaded sim) to avoid per-tick allocation.
    private readonly List<uint> _emptyScratch = new();

    public IReadOnlyDictionary<uint, Squad> Squads => _squads;

    public bool TryGet(uint id, out Squad squad) => _squads.TryGetValue(id, out squad!);

    public Squad? Get(uint id) => _squads.TryGetValue(id, out var s) ? s : null;

    /// <summary>Archetypes that form squads. Herds, packs and patrols coordinate; solo/horde/worker
    /// archetypes don't (the player's horde is steered by the necromancer, not squad cohesion).</summary>
    public static bool IsSquadKind(byte archetype) =>
        archetype == ArchetypeRegistry.DeerHerd
        || archetype == ArchetypeRegistry.WolfPack
        || archetype == ArchetypeRegistry.RatPack
        || archetype == ArchetypeRegistry.PatrolSoldier;

    public void Clear()
    {
        _squads.Clear();
        _nextId = 1;
    }

    public void Update(UnitArrays units)
    {
        AssignUnassigned(units);
        Recompute(units);
    }

    /// <summary>Give every squad-kind unit that still lacks a squad a home: join the nearest
    /// same-faction, same-archetype squad within <see cref="ClusterRadius"/> that has room,
    /// otherwise seed a new squad. Centroids are updated incrementally as members join so a
    /// whole spawn batch on one frame still clusters correctly (the first deer seeds a herd,
    /// the rest fall into it).</summary>
    private void AssignUnassigned(UnitArrays units)
    {
        for (int i = 0; i < units.Count; i++)
        {
            var u = units[i];
            if (!u.Alive) continue;
            if (!IsSquadKind(u.Archetype)) continue;

            // Already in a live squad? Nothing to do. If its squad vanished (all members died
            // and it was culled), fall through and let it rejoin/reseed.
            if (u.SquadId != 0 && _squads.ContainsKey(u.SquadId)) continue;

            Vec2 pos = u.Position;
            Squad? best = null;
            float bestSq = ClusterRadius * ClusterRadius;
            foreach (var sq in _squads.Values)
            {
                if (sq.Archetype != u.Archetype || sq.Faction != u.Faction) continue;
                if (sq.Members.Count >= MaxMembers) continue;
                float d2 = (sq.Centroid - pos).LengthSq();
                if (d2 <= bestSq) { bestSq = d2; best = sq; }
            }

            if (best == null)
            {
                best = new Squad { Id = _nextId++, Faction = u.Faction, Archetype = u.Archetype, Centroid = pos };
                _squads[best.Id] = best;
            }
            else
            {
                // Fold the new member into the running centroid so same-frame joins keep clustering.
                int n = best.Members.Count;
                best.Centroid = (best.Centroid * (float)n + pos) * (1f / (n + 1));
            }
            best.Members.Add(u.Id);
            u.SquadId = best.Id;
        }
    }

    /// <summary>Prune dead members and recompute each squad's centroid / spread / alert / leader.
    /// Squads left with no living members are culled.</summary>
    private void Recompute(UnitArrays units)
    {
        _emptyScratch.Clear();

        foreach (var sq in _squads.Values)
        {
            // Compact the member list in place, keeping only live units, while summing positions.
            var members = sq.Members;
            int write = 0;
            Vec2 sum = Vec2.Zero;
            byte maxAlert = 0;
            uint alertTarget = GameConstants.InvalidUnit;
            for (int r = 0; r < members.Count; r++)
            {
                uint id = members[r];
                if (!units.TryGetIndex(id, out int idx) || !units[idx].Alive) continue;
                members[write++] = id;
                var m = units[idx];
                sum += m.Position;
                if (m.AlertState > maxAlert)
                {
                    maxAlert = m.AlertState;
                    alertTarget = m.AlertTarget;
                }
            }
            if (write < members.Count) members.RemoveRange(write, members.Count - write);

            sq.AliveCount = write;
            sq.AlertState = maxAlert;
            sq.AlertTarget = alertTarget;

            if (write == 0)
            {
                _emptyScratch.Add(sq.Id);
                continue;
            }

            Vec2 centroid = sum * (1f / write);
            sq.Centroid = centroid;

            // Spread = distance to the farthest member; leader = the member nearest the centroid.
            float spreadSq = 0f;
            float bestSq = float.MaxValue;
            uint leader = 0;
            for (int r = 0; r < write; r++)
            {
                if (!units.TryGetIndex(members[r], out int idx)) continue;
                float d2 = (units[idx].Position - centroid).LengthSq();
                if (d2 > spreadSq) spreadSq = d2;
                if (d2 < bestSq) { bestSq = d2; leader = members[r]; }
            }
            sq.Spread = MathF.Sqrt(spreadSq);
            sq.LeaderId = leader;
        }

        for (int i = 0; i < _emptyScratch.Count; i++)
            _squads.Remove(_emptyScratch[i]);
    }
}
