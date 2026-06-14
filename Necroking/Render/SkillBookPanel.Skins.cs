using System;
using Microsoft.Xna.Framework;

namespace Necroking.Render;

/// <summary>
/// Visual skins for the skill book. The chosen base = skin-10 frame (thin gold
/// Renai panel border + parchment page + gold trim) with skin-8 interior buttons
/// (solid state-fill nodes in thin gold frames). The 5 skins differ only in the
/// TAB style. Cycle with Shift+B while the book is open.
/// </summary>
public partial class SkillBookPanel
{
    private sealed class Skin
    {
        public string Name = "";
        public string? PanelNs;            public float PanelNsScale = 1f;   // panel border (nine-slice)
        public string? PanelBg;            public float PanelBgInset;        // panel interior texture
        public string? ContentBg;          public float ContentInset;        // page texture
        public string? TitleBg;            public Color TitleText = GoldBright;
        // Tabs: background texture and/or a nine-slice frame, plus text colours.
        public string? TabActBg, TabIdleBg;
        public string? TabActNs, TabIdleNs; public float TabNsScale = 1f;
        public bool TabGoldAccent = true;
        public Color TabActText = new(23, 17, 13);
        public Color TabIdleText = new(210, 196, 168);
        public string? NodeNs;             public float NodeNsScale = 1f;    // node frame (nine-slice)
        public bool NodeParchment;         // texture the node interior with parchment (else solid state fill)
        public bool GoldTrim;              // gold lines on the content page edges
        public bool Corners = true;        // original corner brackets
        public Color Accent = Gold;
    }

    // Shared base: skin-10 frame + page + skin-8 nodes. Only the tab fields differ.
    private static Skin Base(string name) => new()
    {
        Name = name,
        PanelNs = "RenaiThinBorder",
        ContentBg = "SpellSlotBg", ContentInset = 0.16f, GoldTrim = true,
        NodeParchment = false, NodeNs = "RenaiThinBorder", NodeNsScale = 0.4f,
        Corners = false,
    };

    private static Skin TabVariant(string name, Action<Skin> setTabs)
    {
        var s = Base(name);
        setTabs(s);
        return s;
    }

    private static readonly Skin[] Skins =
    {
        // 1 — Parchment tabs: active tab = bright parchment, idle = flat dark.
        TabVariant("Parchment Tabs", s => {
            s.TabActBg = "SpellSlotBg"; s.TabActText = new(23, 17, 13);
            s.TabIdleText = new(214, 198, 162); s.TabGoldAccent = true; }),

        // 2 — Gold-framed tabs: parchment under a thin gold Renai frame.
        TabVariant("Gold-Framed Tabs", s => {
            s.TabActBg = "SpellSlotBg"; s.TabActNs = "RenaiThinBorder"; s.TabIdleNs = "RenaiThinBorder";
            s.TabNsScale = 0.4f; s.TabActText = new(23, 17, 13); s.TabIdleText = new(214, 198, 162);
            s.TabGoldAccent = false; }),

        // 3 — Blue swath tabs (unit-sheet rows), idle dimmed.
        TabVariant("Blue Swath Tabs", s => {
            s.TabActBg = "ST_RowSwatch"; s.TabIdleBg = "ST_RowSwatch";
            s.TabActText = new(247, 240, 222); s.TabIdleText = new(190, 200, 220); s.TabGoldAccent = true; }),

        // 4 — Ornate Swatch1.3 banner tabs.
        TabVariant("Swatch Banner Tabs", s => {
            s.TabActNs = "SwatchBanner"; s.TabActText = new(23, 17, 13);
            s.TabIdleText = new(214, 198, 162); s.TabGoldAccent = false; }),

        // 5 — Minimal: flat parchment active / leather idle + gold accent bar.
        TabVariant("Minimal Tabs", s => {
            s.TabActText = new(23, 17, 13); s.TabIdleText = new(210, 196, 168); s.TabGoldAccent = true; }),
    };

    private int _skinIndex;
    public static int SkinCount => Skins.Length;
    private Skin Active => Skins[_skinIndex < 0 || _skinIndex >= Skins.Length ? 0 : _skinIndex];
    public string CurrentSkinName => $"{_skinIndex + 1}/{Skins.Length}  {Active.Name}";

    /// <summary>Shift+B cycles skins (design review).</summary>
    public void CycleSkin(int dir)
    {
        _skinIndex += dir;
        if (_skinIndex >= Skins.Length) _skinIndex = 0;
        if (_skinIndex < 0) _skinIndex = Skins.Length - 1;
    }
    /// <summary>Set the skin directly (scenario screenshot sweep).</summary>
    public void SetSkin(int i) { _skinIndex = i; }

    /// <summary>Semi-transparent tint over a parchment node so its state still reads
    /// (only used when NodeParchment is true).</summary>
    private static Color NodeStateOverlay(bool learned, bool excluded, bool prereqsMet, bool affordable)
    {
        if (learned)     return new Color(40, 36, 30, 150);
        if (excluded)    return new Color(70, 16, 16, 140);
        if (!prereqsMet) return new Color(30, 22, 12, 150);
        if (affordable)  return new Color(255, 238, 188, 26);
        return new Color(70, 52, 28, 80);
    }

    // ---- skin-aware draw helpers (no-op / flat fallback when assets unavailable) ----
    private bool Has(string? id) => _widgets != null && !string.IsNullOrEmpty(id);
    private void Tex(string? id, Rectangle r, float inset = 0f) { if (Has(id)) _widgets!.DrawElementImage(id!, r, inset); }
    private void Ns(string? id, Rectangle r, float scale = 1f) { if (Has(id)) _widgets!.DrawNineSlice(id!, r, null, scale); }
}
