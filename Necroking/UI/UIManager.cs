using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Necroking.UI;

public class UIManager
{
    private readonly Dictionary<string, NineSlice> _nineSlices = new();
    private readonly Dictionary<string, UIElementDef> _elementDefs = new();
    private readonly Dictionary<string, UIWidgetDef> _widgetDefs = new();

    private struct ActiveWidget
    {
        public UIWidget Widget;
        public Vector2 ScreenPos;
    }
    private readonly List<ActiveWidget> _activeWidgets = new();

    public IReadOnlyDictionary<string, NineSlice> NineSlices => _nineSlices;
    public IReadOnlyDictionary<string, UIElementDef> ElementDefs => _elementDefs;
    public IReadOnlyDictionary<string, UIWidgetDef> WidgetDefs => _widgetDefs;

    public void Init() { }

    public bool LoadDefinitions(GraphicsDevice device, string dirPath)
    {
        bool ok = true;
        string nsPath = Path.Combine(dirPath, "nine_slices.json");
        if (File.Exists(nsPath))
        {
            // Load nine-slice definitions and textures
            // For now, just note it exists
        }
        return ok;
    }

    public UIWidget? OpenWidget(string widgetId)
    {
        if (!_widgetDefs.TryGetValue(widgetId, out var def)) return null;
        var widget = new UIWidget();
        _nineSlices.TryGetValue(def.BackgroundNineSliceId, out var bg);
        widget.Init(def, bg);
        _activeWidgets.Add(new ActiveWidget { Widget = widget, ScreenPos = Vector2.Zero });
        return widget;
    }

    public void CloseWidget(UIWidget w)
    {
        _activeWidgets.RemoveAll(aw => aw.Widget == w);
    }

    public void CloseAll() => _activeWidgets.Clear();

    public void Update()
    {
        var mouseState = Microsoft.Xna.Framework.Input.Mouse.GetState();
        var mousePos = new Vector2(mouseState.X, mouseState.Y);
        bool mouseDown = mouseState.LeftButton == Microsoft.Xna.Framework.Input.ButtonState.Pressed;

        foreach (var aw in _activeWidgets)
            aw.Widget.Update(aw.ScreenPos, mousePos, mouseDown, false);
    }

    public void Draw(SpriteBatch batch)
    {
        foreach (var aw in _activeWidgets)
            aw.Widget.Draw(batch, aw.ScreenPos);
    }

    public bool HasModalWidget()
    {
        foreach (var aw in _activeWidgets)
            if (aw.Widget.IsModal) return true;
        return false;
    }

    public void Shutdown()
    {
        foreach (var ns in _nineSlices.Values) ns.Unload();
        _nineSlices.Clear();
        _elementDefs.Clear();
        _widgetDefs.Clear();
        _activeWidgets.Clear();
    }
}
