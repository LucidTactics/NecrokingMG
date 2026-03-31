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
        // Layer 1: Background (with inset)
        if (!string.IsNullOrEmpty(def.Background))
        {
            var bgNs = GetNineSlice(def.Background, "bg");
            if (bgNs != null)
            {
                int bi = def.BackgroundInset;
                var bgRect = new Rectangle(drawX + bi, drawY + bi, drawW - bi * 2, drawH - bi * 2);
                var bgColor = def.BackgroundTint != null ? ByteColor(def.BackgroundTint) : Color.White;
                bgNs.Draw(_sb, bgRect, bgColor, def.BackgroundScale);
            }
        }

        // Layer 2: Stencil (with inset) — nine-slice or stretched image
        {
            int si = def.StencilInset;
            var stRect = new Rectangle(drawX + si, drawY + si, drawW - si * 2, drawH - si * 2);
            var stColor = def.StencilTint != null ? ByteColor(def.StencilTint) : Color.White;

            if (!string.IsNullOrEmpty(def.StencilImagePath))
            {
                var stTex = GetTexture(def.StencilImagePath, "st");
                if (stTex != null) _sb.Draw(stTex, stRect, stColor);
            }
            else if (!string.IsNullOrEmpty(def.Stencil))
            {
                var stNs = GetNineSlice(def.Stencil, "st");
                if (stNs != null) stNs.Draw(_sb, stRect, stColor);
            }
        }

        // Layer 3: Frame (topmost, full bounds)
        if (drawFrame && !string.IsNullOrEmpty(def.Frame))
        {
            var frNs = GetNineSlice(def.Frame, "fr");
            if (frNs != null)
            {
                var frColor = def.FrameTint != null ? ByteColor(def.FrameTint) : Color.White;
                frNs.Draw(_sb, new Rectangle(drawX, drawY, drawW, drawH), frColor);
            }
        }
    }

    /// <summary>Draw just the frame layer of a widget.</summary>
    private void DrawWidgetFrame(UIEditorWidgetDef def, int drawX, int drawY, int drawW, int drawH)
    {
        if (!string.IsNullOrEmpty(def.Frame))
        {
            var frNs = GetNineSlice(def.Frame, "fr");
            if (frNs != null)
            {
                var frColor = def.FrameTint != null ? ByteColor(def.FrameTint) : Color.White;
                frNs.Draw(_sb, new Rectangle(drawX, drawY, drawW, drawH), frColor);
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
    {
        bool changed = false;
        int labelW = 80;
        int fieldX = x + labelW;

        // Target color — write live every frame (not just on OK) for live preview
        DrawText("Target:", new Vector2(x, curY + 2), TextDim);
        var targHdr = BytesToHdr(settings.TargetColor);
        DrawColorSwatch(swatchId, fieldX, curY, 40, 18, ref targHdr, hideIntensity: false);
        byte[] liveTarget = HdrToBytes(targHdr);
        if (!liveTarget.SequenceEqual(settings.TargetColor))
        {
            settings.TargetColor = liveTarget;
            changed = true;
        }
        curY += 22;

        // Mode toggle
        DrawText("Mode:", new Vector2(x, curY + 2), TextDim);
        if (DrawButton(settings.UseHcl ? "HCL" : "HSV", fieldX, curY, 48, 18))
        {
            settings.UseHcl = !settings.UseHcl;
            changed = true;
        }
        curY += 22;

        // 3 sliders
        string[] labels = settings.UseHcl ? new[] { "Hue:", "Chroma:", "Lum:" } : new[] { "Hue:", "Sat:", "Value:" };
        float[] vals = { settings.HueStrength, settings.SatStrength, settings.ValStrength };
        for (int i = 0; i < 3; i++)
        {
            float newVal = DrawSliderFloat($"{swatchId}_s{i}", labels[i], vals[i], 0f, 1f, x, curY, w);
            if (MathF.Abs(newVal - vals[i]) > 0.001f) { vals[i] = newVal; changed = true; }
            curY += 22;
        }
        settings.HueStrength = vals[0];
        settings.SatStrength = vals[1];
        settings.ValStrength = vals[2];

        return changed;
    }

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
                ApplyHarmonize(nsRef, imgPath, "el", el.Harmonize);
            }
        }
        else if (ActiveTab == UIEditorTab.Widgets && SelectedIndex >= 0 && SelectedIndex < _widgets.Count)
        {
            var wd = _widgets[SelectedIndex];
            if (wd.BgHarmonize != null && wd.BgHarmonize.HasEffect)
                ApplyHarmonize(wd.Background, "", "bg", wd.BgHarmonize);
            if (wd.StencilHarmonize != null && wd.StencilHarmonize.HasEffect)
                ApplyHarmonize(wd.Stencil, wd.StencilImagePath, "st", wd.StencilHarmonize);
            if (wd.FrameHarmonize != null && wd.FrameHarmonize.HasEffect)
                ApplyHarmonize(wd.Frame, "", "fr", wd.FrameHarmonize);
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
            ApplyHarmonize(nsRef, imgPath, "el", el.Harmonize);
        }
        foreach (var wd in _widgets)
        {
            if (wd.BgHarmonize != null && wd.BgHarmonize.HasEffect)
                ApplyHarmonize(wd.Background, "", "bg", wd.BgHarmonize);
            if (wd.StencilHarmonize != null && wd.StencilHarmonize.HasEffect)
                ApplyHarmonize(wd.Stencil, wd.StencilImagePath, "st", wd.StencilHarmonize);
            if (wd.FrameHarmonize != null && wd.FrameHarmonize.HasEffect)
                ApplyHarmonize(wd.Frame, "", "fr", wd.FrameHarmonize);
        }
    }

    // ═══════════════════════════════════════
    //  Layout Helpers
    // ═══════════════════════════════════════

    /// <summary>Calculate child rects with layout applied (horizontal/vertical auto-positioning).
    /// Matches C++ computeLayoutPositions: supports wrapping, per-side padding, spacing.</summary>
    private static System.Collections.Generic.List<Rectangle> ComputeLayoutRects(UIEditorWidgetDef def, int wdX, int wdY)
    {
        var rects = new System.Collections.Generic.List<Rectangle>();
        bool isHoriz = def.Layout == "horizontal";
        bool isVert = def.Layout == "vertical";
        bool useLayout = isHoriz || isVert;

        int padL = def.LayoutPadLeft > 0 ? def.LayoutPadLeft : def.LayoutPadding;
        int padR = def.LayoutPadRight > 0 ? def.LayoutPadRight : def.LayoutPadding;
        int padT = def.LayoutPadTop > 0 ? def.LayoutPadTop : def.LayoutPadding;
        int padB = def.LayoutPadBottom > 0 ? def.LayoutPadBottom : def.LayoutPadding;
        int spacX = def.LayoutSpacingX > 0 ? def.LayoutSpacingX : def.LayoutSpacing;
        int spacY = def.LayoutSpacingY > 0 ? def.LayoutSpacingY : def.LayoutSpacing;

        int curX = padL, curY = padT;
        int rowMaxH = 0, colMaxW = 0;

        for (int i = 0; i < def.Children.Count; i++)
        {
            var child = def.Children[i];
            int cw = child.Width > 0 ? child.Width : 100;
            int ch = child.Height > 0 ? child.Height : 40;

            if (useLayout && !child.IgnoreLayout)
            {
                if (isHoriz)
                {
                    // Wrap to next row if doesn't fit
                    if (curX > padL && curX + cw > def.Width - padR)
                    {
                        curY += rowMaxH + spacY;
                        curX = padL;
                        rowMaxH = 0;
                    }
                    // Layout positions children — child.X/Y are ignored (C++ overwrites them)
                    rects.Add(new Rectangle(wdX + curX, wdY + curY, cw, ch));
                    curX += cw + spacX;
                    if (ch > rowMaxH) rowMaxH = ch;
                }
                else // vertical
                {
                    // Wrap to next column if doesn't fit
                    if (curY > padT && curY + ch > def.Height - padB)
                    {
                        curX += colMaxW + spacX;
                        curY = padT;
                        colMaxW = 0;
                    }
                    // Layout positions children — child.X/Y are ignored (C++ overwrites them)
                    rects.Add(new Rectangle(wdX + curX, wdY + curY, cw, ch));
                    curY += ch + spacY;
                    if (cw > colMaxW) colMaxW = cw;
                }
            }
            else
            {
                // Anchor-based positioning (no layout or ignoreLayout)
                int col = child.Anchor % 3, row = child.Anchor / 3;
                int anchorX = col switch { 0 => 0, 1 => def.Width / 2, 2 => def.Width, _ => 0 };
                int anchorY = row switch { 0 => 0, 1 => def.Height / 2, 2 => def.Height, _ => 0 };
                rects.Add(new Rectangle(wdX + anchorX + child.X, wdY + anchorY + child.Y, cw, ch));
            }
        }
        return rects;
    }

    // ═══════════════════════════════════════
    //  Color Conversion Utilities
    // ═══════════════════════════════════════

    /// <summary>Convert byte[] RGBA to premultiplied Color for correct alpha blending.</summary>
    private static Color ByteColor(byte[] c, byte alphaOverride = 0)
    {
        byte a = alphaOverride > 0 ? alphaOverride : (c.Length > 3 ? c[3] : (byte)255);
        if (a == 255)
            return new Color((int)c[0], (int)c[1], (int)c[2], 255);
        float af = a / 255f;
        return new Color((byte)(c[0] * af), (byte)(c[1] * af), (byte)(c[2] * af), a);
    }

    private static HdrColor BytesToHdr(byte[] c)
    {
        return new HdrColor(c[0], c[1], c[2], c.Length > 3 ? c[3] : (byte)255);
    }

    private static byte[] HdrToBytes(HdrColor h)
    {
        return new[] { h.R, h.G, h.B, h.A };
    }

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
