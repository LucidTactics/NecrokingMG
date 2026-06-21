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

            var spawnPos = corpse.Position;
            float corpseFacing = corpse.FacingAngle;
            _sim.ConsumeCorpse(i);

            SpawnUnit(resolvedID, spawnPos);
            int idx = _sim.Units.Count - 1;
            if (idx >= 0) {
               _sim.UnitsMut[idx].FacingAngle = corpseFacing;
               _sim.UnitsMut[idx].StandupTimer = 1.5f;
               // Add to horde if undead
               if (_sim.Units[idx].Faction == Faction.Undead &&
                   _sim.Units[idx].AI != AIBehavior.PlayerControlled)
                  _sim.Horde.AddUnit(_sim.Units[idx].Id);
               raised++;
            }

            // Spawn summon effect at each corpse location
            SpawnSummonEffect(spell, spawnPos);
         }
      } else {
         // Single corpse consume (Corpse targeting)
         float corpseFacing = -1f; // -1 = no corpse consumed
         string summonUnitID = pending.SummonUnitID;

         if (spell.SummonTargetReq == "Corpse" && pending.TargetCorpseIdx >= 0) {
            // Resolve zombie type from corpse if summonUnitID is empty.
            // Shared helper: see comment on the AOE branch above.
            if (string.IsNullOrEmpty(summonUnitID) && pending.TargetCorpseIdx < _sim.Corpses.Count) {
               var corpse = _sim.Corpses[pending.TargetCorpseIdx];
               summonUnitID = Game.TableCraftingSystem.ResolveZombieUnitID(_gameData, corpse.UnitDefID);
            }

            if (pending.TargetCorpseIdx < _sim.Corpses.Count) {
               corpseFacing = _sim.Corpses[pending.TargetCorpseIdx].FacingAngle;
               _sim.ConsumeCorpse(pending.TargetCorpseIdx);
            }
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

               SpawnUnit(summonUnitID, unitSpawnPos);
               int idx = _sim.Units.Count - 1;
               if (idx >= 0) {
                  // Inherit corpse rotation for reanimated units
                  if (corpseFacing >= 0f) {
                     _sim.UnitsMut[idx].FacingAngle = corpseFacing;
                     _sim.UnitsMut[idx].StandupTimer = 1.5f;
                  }

                  // Add to horde if undead
                  if (_sim.Units[idx].Faction == Faction.Undead &&
                      _sim.Units[idx].AI != AIBehavior.PlayerControlled)
                     _sim.Horde.AddUnit(_sim.Units[idx].Id);
               }
            }

            // Spawn summon effect at the primary spawn location
            SpawnSummonEffect(spell, spawnPos);
         }
      }
   }

   /// <summary>
   /// Spawn the visual summon flipbook effect at a given position.
   /// </summary>
   void SpawnCastEffect(SpellDef spell, Vec2 pos) {
      if (spell.CastFlipbook == null || string.IsNullOrEmpty(spell.CastFlipbook.FlipbookID)) return;

      var fb = spell.CastFlipbook;
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
         (sp, cIdx) => ExecuteSummonSpell(sp, _pendingSpell, _sim.Units[cIdx].Position, cIdx));

      // Apply side effects that SpellEffectSystem can't own (Game1 state)
      if (result.ChannelingSlot >= 0)
         _channelingSlot = result.ChannelingSlot;
      if (result.PendingProjectile.HasValue)
         _pendingProjectiles.Add(result.PendingProjectile.Value);
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
      if (spell.SummonFlipbook == null || string.IsNullOrEmpty(spell.SummonFlipbook.FlipbookID)) return;

      var fb = spell.SummonFlipbook;
      var tint = fb.Color.ToColor();
      int blendMode = fb.BlendMode == "Additive" ? 1 : 0;
      int alignment = fb.Alignment == "Upright" ? 1 : 0;
      float duration = fb.Duration >= 0f ? fb.Duration : 0.4f;

      _effectManager.SpawnSpellImpact(pos, fb.Scale, tint, fb.FlipbookID,
         fb.Color.Intensity, blendMode, alignment, duration);
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

   /// <summary>Single dispatch path for both spell bars. Handles built-in
   /// ability intercepts (melee_gather, poison_berries_*) before falling
   /// through to the normal SpellCaster + casting-buff + pending-anim pipeline.
   /// Returns the cast result so callers can react (e.g. LMB melee fallback).</summary>
   /// <summary>Light up a hotbar slot for SlotFlashDuration so a fired spell gives
   /// immediate visual feedback. Decayed in real time in Update; drawn by the HUD.</summary>
   void FlashSpellSlot(int slot, bool isSecondary) {
      var arr = isSecondary ? _secondarySlotFlash : _primarySlotFlash;
      if (slot >= 0 && slot < arr.Length) arr[slot] = HUDRenderer.SlotFlashDuration;
   }

   CastResult DispatchSpellCast(string spellId, int necroIdx, int slot,
      Vec2 mouseWorld, bool isSecondary) {
      if (string.IsNullOrEmpty(spellId) || necroIdx < 0) return CastResult.NoValidTarget;

      // Built-in abilities short-circuit the normal spell pipeline.
      if (TryDispatchBuiltinAbility(spellId, necroIdx, mouseWorld)) {
         FlashSpellSlot(slot, isSecondary);
         return CastResult.Success;
      }

      // Potion-spells (ConsumesItem set) run through the existing PotionSystem
      // throw/drink path + inventory consume, not the normal spell pipeline.
      var spellDef = _gameData.Spells.Get(spellId);
      if (spellDef != null && !string.IsNullOrEmpty(spellDef.ConsumesItem)) {
         CastPotionSpell(spellId, spellDef.ConsumesItem, necroIdx, mouseWorld);
         FlashSpellSlot(slot, isSecondary);
         return CastResult.Success;
      }

      // Can't cast a real spell while one is mid-animation.
      if (_pendingCastAnim != null) return CastResult.NoValidTarget;

      var result = SpellCaster.TryStartSpellCast(spellId, _gameData.Spells, _sim.NecroState,
         _sim.Units, necroIdx, mouseWorld, _sim.Corpses, _pendingSpell, _gameData);
      if (result == CastResult.HordeCapFull)
         SpawnHordeCapText(necroIdx);
      if (result != CastResult.Success) return result;

      // Successful real-spell cast → light up its hotbar slot.
      FlashSpellSlot(slot, isSecondary);

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
            SpellID = spellId, Target = mouseWorld, Slot = slot, IsSecondary = isSecondary,
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
            IsSecondary = isSecondary,
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

      if (spellId == "order_attack") {
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
      if (necro.GetCooldown("order_attack") > 0f) return;   // still recharging

      var units = _sim.UnitsMut;
      for (int ci = 0; ci < units.Count; ci++) {
         if (!units[ci].Alive) continue;
         if (units[ci].Faction != Faction.Undead) continue;
         if (units[ci].Archetype != AI.ArchetypeRegistry.HordeMinion) continue;

         units[ci].Routine = 4;            // RoutineCommanded
         units[ci].Subroutine = 0;
         units[ci].SubroutineTimer = 0f;
         units[ci].MoveTarget = target;
         units[ci].Target = CombatTarget.None;
         units[ci].EngagedTarget = CombatTarget.None;
      }

      float cd = _gameData.Spells.Get("order_attack")?.Cooldown ?? 0f;
      if (cd > 0f) necro.SpellCooldowns["order_attack"] = cd;
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
         if (units[ci].Routine != 4) continue;   // only those under a command (RoutineCommanded)

         units[ci].Routine = 3;            // RoutineReturning → back to formation
         units[ci].Subroutine = 0;
         units[ci].SubroutineTimer = 0f;
         units[ci].Target = CombatTarget.None;
         units[ci].EngagedTarget = CombatTarget.None;
         units[ci].InCombat = false;
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

      var u = _sim.UnitsMut[necroIdx];
      u.Routine = AI.PlayerControlledHandler.RoutineWorkOnBush;
      u.Subroutine = AI.PlayerControlledHandler.BuildSub_WalkToSite;
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