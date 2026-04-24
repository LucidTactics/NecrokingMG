using System;
using System.Collections.Generic;
using Necroking.Core;
using Necroking.Data;
using Necroking.Game;
using Necroking.Movement;
using Necroking.Render;

namespace Necroking.GameSystems;

/// <summary>
/// Scripted charge / trample system. An attacker homes on a smaller-sized target
/// at CombatSpeed × (1 + SpeedBonus). While in flight it phases through units
/// smaller than itself, damaging each one that enters TrampleRadius (independent
/// hit roll + knockback impulse). The charge ends when the attacker closes
/// within TrampleImpactRange of the target, traverses TrampleMaxChaseDistance,
/// hits a wall, or meets a unit of equal-or-larger size. On impact a radial
/// explosion impulse fires (shared with spell knockback via PhysicsSystem).
///
/// ChargePhase state machine:
///   0 = None
///   1 = Charging  — homing toward ChargeTargetId's current position
///   2 = Recovery  — on-ground lockout while recovery timer counts down
///
/// Integration points (see Simulation.cs):
///   - Weapon selection branch at the attack-queue loop (size + range gate)
///   - Movement entry guard at UpdateMovement (ChargePhase==1 writes Velocity
///     directly, skips ORCA, passes through smaller units but respects walls)
///   - Facing / AI updates skip when ChargePhase > 0 (same pattern as Jumping)
///   - Per-unit TickCharge call from the main update loop
/// </summary>
public static class TrampleSystem
{
    private const float RecoveryDuration = 0.6f;

    /// <summary>Begin charging idx toward the live target. Locks the weapon cooldown
    /// and drives the charge state machine; TickCharge will home on the target's
    /// current position every frame. Returns false if initiation failed (invalid
    /// target, weapon).</summary>
    public static bool BeginCharge(UnitArrays units, int idx, int targetIdx,
        int weaponIdx, Simulation sim)
    {
        if (idx < 0 || idx >= units.Count || !units[idx].Alive) return false;
        if (targetIdx < 0 || targetIdx >= units.Count || !units[targetIdx].Alive) return false;
        if (weaponIdx < 0 || weaponIdx >= units[idx].Stats.MeleeWeapons.Count) return false;

        var weapon = units[idx].Stats.MeleeWeapons[weaponIdx];

        units[idx].ChargePhase = 1;
        units[idx].ChargeTargetId = units[targetIdx].Id;
        units[idx].ChargeWeaponIdx = weaponIdx;
        units[idx].ChargeTraveled = 0f;
        units[idx].ChargeRecoveryTimer = 0f;
        if (units[idx].TrampledIds == null) units[idx].TrampledIds = new HashSet<uint>();
        else units[idx].TrampledIds!.Clear();

        // Face the target immediately (no rate cap during charge commit).
        // FacingAngle is stored in DEGREES (see Movement/FacingUtil.cs).
        Vec2 toTarget = units[targetIdx].Position - units[idx].Position;
        if (toTarget.LengthSq() > 0.0001f)
            units[idx].FacingAngle = MathF.Atan2(toTarget.Y, toTarget.X) * (180f / MathF.PI);

        // Drive a Run anim during the charge so sprite matches locomotion. The
        // per-weapon AnimName override (if any) will be applied via normal anim
        // resolution; here we just make sure the default locomotion fits the pace.
        units[idx].PreferredVel = Vec2.Zero; // we write Velocity directly in UpdateMovement

        DebugLog.Log("trample",
            $"[BeginCharge] unit#{idx} → target id={units[targetIdx].Id} " +
            $"dist={toTarget.Length():F2} maxChase={weapon.TrampleMaxChaseDistance:F1} " +
            $"speedBonus={weapon.TrampleSpeedBonus:P0}");
        return true;
    }

    /// <summary>Per-frame update for a single charging unit. Call from the main
    /// simulation tick BEFORE UpdateMovement so the velocity write takes effect
    /// this frame. Returns true if the unit is still charging (caller may skip
    /// other AI/movement paths), false otherwise.</summary>
    public static bool TickCharge(float dt, UnitArrays units, int idx, Simulation sim)
    {
        if (idx < 0 || idx >= units.Count) return false;
        if (units[idx].ChargePhase == 0) return false;

        // Recovery phase: count down, then clear.
        if (units[idx].ChargePhase == 2)
        {
            units[idx].ChargeRecoveryTimer -= dt;
            if (units[idx].ChargeRecoveryTimer <= 0f)
            {
                units[idx].ChargePhase = 0;
                units[idx].ChargeTargetId = GameConstants.InvalidUnit;
                units[idx].ChargeWeaponIdx = -1;
                units[idx].ChargeTraveled = 0f;
                units[idx].TrampledIds?.Clear();
            }
            return true;
        }

        // Phase 1 — active charge. Unit dead → end.
        if (!units[idx].Alive) { EndCharge(units, idx, sim, impactAtPos: null); return false; }

        int weaponIdx = units[idx].ChargeWeaponIdx;
        if (weaponIdx < 0 || weaponIdx >= units[idx].Stats.MeleeWeapons.Count)
        { EndCharge(units, idx, sim, impactAtPos: null); return false; }
        var weapon = units[idx].Stats.MeleeWeapons[weaponIdx];

        // Look up target; if dead or missing, fire impact at current pos and end.
        int targetIdx = UnitUtil.ResolveUnitIndex(units, units[idx].ChargeTargetId);
        if (targetIdx < 0 || !units[targetIdx].Alive)
        {
            DebugLog.Log("trample", $"[TickCharge] unit#{idx} target vanished mid-charge; impacting at self pos");
            EndCharge(units, idx, sim, impactAtPos: units[idx].Position);
            return true;
        }

        // Home on target's current position.
        Vec2 toTarget = units[targetIdx].Position - units[idx].Position;
        float distToTarget = toTarget.Length();

        // Impact: within TrampleImpactRange — fire full attack + radial blast + end.
        if (distToTarget <= weapon.TrampleImpactRange)
        {
            DebugLog.Log("trample",
                $"[TickCharge] unit#{idx} IMPACT at target id={units[targetIdx].Id} " +
                $"dist={distToTarget:F2} traveled={units[idx].ChargeTraveled:F2}");
            // Primary target attack — full damage roll with bonuses.
            TryTrampleHit(sim, idx, targetIdx, weaponIdx, forceDir: Vec2.Zero, isImpact: true);
            // Radial AOE explosion around target: knocks everyone nearby off their feet
            // without additional damage (transit hits already handled damage for small
            // units; big units don't take damage but get blown back). Excludes the
            // charger itself — otherwise the boar gets launched by its own impact.
            RadialImpulseExcluding(sim, units[targetIdx].Position,
                weapon.TrampleRadius * 1.5f, weapon.TrampleImpactForce,
                upwardForce: weapon.TrampleImpactForce * 0.3f, excludeIdx: idx);
            // Pass null to EndCharge: we already fired the radial; don't double-fire.
            EndCharge(units, idx, sim, impactAtPos: null);
            return true;
        }

        // Max chase distance cap — impact in place and end.
        if (units[idx].ChargeTraveled >= weapon.TrampleMaxChaseDistance)
        {
            DebugLog.Log("trample",
                $"[TickCharge] unit#{idx} max chase distance exceeded " +
                $"({units[idx].ChargeTraveled:F2} ≥ {weapon.TrampleMaxChaseDistance:F1}); ending");
            EndCharge(units, idx, sim, impactAtPos: units[idx].Position);
            return true;
        }

        // Compute step velocity — straight-line homing at CombatSpeed × (1 + bonus).
        float speed = units[idx].Stats.CombatSpeed * (1f + weapon.TrampleSpeedBonus);
        float invDist = distToTarget > 0.0001f ? 1f / distToTarget : 0f;
        Vec2 dir = new Vec2(toTarget.X * invDist, toTarget.Y * invDist);
        float stepLen = speed * dt;
        // Don't overshoot — cap step to remaining distance to target.
        if (stepLen > distToTarget) stepLen = distToTarget;

        // Write velocity directly (UpdateMovement's ChargePhase==1 guard uses
        // Velocity without ORCA, letting us phase through smaller units).
        units[idx].Velocity = new Vec2(dir.X * speed, dir.Y * speed);
        // Face the charge direction (FacingAngle is in degrees — see FacingUtil).
        units[idx].FacingAngle = MathF.Atan2(dir.Y, dir.X) * (180f / MathF.PI);
        // Track distance for the max-chase cap (uses velocity-based step so it
        // reflects what actually moved — wall slides are accounted for by
        // re-comparing position next frame).
        Vec2 preMovePos = units[idx].Position;

        // Scan for smaller-sized hostiles within TrampleRadius of the charger
        // at current (pre-move) position. The actual movement happens later in
        // UpdateMovement; scanning here means victims get damage before being
        // physically blown back by the step, which keeps the visual correct.
        ScanAndTrample(sim, idx, weaponIdx, weapon);

        // Check for an equal-or-larger unit blocking the path (not the target —
        // target is handled by the impact range check above). If one is directly
        // in front and very close, stop the charge.
        if (TryCheckBlockingUnit(sim, idx, weapon.TrampleRadius, out int blockerIdx))
        {
            DebugLog.Log("trample",
                $"[TickCharge] unit#{idx} blocked by larger unit id={units[blockerIdx].Id} " +
                $"size={units[blockerIdx].Size} (self size={units[idx].Size}); ending");
            EndCharge(units, idx, sim, impactAtPos: units[idx].Position);
            return true;
        }

        // UpdateMovement (later in the tick) will apply the Velocity with wall
        // collision. Next frame we'll measure how far we actually moved.
        // Here we estimate travel for the cap:
        units[idx].ChargeTraveled += stepLen;
        _ = preMovePos; // silence unused; kept for future position-delta telemetry

        return true;
    }

    /// <summary>Scan quadtree for smaller-sized hostile units within TrampleRadius
    /// of the charger. For each one not yet trampled, roll a melee hit and apply
    /// knockback impulse on success.</summary>
    private static readonly List<uint> _scratch = new(16);
    private static void ScanAndTrample(Simulation sim, int idx, int weaponIdx, WeaponStats weapon)
    {
        var units = sim.UnitsMut;
        Faction selfFaction = units[idx].Faction;
        FactionMask mask = FactionMaskExt.AllExcept(selfFaction);

        _scratch.Clear();
        sim.Quadtree.QueryRadiusByFaction(units[idx].Position, weapon.TrampleRadius, mask, _scratch);

        int selfSize = units[idx].Size;
        for (int k = 0; k < _scratch.Count; k++)
        {
            int vi = UnitUtil.ResolveUnitIndex(units, _scratch[k]);
            if (vi < 0 || vi == idx || !units[vi].Alive) continue;
            if (units[vi].Size >= selfSize) continue; // only smaller victims
            if (units[idx].TrampledIds!.Contains(units[vi].Id)) continue;
            // Mark before resolving so re-entries can't double-hit even if
            // ResolveMeleeAttack triggers secondary effects that might scan.
            units[idx].TrampledIds!.Add(units[vi].Id);

            Vec2 toVic = units[vi].Position - units[idx].Position;
            TryTrampleHit(sim, idx, vi, weaponIdx,
                forceDir: toVic.LengthSq() > 0.0001f ? toVic : new Vec2(units[idx].FacingAngle, 0),
                isImpact: false);
        }
    }

    /// <summary>Resolve a single melee hit (routing through the shared ResolveMeleeAttack
    /// pipeline so all bonuses — knockdown, armor, coats — apply uniformly), then
    /// tack on a directional knockback impulse if the hit actually damaged them.
    /// isImpact=true uses the impact force (heavier blow); false uses the transit
    /// pass-through force.</summary>
    private static void TryTrampleHit(Simulation sim, int attackerIdx, int defenderIdx,
        int weaponIdx, Vec2 forceDir, bool isImpact)
    {
        var units = sim.UnitsMut;
        if (!units[defenderIdx].Alive) return;

        int hpBefore = units[defenderIdx].Stats.HP;
        sim.ResolveMeleeAttackExternal(attackerIdx, defenderIdx, weaponIdx);

        // Apply knockback only if the hit actually connected (HP changed) — a
        // dodge (miss) leaves the defender untouched, matching the "got out of
        // the way" narrative the attack archetype advertises.
        if (!units[defenderIdx].Alive || units[defenderIdx].Stats.HP == hpBefore) return;

        var weapon = units[attackerIdx].Stats.MeleeWeapons[weaponIdx];
        float force = isImpact ? weapon.TrampleImpactForce : weapon.TrampleKnockbackForce;
        Vec2 dir = forceDir;
        float len = dir.Length();
        if (len < 0.0001f)
        {
            // Fall back to attacker's facing (FacingAngle is degrees — convert).
            float fRad = units[attackerIdx].FacingAngle * (MathF.PI / 180f);
            dir = new Vec2(MathF.Cos(fRad), MathF.Sin(fRad));
        }
        else dir = new Vec2(dir.X / len, dir.Y / len);

        sim.Physics.ApplyImpulse(units, defenderIdx, dir, force, upwardForce: force * 0.2f);
    }

    /// <summary>Return true if a unit of size >= self is within probeRadius in the
    /// charge direction (roughly in front). Used to stop a charge that would ram
    /// a same-or-larger target not being directly charged at.</summary>
    private static bool TryCheckBlockingUnit(Simulation sim, int idx, float probeRadius, out int blockerIdx)
    {
        blockerIdx = -1;
        var units = sim.UnitsMut;
        _scratch.Clear();
        sim.Quadtree.QueryRadius(units[idx].Position, probeRadius, _scratch);

        int selfSize = units[idx].Size;
        // FacingAngle is in degrees; convert for trig.
        float selfFacingRad = units[idx].FacingAngle * (MathF.PI / 180f);
        float fc = MathF.Cos(selfFacingRad), fs = MathF.Sin(selfFacingRad);
        uint chargeTgt = units[idx].ChargeTargetId;

        for (int k = 0; k < _scratch.Count; k++)
        {
            int oi = UnitUtil.ResolveUnitIndex(units, _scratch[k]);
            if (oi < 0 || oi == idx || !units[oi].Alive) continue;
            if (units[oi].Id == chargeTgt) continue; // the charge target goes through its own impact path
            if (units[oi].Size < selfSize) continue; // smaller units don't block
            // Cone-forward check: only block if they're roughly in front (dot > 0).
            Vec2 d = units[oi].Position - units[idx].Position;
            float dLen = d.Length();
            if (dLen < 0.0001f) { blockerIdx = oi; return true; }
            float dot = (d.X * fc + d.Y * fs) / dLen;
            if (dot <= 0f) continue; // behind or beside us
            blockerIdx = oi;
            return true;
        }
        return false;
    }

    /// <summary>End the charge, optionally firing a radial impact impulse at impactAtPos
    /// (for wall-slam / blocker-slam scenarios where no explicit target was hit).</summary>
    public static void EndCharge(UnitArrays units, int idx, Simulation sim, Vec2? impactAtPos)
    {
        if (idx < 0 || idx >= units.Count) return;
        if (units[idx].ChargePhase == 0) return;

        // If caller supplied a pos AND the normal impact path didn't already fire
        // its radial (which is the case for wall/blocker stops), fire one here.
        // The primary-target impact path sets the radial itself, then calls EndCharge
        // with the same pos; we pass null from that path to avoid double-firing, so
        // impactAtPos != null ⇒ this is the "unexpected stop" radial.
        if (impactAtPos.HasValue && units[idx].ChargeWeaponIdx >= 0
            && units[idx].ChargeWeaponIdx < units[idx].Stats.MeleeWeapons.Count)
        {
            var weapon = units[idx].Stats.MeleeWeapons[units[idx].ChargeWeaponIdx];
            RadialImpulseExcluding(sim, impactAtPos.Value,
                weapon.TrampleRadius * 1.5f, weapon.TrampleImpactForce,
                upwardForce: weapon.TrampleImpactForce * 0.3f, excludeIdx: idx);
        }

        units[idx].ChargePhase = 2;
        units[idx].ChargeRecoveryTimer = RecoveryDuration;
        units[idx].Velocity = Vec2.Zero;
        units[idx].PreferredVel = Vec2.Zero;
    }

    /// <summary>Radial physics impulse (explosion) that skips the charger and
    /// same-faction allies — trample doesn't friendly-fire, so the impact blast
    /// only launches hostiles. Linear falloff, mirrors the shape of
    /// PhysicsSystem.ApplyRadialImpulse but with faction + self filtering.</summary>
    private static void RadialImpulseExcluding(Simulation sim, Vec2 center, float radius,
        float force, float upwardForce, int excludeIdx)
    {
        var units = sim.UnitsMut;
        Faction ownerFaction = units[excludeIdx].Faction;
        float r2 = radius * radius;
        for (int i = 0; i < units.Count; i++)
        {
            if (i == excludeIdx) continue;
            if (!units[i].Alive || units[i].InPhysics) continue;
            if (units[i].Faction == ownerFaction) continue; // no friendly fire
            Vec2 delta = units[i].Position - center;
            float d2 = delta.LengthSq();
            if (d2 > r2) continue;
            float dist = MathF.Sqrt(d2);
            float falloff = 1f - (dist / radius);
            Vec2 dir = dist > 0.01f ? delta * (1f / dist) : new Vec2(1f, 0f);
            sim.Physics.ApplyImpulse(units, i, dir, force * falloff, upwardForce * falloff);
        }
    }

    /// <summary>Tick all charging units. Called from the main simulation update
    /// before UpdateMovement so velocity writes take effect this frame.</summary>
    public static void TickAll(Simulation sim, float dt)
    {
        var units = sim.UnitsMut;
        for (int i = 0; i < units.Count; i++)
        {
            if (units[i].ChargePhase == 0) continue;
            TickCharge(dt, units, i, sim);
        }
    }
}
