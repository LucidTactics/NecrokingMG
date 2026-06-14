using Microsoft.Xna.Framework;

namespace Necroking.Render;

/// <summary>
/// Visual skins for the skill book, reusing grimoire / unit-sheet / tooltip
/// chrome (parchment, fancy/leather/Renai frames, swaths, ribbons, dragon &amp;
/// nations patterns, gold trim). Cycle with Shift+B while the book is open.
/// Skin 0 is the original flat look; 1..10 are the new designs. Once one is
/// chosen the rest get removed.
/// </summary>
public partial class SkillBookPanel
{
    /// <summary>A skin is mostly data: which textured element/nine-slice backs each
    /// region (null = fall back to the original flat fill) plus a few colours.</summary>
    private sealed class Skin
    {
        public string Name = "";
        public string? PanelNs;            public float PanelNsScale = 1f;   // panel border (nine-slice)
        public string? PanelBg;            public float PanelBgInset;        // panel interior texture
        public string? ContentBg;          public float ContentInset;        // page/parchment texture
        public string? TitleBg;            public Color TitleText = GoldBright;
        public string? TabActBg, TabIdleBg;
        public Color TabActText = Ink;     public Color TabIdleText = new(210, 196, 168);
        public string? NodeNs;             public float NodeNsScale = 1f;    // node frame (nine-slice)
        public bool NodeParchment;         // texture the node interior with parchment (tinted by state)
        public bool GoldTrim;              // gold lines on title/content edges
        public bool Corners = true;        // the original corner brackets
        public Color Accent = Gold;
    }

    private static readonly Skin[] Skins =
    {
        // 0 — original flat leather + parchment.
        new() { Name = "Original" },

        // 1 — Grimoire: fancy gold frame, parchment page, ribbon title, framed nodes.
        new() { Name = "Grimoire", PanelNs = "frame_fancy", PanelNsScale = 0.5f, PanelBg = "SpellSlotBg",
                PanelBgInset = 0.16f, ContentBg = "SpellSlotBg", ContentInset = 0.16f, TitleBg = "Grim_TitleRibbon",
                TabActBg = "SpellSlotBg", NodeNs = "RenaiThinBorder", NodeNsScale = 0.5f, NodeParchment = true,
                TabActText = Ink, TabIdleText = new(232, 214, 176) },

        // 2 — Leather tome: embossed leather border, parchment page.
        new() { Name = "Leather Tome", PanelNs = "LeatherBackground", PanelNsScale = 0.45f, ContentBg = "SpellSlotBg",
                ContentInset = 0.16f, NodeParchment = true, NodeNs = "RenaiThinBorder", NodeNsScale = 0.45f,
                GoldTrim = true },

        // 3 — Unit-sheet blue swaths: dark page, swath-row nodes, swath tabs.
        new() { Name = "Unit Swath", PanelNs = "RenaiThinBorder16", PanelBg = null, ContentBg = null,
                TabActBg = "ST_RowSwatch", TabIdleBg = "ST_RowSwatch", NodeParchment = false, GoldTrim = true,
                TabActText = new(247, 240, 222), TitleBg = "UnitTitleSwatch", TitleText = new(247, 240, 222) },

        // 4 — Dragon damask page under a gold frame.
        new() { Name = "Dragon Damask", PanelNs = "frame_fancy", PanelNsScale = 0.5f, ContentBg = "UnitDescPattern",
                NodeParchment = true, NodeNs = "RenaiThinBorder", NodeNsScale = 0.45f, TitleBg = "Grim_TitleRibbon" },

        // 5 — Renai thin tooltip frame throughout.
        new() { Name = "Renai Thin", PanelNs = "RenaiThinBorder16", ContentBg = "SpellSlotBg", ContentInset = 0.16f,
                NodeNs = "RenaiThinBorder", NodeNsScale = 0.4f, NodeParchment = true, GoldTrim = true,
                Corners = false },

        // 6 — Cloth-upgrade frame + nations pattern page.
        new() { Name = "Cloth + Nations", PanelNs = "Thinclothborder", PanelNsScale = 0.6f, ContentBg = "AbilitiesPattern",
                NodeParchment = true, NodeNs = "RenaiThinBorder", NodeNsScale = 0.45f, Corners = false },

        // 7 — Ornate: full fancy frame + ribbon title + fancy-button nodes.
        new() { Name = "Ornate", PanelNs = "frame_fancy", PanelNsScale = 0.7f, ContentBg = "SpellSlotBg",
                ContentInset = 0.16f, TitleBg = "Grim_TitleRibbon", NodeNs = "FancyButton", NodeParchment = true,
                Corners = false },

        // 8 — Stat-box swaths for page + nodes (the unit panel's blue boxes).
        new() { Name = "Stat Box", PanelNs = "RenaiThinBorder16", ContentBg = "EQ_StatBox", NodeParchment = false,
                NodeNs = "RenaiThinBorder", NodeNsScale = 0.4f, GoldTrim = true, TitleBg = "UnitTitleSwatch",
                TitleText = new(247, 240, 222) },

        // 9 — Swatch banner frame + parchment.
        new() { Name = "Swatch Banner", PanelNs = "SwatchBanner", PanelNsScale = 1.2f, ContentBg = "SpellSlotBg",
                ContentInset = 0.16f, NodeParchment = true, NodeNs = "RenaiThinBorder", NodeNsScale = 0.45f,
                Corners = false },

        // 10 — Minimal: flat parchment page + slim gold trim, no heavy frame.
        new() { Name = "Minimal Gold", ContentBg = "SpellSlotBg", ContentInset = 0.16f, GoldTrim = true,
                NodeParchment = true, Corners = false, PanelNs = "RenaiThinBorder" },
    };

    private int _skinIndex = 1;
    public static int SkinCount => Skins.Length;
    private Skin Active => Skins[_skinIndex < 0 || _skinIndex >= Skins.Length ? 0 : _skinIndex];
    public string CurrentSkinName => $"{_skinIndex}/{Skins.Length - 1}  {Active.Name}";

    /// <summary>Shift+B cycles skins (design review).</summary>
    public void CycleSkin(int dir)
    {
        _skinIndex += dir;
        if (_skinIndex >= Skins.Length) _skinIndex = 0;
        if (_skinIndex < 0) _skinIndex = Skins.Length - 1;
    }
    /// <summary>Set the skin directly (scenario screenshot sweep).</summary>
    public void SetSkin(int i) { _skinIndex = i; }

    // ---- skin-aware draw helpers (no-op / flat fallback when assets unavailable) ----
    /// <summary>Semi-transparent tint drawn over a parchment node so its state still
    /// reads: bright = affordable, darkened = locked, grey = learned, red = excluded.</summary>
    private static Color NodeStateOverlay(bool learned, bool excluded, bool prereqsMet, bool affordable)
    {
        if (learned)    return new Color(40, 36, 30, 150);
        if (excluded)   return new Color(70, 16, 16, 140);
        if (!prereqsMet) return new Color(30, 22, 12, 150);
        if (affordable) return new Color(255, 238, 188, 26);   // keep it bright + warm
        return new Color(70, 52, 28, 80);                      // affordable-later: gently dimmed
    }

    private bool Has(string? id) => _widgets != null && !string.IsNullOrEmpty(id);
    private void Tex(string? id, Rectangle r, float inset = 0f) { if (Has(id)) _widgets!.DrawElementImage(id!, r, inset); }
    private void Ns(string? id, Rectangle r, float scale = 1f) { if (Has(id)) _widgets!.DrawNineSlice(id!, r, null, scale); }
}
