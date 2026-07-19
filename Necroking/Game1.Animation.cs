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

// Game1 partial: per-frame animation, cast-phase and attack-anim updates.
public partial class Game1
{
    /// <summary>
    /// Single canonical "build UnitAnimData from a unit def" factory: controller
    /// init, anim metadata, per-def attack-anim override, per-unit AnimTimings
    /// overrides from the unit editor (incl. EffectTimeMs, which times when attack
    /// damage lands), and the Idle reference frame height. Used by SpawnUnit,
    /// RebuildUnitAnim and the lazy per-frame init in UpdateAnimations. Returns
    /// null when the def is missing, has no sprite, or the sprite doesn't resolve
    /// in its atlas.
    /// </summary>
    internal UnitAnimData? BuildUnitAnimData(string unitDefID)
    {
        var unitDef = _gameData.Units.Get(unitDefID);
        if (unitDef?.Sprite == null) return null;

        var atlasId = AtlasDefs.ResolveAtlasName(unitDef.Sprite.AtlasName);
        var spriteData = _atlases[atlasId].GetUnit(unitDef.Sprite.SpriteName);
        if (spriteData == null) return null;

        var ctrl = new AnimController();
        ctrl.Init(spriteData);
        ctrl.ForceState(AnimState.Idle);

        if (_animMeta.Count > 0)
            ctrl.SetAnimMeta(_animMeta, unitDef.Sprite.SpriteName);

        if (unitDef.AttackAnim != null)
            ctrl.SetAttackAnimOverride(unitDef.AttackAnim);

        // Wire per-unit animation timing overrides (from unit editor)
        if (unitDef.AnimTimings.Count > 0)
        {
            var overrides = new Dictionary<string, AnimTimingOverride>();
            foreach (var (anim, ov) in unitDef.AnimTimings)
                overrides[anim] = new AnimTimingOverride
                {
                    FrameDurationsMs = new List<int>(ov.FrameDurationsMs),
                    EffectTimeMs = ov.EffectTimeMs
                };
            ctrl.SetAnimTimings(overrides);
        }

        float refH = 128f;
        var idleAnim = spriteData.GetAnim("Idle");
        if (idleAnim != null)
        {
            var kfs = GameRenderer.PickIdleFrames(idleAnim);
            if (kfs != null && kfs.Count > 0)
                refH = kfs[0].Frame.Rect.Height;
        }

        return new UnitAnimData
        {
            Ctrl = ctrl,
            AtlasID = atlasId,
            RefFrameHeight = refH,
            CachedDefID = unitDefID
        };
    }

    /// <summary>
    /// Rebuild animation data for a unit (e.g. after transform).
    /// </summary>
    internal void RebuildUnitAnim(int unitIdx, string unitDefID)
    {
        var anim = BuildUnitAnimData(unitDefID);
        if (anim == null) return;
        _unitAnims[_sim.Units[unitIdx].Id] = anim.Value;
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

    /// <summary>Playback-speed multiplier (and Loop-phase wall-clock budget, via out) that
    /// fits a channeled cast's Start+Loop+Finish into its CastTime:
    ///  - CastTime ≥ one natural cycle → spd 1, loopBudget &gt; one cycle (the loop repeats
    ///    to fill: a longer cast plays MORE loops, not a slowed animation).
    ///  - CastTime too short for one cycle → spd &gt; 1 (frame-rate accelerated), loopBudget =
    ///    one scaled cycle (lC/spd), so a short CastTime genuinely shortens the cast.
    /// Apply the returned speed to the controller right before the per-frame ctrl.Update
    /// that advances the anim — the legacy locomotion block resets PlaybackSpeed every
    /// frame and UpdateChanneledCast runs after that Update, so anywhere else is too late.</summary>
    private static float ChannelPlaybackSpeed(AnimController ctrl, in PendingCastAnim pca, out float loopBudget)
    {
        GetChannelStates(pca.CastAnim, out var startS, out var loopS, out var finishS);
        float sD = ctrl.AnimDurationMsFor(startS) / 1000f;
        float lC = MathF.Max(0.05f, ctrl.AnimDurationMsFor(loopS) / 1000f);
        float fD = finishS.HasValue ? ctrl.AnimDurationMsFor(finishS.Value) / 1000f : 0f;
        float baseTotal = sD + lC + fD;
        bool tooShort = pca.CastTime > 0.01f && baseTotal > pca.CastTime;
        float spd = tooShort ? baseTotal / pca.CastTime : 1f;
        loopBudget = tooShort ? lC / spd : MathF.Max(lC, pca.CastTime - sD - fD);
        return spd;
    }

    /// <summary>Drive a channeled reanimation cast: Start → Loop → (Finish). The
    /// spell effect fires at the END of the loop; the necromancer faces the target
    /// throughout. Raise has no Finish → straight to Idle.
    ///
    /// Fitting Start+Loop+Finish to CastTime mirrors the table-reanimation path
    /// (<see cref="UpdateAnimations"/> ImbueTable block):
    ///  - CastTime LONGER than one natural cycle: play at natural speed (spd = 1) and
    ///    let the LOOP repeat to fill the extra time — a longer cast plays *more loops*,
    ///    not a slowed-down animation. That's the point of a channel.
    ///  - CastTime TOO SHORT for even one Start+cycle+Finish: time-stretch the whole
    ///    channel (PlaybackSpeed > 1, frame-rate accelerated) so it still fits, keeping
    ///    exactly one (scaled) loop cycle. The min-one-cycle cull uses the SCALED cycle
    ///    (lC / spd), so a short CastTime genuinely shortens the cast.</summary>
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

        // Facing toward the target for the whole channel is owned by
        // Locomotion.UpdateFacing's cast-plant branch (fast TurnToward, no raw snap) —
        // TickCastPlant feeds it the frozen aim angle every frame.

        // PlaybackSpeed (the Start/Loop/Finish time-stretch) is applied in the per-unit
        // anim loop right before ctrl.Update — see the necromancer channel override there.
        // Setting it here is too late: UpdateChanneledCast runs AFTER that Update each
        // frame, so the locomotion block would already have advanced the anim at speed 1.
        // Here we only need the Loop-phase wall-clock budget.
        ChannelPlaybackSpeed(ctrl, pca, out float loopBudget);

        pca.ChannelElapsed += dt;

        switch (pca.ChannelPhase)
        {
            case 0: // Start (play once, hold at end — finishes in sD/spd wall-clock)
                if (ctrl.CurrentState != startS) ctrl.ForceState(startS);
                if (ctrl.IsAnimFinished)
                {
                    pca.ChannelPhase = 1;
                    pca.LoopElapsed = 0f;
                    ctrl.ForceState(loopS);
                }
                break;

            case 1: // Loop — fire the effect once the loop budget is spent
                if (ctrl.CurrentState != loopS) ctrl.ForceState(loopS);
                pca.LoopElapsed += dt;
                if (pca.LoopElapsed >= loopBudget)
                {
                    var spell = _gameData.Spells.Get(pca.SpellID);
                    if (spell != null) ExecuteSpellEffect(spell, necroIdx, pca.Target, pca.Slot, _pendingSpell);
                    // Tail-cancel (Q3, Settings.Animation.CastTailCancel): with
                    // movement input held, skip the Finish anim — the payoff has
                    // already popped; the player is running again this frame.
                    bool tailCancel = (_gameData.Settings.Animation?.CastTailCancel ?? true)
                        && _sim.NecroMoveInputActive;
                    if (finishS.HasValue && !tailCancel)
                    {
                        pca.ChannelPhase = 2;
                        ctrl.ForceState(finishS.Value);
                    }
                    else
                    {
                        ctrl.ForceState(tailCancel ? AnimState.Walk : AnimState.Idle);
                        ctrl.PlaybackSpeed = 1f; // clear the channel time-stretch
                        RemoveCastingBuffAll(necroIdx);
                        _pendingCastAnim = null;
                        return;
                    }
                }
                break;

            case 2: // Finish (play once — finishes in fD/spd wall-clock)
                if (finishS.HasValue && ctrl.CurrentState != finishS.Value) ctrl.ForceState(finishS.Value);
                if (ctrl.IsAnimFinished)
                {
                    ctrl.ForceState(AnimState.Idle);
                    ctrl.PlaybackSpeed = 1f; // clear the channel time-stretch
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

    /// <summary>Per-frame cast-plant driver (todos/player_cast_plant.md). Runs at
    /// the top of UpdateAnimations, every frame:
    ///  - syncs the sim-side plant flag + aim angle with the pending cast
    ///    (declarative — the plant can never outlive or lag the cast, whichever
    ///    of the several end paths fires),
    ///  - cancels + REFUNDS the cast on a hard interrupt (knockdown/knockback) —
    ///    previously a knockdown mid-cast still fired the spell via the
    ///    left-Spell1 safety net,
    ///  - releases the queued cast anim once the brake passes the speed gate
    ///    (Q1: gate ≈ walking speed, so walking casts start the same frame and
    ///    only a sprint pays a ~0.1s visible skid).</summary>
    private void TickCastPlant()
    {
        if (_pendingCastAnim == null)
        {
            // No wind-up in flight — but a live Beam/Drain hold-channel keeps its
            // own plant + facing lock going (the channel starts at Spell1's effect
            // frame, exactly when _pendingCastAnim clears, so the two hand off
            // seamlessly).
            SyncChannelPlant();
            return;
        }
        int necroIdx = FindNecromancer();
        if (necroIdx < 0)
        {
            _pendingCastAnim = null;
            _sim.SetNecromancerCasting(false);
            return;
        }

        // Hard interrupt: knockdown / knockback cancels the cast and refunds
        // mana + cooldown (Q4) — an interrupted cast that stays spent feels bad.
        if (_sim.Units[necroIdx].Incap.Active || _sim.Units[necroIdx].InPhysics)
        {
            var cancelled = _pendingCastAnim.Value;
            SpellCaster.RefundSpellCast(cancelled.SpellID, _gameData.Spells, _sim.NecroState,
                _sim.Units, necroIdx, _gameData);
            RemoveCastingBuffAll(necroIdx);
            _pendingCastAnim = null;
            _sim.SetNecromancerCasting(false);
            return;
        }

        var pca = _pendingCastAnim.Value;
        var aimDir = pca.Target - _sim.Units[necroIdx].Position;
        _sim.SetNecromancerCasting(true, aimDir.LengthSq() > 0.0001f
            ? MathF.Atan2(aimDir.Y, aimDir.X) * 180f / MathF.PI : float.NaN);

        if (!pca.WaitingForPlant) return;

        // Anim-start gate: "some decel achieved" = at/below walking speed.
        float gate = _sim.Units[necroIdx].Stats.CombatSpeed
            * (_gameData.Settings.Animation?.CastPlantGateSpeedMult ?? 1.15f);
        if (_sim.Units[necroIdx].Velocity.Length() > gate) return;

        pca.WaitingForPlant = false;
        _pendingCastAnim = pca;
        uint uid = _sim.Units[necroIdx].Id;
        if (_unitAnims.TryGetValue(uid, out var anim))
        {
            if (IsChanneledCast(pca.CastAnim))
            {
                GetChannelStates(pca.CastAnim, out var startS, out _, out _);
                anim.Ctrl.ForceState(startS);
            }
            else
            {
                anim.Ctrl.RequestState(AnimState.Spell1);
            }
            _unitAnims[uid] = anim;
        }
    }

    /// <summary>Per-frame movement/facing sync for a live player Beam/Drain
    /// hold-channel (runs from TickCastPlant once the wind-up's _pendingCastAnim
    /// has handed off). While channeling:
    ///  - movement input is planted if the spell's ChannelStopsMovement is on
    ///    (default for channeled spells),
    ///  - facing locks to the live channel target — following it as it moves —
    ///    or holds the last-known cast direction when no target is resolvable,
    ///    which also means the body never follows the mouse mid-channel (the
    ///    cast-aim facing outranks the mouse override in Locomotion's ladder).</summary>
    private void SyncChannelPlant()
    {
        if (_channelingSlot < 0) { _sim.SetNecromancerCasting(false); return; }
        int necroIdx = FindNecromancer();
        if (necroIdx < 0) { _sim.SetNecromancerCasting(false); return; }

        // Follow the live channel target; a gone/corpse target keeps the last aim.
        if (TryGetChannelTargetUid(_sim.Units[necroIdx].Id, out uint targetUid)
            && targetUid != GameConstants.InvalidUnit)
        {
            int ti = _sim.ResolveUnitID(targetUid);
            if (ti >= 0 && _sim.Units[ti].Alive)
            {
                var to = _sim.Units[ti].Position - _sim.Units[necroIdx].Position;
                if (to.LengthSq() > 0.0001f)
                    _channelAimAngleDeg = MathF.Atan2(to.Y, to.X) * 180f / MathF.PI;
            }
        }

        bool stopMovement = _gameData.Spells.Get(_channelingSpellID)?.ChannelStopsMovement ?? true;
        _sim.SetNecromancerCasting(stopMovement, _channelAimAngleDeg);
    }

    private void UpdateAnimations(float dt)
    {
        TickCastPlant();

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
                // Try to init from defID via the shared factory.
                // Defensive: a reanimated unit whose sprite can't resolve would never
                // get an AnimController, so neither recovery path could ever tick its
                // recover-timer down — leaving it Incap-locked forever. Release the
                // lock here so a missing/broken sprite degrades to "no rise", not "stuck".
                var built = BuildUnitAnimData(_sim.Units[i].UnitDefID);
                if (built == null) { if (_sim.Units[i].Incap.Recovering) _sim.UnitsMut[i].Incap = default; continue; }
                animData = built.Value;
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

            // --- Nightfall rogue jump (ported partial-animation leap; NightfallPorts/RogueJump.cs) ---
            // Same ownership contract as JumpSystem above: while it owns the unit it
            // drives ctrl + Position + Z itself, and returning true means "skip normal
            // anim/movement this frame".
            if (Necroking.NightfallPorts.RogueJump.IsJumping(uid))
            {
                if (Necroking.NightfallPorts.RogueJump.TickUnit(dt, _sim.UnitsMut, i, animData.Ctrl))
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
                                // _tableMenuUI.OpenForTable(tableIdx, sw, sh, _camera, _renderer);
                                StartTableCraft(tableIdx);


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
                // (No speculative pre-roll here: attack anims start ONLY when a
                // swing is actually queued (PendingAttack above). The old pre-roll
                // played the windup early so effect_time aligned with cooldown end,
                // but nothing guaranteed an attack would stamp — against a fleeing
                // target it produced constant phantom windups: full swing anim, no
                // dice. Animation ⇔ committed attack, one-to-one.)

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
                    animData.Ctrl.PlaybackSpeed = Movement.Locomotion.ComputePlayback(
                        _sim.Units[i], _gameData, curState, _sim.Units[i].Velocity.Length());
                }
                // Beam/Drain hold-channel (AI, timer-keyed): pin the Spell1 cast
                // anim at its effect frame while the channel is live — the caster
                // holds the casting pose, then the tail plays as the wind-down and
                // the OneShot override auto-clears (see the legacy-path twin below).
                animData.Ctrl.HoldAtEffectFrame = _sim.Units[i].ChannelTimer > 0f
                    && animData.Ctrl.CurrentState == AnimState.Spell1;
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
                // Combat stance between swings. (No speculative Attack1 pre-roll —
                // attack anims start only when PendingAttack stamps, same rule as
                // the archetype path: animation ⇔ committed attack.)
                targetState = AnimState.Block;
            else if (_sim.Units[i].GhostMode)
                targetState = AnimState.Hover;
            else
            {
                bool carrying = _sim.Units[i].CarryingCorpseID >= 0;
                // While the necromancer is mid-cast (_pendingCastAnim), let the cast
                // animation drive instead of pinning the Carry pose. Forcing Carry every
                // frame fights the channeled cast's Start state (UpdateChanneledCast),
                // so it never finishes — the channel hangs in phase 0 and the
                // necromancer freezes. Repro: carry a corpse, then cast Reanimate. The
                // corpse stays carried; Carry resumes once the cast ends. (A non-carrying
                // cast already works because it falls through to Idle here.)
                bool midCast = _pendingCastAnim.HasValue && i == _sim.NecromancerIndex;
                if (carrying && !midCast)
                    targetState = AnimState.Carry;
                else
                {
                    // Movement tier picked centrally by Locomotion.UpdateLocoVectorsAndGait
                    // (from the smoothed loco vector) — same selector as archetype units.
                    var loco = _sim.Units[i].RoutineAnim.State;
                    bool locoClass = loco == AnimState.Idle || loco == AnimState.Walk
                        || loco == AnimState.Jog || loco == AnimState.Run;
                    targetState = locoClass ? loco : AnimState.Idle;
                }
            }

            // Reverse walk playback
            var facingDir = Movement.FacingUtil.ForwardDir(_sim.Units[i]);
            var vel = _sim.Units[i].Velocity;
            bool movingBackward = vel.LengthSq() > 0.1f && vel.Normalized().Dot(facingDir) < -0.3f;
            animData.Ctrl.SetReversePlayback(movingBackward);

            // Locomotion playback scaling (Walk/Jog/Run/Carry) — keeps foot-cycle
            // frequency matched to actual velocity so anims don't skate.
            animData.Ctrl.PlaybackSpeed = Movement.Locomotion.ComputePlayback(
                _sim.Units[i], _gameData, targetState, _sim.Units[i].Velocity.Length());
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
            // A channeled spell cast owns the necromancer's playback so a short CastTime
            // time-stretches Start/Loop/Finish (driven by UpdateChanneledCast). The
            // locomotion block above resets PlaybackSpeed every frame, so re-apply it here,
            // immediately before Update, or the stretch is clobbered and CastTime has no
            // effect (the cast just stays slow).
            if (_pendingCastAnim.HasValue && i == _sim.NecromancerIndex
                && IsChanneledCast(_pendingCastAnim.Value.CastAnim))
                animData.Ctrl.PlaybackSpeed = ChannelPlaybackSpeed(animData.Ctrl, _pendingCastAnim.Value, out _);
            // Beam/Drain hold-channel: pin the Spell1 cast anim at its effect frame
            // for as long as the channel is live, so the caster holds the casting
            // pose instead of playing the wind-down and idling mid-beam. Declarative
            // — the frame the channel ends (any path) the pin drops and the Spell1
            // tail plays as the natural wind-down. Covers the player (slot-keyed
            // channel) and any legacy-path unit on a timer channel.
            animData.Ctrl.HoldAtEffectFrame =
                ((i == _sim.NecromancerIndex && _channelingSlot >= 0)
                    || _sim.Units[i].ChannelTimer > 0f)
                && animData.Ctrl.CurrentState == AnimState.Spell1;
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
                        ExecuteSpellEffect(spell, i, pca.Target, pca.Slot, _pendingSpell);
                    _pendingCastAnim = null;
                    // Tail-cancel (Q3, Settings.Animation.CastTailCancel): the spell
                    // has fired; with movement input held, cut the Spell1 recovery
                    // tail straight into locomotion — otherwise the plant would end
                    // here while the tail keeps playing (a fresh slide), or the
                    // player would stand locked through frames that no longer do
                    // anything. The gait picker corrects Walk→Jog/Run next frame.
                    // EXCEPT when this cast just started a Beam/Drain hold-channel
                    // (_channelingSlot set inside ExecuteSpellEffect above): the
                    // channel needs the Spell1 pose held at this very frame.
                    if ((_gameData.Settings.Animation?.CastTailCancel ?? true)
                        && _sim.NecroMoveInputActive && _channelingSlot < 0)
                    {
                        RemoveCastingBuffAll(i);
                        animData.Ctrl.ForceState(AnimState.Walk);
                    }
                }
                else if (hasPendingAttack)
                {
                    // Impact integrity: only the unit's actual attack anim may
                    // deliver the queued swing. Without this state check, the
                    // 50%-of-clip fallback edge (which fires for ANY state,
                    // including Run loops) could resolve a lingering
                    // PendingAttack invisibly mid-locomotion.
                    var expectedAtkState = ResolvePendingAttackAnim(_sim.Units[i].Stats,
                        _sim.Units[i].PendingWeaponIdx, _sim.Units[i].PendingWeaponIsRanged,
                        _sim.Units[i].Archetype);
                    if (animData.Ctrl.CurrentState == expectedAtkState)
                        _sim.TryResolvePendingAttackAtImpact(i);
                }
            }

            // Janitor: a queued swing whose window expired without its anim ever
            // delivering the impact frame (preempted by knockdown, physics, a
            // forced state, ...) is dead — clear it so it can't resolve during
            // some later unrelated anim. Jump-phase units never reach here
            // (pounce resolves at landing, which may outlive PostAttackTimer).
            if (!_sim.Units[i].PendingAttack.IsNone && _sim.Units[i].PostAttackTimer <= 0f)
            {
                DebugLog.Log("combat",
                    $"[SwingJanitor] unit#{i} queued swing expired unresolved " +
                    $"(state={animData.Ctrl.CurrentState}, weaponIdx={_sim.Units[i].PendingWeaponIdx}) — cleared");
                _sim.UnitsMut[i].PendingAttack = CombatTarget.None;
                _sim.UnitsMut[i].PendingWeaponIdx = -1;
            }

            _unitAnims[uid] = animData;
        }

        // While WaitingForPlant the cast anim hasn't started (the player is still
        // braking): the channel machine must not run (it would ForceState the
        // Start anim immediately) and the left-Spell1 safety net must not fire
        // (the necromancer is legitimately NOT in Spell1 yet — the net would
        // execute the spell instantly at press).
        if (_pendingCastAnim != null && _pendingCastAnim.Value.WaitingForPlant)
        {
            // braking — handled by TickCastPlant
        }
        // Channeled reanimation casts run their own Start→Loop→Finish machine.
        else if (_pendingCastAnim != null && IsChanneledCast(_pendingCastAnim.Value.CastAnim))
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
                        ExecuteSpellEffect(spell, necroIdx, pca.Target, pca.Slot, _pendingSpell);
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
        Toasts.Update(dt);
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

        // Death-fog diffusion + ground-corruption spread is gameplay simulation. This
        // whole method now runs on GameClock.WorldDt (0 while paused OR in an editor),
        // and DeathFogSystem.Update no-ops on dt<=0 — so the old ad-hoc !EditorActive
        // guard is gone; the domain choice IS the gate. The corruption/grass *fades*
        // below share the same dt: they only finish transitions already begun.
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
