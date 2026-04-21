using Necroking.Core;
using Necroking.Movement;

namespace Necroking.Render;

/// <summary>
/// DEBUG-only runtime checks for animation-state invariants. Each invariant is
/// a rule we've implicitly been relying on; violating one produced a real bug
/// earlier in development. Running these every frame in DEBUG builds catches
/// the violation at the exact moment it happens, rather than ten frames later
/// when the visual goes wrong.
///
/// Conditional is <c>DEBUG_ANIM_INVARIANTS</c> so production builds pay zero.
/// Enable by defining DEBUG_ANIM_INVARIANTS in the project file's
/// &lt;DefineConstants&gt;, or just drop the conditional once the invariants
/// are stable.
/// </summary>
public static class AnimInvariants
{
    [System.Diagnostics.Conditional("DEBUG_ANIM_INVARIANTS")]
    public static void Check(Unit unit, AnimController ctrl)
    {
        // --- Incap coherence ---
        // When a unit is actively incapped (not yet recovering), its OverrideAnim
        // must be the HoldAnim. If someone replaces the hold mid-incap, the unit
        // would be gameplay-locked but visually wrong (the Following/Knockdown
        // stuck-frame bug of commit e791330 was a mirror of this).
        if (unit.Incap.Active && !unit.Incap.Recovering)
        {
            Assert(unit.OverrideAnim.IsActive,
                $"Incap active but OverrideAnim is not — nothing is holding the incap pose. unit={unit.Id}");
            Assert(unit.OverrideAnim.State == unit.Incap.HoldAnim,
                $"Incap.HoldAnim={unit.Incap.HoldAnim} but OverrideAnim.State={unit.OverrideAnim.State}. " +
                $"Something replaced the hold override mid-incap. unit={unit.Id}");
        }

        // When recovering, OverrideAnim must be the RecoverAnim (Forced) or we'd
        // be ending incap with a visually-wrong pose.
        if (unit.Incap.Recovering)
        {
            Assert(unit.OverrideAnim.IsActive,
                $"Incap recovering but OverrideAnim cleared — RecoverAnim will never play out. unit={unit.Id}");
            Assert(unit.OverrideAnim.State == unit.Incap.RecoverAnim,
                $"Incap.RecoverAnim={unit.Incap.RecoverAnim} but OverrideAnim.State={unit.OverrideAnim.State}. unit={unit.Id}");
        }

        // --- JumpSystem ownership ---
        // During a jump, JumpSystem owns the controller state. OverrideAnim is
        // allowed to be stale (it'll be cleared post-jump by the normal resolver
        // mismatch path) but the ctrl state must be one of the jump anims.
        if (unit.JumpPhase != 0 && !unit.JumpAttackFired) // post-landing Recovery is forgiving
        {
            bool isJumpAnim = ctrl.CurrentState == AnimState.JumpTakeoff
                           || ctrl.CurrentState == AnimState.JumpLoop
                           || ctrl.CurrentState == AnimState.JumpLand
                           || ctrl.CurrentState == AnimState.JumpAttackSetup
                           || ctrl.CurrentState == AnimState.JumpAttackHit;
            Assert(isJumpAnim || ctrl.CurrentState == AnimState.Idle,
                $"JumpPhase={unit.JumpPhase} but ctrl state is {ctrl.CurrentState}. JumpSystem lost control. unit={unit.Id}");
        }

        // --- PendingAttack coherence ---
        // PendingAttack must be a unit reference OR None. If it's set, an archetype
        // unit (anything non-legacy) must have a concrete weapon index so we know
        // what to resolve at effect_time. Legacy (archetype=0) units are allowed
        // -1 for unarmed fallback.
        if (!unit.PendingAttack.IsNone && unit.Archetype > 0)
        {
            Assert(unit.PendingAttack.IsUnit,
                $"PendingAttack is non-None but not a unit reference on archetype unit. unit={unit.Id}");
            // Ranged fallback path allows weaponIdx=-1 too; just require at least
            // SOME concrete pending state.
            if (!unit.PendingWeaponIsRanged)
            {
                Assert(unit.PendingWeaponIdx >= 0 || unit.Stats.MeleeWeapons.Count == 0,
                    $"Melee PendingAttack set but PendingWeaponIdx=-1 with weapons available. unit={unit.Id}");
            }
        }

        // --- OverrideStarted coherence ---
        // OverrideStarted should only be true while OverrideAnim is active. If the
        // override was cleared without resetting the flag, the next SetOverride
        // call would see a stale true and AnimResolver's mismatch path could wipe
        // the fresh override on its first frame (commit 9247c71 root-caused exactly
        // this class of bug).
        if (unit.OverrideStarted)
        {
            Assert(unit.OverrideAnim.IsActive,
                $"OverrideStarted=true but OverrideAnim is cleared — stale flag will break next override. unit={unit.Id}");
        }

        // --- Incap lockout ---
        // A locked incap unit must have zero velocity. If ORCA or movement code
        // somehow pushes it, we'll see a unit sliding while knocked down.
        if (unit.Incap.IsLocked)
        {
            float vSq = unit.Velocity.LengthSq();
            Assert(vSq < 0.0001f,
                $"Incap locked but velocity={unit.Velocity} — movement bypassed the lockout. unit={unit.Id}");
        }
    }

    private static void Assert(bool cond, string msg)
    {
        if (cond) return;
        DebugLog.Log("anim_invariant", "INVARIANT VIOLATION: " + msg);
        System.Diagnostics.Debug.Assert(cond, msg);
    }
}
