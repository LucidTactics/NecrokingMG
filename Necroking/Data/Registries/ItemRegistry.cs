namespace Necroking.Data.Registries;

public class ItemDef : IHasId
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Icon { get; set; } = "";          // texture path, e.g. "assets/Environment/Mushrooms/Deathcap.png"
    public int MaxStack { get; set; } = 99;
    public string Category { get; set; } = "";       // "material", "consumable", "equipment", etc.
    public string Description { get; set; } = "";
}

public class ItemRegistry : RegistryBase<ItemDef>
{
    protected override string RootKey => "items";
}
