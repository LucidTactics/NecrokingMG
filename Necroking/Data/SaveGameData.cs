using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Necroking.Data;

/// <summary>JSON shape of a save-game file (saves/{name}.json). Initial version
/// captures the play-session essentials: which map, where the player is,
/// which form they wear, their active buffs, the spellbar layout, and the
/// player's inventory. Loading runs the normal StartGame(mapName) flow and then
/// applies this on top. Deliberately NOT covered yet (future extensions):
/// mana/NecromancerState, SkillBookState (incl. the "morphed:&lt;id&gt;"
/// passive), horde units, and world/corpse state.</summary>
public class SaveGameData
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("mapName")] public string MapName { get; set; } = "default";
    [JsonPropertyName("savedAtUtc")] public string SavedAtUtc { get; set; } = "";
    [JsonPropertyName("player")] public SavedPlayer Player { get; set; } = new();
    /// <summary>Flat 10-entry array of SpellIDs, one per spellbar slot ("" = empty).</summary>
    [JsonPropertyName("spellBar")] public List<string> SpellBar { get; set; } = new();
}

public class SavedPlayer
{
    [JsonPropertyName("x")] public float X { get; set; }
    [JsonPropertyName("y")] public float Y { get; set; }
    [JsonPropertyName("facing")] public float Facing { get; set; } = 90f;
    /// <summary>The player Unit's UnitDefID (a PlayerForm UnitDef id, e.g. "wretched").</summary>
    [JsonPropertyName("formId")] public string FormId { get; set; } = "";
    // Buffs live on the owning entity, not at the save root — other things
    // (horde units, world objects) will carry their own buff lists later.
    [JsonPropertyName("buffs")] public List<SavedBuff> Buffs { get; set; } = new();
    /// <summary>Non-empty inventory slots. Each carries its slot index so the
    /// exact grid layout (gaps included) round-trips; empty slots are omitted.</summary>
    [JsonPropertyName("inventory")] public List<SavedInventorySlot> Inventory { get; set; } = new();
}

public class SavedInventorySlot
{
    [JsonPropertyName("slot")] public int Slot { get; set; }
    [JsonPropertyName("itemId")] public string ItemId { get; set; } = "";
    [JsonPropertyName("qty")] public int Quantity { get; set; } = 1;
}

public class SavedBuff
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("remaining")] public float Remaining { get; set; }
    [JsonPropertyName("permanent")] public bool Permanent { get; set; }
    [JsonPropertyName("stacks")] public int Stacks { get; set; } = 1;
}

/// <summary>Lightweight entry for save-list UIs (save dialog, load menu) —
/// derived from the files on disk, never serialized itself.</summary>
public class SaveGameInfo
{
    public string Name = "";
    public string MapName = "";
    public DateTime SavedAt;
    public string FilePath = "";
    // Preview-card data (see GameRenderer.DrawSavePreviewCard).
    public string FormId = "";
    public List<string> SpellBar = new();
}
