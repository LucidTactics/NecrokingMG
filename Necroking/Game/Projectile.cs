using System;
using System.Collections.Generic;
using Necroking.Core;
using Necroking.Data;
using Necroking.Lib;
using Necroking.Movement;
using Necroking.Spatial;

namespace Necroking.GameSystems;

public class Projectile
{
    public Vec2 Position;
    public float Height;
    public Vec2 Velocity;
    public float VelocityZ;
    /// <summary>Multiplier on the downward pull applied each tick (1 = normal gravity).
    /// &lt;1 flattens the arc (floatier, more airtime), &gt;1 steepens it, 0 = flies dead
    /// flat. The launch solve is fed the SAME scaled gravity, so a lob still lands on its
    /// target — only the arc shape changes.</summary>
    public float GravityScale = 1f;
    public ProjectileType Type = ProjectileType.RegularHit;
    public Faction OwnerFaction = Faction.Human;
    public uint OwnerID = GameConstants.InvalidUnit;
    public int Damage;
    public int Precision;
    public string WeaponName = "";
    public float AoeRadius;
    public bool NoFriendlyFire = true;
    public bool IsLob;
    public bool Alive = true;
    /// <summary>Detonate exactly at <see cref="TargetPos"/> (the aimed point) the
    /// moment the projectile arcs over it, instead of overshooting to wherever its
    /// ballistic arc reaches the ground. Used by thrown "bomb" spells that must burst
    /// where you clicked (e.g. the Blight bomb).</summary>
    public bool DetonateAtTarget;
    public float Age;
    public string SpellID = "";
    public string FlipbookID = "";
    public string HitEffectFlipbookID = "";
    public HdrColor ParticleColor = new();
    public float ParticleScale = 1f;
    public HdrColor HitEffectColor = new();
    public float HitEffectScale = 1f;
    public int HitEffectBlendMode; // 0=alpha, 1=additive
    public int HitEffectAlignment; // 0=ground, 1=upright
    public Vec2 TargetPos;
    public float HomingStrength;
    public Vec2 BaseDirection;
    public float SwirlFreq;
    public float SwirlAmplitude;
    public float SwirlPhase;
    public float SwirlFreq2;
    public float SwirlAmplitude2;
    public float SwirlPhase2;
    public string PotionID = "";
    public string IconTexturePath = "";
    public bool HitsCorpses;
    public string PotionTargetType = "Any"; // "Friendly", "Enemy", "Any"
    /// <summary>Directional physics shove applied to each unit this projectile hits,
    /// along the flight direction at impact. 0 = none.</summary>
    public float ImpactForce;
    public float ImpactUpward;

    /// <summary>Instantaneous XY velocity the projectile is *visually* travelling,
    /// including the swirl wobble's analytic derivative. Swirl displaces Position
    /// directly each tick without touching <see cref="Velocity"/> (homing,
    /// detonate-at-target and impact FlightDir all depend on Velocity staying the
    /// ballistic base), so renderers that orient the sprite along the flight path
    /// must face this instead.</summary>
    public Vec2 VisualVelocity
    {
        get
        {
            if (SwirlFreq <= 0f) return Velocity;
            var perp = new Vec2(-BaseDirection.Y, BaseDirection.X);
            float omega = SwirlFreq * 2f * MathF.PI;
            float swirlVel = MathF.Cos(omega * Age + SwirlPhase) * SwirlAmplitude * omega;

            return Velocity + perp * swirlVel;
        }
    }

    public float VisualVelocityZ {
       get {
          if (SwirlFreq2 <= 0f) return VelocityZ;

          float omega = SwirlFreq2 * 2f * MathF.PI;
          float swirlVel = MathF.Cos(omega * Age + SwirlPhase2) * SwirlAmplitude2 * omega;
          return VelocityZ + swirlVel;
       }
    }
}

public class ImpactEvent
{
    public Vec2 Position;
    public ProjectileType Type;
    public float AoeRadius;
    /// <summary>Spell that fired this projectile, if any (empty for arrows/potions).
    /// Lets ground-targeted spell effects (e.g. a Blight bomb dropping death-fog)
    /// fire at the exact spot the projectile exploded, not at cast time.</summary>
    public string SpellID = "";
    public string HitEffectFlipbookID = "";
    public HdrColor HitEffectColor = new();
    public float HitEffectScale = 1f;
    public int HitEffectBlendMode;
    public int HitEffectAlignment;
}

public class ProjectileHit
{
    public int UnitIdx;
    public int Damage;
    public HitLocation HitLocation = HitLocation.Chest;
    public uint OwnerID = GameConstants.InvalidUnit;
    public Faction OwnerFaction = Faction.Human;
    public int Precision;
    public string WeaponName = "";
    public ProjectileType ProjectileType = ProjectileType.RegularHit;
    public string PotionID = "";
    public Vec2 ImpactPos;
    public int CorpseHitIdx = -1;
    public string SpellID = "";
    public float AoeRadius;
    /// <summary>Projectile's horizontal velocity at the moment of impact (unnormalized
    /// — PhysicsSystem.ApplyImpulse normalizes). Direction for <see cref="ImpactForce"/>.</summary>
    public Vec2 FlightDir;
    public float ImpactForce;
    public float ImpactUpward;
}

public class ProjectileManager
{
    public const float ArrowSpeed = 23.58f;
    public const float MagicSpeed = 28.29f;
    public const float Gravity = 13.89f;

    private const float UnitHitHeight = 2.0f;
    private const float ExplosiveHitHeight = 5.0f;
    private const float ExplosiveArmTime = 0.15f;
    private const float HitRadius = 0.6f;
    private const float PotionHitRadius = 1.0f;
    private const float PotionHitHeight = 3.0f;
    private const float PotionArmTime = 0.2f;
    private const float MaxAge = 10.0f;
    private const float Pi = 3.14159265f;
    private const float Deg2Rad = Pi / 180f;

    /// <summary>Launch angle of a flat direct-fire shot (arrows outside volleys,
    /// DirectFire/Swirly/Homing spell trajectories).</summary>
    public const float DirectFireTheta = 5f * Deg2Rad;

    private static readonly Random _rng = new();

    private readonly List<Projectile> _projectiles = new();
    private readonly List<ImpactEvent> _impacts = new();
    private readonly List<ProjectileHit> _hits = new();

    public IReadOnlyList<Projectile> Projectiles => _projectiles;
    public IReadOnlyList<ImpactEvent> Impacts => _impacts;
    public IReadOnlyList<ProjectileHit> Hits => _hits;

    // ------------------------------------------------------------ arc solver
    // THE ballistic solve — every projectile launch (arrows, fireballs, potion
    // lobs, spell trajectories) derives its velocity through these two helpers
    // so the launch model has one home. (Editor/SpellPreview still carries its
    // own copy — a later consolidation batch adopts this solver there.)

    /// <summary>Launch angle (radians) for a ballistic lob that lands
    /// <paramref name="dist"/> away at <paramref name="speed"/> under gravity
    /// <paramref name="gravity"/>: θ = ½·asin(min(d·g/v², 1)). Optional clamp —
    /// arrow volleys use [10°, 45°] so short lobs still arc visibly. Pass a scaled
    /// <paramref name="gravity"/> (Gravity·GravityScale) so the arc still lands on target
    /// when a projectile's gravity is tuned.
    /// <paramref name="preferLob"/> returns the higher arc, WIP, this needs more work since
    /// realistic high arcs feel bad!</summary>
    public static float SolveLobTheta(float dist, float speed,
        float minTheta = -Pi / 2f, float maxTheta = Pi / 2f, float gravity = Gravity, bool preferLob=false)
    {
        float sinTwoTheta = MathF.Min(dist * gravity / (speed * speed), 1f);
        float res = MathUtil.Clamp(0.5f * MathF.Asin(sinTwoTheta), minTheta, maxTheta);
        if (preferLob) return Pi / 2f - res;
        return res;
    }

    /// <summary>Split a launch (dir, speed, theta) into the planar Velocity and
    /// vertical VelocityZ — the shared final step of every lob/direct solve.</summary>
    public static (Vec2 vel, float velZ) BallisticVelocity(Vec2 dir, float speed, float theta)
        => (dir * speed * MathF.Cos(theta), speed * MathF.Sin(theta));

    /// <summary>The shared aim preamble: normalized direction + distance with a
    /// 0.1 floor (a zero-length shot would NaN the solve).</summary>
    private static void PrepAim(Vec2 from, Vec2 target, out Vec2 dir, out float dist)
    {
        dir = (target - from).Normalized();
        dist = (target - from).Length();
        if (dist < 0.1f) dist = 0.1f;
    }

    /// <summary>
    /// Spawn a projectile — the single entry point for every projectile in the game
    /// (unit arrows, spell shots, potion lobs, dev fireballs). What it does on contact
    /// is <paramref name="type"/> (see <see cref="ProjectileType"/>); how it flies is
    /// <paramref name="lob"/>: false = flat direct fire at <see cref="DirectFireTheta"/>,
    /// true = ballistic arc solved to land on the target. A RegularHit lob (arrow volley)
    /// additionally scatters around the target (worse <paramref name="precision"/> =
    /// wider) and clamps the arc to [10°, 45°] so short lobs still rise visibly over
    /// allies' heads; an Explosive/Potion lob flies the exact min-energy arc, or the
    /// high arc when <paramref name="preferHighArc"/> is set.
    /// <paramref name="spawnHeight"/> is the starting world-unit height above ground —
    /// pass the shooter's <c>Unit.EffectSpawnHeight</c> (set by
    /// <c>UpdateEffectSpawnPositions</c> from the weapon-tip anim point) so the arc
    /// starts at the bow tip / staff / throwing hand. The 0.6 default is the "no
    /// weapon-tip data" fallback and should only be used by tests / non-unit sources.
    /// Returns the spawned projectile so callers can post-configure it (SpellID,
    /// flipbooks, homing, potion payload, ...).
    /// </summary>
    public Projectile Spawn(Vec2 from, Vec2 target, Faction faction, uint owner,
                            ProjectileType type, int damage, float speed, bool lob,
                            float aoeRadius = 0f, int precision = 0, string weaponName = "",
                            float spawnHeight = 0.6f, float gravityScale = 1f,
                            bool preferHighArc = false)
    {
        PrepAim(from, target, out var dir, out float dist);

        float theta;
        if (!lob)
        {
           // Direct fire, no accuracy needed.
           theta = SolveLobTheta(dist, speed, gravity: Gravity * gravityScale);
        }
        else
        {
            float precFactor = MathF.Sqrt(precision / 10f);
            float scatterRadius = (dist / 40f) * 3f / MathF.Max(precFactor, 0.1f);
            float scatterAngle = (float)_rng.NextDouble() * 2f * Pi;
            float scatterDist = (float)_rng.NextDouble() * scatterRadius;
            target += new Vec2(MathF.Cos(scatterAngle), MathF.Sin(scatterAngle)) * scatterDist;
            dir = (target - from).Normalized();
            dist = (target - from).Length();
            theta = SolveLobTheta(dist, speed, gravity: Gravity * gravityScale, preferLob: preferHighArc);
        }

        var p = new Projectile
        {
            Position = from, Height = spawnHeight, Type = type,
            OwnerFaction = faction, OwnerID = owner, Damage = damage,
            AoeRadius = aoeRadius, Precision = precision, WeaponName = weaponName,
            // A RegularHit shot respects factions; explosions and potion splashes hit everyone.
            NoFriendlyFire = type == ProjectileType.RegularHit,
            IsLob = lob, GravityScale = gravityScale, BaseDirection = dir
        };
        (p.Velocity, p.VelocityZ) = BallisticVelocity(dir, speed, theta);
        _projectiles.Add(p);
        return p;
    }

    public void Update(float dt, UnitArrays units, Quadtree qt) => Update(dt, units, qt, null);

    public void Update(float dt, UnitArrays units, Quadtree qt, IReadOnlyList<Corpse>? corpses)
    {
        _impacts.Clear();
        _hits.Clear();
        var nearbyIDs = new List<uint>();

        foreach (var proj in _projectiles)
        {
            if (!proj.Alive) continue;

            // Physics
            proj.Position += proj.Velocity * dt;
            proj.Height += proj.VelocityZ * dt;
            proj.VelocityZ -= Gravity * proj.GravityScale * dt;
            proj.Age += dt;

            // Homing
            if (proj.HomingStrength > 0f)
            {
                var toTarget = proj.TargetPos - proj.Position;
                float dist = toTarget.Length();
                if (dist > 0.5f)
                {
                    var desired = toTarget * (1f / dist);
                    float speed = proj.Velocity.Length();
                    if (speed > 0.01f)
                    {
                        var currentDir = proj.Velocity * (1f / speed);
                        var newDir = (currentDir + desired * (proj.HomingStrength * dt)).Normalized();
                        proj.Velocity = newDir * speed;
                        proj.BaseDirection = newDir;
                    }
                }
            }

            // Swirl
            if (proj.SwirlFreq > 0f)
            {
                var perp = new Vec2(-proj.BaseDirection.Y, proj.BaseDirection.X);
                float prevSwirl = MathF.Sin(proj.SwirlFreq * (proj.Age - dt) * 2f * Pi + proj.SwirlPhase) * proj.SwirlAmplitude;
                float currSwirl = MathF.Sin(proj.SwirlFreq * proj.Age * 2f * Pi + proj.SwirlPhase) * proj.SwirlAmplitude;
                proj.Position += perp * (currSwirl - prevSwirl);
            }
            if (proj.SwirlFreq2 > 0f)
            {
               float prevSwirl = MathF.Sin(proj.SwirlFreq2 * (proj.Age - dt) * 2f * Pi + proj.SwirlPhase2) * proj.SwirlAmplitude2;
               float currSwirl = MathF.Sin(proj.SwirlFreq2 * proj.Age * 2f * Pi + proj.SwirlPhase2) * proj.SwirlAmplitude2;
               proj.Height += (currSwirl - prevSwirl);
            }

            // Detonate-at-target: burst exactly at the aimed point once the bomb
            // crosses its target's horizontal position, rather than overshooting to
            // wherever the ballistic arc meets the ground (short lobs launched from
            // staff height otherwise sail well past where you clicked).
            if (proj.Alive && proj.DetonateAtTarget && proj.Age > ExplosiveArmTime)
            {
                var toTarget = proj.TargetPos - proj.Position;
                if (proj.Velocity.LengthSq() > 1e-4f && toTarget.Dot(proj.Velocity) <= 0f)
                {
                    proj.Position = proj.TargetPos;
                    proj.Height = 0f;
                    _impacts.Add(new ImpactEvent { Position = proj.Position, Type = proj.Type, AoeRadius = proj.AoeRadius, SpellID = proj.SpellID, HitEffectFlipbookID = proj.HitEffectFlipbookID, HitEffectColor = proj.HitEffectColor, HitEffectScale = proj.HitEffectScale, HitEffectBlendMode = proj.HitEffectBlendMode, HitEffectAlignment = proj.HitEffectAlignment });
                    proj.Alive = false;
                    continue;
                }
            }

            // Explosive in-flight collision
            if (proj.Alive && proj.Type == ProjectileType.Explosive &&
                proj.Age > ExplosiveArmTime && proj.Height < ExplosiveHitHeight)
            {
                nearbyIDs.Clear();
                float collisionRadius = MathF.Max(proj.AoeRadius * 0.5f, HitRadius);
                qt.QueryRadius(proj.Position, collisionRadius, nearbyIDs);
                // Ensure we deal damage to what we collided with at least.
                // TODO: Clear up the logic here, this is not good, but at least this way we can see logs of what happened.
                float aoe_radius = MathF.Max(proj.AoeRadius, HitRadius);

                bool hitSomething = false;
                foreach (uint nid in nearbyIDs)
                {
                    if (nid == proj.OwnerID) continue;
                    if (UnitUtil.ResolveUnitIndex(units, nid) >= 0) { hitSomething = true; break; }
                }

                if (hitSomething)
                {
                    // AOE damage
                    nearbyIDs.Clear();
                    if (proj.NoFriendlyFire)
                        qt.QueryRadiusByFaction(proj.Position, aoe_radius,
                            FactionMaskExt.AllExcept(proj.OwnerFaction), nearbyIDs);
                    else
                        qt.QueryRadius(proj.Position, aoe_radius, nearbyIDs);
                    foreach (uint nid in nearbyIDs)
                    {
                        if (nid == proj.OwnerID) continue;
                        int hitIdx = UnitUtil.ResolveUnitIndex(units, nid);
                        if (hitIdx < 0) continue;
                        _hits.Add(new ProjectileHit { UnitIdx = hitIdx, Damage = proj.Damage, OwnerID = proj.OwnerID, OwnerFaction = proj.OwnerFaction, WeaponName = proj.WeaponName, ProjectileType = proj.Type, SpellID = proj.SpellID, AoeRadius = proj.AoeRadius, ImpactPos = proj.Position, FlightDir = proj.Velocity, ImpactForce = proj.ImpactForce, ImpactUpward = proj.ImpactUpward });
                    }
                    _impacts.Add(new ImpactEvent { Position = proj.Position, Type = proj.Type, AoeRadius = proj.AoeRadius, SpellID = proj.SpellID, HitEffectFlipbookID = proj.HitEffectFlipbookID, HitEffectColor = proj.HitEffectColor, HitEffectScale = proj.HitEffectScale, HitEffectBlendMode = proj.HitEffectBlendMode, HitEffectAlignment = proj.HitEffectAlignment });
                    proj.Alive = false;
                }
            }

            // Direct-hit in-flight collision (arrows, magic darts)
            if (proj.Alive && proj.Type == ProjectileType.RegularHit && proj.Height < UnitHitHeight && proj.Height > 0f)
            {
                nearbyIDs.Clear();
                if (proj.NoFriendlyFire)
                    qt.QueryRadiusByFaction(proj.Position, HitRadius,
                        FactionMaskExt.AllExcept(proj.OwnerFaction), nearbyIDs);
                else
                    qt.QueryRadius(proj.Position, HitRadius, nearbyIDs);
                foreach (uint nid in nearbyIDs)
                {
                    if (nid == proj.OwnerID) continue;
                    int hitIdx = UnitUtil.ResolveUnitIndex(units, nid);
                    if (hitIdx < 0) continue;
                    _hits.Add(new ProjectileHit { UnitIdx = hitIdx, Damage = proj.Damage, OwnerID = proj.OwnerID, OwnerFaction = proj.OwnerFaction, Precision = proj.Precision, WeaponName = proj.WeaponName, ProjectileType = proj.Type, HitLocation = RollArrowHitLocation(proj.IsLob), FlightDir = proj.Velocity, ImpactForce = proj.ImpactForce, ImpactUpward = proj.ImpactUpward });
                    _impacts.Add(new ImpactEvent { Position = proj.Position, Type = proj.Type });
                    proj.Alive = false;
                    break;
                }
            }

            // Potion collision — unified: hits valid unit or corpse within radius
            // Checks after arm time, while below potion hit height (not just on descent)
            if (proj.Alive && proj.Type == ProjectileType.Potion &&
                proj.Age > PotionArmTime && proj.Height < PotionHitHeight)
            {
                float searchR = PotionHitRadius;
                float searchRSq = searchR * searchR;

                // Find closest valid unit
                int bestUnitIdx = -1;
                float bestUnitDist = searchRSq;
                nearbyIDs.Clear();
                // Narrow the quadtree scan by potion target type — "Friendly" is owner's
                // faction only, "Enemy" is everything else, "Any" hits both.
                FactionMask potionMask = proj.PotionTargetType switch
                {
                    "Friendly" => proj.OwnerFaction.Bit(),
                    "Enemy"    => FactionMaskExt.AllExcept(proj.OwnerFaction),
                    _          => FactionMask.All,
                };
                qt.QueryRadiusByFaction(proj.Position, searchR, potionMask, nearbyIDs);
                foreach (uint nid in nearbyIDs)
                {
                    if (nid == proj.OwnerID) continue;
                    int hitIdx = UnitUtil.ResolveUnitIndex(units, nid);
                    if (hitIdx < 0) continue;
                    float d = Vec2.DistSq(units[hitIdx].Position, proj.Position);
                    if (d < bestUnitDist) { bestUnitDist = d; bestUnitIdx = hitIdx; }
                }

                // Find closest corpse if enabled
                int bestCorpseIdx = -1;
                float bestCorpseDist = searchRSq;
                if (proj.HitsCorpses && corpses != null)
                {
                    for (int ci = 0; ci < corpses.Count; ci++)
                    {
                        if (corpses[ci].Dissolving) continue;
                        float d = (corpses[ci].Position - proj.Position).LengthSq();
                        if (d < bestCorpseDist) { bestCorpseDist = d; bestCorpseIdx = ci; }
                    }
                }

                // Hit whichever is closer
                bool hitUnit = bestUnitIdx >= 0 && (bestCorpseIdx < 0 || bestUnitDist <= bestCorpseDist);
                bool hitCorpse = bestCorpseIdx >= 0 && !hitUnit;

                if (hitUnit || hitCorpse)
                {
                    _hits.Add(new ProjectileHit {
                        UnitIdx = hitUnit ? bestUnitIdx : -1,
                        CorpseHitIdx = hitCorpse ? bestCorpseIdx : -1,
                        Damage = 0, OwnerID = proj.OwnerID, OwnerFaction = proj.OwnerFaction,
                        PotionID = proj.PotionID, ProjectileType = proj.Type, ImpactPos = proj.Position
                    });
                    _impacts.Add(new ImpactEvent { Position = proj.Position, Type = proj.Type, AoeRadius = 0,
                        HitEffectFlipbookID = proj.HitEffectFlipbookID, HitEffectColor = proj.HitEffectColor, HitEffectScale = proj.HitEffectScale, HitEffectBlendMode = proj.HitEffectBlendMode, HitEffectAlignment = proj.HitEffectAlignment });
                    proj.Alive = false;
                }
            }

            // Potion ground impact — missed everything, record for ground-targeted effects
            if (proj.Alive && proj.Type == ProjectileType.Potion && proj.Height <= 0f && proj.VelocityZ < 0f)
            {
                proj.Height = 0f;
                _hits.Add(new ProjectileHit {
                    UnitIdx = -1, CorpseHitIdx = -1,
                    Damage = 0, OwnerID = proj.OwnerID, OwnerFaction = proj.OwnerFaction,
                    PotionID = proj.PotionID, ProjectileType = proj.Type, ImpactPos = proj.Position
                });
                _impacts.Add(new ImpactEvent { Position = proj.Position, Type = proj.Type, AoeRadius = 0,
                    HitEffectFlipbookID = proj.HitEffectFlipbookID, HitEffectColor = proj.HitEffectColor, HitEffectScale = proj.HitEffectScale, HitEffectBlendMode = proj.HitEffectBlendMode, HitEffectAlignment = proj.HitEffectAlignment });
                proj.Alive = false;
            }

            // Ground impact
            if (proj.Alive && proj.Height <= 0f && proj.VelocityZ < 0f)
            {
                proj.Height = 0f;
                if (proj.AoeRadius > 0f)
                {
                    nearbyIDs.Clear();
                    if (proj.NoFriendlyFire)
                        qt.QueryRadiusByFaction(proj.Position, proj.AoeRadius,
                            FactionMaskExt.AllExcept(proj.OwnerFaction), nearbyIDs);
                    else
                        qt.QueryRadius(proj.Position, proj.AoeRadius, nearbyIDs);
                    foreach (uint nid in nearbyIDs)
                    {
                        if (nid == proj.OwnerID) continue;
                        int hitIdx = UnitUtil.ResolveUnitIndex(units, nid);
                        if (hitIdx < 0) continue;
                        _hits.Add(new ProjectileHit { UnitIdx = hitIdx, Damage = proj.Damage, OwnerID = proj.OwnerID, OwnerFaction = proj.OwnerFaction, Precision = proj.Precision, WeaponName = proj.WeaponName, ProjectileType = proj.Type, SpellID = proj.SpellID, AoeRadius = proj.AoeRadius, ImpactPos = proj.Position, FlightDir = proj.Velocity, ImpactForce = proj.ImpactForce, ImpactUpward = proj.ImpactUpward });
                    }
                }
                else
                {
                    nearbyIDs.Clear();
                    if (proj.NoFriendlyFire)
                        qt.QueryRadiusByFaction(proj.Position, HitRadius * 1.5f,
                            FactionMaskExt.AllExcept(proj.OwnerFaction), nearbyIDs);
                    else
                        qt.QueryRadius(proj.Position, HitRadius * 1.5f, nearbyIDs);
                    float bestDist = float.MaxValue;
                    int bestIdx = -1;
                    foreach (uint nid in nearbyIDs)
                    {
                        if (nid == proj.OwnerID) continue;
                        int idx = UnitUtil.ResolveUnitIndex(units, nid);
                        if (idx < 0) continue;
                        float d = Vec2.DistSq(units[idx].Position, proj.Position);
                        if (d < bestDist) { bestDist = d; bestIdx = idx; }
                    }
                    if (bestIdx >= 0)
                        _hits.Add(new ProjectileHit { UnitIdx = bestIdx, Damage = proj.Damage, OwnerID = proj.OwnerID, OwnerFaction = proj.OwnerFaction, Precision = proj.Precision, WeaponName = proj.WeaponName, ProjectileType = proj.Type, SpellID = proj.SpellID, ImpactPos = proj.Position, HitLocation = proj.Type == ProjectileType.RegularHit ? RollArrowHitLocation(proj.IsLob) : HitLocation.Chest, FlightDir = proj.Velocity, ImpactForce = proj.ImpactForce, ImpactUpward = proj.ImpactUpward });
                }
                _impacts.Add(new ImpactEvent { Position = proj.Position, Type = proj.Type, AoeRadius = proj.AoeRadius, SpellID = proj.SpellID, HitEffectFlipbookID = proj.HitEffectFlipbookID, HitEffectColor = proj.HitEffectColor, HitEffectScale = proj.HitEffectScale, HitEffectBlendMode = proj.HitEffectBlendMode, HitEffectAlignment = proj.HitEffectAlignment });
                proj.Alive = false;
            }

            if (proj.Age > MaxAge) proj.Alive = false;
        }

        // Remove dead
        for (int i = _projectiles.Count - 1; i >= 0; i--)
            if (!_projectiles[i].Alive) _projectiles.RemoveAt(i);
    }

    // Overload without quadtree (basic physics only)
    public void Update(float dt)
    {
        _impacts.Clear();
        _hits.Clear();
        foreach (var p in _projectiles)
        {
            if (!p.Alive) continue;
            p.Position += p.Velocity * dt;
            p.Height += p.VelocityZ * dt;
            p.VelocityZ -= Gravity * p.GravityScale * dt;
            p.Age += dt;
            if (p.Height <= 0f && p.Age > 0.1f) { p.Alive = false; _impacts.Add(new ImpactEvent { Position = p.Position, Type = p.Type, AoeRadius = p.AoeRadius }); }
            if (p.Age > MaxAge) p.Alive = false;
        }
        for (int i = _projectiles.Count - 1; i >= 0; i--)
            if (!_projectiles[i].Alive) _projectiles.RemoveAt(i);
    }

    /// <summary>Where an arrow strikes: a plunging lob comes down on the target
    /// (50% head), a flat direct shot flies at torso height (20% head). Mirrors
    /// the C++ hit-location split; feeds head-vs-body armor in ResolveArrowHit.</summary>
    private static HitLocation RollArrowHitLocation(bool isLob)
    {
        float headChance = isLob ? 0.5f : 0.2f;
        return _rng.NextDouble() < headChance ? HitLocation.Head : HitLocation.Chest;
    }

    public void Clear() { _projectiles.Clear(); _impacts.Clear(); _hits.Clear(); }
}
