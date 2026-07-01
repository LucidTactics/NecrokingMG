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
        _sim.UnitsMut[necroIdx].CraftTableIdx = envIdx;
        _sim.UnitsMut[necroIdx].Routine = AI.PlayerControlledHandler.RoutineCraftAtTable;
        _sim.UnitsMut[necroIdx].Subroutine = AI.PlayerControlledHandler.BuildSub_WalkToSite;
        _sim.UnitsMut[necroIdx].BuildTimer = 0f;
        return true;
    }

    /// <summary>Start a foragable collection with arc animation instead of instant pickup.</summary>
    private void StartForagableCollection(int objIdx) => _foragables.StartCollection(objIdx);

    /// <summary>Game1 hook fired by ForagableSystem after a pickup lands.
    /// Spawns the floating green pickup text in the damage-numbers list.</summary>
    private void OnForagablePickedUp(Vec2 worldPos, string resourceType)
    {
        _damageNumbers.Add(new DamageNumber
        {
            WorldPos = worldPos,
            Damage = 0,
            Timer = 0f,
            Height = 2f,
            IsPoison = false,
            PickupText = resourceType,
        });
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
