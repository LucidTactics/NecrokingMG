using System.Collections.Generic;
using System.Text.Json.Serialization;
using Necroking.Editor;

namespace Necroking.Data.Registries;

public class RecipeIngredient
{
    [JsonPropertyName("itemId")] public string ItemId { get; set; } = "";
    [JsonPropertyName("amount")] public int Amount { get; set; } = 1;
}

public class PotionDef : IHasId
{
    [EditorHide] [JsonPropertyName("id")] public string Id { get; set; } = "";
    [EditorHide] [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
    [EditorHide] [JsonPropertyName("icon")] public string Icon { get; set; } = "";
    [EditorHide] [JsonPropertyName("description")] public string Description { get; set; } = "";

    // Targeting
    [EditorField(Label = "Target Type", Order = 0)]
    [EditorCombo("Friendly", "Enemy", "Any", "FriendlyOrCorpse")]
    [JsonPropertyName("targetType")] public string TargetType { get; set; } = "Enemy";

    [EditorField(Label = "Throw Range", Order = 1, Step = 0.5f)]
    [JsonPropertyName("throwRange")] public float ThrowRange { get; set; } = 5f;

    [EditorField(Label = "Proj. Scale", Order = 2)]
    [JsonPropertyName("projectileScale")] public float ProjectileScale { get; set; } = 0.5f;

    // Effect
    [EditorField(Label = "Buff ID", Order = 3)]
    [JsonPropertyName("buffID")] public string BuffID { get; set; } = "";

    [EditorField(Label = "Buff Duration", Order = 4, Step = 0.05f)]
    [JsonPropertyName("buffDuration")] public float BuffDuration { get; set; }

    [EditorField(Label = "On Hit Effect", Order = 5)]
    [EditorCombo("", "Frenzy", "Paralysis", "Zombie", "Poison")]
    [JsonPropertyName("onHitEffect")] public string OnHitEffect { get; set; } = "";

    [EditorField(Label = "Hits Corpses", Order = 6)]
    [JsonPropertyName("hitsCorpses")] public bool HitsCorpses { get; set; }

    // Visuals (custom rendering)
    [EditorHide] [JsonPropertyName("projectileFlipbook")] public FlipbookRef? ProjectileFlipbook { get; set; }
    [EditorHide] [JsonPropertyName("hitEffectFlipbook")] public FlipbookRef? HitEffectFlipbook { get; set; }

    // Recipe (custom rendering)
    [EditorHide] [JsonPropertyName("recipe")] public List<RecipeIngredient> Recipe { get; set; } = new();

    [EditorField(Label = "Craft Time", Order = 7, Step = 0.1f)]
    [JsonPropertyName("craftTime")] public float CraftTime { get; set; } = 1.0f;

    // Linked inventory item (internal linkage)
    [EditorHide] [JsonPropertyName("itemID")] public string ItemID { get; set; } = "";
}

public class PotionRegistry : RegistryBase<PotionDef>
{
    protected override string RootKey => "potions";
}
