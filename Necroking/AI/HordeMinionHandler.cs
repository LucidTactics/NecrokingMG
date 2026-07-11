using Necroking.Core;
using Necroking.GameSystems;
using Necroking.Movement;
using Microsoft.Xna.Framework;

namespace Necroking.AI;

/// <summary>
/// Horde minion AI archetype: undead units that follow the necromancer's formation.
///
/// Routines:
///   0 = Following    — move to assigned horde formation slot
///   1 = Chasing      — pursuing an enemy assigned by horde system
///   2 = Engaged      — in melee combat with target
///   3 = Returning    — pathfinding back to formation after combat
///   4 = Commanded    — attack-move to a target point, fight enemies there, auto-return when clear or timeout
///
/// The HordeSystem still manages formation geometry (slot positions, circle center).
/// This handler reads horde state and drives unit movement/combat accordingly.
/// Targets are acquired either from horde chasing assignments or direct enemy detection.
/// </summary>
public class HordeMinionHandler : IArchetypeHandler
{
    // Public: external intent APIs (CommandTo/Recall) and spawn paths reference these by
    // name — never write raw byte literals that must match this numbering by convention.
    public const byte RoutineFollowing = 0;
    public const byte RoutineChasing = 1;
    public const byte RoutineEngaged = 2;
    public const byte RoutineReturning = 3;
    public const byte RoutineCommanded = 4;

    // Following subroutines
    private const byte FollowMoving = 0;  // actively moving toward slot
    private const byte FollowIdle = 1;    // arrived at slot, waiting for slot to drift >1.5 before moving again

    private const float FollowDeadzone = 1.5f;  // don't issue new move until slot is this far away
    private const float FollowArriveThreshold = 0.5f; // close enough to count as "arrived"

    // Follow-effort matrix distance bands (distance from formation slot).
    private const float FollowNearBand = 5f;   // < this  → "in slot"   (row 0)
    private const float FollowFarBand  = 15f;  // ≥ this  → "straggler" (row 2); between → "trailing" (row 1)

    // Effort a following minion targets, indexed [distanceRow, necroColumn].
    //   rows: 0 = in slot (<5u), 1 = trailing (5–15u), 2 = straggler (>15u)
    //   cols: 0 = necro stopped, 1 = necro moving (normal), 2 = necro sprinting
    // Hand-tuned on purpose — NOT max(distanceBand, necroEffort): a plain max
    // would put Sprint in the in-slot×sprinting corner, making a minion glued to
    // its slot sprint-in-place the instant the necro sprints. The matrix keeps
    // the whole top row off Sprint, so a minion only ramps to Sprint once it has
    // actually fallen into the trailing/straggler bands. Net feel: tight when you
    // stroll, a streaming trail when you sprint, and stragglers always sprint back
    // regardless of what the necro is doing. Hurry == Jog gait.
    private static readonly MoveEffort[,] FollowEffortMatrix =
    {
        //   stopped             moving              sprinting
        { MoveEffort.Walk,  MoveEffort.Walk,   MoveEffort.Hurry  },  // in slot   (<5u)
        { MoveEffort.Walk,  MoveEffort.Hurry,  MoveEffort.Sprint },  // trailing  (5–15u)
        { MoveEffort.Hurry, MoveEffort.Sprint, MoveEffort.Sprint },  // straggler (>15u)
    };

    private const float CommandTimeout = 45f;
    private const float CommandClearRadius = 10f;

    public void OnSpawn(ref AIContext ctx)
    {
        ctx.Routine = RoutineFollowing;
        ctx.Subroutine = 0;
        ctx.SubroutineTimer = 0f;

        // Auto-enroll in horde
        ctx.Horde?.AddUnit(ctx.MyId);
    }

    public void OnRoutineExit(ref AIContext ctx, byte oldRoutine, byte newRoutine)
    {
        // The combat routines own the melee-lock fields. EngagedTarget must not leak
        // across routines; PendingAttack and PostAttackTimer are deliberately KEPT —
        // an in-progress swing finishes planted and resolves (Hit/Miss/Whiff) at its
        // animation's impact frame, which is guaranteed to land inside the
        // PostAttackTimer window (and the SwingJanitor in Game1.Animation clears
        // anything that slips through when that window expires — no pin risk).
        // Clearing PendingAttack here made escaped-target swings vanish silently:
        // the anim played but the dice never rolled and nothing was logged.
        if (oldRoutine == RoutineChasing || oldRoutine == RoutineEngaged || oldRoutine == RoutineCommanded)
        {
            ctx.Units[ctx.UnitIndex].EngagedTarget = CombatTarget.None;
        }
    }

    public void OnRoutineEnter(ref AIContext ctx, byte oldRoutine, byte newRoutine)
    {
        // Returning/Following are the disengaged states — entering them means the fight
        // is over, whatever path led here.
        if (newRoutine == RoutineReturning || newRoutine == RoutineFollowing)
        {
            ctx.Units[ctx.UnitIndex].Target = CombatTarget.None;
            ctx.Units[ctx.UnitIndex].InCombat = false;
        }
    }

    // ═══════════════════════════════════════
    //  External intent APIs (player orders)
    // ═══════════════════════════════════════

    /// <summary>Player "Command" (attack-move) order — the external entry point for the
    /// built-in order_attack ability. Re-issuing while already Commanded restarts the
    /// command (timer + target) rather than continuing the old one.</summary>
    public static void CommandTo(Movement.UnitArrays units, int idx, Vec2 target)
    {
        AIControl.StartRoutine(units, idx, RoutineCommanded);
        units[idx].MoveTarget = target;
        // Fresh order — drop whatever fight the unit was in and reassess at the target.
        units[idx].Target = CombatTarget.None;
        units[idx].EngagedTarget = CombatTarget.None;
    }

    /// <summary>Player "Regroup" order: pull a commanded minion straight back to formation
    /// (the same reset <see cref="ReturnFromCommand"/> does when a command finishes on its
    /// own). No-op unless the unit is currently under a command.</summary>
    public static void Recall(Movement.UnitArrays units, int idx)
    {
        if (units[idx].Routine != RoutineCommanded) return;
        AIControl.StartRoutine(units, idx, RoutineReturning);
    }

    public void Update(ref AIContext ctx)
    {
        if (ctx.Horde == null) return;

        // Sync with horde system state
        var hordeState = ctx.Horde.GetUnitState(ctx.MyId);
        SyncHordeState(ref ctx, hordeState);

        // Amortize only low-urgency routines: Following drifts to a slot and
        // Returning walks back after combat — neither reacts to per-frame
        // events. Chasing/Engaged/Commanded stay every-frame so combat and
        // player orders respond instantly. On skipped frames the unit keeps
        // its previous PreferredVel; ORCA still runs, so it keeps moving.
        //
        // Urgent events bypass amortization: an amortized minion that just got
        // hit, or whose combat state just flipped, needs to react *this* frame
        // — otherwise the reaction is delayed up to AIUpdateInterval (6 frames
        // / ~100ms) which reads as input lag in combat.
        bool lowUrgency = ctx.Routine == RoutineFollowing || ctx.Routine == RoutineReturning;
        bool urgent = ctx.Units[ctx.UnitIndex].HitReacting
            || ctx.Units[ctx.UnitIndex].JustEnteredCombat
            || ctx.Units[ctx.UnitIndex].JustLeftCombat;
        // Amortized skip: the velocity cap no longer needs re-applying here.
        // Locomotion.UpdateSpeeds derives MaxSpeed each frame from the persisted
        // MoveEffort, so a skipped follower keeps its Jog/Sprint speed
        // automatically. PreferredVel from the last full tick still points at
        // the slot.
        if (lowUrgency && !ctx.IsAmortizeTick && !urgent) return;

        // Execute current routine
        switch (ctx.Routine)
        {
            case RoutineFollowing: UpdateFollowing(ref ctx); break;
            case RoutineChasing:   UpdateChasing(ref ctx); break;
            case RoutineEngaged:   UpdateEngaged(ref ctx); break;
            case RoutineReturning: UpdateReturning(ref ctx); break;
            case RoutineCommanded: UpdateCommanded(ref ctx); break;
        }
    }

    /// <summary>Sync our routine with the HordeSystem's state assignments.</summary>
    private static void SyncHordeState(ref AIContext ctx, HordeUnitState hordeState)
    {
        // Don't override commanded units with horde assignments
        if (ctx.Routine == RoutineCommanded) return;

        // Horde assigns Chasing with a target — pick it up
        if (hordeState == HordeUnitState.Chasing && ctx.Routine != RoutineChasing && ctx.Routine != RoutineEngaged)
        {
            // Pack-hunting wolves mid-stalk must not be yanked onto a naive charge by a horde
            // chase assignment (stale or fresh) — same suppression as UpdateFollowing's
            // self-aggro. Clear the horde-side state too, or this branch re-fires every tick.
            if (WolfPackHuntAI.WantsToFlank(ref ctx))
            {
                ctx.Horde!.ResetChasingToFollowing(ctx.MyId);
                return;
            }
            uint chasingId = ctx.Horde!.GetChasingTarget(ctx.MyId);
            if (chasingId != GameConstants.InvalidUnit)
            {
                ctx.Units[ctx.UnitIndex].Target = CombatTarget.Unit(chasingId);
                ctx.TransitionTo(RoutineChasing);
                DebugLog.Log("horde_aggro",
                    $"  [Minion {ctx.MyId}] accepted horde Chasing assignment → target={chasingId}");
            }
        }

        // Horde says return → obey even if target is still alive. Previously we
        // only returned when the target was dead, which let a single chaser drag
        // the whole horde across the map. The horde system is the authority on
        // whether a minion should keep pursuing; we trust its Returning signal.
        // Still-engaged minions (InCombat=true) finish their swing cycle first
        // (UpdateEngaged will naturally transition to Returning when the target
        // leaves range or dies).
        //
        // Following counts too: HordeSystem.UpdateStates flips Chasing→Following
        // (not Returning) when the chase target leaves AggroRadius, expecting the
        // minion to fall back. Without this branch the minion kept Chasing on its
        // own until the hard leash break at `leashRadius * 1.5` — so between
        // leashRadius and 1.5× a chaser dragged east of the necromancer would
        // pursue a fleeing wild deer indefinitely.
        bool hordeSaysGiveUp =
            hordeState == HordeUnitState.Returning
            || hordeState == HordeUnitState.Following;
        if (hordeSaysGiveUp
            && (ctx.Routine == RoutineChasing
                || (ctx.Routine == RoutineEngaged && !ctx.Units[ctx.UnitIndex].InCombat)))
        {
            // Exit/enter hooks clear Target/EngagedTarget/PendingAttack/InCombat.
            ctx.TransitionTo(RoutineReturning);
        }
    }

    private static void UpdateFollowing(ref AIContext ctx)
    {
        // If a target is already set (e.g. DamageSystem assigns Target=attacker
        // on hit for units whose EngagedTarget was None) and it's alive, switch
        // to Chasing so we actually fight back. Without this, a following minion
        // hit by a wolf would keep following its slot — target set but ignored.
        if (SubroutineSteps.IsTargetAlive(ref ctx))
        {
            ctx.TransitionTo(RoutineChasing);
            DebugLog.Log("horde_aggro",
                $"  [Minion {ctx.MyId}] UpdateFollowing saw live Target → Chasing " +
                $"(target id={ctx.Units[ctx.UnitIndex].Target.UnitID})");
            return;
        }

        // No target — scan for enemies within engagement range. Pack-hunting wolves
        // in the flank phase are the exception: they must stalk around to the far side
        // of a deer rather than charge it, so WolfPackHuntAI suppresses this proactive
        // acquisition until the pack commits to the drive (then the wolf engages normally).
        if (!WolfPackHuntAI.WantsToFlank(ref ctx))
        {
            // Per-unit aggro scale — "timid" units (e.g. zombie deer) engage at a shorter range.
            float engageRange = (ctx.Horde?.EngagementRange ?? 10f) * ctx.Units[ctx.UnitIndex].AggroRangeScale;
            int enemy = SubroutineSteps.FindClosestEnemy(ref ctx, engageRange);
            if (enemy >= 0)
            {
                ctx.Units[ctx.UnitIndex].Target = CombatTarget.Unit(ctx.Units[enemy].Id);
                ctx.TransitionTo(RoutineChasing);
                DebugLog.Log("horde_aggro",
                    $"  [Minion {ctx.MyId}] self-aggro (UpdateFollowing) → enemy id={ctx.Units[enemy].Id} " +
                    $"dist={(ctx.Units[enemy].Position - ctx.MyPos).Length():F1} range={engageRange:F1}");
                return;
            }
        }

        // Follow horde slot position with deadzone to prevent stuttering
        if (ctx.Horde != null && ctx.Horde.GetTargetPosition(ctx.MyId, out var slotPos))
        {
            float dist = (ctx.MyPos - slotPos).Length();

            if (ctx.Subroutine == FollowIdle)
            {
                // Idle at slot — only start moving again if slot drifted far enough.
                // SetIdle() zeros PreferredVel *and* sets RoutineAnim=Idle so the
                // AnimResolver stops picking whatever locomotion state MoveToward
                // left behind the last time the unit was chasing the slot.
                SubroutineSteps.SetIdle(ref ctx);
                if (dist > FollowDeadzone)
                    ctx.Subroutine = FollowMoving;
            }
            else
            {
                // Actively moving toward slot. Effort comes from a 2-axis matrix:
                // distance-from-slot × how hard the necromancer is moving. See
                // FollowEffortMatrix for why it's hand-tuned rather than a simple
                // max() of the two axes. Pick the necro column once (sprint wins
                // over moving wins over stopped — sprinting implies moving).
                bool necroSprinting = ctx.NecroSprintT > 0.5f;
                bool necroMoving = ctx.Horde!.IsNecroMoving;
                int col = necroSprinting ? 2 : necroMoving ? 1 : 0;
                int row = dist < FollowNearBand ? 0
                        : dist < FollowFarBand   ? 1
                        : 2;
                SubroutineSteps.SetEffort(ref ctx, FollowEffortMatrix[row, col]);

                // Only allow the "arrived → idle" flip while the necro is parked.
                // If the necro is moving, a minion that momentarily touches its
                // (also-moving) slot must NOT drop to FollowIdle/Idle anim — the
                // slot slides away again next frame and the unit would foot-stutter
                // between Idle and a gait. Keep driving toward the slot so it holds
                // a smooth locomotion gait; settle to Idle only once movement stops.
                if (dist > FollowArriveThreshold || necroMoving || necroSprinting)
                {
                    SubroutineSteps.MoveToward(ref ctx, slotPos, ctx.MyMaxSpeed);
                }
                else
                {
                    // Arrived AND necro is stopped — flip the routine anim to Idle
                    // right away (critical, otherwise the last Walk/Jog/Run request
                    // sticks and the unit walks-in-place until the next chase).
                    SubroutineSteps.SetIdle(ref ctx);
                    ctx.Subroutine = FollowIdle;
                }
            }
        }
        else
        {
            SubroutineSteps.SetIdle(ref ctx);
        }
    }

    private static void UpdateChasing(ref AIContext ctx)
    {
        // Canonical chase-exit checks (dead target → Return, leash break → Return).
        // Pack-hunting wolves (hunt tag set) chase their driven quarry outside the horde
        // circle BY DESIGN — suspend the leash break (0 = no leash) or the whole pack
        // abandons the deer in unison the moment it crosses the radius. WolfPackHuntAI's
        // own, longer leash bounds the hunt instead.
        float leash = ctx.Units[ctx.UnitIndex].WolfHuntTargetId != 0
            ? 0f : ctx.Horde?.LeashRadius ?? 0f;
        Vec2 center = ctx.Horde?.CircleCenter ?? Vec2.Zero;
        if (CombatTransitions.StandardChasingExits(ref ctx, RoutineReturning, leash, center))
            return;

        int targetIdx = SubroutineSteps.ResolveTarget(ref ctx);
        if (targetIdx >= 0)
        {
            // Full Sprint commit when chasing an enemy.
            SubroutineSteps.SetEffort(ref ctx, MoveEffort.Sprint);
            SubroutineSteps.MoveToward(ref ctx, ctx.Units[targetIdx].Position, ctx.MyMaxSpeed);

            // Auto-engage when in melee range
            float dist = (ctx.Units[targetIdx].Position - ctx.MyPos).Length();
            float engageRange = SubroutineSteps.GetMeleeRange(ref ctx, targetIdx);
            if (dist <= engageRange)
            {
                ctx.TransitionTo(RoutineEngaged);
                ctx.Units[ctx.UnitIndex].EngagedTarget = ctx.Units[ctx.UnitIndex].Target;
            }
        }
    }

    private static void UpdateEngaged(ref AIContext ctx)
    {
        // Canonical engaged-exit checks:
        //   - target dead → Returning (or Chasing if frenzied with another target)
        //   - target alive but out of melee (>1.2× range) → Chasing
        //   - leashed too far from horde center → Returning
        // Same pack-hunt exemption as UpdateChasing: a wolf mid-kill on its driven quarry
        // must not drop it on the horde leash.
        float leash = ctx.Units[ctx.UnitIndex].WolfHuntTargetId != 0
            ? 0f : ctx.Horde?.LeashRadius ?? 0f;
        Vec2 center = ctx.Horde?.CircleCenter ?? Vec2.Zero;
        if (CombatTransitions.StandardEngagedExits(ref ctx,
                chasingRoutine: RoutineChasing,
                returningRoutine: RoutineReturning,
                leashRadius: leash,
                leashCenter: center))
            return;

        // Stay near target, let combat system handle attacks.
        SubroutineSteps.AttackTarget(ref ctx);
    }

    private static void UpdateReturning(ref AIContext ctx)
    {
        ctx.Units[ctx.UnitIndex].Target = CombatTarget.None;
        ctx.Units[ctx.UnitIndex].EngagedTarget = CombatTarget.None;
        ctx.Units[ctx.UnitIndex].InCombat = false;
        ctx.Units[ctx.UnitIndex].PendingAttack = CombatTarget.None;

        if (ctx.Horde != null && ctx.Horde.GetTargetPosition(ctx.MyId, out var slotPos))
        {
            float dist = (ctx.MyPos - slotPos).Length();
            if (dist > 1.5f)
            {
                // Sprint home — Returning is triggered by leash break (out of
                // place after combat). Definitionally urgent.
                SubroutineSteps.SetEffort(ref ctx, MoveEffort.Sprint);
                SubroutineSteps.MoveToward(ref ctx, slotPos, ctx.MyMaxSpeed);
            }
            else
            {
                ctx.TransitionTo(RoutineFollowing, FollowIdle);
                ctx.Units[ctx.UnitIndex].PreferredVel = Vec2.Zero;
            }
        }
        else
        {
            ctx.TransitionTo(RoutineFollowing, FollowMoving);
            ctx.Units[ctx.UnitIndex].PreferredVel = Vec2.Zero;
        }
    }

    private static void UpdateCommanded(ref AIContext ctx)
    {
        ctx.SubroutineTimer += ctx.Dt;
        Vec2 commandTarget = ctx.Units[ctx.UnitIndex].MoveTarget;

        // Timeout — return to horde
        if (ctx.SubroutineTimer > CommandTimeout)
        {
            ReturnFromCommand(ref ctx);
            return;
        }

        // If we have a combat target, fight it
        if (SubroutineSteps.IsTargetAlive(ref ctx))
        {
            int targetIdx = SubroutineSteps.ResolveTarget(ref ctx);
            if (targetIdx >= 0)
            {
                float dist = (ctx.Units[targetIdx].Position - ctx.MyPos).Length();
                float meleeRange = SubroutineSteps.GetMeleeRange(ref ctx, targetIdx);
                if (dist <= meleeRange)
                {
                    ctx.Units[ctx.UnitIndex].EngagedTarget = ctx.Units[ctx.UnitIndex].Target;
                    SubroutineSteps.AttackTarget(ref ctx);
                }
                else
                {
                    // Commanded chase = Sprint (player issued attack-move).
                    SubroutineSteps.SetEffort(ref ctx, MoveEffort.Sprint);
                    SubroutineSteps.MoveToward(ref ctx, ctx.Units[targetIdx].Position, ctx.MyMaxSpeed);
                }
            }
            return;
        }

        // No current target — are we at the command point?
        float distToTarget = (ctx.MyPos - commandTarget).Length();
        if (distToTarget > 2f)
        {
            // Still moving to command point — Hurry (purposeful but not panic).
            ctx.Units[ctx.UnitIndex].Target = CombatTarget.None;
            ctx.Units[ctx.UnitIndex].EngagedTarget = CombatTarget.None;
            SubroutineSteps.SetEffort(ref ctx, MoveEffort.Hurry);
            SubroutineSteps.MoveToward(ref ctx, commandTarget, ctx.MyMaxSpeed);
        }
        else
        {
            // At command point — look for enemies nearby
            int enemy = SubroutineSteps.FindClosestEnemy(ref ctx, CommandClearRadius);
            if (enemy >= 0)
            {
                ctx.Units[ctx.UnitIndex].Target = CombatTarget.Unit(ctx.Units[enemy].Id);
            }
            else
            {
                // Area is clear — return to horde
                ReturnFromCommand(ref ctx);
            }
        }
    }

    private static void ReturnFromCommand(ref AIContext ctx)
    {
        // Exit/enter hooks clear Target/EngagedTarget/PendingAttack/InCombat.
        ctx.TransitionTo(RoutineReturning);
    }

    public string GetRoutineName(byte routine) => routine switch
    {
        RoutineFollowing => "Following",
        RoutineChasing => "Chasing",
        RoutineEngaged => "Engaged",
        RoutineReturning => "Returning",
        RoutineCommanded => "Commanded",
        _ => $"Unknown({routine})"
    };

    public string GetSubroutineName(byte routine, byte subroutine) =>
        subroutine == 0 ? "Default" : $"Unknown({subroutine})";
}
