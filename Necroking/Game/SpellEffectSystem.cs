using System;
using System.Collections.Generic;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.Game;
using Necroking.Movement;
using Necroking.Spatial;

namespace Necroking.GameSystems;

/// <summary>Queued multi-projectile group for delayed spawning.</summary>
public struct PendingProjectileGroup
{
    public string SpellID;
    public Vec2 Origin;
    public Vec2 Target;
    public int Remaining;
    public float Timer;
    public float Interval;
}

/// <summary>Floating damage/pickup number for visual feedback.</summary>
public struct DamageNumber
{
    public Vec2 WorldPos;
    public int Damage;
    public float Timer;
    public string? PickupText;
    public float Height;
    public bool IsPoison;
    public bool IsFatigue;
    /// <summary>Render the PickupText as a warning (no "+" prefix, red colour).
    /// Used for things like "Horde Full" where the text isn't a pickup gain.</summary>
    public bool IsAlert;
}

/// <summary>
/// Result of executing a spell effect. Game1 reads these to update its own state.
/// </summary>
public struct SpellEffectResult
{
    /// <summary>Set to >= 0 when a channeling spell (Beam/Drain) started.</summary>
    public int ChannelingSlot;

    /// <summary>If non-null, a multi-projectile group to queue for delayed spawning.</summary>
    public PendingProjectileGroup? PendingProjectile;

    public static SpellEffectResult None => new() { ChannelingSlot = -1 };
}

/// <summary>
/// Handles execution of spell effects by category. Extracted from Game1.ExecuteSpellEffect
/// to decouple spell logic from the main game loop.
/// </summary>
public class SpellEffectSystem
{
    /// <summary>
    /// Execute a spell effect. Returns what Game1 needs to update in its own state.
    /// </summary>
    /// <param name="spell">Spell definition</param>
    /// <param name="sim">Simulation (for units, lightning, clouds, quadtree)</param>
    /// <param name="gameData">Game data (for buffs, unit defs)</param>
    /// <param name="casterIdx">Caster unit index</param>
    /// <param name="target">World-space target position</param>
    /// <param name="slot">Spell bar slot index (for channeling)</param>
    /// <param name="damageNumbers">Visual damage numbers list</param>
    /// <param name="spawnProjectile">Callback to spawn a projectile (needs Game1 rendering state).
    /// Signature: (spell, origin, target, ownerUid, spawnHeight).</param>
    /// <param name="executeSummon">Callback for summon spells (deeply coupled to Game1)</param>
    /// <param name="applyBlight">Callback for Blight spells — mutates the death-fog
    /// field, which is Game1 state. Signature: (spell, target).</param>
    public SpellEffectResult Execute(
        SpellDef spell, Simulation sim, GameData gameData,
        int casterIdx, Vec2 target, int slot,
        List<DamageNumber> damageNumbers,
        Action<SpellDef, Vec2, Vec2, uint, float> spawnProjectile,
        Action<SpellDef, int> executeSummon,
        Action<SpellDef, Vec2> applyBlight)
    {
        var result = SpellEffectResult.None;
        var units = sim.UnitsMut;
        var casterPos = units[casterIdx].Position;
        var casterUid = units[casterIdx].Id;
        var effectOrigin = units[casterIdx].EffectSpawnPos2D;
        var effectOriginH = units[casterIdx].EffectSpawnHeight;

        switch (spell.Category)
        {
            case "Projectile":
                spawnProjectile(spell, effectOrigin, target, casterUid, effectOriginH);
                if (spell.Quantity > 1)
                {
                    result.PendingProjectile = new PendingProjectileGroup
                    {
                        SpellID = spell.Id,
                        Origin = effectOrigin,
                        Target = target,
                        Remaining = spell.Quantity - 1,
                        Timer = 0f,
                        Interval = spell.ProjectileDelay > 0f ? spell.ProjectileDelay : 0.1f
                    };
                }
                break;

            case "Buff":
            case "Debuff":
                BuffSystem.ApplyBuffById(units, casterIdx, gameData.Buffs, spell.BuffID);
                break;

            case "Strike":
                ExecuteStrike(spell, sim, gameData, casterIdx, target, effectOrigin, damageNumbers);
                break;

            case "Summon":
                executeSummon(spell, casterIdx);
                break;

            case "Beam":
            {
                int beamTarget = FindClosestEnemy(sim, target, 3f, units[casterIdx].Faction);
                if (beamTarget >= 0)
                {
                    sim.Lightning.SpawnBeam(casterUid, units[beamTarget].Id,
                        spell.Id, spell.Damage, spell.BeamTickRate, spell.BeamRetargetRadius,
                        spell.BuildBeamStyle());
                    result.ChannelingSlot = slot;
                }
                break;
            }

            case "Drain":
            {
                int drainTarget = FindClosestEnemy(sim, target, 5f, units[casterIdx].Faction);
                if (drainTarget >= 0)
                {
                    sim.Lightning.SpawnDrain(casterUid, units[drainTarget].Id,
                        spell.Id, spell.Damage, spell.DrainTickRate, spell.DrainHealPercent,
                        spell.DrainCorpseHP, spell.DrainReversed, spell.DrainMaxDuration,
                        spell.BuildDrainVisuals());
                    result.ChannelingSlot = slot;
                }
                break;
            }

            // "Command" is no longer a spell category — the order_attack ability is a
            // built-in (Game1.TryCommandHorde) so the category can host more uses.

            case "Sacrifice":
                ExecuteSacrifice(spell, sim, casterIdx, target, damageNumbers);
                break;

            case "Cloud":
                ExecuteCloud(spell, sim, target);
                break;

            case "WolfHunt":
                // Point the player's zombie wolves at the herd near the cast point: they flank to the
                // far side and drive it toward the necromancer. The behavior lives in AI.WolfPackHuntAI;
                // this just arms the sim-level command for the spell's duration.
                sim.CommandWolfHunt(target, spell.WolfHuntDuration > 0f ? spell.WolfHuntDuration : 18f);
                break;

            case "Blight":
            {
                // Mutate the death-fog ("blight") field — Add dumps blight at the
                // target cell, Purify cleanses a 5×5 kernel. The field lives in Game1.
                //
                // Visual: reuse the Strike / Projectile renderers so a Blight spell can
                // present as a god-ray (StrikeVisual=GodRay) or a thrown bomb
                // (ProjectileFlipbook set), driven entirely by the def. Damage stays 0
                // — the fog change is the real effect.
                bool hasBomb = spell.StrikeVisualType != "GodRay"
                    && spell.ProjectileFlipbook != null
                    && !string.IsNullOrEmpty(spell.ProjectileFlipbook.FlipbookID);

                // With a thrown bomb, the fog is applied where/when the bomb explodes
                // (Game1 reads the projectile impact by spell id). Without one, apply it
                // now at the target point.
                if (!hasBomb)
                    applyBlight(spell, target);

                if (spell.StrikeVisualType == "GodRay")
                {
                    ExecuteStrike(spell, sim, gameData, casterIdx, target, effectOrigin, damageNumbers);
                }
                else if (hasBomb)
                {
                    spawnProjectile(spell, effectOrigin, target, casterUid, effectOriginH);
                    if (spell.Quantity > 1)
                    {
                        result.PendingProjectile = new PendingProjectileGroup
                        {
                            SpellID = spell.Id,
                            Origin = effectOrigin,
                            Target = target,
                            Remaining = spell.Quantity - 1,
                            Timer = 0f,
                            Interval = spell.ProjectileDelay > 0f ? spell.ProjectileDelay : 0.1f
                        };
                    }
                }
                break;
            }

            case "Toggle":
                if (spell.ToggleEffect == "ghost_mode")
                    units[casterIdx].GhostMode = !units[casterIdx].GhostMode;
                break;
        }

        return result;
    }

    private void ExecuteStrike(SpellDef spell, Simulation sim, GameData gameData,
        int casterIdx, Vec2 target, Vec2 effectOrigin, List<DamageNumber> damageNumbers)
    {
        var units = sim.UnitsMut;
        var style = spell.BuildStrikeStyle();
        var sVis = spell.StrikeVisualType == "GodRay" ? StrikeVisual.GodRay : StrikeVisual.Lightning;
        var sGrp = spell.BuildGodRayParams();
        Enum.TryParse<SpellTargetFilter>(spell.TargetFilter, out var sTF);

        if (spell.StrikeTargetUnit)
        {
            float casterH = units[casterIdx].EffectSpawnHeight;
            int enemy = FindClosestEnemy(sim, target, spell.Range, units[casterIdx].Faction);
            if (enemy >= 0)
            {
                var targetPos = units[enemy].Position;
                float targetH = 1.0f;
                var tDef = gameData.Units.Get(units[enemy].UnitDefID);
                if (tDef != null) targetH = tDef.SpriteWorldHeight * 0.5f;

                sim.Lightning.SpawnZap(effectOrigin, targetPos,
                    spell.ZapDuration > 0 ? spell.ZapDuration : spell.StrikeDuration,
                    style, casterH, targetH);
                // Magic-resistance gate: an MR-checked strike only lands if it
                // penetrates the target's MR.
                if (SpellPenetration.Affects(gameData, units, casterIdx, enemy, spell))
                {
                    sim.DealDamage(enemy, spell.Damage, casterIdx);
                    damageNumbers.Add(new DamageNumber
                    {
                        WorldPos = targetPos, Damage = spell.Damage,
                        Timer = 0f, Height = targetH
                    });
                }
            }
        }
        else
        {
            sim.Lightning.SpawnStrike(target, spell.TelegraphDuration,
                spell.StrikeDuration, spell.AoeRadius, spell.Damage,
                style, spell.Id, sVis, sGrp, sTF, spell.TelegraphVisible,
                units[casterIdx].Id);
        }
    }

    public void ExecuteCloud(SpellDef spell, Simulation sim, Vec2 target)
    {
        sim.PoisonClouds.SpawnCloud(target, spell, Faction.Undead);

        float radius = spell.AoeRadius > 0 ? spell.AoeRadius : spell.CloudRadius;

        if (spell.CloudAppliesParalysis)
        {
            // Paralysis clouds use their AoE burst to apply paralysis (no poison stacks).
            PotionSystem.ApplyParalysisAoE(sim.UnitsMut, sim.Quadtree, target, radius, Faction.Undead);
            return;
        }

        if (spell.Damage > 0)
        {
            var flags = SpellDamageFlags(spell);
            DamageSystem.ApplyAoE(sim.UnitsMut, sim.Quadtree, target, radius,
                spell.Damage, DamageType.Poison, flags, Faction.Undead, sim.DamageEventsMut);
        }
    }

    /// <summary>Build DamageFlags from a spell's AN/DN settings.</summary>
    private static DamageFlags SpellDamageFlags(SpellDef spell)
    {
        var flags = DamageFlags.None;
        if (spell.ArmorNegating) flags |= DamageFlags.ArmorNegating;
        if (spell.DefenseNegating) flags |= DamageFlags.DefenseNegating;
        return flags;
    }

    /// <summary>Sacrifice the friendly undead nearest the target point: it dies
    /// (crumbles into a corpse, attributed to the caster) and the caster is healed
    /// by HealFlat + HealPercent × the victim's effective max HP, clamped to the
    /// caster's own effective max HP. A floating "+N" reports the gain.</summary>
    private void ExecuteSacrifice(SpellDef spell, Simulation sim, int casterIdx,
        Vec2 target, List<DamageNumber> damageNumbers)
    {
        var units = sim.UnitsMut;
        int victim = FindClosestAlly(sim, target, 5f, units[casterIdx].Faction, casterIdx);
        if (victim < 0) return;

        int victimMaxHp = BuffSystem.EffectiveMaxHP(units, victim);
        int heal = spell.SacrificeHealFlat + (int)MathF.Round(spell.SacrificeHealPercent * victimMaxHp);
        if (heal < 0) heal = 0;

        int casterMax = BuffSystem.EffectiveMaxHP(units, casterIdx);
        int before = units[casterIdx].Stats.HP;
        units[casterIdx].Stats.HP = Math.Min(casterMax, before + heal);
        int gained = units[casterIdx].Stats.HP - before;

        if (gained > 0)
        {
            damageNumbers.Add(new DamageNumber
            {
                WorldPos = units[casterIdx].Position,
                PickupText = gained.ToString(),
                Timer = 0f,
                Height = units[casterIdx].EffectSpawnHeight,
            });
        }

        // The victim crumbles into a corpse — the visible "sacrifice".
        sim.DealDamage(victim, 999999, casterIdx);
    }

    /// <summary>Find the closest same-faction unit to a point within range,
    /// excluding one index (the caster).</summary>
    private static int FindClosestAlly(Simulation sim, Vec2 point, float range,
        Faction faction, int excludeIdx)
    {
        int best = -1;
        float bestDist = range * range;
        for (int i = 0; i < sim.Units.Count; i++)
        {
            if (i == excludeIdx) continue;
            if (!sim.Units[i].Alive || sim.Units[i].Faction != faction) continue;
            float d = (point - sim.Units[i].Position).LengthSq();
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    /// <summary>Find closest enemy unit to a point within range.</summary>
    private static int FindClosestEnemy(Simulation sim, Vec2 point, float range, Faction casterFaction)
    {
        int best = -1;
        float bestDist = range * range;
        for (int i = 0; i < sim.Units.Count; i++)
        {
            if (!sim.Units[i].Alive || sim.Units[i].Faction == casterFaction) continue;
            float d = (point - sim.Units[i].Position).LengthSq();
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best;
    }
}
