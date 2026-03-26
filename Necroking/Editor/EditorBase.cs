using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace Necroking.Editor;

/// <summary>
/// Shared SpriteBatch-based editor UI infrastructure.
/// Provides panel drawing, buttons, text fields, scrolling, and input handling.
/// </summary>
public class EditorBase
{
    // Colors
    public static readonly Color PanelBg = new(25, 25, 40, 240);
    public static readonly Color PanelHeader = new(40, 40, 65, 250);
    public static readonly Color PanelBorder = new(80, 80, 120);
    public static readonly Color ItemBg = new(35, 35, 55, 220);
    public static readonly Color ItemHover = new(50, 50, 80, 230);
    public static readonly Color ItemSelected = new(60, 60, 110, 240);
    public static readonly Color ButtonBg = new(55, 55, 85, 240);
    public static readonly Color ButtonHover = new(70, 70, 110, 250);
    public static readonly Color ButtonPress = new(40, 80, 140, 255);
    public static readonly Color InputBg = new(15, 15, 25, 230);
    public static readonly Color InputBorder = new(70, 70, 100);
    public static readonly Color InputActive = new(80, 100, 160);
    public static readonly Color TextColor = new(200, 200, 220);
    public static readonly Color TextDim = new(140, 140, 165);
    public static readonly Color TextBright = new(240, 240, 255);
    public static readonly Color AccentColor = new(100, 140, 220);
    public static readonly Color DangerColor = new(200, 80, 80);
    public static readonly Color SuccessColor = new(80, 200, 100);

    // State
    protected SpriteBatch _sb = null!;
    protected Texture2D _pixel = null!;
    protected SpriteFont? _font;
    protected SpriteFont? _smallFont;
    protected SpriteFont? _largeFont;
    internal MouseState _mouse;
    internal MouseState _prevMouse;
    internal KeyboardState _kb;
    internal KeyboardState _prevKb;
    protected int _screenW, _screenH;

    // Text input state
    private string? _activeFieldId;
    private string _inputBuffer = "";
    private float _cursorBlink;
    private double _keyRepeatTimer;
    private Keys _lastRepeatingKey;

    // Scroll state (per-panel, keyed by panel ID)
    private readonly Dictionary<string, float> _scrollOffsets = new();

    public void SetContext(SpriteBatch sb, Texture2D pixel, SpriteFont? font, SpriteFont? smallFont, SpriteFont? largeFont)
    {
        _sb = sb;
        _pixel = pixel;
        _font = font;
        _smallFont = smallFont;
        _largeFont = largeFont;
    }

    public void UpdateInput(MouseState mouse, MouseState prevMouse, KeyboardState kb, KeyboardState prevKb,
        int screenW, int screenH, GameTime gameTime)
    {
        _mouse = mouse;
        _prevMouse = prevMouse;
        _kb = kb;
        _prevKb = prevKb;
        _screenW = screenW;
        _screenH = screenH;
        _cursorBlink += (float)gameTime.ElapsedGameTime.TotalSeconds;

        // Handle key repeat for text input
        if (_activeFieldId != null)
            HandleTextInput(gameTime);
    }

    // === Drawing primitives ===

    public void DrawRect(Rectangle rect, Color color)
    {
        _sb.Draw(_pixel, rect, color);
    }

    public void DrawBorder(Rectangle rect, Color color, int thickness = 1)
    {
        DrawRect(new Rectangle(rect.X, rect.Y, rect.Width, thickness), color);
        DrawRect(new Rectangle(rect.X, rect.Y + rect.Height - thickness, rect.Width, thickness), color);
        DrawRect(new Rectangle(rect.X, rect.Y, thickness, rect.Height), color);
        DrawRect(new Rectangle(rect.X + rect.Width - thickness, rect.Y, thickness, rect.Height), color);
    }

    public void DrawText(string text, Vector2 pos, Color color, SpriteFont? font = null)
    {
        var f = font ?? _smallFont ?? _font;
        if (f != null)
            _sb.DrawString(f, text, pos, color);
    }

    public Vector2 MeasureText(string text, SpriteFont? font = null)
    {
        var f = font ?? _smallFont ?? _font;
        return f?.MeasureString(text) ?? Vector2.Zero;
    }

    public void DrawTexture(Texture2D texture, Vector2 position, Rectangle sourceRect,
        Color color, float rotation, Vector2 origin, float scale, SpriteEffects effects)
    {
        _sb.Draw(texture, position, sourceRect, color, rotation, origin, scale, effects, 0f);
    }

    // === Panel ===

    public Rectangle DrawPanel(int x, int y, int w, int h, string title)
    {
        var rect = new Rectangle(x, y, w, h);
        DrawRect(rect, PanelBg);
        DrawRect(new Rectangle(x, y, w, 28), PanelHeader);
        DrawRect(new Rectangle(x, y + 28, w, 1), PanelBorder);
        DrawText(title, new Vector2(x + 8, y + 5), TextBright, _font);
        return rect;
    }

    // === Button ===

    public bool DrawButton(string text, int x, int y, int w, int h, Color? bgOverride = null)
    {
        var rect = new Rectangle(x, y, w, h);
        bool hovered = rect.Contains(_mouse.X, _mouse.Y);
        bool pressed = hovered && _mouse.LeftButton == ButtonState.Pressed;
        bool clicked = hovered && _mouse.LeftButton == ButtonState.Released && _prevMouse.LeftButton == ButtonState.Pressed;

        Color bg = bgOverride ?? ButtonBg;
        if (pressed) bg = ButtonPress;
        else if (hovered) bg = ButtonHover;

        DrawRect(rect, bg);
        var textSize = MeasureText(text);
        DrawText(text, new Vector2(x + (w - textSize.X) / 2, y + (h - textSize.Y) / 2), TextBright);
        return clicked;
    }

    // === Scrollable List ===

    /// <summary>
    /// Draws a scrollable list of items. Returns the index of the clicked item, or -1.
    /// </summary>
    public int DrawScrollableList(string panelId, IReadOnlyList<string> items, int selectedIdx,
        int x, int y, int w, int h, string? searchFilter = null)
    {
        // Clip region
        var clipRect = new Rectangle(x, y, w, h);
        DrawRect(clipRect, new Color(20, 20, 35, 200));

        if (!_scrollOffsets.ContainsKey(panelId))
            _scrollOffsets[panelId] = 0;

        float scroll = _scrollOffsets[panelId];
        int itemH = 22;
        int clicked = -1;

        // Handle scroll wheel when hovering
        if (clipRect.Contains(_mouse.X, _mouse.Y))
        {
            int scrollDelta = _mouse.ScrollWheelValue - _prevMouse.ScrollWheelValue;
            if (scrollDelta != 0)
            {
                scroll -= scrollDelta * 0.15f;
                int totalH = 0;
                for (int i = 0; i < items.Count; i++)
                {
                    if (searchFilter != null && !items[i].Contains(searchFilter, StringComparison.OrdinalIgnoreCase))
                        continue;
                    totalH += itemH;
                }
                float maxScroll = Math.Max(0, totalH - h);
                scroll = Math.Clamp(scroll, 0, maxScroll);
                _scrollOffsets[panelId] = scroll;
            }
        }

        // Draw items
        float drawY = y - scroll;
        for (int i = 0; i < items.Count; i++)
        {
            if (searchFilter != null && !items[i].Contains(searchFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            if (drawY + itemH < y) { drawY += itemH; continue; }
            if (drawY >= y + h) break;

            var itemRect = new Rectangle(x, (int)drawY, w, itemH);
            bool hovered = itemRect.Contains(_mouse.X, _mouse.Y) && clipRect.Contains(_mouse.X, _mouse.Y);

            Color bg;
            if (i == selectedIdx) bg = ItemSelected;
            else if (hovered) bg = ItemHover;
            else bg = (i % 2 == 0) ? new Color(30, 30, 48, 200) : new Color(25, 25, 40, 200);

            // Only draw if visible
            if (drawY + itemH > y && drawY < y + h)
            {
                DrawRect(itemRect, bg);
                float textClipY = Math.Max(drawY, y);
                if (drawY >= y)
                    DrawText(items[i], new Vector2(x + 4, drawY + 2), i == selectedIdx ? TextBright : TextColor);

                if (hovered && _mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released)
                    clicked = i;
            }

            drawY += itemH;
        }

        // Scrollbar
        int totalItems = 0;
        for (int i = 0; i < items.Count; i++)
        {
            if (searchFilter == null || items[i].Contains(searchFilter, StringComparison.OrdinalIgnoreCase))
                totalItems++;
        }
        int totalHeight = totalItems * itemH;
        if (totalHeight > h)
        {
            float scrollRatio = scroll / (totalHeight - h);
            int barH = Math.Max(20, h * h / totalHeight);
            int barY = y + (int)(scrollRatio * (h - barH));
            DrawRect(new Rectangle(x + w - 6, barY, 5, barH), new Color(100, 100, 140, 180));
        }

        return clicked;
    }

    // === Text Input Field ===

    public string DrawTextField(string fieldId, string label, string value, int x, int y, int w)
    {
        int labelW = 120;
        DrawText(label, new Vector2(x, y + 2), TextDim);

        int inputX = x + labelW;
        int inputW = w - labelW;
        int inputH = 20;
        var inputRect = new Rectangle(inputX, y, inputW, inputH);

        bool isActive = _activeFieldId == fieldId;
        bool hovered = inputRect.Contains(_mouse.X, _mouse.Y);

        // Click to activate
        if (hovered && _mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released)
        {
            _activeFieldId = fieldId;
            _inputBuffer = value;
            _cursorBlink = 0;
            isActive = true;
        }
        // Click elsewhere to deactivate
        else if (isActive && _mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released && !hovered)
        {
            _activeFieldId = null;
            return _inputBuffer;
        }

        DrawRect(inputRect, InputBg);
        DrawBorder(inputRect, isActive ? InputActive : InputBorder);

        string displayText = isActive ? _inputBuffer : value;
        DrawText(displayText, new Vector2(inputX + 3, y + 2), isActive ? TextBright : TextColor);

        // Draw cursor
        if (isActive && ((int)(_cursorBlink * 2) % 2 == 0))
        {
            float cursorX = inputX + 3 + MeasureText(displayText).X;
            DrawRect(new Rectangle((int)cursorX, y + 2, 1, inputH - 4), TextBright);
        }

        return isActive ? _inputBuffer : value;
    }

    /// <summary>
    /// Draw an int input field with +/- buttons
    /// </summary>
    public int DrawIntField(string fieldId, string label, int value, int x, int y, int w)
    {
        int labelW = 120;
        DrawText(label, new Vector2(x, y + 2), TextDim);

        int inputX = x + labelW;
        int inputW = w - labelW - 46; // room for +/- buttons
        int inputH = 20;

        // Text display
        var inputRect = new Rectangle(inputX, y, inputW, inputH);
        bool isActive = _activeFieldId == fieldId;
        bool hovered = inputRect.Contains(_mouse.X, _mouse.Y);

        if (hovered && _mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released)
        {
            _activeFieldId = fieldId;
            _inputBuffer = value.ToString();
            _cursorBlink = 0;
            isActive = true;
        }
        else if (isActive && _mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released && !hovered)
        {
            _activeFieldId = null;
            if (int.TryParse(_inputBuffer, out int parsed))
                return parsed;
            return value;
        }

        DrawRect(inputRect, InputBg);
        DrawBorder(inputRect, isActive ? InputActive : InputBorder);
        string displayText = isActive ? _inputBuffer : value.ToString();
        DrawText(displayText, new Vector2(inputX + 3, y + 2), isActive ? TextBright : TextColor);

        if (isActive && ((int)(_cursorBlink * 2) % 2 == 0))
        {
            float cursorX = inputX + 3 + MeasureText(displayText).X;
            DrawRect(new Rectangle((int)cursorX, y + 2, 1, inputH - 4), TextBright);
        }

        // +/- buttons
        int btnX = inputX + inputW + 2;
        if (DrawButton("-", btnX, y, 20, inputH))
        {
            if (isActive && int.TryParse(_inputBuffer, out int v)) { _inputBuffer = (v - 1).ToString(); return v - 1; }
            return value - 1;
        }
        if (DrawButton("+", btnX + 22, y, 20, inputH))
        {
            if (isActive && int.TryParse(_inputBuffer, out int v)) { _inputBuffer = (v + 1).ToString(); return v + 1; }
            return value + 1;
        }

        if (isActive && int.TryParse(_inputBuffer, out int current))
            return current;
        return value;
    }

    /// <summary>
    /// Draw a float input field with drag behavior
    /// </summary>
    public float DrawFloatField(string fieldId, string label, float value, int x, int y, int w, float step = 0.1f)
    {
        int labelW = 120;
        DrawText(label, new Vector2(x, y + 2), TextDim);

        int inputX = x + labelW;
        int inputW = w - labelW - 46;
        int inputH = 20;

        var inputRect = new Rectangle(inputX, y, inputW, inputH);
        bool isActive = _activeFieldId == fieldId;
        bool hovered = inputRect.Contains(_mouse.X, _mouse.Y);

        if (hovered && _mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released)
        {
            _activeFieldId = fieldId;
            _inputBuffer = value.ToString("F2");
            _cursorBlink = 0;
            isActive = true;
        }
        else if (isActive && _mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released && !hovered)
        {
            _activeFieldId = null;
            if (float.TryParse(_inputBuffer, out float parsed))
                return parsed;
            return value;
        }

        DrawRect(inputRect, InputBg);
        DrawBorder(inputRect, isActive ? InputActive : InputBorder);
        string displayText = isActive ? _inputBuffer : value.ToString("F2");
        DrawText(displayText, new Vector2(inputX + 3, y + 2), isActive ? TextBright : TextColor);

        if (isActive && ((int)(_cursorBlink * 2) % 2 == 0))
        {
            float cursorX = inputX + 3 + MeasureText(displayText).X;
            DrawRect(new Rectangle((int)cursorX, y + 2, 1, inputH - 4), TextBright);
        }

        // +/- buttons
        int btnX = inputX + inputW + 2;
        if (DrawButton("-", btnX, y, 20, inputH))
        {
            float newVal = MathF.Round((value - step) / step) * step;
            if (isActive) _inputBuffer = newVal.ToString("F2");
            return newVal;
        }
        if (DrawButton("+", btnX + 22, y, 20, inputH))
        {
            float newVal = MathF.Round((value + step) / step) * step;
            if (isActive) _inputBuffer = newVal.ToString("F2");
            return newVal;
        }

        if (isActive && float.TryParse(_inputBuffer, out float cur))
            return cur;
        return value;
    }

    /// <summary>
    /// Draw a checkbox/boolean toggle
    /// </summary>
    public bool DrawCheckbox(string label, bool value, int x, int y)
    {
        int boxSize = 16;
        var boxRect = new Rectangle(x, y + 2, boxSize, boxSize);
        bool hovered = boxRect.Contains(_mouse.X, _mouse.Y);
        bool clicked = hovered && _mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released;

        DrawRect(boxRect, InputBg);
        DrawBorder(boxRect, hovered ? InputActive : InputBorder);

        if (value)
        {
            DrawRect(new Rectangle(x + 3, y + 5, boxSize - 6, boxSize - 6), AccentColor);
        }

        DrawText(label, new Vector2(x + boxSize + 6, y + 2), TextColor);

        return clicked ? !value : value;
    }

    /// <summary>
    /// Draw a dropdown/combo for string enum values
    /// </summary>
    public string DrawCombo(string fieldId, string label, string value, string[] options, int x, int y, int w)
    {
        int labelW = 120;
        DrawText(label, new Vector2(x, y + 2), TextDim);

        int inputX = x + labelW;
        int inputW = w - labelW;
        int inputH = 20;
        var inputRect = new Rectangle(inputX, y, inputW, inputH);
        bool hovered = inputRect.Contains(_mouse.X, _mouse.Y);
        bool isOpen = _activeFieldId == fieldId + "_combo";

        // Draw the current value
        DrawRect(inputRect, hovered || isOpen ? ItemHover : InputBg);
        DrawBorder(inputRect, isOpen ? InputActive : InputBorder);
        DrawText(value, new Vector2(inputX + 3, y + 2), TextColor);
        DrawText("v", new Vector2(inputX + inputW - 14, y + 2), TextDim);

        if (hovered && _mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released)
        {
            _activeFieldId = isOpen ? null : fieldId + "_combo";
            return value;
        }

        // Draw dropdown if open
        if (isOpen)
        {
            int dropH = Math.Min(options.Length * 20, 200);
            int dropY = y + inputH;

            DrawRect(new Rectangle(inputX, dropY, inputW, dropH), PanelBg);
            DrawBorder(new Rectangle(inputX, dropY, inputW, dropH), PanelBorder);

            for (int i = 0; i < options.Length && i * 20 < dropH; i++)
            {
                var optRect = new Rectangle(inputX, dropY + i * 20, inputW, 20);
                bool optHovered = optRect.Contains(_mouse.X, _mouse.Y);
                bool selected = options[i] == value;

                if (optHovered || selected)
                    DrawRect(optRect, optHovered ? ItemHover : ItemSelected);

                DrawText(options[i], new Vector2(inputX + 3, dropY + i * 20 + 2), selected ? TextBright : TextColor);

                if (optHovered && _mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released)
                {
                    _activeFieldId = null;
                    return options[i];
                }
            }
        }

        return value;
    }

    /// <summary>
    /// Draw an HdrColor editor (R,G,B,A sliders + intensity)
    /// </summary>
    public (Core.HdrColor color, int height) DrawHdrColorField(string fieldId, string label, Core.HdrColor color, int x, int y, int w)
    {
        DrawText(label, new Vector2(x, y + 2), TextDim);
        int rowH = 22;
        int curY = y + rowH;

        // Color preview swatch
        var previewColor = color.ToScaledColor();
        DrawRect(new Rectangle(x + 120, y, 40, 18), previewColor);
        DrawBorder(new Rectangle(x + 120, y, 40, 18), InputBorder);

        // R, G, B, A sliders
        int newR = DrawIntField(fieldId + "_r", "  R", color.R, x, curY, w);
        color.R = (byte)Math.Clamp(newR, 0, 255);
        curY += rowH;

        int newG = DrawIntField(fieldId + "_g", "  G", color.G, x, curY, w);
        color.G = (byte)Math.Clamp(newG, 0, 255);
        curY += rowH;

        int newB = DrawIntField(fieldId + "_b", "  B", color.B, x, curY, w);
        color.B = (byte)Math.Clamp(newB, 0, 255);
        curY += rowH;

        int newA = DrawIntField(fieldId + "_a", "  A", color.A, x, curY, w);
        color.A = (byte)Math.Clamp(newA, 0, 255);
        curY += rowH;

        float newI = DrawFloatField(fieldId + "_i", "  Intensity", color.Intensity, x, curY, w, 0.1f);
        color.Intensity = Math.Max(0, newI);
        curY += rowH;

        return (color, curY - y);
    }

    /// <summary>
    /// Draw a search/filter text field (no label, full width)
    /// </summary>
    public string DrawSearchField(string fieldId, string value, int x, int y, int w)
    {
        int inputH = 22;
        var inputRect = new Rectangle(x, y, w, inputH);
        bool isActive = _activeFieldId == fieldId;
        bool hovered = inputRect.Contains(_mouse.X, _mouse.Y);

        if (hovered && _mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released)
        {
            _activeFieldId = fieldId;
            _inputBuffer = value;
            _cursorBlink = 0;
            isActive = true;
        }
        else if (isActive && _mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released && !hovered)
        {
            _activeFieldId = null;
            return _inputBuffer;
        }

        DrawRect(inputRect, InputBg);
        DrawBorder(inputRect, isActive ? InputActive : InputBorder);

        string displayText = isActive ? _inputBuffer : value;
        if (string.IsNullOrEmpty(displayText) && !isActive)
            DrawText("Search...", new Vector2(x + 3, y + 3), new Color(80, 80, 100));
        else
            DrawText(displayText, new Vector2(x + 3, y + 3), isActive ? TextBright : TextColor);

        if (isActive && ((int)(_cursorBlink * 2) % 2 == 0))
        {
            float cursorX = x + 3 + MeasureText(displayText).X;
            DrawRect(new Rectangle((int)cursorX, y + 3, 1, inputH - 6), TextBright);
        }

        return isActive ? _inputBuffer : value;
    }

    // === Text Input Handling ===

    private void HandleTextInput(GameTime gameTime)
    {
        if (_activeFieldId == null) return;
        // Skip combo dropdowns
        if (_activeFieldId.EndsWith("_combo")) return;

        var pressed = _kb.GetPressedKeys();
        double dt = gameTime.ElapsedGameTime.TotalSeconds;
        bool shift = _kb.IsKeyDown(Keys.LeftShift) || _kb.IsKeyDown(Keys.RightShift);

        foreach (var key in pressed)
        {
            bool justPressed = _prevKb.IsKeyUp(key);
            bool repeat = false;

            if (!justPressed && key == _lastRepeatingKey)
            {
                _keyRepeatTimer += dt;
                if (_keyRepeatTimer > 0.4)
                {
                    _keyRepeatTimer -= 0.03;
                    repeat = true;
                }
            }

            if (!justPressed && !repeat) continue;

            if (justPressed)
            {
                _lastRepeatingKey = key;
                _keyRepeatTimer = 0;
            }

            if (key == Keys.Back && _inputBuffer.Length > 0)
            {
                _inputBuffer = _inputBuffer[..^1];
                _cursorBlink = 0;
            }
            else if (key == Keys.Enter || key == Keys.Tab)
            {
                _activeFieldId = null;
                return;
            }
            else if (key == Keys.Escape)
            {
                _activeFieldId = null;
                return;
            }
            else
            {
                char? c = KeyToChar(key, shift);
                if (c.HasValue)
                {
                    _inputBuffer += c.Value;
                    _cursorBlink = 0;
                }
            }
        }
    }

    private static char? KeyToChar(Keys key, bool shift)
    {
        if (key >= Keys.A && key <= Keys.Z)
        {
            char c = (char)('a' + (key - Keys.A));
            return shift ? char.ToUpper(c) : c;
        }
        if (key >= Keys.D0 && key <= Keys.D9)
        {
            if (shift)
            {
                return (key - Keys.D0) switch
                {
                    1 => '!', 2 => '@', 3 => '#', 4 => '$', 5 => '%',
                    6 => '^', 7 => '&', 8 => '*', 9 => '(', 0 => ')',
                    _ => null
                };
            }
            return (char)('0' + (key - Keys.D0));
        }
        if (key >= Keys.NumPad0 && key <= Keys.NumPad9)
            return (char)('0' + (key - Keys.NumPad0));

        return key switch
        {
            Keys.Space => ' ',
            Keys.OemPeriod => shift ? '>' : '.',
            Keys.OemComma => shift ? '<' : ',',
            Keys.OemMinus => shift ? '_' : '-',
            Keys.OemPlus => shift ? '+' : '=',
            Keys.OemQuestion => shift ? '?' : '/',
            Keys.OemSemicolon => shift ? ':' : ';',
            Keys.OemQuotes => shift ? '"' : '\'',
            Keys.OemOpenBrackets => shift ? '{' : '[',
            Keys.OemCloseBrackets => shift ? '}' : ']',
            Keys.OemBackslash => shift ? '|' : '\\',
            Keys.OemTilde => shift ? '~' : '`',
            Keys.Decimal => '.',
            Keys.Divide => '/',
            Keys.Multiply => '*',
            Keys.Subtract => '-',
            Keys.Add => '+',
            _ => null
        };
    }

    /// <summary>
    /// Returns true if any text input field is currently active (to suppress game input)
    /// </summary>
    public bool IsTextInputActive => _activeFieldId != null && !_activeFieldId.EndsWith("_combo");

    /// <summary>
    /// Deactivate any active text field
    /// </summary>
    public void ClearActiveField() { _activeFieldId = null; }
}
