using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.GameSystems;

namespace Necroking.UI;

/// <summary>
/// The full character/unit sheet (imported Unity "Unit Tooltip2" panel) wired
/// to live unit data. Two entry points:
///   'U' — character sheet for the player necromancer (whatever its current form)
///   'O' — inspect the unit under the cursor (Game1 auto-pauses while open)
/// Same widget instance either way; all dynamic content goes through
/// RuntimeWidgetRenderer overrides (SetText / SetHidden), the portrait child is
/// hidden and the unit's idle atlas sprite is drawn over its rect instead.
/// </summary>
public class UnitInfoPanel : IModalLayer
{
    private const string WidgetId = "UnitTooltipWindow";
    private const string InstanceId = "unitinfo";
    private const int PanelW = 468;
    private const int PanelH = 745;

    private RuntimeWidgetRenderer _renderer = null!;
    private GameData? _gameData;

    private int _unitIndex = -1;
    private int _panelX, _panelY;

    public bool IsVisible { get; private set; }
    public int UnitIndex => _unitIndex;

    /// <summary>Draws a unit def's idle atlas sprite into a screen rect
    /// (Game1.DrawUnitIdleSprite). Null = portrait area stays parchment.</summary>
    public Action<string, Rectangle>? DrawUnitIconCallback;

    /// <summary>Fired whenever the panel closes (any path: toggle, ESC,
    /// unit death). Game1 uses this to restore auto-pause state.</summary>
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
        if (!IsVisible)
        {
            IsVisible = true;
            Game1.Popups.Push(this);
        }
    }

    public void Hide()
    {
        if (!IsVisible) return;
        IsVisible = false;
        _unitIndex = -1;
        Game1.Popups.Pop(this);
        OnClosed?.Invoke();
    }

    // === IModalLayer ===
    public bool ContainsMouse(int mx, int my)
        => IsVisible && mx >= _panelX && mx < _panelX + PanelW && my >= _panelY && my < _panelY + PanelH;

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

        _panelX = screenW - PanelW - 12;
        _panelY = Math.Max(8, (screenH - PanelH) / 2);

        Populate(sim.Units[_unitIndex]);
        _renderer.DrawWidget(WidgetId, _panelX, _panelY, InstanceId);

        var unit = sim.Units[_unitIndex];
        if (DrawUnitIconCallback != null && !string.IsNullOrEmpty(unit.UnitDefID))
        {
            var rect = _renderer.GetChildRect(WidgetId, "ud_portrait", _panelX, _panelY);
            if (rect != Rectangle.Empty)
                DrawUnitIconCallback(unit.UnitDefID, rect);
        }
    }

    // ───────────────────────── data binding ─────────────────────────

    private void Populate(Movement.Unit unit)
    {
        var r = _renderer;
        r.ClearOverrides(InstanceId);

        var def = _gameData?.Units.Get(unit.UnitDefID);
        string name = !string.IsNullOrEmpty(def?.DisplayName) ? def!.DisplayName
                    : !string.IsNullOrEmpty(unit.UnitDefID) ? unit.UnitDefID
                    : unit.Type.ToString();
        r.SetText(InstanceId, "ud_title", name);
        r.SetText(InstanceId, "ud_title_drop", name);

        var s = unit.Stats;
        string faction = def?.Faction ?? unit.Faction.ToString();
        r.SetText(InstanceId, "ud_desc",
            $"{faction} · Size {def?.Size ?? 2}\n{DescribeLoadout(s)}");

        // The unit's idle sprite is drawn over this rect by the panel owner.
        r.SetHidden(InstanceId, "ud_portrait", true);

        // ── Stats grid: relabel the imported cells to our stat system.
        // Stats with no linked field yet show '-' (user preference: always
        // show the full grid rather than hiding unimplemented cells). ──
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

        // Abilities box: no ability system wired yet — blank the sample icon.
        r.SetHidden(InstanceId, "ab_r0_icon", true);
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
        r.SetText(InstanceId, cell + "_label", label);
        r.SetText(InstanceId, cell + "_value", value);
    }

    /// <summary>Equipment rows. The imported rows carry fixed per-row stat
    /// icons, so content is assigned by row archetype: r0/r5 weapons
    /// (dmg/atk/len/def), r1 shield (prot/parry/enc/def), r2 helm and
    /// r3 body armor (prot/cov/enc/def), r4 boots (unused), r6 spare.</summary>
    private void PopulateEquipment(RuntimeWidgetRenderer r, UnitStats s)
    {
        var weapons = new List<WeaponStats>();
        weapons.AddRange(s.MeleeWeapons);
        weapons.AddRange(s.RangedWeapons);

        if (weapons.Count > 0)
            SetWeaponRow(r, "eq_r0", weapons[0]);
        else
            HideEqRow(r, "eq_r0");

        if (s.ShieldProtection > 0 || s.ShieldParry > 0 || s.ShieldDefense > 0)
        {
            r.SetText(InstanceId, "eq_r1_name", "Shield");
            r.SetText(InstanceId, "eq_r1v0", s.ShieldProtection.ToString());
            r.SetText(InstanceId, "eq_r1v1", s.ShieldParry.ToString());
            r.SetText(InstanceId, "eq_r1v2", "-");
            r.SetText(InstanceId, "eq_r1v3", s.ShieldDefense.ToString());
        }
        else HideEqRow(r, "eq_r1");

        if (s.Armor.HeadProtection > 0)
        {
            r.SetText(InstanceId, "eq_r2_name", "Helmet");
            r.SetText(InstanceId, "eq_r2v0", s.Armor.HeadProtection.ToString());
            r.SetText(InstanceId, "eq_r2v1", "-");
            r.SetText(InstanceId, "eq_r2v2", "-");
            r.SetText(InstanceId, "eq_r2v3", "-");
        }
        else HideEqRow(r, "eq_r2");

        if (s.Armor.BodyProtection > 0)
        {
            r.SetText(InstanceId, "eq_r3_name", "Body Armor");
            r.SetText(InstanceId, "eq_r3v0", s.Armor.BodyProtection.ToString());
            r.SetText(InstanceId, "eq_r3v1", "-");
            r.SetText(InstanceId, "eq_r3v2", "-");
            r.SetText(InstanceId, "eq_r3v3", "-");
        }
        else HideEqRow(r, "eq_r3");

        HideEqRow(r, "eq_r4");

        if (weapons.Count > 1)
            SetWeaponRow(r, "eq_r5", weapons[1]);
        else
            HideEqRow(r, "eq_r5");

        HideEqRow(r, "eq_r6");
    }

    private void SetWeaponRow(RuntimeWidgetRenderer r, string row, WeaponStats w)
    {
        r.SetText(InstanceId, row + "_name", string.IsNullOrEmpty(w.Name) ? "Weapon" : w.Name);
        r.SetText(InstanceId, row + "v0", w.Damage.ToString());
        r.SetText(InstanceId, row + "v1", FormatBonus(w.AttackBonus));
        r.SetText(InstanceId, row + "v2", w.Length.ToString());
        r.SetText(InstanceId, row + "v3", FormatBonus(w.DefenseBonus));
    }

    private void HideEqRow(RuntimeWidgetRenderer r, string row)
    {
        r.SetText(InstanceId, row + "_name", "");
        r.SetHidden(InstanceId, row + "_icon", true);
        for (int i = 0; i < 4; i++)
        {
            r.SetText(InstanceId, $"{row}v{i}", "");
            r.SetHidden(InstanceId, $"{row}s{i}", true);
        }
    }

    /// <summary>Attack rows: effective values per weapon — damage includes
    /// Strength for melee (Dominions style), attack skill includes the weapon
    /// bonus; the last column (fatigue icon) shows unit encumbrance.</summary>
    private void PopulateAttacks(RuntimeWidgetRenderer r, UnitStats s)
    {
        var attacks = new List<(WeaponStats W, bool Ranged)>();
        foreach (var w in s.MeleeWeapons) attacks.Add((w, false));
        foreach (var w in s.RangedWeapons) attacks.Add((w, true));

        for (int i = 0; i < 3; i++)
        {
            string row = $"at_r{i}";
            if (i < attacks.Count)
            {
                var (w, ranged) = attacks[i];
                int dmg = w.Damage + (ranged ? 0 : s.Strength);
                r.SetText(InstanceId, row + "_name", string.IsNullOrEmpty(w.Name) ? "Attack" : w.Name);
                r.SetText(InstanceId, row + "v0", dmg.ToString());
                r.SetText(InstanceId, row + "v1", (s.Attack + w.AttackBonus).ToString());
                r.SetText(InstanceId, row + "v2", w.Length.ToString());
                r.SetText(InstanceId, row + "v3", s.Encumbrance.ToString());
            }
            else
            {
                r.SetText(InstanceId, row + "_name", "");
                r.SetHidden(InstanceId, row + "_icon", true);
                for (int v = 0; v < 4; v++)
                {
                    r.SetText(InstanceId, $"{row}v{v}", "");
                    r.SetHidden(InstanceId, $"{row}s{v}", true);
                }
            }
        }
    }

    private static string FormatBonus(int v) => v > 0 ? "+" + v : v.ToString();
}
