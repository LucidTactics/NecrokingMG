using System;
using System.Collections.Generic;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.Game;
using Necroking.GameSystems;
using Necroking.Render;

namespace Necroking;

public partial class Game1 {
   /// <summary>
   /// Execute the full summon spell logic matching C++ Simulation::executeSpellCast for Summon category.
   /// Handles all SummonTargetReq types: None, Corpse, UnitType, CorpseAOE.
   /// Handles SummonMode: Spawn, Transform.
   /// </summary>
   void ExecuteSummonSpell(SpellDef spell, PendingSpellCast pending, Vec2 necroPos, int necroIdx) {
      if (spell.SummonTargetReq == "CorpseAOE") {
         // AOE corpse raise: iterate corpses in range, resolve zombie type per corpse
         int raised = 0;
         for (int i = 0; i < _sim.Corpses.Count && raised < spell.SummonQuantity; i++) {
            var corpse = _sim.Corpses[i];
            if (corpse.Dissolving || corpse.ConsumedBySummon) continue;
            if (corpse.DraggedByUnitID != GameConstants.InvalidUnit) continue;
            if (corpse.BaggedByUnitID != GameConstants.InvalidUnit) continue; // mid-bagging
            float dist = (corpse.Position - pending.TargetPos).Length();
            if (dist > spell.AoeRadius) continue;

            // Resolve zombie type from corpse's UnitDef. Shared with
            // TableCraftingSystem so the same source corpse always raises
            // into the same unit class regardless of how it was triggered.
            string resolvedID = Game.TableCraftingSystem.ResolveZombieUnitID(_gameData, corpse.UnitDefID);
            if (string.IsNullOrEmpty(resolvedID)) continue;

            // Per-corpse cap check — AOE may mix categories. Skip corpses
            // whose category is full but keep iterating to consume others
            // whose category still has room.
            var aoeCat = HordeCapTracker.CategoryFor(_gameData, resolvedID);
            if (aoeCat != UndeadCategory.None
                && HordeCapTracker.Available(_sim.Units, _gameData, _sim.NecroState, aoeCat) <= 0)
               continue;

            // Keep the corpse visible; QueueReanimRise claims it + plays the rise effect (green
            // outline fading in on the body), then spawns the unit + removes the corpse cleanly
            // after a short delay so the smoke/clouds build first. (Legacy spells dissolve + spawn now.)
            // The reanim_smoke composite is the ONLY raise VFX now — the old green fire_loop summon
            // flame is no longer layered on top of an AOE raise.
            QueueReanimRise(resolvedID, corpse.CorpseID, spell.ReanimationEffectID,
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
            int corpseIdx = _sim.FindCorpseIndexByID(pending.TargetCorpseID);
            if (corpseIdx >= 0) {
               var corpse = _sim.Corpses[corpseIdx];
               // Resolve zombie type from the corpse if summonUnitID is empty (shared
               // helper; see the AOE branch above).
               if (string.IsNullOrEmpty(summonUnitID))
                  summonUnitID = Game.TableCraftingSystem.ResolveZombieUnitID(_gameData, corpse.UnitDefID);

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
               _sim.UnitsMut[idx].Archetype = AI.ArchetypeRegistry.FromName("CorpsePuppet");
               _sim.UnitsMut[idx].PuppetSourceDefID = srcDef;
            };
         }

         if (spell.SummonMode == "Transform" && pending.TargetUnitID != GameConstants.InvalidUnit) {
            // Transform mode: replace existing unit with the summon unit
            int targetIdx = _sim.ResolveUnitID(pending.TargetUnitID);
            if (targetIdx >= 0 && !string.IsNullOrEmpty(summonUnitID)) {
               var targetPos = _sim.Units[targetIdx].Position;
               _sim.TransformUnit(targetIdx, summonUnitID);

               // Rebuild animation for the transformed unit
               RebuildUnitAnim(targetIdx, summonUnitID);

               // Spawn summon effect at target position
               SpawnSummonEffect(spell, targetPos);
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
                  float angle = _rng.Next(360) * MathF.PI / 180f;
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
            var spawnCat = HordeCapTracker.CategoryFor(_gameData, summonUnitID);
            if (spawnCat != UndeadCategory.None) {
               int avail = HordeCapTracker.Available(_sim.Units, _gameData, _sim.NecroState, spawnCat);
               if (avail < spawnQty) spawnQty = avail;
            }

            for (int q = 0; q < spawnQty; q++) {
               var unitSpawnPos = spawnPos;
               if (q > 0) {
                  // Offset additional spawns slightly
                  float angle = _rng.Next(360) * MathF.PI / 180f;
                  unitSpawnPos = spawnPos + new Vec2(MathF.Cos(angle) * 1f, MathF.Sin(angle) * 1f);
               }

               if (fromCorpse) {
                  // Corpse reanimation → canonical horde minion (HordeMinion archetype). The rise
                  // effect plays NOW at the grave (corpse stays visible, green outline fading in);
                  // the unit spawns + stands up + the corpse is removed after a short delay.
                  QueueReanimRise(summonUnitID, pending.TargetCorpseID, spell.ReanimationEffectID,
                     riseSpeed: spell.TestRiseSpeed, fogSpeed: spell.TestFogSpeed,
                     onSpawned: onPuppetSpawned);
               } else {
                  // Non-corpse summon (e.g. summon-from-def): plain spawn + horde enroll.
                  SpawnUnit(summonUnitID, unitSpawnPos);
                  int idx = _sim.Units.Count - 1;
                  if (idx >= 0 && _sim.Units[idx].Faction == Faction.Undead &&
                      _sim.Units[idx].AI != AIBehavior.PlayerControlled)
                     _sim.Horde.AddUnit(_sim.Units[idx].Id);
               }
            }

            // Spawn summon effect at the primary spawn location — only for a from-nothing summon.
            // A corpse reanimation already gets the reanim_smoke composite at the grave; the legacy
            // green fire_loop flame used to double up on raises, so suppress it here when fromCorpse.
            if (!fromCorpse) SpawnSummonEffect(spell, spawnPos);
         }
      }
   }

   // --- Deferred reanimation rise ------------------------------------------------
   // The composite rise effect (ReanimEffectSystem) plays at the grave the instant the
   // spell resolves, but the reanimated unit only spawns + plays its slow standup a short
   // delay later, so the smoke/clouds build up before the body actually gets up.
   private struct PendingReanimRise
   {
      public Vec2 Pos;
      public float Facing;
      public string DefId;
      public int FxInstanceId;   // ReanimEffectSystem handle, so the outline attaches on spawn
      public int CorpseId;       // source corpse (-1 = none, e.g. table-craft) — removed cleanly when the unit rises
      public float Timer;
      public float StandupSpeed; // Standup anim playback speed (0.5 = the default slow rise)
      public Action<int>? OnSpawned;  // runs on the freshly-spawned unit (e.g. apply crafted item bonuses)
   }
   private readonly List<PendingReanimRise> _pendingReanimRises = new();

   /// <summary>Bridge for sim-layer raises (potion / on-death / table-craft): resolve the zombie
   /// type and route through the SAME composite reanimation pipeline as spells. Wired to
   /// Simulation.ReanimHandler in LoadContent. corpseId &lt; 0 (no world corpse) no-ops cleanly.</summary>
   private void OnSimReanimReady(PendingZombieRaise r)
   {
      string zombieDef = Game.TableCraftingSystem.ResolveZombieUnitID(_gameData, r.UnitDefID);
      if (string.IsNullOrEmpty(zombieDef)) zombieDef = "skeleton";
      // Corpse-based raises ignore the overrides (read the corpse); corpse-less ones (table-craft,
      // CorpseId < 0) use them to place the effect + rise.
      QueueReanimRise(zombieDef, r.CorpseId, "",   // "" → the raised unit's own effect (else reanim_smoke)
         posOverride: r.Position, facingOverride: r.FacingAngle, scaleOverride: r.SpriteScale,
         onSpawned: r.OnSpawned);
   }

   /// <summary>Raise a unit through the one composite reanim pipeline. With a world corpse
   /// (<paramref name="corpseId"/> &gt;= 0): claim it (stays fully VISIBLE — the renderer fades in the
   /// green undead outline and morphs the body), play the effect, then after <paramref name="delay"/>s
   /// spawn the unit + its slow standup and remove the corpse cleanly. Without one (corpseId &lt; 0,
   /// e.g. table crafting): play the effect at <paramref name="posOverride"/> and rise from it (no body
   /// to morph). <paramref name="onSpawned"/> runs on the freshly-spawned unit (e.g. apply crafted item
   /// bonuses). If the effect asset is missing, falls back to an immediate spawn.</summary>
   void QueueReanimRise(string defId, int corpseId, string? configId, float delay = 3.5f,
                        Vec2 posOverride = default, float facingOverride = 0f, float scaleOverride = 1f,
                        Action<int>? onSpawned = null, float riseSpeed = 2f, float fogSpeed = 1f)
   {
      // Two independent speeds (both default 1):
      //  • riseSpeed scales the BODY rising — the standup anim, the spawn delay, and the
      //    effect's rise clock (outline + pose-morph build-up), so the morph stays synced
      //    to the unit getting up. `delay` is the build-up in rise-effect-time; the instance
      //    runs at riseSpeed, so the body stands up after delay/riseSpeed wall seconds.
      //  • fogSpeed scales the SMOKE — the green cloud + dust puffs — on the effect's own
      //    fog clock, so the smoke can linger while the body pops up fast (or the reverse).
      // Clamp to a floor so a zero/negative value can't divide-by-zero or stall the rise.
      riseSpeed = MathF.Max(0.1f, riseSpeed);
      fogSpeed = MathF.Max(0.1f, fogSpeed);
      float standupSpeed = 0.5f * riseSpeed;   // 0.5 = the default slow rise
      float spawnDelay = delay / riseSpeed;    // wall-clock time until the unit spawns
      int corpseIdx = -1;
      Vec2 pos; float facing; float scale;
      if (corpseId >= 0)
      {
         corpseIdx = _sim.FindCorpseIndexByID(corpseId);
         if (corpseIdx < 0) return;              // corpse already gone — nothing to raise
         var c = _sim.Corpses[corpseIdx];
         pos = c.Position; facing = c.FacingAngle; scale = c.SpriteScale;
      }
      else { pos = posOverride; facing = facingOverride; scale = scaleOverride; }

      // Reanimation effect precedence: spell-specific id (passed in) beats the raised
      // unit's own ReanimationEffectID, which beats the global "reanim_smoke" default —
      // a skeleton/zombie/frog each picks its own plume without per-spell wiring.
      if (string.IsNullOrEmpty(configId))
         configId = _gameData.Units.Get(defId)?.ReanimationEffectID;
      if (string.IsNullOrEmpty(configId)) configId = "reanim_smoke";

      if (!_reanimFx.HasConfig(configId))
      {
         // Safety fallback only (effect asset missing): consume the corpse + spawn immediately.
         if (corpseIdx >= 0) _sim.ConsumeCorpse(corpseIdx);
         int now = _sim.SpawnZombieMinion(defId, pos);
         if (now >= 0)
         {
            _sim.UnitsMut[now].FacingAngle = facing;
            BuffSystem.BeginReanimationRise(_sim.UnitsMut, now, standupSpeed);
            onSpawned?.Invoke(now);
         }
         return;
      }

      // Composite effect: claim the corpse (other systems skip ConsumedBySummon) but leave
      // Dissolving=false so it stays fully visible; the renderer draws the green outline + morph on it.
      // The unit spawns + the corpse is removed cleanly after the delay (TickPendingReanimRises).
      if (corpseIdx >= 0) _sim.Corpses[corpseIdx].ConsumedBySummon = true;
      int fxId = _reanimFx.Begin(GameConstants.InvalidUnit, pos, scale, configId,
                                 outlineFadeIn: delay, morphHold: MathF.Max(0f, delay - 1.5f),
                                 riseSpeed: riseSpeed, fogSpeed: fogSpeed);
      if (corpseIdx >= 0)
      {
         _sim.Corpses[corpseIdx].ReanimInstanceId = fxId;
         _sim.Corpses[corpseIdx].ReanimZombieDefId = defId;   // morph targets the zombie's standup pose
      }
      _pendingReanimRises.Add(new PendingReanimRise
         { Pos = pos, Facing = facing, DefId = defId, FxInstanceId = fxId, CorpseId = corpseId,
           Timer = spawnDelay, StandupSpeed = standupSpeed, OnSpawned = onSpawned });
   }

   /// <summary>Tick queued rises (called each sim step alongside the effect update). When a
   /// delay elapses, spawn the horde minion, start its slow standup, and attach its outline
   /// to the already-running effect.</summary>
   void TickPendingReanimRises(float dt)
   {
      for (int i = _pendingReanimRises.Count - 1; i >= 0; i--)
      {
         var pr = _pendingReanimRises[i];
         pr.Timer -= dt;
         if (pr.Timer > 0f) { _pendingReanimRises[i] = pr; continue; }
         _pendingReanimRises.RemoveAt(i);

         if (pr.CorpseId >= 0)
            _sim.RemoveCorpseClean(pr.CorpseId);   // body vanishes cleanly as the unit takes its place
         int idx = _sim.SpawnZombieMinion(pr.DefId, pr.Pos);
         if (idx >= 0)
         {
            _sim.UnitsMut[idx].FacingAngle = pr.Facing;
            BuffSystem.BeginReanimationRise(_sim.UnitsMut, idx, pr.StandupSpeed);
            _reanimFx.SetUnitId(pr.FxInstanceId, _sim.Units[idx].Id);
            pr.OnSpawned?.Invoke(idx);
         }
      }
   }

   /// <summary>
   /// Spawn the visual summon flipbook effect at a given position.
   /// </summary>
   void SpawnCastEffect(SpellDef spell, Vec2 pos) {
      SpawnFlipbookEffect(spell.CastFlipbook, pos);
   }

   private void SpawnFlipbookEffect(FlipbookRef? fb, Vec2 pos) {
      if (fb == null || string.IsNullOrEmpty(fb.FlipbookID)) return;

      var tint = fb.Color.ToColor();
      int blendMode = fb.BlendMode == "Additive" ? 1 : 0;
      int alignment = fb.Alignment == "Upright" ? 1 : 0;
      float duration = fb.Duration >= 0f ? fb.Duration : 0.4f;

      _effectManager.SpawnSpellImpact(pos, fb.Scale, tint, fb.FlipbookID,
         fb.Color.Intensity, blendMode, alignment, duration);
   }

   /// <summary>
   /// Execute a spell's effect (projectile, buff, strike, etc.). Called either immediately
   /// (no casting buff) or at the Spell1 animation action moment (deferred cast).
   /// </summary>
   void ExecuteSpellEffect(SpellDef spell, int necroIdx, Vec2 target, int slot) {
      // Cast flipbook effect at caster position
      SpawnCastEffect(spell, _sim.Units[necroIdx].EffectSpawnPos2D);

      // Delegate to SpellEffectSystem — all category logic lives there
      var result = _spellEffects.Execute(spell, _sim, _gameData, necroIdx, target, slot,
         _damageNumbers,
         SpawnSpellProjectile,
         (sp, cIdx) => ExecuteSummonSpell(sp, _pendingSpell, _sim.Units[cIdx].Position, cIdx),
         ApplyBlightSpell);

      // Apply side effects that SpellEffectSystem can't own (Game1 state)
      if (result.ChannelingSlot >= 0)
         _channelingSlot = result.ChannelingSlot;
      if (result.PendingProjectile.HasValue)
         _pendingProjectiles.Add(result.PendingProjectile.Value);
   }

   /// <summary>Apply a Blight-category spell to the death-fog field at the target.
   /// Add mode dumps BlightAmount blight into the target cell (blight bomb); Purify
   /// mode cleanses a 5×5 cell kernel centered on the target (purifying bomb), with
   /// BlightAmount as the center cleanse strength. See DeathFogSystem.</summary>
   void ApplyBlightSpell(SpellDef spell, Vec2 target) {
      if (string.Equals(spell.BlightMode, "Purify", StringComparison.OrdinalIgnoreCase))
         _deathFog.PurifyArea(target.X, target.Y, spell.BlightAmount);
      else
         _deathFog.AddDensity(target.X, target.Y, spell.BlightAmount);
   }

   /// <summary>Drop the blight where a thrown Blight bomb actually exploded. A Blight
   /// spell that throws a projectile (a ProjectileFlipbook def) defers its fog change
   /// to impact — the projectile carries the spell id, so this reads the just-resolved
   /// projectile impacts (from <see cref="_sim"/>.Tick) and applies the fog at each
   /// explosion point. Called once per tick right after the sim, before the impacts
   /// are cleared by the next tick's projectile update.</summary>
   void ApplyBlightBombImpacts() {
      var impacts = _sim.Projectiles.Impacts;
      if (impacts.Count == 0) return;
      for (int i = 0; i < impacts.Count; i++) {
         string id = impacts[i].SpellID;
         if (string.IsNullOrEmpty(id)) continue;
         var spell = _gameData.Spells.Get(id);
         if (spell == null || spell.Category != "Blight") continue;
         ApplyBlightSpell(spell, impacts[i].Position);
      }
   }

   /// <summary>Remove all casting effect buffs from a unit (buff_4 variants).</summary>
   void RemoveCastingBuffAll(int unitIdx) {
      var buffs = _sim.UnitsMut[unitIdx].ActiveBuffs;
      for (int b = buffs.Count - 1; b >= 0; b--) {
         var def = _gameData.Buffs.Get(buffs[b].BuffDefID);
         if (def != null && def.HasWeaponParticle)
            buffs.RemoveAt(b);
      }
   }

   void SpawnSummonEffect(SpellDef spell, Vec2 pos) {
      SpawnFlipbookEffect(spell.SummonFlipbook, pos);
   }

   /// <summary>Cast a potion-spell: drink it (self, cursor near the necromancer)
   /// or throw it (at the cursor), consuming the inventory item — the same logic
   /// the old potion hotkeys used, now reachable from any spell slot.</summary>
   void CastPotionSpell(string potionId, string itemId, int necroIdx, Vec2 mouseWorld) {
      if (necroIdx < 0 || _inventory.GetItemCount(itemId) <= 0) return;
      var potionDef = _gameData.Potions.Get(potionId);
      if (potionDef == null) return;

      var necroPos = _sim.Units[necroIdx].Position;
      if ((mouseWorld - necroPos).Length() < 1.0f) {
         // Self-target: drink.
         _inventory.RemoveItem(itemId, 1);
         PotionSystem.ApplyPotionEffect(potionDef.Id, _gameData.Potions, _gameData.Buffs,
            necroIdx, _sim.UnitsMut, _sim.Units[necroIdx].Faction,
            _sim.PendingZombieRaises, _sim.CorpsesMut, necroPos);
      } else {
         PotionSystem.TryThrowPotion(potionDef.Id, _gameData.Potions, _inventory,
            _sim.UnitsMut, necroIdx, mouseWorld, _sim.Corpses, _sim.Projectiles);
      }
   }

   /// <summary>Light up a hotbar slot for SlotFlashDuration so a fired spell gives
   /// immediate visual feedback. Decayed in real time in Update; drawn by the HUD.</summary>
   void FlashSpellSlot(int slot) {
      if (slot >= 0 && slot < _slotFlash.Length) _slotFlash[slot] = HUDRenderer.SlotFlashDuration;
   }

   /// <summary>Single dispatch path for the spell bar (and the dev 'cast'
   /// command). Handles built-in ability intercepts (melee_gather,
   /// poison_berries_*) before falling through to the normal SpellCaster +
   /// casting-buff + pending-anim pipeline. Returns the cast result.</summary>
   CastResult DispatchSpellCast(string spellId, int necroIdx, int slot,
      Vec2 mouseWorld) {
      if (string.IsNullOrEmpty(spellId) || necroIdx < 0) return CastResult.NoValidTarget;

      // Built-in abilities short-circuit the normal spell pipeline.
      if (TryDispatchBuiltinAbility(spellId, necroIdx, mouseWorld)) {
         FlashSpellSlot(slot);
         return CastResult.Success;
      }

      // Potion-spells (ConsumesItem set) run through the existing PotionSystem
      // throw/drink path + inventory consume, not the normal spell pipeline.
      var spellDef = _gameData.Spells.Get(spellId);
      if (spellDef != null && !string.IsNullOrEmpty(spellDef.ConsumesItem)) {
         CastPotionSpell(spellId, spellDef.ConsumesItem, necroIdx, mouseWorld);
         FlashSpellSlot(slot);
         return CastResult.Success;
      }

      // Can't cast a real spell while one is mid-animation.
      if (_pendingCastAnim != null) return CastResult.NoValidTarget;

      var result = SpellCaster.TryStartSpellCast(spellId, _gameData.Spells, _sim.NecroState,
         _sim.Units, necroIdx, mouseWorld, _sim.Corpses, _pendingSpell, _gameData);
      switch (result) {
         case CastResult.HordeCapFull:
            SpawnHordeCapText(necroIdx);
            break;
         // Path-locked: name the path the necromancer still needs so the failure
         // never reads as (or is silently mistaken for) a mana shortfall.
         case CastResult.MissingPath:
            SpawnMissingPathText(necroIdx, spellId);
            break;
         case CastResult.OutOfRange:
            SpawnCastFailText(necroIdx, "Out of Range");
            break;
         case CastResult.NotEnoughMana:
            SpawnCastFailText(necroIdx, "Not Enough Mana");
            break;
         case CastResult.OnCooldown:
            SpawnCastFailText(necroIdx, "Cooling Down");
            break;
         case CastResult.NoValidTarget:
            SpawnCastFailText(necroIdx, "No Target");
            break;
      }
      if (result != CastResult.Success) return result;

      // Successful real-spell cast → light up its hotbar slot.
      FlashSpellSlot(slot);

      // Tally a player spell cast for the skill-book milestone (mirrors the
      // monster_kill / human_kill counters). Magic-tree skills cost "cast_spell"
      // events, so each successful real-spell cast advances them. Built-in
      // abilities and potion-throws short-circuit above and don't count.
      _sim.PlayerEvents.Tally(PlayerEventTracker.Keys.CastSpell);

      var spell = _gameData.Spells.Get(spellId);
      if (spell == null) return result;

      if (IsChanneledCast(spell.CastAnim)) {
         // Channeled reanimation cast (Start→Loop→Finish). Effect fires at the
         // end of the loop; the necromancer faces the target for the duration.
         if (!string.IsNullOrEmpty(spell.CastingBuffID)) {
            var cb = _gameData.Buffs.Get(spell.CastingBuffID);
            if (cb != null) BuffSystem.ApplyBuff(_sim.UnitsMut, necroIdx, cb);
         }

         var dir = mouseWorld - _sim.Units[necroIdx].Position;
         if (dir.LengthSq() > 0.0001f)
            _sim.UnitsMut[necroIdx].FacingAngle = MathF.Atan2(dir.Y, dir.X) * 180f / MathF.PI;

         _pendingCastAnim = new PendingCastAnim {
            SpellID = spellId, Target = mouseWorld, Slot = slot,
            CastingBuffID = spell.CastingBuffID,
            CastAnim = spell.CastAnim, ChannelPhase = 0,
            ChannelElapsed = 0f, LoopElapsed = 0f, CastTime = spell.CastTime, Executed = false,
         };

         GetChannelStates(spell.CastAnim, out var startS, out _, out _);
         uint nUid = _sim.Units[necroIdx].Id;
         if (_unitAnims.TryGetValue(nUid, out var nAnim)) {
            nAnim.Ctrl.ForceState(startS);
            _unitAnims[nUid] = nAnim;
         }
      } else if (!string.IsNullOrEmpty(spell.CastingBuffID)) {
         // Defer execution to the Spell1 animation event.
         var castBuff = _gameData.Buffs.Get(spell.CastingBuffID);
         if (castBuff != null) BuffSystem.ApplyBuff(_sim.UnitsMut, necroIdx, castBuff);

         _pendingCastAnim = new PendingCastAnim {
            SpellID = spellId,
            Target = mouseWorld,
            Slot = slot,
            CastingBuffID = spell.CastingBuffID,
         };

         uint necroUid = _sim.Units[necroIdx].Id;
         if (_unitAnims.TryGetValue(necroUid, out var necroAnim)) {
            necroAnim.Ctrl.RequestState(AnimState.Spell1);
            _unitAnims[necroUid] = necroAnim;
         }
      } else {
         // No casting buff → execute immediately (legacy behavior).
         ExecuteSpellEffect(spell, necroIdx, mouseWorld, slot);
      }

      return CastResult.Success;
   }

   /// <summary>Built-in abilities don't live in spells.json — they're hard-wired
   /// IDs that bypass the SpellCaster pipeline entirely. Returns true if the id
   /// was a built-in (handled or rejected); false if the caller should fall
   /// through to normal spell dispatch.</summary>
   bool TryDispatchBuiltinAbility(string spellId, int necroIdx, Vec2 mouseWorld) {
      if (spellId == "melee_gather") {
         TryMeleeOrGather(necroIdx, mouseWorld);
         return true;
      }

      if (spellId == "command") {
         TryCommandHorde(necroIdx, mouseWorld);
         return true;
      }

      if (spellId == "regroup") {
         TryRegroupHorde(necroIdx);
         return true;
      }

      if (PoisonBerryAbilities.TryGetValue(spellId, out var pb)) {
         TryStartPoisonBerries(necroIdx, mouseWorld, pb.buffID, pb.itemID);
         return true;
      }

      return false;
   }

   /// <summary>Built-in "Command" ability (formerly the one-off SpellCategory.Command):
   /// order every living undead horde minion to attack-move to the cursor — Routine 4
   /// (RoutineCommanded) makes them fight enemies there and auto-return when clear.
   /// Like the other built-ins it bypasses the SpellCaster pipeline, but still honors
   /// the spell def's cooldown (stored on NecroState, read by the HUD sweep) so it
   /// can't be spammed. Living here as a built-in frees the Command spell *category*
   /// so we can add more command kinds without burning a whole category each.</summary>
   void TryCommandHorde(int necroIdx, Vec2 target) {
      if (necroIdx < 0) return;
      var necro = _sim.NecroState;
      if (necro.GetCooldown("command") > 0f) return;   // still recharging

      var units = _sim.UnitsMut;
      for (int ci = 0; ci < units.Count; ci++) {
         if (!units[ci].Alive) continue;
         if (units[ci].Faction != Faction.Undead) continue;
         if (units[ci].Archetype != AI.ArchetypeRegistry.HordeMinion) continue;

         AI.HordeMinionHandler.CommandTo(units, ci, target);
      }

      float cd = _gameData.Spells.Get("command")?.Cooldown ?? 0f;
      if (cd > 0f) necro.SpellCooldowns["command"] = cd;
   }

   /// <summary>Built-in "Regroup" ability: cancel any command in effect and snap
   /// every commanded undead horde minion straight back to standard horde behavior.
   /// They break off, drop their attack-move, and head for formation (Routine 3 =
   /// RoutineReturning, which the horde state machine resolves to Following at the
   /// slot) — the exact reset HordeMinionHandler.ReturnFromCommand does when a
   /// command finishes on its own. Instant, no target; only touches minions that are
   /// actually under a command (Routine 4), so it's a no-op if nothing is commanded.</summary>
   void TryRegroupHorde(int necroIdx) {
      if (necroIdx < 0) return;
      var units = _sim.UnitsMut;
      for (int ci = 0; ci < units.Count; ci++) {
         if (!units[ci].Alive) continue;
         if (units[ci].Faction != Faction.Undead) continue;
         if (units[ci].Archetype != AI.ArchetypeRegistry.HordeMinion) continue;

         AI.HordeMinionHandler.Recall(units, ci); // no-op unless under a command
      }
   }

   /// <summary>Built-in poison-berries abilities: spell id → (buff applied to the
   /// eater, potion item consumed). Single source of truth for both casting
   /// (<see cref="TryDispatchBuiltinAbility"/>) and the grimoire "seen materials"
   /// gate: each spell def MUST declare consumesItem == itemID so the ability
   /// stays hidden until the player has seen the potion. <see
   /// cref="ValidatePotionAbilities"/> enforces that at load.</summary>
   static readonly Dictionary<string, (string buffID, string itemID)> PoisonBerryAbilities = new() {
      ["poison_berries_poison"] = ("buff_poison_dot", "potion_poison"),
      ["poison_berries_paralysis"] = ("buff_paralysis_slow", "potion_paralysis"),
   };

   /// <summary>Guard against the "skill visible before its material is seen"
   /// regression: every built-in potion ability must declare consumesItem ==
   /// the potion it actually consumes, and that item must exist. The grimoire
   /// gate keys off consumesItem, so a missing/mismatched value silently leaks
   /// the ability into the menu before the player has seen the potion. Logs a
   /// loud warning (rather than throwing) so a data slip is caught in the log
   /// without bricking the game.</summary>
   void ValidatePotionAbilities() {
      foreach (var (spellId, pb) in PoisonBerryAbilities) {
         var def = _gameData.Spells.Get(spellId);
         if (def == null) {
            DebugLog.Log("startup",
               $"[ValidatePotionAbilities] WARNING: built-in ability '{spellId}' has no spell def in spells.json");
            continue;
         }

         if (def.ConsumesItem != pb.itemID)
            DebugLog.Log("startup",
               $"[ValidatePotionAbilities] WARNING: '{spellId}' consumesItem='{def.ConsumesItem}' but ability consumes '{pb.itemID}' — the 'seen materials' gate will not hide it correctly. Set consumesItem to '{pb.itemID}' in spells.json.");
         if (_gameData.Items.Get(pb.itemID) == null)
            DebugLog.Log("startup",
               $"[ValidatePotionAbilities] WARNING: '{spellId}' consumes item '{pb.itemID}' which is not in the item registry.");
      }
   }

   /// <summary>Player clicked a target while holding the Poison Berries ability.
   /// Picks the nearest berry bush within range of the mouse, validates that
   /// the bush is in Berries state, that the player has the matching potion,
   /// and starts the work routine. Does NOT consume the potion — consumption
   /// only happens when the routine completes successfully (see
   /// <see cref="FinalizeBushWorkIfPending"/>).</summary>
   void TryStartPoisonBerries(int necroIdx, Vec2 mouseWorld, string buffID, string itemID) {
      if (necroIdx < 0) return;
      if (_inventory.GetItemCount(itemID) <= 0) {
         DebugLog.Log("ai", $"[PoisonBerries] no {itemID} in inventory — ignored");
         return;
      }

      // Two-stage pick: prefer the bush closest to the cursor (small radius);
      // if the click was nowhere near a bush, fall back to the bush closest to
      // the necromancer within a larger range so the ability is forgiving.
      int bushIdx = FindBerryBushNear(mouseWorld, 4f);
      if (bushIdx < 0) {
         var necroPos = _sim.Units[necroIdx].Position;
         bushIdx = FindBerryBushNear(necroPos, 20f);
      }

      if (bushIdx < 0) {
         DebugLog.Log("ai", "[PoisonBerries] no Berries-state berry bush near cursor or player");
         return;
      }

      // StartRoutine fires the OLD routine's exit cleanup (and restarts if already
      // working a bush); the new routine's fields are set after.
      AI.AIControl.StartRoutine(_sim.UnitsMut, necroIdx,
         AI.PlayerControlledHandler.RoutineWorkOnBush,
         AI.PlayerControlledHandler.BuildSub_WalkToSite);
      var u = _sim.UnitsMut[necroIdx];
      u.BushWorkObjIdx = bushIdx;
      u.BushWorkBuffID = buffID;
      u.BushWorkItemID = itemID;
      u.BuildTimer = 0f;
      u.CorpseInteractPhase = 0;
      DebugLog.Log("ai", $"[PoisonBerries] start: bushIdx={bushIdx} buff={buffID} item={itemID}");
   }

   static readonly Random _projRng = new();

   void SpawnSpellProjectile(SpellDef spell, Vec2 origin, Vec2 target, uint ownerUid, float spawnHeight) {
      _sim.Projectiles.SpawnFireball(origin, target,
         Faction.Undead, ownerUid, spell.Damage, spell.AoeRadius, spell.DisplayName,
         spawnHeight: spawnHeight);
      var projs = _sim.Projectiles.Projectiles;
      if (projs.Count > 0) {
         var lastProj = projs[projs.Count - 1];

         // Tag projectile with spell ID for physics knockback lookup on impact
         lastProj.SpellID = spell.Id;

         // Blight bombs must burst exactly where the player clicked, not wherever
         // the ballistic arc happens to land. Forward the aimed point and flag the
         // projectile so it detonates on overshoot.
         if (spell.Category == "Blight") {
            lastProj.TargetPos = target;
            lastProj.DetonateAtTarget = true;
         }

         // Apply trajectory type
         var traj = Enum.TryParse<Trajectory>(spell.Trajectory, true, out var t) ? t : Trajectory.Lob;
         var dir = (target - origin).Normalized();
         float speed = spell.ProjectileSpeed > 0 ? spell.ProjectileSpeed : ProjectileManager.MagicSpeed;

         switch (traj) {
            case Trajectory.DirectFire: {
               float theta = 5f * MathF.PI / 180f;
               lastProj.Velocity = dir * speed * MathF.Cos(theta);
               lastProj.VelocityZ = speed * MathF.Sin(theta);
               lastProj.BaseDirection = dir;
               lastProj.IsLob = false;
               break;
            }
            case Trajectory.Swirly: {
               float theta = 5f * MathF.PI / 180f;
               lastProj.Velocity = dir * speed * MathF.Cos(theta);
               lastProj.VelocityZ = speed * MathF.Sin(theta);
               lastProj.BaseDirection = dir;
               lastProj.IsLob = false;
               lastProj.SwirlFreq = 3f + (float)_projRng.NextDouble() * 5f;
               lastProj.SwirlAmplitude = 0.5f + (float)_projRng.NextDouble() * 1.5f;
               lastProj.SwirlPhase = (float)_projRng.NextDouble() * 2f * MathF.PI;
               break;
            }
            case Trajectory.Homing: {
               float theta = 5f * MathF.PI / 180f;
               lastProj.Velocity = dir * speed * MathF.Cos(theta);
               lastProj.VelocityZ = speed * MathF.Sin(theta);
               lastProj.BaseDirection = dir;
               lastProj.IsLob = false;
               lastProj.TargetPos = target;
               lastProj.HomingStrength = 5f;
               break;
            }
            case Trajectory.HomingSwirly: {
               float theta = 5f * MathF.PI / 180f;
               lastProj.Velocity = dir * speed * MathF.Cos(theta);
               lastProj.VelocityZ = speed * MathF.Sin(theta);
               lastProj.BaseDirection = dir;
               lastProj.IsLob = false;
               lastProj.TargetPos = target;
               lastProj.HomingStrength = 5f;
               lastProj.SwirlFreq = 3f + (float)_projRng.NextDouble() * 5f;
               lastProj.SwirlAmplitude = 0.5f + (float)_projRng.NextDouble() * 1.5f;
               lastProj.SwirlPhase = (float)_projRng.NextDouble() * 2f * MathF.PI;
               break;
            }
            // Lob is the default from SpawnFireball — no changes needed
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

   void TickPendingProjectiles(float dt) {
      for (int i = _pendingProjectiles.Count - 1; i >= 0; i--) {
         var pg = _pendingProjectiles[i];
         pg.Timer += dt;
         if (pg.Timer >= pg.Interval) {
            pg.Timer -= pg.Interval;
            pg.Remaining--;

            var spell = _gameData.Spells.Get(pg.SpellID);
            if (spell != null) {
               int necroIdx = FindNecromancer();
               uint ownerUid = necroIdx >= 0 ? _sim.Units[necroIdx].Id : 0;
               Vec2 origin = necroIdx >= 0 ? _sim.Units[necroIdx].EffectSpawnPos2D : pg.Origin;
               float spawnH = necroIdx >= 0 ? _sim.Units[necroIdx].EffectSpawnHeight : 0.6f;
               SpawnSpellProjectile(spell, origin, pg.Target, ownerUid, spawnH);
            }

            if (pg.Remaining <= 0) {
               _pendingProjectiles.RemoveAt(i);
               continue;
            }
         }

         _pendingProjectiles[i] = pg;
      }
   }
}