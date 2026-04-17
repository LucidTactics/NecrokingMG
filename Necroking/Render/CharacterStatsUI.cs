using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.GameSystems;

namespace Necroking.Render;

/// <summary>
/// Toggleable panel (Tab) showing the player necromancer's stats, plus two
/// skill panels to the right:
///   - "Learn Skills": click to learn/unlearn. Passives apply on learn.
///   - "Active Skills": shows learned active spells; click to bind / unbind
///     from the primary bar's right-click slot.
/// Stats modified by active buffs are colored green (higher) or red (lower)
/// vs base.
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

    // Top-left anchor: sits under the HP/mana bars (mana bar ends around y=66).
    private const int AnchorX = 10;
    private const int AnchorY = 74;

    private enum SkillKind { ActiveSpell, PassiveGhostMode, PassiveArchmage }

    // Skill list: Active skills can be bound to the RC slot; Passive skills toggle effects on the necromancer.
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

    private const int RcSlotIndex = 3;

    // Learned skills (keyed by Name). Passives apply while in this set;
    // actives appear in the Active Skills panel while in this set.
    private readonly HashSet<string> _learned = new();

    // Skill points: each learned skill costs 1, refunded on unlearn.
    private const int StartingSkillPoints = 5;
    private const int SkillCost = 1;
    private int _skillPoints = StartingSkillPoints;

    // Extra vertical space in the Learn panel for the "Points: N" counter.
    private const int SkillPointsHeaderH = 22;

    private static readonly Color SkillBtnBg = new(40, 40, 60, 220);
    private static readonly Color SkillBtnBgLearned = new(60, 80, 100, 230);
    private static readonly Color SkillBtnBgActive = new(80, 110, 70, 240);
    private static readonly Color SkillBtnBgHover = new(80, 80, 120, 240);
    private static readonly Color SkillBtnBgDisabled = new(30, 30, 40, 180);
    private static readonly Color SkillBtnBorder = new(120, 120, 170, 240);
    private static readonly Color SkillBtnBorderLearned = new(140, 180, 220, 240);
    private static readonly Color SkillBtnBorderActive = new(180, 230, 140, 255);
    private static readonly Color SkillBtnText = new(230, 230, 240);
    private static readonly Color SkillBtnTextDim = new(130, 130, 150);
    private static readonly Color SkillBtnTextActive = new(230, 255, 210);
    private static readonly Color SkillPointsColor = new(255, 220, 140);
    private static readonly Color SkillPointsEmptyColor = new(220, 130, 130);

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

    /// <summary>Bounds test covering all three panels (stats + learn + active).</summary>
    public bool ContainsMouse(int screenW, int screenH, int mx, int my, Simulation sim)
    {
        if (!IsVisible) return false;
        int necroIdx = sim.NecromancerIndex;
        if (necroIdx < 0 || necroIdx >= sim.Units.Count) return false;

        int activeBuffs = sim.Units[necroIdx].ActiveBuffs.Count;
        int statsRowCount = 22; // keep in sync with rows list in Draw
        int buffListH = activeBuffs > 0 ? BuffsHeaderH + activeBuffs * RowH + 6 : 0;
        int statsH = TitleH + PadY * 2 + statsRowCount * RowH + buffListH;
        int statsX = AnchorX;
        int statsY = AnchorY;

        if (mx >= statsX && mx < statsX + PanelW && my >= statsY && my < statsY + statsH)
            return true;

        int learnX = statsX + PanelW + SkillsGap;
        int learnH = LearnPanelHeight();
        if (mx >= learnX && mx < learnX + SkillsPanelW && my >= statsY && my < statsY + learnH)
            return true;

        int activeX = learnX + SkillsPanelW + SkillsGap;
        int activeH = ActivePanelHeight();
        return mx >= activeX && mx < activeX + SkillsPanelW && my >= statsY && my < statsY + activeH;
    }

    private static int LearnPanelHeight() => TitleH + SkillPointsHeaderH + PadY * 2 + Skills.Length * (SkillBtnH + SkillBtnGap) - SkillBtnGap;
    private int ActivePanelHeight()
    {
        int count = 0;
        foreach (var sk in Skills)
            if (sk.Kind == SkillKind.ActiveSpell && _learned.Contains(sk.Name)) count++;
        int rows = System.Math.Max(count, 1); // always have at least title + hint space
        return TitleH + PadY * 2 + rows * (SkillBtnH + SkillBtnGap) - SkillBtnGap;
    }

    private readonly struct Row
    {
        public readonly string Label;
        public readonly string Value;
        public readonly Color Color;
        public readonly string? BaseSuffix;
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

        int statsH = TitleH + PadY * 2 + rows.Count * RowH + buffListH;
        int panelX = AnchorX;
        int panelY = AnchorY;

        // Stats panel background + border
        _batch.Draw(_pixel, new Rectangle(panelX, panelY, PanelW, statsH), PanelBg);
        DrawBorder(panelX, panelY, PanelW, statsH, PanelBorder);

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

        int learnX = panelX + PanelW + SkillsGap;
        DrawLearnPanel(learnX, panelY, rowFont, input, sim, necroIdx, ref primaryBar);

        int activeX = learnX + SkillsPanelW + SkillsGap;
        DrawActivePanel(activeX, panelY, rowFont, input, ref primaryBar);
    }

    private void DrawBorder(int x, int y, int w, int h, Color c)
    {
        _batch.Draw(_pixel, new Rectangle(x, y, w, 2), c);
        _batch.Draw(_pixel, new Rectangle(x, y + h - 2, w, 2), c);
        _batch.Draw(_pixel, new Rectangle(x, y, 2, h), c);
        _batch.Draw(_pixel, new Rectangle(x + w - 2, y, 2, h), c);
    }

    private void DrawLearnPanel(int px, int py, SpriteFont rowFont, InputState input,
        Simulation sim, int necroIdx, ref SpellBarState primaryBar)
    {
        int panelH = LearnPanelHeight();

        _batch.Draw(_pixel, new Rectangle(px, py, SkillsPanelW, panelH), PanelBg);
        DrawBorder(px, py, SkillsPanelW, panelH, PanelBorder);

        string title = "Learn Skills";
        var titleSize = _font!.MeasureString(title);
        _batch.DrawString(_font, title,
            new Vector2((int)(px + (SkillsPanelW - titleSize.X) / 2), (int)(py + 4)),
            TitleColor);

        // Skill points counter below the title
        string pts = $"Points: {_skillPoints}";
        var ptsSize = _font.MeasureString(pts);
        _batch.DrawString(_font, pts,
            new Vector2((int)(px + (SkillsPanelW - ptsSize.X) / 2), (int)(py + TitleH + 2)),
            _skillPoints > 0 ? SkillPointsColor : SkillPointsEmptyColor);

        int mx = (int)input.MousePos.X;
        int my = (int)input.MousePos.Y;

        int btnY = py + TitleH + SkillPointsHeaderH + PadY;
        for (int i = 0; i < Skills.Length; i++)
        {
            var rect = new Rectangle(px + PadX, btnY, SkillsPanelW - PadX * 2, SkillBtnH);
            bool learned = _learned.Contains(Skills[i].Name);
            bool hovered = rect.Contains(mx, my);
            bool canAfford = learned || _skillPoints >= SkillCost;

            Color bg;
            Color border;
            Color textColor;
            if (learned)
            {
                bg = hovered ? SkillBtnBgHover : SkillBtnBgLearned;
                border = SkillBtnBorderLearned;
                textColor = SkillBtnText;
            }
            else if (!canAfford)
            {
                bg = SkillBtnBgDisabled;
                border = SkillBtnBorder;
                textColor = SkillBtnTextDim;
            }
            else
            {
                bg = hovered ? SkillBtnBgHover : SkillBtnBg;
                border = SkillBtnBorder;
                textColor = SkillBtnTextDim;
            }

            _batch.Draw(_pixel, rect, bg);
            DrawBorder(rect.X, rect.Y, rect.Width, rect.Height, border);

            var tSize = rowFont.MeasureString(Skills[i].Name);
            _batch.DrawString(rowFont, Skills[i].Name,
                new Vector2((int)(rect.X + (rect.Width - tSize.X) / 2),
                           (int)(rect.Y + (rect.Height - tSize.Y) / 2)),
                textColor);

            if (hovered && input.LeftPressed && !input.IsMouseConsumed && canAfford)
            {
                bool wantLearn = !learned;
                SetLearned(Skills[i], wantLearn, sim, necroIdx, ref primaryBar);
                input.ConsumeMouse();
            }

            btnY += SkillBtnH + SkillBtnGap;
        }
    }

    private void DrawActivePanel(int px, int py, SpriteFont rowFont, InputState input,
        ref SpellBarState primaryBar)
    {
        int panelH = ActivePanelHeight();

        _batch.Draw(_pixel, new Rectangle(px, py, SkillsPanelW, panelH), PanelBg);
        DrawBorder(px, py, SkillsPanelW, panelH, PanelBorder);

        string title = "Active Skills (RC)";
        var titleSize = _font!.MeasureString(title);
        _batch.DrawString(_font, title,
            new Vector2((int)(px + (SkillsPanelW - titleSize.X) / 2), (int)(py + 4)),
            TitleColor);

        string boundId = primaryBar.Slots != null && primaryBar.Slots.Length > RcSlotIndex
            ? (primaryBar.Slots[RcSlotIndex].SpellID ?? "") : "";

        int mx = (int)input.MousePos.X;
        int my = (int)input.MousePos.Y;

        int btnY = py + TitleH + PadY;
        int learnedActiveCount = 0;
        for (int i = 0; i < Skills.Length; i++)
        {
            if (Skills[i].Kind != SkillKind.ActiveSpell) continue;
            if (!_learned.Contains(Skills[i].Name)) continue;

            learnedActiveCount++;
            var rect = new Rectangle(px + PadX, btnY, SkillsPanelW - PadX * 2, SkillBtnH);
            bool bound = Skills[i].SpellId == boundId;
            bool hovered = rect.Contains(mx, my);

            Color bg = bound ? SkillBtnBgActive : (hovered ? SkillBtnBgHover : SkillBtnBg);
            Color border = bound ? SkillBtnBorderActive : SkillBtnBorder;
            Color textColor = bound ? SkillBtnTextActive : SkillBtnText;

            _batch.Draw(_pixel, rect, bg);
            DrawBorder(rect.X, rect.Y, rect.Width, rect.Height, border);

            var tSize = rowFont.MeasureString(Skills[i].Name);
            _batch.DrawString(rowFont, Skills[i].Name,
                new Vector2((int)(rect.X + (rect.Width - tSize.X) / 2),
                           (int)(rect.Y + (rect.Height - tSize.Y) / 2)),
                textColor);

            if (hovered && input.LeftPressed && !input.IsMouseConsumed
                && primaryBar.Slots != null && primaryBar.Slots.Length > RcSlotIndex)
            {
                primaryBar.Slots[RcSlotIndex].SpellID = bound ? "" : Skills[i].SpellId;
                input.ConsumeMouse();
            }

            btnY += SkillBtnH + SkillBtnGap;
        }

        if (learnedActiveCount == 0)
        {
            string hint = "(learn skills to populate)";
            var hSize = rowFont.MeasureString(hint);
            _batch.DrawString(rowFont, hint,
                new Vector2((int)(px + (SkillsPanelW - hSize.X) / 2), btnY + 4),
                SkillBtnTextDim);
        }
    }

    private void SetLearned((string Name, string SpellId, SkillKind Kind) skill, bool learn,
        Simulation sim, int necroIdx, ref SpellBarState primaryBar)
    {
        bool wasLearned = _learned.Contains(skill.Name);
        if (wasLearned == learn) return;
        if (learn && _skillPoints < SkillCost) return;

        if (learn)
        {
            _learned.Add(skill.Name);
            _skillPoints -= SkillCost;
        }
        else
        {
            _learned.Remove(skill.Name);
            _skillPoints += SkillCost;
        }

        switch (skill.Kind)
        {
            case SkillKind.ActiveSpell:
                // Unlearning clears the RC slot if bound.
                if (!learn && primaryBar.Slots != null && primaryBar.Slots.Length > RcSlotIndex
                    && primaryBar.Slots[RcSlotIndex].SpellID == skill.SpellId)
                {
                    primaryBar.Slots[RcSlotIndex].SpellID = "";
                }
                break;
            case SkillKind.PassiveGhostMode:
                sim.UnitsMut[necroIdx].GhostMode = learn;
                break;
            case SkillKind.PassiveArchmage:
                ApplyArchmage(sim, learn);
                break;
        }
    }

    private void ApplyArchmage(Simulation sim, bool enable)
    {
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
