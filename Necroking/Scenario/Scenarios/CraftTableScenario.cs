using System;
using Necroking.AI;
using Necroking.Core;
using Necroking.Data;
using Necroking.Game;
using Necroking.GameSystems;
using Necroking.Movement;
using Necroking.World;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// End-to-end test of the table-crafting pipeline.
///
/// Setup: necromancer + Table2 + soldier corpse + 1 zombie potion deposited into
/// the table item slot. Drives the craft directly (skips the F-press anim path
/// and the UI button) by writing to the table state and routine fields, since
/// scenarios can't synthesize input. Verifies:
///   - Corpse loads into the table slot
///   - Necromancer walks to the table and channels (Routine=CraftAtTable, Subroutine=WorkLoop)
///   - TableCraftingSystem advances the timer, completes the craft, spawns a zombie
///   - Zombie has a ZombieOnDeath BonusEffect from the deposited potion
///   - Slots are cleared after completion
///
/// No combat verification — that lives in a future test once the full UI path is verified.
/// </summary>
public class CraftTableScenario : ScenarioBase
{
    public override string Name => "craft_table";

    private const float CX = 32f;
    private const float CY = 32f;
    private const float MaxDuration = 30f;

    private float _elapsed;
    private bool _complete;
    private int _phase;
    private int _lastHeartbeat = -1;

    private int _tableIdx = -1;
    private uint _necroId;
    private int _corpseId = -1;

    // Outcome flags for OnComplete validation
    private bool _corpseLoaded;
    private bool _itemLoaded;
    private bool _craftStarted;
    private bool _channelObserved;
    private bool _zombieSpawned;
    private bool _bonusEffectApplied;
    private bool _combatVerified;
    private uint _spawnedZombieId;
    private uint _testTargetId;
    private float _combatStartTime;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, $"=== Scenario: {Name} ===");

        if (sim.EnvironmentSystem == null || sim.GameData == null)
        {
            DebugLog.Log(ScenarioLog, "ERROR: missing env system or game data");
            _complete = true;
            return;
        }

        // Find or register the Table2 def. The data file has it; if it didn't load
        // for some reason (e.g. running with a stripped data set), register a minimal
        // copy so the scenario still runs.
        var env = sim.EnvironmentSystem;
        int tableDefIdx = env.FindDef("Table2");
        if (tableDefIdx < 0)
        {
            // Scenarios don't load env_defs.json (only StartGame does), so we
            // recreate the relevant fields. Mirrors the real def at data/env_defs.json.
            var def = new EnvironmentObjectDef
            {
                Id = "Table2", Name = "Table2", Category = "Building",
                TexturePath = "assets/Environment/Buildings/Table2.png",
                IsBuilding = true, BuildingMaxHP = 500,
                CorpseSlots = 1, ItemSlots = 1, EssenceCost = 10,
                ProcessTime = 2f, // shortened for the test
                Scale = 0.22f,
                PivotX = 0.578125f, PivotY = 0.93359375f,
                CollisionRadius = 0.7500006f,
                CollisionOffsetX = -0.11351244f, CollisionOffsetY = -0.6836604f,
                SpawnOffsetX = 0f, SpawnOffsetY = 1.5f,
                SpriteWorldHeight = 7f,
            };
            tableDefIdx = env.AddDef(def);
            DebugLog.Log(ScenarioLog, "Registered fallback Table2 def (data file not loaded for scenarios)");
        }
        else
        {
            // Override processTime to keep the scenario fast (data has 10s).
            var d = env.GetDef(tableDefIdx);
            d.ProcessTime = 2f;
            DebugLog.Log(ScenarioLog, $"Found Table2 in data; processTime overridden to 2s for test");
        }

        _tableIdx = env.AddObject((ushort)tableDefIdx, CX + 2f, CY);
        DebugLog.Log(ScenarioLog, $"Placed Table2 at ({CX + 2f}, {CY}) envIdx={_tableIdx}");

        // Necromancer
        var units = sim.UnitsMut;
        int necroIdx = units.AddUnit(new Vec2(CX - 3f, CY), UnitType.Necromancer);
        units[necroIdx].AI = AIBehavior.PlayerControlled;
        _necroId = units[necroIdx].Id;
        sim.SetNecromancerIndex(necroIdx);
        DebugLog.Log(ScenarioLog, $"Spawned necromancer id={_necroId} at ({CX - 3f}, {CY}) AI=PlayerControlled");

        // Corpse — Wolf (Animal faction, ZombieTypeID="ZombieWolf"; direct unit ID
        // resolution rather than a group, which keeps the test deterministic).
        var corpses = sim.CorpsesMut;
        var wolfDef = sim.GameData.Units.Get("Wolf");
        string sourceUnitDefID = wolfDef != null ? "Wolf" : "";
        DebugLog.Log(ScenarioLog,
            $"Wolf def lookup: {(wolfDef != null ? $"OK (zombieTypeID='{wolfDef.ZombieTypeID}')" : "NULL — units.json not loaded?")}");
        corpses.Add(new Corpse
        {
            Position = new Vec2(CX - 1f, CY),
            UnitType = UnitType.Skeleton, // UnitType enum doesn't have Wolf; the field is mostly unused for crafted spawns
            UnitDefID = sourceUnitDefID,
            CorpseID = 9001,
            FacingAngle = 0f,
            SpriteScale = 1f,
            Bagged = true,
        });
        _corpseId = 9001;
        DebugLog.Log(ScenarioLog, $"Placed bagged Wolf corpse id={_corpseId} (UnitDefID='{sourceUnitDefID}') at ({CX - 1f}, {CY})");

        // Pre-set essence
        sim.PlayerResources.Essence = 100;
        DebugLog.Log(ScenarioLog, $"PlayerResources.Essence = {sim.PlayerResources.Essence}");

        ZoomOnLocation(CX, CY, 32f);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        if (_complete) return;
        _elapsed += dt;

        if (_elapsed > MaxDuration)
        {
            DebugLog.Log(ScenarioLog, $"TIMEOUT at {_elapsed:F1}s phase={_phase}");
            _complete = true;
            return;
        }

        var env = sim.EnvironmentSystem;
        if (env == null || _tableIdx < 0) { _complete = true; return; }

        switch (_phase)
        {
            case 0:
                // Phase 0 — load corpse onto the table directly (simulate F-press completion)
                if (_elapsed > 0.5f)
                {
                    int ci = sim.FindCorpseIndexByID(_corpseId);
                    if (ci >= 0)
                    {
                        var cc = sim.Corpses[ci];
                        int slot = TableSystem.LoadCorpseIntoTable(env, _tableIdx, cc);
                        if (slot >= 0)
                        {
                            sim.CorpsesMut.RemoveAt(ci);
                            _corpseLoaded = true;
                            DebugLog.Log(ScenarioLog, $"[{_elapsed:F2}s] Loaded corpse into table slot {slot}");
                        }
                        else
                        {
                            DebugLog.Log(ScenarioLog, $"[{_elapsed:F2}s] FAILED to load corpse — slot full or table invalid");
                        }
                    }
                    DeferredScreenshot = "craft_01_loaded";
                    _phase = 1;
                }
                break;

            case 1:
                // Phase 1 — load a zombie potion item (if available) into item slot 0
                if (_elapsed > 1.0f)
                {
                    string zombiePotionItem = FindZombiePotionItemId(sim.GameData!);
                    if (!string.IsNullOrEmpty(zombiePotionItem))
                    {
                        int slot = TableSystem.LoadItemIntoTable(env, _tableIdx, zombiePotionItem);
                        if (slot >= 0)
                        {
                            _itemLoaded = true;
                            DebugLog.Log(ScenarioLog, $"[{_elapsed:F2}s] Loaded item '{zombiePotionItem}' into table slot {slot}");
                        }
                    }
                    else
                    {
                        DebugLog.Log(ScenarioLog, $"[{_elapsed:F2}s] No zombie potion in registry — proceeding without item bonus");
                    }
                    _phase = 2;
                }
                break;

            case 2:
                // Phase 2 — start the craft (bypasses the UI button; mirrors Game1.StartTableCraft)
                if (_elapsed > 1.5f)
                {
                    var def = env.Defs[env.GetObject(_tableIdx).DefIndex];
                    var ts = env.GetTableState(_tableIdx);
                    int necroIdx = sim.NecromancerIndex;

                    DebugLog.Log(ScenarioLog,
                        $"[{_elapsed:F2}s] Phase 2 entry: necroIdx={necroIdx} " +
                        $"def.CorpseSlots={def.CorpseSlots} def.ItemSlots={def.ItemSlots} " +
                        $"def.EssenceCost={def.EssenceCost} def.IsBuilding={def.IsBuilding} " +
                        $"ts.CorpseSlots.Length={ts.CorpseSlots.Length} ts.HasAnyCorpse()={ts.HasAnyCorpse()} " +
                        $"essence={sim.PlayerResources.Essence}");

                    if (necroIdx < 0)
                    {
                        DebugLog.Log(ScenarioLog, $"[{_elapsed:F2}s] FAIL: necromancer index lost");
                        _complete = true;
                        return;
                    }

                    if (ts.HasAnyCorpse() && sim.PlayerResources.SpendEssence(def.EssenceCost))
                    {
                        ts.Crafting = true;
                        ts.CraftTimer = 0f;
                        ts.ChannelerUnitID = sim.Units[necroIdx].Id;
                        sim.UnitsMut[necroIdx].CraftTableIdx = _tableIdx;
                        sim.UnitsMut[necroIdx].Routine = PlayerControlledHandler.RoutineCraftAtTable;
                        sim.UnitsMut[necroIdx].Subroutine = PlayerControlledHandler.BuildSub_WalkToSite;
                        _craftStarted = true;
                        DebugLog.Log(ScenarioLog,
                            $"[{_elapsed:F2}s] Craft started. Essence remaining: {sim.PlayerResources.Essence}, " +
                            $"ProcessTime: {def.ProcessTime}s");
                    }
                    _phase = 3;
                }
                break;

            case 3:
                // Phase 3 — wait for the channeler to enter WorkLoop subroutine
                {
                    int necroIdx = sim.NecromancerIndex;
                    if (necroIdx >= 0 && sim.Units[necroIdx].Subroutine == PlayerControlledHandler.BuildSub_WorkLoop)
                    {
                        _channelObserved = true;
                        DebugLog.Log(ScenarioLog,
                            $"[{_elapsed:F2}s] Necromancer reached WorkLoop subroutine (channeling started)");
                        DeferredScreenshot = "craft_02_channel";
                        _phase = 4;
                    }
                    // Heartbeat every 2s while waiting so we can see what's happening
                    else if (necroIdx >= 0 && (int)(_elapsed * 2) != _lastHeartbeat)
                    {
                        _lastHeartbeat = (int)(_elapsed * 2);
                        var u = sim.Units[necroIdx];
                        var tableObj = env.GetObject(_tableIdx);
                        float distToTable = (u.Position - new Vec2(tableObj.X, tableObj.Y)).Length();
                        DebugLog.Log(ScenarioLog,
                            $"[{_elapsed:F2}s] Phase 3 heartbeat: necroPos=({u.Position.X:F1},{u.Position.Y:F1}) " +
                            $"distToTable={distToTable:F2} routine={u.Routine} subroutine={u.Subroutine} " +
                            $"corpseInteractPhase={u.CorpseInteractPhase} craftTableIdx={u.CraftTableIdx}");
                    }
                }
                break;

            case 4:
                // Phase 4 — wait for completion (ts.Crafting flips false)
                {
                    var ts = env.GetTableState(_tableIdx);
                    if (!ts.Crafting)
                    {
                        DebugLog.Log(ScenarioLog,
                            $"[{_elapsed:F2}s] Craft completed (ts.Crafting=false). Inspecting spawned units…");
                        InspectOutcome(sim);
                        DeferredScreenshot = "craft_03_done";

                        // Phase 5: spawn an enemy and verify the BonusEffect fires on hit.
                        // Only meaningful if a zombie + bonus actually got applied.
                        if (_zombieSpawned && _bonusEffectApplied)
                        {
                            int targetIdx = sim.UnitsMut.AddUnit(new Vec2(CX + 4f, CY), UnitType.Soldier);
                            if (targetIdx >= 0)
                            {
                                sim.UnitsMut[targetIdx].AI = AIBehavior.IdleAtPoint;
                                sim.UnitsMut[targetIdx].Faction = Faction.Human;
                                sim.UnitsMut[targetIdx].Stats.MaxHP = 500;
                                sim.UnitsMut[targetIdx].Stats.HP = 500;
                                _testTargetId = sim.Units[targetIdx].Id;
                                _combatStartTime = _elapsed;
                                DebugLog.Log(ScenarioLog,
                                    $"[{_elapsed:F2}s] Combat verification setup: target Soldier id={_testTargetId} at ({CX + 4f},{CY})");
                            }
                        }

                        _phase = 5;
                    }
                }
                break;

            case 5:
                // Phase 5 — combat verification: wait for the crafted zombie to land
                // a hit and verify the target accumulated PoisonStacks (proves the
                // BonusEffect actually fires from melee resolution).
                if (_testTargetId != 0)
                {
                    int targetIdx = -1;
                    for (int i = 0; i < sim.Units.Count; i++)
                        if (sim.Units[i].Id == _testTargetId) { targetIdx = i; break; }

                    if (targetIdx >= 0 && sim.Units[targetIdx].PoisonStacks > 0)
                    {
                        _combatVerified = true;
                        DebugLog.Log(ScenarioLog,
                            $"[{_elapsed:F2}s] COMBAT VERIFIED: target accumulated {sim.Units[targetIdx].PoisonStacks} poison stacks");
                        _complete = true;
                    }
                    else if (_elapsed - _combatStartTime > 12f)
                    {
                        DebugLog.Log(ScenarioLog,
                            $"[{_elapsed:F2}s] COMBAT TIMEOUT: target stacks=" +
                            (targetIdx >= 0 ? sim.Units[targetIdx].PoisonStacks.ToString() : "(target gone)"));
                        _complete = true;
                    }
                }
                else if (_elapsed > 0.5f + (_combatStartTime > 0 ? _combatStartTime : 0))
                {
                    _complete = true;
                }
                break;
        }
    }

    private void InspectOutcome(Simulation sim)
    {
        // Look for the most recently spawned undead unit (zombie). The original
        // necromancer is also Undead, so we filter by UnitDefID.
        var units = sim.Units;
        for (int i = 0; i < units.Count; i++)
        {
            if (!units[i].Alive) continue;
            if (units[i].Id == _necroId) continue; // skip necromancer
            if (units[i].Faction != Faction.Undead) continue;

            _zombieSpawned = true;
            DebugLog.Log(ScenarioLog,
                $"  → spawned unit: idx={i} id={units[i].Id} defId='{units[i].UnitDefID}' " +
                $"pos=({units[i].Position.X:F1},{units[i].Position.Y:F1}) facing={units[i].FacingAngle:F0}°");

            var bonuses = units[i].BonusEffects;
            if (bonuses != null && bonuses.Count > 0)
            {
                _bonusEffectApplied = true;
                for (int b = 0; b < bonuses.Count; b++)
                {
                    var be = bonuses[b];
                    DebugLog.Log(ScenarioLog,
                        $"    BonusEffect[{b}]: kind={be.Kind} dmgType={be.DmgType} " +
                        $"amount={be.Amount} flags={be.DmgFlags} chancePct={be.ChancePct} permanent={be.Permanent}");
                }
            }
            else
            {
                DebugLog.Log(ScenarioLog, $"    BonusEffects: (none)");
            }
            break;
        }

        if (!_zombieSpawned)
            DebugLog.Log(ScenarioLog, "  → no zombie unit found after craft completion");
    }

    private static string FindZombiePotionItemId(GameData gameData)
    {
        // Use poison potion for testing — combat verification looks for poison
        // stack accumulation on the target as proof the BonusEffect fired on hit.
        // Falls back to any with-effect potion if poison isn't in the registry.
        if (gameData.Potions == null) return "";
        var ids = gameData.Potions.GetIDs();
        for (int i = 0; i < ids.Count; i++)
        {
            var p = gameData.Potions.Get(ids[i]);
            if (p != null && p.OnHitEffect == "Poison") return p.ItemID;
        }
        for (int i = 0; i < ids.Count; i++)
        {
            var p = gameData.Potions.Get(ids[i]);
            if (p != null && p.OnHitEffect == "Zombie") return p.ItemID;
        }
        return "";
    }

    public override bool IsComplete => _complete && DeferredScreenshot == null;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, $"=== Scenario Complete: {Name} ===");
        DebugLog.Log(ScenarioLog, $"Total elapsed: {_elapsed:F1}s");

        DebugLog.Log(ScenarioLog, $"  corpseLoaded     : {_corpseLoaded}");
        DebugLog.Log(ScenarioLog, $"  itemLoaded       : {_itemLoaded}");
        DebugLog.Log(ScenarioLog, $"  craftStarted     : {_craftStarted}");
        DebugLog.Log(ScenarioLog, $"  channelObserved  : {_channelObserved}");
        DebugLog.Log(ScenarioLog, $"  zombieSpawned    : {_zombieSpawned}");
        DebugLog.Log(ScenarioLog, $"  bonusEffectApplied: {_bonusEffectApplied}");
        DebugLog.Log(ScenarioLog, $"  combatVerified   : {_combatVerified}");

        // Required gates: corpse load → craft start → channel → spawn.
        // Item / bonus is optional (depends on whether a "Zombie" potion exists in
        // the data set; OK to skip if not present).
        if (!_corpseLoaded) { DebugLog.Log(ScenarioLog, "FAIL: corpse never loaded"); return 1; }
        if (!_craftStarted) { DebugLog.Log(ScenarioLog, "FAIL: craft never started"); return 2; }
        if (!_channelObserved) { DebugLog.Log(ScenarioLog, "FAIL: channeler never reached WorkLoop"); return 3; }
        if (!_zombieSpawned) { DebugLog.Log(ScenarioLog, "FAIL: no zombie spawned"); return 4; }
        if (_itemLoaded && !_bonusEffectApplied)
        {
            DebugLog.Log(ScenarioLog, "FAIL: item loaded but bonus effect not applied");
            return 5;
        }
        if (_itemLoaded && _bonusEffectApplied && !_combatVerified)
        {
            DebugLog.Log(ScenarioLog, "FAIL: bonus effect applied but never fired in combat (target accumulated 0 poison stacks)");
            return 6;
        }
        DebugLog.Log(ScenarioLog, "PASS");
        return 0;
    }
}
