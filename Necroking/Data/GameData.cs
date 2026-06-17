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
    public PotionRegistry Potions { get; } = new();
    public CorpseSettings Corpse { get; } = new();

    public bool Load()
    {
        string dataDir = Core.GamePaths.Resolve("data");
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

        // Settings: load the per-machine user copy ('user settings/', gitignored),
        // seeding it from the shipped default (data/settings.json) on first run so a
        // fresh clone still gets sensible defaults. Runtime writes go to the user copy
        // only — data/settings.json is never written, so it stops churning in git.
        string userSettings = Core.GamePaths.Resolve(Core.GamePaths.UserSettingsJson);
        string defaultSettings = Path.Combine(dataDir, "settings.json");
        if (!File.Exists(userSettings) && File.Exists(defaultSettings))
        {
            Directory.CreateDirectory(Core.GamePaths.Resolve(Core.GamePaths.UserSettingsDir));
            File.Copy(defaultSettings, userSettings);
        }
        ok &= Settings.Load(File.Exists(userSettings) ? userSettings : defaultSettings);

        Items.Load(Path.Combine(dataDir, "items.json")); // optional, don't fail if missing
        Potions.Load(Path.Combine(dataDir, "potions.json")); // optional, don't fail if missing
        GeneratePotionSpells(); // surface each potion as a Construction spell
        Corpse.Load(Path.Combine(dataDir, "corpse.json")); // optional, falls back to spritemeta pivots
        // Load weapon_points.json (must be after units.json so UnitDefs exist)
        ok &= Units.LoadWeaponPoints(Path.Combine(dataDir, "weapon_points.json"));
        // Wading defaults override the code-level WadingDefaults constants if
        // the file is present. Falls back silently to the code defaults when
        // missing (acceptable for fresh setups / scenarios).
        WadingDefaultsFile.Load(Path.Combine(dataDir, "wading_defaults.json"));
        return ok;
    }

    /// <summary>Surface every potion as a Construction-school spell (id == potion
    /// id) so potions live in the grimoire and the spell bar like any other spell.
    /// Casting is intercepted in Game1 and routed to the existing PotionSystem
    /// (throw / drink + inventory consume); ConsumesItem drives the bar charge
    /// count. Idempotent — skips ids that already exist as spells.</summary>
    private void GeneratePotionSpells()
    {
        foreach (var pid in Potions.GetIDs())
        {
            var p = Potions.Get(pid);
            if (p == null || string.IsNullOrEmpty(p.ItemID) || Spells.Get(pid) != null) continue;
            Spells.Add(new SpellDef
            {
                Id = pid,
                DisplayName = string.IsNullOrEmpty(p.DisplayName) ? pid : p.DisplayName,
                Icon = p.Icon,
                School = "Construction",
                Category = "Buff",
                TileTemplate = "buff",
                ConsumesItem = p.ItemID,
                ManaCost = 0f,
                PrimaryPath = "",
                PrimaryLevel = 0,
                Range = p.ThrowRange < 1f ? 1f : p.ThrowRange,
            });
        }
    }

    public bool Save()
    {
        string dataDir = Core.GamePaths.Resolve("data");
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

        // Settings: always save to the per-machine 'user settings/' (gitignored)
        string userDir = Core.GamePaths.Resolve(Core.GamePaths.UserSettingsDir);
        Directory.CreateDirectory(userDir);
        ok &= Settings.Save(Path.Combine(userDir, "settings.json"));

        ok &= Items.Save(Path.Combine(dataDir, "items.json"));
        ok &= Potions.Save(Path.Combine(dataDir, "potions.json"));
        ok &= Corpse.Save(Path.Combine(dataDir, "corpse.json"));
        ok &= Units.SaveWeaponPoints(Path.Combine(dataDir, "weapon_points.json"));
        return ok;
    }
}
