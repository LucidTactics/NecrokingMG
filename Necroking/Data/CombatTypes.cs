using System.Collections.Generic;

namespace Necroking.Data;

public class WeaponStats
{
    public int Damage { get; set; }
    public int AttackBonus { get; set; }
    public int DefenseBonus { get; set; }
    public int Length { get; set; } = 1;
    public string Name { get; set; } = "";
}

public class ArmorStats
{
    public int BodyProtection { get; set; }
    public int HeadProtection { get; set; }
}

public class RangedStats
{
    public float Range { get; set; }
    public float DirectRange { get; set; }
    public float Cooldown { get; set; }
    public int Damage { get; set; }
    public int Precision { get; set; }
}

public class UnitStats
{
    public int MaxHP { get; set; } = 10;
    public int HP { get; set; } = 10;
    public int Strength { get; set; } = 10;
    public int Attack { get; set; } = 10;
    public int Defense { get; set; } = 10;
    public int MagicResist { get; set; } = 10;
    public int Encumbrance { get; set; }
    public int NaturalProt { get; set; }
    public float CombatSpeed { get; set; } = 8.0f;

    // Primary weapon (backward compat — populated from first melee weapon)
    public int Damage { get; set; }
    public int Length { get; set; } = 1;

    // Multi-weapon support
    public List<WeaponStats> MeleeWeapons { get; set; } = new();
    public List<WeaponStats> RangedWeapons { get; set; } = new();
    public ArmorStats Armor { get; set; } = new();

    // Shield
    public int ShieldProtection { get; set; }
    public int ShieldParry { get; set; }
    public int ShieldDefense { get; set; }

    // Bonus flags
    public bool HasArmorPiercing { get; set; }
    public bool HasArmorNegating { get; set; }
    public bool HasTrueArmor { get; set; }
    public bool HasBarbed { get; set; }

    public List<float> RangedRange { get; set; } = new();
    public List<float> RangedDirectRange { get; set; } = new();
    public List<float> RangedCooldownTime { get; set; } = new();
    public List<int> RangedDmg { get; set; } = new();
}
