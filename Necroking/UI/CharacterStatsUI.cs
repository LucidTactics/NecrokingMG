using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.Game;
using Necroking.GameSystems;
using Necroking.Render;

namespace Necroking.UI;

/// <summary>
/// Toggleable panel (Tab) showing the player necromancer's stats, plus two
/// skill panels to the right:
///   - "Learn Skills": click to learn/unlearn. Passives apply on learn.
///   - "Active Skills": shows learned active spells; click to bind / unbind
///     from the primary bar's right-click slot.
/// Stats modified by active buffs are colored green (higher) or red (lower)
/// vs base.
/// </summary>
public class CharacterStatsUI : IModalLayer
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
        ("Nether Blast", "nether_blast", SkillKind.ActiveSpell),
        ("Life Drain", "life_drain", SkillKind.ActiveSpell),
        ("Necromantic Miasma", "necromantic_miasma", SkillKind.ActiveSpell),
        ("Raise Zombie", "raise_zombie", SkillKind.ActiveSpell),
        ("God Ray", "god_ray", SkillKind.ActiveSpell),
        ("Ghost Mode", "", SkillKind.PassiveGhostMode),
        ("Archmage", "", SkillKind.PassiveArchmage),
    };

    // Archmage passive bonuses
    private const float ArchmageMaxManaBonus = 150f;
    private const float ArchmageRegenBonus = 5f;

    /// <summary>Index of the bar slot holding this spell, or -1. Active skills
    /// toggle on/off the spell bar (first empty slot on bind).</summary>
    private static int FindBarSlot(in SpellBarState bar, string spellId)
    {
        if (bar.Slots == null || string.IsNullOrEmpty(spellId)) return -1;
        for (int i = 0; i < bar.Slots.Length; i++)
            if (bar.Slots[i].SpellID == spellId) return i;
        return -1;
    }

    private static int FindEmptyBarSlot(in SpellBarState bar)
    {
        if (bar.Slots == null) return -1;
        for (int i = 0; i < bar.Slots.Length; i++)
            if (string.IsNullOrEmpty(bar.Slots[i].SpellID)) return i;
        return -1;
    }

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
    private Render.SpriteScope Scope => _batch;  // straight-alpha draw surface (implicit conversion)
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

    public void Toggle()
    {
        if (IsVisible)
        {
            Close();
        }
        else
        {
            IsVisible = true;
        }
    }

    public void Close()
    {
        if (!IsVisible) return;
        IsVisible = false;
    }

    // === IModalLayer ===
    public bool LightDismiss => false;
    public bool IsBlocking => false;  // side panel — gameplay coexists
    /// <summary>Cached during the most recent Draw call. PopupManager calls
    /// ContainsMouse(mx,my) with no other context, so we precompute the union
    /// of the three panel rects each frame and let the manager hit-test that.</summary>
    private Microsoft.Xna.Framework.Rectangle _lastBoundsRect;
    public bool ContainsMouse(int mx, int my) => _lastBoundsRect.Contains(mx, my);
    public Microsoft.Xna.Framework.Rectangle? HitBounds(int screenW, int screenH)
        => IsVisible ? _lastBoundsRect : null;
    public void OnCancel() => Toggle();

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
        // Optional breakdown for the hover tooltip (e.g. base + buff contributions).
        public readonly List<TipLine>? TipLines;

        public Row(string label, string value, Color color, string? baseSuffix = null,
            bool isSection = false, List<TipLine>? tipLines = null)
        {
            Label = label; Value = value; Color = color; BaseSuffix = baseSuffix;
            IsSection = isSection; TipLines = tipLines;
        }

        public static Row Section(string label) => new(label, "", SectionColor_, null, true);
        public static readonly Row Blank = new("", "", default, null, false);
        private static readonly Color SectionColor_ = new(160, 200, 240);
    }

    /// <summary>One row in a stat tooltip's "where the number comes from" breakdown.</summary>
    public readonly struct TipLine
    {
        public readonly string Label;
        public readonly string Value;
        public readonly Color Color;
        public TipLine(string label, string value, Color color)
        { Label = label; Value = value; Color = color; }
    }

    /// <summary>A hovered stat row recorded during Draw, used to place the tooltip.</summary>
    private readonly struct StatHover
    {
        public readonly Rectangle Rect;
        public readonly string Label;
        public readonly string Value;
        public readonly List<TipLine>? Lines;
        public StatHover(Rectangle rect, string label, string value, List<TipLine>? lines)
        { Rect = rect; Label = label; Value = value; Lines = lines; }
    }

    // Hover regions for stat rows, rebuilt each Draw.
    private readonly List<StatHover> _statHover = new();

    private static readonly Color TipBg = new(20, 20, 32, 245);

    // What each stat does. Wording is shared with the unit-sheet tooltips
    // (Necroking.UI.StatTooltips) so descriptions stay in one place; rows the
    // unit sheet doesn't cover (mana, per-slot armor) get their own text here.
    private static string Desc(string key) => Necroking.UI.StatTooltips.Info[key].Desc;
    private static readonly Dictionary<string, string> StatDesc = new()
    {
        ["HP"] = Desc("hp"),
        ["Mana"] = "Mana fuels your spells. Every cast drains it, and it slowly regenerates over time. At zero mana you cannot cast.",
        ["Mana Regen"] = "How much mana you recover each second.",
        ["Strength"] = Desc("strength"),
        ["Attack"] = Desc("attack"),
        ["Defense"] = Desc("defense"),
        ["Magic Resist"] = Desc("magicres"),
        ["Morale"] = Desc("morale"),
        ["Combat Speed"] = Desc("speed"),
        ["Damage"] = "Base damage of the weapon, before strength is added and the target's protection is subtracted.",
        ["Weapon Length"] = "Reach of the weapon. Longer weapons strike first when closing in and can hit over shorter ones.",
        ["Body Prot"] = "Armor over the torso. Subtracted from incoming damage that lands on the body.",
        ["Head Prot"] = "Armor over the head. Subtracted from incoming damage that lands on the head.",
        ["Natural Prot"] = Desc("protection"),
        ["Shield Prot"] = Desc("shield"),
        ["Shield Parry"] = Desc("parry"),
        ["Shield Defense"] = "Extra defense granted while a shield is raised, improving the chance to avoid blows entirely.",
        ["Encumbrance"] = Desc("encumbrance"),
    };

    public void Draw(int screenW, int screenH, Simulation sim, BuffRegistry buffs,
        ref SpellBarState primaryBar, InputState input, GameData gameData = null,
        SkillBookState? bookState = null)
    {
        if (!IsVisible || _font == null) return;
        int necroIdx = sim.NecromancerIndex;
        if (necroIdx < 0 || necroIdx >= sim.Units.Count) return;

        // Refresh the cached bounds rect for IModalLayer.ContainsMouse. The
        // existing five-arg ContainsMouse(screenW,screenH,mx,my,sim) is still
        // used by Game1's MouseOverUI block; this just provides the manager-
        // compatible (mx,my)-only overload with the same geometry.
        {
            int _activeBuffs = sim.Units[necroIdx].ActiveBuffs.Count;
            int _statsRowCount = 22;
            int _buffListH = _activeBuffs > 0 ? BuffsHeaderH + _activeBuffs * RowH + 6 : 0;
            int _statsH = TitleH + PadY * 2 + _statsRowCount * RowH + _buffListH;
            int _learnX = AnchorX + PanelW + SkillsGap;
            int _activeX = _learnX + SkillsPanelW + SkillsGap;
            int _totalRight = _activeX + SkillsPanelW;
            int _maxPanelH = System.Math.Max(System.Math.Max(_statsH, LearnPanelHeight()), ActivePanelHeight());
            _lastBoundsRect = new Microsoft.Xna.Framework.Rectangle(
                AnchorX, AnchorY, _totalRight - AnchorX, _maxPanelH);
        }

        var unit = sim.Units[necroIdx];
        var s = unit.Stats;
        var necro = sim.NecroState;

        // Necromancer's UnitDef for path lookup. Optional — without GameData
        // we just skip the inline path row, the rest of the panel renders fine.
        var necroDef = gameData.Units.Get(unit.UnitDefID);

        float buffedMaxHp = BuffSystem.GetModifiedStat(sim.UnitsMut, necroIdx, BuffStat.MaxHP, s.MaxHP);
        int buffedMaxHpI = (int)System.MathF.Round(buffedMaxHp);
        bool maxHpDiffers = buffedMaxHpI != s.MaxHP;
        Color hpColor = !maxHpDiffers ? ValueColor
            : (buffedMaxHpI > s.MaxHP ? BuffedUpColor : BuffedDownColor);
        string hpValue = $"{s.HP} / {buffedMaxHpI}";
        string? hpSuffix = maxHpDiffers ? $"(base {s.MaxHP})" : null;

        var hpLines = new List<TipLine> { new("Current", s.HP.ToString(), ValueColor) };
        if (maxHpDiffers)
        {
            hpLines.Add(new("Max (base)", s.MaxHP.ToString(), BaseDimColor));
            AddBuffLines(hpLines, unit.ActiveBuffs, buffs, BuffStat.MaxHP, false);
            hpLines.Add(new("Max total", buffedMaxHpI.ToString(), hpColor));
        }
        else
        {
            hpLines.Add(new("Max", s.MaxHP.ToString(), ValueColor));
        }

        var manaLines = new List<TipLine>
        {
            new("Current", ((int)necro.Mana).ToString(), ValueColor),
            new("Max", ((int)necro.MaxMana).ToString(), ValueColor),
            new("Regen", $"{necro.ManaRegen:F1}/s", ValueColor),
        };
        var regenLines = new List<TipLine> { new("Per second", $"{necro.ManaRegen:F1}", ValueColor) };

        // Cooldown rate (1.0x = real time). God mode multiplies it ×10 → spells
        // recharge 10× faster; show the effective rate, tinted when buffed.
        float cdRate = BuffSystem.GetModifiedExtra(sim.UnitsMut, necroIdx, "CooldownRate", necro.CooldownRate);
        bool cdBuffed = System.MathF.Abs(cdRate - necro.CooldownRate) > 0.001f;
        Color cdColor = !cdBuffed ? ValueColor
            : (cdRate > necro.CooldownRate ? BuffedUpColor : BuffedDownColor);
        var cdLines = new List<TipLine>
        {
            new("Base", $"{necro.CooldownRate:F1}x", cdBuffed ? BaseDimColor : ValueColor),
        };
        if (cdBuffed) cdLines.Add(new("Effective", $"{cdRate:F1}x", cdColor));

        var rows = new List<Row>
        {
            Row.Section("-- Vitals --"),
            new("HP", hpValue, hpColor, hpSuffix, tipLines: hpLines),
            new("Mana", $"{(int)necro.Mana} / {(int)necro.MaxMana}", ValueColor, tipLines: manaLines),
            new("Mana Regen", $"{necro.ManaRegen:F1}/s", ValueColor, tipLines: regenLines),
            new("Cooldown Rate", $"{cdRate:F1}x", cdColor, tipLines: cdLines),
            Row.Blank,
            Row.Section("-- Combat --"),
            MakeBuffedRow("Strength", s.Strength, sim, necroIdx, BuffStat.Strength, buffs),
            MakeBuffedRow("Attack", s.Attack, sim, necroIdx, BuffStat.Attack, buffs),
            MakeBuffedRow("Defense", s.Defense, sim, necroIdx, BuffStat.Defense, buffs),
            MakeBuffedRow("Magic Resist", s.MagicResist, sim, necroIdx, BuffStat.MagicResist, buffs),
            new("Morale", s.Morale.ToString(), ValueColor),
            MakeBuffedRowF("Combat Speed", s.CombatSpeed, sim, necroIdx, BuffStat.CombatSpeed, buffs),
            new("Damage", s.Damage.ToString(), ValueColor),
            new("Weapon Length", s.Length.ToString(), ValueColor),
            Row.Blank,
            Row.Section("-- Defense --"),
            new("Body Prot", s.Armor.BodyProtection.ToString(), ValueColor),
            new("Head Prot", s.Armor.HeadProtection.ToString(), ValueColor),
            MakeBuffedRow("Natural Prot", s.NaturalProt, sim, necroIdx, BuffStat.NaturalProt, buffs),
            new("Shield Prot", s.ShieldProtection.ToString(), ValueColor),
            new("Shield Parry", s.ShieldParry.ToString(), ValueColor),
            new("Shield Defense", s.ShieldDefense.ToString(), ValueColor),
            MakeBuffedRow("Encumbrance", s.Encumbrance, sim, necroIdx, BuffStat.Encumbrance, buffs),
        };

        _statHover.Clear();

        // Metamorphosis active abilities — buttons render inline when the
        // matching skill is learned. Height calc up front so the panel sizes
        // correctly.
        bool hasCorpseEat   = bookState?.HasPassive("action:corpse_eating") ?? false;
        bool hasSoulConsume = bookState?.HasPassive("action:soul_consumption") ?? false;
        int metamorphActions = (hasCorpseEat ? 1 : 0) + (hasSoulConsume ? 1 : 0);
        int metamorphH = metamorphActions > 0 ? BuffsHeaderH + metamorphActions * (SkillBtnH + SkillBtnGap) : 0;

        int activeBuffCount = unit.ActiveBuffs.Count;
        int buffListH = activeBuffCount > 0 ? BuffsHeaderH + activeBuffCount * RowH + 6 : 0;

        // Collect non-zero paths for the inline display row. We render only
        // paths the unit actually has — zero-paths are hidden per design.
        // Use the EFFECTIVE level (native + path buffs, e.g. arcane_apprentice's
        // +1 Shock, then any AllPaths floor) so buff-granted paths show here too,
        // not just inherent UnitDef ones.
        var nonZeroPaths = new List<(MagicPath path, int level)>();
        if (necroDef != null)
        {
            foreach (var p in MagicPathHelpers.AllInOrder)
            {
                int lvl = BuffSystem.EffectivePathLevel(sim.UnitsMut, necroIdx, necroDef, p);
                if (lvl > 0) nonZeroPaths.Add((p, lvl));
            }
        }
        int pathsRowH = nonZeroPaths.Count > 0 ? BuffsHeaderH + RowH : 0;

        int statsH = TitleH + PadY * 2 + rows.Count * RowH + pathsRowH + metamorphH + buffListH;
        int panelX = AnchorX;
        int panelY = AnchorY;

        // Stats panel background + border
        Scope.Draw(_pixel, new Rectangle(panelX, panelY, PanelW, statsH), PanelBg);
        DrawBorder(panelX, panelY, PanelW, statsH, PanelBorder);

        string title = "Character Stats";
        var titleSize = _font.MeasureString(title);
        Scope.DrawString(_font, title,
            new Vector2((int)(panelX + (PanelW - titleSize.X) / 2), (int)(panelY + 4)),
            TitleColor);

        var rowFont = _smallFont ?? _font;
        int y = panelY + TitleH + PadY;
        foreach (var r in rows)
        {
            if (r.IsSection)
            {
                Scope.DrawString(rowFont, r.Label, new Vector2(panelX + PadX, y), r.Color);
            }
            else if (!string.IsNullOrEmpty(r.Label))
            {
                Scope.DrawString(rowFont, r.Label, new Vector2(panelX + PadX, y), LabelColor);
                var valSize = rowFont.MeasureString(r.Value);
                int valX = (int)(panelX + PanelW - PadX - valSize.X);
                Scope.DrawString(rowFont, r.Value, new Vector2(valX, y), r.Color);

                if (!string.IsNullOrEmpty(r.BaseSuffix))
                {
                    var suffSize = rowFont.MeasureString(r.BaseSuffix);
                    Scope.DrawString(rowFont, r.BaseSuffix,
                        new Vector2(valX - (int)suffSize.X - 6, y), BaseDimColor);
                }

                // Record the full-width row rect so we can show a tooltip on hover.
                _statHover.Add(new StatHover(
                    new Rectangle(panelX, y, PanelW, RowH), r.Label, r.Value, r.TipLines));
            }
            y += RowH;
        }

        if (nonZeroPaths.Count > 0)
        {
            Scope.DrawString(rowFont, "-- Paths --", new Vector2(panelX + PadX, y), SectionColor);
            y += BuffsHeaderH;

            int iconSize = 16;
            int slotW = iconSize + 16; // icon + small gap + 1-2 digits
            int px = panelX + PadX;
            foreach (var (path, level) in nonZeroPaths)
            {
                if (px + slotW > panelX + PanelW - PadX) break; // single-line clip
                var tex = MagicPathIcons.Get(path, 24);
                if (tex != null)
                {
                    Scope.Draw(tex, new Rectangle(px, y + 1, iconSize, iconSize),
                        Color.White);
                }
                else
                {
                    // Icon missing — show the short tag as a fallback so the entry still reads.
                    string tag = $"({MagicPathHelpers.ShortTag(path)})";
                    Scope.DrawString(rowFont, tag, new Vector2(px, y), LabelColor);
                }
                string n = level.ToString();
                Scope.DrawString(rowFont, n, new Vector2(px + iconSize + 2, y), ValueColor);
                px += slotW;
            }
            y += RowH;
        }

        if (metamorphActions > 0 && bookState != null)
        {
            Scope.DrawString(rowFont, "-- Metamorphosis --",
                new Vector2(panelX + PadX, y), SectionColor);
            y += BuffsHeaderH;

            int btnMX = (int)input.MousePos.X;
            int btnMY = (int)input.MousePos.Y;

            if (hasCorpseEat)
            {
                int bonus = bookState.CorpseEatingBonus;
                int cap = SkillBookState.CorpseEatingHPCap;
                string label = bonus >= cap ? $"Eat Corpse (capped {cap}/{cap})"
                                            : $"Eat Corpse  (+{bonus}/{cap} HP)";
                var rect = new Rectangle(panelX + PadX, y, PanelW - PadX * 2, SkillBtnH);
                bool hovered = rect.Contains(btnMX, btnMY);
                Scope.Draw(_pixel, rect, hovered ? SkillBtnBgHover : SkillBtnBg);
                DrawBorder(rect.X, rect.Y, rect.Width, rect.Height, SkillBtnBorder);
                var lz = rowFont.MeasureString(label);
                Scope.DrawString(rowFont, label,
                    new Vector2((int)(rect.X + (rect.Width - lz.X) / 2),
                                (int)(rect.Y + (rect.Height - lz.Y) / 2)),
                    SkillBtnText);
                // Inside-panel click — PopupManager already consumed.
                if (hovered && input.LeftPressed)
                {
                    TryConsumeNearestCorpse(sim, necroIdx, bookState, gameData, humansOnly: false);
                }
                y += SkillBtnH + SkillBtnGap;
            }
            if (hasSoulConsume)
            {
                int bonus = bookState.SoulConsumptionBonus;
                int cap = SkillBookState.SoulConsumptionManaCap;
                string label = bonus >= cap ? $"Consume Soul (capped {cap}/{cap})"
                                            : $"Consume Soul  (+{bonus}/{cap} Mana)";
                var rect = new Rectangle(panelX + PadX, y, PanelW - PadX * 2, SkillBtnH);
                bool hovered = rect.Contains(btnMX, btnMY);
                Scope.Draw(_pixel, rect, hovered ? SkillBtnBgHover : SkillBtnBg);
                DrawBorder(rect.X, rect.Y, rect.Width, rect.Height, SkillBtnBorder);
                var lz = rowFont.MeasureString(label);
                Scope.DrawString(rowFont, label,
                    new Vector2((int)(rect.X + (rect.Width - lz.X) / 2),
                                (int)(rect.Y + (rect.Height - lz.Y) / 2)),
                    SkillBtnText);
                if (hovered && input.LeftPressed)
                {
                    TryConsumeNearestCorpse(sim, necroIdx, bookState, gameData, humansOnly: true);
                }
                y += SkillBtnH + SkillBtnGap;
            }
        }

        if (activeBuffCount > 0)
        {
            Scope.DrawString(rowFont, "-- Active Buffs --",
                new Vector2(panelX + PadX, y), SectionColor);
            y += BuffsHeaderH;
            foreach (var ab in unit.ActiveBuffs)
            {
                var def = buffs.Get(ab.BuffDefID);
                string name = def?.DisplayName ?? ab.BuffDefID;
                string stacks = ab.StackCount > 1 ? $" x{ab.StackCount}" : "";
                string dur = ab.RemainingDuration > 0f ? $"{ab.RemainingDuration:F1}s" : "perm";
                Scope.DrawString(rowFont, $"{name}{stacks}",
                    new Vector2(panelX + PadX, y), BuffNameColor);
                var durSize = rowFont.MeasureString(dur);
                Scope.DrawString(rowFont, dur,
                    new Vector2((int)(panelX + PanelW - PadX - durSize.X), y), BaseDimColor);
                y += RowH;
            }
        }

        int learnX = panelX + PanelW + SkillsGap;
        DrawLearnPanel(learnX, panelY, rowFont, input, sim, necroIdx, ref primaryBar);

        int activeX = learnX + SkillsPanelW + SkillsGap;
        DrawActivePanel(activeX, panelY, rowFont, input, ref primaryBar);

        // Stat hover tooltip, drawn last so it sits on top of every panel.
        int tipMx = (int)input.MousePos.X;
        int tipMy = (int)input.MousePos.Y;
        foreach (var hreg in _statHover)
        {
            if (hreg.Rect.Contains(tipMx, tipMy))
            {
                DrawStatTooltip(hreg, tipMx, tipMy, screenW, screenH, rowFont);
                break;
            }
        }
    }

    /// <summary>Append one breakdown line per active buff that modifies
    /// <paramref name="stat"/>, summarizing its net Add / Multiply / Set
    /// contribution. Mirrors the combination logic in BuffSystem.GetModifiedStat.</summary>
    private void AddBuffLines(List<TipLine> lines,
        IReadOnlyList<Necroking.Movement.ActiveBuff> activeBuffs, BuffRegistry buffs,
        BuffStat stat, bool isFloat)
    {
        string sn = stat.ToString();
        foreach (var ab in activeBuffs)
        {
            float add = 0f, mult = 1f;
            float? set = null;
            if (ab.Effects != null)
            {
                foreach (var eff in ab.Effects)
                {
                    if (eff.Stat != sn) continue;
                    switch (eff.Type)
                    {
                        case "Add": add += eff.Value * ab.StackCount; break;
                        case "Multiply": mult *= System.MathF.Pow(eff.Value, ab.StackCount); break;
                        case "Set": set = eff.Value; break;
                    }
                }
            }
            if (add == 0f && mult == 1f && set == null) continue;

            string name = buffs.Get(ab.BuffDefID)?.DisplayName ?? ab.BuffDefID;
            string Fmt(float v) => isFloat ? v.ToString("F1") : ((int)System.MathF.Round(v)).ToString();
            string val;
            Color col;
            if (set != null)        { val = "=" + Fmt(set.Value); col = ValueColor; }
            else if (mult != 1f)    { val = "x" + mult.ToString("0.##"); col = mult > 1f ? BuffedUpColor : BuffedDownColor; }
            else                    { val = (add > 0 ? "+" : "") + Fmt(add); col = add > 0 ? BuffedUpColor : BuffedDownColor; }
            lines.Add(new TipLine(name, val, col));
        }
    }

    private void DrawStatTooltip(StatHover h, int mx, int my, int screenW, int screenH, SpriteFont rowFont)
    {
        if (_font == null) return;

        const int TipW = 280;
        const int Pad = 8;
        int innerW = TipW - Pad * 2;

        string desc = StatDesc.TryGetValue(h.Label, out var d) ? d : "";
        var descLines = WrapText(rowFont, desc, innerW);

        // Breakdown: explicit lines if provided, else a single "Base" line so
        // every stat still shows its number.
        var bdLines = h.Lines ?? (string.IsNullOrEmpty(h.Value)
            ? new List<TipLine>()
            : new List<TipLine> { new("Base", h.Value, ValueColor) });

        int lineH = rowFont.LineSpacing;
        int titleH = (int)_font.MeasureString(h.Label).Y;

        int height = Pad + titleH + 4;
        height += descLines.Count * lineH;
        if (bdLines.Count > 0) height += 8 + bdLines.Count * lineH; // divider gap + rows
        height += Pad;

        var (tx, ty) = PlaceTip(mx, my, TipW, height, screenW, screenH);

        Scope.Draw(_pixel, new Rectangle(tx, ty, TipW, height), TipBg);
        DrawBorder(tx, ty, TipW, height, PanelBorder);

        int cy = ty + Pad;
        Scope.DrawString(_font, h.Label, new Vector2(tx + Pad, cy), TitleColor);
        cy += titleH + 4;

        foreach (var ln in descLines)
        {
            Scope.DrawString(rowFont, ln, new Vector2(tx + Pad, cy), LabelColor);
            cy += lineH;
        }

        if (bdLines.Count > 0)
        {
            cy += 3;
            Scope.Draw(_pixel, new Rectangle(tx + Pad, cy, innerW, 1), PanelBorder);
            cy += 5;
            foreach (var ln in bdLines)
            {
                Scope.DrawString(rowFont, ln.Label, new Vector2(tx + Pad, cy), LabelColor);
                var vs = rowFont.MeasureString(ln.Value);
                Scope.DrawString(rowFont, ln.Value,
                    new Vector2((int)(tx + TipW - Pad - vs.X), cy), ln.Color);
                cy += lineH;
            }
        }
    }

    /// <summary>Greedy word-wrap to a pixel width.</summary>
    private static List<string> WrapText(SpriteFont font, string text, float maxW)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(text)) return result;
        var sb = new System.Text.StringBuilder();
        foreach (var word in text.Split(' '))
        {
            string trial = sb.Length == 0 ? word : sb + " " + word;
            if (sb.Length > 0 && font.MeasureString(trial).X > maxW)
            {
                result.Add(sb.ToString());
                sb.Clear();
                sb.Append(word);
            }
            else
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(word);
            }
        }
        if (sb.Length > 0) result.Add(sb.ToString());
        return result;
    }

    /// <summary>Cursor offset +16/+20, flipped when it would clip a screen edge.</summary>
    private static (int, int) PlaceTip(int mx, int my, int w, int h, int sw, int sh)
    {
        int x = mx + 16, y = my + 20;
        if (x + w > sw - 4) x = mx - w - 8;
        if (y + h > sh - 4) y = my - h - 8;
        return (System.Math.Max(4, x), System.Math.Max(4, y));
    }

    /// <summary>Find the closest non-dissolving corpse within range and consume
    /// it: dissolve the corpse, heal the necromancer (HP for Corpse Eating,
    /// Mana for Soul Consumption), and grant +1 max-stat bonus on the
    /// SkillBookState (subject to per-skill cap). humansOnly filters by the
    /// corpse's source UnitDef's Faction == Human. Returns silently when no
    /// valid corpse is in range — the player just sees no effect.</summary>
    private void TryConsumeNearestCorpse(Simulation sim, int necroIdx,
        SkillBookState bookState, GameData gameData, bool humansOnly)
    {
        if (necroIdx < 0) return;
        const float Range = 6f;
        var necroPos = sim.Units[necroIdx].Position;
        int bestIdx = sim.Query.NearestCorpse(necroPos, Range,
            new ConsumableCorpses(humansOnly ? gameData : null));
        if (bestIdx < 0)
        {
            Core.DebugLog.Log("skillbook", $"metamorph action: no {(humansOnly ? "human " : "")}corpse in range");
            return;
        }

        sim.ConsumeCorpse(bestIdx);
        // Milestone tally for skill requirements (e.g. Wight transformation). Both
        // Corpse Eating and Soul Consumption consume a corpse, so both count.
        bookState.Events.Tally("corpses_eaten");
        if (humansOnly)
        {
            var necro = sim.NecroState;
            necro.Mana = MathF.Min(necro.MaxMana, necro.Mana + 10f);
            if (bookState.TryGrantSoulConsumptionBonus())
                necro.MaxMana += 1f;
            Core.DebugLog.Log("skillbook",
                $"Soul Consumption: mana {(int)necro.Mana}/{(int)necro.MaxMana}, bonus {bookState.SoulConsumptionBonus}/{SkillBookState.SoulConsumptionManaCap}");
        }
        else
        {
            // Unit is a class — indexer returns the live reference, so field
            // mutations land in the array without needing a write-back.
            var u = sim.UnitsMut[necroIdx];
            var stats = u.Stats;
            stats.HP = Math.Min(stats.MaxHP, stats.HP + 10);
            if (bookState.TryGrantCorpseEatingBonus())
                stats.MaxHP += 1;
            u.Stats = stats;
            Core.DebugLog.Log("skillbook",
                $"Corpse Eating: hp {u.Stats.HP}/{u.Stats.MaxHP}, bonus {bookState.CorpseEatingBonus}/{SkillBookState.CorpseEatingHPCap}");
        }
    }

    /// <summary>Corpses eligible for consumption: not dissolving, and — when a
    /// GameData is supplied — only bodies whose source UnitDef faction is Human
    /// (the Soul Consumption gate). Null gameData = any faction.</summary>
    private readonly struct ConsumableCorpses : ICorpseQueryFilter
    {
        private readonly GameData? _humanGate;
        public ConsumableCorpses(GameData? humanGate) { _humanGate = humanGate; }
        public bool Match(Corpse c)
        {
            if (c.Dissolving) return false;
            if (_humanGate == null) return true;
            var cDef = _humanGate.Units.Get(c.UnitDefID);
            return cDef != null && cDef.Faction == "Human";
        }
    }

    private void DrawBorder(int x, int y, int w, int h, Color c)
    {
        Necroking.Render.DrawUtils.DrawRectBorder(_batch, _pixel, new Rectangle(x, y, w, h), c, 2);
    }

    private void DrawLearnPanel(int px, int py, SpriteFont rowFont, InputState input,
        Simulation sim, int necroIdx, ref SpellBarState primaryBar)
    {
        int panelH = LearnPanelHeight();

        Scope.Draw(_pixel, new Rectangle(px, py, SkillsPanelW, panelH), PanelBg);
        DrawBorder(px, py, SkillsPanelW, panelH, PanelBorder);

        string title = "Learn Skills";
        var titleSize = _font!.MeasureString(title);
        Scope.DrawString(_font, title,
            new Vector2((int)(px + (SkillsPanelW - titleSize.X) / 2), (int)(py + 4)),
            TitleColor);

        // Skill points counter below the title
        string pts = $"Points: {_skillPoints}";
        var ptsSize = _font.MeasureString(pts);
        Scope.DrawString(_font, pts,
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

            Scope.Draw(_pixel, rect, bg);
            DrawBorder(rect.X, rect.Y, rect.Width, rect.Height, border);

            var tSize = rowFont.MeasureString(Skills[i].Name);
            Scope.DrawString(rowFont, Skills[i].Name,
                new Vector2((int)(rect.X + (rect.Width - tSize.X) / 2),
                           (int)(rect.Y + (rect.Height - tSize.Y) / 2)),
                textColor);

            if (hovered && input.LeftPressed && canAfford)
            {
                bool wantLearn = !learned;
                SetLearned(Skills[i], wantLearn, sim, necroIdx, ref primaryBar);
            }

            btnY += SkillBtnH + SkillBtnGap;
        }
    }

    private void DrawActivePanel(int px, int py, SpriteFont rowFont, InputState input,
        ref SpellBarState primaryBar)
    {
        int panelH = ActivePanelHeight();

        Scope.Draw(_pixel, new Rectangle(px, py, SkillsPanelW, panelH), PanelBg);
        DrawBorder(px, py, SkillsPanelW, panelH, PanelBorder);

        string title = "Active Skills";
        var titleSize = _font!.MeasureString(title);
        Scope.DrawString(_font, title,
            new Vector2((int)(px + (SkillsPanelW - titleSize.X) / 2), (int)(py + 4)),
            TitleColor);

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
            int boundSlot = FindBarSlot(primaryBar, Skills[i].SpellId);
            bool bound = boundSlot >= 0;
            bool hovered = rect.Contains(mx, my);

            Color bg = bound ? SkillBtnBgActive : (hovered ? SkillBtnBgHover : SkillBtnBg);
            Color border = bound ? SkillBtnBorderActive : SkillBtnBorder;
            Color textColor = bound ? SkillBtnTextActive : SkillBtnText;

            Scope.Draw(_pixel, rect, bg);
            DrawBorder(rect.X, rect.Y, rect.Width, rect.Height, border);

            var tSize = rowFont.MeasureString(Skills[i].Name);
            Scope.DrawString(rowFont, Skills[i].Name,
                new Vector2((int)(rect.X + (rect.Width - tSize.X) / 2),
                           (int)(rect.Y + (rect.Height - tSize.Y) / 2)),
                textColor);

            if (hovered && input.LeftPressed && primaryBar.Slots != null)
            {
                if (bound)
                    primaryBar.Slots[boundSlot].SpellID = "";
                else
                {
                    int empty = FindEmptyBarSlot(primaryBar);
                    if (empty >= 0) primaryBar.Slots[empty].SpellID = Skills[i].SpellId;
                }
            }

            btnY += SkillBtnH + SkillBtnGap;
        }

        if (learnedActiveCount == 0)
        {
            string hint = "(learn skills to populate)";
            var hSize = rowFont.MeasureString(hint);
            Scope.DrawString(rowFont, hint,
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
                // Unlearning clears the spell's bar slot if bound.
                if (!learn)
                {
                    int boundSlot = FindBarSlot(primaryBar, skill.SpellId);
                    if (boundSlot >= 0) primaryBar.Slots[boundSlot].SpellID = "";
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

    private Row MakeBuffedRow(string label, int baseVal, Simulation sim, int unitIdx, BuffStat stat, BuffRegistry buffs)
    {
        float buffed = BuffSystem.GetModifiedStat(sim.UnitsMut, unitIdx, stat, baseVal);
        int buffedI = (int)System.MathF.Round(buffed);
        bool differs = buffedI != baseVal;
        Color color = !differs ? ValueColor
            : (buffedI > baseVal ? BuffedUpColor : BuffedDownColor);
        string value = buffedI.ToString();
        string? baseSuffix = differs ? $"(base {baseVal})" : null;

        var lines = new List<TipLine> { new("Base", baseVal.ToString(), BaseDimColor) };
        AddBuffLines(lines, sim.Units[unitIdx].ActiveBuffs, buffs, stat, false);
        if (lines.Count > 1) lines.Add(new TipLine("Total", buffedI.ToString(), color));
        return new Row(label, value, color, baseSuffix, tipLines: lines);
    }

    private Row MakeBuffedRowF(string label, float baseVal, Simulation sim, int unitIdx, BuffStat stat, BuffRegistry buffs)
    {
        float buffed = BuffSystem.GetModifiedStat(sim.UnitsMut, unitIdx, stat, baseVal);
        bool differs = System.MathF.Abs(buffed - baseVal) > 0.01f;
        Color color = !differs ? ValueColor
            : (buffed > baseVal ? BuffedUpColor : BuffedDownColor);
        string value = buffed.ToString("F1");
        string? baseSuffix = differs ? $"(base {baseVal:F1})" : null;

        var lines = new List<TipLine> { new("Base", baseVal.ToString("F1"), BaseDimColor) };
        AddBuffLines(lines, sim.Units[unitIdx].ActiveBuffs, buffs, stat, true);
        if (lines.Count > 1) lines.Add(new TipLine("Total", buffed.ToString("F1"), color));
        return new Row(label, value, color, baseSuffix, tipLines: lines);
    }
}
