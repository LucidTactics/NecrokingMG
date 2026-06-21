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

    // --- Consumable: grant skill points when clicked in the inventory ---
    // Empty pool or zero amount = not a skill-point consumable. Pool matches a
    // SkillBookState skill-point pool (e.g. "potions", "monstrology").
    [EditorField(Label = "Skill Point Pool", Order = 6)]
    [EditorCombo("", "potions", "monstrology")]
    public string SkillPointPool { get; set; } = "";

    [EditorField(Label = "Skill Point Amount", Order = 7)]
    public int SkillPointAmount { get; set; }
}

public class ItemRegistry : RegistryBase<ItemDef>
{
    protected override string RootKey => "items";
}
