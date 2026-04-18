using System;
using System.Collections.Generic;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.GameSystems;
using Necroking.Movement;
using Necroking.Render;

namespace Necroking.Game;

public struct PendingZombieRaise
{
    public Vec2 Position;
    public string UnitDefID;
    public float FacingAngle;
    public float SpriteScale;
    public float Timer;
}

public static class PotionSystem
{
    /// <summary>
    /// Try to throw a potion. Returns true if successfully thrown (projectile spawned or direct-applied).
    /// </summary>
    public static bool TryThrowPotion(
        string potionId, PotionRegistry potions, Inventory inventory,
        UnitArrays units, int necroIdx, Vec2 mouseWorld,
        IReadOnlyList<Corpse> corpses, ProjectileManager projectiles)
    {
        var potion = potions.Get(potionId);
        if (potion == null || necroIdx < 0) return false;

        // Must have the item in inventory
        if (inventory.GetItemCount(potion.ItemID) <= 0) return false;

        var necroPos = units[necroIdx].Position;
        float dist = (mouseWorld - necroPos).Length();

        // Range check
        if (dist > potion.ThrowRange + 1f) return false;

        // Self-target: apply directly if clicking near necromancer
        if (dist < 1.0f)
        {
            // Direct apply will be handled by caller after consuming item
            inventory.RemoveItem(potion.ItemID, 1);
            return true; // caller checks dist < 1.0 and applies directly
        }

        // Throw projectile
        uint necroUid = units[necroIdx].Id;
        projectiles.SpawnPotionLob(necroPos, mouseWorld, units[necroIdx].Faction, necroUid,
            potionId, potion.ProjectileScale);

        // Set visuals on the spawned projectile
        var projs = projectiles.Projectiles;
        if (projs.Count > 0)
        {
            var lastProj = projs[projs.Count - 1];
            // Set icon texture path for potion sprite rendering
            lastProj.IconTexturePath = potion.Icon;
            lastProj.HitsCorpses = potion.HitsCorpses;
            lastProj.PotionTargetType = potion.TargetType;
            if (potion.ProjectileFlipbook != null)
            {
                lastProj.FlipbookID = potion.ProjectileFlipbook.FlipbookID;
                lastProj.ParticleScale = potion.ProjectileFlipbook.Scale;
                lastProj.ParticleColor = potion.ProjectileFlipbook.Color;
            }
            if (potion.HitEffectFlipbook != null)
            {
                lastProj.HitEffectFlipbookID = potion.HitEffectFlipbook.FlipbookID;
                lastProj.HitEffectScale = potion.HitEffectFlipbook.Scale;
                lastProj.HitEffectColor = potion.HitEffectFlipbook.Color;
                lastProj.HitEffectBlendMode = potion.HitEffectFlipbook.BlendMode == "Additive" ? 1 : 0;
                lastProj.HitEffectAlignment = potion.HitEffectFlipbook.Alignment == "Upright" ? 1 : 0;
            }
        }

        inventory.RemoveItem(potion.ItemID, 1);
        return true;
    }

    /// <summary>
    /// Apply a potion effect on hit. Called when a potion projectile lands.
    /// </summary>
    public static void ApplyPotionEffect(
        string potionId, PotionRegistry potions, BuffRegistry buffs,
        int hitUnitIdx, UnitArrays units, Faction ownerFaction,
        List<PendingZombieRaise> pendingRaises, List<Corpse> corpses, Vec2 impactPos,
        List<DamageEvent>? damageEvents = null)
    {
        var potion = potions.Get(potionId);
        if (potion == null) return;

        switch (potion.OnHitEffect)
        {
            case "Frenzy":
                ApplyFrenzy(potion, buffs, hitUnitIdx, units);
                break;
            case "Paralysis":
                ApplyParalysis(potion, buffs, hitUnitIdx, units);
                break;
            case "Zombie":
                ApplyZombie(potion, buffs, hitUnitIdx, units, ownerFaction, pendingRaises, corpses, impactPos);
                break;
            case "Poison":
                ApplyPoison(potion, buffs, hitUnitIdx, units, ownerFaction, damageEvents);
                break;
            default:
                // Generic buff application
                if (!string.IsNullOrEmpty(potion.BuffID) && hitUnitIdx >= 0)
                {
                    var buffDef = buffs.Get(potion.BuffID);
                    if (buffDef != null)
                        BuffSystem.ApplyBuff(units, hitUnitIdx, buffDef);
                }
                break;
        }
    }

    private static void ApplyFrenzy(PotionDef potion, BuffRegistry buffs, int unitIdx, UnitArrays units)
    {
        if (unitIdx < 0 || unitIdx >= units.Count) return;

        // Apply frenzy buff (permanent)
        if (!string.IsNullOrEmpty(potion.BuffID))
        {
            var buffDef = buffs.Get(potion.BuffID);
            if (buffDef != null)
            {
                BuffSystem.ApplyBuff(units, unitIdx, buffDef);
                // Mark the buff as permanent
                var activeBuffs = units[unitIdx].ActiveBuffs;
                for (int i = 0; i < activeBuffs.Count; i++)
                {
                    if (activeBuffs[i].BuffDefID == buffDef.Id)
                    {
                        var b = activeBuffs[i];
                        b.Permanent = true;
                        activeBuffs[i] = b;
                        break;
                    }
                }
            }
        }

        // Set frenzy behavior flag
        units[unitIdx].Frenzied = true;
    }

    private static void ApplyParalysis(PotionDef potion, BuffRegistry buffs, int unitIdx, UnitArrays units)
    {
        ApplyParalysis(unitIdx, units);
        if (!string.IsNullOrEmpty(potion.BuffID))
        {
            var buffDef = buffs.Get(potion.BuffID);
            if (buffDef != null)
                BuffSystem.ApplyBuff(units, unitIdx, buffDef);
        }
    }

    /// <summary>
    /// Start the paralysis sequence on a unit. No-op if the unit is already in the slow
    /// phase (timer keeps counting down, so staying in the cloud doesn't postpone the stun)
    /// or stun phase. Call once per tick — re-hits within the same cloud have no effect.
    /// </summary>
    public static void ApplyParalysis(int unitIdx, UnitArrays units)
    {
        if (unitIdx < 0 || unitIdx >= units.Count || !units[unitIdx].Alive) return;
        if (units[unitIdx].ParalysisSlowTimer > 0f || units[unitIdx].ParalysisStunTimer > 0f) return;
        units[unitIdx].ParalysisSlowTimer = ParalyzeSlowDuration;
    }

    private static void ApplyZombie(PotionDef potion, BuffRegistry buffs, int unitIdx, UnitArrays units,
        Faction ownerFaction, List<PendingZombieRaise> pendingRaises, List<Corpse> corpses, Vec2 impactPos)
    {
        // No unit hit and no corpse hit (corpse hits are handled directly in Simulation via CorpseHitIdx)
        if (unitIdx < 0) return;

        // Hit a unit
        if (units[unitIdx].Faction == ownerFaction)
        {
            // Friendly: coat weapons with zombie curse
            units[unitIdx].WeaponZombieCoatTimer = 300f; // 5 minutes

            if (!string.IsNullOrEmpty(potion.BuffID))
            {
                var buffDef = buffs.Get(potion.BuffID);
                if (buffDef != null)
                    BuffSystem.ApplyBuff(units, unitIdx, buffDef);
            }
        }
        else
        {
            // Enemy: mark to raise as zombie on death
            units[unitIdx].ZombieOnDeath = true;

            if (!string.IsNullOrEmpty(potion.BuffID))
            {
                var buffDef = buffs.Get(potion.BuffID);
                if (buffDef != null)
                    BuffSystem.ApplyBuff(units, unitIdx, buffDef);
            }
        }
    }

    private static void ApplyPoison(PotionDef potion, BuffRegistry buffs, int unitIdx, UnitArrays units, Faction ownerFaction, List<DamageEvent>? damageEvents = null)
    {
        if (unitIdx < 0 || unitIdx >= units.Count) return;

        if (units[unitIdx].Faction == ownerFaction)
        {
            // Friendly: coat weapons with poison
            units[unitIdx].WeaponPoisonCoatTimer = 300f; // 5 minutes
            units[unitIdx].WeaponPoisonAmount = 5;

            if (!string.IsNullOrEmpty(potion.BuffID))
            {
                var buffDef = buffs.Get(potion.BuffID);
                if (buffDef != null)
                    BuffSystem.ApplyBuff(units, unitIdx, buffDef);
            }
        }
        else
        {
            // Enemy: apply 10 poison stacks through damage system (potions bypass armor)
            var dmgEvents = damageEvents ?? new List<DamageEvent>();
            DamageSystem.Apply(units, unitIdx, 10,
                DamageType.Poison, DamageFlags.ArmorNegating, dmgEvents);

            if (!string.IsNullOrEmpty(potion.BuffID))
            {
                var buffDef = buffs.Get(potion.BuffID);
                if (buffDef != null)
                    BuffSystem.ApplyBuff(units, unitIdx, buffDef);
            }
        }
    }

    // Paralysis timings (seconds).
    public const float ParalyzeSlowDuration = 8f;
    public const float ParalyzeStunDuration = 6f;
    // Speed multiplier at the start of the slow phase — the curve lerps this to 0 over ParalyzeSlowDuration.
    private const float ParalyzeSlowStartMultiplier = 0.7f;

    /// <summary>
    /// Get the paralysis stat multiplier for a unit (0..1). 1 = no effect on attack/defense,
    /// 0 = fully stunned. The slow phase only reduces movement speed; attack/defense stay
    /// at full until the stun phase begins.
    /// </summary>
    public static float GetParalysisFraction(UnitArrays units, int unitIdx)
    {
        if (units[unitIdx].ParalysisStunTimer > 0f) return 0f;
        return 1f;
    }

    /// <summary>
    /// Tick all potion effects each frame. Called from Simulation.Tick().
    /// </summary>
    public static void TickPotionEffects(UnitArrays units, List<DamageEvent> damageEvents, float dt)
    {
        for (int i = 0; i < units.Count; i++)
        {
            // --- Paralysis slow phase ---
            // Movement speed lerps from ParalyzeSlowStartMultiplier (0.7x) down to 0 over
            // ParalyzeSlowDuration seconds. Attack/defense are unaffected in this phase.
            if (units[i].ParalysisSlowTimer > 0f)
            {
                units[i].ParalysisSlowTimer -= dt;
                float t = MathF.Max(units[i].ParalysisSlowTimer / ParalyzeSlowDuration, 0f);
                units[i].MaxSpeed *= ParalyzeSlowStartMultiplier * t;

                if (units[i].ParalysisSlowTimer <= 0f)
                {
                    // Transition to stun: lock movement, play Stunned anim, put attack on
                    // cooldown for the stun duration. GetParalysisFraction returns 0 during
                    // stun, so combat resolution zeroes Attack and Defense.
                    units[i].ParalysisSlowTimer = 0f;
                    units[i].ParalysisStunTimer = ParalyzeStunDuration;
                    units[i].AttackCooldown = ParalyzeStunDuration;
                    units[i].Incap = new IncapState
                    {
                        Active = true,
                        HoldAnim = AnimState.Stunned,
                        RecoverAnim = AnimState.Idle,
                        RecoverTime = 0f,
                        RecoverTimer = 0f,
                        HoldAtEnd = false,
                    };
                    units[i].OverrideAnim = new AnimRequest
                    {
                        State = AnimState.Stunned, Priority = 3, Interrupt = true,
                        Duration = -1, PlaybackSpeed = 1f
                    };
                }
            }

            // --- Paralysis stun phase ---
            if (units[i].ParalysisStunTimer > 0f)
            {
                units[i].ParalysisStunTimer -= dt;
                units[i].MaxSpeed = 0f;

                if (units[i].ParalysisStunTimer <= 0f)
                {
                    units[i].ParalysisStunTimer = 0f;
                    units[i].Incap = default; // clear incapacitation, unit recovers
                }
            }

            // --- Poison DoT ---
            // Poison ticks convert stacks to HP damage. This is the debuff's own
            // HP reduction — separate from the unified damage formula which *adds* stacks.
            if (units[i].PoisonStacks > 0)
            {
                units[i].PoisonTickTimer -= dt;
                if (units[i].PoisonTickTimer <= 0f)
                {
                    int dmg = (int)MathF.Ceiling(units[i].PoisonStacks / 10f);
                    units[i].PoisonStacks -= dmg;
                    if (units[i].PoisonStacks <= 0) units[i].PoisonStacks = 0;

                    // HP reduction bypasses armor (poison already got through armor when applied).
                    // Do NOT set HitReacting here — only the initial stack application should
                    // alert/flee AI. Ticking HitReacting would make poisoned units re-flee every 3s.
                    if (dmg > 0)
                    {
                        units[i].Stats.HP -= dmg;
                        DebugLog.Log("ai", $"[PoisonTick] unit#{i} dmg={dmg} stacks={units[i].PoisonStacks} " +
                            $"HP={units[i].Stats.HP} faction={units[i].Faction}");
                        damageEvents.Add(new DamageEvent
                        {
                            Position = units[i].Position,
                            Damage = dmg,
                            Height = 1.5f,
                            IsPoison = true
                        });
                        if (units[i].Stats.HP <= 0)
                        {
                            units[i].Stats.HP = 0;
                            units[i].Alive = false;
                        }
                    }

                    if (units[i].PoisonStacks > 0)
                        units[i].PoisonTickTimer = 3f;
                }
            }

            // --- Weapon coat timers ---
            if (units[i].WeaponPoisonCoatTimer > 0f)
            {
                units[i].WeaponPoisonCoatTimer -= dt;
                if (units[i].WeaponPoisonCoatTimer <= 0f)
                {
                    units[i].WeaponPoisonCoatTimer = 0f;
                    units[i].WeaponPoisonAmount = 0;
                }
            }

            if (units[i].WeaponZombieCoatTimer > 0f)
            {
                units[i].WeaponZombieCoatTimer -= dt;
                if (units[i].WeaponZombieCoatTimer <= 0f)
                    units[i].WeaponZombieCoatTimer = 0f;
            }
        }
    }

    /// <summary>
    /// Tick pending zombie raises. Called from Simulation.Tick().
    /// Uses Simulation.SpawnUnitByID for proper unit setup.
    /// </summary>
    public static void TickZombieRaises(List<PendingZombieRaise> raises, float dt,
        Action<string, Vec2, float, float> spawnZombie)
    {
        for (int i = raises.Count - 1; i >= 0; i--)
        {
            var r = raises[i];
            r.Timer -= dt;
            raises[i] = r;

            if (r.Timer <= 0f)
            {
                spawnZombie(r.UnitDefID, r.Position, r.FacingAngle, r.SpriteScale);
                raises.RemoveAt(i);
            }
        }
    }
}
