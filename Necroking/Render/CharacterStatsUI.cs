using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.GameSystems;

namespace Necroking.Render;

/// <summary>
/// Toggleable panel (Tab) showing the player necromancer's stats.
/// Stats modified by active buffs are colored green (higher) or red (lower) vs base.
/// </summary>
public class CharacterStatsUI
{
    private const int PanelW = 340;
    private const int RowH = 18;
    private const int PadX = 14;
    private const int PadY = 12;
    private const int TitleH = 26;
    private const int BuffsHeaderH = 22;

    private static readonly Color PanelBg = new(15, 15, 25, 230);
    private static readonly Color PanelBorder = new(120, 120, 170, 240);
    private static readonly Color TitleColor = new(255, 220, 140);
    private static readonly Color LabelColor = new(180, 180, 200);
    private static readonly Color ValueColor = new(230, 230, 240);
    private static readonly Color SectionColor = new(160, 200, 240);
    private static readonly Color BuffedUpColor = new(120, 230, 120);
    private static readonly Color BuffedDownColor = new(230, 110, 110);
    private static readonly Color BaseDimColor = new(140, 140, 155);
    private static readonly Color BuffNameColor = new(220, 200, 160);

    private SpriteBatch _batch = null!;
    private Texture2D _pixel = null!;
    private SpriteFont? _font;
    private SpriteFont? _smallFont;

    public bool IsVisible { get; private set; }

    public void Init(SpriteBatch batch, Texture2D pixel, SpriteFont? font, SpriteFont? smallFont)
    {
        _batch = batch;
        _pixel = pixel;
        _font = font;
        _smallFont = smallFont;
    }

    public void Toggle() => IsVisible = !IsVisible;

    private readonly struct Row
    {
        public readonly string Label;
        public readonly string Value;
        public readonly Color Color;
        public readonly string? BaseSuffix; // e.g. "(base 10)"
        public readonly bool IsSection;

        public Row(string label, string value, Color color, string? baseSuffix = null, bool isSection = false)
        {
            Label = label; Value = value; Color = color; BaseSuffix = baseSuffix; IsSection = isSection;
        }

        public static Row Section(string label) => new(label, "", SectionColor_, null, true);
        public static readonly Row Blank = new("", "", default, null, false);
        private static readonly Color SectionColor_ = new(160, 200, 240);
    }

    public void Draw(int screenW, int screenH, Simulation sim, BuffRegistry buffs)
    {
        if (!IsVisible || _font == null) return;
        int necroIdx = sim.NecromancerIndex;
        if (necroIdx < 0 || necroIdx >= sim.Units.Count) return;

        var unit = sim.Units[necroIdx];
        var s = unit.Stats;
        var necro = sim.NecroState;

        float buffedMaxHp = BuffSystem.GetModifiedStat(sim.UnitsMut, necroIdx, BuffStat.MaxHP, s.MaxHP);
        int buffedMaxHpI = (int)System.MathF.Round(buffedMaxHp);
        bool maxHpDiffers = buffedMaxHpI != s.MaxHP;
        Color hpColor = !maxHpDiffers ? ValueColor
            : (buffedMaxHpI > s.MaxHP ? BuffedUpColor : BuffedDownColor);
        string hpValue = $"{s.HP} / {buffedMaxHpI}";
        string? hpSuffix = maxHpDiffers ? $"(base {s.MaxHP})" : null;

        var rows = new List<Row>
        {
            Row.Section("-- Vitals --"),
            new("HP", hpValue, hpColor, hpSuffix),
            new("Mana", $"{(int)necro.Mana} / {(int)necro.MaxMana}", ValueColor),
            new("Mana Regen", $"{necro.ManaRegen:F1}/s", ValueColor),
            Row.Blank,
            Row.Section("-- Combat --"),
            MakeBuffedRow("Strength", s.Strength, sim, necroIdx, BuffStat.Strength),
            MakeBuffedRow("Attack", s.Attack, sim, necroIdx, BuffStat.Attack),
            MakeBuffedRow("Defense", s.Defense, sim, necroIdx, BuffStat.Defense),
            MakeBuffedRow("Magic Resist", s.MagicResist, sim, necroIdx, BuffStat.MagicResist),
            MakeBuffedRowF("Combat Speed", s.CombatSpeed, sim, necroIdx, BuffStat.CombatSpeed),
            new("Damage", s.Damage.ToString(), ValueColor),
            new("Weapon Length", s.Length.ToString(), ValueColor),
            Row.Blank,
            Row.Section("-- Defense --"),
            new("Body Prot", s.Armor.BodyProtection.ToString(), ValueColor),
            new("Head Prot", s.Armor.HeadProtection.ToString(), ValueColor),
            MakeBuffedRow("Natural Prot", s.NaturalProt, sim, necroIdx, BuffStat.NaturalProt),
            new("Shield Prot", s.ShieldProtection.ToString(), ValueColor),
            new("Shield Parry", s.ShieldParry.ToString(), ValueColor),
            new("Shield Defense", s.ShieldDefense.ToString(), ValueColor),
            MakeBuffedRow("Encumbrance", s.Encumbrance, sim, necroIdx, BuffStat.Encumbrance),
        };

        int activeBuffCount = unit.ActiveBuffs.Count;
        int buffListH = activeBuffCount > 0 ? BuffsHeaderH + activeBuffCount * RowH + 6 : 0;

        int panelH = TitleH + PadY * 2 + rows.Count * RowH + buffListH;
        int panelX = (screenW - PanelW) / 2;
        int panelY = (screenH - panelH) / 2;

        _batch.Draw(_pixel, new Rectangle(panelX, panelY, PanelW, panelH), PanelBg);
        _batch.Draw(_pixel, new Rectangle(panelX, panelY, PanelW, 2), PanelBorder);
        _batch.Draw(_pixel, new Rectangle(panelX, panelY + panelH - 2, PanelW, 2), PanelBorder);
        _batch.Draw(_pixel, new Rectangle(panelX, panelY, 2, panelH), PanelBorder);
        _batch.Draw(_pixel, new Rectangle(panelX + PanelW - 2, panelY, 2, panelH), PanelBorder);

        string title = "Character Stats";
        var titleSize = _font.MeasureString(title);
        _batch.DrawString(_font, title,
            new Vector2((int)(panelX + (PanelW - titleSize.X) / 2), (int)(panelY + 4)),
            TitleColor);

        var rowFont = _smallFont ?? _font;
        int y = panelY + TitleH + PadY;
        foreach (var r in rows)
        {
            if (r.IsSection)
            {
                _batch.DrawString(rowFont, r.Label, new Vector2(panelX + PadX, y), r.Color);
            }
            else if (!string.IsNullOrEmpty(r.Label))
            {
                _batch.DrawString(rowFont, r.Label, new Vector2(panelX + PadX, y), LabelColor);

                // Draw value right-aligned, then optional dim base suffix to the left of it.
                var valSize = rowFont.MeasureString(r.Value);
                int valX = (int)(panelX + PanelW - PadX - valSize.X);
                _batch.DrawString(rowFont, r.Value, new Vector2(valX, y), r.Color);

                if (!string.IsNullOrEmpty(r.BaseSuffix))
                {
                    var suffSize = rowFont.MeasureString(r.BaseSuffix);
                    _batch.DrawString(rowFont, r.BaseSuffix,
                        new Vector2(valX - (int)suffSize.X - 6, y), BaseDimColor);
                }
            }
            y += RowH;
        }

        if (activeBuffCount > 0)
        {
            _batch.DrawString(rowFont, "-- Active Buffs --",
                new Vector2(panelX + PadX, y), SectionColor);
            y += BuffsHeaderH;
            foreach (var ab in unit.ActiveBuffs)
            {
                var def = buffs.Get(ab.BuffDefID);
                string name = def?.DisplayName ?? ab.BuffDefID;
                string stacks = ab.StackCount > 1 ? $" x{ab.StackCount}" : "";
                string dur = ab.RemainingDuration > 0f ? $"{ab.RemainingDuration:F1}s" : "perm";
                _batch.DrawString(rowFont, $"{name}{stacks}",
                    new Vector2(panelX + PadX, y), BuffNameColor);
                var durSize = rowFont.MeasureString(dur);
                _batch.DrawString(rowFont, dur,
                    new Vector2((int)(panelX + PanelW - PadX - durSize.X), y), BaseDimColor);
                y += RowH;
            }
        }
    }

    private Row MakeBuffedRow(string label, int baseVal, Simulation sim, int unitIdx, BuffStat stat)
    {
        float buffed = BuffSystem.GetModifiedStat(sim.UnitsMut, unitIdx, stat, baseVal);
        int buffedI = (int)System.MathF.Round(buffed);
        bool differs = buffedI != baseVal;
        Color color = !differs ? ValueColor
            : (buffedI > baseVal ? BuffedUpColor : BuffedDownColor);
        string value = buffedI.ToString();
        string? baseSuffix = differs ? $"(base {baseVal})" : null;
        return new Row(label, value, color, baseSuffix);
    }

    private Row MakeBuffedRowF(string label, float baseVal, Simulation sim, int unitIdx, BuffStat stat)
    {
        float buffed = BuffSystem.GetModifiedStat(sim.UnitsMut, unitIdx, stat, baseVal);
        bool differs = System.MathF.Abs(buffed - baseVal) > 0.01f;
        Color color = !differs ? ValueColor
            : (buffed > baseVal ? BuffedUpColor : BuffedDownColor);
        string value = buffed.ToString("F1");
        string? baseSuffix = differs ? $"(base {baseVal:F1})" : null;
        return new Row(label, value, color, baseSuffix);
    }
}
