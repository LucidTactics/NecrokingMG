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
    public float Gravity = 50f;             // units/s² downward (higher = snappier arcs, less hang time)
    public float DefaultDrag = 2.0f;        // XY velocity decay
    public float UpwardBias = 1.2f;         // multiplier on Z impulse
    public float MinLaunchZ = 4.0f;         // minimum upward velocity for visible arc
    public float CollisionRadius = 0.8f;    // flying→standing hit detection
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
    }

    private readonly List<PhysicsBody> _bodies = new();
    private static readonly Random _rng = new();

    /// <summary>
    /// Try to launch a unit into physics. Returns false if the unit resists.
    /// </summary>
    /// <param name="units">Unit array</param>
    /// <param name="unitIdx">Unit to launch</param>
    /// <param name="direction">XY direction of the impulse (will be normalized)</param>
    /// <param name="force">Impulse magnitude</param>
    /// <param name="upwardForce">Additional upward (Z) force. Gets UpwardBias multiplied.</param>
    /// <param name="bypassResistance">If true, skip resistance check (used for chain collisions).</param>
    public bool ApplyImpulse(UnitArrays units, int unitIdx, Vec2 direction, float force,
        float upwardForce = 0f, bool bypassResistance = false)
    {
        if (unitIdx < 0 || unitIdx >= units.Count || !units[unitIdx].Alive) return false;
        if (units[unitIdx].InPhysics) return false; // already flying

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
        float launchZ = MathF.Max(MinLaunchZ, (upwardForce > 0f ? upwardForce : effectiveForce * 0.5f) * UpwardBias);

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
        units[unitIdx].OverrideAnim = AnimRequest.Forced(AnimState.Fall);

        _bodies.Add(new PhysicsBody
        {
            UnitIdx = unitIdx,
            VelocityXY = launchXY,
            VelocityZ = launchZ,
            Drag = DefaultDrag,
            Active = true,
            LaunchGrace = 0.05f,  // 50ms before collision checks start (prevents same-frame re-collision)
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
            if (units[i].Faction == ownerFaction) continue;

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
            if (!body.Active || body.UnitIdx >= units.Count || !units[body.UnitIdx].Alive)
            {
                _bodies.RemoveAt(bi);
                continue;
            }

            int idx = body.UnitIdx;

            // Integrate velocity
            units[idx].Position += body.VelocityXY * dt;
            units[idx].Z += body.VelocityZ * dt;

            // Gravity
            body.VelocityZ -= Gravity * dt;

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
            // Only check after launch grace expires and near ground level
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
        if (flyerSpeed < 1f) return; // too slow to knock anyone

        for (int i = 0; i < units.Count; i++)
        {
            if (i == flyerIdx || !units[i].Alive || units[i].InPhysics) continue;

            float dist = (units[i].Position - units[flyerIdx].Position).Length();
            if (dist > CollisionRadius + units[i].Radius) continue;

            // Height check: flyer must be near ground to hit standing units
            if (units[flyerIdx].Z > 2f) continue;

            // Absorption based on target size
            float absorption = units[i].Size * ResistanceMultiplier;

            if (flyerSpeed > absorption)
            {
                // Remaining force splits between both
                float remaining = flyerSpeed - absorption;
                float targetShare = remaining * 0.6f;
                float flyerSlowdown = remaining * 0.4f;

                Vec2 pushDir = units[i].Position - units[flyerIdx].Position;
                float pushLen = pushDir.Length();
                if (pushLen > 0.01f) pushDir *= 1f / pushLen;
                else pushDir = body.VelocityXY.Length() > 0.01f
                    ? body.VelocityXY * (1f / body.VelocityXY.Length())
                    : new Vec2(1f, 0f);

                // Launch the standing unit (chain reaction — bypasses resistance)
                ApplyImpulse(units, i, pushDir, targetShare, targetShare * 0.3f, bypassResistance: true);

                // Slow down the flyer
                float newSpeed = flyerSpeed - flyerSlowdown - absorption;
                if (newSpeed < 0.5f) newSpeed = 0.5f;
                float scale = newSpeed / flyerSpeed;
                body.VelocityXY *= scale;

                DebugLog.Log("physics", $"[Collision] flyer#{flyerIdx} hit unit#{i} " +
                    $"speed={flyerSpeed:F1} absorption={absorption:F1} " +
                    $"targetLaunch={targetShare:F1} flyerSlowdown={flyerSlowdown:F1}");

                // TODO: Apply collision damage to both units
            }
            else
            {
                // Flyer too slow — just stagger the standing unit in place
                units[i].KnockdownTimer = (float)(_rng.NextDouble() *
                    (KnockdownTimeMax - KnockdownTimeMin) + KnockdownTimeMin);
                units[i].OverrideAnim = AnimRequest.Forced(AnimState.Knockdown);

                // Flyer stops
                body.VelocityXY = Vec2.Zero;

                DebugLog.Log("physics", $"[Collision] flyer#{flyerIdx} stopped by unit#{i} " +
                    $"(absorbed, speed={flyerSpeed:F1} <= absorption={absorption:F1})");
            }
        }
    }

    private void Land(UnitArrays units, int idx)
    {
        units[idx].Z = 0f;
        units[idx].InPhysics = false;
        units[idx].Velocity = Vec2.Zero;
        units[idx].PreferredVel = Vec2.Zero;

        float knockdownTime = (float)(_rng.NextDouble() *
            (KnockdownTimeMax - KnockdownTimeMin) + KnockdownTimeMin);
        units[idx].KnockdownTimer = knockdownTime;
        units[idx].OverrideAnim = AnimRequest.Forced(AnimState.Knockdown);

        DebugLog.Log("physics", $"[Land] unit#{idx} knockdown={knockdownTime:F1}s " +
            $"pos=({units[idx].Position.X:F1},{units[idx].Position.Y:F1})");

        // TODO: Apply landing impact damage based on velocity at impact
    }

    // TODO: Cavalry trample — mounted unit moving at speed applies impulse
    // to units it passes through. Horse slows from each impact.
    // public void ProcessCavalryTrample(UnitArrays units, int cavalryIdx, float dt) { }

    public int ActiveCount => _bodies.Count;

    public void Clear() => _bodies.Clear();
}
