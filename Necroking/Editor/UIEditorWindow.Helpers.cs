using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Core;
using Necroking.UI;
using System.Linq;

namespace Necroking.Editor;

public partial class UIEditorWindow
{
    // ═══════════════════════════════════════
    //  Texture / Nine-Slice Lookup Helpers
    // ═══════════════════════════════════════

    /// <summary>Get harmonized texture if available, otherwise original. Handles cache prefix.</summary>
    private Texture2D? GetTexture(string imagePath, string cachePrefix = "")
    {
        if (string.IsNullOrEmpty(imagePath)) return null;
        string cacheKey = string.IsNullOrEmpty(cachePrefix) ? imagePath : cachePrefix + "|" + imagePath;
        if (_harmonizedTextures.TryGetValue(cacheKey, out var harmonized))
            return harmonized;
        return GetOrLoadTexture(imagePath);
    }

    /// <summary>Get harmonized nine-slice if available, otherwise original. Handles cache prefix.</summary>
    private NineSlice? GetNineSlice(string nsId, string cachePrefix = "")
    {
        if (string.IsNullOrEmpty(nsId)) return null;
        string cacheKey = string.IsNullOrEmpty(cachePrefix) ? nsId : cachePrefix + "|" + nsId;
        if (_harmonizedNineSlices.TryGetValue(cacheKey, out var harmonized))
            return harmonized;
        return GetOrLoadNineSlice(nsId);
    }

    // ═══════════════════════════════════════
    //  Widget Layer Rendering
    // ═══════════════════════════════════════

    /// <summary>Draw widget layers into a rectangle.
    /// When drawFrame=false, only draws background + stencil (for inserting children between stencil and frame).</summary>
    private void DrawWidgetLayers(UIEditorWidgetDef def, int drawX, int drawY, int drawW, int drawH, bool drawFrame = true)
    {
        // Layer 1: Background (with inset) — nine-slice or direct image
        if (!string.IsNullOrEmpty(def.Background))
        {
            var bgNs = GetNineSlice(def.Background, "bg:" + def.Id);
            if (bgNs != null)
            {
                int bi = def.BackgroundInset;
                var bgRect = new Rectangle(drawX + bi, drawY + bi, drawW - bi * 2, drawH - bi * 2);
                var bgColor = def.BackgroundTint != null ? ByteColor(def.BackgroundTint) : Color.White;
                bgNs.Draw(_sb, bgRect, bgColor, def.BackgroundScale);
            }
        }
        else if (!string.IsNullOrEmpty(def.BackgroundImagePath))
        {
            var bgTex = GetTexture(def.BackgroundImagePath, "bg:" + def.Id);
            if (bgTex != null)
            {
                int bi = def.BackgroundInset;
                var bgRect = new Rectangle(drawX + bi, drawY + bi, drawW - bi * 2, drawH - bi * 2);
                var bgColor = def.BackgroundTint != null ? ByteColor(def.BackgroundTint) : Color.White;
                DrawImageLayerCropTop(bgTex, bgRect, bgColor, def.Height - bi * 2);
            }
        }

        // Layer 2: Stencil (with inset) — nine-slice or stretched image
        {
            int si = def.StencilInset;
            var stRect = new Rectangle(drawX + si, drawY + si, drawW - si * 2, drawH - si * 2);
            var stColor = def.StencilTint != null ? ByteColor(def.StencilTint) : Color.White;

            if (!string.IsNullOrEmpty(def.StencilImagePath))
            {
                var stTex = GetTexture(def.StencilImagePath, "st:" + def.Id);
                if (stTex != null) DrawImageLayerCropTop(stTex, stRect, stColor, def.Height - si * 2);
            }
            else if (!string.IsNullOrEmpty(def.Stencil))
            {
                var stNs = GetNineSlice(def.Stencil, "st:" + def.Id);
                if (stNs != null) stNs.Draw(_sb, stRect, stColor);
            }
        }

        // Layer 3: Frame (topmost, full bounds)
        if (drawFrame)
            DrawWidgetFrame(def, drawX, drawY, drawW, drawH);
    }

    /// <summary>Image layers on auto-size widgets are baked at the widget's MAX
    /// height; when drawn shorter, crop from the top instead of squashing
    /// (mirrors RuntimeWidgetRenderer.DrawImageLayerCropTop).</summary>
    private void DrawImageLayerCropTop(Texture2D tex, Rectangle rect, Color color, int defMaxH)
    {
        if (defMaxH > 0 && rect.Height < defMaxH)
        {
            int srcH = (int)System.Math.Round(tex.Height * (rect.Height / (float)defMaxH));
            Scope.Draw(tex, rect, new Rectangle(0, 0, tex.Width, System.Math.Max(1, srcH)), color);
        }
        else
        {
            Scope.Draw(tex, rect, color);
        }
    }

    // ─── Auto-size preview (editor-side mirror of the runtime's instance-aware
    //     layout: hide trailing rows, measure, stack) ───
    private int _previewHiddenRows;

    private int MeasureAutoHeight(UIEditorWidgetDef def, System.Collections.Generic.HashSet<int> hiddenIdx)
    {
        if (!def.AutoSizeHeight || def.Layout != "vertical") return def.Height;
        int padT = def.LayoutPadTop > 0 ? def.LayoutPadTop : def.LayoutPadding;
        int padB = def.LayoutPadBottom > 0 ? def.LayoutPadBottom : def.LayoutPadding;
        int spacY = def.LayoutSpacingY > 0 ? def.LayoutSpacingY : def.LayoutSpacing;
        int total = 0, count = 0;
        for (int i = 0; i < def.Children.Count; i++)
        {
            var child = def.Children[i];
            if (child.IgnoreLayout || hiddenIdx.Contains(i)) continue;
            total += PreviewChildHeight(child);
            count++;
        }
        if (count > 1) total += (count - 1) * spacY;
        return padT + total + padB;
    }

    private int PreviewChildHeight(UIEditorChildDef child)
    {
        if (!string.IsNullOrEmpty(child.Widget))
        {
            var sub = _widgets.Find(w => w.Id == child.Widget);
            if (sub != null && sub.AutoSizeHeight)
                return MeasureAutoHeight(sub, new System.Collections.Generic.HashSet<int>());
        }
        return child.Height > 0 ? child.Height : 40;
    }

    // Editor preview layout — the SAME shared pass the runtime uses (so preview ==
    // game), supplying the preview's hidden set and auto-size-aware heights. Heights
    // still come from PreviewChildHeight since the editor has no per-instance overrides.
    private System.Collections.Generic.List<Rectangle> ComputePreviewRects(
        UIEditorWidgetDef def, int wdX, int wdY, System.Collections.Generic.HashSet<int> hiddenIdx)
        => Necroking.UI.WidgetLayoutUtils.ComputeLayoutRects(def, wdX, wdY,
            i => hiddenIdx.Contains(i),
            (child, i) => PreviewChildHeight(child));

    /// <summary>Draw just the frame layer of a widget.</summary>
    private void DrawWidgetFrame(UIEditorWidgetDef def, int drawX, int drawY, int drawW, int drawH)
    {
        if (!string.IsNullOrEmpty(def.Frame))
        {
            var frNs = GetNineSlice(def.Frame, "fr:" + def.Id);
            if (frNs != null)
            {
                var frColor = def.FrameTint != null ? ByteColor(def.FrameTint) : Color.White;
                int fi = def.FrameInset;
                frNs.Draw(_sb, new Rectangle(drawX + fi, drawY + fi, drawW - fi * 2 - def.FrameInsetR, drawH - fi * 2),
                    frColor, def.FrameScale);
            }
        }
    }

    // ═══════════════════════════════════════
    //  Tint Swatch Helper
    // ═══════════════════════════════════════

    /// <summary>Draw a tint color swatch with ByteColor conversion. Returns true if changed.</summary>
    private bool DrawTintSwatch(string swatchId, ref byte[]? tintColor, int x, int y, bool hideIntensity = true)
    {
        byte[] tint = tintColor ?? new byte[] { 255, 255, 255, 255 };
        var hdr = BytesToHdr(tint);
        bool clicked = DrawColorSwatch(swatchId, x, y, 40, 18, ref hdr, hideIntensity);
        var newTint = HdrToBytes(hdr);
        if (tintColor == null || !newTint.SequenceEqual(tintColor))
        {
            tintColor = newTint;
            _unsavedChanges = true;
            return true;
        }
        if (clicked) _unsavedChanges = true;
        return clicked;
    }

    // ═══════════════════════════════════════
    //  Harmonizer Helpers (static, no shared state)
    // ═══════════════════════════════════════

    /// <summary>Apply harmonization to a layer's texture using settings directly.
    /// No shared harmonizer state — each call is self-contained.</summary>
    private void ApplyHarmonize(string nsId, string imagePath, string cachePrefix, HarmonizeSettings? settings)
    {
        if (_device == null) return;

        // Find source texture
        Texture2D? sourceTex = null;
        string texKey = "";
        string? nsLookupId = null;

        if (!string.IsNullOrEmpty(imagePath))
        {
            sourceTex = GetOrLoadTexture(imagePath);
            texKey = imagePath;
        }
        else if (!string.IsNullOrEmpty(nsId))
        {
            var nsDef = _nineSlices.FirstOrDefault(n => n.Id == nsId);
            if (nsDef != null && !string.IsNullOrEmpty(nsDef.Texture))
            {
                sourceTex = GetOrLoadTexture(nsDef.Texture);
                texKey = nsDef.Texture;
                nsLookupId = nsId;
            }
        }

        if (sourceTex == null || string.IsNullOrEmpty(texKey)) return;

        string cacheKey = string.IsNullOrEmpty(cachePrefix) ? texKey : cachePrefix + "|" + texKey;

        if (settings == null || !settings.HasEffect)
        {
            // Clear cache to show original
            if (_harmonizedTextures.TryGetValue(cacheKey, out var old) && old != sourceTex)
                old.Dispose();
            _harmonizedTextures.Remove(cacheKey);
            if (!string.IsNullOrEmpty(nsLookupId))
                _harmonizedNineSlices.Remove(cachePrefix + "|" + nsLookupId);
            return;
        }

        // Static call — no shared state
        var harmonized = ColorHarmonizer.HarmonizeTexture(sourceTex, _device, settings);
        if (harmonized != null)
        {
            if (_harmonizedTextures.TryGetValue(cacheKey, out var old) && old != sourceTex)
                old.Dispose();
            _harmonizedTextures[cacheKey] = harmonized;

            if (!string.IsNullOrEmpty(nsLookupId))
            {
                var nsDef = _nineSlices.FirstOrDefault(n => n.Id == nsLookupId);
                if (nsDef != null)
                {
                    var nsInst = new NineSlice();
                    nsInst.LoadFromTexture(harmonized, nsDef.BorderLeft, nsDef.BorderRight,
                        nsDef.BorderTop, nsDef.BorderBottom, nsDef.TileEdges);
                    _harmonizedNineSlices[cachePrefix + "|" + nsLookupId] = nsInst;
                }
            }
        }
    }

    /// <summary>Draw inline harmonizer sliders that directly modify persistent settings.
    /// Also writes the live color picker value to settings every frame for live preview.
    /// Returns true if any setting changed.</summary>
    private bool DrawInlineHarmonizeSliders(string swatchId, HarmonizeSettings settings, int x, ref int curY, int w)
        // Delegates to the canonical EditorBase implementation (shared with the env-object editor).
        => DrawHarmonizeSliders(swatchId, settings, x, ref curY, w);

    /// <summary>Draw a complete layer harmonize section: tint swatch + toggle + sliders.
    /// Handles both nine-slice and image paths.</summary>
    private void DrawLayerHarmonizeSection(string label, string swatchId, string cachePrefix,
        string nsId, string imagePath,
        ref byte[]? tintColor, ref HarmonizeSettings? settings,
        int x, int pad, int propW, ref int curY)
    {
        bool hasNs = !string.IsNullOrEmpty(nsId);
        bool hasImg = !string.IsNullOrEmpty(imagePath);
        if (!hasNs && !hasImg) return;

        DrawText($"{label} Tint:", new Vector2(x + pad, curY + 2), TextDim);
        DrawTintSwatch(swatchId, ref tintColor, x + pad + 120, curY, false);

        bool showHarm = settings != null;
        if (DrawButton(showHarm ? "Hide Harm." : "Harmonize", x + pad + 170, curY, 80, 18))
        {
            settings = showHarm ? null : new HarmonizeSettings();
            _unsavedChanges = true;
        }
        curY += 24;

        if (settings != null)
        {
            bool changed = DrawInlineHarmonizeSliders(swatchId + "_harm", settings, x + pad, ref curY, propW);
            if (changed)
            {
                ApplyHarmonize(nsId, imagePath, cachePrefix, settings);
                _unsavedChanges = true;
            }
        }
    }

    /// <summary>Regenerate all harmonized textures for the currently selected element/widget.
    /// Used by live preview timer.</summary>
    private void RegenerateAllHarmonized()
    {
        if (_device == null) return;

        if (ActiveTab == UIEditorTab.Elements && SelectedIndex >= 0 && SelectedIndex < _elements.Count)
        {
            var el = _elements[SelectedIndex];
            if (el.Harmonize != null && el.Harmonize.HasEffect)
            {
                string imgPath = el.Type == "image" ? el.ImagePath : "";
                string nsRef = el.Type == "nineSlice" ? el.NineSlice : "";
                ApplyHarmonize(nsRef, imgPath, "el:" + el.Id, el.Harmonize);
            }
        }
        else if (ActiveTab == UIEditorTab.Widgets && SelectedIndex >= 0 && SelectedIndex < _widgets.Count)
        {
            var wd = _widgets[SelectedIndex];
            if (wd.BgHarmonize != null && wd.BgHarmonize.HasEffect)
                ApplyHarmonize(wd.Background, "", "bg:" + wd.Id, wd.BgHarmonize);
            if (wd.StencilHarmonize != null && wd.StencilHarmonize.HasEffect)
                ApplyHarmonize(wd.Stencil, wd.StencilImagePath, "st:" + wd.Id, wd.StencilHarmonize);
            if (wd.FrameHarmonize != null && wd.FrameHarmonize.HasEffect)
                ApplyHarmonize(wd.Frame, "", "fr:" + wd.Id, wd.FrameHarmonize);
        }
    }

    /// <summary>Regenerate harmonized textures from saved settings on load.</summary>
    private void RegenerateAllOnLoad()
    {
        foreach (var el in _elements)
        {
            if (el.Harmonize == null || !el.Harmonize.HasEffect) continue;
            string imgPath = el.Type == "image" ? el.ImagePath : "";
            string nsRef = el.Type == "nineSlice" ? el.NineSlice : "";
            ApplyHarmonize(nsRef, imgPath, "el:" + el.Id, el.Harmonize);
        }
        foreach (var wd in _widgets)
        {
            if (wd.BgHarmonize != null && wd.BgHarmonize.HasEffect)
                ApplyHarmonize(wd.Background, "", "bg:" + wd.Id, wd.BgHarmonize);
            if (wd.StencilHarmonize != null && wd.StencilHarmonize.HasEffect)
                ApplyHarmonize(wd.Stencil, wd.StencilImagePath, "st:" + wd.Id, wd.StencilHarmonize);
            if (wd.FrameHarmonize != null && wd.FrameHarmonize.HasEffect)
                ApplyHarmonize(wd.Frame, "", "fr:" + wd.Id, wd.FrameHarmonize);
        }
    }

    // ═══════════════════════════════════════
    //  Layout Helpers (delegates to shared utility)
    // ═══════════════════════════════════════

    private static System.Collections.Generic.List<Rectangle> ComputeLayoutRects(UIEditorWidgetDef def, int wdX, int wdY,
        System.Func<int, bool>? isHidden = null,
        System.Func<UIEditorChildDef, int, int>? childHeight = null,
        int instW = -1, int instH = -1)
        => Necroking.UI.WidgetLayoutUtils.ComputeLayoutRects(def, wdX, wdY, isHidden, childHeight, instW, instH);

    // ═══════════════════════════════════════
    //  Color Conversion Utilities
    // ═══════════════════════════════════════

    private static Color ByteColor(byte[] c, byte alphaOverride = 0)
        => Necroking.Core.ColorUtils.ByteColor(c, alphaOverride);

    private static HdrColor BytesToHdr(byte[] c)
        => Necroking.Core.ColorUtils.BytesToHdr(c);

    private static byte[] HdrToBytes(HdrColor h)
        => Necroking.Core.ColorUtils.HdrToBytes(h);

    private void DrawResizeHandle(int x, int y, int size)
    {
        var r = new Rectangle(x, y, size, size);
        bool hovered = InRect(r);
        DrawRect(r, hovered ? AccentColor : new Color(80, 80, 120, 255));
        DrawBorder(r, TextBright);
    }

    private bool HitHandle(Point mouse, int hx, int hy, int size)
    {
        return mouse.X >= hx && mouse.X < hx + size &&
               mouse.Y >= hy && mouse.Y < hy + size;
    }
}
