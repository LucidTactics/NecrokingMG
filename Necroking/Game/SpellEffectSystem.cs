using System;
using System.Collections.Generic;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.Game;
using Necroking.Movement;
using Necroking.Spatial;

namespace Necroking.GameSystems;

/// <summary>Queued multi-projectile group for delayed spawning. Carries its caster's
/// stable uid so follow-up shots track THAT caster's hand (player or AI) — the group
/// is dropped if the caster dies mid-volley.</summary>
public struct PendingProjectileGroup
{
    public string SpellID;
    public uint CasterUid;
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
/// Canonical spawn helpers for floating text (<see cref="DamageNumber"/>).
/// The Height-anchor trap (documented in docs/locate-behavior/render.md):
/// WorldToScreen lifts by Height*Zoom*YRatio, but sprites draw
/// SpriteWorldHeight*SpriteScale*Zoom pixels tall (no YRatio) — so
/// <see cref="HeadHeight"/> is the ONE place the sprite-height → lift-units
/// conversion lives. Spawn through these instead of inline
/// <c>list.Add(new DamageNumber { ... })</c> so height conventions can't drift.
/// </summary>
public static class FloatingText
{
    /// <summary>Head height of a unit in DamageNumber.Height lift units:
    /// Z + SpriteWorldHeight*SpriteScale / yRatio (1.8f default sprite height).
    /// The old constant 2f landed mid-sprite for want of the YRatio divide.</summary>
    public static float HeadHeight(Unit u, UnitDef? def, float yRatio)
    {
        float spriteWorldH = (def != null && def.SpriteWorldHeight > 0 ? def.SpriteWorldHeight : 1.8f)
                             * u.SpriteScale;
        return u.Z + spriteWorldH / yRatio;
    }

    /// <summary>Floating damage number at a world position.</summary>
    public static void AddDamage(List<DamageNumber> list, Vec2 pos, int damage,
        float height, bool poison = false, bool fatigue = false)
        => list.Add(new DamageNumber
        {
            WorldPos = pos, Damage = damage, Timer = 0f, Height = height,
            IsPoison = poison, IsFatigue = fatigue,
        });

    /// <summary>Floating text: green "+text" pickup gain by default, or a red
    /// warning (no "+" prefix) when <paramref name="alert"/> is set.</summary>
    public static void AddText(List<DamageNumber> list, Vec2 pos, string text,
        float height, bool alert = false)
        => list.Add(new DamageNumber
        {
            WorldPos = pos, PickupText = text, Timer = 0f, Height = height,
            IsAlert = alert,
        });
}

/// <summary>
/// Owns ALL spell-category effect logic. Takes Game1 directly (same pattern as
/// GameRenderer/ForagableSystem) and reaches sim + presentation state through it —
/// Game1-owned results (channeling slot, pending projectile groups, reanim rises)
/// are written straight onto the game instance / enqueued via its internal methods.
/// </summary>
public static class SpellEffectSystem
{
    /// <summary>
    /// Execute a spell effect by category — for ANY caster, player or AI.
    /// </summary>
    /// <param name="spell">Spell definition</param>
    /// <param name="game">The game — sim, game data, death fog, damage numbers,
    /// channeling/pending-projectile state, and the summon/reanim pipeline.</param>
    /// <param name="casterIdx">Caster unit index</param>
    /// <param name="target">World-space target position</param>
    /// <param name="slot">Spell bar slot index for the player's hold-to-channel
    /// (Beam/Drain). Pass -1 for AI casts: their channels run on a per-unit timer
    /// (Unit.ChannelTimer) instead of a held key.</param>
    /// <param name="pending">The targeting results TryStartSpellCast wrote for THIS
    /// cast (corpse/unit ids, summon type). Per-caster — the player passes
    /// Game1._pendingSpell, AI casts pass their own record, so concurrent casters
    /// can't stomp each other's targeting.</param>
    public static void Execute(SpellDef spell, Game1 game, int casterIdx, Vec2 target, int slot,
        PendingSpellCast pending)
    {
        var sim = game._sim;
        var gameData = game._gameData;
        var units = sim.UnitsMut;
        var casterUid = units[casterIdx].Id;
        var casterFaction = units[casterIdx].Faction;
        var effectOrigin = units[casterIdx].EffectSpawnPos2D;
        var effectOriginH = units[casterIdx].EffectSpawnHeight;

        switch (spell.Category)
        {
            case "Projectile":
                SpawnProjectile(spell, sim.Projectiles, effectOrigin,
                    VolleyAimPoint(spell, target, game._rng), casterUid,
                    effectOriginH, casterFaction);
                if (spell.Quantity > 1)
                {
                    game._pendingProjectiles.Add(new PendingProjectileGroup
                    {
                        SpellID = spell.Id,
                        CasterUid = casterUid,
                        Origin = effectOrigin,
                        Target = target,
                        Remaining = spell.Quantity - 1,
                        Timer = 0f,
                        Interval = spell.ProjectileDelay > 0f ? spell.ProjectileDelay : 0.1f
                    });
                }
                break;

            case "Buff":
            case "Debuff":
                BuffSystem.ApplyBuffById(units, casterIdx, gameData.Buffs, spell.BuffID);
                break;

            case "Strike":
                ExecuteStrike(spell, sim, gameData, casterIdx, target, effectOrigin, game._damageNumbers);
                break;

            case "Summon":
                ExecuteSummonSpell(spell, game, pending, units[casterIdx].Position, casterIdx);
                break;

            case "Beam":
            {
                int beamTarget = FindClosestEnemy(sim, target, 3f, casterFaction);
                if (beamTarget >= 0)
                {
                    sim.Lightning.SpawnBeam(casterUid, units[beamTarget].Id,
                        spell.Id, spell.Damage, spell.BeamTickRate, spell.BeamRetargetRadius,
                        spell.BuildBeamStyle());
                    StartChannel(game, units, casterIdx, slot, spell);
                }
                break;
            }

            case "Drain":
            {
                int drainTarget = FindClosestEnemy(sim, target, 5f, casterFaction);
                if (drainTarget >= 0)
                {
                    sim.Lightning.SpawnDrain(casterUid, units[drainTarget].Id,
                        spell.Id, spell.Damage, spell.DrainTickRate, spell.DrainHealPercent,
                        spell.DrainCorpseHP, spell.DrainReversed, spell.DrainMaxDuration,
                        spell.BuildDrainVisuals());
                    StartChannel(game, units, casterIdx, slot, spell);
                }
                break;
            }

            // "Command" is no longer a spell category — the order_attack ability is a
            // built-in (Game1.TryCommandHorde) so the category can host more uses.

            case "Sacrifice":
                ExecuteSacrifice(spell, sim, casterIdx, target, game._damageNumbers);
                break;

            case "Cloud":
                ExecuteCloud(spell, sim, target, casterFaction);
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
                // target cell, Purify cleanses a 5×5 kernel.
                //
                // Visual: reuse the Strike / Projectile renderers so a Blight spell can
                // present as a god-ray (StrikeVisual=GodRay) or a thrown bomb
                // (ProjectileFlipbook set), driven entirely by the def. Damage stays 0
                // — the fog change is the real effect.
                bool hasBomb = spell.StrikeVisualType != "GodRay"
                    && spell.ProjectileFlipbook != null
                    && !string.IsNullOrEmpty(spell.ProjectileFlipbook.FlipbookID);

                // With a thrown bomb, the fog is applied where/when the bomb explodes
                // (Game1.ApplyBlightBombImpacts reads the projectile impact by spell id).
                // Without one, apply it now at the target point.
                if (!hasBomb)
                    ApplyBlight(spell, target, game._deathFog);

                if (spell.StrikeVisualType == "GodRay")
                {
                    ExecuteStrike(spell, sim, gameData, casterIdx, target, effectOrigin, game._damageNumbers);
                }
                else if (hasBomb)
                {
                    SpawnProjectile(spell, sim.Projectiles, effectOrigin, target, casterUid,
                        effectOriginH, casterFaction);
                    if (spell.Quantity > 1)
                    {
                        game._pendingProjectiles.Add(new PendingProjectileGroup
                        {
                            SpellID = spell.Id,
                            CasterUid = casterUid,
                            Origin = effectOrigin,
                            Target = target,
                            Remaining = spell.Quantity - 1,
                            Timer = 0f,
                            Interval = spell.ProjectileDelay > 0f ? spell.ProjectileDelay : 0.1f
                        });
                    }
                }
                break;
            }

            case "Toggle":
                if (spell.ToggleEffect == "ghost_mode")
                    units[casterIdx].GhostMode = !units[casterIdx].GhostMode;
                break;
        }
    }

    /// <summary>Arm the channel-hold for a just-started Beam/Drain. Player casts
    /// (slot &gt;= 0) are released by letting go of the spell-bar key (Game1's
    /// channel-hold block); AI casts (slot &lt; 0) run on a per-unit timer that the
    /// caster's archetype handler ticks down and cancels — an AI has no key to
    /// release. Drains prefer their own max duration so the channel matches the
    /// visual; CastTime is the generic fallback knob.</summary>
    private static void StartChannel(Game1 game, UnitArrays units, int casterIdx, int slot, SpellDef spell)
    {
        if (slot >= 0)
        {
            game._channelingSlot = slot;
            return;
        }
        float duration = spell.Category == "Drain" && spell.DrainMaxDuration > 0f
            ? spell.DrainMaxDuration
            : (spell.CastTime > 0f ? spell.CastTime : 4f);
        units[casterIdx].ChannelTimer = duration;
    }

    /// <summary>
    /// Execute the full summon spell logic matching C++ Simulation::executeSpellCast for Summon category.
    /// Handles all SummonTargetReq types: None, Corpse, UnitType, CorpseAOE.
    /// Handles SummonMode: Spawn, Transform.
    /// </summary>
    private static void ExecuteSummonSpell(SpellDef spell, Game1 game, PendingSpellCast pending,
        Vec2 necroPos, int necroIdx)
    {
        var sim = game._sim;
        var gameData = game._gameData;

        if (spell.SummonTargetReq == "CorpseAOE") {
            // AOE corpse raise: iterate corpses in range, resolve zombie type per corpse
            int raised = 0;
            for (int i = 0; i < sim.Corpses.Count && raised < spell.SummonQuantity; i++) {
                var corpse = sim.Corpses[i];
                if (corpse.Dissolving || corpse.ConsumedBySummon) continue;
                if (corpse.DraggedByUnitID != GameConstants.InvalidUnit) continue;
                if (corpse.BaggedByUnitID != GameConstants.InvalidUnit) continue; // mid-bagging
                float dist = (corpse.Position - pending.TargetPos).Length();
                if (dist > spell.AoeRadius) continue;

                // Resolve zombie type from corpse's UnitDef. Shared with
                // TableCraftingSystem so the same source corpse always raises
                // into the same unit class regardless of how it was triggered.
                string resolvedID = TableCraftingSystem.ResolveZombieUnitID(gameData, corpse.UnitDefID);
                if (string.IsNullOrEmpty(resolvedID)) continue;

                // Per-corpse cap check — AOE may mix categories. Skip corpses
                // whose category is full but keep iterating to consume others
                // whose category still has room.
                var aoeCat = HordeCapTracker.CategoryFor(gameData, resolvedID);
                if (aoeCat != UndeadCategory.None
                    && HordeCapTracker.Available(sim.Units, gameData, sim.NecroState, aoeCat) <= 0)
                    continue;

                // Keep the corpse visible; QueueReanimRise claims it + plays the rise effect (green
                // outline fading in on the body), then spawns the unit + removes the corpse cleanly
                // after a short delay so the smoke/clouds build first. (Legacy spells dissolve + spawn now.)
                // The reanim_smoke composite is the ONLY raise VFX now — the old green fire_loop summon
                // flame is no longer layered on top of an AOE raise.
                game.QueueReanimRise(resolvedID, corpse.CorpseID, spell.ReanimationEffectID,
                    riseSpeed: spell.TestRiseSpeed, fogSpeed: spell.TestFogSpeed);
                raised++;
            }
        } else {
            // Single corpse consume (Corpse targeting)
            bool fromCorpse = false; // set when a corpse is consumed; drives the horde-minion
                                     // raise. Do NOT use corpseFacing's sign for this — FacingAngle
                                     // can legitimately be negative, which mis-routed negative-facing
                                     // corpses to the plain-spawn path (archetype 0 → no follow).
            string summonUnitID = pending.SummonUnitID;
            string puppetSourceDef = "";  // corpse-puppet raise: the ORIGINAL corpse's def, so the
                                          // puppet piles as that body rather than the zombie it wears.

            if (spell.SummonTargetReq == "Corpse" && pending.TargetCorpseID >= 0) {
                // Re-resolve the corpse by STABLE id, not the captured list index: a channeled
                // reanimate executes after a multi-second channel, during which _corpses can be
                // compacted (dissolve cleanup / carry-to-table) — the captured index would then
                // silently rebind to a different body. FindCorpseIndexByID returns -1 if the
                // aimed-at corpse is gone, in which case the raise simply no-ops.
                int corpseIdx = sim.FindCorpseIndexByID(pending.TargetCorpseID);
                if (corpseIdx >= 0) {
                    var corpse = sim.Corpses[corpseIdx];
                    // Resolve zombie type from the corpse if summonUnitID is empty (shared
                    // helper; see the AOE branch above).
                    if (string.IsNullOrEmpty(summonUnitID))
                        summonUnitID = TableCraftingSystem.ResolveZombieUnitID(gameData, corpse.UnitDefID);

                    // Corpse-puppet raise: remember the original body so the puppet deposits as that
                    // type at a Corpse Pile (the OnSpawned callback below stamps it onto the unit).
                    if (spell.SummonAsPuppet) puppetSourceDef = corpse.UnitDefID;

                    fromCorpse = true;
                    // corpse is NOT consumed here — QueueReanimRise (below) claims it and either plays
                    // the rise effect (keeping it visible) or legacy-dissolves it.
                }
            }

            // Corpse-puppet override: once the zombie stands up, swap its AI to the CorpsePuppet
            // archetype (walk to nearest Corpse Pile + deposit self) and record the original corpse
            // type so it piles as that body. Runs on the freshly-spawned unit (deferred rise spawn).
            Action<int>? onPuppetSpawned = null;
            if (spell.SummonAsPuppet) {
                string srcDef = puppetSourceDef;
                onPuppetSpawned = idx => {
                    sim.UnitsMut[idx].Archetype = AI.ArchetypeRegistry.FromName("CorpsePuppet");
                    sim.UnitsMut[idx].PuppetSourceDefID = srcDef;
                };
            }

            if (spell.SummonMode == "Transform" && pending.TargetUnitID != GameConstants.InvalidUnit) {
                // Transform mode: replace existing unit with the summon unit
                int targetIdx = sim.ResolveUnitID(pending.TargetUnitID);
                if (targetIdx >= 0 && !string.IsNullOrEmpty(summonUnitID)) {
                    var targetPos = sim.Units[targetIdx].Position;
                    sim.TransformUnit(targetIdx, summonUnitID);

                    // Rebuild animation for the transformed unit
                    game.RebuildUnitAnim(targetIdx, summonUnitID);

                    // Spawn summon effect at target position
                    game.SpawnSummonEffect(spell, targetPos);
                }
            } else {
                // Spawn mode
                if (string.IsNullOrEmpty(summonUnitID)) return;

                Vec2 spawnPos;
                switch (spell.SpawnLocation) {
                    case "NearestTargetToMouse":
                        spawnPos = pending.TargetPos;
                        break;
                    case "NearestTargetToCaster":
                        spawnPos = pending.TargetPos;
                        break;
                    case "AdjacentToCaster": {
                        float angle = game._rng.Next(360) * MathF.PI / 180f;
                        spawnPos = necroPos + new Vec2(MathF.Cos(angle) * 2f, MathF.Sin(angle) * 2f);
                        break;
                    }
                    case "AtTargetLocation":
                        spawnPos = pending.TargetPos;
                        break;
                    default:
                        spawnPos = pending.TargetPos;
                        break;
                }

                // Cap-limited summon count: spawn min(SummonQuantity, available
                // slots in the resolved category). Pre-check in SpellCaster
                // already refused when available=0; this clamps the multi-spawn
                // case so we never overshoot the cap.
                int spawnQty = spell.SummonQuantity;
                var spawnCat = HordeCapTracker.CategoryFor(gameData, summonUnitID);
                if (spawnCat != UndeadCategory.None) {
                    int avail = HordeCapTracker.Available(sim.Units, gameData, sim.NecroState, spawnCat);
                    if (avail < spawnQty) spawnQty = avail;
                }

                for (int q = 0; q < spawnQty; q++) {
                    var unitSpawnPos = spawnPos;
                    if (q > 0) {
                        // Offset additional spawns slightly
                        float angle = game._rng.Next(360) * MathF.PI / 180f;
                        unitSpawnPos = spawnPos + new Vec2(MathF.Cos(angle) * 1f, MathF.Sin(angle) * 1f);
                    }

                    if (fromCorpse) {
                        // Corpse reanimation → canonical horde minion (HordeMinion archetype). The rise
                        // effect plays NOW at the grave (corpse stays visible, green outline fading in);
                        // the unit spawns + stands up + the corpse is removed after a short delay.
                        game.QueueReanimRise(summonUnitID, pending.TargetCorpseID, spell.ReanimationEffectID,
                            riseSpeed: spell.TestRiseSpeed, fogSpeed: spell.TestFogSpeed,
                            onSpawned: onPuppetSpawned);
                    } else {
                        // Non-corpse summon (e.g. summon-from-def): plain spawn + horde enroll.
                        game.SpawnUnit(summonUnitID, unitSpawnPos);
                        int idx = sim.Units.Count - 1;
                        if (idx >= 0 && sim.Units[idx].Faction == Faction.Undead &&
                            sim.Units[idx].AI != AIBehavior.PlayerControlled)
                            sim.Horde.AddUnit(sim.Units[idx].Id);
                    }
                }

                // Spawn summon effect at the primary spawn location — only for a from-nothing summon.
                // A corpse reanimation already gets the reanim_smoke composite at the grave; the legacy
                // green fire_loop flame used to double up on raises, so suppress it here when fromCorpse.
                if (!fromCorpse) game.SpawnSummonEffect(spell, spawnPos);
            }
        }
    }

    // Swirl-trajectory jitter. Pre-existing cosmetic nondeterminism: the sim has no
    // shared RNG (events seed their own), and the swirl only affects the visual arc.
    private static readonly Random _projRng = new();

    /// <summary>Per-shot aim point for a projectile volley: the base target jittered by a
    /// uniform-over-area offset within <see cref="SpellDef.ProjectileSpread"/> (0 → the
    /// exact target, no spread). Barrage spells set a spread so their shots scatter evenly
    /// across the disc instead of stacking on one point. The GROUP stores the un-jittered
    /// base target, so every shot (and any cursor retarget) re-samples fresh.</summary>
    public static Vec2 VolleyAimPoint(SpellDef spell, Vec2 baseTarget, Random rng)
        => spell.ProjectileSpread > 0f
            ? baseTarget + MathUtil.RandomInDisc(rng, spell.ProjectileSpread)
            : baseTarget;

    /// <summary>Randomize the swirl params (freq/amplitude/phase) on a projectile —
    /// shared by the Swirly and HomingSwirly trajectories.</summary>
    private static void ApplySwirl(Projectile p)
    {
        p.SwirlFreq = 3f + (float)_projRng.NextDouble() * 5f;
        p.SwirlAmplitude = 0.5f + (float)_projRng.NextDouble() * 1.5f;
        p.SwirlPhase = (float)_projRng.NextDouble() * 2f * MathF.PI;
    }

    /// <summary>Spawn a spell projectile and post-configure it: tag it with the spell id
    /// (impact knockback / blight-bomb lookup), apply the def's Trajectory, and copy the
    /// projectile/hit-effect flipbook refs. Public — Game1.TickPendingProjectiles also
    /// calls it for the staggered Quantity&gt;1 shots.</summary>
    public static void SpawnProjectile(SpellDef spell, ProjectileManager projectiles,
        Vec2 origin, Vec2 target, uint ownerUid, float spawnHeight, Faction casterFaction)
    {
        // AOE spells fly as fireballs and burst on impact; a single-target (zero-AOE)
        // spell flies as an arrow instead, so it actually strikes its target rather
        // than relying on a zero-radius explosion happening to reach it. The +10
        // precision keeps these magic darts reliable — conceptually they home in,
        // unlike a physically-fired arrow that can be dodged.
        if (spell.AoeRadius > 0f)
        {
            projectiles.SpawnFireball(origin, target,
                casterFaction, ownerUid, spell.Damage, spell.AoeRadius, spell.DisplayName,
                spawnHeight: spawnHeight, gravityScale: spell.GravityScale);
        }
        else
        {
            projectiles.SpawnArrow(origin, target,
                casterFaction, ownerUid, spell.Damage,
                volley: spell.Trajectory == "Lob",
                precision: spell.PrecisionBonus + 10,
                weaponName: spell.DisplayName,
                spawnHeight: spawnHeight, gravityScale: spell.GravityScale);
        }
        var projs = projectiles.Projectiles;
        if (projs.Count > 0) {
            var lastProj = projs[projs.Count - 1];

            // Tag projectile with spell ID for physics knockback lookup on impact
            lastProj.SpellID = spell.Id;
            lastProj.ImpactForce = spell.ImpactForce;
            lastProj.ImpactUpward = spell.ImpactUpward;

            // Blight bombs must burst exactly where the player clicked, not wherever
            // the ballistic arc happens to land. Forward the aimed point and flag the
            // projectile so it detonates on overshoot. Other spells opt in via the def.
            if (spell.Category == "Blight" || spell.DetonateAtTarget) {
                lastProj.TargetPos = target;
                lastProj.DetonateAtTarget = true;
            }

            // Apply trajectory type. Lob keeps SpawnFireball's arc; every other
            // trajectory is a flat direct-fire launch through the ONE shared
            // solver (the four cases used to re-paste the 5° solve, and it
            // pointlessly overwrote SpawnFireball's lob solve four ways).
            var traj = Enum.TryParse<Trajectory>(spell.Trajectory, true, out var t) ? t : Trajectory.Lob;
            if (traj != Trajectory.Lob) {
                var dir = (target - origin).Normalized();
                float speed = spell.ProjectileSpeed > 0 ? spell.ProjectileSpeed : ProjectileManager.MagicSpeed;
                (lastProj.Velocity, lastProj.VelocityZ) =
                    ProjectileManager.BallisticVelocity(dir, speed, ProjectileManager.DirectFireTheta);
                lastProj.BaseDirection = dir;
                lastProj.IsLob = false;
            }

            switch (traj) {
                case Trajectory.Swirly:
                    ApplySwirl(lastProj);
                    break;
                case Trajectory.Homing:
                    lastProj.TargetPos = target;
                    lastProj.HomingStrength = 5f;
                    break;
                case Trajectory.HomingSwirly:
                    lastProj.TargetPos = target;
                    lastProj.HomingStrength = 5f;
                    ApplySwirl(lastProj);
                    break;
                // DirectFire needs nothing beyond the shared launch above;
                // Lob is the default from SpawnFireball — no changes needed.
            }

            if (spell.ProjectileFlipbook != null) {
                lastProj.FlipbookID = spell.ProjectileFlipbook.FlipbookID;
                lastProj.ParticleScale = spell.ProjectileFlipbook.Scale;
                lastProj.ParticleColor = spell.ProjectileFlipbook.Color;
            }

            if (spell.HitEffectFlipbook != null) {
                lastProj.HitEffectFlipbookID = spell.HitEffectFlipbook.FlipbookID;
                lastProj.HitEffectScale = spell.HitEffectFlipbook.Scale;
                lastProj.HitEffectColor = spell.HitEffectFlipbook.Color;
                lastProj.HitEffectBlendMode = spell.HitEffectFlipbook.BlendMode == "Additive" ? 1 : 0;
                lastProj.HitEffectAlignment = spell.HitEffectFlipbook.Alignment == "Upright" ? 1 : 0;
            }
        }
    }

    /// <summary>Apply a Blight-category spell to the death-fog field at the target.
    /// Add mode dumps BlightAmount blight into the target cell (blight bomb); Purify
    /// mode cleanses a 5×5 cell kernel centered on the target (purifying bomb), with
    /// BlightAmount as the center cleanse strength. Public — Game1.ApplyBlightBombImpacts
    /// also calls it for bomb-style Blight defs that defer the fog change to impact.</summary>
    public static void ApplyBlight(SpellDef spell, Vec2 target, DeathFogSystem deathFog)
    {
        if (string.Equals(spell.BlightMode, "Purify", StringComparison.OrdinalIgnoreCase))
            deathFog.PurifyArea(target.X, target.Y, spell.BlightAmount);
        else
            deathFog.AddDensity(target.X, target.Y, spell.BlightAmount);
    }

    private static void ExecuteStrike(SpellDef spell, Simulation sim, GameData gameData,
        int casterIdx, Vec2 target, Vec2 effectOrigin, List<DamageNumber> damageNumbers)
    {
        var units = sim.UnitsMut;
        ExecuteStrikeFrom(spell, sim, gameData, casterIdx, units[casterIdx].Id,
            effectOrigin, units[casterIdx].EffectSpawnHeight, target,
            units[casterIdx].Faction, damageNumbers);
    }

    /// <summary>
    /// Origin-based strike executor — the single Strike-category implementation
    /// for every source: player casts, AI casts (both via <see cref="Execute"/>)
    /// and casterless sources like traps, which have a world-space origin but no
    /// caster unit. Pass <paramref name="casterIdx"/> = -1 for casterless
    /// sources: the MR gate then uses base spell penetration (no caster path
    /// bonus) and kills carry no attacker credit; pass
    /// <see cref="GameConstants.InvalidUnit"/> as <paramref name="ownerUid"/> so
    /// ground strikes stay unattributed.
    /// </summary>
    public static void ExecuteStrikeFrom(SpellDef spell, Simulation sim, GameData gameData,
        int casterIdx, uint ownerUid, Vec2 origin, float originHeight,
        Vec2 target, Faction sourceFaction, List<DamageNumber> damageNumbers)
    {
        var units = sim.UnitsMut;
        var style = spell.BuildStrikeStyle();
        var sVis = spell.StrikeVisualType == "GodRay" ? StrikeVisual.GodRay : StrikeVisual.Lightning;
        var sGrp = spell.BuildGodRayParams();
        Enum.TryParse<SpellTargetFilter>(spell.TargetFilter, out var sTF);

        if (spell.StrikeTargetUnit)
        {
            int enemy = FindClosestEnemy(sim, target, spell.Range, sourceFaction);
            if (enemy >= 0)
            {
                var targetPos = units[enemy].Position;
                float targetH = 1.0f;
                var tDef = gameData.Units.Get(units[enemy].UnitDefID);
                if (tDef != null) targetH = tDef.SpriteWorldHeight * 0.5f;

                sim.Lightning.SpawnZap(origin, targetPos,
                    spell.ZapDuration > 0 ? spell.ZapDuration : spell.StrikeDuration,
                    style, originHeight, targetH);
                // Magic-resistance gate: an MR-checked strike only lands if it
                // penetrates the target's MR.
                if (SpellPenetration.Affects(gameData, units, casterIdx, enemy, spell))
                {
                    sim.DealDamage(enemy, spell.Damage, casterIdx);
                    FloatingText.AddDamage(damageNumbers, targetPos, spell.Damage, targetH);
                }
            }
        }
        else
        {
            sim.Lightning.SpawnStrike(target, spell.TelegraphDuration,
                spell.StrikeDuration, spell.AoeRadius, spell.Damage,
                style, spell.Id, sVis, sGrp, sTF, spell.TelegraphVisible,
                ownerUid);
        }
    }

    public static void ExecuteCloud(SpellDef spell, Simulation sim, Vec2 target, Faction casterFaction)
    {
        sim.PoisonClouds.SpawnCloud(target, spell, casterFaction);

        float radius = spell.AoeRadius > 0 ? spell.AoeRadius : spell.CloudRadius;

        if (spell.CloudAppliesParalysis)
        {
            // Paralysis clouds use their AoE burst to apply paralysis (no poison stacks).
            PotionSystem.ApplyParalysisAoE(sim.UnitsMut, sim.Quadtree, target, radius, casterFaction);
            return;
        }

        if (spell.Damage > 0)
        {
            var flags = SpellDamageFlags(spell);
            DamageSystem.ApplyAoE(sim.UnitsMut, sim.Quadtree, target, radius,
                spell.Damage, DamageType.Poison, flags, casterFaction, sim.DamageEventsMut);
        }
    }

    /// <summary>Build DamageFlags from a spell's AN/DN settings. Public because the
    /// projectile-hit path in Simulation routes spell projectiles through the same
    /// armor+toughness pipeline and needs the same flags.</summary>
    public static DamageFlags SpellDamageFlags(SpellDef spell)
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
    private static void ExecuteSacrifice(SpellDef spell, Simulation sim, int casterIdx,
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
            // Weapon-tip anchor (EffectSpawnHeight), deliberately not head height.
            FloatingText.AddText(damageNumbers, units[casterIdx].Position,
                gained.ToString(), units[casterIdx].EffectSpawnHeight);
        }

        // The victim crumbles into a corpse — the visible "sacrifice".
        sim.DealDamage(victim, 999999, casterIdx);
    }

    /// <summary>Find the closest same-faction unit to a point within range,
    /// excluding one index (the caster).</summary>
    private static int FindClosestAlly(Simulation sim, Vec2 point, float range,
        Faction faction, int excludeIdx)
        => sim.Query.NearestAllyToPoint(point, range, faction, excludeIdx);

    /// <summary>Find closest enemy unit to a point within range.</summary>
    private static int FindClosestEnemy(Simulation sim, Vec2 point, float range, Faction casterFaction)
        => sim.Query.NearestEnemyToPoint(point, range, casterFaction);
}
