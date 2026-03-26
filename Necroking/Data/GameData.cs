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
        return ok;
    }
}
