using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Necroking.Core;

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
    internal SpriteBatch _sb = null!;
    public SpriteBatch SpriteBatch => _sb;
    internal Texture2D _pixel = null!;
    protected SpriteFont? _font;
    protected SpriteFont? _smallFont;
    protected SpriteFont? _largeFont;
    internal GraphicsDevice _gd = null!;
    internal MouseState _mouse;
    internal MouseState _prevMouse;
    internal KeyboardState _kb;
    internal KeyboardState _prevKb;
    protected int _screenW, _screenH;

    // INF02: Global scroll consumed flag
    private bool _scrollConsumed;
    public bool IsScrollConsumed => _scrollConsumed;
    public void ConsumeScroll() => _scrollConsumed = true;

    // INF03: Global mouse-over-UI flag
    private bool _mouseOverEditorUI;
    public bool IsMouseOverUI => _mouseOverEditorUI;
    public void SetMouseOverUI() => _mouseOverEditorUI = true;

    // Text input state
    protected string? _activeFieldId;
    private string _inputBuffer = "";
    private float _cursorBlink;
    private double _keyRepeatTimer;
    private Keys _lastRepeatingKey;

    // Cursor and selection state (shared by single-line and text area fields)
    private int _cursorPos;           // cursor position within _inputBuffer
    private int _selectionStart = -1; // start of selection (-1 = no selection)
    private bool _selectAll;          // select-all flag set on focus
    private bool _draggingSelection;  // mouse drag in progress for selection
    private int _activeFieldInputX;   // left edge of the active input rect (for click-to-position)
    private int _activeFieldInputW;   // width of the active input rect

    // INF09: Multi-line text area state
    private int _textAreaScrollOffset;
    private int _textAreaCursorPos;

    // Scroll state (per-panel, keyed by panel ID)
    private readonly Dictionary<string, float> _scrollOffsets = new();

    /// <summary>
    /// Get the current scroll offset for a given panel, or 0 if not found.
    /// </summary>
    public float GetScrollOffset(string panelId) =>
        _scrollOffsets.TryGetValue(panelId, out float v) ? v : 0f;

    // Scissor clipping stack
    private readonly Stack<Rectangle> _scissorStack = new();
    private static readonly RasterizerState _scissorRasterState = new() { ScissorTestEnable = true };

    // Input layer system (0=main, 1=popup, 2=dropdown, 3=confirm dialog)
    private int _inputLayer;
    public int InputLayer { get => _inputLayer; set => _inputLayer = value; }
    public bool IsInputBlocked(int layer) => _inputLayer > layer;

    // Combo dropdown scroll state (keyed by fieldId)
    private readonly Dictionary<string, int> _comboScrollOffsets = new();

    // Combo filter text per-dropdown (keyed by fieldId)
    private readonly Dictionary<string, string> _comboFilterText = new();

    // Combo keyboard highlight index (-1 = no highlight)
    private int _comboHighlightIdx = -1;

    // Deferred dropdown overlay data
    private struct PendingDropdown
    {
        public string FieldId;
        public string[] Options;        // full option list (may include "(none)")
        public string[] FilteredOptions; // after applying search filter
        public int[] FilteredIndices;    // indices into Options for each filtered item
        public string CurrentValue;
        public Rectangle InputRect;
        public int ScrollOffset;
        public int MaxScroll;
        public bool NeedsScroll;
        public int VisibleCount;
        public int DropY;
        public bool FlippedUp; // INF11: dropdown flipped upward
        public bool ShowFilter; // whether the filter text box is shown
        public string FilterText; // current filter string
        public int HighlightIdx; // keyboard highlight index into FilteredOptions
    }
    private PendingDropdown? _pendingDropdown;
    private bool _dropdownOverlayConsumedClick;

    // Color picker popup (shared instance)
    private readonly ColorPickerPopup _colorPicker = new();

    public void SetContext(SpriteBatch sb, Texture2D pixel, SpriteFont? font, SpriteFont? smallFont, SpriteFont? largeFont)
    {
        _sb = sb;
        _pixel = pixel;
        _font = font;
        _smallFont = smallFont;
        _largeFont = largeFont;
        _gd = sb.GraphicsDevice;
        _colorPicker.SetContext(sb, pixel, font, smallFont);
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

        // Reset per-frame flags
        // If a dropdown is open from last frame, pre-set input layer to block all widgets
        // This prevents widgets drawn BEFORE the combo from stealing clicks
        bool dropdownWasOpen = _activeFieldId != null && _activeFieldId.EndsWith("_combo");
        _inputLayer = dropdownWasOpen ? 2 : 0;
        _scrollConsumed = false;
        _mouseOverEditorUI = false;
        _pendingDropdown = null;
        _dropdownOverlayConsumedClick = false;

        // Update color picker popup input (with keyboard for value box editing)
        float dt = (float)gameTime.ElapsedGameTime.TotalSeconds;
        _colorPicker.UpdateInput(mouse, prevMouse, kb, prevKb, screenW, screenH, dt);

        // Handle key repeat for text input
        if (_activeFieldId != null)
            HandleTextInput(gameTime);
    }

    // === Drawing primitives ===

    public void DrawRect(Rectangle rect, Color color)
    {
        _sb.Draw(_pixel, rect, color);
    }

    /// <summary>
    /// Draw a line between two screen-space points using a rotated 1-pixel texture.
    /// </summary>
    public void DrawLine(Vector2 a, Vector2 b, Color color, int thickness = 1)
    {
        var diff = b - a;
        float length = diff.Length();
        if (length < 0.5f) return;
        float angle = MathF.Atan2(diff.Y, diff.X);
        _sb.Draw(_pixel, a, null, color, angle, Vector2.Zero, new Vector2(length, thickness), SpriteEffects.None, 0f);
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
        if (string.IsNullOrEmpty(text)) return;
        var f = font ?? _smallFont ?? _font;
        if (f != null)
        {
            // Round to integer pixels to avoid sub-pixel artifacts with PointClamp sampling
            pos = new Vector2((int)pos.X, (int)pos.Y);
            try { _sb.DrawString(f, text, pos, color); }
            catch { _sb.DrawString(f, SanitizeForFont(text, f), pos, color); }
        }
    }

    public Vector2 MeasureText(string text, SpriteFont? font = null)
    {
        if (string.IsNullOrEmpty(text)) return Vector2.Zero;
        var f = font ?? _smallFont ?? _font;
        if (f == null) return Vector2.Zero;
        try { return f.MeasureString(text); }
        catch { return f.MeasureString(SanitizeForFont(text, f)); }
    }

    /// <summary>Remove characters not supported by the font.</summary>
    private static string SanitizeForFont(string text, SpriteFont font)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (font.Characters.Contains(c) || c == '\n' || c == '\r')
                sb.Append(c);
            else
                sb.Append('?');
        }
        return sb.ToString();
    }

    /// <summary>Find character index from pixel X position within a text string.</summary>
    private int CharIndexFromPixelX(string text, int pixelX)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        for (int i = 1; i <= text.Length; i++)
        {
            float w = MeasureText(text[..i]).X;
            if (pixelX < w - MeasureText(text[(i-1)..i]).X * 0.5f)
                return i - 1;
        }
        return text.Length;
    }

    /// <summary>Check if there's an active text selection.</summary>
    private bool HasSelection => _selectionStart >= 0 && _selectionStart != _cursorPos;

    /// <summary>Get ordered selection range (min, max).</summary>
    private (int start, int end) GetSelectionRange()
    {
        int a = Math.Min(_selectionStart, _cursorPos);
        int b = Math.Max(_selectionStart, _cursorPos);
        return (a, b);
    }

    /// <summary>Delete selected text and collapse cursor.</summary>
    private void DeleteSelection()
    {
        if (!HasSelection) return;
        var (start, end) = GetSelectionRange();
        _inputBuffer = _inputBuffer[..start] + _inputBuffer[end..];
        _cursorPos = start;
        _selectionStart = -1;
        _selectAll = false;
    }

    public void DrawTexture(Texture2D texture, Vector2 position, Rectangle sourceRect,
        Color color, float rotation, Vector2 origin, float scale, SpriteEffects effects)
    {
        _sb.Draw(texture, position, sourceRect, color, rotation, origin, scale, effects, 0f);
    }

    // === Scissor Clipping ===

    /// <summary>
    /// Begin a scissor-clipped region. Ends the current SpriteBatch, sets the scissor rectangle,
    /// and begins a new SpriteBatch with ScissorTestEnable. Supports nesting via a stack.
    /// </summary>
    public void BeginClip(Rectangle clipRect)
    {
        // End the current SpriteBatch pass
        _sb.End();

        // Push the current scissor rect onto the stack (for nesting)
        _scissorStack.Push(_gd.ScissorRectangle);

        // Set the new scissor rectangle
        _gd.ScissorRectangle = clipRect;

        // Begin a new SpriteBatch with scissor test enabled
        _sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp,
            null, _scissorRasterState);
    }

    /// <summary>
    /// End a scissor-clipped region. Restores the previous scissor rectangle from the stack.
    /// </summary>
    public void EndClip()
    {
        // End the scissor-enabled SpriteBatch pass
        _sb.End();

        // Restore the previous scissor rectangle
        if (_scissorStack.Count > 0)
            _gd.ScissorRectangle = _scissorStack.Pop();

        // Resume the normal SpriteBatch (matching the HUD pass in Game1.Draw)
        if (_scissorStack.Count > 0)
        {
            // Still nested - re-enable scissor with the parent clip rect
            _sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp,
                null, _scissorRasterState);
        }
        else
        {
            // Back to normal - no scissor test
            _sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
        }
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

    public bool DrawButton(string text, int x, int y, int w, int h, Color? bgOverride = null, int layer = 0)
    {
        var rect = new Rectangle(x, y, w, h);
        bool hovered = !IsInputBlocked(layer) && rect.Contains(_mouse.X, _mouse.Y);
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
        bool inputBlocked = IsInputBlocked(0);

        // Clip region
        var clipRect = new Rectangle(x, y, w, h);
        DrawRect(clipRect, new Color(20, 20, 35, 200));

        if (!_scrollOffsets.ContainsKey(panelId))
            _scrollOffsets[panelId] = 0;

        float scroll = _scrollOffsets[panelId];
        int itemH = 22;
        int clicked = -1;

        // Handle scroll wheel when hovering
        if (!inputBlocked && !_scrollConsumed && clipRect.Contains(_mouse.X, _mouse.Y))
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
                ConsumeScroll();
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
            bool hovered = !inputBlocked && itemRect.Contains(_mouse.X, _mouse.Y) && clipRect.Contains(_mouse.X, _mouse.Y);

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
        bool hovered = !IsInputBlocked(0) && inputRect.Contains(_mouse.X, _mouse.Y);

        // Click to activate (or reposition cursor if already active)
        if (hovered && _mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released)
        {
            if (!isActive)
            {
                // First click: activate with select-all
                _activeFieldId = fieldId;
                _inputBuffer = value;
                _cursorPos = value.Length;
                _selectionStart = 0;
                _selectAll = true;
                _cursorBlink = 0;
                _activeFieldInputX = inputX + 3;
                _activeFieldInputW = inputW - 6;
                isActive = true;
            }
            else
            {
                // Click in already-active field: position cursor, start drag selection
                int clickPos = CharIndexFromPixelX(_inputBuffer, _mouse.X - inputX - 3);
                _cursorPos = clickPos;
                _selectionStart = clickPos;
                _selectAll = false;
                _draggingSelection = true;
                _cursorBlink = 0;
            }
        }
        // Drag to extend selection
        else if (isActive && _draggingSelection && _mouse.LeftButton == ButtonState.Pressed)
        {
            int dragPos = CharIndexFromPixelX(_inputBuffer, _mouse.X - inputX - 3);
            _cursorPos = dragPos;
            _cursorBlink = 0;
        }
        // Release drag
        else if (_draggingSelection && _mouse.LeftButton == ButtonState.Released)
        {
            _draggingSelection = false;
            // If start == cursor, clear selection
            if (_selectionStart == _cursorPos)
                _selectionStart = -1;
        }
        // Click elsewhere to deactivate
        else if (isActive && _mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released && !hovered)
        {
            _activeFieldId = null;
            _selectAll = false;
            _selectionStart = -1;
            _draggingSelection = false;
            return _inputBuffer;
        }

        DrawRect(inputRect, InputBg);
        DrawBorder(inputRect, isActive ? InputActive : InputBorder);

        string displayText = isActive ? _inputBuffer : value;

        // Draw selection highlight
        if (isActive && HasSelection)
        {
            var (selStart, selEnd) = GetSelectionRange();
            float selX1 = MeasureText(displayText[..selStart]).X;
            float selX2 = MeasureText(displayText[..selEnd]).X;
            DrawRect(new Rectangle(inputX + 3 + (int)selX1, y + 2, (int)(selX2 - selX1), inputH - 4),
                new Color(80, 120, 200, 140));
        }

        DrawText(displayText, new Vector2(inputX + 3, y + 2), isActive ? TextBright : TextColor);

        // Draw cursor
        if (isActive && !HasSelection && ((int)(_cursorBlink * 2) % 2 == 0))
        {
            float cursorPixelX = inputX + 3 + MeasureText(displayText[.._cursorPos]).X;
            DrawRect(new Rectangle((int)cursorPixelX, y + 2, 1, inputH - 4), TextBright);
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
        bool hovered = !IsInputBlocked(0) && inputRect.Contains(_mouse.X, _mouse.Y);

        if (hovered && _mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released)
        {
            _activeFieldId = fieldId;
            _inputBuffer = value.ToString();
            _cursorPos = _inputBuffer.Length;
            _selectionStart = 0;
            _selectAll = true;
            _cursorBlink = 0;
            isActive = true;
        }
        else if (isActive && _mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released && !hovered)
        {
            _activeFieldId = null;
            _selectAll = false;
            _selectionStart = -1;
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
        bool hovered = !IsInputBlocked(0) && inputRect.Contains(_mouse.X, _mouse.Y);

        if (hovered && _mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released)
        {
            _activeFieldId = fieldId;
            _inputBuffer = value.ToString("F2");
            _cursorPos = _inputBuffer.Length;
            _selectionStart = 0;
            _selectAll = true;
            _cursorBlink = 0;
            isActive = true;
        }
        else if (isActive && _mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released && !hovered)
        {
            _activeFieldId = null;
            _selectAll = false;
            _selectionStart = -1;
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
        bool hovered = !IsInputBlocked(0) && boxRect.Contains(_mouse.X, _mouse.Y);
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
    /// Draw a dropdown/combo for string enum values.
    /// Supports scrolling, search filtering (auto for 15+ items), keyboard navigation,
    /// max-height capping (40% screen), and an optional "(none)" entry.
    /// </summary>
    private const int ComboItemH = 20;
    private const int ComboFilterH = 22; // height of the filter text box row

    // Highlight color for keyboard navigation (distinct from hover and selected)
    private static readonly Color ComboHighlight = new(70, 90, 140, 240);

    public string DrawCombo(string fieldId, string label, string value, string[] options,
        int x, int y, int w, bool allowNone = false)
    {
        int labelW = 120;
        DrawText(label, new Vector2(x, y + 2), TextDim);

        int inputX = x + labelW;
        int inputW = w - labelW;
        int inputH = 20;
        var inputRect = new Rectangle(inputX, y, inputW, inputH);

        // Build effective option list (prepend "(none)" when allowed)
        string[] effectiveOptions;
        if (allowNone)
        {
            effectiveOptions = new string[options.Length + 1];
            effectiveOptions[0] = "(none)";
            Array.Copy(options, 0, effectiveOptions, 1, options.Length);
        }
        else
        {
            effectiveOptions = options;
        }

        // Map display value: if allowNone and value is empty, show "(none)"
        string displayValue = value;
        if (allowNone && string.IsNullOrEmpty(value))
            displayValue = "(none)";

        // If a dropdown overlay consumed the click this frame, block combo button interaction
        string comboId = fieldId + "_combo";
        bool isOpen = _activeFieldId == comboId;
        // The combo button itself is always clickable when it's the open dropdown (to allow closing)
        bool hovered = !_dropdownOverlayConsumedClick && (isOpen || !IsInputBlocked(0)) && inputRect.Contains(_mouse.X, _mouse.Y);

        // Draw the current value
        DrawRect(inputRect, hovered || isOpen ? ItemHover : InputBg);
        DrawBorder(inputRect, isOpen ? InputActive : InputBorder);
        DrawText(displayValue, new Vector2(inputX + 3, y + 2), TextColor);
        DrawText("v", new Vector2(inputX + inputW - 14, y + 2), TextDim);

        if (hovered && _mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released)
        {
            if (isOpen)
            {
                _activeFieldId = null;
                _comboFilterText.Remove(comboId);
                _comboHighlightIdx = -1;
            }
            else
            {
                _activeFieldId = comboId;
                _comboFilterText[comboId] = "";
                _comboHighlightIdx = -1;

                // Compute max visible based on 40% screen height
                int maxDropH = (int)(_screenH * 0.4f);
                int maxVisible = Math.Max(1, maxDropH / ComboItemH);
                int visCount = Math.Min(effectiveOptions.Length, maxVisible);

                // Reset scroll to center the selected item
                int selectedIdx = Array.IndexOf(effectiveOptions, displayValue);
                if (selectedIdx >= 0 && effectiveOptions.Length > visCount)
                    _comboScrollOffsets[comboId] = Math.Max(0, Math.Min(selectedIdx - visCount / 2, effectiveOptions.Length - visCount));
                else
                    _comboScrollOffsets[comboId] = 0;
            }
            return value;
        }

        // When open: handle input inline (so DrawCombo can return the selection),
        // but defer visual rendering to DrawDropdownOverlays()
        if (isOpen)
        {
            // Set input layer to prevent click-through to widgets behind the dropdown
            if (_inputLayer < 2) _inputLayer = 2;

            // Filter support: auto-enable when 15+ items
            bool showFilter = effectiveOptions.Length >= 15;
            if (!_comboFilterText.ContainsKey(comboId))
                _comboFilterText[comboId] = "";
            string filterText = _comboFilterText[comboId];

            // Build filtered list
            string[] filteredOptions;
            int[] filteredIndices;
            if (showFilter && !string.IsNullOrEmpty(filterText))
            {
                var fOpts = new List<string>();
                var fIdxs = new List<int>();
                for (int oi = 0; oi < effectiveOptions.Length; oi++)
                {
                    if (effectiveOptions[oi].Contains(filterText, StringComparison.OrdinalIgnoreCase))
                    {
                        fOpts.Add(effectiveOptions[oi]);
                        fIdxs.Add(oi);
                    }
                }
                filteredOptions = fOpts.ToArray();
                filteredIndices = fIdxs.ToArray();
            }
            else
            {
                filteredOptions = effectiveOptions;
                filteredIndices = new int[effectiveOptions.Length];
                for (int oi = 0; oi < effectiveOptions.Length; oi++) filteredIndices[oi] = oi;
            }

            // Compute max visible based on 40% screen height
            int maxDropH = (int)(_screenH * 0.4f);
            int filterRowH = showFilter ? ComboFilterH : 0;
            int maxVisibleItems = Math.Max(1, (maxDropH - filterRowH) / ComboItemH);
            int visibleCount = Math.Min(filteredOptions.Length, maxVisibleItems);
            bool needsScroll = filteredOptions.Length > maxVisibleItems;

            // Get or init scroll offset
            if (!_comboScrollOffsets.ContainsKey(comboId))
                _comboScrollOffsets[comboId] = 0;
            int scrollOffset = _comboScrollOffsets[comboId];
            int maxScroll = Math.Max(0, filteredOptions.Length - visibleCount);
            scrollOffset = Math.Clamp(scrollOffset, 0, maxScroll);

            int dropH = visibleCount * ComboItemH + filterRowH;

            // INF11: Flip dropdown upward if it would extend below the screen
            int dropY = y + inputH;
            bool flippedUp = false;
            if (dropY + dropH > _screenH)
            {
                dropY = y - dropH;
                flippedUp = true;
                if (dropY < 0) dropY = 0;
            }
            var dropRect = new Rectangle(inputX, dropY, inputW, dropH);

            // Keyboard navigation (when the dropdown is open)
            {
                bool arrowDown = _kb.IsKeyDown(Keys.Down) && _prevKb.IsKeyUp(Keys.Down);
                bool arrowUp = _kb.IsKeyDown(Keys.Up) && _prevKb.IsKeyUp(Keys.Up);
                bool enterKey = _kb.IsKeyDown(Keys.Enter) && _prevKb.IsKeyUp(Keys.Enter);
                bool escapeKey = _kb.IsKeyDown(Keys.Escape) && _prevKb.IsKeyUp(Keys.Escape);

                if (escapeKey)
                {
                    _activeFieldId = null;
                    _comboFilterText.Remove(comboId);
                    _comboHighlightIdx = -1;
                    _dropdownOverlayConsumedClick = true;
                    return value;
                }

                if (arrowDown)
                {
                    _comboHighlightIdx++;
                    if (_comboHighlightIdx >= filteredOptions.Length) _comboHighlightIdx = 0;
                    // Scroll to keep highlight visible
                    if (_comboHighlightIdx >= scrollOffset + visibleCount)
                        scrollOffset = _comboHighlightIdx - visibleCount + 1;
                    else if (_comboHighlightIdx < scrollOffset)
                        scrollOffset = _comboHighlightIdx;
                    scrollOffset = Math.Clamp(scrollOffset, 0, maxScroll);
                    _comboScrollOffsets[comboId] = scrollOffset;
                }
                if (arrowUp)
                {
                    _comboHighlightIdx--;
                    if (_comboHighlightIdx < 0) _comboHighlightIdx = filteredOptions.Length - 1;
                    if (_comboHighlightIdx < scrollOffset)
                        scrollOffset = _comboHighlightIdx;
                    else if (_comboHighlightIdx >= scrollOffset + visibleCount)
                        scrollOffset = _comboHighlightIdx - visibleCount + 1;
                    scrollOffset = Math.Clamp(scrollOffset, 0, maxScroll);
                    _comboScrollOffsets[comboId] = scrollOffset;
                }
                if (enterKey && _comboHighlightIdx >= 0 && _comboHighlightIdx < filteredOptions.Length)
                {
                    string picked = filteredOptions[_comboHighlightIdx];
                    _activeFieldId = null;
                    _comboFilterText.Remove(comboId);
                    _comboHighlightIdx = -1;
                    _dropdownOverlayConsumedClick = true;
                    if (allowNone && picked == "(none)") return "";
                    return picked;
                }

                // Filter text input via keyboard (only when filter is shown)
                if (showFilter)
                {
                    bool shift = _kb.IsKeyDown(Keys.LeftShift) || _kb.IsKeyDown(Keys.RightShift);
                    foreach (var key in _kb.GetPressedKeys())
                    {
                        if (_prevKb.IsKeyUp(key))
                        {
                            if (key == Keys.Back && filterText.Length > 0)
                            {
                                filterText = filterText[..^1];
                                _comboFilterText[comboId] = filterText;
                                _comboHighlightIdx = -1;
                                // Re-filter will happen next frame; reset scroll
                                _comboScrollOffsets[comboId] = 0;
                            }
                            else
                            {
                                char? c = KeyToChar(key, shift);
                                if (c.HasValue)
                                {
                                    filterText += c.Value;
                                    _comboFilterText[comboId] = filterText;
                                    _comboHighlightIdx = -1;
                                    _comboScrollOffsets[comboId] = 0;
                                }
                            }
                        }
                    }
                }
            }

            // The items area starts below the filter row (if present)
            int itemsY = dropY + filterRowH;

            // Handle mouse wheel scrolling within the dropdown + consume scroll
            if (dropRect.Contains(_mouse.X, _mouse.Y))
            {
                ConsumeScroll();

                if (needsScroll)
                {
                    int scrollDelta = _mouse.ScrollWheelValue - _prevMouse.ScrollWheelValue;
                    if (scrollDelta != 0)
                    {
                        scrollOffset -= scrollDelta > 0 ? 1 : -1;
                        scrollOffset = Math.Clamp(scrollOffset, 0, maxScroll);
                        _comboScrollOffsets[comboId] = scrollOffset;
                    }
                }
            }

            // Check for option clicks (input handling -- no drawing)
            for (int vi = 0; vi < visibleCount; vi++)
            {
                int fi = vi + scrollOffset;
                if (fi >= filteredOptions.Length) break;

                var optRect = new Rectangle(inputX, itemsY + vi * ComboItemH, inputW, ComboItemH);
                bool optHovered = optRect.Contains(_mouse.X, _mouse.Y);

                if (optHovered && _mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released)
                {
                    string picked = filteredOptions[fi];
                    _activeFieldId = null;
                    _comboFilterText.Remove(comboId);
                    _comboHighlightIdx = -1;
                    _dropdownOverlayConsumedClick = true;
                    if (allowNone && picked == "(none)") return "";
                    return picked;
                }
            }

            // Close dropdown if clicking outside both the combo button and dropdown
            if (_mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released
                && !dropRect.Contains(_mouse.X, _mouse.Y) && !inputRect.Contains(_mouse.X, _mouse.Y))
            {
                _activeFieldId = null;
                _comboFilterText.Remove(comboId);
                _comboHighlightIdx = -1;
                _dropdownOverlayConsumedClick = true;
            }

            // Save for deferred rendering (visual only)
            _pendingDropdown = new PendingDropdown
            {
                FieldId = comboId,
                Options = effectiveOptions,
                FilteredOptions = filteredOptions,
                FilteredIndices = filteredIndices,
                CurrentValue = displayValue,
                InputRect = inputRect,
                ScrollOffset = scrollOffset,
                MaxScroll = maxScroll,
                NeedsScroll = needsScroll,
                VisibleCount = visibleCount,
                DropY = dropY,
                FlippedUp = flippedUp,
                ShowFilter = showFilter,
                FilterText = filterText,
                HighlightIdx = _comboHighlightIdx,
            };
        }

        return value;
    }

    /// <summary>
    /// Draw all pending dropdown overlays on top of everything.
    /// Must be called at the very end of an editor's Draw method,
    /// after all panels, popups, etc., so the dropdown renders at the highest z-level.
    /// </summary>
    public void DrawDropdownOverlays()
    {
        if (_pendingDropdown == null) return;

        var dd = _pendingDropdown.Value;
        int inputX = dd.InputRect.X;
        int inputW = dd.InputRect.Width;
        int filterRowH = dd.ShowFilter ? ComboFilterH : 0;
        int itemsDropH = dd.VisibleCount * ComboItemH;
        int dropH = itemsDropH + filterRowH;
        var dropRect = new Rectangle(inputX, dd.DropY, inputW, dropH);
        int scrollOffset = dd.ScrollOffset;

        // INF11: Shadow behind the dropdown for visual separation
        var shadowRect = new Rectangle(dropRect.X + 3, dropRect.Y + 3, dropRect.Width, dropRect.Height);
        DrawRect(shadowRect, new Color(0, 0, 0, 100));

        // Background
        DrawRect(dropRect, PanelBg);
        // INF11: Stronger border (2px) to distinguish from content behind
        DrawBorder(dropRect, AccentColor, 2);

        // Draw filter text box at the top if applicable
        int itemsY = dd.DropY + filterRowH;
        if (dd.ShowFilter)
        {
            var filterRect = new Rectangle(inputX + 2, dd.DropY + 1, inputW - 4, ComboFilterH - 2);
            DrawRect(filterRect, InputBg);
            DrawBorder(filterRect, InputActive);
            string filterDisplay = string.IsNullOrEmpty(dd.FilterText) ? "Type to filter..." : dd.FilterText;
            Color filterColor = string.IsNullOrEmpty(dd.FilterText) ? TextDim : TextColor;
            DrawText(filterDisplay, new Vector2(inputX + 5, dd.DropY + 3), filterColor);
            // Blinking cursor
            if (!string.IsNullOrEmpty(dd.FilterText) || _cursorBlink % 1.0f < 0.5f)
            {
                float cursorX = inputX + 5 + MeasureText(dd.FilterText ?? "").X;
                if (_cursorBlink % 1.0f < 0.5f)
                    DrawRect(new Rectangle((int)cursorX, dd.DropY + 3, 1, 14), TextBright);
            }
        }

        // Use scissor clipping for clean edges on the items area
        var itemsClip = new Rectangle(inputX, itemsY, inputW, itemsDropH);
        BeginClip(itemsClip);

        for (int vi = 0; vi < dd.VisibleCount; vi++)
        {
            int fi = vi + scrollOffset;
            if (fi >= dd.FilteredOptions.Length) break;

            var optRect = new Rectangle(inputX, itemsY + vi * ComboItemH, inputW, ComboItemH);
            bool optHovered = optRect.Contains(_mouse.X, _mouse.Y);
            bool isSelected = dd.FilteredOptions[fi] == dd.CurrentValue;
            bool isHighlighted = fi == dd.HighlightIdx;

            if (isHighlighted)
                DrawRect(optRect, ComboHighlight);
            else if (optHovered || isSelected)
                DrawRect(optRect, optHovered ? ItemHover : ItemSelected);

            Color textCol = isSelected ? TextBright : (isHighlighted ? TextBright : TextColor);
            DrawText(dd.FilteredOptions[fi], new Vector2(inputX + 3, itemsY + vi * ComboItemH + 2), textCol);
        }

        EndClip();

        // Draw scrollbar indicator if scrollable
        if (dd.NeedsScroll)
        {
            float scrollRatio = dd.MaxScroll > 0 ? (float)scrollOffset / dd.MaxScroll : 0;
            int barH = Math.Max(12, itemsDropH * dd.VisibleCount / Math.Max(1, dd.FilteredOptions.Length));
            int barY = itemsY + (int)(scrollRatio * (itemsDropH - barH));
            DrawRect(new Rectangle(inputX + inputW - 6, barY, 5, barH), new Color(100, 100, 140, 180));
        }
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
    /// Draw a clickable color swatch that opens the HSV color picker popup.
    /// Returns true when OK is pressed (color was changed).
    /// </summary>
    public bool DrawColorSwatch(string id, int x, int y, int w, int h, ref HdrColor color, bool hideIntensity = false)
    {
        // If the color picker is open for this id, pass hideIntensity on open (already handled)
        // The ColorSwatch method handles drawing, click detection, and popup open/sync
        return _colorPicker.ColorSwatch(id, x, y, w, h, ref color);
    }

    /// <summary>
    /// Open the color picker popup for a specific color (alternative to swatch-based opening).
    /// </summary>
    public void OpenColorPicker(string id, HdrColor color, bool hideIntensity = false)
    {
        _colorPicker.Open(id, color, hideIntensity);
    }

    /// <summary>
    /// Draw the color picker popup overlay. Call this AFTER all other editor drawing
    /// so it renders on top of everything.
    /// </summary>
    public void DrawColorPickerPopup()
    {
        // Capture back buffer for eyedropper if dropper is active
        if (_colorPicker.IsDropperActive)
            _colorPicker.CaptureBackBuffer(_gd);
        _colorPicker.Draw();
    }

    /// <summary>
    /// Returns true if the color picker popup is currently open (to suppress other interactions).
    /// </summary>
    public bool IsColorPickerOpen => _colorPicker.IsOpen;
    public bool IsDropdownOpen => _activeFieldId != null && _activeFieldId.EndsWith("_combo");

    /// <summary>
    /// Returns true if the eyedropper mode is active.
    /// </summary>
    public bool IsDropperActive => _colorPicker.IsDropperActive;

    /// <summary>
    /// Activate the eyedropper tool on the color picker.
    /// </summary>
    public void OpenDropper()
    {
        _colorPicker.OpenDropper();
    }

    /// <summary>
    /// Draw a search/filter text field (no label, full width)
    /// </summary>
    public string DrawSearchField(string fieldId, string value, int x, int y, int w)
    {
        int inputH = 22;
        var inputRect = new Rectangle(x, y, w, inputH);
        bool isActive = _activeFieldId == fieldId;
        bool hovered = !IsInputBlocked(0) && inputRect.Contains(_mouse.X, _mouse.Y);

        if (hovered && _mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released)
        {
            _activeFieldId = fieldId;
            _inputBuffer = value;
            _cursorPos = value.Length;
            _selectionStart = 0;
            _selectAll = true;
            _cursorBlink = 0;
            isActive = true;
        }
        else if (isActive && _mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released && !hovered)
        {
            _activeFieldId = null;
            _selectAll = false;
            _selectionStart = -1;
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

    // === INF08: Slider + value box combo widget ===

    /// <summary>
    /// Draw a horizontal slider with a numeric value box to the right.
    /// Click the value box to type exact values.
    /// Returns the new float value.
    /// </summary>
    public float DrawSliderFloat(string id, string label, float value, float min, float max, int x, int y, int w)
    {
        int labelW = 120;
        DrawText(label, new Vector2(x, y + 2), TextDim);

        int sliderX = x + labelW;
        int valueBoxW = 50;
        int sliderW = w - labelW - valueBoxW - 4;
        int sliderH = 16;
        int trackY = y + 2;

        // -- Slider track --
        var trackRect = new Rectangle(sliderX, trackY, sliderW, sliderH);
        DrawRect(trackRect, InputBg);
        DrawBorder(trackRect, InputBorder);

        // Filled portion
        float t = (max > min) ? Math.Clamp((value - min) / (max - min), 0f, 1f) : 0f;
        int fillW = (int)(t * (sliderW - 4));
        if (fillW > 0)
            DrawRect(new Rectangle(sliderX + 2, trackY + 2, fillW, sliderH - 4), AccentColor);

        // Thumb
        int thumbX = sliderX + 2 + (int)(t * (sliderW - 4)) - 4;
        DrawRect(new Rectangle(thumbX, trackY, 8, sliderH), TextBright);

        // Slider interaction (drag)
        bool hovered = !IsInputBlocked(0) && trackRect.Contains(_mouse.X, _mouse.Y);
        if (hovered || (_mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Pressed))
        {
            if (_mouse.LeftButton == ButtonState.Pressed && _mouse.Y >= trackY - 4 && _mouse.Y <= trackY + sliderH + 4
                && _mouse.X >= sliderX - 4 && _mouse.X <= sliderX + sliderW + 4)
            {
                float newT = Math.Clamp((float)(_mouse.X - sliderX - 2) / (sliderW - 4), 0f, 1f);
                value = min + newT * (max - min);
                // Update input buffer if value box is active for this slider
                string valueFieldId = id + "_val";
                if (_activeFieldId == valueFieldId)
                    _inputBuffer = value.ToString("F2");
            }
        }

        // -- Value box (clickable text field) --
        string valueFieldId2 = id + "_val";
        int vbX = sliderX + sliderW + 4;
        var vbRect = new Rectangle(vbX, y, valueBoxW, 20);
        bool vbActive = _activeFieldId == valueFieldId2;
        bool vbHovered = !IsInputBlocked(0) && vbRect.Contains(_mouse.X, _mouse.Y);

        if (vbHovered && _mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released)
        {
            _activeFieldId = valueFieldId2;
            _inputBuffer = value.ToString("F2");
            _cursorBlink = 0;
            vbActive = true;
        }
        else if (vbActive && _mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released && !vbHovered)
        {
            _activeFieldId = null;
            if (float.TryParse(_inputBuffer, out float parsed))
                return Math.Clamp(parsed, min, max);
            return value;
        }

        DrawRect(vbRect, InputBg);
        DrawBorder(vbRect, vbActive ? InputActive : InputBorder);
        string vbText = vbActive ? _inputBuffer : value.ToString("F2");
        DrawText(vbText, new Vector2(vbX + 3, y + 2), vbActive ? TextBright : TextColor);

        if (vbActive && ((int)(_cursorBlink * 2) % 2 == 0))
        {
            float cursorX = vbX + 3 + MeasureText(vbText).X;
            DrawRect(new Rectangle((int)cursorX, y + 2, 1, 16), TextBright);
        }

        if (vbActive && float.TryParse(_inputBuffer, out float cur))
            return Math.Clamp(cur, min, max);

        return Math.Clamp(value, min, max);
    }

    // === INF09: Multi-line text area widget ===

    /// <summary>
    /// Draw a multi-line text area with word wrapping and scrolling.
    /// Click to edit, supports Enter for new lines.
    /// Returns the updated string.
    /// </summary>
    public string DrawTextArea(string id, string value, int x, int y, int w, int h)
    {
        var areaRect = new Rectangle(x, y, w, h);
        string areaFieldId = id + "_textarea";
        bool isActive = _activeFieldId == areaFieldId;
        bool hovered = !IsInputBlocked(0) && areaRect.Contains(_mouse.X, _mouse.Y);

        // Click to activate
        if (hovered && _mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released)
        {
            _activeFieldId = areaFieldId;
            _inputBuffer = value ?? "";
            _textAreaCursorPos = _inputBuffer.Length;
            _textAreaScrollOffset = 0;
            _cursorBlink = 0;
            isActive = true;
        }
        // Click outside to deactivate
        else if (isActive && _mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released && !hovered)
        {
            _activeFieldId = null;
            return _inputBuffer;
        }

        // Background
        DrawRect(areaRect, InputBg);
        DrawBorder(areaRect, isActive ? InputActive : InputBorder);

        string text = isActive ? _inputBuffer : (value ?? "");

        // Word-wrap the text into lines
        var lines = WrapText(text, w - 8);

        // Scroll handling
        int lineH = 16;
        int visibleLines = Math.Max(1, (h - 4) / lineH);
        int totalLines = lines.Count;

        if (isActive && !_scrollConsumed && hovered)
        {
            int scrollDelta = _mouse.ScrollWheelValue - _prevMouse.ScrollWheelValue;
            if (scrollDelta != 0)
            {
                _textAreaScrollOffset -= scrollDelta > 0 ? 1 : -1;
                _textAreaScrollOffset = Math.Clamp(_textAreaScrollOffset, 0, Math.Max(0, totalLines - visibleLines));
                ConsumeScroll();
            }
        }
        _textAreaScrollOffset = Math.Clamp(_textAreaScrollOffset, 0, Math.Max(0, totalLines - visibleLines));

        // Draw text lines
        int drawY = y + 2;
        for (int i = _textAreaScrollOffset; i < lines.Count && drawY < y + h - 2; i++)
        {
            DrawText(lines[i], new Vector2(x + 4, drawY), isActive ? TextBright : TextColor);
            drawY += lineH;
        }

        // Draw cursor
        if (isActive && ((int)(_cursorBlink * 2) % 2 == 0))
        {
            // Find cursor line and column
            int charCount = 0;
            int curLine = 0;
            int curCol = 0;
            for (int i = 0; i < lines.Count; i++)
            {
                if (charCount + lines[i].Length >= _textAreaCursorPos)
                {
                    curLine = i;
                    curCol = _textAreaCursorPos - charCount;
                    break;
                }
                charCount += lines[i].Length;
                if (i < lines.Count - 1) charCount++; // newline char
            }

            if (curLine >= _textAreaScrollOffset && curLine < _textAreaScrollOffset + visibleLines)
            {
                string beforeCursor = curCol <= lines[curLine].Length ? lines[curLine][..curCol] : lines[curLine];
                float cxPos = x + 4 + MeasureText(beforeCursor).X;
                int cyPos = y + 2 + (curLine - _textAreaScrollOffset) * lineH;
                DrawRect(new Rectangle((int)cxPos, cyPos, 1, lineH), TextBright);
            }
        }

        // Scrollbar
        if (totalLines > visibleLines)
        {
            float scrollRatio = totalLines > visibleLines ? (float)_textAreaScrollOffset / (totalLines - visibleLines) : 0;
            int barH = Math.Max(12, (h - 4) * visibleLines / totalLines);
            int barY = y + 2 + (int)(scrollRatio * (h - 4 - barH));
            DrawRect(new Rectangle(x + w - 6, barY, 5, barH), new Color(100, 100, 140, 180));
        }

        return isActive ? _inputBuffer : (value ?? "");
    }

    /// <summary>
    /// Word-wrap text to fit within the given pixel width.
    /// </summary>
    private List<string> WrapText(string text, int maxWidth)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(text)) { result.Add(""); return result; }

        // Split on explicit newlines first
        var paragraphs = text.Split('\n');
        foreach (var para in paragraphs)
        {
            if (string.IsNullOrEmpty(para)) { result.Add(""); continue; }

            var words = para.Split(' ');
            string currentLine = "";
            foreach (var word in words)
            {
                string candidate = string.IsNullOrEmpty(currentLine) ? word : currentLine + " " + word;
                if (MeasureText(candidate).X > maxWidth && !string.IsNullOrEmpty(currentLine))
                {
                    result.Add(currentLine);
                    currentLine = word;
                }
                else
                {
                    currentLine = candidate;
                }
            }
            result.Add(currentLine);
        }
        return result;
    }

    // === INF14: Status message alpha fade helper ===

    /// <summary>
    /// Get an alpha-faded color for status messages.
    /// Timer counts down from a positive value; the last 1.0 second fades out.
    /// </summary>
    public static Color FadeStatusColor(Color baseColor, float timer)
    {
        if (timer <= 0) return Color.Transparent;
        float alpha = timer < 1f ? timer : 1f;
        return baseColor * alpha;
    }

    // === Text Input Handling ===

    private void HandleTextInput(GameTime gameTime)
    {
        if (_activeFieldId == null) return;
        // Skip combo dropdowns
        if (_activeFieldId.EndsWith("_combo")) return;

        bool isTextArea = _activeFieldId.EndsWith("_textarea");

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

            if (isTextArea)
            {
                // Multi-line text area input handling
                _textAreaCursorPos = Math.Clamp(_textAreaCursorPos, 0, _inputBuffer.Length);

                if (key == Keys.Back && _textAreaCursorPos > 0)
                {
                    _inputBuffer = _inputBuffer[..(_textAreaCursorPos - 1)] + _inputBuffer[_textAreaCursorPos..];
                    _textAreaCursorPos--;
                    _cursorBlink = 0;
                }
                else if (key == Keys.Delete && _textAreaCursorPos < _inputBuffer.Length)
                {
                    _inputBuffer = _inputBuffer[.._textAreaCursorPos] + _inputBuffer[(_textAreaCursorPos + 1)..];
                    _cursorBlink = 0;
                }
                else if (key == Keys.Enter)
                {
                    _inputBuffer = _inputBuffer[.._textAreaCursorPos] + "\n" + _inputBuffer[_textAreaCursorPos..];
                    _textAreaCursorPos++;
                    _cursorBlink = 0;
                }
                else if (key == Keys.Left && _textAreaCursorPos > 0)
                {
                    _textAreaCursorPos--;
                    _cursorBlink = 0;
                }
                else if (key == Keys.Right && _textAreaCursorPos < _inputBuffer.Length)
                {
                    _textAreaCursorPos++;
                    _cursorBlink = 0;
                }
                else if (key == Keys.Home)
                {
                    _textAreaCursorPos = 0;
                    _cursorBlink = 0;
                }
                else if (key == Keys.End)
                {
                    _textAreaCursorPos = _inputBuffer.Length;
                    _cursorBlink = 0;
                }
                else if (key == Keys.Tab)
                {
                    // Tab closes text area
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
                        _inputBuffer = _inputBuffer[.._textAreaCursorPos] + c.Value + _inputBuffer[_textAreaCursorPos..];
                        _textAreaCursorPos++;
                        _cursorBlink = 0;
                    }
                }
            }
            else
            {
                // Single-line text input with cursor positioning and selection
                _cursorPos = Math.Clamp(_cursorPos, 0, _inputBuffer.Length);

                if (key == Keys.Enter || key == Keys.Tab)
                {
                    _activeFieldId = null;
                    _selectAll = false;
                    _selectionStart = -1;
                    return;
                }
                else if (key == Keys.Escape)
                {
                    _activeFieldId = null;
                    _selectAll = false;
                    _selectionStart = -1;
                    return;
                }
                else if (key == Keys.Back)
                {
                    if (_selectAll || HasSelection)
                    {
                        DeleteSelection();
                        _selectAll = false;
                    }
                    else if (_cursorPos > 0)
                    {
                        _inputBuffer = _inputBuffer[..(_cursorPos - 1)] + _inputBuffer[_cursorPos..];
                        _cursorPos--;
                    }
                    _cursorBlink = 0;
                }
                else if (key == Keys.Delete)
                {
                    if (_selectAll || HasSelection)
                    {
                        DeleteSelection();
                        _selectAll = false;
                    }
                    else if (_cursorPos < _inputBuffer.Length)
                    {
                        _inputBuffer = _inputBuffer[.._cursorPos] + _inputBuffer[(_cursorPos + 1)..];
                    }
                    _cursorBlink = 0;
                }
                else if (key == Keys.Left)
                {
                    if (HasSelection && !shift)
                    {
                        _cursorPos = Math.Min(_selectionStart, _cursorPos);
                        _selectionStart = -1;
                    }
                    else
                    {
                        if (shift && _selectionStart < 0) _selectionStart = _cursorPos;
                        if (_cursorPos > 0) _cursorPos--;
                        if (!shift) _selectionStart = -1;
                    }
                    _selectAll = false;
                    _cursorBlink = 0;
                }
                else if (key == Keys.Right)
                {
                    if (HasSelection && !shift)
                    {
                        _cursorPos = Math.Max(_selectionStart, _cursorPos);
                        _selectionStart = -1;
                    }
                    else
                    {
                        if (shift && _selectionStart < 0) _selectionStart = _cursorPos;
                        if (_cursorPos < _inputBuffer.Length) _cursorPos++;
                        if (!shift) _selectionStart = -1;
                    }
                    _selectAll = false;
                    _cursorBlink = 0;
                }
                else if (key == Keys.Home)
                {
                    if (shift && _selectionStart < 0) _selectionStart = _cursorPos;
                    _cursorPos = 0;
                    if (!shift) _selectionStart = -1;
                    _selectAll = false;
                    _cursorBlink = 0;
                }
                else if (key == Keys.End)
                {
                    if (shift && _selectionStart < 0) _selectionStart = _cursorPos;
                    _cursorPos = _inputBuffer.Length;
                    if (!shift) _selectionStart = -1;
                    _selectAll = false;
                    _cursorBlink = 0;
                }
                else if (key == Keys.A && (_kb.IsKeyDown(Keys.LeftControl) || _kb.IsKeyDown(Keys.RightControl)))
                {
                    // Ctrl+A: select all
                    _selectionStart = 0;
                    _cursorPos = _inputBuffer.Length;
                    _selectAll = false;
                    _cursorBlink = 0;
                }
                else
                {
                    char? c = KeyToChar(key, shift);
                    if (c.HasValue)
                    {
                        // If select-all or has selection, replace selected text
                        if (_selectAll)
                        {
                            _inputBuffer = c.Value.ToString();
                            _cursorPos = 1;
                            _selectionStart = -1;
                            _selectAll = false;
                        }
                        else if (HasSelection)
                        {
                            DeleteSelection();
                            _inputBuffer = _inputBuffer[.._cursorPos] + c.Value + _inputBuffer[_cursorPos..];
                            _cursorPos++;
                        }
                        else
                        {
                            _inputBuffer = _inputBuffer[.._cursorPos] + c.Value + _inputBuffer[_cursorPos..];
                            _cursorPos++;
                        }
                        _cursorBlink = 0;
                    }
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
    /// Returns true if a specific field (by fieldId) is currently the active text input.
    /// </summary>
    public bool IsFieldActive(string fieldId) => _activeFieldId == fieldId;

    /// <summary>
    /// Deactivate any active text field
    /// </summary>
    public void ClearActiveField() { _activeFieldId = null; }

    /// <summary>
    /// If an active combo dropdown is open, close it and return true.
    /// Editors should call this at the top of their Escape cascade so that
    /// Escape closes the dropdown first before closing sub-editors/popups.
    /// </summary>
    public bool CloseActiveDropdown()
    {
        if (_activeFieldId != null && _activeFieldId.EndsWith("_combo"))
        {
            _comboFilterText.Remove(_activeFieldId);
            _comboHighlightIdx = -1;
            _activeFieldId = null;
            return true;
        }
        return false;
    }

    // === Confirmation Dialog ===

    /// <summary>
    /// Draw a centered confirmation dialog with dark overlay.
    /// Returns true if Confirm is clicked. Sets isOpen to false on either button.
    /// Call this AFTER all other editor drawing so it renders on top.
    /// </summary>
    public bool DrawConfirmDialog(string title, string message, ref bool isOpen)
    {
        if (!isOpen) return false;

        // Set input layer to block all lower-layer interactions
        _inputLayer = 3;

        // Dark overlay
        DrawRect(new Rectangle(0, 0, _screenW, _screenH), new Color(0, 0, 0, 150));

        // Dialog dimensions
        int dialogW = 360;
        int dialogH = 160;
        int dx = (_screenW - dialogW) / 2;
        int dy = (_screenH - dialogH) / 2;

        // Dialog background
        DrawRect(new Rectangle(dx, dy, dialogW, dialogH), PanelBg);
        DrawBorder(new Rectangle(dx, dy, dialogW, dialogH), PanelBorder, 2);

        // Title bar
        DrawRect(new Rectangle(dx, dy, dialogW, 28), PanelHeader);
        DrawRect(new Rectangle(dx, dy + 28, dialogW, 1), PanelBorder);
        DrawText(title, new Vector2(dx + 10, dy + 5), TextBright, _font);

        // Message text
        DrawText(message, new Vector2(dx + 16, dy + 44), TextColor);

        // Buttons
        int btnW = 90;
        int btnH = 28;
        int btnY = dy + dialogH - btnH - 16;
        int confirmX = dx + dialogW / 2 - btnW - 10;
        int cancelX = dx + dialogW / 2 + 10;

        // Temporarily allow button interaction at this layer
        int savedLayer = _inputLayer;
        _inputLayer = 0;

        bool confirmed = false;

        if (DrawButton("Confirm", confirmX, btnY, btnW, btnH, DangerColor))
        {
            confirmed = true;
            isOpen = false;
        }

        if (DrawButton("Cancel", cancelX, btnY, btnW, btnH))
        {
            isOpen = false;
        }

        // Restore input layer
        _inputLayer = savedLayer;

        // Also close on Escape
        if (_kb.IsKeyDown(Keys.Escape) && _prevKb.IsKeyUp(Keys.Escape))
        {
            isOpen = false;
        }

        return confirmed;
    }
}
