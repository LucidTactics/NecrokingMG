using Microsoft.Xna.Framework;

namespace Necroking.Render;

/// <summary>
/// Deep grimoire overhaul of the skill book. The "Grimoire" skin reproduces the
/// spell-grimoire window: cloth border, maroon ribbon title, parchment+gold tabs,
/// an ornate header divider, a LibraryScene page, and — crucially — each skill is
/// drawn as a grimoire SPELL TILE (parchment + diagonal sheen + spider-framed icon
/// + gold serif name + gold underline + cost icon/value + ornate frame) with
/// ornate gold connectors. "Original" keeps the old flat tome for comparison.
/// Shift+B toggles between them.
/// </summary>
public partial class SkillBookPanel
{
    private sealed class Skin
    {
        public string Name = "";
        public bool Grimoire;              // master switch: render the grimoire tile/window/connectors
        public string? PanelNs;            public float PanelNsScale = 1f;   // panel border (nine-slice)
        public string? ContentBg;          public float ContentInset;        // page texture
        public string? TitleBg;            public Color TitleText = GoldBright;
        public string? TabActBg, TabIdleBg;
        public string? TabActNs, TabIdleNs; public float TabNsScale = 1f;
        public bool TabGoldAccent = true;
        public Color TabActText = new(23, 17, 13);
        public Color TabIdleText = new(210, 196, 168);
        public bool Corners = true;
    }

    private static readonly Skin[] Skins =
    {
        // 0 — deep grimoire overhaul. The Grimoire path in SkillBookPanel.Draw
        // reproduces the spell grimoire directly from its baked Grim_* elements
        // (window frame, ribbon, tab strip, divider, page) + the spell-tile
        // elements, so this descriptor only needs the master switch.
        new() { Name = "Grimoire", Grimoire = true },
        // 1 — original flat tome (kept for comparison).
        new() { Name = "Original" },
    };

    private int _skinIndex;
    public static int SkinCount => Skins.Length;
    private Skin Active => Skins[_skinIndex < 0 || _skinIndex >= Skins.Length ? 0 : _skinIndex];
    public string CurrentSkinName => $"{_skinIndex + 1}/{Skins.Length}  {Active.Name}";

    public void CycleSkin(int dir)
    {
        _skinIndex += dir;
        if (_skinIndex >= Skins.Length) _skinIndex = 0;
        if (_skinIndex < 0) _skinIndex = Skins.Length - 1;
    }
    public void SetSkin(int i) { _skinIndex = i; }

    // ---- skin-aware draw helpers (no-op / flat fallback when assets unavailable) ----
    private bool Has(string? id) => _widgets != null && !string.IsNullOrEmpty(id);
    private void Tex(string? id, Rectangle r, float inset = 0f) { if (Has(id)) _widgets!.DrawElementImage(id!, r, inset); }
    private void Ns(string? id, Rectangle r, float scale = 1f) { if (Has(id)) _widgets!.DrawNineSlice(id!, r, null, scale); }

    // ---- grimoire-font text (FontStashSharp, matching the spell grimoire exactly) ----
    // Quintessential for titles & spell/skill names; Roboto for tab labels & values.
    private const string FontSerif = "Quintessential";
    private const string FontSans  = "Roboto";

    /// <summary>Draw text with the grimoire's scalable font + a 1px black outline
    /// (the grimoire's text elements are bold with outlineWidth 1).</summary>
    private void GText(string text, int x, int y, int size, Color color, string family)
    {
        if (_widgets == null || string.IsNullOrEmpty(text)) return;
        var oc = new Color(0, 0, 0, 160);
        _widgets.DrawText(text, x - 1, y, size, oc, family);
        _widgets.DrawText(text, x + 1, y, size, oc, family);
        _widgets.DrawText(text, x, y - 1, size, oc, family);
        _widgets.DrawText(text, x, y + 1, size, oc, family);
        _widgets.DrawText(text, x, y, size, color, family);
    }
    private int GW(string text, int size, string family) => (int)(_widgets?.MeasureText(text, size, family).X ?? 0);
    private int GH(int size, string family) => (int)(_widgets?.MeasureText("Mg", size, family).Y ?? size);

    /// <summary>Truncate to fit a pixel width in the grimoire font.</summary>
    private string GFit(string text, int maxPx, int size, string family)
    {
        if (_widgets == null || string.IsNullOrEmpty(text) || GW(text, size, family) <= maxPx) return text;
        for (int i = text.Length - 1; i > 0; i--)
        {
            string t = text.Substring(0, i).TrimEnd() + "...";
            if (GW(t, size, family) <= maxPx) return t;
        }
        return text;
    }

    // Resolved icon path per skill id (cached so we File.Exists once, not per frame).
    private readonly System.Collections.Generic.Dictionary<string, string> _skillIconCache = new();

    /// <summary>The icon for a skill tile. Prefers a bespoke generated icon
    /// (assets/UI/Icons/Skills/{id}.png from tools/gen_skill_icons.py); falls back
    /// to the first item-cost's icon, else a Death glyph. Mirrors the grimoire
    /// tile's framed icon.</summary>
    private string SkillTileIcon(Data.SkillDef def)
    {
        if (_skillIconCache.TryGetValue(def.Id, out var cached)) return cached;

        string path;
        string rel = $"assets/UI/Icons/Skills/{def.Id}.png";
        if (System.IO.File.Exists(Necroking.Core.GamePaths.Resolve(rel)))
            path = rel;
        else
        {
            path = Data.Registries.MagicPathHelpers.IconPath(Data.Registries.MagicPath.Death, 24);
            foreach (var c in def.Costs)
                if (c.Type == "item")
                {
                    var it = _gameData?.Items?.Get(c.Id);
                    if (it != null && !string.IsNullOrEmpty(it.Icon)) { path = it.Icon; break; }
                }
        }
        _skillIconCache[def.Id] = path;
        return path;
    }
}
