using System;
using System.Collections.Generic;
using Necroking.Core;
using Necroking.Data;
using Necroking.Movement;
using Necroking.Render;

namespace Necroking.GameSystems;

/// <summary>
/// 2.5D impulse physics system. Units temporarily enter physics when hit by
/// explosions, cavalry charges, etc. During physics, the unit's position is
/// driven by velocity + gravity + drag instead of AI/ORCA.
///
/// Popcorn-style: exaggerated upward arcs, dramatic knockbacks.
/// Flying units can bowl into standing units (chain reactions).
/// </summary>
public class PhysicsSystem
{
    // --- Tuning ---
    public float Gravity = 15f;             // units/s² downward (higher = snappier arcs, less hang time)
                                             // Live-overridden from GeneralSettings.Gravity each tick.
    public float DefaultDrag = 2.0f;        // XY velocity decay
    public float UpwardBias = 1.2f;         // multiplier on Z impulse
    public float MinLaunchZ = 4.0f;         // minimum upward velocity for visible arc
    public float KnockdownTimeMin = 1.0f;
    public float KnockdownTimeMax = 2.0f;
    public float ResistanceMultiplier = 2.0f; // size * this = resistance threshold

    private struct PhysicsBody
    {
        public int UnitIdx;
        public Vec2 VelocityXY;
        public float VelocityZ;
        public float Drag;
        public bool Active;
        public float LaunchGrace;  // Seconds before collision checks start (prevents instant re-collision)
        public float GravityMul;   // Per-body gravity scale; 1.0 = full Gravity, 0.3 = floaty
                                   // (trample uses ~0.3 to give visible arcs at the launch
                                   // velocities the billiards math produces).
    }

    private readonly List<PhysicsBody> _bodies = new();
    private static readonly Random _rng = new();
    private Data.Registries.BuffDef? _knockdownBuff;

    private const string KnockdownBuffID = "buff_knockdown";

    /// <summary>Call once after game data is loaded to resolve the knockdown buff def.</summary>
    public void Init(Data.Registries.BuffRegistry buffs)
    {
        _knockdownBuff = buffs.Get(KnockdownBuffID);
        if (_knockdownBuff == null)
            DebugLog.Log("physics", $"WARNING: knockdown buff '{KnockdownBuffID}' not found in registry");
    }

    /// <summary>
    /// Try to launch a unit into physics. Returns false if the unit resists.
    /// </summary>
    /// <param name="units">Unit array</param>
    /// <param name="unitIdx">Unit to launch</param>
    /// <param name="direction">XY direction of the impulse (will be normalized)</param>
    /// <param name="force">Impulse magnitude</param>
    /// <param name="upwardForce">Additional upward (Z) force. Gets UpwardBias multiplied.</param>
    /// <param name="bypassResistance">If true, skip resistance check (used for chain collisions).</param>
    /// <param name="bypassMinZ">If true, skip the MinLaunchZ × UpwardBias floor on Z velocity.
    /// Trample uses this so its angle-based launch math (30°-60° off horizontal) isn't
    /// overridden by the dramatic-arc floor designed for spell explosions.</param>
    /// <param name="gravityMul">Scale on gravity for THIS body only (1.0 = default).
    /// Trample uses ~0.3 to keep launched units airborne long enough that the
    /// horizontal velocity actually visibly translates them — at full gravity the
    /// flight is so brief (~0.1 s for a side-glance vZ) that the target barely
    /// scoots before landing.</param>
    /// <param name="dragMul">Scale on XY drag for THIS body only (1.0 = default).</param>
    public bool ApplyImpulse(UnitArrays units, int unitIdx, Vec2 direction, float force,
        float upwardForce = 0f, bool bypassResistance = false, bool bypassMinZ = false,
        float gravityMul = 1.0f, float dragMul = 1.0f)
    {
        if (unitIdx < 0 || unitIdx >= units.Count || !units[unitIdx].Alive) return false;
        if (units[unitIdx].InPhysics) return false; // already flying
        // Charging units (Trample) are immune to ALL impulses — direct radial
        // blasts, chain collisions from flying victims, anything. The charge is a
        // committed motion; a stray piece of physics debris shouldn't cancel it.
        // Phase 1 (active charge) and 3 (follow-through) are both committed motion.
        if (units[unitIdx].ChargePhase == 1 || units[unitIdx].ChargePhase == 3) return false;

        // Resistance check: large units resist weak impulses
        float resistance = bypassResistance ? 0f : units[unitIdx].Size * ResistanceMultiplier;
        float effectiveForce = force - resistance;
        if (effectiveForce <= 0f) return false; // resisted

        // Normalize direction
        float dirLen = direction.Length();
        if (dirLen > 0.01f)
            direction *= 1f / dirLen;
        else
            direction = new Vec2(1f, 0f);

        // Calculate launch velocity
        Vec2 launchXY = direction * effectiveForce;
        float rawZ = (upwardForce > 0f ? upwardForce : effectiveForce * 0.5f) * UpwardBias;
        float launchZ = bypassMinZ ? rawZ : MathF.Max(MinLaunchZ, rawZ);

        // Enter physics state
        units[unitIdx].InPhysics = true;
        units[unitIdx].PreferredVel = Vec2.Zero;

        // Clear AI/combat state
        units[unitIdx].Routine = 0;
        units[unitIdx].Subroutine = 0;
        units[unitIdx].SubroutineTimer = 0f;
        units[unitIdx].EngagedTarget = CombatTarget.None;
        units[unitIdx].Target = CombatTarget.None;

        // Set fall animation (priority 3 = forced, can't be interrupted)
        AnimResolver.SetOverride(units[unitIdx], AnimRequest.Forced(AnimState.Fall));

        _bodies.Add(new PhysicsBody
        {
            UnitIdx = unitIdx,
            VelocityXY = launchXY,
            VelocityZ = launchZ,
            Drag = DefaultDrag * dragMul,
            Active = true,
            LaunchGrace = 0.05f,  // 50ms before collision checks start (prevents same-frame re-collision)
            GravityMul = gravityMul,
        });

        DebugLog.Log("physics", $"[Launch] unit#{unitIdx} force={force:F1} effective={effectiveForce:F1} " +
            $"velXY=({launchXY.X:F1},{launchXY.Y:F1}) velZ={launchZ:F1} size={units[unitIdx].Size}");

        return true;
    }

    /// <summary>
    /// Apply a radial impulse (explosion) to all enemy units within radius.
    /// </summary>
    public int ApplyRadialImpulse(UnitArrays units, Vec2 center, float radius, float force,
        float upwardForce, Faction ownerFaction)
    {
        int launched = 0;
        for (int i = 0; i < units.Count; i++)
        {
            if (!units[i].Alive || units[i].InPhysics) continue;
            // Physics knockback hits everyone (friendly fire)

            Vec2 delta = units[i].Position - center;
            float dist = delta.Length();
            if (dist > radius) continue;

            // Force falls off with distance (linear)
            float falloff = 1f - (dist / radius);
            float actualForce = force * falloff;
            Vec2 dir = dist > 0.01f ? delta * (1f / dist) : new Vec2(1f, 0f);

            if (ApplyImpulse(units, i, dir, actualForce, upwardForce * falloff))
                launched++;
        }
        return launched;
    }

    /// <summary>
    /// Update all active physics bodies. Call from Simulation.Tick between
    /// UpdateMovement and UpdateCombat.
    /// </summary>
    public void Update(float dt, UnitArrays units)
    {
        if (_bodies.Count > 0 && dt > 0.05f)
            DebugLog.Log("physics", $"[FRAME] dt={dt * 1000:F0}ms bodies={_bodies.Count} — SLOW FRAME");

        for (int bi = _bodies.Count - 1; bi >= 0; bi--)
        {
            var body = _bodies[bi];
            if (!body.Active || body.UnitIdx >= units.Count)
            {
                _bodies.RemoveAt(bi);
                continue;
            }
            // Dead unit: KEEP the body so RemoveDeadUnits (later in the same tick)
            // can still read its velocity and pass it to the spawning corpse.
            // Without this, a unit killed by an impulse-then-damage caller
            // (trample, which runs BEFORE the physics tick) loses its velocity
            // before the corpse is created. Skip integration; the body will be
            // collected via TryRemoveBody from RemoveDeadUnits.
            if (!units[body.UnitIdx].Alive)
            {
                continue;
            }

            int idx = body.UnitIdx;

            // Integrate velocity
            units[idx].Position += body.VelocityXY * dt;
            units[idx].Z += body.VelocityZ * dt;

            // Gravity (per-body multiplier, default 1.0; trample bodies use ~0.3 for
            // visible arc time at the velocities the billiards math produces).
            float bodyGravity = body.GravityMul > 0f ? Gravity * body.GravityMul : Gravity;
            body.VelocityZ -= bodyGravity * dt;

            // XY drag (air resistance)
            float dragFactor = 1f - body.Drag * dt;
            if (dragFactor < 0f) dragFactor = 0f;
            body.VelocityXY *= dragFactor;

            // Also set unit Velocity for animation/rendering
            units[idx].Velocity = body.VelocityXY;

            // Tick launch grace period
            if (body.LaunchGrace > 0f)
                body.LaunchGrace -= dt;

            // --- Collision: flying unit hits standing unit ---
            // Only check after launch grace expires and near ground level.
            if (body.LaunchGrace <= 0f && units[idx].Z < 1.5f)
            {
                CheckUnitCollisions(ref body, units, dt);
            }

            // --- Wall collision ---
            // TODO: Check wall grid walkability at next position
            // If blocked: body.VelocityXY = Vec2.Zero, unit drops straight down
            // TODO: Apply wall impact damage

            // --- Landing ---
            if (units[idx].Z <= 0f)
            {
                Land(units, idx);
                _bodies.RemoveAt(bi);
                continue;
            }

            _bodies[bi] = body; // write back (struct)
        }
    }

    private void CheckUnitCollisions(ref PhysicsBody body, UnitArrays units, float dt)
    {
        int flyerIdx = body.UnitIdx;
        float flyerSpeed = body.VelocityXY.Length();
        if (flyerSpeed < MinTransferSpeed) return; // too slow to register

        if (units[flyerIdx].Z > 2f) return; // too high to hit anyone

        float flyerMass = MassOf(units[flyerIdx]);

        for (int i = 0; i < units.Count; i++)
        {
            if (i == flyerIdx || !units[i].Alive || units[i].InPhysics) continue;
            // Chargers (Trample) phase through smaller units by design.
            if (units[i].ChargePhase == 1 || units[i].ChargePhase == 3) continue;

            // Combined-radius collision: flyer's body + standing's body, no constant
            // CollisionRadius. A flying GreatBoar (radius 0.85) reaches farther than
            // a flying skeleton (radius 0.5) — the chain trigger now scales with
            // physical size, not a one-size-fits-all 0.8u.
            float dist = (units[i].Position - units[flyerIdx].Position).Length();
            if (dist > units[flyerIdx].Radius + units[i].Radius) continue;

            // --- Inelastic ("clay") momentum transfer ---
            // Mass = size^3 (Skeleton 8, Boar 27, GreatBoar 64, Knight 64, etc.).
            // Contact normal points from flyer to standing. Only the velocity
            // component along the normal participates in the collision; the flyer's
            // tangential velocity is preserved (it glances off if hit was off-axis).
            //
            // Combined velocity along the normal: vn' = m1 * vn / (m1 + m2)
            // Both bodies move at vn' along the normal after the collision.
            // Tiny flyer + huge standing → vn' ≈ 0 → standing barely moves, flyer
            // bleeds nearly all normal momentum. Equal masses → vn' = vn/2 → both
            // share the velocity. This naturally caps chain depth: each hop sheds
            // momentum proportional to mass ratio, and below MinTransferSpeed the
            // chain stops entirely.
            Vec2 normal = units[i].Position - units[flyerIdx].Position;
            float normalLen = normal.Length();
            if (normalLen < 0.001f)
            {
                // Co-located: fall back to flyer's motion direction.
                normal = body.VelocityXY * (1f / flyerSpeed);
            }
            else
            {
                normal *= 1f / normalLen;
            }

            float vn = body.VelocityXY.X * normal.X + body.VelocityXY.Y * normal.Y;
            if (vn <= 0f) continue; // moving away or parallel — not a real collision

            float standingMass = MassOf(units[i]);
            float vnCombined = flyerMass * vn / (flyerMass + standingMass);
            if (vnCombined < MinTransferSpeed) continue; // negligible — skip

            // Launch standing unit along the contact normal at the combined velocity.
            // bypassResistance: the mass math already accounts for "how much got
            // through" — we don't want to double-deduct via size resistance.
            ApplyImpulse(units, i, normal, vnCombined, vnCombined * 0.3f, bypassResistance: true);

            // Flyer's normal-component velocity drops to vnCombined; tangential
            // component preserved. Compute flyer's new XY velocity:
            //   vNew = vXY - (vn - vnCombined) * normal
            float deltaVn = vn - vnCombined;
            body.VelocityXY -= normal * deltaVn;

            DebugLog.Log("physics",
                $"[Collision] flyer#{flyerIdx} (mass={flyerMass:F0}, vn={vn:F2}) → " +
                $"unit#{i} (mass={standingMass:F0}); vCombined={vnCombined:F2}");
        }
    }

    /// <summary>Mass model: size cubed. Skel(2)=8, Boar(3)=27, GreatBoar(4)=64.
    /// Used by inelastic-collision math in CheckUnitCollisions.</summary>
    private static float MassOf(in Unit u)
    {
        int s = u.Size;
        return s * s * s;
    }

    /// <summary>Below this normal-component speed, neither launch nor chain.
    /// Applied at the start of CheckUnitCollisions (skip whole call if flyer
    /// itself is below) and after the inelastic math (skip individual targets
    /// if their share is negligible). Also caps chain depth — each hop's
    /// vCombined shrinks, eventually falling below this floor.</summary>
    public float MinTransferSpeed = 1.0f;

    private void Land(UnitArrays units, int idx)
    {
        units[idx].Z = 0f;
        units[idx].InPhysics = false;
        units[idx].Velocity = Vec2.Zero;
        units[idx].PreferredVel = Vec2.Zero;

        // Apply knockdown buff — incap state, animation, and recovery all handled by buff system
        if (_knockdownBuff != null)
            BuffSystem.ApplyBuff(units, idx, _knockdownBuff);

        DebugLog.Log("physics", $"[Land] unit#{idx} pos=({units[idx].Position.X:F1},{units[idx].Position.Y:F1})");

        // TODO: Apply landing impact damage based on velocity at impact
    }

    public int ActiveCount => _bodies.Count;

    /// <summary>
    /// Get physics velocity for a unit (if it has an active body).
    /// Used to transfer momentum to corpses when units die mid-flight.
    /// </summary>
    public bool TryGetBodyVelocity(int unitIdx, out Vec2 velocityXY, out float velocityZ)
    {
        for (int i = 0; i < _bodies.Count; i++)
        {
            if (_bodies[i].Active && _bodies[i].UnitIdx == unitIdx)
            {
                velocityXY = _bodies[i].VelocityXY;
                velocityZ = _bodies[i].VelocityZ;
                return true;
            }
        }
        velocityXY = Vec2.Zero;
        velocityZ = 0f;
        return false;
    }

    /// <summary>Remove the physics body for a unit. Called by RemoveDeadUnits
    /// after the corpse has captured the velocity, so we don't leak bodies
    /// pointing to recycled unit indices.</summary>
    public void RemoveBody(int unitIdx)
    {
        for (int i = _bodies.Count - 1; i >= 0; i--)
        {
            if (_bodies[i].UnitIdx == unitIdx)
            {
                _bodies.RemoveAt(i);
                return;
            }
        }
    }

    /// <summary>Read the per-body physics tuning (gravity/drag scales) so a corpse
    /// can inherit the same arc when its unit dies mid-flight. Returns 1.0/1.0
    /// (default tuning) when no body exists for the unit.</summary>
    public bool TryGetBodyTuning(int unitIdx, out float gravityMul, out float dragMul)
    {
        for (int i = 0; i < _bodies.Count; i++)
        {
            if (_bodies[i].Active && _bodies[i].UnitIdx == unitIdx)
            {
                gravityMul = _bodies[i].GravityMul > 0f ? _bodies[i].GravityMul : 1f;
                dragMul = _bodies[i].Drag / DefaultDrag;
                return true;
            }
        }
        gravityMul = 1f;
        dragMul = 1f;
        return false;
    }

    public void Clear() => _bodies.Clear();
}
