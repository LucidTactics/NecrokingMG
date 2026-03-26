using System;
using System.Collections.Generic;
using Necroking.Core;
using Necroking.Data;
using Necroking.Movement;
using Necroking.Spatial;

namespace Necroking.GameSystems;

public class Projectile
{
    public Vec2 Position;
    public float Height;
    public Vec2 Velocity;
    public float VelocityZ;
    public ProjectileType Type = ProjectileType.Arrow;
    public Faction OwnerFaction = Faction.Human;
    public uint OwnerID = GameConstants.InvalidUnit;
    public int Damage;
    public int Precision;
    public string WeaponName = "";
    public float AoeRadius;
    public bool NoFriendlyFire = true;
    public bool IsLob;
    public bool Alive = true;
    public float Age;
    public string SpellID = "";
    public string FlipbookID = "";
    public string HitEffectFlipbookID = "";
    public HdrColor ParticleColor = new();
    public float ParticleScale = 1f;
    public HdrColor HitEffectColor = new();
    public float HitEffectScale = 1f;
    public Vec2 TargetPos;
    public float HomingStrength;
    public Vec2 BaseDirection;
    public float SwirlFreq;
    public float SwirlAmplitude;
    public float SwirlPhase;
}

public class ImpactEvent
{
    public Vec2 Position;
    public ProjectileType Type;
    public float AoeRadius;
    public string HitEffectFlipbookID = "";
    public HdrColor HitEffectColor = new();
    public float HitEffectScale = 1f;
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
    public ProjectileType ProjectileType = ProjectileType.Arrow;
}

public class ProjectileManager
{
    public const float ArrowSpeed = 23.58f;
    public const float MagicSpeed = 28.29f;
    public const float Gravity = 13.89f;

    private const float UnitHitHeight = 2.0f;
    private const float FireballHitHeight = 5.0f;
    private const float FireballArmTime = 0.15f;
    private const float HitRadius = 0.6f;
    private const float MaxAge = 10.0f;
    private const float Pi = 3.14159265f;
    private const float Deg2Rad = Pi / 180f;

    private static readonly Random _rng = new();

    private readonly List<Projectile> _projectiles = new();
    private readonly List<ImpactEvent> _impacts = new();
    private readonly List<ProjectileHit> _hits = new();

    public IReadOnlyList<Projectile> Projectiles => _projectiles;
    public IReadOnlyList<ImpactEvent> Impacts => _impacts;
    public IReadOnlyList<ProjectileHit> Hits => _hits;

    public void SpawnArrow(Vec2 from, Vec2 target, Faction faction, uint owner, int damage,
                           bool volley, int precision, string weaponName = "", float spawnHeight = 1.5f)
    {
        var dir = (target - from).Normalized();
        float dist = (target - from).Length();
        if (dist < 0.1f) dist = 0.1f;
        float speed = ArrowSpeed;

        var p = new Projectile
        {
            Position = from, Height = spawnHeight, Type = ProjectileType.Arrow,
            OwnerFaction = faction, OwnerID = owner, Damage = damage,
            Precision = precision, WeaponName = weaponName, IsLob = volley
        };

        if (volley)
        {
            float precFactor = MathF.Sqrt(precision / 10f);
            float scatterRadius = (dist / 40f) * 3f / MathF.Max(precFactor, 0.1f);
            float scatterAngle = (float)_rng.NextDouble() * 2f * Pi;
            float scatterDist = (float)_rng.NextDouble() * scatterRadius;
            target += new Vec2(MathF.Cos(scatterAngle), MathF.Sin(scatterAngle)) * scatterDist;
            dir = (target - from).Normalized();
            dist = (target - from).Length();

            float sinTwoTheta = MathF.Min(dist * Gravity / (speed * speed), 1f);
            float theta = MathUtil.Clamp(0.5f * MathF.Asin(sinTwoTheta), 10f * Deg2Rad, 45f * Deg2Rad);
            p.Velocity = dir * speed * MathF.Cos(theta);
            p.VelocityZ = speed * MathF.Sin(theta);
        }
        else
        {
            float theta = 5f * Deg2Rad;
            p.Velocity = dir * speed * MathF.Cos(theta);
            p.VelocityZ = speed * MathF.Sin(theta);
        }

        _projectiles.Add(p);
    }

    public void SpawnFireball(Vec2 from, Vec2 target, Faction faction, uint owner,
                              int damage, float aoeRadius, string weaponName = "", float spawnHeight = 1.5f)
    {
        var dir = (target - from).Normalized();
        float dist = (target - from).Length();
        if (dist < 0.1f) dist = 0.1f;
        float speed = MagicSpeed;

        float sinTwoTheta = MathF.Min(dist * Gravity / (speed * speed), 1f);
        float theta = 0.5f * MathF.Asin(sinTwoTheta);

        _projectiles.Add(new Projectile
        {
            Position = from, Height = spawnHeight, Type = ProjectileType.Fireball,
            OwnerFaction = faction, OwnerID = owner, Damage = damage,
            AoeRadius = aoeRadius, WeaponName = weaponName,
            NoFriendlyFire = false, IsLob = true,
            Velocity = dir * speed * MathF.Cos(theta),
            VelocityZ = speed * MathF.Sin(theta)
        });
    }

    public void Update(float dt, UnitArrays units, Quadtree qt)
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
            proj.VelocityZ -= Gravity * dt;
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

            // Fireball in-flight collision
            if (proj.Alive && proj.Type == ProjectileType.Fireball &&
                proj.Age > FireballArmTime && proj.Height < FireballHitHeight)
            {
                nearbyIDs.Clear();
                float collisionRadius = MathF.Max(proj.AoeRadius * 0.5f, HitRadius);
                qt.QueryRadius(proj.Position, collisionRadius, nearbyIDs);

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
                    qt.QueryRadius(proj.Position, proj.AoeRadius, nearbyIDs);
                    foreach (uint nid in nearbyIDs)
                    {
                        if (nid == proj.OwnerID) continue;
                        int hitIdx = UnitUtil.ResolveUnitIndex(units, nid);
                        if (hitIdx < 0) continue;
                        if (proj.NoFriendlyFire && units.Faction[hitIdx] == proj.OwnerFaction) continue;
                        _hits.Add(new ProjectileHit { UnitIdx = hitIdx, Damage = proj.Damage, OwnerID = proj.OwnerID, OwnerFaction = proj.OwnerFaction, WeaponName = proj.WeaponName, ProjectileType = proj.Type });
                    }
                    _impacts.Add(new ImpactEvent { Position = proj.Position, Type = proj.Type, AoeRadius = proj.AoeRadius, HitEffectFlipbookID = proj.HitEffectFlipbookID, HitEffectColor = proj.HitEffectColor, HitEffectScale = proj.HitEffectScale });
                    proj.Alive = false;
                }
            }

            // Arrow in-flight collision
            if (proj.Alive && proj.Type == ProjectileType.Arrow && proj.Height < UnitHitHeight && proj.Height > 0f)
            {
                nearbyIDs.Clear();
                qt.QueryRadius(proj.Position, HitRadius, nearbyIDs);
                foreach (uint nid in nearbyIDs)
                {
                    if (nid == proj.OwnerID) continue;
                    int hitIdx = UnitUtil.ResolveUnitIndex(units, nid);
                    if (hitIdx < 0) continue;
                    if (proj.NoFriendlyFire && units.Faction[hitIdx] == proj.OwnerFaction) continue;
                    _hits.Add(new ProjectileHit { UnitIdx = hitIdx, Damage = proj.Damage, OwnerID = proj.OwnerID, OwnerFaction = proj.OwnerFaction, Precision = proj.Precision, WeaponName = proj.WeaponName, ProjectileType = proj.Type });
                    _impacts.Add(new ImpactEvent { Position = proj.Position, Type = proj.Type });
                    proj.Alive = false;
                    break;
                }
            }

            // Ground impact
            if (proj.Alive && proj.Height <= 0f && proj.VelocityZ < 0f)
            {
                proj.Height = 0f;
                if (proj.AoeRadius > 0f)
                {
                    nearbyIDs.Clear();
                    qt.QueryRadius(proj.Position, proj.AoeRadius, nearbyIDs);
                    foreach (uint nid in nearbyIDs)
                    {
                        if (nid == proj.OwnerID) continue;
                        int hitIdx = UnitUtil.ResolveUnitIndex(units, nid);
                        if (hitIdx < 0) continue;
                        if (proj.NoFriendlyFire && units.Faction[hitIdx] == proj.OwnerFaction) continue;
                        _hits.Add(new ProjectileHit { UnitIdx = hitIdx, Damage = proj.Damage, OwnerID = proj.OwnerID, OwnerFaction = proj.OwnerFaction, Precision = proj.Precision, WeaponName = proj.WeaponName, ProjectileType = proj.Type });
                    }
                }
                else
                {
                    nearbyIDs.Clear();
                    qt.QueryRadius(proj.Position, HitRadius * 1.5f, nearbyIDs);
                    float bestDist = float.MaxValue;
                    int bestIdx = -1;
                    foreach (uint nid in nearbyIDs)
                    {
                        if (nid == proj.OwnerID) continue;
                        int idx = UnitUtil.ResolveUnitIndex(units, nid);
                        if (idx < 0) continue;
                        if (proj.NoFriendlyFire && units.Faction[idx] == proj.OwnerFaction) continue;
                        float d = (units.Position[idx] - proj.Position).LengthSq();
                        if (d < bestDist) { bestDist = d; bestIdx = idx; }
                    }
                    if (bestIdx >= 0)
                        _hits.Add(new ProjectileHit { UnitIdx = bestIdx, Damage = proj.Damage, OwnerID = proj.OwnerID, OwnerFaction = proj.OwnerFaction, Precision = proj.Precision, WeaponName = proj.WeaponName, ProjectileType = proj.Type });
                }
                _impacts.Add(new ImpactEvent { Position = proj.Position, Type = proj.Type, AoeRadius = proj.AoeRadius, HitEffectFlipbookID = proj.HitEffectFlipbookID, HitEffectColor = proj.HitEffectColor, HitEffectScale = proj.HitEffectScale });
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
            p.VelocityZ -= Gravity * dt;
            p.Age += dt;
            if (p.Height <= 0f && p.Age > 0.1f) { p.Alive = false; _impacts.Add(new ImpactEvent { Position = p.Position, Type = p.Type, AoeRadius = p.AoeRadius }); }
            if (p.Age > MaxAge) p.Alive = false;
        }
        for (int i = _projectiles.Count - 1; i >= 0; i--)
            if (!_projectiles[i].Alive) _projectiles.RemoveAt(i);
    }

    public void Clear() { _projectiles.Clear(); _impacts.Clear(); _hits.Clear(); }
}
