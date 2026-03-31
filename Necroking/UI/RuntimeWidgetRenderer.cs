using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FontStashSharp;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Editor;
using Necroking.Render;

namespace Necroking.UI;

/// <summary>
/// Renders UIEditor widget definitions at runtime (in-game).
/// Reuses the same JSON data model as the editor (UIEditorWidgetDef, UIEditorElementDef, etc.)
/// but draws without any editor chrome (no selection borders, handles, placeholders).
/// </summary>
public class RuntimeWidgetRenderer
{
    private GraphicsDevice _device;
    private SpriteBatch _batch;
    private FontManager? _fontMgr;
    private Texture2D? _pixel;

    // Loaded definitions (shared with editor format)
    private readonly List<UIEditorNineSliceDef> _nineSliceDefs = new();
    private readonly List<UIEditorElementDef> _elementDefs = new();
    private readonly List<UIEditorWidgetDef> _widgetDefs = new();

    // Texture/nine-slice cache
    private readonly Dictionary<string, Texture2D> _textures = new();
    private readonly Dictionary<string, NineSlice> _nsInstances = new();

    // Harmonized texture/nine-slice cache (layer-prefixed keys like editor: "bg|texPath")
    private readonly Dictionary<string, Texture2D> _harmonizedTextures = new();
    private readonly Dictionary<string, NineSlice> _harmonizedNineSlices = new();

    // Per-widget text overrides: widgetInstanceId -> (childName -> text)
    // This allows runtime code to change text without mutating defs
    private readonly Dictionary<string, Dictionary<string, string>> _textOverrides = new();

    // Per-widget image overrides: widgetInstanceId -> (childName -> texturePath)
    private readonly Dictionary<string, Dictionary<string, string>> _imageOverrides = new();

    // Per-widget child visibility: widgetInstanceId -> (childIndex -> widgetId to use)
    // This allows swapping "Item Slot" for "Item Slot_Empty" at runtime
    private readonly Dictionary<string, Dictionary<int, string>> _childWidgetOverrides = new();

    public bool IsLoaded => _widgetDefs.Count > 0;

    public void Init(GraphicsDevice device, SpriteBatch batch, FontManager? fontMgr)
    {
        _device = device;
        _batch = batch;
        _fontMgr = fontMgr;

        _pixel = new Texture2D(device, 1, 1);
        _pixel.SetData(new[] { Color.White });
    }

    /// <summary>Load UI definitions from the same JSON files the editor uses.</summary>
    public bool LoadDefinitions(string dirPath)
    {
        bool ok = true;
        ok &= LoadNineSlices(Path.Combine(dirPath, "nine_slices.json"));
        ok &= LoadElements(Path.Combine(dirPath, "elements.json"));
        ok &= LoadWidgets(Path.Combine(dirPath, "widgets.json"));
        GenerateHarmonizedTextures();
        return ok;
    }

    public UIEditorWidgetDef? GetWidgetDef(string widgetId)
    {
        return _widgetDefs.FirstOrDefault(w => w.Id == widgetId);
    }

    public UIEditorElementDef? GetElementDef(string elementId)
    {
        return _elementDefs.FirstOrDefault(e => e.Id == elementId);
    }

    // ═══════════════════════════════════════
    //  Override API (for game code to modify widget content)
    // ═══════════════════════════════════════

    /// <summary>Set text for a named child within a widget instance.</summary>
    public void SetText(string instanceId, string childName, string text)
    {
        if (!_textOverrides.TryGetValue(instanceId, out var map))
        {
            map = new Dictionary<string, string>();
            _textOverrides[instanceId] = map;
        }
        map[childName] = text;
    }

    /// <summary>Set image path for a named child within a widget instance.</summary>
    public void SetImage(string instanceId, string childName, string imagePath)
    {
        if (!_imageOverrides.TryGetValue(instanceId, out var map))
        {
            map = new Dictionary<string, string>();
            _imageOverrides[instanceId] = map;
        }
        map[childName] = imagePath;
    }

    /// <summary>Override which widget a child slot uses (e.g., swap "Item Slot" for "Item Slot_Empty").</summary>
    public void SetChildWidget(string instanceId, int childIndex, string widgetId)
    {
        if (!_childWidgetOverrides.TryGetValue(instanceId, out var map))
        {
            map = new Dictionary<int, string>();
            _childWidgetOverrides[instanceId] = map;
        }
        map[childIndex] = widgetId;
    }

    /// <summary>Clear all overrides for a widget instance.</summary>
    public void ClearOverrides(string instanceId)
    {
        _textOverrides.Remove(instanceId);
        _imageOverrides.Remove(instanceId);
        _childWidgetOverrides.Remove(instanceId);
    }

    // ═══════════════════════════════════════
    //  Drawing
    // ═══════════════════════════════════════

    /// <summary>Draw a widget at screen position. instanceId is used for override lookups.</summary>
    public void DrawWidget(string widgetId, int x, int y, string? instanceId = null)
    {
        var def = _widgetDefs.FirstOrDefault(w => w.Id == widgetId);
        if (def == null) return;
        DrawWidgetDef(def, x, y, def.Width, def.Height, instanceId ?? widgetId);
    }

    /// <summary>Draw a widget def at a specific rect.</summary>
    public void DrawWidgetDef(UIEditorWidgetDef def, int x, int y, int w, int h, string instanceId)
    {
        // Background + stencil layers
        DrawWidgetLayers(def, x, y, w, h, drawFrame: false);

        // Children (between stencil and frame)
        var rects = ComputeLayoutRects(def, x, y);
        for (int i = 0; i < def.Children.Count && i < rects.Count; i++)
        {
            // Check for child widget override
            string? overrideWidget = null;
            if (_childWidgetOverrides.TryGetValue(instanceId, out var cwMap))
                cwMap.TryGetValue(i, out overrideWidget);

            DrawChild(def.Children[i], rects[i], instanceId, i, overrideWidget);
        }

        // Frame on top
        DrawWidgetFrame(def, x, y, w, h);
    }

    // ═══════════════════════════════════════
    //  Hit testing
    // ═══════════════════════════════════════

    /// <summary>Check if a point is within the widget bounds.</summary>
    public bool HitTest(string widgetId, int widgetX, int widgetY, int pointX, int pointY)
    {
        var def = _widgetDefs.FirstOrDefault(w => w.Id == widgetId);
        if (def == null) return false;
        return pointX >= widgetX && pointX < widgetX + def.Width &&
               pointY >= widgetY && pointY < widgetY + def.Height;
    }

    /// <summary>Find which layout child index a point hits (-1 if none).</summary>
    public int HitTestChild(string widgetId, int widgetX, int widgetY, int pointX, int pointY)
    {
        var def = _widgetDefs.FirstOrDefault(w => w.Id == widgetId);
        if (def == null) return -1;
        var rects = ComputeLayoutRects(def, widgetX, widgetY);
        for (int i = 0; i < rects.Count; i++)
            if (rects[i].Contains(pointX, pointY)) return i;
        return -1;
    }

    // ═══════════════════════════════════════
    //  Internal rendering
    // ═══════════════════════════════════════

    private void DrawChild(UIEditorChildDef child, Rectangle rect, string instanceId, int childIndex, string? overrideWidget)
    {
        bool drawn = false;
        string widgetRef = overrideWidget ?? child.Widget;

        // Widget child — render nested widget layers + children
        if (!string.IsNullOrEmpty(widgetRef))
        {
            var widgetDef = _widgetDefs.FirstOrDefault(w => w.Id == widgetRef);
            if (widgetDef != null)
            {
                drawn = true;
                DrawWidgetLayers(widgetDef, rect.X, rect.Y, rect.Width, rect.Height, drawFrame: false);

                // Nested children — use a sub-instance ID for override scoping
                string subId = $"{instanceId}.{childIndex}";
                var nestedRects = ComputeLayoutRects(widgetDef, rect.X, rect.Y);
                for (int ci = 0; ci < widgetDef.Children.Count && ci < nestedRects.Count; ci++)
                {
                    string? nestedOverride = null;
                    if (_childWidgetOverrides.TryGetValue(subId, out var cwMap))
                        cwMap.TryGetValue(ci, out nestedOverride);
                    DrawChild(widgetDef.Children[ci], nestedRects[ci], subId, ci, nestedOverride);
                }

                DrawWidgetFrame(widgetDef, rect.X, rect.Y, rect.Width, rect.Height);
            }
        }

        // Element child
        if (!drawn && !string.IsNullOrEmpty(child.Element))
        {
            var elemDef = _elementDefs.FirstOrDefault(e => e.Id == child.Element);
            if (elemDef != null)
            {
                byte[] tc = elemDef.TintColor ?? new byte[] { 255, 255, 255, 255 };
                var tint = ByteColor(tc);

                if (elemDef.Type == "text")
                {
                    drawn = true;
                    // Check for text override
                    string text = "";
                    if (_textOverrides.TryGetValue(instanceId, out var tMap) && tMap.TryGetValue(child.Name, out var ov))
                        text = ov;
                    else if (!string.IsNullOrEmpty(child.DefaultText))
                        text = child.DefaultText;
                    else if (!string.IsNullOrEmpty(elemDef.DefaultText))
                        text = elemDef.DefaultText;

                    if (!string.IsNullOrEmpty(text))
                        DrawTextElement(text, elemDef, rect);
                }
                else if (elemDef.Type == "image")
                {
                    // Check for image override
                    string imgPath = elemDef.ImagePath;
                    if (_imageOverrides.TryGetValue(instanceId, out var iMap) && iMap.TryGetValue(child.Name, out var ov))
                        imgPath = ov;

                    if (!string.IsNullOrEmpty(imgPath))
                    {
                        var imgTex = GetOrLoadTexture(imgPath);
                        if (imgTex != null) { _batch.Draw(imgTex, rect, tint); drawn = true; }
                    }
                }
                else if (!string.IsNullOrEmpty(elemDef.NineSlice))
                {
                    var childNs = GetOrLoadNineSlice(elemDef.NineSlice);
                    if (childNs != null) { childNs.Draw(_batch, rect, tint); drawn = true; }
                }

                // Stroke
                if (elemDef.StrokeThickness > 0)
                    DrawStroke(elemDef, rect);
            }
        }

        // DefaultText for non-text elements (labels on buttons etc.)
        if (!string.IsNullOrEmpty(child.DefaultText))
        {
            var elemDef2 = !string.IsNullOrEmpty(child.Element)
                ? _elementDefs.FirstOrDefault(e => e.Id == child.Element) : null;
            if (elemDef2 == null || elemDef2.Type != "text")
            {
                var font = _fontMgr?.GetFont(14);
                if (font != null)
                    _batch.DrawString(font, child.DefaultText,
                        new Vector2(rect.X + 4, rect.Y + rect.Height / 2f - 7), new Color(200, 200, 200, 180));
            }
        }
    }

    private void DrawTextElement(string text, UIEditorElementDef elemDef, Rectangle rect)
    {
        var tr = elemDef.TextRegion;
        var fontColor = tr != null ? ByteColor(tr.FontColor) : Color.White;
        int fontSize = tr?.FontSize ?? 14;
        string fontFamily = tr?.FontFamily ?? "";

        var dynFont = _fontMgr?.GetFont(fontSize, string.IsNullOrEmpty(fontFamily) ? null : fontFamily);
        Vector2 textSize = dynFont?.MeasureString(text) ?? new Vector2(text.Length * 8, 14);

        float tx = (tr?.Align ?? "left") switch
        {
            "center" => rect.X + (rect.Width - textSize.X) / 2,
            "right" => rect.X + rect.Width - textSize.X - 2,
            _ => rect.X + 2
        };
        float ty = (tr?.VAlign ?? "top") switch
        {
            "center" => rect.Y + (rect.Height - textSize.Y) / 2,
            "bottom" => rect.Y + rect.Height - textSize.Y - 2,
            _ => rect.Y + 2
        };

        if (dynFont != null)
            _batch.DrawString(dynFont, text, new Vector2(tx, ty), fontColor);
    }

    private void DrawStroke(UIEditorElementDef elemDef, Rectangle rect)
    {
        if (_pixel == null) return;
        var strokeCol = ByteColor(elemDef.StrokeColor);
        int t = elemDef.StrokeThickness;
        Rectangle sr = elemDef.StrokeMode switch
        {
            "outside" => new Rectangle(rect.X - t, rect.Y - t, rect.Width + t * 2, rect.Height + t * 2),
            "center" => new Rectangle(rect.X - t / 2, rect.Y - t / 2, rect.Width + t, rect.Height + t),
            _ => rect
        };
        _batch.Draw(_pixel, new Rectangle(sr.X, sr.Y, sr.Width, t), strokeCol);
        _batch.Draw(_pixel, new Rectangle(sr.X, sr.Y + sr.Height - t, sr.Width, t), strokeCol);
        _batch.Draw(_pixel, new Rectangle(sr.X, sr.Y + t, t, sr.Height - t * 2), strokeCol);
        _batch.Draw(_pixel, new Rectangle(sr.X + sr.Width - t, sr.Y + t, t, sr.Height - t * 2), strokeCol);
    }

    // ═══════════════════════════════════════
    //  Widget layer rendering (matches editor helpers)
    // ═══════════════════════════════════════

    private void DrawWidgetLayers(UIEditorWidgetDef def, int x, int y, int w, int h, bool drawFrame)
    {
        // Background (use harmonized if available)
        if (!string.IsNullOrEmpty(def.Background))
        {
            var bgNs = GetNineSlice(def.Background, "bg");
            if (bgNs != null)
            {
                int bi = def.BackgroundInset;
                var bgRect = new Rectangle(x + bi, y + bi, w - bi * 2, h - bi * 2);
                var bgColor = def.BackgroundTint != null ? ByteColor(def.BackgroundTint) : Color.White;
                bgNs.Draw(_batch, bgRect, bgColor, def.BackgroundScale);
            }
        }

        // Stencil (use harmonized if available)
        {
            int si = def.StencilInset;
            var stRect = new Rectangle(x + si, y + si, w - si * 2, h - si * 2);
            var stColor = def.StencilTint != null ? ByteColor(def.StencilTint) : Color.White;

            if (!string.IsNullOrEmpty(def.StencilImagePath))
            {
                var stTex = GetTexture(def.StencilImagePath, "st");
                if (stTex != null) _batch.Draw(stTex, stRect, stColor);
            }
            else if (!string.IsNullOrEmpty(def.Stencil))
            {
                var stNs = GetNineSlice(def.Stencil, "st");
                if (stNs != null) stNs.Draw(_batch, stRect, stColor);
            }
        }

        // Frame (use harmonized if available)
        if (drawFrame && !string.IsNullOrEmpty(def.Frame))
        {
            var frNs = GetNineSlice(def.Frame, "fr");
            if (frNs != null)
            {
                var frColor = def.FrameTint != null ? ByteColor(def.FrameTint) : Color.White;
                frNs.Draw(_batch, new Rectangle(x, y, w, h), frColor);
            }
        }
    }

    private void DrawWidgetFrame(UIEditorWidgetDef def, int x, int y, int w, int h)
    {
        if (!string.IsNullOrEmpty(def.Frame))
        {
            var frNs = GetNineSlice(def.Frame, "fr");
            if (frNs != null)
            {
                var frColor = def.FrameTint != null ? ByteColor(def.FrameTint) : Color.White;
                frNs.Draw(_batch, new Rectangle(x, y, w, h), frColor);
            }
        }
    }

    // ═══════════════════════════════════════
    //  Layout (reuses same algorithm as editor)
    // ═══════════════════════════════════════

    private static List<Rectangle> ComputeLayoutRects(UIEditorWidgetDef def, int wdX, int wdY)
    {
        var rects = new List<Rectangle>();
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
                    if (curX > padL && curX + cw > def.Width - padR)
                    {
                        curY += rowMaxH + spacY;
                        curX = padL;
                        rowMaxH = 0;
                    }
                    rects.Add(new Rectangle(wdX + curX, wdY + curY, cw, ch));
                    curX += cw + spacX;
                    if (ch > rowMaxH) rowMaxH = ch;
                }
                else
                {
                    if (curY > padT && curY + ch > def.Height - padB)
                    {
                        curX += colMaxW + spacX;
                        curY = padT;
                        colMaxW = 0;
                    }
                    rects.Add(new Rectangle(wdX + curX, wdY + curY, cw, ch));
                    curY += ch + spacY;
                    if (cw > colMaxW) colMaxW = cw;
                }
            }
            else
            {
                int col = child.Anchor % 3, row = child.Anchor / 3;
                int anchorX = col switch { 0 => 0, 1 => def.Width / 2, 2 => def.Width, _ => 0 };
                int anchorY = row switch { 0 => 0, 1 => def.Height / 2, 2 => def.Height, _ => 0 };
                rects.Add(new Rectangle(wdX + anchorX + child.X, wdY + anchorY + child.Y, cw, ch));
            }
        }
        return rects;
    }

    // ═══════════════════════════════════════
    //  Asset loading
    // ═══════════════════════════════════════

    private Texture2D? GetOrLoadTexture(string texPath)
    {
        if (string.IsNullOrEmpty(texPath)) return null;
        if (_textures.TryGetValue(texPath, out var tex)) return tex;
        if (!File.Exists(texPath)) return null;
        try
        {
            tex = TextureUtil.LoadPremultiplied(_device, texPath);
            _textures[texPath] = tex;
            return tex;
        }
        catch { return null; }
    }

    private NineSlice? GetOrLoadNineSlice(string nsId)
    {
        if (string.IsNullOrEmpty(nsId)) return null;
        if (_nsInstances.TryGetValue(nsId, out var ns)) return ns;

        var def = _nineSliceDefs.FirstOrDefault(d => d.Id == nsId);
        if (def == null) return null;

        var nsDef = new NineSliceDef
        {
            Id = def.Id, TexturePath = def.Texture,
            BorderLeft = def.BorderLeft, BorderRight = def.BorderRight,
            BorderTop = def.BorderTop, BorderBottom = def.BorderBottom,
            TileEdges = def.TileEdges,
        };
        ns = new NineSlice();
        if (!ns.Load(_device, nsDef)) return null;
        _nsInstances[nsId] = ns;
        return ns;
    }

    // ═══════════════════════════════════════
    //  JSON Loading (same format as editor save)
    // ═══════════════════════════════════════

    private bool LoadNineSlices(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("nineSlices", out var arr)) return false;
            foreach (var el in arr.EnumerateArray())
            {
                var ns = new UIEditorNineSliceDef
                {
                    Id = el.GetProperty("id").GetString() ?? "",
                    Texture = el.GetProperty("texture").GetString() ?? "",
                    BorderLeft = el.TryGetProperty("borderLeft", out var bl) ? bl.GetInt32() : 0,
                    BorderRight = el.TryGetProperty("borderRight", out var br) ? br.GetInt32() : 0,
                    BorderTop = el.TryGetProperty("borderTop", out var bt) ? bt.GetInt32() : 0,
                    BorderBottom = el.TryGetProperty("borderBottom", out var bb) ? bb.GetInt32() : 0,
                    TileEdges = el.TryGetProperty("tileEdges", out var te) && te.GetBoolean(),
                };
                _nineSliceDefs.Add(ns);
            }
            return true;
        }
        catch { return false; }
    }

    private bool LoadElements(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("elements", out var arr)) return false;
            foreach (var el in arr.EnumerateArray())
            {
                var elem = new UIEditorElementDef
                {
                    Id = el.GetProperty("id").GetString() ?? "",
                    Type = el.TryGetProperty("type", out var tp) ? tp.GetString() ?? "nineSlice" : "nineSlice",
                    NineSlice = el.TryGetProperty("nineSlice", out var ns) ? ns.GetString() ?? "" : "",
                    ImagePath = el.TryGetProperty("imagePath", out var ip) ? ip.GetString() ?? "" : "",
                    Width = el.TryGetProperty("width", out var w) ? w.GetInt32() : 100,
                    Height = el.TryGetProperty("height", out var h) ? h.GetInt32() : 40,
                    DefaultText = el.TryGetProperty("defaultText", out var dt) ? dt.GetString() ?? "" : "",
                };
                if (el.TryGetProperty("tintColor", out var tc) && tc.ValueKind == JsonValueKind.Array)
                {
                    var arr2 = tc.EnumerateArray().ToArray();
                    if (arr2.Length >= 4)
                        elem.TintColor = new byte[] { (byte)arr2[0].GetInt32(), (byte)arr2[1].GetInt32(),
                            (byte)arr2[2].GetInt32(), (byte)arr2[3].GetInt32() };
                }
                if (el.TryGetProperty("textRegion", out var trEl))
                {
                    elem.TextRegion = new UIEditorTextRegion
                    {
                        X = trEl.TryGetProperty("x", out var tx) ? tx.GetInt32() : 0,
                        Y = trEl.TryGetProperty("y", out var ty) ? ty.GetInt32() : 0,
                        W = trEl.TryGetProperty("w", out var tw) ? tw.GetInt32() : 0,
                        H = trEl.TryGetProperty("h", out var th) ? th.GetInt32() : 0,
                        Align = trEl.TryGetProperty("align", out var al) ? al.GetString() ?? "left" : "left",
                        VAlign = trEl.TryGetProperty("valign", out var va) ? va.GetString() ?? "top" : "top",
                        FontFamily = trEl.TryGetProperty("fontFamily", out var ff) ? ff.GetString() ?? "" : "",
                        FontSize = trEl.TryGetProperty("fontSize", out var fs) ? fs.GetInt32() : 14,
                        FontColor = new byte[] { 255, 255, 255, 255 },
                    };
                    if (trEl.TryGetProperty("fontColor", out var fcArr) && fcArr.ValueKind == JsonValueKind.Array)
                    {
                        var fca = fcArr.EnumerateArray().ToArray();
                        if (fca.Length >= 4)
                            elem.TextRegion.FontColor = new byte[] { (byte)fca[0].GetInt32(), (byte)fca[1].GetInt32(),
                                (byte)fca[2].GetInt32(), (byte)fca[3].GetInt32() };
                    }
                }
                if (el.TryGetProperty("strokeThickness", out var st))
                    elem.StrokeThickness = st.GetInt32();
                if (el.TryGetProperty("strokeColor", out var sc) && sc.ValueKind == JsonValueKind.Array)
                {
                    var sca = sc.EnumerateArray().ToArray();
                    if (sca.Length >= 4)
                        elem.StrokeColor = new byte[] { (byte)sca[0].GetInt32(), (byte)sca[1].GetInt32(),
                            (byte)sca[2].GetInt32(), (byte)sca[3].GetInt32() };
                }
                if (el.TryGetProperty("strokeMode", out var sm))
                    elem.StrokeMode = sm.GetString() ?? "inside";

                _elementDefs.Add(elem);
            }
            return true;
        }
        catch { return false; }
    }

    private bool LoadWidgets(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("widgets", out var arr)) return false;
            foreach (var el in arr.EnumerateArray())
            {
                var wd = new UIEditorWidgetDef
                {
                    Id = el.GetProperty("id").GetString() ?? "",
                    Background = el.TryGetProperty("background", out var bg) ? bg.GetString() ?? "" : "",
                    Stencil = el.TryGetProperty("stencil", out var st) ? st.GetString() ?? "" : "",
                    StencilImagePath = el.TryGetProperty("stencilImagePath", out var sip) ? sip.GetString() ?? "" : "",
                    Frame = el.TryGetProperty("frame", out var fr) ? fr.GetString() ?? "" : "",
                    Width = el.TryGetProperty("width", out var w) ? w.GetInt32() : 200,
                    Height = el.TryGetProperty("height", out var h) ? h.GetInt32() : 100,
                    BackgroundScale = el.TryGetProperty("backgroundScale", out var bs) ? bs.GetSingle() : 1f,
                    BackgroundInset = el.TryGetProperty("backgroundInset", out var bii) ? bii.GetInt32() : 0,
                    StencilInset = el.TryGetProperty("stencilInset", out var sii) ? sii.GetInt32() : 0,
                    Modal = el.TryGetProperty("modal", out var md) && md.GetBoolean(),
                    Layout = el.TryGetProperty("layout", out var ly) ? ly.GetString() ?? "none" : "none",
                    LayoutPadding = el.TryGetProperty("layoutPadding", out var lp) ? lp.GetInt32() : 0,
                    LayoutSpacing = el.TryGetProperty("layoutSpacing", out var ls) ? ls.GetInt32() : 0,
                    LayoutPadTop = el.TryGetProperty("layoutPadTop", out var lpt) ? lpt.GetInt32() : 0,
                    LayoutPadBottom = el.TryGetProperty("layoutPadBottom", out var lpb) ? lpb.GetInt32() : 0,
                    LayoutPadLeft = el.TryGetProperty("layoutPadLeft", out var lpl) ? lpl.GetInt32() : 0,
                    LayoutPadRight = el.TryGetProperty("layoutPadRight", out var lpr) ? lpr.GetInt32() : 0,
                    LayoutSpacingX = el.TryGetProperty("layoutSpacingX", out var lsx) ? lsx.GetInt32() : 0,
                    LayoutSpacingY = el.TryGetProperty("layoutSpacingY", out var lsy) ? lsy.GetInt32() : 0,
                };
                wd.BackgroundTint = ReadTintArray(el, "backgroundTint");
                wd.StencilTint = ReadTintArray(el, "stencilTint");
                wd.FrameTint = ReadTintArray(el, "frameTint");
                wd.BgHarmonize = ReadHarmonizeSettings(el, "bgHarmonize");
                wd.StencilHarmonize = ReadHarmonizeSettings(el, "stencilHarmonize");
                wd.FrameHarmonize = ReadHarmonizeSettings(el, "frameHarmonize");

                // Children
                if (el.TryGetProperty("children", out var chArr) && chArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var ch in chArr.EnumerateArray())
                    {
                        var child = new UIEditorChildDef
                        {
                            Name = ch.TryGetProperty("name", out var cn) ? cn.GetString() ?? "" : "",
                            Element = ch.TryGetProperty("element", out var ce) ? ce.GetString() ?? "" : "",
                            Widget = ch.TryGetProperty("widget", out var cw) ? cw.GetString() ?? "" : "",
                            X = ch.TryGetProperty("x", out var cx) ? cx.GetInt32() : 0,
                            Y = ch.TryGetProperty("y", out var cy) ? cy.GetInt32() : 0,
                            Width = ch.TryGetProperty("width", out var cww) ? cww.GetInt32() : 0,
                            Height = ch.TryGetProperty("height", out var chh) ? chh.GetInt32() : 0,
                            Anchor = ch.TryGetProperty("anchor", out var ca) ? ca.GetInt32() : 0,
                            Interactive = ch.TryGetProperty("interactive", out var ci) && ci.GetBoolean(),
                            DefaultText = ch.TryGetProperty("defaultText", out var cdt) ? cdt.GetString() ?? "" : "",
                            IgnoreLayout = ch.TryGetProperty("ignoreLayout", out var il) && il.GetBoolean(),
                        };
                        wd.Children.Add(child);
                    }
                }
                _widgetDefs.Add(wd);
            }
            return true;
        }
        catch { return false; }
    }

    private static byte[]? ReadTintArray(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            var a = arr.EnumerateArray().ToArray();
            if (a.Length >= 4)
                return new byte[] { (byte)a[0].GetInt32(), (byte)a[1].GetInt32(),
                    (byte)a[2].GetInt32(), (byte)a[3].GetInt32() };
        }
        return null;
    }

    private static HarmonizeSettings? ReadHarmonizeSettings(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var h) || h.ValueKind != JsonValueKind.Object)
            return null;
        var s = new HarmonizeSettings
        {
            HueStrength = h.TryGetProperty("hueStrength", out var hs) ? hs.GetSingle() : 0,
            SatStrength = h.TryGetProperty("satStrength", out var ss) ? ss.GetSingle() : 0,
            ValStrength = h.TryGetProperty("valStrength", out var vs) ? vs.GetSingle() : 0,
            UseHcl = h.TryGetProperty("useHcl", out var hcl) && hcl.GetBoolean(),
        };
        if (h.TryGetProperty("targetColor", out var tc) && tc.ValueKind == JsonValueKind.Array)
        {
            var a = tc.EnumerateArray().ToArray();
            if (a.Length >= 4)
                s.TargetColor = new byte[] { (byte)a[0].GetInt32(), (byte)a[1].GetInt32(),
                    (byte)a[2].GetInt32(), (byte)a[3].GetInt32() };
        }
        return s.HasEffect ? s : null;
    }

    // ═══════════════════════════════════════
    //  Color utility (matches editor ByteColor)
    // ═══════════════════════════════════════

    private static Color ByteColor(byte[] c, byte alphaOverride = 0)
    {
        byte a = alphaOverride > 0 ? alphaOverride : (c.Length > 3 ? c[3] : (byte)255);
        if (a == 255) return new Color((int)c[0], (int)c[1], (int)c[2], 255);
        float af = a / 255f;
        return new Color((byte)(c[0] * af), (byte)(c[1] * af), (byte)(c[2] * af), a);
    }

    /// <summary>Debug: dump override state for an instance.</summary>
    public string DebugDumpOverrides(string instanceId)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Overrides for '{instanceId}':");
        if (_childWidgetOverrides.TryGetValue(instanceId, out var cwMap))
        {
            foreach (var kv in cwMap)
                sb.AppendLine($"  childWidget[{kv.Key}] = {kv.Value}");
        }
        else sb.AppendLine("  (no child widget overrides)");

        // Check sub-instance overrides
        foreach (var key in _textOverrides.Keys)
        {
            if (key.StartsWith(instanceId))
            {
                sb.Append($"  text[{key}]: ");
                foreach (var kv in _textOverrides[key])
                    sb.Append($"{kv.Key}={kv.Value} ");
                sb.AppendLine();
            }
        }
        foreach (var key in _imageOverrides.Keys)
        {
            if (key.StartsWith(instanceId))
            {
                sb.Append($"  image[{key}]: ");
                foreach (var kv in _imageOverrides[key])
                    sb.Append($"{kv.Key}={kv.Value} ");
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }

    // ═══════════════════════════════════════
    //  Harmonization
    // ═══════════════════════════════════════

    /// <summary>Generate harmonized textures for all widgets that have harmonize settings.</summary>
    private void GenerateHarmonizedTextures()
    {
        foreach (var wd in _widgetDefs)
        {
            if (wd.BgHarmonize != null && wd.BgHarmonize.HasEffect)
                ApplyHarmonize(wd.Background, "", "bg", wd.BgHarmonize);
            if (wd.StencilHarmonize != null && wd.StencilHarmonize.HasEffect)
                ApplyHarmonize(wd.Stencil, wd.StencilImagePath, "st", wd.StencilHarmonize);
            if (wd.FrameHarmonize != null && wd.FrameHarmonize.HasEffect)
                ApplyHarmonize(wd.Frame, "", "fr", wd.FrameHarmonize);
        }
    }

    /// <summary>Apply harmonization to a texture and cache the result (mirrors editor ApplyHarmonize).</summary>
    private void ApplyHarmonize(string nsId, string imagePath, string cachePrefix, HarmonizeSettings settings)
    {
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
            var nsDef = _nineSliceDefs.FirstOrDefault(n => n.Id == nsId);
            if (nsDef != null && !string.IsNullOrEmpty(nsDef.Texture))
            {
                sourceTex = GetOrLoadTexture(nsDef.Texture);
                texKey = nsDef.Texture;
                nsLookupId = nsId;
            }
        }

        if (sourceTex == null || string.IsNullOrEmpty(texKey)) return;

        string cacheKey = cachePrefix + "|" + texKey;
        var harmonized = ColorHarmonizer.HarmonizeTexture(sourceTex, _device, settings);
        if (harmonized == null) return;

        _harmonizedTextures[cacheKey] = harmonized;

        // Also build a harmonized NineSlice if this was a nine-slice source
        if (!string.IsNullOrEmpty(nsLookupId))
        {
            var nsDef = _nineSliceDefs.FirstOrDefault(n => n.Id == nsLookupId);
            if (nsDef != null)
            {
                var nsInst = new NineSlice();
                nsInst.LoadFromTexture(harmonized, nsDef.BorderLeft, nsDef.BorderRight,
                    nsDef.BorderTop, nsDef.BorderBottom, nsDef.TileEdges);
                _harmonizedNineSlices[cachePrefix + "|" + nsLookupId] = nsInst;
            }
        }
    }

    /// <summary>Get harmonized nine-slice if available, otherwise original.</summary>
    private NineSlice? GetNineSlice(string nsId, string cachePrefix)
    {
        string cacheKey = cachePrefix + "|" + nsId;
        if (_harmonizedNineSlices.TryGetValue(cacheKey, out var harmonized))
            return harmonized;
        return GetOrLoadNineSlice(nsId);
    }

    /// <summary>Get harmonized texture if available, otherwise original.</summary>
    private Texture2D? GetTexture(string path, string cachePrefix)
    {
        string cacheKey = cachePrefix + "|" + path;
        if (_harmonizedTextures.TryGetValue(cacheKey, out var harmonized))
            return harmonized;
        return GetOrLoadTexture(path);
    }

    public void Shutdown()
    {
        foreach (var ns in _nsInstances.Values) ns.Unload();
        _nsInstances.Clear();
        foreach (var ns in _harmonizedNineSlices.Values) ns.Unload();
        _harmonizedNineSlices.Clear();
        foreach (var tex in _textures.Values) tex.Dispose();
        _textures.Clear();
        foreach (var tex in _harmonizedTextures.Values) tex.Dispose();
        _harmonizedTextures.Clear();
        _pixel?.Dispose();
    }
}
