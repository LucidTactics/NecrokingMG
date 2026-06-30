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

// Game1 partial: per-frame animation, cast-phase and attack-anim updates.
public partial class Game1
{
    /// <summary>
    /// Rebuild animation data for a unit (e.g. after transform).
    /// </summary>
    private void RebuildUnitAnim(int unitIdx, string unitDefID)
    {
        var unitDef = _gameData.Units.Get(unitDefID);
        if (unitDef?.Sprite == null) return;

        var atlasId = AtlasDefs.ResolveAtlasName(unitDef.Sprite.AtlasName);
        var spriteData = _atlases[atlasId].GetUnit(unitDef.Sprite.SpriteName);
        if (spriteData == null) return;

        var ctrl = new AnimController();
        ctrl.Init(spriteData);
        ctrl.ForceState(AnimState.Idle);

        if (_animMeta.Count > 0)
            ctrl.SetAnimMeta(_animMeta, unitDef.Sprite.SpriteName);

        if (unitDef.AttackAnim != null)
            ctrl.SetAttackAnimOverride(unitDef.AttackAnim);

        float refH = 128f;
        var idleAnim = spriteData.GetAnim("Idle");
        if (idleAnim != null)
        {
            var kfs = GameRenderer.PickIdleFrames(idleAnim);
            if (kfs != null && kfs.Count > 0)
                refH = kfs[0].Frame.Rect.Height;
        }

        _unitAnims[_sim.Units[unitIdx].Id] = new UnitAnimData
        {
            Ctrl = ctrl,
            AtlasID = atlasId,
            RefFrameHeight = refH,
            CachedDefID = unitDefID
        };
    }

    /// <summary>Per-unit attack cycle in seconds: weapon.CooldownRounds × RoundDuration.
    /// Falls back to 1 round when the unit has no melee weapon defined.</summary>
    private float ComputeWeaponCycleSeconds(int unitIdx, int weaponIdx)
    {
        float round = _gameData.Settings.Combat.RoundDuration;
        int cdRounds = 1;
        var stats = _sim.Units[unitIdx].Stats;
        if (weaponIdx >= 0 && weaponIdx < stats.MeleeWeapons.Count)
            cdRounds = Math.Max(1, stats.MeleeWeapons[weaponIdx].CooldownRounds);
        return cdRounds * round;
    }

    /// <summary>
    /// Map a pending attack's chosen weapon to an AnimState. Reads the weapon's
    /// AnimName field; falls back to "Ranged1" for ranged and "Attack1" for melee.
    /// Custom anim names (e.g. "AttackBite") map onto Attack1/Ranged1 — the
    /// per-unit AttackAnim override on AnimController handles the actual sprite swap.
    /// </summary>
    private static AnimState ResolvePendingAttackAnim(Data.UnitStats stats,
        int weaponIdx, bool isRanged, byte archetype)
    {
        string? animName = null;
        if (isRanged)
        {
            if (weaponIdx >= 0 && weaponIdx < stats.RangedWeapons.Count)
                animName = stats.RangedWeapons[weaponIdx].AnimName;
        }
        else
        {
            if (weaponIdx >= 0 && weaponIdx < stats.MeleeWeapons.Count)
                animName = stats.MeleeWeapons[weaponIdx].AnimName;
        }

        // Legacy archer fallback: if archetype is ArcherUnit but pending fields weren't
        // set (e.g. unarmed test scenarios), still play Ranged1.
        bool effectiveRanged = isRanged || archetype == AI.ArchetypeRegistry.ArcherUnit;

        if (string.IsNullOrEmpty(animName))
            return effectiveRanged ? AnimState.Ranged1 : AnimState.Attack1;

        return animName switch
        {
            "Attack1"  => AnimState.Attack1,
            "Attack2"  => AnimState.Attack2,
            "Attack3"  => AnimState.Attack3,
            "Ranged1"  => AnimState.Ranged1,
            "Spell1"   => AnimState.Spell1,
            "Special1" => AnimState.Special1,
            _          => effectiveRanged ? AnimState.Ranged1 : AnimState.Attack1,
        };
    }

    /// <summary>Drive a channeled reanimation cast: Start → Loop → (Finish). The
    /// spell effect fires at the END of the loop; total loop time = CastTime minus
    /// the Start duration, with a minimum of one full loop cycle. The necromancer
    /// faces the target throughout. Raise has no Finish → straight to Idle.</summary>
    private void UpdateChanneledCast(float dt)
    {
        if (_pendingCastAnim == null) return;
        int necroIdx = FindNecromancer();
        if (necroIdx < 0) { _pendingCastAnim = null; return; }
        uint uid = _sim.Units[necroIdx].Id;
        if (!_unitAnims.TryGetValue(uid, out var anim)) { _pendingCastAnim = null; return; }
        var ctrl = anim.Ctrl;

        var pca = _pendingCastAnim.Value;
        GetChannelStates(pca.CastAnim, out var startS, out var loopS, out var finishS);

        // Keep facing the target for the whole channel.
        var dir = pca.Target - _sim.Units[necroIdx].Position;
        if (dir.LengthSq() > 0.0001f)
            _sim.UnitsMut[necroIdx].FacingAngle = MathF.Atan2(dir.Y, dir.X) * 180f / MathF.PI;

        pca.ChannelElapsed += dt;

        switch (pca.ChannelPhase)
        {
            case 0: // Start (play once, hold at end)
                if (ctrl.CurrentState != startS) ctrl.ForceState(startS);
                if (ctrl.IsAnimFinished)
                {
                    pca.ChannelPhase = 1;
                    pca.LoopElapsed = 0f;
                    ctrl.ForceState(loopS);
                }
                break;

            case 1: // Loop — fire the effect at the end of the loop
                if (ctrl.CurrentState != loopS) ctrl.ForceState(loopS);
                pca.LoopElapsed += dt;
                float startDur = pca.ChannelElapsed - pca.LoopElapsed;
                float oneCycle = MathF.Max(0.05f, ctrl.CurrentAnimDurationMs / 1000f);
                float loopTarget = MathF.Max(pca.CastTime - startDur, oneCycle);
                if (pca.LoopElapsed >= loopTarget)
                {
                    var spell = _gameData.Spells.Get(pca.SpellID);
                    if (spell != null) ExecuteSpellEffect(spell, necroIdx, pca.Target, pca.Slot, pca.IsSecondary);
                    if (finishS.HasValue)
                    {
                        pca.ChannelPhase = 2;
                        ctrl.ForceState(finishS.Value);
                    }
                    else
                    {
                        ctrl.ForceState(AnimState.Idle);
                        RemoveCastingBuffAll(necroIdx);
                        _pendingCastAnim = null;
                        return;
                    }
                }
                break;

            case 2: // Finish (play once)
                if (finishS.HasValue && ctrl.CurrentState != finishS.Value) ctrl.ForceState(finishS.Value);
                if (ctrl.IsAnimFinished)
                {
                    ctrl.ForceState(AnimState.Idle);
                    RemoveCastingBuffAll(necroIdx);
                    _pendingCastAnim = null;
                    return;
                }
                break;
        }

        _pendingCastAnim = pca;
    }

    /// <summary>Apply/remove the green casting glow on the necromancer based on
    /// whether it's actively channeling (imbuing) at a craft table. Idempotent —
    /// covers every start/cancel/complete path in one place.</summary>
    private void UpdateTableChannelBuff()
    {
        int necroIdx = FindNecromancer();
        if (necroIdx < 0) return;
        bool channeling = _sim.Units[necroIdx].CraftTableIdx >= 0
            && _sim.Units[necroIdx].CorpseInteractPhase != 0;
        bool has = BuffSystem.HasBuff(_sim.UnitsMut, necroIdx, TableChannelBuffId);
        if (channeling && !has)
        {
            var b = _gameData.Buffs.Get(TableChannelBuffId);
            if (b != null) BuffSystem.ApplyBuff(_sim.UnitsMut, necroIdx, b);
        }
        else if (!channeling && has)
        {
            BuffSystem.RemoveBuff(_sim.UnitsMut, necroIdx, TableChannelBuffId);
        }
    }

    private void UpdateAnimations(float dt)
    {
        for (int i = 0; i < _sim.Units.Count; i++)
        {
            if (!_sim.Units[i].Alive) continue;

            uint uid = _sim.Units[i].Id;

            // Drop the cached anim if the unit's def was swapped (e.g. necromancer
            // morph via Metamorphosis skill). Otherwise the controller stays bound
            // to the old atlas + sprite and the visible form never updates.
            if (_unitAnims.TryGetValue(uid, out var existing)
                && existing.CachedDefID != _sim.Units[i].UnitDefID)
                _unitAnims.Remove(uid);

            if (!_unitAnims.TryGetValue(uid, out var animData))
            {
                // Try to init from defID
                string defID = _sim.Units[i].UnitDefID;
                var unitDef = _gameData.Units.Get(defID);
                // Defensive: a reanimated unit whose sprite can't resolve would never
                // get an AnimController, so neither recovery path could ever tick its
                // recover-timer down — leaving it Incap-locked forever. Release the
                // lock here so a missing/broken sprite degrades to "no rise", not "stuck".
                if (unitDef?.Sprite == null) { if (_sim.Units[i].Incap.Recovering) _sim.UnitsMut[i].Incap = default; continue; }
                var atlasId = AtlasDefs.ResolveAtlasName(unitDef.Sprite.AtlasName);
                var spriteData = _atlases[atlasId].GetUnit(unitDef.Sprite.SpriteName);
                if (spriteData == null) { if (_sim.Units[i].Incap.Recovering) _sim.UnitsMut[i].Incap = default; continue; }

                var ctrl = new AnimController();
                ctrl.Init(spriteData);
                if (_animMeta.Count > 0)
                    ctrl.SetAnimMeta(_animMeta, unitDef.Sprite.SpriteName);
                if (unitDef.AttackAnim != null)
                    ctrl.SetAttackAnimOverride(unitDef.AttackAnim);
                animData = new UnitAnimData { Ctrl = ctrl, AtlasID = atlasId, RefFrameHeight = 128f, CachedDefID = defID };

                var idleAnim = spriteData.GetAnim("Idle");
                if (idleAnim != null)
                {
                    var kfs = GameRenderer.PickIdleFrames(idleAnim);
                    if (kfs != null && kfs.Count > 0)
                        animData.RefFrameHeight = kfs[0].Frame.Rect.Height;
                }
                _unitAnims[uid] = animData;
            }

            // --- Jump state machine (voluntary jumps: necromancer attack, wolf pounce) ---
            if (_sim.Units[i].JumpPhase != 0)
            {
                if (JumpSystem.TickUnit(dt, _sim.UnitsMut, i, animData.Ctrl, _sim))
                {
                    _unitAnims[uid] = animData;
                    continue;
                }
            }

            // Force out of work anims if interaction was cancelled (WASD override).
            // Exception: the reanimate_corpse spell channels through the ImbueTable
            // states via _pendingCastAnim (no CorpseInteractPhase), so don't yank the
            // necromancer back to Idle mid-channel — that would kill the channel before
            // its summon effect fires at the end of the loop.
            bool channelingImbueTable = _pendingCastAnim.HasValue
                && _pendingCastAnim.Value.CastAnim == "ImbueTable"
                && i == _sim.NecromancerIndex;
            if (_sim.Units[i].CorpseInteractPhase == 0 && !channelingImbueTable)
            {
                var cur = animData.Ctrl.CurrentState;
                if (cur == AnimState.WorkStart || cur == AnimState.WorkLoop || cur == AnimState.WorkEnd
                    || cur == AnimState.Pickup || cur == AnimState.PutDown
                    || cur == AnimState.ImbueTableStart || cur == AnimState.ImbueTableLoop || cur == AnimState.ImbueTableFinish)
                {
                    animData.Ctrl.ForceState(AnimState.Idle);
                    animData.Ctrl.PlaybackSpeed = 1f; // clear any channel time-stretch
                }
            }

            // --- Corpse interaction state machine ---
            // PlayOnceHold states: ForceState on entry, IsAnimFinished for completion
            if (_sim.Units[i].CorpseInteractPhase != 0)
            {
                byte phase = _sim.Units[i].CorpseInteractPhase;
                const float BaggingDuration = 2.0f;

                // Reanimating a corpse on a craft table uses the ImbueTable
                // animation set instead of the generic Work set. Keyed off the
                // unit's active craft-table index so only table channeling swaps.
                bool imbueTable = _sim.Units[i].CraftTableIdx >= 0;
                AnimState wStart = imbueTable ? AnimState.ImbueTableStart : AnimState.WorkStart;
                AnimState wLoop  = imbueTable ? AnimState.ImbueTableLoop  : AnimState.WorkLoop;
                AnimState wEnd   = imbueTable ? AnimState.ImbueTableFinish : AnimState.WorkEnd;

                // Fit the whole Start+Loop+Finish into the table's ProcessTime: the
                // loop is the flexible middle, and if the natural total exceeds
                // ProcessTime the playback is time-stretched (frame-rate accelerated)
                // to fit, keeping at least one full loop cycle. Computed each frame
                // (cheap) so it tracks ProcessTime edits live.
                if (imbueTable && _envSystem != null
                    && _sim.Units[i].CraftTableIdx < _envSystem.ObjectCount)
                {
                    int tIdx = _sim.Units[i].CraftTableIdx;
                    var tdef = _envSystem.Defs[_envSystem.GetObject(tIdx).DefIndex];
                    float pt = tdef.ProcessTime;
                    float sD = animData.Ctrl.AnimDurationMsFor(AnimState.ImbueTableStart) / 1000f;
                    float lC = animData.Ctrl.AnimDurationMsFor(AnimState.ImbueTableLoop) / 1000f;
                    float fD = animData.Ctrl.AnimDurationMsFor(AnimState.ImbueTableFinish) / 1000f;
                    float baseTotal = sD + lC + fD;
                    float spd = (pt > 0.01f && baseTotal > pt) ? baseTotal / pt : 1f;
                    float budget = baseTotal > pt ? lC / spd : MathF.Max(lC, pt - sD - fD);
                    animData.Ctrl.PlaybackSpeed = spd;
                    _envSystem.GetTableState(tIdx).LoopBudget = budget;
                }

                switch (phase)
                {
                    case 1: // Start (PlayOnceHold)
                        if (animData.Ctrl.CurrentState != wStart)
                            animData.Ctrl.ForceState(wStart);
                        if (animData.Ctrl.IsAnimFinished)
                        {
                            _sim.UnitsMut[i].CorpseInteractPhase = 2;
                            _sim.UnitsMut[i].BaggingTimer = 0f;
                            animData.Ctrl.ForceState(wLoop);
                        }
                        break;

                    case 2: // Loop (Loop — timer driven)
                        if (animData.Ctrl.CurrentState != wLoop)
                            animData.Ctrl.ForceState(wLoop);
                        // Corpse bagging drives timer here; trap building is driven by handler
                        if (_sim.Units[i].Routine == 0) // not in a handler routine
                        {
                            _sim.UnitsMut[i].BaggingTimer += dt;
                            {
                                var bc = _sim.FindCorpseByID(_sim.Units[i].BaggingCorpseID);
                                if (bc != null)
                                    bc.BaggingProgress = Math.Min(1f, _sim.Units[i].BaggingTimer / BaggingDuration);
                            }
                            if (_sim.Units[i].BaggingTimer >= BaggingDuration)
                            {
                                _sim.UnitsMut[i].CorpseInteractPhase = 3;
                                animData.Ctrl.ForceState(wEnd);
                            }
                        }
                        // else: handler controls timer and transitions CorpseInteractPhase
                        break;

                    case 3: // End/Finish (PlayOnceHold)
                        if (animData.Ctrl.CurrentState != wEnd)
                            animData.Ctrl.ForceState(wEnd);
                        if (animData.Ctrl.IsAnimFinished)
                        {
                            if (_sim.Units[i].Routine == 0) // corpse bagging
                            {
                                var bc = _sim.FindCorpseByID(_sim.Units[i].BaggingCorpseID);
                                if (bc != null)
                                {
                                    bc.Bagged = true;
                                    bc.BaggingProgress = 0f;
                                    bc.BaggedByUnitID = GameConstants.InvalidUnit;
                                }
                                _sim.UnitsMut[i].BaggingCorpseID = -1;
                            }
                            _sim.UnitsMut[i].CorpseInteractPhase = 0;
                            animData.Ctrl.ForceState(AnimState.Idle);
                            animData.Ctrl.PlaybackSpeed = 1f; // clear any channel time-stretch
                        }
                        break;

                    case 4: // Pickup — body bag tracks hilt visually via DrawCarriedBodyBag
                        if (animData.Ctrl.CurrentState != AnimState.Pickup)
                            animData.Ctrl.ForceState(AnimState.Pickup);
                        {
                            var cc = _sim.FindCorpseByID(_sim.Units[i].CarryingCorpseID);
                            if (cc != null)
                            {
                                cc.Position = _sim.Units[i].Position; // keep world pos synced for logic
                                cc.FacingAngle = _sim.Units[i].FacingAngle;
                            }
                        }
                        if (animData.Ctrl.IsAnimFinished)
                        {
                            _sim.UnitsMut[i].CorpseInteractPhase = 0;
                            animData.Ctrl.ForceState(AnimState.Carry);
                            animData.Ctrl.PlaybackSpeed = 0f; // freeze until unit moves
                        }
                        break;

                    case 5: // PutDown — body bag tracks hilt visually via DrawCarriedBodyBag
                        if (animData.Ctrl.CurrentState != AnimState.PutDown)
                            animData.Ctrl.ForceState(AnimState.PutDown);
                        {
                            var cc = _sim.FindCorpseByID(_sim.Units[i].CarryingCorpseID);
                            if (cc != null)
                                cc.FacingAngle = _sim.Units[i].FacingAngle;
                        }
                        if (animData.Ctrl.IsAnimFinished)
                        {
                            // Dispatch on PutDownTableIdx: if a table was targeted at F-press
                            // time, load the corpse into its slot and remove the corpse from
                            // the sim. Otherwise, place on ground at LerpStartPos as before.
                            int tableIdx = _sim.Units[i].PutDownTableIdx;
                            int corpseId = _sim.Units[i].CarryingCorpseID;
                            var cc = _sim.FindCorpseByID(corpseId);

                            if (tableIdx >= 0 && _envSystem != null && cc != null
                                && Game.TableSystem.LoadCorpseIntoTable(_envSystem, tableIdx, cc) >= 0)
                            {
                                int ci = _sim.FindCorpseIndexByID(corpseId);
                                if (ci >= 0) _sim.CorpsesMut.RemoveAt(ci);
                                // Auto-open the table menu so the player can pick items
                                // and start crafting without an extra click.
                                int sw = _graphics.PreferredBackBufferWidth;
                                int sh = _graphics.PreferredBackBufferHeight;
                                EnsureInventoryUIsInitialized();
                                _tableMenuUI.OpenForTable(tableIdx, sw, sh, _camera, _renderer);
                            }
                            else if (cc != null)
                            {
                                // Ground drop (or table-load fell through e.g. slot taken).
                                // Land flat at the drop point — zero Z/physics so the
                                // settled draw lands exactly where the put-down draw was.
                                cc.Position = cc.LerpStartPos;
                                cc.Z = 0f;
                                cc.InPhysics = false;
                                cc.DraggedByUnitID = GameConstants.InvalidUnit;
                            }

                            _sim.UnitsMut[i].CarryingCorpseID = -1;
                            _sim.UnitsMut[i].CorpseInteractPhase = 0;
                            _sim.UnitsMut[i].PutDownTableIdx = -1;
                            animData.Ctrl.ForceState(AnimState.Idle);
                        }
                        break;

                    default:
                        _sim.UnitsMut[i].CorpseInteractPhase = 0;
                        break;
                }

                animData.Ctrl.Update(dt);
                _unitAnims[uid] = animData;
                continue;
            }

            // --- Two-channel animation for archetype units ---
            if (_sim.Units[i].Archetype > 0)
            {
                // Archetype units use the RoutineAnim/OverrideAnim two-channel system.
                // AI handlers set RoutineAnim, combat/damage sets OverrideAnim.
                // AnimResolver picks the winner based on priority.

                // Combat engine overrides: pending attacks get priority 2 override.
                // Attack anim plays at its natural ms timing unless it won't fit in
                // the weapon's cycle (CooldownRounds × RoundDuration), in which case
                // it's compressed to fit.
                if (!_sim.Units[i].PendingAttack.IsNone)
                {
                    var atkState = ResolvePendingAttackAnim(_sim.Units[i].Stats,
                        _sim.Units[i].PendingWeaponIdx, _sim.Units[i].PendingWeaponIsRanged,
                        _sim.Units[i].Archetype);
                    float animDur = animData.Ctrl.GetTotalDurationSeconds(atkState);
                    float cycle = ComputeWeaponCycleSeconds(i, _sim.Units[i].PendingWeaponIdx);
                    float spd = (animDur > cycle && cycle > 0f) ? animDur / cycle : 1f;
                    AnimResolver.SetOverride(_sim.UnitsMut[i], AnimRequest.Combat(atkState, spd));
                }
                else if (_sim.Units[i].InCombat && _sim.Units[i].AttackCooldown > 0f)
                {
                    // Pre-roll: start attack animation early so its effect_time lines up
                    // with the end of the cooldown. Use the FIRST non-pounce weapon's
                    // anim (most units have one melee weapon; wolves have Bite then Pounce
                    // and Bite is the in-melee attack).
                    int preRollWeaponIdx = 0;
                    for (int w = 0; w < _sim.Units[i].Stats.MeleeWeapons.Count; w++)
                    {
                        if (_sim.Units[i].Stats.MeleeWeapons[w].Archetype != Data.WeaponArchetype.Pounce)
                        { preRollWeaponIdx = w; break; }
                    }
                    var preRollState = ResolvePendingAttackAnim(_sim.Units[i].Stats,
                        preRollWeaponIdx, false, _sim.Units[i].Archetype);
                    float cooldownRemaining = _sim.Units[i].AttackCooldown;
                    float effectTime = animData.Ctrl.GetEffectTimeSeconds(preRollState);
                    float animDur = animData.Ctrl.GetTotalDurationSeconds(preRollState);
                    float cycle = ComputeWeaponCycleSeconds(i, preRollWeaponIdx);
                    float spd = (animDur > cycle && cycle > 0f) ? animDur / cycle : 1f;
                    float preRollTime = effectTime > 0f ? effectTime / spd : 0f;
                    if (preRollTime > 0f && cooldownRemaining <= preRollTime)
                        AnimResolver.SetOverride(_sim.UnitsMut[i], AnimRequest.Combat(preRollState, spd));
                }

                // Cancel a stale attack swing that would otherwise "bleed" into a chase:
                // once the unit is actually moving and no longer attacking (no pending
                // swing, post-attack lockout elapsed, not in melee), drop the one-shot
                // attack override so locomotion shows instead of the swing sliding along.
                // The swing still plays fully while the unit is planted (PostAttackTimer
                // / InCombat keep Velocity at 0); this only fires once it starts moving.
                {
                    var ovNow = _sim.Units[i].OverrideAnim;
                    bool ovIsAttack = ovNow.IsActive &&
                        (ovNow.State == AnimState.Attack1 || ovNow.State == AnimState.Attack2 || ovNow.State == AnimState.Attack3);
                    bool notAttacking = _sim.Units[i].PendingAttack.IsNone
                        && _sim.Units[i].PostAttackTimer <= 0f
                        && !_sim.Units[i].InCombat;
                    if (ovIsAttack && notAttacking && _sim.Units[i].Velocity.LengthSq() > 1.0f)
                        AnimResolver.ClearOverride(_sim.UnitsMut[i]);
                }

                // Reverse walk playback
                var facingDir2 = Movement.FacingUtil.ForwardDir(_sim.Units[i]);
                var vel2 = _sim.Units[i].Velocity;
                bool backward2 = vel2.LengthSq() > 0.1f && vel2.Normalized().Dot(facingDir2) < -0.3f;
                animData.Ctrl.SetReversePlayback(backward2);

                AnimResolver.Resolve(_sim.UnitsMut[i], animData.Ctrl, dt);

                // Locomotion playback scaling — applied after Resolve so we know the
                // final state the controller landed on. Re-applied every frame because
                // AnimController.SwitchState resets _playbackSpeed to 1.0 on transitions.
                // Only overwrite PlaybackSpeed for actual locomotion states; for attack /
                // spell / jump states, AnimResolver's compression-speed from the winning
                // override must stick through ctrl.Update.
                var curState = animData.Ctrl.CurrentState;
                bool isLocoState = curState == AnimState.Walk || curState == AnimState.Jog
                    || curState == AnimState.Run || curState == AnimState.Carry;
                if (isLocoState)
                {
                    float locoSpeed = _sim.Units[i].Velocity.Length();
                    var locoDef = _gameData.Units.Get(_sim.Units[i].UnitDefID);
                    var locoProfile = locoDef != null
                        ? LocomotionProfile.FromUnit(locoDef)
                        : LocomotionProfile.FromBaseSpeed(_sim.Units[i].Stats.CombatSpeed);
                    animData.Ctrl.PlaybackSpeed = LocomotionScaling.ComputeLocomotionPlayback(
                        animData.Ctrl, locoProfile, curState, locoSpeed);
                }
                animData.Ctrl.Update(dt);

                // Cosmetic attack lunge — writes Unit.RenderOffset based on attack anim
                // progress. All draw sites read Position + RenderOffset via unit.RenderPos.
                LungeSystem.Update(_sim.UnitsMut[i], animData.Ctrl);

                // DEBUG-only invariant checks. No-op in production; fires at the exact
                // frame a rule is violated (easier to diagnose than "the anim got weird
                // 10 frames ago").
                AnimInvariants.Check(_sim.Units[i], animData.Ctrl);
            }
            else
            {
            // --- Legacy animation selection for non-archetype units ---
            AnimState targetState;
            if (_sim.Units[i].InPhysics)
                targetState = AnimState.Fall;
            else if (_sim.Units[i].Incap.Active && !_sim.Units[i].Incap.Recovering)
                targetState = _sim.Units[i].Incap.HoldAnim;
            else if (_sim.Units[i].Incap.Recovering)
            {
                targetState = _sim.Units[i].Incap.RecoverAnim;
                // Set real recovery timer from actual animation duration (first frame only)
                if (_sim.Units[i].Incap.RecoverTimer < 0f)
                {
                    float realDuration = animData.Ctrl.GetTotalDurationSeconds(targetState);
                    if (realDuration <= 0f) realDuration = _sim.Units[i].Incap.RecoverTime; // fallback
                    // Slow rises (reanimation's 0.5x standup) play longer than the 1x clip;
                    // stretch the lock to match (mirrors AnimResolver for archetype units).
                    float rspd = _sim.Units[i].Incap.RecoverPlaybackSpeed > 0f ? _sim.Units[i].Incap.RecoverPlaybackSpeed : 1f;
                    var incap = _sim.Units[i].Incap;
                    incap.RecoverTimer = realDuration / rspd;
                    _sim.UnitsMut[i].Incap = incap;
                }
            }
            else if (_sim.Units[i].Dodging)
                targetState = AnimState.Dodge;
            else if (!_sim.Units[i].PendingAttack.IsNone)
                targetState = ResolvePendingAttackAnim(_sim.Units[i].Stats,
                    _sim.Units[i].PendingWeaponIdx, _sim.Units[i].PendingWeaponIsRanged,
                    _sim.Units[i].Archetype);
            // Flinch driven by HitReactTimer (set by DamageSystem.ApplyHitReactAnim,
            // which already skipped fleeing / prone / refractory units, and is never
            // set for poison) — not the raw HitReacting/BlockReacting flags. Keeps the
            // legacy render in lockstep with the archetype OverrideAnim path.
            else if (_sim.Units[i].HitReactTimer > 0f)
                targetState = AnimState.BlockReact;
            else if (_sim.Units[i].PostAttackTimer > 0f)
                targetState = AnimState.Block;
            else if (_sim.Units[i].InCombat && _sim.Units[i].AttackCooldown > 0f)
            {
                float cooldownRemaining = _sim.Units[i].AttackCooldown;
                float effectTime = animData.Ctrl.GetEffectTimeSeconds(AnimState.Attack1);
                float animDur = animData.Ctrl.GetTotalDurationSeconds(AnimState.Attack1);
                float cycle = ComputeWeaponCycleSeconds(i, 0);
                float speed = (animDur > cycle && cycle > 0f) ? animDur / cycle : 1f;
                float preRollTime = effectTime > 0f ? effectTime / speed : 0f;

                if (preRollTime > 0f && cooldownRemaining <= preRollTime)
                    targetState = AnimState.Attack1;
                else
                    targetState = AnimState.Block;
            }
            else if (_sim.Units[i].GhostMode)
                targetState = AnimState.Hover;
            else
            {
                // Gait selection uses the MAX of actual and intended (PreferredVel)
                // speed so a unit accelerating from a standstill — e.g. a wolf bolting
                // out of an attack into its retreat — shows Walk immediately instead of
                // sliding in Idle while real velocity ramps up. Playback scaling below
                // still uses actual Velocity, so feet stay locked to ground motion.
                float speed = MathF.Max(_sim.Units[i].Velocity.Length(), _sim.Units[i].PreferredVel.Length());
                float baseSpeed = _sim.Units[i].Stats.CombatSpeed;
                float jogThreshold = 4f + baseSpeed / 3f;
                float runThreshold = 6f + 2f * baseSpeed / 3f;

                bool carrying = _sim.Units[i].CarryingCorpseID >= 0;
                if (carrying)
                    targetState = AnimState.Carry;
                else if (speed <= 0.25f)
                    targetState = AnimState.Idle;
                else if (speed < jogThreshold)
                    targetState = AnimState.Walk;
                else if (speed < runThreshold)
                    targetState = AnimState.Jog;
                else
                    targetState = AnimState.Run;
            }

            // Reverse walk playback
            var facingDir = Movement.FacingUtil.ForwardDir(_sim.Units[i]);
            var vel = _sim.Units[i].Velocity;
            bool movingBackward = vel.LengthSq() > 0.1f && vel.Normalized().Dot(facingDir) < -0.3f;
            animData.Ctrl.SetReversePlayback(movingBackward);

            // Locomotion playback scaling (Walk/Jog/Run/Carry) — keeps foot-cycle
            // frequency matched to actual velocity so anims don't skate.
            {
                float locoSpeed = _sim.Units[i].Velocity.Length();
                var locoDef = _gameData.Units.Get(_sim.Units[i].UnitDefID);
                var locoProfile = locoDef != null
                    ? LocomotionProfile.FromUnit(locoDef)
                    : LocomotionProfile.FromBaseSpeed(_sim.Units[i].Stats.CombatSpeed);
                animData.Ctrl.PlaybackSpeed = LocomotionScaling.ComputeLocomotionPlayback(
                    animData.Ctrl, locoProfile, targetState, locoSpeed);
            }
            if (targetState == AnimState.Attack1 && animData.Ctrl.CurrentState != AnimState.Attack1)
            {
                float lockout = _gameData.Settings.Combat.PostAttackLockout;
                float animDur = animData.Ctrl.GetTotalDurationSeconds(AnimState.Attack1);
                if (animDur > 0f && lockout > 0f)
                    animData.Ctrl.PlaybackSpeed = MathF.Max(1f, animDur / lockout);
            }

            var currentAnim = animData.Ctrl.CurrentState;
            // ForceState needed to break out of PlayOnceHold animations
            bool needsForce = (currentAnim == AnimState.Sit || currentAnim == AnimState.Sleep
                || currentAnim == AnimState.Fall || currentAnim == AnimState.Knockdown)
                && currentAnim != targetState;
            // Also force INTO Fall/Knockdown from any state
            needsForce |= (targetState == AnimState.Fall || targetState == AnimState.Knockdown)
                && currentAnim != targetState;
            if (_sim.Units[i].Incap.HoldAtEnd && _sim.Units[i].Incap.Active
                && !_sim.Units[i].Incap.Recovering && currentAnim != targetState)
            {
                animData.Ctrl.ForceStateAtEnd(targetState);
                var incap = _sim.Units[i].Incap;
                incap.HoldAtEnd = false; // only snap once
                _sim.UnitsMut[i].Incap = incap;
            }
            else if (needsForce)
                animData.Ctrl.ForceState(targetState);
            else
                animData.Ctrl.RequestState(targetState);
            // Recovery (incap / reanimation rise) plays at its own rate. The loco block
            // above only scales Walk/Jog/Run, so set the slow-standup speed explicitly.
            if (_sim.Units[i].Incap.Recovering && _sim.Units[i].Incap.RecoverPlaybackSpeed > 0f)
                animData.Ctrl.PlaybackSpeed = _sim.Units[i].Incap.RecoverPlaybackSpeed;
            animData.Ctrl.Update(dt);
            } // end legacy path

            // Action-moment handling via edge flags.
            //
            // JustHitEffectFrame fires on the single tick where _animTime crosses the
            // current state's effect_time_ms (or 50% in tick-based fallback). Unlike
            // the old ConsumeActionMoment model, reading this flag is non-destructive:
            // every interested system inspects the same flag and decides whether it's
            // the intended consumer. Pre-roll can't "steal" an action moment from the
            // real attack anymore — the pre-roll simply has no queued consumer when
            // the flag fires.
            if (animData.Ctrl.JustHitEffectFrame)
            {
                bool hasPendingCast = _pendingCastAnim != null && i == FindNecromancer()
                    && animData.Ctrl.CurrentState == AnimState.Spell1;
                bool hasPendingAttack = !_sim.Units[i].PendingAttack.IsNone;

                if (hasPendingCast)
                {
                    var pca = _pendingCastAnim.Value;
                    var spell = _gameData.Spells.Get(pca.SpellID);
                    if (spell != null)
                        ExecuteSpellEffect(spell, i, pca.Target, pca.Slot, pca.IsSecondary);
                    _pendingCastAnim = null;
                }
                else if (hasPendingAttack)
                {
                    _sim.ResolvePendingAttack(i);
                }
            }

            _unitAnims[uid] = animData;
        }

        // Channeled reanimation casts run their own Start→Loop→Finish machine.
        if (_pendingCastAnim != null && IsChanneledCast(_pendingCastAnim.Value.CastAnim))
        {
            UpdateChanneledCast(dt);
        }
        // Spell1 is PlayOnceTransition — it switches to Idle when done, so we can't check
        // CurrentState == Spell1 after the fact. Instead, detect that the necromancer left
        // Spell1 (no longer in Spell1) while _pendingCastAnim is still set.
        else if (_pendingCastAnim != null)
        {
            int necroIdx = FindNecromancer();
            if (necroIdx >= 0)
            {
                uint nUid = _sim.Units[necroIdx].Id;
                bool stillCasting = _unitAnims.TryGetValue(nUid, out var nAnim)
                    && nAnim.Ctrl.CurrentState == AnimState.Spell1;
                if (!stillCasting)
                {
                    // Spell1 ended (transitioned away) — execute spell if action moment didn't fire
                    var pca = _pendingCastAnim.Value;
                    var spell = _gameData.Spells.Get(pca.SpellID);
                    if (spell != null)
                        ExecuteSpellEffect(spell, necroIdx, pca.Target, pca.Slot, pca.IsSecondary);
                    _pendingCastAnim = null;
                    RemoveCastingBuffAll(necroIdx);
                }
            }
            else
            {
                // Necromancer gone — clear pending cast
                _pendingCastAnim = null;
            }
        }

        // Casting glow while channeling a reanimation at the necro table.
        UpdateTableChannelBuff();

        // --autostart headless diagnostic: world is loaded, exit.
        if (_autostartExitPending) Exit();

        // Update EffectSpawnPos2D / EffectSpawnHeight from weapon tip data
        UpdateEffectSpawnPositions();

        _effectManager.Update(dt);
        _reanimFx.Update(dt);
        TickPendingReanimRises(dt);   // spawn deferred rises in lockstep with their effect clock
        _buffVisuals.Update(dt, _sim.Units, _gameData.Buffs, _gameTime);
        _foragables.Update(dt);
        _gameRenderer.UpdateSkillLearnToasts(dt);
        SyncCorruptionSettings();

        // Death Fog Consumption passive: while the necromancer stands in any
        // non-zero death-fog density, add +2 to their mana regen this tick.
        // BonusManaRegen is consumed by the next Simulation.Update.
        var necroState = _sim.NecroState;
        necroState.BonusManaRegen = 0f;
        if (_skillBookState.HasPassive("death_fog_consumption") && _sim.NecromancerIndex >= 0)
        {
            var necroPos = _sim.Units[_sim.NecromancerIndex].Position;
            if (_deathFog.Sample(necroPos.X, necroPos.Y) > 0.01f)
                necroState.BonusManaRegen = 2f;
        }

        _deathFog.Update(_envSystem, dt, _groundSystem);

        // Advance per-vertex visual fades for newly corrupted grass vertices.
        // Internally rate-limits texture re-uploads so we don't push pixels
        // every frame just to bump fade values by ~1/60.
        _groundSystem.AdvanceCorruptionFades(dt);

        // Advance per-cell grass-tuft tint fades (10s lerp from default to
        // corrupted tint, started when a ground vertex under the cell flips).
        _grassRenderer.AdvanceFades(dt);

        // Ground corruption rolls inside DeathFogSystem may have flipped vertices —
        // push the dirty rect into the existing vertex map texture (partial
        // SetData) instead of disposing and re-allocating a 67 MB texture.
        if (_groundSystem.CorruptionDirty)
        {
            bool partialOk = _groundVertexMapTex != null
                && _groundSystem.UploadDirtyRect(_groundVertexMapTex);
            if (!partialOk)
            {
                _groundVertexMapTex?.Dispose();
                _groundVertexMapTex = _groundSystem.CreateVertexMapTexture(GraphicsDevice);
            }
        }
    }

    /// <summary>
    /// Compute each unit's weapon-tip world position for use as spell/effect origin.
    /// Priority: 1) weapon point tip from UnitDef  2) facing-based fallback
    /// </summary>
    private void UpdateEffectSpawnPositions()
    {
        var mu = _sim.UnitsMut;
        for (int i = 0; i < _sim.Units.Count; i++)
        {
            if (!_sim.Units[i].Alive) continue;

            uint uid = _sim.Units[i].Id;
            if (!_unitAnims.TryGetValue(uid, out var animData)) continue;

            string defID = _sim.Units[i].UnitDefID;
            var unitDef = _gameData.Units.Get(defID);
            bool foundWeaponTip = false;

            if (unitDef != null && unitDef.Sprite != null && animData.RefFrameHeight > 0f)
            {
                string animName = AnimController.StateToAnimName(animData.Ctrl.CurrentState);
                int spriteAngle = animData.Ctrl.ResolveAngle(_sim.Units[i].FacingAngle, out bool flipX);
                int frameIdx = animData.Ctrl.GetCurrentFrameIndex(_sim.Units[i].FacingAngle);

                AnimationMeta? meta = null;
                _animMeta.TryGetValue(AnimMetaLoader.MetaKey(unitDef.Sprite.SpriteName, animName), out meta);

                if (WeaponPointResolver.TryResolve(unitDef, meta, animName, spriteAngle, frameIdx,
                        animData.RefFrameHeight, out var wpf, out _))
                {
                    bool tipSet = wpf.Tip.X != 0f || wpf.Tip.Y != 0f;
                    if (tipSet)
                    {
                        float flipMul = flipX ? -1f : 1f;
                        float worldH = (unitDef.SpriteWorldHeight > 0 ? unitDef.SpriteWorldHeight : 1.8f)
                                       * _sim.Units[i].SpriteScale;
                        float worldScale = worldH / animData.RefFrameHeight;

                        // Spawn position follows the visible weapon tip — if the
                        // unit is lunged, the projectile spawns from where the
                        // weapon visually is.
                        var spawnBase = _sim.Units[i].RenderPos;
                        float tipDx = wpf.Tip.X * worldScale * flipMul;
                        mu[i].EffectSpawnPos2D = new Vec2(spawnBase.X + tipDx, spawnBase.Y);

                        float unitHeight = _sim.Units[i].Z;
                        mu[i].EffectSpawnHeight = unitHeight - wpf.Tip.Y * worldScale;

                        foundWeaponTip = true;
                    }
                }
            }

            if (!foundWeaponTip)
            {
                // Fallback: offset in facing direction. Use RenderPos so spawn follows lunge.
                float radius = _sim.Units[i].Radius;
                mu[i].EffectSpawnPos2D = _sim.Units[i].RenderPos
                    + Movement.FacingUtil.ForwardDir(_sim.Units[i]) * radius * 1.5f;
                mu[i].EffectSpawnHeight = 0.6f;
            }
        }
    }

    /// <summary>Pre-draw pass: write Unit.WadingSinkOffsetY for every
    /// alive unit based on its current waterness and per-unit (or default)
    /// sink magnitude. Runs before shadow / sprite / buff passes so they
    /// all see the consistent sunken RenderPos. Cheap: per-unit it's a
    /// single waterness sample + scale.</summary>
    internal void UpdateWadingSinkOffsets()
    {
        if (_groundSystem == null) return;
        for (int i = 0; i < _sim.Units.Count; i++)
        {
            if (!_sim.Units[i].Alive)
            {
                _sim.UnitsMut[i].WadingSinkOffsetY = 0f;
                continue;
            }
            var unitDef = _gameData.Units.Get(_sim.Units[i].UnitDefID);
            // Negative WadingSinkWorld = explicit "no sink" opt-out.
            float maxSink = unitDef != null && unitDef.WadingSinkWorld != 0f
                ? unitDef.WadingSinkWorld
                : Render.WadingWakeSystem.DefaultMaxSinkWorld;
            if (maxSink <= 0f)
            {
                _sim.UnitsMut[i].WadingSinkOffsetY = 0f;
                continue;
            }
            float waternessRaw = _groundSystem.SampleWaternessSmoothed(
                _sim.Units[i].Position, Render.WadingConfig.KernelRadius);
            float waterness = MathHelper.Clamp(
                (waternessRaw - Render.WadingConfig.ShorelineMidpoint) * 2f, 0f, 1f);
            _sim.UnitsMut[i].WadingSinkOffsetY =
                Render.WadingWakeSystem.ComputeSinkOffset(waterness, maxSink);
        }
    }
}
