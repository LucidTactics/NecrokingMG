using System;
using System.Collections.Generic;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.Movement;
using Necroking.Spatial;

namespace Necroking.GameSystems;

public enum CloudPhase : byte { Eruption, Spread, Decay }

public class PoisonCloud
{
    public Vec2 Position;
    public float Age;
    public float Duration;            // Total lifetime (base + corpse bonus)
    public float BaseRadius;          // Starting radius
    public float CurrentRadius;       // Grows over time
    public float MaxRadius;           // Cap
    public float Potency = 1f;        // 0-2 multiplier, boosted by corpses
    public int BaseDamagePerTick;     // Base damage at center
    public float TickInterval = 1f;   // Seconds between damage ticks
    public float TickTimer;           // Countdown to next tick
    public float CorpseConsumeRate = 2f; // Seconds between corpse consumption
    public float CorpseConsumeTimer;
    public int CorpsesConsumed;
    public Faction OwnerFaction;
    public bool Alive = true;

    // Corpse bonus config (from spell def)
    public float CorpseRadiusBonus = 1.5f;
    public float CorpseDurationBonus = 3f;
    public float CorpsePotencyBonus = 0.25f;
    public bool CorpseBonusEnabled = true;

    // Phase config
    public float EruptionDuration = 2f;
    public float SpreadDuration = 7f;

    // Debuff config
    public string SlowBuffID = "";
    public string PlaguedBuffID = "";
    public float PlagueThreshold = 3f; // Seconds of exposure to get plagued

    // Visual
    public float NoiseOffset;          // Per-cloud randomization

    public CloudPhase Phase
    {
        get
        {
            if (Age < EruptionDuration) return CloudPhase.Eruption;
            if (Age < EruptionDuration + SpreadDuration) return CloudPhase.Spread;
            return CloudPhase.Decay;
        }
    }

    // Convenience: 0-1 progress within current phase
    public float PhaseProgress
    {
        get
        {
            return Phase switch
            {
                CloudPhase.Eruption => MathF.Min(Age / EruptionDuration, 1f),
                CloudPhase.Spread => MathF.Min((Age - EruptionDuration) / SpreadDuration, 1f),
                CloudPhase.Decay => MathF.Min((Age - EruptionDuration - SpreadDuration) /
                    MathF.Max(Duration - EruptionDuration - SpreadDuration, 0.01f), 1f),
                _ => 1f
            };
        }
    }
}

public class PoisonCloudSystem
{
    private readonly List<PoisonCloud> _clouds = new();
    private static readonly Random _rng = new();

    public IReadOnlyList<PoisonCloud> Clouds => _clouds;

    public void SpawnCloud(Vec2 position, SpellDef spell, Faction ownerFaction)
    {
        _clouds.Add(new PoisonCloud
        {
            Position = position,
            Age = 0f,
            Duration = spell.CloudDuration,
            BaseRadius = spell.CloudRadius,
            CurrentRadius = 0.5f, // Start tiny, grows during eruption
            MaxRadius = spell.CloudMaxRadius,
            BaseDamagePerTick = spell.CloudTickDamage,
            TickInterval = spell.CloudTickRate,
            TickTimer = 0.3f, // Small delay before first tick
            CorpseConsumeRate = spell.CloudCorpseConsumeRate,
            CorpseConsumeTimer = spell.CloudCorpseConsumeRate,
            CorpseBonusEnabled = spell.CloudCorpseBonus,
            CorpseRadiusBonus = spell.CloudCorpseRadiusBonus,
            CorpseDurationBonus = spell.CloudCorpseDurationBonus,
            CorpsePotencyBonus = spell.CloudCorpsePotencyBonus,
            EruptionDuration = spell.CloudEruptionDuration,
            SpreadDuration = spell.CloudSpreadDuration,
            SlowBuffID = spell.CloudSlowBuffID,
            PlaguedBuffID = spell.CloudPlaguedBuffID,
            PlagueThreshold = spell.CloudPlagueThreshold,
            OwnerFaction = ownerFaction,
            Potency = 1f,
            NoiseOffset = (float)_rng.NextDouble() * 100f,
        });
    }

    public void Update(float dt, UnitArrays units, Quadtree qt, List<Corpse> corpses,
                       List<DamageEvent> damageEvents, BuffRegistry? buffs)
    {
        var nearbyIDs = new List<uint>();

        for (int ci = _clouds.Count - 1; ci >= 0; ci--)
        {
            var cloud = _clouds[ci];
            if (!cloud.Alive) { _clouds.RemoveAt(ci); continue; }

            cloud.Age += dt;

            // Expire
            if (cloud.Age >= cloud.Duration)
            {
                cloud.Alive = false;
                _clouds.RemoveAt(ci);
                continue;
            }

            // Update phase & radius
            UpdateRadius(cloud, dt);

            // Corpse consumption
            if (cloud.CorpseBonusEnabled)
                ConsumeCorpses(cloud, dt, corpses);

            // Damage tick
            cloud.TickTimer -= dt;
            if (cloud.TickTimer <= 0f)
            {
                cloud.TickTimer += cloud.TickInterval;
                ApplyDamageTick(cloud, units, qt, nearbyIDs, damageEvents, buffs);
            }
        }
    }

    private void UpdateRadius(PoisonCloud cloud, float dt)
    {
        switch (cloud.Phase)
        {
            case CloudPhase.Eruption:
            {
                // Rapidly expand from tiny to base radius
                float t = cloud.PhaseProgress;
                float easeOut = 1f - (1f - t) * (1f - t); // Quadratic ease-out
                cloud.CurrentRadius = MathUtil.Lerp(0.5f, cloud.BaseRadius, easeOut);
                break;
            }
            case CloudPhase.Spread:
            {
                // Slowly expand toward max radius
                float t = cloud.PhaseProgress;
                cloud.CurrentRadius = MathUtil.Lerp(cloud.BaseRadius, cloud.MaxRadius, t * 0.7f);
                break;
            }
            case CloudPhase.Decay:
            {
                // Hold radius, potency fades
                // Radius stays stable, visual handles fade
                break;
            }
        }
    }

    private void ConsumeCorpses(PoisonCloud cloud, float dt, List<Corpse> corpses)
    {
        cloud.CorpseConsumeTimer -= dt;
        if (cloud.CorpseConsumeTimer > 0f) return;
        cloud.CorpseConsumeTimer += cloud.CorpseConsumeRate;

        // Find closest non-dissolving corpse within radius
        float bestDist = cloud.CurrentRadius * cloud.CurrentRadius;
        int bestIdx = -1;
        for (int i = 0; i < corpses.Count; i++)
        {
            if (corpses[i].Dissolving || corpses[i].ConsumedBySummon) continue;
            if (corpses[i].DraggedByUnitID != GameConstants.InvalidUnit) continue;
            float d = (corpses[i].Position - cloud.Position).LengthSq();
            if (d < bestDist) { bestDist = d; bestIdx = i; }
        }

        if (bestIdx < 0) return;

        // Consume the corpse
        corpses[bestIdx].Dissolving = true;
        corpses[bestIdx].ConsumedBySummon = true;
        cloud.CorpsesConsumed++;

        // Apply bonuses (capped)
        cloud.MaxRadius += cloud.CorpseRadiusBonus;
        cloud.Duration += cloud.CorpseDurationBonus;
        cloud.Potency = MathF.Min(cloud.Potency + cloud.CorpsePotencyBonus, 2.5f);

        DebugLog.Log("combat", $"  Miasma consumed corpse #{bestIdx} — " +
            $"potency={cloud.Potency:F2}, maxRadius={cloud.MaxRadius:F1}, " +
            $"duration={cloud.Duration:F1}, consumed={cloud.CorpsesConsumed}");
    }

    private void ApplyDamageTick(PoisonCloud cloud, UnitArrays units, Quadtree qt,
                                  List<uint> nearbyIDs, List<DamageEvent> damageEvents,
                                  BuffRegistry? buffs)
    {
        nearbyIDs.Clear();
        qt.QueryRadius(cloud.Position, cloud.CurrentRadius, nearbyIDs);

        // Potency multiplier based on phase
        float phaseMult = cloud.Phase switch
        {
            CloudPhase.Eruption => 1.5f,
            CloudPhase.Spread => 1.0f,
            CloudPhase.Decay => MathF.Max(0f, 1f - cloud.PhaseProgress),
            _ => 0f
        };

        foreach (uint uid in nearbyIDs)
        {
            int idx = UnitUtil.ResolveUnitIndex(units, uid);
            if (idx < 0 || !units[idx].Alive) continue;
            if (units[idx].Faction == cloud.OwnerFaction) continue; // No friendly fire

            float dist = (units[idx].Position - cloud.Position).Length();
            if (dist > cloud.CurrentRadius) continue;

            // Gaussian-ish falloff: full at center, zero at edge
            float falloff = 1f - (dist / cloud.CurrentRadius) * (dist / cloud.CurrentRadius);
            falloff = MathF.Max(falloff, 0.05f); // Minimum 5% at edge

            int damage = (int)MathF.Ceiling(cloud.BaseDamagePerTick * cloud.Potency * phaseMult * falloff);
            if (damage < 1) damage = 1;

            // Apply as poison stacks (integrates with existing poison DoT system)
            units[idx].PoisonStacks += damage * 3; // 3 stacks per damage point
            if (units[idx].PoisonTickTimer <= 0f)
                units[idx].PoisonTickTimer = 3f;

            // Green damage event for visual feedback
            damageEvents.Add(new DamageEvent
            {
                Position = units[idx].Position,
                Damage = damage,
                Height = 1.5f,
                IsPoison = true
            });

            // Apply slow debuff
            if (buffs != null && !string.IsNullOrEmpty(cloud.SlowBuffID))
            {
                var slowDef = buffs.Get(cloud.SlowBuffID);
                if (slowDef != null)
                    BuffSystem.ApplyBuff(units, idx, slowDef);
            }

            // Track cloud exposure for plague
            units[idx].CloudExposureTime += cloud.TickInterval;
            if (units[idx].CloudExposureTime >= cloud.PlagueThreshold)
            {
                if (buffs != null && !string.IsNullOrEmpty(cloud.PlaguedBuffID))
                {
                    var plagueDef = buffs.Get(cloud.PlaguedBuffID);
                    if (plagueDef != null)
                        BuffSystem.ApplyBuff(units, idx, plagueDef);
                }
            }
        }
    }

    public void Clear() => _clouds.Clear();
}
