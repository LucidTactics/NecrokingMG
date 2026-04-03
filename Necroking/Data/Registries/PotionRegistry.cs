using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Necroking.Data.Registries;

public class RecipeIngredient
{
    [JsonPropertyName("itemId")] public string ItemId { get; set; } = "";
    [JsonPropertyName("amount")] public int Amount { get; set; } = 1;
}

public class PotionDef : IHasId
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("icon")] public string Icon { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";

    // Targeting
    [JsonPropertyName("targetType")] public string TargetType { get; set; } = "Enemy"; // "Friendly", "Enemy", "Any", "FriendlyOrCorpse"
    [JsonPropertyName("throwRange")] public float ThrowRange { get; set; } = 5f;
    [JsonPropertyName("projectileScale")] public float ProjectileScale { get; set; } = 0.5f;

    // Effect
    [JsonPropertyName("buffID")] public string BuffID { get; set; } = "";
    [JsonPropertyName("buffDuration")] public float BuffDuration { get; set; }
    [JsonPropertyName("onHitEffect")] public string OnHitEffect { get; set; } = ""; // "Frenzy", "Paralysis", "Zombie", "Poison"
    [JsonPropertyName("hitsCorpses")] public bool HitsCorpses { get; set; } // projectile can collide with corpses

    // Visuals
    [JsonPropertyName("projectileFlipbook")] public FlipbookRef? ProjectileFlipbook { get; set; }
    [JsonPropertyName("hitEffectFlipbook")] public FlipbookRef? HitEffectFlipbook { get; set; }

    // Recipe
    [JsonPropertyName("recipe")] public List<RecipeIngredient> Recipe { get; set; } = new();
    [JsonPropertyName("craftTime")] public float CraftTime { get; set; } = 1.0f;

    // Linked inventory item
    [JsonPropertyName("itemID")] public string ItemID { get; set; } = "";
}

public class PotionRegistry : RegistryBase<PotionDef>
{
    protected override string RootKey => "potions";
}
