using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.GameSystems;

namespace Necroking.Render;

/// <summary>
/// Toggleable panel (Tab) showing the player necromancer's stats.
/// Stats modified by active buffs are colored green (higher) or red (lower) vs base.
/// Also shows a skill button list that toggles spells into the right-click slot.
/// </summary>
public class CharacterStatsUI
{
    private const int PanelW = 340;
    private const int RowH = 18;
    private const int PadX = 14;
    private const int PadY = 12;
    private const int TitleH = 26;
    private const int BuffsHeaderH = 22;

    private const int SkillsPanelW = 200;
    private const int SkillsGap = 12;
    private const int SkillBtnH = 28;
    private const int SkillBtnGap = 4;

    private enum SkillKind { ActiveSpell, PassiveGhostMode, PassiveArchmage }

    // Skill list: Active skills assign to the RC slot; Passive skills toggle effects on the necromancer.
    private static readonly (string Name, string SpellId, SkillKind Kind)[] Skills =
    {
        ("Nether Darts", "nether_darts", SkillKind.ActiveSpell),
        ("Lightning Zap", "lightning_zap", SkillKind.ActiveSpell),
        ("Lightning Beam", "lightning_beam", SkillKind.ActiveSpell),
        ("Sky Lightning", "sky_lightning", SkillKind.ActiveSpell),
        ("Fireball", "fireball", SkillKind.ActiveSpell),
        ("Life Drain", "life_drain", SkillKind.ActiveSpell),
        ("Poison Cloud", "poison_cloud", SkillKind.ActiveSpell),
        ("Raise Zombie", "raise_zombie", SkillKind.ActiveSpell),
        ("God Ray", "god_ray", SkillKind.ActiveSpell),
        ("Ghost Mode", "", SkillKind.PassiveGhostMode),
        ("Archmage", "", SkillKind.PassiveArchmage),
    };

    // Archmage passive bonuses
    private const float ArchmageMaxManaBonus = 150f;
    private const float ArchmageRegenBonus = 5f;
    private bool _archmageActive;

    private const int RcSlotIndex = 3;

    private static readonly Color SkillBtnBg = new(40, 40, 60, 220);
    private static readonly Color SkillBtnBgActive = new(80, 110, 70, 240);
    private static readonly Color SkillBtnBgHover = new(60, 60, 90, 240);
    private static readonly Color SkillBtnBorder = new(120, 120, 170, 240);
    private static readonly Color SkillBtnBorderActive = new(180, 230, 140, 255);
    private static readonly Color SkillBtnText = new(230, 230, 240);
    private static readonly Color SkillBtnTextActive = new(230, 255, 210);

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

    /// <summary>Approximate bounds test: true if the mouse is over either the stats or skills panel.</summary>
    public bool ContainsMouse(int screenW, int screenH, int mx, int my, Simulation sim)
    {
        if (!IsVisible) return false;
        int necroIdx = sim.NecromancerIndex;
        if (necroIdx < 0 || necroIdx >= sim.Units.Count) return false;

        int activeBuffs = sim.Units[necroIdx].ActiveBuffs.Count;
        int statsRowCount = 22; // matches the rows list below (keep in sync)
        int buffListH = activeBuffs > 0 ? BuffsHeaderH + activeBuffs * RowH + 6 : 0;
        int statsH = TitleH + PadY * 2 + statsRowCount * RowH + buffListH;
        int statsX = (screenW - PanelW) / 2;
        int statsY = (screenH - statsH) / 2;

        if (mx >= statsX && mx < statsX + PanelW && my >= statsY && my < statsY + statsH)
            return true;

        int skillsX = statsX + PanelW + SkillsGap;
        int skillsH = TitleH + PadY * 2 + Skills.Length * (SkillBtnH + SkillBtnGap) - SkillBtnGap;
        return mx >= skillsX && mx < skillsX + SkillsPanelW && my >= statsY && my < statsY + skillsH;
    }

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

    public void Draw(int screenW, int screenH, Simulation sim, BuffRegistry buffs,
        ref SpellBarState primaryBar, InputState input)
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

        DrawSkillsPanel(panelX + PanelW + SkillsGap, panelY, rowFont, ref primaryBar, input, sim, necroIdx);
    }

    private void DrawSkillsPanel(int px, int py, SpriteFont rowFont,
        ref SpellBarState primaryBar, InputState input, Simulation sim, int necroIdx)
    {
        int panelH = TitleH + PadY * 2 + Skills.Length * (SkillBtnH + SkillBtnGap) - SkillBtnGap;

        _batch.Draw(_pixel, new Rectangle(px, py, SkillsPanelW, panelH), PanelBg);
        _batch.Draw(_pixel, new Rectangle(px, py, SkillsPanelW, 2), PanelBorder);
        _batch.Draw(_pixel, new Rectangle(px, py + panelH - 2, SkillsPanelW, 2), PanelBorder);
        _batch.Draw(_pixel, new Rectangle(px, py, 2, panelH), PanelBorder);
        _batch.Draw(_pixel, new Rectangle(px + SkillsPanelW - 2, py, 2, panelH), PanelBorder);

        string title = "Skills (RC)";
        var titleSize = _font!.MeasureString(title);
        _batch.DrawString(_font, title,
            new Vector2((int)(px + (SkillsPanelW - titleSize.X) / 2), (int)(py + 4)),
            TitleColor);

        string activeId = primaryBar.Slots != null && primaryBar.Slots.Length > RcSlotIndex
            ? (primaryBar.Slots[RcSlotIndex].SpellID ?? "") : "";

        int mx = (int)input.MousePos.X;
        int my = (int)input.MousePos.Y;
        bool hoveredAny = false;

        int btnY = py + TitleH + PadY;
        for (int i = 0; i < Skills.Length; i++)
        {
            int btnX = px + PadX;
            int btnW = SkillsPanelW - PadX * 2;
            var rect = new Rectangle(btnX, btnY, btnW, SkillBtnH);

            bool active = Skills[i].Kind switch
            {
                SkillKind.ActiveSpell => Skills[i].SpellId == activeId,
                SkillKind.PassiveGhostMode => sim.Units[necroIdx].GhostMode,
                SkillKind.PassiveArchmage => _archmageActive,
                _ => false,
            };
            bool hovered = rect.Contains(mx, my);
            if (hovered) hoveredAny = true;

            Color bg = active ? SkillBtnBgActive : (hovered ? SkillBtnBgHover : SkillBtnBg);
            Color border = active ? SkillBtnBorderActive : SkillBtnBorder;
            Color textColor = active ? SkillBtnTextActive : SkillBtnText;

            _batch.Draw(_pixel, rect, bg);
            _batch.Draw(_pixel, new Rectangle(rect.X, rect.Y, rect.Width, 1), border);
            _batch.Draw(_pixel, new Rectangle(rect.X, rect.Bottom - 1, rect.Width, 1), border);
            _batch.Draw(_pixel, new Rectangle(rect.X, rect.Y, 1, rect.Height), border);
            _batch.Draw(_pixel, new Rectangle(rect.Right - 1, rect.Y, 1, rect.Height), border);

            var tSize = rowFont.MeasureString(Skills[i].Name);
            _batch.DrawString(rowFont, Skills[i].Name,
                new Vector2((int)(rect.X + (rect.Width - tSize.X) / 2),
                           (int)(rect.Y + (rect.Height - tSize.Y) / 2)),
                textColor);

            if (hovered && input.LeftPressed && !input.IsMouseConsumed)
            {
                switch (Skills[i].Kind)
                {
                    case SkillKind.ActiveSpell:
                        if (primaryBar.Slots != null && primaryBar.Slots.Length > RcSlotIndex)
                        {
                            primaryBar.Slots[RcSlotIndex].SpellID = active ? "" : Skills[i].SpellId;
                            input.ConsumeMouse();
                        }
                        break;
                    case SkillKind.PassiveGhostMode:
                        sim.UnitsMut[necroIdx].GhostMode = !active;
                        input.ConsumeMouse();
                        break;
                    case SkillKind.PassiveArchmage:
                        ToggleArchmage(sim, !active);
                        input.ConsumeMouse();
                        break;
                }
            }

            btnY += SkillBtnH + SkillBtnGap;
        }

        if (hoveredAny) input.MouseOverUI = true;
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

    private void ToggleArchmage(Simulation sim, bool enable)
    {
        if (enable == _archmageActive) return;
        var necro = sim.NecroState;
        if (enable)
        {
            necro.MaxMana += ArchmageMaxManaBonus;
            necro.Mana += ArchmageMaxManaBonus;
            necro.ManaRegen += ArchmageRegenBonus;
        }
        else
        {
            necro.MaxMana -= ArchmageMaxManaBonus;
            necro.ManaRegen -= ArchmageRegenBonus;
            if (necro.Mana > necro.MaxMana) necro.Mana = necro.MaxMana;
            if (necro.Mana < 0f) necro.Mana = 0f;
        }
        _archmageActive = enable;
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
