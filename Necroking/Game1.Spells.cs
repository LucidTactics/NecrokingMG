using System;
using System.Collections.Generic;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.Game;
using Necroking.GameSystems;
using Necroking.Lib;
using Necroking.Render;
using Necroking.UI;

namespace Necroking;

public partial class Game1 {
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
   internal void QueueReanimRise(string defId, int corpseId, string? configId, float delay = 3.5f,
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
   /// Execute a spell's effect (projectile, buff, strike, etc.) for any caster. Called
   /// either immediately (no casting buff / AI cast) or at the Spell1 animation action
   /// moment (player deferred cast). <paramref name="pending"/> is THIS cast's targeting
   /// record (the player's _pendingSpell, or an AI cast's own) — see
   /// SpellEffectSystem.Execute. slot -1 = AI cast (no spell bar, timer-based channel).
   /// </summary>
   void ExecuteSpellEffect(SpellDef spell, int casterIdx, Vec2 target, int slot,
      GameSystems.PendingSpellCast pending) {
      // Cast flipbook effect at caster position
      SpawnCastEffect(spell, _sim.Units[casterIdx].EffectSpawnPos2D);

      // All category logic lives in SpellEffectSystem — it reaches Game1 state
      // (death fog, channeling slot, pending projectiles/rises) through `this`.
      GameSystems.SpellEffectSystem.Execute(spell, this, casterIdx, target, slot, pending);
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
         GameSystems.SpellEffectSystem.ApplyBlight(spell, impacts[i].Position, _deathFog);
      }
   }

   /// <summary>End the player's Beam/Drain hold-channel: cut the beams/drains,
   /// drop the casting glow, and clear the slot bookkeeping. The held Spell1
   /// effect-frame pose and the movement/facing plant both release declaratively
   /// next frame (they key off _channelingSlot — see UpdateAnimations /
   /// SyncChannelPlant), so the wind-down plays and control returns seamlessly.
   /// Safe to call when nothing is channeling.</summary>
   internal void CancelPlayerChannel(int necroIdx) {
      if (necroIdx >= 0) {
         _sim.Lightning.CancelBeamsForCaster(_sim.Units[necroIdx].Id);
         _sim.Lightning.CancelDrainsForCaster(_sim.Units[necroIdx].Id);
         RemoveCastingBuffAll(necroIdx);
      }
      _channelingSlot = -1;
      _channelingSpellID = "";
      _channelAimAngleDeg = float.NaN;
   }

   /// <summary>True while the caster still sources at least one live beam or
   /// drain — the ground truth the player's _channelingSlot must stay in sync
   /// with (the sim can kill a beam on its own: target death, max duration,
   /// caster interrupt).</summary>
   bool HasActiveChannelSource(uint casterUid)
      => TryGetChannelTargetUid(casterUid, out _);

   /// <summary>Find the unit the caster's live beam/drain is attached to.
   /// Returns false when the caster has no live beam/drain; targetUid is
   /// InvalidUnit for a corpse-targeted drain.</summary>
   bool TryGetChannelTargetUid(uint casterUid, out uint targetUid) {
      var beams = _sim.Lightning.Beams;
      for (int i = 0; i < beams.Count; i++)
         if (beams[i].CasterID == casterUid) { targetUid = beams[i].TargetID; return true; }
      var drains = _sim.Lightning.Drains;
      for (int i = 0; i < drains.Count; i++)
         if (drains[i].CasterID == casterUid) {
            targetUid = drains[i].TargetCorpseIdx >= 0
               ? Core.GameConstants.InvalidUnit : drains[i].TargetID;
            return true;
         }
      targetUid = Core.GameConstants.InvalidUnit;
      return false;
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

   internal void SpawnSummonEffect(SpellDef spell, Vec2 pos) {
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

   // --- Circle-targeting aim state (SpellDef.TargetingMode == "Circle") -----
   // Spell-bar slot armed by a keypress, awaiting a confirming left-click.
   // -1 = not aiming. While armed the renderer draws the AoE circle + lights up
   // the units inside it (GameRenderer.Units.DrawSpellAimCircle); the click
   // confirm/cancel lives in Game1.WorldClicks.cs, ESC cancel in Update.
   internal int _aimingSlot = -1;

   /// <summary>The spell armed for circle-targeting, or null when not aiming.
   /// Self-heals: if the armed slot was reassigned/emptied to something that no
   /// longer circle-targets, the aim is dropped.</summary>
   internal SpellDef? AimedSpell() {
      if (_aimingSlot < 0) return null;
      SpellDef? def = _aimingSlot < _spellBarState.Slots.Length
         ? _gameData.Spells.Get(_spellBarState.Slots[_aimingSlot].SpellID) : null;
      if (def == null || def.TargetingMode != "Circle") { _aimingSlot = -1; return null; }
      return def;
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

      // A new successful cast while a Beam/Drain channel is live releases the
      // channel first — its casting is over. (The alternative, stacking a fresh
      // Spell1 on top of the held channel pose, fights the effect-frame hold and
      // strands the old beam with no slot tracking it.) A failed cast above
      // deliberately does NOT break the channel.
      if (_channelingSlot >= 0) CancelPlayerChannel(necroIdx);

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

         // Cast plant: the anim does NOT start here — the necromancer brakes to
         // a stop (locomotion keeps playing the skid) and turns toward the aim
         // at a boosted rate; TickCastPlant starts the channel once speed drops
         // below the gate. The old raw FacingAngle snap is gone — facing is
         // owned by Locomotion.UpdateFacing's cast branch for the whole cast.
         _pendingCastAnim = new PendingCastAnim {
            SpellID = spellId, Target = mouseWorld, Slot = slot,
            CastingBuffID = spell.CastingBuffID,
            CastAnim = spell.CastAnim, ChannelPhase = 0,
            ChannelElapsed = 0f, LoopElapsed = 0f, CastTime = spell.CastTime, Executed = false,
            WaitingForPlant = true,
         };
      } else if (!string.IsNullOrEmpty(spell.CastingBuffID)) {
         // Defer execution to the Spell1 animation event. Like the channel path,
         // the Spell1 anim itself starts in TickCastPlant once braked below the
         // gate (walking casts pass the gate the same frame — no added latency).
         var castBuff = _gameData.Buffs.Get(spell.CastingBuffID);
         if (castBuff != null) BuffSystem.ApplyBuff(_sim.UnitsMut, necroIdx, castBuff);

         _pendingCastAnim = new PendingCastAnim {
            SpellID = spellId,
            Target = mouseWorld,
            Slot = slot,
            CastingBuffID = spell.CastingBuffID,
            WaitingForPlant = true,
         };
      } else {
         // No casting buff → execute immediately (legacy behavior; Q6 — no anim,
         // no pose to mismatch, so no plant either: castable at a full sprint).
         ExecuteSpellEffect(spell, necroIdx, mouseWorld, slot, _pendingSpell);
         return CastResult.Success;
      }

      // Start braking the same frame as the press (Game1's input block runs
      // before _sim.Tick). TickCastPlant re-syncs this every frame after.
      var aimDir = mouseWorld - _sim.Units[necroIdx].Position;
      _sim.SetNecromancerCasting(true, aimDir.LengthSq() > 0.0001f
         ? MathF.Atan2(aimDir.Y, aimDir.X) * 180f / MathF.PI : float.NaN);

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

   void TickPendingProjectiles(float dt) {
      for (int i = _pendingProjectiles.Count - 1; i >= 0; i--) {
         var pg = _pendingProjectiles[i];

         // Follow-up shots track the group's OWN caster (player or AI) by stable
         // id; a caster that died mid-volley stops shooting.
         int casterIdx = _sim.ResolveUnitID(pg.CasterUid);
         if (casterIdx < 0 || !_sim.Units[casterIdx].Alive) {
            _pendingProjectiles.RemoveAt(i);
            continue;
         }

         var spell = _gameData.Spells.Get(pg.SpellID);

         // Player volleys chase the live cursor: each follow-up shot re-aims at the
         // current cursor world position. The cursor only UPDATES the aim point — when
         // it's invalid this frame (_cursorAimWorld null: unfocused window, cursor
         // outside the viewport) the group keeps its last valid target. AI volleys
         // share this list and must not track the player's cursor. Barrages opt out
         // (TracksCursor=false) so the whole spread doesn't home onto the cursor.
         if (_cursorAimWorld.HasValue && spell != null && spell.TracksCursor
             && _sim.Units[casterIdx].AI == AIBehavior.PlayerControlled)
            pg.Target = _cursorAimWorld.Value;

         pg.Timer += dt;
         if (pg.Timer >= pg.Interval) {
            pg.Timer -= pg.Interval;
            pg.Remaining--;

            if (spell != null) {
               // Re-sample the spread around the group's base target for THIS shot, so
               // a barrage scatters evenly over its disc instead of retracing one line.
               var shotTarget = GameSystems.SpellEffectSystem.VolleyAimPoint(spell, pg.Target, _rng);
               GameSystems.SpellEffectSystem.SpawnProjectile(spell, _sim.Projectiles,
                  _sim.Units[casterIdx].EffectSpawnPos2D, shotTarget, pg.CasterUid,
                  _sim.Units[casterIdx].EffectSpawnHeight, _sim.Units[casterIdx].Faction);
            }

            if (pg.Remaining <= 0) {
               _pendingProjectiles.RemoveAt(i);
               continue;
            }
         }

         _pendingProjectiles[i] = pg;
      }
   }

   // Scratch targeting record for AI casts — reset by every TryStartSpellCast and
   // consumed immediately by ExecuteSpellEffect within the same drain iteration, so
   // one instance serves all AI casters (unlike the player's _pendingSpell, which
   // must survive a multi-second cast anim/channel).
   readonly GameSystems.PendingSpellCast _aiPendingSpell = new();

   /// <summary>Run the AI cast requests queued by archetype handlers during this
   /// tick (AIContext.SpellCasts) through the SAME pipeline as the player:
   /// SpellCaster.TryStartSpellCast (targeting + path/mana/cooldown gates, paid from
   /// the unit's own mana + per-spell cooldowns via UnitCasterResources) then
   /// ExecuteSpellEffect (all categories — projectiles/multi-shot, strikes, clouds,
   /// beams/drains with a timer channel, summons, buffs). Player-only side effects
   /// (slot flash, cast-fail text, PlayerEvents.Tally, cast plant) deliberately
   /// don't happen here. Called once per tick right after the sim.</summary>
   void DrainAISpellCasts() {
      var requests = _sim.PendingAISpellCasts;
      if (requests.Count == 0) return;
      for (int r = 0; r < requests.Count; r++) {
         var req = requests[r];
         int casterIdx = _sim.ResolveUnitID(req.CasterId);
         if (casterIdx < 0 || !_sim.Units[casterIdx].Alive) continue;
         var spell = _gameData.Spells.Get(req.SpellID);
         if (spell == null) continue;

         var resources = new Movement.UnitCasterResources(_sim.UnitsMut, casterIdx);
         var result = SpellCaster.TryStartSpellCast(req.SpellID, _gameData.Spells,
            resources, _sim.Units, casterIdx, req.Target, _sim.Corpses,
            _aiPendingSpell, _gameData);
         if (result != CastResult.Success) continue;

         // Casting buff (glow/weapon particle) — same visual the legacy AI cast
         // applied; it expires on its own duration since AI has no anim-end hook.
         if (!string.IsNullOrEmpty(spell.CastingBuffID)) {
            var castBuff = _gameData.Buffs.Get(spell.CastingBuffID);
            if (castBuff != null) BuffSystem.ApplyBuff(_sim.UnitsMut, casterIdx, castBuff, _gameData);
         }

         ExecuteSpellEffect(spell, casterIdx, req.Target, slot: -1, _aiPendingSpell);
      }
      requests.Clear();
   }
}
