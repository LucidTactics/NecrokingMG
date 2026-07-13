using System.Collections.Generic;
using System.Text;
using Necroking.Data;
using Necroking.Data.Registries;

namespace Necroking.Editor;

/// <summary>
/// At-a-glance tooltip text for registry defs, shown when hovering entries in
/// the editors' content-picker dropdowns (units, spells, weapons, ...). One
/// canonical builder per def type so every editor shows the same summary;
/// lines are joined with '\n' (the dropdown tooltip path splits on it).
/// Builders accept null and return null (no tooltip) so callers can pass
/// Get(id) results straight through. Plain ASCII only — the sprite font has
/// no em dash / unicode glyphs.
/// </summary>
public static class DefTips
{
    /// <summary>Dispatch by the registry name used by
    /// [EditorRegistryDropdown] / ReflectionPropertyRenderer.</summary>
    public static string? ForRegistryEntry(GameData data, string registryName, string id)
    {
        return registryName switch
        {
            "Buffs" => ForBuff(data.Buffs.Get(id)),
            "Units" => ForUnit(data.Units.Get(id)),
            "Flipbooks" => ForFlipbook(data.Flipbooks.Get(id)),
            "Weapons" => ForWeapon(data.Weapons.Get(id)),
            "Armors" => ForArmor(data.Armors.Get(id)),
            "Shields" => ForShield(data.Shields.Get(id)),
            "Items" => ForItem(data.Items.Get(id)),
            "Spells" => ForSpell(data.Spells.Get(id)),
            _ => null,
        };
    }

    public static string? ForUnit(UnitDef? d)
    {
        if (d == null) return null;
        var sb = new StringBuilder();
        // Archetype (the live AI system) beats the legacy AI field when set.
        string brain = !string.IsNullOrEmpty(d.Archetype) ? d.Archetype : d.AI;
        sb.Append(d.Faction).Append(" | ").Append(brain).Append(" | Size ").Append(d.Size);
        if (d.Stats != null)
        {
            var s = d.Stats;
            sb.Append('\n').Append("HP ").Append(s.MaxHP)
              .Append("  Str ").Append(s.Strength)
              .Append("  Atk ").Append(s.Attack)
              .Append("  Def ").Append(s.Defense)
              .Append("  MR ").Append(s.MagicResist);
        }
        if (d.Tags.Count > 0)
            sb.Append('\n').Append("Tags: ").Append(string.Join(", ", d.Tags));
        if (d.UndeadCategory != UndeadCategory.None)
            sb.Append('\n').Append("Undead cap pool: ").Append(d.UndeadCategory);
        if (!string.IsNullOrEmpty(d.SpellID))
            sb.Append('\n').Append("Casts: ").Append(d.SpellID);
        return sb.ToString();
    }

    public static string? ForSpell(SpellDef? d)
    {
        if (d == null) return null;
        var sb = new StringBuilder();
        sb.Append(d.Category);
        if (!string.IsNullOrEmpty(d.School)) sb.Append(" | ").Append(d.School);
        sb.Append('\n').Append("Mana ").Append(F(d.ManaCost))
          .Append("  CD ").Append(F(d.Cooldown)).Append('s')
          .Append("  Cast ").Append(F(d.CastTime)).Append('s');
        if (d.Range > 0) sb.Append("  Range ").Append(F(d.Range));
        if (d.Damage > 0) sb.Append('\n').Append("Damage ").Append(d.Damage);
        return sb.ToString();
    }

    public static string? ForWeapon(WeaponDef? d)
    {
        if (d == null) return null;
        var sb = new StringBuilder();
        sb.Append("Dmg ").Append(d.Damage);
        if (!string.IsNullOrEmpty(d.DamageType)) sb.Append(' ').Append(d.DamageType);
        sb.Append("  Reach ").Append(d.Length);
        if (d.IsRanged) sb.Append("  Ranged ").Append(d.RangedDamage).Append(" @ ").Append(F(d.Range));
        sb.Append('\n');
        if (d.AttackBonus != 0) sb.Append("Atk ").Append(Signed(d.AttackBonus)).Append("  ");
        if (d.DefenseBonus != 0) sb.Append("Def ").Append(Signed(d.DefenseBonus)).Append("  ");
        if (d.CooldownRounds > 0) sb.Append("Every ").Append(d.CooldownRounds).Append(" rounds");
        else sb.Append("CD ").Append(F(d.Cooldown)).Append('s');
        if (d.TwoHanded == true) sb.Append("  Two-handed");
        if (!string.IsNullOrEmpty(d.Archetype) && d.Archetype != "None")
            sb.Append('\n').Append("Archetype: ").Append(d.Archetype);
        if (d.Bonuses.Count > 0)
            sb.Append('\n').Append("Bonuses: ").Append(string.Join(", ", d.Bonuses));
        return sb.ToString();
    }

    public static string? ForArmor(ArmorDef? d)
    {
        if (d == null) return null;
        var sb = new StringBuilder();
        sb.Append("Body ").Append(d.BodyProtection)
          .Append("  Head ").Append(d.HeadProtection)
          .Append("  Enc ").Append(d.Encumbrance);
        if (d.Bonuses.Count > 0)
            sb.Append('\n').Append("Bonuses: ").Append(string.Join(", ", d.Bonuses));
        return sb.ToString();
    }

    public static string? ForShield(ShieldDef? d)
    {
        if (d == null) return null;
        return $"Prot {d.Protection}  Parry {d.Parry}  Def {d.Defense}";
    }

    public static string? ForBuff(BuffDef? d)
    {
        if (d == null) return null;
        var sb = new StringBuilder();
        sb.Append("Duration ").Append(F(d.Duration)).Append('s');
        if (d.MaxStacks > 1) sb.Append("  stacks to ").Append(d.MaxStacks);
        if (d.Intrinsic) sb.Append("  (intrinsic)");
        int shown = 0;
        foreach (var e in d.Effects)
        {
            if (shown == 3) { sb.Append('\n').Append("+ ").Append(d.Effects.Count - shown).Append(" more"); break; }
            sb.Append('\n').Append(EffectLine(e));
            shown++;
        }
        if (d.GrantedWeapons.Count > 0)
            sb.Append('\n').Append("Grants: ").Append(string.Join(", ", d.GrantedWeapons));
        return sb.ToString();
    }

    private static string EffectLine(BuffEffect e) => e.Type switch
    {
        "Set" => $"Set {e.Stat} = {F(e.Value)}",
        "Multiply" => $"{e.Stat} x{F(e.Value)}",
        _ => $"{e.Stat} {Signed(e.Value)}",
    };

    public static string? ForItem(ItemDef? d)
    {
        if (d == null) return null;
        var sb = new StringBuilder();
        sb.Append(string.IsNullOrEmpty(d.Category) ? "item" : d.Category)
          .Append("  stack ").Append(d.MaxStack);
        if (!string.IsNullOrEmpty(d.Description))
            sb.Append('\n').Append(Truncate(d.Description, 52));
        return sb.ToString();
    }

    public static string? ForFlipbook(FlipbookDef? d)
    {
        if (d == null) return null;
        return $"{d.Cols}x{d.Rows} frames @ {F(d.DefaultFPS)} fps";
    }

    public static string? ForUnitGroup(UnitGroupDef? d)
    {
        if (d == null) return null;
        var sb = new StringBuilder();
        sb.Append("Group of ").Append(d.Entries.Count);
        int shown = 0;
        foreach (var e in d.Entries)
        {
            if (shown == 4) { sb.Append('\n').Append("+ ").Append(d.Entries.Count - shown).Append(" more"); break; }
            sb.Append('\n').Append(e.UnitDefID).Append("  w ").Append(F(e.Weight));
            shown++;
        }
        return sb.ToString();
    }

    public static string? ForWeatherPreset(WeatherPresetDef? d)
    {
        if (d == null) return null;
        var fx = d.Effects;
        var parts = new List<string>();
        if (fx.RainDensity > 0) parts.Add("rain " + F(fx.RainDensity));
        if (fx.FogDensity > 0) parts.Add("fog " + F(fx.FogDensity));
        if (fx.LightningEnabled) parts.Add("lightning");
        if (fx.WindStrength > 0) parts.Add("wind " + F(fx.WindStrength));
        return parts.Count > 0 ? string.Join(", ", parts) : "clear (no rain/fog/lightning)";
    }

    // Compact float: trims trailing zeros (1.50 -> 1.5, 2.00 -> 2).
    private static string F(float v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    private static string Signed(float v) => (v >= 0 ? "+" : "") + F(v);

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..(max - 3)] + "...";
}
