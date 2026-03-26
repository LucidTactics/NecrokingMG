using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Necroking.UI;

public enum UIAnchor
{
    TopLeft, TopCenter, TopRight,
    MiddleLeft, Center, MiddleRight,
    BottomLeft, BottomCenter, BottomRight
}

public enum LayoutMode { None, Horizontal, Vertical }
public enum ScrollMode { None, Vertical, Horizontal }

public class UIChildDef
{
    public string Name { get; set; } = "";
    public string ElementId { get; set; } = "";
    public string WidgetId { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public UIAnchor Anchor { get; set; } = UIAnchor.TopLeft;
    public bool Interactive { get; set; }
    public UIStateTints Tints { get; set; } = UIStateTints.Default;
    public string DefaultText { get; set; } = "";
}

public class UIWidgetDef
{
    public string Id { get; set; } = "";
    public string BackgroundNineSliceId { get; set; } = "";
    public int Width { get; set; } = 200;
    public int Height { get; set; } = 100;
    public float BackgroundScale { get; set; } = 1f;
    public bool Modal { get; set; }
    public List<UIChildDef> Children { get; set; } = new();
    public LayoutMode Layout { get; set; } = LayoutMode.None;
    public int LayoutPadding { get; set; }
    public int LayoutSpacing { get; set; }
}

public class UIWidget
{
    private UIWidgetDef _def = new();
    private NineSlice? _background;
    private bool _visible;
    private readonly Dictionary<string, string> _childTexts = new();
    private readonly Dictionary<string, Action> _callbacks = new();

    public string Id => _def.Id;
    public int Width => _def.Width;
    public int Height => _def.Height;
    public bool IsVisible { get => _visible; set => _visible = value; }
    public bool IsModal => _def.Modal;

    public void Init(UIWidgetDef def, NineSlice? background)
    {
        _def = def;
        _background = background;
        _visible = true;
    }

    public void SetText(string childName, string text) => _childTexts[childName] = text;
    public void SetCallback(string childName, Action callback) => _callbacks[childName] = callback;

    public void Update(Vector2 screenPos, Vector2 mousePos, bool mouseDown, bool mouseReleased)
    {
        if (!_visible) return;
        // Hit testing and callback invocation will be implemented with full UI
    }

    public void Draw(SpriteBatch batch, Vector2 screenPos, Color? tint = null)
    {
        if (!_visible) return;
        var color = tint ?? Color.White;
        var dest = new Rectangle((int)screenPos.X, (int)screenPos.Y, _def.Width, _def.Height);

        _background?.Draw(batch, dest, color);
    }

    public static Vector2 AnchorOffset(UIAnchor anchor, int w, int h) => anchor switch
    {
        UIAnchor.TopCenter => new(w / 2f, 0),
        UIAnchor.TopRight => new(w, 0),
        UIAnchor.MiddleLeft => new(0, h / 2f),
        UIAnchor.Center => new(w / 2f, h / 2f),
        UIAnchor.MiddleRight => new(w, h / 2f),
        UIAnchor.BottomLeft => new(0, h),
        UIAnchor.BottomCenter => new(w / 2f, h),
        UIAnchor.BottomRight => new(w, h),
        _ => Vector2.Zero
    };
}
