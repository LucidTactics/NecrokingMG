using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Necroking.Core;
using Necroking.UI;

namespace Necroking.Editor;

public enum UIEditorTab { NineSlices, Elements, Widgets }

// ─────────────────────────────────────────────
// Nine-Slice definition (editor working copy)
// ─────────────────────────────────────────────
public class UIEditorNineSliceDef
{
    public string Id { get; set; } = "";
    public string Texture { get; set; } = "";
    public int BorderLeft { get; set; }
    public int BorderRight { get; set; }
    public int BorderTop { get; set; }
    public int BorderBottom { get; set; }
    public bool TileEdges { get; set; }
}

// ─────────────────────────────────────────────
// Element text region
// ─────────────────────────────────────────────
public class UIEditorTextRegion
{
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }
    public string Align { get; set; } = "left";
    public string VAlign { get; set; } = "top";
    public int FontSize { get; set; } = 14;
    public byte[] FontColor { get; set; } = { 255, 255, 255, 255 };
    public bool WordWrap { get; set; }  // RI21
}

// ─────────────────────────────────────────────
// Element definition (editor working copy)
// ─────────────────────────────────────────────
public class UIEditorElementDef
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "nineSlice";
    public string NineSlice { get; set; } = "";
    public string ImagePath { get; set; } = "";  // texture path for image type
    public int Width { get; set; } = 100;
    public int Height { get; set; } = 40;
    public byte[]? TintColor { get; set; }
    public UIEditorTextRegion? TextRegion { get; set; }
    // Stroke/outline
    public int StrokeThickness { get; set; }           // 0 = no stroke
    public byte[] StrokeColor { get; set; } = { 255, 255, 255, 255 };
    public string StrokeMode { get; set; } = "inside"; // "inside", "outside", "center"
}

// ─────────────────────────────────────────────
// Child override entry (RI22: nested overrides)
// ─────────────────────────────────────────────
public class ChildOverrideEntry
{
    public int ChildIndex { get; set; }
    public int? OverrideX { get; set; }
    public int? OverrideY { get; set; }
    public int? OverrideW { get; set; }
    public int? OverrideH { get; set; }
    public string? OverrideDefaultText { get; set; }
    public bool? OverrideIgnoreLayout { get; set; }
}

// ─────────────────────────────────────────────
// Widget child definition (editor working copy)
// ─────────────────────────────────────────────
public class UIEditorChildDef
{
    public string Name { get; set; } = "";
    public string Element { get; set; } = "";
    public string Widget { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int Anchor { get; set; }
    public bool Interactive { get; set; }
    public string DefaultText { get; set; } = "";
    public UIEditorTints? Tints { get; set; }
    public string Stretch { get; set; } = "";
    public bool IgnoreLayout { get; set; }
    public float NineSliceScale { get; set; } = 1f;

    // RI21: Text override per-child
    public bool HasTextOverride { get; set; }
    public UIEditorTextRegion? TextOverride { get; set; }

    // RI22: Child overrides (nested override system)
    public List<ChildOverrideEntry>? ChildOverrides { get; set; }
}

public class UIEditorTints
{
    public byte[] Normal { get; set; } = { 255, 255, 255, 255 };
    public byte[] Hovered { get; set; } = { 220, 220, 255, 255 };
    public byte[] Pressed { get; set; } = { 180, 180, 200, 255 };
    public byte[] Disabled { get; set; } = { 128, 128, 128, 180 };
}

// ─────────────────────────────────────────────
// Widget definition (editor working copy)
// ─────────────────────────────────────────────
public class UIEditorWidgetDef
{
    public string Id { get; set; } = "";
    public string Background { get; set; } = "";
    public int Width { get; set; } = 200;
    public int Height { get; set; } = 100;
    public float BackgroundScale { get; set; } = 1f;
    public byte[]? BackgroundTint { get; set; }  // RI23: background tint color
    public bool Modal { get; set; }
    public string Layout { get; set; } = "none";
    public int LayoutPadding { get; set; }
    public int LayoutSpacing { get; set; }
    public int LayoutPadTop { get; set; }
    public int LayoutPadBottom { get; set; }
    public int LayoutPadLeft { get; set; }
    public int LayoutPadRight { get; set; }
    public int LayoutSpacingX { get; set; }
    public int LayoutSpacingY { get; set; }
    public string Scroll { get; set; } = "none";
    public int ScrollContentW { get; set; }
    public int ScrollContentH { get; set; }
    public int ScrollStep { get; set; } = 20;
    public bool IsScrollbar { get; set; }              // RI20
    public bool ScrollbarProportional { get; set; }    // RI20
    public List<UIEditorChildDef> Children { get; set; } = new();
}

// ═══════════════════════════════════════════════
// Main UI Editor Window
// ═══════════════════════════════════════════════
public class UIEditorWindow : EditorBase
{
    private GraphicsDevice? _device;

    // Loaded data
    private readonly List<UIEditorNineSliceDef> _nineSlices = new();
    private readonly List<UIEditorElementDef> _elements = new();
    private readonly List<UIEditorWidgetDef> _widgets = new();
    private bool _loaded;

    // Editor state
    public UIEditorTab ActiveTab = UIEditorTab.NineSlices;
    public int SelectedIndex = -1;
    private int _selectedChildIdx = -1;

    // Hierarchical child tree state (UI11)
    private List<int> _selectedChildPath = new();  // path of indices for nested selection
    private readonly HashSet<string> _expandedPaths = new(); // "0", "0/1", etc.

    // Child drag-reorder state (UI12)
    private bool _childDragActive;
    private int _childDragSourceIdx = -1;
    private int _childDragInsertIdx = -1;
    private int _childDragStartY;

    // Child clipboard (UI13)
    private UIEditorChildDef? _childClipboard;

    // Circular reference warning (UI15)
    private string _circularRefWarning = "";
    private float _circularRefWarningTimer;

    // Color Harmonizer (CP04 / UI19)
    private readonly ColorHarmonizer _harmonizer = new();
    private bool _harmonizerOpen;
    private enum HarmonizerTarget { ElementTint, WidgetChildTints, WidgetBgTint }
    private HarmonizerTarget _harmonizerTarget;

    // Texture file browser (UI01)
    private readonly TextureFileBrowser _textureBrowser = new();

    // Status
    private string _statusMsg = "";
    private float _statusTimer;
    private bool _unsavedChanges;

    // Nine-slice border dragging
    private int _draggingBorder = -1; // -1=none, 0=L, 1=R, 2=T, 3=B

    // Element canvas drag
    private enum CanvasDragMode { None, Move, EdgeL, EdgeR, EdgeT, EdgeB, CornerTL, CornerTR, CornerBL, CornerBR }
    private enum CanvasDragTarget { None, Element, TextRegion }
    private CanvasDragMode _dragMode = CanvasDragMode.None;
    private CanvasDragTarget _dragTarget = CanvasDragTarget.None;
    private Point _dragStart;
    private int _dragOrigX, _dragOrigY, _dragOrigW, _dragOrigH;

    // Widget canvas drag
    private int _canvasDragChild = -1; // -1=none, -2=widget itself, >=0 = child index
    private CanvasDragMode _widgetDragMode = CanvasDragMode.None;
    private Point _widgetDragStart;
    private int _wDragOrigX, _wDragOrigY, _wDragOrigW, _wDragOrigH;

    // Loaded textures (for nine-slice display)
    private readonly Dictionary<string, Texture2D> _textures = new();

    // Harmonized texture cache (per-pixel color-shifted copies, keyed by source path)
    private readonly Dictionary<string, Texture2D> _harmonizedTextures = new();
    private readonly Dictionary<string, NineSlice> _harmonizedNineSlices = new();

    // Loaded NineSlice instances (for preview rendering)
    private readonly Dictionary<string, NineSlice> _nsInstances = new();

    // Definitions path
    private string _defsDir = "";

    // Previous mouse state (for standalone usage without EditorBase.UpdateInput)
    private MouseState _prevMouseLocal;
    private GameTime? _lastGameTime;

    // Detail panel scroll offsets
    private float _nsDetailScroll;
    private float _elemDetailScroll;
    private float _widgetDetailScroll;

    // Add-child DrawCombo IDs (for programmatic close on tab switch)
    private const string AddElemComboId = "addchild_elem_combo";
    private const string AddWgtComboId = "addchild_wgt_combo";

    // ═══════════════════════════════════════
    //  Initialization (two signatures for compat)
    // ═══════════════════════════════════════

    /// <summary>Old-style init compatible with existing Game1 code.</summary>
    public void Init(SpriteBatch spriteBatch, Texture2D pixel, SpriteFont? font, SpriteFont? smallFont)
    {
        SetContext(spriteBatch, pixel, font, smallFont, null);
        _device = spriteBatch.GraphicsDevice;
        _textureBrowser.SetGraphicsDevice(_device);
    }

    /// <summary>Init with a GraphicsDevice and definitions directory.</summary>
    public void Init(GraphicsDevice device, string definitionsDir)
    {
        _device = device;
        _defsDir = definitionsDir;
        LoadDefinitions(definitionsDir);
    }

    // ═══════════════════════════════════════
    //  JSON Loading / Saving
    // ═══════════════════════════════════════

    public void LoadDefinitions(string dirPath)
    {
        _nineSlices.Clear();
        _elements.Clear();
        _widgets.Clear();
        _defsDir = dirPath;

        LoadNineSlices(Path.Combine(dirPath, "nine_slices.json"));
        LoadElements(Path.Combine(dirPath, "elements.json"));
        LoadWidgets(Path.Combine(dirPath, "widgets.json"));

        _loaded = true;
        _unsavedChanges = false;
    }

    private void LoadNineSlices(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("nineSlices", out var arr)) return;
            foreach (var item in arr.EnumerateArray())
            {
                _nineSlices.Add(new UIEditorNineSliceDef
                {
                    Id = item.GetStringProp("id"),
                    Texture = item.GetStringProp("texture"),
                    BorderLeft = item.GetIntProp("borderLeft"),
                    BorderRight = item.GetIntProp("borderRight"),
                    BorderTop = item.GetIntProp("borderTop"),
                    BorderBottom = item.GetIntProp("borderBottom"),
                    TileEdges = item.GetBoolProp("tileEdges")
                });
            }
        }
        catch { /* skip parse errors */ }
    }

    private void LoadElements(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("elements", out var arr)) return;
            foreach (var item in arr.EnumerateArray())
            {
                var def = new UIEditorElementDef
                {
                    Id = item.GetStringProp("id"),
                    Type = item.GetStringProp("type", "nineSlice"),
                    NineSlice = item.GetStringProp("nineSlice"),
                    ImagePath = item.GetStringProp("imagePath"),
                    Width = item.GetIntProp("width", 100),
                    Height = item.GetIntProp("height", 40),
                };
                if (item.TryGetProperty("tintColor", out var tc) && tc.ValueKind == JsonValueKind.Array)
                    def.TintColor = ReadColorArray(tc);
                def.StrokeThickness = item.GetIntProp("strokeThickness", 0);
                if (item.TryGetProperty("strokeColor", out var sc) && sc.ValueKind == JsonValueKind.Array)
                    def.StrokeColor = ReadColorArray(sc);
                def.StrokeMode = item.GetStringProp("strokeMode", "inside");
                if (item.TryGetProperty("textRegion", out var tr))
                {
                    def.TextRegion = new UIEditorTextRegion
                    {
                        X = tr.GetIntProp("x"),
                        Y = tr.GetIntProp("y"),
                        W = tr.GetIntProp("w"),
                        H = tr.GetIntProp("h"),
                        Align = tr.GetStringProp("align", "left"),
                        VAlign = tr.GetStringProp("valign", "top"),
                        FontSize = tr.GetIntProp("fontSize", 14),
                    };
                    if (tr.TryGetProperty("fontColor", out var fc) && fc.ValueKind == JsonValueKind.Array)
                        def.TextRegion.FontColor = ReadColorArray(fc);
                }
                _elements.Add(def);
            }
        }
        catch { /* skip parse errors */ }
    }

    private void LoadWidgets(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("widgets", out var arr)) return;
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
                // RI23: background tint
                if (item.TryGetProperty("backgroundTint", out var bgTint) && bgTint.ValueKind == JsonValueKind.Array)
                    def.BackgroundTint = ReadColorArray(bgTint);
                if (item.TryGetProperty("children", out var children))
                {
                    foreach (var ch in children.EnumerateArray())
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
                            Interactive = ch.GetBoolProp("interactive"),
                            DefaultText = ch.GetStringProp("defaultText"),
                            Stretch = ch.GetStringProp("stretch"),
                            IgnoreLayout = ch.GetBoolProp("ignoreLayout"),
                            NineSliceScale = ch.GetFloatProp("nineSliceScale", 1f),
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
                            child.TextOverride = new UIEditorTextRegion
                            {
                                X = txo.GetIntProp("x"),
                                Y = txo.GetIntProp("y"),
                                W = txo.GetIntProp("w"),
                                H = txo.GetIntProp("h"),
                                Align = txo.GetStringProp("align", "left"),
                                VAlign = txo.GetStringProp("valign", "top"),
                                FontSize = txo.GetIntProp("fontSize", 14),
                            };
                            if (txo.TryGetProperty("fontColor", out var txfc) && txfc.ValueKind == JsonValueKind.Array)
                                child.TextOverride.FontColor = ReadColorArray(txfc);
                            child.TextOverride.WordWrap = txo.GetBoolProp("wordWrap");
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
                                if (co.TryGetProperty("overrideIgnoreLayout", out var oil))
                                {
                                    if (oil.ValueKind == JsonValueKind.True) entry.OverrideIgnoreLayout = true;
                                    else if (oil.ValueKind == JsonValueKind.False) entry.OverrideIgnoreLayout = false;
                                }
                                child.ChildOverrides.Add(entry);
                            }
                        }
                        def.Children.Add(child);
                    }
                }
                _widgets.Add(def);
            }
        }
        catch { /* skip parse errors */ }
    }

    private static byte[] ReadColorArray(JsonElement el)
    {
        var list = new List<byte>();
        foreach (var v in el.EnumerateArray())
            list.Add((byte)Math.Clamp(v.GetInt32(), 0, 255));
        while (list.Count < 4) list.Add(255);
        return list.ToArray();
    }

    // ── Save ──

    private void SaveNineSlices()
    {
        var path = Path.Combine(_defsDir, "nine_slices.json");
        EnsureDir(path);
        var options = new JsonWriterOptions { Indented = true };
        using var stream = File.Create(path);
        using var writer = new Utf8JsonWriter(stream, options);
        writer.WriteStartObject();
        writer.WritePropertyName("nineSlices");
        writer.WriteStartArray();
        foreach (var ns in _nineSlices)
        {
            writer.WriteStartObject();
            writer.WriteString("id", ns.Id);
            writer.WriteString("texture", ns.Texture);
            writer.WriteNumber("borderLeft", ns.BorderLeft);
            writer.WriteNumber("borderRight", ns.BorderRight);
            writer.WriteNumber("borderTop", ns.BorderTop);
            writer.WriteNumber("borderBottom", ns.BorderBottom);
            writer.WriteBoolean("tileEdges", ns.TileEdges);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private void SaveElements()
    {
        var path = Path.Combine(_defsDir, "elements.json");
        EnsureDir(path);
        var options = new JsonWriterOptions { Indented = true };
        using var stream = File.Create(path);
        using var writer = new Utf8JsonWriter(stream, options);
        writer.WriteStartObject();
        writer.WritePropertyName("elements");
        writer.WriteStartArray();
        foreach (var el in _elements)
        {
            writer.WriteStartObject();
            writer.WriteString("id", el.Id);
            writer.WriteString("type", el.Type);
            if (!string.IsNullOrEmpty(el.NineSlice))
                writer.WriteString("nineSlice", el.NineSlice);
            if (!string.IsNullOrEmpty(el.ImagePath))
                writer.WriteString("imagePath", el.ImagePath);
            writer.WriteNumber("width", el.Width);
            writer.WriteNumber("height", el.Height);
            if (el.TintColor != null)
                WriteColorArray(writer, "tintColor", el.TintColor);
            if (el.StrokeThickness > 0)
            {
                writer.WriteNumber("strokeThickness", el.StrokeThickness);
                WriteColorArray(writer, "strokeColor", el.StrokeColor);
                writer.WriteString("strokeMode", el.StrokeMode);
            }
            if (el.TextRegion != null)
            {
                writer.WritePropertyName("textRegion");
                writer.WriteStartObject();
                writer.WriteNumber("x", el.TextRegion.X);
                writer.WriteNumber("y", el.TextRegion.Y);
                writer.WriteNumber("w", el.TextRegion.W);
                writer.WriteNumber("h", el.TextRegion.H);
                writer.WriteString("align", el.TextRegion.Align);
                writer.WriteString("valign", el.TextRegion.VAlign);
                writer.WriteNumber("fontSize", el.TextRegion.FontSize);
                WriteColorArray(writer, "fontColor", el.TextRegion.FontColor);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private void SaveWidgets()
    {
        var path = Path.Combine(_defsDir, "widgets.json");
        EnsureDir(path);
        var options = new JsonWriterOptions { Indented = true };
        using var stream = File.Create(path);
        using var writer = new Utf8JsonWriter(stream, options);
        writer.WriteStartObject();
        writer.WritePropertyName("widgets");
        writer.WriteStartArray();
        foreach (var wd in _widgets)
        {
            writer.WriteStartObject();
            writer.WriteString("id", wd.Id);
            if (!string.IsNullOrEmpty(wd.Background))
                writer.WriteString("background", wd.Background);
            writer.WriteNumber("width", wd.Width);
            writer.WriteNumber("height", wd.Height);
            writer.WriteNumber("backgroundScale", wd.BackgroundScale);
            if (wd.BackgroundTint != null)
                WriteColorArray(writer, "backgroundTint", wd.BackgroundTint);
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
                    if (!string.IsNullOrEmpty(ch.Stretch))
                        writer.WriteString("stretch", ch.Stretch);
                    if (ch.IgnoreLayout)
                        writer.WriteBoolean("ignoreLayout", true);
                    if (Math.Abs(ch.NineSliceScale - 1f) > 0.001f)
                        writer.WriteNumber("nineSliceScale", ch.NineSliceScale);
                    // RI21: text override
                    if (ch.HasTextOverride && ch.TextOverride != null)
                    {
                        writer.WritePropertyName("textOverride");
                        writer.WriteStartObject();
                        writer.WriteNumber("x", ch.TextOverride.X);
                        writer.WriteNumber("y", ch.TextOverride.Y);
                        writer.WriteNumber("w", ch.TextOverride.W);
                        writer.WriteNumber("h", ch.TextOverride.H);
                        writer.WriteString("align", ch.TextOverride.Align);
                        writer.WriteString("valign", ch.TextOverride.VAlign);
                        writer.WriteNumber("fontSize", ch.TextOverride.FontSize);
                        WriteColorArray(writer, "fontColor", ch.TextOverride.FontColor);
                        if (ch.TextOverride.WordWrap)
                            writer.WriteBoolean("wordWrap", true);
                        writer.WriteEndObject();
                    }
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
                                          co.OverrideDefaultText != null || co.OverrideIgnoreLayout.HasValue;
                            if (!hasAny) continue;
                            writer.WriteStartObject();
                            writer.WriteNumber("childIndex", co.ChildIndex);
                            if (co.OverrideX.HasValue) writer.WriteNumber("overrideX", co.OverrideX.Value);
                            if (co.OverrideY.HasValue) writer.WriteNumber("overrideY", co.OverrideY.Value);
                            if (co.OverrideW.HasValue) writer.WriteNumber("overrideW", co.OverrideW.Value);
                            if (co.OverrideH.HasValue) writer.WriteNumber("overrideH", co.OverrideH.Value);
                            if (co.OverrideDefaultText != null) writer.WriteString("overrideDefaultText", co.OverrideDefaultText);
                            if (co.OverrideIgnoreLayout.HasValue) writer.WriteBoolean("overrideIgnoreLayout", co.OverrideIgnoreLayout.Value);
                            writer.WriteEndObject();
                        }
                        writer.WriteEndArray();
                    }
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteColorArray(Utf8JsonWriter writer, string name, byte[] color)
    {
        writer.WritePropertyName(name);
        writer.WriteStartArray();
        foreach (var b in color) writer.WriteNumberValue(b);
        writer.WriteEndArray();
    }

    private static void EnsureDir(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
    }

    // ═══════════════════════════════════════
    //  Texture loading helpers
    // ═══════════════════════════════════════

    private Texture2D? GetOrLoadTexture(string texPath)
    {
        if (string.IsNullOrEmpty(texPath) || _device == null) return null;
        if (_textures.TryGetValue(texPath, out var tex)) return tex;

        if (!File.Exists(texPath)) return null;
        try
        {
            using var stream = File.OpenRead(texPath);
            tex = Texture2D.FromStream(_device, stream);
            _textures[texPath] = tex;
            return tex;
        }
        catch { return null; }
    }

    private NineSlice? GetOrLoadNineSlice(string nsId)
    {
        if (string.IsNullOrEmpty(nsId) || _device == null) return null;
        if (_nsInstances.TryGetValue(nsId, out var ns)) return ns;

        var def = _nineSlices.FirstOrDefault(d => d.Id == nsId);
        if (def == null) return null;

        var nsDef = new NineSliceDef
        {
            Id = def.Id,
            TexturePath = def.Texture,
            BorderLeft = def.BorderLeft,
            BorderRight = def.BorderRight,
            BorderTop = def.BorderTop,
            BorderBottom = def.BorderBottom,
            TileEdges = def.TileEdges,
        };
        ns = new NineSlice();
        if (!ns.Load(_device, nsDef)) return null;
        _nsInstances[nsId] = ns;
        return ns;
    }

    /// <summary>Invalidate cached NineSlice instance so it gets rebuilt from current def.</summary>
    private void InvalidateNineSlice(string nsId)
    {
        if (_nsInstances.TryGetValue(nsId, out var old))
        {
            old.Unload();
            _nsInstances.Remove(nsId);
        }
    }

    // ═══════════════════════════════════════
    //  Mouse helpers
    // ═══════════════════════════════════════

    private bool LeftJustPressed => _mouse.LeftButton == ButtonState.Pressed &&
                                    _prevMouse.LeftButton == ButtonState.Released;
    private bool LeftHeld => _mouse.LeftButton == ButtonState.Pressed;
    private bool LeftReleased => _mouse.LeftButton == ButtonState.Released;
    private Point MousePos => new(_mouse.X, _mouse.Y);

    private bool InRect(Rectangle r) => r.Contains(_mouse.X, _mouse.Y);

    // ═══════════════════════════════════════
    //  Update (called from Game1)
    // ═══════════════════════════════════════

    public void Update(int screenW, int screenH)
    {
        var mouse = Mouse.GetState();
        var kb = Keyboard.GetState();
        var gt = _lastGameTime ?? new GameTime(TimeSpan.Zero, TimeSpan.FromSeconds(1.0 / 60));
        UpdateInput(mouse, _prevMouseLocal, kb, _prevKb, screenW, screenH, gt);
        _textureBrowser.Update(mouse, _prevMouseLocal, kb, _prevKb);
        _prevMouseLocal = mouse;
        _prevKb = kb;
    }

    // ═══════════════════════════════════════
    //  Main Draw (called each frame)
    // ═══════════════════════════════════════

    /// <summary>Old-style draw compatible with existing Game1 code (no GameTime).</summary>
    public void Draw(int screenW, int screenH)
    {
        Draw(screenW, screenH, _lastGameTime ?? new GameTime(TimeSpan.Zero, TimeSpan.FromSeconds(1.0 / 60)));
    }

    public void Draw(int screenW, int screenH, GameTime gameTime)
    {
        _lastGameTime = gameTime;

        // Status timer (RI33: auto-clear _statusMsg when timer reaches 0)
        if (_statusTimer > 0)
        {
            _statusTimer -= (float)gameTime.ElapsedGameTime.TotalSeconds;
            if (_statusTimer <= 0)
            {
                _statusTimer = 0;
                _statusMsg = "";
            }
        }

        // Ctrl+S save (RI31: check IsTextInputActive to avoid triggering while typing)
        if (!IsTextInputActive && _kb.IsKeyDown(Keys.LeftControl) && _kb.IsKeyDown(Keys.S) && _prevKb.IsKeyUp(Keys.S))
        {
            SaveAll();
        }

        // Panel fills most of the screen
        int margin = 30;
        int panelX = margin;
        int panelY = margin;
        int panelW = screenW - margin * 2;
        int panelH = screenH - margin * 2;

        // Dark overlay
        DrawRect(new Rectangle(0, 0, screenW, screenH), new Color(0, 0, 0, 180));

        // Panel background
        DrawRect(new Rectangle(panelX, panelY, panelW, panelH), PanelBg);
        DrawBorder(new Rectangle(panelX, panelY, panelW, panelH), PanelBorder, 2);

        // ── Tab bar ──
        int tabBarH = 32;
        DrawRect(new Rectangle(panelX, panelY, panelW, tabBarH), PanelHeader);
        DrawRect(new Rectangle(panelX, panelY + tabBarH, panelW, 1), PanelBorder);

        string[] tabNames = { "9-Slices", "Elements", "Widgets" };
        int tabW = 120;
        for (int i = 0; i < 3; i++)
        {
            var tab = (UIEditorTab)i;
            int tx = panelX + 8 + i * (tabW + 4);
            var tabRect = new Rectangle(tx, panelY + 3, tabW, tabBarH - 4);
            bool active = ActiveTab == tab;
            bool hovered = InRect(tabRect);

            Color bg = active ? new Color(60, 60, 90, 255) : hovered ? new Color(50, 50, 70, 255) : new Color(38, 38, 50, 255);
            DrawRect(tabRect, bg);
            if (active)
                DrawRect(new Rectangle(tx + 4, panelY + tabBarH - 3, tabW - 8, 2), AccentColor);

            var textSize = MeasureText(tabNames[i]);
            DrawText(tabNames[i],
                new Vector2(tx + (tabW - textSize.X) / 2, panelY + 3 + (tabBarH - 4 - textSize.Y) / 2),
                active ? TextBright : TextColor);

            if (hovered && LeftJustPressed)
            {
                ActiveTab = tab;
                SelectedIndex = -1;
                _selectedChildIdx = -1;
                _selectedChildPath.Clear();
                _expandedPaths.Clear();
                _childDragActive = false;
                _nsDetailScroll = 0;
                _elemDetailScroll = 0;
                _widgetDetailScroll = 0;
                // Close any open add-child DrawCombo
                if (_activeFieldId == AddElemComboId || _activeFieldId == AddWgtComboId)
                    _activeFieldId = null;
            }
        }

        // Save button
        int saveBtnW = 80;
        int saveBtnH = 26;
        int saveBtnX = panelX + panelW - saveBtnW - 12;
        int saveBtnY = panelY + 3;
        if (DrawButton("Save", saveBtnX, saveBtnY, saveBtnW, saveBtnH))
            SaveAll();

        // Unsaved indicator
        if (_unsavedChanges)
            DrawText("*", new Vector2(saveBtnX - 14, saveBtnY + 4), new Color(255, 200, 100, 255));

        // Status message
        if (_statusTimer > 0 && !string.IsNullOrEmpty(_statusMsg))
        {
            var msgSize = MeasureText(_statusMsg);
            DrawText(_statusMsg, new Vector2(saveBtnX - msgSize.X - 20, saveBtnY + 5), SuccessColor);
        }

        if (!_loaded)
        {
            DrawText("No UI definitions loaded.",
                new Vector2(panelX + 20, panelY + tabBarH + 20), TextDim);
            DrawText("Place definitions in assets/UI/definitions/",
                new Vector2(panelX + 20, panelY + tabBarH + 40), TextDim);
            return;
        }

        // Content area below tabs
        int contentX = panelX;
        int contentY = panelY + tabBarH + 1;
        int contentW = panelW;
        int contentH = panelH - tabBarH - 1;

        switch (ActiveTab)
        {
            case UIEditorTab.NineSlices:
                DrawNineSliceTab(contentX, contentY, contentW, contentH);
                break;
            case UIEditorTab.Elements:
                DrawElementTab(contentX, contentY, contentW, contentH);
                break;
            case UIEditorTab.Widgets:
                DrawWidgetTab(contentX, contentY, contentW, contentH);
                break;
        }

        // Draw color picker popup on top of everything
        DrawColorPickerPopup();

        // Draw texture file browser popup on top of everything
        _textureBrowser.Draw(this, screenW, screenH);

        // Dropdown overlays (drawn last, on top of everything)
        DrawDropdownOverlays();
    }

    private void SaveAll()
    {
        try
        {
            // RI42: Save ALL tabs, not just the active one
            SaveNineSlices();
            SaveElements();
            SaveWidgets();
            _unsavedChanges = false;
            _statusMsg = "Saved all tabs";
            _statusTimer = 3f;

            // Also copy to source tree so dotnet publish picks up the latest
            string srcDefsDir = Path.Combine("..", _defsDir);
            if (Directory.Exists(srcDefsDir))
            {
                try
                {
                    foreach (var file in new[] { "nine_slices.json", "elements.json", "widgets.json" })
                    {
                        string src = Path.Combine(_defsDir, file);
                        if (File.Exists(src))
                            File.Copy(src, Path.Combine(srcDefsDir, file), true);
                    }
                }
                catch { /* source tree copy is best-effort */ }
            }
        }
        catch (Exception ex)
        {
            _statusMsg = $"Error: {ex.Message}";
            _statusTimer = 5f;
        }
    }

    public bool IsMouseOverPanel(int screenW, int screenH)
    {
        int margin = 30;
        var r = new Rectangle(margin, margin, screenW - margin * 2, screenH - margin * 2);
        var mouse = Mouse.GetState();
        return r.Contains(mouse.X, mouse.Y);
    }

    // ═══════════════════════════════════════════════
    //  TAB 1: Nine-Slices
    // ═══════════════════════════════════════════════

    private void DrawNineSliceTab(int x, int y, int w, int h)
    {
        int listW = 240;

        // Vertical divider
        DrawRect(new Rectangle(x + listW, y, 1, h), PanelBorder);

        // Left: list + buttons
        DrawNineSliceList(x, y, listW, h);

        // Right: properties + texture display + preview
        DrawNineSliceDetail(x + listW + 1, y, w - listW - 1, h);
    }

    private void DrawNineSliceList(int x, int y, int w, int h)
    {
        int btnH = 24;
        int btnAreaH = btnH + 8;
        int listH = h - btnAreaH;

        var items = _nineSlices.Select(ns => ns.Id).ToList();
        int clicked = DrawScrollableList("ns_list", items, SelectedIndex, x + 4, y + 4, w - 8, listH - 8);
        if (clicked >= 0) SelectedIndex = clicked;

        // RI15: Draw 20x20 texture thumbnail previews in the list items
        {
            float scroll = GetScrollOffset("ns_list");
            int itemH = 22;
            int listX = x + 4;
            int listY = y + 4;
            int listW2 = w - 8;
            int listH2 = listH - 8;
            float drawY = listY - scroll;
            for (int i = 0; i < _nineSlices.Count; i++)
            {
                if (drawY + itemH < listY) { drawY += itemH; continue; }
                if (drawY >= listY + listH2) break;
                if (drawY >= listY && drawY + itemH <= listY + listH2)
                {
                    var tex = GetOrLoadTexture(_nineSlices[i].Texture);
                    if (tex != null)
                    {
                        int thumbSize = 18;
                        int thumbX = listX + listW2 - thumbSize - 8;
                        int thumbY = (int)drawY + (itemH - thumbSize) / 2;
                        _sb.Draw(tex, new Rectangle(thumbX, thumbY, thumbSize, thumbSize), Color.White);
                        DrawBorder(new Rectangle(thumbX, thumbY, thumbSize, thumbSize), new Color(60, 60, 80));
                    }
                }
                drawY += itemH;
            }
        }

        // Add / Delete buttons
        int btnY = y + h - btnAreaH + 2;
        int btnW = (w - 20) / 2;
        if (DrawButton("+ Add", x + 4, btnY, btnW, btnH))
        {
            _nineSlices.Add(new UIEditorNineSliceDef
            {
                Id = $"new_slice_{_nineSlices.Count}",
                BorderLeft = 10, BorderRight = 10, BorderTop = 10, BorderBottom = 10  // RI16: default borders to 10
            });
            SelectedIndex = _nineSlices.Count - 1;
            _unsavedChanges = true;
        }
        if (DrawButton("Delete", x + 8 + btnW, btnY, btnW, btnH, DangerColor))
        {
            if (SelectedIndex >= 0 && SelectedIndex < _nineSlices.Count)
            {
                var removedId = _nineSlices[SelectedIndex].Id;
                InvalidateNineSlice(removedId);
                _nineSlices.RemoveAt(SelectedIndex);
                if (SelectedIndex >= _nineSlices.Count)
                    SelectedIndex = _nineSlices.Count - 1;
                _unsavedChanges = true;
            }
        }
    }

    private void DrawNineSliceDetail(int x, int y, int w, int h)
    {
        if (SelectedIndex < 0 || SelectedIndex >= _nineSlices.Count)
        {
            DrawText("No nine-slice selected", new Vector2(x + 20, y + 20), TextDim);
            return;
        }

        // Handle scroll wheel
        var clipRect = new Rectangle(x, y, w, h);
        if (!IsColorPickerOpen && clipRect.Contains(_mouse.X, _mouse.Y))
        {
            int scrollDelta = _mouse.ScrollWheelValue - _prevMouse.ScrollWheelValue;
            if (scrollDelta != 0)
                _nsDetailScroll = Math.Max(0, _nsDetailScroll - scrollDelta * 0.3f);
        }

        BeginClip(clipRect);

        var def = _nineSlices[SelectedIndex];
        int propW = Math.Min(340, w);
        int curY = y + 8 - (int)_nsDetailScroll;
        int pad = 8;

        // ID
        string newId = DrawTextField("ns_id", "ID", def.Id, x + pad, curY, propW);
        if (newId != def.Id) { def.Id = newId; _unsavedChanges = true; InvalidateNineSlice(def.Id); }
        curY += 24;

        // Texture path + Browse button
        int browseBtnW = 55;
        string newTex = DrawTextField("ns_tex", "Texture", def.Texture, x + pad, curY, propW - browseBtnW - 4);
        if (newTex != def.Texture)
        {
            def.Texture = newTex;
            _unsavedChanges = true;
            InvalidateNineSlice(def.Id);
        }
        if (DrawButton("Browse", x + pad + propW - browseBtnW, curY, browseBtnW, 20))
        {
            _textureBrowser.Open("assets", def.Texture, path =>
            {
                def.Texture = path;
                _unsavedChanges = true;
                InvalidateNineSlice(def.Id);
            });
        }
        curY += 24;

        // Texture dimensions info
        var tex = GetOrLoadTexture(def.Texture);
        if (tex != null)
        {
            DrawText($"Size: {tex.Width}x{tex.Height}", new Vector2(x + pad, curY), TextDim);
        }
        curY += 20;

        // Border spinners
        DrawText("Borders:", new Vector2(x + pad, curY), TextColor);
        curY += 20;

        int newBL = DrawIntField("ns_bl", "Border Left", def.BorderLeft, x + pad, curY, propW);
        if (newBL != def.BorderLeft) { def.BorderLeft = Math.Max(0, newBL); _unsavedChanges = true; InvalidateNineSlice(def.Id); }
        curY += 24;
        int newBR = DrawIntField("ns_br", "Border Right", def.BorderRight, x + pad, curY, propW);
        if (newBR != def.BorderRight) { def.BorderRight = Math.Max(0, newBR); _unsavedChanges = true; InvalidateNineSlice(def.Id); }
        curY += 24;
        int newBT = DrawIntField("ns_bt", "Border Top", def.BorderTop, x + pad, curY, propW);
        if (newBT != def.BorderTop) { def.BorderTop = Math.Max(0, newBT); _unsavedChanges = true; InvalidateNineSlice(def.Id); }
        curY += 24;
        int newBB = DrawIntField("ns_bb", "Border Bottom", def.BorderBottom, x + pad, curY, propW);
        if (newBB != def.BorderBottom) { def.BorderBottom = Math.Max(0, newBB); _unsavedChanges = true; InvalidateNineSlice(def.Id); }
        curY += 24;

        // Tile edges
        bool newTile = DrawCheckbox("Tile Edges", def.TileEdges, x + pad, curY);
        if (newTile != def.TileEdges) { def.TileEdges = newTile; _unsavedChanges = true; InvalidateNineSlice(def.Id); }
        curY += 24;

        // Separator
        DrawRect(new Rectangle(x + pad, curY, propW, 1), PanelBorder);
        curY += 8;

        // Texture display with border lines
        if (tex != null)
        {
            float maxDispW = Math.Min(w - pad * 2, 380);
            float maxDispH = 280;
            DrawTextureWithBorders(def, tex, x + pad, curY, maxDispW, maxDispH);

            float scale = Math.Min(maxDispW / tex.Width, maxDispH / tex.Height);
            if (scale > 1f) scale = 1f;
            curY += (int)(tex.Height * scale) + 16;

            // Live preview at 3 sizes
            DrawText("Preview:", new Vector2(x + pad, curY), TextColor);
            curY += 20;
            DrawNineSlicePreview(def, x + pad, curY, w - pad * 2);
        }

        EndClip();
    }

    private void DrawTextureWithBorders(UIEditorNineSliceDef def, Texture2D tex,
        int x, int y, float maxW, float maxH)
    {
        float scale = Math.Min(maxW / tex.Width, maxH / tex.Height);
        if (scale > 1f) scale = 1f;
        float dispW = tex.Width * scale;
        float dispH = tex.Height * scale;

        // Checkerboard background
        int checkSize = 8;
        for (int cy = 0; cy < (int)dispH; cy += checkSize)
        {
            for (int cx = 0; cx < (int)dispW; cx += checkSize)
            {
                bool dark = ((cx / checkSize) + (cy / checkSize)) % 2 == 0;
                int cw = Math.Min(checkSize, (int)dispW - cx);
                int ch = Math.Min(checkSize, (int)dispH - cy);
                DrawRect(new Rectangle(x + cx, y + cy, cw, ch),
                    dark ? new Color(40, 40, 40, 255) : new Color(60, 60, 60, 255));
            }
        }

        // Draw texture
        _sb.Draw(tex,
            new Rectangle(x, y, (int)dispW, (int)dispH),
            new Rectangle(0, 0, tex.Width, tex.Height),
            Color.White);

        // Border line positions
        float lineLeft = x + def.BorderLeft * scale;
        float lineRight = x + dispW - def.BorderRight * scale;
        float lineTop = y + def.BorderTop * scale;
        float lineBottom = y + dispH - def.BorderBottom * scale;

        var redLine = new Color(255, 80, 80, 200);
        var blueLine = new Color(80, 120, 255, 200);

        // Left border line (vertical, red)
        DrawRect(new Rectangle((int)lineLeft, y, 2, (int)dispH), redLine);
        // Right border line (vertical, red)
        DrawRect(new Rectangle((int)lineRight, y, 2, (int)dispH), redLine);
        // Top border line (horizontal, blue)
        DrawRect(new Rectangle(x, (int)lineTop, (int)dispW, 2), blueLine);
        // Bottom border line (horizontal, blue)
        DrawRect(new Rectangle(x, (int)lineBottom, (int)dispW, 2), blueLine);

        // Value labels
        DrawText(def.BorderLeft.ToString(), new Vector2(lineLeft + 3, y + 2), redLine);
        DrawText(def.BorderRight.ToString(), new Vector2(lineRight + 3, y + 2), redLine);
        DrawText(def.BorderTop.ToString(), new Vector2(x + 2, lineTop + 3), blueLine);
        DrawText(def.BorderBottom.ToString(), new Vector2(x + 2, lineBottom + 3), blueLine);

        // Drag handling
        var texBounds = new Rectangle(x, y, (int)dispW, (int)dispH);
        float grabTol = 6f;
        var mouse = MousePos;

        if (_draggingBorder == -1 && LeftJustPressed && InRect(texBounds))
        {
            if (Math.Abs(mouse.X - lineLeft) < grabTol)
                _draggingBorder = 0;
            else if (Math.Abs(mouse.X - lineRight) < grabTol)
                _draggingBorder = 1;
            else if (Math.Abs(mouse.Y - lineTop) < grabTol)
                _draggingBorder = 2;
            else if (Math.Abs(mouse.Y - lineBottom) < grabTol)
                _draggingBorder = 3;
        }

        if (_draggingBorder >= 0 && LeftHeld)
        {
            int maxBorderW = tex.Width / 2;
            int maxBorderH = tex.Height / 2;
            switch (_draggingBorder)
            {
                case 0:
                    def.BorderLeft = Math.Clamp((int)((mouse.X - x) / scale), 0, maxBorderW);
                    _unsavedChanges = true;
                    InvalidateNineSlice(def.Id);
                    break;
                case 1:
                    def.BorderRight = Math.Clamp((int)((x + dispW - mouse.X) / scale), 0, maxBorderW);
                    _unsavedChanges = true;
                    InvalidateNineSlice(def.Id);
                    break;
                case 2:
                    def.BorderTop = Math.Clamp((int)((mouse.Y - y) / scale), 0, maxBorderH);
                    _unsavedChanges = true;
                    InvalidateNineSlice(def.Id);
                    break;
                case 3:
                    def.BorderBottom = Math.Clamp((int)((y + dispH - mouse.Y) / scale), 0, maxBorderH);
                    _unsavedChanges = true;
                    InvalidateNineSlice(def.Id);
                    break;
            }
        }

        if (LeftReleased)
            _draggingBorder = -1;
    }

    private void DrawNineSlicePreview(UIEditorNineSliceDef def, int x, int y, int availW)
    {
        var ns = GetOrLoadNineSlice(def.Id);
        if (ns == null)
        {
            DrawText("(Cannot load nine-slice for preview)", new Vector2(x, y), TextDim);
            return;
        }

        // 3 preview sizes
        int[][] sizes = { new[] { 80, 60 }, new[] { 200, 120 }, new[] { 350, 200 } };
        int curX = x;

        foreach (var sz in sizes)
        {
            int pw = sz[0];
            int ph = sz[1];
            if (curX + pw > x + availW) break;

            // Checkerboard background
            DrawRect(new Rectangle(curX, y, pw, ph), new Color(30, 30, 30, 255));
            ns.Draw(_sb, new Rectangle(curX, y, pw, ph));
            DrawBorder(new Rectangle(curX, y, pw, ph), new Color(80, 80, 100, 100));

            // Size label
            DrawText($"{pw}x{ph}", new Vector2(curX, y + ph + 2), TextDim);

            curX += pw + 12;
        }
    }

    // ═══════════════════════════════════════════════
    //  TAB 2: Elements
    // ═══════════════════════════════════════════════

    private void DrawElementTab(int x, int y, int w, int h)
    {
        int listW = 200;
        int propsW = 300;
        int canvasX = x + listW + propsW + 2;
        int canvasW = w - listW - propsW - 2;

        // Dividers
        DrawRect(new Rectangle(x + listW, y, 1, h), PanelBorder);
        DrawRect(new Rectangle(x + listW + propsW + 1, y, 1, h), PanelBorder);

        DrawElementList(x, y, listW, h);
        DrawElementDetail(x + listW + 1, y, propsW, h);
        DrawElementCanvas(canvasX, y, canvasW, h);
    }

    private void DrawElementList(int x, int y, int w, int h)
    {
        int btnH = 24;
        int btnAreaH = btnH + 8;
        int listH = h - btnAreaH;

        var items = new List<string>();
        for (int i = 0; i < _elements.Count; i++)
        {
            var el = _elements[i];
            string prefix = !string.IsNullOrEmpty(el.NineSlice) ? "[NS] " : "     ";
            items.Add($"{prefix}{el.Id}");
        }

        int clicked = DrawScrollableList("el_list", items, SelectedIndex, x + 4, y + 4, w - 8, listH - 8);
        if (clicked >= 0) SelectedIndex = clicked;

        int btnY = y + h - btnAreaH + 2;
        int btnW = (w - 20) / 2;
        if (DrawButton("+ Add", x + 4, btnY, btnW, btnH))
        {
            _elements.Add(new UIEditorElementDef { Id = $"new_element_{_elements.Count}" });
            SelectedIndex = _elements.Count - 1;
            _unsavedChanges = true;
        }
        if (DrawButton("Delete", x + 8 + btnW, btnY, btnW, btnH, DangerColor))
        {
            if (SelectedIndex >= 0 && SelectedIndex < _elements.Count)
            {
                _elements.RemoveAt(SelectedIndex);
                if (SelectedIndex >= _elements.Count)
                    SelectedIndex = _elements.Count - 1;
                _unsavedChanges = true;
            }
        }
    }

    private void DrawElementDetail(int x, int y, int w, int h)
    {
        if (SelectedIndex < 0 || SelectedIndex >= _elements.Count)
        {
            DrawText("No element selected", new Vector2(x + 12, y + 20), TextDim);
            return;
        }

        // Handle scroll wheel
        var clipRect = new Rectangle(x, y, w, h);
        if (!IsColorPickerOpen && clipRect.Contains(_mouse.X, _mouse.Y))
        {
            int scrollDelta = _mouse.ScrollWheelValue - _prevMouse.ScrollWheelValue;
            if (scrollDelta != 0)
                _elemDetailScroll = Math.Max(0, _elemDetailScroll - scrollDelta * 0.3f);
        }

        BeginClip(clipRect);

        var def = _elements[SelectedIndex];
        int curY = y + 8 - (int)_elemDetailScroll;
        int pad = 8;
        int propW = w - pad * 2;

        // ID
        string newId = DrawTextField("el_id", "ID", def.Id, x + pad, curY, propW);
        if (newId != def.Id) { def.Id = newId; _unsavedChanges = true; }
        curY += 24;

        // Type dropdown
        string[] types = { "nineSlice", "text", "image" };
        string newType = DrawCombo("el_type", "Type", def.Type, types, x + pad, curY, propW);
        if (newType != def.Type)
        {
            def.Type = newType;
            if (newType == "text" && def.TextRegion == null)
                def.TextRegion = new UIEditorTextRegion { W = def.Width, H = def.Height };
            _unsavedChanges = true;
        }
        curY += 24;

        // Nine-Slice picker (for nineSlice type)
        if (def.Type == "nineSlice")
        {
            var nsIds = new[] { "(none)" }.Concat(_nineSlices.Select(ns => ns.Id)).ToArray();
            string curNs = string.IsNullOrEmpty(def.NineSlice) ? "(none)" : def.NineSlice;
            string newNs = DrawCombo("el_ns", "Nine-Slice", curNs, nsIds, x + pad, curY, propW);
            if (newNs == "(none)") newNs = "";
            if (newNs != def.NineSlice) { def.NineSlice = newNs; _unsavedChanges = true; }
            curY += 24;
        }

        // Image path picker (for image type) — uses texture file browser
        if (def.Type == "image")
        {
            int browseBtnW = 60;
            string displayPath = string.IsNullOrEmpty(def.ImagePath) ? "(none)" : def.ImagePath;
            DrawTextField("el_img", "Image", displayPath, x + pad, curY, propW - browseBtnW - 4);
            if (DrawButton("Browse", x + pad + propW - browseBtnW, curY, browseBtnW, 20))
            {
                _textureBrowser.Open("assets/UI", def.ImagePath,
                    path => { def.ImagePath = path; _unsavedChanges = true; });
            }
            curY += 24;
        }

        // Size
        int newW = DrawIntField("el_w", "Width", def.Width, x + pad, curY, propW);
        if (newW != def.Width) { def.Width = Math.Max(16, newW); _unsavedChanges = true; }
        curY += 24;
        int newH = DrawIntField("el_h", "Height", def.Height, x + pad, curY, propW);
        if (newH != def.Height) { def.Height = Math.Max(16, newH); _unsavedChanges = true; }
        curY += 24;

        // Tint color -- color swatch
        {
            DrawText("Tint:", new Vector2(x + pad, curY + 2), TextDim);
            byte[] tint = def.TintColor ?? new byte[] { 255, 255, 255, 255 };
            var hdr = BytesToHdr(tint);
            if (DrawColorSwatch("el_tint", x + pad + 120, curY, 40, 18, ref hdr, hideIntensity: true))
                _unsavedChanges = true;
            var newTint = HdrToBytes(hdr);
            if (def.TintColor == null || !newTint.SequenceEqual(def.TintColor))
            {
                def.TintColor = newTint;
                _unsavedChanges = true;
            }

            // Harmonize button — per-pixel texture color shift
            int harmBtnX = x + pad + 170;
            if (DrawButton("Harmonize", harmBtnX, curY, 80, 18))
            {
                // Begin with a dummy color — the real work is per-pixel on Apply
                var colors = new[] { BytesToHdr(def.TintColor ?? new byte[] { 255, 255, 255, 255 }) };
                _harmonizer.Begin(colors);
                _harmonizerOpen = true;
                _harmonizerTarget = HarmonizerTarget.ElementTint;
            }
            curY += 24;
        }

        // Harmonizer panel for per-pixel element texture harmonization
        if (_harmonizerOpen && _harmonizer.Active && ActiveTab == UIEditorTab.Elements && _harmonizerTarget == HarmonizerTarget.ElementTint)
        {
            if (_harmonizer.DrawPanel(this, x, ref curY, propW + pad * 2))
            {
                // Per-pixel harmonize: create a color-shifted copy of the source texture
                if (_device != null)
                {
                    // Find the source texture for this element
                    Texture2D? sourceTex = null;
                    string texKey = "";
                    if (def.Type == "image" && !string.IsNullOrEmpty(def.ImagePath))
                    {
                        sourceTex = GetOrLoadTexture(def.ImagePath);
                        texKey = def.ImagePath;
                    }
                    else if (def.Type == "nineSlice" && !string.IsNullOrEmpty(def.NineSlice))
                    {
                        var nsDef = _nineSlices.FirstOrDefault(n => n.Id == def.NineSlice);
                        if (nsDef != null && !string.IsNullOrEmpty(nsDef.Texture))
                        {
                            sourceTex = GetOrLoadTexture(nsDef.Texture);
                            texKey = nsDef.Texture;
                        }
                    }

                    if (sourceTex != null && !string.IsNullOrEmpty(texKey))
                    {
                        var harmonized = _harmonizer.HarmonizeTexture(sourceTex, _device);
                        if (harmonized != null)
                        {
                            // Cache the harmonized texture for preview
                            if (_harmonizedTextures.TryGetValue(texKey, out var old) && old != sourceTex)
                                old.Dispose();
                            _harmonizedTextures[texKey] = harmonized;

                            // If it's a nine-slice, rebuild the instance with the harmonized texture
                            if (def.Type == "nineSlice" && !string.IsNullOrEmpty(def.NineSlice))
                            {
                                var nsDef = _nineSlices.FirstOrDefault(n => n.Id == def.NineSlice);
                                if (nsDef != null)
                                {
                                    var nsInst = new NineSlice();
                                    nsInst.LoadFromTexture(harmonized, nsDef.BorderLeft, nsDef.BorderRight,
                                        nsDef.BorderTop, nsDef.BorderBottom, nsDef.TileEdges);
                                    _harmonizedNineSlices[def.NineSlice] = nsInst;
                                }
                            }
                            _unsavedChanges = true;
                        }
                    }
                }
            }
        }

        // Stroke/outline
        {
            DrawText("-- Stroke --", new Vector2(x + pad, curY + 2), AccentColor);
            curY += 20;

            int newThick = DrawIntField("el_stroke_t", "Thickness", def.StrokeThickness, x + pad, curY, propW);
            if (newThick != def.StrokeThickness) { def.StrokeThickness = Math.Max(0, newThick); _unsavedChanges = true; }
            curY += 24;

            if (def.StrokeThickness > 0)
            {
                // Stroke color
                DrawText("Color:", new Vector2(x + pad, curY + 2), TextDim);
                var strokeHdr = BytesToHdr(def.StrokeColor);
                if (DrawColorSwatch("el_stroke_c", x + pad + 120, curY, 40, 18, ref strokeHdr, hideIntensity: true))
                    _unsavedChanges = true;
                var newStroke = HdrToBytes(strokeHdr);
                if (!newStroke.SequenceEqual(def.StrokeColor))
                {
                    def.StrokeColor = newStroke;
                    _unsavedChanges = true;
                }
                curY += 24;

                // Stroke mode
                string[] modes = { "inside", "outside", "center" };
                string newMode = DrawCombo("el_stroke_m", "Mode", def.StrokeMode, modes, x + pad, curY, propW);
                if (newMode != def.StrokeMode) { def.StrokeMode = newMode; _unsavedChanges = true; }
                curY += 24;
            }
        }

        // Text Region (RI18: only show for "text" type, not any element with TextRegion != null)
        if (def.Type == "text")
        {
            DrawRect(new Rectangle(x + pad, curY, propW, 1), PanelBorder);
            curY += 6;
            DrawText("Text Region:", new Vector2(x + pad, curY), TextColor);
            curY += 20;

            if (def.TextRegion == null)
                def.TextRegion = new UIEditorTextRegion { W = def.Width, H = def.Height };

            var tr = def.TextRegion;
            int trX = DrawIntField("el_trx", "Rect X", tr.X, x + pad, curY, propW);
            if (trX != tr.X) { tr.X = trX; _unsavedChanges = true; } curY += 22;

            int trY2 = DrawIntField("el_try", "Rect Y", tr.Y, x + pad, curY, propW);
            if (trY2 != tr.Y) { tr.Y = trY2; _unsavedChanges = true; } curY += 22;

            int trW = DrawIntField("el_trw", "Rect W", tr.W, x + pad, curY, propW);
            if (trW != tr.W) { tr.W = Math.Max(0, trW); _unsavedChanges = true; } curY += 22;

            int trH = DrawIntField("el_trh", "Rect H", tr.H, x + pad, curY, propW);
            if (trH != tr.H) { tr.H = Math.Max(0, trH); _unsavedChanges = true; } curY += 22;

            string[] aligns = { "left", "center", "right" };
            string newAlign = DrawCombo("el_align", "Align", tr.Align, aligns, x + pad, curY, propW);
            if (newAlign != tr.Align) { tr.Align = newAlign; _unsavedChanges = true; }
            curY += 22;

            string[] valigns = { "top", "center", "bottom" };
            string newVAlign = DrawCombo("el_valign", "VAlign", tr.VAlign, valigns, x + pad, curY, propW);
            if (newVAlign != tr.VAlign) { tr.VAlign = newVAlign; _unsavedChanges = true; }
            curY += 22;

            int newFS = DrawIntField("el_fs", "Font Size", tr.FontSize, x + pad, curY, propW);
            if (newFS != tr.FontSize) { tr.FontSize = Math.Max(1, newFS); _unsavedChanges = true; }
            curY += 22;

            // Font color -- color swatch
            {
                DrawText("Font Color:", new Vector2(x + pad, curY + 2), TextDim);
                var fcHdr = BytesToHdr(tr.FontColor);
                if (DrawColorSwatch("el_fc", x + pad + 120, curY, 40, 18, ref fcHdr, hideIntensity: true))
                    _unsavedChanges = true;
                tr.FontColor = HdrToBytes(fcHdr);
                curY += 24;
            }
        }

        // Track content height for scroll clamping
        int contentBottom = curY + (int)_elemDetailScroll - y;
        _elemDetailScroll = Math.Min(_elemDetailScroll, Math.Max(0, contentBottom - h));

        EndClip();
    }

    private void DrawElementCanvas(int x, int y, int w, int h)
    {
        // Background
        DrawRect(new Rectangle(x, y, w, h), new Color(20, 20, 30, 255));

        if (SelectedIndex < 0 || SelectedIndex >= _elements.Count) return;
        var def = _elements[SelectedIndex];

        // Center the element in the canvas
        int elX = x + (w - def.Width) / 2;
        int elY = y + (h - def.Height) / 2;
        var elRect = new Rectangle(elX, elY, def.Width, def.Height);

        // Checkerboard background behind the element
        {
            int checkSz = 10;
            for (int cy = 0; cy < def.Height; cy += checkSz)
            {
                for (int cx = 0; cx < def.Width; cx += checkSz)
                {
                    bool dark = ((cx / checkSz) + (cy / checkSz)) % 2 == 0;
                    int cw = Math.Min(checkSz, def.Width - cx);
                    int ch2 = Math.Min(checkSz, def.Height - cy);
                    DrawRect(new Rectangle(elX + cx, elY + cy, cw, ch2),
                        dark ? new Color(40, 40, 40, 255) : new Color(60, 60, 60, 255));
                }
            }
        }

        // Draw element background (nine-slice, image, or plain)
        // Use harmonized texture if available, otherwise original with tint
        if (def.Type == "image" && !string.IsNullOrEmpty(def.ImagePath))
        {
            // Check for harmonized copy first
            var imgTex = _harmonizedTextures.TryGetValue(def.ImagePath, out var harmImg) ? harmImg
                : GetOrLoadTexture(def.ImagePath);
            if (imgTex != null)
            {
                var tint = ByteColor(def.TintColor ?? new byte[] { 255, 255, 255, 255 });
                _sb.Draw(imgTex, elRect, tint);
            }
            else
            {
                DrawRect(elRect, ByteColor(def.TintColor ?? new byte[] { 60, 60, 80, 200 }));
            }
        }
        else
        {
            // Check for harmonized nine-slice first
            NineSlice? ns = null;
            if (!string.IsNullOrEmpty(def.NineSlice))
                ns = _harmonizedNineSlices.TryGetValue(def.NineSlice, out var harmNs) ? harmNs
                    : GetOrLoadNineSlice(def.NineSlice);
            if (ns != null)
            {
                ns.Draw(_sb, elRect, ByteColor(def.TintColor ?? new byte[] { 255, 255, 255, 255 }));
            }
            else
            {
                DrawRect(elRect, ByteColor(def.TintColor ?? new byte[] { 60, 60, 80, 200 }));
            }
        }

        // Stroke/outline (user-defined, rendered before editor outline)
        if (def.StrokeThickness > 0)
        {
            var strokeCol = ByteColor(def.StrokeColor);
            int t = def.StrokeThickness;
            Rectangle strokeRect;
            if (def.StrokeMode == "outside")
                strokeRect = new Rectangle(elX - t, elY - t, def.Width + t * 2, def.Height + t * 2);
            else if (def.StrokeMode == "center")
                strokeRect = new Rectangle(elX - t / 2, elY - t / 2, def.Width + t, def.Height + t);
            else // "inside"
                strokeRect = elRect;

            // Draw stroke as 4 rectangles (top, bottom, left, right)
            int sx = strokeRect.X, sy = strokeRect.Y, sw = strokeRect.Width, sh = strokeRect.Height;
            DrawRect(new Rectangle(sx, sy, sw, t), strokeCol);                  // top
            DrawRect(new Rectangle(sx, sy + sh - t, sw, t), strokeCol);         // bottom
            DrawRect(new Rectangle(sx, sy + t, t, sh - t * 2), strokeCol);      // left
            DrawRect(new Rectangle(sx + sw - t, sy + t, t, sh - t * 2), strokeCol); // right
        }

        // Editor element outline (selection indicator)
        DrawBorder(elRect, AccentColor, 2);

        // Resize handles (8 zones)
        int hs = 8;
        DrawResizeHandle(elX - hs / 2, elY - hs / 2, hs);
        DrawResizeHandle(elX + def.Width / 2 - hs / 2, elY - hs / 2, hs);
        DrawResizeHandle(elX + def.Width - hs / 2, elY - hs / 2, hs);
        DrawResizeHandle(elX - hs / 2, elY + def.Height / 2 - hs / 2, hs);
        DrawResizeHandle(elX + def.Width - hs / 2, elY + def.Height / 2 - hs / 2, hs);
        DrawResizeHandle(elX - hs / 2, elY + def.Height - hs / 2, hs);
        DrawResizeHandle(elX + def.Width / 2 - hs / 2, elY + def.Height - hs / 2, hs);
        DrawResizeHandle(elX + def.Width - hs / 2, elY + def.Height - hs / 2, hs);

        // Text region overlay
        if (def.TextRegion != null)
        {
            var tr = def.TextRegion;
            int trDrawW = tr.W > 0 ? tr.W : def.Width;
            int trDrawH = tr.H > 0 ? tr.H : def.Height;
            var textRect = new Rectangle(elX + tr.X, elY + tr.Y, trDrawW, trDrawH);
            DrawRect(textRect, new Color(100, 200, 100, 30));
            DrawBorder(textRect, new Color(100, 200, 100, 150));
            DrawText("Sample Text", new Vector2(textRect.X + 4, textRect.Y + 4),
                ByteColor(tr.FontColor, 200));
        }

        // Drag logic for element resize
        var canvasRect = new Rectangle(x, y, w, h);
        var mouse = MousePos;

        if (_dragMode == CanvasDragMode.None && LeftJustPressed && InRect(canvasRect))
        {
            _dragTarget = CanvasDragTarget.Element;
            _dragStart = mouse;
            _dragOrigW = def.Width;
            _dragOrigH = def.Height;

            if (HitHandle(mouse, elX - hs / 2, elY - hs / 2, hs))
                _dragMode = CanvasDragMode.CornerTL;
            else if (HitHandle(mouse, elX + def.Width - hs / 2, elY - hs / 2, hs))
                _dragMode = CanvasDragMode.CornerTR;
            else if (HitHandle(mouse, elX - hs / 2, elY + def.Height - hs / 2, hs))
                _dragMode = CanvasDragMode.CornerBL;
            else if (HitHandle(mouse, elX + def.Width - hs / 2, elY + def.Height - hs / 2, hs))
                _dragMode = CanvasDragMode.CornerBR;
            else if (HitHandle(mouse, elX + def.Width / 2 - hs / 2, elY - hs / 2, hs))
                _dragMode = CanvasDragMode.EdgeT;
            else if (HitHandle(mouse, elX + def.Width / 2 - hs / 2, elY + def.Height - hs / 2, hs))
                _dragMode = CanvasDragMode.EdgeB;
            else if (HitHandle(mouse, elX - hs / 2, elY + def.Height / 2 - hs / 2, hs))
                _dragMode = CanvasDragMode.EdgeL;
            else if (HitHandle(mouse, elX + def.Width - hs / 2, elY + def.Height / 2 - hs / 2, hs))
                _dragMode = CanvasDragMode.EdgeR;
            else if (def.TextRegion != null)
            {
                var tr = def.TextRegion;
                int trDrawW = tr.W > 0 ? tr.W : def.Width;
                int trDrawH = tr.H > 0 ? tr.H : def.Height;
                var textRect = new Rectangle(elX + tr.X, elY + tr.Y, trDrawW, trDrawH);
                if (textRect.Contains(mouse))
                {
                    _dragMode = CanvasDragMode.Move;
                    _dragTarget = CanvasDragTarget.TextRegion;
                    _dragOrigX = tr.X;
                    _dragOrigY = tr.Y;
                }
            }
        }

        if (_dragMode != CanvasDragMode.None && LeftHeld)
        {
            int dx = mouse.X - _dragStart.X;
            int dy = mouse.Y - _dragStart.Y;

            if (_dragTarget == CanvasDragTarget.Element)
            {
                switch (_dragMode)
                {
                    case CanvasDragMode.EdgeR: def.Width = Math.Max(16, _dragOrigW + dx); break;
                    case CanvasDragMode.EdgeB: def.Height = Math.Max(16, _dragOrigH + dy); break;
                    case CanvasDragMode.EdgeL: def.Width = Math.Max(16, _dragOrigW - dx); break;
                    case CanvasDragMode.EdgeT: def.Height = Math.Max(16, _dragOrigH - dy); break;
                    case CanvasDragMode.CornerBR:
                        def.Width = Math.Max(16, _dragOrigW + dx);
                        def.Height = Math.Max(16, _dragOrigH + dy);
                        break;
                    case CanvasDragMode.CornerTL:
                        def.Width = Math.Max(16, _dragOrigW - dx);
                        def.Height = Math.Max(16, _dragOrigH - dy);
                        break;
                    case CanvasDragMode.CornerTR:
                        def.Width = Math.Max(16, _dragOrigW + dx);
                        def.Height = Math.Max(16, _dragOrigH - dy);
                        break;
                    case CanvasDragMode.CornerBL:
                        def.Width = Math.Max(16, _dragOrigW - dx);
                        def.Height = Math.Max(16, _dragOrigH + dy);
                        break;
                }
                _unsavedChanges = true;
            }
            else if (_dragTarget == CanvasDragTarget.TextRegion && def.TextRegion != null)
            {
                def.TextRegion.X = _dragOrigX + dx;
                def.TextRegion.Y = _dragOrigY + dy;
                _unsavedChanges = true;
            }
        }

        if (LeftReleased)
            _dragMode = CanvasDragMode.None;

        // Size label
        DrawText($"{def.Width} x {def.Height}", new Vector2(elX, elY + def.Height + 4), TextDim);
    }

    // ═══════════════════════════════════════════════
    //  TAB 3: Widgets
    // ═══════════════════════════════════════════════

    private void DrawWidgetTab(int x, int y, int w, int h)
    {
        int listW = 220;
        int propsW = 320;
        int canvasX = x + listW + propsW + 2;
        int canvasW = w - listW - propsW - 2;

        // Dividers
        DrawRect(new Rectangle(x + listW, y, 1, h), PanelBorder);
        DrawRect(new Rectangle(x + listW + propsW + 1, y, 1, h), PanelBorder);

        DrawWidgetList(x, y, listW, h);
        DrawWidgetDetail(x + listW + 1, y, propsW, h);
        DrawWidgetCanvas(canvasX, y, canvasW, h);
    }

    private void DrawWidgetList(int x, int y, int w, int h)
    {
        int btnH = 24;
        int btnAreaH = btnH + 8;
        int listH = h - btnAreaH;

        var items = _widgets.Select(wd => $"{wd.Id} ({wd.Children.Count}ch)").ToList();
        int clicked = DrawScrollableList("wd_list", items, SelectedIndex, x + 4, y + 4, w - 8, listH - 8);
        if (clicked >= 0) { SelectedIndex = clicked; _selectedChildIdx = -1; _selectedChildPath.Clear(); }

        int btnY = y + h - btnAreaH + 2;
        int btnW3 = (w - 24) / 3;

        if (DrawButton("+ Add", x + 4, btnY, btnW3, btnH))
        {
            _widgets.Add(new UIEditorWidgetDef { Id = $"new_widget_{_widgets.Count}" });
            SelectedIndex = _widgets.Count - 1;
            _selectedChildIdx = -1;
            _selectedChildPath.Clear();
            _unsavedChanges = true;
        }
        if (DrawButton("Copy", x + 8 + btnW3, btnY, btnW3, btnH))
        {
            if (SelectedIndex >= 0 && SelectedIndex < _widgets.Count)
            {
                var orig = _widgets[SelectedIndex];
                var copy = CloneWidget(orig);
                copy.Id = orig.Id + "_copy";
                _widgets.Add(copy);
                SelectedIndex = _widgets.Count - 1;
                _selectedChildIdx = -1;
                _selectedChildPath.Clear();
                _unsavedChanges = true;
            }
        }
        if (DrawButton("Delete", x + 12 + btnW3 * 2, btnY, btnW3, btnH, DangerColor))
        {
            if (SelectedIndex >= 0 && SelectedIndex < _widgets.Count)
            {
                _widgets.RemoveAt(SelectedIndex);
                if (SelectedIndex >= _widgets.Count)
                    SelectedIndex = _widgets.Count - 1;
                _selectedChildIdx = -1;
                _selectedChildPath.Clear();
                _unsavedChanges = true;
            }
        }
    }

    private void DrawWidgetDetail(int x, int y, int w, int h)
    {
        if (SelectedIndex < 0 || SelectedIndex >= _widgets.Count)
        {
            DrawText("No widget selected", new Vector2(x + 12, y + 20), TextDim);
            return;
        }

        // Handle scroll wheel
        var clipRect = new Rectangle(x, y, w, h);
        if (!IsColorPickerOpen && clipRect.Contains(_mouse.X, _mouse.Y))
        {
            int scrollDelta = _mouse.ScrollWheelValue - _prevMouse.ScrollWheelValue;
            if (scrollDelta != 0)
                _widgetDetailScroll = Math.Max(0, _widgetDetailScroll - scrollDelta * 0.3f);
        }

        BeginClip(clipRect);

        var def = _widgets[SelectedIndex];
        int curY = y + 8 - (int)_widgetDetailScroll;
        int pad = 8;
        int propW = w - pad * 2;

        // ID
        string newId = DrawTextField("wd_id", "ID", def.Id, x + pad, curY, propW);
        if (newId != def.Id) { def.Id = newId; _unsavedChanges = true; }
        curY += 24;

        // Background 9-slice dropdown
        var nsIds = new[] { "(none)" }.Concat(_nineSlices.Select(ns => ns.Id)).ToArray();
        string curBg = string.IsNullOrEmpty(def.Background) ? "(none)" : def.Background;
        string newBg = DrawCombo("wd_bg", "Background", curBg, nsIds, x + pad, curY, propW);
        if (newBg == "(none)") newBg = "";
        if (newBg != def.Background) { def.Background = newBg; _unsavedChanges = true; }
        curY += 24;

        // Size
        int newW = DrawIntField("wd_w", "Width", def.Width, x + pad, curY, propW);
        if (newW != def.Width) { def.Width = Math.Max(16, newW); _unsavedChanges = true; }
        curY += 24;

        int newH = DrawIntField("wd_h", "Height", def.Height, x + pad, curY, propW);
        if (newH != def.Height) { def.Height = Math.Max(16, newH); _unsavedChanges = true; }
        curY += 24;

        // BG Scale
        float newBgScale = DrawFloatField("wd_bgsc", "BG Scale", def.BackgroundScale, x + pad, curY, propW, 0.05f);
        if (Math.Abs(newBgScale - def.BackgroundScale) > 0.001f) { def.BackgroundScale = newBgScale; _unsavedChanges = true; }
        curY += 24;

        // Modal toggle
        bool newModal = DrawCheckbox("Modal", def.Modal, x + pad, curY);
        if (newModal != def.Modal) { def.Modal = newModal; _unsavedChanges = true; }
        curY += 24;

        // Layout mode
        string[] layoutModes = { "none", "horizontal", "vertical" };
        string newLayout = DrawCombo("wd_layout", "Layout", def.Layout, layoutModes, x + pad, curY, propW);
        if (newLayout != def.Layout) { def.Layout = newLayout; _unsavedChanges = true; }
        curY += 24;

        if (def.Layout != "none" && !string.IsNullOrEmpty(def.Layout))
        {
            // Uniform padding/spacing (legacy)
            int newPad = DrawIntField("wd_lpad", "Padding", def.LayoutPadding, x + pad, curY, propW);
            if (newPad != def.LayoutPadding) { def.LayoutPadding = Math.Max(0, newPad); _unsavedChanges = true; }
            curY += 22;

            int newSp = DrawIntField("wd_lsp", "Spacing", def.LayoutSpacing, x + pad, curY, propW);
            if (newSp != def.LayoutSpacing) { def.LayoutSpacing = Math.Max(0, newSp); _unsavedChanges = true; }
            curY += 22;

            // Separate padding per side
            DrawText("Per-Side Pad:", new Vector2(x + pad, curY), TextDim);
            curY += 18;
            int halfPW = propW / 2 - 4;

            int nPT = DrawIntField("wd_lpt", "Top", def.LayoutPadTop, x + pad, curY, halfPW);
            int nPB = DrawIntField("wd_lpb", "Bottom", def.LayoutPadBottom, x + pad + halfPW + 8, curY, halfPW);
            if (nPT != def.LayoutPadTop) { def.LayoutPadTop = Math.Max(0, nPT); _unsavedChanges = true; }
            if (nPB != def.LayoutPadBottom) { def.LayoutPadBottom = Math.Max(0, nPB); _unsavedChanges = true; }
            curY += 22;

            int nPL = DrawIntField("wd_lpl", "Left", def.LayoutPadLeft, x + pad, curY, halfPW);
            int nPR = DrawIntField("wd_lpr", "Right", def.LayoutPadRight, x + pad + halfPW + 8, curY, halfPW);
            if (nPL != def.LayoutPadLeft) { def.LayoutPadLeft = Math.Max(0, nPL); _unsavedChanges = true; }
            if (nPR != def.LayoutPadRight) { def.LayoutPadRight = Math.Max(0, nPR); _unsavedChanges = true; }
            curY += 22;

            // Separate spacing X/Y
            int nSX = DrawIntField("wd_lsx", "Spacing X", def.LayoutSpacingX, x + pad, curY, halfPW);
            int nSY = DrawIntField("wd_lsy", "Spacing Y", def.LayoutSpacingY, x + pad + halfPW + 8, curY, halfPW);
            if (nSX != def.LayoutSpacingX) { def.LayoutSpacingX = Math.Max(0, nSX); _unsavedChanges = true; }
            if (nSY != def.LayoutSpacingY) { def.LayoutSpacingY = Math.Max(0, nSY); _unsavedChanges = true; }
            curY += 22;
        }

        // Scroll mode
        string[] scrollModes = { "none", "vertical", "horizontal" };
        string newScroll = DrawCombo("wd_scroll", "Scroll", def.Scroll, scrollModes, x + pad, curY, propW);
        if (newScroll != def.Scroll) { def.Scroll = newScroll; _unsavedChanges = true; }
        curY += 24;

        if (def.Scroll != "none" && !string.IsNullOrEmpty(def.Scroll))
        {
            int newCW = DrawIntField("wd_scw", "Content W", def.ScrollContentW, x + pad, curY, propW);
            if (newCW != def.ScrollContentW) { def.ScrollContentW = newCW; _unsavedChanges = true; }
            curY += 22;

            int newCH = DrawIntField("wd_sch", "Content H", def.ScrollContentH, x + pad, curY, propW);
            if (newCH != def.ScrollContentH) { def.ScrollContentH = newCH; _unsavedChanges = true; }
            curY += 22;

            int newStep = DrawIntField("wd_sst", "Step", def.ScrollStep, x + pad, curY, propW);
            if (newStep != def.ScrollStep) { def.ScrollStep = Math.Max(1, newStep); _unsavedChanges = true; }
            curY += 22;
        }

        // RI20: Scrollbar properties (near scroll section)
        bool newIsSb = DrawCheckbox("Is Scrollbar", def.IsScrollbar, x + pad, curY);
        if (newIsSb != def.IsScrollbar) { def.IsScrollbar = newIsSb; _unsavedChanges = true; }
        curY += 22;

        if (def.IsScrollbar)
        {
            bool newSbProp = DrawCheckbox("Proportional", def.ScrollbarProportional, x + pad + 16, curY);
            if (newSbProp != def.ScrollbarProportional) { def.ScrollbarProportional = newSbProp; _unsavedChanges = true; }
            curY += 22;
        }

        // RI23: Background tint color (shown when widget has a background nine-slice)
        if (!string.IsNullOrEmpty(def.Background))
        {
            DrawText("BG Tint:", new Vector2(x + pad, curY + 2), TextDim);
            byte[] bgTint = def.BackgroundTint ?? new byte[] { 255, 255, 255, 255 };
            var bgHdr = BytesToHdr(bgTint);
            if (DrawColorSwatch("wd_bgtint", x + pad + 120, curY, 40, 18, ref bgHdr, hideIntensity: true))
                _unsavedChanges = true;
            var newBgTint = HdrToBytes(bgHdr);
            if (def.BackgroundTint == null || !newBgTint.SequenceEqual(def.BackgroundTint))
            {
                def.BackgroundTint = newBgTint;
                _unsavedChanges = true;
            }

            // Harmonize button for widget background tint (RI23)
            int harmBgBtnX = x + pad + 170;
            if (DrawButton("Harmonize", harmBgBtnX, curY, 80, 18))
            {
                var colors = new[] { BytesToHdr(def.BackgroundTint ?? new byte[] { 255, 255, 255, 255 }) };
                _harmonizer.Begin(colors);
                _harmonizerOpen = true;
                _harmonizerTarget = HarmonizerTarget.WidgetBgTint;
            }
            curY += 24;

            // Harmonizer panel for widget BG — per-pixel on background nine-slice
            if (_harmonizerOpen && _harmonizer.Active && ActiveTab == UIEditorTab.Widgets && _harmonizerTarget == HarmonizerTarget.WidgetBgTint)
            {
                if (_harmonizer.DrawPanel(this, x + pad, ref curY, propW))
                {
                    // Per-pixel harmonize the background nine-slice texture
                    if (_device != null && !string.IsNullOrEmpty(def.Background))
                    {
                        var nsDef = _nineSlices.FirstOrDefault(n => n.Id == def.Background);
                        if (nsDef != null && !string.IsNullOrEmpty(nsDef.Texture))
                        {
                            var sourceTex = GetOrLoadTexture(nsDef.Texture);
                            if (sourceTex != null)
                            {
                                var harmonized = _harmonizer.HarmonizeTexture(sourceTex, _device);
                                if (harmonized != null)
                                {
                                    if (_harmonizedTextures.TryGetValue(nsDef.Texture, out var old2) && old2 != sourceTex)
                                        old2.Dispose();
                                    _harmonizedTextures[nsDef.Texture] = harmonized;

                                    var nsInst = new NineSlice();
                                    nsInst.LoadFromTexture(harmonized, nsDef.BorderLeft, nsDef.BorderRight,
                                        nsDef.BorderTop, nsDef.BorderBottom, nsDef.TileEdges);
                                    _harmonizedNineSlices[def.Background] = nsInst;
                                    _unsavedChanges = true;
                                }
                            }
                        }
                    }
                }
            }
        }

        // Children section
        DrawRect(new Rectangle(x + pad, curY, propW, 1), PanelBorder);
        curY += 6;
        DrawText($"Children ({def.Children.Count}):", new Vector2(x + pad, curY), TextColor);
        curY += 20;

        // Keyboard shortcuts for clipboard (UI13) -- Ctrl+C / Ctrl+V
        if (!IsTextInputActive)
            HandleChildClipboardShortcuts(def);

        // RI27: Keyboard navigation in widget child tree
        if (!IsTextInputActive && def.Children.Count > 0)
            HandleChildTreeKeyboardNav(def);

        // Circular reference warning display (UI15)
        if (_circularRefWarningTimer > 0)
        {
            _circularRefWarningTimer -= 1f / 60f;
            DrawText(_circularRefWarning, new Vector2(x + pad, curY), DangerColor);
            curY += 18;
        }

        // Hierarchical child tree (UI11)
        int treeAreaH = Math.Min(200, 40 + CountTreeNodes(def.Children, def.Id) * 20);
        if (treeAreaH > 40)
        {
            var treeRect = new Rectangle(x + pad, curY, propW, treeAreaH);
            DrawRect(treeRect, new Color(20, 20, 35, 200));
            DrawBorder(treeRect, new Color(60, 60, 80, 200));

            int treeDrawY = curY + 2;
            DrawChildTree(def.Children, def.Id, x + pad, ref treeDrawY, propW, 0, new List<int>(), treeRect);

            // Draw drag-reorder insert indicator (UI12)
            if (_childDragActive && _childDragInsertIdx >= 0)
            {
                int indicatorY = curY + 2 + _childDragInsertIdx * 20;
                DrawRect(new Rectangle(x + pad + 2, indicatorY - 1, propW - 4, 2), AccentColor);
            }

            curY += treeAreaH + 4;
        }

        // Handle drag-reorder release (UI12)
        if (_childDragActive && LeftReleased)
        {
            if (_childDragSourceIdx >= 0 && _childDragInsertIdx >= 0 &&
                _childDragSourceIdx != _childDragInsertIdx &&
                _childDragSourceIdx != _childDragInsertIdx - 1 &&
                _childDragSourceIdx < def.Children.Count)
            {
                var moving = def.Children[_childDragSourceIdx];
                def.Children.RemoveAt(_childDragSourceIdx);
                int insertAt = _childDragInsertIdx > _childDragSourceIdx
                    ? _childDragInsertIdx - 1
                    : _childDragInsertIdx;
                insertAt = Math.Clamp(insertAt, 0, def.Children.Count);
                def.Children.Insert(insertAt, moving);
                _selectedChildIdx = insertAt;
                _selectedChildPath = new List<int> { insertAt };
                _unsavedChanges = true;
            }
            _childDragActive = false;
            _childDragSourceIdx = -1;
            _childDragInsertIdx = -1;
        }

        // Child buttons (Copy / Paste / Del) + DrawCombo pickers for add-child
        int childBtnW = (propW - 24) / 3;
        int childBtnH = 22;

        // Copy button (UI13)
        if (DrawButton("Copy", x + pad, curY, childBtnW, childBtnH))
        {
            CopySelectedChild(def);
        }

        // Paste button (UI13)
        if (DrawButton("Paste", x + pad + (childBtnW + 4), curY, childBtnW, childBtnH))
        {
            PasteChild(def);
        }

        if (DrawButton("Del", x + pad + (childBtnW + 4) * 2, curY, childBtnW, childBtnH, DangerColor))
        {
            if (_selectedChildIdx >= 0 && _selectedChildIdx < def.Children.Count)
            {
                def.Children.RemoveAt(_selectedChildIdx);
                if (_selectedChildIdx >= def.Children.Count)
                    _selectedChildIdx = def.Children.Count - 1;
                _selectedChildPath = _selectedChildIdx >= 0 ? new List<int> { _selectedChildIdx } : new List<int>();
                _unsavedChanges = true;
            }
        }
        curY += childBtnH + 4;

        // +Element DrawCombo picker (replaces old +Elem button + manual dropdown)
        if (_elements.Count > 0)
        {
            string[] elemNames = _elements.Select(e => e.Id).ToArray();
            string picked = DrawCombo("addchild_elem", "+Element", "(pick)", elemNames, x + pad, curY, propW);
            if (picked != "(pick)")
            {
                var newChild = new UIEditorChildDef { Name = $"child_{def.Children.Count}" };
                newChild.Element = picked;
                var refEl = _elements.FirstOrDefault(e => e.Id == picked);
                newChild.Width = refEl?.Width ?? 100;
                newChild.Height = refEl?.Height ?? 40;
                def.Children.Add(newChild);
                _selectedChildIdx = def.Children.Count - 1;
                _selectedChildPath = new List<int> { _selectedChildIdx };
                _unsavedChanges = true;
            }
            curY += 24;
        }

        // +Widget DrawCombo picker (replaces old +Wgt button + manual dropdown)
        if (_widgets.Count > 0)
        {
            string[] wgtNames = _widgets.Select(wd2 => wd2.Id).ToArray();
            string picked = DrawCombo("addchild_wgt", "+Widget", "(pick)", wgtNames, x + pad, curY, propW);
            if (picked != "(pick)")
            {
                // UI15: Circular reference guard
                if (WouldCreateCircularRef(def.Id, picked))
                {
                    _circularRefWarning = $"Circular ref: {def.Id} -> {picked}";
                    _circularRefWarningTimer = 3f;
                }
                else
                {
                    var newChild = new UIEditorChildDef { Name = $"child_{def.Children.Count}" };
                    newChild.Widget = picked;
                    var refWd = _widgets.FirstOrDefault(wd2 => wd2.Id == picked);
                    newChild.Width = refWd?.Width ?? 200;
                    newChild.Height = refWd?.Height ?? 100;
                    def.Children.Add(newChild);
                    _selectedChildIdx = def.Children.Count - 1;
                    _selectedChildPath = new List<int> { _selectedChildIdx };
                    _unsavedChanges = true;
                }
            }
            curY += 24;
        }

        curY += 2;

        // Selected child properties
        if (_selectedChildIdx >= 0 && _selectedChildIdx < def.Children.Count)
        {
            DrawChildProperties(def.Children[_selectedChildIdx], x + pad, curY, propW);
        }

        // Track content height for scroll clamping
        int contentBottom = curY + (int)_widgetDetailScroll - y + 200; // 200 is estimate for child props
        _widgetDetailScroll = Math.Min(_widgetDetailScroll, Math.Max(0, contentBottom - h));

        EndClip();
    }

    private void DrawChildProperties(UIEditorChildDef child, int x, int y, int propW)
    {
        int curY = y;

        DrawRect(new Rectangle(x, curY, propW, 1), new Color(80, 80, 120, 120));
        curY += 4;
        DrawText("Child Properties:", new Vector2(x, curY), AccentColor);
        curY += 20;

        // Name
        string newName = DrawTextField("ch_name", "Name", child.Name, x, curY, propW);
        if (newName != child.Name) { child.Name = newName; _unsavedChanges = true; }
        curY += 22;

        // Element ref dropdown
        var elemIds = new[] { "(none)" }.Concat(_elements.Select(e => e.Id)).ToArray();
        string curEl = string.IsNullOrEmpty(child.Element) ? "(none)" : child.Element;
        string newEl = DrawCombo("ch_elem", "Element", curEl, elemIds, x, curY, propW);
        if (newEl == "(none)") newEl = "";
        if (newEl != child.Element) { child.Element = newEl; _unsavedChanges = true; }
        curY += 22;

        // Widget ref dropdown (with UI15 circular reference guard)
        var widgetIds = new[] { "(none)" }.Concat(_widgets.Select(wd => wd.Id)).ToArray();
        string curWd = string.IsNullOrEmpty(child.Widget) ? "(none)" : child.Widget;
        string newWd = DrawCombo("ch_widget", "Widget", curWd, widgetIds, x, curY, propW);
        if (newWd == "(none)") newWd = "";
        if (newWd != child.Widget)
        {
            // UI15: Check for circular reference before assigning
            if (!string.IsNullOrEmpty(newWd) && SelectedIndex >= 0 && SelectedIndex < _widgets.Count)
            {
                string parentWidgetId = _widgets[SelectedIndex].Id;
                if (WouldCreateCircularRef(parentWidgetId, newWd))
                {
                    _circularRefWarning = $"Circular ref: {parentWidgetId} -> {newWd}";
                    _circularRefWarningTimer = 3f;
                }
                else
                {
                    child.Widget = newWd;
                    _unsavedChanges = true;
                }
            }
            else
            {
                child.Widget = newWd;
                _unsavedChanges = true;
            }
        }
        curY += 22;

        // Position
        int newCX = DrawIntField("ch_x", "X", child.X, x, curY, propW);
        if (newCX != child.X) { child.X = newCX; _unsavedChanges = true; } curY += 22;

        int newCY = DrawIntField("ch_y", "Y", child.Y, x, curY, propW);
        if (newCY != child.Y) { child.Y = newCY; _unsavedChanges = true; } curY += 22;

        // Size
        int newCW = DrawIntField("ch_w", "Width", child.Width, x, curY, propW);
        if (newCW != child.Width) { child.Width = Math.Max(0, newCW); _unsavedChanges = true; } curY += 22;

        int newCH = DrawIntField("ch_h", "Height", child.Height, x, curY, propW);
        if (newCH != child.Height) { child.Height = Math.Max(0, newCH); _unsavedChanges = true; } curY += 22;

        // Anchor (3x3 compass grid)
        DrawText("Anchor:", new Vector2(x, curY), TextDim);
        curY += 18;
        DrawAnchorGrid(child, x + 120, curY);
        curY += 62;

        // Stretch mode
        string[] stretches = { "", "horizontal", "vertical", "both" };
        string curStretch = child.Stretch ?? "";
        string newStretch = DrawCombo("ch_stretch", "Stretch", curStretch, stretches, x, curY, propW);
        if (newStretch != child.Stretch) { child.Stretch = newStretch; _unsavedChanges = true; }
        curY += 22;

        // IgnoreLayout checkbox
        bool newIgnore = DrawCheckbox("Ignore Layout", child.IgnoreLayout, x, curY);
        if (newIgnore != child.IgnoreLayout) { child.IgnoreLayout = newIgnore; _unsavedChanges = true; }
        curY += 22;

        // NineSliceScale
        float newNsScale = DrawFloatField("ch_nsscale", "9S Scale", child.NineSliceScale, x, curY, propW, 0.05f);
        if (Math.Abs(newNsScale - child.NineSliceScale) > 0.001f) { child.NineSliceScale = newNsScale; _unsavedChanges = true; }
        curY += 22;

        // Interactive toggle
        bool newInteract = DrawCheckbox("Interactive", child.Interactive, x, curY);
        if (newInteract != child.Interactive)
        {
            child.Interactive = newInteract;
            if (newInteract && child.Tints == null)
                child.Tints = new UIEditorTints();
            _unsavedChanges = true;
        }
        curY += 22;

        // State tints (if interactive) -- editable color swatches
        if (child.Interactive && child.Tints != null)
        {
            DrawText("State Tints:", new Vector2(x, curY), TextDim);

            // Harmonize button for all 4 tint colors (UI19)
            int harmTintBtnX = x + 120;
            if (DrawButton("Harmonize", harmTintBtnX, curY, 80, 16))
            {
                var tintColors = new[]
                {
                    BytesToHdr(child.Tints.Normal),
                    BytesToHdr(child.Tints.Hovered),
                    BytesToHdr(child.Tints.Pressed),
                    BytesToHdr(child.Tints.Disabled),
                };
                _harmonizer.Begin(tintColors);
                _harmonizerOpen = true;
                _harmonizerTarget = HarmonizerTarget.WidgetChildTints;
            }
            curY += 18;

            DrawEditableTintRow("ch_tint_n", "Normal", child.Tints.Normal, x, propW, ref curY);
            DrawEditableTintRow("ch_tint_h", "Hovered", child.Tints.Hovered, x, propW, ref curY);
            DrawEditableTintRow("ch_tint_p", "Pressed", child.Tints.Pressed, x, propW, ref curY);
            DrawEditableTintRow("ch_tint_d", "Disabled", child.Tints.Disabled, x, propW, ref curY);

            // Harmonizer panel for widget child tints (UI19)
            if (_harmonizerOpen && _harmonizer.Active && ActiveTab == UIEditorTab.Widgets && _harmonizerTarget == HarmonizerTarget.WidgetChildTints)
            {
                if (_harmonizer.DrawPanel(this, x, ref curY, propW))
                {
                    // Write harmonized results back to tint arrays
                    if (_harmonizer.NumColors >= 4)
                    {
                        WriteTintBytes(child.Tints.Normal, _harmonizer.Result[0]);
                        WriteTintBytes(child.Tints.Hovered, _harmonizer.Result[1]);
                        WriteTintBytes(child.Tints.Pressed, _harmonizer.Result[2]);
                        WriteTintBytes(child.Tints.Disabled, _harmonizer.Result[3]);
                        _unsavedChanges = true;
                    }
                }
            }
        }

        // Default text (wider text field)
        string newText = DrawTextField("ch_text", "Def. Text", child.DefaultText, x, curY, propW);
        if (newText != child.DefaultText) { child.DefaultText = newText; _unsavedChanges = true; }
        curY += 22;

        // RI21: Text Override per-child
        DrawRect(new Rectangle(x, curY, propW, 1), new Color(60, 60, 80, 120));
        curY += 4;
        bool newHasTxo = DrawCheckbox("Text Override", child.HasTextOverride, x, curY);
        if (newHasTxo != child.HasTextOverride)
        {
            child.HasTextOverride = newHasTxo;
            if (newHasTxo && child.TextOverride == null)
                child.TextOverride = new UIEditorTextRegion { W = child.Width, H = child.Height };
            _unsavedChanges = true;
        }
        curY += 22;

        if (child.HasTextOverride && child.TextOverride != null)
        {
            var txo = child.TextOverride;

            int txoFs = DrawIntField("ch_txo_fs", "FontSize", txo.FontSize, x + 8, curY, propW - 8);
            if (txoFs != txo.FontSize) { txo.FontSize = Math.Max(1, txoFs); _unsavedChanges = true; }
            curY += 22;

            string[] aligns = { "left", "center", "right" };
            string newAlign = DrawCombo("ch_txo_al", "Align", txo.Align, aligns, x + 8, curY, propW - 8);
            if (newAlign != txo.Align) { txo.Align = newAlign; _unsavedChanges = true; }
            curY += 22;

            string[] valigns = { "top", "center", "bottom" };
            string newVAlign = DrawCombo("ch_txo_va", "VAlign", txo.VAlign, valigns, x + 8, curY, propW - 8);
            if (newVAlign != txo.VAlign) { txo.VAlign = newVAlign; _unsavedChanges = true; }
            curY += 22;

            bool newWrap = DrawCheckbox("Word Wrap", txo.WordWrap, x + 8, curY);
            if (newWrap != txo.WordWrap) { txo.WordWrap = newWrap; _unsavedChanges = true; }
            curY += 22;

            // Font color swatch
            DrawText("Font Color:", new Vector2(x + 8, curY + 2), TextDim);
            var fcHdr = BytesToHdr(txo.FontColor);
            if (DrawColorSwatch("ch_txo_fc", x + 120, curY, 30, 16, ref fcHdr, hideIntensity: true))
                _unsavedChanges = true;
            var newFc = HdrToBytes(fcHdr);
            for (int bi = 0; bi < Math.Min(txo.FontColor.Length, newFc.Length); bi++)
            {
                if (txo.FontColor[bi] != newFc[bi])
                {
                    txo.FontColor[bi] = newFc[bi];
                    _unsavedChanges = true;
                }
            }
            curY += 22;

            // Text rect X/Y/W/H
            int halfPW2 = (propW - 8) / 2 - 4;
            int txoRX = DrawIntField("ch_txo_rx", "Rect X", txo.X, x + 8, curY, halfPW2);
            int txoRY = DrawIntField("ch_txo_ry", "Rect Y", txo.Y, x + 8 + halfPW2 + 8, curY, halfPW2);
            if (txoRX != txo.X) { txo.X = txoRX; _unsavedChanges = true; }
            if (txoRY != txo.Y) { txo.Y = txoRY; _unsavedChanges = true; }
            curY += 22;

            int txoRW = DrawIntField("ch_txo_rw", "Rect W", txo.W, x + 8, curY, halfPW2);
            int txoRH = DrawIntField("ch_txo_rh", "Rect H", txo.H, x + 8 + halfPW2 + 8, curY, halfPW2);
            if (txoRW != txo.W) { txo.W = txoRW; _unsavedChanges = true; }
            if (txoRH != txo.H) { txo.H = txoRH; _unsavedChanges = true; }
            curY += 22;
        }

        // RI22: Child Overrides (nested override system)
        if (!string.IsNullOrEmpty(child.Widget))
        {
            var refWidget = _widgets.FirstOrDefault(w => w.Id == child.Widget);
            if (refWidget != null && refWidget.Children.Count > 0)
            {
                DrawRect(new Rectangle(x, curY, propW, 1), new Color(60, 60, 80, 120));
                curY += 4;
                DrawText("Child Overrides:", new Vector2(x, curY), AccentColor);
                curY += 20;

                // Ensure we have an override list
                if (child.ChildOverrides == null)
                    child.ChildOverrides = new List<ChildOverrideEntry>();

                for (int ci = 0; ci < refWidget.Children.Count; ci++)
                {
                    var refChild = refWidget.Children[ci];

                    // Find or create the override entry for this child index
                    var entry = child.ChildOverrides.FirstOrDefault(e => e.ChildIndex == ci);
                    if (entry == null)
                    {
                        entry = new ChildOverrideEntry { ChildIndex = ci };
                        child.ChildOverrides.Add(entry);
                    }

                    string childLabel = !string.IsNullOrEmpty(refChild.Name) ? refChild.Name : $"child_{ci}";
                    bool hasAnyOverride = entry.OverrideX.HasValue || entry.OverrideY.HasValue ||
                                          entry.OverrideW.HasValue || entry.OverrideH.HasValue ||
                                          entry.OverrideDefaultText != null || entry.OverrideIgnoreLayout.HasValue;

                    // Green dot if any override is active
                    if (hasAnyOverride)
                        DrawRect(new Rectangle(x, curY + 3, 6, 6), new Color(80, 220, 80, 255));

                    DrawText($"  [{ci}] {childLabel}", new Vector2(x + 8, curY), hasAnyOverride ? TextBright : TextDim);

                    // Reset button
                    if (hasAnyOverride && DrawButton("Reset", x + propW - 48, curY, 44, 16, DangerColor))
                    {
                        entry.OverrideX = null;
                        entry.OverrideY = null;
                        entry.OverrideW = null;
                        entry.OverrideH = null;
                        entry.OverrideDefaultText = null;
                        entry.OverrideIgnoreLayout = null;
                        _unsavedChanges = true;
                    }
                    curY += 18;

                    // Override fields -- nullable int fields shown with green highlight when set
                    int oHalfW = (propW - 16) / 2 - 4;
                    string idPfx = $"co_{ci}";

                    // X / Y
                    DrawNullableIntOverride(idPfx + "_x", "Ovr X", entry.OverrideX, refChild.X,
                        x + 8, curY, oHalfW, v => { entry.OverrideX = v; _unsavedChanges = true; });
                    DrawNullableIntOverride(idPfx + "_y", "Ovr Y", entry.OverrideY, refChild.Y,
                        x + 8 + oHalfW + 8, curY, oHalfW, v => { entry.OverrideY = v; _unsavedChanges = true; });
                    curY += 20;

                    // W / H
                    DrawNullableIntOverride(idPfx + "_w", "Ovr W", entry.OverrideW, refChild.Width,
                        x + 8, curY, oHalfW, v => { entry.OverrideW = v; _unsavedChanges = true; });
                    DrawNullableIntOverride(idPfx + "_h", "Ovr H", entry.OverrideH, refChild.Height,
                        x + 8 + oHalfW + 8, curY, oHalfW, v => { entry.OverrideH = v; _unsavedChanges = true; });
                    curY += 20;

                    // Default text override
                    string curOvrText = entry.OverrideDefaultText ?? "";
                    string newOvrText = DrawTextField(idPfx + "_txt", "Ovr Text", curOvrText, x + 8, curY, propW - 16);
                    if (newOvrText != curOvrText)
                    {
                        entry.OverrideDefaultText = string.IsNullOrEmpty(newOvrText) ? null : newOvrText;
                        _unsavedChanges = true;
                    }
                    if (entry.OverrideDefaultText != null)
                        DrawRect(new Rectangle(x + 8, curY + 18, 4, 2), new Color(80, 220, 80, 255));
                    curY += 22;

                    // IgnoreLayout override
                    DrawText("Ovr IgnLayout:", new Vector2(x + 8, curY + 2), TextDim);
                    string[] ignOpts = { "(inherit)", "true", "false" };
                    string curIgn = entry.OverrideIgnoreLayout.HasValue
                        ? (entry.OverrideIgnoreLayout.Value ? "true" : "false") : "(inherit)";
                    string newIgn = DrawCombo(idPfx + "_ign", "", curIgn, ignOpts, x + 120, curY, propW - 128);
                    if (newIgn != curIgn)
                    {
                        entry.OverrideIgnoreLayout = newIgn switch
                        {
                            "true" => true,
                            "false" => false,
                            _ => null
                        };
                        _unsavedChanges = true;
                    }
                    if (entry.OverrideIgnoreLayout.HasValue)
                        DrawRect(new Rectangle(x + 8, curY + 3, 4, 12), new Color(80, 220, 80, 255));
                    curY += 22;

                    // Separator between children
                    if (ci < refWidget.Children.Count - 1)
                    {
                        DrawRect(new Rectangle(x + 8, curY, propW - 16, 1), new Color(50, 50, 70, 100));
                        curY += 4;
                    }
                }
            }
        }
    }

    /// <summary>Draw a nullable int override field. Shows inherited value dimmed when null, green when overridden.</summary>
    private void DrawNullableIntOverride(string fieldId, string label, int? current, int inherited,
        int x, int y, int w, Action<int?> setter)
    {
        bool isOverridden = current.HasValue;
        int displayVal = current ?? inherited;

        // Green indicator when overridden
        if (isOverridden)
            DrawRect(new Rectangle(x - 4, y + 3, 3, 12), new Color(80, 220, 80, 255));

        int newVal = DrawIntField(fieldId, label, displayVal, x, y, w);
        if (newVal != displayVal)
        {
            setter(newVal);
        }
    }

    private void DrawAnchorGrid(UIEditorChildDef child, int x, int y)
    {
        int cellSize = 18;
        int gap = 2;

        for (int row = 0; row < 3; row++)
        {
            for (int col = 0; col < 3; col++)
            {
                int idx = row * 3 + col;
                int cx = x + col * (cellSize + gap);
                int cy = y + row * (cellSize + gap);
                var cellRect = new Rectangle(cx, cy, cellSize, cellSize);
                bool selected = child.Anchor == idx;
                bool hovered = InRect(cellRect);

                Color bg = selected ? AccentColor : hovered ? ButtonHover : InputBg;
                DrawRect(cellRect, bg);
                DrawBorder(cellRect, InputBorder);

                if (selected)
                    DrawRect(new Rectangle(cx + 4, cy + 4, cellSize - 8, cellSize - 8), TextBright);

                if (hovered && LeftJustPressed)
                {
                    child.Anchor = idx;
                    _unsavedChanges = true;
                }
            }
        }
    }

    private void DrawTintRow(string label, byte[] color, int x, ref int curY)
    {
        DrawText($"  {label}:", new Vector2(x, curY + 2), TextDim);
        DrawRect(new Rectangle(x + 120, curY, 30, 16), ByteColor(color));
        DrawBorder(new Rectangle(x + 120, curY, 30, 16), InputBorder);

        byte a = color.Length > 3 ? color[3] : (byte)255;
        DrawText($"{color[0]},{color[1]},{color[2]},{a}", new Vector2(x + 156, curY + 2), TextDim);
        curY += 18;
    }

    private void DrawEditableTintRow(string id, string label, byte[] color, int x, int propW, ref int curY)
    {
        DrawText($"  {label}:", new Vector2(x, curY + 2), TextDim);
        var hdr = BytesToHdr(color);
        if (DrawColorSwatch(id, x + 120, curY, 30, 16, ref hdr, hideIntensity: true))
            _unsavedChanges = true;
        var newBytes = HdrToBytes(hdr);
        // Copy back into the same array so the reference in UIEditorTints stays valid
        for (int bi = 0; bi < Math.Min(color.Length, newBytes.Length); bi++)
        {
            if (color[bi] != newBytes[bi])
            {
                color[bi] = newBytes[bi];
                _unsavedChanges = true;
            }
        }
        byte a = color.Length > 3 ? color[3] : (byte)255;
        DrawText($"{color[0]},{color[1]},{color[2]},{a}", new Vector2(x + 156, curY + 2), TextDim);
        curY += 22;
    }

    private void DrawWidgetCanvas(int x, int y, int w, int h)
    {
        // Background
        DrawRect(new Rectangle(x, y, w, h), new Color(20, 20, 30, 255));

        if (SelectedIndex < 0 || SelectedIndex >= _widgets.Count) return;
        var def = _widgets[SelectedIndex];

        // Center widget in canvas
        int wdX = x + (w - def.Width) / 2;
        int wdY = y + (h - def.Height) / 2;
        var wdRect = new Rectangle(wdX, wdY, def.Width, def.Height);

        // Draw widget background (nine-slice)
        var bgNs = !string.IsNullOrEmpty(def.Background) ? GetOrLoadNineSlice(def.Background) : null;
        if (bgNs != null)
        {
            var bgColor = def.BackgroundTint != null ? ByteColor(def.BackgroundTint) : Color.White;
            bgNs.Draw(_sb, wdRect, bgColor, def.BackgroundScale);
        }
        else
        {
            DrawRect(wdRect, new Color(40, 40, 60, 200));
        }
        DrawBorder(wdRect, PanelBorder, 1);

        // Draw children
        for (int i = 0; i < def.Children.Count; i++)
        {
            var child = def.Children[i];
            var childRect = GetChildScreenRect(child, wdX, wdY, def.Width, def.Height);
            DrawWidgetChild(child, childRect, i == _selectedChildIdx);
        }

        // Widget outline
        DrawBorder(wdRect, AccentColor, 1);

        // Canvas drag handling
        var canvasRect = new Rectangle(x, y, w, h);
        var mouse = MousePos;

        if (_widgetDragMode == CanvasDragMode.None && LeftJustPressed && InRect(canvasRect))
        {
            _canvasDragChild = -1;

            // First check child resize handles (corners/edges)
            int chs = 6; // child handle size
            for (int i = def.Children.Count - 1; i >= 0; i--)
            {
                var childRect = GetChildScreenRect(def.Children[i], wdX, wdY, def.Width, def.Height);
                int crx = childRect.X, cry = childRect.Y, crw = childRect.Width, crh = childRect.Height;

                if (HitHandle(mouse, crx + crw - chs, cry + crh - chs, chs + 2))
                {
                    _selectedChildIdx = i;
                    _canvasDragChild = i;
                    _widgetDragMode = CanvasDragMode.CornerBR;
                    _widgetDragStart = mouse;
                    _wDragOrigX = def.Children[i].X;
                    _wDragOrigY = def.Children[i].Y;
                    _wDragOrigW = def.Children[i].Width;
                    _wDragOrigH = def.Children[i].Height;
                    break;
                }
                if (HitHandle(mouse, crx + crw - chs, cry + crh / 2 - chs / 2, chs + 2))
                {
                    _selectedChildIdx = i;
                    _canvasDragChild = i;
                    _widgetDragMode = CanvasDragMode.EdgeR;
                    _widgetDragStart = mouse;
                    _wDragOrigW = def.Children[i].Width;
                    _wDragOrigH = def.Children[i].Height;
                    break;
                }
                if (HitHandle(mouse, crx + crw / 2 - chs / 2, cry + crh - chs, chs + 2))
                {
                    _selectedChildIdx = i;
                    _canvasDragChild = i;
                    _widgetDragMode = CanvasDragMode.EdgeB;
                    _widgetDragStart = mouse;
                    _wDragOrigW = def.Children[i].Width;
                    _wDragOrigH = def.Children[i].Height;
                    break;
                }
            }

            // Then check child body for move
            if (_canvasDragChild < 0)
            {
                for (int i = def.Children.Count - 1; i >= 0; i--)
                {
                    var childRect = GetChildScreenRect(def.Children[i], wdX, wdY, def.Width, def.Height);
                    if (childRect.Contains(mouse))
                    {
                        _selectedChildIdx = i;
                        _canvasDragChild = i;
                        _widgetDragMode = CanvasDragMode.Move;
                        _widgetDragStart = mouse;
                        _wDragOrigX = def.Children[i].X;
                        _wDragOrigY = def.Children[i].Y;
                        _wDragOrigW = def.Children[i].Width;
                        _wDragOrigH = def.Children[i].Height;
                        break;
                    }
                }
            }

            if (_canvasDragChild < 0)
            {
                _canvasDragChild = -2;
                _widgetDragStart = mouse;
                _wDragOrigW = def.Width;
                _wDragOrigH = def.Height;

                int hsz = 8;
                if (HitHandle(mouse, wdX + def.Width - hsz / 2, wdY + def.Height - hsz / 2, hsz))
                    _widgetDragMode = CanvasDragMode.CornerBR;
                else if (HitHandle(mouse, wdX + def.Width - hsz / 2, wdY + def.Height / 2 - hsz / 2, hsz))
                    _widgetDragMode = CanvasDragMode.EdgeR;
                else if (HitHandle(mouse, wdX + def.Width / 2 - hsz / 2, wdY + def.Height - hsz / 2, hsz))
                    _widgetDragMode = CanvasDragMode.EdgeB;
                else
                    _widgetDragMode = CanvasDragMode.None;
            }
        }

        if (_widgetDragMode != CanvasDragMode.None && LeftHeld)
        {
            int dx = mouse.X - _widgetDragStart.X;
            int dy = mouse.Y - _widgetDragStart.Y;

            if (_canvasDragChild >= 0 && _canvasDragChild < def.Children.Count)
            {
                switch (_widgetDragMode)
                {
                    case CanvasDragMode.Move:
                        def.Children[_canvasDragChild].X = _wDragOrigX + dx;
                        def.Children[_canvasDragChild].Y = _wDragOrigY + dy;
                        break;
                    case CanvasDragMode.CornerBR:
                        def.Children[_canvasDragChild].Width = Math.Max(8, _wDragOrigW + dx);
                        def.Children[_canvasDragChild].Height = Math.Max(8, _wDragOrigH + dy);
                        break;
                    case CanvasDragMode.EdgeR:
                        def.Children[_canvasDragChild].Width = Math.Max(8, _wDragOrigW + dx);
                        break;
                    case CanvasDragMode.EdgeB:
                        def.Children[_canvasDragChild].Height = Math.Max(8, _wDragOrigH + dy);
                        break;
                }
                _unsavedChanges = true;
            }
            else if (_canvasDragChild == -2)
            {
                switch (_widgetDragMode)
                {
                    case CanvasDragMode.CornerBR:
                        def.Width = Math.Max(16, _wDragOrigW + dx);
                        def.Height = Math.Max(16, _wDragOrigH + dy);
                        break;
                    case CanvasDragMode.EdgeR:
                        def.Width = Math.Max(16, _wDragOrigW + dx);
                        break;
                    case CanvasDragMode.EdgeB:
                        def.Height = Math.Max(16, _wDragOrigH + dy);
                        break;
                }
                _unsavedChanges = true;
            }
        }

        if (LeftReleased)
            _widgetDragMode = CanvasDragMode.None;

        // Widget resize handles
        int handleSz = 8;
        DrawResizeHandle(wdX + def.Width - handleSz / 2, wdY + def.Height - handleSz / 2, handleSz);
        DrawResizeHandle(wdX + def.Width - handleSz / 2, wdY + def.Height / 2 - handleSz / 2, handleSz);
        DrawResizeHandle(wdX + def.Width / 2 - handleSz / 2, wdY + def.Height - handleSz / 2, handleSz);

        // Size label
        DrawText($"{def.Width} x {def.Height}", new Vector2(wdX, wdY + def.Height + 4), TextDim);
    }

    private static Rectangle GetChildScreenRect(UIEditorChildDef child, int wdX, int wdY, int wdW, int wdH)
    {
        int col = child.Anchor % 3;
        int row = child.Anchor / 3;
        int anchorX = col switch { 0 => 0, 1 => wdW / 2, 2 => wdW, _ => 0 };
        int anchorY = row switch { 0 => 0, 1 => wdH / 2, 2 => wdH, _ => 0 };

        int cw = child.Width > 0 ? child.Width : 100;
        int ch = child.Height > 0 ? child.Height : 40;

        return new Rectangle(wdX + anchorX + child.X, wdY + anchorY + child.Y, cw, ch);
    }

    private void DrawWidgetChild(UIEditorChildDef child, Rectangle rect, bool selected)
    {
        var elemDef = _elements.FirstOrDefault(e => e.Id == child.Element);
        bool drawn = false;

        if (elemDef != null)
        {
            byte[] tc = elemDef.TintColor ?? new byte[] { 255, 255, 255, 255 };
            var tint = ByteColor(tc);

            if (elemDef.Type == "image" && !string.IsNullOrEmpty(elemDef.ImagePath))
            {
                // Image element — check for harmonized copy first
                var imgTex = _harmonizedTextures.TryGetValue(elemDef.ImagePath, out var harmImg) ? harmImg
                    : GetOrLoadTexture(elemDef.ImagePath);
                if (imgTex != null)
                {
                    _sb.Draw(imgTex, rect, tint);
                    drawn = true;
                }
            }
            else if (!string.IsNullOrEmpty(elemDef.NineSlice))
            {
                // Nine-slice element — check for harmonized copy first
                NineSlice? childNs = _harmonizedNineSlices.TryGetValue(elemDef.NineSlice, out var harmNs) ? harmNs
                    : GetOrLoadNineSlice(elemDef.NineSlice);
                if (childNs != null)
                {
                    childNs.Draw(_sb, rect, tint);
                    drawn = true;
                }
            }

            // Stroke/outline
            if (elemDef.StrokeThickness > 0)
            {
                var strokeCol = ByteColor(elemDef.StrokeColor);
                int t = elemDef.StrokeThickness;
                Rectangle strokeRect;
                if (elemDef.StrokeMode == "outside")
                    strokeRect = new Rectangle(rect.X - t, rect.Y - t, rect.Width + t * 2, rect.Height + t * 2);
                else if (elemDef.StrokeMode == "center")
                    strokeRect = new Rectangle(rect.X - t / 2, rect.Y - t / 2, rect.Width + t, rect.Height + t);
                else // "inside"
                    strokeRect = rect;

                int sx = strokeRect.X, sy = strokeRect.Y, sw = strokeRect.Width, sh = strokeRect.Height;
                DrawRect(new Rectangle(sx, sy, sw, t), strokeCol);
                DrawRect(new Rectangle(sx, sy + sh - t, sw, t), strokeCol);
                DrawRect(new Rectangle(sx, sy + t, t, sh - t * 2), strokeCol);
                DrawRect(new Rectangle(sx + sw - t, sy + t, t, sh - t * 2), strokeCol);
            }
        }

        if (!drawn)
            DrawRect(rect, new Color(50, 50, 70, 180));

        // Label
        string label = !string.IsNullOrEmpty(child.Name) ? child.Name :
                        !string.IsNullOrEmpty(child.Element) ? child.Element : "child";
        DrawText(label, new Vector2(rect.X + 4, rect.Y + 4),
            selected ? TextBright : TextColor);

        // Default text
        if (!string.IsNullOrEmpty(child.DefaultText))
        {
            DrawText(child.DefaultText,
                new Vector2(rect.X + 4, rect.Y + rect.Height / 2 - 6),
                new Color(200, 200, 200, 180));
        }

        // Selection highlight
        if (selected)
        {
            DrawBorder(rect, new Color(255, 200, 80, 255), 2);

            int chs = 6;
            DrawResizeHandle(rect.X + rect.Width - chs, rect.Y + rect.Height - chs, chs);
            DrawResizeHandle(rect.X + rect.Width - chs, rect.Y + rect.Height / 2 - chs / 2, chs);
            DrawResizeHandle(rect.X + rect.Width / 2 - chs / 2, rect.Y + rect.Height - chs, chs);
        }
        else
        {
            DrawBorder(rect, new Color(100, 100, 140, 100));
        }
    }

    // ═══════════════════════════════════════
    //  Utility
    // ═══════════════════════════════════════

    private void DrawResizeHandle(int x, int y, int size)
    {
        var r = new Rectangle(x, y, size, size);
        bool hovered = InRect(r);
        DrawRect(r, hovered ? AccentColor : new Color(80, 80, 120, 255));
        DrawBorder(r, TextBright);
    }

    private static bool HitHandle(Point mouse, int hx, int hy, int size)
    {
        return mouse.X >= hx && mouse.X < hx + size &&
               mouse.Y >= hy && mouse.Y < hy + size;
    }

    /// <summary>Convert byte[] RGBA to Color, avoiding constructor ambiguity.</summary>
    private static Color ByteColor(byte[] c, byte alphaOverride = 0)
    {
        byte a = alphaOverride > 0 ? alphaOverride : (c.Length > 3 ? c[3] : (byte)255);
        return new Color((int)c[0], (int)c[1], (int)c[2], (int)a);
    }

    /// <summary>Convert byte[] RGBA to HdrColor (LDR, intensity=1).</summary>
    private static HdrColor BytesToHdr(byte[] c)
    {
        return new HdrColor(c[0], c[1], c[2], c.Length > 3 ? c[3] : (byte)255, 1f);
    }

    /// <summary>Convert HdrColor back to byte[] RGBA.</summary>
    private static byte[] HdrToBytes(HdrColor hdr)
    {
        return new[] { hdr.R, hdr.G, hdr.B, hdr.A };
    }

    /// <summary>Write HdrColor RGBA into an existing byte array (preserving the reference).</summary>
    private static void WriteTintBytes(byte[] target, HdrColor src)
    {
        if (target.Length >= 4)
        {
            target[0] = src.R;
            target[1] = src.G;
            target[2] = src.B;
            target[3] = src.A;
        }
    }

    // ═══════════════════════════════════════
    //  Hierarchical child tree (UI11)
    // ═══════════════════════════════════════

    /// <summary>Count total visible tree nodes for sizing the tree area.</summary>
    private int CountTreeNodes(List<UIEditorChildDef> children, string parentWidgetId)
    {
        int count = 0;
        for (int i = 0; i < children.Count; i++)
        {
            count++;
            string pathKey = GetPathKey(new List<int> { i }); // simplified for top-level counting
            if (!string.IsNullOrEmpty(children[i].Widget) && _expandedPaths.Contains(pathKey))
            {
                var subWidget = _widgets.FirstOrDefault(w => w.Id == children[i].Widget);
                if (subWidget != null)
                    count += subWidget.Children.Count; // one level of sub-children
            }
        }
        return count;
    }

    private static string GetPathKey(List<int> path)
    {
        return string.Join("/", path);
    }

    /// <summary>Draw the hierarchical child tree with expand/collapse, drag-reorder, and clipboard buttons.</summary>
    private void DrawChildTree(List<UIEditorChildDef> children, string parentWidgetId,
        int x, ref int curY, int w, int depth, List<int> currentPath, Rectangle clipRect)
    {
        int rowH = 20;
        int indent = 16;

        for (int i = 0; i < children.Count; i++)
        {
            var child = children[i];
            var path = new List<int>(currentPath) { i };
            string pathKey = GetPathKey(path);

            int drawX = x + 4 + depth * indent;
            int drawY = curY;
            int rowW = w - 4;

            // Check if this node has expandable sub-children (Widget reference)
            bool hasSubChildren = false;
            UIEditorWidgetDef? subWidget = null;
            if (!string.IsNullOrEmpty(child.Widget))
            {
                subWidget = _widgets.FirstOrDefault(wd => wd.Id == child.Widget);
                if (subWidget != null && subWidget.Children.Count > 0)
                    hasSubChildren = true;
            }

            bool isExpanded = _expandedPaths.Contains(pathKey);
            bool isSelected = depth == 0 && _selectedChildIdx == i;

            // Row background
            var rowRect = new Rectangle(x + 1, drawY, w - 2, rowH);
            if (rowRect.Bottom < clipRect.Top || rowRect.Top > clipRect.Bottom)
            {
                curY += rowH;
                if (hasSubChildren && isExpanded && subWidget != null)
                {
                    // Skip sub-children Y space too
                    curY += subWidget.Children.Count * rowH;
                }
                continue;
            }

            if (isSelected)
                DrawRect(rowRect, new Color(60, 60, 100, 200));
            else if (rowRect.Contains(_mouse.X, _mouse.Y))
                DrawRect(rowRect, ItemHover);

            // Expand/collapse arrow (UI11)
            if (hasSubChildren)
            {
                string arrow = isExpanded ? "\u25BE" : "\u25B8"; // ▾ or ▸
                var arrowRect = new Rectangle(drawX, drawY, 14, rowH);
                DrawText(arrow, new Vector2(drawX, drawY + 1), TextDim);
                if (arrowRect.Contains(_mouse.X, _mouse.Y) && LeftJustPressed)
                {
                    if (isExpanded)
                        _expandedPaths.Remove(pathKey);
                    else
                        _expandedPaths.Add(pathKey);
                }
                drawX += 14;
            }
            else
            {
                drawX += 14; // consistent indentation
            }

            // Child label
            string refStr = !string.IsNullOrEmpty(child.Element) ? child.Element :
                            !string.IsNullOrEmpty(child.Widget) ? $"[W]{child.Widget}" : "(empty)";
            string label = $"{child.Name} -> {refStr}";

            // Truncate label to fit
            int maxLabelW = w - (drawX - x) - 8;
            string displayLabel = label;
            var labelSize = MeasureText(displayLabel);
            while (labelSize.X > maxLabelW && displayLabel.Length > 3)
            {
                displayLabel = displayLabel[..^4] + "...";
                labelSize = MeasureText(displayLabel);
            }

            DrawText(displayLabel, new Vector2(drawX, drawY + 2),
                isSelected ? TextBright : TextColor);

            // RI29: Green dot indicator for children that have active overrides
            bool hasActiveOverrides = child.ChildOverrides != null && child.ChildOverrides.Count > 0 &&
                child.ChildOverrides.Exists(co =>
                    co.OverrideX.HasValue || co.OverrideY.HasValue ||
                    co.OverrideW.HasValue || co.OverrideH.HasValue ||
                    co.OverrideDefaultText != null || co.OverrideIgnoreLayout.HasValue);
            if (hasActiveOverrides)
            {
                int dotX = x + w - 12;
                int dotY = drawY + rowH / 2 - 3;
                DrawRect(new Rectangle(dotX, dotY, 6, 6), SuccessColor);
            }

            // Click to select (top-level children only for _selectedChildIdx compatibility)
            if (depth == 0 && rowRect.Contains(_mouse.X, _mouse.Y) && LeftJustPressed
                && !new Rectangle(x + 4 + depth * indent, drawY, 14, rowH).Contains(_mouse.X, _mouse.Y))
            {
                _selectedChildIdx = i;
                _selectedChildPath = path;

                // Start drag-reorder (UI12)
                _childDragActive = true;
                _childDragSourceIdx = i;
                _childDragStartY = _mouse.Y;
                _childDragInsertIdx = -1;
            }

            // Update drag insert position (UI12)
            if (_childDragActive && depth == 0 && rowRect.Contains(_mouse.X, _mouse.Y)
                && Math.Abs(_mouse.Y - _childDragStartY) > 4)
            {
                // Insert above or below based on mouse position relative to row center
                if (_mouse.Y < drawY + rowH / 2)
                    _childDragInsertIdx = i;
                else
                    _childDragInsertIdx = i + 1;
            }

            curY += rowH;

            // Draw expanded sub-children (UI11)
            if (hasSubChildren && isExpanded && subWidget != null)
            {
                for (int si = 0; si < subWidget.Children.Count; si++)
                {
                    var subChild = subWidget.Children[si];
                    int subDrawX = x + 4 + (depth + 1) * indent + 14;
                    int subDrawY = curY;

                    var subRowRect = new Rectangle(x + 1, subDrawY, w - 2, rowH);
                    if (subRowRect.Bottom >= clipRect.Top && subRowRect.Top <= clipRect.Bottom)
                    {
                        // Dim background for sub-items
                        DrawRect(subRowRect, new Color(30, 30, 50, 150));

                        string subRef = !string.IsNullOrEmpty(subChild.Element) ? subChild.Element :
                                        !string.IsNullOrEmpty(subChild.Widget) ? $"[W]{subChild.Widget}" : "(empty)";
                        string subLabel = $"{subChild.Name} -> {subRef}";
                        DrawText(subLabel, new Vector2(subDrawX, subDrawY + 2), TextDim);
                    }

                    curY += rowH;
                }
            }
        }
    }

    // ═══════════════════════════════════════
    //  Child clipboard (UI13)
    // ═══════════════════════════════════════

    // RI27: Keyboard navigation in widget child tree
    private void HandleChildTreeKeyboardNav(UIEditorWidgetDef def)
    {
        bool upPressed = _kb.IsKeyDown(Keys.Up) && _prevKb.IsKeyUp(Keys.Up);
        bool downPressed = _kb.IsKeyDown(Keys.Down) && _prevKb.IsKeyUp(Keys.Down);
        bool leftPressed = _kb.IsKeyDown(Keys.Left) && _prevKb.IsKeyUp(Keys.Left);
        bool rightPressed = _kb.IsKeyDown(Keys.Right) && _prevKb.IsKeyUp(Keys.Right);

        if (upPressed)
        {
            if (_selectedChildIdx > 0)
            {
                _selectedChildIdx--;
                _selectedChildPath = new List<int> { _selectedChildIdx };
            }
            else if (_selectedChildIdx < 0 && def.Children.Count > 0)
            {
                _selectedChildIdx = 0;
                _selectedChildPath = new List<int> { 0 };
            }
        }

        if (downPressed)
        {
            if (_selectedChildIdx < def.Children.Count - 1)
            {
                _selectedChildIdx++;
                _selectedChildPath = new List<int> { _selectedChildIdx };
            }
            else if (_selectedChildIdx < 0 && def.Children.Count > 0)
            {
                _selectedChildIdx = 0;
                _selectedChildPath = new List<int> { 0 };
            }
        }

        if (leftPressed && _selectedChildIdx >= 0 && _selectedChildIdx < def.Children.Count)
        {
            // Collapse: remove from expanded set
            string pathKey = _selectedChildIdx.ToString();
            _expandedPaths.Remove(pathKey);
        }

        if (rightPressed && _selectedChildIdx >= 0 && _selectedChildIdx < def.Children.Count)
        {
            // Expand: add to expanded set if this child has sub-children
            var child = def.Children[_selectedChildIdx];
            if (!string.IsNullOrEmpty(child.Widget))
            {
                var subWidget = _widgets.FirstOrDefault(wd => wd.Id == child.Widget);
                if (subWidget != null && subWidget.Children.Count > 0)
                {
                    string pathKey = _selectedChildIdx.ToString();
                    _expandedPaths.Add(pathKey);
                }
            }
        }
    }

    private void HandleChildClipboardShortcuts(UIEditorWidgetDef def)
    {
        bool ctrl = _kb.IsKeyDown(Keys.LeftControl) || _kb.IsKeyDown(Keys.RightControl);

        // Ctrl+C: copy selected child
        if (ctrl && _kb.IsKeyDown(Keys.C) && _prevKb.IsKeyUp(Keys.C))
        {
            CopySelectedChild(def);
        }

        // Ctrl+V: paste child
        if (ctrl && _kb.IsKeyDown(Keys.V) && _prevKb.IsKeyUp(Keys.V))
        {
            PasteChild(def);
        }
    }

    private void CopySelectedChild(UIEditorWidgetDef def)
    {
        if (_selectedChildIdx >= 0 && _selectedChildIdx < def.Children.Count)
        {
            _childClipboard = CloneChild(def.Children[_selectedChildIdx]);
            _statusMsg = "Child copied";
            _statusTimer = 1.5f;
        }
    }

    private void PasteChild(UIEditorWidgetDef def)
    {
        if (_childClipboard != null)
        {
            var pasted = CloneChild(_childClipboard);
            pasted.Name = pasted.Name + "_copy";

            // UI15: Check circular reference if pasting a widget child
            if (!string.IsNullOrEmpty(pasted.Widget) && WouldCreateCircularRef(def.Id, pasted.Widget))
            {
                _circularRefWarning = $"Circular ref: {def.Id} -> {pasted.Widget}";
                _circularRefWarningTimer = 3f;
                return;
            }

            def.Children.Add(pasted);
            _selectedChildIdx = def.Children.Count - 1;
            _selectedChildPath = new List<int> { _selectedChildIdx };
            _unsavedChanges = true;
            _statusMsg = "Child pasted";
            _statusTimer = 1.5f;
        }
    }

    private static UIEditorChildDef CloneChild(UIEditorChildDef ch)
    {
        var clone = new UIEditorChildDef
        {
            Name = ch.Name,
            Element = ch.Element,
            Widget = ch.Widget,
            X = ch.X,
            Y = ch.Y,
            Width = ch.Width,
            Height = ch.Height,
            Anchor = ch.Anchor,
            Interactive = ch.Interactive,
            DefaultText = ch.DefaultText,
            Stretch = ch.Stretch,
            IgnoreLayout = ch.IgnoreLayout,
            NineSliceScale = ch.NineSliceScale,
            Tints = ch.Tints != null ? new UIEditorTints
            {
                Normal = (byte[])ch.Tints.Normal.Clone(),
                Hovered = (byte[])ch.Tints.Hovered.Clone(),
                Pressed = (byte[])ch.Tints.Pressed.Clone(),
                Disabled = (byte[])ch.Tints.Disabled.Clone(),
            } : null,
            // RI21: text override
            HasTextOverride = ch.HasTextOverride,
            TextOverride = ch.TextOverride != null ? new UIEditorTextRegion
            {
                X = ch.TextOverride.X,
                Y = ch.TextOverride.Y,
                W = ch.TextOverride.W,
                H = ch.TextOverride.H,
                Align = ch.TextOverride.Align,
                VAlign = ch.TextOverride.VAlign,
                FontSize = ch.TextOverride.FontSize,
                FontColor = (byte[])ch.TextOverride.FontColor.Clone(),
                WordWrap = ch.TextOverride.WordWrap,
            } : null,
        };
        // RI22: child overrides
        if (ch.ChildOverrides != null)
        {
            clone.ChildOverrides = new List<ChildOverrideEntry>();
            foreach (var co in ch.ChildOverrides)
            {
                clone.ChildOverrides.Add(new ChildOverrideEntry
                {
                    ChildIndex = co.ChildIndex,
                    OverrideX = co.OverrideX,
                    OverrideY = co.OverrideY,
                    OverrideW = co.OverrideW,
                    OverrideH = co.OverrideH,
                    OverrideDefaultText = co.OverrideDefaultText,
                    OverrideIgnoreLayout = co.OverrideIgnoreLayout,
                });
            }
        }
        return clone;
    }

    // ═══════════════════════════════════════
    //  Circular reference guard (UI15)
    // ═══════════════════════════════════════

    /// <summary>
    /// Check if assigning widget 'candidateId' as a child of 'parentId' would
    /// create a circular reference (candidateId already contains parentId, directly or transitively).
    /// </summary>
    private bool WouldCreateCircularRef(string parentId, string candidateId)
    {
        if (string.IsNullOrEmpty(parentId) || string.IsNullOrEmpty(candidateId))
            return false;
        if (parentId == candidateId)
            return true;

        // BFS: check if candidateId transitively contains parentId
        var visited = new HashSet<string>();
        var queue = new Queue<string>();
        queue.Enqueue(candidateId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!visited.Add(current))
                continue;

            var widgetDef = _widgets.FirstOrDefault(w => w.Id == current);
            if (widgetDef == null) continue;

            foreach (var child in widgetDef.Children)
            {
                if (string.IsNullOrEmpty(child.Widget)) continue;
                if (child.Widget == parentId)
                    return true;
                queue.Enqueue(child.Widget);
            }
        }

        return false;
    }

    // ═══════════════════════════════════════

    private static UIEditorWidgetDef CloneWidget(UIEditorWidgetDef orig)
    {
        var clone = new UIEditorWidgetDef
        {
            Id = orig.Id,
            Background = orig.Background,
            Width = orig.Width,
            Height = orig.Height,
            BackgroundScale = orig.BackgroundScale,
            BackgroundTint = orig.BackgroundTint != null ? (byte[])orig.BackgroundTint.Clone() : null,  // RI23
            Modal = orig.Modal,
            Layout = orig.Layout,
            LayoutPadding = orig.LayoutPadding,
            LayoutSpacing = orig.LayoutSpacing,
            LayoutPadTop = orig.LayoutPadTop,
            LayoutPadBottom = orig.LayoutPadBottom,
            LayoutPadLeft = orig.LayoutPadLeft,
            LayoutPadRight = orig.LayoutPadRight,
            LayoutSpacingX = orig.LayoutSpacingX,
            LayoutSpacingY = orig.LayoutSpacingY,
            Scroll = orig.Scroll,
            ScrollContentW = orig.ScrollContentW,
            ScrollContentH = orig.ScrollContentH,
            ScrollStep = orig.ScrollStep,
            IsScrollbar = orig.IsScrollbar,                    // RI20
            ScrollbarProportional = orig.ScrollbarProportional, // RI20
        };
        foreach (var ch in orig.Children)
            clone.Children.Add(CloneChild(ch));
        return clone;
    }
}

// ═══════════════════════════════════════
//  JsonElement extension helpers
// ═══════════════════════════════════════
internal static class JsonElementExtensions
{
    public static string GetStringProp(this JsonElement el, string name, string def = "")
    {
        return el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? def : def;
    }

    public static int GetIntProp(this JsonElement el, string name, int def = 0)
    {
        if (!el.TryGetProperty(name, out var v)) return def;
        if (v.ValueKind == JsonValueKind.Number) return v.GetInt32();
        return def;
    }

    public static float GetFloatProp(this JsonElement el, string name, float def = 0f)
    {
        if (!el.TryGetProperty(name, out var v)) return def;
        if (v.ValueKind == JsonValueKind.Number) return v.GetSingle();
        return def;
    }

    public static bool GetBoolProp(this JsonElement el, string name, bool def = false)
    {
        if (!el.TryGetProperty(name, out var v)) return def;
        if (v.ValueKind == JsonValueKind.True) return true;
        if (v.ValueKind == JsonValueKind.False) return false;
        return def;
    }
}
