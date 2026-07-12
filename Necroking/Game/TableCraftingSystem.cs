using System.Collections.Generic;
using Necroking.AI;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.GameSystems;
using Necroking.Lib;
using Necroking.Movement;
using Necroking.World;

namespace Necroking.Game;

/// <summary>
/// Drives the table-crafting timer + completion. Runs every Simulation.Tick after
/// the AI pass so it can read each unit's current Routine/Subroutine to decide
/// whether the table's channeler is "actually channeling" right now.
///
/// Architecture split:
///   - PlayerControlledHandler.UpdateCraftAtTable owns ANIMATION state machine
///     (WalkToSite → WorkStart → WorkLoop → WorkEnd).
///   - This system owns CRAFT TIMER + COMPLETION (slot consumption, zombie spawn,
///     bonus-effect application). Timer only advances while the channeler is in
///     WorkLoop subroutine + within range — walking away pauses, not resets.
///
/// On completion, we leave WorkEnd transition to the handler (via ts.Crafting=false),
/// which detects the flip and runs WorkEnd → Idle on the unit.
/// </summary>
public static class TableCraftingSystem
{
    /// <summary>Channeler must stay within this many world units of the table to keep advancing the craft.</summary>
    public const float ChannelMaxRange = 2.5f;

    public static void Tick(Simulation sim, EnvironmentSystem envSystem, GameData gameData, float dt)
    {
        if (envSystem == null || gameData == null) return;

        for (int oi = 0; oi < envSystem.ObjectCount; oi++)
        {
            var def = envSystem.Defs[envSystem.GetObject(oi).DefIndex];
            if (!TableSystem.IsTable(def)) continue;
            var ts = envSystem.GetTableState(oi);
            if (!ts.Crafting) continue;

            // Resolve channeler. Anyone whose CraftTableIdx points at this table
            // and is in WorkLoop counts — typically the necromancer.
            int channelerIdx = ResolveChannelerIdx(sim.Units, oi);
            if (channelerIdx < 0) continue; // no active channeler, paused

            // Range gate
            var tableObj = envSystem.GetObject(oi);
            var tablePos = new Vec2(tableObj.X, tableObj.Y);
            float distSq = (sim.Units[channelerIdx].Position - tablePos).LengthSq();
            if (distSq > ChannelMaxRange * ChannelMaxRange) continue;

            ts.CraftTimer += dt;
            // Complete at the loop budget (set render-side so start+loop+finish fit
            // ProcessTime). Falls back to ProcessTime before the budget is computed.
            float budget = ts.LoopBudget > 0.01f ? ts.LoopBudget : def.ProcessTime;
            if (ts.CraftTimer >= budget)
            {
                CompleteCraft(sim, envSystem, gameData, oi);
            }
        }
    }

    /// <summary>Find the unit whose CraftTableIdx points at this table AND is in WorkLoop subroutine.</summary>
    private static int ResolveChannelerIdx(UnitArrays units, int envIdx)
    {
        for (int i = 0; i < units.Count; i++)
        {
            if (!units[i].Alive) continue;
            if (units[i].CraftTableIdx != envIdx) continue;
            if (units[i].Routine != PlayerControlledHandler.RoutineCraftAtTable) continue;
            if (units[i].Subroutine != PlayerControlledHandler.BuildSub_WorkLoop) continue;
            return i;
        }
        return -1;
    }

    /// <summary>
    /// Complete a craft: spawn the zombie unit derived from the corpse's source
    /// UnitDef.ZombieTypeID, apply BonusEffects from item slots, clear all slots,
    /// flip ts.Crafting=false. The handler picks up the flip next frame and runs
    /// the WorkEnd animation back to Idle.
    /// </summary>
    private static void CompleteCraft(Simulation sim, EnvironmentSystem envSystem,
        GameData gameData, int envIdx)
    {
        var ts = envSystem.GetTableState(envIdx);
        // Rise from where the corpse lay: spawn at the table itself (the table is
        // unpathable, so the zombie walks out as its first action) rather than at
        // an adjacent pathable tile.
        var tableObjForSpawn = envSystem.GetObject(envIdx);
        var spawnPos = new Vec2(tableObjForSpawn.X, tableObjForSpawn.Y);

        // Find the corpse to consume — use the first non-empty slot. (Slot index
        // doesn't matter since we only spawn one zombie per craft and the user's
        // current design is 1 corpse per table; multi-corpse-batch is a future.)
        int corpseSlotIdx = -1;
        for (int i = 0; i < ts.CorpseSlots.Length; i++)
        {
            if (!ts.CorpseSlots[i].IsEmpty) { corpseSlotIdx = i; break; }
        }
        if (corpseSlotIdx < 0)
        {
            DebugLog.Log("table", $"[Table {envIdx}] CompleteCraft aborted: no corpse in any slot");
            ts.CancelChannel();
            return;
        }

        var corpseSlot = ts.CorpseSlots[corpseSlotIdx];

        // Resolve zombie unit ID. The user spec: "any non-undead corpse that has
        // a zombie type" — we read UnitDef.ZombieTypeID and either use it directly
        // (if a unit ID) or treat it as a unit-group ID and pick randomly.
        string zombieID = ResolveZombieUnitID(gameData, corpseSlot.SourceUnitDefID);
        if (string.IsNullOrEmpty(zombieID))
        {
            DebugLog.Log("table", $"[Table {envIdx}] CompleteCraft aborted: source '{corpseSlot.SourceUnitDefID}' has no zombieTypeID");
            ts.CorpseSlots[corpseSlotIdx] = default;
            ts.CancelChannel();
            return;
        }

        // Raise into the horde via the canonical helper. This wires the
        // HordeMinion archetype (bare SpawnUnitByID does NOT — it only applies
        // the def's legacy AI enum, which for the animal zombies is the
        // leash-less "AttackClosest"; a deer crafted that way chased fleeing
        // enemies off the map forever).
        // Route the rise through the one composite reanimation effect — the same suite spells and
        // on-death raises use. The corpse was consumed into the table slot (no world body to morph),
        // so this is a corpse-less raise: a green cloud builds at the spawn point and the zombie rises
        // from it after the build-up. Game1 drains PendingZombieRaises -> QueueReanimRise; a headless
        // sim falls back to spawning immediately. Capture the item bonuses NOW (slots clear below) and
        // apply them to the zombie when it actually spawns. (zombieID was validated above; the real
        // pick happens at spawn so the source — not a maybe-random group pick — is what we record.)
        var bonuses = BuildItemBonusList(gameData, ts);
        System.Action<int>? onSpawned = null;
        if (bonuses.Count > 0)
            onSpawned = idx =>
            {
                var u = sim.UnitsMut[idx];
                u.BonusEffects ??= new List<WeaponBonusEffect>();
                u.BonusEffects.AddRange(bonuses);
            };
        sim.PendingZombieRaises.Add(new PendingZombieRaise
        {
            Position = spawnPos,
            UnitDefID = corpseSlot.SourceUnitDefID,
            FacingAngle = corpseSlot.FacingAngle,
            SpriteScale = 1f,
            CorpseId = -1,        // corpse-less: no world body, just the effect + rise
            Timer = 0f,
            OnSpawned = onSpawned,
        });

        DebugLog.Log("table",
            $"[Table {envIdx}] Queued reanim at ({spawnPos.X:F1},{spawnPos.Y:F1}) " +
            $"from corpse '{corpseSlot.SourceUnitDefID}' with {bonuses.Count} item bonus(es)");

        // Clear all slots (corpse consumed, items consumed).
        ts.CorpseSlots[corpseSlotIdx] = default;
        for (int i = 0; i < ts.ItemSlots.Length; i++)
            ts.ItemSlots[i] = default;

        // Flip crafting state — the handler animates WorkEnd → Idle next frame.
        ts.CancelChannel();
    }

    /// <summary>
    /// Resolve a zombie unit ID from a source UnitDefID's ZombieTypeID field.
    /// Returns "" if the source doesn't exist, has no ZombieTypeID, or the ID
    /// can't be resolved as a unit or group. Mirrors the resolution pattern
    /// used by the necromancer raise spell in Game1.
    /// </summary>
    public static string ResolveZombieUnitID(GameData gameData, string sourceUnitDefID)
    {
        if (string.IsNullOrEmpty(sourceUnitDefID)) return "";
        var sourceDef = gameData.Units.Get(sourceUnitDefID);
        if (sourceDef == null) return "";
        string zombieTypeID = sourceDef.ZombieTypeID;
        if (string.IsNullOrEmpty(zombieTypeID)) return "";
        if (gameData.Units.Get(zombieTypeID) != null) return zombieTypeID;
        return gameData.UnitGroups.PickRandom(zombieTypeID) ?? "";
    }

    /// <summary>True if the corpse can be loaded onto a table (non-undead source with a zombieTypeID).</summary>
    public static bool IsCorpseEligibleForTable(GameData gameData, string sourceUnitDefID)
    {
        if (string.IsNullOrEmpty(sourceUnitDefID)) return false;
        var sourceDef = gameData.Units.Get(sourceUnitDefID);
        if (sourceDef == null) return false;
        if (sourceDef.Faction == "Undead") return false;
        return !string.IsNullOrEmpty(sourceDef.ZombieTypeID);
    }

    /// <summary>
    /// Map each filled item slot to a permanent WeaponBonusEffect on the spawned
    /// zombie. Item-slot ItemIDs reference inventory items; we look up which
    /// PotionDef shares that ItemID and switch on its OnHitEffect. Items that
    /// don't match a potion are ignored (no bonus, no error — they're just dropped).
    /// </summary>
    /// <summary>Build the weapon-bonus list a crafted zombie inherits from the table's item slots.
    /// Captured at craft time (the slots clear immediately after) and applied to the zombie when it
    /// rises through the deferred composite reanimation.</summary>
    private static List<WeaponBonusEffect> BuildItemBonusList(GameData gameData, TableCraftState ts)
    {
        var list = new List<WeaponBonusEffect>();
        for (int i = 0; i < ts.ItemSlots.Length; i++)
        {
            if (ts.ItemSlots[i].IsEmpty) continue;
            var potion = FindPotionByItemId(gameData, ts.ItemSlots[i].ItemID);
            if (potion == null) continue;

            switch (potion.OnHitEffect)
            {
                case "Poison":
                    // 5 poison damage on hit, goes through armor (matches existing thrown-coat semantics).
                    list.Add(WeaponBonusEffect.Damage(DamageType.Poison, 5));
                    break;

                case "Paralysis":
                    // 30 fatigue damage on hit, ArmorNegating (bypasses armor). Fatigue
                    // drain instead of HP — DamageSystem switches on DamageType.Fatigue.
                    list.Add(WeaponBonusEffect.Damage(DamageType.Fatigue, 30, DamageFlags.ArmorNegating));
                    break;

                case "Zombie":
                    // 50% chance per hit to set defender's ZombieOnDeath flag.
                    list.Add(WeaponBonusEffect.ZombieOnDeath(50));
                    break;
            }
        }
        return list;
    }

    private static PotionDef? FindPotionByItemId(GameData gameData, string itemId)
    {
        if (gameData.Potions == null || string.IsNullOrEmpty(itemId)) return null;
        var ids = gameData.Potions.GetIDs();
        for (int i = 0; i < ids.Count; i++)
        {
            var p = gameData.Potions.Get(ids[i]);
            if (p != null && p.ItemID == itemId) return p;
        }
        return null;
    }

    private static int CountFilledItemSlots(TableCraftState ts)
    {
        int n = 0;
        for (int i = 0; i < ts.ItemSlots.Length; i++)
            if (!ts.ItemSlots[i].IsEmpty) n++;
        return n;
    }
}
