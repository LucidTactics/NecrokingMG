using System.Collections.Generic;
using Necroking.Core;
using Necroking.GameSystems;
using Necroking.World;

namespace Necroking.Game;

/// <summary>
/// Static helpers for craft-table interaction (Table2 etc.). A "table" is any
/// EnvironmentObjectDef with CorpseSlots > 0. This class owns the geometry
/// queries (cursor-over-table + range-from-unit) and the slot-load logic;
/// crafting tick lives in TableCraftingSystem (Phase E).
///
/// Decoupled from Simulation/Game1 so tests + scenarios can drive it directly.
/// </summary>
public static class TableSystem
{
    /// <summary>Necromancer must be within this many world units of a table to load a corpse onto it.</summary>
    public const float InteractRange = 2.0f;

    /// <summary>
    /// Cursor must be within this many world units of a table's center to count as "over the table".
    /// Tables have ~1.5 world-unit-tall sprites with pivots at the base, so the visible top is up to
    /// 1.4 world units above the center; 2.0 covers the whole sprite plus a small forgiveness margin.
    /// Mirrors the building-hover threshold (radius=2.0) used in Game1's hover detection.
    /// </summary>
    public const float CursorRange = 2.0f;

    /// <summary>True if this def is a craft-table (slot-based recipe).</summary>
    public static bool IsTable(EnvironmentObjectDef def)
        => def.IsBuilding && def.CorpseSlots > 0;

    /// <summary>
    /// Find the closest table whose center is within CursorRange of `mouseWorld`
    /// AND within InteractRange of `unitPos`. Returns env-object index, or -1
    /// if no table satisfies both gates. Used at F-press time to decide whether
    /// to dispatch to TryLoadCorpse or fall through to normal PutDown.
    /// </summary>
    public static int FindTableUnderCursorInRange(EnvironmentSystem env, Vec2 mouseWorld, Vec2 unitPos)
    {
        int best = -1;
        float bestCursorSq = CursorRange * CursorRange;
        float interactSq = InteractRange * InteractRange;

        for (int oi = 0; oi < env.ObjectCount; oi++)
        {
            var obj = env.GetObject(oi);
            var def = env.Defs[obj.DefIndex];
            if (!IsTable(def)) continue;
            if (!env.GetObjectRuntime(oi).Alive) continue;

            var center = new Vec2(obj.X, obj.Y);
            float cursorSq = (center - mouseWorld).LengthSq();
            if (cursorSq > bestCursorSq) continue;
            float unitSq = (center - unitPos).LengthSq();
            if (unitSq > interactSq) continue;

            bestCursorSq = cursorSq;
            best = oi;
        }
        return best;
    }

    /// <summary>
    /// Find the closest table within ClickRange world units of the mouse — used
    /// for left-click-to-reopen (no necromancer-range gate). Returns -1 if none.
    /// </summary>
    public static int FindTableUnderCursor(EnvironmentSystem env, Vec2 mouseWorld, float clickRange = 2.0f)
    {
        int best = -1;
        float bestSq = clickRange * clickRange;
        for (int oi = 0; oi < env.ObjectCount; oi++)
        {
            var obj = env.GetObject(oi);
            var def = env.Defs[obj.DefIndex];
            if (!IsTable(def)) continue;
            if (!env.GetObjectRuntime(oi).Alive) continue;
            var center = new Vec2(obj.X, obj.Y);
            float sq = (center - mouseWorld).LengthSq();
            if (sq < bestSq) { bestSq = sq; best = oi; }
        }
        return best;
    }

    /// <summary>
    /// Load `corpse` data into the table's first empty corpse slot. Returns the
    /// slot index on success, or -1 if the table is full / not a table.
    ///
    /// Caller is responsible for removing the corpse from the sim afterward —
    /// this function only writes the slot. Splitting these concerns lets a future
    /// "table-stored corpse can be unloaded back to ground" feature reuse this.
    /// </summary>
    public static int LoadCorpseIntoTable(EnvironmentSystem env, int envIdx, Corpse corpse)
    {
        if (envIdx < 0 || envIdx >= env.ObjectCount) return -1;
        var def = env.Defs[env.GetObject(envIdx).DefIndex];
        if (!IsTable(def)) return -1;

        var state = env.GetTableState(envIdx);
        state.EnsureSized(def.CorpseSlots, def.ItemSlots); // defensive; AddObject already does this
        int slot = state.FindEmptyCorpseSlot();
        if (slot < 0) return -1;

        state.CorpseSlots[slot] = new TableCorpseSlot
        {
            Occupied = true,
            SourceUnitDefID = corpse.UnitDefID,
            FacingAngle = corpse.FacingAngle,
            SpriteScale = corpse.SpriteScale,
        };
        return slot;
    }

    /// <summary>Add an item id to the first empty item slot. Returns slot index or -1.</summary>
    public static int LoadItemIntoTable(EnvironmentSystem env, int envIdx, string itemId)
    {
        if (envIdx < 0 || envIdx >= env.ObjectCount || string.IsNullOrEmpty(itemId)) return -1;
        var def = env.Defs[env.GetObject(envIdx).DefIndex];
        if (!IsTable(def)) return -1;

        var state = env.GetTableState(envIdx);
        state.EnsureSized(def.CorpseSlots, def.ItemSlots);
        int slot = state.FindEmptyItemSlot();
        if (slot < 0) return -1;

        state.ItemSlots[slot] = new TableItemSlot { Occupied = true, ItemID = itemId };
        return slot;
    }

    /// <summary>Remove the item from the given item slot. Returns the previous item id, or "" if empty.</summary>
    public static string UnloadItemFromTable(EnvironmentSystem env, int envIdx, int itemSlotIdx)
    {
        if (envIdx < 0 || envIdx >= env.ObjectCount) return "";
        var state = env.GetTableState(envIdx);
        if (itemSlotIdx < 0 || itemSlotIdx >= state.ItemSlots.Length) return "";
        string prev = state.ItemSlots[itemSlotIdx].ItemID;
        state.ItemSlots[itemSlotIdx] = default;
        return prev;
    }

    /// <summary>
    /// Get the world-space position where the body bag (and spawned zombie) sit
    /// for this table — the def's spawn offset added to the table center. Used
    /// by both rendering and crafting (for spawn placement / channel position).
    /// </summary>
    public static Vec2 GetSpawnPos(EnvironmentSystem env, int envIdx)
    {
        var obj = env.GetObject(envIdx);
        var def = env.Defs[obj.DefIndex];
        return new Vec2(obj.X + def.SpawnOffsetX, obj.Y + def.SpawnOffsetY);
    }
}
