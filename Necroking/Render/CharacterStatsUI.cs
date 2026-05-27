using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.Game;
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
public class CharacterStatsUI : Necroking.UI.IModalLayer
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

    public void Toggle()
    {
        if (IsVisible)
        {
            IsVisible = false;
            Necroking.Game1.Popups.Pop(this);
        }
        else
        {
            IsVisible = true;
            Necroking.Game1.Popups.Push(this);
        }
    }

    // === IModalLayer ===
    public bool LightDismiss => false;
    public bool IsBlocking => false;  // side panel — gameplay coexists
    /// <summary>Cached during the most recent Draw call. PopupManager calls
    /// ContainsMouse(mx,my) with no other context, so we precompute the union
    /// of the three panel rects each frame and let the manager hit-test that.</summary>
    private Microsoft.Xna.Framework.Rectangle _lastBoundsRect;
    public bool ContainsMouse(int mx, int my) => _lastBoundsRect.Contains(mx, my);
    public void OnCancel() => Toggle();

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
        ref SpellBarState primaryBar, InputState input, GameData? gameData = null,
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
        var necroDef = gameData?.Units.Get(unit.UnitDefID);

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
        // TODO: also hide on the per-unit selection panel when that's wired.
        var nonZeroPaths = new List<(MagicPath path, int level)>();
        if (necroDef != null)
        {
            foreach (var p in MagicPathHelpers.AllInOrder)
            {
                int lvl = necroDef.GetPathLevel(p);
                if (lvl > 0) nonZeroPaths.Add((p, lvl));
            }
        }
        int pathsRowH = nonZeroPaths.Count > 0 ? BuffsHeaderH + RowH : 0;

        int statsH = TitleH + PadY * 2 + rows.Count * RowH + pathsRowH + metamorphH + buffListH;
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

        if (nonZeroPaths.Count > 0)
        {
            _batch.DrawString(rowFont, "-- Paths --", new Vector2(panelX + PadX, y), SectionColor);
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
                    _batch.Draw(tex, new Rectangle(px, y + 1, iconSize, iconSize),
                        Color.White);
                }
                else
                {
                    // Icon missing — show the short tag as a fallback so the entry still reads.
                    string tag = $"({MagicPathHelpers.ShortTag(path)})";
                    _batch.DrawString(rowFont, tag, new Vector2(px, y), LabelColor);
                }
                string n = level.ToString();
                _batch.DrawString(rowFont, n, new Vector2(px + iconSize + 2, y), ValueColor);
                px += slotW;
            }
            y += RowH;
        }

        if (metamorphActions > 0 && bookState != null)
        {
            _batch.DrawString(rowFont, "-- Metamorphosis --",
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
                _batch.Draw(_pixel, rect, hovered ? SkillBtnBgHover : SkillBtnBg);
                DrawBorder(rect.X, rect.Y, rect.Width, rect.Height, SkillBtnBorder);
                var lz = rowFont.MeasureString(label);
                _batch.DrawString(rowFont, label,
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
                _batch.Draw(_pixel, rect, hovered ? SkillBtnBgHover : SkillBtnBg);
                DrawBorder(rect.X, rect.Y, rect.Width, rect.Height, SkillBtnBorder);
                var lz = rowFont.MeasureString(label);
                _batch.DrawString(rowFont, label,
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

    /// <summary>Find the closest non-dissolving corpse within range and consume
    /// it: dissolve the corpse, heal the necromancer (HP for Corpse Eating,
    /// Mana for Soul Consumption), and grant +1 max-stat bonus on the
    /// SkillBookState (subject to per-skill cap). humansOnly filters by the
    /// corpse's source UnitDef's Faction == Human. Returns silently when no
    /// valid corpse is in range — the player just sees no effect.</summary>
    private void TryConsumeNearestCorpse(Simulation sim, int necroIdx,
        SkillBookState bookState, GameData? gameData, bool humansOnly)
    {
        if (necroIdx < 0) return;
        const float Range = 6f;
        var necroPos = sim.Units[necroIdx].Position;
        int bestIdx = -1;
        float bestDistSq = Range * Range;
        for (int i = 0; i < sim.Corpses.Count; i++)
        {
            var c = sim.Corpses[i];
            if (c.Dissolving) continue;
            if (humansOnly && gameData != null)
            {
                var cDef = gameData.Units.Get(c.UnitDefID);
                if (cDef == null) continue;
                if (cDef.Faction != "Human") continue;
            }
            float d = (c.Position - necroPos).LengthSq();
            if (d < bestDistSq) { bestDistSq = d; bestIdx = i; }
        }
        if (bestIdx < 0)
        {
            Core.DebugLog.Log("skillbook", $"metamorph action: no {(humansOnly ? "human " : "")}corpse in range");
            return;
        }

        sim.ConsumeCorpse(bestIdx);
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

            if (hovered && input.LeftPressed
                && primaryBar.Slots != null && primaryBar.Slots.Length > RcSlotIndex)
            {
                primaryBar.Slots[RcSlotIndex].SpellID = bound ? "" : Skills[i].SpellId;
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
