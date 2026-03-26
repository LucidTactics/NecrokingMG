using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Necroking.UI;

public enum UIState { Normal, Hovered, Pressed, Disabled }
public enum TextAlign { Left, Center, Right }
public enum TextVAlign { Top, Center, Bottom }
public enum ElementType { NineSlice, Text, Image }

public struct UIStateTints
{
    public Color Normal, Hovered, Pressed, Disabled;

    public static UIStateTints Default => new()
    {
        Normal = Color.White,
        Hovered = new Color(220, 220, 255),
        Pressed = new Color(180, 180, 200),
        Disabled = new Color(128, 128, 128)
    };

    public Color ForState(UIState s) => s switch
    {
        UIState.Hovered => Hovered,
        UIState.Pressed => Pressed,
        UIState.Disabled => Disabled,
        _ => Normal
    };
}

public class UITextRegion
{
    public Rectangle Rect { get; set; }
    public TextAlign Align { get; set; } = TextAlign.Center;
    public TextVAlign VAlign { get; set; } = TextVAlign.Center;
    public int FontSize { get; set; } = 14;
    public Color FontColor { get; set; } = Color.White;
    public bool WordWrap { get; set; }
}

public class UIElementDef
{
    public string Id { get; set; } = "";
    public ElementType Type { get; set; } = ElementType.NineSlice;
    public string NineSliceId { get; set; } = "";
    public int DefaultWidth { get; set; } = 100;
    public int DefaultHeight { get; set; } = 40;
    public Color TintColor { get; set; } = Color.White;
    public UITextRegion TextRegion { get; set; } = new();
}

public class UIElement
{
    private NineSlice? _nineSlice;
    private UIElementDef _def = new();

    public void Init(UIElementDef def, NineSlice? nineSlice)
    {
        _def = def;
        _nineSlice = nineSlice;
    }

    public void Draw(SpriteBatch batch, Vector2 pos, int width, int height,
                     string? text = null, Color? tint = null)
    {
        var color = tint ?? _def.TintColor;
        var dest = new Rectangle((int)pos.X, (int)pos.Y, width, height);

        if (_def.Type == ElementType.NineSlice && _nineSlice != null)
        {
            _nineSlice.Draw(batch, dest, color);
        }

        // Text rendering would use SpriteFont here
        // Deferred until font loading is set up
    }
}
