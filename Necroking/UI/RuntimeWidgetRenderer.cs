using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FontStashSharp;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Core;
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
    private readonly Dictionary<string, HashSet<string>> _hiddenChildren = new();
    private readonly Dictionary<string, Dictionary<string, Color>> _textColorOverrides = new();
    // Per-widget image/nine-slice tint: widgetInstanceId -> (childName -> tint).
    // Lets runtime code recolor a backing/icon element (e.g. dim an unselected
    // tab, brighten the active one) without mutating the shared element def.
    private readonly Dictionary<string, Dictionary<string, Color>> _elementTintOverrides = new();

    // Per-widget child height override: widgetInstanceId -> (childName -> height).
    // Lets runtime code grow a section (e.g. the unit sheet's Abilities & Buffs
    // row wrapping to multiple rows) so the auto-height panel + frame expand.
    private readonly Dictionary<string, Dictionary<string, int>> _childHeightOverrides = new();

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

    /// <summary>Override the font color of a named text child within a widget
    /// instance (e.g. green/red tabulation values).</summary>
    public void SetTextColor(string instanceId, string childName, byte rr, byte g, byte b, byte a = 255)
    {
        if (!_textColorOverrides.TryGetValue(instanceId, out var map))
        {
            map = new Dictionary<string, Color>();
            _textColorOverrides[instanceId] = map;
        }
        map[childName] = new Color(rr, g, b, a);
    }

    /// <summary>Override the tint of a named image / nine-slice child within a
    /// widget instance. Multiplies the (possibly harmonized) source texture, so
    /// white = unchanged, grey = dimmed. Used to show selected/pressed button
    /// state (bright = active, dim = inactive).</summary>
    public void SetElementTint(string instanceId, string childName, Color tint)
    {
        if (!_elementTintOverrides.TryGetValue(instanceId, out var map))
        {
            map = new Dictionary<string, Color>();
            _elementTintOverrides[instanceId] = map;
        }
        map[childName] = tint;
    }

    /// <summary>Override a child's layout height within a widget instance, used
    /// to grow an auto-height section (e.g. a wrapping icon row). Pass a value
    /// &lt;= 0 to clear the override for that child.</summary>
    public void SetChildHeight(string instanceId, string childName, int height)
    {
        if (!_childHeightOverrides.TryGetValue(instanceId, out var map))
        {
            map = new Dictionary<string, int>();
            _childHeightOverrides[instanceId] = map;
        }
        if (height > 0) map[childName] = height;
        else map.Remove(childName);
    }

    /// <summary>A child's effective layout height: instance override if set,
    /// else the auto-measured / def height.</summary>
    private int ChildHeightFor(UIEditorChildDef child, string instanceId, int childIndex)
    {
        if (_childHeightOverrides.TryGetValue(instanceId, out var m) && m.TryGetValue(child.Name, out var h))
            return h;
        return ChildLayoutHeight(child, $"{instanceId}.{childIndex}");
    }

    /// <summary>Hide or show a named child within a widget instance (hidden
    /// children are skipped entirely, including nested widget content). On
    /// AutoSizeHeight vertical-layout widgets, hidden children also collapse
    /// out of the layout (rows above/below close the gap).</summary>
    public void SetHidden(string instanceId, string childName, bool hidden)
    {
        if (!_hiddenChildren.TryGetValue(instanceId, out var set))
        {
            if (!hidden) return;
            set = new HashSet<string>();
            _hiddenChildren[instanceId] = set;
        }
        if (hidden) set.Add(childName);
        else set.Remove(childName);
    }

    /// <summary>Clear all overrides for a widget instance.</summary>
    public void ClearOverrides(string instanceId)
    {
        _textOverrides.Remove(instanceId);
        _imageOverrides.Remove(instanceId);
        _childWidgetOverrides.Remove(instanceId);
        _hiddenChildren.Remove(instanceId);
        _textColorOverrides.Remove(instanceId);
        _elementTintOverrides.Remove(instanceId);
        _childHeightOverrides.Remove(instanceId);
    }

    /// <summary>Clear overrides for an instance AND all its nested
    /// sub-instances ("{id}", "{id}.3", "{id}.3.0", ...).</summary>
    public void ClearOverridesRecursive(string instanceId)
    {
        string prefix = instanceId + ".";
        foreach (var dict in new System.Collections.IDictionary[]
                 { _textOverrides, _imageOverrides, _childWidgetOverrides, _hiddenChildren,
                   _textColorOverrides, _elementTintOverrides, _childHeightOverrides })
        {
            var stale = new List<object>();
            foreach (var key in dict.Keys)
                if (key is string k && (k == instanceId || k.StartsWith(prefix))) stale.Add(key);
            foreach (var k in stale) dict.Remove(k);
        }
    }

    /// <summary>Screen rect of a named child when the widget is drawn at (x,y).
    /// Returns Rectangle.Empty if the widget or child doesn't exist.</summary>
    public Rectangle GetChildRect(string widgetId, string childName, int x, int y, string? instanceId = null)
    {
        var def = _widgetDefs.FirstOrDefault(w => w.Id == widgetId);
        if (def == null) return Rectangle.Empty;
        var rects = ComputeInstanceRects(def, x, y, instanceId ?? widgetId);
        for (int i = 0; i < def.Children.Count && i < rects.Count; i++)
            if (def.Children[i].Name == childName) return rects[i];
        return Rectangle.Empty;
    }

    // ═══════════════════════════════════════
    //  Drawing
    // ═══════════════════════════════════════

    /// <summary>Draw a widget at screen position. instanceId is used for override lookups.
    /// AutoSizeHeight widgets draw at their measured (visible-content) height.</summary>
    public void DrawWidget(string widgetId, int x, int y, string? instanceId = null)
    {
        var def = _widgetDefs.FirstOrDefault(w => w.Id == widgetId);
        if (def == null) return;
        string inst = instanceId ?? widgetId;
        DrawWidgetDef(def, x, y, def.Width, MeasureHeight(def, inst), inst);
    }

    /// <summary>Measured height of a widget instance: AutoSizeHeight vertical-
    /// layout widgets report visible content height (recursively — nested
    /// auto-size sections propagate up); everything else reports def Height.</summary>
    public int MeasureWidgetHeight(string widgetId, string? instanceId = null)
    {
        var def = _widgetDefs.FirstOrDefault(w => w.Id == widgetId);
        return def == null ? 0 : MeasureHeight(def, instanceId ?? widgetId);
    }

    private int MeasureHeight(UIEditorWidgetDef def, string instanceId)
    {
        if (!def.AutoSizeHeight || def.Layout != "vertical") return def.Height;
        int padT = def.LayoutPadTop > 0 ? def.LayoutPadTop : def.LayoutPadding;
        int padB = def.LayoutPadBottom > 0 ? def.LayoutPadBottom : def.LayoutPadding;
        int spacY = def.LayoutSpacingY > 0 ? def.LayoutSpacingY : def.LayoutSpacing;
        _hiddenChildren.TryGetValue(instanceId, out var hidden);
        int total = 0, count = 0;
        for (int i = 0; i < def.Children.Count; i++)
        {
            var child = def.Children[i];
            if (child.IgnoreLayout) continue;
            if (hidden != null && hidden.Contains(child.Name)) continue;
            total += ChildHeightFor(child, instanceId, i);
            count++;
        }
        if (count > 1) total += (count - 1) * spacY;
        return padT + total + padB;
    }

    private int ChildLayoutHeight(UIEditorChildDef child, string subInstanceId)
    {
        if (!string.IsNullOrEmpty(child.Widget))
        {
            var sub = _widgetDefs.FirstOrDefault(w => w.Id == child.Widget);
            if (sub != null && sub.AutoSizeHeight) return MeasureHeight(sub, subInstanceId);
        }
        return child.Height > 0 ? child.Height : 40;
    }

    /// <summary>Instance-aware layout: like WidgetLayoutUtils.ComputeLayoutRects
    /// but skips hidden children (they get Rectangle.Empty, preserving index
    /// alignment, and don't advance the layout cursor) and sizes auto-height
    /// sub-widget children by their measured content. AutoSizeHeight widgets
    /// never column-wrap (their height IS the content).</summary>
    private List<Rectangle> ComputeInstanceRects(UIEditorWidgetDef def, int wdX, int wdY, string instanceId)
    {
        _hiddenChildren.TryGetValue(instanceId, out var hidden);
        bool isHoriz = def.Layout == "horizontal";
        bool isVert = def.Layout == "vertical";
        if (!isHoriz && !isVert && hidden == null)
            return WidgetLayoutUtils.ComputeLayoutRects(def, wdX, wdY);

        var rects = new List<Rectangle>();
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
            if (hidden != null && hidden.Contains(child.Name))
            {
                rects.Add(Rectangle.Empty);
                continue;
            }
            int cw = child.Width > 0 ? child.Width : 100;
            int ch = ChildHeightFor(child, instanceId, i);

            if (useLayout && !child.IgnoreLayout)
            {
                // Auto-size widgets honor the child's CROSS-AXIS offset (x for
                // vertical stacks, y for horizontal) — the analog of a per-child
                // margin. Legacy layout widgets keep ignoring offsets.
                int crossX = def.AutoSizeHeight ? child.X : 0;
                int crossY = def.AutoSizeHeight ? child.Y : 0;
                if (isHoriz)
                {
                    if (!def.AutoSizeHeight && curX > padL && curX + cw > def.Width - padR)
                    {
                        curY += rowMaxH + spacY;
                        curX = padL;
                        rowMaxH = 0;
                    }
                    rects.Add(new Rectangle(wdX + curX, wdY + curY + crossY, cw, ch));
                    curX += cw + spacX;
                    if (ch > rowMaxH) rowMaxH = ch;
                }
                else
                {
                    if (!def.AutoSizeHeight && curY > padT && curY + ch > def.Height - padB)
                    {
                        curX += colMaxW + spacX;
                        curY = padT;
                        colMaxW = 0;
                    }
                    rects.Add(new Rectangle(wdX + curX + crossX, wdY + curY, cw, ch));
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

    /// <summary>Draw a widget def at a specific rect.</summary>
    public void DrawWidgetDef(UIEditorWidgetDef def, int x, int y, int w, int h, string instanceId)
    {
        // Background + stencil layers
        DrawWidgetLayers(def, x, y, w, h, drawFrame: false);

        // Children (between stencil and frame)
        var rects = ComputeInstanceRects(def, x, y, instanceId);
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
        if (_hiddenChildren.TryGetValue(instanceId, out var hidden) && hidden.Contains(child.Name))
            return;

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
                var nestedRects = ComputeInstanceRects(widgetDef, rect.X, rect.Y, subId);
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
                // Per-instance tint override (selected/pressed state). Applies to
                // image + nine-slice draws below; text uses its own color path.
                if (_elementTintOverrides.TryGetValue(instanceId, out var etMap)
                    && etMap.TryGetValue(child.Name, out var etOv))
                    tint = etOv;

                if (elemDef.Type == "text")
                {
                    drawn = true;
                    // Check for text content override
                    string text = "";
                    if (_textOverrides.TryGetValue(instanceId, out var tMap) && tMap.TryGetValue(child.Name, out var ov))
                        text = ov;
                    else if (!string.IsNullOrEmpty(child.DefaultText))
                        text = child.DefaultText;
                    else if (!string.IsNullOrEmpty(elemDef.DefaultText))
                        text = elemDef.DefaultText;

                    if (!string.IsNullOrEmpty(text))
                    {
                        // Apply child text override if present
                        var txo = (child.HasTextOverride && child.TextOverride != null) ? child.TextOverride : null;
                        Color? colorOv = null;
                        if (_textColorOverrides.TryGetValue(instanceId, out var cMap) && cMap.TryGetValue(child.Name, out var c))
                            colorOv = c;
                        DrawTextElement(text, elemDef, rect, txo, colorOv);
                    }
                }
                else if (elemDef.Type == "image")
                {
                    // Check for image override
                    string imgPath = elemDef.ImagePath;
                    if (_imageOverrides.TryGetValue(instanceId, out var iMap) && iMap.TryGetValue(child.Name, out var ov))
                        imgPath = ov;

                    if (!string.IsNullOrEmpty(imgPath))
                    {
                        // Harmonized lookup falls back to the raw texture (overrides miss the cache)
                        var imgTex = GetTexture(imgPath, "el:" + elemDef.Id);
                        if (imgTex != null) { _batch.Draw(imgTex, rect, tint); drawn = true; }
                    }
                }
                else if (!string.IsNullOrEmpty(elemDef.NineSlice))
                {
                    var childNs = GetNineSlice(elemDef.NineSlice, "el:" + elemDef.Id);
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
                        new Vector2((int)(rect.X + 4), (int)(rect.Y + rect.Height / 2f - 7)), new Color(200, 200, 200, 180));
            }
        }
    }

    private void DrawTextElement(string text, UIEditorElementDef elemDef, Rectangle rect,
        UIEditorTextRegion? textOverride = null, Color? colorOverride = null)
    {
        // Apply child text override if present, falling back to element def
        var tr = elemDef.TextRegion;
        var txo = textOverride;

        var fontColor = colorOverride ?? ByteColor(txo?.FontColor ?? tr?.FontColor ?? new byte[] { 255, 255, 255, 255 });
        int fontSize = (txo != null && txo.FontSize > 0) ? txo.FontSize : tr?.FontSize ?? 14;
        string fontFamily = !string.IsNullOrEmpty(txo?.FontFamily) ? txo.FontFamily : tr?.FontFamily ?? "";
        string align = !string.IsNullOrEmpty(txo?.Align) ? txo.Align : tr?.Align ?? "left";
        string valign = !string.IsNullOrEmpty(txo?.VAlign) ? txo.VAlign : tr?.VAlign ?? "top";

        var dynFont = _fontMgr?.GetFont(fontSize, string.IsNullOrEmpty(fontFamily) ? null : fontFamily);
        if (dynFont == null) return;
        bool wordWrap = txo?.WordWrap ?? tr?.WordWrap ?? false;
        float charSpacing = txo?.CharSpacing ?? tr?.CharSpacing ?? 0f;
        bool bold = txo?.Bold ?? tr?.Bold ?? false;
        if (wordWrap)
            text = WidgetLayoutUtils.WrapText(dynFont, text, rect.Width - 4, charSpacing);
        int lineSpacing = txo?.LineSpacing ?? tr?.LineSpacing ?? 0;

        int outlineW = txo?.TextOutlineWidth ?? tr?.TextOutlineWidth ?? 0;
        byte[]? outlineCol = txo?.TextOutlineColor ?? tr?.TextOutlineColor;
        Color outlineColor = outlineCol != null ? ByteColor(outlineCol) : default;
        if (outlineCol == null) outlineW = 0;
        float boldStrength = txo?.BoldStrength ?? tr?.BoldStrength ?? 1f;
        int outlineOffX = txo?.OutlineOffsetX ?? tr?.OutlineOffsetX ?? 0;
        int outlineOffY = txo?.OutlineOffsetY ?? tr?.OutlineOffsetY ?? 0;
        WidgetLayoutUtils.DrawTextBlock(_batch, dynFont, text, rect, fontColor, align, valign,
            lineSpacing, charSpacing, bold, outlineW, outlineColor, boldStrength, outlineOffX, outlineOffY);
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
            var bgNs = GetNineSlice(def.Background, "bg:" + def.Id);
            if (bgNs != null)
            {
                int bi = def.BackgroundInset;
                var bgRect = new Rectangle(x + bi, y + bi, w - bi * 2, h - bi * 2);
                var bgColor = def.BackgroundTint != null ? ByteColor(def.BackgroundTint) : Color.White;
                bgNs.Draw(_batch, bgRect, bgColor, def.BackgroundScale);
            }
        }
        else if (!string.IsNullOrEmpty(def.BackgroundImagePath))
        {
            var bgTex = GetTexture(def.BackgroundImagePath, "bg:" + def.Id);
            if (bgTex != null)
            {
                int bi = def.BackgroundInset;
                var bgRect = new Rectangle(x + bi, y + bi, w - bi * 2, h - bi * 2);
                var bgColor = def.BackgroundTint != null ? ByteColor(def.BackgroundTint) : Color.White;
                DrawImageLayerCropTop(bgTex, bgRect, bgColor, def.Height - bi * 2);
            }
        }

        // Stencil (use harmonized if available)
        {
            int si = def.StencilInset;
            var stRect = new Rectangle(x + si, y + si, w - si * 2, h - si * 2);
            var stColor = def.StencilTint != null ? ByteColor(def.StencilTint) : Color.White;

            if (!string.IsNullOrEmpty(def.StencilImagePath))
            {
                var stTex = GetTexture(def.StencilImagePath, "st:" + def.Id);
                if (stTex != null) DrawImageLayerCropTop(stTex, stRect, stColor, def.Height - si * 2);
            }
            else if (!string.IsNullOrEmpty(def.Stencil))
            {
                var stNs = GetNineSlice(def.Stencil, "st:" + def.Id);
                if (stNs != null) stNs.Draw(_batch, stRect, stColor);
            }
        }

        // Frame (use harmonized if available)
        if (drawFrame && !string.IsNullOrEmpty(def.Frame))
        {
            var frNs = GetNineSlice(def.Frame, "fr:" + def.Id);
            if (frNs != null)
            {
                var frColor = def.FrameTint != null ? ByteColor(def.FrameTint) : Color.White;
                int fi = def.FrameInset;
                frNs.Draw(_batch, new Rectangle(x + fi, y + fi, w - fi * 2 - def.FrameInsetR, h - fi * 2),
                    frColor, def.FrameScale);
            }
        }
    }

    /// <summary>Image layers on auto-size widgets are baked at the widget's
    /// MAX height (def.Height). When drawn shorter, CROP the texture from the
    /// top (1:1 pixels, no resampling — the PointClamp rule) instead of
    /// squashing it. Drawn at full height this is an ordinary stretch-fit.</summary>
    private void DrawImageLayerCropTop(Texture2D tex, Rectangle rect, Color color, int defMaxH)
    {
        if (defMaxH > 0 && rect.Height < defMaxH)
        {
            int srcH = (int)System.Math.Round(tex.Height * (rect.Height / (float)defMaxH));
            _batch.Draw(tex, rect, new Rectangle(0, 0, tex.Width, System.Math.Max(1, srcH)), color);
        }
        else
        {
            _batch.Draw(tex, rect, color);
        }
    }

    private void DrawWidgetFrame(UIEditorWidgetDef def, int x, int y, int w, int h)
    {
        if (!string.IsNullOrEmpty(def.Frame))
        {
            var frNs = GetNineSlice(def.Frame, "fr:" + def.Id);
            if (frNs != null)
            {
                var frColor = def.FrameTint != null ? ByteColor(def.FrameTint) : Color.White;
                int fi = def.FrameInset;
                frNs.Draw(_batch, new Rectangle(x + fi, y + fi, w - fi * 2 - def.FrameInsetR, h - fi * 2),
                    frColor, def.FrameScale);
            }
        }
    }

    // ═══════════════════════════════════════
    //  Layout (reuses same algorithm as editor)
    // ═══════════════════════════════════════

    private static List<Rectangle> ComputeLayoutRects(UIEditorWidgetDef def, int wdX, int wdY)
        => WidgetLayoutUtils.ComputeLayoutRects(def, wdX, wdY);

    // ═══════════════════════════════════════
    //  Asset loading
    // ═══════════════════════════════════════

    /// <summary>
    /// Draw a cached icon texture into a screen-space rect. Used by code-driven
    /// menus that don't go through the full widget pipeline (e.g. TableCraftMenuUI)
    /// but still want to share the same texture cache as the widget renderer.
    /// Silently no-ops when the path is empty or the file can't be loaded.
    /// </summary>
    public void DrawIcon(string iconPath, int x, int y, int w, int h)
    {
        if (_batch == null) return;
        var tex = GetOrLoadTexture(iconPath);
        if (tex == null) return;
        _batch.Draw(tex, new Rectangle(x, y, w, h), Color.White);
    }

    /// <summary>Draw a named image element into a rect using the SAME pipeline
    /// as widget children — the harmonized (recolored) texture plus the element's
    /// tint. Lets code-driven layout (e.g. a wrapping abilities box) reuse a
    /// widget element's exact look at a dynamic size. No-op for non-image elements.</summary>
    public void DrawElementImage(string elementId, Rectangle rect)
    {
        if (_batch == null) return;
        var elemDef = _elementDefs.FirstOrDefault(e => e.Id == elementId);
        if (elemDef == null || elemDef.Type != "image" || string.IsNullOrEmpty(elemDef.ImagePath)) return;
        var tex = GetTexture(elemDef.ImagePath, "el:" + elemDef.Id);
        if (tex == null) return;
        _batch.Draw(tex, rect, ByteColor(elemDef.TintColor ?? new byte[] { 255, 255, 255, 255 }));
    }

    /// <summary>Draw a line of text directly (for code-driven content layered
    /// over a widget, e.g. the unit sheet's magic-path levels). Position is the
    /// top-left; caller rounds to integer pixels.</summary>
    public void DrawText(string text, int x, int y, int fontSize, Color color, string? fontFamily = null)
    {
        if (_batch == null || string.IsNullOrEmpty(text)) return;
        var font = _fontMgr?.GetFont(fontSize, string.IsNullOrEmpty(fontFamily) ? null : fontFamily);
        if (font == null) return;
        _batch.DrawString(font, text, new Vector2(x, y), color);
    }

    /// <summary>Measure a line of text in the given font (for layout).</summary>
    public Vector2 MeasureText(string text, int fontSize, string? fontFamily = null)
    {
        if (string.IsNullOrEmpty(text)) return Vector2.Zero;
        var font = _fontMgr?.GetFont(fontSize, string.IsNullOrEmpty(fontFamily) ? null : fontFamily);
        return font == null ? Vector2.Zero : font.MeasureString(text);
    }

    private Texture2D? GetOrLoadTexture(string texPath)
    {
        if (string.IsNullOrEmpty(texPath)) return null;
        if (_textures.TryGetValue(texPath, out var tex)) return tex;
        string resolved = Core.GamePaths.Resolve(texPath);
        if (!File.Exists(resolved)) return null;
        try
        {
            tex = TextureUtil.LoadPremultiplied(_device, texPath);
            _textures[texPath] = tex;
            return tex;
        }
        catch (Exception ex) { DebugLog.Log("error", $"Failed to load texture '{texPath}': {ex.Message}"); return null; }
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
        catch (Exception ex) { DebugLog.Log("error", $"Failed to load nine slices from {path}: {ex.Message}"); return false; }
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
                        WordWrap = trEl.TryGetProperty("wordWrap", out var ww) && ww.GetBoolean(),
                        LineSpacing = trEl.TryGetProperty("lineSpacing", out var lsp) ? lsp.GetInt32() : 0,
                        CharSpacing = trEl.TryGetProperty("charSpacing", out var csp) ? csp.GetSingle() : 0f,
                        Bold = trEl.TryGetProperty("bold", out var bld) && bld.GetBoolean(),
                        BoldStrength = trEl.TryGetProperty("boldStrength", out var bst) ? bst.GetSingle() : 1f,
                        TextOutlineWidth = trEl.TryGetProperty("outlineWidth", out var tow) ? tow.GetInt32() : 0,
                        OutlineOffsetX = trEl.TryGetProperty("outlineOffsetX", out var oox) ? oox.GetInt32() : 0,
                        OutlineOffsetY = trEl.TryGetProperty("outlineOffsetY", out var ooy) ? ooy.GetInt32() : 0,
                    };
                    if (trEl.TryGetProperty("outlineColor", out var tocArr) && tocArr.ValueKind == JsonValueKind.Array)
                    {
                        var toc = tocArr.EnumerateArray().ToArray();
                        if (toc.Length >= 4)
                            elem.TextRegion.TextOutlineColor = new byte[] { (byte)toc[0].GetInt32(),
                                (byte)toc[1].GetInt32(), (byte)toc[2].GetInt32(), (byte)toc[3].GetInt32() };
                    }
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
                elem.Harmonize = ReadHarmonizeSettings(el, "harmonize");

                _elementDefs.Add(elem);
            }
            return true;
        }
        catch (Exception ex) { DebugLog.Log("error", $"Failed to load UI elements from {path}: {ex.Message}"); return false; }
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
                    BackgroundImagePath = el.TryGetProperty("backgroundImagePath", out var bip) ? bip.GetString() ?? "" : "",
                    AutoSizeHeight = el.TryGetProperty("autoSizeHeight", out var ash) && ash.GetBoolean(),
                    Frame = el.TryGetProperty("frame", out var fr) ? fr.GetString() ?? "" : "",
                    Width = el.TryGetProperty("width", out var w) ? w.GetInt32() : 200,
                    Height = el.TryGetProperty("height", out var h) ? h.GetInt32() : 100,
                    BackgroundScale = el.TryGetProperty("backgroundScale", out var bs) ? bs.GetSingle() : 1f,
                    FrameScale = el.TryGetProperty("frameScale", out var fs) ? fs.GetSingle() : 1f,
                    BackgroundInset = el.TryGetProperty("backgroundInset", out var bii) ? bii.GetInt32() : 0,
                    StencilInset = el.TryGetProperty("stencilInset", out var sii) ? sii.GetInt32() : 0,
                    FrameInset = el.TryGetProperty("frameInset", out var fii) ? fii.GetInt32() : 0,
                    FrameInsetR = el.TryGetProperty("frameInsetR", out var fir) ? fir.GetInt32() : 0,
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
                        // Text override
                        if (ch.TryGetProperty("textOverride", out var txo))
                        {
                            child.HasTextOverride = true;
                            child.TextOverride = new UIEditorTextRegion
                            {
                                X = txo.TryGetProperty("x", out var txx) ? txx.GetInt32() : 0,
                                Y = txo.TryGetProperty("y", out var txy) ? txy.GetInt32() : 0,
                                W = txo.TryGetProperty("w", out var txw) ? txw.GetInt32() : 0,
                                H = txo.TryGetProperty("h", out var txh) ? txh.GetInt32() : 0,
                                Align = txo.TryGetProperty("align", out var txa) ? txa.GetString() ?? "left" : "left",
                                VAlign = txo.TryGetProperty("valign", out var txva) ? txva.GetString() ?? "top" : "top",
                                FontFamily = txo.TryGetProperty("fontFamily", out var txff) ? txff.GetString() ?? "" : "",
                                FontSize = txo.TryGetProperty("fontSize", out var txfs) ? txfs.GetInt32() : 14,
                            };
                            if (txo.TryGetProperty("fontColor", out var txfc) && txfc.ValueKind == JsonValueKind.Array)
                            {
                                var fca = txfc.EnumerateArray().ToArray();
                                if (fca.Length >= 4)
                                    child.TextOverride.FontColor = new byte[] { (byte)fca[0].GetInt32(), (byte)fca[1].GetInt32(),
                                        (byte)fca[2].GetInt32(), (byte)fca[3].GetInt32() };
                            }
                            if (txo.TryGetProperty("wordWrap", out var txww))
                                child.TextOverride.WordWrap = txww.GetBoolean();
                            if (txo.TryGetProperty("lineSpacing", out var txls))
                                child.TextOverride.LineSpacing = txls.GetInt32();
                            if (txo.TryGetProperty("charSpacing", out var txcs))
                                child.TextOverride.CharSpacing = txcs.GetSingle();
                            if (txo.TryGetProperty("bold", out var txbd))
                                child.TextOverride.Bold = txbd.GetBoolean();
                            if (txo.TryGetProperty("boldStrength", out var txbs))
                                child.TextOverride.BoldStrength = txbs.GetSingle();
                            if (txo.TryGetProperty("outlineWidth", out var txow))
                                child.TextOverride.TextOutlineWidth = txow.GetInt32();
                            if (txo.TryGetProperty("outlineOffsetX", out var txoox))
                                child.TextOverride.OutlineOffsetX = txoox.GetInt32();
                            if (txo.TryGetProperty("outlineOffsetY", out var txooy))
                                child.TextOverride.OutlineOffsetY = txooy.GetInt32();
                            if (txo.TryGetProperty("outlineColor", out var txoc2) && txoc2.ValueKind == JsonValueKind.Array)
                            {
                                var oca = txoc2.EnumerateArray().ToArray();
                                if (oca.Length >= 4)
                                    child.TextOverride.TextOutlineColor = new byte[] { (byte)oca[0].GetInt32(),
                                        (byte)oca[1].GetInt32(), (byte)oca[2].GetInt32(), (byte)oca[3].GetInt32() };
                            }
                        }
                        wd.Children.Add(child);
                    }
                }
                _widgetDefs.Add(wd);
            }
            return true;
        }
        catch (Exception ex) { DebugLog.Log("error", $"Failed to load UI widgets from {path}: {ex.Message}"); return false; }
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
        if (h.TryGetProperty("gradColor", out var gc) && gc.ValueKind == JsonValueKind.Array && gc.GetArrayLength() >= 4)
        {
            var a = gc.EnumerateArray().ToArray();
            s.GradColor = new byte[] { (byte)a[0].GetInt32(), (byte)a[1].GetInt32(),
                (byte)a[2].GetInt32(), (byte)a[3].GetInt32() };
        }
        if (h.TryGetProperty("gradStrength", out var gst)) s.GradStrength = gst.GetSingle();
        if (h.TryGetProperty("outlineColor", out var oc) && oc.ValueKind == JsonValueKind.Array && oc.GetArrayLength() >= 4)
        {
            var a = oc.EnumerateArray().ToArray();
            s.OutlineColor = new byte[] { (byte)a[0].GetInt32(), (byte)a[1].GetInt32(),
                (byte)a[2].GetInt32(), (byte)a[3].GetInt32() };
        }
        if (h.TryGetProperty("outlineThickness", out var oth)) s.OutlineThickness = oth.GetSingle();
        if (h.TryGetProperty("outlineOpacity", out var oop)) s.OutlineOpacity = oop.GetSingle();
        return s.HasEffect ? s : null;
    }

    private static Color ByteColor(byte[] c, byte alphaOverride = 0)
        => Core.ColorUtils.ByteColor(c, alphaOverride);

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

    /// <summary>Generate harmonized textures for all widgets/elements that have harmonize settings.</summary>
    private void GenerateHarmonizedTextures()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // Per-widget prefixes: different widgets may harmonize the SAME texture
        // differently (e.g. the leather background dark vs light).
        foreach (var wd in _widgetDefs)
        {
            if (wd.BgHarmonize != null && wd.BgHarmonize.HasEffect)
                ApplyHarmonize(wd.Background, wd.BackgroundImagePath, "bg:" + wd.Id, wd.BgHarmonize);
            if (wd.StencilHarmonize != null && wd.StencilHarmonize.HasEffect)
                ApplyHarmonize(wd.Stencil, wd.StencilImagePath, "st:" + wd.Id, wd.StencilHarmonize);
            if (wd.FrameHarmonize != null && wd.FrameHarmonize.HasEffect)
                ApplyHarmonize(wd.Frame, "", "fr:" + wd.Id, wd.FrameHarmonize);
        }
        // Elements use a per-id prefix: the same texture can be harmonized
        // differently by different elements (e.g. a frame as shadow vs as gold).
        foreach (var elem in _elementDefs)
        {
            if (elem.Harmonize == null || !elem.Harmonize.HasEffect) continue;
            string imgPath = elem.Type == "image" ? elem.ImagePath : "";
            string nsRef = elem.Type == "nineSlice" ? elem.NineSlice : "";
            ApplyHarmonize(nsRef, imgPath, "el:" + elem.Id, elem.Harmonize);
        }
        Core.DebugLog.Log("startup", $"  [WidgetRenderer] harmonize bake: {sw.ElapsedMilliseconds}ms");
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
