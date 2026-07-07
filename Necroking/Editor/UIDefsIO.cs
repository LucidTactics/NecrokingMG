using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Necroking.UI;

namespace Necroking.Editor;

public enum UIDefsLoadResult
{
    /// <summary>File doesn't exist — silently skipped (defs are optional).</summary>
    Missing,
    Ok,
    /// <summary>File exists but didn't parse (or has the wrong root key). The
    /// editor disables saving that file so a corrupt-but-recoverable file isn't
    /// replaced with an empty one.</summary>
    Failed,
}

/// <summary>
/// THE reader+writer for the three UI definition files (data/ui/nine_slices.json,
/// elements.json, widgets.json) — shared by the UI editor (load/save) and
/// <see cref="UI.RuntimeWidgetRenderer"/> (load). Historically each side kept its
/// own ~300-line JsonDocument walker over the same files and they drifted (the
/// runtime read a nine-slice "harmonize" recipe the editor neither read nor wrote,
/// so an authored one would be silently stripped by the next editor save). Field
/// coverage here is the union of both old parsers; every field the writer emits
/// is read back by the loader in this same file.
///
/// Saves are atomic + if-changed via <see cref="Core.JsonFile.WriteStringIfChanged"/>.
/// </summary>
public static class UIDefsIO
{
    // ═══════════════════════════════════════
    //  Nine-slices
    // ═══════════════════════════════════════

    public static UIDefsLoadResult LoadNineSlices(string path, List<UIEditorNineSliceDef> into)
    {
        if (!File.Exists(path)) return UIDefsLoadResult.Missing;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("nineSlices", out var arr))
            {
                Core.DebugLog.Log("error", $"UIDefsIO: {path} has no \"nineSlices\" root");
                return UIDefsLoadResult.Failed;
            }
            foreach (var item in arr.EnumerateArray())
            {
                var def = new UIEditorNineSliceDef
                {
                    Id = item.GetStringProp("id"),
                    Texture = item.GetStringProp("texture"),
                    BorderLeft = item.GetIntProp("borderLeft"),
                    BorderRight = item.GetIntProp("borderRight"),
                    BorderTop = item.GetIntProp("borderTop"),
                    BorderBottom = item.GetIntProp("borderBottom"),
                    TileEdges = item.GetBoolProp("tileEdges"),
                };
                if (item.TryGetProperty("harmonize", out var nsHarm))
                    def.Harmonize = ReadHarmonize(nsHarm);
                into.Add(def);
            }
            return UIDefsLoadResult.Ok;
        }
        catch (Exception ex)
        {
            Core.DebugLog.Log("error", $"UIDefsIO: failed to parse {path}: {ex.Message}");
            return UIDefsLoadResult.Failed;
        }
    }

    public static bool SaveNineSlices(string path, IReadOnlyList<UIEditorNineSliceDef> defs)
        => WriteFile(path, writer =>
        {
            writer.WriteStartObject();
            writer.WritePropertyName("nineSlices");
            writer.WriteStartArray();
            foreach (var ns in defs)
            {
                writer.WriteStartObject();
                writer.WriteString("id", ns.Id);
                writer.WriteString("texture", ns.Texture);
                writer.WriteNumber("borderLeft", ns.BorderLeft);
                writer.WriteNumber("borderRight", ns.BorderRight);
                writer.WriteNumber("borderTop", ns.BorderTop);
                writer.WriteNumber("borderBottom", ns.BorderBottom);
                writer.WriteBoolean("tileEdges", ns.TileEdges);
                // The runtime renders this; the old editor saver dropped it.
                if (ns.Harmonize != null && ns.Harmonize.HasEffect)
                    WriteHarmonize(writer, "harmonize", ns.Harmonize);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        });

    // ═══════════════════════════════════════
    //  Elements
    // ═══════════════════════════════════════

    public static UIDefsLoadResult LoadElements(string path, List<UIEditorElementDef> into)
    {
        if (!File.Exists(path)) return UIDefsLoadResult.Missing;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("elements", out var arr))
            {
                Core.DebugLog.Log("error", $"UIDefsIO: {path} has no \"elements\" root");
                return UIDefsLoadResult.Failed;
            }
            foreach (var item in arr.EnumerateArray())
            {
                var def = new UIEditorElementDef
                {
                    Id = item.GetStringProp("id"),
                    Type = item.GetStringProp("type", "nineSlice"),
                    NineSlice = item.GetStringProp("nineSlice"),
                    NineSliceScale = item.GetFloatProp("nineSliceScale", 1f),
                    ImagePath = item.GetStringProp("imagePath"),
                    Width = item.GetIntProp("width", 100),
                    Height = item.GetIntProp("height", 40),
                };
                if (item.TryGetProperty("tintColor", out var tc) && tc.ValueKind == JsonValueKind.Array)
                    def.TintColor = ReadColorArray(tc);
                def.DefaultText = item.GetStringProp("defaultText");
                if (item.TryGetProperty("harmonize", out var elHarm))
                    def.Harmonize = ReadHarmonize(elHarm);
                def.StrokeThickness = item.GetIntProp("strokeThickness", 0);
                if (item.TryGetProperty("strokeColor", out var sc) && sc.ValueKind == JsonValueKind.Array)
                    def.StrokeColor = ReadColorArray(sc);
                def.StrokeMode = item.GetStringProp("strokeMode", "inside");
                if (item.TryGetProperty("textRegion", out var tr))
                    def.TextRegion = ReadTextRegion(tr);
                into.Add(def);
            }
            return UIDefsLoadResult.Ok;
        }
        catch (Exception ex)
        {
            Core.DebugLog.Log("error", $"UIDefsIO: failed to parse {path}: {ex.Message}");
            return UIDefsLoadResult.Failed;
        }
    }

    public static bool SaveElements(string path, IReadOnlyList<UIEditorElementDef> defs)
        => WriteFile(path, writer =>
        {
            writer.WriteStartObject();
            writer.WritePropertyName("elements");
            writer.WriteStartArray();
            foreach (var el in defs)
            {
                writer.WriteStartObject();
                writer.WriteString("id", el.Id);
                writer.WriteString("type", el.Type);
                if (!string.IsNullOrEmpty(el.NineSlice))
                    writer.WriteString("nineSlice", el.NineSlice);
                if (Math.Abs(el.NineSliceScale - 1f) > 0.001f)
                    writer.WriteNumber("nineSliceScale", el.NineSliceScale);
                if (!string.IsNullOrEmpty(el.ImagePath))
                    writer.WriteString("imagePath", el.ImagePath);
                writer.WriteNumber("width", el.Width);
                writer.WriteNumber("height", el.Height);
                if (el.TintColor != null)
                    WriteColorArray(writer, "tintColor", el.TintColor);
                if (!string.IsNullOrEmpty(el.DefaultText))
                    writer.WriteString("defaultText", el.DefaultText);
                if (el.Harmonize != null && el.Harmonize.HasEffect)
                    WriteHarmonize(writer, "harmonize", el.Harmonize);
                if (el.StrokeThickness > 0)
                {
                    writer.WriteNumber("strokeThickness", el.StrokeThickness);
                    WriteColorArray(writer, "strokeColor", el.StrokeColor);
                    writer.WriteString("strokeMode", el.StrokeMode);
                }
                if (el.TextRegion != null)
                    WriteTextRegion(writer, "textRegion", el.TextRegion);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        });

    // ═══════════════════════════════════════
    //  Widgets
    // ═══════════════════════════════════════

    public static UIDefsLoadResult LoadWidgets(string path, List<UIEditorWidgetDef> into)
    {
        if (!File.Exists(path)) return UIDefsLoadResult.Missing;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("widgets", out var arr))
            {
                Core.DebugLog.Log("error", $"UIDefsIO: {path} has no \"widgets\" root");
                return UIDefsLoadResult.Failed;
            }
            foreach (var item in arr.EnumerateArray())
            {
                var def = new UIEditorWidgetDef
                {
                    Id = item.GetStringProp("id"),
                    Background = item.GetStringProp("background"),
                    Width = item.GetIntProp("width", 200),
                    Height = item.GetIntProp("height", 100),
                    BackgroundScale = item.GetFloatProp("backgroundScale", 1f),
                    Modal = item.GetBoolProp("modal"),
                    Layout = item.GetStringProp("layout", "none"),
                    LayoutPadding = item.GetIntProp("layoutPadding"),
                    LayoutSpacing = item.GetIntProp("layoutSpacing"),
                    LayoutPadTop = item.GetIntProp("layoutPadTop"),
                    LayoutPadBottom = item.GetIntProp("layoutPadBottom"),
                    LayoutPadLeft = item.GetIntProp("layoutPadLeft"),
                    LayoutPadRight = item.GetIntProp("layoutPadRight"),
                    LayoutSpacingX = item.GetIntProp("layoutSpacingX"),
                    LayoutSpacingY = item.GetIntProp("layoutSpacingY"),
                    Scroll = item.GetStringProp("scroll", "none"),
                    ScrollContentW = item.GetIntProp("scrollContentW"),
                    ScrollContentH = item.GetIntProp("scrollContentH"),
                    ScrollStep = item.GetIntProp("scrollStep", 20),
                    IsScrollbar = item.GetBoolProp("isScrollbar"),                   // RI20
                    ScrollbarProportional = item.GetBoolProp("scrollbarProportional"), // RI20
                };
                def.Stencil = item.GetStringProp("stencil");
                def.StencilImagePath = item.GetStringProp("stencilImagePath");
                def.BackgroundImagePath = item.GetStringProp("backgroundImagePath");
                def.AutoSizeHeight = item.GetBoolProp("autoSizeHeight");
                def.Frame = item.GetStringProp("frame");
                def.FrameScale = item.GetFloatProp("frameScale", 1f);
                def.BackgroundInset = item.GetIntProp("backgroundInset");
                def.StencilInset = item.GetIntProp("stencilInset");
                def.FrameInset = item.GetIntProp("frameInset");
                def.FrameInsetR = item.GetIntProp("frameInsetR");
                if (item.TryGetProperty("bgHarmonize", out var bgH)) def.BgHarmonize = ReadHarmonize(bgH);
                if (item.TryGetProperty("stencilHarmonize", out var stH)) def.StencilHarmonize = ReadHarmonize(stH);
                if (item.TryGetProperty("frameHarmonize", out var frH)) def.FrameHarmonize = ReadHarmonize(frH);
                if (item.TryGetProperty("backgroundTint", out var bgTint) && bgTint.ValueKind == JsonValueKind.Array)
                    def.BackgroundTint = ReadColorArray(bgTint);
                if (item.TryGetProperty("stencilTint", out var stTint) && stTint.ValueKind == JsonValueKind.Array)
                    def.StencilTint = ReadColorArray(stTint);
                if (item.TryGetProperty("frameTint", out var frTint) && frTint.ValueKind == JsonValueKind.Array)
                    def.FrameTint = ReadColorArray(frTint);
                if (item.TryGetProperty("children", out var children))
                {
                    foreach (var ch in children.EnumerateArray())
                        def.Children.Add(ReadChild(ch));
                }
                into.Add(def);
            }
            return UIDefsLoadResult.Ok;
        }
        catch (Exception ex)
        {
            Core.DebugLog.Log("error", $"UIDefsIO: failed to parse {path}: {ex.Message}");
            return UIDefsLoadResult.Failed;
        }
    }

    private static UIEditorChildDef ReadChild(JsonElement ch)
    {
        var child = new UIEditorChildDef
        {
            Name = ch.GetStringProp("name"),
            Element = ch.GetStringProp("element"),
            Widget = ch.GetStringProp("widget"),
            X = ch.GetIntProp("x"),
            Y = ch.GetIntProp("y"),
            Width = ch.GetIntProp("width"),
            Height = ch.GetIntProp("height"),
            Anchor = ch.GetIntProp("anchor"),
            SizeMode = ch.GetStringProp("sizeMode"),
            NineSliceScale = ch.GetFloatProp("nineSliceScale", 1f),
            Interactive = ch.GetBoolProp("interactive"),
            DefaultText = ch.GetStringProp("defaultText"),
            IgnoreLayout = ch.GetBoolProp("ignoreLayout"),
        };
        if (ch.TryGetProperty("tints", out var tints))
        {
            child.Tints = new UIEditorTints();
            if (tints.TryGetProperty("normal", out var tn)) child.Tints.Normal = ReadColorArray(tn);
            if (tints.TryGetProperty("hovered", out var th)) child.Tints.Hovered = ReadColorArray(th);
            if (tints.TryGetProperty("pressed", out var tp)) child.Tints.Pressed = ReadColorArray(tp);
            if (tints.TryGetProperty("disabled", out var td)) child.Tints.Disabled = ReadColorArray(td);
        }
        // RI21: text override
        if (ch.TryGetProperty("textOverride", out var txo))
        {
            child.HasTextOverride = true;
            child.TextOverride = ReadTextRegion(txo);
        }
        // RI22: child overrides
        if (ch.TryGetProperty("childOverrides", out var coArr) && coArr.ValueKind == JsonValueKind.Array)
        {
            child.ChildOverrides = new List<ChildOverrideEntry>();
            foreach (var co in coArr.EnumerateArray())
            {
                var entry = new ChildOverrideEntry
                {
                    ChildIndex = co.GetIntProp("childIndex"),
                };
                if (co.TryGetProperty("overrideX", out var ox) && ox.ValueKind == JsonValueKind.Number)
                    entry.OverrideX = ox.GetInt32();
                if (co.TryGetProperty("overrideY", out var oy) && oy.ValueKind == JsonValueKind.Number)
                    entry.OverrideY = oy.GetInt32();
                if (co.TryGetProperty("overrideW", out var ow) && ow.ValueKind == JsonValueKind.Number)
                    entry.OverrideW = ow.GetInt32();
                if (co.TryGetProperty("overrideH", out var oh) && oh.ValueKind == JsonValueKind.Number)
                    entry.OverrideH = oh.GetInt32();
                if (co.TryGetProperty("overrideDefaultText", out var odt) && odt.ValueKind == JsonValueKind.String)
                    entry.OverrideDefaultText = odt.GetString();
                if (co.TryGetProperty("overrideElement", out var oel) && oel.ValueKind == JsonValueKind.String)
                    entry.OverrideElement = oel.GetString();
                if (co.TryGetProperty("overrideIgnoreLayout", out var oil))
                {
                    if (oil.ValueKind == JsonValueKind.True) entry.OverrideIgnoreLayout = true;
                    else if (oil.ValueKind == JsonValueKind.False) entry.OverrideIgnoreLayout = false;
                }
                child.ChildOverrides.Add(entry);
            }
        }
        return child;
    }

    public static bool SaveWidgets(string path, IReadOnlyList<UIEditorWidgetDef> defs)
        => WriteFile(path, writer =>
        {
            writer.WriteStartObject();
            writer.WritePropertyName("widgets");
            writer.WriteStartArray();
            foreach (var wd in defs)
            {
                writer.WriteStartObject();
                writer.WriteString("id", wd.Id);
                if (!string.IsNullOrEmpty(wd.Background))
                    writer.WriteString("background", wd.Background);
                if (!string.IsNullOrEmpty(wd.Stencil))
                    writer.WriteString("stencil", wd.Stencil);
                if (!string.IsNullOrEmpty(wd.StencilImagePath))
                    writer.WriteString("stencilImagePath", wd.StencilImagePath);
                if (!string.IsNullOrEmpty(wd.BackgroundImagePath))
                    writer.WriteString("backgroundImagePath", wd.BackgroundImagePath);
                if (wd.AutoSizeHeight)
                    writer.WriteBoolean("autoSizeHeight", true);
                if (!string.IsNullOrEmpty(wd.Frame))
                    writer.WriteString("frame", wd.Frame);
                writer.WriteNumber("width", wd.Width);
                writer.WriteNumber("height", wd.Height);
                writer.WriteNumber("backgroundScale", wd.BackgroundScale);
                if (wd.FrameScale != 1f)
                    writer.WriteNumber("frameScale", wd.FrameScale);
                if (wd.BackgroundInset != 0)
                    writer.WriteNumber("backgroundInset", wd.BackgroundInset);
                if (wd.StencilInset != 0)
                    writer.WriteNumber("stencilInset", wd.StencilInset);
                if (wd.FrameInset != 0)
                    writer.WriteNumber("frameInset", wd.FrameInset);
                if (wd.FrameInsetR != 0)
                    writer.WriteNumber("frameInsetR", wd.FrameInsetR);
                if (wd.BackgroundTint != null)
                    WriteColorArray(writer, "backgroundTint", wd.BackgroundTint);
                if (wd.StencilTint != null)
                    WriteColorArray(writer, "stencilTint", wd.StencilTint);
                if (wd.FrameTint != null)
                    WriteColorArray(writer, "frameTint", wd.FrameTint);
                if (wd.BgHarmonize != null && wd.BgHarmonize.HasEffect)
                    WriteHarmonize(writer, "bgHarmonize", wd.BgHarmonize);
                if (wd.StencilHarmonize != null && wd.StencilHarmonize.HasEffect)
                    WriteHarmonize(writer, "stencilHarmonize", wd.StencilHarmonize);
                if (wd.FrameHarmonize != null && wd.FrameHarmonize.HasEffect)
                    WriteHarmonize(writer, "frameHarmonize", wd.FrameHarmonize);
                writer.WriteBoolean("modal", wd.Modal);
                if (wd.Layout != "none" && !string.IsNullOrEmpty(wd.Layout))
                {
                    writer.WriteString("layout", wd.Layout);
                    writer.WriteNumber("layoutPadding", wd.LayoutPadding);
                    writer.WriteNumber("layoutSpacing", wd.LayoutSpacing);
                    writer.WriteNumber("layoutPadTop", wd.LayoutPadTop);
                    writer.WriteNumber("layoutPadBottom", wd.LayoutPadBottom);
                    writer.WriteNumber("layoutPadLeft", wd.LayoutPadLeft);
                    writer.WriteNumber("layoutPadRight", wd.LayoutPadRight);
                    writer.WriteNumber("layoutSpacingX", wd.LayoutSpacingX);
                    writer.WriteNumber("layoutSpacingY", wd.LayoutSpacingY);
                }
                if (wd.Scroll != "none" && !string.IsNullOrEmpty(wd.Scroll))
                {
                    writer.WriteString("scroll", wd.Scroll);
                    writer.WriteNumber("scrollContentW", wd.ScrollContentW);
                    writer.WriteNumber("scrollContentH", wd.ScrollContentH);
                    writer.WriteNumber("scrollStep", wd.ScrollStep);
                }
                // RI20: scrollbar properties
                if (wd.IsScrollbar)
                    writer.WriteBoolean("isScrollbar", true);
                if (wd.ScrollbarProportional)
                    writer.WriteBoolean("scrollbarProportional", true);
                if (wd.Children.Count > 0)
                {
                    writer.WritePropertyName("children");
                    writer.WriteStartArray();
                    foreach (var ch in wd.Children)
                        WriteChild(writer, ch);
                    writer.WriteEndArray();
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        });

    private static void WriteChild(Utf8JsonWriter writer, UIEditorChildDef ch)
    {
        writer.WriteStartObject();
        writer.WriteString("name", ch.Name);
        if (!string.IsNullOrEmpty(ch.Element))
            writer.WriteString("element", ch.Element);
        if (!string.IsNullOrEmpty(ch.Widget))
            writer.WriteString("widget", ch.Widget);
        writer.WriteNumber("x", ch.X);
        writer.WriteNumber("y", ch.Y);
        writer.WriteNumber("width", ch.Width);
        writer.WriteNumber("height", ch.Height);
        writer.WriteNumber("anchor", ch.Anchor);
        if (!string.IsNullOrEmpty(ch.SizeMode))
            writer.WriteString("sizeMode", ch.SizeMode);
        if (Math.Abs(ch.NineSliceScale - 1f) > 0.001f)
            writer.WriteNumber("nineSliceScale", ch.NineSliceScale);
        if (ch.Interactive)
        {
            writer.WriteBoolean("interactive", true);
            if (ch.Tints != null)
            {
                writer.WritePropertyName("tints");
                writer.WriteStartObject();
                WriteColorArray(writer, "normal", ch.Tints.Normal);
                WriteColorArray(writer, "hovered", ch.Tints.Hovered);
                WriteColorArray(writer, "pressed", ch.Tints.Pressed);
                WriteColorArray(writer, "disabled", ch.Tints.Disabled);
                writer.WriteEndObject();
            }
        }
        if (!string.IsNullOrEmpty(ch.DefaultText))
            writer.WriteString("defaultText", ch.DefaultText);
        if (ch.IgnoreLayout)
            writer.WriteBoolean("ignoreLayout", true);
        // RI21: text override
        if (ch.HasTextOverride && ch.TextOverride != null)
            WriteTextRegion(writer, "textOverride", ch.TextOverride);
        // RI22: child overrides
        if (ch.ChildOverrides != null && ch.ChildOverrides.Count > 0)
        {
            writer.WritePropertyName("childOverrides");
            writer.WriteStartArray();
            foreach (var co in ch.ChildOverrides)
            {
                // Only write entries that have at least one override set
                bool hasAny = co.OverrideX.HasValue || co.OverrideY.HasValue ||
                              co.OverrideW.HasValue || co.OverrideH.HasValue ||
                              co.OverrideDefaultText != null || co.OverrideElement != null ||
                              co.OverrideIgnoreLayout.HasValue;
                if (!hasAny) continue;
                writer.WriteStartObject();
                writer.WriteNumber("childIndex", co.ChildIndex);
                if (co.OverrideX.HasValue) writer.WriteNumber("overrideX", co.OverrideX.Value);
                if (co.OverrideY.HasValue) writer.WriteNumber("overrideY", co.OverrideY.Value);
                if (co.OverrideW.HasValue) writer.WriteNumber("overrideW", co.OverrideW.Value);
                if (co.OverrideH.HasValue) writer.WriteNumber("overrideH", co.OverrideH.Value);
                if (co.OverrideDefaultText != null) writer.WriteString("overrideDefaultText", co.OverrideDefaultText);
                if (co.OverrideElement != null) writer.WriteString("overrideElement", co.OverrideElement);
                if (co.OverrideIgnoreLayout.HasValue) writer.WriteBoolean("overrideIgnoreLayout", co.OverrideIgnoreLayout.Value);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }

    // ═══════════════════════════════════════
    //  Shared fragments (text region, harmonize, colors)
    // ═══════════════════════════════════════

    private static UIEditorTextRegion ReadTextRegion(JsonElement tr)
    {
        var region = new UIEditorTextRegion
        {
            X = tr.GetIntProp("x"),
            Y = tr.GetIntProp("y"),
            W = tr.GetIntProp("w"),
            H = tr.GetIntProp("h"),
            Align = tr.GetStringProp("align", "left"),
            VAlign = tr.GetStringProp("valign", "top"),
            FontFamily = tr.GetStringProp("fontFamily"),
            FontSize = tr.GetIntProp("fontSize", 14),
            WordWrap = tr.GetBoolProp("wordWrap"),
            LineSpacing = tr.GetIntProp("lineSpacing"),
            CharSpacing = tr.GetFloatProp("charSpacing"),
            Bold = tr.GetBoolProp("bold"),
            BoldStrength = tr.GetFloatProp("boldStrength", 1f),
            TextOutlineWidth = tr.GetIntProp("outlineWidth"),
            OutlineOffsetX = tr.GetIntProp("outlineOffsetX"),
            OutlineOffsetY = tr.GetIntProp("outlineOffsetY"),
        };
        if (tr.TryGetProperty("fontColor", out var fc) && fc.ValueKind == JsonValueKind.Array)
            region.FontColor = ReadColorArray(fc);
        if (tr.TryGetProperty("outlineColor", out var oc) && oc.ValueKind == JsonValueKind.Array)
            region.TextOutlineColor = ReadColorArray(oc);
        return region;
    }

    private static void WriteTextRegion(Utf8JsonWriter writer, string name, UIEditorTextRegion tr)
    {
        writer.WritePropertyName(name);
        writer.WriteStartObject();
        writer.WriteNumber("x", tr.X);
        writer.WriteNumber("y", tr.Y);
        writer.WriteNumber("w", tr.W);
        writer.WriteNumber("h", tr.H);
        writer.WriteString("align", tr.Align);
        writer.WriteString("valign", tr.VAlign);
        if (!string.IsNullOrEmpty(tr.FontFamily))
            writer.WriteString("fontFamily", tr.FontFamily);
        writer.WriteNumber("fontSize", tr.FontSize);
        WriteColorArray(writer, "fontColor", tr.FontColor);
        if (tr.WordWrap)
            writer.WriteBoolean("wordWrap", true);
        if (tr.LineSpacing != 0)
            writer.WriteNumber("lineSpacing", tr.LineSpacing);
        if (tr.CharSpacing != 0)
            writer.WriteNumber("charSpacing", tr.CharSpacing);
        if (tr.Bold)
            writer.WriteBoolean("bold", true);
        if (tr.BoldStrength != 1f)
            writer.WriteNumber("boldStrength", tr.BoldStrength);
        if (tr.TextOutlineWidth > 0 && tr.TextOutlineColor != null)
        {
            writer.WriteNumber("outlineWidth", tr.TextOutlineWidth);
            WriteColorArray(writer, "outlineColor", tr.TextOutlineColor);
            if (tr.OutlineOffsetX != 0)
                writer.WriteNumber("outlineOffsetX", tr.OutlineOffsetX);
            if (tr.OutlineOffsetY != 0)
                writer.WriteNumber("outlineOffsetY", tr.OutlineOffsetY);
        }
        writer.WriteEndObject();
    }

    internal static HarmonizeSettings? ReadHarmonize(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        var h = new HarmonizeSettings();
        if (el.TryGetProperty("targetColor", out var tc) && tc.ValueKind == JsonValueKind.Array)
            h.TargetColor = ReadColorArray(tc);
        h.HueStrength = el.GetFloatProp("hueStrength");
        h.SatStrength = el.GetFloatProp("satStrength");
        h.ValStrength = el.GetFloatProp("valStrength");
        h.UseHcl = el.GetBoolProp("useHcl");
        if (el.TryGetProperty("gradColor", out var gc) && gc.ValueKind == JsonValueKind.Array)
            h.GradColor = ReadColorArray(gc);
        h.GradStrength = el.GetFloatProp("gradStrength");
        if (el.TryGetProperty("outlineColor", out var oc) && oc.ValueKind == JsonValueKind.Array)
            h.OutlineColor = ReadColorArray(oc);
        h.OutlineThickness = el.GetFloatProp("outlineThickness");
        h.OutlineOpacity = el.GetFloatProp("outlineOpacity", 1f);
        return h;
    }

    internal static void WriteHarmonize(Utf8JsonWriter writer, string name, HarmonizeSettings h)
    {
        writer.WritePropertyName(name);
        writer.WriteStartObject();
        WriteColorArray(writer, "targetColor", h.TargetColor);
        writer.WriteNumber("hueStrength", h.HueStrength);
        writer.WriteNumber("satStrength", h.SatStrength);
        writer.WriteNumber("valStrength", h.ValStrength);
        writer.WriteBoolean("useHcl", h.UseHcl);
        if (h.HasGradient)
        {
            WriteColorArray(writer, "gradColor", h.GradColor!);
            writer.WriteNumber("gradStrength", h.GradStrength);
        }
        if (h.HasOutline)
        {
            WriteColorArray(writer, "outlineColor", h.OutlineColor!);
            writer.WriteNumber("outlineThickness", h.OutlineThickness);
            writer.WriteNumber("outlineOpacity", h.OutlineOpacity);
        }
        writer.WriteEndObject();
    }

    internal static byte[] ReadColorArray(JsonElement el)
    {
        var list = new List<byte>();
        foreach (var v in el.EnumerateArray())
            list.Add((byte)Math.Clamp(v.GetInt32(), 0, 255));
        while (list.Count < 4) list.Add(255);
        return list.ToArray();
    }

    internal static void WriteColorArray(Utf8JsonWriter writer, string name, byte[] color)
    {
        writer.WritePropertyName(name);
        writer.WriteStartArray();
        foreach (var b in color) writer.WriteNumberValue(b);
        writer.WriteEndArray();
    }

    /// <summary>Serialize through a MemoryStream, then hand the text to
    /// <see cref="Core.JsonFile.WriteStringIfChanged"/> — atomic tmp+rename and
    /// no disk touch when the content is unchanged.</summary>
    private static bool WriteFile(string path, Action<Utf8JsonWriter> write)
    {
        try
        {
            using var mem = new MemoryStream();
            using (var writer = new Utf8JsonWriter(mem, new JsonWriterOptions { Indented = true }))
                write(writer);
            string json = System.Text.Encoding.UTF8.GetString(mem.ToArray());
            return Core.JsonFile.WriteStringIfChanged(path, json);
        }
        catch (Exception ex)
        {
            Core.DebugLog.Log("error", $"UIDefsIO: failed to save {path}: {ex.Message}");
            return false;
        }
    }
}
