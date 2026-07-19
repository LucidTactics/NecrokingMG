using System;
using Microsoft.Xna.Framework.Input;
using Necroking.Core;
using Necroking.GameSystems;
using Necroking.Lib;

namespace Necroking;

// World interaction input: what a mouse click (and the F interact key) does to
// things in the world — buildings with panels, corpse piles, foragables.
// Mouse buttons are reserved for world interaction; spell casting is keyboard-
// only (see SpellBarBindings).
//
// Consume convention: handlers are gated on !_input.MouseOverUI — ConsumeMouse()
// sets MouseOverUI too, so "consumed" and "over UI" collapse into one check.
// Every branch that acts on the world calls _input.ConsumeMouse() so nothing
// later this frame double-acts on the same click.
public partial class Game1
{
    /// <summary>Nearest Empty Grave (IsWorkerHome def) under the cursor, or -1.</summary>
    private int FindGraveUnderCursor(Vec2 mouseWorld, float clickRange = 1.6f)
        => _sim.Query.NearestEnvObject(mouseWorld, clickRange, new EnvWorkerHomes());

    // Nearest built Corpse Pile under the cursor (click-to-gather target), or -1.
    private int FindCorpsePileUnderCursor(Vec2 mouseWorld, float clickRange = 1.6f)
    {
        int pileDef = _envSystem != null ? _envSystem.FindDef("corpse_pile") : -1;
        if (pileDef < 0) return -1;
        return _sim.Query.NearestEnvObject(mouseWorld, clickRange, pileDef);
    }

    /// <summary>Built Corpse Piles that actually hold a corpse — the standard
    /// pile gate plus a stock check via the worker system (the WorldQuery
    /// escape hatch for state that lives outside EnvironmentSystem).</summary>
    private readonly struct StockedCorpsePiles : IEnvQueryFilter
    {
        private readonly EnvByDefIndex _pile;
        private readonly Game.Jobs.WorkerSystem _workers;
        public StockedCorpsePiles(int pileDef, Game.Jobs.WorkerSystem workers)
        { _pile = new EnvByDefIndex(pileDef); _workers = workers; }
        public bool Match(World.EnvironmentSystem env, int i)
            => _pile.Match(env, i) && _workers.StoredOf(i, Game.Jobs.JobResources.Corpse) > 0;
    }

    // Nearest built Corpse Pile that actually holds a corpse, within range of a point
    // (used by the F-key pickup so it grabs from a pile the same way as a loose body).
    private int FindNearestCorpsePileInRange(Vec2 from, float range)
    {
        int pileDef = _envSystem != null ? _envSystem.FindDef("corpse_pile") : -1;
        if (pileDef < 0) return -1;
        return _sim.Query.NearestEnvObject(from, range, new StockedCorpsePiles(pileDef, _workerSystem));
    }

    /// <summary>World-object panel routes, ordered — the first pick that hits
    /// wins. Each route pairs a "what's under the cursor" pick with the action
    /// that opens the object's service panel. The routing (this table) is input
    /// code; each panel's service callbacks stay wired where its owning system
    /// is set up (EnsureInventoryUIsInitialized). When a third building panel
    /// arrives, consider one nearest-interactable pick + an env-def-tag →
    /// open-action dictionary instead of per-route picks.</summary>
    private (Func<Vec2, int> Pick, Action<int, int, int> Open)[]? _worldPanelRoutes;

    private void EnsureWorldPanelRoutes() => _worldPanelRoutes ??= new (Func<Vec2, int>, Action<int, int, int>)[]
    {
        // Craft table → its craft menu (world-anchored above the table).
        (mw => _envSystem != null ? Game.TableSystem.FindTableUnderCursor(_envSystem, mw) : -1,
         (idx, sw, sh) => { EnsureInventoryUIsInitialized(); _tableMenuUI.OpenForTable(idx, sw, sh, _camera, _renderer); }),
        // Empty Grave → its worker-assignment roster.
        (mw => FindGraveUnderCursor(mw),
         (idx, sw, sh) => { EnsureInventoryUIsInitialized(); _graveRosterUI.OpenForGrave(idx, sw, sh); }),
    };

    /// <summary>Per-frame world-click dispatch, called from Update once the HUD /
    /// popup layers have had their shot at the mouse. LMB selects/interacts
    /// (open a building's panel, gather from a pile, attack a clicked enemy);
    /// RMB is the context action (forage → pile gather → attack).</summary>
    internal void HandleWorldClicks(int screenW, int screenH, int mouseX, int mouseY,
        Vec2 mouseWorld, int necroIdx)
    {
        if (_input.LeftPressed && !_input.MouseOverUI)
            HandleWorldLeftClick(screenW, screenH, mouseX, mouseY, mouseWorld, necroIdx);
        if (_input.RightPressed && !_input.MouseOverUI)
            HandleWorldRightClick(mouseWorld, necroIdx);
    }

    private void HandleWorldLeftClick(int screenW, int screenH, int mouseX, int mouseY,
        Vec2 mouseWorld, int necroIdx)
    {
        // 0. Circle-targeted spell aim owns the click: cast at the clicked
        // point. A failed cast (out of range / mana / cooldown — the fail text
        // already spawns) keeps the aim armed so the player can just re-click.
        if (AimedSpell() != null)
        {
            var result = DispatchSpellCast(_spellBarState.Slots[_aimingSlot].SpellID,
                necroIdx, _aimingSlot, mouseWorld);
            if (result == CastResult.Success) _aimingSlot = -1;
            _input.ConsumeMouse();
            return;
        }

        // 1. Building placement mode owns the click outright.
        if (_buildingMenuUI.IsPlacementActive)
        {
            if (!_buildingMenuUI.ContainsMouse(mouseX, mouseY))
            {
                _buildingMenuUI.TryPlace(mouseWorld.X, mouseWorld.Y);
                _input.ConsumeMouse();
            }
            return;
        }

        // 2. Buildings with a service panel (craft table, grave roster).
        EnsureWorldPanelRoutes();
        foreach (var route in _worldPanelRoutes!)
        {
            int idx = route.Pick(mouseWorld);
            if (idx < 0) continue;
            route.Open(idx, screenW, screenH);
            _input.ConsumeMouse();
            return;
        }

        // 3. Corpse Pile → gather a corpse by hand, exactly like the F-key
        // pickup: grab one if close enough, otherwise nothing (no auto-walk).
        if (TryPileGatherClick(mouseWorld, necroIdx)) return;

        // 4. Enemy under cursor → order the necromancer to melee it.
        if (TryAttackClick(mouseWorld, necroIdx)) return;

        // Nothing hit — the click falls through unconsumed. (A future
        // unit-select interaction slots in here.)
    }

    private void HandleWorldRightClick(Vec2 mouseWorld, int necroIdx)
    {
        // 0. Right-click cancels a circle-targeted spell aim (mirrors building
        // placement) — and does nothing else on that click.
        if (_aimingSlot >= 0)
        {
            _aimingSlot = -1;
            return;
        }

        // 1. Foragable within reach of the necromancer.
        if (necroIdx >= 0)
        {
            int bestIdx = _foragables.FindNearest(_sim.Units[necroIdx].Position, 2f);
            if (bestIdx >= 0)
            {
                _foragables.StartCollection(bestIdx);
                _input.ConsumeMouse();
                return;
            }
        }

        // 2. Corpse Pile → same hand-gather as a left click.
        if (TryPileGatherClick(mouseWorld, necroIdx)) return;

        // 3. Enemy under cursor → attack.
        if (TryAttackClick(mouseWorld, necroIdx)) return;
    }

    /// <summary>Click on a Corpse Pile: pull one corpse by hand. Returns true
    /// (and consumes the click) when a pile was under the cursor, even if the
    /// grab itself failed — the failure feedback owns the click.</summary>
    private bool TryPileGatherClick(Vec2 mouseWorld, int necroIdx)
    {
        int clickedPile = FindCorpsePileUnderCursor(mouseWorld);
        if (clickedPile < 0 || necroIdx < 0
            || _sim.Units[necroIdx].CarryingCorpseID >= 0
            || _sim.Units[necroIdx].CorpseInteractPhase != 0)
            return false;
        if (!TryTakeCorpseFromPile(necroIdx, clickedPile))
        {
            // Don't fail silently — say why (the pile art looks full
            // even when its corpse count is 0).
            bool empty = _workerSystem.StoredOf(
                clickedPile, Game.Jobs.JobResources.Corpse) <= 0;
            SpawnCastFailText(necroIdx, empty ? "Pile Empty" : "Too Far");
        }
        _input.ConsumeMouse();
        return true;
    }

    private enum CursorMeleeResult { NoTarget, OutOfReach, Ordered }

    /// <summary>Single "order the necromancer to melee the enemy at the cursor"
    /// implementation, shared by the world-click attack (TryAttackClick) and the
    /// melee_gather built-in (TryMeleeOrGather). Pick = nearest enemy to the
    /// cursor (WorldQuery); reach gate = MeleeRangeUtil.Compute (the SSOT melee
    /// range formula the AI and sim use); on success stamps Target +
    /// PendingAttack + the def-driven attack cooldown. Callers own their
    /// pre-gates (busy / cooldown) and failure feedback. Pass
    /// <paramref name="orderAllowed"/> = false to pick + reach-check without
    /// stamping (busy caster whose click should still be consumed).</summary>
    private CursorMeleeResult TryOrderMeleeAtCursor(int necroIdx, Vec2 mouseWorld,
        float pickRadius, bool orderAllowed = true)
    {
        int enemyIdx = FindClosestEnemyToPoint(mouseWorld, pickRadius);
        if (enemyIdx < 0) return CursorMeleeResult.NoTarget;
        // Reach gate: only swing when the enemy is actually within melee range
        // of the necromancer. The cursor pick above only checks proximity to
        // the mouse, not to the caster — without this you could melee an enemy
        // anywhere on screen.
        float meleeRange = GameSystems.Combat.MeleeRangeUtil.Compute(
            _sim.Units, necroIdx, enemyIdx, _gameData);
        float dist = (_sim.Units[enemyIdx].Position - _sim.Units[necroIdx].Position).Length();
        if (dist > meleeRange) return CursorMeleeResult.OutOfReach;

        if (orderAllowed)
        {
            _sim.UnitsMut[necroIdx].Target = CombatTarget.Unit(_sim.Units[enemyIdx].Id);
            _sim.UnitsMut[necroIdx].PendingAttack = CombatTarget.Unit(_sim.Units[enemyIdx].Id);
            _sim.UnitsMut[necroIdx].AttackCooldown =
                _gameData.Units.Get(_sim.Units[necroIdx].UnitDefID)?.AttackCooldown
                ?? _gameData.Settings.Combat.AttackCooldown;
        }
        return CursorMeleeResult.Ordered;
    }

    /// <summary>Click on an enemy: order the necromancer to melee the clicked
    /// unit. Uses the same target stamp as the old LMB melee fallback, but aims
    /// at the unit under the cursor rather than the nearest to the caster.</summary>
    private bool TryAttackClick(Vec2 mouseWorld, int necroIdx)
    {
        if (necroIdx < 0) return false;
        // Busy mid-cast or mid-swing — the click still "hits" the enemy under
        // the cursor, so it must be consumed, but no new order is stamped.
        bool busy = _pendingCastAnim != null || !_sim.Units[necroIdx].PendingAttack.IsNone;
        var result = TryOrderMeleeAtCursor(necroIdx, mouseWorld,
            _gameData.Settings.Tooltips.HoverPickRadius, orderAllowed: !busy);
        if (result == CursorMeleeResult.NoTarget) return false;
        // Out of reach still counts as hitting the enemy under the cursor, so
        // consume the click and report "Too Far" (mirrors the pile-gather
        // feedback) rather than swinging at empty air or falling through.
        if (result == CursorMeleeResult.OutOfReach)
            SpawnCastFailText(necroIdx, "Too Far");
        _input.ConsumeMouse();
        return true;
    }

    /// <summary>F-key world interaction: load a carried corpse onto a craft table
    /// under the cursor, otherwise the normal corpse pickup/put-down — falling back
    /// to pulling a corpse from a nearby pile when nothing loose is in reach.</summary>
    private void HandleInteractKey(int necroIdx, Vec2 mouseWorld)
    {
        // Table-load takes precedence over normal PutDown when carrying a corpse
        // AND the cursor is over a table within InteractRange of the necromancer.
        // Full table → blocked (no PutDown either; corpse stays held).
        bool tableHandled = false;
        if (necroIdx >= 0 && _sim.Units[necroIdx].CarryingCorpseID >= 0
            && _sim.Units[necroIdx].CorpseInteractPhase == 0
            && _envSystem != null)
        {
            var necroPos = _sim.Units[necroIdx].Position;
            int tableIdx = Game.TableSystem.FindTableUnderCursorInRange(_envSystem, mouseWorld, necroPos);
            if (tableIdx >= 0)
            {
                int corpseId = _sim.Units[necroIdx].CarryingCorpseID;
                var cc = _sim.FindCorpseByID(corpseId);
                bool corpseEligible = cc != null
                    && Game.TableCraftingSystem.IsCorpseEligibleForTable(_gameData, cc.UnitDefID);
                // Skill-tree raise gate: can't load a corpse type the player hasn't
                // unlocked (unlock_summon) onto a reanimation table. God mode bypasses.
                // Handled like a full table (message, corpse stays held) rather than
                // falling through to PutDown — dropping the body right after the
                // refusal would read as "did it load?".
                if (corpseEligible && !Game.TableCraftingSystem.IsRaiseUnlocked(_gameData,
                        _skillBookState, cc!.UnitDefID,
                        Necroking.GameSystems.BuffSystem.HasBuff(_sim.Units, necroIdx, "buff_god_mode")))
                {
                    SpawnCastFailText(necroIdx, "Not Studied");
                    DebugLog.Log("table", $"[F-press] BLOCKED: '{cc.UnitDefID}' raise not unlocked");
                    tableHandled = true;
                    corpseEligible = false;
                }
                var ts = _envSystem.GetTableState(tableIdx);
                int emptySlot = ts.FindEmptyCorpseSlot();

                DebugLog.Log("table",
                    $"[F-press] tableIdx={tableIdx} corpseDef='{cc?.UnitDefID ?? "null"}' " +
                    $"eligible={corpseEligible} emptyCorpseSlot={emptySlot}");

                if (corpseEligible)
                {
                    if (emptySlot >= 0)
                    {
                        if (cc != null)
                            cc.LerpStartPos = Game.TableSystem.GetSpawnPos(_envSystem, tableIdx);
                        // Enters the PutDown visual AND schedules the corpse transfer + craft
                        // start on the sim clock (no longer gated on IsAnimFinished).
                        BeginCorpsePutDown(necroIdx, tableIdx);
                        DebugLog.Log("table", $"[F-press] Started PutDown anim → table {tableIdx}");
                    }
                    else
                    {
                        DebugLog.Log("table", $"[F-press] BLOCKED: table {tableIdx} corpse slots full");
                    }
                    tableHandled = true;
                }
                else
                {
                    DebugLog.Log("table",
                        $"[F-press] Corpse '{cc?.UnitDefID ?? "null"}' not eligible — falling through to normal PutDown");
                }
            }
            else if (_sim.Units[necroIdx].CarryingCorpseID >= 0)
            {
                // Carrying but no table under cursor — log only when this would have mattered.
                DebugLog.Log("table",
                    $"[F-press] Carrying but no table found under cursor at ({mouseWorld.X:F1},{mouseWorld.Y:F1}) " +
                    $"within necroRange={Game.TableSystem.InteractRange} cursorRange={Game.TableSystem.CursorRange}");
            }
        }
        if (!tableHandled)
        {
            bool wasCarrying = necroIdx >= 0 && _sim.Units[necroIdx].CarryingCorpseID >= 0;
            Game.CorpseInteractionManager.TryInteract(_sim, necroIdx);
            // Nothing loose to grab? If a Corpse Pile is in reach, pull one from
            // it — same pickup as a loose corpse — so F is consistent near piles.
            if (necroIdx >= 0 && !wasCarrying
                && _sim.Units[necroIdx].CarryingCorpseID < 0
                && _sim.Units[necroIdx].CorpseInteractPhase == 0)
            {
                int pile = FindNearestCorpsePileInRange(_sim.Units[necroIdx].Position, 3f);
                if (pile >= 0) TryTakeCorpseFromPile(necroIdx, pile);
            }
        }
    }
}
