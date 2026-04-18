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
    /// <param name="spawnProjectile">Callback to spawn a projectile (needs Game1 rendering state)</param>
    /// <param name="executeSummon">Callback for summon spells (deeply coupled to Game1)</param>
    public SpellEffectResult Execute(
        SpellDef spell, Simulation sim, GameData gameData,
        int casterIdx, Vec2 target, int slot,
        List<DamageNumber> damageNumbers,
        Action<SpellDef, Vec2, Vec2, uint> spawnProjectile,
        Action<SpellDef, int> executeSummon)
    {
        var result = SpellEffectResult.None;
        var units = sim.UnitsMut;
        var casterPos = units[casterIdx].Position;
        var casterUid = units[casterIdx].Id;
        var effectOrigin = units[casterIdx].EffectSpawnPos2D;

        switch (spell.Category)
        {
            case "Projectile":
                spawnProjectile(spell, effectOrigin, target, casterUid);
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
                if (!string.IsNullOrEmpty(spell.BuffID))
                {
                    var buffDef = gameData.Buffs.Get(spell.BuffID);
                    if (buffDef != null)
                        BuffSystem.ApplyBuff(units, casterIdx, buffDef);
                }
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

            case "Command":
                ExecuteCommand(sim, target);
                break;

            case "Cloud":
                ExecuteCloud(spell, sim, target);
                break;

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
                sim.DealDamage(enemy, spell.Damage);
                damageNumbers.Add(new DamageNumber
                {
                    WorldPos = targetPos, Damage = spell.Damage,
                    Timer = 0f, Height = targetH
                });
            }
        }
        else
        {
            sim.Lightning.SpawnStrike(target, spell.TelegraphDuration,
                spell.StrikeDuration, spell.AoeRadius, spell.Damage,
                style, spell.Id, sVis, sGrp, sTF, spell.TelegraphVisible);
        }
    }

    private void ExecuteCommand(Simulation sim, Vec2 target)
    {
        var units = sim.UnitsMut;
        for (int ci = 0; ci < units.Count; ci++)
        {
            if (!units[ci].Alive) continue;
            if (units[ci].Faction != Faction.Undead) continue;
            if (units[ci].Archetype != AI.ArchetypeRegistry.HordeMinion) continue;

            units[ci].Routine = 4;
            units[ci].Subroutine = 0;
            units[ci].SubroutineTimer = 0f;
            units[ci].MoveTarget = target;
            units[ci].Target = CombatTarget.None;
            units[ci].EngagedTarget = CombatTarget.None;
        }
    }

    public void ExecuteCloud(SpellDef spell, Simulation sim, Vec2 target)
    {
        sim.PoisonClouds.SpawnCloud(target, spell, Faction.Undead);

        float radius = spell.AoeRadius > 0 ? spell.AoeRadius : spell.CloudRadius;

        if (spell.CloudAppliesParalysis)
        {
            // Paralysis clouds use their AoE burst to apply paralysis (no poison stacks).
            var nearbyIDs = new List<uint>();
            sim.Quadtree.QueryRadius(target, radius, nearbyIDs);
            foreach (uint uid in nearbyIDs)
            {
                int idx = UnitUtil.ResolveUnitIndex(sim.UnitsMut, uid);
                if (idx < 0 || !sim.Units[idx].Alive) continue;
                if (sim.Units[idx].Faction == Faction.Undead) continue;
                PotionSystem.ApplyParalysis(idx, sim.UnitsMut);
            }
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
