using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.Core;
using Necroking.Render;
using Necroking.Movement;
using Necroking.Game;
using Necroking.GameSystems;
using Necroking.World;
using Necroking.Scenario;
using Necroking.Editor;
using Necroking.Lib;
using Necroking.UI;

namespace Necroking;

// Game1 partial: resource gathering & crafting.
// Resource- and craft-related code belongs here (foragables, table craft, etc.).
// Note: poison-berry abilities live in Game1.Spells.cs, not here.
public partial class Game1
{
    private int FindNearestForagable(Vec2 fromPos, float maxDist)
        => _foragables.FindNearest(fromPos, maxDist);

    /// <summary>
    /// Start (or resume) a craft on the given table. Spends essence on the FIRST
    /// start of a fresh craft (ts.Crafting=false). When called while ts.Crafting=true,
    /// no essence is spent — this is the resume path after the player walked away
    /// from a paused channel. Always (re)assigns the necromancer to RoutineCraftAtTable.
    /// Returns true if the craft is now active.
    /// </summary>
    private bool StartTableCraft(int envIdx)
    {
        if (envIdx < 0 || _envSystem == null) return false;
        int necroIdx = _sim.NecromancerIndex;
        if (necroIdx < 0) return false;

        // No-op if the necromancer is already in the craft routine for THIS table —
        // double-clicking Start mid-channel would otherwise reset WalkToSite and
        // restart the walk-up animation.
        if (_sim.Units[necroIdx].Routine == AI.PlayerControlledHandler.RoutineCraftAtTable
            && _sim.Units[necroIdx].CraftTableIdx == envIdx)
            return true;

        var def = _envSystem.Defs[_envSystem.GetObject(envIdx).DefIndex];
        var ts = _envSystem.GetTableState(envIdx);

        if (!ts.Crafting)
        {
            // Fresh craft — gate on inputs + spend essence.
            if (!ts.HasAnyCorpse()) return false;

            // Horde-cap gate: peek at the corpse that's about to be raised and
            // refuse if the resulting unit's category is full. Mirrors the spell
            // pre-check so essence isn't spent on a craft that would produce
            // nothing. The actual completion in TableCraftingSystem.CompleteCraft
            // re-checks under the same conditions (state may have shifted).
            int peekSlot = -1;
            for (int i = 0; i < ts.CorpseSlots.Length; i++)
                if (!ts.CorpseSlots[i].IsEmpty) { peekSlot = i; break; }
            if (peekSlot >= 0)
            {
                string peekZombie = Game.TableCraftingSystem.ResolveZombieUnitID(
                    _gameData, ts.CorpseSlots[peekSlot].SourceUnitDefID);
                if (!string.IsNullOrEmpty(peekZombie))
                {
                    var peekCat = HordeCapTracker.CategoryFor(_gameData, peekZombie);
                    if (peekCat != UndeadCategory.None
                        && HordeCapTracker.Available(_sim.Units, _gameData, _sim.NecroState, peekCat) <= 0)
                    {
                        SpawnHordeCapText(necroIdx);
                        return false;
                    }
                }
            }

            if (!_sim.PlayerResources.SpendEssence(def.EssenceCost)) return false;
            ts.Crafting = true;
            ts.CraftTimer = 0f;
            ts.LoopBudget = 0f; // recomputed render-side once the imbue loop starts
        }
        // else: resume — essence already spent on first start; just reassign channeler.

        ts.ChannelerUnitID = _sim.Units[necroIdx].Id;
        // StartRoutine fires the OLD routine's exit cleanup (and restarts the craft
        // routine when retargeting a different table); fields are set after.
        AI.AIControl.StartRoutine(_sim.UnitsMut, necroIdx,
            AI.PlayerControlledHandler.RoutineCraftAtTable,
            AI.PlayerControlledHandler.BuildSub_WalkToSite);
        _sim.UnitsMut[necroIdx].CraftTableIdx = envIdx;
        _sim.UnitsMut[necroIdx].BuildTimer = 0f;
        return true;
    }

    /// <summary>Scheduled resolution of a corpse put-down: the delay is the PutDown clip's
    /// length, the payload is <see cref="CompleteCorpsePutDown"/>. Queued by
    /// <see cref="BeginCorpsePutDown"/> on the sim clock (<c>_sim.Tasks</c>).</summary>
    sealed class CorpsePutDownTask : ScheduledTask
    {
        public int CorpseId;
        public int TableIdx;
        public override string Describe() => $"CorpsePutDown(corpse={CorpseId}, table={TableIdx})";
        protected internal override void Fire()
            => Instance.CompleteCorpsePutDown(CorpseId, TableIdx);
    }

    /// <summary>Begin a corpse put-down (onto a craft table, or the ground when
    /// <paramref name="tableIdx"/> &lt; 0). Enters the visual PutDown phase AND schedules the
    /// gameplay resolution on the sim clock — the transfer/craft no longer waits on the
    /// PutDown animation reporting IsAnimFinished (the old anim-coupling anti-pattern).
    ///
    /// The scheduled delay is the natural length of the PutDown clip, so the speed-1 animation
    /// lands exactly as the event fires; if the clip has no timing we fall back to a fixed
    /// duration and the animation still just reflects it. Caller sets the corpse's LerpStartPos
    /// (table spawn slot vs. ground drop point) before calling. See ScheduledTasks /
    /// Render.AnimTiming for the pattern.</summary>
    internal void BeginCorpsePutDown(int necroIdx, int tableIdx)
    {
        if (necroIdx < 0) return;
        _sim.UnitsMut[necroIdx].PutDownTableIdx = tableIdx;
        _sim.UnitsMut[necroIdx].CorpseInteractPhase = 5; // PutDown (visual only from here on)

        int corpseId = _sim.Units[necroIdx].CarryingCorpseID;

        float putDownSeconds = 0.5f; // fallback if the clip has no timing metadata
        uint uid = _sim.Units[necroIdx].Id;
        if (_unitAnims.TryGetValue(uid, out var anim))
        {
            float natural = Render.AnimTiming.NaturalSeconds(anim.Ctrl, AnimState.PutDown);
            if (natural > 0.01f) putDownSeconds = natural;
        }

        // corpseId/tableIdx are stable across index churn; resolve the necromancer at fire time.
        _sim.Tasks.Schedule(new CorpsePutDownTask { CorpseId = corpseId, TableIdx = tableIdx },
            putDownSeconds);
    }

    /// <summary>Scheduled resolution of a corpse pickup: the delay is the Pickup clip's
    /// length, the payload is <see cref="CompleteCorpsePickup"/>. Queued by
    /// <see cref="BeginCorpsePickup"/> on the sim clock — the PutDown pattern's mirror.</summary>
    sealed class CorpsePickupTask : ScheduledTask
    {
        public int CorpseId;
        public override string Describe() => $"CorpsePickup(corpse={CorpseId})";
        protected internal override void Fire()
            => Instance.CompleteCorpsePickup(CorpseId);
    }

    /// <summary>Begin a corpse pickup: enters the visual Pickup phase AND schedules the
    /// carry handoff on the sim clock, so completion never waits on the Pickup animation
    /// reporting IsAnimFinished (the anim-coupling anti-pattern; see BeginCorpsePutDown).
    /// Caller has already set CarryingCorpseID + the corpse's LerpStartPos/DraggedByUnitID.</summary>
    internal void BeginCorpsePickup(int necroIdx, int corpseId)
    {
        if (necroIdx < 0) return;
        _sim.UnitsMut[necroIdx].CorpseInteractPhase = 4; // Pickup (visual only from here on)

        float pickupSeconds = 0.5f; // fallback if the clip has no timing metadata
        uint uid = _sim.Units[necroIdx].Id;
        if (_unitAnims.TryGetValue(uid, out var anim))
        {
            float natural = Render.AnimTiming.NaturalSeconds(anim.Ctrl, AnimState.Pickup);
            if (natural > 0.01f) pickupSeconds = natural;
        }

        _sim.Tasks.Schedule(new CorpsePickupTask { CorpseId = corpseId }, pickupSeconds);
    }

    /// <summary>Resolve a scheduled corpse pickup (fired from Simulation.Tick via
    /// CorpsePickupTask): leave the Pickup phase and hold the Carry pose frozen until the
    /// unit moves (locomotion re-drives PlaybackSpeed for Carry). Re-validates the
    /// necromancer is still mid-Pickup carrying this exact corpse, so a stale event
    /// (interaction cancelled, corpse gone) is a safe no-op.</summary>
    private void CompleteCorpsePickup(int corpseId)
    {
        int necroIdx = _sim.NecromancerIndex;
        if (necroIdx < 0) return;
        if (_sim.Units[necroIdx].CorpseInteractPhase != 4
            || _sim.Units[necroIdx].CarryingCorpseID != corpseId)
            return; // stale — the pickup was cancelled/superseded

        _sim.UnitsMut[necroIdx].CorpseInteractPhase = 0;
        if (_unitAnims.TryGetValue(_sim.Units[necroIdx].Id, out var anim))
        {
            anim.Ctrl.ForceState(AnimState.Carry);
            anim.Ctrl.PlaybackSpeed = 0f; // freeze until the unit moves
        }
    }

    /// <summary>Resolve a scheduled corpse put-down (fired from Simulation.Tick via
    /// CorpsePutDownTask): load the carried corpse into the table slot and remove it from the sim
    /// (table drop), or settle it flat at the drop point (ground drop), then clear carry state
    /// and auto-start the craft. Re-validates the necromancer is still mid-PutDown carrying this
    /// exact corpse, so a stale event (interaction cancelled, corpse gone) is a safe no-op.</summary>
    private void CompleteCorpsePutDown(int corpseId, int tableIdx)
    {
        int necroIdx = _sim.NecromancerIndex;
        if (necroIdx < 0) return;
        if (_sim.Units[necroIdx].CorpseInteractPhase != 5
            || _sim.Units[necroIdx].CarryingCorpseID != corpseId)
            return; // stale — the put-down was cancelled/superseded

        var cc = _sim.FindCorpseByID(corpseId);

        bool loadedIntoTable = false;
        if (tableIdx >= 0 && _envSystem != null && cc != null
            && TableSystem.LoadCorpseIntoTable(_envSystem, tableIdx, cc) >= 0)
        {
            int ci = _sim.FindCorpseIndexByID(corpseId);
            if (ci >= 0) _sim.CorpsesMut.RemoveAt(ci);
            loadedIntoTable = true;
        }
        else if (cc != null)
        {
            // Ground drop (or table-load fell through, e.g. slot taken). Land flat at the
            // drop point — zero Z/physics so the settled draw lands where the put-down was.
            cc.Position = cc.LerpStartPos;
            cc.Z = 0f;
            cc.InPhysics = false;
            cc.DraggedByUnitID = GameConstants.InvalidUnit;
        }

        _sim.UnitsMut[necroIdx].CarryingCorpseID = -1;
        _sim.UnitsMut[necroIdx].CorpseInteractPhase = 0;
        _sim.UnitsMut[necroIdx].PutDownTableIdx = -1;
        // Animation returns to Idle on its own next frame: the phase-0 work-anim guard in
        // UpdateAnimations forces PutDown → Idle once CorpseInteractPhase is 0.

        // Auto-start production; runs after the carry-state reset above or the unit is still
        // IsLockedByAction when the craft routine starts. If refused (no essence / horde cap),
        // fall back to opening the menu so the drop doesn't look like it ate the corpse for free.
        if (loadedIntoTable && !StartTableCraft(tableIdx))
        {
            int sw = _graphics.PreferredBackBufferWidth;
            int sh = _graphics.PreferredBackBufferHeight;
            EnsureInventoryUIsInitialized();
            _tableMenuUI.OpenForTable(tableIdx, sw, sh, _camera, _renderer);
        }
    }

    /// <summary>Start a foragable collection with arc animation instead of instant pickup.</summary>
    private void StartForagableCollection(int objIdx) => _foragables.StartCollection(objIdx);

    /// <summary>Game1 hook fired by ForagableSystem after a pickup lands.
    /// Spawns the floating green pickup text in the damage-numbers list.</summary>
    private void OnForagablePickedUp(Vec2 worldPos, string resourceType)
    {
        // Fixed 2f lift: the anchor is a ground foragable, not a unit's head.
        FloatingText.AddText(_damageNumbers, worldPos, resourceType, height: 2f);
    }

    /// <summary>Simulation hook fired when a foraging boar swallows a mushroom.
    /// Plays the same pickup pop the player hears, throttled + pitch-rotated so a
    /// pack of grazing boars doesn't machine-gun the sample.</summary>
    private void OnForagerAte(Vec2 worldPos)
    {
        if (_pickupSound == null || _foragerEatSoundCd > 0f) return;
        _foragerEatSoundCd = 0.06f;
        _foragerEatSoundStep = (_foragerEatSoundStep + 1) % 5;
        float pitch = -0.15f + 0.075f * _foragerEatSoundStep;   // -0.15 .. +0.15
        _pickupSound.Play(0.3f, pitch, 0f);
    }

    /// <summary>Game1 hook fired by ForagableSystem on every pickup. First
    /// mushroom of any kind teaches the root Paralysis potion recipe.</summary>
    private void OnForagableLearnTrigger(string resourceType)
    {
        if (resourceType == "Mushroom"
            || resourceType == "MagicMushroom"
            || resourceType == "PoisonMushroom"
            || resourceType == "Ghostcap"
            || resourceType == "Rotgill")
        {
            TryAutoLearn("skill_paralysis", "Recipe Learned");
        }
    }
}
