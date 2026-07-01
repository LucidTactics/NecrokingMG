using System;
using System.Collections.Generic;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Movement;

namespace Necroking.AI;

/// <summary>
/// Pack-hunt AI for the player's zombie wolves (Undead <see cref="ArchetypeRegistry.HordeMinion"/>
/// units tagged <c>wolf</c>). A second, distinct take on wolf behavior from the wild-wolf
/// <see cref="WolfPackHandler"/> archetype — this one drives the necromancer's raised wolves.
///
/// The hunt, in the player's words:
///   "If they see deer, they spread out — further than the deer can see them — and go around
///    to the far side of the deer from you. If you have more wolves they circle the deer.
///    Then, once they're on the other side, they chase the deer back to you."
///
/// So the pack locks onto one nearby deer and runs two phases:
///   • Flanking — each wolf slides around the OUTSIDE of the deer's vision ring (standoff =
///     the deer's DetectionRange + a margin) to an assigned slot on the far side of the deer
///     relative to the necromancer. It circles the ring tangentially rather than cutting
///     straight across, so it never crosses into detection range and spooks the prey early.
///     Multiple wolves fan out across an arc → an encircling net behind the deer.
///   • Driving — once enough of the pack is in position (or a timeout fires), every wolf
///     sprints straight at the deer. The deer flees away from the nearest wolf, and since the
///     wolves are all behind it, "away" means toward the necromancer and the waiting horde.
///
/// Design mirrors <see cref="BoarForageAI"/>: a post-archetype sweep with a Simulation handle
/// (needed for <see cref="Simulation.NecromancerIndex"/> — the archetype AIContext can't see the
/// necromancer's position, which is exactly the "far side from you" reference we need). It runs
/// after the archetype pass and before UpdateMovement so overriding PreferredVel steers the wolf
/// this frame; wolves with no prey are left on their HordeMinion follow velocity, and combat /
/// horde commands always win (the eligibility guards yield the moment a wolf has a real target).
/// </summary>
public static class WolfPackHuntAI
{
    private const float HuntRange = 28f;      // pack notices a deer within this of the pack centroid
    private const float GiveUpRange = 42f;    // drop the locked prey once it's beyond this from the pack
    private const float LeashFromNecro = 40f; // a wolf this far from the necromancer stops hunting and follows home
    private const float DetectMargin = 7f;    // flank standoff = deer DetectionRange + this. Generous so the
                                              // circling pack (and its path chords) stays clear of the deer's
                                              // vision until the drive — a spooked-early deer flees the wrong way.
    private const float CircleStepAngle = 0.7f;     // rad of arc a wolf aims ahead each frame while circling
    private const float ArcSpacing = 0.55f;   // rad between adjacent wolves' slots on the far arc (~31°)
    private const float ReadyFraction = 0.7f; // fraction of the pack that must reach the far side to commit the drive
    private const float FarSideDepthFrac = 0.35f; // a wolf counts as "on the far side" once it's this fraction of
                                                  // the standoff distance beyond the deer, away from the necromancer
    private const float FlankTimeout = 25f;   // safety: force the drive after this long flanking, for a pack that
                                              // can't get around (blocked path, roaming prey). Generous on purpose —
                                              // enough wolves reaching the far side should be what commits the drive;
                                              // circling a half-arc at a jog already takes ~10-12s.

    // Scratch reused across frames to avoid per-tick allocation (single-threaded sim).
    private static readonly List<int> _hunters = new();

    public static void Update(Simulation sim, float dt)
    {
        var gameData = sim.GameData;
        if (gameData == null) return;
        if (sim.NecromancerIndex < 0) return;   // no "you" to drive the prey toward — sit this out

        var units = sim.UnitsMut;
        Vec2 necroPos = units[sim.NecromancerIndex].Position;

        // 1. Gather eligible hunter wolves and their centroid; clear hunt state on the rest.
        _hunters.Clear();
        Vec2 centroid = Vec2.Zero;
        for (int u = 0; u < units.Count; u++)
        {
            if (!IsEligible(units, u, gameData, necroPos))
            {
                if (units[u].WolfHuntTargetId != 0) ClearHunt(units, u);
                continue;
            }
            _hunters.Add(u);
            centroid += units[u].Position;
        }
        if (_hunters.Count == 0) return;
        centroid *= 1f / _hunters.Count;

        // 2. Pick ONE shared prey for the whole pack: keep the currently-locked deer if it's
        //    still alive and near, otherwise the nearest deer to the pack centroid.
        int targetIdx = ChooseTarget(units, centroid);
        if (targetIdx < 0)
        {
            for (int s = 0; s < _hunters.Count; s++) ClearHunt(units, _hunters[s]);
            return;
        }
        uint targetId = units[targetIdx].Id;
        Vec2 deerPos = units[targetIdx].Position;
        float deerDet = units[targetIdx].DetectionRange;
        if (deerDet <= 0f) deerDet = 10f;

        // Stable slot assignment: order hunters by unit id so a given wolf keeps its slot
        // frame to frame (no swapping / oscillation as the list order shifts).
        _hunters.Sort((a, b) => units[a].Id.CompareTo(units[b].Id));
        int n = _hunters.Count;

        // Far side of the deer, opposite the necromancer: the base direction the pack fans around.
        Vec2 nd = deerPos - necroPos;
        float baseAngle = (nd.LengthSq() > 1e-4f) ? MathF.Atan2(nd.Y, nd.X) : 0f;

        // Unit vector from the necromancer toward the deer — "the far side" is anything
        // beyond the deer along this direction.
        Vec2 farDir = nd;
        float ndLen = farDir.Length();
        farDir = ndLen > 1e-3f ? farDir * (1f / ndLen) : new Vec2(0f, 1f);

        // 3. Decide the pack phase collectively. The drive commits only once enough of the pack
        //    has crossed to the FAR side of the deer (beyond it, away from the necromancer) — a
        //    robust geometric test, not fragile slot-matching. Positioned that way, driving inward
        //    makes the deer flee away from the wolves, i.e. straight toward the necromancer. Once
        //    committed it latches (anyDriving) so a deer that bolts can't reset the pack to flanking,
        //    and a flank timeout is the safety valve for a pack that can never get around.
        bool anyDriving = false;
        int positioned = 0;
        float maxTimer = 0f;
        for (int s = 0; s < n; s++)
        {
            int u = _hunters[s];
            if (units[u].WolfHuntPhase == 1) anyDriving = true;
            if (units[u].WolfHuntTimer > maxTimer) maxTimer = units[u].WolfHuntTimer;

            float standoff = deerDet + DetectMargin + units[u].Radius;
            var rel = units[u].Position - deerPos;
            if (rel.Dot(farDir) >= standoff * FarSideDepthFrac) positioned++;
        }
        int needReady = Math.Max(1, (int)MathF.Ceiling(n * ReadyFraction));
        bool drive = anyDriving || positioned >= needReady || maxTimer >= FlankTimeout;

        // 4. Apply movement.
        for (int s = 0; s < n; s++)
        {
            int u = _hunters[s];
            units[u].WolfHuntTargetId = targetId;

            if (drive)
            {
                units[u].WolfHuntPhase = 1;
                units[u].WolfHuntTimer = 0f;
                // Full-commit chase straight at the prey — pushes it toward the necromancer.
                sim.AIWolfHuntMove(u, deerPos, sprint: true, dt);
            }
            else
            {
                units[u].WolfHuntPhase = 0;
                units[u].WolfHuntTimer += dt;
                float slotAngle = SlotAngle(baseAngle, s, n);
                float standoff = deerDet + DetectMargin + units[u].Radius;
                Vec2 ringTarget = CircleTarget(units[u].Position, deerPos, slotAngle, standoff);
                // Cautious jog while flanking — a sneak-up, not a charge.
                sim.AIWolfHuntMove(u, ringTarget, sprint: false, dt);
            }
        }
    }

    /// <summary>Called by <see cref="HordeMinionHandler.UpdateFollowing"/> before its proactive
    /// enemy self-aggro. A pack-hunting wolf in the FLANK phase must NOT charge a nearby deer — it
    /// has to stalk around to the far side first — so this returns true to suppress that acquisition.
    /// Once the pack commits to the DRIVE (<see cref="Unit.WolfHuntPhase"/>==1) it returns false so
    /// the wolf engages normally as it closes the gap. Deer that fight back (an already-assigned
    /// Target) are still fought; only the proactive "see enemy, charge" is gated.
    ///
    /// Deliberately self-contained (reads only the unit + a deer scan, not sweep-set flags beyond the
    /// drive latch): the horde handler runs BEFORE the <see cref="Update"/> sweep each frame, so if
    /// suppression depended on the sweep having already tagged the wolf, frame 1 would let it charge
    /// and the wolf would never enter the hunt.</summary>
    public static bool WantsToFlank(ref AIContext ctx)
    {
        var u = ctx.Units[ctx.UnitIndex];
        if (u.Archetype != ArchetypeRegistry.HordeMinion) return false;
        if (u.Faction != Faction.Undead) return false;
        if (u.WolfHuntPhase == 1) return false;          // driving — let it engage
        var def = ctx.GameData?.Units.Get(u.UnitDefID);
        if (def == null || !def.Tags.Contains("wolf")) return false;
        if (u.WolfHuntTargetId != 0) return true;        // already assigned to a hunt → keep stalking

        // Fresh prey nearby? A deer within hunt range means "stalk mode," not "charge."
        float rSq = HuntRange * HuntRange;
        var units = ctx.Units;
        Vec2 pos = u.Position;
        for (int j = 0; j < units.Count; j++)
        {
            if (!units[j].Alive) continue;
            if (units[j].Archetype != ArchetypeRegistry.DeerHerd) continue;
            if ((units[j].Position - pos).LengthSq() <= rSq) return true;
        }
        return false;
    }

    /// <summary>A player-controlled zombie wolf that's free to hunt: an Undead HordeMinion
    /// tagged <c>wolf</c>, currently just Following (Routine 0), not in combat, not already
    /// heading to a target, not locked by an action, and within leash of the necromancer.
    /// Any of those failing means combat / horde commands own the wolf and we defer.</summary>
    private static bool IsEligible(UnitArrays units, int u, Data.GameData gameData, Vec2 necroPos)
    {
        if (!units[u].Alive) return false;
        if (units[u].Faction != Faction.Undead) return false;
        if (units[u].Archetype != ArchetypeRegistry.HordeMinion) return false;
        if (units[u].Routine != 0) return false;        // 0 == HordeMinion Following
        if (units[u].InCombat) return false;
        if (!units[u].Target.IsNone) return false;
        if (units[u].IsLockedByAction()) return false;
        if ((units[u].Position - necroPos).LengthSq() > LeashFromNecro * LeashFromNecro) return false;

        var def = gameData.Units.Get(units[u].UnitDefID);
        return def != null && def.Tags.Contains("wolf");
    }

    private static void ClearHunt(UnitArrays units, int u)
    {
        units[u].WolfHuntTargetId = 0;
        units[u].WolfHuntPhase = 0;
        units[u].WolfHuntTimer = 0f;
    }

    /// <summary>Keep the pack's locked prey if it's still a live deer within GiveUpRange of the
    /// centroid; otherwise acquire the nearest deer within HuntRange. Returns the unit index of the
    /// prey or -1. The lock is read from whichever hunter already carries a WolfHuntTargetId.</summary>
    private static int ChooseTarget(UnitArrays units, Vec2 centroid)
    {
        // Existing lock (shared across the pack — first hunter with one wins).
        uint lockedId = 0;
        for (int s = 0; s < _hunters.Count; s++)
        {
            uint id = units[_hunters[s]].WolfHuntTargetId;
            if (id != 0) { lockedId = id; break; }
        }
        if (lockedId != 0)
        {
            int idx = UnitUtil.ResolveUnitIndex(units, lockedId);
            if (idx >= 0 && units[idx].Alive
                && units[idx].Archetype == ArchetypeRegistry.DeerHerd
                && (units[idx].Position - centroid).LengthSq() <= GiveUpRange * GiveUpRange)
                return idx;
        }

        // Acquire the nearest deer to the pack centroid within HuntRange.
        float bestSq = HuntRange * HuntRange;
        int best = -1;
        for (int j = 0; j < units.Count; j++)
        {
            if (!units[j].Alive) continue;
            if (units[j].Archetype != ArchetypeRegistry.DeerHerd) continue;
            float d2 = (units[j].Position - centroid).LengthSq();
            if (d2 < bestSq) { bestSq = d2; best = j; }
        }
        return best;
    }

    /// <summary>Angle (world radians) of slot <paramref name="s"/> of <paramref name="n"/> on the
    /// far arc: symmetric fan centred on <paramref name="baseAngle"/> (necromancer→deer direction),
    /// <see cref="ArcSpacing"/> apart. One wolf sits dead-centre behind the deer; more wolves widen
    /// the encirclement outward from there.</summary>
    private static float SlotAngle(float baseAngle, int s, int n)
        => baseAngle + (s - (n - 1) * 0.5f) * ArcSpacing;

    /// <summary>Next waypoint that walks the wolf around the OUTSIDE of the deer's vision ring
    /// toward its slot: step the wolf's current bearing (about the deer) toward the slot bearing by
    /// at most <see cref="CircleStepAngle"/>, projected onto the standoff radius. Aiming a short arc
    /// ahead — instead of straight at the slot — keeps the wolf circling tangentially rather than
    /// diving across the ring into detection range.</summary>
    private static Vec2 CircleTarget(Vec2 wolfPos, Vec2 deerPos, float slotAngle, float standoff)
    {
        Vec2 rel = wolfPos - deerPos;
        float curAngle = (rel.LengthSq() > 1e-4f) ? MathF.Atan2(rel.Y, rel.X) : slotAngle;
        float delta = WrapPi(slotAngle - curAngle);
        float step = Math.Clamp(delta, -CircleStepAngle, CircleStepAngle);
        float aim = curAngle + step;
        return deerPos + new Vec2(MathF.Cos(aim) * standoff, MathF.Sin(aim) * standoff);
    }

    /// <summary>Wrap an angle to (-π, π].</summary>
    private static float WrapPi(float a)
    {
        const float TwoPi = MathF.PI * 2f;
        a %= TwoPi;
        if (a > MathF.PI) a -= TwoPi;
        else if (a < -MathF.PI) a += TwoPi;
        return a;
    }
}
