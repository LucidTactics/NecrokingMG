using System;
using System.Collections.Generic;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.GameSystems;
using Necroking.Movement;
using Necroking.Render;
using Necroking.Spatial;

namespace Necroking.Game;

public struct PendingZombieRaise
{
    public Vec2 Position;
    public string UnitDefID;
    public float FacingAngle;
    public float SpriteScale;
    public int CorpseId;       // source corpse (-1 = none) so the composite reanim morph can target it
    public System.Action<int>? OnSpawned;  // runs on the spawned unit when it rises (e.g. crafted item bonuses)
    public float Timer;

    /// <summary>Default delay (seconds) before a queued raise actually rises.</summary>
    public const float DefaultRiseDelay = 1.0f;

    /// <summary>Build a pending raise from a position/identity (corpse or dying unit),
    /// using the standard rise delay — centralizes the 1.0s magic number.</summary>
    public static PendingZombieRaise At(Vec2 pos, string defId, float facing, float scale, int corpseId = -1, float timer = DefaultRiseDelay)
        => new() { Position = pos, UnitDefID = defId, FacingAngle = facing, SpriteScale = scale, CorpseId = corpseId, Timer = timer };
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

        // Throw projectile — arc starts at the thrower's animation-driven hand tip.
        uint necroUid = units[necroIdx].Id;
        float speed = ProjectileManager.MagicSpeed * 0.7f; // potions are slower than spell shots
        var proj = projectiles.Spawn(necroPos, mouseWorld, units[necroIdx].Faction, necroUid,
            ProjectileType.Potion, damage: 0, speed, lob: true, aoeRadius: 1.0f,
            spawnHeight: units[necroIdx].EffectSpawnHeight);

        // Potion payload + visuals
        proj.PotionID = potionId;
        proj.ParticleScale = potion.ProjectileScale;
        // Set icon texture path for potion sprite rendering
        proj.IconTexturePath = potion.Icon;
        proj.HitsCorpses = potion.HitsCorpses;
        proj.PotionTargetType = potion.TargetType;
        if (potion.ProjectileFlipbook != null)
        {
            proj.FlipbookID = potion.ProjectileFlipbook.FlipbookID;
            proj.ParticleScale = potion.ProjectileFlipbook.Scale;
            proj.ParticleColor = potion.ProjectileFlipbook.Color;
        }
        if (potion.HitEffectFlipbook != null)
        {
            proj.HitEffectFlipbookID = potion.HitEffectFlipbook.FlipbookID;
            proj.HitEffectScale = potion.HitEffectFlipbook.Scale;
            proj.HitEffectColor = potion.HitEffectFlipbook.Color;
            proj.HitEffectBlendMode = potion.HitEffectFlipbook.BlendMode == "Additive" ? 1 : 0;
            proj.HitEffectAlignment = potion.HitEffectFlipbook.Alignment == "Upright" ? 1 : 0;
        }

        inventory.RemoveItem(potion.ItemID, 1);
        return true;
    }

    /// <summary>
    /// Apply a potion effect on hit. Called when a potion projectile lands.
    ///
    /// Dispatches on PotionDef.OnHitEffect. Each branch has different side effects;
    /// callers that pass optional out-lists need to know which branch writes what:
    ///   "Frenzy"    — sets Frenzied flag + stacks the permanent buff on the hit unit.
    ///   "Paralysis" — starts the slow→stun sequence (timers on the hit unit).
    ///   "Zombie"    — friendlies get a 5-min weapon coat; enemies get ZombieOnDeath;
    ///                 corpses hit directly are queued into pendingRaises.
    ///   "Poison"    — friendlies get a weapon coat; enemies take 10 stacks via
    ///                 DamageSystem.Apply, which *appends to damageEvents* if
    ///                 non-null (this is the only branch that touches damageEvents).
    ///   (default)   — plain BuffSystem.ApplyBuff of PotionDef.BuffID if set.
    ///
    /// pendingRaises and corpses are only read by the Zombie branch; other branches
    /// ignore them. Safe to pass empty lists on non-Zombie calls.
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
                BuffSystem.ApplyBuffById(units, hitUnitIdx, buffs, potion.BuffID);
                break;
        }
    }

    private static void ApplyFrenzy(PotionDef potion, BuffRegistry buffs, int unitIdx, UnitArrays units)
    {
        if (unitIdx < 0 || unitIdx >= units.Count) return;

        // Apply frenzy buff (permanent). Duration <= 0 at apply time means
        // "permanent until explicitly removed" (see ApplyBuffWithDuration).
        if (!string.IsNullOrEmpty(potion.BuffID))
        {
            var buffDef = buffs.Get(potion.BuffID);
            if (buffDef != null)
                BuffSystem.ApplyBuffWithDuration(units, unitIdx, buffDef, 0f);
        }

        // Set frenzy behavior flag
        units[unitIdx].Frenzied = true;
    }

    private static void ApplyParalysis(PotionDef potion, BuffRegistry buffs, int unitIdx, UnitArrays units)
    {
        ApplyParalysis(unitIdx, units);
        BuffSystem.ApplyBuffById(units, unitIdx, buffs, potion.BuffID);
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

    /// <summary>
    /// Apply paralysis to every non-owner unit inside a radius. Skips the owner faction
    /// (no friendly fire). Mirrors DamageSystem.ApplyAoE but for paralysis clouds.
    /// </summary>
    public static void ApplyParalysisAoE(UnitArrays units, Quadtree qt,
        Vec2 center, float radius, Faction ownerFaction)
    {
        var nearbyIDs = new List<uint>();
        qt.QueryRadiusByFaction(center, radius, FactionMaskExt.AllExcept(ownerFaction), nearbyIDs);
        foreach (uint uid in nearbyIDs)
        {
            int idx = UnitUtil.ResolveUnitIndex(units, uid);
            if (idx < 0 || !units[idx].Alive) continue;
            ApplyParalysis(idx, units);
        }
    }

    private static void ApplyZombie(PotionDef potion, BuffRegistry buffs, int unitIdx, UnitArrays units,
        Faction ownerFaction, List<PendingZombieRaise> pendingRaises, List<Corpse> corpses, Vec2 impactPos)
    {
        // No unit hit and no corpse hit (corpse hits are handled directly in Simulation via CorpseHitIdx)
        if (unitIdx < 0) return;

        // Hit a unit
        if (units[unitIdx].Faction == ownerFaction)
        {
            // Friendly: coat weapons with zombie curse (timed on-hit bonus effect)
            WeaponBonusEffectSystem.Add(units, unitIdx,
                WeaponBonusEffect.ZombieOnDeath(100) with { Permanent = false, ExpiryTimer = WeaponCoatDuration });

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
            // Friendly: coat weapons with poison (timed on-hit bonus effect).
            // Weapon poison goes through armor (no ArmorNegating flag).
            WeaponBonusEffectSystem.Add(units, unitIdx,
                WeaponBonusEffect.Damage(DamageType.Poison, 5) with { Permanent = false, ExpiryTimer = WeaponCoatDuration });
            BuffSystem.ApplyBuffById(units, unitIdx, buffs, potion.BuffID);
        }
        else
        {
            // Enemy: apply 10 poison stacks through damage system (potions bypass armor)
            var dmgEvents = damageEvents ?? new List<DamageEvent>();
            DamageSystem.Apply(units, unitIdx, 10,
                DamageType.Poison, DamageFlags.ArmorNegating, dmgEvents);
            BuffSystem.ApplyBuffById(units, unitIdx, buffs, potion.BuffID);
        }
    }

    /// <summary>How long a potion weapon coat (poison/zombie) lasts, in seconds.</summary>
    public const float WeaponCoatDuration = 300f; // 5 minutes

    // Paralysis timings (seconds).
    public const float ParalyzeSlowDuration = 8f;
    public const float ParalyzeStunDuration = 6f;
    // Speed multiplier at the start of the slow phase — the curve lerps this to 0 over
    // ParalyzeSlowDuration. Public: the actual MaxSpeed reduction is applied by
    // Movement.Locomotion.UpdateSpeeds (the single MaxSpeed writer) from these constants
    // + the unit's ParalysisSlowTimer; this system only owns the timers/transitions.
    public const float ParalyzeSlowStartMultiplier = 0.7f;

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
    /// <summary>The Incapacitating buff that owns the stun phase's Incap state + Stunned
    /// anim hold (see data/buffs.json). Applied with ParalyzeStunDuration as override
    /// duration so the timing has a single source of truth (the const above). BuffSystem
    /// is the sole writer of Unit.Incap; this system keeps only the timers (speed curve,
    /// stat fraction) which are structural to paralysis.</summary>
    public const string ParalysisStunBuffID = "buff_paralysis_stun";

    public static void TickPotionEffects(UnitArrays units, List<DamageEvent> damageEvents, BuffRegistry? buffs, float dt)
    {
        for (int i = 0; i < units.Count; i++)
        {
            // --- Paralysis slow phase ---
            // Movement speed lerps from ParalyzeSlowStartMultiplier (0.7x) down to 0 over
            // ParalyzeSlowDuration seconds (applied by Locomotion.UpdateSpeeds from the
            // timer). Attack/defense are unaffected in this phase.
            if (units[i].ParalysisSlowTimer > 0f)
            {
                units[i].ParalysisSlowTimer -= dt;

                if (units[i].ParalysisSlowTimer <= 0f)
                {
                    // Transition to stun: lock movement, play Stunned anim, put attack on
                    // cooldown for the stun duration. GetParalysisFraction returns 0 during
                    // stun, so combat resolution zeroes Attack and Defense.
                    units[i].ParalysisSlowTimer = 0f;
                    units[i].ParalysisStunTimer = ParalyzeStunDuration;
                    units[i].AttackCooldown = ParalyzeStunDuration;
                    // Incap state + Stunned anim hold come from the Incapacitating buff —
                    // BuffSystem owns Unit.Incap (apply, hold, release on expiry). The buff's
                    // incapRecoverTime is 0 => instant recovery, matching the old inline
                    // "Incap = default at stun end" behavior.
                    var stunDef = buffs?.Get(ParalysisStunBuffID);
                    if (stunDef != null)
                        BuffSystem.ApplyBuffWithDuration(units, i, stunDef, ParalyzeStunDuration);
                    else
                        DebugLog.Log("combat", $"[PotionSystem] '{ParalysisStunBuffID}' missing from BuffRegistry — stun phase has no incap/anim");
                }
            }

            // --- Paralysis stun phase ---
            // (MaxSpeed = 0 during stun is applied by Locomotion.UpdateSpeeds.)
            // `else if`: no decrement on the transition frame, so this timer stays in
            // lockstep with buff_paralysis_stun's RemainingDuration (whose first TickBuffs
            // decrement is also next frame) and both expire on the same frame.
            else if (units[i].ParalysisStunTimer > 0f)
            {
                units[i].ParalysisStunTimer -= dt;

                if (units[i].ParalysisStunTimer <= 0f)
                {
                    units[i].ParalysisStunTimer = 0f;
                    // Incap clears via buff_paralysis_stun expiry in BuffSystem.TickBuffs
                    // (same tick — both timers start at ParalyzeStunDuration).
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
                        damageEvents.Add(DamageEvent.Create(units[i].RenderPos, dmg, isPoison: true));
                        // Death finalization through the one sanctioned path (plays
                        // the Death anim + prone-snap, which the old inline block missed).
                        if (units[i].Stats.HP <= 0)
                            DamageSystem.Kill(units, i);
                    }

                    if (units[i].PoisonStacks > 0)
                        units[i].PoisonTickTimer = 3f;
                }
            }
        }
    }

    /// <summary>
    /// Tick pending zombie raises. Called from Simulation.Tick().
    /// Uses Simulation.SpawnUnitByID for proper unit setup.
    /// </summary>
    public static void TickZombieRaises(List<PendingZombieRaise> raises, float dt,
        Action<PendingZombieRaise> onReady)
    {
        for (int i = raises.Count - 1; i >= 0; i--)
        {
            var r = raises[i];
            r.Timer -= dt;
            raises[i] = r;

            if (r.Timer <= 0f)
            {
                onReady(r);
                raises.RemoveAt(i);
            }
        }
    }
}
