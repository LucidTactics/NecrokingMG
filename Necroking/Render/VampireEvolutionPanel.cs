using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Core;

namespace Necroking.Render;

/// <summary>
/// Vampire Evolution panel — modal skill tree toggled with N.
/// Port of the Claude Design "Vampire Evolution.html" mock: five evolution
/// lines (Hulking Brute / Noble Court / Arcane Scholar / Crimson Sovereign /
/// Shapeshifter) radiating from the shared origin "The Embrace".
///
/// Visual approach follows todos/css_rendering.md: the CSS mock's gradients,
/// box-shadow glows, and SVG filters are not reproduced with stacked
/// SpriteBatch primitives. The implementation uses flat fills, 1px borders,
/// and the existing UIShaders shader pipeline for a few accent gradients.
/// Layout, state machinery, hover path highlighting, prereq logic, and the
/// refund-with-cascade behaviour are ported directly from the JSX source.
/// </summary>
public class VampireEvolutionPanel
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

    // ----- Palette (ported from evo CSS vars / oklch -> RGB approx) -----
    private static readonly Color Ink0      = new(7, 6, 10);
    private static readonly Color Ink1      = new(12, 10, 18);
    private static readonly Color Ink2      = new(18, 16, 26);
    private static readonly Color Ink3      = new(26, 24, 36);
    private static readonly Color Bone      = new(232, 223, 206);
    private static readonly Color BoneDim   = new(184, 173, 151);
    private static readonly Color BoneFaint = new(125, 116, 99);
    private static readonly Color Rule      = new(42, 38, 51);
    private static readonly Color RuleStrong = new(58, 53, 68);

    private static readonly Color BruteHue  = new(196, 90, 78);    // crimson red
    private static readonly Color NobleHue  = new(205, 180, 108);  // old gold
    private static readonly Color ArcaneHue = new(130, 148, 220);  // cold indigo
    private static readonly Color BloodHue  = new(182, 52, 58);    // deep blood
    private static readonly Color ShiftHue  = new(126, 176, 136);  // moss

    // ----- Data -----
    private struct Line
    {
        public string Key;
        public string Name;
        public string Tagline;
        public Color Color;
        public int Col;
    }

    private static readonly Line[] Lines =
    {
        new() { Key = "brute",  Name = "Hulking Brute",     Tagline = "A monster of flesh and ruin.",
                Color = BruteHue,  Col = 0 },
        new() { Key = "noble",  Name = "Noble Court",        Tagline = "Crown, coin, and obedient throats.",
                Color = NobleHue,  Col = 1 },
        new() { Key = "arcane", Name = "Arcane Scholar",     Tagline = "Cold libraries, colder sorcery.",
                Color = ArcaneHue, Col = 2 },
        new() { Key = "blood",  Name = "Crimson Sovereign",  Tagline = "The red tide answers.",
                Color = BloodHue,  Col = 3 },
        new() { Key = "shift",  Name = "Shapeshifter",       Tagline = "Skin is a suggestion.",
                Color = ShiftHue,  Col = 4 },
    };

    private class Node
    {
        public string Id = "";
        public string LineKey = "";
        public int Tier;    // 0..5
        public int Row;     // -1 left / 0 mid / 1 right
        public string Name = "";
        public string Desc = "";
        public int Cost;
        public string[] Prereqs = Array.Empty<string>();
        public string Glyph = "";   // short ASCII glyph
        public bool Capstone;
    }

    private static readonly Node[] Nodes = new[]
    {
        new Node { Id="origin", LineKey="origin", Tier=0, Row=0, Name="The Embrace", Cost=0,
            Desc="The first death. You wake cold, the world dimmer and louder. All bloodlines begin here.",
            Prereqs=Array.Empty<string>(), Glyph="()+" },

        // BRUTE
        new Node { Id="brute_root", LineKey="brute", Tier=1, Row=0, Name="Feral Awakening", Cost=1,
            Desc="Your frame stretches an inch. Grip strength doubles. The first kill feels... deserved.",
            Prereqs=new[]{"origin"}, Glyph="FIST" },
        new Node { Id="brute_2a", LineKey="brute", Tier=2, Row=-1, Name="Granite Hide", Cost=2,
            Desc="Skin hardens to worn leather. Blades skip. Gunfire, in the dark, is a nuisance.",
            Prereqs=new[]{"brute_root"}, Glyph="SHD" },
        new Node { Id="brute_2b", LineKey="brute", Tier=2, Row=1, Name="Sinew-Breaker", Cost=2,
            Desc="Grip crushes through iron bars, steel handles, collarbones. Enemies disarmed on contact.",
            Prereqs=new[]{"brute_root"}, Glyph="CHN" },
        new Node { Id="brute_3", LineKey="brute", Tier=3, Row=0, Name="Goliath Frame", Cost=3,
            Desc="You tower. Eight and a half feet of coiled wrongness. Doorways are an insult.",
            Prereqs=new[]{"brute_2a","brute_2b"}, Glyph="III" },
        new Node { Id="brute_4a", LineKey="brute", Tier=4, Row=-1, Name="Juggernaut Charge", Cost=3,
            Desc="Full sprint shatters walls. No stagger, no stopping. Momentum becomes a law.",
            Prereqs=new[]{"brute_3"}, Glyph=">>>" },
        new Node { Id="brute_4b", LineKey="brute", Tier=4, Row=1, Name="Maw of the Pit", Cost=3,
            Desc="Jaw unhinges. You bite through plate, through vault doors, through spine.",
            Prereqs=new[]{"brute_3"}, Glyph="FNG" },
        new Node { Id="brute_cap", LineKey="brute", Tier=5, Row=0, Name="Titan of the Long Night", Cost=5,
            Desc="CAPSTONE. You are no longer a vampire. You are what peasants in 1403 saw at the treeline and never spoke of again.",
            Prereqs=new[]{"brute_4a","brute_4b"}, Glyph="VVV", Capstone=true },

        // NOBLE
        new Node { Id="noble_root", LineKey="noble", Tier=1, Row=0, Name="Courtly Presence", Cost=1,
            Desc="Your voice carries the weight of centuries. Merchants give better prices. Guards hesitate.",
            Prereqs=new[]{"origin"}, Glyph="GBT" },
        new Node { Id="noble_2a", LineKey="noble", Tier=2, Row=-1, Name="Silver Tongue", Cost=2,
            Desc="Lies register as truths to the mortal ear. Interrogations end without blood.",
            Prereqs=new[]{"noble_root"}, Glyph="SCR" },
        new Node { Id="noble_2b", LineKey="noble", Tier=2, Row=1, Name="Sealed Estate", Cost=2,
            Desc="Claim a manor. It generates coin and grants a daytime sanctum immune to pursuit.",
            Prereqs=new[]{"noble_root"}, Glyph="MNR" },
        new Node { Id="noble_3", LineKey="noble", Tier=3, Row=0, Name="Thrall-Binding", Cost=3,
            Desc="Bind up to three mortals as lifelong ghouls. They age slowly and die for you gladly.",
            Prereqs=new[]{"noble_2a","noble_2b"}, Glyph="O-O" },
        new Node { Id="noble_4a", LineKey="noble", Tier=4, Row=-1, Name="Shadow Council", Cost=3,
            Desc="Nobles across the realm owe you favor. Recall them once per night for counsel, a warrant, or an army.",
            Prereqs=new[]{"noble_3"}, Glyph="SEL" },
        new Node { Id="noble_4b", LineKey="noble", Tier=4, Row=1, Name="Decree of Blood", Cost=3,
            Desc="Speak a command aloud within your domain. All mortals who hear it obey for one night.",
            Prereqs=new[]{"noble_3"}, Glyph="DCR" },
        new Node { Id="noble_cap", LineKey="noble", Tier=5, Row=0, Name="Prince of the Realm", Cost=5,
            Desc="CAPSTONE. The city is your court. Kings write to you. You outlast dynasties as others outlast winters.",
            Prereqs=new[]{"noble_4a","noble_4b"}, Glyph="CRN", Capstone=true },

        // ARCANE
        new Node { Id="arcane_root", LineKey="arcane", Tier=1, Row=0, Name="The First Cantrip", Cost=1,
            Desc="You remember a word from a book you never read. Candlewicks gutter when you pass.",
            Prereqs=new[]{"origin"}, Glyph="SGL" },
        new Node { Id="arcane_2a", LineKey="arcane", Tier=2, Row=-1, Name="Bone-Frost", Cost=2,
            Desc="Exhale a cone of killing cold. Rivers crust, lungs seize, pursuers stop mid-step.",
            Prereqs=new[]{"arcane_root"}, Glyph="*<>" },
        new Node { Id="arcane_2b", LineKey="arcane", Tier=2, Row=1, Name="Pyre-Tongue", Cost=2,
            Desc="Summon black flame that ignores the damp and spares the undead. A language of burning.",
            Prereqs=new[]{"arcane_root"}, Glyph="FLM" },
        new Node { Id="arcane_3", LineKey="arcane", Tier=3, Row=0, Name="Stormcall", Cost=3,
            Desc="Draw lightning from clear sky. Each bolt is spent deliberately, like a sentence.",
            Prereqs=new[]{"arcane_2a","arcane_2b"}, Glyph="/Z/" },
        new Node { Id="arcane_4a", LineKey="arcane", Tier=4, Row=-1, Name="Glyph of Warding", Cost=3,
            Desc="Inscribe any threshold. Mortals cannot cross. Immortals pay dearly to try.",
            Prereqs=new[]{"arcane_3"}, Glyph="WRD" },
        new Node { Id="arcane_4b", LineKey="arcane", Tier=4, Row=1, Name="Elemental Chorus", Cost=3,
            Desc="Weave frost, flame, and storm into a single incantation. Battlefields become weather.",
            Prereqs=new[]{"arcane_3"}, Glyph="TRI" },
        new Node { Id="arcane_cap", LineKey="arcane", Tier=5, Row=0, Name="Archon of the Pale Library", Cost=5,
            Desc="CAPSTONE. You no longer cast spells; you read them aloud from the world, and it corrects itself to match.",
            Prereqs=new[]{"arcane_4a","arcane_4b"}, Glyph="EYE", Capstone=true },

        // BLOOD
        new Node { Id="blood_root", LineKey="blood", Tier=1, Row=0, Name="The Red Thread", Cost=1,
            Desc="You feel every heartbeat within a block, as tugs on a hanging wire.",
            Prereqs=new[]{"origin"}, Glyph="DRP" },
        new Node { Id="blood_2a", LineKey="blood", Tier=2, Row=-1, Name="Wound-Closure", Cost=2,
            Desc="Knit shut gunshots and gashes with a look. Blood obeys.",
            Prereqs=new[]{"blood_root"}, Glyph="STI" },
        new Node { Id="blood_2b", LineKey="blood", Tier=2, Row=1, Name="Crimson Lash", Cost=2,
            Desc="Pull blood from an open wound — yours or theirs — and shape it into a whip, a needle, a net.",
            Prereqs=new[]{"blood_root"}, Glyph="WHP" },
        new Node { Id="blood_3", LineKey="blood", Tier=3, Row=0, Name="Hemocall", Cost=3,
            Desc="Any creature whose blood you have tasted can be called to you across impossible distance.",
            Prereqs=new[]{"blood_2a","blood_2b"}, Glyph="CMP" },
        new Node { Id="blood_4a", LineKey="blood", Tier=4, Row=-1, Name="Blood-Puppeteer", Cost=3,
            Desc="Seize a mortal's circulation. Walk them. Stop them. Make them dance for the party.",
            Prereqs=new[]{"blood_3"}, Glyph="MAR" },
        new Node { Id="blood_4b", LineKey="blood", Tier=4, Row=1, Name="Scarlet Tide", Cost=3,
            Desc="Draw the blood from every living thing in a hundred feet. It comes, in a long obedient slick.",
            Prereqs=new[]{"blood_3"}, Glyph="TDE" },
        new Node { Id="blood_cap", LineKey="blood", Tier=5, Row=0, Name="Sovereign of the Crimson Veil", Cost=5,
            Desc="CAPSTONE. Blood is your native tongue. The wounded near you heal or empty at your pleasure — often both, in that order.",
            Prereqs=new[]{"blood_4a","blood_4b"}, Glyph="CHL", Capstone=true },

        // SHIFT
        new Node { Id="shift_root", LineKey="shift", Tier=1, Row=0, Name="Hollow Bones", Cost=1,
            Desc="Your ribs learn to click apart. A first shudder of wings. The mirror flickers.",
            Prereqs=new[]{"origin"}, Glyph="FTH" },
        new Node { Id="shift_2a", LineKey="shift", Tier=2, Row=-1, Name="Bat Form", Cost=2,
            Desc="Collapse into a leather-winged rag of a thing. Fly, eavesdrop, squeeze through arrowslits.",
            Prereqs=new[]{"shift_root"}, Glyph="BAT" },
        new Node { Id="shift_2b", LineKey="shift", Tier=2, Row=1, Name="Wolf Form", Cost=2,
            Desc="Four-legged, silent, quick. Track blood for miles. A grin full of older teeth.",
            Prereqs=new[]{"shift_root"}, Glyph="WLF" },
        new Node { Id="shift_3", LineKey="shift", Tier=3, Row=0, Name="Mist Body", Cost=3,
            Desc="Dissolve at will. Pass under doors, through grates, around silver. Reform at a remembered place.",
            Prereqs=new[]{"shift_2a","shift_2b"}, Glyph="MST" },
        new Node { Id="shift_4a", LineKey="shift", Tier=4, Row=-1, Name="Dire Pack", Cost=3,
            Desc="Summon a pack of spectral wolves that share your hunger and forget your name when dawn comes.",
            Prereqs=new[]{"shift_3"}, Glyph="PCK" },
        new Node { Id="shift_4b", LineKey="shift", Tier=4, Row=1, Name="Werewolf Hybrid", Cost=3,
            Desc="Rise onto your hind legs. Nine feet of claw and grief. You have not looked human in weeks.",
            Prereqs=new[]{"shift_3"}, Glyph="WWF" },
        new Node { Id="shift_cap", LineKey="shift", Tier=5, Row=0, Name="Legion of Hungers", Cost=5,
            Desc="CAPSTONE. You shift between all your forms mid-stride. The hunt becomes a single long sentence, and it is never you who is the subject.",
            Prereqs=new[]{"shift_4a","shift_4b"}, Glyph="CHM", Capstone=true },
    };

    // ----- State -----
    private const int StartingPoints = 24;
    private readonly HashSet<string> _unlocked = new();
    private int _bonus;
    private bool _inited;
    private string? _hoverId;
    private Vector2 _mouse;
    private string? _warn;
    private double _warnUntil;

    private void EnsureInit()
    {
        if (_inited) return;
        _inited = true;
        _unlocked.Add("origin");
    }

    private static Line GetLine(string key)
    {
        for (int i = 0; i < Lines.Length; i++) if (Lines[i].Key == key) return Lines[i];
        return default;
    }

    private static Node? GetNode(string id)
    {
        foreach (var n in Nodes) if (n.Id == id) return n;
        return null;
    }

    private int Spent()
    {
        int s = 0;
        foreach (var n in Nodes)
            if (_unlocked.Contains(n.Id) && n.LineKey != "origin") s += n.Cost;
        return s;
    }

    private int Remaining() => StartingPoints + _bonus - Spent();

    private bool CanUnlock(Node n)
    {
        if (_unlocked.Contains(n.Id)) return false;
        if (n.LineKey == "origin") return false;
        if (n.Cost > Remaining()) return false;
        foreach (var p in n.Prereqs) if (!_unlocked.Contains(p)) return false;
        return true;
    }

    private void Refund(Node n)
    {
        if (!_unlocked.Contains(n.Id) || n.LineKey == "origin") return;
        var toRemove = new HashSet<string> { n.Id };
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var m in Nodes)
            {
                if (!_unlocked.Contains(m.Id) || toRemove.Contains(m.Id)) continue;
                foreach (var p in m.Prereqs)
                {
                    if (toRemove.Contains(p)) { toRemove.Add(m.Id); changed = true; break; }
                }
            }
        }
        foreach (var id in toRemove) _unlocked.Remove(id);
        _unlocked.Add("origin");
    }

    private HashSet<string> HoverAncestors()
    {
        var set = new HashSet<string>();
        if (_hoverId == null) return set;
        var stack = new Stack<string>();
        stack.Push(_hoverId);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!set.Add(id)) continue;
            var n = GetNode(id);
            if (n == null) continue;
            foreach (var p in n.Prereqs) stack.Push(p);
        }
        return set;
    }

    // ----- Layout -----
    private struct Layout
    {
        public Rectangle Panel;
        public Rectangle Tree;
        public int ColStride;
        public int RowStride;
        public int OriginX;
        public int OriginY;
    }

    public bool ContainsMouse(int screenW, int screenH, int mx, int my)
    {
        if (!IsVisible) return false;
        var r = PanelRect(screenW, screenH);
        return mx >= r.X && mx < r.Right && my >= r.Y && my < r.Bottom;
    }

    private Rectangle PanelRect(int screenW, int screenH)
    {
        int marginX = Math.Max(40, screenW / 24);
        int marginY = Math.Max(30, screenH / 22);
        int w = Math.Max(screenW - marginX * 2, 900);
        int h = Math.Max(screenH - marginY * 2, 620);
        int maxW = (int)(h * 1.9f);
        if (w > maxW) w = maxW;
        return new Rectangle((screenW - w) / 2, (screenH - h) / 2, w, h);
    }

    private Layout BuildLayout(int screenW, int screenH)
    {
        var panel = PanelRect(screenW, screenH);
        int pad = 20;
        int headerH = 56;
        int footerH = 32;
        var tree = new Rectangle(
            panel.X + pad,
            panel.Y + headerH + pad,
            panel.Width - pad * 2,
            panel.Height - headerH - footerH - pad * 2);

        // 5 columns, 6 tier rows (0..5). Make columns fit the inner tree.
        int innerPadX = 60; // leaves room for tier labels and row-offset
        int innerPadY = 64;
        int usableW = tree.Width - innerPadX * 2;
        int usableH = tree.Height - innerPadY * 2;
        int colStride = usableW / 4;        // 5 columns → 4 gaps
        int rowStride = usableH / 5;        // 6 rows → 5 gaps
        int originX = tree.X + innerPadX + colStride * 2; // center column (arcane)
        int originY = tree.Y + innerPadY;

        return new Layout
        {
            Panel = panel, Tree = tree,
            ColStride = colStride, RowStride = rowStride,
            OriginX = originX, OriginY = originY,
        };
    }

    private (int x, int y) NodeCenter(in Layout lay, Node n)
    {
        if (n.LineKey == "origin") return (lay.OriginX, lay.OriginY);
        var line = GetLine(n.LineKey);
        int colDelta = line.Col - 2;
        int rowOffset = (n.Row != 0) ? (lay.ColStride / 4) * (n.Row > 0 ? 1 : -1) : 0;
        int x = lay.OriginX + colDelta * lay.ColStride + rowOffset;
        int y = lay.OriginY + n.Tier * lay.RowStride;
        return (x, y);
    }

    // ----- Input -----
    public void SetMouse(Vector2 pos) => _mouse = pos;

    public void Update(InputState input, int screenW, int screenH, double timeSec)
    {
        if (!IsVisible) return;
        EnsureInit();
        if (_warn != null && timeSec >= _warnUntil) _warn = null;

        var lay = BuildLayout(screenW, screenH);
        int mx = (int)input.MousePos.X;
        int my = (int)input.MousePos.Y;

        _hoverId = null;
        if (!lay.Panel.Contains(mx, my)) return;
        input.MouseOverUI = true;

        int nodeR = 22;
        foreach (var n in Nodes)
        {
            int r = n.LineKey == "origin" ? 30 : n.Capstone ? 26 : nodeR;
            var (cx, cy) = NodeCenter(lay, n);
            int dx = mx - cx, dy = my - cy;
            if (dx * dx + dy * dy > r * r) continue;
            _hoverId = n.Id;

            if (input.LeftPressed && !input.IsMouseConsumed)
            {
                if (n.LineKey == "origin") { /* noop */ }
                else if (_unlocked.Contains(n.Id)) Warn("Right-click to refund.", timeSec);
                else if (!CanUnlock(n))
                {
                    if (n.Cost > Remaining()) Warn("Insufficient essence.", timeSec);
                    else Warn("Prerequisites not met.", timeSec);
                }
                else { _unlocked.Add(n.Id); }
                input.ConsumeMouse();
            }
            else if (input.RightPressed && !input.IsMouseConsumed)
            {
                Refund(n);
                input.ConsumeMouse();
            }
            break;
        }
    }

    private void Warn(string msg, double timeSec) { _warn = msg; _warnUntil = timeSec + 2.0; }

    // ----- Draw -----
    public void Draw(int screenW, int screenH)
    {
        if (!IsVisible || _font == null) return;
        EnsureInit();

        // Dim the world
        _batch.Draw(_pixel, new Rectangle(0, 0, screenW, screenH), new Color(0, 0, 0, 190));

        var lay = BuildLayout(screenW, screenH);
        DrawChrome(lay.Panel);
        DrawHeader(lay);
        DrawTree(lay);
        DrawFooter(lay.Panel);
        if (_warn != null) DrawWarningToast(lay.Panel, _warn);
        DrawHoverTooltip(lay, screenW, screenH);
    }

    private void DrawChrome(Rectangle r)
    {
        if (_fx != null)
        {
            _fx.DrawRadialGradient(_batch, r, Ink2, Ink0,
                new Vector2(0.5f, 0.4f), 1.15f);
        }
        else
        {
            _batch.Draw(_pixel, r, Ink1);
        }
        DrawBorder(r, RuleStrong, 1);
        // thin outer accent band
        DrawBorder(new Rectangle(r.X + 4, r.Y + 4, r.Width - 8, r.Height - 8), Rule, 1);
    }

    private void DrawHeader(in Layout lay)
    {
        var r = lay.Panel;
        var sf = _smallFont ?? _font!;
        var f = _font!;
        var lf = _largeFont ?? f;

        int y = r.Y + 14;
        string folio = "FOL. VII - TRACTATUS DE EVOLUTIO VAMPYRI";
        _batch.DrawString(sf, folio, new Vector2(r.X + 24, y + 2), BoneFaint);

        string title = "THE BLOOD MUTATIONS";
        var ts = lf.MeasureString(title);
        _batch.DrawString(lf, title,
            new Vector2(r.X + 24 + (int)sf.MeasureString(folio).X + 24, y), Bone);

        int caps = 0;
        foreach (var n in Nodes) if (n.Capstone && _unlocked.Contains(n.Id)) caps++;

        // Right-aligned stats
        int rx = r.Right - 24;
        string essStr = Remaining().ToString();
        string essLbl = "ESSENCE";
        string capStr = $"{caps}/5";
        string capLbl = "CAPSTONES";
        string maxSub = $"/{StartingPoints + _bonus}";

        var essSz = lf.MeasureString(essStr);
        var maxSz = sf.MeasureString(maxSub);
        var essLblSz = sf.MeasureString(essLbl);
        _batch.DrawString(sf, essLbl,
            new Vector2((int)(rx - essSz.X - maxSz.X), y + 2), BoneFaint);
        _batch.DrawString(lf, essStr,
            new Vector2((int)(rx - essSz.X - maxSz.X), y + (int)essLblSz.Y + 2), BloodHue);
        _batch.DrawString(sf, maxSub,
            new Vector2((int)(rx - maxSz.X), y + (int)essLblSz.Y + (int)(essSz.Y - maxSz.Y)), BoneFaint);

        int rx2 = (int)(rx - essSz.X - maxSz.X - 36);
        var capSz = lf.MeasureString(capStr);
        _batch.DrawString(sf, capLbl,
            new Vector2(rx2 - (int)capSz.X, y + 2), BoneFaint);
        _batch.DrawString(lf, capStr,
            new Vector2(rx2 - (int)capSz.X, y + (int)essLblSz.Y + 2),
            caps > 0 ? BloodHue : Bone);

        // divider
        int divY = r.Y + 56;
        _batch.Draw(_pixel, new Rectangle(r.X + 16, divY, r.Width - 32, 1), RuleStrong);
    }

    private void DrawTree(in Layout lay)
    {
        var sf = _smallFont ?? _font!;
        var f = _font!;

        // Column headers (line names + taglines) + accent glow stripe
        for (int i = 0; i < Lines.Length; i++)
        {
            var line = Lines[i];
            int cx = lay.OriginX + (line.Col - 2) * lay.ColStride;
            int topY = lay.Tree.Y + 8;

            // Subtle column accent: tall thin rect of the line color, very low alpha
            var stripeColor = new Color(line.Color, 20);
            _batch.Draw(_pixel, new Rectangle(cx - 1, lay.Tree.Y, 2, lay.Tree.Height), stripeColor);

            string name = line.Name.ToUpper();
            string tag  = line.Tagline.ToUpper();
            var ns = sf.MeasureString(name);
            var tgs = sf.MeasureString(tag);
            _batch.DrawString(sf, name,
                new Vector2((int)(cx - ns.X / 2), topY), line.Color);
            _batch.DrawString(sf, tag,
                new Vector2((int)(cx - tgs.X / 2), topY + (int)ns.Y + 2), BoneFaint);
            // short rule beneath
            _batch.Draw(_pixel, new Rectangle(cx - 40, topY + (int)ns.Y + (int)tgs.Y + 6, 80, 1),
                new Color(line.Color, 140));
        }

        // Tier labels
        for (int t = 1; t <= 5; t++)
        {
            int ty = lay.OriginY + t * lay.RowStride;
            string lbl = $"T.{t:00}";
            _batch.DrawString(sf, lbl, new Vector2(lay.Tree.X + 8, ty - 5),
                new Color(BoneFaint, 140));
            _batch.Draw(_pixel, new Rectangle(lay.Tree.X + 44, ty, 20, 1),
                new Color(RuleStrong, 160));
        }

        var anc = HoverAncestors();

        // Edges
        foreach (var n in Nodes)
        {
            foreach (var pid in n.Prereqs)
            {
                var pNode = GetNode(pid);
                if (pNode == null) continue;
                var (ax, ay) = NodeCenter(lay, pNode);
                var (bx, by) = NodeCenter(lay, n);

                bool active = _unlocked.Contains(pid) && _unlocked.Contains(n.Id);
                bool reachable = _unlocked.Contains(pid);
                bool onPath = anc.Contains(n.Id) && anc.Contains(pid);
                var lineColor = n.LineKey != "origin" ? GetLine(n.LineKey).Color : BoneFaint;

                byte alpha = active ? (byte)230 : reachable ? (byte)130 : (byte)70;
                Color edge = active ? lineColor : RuleStrong;
                if (onPath) edge = lineColor;
                int thick = active ? 2 : 1;
                DrawCurve(ax, ay, bx, by, new Color(edge, alpha), thick, !active);
            }
        }

        // Nodes
        foreach (var n in Nodes)
        {
            var (cx, cy) = NodeCenter(lay, n);
            DrawNode(n, cx, cy, anc.Contains(n.Id));
        }
    }

    private void DrawCurve(int ax, int ay, int bx, int by, Color color, int thick, bool dashed)
    {
        // Vertical S-curve like SVG cubic used in the design
        float midY = (ay + by) / 2f;
        var a = new Vector2(ax, ay);
        var c1 = new Vector2(ax, midY);
        var c2 = new Vector2(bx, midY);
        var b = new Vector2(bx, by);
        const int steps = 24;
        Vector2 prev = a;
        for (int i = 1; i <= steps; i++)
        {
            float t = i / (float)steps;
            float u = 1 - t;
            var p = u * u * u * a + 3 * u * u * t * c1 + 3 * u * t * t * c2 + t * t * t * b;
            if (!dashed || (i % 3 != 0))
                DrawThickLine(prev, p, color, thick);
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
            _batch.Draw(_pixel,
                new Rectangle((int)a.X, (int)a.Y - thickness / 2 + t, (int)len, 1),
                null, color, angle, Vector2.Zero, SpriteEffects.None, 0f);
    }

    private void DrawNode(Node n, int cx, int cy, bool onPath)
    {
        bool isOrigin = n.LineKey == "origin";
        bool unlocked = _unlocked.Contains(n.Id);
        bool avail = CanUnlock(n);
        bool dim = !unlocked && !avail && !isOrigin;
        Color color = isOrigin ? Bone : GetLine(n.LineKey).Color;
        int r = isOrigin ? 30 : n.Capstone ? 26 : 22;

        // Outer aura ring for unlocked / hovered / path
        if (unlocked || onPath || _hoverId == n.Id)
        {
            DrawUtils.DrawCircleOutline(_batch, _pixel,
                new Vector2(cx, cy), r + 8,
                new Color(color, (byte)(unlocked ? 80 : 50)), 48);
        }

        // Fill disc: dark interior
        if (_fx != null)
        {
            _fx.DrawCircle(_batch, new Vector2(cx, cy), r, r,
                new Color(28, 24, 36), new Color(12, 10, 18),
                Color.Transparent);
        }
        else
        {
            FillCircle(cx, cy, r, Ink2);
        }

        // Outline color by state
        Color ring = dim ? RuleStrong :
                     unlocked ? color :
                     avail ? new Color(color, 220) :
                     new Color(color, 90);
        DrawUtils.DrawCircleOutline(_batch, _pixel, new Vector2(cx, cy), r, ring, 48);
        if (n.Capstone)
            DrawUtils.DrawCircleOutline(_batch, _pixel, new Vector2(cx, cy), r - 5,
                new Color(color, 100), 48);

        // Glyph (ASCII)
        var gf = _smallFont ?? _font!;
        string glyph = n.Glyph;
        Vector2 gs;
        try { gs = gf.MeasureString(glyph); }
        catch { glyph = "?"; gs = gf.MeasureString(glyph); }
        Color gc = dim ? BoneFaint : unlocked ? color : avail ? Bone : BoneDim;
        _batch.DrawString(gf, glyph,
            new Vector2((int)(cx - gs.X / 2), (int)(cy - gs.Y / 2)), gc);

        // Unlocked pip in upper-right
        if (unlocked && !isOrigin)
        {
            int px = cx + r - 6, py = cy - r + 2;
            _batch.Draw(_pixel, new Rectangle(px - 2, py - 2, 5, 5), color);
        }

        // Name label below
        var nf = _smallFont ?? _font!;
        string name = n.Name.ToUpper();
        var nsz = nf.MeasureString(name);
        int ny = cy + r + 6;
        Color nameCol = unlocked ? Bone : avail ? BoneDim : BoneFaint;
        _batch.DrawString(nf, name,
            new Vector2((int)(cx - nsz.X / 2), ny), nameCol);

        // Cost
        if (!unlocked && !isOrigin)
        {
            string cost = n.Cost + (n.Cost > 1 ? " PTS" : " PT");
            var cs = nf.MeasureString(cost);
            _batch.DrawString(nf, cost,
                new Vector2((int)(cx - cs.X / 2), ny + (int)nsz.Y + 2),
                avail ? color : BoneFaint);
        }
    }

    private void FillCircle(int cx, int cy, int radius, Color color)
    {
        for (int dy = -radius; dy <= radius; dy++)
        {
            int half = (int)Math.Sqrt(Math.Max(0, radius * radius - dy * dy));
            _batch.Draw(_pixel, new Rectangle(cx - half, cy + dy, half * 2, 1), color);
        }
    }

    private void DrawFooter(Rectangle panel)
    {
        var sf = _smallFont ?? _font!;
        var rect = new Rectangle(panel.X + 18, panel.Bottom - 38,
                                 panel.Width - 36, 26);
        _batch.Draw(_pixel, rect, new Color(Ink1, (byte)220));
        DrawBorder(rect, RuleStrong, 1);
        string[] parts = {
            "[L-CLICK] ACQUIRE",
            "[R-CLICK] REFUND",
            "[HOVER] INSPECT",
            "[N/ESC] CLOSE",
        };
        int slotW = rect.Width / parts.Length;
        for (int i = 0; i < parts.Length; i++)
        {
            var ts = sf.MeasureString(parts[i]);
            int sx = rect.X + slotW * i + (slotW - (int)ts.X) / 2;
            _batch.DrawString(sf, parts[i],
                new Vector2(sx, rect.Y + (rect.Height - (int)ts.Y) / 2), BoneDim);
        }
    }

    private void DrawWarningToast(Rectangle panel, string msg)
    {
        var f = _font!;
        var ts = f.MeasureString(msg);
        int padX = 16, padY = 6;
        var rect = new Rectangle(
            panel.X + (panel.Width - ((int)ts.X + padX * 2)) / 2,
            panel.Bottom - 80,
            (int)ts.X + padX * 2, (int)ts.Y + padY * 2);
        _batch.Draw(_pixel, rect, new Color(Ink0, (byte)240));
        DrawBorder(rect, BloodHue, 1);
        _batch.DrawString(f, msg,
            new Vector2(rect.X + padX, rect.Y + padY), BloodHue);
    }

    private void DrawHoverTooltip(in Layout lay, int screenW, int screenH)
    {
        if (_hoverId == null) return;
        var n = GetNode(_hoverId);
        if (n == null) return;
        var f = _font!;
        var sf = _smallFont ?? f;
        var lf = _largeFont ?? f;

        Color color = n.LineKey == "origin" ? Bone : GetLine(n.LineKey).Color;
        string lineName = n.LineKey == "origin" ? "ORIGIN" : GetLine(n.LineKey).Name.ToUpper();
        string header = n.Capstone ? lineName + "  -  CAPSTONE" : lineName;
        string status = _unlocked.Contains(n.Id) ? "ACQUIRED"
                      : CanUnlock(n) ? "AVAILABLE" : "LOCKED";
        Color statusColor = _unlocked.Contains(n.Id) ? color
                          : CanUnlock(n) ? Bone : BoneFaint;

        int width = 300;
        int flavorH = WrapHeight(sf, n.Desc, width - 24);
        int height = 10 + (int)sf.MeasureString("X").Y + 6
                       + (int)lf.MeasureString(n.Name).Y + 8
                       + flavorH + 18
                       + (int)sf.MeasureString("X").Y + 10;

        int tx = (int)_mouse.X + 18;
        int ty = (int)_mouse.Y + 14;
        if (tx + width > screenW - 8) tx = screenW - width - 8;
        if (ty + height > screenH - 8) ty = screenH - height - 8;
        if (tx < 8) tx = 8;
        if (ty < 8) ty = 8;

        var rect = new Rectangle(tx, ty, width, height);
        _batch.Draw(_pixel, rect, new Color(Ink0, (byte)245));
        DrawBorder(rect, new Color(color, 180), 1);
        // corner accents
        int ca = 7;
        _batch.Draw(_pixel, new Rectangle(rect.X, rect.Y, ca, 1), color);
        _batch.Draw(_pixel, new Rectangle(rect.X, rect.Y, 1, ca), color);
        _batch.Draw(_pixel, new Rectangle(rect.Right - ca, rect.Y, ca, 1), color);
        _batch.Draw(_pixel, new Rectangle(rect.Right - 1, rect.Y, 1, ca), color);
        _batch.Draw(_pixel, new Rectangle(rect.X, rect.Bottom - 1, ca, 1), color);
        _batch.Draw(_pixel, new Rectangle(rect.X, rect.Bottom - ca, 1, ca), color);
        _batch.Draw(_pixel, new Rectangle(rect.Right - ca, rect.Bottom - 1, ca, 1), color);
        _batch.Draw(_pixel, new Rectangle(rect.Right - 1, rect.Bottom - ca, 1, ca), color);

        int y = rect.Y + 10;
        _batch.DrawString(sf, header, new Vector2(rect.X + 12, y), new Color(color, 220));
        y += (int)sf.MeasureString(header).Y + 4;
        _batch.DrawString(lf, n.Name, new Vector2(rect.X + 12, y), Bone);
        y += (int)lf.MeasureString(n.Name).Y + 4;
        _batch.Draw(_pixel, new Rectangle(rect.X + 12, y, rect.Width - 24, 1), RuleStrong);
        y += 4;

        foreach (var ln in WrapLines(sf, n.Desc, width - 24))
        {
            _batch.DrawString(sf, ln, new Vector2(rect.X + 12, y), BoneDim);
            y += (int)sf.MeasureString(ln).Y;
        }

        int fy = rect.Bottom - (int)sf.MeasureString("X").Y - 8;
        _batch.Draw(_pixel, new Rectangle(rect.X + 12, fy - 4, rect.Width - 24, 1), RuleStrong);
        if (n.LineKey != "origin")
            _batch.DrawString(sf, $"COST {n.Cost}",
                new Vector2(rect.X + 12, fy), BoneFaint);
        var ss = sf.MeasureString(status);
        _batch.DrawString(sf, status,
            new Vector2((int)(rect.Right - 12 - ss.X), fy), statusColor);
    }

    private int WrapHeight(SpriteFont font, string text, int maxW)
    {
        int total = 0;
        foreach (var line in WrapLines(font, text, maxW))
            total += (int)font.MeasureString(line).Y;
        return Math.Max(total, (int)font.MeasureString("X").Y);
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

    private void DrawBorder(Rectangle r, Color c, int thickness)
    {
        _batch.Draw(_pixel, new Rectangle(r.X, r.Y, r.Width, thickness), c);
        _batch.Draw(_pixel, new Rectangle(r.X, r.Y + r.Height - thickness, r.Width, thickness), c);
        _batch.Draw(_pixel, new Rectangle(r.X, r.Y, thickness, r.Height), c);
        _batch.Draw(_pixel, new Rectangle(r.X + r.Width - thickness, r.Y, thickness, r.Height), c);
    }
}
