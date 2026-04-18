using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Core;

namespace Necroking.Render;

/// <summary>
/// Skill tree panel -- "Grimoire of the Unhallowed".
/// Modal panel toggled with K. Three schools (Bone / Soul / Shadow) of nodes
/// with prereq connectors, allocate-by-clicking. Visual language adapted from
/// the Claude Design mock: dark leather tome chrome, parchment center, embossed
/// node medallions, blood-red connectors, candle-warm accents.
///
/// !!! VISUAL FIDELITY NOT MATCHED -- USES UNVERIFIED UIGfx HELPERS !!!
/// =====================================================================
/// This panel uses the gradient/shadow/glow helpers in <see cref="UIGfx"/>,
/// every one of which is flagged as unverified. See the warning at the top
/// of UIGfx.cs for why. The user reviewed the resulting render and judged
/// that it does not match the original CSS design -- in particular the
/// gradients band, the glows have halos, and the leather/parchment textures
/// look more like artifacts than texture.
///
/// What does work in this file: the layout (columns, sidebar, footer all
/// scale with screen size), the click crash fix (em-dash removed from Warn),
/// the K-toggle flow, the hover tooltip, the prerequisite logic, and the
/// scenario hooks (TryAllocate / TryClickLocked).
///
/// If you copy patterns out of this file into a new UI, copy the layout and
/// state machinery -- not the visual effects, until UIGfx is rewritten or
/// replaced with a proper shader-based path.
/// </summary>
public class SkillTreePanel
{
    public bool IsVisible { get; private set; }

    public void Toggle() { IsVisible = !IsVisible; if (IsVisible) EnsureInit(); }
    public void Open()   { IsVisible = true;       EnsureInit(); }
    public void Close()  { IsVisible = false; }

    private SpriteBatch _batch = null!;
    private Texture2D _pixel = null!;
    private SpriteFont? _font;
    private SpriteFont? _smallFont;
    private SpriteFont? _largeFont;
    private UIShaders? _fx;

    public void Init(SpriteBatch batch, Texture2D pixel,
        SpriteFont? font, SpriteFont? smallFont, SpriteFont? largeFont)
    {
        _batch = batch;
        _pixel = pixel;
        _font = font;
        _smallFont = smallFont;
        _largeFont = largeFont;
    }

    public void SetUIShaders(UIShaders fx) { _fx = fx; }

    // ----- Palette -----
    private static readonly Color Ink         = new(23, 17, 13);
    private static readonly Color Ink2        = new(36, 26, 19);
    private static readonly Color Parchment   = new(232, 220, 192);
    private static readonly Color Parchment2  = new(214, 201, 168);
    private static readonly Color ParchShadow = new(168, 148, 102);
    private static readonly Color Blood       = new(107, 30, 30);
    private static readonly Color BloodDark   = new(58, 13, 13);
    private static readonly Color BloodBright = new(139, 26, 26);
    private static readonly Color Bruise      = new(74, 44, 90);
    private static readonly Color Bone        = new(201, 184, 138);
    private static readonly Color Gold        = new(138, 109, 50);
    private static readonly Color GoldBright  = new(201, 168, 96);
    private static readonly Color Verdigris   = new(61, 90, 74);

    private static readonly Color LeatherDark = new(26, 13, 8);
    private static readonly Color LeatherMid  = new(42, 26, 18);
    private static readonly Color LeatherDeep = new(10, 5, 4);
    private static readonly Color FrameRivet  = new(58, 42, 24);

    // ----- Layout -----
    // The panel scales with screen size. Most positioning is computed from
    // the panel rect at draw time; these are the only fixed sizes.
    private const int NodeSize = 68;
    private const int RowH = 102;
    private const int TreeHeaderH = 108; // headers + chapter strip above first row
    private const int FooterH = 32;
    private const int SidebarPad = 16;

    private const int TotalPoints = 28;

    private struct Layout
    {
        public Rectangle Panel;
        public Rectangle Sidebar;
        public Rectangle Tree;
        public int SchoolColW;
        public int SchoolGutter;
        public int TreeOriginX; // left edge of first school column inner area
        public int TreeOriginY; // top of first row of nodes
    }

    private Layout BuildLayout(int screenW, int screenH)
    {
        var panel = PanelRect(screenW, screenH);

        int contentX = panel.X + 18;
        int contentY = panel.Y + 18;
        int contentW = panel.Width - 36;
        int contentH = panel.Height - 36 - FooterH - 6;

        // Sidebar takes ~22% of inner width (clamped to a sensible range).
        int sidebarW = Math.Clamp((int)(contentW * 0.22f), 260, 340);
        int gap = 22;

        var sidebar = new Rectangle(contentX, contentY, sidebarW, contentH);
        var tree = new Rectangle(contentX + sidebarW + gap, contentY,
                                 contentW - sidebarW - gap, contentH);

        // Three school columns + 2 gutters fill the tree's inner area.
        int treeInnerPad = 18;
        int treeInnerW = tree.Width - treeInnerPad * 2;
        int gutter = 18;
        int colW = (treeInnerW - gutter * 2) / 3;

        int treeOriginX = tree.X + treeInnerPad;
        int treeOriginY = tree.Y + TreeHeaderH;

        return new Layout
        {
            Panel = panel,
            Sidebar = sidebar,
            Tree = tree,
            SchoolColW = colW,
            SchoolGutter = gutter,
            TreeOriginX = treeOriginX,
            TreeOriginY = treeOriginY,
        };
    }

    private struct School
    {
        public string Id;
        public string Name;
        public string Subtitle;
        public string Motto;
        public Color  Accent;
    }

    private static readonly School[] Schools =
    {
        new() { Id = "bone",   Name = "Bone",   Subtitle = "Of Marrow & Spine",
                Motto = "From the ossuary, an army.",       Accent = new(201, 184, 138) },
        new() { Id = "soul",   Name = "Soul",   Subtitle = "Of Wraith & Phylactery",
                Motto = "The breath is but a lease.",       Accent = new(122,  90, 146) },
        new() { Id = "shadow", Name = "Shadow", Subtitle = "Of Curse & Creeping Dark",
                Motto = "What is spoken in dusk, festers.", Accent = new(139,  26,  26) },
    };

    // Brief one-line dependency: parent id -> required ranks in parent.
    private struct Prereq { public string Id; public int Req; public Prereq(string id, int req){Id=id;Req=req;} }

    private struct Node
    {
        public string Id;
        public string School;
        public int    Tier;   // row 0..4
        public int    Col;    // sub-col 1..3
        public int    Max;
        public string Name;
        public string Glyph;  // short glyph drawn inside the medallion
        public string Flavor;
        public string[] Stats;
        public Prereq[] Prereqs;
    }

    // Sigil glyphs are short ASCII labels — the embedded SpriteFont only
    // ships ASCII 32-126, so any U+0080+ character throws on MeasureString.
    private static readonly Node[] Nodes = new[]
    {
        // ===== Bone =====
        new Node { Id="b_rattle",  School="bone",   Tier=0, Col=2, Max=1, Name="The First Rattling",
                   Glyph="SK",
                   Flavor="A child of bone answers your call, knitted from chapel-floor dust.",
                   Stats=new[]{ "Summon 1 Skeleton Servitor (60s)", "Grants access to the Bone path" },
                   Prereqs=Array.Empty<Prereq>() },
        new Node { Id="b_shard",   School="bone",   Tier=1, Col=1, Max=3, Name="Marrow Shards",
                   Glyph="><",
                   Flavor="Ribs erupt from the soil, still wet.",
                   Stats=new[]{ "+6 piercing damage per rank", "8m radius", "Cooldown 12s" },
                   Prereqs=new[]{ new Prereq("b_rattle", 1) } },
        new Node { Id="b_brittle", School="bone",   Tier=1, Col=3, Max=3, Name="Brittle Ward",
                   Glyph="<>",
                   Flavor="A lattice of fingers, woven before the face.",
                   Stats=new[]{ "+5% physical resistance per rank", "Stacks with Bone Armor" },
                   Prereqs=new[]{ new Prereq("b_rattle", 1) } },
        new Node { Id="b_legion",  School="bone",   Tier=2, Col=2, Max=5, Name="Ossuary Legion",
                   Glyph="WW",
                   Flavor="The crypt empties. The field does not.",
                   Stats=new[]{ "+1 max Skeleton per rank", "+10% skeleton damage per rank" },
                   Prereqs=new[]{ new Prereq("b_shard", 1), new Prereq("b_brittle", 1) } },
        new Node { Id="b_knight",  School="bone",   Tier=3, Col=1, Max=1, Name="Knight of Last Breath",
                   Glyph="++",
                   Flavor="He wore this armor when it still held flesh.",
                   Stats=new[]{ "Summon Bone Knight (permanent)", "Taunts in 6m", "+50% HP" },
                   Prereqs=new[]{ new Prereq("b_legion", 3) } },
        new Node { Id="b_calcify", School="bone",   Tier=3, Col=3, Max=3, Name="Calcify",
                   Glyph="\\|/",
                   Flavor="Their joints lock. Their screams do not.",
                   Stats=new[]{ "Root target 2s +0.5s per rank", "15 damage on break" },
                   Prereqs=new[]{ new Prereq("b_legion", 3) } },
        new Node { Id="b_final",   School="bone",   Tier=4, Col=2, Max=1, Name="Avatar of the Ossuary",
                   Glyph="^^",
                   Flavor="You no longer require your own skeleton. Shed it.",
                   Stats=new[]{ "Become a Bone Colossus for 30s", "Immune to physical during" },
                   Prereqs=new[]{ new Prereq("b_knight", 1), new Prereq("b_calcify", 2) } },

        // ===== Soul =====
        new Node { Id="s_siphon",  School="soul",   Tier=0, Col=2, Max=1, Name="Soul Siphon",
                   Glyph="(O)",
                   Flavor="Drink deep. The dying rarely object.",
                   Stats=new[]{ "Absorb 8% damage as Soul Essence", "Grants access to the Soul path" },
                   Prereqs=Array.Empty<Prereq>() },
        new Node { Id="s_whisper", School="soul",   Tier=1, Col=1, Max=3, Name="Whispering Dead",
                   Glyph="...",
                   Flavor="They murmur the names of the soon-forgotten.",
                   Stats=new[]{ "Reveal enemies in 15m", "+5% crit on whispered foes per rank" },
                   Prereqs=new[]{ new Prereq("s_siphon", 1) } },
        new Node { Id="s_wraith",  School="soul",   Tier=1, Col=3, Max=3, Name="Unbound Wraith",
                   Glyph="~~",
                   Flavor="Tethered to nothing but your hunger.",
                   Stats=new[]{ "Summon Wraith: 20 shadow DPS", "+1 wraith at rank 3" },
                   Prereqs=new[]{ new Prereq("s_siphon", 1) } },
        new Node { Id="s_phylact", School="soul",   Tier=2, Col=2, Max=1, Name="Phylactery",
                   Glyph="[ ]",
                   Flavor="Sealed in lead, whispered to nightly.",
                   Stats=new[]{ "On death, resurrect with 50% HP", "Cooldown 5 min" },
                   Prereqs=new[]{ new Prereq("s_whisper", 1), new Prereq("s_wraith", 1) } },
        new Node { Id="s_harvest", School="soul",   Tier=3, Col=1, Max=3, Name="Harvest of Breath",
                   Glyph="\\_",
                   Flavor="The last exhale is the sweetest.",
                   Stats=new[]{ "Killing blows restore 5% HP per rank", "+1 Soul Charge" },
                   Prereqs=new[]{ new Prereq("s_phylact", 1) } },
        new Node { Id="s_chain",   School="soul",   Tier=3, Col=3, Max=3, Name="Chain the Severed",
                   Glyph="OO",
                   Flavor="A leash for the recently parted.",
                   Stats=new[]{ "Enslave slain enemy as Thrall (20s)", "+5s duration per rank" },
                   Prereqs=new[]{ new Prereq("s_phylact", 1) } },
        new Node { Id="s_final",   School="soul",   Tier=4, Col=2, Max=1, Name="Lichdom",
                   Glyph="WW",
                   Flavor="You shed the tyranny of flesh. There is paperwork.",
                   Stats=new[]{ "Immune to mortal ailments", "+25% to all Soul abilities" },
                   Prereqs=new[]{ new Prereq("s_harvest", 2), new Prereq("s_chain", 2) } },

        // ===== Shadow =====
        new Node { Id="h_veil",    School="shadow", Tier=0, Col=2, Max=1, Name="The Veil",
                   Glyph="==",
                   Flavor="Dusk, folded twice. Pocketed.",
                   Stats=new[]{ "Translucent for 6s, +40% MS", "Grants access to the Shadow path" },
                   Prereqs=Array.Empty<Prereq>() },
        new Node { Id="h_blight",  School="shadow", Tier=1, Col=1, Max=3, Name="Rotting Word",
                   Glyph="*",
                   Flavor="Said once. It continues itself.",
                   Stats=new[]{ "Curse: 12 shadow DPS for 8s +2s/rank", "Spreads on death" },
                   Prereqs=new[]{ new Prereq("h_veil", 1) } },
        new Node { Id="h_ember",   School="shadow", Tier=1, Col=3, Max=3, Name="Black Ember",
                   Glyph="oO",
                   Flavor="Cold to the touch. Colder within.",
                   Stats=new[]{ "Projectile: 25 dmg +8 per rank", "Ignites enemies in shadow" },
                   Prereqs=new[]{ new Prereq("h_veil", 1) } },
        new Node { Id="h_terror",  School="shadow", Tier=2, Col=2, Max=3, Name="Unnamed Terror",
                   Glyph="oo",
                   Flavor="They see a thing you cannot. They know its name.",
                   Stats=new[]{ "Fear in 8m for 3s +0.5s/rank", "Breaks on damage at rank 1" },
                   Prereqs=new[]{ new Prereq("h_blight", 1), new Prereq("h_ember", 1) } },
        new Node { Id="h_drink",   School="shadow", Tier=3, Col=1, Max=3, Name="Drink the Name",
                   Glyph="\\J/",
                   Flavor="After this, none will remember them. Not even stone.",
                   Stats=new[]{ "Execute: <20% HP enemies die", "+5% threshold per rank" },
                   Prereqs=new[]{ new Prereq("h_terror", 2) } },
        new Node { Id="h_seven",   School="shadow", Tier=3, Col=3, Max=1, Name="Seven Hands of Dusk",
                   Glyph="VII",
                   Flavor="Count them. Then count them again.",
                   Stats=new[]{ "Summon 7 Shade Hands in a line", "Cooldown 30s" },
                   Prereqs=new[]{ new Prereq("h_terror", 2) } },
        new Node { Id="h_final",   School="shadow", Tier=4, Col=2, Max=1, Name="Umbra Incarnate",
                   Glyph="(.)",
                   Flavor="The lamp is out. The room remembers being lit.",
                   Stats=new[]{ "Untargetable by non-shadow sources", "All curses cost 0 essence" },
                   Prereqs=new[]{ new Prereq("h_drink", 2), new Prereq("h_seven", 1) } },
    };

    // Allocation state — points per node id, and unspent point pool.
    private readonly Dictionary<string, int> _ranks = new();
    private int _points = TotalPoints;

    // Transient warning toast
    private string? _warn;
    private double _warnUntil;

    private bool _initState;
    private void EnsureInit()
    {
        if (_initState) return;
        _initState = true;
        foreach (var n in Nodes) _ranks[n.Id] = 0;
    }

    private bool IsUnlocked(in Node n)
    {
        if (n.Prereqs.Length == 0) return true;
        foreach (var p in n.Prereqs)
            if (_ranks.GetValueOrDefault(p.Id) < p.Req) return false;
        return true;
    }

    /// <summary>Bounds of the entire panel — used by the main game to detect
    /// "mouse over UI" and to swallow world clicks while open.</summary>
    public bool ContainsMouse(int screenW, int screenH, int mx, int my)
    {
        if (!IsVisible) return false;
        var r = PanelRect(screenW, screenH);
        return mx >= r.X && mx < r.Right && my >= r.Y && my < r.Bottom;
    }

    private Rectangle PanelRect(int screenW, int screenH)
    {
        // Scale panel to fill most of the screen, keeping a margin.
        // Cap aspect so on very wide screens it doesn't stretch too far.
        int marginX = Math.Max(40, screenW / 24);
        int marginY = Math.Max(30, screenH / 22);
        int totalW = screenW - marginX * 2;
        int totalH = screenH - marginY * 2;
        // Cap aspect ratio to ~1.85:1 so the tree stays readable on ultrawide.
        int maxW = (int)(totalH * 1.85f);
        if (totalW > maxW) totalW = maxW;
        // Provide a sensible minimum so very small windows still render reasonably.
        totalW = Math.Max(totalW, 880);
        totalH = Math.Max(totalH, 600);
        int x = (screenW - totalW) / 2;
        int y = (screenH - totalH) / 2;
        return new Rectangle(x, y, totalW, totalH);
    }

    public void Update(InputState input, int screenW, int screenH, double timeSec)
    {
        if (!IsVisible) return;
        EnsureInit();

        if (_warn != null && timeSec >= _warnUntil) _warn = null;

        // Note: K-toggle is handled by Game1 (so opening on K doesn't immediately
        // close on the same input snapshot). Escape is handled in Game1's Escape
        // chain. This Update only handles in-panel mouse interaction.

        var lay = BuildLayout(screenW, screenH);
        _cachedLayout = lay;
        int mx = (int)input.MousePos.X;
        int my = (int)input.MousePos.Y;

        if (!lay.Panel.Contains(mx, my)) return;
        input.MouseOverUI = true;

        // Reset button
        var resetRect = ResetButtonRect(lay);
        if (resetRect.Contains(mx, my))
        {
            if (input.LeftPressed && !input.IsMouseConsumed)
            {
                foreach (var n in Nodes) _ranks[n.Id] = 0;
                _points = TotalPoints;
                input.ConsumeMouse();
            }
            return;
        }

        // Node hit-test
        for (int i = 0; i < Nodes.Length; i++)
        {
            var n = Nodes[i];
            var (nx, ny) = NodeCenter(lay, n);
            int dx = mx - nx; int dy = my - ny;
            if (dx * dx + dy * dy > (NodeSize / 2) * (NodeSize / 2)) continue;

            bool unlocked = IsUnlocked(n);
            int rank = _ranks[n.Id];

            if (input.LeftPressed && !input.IsMouseConsumed)
            {
                if (!unlocked) { Warn("Sealed -- prerequisites unmet", timeSec); }
                else if (rank >= n.Max) { Warn("Already mastered", timeSec); }
                else if (_points <= 0) { Warn("No unspent souls", timeSec); }
                else { _ranks[n.Id] = rank + 1; _points--; }
                input.ConsumeMouse();
            }
            else if (input.RightPressed && !input.IsMouseConsumed)
            {
                if (rank > 0)
                {
                    _ranks[n.Id] = rank - 1;
                    bool wouldBreak = false;
                    foreach (var x in Nodes)
                        if (_ranks[x.Id] > 0 && !IsUnlocked(x)) { wouldBreak = true; break; }
                    if (wouldBreak)
                    {
                        _ranks[n.Id] = rank;
                        Warn($"Revoke dependents first: {n.Name}", timeSec);
                    }
                    else { _points++; }
                }
                input.ConsumeMouse();
            }
            break;
        }
    }

    private void Warn(string msg, double timeSec) { _warn = msg; _warnUntil = timeSec + 2.0; }

    /// <summary>Test hook: simulate clicking a node by id. Returns true if a rank was added.</summary>
    public bool TryAllocate(string nodeId, double timeSec = 0)
    {
        EnsureInit();
        Node? found = null;
        foreach (var n in Nodes) if (n.Id == nodeId) { found = n; break; }
        if (found == null) return false;
        var node = found.Value;
        if (!IsUnlocked(node)) { Warn("Sealed -- prerequisites unmet", timeSec); return false; }
        int rank = _ranks[node.Id];
        if (rank >= node.Max) { Warn("Already mastered", timeSec); return false; }
        if (_points <= 0)    { Warn("No unspent souls", timeSec); return false; }
        _ranks[node.Id] = rank + 1;
        _points--;
        return true;
    }

    /// <summary>Test hook: clicking a locked node (used to repro the original crash path).</summary>
    public void TryClickLocked(double timeSec = 0)
    {
        EnsureInit();
        // Find a node that's locked. Tier > 0 in any school works because nothing is allocated.
        foreach (var n in Nodes) if (!IsUnlocked(n)) { Warn("Sealed -- prerequisites unmet", timeSec); return; }
    }

    public void Draw(int screenW, int screenH)
    {
        if (!IsVisible || _font == null) return;
        EnsureInit();

        // Dim the world behind the panel
        _batch.Draw(_pixel, new Rectangle(0, 0, screenW, screenH), new Color(0, 0, 0, 180));

        var lay = BuildLayout(screenW, screenH);
        _cachedLayout = lay;
        DrawTomeChrome(lay.Panel);
        DrawSidebar(lay);
        DrawTreePanel(lay);
        DrawFooter(lay);
        if (_warn != null) DrawWarningToast(lay.Panel, _warn);
        DrawHoverTooltip(lay.Panel, screenW, screenH);
    }

    // Stored mouse position for tooltip (set in Update)
    private Vector2 _mouse;
    private Node? _hoverNode;
    private Layout _cachedLayout;

    private (int x, int y) NodeCenter(in Layout lay, Node n)
    {
        int schoolIdx = SchoolIndex(n.School);
        int colX = lay.TreeOriginX + schoolIdx * (lay.SchoolColW + lay.SchoolGutter);
        // Sub-columns 1..3 within each school's column.
        int subCenterX = colX + (int)((n.Col - 0.5f) * (lay.SchoolColW / 3f));
        int y = lay.TreeOriginY + n.Tier * RowH + NodeSize / 2;
        return (subCenterX, y);
    }

    private static int SchoolIndex(string s) => s == "bone" ? 0 : s == "soul" ? 1 : 2;

    // ----- Tome chrome -----
    private void DrawTomeChrome(Rectangle r)
    {
        // Outer leather: radial gradient (lighter top-left, fading to deep).
        // Use UV-space radial: center in UV, radius in UV.
        if (_fx != null)
        {
            _fx.DrawRadialGradient(_batch, r,
                new Color(42, 26, 18), new Color(10, 5, 4),
                new Vector2(0.3f, 0.2f), 0.85f);
        }
        else
        {
            _batch.Draw(_pixel, r, LeatherDark);
        }
        // Drop the cross-hatch -- it reads as a dot grid, not leather.
        if (_fx != null)
            _fx.DrawInsetShadow(_batch, r, new Color(0, 0, 0, 200), 40);

        DrawBorder(r.X, r.Y, r.Width, r.Height, FrameRivet, 2);
        DrawBorder(r.X + 4, r.Y + 4, r.Width - 8, r.Height - 8, new Color(58, 42, 24), 1);

        // Corner brackets (gold)
        int cb = 26;
        DrawCornerBracket(r.X + 2,            r.Y + 2,             cb, false, false);
        DrawCornerBracket(r.X + r.Width - cb - 2, r.Y + 2,         cb, true,  false);
        DrawCornerBracket(r.X + 2,            r.Y + r.Height - cb - 2, cb, false, true);
        DrawCornerBracket(r.X + r.Width - cb - 2, r.Y + r.Height - cb - 2, cb, true,  true);

        // Top title plate -- gold-bronze-dark gradient (matches CSS linear-gradient)
        string title = "+  SKILLS - FELL ARTS  +";
        var titleFont = _largeFont ?? _font!;
        var ts = titleFont.MeasureString(title);
        int plateW = (int)ts.X + 64;
        int plateH = 28;
        var plate = new Rectangle(r.X + (r.Width - plateW) / 2, r.Y - plateH / 2, plateW, plateH);
        // Soft drop shadow under the plate (draw FIRST so plate covers its inner area)
        if (_fx != null)
        {
            int sSoft = 8;
            var sOuter = new Rectangle(plate.X - sSoft, plate.Y - sSoft,
                                       plate.Width + sSoft * 2, plate.Height + sSoft * 2);
            _fx.DrawDropShadow(_batch, sOuter, plate,
                Color.Transparent, new Color(0, 0, 0, 180), sSoft);
        }
        if (_fx != null)
        {
            _fx.DrawVertical3StopGradient(_batch, plate,
                new Color(201, 168, 96),    // top: bright gold
                new Color(138, 109, 50),    // mid: bronze
                new Color(90,  66, 32),     // bottom: dark
                0.6f);
        }
        else
        {
            _batch.Draw(_pixel, plate, new Color(138, 109, 50));
        }
        // Top inner highlight
        _batch.Draw(_pixel, new Rectangle(plate.X + 1, plate.Y + 1, plate.Width - 2, 1),
            new Color(255, 240, 200, 100));
        DrawBorder(plate.X, plate.Y, plate.Width, plate.Height, LeatherDeep, 1);

        // Simple 1px text shadow for legibility (not the buggy triple-print emboss)
        var titlePos = new Vector2((int)(plate.X + (plate.Width - ts.X) / 2),
                                   (int)(plate.Y + (plate.Height - ts.Y) / 2));
        // (kept here just to avoid orphaning variables; emboss replaced with flat)
        UIGfx.DrawTextEmbossed(_batch, titleFont, title, titlePos,
            new Color(26, 13, 8),                      // ink
            new Color(255, 240, 200, 100),             // highlight (1px up)
            new Color(0, 0, 0, 80));                   // shadow (2px down)
    }

    private void DrawCornerBracket(int x, int y, int size, bool flipX, bool flipY)
    {
        // Gold L-bracket with rivets
        int bandT = 6;
        // Horizontal arm
        _batch.Draw(_pixel,
            new Rectangle(x, flipY ? y + size - bandT : y, size, bandT), Gold);
        // Vertical arm
        _batch.Draw(_pixel,
            new Rectangle(flipX ? x + size - bandT : x, y, bandT, size), Gold);
        // Rivets along the arms
        for (int i = 0; i < 3; i++)
        {
            int rx = flipX ? x + size - 2 - i * 8 : x + 2 + i * 8;
            int ry = flipY ? y + size - bandT / 2 - 1 : y + bandT / 2 - 1;
            _batch.Draw(_pixel, new Rectangle(rx, ry, 2, 2), LeatherDeep);
            int rx2 = flipX ? x + size - bandT / 2 - 1 : x + bandT / 2 - 1;
            int ry2 = flipY ? y + size - 2 - i * 8 : y + 2 + i * 8;
            _batch.Draw(_pixel, new Rectangle(rx2, ry2, 2, 2), LeatherDeep);
        }
    }

    // ----- Sidebar -----
    private void DrawSidebar(in Layout lay)
    {
        var r = lay.Sidebar;
        // Parchment background -- radial gradient (light at top, tan at edges)
        if (_fx != null)
        {
            _fx.DrawRadialGradient(_batch, r,
                new Color(239, 227, 196), new Color(179, 156, 110),
                new Vector2(0.5f, 0.2f), 1.1f);
            _fx.DrawInsetShadow(_batch, r, new Color(90, 60, 20, 220), 42);
        }
        else
        {
            _batch.Draw(_pixel, r, Parchment);
        }
        // Outer rivet frame (the CSS `0 0 0 6px #1a120a, 0 0 0 8px #3a2a18`)
        DrawBorder(r.X, r.Y, r.Width, r.Height, FrameRivet, 1);
        DrawBorder(r.X - 4, r.Y - 4, r.Width + 8, r.Height + 8, LeatherDeep, 2);
        DrawBorder(r.X - 6, r.Y - 6, r.Width + 12, r.Height + 12, FrameRivet, 1);

        var f = _font!;
        var sf = _smallFont ?? f;
        var lf = _largeFont ?? f;

        int cx = r.X + r.Width / 2;
        int y = r.Y + 18;

        // Title block -- embossed
        string title = "Grimoire";
        var ts = lf.MeasureString(title);
        UIGfx.DrawTextEmbossed(_batch, lf, title,
            new Vector2((int)(cx - ts.X / 2), y),
            BloodDark,
            new Color(255, 240, 200, 110),
            new Color(0, 0, 0, 60));
        y += (int)ts.Y + 2;
        string sub = "OF THE UNHALLOWED";
        var ss = sf.MeasureString(sub);
        _batch.DrawString(sf, sub, new Vector2((int)(cx - ss.X / 2), y), Gold);
        y += (int)ss.Y + 14;

        // Portrait box -- radial gradient (purple bruise center to black)
        int portraitH = Math.Min(200, (int)(r.Height * 0.32f));
        var portrait = new Rectangle(r.X + SidebarPad, y, r.Width - SidebarPad * 2, portraitH);
        if (_fx != null)
        {
            _fx.DrawRadialGradient(_batch, portrait,
                new Color(58, 42, 64), new Color(0, 0, 0),
                new Vector2(0.5f, 0.3f), 0.9f);
        }
        else
        {
            _batch.Draw(_pixel, portrait, new Color(26, 15, 26));
        }
        DrawBorder(portrait.X, portrait.Y, portrait.Width, portrait.Height, FrameRivet, 1);
        DrawSilhouette(portrait);
        // Inner shadow on portrait (shader-based soft inset)
        if (_fx != null)
            _fx.DrawInsetShadow(_batch, portrait, new Color(0, 0, 0, 220), 24);

        var plate = new Rectangle(portrait.X + 6, portrait.Bottom - 22, portrait.Width - 12, 16);
        _batch.Draw(_pixel, plate, new Color(20, 12, 8, 220));
        DrawBorder(plate.X, plate.Y, plate.Width, plate.Height, FrameRivet, 1);
        _batch.DrawString(sf, "MORVELLAN", new Vector2(plate.X + 6, plate.Y + 2), Parchment);
        string lvl = "LVL 42";
        var lvlSize = sf.MeasureString(lvl);
        _batch.DrawString(sf, lvl,
            new Vector2((int)(plate.Right - 6 - lvlSize.X), plate.Y + 2), BloodBright);
        y = portrait.Bottom + 14;

        // Points block -- vertical gradient (dark brown to deeper)
        var pBox = new Rectangle(r.X + SidebarPad, y, r.Width - SidebarPad * 2, 92);
        if (_fx != null)
            _fx.DrawVerticalGradient(_batch, pBox, new Color(26, 18, 10), new Color(11, 8, 5));
        else
            _batch.Draw(_pixel, pBox, LeatherDark);
        DrawBorder(pBox.X, pBox.Y, pBox.Width, pBox.Height, FrameRivet, 1);
        string head = "UNSPENT SOULS";
        var hs = sf.MeasureString(head);
        _batch.DrawString(sf, head,
            new Vector2((int)(pBox.X + (pBox.Width - hs.X) / 2), pBox.Y + 8), Gold);
        string nStr = _points.ToString();
        var ns = lf.MeasureString(nStr);
        Color pColor = _points > 0 ? new Color(240, 208, 96) : new Color(90, 74, 42);
        var numPos = new Vector2((int)(pBox.X + (pBox.Width - ns.X) / 2), pBox.Y + 26);
        // Big number golden glow when there are points to spend
        if (_points > 0)
        {
            // Circular soft glow: 8 directions per ring, alpha falls off with radius.
            for (int g = 6; g >= 1; g--)
            {
                byte a = (byte)Math.Min(255, (7 - g) * 16);
                var glow = new Color((byte)240, (byte)208, (byte)96, a);
                for (int d = 0; d < 8; d++)
                {
                    float ang = d * MathF.PI / 4f;
                    float dx = MathF.Cos(ang) * g;
                    float dy = MathF.Sin(ang) * g;
                    _batch.DrawString(lf, nStr,
                        new Vector2(numPos.X + (int)dx, numPos.Y + (int)dy), glow);
                }
            }
        }
        _batch.DrawString(lf, nStr, numPos, pColor);
        string of = $"of {TotalPoints} harvested";
        var os = sf.MeasureString(of);
        _batch.DrawString(sf, of,
            new Vector2((int)(pBox.X + (pBox.Width - os.X) / 2), pBox.Y + pBox.Height - 18), ParchShadow);
        y = pBox.Bottom + 14;

        // Per-school bars -- gradient fills, tick overlay
        foreach (var s in Schools)
        {
            int spent = 0; int max = 0;
            foreach (var nd in Nodes)
                if (nd.School == s.Id) { spent += _ranks[nd.Id]; max += nd.Max; }

            _batch.DrawString(sf, s.Name.ToUpper(),
                new Vector2(r.X + SidebarPad, y), Ink2);
            string txt = $"{spent}/{max}";
            var tsz = sf.MeasureString(txt);
            _batch.DrawString(sf, txt,
                new Vector2((int)(r.Right - SidebarPad - tsz.X), y), Ink2);
            y += 14;
            var bar = new Rectangle(r.X + SidebarPad, y, r.Width - SidebarPad * 2, 8);
            _batch.Draw(_pixel, bar, LeatherDark);
            DrawBorder(bar.X, bar.Y, bar.Width, bar.Height, FrameRivet, 1);
            float pct = max > 0 ? spent / (float)max : 0f;
            int fillW = Math.Max(0, (int)(bar.Width * pct));
            if (fillW > 0)
            {
                var fillRect = new Rectangle(bar.X, bar.Y, fillW, bar.Height);
                if (_fx != null)
                {
                    _fx.DrawHorizontalGradient(_batch, fillRect,
                        new Color(s.Accent.R, s.Accent.G, s.Accent.B, (byte)170), s.Accent);
                }
                else
                {
                    _batch.Draw(_pixel, fillRect, s.Accent);
                }
                _batch.Draw(_pixel, new Rectangle(fillRect.X, fillRect.Y, fillRect.Width, 1),
                    new Color(255, 255, 255, 40));
            }
            // Tick marks (still fine from UIGfx -- simple and not visually broken)
            UIGfx.DrawRepeatingVerticalTicks(_batch, _pixel, bar, new Color(0, 0, 0, 110), 10);
            y += 16;
        }

        // Total spent
        int spentTotal = 0;
        foreach (var nd in Nodes) spentTotal += _ranks[nd.Id];
        string spentLine = $"+ {spentTotal} POINTS INSCRIBED +";
        var sps = sf.MeasureString(spentLine);
        _batch.DrawString(sf, spentLine,
            new Vector2((int)(cx - sps.X / 2), y + 6), Ink2);

        // Reset button -- vertical gradient (blood to dark blood) + inner highlight
        var rb = ResetButtonRect(lay);
        bool hover = rb.Contains((int)_mouse.X, (int)_mouse.Y);
        // Soft drop shadow first so fill covers its inner area
        if (_fx != null)
        {
            int sSoft = 6;
            var sOuter = new Rectangle(rb.X - sSoft, rb.Y - sSoft,
                                       rb.Width + sSoft * 2, rb.Height + sSoft * 2);
            _fx.DrawDropShadow(_batch, sOuter, rb,
                Color.Transparent, new Color(0, 0, 0, 160), sSoft);
            _fx.DrawVerticalGradient(_batch, rb,
                hover ? new Color(110, 28, 28) : new Color(58, 13, 13),
                hover ? new Color(58,  16, 16) : new Color(26,  5,  5));
        }
        else
        {
            _batch.Draw(_pixel, rb, hover ? new Color(120, 36, 36) : BloodDark);
        }
        // Top highlight stripe
        _batch.Draw(_pixel, new Rectangle(rb.X + 1, rb.Y + 1, rb.Width - 2, 1),
            new Color(180, 80, 80, 100));
        DrawBorder(rb.X, rb.Y, rb.Width, rb.Height, Blood, 1);
        string btn = "*  EFFACE THE SIGILS  *";
        var bs = f.MeasureString(btn);
        _batch.DrawString(f, btn,
            new Vector2((int)(rb.X + (rb.Width - bs.X) / 2),
                       (int)(rb.Y + (rb.Height - bs.Y) / 2)), Parchment);

        // Flavor quote at the bottom
        int flavorY = rb.Bottom + 8;
        string[] lines = {
            "\"What is spent in youth,",
            "is spent in marrow.\"",
            "-- THIERREN, IV.XII"
        };
        for (int i = 0; i < lines.Length; i++)
        {
            var lz = sf.MeasureString(lines[i]);
            _batch.DrawString(sf, lines[i],
                new Vector2((int)(cx - lz.X / 2), flavorY + i * 14),
                new Color(36, 26, 19, 160));
        }
    }

    private Rectangle ResetButtonRect(in Layout lay)
    {
        var r = lay.Sidebar;
        // Anchor the button so a 3-line quote fits beneath it inside the sidebar.
        int btnH = 32;
        int btnY = r.Bottom - btnH - 56;
        return new Rectangle(r.X + SidebarPad, btnY, r.Width - SidebarPad * 2, btnH);
    }

    private void DrawSilhouette(Rectangle box)
    {
        // Hood: a rough trapezoid of dark
        int cx = box.X + box.Width / 2;
        int hoodTop = box.Y + 30;
        int hoodBottom = box.Bottom - 6;
        for (int y = hoodTop; y <= hoodBottom; y++)
        {
            float t = (y - hoodTop) / (float)(hoodBottom - hoodTop);
            int half = (int)(20 + t * 70);
            _batch.Draw(_pixel, new Rectangle(cx - half, y, half * 2, 1), new Color(10, 5, 8));
        }
        // Face shadow oval (rough): rows of decreasing alpha
        int faceCY = box.Y + 90;
        for (int dy = -28; dy <= 28; dy++)
        {
            int half = (int)Math.Sqrt(Math.Max(0, 28 * 28 - dy * dy));
            _batch.Draw(_pixel, new Rectangle(cx - half, faceCY + dy, half * 2, 1),
                new Color(26, 10, 18, 220));
        }
        // Glowing red eyes
        _batch.Draw(_pixel, new Rectangle(cx - 12, faceCY - 4, 6, 4), new Color(155, 48, 48));
        _batch.Draw(_pixel, new Rectangle(cx + 6,  faceCY - 4, 6, 4), new Color(155, 48, 48));
        _batch.Draw(_pixel, new Rectangle(cx - 11, faceCY - 3, 4, 2), new Color(255, 96, 96));
        _batch.Draw(_pixel, new Rectangle(cx + 7,  faceCY - 3, 4, 2), new Color(255, 96, 96));
    }

    // ----- Tree panel -----
    private void DrawTreePanel(in Layout lay)
    {
        var r = lay.Tree;
        // Parchment background -- radial gradient (lighter center, darker edges)
        if (_fx != null)
        {
            _fx.DrawRadialGradient(_batch, r,
                new Color(243, 232, 200), new Color(193, 175, 138),
                new Vector2(0.5f, 0.4f), 0.7f);
            _fx.DrawInsetShadow(_batch, r, new Color(110, 80, 30, 170), 44);
        }
        else
        {
            _batch.Draw(_pixel, r, Parchment2);
        }
        DrawBorder(r.X, r.Y, r.Width, r.Height, FrameRivet, 2);
        DrawBorder(r.X - 4, r.Y - 4, r.Width + 8, r.Height + 8, LeatherDeep, 2);

        // Top folio strip -- vertical gradient (leather)
        var strip = new Rectangle(r.X, r.Y, r.Width, 26);
        if (_fx != null)
            _fx.DrawVerticalGradient(_batch, strip,
                new Color(42, 26, 18), new Color(20, 12, 8));
        else
            _batch.Draw(_pixel, strip, LeatherDark);
        DrawBorder(strip.X, strip.Y, strip.Width, strip.Height, FrameRivet, 1);
        var sf = _smallFont ?? _font!;
        _batch.DrawString(sf, "BOOK III - CHAPTER VII",
            new Vector2(strip.X + 12, strip.Y + 6), GoldBright);
        string folio = "FOL. CXXIV - RECTO";
        var fz = sf.MeasureString(folio);
        _batch.DrawString(sf, folio,
            new Vector2((int)(strip.X + (strip.Width - fz.X) / 2), strip.Y + 6), Gold);
        string oc = "OF NECROMANCIE";
        var oz = sf.MeasureString(oc);
        _batch.DrawString(sf, oc,
            new Vector2((int)(strip.Right - 12 - oz.X), strip.Y + 6), GoldBright);

        // School headers -- embossed name, italic subtitle (effect), gold motto
        int headerY = r.Y + 38;
        var lf = _largeFont ?? _font!;
        for (int i = 0; i < Schools.Length; i++)
        {
            var s = Schools[i];
            int colX = lay.TreeOriginX + i * (lay.SchoolColW + lay.SchoolGutter);
            int colCx = colX + lay.SchoolColW / 2;
            var nameSize = lf.MeasureString(s.Name);
            UIGfx.DrawTextEmbossed(_batch, lf, s.Name,
                new Vector2((int)(colCx - nameSize.X / 2), headerY),
                BloodDark,
                new Color(255, 240, 200, 110),     // light highlight
                new Color(0, 0, 0, 50));           // soft drop
            var subSize = sf.MeasureString(s.Subtitle);
            _batch.DrawString(sf, s.Subtitle,
                new Vector2((int)(colCx - subSize.X / 2), headerY + (int)nameSize.Y + 2),
                new Color(23, 17, 13, 160));
            string motto = "~ " + s.Motto.ToUpper() + " ~";
            var mz = sf.MeasureString(motto);
            _batch.DrawString(sf, motto,
                new Vector2((int)(colCx - mz.X / 2), headerY + (int)nameSize.Y + (int)subSize.Y + 6),
                Gold);
        }

        // School dividers (between cols)
        for (int i = 1; i < 3; i++)
        {
            int dx = lay.TreeOriginX + i * (lay.SchoolColW + lay.SchoolGutter) - lay.SchoolGutter / 2 - 1;
            _batch.Draw(_pixel, new Rectangle(dx, r.Y + 30, 1, r.Height - 60),
                new Color(58, 42, 24, 90));
        }

        // Connectors
        foreach (var n in Nodes)
        {
            foreach (var p in n.Prereqs)
            {
                Node? from = null;
                foreach (var x in Nodes) if (x.Id == p.Id) { from = x; break; }
                if (from == null) continue;
                var (ax, ay) = NodeCenter(lay, from.Value);
                var (bx, by) = NodeCenter(lay, n);

                bool satisfied = _ranks[p.Id] >= p.Req;
                bool allocated = _ranks[n.Id] > 0;
                Color color = allocated ? Blood
                            : satisfied ? new Color(120, 80, 44)
                                        : new Color(58, 42, 24);
                int alpha = satisfied ? 230 : 90;
                color = new Color(color.R, color.G, color.B, alpha);

                int thickness = allocated ? 3 : 2;
                DrawBezierConnector(
                    new Vector2(ax, ay + NodeSize / 2),
                    new Vector2(bx, by - NodeSize / 2),
                    color, thickness, !satisfied);
                if (allocated)
                {
                    DrawBezierConnector(
                        new Vector2(ax, ay + NodeSize / 2),
                        new Vector2(bx, by - NodeSize / 2),
                        new Color(220, 64, 64, 130), 1, false);
                }
            }
        }

        // Nodes
        _hoverNode = null;
        int mxI = (int)_mouse.X; int myI = (int)_mouse.Y;
        foreach (var n in Nodes)
        {
            var (cx, cy) = NodeCenter(lay, n);
            DrawNode(n, cx, cy);
            int dx = mxI - cx; int dy = myI - cy;
            if (dx * dx + dy * dy <= (NodeSize / 2) * (NodeSize / 2))
                _hoverNode = n;
        }
    }

    private void DrawBezierConnector(Vector2 a, Vector2 b, Color color, int thickness, bool dashed)
    {
        // Approximate cubic bezier with line segments
        var c1 = new Vector2(a.X + (b.X - a.X) * 0.1f, a.Y + (b.Y - a.Y) * 0.5f);
        var c2 = new Vector2(b.X - (b.X - a.X) * 0.1f, b.Y - (b.Y - a.Y) * 0.5f);
        const int steps = 24;
        Vector2 prev = a;
        for (int i = 1; i <= steps; i++)
        {
            float t = i / (float)steps;
            float u = 1 - t;
            var p = u * u * u * a + 3 * u * u * t * c1 + 3 * u * t * t * c2 + t * t * t * b;
            if (!dashed || (i % 3 != 0))
                DrawThickLine(prev, p, color, thickness);
            prev = p;
        }
    }

    private void DrawThickLine(Vector2 a, Vector2 b, Color color, int thickness)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 0.5f) return;
        float angle = MathF.Atan2(dy, dx);
        for (int t = 0; t < thickness; t++)
        {
            _batch.Draw(_pixel, new Rectangle((int)a.X, (int)a.Y - thickness / 2 + t, (int)len, 1),
                null, color, angle, Vector2.Zero, SpriteEffects.None, 0f);
        }
    }

    private void DrawNode(Node n, int cx, int cy)
    {
        int r = NodeSize / 2;
        bool unlocked = IsUnlocked(n);
        int rank = _ranks[n.Id];
        bool active = rank > 0;
        bool maxed = rank >= n.Max;
        var schoolAccent = Schools[SchoolIndex(n.School)].Accent;

        // Medallion: AA bone-gradient ring + optional school-color outer glow.
        // The shader does: drop shadow, bone ring with vertical gradient, and
        // the active outer glow all in one pass.
        if (_fx != null)
        {
            // Drop shadow (offset slightly down for depth)
            _fx.DrawCircle(_batch, new Vector2(cx, cy + 2), r + 1, r + 1,
                new Color(0, 0, 0, 160), new Color(0, 0, 0, 160), Color.Transparent);

            // Bone ring: light top, dark bottom; optional school-color glow if active.
            float glowR = active ? r + 10 : r;
            var glowColor = active
                ? new Color(schoolAccent.R, schoolAccent.G, schoolAccent.B, (byte)220)
                : Color.Transparent;
            _fx.DrawCircle(_batch, new Vector2(cx, cy), r, glowR,
                new Color(232, 220, 192),  // top: bright bone
                new Color(120, 95, 44),    // bottom: dark bronze
                glowColor);

            // Inner dark medallion (solid dark circle inside the ring)
            _fx.DrawCircle(_batch, new Vector2(cx, cy), r - 4, r - 4,
                new Color(26, 18, 10), new Color(26, 18, 10), Color.Transparent);
        }
        else
        {
            // Fallback scanline bone ring + inner circle
            FillCircle(cx, cy + 2, r + 2, new Color(0, 0, 0, 170));
            for (int dy = -r; dy <= r; dy++)
            {
                int half = (int)Math.Sqrt(Math.Max(0, r * r - dy * dy));
                float vt = (dy + r) / (float)(2 * r);
                var c = Lerp(new Color(232, 220, 192), new Color(138, 109, 50), vt * 0.85f);
                _batch.Draw(_pixel, new Rectangle(cx - half, cy + dy, half * 2, 1), c);
            }
            FillCircle(cx, cy, r - 4, new Color(26, 18, 10));
        }
        // Outer + inner ring borders (thin outlines still useful for edge crispness)
        DrawUtils.DrawCircleOutline(_batch, _pixel, new Vector2(cx, cy), r, FrameRivet, 40);
        DrawUtils.DrawCircleOutline(_batch, _pixel, new Vector2(cx, cy), r - 4, FrameRivet, 40);

        // Active inner accent ring on top of the gradient
        if (active)
        {
            DrawUtils.DrawCircleOutline(_batch, _pixel, new Vector2(cx, cy), r - 1, schoolAccent, 40);
            DrawUtils.DrawCircleOutline(_batch, _pixel, new Vector2(cx, cy), r - 2,
                new Color(schoolAccent.R, schoolAccent.G, schoolAccent.B, (byte)180), 40);
        }

        // Rivets at cardinal points
        int rivR = r - 1;
        for (int a = 0; a < 4; a++)
        {
            float ang = a * MathF.PI / 2f;
            int rx = cx + (int)(MathF.Cos(ang) * rivR) - 1;
            int ry = cy + (int)(MathF.Sin(ang) * rivR) - 1;
            _batch.Draw(_pixel, new Rectangle(rx, ry, 3, 3), FrameRivet);
        }

        // Sigil OR big lock — locked nodes show a prominent lock instead of the glyph.
        if (!unlocked)
        {
            DrawBigLock(cx, cy);
        }
        else
        {
            var sf = _largeFont ?? _font!;
            var glyph = n.Glyph;
            Vector2 gs;
            try { gs = sf.MeasureString(glyph); }
            catch { glyph = "?"; gs = sf.MeasureString(glyph); }
            Color glyphColor = active ? schoolAccent : new Color(180, 158, 116);
            _batch.DrawString(sf, glyph,
                new Vector2((int)(cx - gs.X / 2), (int)(cy - gs.Y / 2)), glyphColor);
        }

        // Rank pip beneath the node
        var f = _font!;
        string pip = $"{rank}/{n.Max}";
        var ps = f.MeasureString(pip);
        int pipPad = 5;
        var pipRect = new Rectangle(
            (int)(cx - ps.X / 2 - pipPad),
            cy + r + 4,
            (int)ps.X + pipPad * 2, (int)ps.Y + 2);
        _batch.Draw(_pixel, pipRect, active ? BloodDark : Ink2);
        DrawBorder(pipRect.X, pipRect.Y, pipRect.Width, pipRect.Height,
            maxed ? new Color(240, 208, 96) : FrameRivet, 1);
        Color pipColor = maxed ? new Color(240, 208, 96)
                       : active ? Bone : ParchShadow;
        _batch.DrawString(f, pip,
            new Vector2((int)(pipRect.X + pipPad), pipRect.Y + 1), pipColor);

        // Name label below the pip
        var nameFont = _smallFont ?? f;
        var nz = nameFont.MeasureString(n.Name);
        int nameY = pipRect.Bottom + 3;
        _batch.DrawString(nameFont, n.Name,
            new Vector2((int)(cx - nz.X / 2) + 1, nameY + 1),
            new Color(0, 0, 0, 180));
        _batch.DrawString(nameFont, n.Name,
            new Vector2((int)(cx - nz.X / 2), nameY),
            unlocked ? Ink2 : new Color(120, 100, 70));
    }

    /// <summary>
    /// Draws a chunky padlock icon centered on the node — used as the locked-state
    /// glyph. Same visual language as the design: dark body, bright red shackle.
    /// </summary>
    private void DrawBigLock(int cx, int cy)
    {
        int bodyW = 22, bodyH = 16;
        int bx = cx - bodyW / 2;
        int by = cy - 2;
        // Shackle (above the body)
        int sw = 14;
        int sx = cx - sw / 2;
        int sy = by - 12;
        // Vertical posts
        _batch.Draw(_pixel, new Rectangle(sx,           sy + 1, 2, 11), BloodBright);
        _batch.Draw(_pixel, new Rectangle(sx + sw - 2,  sy + 1, 2, 11), BloodBright);
        // Top arc
        _batch.Draw(_pixel, new Rectangle(sx + 2,       sy,     sw - 4, 2), BloodBright);
        _batch.Draw(_pixel, new Rectangle(sx + 1,       sy + 1, 1, 1),     BloodBright);
        _batch.Draw(_pixel, new Rectangle(sx + sw - 2,  sy + 1, 1, 1),     BloodBright);
        // Body
        _batch.Draw(_pixel, new Rectangle(bx, by, bodyW, bodyH), BloodDark);
        DrawBorder(bx, by, bodyW, bodyH, BloodBright, 1);
        // Keyhole
        _batch.Draw(_pixel, new Rectangle(cx - 1, by + 4, 2, 4), Bone);
        _batch.Draw(_pixel, new Rectangle(cx - 2, by + 4, 4, 2), Bone);
    }

    private static Color Lerp(Color a, Color b, float t)
    {
        t = MathHelper.Clamp(t, 0f, 1f);
        return new Color(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t),
            (byte)(a.A + (b.A - a.A) * t));
    }

    private void FillCircle(int cx, int cy, int radius, Color color)
    {
        for (int dy = -radius; dy <= radius; dy++)
        {
            int half = (int)Math.Sqrt(Math.Max(0, radius * radius - dy * dy));
            _batch.Draw(_pixel, new Rectangle(cx - half, cy + dy, half * 2, 1), color);
        }
    }

    // ----- Footer & toast -----
    private void DrawFooter(in Layout lay)
    {
        var panel = lay.Panel;
        var sf = _smallFont ?? _font!;
        var rect = new Rectangle(panel.X + 18, panel.Bottom - FooterH - 12,
                                 panel.Width - 36, FooterH);
        _batch.Draw(_pixel, rect, LeatherDark);
        DrawBorder(rect.X, rect.Y, rect.Width, rect.Height, FrameRivet, 1);

        string[] parts = { "[L-CLICK] INSCRIBE", "[R-CLICK] REVOKE", "[HOVER] READ", "[K/ESC] CLOSE" };
        int slotW = rect.Width / parts.Length;
        for (int i = 0; i < parts.Length; i++)
        {
            var ts = sf.MeasureString(parts[i]);
            int sx = rect.X + slotW * i + (slotW - (int)ts.X) / 2;
            _batch.DrawString(sf, parts[i],
                new Vector2(sx, rect.Y + (rect.Height - (int)ts.Y) / 2), GoldBright);
        }
    }

    private void DrawWarningToast(Rectangle panel, string msg)
    {
        var f = _font!;
        var ts = f.MeasureString(msg);
        int padX = 18; int padY = 6;
        var rect = new Rectangle(panel.X + (panel.Width - ((int)ts.X + padX * 2)) / 2,
                                 panel.Bottom - 90, (int)ts.X + padX * 2, (int)ts.Y + padY * 2);
        _batch.Draw(_pixel, rect, BloodDark);
        DrawBorder(rect.X, rect.Y, rect.Width, rect.Height, BloodBright, 1);
        _batch.DrawString(f, "!  " + msg,
            new Vector2(rect.X + padX, rect.Y + padY), new Color(240, 208, 96));
    }

    // ----- Tooltip -----
    private void DrawHoverTooltip(Rectangle panel, int screenW, int screenH)
    {
        if (_hoverNode == null) return;
        var node = _hoverNode.Value;
        var f = _font!;
        var sf = _smallFont ?? f;
        var lf = _largeFont ?? f;

        int width = 320;
        // Measure stats height
        int statsH = 0;
        foreach (var s in node.Stats) statsH += (int)sf.MeasureString(s).Y + 2;
        int flavorH = WrapHeight(sf, "\"" + node.Flavor + "\"", width - 28);

        int height = 14 + (int)lf.MeasureString(node.Name).Y
                       + (int)sf.MeasureString("subline").Y + 8
                       + flavorH + 10
                       + statsH + 14
                       + (int)sf.MeasureString("X").Y + 12;

        int tx = (int)_mouse.X + 18;
        int ty = (int)_mouse.Y + 14;
        if (tx + width > screenW - 8) tx = screenW - width - 8;
        if (ty + height > screenH - 8) ty = screenH - height - 8;

        var rect = new Rectangle(tx, ty, width, height);
        _batch.Draw(_pixel, rect, Parchment);
        // Inner shading
        for (int i = 0; i < 6; i++)
            _batch.Draw(_pixel,
                new Rectangle(rect.X, rect.Y + rect.Height - (i + 1) * 8, rect.Width, 8),
                new Color(168, 148, 102, 14));
        DrawBorder(rect.X, rect.Y, rect.Width, rect.Height, FrameRivet, 1);
        DrawBorder(rect.X - 2, rect.Y - 2, rect.Width + 4, rect.Height + 4, LeatherDeep, 1);

        int y = rect.Y + 8;
        _batch.DrawString(lf, node.Name, new Vector2(rect.X + 12, y), BloodDark);
        y += (int)lf.MeasureString(node.Name).Y + 2;

        int rank = _ranks[node.Id];
        string sub = $"{Schools[SchoolIndex(node.School)].Name.ToUpper()} - TIER {node.Tier + 1} - {rank}/{node.Max}";
        _batch.DrawString(sf, sub, new Vector2(rect.X + 12, y), Gold);
        y += (int)sf.MeasureString(sub).Y + 8;

        // Flavor with left border
        var schoolColor = Schools[SchoolIndex(node.School)].Accent;
        _batch.Draw(_pixel, new Rectangle(rect.X + 12, y, 2, flavorH), schoolColor);
        DrawWrappedItalic(sf, "\"" + node.Flavor + "\"", rect.X + 20, y, width - 28);
        y += flavorH + 10;

        foreach (var s in node.Stats)
        {
            _batch.DrawString(sf, "+", new Vector2(rect.X + 12, y), Blood);
            _batch.DrawString(sf, s, new Vector2(rect.X + 24, y), Ink2);
            y += (int)sf.MeasureString(s).Y + 2;
        }

        // Footer hint
        y = rect.Bottom - (int)sf.MeasureString("X").Y - 6;
        _batch.Draw(_pixel, new Rectangle(rect.X + 12, y - 4, rect.Width - 24, 1),
            new Color(58, 42, 24, 140));
        bool unlocked = IsUnlocked(node);
        bool maxed = rank >= node.Max;
        string hint = !unlocked ? "X SEALED -- PREREQUISITES UNMET"
                    : maxed     ? "* MASTERED"
                    : "-> L-CLICK INSCRIBE - R-CLICK REVOKE";
        Color hintColor = !unlocked ? BloodBright
                        : maxed     ? Gold
                        : Verdigris;
        _batch.DrawString(sf, hint, new Vector2(rect.X + 12, y), hintColor);
    }

    private int WrapHeight(SpriteFont font, string text, int maxW)
    {
        int total = 0;
        foreach (var line in WrapLines(font, text, maxW))
            total += (int)font.MeasureString(line).Y;
        return Math.Max(total, (int)font.MeasureString("X").Y);
    }

    private void DrawWrappedItalic(SpriteFont font, string text, int x, int y, int maxW)
    {
        foreach (var line in WrapLines(font, text, maxW))
        {
            _batch.DrawString(font, line, new Vector2(x, y), Ink);
            y += (int)font.MeasureString(line).Y;
        }
    }

    private static IEnumerable<string> WrapLines(SpriteFont font, string text, int maxW)
    {
        var words = text.Split(' ');
        string cur = "";
        foreach (var w in words)
        {
            string trial = cur.Length == 0 ? w : cur + " " + w;
            if (font.MeasureString(trial).X <= maxW) cur = trial;
            else
            {
                if (cur.Length > 0) yield return cur;
                cur = w;
            }
        }
        if (cur.Length > 0) yield return cur;
    }

    // ----- Helpers -----
    private void DrawBorder(int x, int y, int w, int h, Color c, int thickness)
    {
        _batch.Draw(_pixel, new Rectangle(x, y, w, thickness), c);
        _batch.Draw(_pixel, new Rectangle(x, y + h - thickness, w, thickness), c);
        _batch.Draw(_pixel, new Rectangle(x, y, thickness, h), c);
        _batch.Draw(_pixel, new Rectangle(x + w - thickness, y, thickness, h), c);
    }

    /// <summary>
    /// Called by the host before <see cref="Draw"/> so the panel knows the
    /// current cursor location for hover/tooltip and the current panel rect
    /// for layout (we can't compute it again with different screen sizes).
    /// </summary>
    public void SetMouse(Vector2 pos) => _mouse = pos;

}
