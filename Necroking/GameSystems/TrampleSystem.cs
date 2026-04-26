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
/// at CombatSpeed × (1 + SpeedBonus). While in flight it phases through smaller
/// units; each one swept by the body takes a melee resolution + a billiards-style
/// knockback (50%-125% of charge speed depending on hit angle, launched along the
/// contact normal at 30°-60° off horizontal). Misses can dodge to a free tile
/// within 1u — if no safe tile exists, the dodge fails and the hit lands.
///
/// Same-or-larger units in front block the charge: the trampler stops, takes a
/// brief stagger, the blocker absorbs a clay-momentum bump, and the charge ends.
///
/// ChargePhase state machine:
///   0 = None
///   1 = Charging      — homing toward ChargeTargetId's current position
///   3 = FollowThrough — straight-line drive past impact point
///   2 = Recovery      — on-ground lockout while recovery timer counts down
/// </summary>
public static class TrampleSystem
{
    private const float RecoveryDuration = 0.6f;

    /// <summary>How far a dodging unit hops, in world units. Tiles within this
    /// radius are candidate safe spots when a trample swing misses.</summary>
    private const float DodgeDistance = 1.0f;

    /// <summary>Total length of a dodge hop (animated position interpolation).
    /// The Dodge anim is sped up to fit inside this window so the visual matches.</summary>
    private const float DodgeDurationSec = 0.4f;

    /// <summary>Minimum normal-component dot for a contact to count as a hit.
    /// Below this (the victim is roughly behind the trampler), no hit fires.
    /// Slightly above 0 so co-moving alongside without forward collision is OK.</summary>
    private const float MinContactDot = 0.05f;

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
        units[idx].ChargeFollowRemaining = 0f;
        units[idx].ChargeFollowDir = Vec2.Zero;
        if (units[idx].TrampledIds == null) units[idx].TrampledIds = new HashSet<uint>();
        else units[idx].TrampledIds!.Clear();

        Vec2 toTarget = units[targetIdx].Position - units[idx].Position;
        if (toTarget.LengthSq() > 0.0001f)
            units[idx].FacingAngle = MathF.Atan2(toTarget.Y, toTarget.X) * (180f / MathF.PI);

        units[idx].PreferredVel = Vec2.Zero;
        units[idx].ActionLabel = string.IsNullOrEmpty(weapon.Name) ? "Charge" : weapon.Name;
        units[idx].ActionLabelTimer = 4f;

        DebugLog.Log("trample",
            $"[BeginCharge] unit#{idx} → target id={units[targetIdx].Id} " +
            $"dist={toTarget.Length():F2} maxChase={weapon.TrampleMaxChaseDistance:F1} " +
            $"speedBonus={weapon.TrampleSpeedBonus:P0} selfR={units[idx].Radius:F2} " +
            $"tgtR={units[targetIdx].Radius:F2}");
        return true;
    }

    /// <summary>Per-frame update for a single charging unit. Returns true if still
    /// charging; the caller may then skip other AI/movement paths.</summary>
    public static bool TickCharge(float dt, UnitArrays units, int idx, Simulation sim)
    {
        if (idx < 0 || idx >= units.Count) return false;
        if (units[idx].ChargePhase == 0) return false;

        if (units[idx].ChargePhase == 2)
        {
            units[idx].ChargeRecoveryTimer -= dt;
            if (units[idx].ChargeRecoveryTimer <= 0f)
            {
                units[idx].ChargePhase = 0;
                units[idx].ChargeTargetId = GameConstants.InvalidUnit;
                units[idx].ChargeWeaponIdx = -1;
                units[idx].ChargeTraveled = 0f;
                units[idx].ChargeFollowRemaining = 0f;
                units[idx].TrampledIds?.Clear();
            }
            return true;
        }

        if (!units[idx].Alive) { EndCharge(units, idx, sim, impactAtPos: null); return false; }

        int weaponIdx = units[idx].ChargeWeaponIdx;
        if (weaponIdx < 0 || weaponIdx >= units[idx].Stats.MeleeWeapons.Count)
        { EndCharge(units, idx, sim, impactAtPos: null); return false; }
        var weapon = units[idx].Stats.MeleeWeapons[weaponIdx];

        units[idx].ActionLabel = string.IsNullOrEmpty(weapon.Name) ? "Charge" : weapon.Name;
        units[idx].ActionLabelTimer = 0.5f;

        float chargeSpeed = units[idx].Stats.CombatSpeed * (1f + weapon.TrampleSpeedBonus);

        // Phase 3 — follow-through. Locked direction, no homing, no impact checks.
        if (units[idx].ChargePhase == 3)
        {
            return TickFollowThrough(dt, sim, idx, weaponIdx, weapon, chargeSpeed);
        }

        // Phase 1 — active charge.
        int targetIdx = UnitUtil.ResolveUnitIndex(units, units[idx].ChargeTargetId);
        if (targetIdx < 0 || !units[targetIdx].Alive)
        {
            DebugLog.Log("trample", $"[TickCharge] unit#{idx} target vanished mid-charge");
            EndCharge(units, idx, sim, impactAtPos: null);
            return true;
        }

        Vec2 toTarget = units[targetIdx].Position - units[idx].Position;
        float distToTarget = toTarget.Length();

        // Impact trigger: leading edges meet.
        float collisionTrigger = units[idx].Radius + units[targetIdx].Radius;
        float effectiveTrigger = MathF.Max(collisionTrigger, weapon.TrampleImpactRange);

        if (distToTarget <= effectiveTrigger)
        {
            Vec2 chargeDir = distToTarget > 0.0001f
                ? new Vec2(toTarget.X / distToTarget, toTarget.Y / distToTarget)
                : DirFromFacing(units[idx].FacingAngle);

            DebugLog.Log("trample",
                $"[TickCharge] unit#{idx} IMPACT target id={units[targetIdx].Id} " +
                $"dist={distToTarget:F2} trigger={effectiveTrigger:F2}");

            // Resolve the primary hit through the unified billiards-style helper.
            // Marks the target as trampled so the same-frame ScanAndTrample below
            // doesn't double-hit it.
            TryTrampleHit(sim, idx, targetIdx, weaponIdx, chargeDir, chargeSpeed,
                isPrimary: true);

            // Enter follow-through and immediately tick one step (so the boar
            // visibly begins driving through the impact point on this same frame).
            float bodySum = units[idx].Radius + units[targetIdx].Radius;
            float followDist = effectiveTrigger + bodySum;
            EnterFollowThrough(units, idx, chargeDir, followDist);
            return TickFollowThrough(dt, sim, idx, weaponIdx, weapon, chargeSpeed);
        }

        // Chase termination — give up only if target left engagement range.
        if (distToTarget > weapon.TrampleMaxRange)
        {
            DebugLog.Log("trample",
                $"[TickCharge] unit#{idx} target escaped (dist={distToTarget:F2} > " +
                $"maxRange={weapon.TrampleMaxRange:F1}); ending");
            EndCharge(units, idx, sim, impactAtPos: null);
            return true;
        }
        if (units[idx].ChargeTraveled >= weapon.TrampleMaxChaseDistance * 3f)
        {
            DebugLog.Log("trample", $"[TickCharge] unit#{idx} hit absolute travel cap");
            EndCharge(units, idx, sim, impactAtPos: null);
            return true;
        }

        // Drive forward.
        float invDist = distToTarget > 0.0001f ? 1f / distToTarget : 0f;
        Vec2 dir = new Vec2(toTarget.X * invDist, toTarget.Y * invDist);
        float stepLen = chargeSpeed * dt;
        if (stepLen > distToTarget) stepLen = distToTarget;

        units[idx].Velocity = new Vec2(dir.X * chargeSpeed, dir.Y * chargeSpeed);
        units[idx].FacingAngle = MathF.Atan2(dir.Y, dir.X) * (180f / MathF.PI);

        ScanAndTrample(sim, idx, weaponIdx, weapon, dir, chargeSpeed);

        // Same-or-larger blocker → stop charge, stagger trampler.
        if (TryCheckBlockingUnit(sim, idx, weapon.TrampleRadius, out int blockerIdx))
        {
            DebugLog.Log("trample",
                $"[TickCharge] unit#{idx} hit blocker id={units[blockerIdx].Id} " +
                $"size={units[blockerIdx].Size} (self size={units[idx].Size}); slamming");
            // EndCharge before HandleBlockerImpact: trampler must leave phase 1/3
            // for ApplyImpulse to launch them (chargers are impulse-immune).
            EndCharge(units, idx, sim, impactAtPos: null);
            HandleBlockerImpact(sim, idx, blockerIdx, dir, chargeSpeed);
            return true;
        }

        units[idx].ChargeTraveled += stepLen;
        return true;
    }

    /// <summary>Phase 3 — straight-line drive past the impact point.</summary>
    private static bool TickFollowThrough(float dt, Simulation sim, int idx,
        int weaponIdx, WeaponStats weapon, float chargeSpeed)
    {
        var units = sim.UnitsMut;
        Vec2 dir = units[idx].ChargeFollowDir;
        if (dir.LengthSq() < 0.0001f)
        {
            EndCharge(units, idx, sim, impactAtPos: null);
            return true;
        }

        float stepLen = chargeSpeed * dt;
        if (stepLen > units[idx].ChargeFollowRemaining)
            stepLen = units[idx].ChargeFollowRemaining;

        units[idx].Velocity = new Vec2(dir.X * chargeSpeed, dir.Y * chargeSpeed);
        units[idx].FacingAngle = MathF.Atan2(dir.Y, dir.X) * (180f / MathF.PI);

        ScanAndTrample(sim, idx, weaponIdx, weapon, dir, chargeSpeed);

        if (TryCheckBlockingUnit(sim, idx, weapon.TrampleRadius, out int blockerIdx))
        {
            DebugLog.Log("trample",
                $"[FollowThrough] unit#{idx} hit blocker id={units[blockerIdx].Id}");
            // EndCharge before HandleBlockerImpact: trampler must leave phase 1/3
            // for ApplyImpulse to launch them (chargers are impulse-immune).
            EndCharge(units, idx, sim, impactAtPos: null);
            HandleBlockerImpact(sim, idx, blockerIdx, dir, chargeSpeed);
            return true;
        }

        units[idx].ChargeFollowRemaining -= stepLen;
        if (units[idx].ChargeFollowRemaining <= 0f)
        {
            EndCharge(units, idx, sim, impactAtPos: null);
            return true;
        }

        return true;
    }

    /// <summary>Unified billiards-style hit resolution. Computes the contact dot
    /// (head-on vs glancing), rolls attack vs defense, applies dodge-or-knockback.
    ///
    ///   hit:  knockback at chargeSpeed × (0.5 + 0.75 × dot), launched along contact
    ///         normal at angle (30° + 30° × dot) off horizontal. Head-on hits arc
    ///         high and slow horizontally; side hits scoot lower and farther.
    ///   miss: try TryDodge — if a free tile within DodgeDistance is found, the
    ///         defender hops there with a 0.4 s anim and no knockback. Otherwise
    ///         the dodge fails and the hit lands anyway (force-hit damage roll).
    ///
    /// TrampledIds dedupes per-charge: each victim is hit at most once per charge.
    /// Returns true if a real interaction happened (hit or dodge).</summary>
    private static bool TryTrampleHit(Simulation sim, int attackerIdx, int defenderIdx,
        int weaponIdx, Vec2 trampleDir, float chargeSpeed, bool isPrimary)
    {
        var units = sim.UnitsMut;
        if (!units[defenderIdx].Alive) return false;

        // Compute contact normal + head-on-ness.
        Vec2 toVic = units[defenderIdx].Position - units[attackerIdx].Position;
        float toVicLen = toVic.Length();
        Vec2 contactNormal = toVicLen > 0.001f
            ? new Vec2(toVic.X / toVicLen, toVic.Y / toVicLen)
            : trampleDir;
        float dot = trampleDir.X * contactNormal.X + trampleDir.Y * contactNormal.Y;
        if (dot < MinContactDot) return false; // victim is behind the trampler

        // Per-charge dedup before the dice roll — a "hit" or "dodge" both consume
        // the slot, so a victim that successfully dodged this charge can't be
        // re-trampled later in the same charge.
        if (units[attackerIdx].TrampledIds == null)
            units[attackerIdx].TrampledIds = new HashSet<uint>();
        if (units[attackerIdx].TrampledIds!.Contains(units[defenderIdx].Id)) return false;
        units[attackerIdx].TrampledIds!.Add(units[defenderIdx].Id);

        // Step 1: peek at the dice roll. Sets LastMeleeAttackHit but applies no
        // side effects — no damage, no log, no dodge anim. We need to know
        // hit/miss BEFORE applying physics so corpses can inherit knockback
        // velocity (mirrors the spell-knockback pattern in Simulation.cs:443).
        sim.ResolveMeleeAttackExternal(attackerIdx, defenderIdx, weaponIdx,
            suppressDodgeAnim: true, peekOnly: true);
        bool hit = sim.LastMeleeAttackHit;

        if (!hit)
        {
            // Defender won the defense check — try to physically dodge.
            if (TryDodge(sim, defenderIdx, attackerIdx))
            {
                DebugLog.Log("trample",
                    $"[TryTrampleHit] defender id={units[defenderIdx].Id} DODGED " +
                    $"(primary={isPrimary})");
                return true; // dodge succeeded — no knockback, no damage applied
            }
            // No safe tile → dodge fails → fall through to forced-hit path below.
            DebugLog.Log("trample",
                $"[TryTrampleHit] defender id={units[defenderIdx].Id} dodge FAILED " +
                $"(no safe tile); hit forced");
        }

        // Step 2: apply the knockback impulse FIRST. The defender enters physics.
        // If the upcoming damage call kills them, the corpse will inherit the
        // body's velocity (RemoveDeadUnits transfers it on death).
        ApplyBilliardsKnockback(sim, defenderIdx, contactNormal, dot, chargeSpeed,
            bypassResistance: isPrimary);

        // Step 3: apply the damage. forceHit: true skips the dice (we already
        // committed to hit in Step 1) but runs all the normal hit side effects:
        // damage, combat log, fatigue, knockdown bonus, weapon coats.
        sim.ResolveMeleeAttackExternal(attackerIdx, defenderIdx, weaponIdx,
            suppressDodgeAnim: true, forceHit: true);
        return true;
    }

    /// <summary>Apply the billiards-style knockback. Velocity magnitude is
    /// chargeSpeed × (0.5 + 0.75 × dot). Direction is contact normal (radial-out
    /// from trampler). Launch angle is (30° + 30° × dot) off horizontal — head-on
    /// hits arc high, side hits scoot lower. bypassMinZ ensures the angle math
    /// isn't overridden by the spell-explosion floor on Z velocity.</summary>
    private static void ApplyBilliardsKnockback(Simulation sim, int defenderIdx,
        Vec2 contactNormal, float dot, float chargeSpeed, bool bypassResistance)
    {
        var units = sim.UnitsMut;
        if (!units[defenderIdx].Alive) return;

        float clampedDot = MathF.Min(1f, MathF.Max(0f, dot));
        float forceMult = 0.5f + 0.75f * clampedDot;          // 0.50 → 1.25
        float launchSpeed = chargeSpeed * forceMult;

        float launchAngleDeg = 30f + 30f * clampedDot;        // 30° → 60°
        float angleRad = launchAngleDeg * (MathF.PI / 180f);
        float vXY = launchSpeed * MathF.Cos(angleRad);
        float vZ  = launchSpeed * MathF.Sin(angleRad);

        // Trample uses default gravity (set globally in GeneralSettings) and a
        // halved drag for slightly longer horizontal travel. The visible-arc
        // tuning lives in the global Gravity setting now — see GeneralSettings.
        sim.Physics.ApplyImpulse(units, defenderIdx, contactNormal,
            vXY, upwardForce: vZ,
            bypassResistance: bypassResistance,
            bypassMinZ: true,
            dragMul: 0.5f);

        DebugLog.Log("trample",
            $"[Knockback] def#{defenderIdx} dot={clampedDot:F2} mult={forceMult:F2} " +
            $"speed={launchSpeed:F2} angle={launchAngleDeg:F0}° vXY={vXY:F2} vZ={vZ:F2}");
    }

    /// <summary>Look for a 1u-radius safe spot the defender can hop to that's away
    /// from the trampler. Tries 8 cardinal+diagonal directions, scored by:
    ///   1. directional preference: dot product with (defender - trampler) — must be > 0
    ///      (further from trampler than current position)
    ///   2. clearance: nearest other unit's body must not overlap the spot, with a
    ///      "full clear" preferred and a "half-radius overlap" tolerated as fallback
    /// Returns true if a hop was started. Leaves the unit's DodgeTimer + DodgeEnd
    /// fields populated so the main tick can interpolate the position.</summary>
    private static bool TryDodge(Simulation sim, int defenderIdx, int attackerIdx)
    {
        var units = sim.UnitsMut;
        if (!units[defenderIdx].Alive) return false;
        if (units[defenderIdx].DodgeTimer > 0f) return false; // already dodging
        if (units[defenderIdx].Incap.Active) return false;
        if (units[defenderIdx].JumpPhase != 0) return false;
        if (units[defenderIdx].InPhysics) return false;
        if (units[defenderIdx].ChargePhase != 0) return false;

        Vec2 defPos = units[defenderIdx].Position;
        Vec2 atkPos = units[attackerIdx].Position;
        Vec2 awayFromAtk = defPos - atkPos;
        float awayLen = awayFromAtk.Length();
        if (awayLen > 0.001f) awayFromAtk *= 1f / awayLen;
        else awayFromAtk = new Vec2(1f, 0f);

        float defR = units[defenderIdx].Radius;
        float fullClear = defR;        // dist to other unit's body required for "clean" spot
        float minClear = defR * 0.5f;  // fallback: at least half-clearance allowed

        // 8 cardinal+diagonal candidates at DodgeDistance from current pos.
        Span<Vec2> dirs = stackalloc Vec2[8];
        const float invSqrt2 = 0.70710678f;
        dirs[0] = new Vec2(1f, 0f);
        dirs[1] = new Vec2(invSqrt2, invSqrt2);
        dirs[2] = new Vec2(0f, 1f);
        dirs[3] = new Vec2(-invSqrt2, invSqrt2);
        dirs[4] = new Vec2(-1f, 0f);
        dirs[5] = new Vec2(-invSqrt2, -invSqrt2);
        dirs[6] = new Vec2(0f, -1f);
        dirs[7] = new Vec2(invSqrt2, -invSqrt2);

        int bestIdx = -1;
        float bestScore = float.NegativeInfinity;
        Vec2 bestEnd = Vec2.Zero;
        bool bestIsClean = false;

        for (int d = 0; d < 8; d++)
        {
            Vec2 candidate = new Vec2(
                defPos.X + dirs[d].X * DodgeDistance,
                defPos.Y + dirs[d].Y * DodgeDistance);

            // Direction preference: must be away from attacker.
            float awayScore = dirs[d].X * awayFromAtk.X + dirs[d].Y * awayFromAtk.Y;
            if (awayScore <= 0f) continue;

            // Find the nearest unit body to this candidate spot.
            float nearestOverlap = float.PositiveInfinity;
            for (int j = 0; j < units.Count; j++)
            {
                if (j == defenderIdx || !units[j].Alive) continue;
                Vec2 dp = units[j].Position - candidate;
                float d2 = dp.X * dp.X + dp.Y * dp.Y;
                float minDist = defR + units[j].Radius;
                if (d2 >= minDist * minDist) continue; // no overlap with this neighbor
                float dDist = MathF.Sqrt(d2);
                float overlapDeficit = minDist - dDist; // how far short of full clearance
                if (overlapDeficit < nearestOverlap) nearestOverlap = overlapDeficit;
            }
            // overlapDeficit ranges: 0 = full clearance, defR = standing on top of someone.
            // Reject if exceeds minClear (more than half body overlap).
            bool isClean = nearestOverlap == float.PositiveInfinity || nearestOverlap <= 0.001f;
            float overlap = nearestOverlap == float.PositiveInfinity ? 0f : nearestOverlap;
            if (overlap > defR - minClear) continue; // can't even fit at half-clearance

            // Score: prefer cleaner spots, then prefer being more "away" from attacker.
            float score = awayScore - overlap * 4f + (isClean ? 1f : 0f);
            if (score > bestScore)
            {
                bestScore = score;
                bestIdx = d;
                bestEnd = candidate;
                bestIsClean = isClean;
            }
        }

        if (bestIdx < 0) return false;

        // Commit the dodge.
        units[defenderIdx].DodgeStartPos = defPos;
        units[defenderIdx].DodgeEndPos = bestEnd;
        units[defenderIdx].DodgeTimer = DodgeDurationSec;
        units[defenderIdx].DodgeDuration = DodgeDurationSec;
        units[defenderIdx].Velocity = Vec2.Zero;
        units[defenderIdx].PreferredVel = Vec2.Zero;
        units[defenderIdx].EngagedTarget = CombatTarget.None;
        units[defenderIdx].Target = CombatTarget.None;

        // One-shot Dodge anim. Duration holds the override slot for 0.4s — the
        // anim plays at natural rate; if it's shorter it ends early, if longer
        // it gets cut at 0.4s when the slot expires. Priority=2 (Combat) so it
        // overrides idle/walk but yields to Forced (e.g. Fall — won't happen
        // here since we skipped the knockback).
        AnimResolver.SetOverride(units[defenderIdx], new AnimRequest
        {
            State = AnimState.Dodge,
            Priority = 2,
            Interrupt = true,
            Kind = OverrideKind.OneShot,
            Duration = DodgeDurationSec,
            PlaybackSpeed = 1f,
        });

        DebugLog.Log("trample",
            $"[Dodge] unit#{defenderIdx} hopping {(bestIsClean ? "clean" : "tight")} " +
            $"to ({bestEnd.X:F2},{bestEnd.Y:F2}) score={bestScore:F2}");
        return true;
    }

    /// <summary>Trampler ran into a unit it cannot trample (size ≥ self). The
    /// charge stops, the trampler is staggered, the blocker absorbs a small
    /// clay-momentum bump (mass = size³). No damage roll either way; this is a
    /// physical body check, not a melee swing.
    ///
    /// Caller MUST call EndCharge BEFORE this — ApplyImpulse on a unit in
    /// ChargePhase 1/3 is a no-op (chargers immune to impulses), so the
    /// trampler-stagger impulse below would silently fail otherwise.</summary>
    private static void HandleBlockerImpact(Simulation sim, int trampleIdx,
        int blockerIdx, Vec2 trampleDir, float chargeSpeed)
    {
        var units = sim.UnitsMut;
        if (!units[blockerIdx].Alive) return;

        // Clay-momentum push on the blocker — mass³ ratios mean a Knight (size 4 = 64)
        // hit by a Boar (size 3 = 27) only takes 27/(27+64) = 30% of the velocity.
        float m1 = units[trampleIdx].Size; m1 = m1 * m1 * m1;
        float m2 = units[blockerIdx].Size; m2 = m2 * m2 * m2;
        float vTransfer = chargeSpeed * (m1 / (m1 + m2));
        if (vTransfer > 0.5f)
        {
            Vec2 pushDir = units[blockerIdx].Position - units[trampleIdx].Position;
            float l = pushDir.Length();
            if (l > 0.001f) pushDir *= 1f / l;
            else pushDir = trampleDir;
            sim.Physics.ApplyImpulse(units, blockerIdx, pushDir,
                vTransfer, upwardForce: vTransfer * 0.3f, bypassMinZ: true);
        }

        // Trampler gets bounced back + knocked down. EndCharge has already moved
        // them to ChargePhase=2 (Recovery), so ApplyImpulse can fire.
        sim.Physics.ApplyImpulse(units, trampleIdx,
            new Vec2(-trampleDir.X, -trampleDir.Y),
            chargeSpeed * 0.5f,
            upwardForce: chargeSpeed * 0.3f,
            bypassResistance: true,
            bypassMinZ: true);
    }

    /// <summary>Scan quadtree for smaller hostile units within TrampleRadius of
    /// the charger and resolve a billiards-style hit on each.</summary>
    private static readonly List<uint> _scratch = new(16);
    private static void ScanAndTrample(Simulation sim, int idx, int weaponIdx,
        WeaponStats weapon, Vec2 trampleDir, float chargeSpeed)
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
            if (units[vi].Size >= selfSize) continue; // bigger blocks via TryCheckBlockingUnit
            if (units[idx].TrampledIds!.Contains(units[vi].Id)) continue;

            TryTrampleHit(sim, idx, vi, weaponIdx, trampleDir, chargeSpeed,
                isPrimary: false);
        }
    }

    /// <summary>Same-or-larger unit within probeRadius in the forward cone.</summary>
    private static bool TryCheckBlockingUnit(Simulation sim, int idx, float probeRadius, out int blockerIdx)
    {
        blockerIdx = -1;
        var units = sim.UnitsMut;
        _scratch.Clear();
        sim.Quadtree.QueryRadius(units[idx].Position, probeRadius, _scratch);

        int selfSize = units[idx].Size;
        Vec2 fwd = units[idx].ChargePhase == 3 && units[idx].ChargeFollowDir.LengthSq() > 0.0001f
            ? units[idx].ChargeFollowDir
            : DirFromFacing(units[idx].FacingAngle);
        const float MinDot = 0.5f; // ~60° half-angle cone
        uint chargeTgt = units[idx].ChargeTargetId;

        for (int k = 0; k < _scratch.Count; k++)
        {
            int oi = UnitUtil.ResolveUnitIndex(units, _scratch[k]);
            if (oi < 0 || oi == idx || !units[oi].Alive) continue;
            if (units[oi].Id == chargeTgt) continue; // primary handled by impact path
            if (units[oi].Size < selfSize) continue;
            Vec2 d = units[oi].Position - units[idx].Position;
            float dLen = d.Length();
            if (dLen < 0.0001f) { blockerIdx = oi; return true; }
            float dot = (d.X * fwd.X + d.Y * fwd.Y) / dLen;
            if (dot < MinDot) continue;
            blockerIdx = oi;
            return true;
        }
        return false;
    }

    private static void EnterFollowThrough(UnitArrays units, int idx, Vec2 dir, float distance)
    {
        units[idx].ChargePhase = 3;
        units[idx].ChargeFollowDir = dir;
        units[idx].ChargeFollowRemaining = distance;
        DebugLog.Log("trample",
            $"[FollowThrough] unit#{idx} begin dist={distance:F2} dir=({dir.X:F2},{dir.Y:F2})");
    }

    public static void EndCharge(UnitArrays units, int idx, Simulation sim, Vec2? impactAtPos)
    {
        if (idx < 0 || idx >= units.Count) return;
        if (units[idx].ChargePhase == 0) return;
        _ = impactAtPos; // legacy parameter, no longer used (blocker path slams directly)

        units[idx].ChargePhase = 2;
        units[idx].ChargeRecoveryTimer = RecoveryDuration;
        units[idx].ChargeFollowRemaining = 0f;
        units[idx].ChargeFollowDir = Vec2.Zero;
        units[idx].Velocity = Vec2.Zero;
        units[idx].PreferredVel = Vec2.Zero;
    }

    private static Vec2 DirFromFacing(float facingDeg)
    {
        float rad = facingDeg * (MathF.PI / 180f);
        return new Vec2(MathF.Cos(rad), MathF.Sin(rad));
    }

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
