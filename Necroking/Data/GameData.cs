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
        ok &= Weather.Load(Core.GamePaths.SeededUserFile(
            Core.GamePaths.UserWeatherJson, Path.Combine(dataDir, "weather.json")));
        ok &= UnitGroups.Load(Path.Combine(dataDir, "unit_groups.json"));

        // Settings: per-machine user copy (gitignored 'user settings/'), seeded from
        // the shipped data/settings.json default on first run; runtime writes go to the
        // user copy only, so data/settings.json stops churning in git. (Weather above
        // and the spell bar do the same — all per-machine, never shared.)
        ok &= Settings.Load(Core.GamePaths.SeededUserFile(
            Core.GamePaths.UserSettingsJson, Path.Combine(dataDir, "settings.json")));

        Items.Load(Path.Combine(dataDir, "items.json")); // optional, don't fail if missing
        Potions.Load(Path.Combine(dataDir, "potions.json")); // optional, don't fail if missing
        GeneratePotionItems();
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

    private void GeneratePotionItems()
    {
        foreach (var pid in Potions.GetIDs())
        {
            var p = Potions.Get(pid);
            if (p == null || string.IsNullOrEmpty(p.ItemID)) continue;
            if (Items.Get(p.ItemID) != null) continue;
            Items.Add(new ItemDef()
            {
                Id = p.ItemID,
                DisplayName = p.DisplayName,
                Icon = p.Icon,
                Description = p.Description,
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
        ok &= UnitGroups.Save(Path.Combine(dataDir, "unit_groups.json"));

        // Per-machine user files: settings + weather presets save to the gitignored
        // 'user settings/' (never written back to data/).
        string userDir = Core.GamePaths.Resolve(Core.GamePaths.UserSettingsDir);
        Directory.CreateDirectory(userDir);
        ok &= Settings.Save(Path.Combine(userDir, "settings.json"));
        ok &= Weather.Save(Path.Combine(userDir, "weather.json"));

        ok &= Items.Save(Path.Combine(dataDir, "items.json"));
        ok &= Potions.Save(Path.Combine(dataDir, "potions.json"));
        ok &= Corpse.Save(Path.Combine(dataDir, "corpse.json"));
        ok &= Units.SaveWeaponPoints(Path.Combine(dataDir, "weapon_points.json"));
        return ok;
    }
}
