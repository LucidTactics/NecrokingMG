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
    private Render.SpriteScope Scope => Game1.Instance.Scope;  // straight-alpha draw surface
    private FontManager? _fontMgr;
    private Texture2D? _pixel;

    // Loaded definitions (shared with editor format)
    private readonly List<UIEditorNineSliceDef> _nineSliceDefs = new();
    private readonly List<UIEditorElementDef> _elementDefs = new();
    private readonly List<UIEditorWidgetDef> _widgetDefs = new();

    // Texture / nine-slice / harmonized cache mechanics (shared with the UI editor).
    // Built in the constructor so it is always non-null (Shutdown may run without Init);
    // the device provider reads _device lazily (assigned in Init).
    private readonly WidgetResourceCache _resources;

    public RuntimeWidgetRenderer()
    {
        _resources = new WidgetResourceCache(() => _device,
            nsId => _nineSliceDefs.FirstOrDefault(d => d.Id == nsId), "error");
    }

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
    private readonly Dictionary<string, Dictionary<string, int>> _childWidthOverrides = new();

    // Per-widget image overrides: widgetInstanceId -> (childName -> texturePath)
    private readonly Dictionary<string, Dictionary<string, string>> _imageOverrides = new();

    // Per-widget child visibility: widgetInstanceId -> (childIndex -> widgetId to use)
    // This allows swapping "Item Slot" for "Item Slot_Empty" at runtime
    private readonly Dictionary<string, Dictionary<int, string>> _childWidgetOverrides = new();

    public bool IsLoaded => _widgetDefs.Count > 0;

    public void Init(GraphicsDevice device, FontManager? fontMgr)
    {
        _device = device;
        _fontMgr = fontMgr;

        _pixel = TextureUtil.GetWhitePixel(device);
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

    /// <summary>Shared plumbing for the per-instance override dicts: get-or-create the
    /// inner map for <paramref name="instanceId"/>, then assign <paramref name="value"/>.
    /// The public Set* methods keep their own typed signatures and clear semantics.</summary>
    private static void SetOverride<TK, TV>(Dictionary<string, Dictionary<TK, TV>> store,
        string instanceId, TK key, TV value) where TK : notnull
    {
        if (!store.TryGetValue(instanceId, out var map))
        {
            map = new Dictionary<TK, TV>();
            store[instanceId] = map;
        }
        map[key] = value;
    }

    /// <summary>Set text for a named child within a widget instance.</summary>
    public void SetText(string instanceId, string childName, string text)
        => SetOverride(_textOverrides, instanceId, childName, text);

    /// <summary>Set image path for a named child within a widget instance.</summary>
    public void SetImage(string instanceId, string childName, string imagePath)
        => SetOverride(_imageOverrides, instanceId, childName, imagePath);

    /// <summary>Override which widget a child slot uses (e.g., swap "Item Slot" for "Item Slot_Empty").</summary>
    public void SetChildWidget(string instanceId, int childIndex, string widgetId)
        => SetOverride(_childWidgetOverrides, instanceId, childIndex, widgetId);

    /// <summary>Override the font color of a named text child within a widget
    /// instance (e.g. green/red tabulation values).</summary>
    public void SetTextColor(string instanceId, string childName, byte rr, byte g, byte b, byte a = 255)
        => SetOverride(_textColorOverrides, instanceId, childName, new Color(rr, g, b, a));

    /// <summary>Override the tint of a named image / nine-slice child within a
    /// widget instance. Multiplies the (possibly harmonized) source texture, so
    /// white = unchanged, grey = dimmed. Used to show selected/pressed button
    /// state (bright = active, dim = inactive).</summary>
    public void SetElementTint(string instanceId, string childName, Color tint)
        => SetOverride(_elementTintOverrides, instanceId, childName, tint);

    /// <summary>Override a child's layout height within a widget instance, used
    /// to grow an auto-height section (e.g. a wrapping icon row). Pass a value
    /// &lt;= 0 to clear the override for that child.</summary>
    public void SetChildHeight(string instanceId, string childName, int height)
    {
        if (height > 0) { SetOverride(_childHeightOverrides, instanceId, childName, height); return; }
        // Clear semantics preserved: the instance map is materialized even when clearing.
        if (!_childHeightOverrides.TryGetValue(instanceId, out var map))
        {
            map = new Dictionary<string, int>();
            _childHeightOverrides[instanceId] = map;
        }
        map.Remove(childName);
    }

    /// <summary>A child's effective layout height: instance override if set,
    /// else the auto-measured / def height.</summary>
    private int ChildHeightFor(UIEditorChildDef child, string instanceId, int childIndex)
    {
        if (_childHeightOverrides.TryGetValue(instanceId, out var m) && m.TryGetValue(child.Name, out var h))
            return h;
        return ChildLayoutHeight(child, $"{instanceId}.{childIndex}");
    }

    /// <summary>Override a child's layout width for one instance (e.g. sizing skill-book
    /// tabs to evenly fill the bar for the current tab count). Pass &lt;=0 to clear.</summary>
    public void SetChildWidth(string instanceId, string childName, int width)
    {
        if (width > 0) { SetOverride(_childWidthOverrides, instanceId, childName, width); return; }
        // Clear semantics preserved: an absent instance map is left absent (no allocation).
        if (_childWidthOverrides.TryGetValue(instanceId, out var map)) map.Remove(childName);
    }

    /// <summary>A child's effective layout width: instance override if set, else def width.</summary>
    private int ChildWidthFor(UIEditorChildDef child, string instanceId)
    {
        if (_childWidthOverrides.TryGetValue(instanceId, out var m) && m.TryGetValue(child.Name, out var w))
            return w;
        return child.Width > 0 ? child.Width : 100;
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
        _childWidthOverrides.Remove(instanceId);
    }

    /// <summary>Clear overrides for an instance AND all its nested
    /// sub-instances ("{id}", "{id}.3", "{id}.3.0", ...).</summary>
    public void ClearOverridesRecursive(string instanceId)
    {
        string prefix = instanceId + ".";
        foreach (var dict in new System.Collections.IDictionary[]
                 { _textOverrides, _imageOverrides, _childWidgetOverrides, _hiddenChildren,
                   _textColorOverrides, _elementTintOverrides, _childHeightOverrides, _childWidthOverrides })
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

    /// <summary>Draw only a widget's background (+ stencil) layer into a rect, scaled
    /// to that rect. For code-driven panels (e.g. the HUD spell bar) that need to
    /// interleave their own content (icons, overlays) between the background and the
    /// frame — pair with <see cref="DrawWidgetFrameLayer"/>.</summary>
    public void DrawWidgetBackground(string widgetId, Rectangle rect)
    {
        var def = _widgetDefs.FirstOrDefault(w => w.Id == widgetId);
        if (def != null) DrawWidgetLayers(def, rect.X, rect.Y, rect.Width, rect.Height, drawFrame: false);
    }

    /// <summary>Draw only a widget's frame layer into a rect, scaled to it.</summary>
    public void DrawWidgetFrameLayer(string widgetId, Rectangle rect)
    {
        var def = _widgetDefs.FirstOrDefault(w => w.Id == widgetId);
        if (def != null) DrawWidgetFrame(def, rect.X, rect.Y, rect.Width, rect.Height);
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
    private List<Rectangle> ComputeInstanceRects(UIEditorWidgetDef def, int wdX, int wdY, string instanceId,
        int instW = -1, int instH = -1)
    {
        _hiddenChildren.TryGetValue(instanceId, out var hidden);
        _childWidthOverrides.TryGetValue(instanceId, out var widths);
        bool isHoriz = def.Layout == "horizontal", isVert = def.Layout == "vertical";
        // Fast path with no per-instance state — straight through the shared pass.
        if (!isHoriz && !isVert && hidden == null && widths == null)
            return WidgetLayoutUtils.ComputeLayoutRects(def, wdX, wdY, null, null, instW, instH);
        // Otherwise drive the same shared pass, supplying this instance's hidden set
        // (by child name) and override-aware heights/widths.
        return WidgetLayoutUtils.ComputeLayoutRects(def, wdX, wdY,
            hidden == null ? null : i => hidden.Contains(def.Children[i].Name),
            (child, i) => ChildHeightFor(child, instanceId, i), instW, instH,
            widths == null ? null : (child, i) => ChildWidthFor(child, instanceId));
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

    private void DrawChild(UIEditorChildDef child, Rectangle rect, string instanceId, int childIndex, string? overrideWidget, string? overrideText = null, string? overrideElement = null)
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

                // Nested children — use a sub-instance ID for override scoping.
                // Pass the instance rect size so the nested widget's children fill /
                // anchor to the size this instance is actually drawn at, not the def.
                string subId = $"{instanceId}.{childIndex}";
                var nestedRects = ComputeInstanceRects(widgetDef, rect.X, rect.Y, subId, rect.Width, rect.Height);
                for (int ci = 0; ci < widgetDef.Children.Count && ci < nestedRects.Count; ci++)
                {
                    string? nestedOverride = null;
                    if (_childWidgetOverrides.TryGetValue(subId, out var cwMap))
                        cwMap.TryGetValue(ci, out nestedOverride);
                    DrawChild(widgetDef.Children[ci], nestedRects[ci], subId, ci, nestedOverride, child.OverrideTextFor(ci), child.OverrideElementFor(ci));
                }

                DrawWidgetFrame(widgetDef, rect.X, rect.Y, rect.Width, rect.Height);
            }
        }

        // Element child (overrideElement lets a nested instance swap which element
        // it shows — e.g. each grimoire tab points its Icon/Text child at its own).
        string elemId = !string.IsNullOrEmpty(overrideElement) ? overrideElement : child.Element;
        if (!drawn && !string.IsNullOrEmpty(elemId))
        {
            var elemDef = _elementDefs.FirstOrDefault(e => e.Id == elemId);
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
                    else if (!string.IsNullOrEmpty(overrideText))
                        text = overrideText;
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
                        if (imgTex != null) { Scope.Draw(imgTex, rect, tint); drawn = true; }
                    }
                }
                else if (!string.IsNullOrEmpty(elemDef.NineSlice))
                {
                    var childNs = GetNineSlice(elemDef.NineSlice, "el:" + elemDef.Id);
                    if (childNs != null) { childNs.Draw(Scope, rect, tint, elemDef.NineSliceScale * child.NineSliceScale); drawn = true; }
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
                    // FontStashSharp extension on the raw batch - encode the tint explicitly.
                    Scope.Batch.DrawString(font, child.DefaultText,
                        new Vector2((int)(rect.X + 4), (int)(rect.Y + rect.Height / 2f - 7)), Scope.EncodeTint(new Color(200, 200, 200, 180)));
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
        WidgetLayoutUtils.DrawTextBlock(Scope, dynFont, text, rect, fontColor, align, valign,
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
        Scope.Draw(_pixel, new Rectangle(sr.X, sr.Y, sr.Width, t), strokeCol);
        Scope.Draw(_pixel, new Rectangle(sr.X, sr.Y + sr.Height - t, sr.Width, t), strokeCol);
        Scope.Draw(_pixel, new Rectangle(sr.X, sr.Y + t, t, sr.Height - t * 2), strokeCol);
        Scope.Draw(_pixel, new Rectangle(sr.X + sr.Width - t, sr.Y + t, t, sr.Height - t * 2), strokeCol);
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
                bgNs.Draw(Scope, bgRect, bgColor, def.BackgroundScale);
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
                if (stNs != null) stNs.Draw(Scope, stRect, stColor);
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
                frNs.Draw(Scope, new Rectangle(x + fi, y + fi, w - fi * 2 - def.FrameInsetR, h - fi * 2),
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
            Scope.Draw(tex, rect, new Rectangle(0, 0, tex.Width, System.Math.Max(1, srcH)), color);
        }
        else
        {
            Scope.Draw(tex, rect, color);
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
                frNs.Draw(Scope, new Rectangle(x + fi, y + fi, w - fi * 2 - def.FrameInsetR, h - fi * 2),
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
        if (_device == null) return;
        var tex = GetOrLoadTexture(iconPath);
        if (tex == null) return;
        Scope.Draw(tex, new Rectangle(x, y, w, h), Color.White);
    }

    /// <summary>Draw a named image element into a rect using the SAME pipeline
    /// as widget children — the harmonized (recolored) texture plus the element's
    /// tint. Lets code-driven layout (e.g. a wrapping abilities box) reuse a
    /// widget element's exact look at a dynamic size. No-op for non-image elements.</summary>
    public void DrawElementImage(string elementId, Rectangle rect, float srcInset = 0f)
    {
        if (_device == null) return;
        var elemDef = _elementDefs.FirstOrDefault(e => e.Id == elementId);
        if (elemDef == null) return;
        var elemTint = ByteColor(elemDef.TintColor ?? new byte[] { 255, 255, 255, 255 });
        // Nine-slice element (e.g. a frame/backing converted from a baked image):
        // draw the (possibly harmonized) nine-slice into the rect. srcInset doesn't
        // apply — the nine-slice's own borders handle the corners.
        if (elemDef.Type == "nineSlice" && !string.IsNullOrEmpty(elemDef.NineSlice))
        {
            GetNineSlice(elemDef.NineSlice, "el:" + elemDef.Id)?.Draw(Scope, rect, elemTint, elemDef.NineSliceScale);
            return;
        }
        if (elemDef.Type != "image" || string.IsNullOrEmpty(elemDef.ImagePath)) return;
        var tex = GetTexture(elemDef.ImagePath, "el:" + elemDef.Id);
        if (tex == null) return;
        var tint = ByteColor(elemDef.TintColor ?? new byte[] { 255, 255, 255, 255 });
        // srcInset crops a fraction off each edge of the source texture — used to
        // discard a texture's transparent margin / corner notches so its solid
        // body fills the destination at any size (a stretch-fit can't do that).
        if (srcInset > 0f)
        {
            int ix = (int)(tex.Width * srcInset), iy = (int)(tex.Height * srcInset);
            Scope.Draw(tex, rect, new Rectangle(ix, iy, tex.Width - 2 * ix, tex.Height - 2 * iy), tint);
        }
        else Scope.Draw(tex, rect, tint);
    }

    /// <summary>Draw a named nine-slice (from nine_slices.json — e.g. frame_fancy,
    /// LeatherBackground, RenaiThinBorder) into a rect with fixed corners and
    /// stretched edges, so a frame doesn't distort the way a stretched image does.
    /// borderScale shrinks the corner/edge size (use &lt;1 for a thinner frame).</summary>
    public void DrawNineSlice(string nsId, Rectangle rect, Color? tint = null, float borderScale = 1f)
    {
        if (_device == null) return;
        // Prefer a harmonized copy (if the def carries a harmonize block), else raw.
        GetNineSlice(nsId, "ns")?.Draw(Scope, rect, tint ?? Color.White, borderScale);
    }

    /// <summary>Draw a line of text directly (for code-driven content layered
    /// over a widget, e.g. the unit sheet's magic-path levels). Position is the
    /// top-left; caller rounds to integer pixels.</summary>
    public void DrawText(string text, int x, int y, int fontSize, Color color, string? fontFamily = null)
    {
        if (_device == null || string.IsNullOrEmpty(text)) return;
        var font = _fontMgr?.GetFont(fontSize, string.IsNullOrEmpty(fontFamily) ? null : fontFamily);
        if (font == null) return;
        // FontStashSharp extension on the raw batch - encode the tint explicitly.
        Scope.Batch.DrawString(font, text, new Vector2(x, y), Scope.EncodeTint(color));
    }

    /// <summary>Measure a line of text in the given font (for layout).</summary>
    public Vector2 MeasureText(string text, int fontSize, string? fontFamily = null)
    {
        if (string.IsNullOrEmpty(text)) return Vector2.Zero;
        var font = _fontMgr?.GetFont(fontSize, string.IsNullOrEmpty(fontFamily) ? null : fontFamily);
        return font == null ? Vector2.Zero : font.MeasureString(text);
    }

    private Texture2D? GetOrLoadTexture(string texPath) => _resources.GetOrLoadTexture(texPath);

    private NineSlice? GetOrLoadNineSlice(string nsId) => _resources.GetOrLoadNineSlice(nsId);

    // ═══════════════════════════════════════
    //  JSON Loading — via UIDefsIO, the single parser+writer shared with the
    //  UI editor, so runtime and editor field coverage can never drift again.
    // ═══════════════════════════════════════

    private bool LoadNineSlices(string path)
        => UIDefsIO.LoadNineSlices(path, _nineSliceDefs) == UIDefsLoadResult.Ok;

    private bool LoadElements(string path)
        => UIDefsIO.LoadElements(path, _elementDefs) == UIDefsLoadResult.Ok;

    private bool LoadWidgets(string path)
        => UIDefsIO.LoadWidgets(path, _widgetDefs) == UIDefsLoadResult.Ok;

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

    /// <summary>One harmonize job: a source pixel buffer (read on the main thread)
    /// transformed in parallel, then uploaded to a Texture2D on the main thread.</summary>
    private sealed class HarmonizeJob
    {
        public string CacheKey = "", Prefix = "";
        public string? NsLookupId;
        public Color[] Pixels = System.Array.Empty<Color>();
        public int W, H;
        public HarmonizeSettings Settings = null!;
        public bool Changed;
    }

    /// <summary>Generate harmonized textures for all widgets/elements that have
    /// harmonize settings. The per-pixel colour transform dominates the cost, so
    /// it runs in three phases: GetData (main thread) → parallel CPU transform →
    /// SetData + nine-slice build (main thread). GetData/SetData must stay on the
    /// GraphicsDevice thread; only the pure pixel math is parallelized.</summary>
    private void GenerateHarmonizedTextures()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var jobs = new List<HarmonizeJob>();

        // Phase A (main thread): resolve each source texture + read its pixels.
        // Per-widget/-element prefixes: the SAME texture can be harmonized
        // differently by different owners (leather bg dark vs light; frame as
        // shadow vs gold), so each (prefix|texture) is a distinct cache entry.
        void Collect(string nsId, string imagePath, string cachePrefix, HarmonizeSettings settings)
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
            var pixels = new Color[sourceTex.Width * sourceTex.Height];
            sourceTex.GetData(pixels);
            jobs.Add(new HarmonizeJob
            {
                CacheKey = cachePrefix + "|" + texKey, Prefix = cachePrefix, NsLookupId = nsLookupId,
                Pixels = pixels, W = sourceTex.Width, H = sourceTex.Height, Settings = settings,
            });
        }
        foreach (var wd in _widgetDefs)
        {
            if (wd.BgHarmonize != null && wd.BgHarmonize.HasEffect)
                Collect(wd.Background, wd.BackgroundImagePath, "bg:" + wd.Id, wd.BgHarmonize);
            if (wd.StencilHarmonize != null && wd.StencilHarmonize.HasEffect)
                Collect(wd.Stencil, wd.StencilImagePath, "st:" + wd.Id, wd.StencilHarmonize);
            if (wd.FrameHarmonize != null && wd.FrameHarmonize.HasEffect)
                Collect(wd.Frame, "", "fr:" + wd.Id, wd.FrameHarmonize);
        }
        foreach (var elem in _elementDefs)
        {
            if (elem.Harmonize == null || !elem.Harmonize.HasEffect) continue;
            string imgPath = elem.Type == "image" ? elem.ImagePath : "";
            string nsRef = elem.Type == "nineSlice" ? elem.NineSlice : "";
            Collect(nsRef, imgPath, "el:" + elem.Id, elem.Harmonize);
        }
        // Standalone nine-slices with their own harmonize block (looked up via the
        // "ns" prefix by the public DrawNineSlice).
        foreach (var ns in _nineSliceDefs)
            if (ns.Harmonize != null && ns.Harmonize.HasEffect)
                Collect(ns.Id, "", "ns", ns.Harmonize);
        long getDataMs = sw.ElapsedMilliseconds; sw.Restart();

        // Phase B (parallel, CPU only): the per-pixel transform — the bottleneck.
        System.Threading.Tasks.Parallel.For(0, jobs.Count, i =>
            jobs[i].Changed = ColorHarmonizer.TransformPixels(jobs[i].Pixels, jobs[i].W, jobs[i].H, jobs[i].Settings));
        long pixelMs = sw.ElapsedMilliseconds; sw.Restart();

        // Phase C (main thread): upload to GPU + build harmonized nine-slices.
        foreach (var job in jobs)
        {
            if (!job.Changed) continue; // settings produced no change (matches old null skip)
            var tex = new Texture2D(_device, job.W, job.H);
            tex.SetData(job.Pixels);
            _resources.StoreHarmonizedTexture(job.CacheKey, tex);
            if (!string.IsNullOrEmpty(job.NsLookupId))
            {
                var nsDef = _nineSliceDefs.FirstOrDefault(n => n.Id == job.NsLookupId);
                if (nsDef != null)
                {
                    var nsInst = new NineSlice();
                    nsInst.LoadFromTexture(tex, nsDef.BorderLeft, nsDef.BorderRight,
                        nsDef.BorderTop, nsDef.BorderBottom, nsDef.TileEdges);
                    _resources.StoreHarmonizedNineSlice(job.Prefix + "|" + job.NsLookupId, nsInst);
                }
            }
        }
        long uploadMs = sw.ElapsedMilliseconds;
        Core.DebugLog.Log("startup", $"  [WidgetRenderer] harmonize bake: {getDataMs + pixelMs + uploadMs}ms " +
            $"(n={jobs.Count} getData={getDataMs}ms pixel(parallel)={pixelMs}ms upload={uploadMs}ms)");
    }

    /// <summary>Get harmonized nine-slice if available, otherwise original.</summary>
    private NineSlice? GetNineSlice(string nsId, string cachePrefix) => _resources.GetNineSlice(nsId, cachePrefix);

    /// <summary>Get harmonized texture if available, otherwise original.</summary>
    private Texture2D? GetTexture(string path, string cachePrefix) => _resources.GetTexture(path, cachePrefix);

    public void Shutdown()
    {
        _resources.ClearNineSliceInstances();
        _resources.ClearHarmonizedNineSlices(unload: true);
        _resources.DisposeRawTextures();
        _resources.ClearHarmonizedTextures();
        // _pixel is the shared TextureUtil.GetWhitePixel cache — do NOT dispose it here.
    }
}
