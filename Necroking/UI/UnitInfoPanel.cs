using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.GameSystems;

namespace Necroking.UI;

/// <summary>
/// The unit/character sheet, on the auto-size UnitSheetDyn widget: equipment
/// and attack rows are bound sequentially and collapse when absent (min one
/// row per section), with zebra striping re-applied by visible index.
/// 'U' = player necromancer (current form), 'L' = inspect under cursor.
/// (The original flat UnitTooltipWindow import was archived to defunct/ui once
/// UnitSheetDyn superseded it — see todos/ui_best_practices_audit.md.)
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

    private const string IcoSword = "assets/UI/Icons/Equipment/Sword_24.png";
    private const string IcoShield = "assets/UI/Icons/Equipment/Shield_24.png";
    private const string IcoHelmet = "assets/UI/Icons/Equipment/Helmet_24.png";
    private const string IcoChest = "assets/UI/Icons/Equipment/ChestArmor_24.png";
    private const string IcoSlash = "assets/UI/Icons/DamageTypes/Slash_24.png";
    private const string IcoPierce = "assets/UI/Icons/DamageTypes/Pierce_24.png";
    private const string IcoProt = "assets/UI/Icons/NewIcons/Prot24.png";
    private const string IcoParry = "assets/UI/Icons/NewIcons/Parry24.png";
    private const string IcoCov = "assets/UI/Icons/NewIcons/Coverage24.png";
    private const string IcoFat = "assets/UI/Icons/SturmIcons/exhausted.2.24.png";

    private RuntimeWidgetRenderer _renderer = null!;
    private GameData _gameData;
    private int _unitIndex = -1;        // resolved from _unitId each Draw — transient
    private uint _unitId = Core.GameConstants.InvalidUnit; // stable identity of the pinned unit
    private int _panelX, _panelY, _panelH;

    public bool IsVisible { get; private set; }
    public int UnitIndex => _unitIndex;
    /// <summary>Stable id of the pinned unit (InvalidUnit when none) — survives the
    /// swap-and-pop reindexing that a raw array index would not.</summary>
    public uint UnitId => _unitId;
    /// <summary>True when the panel is a cursor-driven auto-hover view (not pinned
    /// via 'U'/'L'). Transient views are NOT pushed onto the popup stack, so they
    /// don't claim MouseOverUI — otherwise the hover logic that owns them could
    /// never re-pick a new unit or dismiss when the cursor leaves.</summary>
    public bool IsTransient { get; private set; }
    public Action<string, Rectangle>? DrawUnitIconCallback;
    public Action? OnClosed;

    public void Init(RuntimeWidgetRenderer renderer, GameData gameData)
    {
        _renderer = renderer;
        _gameData = gameData;
    }

    /// <summary>Pin a unit's sheet (manual 'U'/'L' inspect). A pinned sheet is a
    /// visible router layer, so ESC/click routing and MouseOverUI behave like
    /// other panels; a transient view is not (see <see cref="IsTransient"/>).</summary>
    public void ShowForUnit(uint unitId)
    {
        if (IsVisible && _unitId == unitId) { Hide(); return; }
        _unitId = unitId;
        IsTransient = false;
        IsVisible = true;
    }

    /// <summary>Show a unit's sheet as a transient cursor-driven view (auto-hover).
    /// Deliberately not a hit-claiming layer — see <see cref="IsTransient"/>.</summary>
    public void ShowForUnitTransient(uint unitId)
    {
        _unitId = unitId;
        IsVisible = true;
        IsTransient = true;
    }

    public void Hide()
    {
        if (!IsVisible) return;
        IsVisible = false;
        IsTransient = false;
        _unitId = Core.GameConstants.InvalidUnit;
        _unitIndex = -1;
        OnClosed?.Invoke();
    }

    public bool ContainsMouse(int mx, int my)
        => IsVisible && mx >= _panelX && mx < _panelX + PanelW && my >= _panelY && my < _panelY + _panelH;

    /// <summary>Only pinned sheets are on the popup stack, so this is never asked
    /// for a transient view — transient views must not claim MouseOverUI (see
    /// <see cref="IsTransient"/>). Rect fields come from the last Draw.</summary>
    public Microsoft.Xna.Framework.Rectangle? HitBounds(int screenW, int screenH)
        => IsVisible && !IsTransient
            ? new Microsoft.Xna.Framework.Rectangle(_panelX, _panelY, PanelW, _panelH)
            : null;

    public void OnCancel() => Hide();
    public bool LightDismiss => false;
    public bool IsBlocking => false;

    public void Draw(int screenW, int screenH, Simulation sim)
    {
        if (!IsVisible) return;
        // Resolve the stable unit id to a CURRENT array index every frame. sim.Units is
        // swap-and-pop compacted on death, so a stored index would silently rebind to a
        // different unit; ResolveUnitID returns -1 once the pinned unit is gone.
        _unitIndex = sim.ResolveUnitID(_unitId);
        if (_unitIndex < 0 || !sim.Units[_unitIndex].Alive)
        {
            Hide();
            return;
        }

        var unit = sim.Units[_unitIndex];
        Populate(unit);
        ComputeAbilitiesLayout(unit, sim);   // wraps paths+buffs into rows; grows sec_ab
        _panelH = _renderer.MeasureWidgetHeight(WidgetId, InstanceId);
        _panelX = screenW - PanelW - 12;
        _panelY = Math.Max(8, (screenH - _panelH) / 2);
        _renderer.DrawWidget(WidgetId, _panelX, _panelY, InstanceId);

        if (DrawUnitIconCallback != null && !string.IsNullOrEmpty(unit.UnitDefID))
        {
            var sec = _renderer.GetChildRect(WidgetId, "sec_desc", _panelX, _panelY, InstanceId);
            // No instance id here: the child is hidden in the Desc instance
            // (we draw the live sprite instead), so ask for the STATIC rect.
            var rect = _renderer.GetChildRect("UnitDescSection", "ud_portrait", sec.X, sec.Y);
            if (rect != Rectangle.Empty)
                DrawUnitIconCallback(unit.UnitDefID, rect);
        }

        var secAb = _renderer.GetChildRect(WidgetId, "sec_ab", _panelX, _panelY, InstanceId);
        if (secAb != Rectangle.Empty) DrawAbilitiesRow(secAb);

        // Cursor from the frame InputState (NOT raw Mouse.GetState): respects
        // the dev mousepos override and the router's hover masking — the panel's
        // PanelLayer parks MousePos off-screen when another layer owns the
        // cursor, so these tooltips can't pop through a covering panel.
        var inPos = Game1.Instance._input.MousePos;
        int mx = DebugMouseOverride?.X ?? (int)inPos.X, my = DebugMouseOverride?.Y ?? (int)inPos.Y;
        DrawStatTooltips(screenW, screenH, unit, mx, my);
        DrawAbilitiesTooltips(screenW, screenH, unit, sim, mx, my);
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

    /// <summary>Test seam: screen-space centre of a stat cell's label or value
    /// element, using the most recent panel layout. Lets scenarios hover a
    /// specific stat without hard-coding resolution-dependent coordinates.
    /// Returns Point.Zero before the panel has been drawn at least once.</summary>
    public Point DebugCellCenter(string cell, bool value)
    {
        var sec = _renderer.GetChildRect(WidgetId, "sec_stats", _panelX, _panelY, InstanceId);
        if (sec == Rectangle.Empty) return Point.Zero;
        var r = _renderer.GetChildRect("UnitStatsGrid", cell + (value ? "_value" : "_label"), sec.X, sec.Y);
        return r == Rectangle.Empty ? Point.Zero : r.Center;
    }

    /// <summary>Test seams: screen-space centre of the idx-th buff icon / magic
    /// path entry in the Abilities &amp; Buffs row, from the last drawn layout.</summary>
    public Point DebugBuffIconCenter(int idx)
        => idx >= 0 && idx < _buffRects.Count ? _buffRects[idx].Rect.Center : Point.Zero;
    public Point DebugPathEntryCenter(int idx)
        => idx >= 0 && idx < _pathRects.Count ? _pathRects[idx].Rect.Center : Point.Zero;

    private void DrawStatTooltips(int screenW, int screenH, Movement.Unit unit, int mx, int my)
    {
        var sec = _renderer.GetChildRect(WidgetId, "sec_stats", _panelX, _panelY, InstanceId);

        string? key = null;
        bool isValue = false;
        if (sec != Rectangle.Empty)
        {
            foreach (var (cell, k) in CellKeys)
            {
                var lab = _renderer.GetChildRect("UnitStatsGrid", cell + "_label", sec.X, sec.Y);
                var ico = _renderer.GetChildRect("UnitStatsGrid", cell + "_icon", sec.X, sec.Y);
                var val = _renderer.GetChildRect("UnitStatsGrid", cell + "_value", sec.X, sec.Y);
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
                bv = fv = _gameData.Units.Get(unit.UnitDefID)?.Size ?? 2;
                rows.Add(new ResourceTooltip.Row("Base", fv.ToString("0"), ResourceTooltip.ValueDefault));
                break;
            default:
                return null; // TBD stats have no tabulation
        }

        // Fold active-buff modifiers into the breakdown: one row per buff effect
        // on this stat, then recompute the final as the buff-modified value.
        var bstat = KeyToBuffStat(key);
        if (bstat.HasValue)
        {
            string statName = bstat.Value.ToString();
            foreach (var ab in unit.ActiveBuffs)
            {
                var bdef = gd?.Buffs.Get(ab.BuffDefID);
                if (bdef == null) continue;
                foreach (var eff in ab.Effects)
                {
                    if (eff.Stat != statName) continue;
                    string lbl = bdef.DisplayName + (ab.StackCount > 1 ? $" x{ab.StackCount}" : "");
                    switch (eff.Type)
                    {
                        case "Add":
                            float add = eff.Value * ab.StackCount;
                            rows.Add(new ResourceTooltip.Row(lbl, (add > 0 ? "+" : "") + add.ToString("0.#"),
                                add > 0 ? ResourceTooltip.ValueGreen : add < 0 ? ResourceTooltip.ValueRed : ResourceTooltip.ValueDefault));
                            break;
                        case "Multiply":
                            float mul = MathF.Pow(eff.Value, ab.StackCount);
                            rows.Add(new ResourceTooltip.Row(lbl, "x" + mul.ToString("0.##"),
                                mul > 1f ? ResourceTooltip.ValueGreen : mul < 1f ? ResourceTooltip.ValueRed : ResourceTooltip.ValueDefault));
                            break;
                        case "Set":
                            rows.Add(new ResourceTooltip.Row(lbl, "=" + eff.Value.ToString("0.#"), ResourceTooltip.ValueDefault));
                            break;
                    }
                }
            }
            // Final = buff-modified value. Protection's buff hits NaturalProt
            // only, so re-add the armor component already folded into fv.
            if (key == "protection")
                fv = BuffSystem.GetModifiedStat(unit.ActiveBuffs, BuffStat.NaturalProt, s.NaturalProt) + s.Armor.BodyProtection;
            else if (key != "hp") // hp header stays current HP; MaxHp buffs show as rows
                fv = BuffSystem.GetModifiedStat(unit.ActiveBuffs, bstat.Value, fv);
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

        var def = _gameData.Units.Get(unit.UnitDefID);
        string name = !string.IsNullOrEmpty(unit.UnitDefID)
            ? _gameData.Units.NameOf(unit.UnitDefID) : unit.Type.ToString();
        r.SetText(Desc, "ud_title", name);
        r.SetText(Desc, "ud_title_drop", name);

        var s = unit.Stats;
        string faction = def?.Faction ?? unit.Faction.ToString();
        r.SetText(Desc, "ud_desc", $"{faction} · Size {def?.Size ?? 2}\n{DescribeLoadout(s)}");
        r.SetHidden(Desc, "ud_portrait", true);

        // Buff-modified stat values (coloured green/red when an active buff
        // raises/lowers them). The displayed number is the effective value.
        var bf = unit.ActiveBuffs;
        float mHp   = BuffSystem.GetModifiedStat(bf, BuffStat.MaxHP, s.MaxHP);
        float mMr   = BuffSystem.GetModifiedStat(bf, BuffStat.MagicResist, s.MagicResist);
        float mStr  = BuffSystem.GetModifiedStat(bf, BuffStat.Strength, s.Strength);
        float mNat  = BuffSystem.GetModifiedStat(bf, BuffStat.NaturalProt, s.NaturalProt);
        float mAtk  = BuffSystem.GetModifiedStat(bf, BuffStat.Attack, s.Attack);
        float mDef  = BuffSystem.GetModifiedStat(bf, BuffStat.Defense, s.Defense);
        float mSpd  = BuffSystem.GetModifiedStat(bf, BuffStat.CombatSpeed, s.CombatSpeed);
        float mEnc  = BuffSystem.GetModifiedStat(bf, BuffStat.Encumbrance, s.Encumbrance);
        int   I(float v) => (int)MathF.Round(v);
        float baseProt = s.NaturalProt + s.Armor.BodyProtection;
        float modProt  = mNat + s.Armor.BodyProtection;

        SetCell(r, "st_r0c0", "Hp", $"{s.HP}/{I(mHp)}", BuffColor(mHp, s.MaxHP));
        SetCell(r, "st_r0c1", "Magic Res", I(mMr).ToString(), BuffColor(mMr, s.MagicResist));
        SetCell(r, "st_r0c2", "Morale", s.Morale.ToString());
        SetCell(r, "st_r1c0", "Size", (def?.Size ?? 2).ToString());
        SetCell(r, "st_r1c1", "Toughness", "-");
        SetCell(r, "st_r1c2", "Magic Power", "-");
        SetCell(r, "st_r2c0", "Strength", I(mStr).ToString(), BuffColor(mStr, s.Strength));
        SetCell(r, "st_r2c1", "Protection", I(modProt).ToString(), BuffColor(modProt, baseProt));
        SetCell(r, "st_r2c2", "Shield", s.ShieldProtection.ToString());
        SetCell(r, "st_r3c0", "Attack", I(mAtk).ToString(), BuffColor(mAtk, s.Attack));
        SetCell(r, "st_r3c1", "Defense", I(mDef).ToString(), BuffColor(mDef, s.Defense));
        SetCell(r, "st_r3c2", "Parry", s.ShieldParry.ToString());
        SetCell(r, "st_r4c0", "Speed", mSpd.ToString("0.#"), BuffColor(mSpd, s.CombatSpeed));
        SetCell(r, "st_r4c1", "Encumbrance", I(mEnc).ToString(), BuffColor(mEnc, s.Encumbrance));
        SetCell(r, "st_r4c2", "Upkeep", "-");

        PopulateEquipment(r, s);
        PopulateAttacks(r, s);
        // Abilities & Buffs is laid out + drawn in ComputeAbilitiesLayout /
        // DrawAbilitiesRow (it wraps to multiple rows and grows the section).
    }

    // ───────────────────────── abilities & buffs ─────────────────────────

    private const int MaxBuffIcons = 12;
    private const string DefaultBuffIcon = "assets/UI/Icons/Buffs/_default.png";
    private const string BuffTipInst = "bufftip";
    private static readonly Dictionary<string, bool> _fileCache = new();

    private static readonly Color PathNumColor = new(232, 214, 170);
    private const int AbBoxTop = 27;   // box top, relative to the section
    private const int AbRowH = 28;     // height per wrapped icon row
    private const int AbUsableW = 456; // icon area width inside the box

    // One laid-out entry in the Abilities & Buffs row (a magic path or a buff).
    private struct AbEntry
    {
        public bool IsPath;
        public MagicPath Path; public int Eff, Native;     // path
        public BuffDef? Buff; public Movement.ActiveBuff Ab; // buff
        public int Row, RelX, W;
    }
    private readonly List<AbEntry> _abEntries = new();
    private int _abRows = 1;

    // Rects of the magic-path entries / buff icons from the last DrawAbilitiesRow,
    // for hover hit-testing. _abHoverKey + timer drive the hover delay.
    private readonly List<(Rectangle Rect, MagicPath Path)> _pathRects = new();
    private readonly List<(Rectangle Rect, BuffDef Def, Movement.ActiveBuff Ab)> _buffRects = new();
    private string? _abHoverKey;
    private float _abHoverTime;

    /// <summary>Active, non-intrinsic buffs to surface as icons (capped). Same
    /// order DrawAbilitiesRow renders and DrawAbilitiesTooltips hit-tests.</summary>
    private List<(BuffDef Def, Movement.ActiveBuff Ab)> VisibleBuffs(Movement.Unit unit)
    {
        var list = new List<(BuffDef, Movement.ActiveBuff)>();
        foreach (var ab in unit.ActiveBuffs)
        {
            var bdef = _gameData.Buffs.Get(ab.BuffDefID);
            if (bdef == null || bdef.Intrinsic) continue; // intrinsic = permanent passive, no icon
            list.Add((bdef, ab));
            if (list.Count >= MaxBuffIcons) break;
        }
        return list;
    }

    /// <summary>Lay out the Abilities &amp; Buffs row: magic-path entries
    /// ("&lt;pathIcon&gt;&lt;level&gt;") leftmost (native paths first, then buff/item-granted),
    /// then buff icons, wrapping to new rows when the box width is exceeded. Sets
    /// the section's height override so the auto-height panel grows.</summary>
    private void ComputeAbilitiesLayout(Movement.Unit unit, Simulation sim)
    {
        _abEntries.Clear();
        var def = _gameData.Units.Get(unit.UnitDefID);
        int gap = 6, x = 7, row = 0;

        void Place(int w, AbEntry e)
        {
            if (x > 7 && x + w > 7 + AbUsableW) { row++; x = 7; }  // wrap
            e.Row = row; e.RelX = x; e.W = w;
            _abEntries.Add(e);
            x += w + gap;
        }

        // Magic paths: effective level > 0 (native, or floored up by AllPaths
        // buffs / future items). Native paths first so they're never pushed off.
        void AddPath(MagicPath p)
        {
            int native = def?.GetPathLevel(p) ?? 0;
            int eff = BuffSystem.EffectivePathLevel(sim.UnitsMut, _unitIndex, def, p);
            if (eff <= 0) return;
            int numW = (int)MathF.Ceiling(_renderer.MeasureText(eff.ToString(), 16).X);
            Place(26 + numW, new AbEntry { IsPath = true, Path = p, Eff = eff, Native = native });
        }
        foreach (var p in MagicPathHelpers.AllInOrder)
            if ((def?.GetPathLevel(p) ?? 0) > 0) AddPath(p);
        foreach (var p in MagicPathHelpers.AllInOrder)
            if ((def?.GetPathLevel(p) ?? 0) <= 0) AddPath(p);
        if (_abEntries.Count > 0) x += 6; // small separator before buff icons

        foreach (var (bdef, ab) in VisibleBuffs(unit))
            Place(24, new AbEntry { IsPath = false, Buff = bdef, Ab = ab });

        _abRows = row + 1;

        // Grow the section (title + N rows of box) so the panel/frame expand.
        // The box + per-row icons are drawn directly in DrawAbilitiesRow (sized to
        // the row count); the section JSON keeps only the title bar, so there's no
        // static single-row box / placeholder icons left to hide here.
        int boxH = _abRows * AbRowH + 7;
        _renderer.SetChildHeight(InstanceId, "sec_ab", AbBoxTop + boxH + 3);
    }

    /// <summary>Draw the laid-out Abilities &amp; Buffs row(s): a box background
    /// sized to the row count, then each path/buff entry. Records rects for
    /// hover tooltips.</summary>
    private void DrawAbilitiesRow(Rectangle sec)
    {
        _pathRects.Clear();
        _buffRects.Clear();

        // Box background via the same widget elements (harmonized + tinted) so it
        // matches the original swatch exactly, just sized to the row count.
        int boxH = _abRows * AbRowH + 7;
        int boxTop = sec.Y + AbBoxTop;
        _renderer.DrawElementImage("AbilitiesBox", new Rectangle(sec.X + 3, boxTop, 463, boxH));
        _renderer.DrawElementImage("AbilitiesPattern", new Rectangle(sec.X + 7, boxTop + 3, AbUsableW, boxH - 6));

        foreach (var e in _abEntries)
        {
            int ex = sec.X + e.RelX;
            int ey = sec.Y + 33 + e.Row * AbRowH;
            if (e.IsPath)
            {
                _renderer.DrawIcon(MagicPathHelpers.IconPath(e.Path, 24), ex, ey, 24, 24);
                var sz = _renderer.MeasureText(e.Eff.ToString(), 16);
                _renderer.DrawText(e.Eff.ToString(), ex + 26, ey + (24 - (int)sz.Y) / 2, 16,
                    e.Eff > e.Native ? BuffUp : PathNumColor);
                _pathRects.Add((new Rectangle(ex, ey, e.W, 24), e.Path));
            }
            else if (e.Buff != null)
            {
                _renderer.DrawIcon(ResolveBuffIcon(e.Buff), ex, ey, 24, 24);
                _buffRects.Add((new Rectangle(ex, ey, 24, 24), e.Buff, e.Ab));
            }
        }
    }

    /// <summary>Buff icon path: the buff's own icon when present, else its
    /// primary stat-effect icon, else a generic token. (Lets the section work
    /// before the PixelLab art in tools/gen_buff_icons.py has been generated.)</summary>
    private static string ResolveBuffIcon(BuffDef b)
    {
        if (!string.IsNullOrEmpty(b.Icon) && FileExists(b.Icon)) return b.Icon;
        foreach (var eff in b.Effects)
        {
            string? k = StatTipKey(eff.Stat);
            if (k != null) return $"assets/UI/Icons/StatTips/{k}_36.png";
        }
        return DefaultBuffIcon;
    }

    private static bool FileExists(string rel)
    {
        if (_fileCache.TryGetValue(rel, out var e)) return e;
        bool ex = System.IO.File.Exists(Core.GamePaths.Resolve(rel));
        _fileCache[rel] = ex;
        return ex;
    }

    private static string? StatTipKey(string stat) => stat switch
    {
        "Strength" => "strength", "Attack" => "attack", "Defense" => "defense",
        "MagicResist" => "magicres", "NaturalProt" => "protection",
        "CombatSpeed" => "speed", "MaxHP" => "hp", "Encumbrance" => "encumbrance",
        _ => null,
    };

    private static string StatLabel(string stat) => stat switch
    {
        "MagicResist" => "Magic Resist", "NaturalProt" => "Protection",
        "CombatSpeed" => "Speed", "MaxHP" => "Max HP", "MaxMana" => "Max Mana",
        "ManaRegen" => "Mana Regen", "MonsterCap" => "Monster Cap",
        "HumanCap" => "Human Cap", "AllPaths" => "All Paths", _ => stat,
    };

    /// <summary>Hover a path entry or buff icon → rich ResourceTooltipDyn: a
    /// path shows base/buffs/items level breakdown; a buff shows its effects +
    /// duration. Shared hover-delay timer across both.</summary>
    private void DrawAbilitiesTooltips(int screenW, int screenH, Movement.Unit unit, Simulation sim, int mx, int my)
    {
        var def = _gameData.Units.Get(unit.UnitDefID);
        string? key = null;
        Rectangle anchor = Rectangle.Empty;
        Action? bind = null;

        foreach (var (rect, p) in _pathRects)
            if (rect.Contains(mx, my))
            {
                key = "path:" + p; anchor = rect; bind = () => BindPathTooltip(p, def, sim);
                break;
            }
        if (key == null)
            for (int i = 0; i < _buffRects.Count; i++)
                if (_buffRects[i].Rect.Contains(mx, my))
                {
                    var b = _buffRects[i];
                    key = "buff:" + i; anchor = b.Rect; bind = () => BindBuffTooltip(b.Def, b.Ab);
                    break;
                }

        if (key != _abHoverKey) { _abHoverKey = key; _abHoverTime = 0f; }
        else if (key != null) _abHoverTime += 1f / 60f;
        if (key == null || _abHoverTime < 0.35f) return;

        bind!();
        int h = _renderer.MeasureWidgetHeight(ResourceTooltip.WidgetId, BuffTipInst);
        int x = anchor.Center.X + 16, y = anchor.Bottom + 8;
        if (x + 222 > screenW - 4) x = anchor.Center.X - 222 - 8;
        if (y + h > screenH - 4) y = anchor.Y - h - 8;
        _renderer.DrawWidget(ResourceTooltip.WidgetId, Math.Max(4, x), Math.Max(4, y), BuffTipInst);
    }

    /// <summary>Bind the path tooltip: level = base (unit-type native) + buff
    /// bonus (AllPaths floor, e.g. god mode) + item bonus (none yet).</summary>
    private void BindPathTooltip(MagicPath p, UnitDef def, Simulation sim)
    {
        int native = def?.GetPathLevel(p) ?? 0;
        int eff = BuffSystem.EffectivePathLevel(sim.UnitsMut, _unitIndex, def, p);
        var rows = new List<ResourceTooltip.Row>
        {
            new("Base", native.ToString(), ResourceTooltip.ValueDefault),
            ResourceTooltip.Entry("Buffs", eff - native),
            ResourceTooltip.Entry("Items", 0),
        };
        ResourceTooltip.Bind(_renderer, BuffTipInst, p + " Magic", eff.ToString(),
            eff > native ? ResourceTooltip.ValueGreen : ResourceTooltip.ValueDefault, rows, "");
        _renderer.SetImage(BuffTipInst + ".0", "icon", MagicPathHelpers.IconPath(p, 24));
    }

    private void BindBuffTooltip(BuffDef b, Movement.ActiveBuff ab)
    {
        var rows = new List<ResourceTooltip.Row>();
        foreach (var eff in b.Effects)
        {
            string lbl = StatLabel(eff.Stat);
            switch (eff.Type)
            {
                case "Add":
                    float add = eff.Value * ab.StackCount;
                    rows.Add(new ResourceTooltip.Row(lbl, (add > 0 ? "+" : "") + add.ToString("0.#"),
                        add > 0 ? ResourceTooltip.ValueGreen : add < 0 ? ResourceTooltip.ValueRed : ResourceTooltip.ValueDefault));
                    break;
                case "Multiply":
                    float mul = MathF.Pow(eff.Value, ab.StackCount);
                    rows.Add(new ResourceTooltip.Row(lbl, "x" + mul.ToString("0.##"),
                        mul > 1f ? ResourceTooltip.ValueGreen : mul < 1f ? ResourceTooltip.ValueRed : ResourceTooltip.ValueDefault));
                    break;
                case "Set":
                    rows.Add(new ResourceTooltip.Row(lbl, "=" + eff.Value.ToString("0.#"), ResourceTooltip.ValueDefault));
                    break;
            }
        }
        if (rows.Count == 0)
            rows.Add(new ResourceTooltip.Row("Status effect", "", ResourceTooltip.ValueDefault));

        // Duration goes in the full-width footer, not the narrow header value
        // slot (where "Permanent"/"12s" would overflow into the title).
        string title = b.DisplayName + (ab.StackCount > 1 ? $" x{ab.StackCount}" : "");
        string dur = ab.Permanent ? "Permanent" : $"{MathF.Ceiling(ab.RemainingDuration):0}s remaining";
        ResourceTooltip.Bind(_renderer, BuffTipInst, title, "", ResourceTooltip.ValueDefault, rows, dur);
        _renderer.SetImage(BuffTipInst + ".0", "icon", ResolveBuffIcon(b));
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

    /// <summary>Attack rows share the equipment row template (UnitStatRow). Two
    /// columns differ from the equipment defaults: the 4th stat is encumbrance
    /// (fatigue icon, not defense), and the armor-only "Enc" alternate (s2b) must
    /// stay hidden so the length label (s2) shows for the length column.</summary>
    private void AtRowTemplate(string row, bool zebraHidden)
    {
        _renderer.SetHidden(row, "swatch", zebraHidden);
        _renderer.SetImage(row, "s3", IcoFat);
        _renderer.SetHidden(row, "s2b", true);
        _renderer.SetHidden(row, "s2", false);
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
            AtRowTemplate(row, i % 2 != 0);
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
            AtRowTemplate(row, false);
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

    private static void SetCell(RuntimeWidgetRenderer r, string cell, string label, string value,
        Color? valueColor = null)
    {
        r.SetText(Stats, cell + "_label", label);
        r.SetText(Stats, cell + "_value", value);
        if (valueColor.HasValue)
            r.SetTextColor(Stats, cell + "_value", valueColor.Value.R, valueColor.Value.G, valueColor.Value.B);
    }

    // Value colour when a stat is currently raised / lowered by an active buff.
    private static readonly Color BuffUp = new(96, 170, 96);
    private static readonly Color BuffDown = new(198, 74, 62);

    /// <summary>Colour for a buff-modified value: green if the buff raised it,
    /// red if lowered, null (keep default) if unchanged.</summary>
    private static Color? BuffColor(float modified, float baseVal)
        => Math.Abs(modified - baseVal) < 0.01f ? (Color?)null
            : (modified > baseVal ? BuffUp : BuffDown);

    /// <summary>Map a stat-tooltip key to its BuffStat, or null if the stat
    /// isn't buff-modifiable (Morale, Size, Shield, Parry, Upkeep, …).</summary>
    private static BuffStat? KeyToBuffStat(string key) => key switch
    {
        "hp" => BuffStat.MaxHP,
        "magicres" => BuffStat.MagicResist,
        "strength" => BuffStat.Strength,
        "protection" => BuffStat.NaturalProt,
        "attack" => BuffStat.Attack,
        "defense" => BuffStat.Defense,
        "speed" => BuffStat.CombatSpeed,
        "encumbrance" => BuffStat.Encumbrance,
        _ => null,
    };

    private static string FormatBonus(int v) => v > 0 ? "+" + v : v.ToString();
}
