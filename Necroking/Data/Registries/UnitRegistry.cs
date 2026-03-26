using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Necroking.Data.Registries;

public class SpriteRef
{
    [JsonPropertyName("atlas")] public string AtlasName { get; set; } = "";
    [JsonPropertyName("name")] public string SpriteName { get; set; } = "";
}

public class UnitStatsJson
{
    [JsonPropertyName("maxHP")] public int MaxHP { get; set; } = 10;
    [JsonPropertyName("strength")] public int Strength { get; set; } = 10;
    [JsonPropertyName("attack")] public int Attack { get; set; } = 10;
    [JsonPropertyName("defense")] public int Defense { get; set; } = 10;
    [JsonPropertyName("magicResist")] public int MagicResist { get; set; } = 10;
    [JsonPropertyName("encumbrance")] public int Encumbrance { get; set; }
    [JsonPropertyName("naturalProt")] public int NaturalProt { get; set; }
    [JsonPropertyName("combatSpeed")] public float CombatSpeed { get; set; } = 8.0f;
}

public class UnitDef : IHasId
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("type")] public string UnitType { get; set; } = "Dynamic";
    [JsonPropertyName("faction")] public string Faction { get; set; } = "Undead";
    [JsonPropertyName("ai")] public string AI { get; set; } = "AttackClosest";
    [JsonPropertyName("orcaPriority")] public int OrcaPriority { get; set; }
    [JsonPropertyName("size")] public int Size { get; set; } = 2;
    [JsonPropertyName("radius")] public float Radius { get; set; } = 0.495f;
    [JsonPropertyName("spriteScale")] public float SpriteScale { get; set; } = 1.0f;
    [JsonPropertyName("spriteWorldHeight")] public float SpriteWorldHeight { get; set; } = 1.8f;
    [JsonPropertyName("color")] public ColorJson? Color { get; set; }
    [JsonPropertyName("sprite")] public SpriteRef? Sprite { get; set; }
    [JsonPropertyName("stats")] public UnitStatsJson? Stats { get; set; }
    [JsonPropertyName("zombieTypeID")] public string ZombieTypeID { get; set; } = "";
    [JsonPropertyName("spellID")] public string SpellID { get; set; } = "";
    [JsonPropertyName("maxMana")] public float MaxMana { get; set; }
    [JsonPropertyName("manaRegen")] public float ManaRegen { get; set; }
    [JsonPropertyName("weapons")] public List<string> Weapons { get; set; } = new();
    [JsonPropertyName("armors")] public List<string> Armors { get; set; } = new();
    [JsonPropertyName("shields")] public List<string> Shields { get; set; } = new();
}

public class UnitRegistry : RegistryBase<UnitDef>
{
    protected override string RootKey => "units";

    public int CountUnitsWithWeapon(string weaponID)
    {
        int count = 0;
        foreach (var def in _defs.Values)
            if (def.Weapons.Contains(weaponID)) count++;
        return count;
    }

    public int CountUnitsWithArmor(string armorID)
    {
        int count = 0;
        foreach (var def in _defs.Values)
            if (def.Armors.Contains(armorID)) count++;
        return count;
    }

    public int CountUnitsWithShield(string shieldID)
    {
        int count = 0;
        foreach (var def in _defs.Values)
            if (def.Shields.Contains(shieldID)) count++;
        return count;
    }

    public void RemoveWeaponFromAll(string weaponID)
    {
        foreach (var def in _defs.Values)
            def.Weapons.Remove(weaponID);
    }

    public void RemoveArmorFromAll(string armorID)
    {
        foreach (var def in _defs.Values)
            def.Armors.Remove(armorID);
    }

    public void RemoveShieldFromAll(string shieldID)
    {
        foreach (var def in _defs.Values)
            def.Shields.Remove(shieldID);
    }

    /// <summary>
    /// Resolve a unit's equipment into final combat stats.
    /// Looks up weapons, armor, shields from registries and aggregates.
    /// </summary>
    public UnitStats BuildStats(string id, WeaponRegistry weapons, ArmorRegistry armors, ShieldRegistry shields)
    {
        var def = Get(id);
        if (def == null) return new UnitStats();

        var stats = def.Stats ?? new UnitStatsJson();
        var s = new UnitStats
        {
            MaxHP = stats.MaxHP,
            HP = stats.MaxHP,
            Strength = stats.Strength,
            Attack = stats.Attack,
            Defense = stats.Defense,
            MagicResist = stats.MagicResist,
            Encumbrance = stats.Encumbrance,
            NaturalProt = stats.NaturalProt,
            CombatSpeed = stats.CombatSpeed
        };

        // Resolve weapons
        foreach (var wid in def.Weapons)
        {
            var w = weapons.Get(wid);
            if (w == null) continue;

            var ws = new WeaponStats
            {
                Damage = w.Damage,
                AttackBonus = w.AttackBonus,
                DefenseBonus = w.DefenseBonus,
                Length = w.Length,
                Name = w.DisplayName
            };

            if (w.IsRanged)
            {
                s.RangedWeapons.Add(ws);
                s.RangedRange.Add(w.Range);
                s.RangedDirectRange.Add(w.DirectRange);
                s.RangedCooldownTime.Add(w.Cooldown);
                s.RangedDmg.Add(w.RangedDamage);
            }
            else
            {
                s.MeleeWeapons.Add(ws);
            }

            foreach (var b in w.Bonuses)
            {
                if (b == "ArmorPiercing") s.HasArmorPiercing = true;
                if (b == "ArmorNegating") s.HasArmorNegating = true;
            }
        }

        // Backward compat: populate primary weapon from first melee
        if (s.MeleeWeapons.Count > 0)
        {
            s.Damage = s.MeleeWeapons[0].Damage;
            s.Length = s.MeleeWeapons[0].Length;
        }

        // Resolve armor
        foreach (var aid in def.Armors)
        {
            var a = armors.Get(aid);
            if (a == null) continue;
            s.Armor.BodyProtection += a.BodyProtection;
            s.Armor.HeadProtection += a.HeadProtection;

            foreach (var b in a.Bonuses)
            {
                if (b == "TrueArmor") s.HasTrueArmor = true;
                if (b == "Barbed") s.HasBarbed = true;
            }
        }

        // Resolve shields — take best
        foreach (var sid in def.Shields)
        {
            var sh = shields.Get(sid);
            if (sh == null) continue;
            if (sh.Parry > s.ShieldParry)
            {
                s.ShieldProtection = sh.Protection;
                s.ShieldParry = sh.Parry;
                s.ShieldDefense = sh.Defense;
            }
        }

        return s;
    }
}
