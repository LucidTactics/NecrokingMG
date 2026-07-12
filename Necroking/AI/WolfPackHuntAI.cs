using System;
using System.Collections.Generic;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Lib;
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
    private const float HuntRange = 28f;      // acquire the deer nearest the cast point within this
    private const float LeashFromNecro = 80f; // a wolf this far from the necromancer drops the hunt. Must roughly fit
                                              // the flank geometry or wolves oscillate at the boundary (drop hunt →
                                              // follow AI yanks them home → re-acquire → head out again): spell cast
                                              // range (~30) + HuntRange (28) + far-side standoff ≈ 90 worst-case, but
                                              // that stack only maxes out on an extreme cast; 80 covers the common
                                              // case without letting the pack range absurdly far. Note a wolf
                                              // FIGHTING its quarry is exempt (it keeps its hunt tag and finishes
                                              // the kill — see the finishingKill carve-out in Update).
    private const float DetectMargin = 6f;    // flank standoff = deer DetectionRange + this. Keeps the circling
                                              // pack clear of the deer's vision, but small enough that the flank
                                              // ring still fits in tight spaces; the herded cheat covers an early
                                              // spook, so this no longer has to be large.
    private const float CircleStepAngle = 0.4f;     // rad of arc a wolf aims ahead each frame while circling
    private const float ArcSpacing = 0.55f;   // rad between adjacent wolves' slots on the far arc (~31°)
    private const float ReadyFraction = 0.35f; // fraction of the pack that must reach the far side to commit the drive
    private const float FarSideDepthFrac = 0.75f; // a wolf counts as "behind" (positioned to drive, and cleared to
                                                  // charge) once it's this fraction of the standoff distance past the
                                                  // deer, away from the necromancer. Kept modest so the fanned pack
                                                  // actually reaches the threshold and commits; the herded cheat (not
                                                  // this alone) is what guarantees the prey bolts toward you.
    private const float FlankTimeout = 25f;   // safety: force the drive after this long flanking, for a pack that
                                              // can't get around (blocked path, roaming prey). Generous on purpose —
                                              // enough wolves reaching the far side should be what commits the drive;
                                              // circling a half-arc at a jog already takes ~10-12s.
    private const float HerdDuration = 2f;       // seconds the "herded" cheat pins the prey's flee direction

    // Scratch reused across frames to avoid per-tick allocation (single-threaded sim).
    private static readonly List<int> _hunters = new();

    // True while a hunt command has been driving wolves — lets the expiry path sweep-clear
    // every wolf's hunt tag exactly once instead of scanning all units every idle frame.
    private static bool _huntWasActive;

    public static void Update(Simulation sim, float dt)
    {
        var gameData = sim.GameData;
        if (gameData == null) return;
        if (sim.NecromancerIndex < 0) return;   // no "you" to drive the prey toward — sit this out

        var units = sim.UnitsMut;
        // The spell timer is only the ACQUISITION window. A hunt already in progress (any wolf
        // holding a lock) runs to completion even after the timer expires — expiry mid-flank or
        // mid-kill must not make the whole pack "lose interest" and jog home in unison.
        bool armed = sim.WolfHuntTimerArmed;
        if (!armed && !sim.WolfHuntInProgress)  // spell-activated: only hunt on the player's command
        {
            // Hunt fully over: drop every wolf's hunt state once, so stale tags don't
            // keep suppressing the horde's aggro scan and leash logic indefinitely.
            if (_huntWasActive)
            {
                for (int u = 0; u < units.Count; u++)
                    if (units[u].WolfHuntTargetId != 0) ClearHunt(units, u);
                _huntWasActive = false;
            }
            return;
        }
        _huntWasActive = true;

        Vec2 necroPos = units[sim.NecromancerIndex].Position;
        Vec2 cmdPos = sim.WolfHuntCommandPos;   // where the Wolf Hunt spell was cast — the targeted herd

        // 1. Gather eligible hunter wolves; clear hunt state on the rest. Track whether any
        //    INELIGIBLE wolf keeps its tag (= a kill still being finished) so the in-progress
        //    flag stays truthful when every hunter is busy fighting.
        _hunters.Clear();
        bool anyFightingTag = false;
        for (int u = 0; u < units.Count; u++)
        {
            if (!IsEligible(units, u, gameData, necroPos))
            {
                // A drive-phase wolf that's actually FIGHTING its quarry keeps its hunt tag:
                // the tag is what exempts it from the horde's (much shorter) leash, so the
                // pack finishes a kill far from the necromancer instead of mass-abandoning
                // a deer mid-bite the moment it crosses the horde's leash radius. The tag
                // drops naturally when combat ends (target dead → next sweep clears it) —
                // and hard-drops at 1.5× the hunt leash, so a deer the wolf can never quite
                // catch can't kite the pack across the whole map on an endless chase.
                bool finishingKill = units[u].WolfHuntPhase == 1
                    && (units[u].InCombat || !units[u].Target.IsNone)
                    && (units[u].Position - necroPos).LengthSq()
                        <= (LeashFromNecro * 1.5f) * (LeashFromNecro * 1.5f);
                if (!finishingKill && units[u].WolfHuntTargetId != 0) ClearHunt(units, u);
                if (units[u].WolfHuntTargetId != 0) anyFightingTag = true;
                continue;
            }
            _hunters.Add(u);
        }
        if (_hunters.Count == 0)
        {
            sim.WolfHuntInProgress = anyFightingTag;
            return;
        }

        // 2. Pick ONE shared prey HERD for the whole pack: keep the currently-locked squad while it
        //    lives, otherwise acquire the herd of the deer nearest the cast point (new acquisition
        //    only while the spell timer is armed). Hunting the whole herd (its centroid + extent) —
        //    not one animal embedded in a moving cluster — is the core of getting the flank right:
        //    "the far side, outside its vision" only means anything when it's the far side of the
        //    whole pack.
        var squad = ChooseTargetSquad(sim, units, cmdPos, allowAcquire: armed);
        if (squad == null || squad.AliveCount == 0)
        {
            for (int s = 0; s < _hunters.Count; s++) ClearHunt(units, _hunters[s]);
            sim.WolfHuntInProgress = anyFightingTag;
            return;
        }
        sim.WolfHuntInProgress = true;
        uint targetSquadId = squad.Id;
        Vec2 herdPos = squad.Centroid;            // flank/drive reference = where the pack is
        float herdSpread = squad.Spread;          // the pack's extent
        float herdDet = HerdDetectionRange(units, squad); // widest vision in the herd

        // Stable slot assignment: order hunters by unit id so a given wolf keeps its slot
        // frame to frame (no swapping / oscillation as the list order shifts).
        _hunters.Sort((a, b) => units[a].Id.CompareTo(units[b].Id));
        int n = _hunters.Count;

        // Far side of the HERD, opposite the necromancer: the base direction the pack fans around.
        Vec2 nd = herdPos - necroPos;
        float baseAngle = (nd.LengthSq() > 1e-4f) ? MathF.Atan2(nd.Y, nd.X) : 0f;

        // Unit vector from the necromancer toward the herd — "the far side" is anything
        // beyond the herd along this direction.
        Vec2 farDir = nd;
        float ndLen = farDir.Length();
        farDir = ndLen > 1e-3f ? farDir * (1f / ndLen) : new Vec2(0f, 1f);

        // 3. Decide the pack phase collectively. The drive commits only once enough of the pack
        //    has crossed to the FAR side of the herd (beyond it, away from the necromancer) — a
        //    robust geometric test, not fragile slot-matching. Positioned that way, driving inward
        //    makes the herd flee away from the wolves, i.e. straight toward the necromancer. Once
        //    committed it latches (anyDriving) so a herd that bolts can't reset the pack to flanking,
        //    and a flank timeout is the safety valve for a pack that can never get around.
        bool anyDriving = false;
        int positioned = 0;
        float maxTimer = 0f;
        for (int s = 0; s < n; s++)
        {
            int u = _hunters[s];
            if (units[u].WolfHuntPhase == 1) anyDriving = true;
            if (units[u].WolfHuntTimer > maxTimer) maxTimer = units[u].WolfHuntTimer;

            float standoff = herdSpread + herdDet + DetectMargin + units[u].Radius;
            var rel = units[u].Position - herdPos;
            if (rel.Dot(farDir) >= standoff * FarSideDepthFrac) positioned++;
        }
        int needReady = Math.Max(1, (int)MathF.Ceiling(n * ReadyFraction));
        bool drive = anyDriving || positioned >= needReady || maxTimer >= FlankTimeout;

        // The instant the pack commits, stamp the "herded" cheat on the WHOLE herd (each deer once):
        // every member bolts toward the necromancer for HerdDuration. Done at commit — while the
        // charging wolves are still ~a standoff away — so the herd gets a head start and actually
        // runs there, rather than being pounced and pinned before it can move. DeerHerdHandler forces
        // the flee direction. Stamping the whole squad is what makes the herd move as one toward you.
        if (drive)
            ApplyHerdedToSquad(units, squad, necroPos, farDir);

        // 4. Apply movement. Once the pack commits to the drive, only the wolves already on the
        //    FAR side press the attack — they're the initiators, and charging from behind the herd
        //    they drive it toward the necromancer (and the rest of your animals). The wolves on the
        //    necromancer's side keep holding the ring at standoff: staying farther from the herd than
        //    the initiators, they never become the nearest threat, so they never turn the prey the
        //    wrong way or cut off the lane it flees down toward you.
        for (int s = 0; s < n; s++)
        {
            int u = _hunters[s];
            units[u].WolfHuntTargetId = targetSquadId;

            float slotAngle = SlotAngle(baseAngle, s, n);
            float standoff = herdSpread + herdDet + DetectMargin + units[u].Radius;
            var rel = units[u].Position - herdPos;
            // Only wolves genuinely BEHIND the herd (same depth test that gates the drive) charge in.
            // Wolves merely at the herd's sides (dot ≈ 0) would, if they charged, become the nearest
            // threat off to one side and make the prey bolt sideways instead of toward the necromancer —
            // so they keep circling until they've actually gotten behind it.
            bool behind = rel.Dot(farDir) >= standoff * FarSideDepthFrac;

            if (drive && behind)
            {
                units[u].WolfHuntPhase = 1;
                units[u].WolfHuntTimer = 0f;
                // Initiator: hand it its QUARRY directly — the nearest live member of the
                // hunted herd — and let the normal chase/engage routines take over from here.
                // Leaving target pickup to the generic self-aggro (FindClosestEnemy) sent
                // drives everywhere but the deer: a rat or wild wolf the initiator sprints
                // past would win "closest enemy", the side-chase would die on the horde
                // leash, and the whole pack ended up jogging home with the herd untouched.
                uint quarry = NearestQuarry(units, squad, units[u].Position);
                if (quarry != 0)
                    units[u].Target = CombatTarget.Unit(quarry);
                else
                    // Herd membership resolving to nothing this frame — sprint at the
                    // centroid as before; the next sweep re-tries the assignment.
                    sim.AIWolfHuntMove(u, herdPos, sprint: true, dt);
            }
            else
            {
                // Still flanking: the pack hasn't committed, or this wolf is at the herd's side / the
                // necromancer's side and must hold the ring so the prey keeps fleeing toward you.
                units[u].WolfHuntPhase = 0;
                units[u].WolfHuntTimer += dt;
                Vec2 ringTarget = CircleTarget(units[u].Position, herdPos, slotAngle, standoff);
                // Cautious jog while flanking — a sneak-up, not a charge.
                sim.AIWolfHuntMove(u, ringTarget, sprint: false, dt);
            }
        }
    }

    /// <summary>Id of the live herd member nearest <paramref name="from"/>, or 0 if none resolve —
    /// the deer a committing initiator is pointed at (see the drive branch in <see cref="Update"/>).</summary>
    private static uint NearestQuarry(UnitArrays units, Squad squad, Vec2 from)
    {
        uint best = 0;
        float bestSq = float.MaxValue;
        for (int m = 0; m < squad.Members.Count; m++)
        {
            if (!units.TryGetIndex(squad.Members[m], out int j) || !units[j].Alive) continue;
            float d2 = (units[j].Position - from).LengthSq();
            if (d2 < bestSq) { bestSq = d2; best = squad.Members[m]; }
        }
        return best;
    }

    /// <summary>Widest detection range among the herd's live members (fallback 10). The flank
    /// standoff clears the most alert deer's vision, so no member spots the circling pack early.</summary>
    private static float HerdDetectionRange(UnitArrays units, Squad squad)
    {
        float det = 0f;
        for (int m = 0; m < squad.Members.Count; m++)
            if (units.TryGetIndex(squad.Members[m], out int j) && units[j].Alive
                && units[j].DetectionRange > det)
                det = units[j].DetectionRange;
        return det > 0f ? det : 10f;
    }

    /// <summary>Stamp the "herded" flee cheat on every not-yet-kicked member of the target herd,
    /// each fleeing toward the necromancer from its own position. A one-time kick per deer (see
    /// <see cref="Unit.HerdedApplied"/>), mirroring the single-prey path but for the whole squad.</summary>
    private static void ApplyHerdedToSquad(UnitArrays units, Squad squad, Vec2 necroPos, Vec2 farDir)
    {
        for (int m = 0; m < squad.Members.Count; m++)
        {
            if (!units.TryGetIndex(squad.Members[m], out int j) || !units[j].Alive) continue;
            if (units[j].HerdedApplied) continue;
            var toNecro = necroPos - units[j].Position;
            float tl = toNecro.Length();
            units[j].HerdedDir = tl > 1e-3f ? toNecro * (1f / tl) : farDir * -1f;
            units[j].HerdedTimer = HerdDuration;
            units[j].HerdedApplied = true;
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
        if (!ctx.WolfHuntCommandActive) return false;    // spell-activated: no command → normal aggro
        var u = ctx.Units[ctx.UnitIndex];
        if (u.Archetype != ArchetypeRegistry.HordeMinion) return false;
        if (u.Faction != Faction.Undead) return false;
        if (u.WolfHuntPhase == 1) return false;          // driving — let it engage
        var def = ctx.GameData.Units.Get(u.UnitDefID);
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

    /// <summary>Keep the pack's locked prey HERD while it still has live members (a commanded hunt
    /// sticks with its quarry even as it's driven away from the cast point); otherwise acquire the
    /// herd of the deer nearest <paramref name="refPos"/> (the cast point) within HuntRange —
    /// but only while <paramref name="allowAcquire"/> (the spell timer is armed; an expired
    /// command may finish its current hunt but not start another). Returns the target
    /// <see cref="Squad"/> or null. The lock is a squad id read from whichever hunter
    /// already carries a WolfHuntTargetId.</summary>
    private static Squad? ChooseTargetSquad(Simulation sim, UnitArrays units, Vec2 refPos, bool allowAcquire)
    {
        var squads = sim.Squads;

        // Existing lock (shared across the pack — first hunter with one wins). WolfHuntTargetId
        // holds the SQUAD id here, not a unit id.
        uint lockedId = 0;
        for (int s = 0; s < _hunters.Count; s++)
        {
            uint id = units[_hunters[s]].WolfHuntTargetId;
            if (id != 0) { lockedId = id; break; }
        }
        if (lockedId != 0 && squads.TryGet(lockedId, out var locked)
            && locked.Archetype == ArchetypeRegistry.DeerHerd && locked.AliveCount > 0)
            return locked;

        if (!allowAcquire) return null;

        // Acquire the herd of the nearest deer to the cast point within HuntRange.
        float bestSq = HuntRange * HuntRange;
        int best = -1;
        for (int j = 0; j < units.Count; j++)
        {
            if (!units[j].Alive) continue;
            if (units[j].Archetype != ArchetypeRegistry.DeerHerd) continue;
            float d2 = (units[j].Position - refPos).LengthSq();
            if (d2 < bestSq) { bestSq = d2; best = j; }
        }
        if (best < 0) return null;
        return squads.Get(units[best].SquadId);
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
