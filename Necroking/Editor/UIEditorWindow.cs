using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
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
}

// ─────────────────────────────────────────────
// Element definition (editor working copy)
// ─────────────────────────────────────────────
public class UIEditorElementDef
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "nineSlice";
    public string NineSlice { get; set; } = "";
    public int Width { get; set; } = 100;
    public int Height { get; set; } = 40;
    public byte[]? TintColor { get; set; }
    public UIEditorTextRegion? TextRegion { get; set; }
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
    public bool Modal { get; set; }
    public string Layout { get; set; } = "none";
    public int LayoutPadding { get; set; }
    public int LayoutSpacing { get; set; }
    public string Scroll { get; set; } = "none";
    public int ScrollContentW { get; set; }
    public int ScrollContentH { get; set; }
    public int ScrollStep { get; set; } = 20;
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

    // Loaded NineSlice instances (for preview rendering)
    private readonly Dictionary<string, NineSlice> _nsInstances = new();

    // Definitions path
    private string _defsDir = "";

    // Previous mouse state (for standalone usage without EditorBase.UpdateInput)
    private MouseState _prevMouseLocal;
    private GameTime? _lastGameTime;

    // ═══════════════════════════════════════
    //  Initialization (two signatures for compat)
    // ═══════════════════════════════════════

    /// <summary>Old-style init compatible with existing Game1 code.</summary>
    public void Init(SpriteBatch spriteBatch, Texture2D pixel, SpriteFont? font, SpriteFont? smallFont)
    {
        SetContext(spriteBatch, pixel, font, smallFont, null);
        _device = spriteBatch.GraphicsDevice;
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
                    Width = item.GetIntProp("width", 100),
                    Height = item.GetIntProp("height", 40),
                };
                if (item.TryGetProperty("tintColor", out var tc) && tc.ValueKind == JsonValueKind.Array)
                    def.TintColor = ReadColorArray(tc);
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
                    Scroll = item.GetStringProp("scroll", "none"),
                    ScrollContentW = item.GetIntProp("scrollContentW"),
                    ScrollContentH = item.GetIntProp("scrollContentH"),
                    ScrollStep = item.GetIntProp("scrollStep", 20),
                };
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
                        };
                        if (ch.TryGetProperty("tints", out var tints))
                        {
                            child.Tints = new UIEditorTints();
                            if (tints.TryGetProperty("normal", out var tn)) child.Tints.Normal = ReadColorArray(tn);
                            if (tints.TryGetProperty("hovered", out var th)) child.Tints.Hovered = ReadColorArray(th);
                            if (tints.TryGetProperty("pressed", out var tp)) child.Tints.Pressed = ReadColorArray(tp);
                            if (tints.TryGetProperty("disabled", out var td)) child.Tints.Disabled = ReadColorArray(td);
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
            writer.WriteNumber("width", el.Width);
            writer.WriteNumber("height", el.Height);
            if (el.TintColor != null)
                WriteColorArray(writer, "tintColor", el.TintColor);
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
            writer.WriteBoolean("modal", wd.Modal);
            if (wd.Layout != "none" && !string.IsNullOrEmpty(wd.Layout))
            {
                writer.WriteString("layout", wd.Layout);
                writer.WriteNumber("layoutPadding", wd.LayoutPadding);
                writer.WriteNumber("layoutSpacing", wd.LayoutSpacing);
            }
            if (wd.Scroll != "none" && !string.IsNullOrEmpty(wd.Scroll))
            {
                writer.WriteString("scroll", wd.Scroll);
                writer.WriteNumber("scrollContentW", wd.ScrollContentW);
                writer.WriteNumber("scrollContentH", wd.ScrollContentH);
                writer.WriteNumber("scrollStep", wd.ScrollStep);
            }
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
        _prevMouseLocal = mouse;
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

        // Status timer
        if (_statusTimer > 0)
            _statusTimer -= (float)gameTime.ElapsedGameTime.TotalSeconds;

        // Ctrl+S save
        if (_kb.IsKeyDown(Keys.LeftControl) && _kb.IsKeyDown(Keys.S) && _prevKb.IsKeyUp(Keys.S))
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
    }

    private void SaveAll()
    {
        try
        {
            switch (ActiveTab)
            {
                case UIEditorTab.NineSlices: SaveNineSlices(); break;
                case UIEditorTab.Elements: SaveElements(); break;
                case UIEditorTab.Widgets: SaveWidgets(); break;
            }
            _unsavedChanges = false;
            _statusMsg = $"Saved {ActiveTab}";
            _statusTimer = 3f;
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

        // Add / Delete buttons
        int btnY = y + h - btnAreaH + 2;
        int btnW = (w - 20) / 2;
        if (DrawButton("+ Add", x + 4, btnY, btnW, btnH))
        {
            _nineSlices.Add(new UIEditorNineSliceDef { Id = $"new_slice_{_nineSlices.Count}" });
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

        var def = _nineSlices[SelectedIndex];
        int propW = Math.Min(340, w);
        int curY = y + 8;
        int pad = 8;

        // ID
        string newId = DrawTextField("ns_id", "ID", def.Id, x + pad, curY, propW);
        if (newId != def.Id) { def.Id = newId; _unsavedChanges = true; InvalidateNineSlice(def.Id); }
        curY += 24;

        // Texture path
        string newTex = DrawTextField("ns_tex", "Texture", def.Texture, x + pad, curY, propW);
        if (newTex != def.Texture)
        {
            def.Texture = newTex;
            _unsavedChanges = true;
            InvalidateNineSlice(def.Id);
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

        int bfW = propW / 2 - 4;
        int newBL = DrawIntField("ns_bl", "L", def.BorderLeft, x + pad, curY, bfW);
        int newBR = DrawIntField("ns_br", "R", def.BorderRight, x + pad + bfW + 8, curY, bfW);
        if (newBL != def.BorderLeft) { def.BorderLeft = Math.Max(0, newBL); _unsavedChanges = true; InvalidateNineSlice(def.Id); }
        if (newBR != def.BorderRight) { def.BorderRight = Math.Max(0, newBR); _unsavedChanges = true; InvalidateNineSlice(def.Id); }
        curY += 24;

        int newBT = DrawIntField("ns_bt", "T", def.BorderTop, x + pad, curY, bfW);
        int newBB = DrawIntField("ns_bb", "B", def.BorderBottom, x + pad + bfW + 8, curY, bfW);
        if (newBT != def.BorderTop) { def.BorderTop = Math.Max(0, newBT); _unsavedChanges = true; InvalidateNineSlice(def.Id); }
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

        var def = _elements[SelectedIndex];
        int curY = y + 8;
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

        // Nine-Slice picker
        if (def.Type == "nineSlice" || def.Type == "image")
        {
            var nsIds = new[] { "(none)" }.Concat(_nineSlices.Select(ns => ns.Id)).ToArray();
            string curNs = string.IsNullOrEmpty(def.NineSlice) ? "(none)" : def.NineSlice;
            string newNs = DrawCombo("el_ns", "Nine-Slice", curNs, nsIds, x + pad, curY, propW);
            if (newNs == "(none)") newNs = "";
            if (newNs != def.NineSlice) { def.NineSlice = newNs; _unsavedChanges = true; }
            curY += 24;
        }

        // Size
        int newW = DrawIntField("el_w", "Width", def.Width, x + pad, curY, propW);
        if (newW != def.Width) { def.Width = Math.Max(16, newW); _unsavedChanges = true; }
        curY += 24;

        int newH = DrawIntField("el_h", "Height", def.Height, x + pad, curY, propW);
        if (newH != def.Height) { def.Height = Math.Max(16, newH); _unsavedChanges = true; }
        curY += 24;

        // Tint color
        DrawText("Tint:", new Vector2(x + pad, curY), TextDim);
        byte[] tint = def.TintColor ?? new byte[] { 255, 255, 255, 255 };
        var tintColor = ByteColor(tint);
        DrawRect(new Rectangle(x + pad + 120, curY, 40, 18), tintColor);
        DrawBorder(new Rectangle(x + pad + 120, curY, 40, 18), InputBorder);
        curY += 22;

        int newR = DrawIntField("el_tint_r", "  R", tint[0], x + pad, curY, propW);
        tint[0] = (byte)Math.Clamp(newR, 0, 255); curY += 22;
        int newG = DrawIntField("el_tint_g", "  G", tint[1], x + pad, curY, propW);
        tint[1] = (byte)Math.Clamp(newG, 0, 255); curY += 22;
        int newB = DrawIntField("el_tint_b", "  B", tint[2], x + pad, curY, propW);
        tint[2] = (byte)Math.Clamp(newB, 0, 255); curY += 22;
        int newA = DrawIntField("el_tint_a", "  A", tint.Length > 3 ? tint[3] : 255, x + pad, curY, propW);
        if (tint.Length >= 4) tint[3] = (byte)Math.Clamp(newA, 0, 255); curY += 22;

        if (def.TintColor == null || !tint.SequenceEqual(def.TintColor))
        {
            def.TintColor = tint;
            _unsavedChanges = true;
        }

        // Text Region (for text type)
        if (def.Type == "text" || def.TextRegion != null)
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

            // Font color
            DrawText("Font Color:", new Vector2(x + pad, curY), TextDim);
            DrawRect(new Rectangle(x + pad + 120, curY, 40, 18), ByteColor(tr.FontColor));
            DrawBorder(new Rectangle(x + pad + 120, curY, 40, 18), InputBorder);
            curY += 22;

            int fcR = DrawIntField("el_fcr", "  R", tr.FontColor[0], x + pad, curY, propW);
            tr.FontColor[0] = (byte)Math.Clamp(fcR, 0, 255); curY += 22;
            int fcG = DrawIntField("el_fcg", "  G", tr.FontColor[1], x + pad, curY, propW);
            tr.FontColor[1] = (byte)Math.Clamp(fcG, 0, 255); curY += 22;
            int fcB2 = DrawIntField("el_fcb", "  B", tr.FontColor[2], x + pad, curY, propW);
            tr.FontColor[2] = (byte)Math.Clamp(fcB2, 0, 255); curY += 22;
            int fcA = DrawIntField("el_fca", "  A", tr.FontColor.Length > 3 ? tr.FontColor[3] : 255, x + pad, curY, propW);
            if (tr.FontColor.Length >= 4) tr.FontColor[3] = (byte)Math.Clamp(fcA, 0, 255);
        }
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

        // Draw element background (nine-slice or plain)
        var ns = !string.IsNullOrEmpty(def.NineSlice) ? GetOrLoadNineSlice(def.NineSlice) : null;
        if (ns != null)
        {
            ns.Draw(_sb, elRect, ByteColor(def.TintColor ?? new byte[] { 255, 255, 255, 255 }));
        }
        else
        {
            DrawRect(elRect, ByteColor(def.TintColor ?? new byte[] { 60, 60, 80, 200 }));
        }

        // Element outline
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
        if (clicked >= 0) { SelectedIndex = clicked; _selectedChildIdx = -1; }

        int btnY = y + h - btnAreaH + 2;
        int btnW3 = (w - 24) / 3;

        if (DrawButton("+ Add", x + 4, btnY, btnW3, btnH))
        {
            _widgets.Add(new UIEditorWidgetDef { Id = $"new_widget_{_widgets.Count}" });
            SelectedIndex = _widgets.Count - 1;
            _selectedChildIdx = -1;
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

        var def = _widgets[SelectedIndex];
        int curY = y + 8;
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
            int newPad = DrawIntField("wd_lpad", "Padding", def.LayoutPadding, x + pad, curY, propW);
            if (newPad != def.LayoutPadding) { def.LayoutPadding = Math.Max(0, newPad); _unsavedChanges = true; }
            curY += 22;

            int newSp = DrawIntField("wd_lsp", "Spacing", def.LayoutSpacing, x + pad, curY, propW);
            if (newSp != def.LayoutSpacing) { def.LayoutSpacing = Math.Max(0, newSp); _unsavedChanges = true; }
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

        // Children section
        DrawRect(new Rectangle(x + pad, curY, propW, 1), PanelBorder);
        curY += 6;
        DrawText($"Children ({def.Children.Count}):", new Vector2(x + pad, curY), TextColor);
        curY += 20;

        // Children list
        int childListH = Math.Min(150, h - (curY - y) - 80);
        if (childListH > 40)
        {
            var childNames = def.Children.Select(c =>
            {
                string refStr = !string.IsNullOrEmpty(c.Element) ? c.Element :
                                !string.IsNullOrEmpty(c.Widget) ? $"[W]{c.Widget}" : "(empty)";
                return $"{c.Name} -> {refStr}";
            }).ToList();

            int childClicked = DrawScrollableList("wd_children", childNames, _selectedChildIdx,
                x + pad, curY, propW, childListH);
            if (childClicked >= 0) _selectedChildIdx = childClicked;
            curY += childListH + 4;
        }

        // Child buttons
        int childBtnW = (propW - 16) / 4;
        int childBtnH = 22;

        if (DrawButton("+Elem", x + pad, curY, childBtnW, childBtnH))
        {
            var newChild = new UIEditorChildDef
            {
                Name = $"child_{def.Children.Count}",
                Width = 100,
                Height = 40,
            };
            if (_elements.Count > 0) newChild.Element = _elements[0].Id;
            def.Children.Add(newChild);
            _selectedChildIdx = def.Children.Count - 1;
            _unsavedChanges = true;
        }

        if (DrawButton("+Wgt", x + pad + childBtnW + 4, curY, childBtnW, childBtnH))
        {
            var newChild = new UIEditorChildDef
            {
                Name = $"child_{def.Children.Count}",
                Width = 200,
                Height = 100,
            };
            def.Children.Add(newChild);
            _selectedChildIdx = def.Children.Count - 1;
            _unsavedChanges = true;
        }

        if (DrawButton("Up", x + pad + (childBtnW + 4) * 2, curY, childBtnW, childBtnH))
        {
            if (_selectedChildIdx > 0 && _selectedChildIdx < def.Children.Count)
            {
                (def.Children[_selectedChildIdx], def.Children[_selectedChildIdx - 1]) =
                    (def.Children[_selectedChildIdx - 1], def.Children[_selectedChildIdx]);
                _selectedChildIdx--;
                _unsavedChanges = true;
            }
        }

        if (DrawButton("Del", x + pad + (childBtnW + 4) * 3, curY, childBtnW, childBtnH, DangerColor))
        {
            if (_selectedChildIdx >= 0 && _selectedChildIdx < def.Children.Count)
            {
                def.Children.RemoveAt(_selectedChildIdx);
                if (_selectedChildIdx >= def.Children.Count)
                    _selectedChildIdx = def.Children.Count - 1;
                _unsavedChanges = true;
            }
        }
        curY += childBtnH + 6;

        // Selected child properties
        if (_selectedChildIdx >= 0 && _selectedChildIdx < def.Children.Count)
        {
            DrawChildProperties(def.Children[_selectedChildIdx], x + pad, curY, propW, y + h - curY);
        }
    }

    private void DrawChildProperties(UIEditorChildDef child, int x, int y, int propW, int maxH)
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

        // Widget ref dropdown
        var widgetIds = new[] { "(none)" }.Concat(_widgets.Select(wd => wd.Id)).ToArray();
        string curWd = string.IsNullOrEmpty(child.Widget) ? "(none)" : child.Widget;
        string newWd = DrawCombo("ch_widget", "Widget", curWd, widgetIds, x, curY, propW);
        if (newWd == "(none)") newWd = "";
        if (newWd != child.Widget) { child.Widget = newWd; _unsavedChanges = true; }
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
        if (curY + 80 < y + maxH)
        {
            DrawText("Anchor:", new Vector2(x, curY), TextDim);
            curY += 18;
            DrawAnchorGrid(child, x + 120, curY);
            curY += 62;
        }

        // Stretch mode
        if (curY + 22 < y + maxH)
        {
            string[] stretches = { "", "horizontal", "vertical", "both" };
            string curStretch = child.Stretch ?? "";
            string newStretch = DrawCombo("ch_stretch", "Stretch", curStretch, stretches, x, curY, propW);
            if (newStretch != child.Stretch) { child.Stretch = newStretch; _unsavedChanges = true; }
            curY += 22;
        }

        // Interactive toggle
        if (curY + 22 < y + maxH)
        {
            bool newInteract = DrawCheckbox("Interactive", child.Interactive, x, curY);
            if (newInteract != child.Interactive)
            {
                child.Interactive = newInteract;
                if (newInteract && child.Tints == null)
                    child.Tints = new UIEditorTints();
                _unsavedChanges = true;
            }
            curY += 22;
        }

        // State tints (if interactive)
        if (child.Interactive && child.Tints != null && curY + 100 < y + maxH)
        {
            DrawText("State Tints:", new Vector2(x, curY), TextDim);
            curY += 18;
            DrawTintRow("Normal", child.Tints.Normal, x, ref curY);
            DrawTintRow("Hovered", child.Tints.Hovered, x, ref curY);
            DrawTintRow("Pressed", child.Tints.Pressed, x, ref curY);
            DrawTintRow("Disabled", child.Tints.Disabled, x, ref curY);
        }

        // Text override
        if (curY + 22 < y + maxH)
        {
            string newText = DrawTextField("ch_text", "Def. Text", child.DefaultText, x, curY, propW);
            if (newText != child.DefaultText) { child.DefaultText = newText; _unsavedChanges = true; }
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
            bgNs.Draw(_sb, wdRect, Color.White, def.BackgroundScale);
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
                def.Children[_canvasDragChild].X = _wDragOrigX + dx;
                def.Children[_canvasDragChild].Y = _wDragOrigY + dy;
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
        NineSlice? childNs = null;
        if (elemDef != null && !string.IsNullOrEmpty(elemDef.NineSlice))
            childNs = GetOrLoadNineSlice(elemDef.NineSlice);

        if (childNs != null)
        {
            byte[] tc = elemDef?.TintColor ?? new byte[] { 255, 255, 255, 255 };
            childNs.Draw(_sb, rect, ByteColor(tc));
        }
        else
        {
            DrawRect(rect, new Color(50, 50, 70, 180));
        }

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
            DrawBorder(rect, new Color(255, 200, 80, 255), 2);
        else
            DrawBorder(rect, new Color(100, 100, 140, 100));
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

    private static UIEditorWidgetDef CloneWidget(UIEditorWidgetDef orig)
    {
        var clone = new UIEditorWidgetDef
        {
            Id = orig.Id,
            Background = orig.Background,
            Width = orig.Width,
            Height = orig.Height,
            BackgroundScale = orig.BackgroundScale,
            Modal = orig.Modal,
            Layout = orig.Layout,
            LayoutPadding = orig.LayoutPadding,
            LayoutSpacing = orig.LayoutSpacing,
            Scroll = orig.Scroll,
            ScrollContentW = orig.ScrollContentW,
            ScrollContentH = orig.ScrollContentH,
            ScrollStep = orig.ScrollStep,
        };
        foreach (var ch in orig.Children)
        {
            clone.Children.Add(new UIEditorChildDef
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
                Tints = ch.Tints != null ? new UIEditorTints
                {
                    Normal = (byte[])ch.Tints.Normal.Clone(),
                    Hovered = (byte[])ch.Tints.Hovered.Clone(),
                    Pressed = (byte[])ch.Tints.Pressed.Clone(),
                    Disabled = (byte[])ch.Tints.Disabled.Clone(),
                } : null
            });
        }
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
