using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Necroking.Data;
using Necroking.GameSystems;

namespace Necroking.UI;

/// <summary>
/// The unit/character sheet, on the auto-size UnitSheetDyn widget: equipment
/// and attack rows are bound sequentially and collapse when absent (min one
/// row per section), with zebra striping re-applied by visible index.
/// 'U' = player necromancer (current form), 'O' = inspect under cursor.
/// The static UnitTooltipWindow widget remains as the import reference.
/// </summary>
public class UnitInfoPanel : IModalLayer
{
    private const string WidgetId = "UnitSheetDyn";
    private const string InstanceId = "unitinfo";
    private const int PanelW = 468;
    private const int EqRows = 7, AtRows = 3;

    // Sub-instance ids (root child indices: desc/stats/eq/at/ab)
    private const string Desc = InstanceId + ".0";
    private const string Stats = InstanceId + ".1";
    private const string EqBox = InstanceId + ".2.1";
    private const string AtBox = InstanceId + ".3.1";
    private const string AbSec = InstanceId + ".4";

    private const string IcoSword = "assets/UI/Icons/Equipment/Sword_24.png";
    private const string IcoShield = "assets/UI/Icons/Equipment/Shield_24.png";
    private const string IcoHelmet = "assets/UI/Icons/Equipment/Helmet_24.png";
    private const string IcoChest = "assets/UI/Icons/Equipment/ChestArmor_24.png";
    private const string IcoSlash = "assets/UI/Icons/DamageTypes/Slash_24.png";
    private const string IcoPierce = "assets/UI/Icons/DamageTypes/Pierce_24.png";
    private const string IcoProt = "assets/UI/Icons/NewIcons/Prot24.png";
    private const string IcoParry = "assets/UI/Icons/NewIcons/Parry24.png";
    private const string IcoCov = "assets/UI/Icons/NewIcons/Coverage24.png";

    private RuntimeWidgetRenderer _renderer = null!;
    private GameData? _gameData;
    private int _unitIndex = -1;
    private int _panelX, _panelY, _panelH;

    public bool IsVisible { get; private set; }
    public int UnitIndex => _unitIndex;
    public Action<string, Rectangle>? DrawUnitIconCallback;
    public Action? OnClosed;

    public void Init(RuntimeWidgetRenderer renderer, GameData gameData)
    {
        _renderer = renderer;
        _gameData = gameData;
    }

    public void ShowForUnit(int unitIndex)
    {
        if (IsVisible && _unitIndex == unitIndex) { Hide(); return; }
        _unitIndex = unitIndex;
        if (!IsVisible) { IsVisible = true; Game1.Popups.Push(this); }
    }

    public void Hide()
    {
        if (!IsVisible) return;
        IsVisible = false;
        _unitIndex = -1;
        Game1.Popups.Pop(this);
        OnClosed?.Invoke();
    }

    public bool ContainsMouse(int mx, int my)
        => IsVisible && mx >= _panelX && mx < _panelX + PanelW && my >= _panelY && my < _panelY + _panelH;

    public void OnCancel() => Hide();
    public bool LightDismiss => false;
    public bool IsBlocking => false;

    public void Draw(int screenW, int screenH, Simulation sim)
    {
        if (!IsVisible) return;
        if (_unitIndex < 0 || _unitIndex >= sim.Units.Count || !sim.Units[_unitIndex].Alive)
        {
            Hide();
            return;
        }

        Populate(sim.Units[_unitIndex]);
        _panelH = _renderer.MeasureWidgetHeight(WidgetId, InstanceId);
        _panelX = screenW - PanelW - 12;
        _panelY = Math.Max(8, (screenH - _panelH) / 2);
        _renderer.DrawWidget(WidgetId, _panelX, _panelY, InstanceId);

        var unit = sim.Units[_unitIndex];
        if (DrawUnitIconCallback != null && !string.IsNullOrEmpty(unit.UnitDefID))
        {
            var sec = _renderer.GetChildRect(WidgetId, "sec_desc", _panelX, _panelY, InstanceId);
            // No instance id here: the child is hidden in the Desc instance
            // (we draw the live sprite instead), so ask for the STATIC rect.
            var rect = _renderer.GetChildRect("UTD_DescSection", "ud_portrait", sec.X, sec.Y);
            if (rect != Rectangle.Empty)
                DrawUnitIconCallback(unit.UnitDefID, rect);
        }

        DrawStatTooltips(screenW, screenH, unit);
    }

    // ───────────────────────── stat hover tooltips ─────────────────────────

    private static readonly (string Cell, string Key)[] CellKeys =
    {
        ("st_r0c0", "hp"), ("st_r0c1", "magicres"), ("st_r0c2", "morale"),
        ("st_r1c0", "size"), ("st_r1c1", "toughness"), ("st_r1c2", "magicpower"),
        ("st_r2c0", "strength"), ("st_r2c1", "protection"), ("st_r2c2", "shield"),
        ("st_r3c0", "attack"), ("st_r3c1", "defense"), ("st_r3c2", "parry"),
        ("st_r4c0", "speed"), ("st_r4c1", "encumbrance"), ("st_r4c2", "upkeep"),
    };

    private readonly StatTooltips _tips = new();

    /// <summary>Test hook: scenarios can't move the OS cursor on a hidden
    /// window, so they inject a cursor position here.</summary>
    public Point? DebugMouseOverride;

    private void DrawStatTooltips(int screenW, int screenH, Movement.Unit unit)
    {
        var ms = Microsoft.Xna.Framework.Input.Mouse.GetState();
        int mx = DebugMouseOverride?.X ?? ms.X, my = DebugMouseOverride?.Y ?? ms.Y;
        var sec = _renderer.GetChildRect(WidgetId, "sec_stats", _panelX, _panelY, InstanceId);

        string? key = null;
        bool isValue = false;
        if (sec != Rectangle.Empty)
        {
            foreach (var (cell, k) in CellKeys)
            {
                var lab = _renderer.GetChildRect("UTD_StatsSection", cell + "_label", sec.X, sec.Y);
                var ico = _renderer.GetChildRect("UTD_StatsSection", cell + "_icon", sec.X, sec.Y);
                var val = _renderer.GetChildRect("UTD_StatsSection", cell + "_value", sec.X, sec.Y);
                if (val.Contains(mx, my)) { key = k; isValue = true; break; }
                if (lab.Contains(mx, my) || ico.Contains(mx, my)) { key = k; break; }
            }
        }
        _tips.Update(key, isValue, 1f / 60f);
        _tips.Draw(_renderer, mx, my, screenW, screenH, k => BuildBreakdown(k, unit));
    }

    /// <summary>Tabulation rows for a stat: Base (default color), itemized
    /// contributions (green/red), Final colored vs base (yellow if equal).</summary>
    private (List<ResourceTooltip.Row> Rows, string Final, Color FinalColor)? BuildBreakdown(
        string key, Movement.Unit unit)
    {
        var gd = _gameData;
        var s = unit.Stats;
        var b = gd != null && !string.IsNullOrEmpty(unit.UnitDefID)
            ? gd.Units.BuildStats(unit.UnitDefID, gd.Weapons, gd.Armors, gd.Shields) : s;

        var rows = new List<ResourceTooltip.Row>();
        void Generic(float baseV, float finalV, string fmt = "0.#")
        {
            rows.Add(new ResourceTooltip.Row("Base", baseV.ToString(fmt), ResourceTooltip.ValueDefault));
            float d = finalV - baseV;
            if (Math.Abs(d) > 0.001f) rows.Add(ResourceTooltip.Entry("Effects", (int)Math.Round(d)));
        }

        float bv, fv;
        switch (key)
        {
            case "hp":
                rows.Add(new ResourceTooltip.Row("Max Hp", s.MaxHP.ToString(), ResourceTooltip.ValueDefault));
                if (s.HP < s.MaxHP)
                    rows.Add(new ResourceTooltip.Row("Damage", (s.HP - s.MaxHP).ToString(), ResourceTooltip.ValueRed));
                bv = s.MaxHP; fv = s.HP;
                break;
            case "protection":
                rows.Add(new ResourceTooltip.Row("Natural", s.NaturalProt.ToString(), ResourceTooltip.ValueDefault));
                if (s.Armor.BodyProtection != 0)
                    rows.Add(ResourceTooltip.Entry("Body Armor", s.Armor.BodyProtection));
                bv = b.NaturalProt + b.Armor.BodyProtection; fv = s.NaturalProt + s.Armor.BodyProtection;
                break;
            case "shield": Generic(b.ShieldProtection, fv = s.ShieldProtection); bv = b.ShieldProtection; break;
            case "parry": Generic(b.ShieldParry, fv = s.ShieldParry); bv = b.ShieldParry; break;
            case "magicres": Generic(b.MagicResist, fv = s.MagicResist); bv = b.MagicResist; break;
            case "morale": Generic(b.Morale, fv = s.Morale); bv = b.Morale; break;
            case "strength": Generic(b.Strength, fv = s.Strength); bv = b.Strength; break;
            case "attack": Generic(b.Attack, fv = s.Attack); bv = b.Attack; break;
            case "defense": Generic(b.Defense, fv = s.Defense); bv = b.Defense; break;
            case "speed": Generic(b.CombatSpeed, fv = s.CombatSpeed); bv = b.CombatSpeed; break;
            case "encumbrance": Generic(b.Encumbrance, fv = s.Encumbrance); bv = b.Encumbrance; break;
            case "size":
                bv = fv = _gameData?.Units.Get(unit.UnitDefID)?.Size ?? 2;
                rows.Add(new ResourceTooltip.Row("Base", fv.ToString("0"), ResourceTooltip.ValueDefault));
                break;
            default:
                return null; // TBD stats have no tabulation
        }

        // The header value IS the final (colored vs base) — no redundant row.
        var col = fv > bv ? ResourceTooltip.ValueGreen
                : fv < bv ? ResourceTooltip.ValueRed : ResourceTooltip.ValueDefault;
        return (rows, fv.ToString("0.#"), col);
    }

    private void Populate(Movement.Unit unit)
    {
        var r = _renderer;
        r.ClearOverridesRecursive(InstanceId);

        var def = _gameData?.Units.Get(unit.UnitDefID);
        string name = !string.IsNullOrEmpty(def?.DisplayName) ? def!.DisplayName
                    : !string.IsNullOrEmpty(unit.UnitDefID) ? unit.UnitDefID : unit.Type.ToString();
        r.SetText(Desc, "ud_title", name);
        r.SetText(Desc, "ud_title_drop", name);

        var s = unit.Stats;
        string faction = def?.Faction ?? unit.Faction.ToString();
        r.SetText(Desc, "ud_desc", $"{faction} · Size {def?.Size ?? 2}\n{DescribeLoadout(s)}");
        r.SetHidden(Desc, "ud_portrait", true);

        SetCell(r, "st_r0c0", "Hp", $"{s.HP}/{s.MaxHP}");
        SetCell(r, "st_r0c1", "Magic Res", s.MagicResist.ToString());
        SetCell(r, "st_r0c2", "Morale", s.Morale.ToString());
        SetCell(r, "st_r1c0", "Size", (def?.Size ?? 2).ToString());
        SetCell(r, "st_r1c1", "Toughness", "-");
        SetCell(r, "st_r1c2", "Magic Power", "-");
        SetCell(r, "st_r2c0", "Strength", s.Strength.ToString());
        SetCell(r, "st_r2c1", "Protection", (s.NaturalProt + s.Armor.BodyProtection).ToString());
        SetCell(r, "st_r2c2", "Shield", s.ShieldProtection.ToString());
        SetCell(r, "st_r3c0", "Attack", s.Attack.ToString());
        SetCell(r, "st_r3c1", "Defense", s.Defense.ToString());
        SetCell(r, "st_r3c2", "Parry", s.ShieldParry.ToString());
        SetCell(r, "st_r4c0", "Speed", s.CombatSpeed.ToString("0.#"));
        SetCell(r, "st_r4c1", "Encumbrance", s.Encumbrance.ToString());
        SetCell(r, "st_r4c2", "Upkeep", "-");

        PopulateEquipment(r, s);
        PopulateAttacks(r, s);
        r.SetHidden(AbSec, "ab_r0_icon", true);
    }

    private static string DmgTypeIcon(WeaponStats w)
        => w.DamageType.ToString() == "Piercing" ? IcoPierce : IcoSlash;

    /// <summary>One equipment row: weapon rows show dmg/atk/len/def (Len as
    /// text); armor rows show prot/cov-or-parry/enc/def (enc icon via s2b).</summary>
    private void EqRow(int i, string icon, string name2, string v0, string s0,
        string v1, string? s1, string v2, bool lenText, string v3)
    {
        var r = _renderer;
        string row = $"{EqBox}.{i}";
        r.SetHidden(row, "swatch", i % 2 != 0); // zebra by VISIBLE index
        r.SetImage(row, "icon", icon);
        r.SetText(row, "name", name2);
        r.SetText(row, "v0", v0);
        r.SetImage(row, "s0", s0);
        r.SetText(row, "v1", v1);
        if (s1 != null) r.SetImage(row, "s1", s1);
        r.SetText(row, "v2", v2);
        r.SetHidden(row, "s2", !lenText);   // 'Len' text element
        r.SetHidden(row, "s2b", lenText);   // Enc icon alternative
        r.SetText(row, "v3", v3);
    }

    private void PopulateEquipment(RuntimeWidgetRenderer r, UnitStats s)
    {
        var weapons = new List<WeaponStats>();
        weapons.AddRange(s.MeleeWeapons);
        weapons.AddRange(s.RangedWeapons);

        int i = 0;
        foreach (var w in weapons)
        {
            if (i >= EqRows) break;
            EqRow(i++, IcoSword, string.IsNullOrEmpty(w.Name) ? "Weapon" : w.Name,
                w.Damage.ToString(), DmgTypeIcon(w),
                FormatBonus(w.AttackBonus), null, w.Length.ToString(), true, FormatBonus(w.DefenseBonus));
        }
        if (i < EqRows && (s.ShieldProtection > 0 || s.ShieldParry > 0 || s.ShieldDefense > 0))
            EqRow(i++, IcoShield, "Shield", s.ShieldProtection.ToString(), IcoProt,
                s.ShieldParry.ToString(), IcoParry, "-", false, s.ShieldDefense.ToString());
        if (i < EqRows && s.Armor.HeadProtection > 0)
            EqRow(i++, IcoHelmet, "Helmet", s.Armor.HeadProtection.ToString(), IcoProt,
                "-", IcoCov, "-", false, "-");
        if (i < EqRows && s.Armor.BodyProtection > 0)
            EqRow(i++, IcoChest, "Body Armor", s.Armor.BodyProtection.ToString(), IcoProt,
                "-", IcoCov, "-", false, "-");
        if (i == 0) // min one row
            EqRow(i++, IcoSword, "(Nothing)", "-", IcoSlash, "-", null, "-", true, "-");
        for (; i < EqRows; i++)
            r.SetHidden(EqBox, $"row{i}", true);
    }

    private void PopulateAttacks(RuntimeWidgetRenderer r, UnitStats s)
    {
        var attacks = new List<(WeaponStats W, bool Ranged)>();
        foreach (var w in s.MeleeWeapons) attacks.Add((w, false));
        foreach (var w in s.RangedWeapons) attacks.Add((w, true));

        int i = 0;
        foreach (var (w, ranged) in attacks)
        {
            if (i >= AtRows) break;
            string row = $"{AtBox}.{i}";
            r.SetHidden(row, "swatch", i % 2 != 0);
            r.SetText(row, "name", string.IsNullOrEmpty(w.Name) ? "Attack" : w.Name);
            r.SetText(row, "v0", (w.Damage + (ranged ? 0 : s.Strength)).ToString());
            r.SetImage(row, "s0", DmgTypeIcon(w));
            r.SetText(row, "v1", (s.Attack + w.AttackBonus).ToString());
            r.SetText(row, "v2", w.Length.ToString());
            r.SetText(row, "v3", s.Encumbrance.ToString());
            i++;
        }
        if (i == 0)
        {
            string row = $"{AtBox}.0";
            r.SetText(row, "name", "(None)");
            foreach (var v in new[] { "v0", "v1", "v2", "v3" }) r.SetText(row, v, "-");
            i = 1;
        }
        for (; i < AtRows; i++)
            r.SetHidden(AtBox, $"row{i}", true);
    }

    private static string DescribeLoadout(UnitStats s)
    {
        var parts = new List<string>();
        foreach (var w in s.MeleeWeapons)
            if (!string.IsNullOrEmpty(w.Name)) parts.Add(w.Name);
        foreach (var w in s.RangedWeapons)
            if (!string.IsNullOrEmpty(w.Name)) parts.Add(w.Name);
        if (s.ShieldProtection > 0 || s.ShieldParry > 0) parts.Add("Shield");
        if (s.Armor.BodyProtection > 0) parts.Add("Armor");
        return parts.Count > 0 ? string.Join(", ", parts) : "Unarmed";
    }

    private static void SetCell(RuntimeWidgetRenderer r, string cell, string label, string value)
    {
        r.SetText(Stats, cell + "_label", label);
        r.SetText(Stats, cell + "_value", value);
    }

    private static string FormatBonus(int v) => v > 0 ? "+" + v : v.ToString();
}
