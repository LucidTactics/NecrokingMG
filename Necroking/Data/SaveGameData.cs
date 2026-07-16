using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Necroking.Data;

/// <summary>JSON shape of a save-game file (saves/{name}.json). Captures the
/// play-session essentials: which map, where the player is, which form they
/// wear, their active buffs, the spellbar layout, the player's inventory, and
/// the skill-book progression (v2). Loading runs the normal StartGame(mapName)
/// flow and then applies this on top. Deliberately NOT covered yet (future
/// extensions): mana/NecromancerState, horde units, and world/corpse state.</summary>
public class SaveGameData
{
    [JsonPropertyName("version")] public int Version { get; set; } = 2;
    [JsonPropertyName("mapName")] public string MapName { get; set; } = "default";
    [JsonPropertyName("savedAtUtc")] public string SavedAtUtc { get; set; } = "";
    [JsonPropertyName("player")] public SavedPlayer Player { get; set; } = new();
    /// <summary>Flat 10-entry array of SpellIDs, one per spellbar slot ("" = empty).</summary>
    [JsonPropertyName("spellBar")] public List<string> SpellBar { get; set; } = new();
    /// <summary>Skill-book progression (learned skills, point pools, event
    /// tallies, unlocks). v1 saves lack this key and load with a fresh book.</summary>
    [JsonPropertyName("skillBook")] public SavedSkillBook SkillBook { get; set; } = new();
}

/// <summary>Snapshot of SkillBookState (+ its PlayerEventTracker tallies).
/// Mirrors the book's own collections verbatim — restoring fills them back and
/// replays only the effects that can't round-trip any other way (grant_path;
/// see SkillBookState.ApplySave). The necromancer's skill-granted buffs travel
/// in SavedPlayer.Buffs like every other buff.</summary>
public class SavedSkillBook
{
    [JsonPropertyName("learned")] public List<string> Learned { get; set; } = new();
    [JsonPropertyName("skillPoints")] public Dictionary<string, int> SkillPoints { get; set; } = new();
    /// <summary>Cumulative milestone tallies (monster_kill, cast_spell, …).</summary>
    [JsonPropertyName("events")] public Dictionary<string, int> Events { get; set; } = new();
    [JsonPropertyName("unlockedPotions")] public List<string> UnlockedPotions { get; set; } = new();
    [JsonPropertyName("unlockedBuildings")] public List<string> UnlockedBuildings { get; set; } = new();
    [JsonPropertyName("passiveFlags")] public List<string> PassiveFlags { get; set; } = new();
    [JsonPropertyName("intrinsicBuffs")] public List<SavedIntrinsicBuff> IntrinsicBuffs { get; set; } = new();
    [JsonPropertyName("unlockedSummons")] public List<string> UnlockedSummons { get; set; } = new();
    [JsonPropertyName("unlockedAI")] public Dictionary<string, int> UnlockedAI { get; set; } = new();
    [JsonPropertyName("potionSlots")] public int PotionSlots { get; set; }
    [JsonPropertyName("corpseEatingBonus")] public int CorpseEatingBonus { get; set; }
    [JsonPropertyName("soulConsumptionBonus")] public int SoulConsumptionBonus { get; set; }
}

public class SavedIntrinsicBuff
{
    [JsonPropertyName("buffId")] public string BuffId { get; set; } = "";
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = new();
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
    public int Version = -1;
    public string Name = "";
    public string MapName = "";
    public DateTime SavedAt;
    public string FilePath = "";
    // Preview-card data (see GameRenderer.DrawSavePreviewCard).
    public string FormId = "";
    public List<string> SpellBar = new();
    public List<SavedInventorySlot> Inventory { get; set; } = new();
}
