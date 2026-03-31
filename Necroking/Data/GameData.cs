using System.IO;
using Necroking.Data.Registries;

namespace Necroking.Data;

public class GameData
{
    public WeaponRegistry Weapons { get; } = new();
    public ArmorRegistry Armors { get; } = new();
    public ShieldRegistry Shields { get; } = new();
    public UnitRegistry Units { get; } = new();
    public FlipbookRegistry Flipbooks { get; } = new();
    public BuffRegistry Buffs { get; } = new();
    public SpellRegistry Spells { get; } = new();
    public WeatherRegistry Weather { get; } = new();
    public UnitGroupRegistry UnitGroups { get; } = new();
    public GameSettingsData Settings { get; } = new();
    public ItemRegistry Items { get; } = new();

    public bool Load(string dataDir = "data")
    {
        bool ok = true;
        ok &= Weapons.Load(Path.Combine(dataDir, "weapons.json"));
        ok &= Armors.Load(Path.Combine(dataDir, "armor.json"));
        ok &= Shields.Load(Path.Combine(dataDir, "shields.json"));
        ok &= Units.Load(Path.Combine(dataDir, "units.json"));
        ok &= Flipbooks.Load(Path.Combine(dataDir, "flipbooks.json"));
        ok &= Buffs.Load(Path.Combine(dataDir, "buffs.json"));
        ok &= Spells.Load(Path.Combine(dataDir, "spells.json"));
        ok &= Weather.Load(Path.Combine(dataDir, "weather.json"));
        ok &= UnitGroups.Load(Path.Combine(dataDir, "unit_groups.json"));
        ok &= Settings.Load(Path.Combine(dataDir, "settings.json"));
        Items.Load(Path.Combine(dataDir, "items.json")); // optional, don't fail if missing
        // Load weapon_points.json (must be after units.json so UnitDefs exist)
        ok &= Units.LoadWeaponPoints(Path.Combine(dataDir, "weapon_points.json"));
        return ok;
    }

    public bool Save(string dataDir = "data")
    {
        bool ok = true;
        ok &= Weapons.Save(Path.Combine(dataDir, "weapons.json"));
        ok &= Armors.Save(Path.Combine(dataDir, "armor.json"));
        ok &= Shields.Save(Path.Combine(dataDir, "shields.json"));
        ok &= Units.Save(Path.Combine(dataDir, "units.json"));
        ok &= Flipbooks.Save(Path.Combine(dataDir, "flipbooks.json"));
        ok &= Buffs.Save(Path.Combine(dataDir, "buffs.json"));
        ok &= Spells.Save(Path.Combine(dataDir, "spells.json"));
        ok &= Weather.Save(Path.Combine(dataDir, "weather.json"));
        ok &= UnitGroups.Save(Path.Combine(dataDir, "unit_groups.json"));
        ok &= Settings.Save(Path.Combine(dataDir, "settings.json"));
        ok &= Items.Save(Path.Combine(dataDir, "items.json"));
        ok &= Units.SaveWeaponPoints(Path.Combine(dataDir, "weapon_points.json"));

        // Also save to source tree so dotnet publish picks up the latest
        string srcDir = Path.Combine("..", dataDir);
        if (Directory.Exists(srcDir))
        {
            Weapons.Save(Path.Combine(srcDir, "weapons.json"));
            Armors.Save(Path.Combine(srcDir, "armor.json"));
            Shields.Save(Path.Combine(srcDir, "shields.json"));
            Units.Save(Path.Combine(srcDir, "units.json"));
            Flipbooks.Save(Path.Combine(srcDir, "flipbooks.json"));
            Buffs.Save(Path.Combine(srcDir, "buffs.json"));
            Spells.Save(Path.Combine(srcDir, "spells.json"));
            Weather.Save(Path.Combine(srcDir, "weather.json"));
            UnitGroups.Save(Path.Combine(srcDir, "unit_groups.json"));
            Settings.Save(Path.Combine(srcDir, "settings.json"));
            Items.Save(Path.Combine(srcDir, "items.json"));
            Units.SaveWeaponPoints(Path.Combine(srcDir, "weapon_points.json"));
        }
        return ok;
    }
}
