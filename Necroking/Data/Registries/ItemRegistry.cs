using Necroking.Editor;

namespace Necroking.Data.Registries;

public class ItemDef : IHasId
{
    [EditorField(Label = "ID", Order = 0)]
    public string Id { get; set; } = "";

    [EditorField(Label = "Display Name", Order = 1)]
    public string DisplayName { get; set; } = "";

    [EditorField(Label = "Icon", Order = 2)]
    public string Icon { get; set; } = "";

    [EditorField(Label = "Max Stack", Order = 3)]
    public int MaxStack { get; set; } = 99;

    [EditorField(Label = "Category", Order = 4)]
    [EditorCombo("material", "potion", "consumable", "equipment")]
    public string Category { get; set; } = "";

    [EditorField(Label = "Description", Order = 5)]
    public string Description { get; set; } = "";
}

public class ItemRegistry : RegistryBase<ItemDef>
{
    protected override string RootKey => "items";
}
