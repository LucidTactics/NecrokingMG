using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.Game;
using Necroking.Game.SkillEffects;
using Necroking.GameSystems;

namespace Necroking.Render;

/// <summary>
/// Skill book panel — modal "tome" UI toggled with lowercase K. Tabs across the
/// top (Potions / Necromancy / Magic / Metamorphosis), each showing a manually
/// laid-out skill tree of one-time-unlock nodes with AND-style dependencies.
/// Costs deduct from the inventory or check the cumulative event tally
/// (<see cref="SkillEventTracker"/>); see <see cref="SkillBookState"/>.
///
/// Visuals adapt the grimoire palette from the older <see cref="SkillTreePanel"/>
/// (now defunct, see Shift+K) but draws everything with verified primitives only —
/// no <see cref="UIGfx"/>. Each node is a rectangular button with title + cost
/// subtitle, color-coded by affordability, with a button press-down animation.
/// </summary>
public class SkillBookPanel
{
    public bool IsVisible { get; private set; }
    public void Toggle() { IsVisible = !IsVisible; }
    public void Open()   { IsVisible = true;  }
    public void Close()  { IsVisible = false; }

    // ----- Wiring -----
    private SpriteBatch _batch = null!;
    private Texture2D _pixel = null!;
    private SpriteFont? _font;
    private SpriteFont? _smallFont;
    private SpriteFont? _largeFont;

    private SkillBookState _state = null!;
    private Inventory _inventory = null!;
    private GameData _gameData = null!;
    private SpellBarState _primaryBar;
    private SpellBarState _secondaryBar;

    public void Init(SpriteBatch batch, Texture2D pixel,
        SpriteFont? font, SpriteFont? smallFont, SpriteFont? largeFont)
    {
        _batch = batch;
        _pixel = pixel;
        _font = font;
        _smallFont = smallFont;
        _largeFont = largeFont;
    }

    /// <summary>Wire runtime references — call once after game systems exist. Also
    /// re-call after the spell bar Slots are reallocated so AddSpellToBarEffect
    /// targets the live arrays (SpellBarState is a struct — copies are independent).</summary>
    public void Bind(SkillBookState state, Inventory inv, GameData gd,
        SpellBarState primaryBar, SpellBarState secondaryBar)
    {
        _state = state;
        _inventory = inv;
        _gameData = gd;
        _primaryBar = primaryBar;
        _secondaryBar = secondaryBar;
    }

    // ----- Palette (grimoire) -----
    // Parchment is intentionally muted — bloom in the main game pushes anything
    // bright toward pure white, which made the original cream look like blank paper.
    private static readonly Color Parchment       = new(196, 174, 128);
    private static readonly Color ParchmentDark   = new(170, 148, 104);
    private static readonly Color ParchmentDeep   = new(132, 110,  74);
    private static readonly Color Ink             = new(23, 17, 13);
    private static readonly Color InkSoft         = new(56, 42, 28);
    private static readonly Color LeatherDark     = new(26, 13, 8);
    private static readonly Color LeatherMid      = new(42, 26, 18);
    private static readonly Color LeatherLight    = new(72, 50, 28);
    private static readonly Color Gold            = new(174, 138,  60);
    private static readonly Color GoldBright      = new(218, 184,  96);
    private static readonly Color GoldDim         = new(108,  84,  40);
    private static readonly Color BloodDark       = new( 80,  18,  18);
    private static readonly Color CostGood        = new( 60, 130,  56);
    private static readonly Color CostBad         = new(168,  44,  44);
    private static readonly Color LearnedFill     = new( 90,  82,  68);
    private static readonly Color LockedFill      = new(150, 138, 110);

    // ----- Layout -----
    // Node sizes are FIXED in screen pixels — only positions are scaled — so that
    // titles and cost subtitles remain readable regardless of viewport size.
    private const int NodeW = 180;
    private const int NodeH = 64;
    private const int TabBarH = 36;
    private const int TitleH  = 30;
    private const int FooterH = 26;
    private const int InnerPad = 16;

    private struct Layout
    {
        public Rectangle Panel;         // outer leather frame
        public Rectangle Title;         // top title plate
        public Rectangle TabBar;        // tab strip
        public Rectangle Content;       // parchment tree area (clipped)
        public Rectangle Footer;        // hint strip at bottom
        public float Scale;             // scale from logical to content px
        public int TreeOriginX;
        public int TreeOriginY;
    }

    private int _activeTab;
    private Vector2 _mouse;
    private string? _hoverSkillId;
    private string? _pressedSkillId;
    private string? _toast;
    private double _toastUntil;

    // ----- Public input -----
    public bool ContainsMouse(int sw, int sh, int mx, int my)
    {
        if (!IsVisible) return false;
        var p = PanelRect(sw, sh);
        return p.Contains(mx, my);
    }

    public void SetMouse(Vector2 m) => _mouse = m;

    public void Update(InputState input, int sw, int sh, double timeSec)
    {
        if (!IsVisible) return;
        if (_toast != null && timeSec >= _toastUntil) _toast = null;

        var lay = BuildLayout(sw, sh);
        int mx = (int)input.MousePos.X;
        int my = (int)input.MousePos.Y;
        if (!lay.Panel.Contains(mx, my)) { _pressedSkillId = null; return; }
        input.MouseOverUI = true;

        // --- Tab clicks ---
        for (int i = 0; i < SkillBookDefs.Tabs.Count; i++)
        {
            var r = TabRect(lay, i);
            if (r.Contains(mx, my) && input.LeftPressed && !input.IsMouseConsumed)
            {
                _activeTab = i;
                input.ConsumeMouse();
                _pressedSkillId = null;
                return;
            }
        }

        // --- Node interaction (active tab only) ---
        if (_activeTab < 0 || _activeTab >= SkillBookDefs.Tabs.Count) return;
        var tab = SkillBookDefs.Tabs[_activeTab];
        _hoverSkillId = null;
        SkillDef? hovered = null;
        foreach (var s in tab.Skills)
        {
            var r = NodeRect(lay, s);
            if (r.Contains(mx, my))
            {
                _hoverSkillId = s.Id;
                hovered = s;
                break;
            }
        }

        // Track button press (down on click, fires on release inside same node)
        if (input.LeftPressed && !input.IsMouseConsumed && hovered != null)
        {
            _pressedSkillId = hovered.Id;
            input.ConsumeMouse();
        }
        else if (!IsLeftDown(input) && _pressedSkillId != null)
        {
            // released — fire if still hovering same node
            if (hovered != null && hovered.Id == _pressedSkillId)
                TryLearnFromUI(hovered, timeSec);
            _pressedSkillId = null;
        }
    }

    private static bool IsLeftDown(InputState input)
        => input.LeftDown;

    private void TryLearnFromUI(SkillDef def, double timeSec)
    {
        if (_state == null) return;
        if (_state.IsLearned(def.Id))
        {
            ShowToast("Already learned.", timeSec);
            return;
        }
        if (!_state.ArePrereqsMet(def))
        {
            ShowToast("Locked - earlier skills required.", timeSec);
            return;
        }
        if (!_state.CanAfford(def, _inventory))
        {
            ShowToast("Not enough resources.", timeSec);
            return;
        }
        var ctx = new SkillEffectContext
        {
            Inventory = _inventory,
            GameData = _gameData,
            PrimaryBar = _primaryBar,
            SecondaryBar = _secondaryBar,
        };
        if (_state.TryLearn(def, ctx))
            ShowToast($"Learned: {def.Name}", timeSec);
    }

    private void ShowToast(string msg, double timeSec)
    {
        _toast = msg;
        _toastUntil = timeSec + 2.2;
    }

    // ----- Layout helpers -----
    private Rectangle PanelRect(int sw, int sh)
    {
        int marginX = Math.Max(40, sw / 24);
        int marginY = Math.Max(30, sh / 22);
        int totalW = sw - marginX * 2;
        int totalH = sh - marginY * 2;
        int maxW = (int)(totalH * 1.85f);
        if (totalW > maxW) totalW = maxW;
        totalW = Math.Max(totalW, 880);
        totalH = Math.Max(totalH, 600);
        int x = (sw - totalW) / 2;
        int y = (sh - totalH) / 2;
        return new Rectangle(x, y, totalW, totalH);
    }

    private Layout BuildLayout(int sw, int sh)
    {
        var p = PanelRect(sw, sh);
        int innerX = p.X + InnerPad;
        int innerY = p.Y + InnerPad;
        int innerW = p.Width - InnerPad * 2;

        var title = new Rectangle(innerX, innerY, innerW, TitleH);
        var tabBar = new Rectangle(innerX, title.Bottom + 4, innerW, TabBarH);
        var footer = new Rectangle(innerX, p.Bottom - InnerPad - FooterH, innerW, FooterH);
        var content = new Rectangle(innerX, tabBar.Bottom + 6,
                                    innerW, footer.Y - 6 - (tabBar.Bottom + 6));

        // Per-tab scaling: positions scale to fit, node W/H stays fixed pixels.
        float scale = 1f;
        int treeOX = content.X + content.Width / 2;
        int treeOY = content.Y + content.Height / 2;
        if (_activeTab >= 0 && _activeTab < SkillBookDefs.Tabs.Count)
        {
            var tab = SkillBookDefs.Tabs[_activeTab];
            float rangeW = Math.Max(1, tab.MaxX - tab.MinX);
            float rangeH = Math.Max(1, tab.MaxY - tab.MinY);
            // Node centers must be at least NodeW/2 from the content edges so the
            // node rect fits. Add 8px breathing room.
            float availW = content.Width  - NodeW - 16;
            float availH = content.Height - NodeH - 16;
            scale = Math.Min(availW / rangeW, availH / rangeH);
            if (scale < 0.25f) scale = 0.25f;
            if (scale > 1.5f)  scale = 1.5f;
            // Origin so that (MinX, MinY) maps to the upper-left of the centered tree.
            int treeW = (int)(rangeW * scale);
            int treeH = (int)(rangeH * scale);
            treeOX = content.X + (content.Width  - treeW) / 2 - (int)(tab.MinX * scale);
            treeOY = content.Y + (content.Height - treeH) / 2 - (int)(tab.MinY * scale);
        }

        return new Layout
        {
            Panel = p, Title = title, TabBar = tabBar,
            Content = content, Footer = footer,
            Scale = scale,
            TreeOriginX = treeOX,
            TreeOriginY = treeOY,
        };
    }

    private Rectangle TabRect(in Layout lay, int i)
    {
        int n = Math.Max(1, SkillBookDefs.Tabs.Count);
        int gap = 4;
        int totalGap = gap * (n - 1);
        int w = (lay.TabBar.Width - totalGap) / n;
        int x = lay.TabBar.X + i * (w + gap);
        return new Rectangle(x, lay.TabBar.Y, w, lay.TabBar.Height);
    }

    private Rectangle NodeRect(in Layout lay, SkillDef def)
    {
        int cx = lay.TreeOriginX + (int)(def.X * lay.Scale);
        int cy = lay.TreeOriginY + (int)(def.Y * lay.Scale);
        int dy = (def.Id == _pressedSkillId) ? 2 : 0;
        return new Rectangle(cx - NodeW / 2, cy - NodeH / 2 + dy, NodeW, NodeH);
    }

    // ----- Drawing -----
    public void Draw(int sw, int sh)
    {
        if (!IsVisible || _font == null) return;
        if (SkillBookDefs.Tabs.Count == 0) return;

        // Dim world
        _batch.Draw(_pixel, new Rectangle(0, 0, sw, sh), new Color(0, 0, 0, 180));

        var lay = BuildLayout(sw, sh);
        DrawChrome(lay);
        DrawTitle(lay);
        DrawTabBar(lay);
        DrawContent(lay);
        DrawFooter(lay);
        if (_hoverSkillId != null) DrawTooltip(lay);
        if (_toast != null) DrawToast(lay);
    }

    private void DrawChrome(in Layout lay)
    {
        // Leather background
        Fill(lay.Panel, LeatherMid);
        // Inner darker frame band
        var inner = Inset(lay.Panel, 6);
        Fill(new Rectangle(lay.Panel.X, lay.Panel.Y, lay.Panel.Width, 6), LeatherDark);
        Fill(new Rectangle(lay.Panel.X, lay.Panel.Bottom - 6, lay.Panel.Width, 6), LeatherDark);
        Fill(new Rectangle(lay.Panel.X, lay.Panel.Y, 6, lay.Panel.Height), LeatherDark);
        Fill(new Rectangle(lay.Panel.Right - 6, lay.Panel.Y, 6, lay.Panel.Height), LeatherDark);
        Border(lay.Panel, GoldDim, 1);
        Border(inner, LeatherDark, 1);
        // Corner brackets
        DrawCorner(lay.Panel.X + 4, lay.Panel.Y + 4, 22, false, false);
        DrawCorner(lay.Panel.Right - 26, lay.Panel.Y + 4, 22, true, false);
        DrawCorner(lay.Panel.X + 4, lay.Panel.Bottom - 26, 22, false, true);
        DrawCorner(lay.Panel.Right - 26, lay.Panel.Bottom - 26, 22, true, true);
    }

    private void DrawCorner(int x, int y, int size, bool flipX, bool flipY)
    {
        int t = 4;
        Fill(new Rectangle(x, flipY ? y + size - t : y, size, t), Gold);
        Fill(new Rectangle(flipX ? x + size - t : x, y, t, size), Gold);
        // rivet
        int rx = flipX ? x + size - 4 : x + 1;
        int ry = flipY ? y + size - 4 : y + 1;
        Fill(new Rectangle(rx, ry, 3, 3), LeatherDark);
    }

    private void DrawTitle(in Layout lay)
    {
        var r = lay.Title;
        Fill(r, LeatherDark);
        Border(r, GoldDim, 1);
        var f = _largeFont ?? _font!;
        string title = "TOME OF THE NECROKING";
        var size = f.MeasureString(title);
        var pos = new Vector2((int)(r.X + (r.Width - size.X) / 2),
                              (int)(r.Y + (r.Height - size.Y) / 2));
        DrawShadowText(f, title, pos, GoldBright);
    }

    private void DrawTabBar(in Layout lay)
    {
        // Backing strip
        Fill(lay.TabBar, LeatherDark);
        for (int i = 0; i < SkillBookDefs.Tabs.Count; i++)
        {
            var r = TabRect(lay, i);
            var tab = SkillBookDefs.Tabs[i];
            bool active = i == _activeTab;
            bool hover = r.Contains((int)_mouse.X, (int)_mouse.Y);

            Color fill = active ? Parchment
                       : hover  ? new Color(72, 50, 28)
                                : LeatherMid;
            Fill(r, fill);

            // Top accent on active tab (gold band)
            if (active)
                Fill(new Rectangle(r.X, r.Y, r.Width, 2), GoldBright);

            Border(r, active ? Gold : GoldDim, 1);

            var (learned, total) = _state?.GetProgress(tab) ?? (0, tab.Skills.Count);
            string label = tab.DisplayName;
            string frac = $"{learned}/{total}";
            var f = _font!;
            var sf = _smallFont ?? f;
            var lblSize = f.MeasureString(label);
            var fracSize = sf.MeasureString(frac);
            int textY = r.Y + (r.Height - (int)lblSize.Y - (int)fracSize.Y - 2) / 2;
            Color textColor = active ? Ink : new Color(210, 196, 168);
            Color fracColor = active ? InkSoft : new Color(180, 168, 140);
            DrawText(f, label,
                new Vector2((int)(r.X + (r.Width - lblSize.X) / 2), textY), textColor);
            DrawText(sf, frac,
                new Vector2((int)(r.X + (r.Width - fracSize.X) / 2),
                            textY + (int)lblSize.Y + 2), fracColor);
        }
    }

    private void DrawContent(in Layout lay)
    {
        // Parchment tree area: base fill, then a vignette + flecks so it doesn't
        // read as a flat off-white rectangle once bloom hits it in the main game.
        Fill(lay.Content, Parchment);
        DrawParchmentVignette(lay.Content);
        DrawParchmentFlecks(lay.Content);
        // Top folio band — a darker leather strip sells the "page in a tome" feel
        // and prevents the parchment from running edge-to-edge as a single white block.
        var folio = new Rectangle(lay.Content.X, lay.Content.Y, lay.Content.Width, 12);
        Fill(folio, LeatherMid);
        Fill(new Rectangle(folio.X, folio.Bottom - 1, folio.Width, 1), GoldDim);
        Border(lay.Content, LeatherDark, 1);

        if (_activeTab < 0 || _activeTab >= SkillBookDefs.Tabs.Count) return;
        var tab = SkillBookDefs.Tabs[_activeTab];

        // Connectors first (under nodes)
        foreach (var s in tab.Skills)
        {
            foreach (var pid in s.Parents)
            {
                int pi = tab.IndexOf(pid);
                if (pi < 0) continue;
                var parent = tab.Skills[pi];
                var pr = NodeRect(lay, parent);
                var cr = NodeRect(lay, s);
                var a = new Vector2(pr.X + pr.Width / 2f, pr.Bottom);
                var b = new Vector2(cr.X + cr.Width / 2f, cr.Y);

                bool parentLearned = _state?.IsLearned(parent.Id) ?? false;
                bool childLearned  = _state?.IsLearned(s.Id) ?? false;
                Color line = childLearned ? GoldBright
                           : parentLearned ? Gold
                                           : new Color(120, 100, 70, 140);
                int thick = childLearned ? 3 : 2;
                DrawConnector(a, b, line, thick);
            }
        }

        // Nodes on top
        foreach (var s in tab.Skills) DrawNode(lay, s);
    }

    private void DrawConnector(Vector2 a, Vector2 b, Color color, int thickness)
    {
        // S-curve via cubic bezier with vertical control handles.
        var c1 = new Vector2(a.X, a.Y + (b.Y - a.Y) * 0.5f);
        var c2 = new Vector2(b.X, b.Y - (b.Y - a.Y) * 0.5f);
        const int steps = 22;
        Vector2 prev = a;
        for (int i = 1; i <= steps; i++)
        {
            float t = i / (float)steps;
            float u = 1 - t;
            var p = u * u * u * a + 3 * u * u * t * c1 + 3 * u * t * t * c2 + t * t * t * b;
            DrawThickLine(prev, p, color, thickness);
            prev = p;
        }
    }

    private void DrawNode(in Layout lay, SkillDef def)
    {
        var r = NodeRect(lay, def);
        bool learned     = _state?.IsLearned(def.Id) ?? false;
        bool prereqsMet  = _state?.ArePrereqsMet(def) ?? true;
        bool affordable  = (_state != null && _inventory != null) && _state.CanAfford(def, _inventory);

        // Decide visual state
        Color fill, border, titleColor;
        if (learned)
        {
            fill = LearnedFill;
            border = GoldDim;
            titleColor = new Color(220, 208, 180);
        }
        else if (!prereqsMet)
        {
            fill = LockedFill;
            border = new Color(120, 100, 70);
            titleColor = new Color(80, 70, 50);
        }
        else if (affordable)
        {
            fill = Parchment;
            border = GoldBright;
            titleColor = Ink;
        }
        else
        {
            fill = ParchmentDark;
            border = Gold;
            titleColor = InkSoft;
        }

        // Drop shadow under interactive nodes (not pressed)
        bool pressed = def.Id == _pressedSkillId;
        if (!pressed && !learned)
            Fill(new Rectangle(r.X + 2, r.Bottom, r.Width - 2, 2), new Color(0, 0, 0, 80));

        Fill(r, fill);
        // Top highlight (1px) — subtle emboss
        Fill(new Rectangle(r.X + 1, r.Y + 1, r.Width - 2, 1), new Color(255, 255, 255, learned ? 30 : 60));
        // Bottom shade
        Fill(new Rectangle(r.X + 1, r.Bottom - 2, r.Width - 2, 1), new Color(0, 0, 0, learned ? 60 : 40));
        Border(r, border, 1);
        // Inner accent line for affordable nodes
        if (!learned && prereqsMet && affordable)
            Border(Inset(r, 2), new Color(255, 235, 180, 90), 1);

        // Title
        var f = _font!;
        var sf = _smallFont ?? f;
        string title = def.Name;
        var ts = f.MeasureString(title);
        // Truncate if too wide
        title = TruncateToWidth(f, title, r.Width - 14);
        ts = f.MeasureString(title);
        var titlePos = new Vector2((int)(r.X + (r.Width - ts.X) / 2), r.Y + 6);
        DrawText(f, title, titlePos, titleColor);

        // Cost / status — for unlearned multi-cost skills, render each cost on its own
        // line, color-coded per cost so the player sees exactly which is short.
        Color baseStatusColor;
        if (!prereqsMet) baseStatusColor = new Color(110, 90, 60);
        else baseStatusColor = affordable ? CostGood : CostBad;

        if (learned)
        {
            string statusLine = "Learned";
            var ss = sf.MeasureString(statusLine);
            var sPos = new Vector2((int)(r.X + (r.Width - ss.X) / 2),
                                   r.Bottom - 6 - (int)ss.Y);
            DrawText(sf, statusLine, sPos, new Color(180, 168, 140));
        }
        else if (def.Costs.Count == 0)
        {
            string statusLine = "Free";
            var ss = sf.MeasureString(statusLine);
            var sPos = new Vector2((int)(r.X + (r.Width - ss.X) / 2),
                                   r.Bottom - 6 - (int)ss.Y);
            DrawText(sf, statusLine, sPos, baseStatusColor);
        }
        else
        {
            int lineH = (int)sf.MeasureString("X").Y + 1;
            int totalH = lineH * def.Costs.Count;
            int yStart = r.Bottom - 5 - totalH;
            for (int i = 0; i < def.Costs.Count; i++)
            {
                var c = def.Costs[i];
                int have = c.Type == "item"
                    ? (_inventory?.GetItemCount(c.Id) ?? 0)
                    : (_state?.Events.Get(c.Id) ?? 0);
                bool ok = have >= c.Amount;
                Color cc = !prereqsMet ? new Color(110, 90, 60) : (ok ? CostGood : CostBad);
                string label = c.Type == "item" ? ShortItemName(c.Id) : EventLabel(c.Id);
                string line = $"{have}/{c.Amount} {label}";
                line = TruncateToWidth(sf, line, r.Width - 14);
                var lz = sf.MeasureString(line);
                DrawText(sf, line,
                    new Vector2((int)(r.X + (r.Width - lz.X) / 2), yStart + i * lineH), cc);
            }
        }

        // Lock icon overlay for locked nodes (small, top-right)
        if (!learned && !prereqsMet) DrawLockIcon(r.Right - 14, r.Y + 4);
        // Checkmark for learned (top-right)
        if (learned) DrawCheck(r.Right - 14, r.Y + 4);
    }

    private string FormatCosts(SkillDef def)
    {
        if (def.Costs.Count == 0) return "Free";
        var parts = new List<string>(def.Costs.Count);
        foreach (var c in def.Costs)
        {
            int have = c.Type == "item"
                ? (_inventory?.GetItemCount(c.Id) ?? 0)
                : (_state?.Events.Get(c.Id) ?? 0);
            string label = c.Type == "item" ? ShortItemName(c.Id) : EventLabel(c.Id);
            parts.Add($"{have}/{c.Amount} {label}");
        }
        return string.Join("  ", parts);
    }

    private string ShortItemName(string id)
    {
        var def = _gameData?.Items?.Get(id);
        return def?.DisplayName ?? id;
    }

    private static string EventLabel(string eventId) => eventId switch
    {
        "raise_corpse" => "raised",
        "cast_spell"   => "casts",
        _              => eventId,
    };

    private string TruncateToWidth(SpriteFont f, string text, int maxPx)
    {
        if (string.IsNullOrEmpty(text)) return text;
        // Avoid drawing chars outside the SpriteFont's range — the embedded font is
        // ASCII 32-126 and throws on anything else. Strip first, then truncate.
        text = SanitizeForFont(f, text);
        if (f.MeasureString(text).X <= maxPx) return text;
        const string suffix = "...";
        for (int i = text.Length - 1; i > 0; i--)
        {
            string trimmed = text.Substring(0, i) + suffix;
            if (f.MeasureString(trimmed).X <= maxPx) return trimmed;
        }
        return text;
    }

    private static string SanitizeForFont(SpriteFont f, string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        bool needs = false;
        for (int i = 0; i < text.Length; i++)
            if (text[i] > 126 || text[i] < 32 && text[i] != '\n')
            { needs = true; break; }
        if (!needs) return text;
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text)
            sb.Append(ch >= 32 && ch <= 126 ? ch : '?');
        return sb.ToString();
    }

    private void DrawLockIcon(int x, int y)
    {
        // tiny lock: 8x10
        Fill(new Rectangle(x + 1, y, 6, 1), BloodDark);
        Fill(new Rectangle(x, y + 1, 1, 4), BloodDark);
        Fill(new Rectangle(x + 7, y + 1, 1, 4), BloodDark);
        Fill(new Rectangle(x, y + 4, 8, 6), BloodDark);
        Fill(new Rectangle(x + 3, y + 6, 2, 2), Parchment);
    }

    private void DrawCheck(int x, int y)
    {
        Fill(new Rectangle(x, y + 4, 2, 2), GoldBright);
        Fill(new Rectangle(x + 2, y + 6, 2, 2), GoldBright);
        Fill(new Rectangle(x + 4, y + 4, 2, 2), GoldBright);
        Fill(new Rectangle(x + 6, y + 2, 2, 2), GoldBright);
        Fill(new Rectangle(x + 8, y, 2, 2), GoldBright);
    }

    private void DrawFooter(in Layout lay)
    {
        var r = lay.Footer;
        Fill(r, LeatherDark);
        Border(r, GoldDim, 1);
        var sf = _smallFont ?? _font!;
        string[] parts = {
            "[CLICK] LEARN",
            "[HOVER] DETAILS",
            "[K / ESC] CLOSE",
        };
        int slotW = r.Width / parts.Length;
        for (int i = 0; i < parts.Length; i++)
        {
            var ts = sf.MeasureString(parts[i]);
            int sx = r.X + slotW * i + (slotW - (int)ts.X) / 2;
            DrawText(sf, parts[i],
                new Vector2(sx, r.Y + (r.Height - (int)ts.Y) / 2), Gold);
        }
    }

    private void DrawTooltip(in Layout lay)
    {
        var tab = SkillBookDefs.Tabs[_activeTab];
        SkillDef? def = null;
        foreach (var s in tab.Skills) if (s.Id == _hoverSkillId) { def = s; break; }
        if (def == null) return;

        var f = _font!;
        var sf = _smallFont ?? f;

        // Wrap description to a max width
        const int maxW = 320;
        var titleSize = f.MeasureString(def.Name);
        var lines = WrapText(sf, def.Description, maxW);
        int lineH = (int)sf.MeasureString("X").Y + 1;
        int padX = 8, padY = 6;
        int w = Math.Max((int)titleSize.X + 16, maxW) + padX * 2;
        int h = padY * 2 + (int)titleSize.Y + 4 + lines.Count * lineH;

        // Add cost lines
        var costLines = new List<(string text, Color color)>();
        if (def.Costs.Count == 0) costLines.Add(("Free", CostGood));
        bool prereqsMet = _state?.ArePrereqsMet(def) ?? true;
        foreach (var c in def.Costs)
        {
            int have = c.Type == "item"
                ? (_inventory?.GetItemCount(c.Id) ?? 0)
                : (_state?.Events.Get(c.Id) ?? 0);
            bool ok = have >= c.Amount;
            string lab = c.Type == "item" ? ShortItemName(c.Id) : $"{EventLabel(c.Id)} (event)";
            costLines.Add(($"  - {have}/{c.Amount} {lab}", prereqsMet ? (ok ? CostGood : CostBad) : new Color(120, 100, 70)));
        }
        if (!prereqsMet)
            costLines.Add(("  Prerequisites not met.", new Color(168, 70, 70)));
        h += 6 + costLines.Count * lineH;

        int x = (int)_mouse.X + 16;
        int y = (int)_mouse.Y + 16;
        if (x + w > lay.Panel.Right - 6) x = (int)_mouse.X - w - 12;
        if (y + h > lay.Panel.Bottom - 6) y = (int)_mouse.Y - h - 12;
        x = Math.Max(lay.Panel.X + 6, x);
        y = Math.Max(lay.Panel.Y + 6, y);

        var rect = new Rectangle(x, y, w, h);
        Fill(new Rectangle(rect.X + 3, rect.Y + 3, rect.Width, rect.Height), new Color(0, 0, 0, 140));
        Fill(rect, LeatherMid);
        Border(rect, GoldBright, 1);

        int ty = rect.Y + padY;
        DrawText(f, def.Name, new Vector2(rect.X + padX, ty), GoldBright);
        ty += (int)titleSize.Y + 4;
        foreach (var ln in lines)
        {
            DrawText(sf, ln, new Vector2(rect.X + padX, ty), new Color(232, 220, 192));
            ty += lineH;
        }
        ty += 6;
        foreach (var (text, color) in costLines)
        {
            DrawText(sf, text, new Vector2(rect.X + padX, ty), color);
            ty += lineH;
        }
    }

    private static List<string> WrapText(SpriteFont f, string text, int maxW)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(text)) return lines;
        text = SanitizeForFont(f, text);
        var words = text.Split(' ');
        var cur = "";
        foreach (var w in words)
        {
            string trial = cur.Length == 0 ? w : cur + " " + w;
            if (f.MeasureString(trial).X > maxW)
            {
                if (cur.Length > 0) lines.Add(cur);
                cur = w;
            }
            else cur = trial;
        }
        if (cur.Length > 0) lines.Add(cur);
        return lines;
    }

    private void DrawToast(in Layout lay)
    {
        var f = _font!;
        var ts = f.MeasureString(_toast!);
        int padX = 16, padY = 6;
        var rect = new Rectangle(
            lay.Panel.X + (lay.Panel.Width - ((int)ts.X + padX * 2)) / 2,
            lay.Footer.Y - 36,
            (int)ts.X + padX * 2, (int)ts.Y + padY * 2);
        Fill(new Rectangle(rect.X + 3, rect.Y + 3, rect.Width, rect.Height), new Color(0, 0, 0, 160));
        Fill(rect, LeatherDark);
        Border(rect, GoldBright, 1);
        DrawText(f, _toast!, new Vector2(rect.X + padX, rect.Y + padY), GoldBright);
    }

    /// <summary>Soft inner shadow on the parchment to give it depth instead of a
    /// flat color. Implemented as concentric translucent rect bands — no shaders.</summary>
    private void DrawParchmentVignette(Rectangle r)
    {
        // 6 concentric shadow rings, each slightly inset and slightly stronger
        // toward the edge, so the parchment looks bowl-shaped / aged at the rim.
        const int rings = 6;
        for (int i = 0; i < rings; i++)
        {
            int alpha = 18 - i * 2; // outermost strongest
            if (alpha <= 0) break;
            var shadow = new Color(60, 38, 18, alpha);
            // top + bottom strips
            Fill(new Rectangle(r.X + i, r.Y + i, r.Width - i * 2, 1), shadow);
            Fill(new Rectangle(r.X + i, r.Bottom - 1 - i, r.Width - i * 2, 1), shadow);
            // left + right strips
            Fill(new Rectangle(r.X + i, r.Y + i + 1, 1, r.Height - i * 2 - 2), shadow);
            Fill(new Rectangle(r.Right - 1 - i, r.Y + i + 1, 1, r.Height - i * 2 - 2), shadow);
        }
    }

    /// <summary>Stable pseudo-random parchment "grain" — a sparse spray of darker
    /// 1px specks. Seeded by rect size so a given panel size always renders the
    /// same flecks (no per-frame jitter). Cheap and breaks up the flat color.</summary>
    private void DrawParchmentFlecks(Rectangle r)
    {
        int seed = unchecked(r.Width * 73856093 ^ r.Height * 19349663);
        var rnd = new Random(seed);
        int count = (r.Width * r.Height) / 1400; // density
        for (int i = 0; i < count; i++)
        {
            int x = r.X + rnd.Next(r.Width);
            int y = r.Y + rnd.Next(r.Height);
            int alpha = 18 + rnd.Next(28);
            // mix of warm dark and cool dark for variety
            Color c = (i & 1) == 0
                ? new Color(70, 50, 28, alpha)
                : new Color(40, 30, 20, alpha);
            Fill(new Rectangle(x, y, 1, 1), c);
        }
    }

    // ----- Primitive helpers -----
    private void Fill(Rectangle r, Color c) => _batch.Draw(_pixel, r, c);

    private void Border(Rectangle r, Color c, int t)
    {
        _batch.Draw(_pixel, new Rectangle(r.X, r.Y, r.Width, t), c);
        _batch.Draw(_pixel, new Rectangle(r.X, r.Bottom - t, r.Width, t), c);
        _batch.Draw(_pixel, new Rectangle(r.X, r.Y, t, r.Height), c);
        _batch.Draw(_pixel, new Rectangle(r.Right - t, r.Y, t, r.Height), c);
    }

    private static Rectangle Inset(Rectangle r, int n)
        => new(r.X + n, r.Y + n, r.Width - n * 2, r.Height - n * 2);

    private void DrawThickLine(Vector2 a, Vector2 b, Color color, int thickness)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 0.5f) return;
        float angle = MathF.Atan2(dy, dx);
        for (int t = 0; t < thickness; t++)
        {
            _batch.Draw(_pixel,
                new Rectangle((int)a.X, (int)a.Y - thickness / 2 + t, (int)len, 1),
                null, color, angle, Vector2.Zero, SpriteEffects.None, 0f);
        }
    }

    private void DrawShadowText(SpriteFont f, string text, Vector2 pos, Color color)
    {
        text = SanitizeForFont(f, text);
        _batch.DrawString(f, text, new Vector2(pos.X + 1, pos.Y + 1), new Color(0, 0, 0, 160));
        _batch.DrawString(f, text, pos, color);
    }

    /// <summary>SpriteBatch.DrawString wrapped with font sanitization. Some skill
    /// descriptions and item display names use Unicode that the embedded SpriteFont
    /// can't render — that throws and crashes. Use this for any text that may
    /// originate from JSON.</summary>
    private void DrawText(SpriteFont f, string text, Vector2 pos, Color color)
        => _batch.DrawString(f, SanitizeForFont(f, text), pos, color);

    // ----- Test hooks (for scenarios) -----
    public int ActiveTab => _activeTab;
    public void SetActiveTab(int i)
    {
        if (i < 0 || i >= SkillBookDefs.Tabs.Count) return;
        _activeTab = i;
    }
    public bool TryLearnById(string id, double timeSec = 0)
    {
        foreach (var tab in SkillBookDefs.Tabs)
        {
            int idx = tab.IndexOf(id);
            if (idx >= 0)
            {
                TryLearnFromUI(tab.Skills[idx], timeSec);
                return _state?.IsLearned(id) ?? false;
            }
        }
        return false;
    }
}
